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
///
/// An index is taken at its word, as Access and Jackcess take one: what it holds are the table's rows. A file an
/// earlier version of this library wrote to can have rows its indexes do not hold, and for such a file a caller
/// sets <see cref="Database.VerifyIndexes"/>. Then rows are found through an index, for the relationships
/// <see cref="ForeignKeyEnforcer"/> keeps, only once the index has been seen to hold every row
/// (<see cref="HoldsEveryRow"/>), and a unique index is looked at the same way before a row is compared with it,
/// the rows it does not hold being compared too (<see cref="EnsureNoDuplicates"/>).
///
/// An entry that points at no row is not taken at its word, since Access fails a query that comes to one: a
/// version of this library that did not keep the index left them (#36). Such an entry is no row's duplicate, and
/// when an entry is added or taken out under a key such entries are under, they are taken out
/// (<see cref="TakeOutIfNoRows"/>). An entry that points at a row of the table is believed, as Access believes it,
/// whatever this library makes of the row's values, which it may not key as Access does: it makes a row with its
/// key a duplicate, and stays. Only with <see cref="Database.VerifyIndexes"/>, and where the row's key is one this
/// library makes as Access does (<see cref="IndexKeys.MatchesAccess"/>), is an entry under another key the row's
/// wrong one, moved to the row's own key. An index that is as its rows are has no such entry, and no row is read
/// to find out.
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
            Number          = first.IndexDataNumber;
            Name            = string.Join("/", sharing.Select(ix => ix.Name));
            Columns         = first.Columns;
            Target          = IndexWriter.Target.For(tdefPage, first);
            IsPrimaryKey    = sharing.Any(ix => ix.IsPrimaryKey);
            IsUnique        = sharing.Any(ix => ix.IsUnique);
            RefusesNulls    = IsPrimaryKey || sharing.Any(ix => ix.IsRequired);
            IgnoreNulls    = first.IgnoreNulls;
        }

        /// <summary>The tree's block among the definition's index blocks: what tells it from the table's other trees.</summary>
        public int                        Number       { get; }
        public string                     Name         { get; }
        public IReadOnlyList<IndexColumn> Columns      { get; }
        public IndexWriter.Target         Target       { get; }
        public bool                       IsPrimaryKey { get; }
        public bool                       IsUnique     { get; }
        public bool                       RefusesNulls { get; }
        public bool                       IgnoreNulls { get; }
    }

    /// <summary>A row's entry in one tree: its key, and whether a unique index compares it with other rows'.</summary>
    internal readonly struct Entry
    {
        internal Entry(Tree tree, byte[] key, bool hasNull, IReadOnlyList<object?> values, bool isCut = false, byte[]? earlierKey = null)
        {
            Tree       = tree;
            Key        = key;
            HasNull    = hasNull;
            Values     = values;
            IsCut      = isCut;
            EarlierKey = earlierKey;
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

        /// <summary>
        /// The key as versions of this library after the cut and before #56 cut it, when that is not
        /// <see cref="Key"/>: a key of several columns that ends in another type than text after text that filled
        /// the entry (<see cref="IndexKeys.TruncateAsEarlierVersions"/>). Null for any other key. It is also the
        /// key Access gives another value, so an entry under it is this value's only when the row it points at
        /// has this value (<see cref="TableIndexes.IsEarlierOnly"/>).
        /// </summary>
        internal byte[]? EarlierKey { get; }
    }

    internal IReadOnlyList<Tree> Trees => _trees ??= _table.Indexes
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
        if (WhyNotKept() is { } why) throw new NotSupportedException(why);
    }

    /// <summary>
    /// What <see cref="EnsureCanBeKept"/> refuses the table with, or null when every index can be kept. It is
    /// worked out from the table's definition as it was read: nothing of the file is read or changed.
    /// </summary>
    public string? WhyNotKept()
    {
        if (_table.HasUnreadIndexes)
            return $"{_table.Name}'s indexes cannot be kept: they were not all read from its definition, which ends before " +
                   "they do or names an index column or block it has not. The table is left as it was.";
        foreach (var tree in Trees)
        {
            if (tree.Columns.Count == 0 || tree.Target.RootPage <= 0)
                return $"{_table.Name}'s {Describe(tree)} cannot be kept: its definition gives it no column or no page. " +
                       "The table is left as it was.";
            foreach (var column in tree.Columns)
                if (!IndexKeys.CanEncode(column.Column))
                    return $"{_table.Name}'s {Describe(tree)} cannot be kept: {IndexKeys.WhyNot(column.Column)}. The table is left as it was.";
        }
        return null;
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
            var values = ValuesIn(tree, row, out int nulls);

            if (enforce && nulls > 0 && tree.RefusesNulls)
            {
                var missing = tree.Columns.Where((_, i) => values[i] is null).Select(c => c.Column.Name);
                throw new ConstraintViolationException(
                    $"{_table.Name}'s {Describe(tree)} does not take a null: the row has none for {string.Join(", ", missing)}. " +
                    "The table is left as it was.");
            }
            if (nulls == values.Length && tree.IgnoreNulls) continue;

            // A value Access keys by the characters around it has no key here, whichever index it is in.
            for (int i = 0; i < values.Length; i++)
                if (!IndexKeys.CanEncode(tree.Columns[i].Column, values[i]))
                    throw new NotSupportedException(
                        $"{_table.Name}'s {Describe(tree)} cannot be kept: {IndexKeys.WhyNot(tree.Columns[i].Column, values[i])}. " +
                        "The table is left as it was.");

            entries.Add(EntryOf(tree, values, nulls > 0));
        }
        return entries;
    }

    /// <summary>
    /// The entry of a row with <paramref name="values"/> in <paramref name="tree"/>. Its key is as an entry holds
    /// it: one longer than Access keeps is cut, and ends in a digest of the rest (<see cref="IndexKeys.Truncate"/>),
    /// so it is never longer than an index page takes; and for a cut key, the key earlier versions cut it to, when
    /// that is another (<see cref="Entry.EarlierKey"/>).
    /// </summary>
    private static Entry EntryOf(Tree tree, IReadOnlyList<object?> values, bool hasNull)
    {
        byte[] key = IndexKeys.Encode(tree.Columns, values);
        if (key.Length < IndexKeys.MaxKeyLength) return new Entry(tree, key, hasNull, values);
        byte[] whole = IndexKeys.EncodeWhole(tree.Columns, values);
        if (whole.Length <= IndexKeys.MaxKeyLength) return new Entry(tree, key, hasNull, values);
        byte[] earlier = IndexKeys.TruncateAsEarlierVersions(whole);
        return new Entry(tree, key, hasNull, values, isCut: true, earlier.AsSpan().SequenceEqual(key) ? null : earlier);
    }

    /// <summary><paramref name="row"/>'s values for the tree's columns, in their order, and how many of them are null.</summary>
    private static object?[] ValuesIn(Tree tree, Row row, out int nulls)
    {
        var values = new object?[tree.Columns.Count];
        nulls = 0;
        for (int i = 0; i < values.Length; i++)
        {
            var column = tree.Columns[i].Column;
            // The row's values as the row encoder reads them (RowValues): DBNull is no value, and a Yes/No
            // column holds no null - whatever it is given stands for Yes or No.
            values[i] = row.TryGetValue(column.Name, out var value) && !RowValues.IsNull(value) ? value : null;
            if (column.DataType == DataType.Boolean) values[i] = RowValues.ToBoolean(values[i]);
            else if (values[i] is null) nulls++;
        }
        return values;
    }

    /// <summary>
    /// Refuses <paramref name="entries"/>, a row's about to be written, when another row has one of their keys in
    /// a unique index. <paramref name="replacedRowPointer"/> is the row an update is replacing, whose own entries
    /// are not another row's. Returns the writer that looked, when one did and <paramref name="forEntry"/> asks
    /// for it: it holds the pages it read, which <see cref="Add"/> then reads no second time - so a unique index
    /// costs an insert no more page reads than one that is not.
    /// <para>
    /// With <see cref="Database.VerifyIndexes"/> the index is not taken at its word
    /// (qcatco/exportmdb.interop#73): the rows it was seen not to hold (<see cref="RowsNotHeldBy"/>) are asked as
    /// well, since a second row with the values of one of them is a duplicate the index knows nothing of. Access
    /// adds such a row, and when the file is next compacted cannot build the index, and leaves the table without
    /// it. Seeing costs the first row a session checks one reading of the table, for all its unique indexes
    /// together, and no row after it anything. Without it nothing is read but the index.
    /// </para>
    /// </summary>
    public IndexWriter? EnsureNoDuplicates(IReadOnlyList<Entry> entries, int? replacedRowPointer = null, bool forEntry = false)
    {
        if (_file.VerifyIndexes)
        {
            List<Tree>? unseen = null;
            foreach (var entry in entries)
                if (entry.Tree.IsUnique && !entry.HasNull && !_file.IndexesChecked.ContainsKey(Which(entry.Tree)))
                    (unseen ??= new List<Tree>()).Add(entry.Tree);
            if (unseen is not null) Look(unseen);
        }

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
            var notHeld = RowsNotHeldBy(tree);
            foreach (var (stored, holder) in StoredUnder(writer, entry))
            {
                if (holder == replacedRowPointer || !IsDuplicateOf(entry, stored, holder)) continue;
                throw new ConstraintViolationException(DuplicateIn(entry) + " The table is left as it was.");
            }
            // A row a version from db6f93b entered under the key of the first form of the date it read
            // (EarlierVersionsEntriesOf) is a duplicate when it holds these very bytes: the rows under that key are
            // read to see. Not the key of no value, under which every row with no value is.
            foreach (var earlier in EarlierVersionsEntriesOf(entry, asFrom2930d3f: false))
                foreach (var (_, holder) in StoredUnder(writer, earlier))
                    if (holder != replacedRowPointer && HoldsKey(tree, holder, entry.Key))
                        throw new ConstraintViolationException(DuplicateIn(entry) + " The table is left as it was.");
            if (notHeld.AnotherHas(entry.Key, replacedRowPointer))
                throw new ConstraintViolationException(DuplicateIn(entry) + " The table is left as it was.");
        }
        return forEntry ? writer : null;
    }

    /// <summary>What is said of a row whose <paramref name="entry"/> is a duplicate in its unique index, by whoever found it one.</summary>
    internal string DuplicateIn(Entry entry)
        => $"The row would be a duplicate in {_table.Name}'s {Describe(entry.Tree)}, which is unique: another row holds " +
           $"{string.Join(", ", entry.Tree.Columns.Select((c, i) => $"{Shown(entry.Values[i])} for {c.Column.Name}"))}.";

    // ── Finding rows ─────────────────────────────────────────────────────────

    /// <summary>
    /// The row pointers an index on exactly <paramref name="columns"/> - by their names as the table spells them - in
    /// that order, holds for
    /// <paramref name="values"/> - Access gives a relationship's foreign key such an index, and a primary key is
    /// one - or null when the table has no such index that can be searched: none on those columns, one whose keys
    /// this library does not make, one that holds no entry for a key that is all null, a value this library
    /// makes no key for (<see cref="IndexKeys.CanEncode(Column, object?)"/>), a key it may not make as Access
    /// does (<see cref="IndexKeys.MatchesAccess"/>), or one that does not hold every row
    /// (<see cref="HoldsEveryRow"/>). Each pointer is an
    /// entry, not yet a row: the row it points at is the caller's to read and compare, as an index's text key
    /// finds 'ABC' for 'abc' and an entry may outlive its row in a file an older version of this library wrote.
    /// </summary>
    internal IEnumerable<int>? RowPointersFor(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        if (_table.HasUnreadIndexes) return null;
        var tree = Trees.FirstOrDefault(t =>
            t.Target.RootPage > 0
            && t.Columns.Count == columns.Count
            && t.Columns.Select(c => c.Column.Name).SequenceEqual(columns, StringComparer.Ordinal)
            && t.Columns.All(c => IndexKeys.CanEncode(c.Column)));
        if (tree is null || (tree.IgnoreNulls && values.All(v => v is null))) return null;

        Entry entry;
        try
        {
            // A search that misses says there is no such row. A value Access keys by its neighbours has no key
            // to search by: the caller reads the table for it.
            for (int i = 0; i < values.Count; i++)
                if (!IndexKeys.CanEncode(tree.Columns[i].Column, values[i])) return null;
            // Nor has a key this library may not make as Access does - one cut in an index of columns the cut was
            // not measured in - which may be under another key than this library's: a search for it could miss the row.
            if (!IndexKeys.MatchesAccess(tree.Columns, values)) return null;

            // A key that is cut is looked for under every form an entry of it can have - cut, or whole or cut
            // otherwise as earlier versions wrote it (StoredUnder) - so a row whose entry is one of those is not missed.
            entry = EntryOf(tree, values, hasNull: false);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return Array.Empty<int>();   // not a value the index's columns can hold: no row has it
        }
        return RowPointersFor(entry);
    }

    /// <summary>
    /// The row pointers <paramref name="entry"/>'s index holds under its key - the rows a unique index compares it
    /// with - or null when the index does not hold every row (<see cref="HoldsEveryRow"/>), or the key is one this
    /// library may not make as Access does (<see cref="IndexKeys.MatchesAccess"/>): the caller then reads the table.
    /// A row a version from db6f93b entered under the key of the first form of the date it read
    /// (<see cref="EarlierVersionsEntriesOf"/>) is among them, and so is any other row under that key: as every row
    /// given, it is the caller's to read and compare. The key of no value, which versions from 2930d3f made of
    /// Access's second form and every version that keyed the date it read made of the third layout, is not looked
    /// under: every row with no value is under it, and each would be read.
    /// </summary>
    internal IEnumerable<int>? RowPointersFor(Entry entry)
    {
        if (!HoldsEveryRow(entry.Tree) || !IndexKeys.MatchesAccess(entry.Tree.Columns, entry.Values)) return null;
        var writer = new IndexWriter(_file, _allocator);
        return StoredUnder(writer, entry)
            .Where(stored => !IsEarlierOnly(entry, stored.Key) || HoldsKey(entry.Tree, stored.RowPointer, entry.Key))
            .Concat(EarlierVersionsEntriesOf(entry, asFrom2930d3f: false).SelectMany(earlier => StoredUnder(writer, earlier)))
            .Select(stored => stored.RowPointer);
    }

    // ── Whether an index holds every row ─────────────────────────────────────

    /// <summary>
    /// Whether <paramref name="tree"/> has an entry for every row of the table that a search of it could be for:
    /// what makes "the index has no such row" mean that the table has none. Access takes that on trust, and
    /// Jackcess, and so does this library unless <see cref="Database.VerifyIndexes"/> is set. Then it is seen
    /// once for each index, the first time rows are looked for through it, and remembered while the database is
    /// open (qcatco/exportmdb.interop#64).
    /// <para>
    /// Versions of this library before it kept every index (those in ExportMdb.Interop up to 2.1.2) kept a table's
    /// primary key and no other index. A row one of them inserted has no entry in the others; a row one of them
    /// updated has, in each, the entry of the row it was, where that was; a row one of them deleted left its
    /// entries behind. An entry too many does no harm - whoever follows it reads the row - but a row with no
    /// entry is not found, and a count does not show it: after such an update an index has as many entries as the
    /// table has rows. Nor does an index Access alone has written always have exactly one entry a row. So each
    /// row is looked at: the table is read once, for the index's columns, and every row's key must be in the
    /// index, pointing at that row.
    /// </para>
    /// <para>
    /// A row needs no entry when a search could not be for it: its columns all null in an index that ignores
    /// nulls, or a value this library makes no key for -
    /// <see cref="RowPointersFor(IReadOnlyList{string}, IReadOnlyList{object?})"/> answers neither through the
    /// index. What this library writes afterwards keeps the index as it found it, a complete one complete: every
    /// row it inserts or rewrites gains its entries. The rows an index was found not to hold are kept
    /// (<see cref="RowsNotHeld"/>), and one leaves them when it is deleted or rewritten here: an index holds
    /// every row once none of them is left.
    /// </para>
    /// </summary>
    internal bool HoldsEveryRow(Tree tree) => RowsNotHeldBy(tree).Count == 0;

    /// <summary>
    /// The rows <paramref name="tree"/> does not hold, as they were seen when the index was first looked at in
    /// this session and as this library's own changes have left them since; none, with nothing read, for an
    /// index that is taken at its word.
    /// </summary>
    private RowsNotHeld RowsNotHeldBy(Tree tree)
    {
        if (!_file.VerifyIndexes) return RowsNotHeld.None;
        if (!_file.IndexesChecked.ContainsKey(Which(tree))) Look(new[] { tree });
        return _file.IndexesChecked[Which(tree)];
    }

    private (int TdefPage, int Index) Which(Tree tree) => (_table.TdefPageNumber, tree.Number);

    /// <summary>Reads the table once, and each of <paramref name="trees"/>, for the rows each does not hold.</summary>
    private void Look(IReadOnlyList<Tree> trees)
    {
        // The key each row should be under in each index, by where the row is: every page of the table read once,
        // and of each row only the indexes' columns, as their keys are made of them (RowDecoder.DecodeForKey)...
        var format  = _file.Format;
        var decoder = new RowDecoder(format, _table.Columns, _table.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null);
        var columns = trees.SelectMany(tree => tree.Columns).Select(c => c.Column).Distinct().ToList();
        // Room for every row at once, by the definition's count of them (kept within reason, should it be wrong).
        int rows    = Math.Min(Math.Max(ByteUtil.GetInt(_file.ReadPage(_table.TdefPageNumber), format.TdefOffsetNumRows), 0), 1 << 24);
        var unfound = trees.Select(_ => new Dictionary<int, byte[]>(rows)).ToList();
        // The rows a tree is not asked about: their columns all null in an index that ignores nulls, or a value no
        // key is made for. An entry that points at one is left as it is.
        var unasked = trees.Select(_ => new HashSet<int>()).ToList();
        // The rows whose key this library may not make as Access does (IndexKeys.MatchesAccess): an entry that points
        // at one is believed, whatever its key, but the row is held only when its entry is under this library's
        // key, which a search makes - so that an index with such a row under another key is not searched.
        var believed = trees.Select(_ => new HashSet<int>()).ToList();
        // By the columns' names exactly: a column with no value has no entry, and no other column's is found for it.
        var row     = new Row(columns.Count, _table.Names, exactOnly: true);
        foreach (int pageNumber in UsageMap.GetOwnedPages(_file.ReadPage(_table.UmapPageNumber), _table.OwnedPagesRow, format, _file))
        {
            byte[] page = _file.ReadPage(pageNumber);
            int rowCount = ByteUtil.GetShort(page, format.OffsetDataNumRows);
            for (int r = 0; r < rowCount; r++)
            {
                if (DataRows.Read(_file, page, r) is not { } rowBytes) continue;

                row.Clear();
                foreach (var column in columns)
                    if (decoder.DecodeForKey(rowBytes, column) is { } value)
                        row.Put(column.Name, value);
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree   = trees[t];
                    var values = ValuesIn(tree, row, out int nulls);
                    bool keyed = !(nulls == values.Length && tree.IgnoreNulls);
                    for (int i = 0; keyed && i < values.Length; i++)
                        keyed = IndexKeys.CanEncode(tree.Columns[i].Column, values[i]);
                    int pointer = RowPointer.Pack(pageNumber, r);
                    if (!keyed) { unasked[t].Add(pointer); continue; }
                    unfound[t][pointer] = IndexKeys.Encode(tree.Columns, values);
                    if (!IndexKeys.MatchesAccess(tree.Columns, values)) believed[t].Add(pointer);
                }
            }
        }

        // ...and each index's entries, from first to last: each is the entry of the row it points at, when that
        // row has its key, and otherwise no row's. A long key may be there whole, as earlier versions wrote it
        // (StoredUnder); it is the row's when, cut, it is. One cut with an earlier version's digest is not (KeysMatch).
        for (int t = 0; t < trees.Count; t++)
        {
            var held  = new HashSet<int>();
            var noRow = new List<(byte[] Key, int RowPointer)>();
            var entries = new IndexWriter(_file, _allocator).EntriesWithPrefix(trees[t].Target.RootPage, Array.Empty<byte>());
            foreach (var (key, pointer) in entries)
            {
                if (unfound[t].TryGetValue(pointer, out byte[]? expected) ? KeysMatch(key, expected) : unasked[t].Contains(pointer))
                    held.Add(pointer);
                else if (!believed[t].Contains(pointer))
                    noRow.Add((key, pointer));
            }
            foreach (int pointer in held) unfound[t].Remove(pointer);
            _file.NoteIndexChecked(Which(trees[t]), new RowsNotHeld(unfound[t], noRow));
        }
    }

    /// <summary>
    /// The entries the tree holds for <paramref name="entry"/>'s key, each with the key it is stored under. For a
    /// key that was not cut, those under that key. For one that was, a file can hold four kinds of entry, and
    /// each is one of that key's: Access's, cut at 510 bytes and ended in a digest; this library's, the same
    /// bytes; one written whole, by a version of this library before it cut keys or by Jackcess before it
    /// did; and one cut by a version after that and before #56, whose digest differs from Access's for a key of
    /// several columns that ends in another type than text (<see cref="Entry.EarlierKey"/>). A whole key begins as
    /// its cut forms do, so all are found among the entries that begin with what the cut kept, and a whole one is
    /// this key's when cutting it gives this key - whether it is the same value's or another's that is cut to the
    /// same entry, which Access holds to be the same key. An entry cut the earlier way is found when it is the key
    /// the earlier way gives this value. That key is Access's for another value too - the earlier digest is
    /// Access's with the last byte cut off taken as 0 - so an entry found by it alone (<see cref="IsEarlierOnly"/>)
    /// can be another row's own: before it is counted as a duplicate or followed to a row, the row it points at is
    /// read and its key made (<see cref="HoldsKey"/>); a removal takes it only for the row being removed.
    /// </summary>
    internal static IEnumerable<(byte[] Key, int RowPointer)> StoredUnder(IndexWriter writer, Entry entry)
    {
        int root = entry.Tree.Target.RootPage;
        if (!entry.IsCut)
            return writer.RowPointersForKey(root, entry.Key).Select(pointer => (entry.Key, pointer));

        byte[] kept = entry.Key.Slice(0, IndexKeys.MaxKeyLength - 2);
        byte[] key = entry.Key;
        byte[]? earlier = entry.EarlierKey;
        return writer.EntriesWithPrefix(root, kept).Where(stored =>
            stored.Key.Length > IndexKeys.MaxKeyLength
                ? IndexKeys.Truncate(stored.Key).AsSpan().SequenceEqual(key)
                : stored.Key.AsSpan().SequenceEqual(key) || earlier is not null && stored.Key.AsSpan().SequenceEqual(earlier));
    }

    /// <summary>
    /// Adds <paramref name="entries"/> to their indexes for the row at <paramref name="rowPointer"/>, through
    /// <paramref name="writer"/> when <see cref="EnsureNoDuplicates"/> returned one for them.
    /// </summary>
    public void Add(IReadOnlyList<Entry> entries, int rowPointer, IndexWriter? writer = null)
    {
        if (entries.Count == 0) return;
        writer ??= new IndexWriter(_file, _allocator);
        TakeOutEntriesSeenToBeNoRows(writer);
        foreach (var entry in entries)
        {
            // Another entry under the key of a row a unique index compares points at no row, or - the index
            // verified - is another row's wrong one, or the row would have been refused: it goes
            // (TakeOutIfNoRows), or Access would not find the row through the index.
            bool another = writer.Insert(entry.Tree.Target, entry.Key, rowPointer);
            if (ComparesRows(entry) && (another || entry.IsCut))
                TakeOutEntriesOfNoRow(writer, entry, rowPointer);
        }
    }

    /// <summary>
    /// Refuses, before anything is written, a change that must take <paramref name="entries"/>, the entries of the row
    /// at <paramref name="rowPointer"/>, out of their indexes, when an index taken at its word does not hold one of
    /// them under the row's key: a damaged index, which an earlier version of this library or another program can
    /// leave. Access refuses such an update of the row's key, and such a delete, with "The search key was not found
    /// in any record", and changes nothing; an update of its other columns, which takes no entry out, it makes
    /// (measured with ACE 16 and DAO, #81). Without the refusal the row's new entry was added beside its wrong one,
    /// the row was in the index twice, and DAO's compaction copied it twice and dropped the primary key.
    /// <para>
    /// Returns the writer that looked, which keeps the pages it read: <see cref="Remove"/> given it goes down the
    /// same pages again without reading them, so a change in a sound index reads no more pages for the check. With
    /// <see cref="Database.VerifyIndexes"/> nothing is looked at here and null is returned: the change mends the
    /// index (#66). An entry this library may not key as Access does - a key cut in an index of columns the cut was
    /// not measured in (<see cref="IndexKeys.MatchesAccess"/>), a Date/Time Extended value an earlier version keyed
    /// otherwise (<see cref="EarlierVersionsEntriesOf"/>) - is not judged, and <see cref="Remove"/> looks for it as
    /// it always has; nor is one under a node of the form earlier versions wrote, which only a change rewrites.
    /// </para>
    /// </summary>
    /// <exception cref="NotSupportedException">An index does not hold one of the row's entries. Nothing was changed.</exception>
    public IndexWriter? EnsureHeld(IReadOnlyList<Entry> entries, int rowPointer)
    {
        if (_file.VerifyIndexes || entries.Count == 0) return null;
        var writer = new IndexWriter(_file, _allocator);
        writer.RememberPagesRead();
        foreach (var entry in entries)
        {
            bool held = entry.IsCut
                ? StoredUnder(writer, entry).Any(stored => stored.RowPointer == rowPointer)
                : writer.Holds(entry.Tree.Target, entry.Key, rowPointer) != false;
            if (held || !IndexKeys.MatchesAccess(entry.Tree.Columns, entry.Values)
                     || EarlierVersionsEntriesOf(entry, asFrom2930d3f: true).Any()) continue;
            throw new NotSupportedException(
                $"{_table.Name}'s {Describe(entry.Tree)} does not hold the row under its key, " +
                $"{string.Join(", ", entry.Tree.Columns.Select((c, i) => $"{Shown(entry.Values[i])} for {c.Column.Name}"))}: " +
                "the index is damaged, or an earlier version of this library left the row out of it, and Access refuses the change as " +
                "this library does (\"The search key was not found in any record\"). Setting Database.VerifyIndexes makes the change " +
                "and mends the index. Nothing was changed.");
        }
        return writer;
    }

    /// <summary>
    /// Takes <paramref name="entries"/> out of their indexes for the row that was at <paramref name="rowPointer"/>,
    /// through <paramref name="writer"/> when <see cref="EnsureHeld"/> returned one for them.
    /// </summary>
    public void Remove(IReadOnlyList<Entry> entries, int rowPointer, IndexWriter? writer = null)
    {
        if (entries.Count == 0) return;
        writer ??= new IndexWriter(_file, _allocator);
        TakeOutEntriesSeenToBeNoRows(writer);
        foreach (var entry in entries)
        {
            // Gone, or about to be entered where it now is, the row is not one its index lacks any longer.
            if (_file.IndexesChecked.TryGetValue(Which(entry.Tree), out var notHeld) && notHeld.Forget(rowPointer) is { } key)
                _file.Journal(() => notHeld.PutBack(rowPointer, key));

            // The entry goes under the key it is stored under: for a key that was cut, the cut key, the whole one or
            // the one cut the earlier way (StoredUnder), whichever holds this row - the one that points at it, whose
            // values the entry is made of, so an entry under the earlier way's key is never another value's row's.
            bool found, another;
            if (entry.IsCut)
            {
                var stored = StoredUnder(writer, entry).Where(s => s.RowPointer == rowPointer).Select(s => s.Key).ToList();
                found = stored.Count > 0;
                another = found && writer.Remove(entry.Tree.Target, stored[0], rowPointer);
            }
            else another = writer.Remove(entry.Tree.Target, entry.Key, rowPointer, out found);

            if (!found)
            {
                // A row with a Date/Time Extended value in another form than the one this library writes can be
                // under the key an earlier version of this library made of the date it read (#51): its entry is looked
                // for there. A row whose key this library may not make as Access does - one cut in an index of
                // columns the cut was not measured in - can be under a key this library does not make: when its entry
                // is not under its own key, its entries are found by the row they point at, every leaf of the index
                // read, so that none is left pointing where the row was. Any other row has no entry under its key only
                // where the index is not taken at its word (Database.VerifyIndexes): with the index trusted, EnsureHeld
                // refused the change before anything was written, as Access refuses it (#81) - a row an earlier version
                // of this library wrote without keeping the index among them. Verified, such a row may have no entry at
                // all, which leaves nothing to take out, or one under another key: the index is looked at, once in the
                // session, and the entries it holds that are no row's - this row's wrong one among them, the row being
                // gone or rewritten - go.
                bool removed = RemoveAsEarlierVersionsKeyedIt(writer, entry, rowPointer);
                if (!removed && !IndexKeys.MatchesAccess(entry.Tree.Columns, entry.Values))
                    RemoveByPointer(writer, entry.Tree, rowPointer);
                else if (!removed && _file.VerifyIndexes && !_file.IndexesChecked.ContainsKey(Which(entry.Tree)))
                {
                    Look(new[] { entry.Tree });   // a look part-way through a change is forgotten if it is taken back
                    TakeOutEntriesSeenToBeNoRows(writer);
                }
                continue;
            }
            if (ComparesRows(entry) && (another || entry.IsCut))
                TakeOutEntriesOfNoRow(writer, entry, rowPointer);
        }
    }

    /// <summary>
    /// The entries a version of this library before #51 made for a row with <paramref name="entry"/>'s values, where
    /// they are not under <paramref name="entry"/>'s key: those of a row with a Date/Time Extended value in another
    /// form than the first. Those versions keyed the date they read, in the first form: from 2930d3f they read Access's
    /// second form as no value, and from db6f93b as its date (<see cref="StoredExtendedDate.DateAsEarlierVersionsRead"/>).
    /// Each of them read the layout a DAO table-type recordset writes as no value.
    /// An index or a primary key they added over such a row, and a row they rewrote, is under that key
    /// (tools/earlier-extended-date-keys.ps1). None for values in the first form, which they keyed as this library
    /// does; the key of the date read; and, with <paramref name="asFrom2930d3f"/>, the key of no value too. The key of
    /// a date in the first form is also the key of a row that has the date in that form: a row found under it is
    /// another row's but where it holds the very values looked for. The key of no value is every row's with no value,
    /// so it is looked under only for the row an entry points at (<see cref="RemoveAsEarlierVersionsKeyedIt"/>), never
    /// for the rows a search is for: those versions were on <c>main</c> from 2930d3f to db6f93b, ten hours, and never
    /// released.
    /// </summary>
    internal static IEnumerable<Entry> EarlierVersionsEntriesOf(Entry entry, bool asFrom2930d3f)
    {
        var looked = new List<byte[]> { entry.Key };
        foreach (var values in StoredExtendedDate.AsEarlierVersionsRead(entry.Values, asFrom2930d3f))
        {
            if (entry.Tree.IgnoreNulls && values.All(value => value is null)) continue;

            var earlier = EntryOf(entry.Tree, values, values.Any(value => value is null));
            if (looked.Any(key => key.AsSpan().SequenceEqual(earlier.Key))) continue;
            looked.Add(earlier.Key);
            yield return earlier;
        }
    }

    /// <summary>
    /// Takes out of <paramref name="entry"/>'s tree the entry an earlier version made for the row at
    /// <paramref name="rowPointer"/> (<see cref="EarlierVersionsEntriesOf"/>), the key of no value among them, when its
    /// entry is not under the key of its bytes. Only this row's entry is taken out. Each key is looked under for this
    /// row's entry, one way down the tree to the key and the row: the rows under it are not read. (A key that was cut
    /// is looked for among the entries that begin with what the cut kept, as every cut key's entry is - the rows with
    /// the same values up to the cut.) Returns whether an entry was taken out.
    /// </summary>
    private static bool RemoveAsEarlierVersionsKeyedIt(IndexWriter writer, Entry entry, int rowPointer)
    {
        foreach (var earlier in EarlierVersionsEntriesOf(entry, asFrom2930d3f: true))
        {
            if (!earlier.IsCut)
            {
                writer.Remove(entry.Tree.Target, earlier.Key, rowPointer, out bool removed);
                if (removed) return true;
                continue;
            }
            // A cut key may be stored whole or cut the earlier way too (StoredUnder): the form that points at this row.
            if (StoredUnder(writer, earlier).Where(s => s.RowPointer == rowPointer).Select(s => s.Key).FirstOrDefault() is { } stored)
            {
                writer.Remove(entry.Tree.Target, stored, rowPointer);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Takes every entry of <paramref name="tree"/> that points at <paramref name="rowPointer"/> out of it,
    /// whatever its key: each of the index's leaves is read.
    /// </summary>
    private static void RemoveByPointer(IndexWriter writer, Tree tree, int rowPointer)
    {
        foreach (byte[] key in writer.EntriesWithPrefix(tree.Target.RootPage, Array.Empty<byte>())
                                     .Where(e => e.RowPointer == rowPointer).Select(e => e.Key).ToList())
            writer.Remove(tree.Target, key, rowPointer);
    }

    // ── Entries that are no row's ────────────────────────────────────────────

    /// <summary>Whether a unique index compares <paramref name="entry"/>'s row with others: a row with a null in the key is no row's duplicate.</summary>
    private static bool ComparesRows(Entry entry) => entry.Tree.IsUnique && !entry.HasNull;

    /// <summary>
    /// Takes out of <paramref name="entry"/>'s tree the entries under its key that are no row's, other than the
    /// one for <paramref name="rowPointer"/>. Called when an entry was added or taken out under a key that a
    /// unique index compares and the tree was seen to hold another under it: an index that is as its rows are
    /// has none, so it costs such an index nothing. A key that was cut can also be in the tree whole, as earlier
    /// versions wrote it, where it is not beside the cut one: the entries under such a key are always looked at,
    /// in every form they can have (<see cref="StoredUnder"/>), which reads the pages the entry's own way down read.
    /// </summary>
    private void TakeOutEntriesOfNoRow(IndexWriter writer, Entry entry, int rowPointer)
    {
        foreach (var (key, pointer) in StoredUnder(writer, entry).ToList())
            if (pointer != rowPointer) TakeOutIfNoRows(writer, entry.Tree, key, pointer);
    }

    /// <summary>
    /// With <see cref="Database.VerifyIndexes"/>, takes out the entries each index of the table was seen to hold
    /// that are no row's (<see cref="RowsNotHeld.TakeEntriesOfNoRow"/>), before the table's indexes are next
    /// changed: Access fails a query that comes to one.
    /// </summary>
    private void TakeOutEntriesSeenToBeNoRows(IndexWriter writer)
    {
        if (!_file.VerifyIndexes) return;
        foreach (var tree in Trees)
        {
            if (!_file.IndexesChecked.TryGetValue(Which(tree), out var seen)) continue;
            var taken = seen.TakeEntriesOfNoRow();
            // Taken out by a change that is taken back, they are in the index again (PageFile.Journal).
            if (taken.Count > 0) _file.Journal(() => seen.PutBackEntriesOfNoRow(taken));
            foreach (var (key, pointer) in taken)
                TakeOutIfNoRows(writer, tree, key, pointer);
        }
    }

    /// <summary>
    /// Takes the entry (<paramref name="key"/>, <paramref name="pointer"/>) out of <paramref name="tree"/> when it
    /// is no row's: it points at no row - a slot deleted or never used, or a page that is not one of the table's.
    /// Access fails a query that comes to such an entry ("Not a valid bookmark").
    /// <para>
    /// An entry that points at a row of the table is believed, as Access believes it, whatever this library makes
    /// of the row's values: its key for them may not be the one Access wrote, and an entry Access wrote taken out
    /// on that judgement is a row Access can then neither find nor update. Only with
    /// <see cref="Database.VerifyIndexes"/>, and where the row's key is one this library makes as Access does
    /// (<see cref="IndexKeys.MatchesAccess"/>), is an entry under another key the row's wrong one: it goes, and the
    /// row is entered under its own key, so that it is not left without an entry for having had a wrong one.
    /// </para>
    /// <para>
    /// All of it or none: the entry goes and the row's own comes inside one undo scope (the one already open,
    /// when a larger change has one), so a failure part-way leaves the index as it was.
    /// </para>
    /// </summary>
    private void TakeOutIfNoRows(IndexWriter writer, Tree tree, byte[] key, int pointer)
    {
        var row = RowAt(tree, pointer);
        if (IsItsRows(row, key) || row.IsRow && !_file.VerifyIndexes) return;

        var entered = new List<int>();
        if (_file.IsUndoing) Move(writer, tree, key, pointer, row, entered, depth: 0);
        else
        {
            using var undo = _file.BeginUndo();
            try
            {
                Move(writer, tree, key, pointer, row, entered, depth: 0);
                undo.Commit();
            }
            catch
            {
                writer.ForgetPagesRead();   // what the writer kept of the pages is not what the file holds again
                throw;
            }
        }
        if (_file.IndexesChecked.TryGetValue(Which(tree), out var notHeld))
            foreach (int held in entered)
                if (notHeld.Forget(held) is { } had)
                    _file.Journal(() => notHeld.PutBack(held, had));
    }

    // The entries a row's own key can be chained through before the rest are left as they are: a file an earlier
    // version left has one or two.
    private const int MaxChainedEntries = 16;

    /// <summary>
    /// Takes the entry (<paramref name="key"/>, <paramref name="pointer"/>), which is not <paramref name="row"/>'s,
    /// out of the tree, and enters the row, when there is one, under its own key - which happens only with
    /// <see cref="Database.VerifyIndexes"/> (<see cref="TakeOutIfNoRows"/>). What is under that key already
    /// is looked at the same way first: an entry that points at no row goes, and one that points at a row with
    /// yet another key is moved in its turn (an earlier version could leave 1 pointing at the row that is 2 and 2
    /// at the row that is 3). An entry of a row that does have the key stays, and the row is entered beside it:
    /// the two rows are duplicates, which the file holds whatever the index says, and each is found by its values.
    /// The pointers of the rows entered are added to <paramref name="entered"/>.
    /// </summary>
    private void Move(IndexWriter writer, Tree tree, byte[] key, int pointer, Pointee row, List<int> entered, int depth)
    {
        writer.Remove(tree.Target, key, pointer);
        if (!row.IsRow || row.Own is not { } own || !row.HasEntry) return;

        bool there = false;
        foreach (var (otherKey, other) in StoredUnder(writer, own).ToList())
        {
            if (other == pointer) { there = true; continue; }
            if (!ComparesRows(own)) continue;   // entries under one key are this tree's rows: none is looked at

            var others = RowAt(tree, other);
            if (IsItsRows(others, otherKey)) continue;
            if (depth < MaxChainedEntries) Move(writer, tree, otherKey, other, others, entered, depth + 1);
        }
        if (!there) writer.Insert(tree.Target, own.Key, pointer);
        entered.Add(pointer);
    }

    /// <summary>
    /// Whether an entry under <paramref name="key"/> is the entry of <paramref name="row"/>, which it points at: a
    /// row that has that key, or one whose key this library does not make as Access does, which is believed.
    /// </summary>
    private static bool IsItsRows(Pointee row, byte[] key)
        => row.IsRow && (row.Own is not { } own || KeysMatch(key, own.Key));

    /// <summary>
    /// Whether the entry (<paramref name="key"/>, <paramref name="pointer"/>), stored under <paramref name="entry"/>'s
    /// key in a unique index, makes the row about to be written a duplicate. It does when it points at a row of the
    /// table: Access trusts its index and refuses the row, and so does this library, whatever it makes of that row's
    /// values - its key for them may not be the one Access wrote. With <see cref="Database.VerifyIndexes"/> it does
    /// not when that row's key is one this library makes as Access does (<see cref="IndexKeys.MatchesAccess"/>) and
    /// is another: the entry is then the row's wrong one, and is moved to the row's own key when the table is next
    /// changed. An entry that points at no row makes no row a duplicate. Nor does one found only by the key the
    /// earlier digest gives (<see cref="IsEarlierOnly"/>) unless the row it points at holds the new row's key: that
    /// key is also Access's for another value, a row of which is no duplicate.
    /// </summary>
    private bool IsDuplicateOf(Entry entry, byte[] key, int pointer)
    {
        if (!IsLiveRow(pointer)) return false;
        if (IsEarlierOnly(entry, key)) return HoldsKey(entry.Tree, pointer, entry.Key);
        return !_file.VerifyIndexes || IsItsRows(RowAt(entry.Tree, pointer), key);
    }

    /// <summary>
    /// Whether <paramref name="stored"/>, an entry's key that <see cref="StoredUnder"/> found for
    /// <paramref name="entry"/>, is only the key the earlier digest gives it (<see cref="Entry.EarlierKey"/>). The
    /// earlier digest left the last byte cut off out, so it is Access's digest of the same key with that byte 0: the
    /// key Access gives another value, whose row holds an entry under it that is its own.
    /// </summary>
    private static bool IsEarlierOnly(Entry entry, byte[] stored)
        => entry.EarlierKey is { } earlier && stored.AsSpan().SequenceEqual(earlier);

    /// <summary>
    /// Whether the row at <paramref name="pointer"/> has <paramref name="key"/> in <paramref name="tree"/>: the key
    /// made now of the values it holds. False for no row, and for values no key is made for or that cannot be read.
    /// </summary>
    private bool HoldsKey(Tree tree, int pointer, byte[] key)
        => IsLiveRow(pointer) && ValuesAt(tree, pointer, out _) is { } values
           && IndexKeys.Encode(tree.Columns, values).AsSpan().SequenceEqual(key);

    // A key as an entry holds it and a row's key, which is cut: a whole key an earlier version wrote is the row's
    // when, cut, it is. One an earlier version cut with another digest (Entry.EarlierKey) is not: Access cannot
    // update or delete a row under it, so with VerifyIndexes it is moved to the row's own key, as a row's wrong
    // entry is (#56).
    private static bool KeysMatch(byte[] stored, byte[] rowsKey)
        => (stored.Length > IndexKeys.MaxKeyLength ? IndexKeys.Truncate(stored) : stored).AsSpan().SequenceEqual(rowsKey);

    /// <summary>What an entry of a tree points at.</summary>
    private readonly struct Pointee
    {
        internal Pointee(bool isRow, Entry? own, bool hasEntry)
        {
            IsRow    = isRow;
            Own      = own;
            HasEntry = hasEntry;
        }

        /// <summary>False when no row of the table is there.</summary>
        internal bool   IsRow    { get; }

        /// <summary>
        /// The row's own entry in the tree, by its values; null when its key may not be the one Access writes: a
        /// value no key is made for, or a key this library does not make as Access does
        /// (<see cref="IndexKeys.MatchesAccess"/>), or values that cannot be read.
        /// </summary>
        internal Entry? Own      { get; }

        /// <summary>False for a row the tree holds no entry for: its columns all null in an index that ignores nulls.</summary>
        internal bool   HasEntry { get; }
    }

    private RowDecoder? _keyDecoder;

    /// <summary>
    /// The row at <paramref name="rowPointer"/>, as <paramref name="tree"/> would key it. An entry whose row is
    /// gone, or was written again in the same place with other values, is one a version of this library that did
    /// not keep the index left behind.
    /// </summary>
    private Pointee RowAt(Tree tree, int rowPointer)
    {
        if (!IsLiveRow(rowPointer)) return default;
        // A row whose key may not be Access's is a row all the same, and what the index holds for it is believed.
        var believed = new Pointee(isRow: true, own: null, hasEntry: true);
        if (ValuesAt(tree, rowPointer, out int nulls) is not { } values) return believed;
        if (!IndexKeys.MatchesAccess(tree.Columns, values)) return believed;

        return new Pointee(isRow: true, EntryOf(tree, values, nulls > 0),
                           hasEntry: !(nulls == values.Length && tree.IgnoreNulls));
    }

    /// <summary>
    /// The values of the row at <paramref name="rowPointer"/>, a live one, for <paramref name="tree"/>'s columns in
    /// their order, and how many of them are null; null when its bytes cannot be read or one is a value no key is
    /// made for.
    /// </summary>
    private object?[]? ValuesAt(Tree tree, int rowPointer, out int nulls)
    {
        nulls = 0;
        byte[] page = _file.ReadPage(RowPointer.Page(rowPointer));
        if (DataRows.Read(_file, page, RowPointer.Row(rowPointer)) is not { } rowBytes) return null;

        _keyDecoder ??= new RowDecoder(_file.Format, _table.Columns, _table.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null);
        var row = new Row(tree.Columns.Count, _table.Names, exactOnly: true);
        foreach (var column in tree.Columns)
            if (_keyDecoder.DecodeForKey(rowBytes, column.Column) is { } value)
                row.Put(column.Column.Name, value);

        var values = ValuesIn(tree, row, out nulls);
        for (int i = 0; i < values.Length; i++)
            if (!IndexKeys.CanEncode(tree.Columns[i].Column, values[i]))
                return null;
        return values;
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

    // Whether a row of this table is at a pointer: an entry whose row is gone - one a version of this library
    // that did not keep the index left behind - is no row's.
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
