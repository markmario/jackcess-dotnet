using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What a third review of the cursors and row operations found (#29, #34): a row given to an insert is the row
/// inserted; a cursor's update of a row it read assigns what the row was given.
/// </summary>
public sealed class CursorThirdReviewTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cursor_third_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string Names(Table table) => string.Join("|", table.Rows.Select(r => $"{r["Id"]}={r["Name"]}"));

    // ── A row given to an insert ─────────────────────────────────────────────

    [Fact]
    public void A_row_inserted_is_the_row_it_was_inserted_as_and_an_update_of_it_changes_that_row()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true }, new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
        }, "Id");
        table.Insert(new Row { ["Name"] = "original" });

        // A row read and inserted again as a copy is the copy from then on.
        var copy = table.FindRowByPrimaryKey(1)!;
        var first = copy.Id;
        copy["Name"] = "copy";
        table.Insert(copy);
        Assert.Equal(2, copy["Id"]);
        Assert.Equal(table.FindRowByPrimaryKey(2)!.Id, copy.Id);
        Assert.NotEqual(first, copy.Id);
        copy["Name"] = "copy, renamed";
        table.Update(copy);
        Assert.Equal("1=original|2=copy, renamed", Names(table));

        // So is a row of a batch, and a row a caller made.
        var again = table.FindRowByPrimaryKey(1)!;
        again["Name"] = "batch copy";
        var made = new Row { ["Name"] = "made" };
        table.InsertRange(new[] { again, made });
        again["Name"] = "batch, renamed";
        table.Update(again);
        made["Name"] = "made, renamed";
        table.Update(made);
        Assert.Equal("1=original|2=copy, renamed|3=batch, renamed|4=made, renamed", Names(table));

        // A batch taken back gives its rows back as they came: the row read is row 1 again, with what it was given,
        // and the row made is in no table.
        var read = table.FindRowByPrimaryKey(1)!;
        read["Name"] = "again";
        var fresh = new Row { ["Name"] = "fresh" };
        Assert.Throws<BatchInsertException>(() => table.InsertRange(new[] { read, fresh, new Row { ["Name"] = new string('x', 30) } }));
        Assert.Equal(first, read.Id);
        Assert.Null(fresh.Id);
        Assert.False(fresh.ContainsKey("Id"));
        table.Update(read);
        Assert.Equal("1=again|2=copy, renamed|3=batch, renamed|4=made, renamed", Names(table));

        // And one refused on its own the same.
        var refused = table.FindRowByPrimaryKey(2)!;
        refused["Name"] = new string('y', 30);
        Assert.Throws<ColumnValueTooLongException>(() => table.Insert(refused));
        Assert.Equal(table.FindRowByPrimaryKey(2)!.Id, refused.Id);
        Assert.Equal(2, refused["Id"]);
    }

    // ── What a change taken back leaves of what the session knows ────────────

    [Fact]
    public void A_change_refused_or_taken_back_leaves_what_the_session_knows_of_the_file_as_it_was()
    {
        using (var db = Database.Create(_path))
        {
            var table = db.CreateTable("T", new[]
            {
                new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32), new ColumnDefinition("M", DataType.Memo),
            }, new[] { "Id" }, new[] { new IndexDefinition("ByK", "K") { IsUnique = true } });
            table.InsertRange(Enumerable.Range(1, 5000).Select(i => new Row { ["Id"] = i, ["K"] = i, ["M"] = i % 10 == 0 ? new string('m', 2000) : null }));
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            // Long values freed in this session: their room is noted for the next ones (FreedLvalPages).
            for (int i = 10; i <= 1000; i += 10) table.Delete(table.FindRowByPrimaryKey(i)!);

            int next = 100_000;
            double ReadsPerInsert(Action? before)
            {
                long reads = 0;
                for (int i = 0; i < 20; i++)
                {
                    before?.Invoke();
                    long at = db.File.ReadCount;
                    table.Insert(new Row { ["Id"] = next, ["K"] = next, ["M"] = new string('n', 1900) });
                    reads += db.File.ReadCount - at;
                    next++;
                }
                return reads / 20.0;
            }

            double plain = ReadsPerInsert(null);
            var refused = new Dictionary<string, double>
            {
                // Refused before anything is written: nothing of what the session knows changes.
                ["insert"] = ReadsPerInsert(() => Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 1, ["K"] = 2 }))),
                ["update"] = ReadsPerInsert(() =>
                {
                    var row = table.FindRowByPrimaryKey(2)!;
                    row["K"] = 3;
                    Assert.Throws<ConstraintViolationException>(() => table.Update(row));
                }),
                // Taken back after it wrote: the file is as it was, and so is what the session knows of it.
                ["delete"] = ReadsPerInsert(() =>
                {
                    var before = IndexChecks.AllPages(db);
                    db.File.WritesBeforeFailure = 2;
                    Assert.Throws<IOException>(() => table.Delete(table.FindRowByPrimaryKey(1010)!));
                    db.File.WritesBeforeFailure = null;
                    IndexChecks.AssertUnchanged(before, db);
                }),
            };
            foreach (var (what, reads) in refused)
                Assert.True(reads <= plain * 1.5 + 4, $"after a refused {what}, {reads:F1} pages read for each insert of a Memo; {plain:F1} without");

            Assert.NotNull(table.FindRowByPrimaryKey(1010));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 5000 - 100 + 4 * 20);
        }
    }

    // ── What keeping a change for undoing costs ──────────────────────────────

    [Fact]
    public void Keeping_a_change_for_undoing_reads_no_page_a_second_time()
    {
        Row Make(int i) => new()
        {
            ["Id"] = i, ["Doc"] = $"ABC.{i % 97:D4}.{i:D5}", ["Title"] = new string('t', 10 + i % 40), ["D"] = new DateTime(2020, 1, 1).AddDays(i),
            ["Notes"] = i % 3 == 0 ? new string('n', 300 + i % 1700) : null,
        };
        using (var db = Database.Create(_path))
        {
            db.CreateTable("T", new[]
            {
                new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Doc", DataType.Text) { MaxLength = 50 },
                new ColumnDefinition("Title", DataType.Text) { MaxLength = 255 }, new ColumnDefinition("D", DataType.DateTime),
                new ColumnDefinition("Notes", DataType.Memo),
            }, new[] { "Id" }, new[] { new IndexDefinition("ByDoc", "Doc") });
            db.GetTable("T").InsertRange(Enumerable.Range(1, 400).Select(Make));
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            // Each change its own scope, writing over pages the file had when it began: rows onto the table's last
            // page, long values onto pages with room, the definition, the index's pages, a row grown past its page.
            for (int i = 401; i <= 700; i++) table.Insert(Make(i));
            for (int i = 1; i <= 120; i++)
            {
                var row = table.FindRowByPrimaryKey(i * 5)!;
                row["Title"] = new string('u', i % 2 == 0 ? 255 : 12);
                if (i % 3 == 0) row["Doc"] = $"XYZ.{i:D5}";
                table.Update(row);
            }
            for (int i = 2; i <= 600; i += 12) table.Delete(table.FindRowByPrimaryKey(i)!);
            table.InsertRange(Enumerable.Range(701, 50).Select(Make));

            // A page a change writes over was read by it, or is one the file keeps: its image costs no read.
            Assert.Equal(0, db.File.UndoReadCount);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 750 - 50);
        }
    }

    // ── When a cursor is told ────────────────────────────────────────────────

    [Fact]
    public void A_cursor_is_told_of_a_batch_s_rows_once_the_batch_is_in_and_of_a_batch_taken_back_never()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByK", "K") });
        for (int i = 1; i <= 5; i++) table.Insert(new Row { ["Id"] = i, ["K"] = i * 10 });
        var cursor = table.CreateIndexCursor("ByK");
        Assert.True(cursor.MoveNext());

        // The batch's second row is refused (a duplicate key): the first was in the file for a moment, and is not told.
        Assert.Throws<BatchInsertException>(() => table.InsertRange(new[] { new Row { ["Id"] = 6, ["K"] = 5 }, new Row { ["Id"] = 1, ["K"] = 7 } }));
        Assert.Equal(0, cursor.TimesTold);

        // A batch that goes in is told, a row at a time, once it is in.
        table.InsertRange(new[] { new Row { ["Id"] = 6, ["K"] = 55 }, new Row { ["Id"] = 7, ["K"] = 7 } });
        Assert.Equal(2, cursor.TimesTold);
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, Rest(cursor));
    }

    private static List<int> Rest(Cursor cursor)
    {
        var ids = new List<int>();
        while (cursor.MoveNext()) ids.Add((int)cursor.Current!["Id"]!);
        return ids;
    }

    // ── What a range has come to ─────────────────────────────────────────────

    private static int IdOf(Cursor cursor) => (int)cursor.Current!["Id"]!;

    // The rows a cursor gives one way: the first, then what happens, then the rest.
    private static List<int> Give(Cursor cursor, bool forward, Action afterFirst)
    {
        var ids = new List<int>();
        Assert.True(forward ? cursor.MoveNext() : cursor.MovePrevious());
        ids.Add(IdOf(cursor));
        afterFirst();
        while (forward ? cursor.MoveNext() : cursor.MovePrevious()) ids.Add(IdOf(cursor));
        return ids;
    }

    [Fact]
    public void A_range_gives_a_row_an_update_moves_into_it_from_outside_it()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32) },
            new[] { "Id" }, new[] { new IndexDefinition("ByK", "K") });
        for (int i = 1; i <= 6; i++) table.Insert(new Row { ["Id"] = i, ["K"] = i * 10 });
        void SetK(int id, int k) { var row = table.FindRowByPrimaryKey(id)!; row["K"] = k; table.Update(row); }

        // Row 1, before the range's start, moved into it ahead of the cursor: the cursor never came to it.
        var cursor = table.CreateIndexCursor("ByK", new IndexRange { StartEntry = new object?[] { 30 }, EndEntry = new object?[] { 60 } });
        Assert.Equal(new[] { 3, 4, 1, 5, 6 }, Give(cursor, forward: true, () => SetK(1, 45)));
        SetK(1, 10);

        // Row 2, the start an exclusive range leaves out, moved to 55.
        cursor = table.CreateIndexCursor("ByK", new IndexRange { StartEntry = new object?[] { 20 }, IsStartInclusive = false, EndEntry = new object?[] { 60 } });
        Assert.Equal(new[] { 3, 4, 5, 2, 6 }, Give(cursor, forward: true, () => SetK(2, 55)));
        SetK(2, 20);

        // Backwards: row 6, after the range's end, moved to 15.
        cursor = table.CreateIndexCursor("ByK", new IndexRange { StartEntry = new object?[] { 10 }, EndEntry = new object?[] { 40 } });
        cursor.MoveAfterLast();
        Assert.Equal(new[] { 4, 3, 2, 6, 1 }, Give(cursor, forward: false, () => SetK(6, 15)));
    }

    [Fact]
    public void A_row_a_date_leaves_out_or_a_closest_lookup_looks_past_is_given_once_an_update_moves_it_in()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTime), new ColumnDefinition("L", DataType.Int32),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByD", "D"), new IndexDefinition("ByDL", "D", "L") });
        var day = new DateTime(2020, 1, 1);
        table.Insert(new Row { ["Id"] = 1, ["D"] = day, ["L"] = 3 });
        table.Insert(new Row { ["Id"] = 2, ["D"] = day, ["L"] = 6 });
        table.Insert(new Row { ["Id"] = 3, ["D"] = day.AddDays(1), ["L"] = 0 });
        table.Insert(new Row { ["Id"] = 4, ["D"] = day.AddDays(4), ["L"] = 0 });
        void Set(int id, string column, object value) { var row = table.FindRowByPrimaryKey(id)!; row[column] = value; table.Update(row); }

        // After 1 January, exclusive: rows 1 and 2 are passed, not come to; row 1 moved to 3 January is given.
        var cursor = table.CreateIndexCursor("ByD", new IndexRange { StartEntry = new object?[] { day }, IsStartInclusive = false });
        Assert.Equal(new[] { 3, 1, 4 }, Give(cursor, forward: true, () => Set(1, "D", day.AddDays(2))));
        Set(1, "D", day);

        // (1 January, 5) to (1 January, 9): row 1, (1 January, 3), is passed on the way to row 2, then moved to L 7.
        cursor = table.CreateIndexCursor("ByDL", new IndexRange { StartEntry = new object?[] { day, 5 }, EndEntry = new object?[] { day, 9 } });
        Assert.Equal(new[] { 2, 1 }, Give(cursor, forward: true, () => Set(1, "L", 7)));
        Set(1, "L", 3);

        // The closest row to (1 January, 5) is row 2: the lookup looks past row 1, which an update then moves ahead.
        cursor = table.CreateIndexCursor("ByDL");
        Assert.Equal(2, (int)cursor.FindClosestRowByEntry(day, 5)!["Id"]!);
        Set(1, "L", 7);
        var rest = new List<int>();
        while (cursor.MoveNext()) rest.Add(IdOf(cursor));
        Assert.Equal(new[] { 1, 3, 4 }, rest);
    }

    // ── A cursor's update of a row it read ───────────────────────────────────

    [Fact]
    public void A_cursor_s_update_of_a_row_read_from_the_table_assigns_what_the_row_was_given_as_Table_Update_does()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 50 },
            new ColumnDefinition("B", DataType.Text) { MaxLength = 50 }, new ColumnDefinition("C", DataType.Text) { MaxLength = 50 },
            new ColumnDefinition("M", DataType.Memo),
        }, "Id");
        table.Insert(new Row { ["Id"] = 1, ["A"] = "", ["B"] = "b", ["C"] = "c", ["M"] = new string('m', 4000) });
        table.Columns.First(c => c.Name == "A").Properties.Set("AllowZeroLength", false);
        table.Properties.Save();

        // The cursor's own row given back with B changed: A, which breaks its rule already, is not assigned.
        var cursor = table.CreateCursor();
        Assert.True(cursor.MoveNext());
        var current = cursor.Current!;
        current["B"] = "b2";
        cursor.UpdateCurrentRow(current);
        Assert.Equal("b2", table.FindRowByPrimaryKey(1)!["B"]);
        Assert.Equal("b2", cursor.Current!["B"]);

        // A row read before another update changed C: only B is written - not C's old value, not the Memo.
        var staleCursor = table.CreateCursor();
        Assert.True(staleCursor.MoveNext());
        var stale = staleCursor.Current!;
        var other = table.FindRowByPrimaryKey(1)!;
        other["C"] = "changed by another";
        table.Update(other);
        stale["B"] = "b3";
        long writes = db.File.WriteCount;
        staleCursor.UpdateCurrentRow(stale);
        Assert.Equal(1, db.File.WriteCount - writes);
        var now = table.FindRowByPrimaryKey(1)!;
        Assert.Equal("b3|changed by another|4000", $"{now["B"]}|{now["C"]}|{((string)now["M"]!).Length}");

        // Given back with nothing changed it writes nothing; a set of values of its own assigns every column it
        // names, and A given "" is refused.
        writes = db.File.WriteCount;
        staleCursor.UpdateCurrentRow(staleCursor.Current!);
        Assert.Equal(writes, db.File.WriteCount);
        Assert.Throws<ConstraintViolationException>(() => cursor.UpdateCurrentRow(new Row { ["A"] = "", ["B"] = "b4" }));
    }
}
