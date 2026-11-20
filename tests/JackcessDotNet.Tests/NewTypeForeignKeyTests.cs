using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Relationships on Large Number and Date/Time Extended columns (#38): created, and kept on insert, update and
/// delete, with values at the ends of each type's range and values one apart, which a comparison through any
/// narrower type would take for the same. What Access makes of such a relationship is in
/// SchemaChangesInAccessTests.
/// </summary>
public sealed class NewTypeForeignKeyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"new_type_fk_{Guid.NewGuid():N}.accdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static readonly DateTime Tick = new DateTime(2020, 1, 2, 3, 4, 5).AddTicks(1234567);

    public static IEnumerable<object[]> Keys()
    {
        // Each type with four parent keys and a value that is no parent's, one unit from one that is.
        yield return new object[]
        {
            DataType.Int64,
            new object[] { 1L, long.MaxValue, long.MinValue, 5_000_000_000L },
            long.MaxValue - 1, 6_000_000_000L,
        };
        yield return new object[]
        {
            DataType.DateTimeExtended,
            new object[] { Tick, DateTime.MaxValue, DateTime.MinValue, new DateTime(1600, 2, 29) },
            Tick.AddTicks(1), new DateTime(2525, 1, 1).AddTicks(7),
        };
    }

    private Database Create(DataType type, object[] keys, bool cascade)
    {
        var db = Database.Create(_path, FileFormat.Access2019);
        var parent = db.CreateTable("Parent", new[] { new ColumnDefinition("K", type), new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 } }, primaryKey: "K");
        var child = db.CreateTable("Child", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", type) }, primaryKey: "Id");
        for (int i = 0; i < keys.Length; i++) parent.Insert(new Row { ["K"] = keys[i], ["Name"] = $"parent {i}" });
        for (int i = 0; i < 40; i++) child.Insert(new Row { ["Id"] = i, ["K"] = i % 5 == 4 ? null : keys[i % 5] });
        db.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K")
        {
            Name = "ParentChild", HasReferentialIntegrity = true, CascadeUpdates = cascade, CascadeDeletes = cascade,
        });
        return db;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_relationship_on_the_type_is_created_and_refuses_a_row_that_refers_to_no_parent(DataType type, object[] keys, object nearMiss, object unused)
    {
        using (var db = Create(type, keys, cascade: false))
        {
            var child = db.GetTable("Child");
            Assert.True(child.Indexes.Single(ix => ix.Name == "ParentChild").IsForeignKey);

            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 100, ["K"] = nearMiss }));
            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 100, ["K"] = unused }));
            Assert.Throws<ConstraintViolationException>(() => child.UpdateByPrimaryKey(0, new Row { ["K"] = nearMiss }));
            foreach (object key in keys) child.Insert(new Row { ["Id"] = 100 + Array.IndexOf(keys, key), ["K"] = key });
            child.Insert(new Row { ["Id"] = 200, ["K"] = null });

            // A parent with children is neither deleted nor given another key; one without is.
            var parent = db.GetTable("Parent");
            Assert.Throws<ConstraintViolationException>(() => parent.DeleteRow("K", keys[1]));
            Assert.Throws<ConstraintViolationException>(() => parent.UpdateByPrimaryKey(keys[2], new Row { ["K"] = unused }));
            parent.Insert(new Row { ["K"] = nearMiss, ["Name"] = "no children" });
            parent.UpdateByPrimaryKey(nearMiss, new Row { ["K"] = unused });
            parent.DeleteRow("K", unused);

            Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 45));
            Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Parent", expectedRows: 4));
        }

        // A relationship is refused when a row already refers to a value one unit from a parent's.
        using var again = Database.Open(_path);
        again.DropRelationship("ParentChild");
        again.GetTable("Child").Insert(new Row { ["Id"] = 300, ["K"] = nearMiss });
        var pages = IndexChecks.AllPages(again);
        Assert.Throws<ConstraintViolationException>(() => again.CreateRelationship(
            new RelationshipDefinition("Parent", "K", "Child", "K") { Name = "ParentChild", HasReferentialIntegrity = true }));
        IndexChecks.AssertUnchanged(pages, again);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_cascading_relationship_on_the_type_takes_the_children_with_the_parent(DataType type, object[] keys, object nearMiss, object unused)
    {
        using var db = Create(type, keys, cascade: true);
        var parent = db.GetTable("Parent");
        var child = db.GetTable("Child");

        // The children of the changed key, and no others: eight rows have each key.
        parent.UpdateByPrimaryKey(keys[1], new Row { ["K"] = nearMiss });
        Assert.Equal(8, child.Rows.Count(r => Equals(r["K"], nearMiss)));
        Assert.Equal(0, child.Rows.Count(r => Equals(r["K"], keys[1])));
        Assert.Equal(8, child.Rows.Count(r => Equals(r["K"], keys[0])));

        parent.DeleteRow("K", keys[2]);
        Assert.Equal(0, child.Rows.Count(r => Equals(r["K"], keys[2])));
        Assert.Equal(32, child.RowCount);
        Assert.Equal(8, child.Rows.Count(r => r["K"] is null));

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 32));
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Parent", expectedRows: 3));
        _ = unused;
    }
}
