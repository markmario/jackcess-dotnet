using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// A cursor over the rows of one table, in the order they are stored, which for a table only added to is the
/// order they were inserted in: made by <see cref="Table.CreateCursor"/>. A new cursor is before the first row.
/// Deleted rows are passed, and Memo and OLE values are read whole.
/// <code>
///   var cursor = table.CreateCursor();
///   while (cursor.MoveNext())
///       Use(cursor.Current!);
/// </code>
/// A cursor moves forwards and backwards, finds rows by the values they have (<see cref="FindRow(string, object?)"/>,
/// <see cref="FindNextRow(string, object?)"/>), and updates or deletes the row it is on
/// (<see cref="UpdateCurrentRow"/>, <see cref="DeleteCurrentRow"/>), keeping every index and relationship as
/// <see cref="Table.Update"/> and <see cref="Table.Delete"/> do. It holds no handle and needs no disposing; like
/// the <see cref="Database"/> it reads, it is for one thread at a time.
/// <para>
/// It lists the table's pages the first time it moves or is asked where it is, and again each time it is put before
/// the first row or after the last, and gives the rows they hold then: a row inserted in between is not visited,
/// whichever page it goes to - one the table gains, or one where a delete or an update left room - and a row deleted
/// in between is passed.
/// </para>
/// <para>
/// Over a database opened only to be read (<see cref="DatabaseOpenOptions.ReadOnly"/>), which another program may be
/// writing to, it reads the pages a move needs at that move, keeping none from one move to the next. It is not told
/// of the rows that program inserts, as it is of its own database's: one inserted on a page it listed, where it has
/// still to come, is visited. <see cref="DatabaseOpenOptions.ReadOnly"/> says what a reading beside a writer can and
/// cannot give.
/// </para>
/// </summary>
public class Cursor : IEnumerable<Row>
{
    private readonly PageFile        _file;
    private readonly TableDefinition _definition;
    private readonly Table           _table;
    private readonly RowDecoder      _decoder;
    // The table's pages, listed when the cursor first needs them - a cursor over an index never does - and what it
    // gives of them: the rows they held then (TableScan).
    private List<int>?               _pages;
    private TableScan?               _scan;
    private List<int>                _ownedPages => _pages ??= OwnedPages();

    internal PageFile        File       => _file;
    internal TableDefinition Definition => _definition;

    // Where the cursor is among the table's pages and their rows: before the first page (-1), after the last
    // (the number of pages), or at a row's slot - which may hold no row any longer.
    private int  _pageIndex;
    private int  _rowIndex;
    private Row? _currentRow;

    // The page last read, kept while nothing has been written to the file - and, when another program may write to
    // it, for one move only (BeginMoving).
    private byte[]? _page;
    private int     _pageNumber = -1;
    private long    _pageStamp;

    internal Cursor(Table table, PageFile file, TableDefinition definition)
    {
        _table      = table;
        _file       = file;
        _definition = definition;

        var lvalReader = _definition.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null;
        _decoder = new RowDecoder(_file.Format, _definition.Columns, lvalReader);

        _pageIndex = -1;
        _rowIndex  = -1;
    }

    private List<int> OwnedPages()
    {
        var pages = UsageMap.GetOwnedPages(_file.ReadPage(_definition.UmapPageNumber), _definition.OwnedPagesRow, _file.Format, _file);
        (_scan ??= _file.TableScans.Begin(_definition.TdefPageNumber)).Restart(pages);
        return pages;
    }

    /// <summary>The table whose rows the cursor reads.</summary>
    public Table Table => _table;

    // ── Moving ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the cursor before the first row, where a new cursor is: the next <see cref="MoveNext"/> moves to the
    /// first row. Jackcess's <c>beforeFirst</c>.
    /// </summary>
    public void MoveBeforeFirst()
    {
        _currentRow = null;
        ToStart();
    }

    /// <summary>
    /// Puts the cursor after the last row: the next <see cref="MovePrevious"/> moves to the last row. Jackcess's
    /// <c>afterLast</c>.
    /// </summary>
    public void MoveAfterLast()
    {
        _currentRow = null;
        ToEnd();
    }

    /// <summary>Whether the cursor is before the first row: where a new cursor is, and where moving back past the first row leaves it.</summary>
    public bool IsBeforeFirst => AtStart;

    /// <summary>Whether the cursor is after the last row: where moving on past the last row leaves it.</summary>
    public bool IsAfterLast => AtEnd;

    /// <summary>
    /// The row the cursor is on, or null when it is on none: before the first row, after the last, after a move
    /// that found no row, or once the row it was on has been deleted. The row has an entry for every column the
    /// cursor reads (<see cref="ColumnNames"/>), in the table's column order: <c>null</c> for a column with no
    /// value.
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
    internal Row? NextRow() => Move(forward: true);

    /// <summary>Moves to the previous live row and returns it, or null at the beginning.</summary>
    internal Row? PreviousRow() => Move(forward: false);

    private Row? Move(bool forward)
    {
        // A move that cannot read the row it comes to leaves the cursor as it was.
        object place = BeginMoving();
        try
        {
            Row? row = null;
            while (Step(forward) is { } pointer)
            {
                if (Passes(pointer, forward)) continue;
                if ((row = ReadRow(pointer)) is not null) break;
            }
            DoneMoving(restoreTo: null);
            return _currentRow = row;
        }
        catch
        {
            DoneMoving(restoreTo: place);
            throw;
        }
    }

    // How many moves and searches are under way: a search moves the cursor as it looks.
    private int _moving;

    /// <summary>
    /// Where the cursor is (<see cref="Place"/>), as a move or a search begins: what to put it back to when it
    /// fails or finds nothing (<see cref="DoneMoving"/>).
    /// </summary>
    private protected object BeginMoving()
    {
        // Another program may write to a file opened only to be read, between two moves: what the cursor kept of its
        // pages at the last is read again at this one (#62). Within a move, it is kept.
        if (_moving == 0 && _file.SharedWithWriters) ForgetKeptPages();
        _moving++;
        return Place();
    }

    /// <summary>
    /// Lets go of the pages the cursor keeps from one move to the next - the data page it is on, and an index cursor's
    /// leaf - so that the next move reads them from the file again.
    /// </summary>
    private protected virtual void ForgetKeptPages() => _page = null;

    /// <summary>
    /// Ends what <see cref="BeginMoving"/> began: the cursor is put back to <paramref name="restoreTo"/>, or, when
    /// that is null, kept where it is - and once the outermost move or search is kept, what it came to is settled
    /// (<see cref="Settle"/>).
    /// </summary>
    private protected void DoneMoving(object? restoreTo)
    {
        if (restoreTo is not null) Restore(restoreTo);
        if (--_moving == 0 && restoreTo is null) Settle();
    }

    /// <summary>
    /// After a move or a search the cursor is kept at, outside any other: what a kind of cursor noted while it ran,
    /// to be taken back if it failed, is made part of what the cursor knows.
    /// </summary>
    private protected virtual void Settle() { }

    /// <summary>
    /// Whether the cursor goes past the row at <paramref name="pointer"/> without giving it: a row it has come to
    /// already going this way, which an update has since moved ahead of it in its order. No row moves in a table's
    /// own order.
    /// </summary>
    private protected virtual bool Passes(int pointer, bool forward) => false;

    // ── What a kind of cursor says of its order ──────────────────────────────

    /// <summary>Puts the cursor before its first row; the table's pages are listed again.</summary>
    private protected virtual void ToStart()
    {
        _pages     = null;
        _pageIndex = -1;
        _rowIndex  = -1;
    }

    /// <summary>Puts the cursor after its last row; the table's pages are listed again.</summary>
    private protected virtual void ToEnd()
    {
        _pages     = null;
        _pageIndex = _ownedPages.Count;
        _rowIndex  = 0;
    }

    private protected virtual bool AtStart => _pageIndex < 0;

    private protected virtual bool AtEnd => _pageIndex >= _ownedPages.Count;

    /// <summary>
    /// Moves one place on (or back) in the cursor's order and returns the pointer (<see cref="RowPointer"/>) of
    /// what is there, which need not be a row any longer; null, with the cursor left after its last row (or
    /// before its first), when there is no further place.
    /// </summary>
    private protected virtual int? Step(bool forward)
    {
        var format = _file.Format;
        if (forward)
        {
            int pageIndex = _pageIndex;
            int rowIndex  = _rowIndex + 1;
            if (pageIndex < 0) { pageIndex = 0; rowIndex = 0; }

            for (; pageIndex < _ownedPages.Count; pageIndex++, rowIndex = 0)
            {
                if (rowIndex >= SlotsOn(_ownedPages[pageIndex])) continue;
                _pageIndex = pageIndex;
                _rowIndex  = rowIndex;
                return RowPointer.Pack(_ownedPages[pageIndex], rowIndex);
            }
            _pageIndex = _ownedPages.Count;
            _rowIndex  = 0;
            return null;
        }
        else
        {
            int pageIndex = _pageIndex;
            int rowIndex  = _rowIndex - 1;
            if (pageIndex >= _ownedPages.Count)
            {
                pageIndex = _ownedPages.Count - 1;
                rowIndex  = pageIndex >= 0 ? SlotsOn(_ownedPages[pageIndex]) - 1 : -1;
            }

            while (pageIndex >= 0)
            {
                if (rowIndex >= 0)
                {
                    _pageIndex = pageIndex;
                    _rowIndex  = rowIndex;
                    return RowPointer.Pack(_ownedPages[pageIndex], rowIndex);
                }
                pageIndex--;
                if (pageIndex >= 0) rowIndex = SlotsOn(_ownedPages[pageIndex]) - 1;
            }
            _pageIndex = -1;
            _rowIndex  = -1;
            return null;
        }
    }

    // The slots of a page the cursor gives rows from: those the page had when the cursor listed the table's pages, of
    // which a pointer can name 256 - Jet addresses a row on its page with one byte.
    private int SlotsOn(int pageNumber)
    {
        int slots = ByteUtil.GetShort(PageAt(pageNumber), _file.Format.OffsetDataNumRows);
        return Math.Min(_scan?.Slots(pageNumber, slots) ?? slots, 256);
    }

    /// <summary>Where the cursor is, to come back to (<see cref="Restore"/>).</summary>
    private protected virtual object Place()
        => AtStart ? PlaceBeforeFirst : AtEnd ? PlaceAfterLast : (object)RowPointer.Pack(_ownedPages[_pageIndex], _rowIndex);

    /// <summary>Puts the cursor back where <see cref="Place"/> said it was.</summary>
    private protected virtual void Restore(object place)
    {
        if (ReferenceEquals(place, PlaceBeforeFirst)) { _pageIndex = -1; _rowIndex = -1; return; }
        if (ReferenceEquals(place, PlaceAfterLast))   { _pageIndex = _ownedPages.Count; _rowIndex = 0; return; }

        int pointer   = (int)place;
        int pageIndex = _ownedPages.BinarySearch(RowPointer.Page(pointer));
        if (pageIndex < 0) pageIndex = _ownedPages.IndexOf(RowPointer.Page(pointer));
        if (pageIndex < 0)
        {
            // A page the table has lost since - it gives none up - is past every page it has.
            _pageIndex = _ownedPages.Count;
            _rowIndex  = 0;
            return;
        }
        _pageIndex = pageIndex;
        _rowIndex  = RowPointer.Row(pointer);
    }

    private protected static readonly object PlaceBeforeFirst = new();
    private protected static readonly object PlaceAfterLast   = new();

    /// <summary>
    /// Where the cursor is, for a savepoint to keep (<see cref="RestorePosition"/>): more than <see cref="Place"/>
    /// where the cursor also knows what it has come to, which the savepoint keeps up to date as rows move.
    /// </summary>
    private protected virtual object SavePosition() => Place();

    /// <summary>Puts the cursor back where <see cref="SavePosition"/> said it was; the position can be restored again.</summary>
    private protected virtual void RestorePosition(object position) => Restore(position);

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The names of the columns rows are read with, in the table's column order, or null (as at first) for all
    /// of them. A cursor that reads a few columns of every row is spared decoding the rest, long values among
    /// them: Jackcess's column names of an iterable. A row read with some of the columns can be given to
    /// <see cref="Table.Update"/> and <see cref="UpdateCurrentRow"/> like any other; the columns it was not read
    /// with keep their values. Setting it does not read the current row again.
    /// </summary>
    /// <exception cref="ArgumentException">The value names no column, or a column the table does not have.</exception>
    public IReadOnlyList<string>? ColumnNames
    {
        get => ColumnsToRead?.Select(c => c.Name).ToArray();
        set
        {
            if (value is null) { ColumnsToRead = null; return; }
            var asked = value.Select(name => ColumnNamed(name, nameof(value))).ToList();
            if (asked.Count == 0)
                throw new ArgumentException("A cursor reads at least one column: give null for all of them.", nameof(value));
            ColumnsToRead = _definition.Columns.Where(asked.Contains).ToList();
        }
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
    internal int CurrentRowPointer => _currentRow!.Pointer;

    /// <summary>
    /// Reads one column of the row the cursor is on, as the file has it at this moment: Jackcess's
    /// <c>getCurrentRowValue</c>. Only that column is decoded, so a long value the row also holds is not read.
    /// </summary>
    /// <param name="columnName">The column's name, whatever the case of its letters.</param>
    /// <returns>The value, or null for a column with no value.</returns>
    /// <exception cref="ArgumentException">The table has no column of that name.</exception>
    /// <exception cref="InvalidOperationException">
    /// The cursor is not on a row (<see cref="Current"/> is null), or the row has been deleted through
    /// something other than this cursor since the cursor read it.
    /// </exception>
    public object? GetCurrentRowValue(string columnName)
    {
        var column = ColumnNamed(columnName, nameof(columnName));
        var row    = OnRow();
        var read   = _table.ReadRowAsReadAt(row.Pointer, new[] { column }) ?? throw Table.NoRowAt(_table.Name, row.Pointer);
        return read[column.Name];
    }

    /// <summary>
    /// The number the row the cursor is on stores in a Date/Time column: days since 30 December 1899, the time
    /// of day its fraction. It is the value Access holds and keys the row by, to the last digit, where the
    /// <see cref="DateTime"/> a row is read with is rounded to the millisecond; given to a lookup -
    /// <see cref="FindRow(string, object?)"/>, <see cref="IndexCursor.FindRowByEntry"/>, a range - it finds the
    /// row that stores exactly that number.
    /// </summary>
    /// <param name="columnName">The column's name, whatever the case of its letters.</param>
    /// <returns>The number stored, or null for a column with no value.</returns>
    /// <exception cref="ArgumentException">The table has no column of that name, or it is not a Date/Time column.</exception>
    /// <exception cref="InvalidOperationException">
    /// The cursor is not on a row (<see cref="Current"/> is null), or the row has been deleted since the cursor
    /// read it.
    /// </exception>
    public double? GetCurrentRowDateNumber(string columnName)
    {
        var column = ColumnNamed(columnName, nameof(columnName));
        if (column.DataType != DataType.DateTime)
            throw new ArgumentException($"'{column.Name}' is a {column.DataType} column, not a Date/Time one.", nameof(columnName));
        var row  = OnRow();
        var read = _table.ReadRowAt(row.Pointer, new[] { column }) ?? throw Table.NoRowAt(_table.Name, row.Pointer);
        return read.TryGetValue(column.Name, out var stored) ? (double?)stored : null;
    }

    private protected Column ColumnNamed(string columnName, string parameterName)
    {
        if (columnName is null) throw new ArgumentNullException(parameterName);
        return _definition.FindColumn(columnName)
            ?? throw new ArgumentException($"Table '{_definition.Name}' has no column named '{columnName}'.", parameterName);
    }

    private Row OnRow()
        => _currentRow ?? throw new InvalidOperationException(
            "The cursor is not on a row: it is before the first row or after the last, its last move found none, or the row it was on has been deleted.");

    private byte[] PageAt(int pageNumber)
    {
        if (_page is null || _pageNumber != pageNumber || _pageStamp != _file.ChangeStamp)
        {
            _page       = _file.ReadPage(pageNumber);
            _pageNumber = pageNumber;
            _pageStamp  = _file.ChangeStamp;
        }
        return _page;
    }

    /// <summary>
    /// The row at <paramref name="pointer"/>, with the columns the cursor reads, or null when no row of the table
    /// is there: a slot of a deleted row, or of values moved from another slot; a pointer an index entry kept
    /// after its row had gone.
    /// </summary>
    private protected Row? ReadRow(int pointer)
    {
        int pageNumber = RowPointer.Page(pointer);
        if (pageNumber <= 0 || pageNumber >= _file.PageCount) return null;
        byte[] page = PageAt(pageNumber);
        if (page[0] != JetFormat.PageTypeData || ByteUtil.GetInt(page, JetFormat.OffsetDataTdefPage) != _definition.TdefPageNumber)
            return null;
        if (DataRows.Read(_file, page, RowPointer.Row(pointer)) is not { } rowBytes) return null;

        // Every column read has its entry, in the table's order: null for a column with no value.
        var columns = ColumnsToRead ?? _definition.Columns;
        var row = new Row(columns.Count, _definition.Names);
        foreach (var col in columns)
            row.Put(col.Name, ValuesAsStored ? _decoder.DecodeForKey(rowBytes, col) : _decoder.Decode(rowBytes, col));
        row.ReadFrom(_table, pointer);
        return row;
    }

    // ── Changing the current row ─────────────────────────────────────────────

    /// <summary>
    /// Gives the row the cursor is on the values in <paramref name="values"/>, as <see cref="Table.Update"/>
    /// does - every index kept, every enforced relationship kept or the update refused: Jackcess's
    /// <c>updateCurrentRowFromMap</c>. <see cref="Current"/> is the row as it is afterwards.
    /// <para>
    /// What is assigned is what <see cref="Table.Update"/> assigns. A <see cref="Row"/> read from this table -
    /// <see cref="Current"/> among them, given back with a column changed - assigns the columns given a value
    /// since it was read, and no other: the rest of it, which may no longer be what the file holds, is left as
    /// the file has it, and a <see cref="Row"/> that is the row updated has nothing assigned afterwards. Any other
    /// set of values assigns every column it names. A column that is not assigned keeps its value.
    /// </para>
    /// <para>
    /// The cursor stays where it is: the next move goes on to the row after the one updated. A row keeps its
    /// place in the table when it is updated. In an index's order it moves when the update changes its key in
    /// that index, and a cursor over the index going one way does not come a second time to a row it has given,
    /// whoever updated the row; it comes to every row it has not (<see cref="IndexCursor"/> says what turning
    /// back does).
    /// </para>
    /// </summary>
    /// <param name="values">The values to give the row, by column name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A value given to a Currency column is of a kind no number is made of (<see cref="Table.Insert"/>). Nothing is
    /// written.
    /// </exception>
    /// <exception cref="FormatException">
    /// A value given to a Currency column is text that does not read as a number (<see cref="Table.Insert"/>).
    /// Nothing is written.
    /// </exception>
    /// <exception cref="OverflowException">
    /// A value given to a Currency column is a number no Currency holds (<see cref="Table.Insert"/>). Nothing is
    /// written.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The cursor is not on a row (<see cref="Current"/> is null), or the row has been deleted through
    /// something other than this cursor since the cursor read it. Nothing is written.
    /// </exception>
    /// <exception cref="ConstraintViolationException">
    /// The updated row would break a rule of the table or of a relationship (<see cref="Table.Update"/>). The
    /// row is left as it was.
    /// </exception>
    /// <exception cref="ColumnValueTooLongException">
    /// A Text or Binary value of the updated row is longer than its column - one given, or one the row holds already
    /// (<see cref="Table.Update"/>). The row is left as it was.
    /// </exception>
    /// <exception cref="NotSupportedException">The table's rows cannot be changed (<see cref="Table.Update"/>). Nothing is written.</exception>
    public void UpdateCurrentRow(Row values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var row     = OnRow();
        int pointer = row.Pointer;

        // One rule with Table.Update: a row read from the table assigns what it was given since.
        bool read = ReferenceEquals(values.Source, _table);
        _table.UpdateAt(pointer, read ? values.AssignedSinceRead() : values);
        if (read && values.Pointer == pointer) values.ReadFrom(_table, pointer);
        _currentRow = ReadRow(pointer);
    }

    /// <summary>
    /// Deletes the row the cursor is on, as <see cref="Table.Delete"/> does - its index entries gone, the rows
    /// that refer to it through an enforced relationship deleted, set to null, or refusing the delete: Jackcess's
    /// <c>deleteCurrentRow</c>. The cursor stays where it is, on no row: <see cref="Current"/> is null, and the
    /// next move goes on to the row after the one deleted.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The cursor is not on a row (<see cref="Current"/> is null), or the row has been deleted through
    /// something other than this cursor since the cursor read it. Nothing is deleted.
    /// </exception>
    /// <exception cref="ConstraintViolationException">
    /// Rows of another table refer to the row through a relationship that is enforced and does not cascade.
    /// Nothing is deleted.
    /// </exception>
    /// <exception cref="NotSupportedException">The table's rows cannot be changed (<see cref="Table.Delete"/>). Nothing is deleted.</exception>
    public void DeleteCurrentRow()
    {
        var row = OnRow();
        _table.DeleteAt(row.Pointer);
        row.Detach();
        _currentRow = null;
    }

    // ── Finding ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves to the first row, in the cursor's order, whose <paramref name="columnName"/> is
    /// <paramref name="value"/>, and returns it; when there is none, returns null and leaves the cursor where it
    /// was. Jackcess's <c>findFirstRow</c>.
    /// <para>
    /// Text is matched as its column's sort order has it, which is what Access's <c>=</c> goes by: without
    /// regard to case or to spaces at its end, an accent making another letter; and character for character
    /// where this library makes no index key for the text. A number is matched with a number of another type
    /// that is equal to it, a Double or a Single by its bits, and text is never read as a number. A Date/Time is
    /// the number the row stores, days since 30 December 1899: a <see cref="DateTime"/>, taken first to what it
    /// reads as once a row stores it (to the millisecond, as <see cref="DateTime.Now"/> is not), finds every row
    /// whose number reads as that, so the date read from a row, or written to it, finds the row again; and a
    /// <see cref="double"/> the row that stores exactly that number (<see cref="GetCurrentRowDateNumber"/> gives
    /// a row's), so two dates less than a millisecond apart are told apart. A Date/Time Extended is the 42 bytes the
    /// row stores, which Access writes a date in three layouts of - one by its <c>INSERT</c>, one by its SQL
    /// <c>UPDATE</c>, DAO's <c>AddNew</c> and <c>Edit</c> on a dynaset and its own forms and datasheets, and one by DAO's
    /// <c>AddNew</c> and <c>Edit</c> on a table-type recordset - and a <see cref="DateTime"/> finds every row whose
    /// bytes say that date, read as Access reads them, in whichever layout. A null is never a match: a row with no value
    /// in a column is not found by looking for null. A cursor over a table and one over an index find the same rows, but
    /// for a Date/Time Extended value in a shape that reads as its date and that Access was not seen to write - a space
    /// or a plus sign among its digits, fewer digits, a count other than 7 or 0 - which only a cursor over the table
    /// finds by its date: an index holds it under a key of its own.
    /// </para>
    /// </summary>
    /// <param name="columnName">The name of the column to look in, whatever the case of its letters.</param>
    /// <param name="value">The value to look for. Null finds no row.</param>
    /// <returns>The row, which is then <see cref="Current"/>, or null when no row has the value in that column.</returns>
    /// <exception cref="ArgumentException">The table has no column of that name.</exception>
    public Row? FindRow(string columnName, object? value)
        => Find(new[] { (ColumnNamed(columnName, nameof(columnName)), value) }, fromStart: true);

    /// <summary>
    /// Moves to the first row, in the cursor's order, that has every value of <paramref name="pattern"/> in the
    /// column it is named for, and returns it; when there is none, returns null and leaves the cursor where it
    /// was. Jackcess's <c>findFirstRow</c> with a row pattern. Values are matched as
    /// <see cref="FindRow(string, object?)"/> matches one.
    /// </summary>
    /// <param name="pattern">The values to look for, by column name. A <see cref="Row"/> is one.</param>
    /// <returns>The row, which is then <see cref="Current"/>, or null when no row matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pattern"/> has no values, or names a column the table does not have.
    /// </exception>
    public Row? FindRow(IReadOnlyDictionary<string, object?> pattern)
        => Find(PatternOf(pattern, nameof(pattern)), fromStart: true);

    /// <summary>
    /// Moves to the next row after the one the cursor is on, in the cursor's order, whose
    /// <paramref name="columnName"/> is <paramref name="value"/>, and returns it; when there is none, returns
    /// null and leaves the cursor where it was. Jackcess's <c>findNextRow</c>. Values are matched as
    /// <see cref="FindRow(string, object?)"/> matches one.
    /// </summary>
    /// <param name="columnName">The name of the column to look in, whatever the case of its letters.</param>
    /// <param name="value">The value to look for. Null finds no row.</param>
    /// <returns>The row, which is then <see cref="Current"/>, or null when no further row has the value in that column.</returns>
    /// <exception cref="ArgumentException">The table has no column of that name.</exception>
    public Row? FindNextRow(string columnName, object? value)
        => Find(new[] { (ColumnNamed(columnName, nameof(columnName)), value) }, fromStart: false);

    /// <summary>
    /// Moves to the next row after the one the cursor is on, in the cursor's order, that has every value of
    /// <paramref name="pattern"/> in the column it is named for, and returns it; when there is none, returns
    /// null and leaves the cursor where it was. Jackcess's <c>findNextRow</c> with a row pattern. Values are
    /// matched as <see cref="FindRow(string, object?)"/> matches one.
    /// </summary>
    /// <param name="pattern">The values to look for, by column name. A <see cref="Row"/> is one.</param>
    /// <returns>The row, which is then <see cref="Current"/>, or null when no further row matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pattern"/> has no values, or names a column the table does not have.
    /// </exception>
    public Row? FindNextRow(IReadOnlyDictionary<string, object?> pattern)
        => Find(PatternOf(pattern, nameof(pattern)), fromStart: false);

    private (Column Column, object? Value)[] PatternOf(IReadOnlyDictionary<string, object?> pattern, string parameterName)
    {
        if (pattern is null) throw new ArgumentNullException(parameterName);
        var values = pattern.Select(v => (ColumnNamed(v.Key, parameterName), v.Value)).ToArray();
        if (values.Length == 0)
            throw new ArgumentException("A pattern names at least one column and the value to find in it.", parameterName);
        return values;
    }

    /// <summary>
    /// Moves to the first row (or the next after the cursor's place) that matches <paramref name="pattern"/> and
    /// returns it, read with the cursor's own columns; with none, returns null and leaves the cursor as it was.
    /// </summary>
    private protected Row? Find(IReadOnlyList<(Column Column, object? Value)> pattern, bool fromStart)
    {
        // A null is never a match, so a pattern with one matches no row.
        if (pattern.Any(p => RowValues.IsNull(p.Value))) return null;
        // A number looked for in a Currency column is the value Access takes it to (CurrencyRounding): as that value
        // an index is searched for it and every row compared with it. One no Currency holds, and a value that is no
        // number, is no row's.
        if (!AsLookedFor(ref pattern)) return null;

        object place      = BeginMoving();
        var    current    = _currentRow;
        var    columns    = ColumnsToRead;
        bool   asStored   = ValuesAsStored;
        Row?   row        = null;

        try
        {
            // Only the pattern's columns are decoded while looking, as their keys are made of them.
            ColumnsToRead  = pattern.Select(p => p.Column).Distinct().ToList();
            ValuesAsStored = true;
            int? found;
            try
            {
                found = Seek(pattern, fromStart);
            }
            finally
            {
                ColumnsToRead  = columns;
                ValuesAsStored = asStored;
            }

            // The row found, read again with the cursor's own columns - which can fail where the search did
            // not, on a column the search did not read.
            if (found is { } pointer) row = ReadRow(pointer);
        }
        catch
        {
            // A row that could not be read stops the search: the cursor is as it was before it.
            DoneMoving(restoreTo: place);
            _currentRow = current;
            throw;
        }

        if (row is not null)
        {
            DoneMoving(restoreTo: null);
            return _currentRow = row;
        }

        DoneMoving(restoreTo: place);
        _currentRow = current;
        return null;
    }

    /// <summary>
    /// Moves on to the first row matching <paramref name="pattern"/> - from before the first row, or from where
    /// the cursor is - and returns its pointer, or null when there is none. The cursor reads the pattern's
    /// columns, as stored, while this runs.
    /// </summary>
    private protected virtual int? Seek(IReadOnlyList<(Column Column, object? Value)> pattern, bool fromStart)
    {
        if (fromStart) ToStart();
        while (Move(forward: true) is { } row)
            if (Matches(row, pattern)) return row.Pointer;
        return null;
    }

    /// <summary>
    /// <paramref name="pattern"/> with each value asked of a Currency column replaced by the decimal Access takes it
    /// to - its exact value rounded to four places, half to even (#49, #65) - which is the value a row written with it
    /// stores, so that an index is searched by that value's key and every row compared with that value. False when a
    /// value is not a number, or is a number no Currency holds: no row has it.
    /// </summary>
    private static bool AsLookedFor(ref IReadOnlyList<(Column Column, object? Value)> pattern)
    {
        (Column Column, object? Value)[]? taken = null;
        for (int i = 0; i < pattern.Count; i++)
        {
            var (column, value) = pattern[i];
            if (column.DataType != DataType.Currency) continue;
            if (!CurrencyRounding.TryUnits(value, out long units)) return false;
            taken ??= pattern.ToArray();
            taken[i] = (column, CurrencyRounding.ToDecimal(units));
        }
        if (taken is not null) pattern = taken;
        return true;
    }

    private protected static bool Matches(Row row, IReadOnlyList<(Column Column, object? Value)> pattern)
    {
        foreach (var (column, value) in pattern)
            if (!ValueMatch.Matches(column, row[column.Name], value)) return false;
        return true;
    }

    /// <summary>Moves on (or back) to the next row there is and returns it, as <see cref="MoveNext"/> does, for a kind of cursor to look through its rows with.</summary>
    private protected Row? MoveToRow(bool forward) => Move(forward);

    /// <summary>The row the cursor is on, for a kind of cursor to put back when a move of its own fails.</summary>
    private protected Row? CurrentRow { get => _currentRow; set => _currentRow = value; }

    // ── Savepoints ────────────────────────────────────────────────────────────

    /// <summary>
    /// Notes where the cursor is, to come back to with <see cref="RestoreSavepoint"/>: Jackcess's
    /// <c>getSavepoint</c>. A savepoint is a place in the cursor's order, not a copy of a row. A cursor over an
    /// index notes with it which rows it has come to going the way it is going, and keeps that up to date as
    /// updates move rows in its index, so that restored it does not give a row a second time
    /// (<see cref="IndexCursor"/>).
    /// </summary>
    public CursorSavepoint GetSavepoint() => new(this, SavePosition());

    /// <summary>
    /// Puts the cursor back where it was when <paramref name="savepoint"/> was taken: Jackcess's
    /// <c>restoreSavepoint</c>. <see cref="Current"/> is the row at that place as the file has it now - null when
    /// the cursor was on no row, or when the row has since been deleted;
    /// the next move goes on from that place either way, passing the rows it had come to when the savepoint was
    /// taken wherever updates have moved them since. A savepoint can be restored any number of times.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="savepoint"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="savepoint"/> was taken from another cursor.</exception>
    public void RestoreSavepoint(CursorSavepoint savepoint)
    {
        if (savepoint is null) throw new ArgumentNullException(nameof(savepoint));
        if (!ReferenceEquals(savepoint.Cursor, this))
            throw new ArgumentException("The savepoint was taken from another cursor.", nameof(savepoint));
        RestorePosition(savepoint.Place);
        // The row as the file has it now: beside a writer, not as the page kept from the last move has it (#62).
        if (_file.SharedWithWriters) ForgetKeptPages();
        _currentRow = PointerAt() is { } pointer ? ReadRow(pointer) : null;
    }

    /// <summary>The pointer of the place the cursor is at, or null before the first row and after the last.</summary>
    private protected virtual int? PointerAt()
        => AtStart || AtEnd ? null : RowPointer.Pack(_ownedPages[_pageIndex], _rowIndex);

    // ── IEnumerable ───────────────────────────────────────────────────────────

    /// <summary>
    /// The cursor's rows from the first: the cursor is put before the first row, then moved through them all.
    /// Enumerating moves the cursor itself, so one cursor is enumerated by one loop at a time.
    /// </summary>
    /// <returns>An enumerator that reads a row each time it is moved, and leaves the cursor after the last row.</returns>
    public IEnumerator<Row> GetEnumerator()
    {
        MoveBeforeFirst();
        while (NextRow() is { } r) yield return r;
    }

    /// <summary>
    /// The cursor's rows from the last to the first: the cursor is put after the last row, then moved back
    /// through them all. Enumerating moves the cursor itself, so one cursor is enumerated by one loop at a time.
    /// </summary>
    /// <returns>Rows that are read as they are enumerated; the cursor is left before the first row.</returns>
    public IEnumerable<Row> EnumerateBackward()
    {
        MoveAfterLast();
        while (PreviousRow() is { } r) yield return r;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A place a <see cref="Cursor"/> was at, to put it back to: taken with <see cref="Cursor.GetSavepoint"/> and
/// given to <see cref="Cursor.RestoreSavepoint"/> of the same cursor. Jackcess's <c>Cursor.Savepoint</c>.
/// </summary>
public sealed class CursorSavepoint
{
    internal CursorSavepoint(Cursor cursor, object place)
    {
        Cursor = cursor;
        Place  = place;
    }

    internal Cursor Cursor { get; }
    internal object Place  { get; }
}
