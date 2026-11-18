using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Keeps every index of a table as its rows change, as Access keeps them: the primary key and every other index,
/// whoever made the table. A row inserted gains an entry in each index, a row deleted loses them, and an updated
/// row's entries move with it. Access answers a query through an index when one covers it, so a row without its
/// entry is a row Access does not find, and an entry without its row is one it counts.
///
/// What Access refuses is refused here before anything is changed:
///   • a table with an index whose keys this library cannot write (<see cref="IndexKeys.CanEncode(Column)"/>),
///     or whose indexes it could not read;
///   • a null in a column of the primary key or of an index that requires its values;
///   • a second row with the same values in a unique index. As in Access, a row with a null in any column of the
///     index is not a duplicate of anything.
///
/// Two logical indexes on the same columns can share one tree (Access gives a relationship's index the tree of an
/// index already on its columns); a tree is changed once however many indexes name it. An index that ignores
/// nulls holds no entry for a row whose index columns are all null.
/// </summary>
internal sealed class TableIndexes
{
    private readonly PageFile        _file;
    private readonly PageAllocator   _allocator;
    private readonly TableDefinition _table;
    private IReadOnlyList<Tree>?     _trees;

    public TableIndexes(PageFile file, PageAllocator allocator, TableDefinition table)
    {
        _file      = file;
        _allocator = allocator;
        _table     = table;
    }

    /// <summary>One index tree, and what the indexes sharing it ask of a row.</summary>
    internal sealed class Tree
    {
        public Tree(int tdefPage, IReadOnlyList<Index> sharing)
        {
            var first = sharing[0];
            Name            = string.Join("/", sharing.Select(ix => ix.Name));
            Columns         = first.Columns;
            Target          = IndexWriter.Target.For(tdefPage, first);
            IsPrimaryKey    = sharing.Any(ix => ix.IsPrimaryKey);
            IsUnique        = sharing.Any(ix => ix.IsUnique);
            RefusesNulls    = IsPrimaryKey || sharing.Any(ix => ix.IsRequired);
            IgnoresNulls    = first.IgnoresNulls;
        }

        public string                     Name         { get; }
        public IReadOnlyList<IndexColumn> Columns      { get; }
        public IndexWriter.Target         Target       { get; }
        public bool                       IsPrimaryKey { get; }
        public bool                       IsUnique     { get; }
        public bool                       RefusesNulls { get; }
        public bool                       IgnoresNulls { get; }
    }

    /// <summary>A row's entry in one tree: its key, and whether a unique index compares it with other rows'.</summary>
    internal readonly struct Entry
    {
        internal Entry(Tree tree, byte[] key, bool hasNull, IReadOnlyList<object?> values)
        {
            Tree    = tree;
            Key     = key;
            HasNull = hasNull;
            Values  = values;
        }

        /// <summary>The row's values for the tree's columns, in their order: what a refusal names.</summary>
        internal IReadOnlyList<object?> Values { get; }

        internal Tree   Tree    { get; }
        internal byte[] Key     { get; }
        internal bool   HasNull { get; }
    }

    private IReadOnlyList<Tree> Trees => _trees ??= _table.Indexes
        .GroupBy(ix => ix.IndexDataNumber)
        .Select(g => new Tree(_table.TdefPageNumber, g.ToList()))
        .ToList();

    /// <summary>
    /// Refuses, before anything changes, to change a table with an index this library cannot keep: one on a column
    /// whose entries it cannot write as Access writes them, or indexes it did not read. A row changed with an
    /// index left behind is one Access counts and finds wrongly.
    /// </summary>
    public void EnsureCanBeKept()
    {
        if (_table.HasUnreadIndexes)
            throw new NotSupportedException(
                $"{_table.Name}'s indexes cannot be kept: they were not all read from its definition, which runs over " +
                "more than one page or names an index column or block it has not. The table is left as it was.");
        foreach (var tree in Trees)
        {
            if (tree.Columns.Count == 0 || tree.Target.RootPage <= 0)
                throw new NotSupportedException(
                    $"{_table.Name}'s {Describe(tree)} cannot be kept: its definition gives it no column or no page. " +
                    "The table is left as it was.");
            foreach (var column in tree.Columns)
                if (!IndexKeys.CanEncode(column.Column))
                    throw new NotSupportedException(
                        $"{_table.Name}'s {Describe(tree)} cannot be kept: {IndexKeys.WhyNot(column.Column)}. The table is left as it was.");
        }
    }

    /// <summary>
    /// The entries <paramref name="row"/> has in the table's indexes. With <paramref name="enforce"/>, for a row
    /// about to be written, a null the primary key or a required index does not take is refused; without, for a
    /// row already in the table, the entries are whatever it has.
    /// </summary>
    public IReadOnlyList<Entry> EntriesFor(Row row, bool enforce)
    {
        var entries = new List<Entry>(Trees.Count);
        foreach (var tree in Trees)
        {
            var values = new object?[tree.Columns.Count];
            int nulls = 0;
            for (int i = 0; i < values.Length; i++)
            {
                var column = tree.Columns[i].Column;
                // The row's values as the row encoder reads them (RowValues): DBNull is no value, and a Yes/No
                // column holds no null - whatever it is given stands for Yes or No.
                values[i] = row.TryGetValue(column.Name, out var value) && !RowValues.IsNull(value) ? value : null;
                if (column.DataType == DataType.Boolean) values[i] = RowValues.ToBoolean(values[i]);
                else if (values[i] is null) nulls++;
            }

            if (enforce && nulls > 0 && tree.RefusesNulls)
            {
                var missing = tree.Columns.Where((_, i) => values[i] is null).Select(c => c.Column.Name);
                throw new InvalidOperationException(
                    $"{_table.Name}'s {Describe(tree)} does not take a null: the row has none for {string.Join(", ", missing)}. " +
                    "The table is left as it was.");
            }
            if (nulls == values.Length && tree.IgnoresNulls) continue;

            byte[] key = IndexKeys.Encode(tree.Columns, values);
            if (enforce && key.Length > IndexWriter.MaxKeyLength(_file.Format))
                throw new InvalidOperationException(
                    $"The row's key in {_table.Name}'s {Describe(tree)} is {key.Length} bytes long; an index page holds " +
                    $"keys of up to {IndexWriter.MaxKeyLength(_file.Format)}. The table is left as it was.");
            entries.Add(new Entry(tree, key, nulls > 0, values));
        }
        return entries;
    }

    /// <summary>
    /// Refuses <paramref name="entries"/>, a row's about to be written, when a unique index already holds one of
    /// their keys for another row. <paramref name="replacedRowPointer"/> is the row an update is replacing, whose
    /// own entries are not another row's. Returns the writer that looked, when one did and
    /// <paramref name="forEntry"/> asks for it: it holds the pages it read, which <see cref="Add"/> then reads
    /// no second time - so a unique index costs an insert no more page reads than one that is not.
    /// </summary>
    public IndexWriter? EnsureNoDuplicates(IReadOnlyList<Entry> entries, int? replacedRowPointer = null, bool forEntry = false)
    {
        IndexWriter? writer = null;
        foreach (var entry in entries)
        {
            var tree = entry.Tree;
            if (!tree.IsUnique || entry.HasNull) continue;

            if (writer is null)
            {
                writer = new IndexWriter(_file, _allocator);
                if (forEntry) writer.RememberPagesRead();
            }
            foreach (int holder in writer.RowPointersForKey(tree.Target.RootPage, entry.Key))
            {
                if (holder == replacedRowPointer || !IsLiveRow(holder)) continue;
                throw new InvalidOperationException(DuplicateIn(entry) + " The table is left as it was.");
            }
        }
        return forEntry ? writer : null;
    }

    /// <summary>What is said of a row whose <paramref name="entry"/> is a duplicate in its unique index, by whoever found it one.</summary>
    internal string DuplicateIn(Entry entry)
        => $"The row would be a duplicate in {_table.Name}'s {Describe(entry.Tree)}, which is unique: another row holds " +
           $"{string.Join(", ", entry.Tree.Columns.Select((c, i) => $"{Shown(entry.Values[i])} for {c.Column.Name}"))}.";

    // ── Finding rows ─────────────────────────────────────────────────────────

    /// <summary>
    /// The row pointers an index on exactly <paramref name="columns"/>, in that order, holds for
    /// <paramref name="values"/> - Access gives a relationship's foreign key such an index, and a primary key is
    /// one - or null when the table has no such index that can be searched: none on those columns, one whose keys
    /// this library does not make, or one that holds no entry for a key that is all null. Each pointer is an
    /// entry, not yet a row: the row it points at is the caller's to read and compare, as an index's text key
    /// finds 'ABC' for 'abc' and an entry may outlive its row in a file an older version of this library wrote.
    /// <para>
    /// That covers an entry too many. An entry too few it cannot: the index is trusted to hold every row, as Access
    /// trusts it. Versions of this library before it kept every index (those in ExportMdb.Interop up to 2.1.2)
    /// added a row to its table's primary key and to no other index, so a row one of them inserted into a table
    /// Access made is in none of that table's other indexes, and is not found here
    /// (qcatco/exportmdb.interop#64).
    /// </para>
    /// </summary>
    internal IEnumerable<int>? RowPointersFor(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        if (_table.HasUnreadIndexes) return null;
        var tree = Trees.FirstOrDefault(t =>
            t.Target.RootPage > 0
            && t.Columns.Count == columns.Count
            && t.Columns.Select(c => c.Column.Name).SequenceEqual(columns, StringComparer.OrdinalIgnoreCase)
            && t.Columns.All(c => IndexKeys.CanEncode(c.Column)));
        if (tree is null || (tree.IgnoresNulls && values.All(v => v is null))) return null;

        byte[] key;
        try { key = IndexKeys.Encode(tree.Columns, values); }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return Array.Empty<int>();   // not a value the index's columns can hold: no row has it
        }
        return new IndexWriter(_file, _allocator).RowPointersForKey(tree.Target.RootPage, key);
    }

    /// <summary>The row pointers <paramref name="entry"/>'s index holds under its key: the rows a unique index compares it with.</summary>
    internal IEnumerable<int> RowPointersFor(Entry entry)
        => new IndexWriter(_file, _allocator).RowPointersForKey(entry.Tree.Target.RootPage, entry.Key);

    /// <summary>
    /// Adds <paramref name="entries"/> to their indexes for the row at <paramref name="rowPointer"/>, through
    /// <paramref name="writer"/> when <see cref="EnsureNoDuplicates"/> returned one for them.
    /// </summary>
    public void Add(IReadOnlyList<Entry> entries, int rowPointer, IndexWriter? writer = null)
    {
        if (entries.Count == 0) return;
        writer ??= new IndexWriter(_file, _allocator);
        foreach (var entry in entries)
            writer.Insert(entry.Tree.Target, entry.Key, rowPointer);
    }

    /// <summary>Takes <paramref name="entries"/> out of their indexes for the row that was at <paramref name="rowPointer"/>.</summary>
    public void Remove(IReadOnlyList<Entry> entries, int rowPointer)
    {
        if (entries.Count == 0) return;
        var writer = new IndexWriter(_file, _allocator);
        foreach (var entry in entries)
            writer.Remove(entry.Tree.Target, entry.Key, rowPointer);
    }

    // A value as a refusal shows it: whole when short, its beginning when long.
    private static string Shown(object? value)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (text.Length > 40) text = text.Substring(0, 40) + "...";
        return value is string ? $"'{text}'" : text;
    }

    private static string Describe(Tree tree) => tree.IsPrimaryKey ? "primary key" : $"index {tree.Name}";

    // An entry whose row is gone - one a version of this library that did not keep the index left behind - is
    // no other row's.
    private bool IsLiveRow(int rowPointer)
    {
        int pageNumber = RowPointer.Page(rowPointer);
        if (pageNumber <= 0 || pageNumber >= _file.PageCount) return false;
        byte[] page = _file.ReadPage(pageNumber);
        int row = RowPointer.Row(rowPointer);
        // A page of this table's: one given to another table since is not where a row of this one is.
        if (page[0] != JetFormat.PageTypeData
            || ByteUtil.GetInt(page, JetFormat.OffsetDataTdefPage) != _table.TdefPageNumber
            || row >= ByteUtil.GetShort(page, _file.Format.OffsetDataNumRows)) return false;
        return (ByteUtil.GetUShort(page, _file.Format.OffsetDataRowTable + row * JetFormat.SizeRowEntry) & 0x8000) == 0;
    }
}
