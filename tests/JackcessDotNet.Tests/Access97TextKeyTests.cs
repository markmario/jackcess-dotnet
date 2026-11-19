using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Text keys in Access 97's General order (#23): an Access 97 file is only read, so what its keys are for is
/// finding a row through a text index instead of reading the table for it. A key that is not the one in the file
/// finds nothing, so each must be the bytes Microsoft's engine wrote.
/// <para>
/// The referees are the 32-bit ACE 12.0 of Access 2007 and Jet 4.0, which make and keep a Jet3 file (ACE 16 opens
/// none), and Access 97 itself, which wrote the corpus's V1997 files:
/// </para>
/// <para>
/// IndexKeys/access97-ace12.tsv - the values of the General - Legacy golden file, as ACE 12 keyed them in a Jet3
/// file. Access 97 keeps text in code page 1252, so a value with a character the code page has not is not in the
/// file as it was given: those lines are passed by, and the library makes no key for such a value.
/// </para>
/// <para>
/// IndexKeys/access97-more-ace12.tsv - every character of code page 1252 alone, every pair of those that are
/// letters or have an accent, and 5,000 values of 3 to 12 characters drawn from the whole code page.
/// ExportMdb.Interop's Access97InAccessTests has ACE key every pair of the code page's characters, in a 32-bit
/// process, and compares them all.
/// </para>
/// </summary>
public sealed class Access97TextKeyTests
{
    private static TextIndexCodes Codes => TextIndexCodes.Access97;

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static IEnumerable<(string Value, string CodePoints, string Ascending, string Descending)> GoldenKeys(string file)
    {
        using var stream = typeof(Access97TextKeyTests).Assembly.GetManifestResourceStream("JackcessDotNet.Tests." + file)
            ?? throw new InvalidOperationException($"The golden keys resource {file} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t');
            var value = new StringBuilder();
            foreach (string cp in fields[0].Split(' '))
            {
                int codePoint = int.Parse(cp, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                value.Append(codePoint is >= 0xD800 and <= 0xDFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint));
            }
            yield return (value.ToString(), fields[0], fields[1], fields[2]);
        }
    }

    [Theory]
    [InlineData("access97-ace12.tsv", 2018, 901, 1)]
    [InlineData("access97-more-ace12.tsv", 11491, 0, 0)]
    public void Every_value_code_page_1252_holds_encodes_as_ACE_12_wrote_it_in_an_Access_97_file(string file, int values, int notInTheCodePage, int tooLong)
    {
        var failures = new List<string>();
        int checkedValues = 0, passedBy = 0, cut = 0;
        foreach (var (value, codePoints, ascending, descending) in GoldenKeys(file))
        {
            checkedValues++;
            if (!Codes.CanEncode(value))
            {
                passedBy++;
                Assert.Throws<NotSupportedException>(() => Codes.Encode(value, ascending: true));
                continue;
            }
            byte[] key = IndexKeys.EncodeText(Codes, ascending: true, value);
            if (key.Length > IndexKeys.Access97MaxKeyLength)
            {
                // ACE cut the key to what an entry holds, and its last two bytes are not the key's own: such a
                // value is not looked for through the index.
                cut++;
                Assert.Equal(IndexKeys.Access97MaxKeyLength * 2, ascending.Length);
                Assert.StartsWith(ascending.Substring(0, (IndexKeys.Access97MaxKeyLength - 2) * 2), Hex(key));
                continue;
            }
            string asc  = Hex(key);
            string desc = Hex(IndexKeys.EncodeText(Codes, ascending: false, value));
            if (asc != ascending)   failures.Add($"{codePoints} ascending: ACE {ascending}, engine {asc}");
            if (desc != descending) failures.Add($"{codePoints} descending: ACE {descending}, engine {desc}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} keys differ from ACE's:\n" + string.Join("\n", failures.Take(25)));
        Assert.Equal(values, checkedValues);
        Assert.Equal(notInTheCodePage, passedBy);
        Assert.Equal(tooLong, cut);
    }

    [Fact]
    public void A_key_longer_than_an_Access_97_entry_holds_is_not_looked_for_through_the_index()
    {
        using var db = Database.Open(TestCorpus.File("V1997", "indexCodesV1997.mdb")!);
        var column = db.GetTableNames().Select(db.GetTable).SelectMany(t => t.Columns).First(c => c.DataType == DataType.Text);
        Assert.Equal(SortOrder.Access97General, column.SortOrder);
        var columns = new[] { new IndexColumn(column, 0x01) };

        Assert.True(IndexKeys.CanSearch(columns, new object?[] { new string('x', 253) }));    // 255 bytes with its flag and end
        Assert.False(IndexKeys.CanSearch(columns, new object?[] { new string('x', 254) }));
        Assert.False(IndexKeys.CanSearch(columns, new object?[] { "Ж" }));
    }

    [Fact]
    public void A_character_beyond_U_00FF_has_the_codes_of_its_byte_in_code_page_1252_and_any_other_has_no_key()
    {
        // Š is 0x8A in the code page.
        Assert.Equal("760A00", Hex(Codes.Encode("Š", ascending: true)));
        Assert.True(Codes.CanEncode("€ ™ Œ"));

        // The five bytes the code page has no character for are read as the control characters of their number.
        Assert.True(Codes.CanEncode("\u0081\u008D\u008F\u0090\u009D"));

        // The other control characters of that range are not in the code page: 0x80 is €, not U+0080.
        foreach (string value in new[] { "Ж", "日本", "a\u0301", "\u0100", "\uFFFF", "a\U0001F600", "\u0080", "a\u008A" })
        {
            Assert.False(Codes.CanEncode(value));
            var refused = Assert.Throws<NotSupportedException>(() => Codes.Encode(value, ascending: true));
            Assert.Contains("code page 1252", refused.Message);
        }
    }

    [Fact]
    public void The_order_of_an_Access_97_column_is_the_one_whose_keys_are_made()
    {
        Assert.Same(Codes, TextIndexCodes.For(SortOrder.Access97General));
        Assert.Null(TextIndexCodes.For(new SortOrder(1049, 0xFF)));   // another of Access 97's orders

        using var db = Database.Open(TestCorpus.File("V1997", "indexCodesV1997.mdb")!);
        var text = db.GetTableNames().Select(db.GetTable).SelectMany(t => t.Columns).First(c => c.DataType == DataType.Text);
        Assert.Equal(SortOrder.Access97General, text.SortOrder);
        Assert.True(IndexKeys.CanEncode(text));
        Assert.True(IndexReader.CanResolve(text));
    }

    // ── The files Access 97 wrote ────────────────────────────────────────────

    public static IEnumerable<object[]> Access97Files() => Access97ReadTests.Access97Files();

    private static string PathOf(string file)
        => TestCorpus.File("V1997", file) ?? Path.Combine(AppContext.BaseDirectory, "Assets", file);

    private static bool IsKeyed(Index index)
        => index.RootPageNumber > 0
        && index.Columns.Any(c => c.Column.DataType == DataType.Text)
        && index.Columns.All(c => c.Column.DataType is DataType.Text or DataType.Byte or DataType.Int16 or DataType.Int32);

    private static object? Value(Row row, IndexColumn column) => row.TryGetValue(column.Column.Name, out var value) ? value : null;

    /// <summary>
    /// The keys in the file are the reference: for every index with a Text column, of every table and system
    /// table, each entry's key is the key this library makes of the values of the row it points at.
    /// </summary>
    [Theory]
    [MemberData(nameof(Access97Files))]
    public void Every_text_key_of_an_Access_97_file_is_the_key_made_of_its_rows_values(string file)
    {
        using var db = Database.Open(PathOf(file));
        Assert.Equal(FileFormat.Access97, db.File.Format.Version);

        int compared = 0, indexes = 0;
        var failures = new List<string>();
        foreach (string name in db.GetTableNames().Concat(db.GetSystemTableNames()))
        {
            var table = db.GetTable(name);
            if (table.Definition.HasUnreadIndexes) continue;
            foreach (var index in table.Indexes.Where(IsKeyed).GroupBy(ix => ix.RootPageNumber).Select(g => g.First()))
            {
                indexes++;
                var writer = new IndexWriter(db.File, new PageAllocator(db.File));
                foreach (var (key, pointer) in writer.EntriesWithPrefix(index.RootPageNumber, Array.Empty<byte>()))
                {
                    var row = table.ReadRowAt(pointer, table.Columns);
                    Assert.True(row is not null, $"{file}/{name}/{index.Name}: no row at {pointer:X8}.");
                    object?[] values = index.Columns.Select(c => Value(row!, c)).ToArray();
                    Assert.True(index.Columns.Select((c, i) => IndexKeys.CanEncode(c.Column, values[i])).All(can => can),
                        $"{file}/{name}/{index.Name}: no key is made for ({string.Join(", ", values)}).");

                    string made = Hex(IndexKeys.Encode(index.Columns, values));
                    if (made != Hex(key) && failures.Count < 25)
                        failures.Add($"{file}/{name}/{index.Name} ({string.Join(", ", values)}): Access {Hex(key)}, engine {made}");
                    compared++;
                }
            }
        }

        Assert.True(failures.Count == 0, "Keys differ from the file's:\n" + string.Join("\n", failures));
        Assert.True(indexes > 0 && compared > 0, $"{file}: {indexes} indexes with text, {compared} keys compared.");
    }

    /// <summary>
    /// And by value, as a caller looks: every row of every table is found by its values through each index with a
    /// Text column, and only the rows that have that key are.
    /// </summary>
    [Theory]
    [MemberData(nameof(Access97Files))]
    public void Every_row_of_an_Access_97_file_is_found_by_its_values_through_its_text_indexes(string file)
    {
        using var db = Database.Open(PathOf(file));

        int lookups = 0;
        foreach (string name in db.GetTableNames())
        {
            var table = db.GetTable(name);
            if (table.Definition.HasUnreadIndexes) continue;
            var rows = new List<(int Pointer, Row Row)>();
            var cursor = table.CreateCursor();
            while (cursor.MoveNext()) rows.Add((cursor.CurrentRowPointer, cursor.Current!));

            foreach (var index in table.Indexes.Where(IsKeyed))
            {
                var reader = new IndexReader(db.File, index);
                Assert.True(reader.CanResolveKey, $"{file}/{name}/{index.Name} is not searched.");

                var byKey = rows
                    .Select(r => (r.Pointer, Values: index.Columns.Select(c => Value(r.Row, c)).ToArray()))
                    .Where(r => r.Values.All(v => v is not null) && IndexKeys.CanSearch(index.Columns, r.Values))
                    .GroupBy(r => Hex(IndexKeys.Encode(index.Columns, r.Values)));
                foreach (var sameKey in byKey)
                {
                    var found = reader.FindRowPointersForEntry(sameKey.First().Values).OrderBy(p => p).ToList();
                    Assert.True(sameKey.Select(r => r.Pointer).OrderBy(p => p).SequenceEqual(found),
                        $"{file}/{name}/{index.Name}: {sameKey.Count()} rows have the key of ({string.Join(", ", sameKey.First().Values)}); {found.Count} found.");
                    lookups++;
                }
            }
        }

        if (file is "indexCodesV1997.mdb")
            Assert.True(lookups > 100, $"{file}: {lookups} keys looked up through a text index.");
    }

    /// <summary>The same through a cursor: a row is found by a text value, and the index is what finds it.</summary>
    [Fact]
    public void A_cursor_finds_a_row_of_an_Access_97_file_by_a_text_value_through_its_index()
    {
        using var db = Database.Open(TestCorpus.File("V1997", "indexCodesV1997.mdb")!);
        int found = 0;
        foreach (string name in db.GetTableNames())
        {
            var table = db.GetTable(name);
            var index = table.Indexes.FirstOrDefault(ix => ix.Columns.Count == 1 && IsKeyed(ix));
            if (index is null) continue;
            string column = index.Columns[0].Column.Name;
            var rows = table.Rows.Where(row => row[column] is string text && IndexKeys.CanSearch(index.Columns, new object?[] { text })).ToList();
            if (rows.Count < 200) continue;

            int pages = UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow, db.File.Format, db.File).Count;
            foreach (var row in rows.Take(50))
            {
                long before = db.File.ReadCount;
                var hit = table.CreateIndexCursor(index.Name).FindRow(column, row[column]);
                Assert.NotNull(hit);
                Assert.Equal(row[column], hit![column]);
                if (pages > 12)
                    Assert.True(db.File.ReadCount - before < pages, $"{name}: {db.File.ReadCount - before} pages read to find a row of a table of {pages}.");
                found++;
            }
        }
        Assert.True(found > 0, "No table of indexCodesV1997 has a text index and 200 rows.");
    }
}
