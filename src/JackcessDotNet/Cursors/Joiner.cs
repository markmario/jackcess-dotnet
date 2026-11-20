namespace JackcessDotNet;

/// <summary>
/// Finds the rows of one table that go with a row of another, through an index: the orders of a customer, the
/// customer of an order. Jackcess's <c>Joiner</c>. A joiner is made for the columns of the table the rows come
/// from and the columns of the table they are looked for in, which must be the first columns of one of that
/// table's indexes - as the columns of a relationship are, on both sides, in a database Access made.
/// <code>
///   var ordersOf = new Joiner(customers, new[] { "CustomerID" }, orders, new[] { "CustomerID" });
///   foreach (Row customer in customers.Rows)
///       foreach (Row order in ordersOf.FindRows(customer))
///           Use(customer, order);
/// </code>
/// Each lookup goes through the index (<see cref="IndexCursor.FindRowsByEntry"/>), so it costs a few pages
/// however many rows the table has, and values are matched as an index cursor matches them: a null joins no
/// row. A Date/Time of a row that was read from the table is joined on the number the row stores, as Access
/// joins it, and a Date/Time Extended joined to one on the bytes the row stores, as an Access relationship joins
/// a child to its parent - by the bytes of the form Access wrote, not by the date they say - unless the row has
/// been given a value in that column since it was read: the value given is joined on then, as a
/// <see cref="DateTime"/> is looked for. A joiner holds nothing open and reads the tables as they are when it is
/// asked.
/// </summary>
public sealed class Joiner
{
    private readonly string[] _fromColumns;
    private readonly string[] _toColumns;

    /// <summary>
    /// Creates a joiner from rows of <paramref name="fromTable"/> to the rows of <paramref name="toTable"/> whose
    /// <paramref name="toColumnNames"/> have the values of the row's <paramref name="fromColumnNames"/>, column
    /// for column.
    /// </summary>
    /// <param name="fromTable">The table whose rows are given to the joiner.</param>
    /// <param name="fromColumnNames">The columns of <paramref name="fromTable"/> whose values are looked for.</param>
    /// <param name="toTable">The table whose rows the joiner finds.</param>
    /// <param name="toColumnNames">
    /// The columns of <paramref name="toTable"/> the values are looked for in: the first columns, in this order,
    /// of one of its indexes.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// No columns are named, or not as many on one side as on the other; a table has no column of a name given
    /// for it; or <paramref name="toTable"/> has no index whose first columns are <paramref name="toColumnNames"/>.
    /// </exception>
    public Joiner(Table fromTable, IEnumerable<string> fromColumnNames, Table toTable, IEnumerable<string> toColumnNames)
    {
        FromTable = fromTable ?? throw new ArgumentNullException(nameof(fromTable));
        ToTable   = toTable ?? throw new ArgumentNullException(nameof(toTable));
        _fromColumns = NamesIn(fromTable, fromColumnNames, nameof(fromColumnNames));
        _toColumns   = NamesIn(toTable, toColumnNames, nameof(toColumnNames));
        if (_fromColumns.Length != _toColumns.Length)
            throw new ArgumentException(
                $"A join matches columns in pairs: {_fromColumns.Length} of '{fromTable.Name}' and {_toColumns.Length} of '{toTable.Name}' were given.",
                nameof(toColumnNames));

        ToIndex = toTable.Indexes.FirstOrDefault(index =>
                index.Columns.Count >= _toColumns.Length
                && index.Columns.Take(_toColumns.Length).Select(c => c.Column.Name).SequenceEqual(_toColumns, StringComparer.Ordinal))
            ?? throw new ArgumentException(
                $"Table '{toTable.Name}' has no index whose first columns are {string.Join(", ", _toColumns)}: a join looks its rows up through one.",
                nameof(toColumnNames));
    }

    private static string[] NamesIn(Table table, IEnumerable<string> columnNames, string parameterName)
    {
        if (columnNames is null) throw new ArgumentNullException(parameterName);
        // Each column as the table spells it: of exactly the name given, or else of one Access takes as one with it.
        var names = columnNames.Select(name =>
            table.Definition.FindColumn(name ?? throw new ArgumentException("A column name must not be null.", parameterName))?.Name
            ?? throw new ArgumentException($"Table '{table.Name}' has no column named '{name}'.", parameterName)).ToArray();
        if (names.Length == 0) throw new ArgumentException("A join is on at least one column.", parameterName);
        return names;
    }

    /// <summary>The table whose rows are given to the joiner.</summary>
    public Table FromTable { get; }

    /// <summary>The table whose rows the joiner finds.</summary>
    public Table ToTable { get; }

    /// <summary>The index of <see cref="ToTable"/> the joiner finds rows through.</summary>
    public Index ToIndex { get; }

    /// <summary>The columns of <see cref="FromTable"/> whose values are looked for, as the table spells them.</summary>
    public IReadOnlyList<string> FromColumnNames => _fromColumns;

    /// <summary>The columns of <see cref="ToTable"/> the values are looked for in, as the table spells them.</summary>
    public IReadOnlyList<string> ToColumnNames => _toColumns;

    /// <summary>Whether a row of <see cref="ToTable"/> goes with <paramref name="fromRow"/>.</summary>
    /// <param name="fromRow">A row of <see cref="FromTable"/>, or the values of its joined columns by name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fromRow"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fromRow"/> has no entry for one of <see cref="FromColumnNames"/>.</exception>
    public bool HasRows(IReadOnlyDictionary<string, object?> fromRow) => FindFirstRow(fromRow) is not null;

    /// <summary>
    /// The first row of <see cref="ToTable"/>, in its index's order, that goes with <paramref name="fromRow"/>,
    /// or null when none does.
    /// </summary>
    /// <param name="fromRow">A row of <see cref="FromTable"/>, or the values of its joined columns by name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fromRow"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fromRow"/> has no entry for one of <see cref="FromColumnNames"/>.</exception>
    public Row? FindFirstRow(IReadOnlyDictionary<string, object?> fromRow)
        => ToTable.CreateIndexCursor(ToIndex.Name).FindRowByEntry(ValuesOf(fromRow));

    /// <summary>
    /// The rows of <see cref="ToTable"/> that go with <paramref name="fromRow"/>, in its index's order. They are
    /// read as they are enumerated, each enumeration through a cursor of its own.
    /// </summary>
    /// <param name="fromRow">A row of <see cref="FromTable"/>, or the values of its joined columns by name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fromRow"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fromRow"/> has no entry for one of <see cref="FromColumnNames"/>.</exception>
    public IEnumerable<Row> FindRows(IReadOnlyDictionary<string, object?> fromRow)
    {
        object?[] values = ValuesOf(fromRow);
        return Rows();

        IEnumerable<Row> Rows()
        {
            foreach (var row in ToTable.CreateIndexCursor(ToIndex.Name).FindRowsByEntry(values))
                yield return row;
        }
    }

    /// <summary>
    /// Creates the joiner that goes the other way: from rows of <see cref="ToTable"/> to the rows of
    /// <see cref="FromTable"/> they go with. Jackcess's <c>createReverse</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="FromTable"/> has no index whose first columns are <see cref="FromColumnNames"/>.
    /// </exception>
    public Joiner CreateReverse() => new(ToTable, _toColumns, FromTable, _fromColumns);

    private object?[] ValuesOf(IReadOnlyDictionary<string, object?> fromRow)
    {
        if (fromRow is null) throw new ArgumentNullException(nameof(fromRow));
        var values = new object?[_fromColumns.Length];
        for (int i = 0; i < values.Length; i++)
            if (!TryGet(fromRow, _fromColumns[i], out values[i]))
                throw new ArgumentException(
                    $"The row has no value for '{_fromColumns[i]}', a column the join is on.", nameof(fromRow));

        // A date is joined on the number its row stores, as Access joins it, and not on the DateTime it was read
        // as, which is that number rounded: a row that was read from the table is asked for the number of each
        // such column it has not been given a value in since; one it has is joined on the value given. An extended
        // date joined to another is joined on its row's bytes, as Access's relationship joins it (#51): each layout
        // Access writes a date in is a key of its own (#64).
        if (fromRow is Row read && ReferenceEquals(read.Source, FromTable))
        {
            var dates = FromTable.Columns
                .Where(c => c.DataType is DataType.DateTime or DataType.DateTimeExtended && _fromColumns.Contains(c.Name) && !read.WasAssigned(c.Name))
                .ToList();
            if (dates.Count > 0 && FromTable.ReadRowAt(read.Pointer, dates) is { } stored)
                for (int i = 0; i < values.Length; i++)
                    if (values[i] is DateTime && stored.TryGetValue(_fromColumns[i], out var number)
                        && (number is double || number is StoredExtendedDate && ToIndex.Columns[i].Column.DataType == DataType.DateTimeExtended))
                        values[i] = number;
        }
        return values;
    }

    // A Row finds a column by its name or one Access takes as one with it; another dictionary is asked for the name, and
    // then its keys are looked through as the table's names are compared (ObjectNames.Find).
    private bool TryGet(IReadOnlyDictionary<string, object?> row, string column, out object? value)
    {
        if (row.TryGetValue(column, out value)) return true;
        if (row is Row) return false;
        var keys = row.Keys.ToList();
        int at = FromTable.Definition.Names.Find(keys, column);
        if (at >= 0) return row.TryGetValue(keys[at], out value);
        value = null;
        return false;
    }
}
