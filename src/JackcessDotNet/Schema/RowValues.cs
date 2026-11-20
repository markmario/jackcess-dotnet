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
    /// <see cref="bool"/> it stands for, and a Currency column's and a Decimal column's number as the
    /// <see cref="decimal"/> the row stores (<see cref="AsCurrency"/>, <see cref="AsDecimal"/>). The row itself when
    /// it is already so; otherwise a copy - the caller's row is not changed.
    /// </summary>
    /// <exception cref="ArgumentException">A Currency column's value is of a kind no number is made of.</exception>
    /// <exception cref="FormatException">A Currency column's value is text that does not read as a number.</exception>
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
                : column.DataType == DataType.Decimal && Util.CurrencyRounding.IsNumber(value) ? AsDecimal(column, value!)
                : value;
            if (ReferenceEquals(normal, value)) continue;
            written ??= new Row(row);
            written[column.Name] = normal;
        }
        return written ?? row;
    }

    /// <summary>
    /// <paramref name="value"/>, a number given for the Decimal column <paramref name="column"/>, as the row stores
    /// it: a double or a float taken to a decimal as Access takes one (<see cref="Util.AutomationDecimal"/>), and the
    /// places past the column's scale cut off, toward zero, as ACE stores them (<see cref="NumericCodec.AsStored"/>,
    /// #80). The value itself when it is that decimal already. A number with more digits than the column holds is
    /// refused when the row is encoded.
    /// </summary>
    /// <exception cref="OverflowException"><paramref name="value"/> is a number past what a decimal holds.</exception>
    public static object AsDecimal(Column column, object value)
    {
        decimal stored = NumericCodec.AsStored(Util.AutomationDecimal.From(value, CultureInfo.InvariantCulture), column.Scale);
        return value is decimal given && decimal.GetBits(given).AsSpan().SequenceEqual(decimal.GetBits(stored)) ? value : stored;
    }

    /// <summary>
    /// <paramref name="value"/>, given for the Currency column <paramref name="column"/>, as the row stores it: the
    /// number taken to four places as Access takes it - its exact value, half to even (<see cref="Util.CurrencyRounding"/>,
    /// #65) - as a <see cref="decimal"/>. A value that is not one of .NET's numbers is first taken for the number every
    /// number column takes it for (<see cref="Util.CurrencyRounding.NumberOf"/>: text read in the current culture, a
    /// <see cref="bool"/> as 1 or 0). A decimal that is the stored value already is returned as it is. Refused, before
    /// anything is written, when it makes no number, or a number no Currency holds: NaN, an infinity, or one past the
    /// range once taken to four places.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is of a kind no number is made of: a date, a <see cref="Guid"/>, bytes, a <see cref="char"/>.
    /// </exception>
    /// <exception cref="FormatException"><paramref name="value"/> is text that does not read as a number.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is a number no Currency holds.</exception>
    public static object AsCurrency(Column column, object value)
    {
        object number = value;
        if (!Util.CurrencyRounding.IsNumber(value))
        {
            try { number = Util.CurrencyRounding.NumberOf(value); }
            catch (InvalidCastException e)
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' is a Currency, which holds a number, and {Util.CurrencyRounding.NotANumber(value)}. Nothing was written.", e);
            }
            catch (FormatException e)
            {
                throw new FormatException(
                    $"Column '{column.Name}' is a Currency, which holds a number, and {Util.CurrencyRounding.NotANumber(value)}. Nothing was written.", e);
            }
            catch (OverflowException e)
            {
                throw new OverflowException(
                    $"Column '{column.Name}' is a Currency, and '{value}' is a number past what a decimal holds. Nothing was written.", e);
            }
        }
        if (!Util.CurrencyRounding.TryUnits(number, out long units))
            throw new OverflowException(
                $"Column '{column.Name}' is a Currency, and {Util.CurrencyRounding.NotHeld(number)}. Nothing was written.");
        decimal stored = Util.CurrencyRounding.ToDecimal(units);
        return value is decimal given && given == stored ? value : stored;
    }
}
