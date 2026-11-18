namespace JackcessDotNet;

/// <summary>
/// A column a table has: its name, its type, and what Access's field properties say of it. Read from a table
/// (<see cref="Table.Columns"/>) and never made by a caller: a column to give a table is a
/// <see cref="ColumnDefinition"/>.
/// </summary>
public sealed class Column
{
    /// <summary>Column name (max 64 characters in Jet 4).</summary>
    public string Name { get; }

    /// <summary>The column's type.</summary>
    public DataType DataType { get; }

    /// <summary>
    /// The room a value has in a row, in bytes: twice <see cref="MaxLength"/> for a Text column, whose
    /// characters are two bytes each; the most bytes for a Binary column; the size of the type for a column of
    /// fixed length; 0 for a Memo or OLE Object column, whose values are kept on pages of their own. Jackcess's
    /// <c>getLength</c>.
    /// </summary>
    public int LengthInBytes { get; }

    /// <summary>
    /// The most a value of the column holds, in the type's own units: characters for a Text column, bytes for
    /// any other (<see cref="LengthInBytes"/>). It is the number <see cref="ColumnDefinition.MaxLength"/> sets
    /// and <see cref="ColumnValueTooLongException.MaxLength"/> reports. Jackcess's <c>getLengthInUnits</c>.
    /// </summary>
    public int MaxLength => DataType == DataType.Text ? LengthInBytes / 2 : LengthInBytes;

    /// <summary>
    /// The column's number in its table's definition, counted from 0: its position among the table's columns,
    /// except that Access does not renumber the columns left when one is deleted, so the numbers of a table that
    /// has had a column deleted have gaps.
    /// </summary>
    public int Ordinal { get; internal set; }

    /// <summary>
    /// The properties Access keeps for the column - its Description, Caption, Format and the like - read from
    /// the table's properties the first time they are asked for. Empty for a column Access keeps none for.
    /// </summary>
    public PropertyMap Properties => Owner?.PropertiesOf(this) ?? (_noProperties ??= new PropertyMap(Name, 0x01));

    private PropertyMap? _noProperties;

    /// <summary>The table the column is a column of, which reads its properties; null for one not yet in a table.</summary>
    internal Table? Owner { get; set; }

    /// <summary>
    /// Whether the column was asked to be required when its table was made in this session
    /// (<see cref="ColumnDefinition.IsRequired"/>). It is not read from a file: a table that is opened says
    /// <c>false</c>, whatever Access's Required property of the column is (<see cref="Properties"/> has that).
    /// </summary>
    public bool IsRequired { get; }

    /// <summary>Whether this is an AutoNumber (identity) column.</summary>
    public bool IsAutoNumber { get; }

    /// <summary>
    /// Whether the column was asked to allow zero-length strings when its table was made in this session
    /// (<see cref="ColumnDefinition.AllowZeroLength"/>; Text and Memo only). It is not read from a file: a table
    /// that is opened says <c>false</c>, whatever Access's Allow Zero Length property of the column is
    /// (<see cref="Properties"/> has that).
    /// </summary>
    public bool AllowZeroLength { get; }

    /// <summary>For a Decimal column, the most digits a value has (1-28).</summary>
    public byte Precision { get; }

    /// <summary>For a Decimal column, how many of its digits follow the decimal point (0-28).</summary>
    public byte Scale { get; }

    /// <summary>
    /// Byte offset of this fixed-length column inside the row's fixed-data area,
    /// as stored in the TDEF. -1 for a column kept among the variable-length values, or one
    /// that has not been through a table definition yet.
    ///
    /// This matters for tables that have had columns deleted: Access leaves the
    /// deleted column's slot in row bytes, so subsequent columns' actual offsets
    /// aren't a tight pack — they must be read from the TDEF.
    /// </summary>
    internal short FixedDataOffset { get; set; } = -1;

    /// <summary>
    /// 0-based index into the row's table of variable-length offsets; -1 for a column kept among the fixed-length
    /// values, or one that has not been through a table definition yet.
    /// </summary>
    internal short VarLenTableIndex { get; set; } = -1;

    /// <summary>
    /// Whether a row keeps this column among its variable-length values. It is a flag of the column in the table's
    /// definition, not a property of its type: Access keeps a Long, a Date/Time, a Replication ID or a Decimal
    /// there when the flag says so (its own system tables do, and so does a Replication ID column made by SQL), and
    /// Text or Binary among the fixed-length values (SQL's CHAR and BINARY). A column this library creates is kept
    /// where its type usually is.
    /// </summary>
    public bool IsVariableLength { get; internal set; }

    /// <summary>
    /// True once a table definition has given the column its place in a row - read from one, or written to one:
    /// <see cref="IsVariableLength"/>, <see cref="FixedDataOffset"/> and <see cref="VarLenTableIndex"/> are then
    /// the definition's, and rows are read and written by them.
    /// </summary>
    internal bool HasStoredPlace { get; set; }

    /// <summary>
    /// Whether the column is a calculated one (Access 2010 and later): its value is the result of an expression,
    /// which Access works out and keeps in the row inside a wrapper of its own, as a type the column's properties
    /// name. This library neither works the expression out nor reads the result: the column reads as null, and a
    /// table with a calculated column is not written to.
    /// </summary>
    public bool IsCalculated { get; internal set; }

    /// <summary>
    /// Whether a Text or Memo value may be written in Jet's compressed form (0xFF 0xFE,
    /// then one byte per character): the column's "Unicode Compression" setting, read
    /// from its TDEF. Access reads a value in that form as UTF-16 in a column without
    /// it, so MSysObjects' names, for one, are written uncompressed. Columns this
    /// library creates allow it, as Access's own new Text and Memo fields do.
    /// </summary>
    public bool IsCompressedUnicode { get; internal set; } = true;

    /// <summary>
    /// The order a Text or Memo column's values sort in, in its indexes, read from its TDEF. A column of a table
    /// this library creates sorts in the order of the database the table is created in.
    /// </summary>
    internal SortOrder SortOrder { get; set; } = SortOrder.GeneralLegacy;

    /// <summary>
    /// A copy of this column for a table being created, its text sorting in <paramref name="textOrder"/> - the
    /// order of the database the table is created in. The new table numbers and lays out the copy; this column,
    /// which may be another table's, is not changed.
    /// </summary>
    internal Column CopyFor(SortOrder textOrder)
    {
        var copy = (Column)MemberwiseClone();
        if (DataType is DataType.Text or DataType.Memo) copy.SortOrder = textOrder;
        return copy;
    }

    internal Column(
        string name,
        DataType dataType,
        int length,
        bool isRequired,
        bool isAutoNumber,
        bool allowZeroLength,
        byte precision,
        byte scale)
    {
        Name = name;
        DataType = dataType;
        LengthInBytes = length;
        IsRequired = isRequired;
        IsAutoNumber = isAutoNumber;
        AllowZeroLength = allowZeroLength;
        Precision = precision;
        Scale = scale;
        IsVariableLength = dataType.IsVariableLength();
    }

    /// <summary>The column as "name (type, length in bytes)".</summary>
    public override string ToString() => $"{Name} ({DataType}, {LengthInBytes})";
}
