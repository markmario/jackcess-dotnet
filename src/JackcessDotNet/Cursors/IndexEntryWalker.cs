namespace JackcessDotNet;

/// <summary>
/// Walks the entries of one index in the order the index holds them - by key, then by row pointer - for an
/// <see cref="IndexCursor"/>. A place in the index is an entry's key and row pointer, not a page and a slot, so it
/// stays good however the tree is changed between two moves: each move finds its entry again from the place it
/// is given. The leaf last read is kept and used while nothing has been written to the file
/// (<see cref="PageFile.ChangeStamp"/>), so walking an index that is not being changed reads each leaf once.
/// </summary>
internal sealed class IndexEntryWalker
{
    private readonly PageFile    _file;
    private readonly IndexWriter _tree;
    private readonly int         _root;

    private IndexWriter.Leaf? _leaf;
    private long              _stamp;

    internal IndexEntryWalker(PageFile file, int rootPage)
    {
        _file = file;
        _tree = new IndexWriter(file, new PageAllocator(file));
        _root = rootPage;
    }

    /// <summary>The index's first entry, or null when it has none.</summary>
    internal (byte[] Key, int RowPointer)? First()
    {
        var leaf = Keep(_tree.EndLeaf(_root, last: false));
        while (leaf.Entries.Count == 0 && leaf.Next > 0) leaf = Keep(_tree.ReadLeaf(leaf.Next));
        return leaf.Entries.Count > 0 ? leaf.Entries[0] : null;
    }

    /// <summary>The index's last entry, or null when it has none.</summary>
    internal (byte[] Key, int RowPointer)? Last()
    {
        var leaf = Keep(_tree.EndLeaf(_root, last: true));
        while (leaf.Entries.Count == 0 && leaf.Prev > 0) leaf = Keep(_tree.ReadLeaf(leaf.Prev));
        return leaf.Entries.Count > 0 ? leaf.Entries[leaf.Entries.Count - 1] : null;
    }

    /// <summary>
    /// The first entry after (<paramref name="key"/>, <paramref name="rowPointer"/>), or null when there is none.
    /// No row is at pointer 0, so with it this is the first entry whose key is <paramref name="key"/> or later.
    /// </summary>
    internal (byte[] Key, int RowPointer)? After(byte[] key, int rowPointer)
    {
        // The kept leaf answers when the place is within it, or after the last entry of the last leaf: the entry
        // is in it, or is the first of the next leaf. A place further off is found from the root.
        var leaf = IsKept && _leaf!.Entries.Count > 0 && Compare(_leaf.Entries[0], key, rowPointer) <= 0
                   && (_leaf.Next <= 0 || Compare(_leaf.Entries[_leaf.Entries.Count - 1], key, rowPointer) >= 0)
            ? _leaf
            : Keep(_tree.LeafFor(_root, key, rowPointer));
        while (true)
        {
            int at = FirstAfter(leaf.Entries, key, rowPointer, orAt: false);
            if (at < leaf.Entries.Count) return leaf.Entries[at];
            if (leaf.Next <= 0) return null;
            leaf = Keep(_tree.ReadLeaf(leaf.Next));
        }
    }

    /// <summary>The last entry before (<paramref name="key"/>, <paramref name="rowPointer"/>), or null when there is none.</summary>
    internal (byte[] Key, int RowPointer)? Before(byte[] key, int rowPointer)
    {
        // The kept leaf answers when the place is within it, or before the first entry of the first leaf: the
        // entry is in it, or is the last of the leaf before. A place further off is found from the root.
        IndexWriter.Leaf leaf;
        if (IsKept && _leaf!.Entries.Count > 0 && Compare(_leaf.Entries[_leaf.Entries.Count - 1], key, rowPointer) >= 0
            && (_leaf.Prev <= 0 || Compare(_leaf.Entries[0], key, rowPointer) <= 0))
        {
            leaf = _leaf;
        }
        else
        {
            // The leaf the tree leads to can end before the place (LeafFor): the leaves after it are looked at
            // until one begins at or after it.
            leaf = Keep(_tree.LeafFor(_root, key, rowPointer));
            while (leaf.Next > 0 && (leaf.Entries.Count == 0 || Compare(leaf.Entries[leaf.Entries.Count - 1], key, rowPointer) < 0))
            {
                var next = _tree.ReadLeaf(leaf.Next);
                if (next.Entries.Count > 0 && Compare(next.Entries[0], key, rowPointer) >= 0) break;
                leaf = Keep(next);
            }
        }
        while (true)
        {
            int at = FirstAfter(leaf.Entries, key, rowPointer, orAt: true) - 1;
            if (at >= 0) return leaf.Entries[at];
            if (leaf.Prev <= 0) return null;
            leaf = Keep(_tree.ReadLeaf(leaf.Prev));
        }
    }

    private bool IsKept => _leaf is not null && _stamp == _file.ChangeStamp;

    private IndexWriter.Leaf Keep(IndexWriter.Leaf leaf)
    {
        _leaf  = leaf;
        _stamp = _file.ChangeStamp;
        return leaf;
    }

    // The first entry after the place, or at or after it; entries.Count when there is none.
    private static int FirstAfter(List<(byte[] Key, int RowPointer)> entries, byte[] key, int rowPointer, bool orAt)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            int cmp = Compare(entries[mid], key, rowPointer);
            if (cmp < 0 || (cmp == 0 && !orAt)) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // By key, then by row pointer (its page, then its row), as the tree orders its entries.
    private static int Compare((byte[] Key, int RowPointer) entry, byte[] key, int rowPointer)
    {
        int cmp = CompareKeys(entry.Key, key);
        return cmp != 0 ? cmp : ((uint)entry.RowPointer).CompareTo((uint)rowPointer);
    }

    /// <summary>Two keys in the order an index holds them: byte by byte, the shorter first when one begins the other.</summary>
    internal static int CompareKeys(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int diff = a[i] - b[i];
            if (diff != 0) return diff;
        }
        return a.Length - b.Length;
    }

    /// <summary>Whether <paramref name="key"/> begins with <paramref name="prefix"/>.</summary>
    internal static bool BeginsWith(byte[] key, byte[] prefix)
        => key.Length >= prefix.Length && key.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    /// <summary>
    /// The first key, in an index's order, that is after every key beginning with <paramref name="prefix"/>; null
    /// when no key can be (the prefix is all 0xFF).
    /// </summary>
    internal static byte[]? AfterAllBeginningWith(byte[] prefix)
    {
        int last = prefix.Length - 1;
        while (last >= 0 && prefix[last] == 0xFF) last--;
        if (last < 0) return null;
        var after = new byte[last + 1];
        Array.Copy(prefix, after, after.Length);
        after[last]++;
        return after;
    }
}
