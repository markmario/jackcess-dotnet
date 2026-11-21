using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Reflection;

namespace JackcessDotNet;

/// <summary>
/// Reads a table into a <see cref="DataTable"/>, every table of a database into a <see cref="DataSet"/>, or a
/// table's rows into objects of a type: the counterpart of <see cref="DatabaseImporter"/>.
/// <para>
/// A value has the CLR type a row read from the table gives it (<see cref="Table.Rows"/>):
/// <see cref="DataType.Boolean"/> is <see cref="bool"/>; <see cref="DataType.Byte"/> is <see cref="byte"/>;
/// <see cref="DataType.Int16"/> is <see cref="short"/>; <see cref="DataType.Int32"/> is <see cref="int"/>;
/// <see cref="DataType.Int64"/> is <see cref="long"/>; <see cref="DataType.Single"/> is <see cref="float"/>;
/// <see cref="DataType.Double"/> is <see cref="double"/>; <see cref="DataType.Currency"/> and
/// <see cref="DataType.Decimal"/> are <see cref="decimal"/>; <see cref="DataType.DateTime"/> and
/// <see cref="DataType.DateTimeExtended"/> are <see cref="DateTime"/>; <see cref="DataType.Text"/> and
/// <see cref="DataType.Memo"/> are <see cref="string"/>; <see cref="DataType.Binary"/> and
/// <see cref="DataType.OleObject"/> are <c>byte[]</c>; <see cref="DataType.Guid"/> is
/// <see cref="Guid"/>.
/// </para>
/// </summary>
public static class DatabaseExporter
{
    // ── DataTable / DataSet ──────────────────────────────────────────────────

    /// <summary>
    /// Reads the whole table into a new <see cref="DataTable"/> of the table's name, with a column for each of
    /// the table's columns, in their order, and a row for each of its rows.
    /// <para>
    /// A <see cref="DataColumn"/> has its column's name and the CLR type of its values; a Text column's
    /// <see cref="Column.MaxLength"/> is its <see cref="DataColumn.MaxLength"/>. A null is <see cref="DBNull.Value"/>. Every
    /// column allows nulls, whether or not the table's column is required. When the table's primary key is one
    /// column and no row has a null in it, that column is the <see cref="DataTable.PrimaryKey"/>; a primary key
    /// of several columns is not carried over. The rows are accepted: each is
    /// <see cref="DataRowState.Unchanged"/>.
    /// </para>
    /// </summary>
    /// <param name="table">The table to read.</param>
    /// <returns>A <see cref="DataTable"/> holding the table's rows, which the caller owns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    public static DataTable ToDataTable(this Table table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));

        return BuildDataTable(table);
    }

    /// <summary>
    /// Reads every table of the database - not its system tables - into a new <see cref="DataSet"/>: a
    /// <see cref="DataTable"/> for each, as <see cref="ToDataTable"/> makes it, in the order
    /// <see cref="Database.GetTableNames"/> names them. The database's relationships are not carried over.
    /// </summary>
    /// <param name="database">The database to read.</param>
    /// <returns>A <see cref="DataSet"/> holding the tables, which the caller owns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="database"/> is disposed.</exception>
    public static DataSet ToDataSet(this Database database)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));

        var ds = new DataSet();
        foreach (var name in database.GetTableNames())
            ds.Tables.Add(BuildDataTable(database.GetTable(name)));
        return ds;
    }

    private static DataTable BuildDataTable(Table table)
    {
        var dt = new DataTable(table.Name);

        // Columns first — order matches the on-disk TDEF.
        // AllowDBNull stays true even when the Access column is marked Required:
        // a row read from a file can hold a null there all the same, and would
        // otherwise be rejected by the DataTable on Add. The exported
        // DataTable is a data view, not a schema mirror.
        foreach (var col in table.Columns)
        {
            Type clr = MapToClrType(col.DataType);
            var dc = new DataColumn(col.Name, clr);
            if (col.DataType == DataType.Text && col.LengthInBytes > 0)
                dc.MaxLength = col.MaxLength;
            dt.Columns.Add(dc);
        }

        // Materialise rows first so we can decide whether the PK is safe to set:
        // assigning DataTable.PrimaryKey flips the column's AllowDBNull back to
        // false, which then rejects rows whose PK value is missing (e.g. some
        // MSysObjects rows that come back with a null Id from Rows).
        var sourceRows = table.Rows.ToList();

        // Single-column PK round-trip — multi-column PKs are skipped (DataTable
        // supports them, but Jet only round-trips one PK per table cleanly).
        var pkIndex = table.Indexes.FirstOrDefault(i => i.IsPrimaryKey);
        if (pkIndex is not null && pkIndex.Columns.Count == 1)
        {
            string pkName = pkIndex.Columns[0].Column.Name;
            bool allHavePk = sourceRows.All(r =>
                r.TryGetValue(pkName, out var v) && v is not null);
            if (allHavePk && dt.Columns[pkName] is DataColumn pkCol)
                dt.PrimaryKey = new[] { pkCol };
        }

        foreach (var row in sourceRows)
        {
            var dr = dt.NewRow();
            foreach (DataColumn dc in dt.Columns)
            {
                row.TryGetValue(dc.ColumnName, out var v);
                dr[dc] = v ?? (object)DBNull.Value;
            }
            dt.Rows.Add(dr);
        }
        dt.AcceptChanges();   // freshly loaded rows aren't dirty
        return dt;
    }

    // ── IEnumerable<T> ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the table's rows as objects of <typeparamref name="T"/>, one for each row, in the order of
    /// <see cref="Table.Rows"/>.
    /// <para>
    /// A column's value is set on the public instance property of its name, whatever its case, or on the one a
    /// <see cref="ColumnAttribute"/> names for it. A property without a setter, an indexer and a property with
    /// <see cref="NotMappedAttribute"/> are not set; a column with no such property is not read into anything,
    /// and a property with no column keeps the value a new <typeparamref name="T"/> gives it, as does one whose
    /// column is null in the row.
    /// </para>
    /// <para>
    /// A value is converted to its property's type when it is not of that type already: to the underlying type
    /// of a nullable property; to an enum from its integer; to a <see cref="DateTimeOffset"/> from a
    /// <see cref="DateTime"/> taken as UTC; to a <see cref="Guid"/> from its text; to a <see cref="string"/> by
    /// <see cref="object.ToString"/>; and otherwise as <see cref="Convert.ChangeType(object, Type)"/> converts
    /// it.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type of object to make for each row, which has a public parameterless constructor.</typeparam>
    /// <param name="table">The table to read.</param>
    /// <returns>
    /// The objects. The table is read as the result is enumerated, a row at a time, and read again each time it
    /// is enumerated. Enumerating throws <see cref="InvalidCastException"/>, <see cref="FormatException"/> or
    /// <see cref="OverflowException"/> at a value that cannot be converted to its property's type.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    public static IEnumerable<T> ReadRows<T>(this Table table)
        where T : new()
    {
        if (table is null) throw new ArgumentNullException(nameof(table));

        return EnumerateAsCollection<T>(table);
    }

    private static IEnumerable<T> EnumerateAsCollection<T>(Table table) where T : new()
    {
        var bindings = BuildPropertyBindings<T>(table);

        foreach (var row in table.Rows)
        {
            var instance = new T();
            foreach (var (colName, prop) in bindings)
            {
                if (!row.TryGetValue(colName, out var value) || value is null) continue;
                prop.SetValue(instance, ConvertForProperty(value, prop.PropertyType));
            }
            yield return instance;
        }
    }

    // Each property to the column a lookup of its column's name finds: of exactly that name, or else of one Access
    // takes as one with it (ObjectNames), by the table's own spelling of it.
    private static List<(string ColumnName, PropertyInfo Property)> BuildPropertyBindings<T>(Table table)
    {
        var bindings = new List<(string, PropertyInfo)>();

        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<NotMappedAttribute>() is not null) continue;

            string colName = p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name;
            if (table.Definition.FindColumn(colName) is { } column)
                bindings.Add((column.Name, p));
        }
        return bindings;
    }

    // ── CLR / Jet bridging ───────────────────────────────────────────────────

    /// <summary>
    /// Reverse of <see cref="TypeMapper.MapClrType"/>. Picks the CLR type that
    /// the row decoder actually produces for each Jet <see cref="DataType"/>.
    /// Unknown / complex types fall back to <c>object</c>.
    /// </summary>
    internal static Type MapToClrType(DataType dt) => dt switch
    {
        DataType.Boolean       => typeof(bool),
        DataType.Byte          => typeof(byte),
        DataType.Int16           => typeof(short),
        DataType.Int32          => typeof(int),
        DataType.Currency         => typeof(decimal),
        DataType.Single         => typeof(float),
        DataType.Double        => typeof(double),
        DataType.DateTime => typeof(DateTime),
        DataType.Binary        => typeof(byte[]),
        DataType.Text          => typeof(string),
        DataType.Memo          => typeof(string),
        DataType.OleObject           => typeof(byte[]),
        DataType.Guid          => typeof(Guid),
        DataType.Decimal       => typeof(decimal),
        DataType.Complex       => typeof(int),
        DataType.Int64        => typeof(long),
        DataType.DateTimeExtended => typeof(DateTime),
        _                      => typeof(object),
    };

    /// <summary>
    /// Coerces a boxed value coming back from the row decoder into whatever
    /// <paramref name="targetType"/> the receiving property declares. Handles
    /// Nullable&lt;T&gt;, enums (via underlying type), DateTimeOffset (assumed
    /// UTC for naked DateTime), Guid parsed from string, and the usual
    /// widening/narrowing via <see cref="Convert.ChangeType(object?,Type)"/>.
    /// </summary>
    private static object? ConvertForProperty(object value, Type targetType)
    {
        Type t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (t.IsInstanceOfType(value)) return value;

        if (t.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(t);
            return Enum.ToObject(t, Convert.ChangeType(value, underlying));
        }

        if (t == typeof(DateTimeOffset))
            return value is DateTime dt
                ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
                : DateTimeOffset.Parse(value.ToString()!);

        if (t == typeof(Guid))
            return value is Guid g ? g : Guid.Parse(value.ToString()!);

        if (t == typeof(string))
            return value as string ?? value.ToString();

        return Convert.ChangeType(value, t);
    }
}
