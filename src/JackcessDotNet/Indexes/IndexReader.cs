using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Read-only B-tree walker for indexes parsed from Access-authored .mdb files.
///
/// Scope of this implementation (B-step2):
///   • Single-column indexes only
///   • Integer key types only (Byte, Int, Long) — text/numeric/date deferred to step 4
///   • Both ascending and descending sort
///   • Full B-tree descent: node → leaf, with shared-prefix decompression
///   • No write-side mutations (covered separately in step 3)
///
/// Page layout (per Jet docs + Jackcess Java):
///   [0]             page type:  0x03 = node, 0x04 = leaf
///   [1]             0x01
///   [2..3]          free space (short)
///   [OffsetPrev]    prev-page link (int, sentinel 0xFFFFFFFF)
///   [OffsetNext]    next-page link  (int)
///   [OffsetChildTail] child-tail page link (for nodes — points at a "tail" subtree
///                   for keys greater than all entry keys on this page)
///   [OffsetICBC]    compressed-prefix byte count (ushort; one byte in Jet3), then the page's level (byte)
///   [OffsetEntryMask] entry-mask bitmap (SizeIndexEntryMask bytes). Bit at cumulative
///                   byte-position B = an entry boundary at byte (entriesStart + B).
///   [entriesStart]  entries packed sequentially; first entry stored full, subsequent
///                   entries omit the leading PrefixLen bytes that match the first.
/// </summary>
internal sealed class IndexReader
{
    private const int NoPageSentinel = unchecked((int)0xFFFFFFFFu);
    private readonly PageFile  _file;
    private readonly JetFormat _format;
    private readonly Index     _index;
    private readonly bool      _isAscending;

    public IndexReader(PageFile file, Index index)
    {
        _file        = file  ?? throw new ArgumentNullException(nameof(file));
        _format      = file.Format;
        _index       = index ?? throw new ArgumentNullException(nameof(index));
        _isAscending = index.Columns.Count > 0 && index.Columns[0].IsAscending;
    }

    /// <summary>
    /// True when <see cref="FindRowPointers"/> can resolve a key for this index.
    /// Single- and multi-column indexes are supported; each column must be one
    /// of the encodable types (Byte/Int16/Int32/Int64/DateTimeExtended/Text), and a Text column must sort in
    /// an order whose keys this library makes (<see cref="TextIndexCodes.For"/>): a
    /// key made in one order is not found among keys sorted in another.
    /// </summary>
    public bool CanResolveKey
        => _index.Columns.Count >= 1 && _index.Columns.All(c => CanResolve(c.Column));

    /// <summary>True when a key on <paramref name="column"/> can be made as Access made the index's.</summary>
    internal static bool CanResolve(Column column)
        => column.DataType is DataType.Byte or DataType.Int16 or DataType.Int32 or DataType.Int64 or DataType.DateTimeExtended
        || column.DataType == DataType.Text && TextIndexCodes.For(column.SortOrder) is not null;

    /// <summary>
    /// Walks the B-tree rooted at <see cref="Index.RootPageNumber"/> and yields the
    /// <see cref="RowPointer"/> for every leaf entry whose
    /// key bytes equal those of <paramref name="key"/>. Single-column shortcut.
    /// </summary>
    public IEnumerable<int> FindRowPointers(object key)
        => FindRowPointersForEntry(new[] { (object?)key });

    /// <summary>
    /// Composite-key variant. Encodes one segment per index column in declaration
    /// order, concatenates them, and walks the tree just like the single-column case.
    /// </summary>
    public IEnumerable<int> FindRowPointersForEntry(object?[] keys)
    {
        if (!CanResolveKey) yield break;
        if (keys.Length != _index.Columns.Count) yield break;

        // The key Access made for these values: each column's part in its own order and, for text, sort order.
        byte[] searchKey = IndexKeys.Encode(_index.Columns, keys);
        foreach (int rowPtr in DescendAndCollect(_index.RootPageNumber, searchKey))
            yield return rowPtr;
    }

    /// <summary>
    /// As <see cref="FindRowPointersForEntry"/>, for a key already made - or read from the index itself, which is
    /// how a test walks an index whose keys this library does not make.
    /// </summary>
    internal IEnumerable<int> FindRowPointersForKey(byte[] key) => DescendAndCollect(_index.RootPageNumber, key);

    // ── Tree walk ────────────────────────────────────────────────────────────

    private IEnumerable<int> DescendAndCollect(int pageNum, byte[] searchKey)
    {
        if (pageNum <= 0 || pageNum == NoPageSentinel) yield break;
        var page = ReadIndexPage(pageNum);
        if (page.IsLeaf)
        {
            // The rows of one key can run on from the leaf the tree leads to into the leaves after it: an index
            // that is not unique holds as many entries for a key as rows have it.
            while (true)
            {
                foreach (var entry in page.Entries)
                {
                    int cmp = CompareBytes(entry.KeyBytes, searchKey);
                    if (cmp == 0)
                        yield return RowPointer.Pack(entry.RowPage & RowPointer.MaxPage, entry.RowIndex & 0xFF);
                    else if (cmp > 0)
                        yield break;   // entries are sorted ASC — no further matches possible
                }
                if (page.NextPage <= 0 || page.NextPage == NoPageSentinel) yield break;
                page = ReadIndexPage(page.NextPage);
                if (!page.IsLeaf) yield break;
            }
        }

        // Node page: descend into the FIRST child whose entry.key >= searchKey.
        // If every entry.key < searchKey, descend into the child-tail page if present,
        // else into the last entry's sub-page.
        int target = -1;
        foreach (var entry in page.Entries)
        {
            int cmp = CompareBytes(entry.KeyBytes, searchKey);
            if (cmp >= 0)
            {
                target = entry.SubPage;
                break;
            }
        }
        if (target < 0)
        {
            target = page.ChildTailPage != NoPageSentinel && page.ChildTailPage > 0
                   ? page.ChildTailPage
                   : page.Entries.Count > 0 ? page.Entries[^1].SubPage : -1;
        }
        if (target <= 0 || target == NoPageSentinel) yield break;

        foreach (int rowPtr in DescendAndCollect(target, searchKey))
            yield return rowPtr;
    }

    // ── Page parse ───────────────────────────────────────────────────────────

    private IndexPage ReadIndexPage(int pageNumber)
    {
        byte[] buf = _file.ReadPage(pageNumber);
        byte pageType = buf[0];
        if (pageType != 0x03 && pageType != 0x04)
            throw new InvalidDataException(
                $"Expected index page (0x03 or 0x04) at page {pageNumber}; found 0x{pageType:X2}.");

        bool isLeaf = pageType == 0x04;
        int  prev   = ByteUtil.GetInt(buf, _format.OffsetPrevIndexPage);
        int  next   = ByteUtil.GetInt(buf, _format.OffsetNextIndexPage);
        int  child  = ByteUtil.GetInt(buf, _format.OffsetChildTailIndexPage);
        int  prefixLen   = ReadPrefixLength(buf, _format);
        int  maskOffset  = _format.OffsetIndexEntryMask;
        int  maskLength  = _format.SizeIndexEntryMask;
        int  entriesPos  = maskOffset + maskLength;

        byte[]? sharedPrefix = null;
        var entries = new List<IndexEntryRec>();
        int lastStart = 0;

        for (int i = 0; i < maskLength; i++)
        {
            byte b = buf[maskOffset + i];
            if (b == 0) continue;
            for (int j = 0; j < 8; j++)
            {
                if ((b & (1 << j)) == 0) continue;
                int endOffset = i * 8 + j;
                int storedLen = endOffset - lastStart;

                int entryStart = entriesPos + lastStart;
                if (entryStart + storedLen > buf.Length || storedLen < 0)
                {
                    lastStart = endOffset;
                    continue;
                }

                // Compose the full entry bytes: first entry is stored full;
                // later entries are prefix+storedBytes.
                byte[] full;
                if (entries.Count == 0)
                {
                    full = new byte[storedLen];
                    Array.Copy(buf, entryStart, full, 0, storedLen);
                    if (prefixLen > 0 && storedLen >= prefixLen)
                    {
                        sharedPrefix = new byte[prefixLen];
                        Array.Copy(buf, entryStart, sharedPrefix, 0, prefixLen);
                    }
                }
                else if (sharedPrefix is not null)
                {
                    full = new byte[sharedPrefix.Length + storedLen];
                    Array.Copy(sharedPrefix, 0, full, 0, sharedPrefix.Length);
                    Array.Copy(buf, entryStart, full, sharedPrefix.Length, storedLen);
                }
                else
                {
                    full = new byte[storedLen];
                    Array.Copy(buf, entryStart, full, 0, storedLen);
                }

                // Split into [key bytes][3 page][1 row][4 sub-page (nodes only)].
                int trailerLen = isLeaf ? 4 : 8;
                if (full.Length < trailerLen) { lastStart = endOffset; continue; }
                int keyLen = full.Length - trailerLen;

                var keyBytes = new byte[keyLen];
                Array.Copy(full, 0, keyBytes, 0, keyLen);

                // RowId page is BIG-ENDIAN 3 bytes, then 1-byte row.
                int rowPage = (full[keyLen] << 16) | (full[keyLen + 1] << 8) | full[keyLen + 2];
                int rowIdx  = full[keyLen + 3];
                int subPage = 0;
                if (!isLeaf)
                {
                    subPage = (full[keyLen + 4] << 24)
                            | (full[keyLen + 5] << 16)
                            | (full[keyLen + 6] << 8)
                            |  full[keyLen + 7];
                }

                entries.Add(new IndexEntryRec(keyBytes, rowPage, rowIdx, subPage));
                lastStart = endOffset;
            }
        }

        return new IndexPage(isLeaf, entries, prev, next, child);
    }

    /// <summary>
    /// How many leading bytes the entries of an index page share: two bytes of the page in Jet4, one in Jet3,
    /// where the next byte is the page's level (<see cref="JetFormat.SizeIndexCompressedByteCount"/>).
    /// </summary>
    internal static int ReadPrefixLength(byte[] page, JetFormat format)
        => format.SizeIndexCompressedByteCount == 1
            ? page[format.OffsetIndexCompressedByteCount]
            : ByteUtil.GetUShort(page, format.OffsetIndexCompressedByteCount);

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

    // ── Records ──────────────────────────────────────────────────────────────

    private readonly record struct IndexEntryRec(byte[] KeyBytes, int RowPage, int RowIndex, int SubPage);

    private sealed record IndexPage(
        bool IsLeaf,
        List<IndexEntryRec> Entries,
        int PrevPage,
        int NextPage,
        int ChildTailPage);
}
