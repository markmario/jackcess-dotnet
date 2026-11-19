using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary><see cref="Joiner"/>: the rows of one table that go with a row of another, through an index (#29).</summary>
public sealed class JoinerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"joiner_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private (Table Customers, Table Orders) Create(Database db)
    {
        var customers = db.CreateTable("Customers", new[]
        {
            new ColumnDefinition("Region", DataType.Int32),
            new ColumnDefinition("Code", DataType.Text) { MaxLength = 10 },
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 30 },
        }, "Region", "Code");
        var orders = db.CreateTable("Orders", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("Region", DataType.Int32),
            new ColumnDefinition("Customer", DataType.Text) { MaxLength = 10 },
            new ColumnDefinition("Total", DataType.Currency),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByCustomer", "Region", "Customer") });

        for (int region = 1; region <= 3; region++)
            for (int c = 1; c <= 20; c++)
                customers.Insert(new Row { ["Region"] = region, ["Code"] = "C" + c, ["Name"] = $"customer {region}/{c}" });

        // Customer c of region r has c % 4 orders; they go in in no customer's order.
        int id = 0;
        for (int round = 0; round < 3; round++)
            for (int region = 3; region >= 1; region--)
                for (int c = 20; c >= 1; c--)
                    if (round < c % 4)
                        orders.Insert(new Row { ["Id"] = ++id, ["Region"] = region, ["Customer"] = "C" + c, ["Total"] = (decimal)id });
        return (customers, orders);
    }

    [Fact]
    public void The_rows_that_go_with_a_row_are_found_through_the_index()
    {
        using var db = Database.Create(_path);
        var (customers, orders) = Create(db);
        var ordersOf = new Joiner(customers, new[] { "Region", "Code" }, orders, new[] { "region", "customer" });

        Assert.Same(customers, ordersOf.FromTable);
        Assert.Same(orders, ordersOf.ToTable);
        Assert.Equal("ByCustomer", ordersOf.ToIndex.Name);
        Assert.Equal(new[] { "Region", "Code" }, ordersOf.FromColumnNames);
        Assert.Equal(new[] { "Region", "Customer" }, ordersOf.ToColumnNames);

        foreach (var customer in customers.Rows)
        {
            int region = (int)customer["Region"]!;
            string code = (string)customer["Code"]!;
            var expected = orders.Rows.Where(o => (int)o["Region"]! == region && (string)o["Customer"]! == code).Select(o => (int)o["Id"]!).ToList();
            Assert.Equal(int.Parse(code.Substring(1)) % 4, expected.Count);

            var found = ordersOf.FindRows(customer).ToList();
            Assert.Equal(expected, found.Select(o => (int)o["Id"]!));          // in the order they are stored
            Assert.Equal(expected.Count > 0, ordersOf.HasRows(customer));
            Assert.Equal(expected.Count > 0 ? expected[0] : (int?)null, (int?)ordersOf.FindFirstRow(customer)?["Id"]);
            Assert.All(found, o => Assert.NotNull(o.Id));
        }

        // One lookup reads a few pages, not the table.
        long reads = db.File.ReadCount;
        Assert.Equal(3, ordersOf.FindRows(new Row { ["Region"] = 2, ["Code"] = "C7" }).Count());
        Assert.True(db.File.ReadCount - reads <= 12, $"{db.File.ReadCount - reads} pages read to join one row");

        // The values alone will do, in a dictionary that is not a Row; a null joins nothing.
        var values = new Dictionary<string, object?> { ["region"] = 2, ["CODE"] = "C7" };
        Assert.Equal(3, ordersOf.FindRows(values).Count());
        Assert.False(ordersOf.HasRows(new Row { ["Region"] = 2, ["Code"] = null }));
        Assert.Empty(ordersOf.FindRows(new Row { ["Region"] = 9, ["Code"] = "C7" }));
    }

    [Fact]
    public void A_joiner_goes_the_other_way_too()
    {
        using var db = Database.Create(_path);
        var (customers, orders) = Create(db);
        var customerOf = new Joiner(customers, new[] { "Region", "Code" }, orders, new[] { "Region", "Customer" }).CreateReverse();

        Assert.Same(orders, customerOf.FromTable);
        Assert.True(customerOf.ToIndex.IsPrimaryKey);
        foreach (var order in orders.Rows)
        {
            var customer = Assert.Single(customerOf.FindRows(order));
            Assert.Equal(order["Region"], customer["Region"]);
            Assert.Equal(order["Customer"], customer["Code"]);
        }

        // Rows found can be changed while they are gone through: the orders of a customer are deleted.
        var ordersOf = customerOf.CreateReverse();
        var one = customers.FindRowByPrimaryKey(1, "C3")!;
        foreach (var order in ordersOf.FindRows(one))
            orders.Delete(order);
        Assert.False(ordersOf.HasRows(one));
        Assert.True(ordersOf.HasRows(customers.FindRowByPrimaryKey(1, "C2")!));
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Orders");
    }

    [Fact]
    public void A_join_that_cannot_be_made_is_refused()
    {
        using var db = Database.Create(_path);
        var (customers, orders) = Create(db);

        Assert.Throws<ArgumentNullException>(() => new Joiner(null!, new[] { "Region" }, orders, new[] { "Region" }));
        Assert.Throws<ArgumentNullException>(() => new Joiner(customers, null!, orders, new[] { "Region" }));
        Assert.Throws<ArgumentNullException>(() => new Joiner(customers, new[] { "Region" }, null!, new[] { "Region" }));
        Assert.Throws<ArgumentNullException>(() => new Joiner(customers, new[] { "Region" }, orders, null!));

        Assert.Equal("fromColumnNames", Assert.Throws<ArgumentException>(() => new Joiner(customers, new[] { "Nope" }, orders, new[] { "Region" })).ParamName);
        Assert.Equal("toColumnNames", Assert.Throws<ArgumentException>(() => new Joiner(customers, new[] { "Region" }, orders, new[] { "Nope" })).ParamName);
        Assert.Throws<ArgumentException>(() => new Joiner(customers, Array.Empty<string>(), orders, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new Joiner(customers, new[] { "Region", "Code" }, orders, new[] { "Region" }));

        // Orders has no index that begins with Customer, nor with Total.
        Assert.Contains("no index", Assert.Throws<ArgumentException>(() => new Joiner(customers, new[] { "Code" }, orders, new[] { "Customer" })).Message);
        // The index's first column alone will do.
        Assert.Equal(30, new Joiner(customers, new[] { "Region" }, orders, new[] { "Region" }).FindRows(new Row { ["Region"] = 1 }).Count());

        var ordersOf = new Joiner(customers, new[] { "Region", "Code" }, orders, new[] { "Region", "Customer" });
        Assert.Throws<ArgumentNullException>(() => ordersOf.FindRows(null!));
        Assert.Equal("fromRow", Assert.Throws<ArgumentException>(() => ordersOf.FindRows(new Row { ["Region"] = 1 })).ParamName);
        // Customers has no index that begins with Name, so a join from it has no reverse.
        var lopsided = new Joiner(customers, new[] { "Name" }, orders, new[] { "Region" });
        Assert.Throws<ArgumentException>(() => lopsided.CreateReverse());
    }
}
