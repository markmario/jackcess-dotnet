using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Row"/>: a dictionary of values by column name, whatever the case of the name, that keeps its
/// entries in order; and what a row read from a table holds.
/// </summary>
public sealed class RowTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"row_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Indexer_KeyAccess_IsCaseInsensitive()
    {
        var r = new Row { ["Name"] = "Alice" };
        Assert.Equal("Alice", r["name"]);
        Assert.Equal("Alice", r["NAME"]);
        Assert.Equal("Alice", r["Name"]);
    }

    [Fact]
    public void Indexer_Set_CaseInsensitive_OverwritesPrevious()
    {
        var r = new Row { ["Name"] = "Alice" };
        r["NAME"] = "Bob";
        Assert.Single(r);
        Assert.Equal("Bob", r["name"]);
        Assert.Equal(new[] { "Name" }, r.Keys);   // under the name it was added by
    }

    [Fact]
    public void Indexer_GetMissingKey_Throws()
    {
        var r = new Row();
        Assert.Throws<KeyNotFoundException>(() => r["missing"]);
    }

    [Fact]
    public void TryGetValue_ReturnsFalseForMissingKey()
    {
        var r = new Row { ["Id"] = 1 };
        Assert.False(r.TryGetValue("nope", out _));
        Assert.True (r.TryGetValue("Id",   out var v));
        Assert.Equal(1, v);
    }

    [Fact]
    public void A_collection_initialiser_adds_each_value_and_refuses_a_column_twice()
    {
        var r = new Row
        {
            { "Id",   1 },
            { "Name", "Alice" },
            { "Age",  (short)30 },
        };

        Assert.Equal(3, r.Count);
        Assert.Equal(1,        r["Id"]);
        Assert.Equal("Alice",  r["Name"]);
        Assert.Equal((short)30, r["Age"]);

        Assert.Throws<ArgumentException>(() => r.Add("NAME", "Bob"));
        Assert.Throws<ArgumentException>(() => new Row { { "A", 1 }, { "a", 2 } });
        Assert.Equal("Alice", r["Name"]);
    }

    [Fact]
    public void The_entries_keep_the_order_they_were_added_in()
    {
        var r = new Row { ["Zed"] = 1, ["Alpha"] = 2, ["Mid"] = 3 };
        r["alpha"] = 20;        // changed in place
        r["Last"] = 4;

        Assert.Equal(new[] { "Zed", "Alpha", "Mid", "Last" }, r.Keys);
        Assert.Equal(new object?[] { 1, 20, 3, 4 }, r.Values);
        Assert.Equal(new[] { "Zed", "Alpha", "Mid", "Last" }, r.Select(entry => entry.Key));

        Assert.True(r.Remove("ALPHA"));
        Assert.False(r.Remove("Alpha"));
        Assert.Equal(new[] { "Zed", "Mid", "Last" }, r.Keys);
        Assert.Equal(3, r["mid"]);
        Assert.Equal(4, r["last"]);
        Assert.False(r.ContainsKey("Alpha"));

        r.Clear();
        Assert.Empty(r);
        Assert.False(r.ContainsKey("Zed"));
    }

    [Fact]
    public void IEnumerableCtor_AcceptsKvpSequence()
    {
        var src = new[]
        {
            new KeyValuePair<string, object?>("Id",   1),
            new KeyValuePair<string, object?>("Name", "Alice"),
        };
        var r = new Row(src);
        Assert.Equal(2, r.Count);
        Assert.Equal("Alice", r["name"]);

        var copy = new Row(r) { ["Name"] = "Bob" };
        Assert.Equal("Alice", r["Name"]);
        Assert.Equal(new[] { "Id", "Name" }, copy.Keys);
        Assert.Throws<ArgumentNullException>(() => new Row(null!));
    }

    [Fact]
    public void Row_AllowsNullValues()
    {
        var r = new Row { ["X"] = null };
        Assert.True(r.ContainsKey("X"));
        Assert.Null(r["X"]);
        Assert.True(r.IsNull("X"));
    }

    [Fact]
    public void IsNull_is_true_for_a_null_a_DBNull_and_a_column_the_row_does_not_have()
    {
        var r = new Row { ["Null"] = null, ["DbNull"] = DBNull.Value, ["Zero"] = 0, ["Empty"] = "" };

        Assert.True(r.IsNull("null"));
        Assert.True(r.IsNull("DbNull"));
        Assert.True(r.IsNull("NotThere"));
        Assert.False(r.IsNull("Zero"));
        Assert.False(r.IsNull("Empty"));
    }

    [Fact]
    public void GetValue_gives_the_value_as_the_type_asked_for()
    {
        var id = Guid.NewGuid();
        var r = new Row
        {
            ["Short"] = (short)7, ["Int"] = 42, ["Text"] = "12", ["Half"] = 1.5d,
            ["When"] = new DateTime(2024, 2, 29), ["Id"] = id, ["Bytes"] = new byte[] { 1, 2 },
            ["Null"] = null, ["DbNull"] = DBNull.Value,
        };

        Assert.Equal(42, r.GetValue<int>("int"));
        Assert.Equal(7, r.GetValue<int>("Short"));             // widened
        Assert.Equal(7L, r.GetValue<long?>("Short"));
        Assert.Equal(12, r.GetValue<int>("Text"));             // converted, as Convert.ChangeType does
        Assert.Equal("42", r.GetValue<string>("Int"));
        Assert.Equal("1.5", r.GetValue<string>("Half"));       // in the invariant culture
        Assert.Equal(1.5m, r.GetValue<decimal>("Half"));
        Assert.Equal(new DateTime(2024, 2, 29), r.GetValue<DateTime>("When"));
        Assert.Equal(id, r.GetValue<Guid>("Id"));
        Assert.Equal(new byte[] { 1, 2 }, r.GetValue<byte[]>("Bytes"));
        Assert.Equal(42, r.GetValue<object>("Int"));

        // Null, DBNull and a column the row does not have: the type's default.
        Assert.Null(r.GetValue<string>("Null"));
        Assert.Null(r.GetValue<int?>("Null"));
        Assert.Equal(0, r.GetValue<int>("Null"));
        Assert.Null(r.GetValue<int?>("DbNull"));
        Assert.Null(r.GetValue<string>("NotThere"));
        Assert.Equal(default, r.GetValue<DateTime>("NotThere"));

        Assert.Throws<InvalidCastException>(() => r.GetValue<Guid>("Int"));
        Assert.Throws<FormatException>(() => new Row { ["T"] = "abc" }.GetValue<int>("T"));
        Assert.Throws<OverflowException>(() => new Row { ["Big"] = 70_000 }.GetValue<short>("Big"));
    }

    [Fact]
    public void A_row_is_a_dictionary_to_what_takes_one()
    {
        var r = new Row { ["Id"] = 1, ["Name"] = null };

        IReadOnlyDictionary<string, object?> readOnly = r;
        Assert.Equal(new[] { "Id", "Name" }, readOnly.Keys);
        Assert.Equal(new object?[] { 1, null }, readOnly.Values);
        Assert.True(readOnly.TryGetValue("NAME", out var name));
        Assert.Null(name);

        IDictionary<string, object?> dictionary = r;
        dictionary.Add(new KeyValuePair<string, object?>("Age", 30));
        Assert.True(dictionary.Contains(new KeyValuePair<string, object?>("age", 30)));
        Assert.False(dictionary.Contains(new KeyValuePair<string, object?>("age", 31)));
        Assert.False(dictionary.Remove(new KeyValuePair<string, object?>("age", 31)));
        Assert.True(dictionary.Remove(new KeyValuePair<string, object?>("age", 30)));
        Assert.False(dictionary.IsReadOnly);

        var copied = new KeyValuePair<string, object?>[3];
        dictionary.CopyTo(copied, 1);
        Assert.Equal("Id", copied[1].Key);
        Assert.Equal("Name", copied[2].Key);

        Assert.Throws<ArgumentNullException>(() => r[null!]);
        Assert.Throws<ArgumentNullException>(() => r.ContainsKey(null!));
    }

    [Fact]
    public void A_row_that_is_read_has_every_column_in_the_table_s_order_and_null_for_a_null()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[]
        {
            new ColumnBuilder("Id",    DataType.Long).Build(),
            new ColumnBuilder("Name",  DataType.Text).MaxLength(20).Build(),
            new ColumnBuilder("Notes", DataType.Memo).Build(),
            new ColumnBuilder("Flag",  DataType.Boolean).Build(),
            new ColumnBuilder("When",  DataType.ShortDateTime).Build(),
        }, primaryKey: "Id");
        table.Insert(new Row { ["Id"] = 1 });
        table.Insert(new Row { ["When"] = new DateTime(2024, 1, 2), ["Name"] = DBNull.Value, ["Id"] = 2, ["Notes"] = "n" });

        var columns = new[] { "Id", "Name", "Notes", "Flag", "When" };
        var readers = new Func<List<Row>>[]
        {
            () => table.Rows.ToList(),
            () => table.CreateCursor().ToList(),
            () => table.CreateIndexCursor().ToList(),
            () => new List<Row> { table.FindRowByPrimaryKey(1)!, table.FindRowByPrimaryKey(2)! },
            () => new List<Row> { table.CreateIndexCursor().FindRow("Id", 1)!, table.CreateIndexCursor().FindRowByEntry(2)! },
        };
        foreach (var read in readers)
        {
            var rows = read();
            Assert.Equal(2, rows.Count);
            foreach (var row in rows)
            {
                Assert.Equal(columns, row.Keys);
                Assert.All(row.Values, value => Assert.False(value is DBNull));
                Assert.False(row.ContainsKey("NoSuchColumn"));
            }

            Assert.True(rows[0].ContainsKey("Name"));
            Assert.Null(rows[0]["Name"]);
            Assert.Null(rows[0]["Notes"]);
            Assert.Null(rows[0]["When"]);
            Assert.True(rows[0].IsNull("when"));
            Assert.Equal(false, rows[0]["Flag"]);   // a Yes/No holds no null

            Assert.Null(rows[1]["Name"]);           // DBNull went in; null comes out
            Assert.Equal("n", rows[1]["Notes"]);
            Assert.Equal(new DateTime(2024, 1, 2), rows[1].GetValue<DateTime?>("When"));
        }

        // A null is not found by looking for null.
        Assert.Null(table.CreateIndexCursor().FindRow("Name", null));
    }

    [Fact]
    public void A_row_that_was_read_can_be_written_back_with_its_nulls()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var columns = new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(20).Build(),
            new ColumnBuilder("Age",  DataType.Int).Build(),
        };
        var from = db.CreateTable("From", columns, primaryKey: "Id");
        var to   = db.CreateTable("To", columns, primaryKey: "Id");
        from.Insert(new Row { ["Id"] = 1, ["Name"] = "Ada" });
        from.Insert(new Row { ["Id"] = 2, ["Age"] = (short)40 });

        foreach (var row in from.Rows.ToList())
            to.Insert(row);
        var read = from.FindRowByPrimaryKey(1)!;
        read["Name"] = null;
        read["Age"] = (short)36;
        from.UpdateByPrimaryKey(1, read);

        Assert.Equal(new object?[] { 1, "Ada", null }, to.FindRowByPrimaryKey(1)!.Values);
        Assert.Equal(new object?[] { 2, null, (short)40 }, to.FindRowByPrimaryKey(2)!.Values);
        Assert.Equal(new object?[] { 1, null, (short)36 }, from.FindRowByPrimaryKey(1)!.Values);
    }
}
