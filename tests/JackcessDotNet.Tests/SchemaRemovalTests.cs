using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// An index, a column, a relationship and a table are dropped (#37), each as DAO was seen to drop one: out of the
/// table's definition, its pages given back to the file in the global usage map, its usage maps' rows taken off
/// their page, its rows out of the system tables. What Access refuses is refused, a refused change leaves the
/// file as it was, and one that fails while it is written is taken back. What Access makes of the result is in
/// SchemaChangesInAccessTests.
/// </summary>
public sealed class SchemaRemovalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"schema_removal_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ColumnDefinition Long(string name) => new ColumnDefinition(name, DataType.Int32);
    private static ColumnDefinition Text(string name) => new ColumnDefinition(name, DataType.Text) { MaxLength = 20 };
    private static ColumnDefinition Memo(string name) => new ColumnDefinition(name, DataType.Memo);

    /// <summary>
    /// Parent (K, Name), keyed by K, with five rows; Child (Id, K, Name, Notes, Extra), keyed by Id, with indexes
    /// on Name and on K and three hundred rows whose Notes are long values; and, when asked, the relationship
    /// ParentChild between them, with referential integrity.
    /// </summary>
    private Database Create(bool related = false)
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var child = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Name"), Memo("Notes"), Long("Extra") },
                                   new[] { "Id" }, new[] { new IndexDefinition("ByName", "Name"), new IndexDefinition("ByK", "K") });
        for (int k = 1; k <= 5; k++) parent.Insert(new Row { ["K"] = k, ["Name"] = $"parent {k}" });
        for (int i = 1; i <= 300; i++)
            child.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1, ["Name"] = $"child {i}", ["Notes"] = new string((char)('a' + i % 26), 900), ["Extra"] = i * 2 });
        if (related)
            db.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K") { Name = "ParentChild", HasReferentialIntegrity = true });
        return db;
    }

    private void AssertRefusedAndUnchanged<TException>(Action<Database> change) where TException : Exception
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var refused = Assert.Throws<TException>(() => change(db));
            Assert.Contains("Nothing was changed", refused.Message);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    /// <summary>
    /// Makes <paramref name="change"/> fail at its first page write, then its second, and so on until it has the
    /// writes it needs, and returns how many it failed at. After every failure the file is what it was.
    /// </summary>
    private int FailAtEveryWrite(Action<Database> change)
    {
        byte[] before = File.ReadAllBytes(_path);
        for (int writes = 0; ; writes++)
        {
            Assert.True(writes < 5_000, "the change never finished");
            using (var db = Database.Open(_path))
            {
                var tables = db.GetTableNames().ToList();
                var child = db.GetTable("Child");
                var columns = child.Columns.Select(c => c.Name).ToList();
                var indexes = child.Indexes.Select(ix => ix.Name).ToList();
                int relationships = db.GetRelationships().Count;

                db.File.WritesBeforeFailure = writes;
                try
                {
                    change(db);
                    db.File.WritesBeforeFailure = null;
                    return writes;
                }
                catch (IOException)
                {
                    db.File.WritesBeforeFailure = null;
                }

                // Still open: the tables are to the library what they were.
                Assert.Equal(before.Length, db.File.Length);
                Assert.Equal(tables, db.GetTableNames());
                Assert.Equal(columns, child.Columns.Select(c => c.Name));
                Assert.Equal(indexes, child.Indexes.Select(ix => ix.Name));
                Assert.Equal(relationships, db.GetRelationships().Count);
            }
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), $"the file changed when write {writes + 1} of the change failed");
        }
    }

    private static List<int> PagesOfTree(Database db, string table, string index)
    {
        var block = JetPages.IndexBlocks(db.File, db.GetTable(table).Definition.TdefPageNumber)[db.GetTable(table).Indexes.Single(ix => ix.Name == index).IndexDataNumber];
        return UsageMap.GetOwnedPages(db.File.ReadPage(block.UmapPage), block.UmapRow, db.File.Format, db.File);
    }

    // ── An index ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_index_that_is_dropped_leaves_the_definition_and_gives_its_pages_back()
    {
        using (var db = Create())
        {
            var child = db.GetTable("Child");
            var tree  = PagesOfTree(db, "Child", "ByName");
            Assert.NotEmpty(tree);
            Assert.All(tree, page => Assert.False(JetPages.IsFree(db.File, page)));
            var byK = PagesOfTree(db, "Child", "ByK");

            child.DropIndex("byname");

            Assert.Equal(new[] { "PrimaryKey", "ByK" }, child.Indexes.OrderBy(ix => ix.IndexNumber).Select(ix => ix.Name));
            Assert.Throws<ArgumentException>(() => child.CreateIndexCursor("ByName"));
            Assert.All(tree, page => Assert.True(JetPages.IsFree(db.File, page), $"page {page} of the dropped index's tree is not free"));
            Assert.All(byK, page => Assert.False(JetPages.IsFree(db.File, page)));

            // The index after it has its block's number, and both go on being kept.
            Assert.Equal(new[] { 0, 1 }, child.Indexes.Select(ix => ix.IndexDataNumber).OrderBy(n => n));
            child.Insert(new Row { ["Id"] = 301, ["K"] = 2, ["Name"] = "after" });
            child.DeleteRow("Id", 7);
            Assert.Equal(301, child.CreateIndexCursor("ByK").FindRow("Id", 301)!["Id"]);
            Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 300));
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(2, reopened.GetTable("Child").Indexes.Count);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Child", expectedRows: 300));

        // And an index of the name can be made again.
        reopened.GetTable("Child").AddIndex(new IndexDefinition("ByName", "Name"));
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Child", expectedRows: 300));
    }

    [Fact]
    public void A_primary_key_that_is_dropped_is_the_table_s_key_no_longer()
    {
        using var db = Create();
        var child = db.GetTable("Child");
        child.DropIndex("PrimaryKey");

        Assert.DoesNotContain(child.Indexes, ix => ix.IsPrimaryKey);
        Assert.Throws<InvalidOperationException>(() => child.FindRowByPrimaryKey(7));
        child.Insert(new Row { ["Id"] = 7, ["Name"] = "a second 7" });
        Assert.Equal(2, child.Rows.Count(r => Equals(r["Id"], 7)));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 301));
    }

    [Fact]
    public void An_index_that_shares_a_tree_leaves_the_tree_to_the_other()
    {
        using var db = Create();
        var child = db.GetTable("Child");
        child.AddIndex(new IndexDefinition("AlsoByName", "Name"));
        Assert.Equal(child.Indexes.Single(ix => ix.Name == "ByName").IndexDataNumber, child.Indexes.Single(ix => ix.Name == "AlsoByName").IndexDataNumber);
        var tree = PagesOfTree(db, "Child", "ByName");

        child.DropIndex("ByName");
        Assert.All(tree, page => Assert.False(JetPages.IsFree(db.File, page)));
        Assert.Equal("child 42", child.CreateIndexCursor("AlsoByName").FindRow("Name", "child 42")!["Name"]);
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 300));

        child.DropIndex("AlsoByName");
        Assert.All(tree, page => Assert.True(JetPages.IsFree(db.File, page)));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 300));
    }

    [Fact]
    public void An_index_that_is_not_there_or_that_a_relationship_needs_is_not_dropped()
    {
        Create(related: true).Dispose();
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").DropIndex("Nothing"));
        // The relationship's own index on each table, and the key it refers to the parent's rows by.
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Child").DropIndex("ParentChild"));
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Parent").DropIndex(db.GetTable("Parent").Indexes.Single(ix => ix.IsForeignKey).Name));
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Parent").DropIndex("PrimaryKey"));

        // The index whose tree the relationship's shares on the child is dropped, as Access drops it: the tree stays.
        using var open = Database.Open(_path);
        var child = open.GetTable("Child");
        var tree = PagesOfTree(open, "Child", "ByK");
        child.DropIndex("ByK");
        Assert.All(tree, page => Assert.False(JetPages.IsFree(open.File, page)));
        Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 900, ["K"] = 99 }));
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(open, "Child", expectedRows: 300));
    }

    [Fact]
    public void An_index_whose_dropping_fails_at_any_page_leaves_the_file_as_it_was()
    {
        Create().Dispose();
        int failures = FailAtEveryWrite(db => db.GetTable("Child").DropIndex("ByName"));
        Assert.True(failures >= 3, $"only {failures} writes failed: the global usage map, the usage map's page and the definition are at least three");
        using var after = Database.Open(_path);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "Child", expectedRows: 300));
    }

    // ── A column ─────────────────────────────────────────────────────────────

    [Fact]
    public void Columns_that_are_dropped_leave_the_rows_readable_by_the_columns_that_are_left()
    {
        using (var db = Create())
        {
            var child = db.GetTable("Child");
            var lvalPages = child.Definition.LvalColumnUmapPages["Notes"];
            var notes = UsageMap.GetOwnedPages(db.File.ReadPage(lvalPages.OwnedPage), lvalPages.OwnedRow, db.File.Format, db.File);
            Assert.True(notes.Count > 50, $"only {notes.Count} pages of long values");

            child.DropColumn("extra");
            child.DropColumn("Notes");
            Assert.Equal(new[] { "Id", "K", "Name" }, child.Columns.Select(c => c.Name));

            // The long values' pages, and the page their usage maps were on, are the file's again.
            Assert.All(notes, page => Assert.True(JetPages.IsFree(db.File, page), $"long-value page {page} is not free"));
            Assert.True(JetPages.IsFree(db.File, lvalPages.OwnedPage));
            Assert.Empty(child.Definition.LvalColumnUmapPages);

            // The rows read as they were for the columns that are left, and new ones are written and read.
            var row = child.FindRowByPrimaryKey(42)!;
            Assert.Equal(new[] { "Id", "K", "Name" }, row.Keys);
            Assert.Equal("child 42", row["Name"]);
            child.Insert(new Row { ["Id"] = 301, ["K"] = 1, ["Name"] = "after" });
            child.UpdateByPrimaryKey(42, new Row { ["Name"] = "changed" });
            Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 301));

            // A column added afterwards has a number no row was written with, and reads null in them all.
            child.AddColumn(Long("Later"));
            child.AddColumn(Memo("LaterNotes"));
            Assert.All(child.Rows, r => Assert.Null(r["Later"]));
            child.Insert(new Row { ["Id"] = 302, ["Later"] = 5, ["LaterNotes"] = new string('z', 5000) });
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Child");
        Assert.Equal(new[] { "Id", "K", "Name", "Later", "LaterNotes" }, read.Columns.Select(c => c.Name));
        Assert.Equal(302, read.Rows.Count());
        Assert.Equal("changed", read.FindRowByPrimaryKey(42)!["Name"]);
        Assert.Equal(new string('z', 5000), read.FindRowByPrimaryKey(302)!["LaterNotes"]);
        Assert.Null(read.InsertRefusal);
    }

    [Fact]
    public void A_column_that_is_not_there_or_is_in_an_index_or_a_relationship_or_is_the_last_is_not_dropped()
    {
        using (var db = Create(related: true))
        {
            db.CreateTable("One", new[] { Long("Only") }).Insert(new Row { ["Only"] = 1 });
            db.CreateRelationship(new RelationshipDefinition("Parent", "Name", "Child", "Name") { Name = "Loose" });
            db.GetTable("Child").DropIndex("ByName");
        }

        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").DropColumn("Nothing"));
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Child").DropColumn("Id"));      // its primary key
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Child").DropColumn("K"));       // the foreign key
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Parent").DropColumn("K"));      // the key referred to
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Child").DropColumn("Name"));    // a relationship without integrity
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Parent").DropColumn("Name"));
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.GetTable("One").DropColumn("Only"));
    }

    [Fact]
    public void A_long_value_column_whose_dropping_fails_at_any_page_leaves_the_file_as_it_was()
    {
        Create().Dispose();
        int failures = FailAtEveryWrite(db => db.GetTable("Child").DropColumn("Notes"));
        Assert.True(failures >= 3, $"only {failures} writes failed");
        using var after = Database.Open(_path);
        Assert.Equal(300, after.GetTable("Child").Rows.Count());
        Assert.DoesNotContain(after.GetTable("Child").Columns, c => c.Name == "Notes");
    }

    // A definition that was on several pages and is then shorter: the pages it no longer reaches go back to the file.
    [Fact]
    public void A_definition_that_grows_shorter_gives_back_the_pages_it_no_longer_reaches()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var columns = new List<ColumnDefinition> { Long("Id") };
        columns.AddRange(Enumerable.Range(0, 90).Select(n => Long(MultiPageDefinitionTests.LongName(n))));
        var table = db.CreateTable("Wide", columns, primaryKey: "Id");
        table.Insert(new Row { ["Id"] = 1, [MultiPageDefinitionTests.LongName(0)] = 10, [MultiPageDefinitionTests.LongName(89)] = 890 });
        var pages = MultiPageDefinitionTests.PagesOf(db, "Wide");
        Assert.Equal(4, pages.Count);

        for (int n = 89; n >= 1; n--)
        {
            table.DropColumn(MultiPageDefinitionTests.LongName(n));
            MultiPageDefinitionTests.AssertLaidOutAsAccessDoes(db, "Wide");
        }
        var left = MultiPageDefinitionTests.PagesOf(db, "Wide");
        Assert.Equal(new[] { pages[0] }, left);
        Assert.All(pages.Skip(1), page => Assert.True(JetPages.IsFree(db.File, page), $"definition page {page} is not free"));
        Assert.False(JetPages.IsFree(db.File, pages[0]));
        Assert.Equal(10, table.FindRowByPrimaryKey(1)![MultiPageDefinitionTests.LongName(0)]);
        table.Insert(new Row { ["Id"] = 2, [MultiPageDefinitionTests.LongName(0)] = 20 });
        Assert.Equal(2, table.Rows.Count());
    }

    // A dropped column keeps its number, and a row's null mask has a bit for each number up to 254: a table that
    // has had 255 columns takes no more, however few it has. Access refuses the next too ("Too many fields
    // defined"), with the definition's count of columns ever made at 255, until the file is compacted.
    [Fact]
    public void A_table_that_has_had_255_columns_takes_no_more_though_most_were_dropped()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", new[] { Long("Id"), Long("Keep") }, primaryKey: "Id");
            table.Insert(new Row { ["Id"] = 1, ["Keep"] = 10 });
            for (int i = 0; i < 253; i++)
            {
                table.AddColumn(i % 2 == 0 ? Long($"X{i}") : Text($"X{i}"));
                table.DropColumn($"X{i}");
            }
            Assert.Equal(255, table.Definition.MaxColumnCount);
            Assert.Equal(new[] { "Id", "Keep" }, table.Columns.Select(c => c.Name));

            var pages = IndexChecks.AllPages(db);
            foreach (var column in new[] { Long("Again"), Text("AgainText"), new ColumnDefinition("AgainFlag", DataType.Boolean) })
            {
                var refused = Assert.Throws<ArgumentException>(() => table.AddColumn(column));
                Assert.Contains("has had 255 columns", refused.Message);
                Assert.Contains("compacted", refused.Message);
            }
            IndexChecks.AssertUnchanged(pages, db);

            // It is a table like any other still.
            table.Insert(new Row { ["Id"] = 2, ["Keep"] = 20 });
            Assert.Equal(20, table.FindRowByPrimaryKey(2)!["Keep"]);
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(255, reopened.GetTable("T").Definition.MaxColumnCount);
        Assert.Throws<ArgumentException>(() => reopened.GetTable("T").AddColumn(Long("Again")));
        Assert.Equal(2, reopened.GetTable("T").Rows.Count());
    }

    // An index takes the lowest number no index of its table has, as Access numbers one: the number of an index
    // that was dropped is given again, and the index a relationship has on its referenced table is named by it.
    [Fact]
    public void An_index_is_given_the_lowest_number_free_which_a_dropped_index_leaves()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[] { Long("Id"), Long("Keep"), Long("B"), Long("Y") }, primaryKey: "Id");
        var child = db.CreateTable("C", new[] { Long("Id"), Long("K") }, primaryKey: "Id");
        Assert.Equal(1, table.AddIndex(new IndexDefinition("ixA", "Keep")).IndexNumber);
        Assert.Equal(2, table.AddIndex(new IndexDefinition("ixB", "B")).IndexNumber);
        Assert.Equal(3, table.AddIndex(new IndexDefinition("ixC", "Y")).IndexNumber);

        table.DropIndex("ixA");
        Assert.Equal(1, table.AddIndex(new IndexDefinition("ixD", "Keep")).IndexNumber);
        table.DropIndex("ixB");
        db.CreateRelationship(new RelationshipDefinition("T", "Id", "C", "K") { Name = "TC", HasReferentialIntegrity = true });
        var referenced = table.Indexes.Single(ix => ix.IsForeignKey);
        Assert.Equal(2, referenced.IndexNumber);
        Assert.Equal(".rC", referenced.Name);

        // However many come and go, the numbers stay small.
        for (int i = 0; i < 100; i++)
        {
            table.AddIndex(new IndexDefinition($"ix{i}", "B"));
            table.DropIndex($"ix{i}");
        }
        Assert.Equal(new[] { 0, 1, 2, 3 }, table.Indexes.Select(ix => ix.IndexNumber).OrderBy(n => n));
        child.Insert(new Row { ["Id"] = 1, ["K"] = null });
        Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 2, ["K"] = 5 }));
    }

    // ── A relationship ───────────────────────────────────────────────────────

    [Fact]
    public void A_relationship_that_is_dropped_is_gone_from_the_system_tables_and_from_both_definitions()
    {
        using (var db = Create(related: true))
        {
            var child  = db.GetTable("Child");
            var parent = db.GetTable("Parent");
            int catalogRows = db.GetTable("MSysObjects").RowCount, aceRows = db.GetTable("MSysACEs").RowCount;
            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 900, ["K"] = 99 }));

            db.DropRelationship("parentchild");

            Assert.Empty(db.GetRelationships());
            Assert.Empty(db.GetTable("MSysRelationships").Rows);
            Assert.Equal(catalogRows - 1, db.GetTable("MSysObjects").RowCount);
            Assert.True(db.GetTable("MSysACEs").RowCount < aceRows);
            Assert.DoesNotContain(db.GetTable("MSysObjects").Rows, r => Equals(r["Name"], "ParentChild"));
            Assert.DoesNotContain(child.Indexes, ix => ix.IsForeignKey);
            Assert.DoesNotContain(parent.Indexes, ix => ix.IsForeignKey);
            Assert.Equal(new[] { "ByK", "ByName", "PrimaryKey" }, child.Indexes.Select(ix => ix.Name).OrderBy(n => n));

            // Nothing is enforced any more, and the indexes that are left hold every row.
            child.Insert(new Row { ["Id"] = 900, ["K"] = 99 });
            parent.DeleteRow("K", 1);
            Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 301));
            Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Parent", expectedRows: 4));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysObjects");
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysRelationships", expectedRows: 0);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysACEs");
        }

        using var reopened = Database.Open(_path);
        Assert.Empty(reopened.GetRelationships());
        // And it can be made again.
        reopened.GetTable("Child").DeleteRow("Id", 900);
        reopened.GetTable("Parent").Insert(new Row { ["K"] = 1, ["Name"] = "back" });
        reopened.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K") { Name = "ParentChild", HasReferentialIntegrity = true });
        Assert.Single(reopened.GetRelationships());
    }

    [Fact]
    public void A_relationship_s_own_tree_goes_with_it_and_one_without_integrity_has_none()
    {
        using var db = Create();
        var child = db.GetTable("Child");
        child.DropIndex("ByK");   // so the relationship builds a tree of its own on the foreign key
        db.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K") { Name = "ParentChild", HasReferentialIntegrity = true });
        db.CreateRelationship(new RelationshipDefinition("Parent", "Name", "Child", "Name") { Name = "Loose" });
        var tree = PagesOfTree(db, "Child", "ParentChild");
        Assert.NotEmpty(tree);

        db.DropRelationship("ParentChild");
        Assert.All(tree, page => Assert.True(JetPages.IsFree(db.File, page), $"page {page} of the relationship's tree is not free"));
        Assert.Equal("Loose", Assert.Single(db.GetRelationships()).Name);

        db.DropRelationship("Loose");
        Assert.Empty(db.GetRelationships());
        Assert.Throws<ArgumentException>(() => db.DropRelationship("Loose"));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 300));
    }

    [Fact]
    public void A_relationship_of_a_table_to_itself_is_dropped_with_both_its_indexes()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var staff = db.CreateTable("Staff", new[] { Long("Id"), Long("Manager") }, primaryKey: "Id");
        staff.Insert(new Row { ["Id"] = 1 });
        staff.Insert(new Row { ["Id"] = 2, ["Manager"] = 1 });
        db.CreateRelationship(new RelationshipDefinition("Staff", "Id", "Staff", "Manager") { Name = "Reports", HasReferentialIntegrity = true });
        Assert.Equal(3, staff.Indexes.Count);

        db.DropRelationship("Reports");
        Assert.Equal(new[] { "PrimaryKey" }, staff.Indexes.Select(ix => ix.Name));
        staff.Insert(new Row { ["Id"] = 3, ["Manager"] = 99 });
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Staff", expectedRows: 3));
    }

    [Fact]
    public void A_relationship_whose_dropping_fails_at_any_page_leaves_the_file_as_it_was()
    {
        Create(related: true).Dispose();
        int failures = FailAtEveryWrite(db => db.DropRelationship("ParentChild"));
        Assert.True(failures >= 8, $"only {failures} writes failed: rows of three system tables, their indexes and two definitions are more");
        using var after = Database.Open(_path);
        Assert.Empty(after.GetRelationships());
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(after, "Child", expectedRows: 300));
        IndexChecks.AssertEveryIndexHoldsTheRows(after, "MSysObjects");
    }

    // ── A table ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_table_that_is_dropped_is_gone_from_the_catalog_with_its_relationships_and_gives_every_page_back()
    {
        using (var db = Create(related: true))
        {
            var child = db.GetTable("Child");
            int tdef = child.Definition.TdefPageNumber;
            var owned = Enumerable.Range(0, db.File.PageCount).Where(page =>
            {
                byte[] bytes = db.File.ReadPage(page);
                return (bytes[0] is JetFormat.PageTypeData or JetFormat.PageTypeIndexLeaf or JetFormat.PageTypeIndexNode) && ByteUtil.GetInt(bytes, 4) == tdef;
            }).ToList();
            Assert.True(owned.Count > 5, $"only {owned.Count} pages of the table's rows and indexes were found");
            var lval = child.Definition.LvalColumnUmapPages["Notes"];
            var notes = UsageMap.GetOwnedPages(db.File.ReadPage(lval.OwnedPage), lval.OwnedRow, db.File.Format, db.File);
            int umap = child.Definition.UmapPageNumber;

            db.DropTable("child");

            Assert.Equal(new[] { "Parent" }, db.GetTableNames());
            Assert.False(db.TryGetTable("Child", out _));
            Assert.Throws<InvalidOperationException>(() => child.Rows.Count());
            Assert.Throws<InvalidOperationException>(() => child.Insert(new Row { ["Id"] = 1 }));
            Assert.Empty(db.GetRelationships());
            Assert.DoesNotContain(db.GetTable("Parent").Indexes, ix => ix.IsForeignKey);
            Assert.DoesNotContain(db.GetTable("MSysObjects").Rows, r => Equals(r["Name"], "Child") || Equals(r["Name"], "ParentChild"));
            Assert.DoesNotContain(db.GetTable("MSysACEs").Rows, r => Equals(r["ObjectId"], tdef));

            // Every page it had is the file's again, and its definition's first page is no definition page.
            foreach (int page in owned.Concat(notes).Append(tdef).Append(umap).Append(lval.OwnedPage))
                Assert.True(JetPages.IsFree(db.File, page), $"page {page} of the dropped table is not free");
            Assert.Equal(TdefBuffer.PageTypeDroppedTableDef, db.File.ReadPage(tdef)[0]);

            // The parent is as it was, and a table of the name can be made again.
            db.GetTable("Parent").DeleteRow("K", 3);
            Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Parent", expectedRows: 4));
            var again = db.CreateTable("Child", new[] { Long("Id") }, primaryKey: "Id");
            again.Insert(new Row { ["Id"] = 1 });
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysObjects");
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysACEs");
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(new[] { "Parent", "Child" }, reopened.GetTableNames());
        Assert.Single(reopened.GetTable("Child").Rows);
        Assert.Empty(reopened.GetRelationships());
    }

    [Fact]
    public void A_table_whose_definition_is_on_several_pages_gives_them_all_back()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var columns = new List<ColumnDefinition> { Long("Id") };
        columns.AddRange(Enumerable.Range(0, 90).Select(n => Long(MultiPageDefinitionTests.LongName(n))));
        db.CreateTable("Wide", columns, primaryKey: "Id").Insert(new Row { ["Id"] = 1 });
        var pages = MultiPageDefinitionTests.PagesOf(db, "Wide");
        Assert.Equal(4, pages.Count);

        db.DropTable("Wide");
        Assert.Empty(db.GetTableNames());
        Assert.All(pages, page => Assert.True(JetPages.IsFree(db.File, page), $"definition page {page} is not free"));
        Assert.Equal(TdefBuffer.PageTypeDroppedTableDef, db.File.ReadPage(pages[0])[0]);
    }

    [Fact]
    public void A_table_that_is_not_there_or_is_the_database_s_own_or_that_another_refers_to_is_not_dropped()
    {
        Create(related: true).Dispose();
        AssertRefusedAndUnchanged<ArgumentException>(db => db.DropTable("Nothing"));
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.DropTable("MSysObjects"));
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.GetTable("MSysObjects").DropColumn("Flags"));
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.GetTable("MSysObjects").DropIndex("ParentIdName"));
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.DropTable("Parent"));

        // Once nothing refers to it, it goes.
        using var open = Database.Open(_path);
        open.DropRelationship("ParentChild");
        open.DropTable("Parent");
        Assert.Equal(new[] { "Child" }, open.GetTableNames());
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(open, "Child", expectedRows: 300));
    }

    [Fact]
    public void A_table_related_to_itself_is_dropped_with_the_relationship()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var staff = db.CreateTable("Staff", new[] { Long("Id"), Long("Manager") }, primaryKey: "Id");
        staff.Insert(new Row { ["Id"] = 1 });
        db.CreateRelationship(new RelationshipDefinition("Staff", "Id", "Staff", "Manager") { Name = "Reports", HasReferentialIntegrity = true });

        db.DropTable("Staff");
        Assert.Empty(db.GetTableNames());
        Assert.Empty(db.GetRelationships());
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysObjects");
    }

    [Fact]
    public void A_table_whose_dropping_fails_at_any_page_leaves_the_file_as_it_was()
    {
        Create(related: true).Dispose();
        int failures = FailAtEveryWrite(db => db.DropTable("Child"));
        Assert.True(failures >= 20, $"only {failures} writes failed");
        using var after = Database.Open(_path);
        Assert.Equal(new[] { "Parent" }, after.GetTableNames());
        Assert.Empty(after.GetRelationships());
        IndexChecks.AssertEveryIndexHoldsTheRows(after, "MSysObjects");
    }

    // ── A usage map's row ────────────────────────────────────────────────────

    // As Access was seen to take a usage map's row off its page: the rows after it move up over it, its own entry
    // is left at where it ended with both deleted flags, the free space grows, and the rows keep their numbers.
    [Fact]
    public void A_usage_map_s_row_is_taken_off_its_page_as_Access_takes_one()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var format = db.File.Format;
        int page = db.Allocator.AllocateUmapPage(rows: 4);
        for (int row = 0; row < 4; row++) UsageMap.AddPage(db.File, db.Allocator, page, row, 100 + row);
        byte[] before = db.File.ReadPage(page);
        int length = UsageMap.GetRowLength(before, 1, format);
        int free = ByteUtil.GetShort(before, JetFormat.OffsetDataFreeSpace);

        Assert.False(UsageMap.DeleteRow(db.File, page, 1));

        byte[] after = db.File.ReadPage(page);
        Assert.Equal(4, ByteUtil.GetShort(after, format.OffsetDataNumRows));
        Assert.Equal(free + length, ByteUtil.GetShort(after, JetFormat.OffsetDataFreeSpace));
        Assert.Equal(0xC000 | UsageMap.GetRowStart(before, 0, format), ByteUtil.GetUShort(after, format.OffsetDataRowTable + 2));
        Assert.Equal(UsageMap.GetRowStart(before, 2, format) + length, UsageMap.GetRowStart(after, 2, format));
        Assert.Equal(UsageMap.GetRowStart(before, 3, format) + length, UsageMap.GetRowStart(after, 3, format));
        Assert.False(UsageMap.IsLiveRow(after, 1, format));
        foreach (int row in new[] { 0, 2, 3 })
            Assert.Equal(new[] { 100 + row }, UsageMap.GetOwnedPages(after, row, format, db.File));

        // The rest go the same way, and the page that holds none is said to.
        Assert.False(UsageMap.DeleteRow(db.File, page, 3));
        Assert.False(UsageMap.DeleteRow(db.File, page, 0));
        Assert.Equal(new[] { 102 }, UsageMap.GetOwnedPages(db.File.ReadPage(page), 2, format, db.File));
        Assert.True(UsageMap.DeleteRow(db.File, page, 2));
        Assert.Equal(format.PageSize - format.OffsetDataRowTable - 4 * JetFormat.SizeRowEntry, ByteUtil.GetShort(db.File.ReadPage(page), JetFormat.OffsetDataFreeSpace));
    }
}
