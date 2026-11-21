using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A column is added to a table that already has rows: one this library made, and one
/// Access made. The rows already there read the new column as null, as Access shows them; rows written afterwards
/// hold it; every index goes on holding every row; and a refused column leaves the file byte for byte as it was.
/// What Access makes of the changed table is in SchemaChangesInAccessTests.
/// </summary>
public sealed class AddColumnTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"add_column_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ColumnDefinition Col(string name, DataType type) => new ColumnDefinition(name, type);

    private static readonly DateTime When = new(2026, 11, 18, 9, 30, 0);
    private static readonly Guid Key = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    // One of every kind of place a row has for a value: fixed-length, variable-length, long, and the null mask.
    private static (ColumnDefinition Column, object Value)[] NewColumns() => new (ColumnDefinition, object)[]
    {
        (Col("Whole", DataType.Int32), 42),
        (new ColumnDefinition("Words", DataType.Text) { MaxLength = 30 }, "added later"),
        (Col("Notes", DataType.Memo), new string('n', 9_000)),
        (Col("Flag", DataType.Boolean), true),
        (Col("Real", DataType.Double), 2.5),
        (Col("Seen", DataType.DateTime), When),
        (Col("Ref", DataType.Guid), Key),
        (Col("Small", DataType.Byte), (byte)7),
        (Col("Cost", DataType.Currency), 12.34m),
    };

    private Database CreateWithRows(int rows)
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("Things", new[]
        {
            Col("Id", DataType.Int32),
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 40 },
            Col("Note", DataType.Memo),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByName", "Name") });
        for (int i = 1; i <= rows; i++)
            table.Insert(new Row { ["Id"] = i, ["Name"] = $"name {i}", ["Note"] = $"note {i}" });
        return db;
    }

    private static void AssertOldRowsReadTheNewColumnsAsNull(Table table, int upTo)
    {
        foreach (var row in table.Rows.Where(r => (int)r["Id"]! <= upTo))
        {
            Assert.Equal($"name {row["Id"]}", row["Name"]);
            Assert.Equal($"note {row["Id"]}", row["Note"]);
            foreach (var (column, _) in NewColumns())
            {
                // A Yes/No column holds no null: a row written before it was added is No, as in Access.
                if (column.DataType == DataType.Boolean) Assert.Equal(false, row[column.Name]);
                else Assert.True(row.ContainsKey(column.Name) && row[column.Name] is null, $"row {row["Id"]} has a value for {column.Name}");
            }
        }
    }

    [Fact]
    public void A_column_added_to_a_table_reads_null_in_the_rows_already_there_and_its_value_in_new_ones()
    {
        using (var db = CreateWithRows(50))
        {
            var table = db.GetTable("Things");
            foreach (var (column, _) in NewColumns())
            {
                var added = table.AddColumn(column);
                Assert.Equal(column.Name, added.Name);
                Assert.Equal(column.DataType, added.DataType);
            }
            Assert.Equal(new[] { "Id", "Name", "Note" }.Concat(NewColumns().Select(c => c.Column.Name)), table.Columns.Select(c => c.Name));
            // Each new column has a number, and a place in a row, of its own.
            Assert.Equal(Enumerable.Range(0, 12), table.Columns.Select(c => c.Ordinal));
            Assert.Null(table.InsertRefusal);

            AssertOldRowsReadTheNewColumnsAsNull(table, 50);

            var row = new Row { ["Id"] = 51, ["Name"] = "name 51", ["Note"] = "note 51" };
            foreach (var (column, value) in NewColumns()) row[column.Name] = value;
            table.Insert(row);
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Things");
        Assert.Equal(12, read.Columns.Count);
        AssertOldRowsReadTheNewColumnsAsNull(read, 50);
        var last = read.FindRowByPrimaryKey(51)!;
        foreach (var (column, value) in NewColumns()) Assert.Equal(value, last[column.Name]);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Things", expectedRows: 51));
    }

    [Fact]
    public void A_row_written_before_the_column_takes_a_value_for_it_when_it_is_updated()
    {
        using var db = CreateWithRows(20);
        var table = db.GetTable("Things");
        foreach (var (column, _) in NewColumns()) table.AddColumn(column);

        var changes = new Row();
        foreach (var (column, value) in NewColumns()) changes[column.Name] = value;
        table.UpdateByPrimaryKey(7, changes);
        table.DeleteRow("Id", 8);

        var updated = table.FindRowByPrimaryKey(7)!;
        Assert.Equal("name 7", updated["Name"]);
        Assert.Equal("note 7", updated["Note"]);
        foreach (var (column, value) in NewColumns()) Assert.Equal(value, updated[column.Name]);
        AssertOldRowsReadTheNewColumnsAsNull(table, 6);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Things", expectedRows: 19));
    }

    [Fact]
    public void A_table_object_opened_before_the_column_was_added_writes_rows_with_it()
    {
        using var db = CreateWithRows(3);
        var earlier = db.GetTable("Things");
        db.GetTable("Things").AddColumn(Col("Whole", DataType.Int32));

        Assert.Contains("Whole", earlier.Columns.Select(c => c.Name));
        earlier.Insert(new Row { ["Id"] = 4, ["Name"] = "four", ["Whole"] = 44 });

        Assert.Equal(44, db.GetTable("Things").FindRowByPrimaryKey(4)!["Whole"]);
        Assert.Null(db.GetTable("Things").FindRowByPrimaryKey(3)!["Whole"]);
    }

    // indexV2003.mdb's Table1: five columns, the primary key, an index and two relationships' indexes, four rows.
    [Theory]
    [InlineData("V2003", "indexV2003.mdb")]
    [InlineData("V2010", "indexV2010.accdb")]
    public void A_column_is_added_to_a_table_Access_made(string version, string file)
    {
        string? source = TestCorpus.File(version, file);
        if (source is null) return;   // the corpus is optional locally; CI always has it
        File.Copy(source, _path, overwrite: true);

        List<Row> before;
        using (var db = Database.Open(_path))
        {
            // The rows added below name no row of the tables Table1 refers to: this is about the columns.
            db.EnforceForeignKeys = false;
            var table = db.GetTable("Table1");
            before = table.Rows.ToList();
            foreach (var (column, _) in NewColumns()) table.AddColumn(column);

            var row = new Row { ["id"] = 900, ["otherfk1"] = 1, ["otherfk2"] = 1, ["data"] = "after", ["otherfk3"] = 3 };
            foreach (var (column, value) in NewColumns()) row[column.Name] = value;
            table.Insert(row);
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Table1");
        Assert.Equal(5 + NewColumns().Length, read.Columns.Count);
        Assert.Equal(4, read.Indexes.Count);   // the indexes Access gave it, all read
        var rows = read.Rows.ToList();
        Assert.Equal(before.Count + 1, rows.Count);
        foreach (var was in before)
        {
            var now = rows.Single(r => Equals(r["id"], was["id"]));
            foreach (var pair in was) Assert.Equal(pair.Value, now[pair.Key]);
            Assert.Equal(was.Count(v => v.Value is not null) + 1, now.Count(v => v.Value is not null));   // what it had, and No for the Yes/No column
            Assert.Equal(read.Columns.Count, now.Count);
            Assert.Equal(false, now["Flag"]);
        }
        var added = rows.Single(r => Equals(r["id"], 900));
        foreach (var (column, value) in NewColumns()) Assert.Equal(value, added[column.Name]);
        IndexChecks.AssertEveryIndexHoldsTheRows(reopened, "Table1", expectedRows: before.Count + 1);
    }

    // delColV2003.mdb's Table1 has had columns deleted: numbers 1 and 4 are gone, and their room in a row with them.
    [Fact]
    public void A_column_added_to_a_table_that_has_had_columns_deleted_is_numbered_after_every_column_it_ever_had()
    {
        string? source = TestCorpus.File("V2003", "delColV2003.mdb");
        if (source is null) return;
        File.Copy(source, _path, overwrite: true);

        using var db = Database.Open(_path);
        var table = db.GetTable("Table1");
        var before = table.Rows.ToList();
        Assert.Equal(new[] { 0, 2, 3, 5 }, table.Columns.Select(c => c.Ordinal));

        var whole = table.AddColumn(Col("Whole", DataType.Int32));
        var words = table.AddColumn(new ColumnDefinition("Words", DataType.Text) { MaxLength = 20 });
        Assert.Equal(6, whole.Ordinal);
        Assert.Equal(7, words.Ordinal);
        Assert.Null(table.InsertRefusal);

        table.Insert(new Row { ["id"] = 77, ["id2"] = 78, ["data"] = "d", ["data2"] = "d2", ["Whole"] = 79, ["Words"] = "w" });

        var rows = table.Rows.ToList();
        foreach (var was in before)
        {
            var now = rows.Single(r => Equals(r["id"], was["id"]));
            Assert.Equal(was.Count(v => v.Value is not null), now.Count(v => v.Value is not null));
            foreach (var pair in was) Assert.Equal(pair.Value, now[pair.Key]);
        }
        var added = rows.Single(r => Equals(r["id"], 77));
        Assert.Equal(new object[] { 78, "d", "d2", 79, "w" }, new[] { added["id2"], added["data"], added["data2"], added["Whole"], added["Words"] });
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table1", expectedRows: before.Count + 1);
    }

    [Fact]
    public void An_AutoNumber_column_is_added_to_a_table_with_no_rows_and_numbers_the_rows_that_follow()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("Empty", new[] { new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 } });
        table.AddColumn(new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true });

        var first = new Row { ["Name"] = "one" };
        var second = new Row { ["Name"] = "two" };
        table.Insert(first);
        table.Insert(second);

        Assert.Equal(1, first["Id"]);
        Assert.Equal(2, second["Id"]);
        Assert.Equal(2, table.LastAutoNumber);
    }

    // ── Refusals: nothing changes ────────────────────────────────────────────

    private void AssertRefusedAndUnchanged<TException>(Action<Database> change) where TException : Exception
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var pages = IndexChecks.AllPages(db);
            Assert.Throws<TException>(() => change(db));
            IndexChecks.AssertUnchanged(pages, db);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_column_whose_name_the_table_has_is_refused_and_nothing_changes()
    {
        CreateWithRows(5).Dispose();
        AssertRefusedAndUnchanged<ArgumentException>(db => db.GetTable("Things").AddColumn(Col("NAME", DataType.Int32)));
    }

    [Fact]
    public void An_AutoNumber_column_is_refused_for_a_table_with_rows_and_nothing_changes()
    {
        CreateWithRows(5).Dispose();
        AssertRefusedAndUnchanged<NotSupportedException>(
            db => db.GetTable("Things").AddColumn(new ColumnDefinition("Auto", DataType.Int32) { IsAutoNumber = true }));
    }

    [Fact]
    public void A_complex_column_is_refused_and_nothing_changes()
    {
        CreateWithRows(5).Dispose();
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.GetTable("Things").AddColumn(Col("Files", DataType.Complex)));
    }

    // A table has at most 255 columns, in Access as here: the 256th is refused, and the table - whose definition
    // by then runs over several pages (MultiPageDefinitionTests) - is as writable after as before.
    [Fact]
    public void A_column_past_the_255_a_table_has_is_refused_and_the_table_is_still_written()
    {
        using (var db = CreateWithRows(5))
        {
            var table = db.GetTable("Things");
            for (int added = 0; added < 252; added++)
                table.AddColumn(Col(new string((char)('a' + added % 26), 50) + added.ToString("D4"), DataType.Int32));
            Assert.Equal(255, table.Columns.Count);

            var pages = IndexChecks.AllPages(db);
            var refused = Assert.Throws<ArgumentException>(() => table.AddColumn(Col("OneTooMany", DataType.Int32)));
            Assert.Contains("255 columns", refused.Message);
            IndexChecks.AssertUnchanged(pages, db);

            Assert.Null(table.InsertRefusal);
            table.Insert(new Row { ["Id"] = 6, ["Name"] = "six", [table.Columns[^1].Name] = 66 });
        }

        using var reopened = Database.Open(_path);
        var read = reopened.GetTable("Things");
        Assert.Equal(255, read.Columns.Count);
        Assert.Null(read.InsertRefusal);
        Assert.Equal(66, read.FindRowByPrimaryKey(6)![read.Columns[^1].Name]);
        Assert.Equal(6, read.Rows.Count());
    }

    [Fact]
    public void A_column_is_not_added_to_a_table_of_an_Access_97_file()
    {
        string? source = TestCorpus.File("V1997", "indexV1997.mdb");
        if (source is null) return;
        File.Copy(source, _path, overwrite: true);
        AssertRefusedAndUnchanged<NotSupportedException>(db => db.GetTable("Table1").AddColumn(Col("Whole", DataType.Int32)));
    }
}
