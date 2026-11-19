using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Authentication;

namespace JackcessDotNet;

/// <summary>
/// Entry point for creating and opening Microsoft Access (.mdb / .accdb) database files.
///
/// Usage:
/// <code>
///   using var db = Database.Create("MyFile.mdb");
///   var table = db.CreateTable("Customers", new[]
///   {
///       new ColumnDefinition("Id",   DataType.Int32) { IsAutoNumber = true },
///       new ColumnDefinition("Name", DataType.Text)  { MaxLength = 100 },
///   }, "Id");
///   var row = new Row { ["Name"] = "Alice" };
///   table.Insert(row);          // row["Id"] is now 1
/// </code>
/// <para>
/// A <see cref="Database"/>, and everything got from it - its tables, their cursors and rows - is for one thread
/// at a time. A file has one writer: a database does not see what another handle on the file writes while it
/// is open.
/// </para>
/// <para>
/// <see cref="Dispose"/> closes the file. After it, <see cref="Path"/>, <see cref="FileFormat"/> and
/// <see cref="IsReadOnly"/> still answer, and every other member - and every table and cursor got from the
/// database, when it goes to the file - throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </summary>
public sealed class Database : IDisposable
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;

    /// <summary>The open file: for an import, which writes inside an undo scope of its own, and for tests that look at its pages.</summary>
    internal PageFile File => _file;
    private readonly SystemCatalog _catalog;
    private ForeignKeyEnforcer? _foreignKeys;

    // The tables handed out, by the page of their definition: a table is one object while the database is open,
    // so what is set on it (Table.AllowAutoNumberInsert) is set for everyone who asks for the table.
    private readonly Dictionary<int, Table> _tables = new();

    private bool _enforceForeignKeys = true;
    private bool _allowAutoNumberInsert;
    private bool _disposed;

    private Database(PageFile file)
    {
        _file      = file;
        _allocator = new PageAllocator(file);
        _catalog   = new SystemCatalog(file);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw PageFile.Closed(_file.Path);
    }

    /// <summary>The file's path, as it was given to <see cref="Create(string)"/> or <see cref="Open(string)"/>.</summary>
    public string Path => _file.Path;

    /// <summary>
    /// The format the file is in, as its own header says. A file made as <see cref="FileFormat.Access2007"/> or
    /// <see cref="FileFormat.Access2010"/> becomes <see cref="FileFormat.Access2016"/> or
    /// <see cref="FileFormat.Access2019"/> when a table of it is given a Large Number or a Date/Time Extended
    /// column; this is the format the file had when it was opened or created.
    /// </summary>
    public FileFormat FileFormat => _file.Format.Version;

    /// <summary>
    /// Whether the file is only read: an Access 97 file (<see cref="FileFormat.Access97"/>). Every change to such
    /// a database is refused with <see cref="NotSupportedException"/> before anything is written.
    /// </summary>
    public bool IsReadOnly => _file.IsReadOnly;

    /// <summary>
    /// Whether the relationships in <c>MSysRelationships</c> that have referential integrity are kept, as
    /// Access keeps them, by <see cref="Table.Insert"/>, <see cref="Table.Update"/> and
    /// <see cref="Table.Delete"/>. Default <c>true</c>, as in Jackcess; Access itself always keeps them. When
    /// <c>false</c> nothing is checked and nothing cascades, and a file can be left with rows Access takes to
    /// be impossible: a row that refers to no parent, or a parent deleted from under its children.
    /// <para>
    /// A row that would refer to no parent row is refused: on insert, and when an update changes its foreign key.
    /// A foreign key whose columns are all null refers to nothing and is allowed. Changing a parent's key, or
    /// deleting the parent, does what the relationship says: with cascade update the referring rows take the new
    /// key; with cascade delete they are deleted, and the rows referring to them in turn; with cascade-to-null
    /// their foreign key is set to null; with none of these the change is refused while any row refers to the
    /// parent.
    /// </para>
    /// <para>
    /// A refused change throws <see cref="ConstraintViolationException"/> and leaves the file as it was: everything is
    /// checked, cascades included, before anything is written. A change that fails while it is being written is
    /// taken back.
    /// </para>
    /// <para>
    /// Rows are found through the tables' indexes. Access gives every relationship an index on its foreign key,
    /// and the key it refers to is a primary key or a unique index: so an insert reads a few pages to find its
    /// parent, and an update or delete reads the rows it reaches and the index pages that lead to them - a cascade
    /// delete of five rows about fifty pages, whether the child table has a thousand rows or a million - and holds
    /// only those rows in memory until it is written. Every row a cascade rewrites or deletes keeps its entry in
    /// every index of its table, as any other update or delete does, so Access goes on finding it.
    /// </para>
    /// <para>
    /// An index is trusted to hold every row of its table, as Access trusts one. A file a version of this library
    /// before it kept every index wrote to (those in ExportMdb.Interop up to 2.1.2) can have rows that are in
    /// their table's primary key and in no other index - not in the one on their foreign key, so that through it
    /// they are not found as children, and their parent is deleted or re-keyed from under them, as Access itself
    /// does with such a file (qcatco/exportmdb.interop#64). For such a file set <see cref="VerifyIndexes"/> as
    /// well: the first change that looks for rows through an index, while the database is open, then reads that
    /// index and its table once and compares them row by row, and the changes after it find rows through the
    /// index, or, while it does not hold every row, by reading the table as for a table with no index. An entry
    /// whose row has gone does no harm either way: each row found is read and compared.
    /// </para>
    /// <para>
    /// Keys are matched as Access matches them, which is as its indexes hold them. A null matches a null: a parent
    /// whose key is null (a unique index allows it) has the rows whose foreign key is null for children, so
    /// deleting it deletes them or is refused by them, and re-keying it takes them with it. A parent that has a key
    /// never touches them, and a row may always be added, or changed, to have no foreign key at all. Numbers are
    /// matched as stored: a Double or Single by its bits, so 0 and -0 are two keys, and so are a Double and the
    /// next one after it; a Date/Time by the double stored, not the <see cref="DateTime"/> it reads as. A Yes/No
    /// holds no null: a null given it is stored No, and is checked as No - so a cascade-to-null of a Yes/No foreign
    /// key leaves the row No, as Access leaves it, whether or not a parent's key is No.
    /// </para>
    /// <para>
    /// Only relationships are kept. A column's Required property is not: a caller's own update may null a column
    /// Access would require a value for, as it may with enforcement off. (A cascade may not.)
    /// </para>
    /// <para>
    /// Where no index on the columns can be searched - a table made without one on its foreign key, an index
    /// whose keys this library does not write, a parent key that is null, a key this library may not make as Access
    /// does (a Date/Time Extended one, which ACE can store in a second form keyed otherwise, #51) - the table is
    /// read from end to end,
    /// once in the change, and all its rows are held until the change is written: the row's place and the columns
    /// its indexes and relationships name, some 200 bytes a row with numeric keys (about 60 MB and a second for a
    /// table of 300,000 rows). A change that would rewrite a row of a table whose rows this library does not
    /// write as Access does is refused with <see cref="NotSupportedException"/>.
    /// </para>
    /// </summary>
    public bool EnforceForeignKeys
    {
        get { ThrowIfDisposed(); return _enforceForeignKeys; }
        set
        {
            ThrowIfDisposed();
            _enforceForeignKeys = value;
            // The relationships are read again by whatever next keeps them: they are as the file now has them
            // from the moment enforcement is switched.
            _foreignKeys = null;
        }
    }

    /// <summary>
    /// Whether an index is checked against its table before it is believed. Default <c>false</c>: an index is
    /// taken at its word, as Access and Java Jackcess take one - a unique index or primary key that holds no
    /// entry for a value means no row has it, and, with <see cref="EnforceForeignKeys"/>, an index that holds no
    /// entry for a key means no row refers to it - and nothing is read but the index.
    /// <para>
    /// <b>When to turn it on:</b> for a file that a version of this library before it kept every index wrote
    /// rows into - any up to the one in ExportMdb.Interop 2.1.2 - in a table Access made with indexes, and that
    /// has not been compacted in Access since. Those versions kept a table's primary key and no other index: a
    /// row one of them inserted is in no other index, and a row one of them updated is in each where it was
    /// before. They kept the primary key under the value a row was found by, so a row whose key they changed is
    /// in the key under the value it had. Nothing in a file says that one of them wrote to it. With such a file
    /// and the default, this library does what Access does: it takes a second row with the values of a row a
    /// unique index does not hold - after which Access cannot compact the file without dropping that index, or
    /// the primary key - and, enforcing foreign keys, deletes or re-keys a parent from under a child the index
    /// on its foreign key does not hold. Compacting the file in Access rebuilds its indexes and makes the
    /// option needless.
    /// </para>
    /// <para>
    /// <b>What it does:</b> the first time a session compares a row with a table's unique indexes
    /// (<see cref="Table.Insert"/>, <see cref="Table.Update"/>, <see cref="Table.Validate"/>), or
    /// looks for rows through an index to keep a relationship, the table is read once and each index's entries
    /// are compared with its rows. The rows an index does not hold are kept while the database is open: a row is
    /// compared with them as well as with the index, so the duplicate is refused, and an index that lacks rows
    /// is not searched for a relationship - the table is read instead - until this library has rewritten or
    /// deleted them. A row whose key this library may not make as Access does - a Date/Time Extended value, which
    /// ACE can store in a second form keyed otherwise (#51) - is one the index lacks unless its entry is under the
    /// key this library makes. Set it before the first change; an index is looked at once.
    /// </para>
    /// <para>
    /// <b>What it costs:</b> one reading of the table, and of the indexes' leaf pages, for the first such change
    /// to each table, and memory for a key a row while it is read; nothing after. Measured on a table of
    /// 1,000,000 rows in 30,304 pages, the file in the operating system's cache: the first insert read 33,644
    /// pages in half a second and allocated 620 MB in passing with a primary key, and 36,965 pages in 0.8 to
    /// 0.9 seconds and 990 MB with a unique index as well, where every other insert, and the first without
    /// the option, reads 8 and 11 pages. A table with no unique index, a row with a null in its unique
    /// columns, and a table filled in the session that made it cost nothing.
    /// </para>
    /// <para>
    /// <b>Entries that are no row's:</b> those versions also left entries behind - one that points where a row
    /// was, and one under the value a row had before its primary key was changed, which points at the row under
    /// the value it has. Access follows both: it fails a query that comes to the first ("Not a valid bookmark")
    /// and answers one for the second's key with that row. With or without this option, an entry that points at
    /// no row is no row's duplicate, and goes when an entry is added or taken out under its key (#36); an entry
    /// that points at a row of the table is believed, as Access believes it, and a row with its key is refused.
    /// With the option, every entry an index was seen to hold that points at no row is taken out when the table's
    /// indexes are next changed, whatever the key; and an entry that points at a row whose key is another, where
    /// that key is one this library makes as Access does, is the row's wrong one: a row with the entry's key is
    /// taken, the entry goes, and the row is entered under its own key. Where this library's key for a row may not
    /// be Access's - a Date/Time Extended column (#51), or a key cut at 510 bytes in an index of several columns
    /// other than Text and Long ones - the entry is believed with the option too. A row with no entry at all is
    /// not given one: compacting the file in Access rebuilds its indexes.
    /// </para>
    /// </summary>
    public bool VerifyIndexes
    {
        get { ThrowIfDisposed(); return _file.VerifyIndexes; }
        set { ThrowIfDisposed(); _file.VerifyIndexes = value; }
    }

    /// <summary>
    /// How a long value (Memo or OLE) too long for one page is put on its pages. <c>false</c>, the default, is
    /// Access's way: every chunk of such a value has a page to itself, the last chunk too, and only values that
    /// fit in one chunk share pages - those the column's free-space map lists. <c>true</c> is how this library
    /// placed them before: the last chunk of a value goes on a page with other values' chunks, which makes a
    /// smaller file and one Access reads the same.
    /// <para>
    /// The difference shows when Access itself later deletes or replaces such a value. It takes every page of the
    /// value to be the value's alone and gives them all back to the file, whatever else is on them; the next thing
    /// Access adds is written over the other values there, and they, or the whole file, can no longer be read. A
    /// file written with <c>true</c> is therefore one to be read, or added to by this library, but not one for
    /// Access to change long values in. It is kept for a caller whose files must stay byte for byte as they were.
    /// </para>
    /// </summary>
    public bool ShareLongValuePages
    {
        get { ThrowIfDisposed(); return _file.ShareLongValuePages; }
        set { ThrowIfDisposed(); _file.ShareLongValuePages = value; }
    }

    /// <summary>
    /// Whether a row may bring its own value for an AutoNumber column. Default <c>false</c>: an insert numbers
    /// the row itself and ignores any value the row holds for such a column, and an update leaves the column as
    /// it was. When <c>true</c>, a value the row holds is written as given - and a Long one past the table's
    /// counter moves the counter up to it - while a row without one is still numbered. A table can decide for
    /// itself with <see cref="Table.AllowAutoNumberInsert"/>.
    /// <para>
    /// Turn it on with care: nothing checks that a value supplied this way is not already in the table.
    /// </para>
    /// </summary>
    public bool AllowAutoNumberInsert
    {
        get { ThrowIfDisposed(); return _allowAutoNumberInsert; }
        set { ThrowIfDisposed(); _allowAutoNumberInsert = value; }
    }

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new blank Access 2000-2003 database (an .mdb, <see cref="FileFormat.Access2000"/>) at
    /// <paramref name="path"/> and returns it open and ready for use. That is the format Jackcess creates when
    /// none is asked for; <see cref="Create(string, FileFormat)"/> makes another. A file already at
    /// <paramref name="path"/> is replaced.
    /// </summary>
    /// <param name="path">The path of the file to create.</param>
    /// <returns>The new database, open. The caller disposes it.</returns>
    public static Database Create(string path) => Create(path, FileFormat.Access2000);

    /// <summary>
    /// Creates a new blank database at <paramref name="path"/>, in <paramref name="format"/>, and returns it open
    /// and ready for use: an .mdb for <see cref="FileFormat.Access2000"/> (Access 2000-2003), an .accdb for
    /// <see cref="FileFormat.Access2007"/>, <see cref="FileFormat.Access2010"/>,
    /// <see cref="FileFormat.Access2016"/> and <see cref="FileFormat.Access2019"/> - the formats Jackcess creates.
    /// The file's name is the caller's: no extension is added or asked for. A file already at
    /// <paramref name="path"/> is replaced.
    /// <para>
    /// The new file is a copy of an empty database Access's own engine made. Tables
    /// made in an Access 2000-2003 or an Access 2007 file sort their text in General - Legacy, as Access's own do
    /// there; in the later formats they sort in General.
    /// </para>
    /// </summary>
    /// <param name="path">The path of the file to create.</param>
    /// <param name="format">The format of the file to create.</param>
    /// <returns>The new database, open. The caller disposes it.</returns>
    /// <exception cref="NotSupportedException">
    /// <paramref name="format"/> is <see cref="FileFormat.Access97"/>: Access 97 files are read, and neither created
    /// nor written, as in Jackcess. Nothing is created, and a file already at <paramref name="path"/> is left as
    /// it was.
    /// </exception>
    public static Database Create(string path, FileFormat format)
    {
        var jetFormat = JetFormat.For(format);
        if (jetFormat.IsReadOnly) throw PageFile.ReadOnlyFormat($"'{path}' was not created.");
        var file = new PageFile(path, jetFormat, FileMode.Create);
        var db   = new Database(file);

        DatabaseHeader.Create(jetFormat).WriteTo(file);

        return db;
    }

    /// <summary>
    /// Opens an existing .mdb / .accdb file with what <paramref name="options"/> says about it: the password of
    /// a protected file (<see cref="DatabaseOpenOptions.Password"/>), and the format to read a file as when its
    /// header does not say (<see cref="DatabaseOpenOptions.FallbackFormat"/>). With neither set this is
    /// <see cref="Open(string)"/>.
    /// <para>
    /// A file is protected as its header says, and a password is checked as Access checks it: exactly, letter
    /// case and all.
    /// </para>
    /// <list type="bullet">
    ///   <item>An Access 97 or Access 2000-2003 file (.mdb) with a database password opens with that password and
    ///   no other. The password changes no page: the file is read and written as any other.</item>
    ///   <item>An .mdb whose pages Access encoded (its Encrypt Database command) holds the key to them itself, and
    ///   opens with any password or none, as Access opens it - unless it has a database password as well. Its
    ///   pages are decoded as they are read and encoded as they are written.</item>
    ///   <item>An .accdb with a password is encrypted with it: Agile Encryption (what Access 2010 and later make,
    ///   MS-OFFCRYPTO §2.3.4.10–13), RC4 CryptoAPI (Access 2007's), or ECMA Standard Encryption (§2.3.4.5–9),
    ///   whose non-standard form Jackcess-encrypt reads is read too. Its pages are decrypted as they are read and
    ///   encrypted as they are written. One encrypted by an extensible provider is refused with
    ///   <see cref="NotSupportedException"/>.</item>
    ///   <item>A file that is not protected is opened as it is, and the password is not looked at.</item>
    /// </list>
    /// <para>
    /// An Access 97 file is opened for reading only, whatever the options say (<see cref="IsReadOnly"/>), and so is
    /// an .accdb in ECMA Standard Encryption: no Access is known to make one, and no file of it was found to check
    /// what this library would write to one against Access.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidCredentialException">
    /// The file has a database password, or is an encrypted .accdb, and the password given is not its password or
    /// none was given. Nothing of the file is read past its header.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The file is not an Access database - it is too short, or has no signature - and no
    /// <see cref="DatabaseOpenOptions.FallbackFormat"/> says what to read it as.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is damaged - its catalog does not read, and its header names no protection that accounts for it -
    /// or it is an .accdb encrypted in a way this library does not read.
    /// </exception>
    /// <param name="path">The path of the file to open.</param>
    /// <param name="options">What is known about the file.</param>
    /// <returns>The database, open. The caller disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
    public static Database Open(string path, DatabaseOpenOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        return OpenFile(path, options.FallbackFormat, options.Password is { Length: > 0 } password ? password : null);
    }

    /// <summary>
    /// Opens a file in the format its own header says it is in, as Jackcess takes it, and in
    /// <paramref name="fallback"/> only when the header does not say - a file without the signature of an Access
    /// database. So an Access 97 file is read as one, and is read-only, whatever <paramref name="fallback"/> is,
    /// and an Access 2000 file opened with a fallback of <see cref="FileFormat.Access97"/> is the Access 2000
    /// file it is, read and written as any other. A protected file is opened with <paramref name="password"/>, as
    /// <see cref="Open(string, DatabaseOpenOptions)"/> describes.
    /// </summary>
    private static Database OpenFile(string path, FileFormat? fallback, string? password)
    {
        var (format, hasHeader) = FormatOf(path, fallback);
        var file = new PageFile(path, format, FileMode.Open);
        try
        {
            // A file read in the fallback format has no header to say how it is protected: what its bytes there
            // would say means nothing.
            if (hasHeader) Unlock(file, path, password);
            EnsureCatalogReads(file, path);
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new Database(file);
    }

    /// <summary>
    /// The format of the file at <paramref name="path"/>, as its header names it - the signature at offset 0x04,
    /// "Standard Jet DB" for an .mdb and "Standard ACE DB" for an .accdb, and the version byte at 0x14, none of
    /// which a mask or a codec hides - and whether it has that header; <paramref name="fallback"/> for a file
    /// that has none.
    /// </summary>
    /// <exception cref="InvalidDataException">The file has no such header, and there is no fallback.</exception>
    private static (JetFormat Format, bool HasHeader) FormatOf(string path, FileFormat? fallback)
    {
        Span<byte> head = stackalloc byte[0x15];
        int read;
        // With the sharing the file is then opened with (PageFile): another database that has it open, for writing,
        // lets it be read.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            read = fs.Read(head);

        if (read == head.Length && (StartsWithSignature(head, "Standard Jet DB") || StartsWithSignature(head, "Standard ACE DB")))
            return (JetFormat.ForVersionCode(head[0x14]), true);
        if (fallback is { } format)
            return (JetFormat.For(format), false);
        throw new InvalidDataException(read < head.Length
            ? $"File '{path}' is too short to be an Access database."
            : $"File '{path}' is not a recognised Access database (missing 'Standard Jet/ACE DB' signature).");
    }

    /// <summary>
    /// Checks the password of a protected file and gives the file the codec its pages need, as its header says
    /// (see <see cref="Open(string, DatabaseOpenOptions)"/>). A file that is not protected is left as it is, and
    /// the password is not looked at.
    /// </summary>
    /// <exception cref="InvalidCredentialException">The file needs a password, and <paramref name="password"/> is not it.</exception>
    private static void Unlock(PageFile file, string path, string? password)
    {
        if (file.PageCount == 0) return;
        byte[] header = file.ReadPage(0);
        var format = file.Format;

        if (format.Version is FileFormat.Access97 or FileFormat.Access2000)
        {
            // Access checks a database password exactly - an Access 97 one in the file's code page, after converting
            // the one given to it - and opens a file whose pages are encoded with no password at all: the key is in
            // the header (tools/protected-fixtures.ps1, tools/jet3-password-fixture.ps1).
            if (!HeaderMask.OpensWith(header, format, password))
                throw new InvalidCredentialException(password is null
                    ? $"'{path}' has a database password: open it with a DatabaseOpenOptions that has its Password."
                    : $"The password given is not the database password of '{path}'.");
            if (JetCryptCodecHandler.FromDbHeader(header) is { } encoded)
                file.Codec = encoded;
            return;
        }

        // An .accdb with a password is encrypted with it: there is no reading it without.
        if (password is null)
        {
            if (OfficeCryptCodecHandler.IsEncrypted(header))
                throw new InvalidCredentialException(
                    $"'{path}' is encrypted with a password: open it with a DatabaseOpenOptions that has its Password.");
            return;
        }
        if (OfficeCryptCodecHandler.FromDbHeader(header, password) is { } encrypted)
            file.Codec = encrypted;
    }

    /// <summary>
    /// Refuses a file whose catalog page does not read as a table definition once what its header says of its
    /// protection is accounted for: one that is damaged, or protected in a way this library does not read. A file
    /// of fewer than three pages has no catalog to look at, and is let through.
    /// </summary>
    /// <exception cref="NotSupportedException">The catalog page does not read.</exception>
    private static void EnsureCatalogReads(PageFile file, string path)
    {
        if (file.PageCount < 3) return;
        byte[] catalogPage = file.ReadPage(JetFormat.PageSystemCatalog);
        if (catalogPage[0] == JetFormat.PageTypeTableDef && catalogPage[1] == 0x01) return;

        string why = file.Codec switch
        {
            OfficeCryptCodecHandler office =>
                $"was decrypted with its password ({office.Scheme}), and its system catalog page (page 2) still does not " +
                "read as a table definition: the file is damaged, or encrypted in a way this library does not read.",
            JetCryptCodecHandler =>
                "was decoded with the key its header holds, and its system catalog page (page 2) still does not read as " +
                "a table definition: the file is damaged.",
            _ =>
                "appears to be encrypted or corrupt — the system catalog page (page 2) does not start with the TableDef " +
                "header (0x02 0x01), and the file's header names no password or encryption that accounts for it.",
        };
        throw new NotSupportedException($"File '{path}' {why}");
    }

    /// <summary>
    /// Opens an existing .mdb / .accdb file, in the format its header names (the byte at offset 0x14): Access 97,
    /// Access 2000-2003, or one of the .accdb formats, Access 2007 to Access 2019. An .mdb whose pages Access
    /// encoded and that has no database password opens without one, as Access opens it; a file with a password
    /// is opened with <see cref="Open(string, DatabaseOpenOptions)"/>, which describes what is protected how.
    /// <para>
    /// An Access 97 file is opened for reading only, as Jackcess opens one (<see cref="IsReadOnly"/>): its tables
    /// and rows are read, and every change to it - a row inserted, updated or deleted, a table created or
    /// imported - is refused with <see cref="NotSupportedException"/> before anything is written.
    /// </para>
    /// </summary>
    /// <param name="path">The path of the file to open.</param>
    /// <returns>The database, open. The caller disposes it.</returns>
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
    /// <exception cref="InvalidDataException">The file is not an Access database: it is too short, or has no signature.</exception>
    /// <exception cref="InvalidCredentialException">
    /// The file has a database password, or is an encrypted .accdb: open it with
    /// <see cref="Open(string, DatabaseOpenOptions)"/> and its password.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is damaged: its catalog does not read, and its header names no protection that accounts for it.
    /// </exception>
    public static Database Open(string path) => OpenFile(path, fallback: null, password: null);

    // ── Foreign keys ─────────────────────────────────────────────────────────

    /// <summary>
    /// What keeps <paramref name="table"/>'s relationships, or null when nothing is to be kept: enforcement is off,
    /// or no relationship with referential integrity has the table on either side.
    /// </summary>
    internal ForeignKeyEnforcer? ForeignKeysFor(Table table)
    {
        if (!_enforceForeignKeys) return null;
        _foreignKeys ??= new ForeignKeyEnforcer(this);
        return _foreignKeys.Involves(table) ? _foreignKeys : null;
    }

    /// <summary>
    /// Returns true if the first bytes of <paramref name="buf"/> (starting at
    /// offset 0x04 — Jet's signature position) match the ASCII string
    /// <paramref name="signature"/>.
    /// </summary>
    private static bool StartsWithSignature(ReadOnlySpan<byte> buf, string signature)
    {
        const int offset = 0x04;
        if (buf.Length < offset + signature.Length) return false;
        for (int i = 0; i < signature.Length; i++)
            if (buf[offset + i] != (byte)signature[i]) return false;
        return true;
    }

    /// <summary>
    /// The names of the database's own tables - not its system tables (<see cref="GetSystemTableNames"/>) - as
    /// the catalog spells them and in the order it lists them. The catalog is read each time.
    /// </summary>
    public IReadOnlyList<string> GetTableNames()
    {
        ThrowIfDisposed();
        return _catalog.GetTableNames(includeSystem: false);
    }

    /// <summary>
    /// The names of the database's system tables - <c>MSysObjects</c>, <c>MSysRelationships</c> and the others
    /// Access keeps for itself - as the catalog spells them and in the order it lists them. Each can be opened
    /// with <see cref="GetTable"/>. The catalog is read each time.
    /// </summary>
    public IReadOnlyList<string> GetSystemTableNames()
    {
        ThrowIfDisposed();
        return _catalog.GetSystemTableNames();
    }

    /// <summary>
    /// Reads MSysRelationships and returns the schema's foreign-key / join
    /// relationships, grouping the per-column-pair rows back into one entry
    /// per named relationship. Returns an empty list when MSysRelationships
    /// is absent or empty. They are in the order the table lists them, which is the
    /// order they were made in.
    /// </summary>
    public IReadOnlyList<Relationship> GetRelationships()
    {
        // A database has MSysRelationships as a rule - an empty Access file includes it - but a trimmed one
        // may not.
        if (!TryGetTable("MSysRelationships", out var t))
            return Array.Empty<Relationship>();
        return BuildRelationships(t.Rows.ToList());
    }

    private static IReadOnlyList<Relationship> BuildRelationships(IReadOnlyList<Row> rows)
    {
        // MSysRelationships layout per Jackcess:
        //   szRelationship          - relationship name (groups rows together)
        //   szReferencedObject      - FROM table (parent)
        //   szReferencedColumn      - FROM column
        //   szObject                - TO table (child)
        //   szColumn                - TO column
        //   icolumn                 - 0-based pair index within the relationship
        //   ccolumn                 - total pair count
        //   grbit                   - flags (one-to-one, cascade, joins)
        var byName = new Dictionary<string, (string from, string to, int flags,
                                             List<(int idx, string fromCol, string toCol)> pairs)>(
                                             StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        foreach (var r in rows)
        {
            string name = r.TryGetValue("szRelationship", out var n) ? n?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;
            string from = r.TryGetValue("szReferencedObject", out var f) ? f?.ToString() ?? "" : "";
            string to   = r.TryGetValue("szObject",           out var t) ? t?.ToString() ?? "" : "";
            string fc   = r.TryGetValue("szReferencedColumn", out var fcv) ? fcv?.ToString() ?? "" : "";
            string tc   = r.TryGetValue("szColumn",           out var tcv) ? tcv?.ToString() ?? "" : "";
            int idx     = r.TryGetValue("icolumn", out var iv) && iv is not null ? Convert.ToInt32(iv) : 0;
            int flags   = r.TryGetValue("grbit",   out var gv) && gv is not null ? Convert.ToInt32(gv) : 0;

            if (!byName.TryGetValue(name, out var entry))
            {
                entry = (from, to, flags, new List<(int, string, string)>());
                byName[name] = entry;
                names.Add(name);
            }
            entry.pairs.Add((idx, fc, tc));
        }

        var result = new List<Relationship>(byName.Count);
        foreach (string name in names)
        {
            var e = byName[name];
            var pairs = e.pairs.OrderBy(p => p.idx)
                               .Select(p => (p.fromCol, p.toCol))
                               .ToList();
            result.Add(new Relationship(name, e.from, e.to, e.flags, pairs));
        }
        return result;
    }

    // ── Table operations ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a table: writes its definition, registers it in the catalog, and returns the <see cref="Table"/>,
    /// ready for rows. The key's index is kept as rows are inserted, updated and deleted.
    /// <code>
    ///   db.CreateTable("Loose", columns);                       // no primary key
    ///   db.CreateTable("People", columns, "Id");                // a key of one column
    ///   db.CreateTable("Lines", columns, "Order", "Line");      // a key of several, in key order
    /// </code>
    /// </summary>
    /// <param name="name">The table's name, which no other table of the database has.</param>
    /// <param name="columns">The table's columns, in their order: at least one.</param>
    /// <param name="primaryKey">
    /// The names of the primary key's columns, in key order: none for a table without a primary key, and at most
    /// <see cref="IndexDefinition.MaxColumns"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The name is empty, there are no columns, a column's definition is not one a table can have
    /// (<see cref="ColumnDefinition"/>), or the primary key names a column that is not among them or more
    /// columns than a key has. Nothing is created.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The primary key is on a column whose keys this library does not write as Access does, or the database
    /// is an Access 97 file, which is only read. Nothing is created.
    /// </exception>
    public Table CreateTable(string name, IEnumerable<ColumnDefinition> columns, params string[] primaryKey)
        => CreateTableCore(name, ColumnsOf(columns), KeyOf(primaryKey, nameof(primaryKey)), Array.Empty<IndexDefinition>());

    /// <summary>
    /// Creates a table with indexes beside its primary key: single- or multi-column, unique or not. Every index is
    /// kept as rows are inserted, updated and deleted, and a unique index refuses a duplicate before anything of
    /// the row is written.
    /// </summary>
    /// <param name="name">The table's name, which no other table of the database has.</param>
    /// <param name="columns">The table's columns, in their order: at least one.</param>
    /// <param name="primaryKey">The names of the primary key's columns, in key order; null or empty for no primary key.</param>
    /// <param name="indexes">The table's other indexes.</param>
    /// <exception cref="ArgumentException">
    /// The name is empty, there are no columns, a column's definition is not one a table can have
    /// (<see cref="ColumnDefinition"/>), the primary key or an index names a column that is not among them, the
    /// primary key names more columns than a key has, two indexes have one name, or an index definition is a
    /// primary key (<see cref="IndexDefinition.IsPrimaryKey"/>). Nothing is created.
    /// </exception>
    /// <exception cref="NotSupportedException">
    ///   An index is on a column whose keys this library does not write as Access does: a Decimal, Binary, OLE
    ///   or Complex column, or text in a sort order whose keys it does not make; or the database is
    ///   an Access 97 file, which is only read. Nothing is created.
    /// </exception>
    public Table CreateTable(string name, IEnumerable<ColumnDefinition> columns,
                             IEnumerable<string>? primaryKey,
                             IEnumerable<IndexDefinition> indexes)
    {
        if (indexes is null) throw new ArgumentNullException(nameof(indexes));
        return CreateTableCore(name, ColumnsOf(columns), KeyOf(primaryKey, nameof(primaryKey)), indexes.ToList());
    }

    /// <summary>The columns a table is asked to have, each checked: what refuses a definition refuses the table.</summary>
    private static IReadOnlyList<Column> ColumnsOf(IEnumerable<ColumnDefinition> columns)
    {
        if (columns is null) throw new ArgumentNullException(nameof(columns));
        return columns
            .Select(column => (column ?? throw new ArgumentException("A column definition must not be null.", nameof(columns)))
                .ToColumn(nameof(columns)))
            .ToList();
    }

    /// <summary>The primary key's columns, or null for none.</summary>
    private static IReadOnlyList<string>? KeyOf(IEnumerable<string>? primaryKey, string parameterName)
    {
        var key = primaryKey?.ToList();
        if (key is null || key.Count == 0) return null;
        if (key.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A primary key column name must not be empty.", parameterName);
        return key;
    }

    /// <summary>
    /// Creates a table of columns already made - an import's, mapped from another source - as
    /// <see cref="CreateTable(string, IEnumerable{ColumnDefinition}, string[])"/> does.
    /// </summary>
    internal Table CreateTableOf(string name, IReadOnlyList<Column> columns, IReadOnlyList<string>? primaryKey)
        => CreateTableCore(name, columns, primaryKey is { Count: > 0 } ? primaryKey : null, Array.Empty<IndexDefinition>());

    /// <summary>
    /// Forgets <paramref name="table"/>, which this session made and an undo scope then took back with everything
    /// else it wrote: the file no longer has it, and the object is marked dropped, so that anything still holding it
    /// is refused rather than reading pages that are gone.
    /// </summary>
    internal void ForgetTakenBack(Table table)
    {
        int page = table.Definition.TdefPageNumber;
        if (_tables.TryGetValue(page, out var held) && ReferenceEquals(held, table))
            _tables.Remove(page);
        table.MarkDropped();
    }

    private Table CreateTableCore(string name, IReadOnlyList<Column> columns,
                                  IReadOnlyList<string>? pkColumns,
                                  IReadOnlyList<IndexDefinition> secondary)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));
        if (columns is null || columns.Count == 0)
            throw new ArgumentException("At least one column is required.", nameof(columns));
        for (int i = 1; i < columns.Count; i++)
            for (int j = 0; j < i; j++)
                if (ObjectNames.Same(columns[j].Name, columns[i].Name))
                    throw new ArgumentException(
                        $"Table '{name}' is given two columns, '{columns[j].Name}' and '{columns[i].Name}', whose names Access takes as one. Nothing was created.",
                        nameof(columns));
        _file.EnsureWritable();

        var format = _file.Format;

        // The table has columns of its own, copies of the caller's: the caller's may be another table's - one
        // of another database, that sorts otherwise - and are left as they came, whether the table is made or
        // refused. The copies' text sorts as this database's does, as a table Access adds to it would.
        var textOrder = _catalog.TextSortOrder;
        columns = columns.Select(column => column.CopyFor(textOrder)).ToList();

        // Everything that can refuse the table does so before a page is added: its indexes, the
        // definition (laid out once with no pages), the name and the catalog.
        var planned = PlanIndexes(name, columns, pkColumns, secondary);
        new TableDefinition(name, columns)
        {
            Indexes = BuildIndexes(planned, planned.Select(_ => (Page: 0, Row: 0)).ToList(), rootPages: planned.Select(_ => 1).ToList()),
        }.Serialize(format);
        _catalog.EnsureCanRegister(name);

        // What the definitions ask of the columns beyond their types - Required, Allow Zero Length, a Replication
        // ID AutoNumber's GenGUID() - which Access keeps as properties, in the table's row of the catalog.
        var propertyMaps   = ColumnProperties.For(columns);
        byte[]? properties = PropertyMapWriter.Write(propertyMaps, out var propertyNames);

        // A table with a Large Number or a Date/Time Extended column makes the file one of Access 2016's format
        // or Access 2019's, as it does when Access adds the table: older versions of Access then refuse the file
        // rather than misread it. An .mdb has no such column, and the table is refused here, with no page added.
        // The header is changed last, once the table is in the catalog: a creation that fails on the way - a
        // full disk, when a page is added - must not leave a file marked for a table it does not have.
        byte? version = FormatVersionFor($"Table '{name}' was not created", columns, format);

        // 1. Allocate the TDEF page (content written in step 6).
        int tdefPage = _allocator.AllocatePage();

        // 2. Allocate the usage-map page: owned pages, free space, and a map for each index's
        //    pages, where Access keeps them. Maps that do not fit the page go on pages of their own.
        int mapsPerPage = UsageMap.MapsPerUmapPage(format);
        int onFirstPage = Math.Min(planned.Count, mapsPerPage - FirstIndexUmapRow);
        int umapPage    = _allocator.AllocateUmapPage(FirstIndexUmapRow + onFirstPage);
        var indexMaps   = Enumerable.Range(0, onFirstPage).Select(i => (Page: umapPage, Row: FirstIndexUmapRow + i)).ToList();
        while (indexMaps.Count < planned.Count)
        {
            int rows = Math.Min(planned.Count - indexMaps.Count, mapsPerPage);
            int page = _allocator.AllocateUmapPage(rows);
            indexMaps.AddRange(Enumerable.Range(0, rows).Select(row => (Page: page, Row: row)));
        }

        // 3. Allocate a LVAL usage-map page for each Memo/OLE column (must happen before
        //    Serialize so the page numbers can be embedded in the TDEF).
        var lvalUmaps = new System.Collections.Generic.Dictionary<string, LvalUmapRef>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
            if (col.DataType.IsLongValue())
            {
                int lvalUmapPage = _allocator.AllocateUmapPage();
                lvalUmaps[col.Name] = new LvalUmapRef(lvalUmapPage, 0, lvalUmapPage, 1);
            }

        // 4. Build the TableDefinition.
        var tableDef = new TableDefinition(name, columns)
        {
            TdefPageNumber = tdefPage,
            UmapPageNumber = umapPage,
            OwnedPagesRow  = 0,
            FreeSpaceRow   = 1,
        };
        foreach (var kvp in lvalUmaps)
            tableDef.LvalColumnUmapPages[kvp.Key] = kvp.Value;

        // 5. Give each index an empty root leaf, listed in the index's own usage map, so its
        //    page number is known at Serialize time.
        var idxWriter = new IndexWriter(_file, _allocator);
        var rootPages = new List<int>(planned.Count);
        for (int i = 0; i < planned.Count; i++)
        {
            int root = idxWriter.CreateEmptyLeafPage(tdefPage);
            UsageMap.AddPage(_file, _allocator, indexMaps[i].Page, indexMaps[i].Row, root);
            rootPages.Add(root);
        }
        tableDef.Indexes = BuildIndexes(planned, indexMaps, rootPages);

        if (pkColumns is { Count: > 0 })
        {
            var pk = tableDef.Indexes[0];
            tableDef.PrimaryKeyIndexPage      = pk.RootPageNumber;
            tableDef.PrimaryKeyIndexUmapPage  = pk.UsedPagesUmapPage;
            tableDef.PrimaryKeyIndexUmapRow   = pk.UsedPagesUmapRow;
            if (pkColumns.Count == 1)
                tableDef.PrimaryKeyColumnName = pkColumns[0];
            else
                tableDef.PrimaryKeyColumnNames = pkColumns;
        }

        // 6. Now serialise — the index blocks, slots and names are included.
        byte[] tdefBytes = tableDef.Serialize(format);
        TdefBuffer.Write(_file, _allocator, tdefPage, tdefBytes);

        // 7. Register the table in MSysObjects.
        _catalog.InsertTableEntry(name, tdefPage, properties);

        // 8. The file's version, for a column older versions of Access do not have: one page, written in place.
        if (version is { } newer) DatabaseHeader.RaiseVersion(_file, newer);

        propertyMaps.Written(propertyNames);
        var table = new Table(_file, _allocator, tableDef, owningDb: this, properties: propertyMaps);
        _tables[tdefPage] = table;
        return table;
    }

    // A table's index maps follow its owned-pages and free-space maps on its usage-map page.
    private const int FirstIndexUmapRow = 2;

    /// <summary>
    /// The version a file's header must name, at the least, for a table of <paramref name="columns"/> to be in
    /// it: Access 2016's for a Large Number column, Access 2019's for a Date/Time Extended one, and null when
    /// the table has neither. Access gives an .accdb of any earlier format that version when such a column is
    /// added to it, and refuses the column in an .mdb ("Invalid argument"), as this does.
    /// </summary>
    /// <param name="whatWasNotDone">How the refusal begins: "Table 'T' was not created", or the like.</param>
    /// <param name="columns">The table's columns.</param>
    /// <param name="format">The format of the file the table is to be in.</param>
    /// <exception cref="NotSupportedException">The file is an .mdb, which has no such column. Nothing is changed.</exception>
    internal static byte? FormatVersionFor(string whatWasNotDone, IReadOnlyList<Column> columns, JetFormat format)
    {
        var newest = columns.Where(c => c.DataType is DataType.Int64 or DataType.DateTimeExtended)
                            .OrderByDescending(c => c.DataType).FirstOrDefault();
        if (newest is null) return null;
        if (format.Version is FileFormat.Access97 or FileFormat.Access2000)
            throw new NotSupportedException(
                $"{whatWasNotDone}: its column '{newest.Name}' is a {newest.DataType}, which an .mdb file " +
                "(Access 2000-2003) does not have. Create the database as an .accdb, FileFormat.Access2007 or later.");
        return newest.DataType == DataType.DateTimeExtended ? DatabaseHeader.Version2019 : DatabaseHeader.Version2016;
    }

    /// <summary>An index a new table is to have: checked, with its columns found, and no pages yet.</summary>
    private sealed class PlannedIndex
    {
        public PlannedIndex(string name, IReadOnlyList<IndexColumn> columns, byte flags, byte indexType)
        {
            Name      = name;
            Columns   = columns;
            Flags     = flags;
            IndexType = indexType;
        }

        public string Name { get; }
        public IReadOnlyList<IndexColumn> Columns { get; }
        public byte Flags { get; }
        public byte IndexType { get; }
    }

    /// <summary>
    /// Checks the indexes a new table is asked for and returns them in the order they are written: the primary key
    /// first, named as Access names it, then the others. Assigns the columns their numbers, which the index blocks
    /// name (Serialize assigns the same ones).
    /// </summary>
    private static List<PlannedIndex> PlanIndexes(string table, IReadOnlyList<Column> columns,
                                                  IReadOnlyList<string>? pkColumns,
                                                  IReadOnlyList<IndexDefinition> secondary)
    {
        for (short i = 0; i < columns.Count; i++)
            columns[i].Ordinal = i;

        // A key's or an index's columns come in the call that names the table's columns: one that is not among
        // them is a fault in the arguments.
        Column Find(string column, string what, bool ofPrimaryKey = false)
        {
            var found = columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{what} column '{column}' not found in table '{table}'.", ofPrimaryKey ? "primaryKey" : "indexes");
            if (!IndexKeys.CanEncode(found))
                throw new NotSupportedException(
                    $"{what} on '{column}' is not supported: {IndexKeys.WhyNot(found)}, " +
                    "so its index entries would not be the ones Access writes.");
            return found;
        }

        var planned = new List<PlannedIndex>();
        if (pkColumns is { Count: > 0 })
        {
            if (pkColumns.Count > IndexDefinition.MaxColumns)
                throw new ArgumentException(
                    $"Composite primary keys are limited to {IndexDefinition.MaxColumns} columns; got {pkColumns.Count}.", "primaryKey");
            planned.Add(new PlannedIndex(
                TableDefinition.PrimaryKeyIndexName,
                pkColumns.Select(pk => new IndexColumn(Find(pk, "Primary key", ofPrimaryKey: true), TableDefinition.AscendingColumnFlag)).ToList(),
                // Unique and required (0x89), as Access writes a primary key's block.
                TableDefinition.UnknownIndexFlag | TableDefinition.UniqueIndexFlag | TableDefinition.RequiredIndexFlag,
                TableDefinition.PrimaryKeyIndexType));
        }

        foreach (var index in secondary)
        {
            if (index is null) throw new ArgumentException("An index definition must not be null.", nameof(secondary));
            if (index.IsPrimaryKey)
                throw new ArgumentException(
                    $"Index '{index.Name}' is a primary key: a table is created with its primary key by naming the key's columns, " +
                    "not by an index definition.", nameof(secondary));
            if (planned.FirstOrDefault(p => ObjectNames.Same(p.Name, index.Name)) is { } taken)
                throw new ArgumentException($"Table '{table}' is given two indexes, '{taken.Name}' and '{index.Name}', whose names Access takes as one.", nameof(secondary));
            foreach (string descending in index.DescendingColumns)
                if (!index.Columns.Contains(descending, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"Index '{index.Name}' sorts '{descending}' descending, which is not one of its columns.", nameof(secondary));

            byte flags = TableDefinition.UnknownIndexFlag;
            if (index.IsUnique)    flags |= TableDefinition.UniqueIndexFlag;
            if (index.IgnoreNulls) flags |= TableDefinition.IgnoreNullsIndexFlag;
            if (index.IsRequired)  flags |= TableDefinition.RequiredIndexFlag;
            planned.Add(new PlannedIndex(
                index.Name,
                index.Columns.Select(c => new IndexColumn(
                    Find(c, $"Index '{index.Name}'"),
                    index.DescendingColumns.Contains(c, StringComparer.OrdinalIgnoreCase) ? (byte)0 : TableDefinition.AscendingColumnFlag)).ToList(),
                flags,
                indexType: 0));
        }

        if (planned.Count > IndexDefinition.MaxIndexesPerTable)
            throw new ArgumentException(
                $"A table has at most {IndexDefinition.MaxIndexesPerTable} indexes; '{table}' is given {planned.Count}.", nameof(secondary));
        return planned;
    }

    // The planned indexes with their pages: each its own block, with its own usage map and root.
    private static IReadOnlyList<Index> BuildIndexes(List<PlannedIndex> planned, IReadOnlyList<(int Page, int Row)> maps,
                                                     IReadOnlyList<int> rootPages)
        => planned.Select((p, i) => new Index(p.Name, p.Columns, rootPages[i], indexNumber: i, p.Flags, p.IndexType,
                                              indexDataNumber: i, usedPagesUmapPage: maps[i].Page,
                                              usedPagesUmapRow: maps[i].Row)).ToList();

    /// <summary>
    /// The table named <paramref name="name"/>, whatever the case of its letters: one of the database's own
    /// tables or a system table. The catalog is read to find it. A table is one <see cref="Table"/> for as long
    /// as the database is open - asked for again, by any spelling, or returned by
    /// <see cref="CreateTable(string, IEnumerable{ColumnDefinition}, string[])"/>, it is
    /// the same object - so what is set on it (<see cref="Table.AllowAutoNumberInsert"/>) holds for every caller,
    /// and its <see cref="Table.Name"/> is the name as the catalog spells it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The database has no table of that name (<see cref="TryGetTable"/> asks without throwing), or
    /// <paramref name="name"/> is empty.
    /// </exception>
    public Table GetTable(string name)
        => TryGetTable(name, out var table)
            ? table
            : throw new ArgumentException($"Table '{name}' was not found in this database.", nameof(name));

    /// <summary>
    /// The table named <paramref name="name"/> when the database has one: what <see cref="GetTable"/> does, for a
    /// caller asking whether the table is there.
    /// </summary>
    /// <returns>Whether the database has a table of that name; <paramref name="table"/> is it.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public bool TryGetTable(string name, [NotNullWhen(true)] out Table? table)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));

        int tdefPageNum = _catalog.FindTable(name, out string? listedName, out var readProperties);
        if (tdefPageNum < 0)
        {
            table = null;
            return false;
        }

        if (!_tables.TryGetValue(tdefPageNum, out table))
        {
            // The table's properties are read with it, from its row of the catalog as that was just read: what
            // they say of a column (Column.IsRequired, Column.AllowZeroLength) is then known for as long as the
            // column is held, and no insert reads the catalog for them. A value that cannot be read does not keep
            // the table from being opened: it is read again, and the failure reported, when it is asked for.
            PropertyMaps? properties = null;
            try { properties = PropertyMapReader.Read(readProperties!(), _file.Format); }
            catch (Exception e) when (e is IOException or ArgumentException or IndexOutOfRangeException) { }

            table = new Table(_file, _allocator, ReadDefinition(_file, listedName!, tdefPageNum), owningDb: this, properties);
            _tables[tdefPageNum] = table;
        }
        return true;
    }

    /// <summary>
    /// The definition of table <paramref name="name"/> as its definition page, <paramref name="tdefPageNum"/>, now
    /// holds it: what <see cref="GetTable"/> opens a table with, and what a <see cref="Table"/> reads again after
    /// the table has been given a column or an index.
    /// </summary>
    internal static TableDefinition ReadDefinition(PageFile file, string name, int tdefPageNum)
    {
        var format   = file.Format;
        var tdefPage = TdefBuffer.Read(file, tdefPageNum, out _, out bool isBroken);
        var info     = TdefReader.Read(tdefPage, format);

        var tableDef = new TableDefinition(name, info.Columns)
        {
            TdefPageNumber = tdefPageNum,
            UmapPageNumber = info.OwnedPagesUmapPage,
            OwnedPagesRow  = info.OwnedPagesUmapRow,
            FreeSpaceRow   = info.FreeSpaceUmapRow,
            Indexes        = info.Indexes,
            HasUnreadIndexes = info.HasUnreadIndexes || isBroken,
            MaxColumnCount    = info.MaxColumnCount,
            MaxVarColumnCount = info.MaxVarColumnCount,
            HasUnreadColumns  = info.HasUnreadColumns || isBroken,
        };

        foreach (var kvp in info.LvalColumnUmapPages)
            tableDef.LvalColumnUmapPages[kvp.Key] = kvp.Value;

        // Reattach in-memory PK config from the on-disk metadata so that
        // Table.FindRowByPrimaryKey works across open/close boundaries.
        var pkIndex = info.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
        if (pkIndex is not null && pkIndex.Columns.Count > 0)
        {
            tableDef.PrimaryKeyIndexPage       = pkIndex.RootPageNumber;
            tableDef.PrimaryKeyIndexUmapPage   = pkIndex.UsedPagesUmapPage;
            tableDef.PrimaryKeyIndexUmapRow    = pkIndex.UsedPagesUmapRow;
            if (pkIndex.Columns.Count == 1)
                tableDef.PrimaryKeyColumnName = pkIndex.Columns[0].Column.Name;
            else
                tableDef.PrimaryKeyColumnNames =
                    pkIndex.Columns.Select(ic => ic.Column.Name).ToArray();
        }

        return tableDef;
    }

    // ── Properties ───────────────────────────────────────────────────────────

    private PropertyMaps? _databaseProperties;
    private PropertyMaps? _summaryProperties;
    private PropertyMaps? _userDefinedProperties;

    /// <summary>
    /// The database's own properties, as Access keeps them: <c>AccessVersion</c>, <c>Build</c> and the like,
    /// and those a caller adds - what DAO's <c>Database.Properties</c> lists beyond its built-in ones. Jackcess's
    /// <c>getDatabaseProperties</c>. Read from the catalog the first time they are asked for; empty for a
    /// database that has none. <see cref="PropertyMap.Set(string, object)"/> and <see cref="PropertyMap.Save"/>
    /// change them.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    public PropertyMap DatabaseProperties => DatabaseMaps(ref _databaseProperties, "MSysDb").Default;

    /// <summary>
    /// The properties of the database's Summary sheet - <c>Title</c>, <c>Author</c>, <c>Company</c> and the
    /// rest - which Access keeps with its <c>SummaryInfo</c> document. Jackcess's <c>getSummaryProperties</c>.
    /// Empty for a database that has none. A database made by Access has the document; one made by DAO alone
    /// has not, and this library does not add it: <see cref="PropertyMap.Save"/> then throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    public PropertyMap SummaryProperties => DatabaseMaps(ref _summaryProperties, "SummaryInfo").Default;

    /// <summary>
    /// The properties of the database's Custom sheet, which Access keeps with its <c>UserDefined</c> document.
    /// Jackcess's <c>getUserDefinedProperties</c>. Empty for a database that has none; saved as
    /// <see cref="SummaryProperties"/> are, and refused the same way in a database without the document.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    public PropertyMap UserDefinedProperties => DatabaseMaps(ref _userDefinedProperties, "UserDefined").Default;

    // The maps of one document of the Databases container, read once and kept.
    private PropertyMaps DatabaseMaps(ref PropertyMaps? kept, string document)
    {
        ThrowIfDisposed();
        if (kept is not null) return kept;

        var maps = PropertyMapReader.Read(_catalog.FindDatabaseObject(document)?.Properties, _file.Format);
        maps.Saver = saved => SaveProperties(saved, () => _catalog.FindDatabaseObject(document), $"the database's '{document}' document");
        return kept = maps;
    }

    /// <summary>
    /// Writes <paramref name="maps"/> as the LvProp of the catalog's row for the object they belong to, which
    /// <paramref name="find"/> finds: the row is rewritten, its other values as they were and its entries in the
    /// catalog's indexes following it. Everything that refuses the change does so before a byte is written, and
    /// what is written is taken back if writing it fails.
    /// </summary>
    /// <param name="maps">The properties to write.</param>
    /// <param name="find">Finds the object's row in the catalog as it now is; null when it has none.</param>
    /// <param name="owner">What the properties belong to, for a refusal's message.</param>
    /// <param name="withinUndo">Whether the caller has an undo scope open, which takes this change back with its own.</param>
    internal void SaveProperties(PropertyMaps maps, Func<SystemCatalog.ObjectRow?> find, string owner, bool withinUndo = false)
    {
        ThrowIfDisposed();
        _file.EnsureWritable();

        byte[]? value = PropertyMapWriter.Write(maps, out var names);
        var row = find() ?? throw new NotSupportedException(
            $"The properties of {owner} are not written: the database's catalog has no such object to keep them with, " +
            "and this library does not add one. Nothing was changed.");
        var catalog = GetTable("MSysObjects");
        if (catalog.InsertRefusal is { } why)
            throw new NotSupportedException($"The properties of {owner} are not written: {why}");

        var change = new Row { ["LvProp"] = value };
        if (withinUndo)
            catalog.RewriteRowAt(row.RowPointer, change);
        else
        {
            using var undo = _file.BeginUndo();
            catalog.RewriteRowAt(row.RowPointer, change);
            undo.Commit();
        }
        maps.Written(names);
    }

    /// <summary>
    /// Whether the properties of <paramref name="column"/>, a column about to be added to
    /// <paramref name="table"/>, can be written: asked before anything is. Where they cannot - the catalog has an
    /// index whose keys this library does not write (a database that sorts in an order other than General and
    /// General - Legacy), or the table's properties were not read whole - a column that is what was asked for with
    /// no property at all is still added, without them: false is returned. One whose definition only a property
    /// can say is refused.
    /// </summary>
    /// <exception cref="NotSupportedException">The column needs a property and the table's cannot be written.</exception>
    internal bool ColumnPropertiesCanBeWritten(TableDefinition table, Column column)
    {
        string? why = null;
        if (_catalog.FindObject(table.TdefPageNumber) is not { } row)
            why = "the catalog has no row for the table";
        else if (PropertyMapReader.Read(row.Properties, _file.Format).UnreadReason is { } unread)
            why = $"the ones in the file were not all read ({unread}), and writing those that were would lose the rest";
        else if (GetTable("MSysObjects").InsertRefusal is { } refusal)
            why = refusal.TrimEnd('.');
        if (why is null) return true;

        if (ColumnProperties.Needs(column) is not { } needs) return false;
        throw new NotSupportedException(
            $"Column '{column.Name}' is not added to '{table.Name}': {needs} is kept as a property of the column, and the table's " +
            $"properties cannot be written: {why}. Nothing was changed.");
    }

    /// <summary>
    /// Writes the properties a column this library adds is given (<see cref="ColumnProperties"/>): the column's
    /// block, added to the table's properties as the file holds them. What a caller has changed in the table's
    /// maps and not saved is not written with it. Called inside the undo scope of the change that adds the
    /// column, which takes it back with the column if it fails; the table's open maps take the column's
    /// properties in once they are written.
    /// </summary>
    internal void AddColumnProperties(TableDefinition table, Column column)
    {
        int id = table.TdefPageNumber;
        var inFile = PropertyMapReader.Read(_catalog.FindObject(id)?.Properties, _file.Format);

        // A block Access left for a column of this name that is gone is not this column's.
        if (inFile.Get(column.Name) is { } leftover)
            foreach (var property in leftover.ToList())
                leftover.Remove(property.Name);
        ColumnProperties.Add(inFile, column);
        if (inFile.Get(column.Name) is not { Count: > 0 } written) return;   // a Long AutoNumber has none

        SaveProperties(inFile, () => _catalog.FindObject(id), $"table '{table.Name}'", withinUndo: true);
        if (_tables.TryGetValue(id, out var open))
            open.PropertyMaps.Adopt(written, inFile.Names);
    }

    /// <summary>
    /// Whether a column that is about to be dropped or renamed has properties in the file, which go or follow it:
    /// asked before anything is written. A column with none needs nothing of the table's properties.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The column has properties and the table's cannot be written: they were not read whole, or the catalog has
    /// an index whose keys this library does not write.
    /// </exception>
    internal bool ColumnHasPropertiesToChange(TableDefinition table, string column, string whatIsNotDone)
    {
        if (_catalog.FindObject(table.TdefPageNumber) is not { } row) return false;
        var inFile = PropertyMapReader.Read(row.Properties, _file.Format);
        string? why = inFile.UnreadReason is { } unread
            ? $"the ones in the file were not all read ({unread}), and writing those that were would lose the rest"
            : inFile.Get(column) is null ? null : GetTable("MSysObjects").InsertRefusal?.TrimEnd('.');
        if (inFile.UnreadReason is null && inFile.Get(column) is null) return false;
        if (why is null) return true;
        throw new NotSupportedException(
            $"Column '{column}' of '{table.Name}' is not {whatIsNotDone}: Access keeps properties for it, and the table's " +
            $"properties cannot be written: {why}. Nothing was changed.");
    }

    /// <summary>
    /// Takes a dropped column's properties out of the table's, as the file holds them, or - with
    /// <paramref name="newName"/> - gives a renamed column's its new name: what Access was seen to do with a
    /// column's block for each (#37). Written by the one writer of properties (<see cref="SaveProperties"/>),
    /// inside the undo scope of the change to the column; what a caller has changed in the table's maps and not
    /// saved is not written with it, and the table's open maps then have the same change made to them.
    /// </summary>
    internal void ChangeColumnProperties(TableDefinition table, string column, string? newName)
    {
        int id = table.TdefPageNumber;
        var inFile = PropertyMapReader.Read(_catalog.FindObject(id)?.Properties, _file.Format);
        bool changed = newName is null ? inFile.RemoveColumn(column) : inFile.RenameColumn(column, newName);
        if (!changed) return;

        SaveProperties(inFile, () => _catalog.FindObject(id), $"table '{table.Name}'", withinUndo: true);
        if (_tables.TryGetValue(id, out var open))
            open.PropertyMaps.AdoptColumnChange(column, newName);
    }

    // ── Changing the schema ──────────────────────────────────────────────────

    /// <summary>The catalog, for a change to the schema that registers an object in it.</summary>
    internal SystemCatalog Catalog => _catalog;

    /// <summary>What appends pages to the file, for a change to the schema.</summary>
    internal PageAllocator Allocator => _allocator;

    /// <summary>
    /// Has the relationships read again by whatever next keeps them (<see cref="EnforceForeignKeys"/>): one has
    /// been created, or a table in one has been given an index its rows can be found through.
    /// </summary>
    internal void SchemaChanged() => _foreignKeys = null;

    /// <summary>
    /// Creates a relationship between two tables, as Access's Relationships window and Jackcess's
    /// <c>RelationshipBuilder</c> do: its rows in <c>MSysRelationships</c> and its entry in the catalog and, when it
    /// has referential integrity, the two indexes Access keeps for one - on the referenced table an index named
    /// <c>.rB</c>, <c>.rC</c>... that shares the tree of the unique index on the referenced columns, and on the
    /// referring table an index with the relationship's name on the foreign key, built over the rows already there
    /// (or sharing the tree of a plain index already on exactly those columns). Access then shows the
    /// relationship and enforces it, and so does this library when <see cref="EnforceForeignKeys"/> is set.
    /// <para>
    /// Everything is checked before anything is written, and what is written is taken back if writing it fails:
    /// a refused relationship leaves the file byte for byte as it was.
    /// </para>
    /// </summary>
    /// <returns>The relationship, as <see cref="GetRelationships"/> lists it from then on.</returns>
    /// <exception cref="ArgumentException">
    /// The definition names a table or a column that is not there; names no columns, or a different number on
    /// each side, or a column twice on one side of a relationship with referential integrity; or asks for a
    /// cascade without referential integrity. Nothing is changed.
    /// </exception>
    /// <exception cref="ConstraintViolationException">
    /// With referential integrity: a row of the referring table refers to no row of the referenced table.
    /// Nothing is changed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Two matched columns are of different types; a relationship of that name exists, or the referring table
    /// has an index of that name; or, with referential integrity, the referenced columns have no unique index
    /// (or primary key) on exactly them. Nothing is changed.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is an Access 97 one, or the foreign key is on a column whose index keys this library does not
    /// write. Nothing is changed.
    /// </exception>
    public Relationship CreateRelationship(RelationshipDefinition definition)
    {
        ThrowIfDisposed();
        return SchemaChange.CreateRelationship(this, _file, definition ?? throw new ArgumentNullException(nameof(definition)));
    }

    /// <summary>
    /// Drops the relationship named <paramref name="name"/>, whatever the case of its letters, as DAO's
    /// <c>Relations.Delete</c> and Access's Relationships window do: its rows in <c>MSysRelationships</c>, its
    /// entry in the catalog and the permissions on it and, when it has referential integrity, the index Access
    /// keeps for it on each of its tables. An index that was built for it goes with its pages; one that shared
    /// another index's tree leaves that index as it was. The tables' rows are not touched.
    /// <para>
    /// Everything is found before anything is written, and what is written is taken back if writing it fails: a
    /// refused change leaves the file byte for byte as it was.
    /// </para>
    /// </summary>
    /// <param name="name">The relationship's name (<see cref="Relationship.Name"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">The database has no relationship of that name. Nothing is changed.</exception>
    /// <exception cref="NotSupportedException">
    /// The file is an Access 97 one, or a system table that holds the relationship has an index whose keys this
    /// library does not write. Nothing is changed.
    /// </exception>
    public void DropRelationship(string name)
    {
        ThrowIfDisposed();
        SchemaRemoval.DropRelationship(this, _file, name ?? throw new ArgumentNullException(nameof(name)));
    }

    /// <summary>
    /// Drops the table named <paramref name="name"/>, whatever the case of its letters, with its rows, as
    /// Access's <c>DROP TABLE</c> does: its entry in the catalog and the permissions on it go, the relationships
    /// in which it refers to another table - or to itself - go with it, and every page it has is given back to
    /// the file, for Access to use again. A <see cref="Table"/> got for it before is of no further use: whatever
    /// is asked of it throws <see cref="InvalidOperationException"/>.
    /// <para>
    /// Everything is found before anything is written, and what is written is taken back if writing it fails: a
    /// refused change leaves the file byte for byte as it was.
    /// </para>
    /// </summary>
    /// <param name="name">The table's name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">The database has no table of that name, or the name is empty. Nothing is changed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Another table refers to the table in a relationship, as Access refuses it: drop the relationship first
    /// (<see cref="DropRelationship"/>). Nothing is changed.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is an Access 97 one; the table is a system table, or has a complex column (attachments or several
    /// values); or a system table that lists it has an index whose keys this library does not write. Nothing is
    /// changed.
    /// </exception>
    public void DropTable(string name)
    {
        ThrowIfDisposed();
        if (name is null) throw new ArgumentNullException(nameof(name));
        int tdefPage = SchemaRemoval.DropTable(this, _file, name);
        if (_tables.TryGetValue(tdefPage, out var dropped))
        {
            dropped.MarkDropped();
            _tables.Remove(tdefPage);
        }
    }

    /// <summary>
    /// Gives the table named <paramref name="name"/>, whatever the case of its letters, the name
    /// <paramref name="newName"/>, as DAO's <c>TableDef.Name</c> does: in the catalog, and in every relationship
    /// the table is part of. The table's rows, columns and indexes are not touched. A saved query that names the
    /// table goes on naming it as it was, as it does when Access's engine renames one. A <see cref="Table"/> got
    /// for the table before has the new name (<see cref="Table.Name"/>) from then on.
    /// <para>
    /// The change is whole or not at all: a refused one leaves the file byte for byte as it was. A name that
    /// differs from the table's only in the case of its letters is taken; the name the table has changes nothing.
    /// </para>
    /// </summary>
    /// <param name="name">The name the table has.</param>
    /// <param name="newName">The name to give it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="newName"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The database has no table named <paramref name="name"/>, or the name is empty; or
    /// <paramref name="newName"/> is the name of another table or query, or one Access does not allow: empty,
    /// more than 64 characters, with a period, an exclamation mark, a grave accent, a square bracket or a control
    /// character in it, or a space at its start. Nothing is changed.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is an Access 97 one; the table is a system table; or the catalog has an index whose keys this
    /// library does not write, for the name or at all. Nothing is changed.
    /// </exception>
    public void RenameTable(string name, string newName)
    {
        ThrowIfDisposed();
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (newName is null) throw new ArgumentNullException(nameof(newName));
        int tdefPage = SchemaRename.RenameTable(this, _file, name, newName);
        if (_tables.TryGetValue(tdefPage, out var renamed) && renamed.Name != newName)
            renamed.Renamed(newName);
    }

    /// <summary>
    /// Closes the file. Everything written is in the file already: nothing is held back until now. A second call
    /// does nothing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }
}
