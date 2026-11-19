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
/// An import is whole or not at all. Every value is converted to what its column holds before anything is written,
/// and a value no column holds exactly is refused then; what is written is written inside one undo scope, so a row
/// the database refuses part-way - a duplicate in a key, a null in a required column - takes back the table and
/// every row before it. A refused import leaves the file byte for byte as it was.
/// </para>
/// <para>
/// A column a table is made with holds every value of its source exactly. Its type follows the CLR type of the
/// source's column or property, and a length, precision or scale the values need follows the values:
/// <see cref="bool"/> is <see cref="DataType.Boolean"/> (Yes/No), which holds no null, so a null is refused;
/// <see cref="byte"/> is <see cref="DataType.Byte"/>; <see cref="sbyte"/> and <see cref="short"/> are
/// <see cref="DataType.Int16"/> (Integer), Access's Byte being 0 to 255; <see cref="ushort"/> and <see cref="int"/>
/// are <see cref="DataType.Int32"/> (Long Integer); <see cref="uint"/> and <see cref="long"/> are
/// <see cref="DataType.Int64"/> (Large Number) in a file of Access 2016's format or a later one, and
/// <see cref="DataType.Decimal"/>s of 10 and 19 digits in a file of an older format - an .mdb, or an .accdb of Access
/// 2007's or 2010's - which has no Large Number; <see cref="ulong"/> is a <see cref="DataType.Decimal"/> of 20 digits, past a
/// Large Number's range; <see cref="float"/> is <see cref="DataType.Single"/>; <see cref="double"/> is
/// <see cref="DataType.Double"/>; <see cref="decimal"/> is a <see cref="DataType.Decimal"/> whose scale is the most
/// places after the point a value has and whose precision is the digits the values need, 18 at least (Currency
/// keeps four places, and would round the rest); <see cref="DateTime"/> and <see cref="DateTimeOffset"/> are
/// <see cref="DataType.DateTime"/>, a <see cref="DateTimeOffset"/> stored as its UTC date and time; <see cref="Guid"/>
/// is <see cref="DataType.Guid"/>. A <see cref="string"/> or a <see cref="char"/> is <see cref="DataType.Text"/> of
/// the source's maximum length, or of 255 characters when it has none, when every value fits that, and
/// <see cref="DataType.Memo"/> otherwise. A <c>byte[]</c> is <see cref="DataType.Binary"/> of the source's maximum
/// length when it has one of 255 bytes or fewer and every value fits it, and <see cref="DataType.OleObject"/>
/// otherwise. A nullable type is its underlying type, and an enum its underlying integer type.
/// </para>
/// <para>
/// An import does not change the file's format. A Large Number column makes a file of an older format one of Access
/// 2016's (<see cref="Database.CreateTable(string, IEnumerable{ColumnDefinition}, string[])"/>), which Access 2010
/// and 2013 do not open; the importer makes one only in a file of that format already - one created as
/// <see cref="FileFormat.Access2016"/> or later, or given such a column before.
/// </para>
/// <para>
/// A column takes no null - Access's Required property - when the source says so: a <see cref="DataColumn"/> whose
/// <see cref="DataColumn.AllowDBNull"/> is false, and a property with <see cref="RequiredAttribute"/> or of a value
/// type that is not nullable. An AutoNumber and a Yes/No column have a value in every row and are given no such
/// property.
/// </para>
/// <para>
/// A key on <see cref="string"/>s that run past 255 characters is on a Memo column, as Access keeps one: its index keys
/// are a value's first 255 characters, so two values equal in those are one key, and the second is refused as a
/// duplicate (<see cref="ConstraintViolationException"/>), as Access refuses it.
/// </para>
/// <para>
/// A date is held to the millisecond, which is what a Date/Time value is written and read at; a date before
/// 1 January 100, the first a Date/Time column holds - <see cref="DateTime.MinValue"/> among them - is refused.
/// </para>
/// <para>
/// A column or property of any other type is left out, unless
/// <see cref="ImportOptions.FallbackUnmappableToString"/> has it stored as text;
/// <see cref="ImportOptions.OnUnmappableColumn"/> is called for each either way.
/// </para>
/// <para>
/// A table that is already there is refused, unless <see cref="ImportOptions.AppendIfExists"/> has the rows
/// added to it.
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
    /// <exception cref="ArgumentException">A value of the source is one no column holds exactly. Nothing is imported.</exception>
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
    /// rows, or adds its rows to a table that is already there - all of them, or none.
    /// <para>
    /// The table is named <see cref="ImportOptions.TableName"/>, or else <see cref="DataTable.TableName"/>, or
    /// else <c>Table1</c>. A column takes its name and its type from the source's column, as
    /// <see cref="DatabaseImporter"/> describes, the length of text and bytes from its
    /// <see cref="DataColumn.MaxLength"/> and its values, and is required when its
    /// <see cref="DataColumn.AllowDBNull"/> is false; an <see cref="int"/> column with
    /// <see cref="DataColumn.AutoIncrement"/> is an AutoNumber. The primary key is
    /// <see cref="ImportOptions.PrimaryKey"/>, or else the source's <see cref="DataTable.PrimaryKey"/>, of one column
    /// or several, in its order. The table's rows keep the values the source has for an AutoNumber column, and the
    /// table numbers later rows on from the highest. Rows the source has marked deleted are not imported.
    /// </para>
    /// <para>
    /// When the database has a table of that name already - compared without regard to case - and
    /// <see cref="ImportOptions.AppendIfExists"/> is set, the rows are added to it and nothing of the table is
    /// changed: a source column is matched to the table's column of the same name, whatever its case, and each of
    /// its values is converted to that column's type when the column holds it exactly
    /// (<see cref="ImportOptions.AppendIfExists"/> says which conversions those are); a source column the table does
    /// not have is left out, and a column of the table the source does not have is null in the added rows.
    /// <see cref="ImportOptions.PrimaryKey"/> is not used. <see cref="ImportOptions.KeepIdentity"/> says whether
    /// the rows keep the source's AutoNumber values.
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
    /// A value of the source is one its column does not hold exactly - out of its range, with more digits or places
    /// than it keeps, a null in a Yes/No column, a date before 1 January 100 - or a <see cref="decimal"/> column's
    /// values need more than the 28 digits a Decimal holds; or <see cref="ImportOptions.PrimaryKey"/> names a column
    /// the new table does not have, or holds an empty name. Nothing is imported.
    /// </exception>
    /// <exception cref="ColumnValueTooLongException">
    /// Adding to a table, a text value is longer than its column. Nothing is imported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The database has a table of that name already and <see cref="ImportOptions.AppendIfExists"/> is not set;
    /// no column of <paramref name="table"/> has a type a column of a database can have; or, adding to a table,
    /// no column of <paramref name="table"/> has the name of one of its columns. Nothing is imported.
    /// </exception>
    /// <exception cref="ConstraintViolationException">
    /// A row breaks a rule of the table: a duplicate in its primary key or a unique index, a null in a column
    /// of its primary key, of an index that requires its values or that is required, or a foreign key with no row
    /// to refer to. Nothing is imported.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The database is an Access 97 file, which is only read; or the primary key is on a column whose index keys this
    /// library does not write: a Decimal - a <see cref="decimal"/>, and in a file before Access 2016's format a
    /// <see cref="long"/> or a <see cref="uint"/> - or a Binary or OLE Object, a <c>byte[]</c>. Nothing is imported.
    /// </exception>
    public static Table ImportTable(this Database database, DataTable table, ImportOptions options)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        if (table is null)    throw new ArgumentNullException(nameof(table));
        if (options is null)  throw new ArgumentNullException(nameof(options));
        return ImportDataTable(database, table, options);
    }

    private static Table ImportDataTable(Database db, DataTable table, ImportOptions options)
    {
        db.File.EnsureWritable();
        return Write(db, new[] { PlanDataTable(db, table, options, nameof(table)) })[0];
    }

    /// <summary>
    /// Imports every <see cref="DataTable"/> of a <see cref="DataSet"/>, each as a table named for it, with its
    /// primary key: <see cref="ImportTables(Database, DataSet, ImportOptions)"/> with no option set.
    /// </summary>
    /// <param name="database">The database to import into.</param>
    /// <param name="dataSet">The tables to import.</param>
    /// <returns>The tables created, in the order the <see cref="DataSet"/> has them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="dataSet"/> is null.</exception>
    /// <exception cref="ArgumentException">A value of one of the tables is one no column holds exactly. Nothing is imported.</exception>
    /// <exception cref="InvalidOperationException">
    /// The database has a table with the name of one to import already, or no column of one has a type a column
    /// of a database can have. Nothing is imported.
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
    /// The tables are imported together, all of them or none: every table's values are converted before anything
    /// is written, and a table refused, or a row refused, takes back the tables before it.
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
    /// <see cref="ImportOptions.TableName"/> or <see cref="ImportOptions.PrimaryKey"/> is set; or a value of a
    /// table is one its column does not hold exactly. Nothing is imported.
    /// </exception>
    /// <exception cref="ColumnValueTooLongException">
    /// Adding to a table, a text value is longer than its column. Nothing is imported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A table is refused as <see cref="ImportTable(Database, DataTable, ImportOptions)"/> refuses one. Nothing is
    /// imported.
    /// </exception>
    /// <exception cref="ConstraintViolationException">A row breaks a rule of its table. Nothing is imported.</exception>
    /// <exception cref="NotSupportedException">
    /// The database is an Access 97 file, which is only read; it is an .mdb and a table has a <see cref="long"/>
    /// column, which only an .accdb holds; or a primary key is on a column whose index keys this library does not
    /// write. Nothing is imported.
    /// </exception>
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
        // A file that is only read refuses an import before its values are looked at; a DataSet of no tables
        // changes nothing, and is no change to refuse.
        if (dataSet.Tables.Count > 0) db.File.EnsureWritable();
        var plans = new List<TablePlan>(dataSet.Tables.Count);
        foreach (DataTable dt in dataSet.Tables)
            plans.Add(PlanDataTable(db, dt, options, nameof(dataSet)));
        return Write(db, plans);
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
    /// <exception cref="ArgumentException">A value of the objects is one no column holds exactly. Nothing is imported.</exception>
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
    /// there - all of them, or none.
    /// <para>
    /// The table is named <see cref="ImportOptions.TableName"/>, or else the name a <see cref="TableAttribute"/>
    /// on <typeparamref name="T"/> gives (its schema is not used), or else <typeparamref name="T"/>'s own name.
    /// Every public instance property that can be read is a column, in the order reflection lists them, which is
    /// the order they are declared in, but an indexer and a property with <see cref="NotMappedAttribute"/>. A
    /// column is named for its property, or as its <see cref="ColumnAttribute"/> names it, and takes its type as
    /// <see cref="DatabaseImporter"/> describes; <see cref="MaxLengthAttribute"/> is its length, for text and bytes;
    /// it is required when its property has <see cref="RequiredAttribute"/> or is of a value type that is not
    /// nullable; and an <see cref="int"/> property with <see cref="DatabaseGeneratedAttribute"/> of
    /// <see cref="DatabaseGeneratedOption.Identity"/> is an AutoNumber. The primary key is
    /// <see cref="ImportOptions.PrimaryKey"/>, or else the properties with <see cref="KeyAttribute"/>: a key of
    /// several columns when there are several, in the order of their <see cref="ColumnAttribute.Order"/> where they
    /// give one, and then in the order they are declared. An AutoNumber property that has a value keeps it; one
    /// that is 0 is numbered by the table, past every value the objects bring.
    /// </para>
    /// <para>
    /// When the database has a table of that name already - compared without regard to case - and
    /// <see cref="ImportOptions.AppendIfExists"/> is set, the rows are added to it and nothing of the table is
    /// changed: a property is matched to the table's column of the same name, whatever its case, and each of its
    /// values is converted to that column's type when the column holds it exactly; a property the table has no
    /// column for is left out, and a column no property matches is null in the added rows.
    /// <see cref="ImportOptions.PrimaryKey"/> is not used. <see cref="ImportOptions.KeepIdentity"/> says whether
    /// the rows keep the objects' AutoNumber values.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type whose public properties are the table's columns.</typeparam>
    /// <param name="database">The database to import into.</param>
    /// <param name="items">
    /// The objects to import, each a row. A null among them is passed over. They are read once, every value of
    /// every object, before anything is written.
    /// </param>
    /// <param name="options">How to import them.</param>
    /// <returns>The table created, or the table the rows were added to.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="database"/>, <paramref name="items"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A value of the objects is one its column does not hold exactly - out of its range, with more digits or
    /// places than it keeps, a null in a Yes/No column, a date before 1 January 100 - or a <see cref="decimal"/>
    /// property's values need more than the 28 digits a Decimal holds; or <see cref="ImportOptions.PrimaryKey"/>
    /// names a column the new table does not have, or holds an empty name. Nothing is imported.
    /// </exception>
    /// <exception cref="ColumnValueTooLongException">
    /// Adding to a table, a text value is longer than its column. Nothing is imported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The database has a table of that name already and <see cref="ImportOptions.AppendIfExists"/> is not set;
    /// no property of <typeparamref name="T"/> has a type a column of a database can have; or, adding to a
    /// table, no property of <typeparamref name="T"/> has the name of one of its columns. Nothing is imported.
    /// </exception>
    /// <exception cref="ConstraintViolationException">
    /// A row breaks a rule of the table: a duplicate in its primary key or a unique index, a null in a column
    /// of its primary key, of an index that requires its values or that is required, or a foreign key with no row
    /// to refer to. Nothing is imported.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The database is an Access 97 file, which is only read; or the primary key is on a column whose index keys this
    /// library does not write: a Decimal - a <see cref="decimal"/>, and in a file before Access 2016's format a
    /// <see cref="long"/> or a <see cref="uint"/> - or a Binary or OLE Object, a <c>byte[]</c>. Nothing is imported.
    /// </exception>
    public static Table ImportTable<T>(this Database database, IEnumerable<T> items, ImportOptions options)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        if (items is null)    throw new ArgumentNullException(nameof(items));
        if (options is null)  throw new ArgumentNullException(nameof(options));
        return ImportCollection(database, items, options);
    }

    private static Table ImportCollection<T>(Database db, IEnumerable<T> items, ImportOptions options)
    {
        db.File.EnsureWritable();
        return Write(db, new[] { PlanCollection(db, items, options) })[0];
    }

    // ── What an import is to write ───────────────────────────────────────────

    /// <summary>
    /// One table of an import, worked out and checked with nothing written: the table to make (its columns and key)
    /// or the table that is there, and the rows to insert, every value already as its column holds it.
    /// </summary>
    private sealed class TablePlan
    {
        internal TablePlan(string name, Table? existing, IReadOnlyList<Column>? columns, IReadOnlyList<string>? primaryKey,
                           List<Row> rows, bool keepIdentity)
        {
            Name         = name;
            Existing     = existing;
            Columns      = columns;
            PrimaryKey   = primaryKey;
            Rows         = rows;
            KeepIdentity = keepIdentity;
        }

        internal string Name { get; }
        internal Table? Existing { get; }
        internal IReadOnlyList<Column>? Columns { get; }
        internal IReadOnlyList<string>? PrimaryKey { get; }
        internal List<Row> Rows { get; }
        internal bool KeepIdentity { get; }
    }

    /// <summary>
    /// Writes the tables an import has worked out: each table made, or found, and its rows inserted, inside one undo
    /// scope. When anything fails - a row the database refuses, a disk that fills - what was written is taken back,
    /// and a table made is forgotten: the file is as it was.
    /// </summary>
    private static IReadOnlyList<Table> Write(Database db, IReadOnlyList<TablePlan> plans)
    {
        var tables = new List<Table>(plans.Count);
        if (plans.Count == 0) return tables;

        var made = new List<Table>();
        using var undo = db.File.BeginUndo();
        try
        {
            foreach (var plan in plans)
            {
                var table = plan.Existing ?? db.CreateTableOf(plan.Name, plan.Columns!, plan.PrimaryKey);
                if (plan.Existing is null) made.Add(table);
                InsertRows(table, plan.Rows, plan.KeepIdentity);
                tables.Add(table);
            }
            undo.Commit();
        }
        catch
        {
            undo.Dispose();
            foreach (var table in made) db.ForgetTakenBack(table);
            throw;
        }
        return tables;
    }

    // ── A DataTable ──────────────────────────────────────────────────────────

    private static TablePlan PlanDataTable(Database db, DataTable table, ImportOptions options, string paramName)
    {
        string name = ResolveTableName(options.TableName, table.TableName, "Table1");
        var sourceRows = table.Rows.Cast<DataRow>()
            .Select((row, index) => (Row: row, Index: index))
            .Where(r => r.Row.RowState != DataRowState.Deleted)
            .ToList();
        object? Cell(DataRow row, DataColumn column) => row[column] is DBNull ? null : row[column];
        string Where(int index) => $"'{table.TableName}' row {index} (DataTable.Rows[{index}])";

        // Pre-existing target: append rows into the existing schema instead of creating.
        if (TableExists(db, name))
        {
            if (!options.AppendIfExists)
                throw new InvalidOperationException(
                    $"Table '{name}' already exists. Set ImportOptions.AppendIfExists = true to append rows to it.");
            var existing = db.GetTable(name);
            var targetByName = existing.Columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
            var pairs = new List<(DataColumn Source, Column Target)>();
            foreach (DataColumn dc in table.Columns)
                if (targetByName.TryGetValue(dc.ColumnName, out var target))
                    pairs.Add((dc, target));
            if (pairs.Count == 0)
                throw new InvalidOperationException(
                    $"DataTable '{table.TableName}' has no columns that match the existing target table '{existing.Name}'.");
            EnsureOneSourceEach(pairs.Select(p => (p.Source.ColumnName, p.Target)), $"DataTable '{table.TableName}'", paramName);

            var appended = new List<Row>(sourceRows.Count);
            foreach (var (dr, index) in sourceRows)
            {
                var row = new Row();
                foreach (var (source, target) in pairs)
                {
                    // The table numbers every row when the import does not keep the source's numbers.
                    if (target.IsAutoNumber && !options.KeepIdentity) continue;
                    row[target.Name] = Converted(AsText(Cell(dr, source), target, options), target, Where(index), paramName);
                }
                appended.Add(row);
            }
            return new TablePlan(existing.Name, existing, columns: null, primaryKey: null, appended, options.KeepIdentity);
        }

        // The columns, each worked out from its source column and the values it holds. Unmappable columns are
        // either dropped or, when FallbackUnmappableToString is set, stored as Memo via ToString(). A Large Number only
        // in a file of a format that has it: an import does not change a file's format.
        bool largeNumber = DatabaseHeader.HasLargeNumber(db.File);
        var columns = new List<Column>(table.Columns.Count);
        var sources = new List<(DataColumn Source, bool AsText)>(table.Columns.Count);
        foreach (DataColumn dc in table.Columns)
        {
            var values = sourceRows.Select(r => Cell(r.Row, dc)).ToList();
            bool isAuto = dc.AutoIncrement && dc.DataType == typeof(int);
            var col = Mapped(() => TypeMapper.ColumnFor(dc.ColumnName, dc.DataType, dc.MaxLength > 0 ? dc.MaxLength : null, isAuto,
                                                       required: !dc.AllowDBNull, values, largeNumber),
                             $"'{table.TableName}'.{dc.ColumnName}", paramName);
            bool asText = false;
            if (col is null)
            {
                options.OnUnmappableColumn?.Invoke(dc.ColumnName, dc.DataType);
                if (!options.FallbackUnmappableToString) continue;
                col = new ColumnDefinition(dc.ColumnName, DataType.Memo) { IsRequired = !dc.AllowDBNull }.ToColumn(paramName);
                asText = true;
            }
            columns.Add(col);
            sources.Add((dc, asText));
        }

        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"DataTable '{table.TableName}' has no columns that map to a Jet type.");
        EnsureNamesApart(columns, $"DataTable '{table.TableName}'", paramName);

        // The key is read here, for a table that is made: rows added to a table that is there take no key, and the
        // option is not looked at for them.
        var pk = PrimaryKeyOf(options) ?? (table.PrimaryKey.Length > 0 ? table.PrimaryKey.Select(c => c.ColumnName).ToList() : null);
        EnsureKeysWritten(columns, pk, $"DataTable '{table.TableName}'");

        // An AutoIncrement column's values are the rows' own - other tables of a DataSet refer to them - so the new
        // table takes them as given, and its counter ends at the highest: rows added later, here or in Access, are
        // numbered on from it.
        var rows = new List<Row>(sourceRows.Count);
        foreach (var (dr, index) in sourceRows)
        {
            var row = new Row();
            for (int i = 0; i < columns.Count; i++)
            {
                object? raw = Cell(dr, sources[i].Source);
                if (sources[i].AsText && raw is not null) raw = raw.ToString();
                row[columns[i].Name] = Converted(raw, columns[i], Where(index), paramName);
            }
            rows.Add(row);
        }
        return new TablePlan(name, existing: null, columns, pk, rows, keepIdentity: true);
    }

    // ── Objects ──────────────────────────────────────────────────────────────

    private static TablePlan PlanCollection<T>(Database db, IEnumerable<T> items, ImportOptions options)
    {
        const string paramName = nameof(items);

        // Table name precedence: ImportOptions.TableName > [Table("X")] on T > typeof(T).Name.
        // [Table].Schema is ignored — Access has no schemas.
        string? attrName = typeof(T).GetCustomAttribute<TableAttribute>()?.Name;
        string name = ResolveTableName(options.TableName, attrName ?? typeof(T).Name, typeof(T).Name);
        string Where(int index) => $"the {typeof(T).Name} at index {index}";

        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<NotMappedAttribute>() is null)
            .ToList();

        // Every object is read once, before anything is written, and of each the properties the import uses: each
        // such property's values are read once, for its column and its rows.
        var objects = items.Select((item, index) => (Item: item, Index: index)).Where(x => x.Item is not null).ToList();
        object?[] ValuesOf(PropertyInfo property) => objects.Select(o => property.GetValue(o.Item)).ToArray();

        // Pre-existing target: append rows; bind T's properties to the existing schema by name (case-insensitive).
        // Source properties that don't have a matching target column are silently skipped.
        if (TableExists(db, name))
        {
            if (!options.AppendIfExists)
                throw new InvalidOperationException(
                    $"Table '{name}' already exists. Set ImportOptions.AppendIfExists = true to append rows to it.");
            var existing = db.GetTable(name);
            var targetByName = existing.Columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
            var matched = properties.Select(p => (Name: ColumnNameOf(p), Property: p))
                                    .Where(m => targetByName.ContainsKey(m.Name))
                                    .Select(m => (m.Name, m.Property, Target: targetByName[m.Name]))
                                    .ToList();
            if (matched.Count == 0)
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' has no properties that match the existing target table '{existing.Name}'.");
            EnsureOneSourceEach(matched.Select(m => (m.Name, m.Target)), $"Type '{typeof(T).Name}'", paramName);
            var pairs = matched.Select(m => (Values: ValuesOf(m.Property), m.Target)).ToList();

            var appended = new List<Row>(objects.Count);
            for (int r = 0; r < objects.Count; r++)
            {
                var row = new Row();
                foreach (var (values, target) in pairs)
                {
                    object? raw = values[r];
                    // An identity property still at 0 is an object the database has yet to number; and the table
                    // numbers every row when the import does not keep the source's numbers.
                    if (target.IsAutoNumber && (IsUnnumbered(raw) || !options.KeepIdentity)) continue;
                    row[target.Name] = Converted(AsText(raw, target, options), target, Where(objects[r].Index), paramName);
                }
                appended.Add(row);
            }
            return new TablePlan(existing.Name, existing, columns: null, primaryKey: null, appended, options.KeepIdentity);
        }

        // A Large Number only in a file of a format that has it: an import does not change a file's format.
        bool largeNumber = DatabaseHeader.HasLargeNumber(db.File);
        var columns = new List<Column>();
        var cells   = new List<(object?[] Values, bool AsText)>();
        var keys    = new List<(string Name, int Order, int Position)>();
        foreach (var p in properties)
        {
            string colName = ColumnNameOf(p);
            int? maxLen = p.GetCustomAttribute<MaxLengthAttribute>()?.Length;
            bool isAuto = p.GetCustomAttribute<DatabaseGeneratedAttribute>() is { DatabaseGeneratedOption: DatabaseGeneratedOption.Identity }
                          && p.PropertyType == typeof(int);
            // A property takes no null when it says so, or when its type has none.
            bool required = p.GetCustomAttribute<RequiredAttribute>() is not null
                            || (p.PropertyType.IsValueType && Nullable.GetUnderlyingType(p.PropertyType) is null);

            Column? col;
            object?[] values;
            bool asText = false;
            if (TypeMapper.IsMappable(p.PropertyType))
            {
                values = ValuesOf(p);
                col = Mapped(() => TypeMapper.ColumnFor(colName, p.PropertyType, maxLen, isAuto, required, values, largeNumber),
                             $"'{typeof(T).Name}'.{p.Name}", paramName)!;
            }
            else
            {
                options.OnUnmappableColumn?.Invoke(colName, p.PropertyType);
                if (!options.FallbackUnmappableToString) continue;
                values = ValuesOf(p);
                col = new ColumnDefinition(colName, DataType.Memo) { IsRequired = required }.ToColumn(paramName);
                asText = true;
            }
            columns.Add(col);
            cells.Add((values, asText));

            if (p.GetCustomAttribute<KeyAttribute>() is not null)
                keys.Add((colName, p.GetCustomAttribute<ColumnAttribute>()?.Order ?? -1, keys.Count));
        }

        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no public properties that map to a Jet type.");
        EnsureNamesApart(columns, $"Type '{typeof(T).Name}'", paramName);

        // Several [Key] properties are a key of several columns: those that give a column order first, in it, and
        // then the others, in the order they are declared.
        var pk = PrimaryKeyOf(options)
                 ?? (keys.Count == 0 ? null : keys.OrderBy(k => k.Order >= 0 ? k.Order : int.MaxValue).ThenBy(k => k.Position).Select(k => k.Name).ToList());
        EnsureKeysWritten(columns, pk, $"Type '{typeof(T).Name}'");

        // An identity property that has a value keeps it; one still at 0 - an object the database has yet to
        // number - is numbered by the table.
        var rows = new List<Row>(objects.Count);
        for (int r = 0; r < objects.Count; r++)
        {
            var row = new Row();
            for (int i = 0; i < columns.Count; i++)
            {
                object? raw = cells[i].Values[r];
                if (columns[i].IsAutoNumber && IsUnnumbered(raw)) continue;
                if (cells[i].AsText && raw is not null) raw = raw.ToString();
                row[columns[i].Name] = Converted(raw, columns[i], Where(objects[r].Index), paramName);
            }
            rows.Add(row);
        }
        return new TablePlan(name, existing: null, columns, pk, rows, keepIdentity: true);
    }

    private static string ColumnNameOf(PropertyInfo property) => property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;

    // Two columns whose names are one without regard to case - "A" and "a" of a case-sensitive DataTable - would be one
    // column of the table: refused before anything is written, as CreateTable refuses them (ObjectNames).
    private static void EnsureNamesApart(IReadOnlyList<Column> columns, string source, string paramName)
    {
        for (int i = 1; i < columns.Count; i++)
            for (int j = 0; j < i; j++)
                if (ObjectNames.Same(columns[j].Name, columns[i].Name))
                    throw new ArgumentException(
                        $"{source} has two columns, '{columns[j].Name}' and '{columns[i].Name}', whose names are one without regard to case: " +
                        "a table has one column of a name. Nothing was imported.", paramName);
    }

    // A key on a Decimal column is refused before anything is written, as CreateTable refuses one: this library writes
    // no Decimal index keys. A long and a uint are Decimals in a file before Access 2016's format, and Large Numbers,
    // which a key can be on, in one of it.
    private static void EnsureKeysWritten(IReadOnlyList<Column> columns, IReadOnlyList<string>? key, string source)
    {
        if (key is null) return;
        foreach (string name in key)
            if (columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { DataType: DataType.Decimal } column)
                throw new NotSupportedException(
                    $"{source} has its key on '{column.Name}', a Decimal column, whose index keys this library does not write. " +
                    "A long or a uint is a Decimal in a file before Access 2016's format, which has no Large Number, and a Large " +
                    "Number, which a key can be on, in a file of FileFormat.Access2016 or later. Nothing was imported.");
    }

    // Two of the source's columns that both go to one column of a table that is there would leave it one of their
    // values: refused before anything is written.
    private static void EnsureOneSourceEach(IEnumerable<(string Source, Column Target)> pairs, string source, string paramName)
    {
        var twice = pairs.GroupBy(p => p.Target).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
            throw new ArgumentException(
                $"{source} has {twice.Count()} columns - {string.Join(", ", twice.Select(p => $"'{p.Source}'"))} - for the column " +
                $"'{twice.Key.Name}' of the table: it would keep one of their values. Nothing was imported.", paramName);
    }

    // ── Values ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A value bound for <paramref name="target"/>, a column of a table that is there, as text when it is of a type
    /// no column is made for and <see cref="ImportOptions.FallbackUnmappableToString"/> has such values stored as
    /// text in a Text or Memo column.
    /// </summary>
    private static object? AsText(object? raw, Column target, ImportOptions options)
        => raw is not null && options.FallbackUnmappableToString
           && target.DataType is DataType.Text or DataType.Memo
           && !TypeMapper.IsMappable(raw.GetType())
            ? raw.ToString()
            : raw;

    /// <summary>
    /// <paramref name="raw"/> as <paramref name="column"/> holds it, or the refusal, saying which row and column
    /// held a value the column does not hold exactly.
    /// </summary>
    /// <exception cref="ColumnValueTooLongException">Text longer than its column.</exception>
    /// <exception cref="ArgumentException">Any other value the column does not hold exactly.</exception>
    private static object? Converted(object? raw, Column column, string where, string paramName)
    {
        try
        {
            return TypeMapper.ToColumnValue(raw, column);
        }
        catch (TypeMapper.NotHeldException notHeld)
        {
            string message = $"The value of {column.Name} in {where} is not imported: {notHeld.Message}. Nothing was imported.";
            if (notHeld.TextLength is { } length)
                throw new ColumnValueTooLongException(column.Name, column.MaxLength, length, message);
            throw new ArgumentException(message, paramName);
        }
    }

    /// <summary>The column <paramref name="map"/> works out, or the refusal of a column no type holds every value of.</summary>
    /// <exception cref="ArgumentException">The column's values need more than any column of its type holds.</exception>
    private static Column? Mapped(Func<Column?> map, string what, string paramName)
    {
        try
        {
            return map();
        }
        catch (TypeMapper.NotHeldException notHeld)
        {
            throw new ArgumentException($"{what} is not imported: {notHeld.Message}. Nothing was imported.", paramName);
        }
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
    private static void InsertRows(Table target, IReadOnlyList<Row> rows, bool keepIdentity)
    {
        bool? before = target.AllowAutoNumberInsert;
        target.AllowAutoNumberInsert = keepIdentity;
        try
        {
            var identity = keepIdentity
                ? target.Columns.FirstOrDefault(c => c.IsAutoNumber && c.DataType == DataType.Int32)
                : null;
            if (identity is not null)
                ReserveSuppliedIdentities(target, identity, rows);

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
                        $"both hold {number} for {identity.Name}. Nothing was imported.");
        }

        foreach (var row in batch)
            target.Validate(row);

        target.RaiseLastAutoNumber(supplied.Max());
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
    /// source says: a <see cref="DataTable"/>'s primary key, of one column or several, or the properties with
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
    /// <para>
    /// A value is converted to its column's type when the column holds it exactly, and the import is refused
    /// otherwise: a number goes to a numeric column whose range, places and digits hold it (a <see cref="double"/>
    /// and a <see cref="decimal"/> to each other's columns when the value converts back to the one given); text
    /// goes to a numeric, Yes/No, Date/Time or GUID column when it reads as a value of it in the invariant culture;
    /// any of these goes to a Text or Memo column as its invariant text when that fits; text of a length goes to a
    /// Text column that holds that many characters, bytes to a Binary one that holds that many bytes. A Yes/No
    /// column takes no null and no number.
    /// </para>
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
