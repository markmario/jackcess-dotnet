using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A 64-bit process converts a Date/Time with the runtime's own <see cref="DateTime.FromOADate"/> and
/// <see cref="DateTime.ToOADate"/>, and any other with <see cref="OADate"/>'s integers (#125). That holds only while the
/// two give the same results, which is held here over millions of inputs, in every configuration the tests run in:
/// the integers, with the unit at 64 bits where the process has one, against the runtime at 53 bits.
/// <see cref="OADateTests"/> holds the conversions the library uses to the runtime's at 53 bits.
/// </summary>
public sealed class OADateChoiceTests
{
    private const int Sample = 1_000_000;

    private static ulong Bits(double value) => (ulong)BitConverter.DoubleToInt64Bits(value);

    private static long? Ticks(Func<DateTime> read)
    {
        try { return read().Ticks; }
        catch (ArgumentException) { return null; }
    }

    [Fact]
    public void a_64_bit_process_converts_with_the_runtime_and_any_other_with_integers()
        => Assert.Equal(Environment.Is64BitProcess, OADate.RuntimeConverts);

    /// <summary>
    /// Numbers a row can store: the dates of every millisecond across the range as the runtime writes them, numbers of
    /// any 53 bits from the least to the greatest a date is read from, numbers a hair either side of a millisecond's
    /// half, and the ends of the range and beyond them.
    /// </summary>
    [Fact]
    public void the_integers_read_every_number_as_the_runtime_reads_it()
    {
        var random = new Random(125);
        var numbers = new List<double>(4 * Sample + 16);
        long first = new DateTime(100, 1, 1).Ticks, last = DateTime.MaxValue.Ticks;
        for (int i = 0; i < Sample; i++)
        {
            long ticks = first + (long)(random.NextDouble() * (last - first));
            numbers.Add(new DateTime(ticks - ticks % TimeSpan.TicksPerMillisecond).ToOADate());
            numbers.Add(-657435.0 + random.NextDouble() * (2958466.0 + 657435.0));
            numbers.Add(random.NextDouble() * Math.Pow(2, random.Next(-40, 22)) * (random.Next(2) == 0 ? 1 : -1));
            // A millisecond's half, and the doubles nearest it either side.
            double half = ((long)(random.NextDouble() * 1e11) + 0.5) / 86_400_000.0 * (random.Next(4) == 0 ? -1 : 1);
            long bits = BitConverter.DoubleToInt64Bits(half);
            numbers.Add(BitConverter.Int64BitsToDouble(bits + random.Next(-2, 3)));
        }
        numbers.AddRange(new[] { 0.0, -0.0, double.Epsilon, 5.7870370370370361E-09, -5.7870370370370361E-09, 2958465.9999999998,
            2958466.0, -657434.99999999988, -657435.0, 1e-300, -1e-300, 2958465.999999994, -657434.999999994 });

        long?[] runtime = X87.At53Bits(() => numbers.Select(n => Ticks(() => DateTime.FromOADate(n))).ToArray());
        long?[] integers = X87.At64Bits(() => numbers.Select(n => Ticks(() => OADate.ToDateTimeInIntegers(n))).ToArray());
        var differ = Enumerable.Range(0, numbers.Count).Where(i => runtime[i] != integers[i]).ToList();
        Assert.True(differ.Count == 0,
            $"{differ.Count} of {numbers.Count} numbers read otherwise, the first {numbers[differ.FirstOrDefault()]:R}");
    }

    /// <summary>Dates to the tick and to the millisecond across the range, either side of 30 December 1899, and times of day alone.</summary>
    [Fact]
    public void the_integers_write_every_date_as_the_runtime_writes_it()
    {
        var random = new Random(125);
        var dates = new List<DateTime>(2 * Sample);
        long first = new DateTime(100, 1, 1).Ticks, last = DateTime.MaxValue.Ticks;
        for (int i = 0; i < Sample; i++)
        {
            long ticks = first + (long)(random.NextDouble() * (last - first));
            dates.Add(new DateTime(ticks));
            dates.Add(new DateTime(ticks - ticks % TimeSpan.TicksPerMillisecond));
        }
        for (int i = 0; i < 1000; i++) dates.Add(new DateTime((long)(random.NextDouble() * TimeSpan.TicksPerDay)));
        dates.AddRange(new[] { new DateTime(1899, 12, 30), new DateTime(1899, 12, 29, 23, 59, 59, 999), new DateTime(100, 1, 1), DateTime.MaxValue, new DateTime(1, 1, 1) });

        ulong[] runtime = X87.At53Bits(() => dates.Select(d => Bits(d.ToOADate())).ToArray());
        ulong[] integers = X87.At64Bits(() => dates.Select(d => Bits(OADate.FromDateTimeInIntegers(d))).ToArray());
        var differ = Enumerable.Range(0, dates.Count).Where(i => runtime[i] != integers[i]).ToList();
        Assert.True(differ.Count == 0, $"{differ.Count} of {dates.Count} dates written otherwise, the first {dates[differ.FirstOrDefault()]:o}");
        Assert.Throws<OverflowException>(() => OADate.FromDateTimeInIntegers(new DateTime(99, 12, 31)));
        Assert.Throws<OverflowException>(() => new DateTime(99, 12, 31).ToOADate());
    }
}
