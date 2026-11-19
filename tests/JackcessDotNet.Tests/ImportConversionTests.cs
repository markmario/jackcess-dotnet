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
    public void Text_appended_to_a_Date_Time_column_is_the_same_time_in_every_time_zone(string text, string expected)
    {
        foreach (double hours in new[] { 11.0, -7.5, 0.0 })
            using (LocalTimeZone.Set(TimeSpan.FromHours(hours)))
                Assert.Equal(DateTime.Parse(expected, Invariant), Appended("When", text));
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
