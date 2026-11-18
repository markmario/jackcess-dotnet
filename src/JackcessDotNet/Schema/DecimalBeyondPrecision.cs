namespace JackcessDotNet;

/// <summary>
/// A Decimal (Numeric) value that reads with more digits than its column's precision allows: what
/// <see cref="Table.FindDecimalsBeyondPrecision"/> and <see cref="Database.FindDecimalsBeyondPrecision"/> list.
/// Access does not write such a value, and neither does this library, so it is almost certainly one a version of
/// this library before qcatco/exportmdb.interop#61 wrote, with its bytes in the order those versions used:
/// <see cref="ValueInEarlierByteOrder"/> is then the number that was meant.
/// </summary>
public sealed class DecimalBeyondPrecision
{
    internal DecimalBeyondPrecision(string table, Column column, int rowIndex, Row primaryKey,
                                    decimal? value, decimal? valueInEarlierByteOrder)
    {
        Table                   = table;
        Column                  = column;
        RowIndex                = rowIndex;
        PrimaryKey              = primaryKey;
        Value                   = value;
        ValueInEarlierByteOrder = valueInEarlierByteOrder;
    }

    /// <summary>The table's name.</summary>
    public string Table { get; }

    /// <summary>The column: its name, and the <see cref="JackcessDotNet.Column.Precision"/> the value is beyond.</summary>
    public Column Column { get; }

    /// <summary>
    /// The row's place among the table's rows, counted from 0, as <see cref="JackcessDotNet.Table.ReadAllRows"/>
    /// returns them and a cursor reads them. It is the row's place until rows are deleted or rewritten.
    /// </summary>
    public int RowIndex { get; }

    /// <summary>The row's primary-key values by column name; empty for a table without a primary key.</summary>
    public Row PrimaryKey { get; }

    /// <summary>
    /// The value as it is read, and as Access reads it; null when it is more than a <see cref="decimal"/> holds,
    /// for which a read of the row gives null too.
    /// </summary>
    public decimal? Value { get; }

    /// <summary>
    /// The value its bytes make in the order earlier versions of this library wrote them - sixteen bytes, the most
    /// significant first; null when that is more than a <see cref="decimal"/> holds. When this has no more digits
    /// than the column's precision, it is in all likelihood the number the row was given.
    /// </summary>
    public decimal? ValueInEarlierByteOrder { get; }

    public override string ToString()
        => $"{Table}.{Column.Name}, row {RowIndex}" +
           (PrimaryKey.Count > 0 ? $" ({string.Join(", ", PrimaryKey.Select(k => $"{k.Key}={k.Value}"))})" : string.Empty) +
           $": reads {Show(Value)}, {Show(ValueInEarlierByteOrder)} in the earlier byte order";

    private static string Show(decimal? value)
        => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "more than a decimal holds";
}
