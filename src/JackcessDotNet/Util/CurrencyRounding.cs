using System.Globalization;
using System.Numerics;

namespace JackcessDotNet.Util;

/// <summary>
/// The Currency a caller's number is, as Access makes one (#65): the number's exact value - a double's or a float's
/// mantissa times a power of two, a decimal's digits, an integer - taken to ten-thousandths, half to even. Measured
/// with ACE 16 and DAO, in an .mdb and an .accdb, and every way they write a Currency and find one through an index
/// agrees: an <c>INSERT</c> and an <c>UPDATE</c> with a typed parameter, DAO's <c>AddNew</c> and <c>Edit</c> on a
/// dynaset and on a table, an index's <c>=</c>, <c>BETWEEN</c>, <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c> and
/// <c>&lt;</c>, and DAO's <c>Seek</c>. The double 0.03125, an exact half, is 0.0312, and 0.09375 is 0.0938; 0.00015,
/// a double just under the half it is written as, is 0.0001; 562949953421312.5, of more significant digits than 15,
/// is itself; the decimal 1.23465 is 1.2346, and 0.00015 is 0.0002. Taken through a decimal of 15 significant digits
/// first - what <c>Convert.ToDecimal</c> does on .NET Framework and .NET up to 10, where .NET 11 converts exactly - or
/// rounded half away from zero, some of these came out the other way. Nothing here is converted, so every runtime
/// takes a number alike.
/// <para>
/// Where Access does otherwise this does not follow it. ACE's scan, with no index to answer, takes a decimal through
/// a double - 1.23465 is 1.2347 there and 0.00015 is 0.0001 - and so does not find the row its own <c>INSERT</c> wrote
/// with that decimal. ACE and DAO write NaN as the least Currency, and an <c>=</c> finds that row for NaN; here NaN is
/// no Currency, as an infinity and a number past the range are not, which Access refuses ("Overflow"). And Access
/// reads text as a number by the machine's locale ('1,5' is 15 where a comma groups digits): here text is not a
/// number, nor is a Yes/No (which Access writes as -1) or any other value that is not one of .NET's numbers.
/// </para>
/// </summary>
internal static class CurrencyRounding
{
    /// <summary>Whether a value is a number a Currency is made of: an integer of any size, a float, a double or a decimal.</summary>
    internal static bool IsNumber(object? value)
        => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>
    /// The ten-thousandths <paramref name="value"/> is taken to, half to even; false for a value that is not a number
    /// (<see cref="IsNumber"/>) or is a number no Currency holds: not a number, an infinity, or past a Currency's
    /// range once taken to four places.
    /// </summary>
    internal static bool TryUnits(object? value, out long units)
    {
        switch (value)
        {
            case double d:  return TryUnits(d, out units);
            case float f:   return TryUnits((double)f, out units);   // every float is exactly a double
            case decimal m: return TryUnits(m, out units);
            case ulong u:
                units = u <= GreatestWhole ? (long)u * 10_000 : 0;
                return u <= GreatestWhole;
            case sbyte or byte or short or ushort or int or uint or long:
                long whole = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                bool held = whole >= -GreatestWhole && whole <= GreatestWhole;
                units = held ? whole * 10_000 : 0;
                return held;
            default:
                units = 0;
                return false;
        }
    }

    /// <summary>
    /// The ten-thousandths <paramref name="value"/> is taken to, half to even; false for a number no Currency holds:
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
        var scaled = mantissa * Scale;
        bool negative = bits < 0;
        return exponent >= 0
            ? Signed(scaled << exponent, negative, out units)
            : Signed(HalfToEven(scaled, BigInteger.One << -exponent), negative, out units);
    }

    /// <summary>
    /// The ten-thousandths <paramref name="value"/> is taken to, its digits exactly, half to even; false past a
    /// Currency's range.
    /// </summary>
    internal static bool TryUnits(decimal value, out long units)
    {
        // Four places or fewer, within the range - every value a row is read with among them: exact in a decimal.
        if (value >= Least && value <= Greatest && decimal.Round(value, 4) == value)
        {
            units = decimal.ToInt64(value * 10_000m);
            return true;
        }

        // value = magnitude / 10^scale, exactly; in ten-thousandths, magnitude × 10,000 / 10^scale.
        int[] parts = decimal.GetBits(value);
        var magnitude = new BigInteger((uint)parts[2]) << 64 | new BigInteger((uint)parts[1]) << 32 | new BigInteger((uint)parts[0]);
        int scale = (parts[3] >> 16) & 0xFF;
        bool negative = parts[3] < 0;
        return scale <= 4
            ? Signed(magnitude * BigInteger.Pow(10, 4 - scale), negative, out units)
            : Signed(HalfToEven(magnitude, BigInteger.Pow(10, scale - 4)), negative, out units);
    }

    /// <summary>
    /// The ten-thousandths a row stores for <paramref name="value"/>, given for a Currency column: what
    /// <see cref="TryUnits(object?, out long)"/> takes it to.
    /// </summary>
    /// <exception cref="InvalidCastException"><paramref name="value"/> is not a number (<see cref="IsNumber"/>).</exception>
    /// <exception cref="OverflowException">
    /// <paramref name="value"/> is a number no Currency holds: not a number, an infinity, or past the range.
    /// </exception>
    internal static long Units(object value)
    {
        if (!IsNumber(value))
            throw new InvalidCastException($"A Currency is made of a number, and {NotANumber(value)}.");
        if (!TryUnits(value, out long units))
            throw new OverflowException($"A Currency is made of a number, and {NotHeld(value)}.");
        return units;
    }

    /// <summary>A Currency of <paramref name="units"/> ten-thousandths, as a decimal: what a row's value reads as.</summary>
    internal static decimal ToDecimal(long units) => units / 10_000m;

    /// <summary>The least Currency, as a decimal.</summary>
    internal const decimal Least = -922_337_203_685_477.5808m;

    /// <summary>The greatest Currency, as a decimal.</summary>
    internal const decimal Greatest = 922_337_203_685_477.5807m;

    /// <summary>Why <paramref name="value"/>, which is not a number (<see cref="IsNumber"/>), makes no Currency.</summary>
    internal static string NotANumber(object value)
        => value is string
            ? $"{Shown(value)} is text, which is not read as a number"
            : $"{Shown(value)} is a {value.GetType().Name}, not a number";

    /// <summary>
    /// Why no Currency holds <paramref name="value"/>, a number <see cref="TryUnits(object?, out long)"/> refuses: it
    /// is not a number, or it is past the range.
    /// </summary>
    internal static string NotHeld(object value)
        => value is double.NaN or float.NaN
            ? $"{Shown(value)} is not a number"
            : $"{Shown(value)} is past a Currency's range, {Least.ToString(CultureInfo.InvariantCulture)} to " +
              $"{Greatest.ToString(CultureInfo.InvariantCulture)}, once taken to four places";

    // A value as a message shows it: a double or a float in its round-trip form, the same on every target.
    private static string Shown(object value) => value switch
    {
        double d => ExactNumber.Text(d),
        float f  => ExactNumber.Text(f),
        string s => $"'{(s.Length > 40 ? s.Substring(0, 40) + "..." : s)}'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // A Currency's ten-thousandths in one: 10,000.
    private static readonly BigInteger Scale = 10_000;

    // The greatest whole number a Currency holds, and the least's magnitude: the range's whole part either way.
    private const long GreatestWhole = 922_337_203_685_477;

    // dividend / divisor, both positive, rounded to a whole number, half to even.
    private static BigInteger HalfToEven(BigInteger dividend, BigInteger divisor)
    {
        var whole = BigInteger.DivRem(dividend, divisor, out BigInteger rest);
        int againstHalf = (rest << 1).CompareTo(divisor);
        return againstHalf > 0 || againstHalf == 0 && !whole.IsEven ? whole + 1 : whole;
    }

    // A magnitude in ten-thousandths, with its sign, when a Currency holds it.
    private static bool Signed(BigInteger magnitude, bool negative, out long units)
    {
        units = 0;
        if (magnitude > (negative ? MostNegative : long.MaxValue)) return false;
        units = negative ? (long)-magnitude : (long)magnitude;
        return true;
    }

    // The magnitude of the least Currency: a long's least value.
    private static readonly BigInteger MostNegative = new BigInteger(long.MaxValue) + 1;
}
