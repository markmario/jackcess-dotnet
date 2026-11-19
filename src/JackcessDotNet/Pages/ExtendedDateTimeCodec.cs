namespace JackcessDotNet;

/// <summary>
/// A Date/Time Extended value (Access 2019 and later) as a row holds it: 42 bytes of text, one byte a character -
/// <code>
///   0000000000000737425:0000000110451234567:7\0
/// </code>
/// nineteen digits of whole days since 1 January 0001, a colon, nineteen digits of the time of day in units of
/// 100 nanoseconds (twelve of seconds and seven of the fraction, as Jackcess reads them), and ":7" and a zero
/// byte, the seven being the digits of the fraction. That one is 2 January 2020, 03:04:05.1234567.
/// <para>
/// The value is read and written as a <see cref="DateTime"/>, because that is exactly what it is: Access takes
/// dates from 1 January 0001 to 31 December 9999 to the 100 nanoseconds, a <see cref="DateTime"/>'s own range
/// and its own tick, so days and time of day are its <see cref="DateTime.Ticks"/> divided by a day's and the
/// remainder, and every value Access stores comes back, to the tick, as it was written. The row holds no time
/// zone and no offset - a <see cref="DateTimeOffset"/> would have to invent one - so the <see cref="DateTime"/>
/// read has <see cref="DateTimeKind.Unspecified"/>, and one written is stored as its own date and time, whatever
/// its <see cref="DateTime.Kind"/>: nothing is converted to or from UTC. (Jackcess gives a LocalDateTime, which
/// is the same thing with finer units than the file has.)
/// </para>
/// </summary>
internal static class ExtendedDateTimeCodec
{
    /// <summary>The bytes of a value in a row.</summary>
    internal const int Size = 42;

    private const int Digits        = 19;
    private const int TimeAt        = Digits + 1;
    private const int TrailerAt     = TimeAt + Digits;
    private const long DaysInRange  = 3652059;   // DateTime.MaxValue's day, and one more

    /// <summary>
    /// The value at <paramref name="offset"/>; null for 42 bytes that do not say a date: the whole shape is
    /// looked at, not only the two numbers.
    /// <para>
    /// Access reads the bytes as three numbers with colons between them, loosely, and what it made of each form
    /// here was asked of it (DAO 16.0, the bytes of a value changed in a file it had made). Where it reads the
    /// date the bytes say, so does this: spaces before or after a number's digits, a plus sign before them, and
    /// any byte that is not a digit in the last place, where Access writes a zero byte. Where it reads no value,
    /// this reads null: a colon that is not a colon, a third number that is not a number or has a digit after
    /// it, a minus sign. And these are null here although Access shows something for them, because what it
    /// shows is not a date the bytes say:
    /// </para>
    /// <list type="bullet">
    /// <item>a third number other than 7 or 0. It is the count of digits of the fraction, and Access's reading of
    /// others is not one rule - three digits, 123, it shows as .0000123 - while eight or nine are finer than a
    /// <see cref="DateTime"/>;</item>
    /// <item>a number with something other than a digit in it, which Access reads up to there (a day number
    /// 73742X is day 73742, in the year 202), and a number with no digit, which it reads as zero;</item>
    /// <item>a day after 31 December 9999, which it shows as 30 December 1899, and a time of day of a day or
    /// more, which it shows as 24:00:00.</item>
    /// </list>
    /// Jackcess looks at neither colons nor trailer, and takes each byte of the numbers less '0' for a digit.
    /// <para>
    /// A third number of 0 is Access's own too (#38): the time of day is then in whole seconds -
    /// <c>0000000000000737425:0000000000000011045:0</c> is 2 January 2020, 03:04:05 - and it is what Access writes
    /// when the value comes to the column through a Date/Time, as every value its SQL <c>UPDATE</c> sets does, from
    /// text or from another Date/Time Extended column alike. DAO reads it as that date and time, and so does this.
    /// </para>
    /// </summary>
    public static DateTime? Decode(byte[] row, int offset)
    {
        if (row[offset + Digits] != ':' || row[offset + TrailerAt] != ':') return null;
        long unit = row[offset + TrailerAt + 1] switch
        {
            (byte)'7' => 1,
            (byte)'0' => TimeSpan.TicksPerSecond,
            _         => 0,
        };
        if (unit == 0) return null;
        if (row[offset + Size - 1] is >= (byte)'0' and <= (byte)'9') return null;
        if (ReadNumber(row, offset) is not { } days || days >= DaysInRange) return null;
        if (ReadNumber(row, offset + TimeAt) is not { } time || time >= TimeSpan.TicksPerDay / unit) return null;
        return new DateTime(days * TimeSpan.TicksPerDay + time * unit, DateTimeKind.Unspecified);
    }

    /// <summary>The 42 bytes of <paramref name="value"/>, as Access writes them.</summary>
    public static byte[] Encode(DateTime value)
    {
        var bytes = new byte[Size];
        WriteDigits(bytes, 0, value.Ticks / TimeSpan.TicksPerDay);
        bytes[Digits] = (byte)':';
        WriteDigits(bytes, TimeAt, value.Ticks % TimeSpan.TicksPerDay);
        bytes[TrailerAt]     = (byte)':';
        bytes[TrailerAt + 1] = (byte)'7';
        return bytes;
    }

    // One of the two numbers, in its nineteen bytes: digits - Access fills the field with them - which may have
    // spaces before and after and a plus sign before. Null for anything else, and for no digit at all.
    private static long? ReadNumber(byte[] row, int offset)
    {
        int at = offset, end = offset + Digits;
        while (at < end && row[at] == ' ') at++;
        while (end > at && row[end - 1] == ' ') end--;
        if (at < end && row[at] == '+') at++;
        if (at == end) return null;

        long number = 0;
        for (; at < end; at++)
        {
            int digit = row[at] - '0';
            if (digit is < 0 or > 9) return null;
            // Nineteen nines are more than a long holds; no value this reads as a date is near it.
            if (number > (long.MaxValue - digit) / 10) return null;
            number = number * 10 + digit;
        }
        return number;
    }

    private static void WriteDigits(byte[] bytes, int offset, long number)
    {
        for (int i = Digits - 1; i >= 0; i--)
        {
            bytes[offset + i] = (byte)('0' + number % 10);
            number /= 10;
        }
    }
}
