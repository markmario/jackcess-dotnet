using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="ColumnDefinition"/>: what a caller asks a column to be, and the <see cref="Column"/> a table then
/// has. A definition is checked when a table is made or changed with it, and one that is refused leaves nothing
/// made.
/// </summary>
public sealed class ColumnDefinitionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"coldef_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>The column a new table has for <paramref name="definition"/>, read back from the file.</summary>
    private Column Made(ColumnDefinition definition)
    {
        using (var db = Database.Create(_path))
            db.CreateTable("T", new[] { definition });
        using var again = Database.Open(_path);
        return again.GetTable("T").Columns.Single();
    }

    /// <summary>The column a new table has for <paramref name="definition"/>, in the session that makes it.</summary>
    private Column MadeInSession(ColumnDefinition definition)
    {
        using var db = Database.Create(_path);
        return db.CreateTable("T", new[] { definition }).Columns.Single();
    }

    /// <summary>The definition is refused by CreateTable and by AddColumn, as the caller's argument, and nothing is made.</summary>
    private void AssertRefused(ColumnDefinition definition)
    {
        using var db = Database.Create(_path);
        var existing = db.CreateTable("Existing", new[] { new ColumnDefinition("Id", DataType.Int32) });
        long written = db.File.WriteCount;

        var created = Assert.Throws<ArgumentException>(() => db.CreateTable("T", new[] { new ColumnDefinition("Fine", DataType.Int32), definition }));
        Assert.Equal("columns", created.ParamName);
        var added = Assert.Throws<ArgumentException>(() => existing.AddColumn(definition));
        Assert.Equal("column", added.ParamName);

        Assert.Equal(written, db.File.WriteCount);
        Assert.Equal(new[] { "Existing" }, db.GetTableNames());
        Assert.Single(existing.Columns);
    }

    [Fact]
    public void Ctor_RequiresName()
    {
        Assert.Throws<ArgumentNullException>(() => new ColumnDefinition(null!, DataType.Int32));
    }

    [Fact]
    public void A_definition_says_what_it_was_given_and_its_defaults()
    {
        var definition = new ColumnDefinition("Title", DataType.Text);

        Assert.Equal("Title", definition.Name);
        Assert.Equal(DataType.Text, definition.DataType);
        Assert.Null(definition.MaxLength);
        Assert.False(definition.IsRequired);
        Assert.False(definition.IsAutoNumber);
        Assert.True(definition.AllowZeroLength);
        Assert.Equal((byte)18, definition.Precision);
        Assert.Equal((byte)0, definition.Scale);
    }

    [Fact]
    public void EmptyName_IsRefused() => AssertRefused(new ColumnDefinition("", DataType.Int32));

    [Fact]
    public void NameLongerThan64_IsRefused() => AssertRefused(new ColumnDefinition(new string('x', 65), DataType.Int32));

    [Fact]
    public void NameExactly64Chars_Succeeds()
    {
        var col = Made(new ColumnDefinition(new string('x', 64), DataType.Int32));
        Assert.Equal(64, col.Name.Length);
    }

    [Fact]
    public void MaxLength_OnBinary_IsBytes()
    {
        var col = Made(new ColumnDefinition("X", DataType.Binary) { MaxLength = 200 });
        Assert.Equal(200, col.MaxLength);
        Assert.Equal(200, col.LengthInBytes);
    }

    [Fact]
    public void MaxLength_OnText_IsCharacters()
    {
        var col = Made(new ColumnDefinition("X", DataType.Text) { MaxLength = 50 });
        Assert.Equal(50, col.MaxLength);
        Assert.Equal(100, col.LengthInBytes); // 50 chars × 2 bytes UTF-16
    }

    [Fact]
    public void MaxLength_NotSet_IsTheTypesDefault()
    {
        Assert.Equal(50, Made(new ColumnDefinition("X", DataType.Text)).MaxLength);
        File.Delete(_path);
        Assert.Equal(50, Made(new ColumnDefinition("X", DataType.Binary)).MaxLength);
    }

    [Theory]
    [InlineData(DataType.Int32)]
    [InlineData(DataType.Memo)]
    [InlineData(DataType.OleObject)]
    [InlineData(DataType.Boolean)]
    [InlineData(DataType.Decimal)]
    public void MaxLength_OnATypeWithNoLengthToChoose_IsRefused(DataType type)
        => AssertRefused(new ColumnDefinition("X", type) { MaxLength = 50 });

    [Theory]
    [InlineData(DataType.Text, 256)]    // Text holds 255 characters
    [InlineData(DataType.Text, 512)]
    [InlineData(DataType.Text, 0)]
    [InlineData(DataType.Text, -1)]
    [InlineData(DataType.Binary, 256)]  // Binary holds 255 bytes
    [InlineData(DataType.Binary, 0)]
    [InlineData(DataType.Text, int.MaxValue)]
    public void MaxLength_OutsideWhatTheTypeHolds_IsRefused(DataType type, int length)
        => AssertRefused(new ColumnDefinition("X", type) { MaxLength = length });

    [Theory]
    [InlineData(DataType.Text, 1)]
    [InlineData(DataType.Text, 255)]
    [InlineData(DataType.Binary, 1)]
    [InlineData(DataType.Binary, 255)]
    public void MaxLength_AtTheEdgesOfWhatTheTypeHolds_IsTaken(DataType type, int length)
        => Assert.Equal(length, Made(new ColumnDefinition("X", type) { MaxLength = length }).MaxLength);

    [Fact]
    public void FixedLength_HasTheTypesSize()
    {
        var col = Made(new ColumnDefinition("X", DataType.Int32));
        Assert.Equal(4, col.LengthInBytes);
        Assert.Equal(4, col.MaxLength);
    }

    [Fact]
    public void IsRequired_SetsFlag()
    {
        Assert.True(MadeInSession(new ColumnDefinition("X", DataType.Int32) { IsRequired = true }).IsRequired);
        Assert.False(MadeInSession(new ColumnDefinition("X", DataType.Int32)).IsRequired);
    }

    [Theory]
    [InlineData(DataType.Text)]
    [InlineData(DataType.Int16)]
    [InlineData(DataType.Int64)]
    [InlineData(DataType.Double)]
    public void IsAutoNumber_OnATypeThatIsNotNumbered_IsRefused(DataType type)
        => AssertRefused(new ColumnDefinition("X", type) { IsAutoNumber = true });

    [Theory]
    [InlineData(DataType.Int32)]
    [InlineData(DataType.Guid)]
    public void IsAutoNumber_OnInt32AndGuid_SetsFlag(DataType type)
    {
        var col = Made(new ColumnDefinition("X", type) { IsAutoNumber = true });
        Assert.Equal(type, col.DataType);
        Assert.True(col.IsAutoNumber);
    }

    [Fact]
    public void IsAutoNumber_SetFalse_IsNoAutoNumberOfAnyType()
        => Assert.False(Made(new ColumnDefinition("X", DataType.Text) { IsAutoNumber = false }).IsAutoNumber);

    [Fact]
    public void AllowZeroLength_DefaultsTrue_CanBeDisabled()
    {
        Assert.True(MadeInSession(new ColumnDefinition("X", DataType.Text)).AllowZeroLength);
        Assert.False(MadeInSession(new ColumnDefinition("X", DataType.Text) { AllowZeroLength = false }).AllowZeroLength);
    }

    [Fact]
    public void Decimal_DefaultsPrecision18Scale0()
    {
        var col = Made(new ColumnDefinition("a", DataType.Decimal));
        Assert.Equal(DataType.Decimal, col.DataType);
        Assert.Equal((byte)18, col.Precision);
        Assert.Equal((byte)0,  col.Scale);
    }

    [Fact]
    public void Decimal_TakesPrecisionAndScale()
    {
        var col = Made(new ColumnDefinition("X", DataType.Decimal) { Precision = 10, Scale = 3 });
        Assert.Equal((byte)10, col.Precision);
        Assert.Equal((byte)3,  col.Scale);
    }

    [Theory]
    [InlineData(DataType.Int16)]
    [InlineData(DataType.Int32)]
    [InlineData(DataType.Single)]
    [InlineData(DataType.Double)]
    [InlineData(DataType.DateTime)]
    [InlineData(DataType.Boolean)]
    [InlineData(DataType.Memo)]
    [InlineData(DataType.Currency)]
    [InlineData(DataType.Guid)]
    [InlineData(DataType.Byte)]
    [InlineData(DataType.OleObject)]
    public void The_column_made_is_of_the_type_asked_for(DataType type)
    {
        var col = Made(new ColumnDefinition("a", type));
        Assert.Equal(type, col.DataType);
        Assert.False(col.IsAutoNumber);
    }

    [Fact]
    public void The_same_definition_makes_a_column_in_each_table_it_is_given_to()
    {
        var definition = new ColumnDefinition("Name", DataType.Text) { MaxLength = 20, IsRequired = true };
        using var db = Database.Create(_path);
        var one = db.CreateTable("One", new[] { new ColumnDefinition("Id", DataType.Int32), definition });
        var two = db.CreateTable("Two", new[] { definition });
        var three = db.CreateTable("Three", new[] { new ColumnDefinition("Id", DataType.Int32) });
        var added = three.AddColumn(definition);

        Assert.NotSame(one.Columns[1], two.Columns[0]);
        Assert.Equal(1, one.Columns[1].Ordinal);
        Assert.Equal(0, two.Columns[0].Ordinal);
        Assert.Equal(1, added.Ordinal);
        foreach (var column in new[] { one.Columns[1], two.Columns[0], added })
        {
            Assert.Equal("Name", column.Name);
            Assert.Equal(DataType.Text, column.DataType);
            Assert.Equal(20, column.MaxLength);
        }
        Assert.True(one.Columns[1].IsRequired);
        Assert.True(two.Columns[0].IsRequired);

        // The definition is the caller's still, and as it was.
        Assert.Equal(20, definition.MaxLength);
    }

    [Fact]
    public void A_null_definition_is_refused()
    {
        using var db = Database.Create(_path);
        Assert.Throws<ArgumentNullException>(() => db.CreateTable("T", (IEnumerable<ColumnDefinition>)null!));
        Assert.Throws<ArgumentException>(() => db.CreateTable("T", new ColumnDefinition[] { null! }));
        Assert.Throws<ArgumentException>(() => db.CreateTable("T", Array.Empty<ColumnDefinition>()));
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) });
        Assert.Throws<ArgumentNullException>(() => table.AddColumn(null!));
    }

    [Fact]
    public void Column_ToString_HasNameAndType()
    {
        var col = Made(new ColumnDefinition("Title", DataType.Text) { MaxLength = 50 });
        string s = col.ToString();
        Assert.Contains("Title", s);
        Assert.Contains("Text",  s);
    }
}
