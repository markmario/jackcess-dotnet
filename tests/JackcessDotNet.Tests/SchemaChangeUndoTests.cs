using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A change to the schema that fails while it is being written is taken back whole
/// (qcatco/exportmdb.interop#37). Everything that can refuse a change is found before a page is written, so only
/// the file can fail after that: here it is made to, at every page write a change makes in turn
/// (<see cref="PageFile.WritesBeforeFailure"/>), and after each failure the file is byte for byte what it was,
/// its tables are as they were to the library, and the same change then goes through.
/// </summary>
public sealed class SchemaChangeUndoTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"schema_undo_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();
    private static Column Text(string name) => new ColumnBuilder(name, DataType.Text).MaxLength(20).Build();

    private void Create()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var child = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Name") }, primaryKey: "Id");
        for (int k = 1; k <= 5; k++) parent.Insert(new Row { ["K"] = k, ["Name"] = $"parent {k}" });
        for (int i = 1; i <= 300; i++) child.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1, ["Name"] = $"child {i}" });
    }

    /// <summary>
    /// Makes <paramref name="change"/> fail at its first page write, then its second, and so on until it has
    /// enough writes to finish, and returns how many writes it failed at. After every failure the file is what it
    /// was before the change.
    /// </summary>
    private int FailAtEveryWrite(Action<Database> change)
    {
        Create();
        byte[] before = File.ReadAllBytes(_path);
        for (int writes = 0; ; writes++)
        {
            Assert.True(writes < 5_000, "the change never finished");
            using (var db = Database.Open(_path))
            {
                var parentIndexes = db.GetTable("Parent").Indexes.Select(ix => ix.Name).ToList();
                var child = db.GetTable("Child");
                var childColumns = child.Columns.Select(c => c.Name).ToList();
                var childIndexes = child.Indexes.Select(ix => ix.Name).ToList();

                db.File.WritesBeforeFailure = writes;
                try
                {
                    change(db);
                    db.File.WritesBeforeFailure = null;
                    return writes;   // it had the writes it needed
                }
                catch (IOException)
                {
                    db.File.WritesBeforeFailure = null;
                }

                // Still open: the pages are back, and the tables are to the library what they were.
                Assert.Equal(before.Length, db.File.Length);
                Assert.Equal(childColumns, child.Columns.Select(c => c.Name));
                Assert.Equal(childIndexes, child.Indexes.Select(ix => ix.Name));
                Assert.Equal(parentIndexes, db.GetTable("Parent").Indexes.Select(ix => ix.Name));
                Assert.Empty(db.GetRelationships());
            }
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), $"the file changed when write {writes + 1} of the change failed");
        }
    }

    [Fact]
    public void A_long_value_column_whose_writing_fails_at_any_page_leaves_the_file_as_it_was()
    {
        int failures = FailAtEveryWrite(db => db.GetTable("Child").AddColumn(new ColumnBuilder("Notes", DataType.Memo).Build()));

        Assert.True(failures >= 2, $"only {failures} writes failed: the column's usage maps and the definition are at least two");
        using var after = Database.Open(_path);
        var child = after.GetTable("Child");
        child.Insert(new Row { ["Id"] = 900, ["K"] = 1, ["Notes"] = new string('n', 6_000) });
        Assert.Equal(new string('n', 6_000), child.NewIndexCursor().FindRowByPrimaryKey(900)!["Notes"]);
    }

    [Fact]
    public void An_index_whose_writing_fails_at_any_page_leaves_the_file_as_it_was()
    {
        int failures = FailAtEveryWrite(db => db.GetTable("Child").AddIndex(new IndexDefinition("ByName", "Name") { IsUnique = true }));

        Assert.True(failures > 300, $"only {failures} writes failed: the index has an entry to write for each of 300 rows");
        using var after = Database.Open(_path);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "Child", expectedRows: 300));
    }

    [Fact]
    public void A_relationship_whose_writing_fails_at_any_page_leaves_the_file_as_it_was()
    {
        int failures = FailAtEveryWrite(db => db.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K")
        {
            Name = "ParentChild", HasReferentialIntegrity = true, CascadeDeletes = true,
        }));

        Assert.True(failures > 300, $"only {failures} writes failed: the foreign key's index has an entry to write for each of 300 rows");
        using var after = Database.Open(_path);
        Assert.Equal("ParentChild", Assert.Single(after.GetRelationships()).Name);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "Child", expectedRows: 300));
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(after, "Parent", expectedRows: 5));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "MSysObjects"));
    }
}
