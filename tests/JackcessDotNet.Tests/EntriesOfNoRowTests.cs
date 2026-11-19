using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Entries that are no row's, which a version of this library that did not keep every index left behind (#36): an
/// entry that points where a row was, and an entry under the value a row had before it was changed in place.
/// Access follows either - ACE fails a query that comes to the first ("Not a valid bookmark"), a range as well
/// as the key itself, and answers a query for the second's key with the row that no longer has it - and it does
/// so whether or not the row that does have the key has an entry of its own beside it. So when this library adds
/// or takes out an entry under a key a unique index compares, and the tree holds another under that key, one that
/// points at no row is taken out. One that points at a row is believed, as Access believes it, whatever this
/// library makes of that row's key (<see cref="AccessEntriesTests"/>): the row is a duplicate. Only with
/// <see cref="Database.VerifyIndexes"/>, the row's key being one this library makes as Access does, is the entry
/// the row's wrong one, taken out, and the row entered under its own. ExportMdb.Interop's
/// EarlierEngineFilesInAccessTests asks ACE through the index afterwards.
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

    // Believed, the entry for 2 is the row's that it points at, whatever that row's key: a row with Id 2 is refused,
    // as ACE refuses it in this file, and nothing changes.
    [Fact]
    public void a_row_added_under_a_key_whose_entry_points_at_a_row_is_refused_whatever_that_row_s_key()
    {
        using var db = Database.Open(_path);
        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => db.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "new 2" }));
        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(new[] { "7" }, InTheIndex(db, "Renamed", "PrimaryKey", 2));
    }

    // Verified, the row's key is seen to be 7, which this library makes as Access does: the entry for 2 is its
    // wrong one, the row with Id 2 is taken, and the row that is 7 is entered under 7.
    [Fact]
    public void with_indexes_verified_a_row_added_under_a_key_whose_entry_points_at_a_row_with_another_key_is_taken_and_that_row_is_entered_under_its_own()
    {
        Assert.Equal(new[] { "7" }, InTheIndex("Renamed", "PrimaryKey", 2));
        Assert.Empty(InTheIndex("Renamed", "PrimaryKey", 7));

        using (var db = Database.Open(_path))
        {
            db.VerifyIndexes = true;
            db.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "new 2" });
        }

        Assert.Equal(new[] { "2" }, InTheIndex("Renamed", "PrimaryKey", 2));
        Assert.Equal(new[] { "7" }, InTheIndex("Renamed", "PrimaryKey", 7));

        using var read = Database.Open(_path);
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Renamed", expectedRows: 3);
        Assert.Throws<ConstraintViolationException>(() => read.GetTable("Renamed").Insert(new Row { ["Id"] = 7, ["Name"] = "another 7" }));
        Assert.Throws<ConstraintViolationException>(() => read.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "another 2" }));
    }

    /// <summary>
    /// Verified, the row that is 7, found under 2, is given another key, 77: its entry under 2 is its wrong one, taken
    /// out by the update - which reads the row as it is being written, and enters it under 77 - and the update's own
    /// entry under 77 is the one already there. The row is in the index once. Entered by both, it was there twice, and
    /// DAO compacted the file without the key (ExportMdb.Interop's EarlierEngineFilesInAccessTests).
    /// </summary>
    [Fact]
    public void with_indexes_verified_a_row_found_under_a_key_it_does_not_have_and_given_another_is_entered_under_it_once()
    {
        using (var db = Database.Open(_path))
        {
            db.VerifyIndexes = true;
            var cursor = db.GetTable("Renamed").CreateCursor();
            Assert.NotNull(cursor.FindRow("Id", 7));
            cursor.UpdateCurrentRow(new Row { ["Id"] = 77 });
        }

        Assert.Empty(InTheIndex("Renamed", "PrimaryKey", 2));
        Assert.Empty(InTheIndex("Renamed", "PrimaryKey", 7));
        Assert.Equal(new[] { "77" }, InTheIndex("Renamed", "PrimaryKey", 77));
        using var read = Database.Open(_path);
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Renamed", expectedRows: 2);
    }

    /// <summary>
    /// Another row is 7 already, added while the key, believed, held nothing under 7 - as ACE adds it. Verified, the
    /// row that was found under 2 is entered under 7 beside it all the same: the two are duplicates whatever the
    /// key says, and a row is not left without an entry for having had a wrong one. The new row is the one found
    /// under 2.
    /// </summary>
    [Fact]
    public void a_row_found_under_a_key_it_does_not_have_is_entered_under_its_own_beside_a_row_that_has_it()
    {
        using (var db = Database.Open(_path))
            db.GetTable("Renamed").Insert(new Row { ["Id"] = 7, ["Name"] = "second 7" });   // added: the key has nothing under 7
        using (var db = Database.Open(_path))
        {
            db.VerifyIndexes = true;
            db.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "new 2" });
        }

        using var read = Database.Open(_path);
        Assert.Equal(new[] { "2" }, InTheIndex(read, "Renamed", "PrimaryKey", 2));
        Assert.Equal(new[] { "r1", "r2", "second 7", "new 2" }, read.GetTable("Renamed").Rows.Select(row => (string)row["Name"]!));
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Renamed", expectedRows: 4);
    }

    /// <summary>
    /// Entries in a chain: 1 points at the row that is 2, and 2 at the row that is 3, which has no entry under 3.
    /// Believed, the entry for 1 is a row's, and a row added as 1 is refused with nothing changed. Verified, a row
    /// added as 1 moves the first to 2, and what is under 2 is not a row that is 2 either: it moves to 3 in its
    /// turn. Taken for a row with the key because it points at a row, it left the row that is 2 with no entry.
    /// </summary>
    [Fact]
    public void entries_that_each_point_at_the_row_of_the_next_key_are_each_moved_to_their_row_s_own()
    {
        string path = Path.Combine(Path.GetTempPath(), $"entries_of_no_row_chain_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 } }, new[] { "Id" });
                foreach (int id in new[] { 2, 3, 4 })
                    table.Insert(new Row { ["Id"] = id, ["Name"] = "row " + id });

                var tree   = table.Indexes.Single();
                var target = IndexWriter.Target.For(table.Definition.TdefPageNumber, tree);
                byte[] Key(int id) => IndexKeys.Encode(tree.Columns, new object?[] { id });
                int two = PointerOf(db, "T", 2), three = PointerOf(db, "T", 3), four = PointerOf(db, "T", 4);
                var writer = WriterOf(db);
                writer.Remove(target, Key(2), two);
                writer.Remove(target, Key(3), three);
                writer.Remove(target, Key(4), four);
                writer.Insert(target, Key(1), two);
                writer.Insert(target, Key(2), three);
                writer.Insert(target, Key(3), four);
            }

            using (var db = Database.Open(path))
            {
                var before = IndexChecks.AllPages(db);
                Assert.Throws<ConstraintViolationException>(() => db.GetTable("T").Insert(new Row { ["Id"] = 1, ["Name"] = "row 1" }));
                IndexChecks.AssertUnchanged(before, db);
            }
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                db.GetTable("T").Insert(new Row { ["Id"] = 1, ["Name"] = "row 1" });
            }

            using var read = Database.Open(path);
            IndexChecks.AssertEveryIndexHoldsTheRows(read, "T", expectedRows: 4);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// An entry goes and its row's own comes, or neither: the file is made to fail at each page write of the
    /// insert in turn, indexes verified, and after every failure the row that is 7 has one entry in the key, under
    /// 2 as it was or under 7 - never none.
    /// </summary>
    [Fact]
    public void a_row_keeps_an_entry_whichever_page_write_fails_while_its_entry_is_moved()
    {
        byte[] original = File.ReadAllBytes(_path);
        for (int writes = 0; ; writes++)
        {
            File.WriteAllBytes(_path, original);
            bool done;
            using (var db = Database.Open(_path))
            {
                db.VerifyIndexes = true;
                db.File.WritesBeforeFailure = writes;
                try { db.GetTable("Renamed").Insert(new Row { ["Id"] = 2, ["Name"] = "new 2" }); done = true; }
                catch (IOException) { done = false; }
                db.File.WritesBeforeFailure = null;
            }

            using (var read = Database.Open(_path))
            {
                var underTwo   = InTheIndex(read, "Renamed", "PrimaryKey", 2);
                var underSeven = InTheIndex(read, "Renamed", "PrimaryKey", 7);
                Assert.True(underTwo.Count(id => id == "7") + underSeven.Count == 1,
                    $"write {writes + 1} failed: under 2 [{string.Join(", ", underTwo)}], under 7 [{string.Join(", ", underSeven)}]");
                if (done)
                {
                    Assert.Equal(new[] { "2" }, underTwo);
                    Assert.Equal(new[] { "7" }, underSeven);
                }
            }
            if (done)
            {
                Assert.True(writes >= 4, $"the insert took {writes} page writes");
                break;
            }
        }
    }

    // ── A key an earlier version wrote whole ─────────────────────────────────

    private static readonly string Long255 = new('é', 255);

    /// <summary>What the primary key of Coded holds for each of its rows, and what it holds that is no row's.</summary>
    private static (int Rows, int Entries, int OfNoRow) CodedKey(Database db)
    {
        var index   = db.GetTable("Coded").Indexes.Single();
        var entries = JetPages.LeafEntries(db.File, index.RootPageNumber);
        var rows    = JetPages.Rows(db.File, IndexChecks.TdefOf(db, "Coded"), forKeys: true).Select(r => (r.Page, r.Row)).ToHashSet();
        return (rows.Count, entries.Count, entries.Count(e => !rows.Contains((e.RowPage, e.Row))));
    }

    private string LongKeyFile(string asset)
    {
        string path = Path.Combine(Path.GetTempPath(), $"entries_of_no_row_long_{Guid.NewGuid():N}.mdb");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), path);
        return path;
    }

    /// <summary>
    /// Assets/engine-696ca52-long-keys-row-deleted.mdb is the file an earlier version wrote long keys whole in
    /// (<see cref="LegacyLongKeyTests"/>), with the row whose code is 255 of é taken out of its table and not out
    /// of the key: what a delete that looked for the cut key left. The entry is under the whole key, 513 bytes,
    /// which is not beside the cut key a new row with that code is entered under - other keys can lie between -
    /// so it is looked for in every form the key can have, and goes.
    /// </summary>
    [Fact]
    public void an_entry_under_a_whole_key_that_points_at_no_row_goes_when_a_row_is_added_under_the_cut_key()
    {
        string path = LongKeyFile("engine-696ca52-long-keys-row-deleted.mdb");
        try
        {
            using (var db = Database.Open(path))
            {
                Assert.Equal((24, 25, 1), CodedKey(db));
                db.GetTable("Coded").Insert(new Row { ["Code"] = Long255, ["N"] = 99 });
                Assert.Equal((25, 25, 0), CodedKey(db));
                Assert.Equal(99, db.GetTable("Coded").FindRowByPrimaryKey(Long255)?["N"]);
                Assert.Throws<ConstraintViolationException>(() => db.GetTable("Coded").Insert(new Row { ["Code"] = Long255, ["N"] = 100 }));
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The same for an entry under a whole key that points at a row with another code: believed, it is that row's,
    /// and a row with the long code is refused; verified, it goes, and that row keeps the entry it has.
    /// </summary>
    [Fact]
    public void an_entry_under_a_whole_key_that_points_at_a_row_with_another_key_goes_too_with_indexes_verified()
    {
        string path = LongKeyFile("engine-696ca52-long-keys-row-deleted.mdb");
        try
        {
            using (var db = Database.Open(path))
            {
                var coded  = db.GetTable("Coded");
                var index  = coded.Indexes.Single();
                var target = IndexWriter.Target.For(coded.Definition.TdefPageNumber, index);
                byte[] whole = IndexKeys.EncodeWhole(index.Columns, new object?[] { Long255 });
                Assert.True(whole.Length > IndexKeys.MaxKeyLength);
                int gone = WriterOf(db).EntriesWithPrefix(index.RootPageNumber, Array.Empty<byte>()).Single(e => e.Key.AsSpan().SequenceEqual(whole)).RowPointer;
                var cursor = coded.CreateCursor();
                int other = 0;
                while (cursor.MoveNext())
                    if ((string)cursor.Current!["Code"]! == "short 07") other = cursor.CurrentRowPointer;

                WriterOf(db).Remove(target, whole, gone);
                WriterOf(db).Insert(target, whole, other);
                Assert.Equal((24, 25, 0), CodedKey(db));   // every entry points at a row: one of them at the wrong one
            }
            using (var db = Database.Open(path))
            {
                var before = IndexChecks.AllPages(db);
                Assert.Throws<ConstraintViolationException>(() => db.GetTable("Coded").Insert(new Row { ["Code"] = Long255, ["N"] = 99 }));
                IndexChecks.AssertUnchanged(before, db);
            }
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                var coded = db.GetTable("Coded");
                coded.Insert(new Row { ["Code"] = Long255, ["N"] = 99 });
                Assert.Equal((25, 25, 0), CodedKey(db));
                Assert.Equal(99, coded.FindRowByPrimaryKey(Long255)?["N"]);
                Assert.Equal(7, coded.FindRowByPrimaryKey("short 07")?["N"]);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void a_check_that_writes_nothing_leaves_the_entry_where_it_is()
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            db.GetTable("Codes").Validate(Code(6, 200));
            Assert.Throws<ConstraintViolationException>(() => db.GetTable("Renamed").Validate(new Row { ["Id"] = 2, ["Name"] = "new 2" }));
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
