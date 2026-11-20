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
/// <item>A number is equal to a number of another type that is exactly the same number (#49): a Byte, Integer,
/// Long or Large Number column's 5 for a caller's 5L, 5m or 5.0, and for no number that is not that whole number
/// (5.5, 5.5m, 70000 in an Integer, a double one past a Large Number's); a Decimal exactly, as decimals compare, a
/// caller's double taken to a decimal as Access takes one (to 15 significant digits). Measured with ACE, its
/// <c>=</c> answers otherwise in places, and not alike through an index and without one; where its two answers
/// differ, this is the exact one. In a whole-number column that is its scan's: -0.0 is 0, -2^63 as a double is
/// a Large Number's <see cref="long.MinValue"/>, and a decimal with a fraction is no whole number (a Large
/// Number's scan found 0 for every decimal), where ACE's index finds nothing for the first two and rounds the
/// third (5.5m finds 6, 0.1m 0, -1.23465m -1). A double past 2^53 is the Large Number it is exactly, as ACE's
/// index has it, where its scan compares the two as doubles.
/// A Currency is the caller's number taken to its four places by its exact value, half to even, as a row is written
/// with it and as ACE's <c>=</c> through an index and DAO's <c>Seek</c> take one (<see cref="Util.CurrencyRounding"/>,
/// #65): the double 0.03125 is the row of 0.0312, 0.00015, a double just under that half, the row of 0.0001, and
/// 1.23465m the row of 1.2346, where ACE's scan takes a decimal through a double and finds 1.2347.
/// A Double or a Single is the caller's number as a row would store it, by its bits,
/// as ACE's index has it: +0.0 and -0.0 are two keys, where ACE's scan finds both for either, and so are a Double
/// and the next Double after it. NaN is no number a column holds, where ACE's scan finds every row - in a Currency
/// column, ACE's <c>=</c> finds the row of the least Currency, which ACE and DAO write for NaN. Text is no
/// number.</item>
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
                case DataType.Currency:
                    // The caller's number in ten-thousandths, as Access takes it to a Currency and as a row is written
                    // and keyed with it (Util.CurrencyRounding, #49, #65): its exact value, half to even. Text is no
                    // number, and NaN, an infinity or a number past the range no row's value.
                    return Util.CurrencyRounding.TryUnits(stored, out long holds)
                        && Util.CurrencyRounding.TryUnits(asked, out long units) && units == holds;
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

    // The double furthest from 'inside' towards 'outside' that still reads as the date. What is halved is the doubles'
    // places in order (Place), not the doubles: two places one apart are two doubles with none between, so the end is
    // the last place that reads as the date. Halving the doubles themselves ended only when a middle came out equal
    // to an end, which in a 32-bit .NET Framework process it never did once something in the process - ACE's OLE DB
    // provider does - had left the x87 unit computing with 64 bits of precision: the middle of two neighbours was a
    // number between them, and the halving went on for ever (#98). Integers halve alike on every runtime and in
    // every state of the unit, and the halving ends within 64 steps.
    private static double Edge(double inside, double outside, DateTime date)
    {
        if (ReadsAs(outside) == date) return outside;
        long at = Place(inside), past = Place(outside);
        while (Distance(at, past) > 1)
        {
            ulong half = Distance(at, past) / 2;
            long middle = at < past ? at + (long)half : at - (long)half;
            if (ReadsAs(FromPlace(middle)) == date) at = middle;
            else past = middle;
        }
        return FromPlace(at);
    }

    // Where a double is among all doubles, in order: neighbours are one apart, a negative number's place is negative,
    // and 0 and -0 are both at 0. A positive double's bits count up with it; a negative one's count up with its
    // magnitude, so their negation counts up with the number. A Date/Time a row stores is a finite number.
    private static long Place(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return bits < 0 ? -(bits & long.MaxValue) : bits;
    }

    private static double FromPlace(long place)
        => BitConverter.Int64BitsToDouble(place < 0 ? -place | long.MinValue : place);

    // How far apart two places are, as an unsigned number: the places of two numbers either side of 0 can be further
    // apart than a long holds.
    private static ulong Distance(long a, long b) => unchecked(a < b ? (ulong)(b - a) : (ulong)(a - b));

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
            // A Decimal's negative zero is another value than its zero, as the row stores them (#80).
            (decimal a, decimal b) => a == b && decimal.GetBits(a)[3] < 0 == decimal.GetBits(b)[3] < 0,
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
