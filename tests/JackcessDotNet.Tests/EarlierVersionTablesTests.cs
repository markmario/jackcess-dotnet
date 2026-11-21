using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Tables an earlier version of this library created (#93). The JackcessDotNet packages to 1.2.2 and this
/// repository's builds before 442bbf8 gave a table they created a catalog row with ParentId 0 and no Owner, gave its
/// columns no Unicode compression, and wrote every Text and Memo value of characters 0x01 to 0xFF - the table's name
/// among them - compressed: 0xFF 0xFE and a byte each. Access neither lists nor reads such a table; this library reads
/// its name and its text by their bytes, as it read them before it read text as Access does, so the table is found by
/// its name and its values read as they were written.
/// <para>
/// Assets/engine-1.2.2-created-table.mdb and engine-8e41563-created-table.mdb are databases the JackcessDotNet 1.2.2
/// package and a build of 8e41563 created, each with a table People; engine-8e41563-filled-table.mdb is a database DAO
/// made, with a table Filled whose Text and Memo have no Unicode compression, that 8e41563 wrote the same rows into.
/// tools/earlier-created-tables.ps1 made them, and wrote Assets/earlier-created-tables.tsv: what DAO reads of them.
/// </para>
/// </summary>
public sealed class EarlierVersionTablesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"earlier_version_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private string Copy(string asset)
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), _path);
        return _path;
    }

    // The rows tools/earlier-created-tables.ps1 has each version write: Id, Name, Notes.
    private static readonly (int Id, string Name, string? Notes)[] Written =
    {
        (1, "Alice", null),
        (2, "Bob", "Short note"),
        (3, "Zo\u00EB", "Zo\u00EB's note"),
        (4, "\u01FFab", "\u01FFab"),
        (5, "\u65E5\u672C\u8A9E", "\u65E5\u672C\u8A9E\u306E\u30E1\u30E2"),
        (6, "\uFEFFab", "\uFEFFab"),
        (7, "Long", string.Concat(Enumerable.Repeat("abcdefghij", 500))),
        (8, "Mixed", "abc\u65E5\u672C"),
    };

    /// <summary>
    /// What is read back: what was written, but text that begins with U+FEFF, which those versions wrote as UTF-16LE
    /// that begins 0xFF 0xFE - the bytes of compressed text - and which reads without it. 1.2.2 wrote long-value
    /// references that no version reads: its Memo values read as null.
    /// </summary>
    private static string? Read(string? written, bool memoOf122)
        => memoOf122 ? null : written is not null && written.StartsWith("\uFEFF", StringComparison.Ordinal) ? written.Substring(1) : written;

    [Theory]
    [InlineData("engine-1.2.2-created-table.mdb")]
    [InlineData("engine-8e41563-created-table.mdb")]
    public void A_table_an_earlier_version_created_is_found_by_its_name_and_read_as_written(string asset)
    {
        using var db = Database.Open(Copy(asset));
        Assert.Contains("People", db.GetTableNames());
        var table = db.GetTable("People");
        foreach (var (id, name, notes) in Written)
        {
            var row = table.FindRowByPrimaryKey(id)!;
            Assert.Equal(Read(name, memoOf122: false), row["Name"]);
            Assert.Equal(Read(notes, memoOf122: asset.Contains("1.2.2")), row["Notes"]);
        }
    }

    [Fact]
    public void The_engine_adds_to_such_a_table_what_it_reads_back_and_refuses_what_it_would_not()
    {
        using (var db = Database.Open(Copy("engine-8e41563-created-table.mdb")))
        {
            var table = db.GetTable("People");
            table.Insert(new Row { ["Id"] = 9, ["Name"] = "Dora", ["Notes"] = "\uFEFFabQ1Z" });
            table.Insert(new Row { ["Id"] = 10, ["Name"] = "\uFEFFaaaa", ["Notes"] = "\u65E5\u672C" });
            // Read by its bytes, UTF-16LE that begins 0xFF 0xFE would read back changed, wherever it is kept.
            Assert.Throws<ArgumentException>(() => table.Insert(new Row { ["Id"] = 11, ["Name"] = "\uFEFFabc" }));
            Assert.Throws<ArgumentException>(() => table.Insert(new Row { ["Id"] = 11, ["Notes"] = "\uFEFF" + new string('\u30C6', 3000) }));
        }
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("People");
            Assert.Equal("\uFEFFabQ1Z", table.FindRowByPrimaryKey(9)!["Notes"]);
            Assert.Equal("\uFEFFaaaa", table.FindRowByPrimaryKey(10)!["Name"]);
            Assert.Equal("Alice", table.FindRowByPrimaryKey(1)!["Name"]);
            Assert.Null(table.FindRowByPrimaryKey(11));
        }
    }

    /// <summary>
    /// A table Access made, without Unicode compression, that such a version filled: its compressed text is read as
    /// Access reads it from the table, as UTF-16LE - "Alice" as U+FEFF U+6C41 U+6369 - and this library reads it so too,
    /// with U+FFFD for the odd byte at the end, which DAO leaves out. Nothing tells it from text Access wrote. Its
    /// Memo values Access reads as compressed text, kept in the row, and so does this library.
    /// </summary>
    [Fact]
    public void A_table_Access_made_that_an_earlier_version_filled_is_read_as_Access_reads_it()
    {
        var dao = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Assets", "earlier-created-tables.tsv"))
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .Select(line => line.Split('\t'))
            .Where(cells => cells[0] == "8e41563" && cells[1] == "Filled" && cells[2].Length > 0)
            .ToList();
        Assert.Equal(16, dao.Count);

        using var db = Database.Open(Copy("engine-8e41563-filled-table.mdb"));
        var table = db.GetTable("Filled");
        Assert.Equal(8, table.Rows.Count());
        foreach (var cells in dao)
        {
            var read = table.Rows.Single(r => Convert.ToInt32(r["Id"]) == int.Parse(cells[2]))[cells[3]] as string;
            string expected = cells[4] == "null" ? null!
                : cells[4].StartsWith("length ", StringComparison.Ordinal) ? Written[int.Parse(cells[2]) - 1].Notes!
                : new string(cells[4].Split(' ').Select(unit => (char)Convert.ToInt32(unit, 16)).ToArray());
            Assert.Equal(expected, read?.TrimEnd('\uFFFD'));
        }
        Assert.Equal("\uFEFF\u6C41\u6369\uFFFD", table.Rows.Single(r => Convert.ToInt32(r["Id"]) == 1)["Name"]);
    }
}
