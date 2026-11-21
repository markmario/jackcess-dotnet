using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Database.Create"/> makes a file in each format Jackcess creates, where it made an Access 2000 .mdb
/// whatever was asked for: the file is of the format named, a table made in it
/// sorts as Access's own would there, and its rows and indexes are there when the file is opened again. That
/// Access itself opens, reads, adds to and compacts such a file is refereed by ACE in AccdbFormatsInAccessTests.
/// </summary>
public sealed class AccdbCreationTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string NewPath(string extension = ".accdb")
    {
        string path = Path.Combine(Path.GetTempPath(), $"created_{Guid.NewGuid():N}{extension}");
        _paths.Add(path);
        return path;
    }

    // Each format, the two version bytes its header has, and whether its text sorts in General.
    public static IEnumerable<object[]> Formats() => new[]
    {
        new object[] { FileFormat.Access2000,  (byte)0x01, (byte)0x00, false },
        new object[] { FileFormat.Access2007, (byte)0x02, (byte)0x00, false },
        new object[] { FileFormat.Access2010, (byte)0x03, (byte)0x01, true },
        new object[] { FileFormat.Access2016, (byte)0x05, (byte)0x00, true },
        new object[] { FileFormat.Access2019, (byte)0x06, (byte)0x00, true },
    };

    private static ColumnDefinition[] People() => new[]
    {
        new ColumnDefinition("Id", DataType.Int32),
        new ColumnDefinition("Name", DataType.Text) { MaxLength = 60 },
        new ColumnDefinition("Born", DataType.DateTime),
        new ColumnDefinition("Notes", DataType.Memo),
    };

    private static readonly string[] Names = { "résumé", "Æther", "naïve", "Zürich", "日本", "smörgåsbord", "a-b", "O'Brien", "ß", "Ελληνικά" };

    private static Row Person(int i) => new()
    {
        ["Id"]    = i,
        ["Name"]  = $"{Names[i % Names.Length]} {i * 7919 % 2000:D4}",
        ["Born"]  = new DateTime(1900, 1, 1).AddDays(i * 13),
        ["Notes"] = i % 3 == 0 ? null : new string((char)('a' + i % 26), i % 5 == 0 ? 3000 : 40),
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_created_file_is_of_the_format_asked_for(FileFormat version, byte code, byte after, bool general)
    {
        string path = NewPath(version == FileFormat.Access2000 ? ".mdb" : ".accdb");
        using (var db = Database.Create(path, version))
        {
            Assert.Equal(version, db.File.Format.Version);
            Assert.Empty(db.GetTableNames());
        }

        byte[] head = File.ReadAllBytes(path).AsSpan(0, 0x18).ToArray();
        Assert.Equal(version == FileFormat.Access2000 ? "Standard Jet DB" : "Standard ACE DB", System.Text.Encoding.ASCII.GetString(head, 4, 15));
        Assert.Equal(new[] { code, after }, new[] { head[0x14], head[0x15] });

        // Opened again, with no version named and with the wrong one, it is the format its header says.
        using (var db = Database.Open(path))
            Assert.Equal(version, db.File.Format.Version);
        using (var db = Database.Open(path, new DatabaseOpenOptions { FallbackFormat = FileFormat.Access2000 }))
        {
            Assert.Equal(version, db.File.Format.Version);
            var catalogName = db.GetTable("MSysObjects").Columns.Single(c => c.Name == "Name");
            Assert.Equal(general, catalogName.SortOrder.IsGeneral);
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Tables_made_in_a_created_file_keep_their_rows_and_indexes_when_it_is_opened_again(FileFormat version, byte code, byte after, bool general)
    {
        string path = NewPath();
        using (var db = Database.Create(path, version))
        {
            var table = db.CreateTable("People", People(), new[] { "Id" },
                new[] { new IndexDefinition("ByName", "Name"), new IndexDefinition("ByBorn", "Born") { IsUnique = true } });
            for (int i = 1; i <= 1500; i++)
                table.Insert(Person(i));
            db.CreateTable("Other", new[] { new ColumnDefinition("K", DataType.Text) { MaxLength = 20 } }, primaryKey: "K")
                .Insert(new Row { ["K"] = "é" });
        }

        using (var db = Database.Open(path))
        {
            Assert.Equal(new[] { "Other", "People" }, db.GetTableNames().OrderBy(n => n));
            var table = db.GetTable("People");
            Assert.Equal(general, table.Columns.Single(c => c.Name == "Name").SortOrder.IsGeneral);

            var rows = table.Rows.ToList();
            Assert.Equal(1500, rows.Count);
            foreach (var row in rows)
            {
                var expected = Person((int)row["Id"]!);
                Assert.Equal(expected["Name"], row["Name"]);
                Assert.Equal(expected["Born"], row["Born"]);
                Assert.Equal(expected["Notes"], row.TryGetValue("Notes", out var notes) ? notes : null);
            }
            Assert.Equal(Person(777)["Name"], table.FindRowByPrimaryKey(777)!["Name"]);
            Assert.Equal(3, IndexChecks.AssertEveryIndexHoldsTheRows(db, "People", expectedRows: 1500));

            // And it is added to, changed and deleted from as any file is.
            table.Insert(Person(1501));
            table.UpdateByPrimaryKey(10, new Row { ["Name"] = "changed" });
            table.DeleteRow("Id", 11);
            Assert.Throws<ConstraintViolationException>(() => table.Insert(Person(12)));
        }

        using (var db = Database.Open(path))
        {
            Assert.Equal(1500, db.GetTable("People").Rows.Count());
            Assert.Equal("changed", db.GetTable("People").FindRowByPrimaryKey(10)!["Name"]);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "People", expectedRows: 1500);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "Other", expectedRows: 1);
        }
        Assert.Equal(new[] { code, after }, File.ReadAllBytes(path).AsSpan(0x14, 2).ToArray());
    }

    [Fact]
    public void A_created_file_replaces_whatever_was_at_the_path()
    {
        string path = NewPath();
        using (var db = Database.Create(path, FileFormat.Access2000))
            db.CreateTable("Old", new[] { new ColumnDefinition("Id", DataType.Int32) });

        using (var db = Database.Create(path, FileFormat.Access2010))
        {
            Assert.Equal(FileFormat.Access2010, db.File.Format.Version);
            Assert.Empty(db.GetTableNames());
        }
    }

    // A version byte is one Jackcess gives a format to: 5 is Access 2016's and 6 Access 2019's. 5 used to be read
    // as the last format known, and 4, which no file has, as Access 2016's.
    [Theory]
    [InlineData(0x00, FileFormat.Access97)]
    [InlineData(0x01, FileFormat.Access2000)]
    [InlineData(0x02, FileFormat.Access2007)]
    [InlineData(0x03, FileFormat.Access2010)]
    [InlineData(0x04, FileFormat.Access2016)]
    [InlineData(0x05, FileFormat.Access2016)]
    [InlineData(0x06, FileFormat.Access2019)]
    [InlineData(0x07, FileFormat.Access2019)]
    public void A_version_byte_names_the_format_Jackcess_names(byte code, FileFormat version)
    {
        Assert.Equal(version, JetFormat.ForVersionCode(code).Version);
    }

    [Fact]
    public void The_corpus_s_extended_date_file_is_an_Access_2019_one()
    {
        using var db = Database.Open(TestCorpus.File("V2019", "extDateV2019.accdb") ?? throw new FileNotFoundException("extDateV2019.accdb"));
        Assert.Equal(FileFormat.Access2019, db.File.Format.Version);
    }
}
