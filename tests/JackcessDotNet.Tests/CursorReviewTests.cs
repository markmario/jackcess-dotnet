using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What a second review of the cursors and row operations found (#29, #34, #35): dates as a row stores them, what
/// an update of a <see cref="Row"/> assigns, row operations that fail part-way, rows whose keys change around a
/// cursor that has not given them yet, savepoints across such changes, dates anywhere in a range's entries, and
/// times stored as negative numbers.
/// </summary>
public sealed class CursorReviewTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cursor_review_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static object?[] E(params object?[] values) => values;

    /// <summary>Rows (Id, G, D, L) storing the numbers given for D: each inserted, then given its number.</summary>
    private static Table Dated(Database db, bool descending, params (int G, double D, int L)[] rows)
    {
        var byDL  = new IndexDefinition("ByDL", "D", "L");
        var byGDL = new IndexDefinition("ByGDL", "G", "D", "L");
        if (descending) { byDL.DescendingColumns.Add("D"); byGDL.DescendingColumns.Add("D"); }
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("G", DataType.Int32),
            new ColumnDefinition("D", DataType.DateTime), new ColumnDefinition("L", DataType.Int32),
        }, new[] { "Id" }, new[] { byDL, byGDL, new IndexDefinition("ByD", "D") });
        for (int i = 0; i < rows.Length; i++)
        {
            table.Insert(new Row { ["Id"] = i, ["G"] = rows[i].G, ["L"] = rows[i].L });
            table.RewriteRowAt(table.FindRowByPrimaryKey(i)!.Pointer, new Row { ["D"] = rows[i].D });
        }
        return table;
    }

    // ── Dates as a row stores them ───────────────────────────────────────────

    [Fact]
    public void A_date_finer_than_a_millisecond_finds_the_row_it_was_written_to()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("D", DataType.DateTime), new ColumnDefinition("Id", DataType.Int32),
        }, new[] { "D" }, new[] { new IndexDefinition("ById", "Id") });
        var now = new DateTime(2024, 5, 6, 7, 8, 9).AddTicks(1_234_567);     // as DateTime.Now is: to the tick
        table.Insert(new Row { ["D"] = now, ["Id"] = 1 });
        table.Insert(new Row { ["D"] = now.AddSeconds(1), ["Id"] = 2 });
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, 123), table.FindRowByPrimaryKey(now.ToOADate())?["D"]);

        Assert.Equal(1, table.FindRowByPrimaryKey(now)?["Id"]);
        Assert.Equal(1, table.CreateCursor().FindRow("D", now)?["Id"]);
        Assert.Equal(1, table.CreateIndexCursor().FindRowByEntry(now)?["Id"]);
        Assert.Equal(1, table.CreateIndexCursor("ById").FindRow("D", now)?["Id"]);
        Assert.Equal(new[] { 1 }, table.CreateIndexCursor(new IndexRange { StartEntry = E(now), EndEntry = E(now) }).Select(r => (int)r["Id"]!));
        Assert.Equal(1, table.CreateIndexCursor().FindClosestRowByEntry(now)?["Id"]);
    }

    [Fact]
    public void A_time_on_the_first_day_a_date_can_be_is_read()
    {
        // What Access takes and reads (ACE 16): 1 January 100 at any time, to 31 December 9999 at any time.
        Assert.Equal(new DateTime(100, 1, 1, 6, 0, 0), ValueMatch.ReadsAs(-657434.25));
        Assert.Equal(new DateTime(100, 1, 1), ValueMatch.ReadsAs(-657434.0));
        Assert.Equal(new DateTime(100, 1, 1, 23, 59, 59), ValueMatch.ReadsAs(-657434.99998842593));
        Assert.Null(ValueMatch.ReadsAs(-657435.0));
        Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59), ValueMatch.ReadsAs(2958465.99998842593));
        Assert.Null(ValueMatch.ReadsAs(2958466.0));

        // Stored by Access as those numbers, and written by the engine from the dates.
        using var db = Database.Create(_path);
        var table = Dated(db, false, (1, -657434.25, 1), (1, -657434.99998842593, 2));
        Assert.Equal(new DateTime(100, 1, 1, 6, 0, 0), table.FindRowByPrimaryKey(0)!["D"]);
        Assert.Equal(new DateTime(100, 1, 1, 23, 59, 59), table.FindRowByPrimaryKey(1)!["D"]);
        Assert.Equal(1, table.CreateIndexCursor("ByD").FindRowByEntry(new DateTime(100, 1, 1, 6, 0, 0))?["L"]);
        table.Insert(new Row { ["Id"] = 10, ["D"] = new DateTime(100, 1, 1, 12, 30, 0), ["L"] = 3 });
        table.Insert(new Row { ["Id"] = 11, ["D"] = new DateTime(9999, 12, 31, 23, 59, 59), ["L"] = 4 });
        Assert.Equal(new DateTime(100, 1, 1, 12, 30, 0), table.FindRowByPrimaryKey(10)!["D"]);
        var cursor = table.CreateIndexCursor();
        Assert.NotNull(cursor.FindRowByEntry(10));
        Assert.Equal(new DateTime(100, 1, 1, 12, 30, 0).ToOADate(), cursor.GetCurrentRowDateNumber("D"));
        Assert.True(cursor.GetCurrentRowDateNumber("D") < -657434.5);
        Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59), table.FindRowByPrimaryKey(11)!["D"]);
        Assert.Equal(new[] { 1, 2, 3 }, table.CreateIndexCursor("ByD", new IndexRange { EndEntry = E(new DateTime(100, 1, 2)) })
            .Select(r => (int)r["L"]!).OrderBy(l => l));
    }
}
