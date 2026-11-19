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
/// Access writes a value in three layouts, and keys each by its bytes (#51):
/// </para>
/// <list type="bullet">
/// <item>the one above, which its SQL <c>INSERT</c> writes, and this library;</item>
/// <item><c>0000000000000737425:0000000000000011045:0\0</c>, the time of day in whole seconds and a count of 0, which
/// its SQL <c>UPDATE</c> writes, and DAO's <c>AddNew</c> and <c>Edit</c> on a dynaset, and Access's own forms and
/// datasheets: the value is taken through a Date/Time, to the whole second (#38);</item>
/// <item><c>000000000737425:0000000110451234567:7\0</c> and four bytes more, the day in fifteen digits, which DAO's
/// <c>AddNew</c> and <c>Edit</c> on a table-type recordset write - what DAO's <c>OpenRecordset</c> gives a table of the
/// database when it is asked for no other kind (#64). The four bytes after the zero byte are whatever DAO's buffer
/// held, and differ from row to row: they are no part of the value.</item>
/// </list>
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

    /// <summary>Where the third number begins, the count of the fraction's digits: 7 in Access's first form, 0 in its second.</summary>
    internal const int CountAt = TrailerAt + 1;
    private const long DaysInRange  = 3652059;   // DateTime.MaxValue's day, and one more

    // 10 to each count Access reads: the count of the fraction's digits is less than 10.
    private static readonly ulong[] PowersOfTen =
        { 1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000, 1_000_000_000 };

    /// <summary>
    /// The value at <paramref name="offset"/>, read as Access reads it, or null where Access reads no date. Access reads
    /// the bytes as three numbers, not as fields at fixed places (#64; DAO 16 and ACE 16, each rule asked of them with
    /// bytes changed in a file Access made):
    /// <list type="number">
    /// <item>The value ends at its first zero byte; the bytes after it are not read.</item>
    /// <item>It has two colons or more. The day is read from its start, the time of day from after the first colon,
    /// and the count of the fraction's digits from after the last colon.</item>
    /// <item>A number begins with a digit, a white-space byte (a space, a tab, or a line feed, vertical tab, form feed
    /// or carriage return) or a plus sign: anything else there - any other byte, or the end of the value, as where
    /// two colons meet - is an error. The white space is skipped, then one plus sign, and the digits are read up to
    /// the first byte that is not a digit. No digit at all is 0, and a minus sign after the white space ends the
    /// number as any other byte does. A number past 2^64 - 1 is an error.</item>
    /// <item>The day and the count are taken to their low 32 bits: a day of 2^32 + 737425 is day 737425.</item>
    /// <item>A count of 10 or more is an error.</item>
    /// <item>The value is the day, and the time divided by 10 to the count in seconds, and the remainder in units of
    /// 100 nanoseconds, whatever the count: 11045123 with a count of 3 is 03:04:05.0000123, and ACE's own <c>=</c>
    /// compares it so.</item>
    /// </list>
    /// Where Access has an error - DAO gives Empty, and ACE's <c>=</c> fails - this reads null. So it does where
    /// Access reads a value that is no <see cref="DateTime"/>, which Access does not show as a date of its own: a day
    /// after 31 December 9999 (DAO shows 30 December 1899, and ACE puts it after 31 December 9999), a time of day of a
    /// day or more (DAO shows 24:00:00 and on, and ACE puts it after the day's last tick and before the next day), and
    /// a remainder of more than seven digits, from a count of 8 or 9 (DAO shows it as a fraction of eight or nine
    /// places, and ACE's <c>=</c> finds it for no date). Access writes none of these, nor the counts other than 7 and
    /// 0: they are bytes changed by hand, or by another program.
    /// </summary>
    public static DateTime? Decode(byte[] row, int offset)
    {
        int end = offset;
        while (end < offset + Size && row[end] != 0) end++;

        int first = -1, last = -1;
        for (int at = offset; at < end; at++)
        {
            if (row[at] != ':') continue;
            if (first < 0) first = at;
            last = at;
        }
        if (first < 0 || last == first) return null;

        if (ReadNumber(row, offset, end) is not { } day
            || ReadNumber(row, first + 1, end) is not { } time
            || ReadNumber(row, last + 1, end) is not { } count) return null;

        uint days = unchecked((uint)day), digits = unchecked((uint)count);
        if (digits >= PowersOfTen.Length || days >= DaysInRange) return null;
        ulong seconds = time / PowersOfTen[digits], remainder = time % PowersOfTen[digits];
        if (seconds >= TimeSpan.TicksPerDay / TimeSpan.TicksPerSecond || remainder >= TimeSpan.TicksPerSecond) return null;
        return new DateTime(days * TimeSpan.TicksPerDay + (long)seconds * TimeSpan.TicksPerSecond + (long)remainder, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// The value at <paramref name="offset"/> as versions of this library from db6f93b until #64 read it, the
    /// versions that keyed the date they read (#51): two colons at their places in Access's first form, after
    /// nineteen bytes of the day and nineteen of the time, a count of 7 or 0 after the second, anything but a digit in
    /// the last byte, and each number digits that spaces may stand before and after and a plus sign before. Null for
    /// anything else - the layout DAO's table-type recordset writes among it, with its day of fifteen digits. Versions
    /// from 2930d3f read a count of 7 alone.
    /// </summary>
    public static DateTime? DecodeAsEarlierVersions(byte[] row, int offset)
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
        if (ReadNumberAsEarlierVersions(row, offset) is not { } days || days >= DaysInRange) return null;
        if (ReadNumberAsEarlierVersions(row, offset + TimeAt) is not { } time || time >= TimeSpan.TicksPerDay / unit) return null;
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

    /// <summary>
    /// The 42 bytes of <paramref name="value"/> in Access's second form, as its SQL <c>UPDATE</c> writes a value -
    /// the time of day in whole seconds and a count of 0 - or null for a value with a fraction of a second, which
    /// that form does not hold.
    /// </summary>
    public static byte[]? EncodeInSeconds(DateTime value)
    {
        long time = value.Ticks % TimeSpan.TicksPerDay;
        if (time % TimeSpan.TicksPerSecond != 0) return null;
        var bytes = new byte[Size];
        WriteDigits(bytes, 0, value.Ticks / TimeSpan.TicksPerDay);
        bytes[Digits] = (byte)':';
        WriteDigits(bytes, TimeAt, time / TimeSpan.TicksPerSecond);
        bytes[TrailerAt]     = (byte)':';
        bytes[TrailerAt + 1] = (byte)'0';
        return bytes;
    }

    /// <summary>
    /// The bytes of each form Access writes <paramref name="value"/> in: the one its <c>INSERT</c> writes
    /// (<see cref="Encode"/>), and for a value in whole seconds the one its SQL <c>UPDATE</c> writes
    /// (<see cref="EncodeInSeconds"/>). Access keys each form by its bytes, so a row with the value is under one of
    /// their keys.
    /// </summary>
    public static IEnumerable<byte[]> FormsOf(DateTime value)
    {
        yield return Encode(value);
        if (EncodeInSeconds(value) is { } inSeconds) yield return inSeconds;
    }

    // One of the three numbers, read as Access reads it (Decode): from 'at' to the end of the value. Null for an error.
    private static ulong? ReadNumber(byte[] row, int at, int end)
    {
        if (at >= end || !(IsDigit(row[at]) || IsWhiteSpace(row[at]) || row[at] == '+')) return null;
        while (at < end && IsWhiteSpace(row[at])) at++;
        if (at < end && row[at] == '+') at++;

        ulong number = 0;
        for (; at < end && IsDigit(row[at]); at++)
        {
            ulong digit = (ulong)(row[at] - '0');
            if (number > (ulong.MaxValue - digit) / 10) return null;
            number = number * 10 + digit;
        }
        return number;
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    // White space as C's isspace has it, and Access: a space, and a tab, line feed, vertical tab, form feed or
    // carriage return. Not a no-break space, nor the separators 0x1C to 0x1F (measured).
    private static bool IsWhiteSpace(byte b) => b is (byte)' ' or >= 0x09 and <= 0x0D;

    // One of the two numbers as DecodeAsEarlierVersions reads it, in its nineteen bytes: digits - Access fills the
    // field with them - which may have spaces before and after and a plus sign before. Null for anything else, and
    // for no digit at all.
    private static long? ReadNumberAsEarlierVersions(byte[] row, int offset)
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
