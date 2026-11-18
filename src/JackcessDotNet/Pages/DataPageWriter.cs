using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Writes rows into Jet data pages (page type 0x01).
///
/// Data-page slot-array layout:
///   Bytes  0..13  – header (page type, free-space, tdef-page#, row-count)
///   Bytes 14..    – row slot table, 2 bytes per slot, grows forward
///   … free space …
///   …             – row data, packed from end of page, grows backward
///
/// Slot N value = absolute byte-offset of row N's data inside the page.
/// Row 0 is the first row inserted and is placed at the highest address.
/// Row N (N>0) is placed immediately before row N-1.
///
/// Free-space formula:
///   freeSpace = row1Start − ( OffsetDataRowTable + rowCount × SizeRowEntry )
///             = minSlotValue − headerUsed
/// Equivalently:
///   cursor (next write position) = OffsetDataRowTable + rowCount×2 + freeSpace
///   newRowStart = cursor − rowSize
/// </summary>
public sealed class DataPageWriter
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;

    public DataPageWriter(PageFile file, PageAllocator allocator)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="row"/> and appends it to the best available data page
    /// for <paramref name="tableDef"/>.  Allocates a new data page if needed and
    /// registers it in the table's owned-pages usage-map.
    /// </summary>
    /// <returns>The row's pointer (<see cref="RowPointer"/>: its page and its row on it).</returns>
    public int InsertRow(TableDefinition tableDef, Row row) => InsertRow(tableDef, row, datesAsStored: false);

    /// <summary>
    /// As <see cref="InsertRow(TableDefinition, Row)"/>; with <paramref name="datesAsStored"/>, a Date/Time value
    /// may be the double a row stores (<see cref="RowEncoder.DatesAsStored"/>).
    /// </summary>
    private int InsertRow(TableDefinition tableDef, Row row, bool datesAsStored)
    {
        if (tableDef is null) throw new ArgumentNullException(nameof(tableDef));
        if (row      is null) throw new ArgumentNullException(nameof(row));

        var format = _file.Format;

        // Build per-column LvalWriters for any Memo/OLE columns that have a umap page.
        Dictionary<string, LvalWriter>? lvalWriters = null;
        foreach (var col in tableDef.Columns)
        {
            if (col.DataType.IsLongValue() &&
                tableDef.LvalColumnUmapPages.TryGetValue(col.Name, out var umapRef))
            {
                lvalWriters ??= new Dictionary<string, LvalWriter>(StringComparer.OrdinalIgnoreCase);
                lvalWriters[col.Name] = new LvalWriter(_file, _allocator, umapRef);
            }
        }

        var    encoder = new RowEncoder(format, tableDef.Columns, lvalWriters) { DatesAsStored = datesAsStored };
        byte[] rowData = encoder.Encode(row);

        int dataPage   = FindOrAllocateDataPage(tableDef, rowData.Length, format);
        int rowNum     = WriteRowOnPage(dataPage, rowData, tableDef.TdefPageNumber, format);

        return RowPointer.Pack(dataPage, rowNum);
    }

    /// <summary>
    /// Updates the total-row-count field inside the table's TDEF page by adding
    /// <paramref name="delta"/> to the existing value.
    /// </summary>
    public void IncrementTdefRowCount(int tdefPageNumber, int delta = 1)
    {
        var format    = _file.Format;
        var tdefPage  = _file.ReadPage(tdefPageNumber);
        int current   = ByteUtil.GetInt(tdefPage, format.TdefOffsetNumRows);
        ByteUtil.PutInt(tdefPage, format.TdefOffsetNumRows, current + delta);
        _file.WritePage(tdefPageNumber, tdefPage);
    }

    /// <summary>
    /// Records an inserted row in the table's definition: one more row, and - when the insert used or passed the
    /// table's Long auto-number counter - the counter's new value. Both go in one write, after the row is on its
    /// page, so the counter never runs ahead of the rows nor falls behind them.
    /// </summary>
    internal void RecordInsert(int tdefPageNumber, int? lastAutoNumber)
    {
        var format   = _file.Format;
        var tdefPage = _file.ReadPage(tdefPageNumber);
        ByteUtil.PutInt(tdefPage, format.TdefOffsetNumRows, ByteUtil.GetInt(tdefPage, format.TdefOffsetNumRows) + 1);
        if (lastAutoNumber is { } last)
            ByteUtil.PutInt(tdefPage, format.TdefOffsetLastAutoNumber, last);
        _file.WritePage(tdefPageNumber, tdefPage);
    }

    /// <summary>
    /// Moves the table's Long auto-number counter up to <paramref name="value"/> when it is behind it; never down.
    /// </summary>
    internal void RaiseLastAutoNumber(int tdefPageNumber, int value)
    {
        var format   = _file.Format;
        var tdefPage = _file.ReadPage(tdefPageNumber);
        if (value <= ByteUtil.GetInt(tdefPage, format.TdefOffsetLastAutoNumber)) return;
        ByteUtil.PutInt(tdefPage, format.TdefOffsetLastAutoNumber, value);
        _file.WritePage(tdefPageNumber, tdefPage);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// The most rows a page may hold: Jet addresses a row on its page with one byte (Jackcess's
    /// MAX_NUM_ROWS_ON_DATA_PAGE).
    /// </summary>
    internal const int MaxRowsPerPage = 255;

    private int FindOrAllocateDataPage(TableDefinition tableDef, int rowDataSize, JetFormat format)
    {
        int needed = rowDataSize + JetFormat.SizeRowEntry;   // data bytes + one slot

        // The table's last page, when it is one of its data pages with room - as Access and Java Jackcess add rows.
        // Looking through every owned page for room read the whole table on every insert, so each insert was
        // slower than the last, and it put rows ahead of rows inserted before them.
        int last = UsageMap.GetLastPage(_file, tableDef.UmapPageNumber, tableDef.OwnedPagesRow);
        if (last >= 0)
        {
            byte[] dp = _file.ReadPage(last);
            if (dp[0] == JetFormat.PageTypeData
                && ByteUtil.GetInt(dp, JetFormat.OffsetDataTdefPage) == tableDef.TdefPageNumber
                && ByteUtil.GetShort(dp, JetFormat.OffsetDataFreeSpace) >= needed
                && ByteUtil.GetShort(dp, format.OffsetDataNumRows) < MaxRowsPerPage)
                return last;
        }

        // Allocate a fresh data page and register it in the usage-map.
        int newPage = _allocator.AllocateDataPage(tableDef.TdefPageNumber);
        UsageMap.AddPage(_file, _allocator, tableDef.UmapPageNumber, tableDef.OwnedPagesRow, newPage);

        return newPage;
    }

    private int WriteRowOnPage(int pageNumber, byte[] rowData, int tdefPageNumber, JetFormat format)
    {
        byte[] page      = _file.ReadPage(pageNumber);
        int    rowCount  = ByteUtil.GetShort(page, format.OffsetDataNumRows);
        int    freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
        int    needed    = rowData.Length + JetFormat.SizeRowEntry;

        if (freeSpace < needed)
            throw new InvalidOperationException(
                $"Data page {pageNumber} has only {freeSpace} free bytes; row needs {needed}.");
        if (rowCount >= MaxRowsPerPage)
            throw new InvalidOperationException(
                $"Data page {pageNumber} already holds {rowCount} rows, the most a page can.");

        // cursor = first byte of the free gap (grows up), which also equals the
        // left-edge of the already-written data area when rearranged:
        //   cursor = OffsetDataRowTable + rowCount×2 + freeSpace
        int cursor      = format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry + freeSpace;
        int rowStart    = cursor - rowData.Length;

        // Copy row data into the page
        Array.Copy(rowData, 0, page, rowStart, rowData.Length);

        // Write the slot entry for this row
        int slotOffset  = format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry;
        ByteUtil.PutShort(page, slotOffset, (short)rowStart);

        // Update header
        ByteUtil.PutShort(page, format.OffsetDataNumRows,   (short)(rowCount  + 1));
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)(freeSpace - needed));

        _file.WritePage(pageNumber, page);
        return rowCount;   // 0-based row index on this page
    }

    // ── UpdateRowByPrimaryKey ─────────────────────────────────────────────────

    /// <summary>
    /// Finds the row whose primary-key column equals <paramref name="primaryKeyValue"/>
    /// (linear scan of all owned data pages), merges <paramref name="newValues"/> into it,
    /// marks the old row as deleted, and re-inserts the merged row.
    /// Returns the new row's <see cref="RowPointer"/>.
    /// </summary>
    public int UpdateRowByPrimaryKey(TableDefinition table, object primaryKeyValue, Row newValues)
        => UpdateRowByPrimaryKeyMoving(table, primaryKeyValue, newValues).NewRowPtr;

    /// <summary>
    /// As <see cref="UpdateRowByPrimaryKey"/>, with the old row's pointer too: the row moves.
    /// <paramref name="beforeChange"/> is given the row as it was, the row as it will be and the old row's pointer
    /// once the new row is known to be writable and before anything is changed: what it throws refuses the update
    /// with the table as it was.
    /// </summary>
    internal (int OldRowPtr, int NewRowPtr) UpdateRowByPrimaryKeyMoving(
        TableDefinition table, object primaryKeyValue, Row newValues, Action<Row, Row, int>? beforeChange = null)
    {
        if (table    is null) throw new ArgumentNullException(nameof(table));
        if (newValues is null) throw new ArgumentNullException(nameof(newValues));

        if (table.PrimaryKeyColumnName is null)
            throw new InvalidOperationException(
                "PrimaryKeyColumnName is not set on this TableDefinition. " +
                "Specify a primary key column when calling Database.CreateTable.");

        var format  = _file.Format;
        var columns = table.Columns;
        var lvalReader = table.LvalColumnUmapPages.Count > 0
            ? new LvalReader(_file)
            : null;
        var decoder = new RowDecoder(format, columns, lvalReader);

        // Locate the primary-key column descriptor.
        Column? pkCol = null;
        foreach (var col in columns)
        {
            if (col.Name.Equals(table.PrimaryKeyColumnName, StringComparison.OrdinalIgnoreCase))
            {
                pkCol = col;
                break;
            }
        }
        if (pkCol is null)
            throw new InvalidOperationException(
                $"Primary key column '{table.PrimaryKeyColumnName}' not found in table '{table.Name}'.");

        byte[] umapPage  = _file.ReadPage(table.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, table.OwnedPagesRow, format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                int    slotOff = format.OffsetDataRowTable + r * JetFormat.SizeRowEntry;
                ushort slotVal = ByteUtil.GetUShort(dp, slotOff);

                if ((slotVal & 0x8000) != 0) continue;   // deleted
                if ((slotVal & 0x4000) != 0) continue;   // overflow pointer

                int rowStart = slotVal & JetFormat.RowOffsetMask;
                int rowEnd   = (r == 0)
                    ? format.PageSize
                    : (ByteUtil.GetUShort(dp, format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                object? pkVal = decoder.Decode(rowBytes, pkCol);
                if (pkVal is null || !PrimaryKeysEqual(pkVal, primaryKeyValue)) continue;

                return (RowPointer.Pack(pageNum, r), Rewrite(table, decoder, dp, pageNum, r, rowBytes, newValues, beforeChange, keepStoredDates: false));
            }
        }

        throw new InvalidOperationException(
            $"No row with primary key '{primaryKeyValue}' found in table '{table.Name}'.");
    }

    // ── DeleteRow ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the first row where <paramref name="columnName"/> equals <paramref name="value"/>
    /// (linear scan), frees its LVAL chains, and marks the slot as deleted.
    /// The caller is responsible for decrementing the TDEF row count.
    /// </summary>
    public void DeleteRow(TableDefinition table, string columnName, object value)
        => DeleteFirstMatch(table, columnName, value);

    /// <summary>
    /// As <see cref="DeleteRow"/>, reporting the deleted row: its pointer and the values its index keys were
    /// made of, for its entries to leave the table's indexes. Null when no row matched.
    /// </summary>
    internal (int RowPtr, Row Values)? DeleteFirstMatch(TableDefinition table, string columnName, object value)
    {
        if (table is null)      throw new ArgumentNullException(nameof(table));
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));

        Column? targetCol = null;
        foreach (var col in table.Columns)
        {
            if (col.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                targetCol = col;
                break;
            }
        }
        if (targetCol is null)
            throw new InvalidOperationException(
                $"Column '{columnName}' not found in table '{table.Name}'.");

        var format     = _file.Format;
        var lvalReader = table.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null;
        var decoder    = new RowDecoder(format, table.Columns, lvalReader);

        byte[] umapPage  = _file.ReadPage(table.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, table.OwnedPagesRow, format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                int    slotOff = format.OffsetDataRowTable + r * JetFormat.SizeRowEntry;
                ushort slotVal = ByteUtil.GetUShort(dp, slotOff);

                if ((slotVal & 0x8000) != 0) continue;   // already deleted
                if ((slotVal & 0x4000) != 0) continue;   // overflow pointer

                int rowStart = slotVal & JetFormat.RowOffsetMask;
                int rowEnd   = (r == 0)
                    ? format.PageSize
                    : (ByteUtil.GetUShort(dp,
                           format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                object? colVal = decoder.Decode(rowBytes, targetCol);
                if (!PrimaryKeysEqual(colVal, value)) continue;

                return (RowPointer.Pack(pageNum, r), Delete(table, decoder, dp, pageNum, r, rowBytes));
            }
        }

        throw new InvalidOperationException(
            $"No row with {columnName} = '{value}' found in table '{table.Name}'.");
    }

    // ── A row at a known place ────────────────────────────────────────────────

    /// <summary>
    /// Rewrites the row at <paramref name="rowPtr"/> with <paramref name="newValues"/> laid over its own, exactly as
    /// <see cref="UpdateRowByPrimaryKeyMoving"/> rewrites the row it finds - <paramref name="beforeChange"/>
    /// included - and returns where the row now is. One thing differs: a Date/Time value the change does not
    /// touch is written back as the double the row stored, not as the <see cref="DateTime"/> it reads as, which
    /// does not hold every double Access writes. A row rewritten here is one its caller did not name - a cascade
    /// reached it - and a date of it that a relationship refers to must still be the date its parent has.
    /// </summary>
    internal int RewriteRowAt(TableDefinition table, int rowPtr, Row newValues, Action<Row, Row, int>? beforeChange = null)
    {
        var (page, rowBytes) = ReadLiveRow(table, rowPtr);
        return Rewrite(table, DecoderFor(table), page, RowPointer.Page(rowPtr), RowPointer.Row(rowPtr), rowBytes, newValues, beforeChange, keepStoredDates: true);
    }

    /// <summary>
    /// Deletes the row at <paramref name="rowPtr"/>, exactly as <see cref="DeleteFirstMatch"/> deletes the row it
    /// finds, and reports the values its index keys were made of. The caller takes one off the TDEF's row count.
    /// </summary>
    internal Row DeleteRowAt(TableDefinition table, int rowPtr)
    {
        var (page, rowBytes) = ReadLiveRow(table, rowPtr);
        return Delete(table, DecoderFor(table), page, RowPointer.Page(rowPtr), RowPointer.Row(rowPtr), rowBytes);
    }

    /// <summary>
    /// The one way a row is rewritten, however it was found: <paramref name="newValues"/> laid over the row on
    /// <paramref name="page"/>, the old row deleted and the merged one added, so the row moves. Returns the new
    /// row's pointer.
    /// </summary>
    private int Rewrite(TableDefinition table, RowDecoder decoder, byte[] page, int pageNumber, int rowIndex,
                        byte[] rowBytes, Row newValues, Action<Row, Row, int>? beforeChange, bool keepStoredDates)
    {
        // The row as it was, twice: as a caller reads it, for the row that replaces it, and as its
        // index keys were made of it (a date as the double stored), for its entries to be found.
        var old    = new Row();
        var merged = new Row();
        foreach (var col in table.Columns)
        {
            object? existing = decoder.Decode(rowBytes, col);
            if (existing is not null)
                merged[col.Name] = existing;
            object? keyed = col.DataType == DataType.ShortDateTime ? decoder.DecodeForKey(rowBytes, col) : existing;
            if (keyed is not null)
                old[col.Name] = keyed;
            if (keepStoredDates && keyed is double)
                merged[col.Name] = keyed;
        }
        foreach (var kvp in newValues)
            merged[kvp.Key] = kvp.Value;

        // A merged row the new one could not be written as - text longer than its column, a duplicate in
        // a unique index - must not cost the row it replaces, so it is checked before anything is freed
        // or deleted.
        new RowEncoder(_file.Format, table.Columns).Validate(merged);
        beforeChange?.Invoke(old, merged, RowPointer.Pack(pageNumber, rowIndex));

        // Free any LVAL chains referenced by the old row before overwriting.
        FreeRowLvalChains(table, rowBytes, decoder);

        // Mark the original slot as deleted (bit 15).
        int slotOff = _file.Format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        ByteUtil.PutUShort(page, slotOff, (ushort)(ByteUtil.GetUShort(page, slotOff) | 0x8000u));
        _file.WritePage(pageNumber, page);

        // Insert the merged row; the caller moves the row's index entries from the old pointer to the new.
        return InsertRow(table, merged, datesAsStored: keepStoredDates);
    }

    /// <summary>
    /// The one way a row is deleted, however it was found. Returns the values its index keys were made of.
    /// </summary>
    private Row Delete(TableDefinition table, RowDecoder decoder, byte[] page, int pageNumber, int rowIndex, byte[] rowBytes)
    {
        // What its index keys were made of: every column kept in the row itself, and a Memo that is
        // indexed (read before its pages are freed).
        var values = new Row();
        foreach (var col in table.Columns)
            if ((!col.DataType.IsLongValue() || table.Indexes.Any(ix => ix.Columns.Any(c => c.Column.ColumnNumber == col.ColumnNumber)))
                && decoder.DecodeForKey(rowBytes, col) is { } stored)
                values[col.Name] = stored;

        // A row whose index entries could not be taken out stays: nothing has changed yet.
        TableIndexes.EnsureEntriesCanBeRemoved(table, values);

        // Free any LVAL chains referenced by this row.
        FreeRowLvalChains(table, rowBytes, decoder);

        // Mark the slot as deleted.
        int slotOff = _file.Format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        ByteUtil.PutUShort(page, slotOff, (ushort)(ByteUtil.GetUShort(page, slotOff) | 0x8000u));
        _file.WritePage(pageNumber, page);
        return values;
    }

    /// <summary>
    /// The values of <paramref name="columns"/> in the row at <paramref name="rowPtr"/>, as its index keys are made
    /// of them (<see cref="RowDecoder.DecodeForKey"/>: a date as the double stored), or null when no row of the
    /// table is there: what a pointer from an index is checked against. One page is read, and the long-value pages
    /// of the columns asked for.
    /// </summary>
    internal Row? ReadRowAt(TableDefinition table, int rowPtr, IReadOnlyList<Column> columns)
    {
        if (TryReadLiveRow(table, rowPtr) is not { } found) return null;
        var decoder = DecoderFor(table);
        var row = new Row();
        foreach (var column in columns)
            if (decoder.DecodeForKey(found.RowBytes, column) is { } value)
                row[column.Name] = value;
        return row;
    }

    private RowDecoder DecoderFor(TableDefinition table)
        => new(_file.Format, table.Columns, table.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null);

    // The page holding the row at rowPtr and the row's bytes. The row has to be one of this table's and still
    // there: a pointer kept across a change to the row is no longer one.
    private (byte[] Page, byte[] RowBytes) ReadLiveRow(TableDefinition table, int rowPtr)
        => TryReadLiveRow(table, rowPtr) ?? throw new InvalidOperationException(
            $"No row of table '{table.Name}' is at page {RowPointer.Page(rowPtr)}, row {RowPointer.Row(rowPtr)}.");

    private (byte[] Page, byte[] RowBytes)? TryReadLiveRow(TableDefinition table, int rowPtr)
    {
        var format   = _file.Format;
        int rowIndex = RowPointer.Row(rowPtr);
        int pageNum  = RowPointer.Page(rowPtr);
        if (pageNum <= 0 || pageNum >= _file.PageCount) return null;
        byte[] page  = _file.ReadPage(pageNum);
        int slotOff  = format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;

        bool live = page[0] == JetFormat.PageTypeData
            && ByteUtil.GetInt(page, JetFormat.OffsetDataTdefPage) == table.TdefPageNumber
            && rowIndex < ByteUtil.GetShort(page, format.OffsetDataNumRows)
            && (ByteUtil.GetUShort(page, slotOff) & 0xC000) == 0;
        if (!live) return null;

        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = rowIndex == 0
            ? format.PageSize
            : ByteUtil.GetUShort(page, slotOff - JetFormat.SizeRowEntry) & JetFormat.RowOffsetMask;
        return (page, page.Slice(rowStart, rowEnd));
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    // Frees every OTHER_PAGE LVAL chain referenced by the given row bytes. The room each page gains is noted for the
    // column the value was in (its owned-pages map), whose later values may use it.
    private void FreeRowLvalChains(TableDefinition table, byte[] rowBytes, RowDecoder decoder)
    {
        var lvalFree = new LvalFree(_file);
        foreach (var (column, lvalPage, lvalRow, chained) in decoder.GetOtherPageLvRefs(rowBytes))
        {
            LvalUmapRef? owner = table.LvalColumnUmapPages.TryGetValue(column.Name, out var umap) ? umap : null;
            lvalFree.FreeChain(lvalPage, lvalRow, chained, owner);
        }
    }

    /// <summary>
    /// How a value a caller names a row by is matched to a stored one: equal, or equal as numbers (a Long column's 5
    /// for a caller's 5L). Text is matched exactly.
    /// </summary>
    internal static bool PrimaryKeysEqual(object? stored, object? requested)
    {
        if (Equals(stored, requested)) return true;
        if (stored is null || requested is null) return false;
        try { return Convert.ToDecimal(stored) == Convert.ToDecimal(requested); }
        catch { return false; }
    }
}
