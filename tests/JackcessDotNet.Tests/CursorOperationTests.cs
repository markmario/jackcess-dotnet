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
                Assert.NotEqual(was, cursor.Current.Id);
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
        Assert.NotNull(cursor.FindRowByEntry(2));
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

        // Text is matched exactly, and a null never matches.
        Assert.Null(cursor.FindRow("Name", "NAME 010"));
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
