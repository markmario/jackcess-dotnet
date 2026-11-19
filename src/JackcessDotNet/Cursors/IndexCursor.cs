namespace JackcessDotNet;

/// <summary>
/// A cursor over the rows of one table in the order of one of its indexes, which also finds rows by the values
/// they have in the index's columns: made by <see cref="Table.CreateIndexCursor()"/> for the table's primary key,
/// or by <see cref="Table.CreateIndexCursor(string)"/> for the index of a name; with an
/// <see cref="IndexRange"/>, over the rows between two entries of the index only. Jackcess's
/// <c>IndexCursor</c>. To find one row by its primary key, <see cref="Table.FindRowByPrimaryKey"/> does it in
/// one call.
/// <para>
/// The rows are those the index has entries for, in the entries' order: by the index's columns, each ascending
/// or descending as the index has it, nulls first in an ascending column, and rows with the same values in the
/// order they are stored. An index is taken at its word, as Access takes one. Every lookup -
/// <see cref="FindRowByEntry"/>, <see cref="FindRowsByEntry"/>, <see cref="FindClosestRowByEntry"/>, and
/// <see cref="Cursor.FindRow(string, object?)"/> for the index's first column - goes through this index and no
/// other, and leaves the cursor on the row it found.
/// </para>
/// <para>
/// A row is looked for by the key Access keeps for its values, where this library makes that key: for every
/// type it writes index entries for, and for text in the sort orders General - Legacy and General. A value it
/// makes no key for - text in another sort order, a character Access keys by the characters around it, a key
/// longer than an entry holds - is still found by <see cref="FindRowByEntry"/> and
/// <see cref="Cursor.FindRow(string, object?)"/>, which then go through the index's rows comparing each; a range
/// and <see cref="FindClosestRowByEntry"/> need the key, and refuse such a value. Whichever way a row is found,
/// it is compared with the values asked for before it is returned (<see cref="Cursor.FindRow(string, object?)"/>
/// says how), and a null is never a match.
/// </para>
/// </summary>
public sealed class IndexCursor : Cursor
{
    private enum Where { BeforeFirst, AtEntry, AfterLast }

    private readonly Index            _index;
    private readonly IndexEntryWalker _entries;

    // The range: keys from _low on, and before _high; null for no bound. _none: a range no key can be in.
    private readonly byte[]? _low;
    private readonly byte[]? _high;
    private readonly bool    _none;

    // Where the cursor is: an entry's key and row pointer, which stay a place in the index's order whatever
    // becomes of the entry.
    private Where  _where = Where.BeforeFirst;
    private byte[] _key   = Array.Empty<byte>();
    private int    _pointer;

    internal IndexCursor(Table table, PageFile file, TableDefinition definition, Index index, IndexRange? range)
        : base(table, file, definition)
    {
        _index = index;
        if (index.RootPageNumber <= 0 || index.Columns.Count == 0)
            throw new NotSupportedException(
                $"Index '{index.Name}' of table '{definition.Name}' cannot be read: its definition gives it no column or no page.");
        _entries = new IndexEntryWalker(file, index.RootPageNumber);

        if (range?.StartEntry is { } start)
        {
            var (first, last) = BoundKeys(start, nameof(range), "start");
            _low  = range.IsStartInclusive ? first : IndexEntryWalker.AfterAllBeginningWith(last);
            _none = _low is null;
        }
        if (range?.EndEntry is { } end)
        {
            var (first, last) = BoundKeys(end, nameof(range), "end");
            _high = range.IsEndInclusive ? IndexEntryWalker.AfterAllBeginningWith(last) : first;
        }
        table.Watch(this);
    }

    /// <summary>
    /// The index the cursor was made for: the table's primary key for <see cref="Table.CreateIndexCursor()"/>,
    /// and the index of that name for <see cref="Table.CreateIndexCursor(string)"/>. Jackcess's <c>getIndex</c>.
    /// </summary>
    public Index Index => _index;

    // ── The cursor's order ────────────────────────────────────────────────────

    private protected override void ToStart()
    {
        _where = Where.BeforeFirst;
        Jumped();
    }

    private protected override void ToEnd()
    {
        _where = Where.AfterLast;
        Jumped();
    }

    private protected override bool AtStart => _where == Where.BeforeFirst;

    private protected override bool AtEnd => _where == Where.AfterLast;

    private protected override int? Step(bool forward)
    {
        (byte[] Key, int RowPointer)? entry = null;
        if (!_none)
        {
            if (forward)
            {
                entry = _where switch
                {
                    Where.BeforeFirst => _low is null ? _entries.First() : _entries.After(_low, 0),
                    Where.AtEntry     => _entries.After(_key, _pointer),
                    _                 => null,
                };
                if (entry is { } found && _high is not null && IndexEntryWalker.CompareKeys(found.Key, _high) >= 0) entry = null;
            }
            else
            {
                entry = _where switch
                {
                    Where.AfterLast => _high is null ? _entries.Last() : _entries.Before(_high, 0),
                    Where.AtEntry   => _entries.Before(_key, _pointer),
                    _               => null,
                };
                if (entry is { } found && _low is not null && IndexEntryWalker.CompareKeys(found.Key, _low) < 0) entry = null;
            }
        }

        if (entry is not { } next)
        {
            _where = forward ? Where.AfterLast : Where.BeforeFirst;
            return null;
        }
        _where   = Where.AtEntry;
        _key     = next.Key;
        _pointer = next.RowPointer;
        return _pointer;
    }

    private protected override object Place()
        => (_where, _key, _pointer,
            _movedAhead is { Count: > 0 } ? new HashSet<int>(_movedAhead) : null,
            _movedBehind is { Count: > 0 } ? new HashSet<int>(_movedBehind) : null);

    private protected override void Restore(object place)
        => (_where, _key, _pointer, _movedAhead, _movedBehind) = ((Where, byte[], int, HashSet<int>?, HashSet<int>?))place;

    private protected override int? PointerAt() => _where == Where.AtEntry && _pointer != 0 ? _pointer : null;

    // ── Rows that move in the index while the cursor is in it ────────────────

    // The rows the cursor has been at or past whose entries an update has since moved to the other side of it:
    // ahead of it (met again going forwards) and behind it (met again going backwards). Each is passed once, and
    // is then where the cursor has been.
    private HashSet<int>? _movedAhead;
    private HashSet<int>? _movedBehind;

    /// <summary>
    /// Told by the table that an update has changed the key of the row at <paramref name="rowPointer"/> in the
    /// index tree numbered <paramref name="tree"/>, by this cursor or by anything else. A row the cursor has
    /// given already, now on the other side of its place, is not given a second time when the cursor comes to
    /// its new entry.
    /// </summary>
    internal void EntryMoved(int tree, int rowPointer, byte[] oldKey, byte[] newKey)
    {
        if (tree != _index.IndexDataNumber || _where != Where.AtEntry) return;
        int was = Against(oldKey, rowPointer), now = Against(newKey, rowPointer);
        if (was <= 0 && now > 0)
        {
            (_movedAhead ??= new HashSet<int>()).Add(rowPointer);
            _movedBehind?.Remove(rowPointer);
        }
        else if (was >= 0 && now < 0)
        {
            (_movedBehind ??= new HashSet<int>()).Add(rowPointer);
            _movedAhead?.Remove(rowPointer);
        }
    }

    // Where an entry is against the cursor's place: before it (less than 0), at it, or after it.
    private int Against(byte[] key, int rowPointer)
    {
        int byKey = IndexEntryWalker.CompareKeys(key, _key);
        return byKey != 0 ? byKey : ((uint)rowPointer).CompareTo((uint)_pointer);
    }

    private protected override bool Passes(int pointer, bool forward)
        => (forward ? _movedAhead : _movedBehind)?.Remove(pointer) == true;

    private protected override void Jumped()
    {
        _movedAhead  = null;
        _movedBehind = null;
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves to the first row, in the index's order, whose values in the index's first columns are
    /// <paramref name="entryValues"/>, and returns it; when there is none, returns null and leaves the cursor
    /// where it was. Jackcess's <c>findRowByEntry</c>. A value is given for each of the index's columns, in the
    /// index's order, or for the first of them only: fewer values than the index has columns find the first row
    /// that has those in its leading columns.
    /// </summary>
    /// <param name="entryValues">The values to look for, in the order of the index's columns. A null among them finds no row.</param>
    /// <returns>The row, which is then <see cref="Cursor.Current"/>, or null when no row has those values.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entryValues"/> is null, has no values, or has more values than the index has columns.
    /// </exception>
    public Row? FindRowByEntry(params object?[] entryValues)
        => Find(EntryPattern(entryValues, nameof(entryValues)), fromStart: true);

    /// <summary>
    /// The rows whose values in the index's first columns are <paramref name="entryValues"/>, in the index's
    /// order: Jackcess's <c>newEntryIterable</c>. The rows are read as they are enumerated, and enumerating moves
    /// the cursor: it is left on the last of them, or where it was when there is none.
    /// </summary>
    /// <param name="entryValues">The values to look for, in the order of the index's columns. A null among them finds no row.</param>
    /// <returns>The rows, each of which is <see cref="Cursor.Current"/> as it is given.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entryValues"/> is null, has no values, or has more values than the index has columns.
    /// </exception>
    public IEnumerable<Row> FindRowsByEntry(params object?[] entryValues)
    {
        var pattern = EntryPattern(entryValues, nameof(entryValues));
        return Rows();

        IEnumerable<Row> Rows()
        {
            for (var row = Find(pattern, fromStart: true); row is not null; row = Find(pattern, fromStart: false))
                yield return row;
        }
    }

    /// <summary>
    /// Moves to the first row, in the index's order, whose entry is at or after <paramref name="entryValues"/>,
    /// and returns it: the row with those values when there is one, and otherwise the row that comes next.
    /// Jackcess's <c>findClosestRowByEntry</c>. When no row of the cursor is at or after the values, the cursor
    /// is left after its last row and null is returned. A value is given for each of the index's columns, in the
    /// index's order, or for the first of them only; a null is the value an index holds for a column with none.
    /// </summary>
    /// <param name="entryValues">The values to look for, in the order of the index's columns.</param>
    /// <returns>The row, which is then <see cref="Cursor.Current"/>, or null when there is none at or after the values.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entryValues"/> is null, has no values, has more values than the index has columns, or has
    /// a value that is not one its column holds.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// No key is made for the values: the index is on a column whose keys this library does not make (a
    /// Decimal, Binary, OLE or Complex column, or text in a sort order other than General - Legacy and General),
    /// a value has a character Access keys by the characters around it, or the key is longer than an entry
    /// holds. The cursor is left where it was.
    /// </exception>
    public Row? FindClosestRowByEntry(params object?[] entryValues)
    {
        if (entryValues is null) throw new ArgumentException("Entry values are required.", nameof(entryValues));
        var (first, _) = BoundKeys(entryValues, nameof(entryValues), "sought");
        if (_none) { MoveAfterLast(); return null; }

        // No row is at pointer 0: the next entry is the first at or after the key, and within the range. A row
        // that cannot be read leaves the cursor as it was.
        object place = Place();
        var current  = CurrentRow;
        try
        {
            Jumped();
            _where   = Where.AtEntry;
            _key     = _low is not null && IndexEntryWalker.CompareKeys(first, _low) < 0 ? _low : first;
            _pointer = 0;
            return MoveToRow(forward: true);
        }
        catch
        {
            Restore(place);
            CurrentRow = current;
            throw;
        }
    }

    private (Column Column, object? Value)[] EntryPattern(object?[] entryValues, string parameterName)
    {
        if (entryValues is null || entryValues.Length == 0)
            throw new ArgumentException("Entry values are required.", parameterName);
        if (entryValues.Length > _index.Columns.Count)
            throw new ArgumentException(
                $"Index '{_index.Name}' has {_index.Columns.Count} column(s) ({ColumnNamesOf(_index)}); " +
                $"{entryValues.Length} values were given.", parameterName);
        var pattern = new (Column, object?)[entryValues.Length];
        for (int i = 0; i < pattern.Length; i++)
            pattern[i] = (ColumnNamed(_index.Columns[i].Column.Name, parameterName), entryValues[i]);
        return pattern;
    }

    private static string ColumnNamesOf(Index index) => string.Join(", ", index.Columns.Select(c => c.Column.Name));

    /// <summary>
    /// The first and the last key of the entries that have <paramref name="values"/> in the index's first
    /// columns: where a range begins or ends, or a closest row is looked for. They are one key, unless the last
    /// of the values is a <see cref="DateTime"/> for a Date/Time column: then they are the keys of the least and
    /// the greatest number that read as it (<see cref="ValueMatch.StoredRange"/>), so that a bound given as a
    /// date takes in, or leaves out, every row that reads as that date.
    /// </summary>
    private (byte[] First, byte[] Last) BoundKeys(IReadOnlyList<object?> values, string parameterName, string what)
    {
        if (values.Count == 0 || values.Count > _index.Columns.Count)
            throw new ArgumentException(
                $"Index '{_index.Name}' has {_index.Columns.Count} column(s) ({ColumnNamesOf(_index)}); " +
                $"{values.Count} {what} values were given.", parameterName);

        var columns = _index.Columns.Take(values.Count).ToList();
        foreach (var column in columns)
            if (!IndexKeys.CanEncode(column.Column))
                throw new NotSupportedException(
                    $"No key is made for index '{_index.Name}': {IndexKeys.WhyNot(column.Column)}.");
        try
        {
            for (int i = 0; i < columns.Count; i++)
                if (!IndexKeys.CanEncode(columns[i].Column, values[i]))
                    throw new NotSupportedException(
                        $"No key is made for index '{_index.Name}': {IndexKeys.WhyNot(columns[i].Column, values[i])}.");

            byte[] Key(object? last)
            {
                var given = values.ToArray();
                given[given.Length - 1] = last;
                byte[] key = IndexKeys.EncodeWhole(columns, given);
                if (key.Length > LongestKeyOf(columns.Count))
                    throw new NotSupportedException(
                        $"No key is made for index '{_index.Name}': the {what} values' key is longer than an index entry holds.");
                return key;
            }

            int end = values.Count - 1;
            if (values[end] is DateTime date && columns[end].Column.DataType == DataType.DateTime)
            {
                var (least, greatest) = ValueMatch.StoredRange(date);
                byte[] a = Key(least), b = Key(greatest);
                return IndexEntryWalker.CompareKeys(a, b) <= 0 ? (a, b) : (b, a);
            }
            byte[] one = Key(values[end]);
            return (one, one);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException(
                $"A {what} value is not one its column of index '{_index.Name}' holds: {e.Message}", parameterName, e);
        }
    }

    /// <summary>
    /// The parts of the index every entry matching <paramref name="pattern"/> is in, in the index's order, each
    /// from a key to the key it ends before (null: the end of the index) - or null when the pattern has no value
    /// for the first column or no key is made for its values: the rows are then gone through one by one. An
    /// empty list is a value that is not one its column holds, so that no row has it.
    /// <para>
    /// The values of the leading columns make one key, and the entries that begin with it are the part. A
    /// <see cref="DateTime"/> looked for in a Date/Time column is not one key: every number that reads as it is
    /// looked for, so the part runs from the least of them to the greatest, under the key of the columns before
    /// it - and, for a time on 30 December 1899, which a number and its negative both read as, there are two.
    /// </para>
    /// </summary>
    private List<(byte[] From, byte[]? Before)>? PartsFor(IReadOnlyList<(Column Column, object? Value)> pattern)
    {
        var columns = new List<IndexColumn>();
        var values  = new List<object?>();
        DateTime? date = null;
        foreach (var indexColumn in _index.Columns)
        {
            int at = -1;
            for (int i = 0; i < pattern.Count && at < 0; i++)
                if (pattern[i].Column.Ordinal == indexColumn.Column.Ordinal) at = i;
            if (at < 0) break;
            columns.Add(indexColumn);
            values.Add(pattern[at].Value);
            if (indexColumn.Column.DataType == DataType.DateTime && pattern[at].Value is DateTime asked)
            {
                date = asked;
                break;
            }
        }
        if (columns.Count == 0) return null;

        var parts = new List<(byte[] From, byte[]? Before)>();
        try
        {
            byte[]? Key(object? last)
            {
                var given = values.ToArray();
                given[given.Length - 1] = last;
                if (!IndexKeys.CanSearch(columns, given)) return null;
                byte[] key = IndexKeys.EncodeWhole(columns, given);
                return key.Length <= LongestKeyOf(columns.Count) ? key : null;
            }

            if (date is not { } looked)
            {
                if (Key(values[values.Count - 1]) is not { } key) return null;
                parts.Add((key, IndexEntryWalker.AfterAllBeginningWith(key)));
                return parts;
            }

            var (least, greatest) = ValueMatch.StoredRange(looked);
            var ranges = new List<(double, double)> { (least, greatest) };
            if (least > 0 && greatest < 1) ranges.Add((-greatest, -least));
            foreach (var (low, high) in ranges)
            {
                if (Key(low) is not { } a || Key(high) is not { } b) return null;
                if (IndexEntryWalker.CompareKeys(a, b) > 0) (a, b) = (b, a);
                parts.Add((a, IndexEntryWalker.AfterAllBeginningWith(b)));
            }
            parts.Sort((x, y) => IndexEntryWalker.CompareKeys(x.From, y.From));
            return parts;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return new List<(byte[] From, byte[]? Before)>();
        }
    }

    /// <summary>
    /// The longest key of the index's first <paramref name="columns"/> columns that every entry of a row with
    /// those values begins with. An entry longer than it holds is cut, and its last two bytes are a digest
    /// (<see cref="IndexKeys.Truncate"/>): a key of some of the columns that reaches them is not what the cut
    /// entry of the whole row has there.
    /// </summary>
    private int LongestKeyOf(int columns)
        => columns < _index.Columns.Count ? IndexKeys.MaxKeyLength - 2 : IndexKeys.MaxKeyLength;

    private protected override int? Seek(IReadOnlyList<(Column Column, object? Value)> pattern, bool fromStart)
    {
        var parts = PartsFor(pattern);
        if (_none || parts is { Count: 0 }) return null;
        if (parts is null) return base.Seek(pattern, fromStart);

        // Every row that matches has its entry in one of the parts: the cursor goes to the first entry of each
        // in turn - unless it is in the part already, or past it - and no further than the part's last.
        if (fromStart) ToStart();
        foreach (var (from, before) in parts)
        {
            if (_where == Where.AfterLast) break;
            if (_where == Where.AtEntry && before is not null && IndexEntryWalker.CompareKeys(_key, before) >= 0) continue;
            if (_where == Where.BeforeFirst || IndexEntryWalker.CompareKeys(_key, from) < 0)
            {
                _where   = Where.AtEntry;
                _key     = _low is not null && IndexEntryWalker.CompareKeys(from, _low) < 0 ? _low : from;
                _pointer = 0;
            }
            while (MoveToRow(forward: true) is { } row)
            {
                if (before is not null && IndexEntryWalker.CompareKeys(_key, before) >= 0)
                {
                    // Past the part: the entry come to is the next part's to look at, so the cursor is put
                    // just before it.
                    _pointer--;
                    break;
                }
                if (Matches(row, pattern)) return row.Pointer;
            }
        }
        return null;
    }
}

/// <summary>
/// The part of an index an <see cref="IndexCursor"/> goes through: the rows from one entry of the index to
/// another, in the index's order. What Jackcess's <c>CursorBuilder</c> takes as a start and an end entry. Given
/// to <see cref="Table.CreateIndexCursor(IndexRange)"/> or <see cref="Table.CreateIndexCursor(string, IndexRange)"/>.
/// <code>
///   // The orders of customer 7 placed in 2024, through an index on (Customer, Placed).
///   var cursor = orders.CreateIndexCursor("ByCustomerAndDate", new IndexRange
///   {
///       StartEntry = new object?[] { 7, new DateTime(2024, 1, 1) },
///       EndEntry   = new object?[] { 7, new DateTime(2025, 1, 1) },
///       IsEndInclusive = false,
///   });
/// </code>
/// An entry is a value for each of the index's columns, in the index's order, or for the first of them only:
/// with fewer values than the index has columns it stands for every row that has those values in its leading
/// columns - an inclusive start begins at the first such row and an inclusive end stops after the last. A null
/// is the value an index holds for a column with none. The order is the index's own, so in a descending column
/// the start is the greater value.
/// </summary>
public sealed class IndexRange
{
    /// <summary>Where the range begins, or null (the default) for the index's first row.</summary>
    public IReadOnlyList<object?>? StartEntry { get; set; }

    /// <summary>Whether rows with the values of <see cref="StartEntry"/> are in the range. True by default.</summary>
    public bool IsStartInclusive { get; set; } = true;

    /// <summary>Where the range ends, or null (the default) for the index's last row.</summary>
    public IReadOnlyList<object?>? EndEntry { get; set; }

    /// <summary>Whether rows with the values of <see cref="EndEntry"/> are in the range. True by default.</summary>
    public bool IsEndInclusive { get; set; } = true;
}
