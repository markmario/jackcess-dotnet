using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A column's Required and Allow Zero Length are properties, read, written and held to (#41). Access keeps
/// neither in the table's definition. What it refuses was asked of ACE, and these hold the library to the same:
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
}
