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
    /// characters are two bytes each, and <see cref="MaxLength"/> itself in an Access 97 file, which keeps text in
    /// its code page; the most bytes for a Binary column; the size of the type for a column of fixed length; 0 for a
    /// Memo or OLE Object column, whose values are kept on pages of their own. Jackcess's <c>getLength</c>.
    /// </summary>
    public int LengthInBytes { get; }

    /// <summary>
    /// The most a value of the column holds, in the type's own units: characters for a Text column, bytes for
    /// any other (<see cref="LengthInBytes"/>). It is the number <see cref="ColumnDefinition.MaxLength"/> sets
    /// and <see cref="ColumnValueTooLongException.MaxLength"/> reports, and the Size DAO reports. A Text column of
    /// an Access 97 file is as long as its bytes in the database's code page: Text(255) is 255 (#85). Jackcess's
    /// <c>getLengthInUnits</c>.
    /// </summary>
    public int MaxLength => DataType == DataType.Text && !IsCodePageText ? LengthInBytes / 2 : LengthInBytes;

    /// <summary>
    /// Whether the column's text is kept in the database's code page, a byte a character for its length, as Access
    /// 97 keeps it, and not two bytes a character.
    /// </summary>
    internal bool IsCodePageText { get; set; }

    /// <summary>
    /// Whether the column's values are read by their bytes - compressed text when they begin 0xFF 0xFE, wherever they
    /// are kept - whatever its Unicode compression: a column of a table an earlier version of this library created,
    /// which compressed every value of characters 0x01 to 0xFF and gave no column Unicode compression (#93). Access
    /// neither lists nor reads such a table.
    /// </summary>
    internal bool ReadsTextByContent { get; set; }

    /// <summary>
    /// Whether a value beginning 0xFF 0xFE is read as compressed text in this column, wherever the row keeps it: a Text
    /// column with Unicode compression, or one <see cref="ReadsTextByContent"/>.
    /// </summary>
    internal bool TextMayBeCompressed => IsCompressedUnicode || ReadsTextByContent;

    /// <summary>
    /// Whether the column is the Name of the catalog (MSysObjects) read as a table. In the row of a table an earlier
    /// version of this library created (<see cref="SystemCatalog.IsAnEarlierVersionsTable(RowDecoder, byte[], IReadOnlyList{Column})"/>) its value is read by its
    /// bytes, as the catalog reads it to list and find the table; in every other row, as the column's Unicode
    /// compression says (#121).
    /// </summary>
    internal bool IsCatalogName { get; set; }

    /// <summary>
    /// The column's number in its table's definition, counted from 0: its position among the table's columns,
    /// except that Access does not renumber the columns left when one is deleted, so the numbers of a table that
    /// has had a column deleted have gaps.
    /// </summary>
    public int Ordinal { get; internal set; }

    /// <summary>
    /// The properties Access keeps for the column - its Description, Caption, Format and the like - read with
    /// its table's. Empty for a column Access keeps none for. <see cref="PropertyMap.Set(string, object)"/>
    /// changes them, and <see cref="PropertyMap.Save"/> writes them with the table's others.
    /// </summary>
    public PropertyMap Properties => Owner?.PropertiesOf(this) ?? (_noProperties ??= new PropertyMap(Name, PropertyMap.ColumnType));

    private PropertyMap? _noProperties;

    /// <summary>The table the column is a column of, which reads its properties; null for one not yet in a table.</summary>
    internal Table? Owner { get; set; }

    /// <summary>
    /// Whether every row must have a value in the column: Access's Required property of the field, which is
    /// where Access keeps it (<see cref="Properties"/>, "Required"). <c>false</c> for a column that has no such
    /// property, as Access takes it. A row with no value in a required column is refused
    /// (<see cref="Table.Insert"/>, <see cref="Table.Update"/>). A Yes/No column always has a value,
    /// so nothing is refused for it. It is the property as the file has it: one changed with
    /// <see cref="PropertyMap.Set(string, object)"/> counts once it is saved.
    /// </summary>
    public bool IsRequired
        => Owner is { } table ? table.PropertyMaps.SavedRequired(Name) : _isRequired;

    // What the column was defined with, until a table has it: then the table's properties say.
    private bool _isRequired;
    private bool _allowZeroLength;

    /// <summary>Whether this is an AutoNumber (identity) column.</summary>
    public bool IsAutoNumber { get; }

    /// <summary>
    /// Whether a Text or Memo column takes a string of no characters: Access's Allow Zero Length property of
    /// the field, which is where Access keeps it (<see cref="Properties"/>, "AllowZeroLength"). <c>true</c> for a
    /// Text or Memo column that has no such property: Access takes a zero-length string in one, though DAO then
    /// reports the property as <c>false</c>. Always <c>false</c> for a column of another type, which holds no
    /// string. A zero-length string in a column that does not allow one is refused (<see cref="Table.Insert"/>,
    /// <see cref="Table.Update"/>). It is the property as the file has it: one changed with
    /// <see cref="PropertyMap.Set(string, object)"/> counts once it is saved.
    /// </summary>
    public bool AllowZeroLength
        => DataType is DataType.Text or DataType.Memo
           && (Owner is { } table ? table.PropertyMaps.SavedAllowZeroLength(Name) ?? true : _allowZeroLength);

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
        // The copy is in no table yet: it carries what this column's table says of it.
        copy._isRequired      = IsRequired;
        copy._allowZeroLength = AllowZeroLength;
        copy.Owner            = null;
        copy._noProperties    = null;
        // A table is created in a format that keeps text two bytes a character.
        copy.IsCodePageText   = false;
        // The table being created is one Access lists, whose text is read as Access reads it.
        copy.ReadsTextByContent = false;
        copy.IsCatalogName      = false;
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
        _isRequired = isRequired;
        IsAutoNumber = isAutoNumber;
        _allowZeroLength = allowZeroLength;
        Precision = precision;
        Scale = scale;
        IsVariableLength = dataType.IsVariableLength();
    }

    /// <summary>The column as "name (type, length in bytes)".</summary>
    public override string ToString() => $"{Name} ({DataType}, {LengthInBytes})";
}
