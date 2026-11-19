using System.Numerics;

namespace JackcessDotNet.Util;

/// <summary>
/// A <see cref="double"/> or a <see cref="float"/> looked for in a Currency column, taken to the column's four places
/// as Access takes it: its exact binary value - mantissa times a power of two - rounded to ten-thousandths, half to
/// even. Measured with ACE 16 (an <c>=</c> through an index and without one) and DAO's <c>Seek</c>, which agree:
/// 0.03125 and -0.03125, exact halves, find 0.0312 and -0.0312, and 0.09375 finds 0.0938; 0.00015, a double just
/// under the half it is written as, finds 0.0001, and 1.234549999999999 finds 1.2345. Taken through a decimal
/// first - cut to 15 significant digits on .NET Framework and .NET up to 10, exact on .NET 11 - such a number came
/// out a half, or the wrong side of one, and found the row above. Nothing here is converted, so every runtime
/// rounds alike.
/// </summary>
internal static class CurrencyRounding
{
    /// <summary>
    /// The ten-thousandths <paramref name="value"/> rounds to, half to even; false for a number no Currency holds:
    /// not a number, an infinity, or past a Currency's range.
    /// </summary>
    internal static bool TryUnits(double value, out long units)
    {
        units = 0;
        long bits = BitConverter.DoubleToInt64Bits(value);
        int biased = (int)((bits >> 52) & 0x7FF);
        if (biased == 0x7FF) return false;                           // not a number, or an infinity
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        if (biased == 0 && fraction == 0) return true;               // 0 and -0

        // value = mantissa × 2^exponent, exactly; in ten-thousandths, mantissa × 10,000 × 2^exponent.
        var mantissa = new BigInteger(biased == 0 ? fraction : fraction | (1L << 52));
        int exponent = (biased == 0 ? 1 : biased) - 1075;
        var scaled = mantissa * 10_000;
        BigInteger whole;
        if (exponent >= 0)
        {
            whole = scaled << exponent;
        }
        else
        {
            int shift = -exponent;
            whole = scaled >> shift;
            var rest = scaled - (whole << shift);
            int againstHalf = (rest << 1).CompareTo(BigInteger.One << shift);
            if (againstHalf > 0 || againstHalf == 0 && !whole.IsEven) whole += 1;
        }

        bool negative = bits < 0;
        if (whole > (negative ? MostNegative : long.MaxValue)) return false;
        units = negative ? (long)-whole : (long)whole;
        return true;
    }

    // The magnitude of the least Currency: a long's least value.
    private static readonly BigInteger MostNegative = new BigInteger(long.MaxValue) + 1;
}
