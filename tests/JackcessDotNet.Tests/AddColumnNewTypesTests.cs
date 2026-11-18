using JackcessDotNet;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A Large Number or a Date/Time Extended column added to a table that exists: the same rules as for a table
/// created with one. The file becomes one of Access 2016's format or Access 2019's, and an .mdb has no such column.
/// </summary>
public sealed class AddColumnNewTypesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"add_column_new_types_{Guid.NewGuid():N}");

    public AddColumnNewTypesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Column Col(string name, DataType type) => new ColumnBuilder(name, type).Build();

    private string CreateWithRows(string fileName, JetVersion version)
    {
        string path = Path.Combine(_dir, fileName);
        using var db = Database.Create(path, version);
        var table = db.CreateTable("Things", new[] { Col("Id", DataType.Long) });
        for (int i = 1; i <= 3; i++) table.Insert(new Row { ["Id"] = i });
        return path;
    }

    private static byte VersionOf(string path)
    {
        using var stream = File.OpenRead(path);
        stream.Position = DatabaseHeader.OffsetVersion;
        return (byte)stream.ReadByte();
    }

    [Theory]
    [InlineData(DataType.BigInt)]
    [InlineData(DataType.ExtendedDateTime)]
    public void Neither_is_added_to_a_table_of_an_mdb_and_nothing_is_changed(DataType type)
    {
        string path = CreateWithRows("old.mdb", JetVersion.Jet4);
        byte[] before = File.ReadAllBytes(path);

        using (var db = Database.Open(path))
        {
            var e = Assert.Throws<NotSupportedException>(() => db.GetTable("Things").AddColumn(Col("Newer", type)));
            Assert.Contains("an .mdb file", e.Message);
            Assert.Contains("was not added", e.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_large_number_column_added_makes_the_file_Access_2016_s_and_holds_its_values()
    {
        string path = CreateWithRows("large.accdb", JetVersion.Jet12);
        Assert.True(VersionOf(path) < DatabaseHeader.Version2016);

        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Things");
            table.AddColumn(Col("Big", DataType.BigInt));
            table.Insert(new Row { ["Id"] = 4, ["Big"] = long.MaxValue });
            table.Insert(new Row { ["Id"] = 5, ["Big"] = long.MinValue });
        }

        Assert.Equal(DatabaseHeader.Version2016, VersionOf(path));
        using var again = Database.Open(path);
        var rows = again.GetTable("Things").ReadAllRows();
        Assert.Equal(5, rows.Count);
        Assert.All(rows.Take(3), row => Assert.False(row.TryGetValue("Big", out var value) && value is not null));
        Assert.Equal(long.MaxValue, rows[3]["Big"]);
        Assert.Equal(long.MinValue, rows[4]["Big"]);
    }

    [Fact]
    public void An_extended_date_column_added_makes_the_file_Access_2019_s_and_holds_its_values_to_the_tick()
    {
        string path = CreateWithRows("dates.accdb", JetVersion.Jet14);
        Assert.True(VersionOf(path) < DatabaseHeader.Version2019);
        var when = new DateTime(2026, 11, 18, 9, 30, 0).AddTicks(1234567);

        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Things");
            table.AddColumn(Col("Seen", DataType.ExtendedDateTime));
            table.Insert(new Row { ["Id"] = 4, ["Seen"] = when });
        }

        Assert.Equal(DatabaseHeader.Version2019, VersionOf(path));
        using var again = Database.Open(path);
        var rows = again.GetTable("Things").ReadAllRows();
        Assert.Equal(4, rows.Count);
        Assert.Equal(when, rows[3]["Seen"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_table_with_a_newer_column_whose_creation_fails_leaves_the_file_s_version_as_it_was(int writesBeforeFailure)
    {
        // The header is the last thing written: a creation stopped at any earlier write has not touched it.
        string path = CreateWithRows("fails.accdb", JetVersion.Jet12);
        byte version = VersionOf(path);

        using (var db = Database.Open(path))
        {
            db.File.WritesBeforeFailure = writesBeforeFailure;
            Assert.ThrowsAny<Exception>(() => db.CreateTable("Newer", new[] { Col("Id", DataType.Long), Col("Big", DataType.BigInt) }));
            db.File.WritesBeforeFailure = null;
        }

        Assert.Equal(version, VersionOf(path));
        using var again = Database.Open(path);
        Assert.DoesNotContain("Newer", again.ListTables());
    }

    [Fact]
    public void A_column_of_an_older_type_leaves_the_file_s_version_as_it_was()
    {
        string path = CreateWithRows("plain.accdb", JetVersion.Jet12);
        byte version = VersionOf(path);

        using (var db = Database.Open(path))
            db.GetTable("Things").AddColumn(Col("Words", DataType.Memo));

        Assert.Equal(version, VersionOf(path));
    }
}
