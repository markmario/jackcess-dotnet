using System.Diagnostics;
using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Where a long value finds room, and what finding it costs. A column of notes comes to have a page with some room
/// left for nearly every page of notes written, each with less than the next note needs; walked for every value,
/// they made an insert cost in proportion to the pages already written (64,000 notes of 1,500 characters took
/// 19.5 seconds, the last of them 478 microseconds each, where 16,000 took 2). A page is found by its room now
/// (<see cref="FreedLvalPages.TryFindRoom"/>), and <see cref="FreedLvalPages.CandidatesVisited"/> counts the pages
/// a write looked at: these hold it flat as a table grows.
/// </summary>
public sealed class LongValueRoomTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"room_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Table Notes(Database db)
        => db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) });

    // Lengths in characters, the same every run. A note of 33 to 2,038 characters is one chunk; a longer one a chain.
    private static int Length(string workload, int i)
    {
        int spread = (int)((uint)(i * 2654435761u) >> 8);
        return workload switch
        {
            "one length" => 1_500,   // in Greek, 3,000 bytes: each leaves its page 1,080 bytes no other can use: the case that was slow
            "one piece" => 40 + spread % 1_990,
            "chains"    => 2_100 + spread % 4_000,
            _           => 40 + spread % 6_000,
        };
    }

    // A third of the notes Greek, which Access writes two bytes a character, the rest Latin, which it compresses to one up to
    // 1,908 characters (#87); every note Greek where its length in bytes must be the same.
    private static string Note(int i, int length, bool greek = false)
        => new((char)(greek || i % 3 == 0 ? 0x3A9 + i % 20 : 'a' + i % 26), length);

    [Theory]
    [InlineData("one length")]
    [InlineData("one piece")]
    [InlineData("chains")]
    [InlineData("mixed")]
    public void An_insert_looks_at_no_more_pages_for_room_in_a_table_of_50_000_rows_than_in_one_of_1_000(string workload)
    {
        const int Rows = 50_000, Window = 1_000;
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = Notes(db);
        var notes = db.File.FreedLvalPages;

        long visitedEarly = 0, visitedLate = 0, readsEarly = 0, readsLate = 0, most = 0;
        var clock = new Stopwatch();
        TimeSpan early = default, late = default;
        for (int i = 1; i <= Rows; i++)
        {
            long visited = notes.CandidatesVisited, reads = db.File.ReadCount;
            clock.Restart();
            table.Insert(new Row { ["Id"] = i, ["Notes"] = Note(i, Length(workload, i), greek: workload == "one length") });
            clock.Stop();
            visited = notes.CandidatesVisited - visited;
            reads   = db.File.ReadCount - reads;
            most = Math.Max(most, visited);
            if (i <= Window)        { visitedEarly += visited; readsEarly += reads; early += clock.Elapsed; }
            if (i > Rows - Window)  { visitedLate  += visited; readsLate  += reads; late  += clock.Elapsed; }
        }

        // One page at most is looked at for a value, and it is the one the value goes to: what is noted of a page
        // is what the page has, so none is looked at and passed over.
        Assert.True(most <= 1, $"one insert looked at {most} pages for room");
        Assert.True(visitedLate <= Window, $"the last {Window} inserts looked at {visitedLate} pages for room (the first {Window}: {visitedEarly})");
        if (workload is "chains" or "one length") Assert.Equal(0, visitedLate);

        // Pages read: the same handful, whatever the table holds. Each usage map that has grown to map pages of its
        // own - the table's, the column's two - costs a read more, so four more an insert are allowed, and no more.
        Assert.True(readsLate <= readsEarly + 4 * Window,
            $"the last {Window} inserts read {readsLate} pages, the first {Window} read {readsEarly}");

        // The time is shown, not asserted: the counts above pin the cost on any machine, and a busy one can stall
        // for longer than a window of inserts takes.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"the last {Window} inserts took {late.TotalMilliseconds:F0} ms, the first {Window} took {early.TotalMilliseconds:F0} ms");
    }

    // ── What the free-space map lists ────────────────────────────────────────

    private static (HashSet<int> Owned, HashSet<int> Free) Maps(Database db, Table table, string column = "Notes")
    {
        var map = table.Definition.LvalColumnUmapPages[column];
        return (new HashSet<int>(UsageMap.GetOwnedPages(db.File.ReadPage(map.OwnedPage), map.OwnedRow, db.File.Format, db.File)),
                new HashSet<int>(UsageMap.GetOwnedPages(db.File.ReadPage(map.FreePage), map.FreeRow, db.File.Format, db.File)));
    }

    [Fact]
    public void The_free_space_map_lists_the_pages_Access_would_and_what_is_noted_is_what_it_lists()
    {
        // ACE lists a page in the column's free-space map when it is left with 258 bytes or more, and not with
        // 257 or fewer; a page that holds a chunk of a chain it never lists. A new page used to go into the map
        // before anything was written to it, and stay with 64 bytes.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = Notes(db);
        for (int i = 1; i <= 600; i++)
            table.Insert(new Row { ["Id"] = i, ["Notes"] = new string((char)('a' + i % 26), Length("mixed", i)) });

        var (owned, free) = Maps(db, table);
        Assert.NotEmpty(free);
        Assert.Subset(owned, free);
        var format = db.File.Format;
        foreach (int number in owned)
        {
            byte[] page = db.File.ReadPage(number);
            int room = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace), rows = ByteUtil.GetShort(page, format.OffsetDataNumRows);
            bool holdsAChunkOfAChain = false;
            for (int r = 0; r < rows; r++)
            {
                byte[] row = JetPages.RowBytes(db.File, number, r);
                holdsAChunkOfAChain |= row.Length >= 4 && (row[1] | row[2] | row[3]) == 0   // the last chunk: no next page
                                    || row.Length > format.PageSize - 24;                      // any other: a page full
            }
            if (free.Contains(number))
            {
                Assert.True(room >= LvalWriter.MinListedFreeSpace, $"page {number} is listed with {room} bytes free");
                Assert.False(holdsAChunkOfAChain, $"page {number} is listed and holds a chunk of a chain");
            }
            else if (!holdsAChunkOfAChain)
            {
                Assert.True(room < LvalWriter.MinListedFreeSpace, $"page {number} has {room} bytes free and is not listed");
            }
        }

        // What the session has noted is what the map lists: a later session, which has only the map, places the
        // next value where this one would.
        var map = table.Definition.LvalColumnUmapPages["Notes"];
        Assert.Equal(free.OrderBy(p => p), db.File.FreedLvalPages.Of(map.OwnedPage, map.OwnedRow).Select(p => p.Key));
    }

    [Fact]
    public void Notes_written_in_one_session_or_in_several_make_the_same_file()
    {
        string other = _path + ".sessions";
        try
        {
            using (var db = Database.Create(_path, FileFormat.Access2000))
                Notes(db);
            File.Copy(_path, other);   // the same empty table: a new file has a date of its own

            using (var db = Database.Open(_path))
            {
                var table = db.GetTable("T");
                for (int i = 1; i <= 900; i++)
                    table.Insert(new Row { ["Id"] = i, ["Notes"] = Note(i, Length("mixed", i)) });
            }

            for (int from = 1; from <= 900; from += 150)
            {
                using var db = Database.Open(other);
                var table = db.GetTable("T");
                for (int i = from; i < from + 150; i++)
                    table.Insert(new Row { ["Id"] = i, ["Notes"] = Note(i, Length("mixed", i)) });
            }

            Assert.Equal(File.ReadAllBytes(_path), File.ReadAllBytes(other));
        }
        finally
        {
            if (File.Exists(other)) File.Delete(other);
        }
    }

    // ── A file in the earlier placement ──────────────────────────────────────

    private static List<int> PagesOf(Database db, Table table, Column blob, int id)
    {
        int pointer = new IndexWriter(db.File, new PageAllocator(db.File)).FindRowByPrimaryKey(table.Definition, id)!.Value;
        byte[] row = JetPages.RowBytes(db.File, RowPointer.Page(pointer), RowPointer.Row(pointer));
        byte[] reference = new RowDecoder(db.File.Format, table.Columns).RawVariableValue(row, blob)!;
        int flags = ByteUtil.GetInt(reference, 0) & unchecked((int)0xC0000000);
        var pages = new List<int>();
        if (flags == LvalWriter.LvalTypeThisPage) return pages;
        int at = reference[4], page = ByteUtil.Get3ByteInt(reference, 5);
        while (page != 0)
        {
            pages.Add(page);
            if (flags == LvalWriter.LvalTypeOtherPage) break;
            byte[] chunk = JetPages.RowBytes(db.File, page, at);
            at   = chunk[0];
            page = ByteUtil.Get3ByteInt(chunk, 1);
        }
        return pages;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        bytes[1] = 1;   // not the start of a chain's last chunk
        return bytes;
    }

    [Fact]
    public void No_value_is_put_on_a_page_that_holds_the_end_of_a_chain_in_a_file_an_earlier_release_wrote()
    {
        // The earlier placement put the last chunks of two chains on one page, and listed it as having room. With
        // one chain deleted the page has more, and a value put there would be lost when Access freed the other
        // chain: it gives back every page of a chain it frees.
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.ShareLongValuePages = true;
            var made = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Blob", DataType.OleObject) }, primaryKey: "Id");
            made.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(5_000, 1) });
            made.Insert(new Row { ["Id"] = 2, ["Blob"] = Bytes(5_000, 2) });
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            var blob = table.Columns.Single(c => c.Name == "Blob");
            var first = PagesOf(db, table, blob, 1);
            var second = PagesOf(db, table, blob, 2);
            int shared = Assert.Single(first.Intersect(second));   // as the earlier release left them

            // The second chain's chunk is the page's last row, so freeing it gives the page room to be noted for.
            table.DeleteRow("Id", 2);
            for (int i = 10; i < 60; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(300 + i, i) });

            for (int i = 10; i < 60; i++)
                Assert.Empty(PagesOf(db, table, blob, i).Intersect(first));
            Assert.DoesNotContain(shared, Maps(db, table, "Blob").Free);
            Assert.Equal(Bytes(5_000, 1), table.FindRowByPrimaryKey(1)!["Blob"]);
            for (int i = 10; i < 60; i++)
                Assert.Equal(Bytes(300 + i, i), table.FindRowByPrimaryKey(i)!["Blob"]);
        }
    }
}
