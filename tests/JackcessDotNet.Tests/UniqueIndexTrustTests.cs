using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// With <see cref="Database.VerifyIndexes"/>, which every database here is opened with, a unique index answers
/// for a duplicate only with the rows it was seen not to hold (qcatco/exportmdb.interop#73). Versions of this
/// library before it kept every index left rows out of a table's unique indexes, and a row whose primary key
/// they changed under the wrong value in the key: such an index alone says "no other row has this" of a value a
/// row has. So the first row a session checks against a table's unique indexes reads the table once, for all of
/// them, and the rows they do not hold are kept beside them. (Without the option an index is believed and
/// nothing is read: <see cref="TrustedIndexTests"/>.)
/// <para>
/// The rows are left out here by <see cref="IncompleteIndexTests.InsertAsEarlierReleasesDid"/>, not by the earlier
/// code; <see cref="LegacyUniqueIndexTests"/> has a file the earlier code itself wrote to. And what it costs a
/// table whose indexes are whole is held here: one reading of the table, then nothing.
/// </para>
/// </summary>
public sealed class UniqueIndexTrustTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"unique_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private Database OpenVerifying()
    {
        var db = Database.Open(_path);
        db.VerifyIndexes = true;
        return db;
    }

    private static Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();
    private static Column Text(string name, int chars = 20) => new ColumnBuilder(name, DataType.Text).MaxLength(chars).Build();

    private List<string> Rows(string table)
    {
        using var db = OpenVerifying();
        return db.GetTable(table).ReadAllRows()
            .Select(row => string.Join(", ", row.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}")))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();
    }

    private string Refused(Action<Database> change)
    {
        byte[] before = File.ReadAllBytes(_path);
        string message;
        using (var db = OpenVerifying())
            message = Assert.Throws<ConstraintViolationException>(() => change(db)).Message;
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused change altered the file");
        return message;
    }

    // ── Rows a unique index does not hold ────────────────────────────────────

    /// <summary>
    /// Pairs(Id, A, B, Name): Id the primary key, (A, B) unique together. Rows 1 (1, x) and 2 (2, y) as this
    /// library writes them, and 3 (3, z) as an earlier release added a row.
    /// </summary>
    private void Pairs()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var pairs = db.CreateTable("Pairs", new[] { Long("Id"), Long("A"), Text("B"), Text("Name") }, new[] { "Id" },
                                   new[] { new IndexDefinition("OnePair", "A", "B") { IsUnique = true } });
        pairs.Insert(new Row { ["Id"] = 1, ["A"] = 1, ["B"] = "x", ["Name"] = "one" });
        pairs.Insert(new Row { ["Id"] = 2, ["A"] = 2, ["B"] = "y", ["Name"] = "two" });
        IncompleteIndexTests.InsertAsEarlierReleasesDid(pairs, "Id", new Row { ["Id"] = 3, ["A"] = 3, ["B"] = "z", ["Name"] = "three" });
    }

    [Fact]
    public void a_duplicate_of_a_row_left_out_of_a_unique_index_is_refused_on_insert_on_update_and_when_checked()
    {
        Pairs();

        string message = Refused(db => db.GetTable("Pairs").Insert(new Row { ["Id"] = 4, ["A"] = 3, ["B"] = "z" }));
        Assert.Contains("duplicate", message);
        Assert.Contains("OnePair", message);

        // Text is keyed without its case, in an index and out of it.
        Refused(db => db.GetTable("Pairs").Insert(new Row { ["Id"] = 4, ["A"] = 3, ["B"] = "Z" }));
        Refused(db => db.GetTable("Pairs").UpdateByPrimaryKey(1, new Row { ["A"] = 3, ["B"] = "z" }));
        Refused(db => db.GetTable("Pairs").Validate(new Row { ["Id"] = 4, ["A"] = 3, ["B"] = "z" }));

        Assert.Equal(new[] { "A=1, B=x, Id=1, Name=one", "A=2, B=y, Id=2, Name=two", "A=3, B=z, Id=3, Name=three" }, Rows("Pairs"));
    }

    [Fact]
    public void a_row_that_differs_from_the_one_left_out_or_has_a_null_is_taken()
    {
        Pairs();

        using (var db = OpenVerifying())
        {
            var pairs = db.GetTable("Pairs");
            pairs.Insert(new Row { ["Id"] = 4, ["A"] = 3, ["B"] = "y" });
            pairs.Insert(new Row { ["Id"] = 5, ["A"] = 3 });   // a null in a unique index is no row's duplicate
            pairs.UpdateByPrimaryKey(3, new Row { ["Name"] = "still three" });   // its own values are not another row's
        }

        Assert.Equal(5, Rows("Pairs").Count);
        using var read = OpenVerifying();
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Pairs", expectedRows: 5);
    }

    // ── What it costs a table whose indexes are whole ────────────────────────

    private const int Many = 20_000;

    /// <summary>A table of <see cref="Many"/> rows, some 600 pages of them, with the indexes <paramref name="indexes"/>.</summary>
    private void Filled(string[]? primaryKey, params IndexDefinition[] indexes)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", new[] { Long("Id"), Long("Code"), Text("Name", 100) }, primaryKey, indexes);
        for (int i = 0; i < Many; i++)
            table.Insert(new Row { ["Id"] = i, ["Code"] = Many - i, ["Name"] = new string('n', 100) });
    }

    private static int PagesOf(Database db, string table)
    {
        var definition = db.GetTable(table).Definition;
        return UsageMap.GetOwnedPages(db.File.ReadPage(definition.UmapPageNumber), definition.OwnedPagesRow, db.File.Format, db.File).Count;
    }

    private static long ReadsOf(Database db, Action change)
    {
        long before = db.File.ReadCount;
        change();
        return db.File.ReadCount - before;
    }

    private static Row RowOf(int id) => new() { ["Id"] = id, ["Code"] = id, ["Name"] = "added" };

    /// <summary>
    /// The first insert of a session reads the table - once, for the primary key and the unique index together -
    /// and their leaf pages. No insert after it reads the table, through whichever <see cref="Table"/> it is made.
    /// </summary>
    [Fact]
    public void a_table_s_unique_indexes_are_checked_once_by_one_reading_of_the_table_and_then_cost_an_insert_nothing()
    {
        Filled(new[] { "Id" }, new IndexDefinition("OneCode", "Code") { IsUnique = true });

        using (var db = OpenVerifying())
        {
            int pages = PagesOf(db, "T");
            Assert.True(pages > 500, $"the table is only {pages} pages");

            long first = ReadsOf(db, () => db.GetTable("T").Insert(RowOf(Many + 1)));
            Assert.True(first >= pages, $"the first insert read {first} pages: fewer than the table's {pages}, so its unique indexes were not checked");
            Assert.True(first < pages + pages / 2, $"the first insert read {first} pages of a table of {pages}: the table was read more than once");

            for (int i = 2; i <= 40; i++)
            {
                long reads = ReadsOf(db, () => db.GetTable("T").Insert(RowOf(Many + i)));
                Assert.True(reads <= InsertReads, $"insert {i} of the session read {reads} pages");
            }
            Assert.True(ReadsOf(db, () => db.GetTable("T").Validate(RowOf(Many + 100))) <= InsertReads);
            Assert.Throws<ConstraintViolationException>(() => db.GetTable("T").Insert(RowOf(Many + 7)));
        }

        using var read = OpenVerifying();
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "T", expectedRows: Many + 40);
    }

    // What an insert into a table with two indexes reads: the definition, the row's page and the usage map, and
    // each index from its root down. Far under the pages of the table.
    private const int InsertReads = 16;

    [Fact]
    public void a_table_with_no_unique_index_is_not_read()
    {
        Filled(primaryKey: null, new IndexDefinition("ByCode", "Code"));

        using var db = OpenVerifying();
        var table = db.GetTable("T");
        for (int i = 1; i <= 5; i++)
        {
            long reads = ReadsOf(db, () => table.Insert(RowOf(Many + i)));
            Assert.True(reads <= InsertReads, $"insert {i} of the session read {reads} pages");
        }
    }

    [Fact]
    public void a_row_with_a_null_in_its_unique_index_is_inserted_without_reading_the_table()
    {
        Filled(primaryKey: null, new IndexDefinition("OneCode", "Code") { IsUnique = true });

        using var db = OpenVerifying();
        long reads = ReadsOf(db, () => db.GetTable("T").Insert(new Row { ["Id"] = Many + 1, ["Name"] = "no code" }));
        Assert.True(reads <= InsertReads, $"the insert read {reads} pages");
    }

    /// <summary>A table made and filled in one session is checked when it is empty: filling it reads it at no time.</summary>
    [Fact]
    public void filling_a_new_table_never_reads_it()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.VerifyIndexes = true;
        var table = db.CreateTable("T", new[] { Long("Id"), Long("Code"), Text("Name", 100) }, new[] { "Id" },
                                   new[] { new IndexDefinition("OneCode", "Code") { IsUnique = true } });
        for (int i = 0; i < 2_000; i++)
        {
            long reads = ReadsOf(db, () => table.Insert(new Row { ["Id"] = i, ["Code"] = i, ["Name"] = new string('n', 100) }));
            Assert.True(reads <= InsertReads, $"insert {i} read {reads} pages");
        }
    }
}
