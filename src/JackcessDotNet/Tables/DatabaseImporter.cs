using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Reflection;

namespace JackcessDotNet;

/// <summary>
/// High-level import helpers that turn a <see cref="DataTable"/>, <see cref="DataSet"/>,
/// or any <c>IEnumerable&lt;T&gt;</c> into a fully-populated Access table without the
/// caller having to spell out a <see cref="ColumnBuilder"/> per column.
///
/// Schema is inferred via <see cref="TypeMapper"/>:
/// <list type="bullet">
///   <item><see cref="DataTable"/> — uses <c>DataColumn.DataType</c>, <c>MaxLength</c>,
///         <c>AllowDBNull</c>, and the <c>PrimaryKey</c> array.</item>
///   <item><see cref="DataSet"/> — calls <see cref="ImportTable(Database, DataTable, string?, string?)"/>
///         once per table; respects <c>DataSet.Relations</c> only as table iteration
///         order (relationship rows aren't written to MSysRelationships yet).</item>
///   <item><c>IEnumerable&lt;T&gt;</c> — reflects over public instance properties of
///         <c>T</c>. <see cref="MaxLengthAttribute"/> sets Text length;
///         <see cref="KeyAttribute"/> picks the primary key;
///         <see cref="NotMappedAttribute"/> skips a property;
///         <see cref="ColumnAttribute"/> renames a property;
///         <see cref="TableAttribute"/> renames the table (overridden by the
///         <c>tableName</c> parameter; <c>Schema</c> is ignored).</item>
/// </list>
///
/// Properties / DataColumns of unmappable types are skipped with a warning logged via
/// the optional <see cref="ImportOptions.OnUnmappableColumn"/> callback — no exception
/// is thrown so legacy <c>DataTable</c>s with object/Type-typed columns don't blow up.
/// </summary>
public static class DatabaseImporter
{
    // ── Static factories ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh .mdb at <paramref name="path"/> and imports
    /// <paramref name="table"/> into it. Returns the open database — caller owns
    /// the disposal.
    /// </summary>
    public static Database CreateFromDataTable(
        string path, DataTable table, JetVersion version = JetVersion.Jet4,
        string? tableName = null, string? primaryKey = null,
        ImportOptions? options = null)
    {
        var db = Database.Create(path, version);
        try
        {
            db.ImportTable(table, tableName, primaryKey, options);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a fresh .mdb at <paramref name="path"/> and imports every
    /// <see cref="DataTable"/> in <paramref name="dataSet"/>.
    /// </summary>
    public static Database CreateFromDataSet(
        string path, DataSet dataSet, JetVersion version = JetVersion.Jet4,
        ImportOptions? options = null)
    {
        var db = Database.Create(path, version);
        try
        {
            db.ImportTables(dataSet, options);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a fresh .mdb at <paramref name="path"/> and imports
    /// <paramref name="items"/> as a single table.
    /// </summary>
    public static Database CreateFromCollection<T>(
        string path, IEnumerable<T> items, JetVersion version = JetVersion.Jet4,
        string? tableName = null, string? primaryKey = null,
        ImportOptions? options = null)
    {
        var db = Database.Create(path, version);
        try
        {
            db.ImportTable(items, tableName, primaryKey, options);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    // ── Extension methods on Database ────────────────────────────────────────

    /// <summary>
    /// Imports <paramref name="table"/> into the database. The Access table is
    /// named after <paramref name="tableName"/> if supplied, otherwise
    /// <c>DataTable.TableName</c>, falling back to <c>"Table1"</c>.
    /// </summary>
    public static Table ImportTable(this Database db, DataTable table,
        string? tableName = null, string? primaryKey = null,
        ImportOptions? options = null)
    {
        if (db is null)    throw new ArgumentNullException(nameof(db));
        if (table is null) throw new ArgumentNullException(nameof(table));
        options ??= ImportOptions.Default;

        string name = ResolveTableName(tableName, table.TableName, "Table1");
        string? pk  = primaryKey ?? InferPrimaryKey(table);

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
                col = new ColumnBuilder(dc.ColumnName, DataType.Memo).Build();
                fellBack = true;
            }
            columns.Add(col);
            columnSources.Add(dc);
            stringFallback.Add(fellBack);
        }

        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"DataTable '{table.TableName}' has no columns that map to a Jet type.");

        var t = db.CreateTable(name, columns, primaryKey: pk);

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
    /// Imports every <see cref="DataTable"/> in <paramref name="dataSet"/> in
    /// declaration order. Returns the created <see cref="Table"/>s in the same order.
    /// </summary>
    public static IReadOnlyList<Table> ImportTables(this Database db, DataSet dataSet,
        ImportOptions? options = null)
    {
        if (db is null)      throw new ArgumentNullException(nameof(db));
        if (dataSet is null) throw new ArgumentNullException(nameof(dataSet));

        var result = new List<Table>(dataSet.Tables.Count);
        foreach (DataTable dt in dataSet.Tables)
            result.Add(db.ImportTable(dt, options: options));
        return result;
    }

    /// <summary>
    /// Imports <paramref name="items"/> as a new table. Columns are inferred
    /// from <c>T</c>'s public instance properties — see
    /// <see cref="DatabaseImporter"/> for attribute handling. The table name
    /// is taken from <paramref name="tableName"/> if supplied, otherwise from
    /// <see cref="TableAttribute"/> on <c>T</c>, otherwise from <c>T</c>'s
    /// CLR name.
    /// </summary>
    public static Table ImportTable<T>(this Database db, IEnumerable<T> items,
        string? tableName = null, string? primaryKey = null,
        ImportOptions? options = null)
    {
        if (db is null)    throw new ArgumentNullException(nameof(db));
        if (items is null) throw new ArgumentNullException(nameof(items));
        options ??= ImportOptions.Default;

        // Table name precedence: explicit parameter > [Table("X")] on T > typeof(T).Name.
        // [Table].Schema is ignored — Access has no schemas.
        string? attrName = typeof(T).GetCustomAttribute<TableAttribute>()?.Name;
        string name = ResolveTableName(tableName, attrName ?? typeof(T).Name, typeof(T).Name);

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

        string? pk  = primaryKey ?? pkFromAttr;

        var t = db.CreateTable(name, columns, primaryKey: pk);

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

    // ── Append-into-existing-table helpers ───────────────────────────────────

    /// <summary>
    /// Case-insensitive check for whether <paramref name="db"/> already has a
    /// table named <paramref name="name"/>. <see cref="Database.ListTables"/>
    /// already returns the case as it appears in MSysObjects, so we compare
    /// with <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    private static bool TableExists(Database db, string name)
    {
        foreach (var existing in db.ListTables())
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
                ? target.Columns.FirstOrDefault(c => c.IsAutoNumber && c.DataType == DataType.Long)
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
    /// <exception cref="InvalidOperationException">
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
                    throw new InvalidOperationException(
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

    private static string? InferPrimaryKey(DataTable table)
    {
        // Only single-column PKs map cleanly onto Jet primary indexes.
        if (table.PrimaryKey.Length == 1) return table.PrimaryKey[0].ColumnName;
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
                col = new ColumnBuilder(colName, DataType.Memo).Build();
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
/// Optional knobs for <see cref="DatabaseImporter"/>. Use the default instance
/// for "just work" behaviour; supply your own to intercept unmappable columns
/// or force them through as text instead of dropping.
/// </summary>
public sealed class ImportOptions
{
    /// <summary>Default options: silently drop unmappable columns.</summary>
    public static ImportOptions Default { get; } = new();

    /// <summary>
    /// Invoked once per column/property whose CLR type can't be represented in
    /// Jet, *regardless* of <see cref="FallbackUnmappableToString"/>. Receives
    /// (columnName, clrType). Use this to log or surface the event; the
    /// disposition (drop vs. fallback) is controlled by the other flag.
    /// </summary>
    public Action<string, Type>? OnUnmappableColumn { get; init; }

    /// <summary>
    /// When <c>true</c>, columns whose CLR type can't be mapped are stored as
    /// <see cref="DataType.Memo"/> columns containing <c>value.ToString()</c>
    /// (null stays null). When <c>false</c> (default), they are dropped.
    /// Useful for forcing through <see cref="Uri"/>, JSON blobs, or custom
    /// classes that have a meaningful <c>ToString()</c>.
    /// </summary>
    public bool FallbackUnmappableToString { get; init; }

    /// <summary>
    /// When <c>true</c> and the import target name already exists in the
    /// database, the importer appends rows to the existing table instead of
    /// throwing. Source columns / POCO properties are matched to the existing
    /// table's columns by name (case-insensitive). Unmatched source columns are
    /// silently dropped; unmatched target columns are left null on inserted
    /// rows. The existing table's schema, primary key, and indexes are
    /// preserved untouched.
    /// <para>When <c>false</c> (default), importing to a name that already
    /// exists throws <see cref="InvalidOperationException"/> — matches the
    /// pre-existing behaviour.</para>
    /// </summary>
    public bool AppendIfExists { get; init; }

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
    public bool KeepIdentity { get; init; }
}
