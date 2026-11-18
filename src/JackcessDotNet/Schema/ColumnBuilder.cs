namespace JackcessDotNet;

/// <summary>
/// Fluent builder for creating <see cref="Column"/> definitions.
/// </summary>
public sealed class ColumnBuilder
{
    private string _name = string.Empty;
    private DataType _dataType = DataType.Text;
    private int _length = -1;        // -1 = use default
    private bool _isRequired;
    private bool _isAutoNumber;
    private bool _allowZeroLength = true;
    private byte _precision = 18;    // default Numeric precision
    private byte _scale = 0;         // default Numeric scale

    public ColumnBuilder(string name, DataType dataType)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _dataType = dataType;
    }

    /// <summary>Sets the column storage length in bytes.</summary>
    public ColumnBuilder WithLength(int length)
    {
        _length = length;
        return this;
    }

    /// <summary>For Text columns: sets max characters (internally stored as chars * 2 bytes).</summary>
    public ColumnBuilder WithMaxChars(int maxChars)
    {
        if (_dataType != DataType.Text)
            throw new InvalidOperationException("WithMaxChars is only valid for Text columns.");
        _length = maxChars * 2;
        return this;
    }

    public ColumnBuilder WithRequired(bool required = true)
    {
        _isRequired = required;
        return this;
    }

    /// <summary>
    /// Makes the column an AutoNumber, filled in by <see cref="Table.Insert"/>: a Long column counts up from the
    /// table's counter, a Guid column (Access's Replication ID) gets a new GUID.
    /// </summary>
    public ColumnBuilder WithAutoNumber(bool autoNumber = true)
    {
        if (_dataType is not (DataType.Long or DataType.Guid))
            throw new InvalidOperationException("AutoNumber requires DataType.Long or DataType.Guid.");
        _isAutoNumber = autoNumber;
        return this;
    }

    public ColumnBuilder WithAllowZeroLength(bool allow = true)
    {
        _allowZeroLength = allow;
        return this;
    }

    /// <summary>Sets precision and scale for Numeric columns.</summary>
    public ColumnBuilder WithNumericScale(byte precision, byte scale)
    {
        _precision = precision;
        _scale = scale;
        return this;
    }

    /// <summary>Alias for <see cref="ToColumn"/>.</summary>
    public Column Build() => ToColumn();

    /// <summary>Alias for <see cref="WithMaxChars"/> — sets max characters for Text columns.</summary>
    public ColumnBuilder MaxLength(int maxChars) => WithMaxChars(maxChars);

    /// <summary>Instance alias for <see cref="WithAutoNumber"/>.</summary>
    public ColumnBuilder AutoNumber(bool autoNumber = true) => WithAutoNumber(autoNumber);

    public Column ToColumn()
    {
        ValidateName(_name);

        int length = _length >= 0
            ? _length
            : _dataType.GetDefaultSize();

        if (_dataType.IsFixedLength())
            length = _dataType.GetFixedSize();

        int maxSize = _dataType.GetMaxSize();
        if (maxSize > 0 && length > maxSize)
            throw new InvalidOperationException(
                $"Column '{_name}': length {length} exceeds max {maxSize} for {_dataType}.");

        return new Column(
            name: _name,
            dataType: _dataType,
            length: length,
            isRequired: _isRequired,
            isAutoNumber: _isAutoNumber,
            allowZeroLength: _allowZeroLength,
            precision: _precision,
            scale: _scale);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Column name cannot be empty.");
        if (name.Length > 64)
            throw new ArgumentException($"Column name '{name}' exceeds 64-character limit.");
    }

    // ── Static factory shortcuts ──────────────────────────────────────────────

    public static ColumnBuilder Text(string name, int maxChars = 50)
        => new ColumnBuilder(name, DataType.Text).WithMaxChars(maxChars);

    public static ColumnBuilder Long(string name)
        => new ColumnBuilder(name, DataType.Long);

    public static ColumnBuilder AutoNumber(string name)
        => new ColumnBuilder(name, DataType.Long).WithAutoNumber();

    public static ColumnBuilder Int(string name)
        => new ColumnBuilder(name, DataType.Int);

    public static ColumnBuilder Double(string name)
        => new ColumnBuilder(name, DataType.Double);

    public static ColumnBuilder DateTime(string name)
        => new ColumnBuilder(name, DataType.ShortDateTime);

    public static ColumnBuilder Boolean(string name)
        => new ColumnBuilder(name, DataType.Boolean);

    public static ColumnBuilder Memo(string name)
        => new ColumnBuilder(name, DataType.Memo);

    public static ColumnBuilder Money(string name)
        => new ColumnBuilder(name, DataType.Money);

    public static ColumnBuilder Guid(string name)
        => new ColumnBuilder(name, DataType.Guid);

    public static ColumnBuilder Numeric(string name, byte precision = 18, byte scale = 0)
        => new ColumnBuilder(name, DataType.Numeric).WithNumericScale(precision, scale);

    /// <summary>A Large Number column (Access 2016 and later, .accdb only): a <see cref="long"/>.</summary>
    public static ColumnBuilder BigInt(string name)
        => new ColumnBuilder(name, DataType.BigInt);

    /// <summary>
    /// A Date/Time Extended column (Access 2019 and later, .accdb only): a <see cref="System.DateTime"/> kept to
    /// its last tick, over its whole range.
    /// </summary>
    public static ColumnBuilder ExtendedDateTime(string name)
        => new ColumnBuilder(name, DataType.ExtendedDateTime);
}
