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

    private static Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();
    private static Column Text(string name, int chars = 20) => new ColumnBuilder(name, DataType.Text).MaxLength(chars).Build();

    /// <summary>
    /// Parent(K, Code, Name) keyed by K, Code unique; Child(Id, K, Code, Name) keyed by Id: parents 1 to 5, and
    /// forty children that refer to them by K and by Code, two of them referring to nothing.
    /// </summary>
    private Database ParentAndChild(params IndexDefinition[] childIndexes)
    {
        var db = Database.Create(_path, JetVersion.Jet4);
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
        Assert.Equal(("Parent", "Child"), (relationship.FromTable, relationship.ToTable));
        Assert.Equal(new[] { ("K", "K") }, relationship.ColumnPairs);
        Assert.Equal(0x1100, relationship.Flags);
        Assert.True(relationship.HasReferentialIntegrity && relationship.CascadeUpdates && relationship.CascadeDeletes);
        Assert.False(relationship.CascadeNull || relationship.IsOneToOne || relationship.LeftOuterJoin || relationship.RightOuterJoin);

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

        var refused = Assert.Throws<InvalidOperationException>(() => child.Insert(new Row { ["Id"] = 100, ["K"] = 99 }));
        Assert.Contains("ParentChild", refused.Message);
        child.Insert(new Row { ["Id"] = 101, ["K"] = 3 });
        child.Insert(new Row { ["Id"] = 102 });   // refers to nothing

        var parent = db.GetTable("Parent");
        parent.UpdateByPrimaryKey(3, new Row { ["K"] = 30 });
        parent.DeleteRow("K", 1);

        var rows = child.ReadAllRows();
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
    public void A_relationship_shares_a_plain_index_on_its_foreign_key_and_no_unique_one()
    {
        using var db = ParentAndChild(new IndexDefinition("ByK", "K"), new IndexDefinition("UniqueCodeAndId", "Code") { IsRequired = false });
        var child = db.GetTable("Child");
        int byK = child.Indexes.Single(ix => ix.Name == "ByK").RootPageNumber;

        db.CreateRelationship(ByK());
        Assert.Equal(byK, child.Indexes.Single(ix => ix.Name == "ParentChild").RootPageNumber);
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 42));
    }

    [Fact]
    public void A_relationship_between_columns_that_are_each_unique_is_one_to_one_and_its_index_is_unique()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
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
        Assert.True(alsoKey.IsUnique && !alsoKey.IgnoresNulls);
        Assert.NotEqual(other.Indexes.Single(ix => ix.Name == "UniqueAlso").RootPageNumber, alsoKey.RootPageNumber);
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Other", expectedRows: 1));

        db.EnforceForeignKeys = true;
        Assert.Throws<InvalidOperationException>(() => other.Insert(new Row { ["Id"] = 3, ["One"] = 3, ["Also"] = 2 }));   // One is unique
        other.Insert(new Row { ["Id"] = 4, ["One"] = 2, ["Also"] = 2 });
    }

    [Fact]
    public void A_relationship_without_referential_integrity_is_only_recorded()
    {
        using var db = ParentAndChild();
        var parentIndexes = db.GetTable("Parent").Indexes.Select(ix => ix.Name).ToList();
        var childIndexes  = db.GetTable("Child").Indexes.Select(ix => ix.Name).ToList();

        // The children refer to codes by another case, and two to nothing: nothing is checked.
        var relationship = db.CreateRelationship(new RelationshipDefinition("Parent", "Name", "Child", "Name") { JoinType = RelationshipJoinType.LeftOuter });
        var right = db.CreateRelationship(new RelationshipDefinition("Parent", "Name", "Child", "Name") { JoinType = RelationshipJoinType.RightOuter });

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
        using var db = Database.Create(_path, JetVersion.Jet4);
        var region = db.CreateTable("Region", new[] { Long("Id"), Text("Country", 2), Long("Code") }, new[] { "Id" },
                                    new[] { new IndexDefinition("ByCountryAndCode", "Country", "Code") { IsUnique = true } });
        var city = db.CreateTable("City", new[] { Long("Id"), Text("Country", 2), Long("Code") }, primaryKey: "Id");
        region.Insert(new Row { ["Id"] = 1, ["Country"] = "NZ", ["Code"] = 1 });
        region.Insert(new Row { ["Id"] = 2, ["Country"] = "AU", ["Code"] = 1 });
        city.Insert(new Row { ["Id"] = 1, ["Country"] = "nz", ["Code"] = 1 });   // text is matched as its index holds it
        city.Insert(new Row { ["Id"] = 2, ["Country"] = "AU", ["Code"] = 1 });

        var definition = new RelationshipDefinition("Region", "City", ("Country", "Country"), ("Code", "Code"))
        {
            Name = "RegionCity", HasReferentialIntegrity = true, CascadeNull = true,
        };
        db.CreateRelationship(definition);

        var rows = db.GetTable("MSysRelationships").ReadAllRows();
        Assert.Equal(new[] { "0/2 Country>Country", "1/2 Code>Code" },
                     rows.Select(r => $"{r["icolumn"]}/{r["ccolumn"]} {r["szReferencedColumn"]}>{r["szColumn"]}"));
        Assert.All(rows, r => Assert.Equal(0x2000, r["grbit"]));
        Assert.Equal("Country ASC, Code ASC", string.Join(", ", city.Indexes.Single(ix => ix.Name == "RegionCity").Columns));
        Assert.Equal(region.Indexes.Single(ix => ix.Name == "ByCountryAndCode").RootPageNumber,
                     region.Indexes.Single(ix => ix.IsForeignKey).RootPageNumber);
        Assert.Equal(new Slot("RegionCity", 1, 1, 2, 2, IndexChecks.TdefOf(db, "Region"), 0, 2, 2), Slots(db, "City").Single(s => s.Name == "RegionCity"));

        db.EnforceForeignKeys = true;
        Assert.Throws<InvalidOperationException>(() => city.Insert(new Row { ["Id"] = 3, ["Country"] = "AU", ["Code"] = 2 }));
        region.DeleteRow("Id", 2);
        Assert.False(city.NewIndexCursor().FindRowByPrimaryKey(2)!.ContainsKey("Country"));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "City", expectedRows: 2));
    }

    [Fact]
    public void A_table_can_be_related_to_itself()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var staff = db.CreateTable("Staff", new[] { Long("Id"), Long("Manager"), Text("Name") }, primaryKey: "Id");
        staff.Insert(new Row { ["Id"] = 1, ["Name"] = "the top" });
        staff.Insert(new Row { ["Id"] = 2, ["Manager"] = 1 });
        staff.Insert(new Row { ["Id"] = 3, ["Manager"] = 3 });   // a row may refer to itself

        db.CreateRelationship(new RelationshipDefinition("Staff", "Id", "Staff", "Manager") { Name = "Reports", HasReferentialIntegrity = true });

        var slots = Slots(db, "Staff");
        var primary = slots.Single(s => s.Name == ".rB");
        var secondary = slots.Single(s => s.Name == "Reports");
        int tdef = IndexChecks.TdefOf(db, "Staff");
        Assert.Equal(new Slot(".rB", 1, 0, 1, 2, tdef, 0, 0, 2), primary);
        Assert.Equal(new Slot("Reports", 2, 1, 2, 1, tdef, 0, 0, 2), secondary);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Staff", expectedRows: 3));

        db.EnforceForeignKeys = true;
        Assert.Throws<InvalidOperationException>(() => staff.Insert(new Row { ["Id"] = 4, ["Manager"] = 9 }));
        Assert.Throws<InvalidOperationException>(() => staff.DeleteRow("Id", 1));
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
            int third = (int)db.GetTable("Table3").ReadAllRows()[0]["id"]!;
            foreach (var row in table1.ReadAllRows())
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
        var referring = reopened.GetTable("Table1").ReadAllRows();
        Assert.Throws<InvalidOperationException>(() => reopened.GetTable("Table1").UpdateByPrimaryKey(referring[0]["id"]!, new Row { ["otherfk3"] = 999 }));
        // Table3's row is referred to by the relationship Access made as well, which cascades its key too.
        reopened.GetTable("Table3").UpdateByPrimaryKey(referring[0]["otherfk3"]!, new Row { ["id"] = 77 });
        Assert.All(reopened.GetTable("Table1").ReadAllRows(), row => Assert.Equal(77, row["otherfk3"]));
        IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Table1", expectedRows: 4);
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

        AssertRefusedAndUnchanged<InvalidOperationException>(ByK(), "existing data");

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
            new RelationshipDefinition("Parent", "Child", ("K", "K"), ("K", "Id")) { HasReferentialIntegrity = true });
    }

    [Fact]
    public void A_relationship_on_a_column_no_index_can_be_made_on_is_refused_and_nothing_changes()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var amount = new ColumnBuilder("Amount", DataType.Numeric).Build();
            db.CreateTable("Price", new[] { Long("Id"), amount }, primaryKey: "Id");
            db.CreateTable("Sale", new[] { Long("Id"), new ColumnBuilder("Amount", DataType.Numeric).Build(), Long("Price") }, primaryKey: "Id");
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
