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
/// children are copies of their parents' bytes, and a unique index. Assets/engine-c89bfe4-table-type-keys.accdb
/// (tools/earlier-extended-date-table-type-keys.ps1) is a copy that this library at c89bfe4 changed, keying such rows
/// as no value.
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
    private const string EarlierTableType = "engine-c89bfe4-table-type-keys.accdb";

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

    // The orders DAO walks KAsc and KDesc in (measured): the bytes' order. Ascending, the third layout's rows come after
    // those of the other two from the year 2 on - the day's first digit is four places sooner - and among themselves by
    // their dates, then by the bytes DAO left after them; descending, the other way round.
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
        Assert.Equal(new[] { 2, 3, 1, 6, 5, 4 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByKey(D0)));
        Assert.Equal(new[] { 4, 5, 6, 1, 2, 3 }, IdsOf(t.CreateIndexCursor("KDesc").FindRowsByKey(D0)));
        Assert.Equal(new[] { 8, 7 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByKey(D1)));
        Assert.Equal(new[] { 10, 9 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByKey(new DateTime(2020, 1, 2))));
        Assert.Equal(new[] { 11 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByKey(D0.AddSeconds(1))));
        Assert.Equal(new[] { 12 }, IdsOf(t.CreateIndexCursor("KDesc").FindRowsByKey(new DateTime(2020, 1, 1, 23, 59, 59))));
        Assert.Equal(new[] { 13 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByKey(DateTime.MinValue)));
        Assert.Equal(new[] { 14 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByKey(DateTime.MaxValue)));
        Assert.Empty(t.CreateIndexCursor("KAsc").FindRowsByKey(D0.AddTicks(1)));

        Assert.Equal(2, t.CreateIndexCursor("KAsc").FindRow("K", D0)!["Id"]);
        Assert.Equal(4, t.CreateIndexCursor("KDesc").FindRowByKey(D0)!["Id"]);
        // With a Text column after the date: the third layout's part is of the date's key alone.
        Assert.Equal(4, t.CreateIndexCursor("KHow").FindRowByKey(D0, "AddNew, table-type, text")!["Id"]);
        Assert.Equal(7, t.CreateIndexCursor("KHow").FindRowByKey(D1, "AddNew, table-type")!["Id"]);
        Assert.Null(t.CreateIndexCursor("KHow").FindRowByKey(D1, "INSERT, table-type"));

        foreach (var date in new[] { D0, D1, new DateTime(2020, 1, 2), DateTime.MinValue, DateTime.MaxValue })
        {
            var cursor = t.CreateCursor();
            var found = new List<int>();
            for (var row = cursor.FindRow("K", date); row is not null; row = cursor.FindNextRow("K", date)) found.Add((int)row["Id"]!);
            Assert.Equal(Enumerable.Range(1, 14).Where(id => DateOf(id) == date), found);
        }
    }

    // An index with a column before the date - HowK (How, K), added by this library over the rows, keyed from their bytes
    // - is searched under the How given and, after it, the date's three parts: the third layout's is every key that
    // begins with the How's key and the 38 bytes.
    [Fact]
    public void A_date_is_found_through_an_index_with_a_column_before_it()
    {
        using var db = Database.Open(Copy(TableType));
        var t = db.GetTable("T");
        t.AddIndex(new IndexDefinition("HowK", "How", "K"));
        Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 14));
        var howK = t.CreateIndexCursor("HowK");
        Assert.Equal(4, howK.FindRowByKey("AddNew, table-type, text", D0)!["Id"]);
        Assert.Equal(new[] { 7 }, IdsOf(t.CreateIndexCursor("HowK").FindRowsByKey("AddNew, table-type", D1)));
        Assert.Equal(new[] { 13 }, IdsOf(t.CreateIndexCursor("HowK").FindRowsByKey("AddNew, table-type", DateTime.MinValue)));
        Assert.Equal(new[] { 1 }, IdsOf(t.CreateIndexCursor("HowK").FindRowsByKey("INSERT", D0)));
        Assert.Empty(t.CreateIndexCursor("HowK").FindRowsByKey("INSERT", D0.AddSeconds(1)));
        Assert.Empty(t.CreateIndexCursor("HowK").FindRowsByKey("AddNew, table-type, a Date", D1));
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
        Assert.Equal(new[] { "p3", "p2", "p1" }, p.CreateIndexCursor("PPK").FindRowsByKey(D0).Select(r => (string)r["Nm"]!));
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
        Assert.Equal(new[] { 2, 3, 1 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByKey(D0)));
        Assert.Equal(new[] { 4 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByKey(D0.AddDays(1))));
    }

    // ── Entries an earlier version made ─────────────────────────────────────

    /// <summary>The entries of <paramref name="table"/>'s indexes under another key than their row's own, and those of no row.</summary>
    private static (List<string> Wrong, List<string> OfNoRow) Audit(Database db, string table)
    {
        var rows = JetPages.Rows(db.File, IndexChecks.TdefOf(db, table), forKeys: true).ToDictionary(r => (r.Page, r.Row), r => r.Values);
        var (wrong, ofNoRow) = (new List<string>(), new List<string>());
        foreach (var index in db.GetTable(table).Indexes.GroupBy(ix => ix.IndexDataNumber).Select(g => g.First()))
            foreach (var entry in JetPages.LeafEntries(db.File, index.RootPageNumber))
            {
                string shown = $"{index.Name} {entry.Hex} -> {entry.RowPage}/{entry.Row}";
                if (!rows.TryGetValue((entry.RowPage, entry.Row), out var values)) ofNoRow.Add(shown);
                else if (!IndexKeys.Encode(index.Columns, index.Columns.Select(c => values.TryGetValue(c.Column.Name, out var v) ? v : null).ToArray())
                             .AsSpan().SequenceEqual(entry.Key)) wrong.Add(shown);
            }
        return (wrong, ofNoRow);
    }

    // c89bfe4 read the third layout as no value and keyed it so: in W's ByK, which it added, rows 2 and 3 are under the
    // key of no value (and row 4, in the second form, under the key of its date in the first form, as #51 found), and in
    // T's KAsc, KDesc and KHow rows 4, 7 and 9, which it rewrote verifying its indexes. Deleted, or given a new date, each
    // row takes that entry with it, found under the key of no value by the row it points at - one way down the tree, not
    // a reading of the rows with no value - and leaves no entry of no row. Of the four bytes after row 9's value the
    // third is a 7, at byte 40, where the first form has its count: those versions read it as no value all the same,
    // as this library reads what they read; read as its date, as this library reads it, its entry was looked for
    // under that date's key and left behind.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rows_an_earlier_version_keyed_as_no_value_take_those_entries_with_them(bool verify)
    {
        using var db = Database.Open(Copy(EarlierTableType));
        db.VerifyIndexes = verify;
        var (w, t) = (db.GetTable("W"), db.GetTable("T"));
        Assert.Equal(3, Audit(db, "W").Wrong.Count);
        Assert.Equal(9, Audit(db, "T").Wrong.Count);   // rows 4, 7 and 9, in each of the three indexes on K
        Assert.Empty(Audit(db, "T").OfNoRow);

        var (wRows, tRows) = (Pointers(w), Pointers(t));
        foreach (int id in new[] { 2, 3, 4 }) w.DeleteRowAt(wRows[id]);
        t.RewriteRowAt(tRows[4], new Row { ["K"] = D0.AddDays(2) });
        t.DeleteRowAt(tRows[7]);
        t.DeleteRowAt(tRows[9]);

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "W", expectedRows: 1));
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 12));
    }

    // A lookup through ByK - which c89bfe4 added, keying rows 2 and 3 as no value, and rows 1 and 4 under the key of
    // their date in the first form - trusts the index, as Access does, and does not look under the key of no value,
    // under which every row with no value is (#51): it finds rows 1 and 4 for the date and not rows 2 and 3, as ACE,
    // which looks under the keys of the bytes, does not either. A cursor over the table finds rows 1, 2 and 4.
    // Rewritten in another column with VerifyIndexes, which writes every entry of the row again, rows 2, 3 and 4 are
    // under the keys of their bytes, and the lookup finds each in the part of the index its layout is in.
    [Fact]
    public void A_row_an_earlier_version_keyed_as_no_value_is_found_through_the_index_once_it_is_under_its_own_key()
    {
        using (var db = Database.Open(Copy(EarlierTableType)))
        {
            var w = db.GetTable("W");
            Assert.Equal(new[] { 1, 4 }, IdsOf(w.CreateIndexCursor("ByK").FindRowsByKey(D0)));
            Assert.Empty(w.CreateIndexCursor("ByK").FindRowsByKey(D1));
            Assert.Equal(new[] { 1, 2, 4 }, IdsOf(FindAll(w.CreateCursor(), D0)));
        }
        using (var db = Database.Open(Copy(EarlierTableType)))
        {
            db.VerifyIndexes = true;
            var w = db.GetTable("W");
            var rows = Pointers(w);
            foreach (int id in new[] { 2, 3, 4 }) w.RewriteRowAt(rows[id], new Row { ["How"] = "rewritten" });
            Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "W", expectedRows: 4));
            Assert.Equal(new[] { 4, 1, 2 }, IdsOf(w.CreateIndexCursor("ByK").FindRowsByKey(D0)));
            Assert.Equal(new[] { 3 }, IdsOf(w.CreateIndexCursor("ByK").FindRowsByKey(D1)));
        }
    }

    private static List<Row> FindAll(Cursor cursor, DateTime date)
    {
        var found = new List<Row>();
        for (var row = cursor.FindRow("K", date); row is not null; row = cursor.FindNextRow("K", date)) found.Add(row);
        return found;
    }

    // ── What a row in the third layout costs ────────────────────────────────

    /// <summary>The 42 bytes DAO's table-type recordset writes for <paramref name="date"/>, with what DAO left after the value in T's row 4.</summary>
    private static byte[] TableTypeBytes(DateTime date)
    {
        var bytes = new byte[ExtendedDateTimeCodec.Size];
        Encoding.ASCII.GetBytes($"{date.Ticks / TimeSpan.TicksPerDay:D15}:{date.Ticks % TimeSpan.TicksPerDay:D19}:7").CopyTo(bytes, 0);
        bytes[40] = 0xAE;
        bytes[41] = 0x02;
        return bytes;
    }

    /// <summary>The key ACE makes of 42 stored bytes, ascending (measured): built here, not by the library.</summary>
    private static byte[] KeyOfBytes(byte[] stored)
    {
        var key = new List<byte> { 0x7F };
        for (int block = 0; block < 5; block++)
        {
            key.AddRange(stored.Skip(block * 8).Take(8));
            key.Add(9);
        }
        key.AddRange(stored.Skip(40));
        key.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 2 });
        return key.ToArray();
    }

    /// <summary>
    /// Gives the row at <paramref name="pointer"/> of <paramref name="table"/> its date <paramref name="date"/> in the
    /// layout DAO's table-type recordset writes (<see cref="TableTypeBytes"/>), its entry in <paramref name="index"/> -
    /// one of that column alone, ascending - under the key of those bytes: patched into the file.
    /// </summary>
    private static void ToTableTypeLayout(Database db, Table table, Index index, int pointer, DateTime date)
    {
        byte[] first = ExtendedDateTimeCodec.Encode(date), third = TableTypeBytes(date);
        byte[] page = db.File.ReadPage(RowPointer.Page(pointer));
        int at = Enumerable.Range(0, page.Length - first.Length).Single(i => page.AsSpan(i, first.Length).SequenceEqual(first));
        third.CopyTo(page, at);
        db.File.WritePage(RowPointer.Page(pointer), page);
        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        var target = IndexWriter.Target.For(table.Definition.TdefPageNumber, index);
        writer.Remove(target, KeyOfBytes(first), pointer, out bool found);
        Assert.True(found);
        writer.Insert(target, KeyOfBytes(third), pointer);
    }

    /// <summary>
    /// The pages read by four operations on a row and a parent in the third layout, beside <paramref name="nulls"/> rows
    /// of no value, by name, the indexes trusted and verified: U, a unique index UD on D over the rows of no value and
    /// five dates; P, keyed by D, and C, related to it by PD with its updates and deletes cascading, over children of no
    /// parent and three of p1; U's first dated row and p2 in the third layout. Each operation is on a copy of its own,
    /// after a change that has each index it uses looked at with <see cref="Database.VerifyIndexes"/>.
    /// </summary>
    private Dictionary<string, long> PagesBesideRowsOfNoValue(int nulls)
    {
        string path = Path.Combine(Path.GetTempPath(), $"extended_table_type_{Guid.NewGuid():N}.accdb");
        _paths.Add(path);
        using (var db = Database.Create(path, FileFormat.Access2016))
        {
            var u = db.CreateTable("U",
                new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTimeExtended), new ColumnDefinition("N", DataType.Int32) },
                new[] { "Id" }, new[] { new IndexDefinition("UD", "D") { IsUnique = true } });
            u.InsertRange(Enumerable.Range(1, nulls).Select(i => new Row { ["Id"] = i, ["N"] = i })
                .Concat(Enumerable.Range(1, 5).Select(i => new Row { ["Id"] = nulls + i, ["D"] = new DateTime(2019, 1, 1).AddDays(i), ["N"] = i })));
            var p = db.CreateTable("P",
                new[] { new ColumnDefinition("Nm", DataType.Text) { MaxLength = 10 }, new ColumnDefinition("D", DataType.DateTimeExtended) },
                new[] { "D" }, Array.Empty<IndexDefinition>());
            var c = db.CreateTable("C",
                new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("PD", DataType.DateTimeExtended) },
                new[] { "Id" }, Array.Empty<IndexDefinition>());
            db.CreateRelationship(new RelationshipDefinition("P", "D", "C", "PD") { Name = "FKC", HasReferentialIntegrity = true, CascadeUpdates = true, CascadeDeletes = true });
            p.InsertRange(new[] { 1, 2, 3 }.Select(i => new Row { ["Nm"] = "p" + i, ["D"] = new DateTime(2018, 1, i) }));
            c.InsertRange(Enumerable.Range(1, nulls).Select(i => new Row { ["Id"] = i })
                .Concat(Enumerable.Range(1, 3).Select(i => new Row { ["Id"] = nulls + i, ["PD"] = new DateTime(2018, 1, 1) })));

            var cursor = u.CreateCursor();
            Assert.NotNull(cursor.FindRow("Id", nulls + 1));
            ToTableTypeLayout(db, u, u.Indexes.Single(ix => ix.Name == "UD"), cursor.CurrentRowPointer, new DateTime(2019, 1, 2));
            cursor = p.CreateCursor();
            Assert.NotNull(cursor.FindRow("Nm", "p2"));
            ToTableTypeLayout(db, p, p.Indexes.Single(ix => ix.IsPrimaryKey), cursor.CurrentRowPointer, new DateTime(2018, 1, 2));
        }

        var pages = new Dictionary<string, long>();
        foreach (bool verify in new[] { false, true })
        {
            long Measure(Action<Database> before, Func<Database, Action> operation)
            {
                string copy = Path.Combine(Path.GetTempPath(), $"extended_table_type_{Guid.NewGuid():N}.accdb");
                _paths.Add(copy);
                File.Copy(path, copy);
                using var db = Database.Open(copy);
                db.EnforceForeignKeys = true;
                db.VerifyIndexes = verify;
                before(db);
                var change = operation(db);
                long read = db.File.ReadCount;
                change();
                return db.File.ReadCount - read;
            }
            void RowOfU(Database db, int id, int n)
            {
                var cursor = db.GetTable("U").CreateCursor();
                Assert.NotNull(cursor.FindRow("Id", id));
                cursor.UpdateCurrentRow(new Row { ["N"] = n });
            }
            void Redated(Database db, string name, DateTime date)
            {
                var cursor = db.GetTable("P").CreateCursor();
                Assert.NotNull(cursor.FindRow("Nm", name));
                cursor.UpdateCurrentRow(new Row { ["D"] = date });
            }
            Action OnP2(Database db, Action<Cursor> change)
            {
                var cursor = db.GetTable("P").CreateCursor();
                Assert.NotNull(cursor.FindRow("Nm", "p2"));
                return () => change(cursor);
            }
            string mode = verify ? "verified" : "trusted";
            pages[$"U's row in the third layout, another column ({mode})"] = Measure(db => RowOfU(db, nulls + 2, 8), db =>
            {
                var cursor = db.GetTable("U").CreateCursor();
                Assert.NotNull(cursor.FindRow("Id", nulls + 1));
                return () => cursor.UpdateCurrentRow(new Row { ["N"] = 7 });
            });
            pages[$"p2 deleted ({mode})"] = Measure(db => Redated(db, "p3", new DateTime(2018, 3, 3)),
                db => OnP2(db, cursor => cursor.DeleteCurrentRow()));
            pages[$"p2 given another date ({mode})"] = Measure(db => Redated(db, "p3", new DateTime(2018, 3, 3)),
                db => OnP2(db, cursor => cursor.UpdateCurrentRow(new Row { ["D"] = new DateTime(2018, 2, 2) })));
            pages[$"a Joiner from p2 ({mode})"] = Measure(db => Redated(db, "p3", new DateTime(2018, 3, 3)), db =>
            {
                var p = db.GetTable("P");
                var p2 = p.Rows.First(r => (string)r["Nm"]! == "p2");
                var joiner = new Joiner(p, new[] { "D" }, db.GetTable("C"), new[] { "PD" });
                return () => Assert.Empty(joiner.FindRows(p2));
            });
        }
        return pages;
    }

    // A row in the third layout, beside rows of no value under a unique index and a relationship's index - Access's
    // default for both does not ignore nulls: changing the row in another column, deleting the parent, giving it another
    // date and joining from it read as many pages beside 20,000 rows of no value as beside 2,000, the indexes trusted or
    // verified. The versions that keyed the date they read read such bytes as no value, and keyed them so; a search
    // does not look under that key (#51), under which every row of no value is, and each would be read.
    [Fact]
    public void What_a_row_in_the_third_layout_costs_does_not_grow_with_the_rows_of_no_value()
    {
        var few = PagesBesideRowsOfNoValue(2_000);
        var many = PagesBesideRowsOfNoValue(20_000);
        string shown = string.Join("; ", few.Keys.Select(operation => $"{operation}: {few[operation]} beside 2,000, {many[operation]} beside 20,000"));
        foreach (var operation in few.Keys)
            Assert.True(many[operation] <= few[operation] + 4, shown);
    }
}
