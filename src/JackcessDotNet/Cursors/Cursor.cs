using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Sequential row iterator over a single table.
/// Walks the table's owned-pages usage map and yields decoded rows in physical
/// (insertion-)order, transparently following LVAL chains for Memo/OLE columns.
///
/// State machine:
///   position = (pageIndex into ownedPages, rowIndex into that page).
///   MoveBeforeFirst → pageIndex = -1 (so the next MoveNext moves to the first row).
///   MoveAfterLast   → pageIndex = ownedPages.Count (so the next MovePrevious moves to the last row).
///
/// <code>
///   var cursor = table.CreateCursor();
///   while (cursor.MoveNext())
///       Use(cursor.Current!);
/// </code>
/// A cursor moves forwards and backwards. It holds no handle and needs no disposing; like the
/// <see cref="Database"/> it reads, it is for one thread at a time. It lists the table's pages when it is
/// created: rows on pages the table gains afterwards are not visited.
/// </summary>
public class Cursor : IEnumerable<Row>
{
    private   readonly PageFile        _file;
    private   readonly TableDefinition _definition;
    private   readonly Table           _table;
    private   readonly List<int>       _ownedPages;
    private   readonly RowDecoder      _decoder;

    // Members IndexCursor needs for index-driven random access.
    internal PageFile        File       => _file;
    internal TableDefinition Definition => _definition;
    internal RowDecoder      Decoder    => _decoder;

    private int  _pageIndex;
    private int  _rowIndex;
    private Row? _currentRow;

    internal Cursor(Table table, PageFile file, TableDefinition definition)
    {
        _table      = table;
        _file       = file;
        _definition = definition;

        var format = _file.Format;
        byte[] umapPage = _file.ReadPage(_definition.UmapPageNumber);
        _ownedPages = UsageMap.GetOwnedPages(umapPage, _definition.OwnedPagesRow, format, _file);

        var lvalReader = _definition.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null;
        _decoder = new RowDecoder(format, _definition.Columns, lvalReader);

        MoveBeforeFirst();
    }

    /// <summary>The table this cursor iterates.</summary>
    public Table Table => _table;

    /// <summary>
    /// Puts the cursor before the first row, where a new cursor is: the next <see cref="MoveNext"/> moves to the
    /// first row. Jackcess's <c>beforeFirst</c>.
    /// </summary>
    public void MoveBeforeFirst()
    {
        _pageIndex  = -1;
        _rowIndex   = -1;
        _currentRow = null;
    }

    /// <summary>
    /// Puts the cursor after the last row: the next <see cref="MovePrevious"/> moves to the last row. Jackcess's
    /// <c>afterLast</c>.
    /// </summary>
    public void MoveAfterLast()
    {
        _pageIndex  = _ownedPages.Count;
        _rowIndex   = 0;
        _currentRow = null;
    }

    /// <summary>
    /// The columns rows are read with, or null (as at first) for all of them. A reader that looks at a few columns of
    /// every row is spared decoding the rest, long values among them.
    /// </summary>
    internal IReadOnlyList<Column>? ColumnsToRead { get; set; }

    /// <summary>
    /// Whether rows are read with their values as their index keys are made of them
    /// (<see cref="RowDecoder.DecodeForKey"/>): a Date/Time as the double the row stores, which a
    /// <see cref="DateTime"/> does not always hold. For a reader that compares values with keys.
    /// </summary>
    internal bool ValuesAsStored { get; set; }

    /// <summary>The current row's pointer (<see cref="RowPointer"/>); meaningful only while there is a current row.</summary>
    internal int CurrentRowPointer => RowPointer.Pack(_ownedPages[_pageIndex], _rowIndex);

    /// <summary>
    /// The row the cursor is on, or null when it is on none: before the first row, after the last, or after a
    /// move that found no row.
    /// </summary>
    public Row? Current => _currentRow;

    /// <summary>
    /// Moves to the next row, passing deleted ones, and says whether there was one: <see cref="Current"/> is then
    /// that row. When there are no more rows the cursor is left after the last row and <see cref="Current"/> is
    /// null.
    /// </summary>
    public bool MoveNext() => NextRow() is not null;

    /// <summary>
    /// Moves to the previous row, passing deleted ones, and says whether there was one: <see cref="Current"/> is
    /// then that row. At the beginning the cursor is left before the first row and <see cref="Current"/> is null.
    /// </summary>
    public bool MovePrevious() => PreviousRow() is not null;

    /// <summary>Moves to the next live row and returns it, or null when there are no more rows.</summary>
    internal Row? NextRow()
    {
        // Re-anchor onto the next slot, skipping deleted/overflow rows transparently.
        int curPageIdx = _pageIndex;
        int curRowIdx  = _rowIndex + 1;

        if (curPageIdx < 0) { curPageIdx = 0; curRowIdx = 0; }

        while (curPageIdx < _ownedPages.Count)
        {
            byte[] page     = _file.ReadPage(_ownedPages[curPageIdx]);
            int    rowCount = ByteUtil.GetShort(page, _file.Format.OffsetDataNumRows);

            while (curRowIdx < rowCount)
            {
                Row? r = TryReadRow(page, curRowIdx);
                if (r is not null)
                {
                    _pageIndex  = curPageIdx;
                    _rowIndex   = curRowIdx;
                    _currentRow = r;
                    return r;
                }
                curRowIdx++;
            }
            curPageIdx++;
            curRowIdx = 0;
        }

        // Walked off the end.
        _pageIndex  = _ownedPages.Count;
        _rowIndex   = 0;
        _currentRow = null;
        return null;
    }

    /// <summary>Moves to the previous live row and returns it, or null at the beginning.</summary>
    internal Row? PreviousRow()
    {
        int curPageIdx = _pageIndex;
        int curRowIdx  = _rowIndex - 1;

        if (curPageIdx >= _ownedPages.Count)
        {
            curPageIdx = _ownedPages.Count - 1;
            if (curPageIdx >= 0)
            {
                byte[] last = _file.ReadPage(_ownedPages[curPageIdx]);
                curRowIdx = ByteUtil.GetShort(last, _file.Format.OffsetDataNumRows) - 1;
            }
        }

        while (curPageIdx >= 0)
        {
            byte[] page = _file.ReadPage(_ownedPages[curPageIdx]);
            while (curRowIdx >= 0)
            {
                Row? r = TryReadRow(page, curRowIdx);
                if (r is not null)
                {
                    _pageIndex  = curPageIdx;
                    _rowIndex   = curRowIdx;
                    _currentRow = r;
                    return r;
                }
                curRowIdx--;
            }
            curPageIdx--;
            if (curPageIdx >= 0)
            {
                byte[] prev = _file.ReadPage(_ownedPages[curPageIdx]);
                curRowIdx = ByteUtil.GetShort(prev, _file.Format.OffsetDataNumRows) - 1;
            }
        }

        MoveBeforeFirst();
        return null;
    }

    /// <summary>
    /// Reads the live row at <paramref name="pageNumber"/>/<paramref name="rowIndex"/>.
    /// Returns null if that slot is deleted or malformed; an overflow row is read where its pointer leads.
    /// Used by <see cref="IndexCursor"/> for index-driven random access.
    /// </summary>
    internal Row? ReadRowAt(int pageNumber, int rowIndex)
    {
        byte[] page = _file.ReadPage(pageNumber);
        return TryReadRow(page, rowIndex);
    }

    /// <summary>
    /// Decodes the row at <paramref name="rowIndex"/> on <paramref name="page"/>.
    /// Returns null if the slot is deleted or malformed; an overflow row is read where its pointer leads.
    /// </summary>
    private Row? TryReadRow(byte[] page, int rowIndex)
    {
        if (DataRows.Read(_file, page, rowIndex) is not { } rowBytes) return null;

        var row = new Row();
        foreach (var col in ColumnsToRead ?? _definition.Columns)
        {
            object? val = ValuesAsStored ? _decoder.DecodeForKey(rowBytes, col) : _decoder.Decode(rowBytes, col);
            if (val is not null) row[col.Name] = val;
        }
        return row;
    }

    // ── IEnumerable ───────────────────────────────────────────────────────────

    /// <summary>
    /// The table's rows from the first: the cursor is put before the first row, then moved through them all.
    /// Enumerating moves the cursor itself, so one cursor is enumerated by one loop at a time.
    /// </summary>
    public IEnumerator<Row> GetEnumerator()
    {
        MoveBeforeFirst();
        while (NextRow() is { } r) yield return r;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
