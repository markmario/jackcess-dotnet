using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Rows loaded into a table with no rows have its indexes built once they are in (#82): by
/// <see cref="Table.BulkInsert"/>, which goes through the rows once and holds none of them, and by
/// <see cref="Table.InsertRange"/>. Each tree holds the entries an insert of each row would have given it, in a tree
/// that keeps Access's rules, read back from the file's pages (<see cref="IndexChecks"/>); a duplicate is refused with
/// the later row named and the file byte for byte as it was; and a table that has rows is filled row by row as
/// before. What Access makes of a tree built so is in ExportMdb.Interop's IndexedExportInAccessTests.
/// </summary>
public sealed class BulkInsertTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bulk_insert_{Guid.NewGuid():N}.mdb");
    private readonly string _twin = Path.Combine(Path.GetTempPath(), $"bulk_insert_twin_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        foreach (var path in new[] { _path, _twin })
            if (File.Exists(path)) File.Delete(path);
        EntrySorter.Budget = 64L * 1024 * 1024;
    }

    private static ColumnDefinition[] Columns() => new[]
    {
        new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true },
        new ColumnDefinition("Code", DataType.Text) { MaxLength = 40 },
        new ColumnDefinition("Label", DataType.Text) { MaxLength = 255 },
        new ColumnDefinition("Grp", DataType.Int32),
        new ColumnDefinition("Amount", DataType.Double),
        new ColumnDefinition("Note", DataType.Memo),
    };

    private static IndexDefinition[] Indexes() => new[]
    {
        new IndexDefinition("ByCode", "Code") { IsUnique = true },
        new IndexDefinition("ByLabel", "Label"),
        new IndexDefinition("ByGroupAndAmount", "Grp", "Amount") { DescendingColumns = { "Amount" } },
        new IndexDefinition("ByGroupIgnoringNulls", "Grp") { IgnoreNulls = true },
    };

    // Labels repeat, some are long CJK text whose keys are cut at the entry's limit, and every fifth row has no group.
    private static Row RowOf(int i) => new()
    {
        ["Code"] = $"code {(i * 7919L) % 100_003:D6}",
        ["Label"] = i % 11 == 0 ? new string((char)(0x4E00 + i % 50), 250) + i : $"label {i % 97}",
        ["Grp"] = i % 5 == 0 ? null : i % 13,
        ["Amount"] = (i % 9 - 4) * 0.25,
        ["Note"] = i % 4 == 0 ? new string('n', 300 + i % 700) : null,
    };

    // A database with the table Rows and no row in it.
    private static Database Create(string path)
    {
        var db = Database.Create(path, FileFormat.Access2000);
        db.CreateTable("Rows", Columns(), new[] { "Id" }, Indexes());
        return db;
    }

    [Fact]
    public void Rows_loaded_into_a_table_with_none_are_in_every_index_its_trees_built_once()
    {
        const int count = 12_000;
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Rows", Columns(), new[] { "Id" }, Indexes());
            long readsBefore = db.File.ReadCount, writesBefore = db.File.WriteCount;
            Assert.Equal(count, table.BulkInsert(Enumerable.Range(1, count).Select(RowOf)));

            Assert.Equal(count, table.RowCount);
            Assert.Equal(count, table.LastAutoNumber);
            Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: count));
            Assert.All(table.Indexes, ix => Assert.True(IndexChecks.Height(db, ix.RootPageNumber) >= 1, $"{ix.Name} is one page"));

            // A row's cost is its own pages': the trees cost a few writes for each hundred rows, not several for each row.
            double reads = (double)(db.File.ReadCount - readsBefore) / count, writes = (double)(db.File.WriteCount - writesBefore) / count;
            Assert.True(writes < 3.5 && reads < 7, $"{reads:F2} pages read and {writes:F2} written a row");

            // The engine goes on: a row inserted after is entered in every tree, and a duplicate of a loaded one refused.
            table.Insert(RowOf(count + 1));
            Assert.Throws<ConstraintViolationException>(() => table.Insert(RowOf(1)));
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Rows", expectedRows: count + 1));
        Assert.Equal(RowOf(77)["Code"], reopened.GetTable("Rows").CreateIndexCursor("ByCode").FindRow("Code", RowOf(77)["Code"])!["Code"]);
    }

    [Fact]
    public void A_tree_built_whole_holds_the_entries_row_by_row_inserts_give_it_in_the_same_order()
    {
        const int count = 4_000;
        using (Create(_path)) { }
        using (var db = Database.Open(_path)) db.GetTable("Rows").BulkInsert(Enumerable.Range(1, count).Select(RowOf));
        using (var db = Database.Create(_twin, FileFormat.Access2000))
        {
            var table = db.CreateTable("Rows", Columns(), new[] { "Id" }, Indexes());
            foreach (int i in Enumerable.Range(1, count)) table.Insert(RowOf(i));
        }

        using var built = Database.Open(_path);
        using var kept = Database.Open(_twin);
        foreach (var index in built.GetTable("Rows").Indexes)
            Assert.Equal(EntriesById(kept, index.Name), EntriesById(built, index.Name));
    }

    // The index's leaf entries in order: each key, and the id of the row it names.
    private static List<string> EntriesById(Database db, string index)
    {
        var ids = JetPages.Rows(db.File, IndexChecks.TdefOf(db, "Rows")).ToDictionary(r => (r.Page, r.Row), r => (int)r.Values["Id"]!);
        var root = db.GetTable("Rows").Indexes.Single(ix => ix.Name == index).RootPageNumber;
        return JetPages.LeafEntries(db.File, root).Select(e => $"{e.Hex} -> {ids[(e.RowPage, e.Row)]}").ToList();
    }

    [Fact]
    public void A_duplicate_in_a_unique_index_is_refused_with_the_later_row_and_nothing_written()
    {
        using (Create(_path)) { }
        byte[] before = File.ReadAllBytes(_path);
        using var db = Database.Open(_path);
        var table = db.GetTable("Rows");

        // Row 2,501 has the code of row 40, differing only in case: the index's key does not tell them apart.
        var rows = Enumerable.Range(1, 3_000).Select(i =>
        {
            var row = RowOf(i);
            if (i == 2_501) row["Code"] = ((string)RowOf(40)["Code"]!).ToUpperInvariant();
            return row;
        });
        var refused = Assert.Throws<BatchInsertException>(() => table.BulkInsert(rows));
        Assert.Equal(2_500, refused.RowIndex);
        var duplicate = Assert.IsType<ConstraintViolationException>(refused.InnerException);
        Assert.Contains("ByCode", duplicate.Message);
        Assert.Contains(((string)RowOf(40)["Code"]!).ToUpperInvariant(), duplicate.Message);

        Assert.Equal(0, table.RowCount);
        db.Dispose();
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "the refused rows changed the file");
    }

    [Theory]
    // Measured with ACE on ExportMdb's template: its unique index on Document_ID refused the second of each pair
    // marked true, and took both of each pair marked false.
    [InlineData("ABC.0001", "abc.0001", true)]
    [InlineData("ABC.0001", "ABC.0001 ", true)]
    [InlineData("ABC.0001", "ABC.0001  ", true)]
    [InlineData("été.0001", "ÉTÉ.0001", true)]
    [InlineData("ＡＢＣ.0001", "ABC.0001", true)]
    [InlineData("STRASSE.1", "STRAßE.1", true)]
    [InlineData("æ.1", "AE.1", true)]
    [InlineData("ABC.0001", "ABC.000１", true)]
    [InlineData("ABC.0001", " ABC.0001", false)]
    [InlineData("ABC.0001", "ABC0001", false)]
    [InlineData("AB-C.0001", "ABC.0001", false)]
    [InlineData("AB'C.0001", "ABC.0001", false)]
    [InlineData("ABC_0001", "ABC.0001", false)]
    [InlineData("ÉTÉ.0001", "ETE.0001", false)]
    public void Duplicates_are_found_by_the_index_s_key_as_Access_compares_them(string first, string second, bool same)
    {
        using var db = Create(_path);
        var table = db.GetTable("Rows");
        var rows = new[] { "other 1", first, "other 2", second }.Select(code => new Row { ["Code"] = code }).ToList();

        var found = table.FindDuplicates(rows);

        if (!same)
        {
            Assert.Empty(found);
            return;
        }
        var duplicate = Assert.Single(found);
        Assert.Equal("ByCode", duplicate.IndexName);
        Assert.Equal(new[] { 1, 3 }, duplicate.RowIndexes);
        Assert.False(duplicate.InTable);
        // What an insert refuses.
        table.Insert(rows[1]);
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Code"] = second }));
    }

    [Fact]
    public void Duplicates_of_rows_in_the_table_are_found_and_nulls_are_no_duplicates()
    {
        using var db = Create(_path);
        var table = db.GetTable("Rows");
        table.BulkInsert(Enumerable.Range(1, 1_000).Select(RowOf));
        var rows = new List<Row>
        {
            new() { ["Code"] = null },
            new() { ["Code"] = ((string)RowOf(500)["Code"]!).ToUpperInvariant() },
            new() { ["Code"] = null },
            new() { ["Code"] = "new 1" },
            new() { ["Code"] = "NEW 1" },
            new() { ["Code"] = "new 1 " },
            new() { ["Id"] = 7, ["Code"] = "new 2" },
        };

        var found = table.FindDuplicates(rows);

        Assert.Equal(new[] { "ByCode: rows 1 and a row of the table", "ByCode: rows 3, 4, 5", "PrimaryKey: rows 6 and a row of the table" },
                     found.Select(d => d.ToString()));
        Assert.Equal(1_000, table.RowCount);
    }

    [Fact]
    public void Insert_range_into_a_table_with_none_builds_the_trees_and_gives_the_rows_back_when_refused()
    {
        using (Create(_path)) { }
        using var db = Database.Open(_path);
        var table = db.GetTable("Rows");
        var rows = Enumerable.Range(1, 2_000).Select(RowOf).ToList();
        rows[1_999]["Code"] = rows[3]["Code"];

        var refused = Assert.Throws<BatchInsertException>(() => table.InsertRange(rows));
        Assert.Equal(1_999, refused.RowIndex);
        Assert.All(rows, row => Assert.False(row.ContainsKey("Id"), "a row was not given back as it came"));
        Assert.Equal(0, table.RowCount);

        rows[1_999]["Code"] = "a code of its own";
        table.InsertRange(rows);
        Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 2_000));
    }

    [Fact]
    public void Rows_loaded_into_a_table_that_has_rows_are_entered_row_by_row()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Rows", Columns(), new[] { "Id" }, Indexes());
            foreach (int i in Enumerable.Range(1, 500)) table.Insert(RowOf(i));
            table.BulkInsert(Enumerable.Range(501, 2_500).Select(RowOf));
            Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 3_000));

            var refused = Assert.Throws<BatchInsertException>(() => table.BulkInsert(new[] { RowOf(3_001), RowOf(10) }));
            Assert.Equal(1, refused.RowIndex);
            Assert.Equal(3_000, table.RowCount);
        }
    }

    [Fact]
    public void Keys_past_the_memory_budget_are_sorted_in_runs_on_disk_and_build_the_same_trees()
    {
        const int count = 6_000;
        using (Create(_twin)) { }
        using (var db = Database.Open(_twin)) db.GetTable("Rows").BulkInsert(Enumerable.Range(1, count).Select(RowOf));

        EntrySorter.Budget = 64 * 1024;   // a few hundred long keys: every tree goes to disk in runs
        using (Create(_path)) { }
        using (var db = Database.Open(_path)) db.GetTable("Rows").BulkInsert(Enumerable.Range(1, count).Select(RowOf));

        using var inMemory = Database.Open(_twin);
        using var reopened = Database.Open(_path);
        foreach (var index in reopened.GetTable("Rows").Indexes)
            Assert.Equal(EntriesById(inMemory, index.Name), EntriesById(reopened, index.Name));
        Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Rows", expectedRows: count));
    }

    [Fact]
    public void The_sorter_merges_its_runs_in_key_order_and_then_row_order()
    {
        EntrySorter.Budget = 2_000;
        using var sorter = new EntrySorter();
        var random = new Random(5);
        var expected = new List<(string, int)>();
        for (int i = 0; i < 3_000; i++)
        {
            var key = new byte[1 + random.Next(30)];
            random.NextBytes(key);
            key[0] = (byte)(key[0] % 4);   // many keys alike in their first byte, some alike whole
            if (i % 7 == 0) key = new byte[] { 1, 2, 3 };
            int pointer = random.Next();
            sorter.Add(key, pointer, hasNull: false);
            expected.Add((Convert.ToHexString(key), pointer));
        }
        Assert.True(sorter.RunCount > 10, $"{sorter.RunCount} runs");
        var read = sorter.Sorted().Select(e => (Convert.ToHexString(e.Key), e.RowPointer)).ToList();
        var ordered = expected.OrderBy(e => e.Item1.Length == 0 ? "" : e.Item1, StringComparer.Ordinal).ToList();
        Assert.Equal(3_000, read.Count);
        for (int i = 1; i < read.Count; i++)
        {
            var a = Convert.FromHexString(read[i - 1].Item1);
            var b = Convert.FromHexString(read[i].Item1);
            int byKey = a.AsSpan().SequenceCompareTo(b);
            Assert.True(byKey < 0 || byKey == 0 && (uint)read[i - 1].RowPointer <= (uint)read[i].RowPointer, $"entry {i} is out of order");
        }
        Assert.Equal(expected.OrderBy(e => e).ToList(), read.OrderBy(e => e).ToList());
    }

    [Fact]
    public void A_load_whose_writing_fails_at_a_page_leaves_the_file_as_it_was()
    {
        using (Create(_path)) { }
        byte[] before = File.ReadAllBytes(_path);
        int writesNeeded;
        string copy = CopyOf(_path);
        using (var probe = Database.Open(copy))
        {
            long start = probe.File.WriteCount;
            probe.GetTable("Rows").BulkInsert(Enumerable.Range(1, 3_000).Select(RowOf));
            writesNeeded = (int)(probe.File.WriteCount - start);
        }
        File.Delete(copy);

        // Failing at writes spread over the whole load, the trees' among them, which come last.
        foreach (int writes in new[] { 0, 1, 7, writesNeeded / 3, writesNeeded / 2, writesNeeded - 40, writesNeeded - 10, writesNeeded - 2, writesNeeded - 1 })
        {
            using (var db = Database.Open(_path))
            {
                db.File.WritesBeforeFailure = writes;
                var refused = Assert.Throws<BatchInsertException>(() => db.GetTable("Rows").BulkInsert(Enumerable.Range(1, 3_000).Select(RowOf)));
                Assert.IsType<IOException>(refused.InnerException);
                db.File.WritesBeforeFailure = null;
            }
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), $"the file changed when write {writes + 1} of {writesNeeded} failed");
        }
    }

    private static string CopyOf(string path)
    {
        string copy = Path.Combine(Path.GetTempPath(), $"bulk_insert_copy_{Guid.NewGuid():N}.mdb");
        File.Copy(path, copy);
        return copy;
    }
}
