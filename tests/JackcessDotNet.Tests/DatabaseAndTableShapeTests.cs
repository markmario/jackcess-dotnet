using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What a caller sees of a <see cref="Database"/> and its tables beyond the rows: how one is created and opened,
/// what it says of itself, that a table is one object, and what is left after <see cref="Database.Dispose"/>.
/// </summary>
public sealed class DatabaseAndTableShapeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"shape_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Table People(Database db)
        => db.CreateTable("People", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        }, primaryKey: "Id");

    [Fact]
    public void A_database_created_with_no_format_is_an_Access_2000_one()
    {
        using (var db = Database.Create(_path))
        {
            Assert.Equal(FileFormat.Access2000, db.FileFormat);
            Assert.Equal(_path, db.Path);
            Assert.False(db.IsReadOnly);
        }

        byte[] made = File.ReadAllBytes(_path);
        using (Database.Create(_path, FileFormat.Access2000)) { }
        Assert.Equal(File.ReadAllBytes(_path), made);
    }

    [Theory]
    [InlineData(FileFormat.Access2007)]
    [InlineData(FileFormat.Access2010)]
    [InlineData(FileFormat.Access2016)]
    [InlineData(FileFormat.Access2019)]
    public void A_database_says_the_format_it_was_created_in_and_is_opened_in(FileFormat format)
    {
        using (var db = Database.Create(_path, format))
            Assert.Equal(format, db.FileFormat);
        using (var db = Database.Open(_path))
        {
            Assert.Equal(format, db.FileFormat);
            Assert.Equal(_path, db.Path);
            Assert.False(db.IsReadOnly);
        }
    }

    [Fact]
    public void Options_that_say_nothing_open_a_file_as_no_options_do()
    {
        using (var db = Database.Create(_path))
            People(db).Insert(new Row { ["Id"] = 1, ["Name"] = "Ada" });

        using (var db = Database.Open(_path, new DatabaseOpenOptions()))
        {
            Assert.Equal(FileFormat.Access2000, db.FileFormat);
            Assert.Equal("Ada", Assert.Single(db.GetTable("People").Rows)["Name"]);
        }
        Assert.Throws<ArgumentNullException>(() => Database.Open(_path, null!));
    }

    [Fact]
    public void A_file_with_no_header_is_refused_unless_a_fallback_format_says_what_it_is()
    {
        File.WriteAllBytes(_path, new byte[4096 * 2]);

        Assert.Throws<InvalidDataException>(() => Database.Open(_path));
        Assert.Throws<InvalidDataException>(() => Database.Open(_path, new DatabaseOpenOptions()));
        using var db = Database.Open(_path, new DatabaseOpenOptions { FallbackFormat = FileFormat.Access2000 });
        Assert.Equal(FileFormat.Access2000, db.FileFormat);
    }

    [Fact]
    public void The_database_s_own_tables_and_its_system_tables_are_listed_apart()
    {
        using var db = Database.Create(_path);
        People(db);

        Assert.Equal(new[] { "People" }, db.GetTableNames());
        var system = db.GetSystemTableNames();
        Assert.Contains("MSysObjects", system);
        Assert.Contains("MSysRelationships", system);
        Assert.DoesNotContain("People", system);
        foreach (string name in system)
            Assert.Equal(name, db.GetTable(name).Name);
    }

    [Fact]
    public void A_table_is_one_object_while_its_database_is_open()
    {
        using (var db = Database.Create(_path))
        {
            var created = People(db);
            Assert.Same(created, db.GetTable("People"));
            Assert.Same(created, db.GetTable("PEOPLE"));
            Assert.True(db.TryGetTable("people", out var found));
            Assert.Same(created, found);

            // What is set on the table is set for whoever asks for it.
            created.AllowAutoNumberInsert = true;
            Assert.True(db.GetTable("People").AllowAutoNumberInsert);
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("pEOPLE");
            Assert.Equal("People", table.Name);   // as the catalog spells it
            Assert.Null(table.AllowAutoNumberInsert);
            Assert.Same(table, db.GetTable("People"));
        }
    }

    [Fact]
    public void Rows_are_read_as_they_are_asked_for_and_again_each_time()
    {
        using var db = Database.Create(_path);
        var table = People(db);
        for (int i = 1; i <= 2_000; i++)
            table.Insert(new Row { ["Id"] = i, ["Name"] = "row " + i });

        var rows = table.Rows;
        long before = db.File.ReadCount;
        Assert.Equal(1, rows.First()["Id"]);
        long forOne = db.File.ReadCount - before;
        before = db.File.ReadCount;
        Assert.Equal(2_000, rows.Count());
        long forAll = db.File.ReadCount - before;
        Assert.True(forOne < forAll / 4, $"the first row read {forOne} pages, all of them {forAll}");

        // A row added since is in the next enumeration of the same sequence.
        table.Insert(new Row { ["Id"] = 2_001, ["Name"] = "late" });
        Assert.Equal(2_001, rows.Count());
        Assert.Equal(Enumerable.Range(1, 2_001), rows.Select(r => (int)r["Id"]!));
    }

    [Fact]
    public void A_row_is_found_by_a_primary_key_of_several_columns()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("Lines", new[]
        {
            new ColumnBuilder("Order", DataType.Long).Build(),
            new ColumnBuilder("Line", DataType.Long).Build(),
            new ColumnBuilder("What", DataType.Text).MaxLength(50).Build(),
        }, new[] { "Order", "Line" });
        for (int order = 1; order <= 3; order++)
            for (int line = 1; line <= 3; line++)
                table.Insert(new Row { ["Order"] = order, ["Line"] = line, ["What"] = $"{order}/{line}" });

        Assert.Equal("2/3", table.FindRowByPrimaryKey(2, 3)!["What"]);
        Assert.Equal("3/1", table.FindRowByPrimaryKey(3, 1)!["What"]);
        Assert.Null(table.FindRowByPrimaryKey(4, 1));

        var wrong = Assert.Throws<ArgumentException>(() => table.FindRowByPrimaryKey(2));
        Assert.Equal("primaryKey", wrong.ParamName);
        Assert.Throws<ArgumentNullException>(() => table.FindRowByPrimaryKey(null!));
    }

    [Fact]
    public void A_table_with_no_primary_key_has_no_row_to_find_by_it()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("Loose", new[] { new ColumnBuilder("Id", DataType.Long).Build() });
        table.Insert(new Row { ["Id"] = 1 });

        Assert.Throws<InvalidOperationException>(() => table.FindRowByPrimaryKey(1));
    }

    [Fact]
    public void An_index_cursor_says_which_index_it_is_for()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("People", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByName", "Name") });

        Assert.True(table.CreateIndexCursor().Index!.IsPrimaryKey);
        Assert.Equal("ByName", table.CreateIndexCursor("byname").Index!.Name);
        Assert.Null(table.CreateIndexCursor("NoSuchIndex").Index);
        Assert.Throws<ArgumentNullException>(() => table.CreateIndexCursor(null!));

        var loose = db.CreateTable("Loose", new[] { new ColumnBuilder("Id", DataType.Long).Build() });
        Assert.Null(loose.CreateIndexCursor().Index);
    }

    [Fact]
    public void A_database_disposed_twice_is_closed_once_and_refuses_what_is_asked_of_it_after()
    {
        var db = Database.Create(_path);
        var table = People(db);
        table.Insert(new Row { ["Id"] = 1, ["Name"] = "Ada" });
        var cursor = table.CreateCursor();
        var rows = table.Rows;

        db.Dispose();
        db.Dispose();

        // What the database is, it still says.
        Assert.Equal(_path, db.Path);
        Assert.Equal(FileFormat.Access2000, db.FileFormat);
        Assert.False(db.IsReadOnly);

        var refused = new Action[]
        {
            () => db.GetTable("People"),
            () => db.TryGetTable("People", out _),
            () => db.GetTableNames(),
            () => db.GetSystemTableNames(),
            () => db.GetRelationships(),
            () => db.CreateTable("More", new[] { new ColumnBuilder("Id", DataType.Long).Build() }),
            () => db.CreateRelationship(new RelationshipDefinition("People", "Id", "People", "Id")),
            () => db.FindDecimalsBeyondPrecision(),
            () => _ = db.EnforceForeignKeys,
            () => db.EnforceForeignKeys = false,
            () => _ = db.AllowAutoNumberInsert,
            () => db.AllowAutoNumberInsert = true,
            () => _ = db.VerifyIndexes,
            () => db.VerifyIndexes = true,
            () => _ = db.ShareLongValuePages,
            () => db.ShareLongValuePages = true,
            // What was got from it goes to the file, which is closed.
            () => table.Insert(new Row { ["Id"] = 2, ["Name"] = "Bea" }),
            () => rows.ToList(),
            () => _ = table.RowCount,
            () => table.FindRowByPrimaryKey(1),
            () => cursor.MoveNext(),
        };
        for (int i = 0; i < refused.Length; i++)
        {
            var thrown = Record.Exception(refused[i]);
            Assert.True(thrown is ObjectDisposedException, $"call {i} threw {thrown?.GetType().Name ?? "nothing"}");
        }

        // And the file is whole: it opens, with the row written before it was closed.
        using var again = Database.Open(_path);
        Assert.Equal("Ada", Assert.Single(again.GetTable("People").Rows)["Name"]);
    }
}
