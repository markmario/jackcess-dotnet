namespace JackcessDotNet;

/// <summary>
/// Fluent factory for creating tables on a <see cref="Database"/>. Mirrors
/// Java Jackcess's <c>TableBuilder</c>. Carries the column list, the
/// primary-key column name and any other indexes until <see cref="ToTable"/> is called.
///
/// Usage:
/// <code>
///   var table = new TableBuilder("Customers")
///       .AddColumn(new ColumnBuilder("Id",   DataType.Long).Build())
///       .AddColumn(new ColumnBuilder("Name", DataType.Text).MaxLength(100).Build())
///       .WithPrimaryKey("Id")
///       .WithIndex(new IndexDefinition("ByName", "Name") { IsUnique = true })
///       .ToTable(db);
/// </code>
/// </summary>
public sealed class TableBuilder
{
    private readonly string        _name;
    private readonly List<Column>  _columns = new();
    private          string?       _primaryKey;
    private readonly List<IndexDefinition> _indexes = new();

    public TableBuilder(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));
        _name = name;
    }

    /// <summary>Adds a column built externally (e.g. via <see cref="ColumnBuilder"/>).</summary>
    public TableBuilder AddColumn(Column column)
    {
        _columns.Add(column ?? throw new ArgumentNullException(nameof(column)));
        return this;
    }

    /// <summary>Adds a column directly from a <see cref="ColumnBuilder"/>.</summary>
    public TableBuilder AddColumn(ColumnBuilder builder)
        => AddColumn((builder ?? throw new ArgumentNullException(nameof(builder))).Build());

    /// <summary>Adds a column by name + type. The <paramref name="configure"/> callback
    /// lets you tweak the builder (max length, autonumber, etc.) inline.</summary>
    public TableBuilder AddColumn(string name, DataType type, Action<ColumnBuilder>? configure = null)
    {
        var cb = new ColumnBuilder(name, type);
        configure?.Invoke(cb);
        return AddColumn(cb.Build());
    }

    /// <summary>Marks one of the previously-added columns as the primary key.</summary>
    public TableBuilder WithPrimaryKey(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            throw new ArgumentException("PK column name must not be empty.", nameof(columnName));
        _primaryKey = columnName;
        return this;
    }

    /// <summary>Gives the table an index beside its primary key, on columns added before or after.</summary>
    public TableBuilder WithIndex(IndexDefinition index)
    {
        _indexes.Add(index ?? throw new ArgumentNullException(nameof(index)));
        return this;
    }

    /// <summary>Creates the table on <paramref name="db"/> and returns it.</summary>
    public Table ToTable(Database db)
    {
        if (db is null) throw new ArgumentNullException(nameof(db));
        if (_columns.Count == 0)
            throw new InvalidOperationException("TableBuilder requires at least one column before ToTable.");
        return _indexes.Count == 0
            ? db.CreateTable(_name, _columns, _primaryKey)
            : db.CreateTable(_name, _columns, _primaryKey is null ? null : new[] { _primaryKey }, _indexes);
    }
}
