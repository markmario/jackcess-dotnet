using System.Globalization;
using System.Numerics;

namespace JackcessDotNet;

/// <summary>
/// A number exactly as it is: <see cref="Unscaled"/> units of ten to the power -<see cref="Scale"/>, without
/// trailing zeros. Every finite double and float is one. An import converts numbers through these, so that what it
/// writes and what it refuses are the same on every target: .NET Framework formats some doubles otherwise than
/// .NET does, and its round-trip format does not always give text that reads back as the double.
/// </summary>
internal readonly struct ExactNumber : IEquatable<ExactNumber>
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal ExactNumber(BigInteger unscaled, int scale, bool negativeZero = false)
    {
        if (unscaled.IsZero)
        {
            Unscaled = BigInteger.Zero;
            Scale = 0;
            NegativeZero = negativeZero;
            return;
        }
        while ((unscaled % 10).IsZero)
        {
            unscaled /= 10;
            scale--;
        }
        Unscaled = unscaled;
        Scale = scale;
        NegativeZero = false;
    }

    /// <summary>The number's digits, with its sign: no trailing zero, unless the number is 0.</summary>
    internal BigInteger Unscaled { get; }

    /// <summary>The places after the point the digits stand at; negative for a whole number that ends in zeros.</summary>
    internal int Scale { get; }

    /// <summary>Whether the number is -0: a zero that keeps its sign, as a double does. It equals 0.</summary>
    internal bool NegativeZero { get; }

    internal bool IsZero => Unscaled.IsZero;

    /// <summary>Whether the number is below zero, or is -0.</summary>
    internal bool IsNegative => Unscaled.Sign < 0 || NegativeZero;

    /// <summary>How many digits the number has: 1 for 0.</summary>
    internal int Digits => IsZero ? 1 : BigInteger.Abs(Unscaled).ToString(Invariant).Length;

    /// <summary>The power of ten of the first digit: 2 for 123, -1 for 0.5, and 0 for 0.</summary>
    internal int Exponent => IsZero ? 0 : Digits - 1 - Scale;

    public bool Equals(ExactNumber other) => Unscaled == other.Unscaled && Scale == other.Scale;
    public override bool Equals(object? obj) => obj is ExactNumber other && Equals(other);
    public override int GetHashCode() => Unscaled.GetHashCode() ^ Scale;
    public static bool operator ==(ExactNumber a, ExactNumber b) => a.Equals(b);
    public static bool operator !=(ExactNumber a, ExactNumber b) => !a.Equals(b);

    // ── Doubles and floats ───────────────────────────────────────────────────

    /// <summary>A double or float as its significand and binary exponent: |value| = Significand × 2^Exponent.</summary>
    private readonly struct Binary
    {
        internal Binary(BigInteger significand, int exponent, bool negative, bool closerBelow)
        {
            Significand = significand;
            Exponent = exponent;
            Negative = negative;
            CloserBelow = closerBelow;
        }

        internal BigInteger Significand { get; }
        internal int Exponent { get; }
        internal bool Negative { get; }

        /// <summary>
        /// Whether the next number down is half as far as the next one up: the value is the first of its binade, past
        /// the first binade.
        /// </summary>
        internal bool CloserBelow { get; }
    }

    private static Binary BinaryOf(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int field = (int)((bits >> 52) & 0x7FF);
        if (field == 0x7FF) throw new ArgumentOutOfRangeException(nameof(value), "NaN and the infinities are not numbers");
        long fraction = bits & 0xF_FFFF_FFFF_FFFFL;
        return field == 0
            ? new Binary(fraction, -1074, bits < 0, closerBelow: false)
            : new Binary(fraction | (1L << 52), field - 1075, bits < 0, closerBelow: fraction == 0 && field > 1);
    }

    private static Binary BinaryOf(float value)
    {
        int bits = SingleBits(value);
        int field = (bits >> 23) & 0xFF;
        if (field == 0xFF) throw new ArgumentOutOfRangeException(nameof(value), "NaN and the infinities are not numbers");
        int fraction = bits & 0x7F_FFFF;
        return field == 0
            ? new Binary(fraction, -149, bits < 0, closerBelow: false)
            : new Binary(fraction | (1 << 23), field - 150, bits < 0, closerBelow: fraction == 0 && field > 1);
    }

    private static int SingleBits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

    /// <summary>The number <paramref name="value"/> is, exactly. It must not be NaN or an infinity.</summary>
    internal static ExactNumber Of(double value) => Of(BinaryOf(value));

    /// <summary>The number <paramref name="value"/> is, exactly. It must not be NaN or an infinity.</summary>
    internal static ExactNumber Of(float value) => Of(BinaryOf(value));

    private static ExactNumber Of(Binary binary)
    {
        if (binary.Significand.IsZero) return new ExactNumber(BigInteger.Zero, 0, negativeZero: binary.Negative);
        BigInteger significand = binary.Negative ? -binary.Significand : binary.Significand;
        return binary.Exponent >= 0
            ? new ExactNumber(significand << binary.Exponent, 0)
            : new ExactNumber(significand * BigInteger.Pow(5, -binary.Exponent), -binary.Exponent);
    }

    /// <summary>
    /// The number of fewest digits that reads back as <paramref name="value"/> - its round-trip form - and of those
    /// the nearest it. It must not be NaN or an infinity.
    /// </summary>
    internal static ExactNumber Shortest(double value) => Shortest(BinaryOf(value), maxDigits: 17);

    /// <summary>The number of fewest digits that reads back as <paramref name="value"/>, as for a double.</summary>
    internal static ExactNumber Shortest(float value) => Shortest(BinaryOf(value), maxDigits: 9);

    private static ExactNumber Shortest(Binary binary, int maxDigits)
    {
        if (binary.Significand.IsZero) return new ExactNumber(BigInteger.Zero, 0, negativeZero: binary.Negative);

        // In quarters of the last place: the value is 4m, and the numbers that read back as it lie between low and
        // high - on them too when m is even, as reading rounds a tie to the even significand.
        BigInteger value = binary.Significand << 2;
        BigInteger low = value - (binary.CloserBelow ? 1 : 2), high = value + 2;
        int quarter = binary.Exponent - 2;
        bool onTheEdges = binary.Significand.IsEven;
        int first = Of(new Binary(binary.Significand, binary.Exponent, false, false)).Exponent;

        for (int digits = 1; digits <= maxDigits; digits++)
        {
            int unit = first - digits + 1;   // a number of this many digits is a whole number of 10^unit
            BigInteger numerator = value, denominator = BigInteger.One;
            if (quarter >= 0) numerator <<= quarter; else denominator <<= -quarter;
            if (unit <= 0) numerator *= BigInteger.Pow(10, -unit); else denominator *= BigInteger.Pow(10, unit);
            BigInteger below = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);

            if (remainder.IsZero) return Signed(below, unit, binary.Negative);
            BigInteger above = below + 1;
            int nearer = (remainder << 1).CompareTo(denominator);
            bool belowFirst = nearer < 0 || (nearer == 0 && below.IsEven);
            foreach (BigInteger candidate in belowFirst ? new[] { below, above } : new[] { above, below })
                if (Within(candidate, unit, low, high, quarter, onTheEdges)) return Signed(candidate, unit, binary.Negative);
        }
        // Seventeen digits always read back as the double, and nine as the float.
        throw new InvalidOperationException("No round-trip form was found.");
    }

    private static ExactNumber Signed(BigInteger digits, int unit, bool negative)
        => new(negative ? -digits : digits, -unit);

    // Whether digits × 10^unit lies between low and high quarters, or on them when onTheEdges.
    private static bool Within(BigInteger digits, int unit, BigInteger low, BigInteger high, int quarter, bool onTheEdges)
    {
        int fromLow = Compare(digits, unit, low, quarter);
        int fromHigh = Compare(digits, unit, high, quarter);
        return onTheEdges ? fromLow >= 0 && fromHigh <= 0 : fromLow > 0 && fromHigh < 0;
    }

    // digits × 10^unit compared with quarters × 2^quarter, exactly.
    private static int Compare(BigInteger digits, int unit, BigInteger quarters, int quarter)
    {
        BigInteger left = digits, right = quarters;
        if (unit >= 0) left *= BigInteger.Pow(10, unit); else right *= BigInteger.Pow(10, -unit);
        if (quarter >= 0) right <<= quarter; else left <<= -quarter;
        return left.CompareTo(right);
    }

    /// <summary>
    /// The text of <paramref name="value"/> that reads back as it, the same on every target: its round-trip form,
    /// written as .NET writes it - "0.1", "1E-05", "1E+17", "-0", "NaN", "-Infinity".
    /// </summary>
    internal static string Text(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsInfinity(value)) return value > 0 ? "Infinity" : "-Infinity";
        return Shortest(value).ToText(scientificFrom: 17);
    }

    /// <summary>The text of <paramref name="value"/> that reads back as it, as for a double.</summary>
    internal static string Text(float value)
    {
        if (float.IsNaN(value)) return "NaN";
        if (float.IsInfinity(value)) return value > 0 ? "Infinity" : "-Infinity";
        return Shortest(value).ToText(scientificFrom: 9);
    }

    // As .NET's round-trip format writes a number: in full, unless its first digit is at ten to the power
    // scientificFrom or more, or before the fourth place after the point, when it is written with an exponent.
    private string ToText(int scientificFrom)
    {
        if (IsZero) return NegativeZero ? "-0" : "0";
        string digits = BigInteger.Abs(Unscaled).ToString(Invariant);
        int exponent = digits.Length - 1 - Scale;
        string sign = Unscaled.Sign < 0 ? "-" : "";
        if (exponent >= scientificFrom || exponent < -4)
            return sign + digits[0] + (digits.Length > 1 ? "." + digits.Substring(1) : "")
                 + "E" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", Invariant);
        if (exponent < 0) return sign + "0." + new string('0', -exponent - 1) + digits;
        if (digits.Length <= exponent + 1) return sign + digits + new string('0', exponent + 1 - digits.Length);
        return sign + digits.Substring(0, exponent + 1) + "." + digits.Substring(exponent + 1);
    }

    /// <summary>
    /// The double nearest the number - of two as near, the one with the even significand, as reading text gives - or
    /// null when that is past the largest double. A number nearer 0 than the least double is 0, with its sign.
    /// </summary>
    internal double? NearestDouble()
    {
        if (NearestBinary(significandBits: 52, leastExponent: -1074, greatestExponent: 971, pastLargest: 310, nearerZero: -326) is not { } nearest)
            return null;
        BigInteger first = BigInteger.One << 52;
        long bits = nearest.Significand < first
            ? (long)nearest.Significand
            : ((long)(nearest.Exponent + 1075) << 52) | (long)(nearest.Significand - first);
        double value = BitConverter.Int64BitsToDouble(bits);
        return IsNegative ? -value : value;
    }

    /// <summary>The float nearest the number, as for a double; null when that is past the largest float.</summary>
    internal float? NearestSingle()
    {
        if (NearestBinary(significandBits: 23, leastExponent: -149, greatestExponent: 104, pastLargest: 40, nearerZero: -47) is not { } nearest)
            return null;
        BigInteger first = BigInteger.One << 23;
        int bits = nearest.Significand < first
            ? (int)nearest.Significand
            : ((nearest.Exponent + 150) << 23) | (int)(nearest.Significand - first);
        float value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        return IsNegative ? -value : value;
    }

    // The significand and exponent of the binary number nearest |this|, a tie going to the even significand: a
    // significand of significandBits bits after its leading one, or fewer at the least exponent; null past the
    // greatest. A number whose first digit is past ten to the power pastLargest is past it, and one whose first digit
    // is before ten to the power nearerZero is nearer 0, without their digits being worked out.
    private (BigInteger Significand, int Exponent)? NearestBinary(int significandBits, int leastExponent, int greatestExponent, int pastLargest, int nearerZero)
    {
        if (IsZero || Exponent < nearerZero) return (BigInteger.Zero, leastExponent);
        if (Exponent > pastLargest) return null;

        BigInteger numerator = BigInteger.Abs(Unscaled), denominator = BigInteger.One;
        if (Scale >= 0) denominator = BigInteger.Pow(10, Scale); else numerator *= BigInteger.Pow(10, -Scale);

        BigInteger least = BigInteger.One << significandBits, limit = BigInteger.One << (significandBits + 1);
        int exponent = Math.Max(leastExponent, BitLength(numerator) - BitLength(denominator) - (significandBits + 1));
        BigInteger quotient, remainder, divisor;
        while (true)
        {
            // numerator / denominator = (quotient + remainder / divisor) × 2^exponent
            if (exponent >= 0)
            {
                divisor = denominator << exponent;
                quotient = BigInteger.DivRem(numerator, divisor, out remainder);
            }
            else
            {
                divisor = denominator;
                quotient = BigInteger.DivRem(numerator << -exponent, divisor, out remainder);
            }
            if (quotient >= limit) exponent++;
            else if (quotient < least && exponent > leastExponent) exponent--;
            else break;
        }

        int half = (remainder << 1).CompareTo(divisor);
        if (half > 0 || (half == 0 && !quotient.IsEven)) quotient += 1;
        if (quotient == limit)
        {
            quotient >>= 1;
            exponent++;
        }
        return exponent > greatestExponent ? null : (quotient, exponent);
    }

    private static int BitLength(BigInteger value)
    {
        byte[] bytes = value.ToByteArray();
        int top = bytes.Length - 1;
        while (top > 0 && bytes[top] == 0) top--;
        int bits = top * 8;
        for (int b = bytes[top]; b != 0; b >>= 1) bits++;
        return bits;
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    // How far from 1 a number read from text is kept, in powers of ten: past every column's range, and nearer 0 than
    // any column holds a number apart from 0, by far. A number further out stands for every number as far: one, ten to
    // the power Beyond + 1 or -(Beyond + 1), with its sign. Every column refuses it, as it refuses the number written.
    private const long Beyond = 100_000;

    /// <summary>
    /// The number <paramref name="text"/> denotes, exactly, when it is a number as <see cref="NumberStyles.Float"/>
    /// reads one in the invariant culture: white space around it, a sign, digits with a point among them or none, and
    /// an exponent. "NaN" and "Infinity" are not numbers. A number more than ten to the power 100,000 from 1, either
    /// way, is read as one that far: a column holds neither.
    /// </summary>
    internal static bool TryParse(string text, out ExactNumber number)
    {
        number = default;
        int at = 0, end = text.Length;
        while (at < end && IsWhite(text[at])) at++;
        while (end > at && IsWhite(text[end - 1])) end--;

        bool negative = false;
        if (at < end && (text[at] == '+' || text[at] == '-'))
        {
            negative = text[at] == '-';
            at++;
        }

        var digits = new System.Text.StringBuilder();
        int places = 0;
        bool point = false, anyDigit = false;
        for (; at < end; at++)
        {
            char c = text[at];
            if (c >= '0' && c <= '9')
            {
                anyDigit = true;
                if (digits.Length > 0 || c != '0') digits.Append(c);
                if (point) places++;
            }
            else if (c == '.' && !point) point = true;
            else break;
        }
        if (!anyDigit) return false;

        long exponent = 0;
        if (at < end && (text[at] == 'e' || text[at] == 'E'))
        {
            at++;
            bool negativeExponent = false;
            if (at < end && (text[at] == '+' || text[at] == '-'))
            {
                negativeExponent = text[at] == '-';
                at++;
            }
            // Past 10^11 the exponent stops growing: more than any string has digits, so the number is past Beyond
            // either way, whatever its digits.
            int first = at;
            for (; at < end && text[at] >= '0' && text[at] <= '9'; at++)
                if (exponent < 100_000_000_000) exponent = exponent * 10 + (text[at] - '0');
            if (at == first) return false;
            if (negativeExponent) exponent = -exponent;
        }
        if (at != end) return false;

        number = FromDigits(digits, places, exponent, negative);
        return true;
    }

    /// <summary>
    /// The number <paramref name="text"/> denotes, exactly, read as .NET reads a number with
    /// <paramref name="styles"/> in the format of <paramref name="info"/>: white space, a sign or parentheses and a
    /// currency symbol before or after it, digits with group separators and a decimal separator among them, and an
    /// exponent, each where the style allows it. Only the digits, the separators and the sign are read here: whether
    /// the text is a number at all is the runtime's to say, and is asked first
    /// (<see cref="double.Parse(string, NumberStyles, IFormatProvider)"/>). False for text without digits, which
    /// the runtime reads as NaN or an infinity by their symbols, and for text not read to its end. A number more than
    /// ten to the power 100,000 from 1 is read as one that far, as <see cref="TryParse(string, out ExactNumber)"/>
    /// reads it. With <paramref name="keep"/>, the number is the text's first that many significant digits, cut
    /// toward zero, as <see cref="Cut"/> cuts it: the digits after them are only counted, so that text of any length
    /// is read in time in proportion to it.
    /// </summary>
    internal static bool TryRead(string text, NumberStyles styles, NumberFormatInfo info, out ExactNumber number, int keep = int.MaxValue)
    {
        number = default;
        int at = 0;
        bool negative = false, signed = false, parenthesis = false;

        // As the runtime reads it (Number.ParseNumber): when a currency symbol is allowed, the currency's separators
        // are looked for first, and the number's too until a currency symbol has been read.
        bool parsingCurrency = (styles & NumberStyles.AllowCurrencySymbol) != 0;
        string? currencySymbol = parsingCurrency ? info.CurrencySymbol : null;
        string decimalSeparator = parsingCurrency ? info.CurrencyDecimalSeparator : info.NumberDecimalSeparator;
        string groupSeparator = parsingCurrency ? info.CurrencyGroupSeparator : info.NumberGroupSeparator;

        // Before the digits: white space, a sign or an opening parenthesis, and a currency symbol, in any order.
        while (at < text.Length)
        {
            int matched;
            if ((styles & NumberStyles.AllowLeadingWhite) != 0 && IsWhite(text[at])) at++;
            else if (!signed && (styles & NumberStyles.AllowLeadingSign) != 0 && (matched = Match(text, at, info.PositiveSign)) > 0)
            {
                signed = true;
                at += matched;
            }
            else if (!signed && (styles & NumberStyles.AllowLeadingSign) != 0 && (matched = MatchNegative(text, at, info)) > 0)
            {
                signed = negative = true;
                at += matched;
            }
            else if (!signed && (styles & NumberStyles.AllowParentheses) != 0 && text[at] == '(')
            {
                signed = negative = parenthesis = true;
                at++;
            }
            else if (currencySymbol is not null && (matched = Match(text, at, currencySymbol)) > 0)
            {
                currencySymbol = null;
                at += matched;
            }
            else break;
        }

        // The digits, with a decimal separator among them and group separators before it. Past 'keep' significant
        // digits a digit is only counted, before the point, for the power of ten of those kept.
        var digits = new System.Text.StringBuilder();
        int places = 0;
        long dropped = 0;
        bool point = false, anyDigit = false;
        while (at < text.Length)
        {
            char c = text[at];
            int matched;
            if (c >= '0' && c <= '9')
            {
                anyDigit = true;
                if (digits.Length >= keep)
                {
                    if (!point) dropped++;
                }
                else
                {
                    if (digits.Length > 0 || c != '0') digits.Append(c);
                    if (point) places++;
                }
                at++;
            }
            else if (!point && (styles & NumberStyles.AllowDecimalPoint) != 0
                     && ((matched = Match(text, at, decimalSeparator)) > 0
                         || (parsingCurrency && currencySymbol is not null && (matched = Match(text, at, info.NumberDecimalSeparator)) > 0)))
            {
                point = true;
                at += matched;
            }
            else if (!point && anyDigit && (styles & NumberStyles.AllowThousands) != 0
                     && ((matched = Match(text, at, groupSeparator)) > 0
                         || (parsingCurrency && currencySymbol is not null && (matched = Match(text, at, info.NumberGroupSeparator)) > 0)))
                at += matched;
            else break;
        }
        if (!anyDigit) return false;

        // An exponent: e or E, a sign, digits. An e that no digits follow is not one.
        long exponent = 0;
        if ((styles & NumberStyles.AllowExponent) != 0 && at < text.Length && (text[at] == 'e' || text[at] == 'E'))
        {
            int after = at + 1, matched;
            bool negativeExponent = false;
            if ((matched = Match(text, after, info.PositiveSign)) > 0) after += matched;
            else if ((matched = MatchNegative(text, after, info)) > 0)
            {
                negativeExponent = true;
                after += matched;
            }
            if (after < text.Length && text[after] >= '0' && text[after] <= '9')
            {
                at = after;
                for (; at < text.Length && text[at] >= '0' && text[at] <= '9'; at++)
                    if (exponent < 100_000_000_000) exponent = exponent * 10 + (text[at] - '0');
                if (negativeExponent) exponent = -exponent;
            }
        }

        // After the digits: white space, a sign or the closing parenthesis, and a currency symbol, and then nothing
        // but the NULs the runtime allows at the end. Text not read to its end is text this reading does not
        // follow: false, rather than a number read from part of it.
        while (at < text.Length)
        {
            int matched;
            if ((styles & NumberStyles.AllowTrailingWhite) != 0 && IsWhite(text[at])) at++;
            else if (!signed && (styles & NumberStyles.AllowTrailingSign) != 0 && (matched = Match(text, at, info.PositiveSign)) > 0)
            {
                signed = true;
                at += matched;
            }
            else if (!signed && (styles & NumberStyles.AllowTrailingSign) != 0 && (matched = MatchNegative(text, at, info)) > 0)
            {
                signed = negative = true;
                at += matched;
            }
            else if (parenthesis && text[at] == ')')
            {
                parenthesis = false;
                at++;
            }
            else if (currencySymbol is not null && (matched = Match(text, at, currencySymbol)) > 0)
            {
                currencySymbol = null;
                at += matched;
            }
            else break;
        }
        while (at < text.Length && text[at] == '\0') at++;
        if (at < text.Length) return false;

        number = FromDigits(digits, places, exponent + dropped, negative);
        return true;
    }

    // How many characters of text at 'at' are 'value', as the runtime matches a culture's sign or separator: a
    // no-break space or a narrow one in it is matched by a space too (.NET Framework takes the first only, and
    // refuses the text before it is read here). 0 when they are not there, or 'value' is empty.
    private static int Match(string text, int at, string value)
    {
        if (value.Length == 0 || at + value.Length > text.Length) return 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = text[at + i], v = value[i];
            if (c != v && !((v == ' ' || v == ' ') && c == ' ')) return 0;
        }
        return value.Length;
    }

    // The culture's negative sign at 'at'; a hyphen-minus too where the sign is another dash, which .NET (not .NET
    // Framework) reads as it.
    private static int MatchNegative(string text, int at, NumberFormatInfo info)
    {
        int matched = Match(text, at, info.NegativeSign);
        if (matched > 0) return matched;
        bool hyphenToo = info.NegativeSign is "‒" or "⁻" or "₋" or "−" or "➖" or "﹣" or "－";
        return hyphenToo && at < text.Length && text[at] == '-' ? 1 : 0;
    }

    // The number of the digits read - leading zeros left out - with 'places' of them after the point, times ten to
    // the power 'exponent'.
    private static ExactNumber FromDigits(System.Text.StringBuilder digits, int places, long exponent, bool negative)
    {
        int trailing = 0;
        while (trailing < digits.Length && digits[digits.Length - 1 - trailing] == '0') trailing++;
        if (trailing == digits.Length) return new ExactNumber(BigInteger.Zero, 0, negativeZero: negative);
        int count = digits.Length - trailing;
        long scale = checked(places - trailing - exponent);
        long magnitude = checked(count - 1 - scale);   // the power of ten of the first digit
        BigInteger unscaled;
        if (magnitude > Beyond)
        {
            unscaled = BigInteger.One;
            scale = -(Beyond + 1);
        }
        else if (magnitude < -Beyond)
        {
            unscaled = BigInteger.One;
            scale = Beyond + 1;
        }
        else
            unscaled = BigInteger.Parse(digits.ToString(0, count), NumberStyles.None, Invariant);
        return new ExactNumber(negative ? -unscaled : unscaled, checked((int)scale));
    }

    /// <summary>
    /// The number cut to its first <paramref name="digits"/> significant digits, toward zero; the number itself
    /// when it has no more than that.
    /// </summary>
    internal ExactNumber Cut(int digits)
    {
        int length = Digits;
        if (IsZero || length <= digits) return this;
        BigInteger cut = BigInteger.Abs(Unscaled) / BigInteger.Pow(10, length - digits);
        return new ExactNumber(Unscaled.Sign < 0 ? -cut : cut, Scale - (length - digits));
    }

    /// <summary>
    /// The number rounded to <paramref name="digits"/> significant digits, a half away from zero; the number itself
    /// when it has no more than that.
    /// </summary>
    internal ExactNumber RoundedAway(int digits)
    {
        int length = Digits;
        if (IsZero || length <= digits) return this;
        BigInteger divisor = BigInteger.Pow(10, length - digits);
        BigInteger rounded = BigInteger.DivRem(BigInteger.Abs(Unscaled), divisor, out BigInteger remainder);
        if ((remainder << 1) >= divisor) rounded += 1;
        return new ExactNumber(Unscaled.Sign < 0 ? -rounded : rounded, Scale - (length - digits));
    }

    // The white space NumberStyles.AllowLeadingWhite and AllowTrailingWhite take.
    private static bool IsWhite(char c) => c == ' ' || (c >= '\t' && c <= '\r');

    // ── Decimals ─────────────────────────────────────────────────────────────

    private static readonly BigInteger DecimalLimit = BigInteger.One << 96;

    /// <summary>The number <paramref name="value"/> is, exactly.</summary>
    internal static ExactNumber Of(long value) => new(value, 0);

    /// <summary>The number <paramref name="value"/> is, exactly.</summary>
    internal static ExactNumber Of(ulong value) => new(value, 0);

    /// <summary>The number <paramref name="value"/> is, exactly.</summary>
    internal static ExactNumber Of(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger magnitude = new BigInteger((uint)bits[2]) << 64 | new BigInteger((uint)bits[1]) << 32 | new BigInteger((uint)bits[0]);
        bool negative = bits[3] < 0;
        return new ExactNumber(negative ? -magnitude : magnitude, (bits[3] >> 16) & 0xFF, negativeZero: negative && magnitude.IsZero);
    }

    /// <summary>The number as a <see cref="decimal"/>, when one holds it exactly; otherwise null.</summary>
    internal decimal? ToDecimal()
    {
        if (IsZero) return 0m;
        if (Scale > 28 || Exponent > 28) return null;
        BigInteger magnitude = BigInteger.Abs(Unscaled);
        int scale = Scale;
        if (scale < 0)
        {
            magnitude *= BigInteger.Pow(10, -scale);
            scale = 0;
        }
        if (magnitude >= DecimalLimit) return null;
        int Word(int shift) => unchecked((int)(uint)((magnitude >> shift) & uint.MaxValue));
        return new decimal(Word(0), Word(32), Word(64), Unscaled.Sign < 0, (byte)scale);
    }
}
