using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using System.Runtime.InteropServices;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Which two names are one (#63), refereed by DAO, in a database that sorts in General -
    /// Legacy and in one that sorts in General. A pair of names DAO makes in a twin file - as two fields of a table, two
    /// tables and two indexes of a table - is one or two to the engine as it is to DAO: the engine refuses the second
    /// name where DAO refuses it, or makes both and its own lookups then take one for the other, and makes both where
    /// DAO keeps them apart - but for ƀ and Ƀ in a database that sorts in General, which ACE takes as one field name
    /// there in a process that has used such a database through DAO before, and which the engine refuses. Names the
    /// engine makes that Access keeps apart - the micro sign and mu, an accent and none, the dotless i and I - DAO lists
    /// as the engine wrote them and finds each by its own name, ACE reads and writes a table's rows by them, and DAO
    /// compacts the file with them as they were. Which names are one is not the same in every Access build (#95), so
    /// two of the tests ask the machine's DAO first.
    /// </summary>
    public partial class SchemaChangesInAccessTests
    {
        /// <summary>
        /// Pairs of names, each made in a database of each order. In another case, width or kana, an accent written as
        /// one character or as two, ß and ss: one. A trailing space, and an ideographic space against a space at the end:
        /// one, or two DAO then confuses. The micro sign and mu, an accent and none, ı and I: two. Ȥ and ȥ: one to General
        /// - Legacy's keys, which a database that sorts in General compares a table's name by too. ƀ and Ƀ: one to
        /// General's keys - a table's name in a database that sorts in General - and two to General - Legacy's.
        /// </summary>
        private static readonly string[][] NamePairs =
        {
            new[] { "Name", "NAME" },
            new[] { "x\u00C4", "x\u00E4" },
            new[] { "x\uFF21", "xA" },
            new[] { "x\u3042", "x\u30A2" },
            new[] { "x\u00E1", "xa\u0301" },
            new[] { "x\u00DF", "xss" },
            new[] { "xb", "xb " },
            new[] { "x\u3000", "x " },
            new[] { "x\u00B5", "x\u03BC" },
            new[] { "xe", "x\u00E9" },
            new[] { "x\u0131", "xI" },
            new[] { "x\u0224", "x\u0225" },
            new[] { "x\u0180", "x\u0243" },
        };

        private static readonly string[] NameKinds = { "fields", "tables", "indexes" };

        // The DAO error a call threw: 3191 a field defined twice, 3010 a table there already, 3284 an index there already.
        private static int DaoError(Exception e)
        {
            var com = e.GetBaseException() as COMException;
            return com == null ? -1 : com.ErrorCode & 0xFFFF;
        }

        private static bool Finds(Func<string> lookup, string name)
        {
            try { return lookup() == name; }
            catch (Exception) { return false; }
        }

        /// <summary>The values of the one row ACE selects, as text, in the order the query names them.</summary>
        private static List<string> AceValues(string path, string sql)
        {
            var values = new List<string>();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var command = new OleDbCommand(sql, connection))
            {
                connection.Open();
                using (var reader = command.ExecuteReader())
                {
                    Assert.True(reader.Read(), "ACE selected no row: " + sql);
                    for (var i = 0; i < reader.FieldCount; i++) values.Add(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                    Assert.False(reader.Read(), "ACE selected more than one row: " + sql);
                }
            }
            return values;
        }

        private static bool SelectsValue(object database, string sql, int value)
        {
            dynamic db = database;
            dynamic rs = null;
            try
            {
                rs = db.OpenRecordset(sql);
                return Convert.ToInt32(rs.Fields[0].Value) == value;
            }
            catch (Exception) { return false; }
            finally { if (rs != null) rs.Close(); }
        }

        /// <summary>
        /// What DAO makes of <paramref name="a"/> and <paramref name="b"/> as the names of two of a kind: "one" when it
        /// refuses the second; "confused" when it makes both and then a lookup by one of them - in the collection, or in
        /// SQL for a field - finds the other or nothing; "two" when each finds its own.
        /// </summary>
        private static string DaoPair(object database, string kind, string a, string b, string tag)
        {
            dynamic db = database;
            switch (kind)
            {
                case "fields":
                {
                    string table = "F" + tag;
                    dynamic td = db.CreateTableDef(table);
                    td.Fields.Append(td.CreateField(a, 4));
                    try { td.Fields.Append(td.CreateField(b, 4)); db.TableDefs.Append(td); }
                    catch (Exception e) when (DaoError(e) == 3191) { return "one"; }
                    db.TableDefs.Refresh();
                    dynamic made = db.TableDefs[table];
                    bool found = Finds(() => (string)made.Fields[a].Name, a) && Finds(() => (string)made.Fields[b].Name, b);
                    // A row with 1 in the first field and 2 in the second; a table DAO cannot give one is confused too.
                    bool filled = false;
                    dynamic rs = null;
                    try { rs = db.OpenRecordset(table); rs.AddNew(); rs.Fields[0].Value = 1; rs.Fields[1].Value = 2; rs.Update(); filled = true; }
                    catch (Exception) { }
                    finally { if (rs != null) try { rs.Close(); } catch (Exception) { } }
                    found = found && filled && SelectsValue(database, "SELECT [" + a + "] FROM [" + table + "]", 1)
                                            && SelectsValue(database, "SELECT [" + b + "] FROM [" + table + "]", 2);
                    return found ? "two" : "confused";
                }
                case "tables":
                {
                    dynamic first = db.CreateTableDef(a);
                    first.Fields.Append(first.CreateField("A", 4));
                    db.TableDefs.Append(first);
                    try
                    {
                        dynamic second = db.CreateTableDef(b);
                        second.Fields.Append(second.CreateField("B", 4));
                        db.TableDefs.Append(second);
                    }
                    catch (Exception e) when (DaoError(e) == 3010) { return "one"; }
                    db.TableDefs.Refresh();
                    return Finds(() => (string)db.TableDefs[a].Name, a) && Finds(() => (string)db.TableDefs[b].Name, b) ? "two" : "confused";
                }
                default:
                {
                    string table = "I" + tag;
                    dynamic td = db.CreateTableDef(table);
                    td.Fields.Append(td.CreateField("Id", 4));
                    td.Fields.Append(td.CreateField("N", 4));
                    db.TableDefs.Append(td);
                    dynamic holder = db.TableDefs[table];
                    dynamic first = holder.CreateIndex(a);
                    first.Fields.Append(first.CreateField("Id"));
                    holder.Indexes.Append(first);
                    try
                    {
                        dynamic second = holder.CreateIndex(b);
                        second.Fields.Append(second.CreateField("N"));
                        holder.Indexes.Append(second);
                    }
                    catch (Exception e) when (DaoError(e) == 3284) { return "one"; }
                    db.TableDefs.Refresh();
                    dynamic made = db.TableDefs[table];
                    return Finds(() => (string)made.Indexes[a].Name, a) && Finds(() => (string)made.Indexes[b].Name, b) ? "two" : "confused";
                }
            }
        }

        /// <summary>
        /// What the engine makes of the same two names: "one" when it refuses the second as one with the first - its
        /// message names the first - and "two" when it makes both.
        /// </summary>
        private static string EnginePair(string path, string kind, string a, string b, string tag)
        {
            using (var db = Database.Open(path))
            {
                try
                {
                    switch (kind)
                    {
                        case "fields":
                            db.CreateTable("F" + tag, new[] { Long(a) }).AddColumn(Long(b));
                            break;
                        case "tables":
                            db.CreateTable(a, new[] { Long("A") });
                            db.CreateTable(b, new[] { Long("B") });
                            break;
                        default:
                            var holder = db.CreateTable("I" + tag, new[] { Long("Id"), Long("N") });
                            holder.AddIndex(new IndexDefinition(a, "Id"));
                            holder.AddIndex(new IndexDefinition(b, "N"));
                            break;
                    }
                    return "two";
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                {
                    return e.Message.Contains("'" + a + "'") ? "one" : "refused otherwise: " + e.Message;
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void a_name_dao_takes_as_one_with_another_the_engine_refuses_and_one_it_keeps_apart_the_engine_makes(bool general)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = NewDatabase(general, accessMade: false);
            var twin = NewDatabase(general, accessMade: true);
            var verdicts = new List<string>();
            var wrong = new List<string>();
            for (int i = 0; i < NamePairs.Length; i++)
            {
                // A table's name has the pair's number before it, so that no two pairs' tables are one: the number
                // makes no two names one that were not, nor two that were one.
                foreach (var kind in NameKinds)
                {
                    string a = kind == "tables" ? "T" + i + "_" + NamePairs[i][0] : NamePairs[i][0];
                    string b = kind == "tables" ? "T" + i + "_" + NamePairs[i][1] : NamePairs[i][1];
                    string dao = WithDao(twin, db => DaoPair((object)db, kind, a, b, i.ToString()));
                    string engine = EnginePair(path, kind, a, b, i.ToString());
                    string line = string.Join(" ", a.Select(c => ((int)c).ToString("X4"))) + " / " + string.Join(" ", b.Select(c => ((int)c).ToString("X4")))
                                + " as " + kind + ": DAO " + dao + ", engine " + engine;
                    verdicts.Add(line);
                    // In a database that sorts in General the engine takes ƀ and Ƀ as one as a field's and an index's
                    // name too, which DAO keeps apart in a process that has not used such a database before - and ACE
                    // takes as one in a process that has.
                    bool oneOnPurpose = general && NamePairs[i][0] == "xƀ";
                    if (engine != (dao == "two" && !oneOnPurpose ? "two" : "one")) wrong.Add(line);
                }
            }
            Assert.True(wrong.Count == 0, "the engine's verdicts are not DAO's:\n  " + string.Join("\n  ", wrong) + "\nall:\n  " + string.Join("\n  ", verdicts));

            // Each order has pairs of each verdict.
            Assert.Contains(verdicts, v => v.Contains("DAO one"));
            Assert.Contains(verdicts, v => v.Contains("DAO two"));
            Assert.Contains(verdicts, v => v.Contains("DAO confused"));
        }

        /// <summary>
        /// Pairs of names the engine makes as two columns, which Access was measured to keep apart: the micro sign and
        /// mu, an accent and none, the dotless i and I; in a database that sorts in General - Legacy also ƀ and Ƀ, which
        /// the engine takes as one in a database that sorts in General. Which of them Access keeps apart differs between
        /// Access builds: the ACE 2016 redistributable's SQL takes ƀ and Ƀ as one in General - Legacy, where Office's
        /// keeps them apart.
        /// </summary>
        private static string[][] ApartPairs(bool general)
        {
            var pairs = new List<string[]> { new[] { "x\u00B5", "x\u03BC" }, new[] { "xe", "x\u00E9" }, new[] { "x\u0131", "xI" } };
            if (!general) pairs.Add(new[] { "x\u0180", "x\u0243" });
            return pairs.ToArray();
        }

        // Whether DAO on this machine keeps two names apart as two of a kind in a twin database it made: makes both, finds
        // each by its own name and, for fields, reads each by it in SQL. A pair it does not keep apart is printed: there is
        // nothing to referee here for it.
        private static bool DaoKeepsApart(string twin, string kind, string a, string b, string tag)
        {
            string verdict = WithDao(twin, db => DaoPair((object)db, kind, a, b, tag));
            if (verdict != "two")
                TestContext.Current.TestOutputHelper?.WriteLine("Left out: DAO here makes the " + kind + " "
                    + string.Join(" ", a.Select(c => ((int)c).ToString("X4"))) + " / " + string.Join(" ", b.Select(c => ((int)c).ToString("X4")))
                    + " " + verdict + ".");
            return verdict == "two";
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void names_the_engine_makes_that_access_keeps_apart_dao_lists_and_finds_and_ace_reads_writes_and_compacts(bool general)
        {
            if (general) SkipUnlessAceOpensAccess2010();

            // Which pairs Access keeps apart differs between Access builds, so DAO on this machine is asked first, as two
            // fields of a table it makes itself in a twin database: only the pairs it keeps apart are refereed. Three
            // pairs are kept apart by every Access measured - Office's ACE 16 and the ACE 2016 redistributable - so
            // fewer is a failure, not a pass with nothing checked.
            var twin = NewDatabase(general, accessMade: true);
            var all = ApartPairs(general);
            var pairs = all.Where((pair, i) => DaoKeepsApart(twin, "fields", pair[0], pair[1], "P" + i)).ToList();
            Assert.True(pairs.Count >= 3, "DAO here keeps apart " + pairs.Count + " of the " + all.Length + " pairs, fewer than the three every Access measured keeps apart.");
            bool mu = pairs.Any(pair => pair[0] == "x\u00B5");
            bool indexes = mu && DaoKeepsApart(twin, "indexes", "ix\u00B5", "ix\u03BC", "IX");
            bool tables = DaoKeepsApart(twin, "tables", "T\u00B5", "T\u03BC", "TT");
            var columns = new[] { "Id" }.Concat(pairs.SelectMany(pair => pair)).ToArray();
            var indexNames = indexes ? new[] { "ix\u00B5", "ix\u03BC" } : new string[0];
            var tableNames = new[] { "Apart" }.Concat(tables ? new[] { "T\u00B5", "T\u03BC" } : new string[0]).ToArray();

            var path = NewDatabase(general, accessMade: false);
            using (var db = Database.Open(path))
            {
                var table = db.CreateTable("Apart", columns.Select(c => c == "Id" ? Long(c) : Text(c, 20)).ToArray(), "Id");
                if (indexes)
                {
                    table.AddIndex(new IndexDefinition("ix\u00B5", "x\u00B5"));
                    table.AddIndex(new IndexDefinition("ix\u03BC", "x\u03BC"));
                }
                var row = new Row { ["Id"] = 1 };
                foreach (var name in columns.Skip(1)) row[name] = "1 " + name;
                table.Insert(row);
                if (tables)
                {
                    db.CreateTable("T\u00B5", new[] { Long("Id") }, "Id");
                    db.CreateTable("T\u03BC", new[] { Long("Id") }, "Id");
                }
            }

            // DAO lists the columns, the indexes and the tables as the engine wrote them, and finds each by its own name.
            Same(columns.Select(c => c + (c == "Id" ? ":4" : ":10")).ToList(), DaoFields(path, "Apart"));
            Assert.Equal(tableNames, DaoTables(path));
            var found = WithDao(path, db =>
            {
                var lookups = new List<string>();
                foreach (var name in columns) lookups.Add(Finds(() => (string)db.TableDefs["Apart"].Fields[name].Name, name) ? "found" : "not " + name);
                foreach (var name in indexNames) lookups.Add(Finds(() => (string)db.TableDefs["Apart"].Indexes[name].Name, name) ? "found" : "not " + name);
                foreach (var name in tableNames.Skip(1)) lookups.Add(Finds(() => (string)db.TableDefs[name].Name, name) ? "found" : "not " + name);
                return lookups;
            });
            Assert.All(found, lookup => Assert.Equal("found", lookup));

            // ACE reads the row by each name - its values by their places, as a dictionary of names compared without
            // regard to case might take two of these names for one - and writes one by them, which the engine reads.
            var select = "SELECT " + string.Join(", ", columns.Select(c => "[" + c + "]")) + " FROM [Apart] WHERE Id = 1";
            Assert.Equal(columns.Select(c => c == "Id" ? "1" : "1 " + c), AceValues(path, select));
            Execute(path, "INSERT INTO [Apart] (" + string.Join(", ", columns.Select(c => "[" + c + "]")) + ") VALUES (2, "
                          + string.Join(", ", columns.Skip(1).Select(c => "'2 " + c + "'")) + ")");
            using (var db = Database.Open(path))
            {
                var two = db.GetTable("Apart").FindRowByPrimaryKey(2);
                Assert.NotNull(two);
                foreach (var name in columns.Skip(1)) Assert.Equal("2 " + name, two[name]);
            }

            // DAO compacts the file, with every name as it was.
            var compacted = Compacted(path);
            Same(DaoFields(path, "Apart"), DaoFields(compacted, "Apart"));
            Assert.Equal(tableNames, DaoTables(compacted));
            Assert.Equal(2, AceRows(compacted, "SELECT Id FROM [Apart]").Count);
        }

        /// <summary>
        /// A table DAO makes in a database that sorts in General - Legacy, with fields x, x and two spaces, x and the
        /// micro sign, and x and mu: four fields to Access, of which the engine's rule takes the first two as one. A
        /// caller's row gives each its value by its exact name, the engine writes each to its own field, and ACE reads
        /// the four back, by their places - its SQL takes the first two names for one. Office's DAO makes the table; the
        /// ACE 2016 redistributable's refuses it ("Cannot define field more than once"), and with no such table there is
        /// nothing to referee: the test is skipped there, saying why.
        /// </summary>
        [Fact]
        public void a_row_given_a_value_for_each_of_four_fields_dao_made_writes_each_to_its_own_as_ace_reads_them()
        {
            var path = NewDatabase(false, accessMade: true);
            var names = new[] { "x", "x  ", "x\u00B5", "x\u03BC" };
            string refused = null;
            try
            {
                WithDao(path, db =>
                {
                    dynamic td = db.CreateTableDef("Dup");
                    foreach (var name in names) td.Fields.Append(td.CreateField(name, 4));
                    db.TableDefs.Append(td);
                    return 0;
                });
            }
            catch (Exception e) when (DaoError(e) == 3191)
            {
                refused = e.GetBaseException().Message;
            }
            if (refused != null)
                Assert.Skip("DAO on this machine does not make a table with fields x, \"x  \", x\u00B5 and x\u03BC (" + refused
                    + "), so there is no such table here to referee: which names are one differs between Access builds.");
            Same(names.Select(name => name + ":4").ToList(), DaoFields(path, "Dup"));

            using (var db = Database.Open(path))
                db.GetTable("Dup").Insert(new Row { ["x"] = 1, ["x  "] = 2, ["x\u00B5"] = 3, ["x\u03BC"] = 4 });

            Assert.Equal(new[] { "1", "2", "3", "4" }, AceValues(path, "SELECT * FROM [Dup]"));
            using (var db = Database.Open(path))
            {
                var read = db.GetTable("Dup").Rows.Single();
                Assert.Equal(new object[] { 1, 2, 3, 4 }, names.Select(name => read[name]).ToArray());
            }
        }
    }
}
