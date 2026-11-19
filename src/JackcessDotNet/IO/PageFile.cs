using System.IO;

namespace JackcessDotNet;

internal sealed class PageFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly JetFormat _format;
    private readonly string _path;
    private readonly bool _readOnly;
    private readonly bool _openedToRead;

    private bool _disposed;

    public JetFormat Format => _format;
    public long Length { get { ThrowIfDisposed(); return _stream.Length; } }
    public int PageCount { get { ThrowIfDisposed(); return (int)(_stream.Length / _format.PageSize); } }

    /// <summary>
    /// Whether the file is only read (<see cref="ReadOnlyRefusal"/> says why): one in a format that is only read, one
    /// opened only to be read (<see cref="DatabaseOpenOptions.ReadOnly"/>), or one protected by a codec that writes
    /// nothing (<see cref="ICodecHandler.WriteRefusal"/>).
    /// </summary>
    internal bool IsReadOnly => _readOnly || _openedToRead || Codec.WriteRefusal is not null;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw Closed(_path);
    }

    /// <summary>What anything that goes to a file throws once the database has been closed.</summary>
    internal static ObjectDisposedException Closed(string path)
        => new(nameof(Database), $"The database '{path}' has been closed.");

    /// <summary>Pages read since the file was opened: what tests measure an operation's cost in.</summary>
    internal long ReadCount { get; private set; }

    /// <summary>Pages written since the file was opened: what tests tell a change that was begun from one refused.</summary>
    internal long WriteCount { get; private set; }

    /// <summary>
    /// Pages read from the file again, since it was opened, only to keep them as they were for a change that may be
    /// taken back (<see cref="UndoScope"/>): those a change writes over without having read them. Not in
    /// <see cref="ReadCount"/>. For tests of what keeping a change for undoing costs.
    /// </summary>
    internal long UndoReadCount { get; private set; }

    /// <summary>
    /// A number that changes whenever this handle writes the file's pages: when one is written, and when a change is
    /// taken back. What a reader that keeps a page it has read - a cursor - knows a kept page is still good by, when
    /// no one else writes to the file (<see cref="SharedWithWriters"/>).
    /// </summary>
    internal long ChangeStamp { get; private set; }

    /// <summary>
    /// Whether another program, or another <see cref="Database"/>, may write to the file while this one has it open:
    /// a file held only to be read shares writing (the constructor). Its pages can change between two readings though
    /// nothing is written here, so <see cref="ChangeStamp"/> says nothing of them, and a reader keeps a page no longer
    /// than one move of a cursor (#62).
    /// </summary>
    internal bool SharedWithWriters => _openedToRead || _readOnly;

    /// <summary>
    /// For tests of a change that fails while it is being written: when set, that many more pages are written
    /// and the write after them throws <see cref="IOException"/>, as a full disk would. Null (always, outside a
    /// test) for no such failure.
    /// </summary>
    internal int? WritesBeforeFailure { get; set; }

    // Pages read through ReadKeptPage, as last read or written here.
    private readonly Dictionary<int, byte[]> _kept = new();

    /// <summary>
    /// LVAL pages whose space a deleted long value freed in this session (LvalFree), by column, for that column's later
    /// values to use again (LvalWriter). A hint: a page is read, and checked against the writing column's usage map,
    /// before any row goes onto it.
    /// </summary>
    internal FreedLvalPages FreedLvalPages { get; } = new FreedLvalPages();

    /// <summary>
    /// The indexes looked at in this session (<see cref="VerifyIndexes"/>), by the table's definition page and the
    /// index's block in it, each with the rows of its table it does not hold - none, for an index that holds
    /// every row (<see cref="TableIndexes"/>): an index is looked at once.
    /// </summary>
    internal Dictionary<(int TdefPage, int Index), RowsNotHeld> IndexesChecked { get; } = new();

    /// <summary>See <see cref="Database.VerifyIndexes"/>.</summary>
    internal bool VerifyIndexes { get; set; }

    // How many times each table's definition has been changed in this session, by its definition page.
    private readonly Dictionary<int, int> _definitionVersions = new();

    /// <summary>
    /// How many times the definition on <paramref name="tdefPage"/> has been changed while the file has been open:
    /// a column or an index added to the table (<see cref="Table.AddColumn"/>, <see cref="Table.AddIndex"/>,
    /// <see cref="Database.CreateRelationship"/>). A <see cref="Table"/> reads its definition again when the number
    /// is no longer the one it read at, so every <see cref="Table"/> open on a table writes rows by the table's
    /// columns and keeps the table's indexes as they now are, whichever of them made the change.
    /// </summary>
    internal int DefinitionVersion(int tdefPage) => _definitionVersions.TryGetValue(tdefPage, out int version) ? version : 0;

    /// <summary>Records that the definition on <paramref name="tdefPage"/> has been changed and written.</summary>
    internal void DefinitionChanged(int tdefPage) => _definitionVersions[tdefPage] = DefinitionVersion(tdefPage) + 1;

    /// <summary>See <see cref="Database.ShareLongValuePages"/>.</summary>
    internal bool ShareLongValuePages { get; set; }

    /// <summary>
    /// Per-page codec applied on every Read/Write. Default is the identity
    /// (<see cref="NullCodecHandler.Instance"/>); <see cref="Database.Open(string, DatabaseOpenOptions)"/>
    /// installs another for an encoded .mdb or an encrypted .accdb.
    /// </summary>
    public ICodecHandler Codec { get; set; } = NullCodecHandler.Instance;

    public PageFile(string path, JetFormat format, FileMode mode)
        : this(path, format, mode, FileAccess.ReadWrite) { }

    /// <summary>
    /// Opens the underlying file. <paramref name="access"/> is <see cref="FileAccess.Read"/> for a file that is only
    /// to be read (<see cref="DatabaseOpenOptions.ReadOnly"/>): nothing is written to it, and every change is refused
    /// before anything happens (<see cref="ReadOnlyRefusal"/>).
    /// <para>
    /// The file is shared as Access shares a database (#62). Opened to be written, it is shared for reading only:
    /// while it is open no other program or <see cref="Database"/> opens it for writing - ACE and DAO, which ask to
    /// write in every mode but a read-only one, are refused ("file already in use") - and one that has it open for
    /// writing already keeps it from being opened here. This library takes none of Access's locks, so a second writer
    /// would write over pages this one keeps track of, and this one over its. Opened only to be read, it is shared
    /// for reading and writing, as Access shares a database it opens read-only: a writer, Access or a
    /// <see cref="Database"/>, can have it open meanwhile, and only one holding it exclusively keeps it out. Made anew
    /// (<see cref="FileMode.Create"/>, <see cref="FileMode.Truncate"/>), it is shared with no one: it is replaced only
    /// when no one has it open, and no one opens it until it is closed.
    /// </para>
    /// </summary>
    /// <exception cref="DatabaseInUseException">
    /// Another program, or another <see cref="Database"/>, has the file open in a way that does not let this one in:
    /// open for writing, or not shared for writing, when this one would write; not shared for reading - Access
    /// holding it exclusively - when this one would only read. Nothing is created or changed.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="format"/> is one this library only reads (<see cref="JetFormat.IsReadOnly"/>: Access 97's)
    /// and <paramref name="mode"/> would create or empty the file. Nothing is created or changed.
    /// </exception>
    public PageFile(string path, JetFormat format, FileMode mode, FileAccess access)
    {
        _path   = path   ?? throw new ArgumentNullException(nameof(path));
        _format = format ?? throw new ArgumentNullException(nameof(format));

        if (format.IsReadOnly && mode != FileMode.Open) throw ReadOnlyFormat($"'{path}' was not created.");

        // Whether the file is written to is decided by what it is - its own header - and not only by the format
        // the caller gives: an Access 97 file opened as another format is an Access 97 file still. A file in a
        // format that is only read is never held by a handle that could write to it, so whatever is asked of it
        // later, not a byte of it changes. (A mode that creates the file anew replaces whatever was there.)
        // Emptying the file that is there, or adding to its end, is a change to it, and is refused as any other
        // is - before the handle is opened, since opening one to truncate is the truncation.
        if (mode is FileMode.Truncate or FileMode.Append && File.Exists(path) && FormatOf(path) is { IsReadOnly: true })
            throw ReadOnlyFormat($"'{path}' was not changed.");

        _openedToRead = access == FileAccess.Read;
        bool opensWhatIsThere = mode is FileMode.Open or FileMode.OpenOrCreate;
        _readOnly = format.IsReadOnly || (opensWhatIsThere && FormatOf(path) is { IsReadOnly: true });
        if (_readOnly)
        {
            mode   = FileMode.Open;
            access = FileAccess.Read;
        }
        _stream = OpenShared(path, mode, access);
    }

    /// <summary>
    /// The file's handle, shared as the constructor says: for reading only by a writer, for reading and writing by a
    /// reader, and with no one by a file made anew. A sharing violation is the file being in use, and is thrown as
    /// <see cref="DatabaseInUseException"/>.
    /// </summary>
    private static FileStream OpenShared(string path, FileMode mode, FileAccess access)
    {
        // A mode that empties a file that is there replaces it whole, which a reader sharing it for writing would not
        // keep out: such a handle shares nothing, so it is refused while anyone has the file open, and no one opens the
        // file while it is held - the new database's whole life, as DAO holds a database it creates. Windows checks a
        // handle's sharing before it creates or empties the file, in the one call, so a file in use is refused
        // unchanged, and there is no moment between the check and the emptying for anyone to open the file in (a
        // check made with a handle of its own, closed before the file was emptied, left one).
        FileShare share = mode is FileMode.Create or FileMode.Truncate ? FileShare.None
                        : access == FileAccess.Read ? FileShare.ReadWrite
                        : FileShare.Read;
        try
        {
            // No buffer of the stream's own (a size of 1 is none): every page is read and written whole, and a page
            // read again is read from the file. With the default buffer of 4,096 bytes, reading one of an Access 97
            // file's pages of 2,048 read the next into it too, and on .NET that page, read again, came from there -
            // as it was, whatever another program had written to it since (#62).
            return new FileStream(path, mode, access, share, bufferSize: 1);
        }
        catch (IOException e) when (DatabaseInUseException.IsSharingViolation(e))
        {
            throw InUse(path, mode, access, e);
        }
    }

    /// <summary>
    /// What refuses a file that another program, or another <see cref="Database"/>, has open in a way that does not
    /// let an open with <paramref name="mode"/> and <paramref name="access"/> in: <paramref name="e"/>, the sharing
    /// violation Windows gave, said in terms of what was not done.
    /// </summary>
    internal static DatabaseInUseException InUse(string path, FileMode mode, FileAccess access, IOException e)
        => new(access == FileAccess.Read
                ? $"'{path}' is in use: another program has it open without sharing it - Access, with the database " +
                  "open exclusively. It was not opened."
                : mode is FileMode.Open or FileMode.OpenOrCreate
                    ? $"'{path}' is in use: another program, or another Database, has it open for writing - Access, while " +
                      "the database is open in it - or without sharing it. It was not opened for writing, as two writers " +
                      "would write over each other's pages; open it with DatabaseOpenOptions.ReadOnly to read it meanwhile."
                    : $"'{path}' is in use: another program, or another Database, has it open. It was not replaced, and is " +
                      "as it was.",
            e);

    /// <summary>
    /// The format of the file at <paramref name="path"/>, as its header says: the signature at offset 4 and the
    /// version byte at 0x14, which no codec hides. Null when there is no file there, or it is too short or has
    /// no such signature. This is how Jackcess takes a file's format (<c>JetFormat.getFormat</c>): from the file.
    /// </summary>
    internal static JetFormat? FormatOf(string path)
    {
        const int signatureAt = 0x04, versionAt = 0x14;
        if (!File.Exists(path)) return null;

        var head = new byte[versionAt + 1];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            int read = 0;
            while (read < head.Length)
            {
                int n = stream.Read(head, read, head.Length - read);
                if (n == 0) return null;
                read += n;
            }
        }

        string signature = System.Text.Encoding.ASCII.GetString(head, signatureAt, 15);
        if (signature != "Standard Jet DB" && signature != "Standard ACE DB") return null;
        return JetFormat.ForVersionCode(head[versionAt]);
    }

    /// <summary>
    /// Refuses a change to a file this library only reads: one opened in a format that is only read
    /// (<see cref="JetFormat.IsReadOnly"/>), or whose own header says it is in one, one opened only to be read
    /// (<see cref="DatabaseOpenOptions.ReadOnly"/>), or one protected by a codec that writes nothing (ECMA Standard
    /// Encryption). Every operation that writes calls it before it does anything else, so a refused one leaves the
    /// file, and what the caller gave it, as they were.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The file is an Access 97 one, was opened read-only, or is an .accdb in ECMA Standard Encryption.
    /// </exception>
    internal void EnsureWritable()
    {
        if (ReadOnlyRefusal is { } why) throw new NotSupportedException(why);
    }

    /// <summary>
    /// Why nothing in the file is changed, or null when it can be written to: what <see cref="EnsureWritable"/>
    /// throws, asked for instead.
    /// </summary>
    internal string? ReadOnlyRefusal
        => _readOnly ? ReadOnlyFormat($"'{_path}' was not changed.").Message
         : _openedToRead ? $"'{_path}' was opened read-only (DatabaseOpenOptions.ReadOnly), and nothing is written to it: it was not changed."
         : Codec.WriteRefusal is { } why ? $"{why} '{_path}' was not changed."
         : null;

    /// <summary>What refuses a write to, or the creation of, a file in a format that is only read.</summary>
    internal static NotSupportedException ReadOnlyFormat(string whatWasNotDone)
        => new("Access 97 (Jet 3) files are read-only in this library, as they are in Jackcess: " + whatWasNotDone);

    public byte[] ReadPage(int pageNumber)
    {
        ThrowIfDisposed();
        if (pageNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        long offset = (long)pageNumber * _format.PageSize;
        if (offset + _format.PageSize > _stream.Length)
            throw new EndOfStreamException($"Page {pageNumber} is beyond end of file.");

        var buffer = new byte[_format.PageSize];
        ReadCount++;
        _stream.Seek(offset, SeekOrigin.Begin);
        int read = 0;
        while (read < buffer.Length)
        {
            int n = _stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        if (read != buffer.Length)
            throw new EndOfStreamException($"Failed to read full page {pageNumber}.");

        _undo?.Saw(pageNumber, offset, buffer);
        return Codec.DecodePage(buffer, pageNumber);
    }

    /// <summary>
    /// As <see cref="ReadPage"/>, for a page every page appended consults (the header,
    /// the global usage map): read from the file once, then kept as written here. The
    /// copy returned is the caller's to change. Only a file opened to be written is
    /// written to, and nothing else writes to it while it is open (see the constructor),
    /// so what is kept stays what the file holds.
    /// </summary>
    internal byte[] ReadKeptPage(int pageNumber)
    {
        ThrowIfDisposed();
        if (!_kept.TryGetValue(pageNumber, out var page))
        {
            page = ReadPage(pageNumber);
            _kept[pageNumber] = page;
            Journal(() => _kept.Remove(pageNumber));
        }
        return (byte[])page.Clone();
    }

    public void WritePage(int pageNumber, byte[] data)
    {
        ThrowIfDisposed();
        if (pageNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (data.Length != _format.PageSize)
            throw new ArgumentException(
                $"Page data must be exactly {_format.PageSize} bytes for {Format.Version}.");
        EnsureWritable();
        if (WritesBeforeFailure is { } left)
        {
            if (left <= 0) throw new IOException($"Page {pageNumber} was not written: a test asked for the write to fail.");
            WritesBeforeFailure = left - 1;
        }

        byte[] encoded = Codec.EncodePage(data, pageNumber);
        long offset = (long)pageNumber * _format.PageSize;
        long requiredLength = offset + _format.PageSize;
        _undo?.Keep(pageNumber, offset);
        WriteCount++;
        ChangeStamp++;
        if (_stream.Length < requiredLength)
            _stream.SetLength(requiredLength);

        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.Write(encoded, 0, encoded.Length);
        _stream.Flush();
        if (_kept.TryGetValue(pageNumber, out var was))
        {
            _kept[pageNumber] = (byte[])data.Clone();
            Journal(() => _kept[pageNumber] = was);
        }
    }

    // ── Undo ──────────────────────────────────────────────────────────────────

    private UndoScope? _undo;

    /// <summary>Whether a change is being kept for undoing (<see cref="BeginUndo"/>): one that begins inside it is part of it.</summary>
    internal bool IsUndoing => _undo is not null;

    // Buffers of a page's size, for the images of pages an undo scope keeps: used again from one scope to the next,
    // so that a change of a row - each its own scope - allocates none.
    private readonly Stack<byte[]> _spareImages = new();
    private const int SpareImagesKept = 64;

    private byte[] RentImage() => _spareImages.Count > 0 ? _spareImages.Pop() : new byte[_format.PageSize];

    private void ReturnImage(byte[] image)
    {
        if (image.Length == _format.PageSize && _spareImages.Count < SpareImagesKept) _spareImages.Push(image);
    }

    // What FreedLvalPages notes through while a scope is open: one delegate, not one a scope.
    private Action<Action>? _journalNote;

    /// <summary>
    /// Starts keeping every page as it was before it is next written, so that a change of many rows that fails
    /// part-way can be taken back: dispose the scope without <see cref="UndoScope.Commit"/> and the file is as it
    /// was when the scope began, pages added since removed. It is kept in memory and guards against a failure this
    /// process survives - an exception - not against the process ending part-way. One scope is open at a time: a
    /// scope inside another could not be taken back on its own, so beginning one is refused.
    /// </summary>
    internal UndoScope BeginUndo()
    {
        EnsureWritable();
        if (_undo is not null)
            throw new InvalidOperationException("A change to this file is already being kept for undoing; another cannot begin inside it.");
        _undo = new UndoScope(this);
        FreedLvalPages.Journal = _journalNote ??= Journal;
        return _undo;
    }

    /// <summary>
    /// While a change is being kept for undoing, notes <paramref name="undo"/>, which takes back a change made to
    /// what this file keeps in memory of its pages (<see cref="FreedLvalPages"/>, <see cref="IndexesChecked"/>, the
    /// pages <see cref="ReadKeptPage"/> keeps): a change taken back runs them, the last first, so that what was
    /// known before it began is known again. Nothing is noted otherwise.
    /// </summary>
    internal void Journal(Action undo) => _undo?.Remember(undo);

    /// <summary>
    /// Notes, in <see cref="IndexesChecked"/>, the rows an index that was looked at does not hold. A look taken
    /// before the change being kept for undoing has written a page is of the file as it was when the change began,
    /// which is what the file is again if the change is taken back: it is kept then, so that a change refused
    /// before it writes - a duplicate - does not cost the next change a reading of the whole table.
    /// </summary>
    internal void NoteIndexChecked((int TdefPage, int Index) index, RowsNotHeld rows)
    {
        if (_undo is { HasWritten: true })
        {
            bool had = IndexesChecked.TryGetValue(index, out var was);
            Journal(() =>
            {
                if (had) IndexesChecked[index] = was!;
                else IndexesChecked.Remove(index);
            });
        }
        IndexesChecked[index] = rows;
    }

    /// <summary>
    /// As <see cref="BeginUndo"/>, for a change that is made on its own or as part of a larger one: null, with
    /// nothing begun, when a scope is open already, which then takes the change back with the rest of its own.
    /// </summary>
    internal UndoScope? BeginUndoUnlessOpen() => _undo is null ? BeginUndo() : null;

    /// <summary>
    /// Runs <paramref name="action"/> once what has been written is in the file for good: now, when no change is
    /// being kept for undoing, and otherwise when that change is committed - never when it is taken back. What
    /// tells the cursors over a table's indexes of the rows a change has moved.
    /// </summary>
    internal void WhenKept(Action action)
    {
        if (_undo is null) action();
        else _undo.AfterCommit(action);
    }

    /// <summary>
    /// Whether what <see cref="WhenKept"/> runs now is one of several things a change committed together runs: the
    /// rows of a batch, or of a cascade. Each runs once the whole change is in the file, so what one finds there is
    /// what the others left too.
    /// </summary>
    internal bool KeptTogether { get; private set; }

    /// <summary>
    /// The pages a change has written over, as they were, and what it changed of what the file keeps in memory,
    /// until it is committed or disposed.
    /// <para>
    /// A page's image is taken when the scope first writes it, and costs no read of the file when the scope has
    /// read the page already - as a change nearly always has, a page being changed where it is read - or when the
    /// file keeps the page (<see cref="ReadKeptPage"/>): the bytes read are copied as they are read, a window of
    /// the latest pages (<see cref="Saw"/>). A page added since the scope began needs none: taking the change back
    /// cuts the file to the length it had. The buffers go back to the file when the scope ends, for the next.
    /// </para>
    /// </summary>
    internal sealed class UndoScope : IDisposable
    {
        // How many pages read and not yet written a scope keeps the bytes of: enough for any one row's change.
        private const int ReadImagesKept = 64;

        private readonly PageFile _file;
        private readonly Dictionary<int, byte[]> _before = new();
        private readonly Dictionary<int, byte[]> _read = new();
        private readonly long _length;
        private bool _done;
        private bool _wrote;
        private List<Action>? _afterCommit;
        private List<Action>? _journal;

        internal UndoScope(PageFile file)
        {
            _file   = file;
            _length = file._stream.Length;
        }

        /// <summary>Whether the change has written a page yet.</summary>
        internal bool HasWritten => _wrote;

        /// <summary>Notes what to run once the change is committed (<see cref="WhenKept"/>).</summary>
        internal void AfterCommit(Action action) => (_afterCommit ??= new List<Action>()).Add(action);

        /// <summary>Notes what takes back a change to what the file keeps in memory (<see cref="Journal"/>).</summary>
        internal void Remember(Action undo) => (_journal ??= new List<Action>()).Add(undo);

        /// <summary>Keeps the change: nothing is taken back when the scope is disposed.</summary>
        public void Commit()
        {
            End();
            if (_afterCommit is { } actions)
            {
                _afterCommit = null;
                _file.KeptTogether = actions.Count > 1;
                try
                {
                    foreach (var action in actions) action();
                }
                finally
                {
                    _file.KeptTogether = false;
                }
            }
        }

        private void End()
        {
            _done = true;
            _file._undo = null;
            _file.FreedLvalPages.Journal = null;
            Release(_read);
        }

        private void Release(Dictionary<int, byte[]> images)
        {
            foreach (var image in images.Values) _file.ReturnImage(image);
            images.Clear();
        }

        /// <summary>
        /// The bytes of a page just read (<paramref name="raw"/>, as the file holds them), kept for the page's first
        /// write in the scope when the file held them when the scope began: the page was not written since, and is not
        /// one added since.
        /// </summary>
        internal void Saw(int pageNumber, long offset, byte[] raw)
        {
            if (offset >= _length || _before.ContainsKey(pageNumber) || _read.ContainsKey(pageNumber)) return;
            // A window, not every page a large change reads: one of these not written yet is read again if it is.
            if (_read.Count >= ReadImagesKept) Release(_read);
            var image = _file.RentImage();
            Buffer.BlockCopy(raw, 0, image, 0, image.Length);
            _read[pageNumber] = image;
        }

        // The page's bytes as the file holds them (before any codec), the first time it is written in this scope.
        internal void Keep(int pageNumber, long offset)
        {
            _wrote = true;
            if (offset >= _length || _before.ContainsKey(pageNumber)) return;
            if (_read.TryGetValue(pageNumber, out var image))
            {
                _read.Remove(pageNumber);
            }
            else if (_file._kept.TryGetValue(pageNumber, out var kept))
            {
                // As written here or read, before any codec: encoded again, a copy, so that the page kept is untouched.
                image = _file.RentImage();
                Buffer.BlockCopy(kept, 0, image, 0, image.Length);
                image = _file.Codec.EncodePage(image, pageNumber);
            }
            else
            {
                image = _file.RentImage();
                _file.UndoReadCount++;
                _file._stream.Seek(offset, SeekOrigin.Begin);
                int read = 0;
                while (read < image.Length)
                {
                    int n = _file._stream.Read(image, read, image.Length - read);
                    if (n == 0) break;
                    read += n;
                }
            }
            _before[pageNumber] = image;
        }

        /// <summary>
        /// Takes back everything written since the scope began, unless it was committed: the pages, and what the
        /// file keeps in memory of them, which is then what it was when the scope began. What was noted is taken
        /// back whether or not a page was written: a change notes some things before its first write - a page it is
        /// about to put a long value on is no longer one with room (#61) - and when that write fails they are not
        /// true of the file. A scope that wrote nothing and noted nothing changes nothing.
        /// </summary>
        public void Dispose()
        {
            if (!_done)
            {
                End();
                _afterCommit = null;
                TakeBack();
            }
            Release(_before);
        }

        private void TakeBack()
        {
            if (_wrote)
            {
                foreach (var (pageNumber, raw) in _before)
                {
                    _file._stream.Seek((long)pageNumber * _file._format.PageSize, SeekOrigin.Begin);
                    _file._stream.Write(raw, 0, raw.Length);
                }
                _file._stream.SetLength(_length);
                _file._stream.Flush();
                // What was read of the pages since is no longer what the file holds.
                _file.ChangeStamp++;
            }
            // What was noted of them is taken back, the last first.
            if (_journal is { } journal)
                for (int i = journal.Count - 1; i >= 0; i--) journal[i]();
        }
    }

    /// <summary>
    /// Makes the file <paramref name="source"/>'s pages: what a new database is written from
    /// (<see cref="DatabaseHeader.WriteTo"/>). Each page is written as any other is (<see cref="WritePage"/>), so a
    /// failure part-way is one a test can ask for (<see cref="WritesBeforeFailure"/>).
    /// </summary>
    /// <exception cref="InvalidDataException"><paramref name="source"/> does not end at the end of a page.</exception>
    public void ReplaceWith(Stream source)
    {
        ThrowIfDisposed();
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        EnsureWritable();

        ChangeStamp++;
        _kept.Clear();
        IndexesChecked.Clear();
        _stream.SetLength(0);
        var page = new byte[_format.PageSize];
        for (int pageNumber = 0; ; pageNumber++)
        {
            int read = 0;
            for (int n; read < page.Length && (n = source.Read(page, read, page.Length - read)) > 0; )
                read += n;
            if (read == 0) return;
            if (read < page.Length)
                throw new InvalidDataException(
                    $"A database is whole pages of {page.Length} bytes; what was given ends {read} bytes into page {pageNumber}.");
            WritePage(pageNumber, page);
        }
    }

    public string Path => _path;

    public void Dispose()
    {
        _disposed = true;
        _stream.Dispose();
    }
}
