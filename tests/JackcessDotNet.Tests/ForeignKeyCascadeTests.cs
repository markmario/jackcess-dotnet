using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Database.EnforceForeignKeys"/> on update and delete, and what a relationship asks for: cascade
/// update, cascade delete, cascade-to-null (qcatco/exportmdb.interop#38).
/// <para>
/// The library cannot make a relationship (qcatco/exportmdb.interop#37), so most of these declare one by adding its
/// rows to MSysRelationships, which is all the enforcer reads; the corpus tests at the end use relationships Access
/// made. What Access itself does with each case is checked in ExportMdb.Interop's ForeignKeysInAccessTests.
/// </para>
/// </summary>
public sealed class ForeignKeyCascadeTestsThroughIndexes : ForeignKeyCascadeTests
{
    protected override bool IndexForeignKeys => true;
}

/// <summary>As <see cref="ForeignKeyCascadeTestsThroughIndexes"/>, the tables without an index on any foreign key.</summary>
public sealed class ForeignKeyCascadeTestsByScan : ForeignKeyCascadeTests
{
    protected override bool IndexForeignKeys => false;
}

/// <summary>
/// The cases: see <see cref="ForeignKeyCascadeTestsThroughIndexes"/> and <see cref="ForeignKeyCascadeTestsByScan"/>.
/// </summary>
public abstract class ForeignKeyCascadeTests : IDisposable
{
    /// <summary>
    /// Whether the tables these tests make have an index on each foreign key, as Access gives a relationship. With
    /// one, the enforcer finds the rows that refer to a parent through it; without, it reads the table. Every test
    /// runs both ways, and must come to the same.
    /// </summary>
    protected abstract bool IndexForeignKeys { get; }

    /// <summary>A table with a primary key and, when <see cref="IndexForeignKeys"/>, an index on each of <paramref name="keys"/>.</summary>
    private Table Create(Database db, string name, IReadOnlyList<Column> columns, string primaryKey, params string[][] keys)
        => db.CreateTable(name, columns, new[] { primaryKey },
            IndexForeignKeys ? keys.Select((key, i) => new IndexDefinition($"{name}Key{i}", key)).ToList() : new List<IndexDefinition>());

    private static string[] On(params string[] columns) => columns;

    private const int CascadeUpdates = 0x100;
    private const int CascadeDeletes = 0x1000;
    private const int CascadeNull    = 0x2000;
    private const int NoIntegrity    = 0x2;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fkc_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();

    private static Column Text(string name, int chars = 20) => new ColumnBuilder(name, DataType.Text).MaxLength(chars).Build();

    /// <summary>Declares a relationship as Access records one: a row of MSysRelationships for each pair of columns.</summary>
    private static void Relate(Database db, string name, string parent, string child, int flags,
                               params (string Parent, string Child)[] columns)
    {
        var relationships = db.GetTable("MSysRelationships");
        for (int i = 0; i < columns.Length; i++)
            relationships.Insert(new Row
            {
                ["ccolumn"] = columns.Length, ["grbit"] = flags, ["icolumn"] = i,
                ["szColumn"] = columns[i].Child, ["szObject"] = child,
                ["szReferencedColumn"] = columns[i].Parent, ["szReferencedObject"] = parent,
                ["szRelationship"] = name,
            });
    }

    /// <summary>Parent(K, Name) and Child(Id, K, Name), Child.K referring to Parent.K: parents 1, 2, 3; children of 1 and 2.</summary>
    private void ParentAndChild(int flags)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var child  = Create(db, "Child", new[] { Long("Id"), Long("K"), Text("Name") }, "Id", On("K"));
        Relate(db, "ParentChild", "Parent", "Child", flags, ("K", "K"));
        foreach (int k in new[] { 1, 2, 3 })
            parent.Insert(new Row { ["K"] = k, ["Name"] = "p" + k });
        child.Insert(new Row { ["Id"] = 10, ["K"] = 1, ["Name"] = "c10" });
        child.Insert(new Row { ["Id"] = 11, ["K"] = 1, ["Name"] = "c11" });
        child.Insert(new Row { ["Id"] = 20, ["K"] = 2, ["Name"] = "c20" });
        child.Insert(new Row { ["Id"] = 30, ["Name"] = "orphan by choice" });
    }

    private Database OpenEnforcing()
    {
        var db = Database.Open(_path);
        db.EnforceForeignKeys = true;
        return db;
    }

    /// <summary>A table's rows as text, in a fixed order: "Id=10, K=1, Name=c10".</summary>
    private static List<string> Rows(Database db, string table)
        => db.GetTable(table).ReadAllRows()
            .Select(row => string.Join(", ", row.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}")))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

    private List<string> Rows(string table)
    {
        using var db = Database.Open(_path);
        return Rows(db, table);
    }

    /// <summary>Runs <paramref name="change"/> with enforcement on, expecting it refused with not a byte of the file changed.</summary>
    private string Refused(Action<Database> change)
    {
        byte[] before = File.ReadAllBytes(_path);
        string message;
        using (var db = OpenEnforcing())
            message = Assert.Throws<InvalidOperationException>(() => change(db)).Message;
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused change altered the file");
        return message;
    }

    /// <summary>
    /// Makes <paramref name="change"/> with enforcement on, and then requires of every table what Access needs to
    /// answer through its indexes: each index holding exactly the rows' entries, whatever a cascade moved or removed.
    /// </summary>
    private void Enforcing(Action<Database> change)
    {
        using (var db = OpenEnforcing())
            change(db);

        using var read = Database.Open(_path);
        foreach (string table in read.ListTables())
            IndexChecks.AssertEveryIndexHoldsTheRows(read, table);
    }

    // ── Update of a parent's key ─────────────────────────────────────────────

    [Fact]
    public void UpdateParentKey_WithCascadeUpdate_ReferringRowsFollow()
    {
        ParentAndChild(CascadeUpdates);

        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 7 }));

        Assert.Equal(new[] { "K=2, Name=p2", "K=3, Name=p3", "K=7, Name=p1" }, Rows("Parent"));
        Assert.Equal(new[] { "Id=10, K=7, Name=c10", "Id=11, K=7, Name=c11", "Id=20, K=2, Name=c20", "Id=30, Name=orphan by choice" },
                     Rows("Child"));
    }

    [Fact]
    public void UpdateParentKey_WithCascadeUpdate_PrimaryKeysFindTheRowsWhereTheyNowAre()
    {
        ParentAndChild(CascadeUpdates);

        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 7 }));

        using var read = Database.Open(_path);
        var parent = read.GetTable("Parent").NewIndexCursor();
        Assert.Equal("p1", parent.FindRowByPrimaryKey(7)?["Name"]);
        Assert.Null(parent.FindRowByPrimaryKey(1));
        // The children moved when they were rewritten: their key entries moved with them.
        Assert.Equal(7, read.GetTable("Child").NewIndexCursor().FindRowByPrimaryKey(10)?["K"]);
        Assert.Equal(7, read.GetTable("Child").NewIndexCursor().FindRowByPrimaryKey(11)?["K"]);
    }

    [Fact]
    public void UpdateParentKey_WithoutCascade_IsRefusedWhileRowsReferToIt()
    {
        ParentAndChild(flags: 0);

        string message = Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 7 }));

        Assert.Contains("Foreign-key violation", message);
        Assert.Contains("ParentChild", message);
    }

    [Fact]
    public void UpdateParentKey_WithoutCascade_IsAllowedWhenNoRowRefersToIt()
    {
        ParentAndChild(flags: 0);

        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(3, new Row { ["K"] = 9 }));

        Assert.Equal(new[] { "K=1, Name=p1", "K=2, Name=p2", "K=9, Name=p3" }, Rows("Parent"));
    }

    [Fact]
    public void UpdateParent_OfAColumnNoRelationshipNames_IsAllowed()
    {
        ParentAndChild(flags: 0);

        // The key is given too, unchanged: a value written again as it is changes nothing.
        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 1, ["Name"] = "renamed" }));

        Assert.Contains("K=1, Name=renamed", Rows("Parent"));
        Assert.Equal(4, Rows("Child").Count);
    }

    [Fact]
    public void UpdateParentKey_ToOneAnotherRowHas_IsRefused()
    {
        ParentAndChild(CascadeUpdates);

        string message = Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 2 }));

        Assert.Contains("duplicate", message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CascadeUpdates)]
    public void UpdateParentKey_ToNull_IsRefused(int flags)
    {
        // No part of a primary key is null. With cascade update the null would have gone on to the children too.
        ParentAndChild(flags);

        string message = Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = null }));

        Assert.Contains("primary key", message);
        Refused(db => db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["Id"] = null }));
    }

    // ── Delete of a parent ───────────────────────────────────────────────────

    [Fact]
    public void DeleteParent_WithCascadeDelete_DeletesReferringRows()
    {
        ParentAndChild(CascadeDeletes);

        Enforcing(db => db.GetTable("Parent").DeleteRow("K", 1));

        Assert.Equal(new[] { "K=2, Name=p2", "K=3, Name=p3" }, Rows("Parent"));
        Assert.Equal(new[] { "Id=20, K=2, Name=c20", "Id=30, Name=orphan by choice" }, Rows("Child"));

        using var read = Database.Open(_path);
        Assert.Null(read.GetTable("Child").NewIndexCursor().FindRowByPrimaryKey(10));
        Assert.NotNull(read.GetTable("Child").NewIndexCursor().FindRowByPrimaryKey(20));
    }

    [Fact]
    public void DeleteParent_WithCascadeNull_NullsTheForeignKeys()
    {
        ParentAndChild(CascadeNull);

        Enforcing(db => db.GetTable("Parent").DeleteRow("K", 1));

        Assert.Equal(new[] { "K=2, Name=p2", "K=3, Name=p3" }, Rows("Parent"));
        Assert.Equal(new[] { "Id=10, Name=c10", "Id=11, Name=c11", "Id=20, K=2, Name=c20", "Id=30, Name=orphan by choice" },
                     Rows("Child"));
    }

    // A Required column refuses the cascade too, but Required is a property only Access writes: that case is in
    // ExportMdb.Interop's ForeignKeysInAccessTests.
    [Fact]
    public void DeleteParent_WithCascadeNull_IsRefusedWhenTheColumnIsInThePrimaryKey()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
            var child  = db.CreateTable("Child", new[] { Long("K"), Long("N"), Text("Name") }, new[] { "K", "N" });
            Relate(db, "ParentChild", "Parent", "Child", CascadeNull, ("K", "K"));
            parent.Insert(new Row { ["K"] = 1, ["Name"] = "p1" });
            child.Insert(new Row { ["K"] = 1, ["N"] = 1, ["Name"] = "c" });
        }

        string message = Refused(db => db.GetTable("Parent").DeleteRow("K", 1));

        Assert.Contains("null", message);
    }

    [Fact]
    public void DeleteParent_WithNoCascade_IsRefusedWhileRowsReferToIt()
    {
        ParentAndChild(flags: 0);

        string message = Refused(db => db.GetTable("Parent").DeleteRow("K", 1));

        Assert.Contains("Foreign-key violation", message);
    }

    [Fact]
    public void DeleteParent_WithNoCascade_IsAllowedWhenNoRowRefersToIt()
    {
        ParentAndChild(flags: 0);

        Enforcing(db => db.GetTable("Parent").DeleteRow("Name", "p3"));

        Assert.Equal(new[] { "K=1, Name=p1", "K=2, Name=p2" }, Rows("Parent"));
    }

    [Fact]
    public void DeleteChild_IsAlwaysAllowed()
    {
        ParentAndChild(flags: 0);

        Enforcing(db => db.GetTable("Child").DeleteRow("Id", 10));

        Assert.Equal(3, Rows("Child").Count);
        Assert.Equal(3, Rows("Parent").Count);
    }

    // ── Update of a child's foreign key ──────────────────────────────────────

    [Fact]
    public void UpdateChildForeignKey_ToAValueWithNoParent_IsRefused()
    {
        ParentAndChild(flags: 0);

        string message = Refused(db => db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["K"] = 99 }));

        Assert.Contains("no matching row in 'Parent'", message);
    }

    [Fact]
    public void UpdateChildForeignKey_ToAnotherParentOrToNull_IsAllowed()
    {
        ParentAndChild(flags: 0);

        Enforcing(db =>
        {
            db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["K"] = 3 });
            db.GetTable("Child").UpdateByPrimaryKey(11, new Row { ["K"] = null });
        });

        Assert.Equal(new[] { "Id=10, K=3, Name=c10", "Id=11, Name=c11", "Id=20, K=2, Name=c20", "Id=30, Name=orphan by choice" },
                     Rows("Child"));
    }

    [Fact]
    public void Insert_OfAChildWithNoParent_IsRefused_AndWithOneOrWithNullIsAllowed()
    {
        ParentAndChild(flags: 0);

        Refused(db => db.GetTable("Child").Insert(new Row { ["Id"] = 40, ["K"] = 99 }));
        Enforcing(db =>
        {
            db.GetTable("Child").Insert(new Row { ["Id"] = 41, ["K"] = 3 });
            db.GetTable("Child").Insert(new Row { ["Id"] = 42 });
        });

        Assert.Equal(6, Rows("Child").Count);
    }

    // ── AutoNumbers ──────────────────────────────────────────────────────────

    private void ParentAndNumberedChild()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var child  = Create(db, "Child", new[] { new ColumnBuilder("Id", DataType.Long).AutoNumber().Build(), Long("K"), Text("Name") }, "Id", On("K"));
        Relate(db, "ParentChild", "Parent", "Child", CascadeUpdates, ("K", "K"));
        parent.Insert(new Row { ["K"] = 1, ["Name"] = "p1" });
        parent.Insert(new Row { ["K"] = 2, ["Name"] = "p2" });
        child.Insert(new Row { ["K"] = 1, ["Name"] = "first" });
        child.Insert(new Row { ["K"] = 1, ["Name"] = "second" });
    }

    [Fact]
    public void ARefusedInsert_LeavesTheAutoNumberCounterAndTheCallersRowAsTheyWere()
    {
        ParentAndNumberedChild();
        byte[] before = File.ReadAllBytes(_path);

        using (var db = OpenEnforcing())
        {
            var child = db.GetTable("Child");
            var orphan = new Row { ["K"] = 99, ["Name"] = "orphan" };

            var refusal = Assert.Throws<InvalidOperationException>(() => child.Insert(orphan));

            Assert.Contains("Foreign-key violation", refusal.Message);
            Assert.False(orphan.ContainsKey("Id"), "the refused row was left holding the number it was not given");
            Assert.Equal(2, child.LastAutoNumber);
            Assert.Equal(2, child.RowCount);
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused insert altered the file");

        // The number the refused row would have had is the next row's.
        Enforcing(db =>
        {
            var next = new Row { ["K"] = 2, ["Name"] = "third" };
            db.GetTable("Child").Insert(next);
            Assert.Equal(3, next["Id"]);
        });
    }

    [Fact]
    public void AnEnforcedUpdate_KeepsTheRowsAutoNumber_AndACascadeDoesNotTouchTheCounter()
    {
        ParentAndNumberedChild();

        Enforcing(db =>
        {
            // The number a row brings is ignored, as without enforcement; the rest of the update is made.
            db.GetTable("Child").UpdateByPrimaryKey(1, new Row { ["Id"] = 50, ["K"] = 2 });
            db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 7 });
        });

        Assert.Equal(new[] { "Id=1, K=2, Name=first", "Id=2, K=7, Name=second" }, Rows("Child"));
        using var read = Database.Open(_path);
        Assert.Equal(2, read.GetTable("Child").LastAutoNumber);
        Assert.Equal(2, read.GetTable("Child").RowCount);
    }

    [Fact]
    public void ACascade_DoesNotWriteAnAutoNumber()
    {
        // The foreign key is the child's AutoNumber. Access will not make such a relationship cascade updates, and
        // refuses the cascade-to-null of one that reaches a row: "Field cannot be updated".
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
            var child  = Create(db, "Child", new[] { Long("Id"), new ColumnBuilder("K", DataType.Long).AutoNumber().Build(), Text("Name") }, "Id", On("K"));
            Relate(db, "ParentChild", "Parent", "Child", CascadeNull | CascadeUpdates, ("K", "K"));
            parent.Insert(new Row { ["K"] = 1, ["Name"] = "p1" });
            parent.Insert(new Row { ["K"] = 2, ["Name"] = "p2" });
            child.Insert(new Row { ["Id"] = 10, ["Name"] = "numbered 1" });
        }

        Assert.Contains("AutoNumber", Refused(db => db.GetTable("Parent").DeleteRow("K", 1)));
        Assert.Contains("AutoNumber", Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 7 })));

        Enforcing(db => db.GetTable("Parent").DeleteRow("K", 2));   // no child of 2: nothing to cascade
        using var read = Database.Open(_path);
        Assert.Equal(1, read.GetTable("Child").LastAutoNumber);
    }

    // ── Indexes ──────────────────────────────────────────────────────────────

    [Fact]
    public void ACascade_IsRefusedWhenItWouldMakeADuplicateInAUniqueIndexOfTheChild()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
            // One child a parent: K is unique in Child.
            var child = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Name") }, new[] { "Id" },
                new[] { new IndexDefinition("OneAParent", "K") { IsUnique = true } });
            Relate(db, "ParentChild", "Parent", "Child", CascadeUpdates, ("K", "K"));
            parent.Insert(new Row { ["K"] = 1, ["Name"] = "p1" });
            child.Insert(new Row { ["Id"] = 10, ["K"] = 1, ["Name"] = "c10" });
            child.Insert(new Row { ["Id"] = 50, ["K"] = 5, ["Name"] = "in the way" });   // no Parent 5: enforcement is off
        }

        string message = Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 5 }));
        Assert.Contains("duplicate", message);
        Assert.Contains("OneAParent", message);

        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 6 }));
        Assert.Equal(new[] { "Id=10, K=6, Name=c10", "Id=50, K=5, Name=in the way" }, Rows("Child"));
    }

    [Fact]
    public void AChangeThatSwapsWhatTwoRowsHoldInAUniqueIndex_IsJudgedAsItLeavesThem()
    {
        // Head(Id, Next): each row's Next refers to a Head, uniquely. Deleting 1 cascades to 2, the row that refers
        // to it, and to 3 in turn - rows whose keys in the unique index are still in the file while the change is
        // worked out, and are no other row's by the time it is done.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var head = db.CreateTable("Head", new[] { Long("Id"), Long("Next") }, new[] { "Id" },
                new[] { new IndexDefinition("NextIsUnique", "Next") { IsUnique = true } });
            Relate(db, "HeadHead", "Head", "Head", CascadeNull | CascadeUpdates, ("Id", "Next"));
            head.Insert(new Row { ["Id"] = 1 });
            head.Insert(new Row { ["Id"] = 2, ["Next"] = 1 });
            head.Insert(new Row { ["Id"] = 3, ["Next"] = 2 });
        }

        Enforcing(db =>
        {
            db.GetTable("Head").UpdateByPrimaryKey(1, new Row { ["Id"] = 9 });   // 2's Next follows to 9
            db.GetTable("Head").UpdateByPrimaryKey(3, new Row { ["Next"] = null });
            db.GetTable("Head").UpdateByPrimaryKey(3, new Row { ["Next"] = 3 });  // itself: no other row's Next is 3
        });
        Assert.Equal(new[] { "Id=2, Next=9", "Id=3, Next=3", "Id=9" }, Rows("Head"));

        Refused(db => db.GetTable("Head").UpdateByPrimaryKey(3, new Row { ["Next"] = 9 }));   // 2's Next is 9
        Enforcing(db => db.GetTable("Head").DeleteRow("Id", 9));                              // 2's Next becomes null
        Assert.Equal(new[] { "Id=2", "Id=3, Next=3" }, Rows("Head"));
    }

    /// <summary>
    /// With an index on the foreign key - Access gives every relationship one - a cascade reads the rows it reaches
    /// and the index pages that lead to them, not the child table: ten times the rows cost it a level of the tree.
    /// Without one it reads the table, every page of it.
    /// </summary>
    [Fact]
    public void ACascade_ReadsTheRowsItReaches_NotTheTable_WhenTheForeignKeyIsIndexed()
    {
        const int ChildrenEach = 5;

        (long Reads, int ChildPages) ReadsOfOneCascadeDelete(int parents)
        {
            if (File.Exists(_path)) File.Delete(_path);
            using (var db = Database.Create(_path, JetVersion.Jet4))
            {
                var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
                var child  = Create(db, "Child", new[] { Long("Id"), Long("K"), Text("Name", 100) }, "Id", On("K"));
                Relate(db, "ParentChild", "Parent", "Child", CascadeDeletes, ("K", "K"));
                for (int k = 1; k <= parents; k++)
                    parent.Insert(new Row { ["K"] = k, ["Name"] = "p" + k });
                for (int i = 0; i < parents * ChildrenEach; i++)
                    child.Insert(new Row { ["Id"] = i, ["K"] = 1 + i % parents, ["Name"] = new string('c', 100) });
            }

            using (var db = OpenEnforcing())
            {
                var child = db.GetTable("Child");
                int pages = UsageMap.GetOwnedPages(db.File.ReadPage(child.Definition.UmapPageNumber), child.Definition.OwnedPagesRow,
                                                   db.File.Format, db.File).Count;
                db.GetTable("Parent").DeleteRow("K", 2);   // the first change reads the relationships
                long before = db.File.ReadCount;
                db.GetTable("Parent").DeleteRow("K", 3);
                long reads = db.File.ReadCount - before;
                Assert.Equal((parents - 2) * ChildrenEach, child.RowCount);
                return (reads, pages);
            }
        }

        var (small, _)          = ReadsOfOneCascadeDelete(parents: 200);
        var (large, childPages) = ReadsOfOneCascadeDelete(parents: 2_000);

        Assert.True(childPages > 250, $"the child is only {childPages} pages");
        if (IndexForeignKeys)
            Assert.True(large <= small + 30 && large < childPages / 2,
                $"a cascade delete of {ChildrenEach} rows read {small} pages with 1,000 child rows and {large} with 10,000 ({childPages} pages of them)");
        else
            Assert.True(large >= childPages, $"with no index the table is read: {large} pages read, {childPages} in the table");
    }

    // ── Keys as they are stored ──────────────────────────────────────────────

    /// <summary>
    /// Parent(Id, K) and Child(Id, K), Child.K referring to Parent.K, K of <paramref name="type"/>: parent i + 1 and
    /// child (i + 1) * 10 hold the i-th of <paramref name="values"/>. (What Access does with each of these keys is in
    /// ExportMdb.Interop's ForeignKeysInAccessTests.)
    /// </summary>
    private void Keyed(DataType type, int flags, params object?[] values)
    {
        Column Key() => type == DataType.Text ? Text("K") : new ColumnBuilder("K", type).Build();
        using var db = Database.Create(_path, JetVersion.Jet4);
        var parent = Create(db, "Parent", new[] { Long("Id"), Key() }, "Id", On("K"));
        var child  = Create(db, "Child", new[] { Long("Id"), Key() }, "Id", On("K"));
        Relate(db, "ParentChild", "Parent", "Child", flags, ("K", "K"));
        for (int i = 0; i < values.Length; i++)
        {
            parent.Insert(new Row { ["Id"] = i + 1, ["K"] = values[i] });
            child.Insert(new Row { ["Id"] = (i + 1) * 10, ["K"] = values[i] });
        }
    }

    private object? KeyOfChild(int id)
    {
        using var db = Database.Open(_path);
        return db.GetTable("Child").NewIndexCursor().FindRowByPrimaryKey(id)?.GetValueOrDefault("K");
    }

    [Fact]
    public void ADoubleAndTheNextDoubleAfterIt_AreTwoKeys()
    {
        const double Next = 1.0000000000000002;   // one bit from 1: the same number once it has been through a decimal

        Keyed(DataType.Double, flags: 0, 1.0);
        Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = Next }));

        Keyed(DataType.Double, CascadeUpdates, 1.0);
        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = Next }));
        Assert.Equal(BitConverter.DoubleToInt64Bits(Next), BitConverter.DoubleToInt64Bits((double)KeyOfChild(10)!));

        // And each has its own children: there is no parent 1 any more.
        Refused(db => db.GetTable("Child").Insert(new Row { ["Id"] = 11, ["K"] = 1.0 }));
        Enforcing(db => db.GetTable("Child").Insert(new Row { ["Id"] = 12, ["K"] = Next }));
    }

    [Fact]
    public void ASingleAndTheNextSingleAfterIt_AreTwoKeys()
    {
        const float Next = 1.00000012f;

        Keyed(DataType.Float, flags: 0, 1f);
        Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = Next }));

        Keyed(DataType.Float, CascadeUpdates, 1f);
        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = Next }));
        Assert.Equal(Next, (float)KeyOfChild(10)!);
    }

    [Fact]
    public void ZeroAndMinusZero_AreTwoKeys_AsInAccesssIndexes()
    {
        static bool IsMinusZero(object? value) => value is double d && d == 0 && double.IsNegativeInfinity(1 / d);

        // Each zero is the parent of the children that have that zero.
        Keyed(DataType.Double, CascadeDeletes, 0.0, -0.0);
        Enforcing(db => db.GetTable("Parent").DeleteRow("Id", 1));
        Assert.Null(KeyOfChild(10));
        Assert.True(IsMinusZero(KeyOfChild(20)));

        // 0 to -0 is a change: refused while a child has 0, carried to the child with cascade update.
        Keyed(DataType.Double, flags: 0, 0.0);
        Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = -0.0 }));
        Refused(db => db.GetTable("Child").Insert(new Row { ["Id"] = 11, ["K"] = -0.0 }));

        Keyed(DataType.Double, CascadeUpdates, 0.0);
        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = -0.0 }));
        Assert.True(IsMinusZero(KeyOfChild(10)));
    }

    // A Date/Time is keyed by the double the row stores, which a DateTime does not always hold. This library cannot
    // write such a double - a DateTime is all a row takes, and its ToOADate is to the millisecond - so the case that
    // matters, a date Access wrote, is in ForeignKeysInAccessTests; these are dates as this library writes them.
    [Fact]
    public void ADateKey_IsFollowedAndForbidden()
    {
        var odd   = new DateTime(2009, 7, 6, 2, 57, 46).AddTicks(5_678);
        var other = new DateTime(2023, 3, 15, 2, 57, 46).AddTicks(1_234);

        Keyed(DataType.ShortDateTime, flags: 0, odd, other);
        Refused(db => db.GetTable("Parent").DeleteRow("Id", 1));
        Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = new DateTime(2020, 1, 2) }));

        Keyed(DataType.ShortDateTime, CascadeDeletes, odd, other);
        Enforcing(db => db.GetTable("Parent").DeleteRow("Id", 1));
        Assert.Equal(new[] { 20 }, Rows("Child").Select(row => int.Parse(row.Split(',')[0].Substring(3))));

        Keyed(DataType.ShortDateTime, CascadeUpdates, odd, other);
        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = new DateTime(2020, 1, 2) }));
        Assert.Equal(new DateTime(2020, 1, 2), KeyOfChild(10));
    }

    [Fact]
    public void ARowACascadeRewrites_KeepsTheDatesItDidNotChange()
    {
        var odd = new DateTime(2009, 7, 6, 2, 57, 46).AddTicks(5_678);
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var dated   = Create(db, "Dated", new[] { Long("Id"), new ColumnBuilder("K", DataType.ShortDateTime).Build() }, "Id", On("K"));
            var counted = db.CreateTable("Counted", new[] { Long("N") }, primaryKey: "N");
            var child   = Create(db, "Child", new[] { Long("Id"), new ColumnBuilder("K", DataType.ShortDateTime).Build(), Long("N") }, "Id", On("K"), On("N"));
            Relate(db, "DatedChild", "Dated", "Child", 0, ("K", "K"));
            Relate(db, "CountedChild", "Counted", "Child", CascadeUpdates, ("N", "N"));
            dated.Insert(new Row { ["Id"] = 1, ["K"] = odd });
            counted.Insert(new Row { ["N"] = 1 });
            child.Insert(new Row { ["Id"] = 10, ["K"] = odd, ["N"] = 1 });
        }

        Enforcing(db => db.GetTable("Counted").UpdateByPrimaryKey(1, new Row { ["N"] = 9 }));   // rewrites the child

        // The child is still its date's parent's: the parent cannot go, or change.
        Refused(db => db.GetTable("Dated").DeleteRow("Id", 1));
        Refused(db => db.GetTable("Dated").UpdateByPrimaryKey(1, new Row { ["K"] = new DateTime(2020, 1, 2) }));
    }

    [Fact]
    public void AYesNoForeignKey_GivenNull_IsNo()
    {
        Keyed(DataType.Boolean, flags: 0, true);

        // A Yes/No holds no null: the row would be stored No, and there is no parent whose key is No.
        Refused(db => db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["K"] = null }));
        Refused(db => db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["K"] = false }));
        Refused(db => db.GetTable("Child").Insert(new Row { ["Id"] = 11, ["K"] = null }));
        Refused(db => db.GetTable("Child").Insert(new Row { ["Id"] = 11 }));
        Refused(db => db.GetTable("Child").Insert(new Row { ["Id"] = 11, ["K"] = false }));
        Enforcing(db => db.GetTable("Child").Insert(new Row { ["Id"] = 11, ["K"] = true }));

        // With one, each is allowed, and the row is No.
        Keyed(DataType.Boolean, flags: 0, true, false);
        Enforcing(db =>
        {
            db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["K"] = null });
            db.GetTable("Child").Insert(new Row { ["Id"] = 11 });
        });
        Assert.Equal(false, KeyOfChild(10));
        Assert.Equal(false, KeyOfChild(11));
    }

    [Fact]
    public void ACascadeToNull_OfAYesNoForeignKey_LeavesItNo_AsAccessDoes()
    {
        // Access makes this relationship, deletes the parent and leaves the child No, with no parent whose key is
        // No: the one cascade that leaves a row without a parent, and Access's own.
        Keyed(DataType.Boolean, CascadeNull, true);

        Enforcing(db => db.GetTable("Parent").DeleteRow("Id", 1));

        Assert.Empty(Rows("Parent"));
        Assert.Equal(false, KeyOfChild(10));
    }

    [Fact]
    public void AParentWithNoKey_HasTheRowsWithNoForeignKeyForChildren_AsInAccess()
    {
        // Access's index on the foreign key has an entry for a null, and the parent's null finds it.
        void NullAndNot(int flags)
        {
            Keyed(DataType.Long, flags, null, 20);   // parent 1 and child 10 have no K; parent 2 and child 20 have 20
        }

        NullAndNot(CascadeDeletes);
        Enforcing(db => db.GetTable("Parent").DeleteRow("Id", 1));
        Assert.Equal(new[] { "Id=20, K=20" }, Rows("Child"));

        NullAndNot(flags: 0);
        Refused(db => db.GetTable("Parent").DeleteRow("Id", 1));
        Refused(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 5 }));

        NullAndNot(CascadeUpdates);
        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 5 }));
        Assert.Equal(new[] { "Id=10, K=5", "Id=20, K=20" }, Rows("Child"));

        // A parent that has a key never touches them.
        NullAndNot(CascadeDeletes | CascadeUpdates);
        Enforcing(db =>
        {
            db.GetTable("Parent").UpdateByPrimaryKey(2, new Row { ["K"] = 21 });
            db.GetTable("Parent").DeleteRow("Id", 2);
        });
        Assert.Equal(new[] { "Id=10" }, Rows("Child"));
    }

    public static TheoryData<DataType, object, object> KeyTypes => new()
    {
        { DataType.Byte, (byte)7, (byte)200 },
        { DataType.Int, (short)-5, (short)300 },
        { DataType.Long, -70000, 70000 },
        { DataType.Money, 1.2345m, -9.5m },
        { DataType.Float, 1.1f, -2.5f },
        { DataType.Double, 0.1, 1.0000000000000002 },
        { DataType.ShortDateTime, new DateTime(2009, 7, 6, 2, 57, 46).AddTicks(5_678), new DateTime(2020, 1, 2, 3, 4, 5) },
        { DataType.Boolean, true, false },
        { DataType.Text, "abc", "Straße" },
        { DataType.Memo, "a memo", "another memo" },
        { DataType.Guid, new Guid("11111111-2222-3333-4444-555555555555"), new Guid("66666666-7777-8888-9999-000000000000") },
    };

    /// <summary>
    /// For every type of column the engine keys, the children of one parent are found - through the foreign key's
    /// index, where there is one, with no reading the table after it has answered - and no others.
    /// </summary>
    [Theory]
    [MemberData(nameof(KeyTypes))]
    public void ACascade_FindsTheChildren_ForAKeyOfAnyType(DataType type, object one, object other)
    {
        Keyed(type, CascadeDeletes, one, other);
        Enforcing(db =>
        {
            db.GetTable("Child").Insert(new Row { ["Id"] = 11, ["K"] = one });
            db.GetTable("Parent").DeleteRow("Id", 1);
        });
        Assert.Equal(new[] { 20 }, ChildIds());

        Keyed(type, flags: 0, one, other);
        Refused(db => db.GetTable("Parent").DeleteRow("Id", 1));
    }

    private int[] ChildIds()
    {
        using var db = Database.Open(_path);
        return db.GetTable("Child").ReadAllRows().Select(row => Convert.ToInt32(row["Id"])).OrderBy(id => id).ToArray();
    }

    // ── Enforcement off, and relationships that are not enforced ─────────────

    [Fact]
    public void WithEnforcementOff_NothingIsCheckedAndNothingCascades()
    {
        ParentAndChild(CascadeUpdates | CascadeDeletes);

        using (var db = Database.Open(_path))
        {
            db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["Name"] = "still 1" });
            db.GetTable("Parent").DeleteRow("K", 2);
            db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["K"] = 99 });
            db.GetTable("Child").Insert(new Row { ["Id"] = 40, ["K"] = 98 });
        }

        Assert.Equal(new[] { "K=1, Name=still 1", "K=3, Name=p3" }, Rows("Parent"));
        Assert.Equal(new[]
        {
            "Id=10, K=99, Name=c10", "Id=11, K=1, Name=c11", "Id=20, K=2, Name=c20", "Id=30, Name=orphan by choice", "Id=40, K=98",
        }, Rows("Child"));
    }

    [Fact]
    public void ARelationshipWithoutReferentialIntegrity_IsNotEnforced()
    {
        ParentAndChild(NoIntegrity | CascadeDeletes);

        Enforcing(db =>
        {
            Assert.False(db.GetRelationships().Single().HasReferentialIntegrity);
            db.GetTable("Child").Insert(new Row { ["Id"] = 40, ["K"] = 99 });
            db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["K"] = 98 });
            db.GetTable("Parent").DeleteRow("K", 2);
        });

        Assert.Equal(new[] { "K=1, Name=p1", "K=3, Name=p3" }, Rows("Parent"));
        Assert.Equal(5, Rows("Child").Count);
    }

    // ── Relationships of more than one column ────────────────────────────────

    /// <summary>Region(Id, Country, Code, Name) and City(Id, Country, Code, Name), City (Country, Code) referring to Region's.</summary>
    private void RegionsAndCities(int flags)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var region = Create(db, "Region", new[] { Long("Id"), Text("Country", 2), Long("Code"), Text("Name") }, "Id", On("Country", "Code"));
        var city   = Create(db, "City",   new[] { Long("Id"), Text("Country", 2), Long("Code"), Text("Name") }, "Id", On("Country", "Code"));
        Relate(db, "RegionCity", "Region", "City", flags, ("Country", "Country"), ("Code", "Code"));
        region.Insert(new Row { ["Id"] = 1, ["Country"] = "NZ", ["Code"] = 1, ["Name"] = "Otago" });
        region.Insert(new Row { ["Id"] = 2, ["Country"] = "NZ", ["Code"] = 2, ["Name"] = "Canterbury" });
        region.Insert(new Row { ["Id"] = 3, ["Country"] = "AU", ["Code"] = 1, ["Name"] = "Victoria" });
        city.Insert(new Row { ["Id"] = 1, ["Country"] = "NZ", ["Code"] = 1, ["Name"] = "Dunedin" });
        city.Insert(new Row { ["Id"] = 2, ["Country"] = "NZ", ["Code"] = 2, ["Name"] = "Timaru" });
        city.Insert(new Row { ["Id"] = 3, ["Country"] = "AU", ["Code"] = 1, ["Name"] = "Geelong" });
        city.Insert(new Row { ["Id"] = 4, ["Name"] = "Atlantis" });
    }

    [Fact]
    public void TwoColumns_CascadeUpdate_ChangesOnlyRowsMatchingBothColumns()
    {
        RegionsAndCities(CascadeUpdates);

        Enforcing(db => db.GetTable("Region").UpdateByPrimaryKey(1, new Row { ["Code"] = 5 }));

        Assert.Equal(new[]
        {
            "Code=1, Country=AU, Id=3, Name=Geelong", "Code=2, Country=NZ, Id=2, Name=Timaru",
            "Code=5, Country=NZ, Id=1, Name=Dunedin", "Id=4, Name=Atlantis",
        }, Rows("City"));
    }

    [Fact]
    public void TwoColumns_CascadeDelete_DeletesOnlyRowsMatchingBothColumns()
    {
        RegionsAndCities(CascadeDeletes);

        Enforcing(db => db.GetTable("Region").DeleteRow("Id", 1));

        Assert.Equal(new[] { "Code=1, Country=AU, Id=3, Name=Geelong", "Code=2, Country=NZ, Id=2, Name=Timaru", "Id=4, Name=Atlantis" },
                     Rows("City"));
    }

    [Fact]
    public void TwoColumns_CascadeNull_NullsBothColumns()
    {
        RegionsAndCities(CascadeNull);

        Enforcing(db => db.GetTable("Region").DeleteRow("Id", 3));

        Assert.Contains("Id=3, Name=Geelong", Rows("City"));
    }

    [Fact]
    public void TwoColumns_NoCascade_RefusesUpdateAndDeleteOfAKeyRowsReferTo()
    {
        RegionsAndCities(flags: 0);

        Refused(db => db.GetTable("Region").UpdateByPrimaryKey(1, new Row { ["Country"] = "FJ" }));
        Refused(db => db.GetTable("Region").DeleteRow("Id", 1));
    }

    [Fact]
    public void TwoColumns_AChildMustMatchOneParentRowInBothColumns()
    {
        RegionsAndCities(flags: 0);

        // Each value is some region's - AU is a country, 2 is a code - but no region is (AU, 2).
        Refused(db => db.GetTable("City").Insert(new Row { ["Id"] = 9, ["Country"] = "AU", ["Code"] = 2 }));
        Refused(db => db.GetTable("City").UpdateByPrimaryKey(1, new Row { ["Country"] = "AU", ["Code"] = 2 }));
        // Null in part refers to no row: Access refuses it.
        Refused(db => db.GetTable("City").Insert(new Row { ["Id"] = 9, ["Country"] = "NZ" }));
        Refused(db => db.GetTable("City").UpdateByPrimaryKey(1, new Row { ["Code"] = null }));

        Enforcing(db =>
        {
            db.GetTable("City").Insert(new Row { ["Id"] = 9, ["Country"] = "au", ["Code"] = 1 });   // text keys match without regard to case
            db.GetTable("City").UpdateByPrimaryKey(1, new Row { ["Country"] = null, ["Code"] = null });
        });
        Assert.Contains("Code=1, Country=au, Id=9", Rows("City"));
        Assert.Contains("Id=1, Name=Dunedin", Rows("City"));
    }

    // ── Chains of cascades ───────────────────────────────────────────────────

    /// <summary>
    /// Customer(K) ← Order(K, Customer) ← Line(Id, Order): an order's key is what its lines refer to, and
    /// OrderNote(Order) is a one-to-one extension of an order keyed by the order's own key.
    /// </summary>
    private void CustomersOrdersLines(int customerOrder, int orderLine, int orderNote)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var customer = db.CreateTable("Customer", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var order    = Create(db, "Order", new[] { Long("K"), Long("Customer"), Text("Name") }, "K", On("Customer"));
        var line     = Create(db, "Line", new[] { Long("Id"), Long("Order"), Text("Name") }, "Id", On("Order"));
        var note     = db.CreateTable("OrderNote", new[] { Long("Order"), Text("Name") }, primaryKey: "Order");
        Relate(db, "CustomerOrder", "Customer", "Order", customerOrder, ("K", "Customer"));
        Relate(db, "OrderLine", "Order", "Line", orderLine, ("K", "Order"));
        Relate(db, "OrderOrderNote", "Order", "OrderNote", orderNote | 1, ("K", "Order"));
        customer.Insert(new Row { ["K"] = 1, ["Name"] = "one" });
        customer.Insert(new Row { ["K"] = 2, ["Name"] = "two" });
        order.Insert(new Row { ["K"] = 100, ["Customer"] = 1, ["Name"] = "o100" });
        order.Insert(new Row { ["K"] = 101, ["Customer"] = 1, ["Name"] = "o101" });
        order.Insert(new Row { ["K"] = 200, ["Customer"] = 2, ["Name"] = "o200" });
        line.Insert(new Row { ["Id"] = 1, ["Order"] = 100, ["Name"] = "l1" });
        line.Insert(new Row { ["Id"] = 2, ["Order"] = 100, ["Name"] = "l2" });
        line.Insert(new Row { ["Id"] = 3, ["Order"] = 101, ["Name"] = "l3" });
        line.Insert(new Row { ["Id"] = 4, ["Order"] = 200, ["Name"] = "l4" });
        note.Insert(new Row { ["Order"] = 100, ["Name"] = "n100" });
        note.Insert(new Row { ["Order"] = 200, ["Name"] = "n200" });
    }

    [Fact]
    public void Chain_CascadeDelete_ReachesTheGrandchildren()
    {
        CustomersOrdersLines(CascadeDeletes, CascadeDeletes, CascadeDeletes);

        Enforcing(db => db.GetTable("Customer").DeleteRow("K", 1));

        Assert.Equal(new[] { "K=2, Name=two" }, Rows("Customer"));
        Assert.Equal(new[] { "Customer=2, K=200, Name=o200" }, Rows("Order"));
        Assert.Equal(new[] { "Id=4, Name=l4, Order=200" }, Rows("Line"));
        Assert.Equal(new[] { "Name=n200, Order=200" }, Rows("OrderNote"));
    }

    [Fact]
    public void Chain_CascadeDeleteThenNull_NullsTheGrandchildren()
    {
        CustomersOrdersLines(CascadeDeletes, CascadeNull, CascadeDeletes);

        Enforcing(db => db.GetTable("Customer").DeleteRow("K", 1));

        Assert.Equal(new[] { "Id=1, Name=l1", "Id=2, Name=l2", "Id=3, Name=l3", "Id=4, Name=l4, Order=200" }, Rows("Line"));
    }

    [Fact]
    public void Chain_CascadeDelete_IsRefusedWholeWhenAGrandchildForbidsIt()
    {
        CustomersOrdersLines(CascadeDeletes, orderLine: 0, CascadeDeletes);

        string message = Refused(db => db.GetTable("Customer").DeleteRow("K", 1));

        Assert.Contains("OrderLine", message);
    }

    [Fact]
    public void Chain_CascadeUpdate_ReachesTheGrandchildren()
    {
        CustomersOrdersLines(customerOrder: 0, CascadeUpdates, CascadeUpdates);

        Enforcing(db => db.GetTable("Order").UpdateByPrimaryKey(100, new Row { ["K"] = 150 }));

        Assert.Equal(new[] { "Customer=1, K=101, Name=o101", "Customer=1, K=150, Name=o100", "Customer=2, K=200, Name=o200" }, Rows("Order"));
        Assert.Equal(new[] { "Id=1, Name=l1, Order=150", "Id=2, Name=l2, Order=150", "Id=3, Name=l3, Order=101", "Id=4, Name=l4, Order=200" },
                     Rows("Line"));
        Assert.Equal(new[] { "Name=n100, Order=150", "Name=n200, Order=200" }, Rows("OrderNote"));

        using var read = Database.Open(_path);
        Assert.Equal("n100", read.GetTable("OrderNote").NewIndexCursor().FindRowByPrimaryKey(150)?["Name"]);
        Assert.Null(read.GetTable("OrderNote").NewIndexCursor().FindRowByPrimaryKey(100));
    }

    /// <summary>
    /// A key that is itself a foreign key: Root(K) ← Middle(K) ← Leaf(Id, K), each referring to the one before.
    /// </summary>
    private void RootMiddleLeaf(int rootMiddle, int middleLeaf)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var root   = db.CreateTable("Root", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var middle = db.CreateTable("Middle", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var leaf   = Create(db, "Leaf", new[] { Long("Id"), Long("K"), Text("Name") }, "Id", On("K"));
        Relate(db, "RootMiddle", "Root", "Middle", rootMiddle, ("K", "K"));
        Relate(db, "MiddleLeaf", "Middle", "Leaf", middleLeaf, ("K", "K"));
        foreach (int k in new[] { 1, 2 })
        {
            root.Insert(new Row { ["K"] = k, ["Name"] = "r" + k });
            middle.Insert(new Row { ["K"] = k, ["Name"] = "m" + k });
            leaf.Insert(new Row { ["Id"] = k, ["K"] = k, ["Name"] = "l" + k });
        }
    }

    [Fact]
    public void Chain_CascadeUpdate_OfAKeyThatIsAForeignKey_GoesAllTheWayDown()
    {
        RootMiddleLeaf(CascadeUpdates, CascadeUpdates);

        Enforcing(db => db.GetTable("Root").UpdateByPrimaryKey(1, new Row { ["K"] = 5 }));

        Assert.Equal(new[] { "K=2, Name=m2", "K=5, Name=m1" }, Rows("Middle"));
        Assert.Equal(new[] { "Id=1, K=5, Name=l1", "Id=2, K=2, Name=l2" }, Rows("Leaf"));
    }

    [Fact]
    public void Chain_CascadeUpdate_IsRefusedWholeWhenAGrandchildForbidsIt()
    {
        RootMiddleLeaf(CascadeUpdates, middleLeaf: 0);

        string message = Refused(db => db.GetTable("Root").UpdateByPrimaryKey(1, new Row { ["K"] = 5 }));

        Assert.Contains("MiddleLeaf", message);
    }

    [Fact]
    public void Chain_CascadeUpdate_IsRefusedWhenItWouldGiveTwoRowsOneKey()
    {
        RootMiddleLeaf(CascadeUpdates, CascadeUpdates);
        using (var db = Database.Open(_path))
            db.GetTable("Middle").Insert(new Row { ["K"] = 5, ["Name"] = "in the way" });   // no Root 5: enforcement is off

        string message = Refused(db => db.GetTable("Root").UpdateByPrimaryKey(1, new Row { ["K"] = 5 }));

        Assert.Contains("duplicate", message);
    }

    [Fact]
    public void ACascade_IsRefusedWhenItLeavesTheRowWithoutAParentInAnotherRelationship()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var first  = db.CreateTable("First", new[] { Long("K"), Text("Name") }, primaryKey: "K");
            var second = db.CreateTable("Second", new[] { Long("K"), Text("Name") }, primaryKey: "K");
            var both   = Create(db, "Both", new[] { Long("Id"), Long("K") }, "Id", On("K"));
            Relate(db, "FirstBoth", "First", "Both", CascadeUpdates, ("K", "K"));
            Relate(db, "SecondBoth", "Second", "Both", 0, ("K", "K"));
            first.Insert(new Row { ["K"] = 1 });
            second.Insert(new Row { ["K"] = 1 });
            second.Insert(new Row { ["K"] = 2 });
            both.Insert(new Row { ["Id"] = 1, ["K"] = 1 });
        }

        string message = Refused(db => db.GetTable("First").UpdateByPrimaryKey(1, new Row { ["K"] = 3 }));   // no Second 3
        Assert.Contains("SecondBoth", message);

        Enforcing(db => db.GetTable("First").UpdateByPrimaryKey(1, new Row { ["K"] = 2 }));                  // there is a Second 2
        Assert.Equal(new[] { "Id=1, K=2" }, Rows("Both"));
    }

    /// <summary>
    /// X ← Y and X ← Z cascade deletes, and Z ← Y forbids: deleting an X row deletes its Y and Z rows, and whether
    /// the Y row is still there to forbid the Z row's deletion depends on which relationship is taken first. Access
    /// takes them in the order they were made.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RelationshipsAreTakenInTheOrderTheyWereMade(bool yFirst)
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var x = db.CreateTable("X", new[] { Long("Id") }, primaryKey: "Id");
            var y = Create(db, "Y", new[] { Long("Id"), Long("X"), Long("Z") }, "Id", On("X"), On("Z"));
            var z = Create(db, "Z", new[] { Long("Id"), Long("X") }, "Id", On("X"));
            if (yFirst) Relate(db, "XY", "X", "Y", CascadeDeletes, ("Id", "X"));
            Relate(db, "XZ", "X", "Z", CascadeDeletes, ("Id", "X"));
            if (!yFirst) Relate(db, "XY", "X", "Y", CascadeDeletes, ("Id", "X"));
            Relate(db, "ZY", "Z", "Y", 0, ("Id", "Z"));
            x.Insert(new Row { ["Id"] = 1 });
            z.Insert(new Row { ["Id"] = 1, ["X"] = 1 });
            y.Insert(new Row { ["Id"] = 1, ["X"] = 1, ["Z"] = 1 });
        }

        if (yFirst)
        {
            Enforcing(db => db.GetTable("X").DeleteRow("Id", 1));
            Assert.Empty(Rows("X"));
            Assert.Empty(Rows("Y"));
            Assert.Empty(Rows("Z"));
        }
        else
        {
            Assert.Contains("ZY", Refused(db => db.GetTable("X").DeleteRow("Id", 1)));
        }
    }

    [Fact]
    public void ACycleOfCascadeDeletes_Ends()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var a = Create(db, "A", new[] { Long("Id"), Long("B") }, "Id", On("B"));
            var b = Create(db, "B", new[] { Long("Id"), Long("A") }, "Id", On("A"));
            Relate(db, "AB", "A", "B", CascadeDeletes, ("Id", "A"));
            Relate(db, "BA", "B", "A", CascadeDeletes, ("Id", "B"));
            a.Insert(new Row { ["Id"] = 1, ["B"] = 1 });
            a.Insert(new Row { ["Id"] = 2, ["B"] = 1 });
            a.Insert(new Row { ["Id"] = 3 });
            b.Insert(new Row { ["Id"] = 1, ["A"] = 1 });
        }

        Enforcing(db => db.GetTable("A").DeleteRow("Id", 1));

        Assert.Equal(new[] { "Id=3" }, Rows("A"));
        Assert.Empty(Rows("B"));
    }

    // ── A table that refers to itself ────────────────────────────────────────

    /// <summary>Staff(Id, Boss, Name), Boss referring to Staff.Id: 1 is her own boss, 2 and 3 report to 1, 4 to 2, 5 to no one.</summary>
    private void Staff(int flags)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var staff = Create(db, "Staff", new[] { Long("Id"), Long("Boss"), Text("Name") }, "Id", On("Boss"));
        Relate(db, "StaffStaff", "Staff", "Staff", flags, ("Id", "Boss"));
        staff.Insert(new Row { ["Id"] = 1, ["Boss"] = 1, ["Name"] = "top" });
        staff.Insert(new Row { ["Id"] = 2, ["Boss"] = 1, ["Name"] = "two" });
        staff.Insert(new Row { ["Id"] = 3, ["Boss"] = 1, ["Name"] = "three" });
        staff.Insert(new Row { ["Id"] = 4, ["Boss"] = 2, ["Name"] = "four" });
        staff.Insert(new Row { ["Id"] = 5, ["Name"] = "five" });
    }

    [Fact]
    public void SelfReference_CascadeUpdate_IncludesTheRowItself()
    {
        Staff(CascadeUpdates);

        Enforcing(db => db.GetTable("Staff").UpdateByPrimaryKey(1, new Row { ["Id"] = 10 }));

        Assert.Equal(new[]
        {
            "Boss=10, Id=10, Name=top", "Boss=10, Id=2, Name=two", "Boss=10, Id=3, Name=three", "Boss=2, Id=4, Name=four", "Id=5, Name=five",
        }, Rows("Staff"));
    }

    [Fact]
    public void SelfReference_CascadeDelete_DeletesTheWholeTreeBelow()
    {
        Staff(CascadeDeletes);

        Enforcing(db => db.GetTable("Staff").DeleteRow("Id", 1));

        Assert.Equal(new[] { "Id=5, Name=five" }, Rows("Staff"));
    }

    [Fact]
    public void SelfReference_CascadeNull_NullsThoseWhoReported()
    {
        Staff(CascadeNull);

        Enforcing(db => db.GetTable("Staff").DeleteRow("Id", 1));

        Assert.Equal(new[] { "Boss=2, Id=4, Name=four", "Id=2, Name=two", "Id=3, Name=three", "Id=5, Name=five" }, Rows("Staff"));
    }

    [Fact]
    public void SelfReference_NoCascade_RefusesWhatWouldLeaveSomeoneWithoutABoss()
    {
        Staff(flags: 0);

        Refused(db => db.GetTable("Staff").DeleteRow("Id", 1));                                      // 2 and 3 report to 1
        Refused(db => db.GetTable("Staff").UpdateByPrimaryKey(2, new Row { ["Id"] = 20 }));          // 4 reports to 2
        Refused(db => db.GetTable("Staff").UpdateByPrimaryKey(4, new Row { ["Boss"] = 99 }));        // there is no 99
        Refused(db => db.GetTable("Staff").Insert(new Row { ["Id"] = 6, ["Boss"] = 99 }));
        Refused(db => db.GetTable("Staff").UpdateByPrimaryKey(1, new Row { ["Id"] = 10 }));          // she would still report to 1
    }

    [Fact]
    public void SelfReference_ARowMayReferToItself()
    {
        Staff(flags: 0);

        Enforcing(db =>
        {
            var staff = db.GetTable("Staff");
            staff.Insert(new Row { ["Id"] = 6, ["Boss"] = 6, ["Name"] = "six" });       // her own boss from the start
            staff.UpdateByPrimaryKey(6, new Row { ["Id"] = 60, ["Boss"] = 60 });        // and still, under a new key
            staff.UpdateByPrimaryKey(4, new Row { ["Boss"] = 4 });
            staff.DeleteRow("Id", 4);                                                   // only she refers to herself
            staff.DeleteRow("Id", 60);
        });

        Assert.Equal(new[] { "Boss=1, Id=1, Name=top", "Boss=1, Id=2, Name=two", "Boss=1, Id=3, Name=three", "Id=5, Name=five" }, Rows("Staff"));
    }

    /// <summary>
    /// A foreign key that is null in part must match a parent's key, and Access holds null to match null: so it may
    /// refer to a row whose key is null in the same part, the row itself included, and to no other. (Refereed in
    /// ForeignKeysInAccessTests: Access adds the row that refers to itself by (1, null).)
    /// </summary>
    [Fact]
    public void SelfReference_OfTwoColumns_AKeyNullInPartRefersToAKeyNullInTheSamePart()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            Create(db, "Part", new[] { Long("Id"), Long("A"), Long("B"), Long("ParentA"), Long("ParentB") }, "Id", On("A", "B"), On("ParentA", "ParentB"));
            Relate(db, "PartPart", "Part", "Part", 0, ("A", "ParentA"), ("B", "ParentB"));
        }

        Enforcing(db =>
        {
            var part = db.GetTable("Part");
            part.Insert(new Row { ["Id"] = 1, ["A"] = 1, ["ParentA"] = 1 });                                   // (1, null) to itself
            part.Insert(new Row { ["Id"] = 2, ["A"] = 1, ["B"] = 2, ["ParentA"] = 1, ["ParentB"] = 2 });       // (1, 2) to itself
            part.Insert(new Row { ["Id"] = 3, ["A"] = 7, ["B"] = 8, ["ParentA"] = 1 });                        // to row 1's (1, null)
            part.Insert(new Row { ["Id"] = 4, ["A"] = 9 });                                                    // to nothing
        });
        Assert.Equal(4, Rows("Part").Count);

        Refused(db => db.GetTable("Part").Insert(new Row { ["Id"] = 5, ["A"] = 3, ["B"] = 3, ["ParentA"] = 7 }));   // no key is (7, null)
        Refused(db => db.GetTable("Part").Insert(new Row { ["Id"] = 5, ["A"] = 3, ["ParentA"] = 4 }));              // (4, null) is not its own (3, null)
    }

    [Fact]
    public void SelfReference_ALongChainOfCascades_DoesNotRunOutOfStack()
    {
        const int Length = 20_000;
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var link = Create(db, "Link", new[] { Long("Id"), Long("Previous") }, "Id", On("Previous"));
            Relate(db, "LinkLink", "Link", "Link", CascadeDeletes, ("Id", "Previous"));
            link.Insert(new Row { ["Id"] = 0 });
            for (int i = 1; i < Length; i++)
                link.Insert(new Row { ["Id"] = i, ["Previous"] = i - 1 });
            link.Insert(new Row { ["Id"] = Length });
        }

        Enforcing(db => db.GetTable("Link").DeleteRow("Id", 0));

        Assert.Equal(new[] { $"Id={Length}" }, Rows("Link"));
    }

    // ── Text keys ────────────────────────────────────────────────────────────

    private void TextParentAndChild(int flags, int childChars = 10)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var parent = db.CreateTable("Parent", new[] { Text("K", 10), Text("Name") }, primaryKey: "K");
        var child  = Create(db, "Child", new[] { Long("Id"), Text("K", childChars) }, "Id", On("K"));
        Relate(db, "ParentChild", "Parent", "Child", flags, ("K", "K"));
        parent.Insert(new Row { ["K"] = "abc", ["Name"] = "p" });
        parent.Insert(new Row { ["K"] = "lone", ["Name"] = "q" });
        child.Insert(new Row { ["Id"] = 1, ["K"] = "ABC" });
        child.Insert(new Row { ["Id"] = 2, ["K"] = "abc" });
    }

    [Fact]
    public void TextKey_MatchesWithoutRegardToCase_AndACaseChangeCascades()
    {
        TextParentAndChild(CascadeUpdates | CascadeDeletes);

        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey("abc", new Row { ["K"] = "Abc" }));
        Assert.Equal(new[] { "Id=1, K=Abc", "Id=2, K=Abc" }, Rows("Child"));

        Enforcing(db =>
        {
            db.GetTable("Child").UpdateByPrimaryKey(1, new Row { ["K"] = "ABC" });
            db.GetTable("Child").Insert(new Row { ["Id"] = 3, ["K"] = "LONE" });
            db.GetTable("Parent").DeleteRow("Name", "p");
        });
        Assert.Equal(new[] { "Id=3, K=LONE" }, Rows("Child"));
    }

    /// <summary>
    /// An insert finds its parent through the parent's primary key, text or not: the pages it reads do not grow with
    /// the parent table. Reading the table for a text key - as the index cursor's exact comparison once forced -
    /// read every parent row for every child added.
    /// </summary>
    [Theory]
    [InlineData(DataType.Text)]
    [InlineData(DataType.Long)]
    public void Insert_FindsItsParentThroughThePrimaryKey_HoweverLargeTheParent(DataType keyType)
    {
        Column Key() => keyType == DataType.Text ? Text("K", 20) : Long("K");
        object KeyOf(int i) => keyType == DataType.Text ? "Key" + i : i;
        object InAnotherCase(int i) => keyType == DataType.Text ? "KEY" + i : i;
        const int Small = 50, Large = 5_000;

        long ReadsOfOneInsert(int parents, int at)
        {
            using var db = OpenEnforcing();
            var child = db.GetTable("Child");
            child.Insert(new Row { ["Id"] = at, ["K"] = InAnotherCase(1) });   // the first insert reads the relationships
            long before = db.File.ReadCount;
            child.Insert(new Row { ["Id"] = at + 1, ["K"] = InAnotherCase(parents) });
            return db.File.ReadCount - before;
        }

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var parent = db.CreateTable("Parent", new[] { Key(), Text("Name", 200) }, primaryKey: "K");
            Create(db, "Child", new[] { Long("Id"), Key() }, "Id", On("K"));
            Relate(db, "ParentChild", "Parent", "Child", 0, ("K", "K"));
            for (int i = 1; i <= Small; i++)
                parent.Insert(new Row { ["K"] = KeyOf(i), ["Name"] = new string('n', 200) });
        }
        long small = ReadsOfOneInsert(Small, at: 1);

        int parentPages;
        using (var db = Database.Open(_path))
        {
            var parent = db.GetTable("Parent");
            for (int i = Small + 1; i <= Large; i++)
                parent.Insert(new Row { ["K"] = KeyOf(i), ["Name"] = new string('n', 200) });
            parentPages = UsageMap.GetOwnedPages(db.File.ReadPage(parent.Definition.UmapPageNumber), parent.Definition.OwnedPagesRow,
                                                 db.File.Format, db.File).Count;
        }
        long large = ReadsOfOneInsert(Large, at: 3);

        Assert.True(parentPages > 100, $"the parent is only {parentPages} pages");
        // A hundred times the rows: a level or two more of the key's tree, not a hundred times the pages.
        Assert.True(large <= small + 8, $"an insert read {small} pages with {Small} parents and {large} with {Large} ({parentPages} pages of them)");

        Refused(db => db.GetTable("Child").Insert(new Row { ["Id"] = 9, ["K"] = KeyOf(Large + 1) }));
    }

    [Fact]
    public void TextKey_NoCascade_ACaseChangeIsAChange()
    {
        TextParentAndChild(flags: 0);

        Refused(db => db.GetTable("Parent").UpdateByPrimaryKey("abc", new Row { ["K"] = "Abc" }));
        Refused(db => db.GetTable("Parent").DeleteRow("Name", "p"));
    }

    [Fact]
    public void ACascade_IsRefusedBeforeAnythingIsWritten_WhenAChildColumnIsTooShortForTheNewKey()
    {
        TextParentAndChild(CascadeUpdates, childChars: 4);

        byte[] before = File.ReadAllBytes(_path);
        using (var db = OpenEnforcing())
            Assert.Throws<ColumnValueTooLongException>(
                () => db.GetTable("Parent").UpdateByPrimaryKey("abc", new Row { ["K"] = "abcdefgh" }));
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused change altered the file");
    }

    // ── A change that fails while it is being written ────────────────────────

    [Fact]
    public void AChangeThatFailsPartWay_IsTakenBack()
    {
        ParentAndChild(CascadeDeletes);
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var child = db.GetTable("Child");
            using (db.File.BeginUndo())
            {
                // What a cascade writes: rows deleted and rewritten, keys moved, pages added - and no Commit.
                var pointers = new List<int>();
                var cursor = child.NewCursor();
                while (cursor.GetNextRow() is not null) pointers.Add(cursor.CurrentRowPointer);
                child.DeleteRowAt(pointers[0]);
                child.RewriteRowAt(pointers[1], new Row { ["K"] = 3, ["Name"] = new string('x', 20) });
                for (int i = 0; i < 500; i++)
                    child.Insert(new Row { ["Id"] = 1000 + i, ["K"] = 1, ["Name"] = "added" });
                Assert.Equal(503, child.ReadAllRows().Count);
            }

            // The same Database goes on working with what the file now holds.
            Assert.Equal(4, child.ReadAllRows().Count);
            Assert.Equal("c10", child.NewIndexCursor().FindRowByPrimaryKey(10)?["Name"]);
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "the file is not as it was");

        // And the change can then be made for real.
        Enforcing(db => db.GetTable("Parent").DeleteRow("K", 1));
        Assert.Equal(new[] { "Id=20, K=2, Name=c20", "Id=30, Name=orphan by choice" }, Rows("Child"));
    }

    [Fact]
    public void AnUndoScope_CannotBeginInsideAnother()
    {
        ParentAndChild(flags: 0);
        using var db = Database.Open(_path);

        using (db.File.BeginUndo())
            Assert.Throws<InvalidOperationException>(() => db.File.BeginUndo());

        db.File.BeginUndo().Dispose();   // the first has ended: another may begin
    }

    /// <summary>
    /// A cascade that passes every check and then cannot be written: the second child's row is as long as a row can
    /// be, and the longer key does not fit in it. By then the parent and the first child have been rewritten.
    /// </summary>
    [Fact]
    public void ACascadeThatFailsWhileItIsWritten_LeavesTheFileAsItWas()
    {
        const string Greek = "αβγδεζηθικλμνξοπρστυφχψω";   // not compressible: two bytes a character
        string Filler(int chars) => string.Concat(Enumerable.Repeat(Greek, chars / Greek.Length + 1)).Substring(0, chars);

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var parent = db.CreateTable("Parent", new[] { Text("K", 20), Text("Name") }, primaryKey: "K");
            var columns = new List<Column> { Long("Id"), Text("K", 20) };
            for (int i = 1; i <= 8; i++) columns.Add(Text("T" + i, 255));
            var child = Create(db, "Child", columns, "Id", On("K"));
            Relate(db, "ParentChild", "Parent", "Child", CascadeUpdates, ("K", "K"));

            parent.Insert(new Row { ["K"] = "a", ["Name"] = "p" });
            child.Insert(new Row { ["Id"] = 1, ["K"] = "a", ["T1"] = "small" });

            // The longest row the table takes: the last column is shortened until the row fits.
            var big = new Row { ["Id"] = 2, ["K"] = "a" };
            for (int i = 1; i <= 7; i++) big["T" + i] = Filler(255);
            int last = 255;
            while (true)
            {
                big["T8"] = Filler(last);
                try { child.Insert(big); break; }
                catch (Exception) when (last > 1) { last--; }
            }
            Assert.InRange(last, 2, 254);
        }
        byte[] before = File.ReadAllBytes(_path);

        using (var db = OpenEnforcing())
        {
            long written = db.File.WriteCount;
            var failure = Assert.Throws<InvalidOperationException>(
                () => db.GetTable("Parent").UpdateByPrimaryKey("a", new Row { ["K"] = "bbbbbbbbbbbbbbbbbbbb" }));

            // Not a refusal: no check failed, and pages had been written - the parent's and the first child's rows
            // at least - before the row that does not fit stopped it.
            Assert.DoesNotContain("Foreign-key violation", failure.Message);
            Assert.Contains("Row is too large", failure.Message);
            Assert.True(db.File.WriteCount - written >= 4, $"only {db.File.WriteCount - written} pages were written before the failure");

            // The same Database goes on with what the file holds: nothing of the cascade.
            Assert.Equal("p", db.GetTable("Parent").NewIndexCursor().FindRowByPrimaryKey("a")?["Name"]);
            Assert.Equal("a", db.GetTable("Child").NewIndexCursor().FindRowByPrimaryKey(1)?["K"]);
            Assert.Equal("a", db.GetTable("Child").NewIndexCursor().FindRowByPrimaryKey(2)?["K"]);
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "the file is not as it was");
    }

    // ── Relationships Access made ────────────────────────────────────────────

    // indexV2000.mdb: Table2(id) ← Table1(otherfk1) with cascade delete, Table3(id) ← Table1(otherfk2) with cascade
    // update. The file Java Jackcess's own foreign-key test uses.
    private void CopyOfIndexCorpusFile()
    {
        string source = TestCorpus.File("V2000", "indexV2000.mdb") ?? throw new FileNotFoundException("indexV2000.mdb is not in the corpus.");
        File.Copy(source, _path, overwrite: true);
    }

    [Fact]
    public void Corpus_CascadeDelete_DeletesTheRowsThatReferToTheDeletedOne()
    {
        CopyOfIndexCorpusFile();

        Enforcing(db => db.GetTable("Table2").DeleteRow("id", 1));

        Assert.Equal(new[] { "data=foo0, id=0", "data=foo2, id=2" }, Rows("Table2"));
        Assert.Equal(new[]
        {
            "data=baz0, id=0, otherfk1=0, otherfk2=10, otherfk3=0", "data=baz13, id=3, otherfk1=2, otherfk2=13, otherfk3=0",
        }, Rows("Table1"));
    }

    /// <summary>
    /// Table1 has had a column deleted, and the row encoder writes a row of such a table with its fixed columns in
    /// the wrong places (qcatco/exportmdb.interop#55). A cascade would rewrite Table1's rows without the caller
    /// having asked to touch the table, so until that is fixed it is refused, before anything is written.
    /// </summary>
    [Fact]
    public void Corpus_AChangeThatWouldRewriteARowOfATableWithADeletedColumn_IsRefused()
    {
        CopyOfIndexCorpusFile();
        byte[] before = File.ReadAllBytes(_path);

        using (var db = OpenEnforcing())
        {
            // Table3 ← Table1 cascades updates: rows 1 and 2 of Table1 would follow 11 to 45.
            var cascade = Assert.Throws<NotSupportedException>(() => db.GetTable("Table3").UpdateByPrimaryKey(11, new Row { ["id"] = 45 }));
            Assert.Contains("'Table1'", cascade.Message);
            Assert.Contains("#55", cascade.Message);
            // What does not match, by name: the deleted column was number 0, so id is numbered 1 where a row is written with it as 0.
            Assert.Contains("numbers column id 1", cascade.Message);
            Assert.Contains("as column 0", cascade.Message);

            // And a row of Table1 changed directly.
            Assert.Throws<NotSupportedException>(() => db.GetTable("Table1").UpdateByPrimaryKey(1, new Row { ["otherfk2"] = 13 }));
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused change altered the file");

        // A change that rewrites no row of Table1 is made: nothing refers to Table3's 12, so its key changes alone.
        Enforcing(db => db.GetTable("Table3").UpdateByPrimaryKey(12, new Row { ["id"] = 46 }));
        Assert.Contains("data=bar12, id=46", Rows("Table3"));
    }

    [Fact]
    public void Corpus_WhatWouldLeaveAnOrphan_IsRefused()
    {
        CopyOfIndexCorpusFile();

        // Table3 ← Table1 cascades updates, not deletes; Table2 ← Table1 deletes, not updates.
        Refused(db => db.GetTable("Table3").DeleteRow("id", 10));
        Refused(db => db.GetTable("Table2").UpdateByPrimaryKey(1, new Row { ["id"] = 45 }));
        Refused(db => db.GetTable("Table1").UpdateByPrimaryKey(0, new Row { ["otherfk1"] = 99 }));
        Refused(db => db.GetTable("Table1").Insert(new Row { ["id"] = 9, ["otherfk1"] = 0, ["otherfk2"] = 99, ["data"] = "x", ["otherfk3"] = 0 }));

        Enforcing(db => db.GetTable("Table3").DeleteRow("id", 12));   // nothing refers to 12
        Assert.Equal(3, Rows("Table3").Count);
    }
}
