using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table, column, index or relationship is refused a name Access takes as one another of its kind has, before
/// anything is written: names that differ only in case, and the other differences Access does not tell apart. Measured
/// with DAO 16 in an .mdb and an .accdb, by appending a second table, field and index of each name: DAO refuses each
/// a name it takes as one another has. Names Access tells apart are both kept.
/// </summary>
public sealed class ObjectNameTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"names_{Guid.NewGuid():N}.accdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string U(params int[] codes) => new(codes.Select(c => (char)c).ToArray());

    // Pairs of names Access takes as one (measured with DAO 16 in an .mdb and an .accdb).
    public static IEnumerable<object[]> OneToAccess() => new[]
    {
        new object[] { "A", "a" }, new object[] { "Name", "NAME" }, new object[] { U(0xC4), U(0xE4) },
        new object[] { U(0xDF), "ss" }, new object[] { U(0xE6), "ae" }, new object[] { U(0xFF21), "A" },
        new object[] { U(0x3042), U(0x30A2) }, new object[] { U(0xE1), U(0x61, 0x301) }, new object[] { U(0x416), U(0x436) },
    };

    // Pairs Access tells apart.
    public static IEnumerable<object[]> TwoToAccess() => new[]
    {
        new object[] { "e", U(0xE9) }, new object[] { "I", U(0x131) }, new object[] { "a b", "a  b" },
    };

    private static ColumnDefinition Text(string name) => new(name, DataType.Text) { MaxLength = 20 };

    private void Unchanged(Action<Database> refused) => Unchanged<ArgumentException>(refused);

    private void Unchanged<TException>(Action<Database> refused) where TException : Exception
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
            Assert.Throws<TException>(() => refused(db));
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Theory]
    [MemberData(nameof(OneToAccess))]
    public void A_table_is_refused_two_columns_Access_takes_as_one(string first, string second)
    {
        Database.Create(_path, FileFormat.Access2010).Dispose();
        Unchanged(db => db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text(first), Text(second) }, "Id"));
        using var reopen = Database.Open(_path);
        Assert.False(reopen.TryGetTable("T", out _));
    }

    [Theory]
    [MemberData(nameof(TwoToAccess))]
    public void Columns_Access_tells_apart_are_two_and_keep_their_values(string first, string second)
    {
        using (var db = Database.Create(_path, FileFormat.Access2010))
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text(first), Text(second) }, "Id")
              .Insert(new Row { ["Id"] = 1, [first] = "one", [second] = "two" });
        using var reopen = Database.Open(_path);
        var row = reopen.GetTable("T").FindRowByPrimaryKey(1)!;
        Assert.Equal("one", row[first]);
        Assert.Equal("two", row[second]);
    }

    [Theory]
    [MemberData(nameof(OneToAccess))]
    public void A_column_index_or_table_is_refused_a_name_Access_takes_as_one_it_has(string first, string second)
    {
        using (var db = Database.Create(_path, FileFormat.Access2010))
            db.CreateTable(first, new[] { new ColumnDefinition("Id", DataType.Int32), Text(first), new ColumnDefinition("N", DataType.Int32) },
                           new[] { "Id" }, new[] { new IndexDefinition(first, "N") });
        Unchanged(db => db.GetTable(first).AddColumn(Text(second)));
        Unchanged(db => db.GetTable(first).RenameColumn("N", second));
        Unchanged(db => db.GetTable(first).AddIndex(new IndexDefinition(second, "Id")));
        // A name an object of the database has is refused as before, with InvalidOperationException.
        Unchanged<InvalidOperationException>(db => db.CreateTable(second, new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"));
    }

    // Every pair measured with DAO: whether Access takes the two names as one, and so does this library. Where
    // Access takes as one names this library does not (a ligature, ⁵ and 5), or tells apart names .NET's comparison
    // without case takes as one, the pair is in neither list.
    public static IEnumerable<object[]> Measured() => new[]
    {
        new object[] { "A", "a", true }, new object[] { "AB", "ab", true }, new object[] { "e", U(0xE9), false },
        new object[] { "E", U(0xE9), false }, new object[] { U(0xC9), U(0xE9), true }, new object[] { U(0xC4), U(0xE4), true },
        new object[] { U(0xDF), "ss", true }, new object[] { U(0xE6), "ae", true }, new object[] { U(0xFF21), "A", true },
        new object[] { U(0xFF41), "a", true }, new object[] { "I", U(0x131), false }, new object[] { "i", U(0x130), false },
        new object[] { U(0x3A9), U(0x3C9), true }, new object[] { U(0x416), U(0x436), true }, new object[] { U(0xFF71), U(0x30A2), true },
        new object[] { U(0x3042), U(0x30A2), true }, new object[] { U(0xE1), U(0x61, 0x301), true }, new object[] { U(0x1C4), U(0x1C6), true },
        new object[] { "a b", "a  b", false }, new object[] { "x" + U(0xB2), "x2", false }, new object[] { U(0x2460), "1", false },
        new object[] { U(0xFB01), "fi", true }, new object[] { U(0x2168), "IX", false }, new object[] { U(0x212A), "K", false },
        new object[] { U(0x212B), U(0xC5), true }, new object[] { U(0x3C2), U(0x3C3), true }, new object[] { U(0x3C2), U(0x3A3), true },
        new object[] { U(0x17F), "s", false }, new object[] { U(0x1E9E), "ss", false }, new object[] { U(0x153), "oe", true },
        new object[] { U(0x152), "OE", true }, new object[] { "a" + U(0x3000) + "b", "a b", false }, new object[] { U(0xFF76, 0xFF9E), U(0x30AC), true },
        new object[] { U(0x1C5), U(0x1C4), true }, new object[] { U(0x210C), "H", false }, new object[] { U(0x2126), U(0x3A9), false },
        new object[] { U(0xFF10), "0", true }, new object[] { U(0xC5), "A", false }, new object[] { U(0x111), "d", false },
        new object[] { U(0x142), "l", false }, new object[] { U(0xF8), "o", false }, new object[] { U(0xFE), "th", true },
        new object[] { U(0x133), "ij", true },
    };

    [Theory]
    [MemberData(nameof(Measured))]
    public void Names_are_one_where_Access_takes_them_as_one(string first, string second, bool one)
    {
        Assert.Equal(one, ObjectNames.Same(first, second));
        Assert.Equal(one, ObjectNames.Same(second, first));
    }

    // ── Imports ──────────────────────────────────────────────────────────────

    private static DataTable CaseSensitive(params string[] names)
    {
        var table = new DataTable("T") { CaseSensitive = true };
        table.Columns.Add("Id", typeof(int));
        foreach (string name in names) table.Columns.Add(name, typeof(string));
        table.Rows.Add(new object[] { 1 }.Concat(names.Select(n => (object)("value of " + n))).ToArray());
        return table;
    }

    private sealed class TwoCases
    {
        public int Id { get; set; }
        public string? A { get; set; }
        [Column("a")] public string? Lower { get; set; }
    }

    [Fact]
    public void An_import_is_refused_two_columns_Access_takes_as_one_before_anything_is_written()
    {
        Database.Create(_path, FileFormat.Access2010).Dispose();
        Unchanged(db => db.ImportTable(CaseSensitive("A", "a")));
        Unchanged(db => db.ImportTable(CaseSensitive("Stra" + U(0xDF) + "e", "Strasse")));
        Unchanged(db => db.ImportTable(new[] { new TwoCases { Id = 1, A = "upper", Lower = "lower" } }, new ImportOptions { TableName = "T" }));

        // Added to a table that is there: two source columns for one of its columns.
        using (var db = Database.Open(_path))
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text("A") }, "Id");
        Unchanged(db => db.ImportTable(CaseSensitive("A", "a"), new ImportOptions { AppendIfExists = true }));
        Unchanged(db => db.ImportTable(new[] { new TwoCases { Id = 1, A = "upper", Lower = "lower" } }, new ImportOptions { TableName = "T", AppendIfExists = true }));
    }
}
