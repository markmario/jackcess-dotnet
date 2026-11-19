using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Reflection;

namespace JackcessDotNet;

/// <summary>
/// Imports a <see cref="DataTable"/>, the tables of a <see cref="DataSet"/> or a sequence of objects into a
/// <see cref="Database"/>: a table is created with columns worked out from the source, and the source's rows are
/// inserted into it.
/// <para>
/// A column's type follows the CLR type of the source's column or property. <see cref="bool"/> is
/// <see cref="DataType.Boolean"/>; <see cref="byte"/> and <see cref="sbyte"/> are <see cref="DataType.Byte"/>;
/// <see cref="short"/> and <see cref="ushort"/> are <see cref="DataType.Int16"/>; <see cref="int"/>,
/// <see cref="uint"/>, <see cref="long"/> and <see cref="ulong"/> are <see cref="DataType.Int32"/>;
/// <see cref="float"/> is <see cref="DataType.Single"/>; <see cref="double"/> is <see cref="DataType.Double"/>;
/// <see cref="decimal"/> is <see cref="DataType.Currency"/>; <see cref="DateTime"/> and
/// <see cref="DateTimeOffset"/> are <see cref="DataType.DateTime"/>, a <see cref="DateTimeOffset"/> stored as its
/// UTC date and time; <see cref="Guid"/> is <see cref="DataType.Guid"/>. A <see cref="string"/> or a
/// <see cref="char"/> is <see cref="DataType.Text"/> of the source's maximum length, 255 characters when it has
/// none, and <see cref="DataType.Memo"/> when that length is more than 255. A <c>byte[]</c> is
/// <see cref="DataType.Binary"/> of the source's maximum length, 50 bytes when it has none, and
/// <see cref="DataType.OleObject"/> when that length is more than 255. A nullable type is its underlying type,
/// and an enum its underlying integer type. A value that does not fit its column's type - a <see cref="long"/>
/// beyond a 32-bit integer - stops the import at its row, with <see cref="OverflowException"/>.
/// </para>
/// <para>
/// A column or property of any other type is left out, unless
/// <see cref="ImportOptions.FallbackUnmappableToString"/> has it stored as text;
/// <see cref="ImportOptions.OnUnmappableColumn"/> is called for each either way.
/// </para>
/// <para>
/// A table that is already there is refused, unless <see cref="ImportOptions.AppendIfExists"/> has the rows
/// added to it. Rows are inserted one at a time, each checked as <see cref="Table.Insert"/> checks it: when a
/// row is refused, the table and the rows inserted before it stay.
/// </para>
/// </summary>
public static class DatabaseImporter
{
    // ── Extension methods on Database ────────────────────────────────────────

    /// <summary>
    /// Imports a <see cref="DataTable"/> as a table named for it, with its primary key:
    /// <see cref="ImportTable(Database, DataTable, ImportOptions)"/> with no option set.
    /// </summary>
    /// <param name="database">The database to import into.</param>
    /// <param name="table">The table to import: its columns and its rows.</param>
    /// <returns>The table created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="table"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The database has a table of that name already, or no column of <paramref name="table"/> has a type a
    /// column of a database can have.
    /// </exception>
    public static Table ImportTable(this Database database, DataTable table)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        if (table is null)    throw new ArgumentNullException(nameof(table));
        return ImportDataTable(database, table, new ImportOptions());
    }

    /// <summary>
    /// Imports a <see cref="DataTable"/>: creates a table with a column for each of its columns and inserts its
    /// rows, or adds its rows to a table that is already there.
    /// <para>
    /// The table is named <see cref="ImportOptions.TableName"/>, or else <see cref="DataTable.TableName"/>, or
    /// else <c>Table1</c>. A column takes its name, its type and - for text and binary - its
    /// <see cref="DataColumn.MaxLength"/> from the source's column; an <see cref="int"/> column with
    /// <see cref="DataColumn.AutoIncrement"/> is an AutoNumber. The primary key is
    /// <see cref="ImportOptions.PrimaryKey"/>, or else the source's <see cref="DataTable.PrimaryKey"/> when that is
    /// one column; a source key of several columns is not carried over. The table's rows keep the values the
    /// source has for an AutoNumber column, and the table numbers later rows on from the highest. Rows the
    /// source has marked deleted are not imported.
    /// </para>
    /// <para>
    /// When the database has a table of that name already - compared without regard to case - and
    /// <see cref="ImportOptions.AppendIfExists"/> is set, the rows are added to it and nothing of the table is
    /// changed: a source column is matched to the table's column of the same name, whatever its case, its values
    /// converted to that column's type; a source column the table does not have is left out, and a column of the
    /// table the source does not have is null in the added rows. <see cref="ImportOptions.PrimaryKey"/> is not
    /// used. <see cref="ImportOptions.KeepIdentity"/> says whether the rows keep the source's AutoNumber values.
    /// </para>
    /// </summary>
    /// <param name="database">The database to import into.</param>
    /// <param name="table">The table to import: its columns and its rows.</param>
    /// <param name="options">How to import it.</param>
    /// <returns>The table created, or the table the rows were added to.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="database"/>, <paramref name="table"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ImportOptions.PrimaryKey"/> names a column the new table does not have, or holds an empty name.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The database has a table of that name already and <see cref="ImportOptions.AppendIfExists"/> is not set;
    /// no column of <paramref name="table"/> has a type a column of a database can have; or, adding to a table,
    /// no column of <paramref name="table"/> has the name of one of its columns.
    /// </exception>
    /// <exception cref="ConstraintViolationException">
    /// A row breaks a rule of the table: a duplicate in its primary key or a unique index, a null in a column
    /// of its primary key or of an index that requires its values, or a foreign key with no row to refer to.
    /// </exception>
    /// <exception cref="ColumnValueTooLongException">A text value is longer than its column.</exception>
    /// <exception cref="NotSupportedException">The database is an Access 97 file, which is only read.</exception>
    public static Table ImportTable(this Database database, DataTable table, ImportOptions options)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        if (table is null)    throw new ArgumentNullException(nameof(table));
        if (options is null)  throw new ArgumentNullException(nameof(options));
        return ImportDataTable(database, table, options);
    }

    private static Table ImportDataTable(Database db, DataTable table, ImportOptions options)
    {
        string name = ResolveTableName(options.TableName, table.TableName, "Table1");

        // Pre-existing target: append rows into the existing schema instead of creating.
        if (TableExists(db, name))
        {
            if (!options.AppendIfExists)
                throw new InvalidOperationException(
                    $"Table '{name}' already exists. Set ImportOptions.AppendIfExists = true to append rows to it.");
            var existing = db.GetTable(name);
            AppendDataTableRows(existing, table, options);
            return existing;
        }

        // Build column list. Unmappable columns are either dropped or, when
        // FallbackUnmappableToString is set, stored as Memo via ToString().
        var columns       = new List<Column>(table.Columns.Count);
        var columnSources = new List<DataColumn>(table.Columns.Count);
        var stringFallback = new List<bool>(table.Columns.Count);
        foreach (DataColumn dc in table.Columns)
        {
            int? maxLen = dc.MaxLength > 0 ? dc.MaxLength : (int?)null;
            bool isAuto = dc.AutoIncrement && dc.DataType == typeof(int);
            var col = TypeMapper.ColumnFromClrType(dc.ColumnName, dc.DataType, maxLen, isAuto);
            bool fellBack = false;
            if (col is null)
            {
                options.OnUnmappableColumn?.Invoke(dc.ColumnName, dc.DataType);
                if (!options.FallbackUnmappableToString) continue;
                col = new ColumnDefinition(dc.ColumnName, DataType.Memo).ToColumn(nameof(table));
                fellBack = true;
            }
            columns.Add(col);
            columnSources.Add(dc);
            stringFallback.Add(fellBack);
        }

        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"DataTable '{table.TableName}' has no columns that map to a Jet type.");

        // The key is read here, for a table that is made: rows added to a table that is there take no key,
        // and the option is not looked at for them.
        var pk = PrimaryKeyOf(options) ?? InferPrimaryKey(table);
        var t = db.CreateTableOf(name, columns, pk);

        // An AutoIncrement column's values are the rows' own - other tables of a DataSet refer to them - so the
        // new table takes them as given, and its counter ends at the highest: rows added later, here or in
        // Access, are numbered on from it.
        IEnumerable<Row> Rows()
        {
            foreach (DataRow dr in table.Rows)
            {
                if (dr.RowState == DataRowState.Deleted) continue;
                var row = new Row();
                for (int i = 0; i < columnSources.Count; i++)
                {
                    var dc = columnSources[i];
                    object? raw = dr[dc];
                    if (raw == DBNull.Value) raw = null;
                    if (stringFallback[i] && raw is not null) raw = raw.ToString();
                    row[columns[i].Name] = TypeMapper.CoerceForJet(raw, columns[i].DataType);
                }
                yield return row;
            }
        }
        InsertRows(t, Rows(), keepIdentity: true);

        return t;
    }

    /// <summary>
    /// Imports every <see cref="DataTable"/> of a <see cref="DataSet"/>, each as a table named for it, with its
    /// primary key: <see cref="ImportTables(Database, DataSet, ImportOptions)"/> with no option set.
    /// </summary>
    /// <param name="database">The database to import into.</param>
    /// <param name="dataSet">The tables to import.</param>
    /// <returns>The tables created, in the order the <see cref="DataSet"/> has them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="dataSet"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The database has a table with the name of one to import already, or no column of one has a type a column
    /// of a database can have. The tables before it stay.
    /// </exception>
    public static IReadOnlyList<Table> ImportTables(this Database database, DataSet dataSet)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        if (dataSet is null)  throw new ArgumentNullException(nameof(dataSet));
        return ImportDataSet(database, dataSet, new ImportOptions());
    }

    /// <summary>
    /// Imports every <see cref="DataTable"/> of a <see cref="DataSet"/>, in the order the <see cref="DataSet"/>
    /// has them, each as <see cref="ImportTable(Database, DataTable, ImportOptions)"/> imports one. Each table is
    /// named for its <see cref="DataTable"/> and has that table's primary key. The <see cref="DataSet"/>'s
    /// relations are not imported.
    /// <para>
    /// A table that is refused stops the import, and the tables imported before it stay.
    /// </para>
    /// </summary>
    /// <param name="database">The database to import into.</param>
    /// <param name="dataSet">The tables to import.</param>
    /// <param name="options">
    /// How to import each table. <see cref="ImportOptions.TableName"/> and <see cref="ImportOptions.PrimaryKey"/>
    /// describe one table and must be null.
    /// </param>
    /// <returns>
    /// The tables created, or added to, in the order the <see cref="DataSet"/> has them: none for a
    /// <see cref="DataSet"/> without tables.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="database"/>, <paramref name="dataSet"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ImportOptions.TableName"/> or <see cref="ImportOptions.PrimaryKey"/> is set. Nothing is imported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A table is refused as <see cref="ImportTable(Database, DataTable, ImportOptions)"/> refuses one.
    /// </exception>
    /// <exception cref="ConstraintViolationException">A row breaks a rule of its table.</exception>
    /// <exception cref="ColumnValueTooLongException">A text value is longer than its column.</exception>
    /// <exception cref="NotSupportedException">The database is an Access 97 file, which is only read.</exception>
    public static IReadOnlyList<Table> ImportTables(this Database database, DataSet dataSet, ImportOptions options)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        if (dataSet is null)  throw new ArgumentNullException(nameof(dataSet));
        if (options is null)  throw new ArgumentNullException(nameof(options));
        if (options.TableName is not null)
            throw new ArgumentException(
                "ImportOptions.TableName names one table; each table of a DataSet is named for its DataTable.",
                nameof(options));
        if (options.PrimaryKey is not null)
            throw new ArgumentException(
                "ImportOptions.PrimaryKey is one table's key; each table of a DataSet has its DataTable's primary key.",
                nameof(options));
        return ImportDataSet(database, dataSet, options);
    }

    private static IReadOnlyList<Table> ImportDataSet(Database db, DataSet dataSet, ImportOptions options)
    {
        var result = new List<Table>(dataSet.Tables.Count);
        foreach (DataTable dt in dataSet.Tables)
            result.Add(ImportDataTable(db, dt, options));
        return result;
    }

    /// <summary>
    /// Imports a sequence of objects as a table named for their type, with a column for each property:
    /// <see cref="ImportTable{T}(Database, IEnumerable{T}, ImportOptions)"/> with no option set.
    /// </summary>
    /// <typeparam name="T">The type whose public properties are the table's columns.</typeparam>
    /// <param name="database">The database to import into.</param>
    /// <param name="items">The objects to import, each a row. A null among them is passed over.</param>
    /// <returns>The table created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="items"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The database has a table of that name already, or no property of <typeparamref name="T"/> has a type a
    /// column of a database can have.
    /// </exception>
    public static Table ImportTable<T>(this Database database, IEnumerable<T> items)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        if (items is null)    throw new ArgumentNullException(nameof(items));
        return ImportCollection(database, items, new ImportOptions());
    }

    /// <summary>
    /// Imports a sequence of objects: creates a table with a column for each public property of
    /// <typeparamref name="T"/> and inserts a row for each object, or adds the rows to a table that is already
    /// there.
    /// <para>
    /// The table is named <see cref="ImportOptions.TableName"/>, or else the name a <see cref="TableAttribute"/>
    /// on <typeparamref name="T"/> gives (its schema is not used), or else <typeparamref name="T"/>'s own name.
    /// Every public instance property that can be read is a column, in the order reflection lists them, but an
    /// indexer and a property with <see cref="NotMappedAttribute"/>. A column is named for its property, or as
    /// its <see cref="ColumnAttribute"/> names it; <see cref="MaxLengthAttribute"/> is its length, for text and
    /// binary; and an <see cref="int"/> property with <see cref="DatabaseGeneratedAttribute"/> of
    /// <see cref="DatabaseGeneratedOption.Identity"/> is an AutoNumber. The primary key is
    /// <see cref="ImportOptions.PrimaryKey"/>, or else the first property with <see cref="KeyAttribute"/>.
    /// An AutoNumber property that has a value keeps it; one that is 0 is numbered by the table, past every
    /// value the objects bring.
    /// </para>
    /// <para>
    /// When the database has a table of that name already - compared without regard to case - and
    /// <see cref="ImportOptions.AppendIfExists"/> is set, the rows are added to it and nothing of the table is
    /// changed: a property is matched to the table's column of the same name, whatever its case, its values
    /// converted to that column's type; a property the table has no column for is left out, and a column no
    /// property matches is null in the added rows. <see cref="ImportOptions.PrimaryKey"/> is not used.
    /// <see cref="ImportOptions.KeepIdentity"/> says whether the rows keep the objects' AutoNumber values.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type whose public properties are the table's columns.</typeparam>
    /// <param name="database">The database to import into.</param>
    /// <param name="items">
    /// The objects to import, each a row. A null among them is passed over. They are read once, as the rows are
    /// inserted.
    /// </param>
    /// <param name="options">How to import them.</param>
    /// <returns>The table created, or the table the rows were added to.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="database"/>, <paramref name="items"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ImportOptions.PrimaryKey"/> names a column the new table does not have, or holds an empty name.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The database has a table of that name already and <see cref="ImportOptions.AppendIfExists"/> is not set;
    /// no property of <typeparamref name="T"/> has a type a column of a database can have; or, adding to a
    /// table, no property of <typeparamref name="T"/> has the name of one of its columns.
    /// </exception>
    /// <exception cref="ConstraintViolationException">
    /// A row breaks a rule of the table: a duplicate in its primary key or a unique index, a null in a column
    /// of its primary key or of an index that requires its values, or a foreign key with no row to refer to.
    /// </exception>
    /// <exception cref="ColumnValueTooLongException">A text value is longer than its column.</exception>
    /// <exception cref="NotSupportedException">The database is an Access 97 file, which is only read.</exception>
    public static Table ImportTable<T>(this Database database, IEnumerable<T> items, ImportOptions options)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        if (items is null)    throw new ArgumentNullException(nameof(items));
        if (options is null)  throw new ArgumentNullException(nameof(options));
        return ImportCollection(database, items, options);
    }

    private static Table ImportCollection<T>(Database db, IEnumerable<T> items, ImportOptions options)
    {
        // Table name precedence: ImportOptions.TableName > [Table("X")] on T > typeof(T).Name.
        // [Table].Schema is ignored — Access has no schemas.
        string? attrName = typeof(T).GetCustomAttribute<TableAttribute>()?.Name;
        string name = ResolveTableName(options.TableName, attrName ?? typeof(T).Name, typeof(T).Name);

        // Pre-existing target: append rows; bind T's properties to the existing
        // schema by name (case-insensitive). Source properties that don't have
        // a matching target column are silently skipped.
        if (TableExists(db, name))
        {
            if (!options.AppendIfExists)
                throw new InvalidOperationException(
                    $"Table '{name}' already exists. Set ImportOptions.AppendIfExists = true to append rows to it.");
            var existing = db.GetTable(name);
            AppendCollectionRows(existing, items, options);
            return existing;
        }

        var (columns, props, columnNames, stringFallback, pkFromAttr) =
            BuildSchemaFromType(typeof(T), options);
        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no public properties that map to a Jet type.");

        var pk = PrimaryKeyOf(options) ?? (pkFromAttr is null ? null : new[] { pkFromAttr });

        var t = db.CreateTableOf(name, columns, pk);

        // An identity property that has a value keeps it; one still at 0 - an object the database has yet to
        // number - is numbered by the table.
        IEnumerable<Row> Rows()
        {
            foreach (var item in items)
            {
                if (item is null) continue;
                var row = new Row();
                for (int i = 0; i < props.Count; i++)
                {
                    object? raw = props[i].GetValue(item);
                    if (columns[i].IsAutoNumber && IsUnnumbered(raw)) continue;
                    if (stringFallback[i] && raw is not null) raw = raw.ToString();
                    row[columnNames[i]] = TypeMapper.CoerceForJet(raw, columns[i].DataType);
                }
                yield return row;
            }
        }
        InsertRows(t, Rows(), keepIdentity: true);
        return t;
    }

    /// <summary>
    /// The primary key the options ask for: null when they ask for none, and the source's is used; a list
    /// without names for a table without a primary key.
    /// </summary>
    private static IReadOnlyList<string>? PrimaryKeyOf(ImportOptions options)
    {
        var key = options.PrimaryKey?.ToList();
        if (key is not null && key.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A primary key column name must not be empty.", nameof(options));
        return key;
    }

    // ── Append-into-existing-table helpers ───────────────────────────────────

    /// <summary>
    /// Case-insensitive check for whether <paramref name="db"/> already has a
    /// table named <paramref name="name"/>. <see cref="Database.GetTableNames"/>
    /// already returns the case as it appears in MSysObjects, so we compare
    /// with <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    private static bool TableExists(Database db, string name)
    {
        foreach (var existing in db.GetTableNames())
            if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Append every non-deleted row from <paramref name="source"/> into
    /// <paramref name="target"/>. Source columns are matched to target columns
    /// by name (case-insensitive); source columns with no matching target column
    /// are silently dropped. Values are coerced to each target column's
    /// declared <see cref="DataType"/> via <see cref="TypeMapper.CoerceForJet"/>.
    /// The target numbers the rows itself, unless <see cref="ImportOptions.KeepIdentity"/> keeps the source's.
    /// </summary>
    private static void AppendDataTableRows(Table target, DataTable source, ImportOptions options)
    {
        var targetByName = target.Columns.ToDictionary(
            c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

        var pairs = new List<(DataColumn Src, Column Tgt)>();
        foreach (DataColumn dc in source.Columns)
            if (targetByName.TryGetValue(dc.ColumnName, out var tgt))
                pairs.Add((dc, tgt));

        if (pairs.Count == 0)
            throw new InvalidOperationException(
                $"DataTable '{source.TableName}' has no columns that match the existing target table '{target.Name}'.");

        IEnumerable<Row> Rows()
        {
            foreach (DataRow dr in source.Rows)
            {
                if (dr.RowState == DataRowState.Deleted) continue;
                var row = new Row();
                foreach (var (src, tgt) in pairs)
                {
                    object? raw = dr[src];
                    if (raw == DBNull.Value) raw = null;

                    // FallbackUnmappableToString carries over to append: if the
                    // source value's CLR type doesn't map to a Jet type at all and
                    // the target column is text-shaped, use ToString().
                    if (raw is not null && options.FallbackUnmappableToString
                        && (tgt.DataType == DataType.Text || tgt.DataType == DataType.Memo)
                        && TypeMapper.MapClrType(raw.GetType()) is null)
                        raw = raw.ToString();

                    row[tgt.Name] = TypeMapper.CoerceForJet(raw, tgt.DataType);
                }
                yield return row;
            }
        }
        InsertRows(target, Rows(), options.KeepIdentity);
    }

    /// <summary>
    /// Inserts the rows of one import. For as long as it takes, the table itself says whether a row may bring its
    /// own AutoNumber - <paramref name="keepIdentity"/>, whichever way it is, and whatever
    /// <see cref="Database.AllowAutoNumberInsert"/> says: an append that asked for its rows to be numbered afresh
    /// is not to keep their numbers because the database lets other callers bring theirs. The table is then put
    /// back as it was.
    /// <para>
    /// Rows that keep their numbers are counted before any is written, so the outcome does not depend on their
    /// order: see <see cref="ReserveSuppliedIdentities"/>.
    /// </para>
    /// </summary>
    private static void InsertRows(Table target, IEnumerable<Row> rows, bool keepIdentity)
    {
        bool? before = target.AllowAutoNumberInsert;
        target.AllowAutoNumberInsert = keepIdentity;
        try
        {
            var identity = keepIdentity
                ? target.Columns.FirstOrDefault(c => c.IsAutoNumber && c.DataType == DataType.Int32)
                : null;
            if (identity is not null)
            {
                // Read whole, where rows without an identity to keep are written as they come.
                var batch = rows.ToList();
                ReserveSuppliedIdentities(target, identity, batch);
                rows = batch;
            }

            foreach (var row in rows)
                target.Insert(row);
        }
        finally
        {
            target.AllowAutoNumberInsert = before;
        }
    }

    /// <summary>
    /// Accounts for every number the rows of <paramref name="batch"/> bring for <paramref name="identity"/>,
    /// before any of them is written. The table's counter moves up to the highest, so a row that brings none is
    /// numbered past them all, wherever in the batch it comes: objects with identities 0 and 1 were given 1 and 1,
    /// the first numbered before the second's number was seen.
    /// <para>
    /// What the table's indexes would refuse is refused here, with nothing written. Every row is checked as it
    /// will be written (<see cref="Table.Validate"/>): a number a row of the table already has in its primary
    /// key - or any value another row has in a unique index - is the refusal an insert of that row would be, in
    /// the same words, made before the first row goes in and found through the index, not by reading the table.
    /// The rows of the batch are not in any index yet, so a number two of them bring for the primary key is
    /// looked for here. A column that is not a key may repeat a number, as it may in Access.
    /// </para>
    /// </summary>
    /// <exception cref="ConstraintViolationException">
    /// A row would be a duplicate in the primary key or another unique index, of a row of the table or - in the
    /// primary key - of another row of the batch.
    /// </exception>
    private static void ReserveSuppliedIdentities(Table target, Column identity, IReadOnlyList<Row> batch)
    {
        var supplied = batch
            .Where(row => row.TryGetValue(identity.Name, out var value) && !RowValues.IsNull(value))
            .Select(row => Convert.ToInt32(row[identity.Name], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (supplied.Count == 0) return;

        var key = target.Definition.EffectivePrimaryKeyColumns;
        if (key.Count == 1 && string.Equals(key[0], identity.Name, StringComparison.OrdinalIgnoreCase))
        {
            var seen = new HashSet<int>();
            foreach (int number in supplied)
                if (!seen.Add(number))
                    throw new ConstraintViolationException(
                        $"Two rows to import would be duplicates in {target.Name}'s primary key, which is unique: " +
                        $"both hold {number} for {identity.Name}. The table is left as it was.");
        }

        foreach (var row in batch)
            target.Validate(row);

        target.RaiseLastAutoNumber(supplied.Max());
    }

    /// <summary>
    /// POCO sibling of <see cref="AppendDataTableRows"/>. Matches public
    /// readable properties of <c>T</c> to <paramref name="target"/>'s columns
    /// by name (honouring <c>[Column]</c> + <c>[NotMapped]</c>). Properties
    /// without a matching target column are silently skipped.
    /// </summary>
    private static void AppendCollectionRows<T>(Table target, IEnumerable<T> items, ImportOptions options)
    {
        var targetByName = target.Columns.ToDictionary(
            c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

        var pairs = new List<(PropertyInfo Prop, Column Tgt)>();
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<NotMappedAttribute>() is not null) continue;

            string colName = p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name;
            if (targetByName.TryGetValue(colName, out var tgt))
                pairs.Add((p, tgt));
        }

        if (pairs.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no properties that match the existing target table '{target.Name}'.");

        IEnumerable<Row> Rows()
        {
            foreach (var item in items)
            {
                if (item is null) continue;
                var row = new Row();
                foreach (var (prop, tgt) in pairs)
                {
                    object? raw = prop.GetValue(item);

                    // An identity property still at 0 is an object the database has yet to number.
                    if (tgt.IsAutoNumber && IsUnnumbered(raw)) continue;

                    if (raw is not null && options.FallbackUnmappableToString
                        && (tgt.DataType == DataType.Text || tgt.DataType == DataType.Memo)
                        && TypeMapper.MapClrType(raw.GetType()) is null)
                        raw = raw.ToString();

                    row[tgt.Name] = TypeMapper.CoerceForJet(raw, tgt.DataType);
                }
                yield return row;
            }
        }
        InsertRows(target, Rows(), options.KeepIdentity);
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    /// <summary>An identity property still at zero, whatever integer type it is: an object yet to be numbered.</summary>
    private static bool IsUnnumbered(object? identity) => identity is 0 or 0L or (short)0 or 0u or 0ul or (ushort)0;

    private static string ResolveTableName(string? explicitName, string? fromSource, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName!;
        if (!string.IsNullOrWhiteSpace(fromSource))   return fromSource!;
        return fallback;
    }

    private static IReadOnlyList<string>? InferPrimaryKey(DataTable table)
    {
        // Only a key of one column is carried over.
        if (table.PrimaryKey.Length == 1) return new[] { table.PrimaryKey[0].ColumnName };
        return null;
    }

    private static (List<Column> columns,
                    List<PropertyInfo> props,
                    List<string> columnNames,
                    List<bool> stringFallback,
                    string? primaryKey)
        BuildSchemaFromType(Type type, ImportOptions options)
    {
        var columns        = new List<Column>();
        var props          = new List<PropertyInfo>();
        var columnNames    = new List<string>();
        var stringFallback = new List<bool>();
        string? pk         = null;

        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<NotMappedAttribute>() is not null) continue;

            string colName = p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name;
            int? maxLen = p.GetCustomAttribute<MaxLengthAttribute>()?.Length;
            bool isAuto = p.GetCustomAttribute<DatabaseGeneratedAttribute>() is { DatabaseGeneratedOption: DatabaseGeneratedOption.Identity }
                          && p.PropertyType == typeof(int);

            var col = TypeMapper.ColumnFromClrType(colName, p.PropertyType, maxLen, isAuto);
            bool fellBack = false;
            if (col is null)
            {
                options.OnUnmappableColumn?.Invoke(colName, p.PropertyType);
                if (!options.FallbackUnmappableToString) continue;
                col = new ColumnDefinition(colName, DataType.Memo).ToColumn("items");
                fellBack = true;
            }
            columns.Add(col);
            props.Add(p);
            columnNames.Add(colName);
            stringFallback.Add(fellBack);

            if (pk is null && p.GetCustomAttribute<KeyAttribute>() is not null)
                pk = colName;
        }

        return (columns, props, columnNames, stringFallback, pk);
    }
}

/// <summary>
/// How <see cref="DatabaseImporter"/> imports a table: its name and primary key, what becomes of a column whose
/// type a database has no type for, and whether rows are added to a table that is already there. An instance
/// with nothing set imports a table under the source's name, with the source's primary key, leaves out columns
/// that have no type, and refuses a table that is already there.
/// </summary>
public sealed class ImportOptions
{
    /// <summary>Creates options with nothing set.</summary>
    public ImportOptions()
    {
    }

    /// <summary>
    /// The name of the table to create or add to. When null, empty or white space, the source names the table:
    /// <see cref="DataTable.TableName"/>, or the type of the objects (see
    /// <see cref="DatabaseImporter.ImportTable{T}(Database, IEnumerable{T}, ImportOptions)"/>). It describes one
    /// table, so <see cref="DatabaseImporter.ImportTables(Database, DataSet, ImportOptions)"/> refuses options
    /// that set it.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>
    /// The names of the columns of the primary key of a table the import creates, in key order. When null, the
    /// source says: a <see cref="DataTable"/>'s primary key of one column, or the first property with
    /// <see cref="KeyAttribute"/>. When it has no names, the table has no primary key, whatever the source has.
    /// It is not used when rows are added to a table that is already there, and
    /// <see cref="DatabaseImporter.ImportTables(Database, DataSet, ImportOptions)"/> refuses options that set it.
    /// </summary>
    public IEnumerable<string>? PrimaryKey { get; set; }

    /// <summary>
    /// Called once for each column or property whose CLR type a database has no type for, with the column's name
    /// and that type, whether the column is then left out or stored as text
    /// (<see cref="FallbackUnmappableToString"/>). It is not called when rows are added to a table that is
    /// already there.
    /// </summary>
    public Action<string, Type>? OnUnmappableColumn { get; set; }

    /// <summary>
    /// Whether a column or property whose CLR type a database has no type for is stored as a
    /// <see cref="DataType.Memo"/> column holding each value's <see cref="object.ToString"/>, a null staying
    /// null. When <c>false</c>, the default, the column is left out. When rows are added to a table that is
    /// already there, it applies to a value of such a type bound for a Text or Memo column.
    /// </summary>
    public bool FallbackUnmappableToString { get; set; }

    /// <summary>
    /// Whether the rows are added to the table when the database already has one of the name to import under.
    /// The table's columns, primary key and indexes are left as they are; columns of the source are matched to
    /// the table's by name, whatever their case. A column the table does not have is left out, and a column the
    /// source does not have is null in the added rows. When <c>false</c>, the default, importing under a name a
    /// table already has throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool AppendIfExists { get; set; }

    /// <summary>
    /// When rows are appended to an existing table (<see cref="AppendIfExists"/>), whether they keep the values
    /// the source has for the table's AutoNumber columns.
    /// <para>When <c>false</c> (default), the table numbers the appended rows itself, on from its own counter, and
    /// a value the source has is ignored - as <c>SqlBulkCopy</c> does without <c>KeepIdentity</c>. Two batches
    /// that each count from the same number cannot then collide.</para>
    /// <para>Set it to <c>true</c> when other tables refer to the source's values - the tables of a
    /// <see cref="DataSet"/> appended one after another - so a child row still finds its parent. A value another
    /// row of the table already has is refused before anything is written when the column is the table's primary
    /// key, and is otherwise the caller's to avoid. A row that brings no value - a typed object's identity
    /// property still at 0, a null - is numbered by the table, past every value the batch brings, so the result
    /// does not depend on the order of the rows.</para>
    /// <para>It decides for the append whatever <see cref="Database.AllowAutoNumberInsert"/> says.</para>
    /// <para>A table the import creates always keeps the source's values: nothing is there to collide with.</para>
    /// </summary>
    public bool KeepIdentity { get; set; }
}
