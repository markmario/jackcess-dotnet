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

    // ── What an update of a Row assigns ──────────────────────────────────────

    [Fact]
    public void A_column_assigned_the_value_it_has_is_checked_and_one_not_assigned_is_not()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 20 },
            new ColumnDefinition("B", DataType.Text) { MaxLength = 20 }, new ColumnDefinition("C", DataType.Text) { MaxLength = 20 },
        }, "Id");
        table.Insert(new Row { ["Id"] = 1, ["A"] = "", ["B"] = "b", ["C"] = null });
        table.Columns.First(c => c.Name == "A").Properties.Set("AllowZeroLength", false);
        table.Columns.First(c => c.Name == "C").Properties.Set("Required", true);
        table.Properties.Save();
        var before = IndexChecks.AllPages(db);

        // Access refuses an update that assigns the column the value it already has (DAO: Edit, the field given
        // its own value, Update; SQL: UPDATE T SET A = '', and SET A = A), however it came to be given it...
        var row = table.FindRowByPrimaryKey(1)!;
        row["A"] = "";
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));
        row = table.FindRowByPrimaryKey(1)!;
        row["A"] = "x";
        row["A"] = "";
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));
        row = table.FindRowByPrimaryKey(1)!;
        row["C"] = null;
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));
        row = table.FindRowByPrimaryKey(1)!;
        row.Remove("C");
        row.Add("C", null);
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));
        IndexChecks.AssertUnchanged(before, db);

        // ...and takes one that assigns another column only, or nothing.
        table.Update(table.FindRowByPrimaryKey(1)!);
        row = table.FindRowByPrimaryKey(1)!;
        row["B"] = "b2";
        table.Update(row);
        Assert.Equal("|b2|", string.Join("|", table.FindRowByPrimaryKey(1)!.Values.Skip(1)));
    }

    [Fact]
    public void A_column_assigned_is_written_when_it_is_not_what_the_file_has()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 20 },
            new ColumnDefinition("Bin", DataType.Binary) { MaxLength = 8 }, new ColumnDefinition("Ole", DataType.OleObject),
        }, "Id");
        table.Insert(new Row { ["Id"] = 1, ["A"] = "a", ["Bin"] = new byte[] { 1, 2, 3 }, ["Ole"] = new byte[] { 4, 5, 6 } });

        // Another Row changed the column in between: assigning the value this one was read with writes it.
        var stale = table.FindRowByPrimaryKey(1)!;
        var other = table.FindRowByPrimaryKey(1)!;
        other["A"] = "other";
        table.Update(other);
        stale["A"] = "a";
        table.Update(stale);
        Assert.Equal("a", table.FindRowByPrimaryKey(1)!["A"]);

        // A byte array changed in place and given to the row again is written; one only changed in place is not.
        var row = table.FindRowByPrimaryKey(1)!;
        var bin = (byte[])row["Bin"]!;
        var ole = (byte[])row["Ole"]!;
        bin[0] = 9; ole[0] = 9;
        row["Bin"] = bin;
        row["Ole"] = ole;
        table.Update(row);
        Assert.Equal(new byte[] { 9, 2, 3 }, table.FindRowByPrimaryKey(1)!["Bin"]);
        Assert.Equal(new byte[] { 9, 5, 6 }, table.FindRowByPrimaryKey(1)!["Ole"]);
        row = table.FindRowByPrimaryKey(1)!;
        ((byte[])row["Bin"]!)[0] = 7;
        table.Update(row);
        Assert.Equal(new byte[] { 9, 2, 3 }, table.FindRowByPrimaryKey(1)!["Bin"]);

        // Nothing assigned, or a column assigned what the file holds: nothing written.
        long writes = db.File.WriteCount;
        table.Update(table.FindRowByPrimaryKey(1)!);
        row = table.FindRowByPrimaryKey(1)!;
        row["A"] = "a";
        row["Bin"] = new byte[] { 9, 2, 3 };
        table.Update(row);
        Assert.Equal(writes, db.File.WriteCount);
    }

    [Fact]
    public void A_joiner_joins_on_the_date_a_row_has_been_given()
    {
        using var db = Database.Create(_path);
        var from = db.CreateTable("F", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTime) }, "Id");
        var to = db.CreateTable("O", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTime) },
            new[] { "Id" }, new[] { new IndexDefinition("ByD", "D") });
        var first = new DateTime(2024, 1, 1);
        from.Insert(new Row { ["Id"] = 1, ["D"] = first });
        to.Insert(new Row { ["Id"] = 10, ["D"] = first });
        to.Insert(new Row { ["Id"] = 20, ["D"] = first.AddDays(1) });

        var join = new Joiner(from, new[] { "D" }, to, new[] { "D" });
        var row = from.FindRowByPrimaryKey(1)!;
        Assert.Equal(10, join.FindFirstRow(row)?["Id"]);
        row["D"] = first.AddDays(1);
        Assert.Equal(20, join.FindFirstRow(row)?["Id"]);
        Assert.Equal(new[] { 20 }, join.FindRows(row).Select(r => (int)r["Id"]!));
    }

    // ── Row operations that fail part-way ────────────────────────────────────

    private static string Wide(int id, int length) => new((char)(0x3A9 + id % 20), length);

    [Fact]
    public void A_delete_that_fails_while_it_is_written_is_taken_back()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
            new ColumnDefinition("B", DataType.Text) { MaxLength = 255 }, new ColumnDefinition("C", DataType.Text) { MaxLength = 255 },
            new ColumnDefinition("Notes", DataType.Memo),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByA", "A") });
        for (int i = 1; i <= 30; i++) table.Insert(new Row { ["Id"] = i, ["A"] = Wide(i, 100), ["Notes"] = new string('n', 5000) });
        // A row whose values are on another page than its slot.
        var grown = table.FindRowByPrimaryKey(3)!;
        grown["A"] = Wide(3, 255); grown["B"] = Wide(4, 255); grown["C"] = Wide(5, 255);
        table.Update(grown);
        Assert.Equal(JetFormat.OverflowRowFlag, ByteUtil.GetUShort(db.File.ReadPage(grown.Id!.Value.PageNumber),
            db.File.Format.OffsetDataRowTable + grown.Id.Value.RowNumber * JetFormat.SizeRowEntry) & 0xC000);
        var before = IndexChecks.AllPages(db);

        bool deleted = false;
        for (int writes = 0; writes < 200 && !deleted; writes++)
        {
            db.File.WritesBeforeFailure = writes;
            var thrown = Record.Exception(() => table.Delete(table.FindRowByPrimaryKey(3)!));
            db.File.WritesBeforeFailure = null;
            if (thrown is null) { deleted = true; break; }
            Assert.IsType<IOException>(thrown);
            IndexChecks.AssertUnchanged(before, db);
        }
        Assert.True(deleted);
        Assert.Null(table.FindRowByPrimaryKey(3));
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 29);
    }

    [Fact]
    public void An_insert_that_fails_while_it_is_written_is_taken_back()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true }, new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
            new ColumnDefinition("Notes", DataType.Memo),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByA", "A") });
        for (int i = 1; i <= 14; i++) table.Insert(new Row { ["A"] = Wide(i, 255) });
        var before = IndexChecks.AllPages(db);
        int last = table.LastAutoNumber;

        // The row's long value, its page, the definition and two indexes: a failure at each write leaves the file,
        // the counter and the row as they were.
        bool inserted = false;
        for (int writes = 0; writes < 200 && !inserted; writes++)
        {
            var row = new Row { ["A"] = Wide(99, 255), ["Notes"] = new string('n', 9000) };
            db.File.WritesBeforeFailure = writes;
            var thrown = Record.Exception(() => table.Insert(row));
            db.File.WritesBeforeFailure = null;
            if (thrown is null)
            {
                inserted = true;
                Assert.Equal(last + 1, row["Id"]);
                Assert.True(writes > 4, $"{writes} writes");
                break;
            }
            Assert.IsType<IOException>(thrown);
            IndexChecks.AssertUnchanged(before, db);
            Assert.False(row.ContainsKey("Id"));
            Assert.Equal(last, table.LastAutoNumber);
        }
        Assert.True(inserted);
        Assert.Equal(9000, ((string)table.FindRowByPrimaryKey(last + 1)!["Notes"]!).Length);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 15);
    }
}
