namespace JackcessDotNet;

/// <summary>
/// A cursor that also finds a row by the values it has in indexed columns: made by
/// <see cref="Table.CreateIndexCursor()"/> for the table's primary key, or by
/// <see cref="Table.CreateIndexCursor(string)"/> for the index of a name. To find a row by its primary key,
/// <see cref="Table.FindRowByPrimaryKey"/> does it in one call.
/// <para>
/// A row is found through an index when the index's keys are ones this library makes, whoever wrote the index.
/// Otherwise - a text index in a sort order whose keys it does not make, a column with no index -
/// the table is read from its first row and each row's values are compared. Either way a row is compared with
/// the values asked for before it is returned, and a null is never a match: a row with no value in a column is
/// not found by looking for null.
/// </para>
/// <para>
/// A lookup made through an index does not move the cursor. One that reads the table does: the cursor is left
/// on the row found, or after the last row when there is none.
/// </para>
/// </summary>
public sealed class IndexCursor : Cursor
{
    private readonly Table           _table;
    private readonly TableDefinition _definition;
    private readonly PageFile        _file;
    private readonly string?         _indexName;
    private readonly Index?          _index;
    private readonly bool            _isPrimaryKey;

    internal IndexCursor(Table table, PageFile file, TableDefinition definition, string? indexName)
        : base(table, file, definition)
    {
        _table        = table;
        _file         = file;
        _definition   = definition;
        _indexName    = indexName;

        // A name is an index's name and nothing else, as in Jackcess's Table.getIndex: not the name of a column
        // the primary key happens to be on, which another index may have for its own name. Only a cursor made
        // with no name is for the primary key, whatever that index is called.
        _index = indexName is null
            ? definition.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey)
            : definition.Indexes.FirstOrDefault(ix => string.Equals(ix.Name, indexName, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException(
                  $"Table '{definition.Name}' has no index named '{indexName}'.", nameof(indexName));
        _isPrimaryKey = indexName is null || _index!.IsPrimaryKey;
    }

    /// <summary>
    /// The index the cursor was made for: the table's primary key for <see cref="Table.CreateIndexCursor()"/> -
    /// null when the table has none, and the cursor then finds rows by reading the table - and the index of that
    /// name for <see cref="Table.CreateIndexCursor(string)"/>. Jackcess's <c>getIndex</c>.
    /// </summary>
    public Index? Index => _index;

    /// <summary>
    /// Finds the first row where <paramref name="columnName"/> equals <paramref name="value"/>, or null when
    /// there is none. Uses the primary key, or another index on the column alone, when its keys are ones this
    /// library makes - whichever index the cursor was made for; otherwise reads the table from its first row.
    /// <para>
    /// Text is matched exactly, a number with a number of another type that is equal to it, and a Double or a
    /// Single by its bits.
    /// </para>
    /// </summary>
    /// <param name="columnName">The name of the column to look in, whatever the case of its letters.</param>
    /// <param name="value">The value to look for. Null finds no row.</param>
    /// <returns>
    /// The row, with an entry for every column of the table, or null when no row has the value in that column,
    /// or the table has no column of that name.
    /// </returns>
    public Row? FindRow(string columnName, object? value)
    {
        // Fast path #1: the primary key, through IndexWriter's own walk of the tree (prefix-compressed
        // pages and any depth), when its keys are ones IndexKeys makes.
        bool path1Eligible =
            _isPrimaryKey
            && _definition.PrimaryKeyIndexPage > 0
            && _definition.PrimaryKeyColumnName is not null
            && string.Equals(columnName, _definition.PrimaryKeyColumnName, StringComparison.OrdinalIgnoreCase)
            && IndexWriter.PrimaryKeyColumns(_definition) is { Count: 1 } keyColumns
            && IndexKeys.CanSearch(keyColumns, new[] { value });
        if (path1Eligible)
        {
            var iw = new IndexWriter(_file, new PageAllocator(_file));
            foreach (int rowPtr in iw.EnumerateRowPointersForKey(_definition, value!))
            {
                // pageNum is the upper 24 bits (3-byte LE page) and rowNum the low byte.
                int pageNum = RowPointer.Page(rowPtr);
                int rowIdx  = RowPointer.Row(rowPtr);

                // Defensive: only attempt to materialise the row if pageNum points to
                // a real data page. Spurious "matches" against compressed Access leaves
                // can produce nonsense rowPtrs that would otherwise crash ReadRowAt.
                if (!IsLikelyDataPage(pageNum)) continue;

                Row? r;
                try { r = ReadRowAt(pageNum, rowIdx); }
                catch { continue; }
                if (r is null) continue;
                if (HasValue(r, columnName, out var stored) && ValuesEqual(stored, value))
                    return r;
            }
            // fall through
        }

        // Fast path #2: on a table read from disk, walk the real B-tree index whose
        // single column matches the requested column and whose key type we support.
        if (TryFindMatchingDiskIndex(columnName, value, out var diskIx))
        {
            var reader = new IndexReader(_file, diskIx!);
            foreach (int rowPtr in reader.FindRowPointers(value!))
            {
                int pageNum = RowPointer.Page(rowPtr);
                int rowIdx  = RowPointer.Row(rowPtr);
                Row? r = ReadRowAt(pageNum, rowIdx);
                if (r is null) continue;
                if (HasValue(r, columnName, out var stored) && ValuesEqual(stored, value))
                    return r;
            }
            return null;
        }

        // Slow path: forward table scan.
        MoveBeforeFirst();
        while (NextRow() is { } r)
        {
            if (HasValue(r, columnName, out var stored) && ValuesEqual(stored, value))
                return r;
        }
        return null;
    }

    /// <summary>
    /// Picks a disk-resident index that's usable for a single-column lookup on
    /// <paramref name="columnName"/>: must have exactly one column, that column must
    /// match by name, and its keys must be ones IndexReader makes as Access made them
    /// (a type it encodes; text sorted in an order it writes, and a value whose key a
    /// search can trust: <see cref="IndexKeys.CanSearch"/>). Returns false when no such
    /// index is present (caller falls back to scan).
    /// </summary>
    private bool TryFindMatchingDiskIndex(string columnName, object? value, out Index? matched)
    {
        foreach (var ix in _definition.Indexes)
        {
            if (ix.Columns.Count != 1) continue;
            if (ix.RootPageNumber <= 0) continue;
            if (!string.Equals(ix.Columns[0].Column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IndexReader.CanResolve(ix.Columns[0].Column) && IndexKeys.CanSearch(ix.Columns, new[] { value }))
            {
                matched = ix;
                return true;
            }
        }
        matched = null;
        return false;
    }

    /// <summary>
    /// Finds the first row whose values in the index's columns, in the index's order, equal
    /// <paramref name="entryValues"/>, or null when there is none: a lookup by a key of several columns. Uses
    /// the index when its keys are ones this library makes; otherwise reads the table from its first row.
    /// Jackcess's <c>findRowByEntry</c>.
    /// <para>
    /// A cursor made for an index by name (<see cref="Table.CreateIndexCursor(string)"/>) looks in that index
    /// when there are as many values as it has columns. Otherwise, and for a cursor made for the primary key
    /// (<see cref="Table.CreateIndexCursor()"/>), the values are matched against the first of the table's
    /// indexes (<see cref="Table.Indexes"/>) that has that many columns, and when the table has none, against
    /// the table's first columns in their order.
    /// </para>
    /// </summary>
    /// <param name="entryValues">The values to look for, in the order of the index's columns. A null among them finds no row.</param>
    /// <returns>The row, with an entry for every column of the table, or null when no row has those values.</returns>
    /// <exception cref="ArgumentException"><paramref name="entryValues"/> is null or has no values.</exception>
    public Row? FindRowByEntry(params object?[] entryValues)
    {
        if (entryValues is null || entryValues.Length == 0)
            throw new ArgumentException("Entry values are required.", nameof(entryValues));

        // Index path: prefer an explicitly-named index, else any index whose column
        // count and types match the supplied entry shape.
        Index? ix = SelectIndexForEntry(entryValues.Length);
        if (ix is not null)
        {
            var reader = new IndexReader(_file, ix);
            if (reader.CanResolveKey && IndexKeys.CanSearch(ix.Columns, entryValues))
            {
                foreach (int rowPtr in reader.FindRowPointersForEntry(entryValues!))
                {
                    int pageNum = RowPointer.Page(rowPtr);
                    int rowIdx  = RowPointer.Row(rowPtr);
                    Row? r = ReadRowAt(pageNum, rowIdx);
                    if (r is null) continue;
                    if (RowMatchesEntry(r, ix, entryValues))
                        return r;
                }
                return null;
            }
        }

        // Scan path: match the chosen index's columns, as its lookup would (an index whose keys
        // cannot be made: a text index in another sort order); with no index, positionally
        // against the table's columns in column-number order.
        MoveBeforeFirst();
        while (NextRow() is { } r)
        {
            if (ix is not null ? RowMatchesEntry(r, ix, entryValues) : MatchesLeadingColumns(r, entryValues))
                return r;
        }
        return null;
    }

    private bool MatchesLeadingColumns(Row row, object?[] entry)
    {
        for (int i = 0; i < entry.Length && i < _definition.Columns.Count; i++)
        {
            var col = _definition.Columns[i];
            if (!HasValue(row, col.Name, out var stored) || !ValuesEqual(stored, entry[i]))
                return false;
        }
        return true;
    }

    private Index? SelectIndexForEntry(int entryLen)
    {
        // Prefer the explicitly-named index when it fits.
        if (_indexName is not null)
        {
            foreach (var ix in _definition.Indexes)
                if (string.Equals(ix.Name, _indexName, StringComparison.OrdinalIgnoreCase)
                    && ix.Columns.Count == entryLen)
                    return ix;
        }
        // Else any index whose column count matches the supplied entry tuple.
        foreach (var ix in _definition.Indexes)
            if (ix.Columns.Count == entryLen && ix.RootPageNumber > 0)
                return ix;
        return null;
    }

    private static bool RowMatchesEntry(Row row, Index ix, object?[] entry)
    {
        for (int i = 0; i < ix.Columns.Count; i++)
        {
            string col = ix.Columns[i].Column.Name;
            if (!HasValue(row, col, out var stored) || !ValuesEqual(stored, entry[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Sanity-checks that <paramref name="pageNumber"/> points at a data page (type 0x01)
    /// within file bounds. Returns false on any error so spurious rowPtrs from
    /// compressed Access leaves can't crash <see cref="Cursor.ReadRowAt"/>.
    /// </summary>
    private bool IsLikelyDataPage(int pageNumber)
    {
        if (pageNumber <= 0) return false;
        try
        {
            byte[] page = _file.ReadPage(pageNumber);
            return page[0] == JetFormat.PageTypeData;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValuesEqual(object? stored, object? requested)
        => DataPageWriter.PrimaryKeysEqual(stored, requested);

    /// <summary>
    /// The row's value in <paramref name="column"/>, when it has one. A column that is null is never a match,
    /// whatever is looked for: a null is not found by looking for null.
    /// </summary>
    private static bool HasValue(Row row, string column, out object? value)
        => row.TryGetValue(column, out value) && value is not null;
}
