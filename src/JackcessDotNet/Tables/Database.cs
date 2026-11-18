using System.IO;

namespace JackcessDotNet;

/// <summary>
/// Entry point for creating and opening Microsoft Access (.mdb / .accdb) database files.
///
/// Usage:
/// <code>
///   using var db = Database.Create("MyFile.mdb", JetVersion.Jet4);
///   var table = db.CreateTable("Customers", new[]
///   {
///       new ColumnBuilder("Id",   DataType.Long ).AutoNumber().Build(),
///       new ColumnBuilder("Name", DataType.Text ).MaxLength(100).Build(),
///   });
///   var row = new Row { ["Name"] = "Alice" };
///   table.Insert(row);          // row["Id"] is now 1
/// </code>
/// </summary>
public sealed class Database : IDisposable
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;

    /// <summary>The open file, for tests that look at its pages.</summary>
    internal PageFile File => _file;
    private readonly SystemCatalog _catalog;
    private ForeignKeyEnforcer? _foreignKeys;

    private Database(PageFile file)
    {
        _file      = file;
        _allocator = new PageAllocator(file);
        _catalog   = new SystemCatalog(file);
    }

    /// <summary>
    /// When true, the relationships in <c>MSysRelationships</c> that have referential integrity are kept, as
    /// Access keeps them, by <see cref="Table.Insert"/>, <see cref="Table.UpdateByPrimaryKey"/> and
    /// <see cref="Table.DeleteRow"/>. Default <c>false</c>: nothing is checked and nothing cascades.
    /// <para>
    /// A row that would refer to no parent row is refused: on insert, and when an update changes its foreign key.
    /// A foreign key whose columns are all null refers to nothing and is allowed. Changing a parent's key, or
    /// deleting the parent, does what the relationship says: with cascade update the referring rows take the new
    /// key; with cascade delete they are deleted, and the rows referring to them in turn; with cascade-to-null
    /// their foreign key is set to null; with none of these the change is refused while any row refers to the
    /// parent.
    /// </para>
    /// <para>
    /// A refused change throws <see cref="InvalidOperationException"/> and leaves the file as it was: everything is
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
    /// An index is searched only once it has been seen to hold every row of its table. Access trusts an index to;
    /// a file a version of this library before it kept every index wrote to (those in ExportMdb.Interop up to
    /// 2.1.2) can have rows that are in their table's primary key and in no other index - not in the one on their
    /// foreign key, so that through it they would not be found as children, and their parent could be deleted or
    /// re-keyed from under them (qcatco/exportmdb.interop#64). So the first change that looks for rows through an
    /// index, while the database is open, reads that index and its table once and compares them row by row; the
    /// changes after it find rows through the index, or, when it does not hold every row, by reading the table as
    /// for a table with no index. An entry whose row has gone does no harm either way: each row found is read
    /// and compared.
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
    /// whose keys this library does not write, a parent key that is null - the table is read from end to end,
    /// once in the change, and all its rows are held until the change is written: the row's place and the columns
    /// its indexes and relationships name, some 200 bytes a row with numeric keys (about 60 MB and a second for a
    /// table of 300,000 rows). A change that would rewrite a row of a table whose rows this library does not
    /// write as Access does is refused with <see cref="NotSupportedException"/>.
    /// </para>
    /// </summary>
    public bool EnforceForeignKeys { get; set; }

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
        get => _file.ShareLongValuePages;
        set => _file.ShareLongValuePages = value;
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
    public bool AllowAutoNumberInsert { get; set; }

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new blank database at <paramref name="path"/>, in the format of <paramref name="version"/>, and
    /// returns it open and ready for use: an .mdb for <see cref="JetVersion.Jet4"/> (Access 2000-2003), an .accdb
    /// for <see cref="JetVersion.Jet12"/> (Access 2007), <see cref="JetVersion.Jet14"/> (Access 2010),
    /// <see cref="JetVersion.Jet16"/> (Access 2016) and <see cref="JetVersion.Jet17"/> (Access 2019) - the formats
    /// Jackcess creates. The file's name is the caller's: no extension is added or asked for.
    /// <para>
    /// The new file is a copy of an empty database Access's own engine made (<see cref="DatabaseHeader"/>). Tables
    /// made in an Access 2000-2003 or an Access 2007 file sort their text in General - Legacy, as Access's own do
    /// there; in the later formats they sort in General.
    /// </para>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="version"/> is <see cref="JetVersion.Jet3"/>: Access 97 files are read, and neither created
    /// nor written, as in Jackcess. Nothing is created, and a file already at <paramref name="path"/> is left as
    /// it was.
    /// </exception>
    public static Database Create(string path, JetVersion version)
    {
        var format = JetFormat.For(version);
        if (format.IsReadOnly) throw PageFile.ReadOnlyFormat($"'{path}' was not created.");
        var file   = new PageFile(path, format, FileMode.Create);
        var db     = new Database(file);

        DatabaseHeader.Create(format).WriteTo(file);

        return db;
    }

    /// <summary>
    /// Opens an existing .mdb / .accdb file using <paramref name="password"/> to
    /// decrypt password-protected pages.
    ///
    /// For Jet4 (.mdb), the RC4-based MSISAM codec is used — see
    /// <see cref="JetCryptCodecHandler"/> for caveats around verification.
    /// For ACE 12+ (.accdb) the Office Crypto codec is used: <b>Agile
    /// Encryption</b> (Office 2010+, MS-OFFCRYPTO §2.3.4.10–13) and <b>ECMA
    /// Standard Encryption</b> (Office 2007, §2.3.4.5–9). RC4 CryptoAPI and
    /// extensible-provider variants still throw with a clear error — see
    /// <see cref="OfficeCryptCodecHandler"/>.
    /// </summary>
    public static Database Open(string path, string password)
    {
        if (string.IsNullOrEmpty(password)) return Open(path);

        // Sniff version + signature first.
        Span<byte> head = stackalloc byte[0x15];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = fs.Read(head);
            if (read < 0x15)
                throw new InvalidDataException($"File '{path}' is too short to be an Access database.");
        }
        if (!StartsWithSignature(head, "Standard Jet DB")
         && !StartsWithSignature(head, "Standard ACE DB"))
            throw new InvalidDataException(
                $"File '{path}' is not a recognised Access database.");

        JetVersion version = JetFormat.ForVersionCode(head[0x14]).Version;
        return version is JetVersion.Jet3 or JetVersion.Jet4
            ? OpenWithJetCrypt   (path, version, password)
            : OpenWithOfficeCrypt(path, version, password);
    }

    /// <summary>
    /// Opens an .accdb that may be password-protected via Office Agile Encryption.
    /// If the encoding-key slot in the header is zero, the file is unencrypted
    /// and the password is silently ignored. Otherwise the codec is installed
    /// (which itself verifies the password) and a decrypt-on-read database is
    /// returned.
    /// </summary>
    private static Database OpenWithOfficeCrypt(string path, JetVersion version, string password)
    {
        var file = new PageFile(path, JetFormat.For(version), FileMode.Open);
        try
        {
            byte[] header = file.ReadPage(0);
            var codec = OfficeCryptCodecHandler.FromDbHeader(header, password);
            if (codec is not null)
                file.Codec = codec;

            // Whether we installed a codec or not, the system catalog page now
            // has to look like a TDEF. If it doesn't, we got the password wrong
            // (or the file uses an unsupported codec we silently accepted).
            byte[] catalogPage = file.ReadPage(JetFormat.PageSystemCatalog);
            if (catalogPage[0] != JetFormat.PageTypeTableDef || catalogPage[1] != 0x01)
                throw new UnauthorizedAccessException(
                    $"Failed to decrypt '{path}' — wrong password, or the file uses an " +
                    "encryption scheme not yet supported by this codec.");
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new Database(file);
    }

    /// <summary>
    /// Opens a Jet 3/4 (.mdb) file. The password is used only when page 2
    /// doesn't decrypt as a TableDef in cleartext — in that case the file is
    /// taken to be encrypted and <see cref="JetCryptCodecHandler"/> is installed
    /// with the password as the master-key seed. Unencrypted files ignore the
    /// password (no codec is installed).
    /// </summary>
    private static Database OpenWithJetCrypt(string path, JetVersion version, string password)
    {
        JetFormat format = version == JetVersion.Jet3 ? JetFormat.Jet3 : JetFormat.Jet4;
        var file = new PageFile(path, format, FileMode.Open);
        try
        {
            // Try cleartext first — most .mdb files in the wild aren't encrypted.
            byte[] catalogPage = file.ReadPage(JetFormat.PageSystemCatalog);
            bool looksLikeTdef =
                catalogPage[0] == JetFormat.PageTypeTableDef && catalogPage[1] == 0x01;
            if (looksLikeTdef)
                return new Database(file);   // Unencrypted — ignore the password.

            // Page 2 doesn't decode as a TableDef in cleartext → assume encryption,
            // install the codec, retry.
            byte[] dbHeader = file.ReadPage(0);
            var codec = JetCryptCodecHandler.FromDbHeader(dbHeader, password);
            if (codec is null)
                throw new UnauthorizedAccessException(
                    $"'{path}' appears encrypted but no password was supplied " +
                    "(or its verifier block is empty).");
            file.Codec = codec;

            byte[] retry = file.ReadPage(JetFormat.PageSystemCatalog);
            if (retry[0] != JetFormat.PageTypeTableDef || retry[1] != 0x01)
                throw new UnauthorizedAccessException(
                    $"Failed to decrypt '{path}' — wrong password or unsupported encryption scheme.");
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new Database(file);
    }

    /// <summary>
    /// Opens an existing .mdb / .accdb file. An Access 97 file (<see cref="JetVersion.Jet3"/>) is opened for
    /// reading only, as Jackcess opens one: its tables and rows are read, and every change to it - a row
    /// inserted, updated or deleted, a table created or imported - is refused with
    /// <see cref="NotSupportedException"/> before anything is written.
    /// <para>
    /// The file's own header says what format it is in, as Jackcess takes it, and that is the format it is
    /// opened in: <paramref name="version"/> is used only for a file whose header does not say - one without the
    /// signature of an Access database. So an Access 97 file is read as one, and is read-only, whatever
    /// <paramref name="version"/> is given, and a Jet4 file opened as <see cref="JetVersion.Jet3"/> is the Jet4
    /// file it is, read and written as any other.
    /// </para>
    /// </summary>
    public static Database Open(string path, JetVersion version)
    {
        JetFormat format = PageFile.FormatOf(path) ?? JetFormat.For(version);
        var file = new PageFile(path, format, FileMode.Open);
        try
        {
            ThrowIfEncrypted(file, path);
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new Database(file);
    }

    /// <summary>
    /// Sniffs whether the database appears encrypted by inspecting the system
    /// catalog page (page 2). For unencrypted files this page starts with
    /// 0x02 0x01 (TableDef header); encrypted files have those bytes XOR'd
    /// against a key-derived stream and will start with anything else.
    ///
    /// We raise a clear error rather than letting downstream parsers fail with
    /// cryptic messages. Once an <see cref="ICodecHandler"/> implementation lands
    /// (RC4/Office Crypto), this check should install the codec instead of throwing.
    /// </summary>
    private static void ThrowIfEncrypted(PageFile file, string path)
    {
        // File may be smaller than 3 pages (corrupt / not yet populated) — skip
        // the check rather than throw a spurious "encrypted" error.
        if (file.PageCount < 3) return;
        byte[] catalogPage = file.ReadPage(JetFormat.PageSystemCatalog);
        if (catalogPage[0] != JetFormat.PageTypeTableDef || catalogPage[1] != 0x01)
            throw new NotSupportedException(
                $"File '{path}' appears to be encrypted or corrupt — the system catalog " +
                $"page (page 2) does not start with the TableDef header (0x02 0x01). " +
                $"If it is password-protected, open it via Database.Open(path, password).");
    }

    /// <summary>
    /// Opens an existing Access database file, auto-detecting the Jet format
    /// version from the byte at file offset 0x14 (<see cref="JetFormat.ForVersionCode"/>):
    ///   0x00 = Jet3 (Access 97), which is opened for reading only
    ///   0x01 = Jet4 (Access 2000-2003)
    ///   0x02 and later = the .accdb formats, Access 2007 to Access 2019
    /// </summary>
    public static Database Open(string path)
    {
        Span<byte> head = stackalloc byte[0x15];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = fs.Read(head);
            if (read < 0x15)
                throw new InvalidDataException($"File '{path}' is too short to be an Access database.");
        }

        // Validate the database-header signature at offset 0x04:
        //   "Standard Jet DB" — Jet 3/4 (.mdb)
        //   "Standard ACE DB" — ACE 12+ (.accdb)
        // Any other content means the file isn't a recognisable Access database;
        // failing here gives a much clearer error than letting the page parser
        // crash on garbage bytes downstream.
        if (!StartsWithSignature(head, "Standard Jet DB")
         && !StartsWithSignature(head, "Standard ACE DB"))
            throw new InvalidDataException(
                $"File '{path}' is not a recognised Access database (missing 'Standard Jet/ACE DB' signature).");

        return Open(path, JetFormat.ForVersionCode(head[0x14]).Version);
    }

    // ── Foreign keys ─────────────────────────────────────────────────────────

    /// <summary>
    /// What keeps <paramref name="table"/>'s relationships, or null when nothing is to be kept: enforcement is off,
    /// or no relationship with referential integrity has the table on either side.
    /// </summary>
    internal ForeignKeyEnforcer? ForeignKeysFor(Table table)
    {
        if (!EnforceForeignKeys) return null;
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
    /// Returns the names of all user tables in the database (skips MSys* system tables).
    /// </summary>
    public IReadOnlyList<string> ListTables(bool includeSystem = false)
        => _catalog.GetTableNames(includeSystem);

    /// <summary>
    /// Lists the Decimal (Numeric) values in the database's tables - not its system tables - that read with more
    /// digits than their column's precision allows: <see cref="Table.FindDecimalsBeyondPrecision"/> for each
    /// table, where what it finds, and what it cannot, is described. Nothing is changed.
    /// </summary>
    public IEnumerable<DecimalBeyondPrecision> FindDecimalsBeyondPrecision()
        => ListTables().SelectMany(name => GetTable(name).FindDecimalsBeyondPrecision());

    /// <summary>
    /// Reads MSysRelationships and returns the schema's foreign-key / join
    /// relationships, grouping the per-column-pair rows back into one entry
    /// per named relationship. Returns an empty list when MSysRelationships
    /// is absent or empty. They are in the order the table lists them, which is the
    /// order they were made in.
    /// </summary>
    public IReadOnlyList<Relationship> GetRelationships()
    {
        try
        {
            var t = GetTable("MSysRelationships");
            var rows = t.NewCursor().ToList();
            return BuildRelationships(rows);
        }
        catch (InvalidOperationException)
        {
            // Database has no MSysRelationships (rare — empty Access files include it,
            // but defensive in case of corrupt or trimmed files).
            return Array.Empty<Relationship>();
        }
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
    /// Creates a new user table, writes its TDEF and usage-map pages, registers
    /// it in MSysObjects, and returns a <see cref="Table"/> ready for inserts.
    /// </summary>
    /// <param name="name">Table name (must be unique in the database).</param>
    /// <param name="columns">Column definitions (use <see cref="ColumnBuilder"/>).</param>
    /// <param name="primaryKey">
    ///   Optional name of the primary-key column. Its index is kept as rows are
    ///   inserted, deleted and updated.
    /// </param>
    public Table CreateTable(string name, IReadOnlyList<Column> columns, string? primaryKey = null)
        => CreateTableCore(name, columns, primaryKey is null ? null : new[] { primaryKey }, Array.Empty<IndexDefinition>());

    /// <summary>
    /// Composite-PK overload. Creates a table with a multi-column primary key
    /// (up to 10 columns — Jet's hard limit). Pass the column names in the
    /// order they should appear in the index.
    /// </summary>
    public Table CreateTable(string name, IReadOnlyList<Column> columns,
                             IReadOnlyList<string> primaryKeyColumns)
    {
        if (primaryKeyColumns is null || primaryKeyColumns.Count == 0)
            throw new ArgumentException(
                "primaryKeyColumns must contain at least one column.", nameof(primaryKeyColumns));
        return CreateTableCore(name, columns, primaryKeyColumns, Array.Empty<IndexDefinition>());
    }

    /// <summary>
    /// Creates a table with indexes beside its primary key: single- or multi-column, unique or not. Every index is
    /// kept as rows are inserted, updated and deleted, and a unique index refuses a duplicate before anything of
    /// the row is written.
    /// </summary>
    /// <param name="name">Table name (must be unique in the database).</param>
    /// <param name="columns">Column definitions (use <see cref="ColumnBuilder"/>).</param>
    /// <param name="primaryKeyColumns">The primary key's columns, in key order; null or empty for no primary key.</param>
    /// <param name="indexes">The table's other indexes.</param>
    /// <exception cref="NotSupportedException">
    ///   An index is on a column whose keys this library does not write as Access does: a Decimal, Binary, OLE
    ///   or Complex column, or text in a sort order other than General - Legacy and General; or the database is
    ///   an Access 97 file, which is only read. Nothing is created.
    /// </exception>
    public Table CreateTable(string name, IReadOnlyList<Column> columns,
                             IReadOnlyList<string>? primaryKeyColumns,
                             IReadOnlyList<IndexDefinition> indexes)
    {
        if (indexes is null) throw new ArgumentNullException(nameof(indexes));
        return CreateTableCore(name, columns, primaryKeyColumns is { Count: > 0 } ? primaryKeyColumns : null, indexes);
    }

    private Table CreateTableCore(string name, IReadOnlyList<Column> columns,
                                  IReadOnlyList<string>? pkColumns,
                                  IReadOnlyList<IndexDefinition> secondary)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));
        if (columns is null || columns.Count == 0)
            throw new ArgumentException("At least one column is required.", nameof(columns));
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

        // A table with a Large Number or a Date/Time Extended column makes the file one of Access 2016's format
        // or Access 2019's, as it does when Access adds the table: older versions of Access then refuse the file
        // rather than misread it. An .mdb has no such column, and the table is refused here, with no page added.
        if (FormatVersionFor(name, columns, format) is { } version)
            DatabaseHeader.RaiseVersion(_file, version);

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
        _file.WritePage(tdefPage, tdefBytes);

        // 7. Register the table in MSysObjects.
        _catalog.InsertTableEntry(name, tdefPage);

        return new Table(_file, _allocator, tableDef, owningDb: this);
    }

    // A table's index maps follow its owned-pages and free-space maps on its usage-map page.
    private const int FirstIndexUmapRow = 2;

    /// <summary>
    /// The version a file's header must name, at the least, for a table of <paramref name="columns"/> to be in
    /// it: Access 2016's for a Large Number column, Access 2019's for a Date/Time Extended one, and null when
    /// the table has neither. Access gives an .accdb of any earlier format that version when such a column is
    /// added to it, and refuses the column in an .mdb ("Invalid argument"), as this does.
    /// </summary>
    /// <exception cref="NotSupportedException">The file is an .mdb, which has no such column. Nothing is created.</exception>
    private static byte? FormatVersionFor(string table, IReadOnlyList<Column> columns, JetFormat format)
    {
        var newest = columns.Where(c => c.DataType is DataType.BigInt or DataType.ExtendedDateTime)
                            .OrderByDescending(c => c.DataType).FirstOrDefault();
        if (newest is null) return null;
        if (format.Version is JetVersion.Jet3 or JetVersion.Jet4)
            throw new NotSupportedException(
                $"Table '{table}' was not created: its column '{newest.Name}' is a {newest.DataType}, which an .mdb file " +
                "(Access 2000-2003) does not have. Create the database as an .accdb, JetVersion.Jet12 or later.");
        return newest.DataType == DataType.ExtendedDateTime ? DatabaseHeader.Version2019 : DatabaseHeader.Version2016;
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
            columns[i].ColumnNumber = i;

        Column Find(string column, string what)
        {
            var found = columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{what} column '{column}' not found in table '{table}'.");
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
                throw new InvalidOperationException(
                    $"Composite primary keys are limited to {IndexDefinition.MaxColumns} columns; got {pkColumns.Count}.");
            planned.Add(new PlannedIndex(
                TableDefinition.PrimaryKeyIndexName,
                pkColumns.Select(pk => new IndexColumn(Find(pk, "Primary key"), TableDefinition.AscendingColumnFlag)).ToList(),
                TableDefinition.UnknownIndexFlag | TableDefinition.UniqueIndexFlag,
                TableDefinition.PrimaryKeyIndexType));
        }

        foreach (var index in secondary)
        {
            if (index is null) throw new ArgumentException("An index definition must not be null.", nameof(secondary));
            if (planned.Any(p => string.Equals(p.Name, index.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Table '{table}' is given two indexes named '{index.Name}'.", nameof(secondary));
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
    /// Opens an existing user table by name (scans MSysObjects).
    /// </summary>
    public Table GetTable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));

        int tdefPageNum = _catalog.FindTableTdefPage(name);
        if (tdefPageNum < 0)
            throw new InvalidOperationException($"Table '{name}' was not found in this database.");

        var format   = _file.Format;
        var tdefPage = _file.ReadPage(tdefPageNum);
        var info     = TdefReader.Read(tdefPage, format);

        var tableDef = new TableDefinition(name, info.Columns)
        {
            TdefPageNumber = tdefPageNum,
            UmapPageNumber = info.OwnedPagesUmapPage,
            OwnedPagesRow  = info.OwnedPagesUmapRow,
            FreeSpaceRow   = info.FreeSpaceUmapRow,
            Indexes        = info.Indexes,
            HasUnreadIndexes = info.HasUnreadIndexes,
            MaxColumnCount    = info.MaxColumnCount,
            MaxVarColumnCount = info.MaxVarColumnCount,
            HasUnreadColumns  = info.HasUnreadColumns,
        };

        foreach (var kvp in info.LvalColumnUmapPages)
            tableDef.LvalColumnUmapPages[kvp.Key] = kvp.Value;

        // Reattach in-memory PK config from the on-disk metadata so that
        // IndexCursor.FindRowByPrimaryKey works across open/close boundaries.
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

        return new Table(_file, _allocator, tableDef, owningDb: this);
    }

    public void Dispose() => _file.Dispose();
}
