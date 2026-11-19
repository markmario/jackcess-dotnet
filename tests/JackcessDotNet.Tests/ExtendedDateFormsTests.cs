using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A Date/Time Extended value is keyed by the 42 bytes its row stores, as Access keys it, and not by the date they
/// say (#51). Access's INSERT writes a date in one form and its SQL UPDATE in another; it keys the two by their
/// bytes, so a unique index or a primary key takes a row in each, a relationship's child has a parent only with
/// its very bytes, and an index added over such rows holds each under the key of its bytes.
/// <para>
/// Assets/ace-extended-date-forms.accdb (tools/ace-extended-date-forms.ps1) holds rows ACE wrote in both forms and
/// in three more it reads a date from but does not write - patched into the file - with indexes ACE made over them
/// afterwards. Assets/ace-second-form-foreign-key.accdb (tools/ace-second-form-foreign-key.ps1) holds a
/// relationship on such a column: p2's key and child 2's foreign key are 2 January 2020 03:04:05 in the second form.
/// </para>
/// </summary>
public sealed class ExtendedDateFormsTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string Copy(string asset)
    {
        string path = Path.Combine(Path.GetTempPath(), $"extended_forms_{Guid.NewGuid():N}{Path.GetExtension(asset)}");
        _paths.Add(path);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), path);
        File.SetAttributes(path, FileAttributes.Normal);
        return path;
    }

    private const string Forms = "ace-extended-date-forms.accdb";
    private const string SecondFormForeignKey = "ace-second-form-foreign-key.accdb";

    private static readonly DateTime D0 = new(2020, 1, 2, 3, 4, 5);

    /// <summary>T's rows as ACE stored them, by Id (measured: tools/ace-extended-date-forms.ps1 lists how each was made).</summary>
    private static readonly Dictionary<int, string> Stored = new()
    {
        [1]  = "0000000000000737425:0000000110450000000:7\0",   // INSERT
        [2]  = "0000000000000737425:0000000000000011045:0\0",   // UPDATE
        [3]  = " 000000000000737425:0000000110450000000:7\0",   // a space for the day's first digit
        [4]  = "+000000000000737425:0000000110450000000:7\0",   // a plus sign for it
        [5]  = "0000000000000737425:0000000110450000000:7x",    // 'x' in the last byte
        [6]  = "0000000000000737425: 000000110450000000:7\0",   // a space for the time's first digit
        [7]  = " 000000000000737425:0000000000000011045:0\0",   // UPDATE, then a space for the day's first digit
        [8]  = "0000000000000737425:0000000110451234567:7\0",   // INSERT, a fraction of a second
        [9]  = "0000000000000737425:0000000000000011046:0\0",   // UPDATE, a second later
        [10] = "0000000000000737424:0000000863990000000:7\0",   // INSERT, the day before
        [11] = "0000000000000737425:0000000000000000000:0\0",   // UPDATE, midnight
        [12] = "0000000000000737425:0000000000000000000:7\0",   // INSERT, midnight
        [13] = "0000000000000737425:0000000000000000001:7\0",   // INSERT, a tick after midnight
        [14] = "0000000000000737425:0000000000000000001:0\0",   // UPDATE, a second after midnight
        [15] = "0000000000000737425:0000000000010000000:7\0",   // INSERT, a second after midnight
    };

    /// <summary>The date each of T's rows says, by Id: rows 1 to 7 are 2 January 2020 03:04:05.</summary>
    private static DateTime DateOf(int id) => id switch
    {
        <= 7 => D0,
        8  => D0.AddTicks(1234567),
        9  => D0.AddSeconds(1),
        10 => new DateTime(2020, 1, 1, 23, 59, 59),
        11 or 12 => new DateTime(2020, 1, 2),
        13 => new DateTime(2020, 1, 2).AddTicks(1),
        _  => new DateTime(2020, 1, 2, 0, 0, 1),
    };

    private static Dictionary<int, int> Pointers(Table table)
    {
        var pointers = new Dictionary<int, int>();
        var cursor = table.CreateCursor();
        while (cursor.MoveNext()) pointers[(int)cursor.Current!["Id"]!] = cursor.CurrentRowPointer;
        return pointers;
    }

    /// <summary>Every entry of an index, as "key -> page/row".</summary>
    private static List<string> EntriesOf(Database db, string table, string index)
        => JetPages.LeafEntries(db.File, db.GetTable(table).Indexes.Single(ix => ix.Name == index).RootPageNumber)
            .Select(e => $"{e.Hex} -> {e.RowPage}/{e.Row}").ToList();

    // ── The forms, and their keys ───────────────────────────────────────────

    [Fact]
    public void Every_form_is_read_as_the_date_it_says_and_handed_to_keys_as_its_bytes()
    {
        using var db = Database.Open(Copy(Forms));
        foreach (var (_, _, values) in JetPages.Rows(db.File, IndexChecks.TdefOf(db, "T"), forKeys: true))
        {
            int id = (int)values["Id"]!;
            var stored = Assert.IsType<StoredExtendedDate>(values["K"]);
            Assert.Equal(Stored[id], Encoding.ASCII.GetString(stored.Bytes));
            Assert.Equal(DateOf(id), stored.Date);
            Assert.Equal(id is 1 or 8 or 10 or 12 or 13 or 15, stored.IsFirstForm);
        }
        foreach (var row in db.GetTable("T").Rows)
            Assert.Equal(DateOf((int)row["Id"]!), row["K"]);
    }

    // What ACE's UPDATE stored is the second form of the date: its time of day in whole seconds, a count of 0.
    [Fact]
    public void The_second_form_is_the_one_ACE_s_UPDATE_stored()
    {
        foreach (int id in new[] { 2, 9, 11, 14 })
            Assert.Equal(Stored[id], Encoding.ASCII.GetString(ExtendedDateTimeCodec.EncodeInSeconds(DateOf(id))!));
        foreach (int id in new[] { 1, 8, 10, 12, 13, 15 })
            Assert.Equal(Stored[id], Encoding.ASCII.GetString(ExtendedDateTimeCodec.Encode(DateOf(id))));

        Assert.Null(ExtendedDateTimeCodec.EncodeInSeconds(D0.AddTicks(1)));
        Assert.Equal(new[] { Stored[1], Stored[2] }, ExtendedDateTimeCodec.FormsOf(D0).Select(f => Encoding.ASCII.GetString(f)));
        Assert.Equal(new[] { Stored[8] }, ExtendedDateTimeCodec.FormsOf(DateOf(8)).Select(f => Encoding.ASCII.GetString(f)));
    }

    // Every entry of every index ACE made over the forms - one column ascending and descending, with a Text column
    // after it and before it, unique, a primary key - is the key this library makes of the bytes the row stores.
    // Made of the date read, as it was, the key of every row in another form than ACE's INSERT's was another.
    [Theory]
    [InlineData("T", 5, 15)]
    [InlineData("U", 2, 4)]
    [InlineData("V", 1, 2)]
    public void Every_entry_ACE_made_is_the_key_of_the_bytes_its_row_stores(string table, int trees, int rows)
    {
        using var db = Database.Open(Copy(Forms));
        Assert.Equal(trees, IndexChecks.AssertEveryIndexHoldsTheRows(db, table, expectedRows: rows));
    }

    // The order DAO walks KAsc and KDesc in (measured): the order of the bytes, not of the dates they say.
    [Fact]
    public void An_index_cursor_gives_the_rows_in_the_order_DAO_walks_ACE_s_index()
    {
        using var db = Database.Open(Copy(Forms));
        var t = db.GetTable("T");
        Assert.Equal(new[] { 7, 3, 4, 10, 6, 11, 12, 14, 13, 2, 9, 15, 1, 5, 8 }, t.CreateIndexCursor("KAsc").Select(r => (int)r["Id"]!));
        Assert.Equal(new[] { 8, 5, 1, 15, 9, 2, 13, 14, 12, 11, 6, 10, 4, 3, 7 }, t.CreateIndexCursor("KDesc").Select(r => (int)r["Id"]!));
    }

    // ── Indexes added over the forms ────────────────────────────────────────

    // Each of T's indexes dropped and added again by this library over the same rows has the entries ACE made:
    // the bytes' keys, in the bytes' order. Made of the dates read, the entries of rows 2 to 7, 9, 11 and 14 were
    // under the key of their dates in the first form, where ACE does not look for them.
    [Theory]
    [InlineData("KAsc")]
    [InlineData("KDesc")]
    [InlineData("KForm")]
    [InlineData("FormK")]
    public void An_index_added_over_the_forms_has_the_entries_ACE_made(string name)
    {
        using var db = Database.Open(Copy(Forms));
        var t = db.GetTable("T");
        var index = t.Indexes.Single(ix => ix.Name == name);
        var ace = EntriesOf(db, "T", name);
        var definition = new IndexDefinition(name, index.Columns.Select(c => c.Column.Name).ToArray());
        foreach (var column in index.Columns.Where(c => !c.IsAscending)) definition.DescendingColumns.Add(column.Column.Name);

        t.DropIndex(name);
        t.AddIndex(definition);
        Assert.Equal(ace, EntriesOf(db, "T", name));
        Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 15));
    }

    // Rows 1 to 7 are one date in seven forms: seven keys, which a unique index takes, as ACE took U's four. Keyed
    // by the date read, they were taken for duplicates and the index refused.
    [Fact]
    public void A_unique_index_added_over_one_date_in_seven_forms_is_taken()
    {
        using var db = Database.Open(Copy(Forms));
        var t = db.GetTable("T");
        t.AddIndex(new IndexDefinition("KUnique", "K") { IsUnique = true });
        Assert.Equal(EntriesOf(db, "T", "KAsc"), EntriesOf(db, "T", "KUnique"));
        Assert.Throws<ConstraintViolationException>(() => t.Insert(new Row { ["Id"] = 20, ["TForm"] = "again", ["K"] = D0 }));
    }

    // W holds the date in four forms and no key: a primary key over it is taken, as ACE took V's over two (the
    // review of #52's third finding), and then refuses the date's first form again, as ACE refused it in U.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_primary_key_added_over_one_date_in_four_forms_is_taken(bool verify)
    {
        using var db = Database.Open(Copy(Forms));
        db.VerifyIndexes = verify;
        var w = db.GetTable("W");
        w.AddIndex(new IndexDefinition("PrimaryKey", "K") { IsPrimaryKey = true });
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "W", expectedRows: 4));

        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => w.Insert(new Row { ["Id"] = 5, ["WForm"] = "again", ["K"] = D0 }));
        IndexChecks.AssertUnchanged(before, db);
        w.Insert(new Row { ["Id"] = 6, ["WForm"] = "a tick on", ["K"] = D0.AddTicks(1) });
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "W", expectedRows: 5));
    }

    // ── A unique index's duplicates ─────────────────────────────────────────

    // P is keyed by D, and p2's key is 2 January 2020 03:04:05 in the second form. ACE's INSERT of a third parent
    // with that date writes the first form, another key, and ACE takes it; this library does, its index trusted or
    // verified, and refuses a fourth. Verified, p3 was refused: p2 was a row the index lacked under the key of its
    // date read, the first form's.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_parent_with_the_date_of_one_ACE_keyed_in_the_second_form_is_taken_and_a_second_refused(bool verify)
    {
        using var db = Database.Open(Copy(SecondFormForeignKey));
        db.VerifyIndexes = verify;
        var p = db.GetTable("P");
        p.Insert(new Row { ["Nm"] = "p3", ["D"] = D0 });
        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => p.Insert(new Row { ["Nm"] = "p4", ["D"] = D0 }));
        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "P", expectedRows: 3));
    }

    // U's unique index holds the date in four forms: its first form again is refused, trusting the index and
    // verifying it, as ACE refused it; a date a second on is taken.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_unique_index_refuses_a_form_it_holds(bool verify)
    {
        using var db = Database.Open(Copy(Forms));
        db.VerifyIndexes = verify;
        var u = db.GetTable("U");
        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => u.Insert(new Row { ["Id"] = 5, ["UForm"] = "again", ["K"] = D0 }));
        IndexChecks.AssertUnchanged(before, db);
        u.Insert(new Row { ["Id"] = 6, ["UForm"] = "a second on", ["K"] = D0.AddSeconds(1) });
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "U", expectedRows: 5));
    }

    // ── A relationship's rows ───────────────────────────────────────────────

    // A child is matched to its parent by the bytes of their keys, as ACE matches it: a child with p2's date, which
    // this library writes in the first form, has no parent - ACE refuses its INSERT - until a parent with the
    // first form is there; then each parent has its own child, and deleting either is refused. Matched by the date
    // read, the child was taken as p2's.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_child_has_a_parent_only_with_its_parent_s_bytes(bool verify)
    {
        using var db = Database.Open(Copy(SecondFormForeignKey));
        db.EnforceForeignKeys = true;
        db.VerifyIndexes = verify;
        var (p, c) = (db.GetTable("P"), db.GetTable("C"));

        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => c.Insert(new Row { ["Id"] = 4, ["PD"] = D0 }));
        IndexChecks.AssertUnchanged(before, db);

        p.Insert(new Row { ["Nm"] = "p3", ["D"] = D0 });
        c.Insert(new Row { ["Id"] = 5, ["PD"] = D0 });
        before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => p.DeleteRow("Nm", "p3"));
        Assert.Throws<ConstraintViolationException>(() => p.DeleteRow("Nm", "p2"));
        IndexChecks.AssertUnchanged(before, db);

        c.DeleteRow("Id", 5);
        p.DeleteRow("Nm", "p3");
        Assert.Equal(new[] { "p1", "p2" }, p.Rows.Select(r => (string)r["Nm"]!).OrderBy(n => n));
        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "P", expectedRows: 2));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "C", expectedRows: 2));
    }

    // ── Lookups ─────────────────────────────────────────────────────────────

    private static int[] IdsOf(IEnumerable<Row> rows) => rows.Select(r => (int)r["Id"]!).ToArray();

    // A date through ACE's indexes is found in both forms ACE writes, each under its own key, in the index's order:
    // the second form's key comes first ascending and last descending. A cursor over the table finds those and the
    // forms patched in, which the index holds under keys of their own. Through the index the first form alone was
    // found.
    [Fact]
    public void A_date_is_found_through_an_index_in_both_forms_ACE_writes()
    {
        using var db = Database.Open(Copy(Forms));
        var t = db.GetTable("T");
        Assert.Equal(new[] { 2, 1 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0)));
        Assert.Equal(new[] { 1, 2 }, IdsOf(t.CreateIndexCursor("KDesc").FindRowsByEntry(D0)));
        Assert.Equal(2, t.CreateIndexCursor("KAsc").FindRowByEntry(D0)!["Id"]);
        Assert.Equal(1, t.CreateIndexCursor("KDesc").FindRow("K", D0)!["Id"]);

        // Midnight and a second after it in both forms; a tick after midnight, a second form alone, a fraction.
        var midnight = new DateTime(2020, 1, 2);
        Assert.Equal(new[] { 11, 12 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(midnight)));
        Assert.Equal(new[] { 14, 15 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(midnight.AddSeconds(1))));
        Assert.Equal(new[] { 13 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(midnight.AddTicks(1))));
        Assert.Equal(new[] { 9 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0.AddSeconds(1))));
        Assert.Equal(new[] { 8 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0.AddTicks(1234567))));
        Assert.Empty(t.CreateIndexCursor("KAsc").FindRowsByEntry(D0.AddTicks(1)));

        // With a Text column before the date and after it.
        Assert.Equal(2, t.CreateIndexCursor("FormK").FindRowByEntry("second", D0)!["Id"]);
        Assert.Equal(2, t.CreateIndexCursor("KForm").FindRowByEntry(D0, "second")!["Id"]);
        Assert.Null(t.CreateIndexCursor("FormK").FindRowByEntry("space-day", D0));

        var cursor = t.CreateCursor();
        var found = new List<int>();
        for (var row = cursor.FindRow("K", D0); row is not null; row = cursor.FindNextRow("K", D0)) found.Add((int)row["Id"]!);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, found);
    }

    // The review of #52's first finding: p2, keyed by its date in the second form, and child 2, whose foreign key is
    // that date in the second form, are found by FindRowByPrimaryKey and through the primary key and the
    // relationship's index, the indexes trusted or verified; and V's row of the date in the second form, whose key
    // is before the first form's. Each was "not found".
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_row_ACE_keyed_in_the_second_form_is_found_by_its_primary_key_and_through_an_index(bool verify)
    {
        using (var db = Database.Open(Copy(SecondFormForeignKey)))
        {
            db.VerifyIndexes = verify;
            var (p, c) = (db.GetTable("P"), db.GetTable("C"));
            Assert.Equal("p2", p.FindRowByPrimaryKey(D0)!["Nm"]);
            Assert.Equal("p2", p.CreateIndexCursor("PK").FindRow("D", D0)!["Nm"]);
            Assert.Equal("p2", p.CreateIndexCursor("PK").FindRowByEntry(D0)!["Nm"]);
            Assert.Equal(2, c.CreateIndexCursor("FKPD").FindRow("PD", D0)!["Id"]);
        }
        using (var db = Database.Open(Copy(Forms)))
        {
            db.VerifyIndexes = verify;
            Assert.Equal(2, db.GetTable("V").FindRowByPrimaryKey(D0)!["Id"]);
        }
    }

    // A Joiner from P to C joins a parent read from the table to the children with its very bytes, as Access's
    // relationship joins them: p2 to child 2, and p3 - the date in the first form, added beside it - to child 5. A
    // DateTime given joins both forms; and each child joins back to its own parent. A parent read was joined on its
    // date in the first form: p2 to child 5.
    [Fact]
    public void A_joiner_joins_a_row_that_was_read_on_its_bytes()
    {
        using var db = Database.Open(Copy(SecondFormForeignKey));
        db.EnforceForeignKeys = true;
        var (p, c) = (db.GetTable("P"), db.GetTable("C"));
        p.Insert(new Row { ["Nm"] = "p3", ["D"] = D0 });
        c.Insert(new Row { ["Id"] = 5, ["PD"] = D0 });

        var childrenOf = new Joiner(p, new[] { "D" }, c, new[] { "PD" });
        var parents = p.Rows.ToDictionary(r => (string)r["Nm"]!);
        Assert.Equal(new[] { 1 }, IdsOf(childrenOf.FindRows(parents["p1"])));
        Assert.Equal(new[] { 2 }, IdsOf(childrenOf.FindRows(parents["p2"])));
        Assert.Equal(new[] { 5 }, IdsOf(childrenOf.FindRows(parents["p3"])));
        Assert.Equal(new[] { 2, 5 }, IdsOf(childrenOf.FindRows(new Dictionary<string, object?> { ["D"] = D0 })));

        var parentOf = childrenOf.CreateReverse();
        var children = c.Rows.ToDictionary(r => (int)r["Id"]!);
        Assert.Equal("p2", parentOf.FindFirstRow(children[2])!["Nm"]);
        Assert.Equal("p3", parentOf.FindFirstRow(children[5])!["Nm"]);
    }

    // ── Rows changed and deleted ────────────────────────────────────────────

    // A row in any form rewritten in another column keeps the entries ACE made of its date, its indexes trusted or
    // verified, and the indexes with the other column move it to the key of its bytes and the new value. Verified,
    // the entries of the date were taken out by the row they point at and the row entered under the key of its date
    // read, where ACE does not look for it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_row_rewritten_in_another_column_keeps_the_entries_ACE_made_of_its_date(bool verify)
    {
        using var db = Database.Open(Copy(Forms));
        db.VerifyIndexes = verify;
        var t = db.GetTable("T");
        var before = new[] { "KAsc", "KDesc" }.ToDictionary(ix => ix, ix => EntriesOf(db, "T", ix));
        var pointers = Pointers(t);
        foreach (int id in new[] { 2, 3, 4, 5, 6, 7, 9, 11, 14 })
            t.RewriteRowAt(pointers[id], new Row { ["TForm"] = "changed " + id });
        foreach (var index in before.Keys)
            Assert.Equal(before[index], EntriesOf(db, "T", index));
        Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 15));
    }

    // Rows in every form deleted take every entry with them, under the key of their bytes, and leave every other
    // row's where it was.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rows_in_every_form_deleted_take_their_entries_with_them(bool verify)
    {
        using var db = Database.Open(Copy(Forms));
        db.VerifyIndexes = verify;
        var t = db.GetTable("T");
        var pointers = Pointers(t);
        foreach (int id in new[] { 2, 3, 4, 5, 6, 7, 9, 11, 14 })
            t.DeleteRowAt(pointers[id]);
        Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: 6));
    }

    // ── Entries an earlier version made ─────────────────────────────────────

    /// <summary>
    /// The entries of <paramref name="table"/>'s indexes that point at a row of it under another key than the row's
    /// own, and those that point at no row of it, each as "index key -> page/row".
    /// </summary>
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

    // Files this library wrote before #51 (tools/earlier-extended-date-keys.ps1): at 913240c, which read Access's
    // second form as no value, and at c89bfe4, which read it as its date; each keyed the date it read, in the first
    // form. W's index ByK, which such a version added, holds rows 2 to 4 under those keys, and T's indexes on K rows 2
    // and 3, which such a version rewrote. Rows deleted, and a row given another date, take those entries with them,
    // found under the keys those versions made: no row is left with an entry under another key than its own, and no
    // entry pointing at no row is left but those 913240c left when it moved rows 2 and 3 (indexes that are not
    // unique, which nothing here looks at). With nothing looked for under those keys, every one was left behind.
    [Theory]
    [InlineData("engine-913240c-extended-date-keys.accdb", false)]
    [InlineData("engine-913240c-extended-date-keys.accdb", true)]
    [InlineData("engine-c89bfe4-extended-date-keys.accdb", false)]
    [InlineData("engine-c89bfe4-extended-date-keys.accdb", true)]
    public void Rows_an_earlier_version_keyed_by_the_date_it_read_take_those_entries_with_them(string asset, bool verify)
    {
        using var db = Database.Open(Copy(asset));
        db.VerifyIndexes = verify;
        var (w, t) = (db.GetTable("W"), db.GetTable("T"));
        Assert.Equal(3, Audit(db, "W").Wrong.Count);
        Assert.Equal(8, Audit(db, "T").Wrong.Count);   // rows 2 and 3, in each of the four indexes on K
        var ofNoRowBefore = Audit(db, "T").OfNoRow;

        var (wRows, tRows) = (Pointers(w), Pointers(t));
        foreach (int id in new[] { 2, 3, 4 }) w.DeleteRowAt(wRows[id]);
        t.RewriteRowAt(tRows[2], new Row { ["K"] = D0.AddDays(2) });
        t.DeleteRowAt(tRows[3]);

        Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "W", expectedRows: 1));
        var (wrong, ofNoRow) = Audit(db, "T");
        Assert.Empty(wrong);
        Assert.Subset(ofNoRowBefore.ToHashSet(), ofNoRow.ToHashSet());
    }

    // An entry an earlier version put under another key - U's row 2, its date in the second form, under the key of
    // no value, as 913240c entered such a row - is the row's wrong one with VerifyIndexes: when the table next
    // changes it is moved to the key of the row's bytes, as an entry of any other type is (#36). Trusting the index,
    // it stays where it is, believed, as Access believes it. Before #51 such a row's key was not one this library made
    // as Access does, and its entry was believed either way.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_entry_under_a_key_an_earlier_version_made_is_moved_to_the_key_of_the_row_s_bytes_with_VerifyIndexes(bool verify)
    {
        using var db = Database.Open(Copy(Forms));
        var u = db.GetTable("U");
        var ku = u.Indexes.Single(ix => ix.Name == "KU");
        var target = IndexWriter.Target.For(u.Definition.TdefPageNumber, ku);
        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        int row2 = Pointers(u)[2];
        writer.Remove(target, KeyOfBytes(Encoding.ASCII.GetBytes(Stored[2])), row2, out bool found);
        Assert.True(found);
        writer.Insert(target, IndexKeys.Encode(ku.Columns, new object?[] { null }), row2);
        Assert.Single(Audit(db, "U").Wrong);

        db.VerifyIndexes = verify;
        u.Insert(new Row { ["Id"] = 7, ["UForm"] = "a day on", ["K"] = D0.AddDays(1) });
        if (verify) Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "U", expectedRows: 5));
        else Assert.Single(Audit(db, "U").Wrong);
    }

    // ── What it costs ───────────────────────────────────────────────────────

    /// <summary>A date in Access's second form, built here as ACE's SQL UPDATE writes it (measured).</summary>
    private static byte[] InSeconds(DateTime date)
        => Encoding.ASCII.GetBytes($"{date.Ticks / TimeSpan.TicksPerDay:D19}:{date.Ticks % TimeSpan.TicksPerDay / TimeSpan.TicksPerSecond:D19}:0\0");

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

    // Rows ACE stored in the second form, under the keys of their bytes, are deleted, have their date rewritten, and
    // are rewritten in a column no index has for what a Date/Time row costs: one way down the tree, a page more at
    // most for the longer key. At c89bfe4, which keyed the date it read, every leaf of the index was read to delete
    // one or rewrite its date. A row whose entry an earlier version left under the key of its date read costs one
    // way down the tree more, where every leaf was read too.
    [Fact]
    public void A_row_in_the_second_form_is_deleted_and_rewritten_for_what_a_Date_Time_row_costs()
    {
        string path = Path.Combine(Path.GetTempPath(), $"extended_forms_{Guid.NewGuid():N}.accdb");
        _paths.Add(path);
        using var db = Database.Create(path, FileFormat.Access2016);
        ColumnDefinition[] Columns(DataType type) => new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", type),
            new ColumnDefinition("Nt", DataType.Text) { MaxLength = 20 },
        };
        var extended = db.CreateTable("TE", Columns(DataType.DateTimeExtended), new[] { "Id" }, new[] { new IndexDefinition("ByD", "D") });
        var plain = db.CreateTable("TD", Columns(DataType.DateTime), new[] { "Id" }, new[] { new IndexDefinition("ByD", "D") });
        const int Rows = 5_000;
        DateTime DateOf(int id) => new DateTime(2020, 1, 1).AddSeconds(id * 37);
        for (int id = 0; id < Rows; id++)
        {
            extended.Insert(new Row { ["Id"] = id, ["D"] = DateOf(id), ["Nt"] = "n" });
            plain.Insert(new Row { ["Id"] = id, ["D"] = DateOf(id), ["Nt"] = "n" });
        }
        var byD = extended.Indexes.Single(ix => ix.Name == "ByD");
        int leaves = JetPages.TreePages(db.File, byD.RootPageNumber).Count(p => p.IsLeaf);
        Assert.True(leaves > 50, $"the index on D has {leaves} leaves, too few to show them all read");

        // Rows put in the second form as ACE's UPDATE leaves them, under the key of their bytes; and rows whose bytes
        // are in it and whose entry is under the key of their date, as an earlier version of this library left one.
        var (e, d) = (Pointers(extended), Pointers(plain));
        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        var target = IndexWriter.Target.For(extended.Definition.TdefPageNumber, byD);
        void ToSecondForm(int id, bool keyedByItsBytes)
        {
            byte[] first = ExtendedDateTimeCodec.Encode(DateOf(id)), second = InSeconds(DateOf(id));
            byte[] page = db.File.ReadPage(RowPointer.Page(e[id]));
            int at = Enumerable.Range(0, page.Length - first.Length).Single(i => page.AsSpan(i, first.Length).SequenceEqual(first));
            second.CopyTo(page, at);
            db.File.WritePage(RowPointer.Page(e[id]), page);
            if (!keyedByItsBytes) return;
            writer.Remove(target, IndexKeys.EncodeColumn(DataType.DateTimeExtended, ascending: true, DateOf(id)), e[id]);
            writer.Insert(target, KeyOfBytes(second), e[id]);
        }
        var ace = Enumerable.Range(Rows / 2, 20).ToList();
        var earlier = Enumerable.Range(Rows / 4, 20).ToList();
        foreach (int id in ace) ToSecondForm(id, keyedByItsBytes: true);
        foreach (int id in earlier) ToSecondForm(id, keyedByItsBytes: false);

        long Reads(Action change)
        {
            long before = db.File.ReadCount;
            change();
            return db.File.ReadCount - before;
        }
        foreach (int id in ace)
        {
            long extendedReads = Reads(() => extended.RewriteRowAt(e[id], new Row { ["Nt"] = "m" }));
            long plainReads = Reads(() => plain.RewriteRowAt(d[id], new Row { ["Nt"] = "m" }));
            Assert.True(extendedReads <= plainReads + 1, $"rewriting row {id} read {extendedReads} pages, a Date/Time row {plainReads}");
        }
        foreach (int id in ace.Take(10))
        {
            var later = DateOf(id).AddSeconds(1);
            long extendedReads = Reads(() => extended.RewriteRowAt(e[id], new Row { ["D"] = later }));
            long plainReads = Reads(() => plain.RewriteRowAt(d[id], new Row { ["D"] = later }));
            Assert.True(extendedReads <= plainReads + 1, $"rewriting row {id}'s date read {extendedReads} pages, a Date/Time row {plainReads}");
        }
        foreach (int id in ace.Skip(10))
        {
            long extendedReads = Reads(() => extended.DeleteRowAt(e[id]));
            long plainReads = Reads(() => plain.DeleteRowAt(d[id]));
            Assert.True(extendedReads <= plainReads + 1, $"deleting row {id} read {extendedReads} pages, a Date/Time row {plainReads}");
        }
        int wayDown = IndexChecks.Height(db, byD.RootPageNumber) + 1;
        foreach (int id in earlier)
        {
            long extendedReads = Reads(() => extended.DeleteRowAt(e[id]));
            long plainReads = Reads(() => plain.DeleteRowAt(d[id]));
            Assert.True(extendedReads <= plainReads + 1 + wayDown,
                $"deleting row {id}, under the key of its date read, read {extendedReads} pages, a Date/Time row {plainReads}");
        }
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "TE", expectedRows: Rows - 30));
    }
}
