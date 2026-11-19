using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Keys of several columns longer than an entry holds (#56). Access cuts a key at 510 bytes and ends it in a
/// digest of every byte it cut off: their remainder by the CRC-16 polynomial 0x8005. This library, from when it
/// cut keys, left the last of those bytes out, as Jackcess does. That byte is 0 in a key that ends in text, so a
/// key of a text column, or of several that end in one, was Access's; a key that ends in a number after text that
/// filled the entry was not, and Access, which looks for the entry it would write itself, could neither update nor
/// delete the row ("The search key was not found in any record"), nor find it by Seek.
/// <para>
/// IndexKeys/cut-keys-general-legacy-ace16.tsv and cut-keys-general-ace16.tsv hold the entries ACE 16 wrote for 32
/// rows in seven indexes, in General - Legacy and in General (tools/ace-cut-keys.ps1): a text key cut in the first,
/// the middle and the last column, before a Long ascending and descending, after another text column, and in a
/// unique index, the cut falling in the text and in the Long after it.
/// Assets/engine-913240c-cut-keys.mdb is a file this library wrote before it cut such keys as Access does
/// (tools/earlier-cut-keys.ps1): its rows must be found, moved and removed, and seen by a unique index.
/// </para>
/// </summary>
public sealed class CutKeyTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    // ── Access's keys ───────────────────────────────────────────────────────

    private sealed record Golden(string Index, string Columns, int Row, string A, string B, int K, int J, string Key);

    public static TheoryData<string, string> GoldenFiles() => new()
    {
        { "cut-keys-general-legacy-ace16.tsv", "General - Legacy" },
        { "cut-keys-general-ace16.tsv", "General" },
    };

    // Every entry ACE wrote is the key this library makes of the row's values; of them, the entries whose digest
    // the earlier one is not.
    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Every_cut_key_is_the_one_ACE_wrote(string file, string order)
    {
        var codes = order == "General" ? TextIndexCodes.General : TextIndexCodes.GeneralLegacy;
        var failures = new List<string>();
        int cut = 0, earlierDiffers = 0;
        var golden = GoldenEntries(file);
        foreach (var entry in golden)
        {
            byte[] whole = WholeKey(codes, entry);
            string key = Convert.ToHexString(IndexKeys.Truncate(whole));
            if (key != entry.Key) failures.Add($"{entry.Index} row {entry.Row}: ACE ...{entry.Key.Substring(Math.Max(0, entry.Key.Length - 8))}, engine ...{key.Substring(Math.Max(0, key.Length - 8))}");
            if (whole.Length <= IndexKeys.MaxKeyLength) continue;
            cut++;
            if (Convert.ToHexString(IndexKeys.TruncateAsEarlierVersions(whole)) != entry.Key) earlierDiffers++;
        }

        Assert.Equal(224, golden.Count);
        Assert.True(failures.Count == 0, $"{failures.Count} keys differ from ACE's:\n" + string.Join("\n", failures.Take(25)));
        // The file is about cut keys, and all but ByKA's end in a number: a digest that left the last byte out
        // differs from ACE's for every one of those.
        Assert.Equal(order == "General" ? (211, 181) : (125, 109), (cut, earlierDiffers));
    }

    // The table ACE wrote the golden file from, made and filled by this library: every entry of every index is
    // ACE's, byte for byte. The unique index takes rows whose values differ only in the last byte of J, as ACE
    // took them: with the last byte left out of the digest, their cut keys were one key, and the second refused.
    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void The_entries_this_library_writes_are_the_entries_ACE_wrote(string file, string order)
    {
        var golden = GoldenEntries(file);
        using var db = NewDatabase(order == "General");
        var table = CreateCutKeys(db);
        foreach (var row in Rows(golden))
            table.Insert(row);

        AssertEntriesAreACE_s(db, golden, rows: null);
        Assert.Equal(8, IndexChecks.AssertEveryIndexHoldsTheRows(db, "CutKeys", expectedRows: 32));
    }

    // An entry Access wrote is never taken for one that is no row's: with VerifyIndexes, a row added, one changed
    // and one deleted leave every other row's entries as ACE wrote them, in the unique index too.
    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Rows_changed_with_VerifyIndexes_leave_the_entries_ACE_wrote(string file, string order)
    {
        var golden = GoldenEntries(file);
        string path;
        using (var db = NewDatabase(order == "General"))
        {
            path = db.Path;
            var table = CreateCutKeys(db);
            foreach (var row in Rows(golden))
                table.Insert(row);
            AssertEntriesAreACE_s(db, golden, rows: null);
        }

        using (var db = Database.Open(path))
        {
            db.VerifyIndexes = true;
            var table = db.GetTable("CutKeys");
            string omega = new('\u03A9', 255);
            table.Insert(new Row { ["CutKeyRow"] = 100, ["A"] = omega, ["B"] = "c", ["K"] = 2, ["J"] = 10 });   // beside rows 3 and 4
            table.UpdateByPrimaryKey(5, new Row { ["K"] = 3 });
            table.DeleteRow("CutKeyRow", 6);

            var untouched = Enumerable.Range(1, 32).Where(r => r is not (5 or 6)).ToHashSet();
            AssertEntriesAreACE_s(db, golden, untouched);
            Assert.Equal(8, IndexChecks.AssertEveryIndexHoldsTheRows(db, "CutKeys", expectedRows: 32));
        }
    }

    // The indexes added to a table that has its rows already (Table.AddIndex), a primary key among them: each entry
    // is the one ACE wrote for the row. The primary key on A and J holds the keys of the unique index on them.
    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Indexes_and_a_primary_key_added_over_the_rows_hold_the_entries_ACE_wrote(string file, string order)
    {
        var golden = GoldenEntries(file);
        using var db = NewDatabase(order == "General");
        var table = db.CreateTable("CutKeys", new[]
        {
            new ColumnDefinition("CutKeyRow", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
            new ColumnDefinition("B", DataType.Text) { MaxLength = 255 }, new ColumnDefinition("K", DataType.Int32),
            new ColumnDefinition("J", DataType.Int32),
        });
        foreach (var row in Rows(golden))
            table.Insert(row);

        table.AddIndex(new IndexDefinition("PrimaryKey", "A", "J") { IsPrimaryKey = true });
        foreach (var index in IndexesOf(golden).Where(ix => ix.Name != "UniqueAJ"))
            table.AddIndex(index);

        var held = EntriesByRow(db, "CutKeys", "CutKeyRow");
        var differ = golden.Where(g => !held[g.Index == "UniqueAJ" ? "PrimaryKey" : g.Index].TryGetValue(g.Row, out string? key) || key != g.Key)
                           .Select(g => $"{g.Index} row {g.Row}").ToList();
        Assert.True(differ.Count == 0, $"{differ.Count} entries are not ACE's: {string.Join(", ", differ.Take(20))}");
        Assert.Equal(7, IndexChecks.AssertEveryIndexHoldsTheRows(db, "CutKeys", expectedRows: 32));
    }

    /// <summary>The golden file's indexes, as definitions: each with its columns, and those that sort descending.</summary>
    private static IEnumerable<IndexDefinition> IndexesOf(List<Golden> golden)
        => golden.GroupBy(g => g.Index).Select(g =>
        {
            var columns = g.First().Columns.Split(',');
            var index = new IndexDefinition(g.Key, columns.Select(c => c.Split(' ')[0]).ToArray()) { IsUnique = g.Key.StartsWith("Unique", StringComparison.Ordinal) };
            foreach (string column in columns.Where(c => c.EndsWith(" desc", StringComparison.Ordinal)))
                index.DescendingColumns.Add(column.Split(' ')[0]);
            return index;
        });

    private Database NewDatabase(bool general)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cutkeys_{Guid.NewGuid():N}{(general ? ".accdb" : ".mdb")}");
        _paths.Add(path);
        if (general)
        {
            File.Copy(TestCorpus.File("V2010", "common1V2010.accdb") ?? throw new FileNotFoundException("common1V2010.accdb"), path);
            File.SetAttributes(path, FileAttributes.Normal);
        }
        else Database.Create(path, FileFormat.Access2000).Dispose();
        return Database.Open(path);
    }

    /// <summary>The table tools/ace-cut-keys.ps1 has ACE make, with its seven indexes.</summary>
    private static Table CreateCutKeys(Database db)
    {
        ColumnDefinition Text(string name) => new(name, DataType.Text) { MaxLength = 255 };
        IndexDefinition Index(string name, string[] columns, params string[] descending)
        {
            var index = new IndexDefinition(name, columns);
            foreach (string column in descending) index.DescendingColumns.Add(column);
            return index;
        }
        return db.CreateTable("CutKeys", new[]
        {
            new ColumnDefinition("CutKeyRow", DataType.Int32), Text("A"), Text("B"),
            new ColumnDefinition("K", DataType.Int32), new ColumnDefinition("J", DataType.Int32),
        }, new[] { "CutKeyRow" }, new[]
        {
            Index("ByAK", new[] { "A", "K" }), Index("ByADescK", new[] { "A", "K" }, "A"), Index("ByAKDesc", new[] { "A", "K" }, "K"),
            Index("ByKA", new[] { "K", "A" }), Index("ByKAJ", new[] { "K", "A", "J" }), Index("ByABK", new[] { "A", "B", "K" }),
            new IndexDefinition("UniqueAJ", "A", "J") { IsUnique = true },
        });
    }

    private static IEnumerable<Row> Rows(List<Golden> golden)
        => golden.GroupBy(g => g.Row).OrderBy(g => g.Key).Select(g => g.First())
                 .Select(g => new Row { ["CutKeyRow"] = g.Row, ["A"] = g.A, ["B"] = g.B, ["K"] = g.K, ["J"] = g.J });

    /// <summary>
    /// Asserts that each golden entry of a row in <paramref name="rows"/> (every row, when null) is the entry the
    /// table's index of its name holds for that row, byte for byte.
    /// </summary>
    private static void AssertEntriesAreACE_s(Database db, List<Golden> golden, ISet<int>? rows)
    {
        var held = EntriesByRow(db, "CutKeys", "CutKeyRow");
        var differ = golden.Where(g => rows is null || rows.Contains(g.Row))
                           .Where(g => !held[g.Index].TryGetValue(g.Row, out string? key) || key != g.Key)
                           .Select(g => $"{g.Index} row {g.Row}").ToList();
        Assert.True(differ.Count == 0, $"{differ.Count} entries are not ACE's: {string.Join(", ", differ.Take(20))}");
    }

    /// <summary>Each index's entries by the number of the row they point at, as hex.</summary>
    private static Dictionary<string, Dictionary<int, string>> EntriesByRow(Database db, string tableName, string idColumn)
    {
        var table = db.GetTable(tableName);
        int tdef = IndexChecks.TdefOf(db, tableName);
        var ids = JetPages.Rows(db.File, tdef).ToDictionary(r => (r.Page, r.Row), r => (int)r.Values[idColumn]!);
        return table.Indexes.ToDictionary(index => index.Name,
            index => JetPages.LeafEntries(db.File, index.RootPageNumber).ToDictionary(e => ids[(e.RowPage, e.Row)], e => e.Hex));
    }

    /// <summary>A golden entry's key whole, from its columns and values, each column's part made by this library.</summary>
    private static byte[] WholeKey(TextIndexCodes codes, Golden entry)
    {
        var key = new List<byte>();
        foreach (string column in entry.Columns.Split(','))
        {
            bool ascending = !column.EndsWith(" desc", StringComparison.Ordinal);
            key.AddRange(column.Split(' ')[0] switch
            {
                "A" => IndexKeys.EncodeText(codes, ascending, entry.A),
                "B" => IndexKeys.EncodeText(codes, ascending, entry.B),
                "K" => IndexKeys.EncodeColumn(DataType.Int32, ascending, entry.K),
                "J" => IndexKeys.EncodeColumn(DataType.Int32, ascending, entry.J),
                _ => throw new InvalidDataException($"No column {column} in {entry.Index}."),
            });
        }
        return key.ToArray();
    }

    private static List<Golden> GoldenEntries(string file)
    {
        using var stream = typeof(CutKeyTests).Assembly.GetManifestResourceStream("JackcessDotNet.Tests." + file)
            ?? throw new InvalidOperationException($"The golden keys resource {file} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var entries = new List<Golden>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('\t');
            entries.Add(new Golden(f[0], f[1], int.Parse(f[2], CultureInfo.InvariantCulture), Runs(f[3]), Runs(f[4]),
                int.Parse(f[5], CultureInfo.InvariantCulture), int.Parse(f[6], CultureInfo.InvariantCulture), f[7]));
        }
        return entries;
    }

    // "00E9*248 0061*7": 248 of U+00E9, then 7 of 'a'.
    private static string Runs(string runs)
    {
        var text = new StringBuilder();
        foreach (string run in runs.Split(' '))
        {
            string[] parts = run.Split('*');
            text.Append((char)int.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1);
        }
        return text.ToString();
    }

    // ── A file this library wrote before ────────────────────────────────────

    private static readonly string Omega255 = new('\u03A9', 255);

    /// <summary>A copy of the database this library wrote at 913240c.</summary>
    private Database OpenEarlierFixture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"earliercut_{Guid.NewGuid():N}.mdb");
        _paths.Add(path);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "engine-913240c-cut-keys.mdb"), path);
        return Database.Open(path);
    }

    /// <summary>
    /// How each row's entry in <paramref name="indexName"/> is cut: "access" (Access's digest), "earlier" (the
    /// earlier digest, where it is not Access's) or "short" (not cut). Asserts that every row has exactly one
    /// entry, in one of those forms, and that the tree holds no other.
    /// </summary>
    private static List<string> CutAs(Database db, string indexName)
    {
        var index = db.GetTable("Pairs").Indexes.Single(ix => ix.Name == indexName);
        var entries = JetPages.LeafEntries(db.File, index.RootPageNumber);
        var rows = JetPages.Rows(db.File, IndexChecks.TdefOf(db, "Pairs"), forKeys: true);
        var kinds = new List<string>();
        foreach (var (page, row, values) in rows)
        {
            var key = index.Columns.Select(c => values.TryGetValue(c.Column.Name, out var v) ? v : null).ToArray();
            byte[] whole = IndexKeys.EncodeWhole(index.Columns, key);
            string access = Convert.ToHexString(IndexKeys.Truncate(whole));
            string earlier = Convert.ToHexString(IndexKeys.TruncateAsEarlierVersions(whole));
            var own = entries.Where(e => e.RowPage == page && e.Row == row).Select(e => e.Hex).ToList();
            Assert.True(own.Count == 1, $"Pairs.{indexName}: row {page}/{row} has {own.Count} entries");
            Assert.True(own[0] == access || own[0] == earlier, $"Pairs.{indexName}: row {page}/{row} is under neither of its keys");
            kinds.Add(whole.Length <= IndexKeys.MaxKeyLength ? "short" : own[0] == access ? "access" : "earlier");
        }
        Assert.Equal(kinds.Count, entries.Count);
        Assert.Empty(JetPages.RuleBreaks(db.File, index.RootPageNumber, IndexChecks.TdefOf(db, "Pairs")));
        return kinds;
    }

    private static (int Short, int Access, int Earlier) Count(List<string> kinds)
        => (kinds.Count(k => k == "short"), kinds.Count(k => k == "access"), kinds.Count(k => k == "earlier"));

    // What the fixture is: a cut key that ends in a number is there with the earlier digest - all but the row whose
    // K is 256, whose last byte is 0 - and one that ends in text with Access's, which is the same.
    [Fact]
    public void The_file_written_at_913240c_holds_keys_cut_with_the_earlier_digest()
    {
        using var db = OpenEarlierFixture();
        Assert.Equal((2, 1, 6), Count(CutAs(db, "ByAK")));
        Assert.Equal((2, 0, 7), Count(CutAs(db, "UniqueAN")));
        Assert.Equal((2, 7, 0), Count(CutAs(db, "ByKA")));
    }

    // A deleted row's entry goes with it, cut either way: the indexes hold the rows left and nothing else. Looked
    // for under Access's key alone, the entry was left behind.
    [Fact]
    public void A_deleted_row_takes_its_entry_cut_the_earlier_way_with_it()
    {
        using var db = OpenEarlierFixture();
        var pairs = db.GetTable("Pairs");
        for (int id = 2; id <= 8; id++)
            pairs.DeleteRow("Id", id);
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Pairs", expectedRows: 2));
    }

    // An updated row loses its entry cut the earlier way and is entered under Access's key, whichever column changed.
    [Fact]
    public void An_updated_row_is_entered_under_the_key_Access_cuts()
    {
        using var db = OpenEarlierFixture();
        var pairs = db.GetTable("Pairs");
        pairs.UpdateByPrimaryKey(2, new Row { ["N"] = 2001 });   // one index's key
        pairs.UpdateByPrimaryKey(3, new Row { ["K"] = 70001 });  // another's
        Assert.Equal((2, 3, 4), Count(CutAs(db, "ByAK")));        // a row rewritten gains every entry anew
        Assert.Equal((2, 2, 5), Count(CutAs(db, "UniqueAN")));

        for (int id = 1; id <= 9; id++)
            pairs.UpdateByPrimaryKey(id, new Row { ["Id"] = 100 + id });
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Pairs", expectedRows: 9));   // every cut key Access's
    }

    // A unique index sees an entry cut the earlier way: a second row with its values is refused before anything
    // changes, and a value no row has is taken. (N 2256 differs from row 2's 2000 in a byte before the last, so
    // the earlier digest, which left the last byte out, gives the two different keys too.)
    [Fact]
    public void A_duplicate_of_a_value_whose_entry_is_cut_the_earlier_way_is_refused()
    {
        using var db = OpenEarlierFixture();
        var pairs = db.GetTable("Pairs");
        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => pairs.Insert(new Row { ["Id"] = 20, ["A"] = Omega255, ["K"] = 5, ["N"] = 2000 }));
        Assert.Throws<ConstraintViolationException>(() => pairs.UpdateByPrimaryKey(1, new Row { ["A"] = Omega255, ["N"] = 3000 }));
        IndexChecks.AssertUnchanged(before, db);

        pairs.Insert(new Row { ["Id"] = 20, ["A"] = Omega255, ["K"] = 5, ["N"] = 2256 });
        Assert.Equal((2, 1, 7), Count(CutAs(db, "UniqueAN")));
    }

    // With VerifyIndexes, a unique index is looked at before a row is compared with it, and an entry cut the
    // earlier way is not its row's: it is taken out, and the row entered under Access's key, when the table is
    // next changed. An index that is not unique is not looked at: its entries are moved as their rows are written.
    [Fact]
    public void VerifyIndexes_replaces_the_entries_a_unique_index_holds_cut_the_earlier_way()
    {
        using var db = OpenEarlierFixture();
        db.VerifyIndexes = true;
        db.GetTable("Pairs").Insert(new Row { ["Id"] = 10, ["A"] = "another", ["K"] = 1, ["N"] = 10000 });

        Assert.Equal((3, 7, 0), Count(CutAs(db, "UniqueAN")));
        Assert.Equal((3, 1, 6), Count(CutAs(db, "ByAK")));
        Assert.Equal((3, 7, 0), Count(CutAs(db, "ByKA")));
    }
}
