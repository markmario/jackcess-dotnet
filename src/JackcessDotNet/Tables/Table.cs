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
    private readonly DataPageWriter  _dataWriter;
    private readonly Database?       _owningDb;

    // The table as its definition was last read, and how many times the definition had been changed by then
    // (PageFile.DefinitionVersion). Everything below reaches these through the three properties after them, which
    // read the definition again once it has been changed - by this Table or by another open on the same table -
    // so that a row is never written by the columns, nor kept in the indexes, the table had before.
    private TableDefinition       _readDefinition = null!;
    private TableIndexes          _readIndexes = null!;
    private IReadOnlyList<Column> _readAutoNumberColumns = null!;
    private int                   _readAtVersion;

    private TableDefinition _definition { get { Refresh(); return _readDefinition; } }
    private TableIndexes    _indexes    { get { Refresh(); return _readIndexes; } }
    /// <summary>The AutoNumber columns, in column-number order: the order an insert fills them in.</summary>
    private IReadOnlyList<Column> _autoNumberColumns { get { Refresh(); return _readAutoNumberColumns; } }

    internal Table(PageFile file, PageAllocator allocator, TableDefinition definition,
                   Database? owningDb = null)
    {
        _file        = file        ?? throw new ArgumentNullException(nameof(file));
        _allocator   = allocator   ?? throw new ArgumentNullException(nameof(allocator));
        _dataWriter  = new DataPageWriter(file, allocator);
        _owningDb    = owningDb;
        Use(definition ?? throw new ArgumentNullException(nameof(definition)), file.DefinitionVersion(definition.TdefPageNumber));
    }

    private void Use(TableDefinition definition, int version)
    {
        _readDefinition = definition;
        _readIndexes    = new TableIndexes(_file, _allocator, definition);
        // A complex column (attachments, multi-value fields) carries the flag too: its number is the key of the
        // rows that hold its values, counted separately. Complex columns are not written, so it is left alone.
        _readAutoNumberColumns = definition.Columns
            .Where(c => c.IsAutoNumber && c.DataType != DataType.Complex)
            .OrderBy(c => c.ColumnNumber)
            .ToArray();
        _readAtVersion = version;
        _writability   = null;
    }

    // Reads the definition again when it has been changed since it was read.
    private void Refresh()
    {
        int version = _file.DefinitionVersion(_readDefinition.TdefPageNumber);
        if (version != _readAtVersion)
            Use(Database.ReadDefinition(_file, _readDefinition.Name, _readDefinition.TdefPageNumber), version);
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

    /// <summary>
    /// Why the table takes no new row, whatever the row holds, or <c>null</c> when it takes rows: the message of
    /// the <see cref="NotSupportedException"/> that <see cref="Insert"/> and <see cref="Validate"/> throw for it,
    /// which they throw by asking this. The table has an index whose keys this library does not write as Access
    /// does, or indexes it did not read whole; an AutoNumber column that is neither Long nor Guid; or a definition
    /// rows cannot be written by - a calculated column, columns that run past its first page, or two columns in
    /// one place. Such a table is still read.
    /// <para>
    /// A caller about to write to several tables can ask each first, and so write to none when one of them
    /// would refuse. Asking reads the table's definition as it was opened and changes nothing in the file. A table
    /// that takes rows can still refuse a row for what the row holds: <see cref="Validate"/> checks one.
    /// </para>
    /// </summary>
    public string? InsertRefusal
        => _file.ReadOnlyRefusal
        ?? _indexes.WhyNotKept()
        ?? WhyNotNumbered()
        ?? (_writability ??= new RowEncoder(_file.Format, _definition)).WhyNotWritable();

    // What says whether rows can be written by the table's definition, worked out once.
    private RowEncoder? _writability;

    /// <summary>The one refusal of a table that takes no row: what <see cref="InsertRefusal"/> answers, thrown.</summary>
    private void EnsureTakesRows()
    {
        if (InsertRefusal is { } why) throw new NotSupportedException(why);
    }

    /// <summary>Why the table's AutoNumber columns cannot be filled, or null when they can: only Long and Guid ones are.</summary>
    private string? WhyNotNumbered()
        => _autoNumberColumns.FirstOrDefault(c => c.DataType is not (DataType.Long or DataType.Guid)) is { } column
            ? $"{Name}.{column.Name} is an AutoNumber of type {column.DataType}; only Long and Guid " +
              "AutoNumbers are generated. The table is left as it was."
            : null;

    /// <summary>
    /// Moves the Long auto-number counter up to <paramref name="value"/> when it is behind it, so no row numbered
    /// from now on is given a number up to it. An import calls it with the highest number its rows bring, before
    /// it writes any of them.
    /// </summary>
    internal void RaiseLastAutoNumber(int value)
    {
        _file.EnsureWritable();
        _dataWriter.RaiseLastAutoNumber(_definition.TdefPageNumber, value);
    }

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
    /// The row gains its entry in each of the table's indexes - its primary key and every other - so Access
    /// finds it through them and <see cref="IndexCursor.FindRow"/> can use them.
    /// <para>
    /// AutoNumber columns are filled in, as Access fills them, and <paramref name="row"/> holds the values
    /// afterwards: a Long column gets one more than <see cref="LastAutoNumber"/>, a Guid column a new GUID. A
    /// value the row already holds for such a column is ignored and replaced, unless
    /// <see cref="AllowAutoNumberInsert"/> lets a row bring its own. An insert that fails before the row is
    /// written leaves the counter and <paramref name="row"/> as they were, so the number is not lost.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The row, with its AutoNumber values filled in, would be a duplicate in the primary key or another unique
    /// index, or has a null in a column of the primary key or of an index that requires its values. Nothing of
    /// the row is written, and the counter and <paramref name="row"/> are as they were.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The table takes no row (<see cref="InsertRefusal"/>: among the reasons, that it is a table of an Access 97
    /// file, which is only read), or the row has a value in an indexed text column that no key is made for.
    /// Nothing is written, and <paramref name="row"/> is as it was.
    /// </exception>
    public void Insert(Row row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        EnsureTakesRows();

        var given = AutoNumberValuesIn(row);
        int? lastAutoNumber;
        int  rowPtr;
        IReadOnlyList<TableIndexes.Entry> entries;
        IndexWriter? looked;
        try
        {
            lastAutoNumber = FillAutoNumbers(row);

            // The row as it will be written - its AutoNumber values in place, DBNull no value, a Yes/No the
            // bool it stands for - read once, for the row's bytes and its index entries alike.
            var written = RowValues.AsWritten(row, _definition.Columns);
            _owningDb?.ForeignKeysFor(this)?.CheckInsert(this, written);

            // Everything an index can refuse the row for, before any of it is written.
            entries = _indexes.EntriesFor(written, enforce: true);
            looked  = _indexes.EnsureNoDuplicates(entries, forEntry: true);

            rowPtr = _dataWriter.InsertRow(_definition, written);
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
        // index entries: a row in the file is always one the counter has passed, whatever becomes of the entries.
        _dataWriter.RecordInsert(_definition.TdefPageNumber, lastAutoNumber);
        _indexes.Add(entries, rowPtr, looked);
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
    /// Every AutoNumber column is a Long or a Guid: a table with another is refused before this
    /// (<see cref="EnsureTakesRows"/>).
    /// </summary>
    private int? FillAutoNumbers(Row row)
    {
        if (_autoNumberColumns.Count == 0) return null;

        bool allowed = AutoNumberInsertAllowed;
        int? last = null;
        foreach (var column in _autoNumberColumns)
        {
            // No value to bring - null, or DBNull as a DataRow holds an empty cell (RowValues) - and the table
            // numbers the row.
            object? given = allowed && row.TryGetValue(column.Name, out var value) && !RowValues.IsNull(value) ? value : null;
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
    /// Checks <paramref name="row"/> against the table's columns and indexes without writing it: throws what
    /// <see cref="Insert"/> would - <see cref="ColumnValueTooLongException"/> for a Text value longer than its
    /// column, <see cref="NotSupportedException"/> for a table that takes no row (<see cref="InsertRefusal"/>),
    /// <see cref="InvalidOperationException"/> for a null a primary key or required index does not take or a
    /// duplicate of a row already in a unique index. The row is checked as it would be written, with the
    /// AutoNumber values the next insert would give it; <paramref name="row"/> itself is not changed. A caller
    /// writing many rows can check them all first, so a refused row stops the batch before any of it is in the
    /// file. Rows not yet written are not compared with one another: a duplicate within the batch is refused when
    /// it is inserted.
    /// </summary>
    public void Validate(Row row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        EnsureTakesRows();
        new RowEncoder(_file.Format, _definition).Validate(row);

        var asWritten = row;
        if (_autoNumberColumns.Count > 0)
        {
            asWritten = new Row(row);
            FillAutoNumbers(asWritten);
        }
        asWritten = RowValues.AsWritten(asWritten, _definition.Columns);
        _indexes.EnsureNoDuplicates(_indexes.EntriesFor(asWritten, enforce: true));
    }

    /// <summary>
    /// Updates the row with the given primary-key value.
    /// Old LVAL chunks are freed and their space is reclaimed before the new row is written.
    /// The row's entry in each of the table's indexes moves to the rewritten row, under its new values - a
    /// changed primary key among them - as Access keeps an index.
    /// An AutoNumber column keeps the value the row has: one in <paramref name="newValues"/> is ignored, unless
    /// <see cref="AllowAutoNumberInsert"/> lets a row bring its own, when a Long one past the table's counter
    /// moves the counter up to it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The updated row would be a duplicate of another in the primary key or another unique index, or has a null
    /// in a column of the primary key or of an index that requires its values. The row is left as it was.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The table has an index whose keys this library does not write as Access does, or is a table of an Access
    /// 97 file, which is only read. Nothing is written.
    /// </exception>
    public void UpdateByPrimaryKey(object primaryKeyValue, Row newValues)
    {
        _file.EnsureWritable();
        _indexes.EnsureCanBeKept();

        // The new values as they will be written: DBNull no value, a Yes/No the bool it stands for.
        if (newValues is not null)
            newValues = RowValues.AsWritten(newValues, _definition.Columns);

        int? highestGiven = null;
        if (newValues is not null && _autoNumberColumns.Any(c => newValues.ContainsKey(c.Name)))
        {
            // A null, or DBNull, is not a number to bring: the column keeps the one it has, whether or not a row
            // may bring its own. Access holds no AutoNumber as null.
            bool allowed = AutoNumberInsertAllowed;
            newValues = new Row(newValues.Where(v => (allowed && !RowValues.IsNull(v.Value)) || !_autoNumberColumns.Any(c =>
                c.Name.Equals(v.Key, StringComparison.OrdinalIgnoreCase))));
            if (allowed)
                highestGiven = _autoNumberColumns
                    .Where(c => c.DataType == DataType.Long && newValues.ContainsKey(c.Name))
                    .Select(c => (int?)Convert.ToInt32(newValues[c.Name], System.Globalization.CultureInfo.InvariantCulture))
                    .Max();
        }

        // With foreign keys enforced, a table in a relationship is changed through the enforcer: the row, and the
        // rows a cascade reaches, or nothing.
        if (_definition.PrimaryKeyColumnName is not null && _owningDb?.ForeignKeysFor(this) is { } foreignKeys)
        {
            foreignKeys.Update(this, primaryKeyValue, newValues ?? throw new ArgumentNullException(nameof(newValues)));
            if (highestGiven is { } brought)
                _dataWriter.RaiseLastAutoNumber(_definition.TdefPageNumber, brought);
            return;
        }

        var entries = new EntryMove(_indexes, refuseDuplicates: true);
        var (oldRowPtr, newRowPtr) = _dataWriter.UpdateRowByPrimaryKeyMoving(_definition, primaryKeyValue, newValues!, entries.BeforeChange);
        if (highestGiven is { } given)
            _dataWriter.RaiseLastAutoNumber(_definition.TdefPageNumber, given);
        entries.Follow(oldRowPtr, newRowPtr);
    }

    /// <summary>
    /// The index side of rewriting a row, the same however the row was found. What the indexes hold for the row as
    /// it was and as it will be - its primary key, changed or not, and every other index - is found, and a null
    /// refused, once the new row is known and before anything is rewritten; then, the row having moved, every
    /// entry it had goes and it gains one at its new place.
    /// </summary>
    private sealed class EntryMove
    {
        private readonly TableIndexes _indexes;
        private readonly bool _refuseDuplicates;
        private IReadOnlyList<TableIndexes.Entry> _old = Array.Empty<TableIndexes.Entry>();
        private IReadOnlyList<TableIndexes.Entry> _new = Array.Empty<TableIndexes.Entry>();

        /// <param name="refuseDuplicates">
        /// Whether a duplicate in a unique index is looked for in the file. The foreign-key enforcer has looked
        /// already, among the rows as its whole change leaves them: the file, part-way through that change, can
        /// hold for a moment a key that a row about to be rewritten is giving up.
        /// </param>
        internal EntryMove(TableIndexes indexes, bool refuseDuplicates)
        {
            _indexes = indexes;
            _refuseDuplicates = refuseDuplicates;
        }

        internal void BeforeChange(Row oldRow, Row newRow, int oldPointer)
        {
            _old = _indexes.EntriesFor(oldRow, enforce: false);
            _new = _indexes.EntriesFor(newRow, enforce: true);
            if (_refuseDuplicates) _indexes.EnsureNoDuplicates(_new, replacedRowPointer: oldPointer);
        }

        internal void Follow(int oldPointer, int newPointer)
        {
            _indexes.Remove(_old, oldPointer);
            _indexes.Add(_new, newPointer);
        }
    }

    // ── Rows the foreign-key enforcer has found ───────────────────────────────

    /// <summary>
    /// Rewrites the row at <paramref name="rowPtr"/> with <paramref name="changes"/> laid over it, its entry in
    /// every index following it, as <see cref="UpdateByPrimaryKey"/> does for the row it finds. Duplicates are
    /// the enforcer's to have refused.
    /// </summary>
    internal void RewriteRowAt(int rowPtr, Row changes)
    {
        _file.EnsureWritable();
        var entries = new EntryMove(_indexes, refuseDuplicates: false);
        int newRowPtr = _dataWriter.RewriteRowAt(_definition, rowPtr, changes, entries.BeforeChange);
        entries.Follow(rowPtr, newRowPtr);
    }

    /// <summary>Deletes the row at <paramref name="rowPtr"/>, as <see cref="DeleteRow"/> does the row it finds.</summary>
    internal void DeleteRowAt(int rowPtr)
    {
        _file.EnsureWritable();
        RecordDeleted(rowPtr, _dataWriter.DeleteRowAt(_definition, rowPtr));
    }

    /// <summary>The values of <paramref name="columns"/> in the row at <paramref name="rowPtr"/>, or null when no row is there.</summary>
    internal Row? ReadRowAt(int rowPtr, IReadOnlyList<Column> columns) => _dataWriter.ReadRowAt(_definition, rowPtr, columns);

    /// <summary>The file the table is in, for a change of many rows to be taken back whole.</summary>
    internal PageFile File => _file;

    /// <summary>The table's indexes: what a row's entries in them are, and the rows an index holds for a key.</summary>
    internal TableIndexes IndexEntries => _indexes;

    /// <summary>
    /// Deletes the first row where <paramref name="columnName"/> equals <paramref name="value"/>.
    /// Any LVAL page chains held by the deleted row are freed and their space is reclaimed.
    /// The TDEF row count is decremented by one, and the row's entries leave the table's indexes.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The table has an index whose keys this library does not write as Access does, or is a table of an Access
    /// 97 file, which is only read. Nothing is deleted.
    /// </exception>
    public void DeleteRow(string columnName, object value)
    {
        _file.EnsureWritable();
        _indexes.EnsureCanBeKept();
        if (_owningDb?.ForeignKeysFor(this) is { } foreignKeys)
        {
            foreignKeys.Delete(this, columnName ?? throw new ArgumentNullException(nameof(columnName)), value);
            return;
        }

        // A row is always reported: with none to delete, DeleteFirstMatch throws.
        if (_dataWriter.DeleteFirstMatch(_definition, columnName, value) is { } row)
            RecordDeleted(row.RowPtr, row.Values);
    }

    /// <summary>What follows a row's deletion, however it was found: one row fewer, and its index entries gone.</summary>
    private void RecordDeleted(int rowPtr, Row values)
    {
        _dataWriter.IncrementTdefRowCount(_definition.TdefPageNumber, -1);

        // Its index entries go with it: Access counts and finds rows through them.
        _indexes.Remove(_indexes.EntriesFor(values, enforce: false), rowPtr);
    }

    /// <summary>
    /// Lists the Decimal (Numeric) values in the table that read with more digits than their column's precision
    /// allows. Nothing is changed, in the file or in what a read returns: this is a way to look for values a
    /// version of this library before qcatco/exportmdb.interop#61 wrote.
    /// <para>
    /// Those versions wrote a Decimal's sixteen bytes in an order Access does not use, so every value they wrote
    /// reads as a different number - a 1 as 16,777,216 - in Access and, now, here. Nothing in the file says who
    /// wrote a value. What can be seen is a value its column could not have been given: Access refuses a number
    /// with more digits than the column's precision, and so does this library, so one that reads that way was
    /// almost certainly written in the earlier order, and <see cref="DecimalBeyondPrecision.ValueInEarlierByteOrder"/>
    /// is what it was meant to be.
    /// </para>
    /// <para>
    /// It finds some such values, not all, and how many depends on the precision. A number under 4,294,967,296
    /// (before its decimal point is placed) written in the earlier order reads as one of at most ten digits, at
    /// least eight when it is not a multiple of 256. So in a column of precision 7 or less every such number that
    /// is not a multiple of 256 is listed; of precision 8 or 9, some are; and of precision 10 or more - 18 is what
    /// Access and this library give a column by default - none is, and only numbers of ten digits or more can be.
    /// A table with nothing listed may still hold values written in the earlier order.
    /// </para>
    /// <para>
    /// The table is read as it is asked for, a row at a time. A calculated column is not looked at.
    /// </para>
    /// </summary>
    public IEnumerable<DecimalBeyondPrecision> FindDecimalsBeyondPrecision()
    {
        var decimals = _definition.Columns
            .Where(c => c.DataType == DataType.Numeric && !c.IsCalculated && c.Precision > 0)
            .ToList();
        if (decimals.Count == 0) yield break;

        var format  = _file.Format;
        var decoder = new RowDecoder(format, _definition.Columns);
        var keys    = _definition.EffectivePrimaryKeyColumns
            .Select(name => _definition.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OfType<Column>()
            .ToList();

        // The rows in the order ReadAllRows returns them, which RowIndex counts in.
        int rowIndex = -1;
        foreach (int pageNum in UsageMap.GetOwnedPages(_file.ReadPage(_definition.UmapPageNumber), _definition.OwnedPagesRow, format, _file))
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, format.OffsetDataNumRows);
            for (int r = 0; r < rowCount; r++)
            {
                if (DataRows.Read(_file, dp, r) is not { } rowBytes) continue;
                rowIndex++;

                foreach (var column in decimals)
                {
                    byte[]? stored = decoder.RawFixedValue(rowBytes, column) ?? decoder.RawVariableValue(rowBytes, column);
                    if (stored is null || stored.Length < NumericCodec.Size) continue;

                    var unscaled = NumericCodec.Unscaled(stored, 0);
                    if (unscaled < System.Numerics.BigInteger.Pow(10, column.Precision)) continue;

                    var key = new Row();
                    foreach (var keyColumn in keys)
                        key[keyColumn.Name] = decoder.Decode(rowBytes, keyColumn);
                    bool negative = NumericCodec.IsNegative(stored, 0);
                    yield return new DecimalBeyondPrecision(Name, column, rowIndex, key,
                        NumericCodec.Decode(stored, 0, column.Scale),
                        NumericCodec.ToDecimal(NumericCodec.UnscaledInEarlierByteOrder(stored, 0), negative, column.Scale));
                }
            }
        }
    }

    // ── Changing the table ───────────────────────────────────────────────────

    /// <summary>
    /// Adds <paramref name="column"/> to the table, after its other columns, as Access's <c>ALTER TABLE ... ADD
    /// COLUMN</c> and Jackcess's <c>ColumnBuilder.addToTable</c> do. The rows already in the table are not
    /// rewritten: each reads the new column as null (a Yes/No column as No), as Access shows them, until it is
    /// updated. The table may be one Access made or one this library made.
    /// <para>
    /// Only the table's definition changes, and it changes whole or not at all: a refused column leaves the file
    /// byte for byte as it was. This <see cref="Table"/>, and any other open on the same table, has the column
    /// from then on.
    /// </para>
    /// </summary>
    /// <param name="column">The column to add (use <see cref="ColumnBuilder"/>). It is left as it was given.</param>
    /// <returns>The table's new column.</returns>
    /// <exception cref="ArgumentException">The table already has a column of that name, or 255 columns.</exception>
    /// <exception cref="NotSupportedException">
    /// The file is an Access 97 one; the column is of a type this library does not write (a complex or calculated
    /// one), or is an AutoNumber and the table has rows or an AutoNumber already; or the table's definition runs,
    /// or would run, over more than one page, which is all this library reads of one - so a table is never left
    /// that it could no longer write rows to. Nothing is changed.
    /// </exception>
    public Column AddColumn(Column column)
    {
        if (column is null) throw new ArgumentNullException(nameof(column));
        string name = SchemaChange.AddColumn(Owner(), _file, _definition, column);
        return Columns.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds <paramref name="index"/> to the table, built over the rows already in it, as Access's <c>CREATE
    /// INDEX</c> and Jackcess's <c>IndexBuilder.addToTable</c> do. From then on it is kept on every insert, update
    /// and delete, like the table's other indexes, and a unique one refuses a duplicate. An index on the columns,
    /// in the order and with the flags, of one the table already has shares that index's tree, as in Access.
    /// <para>
    /// The rows are read and every key made before anything is written, and what is written is taken back if
    /// writing it fails: a refused index leaves the file byte for byte as it was. This <see cref="Table"/>, and
    /// any other open on the same table, keeps the index from then on.
    /// </para>
    /// </summary>
    /// <returns>The table's new index.</returns>
    /// <exception cref="ArgumentException">
    /// The table already has an index of that name, or 32 indexes; or the index sorts descending a column that
    /// is not one of its own.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A column the index names is not in the table; two rows of the table have the same values in a unique
    /// index; or a row has a null in an index that requires its values. Nothing is changed.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is an Access 97 one; the index is on a column whose keys this library does not write as Access
    /// does (a Decimal, Binary, OLE or Complex column, or text in a sort order other than General - Legacy and
    /// General), or a row holds a value no key is made for; or the table's definition runs, or would run, over
    /// more than one page. Nothing is changed.
    /// </exception>
    public Index AddIndex(IndexDefinition index)
    {
        if (index is null) throw new ArgumentNullException(nameof(index));
        SchemaChange.AddIndex(Owner(), _file, _definition, index);
        return Indexes.First(ix => string.Equals(ix.Name, index.Name, StringComparison.OrdinalIgnoreCase));
    }

    private Database Owner()
        => _owningDb ?? throw new InvalidOperationException($"Table '{Name}' was not opened from a database, so it cannot be changed.");

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
                // Deleted rows are passed; an overflow row is read where its pointer leads.
                if (DataRows.Read(_file, dp, r) is not { } rowBytes) continue;

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
