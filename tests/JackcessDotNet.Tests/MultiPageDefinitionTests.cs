using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table's definition that is longer than a page (#37). Access puts what does not fit the first page on
/// another, named in the first page's bytes 4-7, and so on: each later page has eight bytes of its own and then the
/// definition where the page before left it. Such a definition is read whole - Access's own, in the corpus's
/// <c>common2</c> files - and one that grows past its page, as a column or an index is added, or is longer than a
/// page when its table is created, is written that way, on the pages Access gives it: the fewest that hold its
/// length and eight bytes more. What Access makes of the result is in
/// SchemaChangesInAccessTests.
/// </summary>
public sealed class MultiPageDefinitionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"multi_page_tdef_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ColumnDefinition Long(string name) => new ColumnDefinition(name, DataType.Int32);

    /// <summary>A column name long enough that thirty of them fill a page: 60 characters.</summary>
    internal static string LongName(int n) => $"Column_{n:D3}_" + new string((char)('a' + n % 26), 49);

    /// <summary>The pages a table's definition is on, from its first: read from the file's own bytes.</summary>
    internal static List<int> PagesOf(Database db, string table)
    {
        var pages = new List<int> { db.GetTable(table).Definition.TdefPageNumber };
        while (ByteUtil.GetInt(db.File.ReadPage(pages[^1]), 4) is var next && next != 0)
            pages.Add(next);
        return pages;
    }

    /// <summary>
    /// The definition's pages are laid out as Access lays them out: every page a definition page; as many pages as
    /// hold the definition's length and eight bytes more, the first holding a page and each one after it eight
    /// less, so that a definition ending within eight bytes of a page's end has a page after it with none of it;
    /// the free space of the last page what the pages hold less the length and eight, and of every other 0; and
    /// 0xFFFF where the length says the definition ends. Access's own boundary tables are held to the same
    /// (<see cref="Access_lays_its_own_boundary_definitions_out_this_way"/>), so this is its rule and not the engine's.
    /// </summary>
    internal static void AssertLaidOutAsAccessDoes(Database db, string table)
    {
        var file  = db.File;
        int size  = file.Format.PageSize;
        var pages = PagesOf(db, table);
        int length = ByteUtil.GetInt(file.ReadPage(pages[0]), 8);

        int expectedPages = length + 8 <= size ? 1 : 1 + (length + 8 - size + size - 8 - 1) / (size - 8);
        Assert.Equal(expectedPages, pages.Count);
        int capacity = size + (pages.Count - 1) * (size - 8);

        for (int i = 0; i < pages.Count; i++)
        {
            byte[] page = file.ReadPage(pages[i]);
            bool isLast = i == pages.Count - 1;
            Assert.Equal(JetFormat.PageTypeTableDef, page[0]);
            Assert.Equal(1, page[1]);
            Assert.Equal(isLast ? capacity - length - 8 : 0, ByteUtil.GetShort(page, 2));
            Assert.Equal(isLast ? 0 : pages[i + 1], ByteUtil.GetInt(page, 4));
        }

        byte[] whole = TdefBuffer.Read(file, pages[0]);
        Assert.Equal(0xFFFF, ByteUtil.GetUShort(whole, length - 2));
    }

    // ── Where a definition ends near the end of a page ───────────────────────

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    /// <summary>The ends of a definition's first, second and third page: 4,096 bytes, and 4,088 for each page after.</summary>
    private static readonly int[] PageEnds = { 4096, 8184, 12272 };

    /// <summary>
    /// The names of Long columns that make a definition of <paramref name="length"/> bytes in a table with no
    /// index: 65 bytes, and for each column 27 and twice the characters of its name.
    /// </summary>
    internal static List<string> ColumnsMaking(int length)
    {
        int rest = length - 65;
        for (int count = 1; count <= 255; count++)
        {
            int characters = rest - 27 * count;
            if (characters % 2 != 0 || characters / 2 < 4 * count || characters / 2 > 64 * count) continue;
            characters /= 2;
            var names = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                int take = Math.Min(64, characters - 4 * (count - i));
                characters -= take;
                names.Add($"c{i:D3}" + new string('x', take - 4));
            }
            return names;
        }
        throw new InvalidOperationException($"No columns make a definition of {length} bytes.");
    }

    public static TheoryData<FileFormat, int> LengthsAcrossEachPageEnd()
    {
        var data = new TheoryData<FileFormat, int>();
        foreach (var format in new[] { FileFormat.Access2000, FileFormat.Access2007 })
            foreach (int end in PageEnds)
                for (int length = end - 12; length <= end + 12; length++)
                    data.Add(format, length);
        return data;
    }

    [Theory]
    [MemberData(nameof(LengthsAcrossEachPageEnd))]
    public void A_table_created_with_a_definition_ending_near_a_pages_end_is_laid_out_as_Access_lays_it_out(FileFormat format, int length)
    {
        var names = ColumnsMaking(length);
        using (var db = Database.Create(_path, format))
        {
            var table = db.CreateTable("Edge", names.Select(Long).ToList());
            Assert.Equal(length, ByteUtil.GetInt(db.File.ReadPage(table.Definition.TdefPageNumber), 8));
            AssertLaidOutAsAccessDoes(db, "Edge");
            foreach (int page in PagesOf(db, "Edge"))
                Assert.False(JetPages.IsFree(db.File, page), $"definition page {page} is free in the global usage map");
            table.Insert(new Row { [names[0]] = 1, [names[^1]] = 2 });
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Edge");
        Assert.Equal(names, read.Columns.Select(c => c.Name));
        Assert.False(read.Definition.HasUnreadColumns);
        Assert.Null(read.InsertRefusal);
        var row = Assert.Single(read.Rows);
        Assert.Equal(names.Count == 1 ? 2 : 1, row[names[0]]);
        Assert.Equal(2, row[names[^1]]);
    }

    /// <summary>
    /// A column added to a table with rows that takes its definition to within eight bytes of the end of a page -
    /// or to one past it - gets the definition the pages Access gives it. A Long column named "zz" adds 31 bytes:
    /// 27, and 2 for each character of its name.
    /// </summary>
    [Theory]
    [InlineData(FileFormat.Access2000, 4088)]
    [InlineData(FileFormat.Access2000, 4089)]
    [InlineData(FileFormat.Access2000, 4096)]
    [InlineData(FileFormat.Access2000, 4097)]
    [InlineData(FileFormat.Access2000, 8177)]
    [InlineData(FileFormat.Access2000, 8184)]
    [InlineData(FileFormat.Access2000, 12265)]
    [InlineData(FileFormat.Access2000, 12272)]
    [InlineData(FileFormat.Access2007, 4089)]
    [InlineData(FileFormat.Access2007, 4096)]
    public void A_column_added_that_ends_a_definition_near_a_pages_end_is_laid_out_as_Access_lays_it_out(FileFormat format, int length)
    {
        var names = ColumnsMaking(length - 31);
        using (var db = Database.Create(_path, format))
        {
            var table = db.CreateTable("Edge", names.Select(Long).ToList());
            table.Insert(new Row { [names[0]] = 1 });
            table.AddColumn(Long("zz"));
            Assert.Equal(length, ByteUtil.GetInt(db.File.ReadPage(table.Definition.TdefPageNumber), 8));
            AssertLaidOutAsAccessDoes(db, "Edge");
            foreach (int page in PagesOf(db, "Edge"))
                Assert.False(JetPages.IsFree(db.File, page), $"definition page {page} is free in the global usage map");
            table.Insert(new Row { [names[0]] = 2, ["zz"] = 3 });
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Edge");
        Assert.Equal(names.Append("zz"), read.Columns.Select(c => c.Name));
        Assert.Null(read.InsertRefusal);
        Assert.Equal(new object?[] { 1, 2 }, read.Rows.Select(r => r[names[0]]));
        Assert.Equal(new object?[] { null, 3 }, read.Rows.Select(r => r["zz"]));
    }

    public static TheoryData<string, int> AccessMadeBoundaryTables()
    {
        var data = new TheoryData<string, int>();
        foreach (string file in new[] { "dao-boundary-definitions.mdb", "dao-boundary-definitions.accdb" })
            foreach (int end in PageEnds)
                foreach (int length in new[] { end - 8, end - 7, end - 4, end, end + 1 })
                    data.Add(file, length);
        return data;
    }

    [Theory]
    [MemberData(nameof(AccessMadeBoundaryTables))]
    public void Access_lays_its_own_boundary_definitions_out_this_way(string file, int length)
    {
        using var db = Database.Open(Asset(file));
        var table = db.GetTable($"L{length}");
        Assert.Equal(length, ByteUtil.GetInt(db.File.ReadPage(table.Definition.TdefPageNumber), 8));
        AssertLaidOutAsAccessDoes(db, table.Name);
        Assert.Equal(ColumnsMaking(length), table.Columns.Select(c => c.Name));
        Assert.Equal(1, Assert.Single(table.Rows)[table.Columns[0].Name]);
    }

    [Theory]
    [MemberData(nameof(AccessMadeBoundaryTables))]
    public void A_boundary_definition_Access_made_keeps_Accesss_pages_through_a_rename_an_added_and_a_dropped_column(string file, int length)
    {
        string path = Path.ChangeExtension(_path, Path.GetExtension(file));
        File.Copy(Asset(file), path, overwrite: true);
        try
        {
            string name = $"L{length}";
            string first, renamed;
            using (var db = Database.Open(path))
            {
                var table = db.GetTable(name);
                var pages = PagesOf(db, name);
                first   = table.Columns[0].Name;
                renamed = "r" + first.Substring(1);

                // A name as long as the one it replaces: the definition is as long as it was, and on the pages it was on.
                table.RenameColumn(first, renamed);
                AssertLaidOutAsAccessDoes(db, name);
                Assert.Equal(pages, PagesOf(db, name));
                foreach (int page in pages)
                    Assert.False(JetPages.IsFree(db.File, page), $"definition page {page} was given back to the file");

                table.AddColumn(Long("Added"));
                AssertLaidOutAsAccessDoes(db, name);
                table.Insert(new Row { [renamed] = 2, ["Added"] = 20 });

                table.DropColumn("Added");
                AssertLaidOutAsAccessDoes(db, name);
                var after = PagesOf(db, name);
                Assert.Equal(pages.Count, after.Count);
                Assert.Equal(length, ByteUtil.GetInt(db.File.ReadPage(after[0]), 8));
            }

            using var reopened = Database.Open(path);
            var read = reopened.GetTable(name);
            Assert.Null(read.InsertRefusal);
            Assert.Equal(new object?[] { 1, 2 }, read.Rows.Select(r => r[renamed]));
            // The other tables of the file are as they were.
            foreach (int end in PageEnds)
                foreach (int other in new[] { end - 8, end - 7, end - 4, end, end + 1 })
                    if (other != length) AssertLaidOutAsAccessDoes(reopened, $"L{other}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_definition_that_no_longer_needs_its_last_page_gives_it_back_and_one_that_needs_it_again_takes_a_page()
    {
        // 4,089 bytes: on two pages, the second with none of it. A column shorter, it is on one.
        var names = ColumnsMaking(4089);
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("Edge", names.Select(Long).Append(Long("z")).ToList());
        table.DropColumn("z");
        Assert.Equal(4089, ByteUtil.GetInt(db.File.ReadPage(table.Definition.TdefPageNumber), 8));
        var two = PagesOf(db, "Edge");
        Assert.Equal(2, two.Count);

        table.RenameColumn(names[^1], names[^1].Substring(1));     // a character shorter: 4,087 bytes
        AssertLaidOutAsAccessDoes(db, "Edge");
        Assert.Single(PagesOf(db, "Edge"));
        Assert.True(JetPages.IsFree(db.File, two[1]), "the page the definition no longer needs was not given back");

        table.RenameColumn(names[^1].Substring(1), names[^1]);
        AssertLaidOutAsAccessDoes(db, "Edge");
        Assert.Equal(2, PagesOf(db, "Edge").Count);
        foreach (int page in PagesOf(db, "Edge"))
            Assert.False(JetPages.IsFree(db.File, page), $"definition page {page} is free in the global usage map");
    }

    // ── A definition whose pages stop short ──────────────────────────────────

    /// <summary>
    /// A definition's first page, or its second, names as its next a page that is not one of its own. The table is
    /// opened and its rows read by the columns the pages before name - with the first page's pointer damaged, what
    /// a reader of the first page alone reads - and nothing is written to it.
    /// </summary>
    [Theory]
    [InlineData(0, "a page the file has not")]
    [InlineData(0, "its own first page")]
    [InlineData(0, "a data page")]
    [InlineData(1, "a page the file has not")]
    [InlineData(1, "its own first page")]
    [InlineData(1, "a data page")]
    public void A_table_whose_definition_goes_on_to_a_page_that_is_not_its_own_is_opened_and_read_and_not_written_to(int damagedPage, string next)
    {
        var columns = new List<ColumnDefinition> { Long("Id") };
        columns.AddRange(Enumerable.Range(0, 120).Select(n => Long(LongName(n))));
        int first, damaged;
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Long names", columns);
            for (int i = 1; i <= 3; i++) table.Insert(new Row { ["Id"] = i, [LongName(0)] = 10 * i });
            var other = db.CreateTable("Other", new[] { Long("Id") });
            other.Insert(new Row { ["Id"] = 1 });

            var pages = PagesOf(db, "Long names");
            Assert.True(pages.Count > 2, $"the definition is on {pages.Count} pages");
            first   = pages[0];
            damaged = pages[damagedPage];
            int target = next switch
            {
                "a page the file has not" => db.File.PageCount + 100,
                "its own first page"      => first,
                _                         => Enumerable.Range(0, db.File.PageCount).First(p => db.File.ReadPage(p)[0] == JetFormat.PageTypeData),
            };
            byte[] page = db.File.ReadPage(damaged);
            ByteUtil.PutInt(page, 4, target);
            db.File.WritePage(damaged, page);
        }

        byte[] before = File.ReadAllBytes(_path);
        using (var refusing = Database.Open(_path))
        {
            Assert.Contains("Long names", refusing.GetTableNames());
            var read = refusing.GetTable("Long names");
            Assert.True(read.Definition.HasUnreadColumns);
            Assert.NotNull(read.InsertRefusal);
            Assert.Equal("Id", read.Columns[0].Name);
            Assert.Equal(LongName(0), read.Columns[1].Name);
            Assert.Equal(new object?[] { 1, 2, 3 }, read.Rows.Select(r => r["Id"]));
            Assert.Equal(new object?[] { 10, 20, 30 }, read.Rows.Select(r => r[LongName(0)]));

            Assert.Throws<NotSupportedException>(() => read.Insert(new Row { ["Id"] = 4 }));
            Assert.Throws<NotSupportedException>(() => read.AddColumn(Long("More")));
            Assert.Throws<NotSupportedException>(() => read.DropColumn(LongName(0)));
            Assert.Throws<NotSupportedException>(() => read.RenameColumn("Id", "Key"));
            Assert.Throws<NotSupportedException>(() => refusing.DropTable("Long names"));
        }
        Assert.Equal(before, File.ReadAllBytes(_path));

        // The rest of the file is as usable as it was.
        using var reopened = Database.Open(_path);
        reopened.GetTable("Other").Insert(new Row { ["Id"] = 2 });
        Assert.Equal(2, reopened.GetTable("Other").Rows.Count());
    }

    // ── Access's own ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("V2000", "common2V2000.mdb")]
    [InlineData("V2003", "common2V2003.mdb")]
    [InlineData("V2007", "common2V2007.accdb")]
    [InlineData("V2010", "common2V2010.accdb")]
    public void A_definition_Access_wrote_over_two_pages_is_read_whole_and_its_table_written_to(string version, string name)
    {
        string? source = TestCorpus.File(version, name);
        if (source is null) return;
        File.Copy(source, _path, overwrite: true);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("MSP_PROJECTS");
            Assert.Equal(2, PagesOf(db, "MSP_PROJECTS").Count);
            AssertLaidOutAsAccessDoes(db, "MSP_PROJECTS");

            // Every one of its 74 columns has the name Access gave it, the last ones from the second page.
            Assert.Equal(74, table.Columns.Count);
            Assert.DoesNotContain(table.Columns, c => c.Name.StartsWith("__", StringComparison.Ordinal));
            Assert.Equal("PROJ_ID", table.Columns[0].Name);
            Assert.Contains(table.Columns, c => c.Name == "PROJ_OPT_SPREAD_PCT_COMP");
            Assert.False(table.Definition.HasUnreadColumns);
            Assert.False(table.Definition.HasUnreadIndexes);
            Assert.NotEmpty(table.Indexes);

            // And it takes rows: one more, found through its index, with every row still in every index.
            Assert.Null(table.InsertRefusal);
            int rows = table.Rows.Count();
            table.Insert(new Row { ["PROJ_ID"] = 4242, ["PROJ_NAME"] = "written by the engine", ["PROJ_OPT_SPREAD_PCT_COMP"] = true });
            Assert.Equal(rows + 1, table.RowCount);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSP_PROJECTS", expectedRows: rows + 1);
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("MSP_PROJECTS").Rows.Single(r => Equals(r["PROJ_ID"], 4242));
        Assert.Equal("written by the engine", read["PROJ_NAME"]);
        Assert.Equal(true, read["PROJ_OPT_SPREAD_PCT_COMP"]);
    }

    [Fact]
    public void A_definition_Access_wrote_over_two_pages_takes_a_column_and_an_index()
    {
        string? source = TestCorpus.File("V2003", "common2V2003.mdb");
        if (source is null) return;
        File.Copy(source, _path, overwrite: true);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("MSP_PROJECTS");
            int rows = table.Rows.Count();
            int indexes = table.Indexes.Count;
            table.AddColumn(Long("ADDED_BY_THE_ENGINE"));
            table.AddIndex(new IndexDefinition("ByAdded", "ADDED_BY_THE_ENGINE"));
            table.Insert(new Row { ["PROJ_ID"] = 4243, ["ADDED_BY_THE_ENGINE"] = 7 });

            AssertLaidOutAsAccessDoes(db, "MSP_PROJECTS");
            Assert.Equal(75, table.Columns.Count);
            Assert.Equal(indexes + 1, table.Indexes.Count);
            Assert.Equal(4243, table.CreateIndexCursor("ByAdded").FindRow("ADDED_BY_THE_ENGINE", 7)!["PROJ_ID"]);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSP_PROJECTS", expectedRows: rows + 1);
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(75, reopened.GetTable("MSP_PROJECTS").Columns.Count);
        Assert.Equal(7, reopened.GetTable("MSP_PROJECTS").Rows.Single(r => Equals(r["PROJ_ID"], 4243))["ADDED_BY_THE_ENGINE"]);
    }

    [Fact]
    public void An_Access_97_definition_over_two_pages_is_read_whole()
    {
        // Table2 of the Access 97 common1: 89 columns in pages half the size.
        string? source = TestCorpus.File("V1997", "common1V1997.mdb");
        if (source is null) return;
        using var db = Database.Open(source);
        int multiPage = 0;
        foreach (var table in db.GetTableNames().Select(db.GetTable))
        {
            Assert.DoesNotContain(table.Columns, c => c.Name.StartsWith("__", StringComparison.Ordinal));
            Assert.False(table.Definition.HasUnreadColumns, table.Name);
            if (ByteUtil.GetInt(db.File.ReadPage(table.Definition.TdefPageNumber), 4) != 0) multiPage++;
            _ = table.Rows.Count();
        }
        Assert.True(multiPage > 0, "no table of the file has a definition over more than one page");
    }

    // ── The engine's ─────────────────────────────────────────────────────────

    [Fact]
    public void Columns_added_until_the_definition_is_on_four_pages_are_all_there_and_the_table_is_written_to()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Wide", new[] { Long("Id"), new ColumnDefinition("Name", DataType.Text) { MaxLength = 40 } },
                                       new[] { "Id" }, new[] { new IndexDefinition("ByName", "Name") });
            for (int i = 1; i <= 5; i++) table.Insert(new Row { ["Id"] = i, ["Name"] = $"name {i}" });

            int pagesBefore = 1;
            for (int n = 0; n < 100; n++)
            {
                table.AddColumn(n % 10 == 9 ? new ColumnDefinition(LongName(n), DataType.Memo) : Long(LongName(n)));
                AssertLaidOutAsAccessDoes(db, "Wide");
                int pagesNow = PagesOf(db, "Wide").Count;
                Assert.InRange(pagesNow, pagesBefore, pagesBefore + 1);
                pagesBefore = pagesNow;

                // Every table open on it goes on writing rows by all its columns.
                table.Insert(new Row { ["Id"] = 100 + n, ["Name"] = $"after {n}", [LongName(n)] = n % 10 == 9 ? new string('m', 3_000) : n });
            }
            Assert.Equal(4, pagesBefore);
            table.AddIndex(new IndexDefinition("ByLast", LongName(98)) { IsUnique = true });
            AssertLaidOutAsAccessDoes(db, "Wide");
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Wide");
        Assert.Equal(102, read.Columns.Count);
        Assert.Equal(Enumerable.Range(0, 100).Select(LongName), read.Columns.Skip(2).Select(c => c.Name));
        Assert.Null(read.InsertRefusal);
        Assert.Equal(105, read.Rows.Count());
        Assert.Equal(198, read.CreateIndexCursor("ByLast").FindRow(LongName(98), 98)!["Id"]);
        Assert.Equal(new string('m', 3_000), read.FindRowByPrimaryKey(199)![LongName(99)]);
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Wide", expectedRows: 105));

        // The pages the definition went on to are the file's: none is one Access would hand out again.
        foreach (int page in PagesOf(reopened, "Wide"))
            Assert.False(JetPages.IsFree(reopened.File, page), $"definition page {page} is free in the global usage map");
    }

    [Fact]
    public void A_table_created_with_a_definition_longer_than_a_page_is_written_over_pages_and_takes_rows()
    {
        var columns = new List<ColumnDefinition> { Long("Id") };
        columns.AddRange(Enumerable.Range(0, 120).Select(n => Long(LongName(n))));
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Long names", columns, new[] { "Id" }, new[] { new IndexDefinition("ByLast", LongName(119)) });
            Assert.Equal(5, PagesOf(db, "Long names").Count);
            AssertLaidOutAsAccessDoes(db, "Long names");
            for (int i = 1; i <= 50; i++) table.Insert(new Row { ["Id"] = i, [LongName(0)] = i, [LongName(119)] = -i });
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Long names");
        Assert.Equal(columns.Select(c => c.Name), read.Columns.Select(c => c.Name));
        Assert.Equal(50, read.RowCount);
        Assert.Equal(17, read.CreateIndexCursor("ByLast").FindRow(LongName(119), -17)!["Id"]);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Long names", expectedRows: 50));
    }

    // Only the file can fail once a change is being written: here it does, at each page write in turn of a column
    // that takes the definition onto a new page, and every time the file is byte for byte what it was.
    [Fact]
    public void A_column_that_takes_the_definition_onto_another_page_and_fails_at_any_write_leaves_the_file_as_it_was()
    {
        string added = LongName(500);
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Wide", new[] { Long("Id") }, primaryKey: "Id");
            table.Insert(new Row { ["Id"] = 1 });
            // To the last column the first page holds.
            for (int n = 0; PagesOf(db, "Wide").Count == 1; n++)
            {
                added = LongName(n);
                table.AddColumn(Long(added));
            }
        }
        // One step back: the table as it was before the column that crossed the page.
        byte[] crossed = File.ReadAllBytes(_path);
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Wide", new[] { Long("Id") }, primaryKey: "Id");
            table.Insert(new Row { ["Id"] = 1 });
            for (int n = 0; LongName(n) != added; n++) table.AddColumn(Long(LongName(n)));
            Assert.Single(PagesOf(db, "Wide"));
        }
        byte[] before = File.ReadAllBytes(_path);
        Assert.True(crossed.Length > before.Length);

        int failures = 0;
        for (int writes = 0; ; writes++)
        {
            Assert.True(writes < 100, "the change never finished");
            using var db = Database.Open(_path);
            var table = db.GetTable("Wide");
            int columns = table.Columns.Count;
            db.File.WritesBeforeFailure = writes;
            try
            {
                table.AddColumn(Long(added));
                db.File.WritesBeforeFailure = null;
                Assert.Equal(2, PagesOf(db, "Wide").Count);
                break;
            }
            catch (IOException)
            {
                db.File.WritesBeforeFailure = null;
                failures++;
            }
            Assert.Equal(columns, table.Columns.Count);
            Assert.Single(PagesOf(db, "Wide"));
            db.Dispose();
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), $"the file changed when write {writes + 1} of the change failed");
        }
        Assert.True(failures >= 3, $"only {failures} writes failed: a new page, the global usage map and two definition pages are at least four");
    }
}
