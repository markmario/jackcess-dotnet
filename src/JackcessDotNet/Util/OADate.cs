using System.Runtime.CompilerServices;

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
/// <para>
/// A 64-bit process has no x87 unit to compute doubles with, and there the runtime's two conversions give exactly
/// what the integer ones do, five to ten times as fast (#125): so a 64-bit process converts with the runtime's, and
/// any other with the integers, decided once. OADateChoiceTests holds the two to each other over millions of inputs
/// in every configuration the tests run in.
/// </para>
/// </summary>
internal static class OADate
{
    /// <summary>Whether this process converts with the runtime's own conversions: a 64-bit one.</summary>
    internal static readonly bool RuntimeConverts = Environment.Is64BitProcess;

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
        => RuntimeConverts ? RuntimeFromDateTime(date) : FromDateTimeInIntegers(date);

    // The runtime's conversion; out of line in the builds .NET Framework runs, where inlined it slowed the integers'
    // branch of a 32-bit process by half.
#if NETFRAMEWORK || NETSTANDARD2_0
    [MethodImpl(MethodImplOptions.NoInlining)]
#endif
    private static double RuntimeFromDateTime(DateTime date) => date.ToOADate();

    /// <summary>
    /// The <see cref="DateTime"/> <paramref name="days"/> reads as: <see cref="DateTime.FromOADate"/>'s, to the
    /// millisecond, as a 64-bit process reads it (<see cref="ToDateTimeInIntegers"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="days"/> is not above -657435 and below 2958466, or rounds
    /// to a time past the last <see cref="DateTime"/>.</exception>
    internal static DateTime ToDateTime(double days)
        => RuntimeConverts ? DateTime.FromOADate(days) : ToDateTimeInIntegers(days);

    /// <summary><see cref="FromDateTime"/> in integer arithmetic, in any process.</summary>
    /// <exception cref="OverflowException"><paramref name="date"/> is before 1 January 100.</exception>
    internal static double FromDateTimeInIntegers(DateTime date)
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
        return Days(millis);
    }

    /// <summary>
    /// <see cref="ToDateTime"/> in integer arithmetic, in any process. As <see cref="DateTime.FromOADate"/> rounds it,
    /// the number of milliseconds is the double product of the days and 86,400,000, and a half added to that (taken
    /// from it, below zero) as doubles add, cut toward zero: so a number a hair under a millisecond's half can read as
    /// the next millisecond, as it does in a 64-bit process.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="days"/> is not above -657435 and below 2958466, or rounds
    /// to a time past the last <see cref="DateTime"/>.</exception>
    internal static DateTime ToDateTimeInIntegers(double days)
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
    // zero, so this is the magnitude's, with the sign of the days.
    //
    // |days| is under 2958466, under 2^22 (ToDateTimeInIntegers), and a magnitude under 2^-28 is 0: its product is
    // under a third, and with the half added still under one. Every other is a normal double, of a mantissa of 53 bits
    // and an exponent from -80 to -31, so the product with 84,375 has 69 or 70 bits, and the power of two each step
    // leaves is below zero: no length has to be counted.
    private static long Millis(double days)
    {
        long bits = BitConverter.DoubleToInt64Bits(days);
        int field = (int)((bits >> 52) & 0x7FF);
        if (field < 1023 - 28) return 0;
        ulong mantissa = (ulong)(bits & 0xF_FFFF_FFFF_FFFF) | (1UL << 52);
        int power = field - 1075 + MillisPerDayTwos;   // |days| × 86,400,000 = mantissa × 84,375 × 2^power

        // The product, mantissa × 84,375: 69 or 70 bits, cut to 53, half to even.
#if NETFRAMEWORK || NETSTANDARD2_0
        ulong low = (mantissa & 0xFFFF_FFFF) * MillisPerDayOdd;
        ulong middle = (mantissa >> 32) * MillisPerDayOdd + (low >> 32);
        ulong high = middle >> 32;   // under 2^6
        low = (middle << 32) | (low & 0xFFFF_FFFF);
#else
        ulong high = Math.BigMul(mantissa, MillisPerDayOdd, out ulong low);   // high under 2^6
#endif
        int cut = high >= 1UL << 5 ? 17 : 16;
        ulong product = RoundedHalfToEven((high << (64 - cut)) | (low >> cut), low & ((1UL << cut) - 1), cut,
                                          ref power);

        // product × 2^power, from 2^-54 to 2^-2 a unit; the half added, under 2^54 of those units, so at most one bit
        // too many to round off.
        ulong sum = product + (1UL << (-power - 1));
        if (sum >= 1UL << 53) sum = RoundedHalfToEven(sum >> 1, sum & 1, 1, ref power);
        long magnitude = (long)(sum >> -power);
        return bits < 0 ? -magnitude : magnitude;
    }

    // 'kept', the bits above the 'cut' bits 'dropped', rounded by them half to even; 'power' raised by the bits cut,
    // and by one more where rounding up carried into a 54th bit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    // The double nearest millis / 86,400,000, half to even, as a double division of the two gives it. The magnitude of
    // millis is under 2^53, so it is a double itself. 86,400,000 is 84,375 × 2^10: the quotient by 84,375, a constant
    // the compiler divides by without a division where it can, is scaled by 2^-10.
    private static double Days(long millis)
    {
        if (millis == 0) return 0.0;
        ulong n = (ulong)(millis < 0 ? -millis : millis);
        ulong quotient = n / MillisPerDayOdd, remainder = n % MillisPerDayOdd;
        int power = -MillisPerDayTwos;   // the number is (quotient + remainder / 84,375) × 2^power

        // Bits of the quotient until there are 53: at most 47 at a time, as the remainder is under 2^17.
        while (quotient < 1UL << 52)
        {
            int take = Math.Min(47, 53 - BitLength(quotient));
            remainder <<= take;
            quotient = (quotient << take) | (remainder / MillisPerDayOdd);
            remainder %= MillisPerDayOdd;
            power -= take;
        }

        // The rest, remainder / 84,375, against a half.
        ulong twice = remainder << 1;
        if (twice > MillisPerDayOdd || (twice == MillisPerDayOdd && (quotient & 1) != 0))
        {
            quotient++;
            if (quotient == 1UL << 53)
            {
                quotient >>= 1;
                power++;
            }
        }
        long bits = ((long)(power + 52 + 1023) << 52) | (long)(quotient & 0xF_FFFF_FFFF_FFFF);
        return BitConverter.Int64BitsToDouble(millis < 0 ? bits | long.MinValue : bits);
    }

    // The number of bits of the value, 0 for 0: by the processor's count of leading zeros where the runtime has it, and
    // otherwise halving the value to a byte and looking that up, in 32-bit arithmetic past the first step.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitLength(ulong value)
    {
#if NETFRAMEWORK || NETSTANDARD2_0
        int length = 0;
        uint word = (uint)value;
        if (value >> 32 != 0)
        {
            word = (uint)(value >> 32);
            length = 32;
        }
        if (word >> 16 != 0)
        {
            word >>= 16;
            length += 16;
        }
        if (word >> 8 != 0)
        {
            word >>= 8;
            length += 8;
        }
        return length + ByteBitLengths[word];
#else
        return 64 - System.Numerics.BitOperations.LeadingZeroCount(value);
#endif
    }

#if NETFRAMEWORK || NETSTANDARD2_0
    private static readonly byte[] ByteBitLengths = MakeByteBitLengths();

    private static byte[] MakeByteBitLengths()
    {
        var lengths = new byte[256];
        for (int i = 1; i < lengths.Length; i++) lengths[i] = (byte)(lengths[i >> 1] + 1);
        return lengths;
    }
#endif
}
