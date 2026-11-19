using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// How a value a caller looks for is matched to the value a row has - the one comparison every lookup that
/// confirms a row uses, whether the row was found through an index or by reading the table, so that both find
/// the same rows.
/// <list type="bullet">
/// <item>Text is equal as its column's sort order has it, which is what Access's <c>=</c> and a unique index go
/// by (measured in General - Legacy and General: 'abc' is 'ABC', 'a' is 'a ' with a space after it, 'ss' is
/// 'ß'; 'e' is not 'é', and '7' is not '007'): two values are equal when their index keys are, in the orders
/// whose keys this library writes. Where no key is made - a sort order this library does not write, Access 97's
/// among them, a character Access keys by its neighbours - text is equal when it is the same text, character
/// for character. A text column is looked in for text and nothing else: a number looked for is the text that
/// writes it, and text is never read as a number.</item>
/// <item>A number is equal to a number of another type that is the same number, as Access's <c>=</c> has it
/// (measured with ACE, #49): a Byte, Integer, Long or Large Number column's 5 for a caller's 5L, 5m or 5.0, and
/// for no number that is not that whole number (5.5, 70000 in an Integer, a double one past a Large Number's); a
/// Decimal exactly, as decimals compare, a caller's double taken to a decimal as Access takes one (to 15
/// significant digits). A Currency is the caller's number taken to its four places as Access takes it: a double or
/// a float by its exact value, half to even, as ACE's <c>=</c> - through an index and without - and DAO's
/// <c>Seek</c> take one (0.03125 is the row of 0.0312, and 0.00015, a double just under that half, the row of
/// 0.0001); any other number as a row would store it (1.23456 is the row of 1.2346). A Double or a Single is the
/// caller's number as a row would store it, by its bits: +0.0 and -0.0 are two keys to Access and to an index, and
/// so are a Double and the next Double after it. Text is no number.</item>
/// <item>A Date/Time is the double the row stores, days since 30 December 1899. A number looked for is that
/// double, matched by its bits (#35). A <see cref="DateTime"/> looked for is first taken to what it reads as once
/// a row stores it (<see cref="AsStored"/>: to the millisecond), and matches every stored value that reads as
/// that (<see cref="ReadsAs"/>): a row is read with its date rounded to the millisecond, and the date a caller
/// has read from a row, or has written to one, finds that row again, which the double of a
/// <see cref="DateTime"/> does not for a third of the dates Access computes.</item>
/// <item>A null is never a match: a row with no value in a column is not found by looking for null.</item>
/// </list>
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
            switch (column.DataType)
            {
                case DataType.DateTime:
                    double has = stored is DateTime read ? read.ToOADate() : Convert.ToDouble(stored, CultureInfo.InvariantCulture);
                    if (asked is DateTime date) return ReadsAs(has) == AsStored(date);
                    return IsNumber(asked!)
                        && BitConverter.DoubleToInt64Bits(has) == BitConverter.DoubleToInt64Bits(Convert.ToDouble(asked, CultureInfo.InvariantCulture));
                case DataType.Text:
                case DataType.Memo:
                    return SameText(column, Convert.ToString(stored, CultureInfo.InvariantCulture)!, Convert.ToString(asked, CultureInfo.InvariantCulture)!);
                case DataType.Guid:
                    return stored is Guid guid
                        && guid == (asked is Guid wanted ? wanted : Guid.Parse(Convert.ToString(asked, CultureInfo.InvariantCulture)!));
                case DataType.Currency:
                {
                    // The caller's number in ten-thousandths, as Access takes it (#49): a double or a float by its exact
                    // value, half to even, as ACE's index and scan and DAO's Seek take one (Util.CurrencyRounding); any
                    // other number as the row encoder writes it and an index keys it.
                    long holds = IndexKeys.MoneyUnits(Convert.ToDecimal(stored, CultureInfo.InvariantCulture));
                    if (asked is double or float)
                        return Util.CurrencyRounding.TryUnits(Convert.ToDouble(asked, CultureInfo.InvariantCulture), out long units)
                            && units == holds;
                    return IsNumber(asked!) && holds == IndexKeys.MoneyUnits(Convert.ToDecimal(asked, CultureInfo.InvariantCulture));
                }
            }
            return SameValue(stored, asked!);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether two values of a text column are equal as the column's sort order has it; as the same text where no
    /// key is made, and in Access 97's order, whose keys are made to search its files' indexes but whose equality
    /// was not measured, and whose key leaves some characters out altogether (a line break among them).
    /// </summary>
    private static bool SameText(Column column, string stored, string asked)
    {
        if (!column.SortOrder.IsAccess97General
            && TextIndexCodes.For(column.SortOrder) is { } codes && codes.CanEncode(stored) && codes.CanEncode(asked))
            return codes.Encode(stored, ascending: true).AsSpan().SequenceEqual(codes.Encode(asked, ascending: true));
        return string.Equals(stored, asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <see cref="DateTime"/> a stored Date/Time reads as (<see cref="RowDecoder.Decode(byte[], Column)"/>):
    /// rounded to the millisecond, and null for a number no date is.
    /// </summary>
    public static DateTime? ReadsAs(double stored) => RowDecoder.ReadDate(stored);

    /// <summary>
    /// What <paramref name="date"/> reads as once a row stores it: the date rounded to the millisecond, as
    /// <see cref="DateTime.Now"/> and any date finer than that are. A date a row cannot store is returned as it is.
    /// </summary>
    /// <exception cref="OverflowException"><paramref name="date"/> is before 1 January 100, which no row stores.</exception>
    public static DateTime AsStored(DateTime date) => ReadsAs(date.ToOADate()) ?? date;

    /// <summary>
    /// The least and the greatest double that read as <paramref name="date"/> once a row stores it
    /// (<see cref="AsStored"/>): the stored values a lookup for it finds. For a time on 30 December 1899 the
    /// negative of each number reads as it too; those are not within these.
    /// </summary>
    /// <exception cref="OverflowException"><paramref name="date"/> is before 1 January 100, which no row stores.</exception>
    public static (double Least, double Greatest) StoredRange(DateTime date)
    {
        date = AsStored(date);
        double exact = date.ToOADate();
        if (ReadsAs(exact) != date) return (exact, exact);
        const double twoMilliseconds = 2.0 / 86_400_000;
        return (Edge(exact, exact - twoMilliseconds, date), Edge(exact, exact + twoMilliseconds, date));
    }

    // The double furthest from 'inside' towards 'outside' that still reads as the date.
    private static double Edge(double inside, double outside, DateTime date)
    {
        if (ReadsAs(outside) == date) return outside;
        while (true)
        {
            double middle = inside + (outside - inside) / 2;
            if (middle == inside || middle == outside) return inside;
            if (ReadsAs(middle) == date) inside = middle;
            else outside = middle;
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
            case byte[] bytes:
                return asked is byte[] wanted && bytes.AsSpan().SequenceEqual(wanted);
            case bool flag:
                return asked is bool wantedFlag && flag == wantedFlag;
        }
        if (stored is DateTime || asked is DateTime) return Equals(stored, asked);
        if (!IsNumber(stored) || !IsNumber(asked)) return false;
        return stored switch
        {
            double d  => BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(Convert.ToDouble(asked, CultureInfo.InvariantCulture)),
            float f   => BitConverter.GetBytes(f).AsSpan().SequenceEqual(BitConverter.GetBytes(Convert.ToSingle(asked, CultureInfo.InvariantCulture))),
            // A Decimal: a double or a float asked is taken to a decimal as Access takes it, to the digits its type
            // holds, on every runtime (Util.AutomationDecimal).
            decimal m => m == Util.AutomationDecimal.From(asked, CultureInfo.InvariantCulture),
            // A whole number: a double or a float is it only when it is that whole number exactly. Taken to a decimal
            // it is cut to 15 significant digits, which made 5.000000000000001 an Integer's 5, and a double one past
            // a Large Number's that number (#49).
            _         => asked is double or float
                             ? IsWhole(Convert.ToDouble(asked, CultureInfo.InvariantCulture), out long whole)
                               && whole == Convert.ToInt64(stored, CultureInfo.InvariantCulture)
                             : Convert.ToDecimal(stored, CultureInfo.InvariantCulture) == Convert.ToDecimal(asked, CultureInfo.InvariantCulture),
        };
    }

    // Whether the number is a whole number a long holds, and which.
    private static bool IsWhole(double number, out long whole)
    {
        whole = 0;
        if (Math.Floor(number) != number || number < -9223372036854775808.0 || number >= 9223372036854775808.0) return false;
        whole = (long)number;
        return true;
    }

    private static bool IsNumber(object value)
        => value is double or float or decimal or int or long or short or byte or sbyte or uint or ulong or ushort;
}
