using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What an Access 97 (Jet3) file holds that the nine such files of the corpus do not, read as the engine that
/// wrote it reads it.
/// <para>
/// Assets/access97-jet40.mdb was written by Jet 4.0 (32-bit DAO 3.6), Microsoft's engine of Access 2000 to 2003,
/// which creates and keeps a Jet3 database; tools/access97-fixture.ps1 made it, and is the record of what is in
/// it. It is not a file Access 97 itself wrote. Assets/access97-jet40-long-rows.tsv is its LongRows table as that
/// engine read it back, and the same engine found every row of its Keys table again through both indexes before
/// the file was kept.
/// </para>
/// <para>
/// LongRows has rows longer than 256 bytes. A Jet3 row gives a variable-length value's offset in one byte, and a
/// longer row has a jump table saying which values are past each 256 bytes; read without it, a value past the
/// first 256 bytes was taken from the wrong place or for a null. Keys has index pages above the leaves with many
/// entries and a shared prefix, and pages above those. A Jet3 index page has the length of that prefix in one
/// byte and its level in the next; read as two bytes, the prefix of a page above the leaves was 256 longer for
/// every level, no entry after the page's first had its prefix put back, and a lookup took the wrong page.
/// </para>
/// </summary>
public sealed class Access97ReadTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static Database OpenFixture() => Database.Open(Asset("access97-jet40.mdb"));

    private const int KeysRows = 1500;

    /// <summary>
    /// An Access 97 Text column is as long as its bytes in the database's code page, a byte a character: LongRows'
    /// four Text columns are 255 bytes each, and 32-bit DAO 3.6 reports a Size of 255 for each, as it does the bytes
    /// of all 175 Text columns of the corpus's Access 97 files and this one. The engine said 127, two bytes a
    /// character, and an export to a DataTable refused the values of 255 characters the column holds (#85).
    /// </summary>
    [Fact]
    public void A_Text_column_is_as_long_as_DAO_says()
    {
        using var db = OpenFixture();
        var table = db.GetTable("LongRows");
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            var column = table.Columns.Single(c => c.Name == name);
            Assert.Equal(255, column.MaxLength);
            Assert.Equal(255, column.LengthInBytes);
        }

        var exported = table.ToDataTable();
        Assert.Equal(255, exported.Columns["A"]!.MaxLength);
        Assert.Contains(exported.Rows.Cast<System.Data.DataRow>(), row => row["A"] is string { Length: 255 });
    }

    /// <summary>K1..K10 of row <paramref name="i"/> of Keys, as tools/access97-fixture.ps1 wrote them.</summary>
    private static object[] KeysOf(int i)
    {
        var keys = new object[10];
        keys[0] = i * 7919 % 1500;
        for (int j = 2; j <= 10; j++)
            keys[j - 1] = (int)(((long)i * (j + 1) * 7919 + j * 104729) % 2000003) - 1000001;
        return keys;
    }

    /// <summary>The rows of LongRows as the engine that wrote them read them: Id, then A, B, C, D (null for a null), then N.</summary>
    private static List<(int Id, string?[] Text, int N)> RowsAsJetReadThem()
        => File.ReadAllLines(Asset("access97-jet40-long-rows.tsv"))
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => line.Split('\t'))
            .Select(cells => (int.Parse(cells[0]),
                              cells.Skip(1).Take(4).Select(cell => cell == @"\N" ? null : cell).ToArray(),
                              int.Parse(cells[5])))
            .ToList();

    private static void AssertRowIs((int Id, string?[] Text, int N) expected, Row row)
    {
        Assert.Equal(expected.N, row["N"]);
        string[] names = { "A", "B", "C", "D" };
        for (int c = 0; c < names.Length; c++)
        {
            row.TryGetValue(names[c], out var read);
            Assert.True(Equals(expected.Text[c], read),
                $"Row {expected.Id}, column {names[c]}: {Describe(expected.Text[c])} written, {Describe(read as string)} read.");
        }
    }

    private static string Describe(string? text)
        => text is null ? "null" : $"{text.Length} characters beginning '{text.Substring(0, Math.Min(12, text.Length))}'";

    // ── Rows longer than 256 bytes ───────────────────────────────────────────

    [Fact]
    public void The_fixture_has_rows_of_every_length_about_each_256_bytes()
    {
        using var db = OpenFixture();
        Assert.Equal(FileFormat.Access97, db.File.Format.Version);

        var lengths = new List<int>();
        var table = db.GetTable("LongRows");
        foreach (int page in OwnedPages(db, table))
        {
            byte[] data = db.File.ReadPage(page);
            for (int r = 0; r < ByteUtil.GetShort(data, db.File.Format.OffsetDataNumRows); r++)
                if (DataRows.Read(db.File, data, r) is { } row) lengths.Add(row.Length);
        }

        Assert.Equal(RowsAsJetReadThem().Count, lengths.Count);
        // A row of 257 bytes does not exist: the jump its length calls for makes it 258.
        foreach (int boundary in new[] { 256, 512, 768 })
            for (int length = boundary - 6; length <= boundary + 6; length++)
                if (length != boundary + 1)
                    Assert.Contains(length, lengths);
        Assert.Contains(lengths, length => length > 1024);
    }

    [Fact]
    public void Rows_longer_than_256_bytes_read_as_the_engine_that_wrote_them_reads_them()
    {
        var expected = RowsAsJetReadThem();
        Assert.Equal(260, expected.Count);

        using var db = OpenFixture();
        var table = db.GetTable("LongRows");
        var read = table.Rows.ToDictionary(row => (int)row["Id"]!);

        Assert.Equal(expected.Count, read.Count);
        Assert.Equal(expected.Count, table.RowCount);
        foreach (var row in expected)
            AssertRowIs(row, read[row.Id]);
    }

    [Fact]
    public void A_cursor_reads_rows_longer_than_256_bytes_too()
    {
        var expected = RowsAsJetReadThem().ToDictionary(row => row.Id);

        using var db = OpenFixture();
        int count = 0;
        foreach (var row in db.GetTable("LongRows").CreateCursor())
        {
            AssertRowIs(expected[(int)row["Id"]!], row);
            count++;
        }
        Assert.Equal(expected.Count, count);
    }

    [Fact]
    public void A_row_longer_than_256_bytes_is_found_by_its_primary_key_with_its_values()
    {
        using var db = OpenFixture();
        var cursor = db.GetTable("LongRows").CreateIndexCursor();
        foreach (var expected in RowsAsJetReadThem())
        {
            var found = cursor.Table.FindRowByPrimaryKey(expected.Id);
            Assert.NotNull(found);
            AssertRowIs(expected, found!);
        }
    }

    // ── Index pages above the leaves ─────────────────────────────────────────

    private static List<int> OwnedPages(Database db, Table table)
        => UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow,
                                  db.File.Format, db.File).ToList();

    /// <summary>The index pages of <paramref name="tableName"/>: the pages of the file that name its definition.</summary>
    private static List<byte[]> IndexPagesOf(Database db, string tableName)
    {
        int tdef = db.GetTable(tableName).Definition.TdefPageNumber;
        return Enumerable.Range(0, db.File.PageCount)
            .Select(db.File.ReadPage)
            .Where(page => (page[0] == JetFormat.PageTypeIndexNode || page[0] == JetFormat.PageTypeIndexLeaf)
                           && ByteUtil.GetInt(page, JetFormat.OffsetDataTdefPage) == tdef)
            .ToList();
    }

    [Fact]
    public void A_Jet3_index_page_has_its_prefix_length_in_one_byte_and_its_level_in_the_next()
    {
        var format = JetFormat.Jet3;
        Assert.Equal(20, format.OffsetIndexCompressedByteCount);
        Assert.Equal(1, format.SizeIndexCompressedByteCount);
        Assert.Equal(21, format.OffsetIndexLevel);
        Assert.Equal(2, JetFormat.Jet4.SizeIndexCompressedByteCount);
        Assert.Equal(26, JetFormat.Jet4.OffsetIndexLevel);

        // The pages Jet 4.0 wrote: every leaf at level 0, with the pages above them at 1 and one above those at 2,
        // and pages above the leaves whose entries share a prefix. No page's entries share 256 bytes: an entry
        // here is some fifty.
        using var db = OpenFixture();
        var pages = IndexPagesOf(db, "Keys");
        var leaves = pages.Where(page => page[0] == JetFormat.PageTypeIndexLeaf).ToList();
        var nodes  = pages.Where(page => page[0] == JetFormat.PageTypeIndexNode).ToList();

        Assert.True(leaves.Count > 50, $"{leaves.Count} leaf pages");
        Assert.All(leaves, page => Assert.Equal(0, page[format.OffsetIndexLevel]));
        Assert.Equal(new[] { 1, 1, 1, 1, 2 }, nodes.Select(page => (int)page[format.OffsetIndexLevel]).OrderBy(level => level));
        Assert.Contains(nodes, page => page[format.OffsetIndexCompressedByteCount] > 0);
        Assert.All(pages, page => Assert.Equal(page[format.OffsetIndexCompressedByteCount], IndexReader.ReadPrefixLength(page, format)));
    }

    [Fact]
    public void Every_row_is_found_through_a_primary_key_with_pages_above_its_leaves()
    {
        using var db = OpenFixture();
        var table  = db.GetTable("Keys");
        var index  = table.Indexes.Single(ix => ix.IsPrimaryKey);
        var byScan = table.Rows.ToDictionary(row => (int)row["Id"]!);
        Assert.Equal(KeysRows, byScan.Count);

        var cursor = table.CreateIndexCursor();
        var reader = new IndexReader(db.File, index);
        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        for (int id = 1; id <= KeysRows; id++)
        {
            // Each of the two walks of an index's pages finds the row, and so does the cursor that uses them.
            Assert.Equal(id, RowAt(table, Assert.Single(reader.FindRowPointers(id)))["Id"]);
            Assert.Equal(id, RowAt(table, Assert.Single(writer.EnumerateRowPointersForKey(table.Definition, id)))["Id"]);

            var found = cursor.Table.FindRowByPrimaryKey(id);
            Assert.NotNull(found);
            Assert.Equal(KeysOf(id), Enumerable.Range(1, 10).Select(j => found!["K" + j]));
            Assert.Equal(byScan[id].OrderBy(v => v.Key), found!.OrderBy(v => v.Key));
        }
        Assert.Null(cursor.Table.FindRowByPrimaryKey(KeysRows + 1));
    }

    [Fact]
    public void Every_row_is_found_through_an_index_three_levels_deep()
    {
        using var db = OpenFixture();
        var table = db.GetTable("Keys");
        var index = table.Indexes.Single(ix => ix.Name == "ByAll");
        Assert.Equal(2, db.File.ReadPage(index.RootPageNumber)[JetFormat.Jet3.OffsetIndexLevel]);

        var cursor = table.CreateIndexCursor("ByAll");
        var reader = new IndexReader(db.File, index);
        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        Assert.True(reader.CanResolveKey);
        for (int id = 1; id <= KeysRows; id++)
        {
            object[] keys = KeysOf(id);
            Assert.Equal(id, RowAt(table, Assert.Single(reader.FindRowPointersForEntry(keys)))["Id"]);
            Assert.Equal(id, RowAt(table, Assert.Single(writer.RowPointersForKey(index.RootPageNumber, IndexKeys.Encode(index.Columns, keys))))["Id"]);

            var found = cursor.FindRowByKey(keys);
            Assert.NotNull(found);
            Assert.Equal(id, found!["Id"]);
        }

        // Keys no row has: one before every row's, one after, and one between two rows'.
        object[] none = KeysOf(1);
        none[9] = (int)none[9] + 1;
        Assert.Empty(reader.FindRowPointersForEntry(none));
        Assert.Null(cursor.FindRowByKey(none));
        Assert.Null(cursor.FindRowByKey(Enumerable.Repeat((object)int.MinValue, 10).ToArray()));
        Assert.Null(cursor.FindRowByKey(Enumerable.Repeat((object)int.MaxValue, 10).ToArray()));
    }

    // ── Every index of every Access 97 file ──────────────────────────────────

    public static IEnumerable<object[]> Access97Files()
        => TestCorpus.Files("*.mdb", "V1997").Select(path => new object[] { Path.GetFileName(path) })
            .Append(new object[] { FixtureName });

    private const string FixtureName = "access97-jet40.mdb";

    private static string PathOf(string file) => file == FixtureName ? Asset(file) : TestCorpus.File("V1997", file)!;

    /// <summary>An entry of a Jet3 index page: its key, the row it is for and, above the leaves, the page below it.</summary>
    private sealed record Jet3Entry(byte[] Key, int RowPointer, int SubPage);

    /// <summary>
    /// A Jet3 index page read here, apart from the library's two readers of one: the prefix length in the byte
    /// at 20, the entry mask in the 226 bytes from 22, the entries after it.
    /// </summary>
    private static (bool IsLeaf, int Next, int Level, List<Jet3Entry> Entries) ReadJet3IndexPage(PageFile file, int number)
    {
        byte[] page = file.ReadPage(number);
        Assert.True(page[0] is JetFormat.PageTypeIndexNode or JetFormat.PageTypeIndexLeaf, $"Page {number} is not an index page.");
        bool leaf = page[0] == JetFormat.PageTypeIndexLeaf;
        const int mask = 22, maskSize = 226, entriesStart = mask + maskSize;
        int prefixLength = page[20], trailer = leaf ? 4 : 8;

        var entries = new List<Jet3Entry>();
        byte[] prefix = Array.Empty<byte>();
        int start = 0;
        for (int bit = 0; bit < maskSize * 8; bit++)
        {
            if ((page[mask + bit / 8] & (1 << (bit % 8))) == 0) continue;
            byte[] stored = page.AsSpan(entriesStart + start, bit - start).ToArray();
            byte[] full = entries.Count == 0 ? stored : prefix.Concat(stored).ToArray();
            if (entries.Count == 0) prefix = stored.Take(prefixLength).ToArray();
            int k = full.Length - trailer;
            int row = RowPointer.Pack((full[k] << 16) | (full[k + 1] << 8) | full[k + 2], full[k + 3]);
            int sub = leaf ? 0 : (full[k + 4] << 24) | (full[k + 5] << 16) | (full[k + 6] << 8) | full[k + 7];
            entries.Add(new Jet3Entry(full.Take(k).ToArray(), row, sub));
            start = bit;
        }
        return (leaf, ByteUtil.GetInt(page, 12), page[21], entries);
    }

    /// <summary>Every leaf entry of the index rooted at <paramref name="root"/>, in order, and how many levels it has.</summary>
    private static (List<Jet3Entry> Entries, int Levels) LeafEntries(PageFile file, int root)
    {
        var page = ReadJet3IndexPage(file, root);
        int levels = 1;
        Assert.Equal(page.IsLeaf ? 0 : page.Level, page.Level);
        while (!page.IsLeaf)
        {
            Assert.True(page.Level > 0, "A page above the leaves has a level.");
            page = ReadJet3IndexPage(file, page.Entries[0].SubPage);
            levels++;
        }

        var entries = new List<Jet3Entry>();
        while (true)
        {
            Assert.Equal(0, page.Level);
            entries.AddRange(page.Entries);
            if (page.Next <= 0) return (entries, levels);
            page = ReadJet3IndexPage(file, page.Next);
        }
    }

    /// <summary>Where each row of <paramref name="table"/> is, as a reading of all its pages finds them.</summary>
    private static HashSet<int> RowPointersOf(Database db, Table table)
    {
        var pointers = new HashSet<int>();
        foreach (int page in OwnedPages(db, table))
        {
            byte[] data = db.File.ReadPage(page);
            for (int r = 0; r < ByteUtil.GetShort(data, db.File.Format.OffsetDataNumRows); r++)
                if (DataRows.Read(db.File, data, r) is not null) pointers.Add(RowPointer.Pack(page, r));
        }
        return pointers;
    }

    /// <summary>
    /// Every index of every table - system tables too - of the corpus's nine Access 97 files, and of the one Jet
    /// 4.0 wrote, leads to every row a reading of the whole table finds: its leaves hold an entry for each row
    /// (fewer only where the index passes over nulls), and looking up each entry's key from the root - by each of
    /// the library's two walks of an index - comes to that entry's row. The keys are the index's own, read from
    /// its leaves, so this holds for the text indexes too, whose keys, in Access 97's sort order, this library
    /// does not make.
    /// <para>
    /// indexCodesV1997 and compIndexV1997 have pages above their
    /// leaves, none with a shared prefix. Read with the level for the high byte of the prefix's length, such a
    /// page had a prefix of 256 bytes: IndexReader, finding the page's first entry shorter than that, applied
    /// none, and read these pages rightly all the same; IndexWriter, which a primary key is looked up through,
    /// put the whole first entry before each of the others, and lost the second entry of the one page of
    /// compIndexV1997 that has two. indexCodesV1997 has a key whose rows run from one leaf into the next, where
    /// IndexReader did not follow them. A page with a prefix, and a third level, are in access97-jet40.mdb alone.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Access97Files))]
    public void Every_index_of_an_Access_97_file_leads_to_every_row_of_its_table(string file)
    {
        using var db = Database.Open(PathOf(file));
        Assert.Equal(FileFormat.Access97, db.File.Format.Version);

        int indexes = 0, deep = 0, lookups = 0;
        foreach (string name in db.GetTableNames().Concat(db.GetSystemTableNames()))
        {
            var table = db.GetTable(name);
            if (table.Definition.HasUnreadIndexes) continue;
            var rows = RowPointersOf(db, table);
            foreach (var index in table.Indexes.Where(ix => ix.RootPageNumber > 0).GroupBy(ix => ix.RootPageNumber).Select(g => g.First()))
            {
                string where = $"{file}/{name}/{index.Name}";
                var (entries, levels) = LeafEntries(db.File, index.RootPageNumber);
                indexes++;
                if (levels > 1) deep++;

                Assert.True(entries.Select(e => e.RowPointer).Distinct().Count() == entries.Count, $"{where}: a row has two entries.");
                Assert.True(entries.All(e => rows.Contains(e.RowPointer)), $"{where}: an entry is for a row the table does not have.");
                if (!index.IgnoreNulls)
                    Assert.True(entries.Count == rows.Count, $"{where}: {entries.Count} entries for {rows.Count} rows.");

                var reader = new IndexReader(db.File, index);
                var writer = new IndexWriter(db.File, new PageAllocator(db.File));
                foreach (var entry in entries)
                {
                    Assert.True(writer.RowPointersForKey(index.RootPageNumber, entry.Key).Contains(entry.RowPointer),
                        $"{where}: the row at {entry.RowPointer:X8} is not found by its key {Convert.ToHexString(entry.Key)} (IndexWriter).");
                    Assert.True(reader.FindRowPointersForKey(entry.Key).Contains(entry.RowPointer),
                        $"{where}: the row at {entry.RowPointer:X8} is not found by its key {Convert.ToHexString(entry.Key)} (IndexReader).");
                    lookups++;
                }
            }
        }

        Assert.True(indexes > 0 && lookups > 0, $"{file}: {indexes} indexes, {lookups} lookups.");
        if (file is "indexCodesV1997.mdb" or "compIndexV1997.mdb" or FixtureName)
            Assert.True(deep > 0, $"{file}: no index has a page above its leaves.");
    }

    /// <summary>
    /// The same by value, where this library makes the keys: every index on Byte, Integer and Long columns, one
    /// or several, finds for each row's values the rows that hold them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Access97Files))]
    public void Every_row_of_an_Access_97_file_is_found_by_its_values_through_its_numeric_indexes(string file)
    {
        using var db = Database.Open(PathOf(file));

        int lookups = 0;
        foreach (string name in db.GetTableNames())
        {
            var table = db.GetTable(name);
            var rows  = table.Rows.ToList();
            foreach (var index in table.Indexes)
            {
                if (index.RootPageNumber <= 0 || !index.Columns.All(c => c.Column.DataType is DataType.Byte or DataType.Int16 or DataType.Int32)) continue;
                var reader = new IndexReader(db.File, index);
                Assert.True(reader.CanResolveKey);

                foreach (var row in rows)
                {
                    object?[] keys = index.Columns.Select(c => Value(row, c)).ToArray();
                    // A null has an entry too, but is not a key a caller asks for: IndexKeys.CanSearch says which are.
                    if (keys.Any(key => key is null) || !IndexKeys.CanSearch(index.Columns, keys)) continue;

                    int same = rows.Count(other => index.Columns.All(c => Equals(Value(other, c), Value(row, c))));
                    var found = reader.FindRowPointersForEntry(keys).ToList();
                    Assert.True(same == found.Count,
                        $"{file}/{name}/{index.Name}: {same} rows hold ({string.Join(", ", keys)}); {found.Count} found.");
                    foreach (int pointer in found)
                        Assert.All(index.Columns, c => Assert.Equal(Value(row, c), Value(RowAt(table, pointer), c)));
                    lookups++;
                }
            }
        }

        if (file is "indexCodesV1997.mdb" or "compIndexV1997.mdb" or FixtureName)
            Assert.True(lookups > 0, $"{file}: no row was looked up through an index.");
    }

    // A row holds nothing for a column that is null in it.
    private static object? Value(Row row, IndexColumn column) => row.TryGetValue(column.Column.Name, out var value) ? value : null;

    private static Row RowAt(Table table, int rowPointer)
        => table.ReadRowAt(rowPointer, table.Columns) ?? throw new InvalidDataException($"No row at {rowPointer:X8}.");

    // ── Text in another code page (#154) ─────────────────────────────────────

    // Assets/access97-cp1251.mdb, access97-cp932.mdb and access97-cp1251-password.mdb were made by Jet 4.0 (32-bit DAO
    // 3.6) in a process whose code page was 1251 or 932, and each .tsv beside them is what that DAO read of the file
    // with that code page and with 1252, every line led by the code page (tools/access97-code-page-fixture.ps1). Jet
    // keeps and reads the text in the process's code page, whatever the file's header says: the first two say General
    // and code page 1252.

    /// <summary>The lines DAO read of the fixture with <paramref name="codePage"/>, each without the code page that leads it.</summary>
    private static List<string[]> AsDaoRead(string fixture, int codePage)
        => File.ReadAllLines(Asset($"access97-{fixture}.tsv"), System.Text.Encoding.UTF8)
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => line.Split('\t'))
            .Where(cells => cells[0] == codePage.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Select(cells => cells.Skip(1).ToArray())
            .ToList();

    /// <summary>The fixture, opened with its text in <paramref name="codePage"/>: for 1252, with nothing set.</summary>
    private static Database OpenInCodePage(string fixture, int codePage, string? password = null)
        => Database.Open(Asset($"access97-{fixture}.mdb"),
                         new DatabaseOpenOptions { Access97CodePage = codePage == 1252 ? null : codePage, Password = password });

    public static TheoryData<string, int> CodePageReadings() => new()
    {
        { "cp1251", 1251 }, { "cp1251", 1252 }, { "cp932", 932 }, { "cp932", 1252 }, { "cp1251-password", 1251 }, { "cp1251-password", 1252 },
    };

    /// <summary>
    /// The names of the table, its columns and its indexes, and every row's text, are what DAO read with the code page
    /// the file is opened in; and the table is found by the names DAO found it by. Opened with nothing set, a file
    /// written in another code page reads as Access reads it on a machine of code page 1252.
    /// </summary>
    [Theory]
    [MemberData(nameof(CodePageReadings))]
    public void Text_of_an_Access_97_file_reads_in_the_code_page_it_is_opened_in_as_DAO_reads_it_with_that_code_page(string fixture, int codePage)
    {
        var dao = AsDaoRead(fixture, codePage);
        string? password = dao.FirstOrDefault(line => line[0] == "password" && line[2] == "opens")?[1];
        using var db = OpenInCodePage(fixture, codePage, password);

        string name = Assert.Single(dao, line => line[0] == "table")[1];
        Assert.Equal(new[] { name }, db.GetTableNames());
        var table = db.GetTable(name);
        Assert.Equal(dao.Where(line => line[0] == "column").Select(line => line[2]).OrderBy(n => n, StringComparer.Ordinal),
                     table.Columns.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(dao.Where(line => line[0] == "index").Select(line => line[2] + ":" + line[3]).OrderBy(n => n, StringComparer.Ordinal),
                     table.Indexes.Select(ix => ix.Name + ":" + string.Join(",", ix.Columns.Select(c => c.Column.Name))).OrderBy(n => n, StringComparer.Ordinal));

        var text = table.Columns.Single(c => c.DataType == DataType.Text);
        Assert.Equal(codePage, text.SortOrder.CodePage);
        Assert.Equal(dao.Where(line => line[0] == "row").Select(line => line[1] + "=" + line[2]),
                     table.Rows.Select(row => Convert.ToInt32(row["Id"]) + "=" + ((string?)row[text.Name] ?? "\\N")));

        foreach (var line in dao.Where(line => line[0] == "tabledef"))
        {
            bool found = db.TryGetTable(line[1], out var byName);
            Assert.True(found == (line[2] != "-"), $"'{line[1]}': DAO found {line[2]}, this library {(found ? byName!.Name : "nothing")}.");
            if (found) Assert.Equal(line[2], byName!.Name);
        }
    }

    /// <summary>
    /// Jet keys an Access 97 file's text in General by its bytes, whatever its code page: every entry of the text
    /// index is the key this library makes of its row's value in the code page the file is opened in, and a value is
    /// found through the index as DAO's Seek found it with that code page - Cyrillic in either case with code page
    /// 1251, whose two cases have the bytes of the two cases of Latin-1's letters, and only as it is with 932, whose
    /// two bytes a letter do not. A value with a character the code page has not is not looked for, where DAO looked
    /// for what Windows' best fit made of it.
    /// </summary>
    [Theory]
    [InlineData("cp1251", 1251, 16)]
    [InlineData("cp1251", 1252, 6)]
    [InlineData("cp932", 932, 11)]
    [InlineData("cp932", 1252, 4)]
    public void Text_of_an_Access_97_file_is_found_through_its_index_as_DAO_found_it_with_the_code_page_it_is_opened_in(string fixture, int codePage, int looked)
    {
        var dao = AsDaoRead(fixture, codePage);
        using var db = OpenInCodePage(fixture, codePage);
        var table = db.GetTable(db.GetTableNames().Single());
        var index = table.Indexes.Single(ix => ix.Columns.Count == 1 && ix.Columns[0].Column.DataType == DataType.Text);
        var codes = TextIndexCodes.For(index.Columns[0].Column.SortOrder);
        Assert.NotNull(codes);
        Assert.Equal(codePage, codes!.CodePage);

        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        int entries = 0;
        foreach (var (key, pointer) in writer.EntriesWithPrefix(index.RootPageNumber, Array.Empty<byte>()))
        {
            object?[] values = { Value(RowAt(table, pointer), index.Columns[0]) };
            Assert.Equal(Convert.ToHexString(key), Convert.ToHexString(IndexKeys.Encode(index.Columns, values)));
            entries++;
        }
        Assert.Equal(9, entries);

        var reader = new IndexReader(db.File, index);
        int lookups = 0;
        foreach (var line in dao.Where(line => line[0] == "seek"))
        {
            object?[] asked = { line[1] };
            if (!IndexKeys.CanSearch(index.Columns, asked))
            {
                Assert.False(codes.CanEncode(line[1]), $"'{line[1]}' is not looked for, and its code page has every character of it.");
                continue;
            }
            var ids = reader.FindRowPointersForEntry(asked).Select(p => Convert.ToInt32(RowAt(table, p)["Id"])).ToList();
            if (line[2] == "-") Assert.True(ids.Count == 0, $"'{line[1]}': DAO found no row, this library {string.Join(", ", ids)}.");
            else Assert.True(ids.Contains(int.Parse(line[2], System.Globalization.CultureInfo.InvariantCulture)),
                             $"'{line[1]}': DAO found row {line[2]}, this library {string.Join(", ", ids)}.");
            lookups++;
        }
        Assert.Equal(looked, lookups);
    }

    /// <summary>
    /// A database password is converted to the code page the file is opened in before it is compared, as Jet converts
    /// it to the machine's: the password set as "Пароль1" with code page 1251 opens the file with that code page, and
    /// with 1252 the password "Ïàðîëü1", which has its bytes there, does.
    /// </summary>
    [Theory]
    [InlineData(1251)]
    [InlineData(1252)]
    public void A_database_password_of_an_Access_97_file_opens_it_as_DAO_opened_it_with_the_code_page_it_is_opened_in(int codePage)
    {
        var tried = AsDaoRead("cp1251-password", codePage).Where(line => line[0] == "password").ToList();
        Assert.Equal(4, tried.Count);
        foreach (var line in tried)
        {
            if (line[2] == "opens")
                OpenInCodePage("cp1251-password", codePage, line[1]).Dispose();
            else
                Assert.Throws<System.Security.Authentication.InvalidCredentialException>(() => OpenInCodePage("cp1251-password", codePage, line[1]));
        }
    }

    [Fact]
    public void The_code_page_1252_named_reads_as_nothing_set_does()
    {
        using var named = Database.Open(Asset("access97-cp1251.mdb"), new DatabaseOpenOptions { Access97CodePage = 1252 });
        using var unset = Database.Open(Asset("access97-cp1251.mdb"));
        Assert.Same(JetFormat.Jet3, named.File.Format);
        Assert.Equal(unset.GetTableNames(), named.GetTableNames());
        string name = unset.GetTableNames().Single();
        Assert.Equal(unset.GetTable(name).Rows.Select(Cells), named.GetTable(name).Rows.Select(Cells));

        static string Cells(Row row) => string.Join("|", row.Select(cell => cell.Key + "=" + cell.Value));
    }

    /// <summary>
    /// A code page Access keeps no Access 97 text in is refused before the file is opened, whatever its format: one of
    /// Unicode's encodings (with UTF-8 for its code page DAO 3.6 makes no Access 97 file), or a number that is no code
    /// page. A file of a later format, whose text is Unicode, is read as it is whatever code page is named.
    /// </summary>
    [Theory]
    [InlineData(65001)]
    [InlineData(65000)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99999)]
    public void A_code_page_no_Access_97_text_is_kept_in_is_refused_before_the_file_is_opened(int codePage)
    {
        foreach (string path in new[] { Asset("access97-cp1251.mdb"), TestCorpus.File("V2003", "queryV2003.mdb")! })
        {
            var refused = Assert.Throws<ArgumentException>(() => Database.Open(path, new DatabaseOpenOptions { Access97CodePage = codePage, ReadOnly = true }));
            Assert.Contains(nameof(DatabaseOpenOptions.Access97CodePage), refused.Message);
        }
    }

    [Fact]
    public void A_file_of_a_later_format_reads_the_same_whatever_code_page_is_named()
    {
        string path = TestCorpus.File("V2003", "queryV2003.mdb")!;
        using var unset = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true });
        using var named = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true, Access97CodePage = 1251 });
        Assert.Same(unset.File.Format, named.File.Format);
        Assert.Equal(unset.GetTableNames(), named.GetTableNames());
    }
}
