using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// What a value a caller puts in a <see cref="Row"/> means once it is written: the one reading of it that the
/// row's bytes and its index keys are both made from, so the two cannot disagree.
/// </summary>
internal static class RowValues
{
    /// <summary>True for no value: <c>null</c>, or <see cref="DBNull"/> as a <c>DataRow</c> has it.</summary>
    public static bool IsNull(object? value) => value is null or DBNull;

    /// <summary>
    /// The Yes/No a value stands for, as Java Jackcess reads one: a <see cref="bool"/> is itself, a number is Yes
    /// unless it is zero (Access's own rule), text is Yes when it says "true" in any case, and no value is No -
    /// a Yes/No column holds no null.
    /// </summary>
    public static bool ToBoolean(object? value) => value switch
    {
        null or DBNull => false,
        bool b         => b,
        string s       => bool.TryParse(s, out bool parsed) && parsed,
        IConvertible c => c.GetTypeCode() switch
        {
            TypeCode.Single or TypeCode.Double => c.ToDouble(CultureInfo.InvariantCulture) != 0d,
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Decimal => c.ToDecimal(CultureInfo.InvariantCulture) != 0m,
            _ => false,
        },
        _ => false,
    };

    /// <summary>
    /// <paramref name="row"/> as it will be written to a table of <paramref name="columns"/>: no
    /// <see cref="DBNull"/> (a column without a value has none), a Yes/No column's value as the
    /// <see cref="bool"/> it stands for, and a Currency column's as the <see cref="decimal"/> the row stores
    /// (<see cref="AsCurrency"/>). The row itself when it is already so; otherwise a copy - the caller's row is not
    /// changed.
    /// </summary>
    /// <exception cref="ArgumentException">A Currency column's value is not a number.</exception>
    /// <exception cref="OverflowException">A Currency column's value is a number no Currency holds.</exception>
    public static Row AsWritten(Row row, IReadOnlyList<Column> columns)
    {
        Row? written = null;
        foreach (var column in columns)
        {
            if (!row.TryGetValue(column.Name, out var value)) continue;
            object? normal = value is DBNull ? null
                : column.DataType == DataType.Boolean && value is not (null or bool) ? ToBoolean(value)
                : column.DataType == DataType.Currency && value is not null ? AsCurrency(column, value)
                : value;
            if (ReferenceEquals(normal, value)) continue;
            written ??= new Row(row);
            written[column.Name] = normal;
        }
        return written ?? row;
    }

    /// <summary>
    /// <paramref name="value"/>, given for the Currency column <paramref name="column"/>, as the row stores it: the
    /// number taken to four places as Access takes it - its exact value, half to even (<see cref="Util.CurrencyRounding"/>,
    /// #65) - as a <see cref="decimal"/>. A decimal that is that already is returned as it is. Refused, before
    /// anything is written, when it is not a number - text among them, which a row's number is never read from - or
    /// is a number no Currency holds: NaN, an infinity, or one past the range once taken to four places.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a number.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is a number no Currency holds.</exception>
    public static object AsCurrency(Column column, object value)
    {
        if (!Util.CurrencyRounding.IsNumber(value))
            throw new ArgumentException(
                $"Column '{column.Name}' is a Currency, which holds a number, and {Util.CurrencyRounding.NotANumber(value)}. Nothing was written.");
        if (!Util.CurrencyRounding.TryUnits(value, out long units))
            throw new OverflowException(
                $"Column '{column.Name}' is a Currency, and {Util.CurrencyRounding.NotHeld(value)}. Nothing was written.");
        decimal stored = Util.CurrencyRounding.ToDecimal(units);
        return value is decimal given && given == stored ? value : stored;
    }
}
