using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The layout DAO's <c>AddNew</c> and <c>Edit</c> on a table-type recordset write a Date/Time Extended value in
/// (#64): the day in fifteen digits, the time of day in 100-nanosecond units, ":7", a zero byte, and four bytes that
/// are whatever DAO's buffer held. DAO reads it as its date, ACE keys it by all 42 bytes, and so does this library.
/// <para>
/// Assets/ace-extended-date-table-type.accdb (tools/ace-extended-date-table-type.ps1) holds rows in it beside rows in
/// the two forms Access's SQL writes, keyed by ACE in indexes made before DAO added the rows, a relationship whose
/// children are copies of their parents' bytes, and a unique index.
/// </para>
/// </summary>
public sealed class ExtendedDateTableTypeTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string Copy(string asset)
    {
        string path = Path.Combine(Path.GetTempPath(), $"extended_table_type_{Guid.NewGuid():N}{Path.GetExtension(asset)}");
        _paths.Add(path);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), path);
        File.SetAttributes(path, FileAttributes.Normal);
        return path;
    }

    private const string TableType = "ace-extended-date-table-type.accdb";

    private static readonly DateTime D0 = new(2020, 1, 2, 3, 4, 5);
    private static readonly DateTime D1 = D0.AddTicks(1234567);

    /// <summary>T's rows as ACE and DAO stored them, by Id (tools/ace-extended-date-table-type.ps1 lists how each was made).</summary>
    private static readonly Dictionary<int, string> Stored = new()
    {
        [1]  = "0000000000000737425:0000000110450000000:7\0",                 // INSERT
        [2]  = "0000000000000737425:0000000000000011045:0\0",                 // UPDATE
        [3]  = "0000000000000737425:0000000000000011045:0\0",                 // AddNew, a dynaset
        [4]  = "000000000737425:0000000110450000000:7\0\0\0®\u0002",     // AddNew, a table-type recordset
        [5]  = "000000000737425:0000000110450000000:7\0\0\0x\0",              // the same, given a Date
        [6]  = "000000000737425:0000000110450000000:7\0\0\0\0\0",             // Edit, a table-type recordset
        [7]  = "000000000737425:0000000110451234567:7\0\0\0\0\0",
        [8]  = "0000000000000737425:0000000110451234567:7\0",                 // INSERT
        [9]  = "000000000737425:0000000000000000000:7\0\0\0" + "7\0",
        [10] = "0000000000000737425:0000000000000000000:7\0",                 // INSERT
        [11] = "000000000737425:0000000110460000000:7\0\0\0" + "7\0",
        [12] = "000000000737424:0000000863990000000:7\0\0\0" + "3\0",
        [13] = "000000000000000:0000000000000000000:7\0\0\0" + "7\0",
        [14] = "000000003652058:0000000863999999999:7\0\0\0" + "3\0",
    };

    /// <summary>The date DAO reads each of T's rows as, by Id.</summary>
    private static DateTime DateOf(int id) => id switch
    {
        <= 6 => D0,
        7 or 8 => D1,
        9 or 10 => new DateTime(2020, 1, 2),
        11 => D0.AddSeconds(1),
        12 => new DateTime(2020, 1, 1, 23, 59, 59),
        13 => DateTime.MinValue,
        _ => DateTime.MaxValue,
    };

    private static bool IsTableTypeLayout(int id) => id is 4 or 5 or 6 or 7 or 9 or 11 or 12 or 13 or 14;

    private static string Latin(byte[] bytes) => new(bytes.Select(b => (char)b).ToArray());

    private static Dictionary<int, int> Pointers(Table table)
    {
        var pointers = new Dictionary<int, int>();
        var cursor = table.CreateCursor();
        while (cursor.MoveNext()) pointers[(int)cursor.Current!["Id"]!] = cursor.CurrentRowPointer;
        return pointers;
    }

    private static Dictionary<int, string> StoredBytes(Database db, string table)
        => JetPages.Rows(db.File, IndexChecks.TdefOf(db, table), forKeys: true)
            .ToDictionary(r => (int)r.Values["Id"]!, r => Latin(Assert.IsType<StoredExtendedDate>(r.Values["K"]).Bytes));

    private static int[] IdsOf(IEnumerable<Row> rows) => rows.Select(r => (int)r["Id"]!).ToArray();

    // ── Reading ─────────────────────────────────────────────────────────────

    // Every row is read as the date DAO read it, and handed to keys as its 42 bytes, the four after the value among
    // them. Rows in the third layout were read as null.
    [Fact]
    public void Every_row_is_read_as_DAO_reads_it_and_handed_to_keys_as_its_bytes()
    {
        using var db = Database.Open(Copy(TableType));
        foreach (var (_, _, values) in JetPages.Rows(db.File, IndexChecks.TdefOf(db, "T"), forKeys: true))
        {
            int id = (int)values["Id"]!;
            var stored = Assert.IsType<StoredExtendedDate>(values["K"]);
            Assert.Equal(Stored[id], Latin(stored.Bytes));
            Assert.Equal(DateOf(id), stored.Date);
            Assert.Equal(id is 1 or 8 or 10, stored.IsFirstForm);
            if (IsTableTypeLayout(id))
                Assert.Equal(Latin(ExtendedDateTimeCodec.TableTypeStart(DateOf(id))), Stored[id].Substring(0, ExtendedDateTimeCodec.TableTypeKnownBytes));
        }
        foreach (var row in db.GetTable("T").Rows)
            Assert.Equal(DateOf((int)row["Id"]!), row["K"]);
        Assert.Equal(new[] { D0, D0, D0, D0 }, db.GetTable("C").Rows.Select(r => (DateTime)r["PD"]!));
    }

    // Every entry ACE made - in KAsc, KDesc and KHow, which were there when DAO added the rows, in P's primary key, C's
    // relationship and U's unique index - is the key of the 42 bytes its row stores, the four after the value among
    // them.
    [Theory]
    [InlineData("T", 4, 14)]
    [InlineData("P", 1, 3)]
    [InlineData("C", 2, 4)]
    [InlineData("U", 2, 1)]
    public void Every_entry_ACE_made_is_the_key_of_the_bytes_its_row_stores(string table, int trees, int rows)
    {
        using var db = Database.Open(Copy(TableType));
        Assert.Equal(trees, IndexChecks.AssertEveryIndexHoldsTheRows(db, table, expectedRows: rows));
    }

    // The order DAO walks KAsc in: the bytes' order. The third layout's rows come after those of the other two from the
    // year 2 on - the day's first digit is four places sooner - and among themselves by their dates, then by the bytes
    // DAO left after them.
    [Fact]
    public void An_index_cursor_gives_the_rows_in_the_order_of_their_bytes()
    {
        using var db = Database.Open(Copy(TableType));
        var t = db.GetTable("T");
        Assert.Equal(new[] { 13, 10, 2, 3, 1, 8, 12, 9, 6, 5, 4, 7, 11, 14 }, IdsOf(t.CreateIndexCursor("KAsc")));
        Assert.Equal(new[] { 14, 11, 7, 4, 5, 6, 9, 12, 8, 1, 2, 3, 10, 13 }, IdsOf(t.CreateIndexCursor("KDesc")));
    }

    // ── Lookups ─────────────────────────────────────────────────────────────

    // A date is found through an index in each layout Access writes: under the key of each of the two forms whole, and
    // among the keys that begin with the third layout's 38 known bytes, in the index's order. A cursor over the table
    // finds the same rows. Through the index ACE's own = finds the first form alone, and without one all of them
    // (measured); this library finds what ACE's scan finds, as it does for the second form (#51). Neither way found a
    // row in the third layout: it was read as null.
    [Fact]
    public void A_date_is_found_through_an_index_in_every_layout_Access_writes()
    {
        using var db = Database.Open(Copy(TableType));
        var t = db.GetTable("T");
        Assert.Equal(new[] { 2, 3, 1, 6, 5, 4 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0)));
        Assert.Equal(new[] { 4, 5, 6, 1, 2, 3 }, IdsOf(t.CreateIndexCursor("KDesc").FindRowsByEntry(D0)));
        Assert.Equal(new[] { 8, 7 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D1)));
        Assert.Equal(new[] { 10, 9 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(new DateTime(2020, 1, 2))));
        Assert.Equal(new[] { 11 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0.AddSeconds(1))));
        Assert.Equal(new[] { 12 }, IdsOf(t.CreateIndexCursor("KDesc").FindRowsByEntry(new DateTime(2020, 1, 1, 23, 59, 59))));
        Assert.Equal(new[] { 13 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(DateTime.MinValue)));
        Assert.Equal(new[] { 14 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(DateTime.MaxValue)));
        Assert.Empty(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0.AddTicks(1)));

        Assert.Equal(2, t.CreateIndexCursor("KAsc").FindRow("K", D0)!["Id"]);
        Assert.Equal(4, t.CreateIndexCursor("KDesc").FindRowByEntry(D0)!["Id"]);
        // With a Text column after the date: the third layout's part is of the date's key alone.
        Assert.Equal(4, t.CreateIndexCursor("KHow").FindRowByEntry(D0, "AddNew, table-type, text")!["Id"]);
        Assert.Equal(7, t.CreateIndexCursor("KHow").FindRowByEntry(D1, "AddNew, table-type")!["Id"]);
        Assert.Null(t.CreateIndexCursor("KHow").FindRowByEntry(D1, "INSERT, table-type"));

        foreach (var date in new[] { D0, D1, new DateTime(2020, 1, 2), DateTime.MinValue, DateTime.MaxValue })
        {
            var cursor = t.CreateCursor();
            var found = new List<int>();
            for (var row = cursor.FindRow("K", date); row is not null; row = cursor.FindNextRow("K", date)) found.Add((int)row["Id"]!);
            Assert.Equal(Enumerable.Range(1, 14).Where(id => DateOf(id) == date), found);
        }
    }

    // P is keyed by D, the date in each layout: p3 by UPDATE, p2 by INSERT, p1 by a table-type AddNew, which is the
    // order of their keys. Each is found through the primary key, trusting the index and verifying it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_row_DAO_s_table_type_recordset_wrote_is_found_by_its_primary_key(bool verify)
    {
        using var db = Database.Open(Copy(TableType));
        db.VerifyIndexes = verify;
        var p = db.GetTable("P");
        Assert.Equal("p3", p.FindRowByPrimaryKey(D0)!["Nm"]);
        Assert.Equal(new[] { "p3", "p2", "p1" }, p.CreateIndexCursor("PPK").FindRowsByEntry(D0).Select(r => (string)r["Nm"]!));
    }

    // A Joiner from P to C joins a parent read from the table to the children with its very bytes, as ACE's relationship
    // matches them: p1, whose value DAO's table-type recordset wrote, to children 1 and 4, which INSERT ... SELECT copied
    // from it, the four bytes after the value among them; p2 to child 2 and p3 to child 3. A DateTime given joins all
    // four, and each child joins back to its own parent. p1 was read as null and joined to nothing.
    [Fact]
    public void A_joiner_joins_a_row_DAO_s_table_type_recordset_wrote_on_its_bytes()
    {
        using var db = Database.Open(Copy(TableType));
        var (p, c) = (db.GetTable("P"), db.GetTable("C"));
        var childrenOf = new Joiner(p, new[] { "D" }, c, new[] { "PD" });
        var parents = p.Rows.ToDictionary(r => (string)r["Nm"]!);
        Assert.Equal(new[] { 1, 4 }, IdsOf(childrenOf.FindRows(parents["p1"])));
        Assert.Equal(new[] { 2 }, IdsOf(childrenOf.FindRows(parents["p2"])));
        Assert.Equal(new[] { 3 }, IdsOf(childrenOf.FindRows(parents["p3"])));
        Assert.Equal(new[] { 3, 2, 1, 4 }, IdsOf(childrenOf.FindRows(new Dictionary<string, object?> { ["D"] = D0 })));

        var parentOf = childrenOf.CreateReverse();
        var children = c.Rows.ToDictionary(r => (int)r["Id"]!);
        Assert.Equal("p1", parentOf.FindFirstRow(children[1])!["Nm"]);
        Assert.Equal("p1", parentOf.FindFirstRow(children[4])!["Nm"]);
        Assert.Equal("p2", parentOf.FindFirstRow(children[2])!["Nm"]);
        Assert.Equal("p3", parentOf.FindFirstRow(children[3])!["Nm"]);
    }

    // ── Relationships and unique indexes ────────────────────────────────────

    // A child has a parent only with its parent's very bytes, as ACE holds it: p1 is not deleted from under children 1
    // and 4, nor given another date; once they are gone it is deleted. A child given the date by this library - in the
    // first form, as ACE's INSERT gives it - is p2's.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_parent_DAO_s_table_type_recordset_wrote_keeps_the_children_with_its_bytes(bool verify)
    {
        using var db = Database.Open(Copy(TableType));
        db.EnforceForeignKeys = true;
        db.VerifyIndexes = verify;
        var (p, c) = (db.GetTable("P"), db.GetTable("C"));
        var parents = Pointers(p, "Nm");

        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => p.DeleteAt(parents["p1"]));
        Assert.Throws<ConstraintViolationException>(() => p.UpdateAt(parents["p1"], new Row { ["D"] = D0.AddDays(1) }));
        IndexChecks.AssertUnchanged(before, db);

        c.Insert(new Row { ["Id"] = 5, ["PD"] = D0 });
        Assert.Throws<ConstraintViolationException>(() => p.DeleteAt(parents["p2"]));
        var children = Pointers(c);
        c.DeleteAt(children[1]);
        c.DeleteAt(children[4]);
        p.DeleteAt(parents["p1"]);
        Assert.Equal(new[] { "p2", "p3" }, p.Rows.Select(r => (string)r["Nm"]!).OrderBy(n => n));
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "P", expectedRows: 2));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "C", expectedRows: 3));
    }

    private static Dictionary<string, int> Pointers(Table table, string column)
    {
        var pointers = new Dictionary<string, int>();
        var cursor = table.CreateCursor();
        while (cursor.MoveNext()) pointers[(string)cursor.Current![column]!] = cursor.CurrentRowPointer;
        return pointers;
    }

    // U's unique index holds the date in the third layout: the date given by this library, in the first form, is another
    // key, which ACE takes beside it (measured), and so does this library, trusting the index and verifying it; the
    // first form again is refused.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_unique_index_takes_the_first_form_beside_the_third_layout_and_refuses_it_again(bool verify)
    {
        using var db = Database.Open(Copy(TableType));
        db.VerifyIndexes = verify;
        var u = db.GetTable("U");
        u.Insert(new Row { ["Id"] = 2, ["UHow"] = "this library", ["K"] = D0 });
        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => u.Insert(new Row { ["Id"] = 3, ["UHow"] = "again", ["K"] = D0 }));
        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "U", expectedRows: 2));
    }

    // ── Updates and deletes ─────────────────────────────────────────────────

    // A row rewritten in another column keeps its 42 bytes - the four DAO left after the value among them - and its
    // entries; given its date back, as it was read, it keeps them too, as ACE's UPDATE of another column leaves them.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_row_rewritten_without_a_new_date_keeps_its_bytes_and_its_entries(bool verify)
    {
        using var db = Database.Open(Copy(TableType));
        db.VerifyIndexes = verify;
        var t = db.GetTable("T");
        var pointers = Pointers(t);
        foreach (int id in Stored.Keys.Where(IsTableTypeLayout))
            t.RewriteRowAt(pointers[id], new Row { ["How"] = "changed " + id });
        var cursor = t.CreateCursor();
        Assert.NotNull(cursor.FindRow("Id", 4));
        cursor.UpdateCurrentRow(new Row { ["K"] = cursor.Current!["K"], ["How"] = "given its date back" });

        Assert.Equal(Stored, StoredBytes(db, "T"));
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 14));
    }

    // A row given another date is written in the first form, as this library writes every date, and its entries move to
    // that key; rows deleted take theirs with them. No index is left with an entry of no row, nor a row without its
    // entry.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rows_given_a_new_date_or_deleted_move_or_take_their_entries(bool verify)
    {
        using var db = Database.Open(Copy(TableType));
        db.VerifyIndexes = verify;
        var t = db.GetTable("T");
        var pointers = Pointers(t);
        t.RewriteRowAt(pointers[4], new Row { ["K"] = D0.AddDays(1) });
        t.RewriteRowAt(pointers[14], new Row { ["K"] = DateTime.MaxValue.AddTicks(-1) });
        foreach (int id in new[] { 5, 6, 7, 13 }) t.DeleteRowAt(pointers[id]);

        var stored = StoredBytes(db, "T");
        Assert.Equal(Latin(ExtendedDateTimeCodec.Encode(D0.AddDays(1))), stored[4]);
        Assert.Equal(Latin(ExtendedDateTimeCodec.Encode(DateTime.MaxValue.AddTicks(-1))), stored[14]);
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 10));
        Assert.Equal(new[] { 2, 3, 1 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0)));
        Assert.Equal(new[] { 4 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0.AddDays(1))));
    }
}
