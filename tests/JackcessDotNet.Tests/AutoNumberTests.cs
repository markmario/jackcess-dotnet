using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// An insert fills a table's AutoNumber columns as Access does: a Long one from the counter the table's definition
/// keeps - so the next session, and Access, go on from it - and a Guid one with a new GUID. The row handed in holds
/// the values afterwards. A value the row brings is ignored unless the table or its database allows it. The engine
/// wrote whatever the caller gave, and nothing read or moved the counter (qcatco/exportmdb.interop#36).
/// </summary>
public sealed class AutoNumberTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"autonumber_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Column[] Columns() => new[]
    {
        new ColumnBuilder("Id", DataType.Long).AutoNumber().Build(),
        new ColumnBuilder("Txt", DataType.Text).MaxLength(10).Build(),
    };

    private static int[] Ids(Table table) => table.ReadAllRows().Select(r => (int)r["Id"]!).ToArray();

    // The counter as the file holds it, not as the engine reports it.
    private static int CounterOnDisk(Database db, Table table)
        => ByteUtil.GetInt(db.File.ReadPage(table.Definition.TdefPageNumber), 20);

    [Fact]
    public void A_Long_AutoNumber_counts_up_from_one_and_the_row_holds_its_number()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns());

        var rows = new[] { new Row { ["Txt"] = "a" }, new Row { ["Txt"] = "b" }, new Row { ["Txt"] = "c" } };
        foreach (var row in rows) table.Insert(row);

        Assert.Equal(new object?[] { 1, 2, 3 }, rows.Select(r => r["Id"]));
        Assert.Equal(new[] { 1, 2, 3 }, Ids(table));
        Assert.Equal(3, table.LastAutoNumber);
        Assert.Equal(3, table.RowCount);
    }

    [Fact]
    public void An_AutoNumber_that_is_the_primary_key_is_found_by_its_number()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns(), primaryKey: "Id");
        table.Insert(new Row { ["Txt"] = "a" });
        table.Insert(new Row { ["Txt"] = "b" });

        var found = table.NewIndexCursor().FindRowByPrimaryKey(2);

        Assert.NotNull(found);
        Assert.Equal("b", found!["Txt"]);
    }

    [Fact]
    public void The_counter_is_in_the_table_definition_and_the_next_session_goes_on_from_it()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("T", Columns());
            table.Insert(new Row { ["Txt"] = "a" });
            table.Insert(new Row { ["Txt"] = "b" });
            Assert.Equal(2, CounterOnDisk(db, table));
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            Assert.Equal(2, table.LastAutoNumber);

            var row = new Row { ["Txt"] = "c" };
            table.Insert(row);

            Assert.Equal(3, row["Id"]);
            Assert.Equal(new[] { 1, 2, 3 }, Ids(table));
            Assert.Equal(3, CounterOnDisk(db, table));
        }
    }

    [Fact]
    public void Two_Table_objects_on_one_table_do_not_hand_out_the_same_number()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var first = db.CreateTable("T", Columns());
        var second = db.GetTable("T");

        first.Insert(new Row { ["Txt"] = "a" });
        second.Insert(new Row { ["Txt"] = "b" });
        first.Insert(new Row { ["Txt"] = "c" });

        Assert.Equal(new[] { 1, 2, 3 }, Ids(first));
    }

    [Fact]
    public void A_deleted_row_s_number_is_not_handed_out_again()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns());
        table.Insert(new Row { ["Txt"] = "a" });
        table.Insert(new Row { ["Txt"] = "b" });
        table.DeleteRow("Id", 2);

        table.Insert(new Row { ["Txt"] = "c" });

        Assert.Equal(new[] { 1, 3 }, Ids(table));
    }

    [Fact]
    public void A_number_the_row_brings_is_ignored_and_replaced_when_not_allowed()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns());

        var row = new Row { ["Id"] = 50, ["Txt"] = "a" };
        table.Insert(row);

        Assert.Equal(1, row["Id"]);
        Assert.Equal(new[] { 1 }, Ids(table));
        Assert.Equal(1, table.LastAutoNumber);
    }

    [Fact]
    public void A_number_the_row_brings_is_written_when_allowed_and_the_counter_moves_up_to_it()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns());
        table.AllowAutoNumberInsert = true;

        table.Insert(new Row { ["Id"] = 50, ["Txt"] = "a" });
        Assert.Equal(50, table.LastAutoNumber);

        // A number behind the counter is written too, and leaves the counter where it was.
        table.Insert(new Row { ["Id"] = 7, ["Txt"] = "b" });
        Assert.Equal(50, table.LastAutoNumber);

        // A row that brings none, or null, is still numbered.
        var numbered = new Row { ["Txt"] = "c" };
        table.Insert(numbered);
        var nulled = new Row { ["Id"] = null, ["Txt"] = "d" };
        table.Insert(nulled);

        Assert.Equal(51, numbered["Id"]);
        Assert.Equal(52, nulled["Id"]);
        Assert.Equal(new[] { 50, 7, 51, 52 }, Ids(table));
    }

    [Fact]
    public void The_database_decides_for_its_tables_unless_a_table_decides_for_itself()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.AllowAutoNumberInsert = true;
        var follows = db.CreateTable("Follows", Columns());
        var refuses = db.CreateTable("Refuses", Columns());
        refuses.AllowAutoNumberInsert = false;

        follows.Insert(new Row { ["Id"] = 9, ["Txt"] = "a" });
        refuses.Insert(new Row { ["Id"] = 9, ["Txt"] = "a" });

        Assert.Equal(new[] { 9 }, Ids(follows));
        Assert.Equal(new[] { 1 }, Ids(refuses));
    }

    [Fact]
    public void A_Guid_AutoNumber_gets_a_new_guid_for_each_row()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("T", new[]
            {
                new ColumnBuilder("Key", DataType.Guid).AutoNumber().Build(),
                new ColumnBuilder("Txt", DataType.Text).MaxLength(10).Build(),
            });

            var first = new Row { ["Txt"] = "a" };
            var second = new Row { ["Key"] = Guid.Empty, ["Txt"] = "b" };   // not allowed: replaced
            table.Insert(first);
            table.Insert(second);

            var keys = new[] { Assert.IsType<Guid>(first["Key"]), Assert.IsType<Guid>(second["Key"]) };
            Assert.DoesNotContain(Guid.Empty, keys);
            Assert.NotEqual(keys[0], keys[1]);
            Assert.Equal(keys, table.ReadAllRows().Select(r => (Guid)r["Key"]!));
        }

        using var reopened = Database.Open(_path);
        Assert.True(reopened.GetTable("T").Columns.Single(c => c.Name == "Key").IsAutoNumber);
    }

    [Fact]
    public void A_guid_the_row_brings_is_written_when_allowed()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", new[]
        {
            new ColumnBuilder("Key", DataType.Guid).AutoNumber().Build(),
            new ColumnBuilder("Txt", DataType.Text).MaxLength(10).Build(),
        });
        table.AllowAutoNumberInsert = true;
        var mine = Guid.NewGuid();

        table.Insert(new Row { ["Key"] = mine, ["Txt"] = "a" });
        table.Insert(new Row { ["Key"] = mine.ToString("B"), ["Txt"] = "b" });   // as Access shows one

        Assert.Equal(new[] { mine, mine }, table.ReadAllRows().Select(r => (Guid)r["Key"]!));
    }

    [Fact]
    public void An_insert_that_fails_loses_no_number_and_leaves_the_row_as_it_came()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("T", Columns());
            table.Insert(new Row { ["Txt"] = "a" });

            var tooLong = new Row { ["Txt"] = "longer than ten" };
            Assert.Throws<ColumnValueTooLongException>(() => table.Insert(tooLong));

            Assert.False(tooLong.ContainsKey("Id"));
            Assert.Equal(1, table.LastAutoNumber);
            Assert.Equal(1, CounterOnDisk(db, table));
            Assert.Equal(1, table.RowCount);

            table.Insert(new Row { ["Txt"] = "b" });
            Assert.Equal(new[] { 1, 2 }, Ids(table));
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(2, reopened.GetTable("T").LastAutoNumber);
    }

    [Fact]
    public void An_insert_that_fails_with_a_number_of_its_own_leaves_the_counter_where_it_was()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns());
        table.AllowAutoNumberInsert = true;
        table.Insert(new Row { ["Txt"] = "a" });

        var tooLong = new Row { ["Id"] = 900, ["Txt"] = "longer than ten" };
        Assert.Throws<ColumnValueTooLongException>(() => table.Insert(tooLong));

        Assert.Equal(900, tooLong["Id"]);
        Assert.Equal(1, table.LastAutoNumber);
        table.Insert(new Row { ["Txt"] = "b" });
        Assert.Equal(new[] { 1, 2 }, Ids(table));
    }

    [Fact]
    public void An_update_leaves_the_AutoNumber_as_it_was_unless_allowed()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", new[]
        {
            new ColumnBuilder("Key", DataType.Long).Build(),
            new ColumnBuilder("Id", DataType.Long).AutoNumber().Build(),
            new ColumnBuilder("Txt", DataType.Text).MaxLength(10).Build(),
        }, primaryKey: "Key");
        table.Insert(new Row { ["Key"] = 10, ["Txt"] = "a" });

        table.UpdateByPrimaryKey(10, new Row { ["Id"] = 40, ["Txt"] = "b" });

        var row = Assert.Single(table.ReadAllRows());
        Assert.Equal(1, row["Id"]);
        Assert.Equal("b", row["Txt"]);
        Assert.Equal(1, table.LastAutoNumber);

        table.AllowAutoNumberInsert = true;
        table.UpdateByPrimaryKey(10, new Row { ["Id"] = 40 });

        Assert.Equal(40, Assert.Single(table.ReadAllRows())["Id"]);
        Assert.Equal(40, table.LastAutoNumber);
    }

    [Fact]
    public void An_imported_DataTable_keeps_its_own_numbers_and_the_table_goes_on_from_the_highest()
    {
        var source = new DataTable("People");
        source.Columns.Add(new DataColumn("Id", typeof(int)) { AutoIncrement = true, AutoIncrementSeed = 10, AutoIncrementStep = 5 });
        source.Columns.Add("Name", typeof(string));
        source.Rows.Add(null, "A");
        source.Rows.Add(null, "B");

        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.ImportTable(source);

        Assert.Equal(new[] { 10, 15 }, Ids(table));
        Assert.Equal(15, table.LastAutoNumber);
        Assert.Null(table.AllowAutoNumberInsert);

        var next = new Row { ["Name"] = "C" };
        table.Insert(next);
        Assert.Equal(16, next["Id"]);
    }

    private sealed class Person
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void An_imported_object_keeps_the_identity_it_has_and_one_without_is_numbered()
    {
        var people = new[]
        {
            new Person { Id = 7, Name = "A" },
            new Person { Name = "B" },
            new Person { Name = "C" },
        };

        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.ImportTable(people);

        Assert.Equal(new[] { 7, 8, 9 }, Ids(table));
        Assert.Equal(9, table.LastAutoNumber);
    }

    private static DataTable PeopleCountingFrom(int seed, params string[] names)
    {
        var source = new DataTable("People");
        source.Columns.Add(new DataColumn("Id", typeof(int)) { AutoIncrement = true, AutoIncrementSeed = seed });
        source.Columns.Add("Name", typeof(string));
        foreach (string name in names) source.Rows.Add(null, name);
        return source;
    }

    [Fact]
    public void Rows_appended_to_a_table_are_numbered_by_the_table_unless_the_import_keeps_their_own()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.ImportTable(PeopleCountingFrom(1, "A", "B"));

        // A second batch that counts from 1 again: its numbers are the table's, not its own.
        db.ImportTable(PeopleCountingFrom(1, "C", "D"), options: new ImportOptions { AppendIfExists = true });
        Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(table));

        // Rows other tables refer to keep the numbers they came with.
        db.ImportTable(PeopleCountingFrom(100, "E", "F"), options: new ImportOptions { AppendIfExists = true, KeepIdentity = true });
        Assert.Equal(new[] { 1, 2, 3, 4, 100, 101 }, Ids(table));
        Assert.Equal(101, table.LastAutoNumber);
        Assert.Null(table.AllowAutoNumberInsert);
    }

    [Fact]
    public void Objects_appended_keeping_identity_keep_the_one_they_have_and_one_without_is_numbered()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.ImportTable(new[] { new Person { Name = "A" } });

        db.ImportTable(new[] { new Person { Id = 50, Name = "B" }, new Person { Name = "C" } },
            options: new ImportOptions { AppendIfExists = true, KeepIdentity = true });

        Assert.Equal(new[] { 1, 50, 51 }, Ids(table));
    }

    [Fact]
    public void An_update_that_changes_the_primary_key_moves_the_row_s_index_entry_to_the_new_key()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns(), primaryKey: "Id");
        table.Insert(new Row { ["Txt"] = "one" });
        table.Insert(new Row { ["Txt"] = "two" });

        table.AllowAutoNumberInsert = true;
        table.UpdateByPrimaryKey(1, new Row { ["Id"] = 40 });

        // The index itself, not a cursor: a cursor that finds no entry reads the table instead.
        var index = new IndexWriter(db.File, new PageAllocator(db.File));
        Assert.NotNull(index.FindRowByPrimaryKey(table.Definition, 40));
        Assert.Null(index.FindRowByPrimaryKey(table.Definition, 1));
        Assert.NotNull(index.FindRowByPrimaryKey(table.Definition, 2));

        Assert.Equal("one", table.NewIndexCursor().FindRowByPrimaryKey(40)!["Txt"]);
        Assert.Equal(40, table.LastAutoNumber);
    }

    [Fact]
    public void An_update_that_brings_null_for_an_AutoNumber_leaves_the_number_the_row_has()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns(), primaryKey: "Id");
        table.Insert(new Row { ["Txt"] = "one" });
        table.AllowAutoNumberInsert = true;

        table.UpdateByPrimaryKey(1, new Row { ["Id"] = null, ["Txt"] = "changed" });

        var row = Assert.Single(table.ReadAllRows());
        Assert.Equal(1, row["Id"]);
        Assert.Equal("changed", row["Txt"]);
        Assert.Equal(1, table.LastAutoNumber);
    }

    [Fact]
    public void An_update_to_a_primary_key_another_row_has_is_refused_and_changes_nothing()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns(), primaryKey: "Id");
        table.Insert(new Row { ["Txt"] = "one" });
        table.Insert(new Row { ["Txt"] = "two" });
        table.AllowAutoNumberInsert = true;

        Assert.Throws<InvalidOperationException>(() => table.UpdateByPrimaryKey(1, new Row { ["Id"] = 2, ["Txt"] = "x" }));

        Assert.Equal(new[] { 1, 2 }, Ids(table));
        var cursor = table.NewIndexCursor();
        Assert.Equal("one", cursor.FindRowByPrimaryKey(1)!["Txt"]);
        Assert.Equal("two", cursor.FindRowByPrimaryKey(2)!["Txt"]);
        Assert.Equal(2, table.LastAutoNumber);
    }

    // DBNull is how a DataRow holds an empty cell. For an AutoNumber it is no value, as null is: with rows allowed
    // to bring their own, an insert tried to read it as a number, or a GUID, and threw.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DBNull_for_an_AutoNumber_is_no_value_and_the_table_numbers_the_row(bool allowed)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", new[]
        {
            new ColumnBuilder("Id", DataType.Long).AutoNumber().Build(),
            new ColumnBuilder("Key", DataType.Guid).AutoNumber().Build(),
            new ColumnBuilder("Txt", DataType.Text).MaxLength(10).Build(),
        });
        table.AllowAutoNumberInsert = allowed;

        var first = new Row { ["Id"] = DBNull.Value, ["Key"] = DBNull.Value, ["Txt"] = "a" };
        var second = new Row { ["Id"] = DBNull.Value, ["Key"] = DBNull.Value, ["Txt"] = "b" };
        table.Insert(first);
        table.Insert(second);

        Assert.Equal(new object?[] { 1, 2 }, new[] { first["Id"], second["Id"] });
        Assert.Equal(new[] { 1, 2 }, Ids(table));
        var keys = table.ReadAllRows().Select(r => (Guid)r["Key"]!).ToArray();
        Assert.Equal(new[] { Assert.IsType<Guid>(first["Key"]), Assert.IsType<Guid>(second["Key"]) }, keys);
        Assert.DoesNotContain(Guid.Empty, keys);
        Assert.NotEqual(keys[0], keys[1]);
        Assert.Equal(2, table.LastAutoNumber);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_update_that_brings_DBNull_for_an_AutoNumber_leaves_the_value_the_row_has(bool allowed)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", new[]
        {
            new ColumnBuilder("Pk", DataType.Long).Build(),
            new ColumnBuilder("Id", DataType.Long).AutoNumber().Build(),
            new ColumnBuilder("Key", DataType.Guid).AutoNumber().Build(),
            new ColumnBuilder("Txt", DataType.Text).MaxLength(10).Build(),
        }, primaryKey: "Pk");
        var inserted = new Row { ["Pk"] = 10, ["Txt"] = "a" };
        table.Insert(inserted);
        table.AllowAutoNumberInsert = allowed;

        table.UpdateByPrimaryKey(10, new Row { ["Id"] = DBNull.Value, ["Key"] = DBNull.Value, ["Txt"] = "b" });

        var row = Assert.Single(table.ReadAllRows());
        Assert.Equal(1, row["Id"]);
        Assert.Equal(inserted["Key"], row["Key"]);
        Assert.Equal("b", row["Txt"]);
        Assert.Equal(1, table.LastAutoNumber);
    }

    // ── An import's rows and the numbers they bring (review of qcatco/jackcess-dotnet#7) ──

    private static string[] Names(Table table) => table.ReadAllRows().Select(r => (string)r["Name"]!).ToArray();

    private static Person[] People(params int[] ids)
        => ids.Select((id, i) => new Person { Id = id, Name = ((char)('A' + i)).ToString() }).ToArray();

    // Every row is in the primary key under the number it holds, once.
    private static void AssertKeyFindsEveryRow(Table table)
    {
        var rows = table.ReadAllRows();
        Assert.Equal(rows.Count, rows.Select(r => r["Id"]).Distinct().Count());
        foreach (var row in rows)
            Assert.Equal(row["Name"], table.NewIndexCursor().FindRowByPrimaryKey(row["Id"]!)!["Name"]);
    }

    // The numbers the objects bring are kept and the rest are numbered past the highest, whatever the order:
    // 0 then 1 gave both rows 1, the first numbered before the second's number was seen.
    [Theory]
    [InlineData(new[] { 0, 1 }, new[] { 2, 1 })]
    [InlineData(new[] { 1, 0 }, new[] { 1, 2 })]
    [InlineData(new[] { 0, 5, 0 }, new[] { 6, 5, 7 })]
    [InlineData(new[] { 0, 0, 3, 1 }, new[] { 4, 5, 3, 1 })]
    public void Imported_objects_keep_the_identities_they_bring_and_the_rest_are_numbered_past_them(int[] brought, int[] expected)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);

        var table = db.ImportTable(People(brought));

        Assert.Equal(expected, Ids(table));
        Assert.Equal(expected.Max(), table.LastAutoNumber);
        AssertKeyFindsEveryRow(table);
    }

    [Theory]
    [InlineData(new[] { 0, 10, 0 }, new[] { 1, 2, 11, 10, 12 })]
    [InlineData(new[] { 0, 3 }, new[] { 1, 2, 4, 3 })]
    [InlineData(new[] { 3, 0 }, new[] { 1, 2, 3, 4 })]
    public void Objects_appended_keeping_identity_are_numbered_past_the_ones_the_batch_brings(int[] brought, int[] expected)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.ImportTable(People(0, 0));

        db.ImportTable(People(brought), options: new ImportOptions { AppendIfExists = true, KeepIdentity = true });

        Assert.Equal(expected, Ids(table));
        AssertKeyFindsEveryRow(table);
    }

    [Fact]
    public void An_append_keeping_an_identity_a_row_of_the_table_has_is_refused_before_anything_is_written()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.ImportTable(People(0, 0));
        long length = new FileInfo(_path).Length;

        var refused = Assert.Throws<InvalidOperationException>(() => db.ImportTable(People(0, 9, 2),
            options: new ImportOptions { AppendIfExists = true, KeepIdentity = true }));

        Assert.Contains("another row holds 2 for Id", refused.Message);   // the refusal an insert of the row would be
        Assert.Equal(new[] { 1, 2 }, Ids(table));
        Assert.Equal(new[] { "A", "B" }, Names(table));
        Assert.Equal(2, table.LastAutoNumber);
        Assert.Equal(length, new FileInfo(_path).Length);
        Assert.Null(table.AllowAutoNumberInsert);
    }

    [Fact]
    public void Two_imported_objects_that_bring_one_identity_for_the_primary_key_are_refused_before_any_row_is_written()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);

        Assert.Throws<InvalidOperationException>(() => db.ImportTable(People(4, 0, 4)));

        Assert.Empty(db.GetTable("Person").ReadAllRows());
    }

    // A DataTable's AutoIncrement column where some rows have a number and some have none.
    private static DataTable PeopleNumbered(params int?[] ids)
    {
        var source = new DataTable("People");
        source.Columns.Add(new DataColumn("Id", typeof(int)) { AutoIncrement = true });
        source.Columns.Add("Name", typeof(string));
        for (int i = 0; i < ids.Length; i++)
        {
            var row = source.Rows.Add(null, ((char)('A' + i)).ToString());
            row["Id"] = ids[i] is { } id ? id : DBNull.Value;
        }
        return source;
    }

    [Fact]
    public void A_DataTable_s_rows_without_a_number_are_numbered_past_the_ones_its_other_rows_bring()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);

        var table = db.ImportTable(PeopleNumbered(null, 1, null));
        Assert.Equal(new[] { 2, 1, 3 }, Ids(table));

        db.ImportTable(PeopleNumbered(null, 10), options: new ImportOptions { AppendIfExists = true, KeepIdentity = true });
        Assert.Equal(new[] { 2, 1, 3, 11, 10 }, Ids(table));
        Assert.Equal(11, table.LastAutoNumber);
    }

    // KeepIdentity = false is the append's own choice: the database letting rows bring their numbers does not
    // undo it. Appending an object with identity 1 to a table that has 1 wrote a second 1.
    [Fact]
    public void An_append_that_does_not_keep_identity_numbers_its_rows_afresh_whatever_the_database_allows()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.AllowAutoNumberInsert = true;
        var table = db.ImportTable(People(1));

        var appended = db.ImportTable(People(1, 2), options: new ImportOptions { AppendIfExists = true, KeepIdentity = false });
        Assert.Equal(new[] { 1, 2, 3 }, Ids(table));
        Assert.Null(appended.AllowAutoNumberInsert);

        db.ImportTable(PeopleCountingFrom(1, "X"), tableName: "Person", options: new ImportOptions { AppendIfExists = true });
        Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(table));
        AssertKeyFindsEveryRow(table);
    }

    // The other way about: the database refuses, the append keeps. Either way the table the append hands back is
    // left to follow its database, as it did before.
    [Fact]
    public void An_append_that_keeps_identity_keeps_it_whatever_the_database_allows()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.ImportTable(People(1));

        var kept = db.ImportTable(People(40), options: new ImportOptions { AppendIfExists = true, KeepIdentity = true });
        var numbered = db.ImportTable(People(40), options: new ImportOptions { AppendIfExists = true, KeepIdentity = false });

        Assert.Equal(new[] { 1, 40, 41 }, Ids(table));
        Assert.Null(kept.AllowAutoNumberInsert);
        Assert.Null(numbered.AllowAutoNumberInsert);
    }

    public static TheoryData<string, string> AccessTablesWithALongAutoNumber()
    {
        var data = new TheoryData<string, string>();
        foreach (string file in TestCorpus.Files("*.mdb", "V2000", "V2003"))
        {
            using var db = Database.Open(file);
            foreach (string name in db.ListTables())
            {
                var table = db.GetTable(name);
                if (table.Columns.Any(c => c.IsAutoNumber && c.DataType == DataType.Long))
                    data.Add(Path.GetFileName(Path.GetDirectoryName(file))! + "/" + Path.GetFileName(file), name);
            }
        }
        return data;
    }

    // Tables Access made and numbered: the counter is where Access left it, at or past every number in the table,
    // and the engine's next row is one more.
    [Theory]
    [MemberData(nameof(AccessTablesWithALongAutoNumber))]
    public void A_table_Access_numbered_goes_on_from_Access_s_counter(string file, string tableName)
    {
        File.Copy(Path.Combine(TestCorpus.Root!, file), _path, overwrite: true);
        using var db = Database.Open(_path);
        var table = db.GetTable(tableName);
        string id = table.Columns.First(c => c.IsAutoNumber && c.DataType == DataType.Long).Name;
        int before = table.LastAutoNumber;
        Assert.True(before >= table.ReadAllRows().Select(r => (int)r[id]!).DefaultIfEmpty(0).Max());

        var row = new Row();
        table.Insert(row);

        Assert.Equal(before + 1, row[id]);
        Assert.Equal(before + 1, table.LastAutoNumber);
    }
}
