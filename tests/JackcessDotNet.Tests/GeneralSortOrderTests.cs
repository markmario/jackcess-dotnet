using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Text keys in a database that sorts in Access 2010's General order, and what the engine still refuses: a value
/// with a character Access keys by the characters around it, in General, before anything changes. In General -
/// Legacy such a value is keyed.
/// The keys themselves are checked against ACE's in <see cref="TextIndexKeyTests"/>; these check that the tables
/// and the catalog use them.
/// </summary>
public sealed class GeneralSortOrderTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    // A copy of an Access 2010 database whose text sorts in General.
    private string GeneralDatabase()
    {
        string path = Path.Combine(Path.GetTempPath(), $"general_{Guid.NewGuid():N}.accdb");
        _paths.Add(path);
        File.Copy(TestCorpus.File("V2010", "common1V2010.accdb") ?? throw new FileNotFoundException("common1V2010.accdb"), path);
        return path;
    }

    private string LegacyDatabase()
    {
        string path = Path.Combine(Path.GetTempPath(), $"legacy_{Guid.NewGuid():N}.mdb");
        _paths.Add(path);
        Database.Create(path, FileFormat.Access2000).Dispose();
        return path;
    }

    private static ColumnDefinition[] CodeAndNumber() => new[]
    {
        new ColumnDefinition("Code", DataType.Text) { MaxLength = 255 },
        new ColumnDefinition("N", DataType.Int32),
    };

    private static int TdefOf(PageFile file, string table)
        => (int)JetPages.Rows(file, JetFormat.PageSystemCatalog)
            .Single(r => r.Values.TryGetValue("Name", out var name) && (string?)name == table
                      && r.Values.TryGetValue("Type", out var type) && type is short t && t == 1)
            .Values["Id"]!;

    // Enough rows for the key to split into several pages, of text whose keys differ between the two orders:
    // every entry is the General key of its row, in key order, and every row is found by it.
    [Fact]
    public void A_text_key_in_General_keeps_its_order_over_several_pages()
    {
        string[] words = { "résumé", "Æther", "naïve", "Zürich", "日本", "smörgåsbord", "a-b", "O'Brien", "ß", "Ελληνικά" };
        var codes = Enumerable.Range(0, 3000).Select(i => $"{words[i % words.Length]} {i * 7919 % 3000:D4}").ToList();

        string path = GeneralDatabase();
        using (var db = Database.Open(path))
        {
            var table = db.CreateTable("Words", CodeAndNumber(), primaryKey: "Code");
            for (int i = 0; i < codes.Count; i++)
                table.Insert(new Row { ["Code"] = codes[i], ["N"] = i });
        }

        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Words");
            Assert.True(table.Columns[0].SortOrder.IsGeneral);

            int tdef = TdefOf(db.File, "Words");
            int root = JetPages.IndexBlocks(db.File, tdef)[0].Root;
            Assert.False(JetPages.ReadIndexPage(db.File, root).IsLeaf);
            Assert.Empty(JetPages.RuleBreaks(db.File, root, tdef));
            Assert.Equal(
                codes.Select(c => Convert.ToHexString(IndexKeys.EncodeText(TextIndexCodes.General, ascending: true, c)))
                     .OrderBy(k => k, StringComparer.Ordinal),
                JetPages.LeafEntries(db.File, root).Select(e => e.Hex));

            for (int i = 0; i < codes.Count; i += 37)
                Assert.Equal(i, table.FindRowByPrimaryKey(codes[i])?["N"]);

            table.DeleteRow("Code", codes[5]);
            table.UpdateByPrimaryKey(codes[6], new Row { ["N"] = -6 });
            Assert.Null(table.FindRowByPrimaryKey(codes[5]));
            Assert.Equal(-6, table.FindRowByPrimaryKey(codes[6])?["N"]);
            Assert.Empty(JetPages.RuleBreaks(db.File, JetPages.IndexBlocks(db.File, tdef)[0].Root, tdef));
        }
    }

    // Keys of more than 510 bytes are cut and end in a digest: values that differ only past the cut still have
    // entries of their own, and each finds its row.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Values_whose_keys_are_longer_than_an_entry_holds_are_kept_and_found(bool general)
    {
        string path = general ? GeneralDatabase() : LegacyDatabase();
        var codes = new[] { new string('é', 255), new string('é', 254) + "e", new string('é', 254) + "x", new string('é', 254) };
        using (var db = Database.Open(path))
        {
            var table = db.CreateTable("Long", CodeAndNumber(), primaryKey: "Code");
            for (int i = 0; i < codes.Length; i++)
                table.Insert(new Row { ["Code"] = codes[i], ["N"] = i });
        }
        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Long");
            int tdef = TdefOf(db.File, "Long");
            var entries = JetPages.LeafEntries(db.File, JetPages.IndexBlocks(db.File, tdef)[0].Root);
            Assert.Equal(4, entries.Count);
            Assert.All(entries, e => Assert.True(e.Key.Length <= IndexKeys.MaxKeyLength));
            Assert.Contains(entries, e => e.Key.Length == IndexKeys.MaxKeyLength);
            for (int i = 0; i < codes.Length; i++)
                Assert.Equal(i, table.FindRowByPrimaryKey(codes[i])?["N"]);
        }
    }

    // ー is keyed by the character before it, and in General that key is not made. A row whose key holds it is
    // refused before it is written, a table is not given such a name, and the file is left byte for byte as it was.
    [Fact]
    public void A_key_value_Access_keys_by_its_neighbours_is_refused_in_General_and_the_file_left_as_it_was()
    {
        string path = GeneralDatabase();
        using (var db = Database.Open(path))
        {
            var table = db.CreateTable("Coded", CodeAndNumber(), primaryKey: "Code");
            table.Insert(new Row { ["Code"] = "テスト", ["N"] = 1 });
        }

        byte[] before = File.ReadAllBytes(path);
        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Coded");
            Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["Code"] = "データ", ["N"] = 2 }));
            Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["Code"] = "日々", ["N"] = 2 }));
            Assert.Throws<NotSupportedException>(() => table.UpdateByPrimaryKey("テスト", new Row { ["Code"] = "データ" }));
            Assert.Throws<NotSupportedException>(() => db.CreateTable("データ", CodeAndNumber()));

            // Looked for, such a value is no key to search by: the table is scanned, and it is not there.
            Assert.Null(table.FindRowByPrimaryKey("データ"));
            Assert.Equal(1, table.FindRowByPrimaryKey("テスト")?["N"]);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // In General - Legacy the same values are keys, under ACE's keys for them (TextIndexKeyTests): written, found
    // through the index, updated and deleted. A table is still not given a name with such a mark, with the file as
    // it was.
    [Fact]
    public void A_key_value_Access_keys_by_its_neighbours_is_kept_and_found_in_General_Legacy()
    {
        string path = LegacyDatabase();
        string[] marked = { "データ", "日々", "コンピューター", "ー", "佐々木", "ｺｰﾋｰ", "محمّد" };
        using (var db = Database.Open(path))
        {
            var table = db.CreateTable("Coded", CodeAndNumber(), primaryKey: "Code");
            table.Insert(new Row { ["Code"] = "テスト", ["N"] = 0 });
            for (int i = 0; i < marked.Length; i++)
                table.Insert(new Row { ["Code"] = marked[i], ["N"] = i + 1 });
            table.UpdateByPrimaryKey("テスト", new Row { ["Code"] = "テースト" });
            table.DeleteRow("Code", "日々");
        }
        byte[] before = File.ReadAllBytes(path);
        using (var db = Database.Open(path))
            Assert.Throws<NotSupportedException>(() => db.CreateTable("データ", CodeAndNumber()));
        Assert.Equal(before, File.ReadAllBytes(path));

        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Coded");
            int tdef = TdefOf(db.File, "Coded");
            Assert.Equal(
                marked.Where(m => m != "日々").Append("テースト")
                      .Select(c => Convert.ToHexString(IndexKeys.EncodeText(TextIndexCodes.GeneralLegacy, ascending: true, c)))
                      .OrderBy(k => k, StringComparer.Ordinal),
                JetPages.LeafEntries(db.File, JetPages.IndexBlocks(db.File, tdef)[0].Root).Select(e => e.Hex));
            for (int i = 0; i < marked.Length; i++)
                Assert.Equal(marked[i] == "日々" ? null : i + 1, table.FindRowByPrimaryKey(marked[i])?["N"]);
            Assert.Equal(0, table.FindRowByPrimaryKey("テースト")?["N"]);
            Assert.Null(table.FindRowByPrimaryKey("テスト"));
        }
    }
    // ── Every index, not only the primary key ───────────────────────────────

    private static Table CreateIndexed(Database db) => db.CreateTable("Indexed", new[]
    {
        new ColumnDefinition("Id", DataType.Int32),
        new ColumnDefinition("Code", DataType.Text) { MaxLength = 255 },
        new ColumnDefinition("Family", DataType.Text) { MaxLength = 255 },
        new ColumnDefinition("Given", DataType.Text) { MaxLength = 255 },
        new ColumnDefinition("Notes", DataType.Memo),
        new ColumnDefinition("Free", DataType.Text) { MaxLength = 255 },
    }, new[] { "Id" }, new[]
    {
        new IndexDefinition("ByCode", "Code") { IsUnique = true },
        new IndexDefinition("ByName", "Family", "Given"),
        new IndexDefinition("ByNotes", "Notes"),
    });

    private static readonly string[] Words = { "résumé", "Æther", "naïve", "Zürich", "日本", "a-b", "O'Brien", "ß", "Ελληνικά", "\U0001F600", "テスト" };

    // A table the engine creates in a file that sorts in General, with a unique text index, an index on two text
    // columns and one on a Memo: every index holds exactly the rows, under General keys, through inserts, updates
    // and deletes - and in a General - Legacy file under those keys.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_text_and_Memo_index_of_a_table_is_kept_in_the_order_of_its_database(bool general)
    {
        string path = general ? GeneralDatabase() : LegacyDatabase();
        using (var db = Database.Open(path))
        {
            var table = CreateIndexed(db);
            for (int i = 0; i < 600; i++)
                table.Insert(new Row
                {
                    ["Id"] = i, ["Code"] = $"{Words[i % Words.Length]} {i:D4}", ["Family"] = Words[i % 7], ["Given"] = i % 11 == 0 ? null : Words[i % 5] + i % 13,
                    ["Notes"] = i % 4 == 0 ? new string('é', 300) + i : Words[i % Words.Length], ["Free"] = "データ",
                });
            for (int i = 0; i < 600; i += 7)
                table.DeleteRow("Id", i);
            for (int i = 1; i < 600; i += 7)
                table.UpdateByPrimaryKey(i, new Row { ["Code"] = $"changed {Words[i % Words.Length]} {i}", ["Family"] = "Ünïcødé", ["Notes"] = "ñ" + i });
        }
        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Indexed");
            var order = general ? SortOrder.General : SortOrder.GeneralLegacy;
            Assert.All(table.Columns.Where(c => c.DataType is DataType.Text or DataType.Memo), c => Assert.Equal(order, c.SortOrder));
            Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 600 - 86));
            Assert.Equal(9, table.CreateIndexCursor("ByCode").FindRow("Code", $"{Words[9]} 0009")?["Id"]);
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 9000, ["Code"] = $"{Words[2].ToUpperInvariant()} 0002" }));   // unique, whatever the case
        }
    }

    // In General, a value Access keys by its neighbours is refused in any indexed column - the unique index, the
    // second column of the two-column one, the Memo - on insert and on update, with every page as it was; and taken
    // in a column no index is on.
    [Fact]
    public void A_value_Access_keys_by_its_neighbours_is_refused_in_any_indexed_column_in_General_and_taken_in_others()
    {
        using var db = Database.Open(GeneralDatabase());
        var table = CreateIndexed(db);
        table.Insert(new Row { ["Id"] = 1, ["Code"] = "one", ["Family"] = "f", ["Given"] = "g", ["Notes"] = "n", ["Free"] = "データ 日々" });
        var before = IndexChecks.AllPages(db);

        foreach (string column in new[] { "Code", "Family", "Given", "Notes" })
        {
            var refused = Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["Id"] = 2, ["Code"] = "two", [column] = "データ" }));
            Assert.Contains(column, refused.Message);
            Assert.Throws<NotSupportedException>(() => table.UpdateByPrimaryKey(1, new Row { [column] = "日々" }));
        }
        // Nor is an index built over a column that holds such a value.
        Assert.Throws<NotSupportedException>(() => table.AddIndex(new IndexDefinition("ByFree", "Free")));
        IndexChecks.AssertUnchanged(before, db);

        table.UpdateByPrimaryKey(1, new Row { ["Free"] = "コンピューター" });
        table.Insert(new Row { ["Id"] = 2, ["Code"] = "two", ["Free"] = "ー" });
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 2));
        Assert.Equal("コンピューター", table.FindRowByPrimaryKey(1)?["Free"]);
    }

    // In General - Legacy the same values are kept in every indexed column, and found through each index. A key is
    // the key of every value that has it: "aー" is "aa" to Access, and to the unique index a duplicate of it.
    [Fact]
    public void A_value_Access_keys_by_its_neighbours_is_kept_in_any_indexed_column_in_General_Legacy()
    {
        using var db = Database.Open(LegacyDatabase());
        var table = CreateIndexed(db);
        table.Insert(new Row { ["Id"] = 1, ["Code"] = "one", ["Family"] = "f", ["Given"] = "g", ["Notes"] = "n", ["Free"] = "データ 日々" });
        foreach (string column in new[] { "Code", "Family", "Given", "Notes" })
        {
            table.Insert(new Row { ["Id"] = 2, ["Code"] = "two", [column] = "データ" });
            table.UpdateByPrimaryKey(1, new Row { [column] = "日々" });
            Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 2));
            table.DeleteRow("Id", 2);
            table.UpdateByPrimaryKey(1, new Row { [column] = column == "Code" ? "one" : "x" });
        }

        table.Insert(new Row { ["Id"] = 3, ["Code"] = "aー", ["Family"] = "佐々木", ["Given"] = "ｺｰﾋｰ", ["Notes"] = "コンピューター" });
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 4, ["Code"] = "aa" }));
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 2));
        Assert.Equal(3, table.CreateIndexCursor("ByCode").FindRowByKey("aー")?["Id"]);
        Assert.Equal(3, table.CreateIndexCursor("ByName").FindRow("Family", "佐々木")?["Id"]);
        Assert.Equal(3, table.CreateIndexCursor("ByNotes").FindRowByKey("コンピューター")?["Id"]);

        // A range of the one key, and the rows an insert would be refused for: aa beside the table's aー, and カー
        // beside カｰ, whose keys are one.
        var range = table.CreateIndexCursor("ByCode", new IndexRange { StartKey = new object[] { "aa" }, EndKey = new object[] { "aa" } });
        Assert.Equal(new[] { 3 }, range.Select(r => (int)r["Id"]!));
        var duplicates = table.FindDuplicates(new[]
        {
            new Row { ["Id"] = 5, ["Code"] = "aa" }, new Row { ["Id"] = 6, ["Code"] = "カー" }, new Row { ["Id"] = 7, ["Code"] = "カｰ" },
        }).Where(d => d.IndexName == "ByCode").ToList();
        Assert.Equal(new[] { "0 True", "1,2 False" }, duplicates.Select(d => $"{string.Join(",", d.RowIndexes)} {d.InTable}"));
    }

    // Two values of 255 characters that differ only where their keys are cut, and whose digests are the same:
    // their entries are the same bytes. ACE refuses the second in a unique index as a duplicate (in both orders;
    // EngineTablesInAccessTests), and so does the engine; an index that is not unique takes both.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_long_values_cut_to_the_same_entry_are_duplicates_in_a_unique_index(bool general)
    {
        string start = new string('Æ', 100) + new string('é', 115);
        string first = start + "èèèéèêéèéèëëéëêèèèêêèëéééëëëèêêéèëééëëéè", second = start + "èèëêèééêèèëêéêéëéêêéêêéèéèëèèééèëêéëéééé";
        var codes = general ? TextIndexCodes.General : TextIndexCodes.GeneralLegacy;
        Assert.Equal(IndexKeys.Truncate(IndexKeys.EncodeText(codes, true, first)), IndexKeys.Truncate(IndexKeys.EncodeText(codes, true, second)));
        Assert.NotEqual(IndexKeys.EncodeText(codes, true, first), IndexKeys.EncodeText(codes, true, second));

        using var db = Database.Open(general ? GeneralDatabase() : LegacyDatabase());
        var table = CreateIndexed(db);
        table.Insert(new Row { ["Id"] = 1, ["Code"] = first, ["Family"] = first });
        var refused = Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 2, ["Code"] = second }));
        Assert.Contains("ByCode", refused.Message);

        table.Insert(new Row { ["Id"] = 2, ["Code"] = "other", ["Family"] = second });   // ByName is not unique
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 2));
        Assert.Equal(2, table.CreateIndexCursor("ByName").FindRow("Family", second)?["Id"]);   // scanned for: the key is cut
    }

    // A table made from the columns of a table in another database, which sorts otherwise: the new table has
    // columns of its own, in its database's order, and the columns it was made from - the other table's, still in
    // use - are as they were, whether the new table is made or refused. So the other table goes on keying its
    // text in its own order: rows added to it afterwards are found through its text index, and every index holds
    // the rows under that order's keys.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_table_made_from_another_table_s_columns_leaves_those_columns_and_that_table_as_they_were(bool sourceIsGeneral)
    {
        using var source = Database.Open(sourceIsGeneral ? GeneralDatabase() : LegacyDatabase());
        using var target = Database.Open(sourceIsGeneral ? LegacyDatabase() : GeneralDatabase());
        var sourceOrder = sourceIsGeneral ? SortOrder.General : SortOrder.GeneralLegacy;
        var targetOrder = sourceIsGeneral ? SortOrder.GeneralLegacy : SortOrder.General;

        var table = CreateIndexed(source);
        for (int i = 0; i < 40; i++)
            table.Insert(new Row { ["Id"] = i, ["Code"] = $"{Words[i % Words.Length]} {i:D3}", ["Family"] = Words[i % 5], ["Notes"] = Words[i % 3] });

        var columns = table.Columns;
        List<string> State() => columns.Select(c => $"{c.Name} {c.SortOrder} {c.Ordinal} {c.FixedDataOffset} {c.VarLenTableIndex}").ToList();
        var before = State();
        Assert.All(columns.Where(c => c.DataType is DataType.Text or DataType.Memo), c => Assert.Equal(sourceOrder, c.SortOrder));

        // Refused: a key column the table has not, and a name no key is made for.
        Assert.Throws<ArgumentException>(() => target.CreateTable("Refused", ColumnDefinitions.Of(columns), primaryKey: "NoSuchColumn"));
        Assert.Throws<NotSupportedException>(() => target.CreateTable("データ", ColumnDefinitions.Of(columns), primaryKey: "Id"));
        Assert.Equal(before, State());

        // Made, with the columns in another order than the source has them.
        var copy = target.CreateTable("Copy", ColumnDefinitions.Of(columns.Reverse()), new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") });
        Assert.Equal(before, State());
        Assert.All(copy.Columns, c => Assert.DoesNotContain(columns, own => ReferenceEquals(own, c)));
        Assert.All(copy.Columns.Where(c => c.DataType is DataType.Text or DataType.Memo), c => Assert.Equal(targetOrder, c.SortOrder));
        for (int i = 0; i < 40; i++)
            copy.Insert(new Row { ["Id"] = i, ["Code"] = $"{Words[i % Words.Length]} {i:D3}" });
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(target, "Copy", expectedRows: 40));

        // The source table goes on in its own order.
        for (int i = 40; i < 80; i++)
            table.Insert(new Row { ["Id"] = i, ["Code"] = $"{Words[i % Words.Length]} {i:D3}", ["Family"] = Words[i % 5], ["Notes"] = Words[i % 3] });
        table.UpdateByPrimaryKey(3, new Row { ["Code"] = "Ünïcødé" });
        table.DeleteRow("Id", 4);
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(source, "Indexed", expectedRows: 79));
        var byCode = table.Indexes.Single(ix => ix.Name == "ByCode");
        Assert.Equal(sourceOrder, byCode.Columns[0].Column.SortOrder);
        foreach (int i in new[] { 0, 5, 41, 60, 79 })
            Assert.NotEmpty(new IndexReader(source.File, byCode).FindRowPointers($"{Words[i % Words.Length]} {i:D3}"));
        Assert.Equal(60, table.CreateIndexCursor("ByCode").FindRow("Code", $"{Words[60 % Words.Length]} 060")?["Id"]);
    }

    // A column that is no key takes any text: only keys are made of it.
    [Fact]
    public void A_value_Access_keys_by_its_neighbours_is_written_where_it_is_no_key()
    {
        string path = GeneralDatabase();
        using (var db = Database.Open(path))
        {
            var table = db.CreateTable("Notes", new[]
            {
                new ColumnDefinition("Id", DataType.Int32),
                new ColumnDefinition("Note", DataType.Text) { MaxLength = 50 },
            }, primaryKey: "Id");
            table.Insert(new Row { ["Id"] = 1, ["Note"] = "データ" });
        }
        using (var db = Database.Open(path))
            Assert.Equal("データ", db.GetTable("Notes").FindRowByPrimaryKey(1)?["Note"]);
    }
}
