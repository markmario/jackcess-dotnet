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
}
