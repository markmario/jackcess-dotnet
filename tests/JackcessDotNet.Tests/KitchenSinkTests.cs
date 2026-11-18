using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// End-to-end "everything together" tests that exercise multiple features in
/// one go. If these pass, the library's public API is internally consistent
/// across schema authoring, row CRUD, indexed lookup, and reopen.
/// </summary>
public sealed class KitchenSinkTests : IDisposable
{
    private readonly string _path;
    public KitchenSinkTests()
        => _path = Path.Combine(Path.GetTempPath(), $"ks_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void MultiTypeTable_AllValuesSurviveReopen()
    {
        // Single table carrying one column of each supported fixed-length type
        // plus Text + Memo. Insert one fully-populated row, reopen, scan back.
        var columns = new[]
        {
            new ColumnDefinition("Id",       DataType.Int32),
            new ColumnDefinition("Title",    DataType.Text) { MaxLength = 50 },
            new ColumnDefinition("Age",      DataType.Int16),
            new ColumnDefinition("Active",   DataType.Boolean),
            new ColumnDefinition("Score",    DataType.Double),
            new ColumnDefinition("Balance",  DataType.Currency),
            new ColumnDefinition("Created",  DataType.DateTime),
            new ColumnDefinition("Marker",   DataType.Guid),
            new ColumnDefinition("Body",     DataType.Memo),
        };
        var marker = Guid.NewGuid();
        var when   = new DateTime(2024, 1, 2, 3, 4, 5);

        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Things", columns, primaryKey: "Id");
            t.Insert(new Row
            {
                ["Id"]      = 7,
                ["Title"]   = "Lucky",
                ["Age"]     = (short)21,
                ["Active"]  = true,
                ["Score"]   = 99.5,
                ["Balance"] = 1234.5678m,
                ["Created"] = when,
                ["Marker"]  = marker,
                ["Body"]    = "All here.",
            });
        }

        using var reopen = Database.Open(_path);
        var t2 = reopen.GetTable("Things");
        var rows = t2.Rows.ToList();
        Assert.Single(rows);
        var r = rows[0];

        Assert.Equal(7,           r["Id"]);
        Assert.Equal("Lucky",     r["Title"]);
        Assert.Equal((short)21,   r["Age"]);
        Assert.Equal(true,        r["Active"]);
        Assert.Equal(99.5,        (double)r["Score"]!);
        Assert.Equal(1234.5678m,  r["Balance"]);
        Assert.Equal(when,        r["Created"]);
        Assert.Equal(marker,      r["Marker"]);
        Assert.Equal("All here.", r["Body"]);

        // PK metadata round-tripped through TDEF.
        var pk = t2.Indexes.First(i => i.IsPrimaryKey);
        Assert.Equal("Id", pk.Columns[0].Column.Name);
    }

    [Fact]
    public void TwoTables_IndependentLookups_AfterReopen()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var customers = db.CreateTable("Customers", new[]
            {
                new ColumnDefinition("Id",   DataType.Int32),
                new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
            }, primaryKey: "Id");
            for (int i = 1; i <= 5; i++)
                customers.Insert(new Row { ["Id"] = i, ["Name"] = $"Cust{i}" });

            var orders = db.CreateTable("Orders", new[]
            {
                new ColumnDefinition("OrderId",    DataType.Int32),
                new ColumnDefinition("CustomerId", DataType.Int32),
                new ColumnDefinition("Total",      DataType.Currency),
            }, primaryKey: "OrderId");
            for (int i = 100; i <= 110; i++)
                orders.Insert(new Row
                {
                    ["OrderId"]    = i,
                    ["CustomerId"] = (i % 5) + 1,
                    ["Total"]      = i * 10.5m,
                });
        }

        using var reopen = Database.Open(_path);
        Assert.Contains("Customers", reopen.GetTableNames());
        Assert.Contains("Orders",    reopen.GetTableNames());

        var custRows  = reopen.GetTable("Customers").Rows.ToList();
        var orderRows = reopen.GetTable("Orders").Rows.ToList();
        Assert.Equal(5,  custRows.Count);
        Assert.Equal(11, orderRows.Count);
    }

    [Fact]
    public void UpdateDelete_BothPaths_LeaveTableConsistent()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var t = db.CreateTable("Things", new[]
        {
            new ColumnDefinition("Id",    DataType.Int32),
            new ColumnDefinition("Title", DataType.Text) { MaxLength = 50 },
        }, primaryKey: "Id");

        for (int i = 1; i <= 5; i++)
            t.Insert(new Row { ["Id"] = i, ["Title"] = $"T{i}" });

        t.UpdateByPrimaryKey(3, new Row { ["Title"] = "Updated" });
        t.DeleteRow("Id", 4);

        var rows = t.Rows.OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal("Updated", rows.Single(r => Convert.ToInt32(r["Id"]) == 3)["Title"]);
        Assert.DoesNotContain(rows, r => Convert.ToInt32(r["Id"]) == 4);

        // Cursor sees the consistent state.
        var c = t.CreateIndexCursor();
        Assert.Equal("Updated", c.Table.FindRowByPrimaryKey(3)!["Title"]);
        Assert.Null(c.Table.FindRowByPrimaryKey(4));
    }

    [Fact]
    public void Database_OpenAfterFullPipeline_HasIndexAndDataConsistent()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Demo", new[]
            {
                new ColumnDefinition("Id",   DataType.Int32),
                new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
            }, primaryKey: "Id");
            for (int i = 1; i <= 100; i++)
                t.Insert(new Row { ["Id"] = i, ["Name"] = $"N{i:000}" });
        }

        using var reopen = Database.Open(_path);
        var t2 = reopen.GetTable("Demo");
        Assert.Equal(100, t2.Rows.Count());

        var pkIx = t2.Indexes.First(ix => ix.IsPrimaryKey);
        Assert.Equal("Id", pkIx.Columns[0].Column.Name);
        Assert.True(pkIx.RootPageNumber > 0);
    }
}
