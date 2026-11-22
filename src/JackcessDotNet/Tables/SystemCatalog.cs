using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Reads from and writes to the MSysObjects system catalog (TDEF always at page 2).
///
/// A table is registered the way Access registers one it creates, or Access
/// cannot find it:
///   • a row in MSysObjects: Id = its TDEF page, Name, Type 1, DateCreate and
///     DateUpdate = now, ParentId = the "Tables" container's Id, Flags 0, and
///     Owner = the Admin user (all other columns NULL);
///   • that row's entries in MSysObjects' indexes (Id, and ParentIdName, through
///     which Access finds a table by name);
///   • rows in MSysACEs giving the new table the permissions the Tables container
///     passes on - to its owner too, as Access gives them - with their entries in
///     MSysACEs' index.
/// </summary>
internal sealed class SystemCatalog
{
    private readonly PageFile       _file;
    private readonly JetFormat      _format;
    private readonly PageAllocator  _allocator;
    private readonly DataPageWriter _writer;

    // Well-known MSysObjects column names
    private const string ColId         = "Id";
    private const string ColName       = "Name";
    private const string ColType       = "Type";
    private const string ColDateCreate = "DateCreate";
    private const string ColDateUpdate = "DateUpdate";
    private const string ColParentId   = "ParentId";
    private const string ColFlags      = "Flags";
    private const string ColLvProp     = "LvProp";
    private const string ColOwner      = "Owner";

    // MSysACEs' columns
    private const string AceAcm         = "ACM";
    private const string AceInheritable = "FInheritable";
    private const string AceObjectId    = "ObjectId";
    private const string AceSid         = "SID";

    /// <summary>The ParentId of the top-level containers ("Tables", "Databases", ...).</summary>
    private const int DatabaseParentId = 0x0F000000;
    private const short CatalogTypeContainer = 3;

    /// <summary>
    /// The permissions the owner of a new table is given when its container passes
    /// none on at all: everything Access gives a SID on a table.
    /// </summary>
    private const int TableFullAccess = 0x000FFEFF;

    public SystemCatalog(PageFile file)
    {
        _file      = file   ?? throw new ArgumentNullException(nameof(file));
        _format    = file.Format;
        _allocator = new PageAllocator(file);
        _writer    = new DataPageWriter(file, _allocator);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void CreateBaseCatalog()
    {
        // No-op: the embedded empty-database template already contains MSysObjects.
    }

    /// <summary>
    /// Refuses, before anything is written, a table this library cannot register as
    /// Access would: a name an object in the Tables container already has, in any
    /// case (a table, a query or a linked table: they share one set of names), or a
    /// catalog with an index whose keys it cannot write (names sorted in an order
    /// other than General - Legacy and General, as Access 97 keeps them).
    /// </summary>
    public void EnsureCanRegister(string tableName) => Prepare(tableName);

    /// <summary>
    /// As <see cref="EnsureCanRegister"/>, for the table whose definition is at <paramref name="tdefPage"/> to be
    /// given the name <paramref name="newName"/>: its name is compared with every other object's. The table is left
    /// out by what it is - its row of the catalog, whose id is its definition's page - and not by its name, which
    /// another object's may be equal to without regard to case.
    /// </summary>
    public void EnsureCanRename(int tdefPage, string newName) => Prepare(newName, tdefPage);

    /// <summary>
    /// The order the database's text sorts in, which is the order its catalog keeps names in: ACE gives a new
    /// table's Text and Memo columns that order. Access 97's orders have no Jet4 form, and a catalog with no order
    /// is one an earlier version of this library wrote; both are General - Legacy to a new table.
    /// Jackcess takes the order from the default in the database's header instead. The catalog is used on purpose:
    /// it is the order ACE was seen to give a table it added, the one the new table's name is keyed in whatever the
    /// header says, and reading it needs no unmasking of the header.
    /// </summary>
    internal SortOrder TextSortOrder
    {
        get
        {
            var name = BuildCatalogTableDef().Columns.FirstOrDefault(c => c.Name == ColName);
            return name is null || name.SortOrder.IsAccess97 ? SortOrder.GeneralLegacy : name.SortOrder;
        }
    }

    /// <summary>
    /// How the catalog's names - of tables, queries and relationships - are one to Access (<see cref="ObjectNames"/>):
    /// by the order the catalog keeps them in, Access 97's own in a Jet3 file. In an order this library makes no keys
    /// for, by General - Legacy's: a lookup's best guess, and no name is added to such a catalog.
    /// </summary>
    internal ObjectNames Names => _names ??= NamesOf(BuildCatalogTableDef());

    /// <summary>
    /// The order the catalog keeps names in, as its Name column's definition gives it: Access 97's own in a Jet3 file.
    /// Read once: the catalog's columns do not change.
    /// </summary>
    internal SortOrder NameOrder => _nameOrder ??= NameOrderOf(BuildCatalogTableDef());

    /// <summary>How the names of the database's tables' columns and indexes are one to Access (<see cref="ObjectNames.ForColumns"/>).</summary>
    internal ObjectNames ColumnNames => _columnNames ??= ObjectNames.ForColumns(_format, NameOrder);

    private ObjectNames? _names;
    private SortOrder? _nameOrder;
    private ObjectNames? _columnNames;

    private static SortOrder NameOrderOf(TableDefinition catalogDef)
        => catalogDef.Columns.FirstOrDefault(c => c.Name == ColName)?.SortOrder ?? SortOrder.GeneralLegacy;

    private static ObjectNames NamesOf(TableDefinition catalogDef) => ObjectNames.ForCatalog(NameOrderOf(catalogDef)) ?? ObjectNames.GeneralLegacy;

    /// <summary>
    /// Registers a new user table: its MSysObjects row and index entries, and its
    /// permissions in MSysACEs (see the class summary). Refuses what
    /// <see cref="EnsureCanRegister"/> refuses, before writing anything.
    /// </summary>
    /// <param name="tableName">The table's name.</param>
    /// <param name="tdefPageNumber">The table's definition page, which is its id in the catalog.</param>
    /// <param name="properties">
    /// The table's properties and its columns', as <see cref="PropertyMapWriter"/> encodes them: the row's
    /// LvProp. Null for a table with none.
    /// </param>
    public void InsertTableEntry(string tableName, int tdefPageNumber, byte[]? properties = null)
    {
        var (catalogDef, tablesId, owner, acesDef) = Prepare(tableName);

        var now = DateTime.Now;
        var row = new Row
        {
            [ColId]         = tdefPageNumber,
            [ColName]       = tableName,
            [ColType]       = (short)JetFormat.CatalogTypeTable,
            [ColDateCreate] = now,
            [ColDateUpdate] = now,
            [ColParentId]   = tablesId,
            [ColFlags]      = 0
        };
        if (owner is not null) row[ColOwner] = owner;
        if (properties is not null) row[ColLvProp] = properties;

        int rowPtr = _writer.InsertRow(catalogDef, row);
        _writer.IncrementTdefRowCount(JetFormat.PageSystemCatalog);
        AddIndexEntries(catalogDef, row, rowPtr);

        if (acesDef is not null)
            InsertAccessControlEntries(acesDef, tablesId, tdefPageNumber, owner, CreatorOwner(ReadRows(catalogDef, ColName, ColType, ColOwner)));
    }

    /// <summary>
    /// Gives the new object <paramref name="objectId"/> the permissions its container
    /// passes on, as Access was seen to give them to a table and to a relationship it
    /// creates, in files Access made and in this library's own (#37): an ACE, not
    /// inheritable, for each SID that an inheritable ACE of the container names, with
    /// that ACE's ACM - and where the container's ACE names the creator of whatever
    /// is made in it (<paramref name="creatorOwner"/>), the ACE is the new object's
    /// owner's. A SID named twice has one ACE, with the permissions of both. A
    /// container that passes nothing on gives the owner everything.
    /// <para>
    /// The owner's ACE is what lets Access change the object in an .mdb: without it,
    /// DAO refuses to read a table this library made in a database DAO created, or to
    /// drop a relationship it made ("no delete permission").
    /// </para>
    /// </summary>
    private void InsertAccessControlEntries(TableDefinition acesDef, int tablesId, int objectId, byte[]? owner, byte[]? creatorOwner)
    {
        var granted = new List<(int Acm, byte[] Sid)>();
        foreach (var ace in ReadRows(acesDef, AceAcm, AceInheritable, AceObjectId, AceSid))
        {
            if (Value(ace, AceObjectId) is not int id || id != tablesId || Value(ace, AceInheritable) is not true
                || Value(ace, AceAcm) is not int acm || Value(ace, AceSid) is not byte[] sid)
                continue;
            if (owner is not null && creatorOwner is not null && sid.AsSpan().SequenceEqual(creatorOwner)) sid = owner;
            int at = granted.FindIndex(g => g.Sid.AsSpan().SequenceEqual(sid));
            if (at < 0) granted.Add((acm, sid));
            else granted[at] = (granted[at].Acm | acm, sid);
        }
        if (granted.Count == 0 && owner is not null)
            granted.Add((TableFullAccess, owner));

        foreach (var (acm, sid) in granted)
        {
            var ace = new Row
            {
                [AceAcm]         = acm,
                [AceInheritable] = false,
                [AceObjectId]    = objectId,
                [AceSid]         = sid,
            };
            int rowPtr = _writer.InsertRow(acesDef, ace);
            _writer.IncrementTdefRowCount(acesDef.TdefPageNumber);
            AddIndexEntries(acesDef, ace, rowPtr);
        }
    }

    // ── Properties ───────────────────────────────────────────────────────────

    /// <summary>A row of the catalog, as what keeps properties in it needs it: where it is, and its LvProp.</summary>
    internal readonly record struct ObjectRow(int RowPointer, byte[]? Properties);

    /// <summary>The catalog's row for the object whose id is <paramref name="objectId"/> - a table's is its definition page - or null.</summary>
    internal ObjectRow? FindObject(int objectId)
        => FindRow((id, _, _, _) => id == objectId) is { } row ? new ObjectRow(row.RowPointer, row.ReadProperties()) : null;

    /// <summary>
    /// The catalog's row for the document <paramref name="name"/> of the Databases container, where Access keeps
    /// a database's properties: "MSysDb" its own, "SummaryInfo" and "UserDefined" those of its two property
    /// sheets. Null when the database has no such document.
    /// </summary>
    internal ObjectRow? FindDatabaseObject(string name)
    {
        if (FindRow((_, objectName, parent, type) => type == CatalogTypeContainer && parent == DatabaseParentId && objectName == "Databases")
            is not { } databases) return null;
        return FindRow((_, objectName, parent, _) => parent == databases.Id && string.Equals(objectName, name, StringComparison.OrdinalIgnoreCase))
            is { } row ? new ObjectRow(row.RowPointer, row.ReadProperties()) : null;
    }

    /// <summary>
    /// As <see cref="FindTable(string, out string?)"/>, with what reads the table's properties - the LvProp of
    /// its row - in <paramref name="readProperties"/>: from the row as this pass read it, so reading them reads
    /// no page again, and only the pages of a value too long for its row. The scan stops at the table: the table of
    /// exactly that name, and when there is none, a second scan for one whose name Access takes as one with it
    /// (<see cref="ObjectNames.Find"/>). <paramref name="byAnEarlierVersion"/> says whether an earlier version of this
    /// library created the table, whose text is then read by its bytes (<see cref="Column.ReadsTextByContent"/>).
    /// </summary>
    internal int FindTable(string tableName, out string? listedName, out Func<byte[]?>? readProperties, out bool byAnEarlierVersion)
    {
        // A name given exactly is its table's, though another's is one with it to Access: a file can hold both.
        var row = FindRow((_, name, _, type) => type == JetFormat.CatalogTypeTable && name == tableName)
               ?? FindByRule(tableName);
        listedName         = row?.Name;
        readProperties     = row?.ReadProperties;
        byAnEarlierVersion = row?.ByAnEarlierVersion ?? false;
        return row?.Id ?? -1;
    }

    /// <summary>Whether an earlier version of this library created the table whose definition is on <paramref name="tdefPage"/>.</summary>
    internal bool IsAnEarlierVersionsTable(int tdefPage)
        => FindRow((id, _, _, type) => id == tdefPage && type == JetFormat.CatalogTypeTable) is { ByAnEarlierVersion: true };

    // The table whose name Access takes as one with tableName, for a name no table has exactly: the first by the
    // catalog's index key, and then the first at all (ObjectNames.Find).
    private CatalogRow? FindByRule(string tableName)
    {
        var names = Names;
        var key   = names.KeyOf(tableName);
        string? firstOneWith = null;
        return FindRow((_, name, _, type) =>
               {
                   if (type != JetFormat.CatalogTypeTable || name is null) return false;
                   var other = names.KeyOf(name);
                   if (key.HasIndexKeyOf(other)) return true;
                   if (firstOneWith is null && key.IsOneWith(other)) firstOneWith = name;
                   return false;
               })
            ?? (firstOneWith is null ? null : FindRow((_, name, _, type) => type == JetFormat.CatalogTypeTable && name == firstOneWith));
    }

    // A row of the catalog as a scan found it; ReadProperties reads its LvProp from the bytes the scan read.
    // ByAnEarlierVersion: a table an earlier version of this library created (IsAnEarlierVersionsTable).
    private readonly record struct CatalogRow(int Id, string? Name, int RowPointer, Func<byte[]?> ReadProperties, bool ByAnEarlierVersion = false);

    /// <summary>
    /// Whether the catalog row is a table's that an earlier version of this library created: ParentId 0 and no Owner,
    /// where Access gives a table the Tables container's Id and an owner, and no row Access wrote has ParentId 0. Such a
    /// version - the JackcessDotNet packages to 1.2.2 and this repository's builds before 442bbf8 - wrote the name, and
    /// every value of characters 0x01 to 0xFF, compressed in a column without Unicode compression, and Access neither
    /// lists nor reads the table; the name and the table's text are read by their bytes (#93).
    /// </summary>
    internal static bool IsAnEarlierVersionsTable(RowDecoder decoder, byte[] rowBytes, Column? colType, Column? colParent, Column? colOwner)
        => colType is not null && colParent is not null && colOwner is not null
           && decoder.Decode(rowBytes, colType) is short type && type == JetFormat.CatalogTypeTable
           && decoder.Decode(rowBytes, colParent) is int parent && parent == 0
           && decoder.Decode(rowBytes, colOwner) is null;

    /// <summary>
    /// <see cref="IsAnEarlierVersionsTable(RowDecoder, byte[], Column?, Column?, Column?)"/>, with the catalog's Type,
    /// ParentId and Owner found among <paramref name="columns"/>, the catalog's.
    /// </summary>
    internal static bool IsAnEarlierVersionsTable(RowDecoder decoder, byte[] rowBytes, IReadOnlyList<Column> columns)
        => IsAnEarlierVersionsTable(decoder, rowBytes,
               columns.FirstOrDefault(c => c.Name.Equals(ColType,     StringComparison.OrdinalIgnoreCase)),
               columns.FirstOrDefault(c => c.Name.Equals(ColParentId, StringComparison.OrdinalIgnoreCase)),
               columns.FirstOrDefault(c => c.Name.Equals(ColOwner,    StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// <see cref="IsAnEarlierVersionsTable(RowDecoder, byte[], Column?, Column?, Column?)"/> of a catalog row as values
    /// by column name: the row a change of the catalog writes, whose Name is then written as it is read, by its bytes.
    /// </summary>
    internal static bool IsAnEarlierVersionsTable(Row row)
        => row.TryGetValue(ColType, out var type) && type is short t && t == JetFormat.CatalogTypeTable
           && row.TryGetValue(ColParentId, out var parent) && parent is int p && p == 0
           && (!row.TryGetValue(ColOwner, out var owner) || RowValues.IsNull(owner));

    /// <summary>Whether <paramref name="column"/>, one of the catalog's, is its Name.</summary>
    internal static bool IsNameColumn(Column column) => column.Name.Equals(ColName, StringComparison.OrdinalIgnoreCase);

    // The catalog's name of an object, read by its bytes for a table an earlier version created.
    private static string? NameOf(RowDecoder decoder, byte[] rowBytes, Column colName, bool byAnEarlierVersion)
        => (byAnEarlierVersion ? decoder.DecodeByContent(rowBytes, colName) : decoder.Decode(rowBytes, colName)) as string;

    // The first row of the catalog whose id, name, parent's id and type match.
    private CatalogRow? FindRow(Func<int, string?, int?, short?, bool> match)
    {
        var catalogDef = BuildCatalogTableDef();
        var columns    = catalogDef.Columns;
        Column? colId     = columns.FirstOrDefault(c => c.Name.Equals(ColId,       StringComparison.OrdinalIgnoreCase));
        Column? colName   = columns.FirstOrDefault(c => c.Name.Equals(ColName,     StringComparison.OrdinalIgnoreCase));
        Column? colParent = columns.FirstOrDefault(c => c.Name.Equals(ColParentId, StringComparison.OrdinalIgnoreCase));
        Column? colType   = columns.FirstOrDefault(c => c.Name.Equals(ColType,     StringComparison.OrdinalIgnoreCase));
        Column? colOwner  = columns.FirstOrDefault(c => c.Name.Equals(ColOwner,    StringComparison.OrdinalIgnoreCase));
        Column? colLvProp = columns.FirstOrDefault(c => c.Name.Equals(ColLvProp,   StringComparison.OrdinalIgnoreCase));
        if (colId is null || colName is null || colParent is null || colType is null) return null;

        var decoder = new RowDecoder(_format, columns);   // no LvalReader: the LvProp value is read raw

        byte[] umapPage = _file.ReadPage(catalogDef.UmapPageNumber);
        foreach (int pageNum in UsageMap.GetOwnedPages(umapPage, catalogDef.OwnedPagesRow, _format, _file))
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);
            for (int r = 0; r < rowCount; r++)
            {
                byte[]? rowBytes = ReadRowBytes(dp, r, _format);
                if (rowBytes is null) continue;
                if (decoder.Decode(rowBytes, colId) is not int id) continue;
                bool byAnEarlierVersion = IsAnEarlierVersionsTable(decoder, rowBytes, colType, colParent, colOwner);
                string? name = NameOf(decoder, rowBytes, colName, byAnEarlierVersion);
                if (!match(id, name, decoder.Decode(rowBytes, colParent) as int?, decoder.Decode(rowBytes, colType) as short?)) continue;
                return new CatalogRow(id, name, RowPointer.Pack(pageNum, r),
                    () => colLvProp is null ? null : ReadVariableColumnRawBytes(rowBytes, colLvProp, columns, new LvalReader(_file)),
                    byAnEarlierVersion);
            }
        }
        return null;
    }

    // ── Relationships ────────────────────────────────────────────────────────

    private const short CatalogTypeRelationship = 8;

    /// <summary>What registering a relationship needs, read and checked before anything is written.</summary>
    internal sealed class RelationshipEntry
    {
        internal RelationshipEntry(TableDefinition catalog, int containerId, int objectId, byte[]? owner, TableDefinition? aces)
        {
            Catalog     = catalog;
            ContainerId = containerId;
            ObjectId    = objectId;
            Owner       = owner;
            Aces        = aces;
        }

        internal TableDefinition  Catalog     { get; }
        internal int              ContainerId { get; }
        internal int              ObjectId    { get; }
        internal byte[]?          Owner       { get; }
        internal TableDefinition? Aces        { get; }
    }

    /// <summary>The names of the relationships the catalog lists: the objects in its Relationships container.</summary>
    internal IReadOnlyList<string> RelationshipNames()
        => ReadRows(BuildCatalogTableDef(), ColName, ColType)
            .Where(r => Value(r, ColType) is short type && type == CatalogTypeRelationship)
            .Select(r => Value(r, ColName) as string)
            .OfType<string>()
            .ToList();

    /// <summary>
    /// Reads what a relationship named <paramref name="name"/> is registered with, and refuses, before anything
    /// is written, one this library cannot register as Access would: a database with no Relationships container,
    /// a name a relationship already has, in any case, or one no catalog key is made for, or a catalog with an
    /// index whose keys it cannot write. A relationship is an object of the catalog like a table: Access gives it
    /// an id of its own, counted up from the lowest number an id can be - Jackcess's "synthetic" ids - since it
    /// has no definition page to be known by.
    /// </summary>
    internal RelationshipEntry PrepareRelationship(string name)
    {
        var catalogDef = BuildCatalogTableDef();
        var catalog    = ReadRows(catalogDef, ColId, ColName, ColType, ColParentId, ColOwner);

        var container = catalog.FirstOrDefault(r =>
                Value(r, ColType) is short type && type == CatalogTypeContainer
                && Value(r, ColParentId) is int parent && parent == DatabaseParentId
                && Value(r, ColName) is string containerName && containerName == "Relationships")
            ?? throw new NotSupportedException("The database has no Relationships container to register a relationship in. Nothing was changed.");
        int containerId = (int)Value(container, ColId)!;

        var names = NamesOf(catalogDef);
        var key   = names.KeyOf(name);
        if (catalog.FirstOrDefault(r => Value(r, ColType) is short type && type == CatalogTypeRelationship
                                     && Value(r, ColName) is string existing
                                     && (existing == name || key.IsOneWith(names.KeyOf(existing))))
            is { } taken)
            throw new InvalidOperationException(
                $"There is already a relationship named {ObjectNames.Taken((string)Value(taken, ColName)!, name)} in this database. Nothing was changed.");

        // The ids Access gives objects that have no page count up from int.MinValue: the next is one past the
        // highest negative id there is.
        var negative = catalog.Select(r => Value(r, ColId)).OfType<int>().Where(id => id < 0).ToList();
        int objectId = negative.Count == 0 ? int.MinValue : negative.Max() + 1;
        if (objectId >= 0)
            throw new InvalidOperationException("The database has no id left for another relationship. Nothing was changed.");

        int acesTdef = FindTableTdefPage("MSysACEs");
        var acesDef  = acesTdef >= 0 ? BuildTableDef("MSysACEs", acesTdef) : null;

        EnsureKeysCanBeWritten(catalogDef);
        var nameColumn = catalogDef.Columns.First(c => c.Name == ColName);
        if (WhyNotNamed(nameColumn, name) is { } why)
            throw new NotSupportedException($"A relationship cannot be named '{name}': {why}. Nothing was changed.");
        if (acesDef is not null) EnsureKeysCanBeWritten(acesDef);

        return new RelationshipEntry(catalogDef, containerId, objectId, AdminOwner(catalog), acesDef);
    }

    /// <summary>
    /// Registers a relationship as Access registers one it creates: a row in MSysObjects - its id, its name,
    /// Type 8, the dates, ParentId the Relationships container, Flags 0 and the Admin user as owner - with its
    /// entries in MSysObjects' indexes, and the permissions the container passes on, in MSysACEs.
    /// </summary>
    internal void InsertRelationshipEntry(RelationshipEntry entry, string name)
    {
        var now = DateTime.Now;
        var row = new Row
        {
            [ColId]         = entry.ObjectId,
            [ColName]       = name,
            [ColType]       = CatalogTypeRelationship,
            [ColDateCreate] = now,
            [ColDateUpdate] = now,
            [ColParentId]   = entry.ContainerId,
            [ColFlags]      = 0
        };
        if (entry.Owner is not null) row[ColOwner] = entry.Owner;

        int rowPtr = _writer.InsertRow(entry.Catalog, row);
        _writer.IncrementTdefRowCount(JetFormat.PageSystemCatalog);
        AddIndexEntries(entry.Catalog, row, rowPtr);

        if (entry.Aces is not null)
            InsertAccessControlEntries(entry.Aces, entry.ContainerId, entry.ObjectId, entry.Owner,
                                       CreatorOwner(ReadRows(entry.Catalog, ColName, ColType, ColOwner)));
    }

    /// <summary>
    /// The name the catalog has for table <paramref name="name"/>, as it spells it - the table of exactly that name, or
    /// else of a name Access takes as one with it - or null when there is no such table.
    /// </summary>
    internal string? TableNameAsListed(string name)
    {
        var tables = GetTableNames(includeSystem: true);
        int at = Names.Find(tables, name);
        return at < 0 ? null : tables[at];
    }

    /// <summary>
    /// The Admin user's SID, which Access makes a new table's owner. A file keeps its
    /// SIDs encoded with a key of its own; the Admin user's is the system objects'
    /// owner with 0x0102 mixed in, in every Access-made table of the test corpus
    /// (Access 97 to 2019). Null when MSysObjects' own owner is not a 2-byte SID.
    /// </summary>
    private static byte[]? AdminOwner(IEnumerable<Row> catalog)
    {
        var system = catalog.FirstOrDefault(r =>
            Value(r, ColName) is string name && name == "MSysObjects"
            && Value(r, ColType) is short type && type == JetFormat.CatalogTypeTable);
        return system is not null && Value(system, ColOwner) is byte[] { Length: 2 } sid
            ? new[] { (byte)(sid[0] ^ 0x01), (byte)(sid[1] ^ 0x02) }
            : null;
    }

    /// <summary>
    /// The SID a container's ACE names for "whoever creates an object here": the system
    /// objects' owner with 0x0007 mixed in, in every file looked at - made by DAO and
    /// from this library's templates, .mdb and .accdb - as the Admin user's is that
    /// owner with 0x0102 (<see cref="AdminOwner"/>). Null when MSysObjects' own owner
    /// is not a 2-byte SID.
    /// </summary>
    private static byte[]? CreatorOwner(IEnumerable<Row> catalog)
    {
        var system = catalog.FirstOrDefault(r =>
            Value(r, ColName) is string name && name == "MSysObjects"
            && Value(r, ColType) is short type && type == JetFormat.CatalogTypeTable);
        return system is not null && Value(system, ColOwner) is byte[] { Length: 2 } sid
            ? new[] { sid[0], (byte)(sid[1] ^ 0x07) }
            : null;
    }

    private static object? Value(Row row, string column) => row.TryGetValue(column, out var value) ? value : null;

    // What registering a table needs, read and checked before anything is written; for a table being renamed, the row
    // whose id is renamedTdef is that table's, and its name is no other object's.
    private (TableDefinition Catalog, int TablesId, byte[]? Owner, TableDefinition? Aces) Prepare(string tableName, int? renamedTdef = null)
    {
        var catalogDef = BuildCatalogTableDef();
        var catalog    = ReadRows(catalogDef, ColId, ColName, ColType, ColParentId, ColOwner);

        var tables = catalog.FirstOrDefault(r =>
                Value(r, ColType) is short type && type == CatalogTypeContainer
                && Value(r, ColParentId) is int parent && parent == DatabaseParentId
                && Value(r, ColName) is string name && name == "Tables")
            ?? throw new InvalidDataException("MSysObjects has no Tables container to put a table in.");
        int tablesId = (int)Value(tables, ColId)!;

        // A table's name is any object's in the Tables container, and any table's: versions of this
        // library before 2.1.1 registered tables with ParentId 0, outside the container. Names are one as Access
        // takes them (ObjectNames).
        var names = NamesOf(catalogDef);
        var key   = names.KeyOf(tableName);
        if (catalog.FirstOrDefault(r => Value(r, ColName) is string name
                                     && !(renamedTdef is int renamed && Value(r, ColId) is int id && id == renamed
                                          && Value(r, ColType) is short renamedType && renamedType == JetFormat.CatalogTypeTable)
                                     && (Value(r, ColParentId) is int parent && parent == tablesId
                                         || Value(r, ColType) is short type && type == JetFormat.CatalogTypeTable)
                                     && (name == tableName || key.IsOneWith(names.KeyOf(name))))
            is { } taken)
            throw new InvalidOperationException(
                $"An object named {ObjectNames.Taken((string)Value(taken, ColName)!, tableName)} already exists in this database.");

        int acesTdef = FindTableTdefPage("MSysACEs");
        var acesDef  = acesTdef >= 0 ? BuildTableDef("MSysACEs", acesTdef) : null;

        // Every index must be one this library can write a key for, the name's among them.
        EnsureKeysCanBeWritten(catalogDef);
        var nameColumn = catalogDef.Columns.First(c => c.Name == ColName);
        if (WhyNotNamed(nameColumn, tableName) is { } why)
            throw new NotSupportedException($"A table cannot be named '{tableName}': {why}.");
        if (acesDef is not null) EnsureKeysCanBeWritten(acesDef);

        return (catalogDef, tablesId, AdminOwner(catalog), acesDef);
    }

    /// <summary>
    /// Why an object of the catalog cannot be given <paramref name="name"/>, or null: a name whose key in the catalog's
    /// index is not made, or one with a mark Access keys by the characters around it (ー, 々, the shadda). General -
    /// Legacy's key of such a value is made, but a name with one was not put before Access, which compares such names
    /// by readings this library takes no key of (<see cref="ObjectNames"/>).
    /// </summary>
    private static string? WhyNotNamed(Column nameColumn, string name)
    {
        if (!IndexKeys.CanEncode(nameColumn, name)) return IndexKeys.WhyNot(nameColumn, name);
        if (TextIndexCodes.For(nameColumn.SortOrder) is { } codes)
            foreach (char c in name)
                if (codes.IsContextDependent(c))
                    return $"Access keys U+{(int)c:X4} by the characters around it, and this library makes no name with such a character";
        return null;
    }

    private static void EnsureKeysCanBeWritten(TableDefinition def)
    {
        foreach (var index in def.Indexes)
            foreach (var column in index.Columns)
                if (!IndexKeys.CanEncode(column.Column))
                    throw new NotSupportedException(
                        $"{def.Name}'s index {index.Name} cannot be kept: {IndexKeys.WhyNot(column.Column)}.");
    }

    /// <summary>
    /// Adds the row's entry to each of the table's indexes: once per index block, as
    /// two logical indexes can share one.
    /// </summary>
    private void AddIndexEntries(TableDefinition def, Row row, int rowPtr)
    {
        var writer = new IndexWriter(_file, _allocator);
        foreach (var index in def.Indexes.GroupBy(ix => ix.IndexDataNumber).Select(g => g.First()))
        {
            var values = index.Columns.Select(c => Value(row, c.Column.Name)).ToArray();
            writer.Insert(IndexWriter.Target.For(def.TdefPageNumber, index), IndexKeys.Encode(index.Columns, values), rowPtr);
        }
    }

    /// <summary>The <paramref name="columns"/> of every row of the table <paramref name="def"/> describes.</summary>
    private List<Row> ReadRows(TableDefinition def, params string[] columns)
    {
        var wanted  = def.Columns.Where(c => columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var decoder = new RowDecoder(_format, def.Columns);
        var rows    = new List<Row>();
        // The catalog's own rows: a table an earlier version created has its name read by its bytes.
        bool isCatalog    = def.TdefPageNumber == JetFormat.PageSystemCatalog;
        Column? colType   = def.Columns.FirstOrDefault(c => c.Name.Equals(ColType,     StringComparison.OrdinalIgnoreCase));
        Column? colParent = def.Columns.FirstOrDefault(c => c.Name.Equals(ColParentId, StringComparison.OrdinalIgnoreCase));
        Column? colOwner  = def.Columns.FirstOrDefault(c => c.Name.Equals(ColOwner,    StringComparison.OrdinalIgnoreCase));

        byte[] umapPage = _file.ReadPage(def.UmapPageNumber);
        foreach (int pageNum in UsageMap.GetOwnedPages(umapPage, def.OwnedPagesRow, _format, _file))
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);
            for (int r = 0; r < rowCount; r++)
            {
                byte[]? rowBytes = ReadRowBytes(dp, r, _format);
                if (rowBytes is null) continue;
                var row = new Row();
                bool byAnEarlierVersion = isCatalog && IsAnEarlierVersionsTable(decoder, rowBytes, colType, colParent, colOwner);
                foreach (var column in wanted)
                    row.Put(column.Name, byAnEarlierVersion && column.Name.Equals(ColName, StringComparison.OrdinalIgnoreCase)
                        ? decoder.DecodeByContent(rowBytes, column)
                        : decoder.Decode(rowBytes, column));
                rows.Add(row);
            }
        }
        return rows;
    }

    // Flags stored on each MSysObjects row; matches Jackcess constants.
    private const int SystemObjectFlag    = unchecked((int)0x80000000);
    private const int AltSystemObjectFlag = 0x02;

    /// <summary>
    /// Loads the property bytes ("LvProp" column) for the MSysObjects row whose
    /// Id equals <paramref name="objectTdefPage"/>. Returns null for any row that
    /// has a null/empty LvProp value (most tables have none). The result is the
    /// raw blob; <see cref="PropertyMapReader"/> turns it into a <see cref="PropertyMaps"/>.
    /// </summary>
    public byte[]? GetPropertyBytesForObject(int objectTdefPage) => FindObject(objectTdefPage)?.Properties;

    /// <summary>
    /// Locates <paramref name="targetCol"/>'s slice within <paramref name="rowBytes"/>'
    /// variable-length area and, if it's a long-value (Memo/OLE) column with an
    /// OTHER_PAGE LvRef, follows the chain to assemble the full byte array.
    /// </summary>
    private byte[]? ReadVariableColumnRawBytes(
        byte[] rowBytes, Column targetCol,
        IReadOnlyList<Column> allColumns, LvalReader lvalReader)
    {
        if (!targetCol.DataType.IsVariableLength()) return null;
        if (rowBytes.Length < _format.SizeRowColumnCount) return null;

        int colCount = _format.SizeRowColumnCount == 2
            ? ByteUtil.GetShort(rowBytes, 0)
            : rowBytes[0];

        int maskSize   = (colCount + 7) / 8;
        int maskOffset = rowBytes.Length - maskSize;
        if (maskOffset < _format.SizeRowColumnCount) return null;

        bool notNull = (rowBytes[maskOffset + targetCol.Ordinal / 8]
                        & (1 << (targetCol.Ordinal % 8))) != 0;
        if (!notNull) return null;

        // Determine this column's var-index. Either trust the on-disk TDEF value
        // or compute by enumerating variable-length columns in column-number order.
        int varIdx = targetCol.VarLenTableIndex >= 0
            ? targetCol.VarLenTableIndex
            : CountPrecedingVarColumns(targetCol, allColumns);

        int sz = _format.SizeRowVarColOffset;
        int varCountOffset = maskOffset - sz;
        if (varCountOffset < 0) return null;
        int storedVarCount = sz == 2
            ? ByteUtil.GetShort(rowBytes, varCountOffset)
            : rowBytes[varCountOffset];
        if (varIdx >= storedVarCount) return null;

        int varStartOffset = maskOffset - (varIdx + 2) * sz;
        if (varStartOffset < 0) return null;
        int varStart = sz == 2 ? ByteUtil.GetShort(rowBytes, varStartOffset) : rowBytes[varStartOffset];
        int varEnd;
        if (varIdx + 1 < storedVarCount)
        {
            int nextOffset = maskOffset - (varIdx + 1 + 2) * sz;
            varEnd = sz == 2 ? ByteUtil.GetShort(rowBytes, nextOffset) : rowBytes[nextOffset];
        }
        else
        {
            int eodOffset = maskOffset - (storedVarCount + 2) * sz;
            if (eodOffset < 0) return null;
            varEnd = sz == 2 ? ByteUtil.GetShort(rowBytes, eodOffset) : rowBytes[eodOffset];
        }

        int varLen = varEnd - varStart;
        if (varLen < 4 || varStart < 0 || varEnd > maskOffset) return null;

        // Access LvRef header (12 bytes total in the row's var area, per Jackcess):
        //   bytes 0..3 = lengthWithFlags (LE int): high byte = type, low 24 bits = length
        //   THIS_PAGE   (type 0x80): bytes 4..11 reserved, then inline data at byte 12
        //   OTHER_PAGE  (type 0x40): byte 4 = rowNum, bytes 5..7 = page (LE 3-byte)
        //   OTHER_PAGES (type 0x00): same first 4 bytes, but chunks chain across pages
        if (varLen < 4) return null;
        int lenWithFlags = ByteUtil.GetInt(rowBytes, varStart);
        byte typeFlag   = (byte)((uint)lenWithFlags >> 24);
        int  dataLen    = lenWithFlags & 0x00FFFFFF;
        if (typeFlag == 0x80)
        {
            // Inline data starts 12 bytes into the LvRef.
            int avail = varLen - 12;
            if (avail <= 0) return Array.Empty<byte>();
            int actualLen = Math.Min(dataLen, avail);
            var inline = new byte[actualLen];
            Array.Copy(rowBytes, varStart + 12, inline, 0, actualLen);
            return inline;
        }
        if ((typeFlag == 0x40 || typeFlag == 0x00) && varLen >= 8)
        {
            int lvalRow  = rowBytes[varStart + 4];
            int lvalPage = rowBytes[varStart + 5]
                         | (rowBytes[varStart + 6] << 8)
                         | (rowBytes[varStart + 7] << 16);
            return typeFlag == 0x40
                ? ReadAccessLvalSingleChunk(lvalPage, lvalRow, dataLen)
                : ReadAccessLvalChain      (lvalPage, lvalRow, dataLen);
        }
        return null;
    }

    /// <summary>
    /// Single-chunk OTHER_PAGE read: the entire value lives in one row on an LVAL page.
    /// Row layout: chunk data starts at the row's first byte and runs to rowEnd.
    /// </summary>
    private byte[] ReadAccessLvalSingleChunk(int pageNum, int rowNum, int dataLen)
    {
        byte[] page = _file.ReadPage(pageNum);
        int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
        if (rowNum >= rowCount) return Array.Empty<byte>();

        int slotOff  = _format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = (rowNum == 0)
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowNum - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        int rowLen = rowEnd - rowStart;
        if (rowLen <= 0) return Array.Empty<byte>();

        int copyLen = Math.Min(dataLen, rowLen);
        var result = new byte[copyLen];
        Array.Copy(page, rowStart, result, 0, copyLen);
        return result;
    }

    /// <summary>
    /// Chunk-chain OTHER_PAGES read: each chunk row begins with a 4-byte
    /// (1-byte nextRow + 3-byte LE nextPage) header, followed by data bytes to rowEnd.
    /// </summary>
    private byte[] ReadAccessLvalChain(int pageNum, int rowNum, int dataLen)
    {
        var result = new byte[dataLen];
        int written = 0;

        while (written < dataLen)
        {
            byte[] page = _file.ReadPage(pageNum);
            int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
            if (rowNum >= rowCount) break;

            int slotOff  = _format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry;
            int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
            int rowEnd   = (rowNum == 0)
                ? _format.PageSize
                : (ByteUtil.GetUShort(page,
                       _format.OffsetDataRowTable + (rowNum - 1) * JetFormat.SizeRowEntry)
                   & JetFormat.RowOffsetMask);
            int rowLen = rowEnd - rowStart;
            if (rowLen < 4) break;

            // Header at the start of the chunk row: nextRow + nextPage(3LE)
            int nextRow  = page[rowStart];
            int nextPage = page[rowStart + 1]
                         | (page[rowStart + 2] << 8)
                         | (page[rowStart + 3] << 16);

            int chunkLen = rowLen - 4;
            int copyLen  = Math.Min(chunkLen, dataLen - written);
            Array.Copy(page, rowStart + 4, result, written, copyLen);
            written += copyLen;

            if (nextPage == 0 || (nextPage == 0 && nextRow == 0)) break;
            pageNum = nextPage;
            rowNum  = nextRow;
        }
        return result;
    }

    private static int CountPrecedingVarColumns(Column target, IReadOnlyList<Column> all)
    {
        int idx = 0;
        foreach (var c in all)
        {
            if (!c.DataType.IsVariableLength()) continue;
            if (c.Ordinal == target.Ordinal) return idx;
            idx++;
        }
        return idx;
    }

    /// <summary>
    /// Returns the names of all user tables (or, with <paramref name="includeSystem"/>,
    /// all tables including MSys*). Linked tables are not yet distinguished.
    /// </summary>
    public IReadOnlyList<string> GetTableNames(bool includeSystem)
    {
        var result = new List<string>();
        ForEachTableEntry((name, _, flags) =>
        {
            bool isSystem = (flags & (SystemObjectFlag | AltSystemObjectFlag)) != 0;
            if (isSystem && !includeSystem) return;
            result.Add(name);
        });
        return result;
    }

    /// <summary>The names of the system tables (MSys* and the others flagged so), in the order the catalog lists them.</summary>
    public IReadOnlyList<string> GetSystemTableNames()
    {
        var result = new List<string>();
        ForEachTableEntry((name, _, flags) =>
        {
            if ((flags & (SystemObjectFlag | AltSystemObjectFlag)) != 0) result.Add(name);
        });
        return result;
    }

    /// <summary>
    /// Scans MSysObjects data pages and returns the TDEF page number for
    /// <paramref name="tableName"/>, or -1 if not found.
    /// </summary>
    public int FindTableTdefPage(string tableName) => FindTable(tableName, out _);

    /// <summary>
    /// As <see cref="FindTableTdefPage"/>, with the table's name as the catalog spells it in
    /// <paramref name="listedName"/> (null when there is no such table). The table of exactly that name, when there
    /// is one; otherwise the first whose name Access takes as one with it (<see cref="ObjectNames.Find"/>).
    /// </summary>
    public int FindTable(string tableName, out string? listedName)
    {
        var entries = TableEntries(out var rule);
        int at = rule.Find(entries.Select(e => e.Name).ToList(), tableName);
        listedName = at < 0 ? null : entries[at].Name;
        return at < 0 ? -1 : entries[at].TdefPage;
    }

    /// <summary>
    /// Every table the catalog lists - its name as the catalog spells it and its definition's page - and how the
    /// catalog's names are compared (<see cref="Names"/>), from one reading of the catalog.
    /// </summary>
    internal List<(string Name, int TdefPage)> TableEntries(out ObjectNames names)
    {
        var catalogDef = BuildCatalogTableDef();
        names = NamesOf(catalogDef);
        var entries = new List<(string Name, int TdefPage)>();
        ForEachTableEntry(catalogDef, (name, tdefPage, _) => entries.Add((name, tdefPage)));
        return entries;
    }

    /// <summary>
    /// Iterates every Type=1 row in MSysObjects and invokes <paramref name="visit"/>
    /// with (name, tdefPageNumber, flags). Used by both FindTableTdefPage and GetTableNames.
    /// </summary>
    private void ForEachTableEntry(Action<string, int, int> visit) => ForEachTableEntry(BuildCatalogTableDef(), visit);

    private void ForEachTableEntry(TableDefinition catalogDef, Action<string, int, int> visit)
    {
        var columns    = catalogDef.Columns;
        var decoder    = new RowDecoder(_format, columns);

        Column? colId    = columns.FirstOrDefault(c => c.Name.Equals(ColId,    StringComparison.OrdinalIgnoreCase));
        Column? colName  = columns.FirstOrDefault(c => c.Name.Equals(ColName,  StringComparison.OrdinalIgnoreCase));
        Column? colType  = columns.FirstOrDefault(c => c.Name.Equals(ColType,  StringComparison.OrdinalIgnoreCase));
        Column? colFlags = columns.FirstOrDefault(c => c.Name.Equals(ColFlags, StringComparison.OrdinalIgnoreCase));
        Column? colParent = columns.FirstOrDefault(c => c.Name.Equals(ColParentId, StringComparison.OrdinalIgnoreCase));
        Column? colOwner  = columns.FirstOrDefault(c => c.Name.Equals(ColOwner,    StringComparison.OrdinalIgnoreCase));

        if (colId is null || colName is null || colType is null)
            throw new InvalidOperationException(
                "Could not locate required columns (Id, Name, Type) in MSysObjects TDEF.");

        byte[] umapPage  = _file.ReadPage(catalogDef.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, catalogDef.OwnedPagesRow, _format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                byte[]? rowBytes = ReadRowBytes(dp, r, _format);
                if (rowBytes is null) continue;

                object? typeVal = decoder.Decode(rowBytes, colType);
                if (typeVal is not short typeShort || typeShort != JetFormat.CatalogTypeTable)
                    continue;

                if (NameOf(decoder, rowBytes, colName, IsAnEarlierVersionsTable(decoder, rowBytes, colType, colParent, colOwner))
                    is not string name) continue;

                object? idVal = decoder.Decode(rowBytes, colId);
                if (idVal is not int id) continue;

                int flags = 0;
                if (colFlags is not null && decoder.Decode(rowBytes, colFlags) is int f)
                    flags = f;

                visit(name, id, flags);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private TableDefinition BuildCatalogTableDef() => BuildTableDef("MSysObjects", JetFormat.PageSystemCatalog);

    private TableDefinition BuildTableDef(string name, int tdefPageNumber)
    {
        byte[] tdefPage = TdefBuffer.Read(_file, tdefPageNumber);
        var    info     = TdefReader.Read(tdefPage, _format);

        var def = new TableDefinition(name, info.Columns)
        {
            TdefPageNumber = tdefPageNumber,
            UmapPageNumber = info.OwnedPagesUmapPage,
            OwnedPagesRow  = info.OwnedPagesUmapRow,
            FreeSpaceRow   = info.FreeSpaceUmapRow,
            FreeSpaceUmapPage = info.FreeSpaceUmapPage,
            Indexes        = info.Indexes,
            MaxColumnCount    = info.MaxColumnCount,
            MaxVarColumnCount = info.MaxVarColumnCount,
            HasUnreadColumns  = info.HasUnreadColumns,
            // A system table's columns, of ASCII names, by General - Legacy's keys: the catalog's own order is read from
            // a definition this builds.
            Names             = ObjectNames.ForColumns(_format, SortOrder.GeneralLegacy),
        };
        // Where a long value of each column goes: a new table's properties (LvProp) can be longer than a row keeps.
        foreach (var kvp in info.LvalColumnUmapPages)
            def.LvalColumnUmapPages[kvp.Key] = kvp.Value;
        return def;
    }

    /// <summary>
    /// Returns the raw bytes of row <paramref name="rowNum"/> from a data page,
    /// or <c>null</c> if the row is deleted; an overflow row's bytes are those its pointer leads to.
    /// </summary>
    private byte[]? ReadRowBytes(byte[] page, int rowNum, JetFormat format) => DataRows.Read(_file, page, rowNum);
}
