using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A database opened only to be read (<see cref="DatabaseOpenOptions.ReadOnly"/>) reading beside a writer that changes
/// the file between two of its moves (#62). The writer here is another <see cref="Database"/>, in the same process; in
/// ExportMdb.Interop's FileSharingInAccessTests it is ACE. What a reading beside a writer can and cannot give is in
/// <see cref="DatabaseOpenOptions.ReadOnly"/>; these are the cases a reader that kept what it had read gave wrong:
/// a row twice, and a row with a long value of another row's or of none.
/// </summary>
public sealed class ReaderBesideWriterTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string NewPath(FileFormat format)
    {
        string path = Path.Combine(Path.GetTempPath(), $"beside_{Guid.NewGuid():N}{(format == FileFormat.Access2000 ? ".mdb" : ".accdb")}");
        _paths.Add(path);
        return path;
    }

    public static IEnumerable<object[]> Formats() => new[] { new object[] { FileFormat.Access2000 }, new object[] { FileFormat.Access2010 } };

    private static DatabaseOpenOptions ReadOnly => new() { ReadOnly = true };

    private static string Memo(char c) => new(c, 300);

    /// <summary>T (Id, the primary key, and V, text) of 3,000 rows, "row 1" to "row 3000".</summary>
    private string ThreeThousandRows(FileFormat format)
    {
        string path = NewPath(format);
        using var db = Database.Create(path, format);
        db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("V", DataType.Text) { MaxLength = 50 } }, "Id")
          .InsertRange(Enumerable.Range(1, 3000).Select(i => new Row { ["Id"] = i, ["V"] = "row " + i }));
        return path;
    }

    /// <summary>T (Id, the primary key, and M, a Memo) of five rows, each memo 300 of one letter: 'a' for row 1 to 'e'.</summary>
    private string FiveMemos(FileFormat format)
    {
        string path = NewPath(format);
        using var db = Database.Create(path, format);
        var t = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("M", DataType.Memo) }, "Id");
        for (int i = 1; i <= 5; i++) t.Insert(new Row { ["Id"] = i, ["M"] = Memo((char)('a' + i - 1)) });
        return path;
    }

    // ── A row twice ──────────────────────────────────────────────────────────

    // A reader's index cursor comes to the last row of a leaf; the writer then gives the row after it a key at the end
    // of the index. The cursor kept the leaf it had read, which still had the row's old entry, and gave the row there;
    // it gave it again at its new entry, in a leaf it read afresh: 3,001 rows, the re-keyed one twice. A cursor of a
    // database opened only to be read reads the index again at each move, and gives it once, at its new key.
    [Theory]
    [MemberData(nameof(Formats))]
    public void An_index_cursor_beside_a_writer_gives_once_a_row_the_writer_moves_ahead_of_it(FileFormat format)
    {
        string path = ThreeThousandRows(format);
        using var writer = Database.Open(path);
        using var reader = Database.Open(path, ReadOnly);

        // The first row of the table's second data page.
        var pages = writer.GetTable("T").Rows.Select(r => (Id: (int)r["Id"]!, r.Id!.Value.PageNumber)).OrderBy(p => p.Id).ToList();
        int moved = pages.First(p => p.PageNumber != pages[0].PageNumber).Id;

        var cursor = reader.GetTable("T").CreateIndexCursor();
        var given = new List<int>();
        while (cursor.MoveNext())
        {
            given.Add((int)cursor.Current!["Id"]!);
            if (given[given.Count - 1] == moved - 1) break;
        }
        writer.GetTable("T").UpdateByPrimaryKey(moved, new Row { ["Id"] = 1_000_000 });
        while (cursor.MoveNext()) given.Add((int)cursor.Current!["Id"]!);

        Assert.Equal(3000, given.Count);
        Assert.Single(given, id => id == 1_000_000);
        Assert.DoesNotContain(moved, given);
        Assert.Equal(1_000_000, given[given.Count - 1]);
    }

    // ── A row with another row's long value ──────────────────────────────────

    // A reader's cursor is on row 1; the writer gives row 5 a short memo, kept in its row, which frees row 5's long
    // value - the last on its page, whose row count then drops - and adds row 6, whose memo takes that place on the
    // page. The cursor kept the data page it had read, and read row 5 through the reference it held: row 6's memo.
    // A cursor of a database opened only to be read reads its page again at each move, and gives row 5 as it is.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_table_cursor_beside_a_writer_gives_each_row_its_own_long_value(FileFormat format)
    {
        string path = FiveMemos(format);
        using var writer = Database.Open(path);
        using var reader = Database.Open(path, ReadOnly);

        var cursor = reader.GetTable("T").CreateCursor();
        Assert.True(cursor.MoveNext());
        Assert.Equal(1, cursor.Current!["Id"]);

        writer.GetTable("T").UpdateByPrimaryKey(5, new Row { ["M"] = "short" });
        writer.GetTable("T").Insert(new Row { ["Id"] = 6, ["M"] = Memo('z') });

        var rows = new List<Row>();
        while (cursor.MoveNext()) rows.Add(cursor.Current!);
        AssertTheRowsAsTheyAreNow(rows);
    }

    // The same, read with Table.Rows, which gave a page's rows from one reading of it.
    [Theory]
    [MemberData(nameof(Formats))]
    public void Table_Rows_beside_a_writer_gives_each_row_its_own_long_value(FileFormat format)
    {
        string path = FiveMemos(format);
        using var writer = Database.Open(path);
        using var reader = Database.Open(path, ReadOnly);

        using var rows = reader.GetTable("T").Rows.GetEnumerator();
        Assert.True(rows.MoveNext());
        Assert.Equal(1, rows.Current["Id"]);

        writer.GetTable("T").UpdateByPrimaryKey(5, new Row { ["M"] = "short" });
        writer.GetTable("T").Insert(new Row { ["Id"] = 6, ["M"] = Memo('z') });

        var rest = new List<Row>();
        while (rows.MoveNext()) rest.Add(rows.Current);
        AssertTheRowsAsTheyAreNow(rest);
    }

    private static void AssertTheRowsAsTheyAreNow(List<Row> rows)
    {
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, rows.Select(r => (int)r["Id"]!));
        Assert.Equal(new[] { Memo('b'), Memo('c'), Memo('d'), "short", Memo('z') }, rows.Select(r => (string?)r["M"]));
    }

    // ── What it costs ────────────────────────────────────────────────────────

    // A database opened to be written is the file's one writer, and keeps the page it is on from one move to the next:
    // a cursor reads each data page once. One opened only to be read reads the page again at each move: once a row.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_cursor_reads_a_page_once_when_no_other_program_can_write_and_once_a_move_beside_a_writer(FileFormat format)
    {
        string path = ThreeThousandRows(format);
        int dataPages;
        using (var db = Database.Open(path))
            dataPages = db.GetTable("T").Rows.Select(r => r.Id!.Value.PageNumber).Distinct().Count();

        foreach (bool readOnly in new[] { false, true })
        {
            using var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = readOnly });
            var cursor = db.GetTable("T").CreateCursor();
            long before = db.File.ReadCount;
            int rows = 0;
            while (cursor.MoveNext()) rows++;
            long read = db.File.ReadCount - before;

            Assert.Equal(3000, rows);
            if (readOnly) Assert.InRange(read, 3000, 3000 + dataPages + 2);
            else Assert.InRange(read, dataPages, dataPages + 2);
        }
    }
}
