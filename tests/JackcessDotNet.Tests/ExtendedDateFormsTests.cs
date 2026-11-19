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
}
