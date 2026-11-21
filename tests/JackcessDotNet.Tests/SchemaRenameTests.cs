using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A column, an index and a table are renamed (#37), each as DAO was seen to rename one: a column in its table's
/// definition, in the relationships it is part of and in the properties the catalog keeps for it; an index in the
/// definition, its slot moved to where the new name sorts; a table in the catalog and in its relationships. A
/// name Access refuses is refused, and a refused change leaves the file as it was. What Access makes of the
/// result is in SchemaChangesInAccessTests.
/// </summary>
public sealed class SchemaRenameTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"schema_rename_{Guid.NewGuid():N}.mdb");
    private readonly string _accdb = Path.Combine(Path.GetTempPath(), $"schema_rename_{Guid.NewGuid():N}.accdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_accdb)) File.Delete(_accdb);
    }

    private static ColumnDefinition Long(string name) => new ColumnDefinition(name, DataType.Int32);
    private static ColumnDefinition Text(string name) => new ColumnDefinition(name, DataType.Text) { MaxLength = 20 };

    /// <summary>
    /// Parent (K, Name), keyed by K; Child (Id, K, Name), keyed by Id, with indexes on Name and on K; and the
    /// relationships ParentChild, with referential integrity, and Loose, without, on the two Name columns.
    /// </summary>
    private Database Create()
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var child = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Name") },
                                   new[] { "Id" }, new[] { new IndexDefinition("ByName", "Name"), new IndexDefinition("ByK", "K") });
        for (int k = 1; k <= 5; k++) parent.Insert(new Row { ["K"] = k, ["Name"] = $"parent {k}" });
        for (int i = 1; i <= 50; i++) child.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1, ["Name"] = $"child {i}" });
        db.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K") { Name = "ParentChild", HasReferentialIntegrity = true, CascadeUpdates = true });
        db.CreateRelationship(new RelationshipDefinition("Parent", "Name", "Child", "Name") { Name = "Loose" });
        return db;
    }

    private void AssertRefusedAndUnchanged<TException>(Action<Database> change, string? path = null) where TException : Exception
    {
        path ??= _path;
        byte[] before = File.ReadAllBytes(path);
        using (var db = Database.Open(path))
        {
            var refused = Assert.Throws<TException>(() => change(db));
            Assert.Contains("Nothing was changed", refused.Message);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private int FailAtEveryWrite(Action<Database> change)
    {
        byte[] before = File.ReadAllBytes(_path);
        for (int writes = 0; ; writes++)
        {
            Assert.True(writes < 1_000, "the change never finished");
            using (var db = Database.Open(_path))
            {
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
                Assert.Equal(new[] { "Parent", "Child" }, db.GetTableNames());
                Assert.Equal(new[] { "Id", "K", "Name" }, db.GetTable("Child").Columns.Select(c => c.Name));
            }
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), $"the file changed when write {writes + 1} of the change failed");
        }
    }

    // ── A column ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_column_that_is_renamed_has_its_new_name_in_the_table_and_in_its_relationships()
    {
        using (var db = Create())
        {
            var child = db.GetTable("Child");
            child.RenameColumn("k", "ParentKey");
            child.RenameColumn("Name", "Title");
            db.GetTable("Parent").RenameColumn("K", "Key");

            Assert.Equal(new[] { "Id", "ParentKey", "Title" }, child.Columns.Select(c => c.Name));
            Assert.Equal("child 7", child.FindRowByPrimaryKey(7)!["Title"]);
            Assert.False(child.FindRowByPrimaryKey(7)!.ContainsKey("Name"));

            // Its indexes know it by its number: they are as they were, and found by its new name.
            Assert.Equal(new[] { "ParentKey" }, child.Indexes.Single(ix => ix.Name == "ByK").Columns.Select(c => c.Column.Name));
            Assert.Equal(7, child.CreateIndexCursor("ByName").FindRow("Title", "child 7")!["Id"]);

            var related = db.GetRelationships().ToDictionary(r => r.Name);
            Assert.Equal(new[] { "Key" }, related["ParentChild"].FromColumnNames);
            Assert.Equal(new[] { "ParentKey" }, related["ParentChild"].ToColumnNames);
            Assert.Equal(new[] { "Name" }, related["Loose"].FromColumnNames);
            Assert.Equal(new[] { "Title" }, related["Loose"].ToColumnNames);

            // The relationship is kept under the new names: an orphan refused, a cascade made.
            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 900, ["ParentKey"] = 99 }));
            db.GetTable("Parent").UpdateByPrimaryKey(2, new Row { ["Key"] = 20 });
            Assert.Equal(10, child.Rows.Count(r => Equals(r["ParentKey"], 20)));
            Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 50));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysRelationships", expectedRows: 2);
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(new[] { "Id", "ParentKey", "Title" }, reopened.GetTable("Child").Columns.Select(c => c.Name));
        Assert.Equal(new[] { "Key", "Name" }, reopened.GetTable("Parent").Columns.Select(c => c.Name));
        Assert.Equal(new[] { "ParentKey" }, reopened.GetRelationships().Single(r => r.Name == "ParentChild").ToColumnNames);
    }

    [Fact]
    public void A_column_name_Access_refuses_is_refused_and_one_that_differs_only_in_case_is_taken()
    {
        Create().Dispose();
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").RenameColumn("Nothing", "Something"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").RenameColumn("Name", "K"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").RenameColumn("Name", "id"));
        foreach (string name in new[] { "", "   ", "a.b", "wow!", "gra`ve", "[x]", " lead", "tab\tbed", new string('n', 65) })
        {
            byte[] before = File.ReadAllBytes(_path);
            using (var db = Database.Open(_path))
                Assert.Throws<ArgumentException>(() => db.GetTable("Child").RenameColumn("Name", name));
            Assert.Equal(before, File.ReadAllBytes(_path));
        }

        using var open = Database.Open(_path);
        var child = open.GetTable("Child");
        child.RenameColumn("Name", "NAME");
        child.RenameColumn("K", new string('n', 64));
        child.RenameColumn("Id", "trailing ");
        Assert.Equal(new[] { "trailing ", new string('n', 64), "NAME" }, child.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "NAME" }, open.GetRelationships().Single(r => r.Name == "Loose").ToColumnNames);

        // The name it has changes nothing at all.
        var pages = IndexChecks.AllPages(open);
        child.RenameColumn("NAME", "NAME");
        IndexChecks.AssertUnchanged(pages, open);
    }

    [Fact]
    public void A_column_s_properties_follow_its_new_name_and_go_when_it_is_dropped()
    {
        // A table Access made with two columns, in no index, that have properties of their own.
        static List<Column> Candidates(Table t) => t.Columns
            .Where(c => c.Properties.Count > 0 && c.DataType != DataType.Complex && !t.Indexes.Any(ix => ix.Columns.Any(ic => ic.Column.Name == c.Name)))
            .Take(2).ToList();
        string? source = TestCorpus.Files("*.mdb", "V2003", "V2000").FirstOrDefault(file =>
        {
            using var db = Database.Open(file);
            return db.GetRelationships().Count == 0 && db.GetTableNames().Select(db.GetTable).Any(t => Candidates(t).Count == 2);
        });
        Assert.True(source is not null || TestCorpus.Root is null, "no table of the corpus has two columns with properties");
        if (source is null) return;
        File.Copy(source, _path, overwrite: true);

        using var db = Database.Open(_path);
        var table = db.GetTableNames().Select(db.GetTable).First(t => Candidates(t).Count == 2);
        var withProperties = Candidates(table);
        string renamed = withProperties[0].Name, dropped = withProperties[1].Name;
        var properties = withProperties[0].Properties.Select(p => $"{p.Name}={p.Value}").ToList();
        var others = table.Columns.Where(c => c.Name != renamed && c.Name != dropped).ToDictionary(c => c.Name, c => c.Properties.Select(p => $"{p.Name}={p.Value}").ToList());
        var ofTable = table.Properties.Select(p => $"{p.Name}={p.Value}").ToList();
        int maps = table.PropertyMaps.Count;

        table.RenameColumn(renamed, "RenamedByTheEngine");
        Assert.Equal(properties, table.Columns.Single(c => c.Name == "RenamedByTheEngine").Properties.Select(p => $"{p.Name}={p.Value}"));
        Assert.Null(table.PropertyMaps.Get(renamed));
        Assert.Equal(maps, table.PropertyMaps.Count);

        table.DropColumn(dropped);
        Assert.Null(table.PropertyMaps.Get(dropped));
        Assert.Equal(maps - 1, table.PropertyMaps.Count);

        // Every other column's, and the table's own, are as they were.
        foreach (var column in table.Columns.Where(c => c.Name != "RenamedByTheEngine"))
            Assert.Equal(others[column.Name], column.Properties.Select(p => $"{p.Name}={p.Value}"));
        Assert.Equal(ofTable, table.Properties.Select(p => $"{p.Name}={p.Value}"));
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysObjects");
    }

    // ── An index ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_index_that_is_renamed_is_found_by_its_new_name_where_that_name_sorts()
    {
        using (var db = Create())
        {
            var child = db.GetTable("Child");
            child.RenameIndex("byname", "Zebra");
            child.RenameIndex("PrimaryKey", "AKey");

            // Each where its new name sorts among the others': before them all, and after them all.
            Assert.Equal(new[] { "AKey", "ByK", "ParentChild", "Zebra" }, child.Indexes.Select(ix => ix.Name).OrderBy(n => n));
            Assert.Equal("AKey", child.Indexes[0].Name);
            Assert.Equal("Zebra", child.Indexes[^1].Name);
            Assert.True(child.Indexes.Single(ix => ix.Name == "AKey").IsPrimaryKey);
            Assert.Equal(7, child.CreateIndexCursor("Zebra").FindRow("Name", "child 7")!["Id"]);
            Assert.Throws<ArgumentException>(() => child.CreateIndexCursor("ByName"));
            Assert.Equal("child 7", child.FindRowByPrimaryKey(7)!["Name"]);

            // The relationship's index still names the other side's by its number: it is kept as before.
            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 900, ["K"] = 99 }));
            child.Insert(new Row { ["Id"] = 51, ["K"] = 1, ["Name"] = "after" });
            Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 51));
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(new[] { "AKey", "ByK", "ParentChild", "Zebra" }, reopened.GetTable("Child").Indexes.Select(ix => ix.Name).OrderBy(n => n));
    }

    [Fact]
    public void An_index_name_Access_refuses_is_refused_and_so_is_a_new_name_for_a_relationship_s_index()
    {
        Create().Dispose();
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").RenameIndex("Nothing", "Something"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").RenameIndex("ByName", "byk"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").RenameIndex("ByName", ".rB"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Child").RenameIndex("ByName", new string('n', 65)));
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Child").RenameIndex("ParentChild", "Other"));
        AssertRefusedAndUnchanged<InvalidOperationException>(db => db.GetTable("Parent").RenameIndex(db.GetTable("Parent").Indexes.Single(ix => ix.IsForeignKey).Name, "Other"));
    }

    // ── A table ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_table_that_is_renamed_has_its_new_name_in_the_catalog_and_in_its_relationships()
    {
        using (var db = Create())
        {
            var child = db.GetTable("Child");
            db.RenameTable("child", "Children");
            db.RenameTable("Parent", "Parents");

            Assert.Equal(new[] { "Parents", "Children" }, db.GetTableNames().OrderByDescending(n => n));
            Assert.Equal("Children", child.Name);
            Assert.Same(child, db.GetTable("Children"));
            Assert.False(db.TryGetTable("Child", out _));
            Assert.All(db.GetRelationships(), r => Assert.Equal(("Parents", "Children"), (r.FromTableName, r.ToTableName)));

            // The relationship is kept under the new names, and the catalog's index on names holds every row.
            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 900, ["K"] = 99 }));
            db.GetTable("Parents").UpdateByPrimaryKey(2, new Row { ["K"] = 20 });
            Assert.Equal(10, child.Rows.Count(r => Equals(r["K"], 20)));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysObjects");
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysRelationships", expectedRows: 2);

            // The old name is free: a table can take it.
            db.CreateTable("Child", new[] { Long("Id") });
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(new[] { "Child", "Children", "Parents" }, reopened.GetTableNames().OrderBy(n => n));
        Assert.Equal(50, reopened.GetTable("Children").Rows.Count());
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Children", expectedRows: 50));
    }

    [Fact]
    public void A_table_name_Access_refuses_is_refused_and_one_that_differs_only_in_case_is_taken()
    {
        Create().Dispose();
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable("Nothing", "Something"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable("Child", "parent"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable("Child", "MSysObjects"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable("Child", "a[b"));
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable("Child", new string('n', 65)));
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.RenameTable("MSysRelationships", "Mine"));
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.GetTable("MSysObjects").RenameColumn("Flags", "Flagged"));
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.GetTable("MSysObjects").RenameIndex("ParentIdName", "Other"));

        using var open = Database.Open(_path);
        open.RenameTable("Child", "CHILD");
        Assert.Equal(new[] { "Parent", "CHILD" }, open.GetTableNames());
        Assert.Equal("CHILD", open.GetRelationships()[0].ToTableName);
        var pages = IndexChecks.AllPages(open);
        open.RenameTable("CHILD", "CHILD");
        IndexChecks.AssertUnchanged(pages, open);
    }

    // A table is renamed by what it is, not by its name. In Assets/dao-odd-names.accdb, made with DAO 16, are tables
    // Access keeps apart that .NET takes as one without regard to case - x and the micro sign, x and the Greek mu - and
    // tables named x and a compatibility ideograph and x and the ideograph it decomposes to; a relationship from each
    // of the first two. A table is refused the name another has, however the two compare, with the file unchanged; and
    // a rename changes the relationships of the table renamed, and of no other.
    [Fact]
    public void A_table_is_refused_the_name_another_has_and_a_rename_changes_its_relationships_alone()
    {
        File.Copy(ProtectedFileTests.Asset("dao-odd-names.accdb"), _accdb);
        const string micro = "xµ", mu = "xμ";
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable(micro, mu), _accdb);
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable(mu, micro), _accdb);
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable("x豈", "x豈"), _accdb);
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable("x豈", "x豈"), _accdb);
        AssertRefusedAndUnchanged<ArgumentException>(db => db.RenameTable("F_FDD0", "f_ffff"), _accdb);

        using (var db = Database.Open(_accdb))
        {
            db.RenameTable(micro, "Micro");
            db.RenameTable("Micro", "MICRO");   // in another case: the table's own name
        }

        using var reopened = Database.Open(_accdb);
        var names = reopened.GetTableNames();
        Assert.Contains("MICRO", names);
        Assert.Contains(mu, names);
        Assert.DoesNotContain(micro, names);
        Assert.Contains("x豈", names);
        Assert.Contains("x豈", names);
        var related = reopened.GetRelationships().ToDictionary(r => r.Name);
        Assert.Equal(("MICRO", "F_FFFE"), (related["MicroFFFE"].FromTableName, related["MicroFFFE"].ToTableName));
        Assert.Equal((mu, "F_FDD0"), (related["MuFDD0"].FromTableName, related["MuFDD0"].ToTableName));
        IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "MSysObjects");
    }

    [Fact]
    public void A_rename_that_fails_at_any_page_leaves_the_file_as_it_was()
    {
        Create().Dispose();
        int column = FailAtEveryWrite(db => db.GetTable("Child").RenameColumn("Name", "Title"));
        Assert.True(column >= 2, $"only {column} writes failed: the definition and a row of MSysRelationships are at least two");

        using (var db = Database.Open(_path)) db.GetTable("Child").RenameColumn("Title", "Name");
        int table = FailAtEveryWrite(db => db.RenameTable("Child", "Children"));
        Assert.True(table >= 4, $"only {table} writes failed: the catalog's row, its index on names and two rows of MSysRelationships are more");
        using var after = Database.Open(_path);
        Assert.Equal(new[] { "Parent", "Children" }, after.GetTableNames());
        IndexChecks.AssertEveryIndexHoldsTheRows(after, "MSysObjects");
    }

    [Fact]
    public void A_required_column_is_required_under_its_new_name_and_a_dropped_one_leaves_no_properties()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Rules", new[]
            {
                Long("Id"),
                new ColumnDefinition("Name", DataType.Text) { MaxLength = 20, IsRequired = true, AllowZeroLength = false },
                new ColumnDefinition("Code", DataType.Text) { MaxLength = 20, IsRequired = true },
            });

            // A change to the table's properties that is not saved is not written with the rename, and is still to save.
            table.Properties.Set("Description", "not saved yet");
            table.RenameColumn("Name", "Title");
            table.DropColumn("Code");

            var title = table.Columns.Single(c => c.Name == "Title");
            Assert.True(title.IsRequired);
            Assert.False(title.AllowZeroLength);
            Assert.Null(table.PropertyMaps.Get("Name"));
            Assert.Null(table.PropertyMaps.Get("Code"));
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 1 }));
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 1, ["Title"] = "" }));
            table.Insert(new Row { ["Id"] = 1, ["Title"] = "one" });

            Assert.True(table.PropertyMaps.IsDirty);
            var inFile = PropertyMapReader.Read(db.Catalog.FindObject(table.Definition.TdefPageNumber)!.Value.Properties, db.File.Format);
            Assert.False(inFile.Default.TryGetValue("Description", out _));
            Assert.NotNull(inFile.Get("Title"));
            table.Properties.Save();
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Rules");
        Assert.Equal(new[] { "Id", "Title" }, read.Columns.Select(c => c.Name));
        Assert.True(read.Columns[1].IsRequired);
        Assert.False(read.Columns[1].AllowZeroLength);
        Assert.Equal("not saved yet", read.Properties["Description"]);
        Assert.Null(read.PropertyMaps.Get("Name"));
        Assert.Null(read.PropertyMaps.Get("Code"));
        Assert.Throws<ConstraintViolationException>(() => read.Insert(new Row { ["Id"] = 2 }));

        // A column later given a dropped column's name has none of its properties.
        var code = read.AddColumn(new ColumnDefinition("Code", DataType.Text) { MaxLength = 20 });
        Assert.False(code.IsRequired);
        read.Insert(new Row { ["Id"] = 2, ["Title"] = "two" });
    }
}
