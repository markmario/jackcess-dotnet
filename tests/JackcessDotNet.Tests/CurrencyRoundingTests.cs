using System.Globalization;
using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The Currency a caller's number becomes (#65): its exact value taken to ten-thousandths, half to even, as Access
/// takes one. <c>Assets/ace-currency-writes.tsv</c> is what ACE 16 stored for 7,198 numbers an <c>INSERT</c> gave it
/// as parameters of their own types (<c>tools/ace-currency.ps1</c>, in an .accdb; an .mdb stores the same). Measured
/// for the pull request as well, and the same: ACE's <c>UPDATE</c>, DAO's <c>AddNew</c> and <c>Edit</c> on a dynaset
/// and on a table, and the value ACE's <c>=</c>, <c>BETWEEN</c>, <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c> and
/// <c>&lt;</c> through an index take a number to.
/// </summary>
public sealed class CurrencyRoundingTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static IEnumerable<(string Kind, object Number, string Stored)> AceWrites()
    {
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Assets", "ace-currency-writes.tsv")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            object number = parts[0] switch
            {
                "double"  => BitConverter.Int64BitsToDouble(long.Parse(parts[1], NumberStyles.HexNumber, Invariant)),
                "single"  => BitConverter.ToSingle(BitConverter.GetBytes(int.Parse(parts[1], NumberStyles.HexNumber, Invariant)), 0),
                "decimal" => decimal.Parse(parts[1], NumberStyles.Float, Invariant),
                _         => throw new InvalidDataException("A kind the file does not have: " + parts[0]),
            };
            yield return (parts[0], number, parts[2]);
        }
    }

    private static string Shown(object number) => number switch
    {
        double d => d.ToString("R", Invariant) + " (double)",
        float f  => f.ToString("R", Invariant) + " (float)",
        _        => Convert.ToString(number, Invariant) + " (" + number.GetType().Name + ")",
    };

    [Fact]
    public void Every_number_is_the_Currency_ACE_stores_for_it()
    {
        int compared = 0, refused = 0;
        var kinds = new HashSet<string>();
        foreach (var (kind, number, stored) in AceWrites())
        {
            kinds.Add(kind);
            bool held = CurrencyRounding.TryUnits(number, out long units);
            if (number is double.NaN or float.NaN)
            {
                // ACE stores NaN as the least Currency, the 64-bit integer a processor converts NaN to; here NaN is no
                // Currency, and a row is not written with it.
                Assert.Equal(long.MinValue.ToString(Invariant), stored);
                Assert.False(held, $"NaN ({kind}) is taken to {units}");
                continue;
            }
            compared++;
            if (stored == "refused")
            {
                refused++;
                Assert.False(held, $"{Shown(number)}: ACE refused it, and this library takes it to {units}");
            }
            else
                Assert.True(held && units == long.Parse(stored, Invariant),
                    $"{Shown(number)}: ACE stored {stored}, and this library " + (held ? $"takes it to {units}" : "refuses it"));
        }
        Assert.Equal(new[] { "decimal", "double", "single" }, kinds.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(7196, compared);
        Assert.Equal(18, refused);
    }

    // ── Each kind of number, with what ACE stored for it ───────────────────

    public static IEnumerable<object[]> Doubles() => new (double Number, long? Units)[]
    {
        // Exact halves, to the even neighbour, in both signs; the doubles either side of them.
        (0.03125, 312), (-0.03125, -312), (0.09375, 938), (-0.09375, -938), (1.03125, 10312), (0.15625, 1562),
        (NextDown(0.03125), 312), (NextUp(0.03125), 313), (NextDown(0.09375), 937), (NextUp(-1.03125), -10312),
        // Written as halves, and not: 0.00015 is just under its half and 0.00025 just over; through a decimal of 15
        // significant digits each was a half.
        (0.00015, 1), (0.00025, 3), (1.23465, 12347), (1.234549999999999, 12345), (1.23455, 12346),
        // More significant digits than 15, kept: through a decimal of 15 the first two were 562949953421313 and
        // 922337203685478, past the range.
        (562949953421312.5, 5629499534213125000), (922337203685477.5, 9223372036854775000),
        (-922337203685477.5, -9223372036854775000), (35184372088832.0078125, 351843720888320078),
        // Past the range, and no number.
        (922337203685477.625, null), (-922337203685477.625, null), (1e15, null), (1e300, null),
        (double.PositiveInfinity, null), (double.NegativeInfinity, null), (double.NaN, null),
        (-0.0, 0), (double.Epsilon, 0), (1e-300, 0),
    }.Select(c => new object[] { c.Number, c.Units! });

    [Theory]
    [MemberData(nameof(Doubles))]
    public void A_double_is_its_exact_value_taken_to_four_places_half_to_even(double number, long? units)
    {
        Assert.Equal(units is not null, CurrencyRounding.TryUnits(number, out long taken));
        if (units is { } expected) Assert.Equal(expected, taken);
        Assert.Equal(units is not null, CurrencyRounding.TryUnits((object)number, out long boxed));
        if (units is { } same) Assert.Equal(same, boxed);
    }

    [Fact]
    public void A_float_is_its_exact_value_taken_to_four_places_half_to_even()
    {
        foreach (var (number, units) in new (float, long?)[]
                 {
                     (0.03125f, 312), (-0.03125f, -312), (0.09375f, 938), (0.00015f, 2), (1.23465f, 12347),
                     (131072.015625f, 1310720156), (2048.000244140625f, 20480002),
                     (1e15f, null), (float.NaN, null), (float.PositiveInfinity, null),
                 })
        {
            Assert.Equal(units is not null, CurrencyRounding.TryUnits(number, out long taken));
            if (units is { } expected) Assert.Equal(expected, taken);
        }
    }

    [Fact]
    public void A_decimal_is_its_digits_taken_to_four_places_half_to_even()
    {
        foreach (var (number, units) in new (decimal, long?)[]
                 {
                     (0.03125m, 312), (-0.03125m, -312), (0.09375m, 938), (1.23465m, 12346), (-1.23465m, -12346),
                     (0.00015m, 2), (0.00025m, 2), (1.23455m, 12346), (1.2345m, 12345), (1.5m, 15000),
                     (0.000150000000000000000000001m, 2), (0.000149999999999999999999999m, 1),
                     (922337203685477.5807m, long.MaxValue), (922337203685477.58065m, long.MaxValue - 1),
                     (922337203685477.58074m, long.MaxValue), (-922337203685477.5808m, long.MinValue),
                     (-922337203685477.58085m, long.MinValue), (-922337203685477.58075m, long.MinValue),
                     (922337203685477.5808m, null), (922337203685477.58075m, null), (-922337203685477.5809m, null),
                     (-922337203685477.58095m, null), (decimal.MaxValue, null), (decimal.MinValue, null),
                     (-0.0000m, 0), (0.00004999m, 0), (-0.00005m, 0), (0.00005m, 0), (0.00005001m, 1),
                 })
        {
            Assert.Equal(units is not null, CurrencyRounding.TryUnits(number, out long taken));
            if (units is { } expected) Assert.True(expected == taken, $"{number}: {taken}, not {expected}");
        }
    }

    [Fact]
    public void A_whole_number_of_any_size_is_itself_when_a_Currency_holds_it()
    {
        foreach (var (number, units) in new (object, long?)[]
                 {
                     ((byte)200, 2_000_000), ((sbyte)-5, -50_000), ((short)-32768, -327_680_000), ((ushort)65535, 655_350_000),
                     (int.MinValue, int.MinValue * 10_000L), (uint.MaxValue, uint.MaxValue * 10_000L),
                     (922337203685477L, 9223372036854770000), (-922337203685477L, -9223372036854770000),
                     (922337203685478L, null), (-922337203685478L, null), (long.MaxValue, null), (long.MinValue, null),
                     (922337203685477UL, 9223372036854770000), (ulong.MaxValue, null),
                 })
        {
            Assert.Equal(units is not null, CurrencyRounding.TryUnits(number, out long taken));
            if (units is { } expected) Assert.Equal(expected, taken);
        }
    }

    [Fact]
    public void An_equality_takes_no_value_for_a_number_that_is_not_one_of_NET_s_numbers()
    {
        // As ValueMatch has it for every number column: text, a bool and the rest are no number to an equality.
        foreach (object value in new object[] { "1.5", "0", "1,5", "", true, false, 'x', DateTime.Now, Guid.Empty, new byte[] { 1 }, DayOfWeek.Monday })
        {
            Assert.False(CurrencyRounding.IsNumber(value), $"{value} ({value.GetType().Name})");
            Assert.False(CurrencyRounding.TryUnits(value, out _), $"{value} ({value.GetType().Name})");
        }
        Assert.False(CurrencyRounding.TryUnits(null, out _));
    }

    // What f gives with the current culture the one named.
    private static T InCulture<T>(string name, Func<T> f)
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(name);
        try { return f(); }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void Text_and_a_bool_are_taken_for_the_number_every_number_column_takes_them_for()
    {
        // Convert's reading, in the current culture, as a Double column takes text (Convert.ToDouble); only the
        // rounding of the number it makes is the Currency's own. Access reads text by the machine's locale and writes
        // True as -1, a question for every number type alike.
        Assert.Equal(312, InCulture("en-US", () => CurrencyRounding.Units("0.03125")));
        Assert.Equal(-12346, InCulture("en-US", () => CurrencyRounding.Units(" -1.23465 ")));
        Assert.Equal(150_000, InCulture("en-US", () => CurrencyRounding.Units("1,5")));
        Assert.Equal(15.0, InCulture("en-US", () => Convert.ToDouble("1,5")));
        Assert.Equal(15_000, InCulture("de-DE", () => CurrencyRounding.Units("1,5")));
        Assert.Equal(312, InCulture("de-DE", () => CurrencyRounding.Units("0,03125")));
        Assert.Equal(10_000, CurrencyRounding.Units(true));
        Assert.Equal(0, CurrencyRounding.Units(false));
        Assert.Equal(10_000, CurrencyRounding.Units(DayOfWeek.Monday));

        Assert.Throws<FormatException>(() => InCulture("en-US", () => CurrencyRounding.Units("abc")));
        Assert.Throws<FormatException>(() => CurrencyRounding.Units(""));
        foreach (object value in new object[] { 'x', DateTime.Now, Guid.Empty, new byte[] { 1 } })
            Assert.Throws<InvalidCastException>(() => CurrencyRounding.Units(value));
        Assert.Throws<OverflowException>(() => InCulture("en-US", () => CurrencyRounding.Units("79228162514264337593543950336")));
        Assert.Throws<OverflowException>(() => InCulture("en-US", () => CurrencyRounding.Units("922337203685477.5808")));
        Assert.Throws<OverflowException>(() => CurrencyRounding.Units(double.NaN));
        Assert.Throws<OverflowException>(() => CurrencyRounding.Units(922337203685477.5808m));
        Assert.Equal(312, CurrencyRounding.Units(0.03125));
    }

    [Fact]
    public void A_Currency_reads_as_its_ten_thousandths_over_ten_thousand()
    {
        Assert.Equal(0.0312m, CurrencyRounding.ToDecimal(312));
        Assert.Equal(-922337203685477.5808m, CurrencyRounding.ToDecimal(long.MinValue));
        Assert.Equal(922337203685477.5807m, CurrencyRounding.ToDecimal(long.MaxValue));
        Assert.Equal(CurrencyRounding.Least, CurrencyRounding.ToDecimal(long.MinValue));
        Assert.Equal(CurrencyRounding.Greatest, CurrencyRounding.ToDecimal(long.MaxValue));
    }

    private static double NextUp(double d)
    {
        if (d == 0) return double.Epsilon;
        long bits = BitConverter.DoubleToInt64Bits(d);
        return BitConverter.Int64BitsToDouble(d > 0 ? bits + 1 : bits - 1);
    }

    private static double NextDown(double d) => -NextUp(-d);
}
