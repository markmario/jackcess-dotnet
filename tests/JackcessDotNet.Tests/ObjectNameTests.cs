using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Which two names of a database's objects are one, as Access decides it (#63): by the names' keys in the order Access
/// compares them by (<see cref="ObjectNames"/>), the same on every target. A table, column, index or relationship is
/// refused a name that is one with another of its kind, before anything is written, and a name is looked up exactly
/// first. The pairs are those DAO 16 and Jet 4.0 were asked about, as field, table and index names, in databases that
/// sort in General - Legacy and General and in an Access 97 file: 743 of the 18,140 swept (Assets/dao-object-names.tsv,
/// which tools/dao-object-names.ps1 makes). A name already in a file, whatever it holds, stands in the way of nothing but a
/// new name it is one with (Assets/dao-odd-names.accdb, which tools/odd-names-fixture.ps1 makes).
/// </summary>
public sealed class ObjectNameTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"names_{Guid.NewGuid():N}.accdb");
    private readonly string _mdb  = Path.Combine(Path.GetTempPath(), $"names_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_mdb)) File.Delete(_mdb);
    }

    private static string U(params int[] codes) => new(codes.Select(c => (char)c).ToArray());

    private static string X(params int[] codes) => "x" + U(codes);

    // ── What DAO measured ────────────────────────────────────────────────────

    private static string Unhex(string hex)
        => new(hex.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(h => (char)Convert.ToInt32(h, 16)).ToArray());

    // The rule for each kind of name - a field's, a table's, an index's - in a database of each order: General - Legacy's
    // keys in a database that sorts in General - Legacy; General's and General - Legacy's in one that sorts in General; in
    // an Access 97 file a table's by Access 97's order, and a field's and an index's by that and General - Legacy's.
    private static readonly (string Order, ObjectNames[] Rules)[] Orders =
    {
        ("General - Legacy", new[] { ObjectNames.GeneralLegacy,  ObjectNames.GeneralLegacy, ObjectNames.GeneralLegacy }),
        ("General",          new[] { ObjectNames.General,        ObjectNames.General,       ObjectNames.General }),
        ("Access 97",        new[] { ObjectNames.Access97Columns, ObjectNames.Access97,     ObjectNames.Access97Columns }),
    };

    private static readonly string[] Kinds = { "fields", "tables", "indexes" };

    // Each line: the two names in hex, what makes them a pair, and for each order DAO's verdict as a field, a table and
    // an index name: 1 one (the second refused), c two that DAO's own lookups then confuse, 2 two, - not asked.
    [Fact]
    public void Every_pair_DAO_was_asked_about_is_one_name_or_two_as_Access_takes_it()
    {
        int asked = 0, one = 0, apart = 0, withMarks = 0, inGeneral = 0;
        var wrong = new List<string>();
        foreach (string line in File.ReadLines(ProtectedFileTests.Asset("dao-object-names.tsv")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            string a = Unhex(parts[0]), b = Unhex(parts[1]);
            for (int o = 0; o < Orders.Length; o++)
                for (int k = 0; k < Kinds.Length; k++)
                {
                    char verdict = parts[3 + o][k];
                    if (verdict == '-') continue;
                    asked++;
                    bool same = Orders[o].Rules[k].Same(a, b);
                    Assert.Equal(same, Orders[o].Rules[k].Same(b, a));
                    if (verdict is '1' or 'c')
                    {
                        one++;
                        if (!same) wrong.Add($"{parts[0]} / {parts[1]} ({parts[2]}), {Kinds[k]} in {Orders[o].Order}: one to Access, two here");
                    }
                    else
                    {
                        apart++;
                        if (!same) continue;
                        if ((a + b).Any(IsMark)) withMarks++;
                        else if (Orders[o].Order == "General") inGeneral++;
                        else wrong.Add($"{parts[0]} / {parts[1]} ({parts[2]}), {Kinds[k]} in {Orders[o].Order}: two to Access, one here");
                    }
                }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} verdicts this library does not give:\n" + string.Join("\n", wrong.Take(100)));
        // The file is whole: 4,689 verdicts, 3,145 of them one.
        Assert.True(asked > 4_000 && one > 3_000 && apart > 1_500, $"{asked} verdicts, {one} one, {apart} two");
        // Of the names Access keeps apart, those taken as one here are so on purpose (ObjectNames): a name with a mark
        // Access keys by the characters around it (ー, 々, ゝ, the shadda), any of whose readings is one with the other;
        // and a name in a database that sorts in General, compared there by General's keys and by General - Legacy's.
        // Access compares a table's name there by the one in its catalog and the other in its lookups, and a field's by
        // the other or the one as its process has used such a database before. 41 and 164 of the file's 1,544 verdicts of
        // two; 47 and 3,739 of the 90,205 in the whole sweep.
        Assert.True(withMarks < 60 && inGeneral < 250, $"{withMarks} verdicts of two with marks and {inGeneral} in General are one here");
    }

    private static bool IsMark(char c) => TextIndexCodes.GeneralLegacy.IsContextDependent(c) || TextIndexCodes.General.IsContextDependent(c);

    // Pairs Access takes as one, as a field, a table and an index name, in a database that sorts in General - Legacy.
    public static IEnumerable<object[]> OneToAccess() => new[]
    {
        new object[] { "A", "a" }, new object[] { "Name", "NAME" }, new object[] { U(0xC4), U(0xE4) },
        new object[] { U(0x416), U(0x436) }, new object[] { U(0x3A9), U(0x3C9) }, new object[] { X(0x1C5), X(0x1C6) },
        new object[] { X(0xFF21), X(0x41) }, new object[] { X(0x3042), X(0x30A2) }, new object[] { X(0xE1), X(0x61, 0x301) },
        new object[] { X(0x212B), X(0xC5) }, new object[] { X(0xDF), "xss" }, new object[] { X(0xFE), "xth" },
        new object[] { X(0xB2), "x2" }, new object[] { X(0xB9), "x1" }, new object[] { X(0xFE8D), X(0x627) },
        // A trailing space: one table or index name; two fields DAO's SQL takes for one.
        new object[] { "xa", "xa " },
        // An ideographic space at the end against a space: two of each, which DAO's lookups then confuse.
        new object[] { X(0x3000), "x " },
        // Characters General - Legacy has no codes for: a noncharacter, a code point no character has, U+03F3.
        new object[] { "x", X(0xFFFE) }, new object[] { "x", X(0x378) }, new object[] { "x", X(0x3F3) },
    };

    // Pairs Access keeps apart, as a field, a table and an index name, in a database that sorts in General - Legacy.
    public static IEnumerable<object[]> TwoToAccess() => new[]
    {
        new object[] { "e", U(0xE9) }, new object[] { "I", U(0x131) }, new object[] { "i", U(0x130) },
        new object[] { U(0x17F), "s" }, new object[] { U(0x212A), "K" }, new object[] { X(0x2126), X(0x3A9) },
        new object[] { "a b", "a  b" }, new object[] { X(0xB5), X(0x3BC) }, new object[] { X(0xB5), X(0x39C) },
        new object[] { X(0x2460), "x1" }, new object[] { X(0x1E9E), "xSS" }, new object[] { "x", X(0x3000) },
        new object[] { "x", X(0xA0) }, new object[] { X(0xF900), X(0x8C48) }, new object[] { X(0x374), X(0x2B9) },
        new object[] { X(0x2000), X(0x2002) }, new object[] { X(0x2329), X(0x3008) }, new object[] { X(0xFB2A), X(0x5E9, 0x5C1) },
        new object[] { U(0x61, 0x340), U(0xE0) }, new object[] { X(0x1F71), X(0x3AC) },
    };

    // Names with a mark Access keys by the characters around it, as DAO took them.
    public static IEnumerable<object[]> Marks() => new[]
    {
        new object[] { X(0x30AB, 0x30FC), X(0x304B, 0x30FC), true },   // カー, かー
        new object[] { X(0x30AB, 0x30FC), X(0x30AB, 0xFF70), true },   // カー, カｰ
        new object[] { X(0x30C7, 0x30FC, 0x30BF), X(0xFF83, 0xFF9E, 0xFF70, 0xFF80), true },   // データ, ﾃﾞｰﾀ
        new object[] { X(0x61, 0x30FC), "xaa", true },                  // aー, aa
        new object[] { X(0x3055, 0x309D), X(0x30B5, 0x30FD), true },   // さゝ, サヽ
        new object[] { X(0x309E), X(0x309D, 0x3099), true },           // ゞ, and ゝ with the combining voiced sound mark
        new object[] { X(0x309E), X(0x30FE), true },                   // ゞ, ヾ
        new object[] { X(0x3055, 0x309E), X(0x3055, 0x3056), false },  // さゞ, さざ
        new object[] { X(0x30AB, 0x30FC), X(0x30AB, 0x30A2), false },  // カー, カア
        new object[] { X(0x30AB, 0x30FC), X(0x30AB), false },          // カー, カ
        new object[] { X(0x30AB, 0x30FC), X(0x30AB, 0x2D), false },    // カー, カ-
    };

    [Theory]
    [MemberData(nameof(OneToAccess))]
    public void Names_Access_takes_as_one_are_one_on_every_target(string first, string second)
    {
        Assert.True(ObjectNames.GeneralLegacy.Same(first, second));
        Assert.True(ObjectNames.GeneralLegacy.Same(second, first));
    }

    [Theory]
    [MemberData(nameof(TwoToAccess))]
    public void Names_Access_keeps_apart_are_two_on_every_target(string first, string second)
    {
        Assert.False(ObjectNames.GeneralLegacy.Same(first, second));
        Assert.False(ObjectNames.GeneralLegacy.Same(second, first));
    }

    [Theory]
    [MemberData(nameof(Marks))]
    public void A_name_with_a_mark_Access_keys_by_its_neighbours_is_one_where_Access_takes_it_as_one(string first, string second, bool one)
    {
        // Never two where Access takes them as one; where Access keeps them apart these are two here too.
        Assert.Equal(one, ObjectNames.GeneralLegacy.Same(first, second));
        Assert.Equal(one, ObjectNames.GeneralLegacy.Same(second, first));
    }

    [Fact]
    public void Names_are_compared_and_looked_up_without_throwing_whatever_they_hold()
    {
        var random = new Random(63);
        var names = new List<string> { "", " ", "x", X(0xFDD0), X(0xFFFE), X(0xFFFF), X(0xD800), X(0xDC00, 0x79), U(0xD800, 0xD800), U(0xDBFF), U(0x30FC), U(0x30FC, 0x30FC) };
        for (int i = 0; i < 400; i++)
            names.Add(new string(Enumerable.Range(0, random.Next(0, 9)).Select(_ => (char)random.Next(0, 0x10000)).ToArray()));
        foreach (var rule in new[] { ObjectNames.GeneralLegacy, ObjectNames.General, ObjectNames.Access97, ObjectNames.Access97Columns })
        {
            foreach (string first in names)
            {
                Assert.True(rule.Same(first, first));
                foreach (string second in names.Take(60)) Assert.Equal(rule.Same(first, second), rule.Same(second, first));
            }
            Assert.Equal(names.IndexOf("x"), rule.Find(names, "x"));
        }
    }

    // ── Tables, columns and indexes ──────────────────────────────────────────

    private static ColumnDefinition Text(string name) => new(name, DataType.Text) { MaxLength = 20 };

    private void Unchanged(Action<Database> refused) => Unchanged<ArgumentException>(refused);

    private void Unchanged<TException>(Action<Database> refused, string? path = null) where TException : Exception
    {
        path ??= _path;
        byte[] before = File.ReadAllBytes(path);
        using (var db = Database.Open(path))
            Assert.Throws<TException>(() => refused(db));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // A table made with the two names as columns, and a table given the second as a column added to the first: each
    // keeps its own value, written and read by each column's name. In an Access 2007 file, which sorts in General -
    // Legacy, as the pairs were measured.
    private void AreTwoColumns(string first, string second)
    {
        using (var db = Database.Create(_path, FileFormat.Access2007))
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
    [MemberData(nameof(OneToAccess))]
    public void A_table_is_refused_two_columns_Access_takes_as_one_and_the_file_is_unchanged(string first, string second)
    {
        Database.Create(_path, FileFormat.Access2010).Dispose();
        Unchanged(db => db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text(first), Text(second) }, "Id"));
        using var reopen = Database.Open(_path);
        Assert.False(reopen.TryGetTable("T", out _));
    }

    // In an .mdb, whose catalog sorts in General - Legacy, as its tables' names are compared there.
    [Theory]
    [MemberData(nameof(OneToAccess))]
    public void A_column_index_or_table_is_refused_a_name_Access_takes_as_one_with_one_it_has(string first, string second)
    {
        string table = "T" + first;
        using (var db = Database.Create(_mdb))
            db.CreateTable(table, new[] { new ColumnDefinition("Id", DataType.Int32), Text(first), new ColumnDefinition("N", DataType.Int32) },
                           new[] { "Id" }, new[] { new IndexDefinition(first, "N") });
        Unchanged<ArgumentException>(db => db.GetTable(table).AddColumn(Text(second)), _mdb);
        Unchanged<ArgumentException>(db => db.GetTable(table).RenameColumn("N", second), _mdb);
        Unchanged<ArgumentException>(db => db.GetTable(table).AddIndex(new IndexDefinition(second, "Id")), _mdb);
        // A name an object of the database has is refused as before, with InvalidOperationException.
        Unchanged<InvalidOperationException>(db => db.CreateTable("T" + second, new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"), _mdb);
    }

    // In a database that sorts in General, as an Access 2010 file this library creates does, a table's, a column's and an
    // index's name is compared by General's keys - which take U+0180 and U+0243 as one, where General - Legacy's keep
    // them apart - and by General - Legacy's, which take U+0224 and U+0225 as one, where General's keep them apart. DAO
    // refused U+0243 as a table's name beside U+0180, and made it as a field's, which ACE then took for the other in a
    // process that had used such a database through DAO before; it made U+0225 as a table beside U+0224, and then took
    // either for the other. In a database that sorts in General - Legacy, the first pair are two.
    [Fact]
    public void A_name_in_a_database_that_sorts_in_general_is_compared_by_general_s_keys_and_general_legacy_s()
    {
        using (var db = Database.Create(_path, FileFormat.Access2010))
        {
            db.CreateTable(X(0x180), new[] { new ColumnDefinition("Id", DataType.Int32), Text(X(0x180)), Text(X(0x224)), new ColumnDefinition("N", DataType.Int32) },
                           new[] { "Id" }, new[] { new IndexDefinition(X(0x180), "N"), new IndexDefinition(X(0x224), "Id") });
            db.CreateTable(X(0x224), new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id");
        }
        foreach (var given in new[] { X(0x243), X(0x225) })
        {
            Unchanged<InvalidOperationException>(db => db.CreateTable(given, new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"));
            Unchanged(db => db.GetTable(X(0x180)).AddColumn(Text(given)));
            Unchanged(db => db.GetTable(X(0x180)).AddIndex(new IndexDefinition(given, "Id")));
        }
        using (var reopen = Database.Open(_path))
            Assert.Equal(X(0x180), reopen.GetTable(X(0x243)).Name);
        // The table CreateTable gives back compares new names as the database does.
        using (var db = Database.Open(_path))
        {
            var made = db.CreateTable("Made", new[] { new ColumnDefinition("Id", DataType.Int32), Text(X(0x180)) },
                                      new[] { "Id" }, new[] { new IndexDefinition(X(0x180), "Id") });
            Assert.Throws<ArgumentException>(() => made.AddIndex(new IndexDefinition(X(0x243), "Id")));
            Assert.Throws<ArgumentException>(() => made.AddColumn(Text(X(0x243))));
        }

        using (var db = Database.Create(_mdb))
            db.CreateTable(X(0x180), new[] { new ColumnDefinition("Id", DataType.Int32), Text(X(0x180)) }, "Id")
              .AddColumn(Text(X(0x243)));
        using var legacy = Database.Open(_mdb);
        Assert.Equal(new[] { "Id", X(0x180), X(0x243) }, legacy.GetTable(X(0x180)).Columns.Select(c => c.Name));
    }

    [Theory]
    [MemberData(nameof(TwoToAccess))]
    public void Columns_Access_keeps_apart_are_two_and_keep_their_values_on_every_target(string first, string second)
        => AreTwoColumns(first, second);

    [Fact]
    public void A_row_finds_a_column_by_its_name_or_else_by_one_Access_takes_as_one_with_it()
    {
        var row = new Row { ["Name"] = 1, [X(0xB5)] = 2, [X(0x3BC)] = 3 };
        Assert.Equal(3, row.Count);
        Assert.Equal(1, row["NAME"]);
        Assert.Equal(1, row[U(0xFF2E, 0x61, 0x6D, 0x65)]);                 // the N in full width
        Assert.Equal(2, row[X(0xB5)]);
        Assert.Equal(3, row[X(0x3BC)]);
        Assert.Equal(3, row[X(0x39C)]);                                    // the capital mu is the mu's
        Assert.Throws<ArgumentException>(() => row.Add("name", 4));
        row["NAME"] = 5;
        Assert.Equal(new[] { "Name", X(0xB5), X(0x3BC) }, row.Keys);       // the entry keeps its own name
        Assert.Equal(5, row["Name"]);
        Assert.True(row.Remove("nAmE"));
        Assert.False(row.ContainsKey("Name"));
        Assert.False(row.ContainsKey("Strasse"));
        row["Straße"] = 6;
        Assert.Equal(6, row["STRASSE"]);
    }

    [Fact]
    public void A_table_is_found_by_its_name_exactly_and_otherwise_by_one_Access_takes_as_one_with_it()
    {
        using (var db = Database.Create(_path, FileFormat.Access2010))
        {
            db.CreateTable("Straße", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id");
            db.CreateTable(X(0xB5), new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id");
            db.CreateTable(X(0x3BC), new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id");
        }
        using var reopen = Database.Open(_path);
        Assert.Equal("Straße", reopen.GetTable("STRASSE").Name);
        Assert.Equal(X(0xB5), reopen.GetTable(X(0xB5)).Name);
        Assert.Equal(X(0x3BC), reopen.GetTable(X(0x3BC)).Name);
        Assert.Equal(X(0x3BC), reopen.GetTable(X(0x39C)).Name);
        Assert.False(reopen.TryGetTable("Strase", out _));
    }

    // A table with the micro sign and the mu as two columns, which Access keeps apart: each is read and written by its own
    // name on every target, and an insert that gives one of them a value leaves the other with none - though .NET's
    // comparison without regard to case takes the two names as one.
    [Fact]
    public void The_micro_sign_and_the_mu_are_two_columns_and_an_insert_writes_each_on_every_target()
    {
        AreTwoColumns(X(0xB5), X(0x3BC));
        using var db = Database.Open(_path);
        var table = db.GetTable("T");
        table.Insert(new Row { ["Id"] = 2, [X(0x3BC)] = "mu only" });
        var row = table.FindRowByPrimaryKey(2)!;
        Assert.Null(row[X(0xB5)]);
        Assert.Equal("mu only", row[X(0x3BC)]);
    }

    // A relationship's child is the table of its name, not one whose name .NET takes as one with it: an insert into a
    // table no relationship has is not checked against another table's.
    [Fact]
    public void A_relationship_is_kept_for_its_own_tables_and_not_for_one_whose_name_is_one_with_its_to_dotnet()
    {
        using (var db = Database.Create(_path, FileFormat.Access2010))
        {
            db.CreateTable("P", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id").Insert(new Row { ["Id"] = 1 });
            db.CreateTable("C" + U(0xB5), new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32) }, "Id");
            db.CreateTable("C" + U(0x3BC), new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32) }, "Id");
            db.CreateRelationship(new RelationshipDefinition("P", "Id", "C" + U(0x3BC), "K") { Name = "PMu", HasReferentialIntegrity = true });
        }
        using var reopen = Database.Open(_path);
        reopen.GetTable("C" + U(0xB5)).Insert(new Row { ["Id"] = 1, ["K"] = 9 });   // no relationship: taken
        var refused = Assert.Throws<ConstraintViolationException>(() => reopen.GetTable("C" + U(0x3BC)).Insert(new Row { ["Id"] = 1, ["K"] = 9 }));
        Assert.Contains("'PMu'", refused.Message);
        Assert.Contains("'C" + U(0x3BC) + "'", refused.Message);
    }

    [Fact]
    public void A_new_name_with_half_a_surrogate_pair_alone_is_refused_before_anything_is_written()
    {
        Database.Create(_path, FileFormat.Access2010).Dispose();
        Unchanged<NotSupportedException>(db => db.CreateTable(X(0xD800), new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"));
        Unchanged<NotSupportedException>(db => db.CreateTable("T", new[] { new ColumnDefinition(X(0xDC00), DataType.Int32) }));
        using (var db = Database.Open(_path))
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("N", DataType.Int32) }, "Id");
        Unchanged<NotSupportedException>(db => db.GetTable("T").AddColumn(Text(X(0xD800))));
        Unchanged<NotSupportedException>(db => db.GetTable("T").RenameColumn("N", X(0xD800)));
        Unchanged<NotSupportedException>(db => db.GetTable("T").AddIndex(new IndexDefinition(X(0xDBFF), "N")));
        Unchanged<NotSupportedException>(db => db.RenameTable("T", "T" + U(0xDC00)));
        // A pair whole is a character like any other.
        using (var db = Database.Open(_path))
            db.GetTable("T").AddColumn(Text(X(0xD801, 0xDC00)));
    }

    // A file Access made, whose tables and columns have noncharacters and lone surrogates in their names: each is read as
    // the file holds it and found by that name; a table made in it is compared with every table's name, a column added
    // with its table's columns', and an import with both.
    [Fact]
    public void A_file_with_noncharacters_and_lone_surrogates_in_its_names_takes_a_table_a_column_and_an_import()
    {
        File.Copy(ProtectedFileTests.Asset("dao-odd-names.accdb"), _path);
        var odd = new[]
        {
            ("F_FDD0", X(0xFDD0)), ("F_FFFE", X(0xFFFE)), ("F_FFFF", X(0xFFFF)),
            // A lone surrogate is read as the file holds it.
            ("F_D800", X(0xD800)), ("F_DC00", X(0xDC00, 0x79)),
        };
        using (var db = Database.Open(_path))
        {
            db.CreateTable("New", new[] { new ColumnDefinition("Id", DataType.Int32), Text("Name") }, "Id")
              .Insert(new Row { ["Id"] = 1, ["Name"] = "made" });
            foreach (var (table, _) in odd) db.GetTable(table).AddColumn(Text("Added"));
            db.GetTable("F_D800").Insert(new Row { ["Id"] = 1, [X(0xD800)] = "lone" });

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
            // The column with the lone surrogate is found by that name, by the table and by the row written by it.
            var surrogate = reopen.GetTable("F_D800");
            Assert.Equal(X(0xD800), surrogate.Definition.FindColumn(X(0xD800))!.Name);
            Assert.Equal("lone", surrogate.Rows.Single()[X(0xD800)]);
        }

        // A new name one with one of them is refused, as any other: a noncharacter has no code, so "x" and it are one.
        Unchanged(db => db.GetTable("F_FFFE").AddColumn(Text("X" + U(0xFFFE))));
        Unchanged(db => db.GetTable("F_FFFF").AddColumn(Text("x")));
        Unchanged<InvalidOperationException>(db => db.CreateTable(X(0xFDD0), new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"));
    }

    // A database that sorts in Swedish, an order this library makes no keys for, in which Access compares field and
    // index names by Swedish keys: DAO refused an index "x" with U+028E beside "x" with U+00DC, which General - Legacy's
    // keys keep apart. It was measured to compare names of printable ASCII there as General - Legacy's keys do, so a
    // column of such a name is added beside columns of such names, and compared with them so; another name is refused.
    [Fact]
    public void A_database_that_sorts_in_swedish_takes_a_name_of_printable_ascii_beside_names_of_printable_ascii()
    {
        File.Copy(ProtectedFileTests.Asset("dao-swedish.mdb"), _path);
        File.SetAttributes(_path, FileAttributes.Normal);
        using (var db = Database.Open(_path))
            db.GetTable("People").AddColumn(Text("Street"));
        Unchanged(db => db.GetTable("People").AddColumn(Text("STREET")));
        Unchanged<NotSupportedException>(db => db.GetTable("People").AddColumn(Text(X(0x28E))));
        Unchanged<NotSupportedException>(db => db.GetTable("People").AddIndex(new IndexDefinition(X(0xDC), "Id")));
        Unchanged<NotSupportedException>(db => db.GetTable("People").RenameColumn("Street", "Stra" + U(0xDF) + "e"));
        using (var db = Database.Open(_path))
            db.GetTable("People").AddColumn(Text("Town"));
        using var reopen = Database.Open(_path);
        Assert.Equal(new[] { "Id", "Name", "Street", "Town" }, reopen.GetTable("people").Columns.Select(c => c.Name));
        Assert.Equal("Street", reopen.GetTable("People").Definition.FindColumn("street")!.Name);
    }

    // A database whose text sorts in an order this library makes no keys for and Access was not measured to compare
    // names of printable ASCII in as General - Legacy does - Turkish here, whose catalog this test gives that order - is
    // given no new column, index or column's name: in Turkish, an index named "xİ" is one with "xi", which General -
    // Legacy keeps apart.
    [Fact]
    public void A_database_that_sorts_in_an_order_not_measured_is_given_no_new_name_and_its_tables_are_found()
    {
        using (var db = Database.Create(_mdb))
        {
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("N", DataType.Int32) }, "Id");
            // The catalog's Text and Memo columns sort in 1055, Access's Turkish order.
            byte[] page = db.File.ReadPage(2);
            int columns = Util.ByteUtil.GetShort(page, 45), definitions = 63 + Util.ByteUtil.GetInt(page, 51) * 12;
            for (int i = 0; i < columns; i++)
                if (page[definitions + i * 25] is 10 or 12) page[definitions + i * 25 + 11] = 0x1F;
            db.File.WritePage(2, page);
        }
        Unchanged<NotSupportedException>(db => db.GetTable("T").AddColumn(Text("Added")), _mdb);
        Unchanged<NotSupportedException>(db => db.GetTable("T").AddIndex(new IndexDefinition("ByN", "N")), _mdb);
        Unchanged<NotSupportedException>(db => db.GetTable("T").RenameColumn("N", "M"), _mdb);
        Unchanged<NotSupportedException>(db => db.CreateTable("U", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"), _mdb);
        using var reopen = Database.Open(_mdb);
        Assert.Equal("T", reopen.GetTable("t").Name);
        Assert.Equal("N", reopen.GetTable("T").Definition.FindColumn("n")!.Name);
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
        Unchanged(db => db.ImportTable(CaseSensitive("Straße", "STRASSE")));
        Unchanged(db => db.ImportTable(new[] { new TwoCases { Id = 1, A = "upper", Lower = "lower" } }, new ImportOptions { TableName = "T" }));

        // Added to a table that is there: two source columns for one of its columns.
        using (var db = Database.Open(_path))
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), Text("A") }, "Id");
        Unchanged(db => db.ImportTable(CaseSensitive("A", "a"), new ImportOptions { AppendIfExists = true }));
        Unchanged(db => db.ImportTable(new[] { new TwoCases { Id = 1, A = "upper", Lower = "lower" } }, new ImportOptions { TableName = "T", AppendIfExists = true }));
    }

    [Fact]
    public void An_import_of_columns_Access_keeps_apart_makes_two_and_one_added_finds_its_column_by_a_name_one_with_it()
    {
        Database.Create(_path, FileFormat.Access2010).Dispose();
        using (var db = Database.Open(_path))
        {
            db.ImportTable(CaseSensitive(X(0xB5), X(0x3BC)));
            db.ImportTable(CaseSensitive("STRASSE"), new ImportOptions { TableName = "S" });
            var more = new DataTable("S");
            more.Columns.Add("ID", typeof(int));
            more.Columns.Add("Straße", typeof(string));
            more.Rows.Add(2, "added");
            db.ImportTable(more, new ImportOptions { AppendIfExists = true });
        }
        using var reopen = Database.Open(_path);
        var row = reopen.GetTable("T").Rows.Single();
        Assert.Equal(("value of " + X(0xB5), "value of " + X(0x3BC)), ((string)row[X(0xB5)]!, (string)row[X(0x3BC)]!));
        Assert.Equal(new[] { "value of STRASSE", "added" }, reopen.GetTable("S").Rows.Select(r => (string)r["STRASSE"]!));
    }
}
