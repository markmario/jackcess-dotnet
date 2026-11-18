using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Parses the binary content of a Table-Definition (TDEF) page and extracts the
/// column list plus the Usage-Map references.
///
/// TDEF page layout (Jet4 / Access 2000+):
///   [0..62]  TDEF header   (SizeTdefHeader = 63 bytes)
///   [63..]   Index row-count blocks  (numIndexes × SizeIndexDefinition)  ← before columns
///   [..]     Column definitions      (numCols    × 25 bytes)
///   [..]     Index column blocks     (numIndexes × SizeIndexColumnBlock)  ← after columns
///   [..]     Logical-index info      (numLogicalIndexes × SizeIndexInfoBlock)
///   [..]     Logical-index names     (numLogicalIndexes × (2 + name_bytes))
///   [..]     Column names            (numCols            × (2 + name_bytes))
///   [..]     TDEF trailer            (0xFF 0xFF)
/// </summary>
internal static class TdefReader
{
    public readonly record struct TdefInfo(
        IReadOnlyList<Column> Columns,
        int TotalRowCount,
        int OwnedPagesUmapPage,
        int OwnedPagesUmapRow,
        int FreeSpaceUmapPage,
        int FreeSpaceUmapRow,
        IReadOnlyDictionary<string, LvalUmapRef> LvalColumnUmapPages,
        IReadOnlyList<Index> Indexes,
        bool HasUnreadIndexes = false,
        // One more than the highest column number the table has had, and the number of variable-length offsets
        // its rows carry: both count the columns Access has deleted, which keep their numbers and places.
        int MaxColumnCount = 0,
        int MaxVarColumnCount = 0,
        // The columns' names ran past the definition's first page, which is all that is read: the columns are
        // not all known as Access has them. Reading goes on with what there is; a row is not written by them.
        bool HasUnreadColumns = false);

    /// <summary>Number of slots in an index column block's column array (always 10 in real Access).</summary>
    private const int MaxIndexColumns = 10;
    /// <summary>Sentinel column-number value meaning "this slot is unused".</summary>
    private const short ColumnUnused = unchecked((short)0xFFFF);

    /// <summary>
    /// Reads the TDEF binary and returns the column list and umap references.
    /// Only Jet4 is fully supported; Jet3 is a best-effort read.
    /// </summary>
    public static TdefInfo Read(byte[] page, JetFormat format)
    {
        if (page is null)   throw new ArgumentNullException(nameof(page));
        if (format is null) throw new ArgumentNullException(nameof(format));

        // ── Header fields ─────────────────────────────────────────────────────
        int totalRowCount      = ByteUtil.GetInt  (page, format.TdefOffsetNumRows);
        int numVarCols         = ByteUtil.GetShort(page, format.TdefOffsetNumVarCols);
        int numCols            = ByteUtil.GetShort(page, format.TdefOffsetNumCols);
        int numIndexSlots      = ByteUtil.GetInt  (page, format.TdefOffsetNumIndexSlots);
        int numIndexes         = ByteUtil.GetInt  (page, format.TdefOffsetNumIndexes);

        int ownedRow           = page[format.TdefOffsetOwnedRow];
        int ownedPage          = ByteUtil.Get3ByteInt(page, format.TdefOffsetOwnedPage);
        int freeRow            = page[format.TdefOffsetFreeRow];
        int freePage           = ByteUtil.Get3ByteInt(page, format.TdefOffsetFreePage);

        // ── Column definition area ────────────────────────────────────────────
        // Column defs start after the header + numIndexes index-row-count blocks.
        int colDefStart = format.SizeTdefHeader + numIndexes * format.SizeIndexDefinition;
        int colHdrSize  = format.SizeColumnHeader;   // Jet3 = 18, Jet4 = 25

        var columns = new List<Column>(numCols);

        // Read raw column metadata (names are stored separately, added below).
        for (int i = 0; i < numCols; i++)
        {
            int p = colDefStart + i * colHdrSize;

            var   dataType    = (DataType)page[p];
            short colNum      = ByteUtil.GetShort(page, p + format.OffsetColumnNumber);
            byte  flags       = page[p + format.OffsetColumnFlags];
            bool  isAutoNum   = (flags & 0x04) != 0;
            bool  isAutoGuid  = (flags & 0x40) != 0;
            short length      = ByteUtil.GetShort(page, p + format.OffsetColumnLength);
            short fixedOffset = ByteUtil.GetShort(page, p + format.OffsetColumnFixedDataOffset);
            short varIndex    = ByteUtil.GetShort(page, p + format.OffsetColumnVarTableIndex);

            // Build a temporary Column; name will be patched below.
            var col = new Column(
                name:           $"__col{i}__",   // placeholder
                dataType:       dataType,
                length:         length <= 0 ? dataType.GetDefaultSize() : length,
                isRequired:     false,
                isAutoNumber:   isAutoNum || isAutoGuid,
                allowZeroLength:false,
                precision:      page[p + format.OffsetColumnPrecision],
                scale:          page[p + format.OffsetColumnScale]);

            col.Ordinal = colNum;
            // The column's flag says where a row keeps the value, not its type: Access keeps a Long, a Date/Time,
            // a Replication ID or a Decimal among the variable-length values when the flag says so, and Text or
            // Binary among the fixed-length ones. Its place there is the definition's too (RowLayout).
            bool isVariable = (flags & 0x01) == 0;
            col.IsVariableLength = isVariable;
            col.FixedDataOffset  = isVariable ? (short)-1 : fixedOffset;
            col.VarLenTableIndex = isVariable ? varIndex : (short)-1;
            col.HasStoredPlace   = true;
            // Jet4's extended flags follow the flags byte: bits 6 and 7 mark a calculated column.
            col.IsCalculated     = format.Version != FileFormat.Access97 && (page[p + format.OffsetColumnFlags + 1] & 0xC0) != 0;
            // Jet4's extended flags follow the flags byte; bit 0 is "Unicode Compression".
            col.IsCompressedUnicode = format.Version != FileFormat.Access97
                && (page[p + format.OffsetColumnFlags + 1] & 0x01) != 0;
            if (dataType is DataType.Text or DataType.Memo)
                col.SortOrder = SortOrder.Read(page, p + format.OffsetColumnSortOrder, format);
            columns.Add(col);
        }

        // ── Locate column names ───────────────────────────────────────────────
        // Column names come IMMEDIATELY after column defs (before index blocks).
        // Layout (Jackcess Java confirmed):
        //   [colDefStart]                         numCols × SizeColumnHeader  column defs
        //   [colDefStart + numCols*colHdrSize]    numCols name entries  ← HERE
        //   [+ colNamesSize]                      numIndexes × SizeIndexColumnBlock
        //   [+ idx col blocks]                    numIndexSlots × SizeIndexInfoBlock
        //   [+ idx info blocks]                   numIndexSlots index name entries
        //   [+ idx names]                         long-value col usage maps
        //   [end]                                 0xFF 0xFF trailer
        int pos = colDefStart + numCols * colHdrSize;

        // Read column names and patch them in
        // A definition too long for one page goes on to another (the page's bytes 4-7 name it), which is not
        // read: a column whose name does not end on this page has no name, and the columns after it none either.
        int namesRead = 0;
        for (int i = 0; i < numCols && pos + format.SizeNameLength <= page.Length; i++)
        {
            (string name, int consumed) = ReadName(page, pos, format);
            pos += consumed;
            columns[i] = RenameColumn(columns[i], name);
            if (consumed > format.SizeNameLength) namesRead++;
        }
        bool columnsIncomplete = namesRead < numCols;

        // ── Index column blocks + slots + names ──────────────────────────────
        // Wrapped in try/fallback: a TDEF whose index sections spill past the
        // current page (via the next-TDEF-page pointer at bytes 4-7) would cause
        // out-of-bounds reads. Multi-page TDEF traversal is a future slice — for
        // now, bail to the old skip-with-arithmetic behaviour so plan A's reads
        // continue working.
        IReadOnlyList<Index> indexes;
        bool indexesIncomplete = false;
        int indexBlocksEnd = pos
            + numIndexes     * format.SizeIndexColumnBlock
            + numIndexSlots  * format.SizeIndexInfoBlock;

        if (indexBlocksEnd > page.Length)
        {
            // Multi-page TDEF: skip past the index sections without parsing them,
            // matching the pre-step1 behaviour.
            indexes = Array.Empty<Index>();
            pos = indexBlocksEnd;
            for (int i = 0; i < numIndexSlots && pos + format.SizeNameLength <= page.Length; i++)
            {
                (_, int consumed) = ReadName(page, pos, format);
                pos += consumed;
            }
        }
        else
        {
            indexes = ParseIndexes(page, format, ref pos, numIndexes, numIndexSlots, columns, out indexesIncomplete);
        }

        // Read LVAL usage-map refs. Real format (matches Java Jackcess):
        // 10 bytes per entry - [colNum:2][ownedRow:1][ownedPage:3][freeRow:1]
        // [freePage:3] - terminated by colNum == 0xFFFF. Entries are matched to
        // columns BY COLUMN NUMBER, not by order. (The previous 4-byte parse
        // read garbage page numbers out of real Access TDEFs, corrupting every
        // memo write into such files.)
        var lvalUmapPages = new Dictionary<string, LvalUmapRef>(StringComparer.OrdinalIgnoreCase);
        var lvalColsByNumber = columns.Where(c => c.DataType.IsLongValue())
                                      .ToDictionary(c => (int)c.Ordinal);
        while (lvalColsByNumber.Count > 0 && pos + 10 <= page.Length)
        {
            int colNum = ByteUtil.GetUShort(page, pos);
            if (colNum == 0xFFFF) break;
            int lvOwnedRow  = page[pos + 2];
            int lvOwnedPage = ByteUtil.Get3ByteInt(page, pos + 3);
            int lvFreeRow   = page[pos + 6];
            int lvFreePage  = ByteUtil.Get3ByteInt(page, pos + 7);
            pos += 10;
            if (lvalColsByNumber.TryGetValue(colNum, out var lvalCol) && lvOwnedPage > 0)
                lvalUmapPages[lvalCol.Name] = new LvalUmapRef(lvOwnedPage, lvOwnedRow, lvFreePage, lvFreeRow);
        }

        return new TdefInfo(
            Columns:              columns,
            TotalRowCount:        totalRowCount,
            OwnedPagesUmapPage:   ownedPage,
            OwnedPagesUmapRow:    ownedRow,
            FreeSpaceUmapPage:    freePage,
            FreeSpaceUmapRow:     freeRow,
            LvalColumnUmapPages:  lvalUmapPages,
            Indexes:              indexes,
            // Index blocks past the first page are not parsed, and a block or slot that names a column or a
            // block the definition has not is left out or cut short: the table has indexes that are not in
            // Indexes as Access has them. Reading tolerates that; a write must not trust them.
            HasUnreadIndexes:     indexesIncomplete || numIndexes > 0 && indexBlocksEnd > page.Length,
            // The count of columns ever made is kept just before the count of variable-length ones.
            MaxColumnCount:       ByteUtil.GetShort(page, format.TdefOffsetNumVarCols - 2),
            MaxVarColumnCount:    numVarCols,
            HasUnreadColumns:     columnsIncomplete);
    }

    /// <summary>Intermediate record: one decoded index column block.</summary>
    private readonly record struct IndexDataRecord(
        IReadOnlyList<IndexColumn> Columns, int RootPageNumber, byte Flags, int UmapPage, int UmapRow);

    /// <summary>Intermediate record: one decoded logical-index slot.</summary>
    private readonly record struct IndexSlotRecord(int IndexNumber, int IndexDataNumber, byte IndexType);

    /// <summary>
    /// Parses the index column blocks, the logical-index slot blocks, and the per-slot
    /// name entries that follow them. Caller must ensure the deterministic-size sections
    /// (blocks + slots) all fit on the current page. <paramref name="incomplete"/> is set when what is returned is
    /// not every index as the definition has it: a column of an index could not be found among the table's, a slot
    /// names a block there is not, or a block has no slot.
    /// </summary>
    private static IReadOnlyList<Index> ParseIndexes(
        byte[] page, JetFormat format, ref int pos,
        int numIndexes, int numIndexSlots, List<Column> columns, out bool incomplete)
    {
        incomplete = false;
        // Per index column block: SkipBeforeIndex + MaxIndexColumns × (short col + byte flags)
        //   + 4-byte umap ref + 4-byte root page + SkipBeforeIndexFlags + 1-byte flags + SkipAfterIndexFlags
        // Total = SizeIndexColumnBlock (39 Jet3 / 52 Jet4).
        var indexData = new IndexDataRecord[numIndexes];
        for (int i = 0; i < numIndexes; i++)
        {
            int blockStart = pos;
            int p          = pos + format.SkipBeforeIndex;

            var idxCols = new List<IndexColumn>(MaxIndexColumns);
            for (int c = 0; c < MaxIndexColumns; c++)
            {
                short colNum = ByteUtil.GetShort(page, p);
                byte  cFlags = page[p + 2];
                p += 3;
                if (colNum == ColumnUnused) continue;
                var col = columns.FirstOrDefault(x => x.Ordinal == colNum);
                if (col is null)             // tolerant of unknown column numbers, for reading
                {
                    incomplete = true;
                    continue;
                }
                idxCols.Add(new IndexColumn(col, cFlags));
            }

            int umapRow  = page[p];                         // used-pages UsageMap ref:
            int umapPage = ByteUtil.Get3ByteInt(page, p + 1); // 1-byte row + 3-byte page
            p += 4;
            int rootPage = ByteUtil.GetInt(page, p); p += 4;
            p += format.SkipBeforeIndexFlags;
            byte indexFlags = page[p]; p += 1;
            // SkipAfterIndexFlags absorbed by the deterministic block-size advance below.

            indexData[i] = new IndexDataRecord(idxCols, rootPage, indexFlags, umapPage, umapRow);
            pos = blockStart + format.SizeIndexColumnBlock;
        }

        // Per slot: SkipBeforeIndexSlot + indexNumber(int) + indexDataNumber(int)
        //   + relIndexType(byte) + relIndexNumber(int) + relTablePage(int)
        //   + cascadeUpdates(byte) + cascadeDeletes(byte) + indexType(byte) + SkipAfterIndexSlot
        // Total = SizeIndexInfoBlock (20 Jet3 / 28 Jet4).
        var slots = new IndexSlotRecord[numIndexSlots];
        for (int i = 0; i < numIndexSlots; i++)
        {
            int slotStart = pos;
            int p = pos + format.SkipBeforeIndexSlot;
            int indexNumber     = ByteUtil.GetInt(page, p); p += 4;
            int indexDataNumber = ByteUtil.GetInt(page, p); p += 4;
            p += 11;   // relIndexType + relIndexNumber + relTablePage + cascade flags
            byte indexType = page[p];
            slots[i] = new IndexSlotRecord(indexNumber, indexDataNumber, indexType);
            pos = slotStart + format.SizeIndexInfoBlock;
        }

        // One length-prefixed name per slot.
        var slotNames = new string[numIndexSlots];
        for (int i = 0; i < numIndexSlots && pos + format.SizeNameLength <= page.Length; i++)
        {
            (string name, int consumed) = ReadName(page, pos, format);
            slotNames[i] = name;
            pos += consumed;
        }

        var indexes = new List<Index>(numIndexSlots);
        for (int i = 0; i < numIndexSlots; i++)
        {
            var slot = slots[i];
            if (slot.IndexDataNumber < 0 || slot.IndexDataNumber >= indexData.Length)
            {
                incomplete = true;
                continue;
            }
            var data = indexData[slot.IndexDataNumber];
            indexes.Add(new Index(
                name:           slotNames[i] ?? $"__index_{i}__",
                columns:        data.Columns,
                rootPageNumber: data.RootPageNumber,
                indexNumber:    slot.IndexNumber,
                flags:          data.Flags,
                indexType:      slot.IndexType,
                indexDataNumber:   slot.IndexDataNumber,
                usedPagesUmapPage: data.UmapPage,
                usedPagesUmapRow:  data.UmapRow));
        }
        if (indexes.Select(ix => ix.IndexDataNumber).Distinct().Count() != numIndexes)
            incomplete = true;
        return indexes;
    }

    /// <summary>
    /// Reads a length-prefixed name from the TDEF buffer at <paramref name="pos"/>:
    ///   Jet3 → 1-byte length + cp1252 bytes
    ///   Jet4 → 2-byte length + UTF-16LE bytes
    /// Returns (decoded name, total bytes consumed including the length prefix).
    /// </summary>
    private static (string name, int consumed) ReadName(byte[] page, int pos, JetFormat format)
    {
        int nameLen = format.SizeNameLength == 2
            ? ByteUtil.GetShort(page, pos)
            : page[pos];
        int nameStart = pos + format.SizeNameLength;
        if (nameLen < 0 || nameStart + nameLen > page.Length)
            return ($"__bad_name_at_{pos}__", format.SizeNameLength);

        string name = format.SizeNameLength == 2
            ? Encoding.Unicode.GetString(page, nameStart, nameLen)
            : format.TextEncoding.GetString(page, nameStart, nameLen);
        return (name, format.SizeNameLength + nameLen);
    }

    // Column is a sealed class with internal ctor, so we rebuild it with the real name.
    private static Column RenameColumn(Column src, string name) =>
        new Column(
            name:           name,
            dataType:       src.DataType,
            length:         src.LengthInBytes,
            isRequired:     src.IsRequired,
            isAutoNumber:   src.IsAutoNumber,
            allowZeroLength:src.AllowZeroLength,
            precision:      src.Precision,
            scale:          src.Scale)
        {
            Ordinal          = src.Ordinal,
            FixedDataOffset  = src.FixedDataOffset,
            VarLenTableIndex = src.VarLenTableIndex,
            IsVariableLength = src.IsVariableLength,
            HasStoredPlace   = src.HasStoredPlace,
            IsCalculated     = src.IsCalculated,
            IsCompressedUnicode = src.IsCompressedUnicode,
            SortOrder        = src.SortOrder,
        };
}
