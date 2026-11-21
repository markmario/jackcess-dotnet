using System.Globalization;

namespace JackcessDotNet.Util;

/// <summary>
/// Text taken to a Double or a Single, and a Double or a Single taken to text, as Access takes them (#124): the same
/// on every target, whatever the runtime's own parsing and formatting do.
/// <list type="bullet">
/// <item>Text is read as the runtime reads it - its forms and separators - and then to the number Access stores
/// for it: the nearest double to the number its first 20 significant digits make, and the nearest single to the
/// number its first 12 make, a tie to the even significand, the digits after those left out. ACE 16 stores that
/// for text in an <c>INSERT</c>, as a literal and as a parameter, and DAO for a field's value and a property's
/// (measured: <c>tools/ace-text-to-float.ps1</c>). .NET Framework's own parse is not that in either kind of
/// process, nor .NET's, which rounds every digit.</item>
/// <item>A number is written as Access writes one in a Text column: rounded to 15 significant digits for a
/// double and 7 for a single, a half away from zero; written out unless it is 10^15 or more (10^7 for a single),
/// or has more than 15 places after the point (7), when it is written with an exponent of two digits or more:
/// 0.00001, 1E+15, 1.23456789012345E-05. .NET Framework writes 1E-05, and .NET the round-trip form,
/// 0.30000000000000004 where Access writes 0.3.</item>
/// </list>
/// </summary>
internal static class NumberText
{
    /// <summary>How many significant digits of text Access reads for a Double.</summary>
    internal const int DoubleDigitsRead = 20;

    /// <summary>How many significant digits of text Access reads for a Single.</summary>
    internal const int SingleDigitsRead = 12;

    /// <summary>
    /// The double Access stores for <paramref name="text"/>, which is read with <paramref name="styles"/> in the
    /// format of <paramref name="provider"/> (null: the current culture's). Text the runtime reads as NaN or an
    /// infinity by their symbols is that.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="text"/> is not a number.</exception>
    /// <exception cref="OverflowException"><paramref name="text"/> is a number past what a double holds.</exception>
    internal static double ToDouble(string text, NumberStyles styles, IFormatProvider? provider)
    {
        if (Read(text, styles, provider, DoubleDigitsRead, out double symbol) is not { } number) return symbol;
        return number.Cut(DoubleDigitsRead).NearestDouble()
               ?? throw new OverflowException($"'{text}' is a number past what a Double holds.");
    }

    /// <summary>The single Access stores for <paramref name="text"/>, read as for <see cref="ToDouble"/>.</summary>
    /// <exception cref="FormatException"><paramref name="text"/> is not a number.</exception>
    /// <exception cref="OverflowException"><paramref name="text"/> is a number past what a single holds.</exception>
    internal static float ToSingle(string text, NumberStyles styles, IFormatProvider? provider)
    {
        if (Read(text, styles, provider, SingleDigitsRead, out double symbol) is not { } number) return (float)symbol;
        return number.Cut(SingleDigitsRead).NearestSingle()
               ?? throw new OverflowException($"'{text}' is a number past what a Single holds.");
    }

    // The number of the first 'digits' significant digits of text, cut toward zero - read in time in proportion to the
    // text, however long; or null, with the runtime's own number, for text it reads as NaN or an
    // infinity by their symbols - and for any text the digits are not read from to its end, a form of a culture
    // ExactNumber.TryRead does not follow, which is taken as the runtime takes it rather than read in part. Whether
    // it is a number at all is the runtime's to say: .NET Framework refuses one past a double's range, which is a
    // number all the same.
    private static ExactNumber? Read(string text, NumberStyles styles, IFormatProvider? provider, int digits, out double symbol)
    {
        var info = NumberFormatInfo.GetInstance(provider);
        bool past = false;
        try { symbol = double.Parse(text, styles, info); }
        catch (OverflowException)
        {
            symbol = double.NaN;
            past = true;
        }
        if (ExactNumber.TryRead(text, styles, info, out ExactNumber number, keep: digits)) return number;
        if (past) throw new OverflowException($"'{text}' is a number past what a Double holds.");
        return null;
    }

    /// <summary>
    /// <paramref name="value"/> as Access writes it in a Text column, in the format of
    /// <paramref name="provider"/> (null: the current culture's): its decimal separator and negative sign.
    /// </summary>
    internal static string Text(double value, IFormatProvider? provider)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        if (double.IsNaN(value)) return bits < 0 ? "-nan(ind)" : "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        return Text(ExactNumber.Of(value), 15, NumberFormatInfo.GetInstance(provider));
    }

    /// <summary><paramref name="value"/> as Access writes it in a Text column, as for a double.</summary>
    internal static string Text(float value, IFormatProvider? provider)
    {
        if (float.IsNaN(value)) return BitConverter.ToInt32(BitConverter.GetBytes(value), 0) < 0 ? "-nan(ind)" : "nan";
        if (float.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        return Text(ExactNumber.Of(value), 7, NumberFormatInfo.GetInstance(provider));
    }

    private static string Text(ExactNumber number, int precision, NumberFormatInfo info)
    {
        string sign = number.IsNegative ? info.NegativeSign : "";
        if (number.IsZero) return sign + "0";

        ExactNumber rounded = number.RoundedAway(precision);
        string digits = System.Numerics.BigInteger.Abs(rounded.Unscaled).ToString(CultureInfo.InvariantCulture);
        int exponent = rounded.Exponent;
        string point = info.NumberDecimalSeparator;

        // A number of 10^precision or more before it is rounded is written with an exponent, and so is one that
        // has more than 'precision' places after the point once it is rounded.
        if (number.Exponent >= precision || (exponent < 0 && -exponent - 1 + digits.Length > precision))
            return sign + digits.Substring(0, 1) + (digits.Length > 1 ? point + digits.Substring(1) : "")
                 + "E" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        if (exponent < 0) return sign + "0" + point + new string('0', -exponent - 1) + digits;
        if (digits.Length <= exponent + 1) return sign + digits + new string('0', exponent + 1 - digits.Length);
        return sign + digits.Substring(0, exponent + 1) + point + digits.Substring(exponent + 1);
    }
}
