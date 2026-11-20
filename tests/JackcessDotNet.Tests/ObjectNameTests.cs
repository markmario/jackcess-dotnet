using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table, column, index or relationship is refused a name another of its kind has, the two compared as .NET
/// compares them without regard to case - as this library looks names up - before anything is written. That is not yet
/// Access's own rule; the pairs here, measured with DAO 16 in an .mdb and an .accdb, say where it is not. A name already
/// in a file, whatever it holds, stands in the way of nothing but a new name equal to it (Assets/dao-odd-names.accdb,
/// which tools/odd-names-fixture.ps1 makes).
/// </summary>
public sealed class ObjectNameTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"names_{Guid.NewGuid():N}.accdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string U(params int[] codes) => new(codes.Select(c => (char)c).ToArray());

    private static string X(params int[] codes) => "x" + U(codes);

    // Names that differ only in case: one name to Access, and here.
    public static IEnumerable<object[]> CaseOnly() => new[]
    {
        new object[] { "A", "a" }, new object[] { "Name", "NAME" }, new object[] { U(0xC4), U(0xE4) },
        new object[] { U(0x416), U(0x436) }, new object[] { U(0x3A9), U(0x3C9) },
    };

    // Names Access tells apart, and so does every target.
    public static IEnumerable<object[]> TwoToAccess() => new[]
    {
        new object[] { "e", U(0xE9) }, new object[] { "I", U(0x131) }, new object[] { "a b", "a  b" },
        new object[] { U(0x17F), "s" }, new object[] { U(0x212A), "K" },
    };

    // Names Access keeps apart: an accent written as one character or as two, a character and the one it is
    // canonically equivalent to, the micro sign and the Greek mu. Two names here too, but for the micro sign and the mu
    // on .NET, whose comparison without case takes them as one - so a lookup of a column or table by its name would.
    public static IEnumerable<object[]> ApartToAccess() => new[]
    {
        new object[] { U(0x61, 0x340), U(0xE0), false }, new object[] { X(0x374), X(0x2B9), false },
        new object[] { X(0x37E), X(0x3B), false }, new object[] { X(0x387), X(0xB7), false },
        new object[] { X(0x1F71), X(0x3AC), false }, new object[] { X(0x2000), X(0x2002), false },
        new object[] { X(0x2001), X(0x2003), false }, new object[] { X(0x2329), X(0x3008), false },
        new object[] { X(0x232A), X(0x3009), false }, new object[] { X(0xF900), X(0x8C48), false },
        new object[] { X(0xF914), X(0x6A02), false }, new object[] { X(0xF95C), X(0x6A02), false },
        new object[] { X(0xF9BF), X(0x6A02), false }, new object[] { X(0xFA10), X(0x585A), false },
        new object[] { X(0xFA30), X(0x4FAE), false }, new object[] { X(0xFB2A), X(0x5E9, 0x5C1), false },
        new object[] { X(0xB5), X(0x3BC), MuIsOne }, new object[] { X(0xB5), X(0x39C), MuIsOne },
    };

#if NETFRAMEWORK
    private const bool MuIsOne = false;
#else
    private const bool MuIsOne = true;
#endif

    // Names Access takes as one that are two names here: the width of a character, hiragana and katakana, an accent
    // written as one character or as two, and letters written as one.
    public static IEnumerable<object[]> OneToAccessOnly() => new[]
    {
        new object[] { U(0xFF21), "A" }, new object[] { U(0xFF10), "0" }, new object[] { U(0x3042), U(0x30A2) },
        new object[] { U(0xE1), U(0x61, 0x301) }, new object[] { U(0xDF), "ss" }, new object[] { U(0xE6), "ae" },
        new object[] { U(0xFB01), "fi" },
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

    // A table made with the two names as columns, and a table given the second as a column added to the first: each
    // keeps its own value.
    private void AreTwoColumns(string first, string second)
    {
        using (var db = Database.Create(_path, FileFormat.Access2010))
        {
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text(first), Text(second) }, "Id")
              .Insert(new Row { ["Id"] = 1, [first] = "one", [second] = "two" });
            var added = db.CreateTable("A", new[] { new ColumnDefinition("Id", DataType.Int32), Text(first) }, "Id");
            added.AddColumn(Text(second));
            added.Insert(new Row { ["Id"] = 1, [first] = "one", [second] = "two" });
        }
        using var reopen = Database.Open(_path);
        foreach (string table in new[] { "T", "A" })
        {
            var row = reopen.GetTable(table).FindRowByPrimaryKey(1)!;
            Assert.Equal("one", row[first]);
            Assert.Equal("two", row[second]);
        }
    }

    [Theory]
    [MemberData(nameof(CaseOnly))]
    public void A_table_is_refused_two_columns_whose_names_differ_only_in_case_and_the_file_is_unchanged(string first, string second)
    {
        Database.Create(_path, FileFormat.Access2010).Dispose();
        Unchanged(db => db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text(first), Text(second) }, "Id"));
        using var reopen = Database.Open(_path);
        Assert.False(reopen.TryGetTable("T", out _));
    }

    [Theory]
    [MemberData(nameof(CaseOnly))]
    public void A_column_index_or_table_is_refused_a_name_that_differs_only_in_case_from_one_it_has(string first, string second)
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

    [Theory]
    [MemberData(nameof(TwoToAccess))]
    public void Columns_Access_tells_apart_are_two_and_keep_their_values(string first, string second)
    {
        Assert.False(ObjectNames.Same(first, second));
        AreTwoColumns(first, second);
    }

    [Theory]
    [MemberData(nameof(ApartToAccess))]
    public void Names_Access_keeps_apart_are_two_but_where_the_comparison_without_case_takes_them_as_one(string first, string second, bool one)
    {
        Assert.Equal(one, ObjectNames.Same(first, second));
        Assert.Equal(one, ObjectNames.Same(second, first));
        if (!one)
        {
            AreTwoColumns(first, second);
            return;
        }
        Database.Create(_path, FileFormat.Access2010).Dispose();
        Unchanged(db => db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text(first), Text(second) }, "Id"));
    }

    [Theory]
    [MemberData(nameof(OneToAccessOnly))]
    public void Names_Access_takes_as_one_that_differ_in_more_than_case_are_two(string first, string second)
    {
        Assert.False(ObjectNames.Same(first, second));
        AreTwoColumns(first, second);
    }

    [Fact]
    public void Names_with_noncharacters_or_lone_surrogates_are_compared_without_throwing()
    {
        string[] odd = { X(0xFDD0), X(0xFFFE), X(0xFFFF), X(0xD800), X(0xDC00, 0x79), U(0xD800, 0xD800), U(0xDBFF), "X" + U(0xFFFE) };
        string[] oneInCase = { X(0xFFFE), "X" + U(0xFFFE) };
        foreach (string first in odd)
            foreach (string second in odd)
                Assert.Equal(first == second || (oneInCase.Contains(first) && oneInCase.Contains(second)), ObjectNames.Same(first, second));
    }

    // A file Access made, whose tables and columns have noncharacters and lone surrogates in their names: a table made
    // in it is compared with every table's name, a column added with its table's columns', and an import with both.
    [Fact]
    public void A_file_with_noncharacters_and_lone_surrogates_in_its_names_takes_a_table_a_column_and_an_import()
    {
        File.Copy(ProtectedFileTests.Asset("dao-odd-names.accdb"), _path);
        var odd = new[]
        {
            ("F_FDD0", X(0xFDD0)), ("F_FFFE", X(0xFFFE)), ("F_FFFF", X(0xFFFF)),
            // A lone surrogate is read as the file holds it, and found by that name.
            ("F_D800", X(0xD800)), ("F_DC00", X(0xDC00, 0x79)),
        };
        using (var db = Database.Open(_path))
        {
            db.CreateTable("New", new[] { new ColumnDefinition("Id", DataType.Int32), Text("Name") }, "Id")
              .Insert(new Row { ["Id"] = 1, ["Name"] = "made" });
            foreach (var (table, _) in odd) db.GetTable(table).AddColumn(Text("Added"));

            var source = new DataTable("Imported");
            source.Columns.Add("Id", typeof(int));
            source.Columns.Add("Name", typeof(string));
            source.Rows.Add(1, "imported");
            db.ImportTable(source);

            var into = new DataTable("F_FFFE");
            into.Columns.Add("Id", typeof(int));
            into.Columns.Add(X(0xFFFE), typeof(string));
            into.Columns.Add("Added", typeof(string));
            into.Rows.Add(7, "odd", "added");
            db.ImportTable(into, new ImportOptions { AppendIfExists = true });
        }

        using (var reopen = Database.Open(_path))
        {
            Assert.Equal("made", reopen.GetTable("New").FindRowByPrimaryKey(1)!["Name"]);
            Assert.Equal("imported", reopen.GetTable("Imported").Rows.Single()["Name"]);
            foreach (var (table, column) in odd)
                Assert.Equal(new[] { "Id", column, "Added" }, reopen.GetTable(table).Columns.Select(c => c.Name));
            var row = reopen.GetTable("F_FFFE").Rows.Single();
            Assert.Equal((7, "odd", "added"), ((int)row["Id"]!, (string)row[X(0xFFFE)]!, (string)row["Added"]!));
        }

        // A new name equal to one of them is refused, as any other.
        Unchanged(db => db.GetTable("F_FFFE").AddColumn(Text("X" + U(0xFFFE))));
        Unchanged(db => db.GetTable("F_FFFF").AddColumn(Text(X(0xFFFF))));
        Unchanged<InvalidOperationException>(db => db.CreateTable(X(0xFDD0), new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"));
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
    public void An_import_is_refused_two_columns_whose_names_differ_only_in_case_before_anything_is_written()
    {
        Database.Create(_path, FileFormat.Access2010).Dispose();
        Unchanged(db => db.ImportTable(CaseSensitive("A", "a")));
        Unchanged(db => db.ImportTable(new[] { new TwoCases { Id = 1, A = "upper", Lower = "lower" } }, new ImportOptions { TableName = "T" }));

        // Added to a table that is there: two source columns for one of its columns.
        using (var db = Database.Open(_path))
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text("A") }, "Id");
        Unchanged(db => db.ImportTable(CaseSensitive("A", "a"), new ImportOptions { AppendIfExists = true }));
        Unchanged(db => db.ImportTable(new[] { new TwoCases { Id = 1, A = "upper", Lower = "lower" } }, new ImportOptions { TableName = "T", AppendIfExists = true }));
    }
}
