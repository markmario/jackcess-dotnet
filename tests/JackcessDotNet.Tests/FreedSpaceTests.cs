using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Space a deleted or updated long value freed, used again by later values of the same column
/// (<see cref="FreedLvalPages"/>): what takes it, what is never written to, and what a write reads to find it.
/// </summary>
public sealed class FreedSpaceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"freed_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static IReadOnlyList<Column> BlobColumns(params string[] blobs)
    {
        var columns = new List<Column> { new ColumnBuilder("Id", DataType.Long).Build() };
        foreach (var name in blobs.Length == 0 ? new[] { "Blob" } : blobs)
            columns.Add(new ColumnBuilder(name, DataType.Ole).Build());
        return columns;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static List<int> LvalPages(Database db, Table table, string column)
    {
        var lval = table.Definition.LvalColumnUmapPages[column];
        return UsageMap.GetOwnedPages(db.File.ReadPage(lval.OwnedPage), lval.OwnedRow, db.File.Format, db.File);
    }

    private static int RowsOn(Database db, int page) => ByteUtil.GetShort(db.File.ReadPage(page), db.File.Format.OffsetDataNumRows);

    [Fact]
    public void Every_chunk_of_a_long_value_has_a_page_to_itself_and_one_chunk_values_share_theirs()
    {
        // As Access places them: it gives every page of a chain back to the file when it frees the value, so nothing
        // else may be on one. Ten 2,000-byte values go two to a page; deleting every second leaves five pages half
        // free. A 5,000-byte value is two chunks and takes two pages of its own, not the room on those; a 1,500-byte
        // value, one chunk, takes that room.
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", BlobColumns());
            for (int i = 1; i <= 10; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(2_000, i) });
            for (int i = 2; i <= 10; i += 2)
                table.DeleteRow("Id", i);
            var before = LvalPages(db, table, "Blob");

            for (int i = 11; i <= 15; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(5_000, i) });
            var chains = LvalPages(db, table, "Blob").Except(before).ToList();
            Assert.Equal(10, chains.Count);
            Assert.All(chains, page => Assert.Equal(1, RowsOn(db, page)));

            for (int i = 16; i <= 20; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(1_500, i) });
            Assert.Equal(before.Count + 10, LvalPages(db, table, "Blob").Count);   // the one-chunk values took the freed room
            Assert.All(chains, page => Assert.Equal(1, RowsOn(db, page)));         // and none of them a chain's page

            // A chain's pages, emptied by a delete, are used again by a chain.
            table.DeleteRow("Id", 11);
            table.Insert(new Row { ["Id"] = 21, ["Blob"] = Bytes(8_000, 21) });
            Assert.Equal(before.Count + 10, LvalPages(db, table, "Blob").Count);
        }

        using (var db = Database.Open(_path))
        {
            var rows = db.GetTable("T").Rows.ToDictionary(r => (int)r["Id"]!);
            Assert.Equal(15, rows.Count);
            for (int i = 1; i <= 9; i += 2)
                Assert.Equal(Bytes(2_000, i), rows[i]["Blob"]);
            for (int i = 12; i <= 15; i++)
                Assert.Equal(Bytes(5_000, i), rows[i]["Blob"]);
            for (int i = 16; i <= 20; i++)
                Assert.Equal(Bytes(1_500, i), rows[i]["Blob"]);
            Assert.Equal(Bytes(8_000, 21), rows[21]["Blob"]);
        }
    }

    [Fact]
    public void Sharing_pages_the_last_chunk_of_a_long_value_reuses_a_page_a_delete_left_partly_free()
    {
        // The placement of earlier releases, kept for files that must stay as they were (Database.ShareLongValuePages).
        // Ten 2,000-byte values go two to a page; deleting every second leaves five pages with 2,080 bytes free. A
        // 5,000-byte value is a full chunk, which needs a page of its own, and a 934-byte last one, which fits a freed
        // page. The freed pages were walked once, for the full chunk, and not again for the last.
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.ShareLongValuePages = true;
            var table = db.CreateTable("T", BlobColumns());
            for (int i = 1; i <= 10; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(2_000, i) });
            for (int i = 2; i <= 10; i += 2)
                table.DeleteRow("Id", i);
            int before = LvalPages(db, table, "Blob").Count;

            for (int i = 11; i <= 15; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(5_000, i) });

            Assert.Equal(before + 5, LvalPages(db, table, "Blob").Count);   // a new page for each full chunk, no more
        }

        using (var db = Database.Open(_path))
        {
            var rows = db.GetTable("T").Rows.ToDictionary(r => (int)r["Id"]!);
            Assert.Equal(10, rows.Count);
            for (int i = 1; i <= 9; i += 2)
                Assert.Equal(Bytes(2_000, i), rows[i]["Blob"]);
            for (int i = 11; i <= 15; i++)
                Assert.Equal(Bytes(5_000, i), rows[i]["Blob"]);
        }
    }

    [Fact]
    public void Adding_to_a_file_whose_long_values_share_pages_leaves_them_as_they_are_and_shares_no_more()
    {
        // A file an earlier release wrote: the last chunks of its chains two to a page. Added to as Access places
        // long values, the old values stay where they are - still sharing, still read - and no new chunk goes onto
        // their pages: each new chain has pages of its own.
        List<int> old;
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.ShareLongValuePages = true;
            var table = db.CreateTable("T", BlobColumns());
            for (int i = 1; i <= 10; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(5_000, i) });
            old = LvalPages(db, table, "Blob");
            Assert.Equal(15, old.Count);                                     // ten full chunks, and their last chunks two to a page
            Assert.Equal(5, old.Count(page => RowsOn(db, page) == 2));
        }
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            for (int i = 11; i <= 20; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(5_000, i) });
            for (int i = 21; i <= 25; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(1_500, i) });  // one chunk each

            var added = LvalPages(db, table, "Blob").Except(old).ToList();
            Assert.Equal(20 + 3, added.Count);                               // two pages a chain; the five one-chunk values two to a page
            foreach (int page in old)
                Assert.True(db.File.ReadPage(page).AsSpan().SequenceEqual(before.AsSpan(page * 4096, 4096)), $"page {page}, of the earlier values, was written to");

            var rows = table.Rows.ToDictionary(r => (int)r["Id"]!);
            Assert.Equal(25, rows.Count);
            for (int i = 1; i <= 20; i++)
                Assert.Equal(Bytes(5_000, i), rows[i]["Blob"]);
            for (int i = 21; i <= 25; i++)
                Assert.Equal(Bytes(1_500, i), rows[i]["Blob"]);
        }
    }

    [Fact]
    public void A_column_s_freed_space_is_its_own_in_a_table_with_two_long_value_columns()
    {
        // Freed space is kept by the column's usage map, not by table: deleting a 1 MB value from column A neither slows
        // writes to column B nor gives B any of A's pages.
        (long[] reads, bool shared) WritesToB(bool deleteA)
        {
            string path = Path.Combine(Path.GetTempPath(), $"freed_{Guid.NewGuid():N}.mdb");
            try
            {
                var reads = new long[3];
                bool shared;
                using (var db = Database.Create(path, FileFormat.Access2000))
                {
                    var table = db.CreateTable("T", BlobColumns("A", "B"));
                    table.Insert(new Row { ["Id"] = 1, ["A"] = Bytes(1_000_000, 1) });
                    var aPages = LvalPages(db, table, "A");
                    if (deleteA) table.DeleteRow("Id", 1);
                    for (int i = 0; i < reads.Length; i++)
                    {
                        long before = db.File.ReadCount;
                        table.Insert(new Row { ["Id"] = 2 + i, ["B"] = Bytes(3_000, 2 + i) });
                        reads[i] = db.File.ReadCount - before;
                    }
                    shared = LvalPages(db, table, "B").Intersect(aPages).Any();
                }

                using (var db = Database.Open(path))
                {
                    var rows = db.GetTable("T").Rows.ToDictionary(r => (int)r["Id"]!);
                    Assert.Equal(deleteA ? 3 : 4, rows.Count);
                    if (!deleteA)
                        Assert.Equal(Bytes(1_000_000, 1), rows[1]["A"]);
                    for (int i = 0; i < reads.Length; i++)
                        Assert.Equal(Bytes(3_000, 2 + i), rows[2 + i]["B"]);
                }
                return (reads, shared);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        var kept = WritesToB(deleteA: false);
        var deleted = WritesToB(deleteA: true);

        Assert.Equal(kept.reads, deleted.reads);
        Assert.False(deleted.shared, "column B took a page column A had freed");
    }

    [Fact]
    public void A_freed_page_that_is_not_on_the_column_s_map_is_never_written_to()
    {
        // No delete records another column's page as this column's, but the record is checked against the column's
        // usage map before anything goes onto the page.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var mine = db.CreateTable("Mine", BlobColumns());
        var other = db.CreateTable("Other", BlobColumns());
        other.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(2_000, 1) });
        int othersPage = LvalPages(db, other, "Blob").Single();
        var lval = mine.Definition.LvalColumnUmapPages["Blob"];
        db.File.FreedLvalPages.Set(lval.OwnedPage, lval.OwnedRow, othersPage, 2_000);

        mine.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(1_500, 2) });

        Assert.Equal(1, RowsOn(db, othersPage));
        Assert.DoesNotContain(othersPage, LvalPages(db, mine, "Blob"));
        Assert.Equal(Bytes(2_000, 1), other.Rows.Single()["Blob"]);
        Assert.Equal(Bytes(1_500, 2), mine.Rows.Single()["Blob"]);
        Assert.DoesNotContain(db.File.FreedLvalPages.Of(lval.OwnedPage, lval.OwnedRow), r => r.Key == othersPage);   // and the record is dropped
    }

    [Fact]
    public void A_freed_page_that_is_not_a_long_value_page_is_never_written_to()
    {
        // Another table's data page, listed in the column's map below the column's own last page and recorded as freed.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var mine = db.CreateTable("Mine", BlobColumns());
        var other = db.CreateTable("Other", BlobColumns());
        other.Insert(new Row { ["Id"] = 1 });
        int othersDataPage = UsageMap.GetLastPage(db.File, other.Definition.UmapPageNumber, other.Definition.OwnedPagesRow);
        mine.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(3_500, 1) });
        var lval = mine.Definition.LvalColumnUmapPages["Blob"];
        UsageMap.AddPage(db.File, new PageAllocator(db.File), lval.OwnedPage, lval.OwnedRow, othersDataPage);
        db.File.FreedLvalPages.Set(lval.OwnedPage, lval.OwnedRow, othersDataPage, 3_000);

        mine.Insert(new Row { ["Id"] = 2, ["Blob"] = Bytes(1_500, 2) });

        Assert.Equal(1, RowsOn(db, othersDataPage));
        Assert.Single(other.Rows);
        var rows = mine.Rows.ToDictionary(r => (int)r["Id"]!);
        Assert.Equal(Bytes(3_500, 1), rows[1]["Blob"]);
        Assert.Equal(Bytes(1_500, 2), rows[2]["Blob"]);
    }

    [Fact]
    public void A_freed_page_with_less_room_than_recorded_is_read_before_anything_goes_onto_it()
    {
        // A record says 4,000 bytes are free on a page that is full: the page is read first, and the value goes elsewhere.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", BlobColumns());
        table.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(4_000, 1) });
        int full = LvalPages(db, table, "Blob").Single();
        var lval = table.Definition.LvalColumnUmapPages["Blob"];
        db.File.FreedLvalPages.Set(lval.OwnedPage, lval.OwnedRow, full, 4_000);

        table.Insert(new Row { ["Id"] = 2, ["Blob"] = Bytes(2_000, 2) });

        Assert.Equal(1, RowsOn(db, full));
        var rows = table.Rows.ToDictionary(r => (int)r["Id"]!);
        Assert.Equal(Bytes(4_000, 1), rows[1]["Blob"]);
        Assert.Equal(Bytes(2_000, 2), rows[2]["Blob"]);
    }

    [Fact]
    public void Room_left_on_a_freed_page_is_noted_however_the_value_reached_it()
    {
        // A freed page passed over for a full chunk and then taken, as the column's last page, by the last chunk: its
        // record must say what is left, or a later write reads the page for room it does not have.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", BlobColumns());
        table.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(2_000, 1) });
        table.Insert(new Row { ["Id"] = 2, ["Blob"] = Bytes(2_000, 2) });
        table.DeleteRow("Id", 2);   // the column's only page, and its last, now half free
        var lval = table.Definition.LvalColumnUmapPages["Blob"];
        int page = LvalPages(db, table, "Blob").Single();

        table.Insert(new Row { ["Id"] = 3, ["Blob"] = Bytes(5_000, 3) });

        int actual = ByteUtil.GetShort(db.File.ReadPage(page), JetFormat.OffsetDataFreeSpace);
        var record = Assert.Single(db.File.FreedLvalPages.Of(lval.OwnedPage, lval.OwnedRow), r => r.Key == page);
        Assert.Equal(actual, record.Value);
        Assert.Equal(new[] { 1, 3 }, table.Rows.Select(r => (int)r["Id"]!).OrderBy(i => i));
    }

    [Fact]
    public void A_freed_page_holding_as_many_rows_as_a_page_can_is_forgotten_whatever_its_room()
    {
        // Access packs small rows onto long-value pages; one with 255 takes no more, however much room it has. Kept as
        // having room, it was read again by every write its room fitted.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", BlobColumns());
        var lval = table.Definition.LvalColumnUmapPages["Blob"];
        var allocator = new PageAllocator(db.File);
        int page = allocator.AllocateLvalPage();
        UsageMap.AddPage(db.File, allocator, lval.OwnedPage, lval.OwnedRow, page);
        var format = db.File.Format;
        byte[] bytes = db.File.ReadPage(page);
        for (int i = 0; i < DataPageWriter.MaxRowsPerPage; i++)   // 255 one-byte rows from the end of the page
            ByteUtil.PutShort(bytes, format.OffsetDataRowTable + i * JetFormat.SizeRowEntry, (short)(format.PageSize - i - 1));
        ByteUtil.PutShort(bytes, format.OffsetDataNumRows, (short)DataPageWriter.MaxRowsPerPage);
        int room = format.PageSize - format.OffsetDataRowTable - DataPageWriter.MaxRowsPerPage * (JetFormat.SizeRowEntry + 1);
        ByteUtil.PutShort(bytes, JetFormat.OffsetDataFreeSpace, (short)room);
        db.File.WritePage(page, bytes);
        db.File.FreedLvalPages.Set(lval.OwnedPage, lval.OwnedRow, page, room);

        table.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(1_500, 1) });

        Assert.Equal(DataPageWriter.MaxRowsPerPage, RowsOn(db, page));
        Assert.DoesNotContain(db.File.FreedLvalPages.Of(lval.OwnedPage, lval.OwnedRow), r => r.Key == page);
        Assert.Equal(Bytes(1_500, 1), table.Rows.Single()["Blob"]);
    }

    // The corpus's one file of long values Access wrote (blobV2007.accdb) keeps them in a table with an index on
    // an attachment (Complex) column, whose keys the engine does not write. Until the engine kept every index it
    // deleted a row there and left the row's entry in that index; now it refuses, and the file stays as Access
    // wrote it. Space freed and used again in a file the engine wrote is the rest of this class.
    [Fact]
    public void A_table_Access_wrote_with_an_index_the_engine_cannot_keep_is_left_as_it_was()
    {
        string? source = TestCorpus.File("V2007", "blobV2007.accdb");
        if (source is null) return;   // the corpus is optional locally; CI always has it
        File.Copy(source, _path);
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var (tableName, blob, key, keyValue) = LargestLongValue(db);
            var table = db.GetTable(tableName);
            Assert.Contains(table.Indexes, ix => ix.Columns.Any(c => c.Column.DataType == DataType.Complex));

            var refused = Assert.Throws<NotSupportedException>(() => table.DeleteRow(key, keyValue));
            Assert.Contains("cannot be kept", refused.Message);
            Assert.Throws<NotSupportedException>(() => table.Insert(new Row { [key] = 1_000_000, [blob] = Bytes(5_000, 7) }));
        }

        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    /// <summary>The table, OLE column and key of the longest OLE value in the file, keyed by a column whose values are unique.</summary>
    private static (string table, string blob, string key, object keyValue) LargestLongValue(Database db)
    {
        (string, string, string, object)? best = null;
        int bestLength = 0;
        foreach (var name in db.GetTableNames())
        {
            var table = db.GetTable(name);
            var rows = table.Rows.ToList();
            var key = table.Columns.FirstOrDefault(c =>
                (c.DataType == DataType.Long || c.DataType == DataType.Int || c.DataType == DataType.Text)
                && rows.All(r => r[c.Name] != null) && rows.Select(r => r[c.Name]).Distinct().Count() == rows.Count);
            if (key is null) continue;
            // An OLE column with usage maps of its own: an attachment field's hidden data column has none.
            foreach (var column in table.Columns.Where(c => c.DataType == DataType.Ole && table.Definition.LvalColumnUmapPages.ContainsKey(c.Name)))
            {
                foreach (var row in rows)
                {
                    if (row[column.Name] is byte[] value && value.Length > bestLength)
                    {
                        bestLength = value.Length;
                        best = (name, column.Name, key.Name, row[key.Name]!);
                    }
                }
            }
        }
        Assert.True(best.HasValue && bestLength > 4_096, "the file has no long value that spans pages");
        return best!.Value;
    }
}
