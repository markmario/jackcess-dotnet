using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Entries that are no row's, which a version of this library that did not keep every index left behind (#36): an
/// entry that points where a row was, and an entry under the value a row had before it was changed in place.
/// Access follows either - ACE fails a query that comes to the first ("Not a valid bookmark"), a range as well
/// as the key itself, and answers a query for the second's key with the row that no longer has it - and it does
/// so whether or not the row that does have the key has an entry of its own beside it. So when this library adds
/// or takes out an entry under a key a unique index compares, and the tree holds another under that key, the ones
/// that are no row's are taken out, and a row found under a key it does not have is entered under its own.
/// ExportMdb.Interop's EarlierEngineFilesInAccessTests asks ACE through the index afterwards.
/// <para>
/// The file is Assets/engine-3ad3aed-rows-in-unique-indexes.mdb (<see cref="LegacyUniqueIndexTests"/> says how it
/// was made): CodeUnique has an entry for 200 that points at no row, and Renamed's primary key an entry for 2
/// that points at the row that is 7, which has none under 7.
/// </para>
/// </summary>
public sealed class EntriesOfNoRowTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"entries_of_no_row_{Guid.NewGuid():N}.mdb");

    public EntriesOfNoRowTests()
        => File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "engine-3ad3aed-rows-in-unique-indexes.mdb"), _path);

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static Row Code(int id, int code) => new() { ["Id"] = id, ["Code"] = code, ["Name"] = "c" + id };

    private static IndexWriter WriterOf(Database db) => new(db.File, new PageAllocator(db.File));

    /// <summary>
    /// What <paramref name="index"/> of <paramref name="table"/> holds under <paramref name="value"/>: the Id of
    /// the row each entry points at, or "no row" for an entry that points at none.
    /// </summary>
    private static List<string> InTheIndex(Database db, string table, string index, int value)
    {
        var opened  = db.GetTable(table);
        var tree    = opened.Indexes.First(ix => ix.Name == index);
        var entries = WriterOf(db).RowPointersForKey(tree.RootPageNumber, IndexKeys.Encode(tree.Columns, new object?[] { value }));
        var cursor  = opened.CreateCursor();
        var ids     = new Dictionary<int, int>();
        while (cursor.MoveNext()) ids[cursor.CurrentRowPointer] = (int)cursor.Current!["Id"]!;
        return entries.Select(pointer => ids.TryGetValue(pointer, out int id) ? id.ToString() : "no row").ToList();
    }

    private List<string> InTheIndex(string table, string index, int value)
    {
        using var db = Database.Open(_path);
        return InTheIndex(db, table, index, value);
    }

    private static int PointerOf(Database db, string table, int id)
    {
        var cursor = db.GetTable(table).CreateCursor();
        while (cursor.MoveNext())
            if ((int)cursor.Current!["Id"]! == id) return cursor.CurrentRowPointer;
        throw new InvalidOperationException($"{table} has no row {id}.");
    }

    // ── An index taken at its word ───────────────────────────────────────────

    [Fact]
    public void a_row_added_under_a_key_whose_entry_points_at_no_row_is_the_only_one_the_index_holds_under_it()
    {
        Assert.Equal(new[] { "no row" }, InTheIndex("Codes", "CodeUnique", 200));

        using (var db = Database.Open(_path))
            db.GetTable("Codes").Insert(Code(6, 200));

        Assert.Equal(new[] { "6" }, InTheIndex("Codes", "CodeUnique", 200));
        using var read = Database.Open(_path);
        Assert.Throws<ConstraintViolationException>(() => read.GetTable("Codes").Insert(Code(7, 200)));
    }

    [Fact]
    public void a_row_added_under_a_key_whose_entry_points_at_a_row_with_another_key_is_taken_and_that_row_is_entered_under_its_own()
    {
        Assert.Equal(new[] { "7" }, InTheIndex("Renamed", "PrimaryKey", 2));
        Assert.Empty(InTheIndex("Renamed", "PrimaryKey", 7));

        using (var db = Database.Open(_path))
            db.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "new 2" });

        Assert.Equal(new[] { "2" }, InTheIndex("Renamed", "PrimaryKey", 2));
        Assert.Equal(new[] { "7" }, InTheIndex("Renamed", "PrimaryKey", 7));

        using var read = Database.Open(_path);
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Renamed", expectedRows: 3);
        Assert.Throws<ConstraintViolationException>(() => read.GetTable("Renamed").Insert(new Row { ["Id"] = 7, ["Name"] = "another 7" }));
        Assert.Throws<ConstraintViolationException>(() => read.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "another 2" }));
    }

    /// <summary>
    /// The row that is 7 cannot be entered under 7 when another row is: a unique index holds one row under a key.
    /// Its entry under 2 goes all the same, so the new row is the one found under 2.
    /// </summary>
    [Fact]
    public void a_row_found_under_a_key_it_does_not_have_is_not_entered_beside_another_row_with_its_own()
    {
        using (var db = Database.Open(_path))
        {
            var renamed = db.GetTable("Renamed");
            renamed.Insert(new Row { ["Id"] = 7, ["Name"] = "second 7" });   // added: the key has nothing under 7
            renamed.Insert(new Row { ["Id"] = 2, ["Name"] = "new 2" });
        }

        using var read = Database.Open(_path);
        Assert.Equal(new[] { "2" }, InTheIndex(read, "Renamed", "PrimaryKey", 2));
        Assert.Equal(new[] { "7" }, InTheIndex(read, "Renamed", "PrimaryKey", 7));   // one entry, the second 7's
        Assert.Equal(new[] { "r1", "r2", "second 7", "new 2" }, read.GetTable("Renamed").Rows.Select(row => (string)row["Name"]!));
    }

    [Fact]
    public void a_check_that_writes_nothing_leaves_the_entry_where_it_is()
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            db.GetTable("Codes").Validate(Code(6, 200));
            db.GetTable("Renamed").Validate(new Row { ["Id"] = 2, ["Name"] = "new 2" });
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a check altered the file");
    }

    /// <summary>
    /// The state the file was left in by a row added before entries that are no row's were taken out: the row's
    /// entry and, before it, one that points at no row. Taking the row's entry out takes the other with it.
    /// </summary>
    [Fact]
    public void an_entry_that_points_at_no_row_goes_with_the_entry_of_the_row_deleted_from_beside_it()
    {
        using (var db = Database.Open(_path))
        {
            var codes = db.GetTable("Codes");
            var tree  = codes.Indexes.First(ix => ix.Name == "CodeUnique");
            byte[] key = IndexKeys.Encode(tree.Columns, new object?[] { 200 });
            int nowhere = WriterOf(db).RowPointersForKey(tree.RootPageNumber, key).Single();

            codes.Insert(Code(6, 200));
            WriterOf(db).Insert(IndexWriter.Target.For(codes.Definition.TdefPageNumber, tree), key, nowhere);
            Assert.Equal(new[] { "no row", "6" }, InTheIndex(db, "Codes", "CodeUnique", 200));

            codes.DeleteRow("Id", 6);
            Assert.Empty(InTheIndex(db, "Codes", "CodeUnique", 200));
        }
    }

    [Fact]
    public void an_entry_that_points_at_no_row_goes_when_the_row_beside_it_gives_the_key_up()
    {
        using (var db = Database.Open(_path))
        {
            var codes = db.GetTable("Codes");
            var tree  = codes.Indexes.First(ix => ix.Name == "CodeUnique");
            byte[] key = IndexKeys.Encode(tree.Columns, new object?[] { 200 });
            int nowhere = WriterOf(db).RowPointersForKey(tree.RootPageNumber, key).Single();

            codes.Insert(Code(6, 200));
            WriterOf(db).Insert(IndexWriter.Target.For(codes.Definition.TdefPageNumber, tree), key, nowhere);

            codes.UpdateByPrimaryKey(6, new Row { ["Code"] = 201 });
            Assert.Empty(InTheIndex(db, "Codes", "CodeUnique", 200));
            Assert.Equal(new[] { "6" }, InTheIndex(db, "Codes", "CodeUnique", 201));
        }
    }

    /// <summary>
    /// Two rows with one key, each with its entry - what Access leaves when it adds a duplicate of a row a unique
    /// index did not hold. Both are rows with that key, and both entries stay.
    /// </summary>
    [Fact]
    public void an_entry_of_a_row_that_has_its_key_stays_whatever_is_added_beside_it()
    {
        using (var db = Database.Open(_path))
        {
            var codes = db.GetTable("Codes");
            codes.Insert(Code(4, 300));   // added: CodeUnique does not hold row 3, which has 300

            // Row 3 gains the entry it lacks, beside row 4's.
            var row3 = codes.Rows.First(row => (int)row["Id"]! == 3);
            var entries = codes.IndexEntries.EntriesFor(row3, enforce: false).Where(entry => !entry.Tree.IsPrimaryKey).ToList();
            codes.IndexEntries.Add(entries, PointerOf(db, "Codes", 3));

            Assert.Equal(new[] { "3", "4" }, InTheIndex(db, "Codes", "CodeUnique", 300).OrderBy(id => id));
        }
    }

    // ── An index that is checked ─────────────────────────────────────────────

    /// <summary>
    /// With <see cref="Database.VerifyIndexes"/> an index is read whole before it is believed, and what was seen
    /// then to be no row's is taken out when the table's indexes are next changed, whatever key the change is
    /// under: ACE fails a range that passes an entry pointing at no row.
    /// </summary>
    [Fact]
    public void with_indexes_verified_every_entry_seen_to_be_no_rows_goes_at_the_next_change_to_the_table()
    {
        using (var db = Database.Open(_path))
        {
            db.VerifyIndexes = true;
            db.GetTable("Codes").Insert(Code(6, 600));
            db.GetTable("Renamed").Insert(new Row { ["Id"] = 9, ["Name"] = "r9" });
        }

        Assert.Empty(InTheIndex("Codes", "CodeUnique", 200));
        Assert.Empty(InTheIndex("Renamed", "PrimaryKey", 2));
        Assert.Equal(new[] { "7" }, InTheIndex("Renamed", "PrimaryKey", 7));

        using var read = Database.Open(_path);
        read.VerifyIndexes = true;
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Renamed", expectedRows: 3);
        Assert.Throws<ConstraintViolationException>(() => read.GetTable("Renamed").Insert(new Row { ["Id"] = 7, ["Name"] = "another 7" }));
        read.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "new 2" });
    }

    [Fact]
    public void with_indexes_verified_a_check_that_writes_nothing_takes_nothing_out()
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            db.VerifyIndexes = true;
            db.GetTable("Codes").Validate(Code(6, 600));
            Assert.Throws<ConstraintViolationException>(() => db.GetTable("Codes").Insert(Code(6, 300)));
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a check or a refused row altered the file");
    }

    // ── An index that is as its rows are ─────────────────────────────────────

    /// <summary>
    /// Entries under one key are what an index that is not unique is for, and what a unique index has for rows
    /// with a null in it. No row is read to see whether such an entry is a row's: a row added among two thousand
    /// with its key reads no more pages than one added under a key of its own.
    /// </summary>
    [Theory]
    [InlineData(false)]   // an index that is not unique, every row with one code
    [InlineData(true)]    // a unique index, every row with no code
    public void a_row_added_beside_many_with_its_key_reads_no_row_to_see_whether_their_entries_are_rows(bool unique)
    {
        string path = Path.Combine(Path.GetTempPath(), $"entries_of_no_row_many_{Guid.NewGuid():N}.mdb");
        try
        {
            using var db = Database.Create(path, FileFormat.Access2000);
            var columns = new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Code", DataType.Int32) };
            var alike   = db.CreateTable("Alike", columns, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") { IsUnique = unique } });
            var apart   = db.CreateTable("Apart", columns, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") { IsUnique = unique } });
            object? code = unique ? null : 7;
            for (int i = 0; i < 2_000; i++)
            {
                alike.Insert(new Row { ["Id"] = i, ["Code"] = code });
                apart.Insert(new Row { ["Id"] = i, ["Code"] = i });
            }

            long ReadsOf(Table table, Row row)
            {
                long before = db.File.ReadCount;
                table.Insert(row);
                return db.File.ReadCount - before;
            }
            long most = 0, mostApart = 0;
            for (int i = 2_000; i < 2_200; i++)
            {
                most      = Math.Max(most, ReadsOf(alike, new Row { ["Id"] = i, ["Code"] = code }));
                mostApart = Math.Max(mostApart, ReadsOf(apart, new Row { ["Id"] = i, ["Code"] = i }));
            }
            Assert.True(most <= mostApart + 2, $"a row beside 2,000 with its key read {most} pages; one under a key of its own, {mostApart}");

            IndexChecks.AssertEveryIndexHoldsTheRows(db, "Alike", expectedRows: 2_200);
        }
        finally { File.Delete(path); }
    }
}
