using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Index keys of more than 510 bytes that a file holds whole (qcatco/exportmdb.interop#26, review of
/// qcatco/jackcess-dotnet#10). Access cuts such a key at 510 bytes and ends it in a digest, and so does this
/// library now; before, it wrote the key whole, as Jackcess did before it cut keys. A file can therefore hold three
/// kinds of entry for a long key - Access's cut one, this library's (the same bytes), and a whole one - and a
/// whole one must be found, moved, removed and seen by a unique index like the others, or a delete leaves it
/// behind, an update leaves it beside the new one, and a duplicate of its value is taken.
/// <para>
/// Assets/engine-696ca52-long-keys.mdb is a database this library wrote at commit 696ca52, before it cut keys
/// (tools/legacy-long-keys.ps1 made it, with that commit's own code): the primary keys of its tables hold whole
/// keys. The other tests give a table of today's whole keys in every index - a unique one, one on two columns and
/// one on a Memo, in either sort order - by replacing each cut entry with the whole key.
/// </para>
/// </summary>
public sealed class LegacyLongKeyTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private static readonly string Long255 = new('é', 255);
    private static readonly string Long254e = new string('é', 254) + "e";
    private static readonly string CutAlikeStart = new string('Æ', 100) + new string('é', 115);
    private static readonly string CutAlike1 = CutAlikeStart + "èèèéèêéèéèëëéëêèèèêêèëéééëëëèêêéèëééëëéè";
    private static readonly string CutAlike2 = CutAlikeStart + "èèëêèééêèèëêéêéëéêêéêêéèéèëèèééèëêéëéééé";
    private static readonly string LongAe = new string('Æ', 100) + new string('é', 155);

    private static string PairA(int i) => new string('é', 200) + " " + (i % 3);
    private static string PairB(int i) => new string('è', 200) + " " + i.ToString("D2");

    /// <summary>A copy of the database this library wrote at 696ca52.</summary>
    private Database OpenFixture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"legacy_{Guid.NewGuid():N}.mdb");
        _paths.Add(path);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "engine-696ca52-long-keys.mdb"), path);
        return Database.Open(path);
    }

    /// <summary>
    /// What each row's entry in <paramref name="index"/> is stored under: "whole" or "cut" for a key over 510
    /// bytes, "short" otherwise. Asserts that every row has exactly one entry, under one of its two keys, and
    /// that the tree holds no other entry.
    /// </summary>
    private static List<string> StoredAs(Database db, string tableName, string indexName)
    {
        var table = db.GetTable(tableName);
        var index = table.Indexes.Single(ix => ix.Name == indexName);
        var entries = JetPages.LeafEntries(db.File, index.RootPageNumber);
        var rows = JetPages.Rows(db.File, IndexChecks.TdefOf(db, tableName), forKeys: true);

        var kinds = new List<string>();
        foreach (var (page, row, values) in rows)
        {
            var key = index.Columns.Select(c => values.TryGetValue(c.Column.Name, out var v) ? v : null).ToArray();
            if (index.IgnoresNulls && key.All(v => v is null)) continue;
            string whole = Convert.ToHexString(IndexKeys.EncodeWhole(index.Columns, key));
            string cut = Convert.ToHexString(IndexKeys.Encode(index.Columns, key));
            var own = entries.Where(e => e.RowPage == page && e.Row == row).Select(e => e.Hex).ToList();
            Assert.True(own.Count == 1, $"{tableName}.{indexName}: row {page}/{row} has {own.Count} entries");
            Assert.True(own[0] == whole || own[0] == cut, $"{tableName}.{indexName}: row {page}/{row} is under neither of its keys");
            kinds.Add(whole == cut ? "short" : own[0] == whole ? "whole" : "cut");
        }
        Assert.Equal(kinds.Count, entries.Count);
        Assert.Empty(JetPages.RuleBreaks(db.File, index.RootPageNumber, IndexChecks.TdefOf(db, tableName)));
        return kinds;
    }

    private static int Count(List<string> kinds, string kind) => kinds.Count(k => k == kind);

    // ── The file an earlier release wrote ───────────────────────────────────

    // What the fixture is: its primary keys hold whole keys of more than 510 bytes, which no release since holds.
    [Fact]
    public void The_file_written_at_696ca52_holds_long_keys_whole()
    {
        using var db = OpenFixture();
        var coded = StoredAs(db, "Coded", db.GetTable("Coded").Indexes.Single().Name);
        Assert.Equal((20, 5, 0), (Count(coded, "short"), Count(coded, "whole"), Count(coded, "cut")));
        var paired = StoredAs(db, "Paired", db.GetTable("Paired").Indexes.Single().Name);
        Assert.Equal((2, 12, 0), (Count(paired, "short"), Count(paired, "whole"), Count(paired, "cut")));

        var longest = JetPages.LeafEntries(db.File, db.GetTable("Coded").Indexes.Single().RootPageNumber).Max(e => e.Key.Length);
        Assert.True(longest > IndexKeys.MaxKeyLength, $"the longest key is {longest} bytes");
    }

    // Every row is still found by its key: such a value is looked for in the table.
    [Fact]
    public void Rows_whose_keys_an_earlier_release_wrote_whole_are_found()
    {
        using var db = OpenFixture();
        var coded = db.GetTable("Coded");
        Assert.Equal(21, coded.NewIndexCursor().FindRowByPrimaryKey(Long255)?["N"]);
        Assert.Equal(22, coded.NewIndexCursor().FindRowByPrimaryKey(Long254e)?["N"]);
        Assert.Equal(23, coded.NewIndexCursor().FindRowByPrimaryKey(CutAlike1)?["N"]);
        Assert.Equal(24, coded.NewIndexCursor().FindRowByPrimaryKey(CutAlike2)?["N"]);
        Assert.Equal(25, coded.NewIndexCursor().FindRowByPrimaryKey(LongAe)?["N"]);
        Assert.Equal(7, coded.NewIndexCursor().FindRowByPrimaryKey("short 07")?["N"]);
        Assert.Equal(5, db.GetTable("Paired").NewIndexCursor().FindRowByEntry(PairA(5), PairB(5))?["N"]);
    }

    // A deleted row's whole entry goes with it: the index holds the rows left and nothing else. A delete that
    // looked for the cut key only left the entry behind, and Access counted a row that was not there.
    [Fact]
    public void A_deleted_row_takes_its_whole_key_entry_with_it()
    {
        using var db = OpenFixture();
        var coded = db.GetTable("Coded");
        foreach (string code in new[] { Long255, CutAlike2, Long254e, CutAlike1, LongAe })
            coded.DeleteRow("Code", code);
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Coded", expectedRows: 20));

        var paired = db.GetTable("Paired");
        for (int i = 1; i <= 12; i++)
            paired.DeleteRow("N", i);
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Paired", expectedRows: 2));
    }

    // Of two rows whose keys are cut to the same entry - an earlier release took both, their whole keys being
    // different - the one deleted loses its own entry, and the other keeps its own.
    [Fact]
    public void Of_two_rows_cut_to_the_same_entry_the_one_deleted_loses_its_own()
    {
        using var db = OpenFixture();
        var coded = db.GetTable("Coded");
        coded.DeleteRow("Code", CutAlike1);
        var kinds = StoredAs(db, "Coded", coded.Indexes.Single().Name);
        Assert.Equal((20, 4), (Count(kinds, "short"), Count(kinds, "whole")));
        Assert.Equal(24, coded.NewIndexCursor().FindRowByPrimaryKey(CutAlike2)?["N"]);
        Assert.Null(coded.NewIndexCursor().FindRowByPrimaryKey(CutAlike1));
    }

    // An updated row moves: its whole entry goes and it gains one entry, under the cut key when the key is still
    // long, whether the key changed or another column did.
    [Fact]
    public void An_updated_row_leaves_its_whole_key_entry_and_gains_one_under_the_cut_key()
    {
        using var db = OpenFixture();
        var coded = db.GetTable("Coded");
        string indexName = coded.Indexes.Single().Name;

        coded.UpdateByPrimaryKey(Long255, new Row { ["Code"] = "now short" });           // the key, to a short one
        coded.UpdateByPrimaryKey(Long254e, new Row { ["N"] = 220 });                      // another column
        coded.UpdateByPrimaryKey(LongAe, new Row { ["Code"] = new string('ü', 255) });   // the key, to another long one
        var kinds = StoredAs(db, "Coded", indexName);
        Assert.Equal((21, 2, 2), (Count(kinds, "short"), Count(kinds, "whole"), Count(kinds, "cut")));
        Assert.Equal(220, coded.NewIndexCursor().FindRowByPrimaryKey(Long254e)?["N"]);
        Assert.Equal(25, coded.NewIndexCursor().FindRowByPrimaryKey(new string('ü', 255))?["N"]);

        // (A row of Paired cannot be updated: the library updates by a primary key of one column. Its two-column
        // key is moved in Whole_keys_..._are_replaced_by_cut_ones_on_update.)
        // Rows 23 and 24 are cut to one entry, which a unique key holds once: an earlier release took both, and
        // neither can be written again while the other is there - rewritten, it would be the other's duplicate.
        Assert.Throws<InvalidOperationException>(() => coded.UpdateByPrimaryKey(CutAlike1, new Row { ["N"] = 230 }));
        coded.DeleteRow("Code", CutAlike2);
        coded.UpdateByPrimaryKey(CutAlike1, new Row { ["N"] = 230 });
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Coded", expectedRows: 24));   // every long key left is cut
    }

    // A unique index sees a whole entry: a second row with the value is refused before anything changes, in any
    // case, and so is the value cut to the same entry as a whole one.
    [Fact]
    public void A_duplicate_of_a_value_whose_key_is_held_whole_is_refused_with_nothing_changed()
    {
        using var db = OpenFixture();
        var coded = db.GetTable("Coded");
        var paired = db.GetTable("Paired");
        coded.DeleteRow("Code", CutAlike2);   // leaves the first of the pair, whole
        var before = IndexChecks.AllPages(db);

        Assert.Throws<InvalidOperationException>(() => coded.Insert(new Row { ["Code"] = Long255, ["N"] = 99 }));
        Assert.Throws<InvalidOperationException>(() => coded.Insert(new Row { ["Code"] = Long255.ToUpperInvariant(), ["N"] = 99 }));
        Assert.Throws<InvalidOperationException>(() => coded.Insert(new Row { ["Code"] = CutAlike2, ["N"] = 99 }));   // one entry with the first, to Access
        Assert.Throws<InvalidOperationException>(() => coded.UpdateByPrimaryKey("short 01", new Row { ["Code"] = Long254e }));
        Assert.Throws<InvalidOperationException>(() => paired.Insert(new Row { ["A"] = PairA(4), ["B"] = PairB(4), ["N"] = 99 }));
        IndexChecks.AssertUnchanged(before, db);

        coded.Insert(new Row { ["Code"] = new string('é', 254) + "x", ["N"] = 99 });   // a long key no row has
        paired.Insert(new Row { ["A"] = PairA(4), ["B"] = PairB(13), ["N"] = 13 });
        Assert.Equal(1, Count(StoredAs(db, "Coded", coded.Indexes.Single().Name), "cut"));
    }

    // ── Whole keys in every kind of index, in either sort order ─────────────

    private Database NewDatabase(bool general)
    {
        string path = Path.Combine(Path.GetTempPath(), $"legacy_{Guid.NewGuid():N}{(general ? ".accdb" : ".mdb")}");
        _paths.Add(path);
        if (general) File.Copy(TestCorpus.File("V2010", "common1V2010.accdb") ?? throw new FileNotFoundException("common1V2010.accdb"), path);
        else Database.Create(path, JetVersion.Jet4).Dispose();
        return Database.Open(path);
    }

    private static string LongCode(int i) => new string('é', 253) + i.ToString("D2");   // over 510 bytes in either order

    private static string Notes(int i) => new string('é', 300) + " " + i;   // keyed by its first 255 characters

    /// <summary>
    /// A table with a unique text index, an index on two text columns and one on a Memo, whose 12 long rows are in
    /// each index under their whole keys, as an earlier release or Jackcess wrote them: every cut entry is taken
    /// out and the whole key put in its place.
    /// </summary>
    private static Table CreateWithWholeKeys(Database db)
    {
        var table = db.CreateTable("Kept", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Code", DataType.Text).MaxLength(255).Build(),
            new ColumnBuilder("A", DataType.Text).MaxLength(255).Build(),
            new ColumnBuilder("B", DataType.Text).MaxLength(255).Build(),
            new ColumnBuilder("Notes", DataType.Memo).Build(),
        }, new[] { "Id" }, new[]
        {
            new IndexDefinition("ByCode", "Code") { IsUnique = true },
            new IndexDefinition("ByPair", "A", "B"),
            new IndexDefinition("ByNotes", "Notes"),
        });
        for (int i = 1; i <= 12; i++)
            table.Insert(new Row { ["Id"] = i, ["Code"] = LongCode(i), ["A"] = PairA(i), ["B"] = PairB(i), ["Notes"] = Notes(i % 4) });
        for (int i = 13; i <= 20; i++)
            table.Insert(new Row { ["Id"] = i, ["Code"] = "short " + i, ["A"] = "a", ["B"] = "b" + i, ["Notes"] = "n" });

        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        int tdef = IndexChecks.TdefOf(db, "Kept");
        foreach (var index in table.Indexes.Where(ix => !ix.IsPrimaryKey))
            foreach (var (page, row, values) in JetPages.Rows(db.File, tdef, forKeys: true))
            {
                var key = index.Columns.Select(c => values.TryGetValue(c.Column.Name, out var v) ? v : null).ToArray();
                if (!IndexKeys.IsCut(index.Columns, key)) continue;
                var target = IndexWriter.Target.For(tdef, db.GetTable("Kept").Indexes.Single(ix => ix.Name == index.Name));
                writer.Remove(target, IndexKeys.Encode(index.Columns, key), RowPointer.Pack(page, row));
                writer.Insert(target, IndexKeys.EncodeWhole(index.Columns, key), RowPointer.Pack(page, row));
            }
        return db.GetTable("Kept");
    }

    private static readonly string[] IndexNames = { "ByCode", "ByPair", "ByNotes" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Whole_keys_in_a_unique_index_a_two_column_index_and_a_Memo_index_are_removed_with_their_rows(bool general)
    {
        using var db = NewDatabase(general);
        var table = CreateWithWholeKeys(db);
        foreach (string index in IndexNames)
            Assert.Equal((8, 12, 0), (Count(StoredAs(db, "Kept", index), "short"), Count(StoredAs(db, "Kept", index), "whole"), Count(StoredAs(db, "Kept", index), "cut")));

        for (int i = 1; i <= 12; i++)
            table.DeleteRow("Id", i);
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Kept", expectedRows: 8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Whole_keys_in_a_unique_index_a_two_column_index_and_a_Memo_index_are_replaced_by_cut_ones_on_update(bool general)
    {
        using var db = NewDatabase(general);
        var table = CreateWithWholeKeys(db);

        table.UpdateByPrimaryKey(1, new Row { ["Code"] = "short now" });      // one index's key
        table.UpdateByPrimaryKey(2, new Row { ["B"] = PairB(99) });           // the second column of another's
        table.UpdateByPrimaryKey(3, new Row { ["Notes"] = Notes(9) });        // the Memo
        // A row that is rewritten gains every one of its entries anew, under cut keys: rows 2 and 3 in ByCode too.
        Assert.Equal((9, 9, 2), (Count(StoredAs(db, "Kept", "ByCode"), "short"), Count(StoredAs(db, "Kept", "ByCode"), "whole"), Count(StoredAs(db, "Kept", "ByCode"), "cut")));
        Assert.Equal((8, 9, 3), (Count(StoredAs(db, "Kept", "ByPair"), "short"), Count(StoredAs(db, "Kept", "ByPair"), "whole"), Count(StoredAs(db, "Kept", "ByPair"), "cut")));

        for (int i = 1; i <= 12; i++)                                          // and every row, by its primary key
            table.UpdateByPrimaryKey(i, new Row { ["Id"] = 100 + i });
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Kept", expectedRows: 20));   // every long key now cut
        foreach (string index in IndexNames.Skip(1))
            Assert.Equal(12, Count(StoredAs(db, "Kept", index), "cut"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_duplicate_of_a_whole_key_in_a_unique_index_is_refused_and_other_indexes_take_the_value_again(bool general)
    {
        using var db = NewDatabase(general);
        var table = CreateWithWholeKeys(db);
        var before = IndexChecks.AllPages(db);
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 50, ["Code"] = LongCode(7) }));
        Assert.Throws<InvalidOperationException>(() => table.UpdateByPrimaryKey(15, new Row { ["Code"] = LongCode(7).ToUpperInvariant() }));
        IndexChecks.AssertUnchanged(before, db);

        // An index that is not unique holds the same value twice: one whole, one cut.
        table.Insert(new Row { ["Id"] = 50, ["Code"] = "another", ["A"] = PairA(7), ["B"] = PairB(7), ["Notes"] = Notes(3) });
        Assert.Equal((12, 1), (Count(StoredAs(db, "Kept", "ByPair"), "whole"), Count(StoredAs(db, "Kept", "ByPair"), "cut")));
        Assert.Equal((12, 1), (Count(StoredAs(db, "Kept", "ByNotes"), "whole"), Count(StoredAs(db, "Kept", "ByNotes"), "cut")));
        table.DeleteRow("Id", 7);
        table.DeleteRow("Id", 50);
        Assert.Equal((11, 0), (Count(StoredAs(db, "Kept", "ByPair"), "whole"), Count(StoredAs(db, "Kept", "ByPair"), "cut")));
    }

    // ── A foreign key on a long value whose entries are whole ───────────────

    private const int CascadeUpdates = 0x100;
    private const int CascadeDeletes = 0x1000;

    /// <summary>
    /// Parent(Code, Name) and Child(Id, Code, Name), Child.Code referring to Parent.Code, the parent's key and the
    /// child's index on Code holding the long codes whole. Parent "long" has three children; one short parent has one.
    /// </summary>
    private static void ParentAndChildWithWholeKeys(Database db, int flags)
    {
        Column Text(string name) => new ColumnBuilder(name, DataType.Text).MaxLength(255).Build();
        var parent = db.CreateTable("Parent", new[] { Text("Code"), Text("Name") }, primaryKey: "Code");
        var child = db.CreateTable("Child", new[] { new ColumnBuilder("Id", DataType.Long).Build(), Text("Code"), Text("Name") },
            new[] { "Id" }, new[] { new IndexDefinition("ChildCode", "Code") });
        db.GetTable("MSysRelationships").Insert(new Row
        {
            ["ccolumn"] = 1, ["grbit"] = flags, ["icolumn"] = 0,
            ["szColumn"] = "Code", ["szObject"] = "Child",
            ["szReferencedColumn"] = "Code", ["szReferencedObject"] = "Parent",
            ["szRelationship"] = "ParentChild",
        });

        parent.Insert(new Row { ["Code"] = LongCode(1), ["Name"] = "long" });
        parent.Insert(new Row { ["Code"] = LongCode(2), ["Name"] = "long, childless" });
        parent.Insert(new Row { ["Code"] = "short", ["Name"] = "short" });
        child.Insert(new Row { ["Id"] = 1, ["Code"] = LongCode(1), ["Name"] = "c1" });
        child.Insert(new Row { ["Id"] = 2, ["Code"] = LongCode(1).ToUpperInvariant(), ["Name"] = "c2" });
        child.Insert(new Row { ["Id"] = 3, ["Code"] = LongCode(1), ["Name"] = "c3" });
        child.Insert(new Row { ["Id"] = 4, ["Code"] = "short", ["Name"] = "c4" });

        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        foreach (var (tableName, indexName) in new[] { ("Parent", null as string), ("Child", "ChildCode") })
        {
            int tdef = IndexChecks.TdefOf(db, tableName);
            var index = db.GetTable(tableName).Indexes.Single(ix => indexName is null ? ix.IsPrimaryKey : ix.Name == indexName);
            foreach (var (page, row, values) in JetPages.Rows(db.File, tdef, forKeys: true))
            {
                var key = index.Columns.Select(c => values.TryGetValue(c.Column.Name, out var v) ? v : null).ToArray();
                if (!IndexKeys.IsCut(index.Columns, key)) continue;
                var target = IndexWriter.Target.For(tdef, index);
                writer.Remove(target, IndexKeys.Encode(index.Columns, key), RowPointer.Pack(page, row));
                writer.Insert(target, IndexKeys.EncodeWhole(index.Columns, key), RowPointer.Pack(page, row));
            }
        }
    }

    private static string PrimaryKeyOf(Database db, string table) => db.GetTable(table).Indexes.Single(ix => ix.IsPrimaryKey).Name;

    private static List<string> ChildCodes(Database db)
        => db.GetTable("Child").ReadAllRows().OrderBy(r => (int)r["Id"]!).Select(r => $"{r["Id"]}:{((string)r["Code"]!).Length}").ToList();

    // The enforcer finds the children of a parent through the index on the foreign key, and the parent of a child
    // through its key: entries held whole are found there like cut ones. Without a cascade the parent is
    // protected - by children an index search for the cut key alone would not have seen.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_parent_whose_children_are_indexed_under_whole_keys_is_still_protected(bool general)
    {
        using var db = NewDatabase(general);
        ParentAndChildWithWholeKeys(db, flags: 0);
        Assert.Equal(2, Count(StoredAs(db, "Parent", PrimaryKeyOf(db, "Parent")), "whole"));
        Assert.Equal(3, Count(StoredAs(db, "Child", "ChildCode"), "whole"));
        db.EnforceForeignKeys = true;
        var before = IndexChecks.AllPages(db);

        Assert.Throws<InvalidOperationException>(() => db.GetTable("Parent").DeleteRow("Code", LongCode(1)));
        Assert.Throws<InvalidOperationException>(() => db.GetTable("Parent").UpdateByPrimaryKey(LongCode(1), new Row { ["Code"] = "renamed" }));
        Assert.Throws<InvalidOperationException>(() => db.GetTable("Child").Insert(new Row { ["Id"] = 9, ["Code"] = LongCode(3) }));   // no such parent
        IndexChecks.AssertUnchanged(before, db);

        db.GetTable("Child").Insert(new Row { ["Id"] = 9, ["Code"] = LongCode(2) });   // a parent whose entry is whole is found
        Assert.Throws<InvalidOperationException>(() => db.GetTable("Parent").DeleteRow("Code", LongCode(2)));   // and it has a child now
    }

    // A cascade update finds the children under their whole keys and moves them: every row that named the
    // parent has its new key, and the old entries are gone from both tables' indexes.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_cascade_update_moves_the_children_whose_entries_are_whole(bool general, bool toAnotherLongKey)
    {
        using var db = NewDatabase(general);
        ParentAndChildWithWholeKeys(db, CascadeUpdates | CascadeDeletes);
        db.EnforceForeignKeys = true;

        string renamed = toAnotherLongKey ? LongCode(7) : "renamed";
        db.GetTable("Parent").UpdateByPrimaryKey(LongCode(1), new Row { ["Code"] = renamed });

        Assert.Equal(new[] { $"1:{renamed.Length}", $"2:{renamed.Length}", $"3:{renamed.Length}", "4:5" }, ChildCodes(db));
        Assert.All(db.GetTable("Child").ReadAllRows().Where(r => (int)r["Id"]! <= 3), r => Assert.Equal(renamed, r["Code"]));
        // Nothing whole is left in the child's index; the parent keeps the one whole key of the row not touched.
        var child = StoredAs(db, "Child", "ChildCode");
        Assert.Equal((0, toAnotherLongKey ? 3 : 0), (Count(child, "whole"), Count(child, "cut")));
        var parent = StoredAs(db, "Parent", PrimaryKeyOf(db, "Parent"));
        Assert.Equal((1, toAnotherLongKey ? 1 : 0), (Count(parent, "whole"), Count(parent, "cut")));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 4));
    }

    // A cascade delete finds them too: the parent, its three children and their entries go, and no other row.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cascade_delete_takes_the_children_whose_entries_are_whole(bool general)
    {
        using var db = NewDatabase(general);
        ParentAndChildWithWholeKeys(db, CascadeUpdates | CascadeDeletes);
        db.EnforceForeignKeys = true;

        db.GetTable("Parent").DeleteRow("Code", LongCode(1));

        Assert.Equal(new[] { "4:5" }, ChildCodes(db));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Child", expectedRows: 1));
        var parent = StoredAs(db, "Parent", PrimaryKeyOf(db, "Parent"));
        Assert.Equal((1, 1, 0), (Count(parent, "short"), Count(parent, "whole"), Count(parent, "cut")));

        db.GetTable("Parent").DeleteRow("Code", LongCode(2));
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Parent", expectedRows: 1));
    }
}
