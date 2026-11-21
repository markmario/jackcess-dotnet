using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Foreign keys on text, in a database that sorts in Access 2010's General order and in one that sorts in General -
/// Legacy (#23). A child's value refers to a parent's when Access holds the two
/// equal - whatever the case, but not without an accent - and that is judged by the keys of the column's own sort
/// order; cascades write the new key in every index of both tables; a value whose key a search of an index cannot
/// be trusted with is looked for in the table instead. What ACE does with the same changes is in
/// ForeignKeysInAccessTests.
/// </summary>
public sealed class GeneralForeignKeyTests : IDisposable
{
    private const int CascadeUpdates = 0x100;
    private const int CascadeDeletes = 0x1000;

    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private static ColumnDefinition Long(string name) => new ColumnDefinition(name, DataType.Int32);

    private static ColumnDefinition Text(string name) => new ColumnDefinition(name, DataType.Text) { MaxLength = 255 };

    /// <summary>
    /// Parent(Code, Name) and Child(Id, Code, Name), Child.Code referring to Parent.Code with cascading updates and
    /// deletes, in a copy of an Access 2010 database in General or in a new General - Legacy one. The children name
    /// their parents in other case than the parents have.
    /// </summary>
    private string ParentAndChild(bool general, bool indexForeignKey)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gfk_{Guid.NewGuid():N}{(general ? ".accdb" : ".mdb")}");
        _paths.Add(path);
        if (general) File.Copy(TestCorpus.File("V2010", "common1V2010.accdb") ?? throw new FileNotFoundException("common1V2010.accdb"), path);
        else Database.Create(path, FileFormat.Access2000).Dispose();

        using var db = Database.Open(path);
        var parent = db.CreateTable("Parent", new[] { Text("Code"), Text("Name") }, primaryKey: "Code");
        var child = db.CreateTable("Child", new[] { Long("Id"), Text("Code"), Text("Name") }, new[] { "Id" },
            indexForeignKey ? new[] { new IndexDefinition("ChildCode", "Code") } : Array.Empty<IndexDefinition>());
        db.GetTable("MSysRelationships").Insert(new Row
        {
            ["ccolumn"] = 1, ["grbit"] = CascadeUpdates | CascadeDeletes, ["icolumn"] = 0,
            ["szColumn"] = "Code", ["szObject"] = "Child",
            ["szReferencedColumn"] = "Code", ["szReferencedObject"] = "Parent",
            ["szRelationship"] = "ParentChild",
        });

        foreach (string code in new[] { "résumé", "Æther", "Zürich", "日本", "O'Brien" })
            parent.Insert(new Row { ["Code"] = code, ["Name"] = "parent " + code });
        child.Insert(new Row { ["Id"] = 1, ["Code"] = "RÉSUMÉ", ["Name"] = "c1" });
        child.Insert(new Row { ["Id"] = 2, ["Code"] = "résumé", ["Name"] = "c2" });
        child.Insert(new Row { ["Id"] = 3, ["Code"] = "æther", ["Name"] = "c3" });
        child.Insert(new Row { ["Id"] = 4, ["Code"] = "ZÜRICH", ["Name"] = "c4" });
        child.Insert(new Row { ["Id"] = 5, ["Code"] = "日本", ["Name"] = "c5" });
        child.Insert(new Row { ["Id"] = 6, ["Name"] = "no parent, by choice" });
        return path;
    }

    private static Database OpenEnforcing(string path)
    {
        var db = Database.Open(path);
        db.EnforceForeignKeys = true;
        return db;
    }

    private static List<string> Codes(Database db, string table)
        => db.GetTable(table).Rows.Select(r => r.TryGetValue("Code", out var code) ? (string?)code ?? "(null)" : "(null)")
            .OrderBy(c => c, StringComparer.Ordinal).ToList();

    private static void AssertIndexesHoldTheRows(Database db)
    {
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Parent");
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child");
    }

    public static TheoryData<bool, bool> Cases() => new()
    {
        { true, true }, { true, false }, { false, true }, { false, false },
    };

    // A child may name its parent in any case, and by any text Access keys alike (Æ is keyed as AE); without the
    // accent it names no parent.
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_child_refers_to_a_parent_in_any_case_and_not_without_its_accent(bool general, bool indexForeignKey)
    {
        string path = ParentAndChild(general, indexForeignKey);
        byte[] before = File.ReadAllBytes(path);
        using (var db = OpenEnforcing(path))
        {
            var child = db.GetTable("Child");
            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 10, ["Code"] = "resume" }));
            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 10, ["Code"] = "Zurich" }));
            Assert.Throws<ConstraintViolationException>(() => child.UpdateByPrimaryKey(5, new Row { ["Code"] = "日本語" }));
        }
        Assert.Equal(before, File.ReadAllBytes(path));

        using (var db = OpenEnforcing(path))
        {
            var child = db.GetTable("Child");
            child.Insert(new Row { ["Id"] = 10, ["Code"] = "o'brien" });
            child.Insert(new Row { ["Id"] = 11, ["Code"] = "Aether" });   // Access keys Æ as AE: the same key, so the same parent
            child.UpdateByPrimaryKey(5, new Row { ["Code"] = "ÆTHER" });
            Assert.Equal(new[] { "(null)", "Aether", "RÉSUMÉ", "ZÜRICH", "o'brien", "résumé", "ÆTHER", "æther" }, Codes(db, "Child"));
            AssertIndexesHoldTheRows(db);
        }
    }

    // A parent's new key goes to each child that named it, in whatever case, and a deleted parent takes them with
    // it; every index of both tables holds the rows left, under the keys of the database's order.
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_cascade_rewrites_and_deletes_the_children_that_named_the_parent_in_any_case(bool general, bool indexForeignKey)
    {
        string path = ParentAndChild(general, indexForeignKey);
        using (var db = OpenEnforcing(path))
        {
            db.GetTable("Parent").UpdateByPrimaryKey("résumé", new Row { ["Code"] = "Curriculum Vitæ" });
            db.GetTable("Parent").DeleteRow("Code", "Æther");
        }
        using (var db = Database.Open(path))
        {
            Assert.Equal(new[] { "Curriculum Vitæ", "O'Brien", "Zürich", "日本" }, Codes(db, "Parent"));
            Assert.Equal(new[] { "(null)", "Curriculum Vitæ", "Curriculum Vitæ", "ZÜRICH", "日本" }, Codes(db, "Child"));
            AssertIndexesHoldTheRows(db);
            var order = general ? SortOrder.General : SortOrder.GeneralLegacy;
            Assert.Equal(order, db.GetTable("Child").Columns.Single(c => c.Name == "Code").SortOrder);
        }
    }

    // A new key with a character Access keys by its neighbours is one the engine makes no key for: ACE makes the
    // change and cascades it, the engine refuses it - at the parent, whose key is always indexed - with the file
    // as it was. So too a child's new value, where the foreign key has an index.
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_cascade_to_a_key_the_engine_cannot_make_is_refused_with_nothing_changed(bool general, bool indexForeignKey)
    {
        string path = ParentAndChild(general, indexForeignKey);
        byte[] before = File.ReadAllBytes(path);
        using (var db = OpenEnforcing(path))
        {
            Assert.Throws<NotSupportedException>(() => db.GetTable("Parent").UpdateByPrimaryKey("résumé", new Row { ["Code"] = "データ" }));
            Assert.Throws<NotSupportedException>(() => db.GetTable("Parent").Insert(new Row { ["Code"] = "日々" }));
            if (indexForeignKey)
                Assert.Throws<NotSupportedException>(() => db.GetTable("Child").UpdateByPrimaryKey(6, new Row { ["Code"] = "ー" }));
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // A key longer than an entry holds is cut, as Access cuts it, and the cascade carries it: the children's
    // entries are cut alike. Such a value is looked for in the table, not through the index, so a child may name
    // it and may not name the other value that is cut to the same entry; that other value is, to the parent's
    // unique key, a duplicate - as it is to ACE.
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_cascade_to_a_key_longer_than_an_entry_holds_is_made_under_the_cut_key(bool general, bool indexForeignKey)
    {
        string start = new string('Æ', 100) + new string('é', 115);
        string first = start + "èèèéèêéèéèëëéëêèèèêêèëéééëëëèêêéèëééëëéè", second = start + "èèëêèééêèèëêéêéëéêêéêêéèéèëèèééèëêéëéééé";

        string path = ParentAndChild(general, indexForeignKey);
        using (var db = OpenEnforcing(path))
        {
            var parent = db.GetTable("Parent");
            var child = db.GetTable("Child");
            parent.UpdateByPrimaryKey("résumé", new Row { ["Code"] = first });
            Assert.Equal(new[] { "(null)", "ZÜRICH", first, first, "æther", "日本" }, Codes(db, "Child"));

            child.Insert(new Row { ["Id"] = 20, ["Code"] = first.ToUpperInvariant() });
            Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 21, ["Code"] = second }));    // no such parent
            Assert.Throws<ConstraintViolationException>(() => parent.Insert(new Row { ["Code"] = second }));                // one entry with the first
            AssertIndexesHoldTheRows(db);

            parent.DeleteRow("Code", first);
            Assert.Equal(new[] { "(null)", "ZÜRICH", "æther", "日本" }, Codes(db, "Child"));
            parent.Insert(new Row { ["Code"] = second });
            AssertIndexesHoldTheRows(db);
        }
    }
}
