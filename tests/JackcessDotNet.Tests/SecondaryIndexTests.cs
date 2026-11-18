using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table is created with indexes beside its primary key - on one column or several, ascending or descending,
/// unique or not (qcatco/exportmdb.interop#35) - and every one of them is kept as its rows are inserted, updated
/// and deleted (#27). A unique index refuses a duplicate, and an index that requires its values a null, before
/// anything of the row is written. The indexes are read back from the file's pages (<see cref="IndexChecks"/>).
/// </summary>
public sealed class SecondaryIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"secondary_ix_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Column[] Columns() => new[]
    {
        new ColumnBuilder("Id", DataType.Long).Build(),
        new ColumnBuilder("Code", DataType.Text).MaxLength(40).Build(),
        new ColumnBuilder("Grp", DataType.Long).Build(),
        new ColumnBuilder("Seq", DataType.Int).Build(),
        new ColumnBuilder("Note", DataType.Memo).Build(),
    };

    private static IndexDefinition[] Indexes() => new[]
    {
        new IndexDefinition("ByCode", "Code") { IsUnique = true },
        new IndexDefinition("ByGroup", "Grp"),
        new IndexDefinition("ByGroupAndSeq", "Grp", "Seq") { DescendingColumns = new[] { "Seq" } },
    };

    private static Table Indexed(Database db) => db.CreateTable("Indexed", Columns(), new[] { "Id" }, Indexes());

    private static Row RowOf(int i) => new()
    {
        ["Id"] = i, ["Code"] = $"code {i:D6}", ["Grp"] = i % 17, ["Seq"] = (short)(i % 300), ["Note"] = $"note {i}",
    };

    // 1..count in an order that is not key order in any index.
    private static IEnumerable<int> Shuffled(int count) => Enumerable.Range(1, count).OrderBy(k => (k * 7919L) % 10007);

    [Fact]
    public void A_created_table_has_the_indexes_it_was_given()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            Indexed(db);

        using var reopened = Database.Open(_path);
        var indexes = reopened.GetTable("Indexed").Indexes;
        Assert.Equal(new[] { "PrimaryKey", "ByCode", "ByGroup", "ByGroupAndSeq" }, indexes.Select(ix => ix.Name));
        Assert.Equal(new[] { true, true, false, false }, indexes.Select(ix => ix.IsUnique));
        Assert.Equal(new[] { true, false, false, false }, indexes.Select(ix => ix.IsPrimaryKey));
        Assert.Equal(new[] { "Id ASC", "Code ASC", "Grp ASC", "Grp ASC, Seq DESC" }, indexes.Select(ix => string.Join(", ", ix.Columns)));
        Assert.Equal(4, indexes.Select(ix => ix.RootPageNumber).Distinct().Count());
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Indexed", expectedRows: 0));
    }

    [Fact]
    public void The_table_builder_gives_a_table_its_indexes()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = new TableBuilder("Built")
            .AddColumn("Id", DataType.Long)
            .AddColumn("Name", DataType.Text, c => c.MaxLength(20))
            .WithPrimaryKey("Id")
            .WithIndex(new IndexDefinition("ByName", "Name") { IsUnique = true })
            .ToTable(db);

        table.Insert(new Row { ["Id"] = 1, ["Name"] = "one" });

        Assert.Equal(new[] { "PrimaryKey", "ByName" }, table.Indexes.Select(ix => ix.Name));
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 2, ["Name"] = "ONE" }));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Built", expectedRows: 1));
    }

    [Fact]
    public void Inserted_rows_are_entered_in_every_index()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(2_000))
            table.Insert(RowOf(i));

        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 2_000));
    }

    [Fact]
    public void Deleted_rows_leave_every_index()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(1_500))
            table.Insert(RowOf(i));

        for (int i = 301; i <= 1_200; i++)   // whole leaves of every index empty
            table.DeleteRow("Id", i);

        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 600);
    }

    [Fact]
    public void An_updated_row_moves_in_every_index_under_its_new_values()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(500))
            table.Insert(RowOf(i));

        for (int i = 1; i <= 500; i += 3)
            table.UpdateByPrimaryKey(i, new Row { ["Code"] = $"changed {i}", ["Grp"] = 1_000 + i, ["Seq"] = null });
        table.UpdateByPrimaryKey(2, new Row { ["Note"] = "only the note" });   // no indexed value changes: its entries still move

        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 500);
        var byCode = db.GetTable("Indexed").NewIndexCursor("ByCode");
        Assert.Equal(1_004, byCode.FindRow("Code", "changed 4")?["Grp"]);
        Assert.Null(byCode.FindRow("Code", "code 000004"));
        Assert.Equal("only the note", byCode.FindRow("Code", "code 000002")?["Note"]);
    }

    [Fact]
    public void A_row_is_found_through_an_index_on_several_columns()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(800))
            table.Insert(RowOf(i));

        var cursor = db.GetTable("Indexed").NewIndexCursor("ByGroupAndSeq");
        var found = cursor.FindRowByEntry(321 % 17, (short)(321 % 300));
        Assert.NotNull(found);
        Assert.Equal(321 % 17, found!["Grp"]);
        Assert.Equal((short)21, found["Seq"]);
        Assert.Null(cursor.FindRowByEntry(5, (short)299));   // no row has group 5 with sequence 299

        // Descending on its second column: within a group, the entries run from the highest sequence down.
        var index = db.GetTable("Indexed").Indexes.Single(ix => ix.Name == "ByGroupAndSeq");
        var rows = JetPages.Rows(db.File, IndexChecks.TdefOf(db, "Indexed")).ToDictionary(r => (r.Page, r.Row), r => r.Values);
        var inOrder = JetPages.LeafEntries(db.File, index.RootPageNumber).Select(e => rows[(e.RowPage, e.Row)]).ToList();
        Assert.Equal(inOrder.OrderBy(r => (int)r["Grp"]!).ThenByDescending(r => (short)r["Seq"]!).Select(r => ((int)r["Grp"]!, (short)r["Seq"]!)),
                     inOrder.Select(r => ((int)r["Grp"]!, (short)r["Seq"]!)));
    }

    [Fact]
    public void A_duplicate_in_a_unique_index_is_refused_before_anything_is_written()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(300))
            table.Insert(RowOf(i));
        var before = IndexChecks.AllPages(db);

        // A new primary key and a long memo, whose pages would be written first: the code is row 77's.
        var refused = Assert.Throws<InvalidOperationException>(() => table.Insert(new Row
        {
            ["Id"] = 9_000, ["Code"] = "CODE 000077", ["Grp"] = 1, ["Seq"] = (short)1, ["Note"] = new string('n', 20_000),
        }));

        Assert.Contains("ByCode", refused.Message);
        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(300, db.GetTable("Indexed").ReadAllRows().Count);
    }

    [Fact]
    public void A_duplicate_primary_key_is_refused_before_anything_is_written()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(300))
            table.Insert(RowOf(i));
        var before = IndexChecks.AllPages(db);

        var refused = Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 150, ["Code"] = "not taken" }));

        Assert.Contains("primary key", refused.Message);
        IndexChecks.AssertUnchanged(before, db);
    }

    [Fact]
    public void An_update_that_would_duplicate_a_unique_value_is_refused_and_the_row_kept()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(300))
            table.Insert(RowOf(i));
        var before = IndexChecks.AllPages(db);

        Assert.Throws<InvalidOperationException>(() => table.UpdateByPrimaryKey(10, new Row { ["Code"] = "code 000011", ["Note"] = new string('n', 20_000) }));
        Assert.Throws<InvalidOperationException>(() => table.UpdateByPrimaryKey(10, new Row { ["Id"] = 11 }));
        IndexChecks.AssertUnchanged(before, db);

        // Its own value is not another row's: the row is rewritten with the code it had.
        table.UpdateByPrimaryKey(10, new Row { ["Code"] = "code 000010", ["Grp"] = 99 });
        // A code no longer taken is free again.
        table.UpdateByPrimaryKey(11, new Row { ["Code"] = "moved" });
        table.UpdateByPrimaryKey(10, new Row { ["Code"] = "code 000011" });
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 300);
    }

    [Fact]
    public void Validate_refuses_what_an_insert_would_without_writing()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(100))
            table.Insert(RowOf(i));
        var before = IndexChecks.AllPages(db);

        table.Validate(RowOf(101));
        Assert.Throws<InvalidOperationException>(() => table.Validate(new Row { ["Id"] = 101, ["Code"] = "code 000050" }));   // a duplicate code
        Assert.Throws<InvalidOperationException>(() => table.Validate(new Row { ["Id"] = 50, ["Code"] = "new" }));            // a duplicate key
        Assert.Throws<InvalidOperationException>(() => table.Validate(new Row { ["Code"] = "new" }));                         // no key
        Assert.Throws<ColumnValueTooLongException>(() => table.Validate(new Row { ["Id"] = 101, ["Code"] = new string('c', 41) }));

        IndexChecks.AssertUnchanged(before, db);
    }

    // The checks are of the row as it will be written, its AutoNumber in place; a refusal leaves the counter where
    // it was and the caller's row as it came, so the next row is given the number.
    [Fact]
    public void A_refused_row_of_a_numbered_table_costs_no_number_and_is_handed_back_as_it_came()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Numbered", new[]
        {
            new ColumnBuilder("Id", DataType.Long).AutoNumber().Build(),
            new ColumnBuilder("Code", DataType.Text).MaxLength(20).Build(),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") { IsUnique = true } });
        var first = new Row { ["Code"] = "a" };
        table.Insert(first);          // no Id given: the primary key is the number the table gives
        table.Insert(new Row { ["Code"] = "b" });
        Assert.Equal(1, first["Id"]);
        var before = IndexChecks.AllPages(db);

        var duplicate = new Row { ["Code"] = "A" };
        Assert.Throws<InvalidOperationException>(() => table.Insert(duplicate));
        var withAnId = new Row { ["Id"] = 77, ["Code"] = "b" };
        Assert.Throws<InvalidOperationException>(() => table.Insert(withAnId));
        table.Validate(new Row { ["Code"] = "c" });   // no Id: checked with the one it would be given
        Assert.Throws<InvalidOperationException>(() => table.Validate(new Row { ["Code"] = "B" }));

        Assert.False(duplicate.ContainsKey("Id"));
        Assert.Equal(77, withAnId["Id"]);
        Assert.Equal(2, table.LastAutoNumber);
        IndexChecks.AssertUnchanged(before, db);

        var next = new Row { ["Code"] = "c" };
        table.Insert(next);
        Assert.Equal(3, next["Id"]);

        // A row that brings a number another row has is a duplicate in the primary key.
        table.AllowAutoNumberInsert = true;
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 2, ["Code"] = "d" }));
        Assert.Equal(3, table.LastAutoNumber);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Numbered", expectedRows: 3));
    }

    // The search that finds no duplicate reads the pages the entry then goes down: they are read once.
    [Fact]
    public void A_unique_index_costs_an_insert_no_more_page_reads_than_one_that_is_not()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        Column[] Two() => new[] { new ColumnBuilder("Id", DataType.Long).Build(), new ColumnBuilder("Txt", DataType.Text).MaxLength(30).Build() };
        var unique = db.CreateTable("Unique", Two(), primaryKeyColumns: null, new[] { new IndexDefinition("ById", "Id") { IsUnique = true } });
        var plain  = db.CreateTable("Plain", Two(), primaryKeyColumns: null, new[] { new IndexDefinition("ById", "Id") });
        var keyed  = db.CreateTable("Keyed", Two(), primaryKey: "Id");
        foreach (int i in Shuffled(5_000))
            foreach (var table in new[] { unique, plain, keyed })
                table.Insert(new Row { ["Id"] = i, ["Txt"] = $"row {i}" });

        long ReadsFor(Table table)
        {
            long before = db.File.ReadCount;
            for (int i = 5_001; i <= 5_200; i++)
                table.Insert(new Row { ["Id"] = (i * 7919) % 100_003 + 5_000, ["Txt"] = $"row {i}" });
            return db.File.ReadCount - before;
        }

        long plainReads = ReadsFor(plain);
        Assert.Equal(plainReads, ReadsFor(unique));
        Assert.Equal(plainReads, ReadsFor(keyed));
        Assert.Throws<InvalidOperationException>(() => unique.Insert(new Row { ["Id"] = 17, ["Txt"] = "twice" }));
    }

    // An entry left behind for a row long gone can point at a page that has since become another table's. The row
    // found there is not this table's, and is no duplicate.
    [Fact]
    public void A_stale_entry_pointing_into_another_table_s_page_is_not_a_duplicate()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        table.Insert(RowOf(1));
        var other = db.CreateTable("Other", new[] { new ColumnBuilder("N", DataType.Long).Build() });
        other.Insert(new Row { ["N"] = 1 });
        var (otherPage, otherRow, _) = JetPages.Rows(db.File, IndexChecks.TdefOf(db, "Other")).Single();

        // The stale entry: the code "taken", held for a row on Other's page.
        var byCode = table.Indexes.Single(ix => ix.Name == "ByCode");
        new IndexWriter(db.File, new PageAllocator(db.File)).Insert(
            IndexWriter.Target.For(table.Definition.TdefPageNumber, byCode),
            IndexKeys.Encode(byCode.Columns, new object?[] { "taken" }), RowPointer.Pack(otherPage, otherRow));

        table.Insert(new Row { ["Id"] = 2, ["Code"] = "taken" });

        Assert.Equal(2, table.ReadAllRows().Count);
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 3, ["Code"] = "TAKEN" }));   // now it is a row's
    }

    // A key no index page could hold three of is refused with the row, before anything is written - not found
    // out when a page will not split, with the row already in the table and in some of its indexes.
    [Fact]
    public void A_row_whose_key_is_too_long_for_an_index_page_is_refused_before_anything_is_written()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Wide", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("A", DataType.Text).MaxLength(255).Build(),
            new ColumnBuilder("B", DataType.Text).MaxLength(255).Build(),
            new ColumnBuilder("C", DataType.Text).MaxLength(255).Build(),
            new ColumnBuilder("D", DataType.Text).MaxLength(255).Build(),
            new ColumnBuilder("E", DataType.Text).MaxLength(255).Build(),
        }, new[] { "Id" }, new[] { new IndexDefinition("ById", "Id"), new IndexDefinition("ByAll", "A", "B", "C", "D", "E") });
        string Text(int seed, int length) => new string(Enumerable.Range(0, length).Select(i => (char)('a' + (i * 7 + seed) % 26)).ToArray());
        for (int i = 1; i <= 40; i++)
            table.Insert(new Row { ["Id"] = i, ["A"] = Text(i, 255), ["B"] = Text(i + 1, 100), ["C"] = "c" });
        var before = IndexChecks.AllPages(db);

        var refused = Assert.Throws<InvalidOperationException>(() =>
            table.Insert(new Row { ["Id"] = 41, ["A"] = Text(1, 255), ["B"] = Text(2, 255), ["C"] = Text(3, 255), ["D"] = Text(4, 255), ["E"] = Text(5, 255) }));
        Assert.Contains("ByAll", refused.Message);
        Assert.Throws<InvalidOperationException>(() => table.UpdateByPrimaryKey(5, new Row { ["B"] = Text(2, 255), ["C"] = Text(3, 255), ["D"] = Text(4, 255), ["E"] = Text(5, 255) }));

        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Wide", expectedRows: 40));
    }

    // A Yes/No given as a number or as text is read once, for the row and for its key: 1 is Yes in both. (The row
    // encoder used to store No for anything but a bool true, while the key was made for Yes.)
    [Fact]
    public void A_Yes_No_given_as_a_number_or_as_text_is_stored_and_keyed_as_the_same_value()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Flags", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Flag", DataType.Boolean).Build(),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByFlag", "Flag") { IsUnique = true } });
        bool Stored(int id) => (bool)table.ReadAllRows().Single(r => (int)r["Id"]! == id)["Flag"]!;

        var one = new Row { ["Id"] = 1, ["Flag"] = 1 };
        table.Insert(one);
        Assert.True(Stored(1));
        Assert.Equal(1, one["Flag"]);   // the caller's row is as it came
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 2, ["Flag"] = true }));
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 2, ["Flag"] = "True" }));
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 2, ["Flag"] = -0.5 }));

        table.Insert(new Row { ["Id"] = 2, ["Flag"] = "false" });
        Assert.False(Stored(2));
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 3, ["Flag"] = 0 }));
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 3 }));                          // no value: No
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 3, ["Flag"] = DBNull.Value }));
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 3, ["Flag"] = "yes" }));       // only "true" is Yes
        Assert.Throws<InvalidOperationException>(() => table.UpdateByPrimaryKey(2, new Row { ["Flag"] = 7m }));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Flags", expectedRows: 2));

        table.DeleteRow("Id", 1);        // its entry goes: the key it was entered under is the key of what is stored
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Flags", expectedRows: 1));
        table.Insert(new Row { ["Id"] = 4, ["Flag"] = 1L });
        Assert.True(Stored(4));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Flags", expectedRows: 2));
    }

    // DBNull is no value, for the row and for its key alike, in a column of any type. (It used to be stored as an
    // empty string in a Text column and keyed as null: DBNull and then "" went into a unique index as two empty
    // strings.)
    [Fact]
    public void DBNull_is_stored_and_keyed_as_no_value()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Values", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Code", DataType.Text).MaxLength(20).Build(),
            new ColumnBuilder("N", DataType.Long).Build(),
            new ColumnBuilder("D", DataType.ShortDateTime).Build(),
            new ColumnBuilder("M", DataType.Memo).Build(),
        }, new[] { "Id" }, new[]
        {
            new IndexDefinition("ByCode", "Code") { IsUnique = true },
            new IndexDefinition("ByN", "N"),
            new IndexDefinition("ByD", "D"),
        });

        var given = new Row { ["Id"] = 1, ["Code"] = DBNull.Value, ["N"] = DBNull.Value, ["D"] = DBNull.Value, ["M"] = DBNull.Value };
        table.Insert(given);
        Assert.Equal(DBNull.Value, given["Code"]);   // the caller's row is as it came
        table.Insert(new Row { ["Id"] = 2, ["Code"] = "" });
        table.Insert(new Row { ["Id"] = 3, ["Code"] = null });
        table.Insert(new Row { ["Id"] = 4, ["Code"] = DBNull.Value });      // no value is no duplicate
        table.Validate(new Row { ["Id"] = 5, ["Code"] = DBNull.Value });
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = 5, ["Code"] = "" }));   // an empty string is a value
        Assert.Throws<InvalidOperationException>(() => table.Validate(new Row { ["Id"] = 5, ["Code"] = "" }));

        // Stored as no value...
        var rows = table.ReadAllRows().ToDictionary(r => (int)r["Id"]!);
        Assert.Equal(new[] { "Id" }, rows[1].Keys);
        Assert.Equal("", rows[2]["Code"]);
        Assert.False(rows[3].ContainsKey("Code"));
        Assert.False(rows[4].ContainsKey("Code"));
        // ...and keyed as none: the index holds what a scan finds.
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Values", expectedRows: 4));
        var byCode = db.GetTable("Values").Indexes.Single(ix => ix.Name == "ByCode");
        Assert.Equal(3, JetPages.LeafEntries(db.File, byCode.RootPageNumber).Count(e => e.Hex == "00"));
        Assert.Equal(2, db.GetTable("Values").NewIndexCursor("ByCode").FindRow("Code", "")?["Id"]);

        // An update's DBNull takes the value away, and its key with it.
        table.UpdateByPrimaryKey(2, new Row { ["Code"] = DBNull.Value, ["N"] = DBNull.Value });
        table.Insert(new Row { ["Id"] = 5, ["Code"] = "" });
        Assert.Equal(4, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Values", expectedRows: 5));
        Assert.Equal(5, db.GetTable("Values").NewIndexCursor("ByCode").FindRow("Code", "")?["Id"]);
    }

    [Fact]
    public void A_deleted_row_s_unique_value_can_be_given_again()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Indexed(db);
        foreach (int i in Shuffled(50))
            table.Insert(RowOf(i));

        table.DeleteRow("Id", 20);
        table.Insert(new Row { ["Id"] = 20, ["Code"] = "code 000020", ["Grp"] = 3 });

        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Indexed", expectedRows: 50);
    }

    // As Access: a row with a null in any column of a unique index is no row's duplicate (Java Jackcess
    // refuses a second row unless every column is null).
    [Fact]
    public void A_null_in_a_unique_index_is_not_a_duplicate()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Pairs", new[]
        {
            new ColumnBuilder("A", DataType.Long).Build(),
            new ColumnBuilder("B", DataType.Long).Build(),
            new ColumnBuilder("T", DataType.Text).MaxLength(20).Build(),
        }, primaryKeyColumns: null, new[]
        {
            new IndexDefinition("AB", "A", "B") { IsUnique = true },
            new IndexDefinition("T", "T") { IsUnique = true },
        });

        table.Insert(new Row { ["A"] = 1, ["B"] = null, ["T"] = "x" });
        table.Insert(new Row { ["A"] = 1, ["B"] = null, ["T"] = null });
        table.Insert(new Row { ["A"] = null, ["B"] = null, ["T"] = null });
        table.Insert(new Row());
        table.Insert(new Row { ["A"] = 1, ["B"] = 2 });

        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["A"] = 1, ["B"] = 2 }));
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["A"] = 5, ["B"] = 5, ["T"] = "X" }));   // "x", as Access compares text
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Pairs", expectedRows: 5));
    }

    [Fact]
    public void An_index_that_requires_its_values_refuses_a_null_and_one_that_ignores_nulls_has_no_entry_for_them()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Nulls", new[]
        {
            new ColumnBuilder("A", DataType.Long).Build(),
            new ColumnBuilder("B", DataType.Long).Build(),
            new ColumnBuilder("C", DataType.Long).Build(),
        }, primaryKeyColumns: null, new[]
        {
            new IndexDefinition("Required", "A") { IsRequired = true },
            new IndexDefinition("Sparse", "B", "C") { IgnoreNulls = true },
        });
        table.Insert(new Row { ["A"] = 1, ["B"] = 1, ["C"] = 1 });
        table.Insert(new Row { ["A"] = 2 });
        table.Insert(new Row { ["A"] = 3, ["C"] = 3 });
        var before = IndexChecks.AllPages(db);

        var refused = Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["B"] = 4, ["C"] = 4 }));

        Assert.Contains("Required", refused.Message);
        IndexChecks.AssertUnchanged(before, db);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Nulls", expectedRows: 3);
        var indexes = db.GetTable("Nulls").Indexes;
        Assert.Equal(3, JetPages.LeafEntries(db.File, indexes.Single(ix => ix.Name == "Required").RootPageNumber).Count);
        Assert.Equal(2, JetPages.LeafEntries(db.File, indexes.Single(ix => ix.Name == "Sparse").RootPageNumber).Count);   // not the row with neither B nor C

        table.DeleteRow("A", 2);   // the row with no entry in Sparse
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Nulls", expectedRows: 2);
    }

    [Fact]
    public void A_large_table_s_indexes_split_to_several_levels_and_shrink_again()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Large", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(120).Build(),
            new ColumnBuilder("Grp", DataType.Long).Build(),
        }, new[] { "Id" }, new[]
        {
            // Long keys that share little: few to a page, so the tree is deep with few rows.
            new IndexDefinition("ByName", "Name") { IsUnique = true },
            new IndexDefinition("ByGroupAndName", "Grp", "Name") { DescendingColumns = new[] { "Name" } },
        });
        const int Count = 12_000;
        string NameOf(int i) => $"{(i * 7919L) % 100_003:D6} {new string((char)('a' + i % 26), 90)} {i}";
        foreach (int i in Shuffled(Count))
            table.Insert(new Row { ["Id"] = i, ["Name"] = NameOf(i), ["Grp"] = i % 5 });

        var indexes = db.GetTable("Large").Indexes;
        Assert.True(IndexChecks.Height(db, indexes.Single(ix => ix.Name == "ByName").RootPageNumber) >= 2, "ByName is not deeper than one level of nodes");
        Assert.True(IndexChecks.Height(db, indexes.Single(ix => ix.Name == "ByGroupAndName").RootPageNumber) >= 2);
        Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Large", expectedRows: Count));
        Assert.Equal(4_321, db.GetTable("Large").NewIndexCursor("ByName").FindRow("Name", NameOf(4_321))?["Id"]);
        Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["Id"] = Count + 1, ["Name"] = NameOf(77).ToUpperInvariant() }));

        foreach (int i in Shuffled(Count).Take(9_000))   // in the order they went in: each is the first row left
            table.DeleteRow("Id", i);

        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Large", expectedRows: Count - 9_000);
    }

    [Fact]
    public void An_index_that_cannot_be_made_refuses_the_table_before_a_page_is_added()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var columns = new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Amount", DataType.Numeric).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(20).Build(),
        };
        var before = IndexChecks.AllPages(db);

        // A type whose keys the engine does not write, a column the table has not, a name given twice,
        // a descending column that is not the index's.
        Assert.Throws<NotSupportedException>(() => db.CreateTable("T", columns, new[] { "Id" }, new[] { new IndexDefinition("ByAmount", "Amount") }));
        Assert.Throws<InvalidOperationException>(() => db.CreateTable("T", columns, new[] { "Id" }, new[] { new IndexDefinition("ByNothing", "Missing") }));
        Assert.Throws<ArgumentException>(() => db.CreateTable("T", columns, new[] { "Id" },
            new[] { new IndexDefinition("ByName", "Name"), new IndexDefinition("byname", "Id") }));
        Assert.Throws<ArgumentException>(() => db.CreateTable("T", columns, new[] { "Id" }, new[] { new IndexDefinition("PrimaryKey", "Name") }));
        Assert.Throws<ArgumentException>(() => db.CreateTable("T", columns, null,
            new[] { new IndexDefinition("ByName", "Name") { DescendingColumns = new[] { "Id" } } }));
        Assert.Throws<ArgumentException>(() => db.CreateTable("T", columns, null,
            Enumerable.Range(0, 33).Select(i => new IndexDefinition($"Ix{i}", "Name")).ToArray()));
        Assert.Throws<ArgumentException>(() => new IndexDefinition("Twice", "Name", "name"));
        Assert.Throws<ArgumentException>(() => new IndexDefinition("None"));

        IndexChecks.AssertUnchanged(before, db);
        Assert.DoesNotContain("T", db.ListTables());
    }

    [Fact]
    public void A_table_can_have_thirty_two_indexes()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Many", Columns(), new[] { "Id" },
            Enumerable.Range(1, 31).Select(i => new IndexDefinition($"Index {i}", i % 2 == 0 ? "Code" : "Grp")).ToArray());
        foreach (int i in Shuffled(200))
            table.Insert(RowOf(i));

        Assert.Equal(32, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Many", expectedRows: 200));
    }
}
