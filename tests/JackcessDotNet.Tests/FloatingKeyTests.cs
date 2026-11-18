using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A row named by a floating-point key is the row with those bits. Float and Double columns can be primary keys,
/// and Access keys +0.0 and -0.0 apart, and a Double apart from the next one after it; the comparison that confirms
/// a row took them through decimal, where they are equal, so an update or a delete by the second value changed the
/// first row found (review of qcatco/jackcess-dotnet#11).
/// </summary>
public sealed class FloatingKeyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"floatkey_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static readonly double NextAfterOne = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1.0) + 1);
    private static readonly float NextSingleAfterOne = BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToInt32(BitConverter.GetBytes(1.0f), 0) + 1), 0);

    public static IEnumerable<object[]> Pairs() => new[]
    {
        new object[] { DataType.Double, 0.0, -0.0 },
        new object[] { DataType.Double, 1.0, NextAfterOne },
        new object[] { DataType.Float, 0.0f, -0.0f },
        new object[] { DataType.Float, 1.0f, NextSingleAfterOne },
    };

    private static long Bits(object value) => value is float f
        ? BitConverter.ToInt32(BitConverter.GetBytes(f), 0)
        : BitConverter.DoubleToInt64Bits((double)value);

    private static Dictionary<long, string> Names(Table table)
        => table.Rows.ToDictionary(r => Bits(r["K"]!), r => (string)r["Name"]!);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void An_update_or_a_delete_by_a_floating_point_key_changes_the_row_with_those_bits(DataType type, object first, object second)
    {
        Assert.NotEqual(Bits(first), Bits(second));
        foreach (bool byTheSecond in new[] { true, false })
        {
            if (File.Exists(_path)) File.Delete(_path);
            object named = byTheSecond ? second : first, other = byTheSecond ? first : second;
            using var db = Database.Create(_path, FileFormat.Access2000);
            var table = db.CreateTable("T", new[] { new ColumnBuilder("K", type).Build(), new ColumnBuilder("Name", DataType.Text).Build() }, primaryKey: "K");
            table.Insert(new Row { ["K"] = first, ["Name"] = "first" });
            table.Insert(new Row { ["K"] = second, ["Name"] = "second" });
            table.Insert(new Row { ["K"] = type == DataType.Float ? 7.5f : 7.5, ["Name"] = "another" });
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 3);

            Assert.Equal(byTheSecond ? "second" : "first", table.FindRowByPrimaryKey(named)!["Name"]);

            table.UpdateByPrimaryKey(named, new Row { ["Name"] = "changed" });
            var names = Names(table);
            Assert.Equal("changed", names[Bits(named)]);
            Assert.Equal(byTheSecond ? "first" : "second", names[Bits(other)]);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 3);

            table.DeleteRow("K", named);
            names = Names(table);
            Assert.False(names.ContainsKey(Bits(named)));
            Assert.Equal(byTheSecond ? "first" : "second", names[Bits(other)]);
            Assert.Equal("another", names[Bits(type == DataType.Float ? 7.5f : (object)7.5)]);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 2);
            Assert.Null(table.FindRowByPrimaryKey(named));
            Assert.NotNull(table.FindRowByPrimaryKey(other));
        }
    }

    [Fact]
    public void A_key_given_as_another_number_type_still_finds_its_row()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var longs = db.CreateTable("L", new[] { new ColumnBuilder("K", DataType.Long).Build(), new ColumnBuilder("Name", DataType.Text).Build() }, primaryKey: "K");
        longs.Insert(new Row { ["K"] = 5, ["Name"] = "five" });
        longs.UpdateByPrimaryKey(5L, new Row { ["Name"] = "by a long" });
        longs.UpdateByPrimaryKey((short)5, new Row { ["Name"] = "by a short" });
        longs.UpdateByPrimaryKey(5.0, new Row { ["Name"] = "by a double" });
        Assert.Equal("by a double", longs.Rows.Single()["Name"]);

        var doubles = db.CreateTable("D", new[] { new ColumnBuilder("K", DataType.Double).Build(), new ColumnBuilder("Name", DataType.Text).Build() }, primaryKey: "K");
        doubles.Insert(new Row { ["K"] = 2.0, ["Name"] = "two" });
        doubles.Insert(new Row { ["K"] = 0.5, ["Name"] = "half" });
        doubles.UpdateByPrimaryKey(2, new Row { ["Name"] = "by an int" });
        doubles.UpdateByPrimaryKey(0.5f, new Row { ["Name"] = "by a single" });
        doubles.UpdateByPrimaryKey(0.5m, new Row { ["Name"] = "by a decimal" });
        Assert.Equal(new[] { "by a decimal", "by an int" }, doubles.Rows.Select(r => (string)r["Name"]!).OrderBy(n => n));

        var money = db.CreateTable("M", new[] { new ColumnBuilder("K", DataType.Money).Build(), new ColumnBuilder("Name", DataType.Text).Build() }, primaryKey: "K");
        money.Insert(new Row { ["K"] = 1.25m, ["Name"] = "a" });
        money.Insert(new Row { ["K"] = 1.2501m, ["Name"] = "b" });
        money.UpdateByPrimaryKey(1.2501m, new Row { ["Name"] = "changed" });
        money.UpdateByPrimaryKey(1.250m, new Row { ["Name"] = "and this" });
        Assert.Equal(new[] { "and this", "changed" }, money.Rows.OrderBy(r => (decimal)r["K"]!).Select(r => (string)r["Name"]!));
        foreach (string name in new[] { "L", "D", "M" }) IndexChecks.AssertEveryIndexHoldsTheRows(db, name);
    }
}