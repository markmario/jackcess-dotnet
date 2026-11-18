using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A relationship is created between two tables (qcatco/exportmdb.interop#37): its rows in MSysRelationships, its
/// entry in the catalog and - with referential integrity - the index Access keeps on each side, written as Access
/// was seen to write them. The library then enforces it (<see cref="Database.EnforceForeignKeys"/>). One the rows
/// do not allow, or that cannot be written, is refused with the file byte for byte as it was. What Access makes of
/// the relationship is in ExportMdb.Interop's SchemaChangesInAccessTests.
/// </summary>
public sealed class CreateRelationshipTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"create_rel_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ColumnDefinition Long(string name) => new ColumnDefinition(name, DataType.Int32);
    private static ColumnDefinition Text(string name, int chars = 20) => new ColumnDefinition(name, DataType.Text) { MaxLength = chars };

    /// <summary>
    /// Parent(K, Code, Name) keyed by K, Code unique; Child(Id, K, Code, Name) keyed by Id: parents 1 to 5, and
    /// forty children that refer to them by K and by Code, two of them referring to nothing.
    /// </summary>
    private Database ParentAndChild(params IndexDefinition[] childIndexes)
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Code"), Text("Name") }, new[] { "K" },
                                    new[] { new IndexDefinition("ByCode", "Code") { IsUnique = true } });
        var child = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Code"), Text("Name") }, new[] { "Id" }, childIndexes);
        for (int k = 1; k <= 5; k++)
            parent.Insert(new Row { ["K"] = k, ["Code"] = $"p{k}", ["Name"] = $"parent {k}" });
        for (int i = 1; i <= 40; i++)
            child.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1, ["Code"] = $"P{i % 5 + 1}", ["Name"] = $"child {i}" });
        child.Insert(new Row { ["Id"] = 41, ["Name"] = "no parent" });
        child.Insert(new Row { ["Id"] = 42, ["Name"] = "no parent either" });
        return db;
    }

    private static RelationshipDefinition ByK(string? name = "ParentChild") => new("Parent", "K", "Child", "K")
    {
        Name = name, HasReferentialIntegrity = true,
    };

    /// <summary>One index slot of a table's definition, as the page holds it.</summary>
    private sealed record Slot(string Name, int Number, int Data, byte OtherTableType, int OtherIndex, int OtherTable,
                               byte CascadeUpdates, byte CascadeDeletes, byte Type);

    private static List<Slot> Slots(Database db, string table)
    {
        byte[] p = db.File.ReadPage(IndexChecks.TdefOf(db, table));
        int columns = ByteUtil.GetShort(p, 45), slots = ByteUtil.GetInt(p, 47), blocks = ByteUtil.GetInt(p, 51);
        int pos = 63 + blocks * 12 + columns * 25;
        for (int i = 0; i < columns; i++) pos += 2 + ByteUtil.GetShort(p, pos);
        pos += blocks * 52;
        int names = pos + slots * 28;
        var result = new List<Slot>();
        for (int i = 0; i < slots; i++)
        {
            int s = pos + i * 28 + 4;
            int length = ByteUtil.GetShort(p, names);
            string name = System.Text.Encoding.Unicode.GetString(p, names + 2, length);
            names += 2 + length;
            result.Add(new Slot(name, ByteUtil.GetInt(p, s), ByteUtil.GetInt(p, s + 4), p[s + 8], ByteUtil.GetInt(p, s + 9),
                                ByteUtil.GetInt(p, s + 13), p[s + 17], p[s + 18], p[s + 19]));
        }
        return result;
    }

    [Fact]
    public void A_relationship_with_referential_integrity_is_listed_and_has_an_index_on_each_side()
    {
        int highest;
        using (var db = ParentAndChild())
        {
            // The ids of the objects that have no page: a new database has some already.
            highest = JetPages.Rows(db.File, JetFormat.PageSystemCatalog).Select(r => r.Values["Id"]).OfType<int>().Where(id => id < 0).Max();
            var created = db.CreateRelationship(new RelationshipDefinition("parent", "k", "child", "k")
            {
                Name = "ParentChild", HasReferentialIntegrity = true, CascadeUpdates = true, CascadeDeletes = true,
            });
            Assert.Equal("ParentChild: Parent(K) → Child(K)", created.ToString());   // names as the tables have them
        }

        using var reopened = Database.Open(_path);
        var relationship = Assert.Single(reopened.GetRelationships());
        Assert.Equal("ParentChild", relationship.Name);
        Assert.Equal(("Parent", "Child"), (relationship.FromTableName, relationship.ToTableName));
        Assert.Equal(new[] { "K" }, relationship.FromColumnNames);
        Assert.Equal(new[] { "K" }, relationship.ToColumnNames);
        Assert.Equal(0x1100, relationship.Flags);
        Assert.True(relationship.HasReferentialIntegrity && relationship.CascadeUpdates && relationship.CascadeDeletes);
        Assert.False(relationship.CascadeNullOnDelete || relationship.IsOneToOne);
        Assert.Equal(JoinType.Inner, relationship.JoinType);

        // The catalog lists it as Access lists a relationship: Type 8, in the Relationships container, with the
        // next of the ids Access gives objects that have no page.
        var catalog = JetPages.Rows(reopened.File, JetFormat.PageSystemCatalog).Select(r => r.Values).ToList();
        var container = catalog.Single(r => (string?)r["Name"] == "Relationships" && Convert.ToInt32(r["Type"]) == 3);
        var entry = catalog.Single(r => (string?)r["Name"] == "ParentChild");
        Assert.Equal(8, Convert.ToInt32(entry["Type"]));
        Assert.Equal(container["Id"], entry["ParentId"]);
        Assert.Equal(highest + 1, entry["Id"]);
        Assert.Equal(0, entry["Flags"]);
        Assert.Equal(catalog.Single(r => (string?)r["Name"] == "Parent")["Owner"], entry["Owner"]);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "MSysObjects"));

        // The referenced table: an index named .rC (it is the table's third) that shares the primary key's tree
        // and names the other side's index, its table and the cascades.
        var parent = reopened.GetTable("Parent");
        var child  = reopened.GetTable("Child");
        var primary = parent.Indexes.Single(ix => ix.Name == ".rC");
        Assert.True(primary.IsForeignKey && primary.IsUnique);
        Assert.Equal(parent.Indexes.Single(ix => ix.IsPrimaryKey).RootPageNumber, primary.RootPageNumber);
        var secondary = child.Indexes.Single(ix => ix.Name == "ParentChild");
        Assert.True(secondary.IsForeignKey && !secondary.IsUnique);
        Assert.Equal("K ASC", string.Join(", ", secondary.Columns));

        var primarySlot   = Slots(reopened, "Parent").Single(s => s.Name == ".rC");
        var secondarySlot = Slots(reopened, "Child").Single(s => s.Name == "ParentChild");
        Assert.Equal(new Slot(".rC", 2, 0, 1, secondarySlot.Number, IndexChecks.TdefOf(reopened, "Child"), 1, 1, 2), primarySlot);
        Assert.Equal(new Slot("ParentChild", 1, 1, 2, primarySlot.Number, IndexChecks.TdefOf(reopened, "Parent"), 1, 1, 2), secondarySlot);
        // Access keeps a table's slots in the order of their names.
        Assert.Equal(new[] { ".rC", "ByCode", "PrimaryKey" }, Slots(reopened, "Parent").Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(".rC", Slots(reopened, "Parent")[0].Name);

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Parent", expectedRows: 5));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Child", expectedRows: 42));
    }

    [Fact]
    public void The_library_enforces_a_relationship_it_created()
    {
        using var db = ParentAndChild();
        db.EnforceForeignKeys = true;
        var child = db.GetTable("Child");
        child.Insert(new Row { ["Id"] = 100, ["K"] = 99, ["Name"] = "allowed until then" });
        child.DeleteRow("Id", 100);

        db.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K")
        {
            Name = "ParentChild", HasReferentialIntegrity = true, CascadeUpdates = true, CascadeDeletes = true,
        });

        var refused = Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 100, ["K"] = 99 }));
        Assert.Contains("ParentChild", refused.Message);
        child.Insert(new Row { ["Id"] = 101, ["K"] = 3 });
        child.Insert(new Row { ["Id"] = 102 });   // refers to nothing

        var parent = db.GetTable("Parent");
        parent.UpdateByPrimaryKey(3, new Row { ["K"] = 30 });
        parent.DeleteRow("K", 1);

        var rows = child.Rows.ToList();
        Assert.Equal(9, rows.Count(r => Equals(r.GetValueOrDefault("K"), 30)));   // 8 children of 3, and row 101
        Assert.DoesNotContain(rows, r => Equals(r.GetValueOrDefault("K"), 3) || Equals(r.GetValueOrDefault("K"), 1));
        Assert.Equal(44 - 8, rows.Count);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 36));
    }

    [Fact]
    public void Rows_written_after_the_relationship_are_in_its_index()
    {
        using var db = ParentAndChild();
        var child = db.GetTable("Child");   // opened before the relationship: it keeps the new index too
        db.CreateRelationship(ByK());

        for (int i = 200; i < 900; i++) child.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1 });
        for (int i = 200; i < 900; i += 3) child.UpdateByPrimaryKey(i, new Row { ["K"] = (i + 1) % 5 + 1 });
        for (int i = 201; i < 900; i += 3) child.DeleteRow("Id", i);

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 42 + 700 - 233));
    }

    [Fact]
    public void A_relationship_shares_a_plain_index_on_its_foreign_key()
    {
        // An index on the foreign key that ignores nulls comes first: it is passed over for the plain one.
        using var db = ParentAndChild(new IndexDefinition("IgnoringNulls", "K") { IgnoreNulls = true }, new IndexDefinition("ByK", "K"));
        var child = db.GetTable("Child");
        int byK = child.Indexes.Single(ix => ix.Name == "ByK").RootPageNumber;

        db.CreateRelationship(ByK());
        Assert.Equal(byK, child.Indexes.Single(ix => ix.Name == "ParentChild").RootPageNumber);
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 42));
    }

    // As Access was seen to do: the foreign key's index shares no tree but a plain index's.
    [Fact]
    public void A_relationship_builds_its_own_index_when_the_one_on_its_foreign_key_is_not_plain()
    {
        using var db = ParentAndChild(new IndexDefinition("IgnoringNulls", "K") { IgnoreNulls = true });
        var child = db.GetTable("Child");
        int ignoring = child.Indexes.Single(ix => ix.Name == "IgnoringNulls").RootPageNumber;

        db.CreateRelationship(ByK());
        var foreignKey = child.Indexes.Single(ix => ix.Name == "ParentChild");
        Assert.NotEqual(ignoring, foreignKey.RootPageNumber);
        Assert.False(foreignKey.IgnoreNulls);
        // The rows with no K have an entry in the relationship's index, and none in the one that ignores nulls.
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 42));
    }

    [Fact]
    public void A_relationship_between_columns_that_are_each_unique_is_one_to_one_and_its_index_is_unique()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        db.CreateTable("One", new[] { Long("Id"), Text("Name") }, primaryKey: "Id");
        var other = db.CreateTable("Other", new[] { Long("Id"), Long("One"), Long("Also") }, new[] { "Id" }, new[]
        {
            new IndexDefinition("UniqueOne", "One") { IsUnique = true },
            new IndexDefinition("UniqueAlso", "Also") { IsUnique = true, IgnoreNulls = true },
        });
        for (int i = 1; i <= 3; i++) db.GetTable("One").Insert(new Row { ["Id"] = i });
        other.Insert(new Row { ["Id"] = 2, ["One"] = 3, ["Also"] = 1 });

        var relationship = db.CreateRelationship(new RelationshipDefinition("One", "Id", "Other", "One") { HasReferentialIntegrity = true });

        Assert.Equal("OneOther", relationship.Name);   // no name given: the tables' names, as Access's window names one
        Assert.True(relationship.IsOneToOne);
        Assert.Equal(0x1, relationship.Flags);
        // As Access makes it with that flag: the foreign key's index is unique, and shares the tree of the index
        // that is unique and nothing more.
        var foreignKey = other.Indexes.Single(ix => ix.Name == "OneOther");
        Assert.True(foreignKey.IsUnique);
        Assert.Equal(other.Indexes.Single(ix => ix.Name == "UniqueOne").RootPageNumber, foreignKey.RootPageNumber);

        // An index that is unique and ignores nulls is not shared: the foreign key gets a unique tree of its own.
        var also = db.CreateRelationship(new RelationshipDefinition("One", "Id", "Other", "Also") { Name = "AlsoOne", HasReferentialIntegrity = true });
        Assert.True(also.IsOneToOne);
        var alsoKey = other.Indexes.Single(ix => ix.Name == "AlsoOne");
        Assert.True(alsoKey.IsUnique && !alsoKey.IgnoreNulls);
        Assert.NotEqual(other.Indexes.Single(ix => ix.Name == "UniqueAlso").RootPageNumber, alsoKey.RootPageNumber);
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Other", expectedRows: 1));

        db.EnforceForeignKeys = true;
        Assert.Throws<ConstraintViolationException>(() => other.Insert(new Row { ["Id"] = 3, ["One"] = 3, ["Also"] = 2 }));   // One is unique
        other.Insert(new Row { ["Id"] = 4, ["One"] = 2, ["Also"] = 2 });
    }

    [Fact]
    public void A_relationship_without_referential_integrity_is_only_recorded()
    {
        using var db = ParentAndChild();
        var parentIndexes = db.GetTable("Parent").Indexes.Select(ix => ix.Name).ToList();
        var childIndexes  = db.GetTable("Child").Indexes.Select(ix => ix.Name).ToList();

        // The children refer to codes by another case, and two to nothing: nothing is checked.
        var relationship = db.CreateRelationship(new RelationshipDefinition("Parent", "Name", "Child", "Name") { JoinType = JoinType.LeftOuter });
        var right = db.CreateRelationship(new RelationshipDefinition("Parent", "Name", "Child", "Name") { JoinType = JoinType.RightOuter });

        Assert.Equal(0x01000002, relationship.Flags);
        Assert.Equal(0x02000002, right.Flags);
        Assert.Equal(new[] { "ParentChild", "ParentChild1" }, db.GetRelationships().Select(r => r.Name));
        Assert.Equal(parentIndexes, db.GetTable("Parent").Indexes.Select(ix => ix.Name));
        Assert.Equal(childIndexes, db.GetTable("Child").Indexes.Select(ix => ix.Name));
        Assert.All(new[] { "Parent", "Child" }, table => Assert.All(Slots(db, table), slot => Assert.Equal(0, slot.OtherTableType)));

        db.EnforceForeignKeys = true;
        db.GetTable("Child").Insert(new Row { ["Id"] = 500, ["Name"] = "no parent's name" });
    }

    [Fact]
    public void A_relationship_of_two_columns_is_recorded_a_row_for_each_and_indexed_on_both()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var region = db.CreateTable("Region", new[] { Long("Id"), Text("Country", 2), Long("Code") }, new[] { "Id" },
                                    new[] { new IndexDefinition("ByCountryAndCode", "Country", "Code") { IsUnique = true } });
        var city = db.CreateTable("City", new[] { Long("Id"), Text("Country", 2), Long("Code") }, primaryKey: "Id");
        region.Insert(new Row { ["Id"] = 1, ["Country"] = "NZ", ["Code"] = 1 });
        region.Insert(new Row { ["Id"] = 2, ["Country"] = "AU", ["Code"] = 1 });
        city.Insert(new Row { ["Id"] = 1, ["Country"] = "nz", ["Code"] = 1 });   // text is matched as its index holds it
        city.Insert(new Row { ["Id"] = 2, ["Country"] = "AU", ["Code"] = 1 });

        var definition = new RelationshipDefinition("Region", new[] { "Country", "Code" }, "City", new[] { "Country", "Code" })
        {
            Name = "RegionCity", HasReferentialIntegrity = true, CascadeNullOnDelete = true,
        };
        db.CreateRelationship(definition);

        var rows = db.GetTable("MSysRelationships").Rows.ToList();
        Assert.Equal(new[] { "0/2 Country>Country", "1/2 Code>Code" },
                     rows.Select(r => $"{r["icolumn"]}/{r["ccolumn"]} {r["szReferencedColumn"]}>{r["szColumn"]}"));
        Assert.All(rows, r => Assert.Equal(0x2000, r["grbit"]));
        Assert.Equal("Country ASC, Code ASC", string.Join(", ", city.Indexes.Single(ix => ix.Name == "RegionCity").Columns));
        Assert.Equal(region.Indexes.Single(ix => ix.Name == "ByCountryAndCode").RootPageNumber,
                     region.Indexes.Single(ix => ix.IsForeignKey).RootPageNumber);
        Assert.Equal(new Slot("RegionCity", 1, 1, 2, 2, IndexChecks.TdefOf(db, "Region"), 0, 2, 2), Slots(db, "City").Single(s => s.Name == "RegionCity"));

        db.EnforceForeignKeys = true;
        Assert.Throws<ConstraintViolationException>(() => city.Insert(new Row { ["Id"] = 3, ["Country"] = "AU", ["Code"] = 2 }));
        region.DeleteRow("Id", 2);
        Assert.Null(city.FindRowByPrimaryKey(2)!["Country"]);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "City", expectedRows: 2));
    }

    [Fact]
    public void A_table_can_be_related_to_itself()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var staff = db.CreateTable("Staff", new[] { Long("Id"), Long("Manager"), Text("Name") }, primaryKey: "Id");
        staff.Insert(new Row { ["Id"] = 1, ["Name"] = "the top" });
        staff.Insert(new Row { ["Id"] = 2, ["Manager"] = 1 });
        staff.Insert(new Row { ["Id"] = 3, ["Manager"] = 3 });   // a row may refer to itself

        db.CreateRelationship(new RelationshipDefinition("Staff", "Id", "Staff", "Manager") { Name = "Reports", HasReferentialIntegrity = true });

        // As DAO numbers them in one table: the foreign key's index first, then the referenced side's, which is
        // named by its own number - .rC, the table's third index.
        var slots = Slots(db, "Staff");
        var primary = slots.Single(s => s.Name == ".rC");
        var secondary = slots.Single(s => s.Name == "Reports");
        int tdef = IndexChecks.TdefOf(db, "Staff");
        Assert.Equal(new Slot(".rC", 2, 0, 1, 1, tdef, 0, 0, 2), primary);
        Assert.Equal(new Slot("Reports", 1, 1, 2, 2, tdef, 0, 0, 2), secondary);
        Assert.Equal(3, slots.Select(s => s.Name).Distinct().Count());
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Staff", expectedRows: 3));

        db.EnforceForeignKeys = true;
        Assert.Throws<ConstraintViolationException>(() => staff.Insert(new Row { ["Id"] = 4, ["Manager"] = 9 }));
        Assert.Throws<ConstraintViolationException>(() => staff.DeleteRow("Id", 1));
    }

    // indexV2003.mdb's Table1 refers to Table2 and Table3 already; its otherfk3 refers to nothing yet.
    [Theory]
    [InlineData("V2003", "indexV2003.mdb")]
    [InlineData("V2010", "indexV2010.accdb")]
    public void A_relationship_is_created_between_tables_Access_made_beside_the_ones_Access_made(string version, string file)
    {
        string? source = TestCorpus.File(version, file);
        if (source is null) return;   // the corpus is optional locally; CI always has it
        File.Copy(source, _path, overwrite: true);

        using (var db = Database.Open(_path))
        {
            var table1 = db.GetTable("Table1");
            int third = (int)db.GetTable("Table3").Rows.ToList()[0]["id"]!;
            foreach (var row in table1.Rows.ToList())
                table1.UpdateByPrimaryKey(row["id"]!, new Row { ["otherfk3"] = third });   // every row refers to Table3's row 1...
            int highest = JetPages.Rows(db.File, JetFormat.PageSystemCatalog).Select(r => r.Values["Id"]).OfType<int>().Where(id => id < 0).Max();

            db.CreateRelationship(new RelationshipDefinition("Table3", "id", "Table1", "otherfk3")
            {
                Name = "Third", HasReferentialIntegrity = true, CascadeUpdates = true,
            });

            Assert.Equal(new[] { "Table2Table1", "Table3Table1", "Third" }, db.GetRelationships().Select(r => r.Name));
            Assert.Equal(highest + 1, JetPages.Rows(db.File, JetFormat.PageSystemCatalog).Single(r => (string?)r.Values["Name"] == "Third").Values["Id"]);
            // Table3 has .rC for the relationship Access made; this one's index is its fourth, .rD.
            Assert.Equal(new[] { ".rC", ".rD", "id", "PrimaryKey" }, db.GetTable("Table3").Indexes.Select(ix => ix.Name));
            Assert.Equal(new[] { "id", "PrimaryKey", "Table2Table1", "Table3Table1", "Third" }, db.GetTable("Table1").Indexes.Select(ix => ix.Name));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table1", expectedRows: 4);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table3", expectedRows: 4);
        }

        using var reopened = Database.Open(_path);
        reopened.EnforceForeignKeys = true;
        var referring = reopened.GetTable("Table1").Rows.ToList();
        Assert.Throws<ConstraintViolationException>(() => reopened.GetTable("Table1").UpdateByPrimaryKey(referring[0]["id"]!, new Row { ["otherfk3"] = 999 }));
        // Table3's row is referred to by the relationship Access made as well, which cascades its key too.
        reopened.GetTable("Table3").UpdateByPrimaryKey(referring[0]["otherfk3"]!, new Row { ["id"] = 77 });
        Assert.All(reopened.GetTable("Table1").Rows.ToList(), row => Assert.Equal(77, row["otherfk3"]));
        IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Table1", expectedRows: 4);
    }

    // Access takes no name with a period, an exclamation mark, a grave accent or a square bracket in it, nor one
    // that begins with a space: so no relationship can have the name Access gives the index on a referenced
    // table (.rB, .rC...). Given ".rB" for a table related to itself, whose referenced side's index would be
    // named so, the library used to write two indexes of that name into the one table.
    [Theory]
    [InlineData(".rB")]
    [InlineData(".rC")]
    [InlineData("a.b")]
    [InlineData("wow!")]
    [InlineData("[bracketed]")]
    [InlineData("gra`ve")]
    [InlineData(" leading")]
    public void A_relationship_with_a_name_Access_does_not_allow_is_refused_and_nothing_changes(string name)
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var staff = db.CreateTable("Staff", new[] { Long("Id"), Long("Manager"), Text("Name") }, primaryKey: "Id");
            staff.Insert(new Row { ["Id"] = 1 });
            staff.Insert(new Row { ["Id"] = 2, ["Manager"] = 1 });
        }

        foreach (bool enforced in new[] { true, false })
            AssertRefusedAndUnchanged<ArgumentException>(
                new RelationshipDefinition("Staff", "Id", "Staff", "Manager") { Name = name, HasReferentialIntegrity = enforced }, "not a valid name");

        using var again = Database.Open(_path);
        Assert.Equal(new[] { "PrimaryKey" }, again.GetTable("Staff").Indexes.Select(ix => ix.Name));
    }

    // The name the library makes when none is given is of the tables' names: when they hold what a name may not,
    // a name has to be given.
    [Fact]
    public void A_relationship_between_tables_whose_names_make_no_valid_name_needs_a_name()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("Par.ent", new[] { Long("K") }, primaryKey: "K");
            db.CreateTable("Child", new[] { Long("Id"), Long("K") }, primaryKey: "Id");
        }
        AssertRefusedAndUnchanged<ArgumentException>(new RelationshipDefinition("Par.ent", "K", "Child", "K") { HasReferentialIntegrity = true }, "not a valid name");

        using var again = Database.Open(_path);
        again.CreateRelationship(new RelationshipDefinition("Par.ent", "K", "Child", "K") { Name = "ParentChild", HasReferentialIntegrity = true });
        Assert.Equal(new[] { ".rB", "PrimaryKey" }, again.GetTable("Par.ent").Indexes.Select(ix => ix.Name));
    }

    // Two relationships made one after another to the same table: each referenced-side index is named by its number.
    [Fact]
    public void Relationships_made_one_after_another_give_the_referenced_table_indexes_of_different_names()
    {
        using var db = ParentAndChild();
        db.CreateRelationship(ByK("First"));
        db.CreateRelationship(new RelationshipDefinition("Parent", "Code", "Child", "Code") { Name = "Second", HasReferentialIntegrity = true });
        db.CreateRelationship(ByK("Third"));

        // PrimaryKey 0 and ByCode 1 were there: .rC, .rD and .rE follow.
        Assert.Equal(new[] { ".rC 2", ".rD 3", ".rE 4", "ByCode 1", "PrimaryKey 0" },
                     db.GetTable("Parent").Indexes.Select(ix => $"{ix.Name} {ix.IndexNumber}").OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Parent", expectedRows: 5));
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 42));
    }

    // Access allows no index a name like .rC, and nor does AddIndex; a table made by CreateTable, which checks no
    // names, can have one. The name made for the referenced side's index passes over it.
    [Fact]
    public void The_name_made_for_the_referenced_side_passes_over_an_index_that_has_it()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, new[] { "K" }, new[] { new IndexDefinition(".rC", "Name") });
        db.CreateTable("Child", new[] { Long("Id"), Long("K") }, primaryKey: "Id");

        db.CreateRelationship(ByK());   // the table's third index, which Access would name .rC

        Assert.Equal(new[] { ".rC 1", ".rD 2", "PrimaryKey 0" },
                     parent.Indexes.Select(ix => $"{ix.Name} {ix.IndexNumber}").OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(parent.Indexes.Single(ix => ix.Name == ".rD").IsForeignKey);
        Assert.Equal(new[] { ".rC", ".rD", "PrimaryKey" }, Slots(db, "Parent").Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ── Refusals: nothing changes ────────────────────────────────────────────

    private void AssertRefusedAndUnchanged<TException>(RelationshipDefinition definition, string? says = null) where TException : Exception
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var pages = IndexChecks.AllPages(db);
            var refused = Assert.Throws<TException>(() => db.CreateRelationship(definition));
            if (says is not null) Assert.Contains(says, refused.Message);
            IndexChecks.AssertUnchanged(pages, db);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_relationship_is_refused_when_a_row_already_there_refers_to_no_parent_and_nothing_changes()
    {
        using (var db = ParentAndChild())
            db.GetTable("Child").Insert(new Row { ["Id"] = 77, ["K"] = 99, ["Name"] = "an orphan" });

        AssertRefusedAndUnchanged<ConstraintViolationException>(ByK(), "existing data");

        // Without referential integrity the same relationship is made: nothing is asked of the rows.
        using var again = Database.Open(_path);
        again.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K") { Name = "ParentChild" });
        Assert.False(Assert.Single(again.GetRelationships()).HasReferentialIntegrity);
    }

    [Fact]
    public void A_relationship_is_refused_when_the_referenced_columns_have_no_unique_index_and_nothing_changes()
    {
        ParentAndChild().Dispose();
        AssertRefusedAndUnchanged<InvalidOperationException>(
            new RelationshipDefinition("Parent", "Name", "Child", "Name") { HasReferentialIntegrity = true }, "No unique index");
    }

    [Fact]
    public void A_relationship_between_columns_of_different_types_is_refused_and_nothing_changes()
    {
        ParentAndChild().Dispose();
        AssertRefusedAndUnchanged<InvalidOperationException>(new RelationshipDefinition("Parent", "K", "Child", "Code"), "same data type");
    }

    [Fact]
    public void A_relationship_with_a_name_that_is_taken_is_refused_and_nothing_changes()
    {
        using (var db = ParentAndChild(new IndexDefinition("AnIndex", "K")))
            db.CreateRelationship(ByK());

        AssertRefusedAndUnchanged<InvalidOperationException>(new RelationshipDefinition("Parent", "Code", "Child", "Code") { Name = "PARENTCHILD" }, "already");
        // With referential integrity its name is an index's name in the referring table, where it must be free too.
        AssertRefusedAndUnchanged<InvalidOperationException>(
            new RelationshipDefinition("Parent", "Code", "Child", "Code") { Name = "AnIndex", HasReferentialIntegrity = true }, "already");
    }

    [Fact]
    public void A_relationship_that_names_what_is_not_there_or_asks_what_cannot_be_is_refused_and_nothing_changes()
    {
        ParentAndChild().Dispose();
        AssertRefusedAndUnchanged<InvalidOperationException>(new RelationshipDefinition("Nowhere", "K", "Child", "K"));
        AssertRefusedAndUnchanged<InvalidOperationException>(new RelationshipDefinition("Parent", "K", "Child", "Nothing"));
        AssertRefusedAndUnchanged<ArgumentException>(new RelationshipDefinition("Parent", "K", "Child", "K") { CascadeDeletes = true });
        AssertRefusedAndUnchanged<ArgumentException>(
            new RelationshipDefinition("Parent", new[] { "K", "K" }, "Child", new[] { "K", "Id" }) { HasReferentialIntegrity = true });
    }

    [Fact]
    public void A_relationship_on_a_column_no_index_can_be_made_on_is_refused_and_nothing_changes()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var amount = new ColumnDefinition("Amount", DataType.Decimal);
            db.CreateTable("Price", new[] { Long("Id"), amount }, primaryKey: "Id");
            db.CreateTable("Sale", new[] { Long("Id"), new ColumnDefinition("Amount", DataType.Decimal), Long("Price") }, primaryKey: "Id");
        }

        // A Decimal column has no unique index to refer to (none can be made), and is no foreign key's index either.
        AssertRefusedAndUnchanged<InvalidOperationException>(
            new RelationshipDefinition("Price", "Amount", "Sale", "Amount") { HasReferentialIntegrity = true }, "No unique index");
    }

    [Fact]
    public void A_relationship_is_not_created_in_an_Access_97_file()
    {
        string? source = TestCorpus.File("V1997", "indexV1997.mdb");
        if (source is null) return;
        File.Copy(source, _path, overwrite: true);
        AssertRefusedAndUnchanged<NotSupportedException>(new RelationshipDefinition("Table3", "id", "Table1", "otherfk3"));
    }
}
