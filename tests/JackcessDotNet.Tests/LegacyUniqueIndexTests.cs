using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A duplicate of a row that a unique index does not hold, in a file Access made and an earlier version of this
/// library wrote to (qcatco/exportmdb.interop#73): the real thing <see cref="UniqueIndexTrustTests"/> reconstructs.
/// <para>
/// Assets/engine-3ad3aed-rows-in-unique-indexes.mdb was made by tools/legacy-unique-indexes.ps1. DAO made
/// Codes(Id primary key, Code in the unique index CodeUnique, Name) with rows (1, 100) and (2, 200), and
/// Renamed(Id primary key, Name) with rows 1 and 2. Then this library as it was at commit 3ad3aed, the one in
/// ExportMdb.Interop 2.1.2, which kept a primary key under the value a row was found by, and no other index:
/// inserted Codes (3, 300); changed Codes 2 to Code 250; changed Renamed 2 to Id 7.
/// </para>
/// <para>
/// Access trusts its index: asked for a second row with Code 300 or 250, or a second Renamed 7, in this file, ACE
/// 12 and ACE 16 add it, and DAO, compacting the file afterwards, cannot build CodeUnique or Renamed's primary key
/// and leaves the tables without them. Compacted first, ACE refuses all three. They are refused here, with the
/// file as the earlier release left it, by a database opened with <see cref="Database.VerifyIndexes"/> - which
/// every one here is. <see cref="TrustedIndexTests"/> has the same file without it, where this library adds
/// them as Access does.
/// </para>
/// </summary>
public sealed class LegacyUniqueIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"legacy_unique_{Guid.NewGuid():N}.mdb");

    public LegacyUniqueIndexTests()
        => File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "engine-3ad3aed-rows-in-unique-indexes.mdb"), _path);

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private Database OpenVerifying()
    {
        var db = Database.Open(_path);
        db.VerifyIndexes = true;
        return db;
    }

    /// <summary>Codes' rows as "Id has Code", in Id order.</summary>
    private List<string> Codes()
    {
        using var db = OpenVerifying();
        return db.GetTable("Codes").ReadAllRows().OrderBy(row => (int)row["Id"]!).Select(row => $"{row["Id"]} has {row["Code"]}").ToList();
    }

    /// <summary>Renamed's rows as "Id Name", in table order.</summary>
    private List<string> Renamed()
    {
        using var db = OpenVerifying();
        return db.GetTable("Renamed").ReadAllRows().Select(row => $"{row["Id"]} {row["Name"]}").ToList();
    }

    /// <summary>
    /// What <paramref name="index"/> of <paramref name="table"/> holds under <paramref name="value"/>: the Id of
    /// the row each entry points at, or "no row" for an entry that points at none.
    /// </summary>
    private List<string> InTheIndex(string table, string index, int value)
    {
        using var db = OpenVerifying();
        var opened  = db.GetTable(table);
        var tree    = opened.Indexes.First(ix => ix.Name == index);
        var entries = new IndexWriter(db.File, new PageAllocator(db.File)).RowPointersForKey(tree.RootPageNumber, IndexKeys.Encode(tree.Columns, new object?[] { value }));
        var cursor  = opened.NewCursor();
        var ids     = new Dictionary<int, int>();
        while (cursor.GetNextRow() is { } row) ids[cursor.CurrentRowPointer] = (int)row["Id"]!;
        return entries.Select(pointer => ids.TryGetValue(pointer, out int id) ? id.ToString() : "no row").ToList();
    }

    /// <summary>Runs <paramref name="change"/>, expecting it refused as a duplicate with not a byte of the file changed.</summary>
    private string RefusedAsADuplicate(Action<Database> change)
    {
        byte[] before = File.ReadAllBytes(_path);
        string message;
        using (var db = OpenVerifying())
            message = Assert.Throws<InvalidOperationException>(() => change(db)).Message;
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused change altered the file");
        Assert.Contains("duplicate", message);
        return message;
    }

    [Fact]
    public void the_file_is_as_the_earlier_release_left_it()
    {
        Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300" }, Codes());
        Assert.Equal(new[] { "1 r1", "7 r2" }, Renamed());

        // The row it inserted is not in the unique index. The row it changed was written again in another place:
        // the index has it under the code it had, pointing where it was, and not under the code it has.
        Assert.Equal(new[] { "1" }, InTheIndex("Codes", "CodeUnique", 100));
        Assert.Empty(InTheIndex("Codes", "CodeUnique", 300));
        Assert.Empty(InTheIndex("Codes", "CodeUnique", 250));
        Assert.Equal(new[] { "no row" }, InTheIndex("Codes", "CodeUnique", 200));

        // The row whose primary key it changed is in the key under the value it was found by.
        Assert.Empty(InTheIndex("Renamed", "PrimaryKey", 7));
        Assert.Equal(new[] { "7" }, InTheIndex("Renamed", "PrimaryKey", 2));
    }

    [Theory]
    [InlineData(300)]   // the code of the row the earlier release inserted
    [InlineData(250)]   // the code it changed a row to
    [InlineData(100)]   // a code the index does hold
    public void a_second_row_with_a_code_is_refused_whether_or_not_the_unique_index_holds_the_first(int code)
    {
        string message = RefusedAsADuplicate(db => db.GetTable("Codes").Insert(new Row { ["Id"] = 4, ["Code"] = code, ["Name"] = "c4" }));

        Assert.Contains("CodeUnique", message);
        Assert.Contains($"{code} for Code", message);
        Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300" }, Codes());
    }

    [Fact]
    public void a_row_checked_before_it_is_written_is_refused_too()
    {
        RefusedAsADuplicate(db => db.GetTable("Codes").Validate(new Row { ["Id"] = 4, ["Code"] = 300, ["Name"] = "c4" }));
        RefusedAsADuplicate(db => db.GetTable("Codes").Validate(new Row { ["Id"] = 4, ["Code"] = 250, ["Name"] = "c4" }));

        using var db = OpenVerifying();
        db.GetTable("Codes").Validate(new Row { ["Id"] = 4, ["Code"] = 400, ["Name"] = "c4" });
    }

    [Fact]
    public void a_row_is_not_given_the_code_of_a_row_the_unique_index_does_not_hold()
    {
        RefusedAsADuplicate(db => db.GetTable("Codes").UpdateByPrimaryKey(1, new Row { ["Code"] = 300 }));
        RefusedAsADuplicate(db => db.GetTable("Codes").UpdateByPrimaryKey(3, new Row { ["Code"] = 250 }));

        Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300" }, Codes());
    }

    [Fact]
    public void a_second_row_with_a_primary_key_the_key_does_not_hold_is_refused()
    {
        string message = RefusedAsADuplicate(db => db.GetTable("Renamed").Insert(new Row { ["Id"] = 7, ["Name"] = "another" }));

        Assert.Contains("primary key", message);
        Assert.Equal(new[] { "1 r1", "7 r2" }, Renamed());
    }

    /// <summary>
    /// What an index says is there is still believed, as before. Codes 200 is an entry that points where a row
    /// was, and an entry whose row has gone is no row's: the code is taken, once. Renamed 2 is an entry that
    /// points at a row, the one that is now 7: a second row under that value is refused, as ACE refuses it in
    /// this file - no row has the value, but the key would hold two entries for it and lead to the wrong one.
    /// </summary>
    [Fact]
    public void a_value_a_row_had_before_the_earlier_release_changed_it_is_taken_only_when_its_entry_points_at_no_row()
    {
        using (var db = OpenVerifying())
            db.GetTable("Codes").Insert(new Row { ["Id"] = 4, ["Code"] = 200, ["Name"] = "c4" });

        Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300", "4 has 200" }, Codes());
        RefusedAsADuplicate(db => db.GetTable("Codes").Insert(new Row { ["Id"] = 5, ["Code"] = 200, ["Name"] = "c5" }));

        RefusedAsADuplicate(db => db.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "r2 again" }));
        Assert.Equal(new[] { "1 r1", "7 r2" }, Renamed());
    }

    [Fact]
    public void a_row_the_index_does_not_hold_is_rewritten_with_the_code_it_has_and_gains_its_entry()
    {
        using (var db = OpenVerifying())
        {
            db.GetTable("Codes").UpdateByPrimaryKey(3, new Row { ["Name"] = "renamed" });
            db.GetTable("Codes").UpdateByPrimaryKey(2, new Row { ["Code"] = 250, ["Name"] = "renamed" });
        }

        Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300" }, Codes());
        Assert.Equal(new[] { "3" }, InTheIndex("Codes", "CodeUnique", 300));
        Assert.Equal(new[] { "2" }, InTheIndex("Codes", "CodeUnique", 250));
    }

    /// <summary>
    /// What is known of an index follows what the session does to its rows: a code a row outside the index gives
    /// up, or is deleted with, is free at once, and the code it is given is held.
    /// </summary>
    [Fact]
    public void a_code_is_free_once_the_row_that_had_it_is_changed_or_deleted_in_the_same_session()
    {
        using (var db = OpenVerifying())
        {
            var codes = db.GetTable("Codes");
            Assert.Throws<InvalidOperationException>(() => codes.Insert(new Row { ["Id"] = 4, ["Code"] = 300, ["Name"] = "c4" }));

            codes.UpdateByPrimaryKey(3, new Row { ["Code"] = 301 });
            codes.Insert(new Row { ["Id"] = 4, ["Code"] = 300, ["Name"] = "c4" });
            Assert.Throws<InvalidOperationException>(() => codes.Insert(new Row { ["Id"] = 5, ["Code"] = 301, ["Name"] = "c5" }));

            codes.DeleteRow("Id", 2);
            codes.Insert(new Row { ["Id"] = 5, ["Code"] = 250, ["Name"] = "c5" });
            Assert.Throws<InvalidOperationException>(() => codes.Insert(new Row { ["Id"] = 6, ["Code"] = 250, ["Name"] = "c6" }));
        }

        Assert.Equal(new[] { "1 has 100", "3 has 301", "4 has 300", "5 has 250" }, Codes());

        // Every row is in the index now, where another session finds it.
        foreach (int code in new[] { 100, 301, 300, 250 })
            RefusedAsADuplicate(db => db.GetTable("Codes").Insert(new Row { ["Id"] = 9, ["Code"] = code, ["Name"] = "c9" }));
    }

    /// <summary>A change that is taken back is taken back in what is known of the index too: the row is there again, and not in the index.</summary>
    [Fact]
    public void a_row_whose_deletion_is_taken_back_is_still_one_the_index_does_not_hold()
    {
        using (var db = OpenVerifying())
        {
            var codes = db.GetTable("Codes");
            Assert.Throws<InvalidOperationException>(() => codes.Insert(new Row { ["Id"] = 4, ["Code"] = 300, ["Name"] = "c4" }));

            using (db.File.BeginUndo())
                codes.DeleteRow("Id", 3);   // never committed

            Assert.Throws<InvalidOperationException>(() => codes.Insert(new Row { ["Id"] = 4, ["Code"] = 300, ["Name"] = "c4" }));
        }

        Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300" }, Codes());
    }
}
