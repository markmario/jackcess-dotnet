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
/// <item>A number is equal to a number of another type that is the same number (a Long column's 5 for a
/// caller's 5L; a Currency or a Decimal exactly, as decimals compare); a Double or a Single by its bits, the
/// caller's taken to the column's type as a row would store it: +0.0 and -0.0 are two keys to Access and to an
/// index, and so are a Double and the next Double after it. Text is no number.</item>
/// <item>A Date/Time is the double the row stores, days since 30 December 1899. A number looked for is that
/// double, matched by its bits (#35). A <see cref="DateTime"/> looked for is first taken to what it reads as once
/// a row stores it (<see cref="AsStored"/>: to the millisecond), and matches every stored value that reads as
/// that (<see cref="ReadsAs"/>): a row is read with its date rounded to the millisecond, and the date a caller
/// has read from a row, or has written to one, finds that row again, which the double of a
/// <see cref="DateTime"/> does not for a third of the dates Access computes.</item>
/// <item>A Date/Time Extended is the 42 bytes the row stores (<see cref="StoredExtendedDate"/>). A
/// <see cref="DateTime"/> looked for matches every row whose bytes say that date, in whichever form Access stored
/// them, as Access's <c>=</c> matches them where no index answers it; the bytes a row stores match the rows that
/// store exactly them, as a relationship matches a child to its parent (#51).</item>
/// <item>A null is never a match: a row with no value in a column is not found by looking for null.</item>
/// </list>
/// </summary>
internal static class ValueMatch
{
    /// <summary>
    /// Whether <paramref name="stored"/>, a row's value in <paramref name="column"/> as its index keys are made
    /// of it (<see cref="RowDecoder.DecodeForKey"/>: a Date/Time as the double stored, a Date/Time Extended as its
    /// 42 bytes), is the value <paramref name="asked"/> for.
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
                case DataType.DateTimeExtended:
                    if (asked is StoredExtendedDate bytes) return bytes.Equals(stored);
                    DateTime? says = stored is StoredExtendedDate extended ? extended.Date : stored as DateTime?;
                    return asked is DateTime when && says == when;
                case DataType.Text:
                case DataType.Memo:
                    return SameText(column, Convert.ToString(stored, CultureInfo.InvariantCulture)!, Convert.ToString(asked, CultureInfo.InvariantCulture)!);
                case DataType.Guid:
                    return stored is Guid guid
                        && guid == (asked is Guid wanted ? wanted : Guid.Parse(Convert.ToString(asked, CultureInfo.InvariantCulture)!));
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
            double d => BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(Convert.ToDouble(asked, CultureInfo.InvariantCulture)),
            float f  => BitConverter.GetBytes(f).AsSpan().SequenceEqual(BitConverter.GetBytes(Convert.ToSingle(asked, CultureInfo.InvariantCulture))),
            _        => Equals(stored, asked)
                        || Convert.ToDecimal(stored, CultureInfo.InvariantCulture) == Convert.ToDecimal(asked, CultureInfo.InvariantCulture),
        };
    }

    private static bool IsNumber(object value)
        => value is double or float or decimal or int or long or short or byte or sbyte or uint or ulong or ushort;
}
