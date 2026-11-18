using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class BuilderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"builder_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void TableBuilder_AddColumn_BuildsTableWithCorrectSchema()
    {
        using var db = Database.Create(_path);

        var t = new TableBuilder("Customers")
            .AddColumn(new ColumnBuilder("Id",   DataType.Long).Build())
            .AddColumn(new ColumnBuilder("Name", DataType.Text).MaxLength(100).Build())
            .ToTable(db);

        Assert.Equal("Customers", t.Name);
        Assert.Equal(2, t.Columns.Count);
        Assert.Equal("Id",   t.Columns[0].Name);
        Assert.Equal("Name", t.Columns[1].Name);
    }

    [Fact]
    public void TableBuilder_AddColumnByName_WithInlineConfigure()
    {
        using var db = Database.Create(_path);

        var t = new TableBuilder("Items")
            .AddColumn("Id",    DataType.Long)
            .AddColumn("Label", DataType.Text, cb => cb.MaxLength(50))
            .ToTable(db);

        Assert.Equal(2, t.Columns.Count);
        Assert.Equal(100, t.Columns[1].Length);   // 50 chars × 2 bytes UTF-16
    }

    [Fact]
    public void TableBuilder_WithPrimaryKey_PropagatesToTableDefinition()
    {
        using var db = Database.Create(_path);

        var t = new TableBuilder("Articles")
            .AddColumn("Id",    DataType.Long)
            .AddColumn("Title", DataType.Text, cb => cb.MaxLength(80))
            .WithPrimaryKey("Id")
            .ToTable(db);

        t.Insert(new Row { ["Id"] = 1, ["Title"] = "A" });
        t.Insert(new Row { ["Id"] = 2, ["Title"] = "B" });

        var r = t.FindRowByPrimaryKey(2);
        Assert.NotNull(r);
        Assert.Equal("B", r!["Title"]);
    }

    [Fact]
    public void TableBuilder_ToTable_WithoutColumns_Throws()
    {
        using var db = Database.Create(_path);
        Assert.Throws<InvalidOperationException>(
            () => new TableBuilder("Empty").ToTable(db));
    }
}
