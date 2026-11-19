using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Room on a table's data pages is used again as Access uses it (#58), as measured with DAO 16 and ACE 16 in .mdb and
/// .accdb files, on tables Access made and on tables this library made: a row goes to the lowest page the table's
/// free-space map lists, a delete takes the row's bytes off its page and lists the page when it had no room for the row,
/// a row that comes home lists the page its values leave, and a page an update leaves with no row on it is used again.
/// ExportMdb.Interop's suite has Access place the same rows and compares every page's room and slots, and the map
/// (<c>RoomReuseInAccessTests</c>).
/// Rows of 200 Greek letters are 419 bytes: nine fill a page and leave it 293 bytes.
/// </summary>
public sealed class RoomReuseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"room_reuse_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Table Wide(Database db, string name = "T") => db.CreateTable(name, new[]
    {
        new ColumnDefinition("Id", DataType.Int32),
        new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
        new ColumnDefinition("B", DataType.Text) { MaxLength = 255 },
        new ColumnDefinition("C", DataType.Text) { MaxLength = 255 },
        new ColumnDefinition("D", DataType.Text) { MaxLength = 255 },
    }, new[] { "Id" }, new[] { new IndexDefinition("ByA", "A") });

    /// <summary>Text of <paramref name="chars"/> Greek letters, two bytes each in a row; null for none.</summary>
    private static string? Greek(int chars) => chars == 0 ? null : new string('Ω', chars);

    /// <summary>A row of the wide table: 19 bytes and two a letter.</summary>
    private static void Put(Table table, int id, int a, int b = 0, int c = 0, int d = 0)
        => table.Insert(new Row { ["Id"] = id, ["A"] = Greek(a), ["B"] = Greek(b), ["C"] = Greek(c), ["D"] = Greek(d) });

    private static void Set(Table table, int id, params (string Column, int Chars)[] values)
    {
        var row = table.FindRowByPrimaryKey(id)!;
        foreach (var (column, chars) in values) row[column] = Greek(chars);
        table.Update(row);
    }

    private static void Delete(Table table, int id) => table.Delete(table.FindRowByPrimaryKey(id)!);

    private static RowId Where(Table table, int id) => table.FindRowByPrimaryKey(id)!.Id!.Value;

    /// <summary>The pages the table's free-space map lists, as the file has them.</summary>
    private static List<int> Listed(Database db, Table table)
    {
        var def = table.Definition;
        return UsageMap.GetOwnedPages(db.File.ReadPage(def.FreeSpaceUmapPage), def.FreeSpaceRow, db.File.Format, db.File);
    }

    /// <summary>The pages the session takes to be listed: what it places rows by.</summary>
    private static List<int> Known(Database db, Table table) => db.File.ListedDataPages.Of(table.Definition)!.ToList();

    private static List<int> Owned(Database db, Table table)
    {
        var def = table.Definition;
        return UsageMap.GetOwnedPages(db.File.ReadPage(def.UmapPageNumber), def.OwnedPagesRow, db.File.Format, db.File);
    }

    private static int Free(Database db, int page) => ByteUtil.GetShort(db.File.ReadPage(page), JetFormat.OffsetDataFreeSpace);

    private static int Slots(Database db, int page) => ByteUtil.GetShort(db.File.ReadPage(page), db.File.Format.OffsetDataNumRows);

    /// <summary>A slot's two flag bits, and the bytes it holds.</summary>
    private static (int Flags, int Length) Slot(Database db, int page, int row)
    {
        byte[] bytes = db.File.ReadPage(page);
        int table = db.File.Format.OffsetDataRowTable;
        int slot  = ByteUtil.GetUShort(bytes, table + row * JetFormat.SizeRowEntry);
        int end   = row == 0 ? bytes.Length : ByteUtil.GetUShort(bytes, table + (row - 1) * JetFormat.SizeRowEntry) & JetFormat.RowOffsetMask;
        return (slot & 0xC000, end - (slot & JetFormat.RowOffsetMask));
    }

    /// <summary>The table's data pages that hold slots and not one row: what a row updated back and forth left behind.</summary>
    private static int PagesOfDeletedSlotsOnly(Database db, Table table)
        => Owned(db, table).Count(page => Slots(db, page) > 0
            && Enumerable.Range(0, Slots(db, page)).All(row => Slot(db, page, row).Flags == JetFormat.DeletedRowFlags));

    // ── A delete ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2007)]
    public void A_deleted_row_s_bytes_leave_its_page_and_the_next_row_takes_the_room(FileFormat format)
    {
        string path = Path.ChangeExtension(_path, format == FileFormat.Access2000 ? ".mdb" : ".accdb");
        try
        {
            using (var db = Database.Create(path, format))
            {
                var table = Wide(db);
                for (int id = 1; id <= 30; id++) Put(table, id, 200);
                var pages = Owned(db, table);
                Assert.Equal(4, pages.Count);
                Assert.Equal(new[] { pages[3] }, Listed(db, table));   // a new page is listed; full ones left the map
                Assert.Equal(293, Free(db, pages[0]));

                // Its slot stays, deleted and empty; the rows after it close up; the page had no room for it: listed.
                Delete(table, 2);
                Assert.Equal((JetFormat.DeletedRowFlags, 0), Slot(db, pages[0], 1));
                Assert.Equal(293 + 419, Free(db, pages[0]));
                Assert.Equal(new[] { pages[0], pages[3] }, Listed(db, table));

                // The next row goes there, to a slot of its own: a deleted row's is never used again.
                Put(table, 31, 200);
                Assert.Equal(new RowId(pages[0], 9), Where(table, 31));
                Assert.Equal(new[] { pages[0], pages[3] }, Listed(db, table));

                // The one after does not fit there: the page leaves the map, and the row goes to the next listed.
                Put(table, 32, 200);
                Assert.Equal(pages[3], Where(table, 32).PageNumber);
                Assert.Equal(new[] { pages[3] }, Listed(db, table));
                Assert.Equal(Listed(db, table), Known(db, table));
            }

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                Assert.Equal(31, table.Rows.Count());
                Assert.Equal(Greek(200), table.FindRowByPrimaryKey(31)!["A"]);
                Assert.Null(table.FindRowByPrimaryKey(2));
                IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 31);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Access lists a page for a delete when, before it, the page had no room for the row it lost and a slot - at a
    /// byte either side, for rows of 101 to 401 bytes - whatever room the delete leaves it.
    /// </summary>
    [Theory]
    [InlineData(101, 102, true)]
    [InlineData(101, 103, false)]
    [InlineData(401, 402, true)]
    [InlineData(401, 403, false)]
    [InlineData(21, 1, true)]
    [InlineData(21, 51, false)]
    public void A_delete_lists_its_page_only_when_the_page_had_no_room_for_the_row(int rowBytes, int room, bool listed)
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        int id = 1;
        void Row(int bytes) => Put(table, id++, (bytes - 19) / 2);

        // The row to delete, then rows that leave the page exactly the room asked for: each row takes its bytes and a
        // two-byte slot, and every row is an odd number of bytes.
        Row(rowBytes);
        int need = 4082 - (rowBytes + 2) - room;
        int full = (need - 2 - 21) / 421;
        int rest = need - full * 421 - 2;
        var fillers = new List<int>();
        if (rest % 2 == 1) fillers.Add(rest);
        else if (rest >= 44) fillers.AddRange(new[] { 21, rest - 23 });
        else { full--; fillers.AddRange(new[] { 21, 21, rest + 375 }); }
        for (int i = 0; i < full; i++) Row(419);
        foreach (int filler in fillers) Row(filler);
        int page = Where(table, 1).PageNumber;
        Assert.Equal(room, Free(db, page));

        Row(529);   // does not fit: the page leaves the map
        Assert.DoesNotContain(page, Listed(db, table));

        Delete(table, 1);
        Assert.Equal(room + rowBytes, Free(db, page));
        Assert.Equal(listed, Listed(db, table).Contains(page));
        Assert.Equal(Listed(db, table), Known(db, table));
    }

    [Fact]
    public void A_delete_leaves_a_page_it_empties_to_the_table()
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 10; id++) Put(table, id, 200);
        int last = Where(table, 10).PageNumber;

        Delete(table, 10);
        Assert.Contains(last, Owned(db, table));
        Assert.Equal((JetFormat.DeletedRowFlags, 0), Slot(db, last, 0));
        Assert.Contains(last, Listed(db, table));
        Put(table, 11, 200);
        Assert.Equal(new RowId(last, 1), Where(table, 11));
    }

    [Fact]
    public void Rows_deleted_all_over_a_table_leave_room_the_next_rows_take()
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 900; id++) Put(table, id, 200);
        var pages = Owned(db, table);
        Assert.Equal(100, pages.Count);

        for (int id = 3; id <= 900; id += 3) Delete(table, id);
        for (int id = 901; id <= 1200; id++) Put(table, id, 200);

        Assert.Equal(pages, Owned(db, table));
        Assert.Equal(900, table.RowCount);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 900);
    }

    [Fact]
    public void A_deleted_row_s_slot_is_never_used_again_on_its_page()
    {
        // Access puts each row in a slot of its own and uses none a deleted row had, so a page that has had 255 rows
        // takes no more: the next go to a new page.
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 3; id++) Put(table, id, 200);
        int first = Where(table, 1).PageNumber;
        for (int i = 0; i < 300; i++)
        {
            Put(table, 100 + i, 1);
            Delete(table, 100 + i);
        }
        Assert.Equal(255, Slots(db, first));
        int second = Owned(db, table).Single(page => page != first);
        Assert.Equal(48, Slots(db, second));
        Assert.Equal(new[] { second }, Listed(db, table));
    }

    // ── Where a row goes ─────────────────────────────────────────────────────

    [Fact]
    public void A_row_goes_to_the_lowest_page_listed_not_the_last_one_listed()
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 30; id++) Put(table, id, 200);
        var pages = Owned(db, table);
        Delete(table, 2);    // the first page
        Delete(table, 21);   // the third
        Assert.Equal(new[] { pages[0], pages[2], pages[3] }, Listed(db, table));

        Put(table, 31, 200);
        Assert.Equal(pages[0], Where(table, 31).PageNumber);
        Put(table, 32, 200);
        Assert.Equal(pages[2], Where(table, 32).PageNumber);   // the first had no room left: it left the map
        Assert.Equal(new[] { pages[2], pages[3] }, Listed(db, table));
    }

    [Fact]
    public void A_page_leaves_the_map_only_when_a_row_is_tried_there_and_does_not_fit()
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 9; id++) Put(table, id, 200);
        Put(table, 10, 136);   // 291 bytes: the page has none left
        int first = Where(table, 1).PageNumber;
        Assert.Equal(0, Free(db, first));
        Assert.Equal(new[] { first }, Listed(db, table));

        Put(table, 11, 1);
        Assert.NotEqual(first, Where(table, 11).PageNumber);
        Assert.Equal(new[] { Where(table, 11).PageNumber }, Listed(db, table));
    }

    [Fact]
    public void A_new_page_is_listed_only_when_it_has_room_for_another_row_of_its_size()
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        Put(table, 1, 255, 255, 255, 255);   // 2,059 bytes: 2,021 left
        int first = Where(table, 1).PageNumber;
        Assert.Empty(Listed(db, table));

        // Access adds the next row, however small, to a page of its own, which it lists.
        Put(table, 2, 1);
        int second = Where(table, 2).PageNumber;
        Assert.NotEqual(first, second);
        Assert.Equal(new[] { second }, Listed(db, table));

        var other = Wide(db, "U");
        Put(other, 1, 255, 255, 255, 221);   // 1,991 bytes: 2,089 left
        Assert.Equal(new[] { Where(other, 1).PageNumber }, Listed(db, other));
    }

    // ── A row that moves ─────────────────────────────────────────────────────

    [Fact]
    public void A_row_that_comes_home_lists_the_page_its_values_leave_and_one_that_moves_on_does_not()
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 20; id++) Put(table, id, 200);
        Set(table, 3, ("B", 255), ("C", 255));               // to the third page
        for (int id = 21; id <= 27; id++) Put(table, id, 200);   // which rows then fill and leave off the map
        int third = Owned(db, table)[2];
        Assert.DoesNotContain(third, Listed(db, table));

        Set(table, 3, ("B", 0), ("C", 0));                   // home: 1,439 bytes freed on a page with 115
        Assert.Contains(third, Listed(db, table));

        var other = Wide(db, "U");
        for (int id = 1; id <= 20; id++) Put(other, id, 200);
        Set(other, 3, ("B", 255), ("C", 255));
        for (int id = 21; id <= 27; id++) Put(other, id, 200);
        int away = Owned(db, other)[2];
        Set(other, 3, ("D", 255));                           // past that page's room too: on to another
        Assert.NotEqual(away, RowsMovedTo(db, other, 3));
        Assert.DoesNotContain(away, Listed(db, other));
        Assert.Equal(Listed(db, other), Known(db, other));
    }

    /// <summary>The page the values of row <paramref name="id"/> were moved to.</summary>
    private static int RowsMovedTo(Database db, Table table, int id)
    {
        var at = Where(table, id);
        byte[] page = db.File.ReadPage(at.PageNumber);
        int start = ByteUtil.GetUShort(page, db.File.Format.OffsetDataRowTable + at.RowNumber * JetFormat.SizeRowEntry) & JetFormat.RowOffsetMask;
        Assert.Equal(JetFormat.OverflowRowFlag, Slot(db, at.PageNumber, at.RowNumber).Flags);
        return ByteUtil.Get3ByteInt(page, start + 1);
    }

    [Fact]
    public void A_page_an_update_leaves_with_no_row_is_begun_again_and_takes_the_next_row()
    {
        // Access gives such a page back to the file and takes it again, its slots begun anew, for the next page it
        // needs; this library keeps it for the table, empty and listed.
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 18; id++) Put(table, id, 200);
        Set(table, 3, ("B", 255), ("C", 255));
        Set(table, 4, ("B", 255), ("C", 255));
        int away = RowsMovedTo(db, table, 3);
        Assert.Equal(away, RowsMovedTo(db, table, 4));

        Set(table, 3, ("B", 0), ("C", 0));
        Assert.Equal(2, Slots(db, away));                    // row 4's values are still there
        Set(table, 4, ("B", 0), ("C", 0));
        Assert.Equal(0, Slots(db, away));
        Assert.Equal(4082, Free(db, away));
        Assert.Contains(away, Owned(db, table));
        Assert.Contains(away, Listed(db, table));

        Put(table, 19, 200);
        Assert.Equal(new RowId(away, 0), Where(table, 19));
        Assert.Equal(19, table.Rows.Count());
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 19);
    }

    [Fact]
    public void A_row_whose_values_move_on_from_a_page_they_leave_empty_does_not_go_back_to_it()
    {
        // As Access moves values on: the new place is found before the old one is left, so it is a new page, and the
        // one left - 250 deleted slots and nothing else, too little room for the row - is begun again.
        using var db = Database.Create(_path);
        var table = db.CreateTable("W", new[] { "Id", "A", "B", "C", "D", "E", "F", "G" }
            .Select(name => name == "Id" ? new ColumnDefinition(name, DataType.Int32) : new ColumnDefinition(name, DataType.Text) { MaxLength = 255 })
            .ToList(), "Id");
        Row Of(int id, int a, int other = 0) => new Row
        {
            ["Id"] = id, ["A"] = Greek(a), ["B"] = Greek(other), ["C"] = Greek(other), ["D"] = Greek(other),
            ["E"] = Greek(other), ["F"] = Greek(other), ["G"] = Greek(other),
        };
        for (int id = 1; id <= 10; id++) table.Insert(Of(id, 200));
        for (int i = 0; i < 249; i++)
        {
            table.Insert(Of(100 + i, 1));
            table.Delete(table.FindRowByPrimaryKey(100 + i)!);
        }
        table.Delete(table.FindRowByPrimaryKey(10)!);
        int second = Owned(db, table)[1];
        Assert.Equal(250, Slots(db, second));

        var row = table.FindRowByPrimaryKey(3)!;
        foreach (var column in new[] { "B", "C", "D", "E" }) row[column] = Greek(255);
        table.Update(row);
        Assert.Equal(second, RowsMovedTo(db, table, 3));

        row = table.FindRowByPrimaryKey(3)!;
        foreach (var column in new[] { "A", "F", "G" }) row[column] = Greek(255);
        table.Update(row);
        int third = RowsMovedTo(db, table, 3);
        Assert.NotEqual(second, third);
        Assert.Equal(0, Slots(db, second));
        Assert.Contains(second, Listed(db, table));
        Assert.DoesNotContain(third, Listed(db, table));   // 485 bytes left: no room for another row of 3,595
        Assert.Equal(Greek(255), table.FindRowByPrimaryKey(3)!["G"]);
    }

    // ── A row updated back and forth ─────────────────────────────────────────

    /// <summary>
    /// The issue's probe: a row grows past its page, grows again and comes home, again and again, and another row now
    /// and then grows past its page too, or comes home. Access's file gained a page in 3,000 such cycles; this
    /// library's gained a page every 230 or so, each holding nothing but deleted slots.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void A_row_updated_back_and_forth_grows_the_file_by_a_page_at_most(int cycles)
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 30; id++) Put(table, id, 200);
        int pages = db.File.PageCount;

        for (int i = 1; i <= cycles; i++)
        {
            Set(table, 3, ("B", 255), ("C", 255));
            Set(table, 3, ("D", 100));
            Set(table, 3, ("B", 0), ("C", 0), ("D", 0));
            if (i % 10 == 0)
            {
                int other = i % 20 == 10 ? 255 : 0;
                Set(table, 17, ("B", other), ("C", other), ("D", other));
            }
        }

        Assert.InRange(db.File.PageCount - pages, 0, 1);
        Assert.Equal(0, PagesOfDeletedSlotsOnly(db, table));
        Assert.Equal(30, table.Rows.Count());
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 30);
    }

    /// <summary>
    /// A Memo or OLE value made longer - a chain of two chunks - and shorter - one chunk, then in the row - again and
    /// again, a session to each change: a page of its column that a change emptied is used again by a chain written in
    /// a later session. Earlier, a session whose first long value was a chain added two pages; Access's file gains a
    /// few pages, whatever the sessions.
    /// </summary>
    [Theory]
    [InlineData(DataType.Memo)]
    [InlineData(DataType.OleObject)]
    public void A_long_value_made_longer_and_shorter_grows_the_file_by_a_page_at_most(DataType type)
    {
        object Value(int bytes) => type == DataType.Memo
            ? new string('Ω', bytes / 2)
            : Enumerable.Range(0, bytes).Select(i => (byte)(1 + i % 250)).ToArray();
        using (var db = Database.Create(_path))
        {
            var table = db.CreateTable("L", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("M", type) }, "Id");
            for (int id = 1; id <= 10; id++) table.Insert(new Row { ["Id"] = id, ["M"] = Value(3_000) });
        }
        long pages = new FileInfo(_path).Length / 4096;

        for (int i = 0; i < 100; i++)
            foreach (int bytes in new[] { 6_000, 3_000, 20 })
            {
                using var db = Database.Open(_path);
                var table = db.GetTable("L");
                var row = table.FindRowByPrimaryKey(5)!;
                row["M"] = Value(bytes);
                table.Update(row);
            }

        Assert.InRange(new FileInfo(_path).Length / 4096 - pages, 0, 1);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("L");
            Assert.Equal(Value(20), table.FindRowByPrimaryKey(5)!["M"]);
            Assert.Equal(Value(3_000), table.FindRowByPrimaryKey(6)!["M"]);
        }
    }

    // ── What finding room costs ──────────────────────────────────────────────

    [Fact]
    public void An_insert_reads_as_few_pages_however_many_pages_the_free_space_map_lists()
    {
        // A table with no index, so that what is counted is finding a page and writing the row there.
        (double Reads, int Listed) PerInsert(int rows)
        {
            string path = Path.Combine(Path.GetTempPath(), $"room_cost_{Guid.NewGuid():N}.mdb");
            try
            {
                using (var db = Database.Create(path))
                {
                    var table = db.CreateTable("P", new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
                    });
                    table.InsertRange(Enumerable.Range(1, rows).Select(id => new Row { ["Id"] = id, ["A"] = Greek(200) }));
                }
                using (var db = Database.Open(path))
                {
                    // A row deleted from every page, each of which then has room for one: all of them listed.
                    var table = db.GetTable("P");
                    foreach (var row in table.Rows.Where(r => (int)r["Id"]! % 9 == 1).ToList()) table.Delete(row);
                }
                using (var db = Database.Open(path))
                {
                    // Each insert fills a listed page and the next takes it off the map: fewer inserts than pages listed.
                    var table = db.GetTable("P");
                    int listed = Listed(db, table).Count;
                    table.Insert(new Row { ["Id"] = rows + 1, ["A"] = Greek(200) });   // the first reads the map, once
                    long before = db.File.ReadCount;
                    for (int id = rows + 2; id <= rows + 21; id++) table.Insert(new Row { ["Id"] = id, ["A"] = Greek(200) });
                    return ((db.File.ReadCount - before) / 20.0, listed);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        var small = PerInsert(270);
        var middle = PerInsert(15_300);
        var large = PerInsert(27_000);
        Assert.Equal(30, small.Listed);
        Assert.Equal(1_700, middle.Listed);
        Assert.Equal(3_000, large.Listed);
        // A map of more pages than one inline window holds is a reference map: taking a page out of it reads the map
        // page that holds the page's bit as well, and no more however many pages it lists.
        Assert.True(large.Reads == middle.Reads,
            $"an insert read {large.Reads} pages into a table with 3,000 pages listed and {middle.Reads} into one with 1,700");
        Assert.True(middle.Reads <= small.Reads + 1,
            $"an insert read {middle.Reads} pages into a table with 1,700 pages listed and {small.Reads} into one with 30");
    }

    // ── Kept for undoing ─────────────────────────────────────────────────────

    [Fact]
    public void A_change_that_fails_while_it_is_written_leaves_the_map_and_what_the_session_knows_of_it_as_they_were()
    {
        using var db = Database.Create(_path);
        var table = Wide(db);
        for (int id = 1; id <= 30; id++) Put(table, id, 200);

        var changes = new (string What, Action Change)[]
        {
            ("a delete that lists its page", () => Delete(table, 5)),
            ("an insert into the room it left", () => Put(table, 40, 200)),
            ("an insert onto the last page", () => Put(table, 41, 255, 255, 255, 255)),
            ("an insert that takes a page off the map and adds one", () => Put(table, 42, 255, 255, 255, 255)),
            ("a row grown past its page", () => Set(table, 12, ("B", 255), ("C", 255))),
            ("the row come home, the page it left begun again", () => Set(table, 12, ("B", 0), ("C", 0))),
            ("another row grown past its page", () => Set(table, 13, ("B", 255), ("C", 255))),
            ("a delete of a row whose values were moved", () => Delete(table, 13)),
        };
        foreach (var (what, change) in changes)
        {
            Known(db, table);   // what the session knows of the map is read before the change, so it is what is tested
            for (int writes = 0; ; writes++)
            {
                var before = IndexChecks.AllPages(db);
                var known  = Known(db, table);
                db.File.WritesBeforeFailure = writes;
                var thrown = Record.Exception(change);
                db.File.WritesBeforeFailure = null;
                if (thrown is null) break;
                Assert.True(thrown is IOException, $"{what}: {thrown}");
                IndexChecks.AssertUnchanged(before, db);
                Assert.Equal(known, Known(db, table));
                Assert.True(writes < 200, $"{what} never got written");
            }
            Assert.Equal(Listed(db, table), Known(db, table));
        }
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 31);
    }

    // ── A table with no free-space map ───────────────────────────────────────

    [Fact]
    public void A_table_whose_definition_names_no_free_space_map_takes_rows_on_its_last_page()
    {
        int tdef;
        using (var db = Database.Create(_path))
        {
            var table = Wide(db);
            Put(table, 1, 200);
            tdef = table.Definition.TdefPageNumber;
        }
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = (long)tdef * 4096 + JetFormat.Jet4.TdefOffsetFreePage;
            stream.Write(new byte[3], 0, 3);   // the free-space map's page: none
        }
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            Assert.Null(db.File.ListedDataPages.Of(table.Definition));
            for (int id = 2; id <= 12; id++) Put(table, id, 200);
            Assert.Equal(2, Owned(db, table).Count);
            Assert.Equal(12, table.Rows.Count());
        }
    }
}
