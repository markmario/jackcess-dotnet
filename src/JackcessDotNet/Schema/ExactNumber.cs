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

    // ── Decimals ─────────────────────────────────────────────────────────────

    private static readonly BigInteger DecimalLimit = BigInteger.One << 96;

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
