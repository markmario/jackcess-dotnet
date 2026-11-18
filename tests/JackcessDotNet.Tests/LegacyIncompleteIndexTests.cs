using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Database.EnforceForeignKeys"/> over a file Access made and an earlier version of this library wrote
/// to (qcatco/exportmdb.interop#64): the real thing <see cref="IncompleteIndexTests"/> reconstructs.
/// <para>
/// Assets/engine-3ad3aed-rows-in-access-tables.mdb was made by tools/legacy-incomplete-indexes.ps1. DAO made
/// Parent (1 to 4) and three tables whose K refers to Parent.K - Followers and Movers, cascading updates and
/// deletes, and Holders, cascading nothing - each with the index Access gives a relationship. Then this library as
/// it was at commit 3ad3aed, the one in ExportMdb.Interop 2.1.2, which kept a primary key and no other index:
/// inserted Followers 5, of parent 1; changed Movers 30 from parent 4 to parent 3; inserted Holders 5, of parent 2.
/// </para>
/// <para>
/// Every change here is made with <see cref="Database.VerifyIndexes"/> on, which is what such a file is for.
/// <see cref="TrustedIndexTests"/> has the same file without it, where this library does what Access does.
/// </para>
/// </summary>
public sealed class LegacyIncompleteIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"legacy_fk_{Guid.NewGuid():N}.mdb");

    public LegacyIncompleteIndexTests()
        => File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "engine-3ad3aed-rows-in-access-tables.mdb"), _path);

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private Database OpenEnforcing()
    {
        var db = Database.Open(_path);
        db.EnforceForeignKeys = true;
        db.VerifyIndexes = true;
        return db;
    }

    /// <summary>A table's rows as "Id of K", in Id order.</summary>
    private List<string> Rows(string table)
    {
        using var db = Database.Open(_path);
        return db.GetTable(table).Rows.ToList()
            .OrderBy(row => (int)row["Id"]!)
            .Select(row => $"{row["Id"]} of {(row.TryGetValue("K", out var k) ? k : "none")}")
            .ToList();
    }

    private List<int> Parents()
    {
        using var db = Database.Open(_path);
        return db.GetTable("Parent").Rows.Select(row => (int)row["K"]!).OrderBy(k => k).ToList();
    }

    /// <summary>
    /// What the index on <paramref name="table"/>'s foreign key holds for parent <paramref name="k"/>: the Id of
    /// the row each entry points at, or "no row" for an entry that points at none.
    /// </summary>
    private List<string> InTheIndexFor(string table, int k)
    {
        using var db = Database.Open(_path);
        var child   = db.GetTable(table);
        var index   = child.Indexes.First(ix => !ix.IsPrimaryKey && ix.Columns.Count == 1 && ix.Columns[0].Column.Name == "K");
        var entries = new IndexWriter(db.File, new PageAllocator(db.File)).RowPointersForKey(index.RootPageNumber, IndexKeys.Encode(index.Columns, new object?[] { k }));
        var cursor  = child.CreateCursor();
        var ids     = new Dictionary<int, int>();
        while (cursor.MoveNext()) ids[cursor.CurrentRowPointer] = (int)cursor.Current!["Id"]!;
        return entries.Select(pointer => ids.TryGetValue(pointer, out int id) ? id.ToString() : "no row").OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void the_file_is_as_the_earlier_release_left_it()
    {
        Assert.Equal(new[] { "5 of 1", "10 of 1", "11 of 1", "20 of 2", "40 of 4" }, Rows("Followers"));
        Assert.Equal(new[] { "30 of 3" }, Rows("Movers"));
        Assert.Equal(new[] { "1 of none", "5 of 2" }, Rows("Holders"));

        // The row it inserted is in no index but the primary key. The row it changed was written again in another
        // place: its entry is still under 4, pointing where the row was, and it has none under 3 - one entry in
        // the index and one row in the table, all the same.
        Assert.Equal(new[] { "10", "11" }, InTheIndexFor("Followers", 1));
        Assert.Empty(InTheIndexFor("Movers", 3));
        Assert.Equal(new[] { "no row" }, InTheIndexFor("Movers", 4));
        Assert.Empty(InTheIndexFor("Holders", 2));

        using var db = Database.Open(_path);
        Assert.Equal(1, db.GetTable("Movers").RowCount);
    }

    [Fact]
    public void a_parent_is_not_deleted_from_under_a_child_the_earlier_release_inserted()
    {
        using (var db = OpenEnforcing())
        {
            string message = Assert.Throws<ConstraintViolationException>(() => db.GetTable("Parent").DeleteRow("K", 2)).Message;
            Assert.Contains("ParentHolders", message);
        }

        Assert.Equal(new[] { 1, 2, 3, 4 }, Parents());
        Assert.Contains("20 of 2", Rows("Followers"));
    }

    [Fact]
    public void a_cascade_delete_takes_a_child_the_earlier_release_inserted()
    {
        using (var db = Database.Open(_path))
            db.GetTable("Holders").DeleteRow("Id", 5);   // nothing now forbids deleting parent 2, or 1

        using (var db = OpenEnforcing())
            db.GetTable("Parent").DeleteRow("K", 1);

        Assert.Equal(new[] { 2, 3, 4 }, Parents());
        Assert.Equal(new[] { "20 of 2", "40 of 4" }, Rows("Followers"));
    }

    [Fact]
    public void a_cascade_update_takes_a_child_the_earlier_release_inserted()
    {
        using (var db = OpenEnforcing())
            db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 9 });

        Assert.Equal(new[] { "5 of 9", "10 of 9", "11 of 9", "20 of 2", "40 of 4" }, Rows("Followers"));
        Assert.Equal(new[] { "10", "11", "5" }, InTheIndexFor("Followers", 9));
    }

    /// <summary>
    /// Movers' index has one entry and the table one row: a count finds nothing wrong. The entry is under 4, where
    /// the row was before the earlier release changed it, and the row is parent 3's.
    /// </summary>
    [Fact]
    public void a_cascade_delete_takes_a_child_the_earlier_release_moved_to_the_parent()
    {
        using (var db = OpenEnforcing())
            db.GetTable("Parent").DeleteRow("K", 3);

        Assert.Equal(new[] { 1, 2, 4 }, Parents());
        Assert.Empty(Rows("Movers"));
    }

    [Fact]
    public void a_cascade_delete_leaves_a_child_the_earlier_release_moved_from_the_parent()
    {
        using (var db = OpenEnforcing())
            db.GetTable("Parent").DeleteRow("K", 4);

        Assert.Equal(new[] { 1, 2, 3 }, Parents());
        Assert.Equal(new[] { "30 of 3" }, Rows("Movers"));
        Assert.Equal(new[] { "5 of 1", "10 of 1", "11 of 1", "20 of 2" }, Rows("Followers"));
    }

    [Fact]
    public void a_child_is_added_to_a_parent_and_refused_one_it_has_not()
    {
        using var db = OpenEnforcing();
        db.GetTable("Followers").Insert(new Row { ["Id"] = 6, ["K"] = 3, ["Name"] = "f6" });
        Assert.Throws<ConstraintViolationException>(() => db.GetTable("Followers").Insert(new Row { ["Id"] = 7, ["K"] = 8, ["Name"] = "f7" }));
    }
}
