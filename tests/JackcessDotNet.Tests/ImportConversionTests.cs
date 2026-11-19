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
