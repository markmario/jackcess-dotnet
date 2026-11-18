using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// An index is taken at its word unless a caller asks otherwise, as Access and Java Jackcess take one
/// (qcatco/exportmdb.interop#78): <see cref="Database.VerifyIndexes"/> is off by default, and nothing here turns
/// it on. Two things follow, and both are held here.
/// <para>
/// Nothing is read to check an index: the first insert of a session, and the first change that keeps a
/// relationship, read the pages the ones after them read.
/// </para>
/// <para>
/// With a file an earlier version of this library left rows out of an index of, this library does what Access
/// does with the same file - what ACE 12 and ACE 16 were seen to do with these two, which ExportMdb.Interop's
/// EarlierEngineFilesInAccessTests holds ACE to: a duplicate of a row a unique index does not hold is added, and
/// a parent is deleted or re-keyed from under a child the index on its foreign key does not hold.
/// <see cref="LegacyUniqueIndexTests"/> and <see cref="LegacyIncompleteIndexTests"/> have the same files with the
/// option on.
/// </para>
/// </summary>
public sealed class TrustedIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"trusted_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();
    private static Column Text(string name, int chars = 20) => new ColumnBuilder(name, DataType.Text).MaxLength(chars).Build();

    private void CopyOf(string asset) => File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), _path, overwrite: true);

    /// <summary>A table's rows, each as its values in the order of <paramref name="columns"/>, by the first of them.</summary>
    private List<string> Rows(string table, params string[] columns)
    {
        using var db = Database.Open(_path);
        return db.GetTable(table).Rows.ToList()
            .OrderBy(row => (int)row[columns[0]]!)
            .Select(row => string.Join(" ", columns.Select(c => row[c] ?? "none")))
            .ToList();
    }

    private static long ReadsOf(Database db, Action change)
    {
        long before = db.File.ReadCount;
        change();
        return db.File.ReadCount - before;
    }

    private static int PagesOf(Database db, string table)
    {
        var definition = db.GetTable(table).Definition;
        return UsageMap.GetOwnedPages(db.File.ReadPage(definition.UmapPageNumber), definition.OwnedPagesRow, db.File.Format, db.File).Count;
    }

    // ── Nothing is read to check an index ────────────────────────────────────

    [Fact]
    public void the_option_is_off_unless_it_is_set()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            Assert.False(db.VerifyIndexes);
        using (var db = Database.Open(_path))
        {
            Assert.False(db.VerifyIndexes);
            db.VerifyIndexes = true;
            Assert.True(db.VerifyIndexes);
        }
    }

    [Fact]
    public void the_first_insert_of_a_session_reads_no_more_pages_than_the_ones_after_it()
    {
        const int Many = 20_000;
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", new[] { Long("Id"), Long("Code"), Text("Name", 100) }, new[] { "Id" },
                                       new[] { new IndexDefinition("OneCode", "Code") { IsUnique = true } });
            for (int i = 0; i < Many; i++)
                table.Insert(new Row { ["Id"] = i, ["Code"] = Many - i, ["Name"] = new string('n', 100) });
        }

        using (var db = Database.Open(_path))
        {
            // The first change of a session that enforces foreign keys also reads the relationships, once: that
            // is not what is counted here.
            db.EnforceForeignKeys = false;
            Assert.True(PagesOf(db, "T") > 500, $"the table is only {PagesOf(db, "T")} pages");
            var table = db.GetTable("T");
            Row RowOf(int id) => new() { ["Id"] = id, ["Code"] = id, ["Name"] = "added" };

            long first = ReadsOf(db, () => table.Insert(RowOf(Many + 1)));
            long later = Enumerable.Range(2, 20).Max(i => ReadsOf(db, () => table.Insert(RowOf(Many + i))));

            Assert.True(first <= later + 1, $"the first insert read {first} pages and those after it at most {later}");
            Assert.True(ReadsOf(db, () => table.Validate(RowOf(Many + 100))) <= later);
            Assert.Throws<ConstraintViolationException>(() => table.Insert(RowOf(Many + 7)));   // a duplicate the index holds
        }

        using var read = Database.Open(_path);
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "T", expectedRows: Many + 21);
    }

    [Fact]
    public void the_first_change_that_keeps_a_relationship_reads_no_more_pages_than_the_ones_after_it()
    {
        const int Parents = 2_000, ChildrenEach = 5;
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
            var child  = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Name", 100) }, new[] { "Id" },
                                        new[] { new IndexDefinition("ChildK", "K") });
            db.GetTable("MSysRelationships").Insert(new Row
            {
                ["ccolumn"] = 1, ["grbit"] = 0x1000 /* cascade deletes */, ["icolumn"] = 0,
                ["szColumn"] = "K", ["szObject"] = "Child", ["szReferencedColumn"] = "K", ["szReferencedObject"] = "Parent",
                ["szRelationship"] = "ParentChild",
            });
            for (int k = 1; k <= Parents; k++)
                parent.Insert(new Row { ["K"] = k, ["Name"] = "p" + k });
            for (int i = 0; i < Parents * ChildrenEach; i++)
                child.Insert(new Row { ["Id"] = i, ["K"] = 1 + i % Parents, ["Name"] = new string('c', 100) });
        }

        using (var db = Database.Open(_path))
        {
            db.EnforceForeignKeys = true;
            int childPages = PagesOf(db, "Child");
            Assert.True(childPages > 250, $"the child is only {childPages} pages");

            long first = ReadsOf(db, () => db.GetTable("Parent").DeleteRow("K", 2));
            long later = Enumerable.Range(3, 5).Max(k => ReadsOf(db, () => db.GetTable("Parent").DeleteRow("K", k)));

            Assert.True(first < childPages / 4, $"the first cascade delete read {first} pages of a child of {childPages}: the table was read");
            Assert.True(first <= later + later / 2, $"the first cascade delete read {first} pages and those after it at most {later}");
            Assert.Equal((Parents - 6) * ChildrenEach, db.GetTable("Child").RowCount);
        }

        using var read = Database.Open(_path);
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Child");
    }

    // ── With a file an earlier version left rows out of an index of: as Access ──

    /// <summary>
    /// The statements ACE was given, in the order it was given them, and what it did with each: it added the
    /// second row with Code 300, with Code 250 and with Id 7, and refused Id 2, which the primary key has an
    /// entry for, and Code 100, which a row has.
    /// </summary>
    [Fact]
    public void a_duplicate_of_a_row_a_unique_index_does_not_hold_is_added_as_access_adds_it()
    {
        CopyOf("engine-3ad3aed-rows-in-unique-indexes.mdb");

        using (var db = Database.Open(_path))
        {
            var codes   = db.GetTable("Codes");
            var renamed = db.GetTable("Renamed");
            codes.Insert(new Row { ["Id"] = 4, ["Code"] = 300, ["Name"] = "c4" });
            codes.Insert(new Row { ["Id"] = 5, ["Code"] = 250, ["Name"] = "c5" });
            renamed.Insert(new Row { ["Id"] = 7, ["Name"] = "r7" });

            Assert.Throws<ConstraintViolationException>(() => renamed.Insert(new Row { ["Id"] = 2, ["Name"] = "r2" }));
            Assert.Throws<ConstraintViolationException>(() => codes.Insert(new Row { ["Id"] = 8, ["Code"] = 100, ["Name"] = "c8" }));
        }

        Assert.Equal(new[] { "1 100", "2 250", "3 300", "4 300", "5 250" }, Rows("Codes", "Id", "Code"));
        Assert.Equal(new[] { "1 r1", "7 r2", "7 r7" }, Rows("Renamed", "Id", "Name"));
    }

    /// <summary>
    /// ACE deleted parent 3 and left Movers 30, which is parent 3's and is in the index under 4; deleted parent 2
    /// from under Holders 5, which is in no index, taking Followers 20, which is; and re-keyed parent 1 to 9 with
    /// Followers 10 and 11, passing Followers 5 by.
    /// </summary>
    [Fact]
    public void a_parent_is_deleted_and_re_keyed_from_under_a_child_the_index_does_not_hold_as_access_does_it()
    {
        CopyOf("engine-3ad3aed-rows-in-access-tables.mdb");

        using (var db = Database.Open(_path))
        {
            db.EnforceForeignKeys = true;
            db.GetTable("Parent").DeleteRow("K", 3);
            db.GetTable("Parent").DeleteRow("K", 2);
            db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 9 });
        }

        Assert.Equal(new[] { "4", "9" }, Rows("Parent", "K"));
        Assert.Equal(new[] { "30 3" }, Rows("Movers", "Id", "K"));
        Assert.Equal(new[] { "1 none", "5 2" }, Rows("Holders", "Id", "K"));
        Assert.Equal(new[] { "5 1", "10 9", "11 9", "40 4" }, Rows("Followers", "Id", "K"));
    }
}
