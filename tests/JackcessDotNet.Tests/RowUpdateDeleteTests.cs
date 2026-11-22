using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Table.Update"/> and <see cref="Table.Delete"/>: a row is changed by where it was read
/// (<see cref="Row.Id"/>), whatever key its table has (#34).
/// </summary>
public sealed class RowUpdateDeleteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"row_ops_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ColumnDefinition[] Columns() => new[]
    {
        new ColumnDefinition("A", DataType.Int32),
        new ColumnDefinition("B", DataType.Int32),
        new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
        new ColumnDefinition("Notes", DataType.Memo),
    };

    private static void Fill(Table table, int rows)
    {
        for (int i = 1; i <= rows; i++)
            table.Insert(new Row { ["A"] = i / 10, ["B"] = i, ["Name"] = "name " + i, ["Notes"] = "notes " + i });
    }

    private static List<string> Lines(Table table)
        => table.Rows.Select(r => $"{r["A"]}|{r["B"]}|{r["Name"]}|{r["Notes"]}").OrderBy(l => l, StringComparer.Ordinal).ToList();

    [Fact]
    public void A_row_that_is_read_says_where_it_is_and_a_row_a_caller_makes_does_not()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", Columns(), primaryKey: "B");
        Fill(table, 300);

        Assert.Null(new Row { ["B"] = 1 }.Id);

        var ids = table.Rows.Select(r => r.Id!.Value).ToList();
        Assert.Equal(300, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => id).ToList(), ids);                      // the order rows are stored in
        Assert.All(ids, id => Assert.True(id.PageNumber > 0 && id.RowNumber is >= 0 and <= 255));
        Assert.True(ids.Select(id => id.PageNumber).Distinct().Count() > 1);    // more than one page of them

        // Every reader gives the same row the same id.
        var byCursor = table.CreateCursor().Select(r => r.Id!.Value).ToList();
        Assert.Equal(ids, byCursor);
        Assert.Equal(ids.OrderBy(id => id), table.CreateIndexCursor().Select(r => r.Id!.Value).OrderBy(id => id));
        Assert.Equal(table.Rows.First(r => (int)r["B"]! == 123).Id, table.FindRowByPrimaryKey(123)!.Id);

        // A copy is a new row.
        Assert.Null(new Row(table.FindRowByPrimaryKey(5)!).Id);
    }

    [Fact]
    public void A_row_id_is_a_page_and_a_row_and_is_ordered_by_them()
    {
        var id = new RowId(7, 3);
        Assert.Equal(7, id.PageNumber);
        Assert.Equal(3, id.RowNumber);
        Assert.Equal("7:3", id.ToString());
        Assert.Equal(new RowId(7, 3), id);
        Assert.True(id == new RowId(7, 3));
        Assert.True(id != new RowId(7, 4));
        Assert.Equal(new RowId(7, 3).GetHashCode(), id.GetHashCode());
        Assert.True(id.CompareTo(new RowId(7, 4)) < 0);
        Assert.True(id.CompareTo(new RowId(6, 200)) > 0);
        Assert.Equal(0, id.CompareTo(new RowId(7, 3)));
        Assert.False(id.Equals("7:3"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new RowId(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowId(0x1000000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowId(1, 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowId(1, -1));
    }

    public static IEnumerable<object[]> Keys() => new[]
    {
        new object[] { "none", Array.Empty<string>() },
        new object[] { "one column", new[] { "B" } },
        new object[] { "two columns", new[] { "A", "B" } },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_row_is_updated_and_deleted_whatever_key_its_table_has(string keyed, string[] key)
    {
        Assert.NotEmpty(keyed);
        using (var db = Database.Create(_path))
        {
            var table = db.CreateTable("T", Columns(), key, new[] { new IndexDefinition("ByName", "Name") });
            Fill(table, 200);

            var row = table.Rows.Single(r => (int)r["B"]! == 57);
            var was = row.Id!.Value;
            row["Name"]  = "renamed";
            row["Notes"] = new string('n', 5000);
            table.Update(row);

            Assert.Equal(was, row.Id);              // a row keeps its place, though it no longer fits its page
            Assert.Equal(200, table.RowCount);

            table.Delete(table.Rows.Single(r => (int)r["B"]! == 58));
            Assert.Equal(199, table.RowCount);

            // The row updated is updated again by the Row it was given back in.
            row["A"] = 99;
            table.Update(row);
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            var lines = Lines(table);
            Assert.Equal(199, lines.Count);
            Assert.Contains($"99|57|renamed|{new string('n', 5000)}", lines);
            Assert.DoesNotContain(lines, l => l.Contains("|58|"));
            Assert.Contains("5|56|name 56|notes 56", lines);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 199);

            Assert.Equal(57, table.CreateIndexCursor("ByName").FindRowByKey("renamed")!["B"]);
            Assert.Null(table.CreateIndexCursor("ByName").FindRowByKey("name 57"));
            Assert.Null(table.CreateIndexCursor("ByName").FindRowByKey("name 58"));
        }
    }

    [Fact]
    public void An_update_writes_only_what_changed()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", Columns(), primaryKey: "B");
        Fill(table, 20);

        // Nothing changed: nothing is written, and the row stays where it is.
        var row = table.FindRowByPrimaryKey(7)!;
        var was = row.Id;
        long writes = db.File.WriteCount;
        table.Update(row);
        Assert.Equal(writes, db.File.WriteCount);
        Assert.Equal(was, row.Id);

        // A row read with some of its columns is updated in those, and keeps the rest.
        var cursor = table.CreateCursor();
        cursor.ColumnNames = new[] { "B", "Name" };
        var partial = cursor.First(r => (int)r["B"]! == 9);
        Assert.Equal(new[] { "B", "Name" }, partial.Keys);
        partial["Name"] = "nine";
        table.Update(partial);

        var read = table.FindRowByPrimaryKey(9)!;
        Assert.Equal("nine", read["Name"]);
        Assert.Equal("notes 9", read["Notes"]);
        Assert.Equal(0, read["A"]);

        // A value set to null is written as null.
        read["Notes"] = null;
        table.Update(read);
        Assert.Null(table.FindRowByPrimaryKey(9)!["Notes"]);
    }

    [Fact]
    public void A_date_the_update_does_not_touch_keeps_every_digit_the_row_stored()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("When", DataType.DateTime),
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByWhen", "When") });
        table.Insert(new Row { ["Id"] = 1, ["When"] = new DateTime(2024, 5, 6, 7, 8, 9), ["Name"] = "one" });

        // A date as Access can store one: a fraction of a millisecond past what a DateTime reads it as.
        double stored = new DateTime(2024, 5, 6, 7, 8, 9).ToOADate() + 1e-9;
        int moved = table.RewriteRowAt(table.Rows.Single().Pointer, new Row { ["When"] = stored });
        Assert.Equal(stored, table.ReadRowAt(moved, table.Columns)!["When"]);

        var row = table.Rows.Single();
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), row["When"]);   // read to the millisecond
        row["Name"] = "renamed";
        table.Update(row);

        var after = table.Rows.Single();
        Assert.Equal("renamed", after["Name"]);
        Assert.Equal(stored, table.ReadRowAt(after.Pointer, table.Columns)!["When"]);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 1);   // under the key of the date stored
    }

    [Fact]
    public void A_row_that_was_not_read_from_the_table_is_refused()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", Columns(), primaryKey: "B");
        var other = db.CreateTable("Other", Columns(), primaryKey: "B");
        Fill(table, 5);
        Fill(other, 5);
        var before = IndexChecks.AllPages(db);

        Assert.Throws<ArgumentNullException>(() => table.Update(null!));
        Assert.Throws<ArgumentNullException>(() => table.Delete(null!));

        var made = Assert.Throws<ArgumentException>(() => table.Update(new Row { ["B"] = 1, ["Name"] = "x" }));
        Assert.Equal("row", made.ParamName);
        Assert.Throws<ArgumentException>(() => table.Delete(new Row { ["B"] = 1 }));

        var others = other.FindRowByPrimaryKey(1)!;
        var wrong = Assert.Throws<ArgumentException>(() => table.Update(others));
        Assert.Contains("'Other'", wrong.Message);
        Assert.Throws<ArgumentException>(() => table.Delete(others));

        IndexChecks.AssertUnchanged(before, db);
    }

    [Fact]
    public void A_row_that_is_no_longer_where_it_was_read_is_refused()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", Columns(), primaryKey: "B");
        Fill(table, 5);

        // Deleted: it has no id, and a second Row read from the same place finds no row there.
        var row   = table.FindRowByPrimaryKey(2)!;
        var again = table.FindRowByPrimaryKey(2)!;
        table.Delete(row);
        Assert.Null(row.Id);
        Assert.Throws<ArgumentException>(() => table.Delete(row));
        Assert.Throws<InvalidOperationException>(() => table.Delete(again));
        again["Name"] = "x";
        Assert.Throws<InvalidOperationException>(() => table.Update(again));

        // Updated through one Row, the row is where it was: another Row read from there is still the row's, and
        // writes what was changed in it and nothing else.
        var first  = table.FindRowByPrimaryKey(3)!;
        var second = table.FindRowByPrimaryKey(3)!;
        first["Name"] = "changed";
        table.Update(first);
        second["Notes"] = "noted";
        table.Update(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("changed", table.FindRowByPrimaryKey(3)!["Name"]);
        Assert.Equal("noted", table.FindRowByPrimaryKey(3)!["Notes"]);
        Assert.Equal(4, table.RowCount);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 4);
    }

    [Fact]
    public void An_update_that_breaks_a_rule_leaves_the_row_as_it_was()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", Columns(), new[] { "A", "B" },
            new[] { new IndexDefinition("ByName", "Name") { IsUnique = true } });
        Fill(table, 30);
        var before = IndexChecks.AllPages(db);

        var row = table.Rows.Single(r => (int)r["B"]! == 12);
        row["Name"] = "name 13";                                   // another row's, in a unique index
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));

        row = table.Rows.Single(r => (int)r["B"]! == 12);
        row["A"] = 1; row["B"] = 13;                               // another row's key
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));

        row = table.Rows.Single(r => (int)r["B"]! == 12);
        row["B"] = null;                                           // a key takes no null
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));

        row = table.Rows.Single(r => (int)r["B"]! == 12);
        row["Name"] = new string('x', 51);                         // longer than the column
        Assert.Throws<ColumnValueTooLongException>(() => table.Update(row));

        IndexChecks.AssertUnchanged(before, db);
    }

    [Fact]
    public void Relationships_are_kept_for_a_table_with_a_key_of_two_columns()
    {
        using var db = Database.Create(_path);
        var parent = db.CreateTable("Parent", new[]
        {
            new ColumnDefinition("A", DataType.Int32), new ColumnDefinition("B", DataType.Int32),
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
        }, "A", "B");
        var child = db.CreateTable("Child", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Int32), new ColumnDefinition("B", DataType.Int32),
        }, "Id");
        var loose = db.CreateTable("Loose", new[]
        {
            new ColumnDefinition("A", DataType.Int32), new ColumnDefinition("B", DataType.Int32),
        });
        for (int i = 1; i <= 5; i++) parent.Insert(new Row { ["A"] = 1, ["B"] = i, ["Name"] = "p" + i });
        for (int i = 1; i <= 6; i++) child.Insert(new Row { ["Id"] = i, ["A"] = 1, ["B"] = (i + 1) / 2 });
        for (int i = 1; i <= 2; i++) loose.Insert(new Row { ["A"] = 1, ["B"] = 3 });
        db.CreateRelationship(new RelationshipDefinition("Parent", new[] { "A", "B" }, "Child", new[] { "A", "B" })
        {
            HasReferentialIntegrity = true, CascadeUpdates = true, CascadeDeletes = true,
        });
        db.CreateRelationship(new RelationshipDefinition("Parent", new[] { "A", "B" }, "Loose", new[] { "A", "B" })
        {
            HasReferentialIntegrity = true,
        });
        db.EnforceForeignKeys = true;

        // The key of a parent changes: its children follow it.
        var row = parent.FindRowByPrimaryKey(1, 2)!;
        row["B"] = 20;
        parent.Update(row);
        Assert.Equal(new[] { 1, 1, 20, 20, 3, 3 }, child.Rows.OrderBy(r => (int)r["Id"]!).Select(r => (int)r["B"]!));

        // A parent goes: its children go with it.
        parent.Delete(parent.FindRowByPrimaryKey(1, 1)!);
        Assert.Equal(new[] { 3, 4, 5, 6 }, child.Rows.Select(r => (int)r["Id"]!).OrderBy(i => i));

        // Rows of a table with no key refer to (1, 3) through a relationship that does not cascade.
        var before = IndexChecks.AllPages(db);
        var held = parent.FindRowByPrimaryKey(1, 3)!;
        Assert.Throws<ConstraintViolationException>(() => parent.Delete(held));
        held["B"] = 30;
        Assert.Throws<ConstraintViolationException>(() => parent.Update(held));
        IndexChecks.AssertUnchanged(before, db);

        // A row of the table with no key is changed to refer to nothing there is: refused; and deleted: done.
        var referring = loose.Rows.First();
        referring["B"] = 77;
        Assert.Throws<ConstraintViolationException>(() => loose.Update(referring));
        loose.Delete(loose.Rows.First());
        Assert.Equal(1, loose.RowCount);

        foreach (string name in new[] { "Parent", "Child", "Loose" })
            IndexChecks.AssertEveryIndexHoldsTheRows(db, name);
    }

    [Fact]
    public void An_AutoNumber_keeps_its_value_through_an_update()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true },
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
        }, "Id");
        table.Insert(new Row { ["Name"] = "one" });
        table.Insert(new Row { ["Name"] = "two" });

        var row = table.FindRowByPrimaryKey(2)!;
        row["Id"]   = 50;
        row["Name"] = "second";
        table.Update(row);
        Assert.Equal("second", table.FindRowByPrimaryKey(2)!["Name"]);
        Assert.Null(table.FindRowByPrimaryKey(50));

        table.AllowAutoNumberInsert = true;
        row = table.FindRowByPrimaryKey(2)!;
        row["Id"] = 50;
        table.Update(row);
        Assert.Equal("second", table.FindRowByPrimaryKey(50)!["Name"]);
        Assert.Equal(50, table.LastAutoNumber);
    }
}
