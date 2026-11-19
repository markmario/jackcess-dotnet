using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What an index says of a row that is there is believed (a review of #52). An entry is judged a row's by making
/// the row's key and comparing, and where this library's key for a row is not the one Access wrote, an entry
/// Access wrote looks like one that is no row's. Taken out on that judgement, ACE could not then find, update or
/// delete the row, and took a duplicate of it; believed, nothing is lost. So an entry that points at a row of the
/// table is never taken out or moved by default, and makes a row with its key a duplicate whatever this library
/// makes of the row it points at. One that points at no row - a slot deleted or never used, a page that is not one
/// of the table's - may go when an entry is added or taken out under its key, and is no row's duplicate. With
/// <see cref="Database.VerifyIndexes"/>, a row's wrong entry is moved to the row's own key only where that key is
/// one this library makes as Access does (<see cref="IndexKeys.MatchesAccess"/>).
/// <para>
/// Assets/ace-divergent-keys.accdb (tools/ace-divergent-keys.ps1) is a file ACE made, with rows whose keys this
/// library does not make as Access does: Date/Time Extended values in the second form ACE stores, which ACE keys by
/// those bytes and this library by the date it reads (#51), and keys of several columns cut at 510 bytes, which it
/// did not make as Access does before #56. Assets/ace-second-form-foreign-key.accdb
/// (tools/ace-second-form-foreign-key.ps1) has a relationship on such a Date/Time Extended column. The other files
/// here are made by the tests, as an earlier version of this library left them.
/// </para>
/// </summary>
public sealed class AccessEntriesTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string Copy(string asset)
    {
        string path = Path.Combine(Path.GetTempPath(), $"access_entries_{Guid.NewGuid():N}{Path.GetExtension(asset)}");
        _paths.Add(path);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), path);
        File.SetAttributes(path, FileAttributes.Normal);
        return path;
    }

    private string NewPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"access_entries_{Guid.NewGuid():N}.mdb");
        _paths.Add(path);
        return path;
    }

    private static IndexWriter WriterOf(Database db) => new(db.File, new PageAllocator(db.File));

    private static int PointerOf(Table table, string column, object value)
    {
        var cursor = table.CreateCursor();
        while (cursor.MoveNext())
            if (Equals(cursor.Current![column], value)) return cursor.CurrentRowPointer;
        throw new InvalidOperationException($"{table.Name} has no row with {column} {value}.");
    }

    /// <summary>Every entry of every index of <paramref name="table"/>, as "index key -> page/row".</summary>
    private static HashSet<string> EntriesOf(Database db, string table)
        => db.GetTable(table).Indexes.SelectMany(ix => JetPages.LeafEntries(db.File, ix.RootPageNumber)
                                                       .Select(e => $"{ix.Name} {e.Hex} -> {e.RowPage}/{e.Row}")).ToHashSet();

    private static readonly string[] DivergentTables = { "Ext", "Cut", "Dbl", "Dt" };

    // ── A file Access made ──────────────────────────────────────────────────

    // A row added to each table of ACE's file, the indexes believed or verified: every entry Access wrote is
    // where it was, byte for byte, beside the new rows'. Verified, at b462fcb - which read the second form of Ext's
    // rows 2 and 3 as no value - they were entered under the key of no value, and their entries taken out of the
    // unique index: three rows under one key, which ACE could then neither find nor update.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_row_added_leaves_every_entry_ACE_wrote_where_it_was(bool verify)
    {
        string path = Copy("ace-divergent-keys.accdb");
        var before = new Dictionary<string, HashSet<string>>();
        using (var db = Database.Open(path))
            foreach (string table in DivergentTables) before[table] = EntriesOf(db, table);

        using (var db = Database.Open(path))
        {
            db.VerifyIndexes = verify;
            db.GetTable("Ext").Insert(new Row { ["Id"] = 10, ["Nt"] = "new", ["D"] = new DateTime(2021, 5, 5) });
            db.GetTable("Cut").Insert(new Row { ["Id"] = 10, ["A"] = "x", ["K"] = 1 });
            db.GetTable("Dbl").Insert(new Row { ["Id"] = 10, ["X"] = 2.5 });
            db.GetTable("Dt").Insert(new Row { ["Id"] = 10, ["T"] = new DateTime(2021, 5, 5) });
        }

        using (var db = Database.Open(path))
            foreach (string table in DivergentTables)
            {
                var after = EntriesOf(db, table);
                Assert.True(before[table].IsSubsetOf(after), $"{table}: entries ACE wrote are gone: {string.Join("; ", before[table].Except(after).Take(4))}");
                Assert.Equal(before[table].Count + db.GetTable(table).Indexes.Count, after.Count);   // and one more an index
            }
    }

    // ── Keys that differ in the last byte cut off ───────────────────────────

    // Pair holds (Ω, 0) and (Ω, 1), which ACE took. The key the digest before #56 gives (Ω, 1) - and (Ω, 2) - is
    // ACE's key of (Ω, 0), and the one it gives (Ω, 8356114) ACE's key of (Ω, 1). None is a duplicate of the row
    // whose entry is under that key: (Ω, 1) is rewritten, and (Ω, 2) and (Ω, 8356114) added, as ACE takes them;
    // (Ω, 0) and (Ω, 1) again are refused, as ACE refuses them. Each of the three was refused.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rows_ACE_took_whose_keys_differ_only_in_the_last_byte_cut_off_are_no_duplicates(bool verify)
    {
        string omega = new('Ω', 255);
        using var db = Database.Open(Copy("ace-divergent-keys.accdb"));
        db.VerifyIndexes = verify;
        var pair = db.GetTable("Pair");
        pair.UpdateByPrimaryKey(2, new Row { ["Nt"] = "engine" });
        pair.Insert(new Row { ["Id"] = 3, ["A"] = omega, ["X"] = 2, ["Nt"] = "engine" });
        pair.Insert(new Row { ["Id"] = 4, ["A"] = omega, ["X"] = 8356114, ["Nt"] = "engine" });

        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => pair.Insert(new Row { ["Id"] = 5, ["A"] = omega, ["X"] = 0 }));
        Assert.Throws<ConstraintViolationException>(() => pair.Insert(new Row { ["Id"] = 5, ["A"] = omega, ["X"] = 1 }));
        Assert.Throws<ConstraintViolationException>(() => pair.UpdateByPrimaryKey(3, new Row { ["X"] = 1 }));
        IndexChecks.AssertUnchanged(before, db);

        Assert.Equal(new[] { "engine", "engine", "engine" }, pair.Rows.Where(r => (int)r["Id"]! > 1).Select(r => (string)r["Nt"]!));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Pair", expectedRows: 4));   // each row under ACE's key
    }

    // ── Keys ACE made of a value's second form ──────────────────────────────

    /// <summary>Every entry of <paramref name="table"/>'s indexes that points at no row of it, as "index key".</summary>
    private static List<string> EntriesOfNoRow(Database db, string table)
    {
        var rows = JetPages.Rows(db.File, IndexChecks.TdefOf(db, table)).Select(r => (r.Page, r.Row)).ToHashSet();
        return db.GetTable(table).Indexes.SelectMany(ix => JetPages.LeafEntries(db.File, ix.RootPageNumber)
                .Where(e => !rows.Contains((e.RowPage, e.Row))).Select(e => $"{ix.Name} {e.Hex}")).ToList();
    }

    // Assets/ace-second-form-foreign-key.accdb (tools/ace-second-form-foreign-key.ps1): p2's key and child 2's
    // foreign key are Date/Time Extended values ACE stored in its second form, under keys this library does not make
    // (#51). Deleting p2 is refused, as ACE refuses it: the child is found by reading the table, not by searching
    // the index for a key it is not under. Verified, the child counted as held, the index was searched, p2 was
    // deleted from under child 2, and its entries were left pointing at no row.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_parent_whose_child_ACE_keyed_in_the_second_form_is_not_deleted_from_under_it(bool verify)
    {
        using var db = Database.Open(Copy("ace-second-form-foreign-key.accdb"));
        db.EnforceForeignKeys = true;
        db.VerifyIndexes = verify;
        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => db.GetTable("P").DeleteRow("Nm", "p2"));
        IndexChecks.AssertUnchanged(before, db);
    }

    // The child deleted, then the parent: each row's entries go with it, the ones ACE made of the second form found
    // by the row they point at. Found by this library's key, they were left behind, pointing at no row: ACE then
    // listed a row that was not there and took a child of it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rows_ACE_keyed_in_the_second_form_take_their_entries_with_them(bool verify)
    {
        using var db = Database.Open(Copy("ace-second-form-foreign-key.accdb"));
        db.EnforceForeignKeys = true;
        db.VerifyIndexes = verify;
        db.GetTable("C").DeleteRow("Id", 2);
        db.GetTable("P").DeleteRow("Nm", "p2");

        Assert.Empty(EntriesOfNoRow(db, "C"));
        Assert.Empty(EntriesOfNoRow(db, "P"));
        Assert.Equal(new[] { "p1" }, db.GetTable("P").Rows.Select(r => (string)r["Nm"]!));
        Assert.Equal(new[] { 1 }, db.GetTable("C").Rows.Select(r => (int)r["Id"]!));
    }

    // ── Entries an earlier version left ─────────────────────────────────────

    /// <summary>
    /// A unique index on Code as a version that kept only the primary key left it: row A, whose Code is now 2, has
    /// its entry under 1, as it was, and none under 2; row B, whose Code is 1, was added with no entry.
    /// </summary>
    private string StaleEntryAndARowWithout()
    {
        string path = NewPath();
        using var db = Database.Create(path, FileFormat.Access2000);
        var t = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Code", DataType.Int32) },
                               new[] { "Id" }, new[] { new IndexDefinition("U", "Code") { IsUnique = true } });
        t.Insert(new Row { ["Id"] = 1, ["Code"] = 2 });   // A
        t.Insert(new Row { ["Id"] = 2, ["Code"] = 1 });   // B
        var u      = t.Indexes.Single(ix => ix.Name == "U");
        var target = IndexWriter.Target.For(t.Definition.TdefPageNumber, u);
        byte[] Key(int code) => IndexKeys.Encode(u.Columns, new object?[] { code });
        int a = PointerOf(t, "Id", 1), b = PointerOf(t, "Id", 2);
        var writer = WriterOf(db);
        writer.Remove(target, Key(2), a);
        writer.Insert(target, Key(1), a);
        writer.Remove(target, Key(1), b);
        return path;
    }

    // A third row with Code 1 is a duplicate of B. Believed, the entry under 1 is a row's, so the row is refused,
    // as ACE refuses it; verified, the index is known to lack B, which has 1, so it is refused too. Taken, as it
    // was, the file held two rows with Code 1, and DAO's compaction dropped the index.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_duplicate_of_a_row_the_index_lacks_is_refused_beside_another_row_s_stale_entry(bool verify)
    {
        string path = StaleEntryAndARowWithout();
        using var db = Database.Open(path);
        db.VerifyIndexes = verify;
        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => db.GetTable("T").Insert(new Row { ["Id"] = 3, ["Code"] = 1 }));
        IndexChecks.AssertUnchanged(before, db);
    }

    // The same of a primary key whose entries an earlier version left in a cycle: 1 points at the row that is 2, 2
    // at the row that is 3, 3 at the row that is 1. A row with Id 1 is a duplicate, believed or verified.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_duplicate_of_a_primary_key_whose_entries_are_in_a_cycle_is_refused(bool verify)
    {
        string path = NewPath();
        using (var db = Database.Create(path, FileFormat.Access2000))
        {
            var t = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 } }, new[] { "Id" });
            foreach (int id in new[] { 1, 2, 3 }) t.Insert(new Row { ["Id"] = id, ["Name"] = "row " + id });
            var pk     = t.Indexes.Single();
            var target = IndexWriter.Target.For(t.Definition.TdefPageNumber, pk);
            byte[] Key(int id) => IndexKeys.Encode(pk.Columns, new object?[] { id });
            int r1 = PointerOf(t, "Id", 1), r2 = PointerOf(t, "Id", 2), r3 = PointerOf(t, "Id", 3);
            var writer = WriterOf(db);
            writer.Remove(target, Key(1), r1); writer.Remove(target, Key(2), r2); writer.Remove(target, Key(3), r3);
            writer.Insert(target, Key(1), r2); writer.Insert(target, Key(2), r3); writer.Insert(target, Key(3), r1);
        }
        using (var db = Database.Open(path))
        {
            db.VerifyIndexes = verify;
            var before = IndexChecks.AllPages(db);
            Assert.Throws<ConstraintViolationException>(() => db.GetTable("T").Insert(new Row { ["Id"] = 1, ["Name"] = "new 1" }));
            IndexChecks.AssertUnchanged(before, db);
        }
    }

    // ── Where a row is ──────────────────────────────────────────────────────

    // A row Access moved to another page (an overflow row: its slot holds where its values went) is a row of the
    // table. An entry that points at it is a row's: a row with that entry's key is refused, and the entry stays.
    [Fact]
    public void An_entry_that_points_at_a_row_Access_moved_to_another_page_is_a_row_s()
    {
        string path = Path.Combine(Path.GetTempPath(), $"access_entries_{Guid.NewGuid():N}.mdb");
        _paths.Add(path);
        File.Copy(TestCorpus.File("V2000", "overflowV2000.mdb") ?? throw new FileNotFoundException("overflowV2000.mdb"), path);
        File.SetAttributes(path, FileAttributes.Normal);
        using var db = Database.Open(path);
        var table = db.GetTable("Table1");
        table.AddIndex(new IndexDefinition("U", "data1") { IsUnique = true });
        table = db.GetTable("Table1");

        // An overflow row: a slot with 0x4000 alone.
        int moved = -1;
        foreach (int page in UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow, db.File.Format, db.File))
        {
            byte[] bytes = db.File.ReadPage(page);
            for (int row = 0; row < ByteUtil.GetShort(bytes, db.File.Format.OffsetDataNumRows) && moved < 0; row++)
                if ((ByteUtil.GetUShort(bytes, db.File.Format.OffsetDataRowTable + row * JetFormat.SizeRowEntry) & JetFormat.DeletedRowFlags) == JetFormat.OverflowRowFlag)
                    moved = RowPointer.Pack(page, row);
        }
        Assert.True(moved >= 0, "the file has no overflow row");

        // Its entry moved under a value no row has, as an earlier version could leave an updated row's.
        var u      = table.Indexes.Single(ix => ix.Name == "U");
        var target = IndexWriter.Target.For(table.Definition.TdefPageNumber, u);
        var own    = table.ReadRowAt(moved, table.Columns)!;
        var writer = WriterOf(db);
        writer.Remove(target, IndexKeys.Encode(u.Columns, new object?[] { own.TryGetValue("data1", out var value) ? value : null }), moved);
        writer.Insert(target, IndexKeys.Encode(u.Columns, new object?[] { "nobody's" }), moved);

        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["data1"] = "nobody's" }));
        IndexChecks.AssertUnchanged(before, db);
    }

    // ── A removal that sees the next leaf ───────────────────────────────────

    // A dead entry under a row's key, the first of the leaf after the one that holds the row's entry: entries are in
    // the order of their keys and then of their rows, so the next leaf can begin with the same key. The row is
    // deleted, and the dead entry goes with it, as it does when it is beside it on the same leaf.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_deleted_row_takes_a_dead_entry_under_its_key_with_it_from_the_next_leaf_too(bool sameLeaf)
    {
        using var db = Database.Create(NewPath(), FileFormat.Access2000);
        var t = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Code", DataType.Text) { MaxLength = 200 } },
                               new[] { "Id" }, new[] { new IndexDefinition("U", "Code") { IsUnique = true } });
        for (int i = 0; i < 300; i++) t.Insert(new Row { ["Id"] = i, ["Code"] = i.ToString("D4") + new string('k', 150) });
        var u      = t.Indexes.Single(ix => ix.Name == "U");
        var target = IndexWriter.Target.For(t.Definition.TdefPageNumber, u);
        var first  = JetPages.TreePages(db.File, u.RootPageNumber).Single(p => p.IsLeaf && p.Prev == 0);
        var last   = sameLeaf ? first.Entries[^2] : first.Entries[^1];
        int row    = RowPointer.Pack(last.RowPage, last.Row);
        int dead   = RowPointer.Pack(last.RowPage, 250);   // past the page's rows
        WriterOf(db).Insert(target, last.Key, dead);
        var nextFirst = JetPages.ReadIndexPage(db.File, first.Next).Entries[0];
        Assert.Equal(!sameLeaf, RowPointer.Pack(nextFirst.RowPage, nextFirst.Row) == dead);

        t.DeleteRowAt(row);
        Assert.DoesNotContain(JetPages.LeafEntries(db.File, u.RootPageNumber), e => RowPointer.Pack(e.RowPage, e.Row) == dead);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 299));
    }
}
