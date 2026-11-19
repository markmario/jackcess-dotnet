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
internal sealed class DataPageWriter
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
    public int InsertRow(TableDefinition tableDef, Row row) => InsertRow(tableDef, row, null, datesAsStored: false);

    /// <summary>
    /// As <see cref="InsertRow(TableDefinition, Row)"/>, for the row an update writes in place of another:
    /// <paramref name="keptVariableValues"/> are the old row's own bytes for the variable-length columns the update
    /// leaves alone, written back as they are (<see cref="RowEncoder.Prepare"/>);
    /// with <paramref name="datesAsStored"/>, a Date/Time value may be the double a row stores
    /// (<see cref="RowEncoder.DatesAsStored"/>).
    /// </summary>
    internal int InsertRow(TableDefinition tableDef, Row row, IReadOnlyDictionary<Column, byte[]>? keptVariableValues, bool datesAsStored = false)
    {
        if (tableDef is null) throw new ArgumentNullException(nameof(tableDef));
        if (row      is null) throw new ArgumentNullException(nameof(row));

        var format = _file.Format;

        // Everything that can refuse the row - a value its column cannot hold, a row too large - does so here,
        // before a page is written: a refused row leaves the file as it was.
        var encoder = new RowEncoder(format, tableDef, LvalWritersFor(tableDef)) { DatesAsStored = datesAsStored };
        return Place(tableDef, encoder, encoder.Prepare(row, keptVariableValues, null));
    }

    // Writes a prepared row's long values and then the row. Only the file can fail here.
    private int Place(TableDefinition tableDef, RowEncoder encoder, RowEncoder.PreparedRow prepared)
    {
        var    format  = _file.Format;
        byte[] rowData = encoder.Complete(prepared);
        int dataPage   = FindOrAllocateDataPage(tableDef, rowData.Length, format);
        int rowNum     = WriteRowOnPage(dataPage, rowData, tableDef.TdefPageNumber, format);
        return RowPointer.Pack(dataPage, rowNum);
    }

    // A writer for each Memo/OLE column that has usage maps of its own: where its longer values go.
    private Dictionary<string, LvalWriter>? LvalWritersFor(TableDefinition tableDef)
    {
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
        return lvalWriters;
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

    // ── A row at a known place ────────────────────────────────────────────────

    /// <summary>
    /// Rewrites the row at <paramref name="rowPtr"/> with <paramref name="newValues"/> laid over its own, and
    /// returns where the row now is: the old row is deleted and the merged one added, so the row moves.
    /// <paramref name="beforeChange"/> is given the row as it was, the row as it will be and the old row's pointer
    /// once the new row is known to be writable and before anything is changed: what it throws refuses the update
    /// with the table as it was. A Date/Time value the change does not touch is written back as the double the
    /// row stored, not as the <see cref="DateTime"/> it reads as, which does not hold every double Access writes:
    /// a date a relationship refers to must still be the date its parent has, and the row's index keys the keys
    /// its values have.
    /// </summary>
    /// <exception cref="InvalidOperationException">No row of the table is at <paramref name="rowPtr"/>.</exception>
    internal int RewriteRowAt(TableDefinition table, int rowPtr, Row newValues, Action<Row, Row, int>? beforeChange = null)
    {
        var (page, rowBytes) = ReadLiveRow(table, rowPtr);
        return Rewrite(table, DecoderFor(table), page, RowPointer.Page(rowPtr), RowPointer.Row(rowPtr), rowBytes, newValues, beforeChange, keepStoredDates: true);
    }

    /// <summary>
    /// Deletes the row at <paramref name="rowPtr"/> - its long values freed, its slot marked - and reports the
    /// values its index keys were made of. The caller takes one off the TDEF's row count.
    /// </summary>
    /// <exception cref="InvalidOperationException">No row of the table is at <paramref name="rowPtr"/>.</exception>
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
            object? keyed = col.DataType == DataType.DateTime ? decoder.DecodeForKey(rowBytes, col) : existing;
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
        var encoder = new RowEncoder(_file.Format, table, LvalWritersFor(table)) { DatesAsStored = keepStoredDates };

        // A long value the update does not change stays where it is: the new row carries the old row's reference
        // to it, as Access and Java Jackcess leave it, so it is neither freed nor written again. Only the long
        // values being replaced are freed, before the row is rewritten. One kept in the row though longer than a
        // row may keep (releases before ExportMdb.Interop 2.1.2 wrote those, and Access shows #Error for them,
        // qcatco/exportmdb.interop#29) is not kept as it is: written again, it goes to pages of its own.
        //
        // Every other column the update does not name keeps the row's own bytes too: an update does not change a
        // value it was not given. Read and written again, a Date/Time outside what DateTime holds came back null.
        var kept      = new Dictionary<Column, byte[]>();
        var keptFixed = new Dictionary<Column, byte[]>();
        foreach (var col in table.Columns)
        {
            if (col.DataType == DataType.Boolean || newValues.ContainsKey(col.Name)) continue;
            if (decoder.RawFixedValue(rowBytes, col) is { } bytes)
                keptFixed[col] = bytes;
            else if (decoder.RawVariableValue(rowBytes, col) is { } stored
                     && !(col.DataType.IsLongValue() && (stored.Length < 4 || IsOverlongInRowValue(stored))))
                kept[col] = stored;
        }

        // The new row whole, but for the long values still to go to their pages: a value its column cannot hold,
        // or one not of its type, or a row too large for the format is refused here, with nothing changed. Refused
        // only as the new row was written, the row it replaced was already gone (qcatco/exportmdb.interop#56).
        var prepared = encoder.Prepare(merged, kept, keptFixed);
        beforeChange?.Invoke(old, merged, RowPointer.Pack(pageNumber, rowIndex));

        FreeRowLvalChains(table, rowBytes, decoder, kept);

        // Mark the original slot as deleted, and with it wherever Access had moved the row's values to.
        var moved = new List<(int Page, int Row)>();
        DataRows.Read(_file, page, rowIndex, moved);
        int slotOff = _file.Format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        ByteUtil.PutUShort(page, slotOff, (ushort)(ByteUtil.GetUShort(page, slotOff) | JetFormat.DeletedRowFlags));
        _file.WritePage(pageNumber, page);
        DataRows.MarkDeleted(_file, moved);

        // Insert the merged row; the caller moves the row's index entries from the old pointer to the new.
        return Place(table, encoder, prepared);
    }

    // A long value's reference whose value is in the row (THIS_PAGE) and longer than Access keeps one there.
    private static bool IsOverlongInRowValue(byte[] reference)
    {
        int lengthWithFlags = ByteUtil.GetInt(reference, 0);
        return (lengthWithFlags & unchecked((int)0xC0000000)) == LvalWriter.LvalTypeThisPage
            && (lengthWithFlags & LvalWriter.LvalLengthMask) > RowEncoder.InlineLvalThreshold;
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
            if ((!col.DataType.IsLongValue() || table.Indexes.Any(ix => ix.Columns.Any(c => c.Column.Ordinal == col.Ordinal)))
                && decoder.DecodeForKey(rowBytes, col) is { } stored)
                values[col.Name] = stored;

        // A table with a calculated column is not written to, and a delete is a write (qcatco/exportmdb.interop#42).
        if (table.Columns.FirstOrDefault(c => c.IsCalculated) is { } calculated)
            throw new NotSupportedException(
                $"No row of this table is deleted: its column {calculated.Name} is calculated, and this library " +
                "works no expression out. Nothing was changed.");

        // A row whose index entries could not be taken out stays: nothing has changed yet.
        TableIndexes.EnsureEntriesCanBeRemoved(table, values);

        // Free any LVAL chains referenced by this row.
        FreeRowLvalChains(table, rowBytes, decoder);

        // Mark the slot as deleted, and with it wherever Access had moved the row's values to.
        var moved = new List<(int Page, int Row)>();
        DataRows.Read(_file, page, rowIndex, moved);
        int slotOff = _file.Format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        ByteUtil.PutUShort(page, slotOff, (ushort)(ByteUtil.GetUShort(page, slotOff) | JetFormat.DeletedRowFlags));
        _file.WritePage(pageNumber, page);
        DataRows.MarkDeleted(_file, moved);
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

    /// <summary>
    /// The values of <paramref name="columns"/> in the row at <paramref name="rowPtr"/> as a caller reads them
    /// (<see cref="RowDecoder.Decode(byte[], Column)"/>), an entry for each, or null when no row of the table is
    /// there: what an update compares the values it is given with, to leave alone those that are the row's own.
    /// </summary>
    internal Row? ReadRowAsReadAt(TableDefinition table, int rowPtr, IEnumerable<Column> columns)
    {
        if (TryReadLiveRow(table, rowPtr) is not { } found) return null;
        var decoder = DecoderFor(table);
        var row = new Row();
        foreach (var column in columns)
            row[column.Name] = decoder.Decode(found.RowBytes, column);
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

        // The page is the one the row's slot is on, whose slot a delete or an update marks; the bytes are the
        // row's own, from wherever an overflow pointer leads.
        if (page[0] != JetFormat.PageTypeData || ByteUtil.GetInt(page, JetFormat.OffsetDataTdefPage) != table.TdefPageNumber)
            return null;
        return DataRows.Read(_file, page, rowIndex) is { } rowBytes ? (page, rowBytes) : null;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    // Frees every OTHER_PAGE LVAL chain referenced by the given row bytes. The room each page gains is noted for the
    // column the value was in (its owned-pages map), whose later values may use it.
    private void FreeRowLvalChains(TableDefinition table, byte[] rowBytes, RowDecoder decoder,
                                   IReadOnlyDictionary<Column, byte[]>? kept = null)
    {
        var lvalFree = new LvalFree(_file);
        foreach (var (column, lvalPage, lvalRow, chained) in decoder.GetOtherPageLvRefs(rowBytes))
        {
            if (kept is not null && kept.ContainsKey(column)) continue;
            LvalUmapRef? owner = table.LvalColumnUmapPages.TryGetValue(column.Name, out var umap) ? umap : null;
            lvalFree.FreeChain(lvalPage, lvalRow, chained, owner);
        }
    }
}
