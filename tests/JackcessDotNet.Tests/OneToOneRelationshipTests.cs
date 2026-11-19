using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Whether a relationship is one to one is the caller's to say (#37): <see cref="RelationshipDefinition.IsOneToOne"/>
/// null marks it when both sides are unique, as Access's Relationships window does; true marks it whatever
/// indexes there are, as DAO does when told to, and the foreign key's index is then a unique one, built over the
/// rows; false leaves it unmarked. What DAO makes of each is in ExportMdb.Interop's SchemaChangesInAccessTests.
/// </summary>
public sealed class OneToOneRelationshipTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"one_to_one_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ColumnDefinition Long(string name) => new ColumnDefinition(name, DataType.Int32);

    private Database Create(int childrenOfEach)
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var parent = db.CreateTable("Parent", new[] { Long("K") }, primaryKey: "K");
        var child = db.CreateTable("Child", new[] { Long("Id"), Long("K") }, primaryKey: "Id");
        db.CreateTable("Unique", new[] { Long("Id"), Long("K") }, new[] { "Id" }, new[] { new IndexDefinition("OneEach", "K") { IsUnique = true } });
        for (int k = 1; k <= 5; k++) parent.Insert(new Row { ["K"] = k });
        for (int i = 0; i < 5 * childrenOfEach; i++) child.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1 });
        return db;
    }

    private static RelationshipDefinition To(string table, bool? oneToOne, bool enforced = true)
        => new RelationshipDefinition("Parent", "K", table, "K") { Name = "To" + table, HasReferentialIntegrity = enforced, IsOneToOne = oneToOne };

    [Fact]
    public void Asked_for_a_relationship_is_one_to_one_and_its_foreign_key_s_index_is_unique()
    {
        using var db = Create(childrenOfEach: 1);
        var created = db.CreateRelationship(To("Child", oneToOne: true));
        Assert.True(created.IsOneToOne);

        var child = db.GetTable("Child");
        var index = child.Indexes.Single(ix => ix.Name == "ToChild");
        Assert.True(index.IsForeignKey);
        Assert.True(index.IsUnique);
        Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 100, ["K"] = 3 }));
        child.Insert(new Row { ["Id"] = 101, ["K"] = null });
        child.Insert(new Row { ["Id"] = 102, ["K"] = null });
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 7));
    }

    [Fact]
    public void Asked_for_a_one_to_one_relationship_is_refused_when_two_rows_refer_to_one_parent()
    {
        Create(childrenOfEach: 2).Dispose();
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var refused = Assert.Throws<ConstraintViolationException>(() => db.CreateRelationship(To("Child", oneToOne: true)));
            Assert.Contains("Nothing was changed", refused.Message);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));

        // Unasked, the same relationship is one to many, as before.
        using var open = Database.Open(_path);
        Assert.False(open.CreateRelationship(To("Child", oneToOne: null)).IsOneToOne);
        Assert.False(open.GetTable("Child").Indexes.Single(ix => ix.Name == "ToChild").IsUnique);
    }

    [Fact]
    public void Declined_a_relationship_between_two_unique_columns_is_not_marked_and_its_index_is_an_ordinary_one()
    {
        using var db = Create(childrenOfEach: 1);
        Assert.False(db.CreateRelationship(To("Unique", oneToOne: false)).IsOneToOne);
        var index = db.GetTable("Unique").Indexes.Single(ix => ix.Name == "ToUnique");
        Assert.False(index.IsUnique);
        Assert.NotEqual(db.GetTable("Unique").Indexes.Single(ix => ix.Name == "OneEach").IndexDataNumber, index.IndexDataNumber);

        // Unasked, it is one to one, and shares the unique index's tree: the default is as it was.
        db.DropRelationship("ToUnique");
        Assert.True(db.CreateRelationship(To("Unique", oneToOne: null)).IsOneToOne);
        var shared = db.GetTable("Unique").Indexes.Single(ix => ix.Name == "ToUnique");
        Assert.Equal(db.GetTable("Unique").Indexes.Single(ix => ix.Name == "OneEach").IndexDataNumber, shared.IndexDataNumber);
    }

    [Fact]
    public void Without_referential_integrity_the_mark_is_only_a_mark()
    {
        using var db = Create(childrenOfEach: 2);
        Assert.True(db.CreateRelationship(To("Child", oneToOne: true, enforced: false)).IsOneToOne);
        Assert.DoesNotContain(db.GetTable("Child").Indexes, ix => ix.IsForeignKey);
        Assert.True(db.GetRelationships().Single().IsOneToOne);
    }
}
