using System.IO;

namespace JackcessDotNet;

public sealed class PageFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly JetFormat _format;
    private readonly string _path;
    private readonly bool _readOnly;

    public JetFormat Format => _format;
    public long Length => _stream.Length;
    public int PageCount => (int)(_stream.Length / _format.PageSize);

    /// <summary>Pages read since the file was opened: what tests measure an operation's cost in.</summary>
    internal long ReadCount { get; private set; }

    /// <summary>Pages written since the file was opened: what tests tell a change that was begun from one refused.</summary>
    internal long WriteCount { get; private set; }

    // Pages read through ReadKeptPage, as last read or written here.
    private readonly Dictionary<int, byte[]> _kept = new();

    /// <summary>
    /// LVAL pages whose space a deleted long value freed in this session (LvalFree), by column, for that column's later
    /// values to use again (LvalWriter). A hint: a page is read, and checked against the writing column's usage map,
    /// before any row goes onto it.
    /// </summary>
    internal FreedLvalPages FreedLvalPages { get; } = new FreedLvalPages();

    /// <summary>
    /// The indexes seen in this session to hold, or not to hold, every row of their table, by the table's
    /// definition page and the index's block in it (<see cref="TableIndexes"/>): an index is looked at once.
    /// </summary>
    internal Dictionary<(int TdefPage, int Index), bool> IndexesChecked { get; } = new();

    /// <summary>See <see cref="Database.ShareLongValuePages"/>.</summary>
    internal bool ShareLongValuePages { get; set; }

    /// <summary>
    /// Per-page codec applied on every Read/Write. Default is the identity
    /// (<see cref="NullCodecHandler.Instance"/>); <see cref="Database.Open"/> may
    /// install a different one for encrypted files (none implemented today).
    /// </summary>
    public ICodecHandler Codec { get; set; } = NullCodecHandler.Instance;

    public PageFile(string path, JetFormat format, FileMode mode)
        : this(path, format, mode, FileAccess.ReadWrite) { }

    /// <summary>
    /// Opens the underlying file. <paramref name="access"/> should be
    /// <see cref="FileAccess.Read"/> when the caller will not mutate pages
    /// (read-only access avoids contending with other readers for write locks
    /// on corpus files mounted from read-only filesystems or git checkouts).
    /// </summary>
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

        bool opensWhatIsThere = mode is FileMode.Open or FileMode.OpenOrCreate;
        _readOnly = format.IsReadOnly || (opensWhatIsThere && FormatOf(path) is { IsReadOnly: true });
        if (_readOnly)
        {
            mode   = FileMode.Open;
            access = FileAccess.Read;
        }
        _stream = new FileStream(path, mode, access, FileShare.ReadWrite);
    }

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
        return head[versionAt] switch
        {
            0x00 => JetFormat.Jet3,
            0x01 => JetFormat.Jet4,
            0x02 => JetFormat.Jet12,
            0x03 => JetFormat.Jet14,
            0x04 => JetFormat.Jet16,
            // Later generations keep the layout of ACE 17.
            _    => JetFormat.Jet17,
        };
    }

    /// <summary>
    /// Refuses a change to a file this library only reads: one opened in a format that is only read
    /// (<see cref="JetFormat.IsReadOnly"/>), or whose own header says it is in one. Every operation that writes
    /// calls it before it does anything else, so a refused one leaves the file, and what the caller gave it, as
    /// they were.
    /// </summary>
    /// <exception cref="NotSupportedException">The file is an Access 97 one.</exception>
    internal void EnsureWritable()
    {
        if (ReadOnlyRefusal is { } why) throw new NotSupportedException(why);
    }

    /// <summary>
    /// Why nothing in the file is changed, or null when it can be written to: what <see cref="EnsureWritable"/>
    /// throws, asked for instead.
    /// </summary>
    internal string? ReadOnlyRefusal
        => _readOnly ? ReadOnlyFormat($"'{_path}' was not changed.").Message : null;

    /// <summary>What refuses a write to, or the creation of, a file in a format that is only read.</summary>
    internal static NotSupportedException ReadOnlyFormat(string whatWasNotDone)
        => new("Access 97 (Jet 3) files are read-only in this library, as they are in Jackcess: " + whatWasNotDone);

    public byte[] ReadPage(int pageNumber)
    {
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

        return Codec.DecodePage(buffer, pageNumber);
    }

    /// <summary>
    /// As <see cref="ReadPage"/>, for a page every page appended consults (the header,
    /// the global usage map): read from the file once, then kept as written here. The
    /// copy returned is the caller's to change. A file has one writer: like the rest of
    /// this library, it does not see what another handle on the file writes - another
    /// Database open on it, in this process or another - while it is open.
    /// </summary>
    internal byte[] ReadKeptPage(int pageNumber)
    {
        if (!_kept.TryGetValue(pageNumber, out var page))
        {
            page = ReadPage(pageNumber);
            _kept[pageNumber] = page;
        }
        return (byte[])page.Clone();
    }

    public void WritePage(int pageNumber, byte[] data)
    {
        if (pageNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (data.Length != _format.PageSize)
            throw new ArgumentException(
                $"Page data must be exactly {_format.PageSize} bytes for {Format.Version}.");
        EnsureWritable();

        byte[] encoded = Codec.EncodePage(data, pageNumber);
        long offset = (long)pageNumber * _format.PageSize;
        long requiredLength = offset + _format.PageSize;
        _undo?.Keep(pageNumber, offset);
        WriteCount++;
        if (_stream.Length < requiredLength)
            _stream.SetLength(requiredLength);

        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.Write(encoded, 0, encoded.Length);
        _stream.Flush();
        if (_kept.ContainsKey(pageNumber))
            _kept[pageNumber] = (byte[])data.Clone();
    }

    // ── Undo ──────────────────────────────────────────────────────────────────

    private UndoScope? _undo;

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
        return _undo = new UndoScope(this);
    }

    /// <summary>The pages a change has written over, as they were, until it is committed or disposed.</summary>
    internal sealed class UndoScope : IDisposable
    {
        private readonly PageFile _file;
        private readonly Dictionary<int, byte[]> _before = new();
        private readonly long _length;
        private bool _done;

        internal UndoScope(PageFile file)
        {
            _file   = file;
            _length = file._stream.Length;
        }

        /// <summary>Keeps the change: nothing is taken back when the scope is disposed.</summary>
        public void Commit()
        {
            _done = true;
            _file._undo = null;
        }

        // The page's bytes as the file holds them (before any codec), the first time it is written in this scope.
        internal void Keep(int pageNumber, long offset)
        {
            if (offset >= _length || _before.ContainsKey(pageNumber)) return;
            var raw = new byte[_file._format.PageSize];
            _file._stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < raw.Length)
            {
                int n = _file._stream.Read(raw, read, raw.Length - read);
                if (n == 0) break;
                read += n;
            }
            _before[pageNumber] = raw;
        }

        /// <summary>Takes back everything written since the scope began, unless it was committed.</summary>
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _file._undo = null;
            foreach (var (pageNumber, raw) in _before)
            {
                _file._stream.Seek((long)pageNumber * _file._format.PageSize, SeekOrigin.Begin);
                _file._stream.Write(raw, 0, raw.Length);
            }
            _file._stream.SetLength(_length);
            _file._stream.Flush();
            // What was read or noted of the pages since is no longer what the file holds.
            _file._kept.Clear();
            _file.FreedLvalPages.Clear();
        }
    }

    public void ReplaceWith(Stream source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        EnsureWritable();

        _kept.Clear();
        IndexesChecked.Clear();
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.SetLength(0);
        source.CopyTo(_stream);
        _stream.Flush();
    }

    public string Path => _path;

    public void Dispose()
        => _stream.Dispose();
}
