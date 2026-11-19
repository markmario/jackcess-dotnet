using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A column's Required and Allow Zero Length are properties, read, written and held to (#41), and a Replication
/// ID AutoNumber has the default value Access generates one from (#33). Access keeps none of it in the table's
/// definition. What it refuses was asked of ACE, and these hold the library to the same:
/// a null in a required column, and a zero-length string in a Text or Memo column whose AllowZeroLength property
/// is there and false - in every column of a row it adds, and of a row it updates only in the columns the update
/// gives a value. A column with no AllowZeroLength property takes a zero-length string.
/// Assets/dao-properties.mdb is a database DAO made (tools/dao-properties.ps1).
/// </summary>
public sealed class ColumnRuleTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"column_rules_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void CopyDaoMade()
        => File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "dao-properties.mdb"), _path, overwrite: true);

    private static ColumnDefinition[] Columns() => new[]
    {
        new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true },
        new ColumnDefinition("OptLong", DataType.Int32),
        new ColumnDefinition("ReqLong", DataType.Int32) { IsRequired = true },
        new ColumnDefinition("TextDefault", DataType.Text) { MaxLength = 20 },
        new ColumnDefinition("TextNoZero", DataType.Text) { MaxLength = 20, AllowZeroLength = false },
        new ColumnDefinition("TextReq", DataType.Text) { MaxLength = 20, IsRequired = true },
        new ColumnDefinition("MemoNoZero", DataType.Memo) { AllowZeroLength = false },
        new ColumnDefinition("BoolReq", DataType.Boolean) { IsRequired = true },
    };

    private static Row Good() => new() { ["ReqLong"] = 1, ["TextReq"] = "x" };

    private static Column Column(Table table, string name) => table.Columns.Single(c => c.Name == name);

    // ── Read ─────────────────────────────────────────────────────────────────

    [Fact]
    public void What_Access_set_is_read_from_a_table_DAO_made()
    {
        CopyDaoMade();
        using var db = Database.Open(_path);
        var table = db.GetTable("DaoMade");

        var required = new[] { "ReqLong", "TextReq", "TextReqAzl", "MemoReq", "BoolReq", "DateReq" };
        Assert.Equal(required.OrderBy(n => n), table.Columns.Where(c => c.IsRequired).Select(c => c.Name).OrderBy(n => n));

        // DAO's default for a Text or Memo field is not to allow a zero-length string.
        var allowing = new[] { "TextAzl", "TextReqAzl", "MemoAzl" };
        Assert.Equal(allowing.OrderBy(n => n), table.Columns.Where(c => c.AllowZeroLength).Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public void A_column_with_no_property_is_not_required_and_takes_a_zero_length_string()
    {
        CopyDaoMade();
        using var db = Database.Open(_path);
        var table = db.GetTable("SqlMade");   // CREATE TABLE gives only a NOT NULL column a property: Required

        Assert.Equal(new[] { "ReqLong", "TextReq" }, table.Columns.Where(c => c.IsRequired).Select(c => c.Name));
        Assert.Equal(new[] { "TextReq", "TextOpt", "MemoOpt" }, table.Columns.Where(c => c.AllowZeroLength).Select(c => c.Name));

        // As Access takes them: asked of ACE, which stored each of these rows.
        table.Insert(new Row { ["ReqLong"] = 1, ["TextReq"] = "" });
        table.Insert(new Row { ["ReqLong"] = 1, ["TextReq"] = "x", ["TextOpt"] = "", ["MemoOpt"] = "" });
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["TextReq"] = "x" }));
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["ReqLong"] = 1 }));
        Assert.Equal(2, table.RowCount);
    }

    [Fact]
    public void A_column_keeps_what_it_says_after_its_database_is_closed()
    {
        Column required, plain;
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", Columns());
            required = Column(table, "ReqLong");
            plain = Column(table, "TextNoZero");
        }
        Assert.True(required.IsRequired);
        Assert.False(plain.IsRequired);
        Assert.False(plain.AllowZeroLength);
    }

    // ── Written ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2010)]
    public void A_table_made_here_says_what_it_was_asked_when_it_is_opened_again(FileFormat format)
    {
        using (var db = Database.Create(_path, format))
        {
            var made = db.CreateTable("T", Columns(), "Id");
            made.AddColumn(new ColumnDefinition("AddedReq", DataType.Text) { IsRequired = true, AllowZeroLength = false });
            made.AddColumn(new ColumnDefinition("AddedPlain", DataType.Memo));
            Assert.True(Column(made, "AddedReq").IsRequired);
        }

        using var read = Database.Open(_path);
        var table = read.GetTable("T");
        Assert.Equal(new[] { "ReqLong", "TextReq", "BoolReq", "AddedReq" }, table.Columns.Where(c => c.IsRequired).Select(c => c.Name));
        Assert.Equal(new[] { "TextDefault", "TextReq", "AddedPlain" }, table.Columns.Where(c => c.AllowZeroLength).Select(c => c.Name));

        // The properties DAO gives a field it makes: AllowZeroLength for Text and Memo, then Required; none for
        // an AutoNumber.
        Assert.Empty(Column(table, "Id").Properties);
        Assert.Equal(new[] { "Required" }, Column(table, "OptLong").Properties.Select(p => p.Name));
        var text = Column(table, "TextNoZero").Properties;
        Assert.Equal(new[] { "AllowZeroLength", "Required" }, text.Select(p => p.Name));
        Assert.All(text, p => Assert.Equal((DataType.Boolean, true), (p.DataType, p.IsDdl)));
        Assert.Equal(new object[] { false, false }, text.Select(p => p.Value));
        Assert.Equal(new object[] { false, true }, Column(table, "AddedReq").Properties.Select(p => p.Value));
        Assert.Empty(table.Properties);
    }

    [Fact]
    public void A_column_s_rule_changes_with_its_property()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", Columns(), "Id");
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["TextReq"] = "x" }));

            Column(table, "ReqLong").Properties.Set("Required", false);
            Column(table, "OptLong").Properties.Set("Required", true);
            table.Properties.Save();
            table.Insert(new Row { ["TextReq"] = "x", ["OptLong"] = 2 });
            Assert.Throws<ConstraintViolationException>(() => table.Insert(Good()));
        }
        using var read = Database.Open(_path);
        Assert.False(Column(read.GetTable("T"), "ReqLong").IsRequired);
        Assert.True(Column(read.GetTable("T"), "OptLong").IsRequired);
    }

    // ── Held to ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_row_is_refused_for_what_Access_refuses_it_for_and_nothing_is_written()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("T", Columns(), "Id").Insert(Good());
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            void Refused(string column, Action<Row> change)
            {
                var row = Good();
                change(row);
                var given = new Row(row);
                var refused = Assert.Throws<ConstraintViolationException>(() => table.Insert(row));
                Assert.Contains($"T.{column}", refused.Message);
                Assert.Equal(given.OrderBy(v => v.Key), row.OrderBy(v => v.Key));   // the row is as it came: no Id
                Assert.Throws<ConstraintViolationException>(() => table.Validate(row));
            }

            Refused("ReqLong", row => row.Remove("ReqLong"));
            Refused("ReqLong", row => row["ReqLong"] = null);
            Refused("ReqLong", row => row["ReqLong"] = DBNull.Value);
            Refused("TextReq", row => row.Remove("TextReq"));
            Refused("TextNoZero", row => row["TextNoZero"] = "");
            Refused("MemoNoZero", row => row["MemoNoZero"] = "");
            Assert.Equal(1, table.LastAutoNumber);
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused row changed the file");

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            // What Access takes: a zero-length string where it is allowed - in a required column too - spaces,
            // and no value for a required Yes/No, which is No.
            table.Insert(new Row { ["ReqLong"] = 2, ["TextReq"] = "", ["TextDefault"] = "" });
            table.Insert(new Row { ["ReqLong"] = 3, ["TextReq"] = "x", ["TextNoZero"] = "   ", ["MemoNoZero"] = " " });
            table.Validate(new Row { ["ReqLong"] = 4, ["TextReq"] = "x" });
            Assert.Equal(new object?[] { 1, 2, 3 }, table.Rows.Select(r => r["Id"]));
            Assert.All(table.Rows, r => Assert.Equal(false, r["BoolReq"]));
        }
    }

    [Fact]
    public void An_update_is_refused_only_for_the_columns_it_gives_a_value()
    {
        // A row this library wrote before it held to the properties: nulls in required columns and a zero-length
        // string where none is allowed. The properties are written afterwards, so the row is there.
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", Columns().Select(c => new ColumnDefinition(c.Name, c.DataType) { MaxLength = c.MaxLength, IsAutoNumber = c.IsAutoNumber }), "Id");
            table.Insert(new Row { ["OptLong"] = 7, ["TextNoZero"] = "" });
            Column(table, "ReqLong").Properties.Set("Required", true);
            Column(table, "TextReq").Properties.Set("Required", true);
            Column(table, "TextNoZero").Properties.Set("AllowZeroLength", false);
            table.Properties.Save();
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            // Access updates such a row in another column, and refuses the column that breaks the rule only when
            // the update gives it a value that breaks it.
            table.UpdateByPrimaryKey(1, new Row { ["OptLong"] = 8 });
            table.UpdateByPrimaryKey(1, new Row { ["TextDefault"] = "" });
            Assert.Throws<ConstraintViolationException>(() => table.UpdateByPrimaryKey(1, new Row { ["ReqLong"] = null }));
            Assert.Throws<ConstraintViolationException>(() => table.UpdateByPrimaryKey(1, new Row { ["OptLong"] = 9, ["TextReq"] = DBNull.Value }));
            Assert.Throws<ConstraintViolationException>(() => table.UpdateByPrimaryKey(1, new Row { ["TextNoZero"] = "" }));
            table.UpdateByPrimaryKey(1, new Row { ["ReqLong"] = 3 });
        }

        using var read = Database.Open(_path);
        var row = read.GetTable("T").Rows.Single();
        Assert.Equal((8, 3, "", ""), ((int)row["OptLong"]!, (int)row["ReqLong"]!, (string)row["TextDefault"]!, (string)row["TextNoZero"]!));
        Assert.Null(row["TextReq"]);
    }

    // ── A value a cascade gives a column ─────────────────────────────────────

    /// <summary>
    /// P, keyed by its text K, with rows 'a' and 'b'; C, whose K refers to it, with two rows of 'a' and one of
    /// 'b'. The relationship cascades updates, and deletes by making the children's K null.
    /// </summary>
    private void ParentAndChild(bool childAllowsZeroLength, bool childRequired)
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var parent = db.CreateTable("P", new[] { new ColumnDefinition("K", DataType.Text) { MaxLength = 20 }, new ColumnDefinition("T", DataType.Text) }, "K");
        var child = db.CreateTable("C", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("K", DataType.Text) { MaxLength = 20, AllowZeroLength = childAllowsZeroLength, IsRequired = childRequired },
        }, "Id");
        db.CreateRelationship(new RelationshipDefinition("P", "K", "C", "K") { HasReferentialIntegrity = true, CascadeUpdates = true, CascadeNullOnDelete = true });
        parent.Insert(new Row { ["K"] = "a", ["T"] = "one" });
        parent.Insert(new Row { ["K"] = "b", ["T"] = "two" });
        child.Insert(new Row { ["Id"] = 1, ["K"] = "a" });
        child.Insert(new Row { ["Id"] = 2, ["K"] = "a" });
        child.Insert(new Row { ["Id"] = 3, ["K"] = "b" });
    }

    private List<string> Children()
    {
        using var db = Database.Open(_path);
        return db.GetTable("C").Rows.OrderBy(r => r["Id"]).Select(r => $"{r["Id"]}={r["K"] ?? "null"}").ToList();
    }

    [Fact]
    public void A_cascade_that_would_give_a_column_a_zero_length_string_it_does_not_take_is_refused_whole()
    {
        // Asked of ACE: "Cannot perform cascading operation. It would cause field 'C.K' to become a zero-length
        // string, which is not allowed." The parent is not changed either.
        ParentAndChild(childAllowsZeroLength: false, childRequired: false);
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var refused = Assert.Throws<ConstraintViolationException>(() => db.GetTable("P").UpdateByPrimaryKey("a", new Row { ["K"] = "" }));
            Assert.Contains("C.K", refused.Message);
            Assert.Contains("zero-length", refused.Message);
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused cascade changed the file");

        // Another key cascades, and a delete makes the children's key null: the column is not required.
        using (var db = Database.Open(_path))
        {
            db.GetTable("P").UpdateByPrimaryKey("b", new Row { ["K"] = "z" });
            db.GetTable("P").DeleteRow("K", "a");
        }
        Assert.Equal(new[] { "1=null", "2=null", "3=z" }, Children());
    }

    [Fact]
    public void A_cascade_gives_a_required_column_a_zero_length_string_it_allows_and_no_null()
    {
        ParentAndChild(childAllowsZeroLength: true, childRequired: true);
        using (var db = Database.Open(_path))
            db.GetTable("P").UpdateByPrimaryKey("a", new Row { ["K"] = "" });
        Assert.Equal(new[] { "1=", "2=", "3=b" }, Children());

        // Asked of ACE: "It would cause field 'C.K' to become Null, which is not allowed."
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
            Assert.Throws<ConstraintViolationException>(() => db.GetTable("P").DeleteRow("K", "b"));
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused cascade changed the file");
    }

    // ── Values a caller did not give: a column added to a table with rows, and an import ──────────────────

    [Fact]
    public void A_required_column_is_added_to_a_table_with_rows_which_keep_no_value_in_it()
    {
        // Asked of ACE and DAO: ALTER TABLE ... ADD COLUMN x LONG NOT NULL, and a required field appended with
        // DAO, are both taken by a table with rows; the rows read null there and are still updated in other
        // columns, and a new row needs a value.
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) }, "Id");
            table.Insert(new Row { ["Id"] = 1, ["Name"] = "before" });
            table.AddColumn(new ColumnDefinition("Req", DataType.Int32) { IsRequired = true });
            Assert.Null(table.Rows.Single()["Req"]);
            table.UpdateByPrimaryKey(1, new Row { ["Name"] = "after" });
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 2, ["Name"] = "new" }));
            table.Insert(new Row { ["Id"] = 2, ["Name"] = "new", ["Req"] = 5 });
        }
        using var read = Database.Open(_path);
        Assert.Equal(new object?[] { null, 5 }, read.GetTable("T").Rows.OrderBy(r => r["Id"]).Select(r => r["Req"]));
    }

    [Fact]
    public void Rows_an_import_adds_to_a_table_that_is_there_are_held_to_its_columns()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { IsRequired = true, AllowZeroLength = false } }, "Id");

        var source = new System.Data.DataTable("T");
        source.Columns.Add("Id", typeof(int));
        source.Columns.Add("Name", typeof(string));
        source.Rows.Add(1, "one");
        using (var db = Database.Open(_path))
            db.ImportTable(source, new ImportOptions { AppendIfExists = true });

        foreach (object bad in new object[] { DBNull.Value, "" })
        {
            source.Rows.Clear();
            source.Rows.Add(2, bad);
            using var db = Database.Open(_path);
            Assert.Throws<ConstraintViolationException>(() => db.ImportTable(source, new ImportOptions { AppendIfExists = true }));
        }
        using var read = Database.Open(_path);
        Assert.Equal(new object?[] { 1 }, read.GetTable("T").Rows.Select(r => r["Id"]));
    }

    [Fact]
    public void A_table_an_earlier_version_made_has_no_properties_and_takes_the_rows_it_took()
    {
        // Assets/engine-696ca52-long-keys.mdb was written by this library before it wrote any property.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "engine-696ca52-long-keys.mdb"), _path, overwrite: true);
        using var db = Database.Open(_path);
        foreach (string name in db.GetTableNames())
        {
            var table = db.GetTable(name);
            Assert.Empty(table.Properties);
            Assert.All(table.Columns, c =>
            {
                Assert.Empty(c.Properties);
                Assert.False(c.IsRequired);
                Assert.Equal(c.DataType is DataType.Text or DataType.Memo, c.AllowZeroLength);
            });
        }
    }

    // ── A Replication ID AutoNumber ──────────────────────────────────────────

    [Fact]
    public void A_Replication_ID_AutoNumber_has_the_default_value_Access_generates_one_from()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("Made", new[] { new ColumnDefinition("Key", DataType.Guid) { IsAutoNumber = true }, new ColumnDefinition("Txt", DataType.Text) });
            var added = db.CreateTable("Added", new[] { new ColumnDefinition("Txt", DataType.Text) });
            added.AddColumn(new ColumnDefinition("Key", DataType.Guid) { IsAutoNumber = true });
            db.CreateTable("Plain", new[] { new ColumnDefinition("Key", DataType.Guid), new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true } });
        }

        using var read = Database.Open(_path);
        foreach (string name in new[] { "Made", "Added" })
        {
            var key = Column(read.GetTable(name), "Key");
            Assert.True(key.IsAutoNumber);
            var property = Assert.Single(key.Properties);
            Assert.Equal(("DefaultValue", DataType.Memo, (object)"GenGUID()", true), (property.Name, property.DataType, property.Value, property.IsDdl));
        }
        // A Replication ID that is not an AutoNumber, and a Long AutoNumber, have no default value.
        Assert.Equal(new[] { "Required" }, Column(read.GetTable("Plain"), "Key").Properties.Select(p => p.Name));
        Assert.Empty(Column(read.GetTable("Plain"), "Id").Properties);
    }
}
