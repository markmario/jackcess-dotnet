using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table's definition that is longer than a page (#37). Access puts what does not fit the first page on
/// another, named in the first page's bytes 4-7, and so on: each later page has eight bytes of its own and then the
/// definition where the page before left it. Such a definition is read whole - Access's own, in the corpus's
/// <c>common2</c> files - and one that grows past its page, as a column or an index is added, or is longer than a
/// page when its table is created, is written that way. What Access makes of the result is in ExportMdb.Interop's
/// MultiPageDefinitionsInAccessTests.
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
    /// The definition's pages are laid out as Access lays them out: every page a definition page, the length the
    /// first page counted whole and the others without their eight bytes, a full page's free space 0 and the
    /// last page's what is left of it less eight, and 0xFFFF where the length says the definition ends.
    /// </summary>
    internal static void AssertLaidOutAsAccessDoes(Database db, string table)
    {
        var file  = db.File;
        int size  = file.Format.PageSize;
        var pages = PagesOf(db, table);
        int length = ByteUtil.GetInt(file.ReadPage(pages[0]), 8);

        int expectedPages = length <= size ? 1 : 1 + (length - size + size - 8 - 1) / (size - 8);
        Assert.Equal(expectedPages, pages.Count);

        int left = length;
        for (int i = 0; i < pages.Count; i++)
        {
            byte[] page = file.ReadPage(pages[i]);
            int room = i == 0 ? size : size - 8;
            int held = Math.Min(room, left);
            left -= held;
            Assert.Equal(JetFormat.PageTypeTableDef, page[0]);
            Assert.Equal(1, page[1]);
            Assert.Equal(Math.Max(room - held - 8, 0), ByteUtil.GetShort(page, 2));
            Assert.Equal(i + 1 < pages.Count ? pages[i + 1] : 0, ByteUtil.GetInt(page, 4));
        }
        Assert.Equal(0, left);

        byte[] whole = TdefBuffer.Read(file, pages[0]);
        Assert.Equal(0xFFFF, ByteUtil.GetUShort(whole, length - 2));
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
