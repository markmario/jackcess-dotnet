using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Round-trip tests for composite (multi-column) primary keys: create a table
/// with a 2- or 3-column PK, insert rows, reopen, verify the PK metadata
/// survives and the rows are still queryable.
/// </summary>
public sealed class CompositePrimaryKeyTests : IDisposable
{
    private readonly string _path;
    public CompositePrimaryKeyTests()
        => _path = Path.Combine(Path.GetTempPath(), $"compk_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void CreateTable_WithCompositePK_PersistsAllColumnsInIndex()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("Orders", new[]
            {
                new ColumnDefinition("CustomerId", DataType.Int32),
                new ColumnDefinition("OrderId",    DataType.Int32),
                new ColumnDefinition("Total",      DataType.Currency),
            }, primaryKey: new[] { "CustomerId", "OrderId" });
        }

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("Orders");
        var pk = t.Indexes.Single(i => i.IsPrimaryKey);
        Assert.Equal(2, pk.Columns.Count);
        Assert.Equal("CustomerId", pk.Columns[0].Column.Name);
        Assert.Equal("OrderId",    pk.Columns[1].Column.Name);
    }

    [Fact]
    public void Insert_WithCompositePK_RowsRoundTrip()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Orders", new[]
            {
                new ColumnDefinition("CustomerId", DataType.Int32),
                new ColumnDefinition("OrderId",    DataType.Int32),
                new ColumnDefinition("Total",      DataType.Currency),
            }, primaryKey: new[] { "CustomerId", "OrderId" });

            for (int c = 1; c <= 3; c++)
                for (int o = 100; o < 105; o++)
                    t.Insert(new Row
                    {
                        ["CustomerId"] = c,
                        ["OrderId"]    = o,
                        ["Total"]      = c * o * 1.5m,
                    });
        }

        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("Orders").Rows.ToList();
        Assert.Equal(15, rows.Count);

        // Spot-check a value to confirm decoder still works on a composite-PK table.
        var target = rows.Single(r =>
            Convert.ToInt32(r["CustomerId"]) == 2 && Convert.ToInt32(r["OrderId"]) == 103);
        Assert.Equal(2 * 103 * 1.5m, target["Total"]);
    }

    [Fact]
    public void CompositePK_AllowsDuplicateValuesInIndividualColumns()
    {
        // A single-column PK on CustomerId would reject the second row; the
        // composite PK only requires the (CustomerId, OrderId) tuple to be unique.
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Orders", new[]
            {
                new ColumnDefinition("CustomerId", DataType.Int32),
                new ColumnDefinition("OrderId",    DataType.Int32),
            }, primaryKey: new[] { "CustomerId", "OrderId" });

            t.Insert(new Row { ["CustomerId"] = 1, ["OrderId"] = 100 });
            t.Insert(new Row { ["CustomerId"] = 1, ["OrderId"] = 101 });   // dup CustomerId, OK
            t.Insert(new Row { ["CustomerId"] = 2, ["OrderId"] = 100 });   // dup OrderId, OK
        }

        using var reopen = Database.Open(_path);
        Assert.Equal(3, reopen.GetTable("Orders").Rows.Count());
    }

    [Fact]
    public void CreateTable_WithNoKeyColumns_MakesATableWithNoPrimaryKey()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var columns = new[] { new ColumnDefinition("Id", DataType.Int32) };

        Assert.Empty(db.CreateTable("None", columns).Indexes);
        Assert.Empty(db.CreateTable("Empty", columns, primaryKey: Array.Empty<string>()).Indexes);
        Assert.Empty(db.CreateTable("Null", columns, (string[])null!).Indexes);
        Assert.Empty(db.CreateTable("EmptyWithIndexes", columns, Array.Empty<string>(), Array.Empty<IndexDefinition>()).Indexes);
        Assert.Empty(db.CreateTable("NullWithIndexes", columns, null, Array.Empty<IndexDefinition>()).Indexes);

        // A key column with no name is the caller's mistake, and makes no table.
        Assert.Throws<ArgumentException>(() => db.CreateTable("Blank", columns, ""));
        Assert.Throws<ArgumentException>(() => db.CreateTable("NullName", columns, new string[] { null! }));
        Assert.DoesNotContain("Blank", db.GetTableNames());
        Assert.DoesNotContain("NullName", db.GetTableNames());
    }

    [Fact]
    public void CreateTable_Rejects_MoreThanTenPkColumns()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);

        var columns = Enumerable.Range(0, 11)
            .Select(i => new ColumnDefinition($"C{i}", DataType.Int32)).ToArray();
        var pkNames = columns.Select(c => c.Name).ToArray();

        Assert.Throws<InvalidOperationException>(
            () => db.CreateTable("TooMany", columns, primaryKey: pkNames));
    }

    [Fact]
    public void CompositePK_MixedTypes_TextAndLong_RoundTrip()
    {
        // Composite of Text + Long — exercises the per-column encoder dispatch
        // (text path produces variable-length compressed bytes, Long is a
        // fixed-width XOR-biased run).
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Sessions", new[]
            {
                new ColumnDefinition("UserId",     DataType.Text) { MaxLength = 20 },
                new ColumnDefinition("LoginEpoch", DataType.Int32),
                new ColumnDefinition("UserAgent",  DataType.Text) { MaxLength = 100 },
            }, primaryKey: new[] { "UserId", "LoginEpoch" });

            t.Insert(new Row { ["UserId"] = "alice", ["LoginEpoch"] = 1000, ["UserAgent"] = "Chrome" });
            t.Insert(new Row { ["UserId"] = "alice", ["LoginEpoch"] = 2000, ["UserAgent"] = "Firefox" });
            t.Insert(new Row { ["UserId"] = "bob",   ["LoginEpoch"] = 1000, ["UserAgent"] = "Safari" });
        }

        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("Sessions").Rows.ToList();
        Assert.Equal(3, rows.Count);

        var alice1000 = rows.Single(r =>
            (string)r["UserId"]! == "alice" && Convert.ToInt32(r["LoginEpoch"]) == 1000);
        Assert.Equal("Chrome", alice1000["UserAgent"]);
    }

    [Fact]
    public void CompositePK_InsertsWithSameLeadColumn_DoNotClash()
    {
        // Three rows that would all collide on a single-column PK if Jackcess
        // happened to only index the first column. Composite PK encodes the
        // full tuple, so all three coexist.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var t = db.CreateTable("Orders", new[]
        {
            new ColumnDefinition("CustomerId", DataType.Int32),
            new ColumnDefinition("OrderId",    DataType.Int32),
            new ColumnDefinition("Total",      DataType.Currency),
        }, primaryKey: new[] { "CustomerId", "OrderId" });

        t.Insert(new Row { ["CustomerId"] = 1, ["OrderId"] = 100, ["Total"] = 5m  });
        t.Insert(new Row { ["CustomerId"] = 1, ["OrderId"] = 101, ["Total"] = 10m });
        t.Insert(new Row { ["CustomerId"] = 2, ["OrderId"] = 100, ["Total"] = 15m });

        var rows = t.Rows.ToList();
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void CompositePK_NullComponent_Throws()
    {
        // A primary key takes no null, as in Access: the row is refused before
        // anything of it is written. (It used to go in with no entry in the key,
        // a row Access did not find through it.)
        using var db = Database.Create(_path, FileFormat.Access2000);
        var t = db.CreateTable("T", new[]
        {
            new ColumnDefinition("A", DataType.Int32),
            new ColumnDefinition("B", DataType.Int32),
        }, primaryKey: new[] { "A", "B" });

        var refused = Assert.Throws<ConstraintViolationException>(() => t.Insert(new Row { ["A"] = 1, ["B"] = null }));
        Assert.Contains("primary key does not take a null", refused.Message);
        Assert.Empty(t.Rows);
    }

    [Fact]
    public void CompositePK_SingleColumn_StillWorks_LikeLegacyApi()
    {
        // primaryKeyColumns with one entry should behave identically to the
        // single-string primaryKey overload — no regression for existing callers.
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("People", new[]
            {
                new ColumnDefinition("Id",   DataType.Int32),
                new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
            }, primaryKey: new[] { "Id" })
            .Insert(new Row { ["Id"] = 1, ["Name"] = "Alice" });
        }

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("People");
        var pk = t.Indexes.Single(i => i.IsPrimaryKey);
        Assert.Single(pk.Columns);
        Assert.Equal("Id", pk.Columns[0].Column.Name);

        // FindRowByPrimaryKey (single-column path) still works.
        var cur = t.CreateIndexCursor();
        var row = cur.Table.FindRowByPrimaryKey(1);
        Assert.NotNull(row);
        Assert.Equal("Alice", row!["Name"]);
    }
}
