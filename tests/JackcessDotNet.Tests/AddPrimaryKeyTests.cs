using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A primary key given to a table that has rows and no key (#37). Access writes one as an index whose block has
/// the flags 0x89 - unique and required - and whose slot has type 1, built over the rows; so does this. What
/// Access makes of the result is in ExportMdb.Interop's SchemaChangesInAccessTests.
/// </summary>
public sealed class AddPrimaryKeyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"add_pk_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ColumnDefinition Long(string name) => new ColumnDefinition(name, DataType.Int32);
    private static ColumnDefinition Text(string name) => new ColumnDefinition(name, DataType.Text) { MaxLength = 20 };

    private static IndexDefinition Key(params string[] columns) => new IndexDefinition("PrimaryKey", columns) { IsPrimaryKey = true };

    private Database CreateWithRows(int rows)
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[] { Long("Id"), Long("K"), Text("Name") },
                                   primaryKey: null, new[] { new IndexDefinition("ByK", "K") });
        for (int i = 1; i <= rows; i++)
            table.Insert(new Row { ["Id"] = i, ["K"] = i % 5, ["Name"] = $"name {i}" });
        return db;
    }

    private void AssertRefusedAndUnchanged<TException>(Action<Table> change) where TException : Exception
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            var refused = Assert.Throws<TException>(() => change(table));
            Assert.Contains("Nothing was changed", refused.Message);
            Assert.DoesNotContain(table.Indexes, ix => ix.IsPrimaryKey);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_primary_key_added_to_a_table_with_rows_is_the_table_s_key_from_then_on()
    {
        using (var db = CreateWithRows(300))
        {
            var table = db.GetTable("T");
            Assert.Throws<InvalidOperationException>(() => table.FindRowByPrimaryKey(7));

            var key = table.AddIndex(Key("Id"));
            Assert.True(key.IsPrimaryKey);
            Assert.True(key.IsUnique);
            Assert.Equal("PrimaryKey", key.Name);
            Assert.Equal("name 7", table.FindRowByPrimaryKey(7)!["Name"]);

            // It is kept: a duplicate and a null are refused, an update finds its row by it.
            table.Insert(new Row { ["Id"] = 301, ["K"] = 1, ["Name"] = "after" });
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 301, ["Name"] = "twice" }));
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Name"] = "no id" }));
            table.UpdateByPrimaryKey(301, new Row { ["Name"] = "changed" });
            Assert.Equal("changed", table.FindRowByPrimaryKey(301)!["Name"]);
            Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 301));
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("T");
        var index = read.Indexes.Single(ix => ix.IsPrimaryKey);
        Assert.Equal(new[] { "Id" }, index.Columns.Select(c => c.Column.Name));
        Assert.Equal("changed", read.FindRowByPrimaryKey(301)!["Name"]);

        // As Access writes one: the block's flags unique and required, the slot's type 1, the slots in name order.
        Assert.Equal(0x89, index.Flags);
        Assert.Equal(TableDefinition.PrimaryKeyIndexType, index.IndexType);
        Assert.Equal(new[] { "ByK", "PrimaryKey" }, read.Indexes.Select(ix => ix.Name));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "T", expectedRows: 301));
    }

    [Fact]
    public void A_primary_key_of_several_columns_is_added_and_finds_rows()
    {
        using var db = CreateWithRows(40);
        var table = db.GetTable("T");
        table.AddIndex(new IndexDefinition("Both", "K", "Id") { IsPrimaryKey = true });

        Assert.Equal("name 12", table.FindRowByPrimaryKey(2, 12)!["Name"]);
        Assert.Null(table.FindRowByPrimaryKey(3, 12));
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 12, ["K"] = 2 }));
        table.Insert(new Row { ["Id"] = 12, ["K"] = 3 });
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 41));
    }

    [Fact]
    public void A_primary_key_the_rows_do_not_allow_is_refused_and_nothing_changes()
    {
        using (var db = CreateWithRows(20))
        {
            var table = db.GetTable("T");
            table.Insert(new Row { ["Id"] = 7, ["K"] = 9, ["Name"] = "a second 7" });
            table.Insert(new Row { ["K"] = 8, ["Name"] = "no id" });
        }

        // Two rows with one Id; a row with no Id; and K, of which many rows have each value.
        AssertRefusedAndUnchanged<ConstraintViolationException>(table => table.AddIndex(Key("Id")));
        AssertRefusedAndUnchanged<ConstraintViolationException>(table => table.AddIndex(Key("K")));
        AssertRefusedAndUnchanged<ConstraintViolationException>(table => table.AddIndex(Key("Name", "Id")));
    }

    [Fact]
    public void A_second_primary_key_is_refused_and_so_is_one_that_ignores_nulls()
    {
        using (var db = CreateWithRows(5))
        {
            var table = db.GetTable("T");
            Assert.Throws<ArgumentException>(() => table.AddIndex(new IndexDefinition("PrimaryKey", "Id") { IsPrimaryKey = true, IgnoreNulls = true }));
            table.AddIndex(Key("Id"));
            var pages = IndexChecks.AllPages(db);
            var refused = Assert.Throws<ArgumentException>(() => table.AddIndex(new IndexDefinition("Another", "K", "Id") { IsPrimaryKey = true }));
            Assert.Contains("already has a primary key", refused.Message);
            IndexChecks.AssertUnchanged(pages, db);
        }

        // And a table is not created with a primary key among its index definitions: its key is named by its columns.
        using var created = Database.Open(_path);
        int count = created.File.PageCount;
        Assert.Throws<ArgumentException>(() => created.CreateTable("U", new[] { Long("Id") }, primaryKey: null, new[] { Key("Id") }));
        Assert.Equal(count, created.File.PageCount);
    }

    [Fact]
    public void A_primary_key_on_the_columns_of_a_unique_required_index_shares_its_tree()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[] { Long("Id"), Text("Name") }, primaryKey: null,
                                   new[] { new IndexDefinition("Unique", "Id") { IsUnique = true, IsRequired = true } });
        for (int i = 1; i <= 10; i++) table.Insert(new Row { ["Id"] = i, ["Name"] = $"name {i}" });

        int pages = db.File.PageCount;
        var key = table.AddIndex(Key("Id"));
        Assert.Equal(table.Indexes.Single(ix => ix.Name == "Unique").IndexDataNumber, key.IndexDataNumber);
        Assert.Equal(pages, db.File.PageCount);
        Assert.Equal("name 4", table.FindRowByPrimaryKey(4)!["Name"]);
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 10));
    }

    [Fact]
    public void A_relationship_can_refer_to_a_primary_key_that_was_added()
    {
        using var db = CreateWithRows(10);
        var parent = db.GetTable("T");
        var child = db.CreateTable("Child", new[] { Long("Id"), Long("T") }, primaryKey: "Id");

        Assert.Throws<InvalidOperationException>(() => db.CreateRelationship(
            new RelationshipDefinition("T", "Id", "Child", "T") { HasReferentialIntegrity = true }));
        parent.AddIndex(Key("Id"));
        db.CreateRelationship(new RelationshipDefinition("T", "Id", "Child", "T") { HasReferentialIntegrity = true });

        child.Insert(new Row { ["Id"] = 1, ["T"] = 3 });
        Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 2, ["T"] = 99 }));
        Assert.Throws<ConstraintViolationException>(() => parent.DeleteRow("Id", 3));
    }

    [Fact]
    public void A_primary_key_whose_writing_fails_at_any_page_leaves_the_file_as_it_was()
    {
        CreateWithRows(300).Dispose();
        byte[] before = File.ReadAllBytes(_path);
        int failures = 0;
        for (int writes = 0; ; writes++)
        {
            Assert.True(writes < 5_000, "the change never finished");
            using var db = Database.Open(_path);
            var table = db.GetTable("T");
            db.File.WritesBeforeFailure = writes;
            try
            {
                table.AddIndex(Key("Id"));
                db.File.WritesBeforeFailure = null;
                break;
            }
            catch (IOException)
            {
                db.File.WritesBeforeFailure = null;
                failures++;
            }
            Assert.DoesNotContain(table.Indexes, ix => ix.IsPrimaryKey);
            db.Dispose();
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), $"the file changed when write {writes + 1} of the change failed");
        }
        // The key's tree of 300 entries is written page after page (#82): its leaves, its root, its usage map and the
        // definition are more than five writes.
        Assert.True(failures > 5, $"only {failures} writes failed: the key's leaves, root, usage map and definition are more");

        using var after = Database.Open(_path);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "T", expectedRows: 300));
    }
}
