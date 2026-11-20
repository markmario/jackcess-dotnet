using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// A table definition that ends near the end of a definition page (#37). Access puts a
    /// definition on the fewest pages that hold its length and eight bytes more, so one that ends within eight
    /// bytes of a page's end has a page after it with none of the definition on it; the last page's free space is
    /// what the pages hold less the length and eight. A table the engine wrote without that page was one Access
    /// did not open at all: "Unrecognized database format". Here the engine makes a table at every length from
    /// twelve bytes short of the first, second and third page's end to twelve past it, adds a column that takes a
    /// table of its own to either side of each end, and renames, adds and drops a column of tables Access made
    /// either side of each end; DAO lists every field and reads every row, ACE adds rows and a column of its own,
    /// and DAO compacts the file.
    /// <para>
    /// Without <see cref="Sweeps.Full"/> they run at the first and second page's end only, and the engine makes
    /// tables of the lengths from nine bytes short of each to one past it: the eight lengths that leave a page with
    /// none of the definition on it, and one either side, where the second page is not needed and where it is.
    /// </para>
    /// </summary>
    public partial class SchemaChangesInAccessTests
    {
        /// <summary>The ends of a definition's first, second and third page: 4,096 bytes, and 4,088 for each page after.</summary>
        private static readonly int[] DefinitionPageEnds = { 4096, 8184, 12272 };

        /// <summary>The page ends the tests below run at: all three in the full sweep, the first two in the sample.</summary>
        private static IEnumerable<int> PageEndsSwept => Sweeps.Full ? DefinitionPageEnds : DefinitionPageEnds.Take(2);

        /// <summary>
        /// The names of Long columns that make a definition of <paramref name="length"/> bytes in a table with no
        /// index: 65 bytes, and for each column 27 and twice the characters of its name.
        /// </summary>
        private static List<string> ColumnsMaking(int length)
        {
            var rest = length - 65;
            for (var count = 1; count <= 255; count++)
            {
                var characters = rest - 27 * count;
                if (characters % 2 != 0 || characters / 2 < 4 * count || characters / 2 > 64 * count) continue;
                characters /= 2;
                var names = new List<string>();
                for (var i = 1; i <= count; i++)
                {
                    var take = Math.Min(64, characters - 4 * (count - i));
                    characters -= take;
                    names.Add("c" + i.ToString("D3") + new string('x', take - 4));
                }
                return names;
            }
            throw new InvalidOperationException("No columns make a definition of " + length + " bytes.");
        }

        /// <summary>The length the definition of <paramref name="table"/> says it has, and the pages it is on, from the file's bytes.</summary>
        private static Tuple<int, int> DefinitionLengthAndPages(string path, string table)
        {
            var definition = DefinitionBytes(path, TablePages(path).Single(p => p.Value == table).Key);
            return Tuple.Create(BitConverter.ToInt32(definition.Item1, 8), definition.Item2.Count);
        }

        /// <summary>The pages Access has a definition of <paramref name="length"/> bytes on.</summary>
        private static int PagesAccessGives(int length)
        {
            return length + 8 <= 4096 ? 1 : 1 + (length + 8 - 4096 + 4087) / 4088;
        }

        /// <summary>
        /// What DAO reads of each of <paramref name="tables"/>, in one session: its fields, by name and type, and
        /// its rows, each as the fields it has a value in - "#position=value" - the rows sorted.
        /// </summary>
        private static Dictionary<string, string> DaoFieldsAndRows(string path, IEnumerable<string> tables)
        {
            return WithDao(path, db =>
            {
                var read = new Dictionary<string, string>();
                foreach (var table in tables)
                {
                    var fields = new List<string>();
                    foreach (dynamic field in db.TableDefs[table].Fields)
                        fields.Add((string)field.Name + ":" + (int)field.Type);
                    var rows = new List<string>();
                    dynamic recordset = db.OpenRecordset("SELECT * FROM [" + table + "]", 4 /* dbOpenSnapshot */);
                    try
                    {
                        int count = recordset.Fields.Count;
                        while (!(bool)recordset.EOF)
                        {
                            var values = new List<string>();
                            for (var i = 0; i < count; i++)
                            {
                                object value = recordset.Fields[i].Value;
                                if (value != null && !(value is DBNull))
                                    values.Add("#" + i + "=" + Convert.ToString(value, CultureInfo.InvariantCulture));
                            }
                            rows.Add(string.Join(" ", values));
                            recordset.MoveNext();
                        }
                    }
                    finally { recordset.Close(); }
                    read[table] = FieldsAndRows(fields, rows);
                }
                return read;
            });
        }

        /// <summary>A table's fields and rows in the form <see cref="DaoFieldsAndRows"/> gives them.</summary>
        private static string FieldsAndRows(IEnumerable<string> fields, IEnumerable<string> rows)
        {
            return "fields " + string.Join(", ", fields) + "; rows [" + string.Join("] [", rows.OrderBy(r => r, StringComparer.Ordinal)) + "]";
        }

        public static IEnumerable<object[]> BoundaryKinds()
        {
            return new[] { new object[] { false }, new object[] { true } };
        }

        [Theory]
        [MemberData(nameof(BoundaryKinds))]
        public void tables_the_engine_makes_with_definitions_ending_near_a_pages_end_are_ones_access_reads_adds_to_and_compacts(bool general)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = NewDatabase(general, accessMade: false);
            var lengths = PageEndsSwept.SelectMany(end => Sweeps.Full ? Enumerable.Range(end - 12, 25) : Enumerable.Range(end - 9, 11)).ToList();
            var tables = lengths.Select(length => "E" + length).ToList();

            using (var db = Database.Open(path))
            {
                foreach (var length in lengths)
                {
                    var names = ColumnsMaking(length);
                    var table = db.CreateTable("E" + length, names.Select(Long).ToList());
                    table.Insert(new Row { [names[0]] = length });
                }
            }

            foreach (var length in lengths)
            {
                var made = DefinitionLengthAndPages(path, "E" + length);
                Assert.True(made.Item1 == length, "E" + length + " has a definition of " + made.Item1 + " bytes");
                Assert.True(made.Item2 == PagesAccessGives(length), "E" + length + " is on " + made.Item2 + " pages; Access has such a definition on " + PagesAccessGives(length));
            }

            Action<string, string, string[]> check = (file, when, rows) =>
            {
                var read = DaoFieldsAndRows(file, tables);
                foreach (var length in lengths)
                {
                    var expected = FieldsAndRows(ColumnsMaking(length).Select(n => n + ":4"), rows.Select(r => string.Format(r, length)));
                    Assert.True(expected == read["E" + length], "DAO reads E" + length + " " + when + " as\n  " + read["E" + length] + "\nnot\n  " + expected);
                }
            };
            check(path, "as the engine made it", new[] { "#0={0}" });

            foreach (var length in lengths)
                Execute(path, "INSERT INTO E" + length + " (" + ColumnsMaking(length)[0] + ") VALUES (-1)");
            foreach (var length in lengths)
                Assert.True(Count(path, "E" + length, ColumnsMaking(length)[0] + " = " + length) == 1, "ACE does not find the engine's row of E" + length);
            check(path, "after ACE added a row to it", new[] { "#0={0}", "#0=-1" });

            var compacted = Compact(path);
            check(compacted, "after DAO compacted the file", new[] { "#0={0}", "#0=-1" });
            using (var db = Database.Open(compacted))
                foreach (var length in lengths)
                    Assert.True(db.GetTable("E" + length).Rows.Count() == 2, "the engine does not read both rows of E" + length + " in the compacted file");
        }

        [Theory]
        [MemberData(nameof(BoundaryKinds))]
        public void columns_the_engine_adds_that_end_a_definition_near_a_pages_end_leave_tables_access_reads_adds_to_and_compacts(bool general)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = NewDatabase(general, accessMade: false);
            var lengths = PageEndsSwept.SelectMany(end => new[] { end - 8, end - 7, end - 4, end, end + 1 }).ToList();
            var tables = lengths.Select(length => "F" + length).ToList();

            // A Long column named zz adds 31 bytes to a definition: 27, and 2 for each character of its name.
            using (var db = Database.Open(path))
                foreach (var length in lengths)
                {
                    var names = ColumnsMaking(length - 31);
                    var table = db.CreateTable("F" + length, names.Select(Long).ToList());
                    table.Insert(new Row { [names[0]] = length });
                    table.AddColumn(Long("zz"));
                    table.Insert(new Row { [names[0]] = -1, ["zz"] = 1 });
                }
            foreach (var length in lengths)
            {
                var made = DefinitionLengthAndPages(path, "F" + length);
                Assert.True(made.Item1 == length && made.Item2 == PagesAccessGives(length),
                            "F" + length + " is " + made.Item1 + " bytes on " + made.Item2 + " pages after the engine added a column to it");
            }

            Action<string, string, string[]> check = (file, when, rows) =>
            {
                var read = DaoFieldsAndRows(file, tables);
                foreach (var length in lengths)
                {
                    var names = ColumnsMaking(length - 31);
                    var expected = FieldsAndRows(names.Select(n => n + ":4").Concat(new[] { "zz:4" }), rows.Select(r => string.Format(r, length, names.Count)));
                    Assert.True(expected == read["F" + length], "DAO reads F" + length + " " + when + " as\n  " + read["F" + length] + "\nnot\n  " + expected);
                }
            };
            check(path, "as the engine left it", new[] { "#0={0}", "#0=-1 #{1}=1" });

            foreach (var length in lengths)
                Execute(path, "INSERT INTO F" + length + " (zz) VALUES (2)");
            check(path, "after ACE added a row to it", new[] { "#0={0}", "#0=-1 #{1}=1", "#{1}=2" });

            var compacted = Compact(path);
            check(compacted, "after DAO compacted the file", new[] { "#0={0}", "#0=-1 #{1}=1", "#{1}=2" });
        }

        [Theory]
        [MemberData(nameof(BoundaryKinds))]
        public void tables_access_made_with_definitions_ending_near_a_pages_end_are_still_accesss_after_the_engine_changes_them(bool general)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = NewDatabase(general, accessMade: true);
            var lengths = PageEndsSwept.SelectMany(end => new[] { end - 8, end - 7, end - 4, end, end + 1 }).ToList();
            var tables = lengths.Select(length => "A" + length).ToList();

            foreach (var length in lengths)
            {
                var names = ColumnsMaking(length);
                Execute(path, "CREATE TABLE A" + length + " (" + string.Join(", ", names.Select(n => n + " LONG")) + ")",
                              "INSERT INTO A" + length + " (" + names[0] + ") VALUES (" + length + ")");
            }
            // What Access itself does with them is what the engine is held to.
            foreach (var length in lengths)
            {
                var made = DefinitionLengthAndPages(path, "A" + length);
                Assert.True(made.Item1 == length, "Access made A" + length + " with a definition of " + made.Item1 + " bytes");
                Assert.True(made.Item2 == PagesAccessGives(length), "Access put A" + length + " on " + made.Item2 + " pages");
            }

            // DAO reads every table as it should be: its columns - the first renamed - and Added at the end when it
            // has it, and the rows given as "#position=value" with {0} for the table's length and {1} for Added's
            // position.
            Action<string, string, bool, string[]> check = (file, when, withAdded, rows) =>
            {
                var read = DaoFieldsAndRows(file, tables);
                foreach (var length in lengths)
                {
                    var names = ColumnsMaking(length);
                    names[0] = "r" + names[0].Substring(1);
                    var fields = names.Select(n => n + ":4").ToList();
                    if (withAdded) fields.Add("Added:4");
                    var expected = FieldsAndRows(fields, rows.Select(r => string.Format(r, length, names.Count)));
                    Assert.True(expected == read["A" + length], "DAO reads A" + length + " " + when + " as\n  " + read["A" + length] + "\nnot\n  " + expected);
                }
            };
            Action<string> onAccesssPages = when =>
            {
                foreach (var length in lengths)
                {
                    var now = DefinitionLengthAndPages(path, "A" + length);
                    Assert.True(now.Item1 == length && now.Item2 == PagesAccessGives(length),
                                "A" + length + " is " + now.Item1 + " bytes on " + now.Item2 + " pages " + when);
                }
            };

            // A rename to a name as long: the definition is as long as it was, and is still on Access's pages.
            using (var db = Database.Open(path))
                foreach (var length in lengths)
                    db.GetTable("A" + length).RenameColumn(ColumnsMaking(length)[0], "r" + ColumnsMaking(length)[0].Substring(1));
            onAccesssPages("after the engine renamed a column of it");
            check(path, "after the engine renamed a column", false, new[] { "#0={0}" });

            using (var db = Database.Open(path))
                foreach (var length in lengths)
                {
                    var table = db.GetTable("A" + length);
                    table.AddColumn(Long("Added"));
                    table.Insert(new Row { ["r" + ColumnsMaking(length)[0].Substring(1)] = -1, ["Added"] = 1 });
                }
            check(path, "after the engine added a column and a row", true, new[] { "#0={0}", "#0=-1 #{1}=1" });
            foreach (var length in lengths)
                Execute(path, "INSERT INTO A" + length + " (Added) VALUES (2)");
            check(path, "after ACE added a row", true, new[] { "#0={0}", "#0=-1 #{1}=1", "#{1}=2" });

            using (var db = Database.Open(path))
                foreach (var length in lengths)
                    db.GetTable("A" + length).DropColumn("Added");
            onAccesssPages("after the engine dropped the column it added");
            check(path, "after the engine dropped the column", false, new[] { "#0={0}", "#0=-1", "" });

            // Access changes them in its turn, rewriting the pages the engine wrote.
            foreach (var length in lengths)
                Execute(path, "ALTER TABLE A" + length + " ADD COLUMN Added LONG", "UPDATE A" + length + " SET Added = 7");
            check(path, "after ACE added a column to it", true, new[] { "#0={0} #{1}=7", "#0=-1 #{1}=7", "#{1}=7" });

            var compacted = Compact(path);
            check(compacted, "after DAO compacted the file", true, new[] { "#0={0} #{1}=7", "#0=-1 #{1}=7", "#{1}=7" });
            using (var db = Database.Open(compacted))
                foreach (var length in lengths)
                    Assert.True(db.GetTable("A" + length).Rows.Count(r => Equals(r["Added"], 7)) == 3, "the engine does not read the rows of A" + length + " in the compacted file");
        }
    }
}
