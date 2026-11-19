namespace JackcessDotNet;

/// <summary>
/// A column to give a table: its name and type, and what else Access lets a field be given. Pass it to
/// <see cref="Database.CreateTable(string, IEnumerable{ColumnDefinition}, string[])"/> or
/// <see cref="Table.AddColumn"/>; the column the table then has is a <see cref="Column"/>.
/// <code>
///   new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true }
///   new ColumnDefinition("Name", DataType.Text) { MaxLength = 100, IsRequired = true }
///   new ColumnDefinition("Price", DataType.Decimal) { Precision = 10, Scale = 2 }
/// </code>
/// A definition is checked when a table is made with it, not as its properties are set: a length on a column
/// that has none, or an AutoNumber of a type Access does not number, is refused then, with
/// <see cref="ArgumentException"/>, and no table is made.
/// </summary>
public sealed class ColumnDefinition
{
    // The longest name Access gives a field.
    private const int MaxNameLength = 64;

    /// <summary>
    /// Describes a column of a name and a type. The name's length is checked when a table is made with the
    /// definition, not here.
    /// </summary>
    /// <param name="name">The column's name: 1 to 64 characters, unique among the table's columns.</param>
    /// <param name="dataType">The column's type.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public ColumnDefinition(string name, DataType dataType)
    {
        Name     = name ?? throw new ArgumentNullException(nameof(name));
        DataType = dataType;
    }

    /// <summary>The column's name.</summary>
    public string Name { get; }

    /// <summary>The column's type.</summary>
    public DataType DataType { get; }

    /// <summary>
    /// The most a value of the column holds: characters for <see cref="DataType.Text"/> (1 to 255), bytes for
    /// <see cref="DataType.Binary"/> (1 to 255). Null (the default) for the type's default: 50 characters, or 50
    /// bytes. No other type has a length to choose, and setting one for it is refused when the table is made.
    /// </summary>
    public int? MaxLength { get; set; }

    /// <summary>
    /// Whether the column is to have a value in every row (Access's Required property). Default <c>false</c>.
    /// It is written to the file as the column's Required property, where Access keeps it, so Access refuses a
    /// row without a value there, and so does this library (<see cref="Column.IsRequired"/>). An AutoNumber
    /// column is given no such property: every row has its number.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Whether the column is an AutoNumber, filled in by <see cref="Table.Insert"/>: a
    /// <see cref="DataType.Int32"/> column counts up from the table's counter, a <see cref="DataType.Guid"/>
    /// column (Access's Replication ID) is given a new GUID. No other type is numbered. Default <c>false</c>.
    /// A <see cref="DataType.Guid"/> AutoNumber is also given the <c>DefaultValue</c> property
    /// <c>GenGUID()</c>, as Access gives one: it is what Access fills the column from in a row it adds.
    /// </summary>
    public bool IsAutoNumber { get; set; }

    /// <summary>
    /// Whether a Text or Memo column takes a string of no characters (Access's Allow Zero Length property).
    /// Default <c>true</c>, as for a field made in Access's table designer (a field made with DAO does not
    /// allow one unless asked). It is written to the file as the column's AllowZeroLength property, where Access
    /// keeps it, so Access refuses a zero-length string in a column that does not allow one, and so does this
    /// library (<see cref="Column.AllowZeroLength"/>). It says nothing of a column of another type.
    /// </summary>
    public bool AllowZeroLength { get; set; } = true;

    /// <summary>
    /// For a <see cref="DataType.Decimal"/> column, the most digits a value has, 1 to 28. Default 18, as in
    /// Access.
    /// </summary>
    public byte Precision { get; set; } = 18;

    /// <summary>
    /// For a <see cref="DataType.Decimal"/> column, how many of its digits follow the decimal point. Default 0.
    /// </summary>
    public byte Scale { get; set; }

    /// <summary>
    /// The column this describes, as a table about to be made or changed holds it before it is laid out.
    /// </summary>
    /// <param name="parameterName">The caller's parameter the definition came in, for the exception.</param>
    /// <exception cref="ArgumentException">
    /// The name is empty or longer than 64 characters; a length is given for a type that has none, or is less
    /// than 1 or more than the type allows; or an AutoNumber is asked of a type other than
    /// <see cref="DataType.Int32"/> and <see cref="DataType.Guid"/>.
    /// </exception>
    internal Column ToColumn(string parameterName)
    {
        if (Name.Length == 0)
            throw new ArgumentException("Column name cannot be empty.", parameterName);
        if (Name.Length > MaxNameLength)
            throw new ArgumentException($"Column name '{Name}' exceeds {MaxNameLength}-character limit.", parameterName);

        int length = DataType.IsFixedLength() ? DataType.GetFixedSize() : DataType.GetDefaultSize();
        if (MaxLength is { } units)
        {
            if (DataType is not (DataType.Text or DataType.Binary))
                throw new ArgumentException(
                    $"Column '{Name}': a {DataType} column has no length to choose; MaxLength is for Text and Binary columns.",
                    parameterName);
            // A Text column's room is two bytes a character.
            int bytesEach = DataType == DataType.Text ? 2 : 1;
            int most      = DataType.GetMaxSize() / bytesEach;
            if (units < 1 || units > most)
                throw new ArgumentException(
                    $"Column '{Name}': a {DataType} column holds 1 to {most} " +
                    $"{(DataType == DataType.Text ? "characters" : "bytes")}; {units} was asked for.", parameterName);
            length = units * bytesEach;
        }

        if (IsAutoNumber && DataType is not (DataType.Int32 or DataType.Guid))
            throw new ArgumentException(
                $"Column '{Name}': an AutoNumber is a DataType.Int32 or a DataType.Guid column, not a {DataType} one.",
                parameterName);

        return new Column(
            name: Name,
            dataType: DataType,
            length: length,
            isRequired: IsRequired,
            isAutoNumber: IsAutoNumber,
            allowZeroLength: AllowZeroLength,
            precision: Precision,
            scale: Scale);
    }
}
