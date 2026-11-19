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
            byte[] key = BoundKey(start, nameof(range), "start");
            _low  = range.IsStartInclusive ? key : IndexEntryWalker.AfterAllBeginningWith(key);
            _none = _low is null;
        }
        if (range?.EndEntry is { } end)
        {
            byte[] key = BoundKey(end, nameof(range), "end");
            _high = range.IsEndInclusive ? IndexEntryWalker.AfterAllBeginningWith(key) : key;
        }
    }

    /// <summary>
    /// The index the cursor was made for: the table's primary key for <see cref="Table.CreateIndexCursor()"/>,
    /// and the index of that name for <see cref="Table.CreateIndexCursor(string)"/>. Jackcess's <c>getIndex</c>.
    /// </summary>
    public Index Index => _index;

    // ── The cursor's order ────────────────────────────────────────────────────

    private protected override void ToStart() => _where = Where.BeforeFirst;

    private protected override void ToEnd() => _where = Where.AfterLast;

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

    private protected override object Place() => (_where, _key, _pointer);

    private protected override void Restore(object place) => (_where, _key, _pointer) = ((Where, byte[], int))place;

    private protected override int? PointerAt() => _where == Where.AtEntry && _pointer != 0 ? _pointer : null;

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
        byte[] key = BoundKey(entryValues, nameof(entryValues), "sought");
        if (_none) { MoveAfterLast(); return null; }

        // No row is at pointer 0: the next entry is the first at or after the key, and within the range.
        ForgetRewrittenRows();
        _where   = Where.AtEntry;
        _key     = _low is not null && IndexEntryWalker.CompareKeys(key, _low) < 0 ? _low : key;
        _pointer = 0;
        return MoveToRow(forward: true);
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
    /// The key of <paramref name="values"/> in the index's first columns: where a range begins or ends, or a
    /// closest row is looked for.
    /// </summary>
    private byte[] BoundKey(IReadOnlyList<object?> values, string parameterName, string what)
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
            byte[] key = IndexKeys.EncodeWhole(columns, values);
            if (key.Length > IndexKeys.MaxKeyLength)
                throw new NotSupportedException(
                    $"No key is made for index '{_index.Name}': the {what} values' key is longer than an index entry holds.");
            return key;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException(
                $"A {what} value is not one its column of index '{_index.Name}' holds: {e.Message}", parameterName, e);
        }
    }

    /// <summary>
    /// The key every entry matching <paramref name="pattern"/> begins with - that of its values for the index's
    /// leading columns - or null when the pattern has no value for the first column or no key is made for its
    /// values: the rows are then gone through one by one. <paramref name="noRow"/> is set when a value is not
    /// one its column holds, so that no row has it.
    /// </summary>
    private byte[]? LeadingKey(IReadOnlyList<(Column Column, object? Value)> pattern, out bool noRow)
    {
        noRow = false;
        var columns = new List<IndexColumn>();
        var values  = new List<object?>();
        foreach (var indexColumn in _index.Columns)
        {
            int at = -1;
            for (int i = 0; i < pattern.Count && at < 0; i++)
                if (pattern[i].Column.Ordinal == indexColumn.Column.Ordinal) at = i;
            if (at < 0) break;
            columns.Add(indexColumn);
            values.Add(pattern[at].Value);
        }
        if (columns.Count == 0) return null;

        try
        {
            return IndexKeys.CanSearch(columns, values) ? IndexKeys.EncodeWhole(columns, values) : null;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            noRow = true;
            return null;
        }
    }

    private protected override int? Seek(IReadOnlyList<(Column Column, object? Value)> pattern, bool fromStart)
    {
        byte[]? key = LeadingKey(pattern, out bool noRow);
        if (noRow || _none) return null;
        if (key is null) return base.Seek(pattern, fromStart);

        // Every row that matches has an entry beginning with the key, and those entries are together: the
        // cursor goes to the first of them - unless it is among them already, or past them - and no further
        // than the last.
        if (fromStart || _where == Where.BeforeFirst || (_where == Where.AtEntry && IndexEntryWalker.CompareKeys(_key, key) < 0))
        {
            _where   = Where.AtEntry;
            _key     = _low is not null && IndexEntryWalker.CompareKeys(key, _low) < 0 ? _low : key;
            _pointer = 0;
        }
        while (MoveToRow(forward: true) is { } row)
        {
            if (!IndexEntryWalker.BeginsWith(_key, key)) return null;
            if (Matches(row, pattern)) return row.Pointer;
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
