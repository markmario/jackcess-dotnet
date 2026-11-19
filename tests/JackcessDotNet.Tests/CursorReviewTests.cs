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

    // ── Rows written before a column was dropped ─────────────────────────────

    [Fact]
    public void Rows_written_before_a_column_was_dropped_are_updated_where_they_are_past_their_page_and_back_and_deleted()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
            new ColumnDefinition("B", DataType.Text) { MaxLength = 255 }, new ColumnDefinition("Notes", DataType.Memo),
            new ColumnDefinition("D", DataType.Text) { MaxLength = 255 }, new ColumnDefinition("N", DataType.Int32),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByA", "A") });
        for (int i = 1; i <= 30; i++)
            table.Insert(new Row { ["Id"] = i, ["A"] = Wide(i, 100), ["B"] = Wide(i + 1, 100), ["Notes"] = new string((char)('a' + i % 26), 2000), ["N"] = i });
        // Each row keeps the bytes it had for the two columns; the Memo's pages are given back.
        table.DropColumn("B");
        table.DropColumn("Notes");

        var expected = Enumerable.Range(1, 30).ToDictionary(i => i, i => (A: Wide(i, 100), D: (string?)null, N: i));
        void Set(int id, string a, string? d)
        {
            var row = table.FindRowByPrimaryKey(id)!;
            var was = row.Id;
            row["A"] = a;
            row["D"] = d;
            table.Update(row);
            Assert.Equal(was, table.FindRowByPrimaryKey(id)!.Id);
            expected[id] = (a, d, expected[id].N);
        }
        ushort SlotOf(int id)
        {
            var at = table.FindRowByPrimaryKey(id)!.Id!.Value;
            return ByteUtil.GetUShort(db.File.ReadPage(at.PageNumber), db.File.Format.OffsetDataRowTable + at.RowNumber * JetFormat.SizeRowEntry);
        }

        // A rewritten row has no bytes for the dropped columns, so each update below gives its page room: the
        // row that goes past its page's room goes first.
        Set(3, Wide(3, 255), Wide(4, 255));                 // past its page's room
        Assert.Equal(JetFormat.OverflowRowFlag, SlotOf(3) & 0xC000);
        Set(3, "home", null);                               // and back
        Assert.Equal(0, SlotOf(3) & 0xC000);
        Set(2, "x", null);                                  // smaller, where it is
        Set(5, Wide(5, 140), null);                         // larger
        Set(9, Wide(9, 255), Wide(10, 255));
        table.Delete(table.FindRowByPrimaryKey(7)!);
        expected.Remove(7);
        table.Insert(new Row { ["Id"] = 31, ["A"] = "after", ["N"] = 31 });
        expected[31] = ("after", null, 31);

        Assert.Equal(new[] { "Id", "A", "D", "N" }, table.Columns.Select(c => c.Name));
        var rows = table.Rows.ToDictionary(r => (int)r["Id"]!);
        Assert.Equal(expected.Keys.OrderBy(k => k), rows.Keys.OrderBy(k => k));
        foreach (var pair in expected)
        {
            Assert.Equal(pair.Value.A, rows[pair.Key]["A"]);
            Assert.Equal(pair.Value.D, rows[pair.Key]["D"]);
            Assert.Equal(pair.Value.N, rows[pair.Key]["N"]);
            Assert.Equal(4, rows[pair.Key].Count);
        }
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 30);
    }

    // ── Tables whose definition was not read whole ───────────────────────────

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Fact]
    public void A_row_of_a_table_whose_columns_were_not_all_read_is_neither_updated_nor_deleted()
    {
        // The definition's last column name runs past its page, as only a damaged one's does. The table has no
        // index: nothing but its columns says its rows are not to be changed.
        using (var db = Database.Create(_path))
        {
            var table = db.CreateTable("Cut", new[]
            {
                new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo),
                new ColumnDefinition("LastColumnOfCut", DataType.Int32),
            });
            table.Insert(new Row { ["Id"] = 1, ["Notes"] = new string('n', 3000), ["LastColumnOfCut"] = 7 });
            table.Insert(new Row { ["Id"] = 2, ["Notes"] = "short", ["LastColumnOfCut"] = 8 });
            int tdef = table.Definition.TdefPageNumber;
            byte[] page = db.File.ReadPage(tdef);
            int at = IndexOf(page, System.Text.Encoding.Unicode.GetBytes("LastColumnOfCut"));
            Assert.True(at > 2, "the column's name was not found in its definition");
            page[at - 2] = 0xFF;
            page[at - 1] = 0x7F;
            db.File.WritePage(tdef, page);
        }

        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Cut");
            Assert.True(table.Definition.HasUnreadColumns);
            Assert.False(table.Definition.HasUnreadIndexes);
            Assert.Equal(new object?[] { 1, 2 }, table.Rows.Select(r => r["Id"]));

            var row = table.Rows.First();
            row["Id"] = 10;
            Assert.Throws<NotSupportedException>(() => table.Update(row));
            Assert.Throws<NotSupportedException>(() => table.Delete(table.Rows.First()));
            var cursor = table.CreateCursor();
            Assert.True(cursor.MoveNext());
            Assert.Throws<NotSupportedException>(() => cursor.UpdateCurrentRow(new Row { ["Id"] = 10 }));
            Assert.Throws<NotSupportedException>(() => cursor.DeleteCurrentRow());
            Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["Id"] = 3 }));
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_row_of_a_table_whose_definition_goes_on_to_a_page_not_its_own_is_neither_updated_nor_deleted()
    {
        string name = MultiPageDefinitionTests.LongName(0);
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var columns = new List<ColumnDefinition> { new("Id", DataType.Int32) };
            columns.AddRange(Enumerable.Range(0, 120).Select(n => new ColumnDefinition(MultiPageDefinitionTests.LongName(n), DataType.Int32)));
            var table = db.CreateTable("Long names", columns, "Id");
            for (int i = 1; i <= 3; i++) table.Insert(new Row { ["Id"] = i, [name] = 10 * i });
            var pages = MultiPageDefinitionTests.PagesOf(db, "Long names");
            byte[] page = db.File.ReadPage(pages[1]);
            ByteUtil.PutInt(page, 4, db.File.PageCount + 100);       // its next page is one the file has not
            db.File.WritePage(pages[1], page);
        }

        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Long names");
            Assert.True(table.Definition.HasUnreadColumns);
            var row = table.Rows.First();
            row[name] = 99;
            Assert.Throws<NotSupportedException>(() => table.Update(row));
            Assert.Throws<NotSupportedException>(() => table.Delete(table.Rows.First()));
            var cursor = table.CreateCursor();
            Assert.True(cursor.MoveNext());
            Assert.Throws<NotSupportedException>(() => cursor.UpdateCurrentRow(new Row { [name] = 5 }));
            Assert.Throws<NotSupportedException>(() => cursor.DeleteCurrentRow());
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
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

    // ── Keys that change around a cursor ─────────────────────────────────────

    /// <summary>Ten rows, Id 1 to 10, K ten times Id, with an index on K.</summary>
    private static Table Ten(Database db, bool ignoreNulls = false)
    {
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByK", "K") { IgnoreNulls = ignoreNulls } });
        for (int i = 1; i <= 10; i++) table.Insert(new Row { ["Id"] = i, ["K"] = i * 10 });
        return table;
    }

    private static void SetK(Table table, int id, int? k)
    {
        var row = table.FindRowByPrimaryKey(id)!;
        row["K"] = k;
        table.Update(row);
    }

    private static List<int> Rest(IndexCursor cursor, bool forward)
    {
        var ids = new List<int>();
        while (forward ? cursor.MoveNext() : cursor.MovePrevious())
        {
            ids.Add((int)cursor.Current!["Id"]!);
            Assert.True(ids.Count <= 30, "the cursor does not come to an end");
        }
        return ids;
    }

    private static IndexCursor At(Table table, int rows, bool forward)
    {
        var cursor = table.CreateIndexCursor("ByK");
        if (!forward) cursor.MoveAfterLast();
        for (int i = 0; i < rows; i++) Assert.True(forward ? cursor.MoveNext() : cursor.MovePrevious());
        return cursor;
    }

    [Fact]
    public void A_row_not_yet_given_is_given_wherever_its_key_has_been_meanwhile()
    {
        using var db = Database.Create(_path);
        var table = Ten(db);

        // Behind the cursor and back ahead of it, to the key it had.
        var cursor = At(table, 3, forward: true);
        SetK(table, 7, 5);
        SetK(table, 7, 70);
        Assert.Equal(new[] { 4, 5, 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));

        // The same going backwards: rows 10, 9, 8 given; row 4 past the cursor and back.
        cursor = At(table, 3, forward: false);
        SetK(table, 4, 95);
        SetK(table, 4, 40);
        Assert.Equal(new[] { 7, 6, 5, 4, 3, 2, 1 }, Rest(cursor, forward: false));

        // Behind the cursor, then ahead of it to another key.
        cursor = At(table, 5, forward: true);
        SetK(table, 8, 15);
        SetK(table, 8, 85);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
        SetK(table, 8, 80);

        // Brought behind the cursor, and the cursor then turned back: it comes to the row, which it never gave.
        cursor = At(table, 5, forward: true);
        SetK(table, 9, 25);
        Assert.Equal(new[] { 4, 3, 9, 2, 1 }, Rest(cursor, forward: false));
        SetK(table, 9, 90);

        // Inserted behind the cursor, and moved ahead of it.
        cursor = At(table, 5, forward: true);
        table.Insert(new Row { ["Id"] = 11, ["K"] = 15 });
        SetK(table, 11, 65);
        Assert.Equal(new[] { 6, 11, 7, 8, 9, 10 }, Rest(cursor, forward: true));
    }

    [Fact]
    public void A_row_already_given_is_not_given_again_when_its_key_takes_it_ahead_of_the_cursor()
    {
        using var db = Database.Create(_path);
        var table = Ten(db);

        // By Table.Update of a row that is not the cursor's current one.
        var cursor = At(table, 5, forward: true);
        SetK(table, 2, 75);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
        SetK(table, 2, 20);

        // Twice over, the second time while already ahead.
        cursor = At(table, 5, forward: true);
        SetK(table, 2, 75);
        SetK(table, 2, 95);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
        SetK(table, 2, 20);

        // Going backwards, a given row lowered past the cursor.
        cursor = At(table, 4, forward: false);
        SetK(table, 9, 15);
        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1 }, Rest(cursor, forward: false));
        SetK(table, 9, 90);

        // Two cursors on the one index: each row's key raised through one, as the other goes through them all.
        var reader = At(table, 0, forward: true);
        var writer = table.CreateIndexCursor("ByK");
        var given = new List<int>();
        while (reader.MoveNext())
        {
            int id = (int)reader.Current!["Id"]!;
            given.Add(id);
            Assert.True(given.Count <= 30);
            Assert.NotNull(writer.FindRowByEntry(reader.Current["K"]));
            writer.UpdateCurrentRow(new Row { ["K"] = (int)reader.Current["K"]! + 1000 });
        }
        Assert.Equal(Enumerable.Range(1, 10), given);
    }

    [Fact]
    public void A_row_whose_entry_leaves_an_index_that_ignores_nulls_and_comes_back_is_given_once()
    {
        using var db = Database.Create(_path);
        var table = Ten(db, ignoreNulls: true);

        var cursor = At(table, 5, forward: true);
        SetK(table, 2, null);      // a row given: its entry leaves the index...
        SetK(table, 2, 75);        // ...and comes back ahead of the cursor
        SetK(table, 8, null);      // a row not given leaves and comes back
        SetK(table, 8, 85);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
    }

    [Fact]
    public void A_cursor_that_turns_back_gives_again_the_rows_it_passed_but_not_the_row_it_is_on()
    {
        using var db = Database.Create(_path);
        var table = Ten(db);

        // The row the cursor is on, moved behind it, is not given going back; turned again at the beginning, the
        // cursor gives every row.
        var cursor = At(table, 5, forward: true);
        cursor.UpdateCurrentRow(new Row { ["K"] = 15 });
        Assert.Equal(new[] { 4, 3, 2, 1 }, Rest(cursor, forward: false));
        Assert.Equal(new[] { 1, 5, 2, 3, 4, 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
        SetK(table, 5, 50);

        // A row given and moved ahead of the cursor, which then turns back and forward again: going forward
        // again, the cursor gives the rows ahead of it, that one among them.
        cursor = At(table, 5, forward: true);
        SetK(table, 2, 75);
        Assert.Equal(new[] { 4, 3, 1 }, Rest(cursor, forward: false));
        Assert.Equal(new[] { 1, 3, 4, 5, 6, 7, 2, 8, 9, 10 }, Rest(cursor, forward: true));
    }

    [Fact]
    public void A_savepoint_keeps_what_the_cursor_had_given_whatever_keys_change_before_it_is_restored()
    {
        using var db = Database.Create(_path);
        var table = Ten(db);

        // A given row's key is raised after the savepoint was taken: restored, the cursor does not give it again.
        var cursor = At(table, 5, forward: true);
        var saved = cursor.GetSavepoint();
        Assert.True(cursor.MoveNext());
        SetK(table, 2, 75);
        Assert.True(cursor.MoveNext());
        cursor.RestoreSavepoint(saved);
        Assert.Equal(5, cursor.Current!["Id"]);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
        cursor.RestoreSavepoint(saved);                                     // and a second time the same
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
        SetK(table, 2, 20);

        // Raised before the savepoint was taken.
        cursor = At(table, 5, forward: true);
        SetK(table, 2, 75);
        saved = cursor.GetSavepoint();
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
        cursor.RestoreSavepoint(saved);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Rest(cursor, forward: true));
        SetK(table, 2, 20);

        // A row not given when the savepoint was taken, moved behind where the cursor then was: given once the
        // savepoint is restored.
        cursor = At(table, 3, forward: true);
        saved = cursor.GetSavepoint();
        cursor.MoveNext();
        cursor.MoveNext();
        SetK(table, 7, 45);
        cursor.RestoreSavepoint(saved);
        Assert.Equal(new[] { 4, 7, 5, 6, 8, 9, 10 }, Rest(cursor, forward: true));
    }

    // ── Dates in a range's entries ───────────────────────────────────────────

    private static double Next(double number, int steps = 1)
        => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(number) + steps);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_date_anywhere_in_a_range_s_entry_takes_in_every_number_that_reads_as_it(bool descending)
    {
        using var db = Database.Create(_path);
        var date = new DateTime(2024, 5, 6, 7, 8, 9);
        double d1 = date.ToOADate(), d2 = Next(d1, 3);
        Assert.Equal(date, ValueMatch.ReadsAs(d2));
        var table = Dated(db, descending,
            (1, d1, 3), (1, d1, 7), (1, d2, 4), (1, d2, 8), (1, d1 - 1, 6), (1, d1 + 1, 6), (2, d2, 6), (2, d1, 11));

        int[] Ls(IndexCursor cursor) => cursor.Select(r => (int)r["L"]!).OrderBy(l => l).ToArray();

        // (Date, Long): the rows whose date reads as it and whose Long is between.
        Assert.Equal(new[] { 6, 7, 8 }, Ls(table.CreateIndexCursor("ByDL", new IndexRange { StartEntry = E(date, 5), EndEntry = E(date, 9) })));
        Assert.Equal(new[] { 6, 7 }, Ls(table.CreateIndexCursor("ByDL", new IndexRange { StartEntry = E(date, 5), EndEntry = E(date, 8), IsEndInclusive = false })));
        Assert.Equal(new[] { 7, 8, 11 }, Ls(table.CreateIndexCursor("ByDL", new IndexRange { StartEntry = E(date, 6), IsStartInclusive = false, EndEntry = E(date) })));
        Assert.Equal(new[] { 3, 4, 6, 7, 8, 11 }, Ls(table.CreateIndexCursor("ByDL", new IndexRange { StartEntry = E(date), EndEntry = E(date) })));
        // (Long, Date, Long).
        Assert.Equal(new[] { 4, 7, 8 }, Ls(table.CreateIndexCursor("ByGDL", new IndexRange { StartEntry = E(1, date, 4), EndEntry = E(1, date, 8) })));
        Assert.Equal(new[] { 6, 11 }, Ls(table.CreateIndexCursor("ByGDL", new IndexRange { StartEntry = E(2, date), EndEntry = E(2, date, 50) })));

        // The closest row to an entry with the date in it: the row with those values when there is one.
        Assert.Equal(7, table.CreateIndexCursor("ByDL").FindClosestRowByEntry(date, 7)!["L"]);
        Assert.Equal(8, table.CreateIndexCursor("ByDL").FindClosestRowByEntry(date, 8)!["L"]);
        Assert.Equal(11, table.CreateIndexCursor("ByGDL").FindClosestRowByEntry(2, date, 11)!["L"]);
        Assert.Equal(4, table.CreateIndexCursor("ByGDL").FindClosestRowByEntry(1, date, 4)!["L"]);
        // None has (date, 5): the first row after it in the index's order, which is by the numbers stored first and
        // by L within one number. Ascending, (date, 7) stored as d1 comes before (date, 6) stored as d2, and is the
        // one; descending, d2 comes first, and (date, 6) is.
        var closest = table.CreateIndexCursor("ByDL").FindClosestRowByEntry(date, 5)!;
        Assert.Equal(descending ? 6 : 1, closest["Id"]);
        Assert.Equal(descending ? 6 : 7, closest["L"]);
    }

    [Fact]
    public void A_time_stored_as_a_negative_number_is_in_the_ranges_and_the_closest_row_its_time_is_in()
    {
        using var db = Database.Create(_path);
        var day = new DateTime(1899, 12, 30);
        var table = Dated(db, false, (1, -0.25, 1), (1, 0.25, 2), (1, 0.5, 3), (1, -0.75, 4), (1, 2.25, 5), (1, -2.25, 6));
        Assert.Equal(day.AddHours(6), table.FindRowByPrimaryKey(0)!["D"]);
        Assert.Equal(day.AddHours(18), table.FindRowByPrimaryKey(3)!["D"]);

        int[] Ls(IndexRange range) => table.CreateIndexCursor("ByD", range).Select(r => (int)r["L"]!).OrderBy(l => l).ToArray();

        Assert.Equal(new[] { 1, 2 }, Ls(new IndexRange { StartEntry = E(day.AddHours(6)), EndEntry = E(day.AddHours(6)) }));
        Assert.Equal(new[] { 1, 2 }, Ls(new IndexRange { StartEntry = E(day.AddHours(5)), EndEntry = E(day.AddHours(7)) }));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Ls(new IndexRange { StartEntry = E(day.AddHours(5)), EndEntry = E(day.AddHours(19)) }));
        Assert.Equal(new[] { 3, 4, 5 }, Ls(new IndexRange { StartEntry = E(day.AddHours(12)), EndEntry = E(day.AddDays(3)) }));
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, Ls(new IndexRange { StartEntry = E(day.AddDays(-3)), EndEntry = E(day.AddDays(1)) }));
        Assert.Equal(new[] { 3, 4, 5 }, Ls(new IndexRange { StartEntry = E(day.AddHours(6)), IsStartInclusive = false, EndEntry = E(day.AddDays(2).AddHours(6)) }));

        // A number is put against the numbers, as Access's BETWEEN puts a literal against them.
        Assert.Equal(new[] { 2 }, Ls(new IndexRange { StartEntry = E(5 / 24.0), EndEntry = E(7 / 24.0) }));

        Assert.Equal(4, table.CreateIndexCursor("ByD").FindClosestRowByEntry(day.AddHours(18))?["L"]);
        Assert.Equal(1, table.CreateIndexCursor("ByD").FindClosestRowByEntry(day.AddHours(6))?["L"]);
        table.Delete(table.FindRowByPrimaryKey(1)!);
        table.Delete(table.FindRowByPrimaryKey(3)!);
        Assert.Equal(1, table.CreateIndexCursor("ByD").FindClosestRowByEntry(day.AddHours(6))?["L"]);
        Assert.Equal(1, table.CreateIndexCursor("ByD").FindClosestRowByEntry(day.AddHours(5))?["L"]);
    }

    [Fact]
    public void A_range_of_dates_before_1900_has_the_rows_whose_dates_are_in_it_in_the_index_s_order()
    {
        using var db = Database.Create(_path);
        // 28 December 1899 at 18:00 and 06:00; 29 December at 18:00, 06:00 and midnight: before 30 December 1899 a
        // day's times run back from its midnight.
        var table = Dated(db, false, (1, -2.75, 1), (1, -2.25, 2), (1, -1.75, 3), (1, -1.25, 4), (1, -1.0, 5));
        var dec28 = new DateTime(1899, 12, 28);
        var dec29 = dec28.AddDays(1);
        Assert.Equal(dec28.AddHours(18), table.FindRowByPrimaryKey(0)!["D"]);

        int[] Ls(IndexRange range) => table.CreateIndexCursor("ByD", range).Select(r => (int)r["L"]!).ToArray();

        Assert.Equal(new[] { 1, 5 }, Ls(new IndexRange { StartEntry = E(dec28.AddHours(12)), EndEntry = E(dec29.AddHours(3)) }));
        Assert.Equal(new[] { 4, 5 }, Ls(new IndexRange { StartEntry = E(dec29), EndEntry = E(dec29.AddHours(12)) }));
        Assert.Equal(new[] { 3, 4 }, Ls(new IndexRange { StartEntry = E(dec29.AddHours(6)), EndEntry = E(dec29.AddHours(18)) }));
        // Numbers, as Access's BETWEEN #12/28/1899 12:00# AND #12/29/1899 03:00# takes them.
        Assert.Equal(new[] { 2, 3, 4 }, Ls(new IndexRange { StartEntry = E(-2.5), EndEntry = E(-1.125) }));

        Assert.Equal(4, table.CreateIndexCursor("ByD").FindClosestRowByEntry(dec29.AddHours(6))!["L"]);
        // None at five: the first after it in the index's order, which is six in the evening.
        Assert.Equal(3, table.CreateIndexCursor("ByD").FindClosestRowByEntry(dec29.AddHours(5))!["L"]);
    }
}
