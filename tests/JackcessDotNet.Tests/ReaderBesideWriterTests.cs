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

    // A savepoint restored gives the row at its place as the file has it now. Restoring is no move, and the cursor read
    // the row from the page it had kept from its last move - row 1 as it was before the writer changed it, its memo
    // through a reference the writer had since freed. Beside a writer the row is read from the file again.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_savepoint_restored_beside_a_writer_gives_its_row_as_the_file_has_it_now(FileFormat format)
    {
        string path = FiveMemos(format);
        using var writer = Database.Open(path);
        using var reader = Database.Open(path, ReadOnly);

        var cursor = reader.GetTable("T").CreateCursor();
        Assert.True(cursor.MoveNext());
        var onRow1 = cursor.GetSavepoint();
        Assert.True(cursor.MoveNext());
        Assert.Equal(2, cursor.Current!["Id"]);

        writer.GetTable("T").UpdateByPrimaryKey(1, new Row { ["M"] = "changed" });

        cursor.RestoreSavepoint(onRow1);
        Assert.Equal(1, cursor.Current!["Id"]);
        Assert.Equal("changed", cursor.Current!["M"]);
    }

    // ── A page read again ─────────────────────────────────────────────────────

    // A page read again is read from the file, whatever its size. The file's stream kept a buffer of 4,096 bytes, so
    // reading one of an Access 97 file's pages of 2,048 read the next with it, and that one, read again at a later
    // move, came from the buffer as it was - on .NET, whatever another program had written to it since. A page of
    // 4,096 was always read from the file. The writer here is a handle of its own writing a page, as DAO writes an
    // Access 97 file this library only reads.
    [Theory]
    [InlineData("access97-jet40.mdb")]
    [InlineData(null)]
    public void A_page_read_again_beside_a_writer_is_read_from_the_file_whatever_its_size(string? asset)
    {
        string path = NewPath(FileFormat.Access2000);
        if (asset is null) Database.Create(path, FileFormat.Access2000).Dispose();
        else File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), path, overwrite: true);

        using var reader = Database.Open(path, ReadOnly);
        int size = reader.File.Format.PageSize;
        Assert.Equal(asset is null ? 4096 : 2048, size);
        reader.File.ReadPage(2);
        byte[] before = reader.File.ReadPage(3);

        var written = new byte[size];
        for (int i = 0; i < size; i++) written[i] = (byte)(before[i] ^ 0x5A);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Seek(3L * size, SeekOrigin.Begin);
            writer.Write(written, 0, size);
        }

        Assert.Equal(written, reader.File.ReadPage(3));
    }

    // ── A long value through a reference its row no longer holds ─────────────

    /// <summary>
    /// The bytes of the row whose Id is <paramref name="id"/>, read from its page as a reader that read the page a
    /// moment ago holds them - long-value references and all.
    /// </summary>
    private static byte[] RowAsReadNow(Database db, int id)
    {
        var table = db.GetTable("T");
        var rowId = table.FindRowByPrimaryKey(id)!.Id!.Value;
        return DataRows.Read(db.File, db.File.ReadPage(rowId.PageNumber), rowId.RowNumber)!;
    }

    private static object? DecodeMemo(Database db, byte[] row)
    {
        var table = db.GetTable("T");
        var decoder = new RowDecoder(db.File.Format, table.Definition.Columns, new LvalReader(db.File));
        return decoder.Decode(row, table.Definition.Columns.First(c => c.Name == "M"));
    }

    // A reference to a long value that has been deleted - the slot it names on its page marked so, and not yet the
    // page's last - read the value's old bytes as though they were still the row's. It is refused.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_long_value_whose_place_has_been_deleted_is_refused(FileFormat format)
    {
        string path = FiveMemos(format);
        using var writer = Database.Open(path);
        using var reader = Database.Open(path, ReadOnly);
        byte[] row3 = RowAsReadNow(reader, 3);
        Assert.Equal(Memo('c'), DecodeMemo(reader, row3));

        writer.GetTable("T").DeleteRow("Id", 3);

        var refused = Assert.Throws<InvalidDataException>(() => DecodeMemo(reader, row3));
        Assert.Contains("deleted", refused.Message);
    }

    // A reference to a long value whose place now holds a shorter one - another row's - read that row's bytes and
    // made up the rest with zeros. It is refused: the value no longer fits the reference.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_long_value_whose_place_now_holds_a_shorter_one_is_refused(FileFormat format)
    {
        string path = FiveMemos(format);
        using var writer = Database.Open(path);
        using var reader = Database.Open(path, ReadOnly);
        byte[] row5 = RowAsReadNow(reader, 5);

        writer.GetTable("T").UpdateByPrimaryKey(5, new Row { ["M"] = "short" });
        writer.GetTable("T").Insert(new Row { ["Id"] = 6, ["M"] = new string('z', 200) });

        Assert.Throws<InvalidDataException>(() => DecodeMemo(reader, row5));
    }

    // A reference to a long value whose page has dropped the row it names - deleted at the page's end - was refused
    // as an InvalidOperationException; it is refused as data that does not read, as every other such reference is.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_long_value_whose_page_has_dropped_its_place_is_refused(FileFormat format)
    {
        string path = FiveMemos(format);
        using var writer = Database.Open(path);
        using var reader = Database.Open(path, ReadOnly);
        byte[] row5 = RowAsReadNow(reader, 5);

        writer.GetTable("T").DeleteRow("Id", 5);

        Assert.Throws<InvalidDataException>(() => DecodeMemo(reader, row5));
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
