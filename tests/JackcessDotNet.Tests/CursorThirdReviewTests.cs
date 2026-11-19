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
