using JackcessDotNet.Diagnostics;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// A table of an open database: its columns and indexes, its rows to read, and the operations that insert,
/// update and delete them. Got from <see cref="Database.GetTable"/> or
/// <see cref="Database.CreateTable(string, IEnumerable{ColumnDefinition}, string[])"/>; a
/// table is one object for as long as its database is open.
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

    /// <param name="file">The file the table is in.</param>
    /// <param name="allocator">What appends pages to the file.</param>
    /// <param name="definition">The table's definition, as its definition page holds it.</param>
    /// <param name="owningDb">The database the table was opened from, which changes to its schema and properties go through.</param>
    /// <param name="properties">
    /// The table's properties and its columns', as the catalog has them; null to read them from the catalog when
    /// they are first asked for.
    /// </param>
    internal Table(PageFile file, PageAllocator allocator, TableDefinition definition,
                   Database? owningDb = null, PropertyMaps? properties = null)
    {
        _file        = file        ?? throw new ArgumentNullException(nameof(file));
        _allocator   = allocator   ?? throw new ArgumentNullException(nameof(allocator));
        _dataWriter  = new DataPageWriter(file, allocator);
        _owningDb    = owningDb;
        Use(definition ?? throw new ArgumentNullException(nameof(definition)), file.DefinitionVersion(definition.TdefPageNumber));

        if (properties is not null) _propertiesCache = Attach(properties);
    }

    private void Use(TableDefinition definition, int version)
    {
        _readDefinition = definition;
        _readIndexes    = new TableIndexes(_file, _allocator, definition);
        // A complex column (attachments, multi-value fields) carries the flag too: its number is the key of the
        // rows that hold its values, counted separately. Complex columns are not written, so it is left alone.
        _readAutoNumberColumns = definition.Columns
            .Where(c => c.IsAutoNumber && c.DataType != DataType.Complex)
            .OrderBy(c => c.Ordinal)
            .ToArray();
        _readAtVersion = version;
        _writability   = null;
        _rules         = null;
        // Each column reads its properties through its table.
        foreach (var column in definition.Columns) column.Owner = this;
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

    /// <summary>The table's name, as the database's catalog spells it.</summary>
    public string                Name    => _definition.Name;
    /// <summary>The table's columns, in the order of their positions in the table.</summary>
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

    /// <summary>Why the table's AutoNumber columns cannot be filled, or null when they can: only Int32 and Guid ones are.</summary>
    private string? WhyNotNumbered()
        => _autoNumberColumns.FirstOrDefault(c => c.DataType is not (DataType.Int32 or DataType.Guid)) is { } column
            ? $"{Name}.{column.Name} is an AutoNumber of type {column.DataType}; only Int32 (Long Integer) and Guid " +
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
    /// The properties Access keeps for the table itself - its Description and the like - read from the catalog
    /// (MSysObjects.LvProp) the first time they are asked for. Empty for a table Access keeps none for. A
    /// column's properties are the column's: <see cref="Column.Properties"/>.
    /// </summary>
    public PropertyMap Properties => PropertyMaps.Default;

    /// <summary>The table's property maps: its own and one for each column that has properties.</summary>
    internal PropertyMaps PropertyMaps
    {
        get
        {
            if (_propertiesCache is not null) return _propertiesCache;
            var catalog = new SystemCatalog(_file);
            byte[]? bytes = catalog.GetPropertyBytesForObject(_readDefinition.TdefPageNumber);
            return _propertiesCache = Attach(PropertyMapReader.Read(bytes, _file.Format));
        }
    }

    // The table's maps are saved through its database, to the table's row of the catalog.
    private PropertyMaps Attach(PropertyMaps maps)
    {
        if (_owningDb is { } db)
        {
            int id = _readDefinition.TdefPageNumber;
            maps.Saver = saved => db.SaveProperties(saved, () => db.Catalog.FindObject(id), $"table '{Name}'");
        }
        return maps;
    }

    /// <summary>The properties of <paramref name="column"/>: the map the catalog has under its name, or an empty one.</summary>
    internal PropertyMap PropertiesOf(Column column) => PropertyMaps.GetOrAdd(column.Name, PropertyMap.ColumnType);

    // ── What a column's properties refuse a row for ──────────────────────────

    // The columns whose properties can refuse a row, worked out from the properties as they were at _rulesVersion.
    private List<(Column Column, bool IsRequired, bool RefusesZeroLength)>? _rules;
    private int _rulesVersion;

    /// <summary>
    /// The columns a row can be refused for, by Access's Required and Allow Zero Length properties. An
    /// AutoNumber column is not among them - Access gives every row its number - nor a Yes/No column for
    /// Required, since it always has a value.
    /// </summary>
    private List<(Column Column, bool IsRequired, bool RefusesZeroLength)> Rules
    {
        get
        {
            var definition = _definition;
            var maps       = PropertyMaps;
            if (_rules is not null && _rulesVersion == maps.Version) return _rules;

            var rules = new List<(Column, bool, bool)>();
            foreach (var column in definition.Columns)
            {
                if (column.IsAutoNumber || column.IsCalculated) continue;
                bool required    = column.IsRequired && column.DataType != DataType.Boolean;
                bool refusesNone = column.DataType is DataType.Text or DataType.Memo && !column.AllowZeroLength;
                if (required || refusesNone) rules.Add((column, required, refusesNone));
            }
            _rulesVersion = maps.Version;
            return _rules = rules;
        }
    }

    /// <summary>
    /// Refuses <paramref name="written"/> - a row as it will be written - for what Access refuses it for by a
    /// column's properties: no value in a required column, or a zero-length string in a Text or Memo column that
    /// does not allow one. Access checks every column of a row it adds, and of a row it updates only the columns
    /// the update gives a value: <paramref name="assignedOnly"/> says which. It does not look at a column's
    /// default value, and neither does this library fill one in.
    /// </summary>
    private void EnsureColumnRules(Row written, bool assignedOnly)
    {
        foreach (var (column, isRequired, refusesZeroLength) in Rules)
        {
            bool assigned = written.TryGetValue(column.Name, out var value);
            if (assignedOnly && !assigned) continue;

            if (value is null)
            {
                if (isRequired)
                    throw new ConstraintViolationException(
                        $"{Name}.{column.Name} is a required column (Access's Required property) and the row has no value in it. " +
                        "Nothing was written.");
                continue;
            }
            if (refusesZeroLength && (value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))?.Length == 0)
                throw new ConstraintViolationException(
                    $"{Name}.{column.Name} does not take a zero-length string (Access's Allow Zero Length property). " +
                    "Nothing was written.");
        }
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The table's rows, in the order they are stored, which for a table only added to is the order they were
    /// inserted in. Deleted rows are passed, and Memo and OLE values are read whole. Each row has an entry for
    /// every column of the table, in the table's column order: <c>null</c> for a column with no value.
    /// <para>
    /// The rows are read as they are enumerated, a page at a time, so a table of any size can be gone through
    /// without holding it all; <c>Rows.ToList()</c> gives a list. Each enumeration reads the table again from its
    /// first row. Insert, update or delete rows of the table while enumerating it and the rows still to come
    /// are those of the pages as they were listed, and of each page as it was when it was reached: take the
    /// list first.
    /// </para>
    /// </summary>
    public IEnumerable<Row> Rows
    {
        get
        {
            var definition = _definition;
            var format     = _file.Format;
            var lvalReader = definition.LvalColumnUmapPages.Count > 0
                ? new LvalReader(_file)
                : null;
            var decoder = new RowDecoder(format, definition.Columns, lvalReader);

            byte[] umapPage  = _file.ReadPage(definition.UmapPageNumber);
            var    ownedList = UsageMap.GetOwnedPages(umapPage, definition.OwnedPagesRow, format, _file);

            // Each page is read once, and its rows given from that reading.
            foreach (int pageNum in ownedList)
            {
                byte[] dp       = _file.ReadPage(pageNum);
                int    rowCount = ByteUtil.GetShort(dp, format.OffsetDataNumRows);

                for (int r = 0; r < rowCount; r++)
                {
                    // Deleted rows are passed; an overflow row is read where its pointer leads.
                    if (DataRows.Read(_file, dp, r) is not { } rowBytes) continue;

                    // Every column has its entry, in the table's order: null for a column with no value.
                    var row = new Row(definition.Columns.Count);
                    foreach (var col in definition.Columns)
                        row[col.Name] = decoder.Decode(rowBytes, col);
                    yield return row;
                }
            }
        }
    }

    /// <summary>
    /// Creates a cursor over the table's rows in the order they are stored, which can move both ways. For a
    /// plain read of every row, <see cref="Rows"/>.
    /// </summary>
    public Cursor CreateCursor() => new Cursor(this, _file, _definition);

    /// <summary>
    /// Creates a cursor that finds rows through the table's primary key (<see cref="IndexCursor.FindRow"/>,
    /// <see cref="IndexCursor.FindRowByEntry"/>).
    /// </summary>
    public IndexCursor CreateIndexCursor() => new IndexCursor(this, _file, _definition, indexName: null);

    /// <summary>
    /// Creates a cursor that finds rows through the index named <paramref name="indexName"/>, whatever the case
    /// of its letters. The name is an index's name (<see cref="Indexes"/>), not a column's: a primary key is
    /// asked for by its index's name, "PrimaryKey" in a table Access or this library made, or with
    /// <see cref="CreateIndexCursor()"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The table has no index of that name.</exception>
    public IndexCursor CreateIndexCursor(string indexName)
        => new IndexCursor(this, _file, _definition, indexName ?? throw new ArgumentNullException(nameof(indexName)));

    /// <summary>
    /// The row whose primary key is <paramref name="primaryKey"/>, or null when the table has none: one value for
    /// a key of one column, and for a key of several a value for each, in the key's order. The row is found
    /// through the primary key's index when its keys are ones this library makes, and otherwise by reading the
    /// table.
    /// </summary>
    /// <exception cref="ArgumentException">The number of values is not the number of columns in the primary key.</exception>
    /// <exception cref="InvalidOperationException">The table has no primary key.</exception>
    public Row? FindRowByPrimaryKey(params object?[] primaryKey)
    {
        if (primaryKey is null) throw new ArgumentNullException(nameof(primaryKey));

        var definition = _definition;
        var keyColumns = definition.EffectivePrimaryKeyColumns;
        if (keyColumns.Count == 0)
            throw new InvalidOperationException(
                $"Table '{definition.Name}' has no primary key. Use an index cursor's FindRowByEntry for another " +
                "index, or its FindRow(column, value) to look in one column.");
        if (primaryKey.Length != keyColumns.Count)
            throw new ArgumentException(
                $"The primary key of '{definition.Name}' has {keyColumns.Count} column(s) " +
                $"({string.Join(", ", keyColumns)}); {primaryKey.Length} value(s) were given.", nameof(primaryKey));

        if (keyColumns.Count == 1)
            return new IndexCursor(this, _file, definition, indexName: null).FindRow(keyColumns[0], primaryKey[0]);

        string? keyIndex = definition.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey)?.Name;
        return new IndexCursor(this, _file, definition, keyIndex).FindRowByEntry(primaryKey);
    }

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
    /// <exception cref="ConstraintViolationException">
    /// The row, with its AutoNumber values filled in, would be a duplicate in the primary key or another unique
    /// index, or has a null in a column of the primary key or of an index that requires its values; or it has no
    /// value in a required column (<see cref="Column.IsRequired"/>), or a zero-length string in a Text or Memo
    /// column that does not allow one (<see cref="Column.AllowZeroLength"/>). Nothing of the row is written, and
    /// the counter and <paramref name="row"/> are as they were.
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
            EnsureColumnRules(written, assignedOnly: false);
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
    /// <see cref="ConstraintViolationException"/> for a null a primary key, a required index or a required column
    /// does not take, a zero-length string in a column that does not allow one, or a
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
        EnsureColumnRules(asWritten, assignedOnly: false);
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
    /// <param name="primaryKeyValue">The value the row has in the primary key's column.</param>
    /// <param name="newValues">
    /// The values to give the row, by column name. A column it does not name keeps the value it has.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="newValues"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// No row has that primary key, or the table's primary key is not one column: it has none, or one of
    /// several columns. Nothing is written.
    /// </exception>
    /// <exception cref="ConstraintViolationException">
    /// The updated row would be a duplicate of another in the primary key or another unique index, or has a null
    /// in a column of the primary key or of an index that requires its values; or <paramref name="newValues"/>
    /// gives no value to a required column (<see cref="Column.IsRequired"/>), or a zero-length string to a Text
    /// or Memo column that does not allow one (<see cref="Column.AllowZeroLength"/>). Only the columns
    /// <paramref name="newValues"/> names are checked against those two properties, as Access checks an update.
    /// The row is left as it was.
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
                    .Where(c => c.DataType == DataType.Int32 && newValues.ContainsKey(c.Name))
                    .Select(c => (int?)Convert.ToInt32(newValues[c.Name], System.Globalization.CultureInfo.InvariantCulture))
                    .Max();
        }

        // What the columns' properties refuse, of the columns the update gives a value: Access does not look at
        // the others, so a row that already breaks a rule in one of them is still updated.
        if (newValues is not null) EnsureColumnRules(newValues, assignedOnly: true);

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

        /// <param name="indexes">The table's indexes, whose entries move.</param>
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
    /// <param name="columnName">The name of the column to look in, whatever the case of its letters.</param>
    /// <param name="value">The value the row to delete has in that column.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The table has no column of that name, or no row has the value in it. Nothing is deleted.
    /// </exception>
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
    /// The table's Decimal values whose stored number has more digits than their column's precision allows:
    /// what <see cref="DecimalByteOrder.Find(Table)"/> lists, where it is described.
    /// </summary>
    internal IEnumerable<DecimalBeyondPrecision> FindDecimalsBeyondPrecision()
    {
        var decimals = _definition.Columns
            .Where(c => c.DataType == DataType.Decimal && !c.IsCalculated && c.Precision > 0)
            .ToList();
        if (decimals.Count == 0) yield break;

        var format  = _file.Format;
        var decoder = new RowDecoder(format, _definition.Columns);
        var keys    = _definition.EffectivePrimaryKeyColumns
            .Select(name => _definition.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OfType<Column>()
            .ToList();

        // The rows in the order Rows gives them, which RowIndex counts in.
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
                        NumericCodec.Decode(stored, 0, column.Scale), isCut: unscaled >> 96 != 0,
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
    /// <param name="column">The column to add.</param>
    /// <returns>The table's new column.</returns>
    /// <exception cref="ArgumentException">
    /// The table already has a column of that name, or 255 columns; or the definition is not one a table can
    /// have (<see cref="ColumnDefinition"/>). Nothing is changed.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is an Access 97 one; the column is of a type this library does not write (a complex or calculated
    /// one), or is an AutoNumber and the table has rows or an AutoNumber already; or the table's definition runs,
    /// or would run, over more than one page, which is all this library reads of one - so a table is never left
    /// that it could no longer write rows to. Nothing is changed.
    /// </exception>
    public Column AddColumn(ColumnDefinition column)
    {
        if (column is null) throw new ArgumentNullException(nameof(column));
        string name = SchemaChange.AddColumn(Owner(), _file, _definition, column.ToColumn(nameof(column)));
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
    /// <exception cref="ConstraintViolationException">
    /// Two rows of the table have the same values in a unique index, or a row has a null in an index that
    /// requires its values. Nothing is changed.
    /// </exception>
    /// <exception cref="InvalidOperationException">A column the index names is not in the table. Nothing is changed.</exception>
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
}
