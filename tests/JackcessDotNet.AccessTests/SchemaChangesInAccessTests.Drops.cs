using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// What the engine drops - an index, a column, a relationship, a table (#37) - beside
    /// the same thing dropped by Access in a twin file. Java Jackcess drops none of them, so Access is the only
    /// referee: DAO lists the same fields, indexes and relationships in both files, the definitions hold the same
    /// index slots and the system tables the same rows; then ACE goes on writing to the engine's file - into the
    /// pages the engine gave back, which Access hands out again - answers through the indexes that are left as a
    /// scan answers, and DAO compacts the file. What Access refuses to drop, the engine refuses, leaving its file
    /// as it was.
    /// </summary>
    public partial class SchemaChangesInAccessTests
    {
        // ── DAO ──────────────────────────────────────────────────────────────

        /// <summary>The database's own tables as DAO lists them, in name order.</summary>
        private static List<string> DaoTables(string path)
        {
            return WithDao(path, db =>
            {
                var tables = new List<string>();
                foreach (dynamic table in db.TableDefs)
                    if (!((string)table.Name).StartsWith("MSys", StringComparison.Ordinal)) tables.Add((string)table.Name);
                tables.Sort(StringComparer.Ordinal);
                return tables;
            });
        }

        /// <summary>DAO gives a field, or with no field the table, a Description: a property Access keeps in the catalog.</summary>
        private static void DaoDescribe(string path, string table, string field, string text)
        {
            WithDao(path, db =>
            {
                dynamic owner = field == null ? db.TableDefs[table] : db.TableDefs[table].Fields[field];
                owner.Properties.Append(owner.CreateProperty("Description", 10 /* dbText */, text));
                return 0;
            });
        }

        /// <summary>The Description DAO reads of a field, or with no field of the table; null when it has none.</summary>
        private static string DaoDescription(string path, string table, string field)
        {
            return WithDao(path, db =>
            {
                dynamic owner = field == null ? db.TableDefs[table] : db.TableDefs[table].Fields[field];
                try { return (string)owner.Properties["Description"].Value; }
                catch (Exception) { return null; }
            });
        }

        /// <summary>What DAO makes of <paramref name="change"/>: "made", or that it was refused.</summary>
        private static string DaoDoes(string path, Action<dynamic> change)
        {
            try { WithDao(path, db => { change(db); return 0; }); return "made"; }
            catch (Exception e) { return "refused: " + e.GetBaseException().Message; }
        }

        /// <summary>
        /// G (Id, K, T, M, X, D, Y), keyed by Id, with an index on T and sixty rows whose M are long values: ACE's or
        /// the engine's.
        /// </summary>
        private string Things(bool general, bool accessMade)
        {
            var path = NewDatabase(general, accessMade);
            if (accessMade)
            {
                Execute(path,
                    "CREATE TABLE G (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, T TEXT(20), M MEMO, X LONG, D DOUBLE, Y YESNO)",
                    "CREATE INDEX ixT ON G (T)");
                for (var i = 1; i <= 60; i++) AceInsert(path, "G", Thing(i, "made"));
                return path;
            }

            using (var db = Database.Open(path))
            {
                var g = db.CreateTable("G", new[]
                {
                    Long("Id"), Long("K"), Text("T", 20), new ColumnDefinition("M", DataType.Memo), Long("X"),
                    new ColumnDefinition("D", DataType.Double), new ColumnDefinition("Y", DataType.Boolean),
                }, new[] { "Id" }, new[] { new IndexDefinition("ixT", "T") });
                for (var i = 1; i <= 60; i++) g.Insert(EngineRow(Thing(i, "made")));
            }
            return path;
        }

        private static Values Thing(int id, string by)
        {
            return V("Id", id, "K", id % 5 + 1, "T", by + id, "M", new string((char)('a' + id % 26), 300 + id * 90), "X", id * 10, "D", id / 4.0, "Y", id % 2 == 0);
        }

        private static Values Without(Values row, params string[] columns)
        {
            var left = new Values();
            foreach (var pair in row)
                if (!columns.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) left[pair.Key] = pair.Value;
            return left;
        }

        // ── An index ─────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Kinds))]
        public void an_index_the_engine_drops_is_gone_as_the_one_ace_drops_and_ace_writes_on(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            foreach (var file in new[] { path, twin })
                Execute(file, "CREATE INDEX ixT ON C (T)", "CREATE INDEX ixK ON C (K)", "CREATE UNIQUE INDEX uxKT ON C (K, T)", "CREATE INDEX ixT2 ON C (T)");

            // ixT and ixT2 are on the one column, as Access lets two indexes be: one goes, the other answers.
            Execute(twin, "DROP INDEX ixT ON C");
            using (var db = Database.Open(path)) db.GetTable("C").DropIndex("ixT");
            Same(DaoIndexes(twin, "C"), DaoIndexes(path, "C"));
            Same(Slots(twin, "C"), Slots(path, "C"));
            Assert.Equal(17, DaoSeek(path, "C", "ixT2", "c17"));

            // ixK has a tree of its own, which goes with it: the blocks after it take its number.
            Execute(twin, "DROP INDEX ixK ON C");
            using (var db = Database.Open(path)) db.GetTable("C").DropIndex("ixK");
            Same(DaoIndexes(twin, "C"), DaoIndexes(path, "C"));
            Same(Slots(twin, "C"), Slots(path, "C"));
            Assert.Equal(17, DaoSeek(path, "C", "uxKT", 3, "c17"));
            Assert.StartsWith("refused", DaoDoes(path, db => { dynamic rows = db.OpenRecordset("C", 1); try { rows.Index = "ixK"; } finally { rows.Close(); } }));

            // ACE writes on, into the trees that are left - and into the pages the engine gave back.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 61; i <= 1500; i++)
                    using (var command = new OleDbCommand("INSERT INTO C (Id, K, T) VALUES (" + i + ", " + (i % 5 + 1) + ", 'c" + i + "')", connection))
                        command.ExecuteNonQuery();
            }
            Assert.Equal("refused: duplicate", Ace(path, "INSERT INTO C (Id, K, T) VALUES (5000, 3, 'C17')"));
            Assert.Equal(1417, DaoSeek(path, "C", "ixT2", "c1417"));
            Assert.Equal(1417, DaoSeek(path, "C", "uxKT", 3, "c1417"));
            foreach (var literal in new[] { "'c17'", "'c1417'", "'nothing'" })
                Assert.Equal(Count(path, "C", "T & '' = " + literal), Count(path, "C", "T = " + literal));
            AssertEngineReadsWhatAceReads(path, "C");
            AssertEngineReadsWhatAceReads(path, "P");
            AssertEngineReadsWhatAceReads(path, "U");

            // The primary key goes like any other index, and the table then takes a second row of an Id.
            var key = accessMade ? "pk" : "PrimaryKey";
            Execute(twin, "DROP INDEX " + key + " ON C");
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                c.DropIndex(key);
                c.Insert(new Row { ["Id"] = 17, ["K"] = 1, ["T"] = "a second 17" });
            }
            Same(DaoIndexes(twin, "C"), DaoIndexes(path, "C"));
            Same(Slots(twin, "C"), Slots(path, "C"));
            Assert.Equal("made", Ace(path, "INSERT INTO C (Id, K, T) VALUES (17, 2, 'a third 17')"));
            Assert.Equal(3, Count(path, "C", "Id = 17"));

            // And the engine goes on keeping what is left.
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                c.Insert(new Row { ["Id"] = 6000, ["K"] = 2, ["T"] = "engine row" });
                Assert.Throws<ConstraintViolationException>(() => c.Insert(new Row { ["Id"] = 6001, ["K"] = 2, ["T"] = "ENGINE ROW" }));
                c.AddIndex(new IndexDefinition("ixK", "K"));
            }
            Assert.Equal(6000, DaoSeek(path, "C", "ixT2", "engine row"));
            Assert.NotNull(DaoSeek(path, "C", "ixK", 4));
            for (var k = 1; k <= 5; k++)
                Assert.Equal(Count(path, "C", "K + 0 = " + k), Count(path, "C", "K = " + k));

            var compacted = Compacted(path);
            Same(DaoIndexes(path, "C"), DaoIndexes(compacted, "C"));
            Same(Rows(path, "C"), Rows(compacted, "C"));
            Assert.Equal(1503, Count(compacted, "C", "Id > 0"));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void an_index_dao_does_not_drop_is_not_dropped_by_the_engine_with_the_file_as_it_was(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            foreach (var file in new[] { path, twin })
            {
                Execute(file, "CREATE INDEX ixK ON C (K)");
                DaoRelate(file, "PC", "P", "K", "C", "K", 0);
            }
            var referenced = Slots(path, "P").Single(slot => slot.Contains(" type 2 ")).Split(':')[0];

            // The relationship's own index on each table, and the key it refers to P's rows by.
            Assert.StartsWith("refused", Ace(twin, "DROP INDEX PC ON C"));
            Assert.StartsWith("refused", Ace(twin, "DROP INDEX pk ON P"));
            Assert.StartsWith("refused", Ace(twin, "DROP INDEX nothing ON C"));
            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                Assert.Throws<InvalidOperationException>(() => db.GetTable("C").DropIndex("PC"));
                Assert.Throws<InvalidOperationException>(() => db.GetTable("P").DropIndex(referenced));
                Assert.Throws<InvalidOperationException>(() => db.GetTable("P").DropIndex(db.GetTable("P").Indexes.Single(ix => ix.IsPrimaryKey).Name));
                Assert.Throws<ArgumentException>(() => db.GetTable("C").DropIndex("nothing"));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            // The index whose tree the relationship's shares is dropped by both: the tree stays, and the relationship is kept.
            Execute(twin, "DROP INDEX ixK ON C");
            using (var db = Database.Open(path)) db.GetTable("C").DropIndex("ixK");
            foreach (var table in new[] { "P", "C" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }
            Assert.Equal("refused: no parent", Ace(path, "INSERT INTO C (Id, K, T) VALUES (900, 99, 'orphan')"));
            Assert.Equal("made", Ace(path, "INSERT INTO C (Id, K, T) VALUES (901, 2, 'child of 2')"));
            Assert.NotNull(DaoSeek(path, "C", "PC", 2));
            Same(DaoRelations(twin), DaoRelations(Compacted(path)));
        }

        // ── A column ─────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Kinds))]
        public void columns_the_engine_drops_are_gone_as_the_ones_ace_drops_and_both_write_on(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = Things(general, accessMade);
            var twin = Things(general, accessMade);
            foreach (var file in new[] { path, twin })
            {
                DaoDescribe(file, "G", null, "the table");
                foreach (var field in new[] { "X", "D", "M", "T" }) DaoDescribe(file, "G", field, "about " + field);
            }

            // A fixed-length column, a long-value one and a Yes/No: the rows are not rewritten by either.
            Execute(twin, "ALTER TABLE G DROP COLUMN X", "ALTER TABLE G DROP COLUMN M", "ALTER TABLE G DROP COLUMN Y");
            using (var db = Database.Open(path))
            {
                var g = db.GetTable("G");
                g.DropColumn("X");
                g.DropColumn("M");
                g.DropColumn("Y");
                Assert.Equal(new[] { "Id", "K", "T", "D" }, g.Columns.Select(c => c.Name));
            }
            Same(DaoFields(twin, "G"), DaoFields(path, "G"));
            Assert.Equal(new[] { "Id:4", "K:4", "T:10", "D:7" }, DaoFields(path, "G"));
            Same(Rows(twin, "G"), Rows(path, "G"));
            Same(DaoIndexes(twin, "G"), DaoIndexes(path, "G"));

            // The properties of the columns that are left are theirs still; the dropped columns' are gone.
            Assert.Equal("the table", DaoDescription(path, "G", null));
            Assert.Equal("about D", DaoDescription(path, "G", "D"));
            Assert.Equal("about T", DaoDescription(path, "G", "T"));

            // A column in an index is refused by both; with the index gone, it goes.
            Assert.StartsWith("refused", Ace(twin, "ALTER TABLE G DROP COLUMN T"));
            Assert.StartsWith("refused", Ace(twin, "ALTER TABLE G DROP COLUMN Id"));
            Assert.StartsWith("refused", Ace(twin, "ALTER TABLE G DROP COLUMN Nothing"));
            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                Assert.Throws<InvalidOperationException>(() => db.GetTable("G").DropColumn("T"));
                Assert.Throws<InvalidOperationException>(() => db.GetTable("G").DropColumn("Id"));
                Assert.Throws<ArgumentException>(() => db.GetTable("G").DropColumn("Nothing"));
            }
            Assert.Equal(before, File.ReadAllBytes(path));
            Execute(twin, "DROP INDEX ixT ON G", "ALTER TABLE G DROP COLUMN T");
            using (var db = Database.Open(path))
            {
                var g = db.GetTable("G");
                g.DropIndex("ixT");
                g.DropColumn("T");
            }
            Same(DaoFields(twin, "G"), DaoFields(path, "G"));
            Same(Rows(twin, "G"), Rows(path, "G"));
            Same(Slots(twin, "G"), Slots(path, "G"));
            Assert.Equal("about D", DaoDescription(path, "G", "D"));

            // Both go on writing the table, by the columns that are left, into a file with pages given back.
            var subject = new Subject(path, "G", "Id");
            for (var i = 61; i <= 90; i++) subject.AceInserts(Without(Thing(i, "ace"), "T", "M", "X", "Y"));
            for (var i = 91; i <= 120; i++) subject.EngineInserts(Without(Thing(i, "engine"), "T", "M", "X", "Y"));
            subject.AceUpdates(2, V("K", 222, "D", 2.5));
            subject.EngineUpdates(3, V("K", 333, "D", 3.5));
            subject.AceDeletes(4, 62, 92);
            subject.EngineDeletes(5, 63, 93);
            subject.Check("after columns were dropped by the engine and both wrote on");

            // Columns added afterwards, by the engine and by ACE, read null in the rows that were there.
            using (var db = Database.Open(path))
            {
                var g = db.GetTable("G");
                g.AddColumn(new ColumnDefinition("M2", DataType.Memo));
                g.AddColumn(Long("X2"));
                g.Insert(new Row { ["Id"] = 200, ["K"] = 1, ["M2"] = new string('n', 6000), ["X2"] = 20 });
            }
            Execute(path, "ALTER TABLE G ADD COLUMN T2 TEXT(20)", "CREATE INDEX ixT2 ON G (T2)", "INSERT INTO G (Id, K, T2, M2, X2) VALUES (201, 2, 'ace', 'memo by ace', 21)");
            Assert.Equal(new[] { "Id:4", "K:4", "D:7", "M2:12", "X2:4", "T2:10" }, DaoFields(path, "G"));
            Assert.Equal(2, Count(path, "G", "X2 IS NOT NULL"));
            Assert.Equal(201, DaoSeek(path, "G", "ixT2", "ace"));
            using (var db = Database.Open(path))
                db.GetTable("G").Insert(new Row { ["Id"] = 202, ["T2"] = "engine", ["M2"] = "memo by the engine" });
            Assert.Equal(202, DaoSeek(path, "G", "ixT2", "engine"));
            var rows = AssertEngineReadsWhatAceReads(path, "G");

            var compacted = Compacted(path);
            Assert.Equal(DaoFields(path, "G"), DaoFields(compacted, "G"));
            AssertSame(Written(rows), Written(AceTable(compacted, "G")), "G after DAO compacted the file");
            Assert.Equal("about D", DaoDescription(compacted, "G", "D"));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_column_of_a_relationship_is_not_dropped_by_dao_or_by_the_engine(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            foreach (var file in new[] { path, twin })
            {
                DaoRelate(file, "PC", "P", "K", "C", "K", 0);
                DaoRelate(file, "Loose", "P", "T", "C", "T", DontEnforce);
            }

            foreach (var sql in new[] { "ALTER TABLE C DROP COLUMN K", "ALTER TABLE P DROP COLUMN K", "ALTER TABLE C DROP COLUMN T", "ALTER TABLE P DROP COLUMN T" })
                Assert.Contains("relationship", Ace(twin, sql));
            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                Assert.Throws<InvalidOperationException>(() => db.GetTable("C").DropColumn("K"));
                Assert.Throws<InvalidOperationException>(() => db.GetTable("P").DropColumn("K"));
                Assert.Throws<InvalidOperationException>(() => db.GetTable("C").DropColumn("T"));
                Assert.Throws<InvalidOperationException>(() => db.GetTable("P").DropColumn("T"));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            // With the relationship gone, both drop it.
            Assert.Equal("made", DaoDoes(twin, db => db.Relations.Delete("Loose")));
            Execute(twin, "ALTER TABLE C DROP COLUMN T");
            using (var db = Database.Open(path))
            {
                db.DropRelationship("Loose");
                db.GetTable("C").DropColumn("T");
            }
            Same(DaoFields(twin, "C"), DaoFields(path, "C"));
            Same(DaoRelations(twin), DaoRelations(path));
            Same(Rows(twin, "C"), Rows(path, "C"));
        }

        // A dropped column keeps its number until the file is compacted, and a table has 255 numbers: Access refuses
        // a column for a table that has had 255, however few it has ("Too many fields defined"), and so does the
        // engine, at the same column. DAO's compacting numbers the columns afresh, and both then add again.
        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_table_that_has_had_255_columns_takes_no_more_from_ace_or_from_the_engine_until_dao_compacts_it(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);

            // C has three columns: 252 more come and go, by ACE in one file and by the engine in the other.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(twin)))
            {
                connection.Open();
                for (var i = 0; i < 252; i++)
                    foreach (var sql in new[] { "ALTER TABLE C ADD COLUMN X" + i + (i % 2 == 0 ? " LONG" : " TEXT(20)"), "ALTER TABLE C DROP COLUMN X" + i })
                        using (var command = new OleDbCommand(sql, connection))
                            command.ExecuteNonQuery();
            }
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                for (var i = 0; i < 252; i++)
                {
                    c.AddColumn(i % 2 == 0 ? Long("X" + i) : Text("X" + i, 20));
                    c.DropColumn("X" + i);
                }
            }
            Same(DaoFields(twin, "C"), DaoFields(path, "C"));
            Same(Rows(twin, "C"), Rows(path, "C"));

            // The 253rd is refused by both, in either file, and nothing of the engine's file changes.
            foreach (var file in new[] { twin, path })
                foreach (var type in new[] { "LONG", "TEXT(20)", "YESNO" })
                    Assert.Contains("Too many fields defined", Ace(file, "ALTER TABLE C ADD COLUMN OneTooMany " + type));
            var before = File.ReadAllBytes(path);
            foreach (var file in new[] { twin, path })
                using (var db = Database.Open(file))
                {
                    var refused = Assert.Throws<ArgumentException>(() => db.GetTable("C").AddColumn(Long("OneTooMany")));
                    Assert.Contains("has had 255 columns", refused.Message);
                }
            Assert.Equal(before, File.ReadAllBytes(path));

            // The table is written and read as before.
            Assert.Equal("made", Ace(path, "INSERT INTO C (Id, K, T) VALUES (900, 2, 'by ace')"));
            using (var db = Database.Open(path))
                db.GetTable("C").Insert(new Row { ["Id"] = 901, ["K"] = 3, ["T"] = "by the engine" });
            Assert.Equal(901, Convert.ToInt32(Scalar(path, "SELECT Id FROM C WHERE T = 'by the engine'")));
            var rows = AssertEngineReadsWhatAceReads(path, "C");

            // Compacted, the engine's file takes columns again, from the engine and from ACE.
            var compacted = Compacted(path);
            using (var db = Database.Open(compacted))
            {
                var c = db.GetTable("C");
                c.AddColumn(Long("AfterCompacting"));
                c.Insert(new Row { ["Id"] = 902, ["K"] = 4, ["T"] = "after", ["AfterCompacting"] = 9 });
            }
            Assert.Equal("made", Ace(compacted, "ALTER TABLE C ADD COLUMN ByAce LONG"));
            Assert.Equal(new[] { "Id:4", "K:4", "T:10", "AfterCompacting:4", "ByAce:4" }, DaoFields(compacted, "C"));
            Assert.Equal(rows.Count + 1, Count(compacted, "C", "Id > 0"));
            Assert.Equal(1, Count(compacted, "C", "AfterCompacting = 9"));
            AssertEngineReadsWhatAceReads(compacted, "C");
        }

        // An index is given the lowest number no index of its table has: the number a dropped index left. The engine
        // gave one past the highest, so its numbers - and the name of a relationship's index on the referenced
        // table, which is made of its number - parted from Access's once an index had been dropped.
        [Theory]
        [MemberData(nameof(Kinds))]
        public void an_index_takes_the_number_a_dropped_index_left_as_ace_gives_it(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            foreach (var file in new[] { path, twin })
                Execute(file, "ALTER TABLE P ADD COLUMN B LONG", "ALTER TABLE P ADD COLUMN Y LONG",
                    "CREATE INDEX ixA ON P (T)", "CREATE INDEX ixB ON P (B)", "CREATE INDEX ixC ON P (Y)");

            Execute(twin, "DROP INDEX ixA ON P", "CREATE INDEX ixD ON P (T)", "DROP INDEX ixB ON P");
            DaoRelate(twin, "PC", "P", "K", "C", "K", 0);
            using (var db = Database.Open(path))
            {
                var p = db.GetTable("P");
                p.DropIndex("ixA");
                p.AddIndex(new IndexDefinition("ixD", "T"));
                p.DropIndex("ixB");
                db.CreateRelationship(new RelationshipDefinition("P", "K", "C", "K") { Name = "PC", HasReferentialIntegrity = true });
            }
            foreach (var table in new[] { "P", "C" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }
            Same(DaoRelations(twin), DaoRelations(path));
            foreach (var sql in new[] { "INSERT INTO C (Id, K, T) VALUES (900, 99, 'orphan')", "INSERT INTO C (Id, K, T) VALUES (901, 2, 'child')", "DELETE FROM P WHERE K = 2" })
                Assert.Equal(Ace(twin, sql), Ace(path, sql));
            Same(DaoRelations(twin), DaoRelations(Compacted(path)));
        }

        // ── A relationship ───────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Relationships))]
        public void a_relationship_the_engine_drops_is_gone_as_the_one_dao_drops(bool general, bool accessMade, int attributes)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            var enforced = (attributes & DontEnforce) == 0;

            // Two relationships, made by DAO in both files: PC, whose index on C is built for it, and PU, one to
            // one, whose index on U shares the tree of U's unique index. PC goes; PU stays.
            foreach (var file in new[] { path, twin })
            {
                DaoRelate(file, "PC", "P", "K", "C", "K", attributes);
                DaoRelate(file, "PU", "P", "K", "U", "K", 1 /* dbRelationUnique */);
            }

            Assert.Equal("made", DaoDoes(twin, db => db.Relations.Delete("PC")));
            using (var db = Database.Open(path))
            {
                db.DropRelationship("PC");
                Assert.Equal("PU", db.GetRelationships().Single().Name);
            }

            Same(DaoRelations(twin), DaoRelations(path));
            Assert.Equal(new[] { "PU: P -> U attributes 1 fields K>K" }, DaoRelations(path));
            Same(RelationshipRows(twin), RelationshipRows(path));
            Same(CatalogRelationships(twin), CatalogRelationships(path));
            foreach (var table in new[] { "P", "C", "U" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }
            Assert.DoesNotContain(Slots(path, "C"), slot => slot.Contains(" type 2 "));

            // Nothing keeps C's rows to P's any more, in either file; PU is kept in both.
            foreach (var sql in new[]
            {
                "INSERT INTO C (Id, K, T) VALUES (900, 99, 'orphan')", "UPDATE C SET K = 77 WHERE Id = 1",
                "INSERT INTO U (Id, K) VALUES (4, 9)", "INSERT INTO U (Id, K) VALUES (5, 4)", "DELETE FROM P WHERE K = 1", "DELETE FROM P WHERE K = 5",
            })
            {
                Assert.Equal(Ace(twin, sql), Ace(path, sql));
                Same(Rows(twin, "P", "C", "U"), Rows(path, "P", "C", "U"));
            }
            Assert.Equal("made", Ace(path, "INSERT INTO C (Id, K, T) VALUES (901, 98, 'orphan')"));
            using (var db = Database.Open(path))
            {
                db.GetTable("C").Insert(new Row { ["Id"] = 902, ["K"] = 97, ["T"] = "orphan" });
                Assert.Throws<ConstraintViolationException>(() => db.GetTable("U").Insert(new Row { ["Id"] = 6, ["K"] = 96 }));
            }
            AssertEngineReadsWhatAceReads(path, "C");

            // And one of the name can be made again, by DAO, once the rows allow it.
            Execute(path, "DELETE FROM C WHERE K NOT IN (SELECT K FROM P)");
            DaoRelate(path, "PC", "P", "K", "C", "K", attributes);
            Assert.Equal(enforced ? "refused: no parent" : "made", Ace(path, "INSERT INTO C (Id, K, T) VALUES (903, 95, 'orphan')"));

            var compacted = Compacted(path);
            Same(DaoRelations(path), DaoRelations(compacted));
            Same(Rows(path, "P", "C", "U"), Rows(compacted, "P", "C", "U"));
        }

        // The other way about: a relationship the engine made is one DAO can drop, leaving what a file that never had it holds.
        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_relationship_the_engine_creates_is_one_dao_drops_and_the_engine_drops_one_of_a_table_to_itself(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            using (var db = Database.Open(path))
                db.CreateRelationship(Definition(UpdateCascade | DeleteCascade));
            Assert.Equal("made", DaoDoes(path, db => db.Relations.Delete("PC")));

            Assert.Empty(DaoRelations(path));
            Same(RelationshipRows(twin), RelationshipRows(path));
            Same(CatalogRelationships(twin), CatalogRelationships(path));
            foreach (var table in new[] { "P", "C" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }
            using (var db = Database.Open(path))
            {
                Assert.Empty(db.GetRelationships());
                db.GetTable("C").Insert(new Row { ["Id"] = 900, ["K"] = 99, ["T"] = "orphan" });
            }
            AssertEngineReadsWhatAceReads(path, "C");

            // A table related to itself has both of the relationship's indexes: both go.
            var staff = Staff(general, accessMade);
            var staffTwin = Staff(general, accessMade);
            foreach (var file in new[] { staff, staffTwin })
                DaoRelate(file, "Reports", "S", "Id", "S", "M", UpdateCascade);
            Assert.Equal("made", DaoDoes(staffTwin, db => db.Relations.Delete("Reports")));
            using (var db = Database.Open(staff)) db.DropRelationship("Reports");
            Same(DaoRelations(staffTwin), DaoRelations(staff));
            Same(RelationshipRows(staffTwin), RelationshipRows(staff));
            Same(DaoIndexes(staffTwin, "S"), DaoIndexes(staff, "S"));
            Same(Slots(staffTwin, "S"), Slots(staff, "S"));
            Assert.Equal("made", Ace(staff, "INSERT INTO S (Id, M) VALUES (4, 9)"));
            Same(DaoIndexes(staffTwin, "S"), DaoIndexes(Compacted(staff), "S"));
        }

        // ── A table ──────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_table_the_engine_drops_is_gone_as_the_one_ace_drops_and_ace_uses_its_pages_again(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            foreach (var file in new[] { path, twin })
            {
                Execute(file, "ALTER TABLE C ADD COLUMN M MEMO", "CREATE INDEX ixT ON C (T)", "UPDATE C SET M = String(3000, 'm') & T");
                DaoRelate(file, "PC", "P", "K", "C", "K", UpdateCascade | DeleteCascade);
                DaoRelate(file, "Loose", "P", "T", "C", "T", DontEnforce);
            }

            // The table others refer to is refused by both; nothing changes.
            Assert.StartsWith("refused", Ace(twin, "DROP TABLE P"));
            Assert.StartsWith("refused", Ace(twin, "DROP TABLE Nothing"));
            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                Assert.Throws<InvalidOperationException>(() => db.DropTable("P"));
                Assert.Throws<ArgumentException>(() => db.DropTable("Nothing"));
                Assert.Throws<NotSupportedException>(() => db.DropTable("MSysObjects"));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            // The table that refers goes, and takes its relationships with it.
            var size = new FileInfo(path).Length;
            Execute(twin, "DROP TABLE C");
            using (var db = Database.Open(path))
            {
                db.DropTable("C");
                Assert.Equal(new[] { "P", "U" }, db.GetTableNames().OrderBy(n => n));
            }
            Assert.Equal(size, new FileInfo(path).Length);
            Same(DaoTables(twin), DaoTables(path));
            Assert.Equal(new[] { "P", "U" }, DaoTables(path));
            Assert.Empty(DaoRelations(twin));
            Assert.Empty(DaoRelations(path));
            Same(RelationshipRows(twin), RelationshipRows(path));
            Same(CatalogRelationships(twin), CatalogRelationships(path));
            foreach (var table in new[] { "P", "U" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
                Same(Rows(twin, table), Rows(path, table));
            }
            Assert.StartsWith("refused", Ace(path, "SELECT COUNT(*) FROM C"));

            // ACE makes a table of the name again and fills it: in the pages the engine gave back, so the file does
            // not grow until they are used up. P and U are as they were.
            Execute(path, "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, T TEXT(20), M MEMO)", "CREATE INDEX ixK ON C (K)");
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 1; i <= 40; i++)
                    using (var command = new OleDbCommand("INSERT INTO C (Id, K, T, M) VALUES (" + i + ", " + (i % 5 + 1) + ", 'again " + i + "', String(3000, 'n'))", connection))
                        command.ExecuteNonQuery();
            }
            Assert.True(new FileInfo(path).Length <= size + 8 * 4096, "ACE did not use the pages the engine gave back: the file grew from " + size + " to " + new FileInfo(path).Length);
            Assert.Equal(40, Count(path, "C", "Id > 0"));
            foreach (var table in new[] { "P", "U" })
                Same(Rows(twin, table), Rows(path, table));
            foreach (var table in new[] { "C", "P", "U" })
                AssertEngineReadsWhatAceReads(path, table);

            // The engine goes on too: rows for ACE's table, a table of its own, and a relationship between them.
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                for (var i = 41; i <= 80; i++) c.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1, ["T"] = "engine " + i, ["M"] = new string('e', 3000) });
                var n = db.CreateTable("N", new[] { Long("Id"), Text("T", 20) }, primaryKey: "Id");
                for (var i = 1; i <= 200; i++) n.Insert(new Row { ["Id"] = i, ["T"] = "n" + i });
                db.CreateRelationship(new RelationshipDefinition("P", "K", "C", "K") { Name = "PC", HasReferentialIntegrity = true });
            }
            Assert.Equal(80, Count(path, "C", "Id > 0"));
            Assert.Equal(200, Count(path, "N", "Id > 0"));
            Assert.Equal("refused: no parent", Ace(path, "INSERT INTO C (Id, K, T) VALUES (900, 99, 'orphan')"));
            for (var k = 1; k <= 5; k++)
                Assert.Equal(Count(path, "C", "K + 0 = " + k), Count(path, "C", "K = " + k));
            foreach (var table in new[] { "C", "N", "P", "U" })
                AssertEngineReadsWhatAceReads(path, table);

            var compacted = Compacted(path);
            Assert.Equal(new[] { "C", "N", "P", "U" }, DaoTables(compacted));
            Same(Rows(path, "C", "N", "P", "U"), Rows(compacted, "C", "N", "P", "U"));
            Same(DaoRelations(path), DaoRelations(compacted));
        }

        // A table whose definition is on several pages, and one related to itself: all of each goes.
        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_wide_table_and_a_table_related_to_itself_are_dropped_as_ace_drops_them(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = Wide(general, accessMade);
            var twin = Wide(general, accessMade);
            Execute(twin, "DROP TABLE W");
            using (var db = Database.Open(path)) db.DropTable("W");
            Same(DaoTables(twin), DaoTables(path));
            Assert.Empty(DaoTables(path));
            Execute(path, "CREATE TABLE W (Id LONG CONSTRAINT pk PRIMARY KEY, T TEXT(20))", "INSERT INTO W (Id, T) VALUES (1, 'again')");
            AssertEngineReadsWhatAceReads(path, "W");
            Assert.Equal(new[] { "W" }, DaoTables(Compacted(path)));

            var staff = Staff(general, accessMade);
            var staffTwin = Staff(general, accessMade);
            foreach (var file in new[] { staff, staffTwin })
                DaoRelate(file, "Reports", "S", "Id", "S", "M", UpdateCascade);
            Execute(staffTwin, "DROP TABLE S");
            using (var db = Database.Open(staff)) db.DropTable("S");
            Same(DaoTables(staffTwin), DaoTables(staff));
            Assert.Empty(DaoRelations(staff));
            Same(RelationshipRows(staffTwin), RelationshipRows(staff));
            Same(CatalogRelationships(staffTwin), CatalogRelationships(staff));
            Assert.Empty(DaoTables(Compacted(staff)));
        }
    }
}
