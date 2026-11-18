namespace JackcessDotNet;

/// <summary>
/// Describes a column in an Access table.
/// </summary>
public sealed class Column
{
    /// <summary>Column name (max 64 characters in Jet 4).</summary>
    public string Name { get; }

    /// <summary>Jet data type.</summary>
    public DataType DataType { get; }

    /// <summary>
    /// Storage length in bytes.
    /// For Text: max bytes = max chars * 2 (UTF-16LE).
    /// For Binary: max bytes directly.
    /// For fixed-length types: matches <see cref="DataType.GetFixedSize()"/>.
    /// </summary>
    public int Length { get; }

    /// <summary>Column ordinal position (0-based).</summary>
    public int ColumnNumber { get; internal set; }

    /// <summary>Whether the column is required (NOT NULL).</summary>
    public bool IsRequired { get; }

    /// <summary>Whether this is an AutoNumber (identity) column.</summary>
    public bool IsAutoNumber { get; }

    /// <summary>Whether this column allows zero-length strings (Text/Memo only).</summary>
    public bool AllowZeroLength { get; }

    /// <summary>Precision for Numeric type (1-28).</summary>
    public byte Precision { get; }

    /// <summary>Scale for Numeric type (0-28).</summary>
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
    public short FixedDataOffset { get; internal set; } = -1;

    /// <summary>
    /// 0-based index into the row's table of variable-length offsets; -1 for a column kept among the fixed-length
    /// values, or one that has not been through a table definition yet.
    /// </summary>
    public short VarLenTableIndex { get; internal set; } = -1;

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
        Length = length;
        IsRequired = isRequired;
        IsAutoNumber = isAutoNumber;
        AllowZeroLength = allowZeroLength;
        Precision = precision;
        Scale = scale;
        IsVariableLength = dataType.IsVariableLength();
    }

    public override string ToString() => $"{Name} ({DataType}, {Length})";
}
