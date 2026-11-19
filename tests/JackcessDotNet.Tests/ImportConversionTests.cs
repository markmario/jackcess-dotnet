using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What an append converts a value to for a column that is there (#44): the same on every target and in every time
/// zone, and only where the column holds the value exactly.
/// </summary>
[Collection(LocalTimeZone.Collection)]
public sealed class ImportConversionTests : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"conversion_{Guid.NewGuid():N}.accdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // A table of one column of each type an append converts to, keyed on Id.
    private void MakeTable()
    {
        if (File.Exists(_path)) File.Delete(_path);
        using var db = Database.Create(_path, FileFormat.Access2010);
        db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("I16", DataType.Int16),
            new ColumnDefinition("Cur", DataType.Currency),
            new ColumnDefinition("Dec", DataType.Decimal) { Precision = 10, Scale = 2 },
            new ColumnDefinition("Dec16", DataType.Decimal) { Precision = 18, Scale = 16 },
            new ColumnDefinition("Dbl", DataType.Double),
            new ColumnDefinition("Sng", DataType.Single),
            new ColumnDefinition("Txt", DataType.Text) { MaxLength = 30 },
            new ColumnDefinition("When", DataType.DateTime),
        }, "Id");
    }

    private static DataTable Source(string column, object value)
    {
        var table = new DataTable("T");
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add(column, value.GetType());
        table.Rows.Add(1, value);
        return table;
    }

    // The value an append of one row stores in the column, read back from the file.
    private object? Appended(string column, object value)
    {
        MakeTable();
        using (var db = Database.Open(_path))
            db.ImportTable(Source(column, value), new ImportOptions { AppendIfExists = true });
        using var reopen = Database.Open(_path);
        return reopen.GetTable("T").FindRowByPrimaryKey(1)![column];
    }

    // An object whose one value goes to the Text column: a DataTable keeps no -0.
    private sealed class ForText<T>
    {
        public int Id { get; set; } = 1;
        [System.ComponentModel.DataAnnotations.Schema.Column("Txt")] public T? Value { get; set; }
    }

    private object? AppendedToText<T>(T value)
    {
        MakeTable();
        using (var db = Database.Open(_path))
            db.ImportTable(new[] { new ForText<T> { Value = value } }, new ImportOptions { TableName = "T", AppendIfExists = true });
        using var reopen = Database.Open(_path);
        return reopen.GetTable("T").FindRowByPrimaryKey(1)!["Txt"];
    }

    // ── Doubles and floats as text, and as decimals ──────────────────────────

    // A double or a float appended to a Text column is its round-trip form - the fewest digits that read back as it,
    // written as .NET writes them - and the same text on every target.
    [Theory]
    [InlineData(0.84551240822557006, "0.8455124082255701")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.0 / 3, "0.3333333333333333")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1E-05")]
    [InlineData(1e16, "10000000000000000")]
    [InlineData(1e17, "1E+17")]
    [InlineData(99999999999999984.0, "99999999999999980")]
    [InlineData(5e-324, "5E-324")]
    [InlineData(2.2250738585072014E-308, "2.2250738585072014E-308")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void A_double_appended_to_a_Text_column_is_its_round_trip_form_on_every_target(double value, string text)
        => Assert.Equal(text, Appended("Txt", value));

    [Theory]
    [InlineData(0.1f, "0.1")]
    [InlineData(16777216f, "16777216")]
    [InlineData(123456789f, "123456790")]
    [InlineData(1e9f, "1E+09")]
    [InlineData(3.4e38f, "3.4E+38")]
    [InlineData(1.17549435E-38f, "1.1754944E-38")]
    [InlineData(1.401298E-45f, "1E-45")]
    public void A_float_appended_to_a_Text_column_is_its_round_trip_form_on_every_target(float value, string text)
        => Assert.Equal(text, Appended("Txt", value));

    [Fact]
    public void A_negative_zero_appended_to_a_Text_column_keeps_its_sign_on_every_target()
    {
        Assert.Equal("-0", AppendedToText(-0.0));
        Assert.Equal("-0", AppendedToText(-0f));
    }

    // A double goes to a Decimal or Currency column as the number it is when the column holds that, and otherwise as
    // the number its round-trip form denotes: a form of sixteen places is held by a column of sixteen, on every target.
    [Fact]
    public void A_double_appended_to_a_Decimal_column_is_the_number_its_round_trip_form_denotes_on_every_target()
    {
        Assert.Equal(0.8455124082255701m, Appended("Dec16", 0.84551240822557006));
        Assert.Equal(0.3333333333333333m, Appended("Dec16", 1.0 / 3));
        Assert.Equal(0.1m, Appended("Cur", 0.1));
        Assert.Equal(0.5m, Appended("Dec", 0.5));
    }

    // ── Text as numbers ──────────────────────────────────────────────────────

    // A column, text appended to it, and what the column holds of it: null where it is refused.
    public static IEnumerable<object?[]> TextToNumbers() => new[]
    {
        new object?[] { "I16", "12", (short)12 },
        new object?[] { "I16", "12.0", (short)12 },
        new object?[] { "I16", " 12 ", (short)12 },
        new object?[] { "I16", "1e2", (short)100 },
        new object?[] { "I16", "-0", (short)0 },
        new object?[] { "I16", "1.00000000000000000000000000001", null },
        new object?[] { "I16", "40000", null },
        new object?[] { "I16", "1e400", null },
        new object?[] { "Cur", "0.1", 0.1m },
        new object?[] { "Cur", "922337203685477.5807", 922337203685477.5807m },
        new object?[] { "Cur", "922337203685477.5808", null },
        new object?[] { "Cur", "1e-30", null },
        new object?[] { "Cur", "0.00000000000000000000000000001", null },
        new object?[] { "Dec", "1.10", 1.1m },
        new object?[] { "Dec", "12345678.91", 12345678.91m },
        new object?[] { "Dec", "123456789.1", null },
        new object?[] { "Dec", "1.005", null },
        new object?[] { "Dec", "1e-30", null },
        new object?[] { "Dbl", "1e-30", 1e-30 },
        new object?[] { "Dbl", "0.1", 0.1 },
        new object?[] { "Dbl", "1.5", 1.5 },
        new object?[] { "Dbl", "-0", -0.0 },
        new object?[] { "Dbl", "5e-324", 5e-324 },
        new object?[] { "Dbl", "1.2345678901234568E+29", 1.2345678901234568E+29 },
        new object?[] { "Dbl", "1152921504606846976", 1152921504606846976.0 },                             // 2^60, the double exactly
        new object?[] { "Dbl", "0.1000000000000000055511151231257827021181583404541015625", 0.1 },        // the double 0.1, exactly
        new object?[] { "Dbl", "0.1000000000000000000000000000001", null },
        new object?[] { "Dbl", "0.10000000000000001", null },
        new object?[] { "Dbl", "123456789012345678901234567890", null },
        new object?[] { "Dbl", "1e400", null },
        new object?[] { "Dbl", "-1e400", null },
        new object?[] { "Dbl", "1e-400", null },
        new object?[] { "Dbl", "NaN", null },
        new object?[] { "Dbl", "Infinity", null },
        new object?[] { "Sng", "3.4e38", 3.4e38f },
        new object?[] { "Sng", "1e-30", 1e-30f },
        new object?[] { "Sng", "0.1", 0.1f },
        new object?[] { "Sng", "16777216", 16777216f },
        new object?[] { "Sng", "16777217", null },
        new object?[] { "Sng", "1e39", null },
    };

    // Text goes to a number column as the number it denotes when the column holds exactly that - for a Double or Single
    // column, when the value it becomes is that number or has it as its round-trip form - and is refused otherwise,
    // past the column's range, NaN and the infinities included; the same on every target.
    [Theory]
    [MemberData(nameof(TextToNumbers), DisableDiscoveryEnumeration = true)]
    public void Text_appended_to_a_number_column_is_the_number_it_denotes_exactly_or_is_refused(string column, string text, object? held)
    {
        if (held is null)
        {
            MakeTable();
            byte[] before = File.ReadAllBytes(_path);
            using (var db = Database.Open(_path))
                Assert.ThrowsAny<ArgumentException>(() => db.ImportTable(Source(column, text), new ImportOptions { AppendIfExists = true }));
            Assert.Equal(before, File.ReadAllBytes(_path));
            return;
        }
        object? stored = Appended(column, text);
        switch (held)
        {
            case double d:
                Assert.Equal(BitConverter.DoubleToInt64Bits(d), BitConverter.DoubleToInt64Bits(Assert.IsType<double>(stored)));
                break;
            case float f:
                Assert.Equal(BitConverter.GetBytes(f), BitConverter.GetBytes(Assert.IsType<float>(stored)));
                break;
            default:
                Assert.Equal(held, stored);
                break;
        }
    }

    // Text whose exponent is past what an int holds, or near it: a number past every column's range, or nearer 0 than
    // any column holds a number apart from 0.
    public static IEnumerable<object[]> FarExponents() =>
        from text in new[]
        {
            "1e4294967296", "-7e4294967296", "100e4294967296", "1.5e4294967296",
            "1e-4294967296", "-7e-4294967296", "100e-4294967296", "1.5e-4294967296",
            "1e2147483648", "1e-2147483648", "1e2147483647", "1e-2147483647", "1e-2147483649", "0.001e2147483650",
            "1e99999999999999999999", "-1e-99999999999999999999", "1e100001", "1e-100001",
        }
        from column in new[] { "I16", "Cur", "Dec", "Dec16", "Dbl", "Sng" }
        select new object[] { column, text };

    // Such text is refused, as other text no column holds, with ArgumentException naming the column, and the file is
    // left as it was: nothing wraps, and no other exception escapes. It was written as 1, -7, 100 or 1.5 where the
    // exponent wrapped to 0, and an exponent of 2^31 threw ArgumentOutOfRangeException.
    [Theory]
    [MemberData(nameof(FarExponents), DisableDiscoveryEnumeration = true)]
    public void Text_with_an_exponent_past_an_int_is_refused_by_every_number_column(string column, string text)
    {
        MakeTable();
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var refused = Assert.Throws<ArgumentException>(() => db.ImportTable(Source(column, text), new ImportOptions { AppendIfExists = true }));
            Assert.Contains($"The value of {column} in", refused.Message);
            Assert.Contains("Nothing was imported", refused.Message);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // A zero is zero, whatever its exponent.
    [Theory]
    [InlineData("0e4294967296")]
    [InlineData("0.000e-4294967296")]
    [InlineData("-0e2147483648")]
    public void Zero_with_any_exponent_is_zero(string text)
    {
        Assert.Equal((short)0, Appended("I16", text));
        Assert.Equal(0.0, Math.Abs(Assert.IsType<double>(Appended("Dbl", text))));
    }

#if NET
    // On .NET the round-trip format is the shortest text that reads back as the double, as ExactNumber's is.
    [Fact]
    public void The_round_trip_form_is_dotnets_for_any_double_and_float()
    {
        var random = new Random(20261119);
        var bytes = new byte[8];
        for (int i = 0; i < 20000; i++)
        {
            random.NextBytes(bytes);
            double d = BitConverter.ToDouble(bytes, 0);
            if (!double.IsNaN(d) && !double.IsInfinity(d))
                Assert.Equal(d.ToString("R", Invariant), ExactNumber.Text(d));
            float f = BitConverter.ToSingle(bytes, 0);
            if (!float.IsNaN(f) && !float.IsInfinity(f))
                Assert.Equal(f.ToString("R", Invariant), ExactNumber.Text(f));
        }
        foreach (double d in new[] { 1.0, 10.0, 1e22, 1e23, 9007199254740993.0, 0.3, 2.0 / 3, 4.35, 1e-7, 123e-20 })
            Assert.Equal(d.ToString("R", Invariant), ExactNumber.Text(d));
    }

    // On .NET, reading text gives the double, and the float, nearest the number it denotes, as ExactNumber does.
    [Fact]
    public void The_nearest_double_and_float_are_dotnets_for_any_text()
    {
        var random = new Random(1119);
        for (int i = 0; i < 20000; i++)
        {
            int digits = random.Next(1, 30);
            var text = new System.Text.StringBuilder(random.Next(2) == 0 ? "" : "-");
            for (int d = 0; d < digits; d++) text.Append((char)('0' + random.Next(10)));
            // Ties: a digit string that ends in 5 at a place a double or float rounds at comes up often enough here.
            text.Append('E').Append(random.Next(-340, 320).ToString(Invariant));
            string s = text.ToString();
            Assert.True(ExactNumber.TryParse(s, out ExactNumber number));
            double parsed = double.Parse(s, NumberStyles.Float, Invariant);
            double? nearest = number.NearestDouble();
            if (double.IsInfinity(parsed)) Assert.Null(nearest);
            else Assert.Equal(BitConverter.DoubleToInt64Bits(parsed), BitConverter.DoubleToInt64Bits(nearest!.Value));
            float parsedSingle = float.Parse(s, NumberStyles.Float, Invariant);
            float? nearestSingle = number.NearestSingle();
            if (float.IsInfinity(parsedSingle)) Assert.Null(nearestSingle);
            else Assert.Equal(BitConverter.GetBytes(parsedSingle), BitConverter.GetBytes(nearestSingle!.Value));
        }
        // Exactly between two doubles, and between the least double and 0.
        foreach (string s in new[] { "9007199254740993", "9007199254740995", "2.4703282292062327E-324", "2.4703282292062328E-324", "1.7976931348623158E+308", "1.7976931348623159E+308" })
        {
            Assert.True(ExactNumber.TryParse(s, out ExactNumber number));
            double parsed = double.Parse(s, NumberStyles.Float, Invariant);
            if (double.IsInfinity(parsed)) Assert.Null(number.NearestDouble());
            else Assert.Equal(BitConverter.DoubleToInt64Bits(parsed), BitConverter.DoubleToInt64Bits(number.NearestDouble()!.Value));
        }
    }
#endif

    // ── Dates ────────────────────────────────────────────────────────────────

    // Text with an offset is the UTC time it denotes, as a DateTimeOffset is; text without one is the time as written.
    [Theory]
    [InlineData("2026-11-19T10:00:00Z", "2026-11-19T10:00:00")]
    [InlineData("2026-11-19T10:00:00+05:00", "2026-11-19T05:00:00")]
    [InlineData("2026-11-19T10:00:00-07:30", "2026-11-19T17:30:00")]
    [InlineData("2026-11-19T00:30:00+01:00", "2026-11-18T23:30:00")]
    [InlineData("2026-11-19 10:00", "2026-11-19T10:00:00")]
    [InlineData("2026-11-19T10:00:00", "2026-11-19T10:00:00")]
    [InlineData("11/19/2026", "2026-11-19T00:00:00")]
    // A time without a date is on 30 December 1899, the day Access gives one; it was on the day of the import.
    [InlineData("10:30", "1899-12-30T10:30:00")]
    [InlineData("22:15:30.5", "1899-12-30T22:15:30.5")]
    [InlineData("10:30 PM", "1899-12-30T22:30:00")]
    [InlineData("00:00", "1899-12-30T00:00:00")]
    [InlineData("10:30Z", "1899-12-30T10:30:00")]
    [InlineData("01:00+05:00", "1899-12-29T20:00:00")]
    [InlineData("23:00-05:00", "1899-12-31T04:00:00")]
    public void Text_appended_to_a_Date_Time_column_is_the_same_time_in_every_time_zone(string text, string expected)
    {
        foreach (double hours in new[] { 11.0, -7.5, 0.0 })
            using (LocalTimeZone.Set(TimeSpan.FromHours(hours)))
                Assert.Equal(DateTime.Parse(expected, Invariant), Appended("When", text));
    }

    // ACE 16 inserts a time without a date, '10:30', in a Date/Time column and in a Date/Time Extended one alike, as 30
    // December 1899 10:30 (measured: the engine reads both so); an append writes it so, in every time zone.
    [Theory]
    [InlineData("10:30", "1899-12-30T10:30:00")]
    [InlineData("22:15", "1899-12-30T22:15:00")]
    [InlineData("2026-11-19 10:30", "2026-11-19T10:30:00")]
    public void Time_only_text_is_on_30_December_1899_in_either_date_column_in_every_time_zone(string text, string expected)
    {
        foreach (double hours in new[] { 13.0, -9.5 })
            using (LocalTimeZone.Set(TimeSpan.FromHours(hours)))
            {
                if (File.Exists(_path)) File.Delete(_path);
                using (var db = Database.Create(_path, FileFormat.Access2016))
                {
                    db.CreateTable("T", new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32),
                        new ColumnDefinition("D", DataType.DateTime),
                        new ColumnDefinition("X", DataType.DateTimeExtended),
                    }, "Id");
                    var source = new DataTable("T");
                    source.Columns.Add("Id", typeof(int));
                    source.Columns.Add("D", typeof(string));
                    source.Columns.Add("X", typeof(string));
                    source.Rows.Add(1, text, text);
                    db.ImportTable(source, new ImportOptions { AppendIfExists = true });
                }
                using var reopen = Database.Open(_path);
                var row = reopen.GetTable("T").FindRowByPrimaryKey(1)!;
                Assert.Equal(DateTime.Parse(expected, Invariant), row["D"]);
                Assert.Equal(DateTime.Parse(expected, Invariant), row["X"]);
            }
    }
}

/// <summary>
/// Sets the time zone this process takes as the machine's, for a test whose result must not depend on it; the
/// machine's own is back when the result is disposed. Tests that use it run alone (<see cref="Collection"/>).
/// </summary>
internal static class LocalTimeZone
{
    internal const string Collection = "The machine's time zone";

    internal static IDisposable Set(TimeSpan offset)
    {
        TimeZoneInfo.ClearCachedData();
        object cached = typeof(TimeZoneInfo).GetField("s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var local = cached.GetType().GetField("_localTimeZone", BindingFlags.NonPublic | BindingFlags.Instance)   // .NET
                 ?? cached.GetType().GetField("m_localTimeZone", BindingFlags.NonPublic | BindingFlags.Instance)!; // .NET Framework
        string name = $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        local.SetValue(cached, TimeZoneInfo.CreateCustomTimeZone(name, offset, name, name));
        Assert.Equal(name, TimeZoneInfo.Local.Id);
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => TimeZoneInfo.ClearCachedData();
    }
}

[CollectionDefinition(LocalTimeZone.Collection, DisableParallelization = true)]
public sealed class LocalTimeZoneCollection { }
