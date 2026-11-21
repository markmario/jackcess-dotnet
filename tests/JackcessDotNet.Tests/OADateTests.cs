using System.Globalization;
using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A Date/Time is read and written as a 64-bit process reads and writes it, whatever the precision of the x87 unit a
/// 32-bit process computes doubles with (#100). <see cref="DateTime.FromOADate"/> and <see cref="DateTime.ToOADate"/>
/// compute in doubles; with the unit at 64 bits of precision, as ACE leaves a thread that ran a statement, a stored
/// number a hair from a millisecond's half read as the millisecond beside the one a 64-bit process reads, and a date
/// was written as the double one above or below. The library converts in integers (<see cref="OADate"/>). Each test
/// here works with the unit at 64 bits (<see cref="X87"/>), and holds the result to the runtime's own conversion at 53
/// bits - in a 64-bit process, its only one.
/// <para>
/// The other conversions of a number on its way to or from a file were measured alike and do not depend on the unit,
/// and are held to that here: a double or a float to a Decimal column (<see cref="AutomationDecimal.From(double)"/>),
/// a double to a Currency (<see cref="CurrencyRounding"/>, in integers), and a decimal to a Double or a Single column.
/// <see cref="AutomationDecimal.Rounded"/> is not among them: it rounds in doubles, and the library calls it only on
/// a runtime whose own conversion does not round, .NET 11 and later, which computes doubles with SSE2.
/// </para>
/// </summary>
public sealed class OADateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"oadate_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private const long MillisPerDay = 86_400_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Stored numbers that a 32-bit process with the unit at 64 bits read as the millisecond beside the one given here,
    // which a 64-bit process reads: the first two a little under half a millisecond either side of midnight on
    // 30 December 1899, the rest from the year 550 to the year 9971.
    private static readonly (ulong Bits, string Date)[] ReadCases =
    {
        (0x3E38DAEA1D7F4CF6, "1899-12-30 00:00:00.001"),
        (0xBE38DAEA1D7F4CF6, "1899-12-30 00:00:00.001"),
        (0xC11E17619CC339D9, "0550-03-01 09:40:26.844"),
        (0xC111A4A00C2888A1, "1108-07-26 00:17:05.859"),
        (0xC0E35DB93D517005, "1791-05-29 18:55:46.714"),
        (0x410B2163ECCE6C58, "2508-07-02 11:46:30.267"),
        (0x411ABB88790FBECD, "3099-02-28 02:50:14.564"),
        (0x4124DF6840BCFC53, "3772-08-08 03:02:04.575"),
        (0x412D2F30F52C213D, "4518-04-16 11:29:32.839"),
        (0x4133B9623D5E4EC8, "5439-02-17 05:45:11.831"),
        (0x4138799DB9C888A1, "6291-08-04 17:25:01.875"),
        (0x413ACB1D9F1F427A, "6707-07-31 14:55:03.711"),
        (0x413F281D2999F8A6, "7490-06-27 03:54:00.489"),
        (0x414180D9709888A1, "8181-04-14 21:06:42.188"),
        (0x41445078B137362C, "9190-01-22 09:13:40.577"),
        (0x41467E048670F3B2, "9971-08-20 01:12:27.822"),
    };

    // Dates that a 32-bit process with the unit at 64 bits wrote as the double one above or below the one given here,
    // which a 64-bit process writes.
    private static readonly (string Date, ulong Bits)[] WrittenCases =
    {
        ("0104-11-05 09:04:37.2690000", 0xC1240262C1A4A099),
        ("0898-11-23 11:56:11.5570000", 0xC1165131FD4AE313),
        ("1518-07-23 18:50:02.7580000", 0xC10101B6472D2E2F),
        ("2132-09-10 22:31:07.0040000", 0x40F4BFFF032D2E2F),
        ("2985-04-15 07:32:36.6785347", 0x411831AD41DB5F67),
        ("3681-12-02 02:48:15.2183733", 0x4123DCA63BD2D1D1),
        ("4501-10-23 07:00:32.6870405", 0x412D002A9586EBB5),
        ("5349-03-17 19:33:48.3140000", 0x41333917D0AD2E2F),
        ("6137-05-09 12:36:51.5890000", 0x41379D8F868D87FD),
        ("6966-05-29 13:15:43.6400000", 0x413C3C658D766F61),
        ("7758-11-23 11:57:46.9280000", 0x41405387BFCD87FD),
        ("8567-05-16 09:57:50.6510000", 0x41429444B52446CB),
        ("9250-10-05 05:15:18.2480000", 0x41447BC61C06EBB5),
        ("9988-06-22 09:17:58.6270000", 0x41468A083199144B),
    };

    public static TheoryData<ulong, string> Read()
    {
        var data = new TheoryData<ulong, string>();
        foreach (var (bits, date) in ReadCases) data.Add(bits, date);
        return data;
    }

    public static TheoryData<string, ulong> Written()
    {
        var data = new TheoryData<string, ulong>();
        foreach (var (date, bits) in WrittenCases) data.Add(date, bits);
        return data;
    }

    private static DateTime Date(string text)
        => DateTime.ParseExact(text, text.Length > 23 ? "yyyy-MM-dd HH:mm:ss.fffffff" : "yyyy-MM-dd HH:mm:ss.fff", Invariant);

    private static double Number(ulong bits) => BitConverter.Int64BitsToDouble(unchecked((long)bits));

    private static ulong Bits(double number) => unchecked((ulong)BitConverter.DoubleToInt64Bits(number));

    [Theory]
    [MemberData(nameof(Read))]
    public void a_stored_number_reads_as_the_millisecond_a_64_bit_process_reads(ulong bits, string date)
    {
        double number = Number(bits);
        Assert.Equal(Date(date), X87.At53Bits(() => DateTime.FromOADate(number)));
        Assert.Equal(Date(date), X87.At64Bits(() => RowDecoder.ReadDate(number)));
        Assert.Equal(Date(date), X87.At64Bits(() => ValueMatch.ReadsAs(number)));
    }

    [Theory]
    [MemberData(nameof(Written))]
    public void a_date_is_written_as_the_number_a_64_bit_process_writes(string date, ulong bits)
    {
        Assert.Equal(bits, Bits(X87.At53Bits(() => Date(date).ToOADate())));
        Assert.Equal(bits, Bits(X87.At64Bits(() => OADate.FromDateTime(Date(date)))));
    }

    [Fact]
    public void dates_written_to_a_table_and_numbers_read_from_one_with_the_unit_at_64_bits_are_a_64_bit_process_s()
    {
        var written = WrittenCases.Select(c => (Date(c.Date), c.Bits)).ToList();
        var read = ReadCases.Select(c => (c.Bits, Date(c.Date))).ToList();

        // Rows with whole days for the numbers to be read: each day's number is put in the file's bytes in place of it.
        X87.At64Bits(() =>
        {
            using var db = Database.Create(_path, FileFormat.Access2000);
            var dates = db.CreateTable("W", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTime) },
                new[] { "Id" }, new[] { new IndexDefinition("ByD", "D") });
            for (int i = 0; i < written.Count; i++) dates.Insert(new Row { ["Id"] = i, ["D"] = written[i].Item1 });
            var numbers = db.CreateTable("R", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTime) }, "Id");
            for (int i = 0; i < read.Count; i++) numbers.Insert(new Row { ["Id"] = i, ["D"] = new DateTime(2000, 1, 1).AddDays(i) });
        });
        byte[] file = File.ReadAllBytes(_path);
        for (int i = 0; i < read.Count; i++)
        {
            byte[] day = BitConverter.GetBytes(new DateTime(2000, 1, 1).AddDays(i).ToOADate());
            int at = Enumerable.Range(0, file.Length - 7).Single(p => file.AsSpan(p, 8).SequenceEqual(day));
            BitConverter.GetBytes(read[i].Item1).CopyTo(file, at);
        }
        File.WriteAllBytes(_path, file);

        X87.At64Bits(() =>
        {
            using var db = Database.Open(_path);
            var dates = db.GetTable("W");
            var cursor = dates.CreateCursor();
            for (int i = 0; i < written.Count; i++)
            {
                var (date, bits) = written[i];
                Assert.NotNull(cursor.FindRow("Id", i));
                Assert.Equal(bits, Bits(cursor.GetCurrentRowDateNumber("D")!.Value));
                Assert.Equal(i, dates.CreateIndexCursor("ByD").FindRowByEntry(date)?["Id"]);
            }
            foreach (var row in db.GetTable("R").CreateCursor())
                Assert.Equal(read[(int)row["Id"]!].Item2, row["D"]);
        });
    }

    [Fact]
    public void numbers_either_side_of_each_half_millisecond_read_as_the_runtime_reads_them_at_53_bits()
    {
        // Days at each end of the range and about 0, and spread between; times at the second's and the day's ends; and
        // a sample of millisecond counts across the range. For each count, the doubles nearest it and its halves
        // either side, and four more each way.
        var counts = new List<long>();
        var days = new long[] { -657434, -657433, -1, 0, 1, 2958464, 2958465 }.Concat(Enumerable.Range(0, 60).Select(k => -657434 + k * 60_998L));
        var times = new long[] { 0, 1, 499, 500, 999, 43_199_999, 43_200_000, 86_399_499, 86_399_500, 86_399_999 };
        foreach (long day in days)
            foreach (long time in times)
            {
                if (day >= 0) counts.Add(day * MillisPerDay + time);
                if (day <= 0) counts.Add(day * MillisPerDay - time);
            }
        var random = new Random(100);
        long low = -657435 * MillisPerDay + 1, high = 2958466 * MillisPerDay - 1;
        for (int i = 0; i < 15_000; i++) counts.Add(low + (long)(random.NextDouble() * (high - low)));
        var numbers = new List<double>(ReadCases.Select(c => Number(c.Bits)));
        foreach (long count in counts)
            foreach (long twice in new[] { 2 * count - 1, 2 * count, 2 * count + 1 })
            {
                double nearest = twice / (2.0 * MillisPerDay);
                for (int k = -4; k <= 4; k++) numbers.Add(Step(nearest, k));
            }

        long?[] runtime53 = X87.At53Bits(() => numbers.Select(n => Ticks(() => DateTime.FromOADate(n))).ToArray());
        long?[] library64 = X87.At64Bits(() => numbers.Select(n => RowDecoder.ReadDate(n)?.Ticks).ToArray());
        var differ = Enumerable.Range(0, numbers.Count).Where(i => runtime53[i] != library64[i]).ToList();
        Assert.True(differ.Count == 0, $"{differ.Count} of {numbers.Count} numbers read otherwise, the first {numbers[differ.FirstOrDefault()]:R}");

        // The sample still has numbers the runtime itself reads otherwise at 64 bits.
        if (X87.Applies)
        {
            long?[] runtime64 = X87.At64Bits(() => numbers.Select(n => Ticks(() => DateTime.FromOADate(n))).ToArray());
            Assert.True(Enumerable.Range(0, numbers.Count).Count(i => runtime53[i] != runtime64[i]) >= ReadCases.Length,
                "the numbers no longer tell the unit's two states apart");
        }
    }

    [Fact]
    public void dates_are_written_as_the_runtime_writes_them_at_53_bits()
    {
        // Dates to the millisecond and to the tick across the range, either side of 30 December 1899, and the times of
        // day alone, which are times on 30 December 1899.
        var dates = new List<DateTime>(WrittenCases.Select(c => Date(c.Date)));
        var random = new Random(100);
        long first = new DateTime(100, 1, 1).Ticks, last = DateTime.MaxValue.Ticks;
        for (int i = 0; i < 150_000; i++)
        {
            long ticks = first + (long)(random.NextDouble() * (last - first));
            dates.Add(new DateTime(i % 4 == 0 ? ticks : ticks - ticks % TimeSpan.TicksPerMillisecond));
        }
        dates.AddRange(new[] { new DateTime(1899, 12, 30), new DateTime(1899, 12, 29, 23, 59, 59, 999), new DateTime(1899, 12, 30, 0, 0, 0, 1),
            new DateTime(100, 1, 1), DateTime.MaxValue, new DateTime(1, 1, 1), new DateTime(1, 1, 1, 6, 0, 0), new DateTime(1, 1, 1, 23, 59, 59, 999) });

        ulong[] runtime53 = X87.At53Bits(() => dates.Select(d => Bits(d.ToOADate())).ToArray());
        ulong[] library64 = X87.At64Bits(() => dates.Select(d => Bits(OADate.FromDateTime(d))).ToArray());
        var differ = Enumerable.Range(0, dates.Count).Where(i => runtime53[i] != library64[i]).ToList();
        Assert.True(differ.Count == 0, $"{differ.Count} of {dates.Count} dates written otherwise, the first {dates[differ.FirstOrDefault()]:o}");
        Assert.Throws<OverflowException>(() => OADate.FromDateTime(new DateTime(99, 12, 31)));

        if (X87.Applies)
        {
            ulong[] runtime64 = X87.At64Bits(() => dates.Select(d => Bits(d.ToOADate())).ToArray());
            Assert.True(Enumerable.Range(0, dates.Count).Count(i => runtime53[i] != runtime64[i]) >= WrittenCases.Length,
                "the dates no longer tell the unit's two states apart");
        }
    }

    [Fact]
    public void numbers_past_the_dates_access_holds_read_as_none_and_the_ends_as_the_runtime_reads_them()
    {
        foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -657435.0, 2958466.0, 1e300, -1e300 })
        {
            Assert.Null(X87.At64Bits(() => RowDecoder.ReadDate(number)));
            Assert.Throws<ArgumentException>(() => OADate.ToDateTime(number));
        }
        foreach (double end in new[] { -657435.0, 2958466.0, 0.0 })
            for (int k = -64; k <= 64; k++)
            {
                double number = Step(end, k);
                Assert.Equal(X87.At53Bits(() => Ticks(() => DateTime.FromOADate(number))), X87.At64Bits(() => Ticks(() => OADate.ToDateTime(number))));
            }
        foreach (double tiny in new[] { double.Epsilon, -double.Epsilon, 1e-300, -1e-300, -0.0 })
            Assert.Equal(new DateTime(1899, 12, 30), OADate.ToDateTime(tiny));
    }

    [Fact]
    public void numbers_taken_to_a_decimal_a_currency_a_double_or_a_single_are_the_same_at_either_precision()
    {
        // Doubles and floats nearest the half of a last digit - of 15 and of 7 significant digits, and of the fourth
        // place - and their neighbours, either sign, across the powers of ten a decimal holds; decimals at the
        // midpoint of two doubles, of two floats, and either side.
        var random = new Random(100);
        var doubles = new List<double>();
        var floats = new List<float>();
        for (int e = -29; e <= 28; e++)
            for (int i = 0; i < 40; i++)
            {
                long digits = 100_000_000_000_000 + (long)(random.NextDouble() * 899_999_999_999_999);
                double half = double.Parse($"{digits}5E{e - 15}", Invariant);
                float halfFloat = float.Parse($"{digits / 100_000_000}5E{e - 7}", Invariant);
                double currency = double.Parse($"{digits / (long)Math.Pow(10, i % 15)}5E-5", Invariant);
                for (int k = -2; k <= 2; k++)
                {
                    doubles.Add(Step(half, k)); doubles.Add(-Step(half, k));
                    doubles.Add(Step(currency, k)); doubles.Add(-Step(currency, k));
                    floats.Add((float)Step(halfFloat, k)); floats.Add(-(float)Step(halfFloat, k));
                }
            }
        var decimals = new List<decimal>();
        for (int i = 0; i < 4000; i++)
        {
            // An odd number of 54 (or 25) bits, shifted: the midpoint of two doubles (or floats), whole.
            int significand = i % 2 == 0 ? 53 : 24;
            ulong top = 1UL << significand;
            ulong odd = top | (ulong)(random.NextDouble() * top) | 1;
            var midpoint = new System.Numerics.BigInteger(odd) << random.Next(0, 95 - significand);
            foreach (int delta in new[] { -1, 0, 1 })
            {
                byte[] bytes = (midpoint + delta).ToByteArray();
                Array.Resize(ref bytes, 16);
                decimals.Add(new decimal(BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4), BitConverter.ToInt32(bytes, 8), i % 3 == 0, 0));
            }
        }

        string Both(Func<string> work) => X87.At53Bits(work) + " | " + X87.At64Bits(work);
        foreach (double d in doubles)
        {
            string both = Both(() => Decimal(() => AutomationDecimal.From(d)) + " " + (CurrencyRounding.TryUnits(d, out long units) ? units : 0) + " " + Bits((float)d));
            Assert.True(Same(both), $"{d:R}: {both}");
        }
        foreach (float f in floats)
        {
            string both = Both(() => Decimal(() => AutomationDecimal.From(f)));
            Assert.True(Same(both), $"{f:R}: {both}");
        }
        foreach (decimal m in decimals)
        {
            string both = Both(() => Bits(Convert.ToDouble(m)) + " " + BitConverter.ToInt32(BitConverter.GetBytes(Convert.ToSingle(m)), 0));
            Assert.True(Same(both), $"{m}: {both}");
        }
    }

    private static bool Same(string both)
    {
        int bar = both.IndexOf(" | ", StringComparison.Ordinal);
        return both.Substring(0, bar) == both.Substring(bar + 3);
    }

    private static string Decimal(Func<decimal> convert)
    {
        try { return string.Join(",", decimal.GetBits(convert())); }
        catch (OverflowException) { return "overflow"; }
    }

    private static long? Ticks(Func<DateTime> read)
    {
        try { return read().Ticks; }
        catch (ArgumentException) { return null; }
    }

    // The double k steps along from 'number', towards plus infinity for k above 0 and minus infinity below.
    private static double Step(double number, int k)
    {
        for (; k > 0; k--) number = number == 0 ? double.Epsilon : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(number) + (number > 0 ? 1 : -1));
        for (; k < 0; k++) number = number == 0 ? -double.Epsilon : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(number) + (number > 0 ? -1 : 1));
        return number;
    }
}
