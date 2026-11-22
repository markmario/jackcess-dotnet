using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// An index is added to a table that already has rows: it is built over them, kept on
/// every write after, and unique when asked - in a table this library made and in one Access made. An index the
/// rows do not allow, or whose keys this library does not write, is refused with the file byte for byte as it was.
/// The indexes are read back from the file's pages (<see cref="IndexChecks"/>); what Access makes of them is in
/// SchemaChangesInAccessTests.
/// </summary>
public sealed class AddIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"add_index_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ColumnDefinition[] Columns() => new[]
    {
        new ColumnDefinition("Id", DataType.Int32),
        new ColumnDefinition("Code", DataType.Text) { MaxLength = 40 },
        new ColumnDefinition("Grp", DataType.Int32),
        new ColumnDefinition("Seq", DataType.Int16),
        new ColumnDefinition("Note", DataType.Memo),
        new ColumnDefinition("Amount", DataType.Decimal),
    };

    // Every third row has no group and no sequence: an index that ignores nulls holds no entry for it.
    private static Row RowOf(int i) => new()
    {
        ["Id"] = i, ["Code"] = $"code {i:D6}", ["Grp"] = i % 3 == 0 ? null : i % 17, ["Seq"] = i % 3 == 0 ? null : (short)(i % 300),
        ["Note"] = $"note {i}",
    };

    // 1..count in an order that is not key order in any index.
    private static IEnumerable<int> Shuffled(int count) => Enumerable.Range(1, count).OrderBy(k => (k * 7919L) % 10007);

    private Database CreateWithRows(int rows)
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("Rows", Columns(), primaryKey: "Id");
        foreach (int i in Shuffled(rows)) table.Insert(RowOf(i));
        return db;
    }

    [Fact]
    public void An_index_added_to_a_table_holds_every_row_already_there()
    {
        using (var db = CreateWithRows(5_000))
        {
            var table = db.GetTable("Rows");
            var byCode = table.AddIndex(new IndexDefinition("ByCode", "Code") { IsUnique = true });
            table.AddIndex(new IndexDefinition("ByGroup", "Grp"));
            table.AddIndex(new IndexDefinition("ByGroupAndSeq", "Grp", "Seq") { DescendingColumns = { "Seq" }, IgnoreNulls = true });
            table.AddIndex(new IndexDefinition("ByNote", "Note"));

            Assert.True(byCode.IsUnique);
            Assert.Equal(new[] { "ByCode", "ByGroup", "ByGroupAndSeq", "ByNote", "PrimaryKey" }, table.Indexes.Select(ix => ix.Name).OrderBy(n => n));
            Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 5_000));
            // 5,000 entries do not fit a page: the trees were built to more than one level.
            Assert.All(table.Indexes, ix => Assert.True(IndexChecks.Height(db, ix.RootPageNumber) >= 1, $"{ix.Name} is one page"));
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Rows");
        Assert.Equal("Grp ASC, Seq DESC", string.Join(", ", read.Indexes.Single(ix => ix.Name == "ByGroupAndSeq").Columns));
        Assert.True(read.Indexes.Single(ix => ix.Name == "ByGroupAndSeq").IgnoreNulls);
        Assert.Equal(1_234, read.CreateIndexCursor("ByCode").FindRow("Code", "code 001234")?["Id"]);
        Assert.Equal(5, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Rows", expectedRows: 5_000));
    }

    [Fact]
    public void An_added_index_is_kept_as_rows_are_inserted_updated_and_deleted()
    {
        using var db = CreateWithRows(600);
        var earlier = db.GetTable("Rows");   // opened before the index was added: it keeps the index too
        db.GetTable("Rows").AddIndex(new IndexDefinition("ByCode", "Code") { IsUnique = true });
        db.GetTable("Rows").AddIndex(new IndexDefinition("ByGroup", "Grp"));

        for (int i = 601; i <= 900; i++) earlier.Insert(RowOf(i));
        for (int i = 1; i <= 900; i += 4) earlier.UpdateByPrimaryKey(i, new Row { ["Code"] = $"changed {i}", ["Grp"] = 1_000 + i });
        for (int i = 2; i <= 900; i += 4) earlier.DeleteRow("Id", i);

        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 675));
        var refused = Assert.Throws<ConstraintViolationException>(() => earlier.Insert(new Row { ["Id"] = 5_000, ["Code"] = "CHANGED 5" }));
        Assert.Contains("ByCode", refused.Message);
    }

    [Fact]
    public void An_index_like_one_the_table_has_shares_its_tree()
    {
        using var db = CreateWithRows(300);
        var table = db.GetTable("Rows");
        var first  = table.AddIndex(new IndexDefinition("ByGroup", "Grp"));
        var second = table.AddIndex(new IndexDefinition("AlsoByGroup", "Grp"));
        var other  = table.AddIndex(new IndexDefinition("UniqueByGroupAndId", "Grp", "Id") { IsUnique = true });

        Assert.Equal(first.RootPageNumber, second.RootPageNumber);
        Assert.NotEqual(first.RootPageNumber, other.RootPageNumber);
        Assert.NotEqual(first.IndexNumber, second.IndexNumber);
        // Four indexes beside the primary key's tree... three trees in all.
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 300));

        table.Insert(RowOf(301));
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 301));
    }

    // indexV2003.mdb's Table1: the primary key, an index and two relationships' indexes, four rows.
    [Theory]
    [InlineData("V2003", "indexV2003.mdb")]
    [InlineData("V2010", "indexV2010.accdb")]
    public void An_index_is_added_to_a_table_Access_made_and_kept_with_the_indexes_Access_gave_it(string version, string file)
    {
        string? source = TestCorpus.File(version, file);
        if (source is null) return;   // the corpus is optional locally; CI always has it
        File.Copy(source, _path, overwrite: true);

        using (var db = Database.Open(_path))
        {
            // The rows added below name no row of the tables Table1 refers to: this is about the indexes.
            db.EnforceForeignKeys = false;
            var table = db.GetTable("Table1");
            int trees = IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table1", expectedRows: 4);
            table.AddIndex(new IndexDefinition("byData", "data"));
            table.AddIndex(new IndexDefinition("Zed", "otherfk3", "data") { IsUnique = true });

            for (int i = 0; i < 500; i++)
                table.Insert(new Row { ["id"] = 100 + i, ["otherfk1"] = 1, ["otherfk2"] = 1, ["data"] = $"row {i}", ["otherfk3"] = i % 7 });
            Assert.Equal(trees + 2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table1", expectedRows: 504));
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Table1");
        // Access keeps a table's indexes in the order of their names; the new ones are where it would put them.
        Assert.Equal(new[] { "byData", "id", "PrimaryKey", "Table2Table1", "Table3Table1", "Zed" }, read.Indexes.Select(ix => ix.Name));
        Assert.Equal(Enumerable.Range(0, 6), read.Indexes.Select(ix => ix.IndexNumber).OrderBy(n => n));
        Assert.Equal(107, read.CreateIndexCursor("byData").FindRow("data", "row 7")?["id"]);
        IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Table1", expectedRows: 504);
    }

    // ── Refusals: nothing changes ────────────────────────────────────────────

    private void AssertRefusedAndUnchanged<TException>(Action<Table> change, string? says = null) where TException : Exception
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var pages = IndexChecks.AllPages(db);
            var table = db.GetTable("Rows");
            var names = table.Indexes.Select(ix => ix.Name).ToList();
            var refused = Assert.Throws<TException>(() => change(table));
            if (says is not null) Assert.Contains(says, refused.Message);
            IndexChecks.AssertUnchanged(pages, db);
            Assert.Equal(names, table.Indexes.Select(ix => ix.Name));
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_unique_index_is_refused_when_two_rows_have_the_same_values_and_nothing_changes()
    {
        using (var db = CreateWithRows(400))
            db.GetTable("Rows").UpdateByPrimaryKey(399, new Row { ["Code"] = "CODE 000007" });   // the same key as row 7's, in another case

        AssertRefusedAndUnchanged<ConstraintViolationException>(
            table => table.AddIndex(new IndexDefinition("ByCode", "Code") { IsUnique = true }), "unique");
    }

    [Fact]
    public void A_unique_index_takes_rows_that_are_the_same_only_in_a_null()
    {
        // Every third row has neither group nor sequence, and rows 1 and 18 are made group 1 with no sequence:
        // with a null in a column of the index, rows are duplicates of nothing. The rest differ in the two.
        using var db = CreateWithRows(400);
        var table = db.GetTable("Rows");
        table.UpdateByPrimaryKey(1, new Row { ["Seq"] = null });
        table.UpdateByPrimaryKey(18, new Row { ["Seq"] = null });

        table.AddIndex(new IndexDefinition("UniqueWithNulls", "Grp", "Seq") { IsUnique = true });
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 400));

        // And it is unique for the rows that have both.
        var refused = Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 401, ["Grp"] = 2, ["Seq"] = (short)2 }));
        Assert.Contains("UniqueWithNulls", refused.Message);
        table.Insert(new Row { ["Id"] = 402, ["Grp"] = 1, ["Seq"] = null });
    }

    [Fact]
    public void An_index_that_requires_its_values_is_refused_when_a_row_has_a_null_and_nothing_changes()
    {
        CreateWithRows(40).Dispose();
        AssertRefusedAndUnchanged<ConstraintViolationException>(
            table => table.AddIndex(new IndexDefinition("ByGroup", "Grp") { IsRequired = true }), "requires");
    }

    [Fact]
    public void An_index_on_a_column_whose_keys_are_not_written_is_refused_and_nothing_changes()
    {
        CreateWithRows(40).Dispose();
        AssertRefusedAndUnchanged<NotSupportedException>(table => table.AddIndex(new IndexDefinition("ByAmount", "Amount")));
        AssertRefusedAndUnchanged<NotSupportedException>(table => table.AddIndex(new IndexDefinition("ByGroupAndAmount", "Grp", "Amount")));
    }

    // With no row there is no key to fail to make: the column's type alone refuses the index.
    [Fact]
    public void An_index_on_a_column_whose_keys_are_not_written_is_refused_for_a_table_with_no_rows_too()
    {
        CreateWithRows(0).Dispose();
        AssertRefusedAndUnchanged<NotSupportedException>(table => table.AddIndex(new IndexDefinition("ByAmount", "Amount")), "not supported");
    }

    // ー and 々 are keyed by the character before them: an index built over values that hold them has their keys.
    // (In General their keys are not made, and such an index is refused: GeneralSortOrderTests.)
    [Fact]
    public void An_index_over_values_Access_keys_by_their_neighbours_holds_them()
    {
        using (var db = CreateWithRows(40))
        {
            var table = db.GetTable("Rows");
            table.UpdateByPrimaryKey(9, new Row { ["Code"] = "コーヒー" });
            table.UpdateByPrimaryKey(10, new Row { ["Code"] = "佐々木" });
            table.AddIndex(new IndexDefinition("ByCode", "Code") { IsUnique = true });
            Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 40));
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(9, reopened.GetTable("Rows").CreateIndexCursor("ByCode").FindRow("Code", "コーヒー")?["Id"]);
        Assert.Equal(10, reopened.GetTable("Rows").CreateIndexCursor("ByCode").FindRowByEntry("佐々木")?["Id"]);
    }

    [Fact]
    public void An_index_with_a_name_or_a_column_the_table_cannot_give_it_is_refused_and_nothing_changes()
    {
        CreateWithRows(40).Dispose();
        AssertRefusedAndUnchanged<ArgumentException>(table => table.AddIndex(new IndexDefinition("primarykey", "Grp")));
        AssertRefusedAndUnchanged<ArgumentException>(table => table.AddIndex(new IndexDefinition("ByNothing", "Nowhere")));
        AssertRefusedAndUnchanged<ArgumentException>(
            table => table.AddIndex(new IndexDefinition("Descending", "Grp") { DescendingColumns = { "Seq" } }));
    }

    // As Access refuses them: with such a name allowed, an index could take the name Access gives a
    // relationship's index on a referenced table (.rB, .rC...).
    [Theory]
    [InlineData(".rB")]
    [InlineData("a.b")]
    [InlineData("wow!")]
    [InlineData("[bracketed]")]
    [InlineData(" leading")]
    public void An_index_with_a_name_Access_does_not_allow_is_refused_and_nothing_changes(string name)
    {
        CreateWithRows(10).Dispose();
        AssertRefusedAndUnchanged<ArgumentException>(table => table.AddIndex(new IndexDefinition(name, "Grp")), "not a valid name");
    }

    [Fact]
    public void A_table_takes_thirty_two_indexes_and_the_next_is_refused()
    {
        using (var db = CreateWithRows(10))
        {
            var table = db.GetTable("Rows");
            for (int i = 1; i < IndexDefinition.MaxIndexesPerTable; i++)   // the primary key is the first
                table.AddIndex(new IndexDefinition($"Ix{i:D2}", i % 2 == 0 ? "Grp" : "Seq"));
            Assert.Equal(IndexDefinition.MaxIndexesPerTable, table.Indexes.Count);
            Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Rows", expectedRows: 10));
        }
        AssertRefusedAndUnchanged<ArgumentException>(table => table.AddIndex(new IndexDefinition("OneTooMany", "Code")), "at most");
    }

    [Fact]
    public void An_index_is_not_added_to_a_table_of_an_Access_97_file()
    {
        string? source = TestCorpus.File("V1997", "indexV1997.mdb");
        if (source is null) return;
        File.Copy(source, _path, overwrite: true);

        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
            Assert.Throws<NotSupportedException>(() => db.GetTable("Table1").AddIndex(new IndexDefinition("byData", "data")));
        Assert.Equal(before, File.ReadAllBytes(_path));
    }
}
