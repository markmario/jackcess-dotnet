namespace JackcessDotNet.Util;

/// <summary>
/// A Date/Time as a row stores it - an OLE Automation date, days since 30 December 1899 with the time of day as the
/// fraction - to and from a <see cref="DateTime"/>, exactly as <see cref="DateTime.ToOADate"/> and
/// <see cref="DateTime.FromOADate"/> convert it in a 64-bit process, and in integer arithmetic. Those two compute in
/// doubles, which a 32-bit .NET Framework process computes with the x87 unit; once something in the process has left
/// that unit computing with 64 bits of precision - ACE's OLE DB provider does, on the thread that ran a statement - a
/// product is rounded once instead of twice, and the result can be another. Measured there (#100): of 15 million
/// stored numbers chosen either side of a millisecond's half, 241 read as the millisecond beside the one a 64-bit
/// process reads (5.7870370370370361E-09, a little under half a millisecond after midnight, as midnight rather than
/// 00:00:00.001); and of 3.4 million dates, 783 were written as the double one above or below. Integers compute alike
/// on every runtime and in every state of the unit.
/// </summary>
internal static class OADate
{
    private const long MillisPerDay = 86_400_000;

    // 86,400,000 is 84,375 × 2^10.
    private const uint MillisPerDayOdd = 84_375;
    private const int MillisPerDayTwos = 10;

    private static readonly long Epoch = new DateTime(1899, 12, 30).Ticks;
    private static readonly long First = new DateTime(100, 1, 1).Ticks;
    private static readonly long EpochMillis = Epoch / TimeSpan.TicksPerMillisecond;
    private static readonly long EndMillis = (DateTime.MaxValue.Ticks + 1) / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// The number a row stores for <paramref name="date"/>: <see cref="DateTime.ToOADate"/>'s. The date is cut to the
    /// millisecond, toward 30 December 1899, and that many days taken to the nearest double, half to even; a time of
    /// day alone, a date on 1 January of year 1, is a time on 30 December 1899.
    /// </summary>
    /// <exception cref="OverflowException"><paramref name="date"/> is before 1 January 100.</exception>
    internal static double FromDateTime(DateTime date)
    {
        long ticks = date.Ticks;
        if (ticks == 0) return 0.0;
        if (ticks < TimeSpan.TicksPerDay) ticks += Epoch;
        if (ticks < First) throw new OverflowException("Not a legal OleAut date.");
        long millis = (ticks - Epoch) / TimeSpan.TicksPerMillisecond;

        // Before 30 December 1899 the whole days count back and the time of day forward: -1.25 is 29 December at 06:00.
        if (millis < 0)
        {
            long time = millis % MillisPerDay;
            if (time != 0) millis -= (MillisPerDay + time) * 2;
        }
        return Quotient(millis, MillisPerDay);
    }

    /// <summary>
    /// The <see cref="DateTime"/> <paramref name="days"/> reads as: <see cref="DateTime.FromOADate"/>'s, to the
    /// millisecond. As that rounds it, the number of milliseconds is the double product of the days and 86,400,000, and
    /// a half added to that (taken from it, below zero) as doubles add, cut toward zero: so a number a hair under a
    /// millisecond's half can read as the next millisecond, as it does in a 64-bit process.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="days"/> is not above -657435 and below 2958466, or rounds
    /// to a time past the last <see cref="DateTime"/>.</exception>
    internal static DateTime ToDateTime(double days)
    {
        if (!(days < 2958466.0) || !(days > -657435.0)) throw new ArgumentException("Not a legal OleAut date.", nameof(days));
        long millis = Millis(days);
        if (millis < 0) millis -= millis % MillisPerDay * 2;
        millis += EpochMillis;
        if (millis < 0 || millis >= EndMillis) throw new ArgumentException("OleAut date did not convert to a DateTime correctly.", nameof(days));
        return new DateTime(millis * TimeSpan.TicksPerMillisecond);
    }

    // (long)(days * 86400000 + (days >= 0 ? 0.5 : -0.5)) as doubles compute it: the product rounded to 53 bits, half to
    // even; the half added and that rounded likewise; cut toward zero. Rounding half to even is the same either side of
    // zero, so this is the magnitude's, with the sign of the days. |days| is under 2958466, so the product is under 2^48.
    private static long Millis(double days)
    {
        long bits = BitConverter.DoubleToInt64Bits(days);
        int field = (int)((bits >> 52) & 0x7FF);
        ulong mantissa = (ulong)(bits & 0xF_FFFF_FFFF_FFFF);
        if (field != 0) mantissa |= 1UL << 52;
        if (mantissa == 0) return 0;
        int exponent = (field == 0 ? 1 : field) - 1075;   // |days| = mantissa × 2^exponent

        // The product, mantissa × 84,375 × 2^(exponent + 10), in two halves: under 2^70.
        ulong low = (mantissa & 0xFFFF_FFFF) * MillisPerDayOdd;
        ulong middle = (mantissa >> 32) * MillisPerDayOdd + (low >> 32);
        ulong high = middle >> 32;
        low = (middle << 32) | (low & 0xFFFF_FFFF);
        int length = high != 0 ? 64 + BitLength(high) : BitLength(low);
        int power = exponent + MillisPerDayTwos;
        ulong product = low;
        if (length > 53)
        {
            int cut = length - 53;
            ulong kept = (high << (64 - cut)) | (low >> cut);
            product = RoundedHalfToEven(kept, low & ((1UL << cut) - 1), cut, ref power);
        }

        // product × 2^power; the half added. A product under a quarter is still under one with it.
        long magnitude;
        if (power >= 0)
            magnitude = (long)(product << power);   // a whole number, of fewer than 49 bits: the half is kept and cut off
        else if (BitLength(product) + power <= -2)
            magnitude = 0;
        else
        {
            ulong sum = product + (1UL << (-power - 1));   // (product × 2^power + 1/2) × 2^-power, under 2^55
            int sumLength = BitLength(sum);
            if (sumLength > 53)
            {
                int cut = sumLength - 53;
                sum = RoundedHalfToEven(sum >> cut, sum & ((1UL << cut) - 1), cut, ref power);
            }
            magnitude = power >= 0 ? (long)(sum << power) : -power >= 64 ? 0 : (long)(sum >> -power);
        }
        return bits < 0 ? -magnitude : magnitude;
    }

    // 'kept', the bits above the 'cut' bits 'dropped', rounded by them half to even; 'power' raised by the bits cut,
    // and by one more where rounding up carried into a 54th bit.
    private static ulong RoundedHalfToEven(ulong kept, ulong dropped, int cut, ref int power)
    {
        ulong half = 1UL << (cut - 1);
        if (dropped > half || (dropped == half && (kept & 1) != 0)) kept++;
        power += cut;
        if (kept == 1UL << 53)
        {
            kept >>= 1;
            power++;
        }
        return kept;
    }

    // The double nearest numerator / denominator, half to even, as a double division of the two gives it. The
    // numerator's magnitude is under 2^53, so it is a double itself; the denominator is above 0 and under 2^32.
    private static double Quotient(long numerator, long denominator)
    {
        if (numerator == 0) return 0.0;
        ulong n = (ulong)(numerator < 0 ? -numerator : numerator), d = (ulong)denominator;
        ulong quotient = n / d, remainder = n % d;
        int power = 0;   // the number is (quotient + remainder / d) × 2^power

        // Bits of the quotient until there are 53: at most 32 at a time, as the remainder is under 2^32.
        while (quotient < 1UL << 52)
        {
            int take = Math.Min(32, 53 - BitLength(quotient));
            remainder <<= take;
            quotient = (quotient << take) | (remainder / d);
            remainder %= d;
            power -= take;
        }

        // The rest, remainder / d, against a half.
        ulong twice = remainder << 1;
        if (twice > d || (twice == d && (quotient & 1) != 0))
        {
            quotient++;
            if (quotient == 1UL << 53)
            {
                quotient >>= 1;
                power++;
            }
        }
        long bits = ((long)(power + 52 + 1023) << 52) | (long)(quotient & 0xF_FFFF_FFFF_FFFF);
        return BitConverter.Int64BitsToDouble(numerator < 0 ? bits | long.MinValue : bits);
    }

    private static int BitLength(ulong value)
    {
        int length = 0;
        while (value != 0)
        {
            value >>= 1;
            length++;
        }
        return length;
    }
}
