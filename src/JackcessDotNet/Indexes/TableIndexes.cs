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
///   • a value in an indexed text or Memo column with a character Access keys by the characters around it,
///     for which this library makes no key (<see cref="IndexKeys.CanEncode(Column, object?)"/>);
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
        internal Entry(Tree tree, byte[] key, bool hasNull, IReadOnlyList<object?> values, bool isCut = false)
        {
            Tree    = tree;
            Key     = key;
            HasNull = hasNull;
            Values  = values;
            IsCut   = isCut;
        }

        /// <summary>The row's values for the tree's columns, in their order: what a refusal names.</summary>
        internal IReadOnlyList<object?> Values { get; }

        internal Tree   Tree    { get; }

        /// <summary>The key as Access keeps it, and as this library writes it: cut when it is longer than an entry holds.</summary>
        internal byte[] Key     { get; }
        internal bool   HasNull { get; }

        /// <summary>
        /// Whether <see cref="Key"/> was cut. The tree may then hold the row under that key or under the whole
        /// one, which versions of this library before the cut, and Jackcess before it, wrote
        /// (<see cref="TableIndexes.StoredUnder"/>).
        /// </summary>
        internal bool   IsCut   { get; }
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

            // A value Access keys by the characters around it has no key here, whichever index it is in.
            for (int i = 0; i < values.Length; i++)
                if (!IndexKeys.CanEncode(tree.Columns[i].Column, values[i]))
                    throw new NotSupportedException(
                        $"{_table.Name}'s {Describe(tree)} cannot be kept: {IndexKeys.WhyNot(tree.Columns[i].Column, values[i])}. " +
                        "The table is left as it was.");

            // The key as an entry holds it: one longer than Access keeps is cut, and ends in a digest of the rest
            // (IndexKeys.Truncate), so it is never longer than an index page takes.
            byte[] key = IndexKeys.Encode(tree.Columns, values);
            bool isCut = key.Length == IndexKeys.MaxKeyLength && IndexKeys.IsCut(tree.Columns, values);
            entries.Add(new Entry(tree, key, nulls > 0, values, isCut));
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
            foreach (var (_, holder) in StoredUnder(writer, entry))
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
    /// this library does not make, one that holds no entry for a key that is all null, or a value this
    /// library makes no key for (<see cref="IndexKeys.CanEncode(Column, object?)"/>). Each pointer is an
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

        Entry entry;
        try
        {
            // A search that misses says there is no such row. A value Access keys by its neighbours has no key
            // to search by: the caller reads the table for it.
            for (int i = 0; i < values.Count; i++)
                if (!IndexKeys.CanEncode(tree.Columns[i].Column, values[i])) return null;

            // A key that is cut is looked for under every form an entry of it can have - cut, or whole as
            // earlier releases wrote it (StoredUnder) - so a row whose entry is whole is not missed.
            byte[] key = IndexKeys.Encode(tree.Columns, values);
            bool isCut = key.Length == IndexKeys.MaxKeyLength && IndexKeys.IsCut(tree.Columns, values);
            entry = new Entry(tree, key, hasNull: false, values, isCut);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return Array.Empty<int>();   // not a value the index's columns can hold: no row has it
        }
        return RowPointersFor(entry);
    }

    /// <summary>The row pointers <paramref name="entry"/>'s index holds under its key: the rows a unique index compares it with.</summary>
    internal IEnumerable<int> RowPointersFor(Entry entry)
        => StoredUnder(new IndexWriter(_file, _allocator), entry).Select(stored => stored.RowPointer);

    /// <summary>
    /// The entries the tree holds for <paramref name="entry"/>'s key, each with the key it is stored under. For a
    /// key that was not cut, those under that key. For one that was, a file can hold three kinds of entry, and
    /// each is one of that key's: Access's, cut at 510 bytes and ended in a digest; this library's, the same
    /// bytes; and one written whole, by a version of this library before it cut keys or by Jackcess before it
    /// did. A whole key begins as its cut form does, so all are found among the entries that begin with what
    /// the cut kept, and a whole one is this key's when cutting it gives this key - whether it is the same
    /// value's or another's that is cut to the same entry, which Access holds to be the same key.
    /// </summary>
    internal static IEnumerable<(byte[] Key, int RowPointer)> StoredUnder(IndexWriter writer, Entry entry)
    {
        int root = entry.Tree.Target.RootPage;
        if (!entry.IsCut)
            return writer.RowPointersForKey(root, entry.Key).Select(pointer => (entry.Key, pointer));

        byte[] kept = entry.Key.Slice(0, IndexKeys.MaxKeyLength - 2);
        return writer.EntriesWithPrefix(root, kept).Where(stored =>
            stored.Key.Length > IndexKeys.MaxKeyLength
                ? IndexKeys.Truncate(stored.Key).AsSpan().SequenceEqual(entry.Key)
                : stored.Key.AsSpan().SequenceEqual(entry.Key));
    }

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
        {
            // The entry goes under the key it is stored under: for a key that was cut, the cut key or the whole
            // one (StoredUnder), whichever holds this row. A row with no entry at all - one an earlier version of
            // this library wrote without keeping the index - has nothing to take out, and that is no error: the
            // row is gone and the index holds nothing of it, which is what a delete is for.
            byte[] storedKey = entry.Key;
            if (entry.IsCut)
            {
                var stored = StoredUnder(writer, entry).Where(s => s.RowPointer == rowPointer).Select(s => s.Key).ToList();
                if (stored.Count == 0) continue;
                storedKey = stored[0];
            }
            writer.Remove(entry.Tree.Target, storedKey, rowPointer);
        }
    }

    // A value as a refusal shows it: whole when short, its beginning when long.
    private static string Shown(object? value)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (text.Length > 40) text = text.Substring(0, 40) + "...";
        return value is string ? $"'{text}'" : text;
    }

    /// <summary>
    /// Refuses, before a row with <paramref name="values"/> is deleted, a row with a value in an indexed column
    /// that no key is made for - one Access wrote, and keyed by the characters around it: its entries could not
    /// be taken out with it. Called by the one place a row is deleted, so the row stays, with its entries.
    /// </summary>
    internal static void EnsureEntriesCanBeRemoved(TableDefinition table, Row values)
    {
        foreach (var column in table.Indexes.SelectMany(ix => ix.Columns).Select(c => c.Column))
            if (values.TryGetValue(column.Name, out var value) && IndexKeys.CanEncode(column) && !IndexKeys.CanEncode(column, value))
                throw new NotSupportedException(
                    $"{table.Name}'s indexes cannot be kept: {IndexKeys.WhyNot(column, value)}. The table is left as it was.");
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
