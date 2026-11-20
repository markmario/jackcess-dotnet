using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Table definitions of more than one page (#37). Access puts a definition too long
    /// for a page on as many as it takes; the engine read the first and refused to change such a table, or to make
    /// one. Here a table whose definition is already on three pages - Access's or the engine's - is given columns
    /// and indexes by the engine until it is on more, and rows; ACE lists the columns, reads and writes the rows,
    /// seeks through the indexes, changes the table itself - rewriting the pages the engine wrote - and DAO compacts
    /// the file. And the table of the corpus whose definition Access wrote over two pages takes rows from the
    /// engine that ACE then finds through its indexes.
    /// </summary>
    public partial class SchemaChangesInAccessTests
    {
        /// <summary>A column name of 56 characters: sixty of them, with their definitions, take two pages.</summary>
        private static string WideName(int n)
        {
            return "Column_" + n.ToString("D3") + "_" + new string((char)('a' + n % 26), 45);
        }

        /// <summary>
        /// W (Id, Name, and sixty Long columns with long names), keyed by Id, with five rows: its definition is on
        /// three pages when it is made, by ACE or by the engine.
        /// </summary>
        private string Wide(bool general, bool accessMade)
        {
            var path = NewDatabase(general, accessMade);
            if (accessMade)
            {
                var statements = new List<string>
                {
                    "CREATE TABLE W (Id LONG CONSTRAINT pk PRIMARY KEY, Name TEXT(40), "
                        + string.Join(", ", Enumerable.Range(0, 60).Select(n => WideName(n) + " LONG")) + ")",
                };
                for (var i = 1; i <= 5; i++)
                    statements.Add("INSERT INTO W (Id, Name, " + WideName(0) + ", " + WideName(59) + ") VALUES (" + i + ", 'made " + i + "', " + i + ", " + i * 59 + ")");
                Execute(path, statements.ToArray());
                return path;
            }

            using (var db = Database.Open(path))
            {
                var columns = new List<ColumnDefinition> { Long("Id"), Text("Name", 40) };
                columns.AddRange(Enumerable.Range(0, 60).Select(n => Long(WideName(n))));
                var w = db.CreateTable("W", columns, primaryKey: "Id");
                for (var i = 1; i <= 5; i++)
                    w.Insert(new Row { ["Id"] = i, ["Name"] = "made " + i, [WideName(0)] = i, [WideName(59)] = i * 59 });
            }
            return path;
        }

        /// <summary>The pages the definition of <paramref name="table"/> is on, read from the file's bytes.</summary>
        private static List<int> DefinitionPages(string path, string table)
        {
            return DefinitionBytes(path, TablePages(path).Single(p => p.Value == table).Key).Item2;
        }

        private static Values WideRow(int id, string by)
        {
            return V("Id", id, "Name", by + " " + id, WideName(0), id, WideName(59), id * 59, WideName(60), id * 60, WideName(98), id * 98,
                     "Words", by + " words " + id, "Notes", new string((char)('a' + id % 26), 200 + id * 30), "AddedLast", id * 1000);
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_definition_the_engine_takes_onto_more_pages_is_one_ace_reads_writes_to_changes_and_compacts(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = Wide(general, accessMade);
            var pagesAtFirst = DefinitionPages(path, "W").Count;
            Assert.True(pagesAtFirst == 3, "the definition is on " + pagesAtFirst + " pages when the table is made");

            var expected = new List<string> { "Id:4", "Name:10" };
            expected.AddRange(Enumerable.Range(0, 99).Select(n => WideName(n) + ":4"));
            expected.AddRange(new[] { "Words:10", "Notes:12", "AddedLast:4" });

            using (var db = Database.Open(path))
            {
                // Read whole, whoever wrote it: every column by its name.
                var w = db.GetTable("W");
                Assert.Equal(expected.Take(62).Select(f => f.Split(':')[0]), w.Columns.Select(c => c.Name));
                Assert.Null(w.InsertRefusal);

                for (var n = 60; n < 99; n++) w.AddColumn(Long(WideName(n)));
                w.AddColumn(Text("Words", 30));
                w.AddColumn(new ColumnDefinition("Notes", DataType.Memo));
                w.AddColumn(Long("AddedLast"));
                w.AddIndex(new IndexDefinition("ixLate", WideName(98)));
                w.AddIndex(new IndexDefinition("uxLast", "AddedLast") { IsUnique = true });
                for (var i = 6; i <= 30; i++) w.Insert(EngineRow(WideRow(i, "engine")));
            }
            Assert.True(DefinitionPages(path, "W").Count == 4, "the definition is on " + DefinitionPages(path, "W").Count + " pages after the engine added to it");

            // dbLong 4, dbText 10, dbMemo 12
            Assert.Equal(expected, DaoFields(path, "W"));
            Assert.Equal(5, Count(path, "W", WideName(98) + " IS NULL AND Words IS NULL AND Notes IS NULL AND AddedLast IS NULL"));
            Assert.Equal(25, Count(path, "W", "AddedLast IS NOT NULL"));

            // A seek is answered by the index's tree alone; a query through it as a scan answers.
            Assert.Equal(17, DaoSeek(path, "W", "ixLate", 17 * 98));
            Assert.Equal(17, DaoSeek(path, "W", "uxLast", 17000));
            Assert.Null(DaoSeek(path, "W", "uxLast", 31000));
            foreach (var value in new[] { 7 * 98, 30 * 98, 5 })
                Assert.Equal(Count(path, "W", WideName(98) + " + 0 = " + value), Count(path, "W", WideName(98) + " = " + value));

            // Both go on writing it.
            var subject = new Subject(path, "W", "Id");
            for (var i = 31; i <= 40; i++) subject.AceInserts(WideRow(i, "ace"));
            subject.AceUpdates(2, V(WideName(98), 222, "Words", "set by ace", "Notes", new string('z', 5000), "AddedLast", 2000));
            subject.EngineUpdates(3, V(WideName(98), 333, "Words", "set by engine", "Notes", new string('y', 5000), "AddedLast", 3000));
            subject.EngineInserts(WideRow(41, "engine"));
            subject.AceDeletes(4, 7, 32);
            subject.EngineDeletes(5, 8, 33);
            subject.Check("after ACE and the engine both wrote a table whose definition the engine took onto more pages");
            Assert.Equal("refused: duplicate", Ace(path, "INSERT INTO W (Id, AddedLast) VALUES (900, 41000)"));
            Assert.Equal(31, DaoSeek(path, "W", "ixLate", 31 * 98));
            Assert.Equal(41, DaoSeek(path, "W", "uxLast", 41000));

            // ACE changes the table itself: it writes the definition again, over the pages the engine put it on.
            Execute(path, "ALTER TABLE W ADD COLUMN AddedByAce LONG", "CREATE INDEX ixAce ON W (AddedByAce)", "UPDATE W SET AddedByAce = Id + 1");
            expected.Add("AddedByAce:4");
            Assert.Equal(expected, DaoFields(path, "W"));
            using (var db = Database.Open(path))
            {
                var w = db.GetTable("W");
                Assert.Equal(expected.Select(f => f.Split(':')[0]), w.Columns.Select(c => c.Name));
                Assert.Equal(40, w.CreateIndexCursor("ixAce").FindRow("AddedByAce", 41)["Id"]);
                w.Insert(new Row { ["Id"] = 50, ["Name"] = "after ace", ["AddedByAce"] = 5050, ["AddedLast"] = 50000 });
                w.AddColumn(Long("AddedAfterAce"));
                w.Insert(new Row { ["Id"] = 51, ["AddedAfterAce"] = 51 });
            }
            expected.Add("AddedAfterAce:4");
            Assert.Equal(50, DaoSeek(path, "W", "ixAce", 5050));
            Assert.Equal(1, Count(path, "W", "AddedAfterAce = 51"));
            var rows = AssertEngineReadsWhatAceReads(path, "W");

            var compacted = Compacted(path);
            Assert.Equal(expected, DaoFields(compacted, "W"));
            Same(DaoIndexes(path, "W"), DaoIndexes(compacted, "W"));
            AssertSame(Written(rows), Written(AceTable(compacted, "W")), "W after DAO compacted the file");
            Assert.Equal(41, DaoSeek(compacted, "W", "uxLast", 41000));
            AssertEngineReadsWhatAceReads(compacted, "W");
        }

        // A table has at most 255 columns, to Access and to the engine: the engine makes one of 255, whose definition
        // runs over many pages, and ACE reads it, adds to it and refuses it a 256th column as the engine does.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void a_table_the_engine_makes_with_255_columns_is_one_ace_reads_and_adds_to(bool general)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = NewDatabase(general, accessMade: false);
            var names = Enumerable.Range(0, 254).Select(WideName).ToList();
            using (var db = Database.Open(path))
            {
                var columns = new List<ColumnDefinition> { Long("Id") };
                columns.AddRange(names.Select(Long));
                var t = db.CreateTable("Most", columns, new[] { "Id" }, new[] { new IndexDefinition("ixLast", names[253]) });
                for (var i = 1; i <= 20; i++) t.Insert(new Row { ["Id"] = i, [names[0]] = i, [names[253]] = -i });
                Assert.Throws<ArgumentException>(() => t.AddColumn(Long("OneTooMany")));
            }
            Assert.True(DefinitionPages(path, "Most").Count >= 9, "the definition is on " + DefinitionPages(path, "Most").Count + " pages");

            Assert.Equal(255, DaoFields(path, "Most").Count);
            Assert.StartsWith("refused", Ace(path, "ALTER TABLE Most ADD COLUMN OneTooMany LONG"));
            Assert.Equal(20, Count(path, "Most", names[253] + " < 0"));
            Assert.Equal(9, DaoSeek(path, "Most", "ixLast", -9));
            Assert.Equal("made", Ace(path, "INSERT INTO Most (Id, " + names[253] + ") VALUES (21, -21)"));
            Assert.Equal(21, DaoSeek(path, "Most", "ixLast", -21));
            var rows = AssertEngineReadsWhatAceReads(path, "Most");
            AssertSame(Written(rows), Written(AceTable(Compacted(path), "Most")), "Most after DAO compacted the file");
        }

        // MSP_PROJECTS of the corpus's common2 files: 74 columns, the definition Access wrote for it on two pages.
        // The engine read the first and wrote no row to it.
        [Theory]
        [InlineData("V2003", "common2V2003.mdb")]
        [InlineData("V2010", "common2V2010.accdb")]
        public void a_table_whose_definition_access_wrote_over_two_pages_takes_rows_and_a_column_from_the_engine(string version, string file)
        {
            if (version == "V2010") SkipUnlessAceOpensAccess2010();
            var corpus = typeof(SchemaChangesInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineCorpusDir").Value;
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + Path.GetExtension(file));
            File.Copy(Path.Combine(corpus, version, file), path);
            Assert.Equal(2, DefinitionPages(path, "MSP_PROJECTS").Count);
            var indexes = DaoIndexes(path, "MSP_PROJECTS");
            var before = Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM MSP_PROJECTS"));

            using (var db = Database.Open(path))
            {
                var projects = db.GetTable("MSP_PROJECTS");
                Assert.Null(projects.InsertRefusal);
                for (var i = 1; i <= 40; i++)
                    projects.Insert(new Row { ["PROJ_ID"] = 5000 + i, ["PROJ_NAME"] = "engine " + i, ["PROJ_OPT_SPREAD_PCT_COMP"] = i % 2 == 0 });
                projects.DeleteRow("PROJ_ID", 5007);
                projects.AddColumn(Long("ADDED_BY_THE_ENGINE"));
                projects.AddIndex(new IndexDefinition("ixAdded", "ADDED_BY_THE_ENGINE"));
                projects.Insert(new Row { ["PROJ_ID"] = 6000, ["PROJ_NAME"] = "with the new column", ["ADDED_BY_THE_ENGINE"] = 66 });
            }

            Assert.Equal(before + 40, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM MSP_PROJECTS")));
            Assert.Equal(DaoIndexes(path, "MSP_PROJECTS").Where(ix => !ix.StartsWith("ixAdded:")).ToList(), indexes);
            foreach (var id in new[] { 5001, 5007, 5040, 6000 })
                Assert.Equal(Count(path, "MSP_PROJECTS", "PROJ_ID + 0 = " + id), Count(path, "MSP_PROJECTS", "PROJ_ID = " + id));
            Assert.Equal(0, Count(path, "MSP_PROJECTS", "PROJ_ID = 5007"));
            Assert.Equal("with the new column", Scalar(path, "SELECT PROJ_NAME FROM MSP_PROJECTS WHERE ADDED_BY_THE_ENGINE = 66"));
            Assert.Equal(20, Count(path, "MSP_PROJECTS", "PROJ_OPT_SPREAD_PCT_COMP = True AND PROJ_ID > 5000 AND PROJ_ID < 6000"));

            Assert.Equal("made", Ace(path, "INSERT INTO MSP_PROJECTS (PROJ_ID, PROJ_NAME, ADDED_BY_THE_ENGINE) VALUES (7000, 'ace', 77)"));
            Assert.Equal("made", Ace(path, "UPDATE MSP_PROJECTS SET ADDED_BY_THE_ENGINE = 5 WHERE PROJ_ID = 5005"));
            var rows = AssertEngineReadsWhatAceReads(path, "MSP_PROJECTS");
            Assert.Equal(before + 41, rows.Count);

            var compacted = Compacted(path);
            AssertSame(Written(rows), Written(AceTable(compacted, "MSP_PROJECTS")), "MSP_PROJECTS after DAO compacted the file");
            Same(DaoIndexes(path, "MSP_PROJECTS"), DaoIndexes(compacted, "MSP_PROJECTS"));
        }
    }
}
