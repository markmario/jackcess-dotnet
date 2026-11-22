using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What a cursor does besides move (#29): updates and deletes the row it is on, finds rows by their values,
/// reads one column, reads some columns, goes backwards, and comes back to a place it noted. Each case runs over
/// a cursor in the table's own order and over one in an index's.
/// </summary>
public sealed class CursorOperationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cursor_ops_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private const int Rows = 400;

    private Table Create(Database db)
    {
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("Grp", DataType.Int32),
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 40 },
            new ColumnDefinition("N", DataType.Int32),
            new ColumnDefinition("Notes", DataType.Memo),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByGrp", "Grp"), new IndexDefinition("ByName", "Name") { IsUnique = true } });
        // Not in the order of any index.
        for (int i = 0; i < Rows; i++)
        {
            int id = (i * 37) % Rows + 1;
            table.Insert(new Row { ["Id"] = id, ["Grp"] = id % 7, ["Name"] = $"name {id:000}", ["N"] = 0, ["Notes"] = "notes " + id });
        }
        return table;
    }

    public static IEnumerable<object[]> Orders() => new[] { new object[] { "table" }, new object[] { "PrimaryKey" }, new object[] { "ByGrp" } };

    private static Cursor CursorOver(Table table, string order) => order == "table" ? table.CreateCursor() : table.CreateIndexCursor(order);

    [Theory]
    [MemberData(nameof(Orders))]
    public void Every_row_is_updated_once_through_a_cursor(string order)
    {
        using (var db = Database.Create(_path))
        {
            var table  = Create(db);
            var cursor = CursorOver(table, order);
            int updated = 0;
            while (cursor.MoveNext())
            {
                int n = (int)cursor.Current!["N"]!;
                var was = cursor.Current.Id;
                cursor.UpdateCurrentRow(new Row { ["N"] = n + 1, ["Notes"] = new string('x', 300) });
                Assert.Equal(n + 1, cursor.Current!["N"]);            // the row as it is afterwards
                Assert.Equal(was, cursor.Current.Id);                  // where it was
                updated++;
            }
            Assert.Equal(Rows, updated);
            Assert.True(cursor.IsAfterLast);
            Assert.All(table.Rows, r => Assert.Equal(1, r["N"]));

            // Put before the first row again, the cursor goes through every row again, wherever each now is.
            cursor.MoveBeforeFirst();
            Assert.Equal(Rows, cursor.Count());
            Assert.Equal(Rows, cursor.EnumerateBackward().Count());
        }

        using (var db = Database.Open(_path))
        {
            Assert.Equal(Rows, db.GetTable("T").RowCount);
            Assert.All(db.GetTable("T").Rows, r => Assert.Equal(new string('x', 300), r["Notes"]));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", Rows);
        }
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void A_row_a_cursor_updated_is_found_where_it_now_is_once_the_cursor_goes_back_or_is_restored(string order)
    {
        using var db = Database.Create(_path);
        var table  = Create(db);
        var cursor = CursorOver(table, order);
        var ids    = CursorOver(table, order).Select(r => (int)r["Id"]!).ToList();

        // The first row is updated, and the cursor goes on to the end without coming to it again...
        Assert.True(cursor.MoveNext());
        cursor.UpdateCurrentRow(new Row { ["N"] = 1 });
        int forwards = 1;
        while (cursor.MoveNext()) forwards++;
        Assert.Equal(Rows, forwards);

        // ...and back through every row, the updated one among them, where it now is.
        var back = new List<int>();
        while (cursor.MovePrevious()) back.Add((int)cursor.Current!["Id"]!);
        Assert.Equal(ids.OrderBy(i => i), back.OrderBy(i => i));

        // A row updated after a savepoint was taken is there when the cursor is brought back and goes on.
        cursor.MoveBeforeFirst();
        for (int i = 0; i < 10; i++) cursor.MoveNext();
        var at10 = cursor.GetSavepoint();
        for (int i = 0; i < 10; i++) cursor.MoveNext();
        int updated = (int)cursor.Current!["Id"]!;
        cursor.UpdateCurrentRow(new Row { ["N"] = 2 });
        cursor.RestoreSavepoint(at10);
        var onward = new List<int>();
        while (cursor.MoveNext()) onward.Add((int)cursor.Current!["Id"]!);
        Assert.Single(onward, id => id == updated);
        Assert.Equal(onward.Count, onward.Distinct().Count());
    }

    [Fact]
    public void A_search_that_cannot_read_a_row_leaves_the_cursor_as_it_was()
    {
        using var db = Database.Create(_path);
        var table  = Create(db);
        var cursor = table.CreateCursor();
        for (int i = 0; i < 5; i++) cursor.MoveNext();
        var on = cursor.Current!;

        // A row further on is made unreadable: its slot says its values are on a page the file has not.
        var damaged = table.Rows.Skip(50).First().Id!.Value;
        byte[] page = db.File.ReadPage(damaged.PageNumber);
        int slot  = db.File.Format.OffsetDataRowTable + damaged.RowNumber * 2;
        int start = (page[slot] | page[slot + 1] << 8) & 0x1FFF;
        page[slot + 1] |= 0x40;
        page[start] = 0; page[start + 1] = 0xFF; page[start + 2] = 0xFF; page[start + 3] = 0x7F;
        db.File.WritePage(damaged.PageNumber, page);

        Assert.Throws<InvalidDataException>(() => cursor.FindRow("Name", "nobody"));
        Assert.Same(on, cursor.Current);
        Assert.Equal(5, on.Count);
        Assert.Throws<InvalidDataException>(() => cursor.FindNextRow("Name", "nobody"));
        Assert.Same(on, cursor.Current);
        Assert.True(cursor.MoveNext());
        Assert.NotEqual(on.Id, cursor.Current!.Id);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void A_search_that_finds_a_row_it_cannot_read_whole_leaves_the_cursor_as_it_was(string order)
    {
        using var db = Database.Create(_path);
        var table = Create(db);
        // A row whose Memo is on a page of its own, and is the only thing wrong with it.
        var row = table.FindRowByPrimaryKey(77)!;
        row["Notes"] = new string('m', 3000);
        table.Update(row);

        // The Memo's reference in the row - [row:1][page:3] after its length - is made to name a page the file has not.
        var id = row.Id!.Value;
        byte[] page = db.File.ReadPage(id.PageNumber);
        var format  = db.File.Format;
        int slot    = format.OffsetDataRowTable + id.RowNumber * 2;
        int start   = (page[slot] | page[slot + 1] << 8) & 0x1FFF;
        int end     = id.RowNumber == 0 ? format.PageSize : (page[slot - 2] | page[slot - 1] << 8) & 0x1FFF;
        byte[] rowBytes = page.Skip(start).Take(end - start).ToArray();
        var reference = new RowDecoder(format, table.Columns).GetOtherPageLvRefs(rowBytes).Single();
        int at = -1;
        for (int i = start + 4; i + 4 <= end && at < 0; i++)
            if (page[i] == reference.lvalRow && (page[i + 1] | page[i + 2] << 8 | page[i + 3] << 16) == reference.lvalPage
                && (BitConverter.ToInt32(page, i - 4) & 0x3FFFFFFF) == 6000)
                at = i;
        Assert.True(at > 0, "the Memo's reference was not found in its row");
        page[at + 1] = 0xFF; page[at + 2] = 0xFF; page[at + 3] = 0x7F;
        db.File.WritePage(id.PageNumber, page);

        // The row cannot be read whole, and the exception that says so is the one any reader of it gets.
        var damaged = Record.Exception(() => table.Rows.ToList());
        Assert.NotNull(damaged);

        var cursor = CursorOver(table, order);
        for (int i = 0; i < 5; i++) cursor.MoveNext();
        var on   = cursor.Current!;
        var next = CursorOver(table, order).Skip(5).First()["Id"];

        // The search reads Id alone and finds the row; reading it with every column is what fails.
        var thrown = Record.Exception(() => cursor.FindRow("Id", 77));
        Assert.IsType(damaged!.GetType(), thrown);
        Assert.Same(on, cursor.Current);
        Assert.Equal(5, on.Count);
        thrown = Record.Exception(() => cursor.FindNextRow(new Row { ["Id"] = 77 }));
        Assert.IsType(damaged.GetType(), thrown);
        Assert.Same(on, cursor.Current);
        Assert.True(cursor.MoveNext());
        Assert.Equal(next, cursor.Current!["Id"]);

        // A cursor that does not read the damaged column finds the row.
        cursor.ColumnNames = new[] { "Id", "Name" };
        Assert.Equal("name 077", cursor.FindRow("Id", 77)!["Name"]);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void Rows_are_deleted_through_a_cursor_going_either_way(string order)
    {
        using (var db = Database.Create(_path))
        {
            var table  = Create(db);
            var cursor = CursorOver(table, order);

            // Forwards: every row whose Id is even.
            while (cursor.MoveNext())
            {
                if ((int)cursor.Current!["Id"]! % 2 != 0) continue;
                cursor.DeleteCurrentRow();
                Assert.Null(cursor.Current);
                Assert.Throws<InvalidOperationException>(() => cursor.DeleteCurrentRow());
                Assert.Throws<InvalidOperationException>(() => cursor.UpdateCurrentRow(new Row { ["N"] = 1 }));
                Assert.Throws<InvalidOperationException>(() => cursor.GetCurrentRowValue("N"));
            }
            Assert.Equal(Rows / 2, table.RowCount);

            // Backwards: every row whose Id is a multiple of 3.
            while (cursor.MovePrevious())
                if ((int)cursor.Current!["Id"]! % 3 == 0) cursor.DeleteCurrentRow();
            Assert.True(cursor.IsBeforeFirst);
        }

        using (var db = Database.Open(_path))
        {
            var expected = Enumerable.Range(1, Rows).Where(i => i % 2 != 0 && i % 3 != 0).ToList();
            Assert.Equal(expected, db.GetTable("T").Rows.Select(r => (int)r["Id"]!).OrderBy(i => i));
            Assert.Equal(expected.Count, db.GetTable("T").RowCount);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expected.Count);
        }
    }

    [Fact]
    public void A_cursor_s_update_keeps_the_rules_the_table_s_update_keeps()
    {
        using var db = Database.Create(_path);
        var table = Create(db);
        var child = db.CreateTable("Child", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Parent", DataType.Int32),
        }, "Id");
        for (int i = 1; i <= 10; i++) child.Insert(new Row { ["Id"] = i, ["Parent"] = (i + 1) / 2 });
        db.CreateRelationship(new RelationshipDefinition("T", "Id", "Child", "Parent") { HasReferentialIntegrity = true, CascadeUpdates = true });
        db.EnforceForeignKeys = true;

        var cursor = table.CreateIndexCursor();
        Assert.NotNull(cursor.FindRowByKey(2));
        var before = IndexChecks.AllPages(db);

        // A duplicate in a unique index, a null in the key, text too long, a row that rows refer to: each refused whole.
        Assert.Throws<ConstraintViolationException>(() => cursor.UpdateCurrentRow(new Row { ["Name"] = "name 003" }));
        Assert.Throws<ConstraintViolationException>(() => cursor.UpdateCurrentRow(new Row { ["Id"] = 3 }));
        Assert.Throws<ConstraintViolationException>(() => cursor.UpdateCurrentRow(new Row { ["Id"] = null }));
        Assert.Throws<ColumnValueTooLongException>(() => cursor.UpdateCurrentRow(new Row { ["Name"] = new string('x', 41) }));
        Assert.Throws<ConstraintViolationException>(() => cursor.DeleteCurrentRow());
        Assert.Throws<ArgumentNullException>(() => cursor.UpdateCurrentRow(null!));
        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(2, cursor.Current!["Id"]);

        // A change of key cascades.
        cursor.UpdateCurrentRow(new Row { ["Id"] = 1000 });
        Assert.Equal(1000, cursor.Current!["Id"]);
        Assert.Equal(new[] { 3, 4 }, child.Rows.Where(r => (int)r["Parent"]! == 1000).Select(r => (int)r["Id"]!).OrderBy(i => i));
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", Rows);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", 10);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void Rows_are_found_by_a_column_s_value_first_and_next(string order)
    {
        using var db = Database.Create(_path);
        var table  = Create(db);
        var cursor = CursorOver(table, order);

        // The rows of group 3, in the cursor's own order.
        var expected = CursorOver(table, order).Where(r => (int)r["Grp"]! == 3).Select(r => (int)r["Id"]!).ToList();
        Assert.Equal(Rows / 7 + (Rows % 7 >= 3 ? 1 : 0), expected.Count);

        var found = new List<int>();
        for (var row = cursor.FindRow("Grp", 3); row is not null; row = cursor.FindNextRow("grp", 3L))
        {
            Assert.Same(row, cursor.Current);
            Assert.Equal(5, row.Count);                       // every column, whatever was read to find it
            found.Add((int)row["Id"]!);
        }
        Assert.Equal(expected, found);

        // With no further row the cursor stays on the last one found.
        Assert.Equal(expected[expected.Count - 1], cursor.Current!["Id"]);
        Assert.Null(cursor.FindNextRow("Grp", 3));
        Assert.Equal(expected[expected.Count - 1], cursor.Current!["Id"]);

        // A pattern of several columns.
        var one = cursor.FindRow(new Row { ["Grp"] = 3, ["Name"] = "name 010" });
        Assert.Equal(10, one!["Id"]);
        Assert.Null(cursor.FindNextRow(new Row { ["Grp"] = 3, ["Name"] = "name 010" }));
        Assert.Equal(10, cursor.Current!["Id"]);             // where it was

        // No such row: the cursor is left where it was, on a row or off one.
        Assert.Null(cursor.FindRow("Name", "nobody"));
        Assert.Null(cursor.FindRow(new Row { ["Grp"] = 4, ["Name"] = "name 010" }));
        Assert.Equal(10, cursor.Current!["Id"]);
        cursor.MoveBeforeFirst();
        Assert.Null(cursor.FindRow("Name", "nobody"));
        Assert.True(cursor.IsBeforeFirst);
        Assert.Null(cursor.Current);

        // Text is matched as its sort order has it - without regard to case - and a null never matches.
        Assert.Equal(10, cursor.FindRow("Name", "NAME 010")!["Id"]);
        Assert.Null(cursor.FindRow("Name", "name 10"));
        Assert.Null(cursor.FindRow("Notes", null));
        Assert.Null(cursor.FindRow(new Row { ["Grp"] = 3, ["Notes"] = null }));

        // A column the table does not have, and a pattern with nothing in it, are the caller's mistake.
        Assert.Equal("columnName", Assert.Throws<ArgumentException>(() => cursor.FindRow("Nope", 1)).ParamName);
        Assert.Throws<ArgumentException>(() => cursor.FindNextRow("Nope", 1));
        Assert.Throws<ArgumentException>(() => cursor.FindRow(new Row { ["Nope"] = 1 }));
        Assert.Throws<ArgumentException>(() => cursor.FindRow(new Row()));
        Assert.Throws<ArgumentNullException>(() => cursor.FindRow((IReadOnlyDictionary<string, object?>)null!));
        Assert.Throws<ArgumentNullException>(() => cursor.FindRow(null!, 1));
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void One_column_of_the_current_row_is_read_and_a_cursor_reads_the_columns_it_is_given(string order)
    {
        using var db = Database.Create(_path);
        var table  = Create(db);
        var cursor = CursorOver(table, order);

        Assert.Throws<InvalidOperationException>(() => cursor.GetCurrentRowValue("Name"));   // on no row yet
        Assert.NotNull(cursor.FindRow("Id", 42));
        Assert.Equal("name 042", cursor.GetCurrentRowValue("name"));
        Assert.Equal("notes 42", cursor.GetCurrentRowValue("Notes"));
        Assert.Equal("columnName", Assert.Throws<ArgumentException>(() => cursor.GetCurrentRowValue("Nope")).ParamName);

        // It is read from the file, not from the row the cursor holds.
        cursor.Current!["Name"] = "changed in memory";
        Assert.Equal("name 042", cursor.GetCurrentRowValue("Name"));
        cursor.UpdateCurrentRow(new Row { ["Name"] = "written" });
        Assert.Equal("written", cursor.GetCurrentRowValue("Name"));

        // The long value is not read for a column that is not it.
        long reads = db.File.ReadCount;
        cursor.GetCurrentRowValue("N");
        Assert.True(db.File.ReadCount - reads <= 1, $"{db.File.ReadCount - reads} pages read for one fixed column");

        // Some of the columns: in the table's order whatever order they are asked for in.
        Assert.Null(cursor.ColumnNames);
        cursor.ColumnNames = new[] { "name", "Id" };
        Assert.Equal(new[] { "Id", "Name" }, cursor.ColumnNames);
        cursor.MoveBeforeFirst();
        int count = 0;
        while (cursor.MoveNext())
        {
            Assert.Equal(new[] { "Id", "Name" }, cursor.Current!.Keys);
            count++;
        }
        Assert.Equal(Rows, count);
        Assert.Equal(new[] { "Id", "Name" }, cursor.FindRow("Grp", 2)!.Keys);   // found by a column it does not read

        cursor.ColumnNames = null;
        Assert.Equal(5, cursor.FindRow("Grp", 2)!.Count);
        Assert.Throws<ArgumentException>(() => cursor.ColumnNames = new[] { "Id", "Nope" });
        Assert.Throws<ArgumentException>(() => cursor.ColumnNames = Array.Empty<string>());
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void A_cursor_goes_backwards_through_the_rows_it_goes_forwards_through(string order)
    {
        using var db = Database.Create(_path);
        var table  = Create(db);
        var cursor = CursorOver(table, order);

        var forwards = cursor.Select(r => (int)r["Id"]!).ToList();
        Assert.Equal(Rows, forwards.Count);
        Assert.True(cursor.IsAfterLast);
        Assert.False(cursor.IsBeforeFirst);

        var backwards = cursor.EnumerateBackward().Select(r => (int)r["Id"]!).ToList();
        forwards.Reverse();
        Assert.Equal(forwards, backwards);
        Assert.True(cursor.IsBeforeFirst);
        Assert.False(cursor.IsAfterLast);

        if (order == "PrimaryKey") Assert.Equal(Enumerable.Range(1, Rows).Reverse(), backwards);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void A_cursor_comes_back_to_a_place_it_noted(string order)
    {
        using var db = Database.Create(_path);
        var table  = Create(db);
        var cursor = CursorOver(table, order);
        var ids    = CursorOver(table, order).Select(r => (int)r["Id"]!).ToList();

        var atStart = cursor.GetSavepoint();
        for (int i = 0; i < 100; i++) cursor.MoveNext();
        var at100 = cursor.GetSavepoint();
        Assert.Equal(ids[99], cursor.Current!["Id"]);

        for (int i = 0; i < 50; i++) cursor.MoveNext();
        cursor.RestoreSavepoint(at100);
        Assert.Equal(ids[99], cursor.Current!["Id"]);
        Assert.True(cursor.MoveNext());
        Assert.Equal(ids[100], cursor.Current!["Id"]);
        cursor.RestoreSavepoint(at100);
        Assert.True(cursor.MovePrevious());
        Assert.Equal(ids[98], cursor.Current!["Id"]);

        cursor.MoveAfterLast();
        var atEnd = cursor.GetSavepoint();
        cursor.RestoreSavepoint(atStart);
        Assert.True(cursor.IsBeforeFirst);
        Assert.Null(cursor.Current);
        Assert.True(cursor.MoveNext());
        Assert.Equal(ids[0], cursor.Current!["Id"]);
        cursor.RestoreSavepoint(atEnd);
        Assert.True(cursor.IsAfterLast);
        Assert.True(cursor.MovePrevious());
        Assert.Equal(ids[Rows - 1], cursor.Current!["Id"]);

        // The row at a noted place is deleted: the place is still there, with no row at it.
        cursor.RestoreSavepoint(at100);
        cursor.DeleteCurrentRow();
        cursor.MoveBeforeFirst();
        cursor.RestoreSavepoint(at100);
        Assert.Null(cursor.Current);
        Assert.True(cursor.MoveNext());
        Assert.Equal(ids[100], cursor.Current!["Id"]);

        // Another cursor's savepoint is not this one's.
        Assert.Throws<ArgumentException>(() => CursorOver(table, order).RestoreSavepoint(at100));
        Assert.Throws<ArgumentNullException>(() => cursor.RestoreSavepoint(null!));
    }

    [Fact]
    public void A_table_cursor_reads_each_page_once_however_many_rows_it_has()
    {
        using var db = Database.Create(_path);
        var table = Create(db);
        int pages = table.Rows.Select(r => r.Id!.Value.PageNumber).Distinct().Count();

        var cursor = table.CreateCursor();
        cursor.ColumnNames = new[] { "Id" };
        long reads = db.File.ReadCount;
        Assert.Equal(Rows, cursor.Count());
        Assert.True(db.File.ReadCount - reads <= pages + 2, $"{db.File.ReadCount - reads} pages read for {pages} pages of rows");
    }
}
