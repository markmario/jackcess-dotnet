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
    /// The value at <paramref name="offset"/>; null for bytes that are not such a value - something other than a
    /// digit where a digit goes, or a day or a time of day past what there is.
    /// </summary>
    public static DateTime? Decode(byte[] row, int offset)
    {
        if (ReadDigits(row, offset) is not { } days || days >= DaysInRange) return null;
        if (ReadDigits(row, offset + TimeAt) is not { } time || time >= TimeSpan.TicksPerDay) return null;
        return new DateTime(days * TimeSpan.TicksPerDay + time, DateTimeKind.Unspecified);
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

    private static long? ReadDigits(byte[] row, int offset)
    {
        long number = 0;
        for (int i = 0; i < Digits; i++)
        {
            int digit = row[offset + i] - '0';
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
