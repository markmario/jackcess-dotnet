using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Adds entries to, and takes them out of, an index's B-tree the way Access writes
/// one, so Access reads the tree and can go on adding to it: every index of a table,
/// whoever made it (<see cref="TableIndexes"/> says which entries a row has), and
/// MSysObjects' and MSysACEs' indexes when a table is created.
///
/// Access's rules, as its own pages show them:
///   • A leaf's entries are sorted by key, then by row pointer; each is the key
///     followed by the row's page (3 bytes, big-endian) and row (1 byte).
///   • A node holds, for each child but its last, a copy of the last entry under
///     that child followed by the child's page (4 bytes, big-endian). The last
///     child has no entry: the node's tail pointer leads to it.
///   • Every level is a list linked through prev/next; 0 is "no page".
///   • A page's header names its table's TDEF page and (Jet4) its height above
///     the leaves.
///   • Every page of an index is in the index's used-pages usage map.
///
/// A page stores the bytes all its keys begin with once, as Access does: the first
/// entry is written in full, the rest without that prefix, whose length the page
/// header holds (Jackcess takes it the same way, from the first and last keys).
/// A full Access page holds more entries than would fit written out in full, so
/// every page is written that way. A page that does not fit, after any change,
/// splits - in two, or, where a key sharing nothing with its neighbours undoes the
/// packing, into as many pieces as halving takes - and its parent gains an entry
/// for each new piece; a parent that overflows splits the same way. The root keeps
/// its page, as Jackcess's does: when it splits, its entries move to new pages
/// beneath it and it becomes the node above them, and a root left with one child
/// takes that child's entries back, as often as that leaves it with one child. So
/// the TDEF's root, and every copy of it a reader or writer holds, stays right.
///
/// Pages written by earlier versions of this library (an entry for every child,
/// each with row pointer 0, and no tail; 0xFFFFFFFF for "no page"; no TDEF named)
/// are read either way and brought to these rules, children and all, when the
/// tree is changed.
/// </summary>
internal sealed class IndexWriter
{
    private const int LeafTrailerLen = 4;   // 3-byte BE page + 1-byte row
    private const int NodeTrailerLen = 8;   // leaf trailer + 4-byte BE child page
    private const int OffsetTdefPage = 4;
    private const int OldNoPage      = unchecked((int)0xFFFFFFFF);

    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;
    private readonly JetFormat     _format;

    // The index pages this writer has read, when it is asked to keep them (RememberPagesRead): a row is looked
    // for in a unique index before it is written and entered in it after, down the same pages, and nothing
    // changes an index in between. A page this writer writes is forgotten.
    private Dictionary<int, byte[]>? _remembered;

    /// <summary>
    /// Keeps each index page read from here on, so that an entry added through this writer after a search
    /// through it reads no page twice. For one row's check and entry only: pages another writer changes
    /// meanwhile are not seen.
    /// </summary>
    internal void RememberPagesRead() => _remembered ??= new Dictionary<int, byte[]>();

    /// <summary>Forgets every page kept: the file may hold other bytes for them, as after a change that was undone.</summary>
    internal void ForgetPagesRead() => _remembered?.Clear();

    private byte[] ReadRaw(int pageNumber)
    {
        if (_remembered is null) return _file.ReadPage(pageNumber);
        if (!_remembered.TryGetValue(pageNumber, out var raw))
            _remembered[pageNumber] = raw = _file.ReadPage(pageNumber);
        return (byte[])raw.Clone();
    }

    private void WriteRaw(int pageNumber, byte[] page)
    {
        _remembered?.Remove(pageNumber);
        _file.WritePage(pageNumber, page);
    }

    public IndexWriter(PageFile file, PageAllocator allocator)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _format    = file.Format;
    }

    // ── Primary keys ─────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh, empty leaf page and returns its page number.</summary>
    public int CreateEmptyLeafPage() => CreateEmptyLeafPage(tdefPage: 0);

    internal int CreateEmptyLeafPage(int tdefPage)
    {
        var page = new IndexPage(_allocator.AllocatePage(), isLeaf: true, raw: null) { Tdef = tdefPage };
        Write(page);
        return page.Number;
    }

    public int CreatePrimaryKeyIndex(TableDefinition table, string indexName)
        => CreateEmptyLeafPage(table.TdefPageNumber);

    /// <summary>Composite-key companion of <see cref="CreatePrimaryKeyIndex(TableDefinition, string)"/>.</summary>
    public int CreatePrimaryKeyIndex(TableDefinition table, IReadOnlyList<string> pkColumns)
        => CreateEmptyLeafPage(table.TdefPageNumber);

    public int? FindRowByPrimaryKey(TableDefinition table, object primaryKeyValue)
    {
        foreach (int rowPtr in EnumerateRowPointersForKey(table, primaryKeyValue))
            return rowPtr;
        return null;
    }

    /// <summary>Composite-key variant of <see cref="FindRowByPrimaryKey(TableDefinition, object)"/>.</summary>
    public int? FindRowByPrimaryKey(TableDefinition table, IReadOnlyList<object?> values)
    {
        if (table.PrimaryKeyIndexPage == 0) return null;
        foreach (int rowPtr in RowPointersForKey(table.PrimaryKeyIndexPage, PrimaryKeyBytes(table, values)))
            return rowPtr;
        return null;
    }

    /// <summary>
    /// Every row pointer the primary key holds under <paramref name="primaryKeyValue"/>,
    /// including those an update left behind (callers check the row).
    /// </summary>
    public IEnumerable<int> EnumerateRowPointersForKey(TableDefinition table, object primaryKeyValue)
    {
        if (table.PrimaryKeyIndexPage == 0) return Array.Empty<int>();
        return RowPointersForKey(table.PrimaryKeyIndexPage, PrimaryKeyBytes(table, new[] { primaryKeyValue }));
    }

    /// <summary>
    /// Adds a (key, row) entry to the table's primary key. The root stays on its page
    /// (see the class summary), so <see cref="TableDefinition.PrimaryKeyIndexPage"/> and
    /// the TDEF stay right, for this table object and any other.
    /// </summary>
    public void InsertPrimaryKey(TableDefinition table, object primaryKeyValue, int rowPointer)
        => InsertPrimaryKey(table, new[] { primaryKeyValue }, rowPointer);

    /// <summary>Composite-key variant of <see cref="InsertPrimaryKey(TableDefinition, object, int)"/>.</summary>
    public void InsertPrimaryKey(TableDefinition table, IReadOnlyList<object?> values, int rowPointer)
    {
        if (table.PrimaryKeyIndexPage == 0)
            throw new InvalidOperationException(
                "Table has no primary key index page. " +
                "Specify a primary key column name when calling Database.CreateTable.");

        var target = new Target(table.TdefPageNumber, table.PrimaryKeyIndexPage,
                                table.PrimaryKeyIndexUmapPage, table.PrimaryKeyIndexUmapRow);
        Insert(target, PrimaryKeyBytes(table, values), rowPointer);
    }

    /// <summary>
    /// The primary key's columns: from the index on disk when the table was read,
    /// or from its definition (ascending) when this library made it.
    /// </summary>
    internal static IReadOnlyList<IndexColumn> PrimaryKeyColumns(TableDefinition table)
    {
        var pk = table.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
        if (pk is not null && pk.Columns.Count > 0) return pk.Columns;
        return table.EffectivePrimaryKeyColumns
            .Select(name => new IndexColumn(
                table.Columns.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)), 0x01))
            .ToList();
    }

    private static byte[] PrimaryKeyBytes(TableDefinition table, IReadOnlyList<object?> values)
    {
        var columns = PrimaryKeyColumns(table);
        foreach (var value in values)
            if (value is null)
                throw new NotSupportedException("A primary key value cannot be null.");
        return IndexKeys.Encode(columns, values);
    }

    // ── Any index ────────────────────────────────────────────────────────────

    /// <summary>An index to change: its table's TDEF page, its root page (which never moves), and its used-pages usage map.</summary>
    internal sealed class Target
    {
        public Target(int tdefPage, int rootPage, int umapPage, int umapRow)
        {
            TdefPage = tdefPage;
            RootPage = rootPage;
            UmapPage = umapPage;
            UmapRow  = umapRow;
        }

        public static Target For(int tdefPage, Index index)
            => new(tdefPage, index.RootPageNumber, index.UsedPagesUmapPage, index.UsedPagesUmapRow);

        public int TdefPage { get; }
        public int RootPage { get; }
        public int UmapPage { get; }
        public int UmapRow  { get; }
    }

    /// <summary>
    /// Adds the entry (<paramref name="key"/>, <paramref name="rowPointer"/>), splitting
    /// pages up the tree as they fill. Returns whether the tree holds another entry under <paramref name="key"/>,
    /// as the pages read on the way down show it, with no page read to find out: the entry before the new one
    /// and the entry after it.
    /// <para>
    /// An entry the tree holds already is not added again: a tree holds each entry once. Access walks a row whose
    /// entry an index holds twice twice; deleting the row it takes one of the two out, and the other, pointing at no
    /// row, is a child for which it refuses to delete the row's parent; and DAO compacts a file whose primary key holds
    /// one without the key (measured). Such an entry is where the new one would go, in the leaf the way down comes to,
    /// so finding it reads no page; whether another entry is under the key then reads the next leaf when the entry is
    /// its leaf's last, as <see cref="Remove(Target, byte[], int, out bool)"/> does.
    /// </para>
    /// </summary>
    internal bool Insert(Target target, byte[] key, int rowPointer)
    {
        var entry = new Entry(key, rowPointer, 0);
        var path  = new List<(IndexPage Node, int Slot)>();   // Slot -1: the node's tail

        var page = ReadFor(target, target.RootPage);
        while (!page.IsLeaf)
        {
            UseTail(page, target);
            int slot = FirstAtLeast(page.Entries, entry);
            path.Add((page, slot));
            page = ReadFor(target, slot >= 0 ? page.Entries[slot].SubPage : page.Tail);
        }
        int at = FirstAtLeast(page.Entries, entry);
        if (at < 0) at = page.Entries.Count;
        if (at < page.Entries.Count && Compare(page.Entries[at], entry) == 0)
        {
            if (HasNeighbourUnder(key, page.Entries, at - 1, at + 1, path)) return true;
            return at == page.Entries.Count - 1 && page.Next > 0
                && ReadFor(target, page.Next).Entries is { Count: > 0 } next && CompareBytes(next[0].Key, key) == 0;
        }
        bool another = HasNeighbourUnder(key, page.Entries, at - 1, at, path);
        page.Entries.Insert(at, entry);
        page.Level = 0;
        WriteUp(path, path.Count, page, target);
        return another;
    }

    /// <summary>
    /// Whether the tree of <paramref name="target"/> holds no entry: its root is a leaf with none, as an index Access
    /// made for a table with no rows has it.
    /// </summary>
    internal bool IsEmpty(Target target)
    {
        var root = Read(target.RootPage);
        return root.IsLeaf && root.Entries.Count == 0;
    }

    /// <summary>
    /// Writes the whole tree of an index whose tree holds no entry (<see cref="IsEmpty"/>) from
    /// <paramref name="sorted"/>, its entries in key order and, under one key, in row-pointer order - as Access
    /// builds a tree when it compacts a file, page after page, not entry after entry. Each leaf takes entries until
    /// the next does not fit it; each node above takes, for every child but its last, a copy of the last entry
    /// under that child, its tail leading to the last; every level is chained through prev/next, each page names
    /// its height above the leaves and the table's TDEF, and every page but the root, which stays on its page, is
    /// added to the index's usage map. The rules are those <see cref="Insert"/> keeps, so the tree is one Access
    /// reads and adds to, and this writer goes on adding to.
    /// <para>
    /// The entries are gone through once and only a leaf's worth is held, with the last entry of each leaf written
    /// for the nodes: the leaves are written as they fill, each when the one after it is begun.
    /// </para>
    /// </summary>
    internal void Build(Target target, IEnumerable<(byte[] Key, int RowPointer)> sorted)
    {
        var root = ReadFor(target, target.RootPage);
        if (!root.IsLeaf || root.Entries.Count > 0)
            throw new InvalidOperationException($"Index page {target.RootPage} holds entries: only a tree that holds none is built whole.");

        int room = _format.PageSize - EntriesStart;
        var leaves = new List<(int Number, Entry Last)>();
        var current = new List<Entry>();
        int keyBytes = 0, prefix = 0;
        List<Entry>? pending = null;      // the leaf closed last, written when the next one is numbered
        int pendingNumber = 0, pendingPrev = 0;

        void Close(List<Entry> entries)
        {
            int number = _allocator.AllocatePage();
            if (pending is not null) WriteLeaf(pending, pendingNumber, pendingPrev, number, target);
            pendingPrev   = pending is null ? 0 : pendingNumber;
            pending       = entries;
            pendingNumber = number;
            leaves.Add((number, entries[^1]));
        }

        foreach (var (key, rowPointer) in sorted)
        {
            var e = new Entry(key, rowPointer, 0);
            if (current.Count > 0)
            {
                int shared = Shared(current[0].Key, key, current.Count == 1 ? int.MaxValue : prefix);
                int size = keyBytes + key.Length + (current.Count + 1) * LeafTrailerLen - current.Count * shared;
                if (size > room)
                {
                    Close(current);
                    current = new List<Entry>();
                    keyBytes = 0;
                    prefix = 0;
                }
                else prefix = shared;
            }
            current.Add(e);
            keyBytes += key.Length;
        }
        if (current.Count == 0) return;

        if (leaves.Count == 0)
        {
            // One leaf holds them all: the root.
            var leaf = new IndexPage(root.Number, isLeaf: true, root.Raw) { Tdef = target.TdefPage };
            leaf.Entries.AddRange(current);
            Write(leaf);
            return;
        }
        Close(current);
        WriteLeaf(pending!, pendingNumber, pendingPrev, 0, target);

        // Each level of nodes above, until one page holds a level: the root, which stays on its page.
        var children = leaves;
        for (int height = 1; ; height++)
        {
            var nodes = PackNodes(children);
            if (nodes.Count == 1)
            {
                var top = new IndexPage(root.Number, isLeaf: false, root.Raw) { Tdef = target.TdefPage, Level = height, Tail = nodes[0].Tail };
                top.Entries.AddRange(nodes[0].Entries);
                Write(top);
                return;
            }
            WriteLevel(nodes, target, isLeaf: false, height);
            children = nodes.Select(n => (n.Number, n.Last)).ToList();
        }
    }

    private void WriteLeaf(List<Entry> entries, int number, int prev, int next, Target target)
    {
        var page = new IndexPage(number, isLeaf: true, raw: null) { Tdef = target.TdefPage, Prev = prev, Next = next };
        page.Entries.AddRange(entries);
        Write(page);
        Own(target, number);
    }

    /// <summary>The entries of one page of a tree being built, and the last entry under it, which its parent holds.</summary>
    private sealed class Packed
    {
        public List<Entry> Entries = new();
        public int Tail;
        public Entry Last;
        public int Number;
    }

    // Nodes over the pages of the level below, in order: each holds, for every child but its last, a copy of the
    // last entry under the child with the child's page, and its tail is its last child. A node is never left with
    // its tail alone: the node before gives it a child.
    private List<Packed> PackNodes(List<(int Number, Entry Last)> children)
    {
        var nodes = new List<Packed>();
        Packed? current = null;
        (int Number, Entry Last) pending = default;
        foreach (var child in children)
        {
            if (current is null)
            {
                current = new Packed();
                pending = child;
                continue;
            }
            current.Entries.Add(new Entry(pending.Last.Key, pending.Last.RowPtr, pending.Number));
            if (!Fits(false, current.Entries) && current.Entries.Count > 1)
            {
                // The entry for the child before does not fit: that child is this node's tail, and a new node begins.
                current.Entries.RemoveAt(current.Entries.Count - 1);
                current.Tail = pending.Number;
                current.Last = pending.Last;
                nodes.Add(current);
                current = new Packed();
            }
            pending = child;
        }
        current!.Tail = pending.Number;
        current.Last  = pending.Last;
        nodes.Add(current);

        if (nodes.Count > 1 && nodes[^1].Entries.Count == 0)
        {
            // The last node has its tail alone: the node before gives up its own tail to it.
            var before = nodes[^2];
            var last   = nodes[^1];
            var moved  = before.Entries[^1];
            last.Entries.Add(new Entry(before.Last.Key, before.Last.RowPtr, before.Tail));
            before.Tail = moved.SubPage;
            before.Last = new Entry(moved.Key, moved.RowPtr, 0);
            before.Entries.RemoveAt(before.Entries.Count - 1);
        }
        return nodes;
    }

    // Gives each page of a level a new page, chains them, writes them and adds them to the index's usage map.
    private void WriteLevel(List<Packed> pages, Target target, bool isLeaf, int height)
    {
        foreach (var p in pages) p.Number = _allocator.AllocatePage();
        for (int i = 0; i < pages.Count; i++)
        {
            var page = new IndexPage(pages[i].Number, isLeaf, raw: null)
            {
                Tdef  = target.TdefPage,
                Level = height,
                Prev  = i > 0 ? pages[i - 1].Number : 0,
                Next  = i < pages.Count - 1 ? pages[i + 1].Number : 0,
                Tail  = isLeaf ? 0 : pages[i].Tail,
            };
            page.Entries.AddRange(pages[i].Entries);
            Write(page);
            Own(target, page.Number);
        }
    }

    // How many bytes a and b begin with alike, up to limit.
    private static int Shared(byte[] a, byte[] b, int limit)
    {
        int n = Math.Min(Math.Min(a.Length, b.Length), limit), shared = 0;
        while (shared < n && a[shared] == b[shared]) shared++;
        return shared;
    }

    /// <summary>
    /// Whether the entry at <paramref name="before"/> or at <paramref name="after"/> in a leaf's
    /// <paramref name="entries"/> has <paramref name="key"/>. Entries are in key order, so another entry under a
    /// key is beside it. The entry before a leaf's first is the last under the leaf before, which the nearest node
    /// above with a child before the one gone down holds a copy of; nothing is after a leaf's last that is not
    /// greater than the entry its parent has for it.
    /// </summary>
    private static bool HasNeighbourUnder(byte[] key, List<Entry> entries, int before, int after, List<(IndexPage Node, int Slot)> path)
    {
        if (after < entries.Count && CompareBytes(entries[after].Key, key) == 0) return true;
        if (before >= 0) return CompareBytes(entries[before].Key, key) == 0;

        for (int depth = path.Count - 1; depth >= 0; depth--)
        {
            var (node, slot) = path[depth];
            if (slot > 0) return CompareBytes(node.Entries[slot - 1].Key, key) == 0;
            if (slot < 0 && node.Entries.Count > 0) return CompareBytes(node.Entries[^1].Key, key) == 0;
        }
        return false;
    }

    /// <summary>
    /// Writes <paramref name="child"/>, the page at <paramref name="depth"/> on <paramref name="path"/> (whose
    /// parent is <c>path[depth - 1]</c>): while a page does not fit, it splits and its parent gains the entry, up to
    /// the root, which splits in place.
    /// </summary>
    private void WriteUp(List<(IndexPage Node, int Slot)> path, int depth, IndexPage child, Target target)
    {
        for (; ; depth--)
        {
            if (Fits(child))
            {
                Write(child);
                return;
            }

            if (depth == 0)
            {
                SplitRoot(child, target);
                return;
            }

            var (pieces, lasts) = Split(child, target, keepNumber: true);
            var added = lasts.Select((last, i) => new Entry(last.Key, last.RowPtr, pieces[i].Number)).ToList();

            var (parent, parentSlot) = path[depth - 1];
            if (parentSlot >= 0)
            {
                // The split page's entry now names its last piece, which holds its last row;
                // each piece before it gets an entry of its own.
                var old = parent.Entries[parentSlot];
                parent.Entries[parentSlot] = new Entry(old.Key, old.RowPtr, pieces[^1].Number);
                parent.Entries.InsertRange(parentSlot, added);
            }
            else
            {
                // The split page was the tail: the pieces before its last get entries; its last is the tail.
                parent.Entries.AddRange(added);
                parent.Tail = pieces[^1].Number;
            }
            parent.Level = child.Level + 1;
            child = parent;
        }
    }

    /// <summary>
    /// Takes the entry (<paramref name="key"/>, <paramref name="rowPointer"/>) out of the tree, as Access and
    /// Jackcess do: a page left empty leaves the tree and its level's chain (a tail leaving its node, the node's
    /// last entry's child becomes the tail), an entry copying the last entry under a page follows it when that
    /// changes, and a root left with one child gives way to it. An entry the tree does not hold is no change.
    /// Returns whether the tree holds another entry under <paramref name="key"/> beside the one taken out, as
    /// <see cref="Insert"/> does, the first of the next leaf among them; false when it held no such entry.
    /// </summary>
    internal bool Remove(Target target, byte[] key, int rowPointer) => Remove(target, key, rowPointer, out _);

    /// <summary>
    /// <see cref="Remove(Target, byte[], int)"/>, saying in <paramref name="found"/> whether the tree held the entry,
    /// and so whether it was taken out.
    /// </summary>
    internal bool Remove(Target target, byte[] key, int rowPointer, out bool found)
    {
        found = false;
        var entry = new Entry(key, rowPointer, 0);
        var path  = new List<(IndexPage Node, int Slot)>();   // Slot -1: the node's tail

        var page = ReadFor(target, target.RootPage);
        while (!page.IsLeaf)
        {
            UseTail(page, target);
            int slot = FirstAtLeast(page.Entries, entry);
            path.Add((page, slot));
            page = ReadFor(target, slot >= 0 ? page.Entries[slot].SubPage : page.Tail);
        }
        int at = page.Entries.FindIndex(e => Compare(e, entry) == 0);
        if (at < 0) return false;
        found = true;
        bool another = HasNeighbourUnder(key, page.Entries, at - 1, at + 1, path);
        // Entries are in the order of their keys and then of their rows, so the leaf after this one can begin under
        // the key of this one's last entry: when that is the one taken out, the next leaf's first entry is read too
        // - one page more for a removal at the end of a leaf, and none for any other.
        if (!another && at == page.Entries.Count - 1 && page.Next > 0)
        {
            var next = ReadFor(target, page.Next);
            another = next.Entries.Count > 0 && CompareBytes(next.Entries[0].Key, key) == 0;
        }
        page.Entries.RemoveAt(at);
        page.Level = 0;

        // The last entry under the page, when the removal changed it.
        Entry? newLast = at == page.Entries.Count && page.Entries.Count > 0 ? page.Entries[^1] : null;

        // An empty page leaves the tree; its parent loses the entry for it, or its tail.
        var child = page;
        int depth = path.Count;
        while (depth > 0 && child.Entries.Count == 0 && (child.IsLeaf || child.Tail <= 0))
        {
            Drop(child, target);
            var (parent, slot) = path[depth - 1];
            if (slot >= 0)
            {
                parent.Entries.RemoveAt(slot);
                newLast = null;   // the parent's tail still holds what is last under it
            }
            else if (parent.Entries.Count > 0)
            {
                var last = parent.Entries[^1];
                parent.Entries.RemoveAt(parent.Entries.Count - 1);
                parent.Tail = last.SubPage;
                newLast = last;
            }
            else
            {
                parent.Tail = 0;
                newLast = null;
            }
            child = parent;
            depth--;
        }

        if (depth == 0 && !child.IsLeaf && child.Entries.Count == 0)
        {
            CollapseRoot(child, target);
            return another;
        }
        WriteUp(path, depth, child, target);

        // The first ancestor naming the page in an entry copies its new last entry, which can
        // make that ancestor's page too full: it splits as an insert's would.
        if (newLast is { } changed)
        {
            for (int d = depth - 1; d >= 0; d--)
            {
                var (parent, slot) = path[d];
                if (slot < 0) continue;
                parent.Entries[slot] = new Entry(changed.Key, changed.RowPtr, parent.Entries[slot].SubPage);
                WriteUp(path, d, parent, target);
                break;
            }
        }
        return another;
    }

    /// <summary>
    /// Whether the tree holds the entry (<paramref name="key"/>, <paramref name="rowPointer"/>), found one way down
    /// as <see cref="Remove(Target, byte[], int, out bool)"/> goes, the same pages read and nothing written; null
    /// when a node on the way is in the form earlier versions of this library wrote, without a tail, which only a
    /// change brings to the form the way down is decided by.
    /// </summary>
    internal bool? Holds(Target target, byte[] key, int rowPointer)
    {
        var entry = new Entry(key, rowPointer, 0);
        var page = Read(target.RootPage);
        while (!page.IsLeaf)
        {
            if (page.Tail <= 0) return page.Entries.Count == 0 ? false : null;
            int slot = FirstAtLeast(page.Entries, entry);
            page = Read(slot >= 0 ? page.Entries[slot].SubPage : page.Tail);
        }
        return page.Entries.Exists(e => Compare(e, entry) == 0);
    }

    /// <summary>
    /// A root with one child takes that child's entries back, on its own page, for as
    /// long as that leaves it with one child; a root with none is an empty leaf again.
    /// </summary>
    private void CollapseRoot(IndexPage root, Target target)
    {
        while (!root.IsLeaf && root.Entries.Count == 0)
        {
            if (root.Tail <= 0)
            {
                Write(new IndexPage(root.Number, isLeaf: true, root.Raw) { Tdef = target.TdefPage });
                return;
            }
            var only = Read(root.Tail);
            var taken = new IndexPage(root.Number, only.IsLeaf, root.Raw)
            {
                Tdef  = target.TdefPage,
                Level = only.Level,
                Tail  = only.Tail,
            };
            taken.Entries.AddRange(only.Entries);
            if (!Fits(taken)) break;   // a child Access packed tighter than these pages are written
            if (target.UmapPage > 0)
                UsageMap.RemovePage(_file, target.UmapPage, target.UmapRow, only.Number);
            root = taken;
        }
        Write(root);
    }

    // A page leaving the tree: out of its level's chain and its index's usage map.
    private void Drop(IndexPage page, Target target)
    {
        if (page.Prev > 0) PatchLink(page.Prev, _format.OffsetNextIndexPage, page.Next);
        if (page.Next > 0) PatchLink(page.Next, _format.OffsetPrevIndexPage, page.Prev);
        if (target.UmapPage > 0)
            UsageMap.RemovePage(_file, target.UmapPage, target.UmapRow, page.Number);
    }

    /// <summary>
    /// The row pointers of every entry whose key is <paramref name="key"/>, in order,
    /// in the tree rooted at <paramref name="rootPage"/>.
    /// </summary>
    internal IEnumerable<int> RowPointersForKey(int rootPage, byte[] key)
    {
        var first = new Entry(key, 0, 0);
        var page  = Read(rootPage);
        while (!page.IsLeaf)
        {
            UseTail(page, target: null);
            int slot  = FirstAtLeast(page.Entries, first);
            int child = slot >= 0 ? page.Entries[slot].SubPage : page.Tail;
            if (child <= 0) yield break;
            page = Read(child);
        }

        while (true)
        {
            foreach (var e in page.Entries)
            {
                int cmp = CompareBytes(e.Key, key);
                if (cmp == 0) yield return e.RowPtr;
                else if (cmp > 0) yield break;
            }
            if (page.Next <= 0) yield break;
            page = Read(page.Next);
        }
    }

    /// <summary>
    /// Every entry whose key begins with <paramref name="prefix"/>, in order, in the tree rooted at
    /// <paramref name="rootPage"/>: its key as the tree holds it, and its row pointer. A key that was cut and the
    /// same key written whole - as versions of this library before the cut, and Jackcess before it, wrote one -
    /// begin alike, so this finds an entry under either.
    /// </summary>
    internal IEnumerable<(byte[] Key, int RowPointer)> EntriesWithPrefix(int rootPage, byte[] prefix)
    {
        var first = new Entry(prefix, 0, 0);
        var page  = Read(rootPage);
        while (!page.IsLeaf)
        {
            UseTail(page, target: null);
            int slot  = FirstAtLeast(page.Entries, first);
            int child = slot >= 0 ? page.Entries[slot].SubPage : page.Tail;
            if (child <= 0) yield break;
            page = Read(child);
        }

        while (true)
        {
            foreach (var e in page.Entries)
            {
                if (e.Key.Length >= prefix.Length && e.Key.AsSpan(0, prefix.Length).SequenceEqual(prefix)) yield return (e.Key, e.RowPtr);
                else if (CompareBytes(e.Key, prefix) > 0) yield break;
            }
            if (page.Next <= 0) yield break;
            page = Read(page.Next);
        }
    }

    // ── Leaves, for a cursor ─────────────────────────────────────────────────

    /// <summary>A leaf of a tree as a cursor walks it: its entries in order, and the leaves either side of it (0 for none).</summary>
    internal sealed class Leaf
    {
        internal Leaf(List<(byte[] Key, int RowPointer)> entries, int prev, int next)
        {
            Entries = entries;
            Prev    = prev;
            Next    = next;
        }

        internal List<(byte[] Key, int RowPointer)> Entries { get; }
        internal int Prev { get; }
        internal int Next { get; }
    }

    /// <summary>The leaf on page <paramref name="pageNumber"/>.</summary>
    internal Leaf ReadLeaf(int pageNumber)
    {
        var page = Read(pageNumber);
        if (!page.IsLeaf) throw new InvalidDataException($"Index page {pageNumber} is not a leaf.");
        return AsLeaf(page);
    }

    private static Leaf AsLeaf(IndexPage page)
        => new(page.Entries.Select(e => (e.Key, e.RowPtr)).ToList(), page.Prev, page.Next);

    /// <summary>
    /// The leaf, in the tree rooted at <paramref name="rootPage"/>, where the first entry at or after
    /// (<paramref name="key"/>, <paramref name="rowPointer"/>) is - or, in a tree whose nodes an earlier version
    /// of this library wrote without row pointers, a leaf at or before it: the caller reads on from it.
    /// </summary>
    internal Leaf LeafFor(int rootPage, byte[] key, int rowPointer)
    {
        var page = Read(rootPage);
        while (!page.IsLeaf)
        {
            // A node with no tail holds no row pointers (UseTail): the key's first entry is looked for.
            var sought = new Entry(key, page.Tail > 0 ? rowPointer : 0, 0);
            UseTail(page, target: null);
            int slot  = FirstAtLeast(page.Entries, sought);
            int child = slot >= 0 ? page.Entries[slot].SubPage : page.Tail;
            if (child <= 0) throw new InvalidDataException($"Index page {page.Number} names no page beneath it.");
            page = Read(child);
        }
        return AsLeaf(page);
    }

    /// <summary>The first leaf (<paramref name="last"/> false) or the last of the tree rooted at <paramref name="rootPage"/>.</summary>
    internal Leaf EndLeaf(int rootPage, bool last)
    {
        var page = Read(rootPage);
        while (!page.IsLeaf)
        {
            UseTail(page, target: null);
            int child = last
                ? (page.Tail > 0 ? page.Tail : page.Entries.Count > 0 ? page.Entries[^1].SubPage : 0)
                : (page.Entries.Count > 0 ? page.Entries[0].SubPage : page.Tail);
            if (child <= 0) throw new InvalidDataException($"Index page {page.Number} names no page beneath it.");
            page = Read(child);
        }
        return AsLeaf(page);
    }

    // ── Splitting ────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits <paramref name="page"/>, which does not fit, into pieces that each do:
    /// it halves, and a half that still does not fit halves again, as a page holding
    /// keys that share a long prefix and one that shares none can need. With
    /// <paramref name="keepNumber"/> the first piece keeps the page's number and the
    /// pieces take its place in its level's chain; otherwise they are all new pages,
    /// chained to one another. Writes them and returns them in order with, for each
    /// but the last, the last entry under it, which its parent holds a copy of (the
    /// last piece's is the page's own).
    /// </summary>
    private (List<IndexPage> Pieces, List<Entry> Lasts) Split(IndexPage page, Target target, bool keepNumber)
    {
        var contents = new List<(List<Entry> Entries, int Tail)>();
        var lasts    = new List<Entry>();
        Halve(page.IsLeaf, page.Entries, page.Tail, contents, lasts);

        var pieces = new List<IndexPage>();
        for (int i = 0; i < contents.Count; i++)
        {
            var piece = i == 0 && keepNumber
                ? new IndexPage(page.Number, page.IsLeaf, page.Raw)
                : new IndexPage(_allocator.AllocatePage(), page.IsLeaf, raw: null);
            piece.Tdef  = target.TdefPage;
            piece.Level = page.Level;
            piece.Tail  = contents[i].Tail;
            piece.Entries.AddRange(contents[i].Entries);
            pieces.Add(piece);
        }
        for (int i = 0; i < pieces.Count; i++)
        {
            pieces[i].Prev = i > 0 ? pieces[i - 1].Number : keepNumber ? page.Prev : 0;
            pieces[i].Next = i < pieces.Count - 1 ? pieces[i + 1].Number : keepNumber ? page.Next : 0;
        }
        if (keepNumber && page.Next > 0)
            PatchLink(page.Next, _format.OffsetPrevIndexPage, pieces[^1].Number);

        foreach (var piece in pieces)
            Write(piece);
        foreach (var piece in pieces.Skip(keepNumber ? 1 : 0))
            Own(target, piece.Number);
        return (pieces, lasts);
    }

    // Halves a page's content until each part fits, adding the parts and, between them, the
    // last entry under each part but the last. A node's middle entry goes up between its halves:
    // its child becomes the left half's tail.
    private void Halve(bool isLeaf, List<Entry> entries, int tail, List<(List<Entry>, int)> parts, List<Entry> lasts)
    {
        if (Fits(isLeaf, entries))
        {
            parts.Add((entries, tail));
            return;
        }
        int mid = entries.Count / 2;
        if (isLeaf)
        {
            if (entries.Count < 2)
                throw new InvalidDataException($"An index entry of {entries[0].Key.Length + LeafTrailerLen} bytes does not fit a page.");
            Halve(true, entries.GetRange(0, mid), 0, parts, lasts);
            lasts.Add(entries[mid - 1]);
            Halve(true, entries.GetRange(mid, entries.Count - mid), 0, parts, lasts);
        }
        else
        {
            if (entries.Count < 3)
                throw new InvalidDataException($"An index node of {entries.Count} entries does not fit a page.");
            var middle = entries[mid];
            Halve(false, entries.GetRange(0, mid), middle.SubPage, parts, lasts);
            lasts.Add(middle);
            Halve(false, entries.GetRange(mid + 1, entries.Count - mid - 1), tail, parts, lasts);
        }
    }

    /// <summary>
    /// Splits the root without moving it, as Jackcess does: its content goes to new
    /// pages beneath it, as many as fit it, chained as their level, and it becomes the
    /// node above them - splitting again, the same way, if that node does not fit.
    /// </summary>
    private void SplitRoot(IndexPage root, Target target)
    {
        var (pieces, lasts) = Split(root, target, keepNumber: false);
        var node = new IndexPage(root.Number, isLeaf: false, root.Raw)
        {
            Tdef  = target.TdefPage,
            Level = root.Level + 1,
            Tail  = pieces[^1].Number,
        };
        node.Entries.AddRange(lasts.Select((last, i) => new Entry(last.Key, last.RowPtr, pieces[i].Number)));
        if (Fits(node)) Write(node);
        else SplitRoot(node, target);
    }

    /// <summary>
    /// A node written by an earlier version of this library has an entry for every
    /// child, each with row pointer 0, and no tail: its last child becomes the tail.
    /// When the tree is being changed (a <paramref name="target"/>), each entry also
    /// takes the row of the last entry under its child, so entries compare as Access's
    /// do; its children are written back naming the TDEF, with 0 for no page; and the
    /// node is written back with its level. A search for a key's first entry (row 0)
    /// goes to the same child either way, so a search reads no more.
    /// </summary>
    private void UseTail(IndexPage node, Target? target)
    {
        if (node.Tail > 0 || node.Entries.Count == 0) return;
        node.Tail = node.Entries[^1].SubPage;
        node.Entries.RemoveAt(node.Entries.Count - 1);
        if (target is null) return;
        for (int i = 0; i < node.Entries.Count; i++)
        {
            if (LastEntryUnder(node.Entries[i].SubPage) is { } last)
                node.Entries[i] = new Entry(last.Key, last.RowPtr, node.Entries[i].SubPage);
        }
        foreach (int child in node.Entries.Select(e => e.SubPage).Append(node.Tail))
            Write(ReadFor(target, child));
        node.Level = HeightOf(node);
        Write(node);
    }

    // The last entry in the subtree under a page: down its tails (or last entries) to a leaf.
    private Entry? LastEntryUnder(int pageNumber)
    {
        var page = Read(pageNumber);
        while (!page.IsLeaf)
        {
            int next = page.Tail > 0 ? page.Tail : page.Entries.Count > 0 ? page.Entries[^1].SubPage : 0;
            if (next <= 0) return null;
            page = Read(next);
        }
        return page.Entries.Count > 0 ? page.Entries[^1] : null;
    }

    // A page's height above the leaves, found by walking down its last children.
    private int HeightOf(IndexPage page)
    {
        int height = 0;
        while (!page.IsLeaf)
        {
            int next = page.Tail > 0 ? page.Tail : page.Entries.Count > 0 ? page.Entries[^1].SubPage : 0;
            if (next <= 0) break;
            page = Read(next);
            height++;
        }
        return height;
    }

    private void Own(Target target, int pageNumber)
    {
        if (target.UmapPage > 0)
            UsageMap.AddPage(_file, _allocator, target.UmapPage, target.UmapRow, pageNumber);
    }

    private void PatchLink(int pageNumber, int offset, int value)
    {
        byte[] raw = ReadRaw(pageNumber);
        ByteUtil.PutInt(raw, offset, value);
        WriteRaw(pageNumber, raw);
    }

    // ── Pages ────────────────────────────────────────────────────────────────

    private sealed class IndexPage
    {
        public IndexPage(int number, bool isLeaf, byte[]? raw)
        {
            Number = number;
            IsLeaf = isLeaf;
            Raw    = raw;
        }

        public int         Number  { get; }
        public bool        IsLeaf  { get; }
        public byte[]?     Raw     { get; }   // as read; null for a new page
        public int         Tdef    { get; set; }
        public int         Level   { get; set; }
        public int         Prev    { get; set; }
        public int         Next    { get; set; }
        public int         Tail    { get; set; }
        public List<Entry> Entries { get; } = new();
    }

    private readonly record struct Entry(byte[] Key, int RowPtr, int SubPage);

    private int EntriesStart => _format.OffsetIndexEntryMask + _format.SizeIndexEntryMask;

    /// <summary>
    /// The longest key an entry can have and its tree still be written: a node must hold three entries - one
    /// each side of the entry that goes up when it splits - written in full, each with its row and child page.
    /// A longer key is refused before its row is written, not found out when a page will not split.
    /// </summary>
    internal static int MaxKeyLength(JetFormat format)
        => (format.PageSize - format.OffsetIndexEntryMask - format.SizeIndexEntryMask) / 3 - NodeTrailerLen;

    private IndexPage Read(int number)
    {
        byte[] raw = ReadRaw(number);
        if (raw[0] != JetFormat.PageTypeIndexLeaf && raw[0] != JetFormat.PageTypeIndexNode)
            throw new InvalidDataException($"Page {number} is not an index page (type 0x{raw[0]:X2}).");

        bool isLeaf = raw[0] == JetFormat.PageTypeIndexLeaf;
        var page = new IndexPage(number, isLeaf, raw)
        {
            Tdef  = ByteUtil.GetInt(raw, OffsetTdefPage),
            Level = raw[_format.OffsetIndexLevel],
            Prev  = Link(ByteUtil.GetInt(raw, _format.OffsetPrevIndexPage)),
            Next  = Link(ByteUtil.GetInt(raw, _format.OffsetNextIndexPage)),
            Tail  = isLeaf ? 0 : Link(ByteUtil.GetInt(raw, _format.OffsetChildTailIndexPage)),
        };

        // Entries end where the mask has a bit set; the first is stored in full and
        // the rest without the prefix they share with it.
        int prefixLen  = IndexReader.ReadPrefixLength(raw, _format);
        int trailerLen = isLeaf ? LeafTrailerLen : NodeTrailerLen;
        byte[]? prefix = null;
        int start = 0;
        for (int i = 0; i < _format.SizeIndexEntryMask; i++)
        {
            byte b = raw[_format.OffsetIndexEntryMask + i];
            if (b == 0) continue;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) == 0) continue;
                int end    = i * 8 + bit;
                int stored = end - start;
                byte[] full;
                if (page.Entries.Count == 0 || prefix is null)
                {
                    full = new byte[stored];
                    Array.Copy(raw, EntriesStart + start, full, 0, stored);
                    if (page.Entries.Count == 0 && prefixLen > 0)
                        prefix = full.Take(prefixLen).ToArray();
                }
                else
                {
                    full = new byte[prefix.Length + stored];
                    Array.Copy(prefix, full, prefix.Length);
                    Array.Copy(raw, EntriesStart + start, full, prefix.Length, stored);
                }
                start = end;

                if (full.Length < trailerLen)
                    throw new InvalidDataException($"Index page {number} has an entry of {full.Length} bytes.");
                int keyLen = full.Length - trailerLen;
                int rowPage = (full[keyLen] << 16) | (full[keyLen + 1] << 8) | full[keyLen + 2];
                int rowPtr  = RowPointer.Pack(rowPage, full[keyLen + 3]);
                int subPage = isLeaf ? 0
                    : (full[keyLen + 4] << 24) | (full[keyLen + 5] << 16) | (full[keyLen + 6] << 8) | full[keyLen + 7];
                page.Entries.Add(new Entry(full.Take(keyLen).ToArray(), rowPtr, subPage));
            }
        }
        return page;
    }

    private static int Link(int value) => value == OldNoPage ? 0 : value;

    // A page of the index being changed: it names the index's table, as Access's pages do.
    private IndexPage ReadFor(Target target, int number)
    {
        var page = Read(number);
        page.Tdef = target.TdefPage;
        return page;
    }

    /// <summary>
    /// The bytes every key of a page begins with: those its first and last keys
    /// share, the keys being in order. Stored once, in the first entry.
    /// </summary>
    private static int SharedPrefix(List<Entry> entries)
    {
        if (entries.Count < 2) return 0;
        byte[] first = entries[0].Key, last = entries[^1].Key;
        int n = Math.Min(first.Length, last.Length), shared = 0;
        while (shared < n && first[shared] == last[shared]) shared++;
        return shared;
    }

    private static int Size(bool isLeaf, List<Entry> entries) => entries.Sum(e => e.Key.Length)
        + entries.Count * (isLeaf ? LeafTrailerLen : NodeTrailerLen)
        - Math.Max(0, entries.Count - 1) * SharedPrefix(entries);

    private bool Fits(bool isLeaf, List<Entry> entries) => Size(isLeaf, entries) <= _format.PageSize - EntriesStart;

    private bool Fits(IndexPage page) => Fits(page.IsLeaf, page.Entries);

    private void Write(IndexPage p)
    {
        byte[] page = p.Raw ?? new byte[_format.PageSize];
        page[0] = p.IsLeaf ? JetFormat.PageTypeIndexLeaf : JetFormat.PageTypeIndexNode;
        page[1] = 0x01;
        ByteUtil.PutInt  (page, OffsetTdefPage,                    p.Tdef);
        ByteUtil.PutInt  (page, _format.OffsetPrevIndexPage,       p.Prev);
        ByteUtil.PutInt  (page, _format.OffsetNextIndexPage,       p.Next);
        ByteUtil.PutInt  (page, _format.OffsetChildTailIndexPage,  p.IsLeaf ? 0 : p.Tail);
        int shared = SharedPrefix(p.Entries);
        // Two bytes in every format this library writes; the level is the byte after them.
        ByteUtil.PutShort(page, _format.OffsetIndexCompressedByteCount, (short)shared);
        page[_format.OffsetIndexLevel] = (byte)p.Level;
        if (!Fits(p))
            throw new InvalidOperationException(
                $"Index page {p.Number} holds {Size(p.IsLeaf, p.Entries)} bytes of entries; it has room for {_format.PageSize - EntriesStart}.");

        Array.Clear(page, _format.OffsetIndexEntryMask, _format.PageSize - _format.OffsetIndexEntryMask);
        int end = 0;
        for (int i = 0; i < p.Entries.Count; i++)
        {
            var e = p.Entries[i];
            int skip = i == 0 ? 0 : shared;   // the first entry holds the prefix the rest leave out
            int at = EntriesStart + end;
            Array.Copy(e.Key, skip, page, at, e.Key.Length - skip);
            at += e.Key.Length - skip;
            int rowPage = RowPointer.Page(e.RowPtr);
            page[at++] = (byte)(rowPage >> 16);
            page[at++] = (byte)(rowPage >> 8);
            page[at++] = (byte) rowPage;
            page[at++] = (byte)RowPointer.Row(e.RowPtr);
            if (!p.IsLeaf)
            {
                page[at++] = (byte)(e.SubPage >> 24);
                page[at++] = (byte)(e.SubPage >> 16);
                page[at++] = (byte)(e.SubPage >> 8);
                page[at++] = (byte) e.SubPage;
            }
            end = at - EntriesStart;
            page[_format.OffsetIndexEntryMask + end / 8] |= (byte)(1 << (end % 8));
        }
        ByteUtil.PutShort(page, 2, (short)(_format.PageSize - EntriesStart - end));
        WriteRaw(p.Number, page);
    }

    // ── Ordering ─────────────────────────────────────────────────────────────

    // By key, then by row pointer (its page, then its row).
    private static int Compare(Entry a, Entry b)
    {
        int cmp = CompareBytes(a.Key, b.Key);
        return cmp != 0 ? cmp : ((uint)a.RowPtr).CompareTo((uint)b.RowPtr);
    }

    // The first entry at or after x; -1 when x is after them all.
    private static int FirstAtLeast(List<Entry> entries, Entry x)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Compare(entries[mid], x) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo < entries.Count ? lo : -1;
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int diff = a[i] - b[i];
            if (diff != 0) return diff;
        }
        return a.Length - b.Length;
    }
}
