using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Changes to a database's schema after its tables are made (qcatco/exportmdb.interop#37): a column added to a
/// table, an index added to a table and built over its rows, and a relationship created between two tables - what
/// Jackcess's <c>TableUpdater</c> and <c>RelationshipCreator</c> do, written to the file as Access was seen to
/// write the same changes.
/// <para>
/// Each change is all or nothing. What can refuse it - a name taken, a type not written, rows that are duplicates
/// in a unique index or refer to no parent, a definition with no room left on its page - is found before anything
/// is written, and what is written is written inside <see cref="PageFile.BeginUndo"/>, so a change that fails
/// part-way is taken back. Either way the file is byte for byte what it was.
/// </para>
/// <para>
/// What differs from Access, deliberately. A new index's pages, and a new long-value column's, are listed in usage
/// maps on a page of their own (as for a table this library creates), where Access finds room on the table's
/// usage-map page first. A column added is never filled in for the rows already there, so an AutoNumber column is
/// added only to a table with no rows - Access numbers the rows it has. And a definition is never let run onto a
/// second page (<see cref="TdefEditor"/>).
/// </para>
/// </summary>
internal static class SchemaChange
{
    private const int MaxNameLength = 64;
    private const int MaxColumns    = 255;

    // ── A column ─────────────────────────────────────────────────────────────

    /// <summary>Adds a copy of <paramref name="given"/> to <paramref name="table"/> and returns its name.</summary>
    internal static string AddColumn(Database db, PageFile file, TableDefinition table, Column given)
    {
        file.EnsureWritable();
        var format = file.Format;
        EnsureReadWhole(table);

        if (string.IsNullOrWhiteSpace(given.Name) || given.Name.Length > MaxNameLength)
            throw new ArgumentException($"A column's name is 1 to {MaxNameLength} characters; '{given.Name}' is not.", nameof(given));
        if (table.Columns.Any(c => string.Equals(c.Name, given.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Table '{table.Name}' already has a column named '{given.Name}'. Nothing was changed.", nameof(given));
        if (table.Columns.Count >= MaxColumns)
            throw new ArgumentException($"Table '{table.Name}' has {MaxColumns} columns, the most a table has. Nothing was changed.", nameof(given));
        if (given.DataType == DataType.Complex || given.IsCalculated || !Enum.IsDefined(typeof(DataType), given.DataType))
            throw new NotSupportedException(
                $"A column of type {given.DataType}{(given.IsCalculated ? " that is calculated" : "")} is not added: this library does not write one. Nothing was changed.");

        // A Large Number or a Date/Time Extended column makes the file one of Access 2016's format or 2019's, as
        // it does when a table is created with one, and an .mdb has no such column: refused here, nothing changed.
        byte? version = Database.FormatVersionFor($"A column was not added to '{table.Name}'", new[] { given }, format);

        byte[] page = file.ReadPage(table.TdefPageNumber);
        if (given.IsAutoNumber)
        {
            // Access numbers the rows a table has when it is given an AutoNumber; nothing here rewrites a row.
            if (ByteUtil.GetInt(page, format.TdefOffsetNumRows) != 0)
                throw new NotSupportedException(
                    $"An AutoNumber column is not added to '{table.Name}', which has rows: Access would number them, and this " +
                    "library rewrites no row when it adds a column. Nothing was changed.");
            if (table.Columns.Any(c => c.IsAutoNumber))
                throw new NotSupportedException($"Table '{table.Name}' already has an AutoNumber column. Nothing was changed.");
        }

        var editor = new TdefEditor(page, format, table.Name);
        bool isLong = given.DataType.IsLongValue();
        editor.EnsureRoomFor(editor.SizeOfColumn(given.Name, isLong));

        // The table's own column, a copy of the caller's: numbered after every column the table has ever had, so
        // that no row written before it has its bit in a null mask, and placed after the values those rows hold.
        var column = given.CopyFor(db.Catalog.TextSortOrder);
        bool isVariable = column.DataType.IsVariableLength();
        int  maxVar     = editor.MaxVarColumnCount;
        column.ColumnNumber     = editor.MaxColumnCount;
        column.IsVariableLength = isVariable;
        column.HasStoredPlace   = true;
        if (isVariable)
        {
            column.VarLenTableIndex = (short)maxVar;
            column.FixedDataOffset  = -1;
        }
        else
        {
            column.VarLenTableIndex = -1;
            // A Yes/No column takes no room: its value is its bit in the null mask, and Access gives it offset 0.
            column.FixedDataOffset  = column.DataType == DataType.Boolean ? (short)0 : (short)new RowLayout(format, table).FixedDataSize;
        }

        // Rows must still be writable by the table's columns with this one among them.
        var layout = new RowLayout(format, table.Columns.Append(column).ToList(), column.ColumnNumber + 1, maxVar + (isVariable ? 1 : 0));
        if (layout.WhyNotWritable is { } why)
            throw new NotSupportedException($"A column is not added to '{table.Name}': {why}. Nothing was changed.");

        int columnId = editor.ColumnIds.DefaultIfEmpty(-1).Max() + 1;
        using (var undo = file.BeginUndo())
        {
            LvalUmapRef? maps = null;
            if (isLong)
            {
                int umapPage = db.Allocator.AllocateUmapPage();
                maps = new LvalUmapRef(umapPage, 0, umapPage, 1);
            }
            editor.AddColumn(column, columnId, maps);
            file.WritePage(table.TdefPageNumber, editor.ToPage());
            if (version is { } newer) DatabaseHeader.RaiseVersion(file, newer);
            undo.Commit();
        }
        Changed(db, file, table.TdefPageNumber);
        return column.Name;
    }

    // ── An index ─────────────────────────────────────────────────────────────

    /// <summary>Adds <paramref name="index"/> to <paramref name="table"/>, built over its rows.</summary>
    internal static void AddIndex(Database db, PageFile file, TableDefinition table, IndexDefinition index)
    {
        file.EnsureWritable();
        EnsureReadWhole(table);

        if (index.Name.Length > MaxNameLength)
            throw new ArgumentException($"An index's name is at most {MaxNameLength} characters; '{index.Name}' is longer.", nameof(index));
        EnsureValidName(index.Name, "an index", nameof(index));
        foreach (string descending in index.DescendingColumns)
            if (!index.Columns.Contains(descending, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Index '{index.Name}' sorts '{descending}' descending, which is not one of its columns.", nameof(index));

        byte flags = TableDefinition.UnknownIndexFlag;
        if (index.IsUnique)    flags |= TableDefinition.UniqueIndexFlag;
        if (index.IgnoreNulls) flags |= TableDefinition.IgnoreNullsIndexFlag;
        if (index.IsRequired)  flags |= TableDefinition.RequiredIndexFlag;

        var columns = index.Columns.Select(name => new IndexColumn(
            ColumnOf(table, name, $"Index '{index.Name}'"),
            index.DescendingColumns.Contains(name, StringComparer.OrdinalIgnoreCase) ? (byte)0 : TableDefinition.AscendingColumnFlag)).ToList();

        // An index like one the table has - the same columns, each sorted the same way, and the same flags -
        // shares its tree, as Jackcess shares it.
        var planned = Plan(file, table, index.Name, columns, flags, indexType: 0, shares: existing => existing.Flags == flags);

        using (var undo = file.BeginUndo())
        {
            Write(db, file, planned, foreignKey: null);
            undo.Commit();
        }
        Changed(db, file, table.TdefPageNumber);
    }

    /// <summary>An index about to be added to a table: checked, its keys made, and nothing written yet.</summary>
    private sealed class PlannedIndex
    {
        internal PlannedIndex(TableDefinition table, string name, IReadOnlyList<IndexColumn> columns, byte flags, byte indexType)
        {
            Table     = table;
            Name      = name;
            Columns   = columns;
            Flags     = flags;
            IndexType = indexType;
        }

        internal TableDefinition Table { get; }
        internal string Name { get; }
        internal IReadOnlyList<IndexColumn> Columns { get; }
        internal byte Flags { get; }
        internal byte IndexType { get; }

        /// <summary>The block of an index the table already has whose tree this one shares, or null for a tree of its own.</summary>
        internal int? SharedData { get; set; }

        /// <summary>The entries of a tree of its own, in key order: one for each row the index holds.</summary>
        internal List<(byte[] Key, int RowPointer)> Entries { get; set; } = new();

        internal int DistinctKeys { get; set; }

        /// <summary>The bytes the index adds to the table's definition.</summary>
        internal int DefinitionBytes { get; set; }
    }

    /// <summary>
    /// Checks an index <paramref name="table"/> is to be given and makes its entries, writing nothing: its name is
    /// free, the table has room for another index and its definition for this one's bytes (and
    /// <paramref name="alsoAdded"/> more, which the same change adds), every column's keys are ones this library
    /// writes, and - unless a tree already there is shared - every row has a key the index takes.
    /// </summary>
    private static PlannedIndex Plan(PageFile file, TableDefinition table, string name, IReadOnlyList<IndexColumn> columns,
                                     byte flags, byte indexType, Func<Index, bool> shares, int alsoAdded = 0, int alsoIndexes = 0)
    {
        if (table.Indexes.Any(ix => string.Equals(ix.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Table '{table.Name}' already has an index named '{name}'. Nothing was changed.");
        if (table.Indexes.Count + alsoIndexes >= IndexDefinition.MaxIndexesPerTable)
            throw new ArgumentException(
                $"Table '{table.Name}' has {table.Indexes.Count + alsoIndexes} indexes; a table has at most {IndexDefinition.MaxIndexesPerTable}. Nothing was changed.");
        foreach (var column in columns)
            if (!IndexKeys.CanEncode(column.Column))
                throw new NotSupportedException(
                    $"Index '{name}' on '{table.Name}.{column.Column.Name}' is not supported: {IndexKeys.WhyNot(column.Column)}, " +
                    "so its index entries would not be the ones Access writes. Nothing was changed.");

        var planned = new PlannedIndex(table, name, columns, flags, indexType);
        var shared = table.Indexes.FirstOrDefault(existing =>
            existing.RootPageNumber > 0
            && existing.Columns.Count == columns.Count
            && existing.Columns.Zip(columns, (a, b) => a.Column.ColumnNumber == b.Column.ColumnNumber && a.Flags == b.Flags).All(same => same)
            && shares(existing));
        planned.SharedData = shared?.IndexDataNumber;

        var editor = new TdefEditor(file.ReadPage(table.TdefPageNumber), file.Format, table.Name);
        planned.DefinitionBytes = editor.SizeOfIndexSlot(name) + (shared is null ? editor.SizeOfIndexData : 0);
        editor.EnsureRoomFor(planned.DefinitionBytes + alsoAdded);

        if (shared is null) BuildEntries(file, planned);
        return planned;
    }

    /// <summary>
    /// Reads every row of the table and makes its entry in the planned index, as <see cref="TableIndexes"/> makes a
    /// row's entry when it is inserted - and refuses what an insert is refused for: a null in an index that
    /// requires its values, a value no key is made for, and two rows with the same key in a unique index (rows
    /// with a null in any of its columns aside, which are duplicates of nothing).
    /// </summary>
    private static void BuildEntries(PageFile file, PlannedIndex planned)
    {
        var table    = planned.Table;
        var format   = file.Format;
        var columns  = planned.Columns;
        bool unique  = (planned.Flags & TableDefinition.UniqueIndexFlag) != 0;
        bool require = (planned.Flags & TableDefinition.RequiredIndexFlag) != 0 || planned.IndexType == TableDefinition.PrimaryKeyIndexType;
        bool ignore  = (planned.Flags & TableDefinition.IgnoreNullsIndexFlag) != 0;

        var decoder  = new RowDecoder(format, table.Columns, table.LvalColumnUmapPages.Count > 0 ? new LvalReader(file) : null);
        var entries  = new List<(byte[] Key, int RowPointer, bool HasNull, object?[] Values)>();
        foreach (int pageNumber in UsageMap.GetOwnedPages(file.ReadPage(table.UmapPageNumber), table.OwnedPagesRow, format, file))
        {
            byte[] page = file.ReadPage(pageNumber);
            int rowCount = ByteUtil.GetShort(page, format.OffsetDataNumRows);
            for (int r = 0; r < rowCount; r++)
            {
                if (DataRows.Read(file, page, r) is not { } rowBytes) continue;

                // The row's values as it stores them, which is what its keys are made of: a Yes/No holds no null.
                var values = new object?[columns.Count];
                int nulls = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    var column = columns[i].Column;
                    values[i] = decoder.DecodeForKey(rowBytes, column);
                    if (column.DataType == DataType.Boolean) values[i] = RowValues.ToBoolean(values[i]);
                    else if (values[i] is null) nulls++;
                }

                if (nulls > 0 && require)
                    throw new InvalidOperationException(
                        $"Index '{planned.Name}' requires a value in each of its columns, and a row of '{table.Name}' has none for " +
                        $"{string.Join(", ", columns.Where((_, i) => values[i] is null).Select(c => c.Column.Name))}. Nothing was changed.");
                if (nulls == values.Length && ignore) continue;
                for (int i = 0; i < values.Length; i++)
                    if (!IndexKeys.CanEncode(columns[i].Column, values[i]))
                        throw new NotSupportedException(
                            $"Index '{planned.Name}' cannot be built over '{table.Name}': {IndexKeys.WhyNot(columns[i].Column, values[i])}. Nothing was changed.");

                entries.Add((IndexKeys.Encode(columns, values), RowPointer.Pack(pageNumber, r), nulls > 0, values));
            }
        }

        entries.Sort((a, b) =>
        {
            int byKey = CompareBytes(a.Key, b.Key);
            return byKey != 0 ? byKey : ((uint)a.RowPointer).CompareTo((uint)b.RowPointer);
        });

        int distinct = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            bool sameAsLast = i > 0 && CompareBytes(entries[i - 1].Key, entries[i].Key) == 0;
            if (!sameAsLast) distinct++;
            // A key longer than an entry holds is cut (IndexKeys.Encode), and two values cut to the same entry
            // are duplicates to a unique index, in Access as here.
            else if (unique && !entries[i].HasNull && !entries[i - 1].HasNull)
                throw new InvalidOperationException(
                    $"Index '{planned.Name}' is unique, and two rows of '{table.Name}' hold " +
                    $"{string.Join(", ", columns.Select((c, n) => $"{Shown(entries[i].Values[n])} for {c.Column.Name}"))}. Nothing was changed.");
        }

        planned.Entries      = entries.Select(e => (e.Key, e.RowPointer)).ToList();
        planned.DistinctKeys = distinct;
    }

    /// <summary>
    /// Writes a planned index: its tree, unless it shares one - a root page, a usage map for its pages, and an
    /// entry for each row - then its block and its slot in the table's definition. Returns the number the index
    /// has in the table. The caller holds the undo scope.
    /// </summary>
    private static int Write(Database db, PageFile file, PlannedIndex planned, TdefEditor.ForeignKeyReference? foreignKey)
    {
        var table = planned.Table;
        int data;
        int root = 0, umapPage = 0;
        if (planned.SharedData is { } sharedData)
        {
            data = sharedData;
        }
        else
        {
            var writer = new IndexWriter(file, db.Allocator);
            root     = writer.CreateEmptyLeafPage(table.TdefPageNumber);
            umapPage = db.Allocator.AllocateUmapPage(rows: 1);
            UsageMap.AddPage(file, db.Allocator, umapPage, 0, root);
            var target = new IndexWriter.Target(table.TdefPageNumber, root, umapPage, 0);
            foreach (var (key, rowPointer) in planned.Entries)
                writer.Insert(target, key, rowPointer);
            data = -1;
        }

        // The definition as it now is: an index added to the same table earlier in this change is in it.
        var editor = new TdefEditor(file.ReadPage(table.TdefPageNumber), file.Format, table.Name);
        if (data < 0)
            data = editor.AddIndexData(planned.Columns, planned.Flags, root, umapPage, 0, planned.Entries.Count, planned.DistinctKeys);

        int number = NextIndexNumber(editor);
        editor.AddIndexSlot(SlotPosition(editor.SlotNames, planned.Name, db.Catalog.TextSortOrder), planned.Name, number, data,
                            planned.IndexType, foreignKey);
        file.WritePage(table.TdefPageNumber, editor.ToPage());
        return number;
    }

    /// <summary>The number the next index of a table is given: one past the highest it has.</summary>
    private static int NextIndexNumber(TdefEditor editor) => editor.SlotNumbers.DefaultIfEmpty(-1).Max() + 1;

    /// <summary>
    /// Where a new index's slot goes among a table's: Access keeps them in the order of their names, as its
    /// catalog sorts text, so the slot goes before the first whose name sorts after its own. (Jackcess puts a new
    /// one last.) A name no key is made for is placed by its letters, without regard to case.
    /// </summary>
    private static int SlotPosition(IReadOnlyList<string> names, string name, SortOrder order)
    {
        var codes = TextIndexCodes.For(order);
        byte[]? KeyOf(string text)
        {
            if (codes is null) return null;
            try { return IndexKeys.EncodeText(codes, ascending: true, text); }
            catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
        }

        byte[]? key = KeyOf(name);
        for (int i = 0; i < names.Count; i++)
        {
            byte[]? other = key is null ? null : KeyOf(names[i]);
            int order2 = key is not null && other is not null
                ? CompareBytes(other, key)
                : string.Compare(names[i], name, StringComparison.OrdinalIgnoreCase);
            if (order2 > 0) return i;
        }
        return names.Count;
    }

    // ── A relationship ───────────────────────────────────────────────────────

    private const int NoReferentialIntegrityFlag = 0x00000002;
    private const int OneToOneFlag               = 0x00000001;
    private const int CascadeUpdatesFlag         = 0x00000100;
    private const int CascadeDeletesFlag         = 0x00001000;
    private const int CascadeNullFlag            = 0x00002000;
    private const int LeftOuterJoinFlag          = 0x01000000;
    private const int RightOuterJoinFlag         = 0x02000000;

    private const string RelationshipsTable = "MSysRelationships";

    internal static Relationship CreateRelationship(Database db, PageFile file, RelationshipDefinition definition)
    {
        file.EnsureWritable();

        // The tables, by the names the catalog has for them: MSysRelationships holds those.
        string fromName = db.Catalog.TableNameAsListed(definition.FromTable)
            ?? throw new InvalidOperationException($"Table '{definition.FromTable}' was not found in this database. Nothing was changed.");
        string toName = db.Catalog.TableNameAsListed(definition.ToTable)
            ?? throw new InvalidOperationException($"Table '{definition.ToTable}' was not found in this database. Nothing was changed.");
        var parent = db.GetTable(fromName);
        var child  = db.GetTable(toName);
        bool sameTable = string.Equals(fromName, toName, StringComparison.OrdinalIgnoreCase);

        var fromColumns = definition.ColumnPairs.Select(p => ColumnOf(parent.Definition, p.FromColumn, "A relationship's")).ToList();
        var toColumns   = definition.ColumnPairs.Select(p => ColumnOf(child.Definition, p.ToColumn, "A relationship's")).ToList();
        for (int i = 0; i < fromColumns.Count; i++)
            if (fromColumns[i].DataType != toColumns[i].DataType)
                throw new InvalidOperationException(
                    $"A relationship's matched columns must have the same data type: '{fromName}.{fromColumns[i].Name}' is " +
                    $"{fromColumns[i].DataType} and '{toName}.{toColumns[i].Name}' is {toColumns[i].DataType}. Nothing was changed.");

        bool enforced = definition.HasReferentialIntegrity;
        if (!enforced && (definition.CascadeUpdates || definition.CascadeDeletes || definition.CascadeNull))
            throw new ArgumentException("A relationship cascades only when it has referential integrity.", nameof(definition));

        int flags = enforced ? 0 : NoReferentialIntegrityFlag;
        if (definition.CascadeUpdates) flags |= CascadeUpdatesFlag;
        if (definition.CascadeDeletes) flags |= CascadeDeletesFlag;
        if (definition.CascadeNull)    flags |= CascadeNullFlag;
        if (definition.JoinType == RelationshipJoinType.LeftOuter)  flags |= LeftOuterJoinFlag;
        if (definition.JoinType == RelationshipJoinType.RightOuter) flags |= RightOuterJoinFlag;
        // One to one when each side's columns have a unique index of their own, as Access's Relationships window
        // and Jackcess mark it. To Access the mark is more than a description: the foreign key's index is then a
        // unique one (below).
        bool oneToOne = UniqueIndexOn(parent, fromColumns) is not null && UniqueIndexOn(child, toColumns) is not null;
        if (oneToOne) flags |= OneToOneFlag;

        Table relationships;
        try { relationships = db.GetTable(RelationshipsTable); }
        catch (InvalidOperationException)
        {
            throw new NotSupportedException($"The database has no {RelationshipsTable} table to hold a relationship. Nothing was changed.");
        }
        if (relationships.InsertRefusal is { } refusal) throw new NotSupportedException(refusal);

        // The name: the one asked for, which must be free, or one made of the tables' names and made free.
        var taken = new HashSet<string>(db.GetRelationships().Select(r => r.Name).Concat(db.Catalog.RelationshipNames()),
                                        StringComparer.OrdinalIgnoreCase);
        if (enforced)
            foreach (var index in child.Indexes) taken.Add(index.Name);
        string name;
        if (definition.Name is { } asked)
        {
            if (string.IsNullOrWhiteSpace(asked) || asked.Length > MaxNameLength)
                throw new ArgumentException($"A relationship's name is 1 to {MaxNameLength} characters; '{asked}' is not.", nameof(definition));
            EnsureValidName(asked, "a relationship", nameof(definition));
            if (taken.Contains(asked))
                throw new InvalidOperationException(
                    $"There is already a relationship named '{asked}' in this database, or an index of that name on '{toName}'. Nothing was changed.");
            name = asked;
        }
        else
        {
            // Made of the tables' names, which this library lets hold what a name may not: then one must be given.
            name = FreeName(fromName, toName, taken);
            EnsureValidName(name, "a relationship (made of the tables' names: give it one of its own)", nameof(definition));
        }
        var entry = db.Catalog.PrepareRelationship(name);

        var pairs = fromColumns.Select((c, i) => (c.Name, toColumns[i].Name)).ToList();
        var rows = pairs.Select((pair, i) => new Row
        {
            ["ccolumn"]            = pairs.Count,
            ["grbit"]              = flags,
            ["icolumn"]            = i,
            ["szColumn"]           = pair.Item2,
            ["szObject"]           = toName,
            ["szReferencedColumn"] = pair.Item1,
            ["szReferencedObject"] = fromName,
            ["szRelationship"]     = name,
        }).ToList();
        foreach (var row in rows) relationships.Validate(row);

        var relationship = new Relationship(name, fromName, toName, flags, pairs);

        PlannedIndex? primary = null, secondary = null;
        if (enforced)
        {
            if (fromColumns.Distinct().Count() != fromColumns.Count || toColumns.Distinct().Count() != toColumns.Count)
                throw new ArgumentException("A relationship with referential integrity names each column of a table once.", nameof(definition));
            if (UniqueIndexOn(parent, fromColumns) is null)
                throw new InvalidOperationException(
                    $"No unique index found for the referenced columns ({string.Join(", ", fromColumns.Select(c => c.Name))}) of " +
                    $"'{fromName}': a relationship with referential integrity refers to a primary key or a unique index. Nothing was changed.");
            EnsureReadWhole(parent.Definition);
            EnsureReadWhole(child.Definition);

            if (ForeignKeyEnforcer.FirstOrphan(relationship, parent, child) is { } orphan)
                throw new InvalidOperationException(
                    $"Relationship '{name}' cannot have referential integrity: existing data in '{toName}' violates it - {orphan}. Nothing was changed.");

            // On the referenced table, an index Access names .rB, .rC... that shares the tree of the unique index
            // on those columns; on the referring table, an index with the relationship's name on the foreign key,
            // which shares the tree of a plain index on exactly those columns when there is one and otherwise is
            // built - Access shares no unique, required or null-ignoring index's tree for it. In a one-to-one
            // relationship Access makes that index unique instead, and it shares the tree of an index that is
            // unique and nothing more (Jackcess gives it a plain one, which Access itself never writes there).
            var ascending = TableDefinition.AscendingColumnFlag;
            byte plain  = TableDefinition.UnknownIndexFlag;
            byte unique = (byte)(TableDefinition.UnknownIndexFlag | TableDefinition.UniqueIndexFlag);
            const byte ignored = TableDefinition.IgnoreNullsIndexFlag | TableDefinition.RequiredIndexFlag;

            // In one table Access numbers the foreign key's index first and the referenced side's after it, and
            // names the second by that number.
            primary = Plan(file, parent.Definition, PrimaryIndexName(parent.Definition, numbersTaken: sameTable ? 1 : 0),
                           fromColumns.Select(c => new IndexColumn(c, ascending)).ToList(), unique, TdefEditor.IndexTypeForeignKey,
                           shares: existing => (existing.Flags | ignored) == (unique | ignored));
            secondary = Plan(file, child.Definition, name,
                             toColumns.Select(c => new IndexColumn(c, ascending)).ToList(), oneToOne ? unique : plain,
                             TdefEditor.IndexTypeForeignKey,
                             shares: existing => existing.Flags == (oneToOne ? unique : plain),
                             alsoAdded: sameTable ? primary.DefinitionBytes : 0, alsoIndexes: sameTable ? 1 : 0);
        }

        using (var undo = file.BeginUndo())
        {
            foreach (var row in rows) relationships.Insert(row);
            db.Catalog.InsertRelationshipEntry(entry, name);

            if (primary is not null && secondary is not null)
            {
                // Each side's slot names the other's index by its number, so both are settled before either is written.
                // A table related to itself has both: the foreign key's index takes the next number and the
                // referenced side's the one after, as DAO numbers them, so it is written first.
                int secondaryNumber = NextIndexNumber(new TdefEditor(file.ReadPage(child.Definition.TdefPageNumber), file.Format, toName));
                int primaryNumber   = sameTable
                    ? secondaryNumber + 1
                    : NextIndexNumber(new TdefEditor(file.ReadPage(parent.Definition.TdefPageNumber), file.Format, fromName));
                var fromPrimary = new TdefEditor.ForeignKeyReference(
                    TdefEditor.ForeignKeyPrimaryTable, secondaryNumber, child.Definition.TdefPageNumber,
                    definition.CascadeUpdates, definition.CascadeDeletes, definition.CascadeNull);
                var fromSecondary = new TdefEditor.ForeignKeyReference(
                    TdefEditor.ForeignKeySecondaryTable, primaryNumber, parent.Definition.TdefPageNumber,
                    definition.CascadeUpdates, definition.CascadeDeletes, definition.CascadeNull);

                int writtenSecondary = Write(db, file, secondary, fromSecondary);
                int writtenPrimary   = Write(db, file, primary, fromPrimary);
                if (writtenSecondary != secondaryNumber || writtenPrimary != primaryNumber)
                    throw new InvalidOperationException(
                        $"Relationship '{name}' was not created: its indexes were not given the numbers their slots name. Nothing was changed.");
            }
            undo.Commit();
        }

        Changed(db, file, parent.Definition.TdefPageNumber);
        Changed(db, file, child.Definition.TdefPageNumber);
        return relationship;
    }

    /// <summary>The unique index (or primary key) of <paramref name="table"/> on exactly <paramref name="columns"/>, in that order, or null.</summary>
    private static Index? UniqueIndexOn(Table table, IReadOnlyList<Column> columns)
        => table.Indexes.FirstOrDefault(index =>
            index.IsUnique
            && index.Columns.Count == columns.Count
            && index.Columns.Zip(columns, (a, b) => a.Column.ColumnNumber == b.ColumnNumber).All(same => same));

    /// <summary>
    /// The name Access gives the index a relationship has on its referenced table: ".r" and a letter, B for the
    /// table's second index, C for its third - the letter the index's number counts to - with the lower-case
    /// letters after Z; and, as in Jackcess, the next letter while a name is taken.
    /// </summary>
    private static string PrimaryIndexName(TableDefinition table, int numbersTaken)
    {
        var taken = new HashSet<string>(table.Indexes.Select(ix => ix.Name), StringComparer.OrdinalIgnoreCase);
        int number = table.Indexes.Select(ix => ix.IndexNumber).DefaultIfEmpty(-1).Max() + 1 + numbersTaken;
        for (int n = number; ; n++)
        {
            char letter = n < 26 ? (char)('A' + n) : (char)('a' + (n - 26) % 26);
            string name = ".r" + letter + (n < 52 ? "" : (n / 52).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!taken.Contains(name)) return name;
        }
    }

    /// <summary>
    /// Refuses a name Access does not allow an index or a relationship: one with a period, an exclamation mark, a
    /// grave accent or a square bracket in it, a control character, or a space at its start ("... is not a valid
    /// name. Make sure that it does not include invalid characters or punctuation"). It is what keeps the names
    /// Access makes itself for a referenced table's index - .rB, .rC... - from being anyone else's: given ".rB"
    /// for a relationship of a table to itself, this library once wrote two indexes of that name into the table.
    /// </summary>
    private static void EnsureValidName(string name, string ofWhat, string parameter)
    {
        if (name.Length > 0 && name[0] != ' ' && !name.Any(c => c is '.' or '!' or '`' or '[' or ']' || char.IsControl(c))) return;
        throw new ArgumentException(
            $"'{name}' is not a valid name for {ofWhat}: Access allows no period, exclamation mark, grave accent, square bracket " +
            "or control character in one, and no space at its start. Nothing was changed.", parameter);
    }

    /// <summary>
    /// A name for a relationship that was given none: the two tables' names run together, as Access's
    /// Relationships window names one, kept to what an index's name can be (Jackcess's limits), with a number
    /// after it when a relationship - or an index of the referring table - has it already.
    /// </summary>
    private static string FreeName(string fromTable, string toTable, HashSet<string> taken)
    {
        int limit = MaxNameLength / 2 - 3;
        string name = (fromTable.Length > limit ? fromTable.Substring(0, limit) : fromTable) + toTable;
        limit = MaxNameLength - 3;
        if (name.Length > limit) name = name.Substring(0, limit);

        string free = name;
        for (int i = 1; taken.Contains(free); i++)
            free = name + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return free;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses to change a table whose definition was not read whole - it runs past its first page - since what
    /// would be written back is only what was read.
    /// </summary>
    private static void EnsureReadWhole(TableDefinition table)
    {
        if (table.HasUnreadColumns || table.HasUnreadIndexes)
            throw new NotSupportedException(
                $"Table '{table.Name}' is not changed: its definition runs over more than one page, or names an index column " +
                "or block it has not, and this library reads and writes a definition of one page only. Nothing was changed.");
    }

    private static Column ColumnOf(TableDefinition table, string name, string what)
        => table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{what} column '{name}' was not found in table '{table.Name}'. Nothing was changed.");

    /// <summary>
    /// After a change is written: every <see cref="Table"/> open on the table reads its definition again, and the
    /// relationships are read again by whatever keeps them.
    /// </summary>
    private static void Changed(Database db, PageFile file, int tdefPage)
    {
        file.DefinitionChanged(tdefPage);
        db.SchemaChanged();
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++)
            if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        return a.Length.CompareTo(b.Length);
    }

    // A value as a refusal shows it: whole when short, its beginning when long.
    private static string Shown(object? value)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        if (text.Length > 40) text = text.Substring(0, 40) + "...";
        return value is string ? $"'{text}'" : value is null ? "null" : text;
    }
}
