using System.Linq;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Holds the schema of a table plus the page-level state needed to write rows.
/// </summary>
internal sealed class TableDefinition
{
    private const int     MagicTableNumber = 1625;
    private const int     MagicIndexNumber = 1923;
    private const byte    TableTypeUser    = 0x4E;

    private const byte    ColumnFlagFixedLen      = 0x01;
    private const byte    ColumnFlagUpdatable     = 0x02;
    private const byte    ColumnFlagAutoNumber    = 0x04;
    private const byte    ColumnFlagAutoNumberGuid= 0x40;

    // Per-index sort-flag bits (per Jackcess IndexData)
    internal const byte   AscendingColumnFlag     = 0x01;
    // Per-index index-data-flags byte (per Jackcess IndexData)
    internal const byte   UniqueIndexFlag         = 0x01;
    internal const byte   IgnoreNullsIndexFlag    = 0x02;
    internal const byte   RequiredIndexFlag       = 0x08;
    internal const byte   UnknownIndexFlag        = 0x80;   // always set on Access 2000+ indexes
    // Index slot's index-type byte
    internal const byte   PrimaryKeyIndexType     = 0x01;
    // Sentinel meaning "no related index"
    private const int     InvalidIndexNumber      = -1;

    // ── Schema ────────────────────────────────────────────────────────────────
    public string               Name    { get; }
    public IReadOnlyList<Column> Columns { get; }

    // ── Page-level state (set by Database.CreateTable / GetTable) ────────────
    /// <summary>Page number that contains this table's TDEF.</summary>
    public int TdefPageNumber   { get; set; }
    /// <summary>Page number that contains the usage-map rows for this table.</summary>
    public int UmapPageNumber   { get; set; }
    /// <summary>Row index inside the umap page that holds the owned-pages bitmap.</summary>
    public int OwnedPagesRow    { get; set; }
    /// <summary>Row index inside the umap page that holds the free-space bitmap.</summary>
    public int FreeSpaceRow     { get; set; }

    private int? _freeSpaceUmapPage;

    /// <summary>
    /// The page of the table's free-space map, the data pages where a row goes first (<see cref="ListedDataPages"/>):
    /// what the definition names - 0 when it names none - which Access puts on the page of the owned-pages map, as this
    /// library does for a table it creates. <see cref="UmapPageNumber"/> until set.
    /// </summary>
    internal int FreeSpaceUmapPage
    {
        get => _freeSpaceUmapPage ?? UmapPageNumber;
        set => _freeSpaceUmapPage = value;
    }

    // ── Primary-key index (optional) ─────────────────────────────────────────
    /// <summary>Page number of the primary key index's root page (0 if no primary key index).</summary>
    public int     PrimaryKeyIndexPage   { get; set; }
    /// <summary>
    /// The usage map listing the primary key's index pages: its page (0 when there
    /// is none) and row. A table this library makes keeps it on its own usage-map
    /// page, in the row after the free-space map, as Access does.
    /// </summary>
    internal int   PrimaryKeyIndexUmapPage   { get; set; }
    internal int   PrimaryKeyIndexUmapRow    { get; set; }
    /// <summary>
    /// Name of the primary key column for single-column PKs (null when there's
    /// no PK <i>or</i> the PK is composite). For composite PKs use
    /// <see cref="PrimaryKeyColumnNames"/>.
    /// </summary>
    public string? PrimaryKeyColumnName  { get; set; }
    /// <summary>
    /// Names of the PK columns for composite keys, in declaration order
    /// (max 10 entries — Jet's hard limit per index). Null/empty when the PK
    /// is single-column (see <see cref="PrimaryKeyColumnName"/>) or absent.
    /// </summary>
    public IReadOnlyList<string>? PrimaryKeyColumnNames { get; set; }

    /// <summary>
    /// Resolved list of PK column names. Empty when the table has no PK,
    /// single-element for the legacy single-column case, multi-element for
    /// composite keys.
    /// </summary>
    public IReadOnlyList<string> EffectivePrimaryKeyColumns =>
        PrimaryKeyColumnNames is { Count: > 0 } cols
            ? cols
            : PrimaryKeyColumnName is { } single
                ? new[] { single }
                : Array.Empty<string>();

    // ── Long-value column usage maps (one per Memo/OLE column) ───────────────
    /// <summary>
    /// Usage-map page numbers for long-value (Memo/OLE) columns, keyed by column name.
    /// Populated by <see cref="Database.CreateTable(string, IEnumerable{ColumnDefinition}, string[])"/> before <see cref="Serialize"/> is called.
    /// </summary>
    public Dictionary<string, LvalUmapRef> LvalColumnUmapPages { get; }
        = new Dictionary<string, LvalUmapRef>(StringComparer.Ordinal);

    /// <summary>
    /// How the names of the table's columns and indexes are one to Access (<see cref="ObjectNames"/>): by General -
    /// Legacy's keys, or in an Access 97 file by Access 97's.
    /// </summary>
    internal ObjectNames Names { get; set; } = ObjectNames.GeneralLegacy;

    private HashSet<string>? _columnNameSet;

    /// <summary>The names of the table's columns, exactly as the definition has them.</summary>
    internal HashSet<string> ColumnNameSet => _columnNameSet ??= new HashSet<string>(Columns.Select(c => c.Name), StringComparer.Ordinal);

    private bool? _hasColumnsThatAreOne;

    /// <summary>
    /// Whether two of the table's columns have names Access takes as one: a table Access let have them - it makes two
    /// fields "x" and "x " - whose columns this library looks up only by their exact names when it writes a row.
    /// </summary>
    internal bool HasColumnsThatAreOne => _hasColumnsThatAreOne ??= Names.FirstTwoThatAreOne(Columns.Select(c => c.Name).ToList()) is not null;

    /// <summary>
    /// The column a lookup of <paramref name="name"/> finds: the column of exactly that name, or else the first whose
    /// name Access takes as one with it (<see cref="ObjectNames.Find"/>); null when there is none.
    /// </summary>
    internal Column? FindColumn(string name)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (Columns[i].Name == name) return Columns[i];
        int at = Names.Find(_columnNames ??= Columns.Select(c => c.Name).ToList(), name);
        return at < 0 ? null : Columns[at];
    }

    // The columns' names in their order, for a lookup by a name of another spelling: a row a table gave looks up so.
    private List<string>? _columnNames;

    /// <summary>The index a lookup of <paramref name="name"/> finds, as <see cref="FindColumn"/> finds a column; null when there is none.</summary>
    internal Index? FindIndex(string name)
    {
        for (int i = 0; i < Indexes.Count; i++)
            if (Indexes[i].Name == name) return Indexes[i];
        int at = Names.Find(Indexes.Select(ix => ix.Name).ToList(), name);
        return at < 0 ? null : Indexes[at];
    }

    /// <summary>
    /// The table's indexes: parsed from the on-disk TDEF, or those it was created with
    /// (its primary key first).
    /// </summary>
    public IReadOnlyList<Index> Indexes { get; internal set; } = Array.Empty<Index>();

    /// <summary>
    /// True when the table has indexes this library did not read: its definition is damaged - its index blocks
    /// run past its end, or it names an index column or block it has not. Such a table is not changed - a row
    /// written without its index entries is one Access does not find through them.
    /// </summary>
    internal bool HasUnreadIndexes { get; set; }

    /// <summary>
    /// The column count a row of this table is written with: one more than the highest column number the table has
    /// ever had. Access leaves a deleted column's number behind, so it can be more than <see cref="Columns"/> has.
    /// 0 until the definition is read or written.
    /// </summary>
    internal int MaxColumnCount { get; set; }

    /// <summary>
    /// The number of variable-length offsets a row of this table is written with, a deleted column's among them.
    /// 0 until the definition is read or written.
    /// </summary>
    internal int MaxVarColumnCount { get; set; }

    /// <summary>
    /// True when the table's columns were not all read: its definition is damaged, and ends before the column
    /// names do. Such a table's rows are not written - a row laid out by half the columns is not one Access reads.
    /// </summary>
    internal bool HasUnreadColumns { get; set; }

    public TableDefinition(string name, IReadOnlyList<Column> columns)
    {
        Name    = name    ?? throw new ArgumentNullException(nameof(name));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
    }

    /// <summary>The name Access gives a table's primary key index.</summary>
    internal const string PrimaryKeyIndexName = "PrimaryKey";

    /// <summary>
    /// The indexes <see cref="Serialize"/> writes: <see cref="Indexes"/> when the table has them, and otherwise the
    /// primary key the <c>PrimaryKey…</c> properties describe, as index 0.
    /// </summary>
    private IReadOnlyList<Index> IndexesToWrite(IReadOnlyList<string> pkColumns)
    {
        if (Indexes.Count > 0) return Indexes;
        if (pkColumns.Count == 0 || PrimaryKeyIndexPage <= 0) return Array.Empty<Index>();

        var columns = pkColumns.Select(name => new IndexColumn(
            FindColumn(name) ?? throw new InvalidOperationException($"Primary key column '{name}' not found in table '{Name}'."),
            AscendingColumnFlag)).ToList();
        return new[]
        {
            new Index(PrimaryKeyIndexName, columns, PrimaryKeyIndexPage, indexNumber: 0,
                      flags: UnknownIndexFlag | UniqueIndexFlag | RequiredIndexFlag, indexType: PrimaryKeyIndexType,
                      indexDataNumber: 0, usedPagesUmapPage: PrimaryKeyIndexUmapPage, usedPagesUmapRow: PrimaryKeyIndexUmapRow),
        };
    }

    // ── Serialisation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises the table definition to a byte array that is ready to be written
    /// to its pages (<see cref="TdefBuffer.Write"/>): at least <see cref="JetFormat.PageSize"/> bytes,
    /// and as many as the definition takes when that is more than a page; unused trailing space is left as zero.
    ///
    /// Layout produced (Jet4):
    ///   [0..62]   TDEF header  (63 bytes)
    ///   [63..]    Column definitions  (numCols × 25 bytes)   ← no index blocks (0 indexes)
    ///   [..]      Column names        (2 + UTF-16LE per name)
    ///   [..]      Trailer             (0xFF 0xFF)
    /// </summary>
    public byte[] Serialize(JetFormat format)
    {
        if (format is null) throw new ArgumentNullException(nameof(format));
        if (Columns.Count == 0)
            throw new InvalidOperationException("Table must have at least one column.");
        if (Columns.Count > 255)
            throw new InvalidOperationException("Tables with more than 255 columns are not supported.");
        if (format.IsReadOnly)
            throw PageFile.ReadOnlyFormat($"no definition of table '{Name}' is made for one.");

        // Compute column-name bytes (always UTF-16LE in TDEF)
        byte[][] nameBytes = Columns.Select(c => System.Text.Encoding.Unicode.GetBytes(c.Name)).ToArray();

        var pkColumns = EffectivePrimaryKeyColumns;
        if (pkColumns.Count > 10)
            throw new InvalidOperationException(
                $"Composite primary keys are limited to 10 columns; got {pkColumns.Count}.");

        // The indexes to write: those the table was given, or, for a definition that only names a
        // primary key, that key. One block per index's data, one slot and one name per index.
        var indexes = IndexesToWrite(pkColumns);
        var blocks  = indexes.GroupBy(ix => ix.IndexDataNumber).OrderBy(g => g.Key).Select(g => g.First()).ToList();
        int  numIndexes    = blocks.Count;
        int  numIndexSlots = indexes.Count;
        byte[][] indexNameBytes = indexes.Select(ix => System.Text.Encoding.Unicode.GetBytes(ix.Name)).ToArray();

        int headerSize     = format.SizeTdefHeader;                  // 63 (Jet4) or 43 (Jet3)
        int idxDefSection  = numIndexes * format.SizeIndexDefinition;
        int colDefsSize    = format.SizeColumnHeader * Columns.Count;
        int nameSection    = nameBytes.Sum(b => 2 + b.Length);
        int idxColBlocks   = numIndexes * format.SizeIndexColumnBlock;
        int idxInfoBlocks  = numIndexSlots * format.SizeIndexInfoBlock;
        int idxNameSection = indexNameBytes.Sum(b => 2 + b.Length);
        int lvalSection    = Columns.Count(c => c.DataType.IsLongValue()) * 10;
        int trailerSize    = 2;

        int contentSize = headerSize + idxDefSection + colDefsSize + nameSection
                        + idxColBlocks + idxInfoBlocks + idxNameSection
                        + lvalSection + trailerSize;
        // The buffer is a full page at the least; content starts at byte 0. A definition longer than a page is
        // laid out as one run of bytes, and put on its pages when it is written.
        var page = new byte[Math.Max(contentSize, format.PageSize)];

        // ── 8-byte page prefix ────────────────────────────────────────────────
        page[0] = JetFormat.PageTypeTableDef;
        page[1] = 0x01;
        page[2] = 0x00;
        page[3] = 0x00;
        ByteUtil.PutInt(page, 4, 0);   // next-TDEF-page: set when the definition is put on its pages

        int pos = 8;

        // ── TDEF header (varies by format) ────────────────────────────────────
        ByteUtil.PutInt  (page, pos, contentSize); pos += 4;  // total content size
        ByteUtil.PutInt  (page, pos, MagicTableNumber); pos += 4;
        ByteUtil.PutInt  (page, pos, 0); pos += 4;   // num rows (initially 0)
        ByteUtil.PutInt  (page, pos, 0); pos += 4;   // last autonumber
        page[pos++] = 0x01;                           // autonumber flag

        // SizeTdefHeader is measured from byte 0 of the page, so we subtract
        // the current absolute write position (pos) plus the size of all
        // remaining fixed fields that follow the unknown padding.
        int unknownPad = format.SizeTdefHeader - pos
                         - 1  /* table type */
                         - 2  /* max cols */
                         - 2  /* var cols */
                         - 2  /* num cols */
                         - 4  /* logical index count */
                         - 4  /* real index count */
                         - 4  /* owned-pages umap ref  (1 row byte + 3 page bytes) */
                         - 4  /* free-space umap ref   (1 row byte + 3 page bytes) */;
        for (int i = 0; i < unknownPad; i++) page[pos++] = 0x00;

        page[pos++] = TableTypeUser;
        ByteUtil.PutShort(page, pos, (short)Columns.Count); pos += 2;  // max cols
        ByteUtil.PutShort(page, pos, (short)Columns.Count(c => c.DataType.IsVariableLength())); pos += 2;
        ByteUtil.PutShort(page, pos, (short)Columns.Count); pos += 2;  // col count
        ByteUtil.PutInt  (page, pos, numIndexSlots); pos += 4;   // logical index count
        ByteUtil.PutInt  (page, pos, numIndexes);    pos += 4;   // real index count

        // Owned-pages umap reference: row 0 of UmapPageNumber
        page[pos++] = (byte)OwnedPagesRow;
        ByteUtil.Put3ByteInt(page, pos, UmapPageNumber); pos += 3;

        // Free-space umap reference: row 1 of UmapPageNumber
        page[pos++] = (byte)FreeSpaceRow;
        ByteUtil.Put3ByteInt(page, pos, UmapPageNumber); pos += 3;

        // pos should now == SizeTdefHeader (the header size includes the 8-byte page prefix)
        pos = headerSize;

        // ── Index row-count blocks (zeros — Jackcess does the same) ──────────
        pos += idxDefSection;

        // ── Assign column layout ──────────────────────────────────────────────
        var fixedOffsets = new Dictionary<Column, short>();
        var varIndexes   = new Dictionary<Column, short>();
        short fixedOff = 0;
        short varIdx   = 0;

        // A new table's columns are numbered in order and kept where their types usually are: variable-length
        // values in column order, fixed-length ones one after another, each taking its length. A Yes/No column is
        // given a byte it never uses (Access gives it none, at offset 0): Access reads and writes rows by the
        // offsets the definition gives, so it reads these, and tables this library made before are laid out so.
        // Each column is told its place, as one read from a definition is: rows are written by it (RowLayout).
        for (short i = 0; i < Columns.Count; i++)
        {
            var column = Columns[i];
            column.Ordinal     = i;
            column.IsVariableLength = column.DataType.IsVariableLength();
            column.HasStoredPlace   = true;
            if (column.IsVariableLength)
            {
                varIndexes[column] = varIdx;
                column.VarLenTableIndex = varIdx++;
                column.FixedDataOffset  = -1;
            }
            else
            {
                fixedOffsets[column] = fixedOff;
                column.FixedDataOffset  = fixedOff;
                column.VarLenTableIndex = -1;
                fixedOff += (short)column.LengthInBytes;
            }
        }
        MaxColumnCount    = Columns.Count;
        MaxVarColumnCount = varIdx;

        // ── Column definitions (25 bytes each) ───────────────────────────────
        foreach (var col in Columns)
        {
            page[pos++] = (byte)col.DataType;
            ByteUtil.PutInt  (page, pos, MagicTableNumber); pos += 4;
            ByteUtil.PutShort(page, pos, (short)col.Ordinal); pos += 2;
            ByteUtil.PutShort(page, pos, col.DataType.IsVariableLength() ? varIndexes[col] : (short)0); pos += 2;
            ByteUtil.PutShort(page, pos, (short)col.Ordinal); pos += 2;

            if (col.DataType is DataType.Text or DataType.Memo)
            {
                // The order the column sorts in: Access keys a column with none in an order of its own.
                // Access 97's orders have no Jet4 form; such a column sorts in General - Legacy.
                (col.SortOrder.IsAccess97 ? SortOrder.GeneralLegacy : col.SortOrder).Write(page, pos, format);
                pos += format.SizeSortOrder;
            }
            else
            {
                page[pos++] = col.DataType == DataType.Decimal ? col.Precision : (byte)0x00;
                page[pos++] = col.DataType == DataType.Decimal ? col.Scale     : (byte)0x00;
                ByteUtil.PutShort(page, pos, 0); pos += 2;
            }

            byte flags = ColumnFlagUpdatable;
            if (!col.DataType.IsVariableLength()) flags |= ColumnFlagFixedLen;
            if (col.IsAutoNumber)
                flags |= (col.DataType == DataType.Guid ? ColumnFlagAutoNumberGuid : ColumnFlagAutoNumber);

            page[pos++] = flags;
            // Ext flags: bit 0 is "Unicode Compression", which Access sets on its new Text and Memo fields.
            page[pos++] = (byte)((col.DataType is DataType.Text or DataType.Memo) && col.IsCompressedUnicode ? 0x01 : 0x00);
            ByteUtil.PutInt  (page, pos, 0); pos += 4;
            ByteUtil.PutShort(page, pos, col.DataType.IsVariableLength() ? (short)0 : fixedOffsets[col]); pos += 2;
            ByteUtil.PutShort(page, pos, col.DataType.IsLongValue()      ? (short)0 : (short)col.LengthInBytes); pos += 2;
        }

        // ── Column names (length-prefixed UTF-16LE) ───────────────────────────
        for (int i = 0; i < Columns.Count; i++)
        {
            ByteUtil.PutShort(page, pos, (short)nameBytes[i].Length); pos += 2;
            Array.Copy(nameBytes[i], 0, page, pos, nameBytes[i].Length);
            pos += nameBytes[i].Length;
        }

        // ── Index column blocks, slots and names ──────────────────────────────
        // As Access lays them out (and Jackcess reads them):
        //   index column block: 4-byte magic + 10×(short colNum + byte flags)
        //                     + 4-byte umap ref + 4-byte root page
        //                     + SkipBeforeIndexFlags + 1-byte flags + SkipAfterIndexFlags
        //   slot block:        4-byte magic + 4-byte indexNumber + 4-byte indexDataNumber
        //                     + 1-byte relIndexType + 4-byte relIndexNumber + 4-byte relTablePage
        //                     + 1-byte cascadeUpdates + 1-byte cascadeDeletes + 1-byte indexType
        //                     + SkipAfterIndexSlot
        //   slot name:         length-prefixed UTF-16LE
        foreach (var index in blocks)
        {
            int blockStart = pos;
            if (format.SkipBeforeIndex >= 4) ByteUtil.PutInt(page, pos, MagicIndexNumber);
            int p = pos + format.SkipBeforeIndex;

            // 10 column slots: the index's columns, then COLUMN_UNUSED (-1).
            for (int c = 0; c < IndexDefinition.MaxColumns; c++)
            {
                if (c < index.Columns.Count)
                {
                    ByteUtil.PutShort(page, p, (short)index.Columns[c].Column.Ordinal); p += 2;
                    page[p++] = index.Columns[c].Flags;
                }
                else
                {
                    ByteUtil.PutShort(page, p, unchecked((short)0xFFFF)); p += 2;
                    page[p++] = 0x00;
                }
            }
            // The index's used-pages UsageMap ref (1-byte row + 3-byte page).
            page[p] = (byte)index.UsedPagesUmapRow;
            ByteUtil.Put3ByteInt(page, p + 1, index.UsedPagesUmapPage);
            p += 4;
            ByteUtil.PutInt(page, p, index.RootPageNumber); p += 4;
            p += format.SkipBeforeIndexFlags;
            page[p++] = index.Flags;
            // SkipAfterIndexFlags absorbed by the block-size advance below.
            pos = blockStart + format.SizeIndexColumnBlock;
        }

        foreach (var index in indexes)
        {
            int slotStart = pos;
            if (format.SkipBeforeIndexSlot >= 4) ByteUtil.PutInt(page, pos, MagicTableNumber);
            int p = pos + format.SkipBeforeIndexSlot;
            ByteUtil.PutInt(page, p, index.IndexNumber);      p += 4;
            ByteUtil.PutInt(page, p, index.IndexDataNumber);  p += 4;
            p += 1;                                                 // relIndexType
            ByteUtil.PutInt(page, p, InvalidIndexNumber);  p += 4;   // relIndexNumber
            p += 4;                                                 // relTablePage
            // cascadeUpdates + cascadeDeletes: 4 in each, as Access writes them in a slot that is no relationship's.
            page[p++] = TdefEditor.NoRelationshipCascade;
            page[p++] = TdefEditor.NoRelationshipCascade;
            page[p] = index.IndexType;
            pos = slotStart + format.SizeIndexInfoBlock;
        }

        foreach (byte[] indexName in indexNameBytes)
        {
            ByteUtil.PutShort(page, pos, (short)indexName.Length); pos += 2;
            Array.Copy(indexName, 0, page, pos, indexName.Length);
            pos += indexName.Length;
        }

        // ── LVAL column usage-map references ─────────────────────────────────
        // Real format (see LvalUmapRef): 10 bytes per entry, column-number
        // prefixed, 0xFFFF terminated (the trailer below doubles as terminator).
        foreach (var col in Columns.Where(c => c.DataType.IsLongValue())
                                   .OrderBy(c => c.Ordinal))
        {
            if (!LvalColumnUmapPages.TryGetValue(col.Name, out var lvalRef)) continue;
            ByteUtil.PutShort(page, pos, (short)col.Ordinal); pos += 2;
            page[pos++] = (byte)lvalRef.OwnedRow;
            ByteUtil.Put3ByteInt(page, pos, lvalRef.OwnedPage); pos += 3;
            page[pos++] = (byte)lvalRef.FreeRow;
            ByteUtil.Put3ByteInt(page, pos, lvalRef.FreePage); pos += 3;
        }

        // ── Trailer ───────────────────────────────────────────────────────────
        page[pos++] = 0xFF;
        page[pos]   = 0xFF;

        return page;
    }
}
