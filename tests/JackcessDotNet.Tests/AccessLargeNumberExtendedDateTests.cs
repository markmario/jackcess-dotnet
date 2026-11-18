using System.Globalization;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Large Number (Access 2016) and Date/Time Extended (Access 2019) columns as Access wrote them
/// (qcatco/exportmdb.interop#40): every value is read as the one Access was given, and every index on them holds
/// each row under the key this library makes for it - so the keys are Access's, for the ends of both ranges, in
/// both directions and with another column after them. Assets/ace-large-numbers-extended-dates.accdb was made by
/// tools/large-number-extended-date-fixture.ps1, which has the same rows as <see cref="Numbers"/> and
/// <see cref="Dates"/>; the corpus's own file of extended dates is Access 2019's.
/// </summary>
public sealed class AccessLargeNumberExtendedDateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"large_numbers_{Guid.NewGuid():N}.accdb");

    public AccessLargeNumberExtendedDateTests()
        => File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "ace-large-numbers-extended-dates.accdb"), _path);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    internal static readonly long?[] Numbers =
    {
        long.MaxValue, long.MinValue, -1, 0, 1, 4294967296, -4294967296, 255, 256, -256, 2147483648, null,
    };

    internal static readonly string?[] Dates =
    {
        "0001-01-01 00:00:00.0000000", "9999-12-31 23:59:59.9999999", "1899-12-29 23:59:59.9999999",
        "1899-12-30 00:00:00.0000000", "2020-01-02 03:04:05.1234567", "2020-01-02 03:04:05.1234568",
        "1066-10-14 09:00:00.0000001", "0100-06-14 12:45:00.1234567", "2024-02-29 23:59:59.9999999",
        null, "1000-01-01 00:00:00.0000000", "1999-12-31 00:00:00.0000000",
    };

    internal static DateTime? Date(string? text)
        => text is null ? null : DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static object? Value(Row row, string column) => row.TryGetValue(column, out var value) ? value : null;

    // BySql keeps its Large Number among a row's variable-length values, as SQL's BIGINT is kept; ByDao among the
    // fixed-length ones.
    [Theory]
    [InlineData("BySql", true)]
    [InlineData("ByDao", false)]
    public void Access_s_large_numbers_and_extended_dates_are_read_as_it_was_given_them(string name, bool numberIsVariable)
    {
        using var db = Database.Open(_path);
        var table = db.GetTable(name);
        Assert.Equal(numberIsVariable, table.Columns.Single(c => c.Name == "N").IsVariableLength);

        var rows = table.ReadAllRows().OrderBy(r => (int)r["Id"]!).ToList();
        Assert.Equal(Numbers.Length, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(Numbers[i], (long?)Value(rows[i], "N"));
            Assert.Equal(Date(Dates[i]), (DateTime?)Value(rows[i], "D"));
            // To the tick, and of no time zone.
            if (Value(rows[i], "D") is DateTime read)
            {
                Assert.Equal(Date(Dates[i])!.Value.Ticks, read.Ticks);
                Assert.Equal(DateTimeKind.Unspecified, read.Kind);
            }
        }
    }

    [Theory]
    [InlineData("BySql")]
    [InlineData("ByDao")]
    public void Every_index_Access_made_on_them_holds_each_row_under_the_key_this_library_makes(string name)
    {
        using var db = Database.Open(_path);
        // The primary key, each column ascending and descending, and the two together.
        Assert.Equal(6, IndexChecks.AssertEveryIndexHoldsTheRows(db, name, expectedRows: Numbers.Length));
    }

    [Theory]
    [InlineData("BySql")]
    [InlineData("ByDao")]
    public void A_row_Access_wrote_is_found_by_its_large_number_and_by_its_extended_date(string name)
    {
        using var db = Database.Open(_path);
        var table = db.GetTable(name);
        for (int i = 0; i < Numbers.Length; i++)
        {
            if (Numbers[i] is { } number)
                Assert.Equal(i + 1, table.NewIndexCursor("NAsc").FindRow("N", number)!["Id"]);
            if (Date(Dates[i]) is { } date)
                Assert.Equal(i + 1, table.NewIndexCursor("DDesc").FindRow("D", date)!["Id"]);
        }
        Assert.Null(table.NewIndexCursor("NAsc").FindRow("N", 2L));
        Assert.Null(table.NewIndexCursor("DAsc").FindRow("D", new DateTime(2020, 1, 2, 3, 4, 5).AddTicks(1234569)));
    }

    // The corpus's file: nine rows, the dates Access shows for them in the text column beside.
    [Fact]
    public void The_corpus_s_extended_dates_are_read_to_the_tick()
    {
        string?[] expected =
        {
            "2020-06-17 00:00:00.0000000", "2021-06-14 00:00:00.0000000", "2021-06-14 12:45:00.0000000",
            "2021-06-14 01:45:00.0000000", null, "2021-06-14 22:45:12.3456789", "1765-06-14 12:45:00.0000000",
            "0100-06-14 12:45:00.1234567", "1265-06-14 12:45:00.0000000",
        };

        using var db = Database.Open(TestCorpus.File("V2019", "extDateV2019.accdb") ?? throw new FileNotFoundException("extDateV2019.accdb"));
        var rows = db.GetTable("Table1").ReadAllRows().OrderBy(r => (int)r["ID"]!).ToList();
        Assert.Equal(expected.Select(Date), rows.Select(r => (DateTime?)Value(r, "DateExt")));

        // Its two indexes on the column, one descending, hold the rows under this library's keys.
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table1", expectedRows: 9));
    }
}
