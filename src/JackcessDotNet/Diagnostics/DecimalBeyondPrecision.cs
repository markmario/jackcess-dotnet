namespace JackcessDotNet.Diagnostics;

/// <summary>
/// A Decimal (Numeric) value whose stored number has more digits than its column's precision allows: what
/// <see cref="DecimalByteOrder.Find(JackcessDotNet.Table)"/> and <see cref="DecimalByteOrder.Find(Database)"/> list.
/// Access does not write such a value, and neither does this library, so it is almost certainly one a version of
/// this library before qcatco/exportmdb.interop#61 wrote, with its bytes in the order those versions used:
/// <see cref="ValueInEarlierByteOrder"/> is then the number that was meant.
/// </summary>
public sealed class DecimalBeyondPrecision
{
    internal DecimalBeyondPrecision(string table, Column column, int rowIndex, Row primaryKey,
                                    decimal? value, bool isCut, decimal? valueInEarlierByteOrder)
    {
        Table                   = table;
        Column                  = column;
        RowIndex                = rowIndex;
        PrimaryKey              = primaryKey;
        Value                   = value;
        IsCut                   = isCut;
        ValueInEarlierByteOrder = valueInEarlierByteOrder;
    }

    /// <summary>The table's name.</summary>
    public string Table { get; }

    /// <summary>The column: its name, and the <see cref="JackcessDotNet.Column.Precision"/> the value is beyond.</summary>
    public Column Column { get; }

    /// <summary>
    /// The row's place among the table's rows, counted from 0, as <see cref="JackcessDotNet.Table.Rows"/>
    /// returns them and a cursor reads them. It is the row's place until rows are deleted or rewritten.
    /// </summary>
    public int RowIndex { get; }

    /// <summary>The row's primary-key values by column name; empty for a table without a primary key.</summary>
    public Row PrimaryKey { get; }

    /// <summary>
    /// The value as it is read, and as Access reads it: what a read of the row gives. When the stored number runs
    /// past 96 bits (<see cref="IsCut"/>) that is the number its low 96 bits make. Null only for a column whose
    /// scale is past 28, which a <see cref="decimal"/> cannot hold.
    /// </summary>
    public decimal? Value { get; }

    /// <summary>
    /// Whether the stored number runs past the 96 bits Access reads of one, so that <see cref="Value"/> is only
    /// its low 96 bits - which may be a number the column would hold, 0 among them. Access writes no such number.
    /// </summary>
    public bool IsCut { get; }

    /// <summary>
    /// The value its bytes make in the order earlier versions of this library wrote them - sixteen bytes, the most
    /// significant first; null when that is more than a <see cref="decimal"/> holds. When this has no more digits
    /// than the column's precision, it is in all likelihood the number the row was given.
    /// </summary>
    public decimal? ValueInEarlierByteOrder { get; }

    /// <summary>
    /// The value in a line of text: its table and column, its row's place and primary key, the number it reads
    /// as and the number its bytes make in the earlier order, those two written in the invariant culture.
    /// </summary>
    /// <returns>
    /// Text such as <c>Prices.Price, row 1 (Id=2): reads 16777216.00, 1.00 in the earlier byte order</c>.
    /// </returns>
    public override string ToString()
        => $"{Table}.{Column.Name}, row {RowIndex}" +
           (PrimaryKey.Count > 0 ? $" ({string.Join(", ", PrimaryKey.Select(k => $"{k.Key}={k.Value}"))})" : string.Empty) +
           $": reads {Show(Value)}{(IsCut ? " (the low 96 bits of a longer number)" : string.Empty)}, " +
           $"{Show(ValueInEarlierByteOrder)} in the earlier byte order";

    private static string Show(decimal? value)
        => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "more than a decimal holds";
}
