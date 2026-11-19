using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// How a value a caller looks for is matched to the value a row has - the one comparison every lookup that
/// confirms a row uses, whether the row was found through an index or by reading the table.
/// <para>
/// A value is matched as the row stores it. A floating-point value is matched by its bits, the caller's taken to
/// the column's type as a row would store it: +0.0 and -0.0 are two keys to Access and to an index, and so are
/// a Double and the next Double after it. A Date/Time is the double the row stores (days since 30 December
/// 1899), matched by its bits too: a <see cref="DateTime"/> looked for is the double it converts to
/// (<see cref="DateTime.ToOADate"/>), and a number looked for is that double itself, so two dates less than a
/// millisecond apart - which read as the same <see cref="DateTime"/> - are two values (#35). Anything else is
/// equal, or equal as numbers (a Long column's 5 for a caller's 5L; a Currency or a Decimal exactly, as decimals
/// compare). Text is matched exactly, and binary values byte for byte.
/// </para>
/// <para>
/// A null is never a match: a row with no value in a column is not found by looking for null.
/// </para>
/// </summary>
internal static class ValueMatch
{
    /// <summary>
    /// Whether <paramref name="stored"/>, a row's value in <paramref name="column"/> as its index keys are made
    /// of it (<see cref="RowDecoder.DecodeForKey"/>: a Date/Time as the double stored), is the value
    /// <paramref name="asked"/> for.
    /// </summary>
    public static bool Matches(Column column, object? stored, object? asked)
    {
        if (stored is null || RowValues.IsNull(asked)) return false;
        try
        {
            if (column.DataType == DataType.DateTime)
            {
                double has = stored is DateTime read ? read.ToOADate() : Convert.ToDouble(stored, CultureInfo.InvariantCulture);
                if (asked is not DateTime && !IsNumber(asked!)) return false;
                double wanted = asked is DateTime date ? date.ToOADate() : Convert.ToDouble(asked, CultureInfo.InvariantCulture);
                return BitConverter.DoubleToInt64Bits(has) == BitConverter.DoubleToInt64Bits(wanted);
            }
            return SameValue(stored, asked!);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="given"/> is the value <paramref name="read"/>, one a row was read with
    /// (<see cref="RowDecoder.Decode(byte[], Column)"/>): what tells an update which of a row's values it was
    /// given back unchanged, and so leaves as the row stores them.
    /// </summary>
    public static bool IsAsRead(object? read, object? given)
    {
        if (read is null || given is null) return read is null && given is null;
        return (read, given) switch
        {
            (double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            (float a, float b)   => BitConverter.GetBytes(a).AsSpan().SequenceEqual(BitConverter.GetBytes(b)),
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            _                    => read.GetType() == given.GetType() && Equals(read, given),
        };
    }

    private static bool SameValue(object stored, object asked)
    {
        switch (stored)
        {
            case double d:
                return BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(Convert.ToDouble(asked, CultureInfo.InvariantCulture));
            case float f:
                return BitConverter.GetBytes(f).AsSpan().SequenceEqual(BitConverter.GetBytes(Convert.ToSingle(asked, CultureInfo.InvariantCulture)));
            case byte[] bytes:
                return asked is byte[] wanted && bytes.AsSpan().SequenceEqual(wanted);
        }
        if (Equals(stored, asked)) return true;
        return Convert.ToDecimal(stored, CultureInfo.InvariantCulture) == Convert.ToDecimal(asked, CultureInfo.InvariantCulture);
    }

    private static bool IsNumber(object value)
        => value is double or float or decimal or int or long or short or byte or sbyte or uint or ulong or ushort;
}
