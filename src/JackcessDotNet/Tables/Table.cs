using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Represents an open user table and exposes row-insertion operations.
/// Instances are created by <see cref="Database.CreateTable"/> or
/// <see cref="Database.GetTable"/>.
/// </summary>
public sealed class Table
{
    private readonly PageFile       _file;
    private readonly PageAllocator  _allocator;
    private readonly TableDefinition _definition;
    private readonly DataPageWriter  _dataWriter;
    private readonly Database?       _owningDb;
    /// <summary>The AutoNumber columns, in column-number order: the order an insert fills them in.</summary>
    private readonly IReadOnlyList<Column> _autoNumberColumns;

    internal Table(PageFile file, PageAllocator allocator, TableDefinition definition,
                   Database? owningDb = null)
    {
        _file        = file        ?? throw new ArgumentNullException(nameof(file));
        _allocator   = allocator   ?? throw new ArgumentNullException(nameof(allocator));
        _definition  = definition  ?? throw new ArgumentNullException(nameof(definition));
        _dataWriter  = new DataPageWriter(file, allocator);
        _owningDb    = owningDb;
        // A complex column (attachments, multi-value fields) carries the flag too: its number is the key of the
        // rows that hold its values, counted separately. Complex columns are not written, so it is left alone.
        _autoNumberColumns = definition.Columns
            .Where(c => c.IsAutoNumber && c.DataType != DataType.Complex)
            .OrderBy(c => c.ColumnNumber)
            .ToArray();
    }

    /// <summary>The table's definition, for tests that look at its pages.</summary>
    internal TableDefinition Definition => _definition;

    // ── Public properties ─────────────────────────────────────────────────────

    public string                Name    => _definition.Name;
    public IReadOnlyList<Column> Columns => _definition.Columns;
    /// <summary>Indexes defined on this table (read from the on-disk TDEF).</summary>
    public IReadOnlyList<Index>  Indexes => _definition.Indexes;

    /// <summary>The number of rows in the table, as its definition counts them.</summary>
    public int RowCount => ByteUtil.GetInt(ReadDefinitionPage(), _file.Format.TdefOffsetNumRows);

    /// <summary>
    /// The last Long auto-number the table handed out, or was given: the next row inserted gets one more. It is
    /// kept in the table's definition, where Access keeps it, so it is the same number after the file is closed
    /// and opened again, and the one Access itself continues from. 0 for a table that has numbered no row.
    /// </summary>
    public int LastAutoNumber => ByteUtil.GetInt(ReadDefinitionPage(), _file.Format.TdefOffsetLastAutoNumber);

    /// <summary>
    /// Whether a row may bring its own value for an AutoNumber column of this table: <c>true</c> or <c>false</c>
    /// decides it for this table, <c>null</c> (the default) leaves it to
    /// <see cref="Database.AllowAutoNumberInsert"/>, where what each choice does is described.
    /// </summary>
    public bool? AllowAutoNumberInsert { get; set; }

    private bool AutoNumberInsertAllowed => AllowAutoNumberInsert ?? _owningDb?.AllowAutoNumberInsert ?? false;

    private byte[] ReadDefinitionPage() => _file.ReadPage(_definition.TdefPageNumber);

    private PropertyMaps? _propertiesCache;
    /// <summary>
    /// Property maps attached to this table in MSysObjects.LvProp. The
    /// <c>Default</c> map holds table-level properties (Description, Caption, etc.);
    /// per-column properties live in named maps keyed by column name.
    /// Lazily decoded on first access; null bytes → empty <see cref="PropertyMaps"/>.
    /// </summary>
    public PropertyMaps Properties
    {
        get
        {
            if (_propertiesCache is not null) return _propertiesCache;
            var catalog = new SystemCatalog(_file);
            byte[]? bytes = catalog.GetPropertyBytesForObject(_definition.TdefPageNumber);
            _propertiesCache = PropertyMapReader.Read(bytes, _file.Format);
            return _propertiesCache;
        }
    }

    // ── Cursor factories ──────────────────────────────────────────────────────

    /// <summary>Creates a sequential table-scan cursor (Jackcess-shape API).</summary>
    public Cursor      NewCursor()                               => NewCursorInternal();

    /// <summary>Creates an index cursor that supports FindRow / FindRowByPrimaryKey.</summary>
    public IndexCursor NewIndexCursor(string? indexName = null)  => NewIndexCursorInternal(indexName);

    internal Cursor      NewCursorInternal()                       => new Cursor(this, _file, _definition);
    internal IndexCursor NewIndexCursorInternal(string? indexName) => new IndexCursor(this, _file, _definition, indexName);

    // ── Row operations ────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes and inserts <paramref name="row"/> into the table.
    /// A new data page is allocated automatically if the current one is full.
    /// The total row count in the table's TDEF is updated on every insert.
    /// When the table has a primary-key column configured, a leaf entry is also
    /// appended to the PK index page so <see cref="IndexCursor.FindRow"/> can use it.
    /// <para>
    /// AutoNumber columns are filled in, as Access fills them, and <paramref name="row"/> holds the values
    /// afterwards: a Long column gets one more than <see cref="LastAutoNumber"/>, a Guid column a new GUID. A
    /// value the row already holds for such a column is ignored and replaced, unless
    /// <see cref="AllowAutoNumberInsert"/> lets a row bring its own. An insert that fails before the row is
    /// written leaves the counter and <paramref name="row"/> as they were, so the number is not lost.
    /// </para>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The table has an AutoNumber column that is neither Long nor Guid, or a primary key that cannot be kept.
    /// </exception>
    public void Insert(Row row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        EnsurePrimaryKeyCanBeKept();

        var given = AutoNumberValuesIn(row);
        int? lastAutoNumber;
        int  rowPtr;
        try
        {
            lastAutoNumber = FillAutoNumbers(row);
            _owningDb?.ValidateForeignKeysForInsert(this, row);
            rowPtr = _dataWriter.InsertRow(_definition, row);
        }
        catch when (given is not null)
        {
            // The row was not written: it goes back to the caller as it came. The counter was never moved - it
            // is written with the row count, below - so the next insert is given this row's number.
            foreach (var (name, had, value) in given)
            {
                if (had) row[name] = value;
                else     row.Remove(name);
            }
            throw;
        }

        // The counter and the row count go to the definition together, once the row is on its page and before its
        // key: a row in the file is always one the counter has passed, whatever becomes of the key.
        _dataWriter.RecordInsert(_definition.TdefPageNumber, lastAutoNumber);
        MaybeAddPrimaryKeyIndexEntry(row, rowPtr);
    }

    /// <summary>What <paramref name="row"/> holds for each AutoNumber column; null for a table with none.</summary>
    private List<(string Name, bool Had, object? Value)>? AutoNumberValuesIn(Row row)
    {
        if (_autoNumberColumns.Count == 0) return null;
        var values = new List<(string, bool, object?)>(_autoNumberColumns.Count);
        foreach (var column in _autoNumberColumns)
            values.Add((column.Name, row.TryGetValue(column.Name, out var value), value));
        return values;
    }

    /// <summary>
    /// Puts each AutoNumber column's value in <paramref name="row"/> and returns what the table's Long counter
    /// becomes once the row is written (null when the table has no Long AutoNumber). Nothing is written here.
    /// </summary>
    private int? FillAutoNumbers(Row row)
    {
        if (_autoNumberColumns.Count == 0) return null;

        foreach (var column in _autoNumberColumns)
            if (column.DataType is not (DataType.Long or DataType.Guid))
                throw new NotSupportedException(
                    $"{Name}.{column.Name} is an AutoNumber of type {column.DataType}; only Long and Guid " +
                    "AutoNumbers are generated. The table is left as it was.");

        bool allowed = AutoNumberInsertAllowed;
        int? last = null;
        foreach (var column in _autoNumberColumns)
        {
            object? given = allowed && row.TryGetValue(column.Name, out var value) ? value : null;
            if (column.DataType == DataType.Guid)
            {
                row[column.Name] = given switch
                {
                    null      => Guid.NewGuid(),
                    Guid guid => guid,
                    _         => Guid.Parse(Convert.ToString(given, System.Globalization.CultureInfo.InvariantCulture)!),
                };
                continue;
            }

            last ??= LastAutoNumber;
            if (given is null)
            {
                // Past int.MaxValue the counter wraps to the negative numbers.
                last = unchecked(last.Value + 1);
                row[column.Name] = last.Value;
            }
            else
            {
                int number = Convert.ToInt32(given, System.Globalization.CultureInfo.InvariantCulture);
                row[column.Name] = number;
                if (number > last) last = number;
            }
        }
        return last;
    }

    /// <summary>
    /// Checks <paramref name="row"/> against the table's columns without writing it: throws
    /// <see cref="ColumnValueTooLongException"/> for a Text value longer than its column, as <see cref="Insert"/>
    /// would. A caller writing many rows can check them all first, so a refused value stops the batch before any of
    /// it is in the file.
    /// </summary>
    public void Validate(Row row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        new RowEncoder(_file.Format, _definition.Columns).Validate(row);
    }

    /// <summary>
    /// Updates the row with the given primary-key value.
    /// Old LVAL chunks are freed and their space is reclaimed before the new row is written.
    /// The row's primary-key entry moves to the rewritten row, as Access keeps an index.
    /// An AutoNumber column keeps the value the row has: one in <paramref name="newValues"/> is ignored, unless
    /// <see cref="AllowAutoNumberInsert"/> lets a row bring its own, when a Long one past the table's counter
    /// moves the counter up to it.
    /// </summary>
    public void UpdateByPrimaryKey(object primaryKeyValue, Row newValues)
    {
        EnsurePrimaryKeyCanBeKept();

        int? highestGiven = null;
        if (newValues is not null && _autoNumberColumns.Any(c => newValues.ContainsKey(c.Name)))
        {
            if (AutoNumberInsertAllowed)
                highestGiven = _autoNumberColumns
                    .Where(c => c.DataType == DataType.Long && newValues.TryGetValue(c.Name, out var v) && v is not null)
                    .Select(c => (int?)Convert.ToInt32(newValues[c.Name], System.Globalization.CultureInfo.InvariantCulture))
                    .Max();
            else
                newValues = new Row(newValues.Where(v => !_autoNumberColumns.Any(c =>
                    c.Name.Equals(v.Key, StringComparison.OrdinalIgnoreCase))));
        }

        // The key the rewritten row is found under: the one newValues brings for the primary-key column, when it
        // brings a different one, and otherwise the key it had. A key another row already has is refused here,
        // before the row is rewritten.
        bool indexed = _definition.PrimaryKeyColumnName is not null && _definition.PrimaryKeyIndexPage > 0;
        object newKey = primaryKeyValue;
        if (indexed && newValues is not null
            && newValues.TryGetValue(_definition.PrimaryKeyColumnName!, out var replacement) && replacement is not null)
        {
            var keyColumns = IndexWriter.PrimaryKeyColumns(_definition);
            if (keyColumns.Count == 1
                && !IndexKeys.Encode(keyColumns, new[] { replacement })
                        .AsSpan().SequenceEqual(IndexKeys.Encode(keyColumns, new[] { primaryKeyValue })))
            {
                if (new IndexWriter(_file, _allocator).FindRowByPrimaryKey(_definition, replacement) is not null)
                    throw new InvalidOperationException(
                        $"{Name} already has a row whose primary key is {replacement}. The table is left as it was.");
                newKey = replacement;
            }
        }

        var (oldRowPtr, newRowPtr) = _dataWriter.UpdateRowByPrimaryKeyMoving(_definition, primaryKeyValue, newValues!);
        if (highestGiven is { } given)
            _dataWriter.RaiseLastAutoNumber(_definition.TdefPageNumber, given);

        if (indexed)
        {
            var writer = new IndexWriter(_file, _allocator);
            writer.RemovePrimaryKey(_definition, new[] { primaryKeyValue }, oldRowPtr);
            writer.InsertPrimaryKey(_definition, newKey, newRowPtr);
        }
    }

    /// <summary>
    /// Refuses, before anything changes, to change a table whose primary key this
    /// library cannot keep: a key on a column whose entries it cannot write as Access
    /// writes them. A row changed with its key left behind is one Access counts and
    /// finds wrongly.
    /// </summary>
    private void EnsurePrimaryKeyCanBeKept()
    {
        if (_definition.PrimaryKeyIndexPage == 0) return;
        foreach (var column in IndexWriter.PrimaryKeyColumns(_definition))
            if (!IndexKeys.CanEncode(column.Column))
                throw new NotSupportedException(
                    $"{Name}'s primary key cannot be kept: {IndexKeys.WhyNot(column.Column)}. The table is left as it was.");
    }

    private void MaybeAddPrimaryKeyIndexEntry(Row row, int rowPtr)
    {
        if (_definition.PrimaryKeyIndexPage == 0) return;

        var pkCols = _definition.EffectivePrimaryKeyColumns;
        if (pkCols.Count == 0) return;

        var writer = new IndexWriter(_file, _allocator);
        if (pkCols.Count == 1)
        {
            // Single-column path: missing-or-null skips the index entry, matching
            // the existing (pre-composite) behaviour exactly.
            if (!row.TryGetValue(pkCols[0], out var pkValue) || pkValue is null) return;
            writer.InsertPrimaryKey(_definition, pkValue, rowPtr);
        }
        else
        {
            // Composite path: every component must be present + non-null.
            var values = new object?[pkCols.Count];
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (!row.TryGetValue(pkCols[i], out var v) || v is null) return;
                values[i] = v;
            }
            writer.InsertPrimaryKey(_definition, values, rowPtr);
        }
    }

    /// <summary>
    /// Deletes the first row where <paramref name="columnName"/> equals <paramref name="value"/>.
    /// Any LVAL page chains held by the deleted row are freed and their space is reclaimed.
    /// The TDEF row count is decremented by one.
    /// </summary>
    public void DeleteRow(string columnName, object value)
    {
        EnsurePrimaryKeyCanBeKept();
        var deleted = _dataWriter.DeleteFirstMatch(_definition, columnName, value);
        _dataWriter.IncrementTdefRowCount(_definition.TdefPageNumber, -1);

        // Its primary-key entry goes with it: Access counts and finds rows through the key.
        if (deleted is { } row && _definition.PrimaryKeyIndexPage > 0 && row.PrimaryKey.All(v => v is not null))
            new IndexWriter(_file, _allocator).RemovePrimaryKey(_definition, row.PrimaryKey, row.RowPtr);
    }

    /// <summary>
    /// Reads and decodes every non-deleted row in the table and returns them in
    /// insertion order.  Follows LVAL page chains for Memo/OLE columns.
    /// </summary>
    public IReadOnlyList<Row> ReadAllRows()
    {
        var format     = _file.Format;
        var lvalReader = _definition.LvalColumnUmapPages.Count > 0
            ? new LvalReader(_file)
            : null;
        var decoder = new RowDecoder(format, _definition.Columns, lvalReader);

        byte[] umapPage  = _file.ReadPage(_definition.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, _definition.OwnedPagesRow, format, _file);

        var result = new List<Row>();
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
                    : (ByteUtil.GetUShort(dp,
                           format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                var row = new Row();
                foreach (var col in _definition.Columns)
                {
                    object? val = decoder.Decode(rowBytes, col);
                    if (val is not null)
                        row[col.Name] = val;
                }
                result.Add(row);
            }
        }
        return result;
    }
}
