using System.Globalization;

namespace JackcessDotNet.Util;

/// <summary>
/// A number as a <see cref="decimal"/> the way OLE Automation makes one (<c>VarDecFromR8</c>, <c>VarDecFromR4</c>):
/// a <see cref="double"/> rounded to 15 significant digits and a <see cref="float"/> to 7, the digits its type
/// holds, so that 0.1 is 0.1. That is how Access takes a number asked of a Decimal column to a Decimal (measured:
/// <c>=</c> with the double 0.1, or the single 0.1, finds the Decimal 0.1; #49), and how
/// <see cref="Convert.ToDecimal(double)"/> converts on .NET Framework and on .NET up to 10. .NET 11 converts
/// exactly - the double 0.1 is 0.1000000000000000055511151231 - so on a runtime that does, the rounding is done
/// here, as those runtimes do it: the number scaled by a power of ten to a whole number of that many digits, in
/// double arithmetic, and rounded half to even.
/// </summary>
internal static class AutomationDecimal
{
    // Whether this runtime's own conversion rounds as OLE Automation does: then it is the one used.
    private static readonly bool RuntimeRounds = Convert.ToDecimal(0.1) == 0.1m && Convert.ToDecimal(0.1f) == 0.1m;

    /// <summary>
    /// <paramref name="value"/> as a decimal: a double or a float as OLE Automation rounds one, anything else as
    /// <see cref="Convert.ToDecimal(object, IFormatProvider)"/> converts it, text in <paramref name="provider"/>'s
    /// format (null: the current culture's).
    /// </summary>
    /// <exception cref="OverflowException">The number is past what a decimal holds, or not a number.</exception>
    /// <exception cref="FormatException">The value is text that is no number.</exception>
    /// <exception cref="InvalidCastException">The value is of no type a number is converted from.</exception>
    internal static decimal From(object? value, IFormatProvider? provider) => value switch
    {
        double d => From(d),
        float f  => From(f),
        _        => Convert.ToDecimal(value, provider),
    };

    /// <summary><paramref name="value"/> rounded to 15 significant digits, as <c>VarDecFromR8</c> rounds it.</summary>
    internal static decimal From(double value) => RuntimeRounds ? Convert.ToDecimal(value) : Rounded(value, 15);

    /// <summary><paramref name="value"/> rounded to 7 significant digits, as <c>VarDecFromR4</c> rounds it.</summary>
    internal static decimal From(float value) => RuntimeRounds ? Convert.ToDecimal(value) : Rounded(value, 7);

    /// <summary>
    /// <paramref name="value"/> rounded to <paramref name="digits"/> significant digits as OLE Automation rounds it,
    /// whatever the runtime: what <see cref="From(double)"/> and <see cref="From(float)"/> give on one whose own
    /// conversion does not round (and what tests hold to the conversion of one that does).
    /// </summary>
    internal static decimal Rounded(double value, int digits)
    {
        // The power of two the number is under: 2^(exponent - 1) <= |value| < 2^exponent. A float's is a double's.
        int exponent = (int)((BitConverter.DoubleToInt64Bits(value) >> 52) & 0x7FF) - 1022;
        if (exponent < -94) return 0m;   // under half of 10^-28, the least a decimal holds
        if (exponent > 96) throw new OverflowException("Value was either too large or too small for a Decimal.");

        // Scaled by the power of ten that makes it a whole number of the given digits - log10(2) taken as
        // 19728 / 65536 - and rounded to that whole number, half to even.
        bool negative = value < 0;
        double scaled = Math.Abs(value);
        int power = digits - 1 - ((exponent * 19728) >> 16);
        if (power >= 0)
        {
            power = Math.Min(power, MaxScale);
            scaled *= PowersOfTen[power];
        }
        else if (power != -1 || scaled >= PowersOfTen[digits])
            scaled /= PowersOfTen[-power];
        else
            power = 0;
        if (scaled < PowersOfTen[digits - 1] && power < MaxScale)
        {
            scaled *= 10;
            power++;
        }
        ulong whole = (ulong)Math.Round(scaled, MidpointRounding.ToEven);
        if (whole == 0) return 0m;

        // The whole number with the point put back: places after it, or zeros after it for a number of more digits.
        if (power >= 0)
            return new decimal(unchecked((int)(uint)whole), unchecked((int)(uint)(whole >> 32)), 0, negative, (byte)power);
        decimal result = whole * DecimalPowersOfTen[-power];
        return negative ? -result : result;
    }

    private const int MaxScale = 28;

    // 10^0 to 10^80 as doubles, each as a literal is: the nearest double.
    private static readonly double[] PowersOfTen =
    {
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19,
        1e20, 1e21, 1e22, 1e23, 1e24, 1e25, 1e26, 1e27, 1e28, 1e29, 1e30, 1e31, 1e32, 1e33, 1e34, 1e35, 1e36, 1e37,
        1e38, 1e39, 1e40, 1e41, 1e42, 1e43, 1e44, 1e45, 1e46, 1e47, 1e48, 1e49, 1e50, 1e51, 1e52, 1e53, 1e54, 1e55,
        1e56, 1e57, 1e58, 1e59, 1e60, 1e61, 1e62, 1e63, 1e64, 1e65, 1e66, 1e67, 1e68, 1e69, 1e70, 1e71, 1e72, 1e73,
        1e74, 1e75, 1e76, 1e77, 1e78, 1e79, 1e80,
    };

    // 10^0 to 10^28 as decimals, for a whole number with zeros after it.
    private static readonly decimal[] DecimalPowersOfTen =
        Enumerable.Range(0, MaxScale + 1).Select(n => Enumerable.Repeat(10m, n).Aggregate(1m, (product, ten) => product * ten)).ToArray();
}
