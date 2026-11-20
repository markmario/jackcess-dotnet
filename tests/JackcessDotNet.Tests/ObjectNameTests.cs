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

    // A caller's row holds each value under its exact name, and reads one by its exact name or else by a name Access
    // takes as one with it.
    [Fact]
    public void A_caller_s_row_holds_each_name_exactly_and_reads_by_one_Access_takes_as_one_with_it()
    {
        var row = new Row { ["Name"] = 1, [X(0xB5)] = 2, [X(0x3BC)] = 3 };
        Assert.Equal(3, row.Count);
        Assert.Equal(1, row["NAME"]);
        Assert.Equal(1, row[U(0xFF2E, 0x61, 0x6D, 0x65)]);                 // the N in full width
        Assert.Equal(2, row[X(0xB5)]);
        Assert.Equal(3, row[X(0x3BC)]);
        Assert.Equal(3, row[X(0x39C)]);                                    // the capital mu is the mu's
        Assert.False(row.ContainsKey("Strasse"));
        // A value is set, added and taken out by its exact name alone: another spelling is another entry.
        row["NAME"] = 5;
        Assert.Equal(new[] { "Name", X(0xB5), X(0x3BC), "NAME" }, row.Keys);
        Assert.Equal((1, 5), ((int)row["Name"]!, (int)row["NAME"]!));
        Assert.Throws<ArgumentException>(() => row.Add("Name", 4));
        row.Add("name", 4);
        Assert.False(row.Remove("nAmE"));
        Assert.True(row.Remove("name"));
        Assert.Equal(4, row.Count);
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
    // keys keep apart. A new name is compared there with the others by General - Legacy's keys and without regard to
    // case, and refused only when it is one with another so.
    [Fact]
    public void A_database_that_sorts_in_swedish_compares_a_new_name_by_general_legacy_s_keys_and_without_regard_to_case()
    {
        File.Copy(ProtectedFileTests.Asset("dao-swedish.mdb"), _path);
        File.SetAttributes(_path, FileAttributes.Normal);
        using (var db = Database.Open(_path))
        {
            var people = db.GetTable("People");
            people.AddColumn(Text("Street"));
            people.AddColumn(Text(X(0x28E)));
            people.AddIndex(new IndexDefinition(X(0xDC), "Id"));
            people.RenameColumn("Street", "Stra" + U(0xDF) + "e");
        }
        Unchanged(db => db.GetTable("People").AddColumn(Text("NAME")));                 // another case
        Unchanged(db => db.GetTable("People").AddColumn(Text("STRASSE")));              // one with Straße by its keys
        Unchanged(db => db.GetTable("People").AddIndex(new IndexDefinition(X(0xFC), "Name")));
        using var reopen = Database.Open(_path);
        Assert.Equal(new[] { "Id", "Name", "Stra" + U(0xDF) + "e", X(0x28E) }, reopen.GetTable("people").Columns.Select(c => c.Name));
        Assert.Equal("Name", reopen.GetTable("People").Definition.FindColumn("name")!.Name);
    }

    // A database whose text sorts in an order this library makes no keys for - Turkish, Polish or Spanish, which this
    // test gives its catalog: a new column's or index's name is compared with the others by General - Legacy's keys and
    // without regard to case, and refused - ArgumentException, the file unchanged - only when it is one with another so.
    // That is not the order's own rule: in Turkish, an index named "xİ" is one with "xi", which both keep apart. A table
    // is not created there, its catalog's keys not made; the tables there are found by name.
    [Theory]
    [InlineData(0x1F)]   // Turkish, 1055
    [InlineData(0x15)]   // Polish, 1045
    [InlineData(0x0A)]   // Spanish, 1034
    public void A_database_that_sorts_in_an_order_without_keys_compares_a_new_name_by_general_legacy_s_keys_and_without_regard_to_case(int language)
    {
        using (var db = Database.Create(_mdb))
        {
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true }, new ColumnDefinition("N", DataType.Int32) }, "Id");
            // The catalog's Text and Memo columns sort in Access's order of that language.
            byte[] page = db.File.ReadPage(2);
            int columns = Util.ByteUtil.GetShort(page, 45), definitions = 63 + Util.ByteUtil.GetInt(page, 51) * 12;
            for (int i = 0; i < columns; i++)
                if (page[definitions + i * 25] is 10 or 12) page[definitions + i * 25 + 11] = (byte)language;
            db.File.WritePage(2, page);
        }
        using (var db = Database.Open(_mdb))
        {
            var table = db.GetTable("T");
            table.AddColumn(Text("Added"));
            table.AddIndex(new IndexDefinition("ByN", "N"));
            table.RenameColumn("Id", "Key");                                    // a Long AutoNumber: no properties to follow it
            table.RenameIndex("ByN", "ByM");
            table.AddColumn(Text("xe"));
            table.AddColumn(Text("x" + U(0xE9)));                                        // two by both
        }
        Unchanged<ArgumentException>(db => db.GetTable("T").AddColumn(Text("ADDED")), _mdb);
        Unchanged<ArgumentException>(db => db.GetTable("T").AddColumn(Text(U(0xFF21) + "dded")), _mdb);   // a full-width A
        Unchanged<ArgumentException>(db => db.GetTable("T").AddIndex(new IndexDefinition("BYM", "Id")), _mdb);
        Unchanged<ArgumentException>(db => db.GetTable("T").RenameColumn("Added", "key"), _mdb);
        Unchanged<ArgumentException>(db => db.GetTable("T").RenameIndex("ByM", "primarykey"), _mdb);
        Unchanged<NotSupportedException>(db => db.CreateTable("U", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"), _mdb);
        using var reopen = Database.Open(_mdb);
        Assert.Equal("T", reopen.GetTable("t").Name);
        Assert.Equal(new[] { "Key", "N", "Added", "xe", "x" + U(0xE9) }, reopen.GetTable("T").Columns.Select(c => c.Name));
        Assert.Equal("Key", reopen.GetTable("T").Definition.FindColumn("key")!.Name);
        Assert.Equal(new[] { "ByM", "PrimaryKey" }, reopen.GetTable("T").Indexes.Select(ix => ix.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ── Rows a caller makes ──────────────────────────────────────────────────

    private static ColumnDefinition Long(string name) => new(name, DataType.Int32);

    // Gives a column of a table this library made, by its definition's bytes, a name this library would refuse beside
    // another - as Access lets a file have: the two names are of one length.
    private static void RenameInDefinition(string path, string table, string from, string to)
    {
        Assert.Equal(from.Length, to.Length);
        using var db = Database.Open(path);
        int page = db.GetTable(table).Definition.TdefPageNumber;
        byte[] bytes = db.File.ReadPage(page);
        byte[] pattern = new[] { (byte)(from.Length * 2), (byte)0 }.Concat(Util.ByteUtil.EncodeUtf16(from)).ToArray();
        int at = bytes.AsSpan().IndexOf(pattern);
        Assert.True(at >= 0 && bytes.AsSpan(at + 1).IndexOf(pattern) < 0, $"the name '{from}' is not in the definition once");
        Util.ByteUtil.EncodeUtf16(to).CopyTo(bytes, at + 2);
        db.File.WritePage(page, bytes);
    }

    // A table of a file that sorts in General - Legacy with fields x, x and two spaces, x and the micro sign, x and mu,
    // as DAO makes them (the library's referee test has DAO make it): a caller's row holds a value for each, by its
    // exact name, and each reaches its own column. Before #63 was fixed, the row took "x  " for "x" and lost a value.
    [Fact]
    public void A_caller_s_row_holds_a_value_for_each_of_two_columns_whose_names_are_one_and_each_reaches_its_column()
    {
        using (var db = Database.Create(_mdb))
            db.CreateTable("Dup", new[] { Long("Id"), Long("x"), Long("y  "), Long(X(0xB5)), Long(X(0x3BC)) }, "Id");
        RenameInDefinition(_mdb, "Dup", "y  ", "x  ");

        var row = new Row { ["Id"] = 1, ["x"] = 1, ["x  "] = 2, [X(0xB5)] = 3, [X(0x3BC)] = 4 };
        Assert.Equal(5, row.Count);
        using (var db = Database.Open(_mdb))
            db.GetTable("Dup").Insert(row);
        using var reopen = Database.Open(_mdb);
        var read = reopen.GetTable("Dup").Rows.Single();
        Assert.Equal(new object?[] { 1, 1, 2, 3, 4 }, new[] { "Id", "x", "x  ", X(0xB5), X(0x3BC) }.Select(name => read[name]));
    }

    // In a database that sorts in General, fields x with U+2460 (a circled one) and x1, which DAO makes as two and this
    // library takes as one: a caller's row reaches each.
    [Fact]
    public void A_caller_s_row_reaches_each_of_two_columns_a_database_that_sorts_in_general_takes_as_one()
    {
        using (var db = Database.Create(_path, FileFormat.Access2010))
            db.CreateTable("T", new[] { Long("Id"), Long(X(0x2460)), Long("y1") }, "Id");
        RenameInDefinition(_path, "T", "y1", "x1");
        using (var db = Database.Open(_path))
            db.GetTable("T").Insert(new Row { ["Id"] = 1, [X(0x2460)] = 10, ["x1"] = 20 });
        using var reopen = Database.Open(_path);
        var read = reopen.GetTable("T").Rows.Single();
        Assert.Equal((10, 20), ((int)read[X(0x2460)]!, (int)read["x1"]!));
    }

    // Two of a row's names that go to one column are refused, naming both and the column, before anything is written:
    // neither is dropped. A name in another case alone reaches its column.
    [Fact]
    public void Two_names_of_a_row_for_one_column_are_refused_and_a_name_in_another_case_reaches_its_column()
    {
        using (var db = Database.Create(_mdb))
            db.CreateTable("T", new[] { Long("Id"), Long("x") }, "Id");
        byte[] before = File.ReadAllBytes(_mdb);
        using (var db = Database.Open(_mdb))
        {
            var refused = Assert.Throws<ArgumentException>(() => db.GetTable("T").Insert(new Row { ["Id"] = 1, ["x"] = 1, ["X"] = 2 }));
            Assert.Contains("'x'", refused.Message);
            Assert.Contains("'X'", refused.Message);
        }
        Assert.Equal(before, File.ReadAllBytes(_mdb));

        using (var db = Database.Open(_mdb))
            db.GetTable("T").Insert(new Row { ["ID"] = 2, ["X"] = 5 });
        using var reopen = Database.Open(_mdb);
        var read = reopen.GetTable("T").Rows.Single();
        Assert.Equal((2, 5), ((int)read["Id"]!, (int)read["x"]!));
    }

    // An AutoNumber column x beside a column "x  ", whose names are one to Access: a row that names only "x  " keeps its
    // value there, and the table numbers x - the number goes under the AutoNumber column's own name, not into the
    // entry of the other column.
    [Fact]
    public void An_autonumber_is_put_under_its_own_column_beside_a_column_whose_name_is_one_with_it()
    {
        using (var db = Database.Create(_mdb))
            db.CreateTable("T", new[] { new ColumnDefinition("x", DataType.Int32) { IsAutoNumber = true }, Long("y  ") }, "x");
        RenameInDefinition(_mdb, "T", "y  ", "x  ");
        var row = new Row { ["x  "] = 7 };
        using (var db = Database.Open(_mdb))
            db.GetTable("T").Insert(row);
        Assert.Equal((1, 7), ((int)row["x"]!, (int)row["x  "]!));
        using var reopen = Database.Open(_mdb);
        var read = reopen.GetTable("T").Rows.Single();
        Assert.Equal((1, 7), ((int)read["x"]!, (int)read["x  "]!));
    }

    // The same in a batch: a refused InsertRange gives its rows back as they came - the number taken off x, "x  " keeping
    // its value - and a BulkInsert into the empty table, whose index is built once its rows are in, leaves each row it
    // inserted with its number under x.
    [Fact]
    public void A_batch_puts_an_autonumber_under_its_own_column_and_a_refused_one_takes_it_off_there()
    {
        using (var db = Database.Create(_mdb))
            db.CreateTable("T", new[]
            {
                new ColumnDefinition("x", DataType.Int32) { IsAutoNumber = true }, Long("y  "),
                new ColumnDefinition("S", DataType.Text) { MaxLength = 2 },
            }, "x");
        RenameInDefinition(_mdb, "T", "y  ", "x  ");
        using (var db = Database.Open(_mdb))
        {
            var table = db.GetTable("T");
            var refused = new[] { new Row { ["x  "] = 7 }, new Row { ["x  "] = 8, ["S"] = "too long" } };
            Assert.Throws<BatchInsertException>(() => table.InsertRange(refused));
            Assert.All(refused, row => Assert.Equal(new[] { "x  " }, row.Keys.Where(k => k.StartsWith("x", StringComparison.Ordinal))));
            Assert.Equal((7, 8), ((int)refused[0]["x  "]!, (int)refused[1]["x  "]!));

            var loaded = new[] { new Row { ["x  "] = 7 }, new Row { ["x  "] = 8 } };
            Assert.Equal(2, table.BulkInsert(loaded));
            Assert.Equal(new[] { (1, 7), (2, 8) }, loaded.Select(row => ((int)row["x"]!, (int)row["x  "]!)));
        }
        using var reopen = Database.Open(_mdb);
        Assert.Equal(new[] { (1, 7), (2, 8) }, reopen.GetTable("T").Rows.Select(row => ((int)row["x"]!, (int)row["x  "]!)));
    }

    // A row read with only "x  " of the four columns x, "x  ", xµ and xμ, given a value by "X": the name goes where it
    // goes from a whole row and from an insert - to x, the first column a lookup of it finds - and not to the entry of
    // "x  " the row holds. Before it is given one, the row has no value for "X".
    [Fact]
    public void A_row_read_with_some_of_the_columns_sends_a_name_of_another_spelling_where_a_whole_row_does()
    {
        using (var db = Database.Create(_mdb))
            db.CreateTable("Dup", new[] { Long("x"), Long("y  "), Long(X(0xB5)), Long(X(0x3BC)) });
        RenameInDefinition(_mdb, "Dup", "y  ", "x  ");
        using (var db = Database.Open(_mdb))
        {
            var table = db.GetTable("Dup");
            table.Insert(new Row { ["x"] = 1, ["x  "] = 2, [X(0xB5)] = 3, [X(0x3BC)] = 4 });
            var cursor = table.CreateCursor();
            cursor.ColumnNames = new[] { "x  " };
            Assert.True(cursor.MoveNext());
            var row = cursor.Current!;
            Assert.False(row.TryGetValue("X", out _));
            row["X"] = 777;
            Assert.Equal((777, 2), ((int)row["X"]!, (int)row["x  "]!));
            Assert.Equal(new[] { "x  ", "x" }, row.Keys);
            table.Update(row);
        }
        using var reopen = Database.Open(_mdb);
        var read = reopen.GetTable("Dup").Rows.Single();
        Assert.Equal(new object?[] { 777, 2, 3, 4 }, new[] { "x", "x  ", X(0xB5), X(0x3BC) }.Select(name => read[name]));
        Assert.Equal(777, read["X"]);
    }

    // A row read without column "x  ", beside x: given a value by the name of the column it was not read with, it gives
    // that column the value, not x, whose name is one with it.
    [Fact]
    public void A_row_read_with_some_of_the_columns_gives_a_value_to_the_column_of_exactly_its_name()
    {
        using (var db = Database.Create(_mdb))
            db.CreateTable("T", new[] { Long("Id"), Long("x"), Long("y  ") }, "Id");
        RenameInDefinition(_mdb, "T", "y  ", "x  ");
        using (var db = Database.Open(_mdb))
        {
            var table = db.GetTable("T");
            table.Insert(new Row { ["Id"] = 1, ["x"] = 1, ["x  "] = 2 });
            var cursor = table.CreateCursor();
            cursor.ColumnNames = new[] { "Id", "x" };
            Assert.True(cursor.MoveNext());
            var row = cursor.Current!;
            row["x  "] = 20;
            Assert.Equal(1, row["x"]);
            table.Update(row);
        }
        using var reopen = Database.Open(_mdb);
        var read = reopen.GetTable("T").Rows.Single();
        Assert.Equal((1, 20), ((int)read["x"]!, (int)read["x  "]!));
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
