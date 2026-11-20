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
    /// A primary key the engine gives a table that has rows and no key (#37), beside the
    /// same key ACE gives the same table in a twin file: DAO lists the same indexes, the two definitions hold the
    /// same slots, DAO seeks through the key, ACE adds to it and refuses a duplicate and a null in it, a
    /// relationship DAO makes refers to it, and it is the table's key after DAO compacts the file. A key the rows
    /// do not allow is refused by both, the engine leaving its file as it was.
    /// </summary>
    public partial class SchemaChangesInAccessTests
    {
        /// <summary>
        /// L (Id, K, T) with no primary key, an index on K, and sixty rows; and D (Id, L), keyed by Id, with none.
        /// ACE's or the engine's.
        /// </summary>
        private string Unkeyed(bool general, bool accessMade)
        {
            var path = NewDatabase(general, accessMade);
            if (accessMade)
            {
                var statements = new List<string>
                {
                    "CREATE TABLE L (Id LONG, K LONG, T TEXT(20))",
                    "CREATE INDEX ixK ON L (K)",
                    "CREATE TABLE D (Id LONG CONSTRAINT pk PRIMARY KEY, L LONG)",
                };
                for (var i = 1; i <= 60; i++) statements.Add("INSERT INTO L (Id, K, T) VALUES (" + i + ", " + (i % 5 + 1) + ", 'l" + i + "')");
                Execute(path, statements.ToArray());
                return path;
            }

            using (var db = Database.Open(path))
            {
                var l = db.CreateTable("L", new[] { Long("Id"), Long("K"), Text("T", 20) }, null, new[] { new IndexDefinition("ixK", "K") });
                db.CreateTable("D", new[] { Long("Id"), Long("L") }, primaryKey: "Id");
                for (var i = 1; i <= 60; i++) l.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1, ["T"] = "l" + i });
            }
            return path;
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_primary_key_the_engine_adds_is_the_one_ace_adds_and_ace_keeps_it(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = Unkeyed(general, accessMade);
            var twin = Unkeyed(general, accessMade);

            Execute(twin, "ALTER TABLE L ADD CONSTRAINT PrimaryKey PRIMARY KEY (Id)");
            using (var db = Database.Open(path))
            {
                var key = db.GetTable("L").AddIndex(new IndexDefinition("PrimaryKey", "Id") { IsPrimaryKey = true });
                Assert.True(key.IsPrimaryKey);
            }

            // The same key, to DAO and in the definitions.
            Same(DaoIndexes(twin, "L"), DaoIndexes(path, "L"));
            Assert.Contains(DaoIndexes(path, "L"), ix => ix.StartsWith("PrimaryKey: unique=True primary=True "));
            Same(Slots(twin, "L"), Slots(path, "L"));
            Assert.Contains(Slots(path, "L"), slot => slot.StartsWith("PrimaryKey: ") && slot.Contains(" flags 0x89 type 1 "));

            // A seek is answered by the key's tree alone.
            Assert.Equal(17, DaoSeek(path, "L", "PrimaryKey", 17));
            Assert.Null(DaoSeek(path, "L", "PrimaryKey", 61));

            // ACE writes into the tree the engine built, and keeps it a key.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 61; i <= 900; i++)
                    using (var command = new OleDbCommand("INSERT INTO L (Id, K, T) VALUES (" + i + ", " + (i % 5 + 1) + ", 'l" + i + "')", connection))
                        command.ExecuteNonQuery();
            }
            foreach (var sql in new[] { "INSERT INTO L (Id, K, T) VALUES (17, 1, 'twice')", "INSERT INTO L (K, T) VALUES (1, 'no id')", "UPDATE L SET Id = 18 WHERE Id = 19" })
            {
                Assert.StartsWith("refused", Ace(twin, sql));
                Assert.Equal(Ace(twin, sql), Ace(path, sql));
            }
            Assert.Equal(617, DaoSeek(path, "L", "PrimaryKey", 617));
            foreach (var id in new[] { 17, 617, 900, 901 })
                Assert.Equal(Count(path, "L", "Id + 0 = " + id), Count(path, "L", "Id = " + id));

            // A relationship refers to it, made by DAO in the engine's file as in ACE's.
            foreach (var file in new[] { path, twin })
                DaoRelate(file, "LD", "L", "Id", "D", "L", 0);
            Same(DaoRelations(twin), DaoRelations(path));
            Assert.Equal("refused: no parent", Ace(path, "INSERT INTO D (Id, L) VALUES (1, 5000)"));
            Assert.Equal("made", Ace(path, "INSERT INTO D (Id, L) VALUES (2, 617)"));
            Assert.Equal("refused: has children", Ace(path, "DELETE FROM L WHERE Id = 617"));

            // The engine keeps it too.
            using (var db = Database.Open(path))
            {
                var l = db.GetTable("L");
                Assert.Equal("l617", l.FindRowByPrimaryKey(617)["T"]);
                l.Insert(new Row { ["Id"] = 6000, ["K"] = 2, ["T"] = "engine row" });
                Assert.Throws<ConstraintViolationException>(() => l.Insert(new Row { ["Id"] = 6000, ["K"] = 3, ["T"] = "twice" }));
                Assert.Throws<ConstraintViolationException>(() => l.Insert(new Row { ["K"] = 3, ["T"] = "no id" }));
                l.UpdateByPrimaryKey(618, new Row { ["T"] = "moved" });
                l.DeleteRow("Id", 619);
            }
            Assert.Equal(6000, DaoSeek(path, "L", "PrimaryKey", 6000));
            Assert.Null(DaoSeek(path, "L", "PrimaryKey", 619));
            Assert.Equal("moved", Scalar(path, "SELECT T FROM L WHERE Id = 618"));
            AssertEngineReadsWhatAceReads(path, "L");

            var compacted = Compacted(path);
            Same(DaoIndexes(path, "L"), DaoIndexes(compacted, "L"));
            Assert.Contains(DaoIndexes(compacted, "L"), ix => ix.StartsWith("PrimaryKey: unique=True primary=True "));
            Same(Rows(path, "L"), Rows(compacted, "L"));
            Assert.Equal(900, Count(compacted, "L", "Id > 0"));
            Assert.Equal("refused: duplicate", Ace(compacted, "INSERT INTO L (Id, K, T) VALUES (17, 1, 'twice')"));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_primary_key_of_two_columns_the_engine_adds_is_the_one_ace_adds(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = Unkeyed(general, accessMade);
            var twin = Unkeyed(general, accessMade);

            Execute(twin, "ALTER TABLE L ADD CONSTRAINT KeyOfTwo PRIMARY KEY (K, Id)");
            using (var db = Database.Open(path))
                db.GetTable("L").AddIndex(new IndexDefinition("KeyOfTwo", "K", "Id") { IsPrimaryKey = true });

            Same(DaoIndexes(twin, "L"), DaoIndexes(path, "L"));
            Same(Slots(twin, "L"), Slots(path, "L"));
            Assert.Equal(12, DaoSeek(path, "L", "KeyOfTwo", 3, 12));
            Assert.Null(DaoSeek(path, "L", "KeyOfTwo", 4, 12));
            foreach (var sql in new[] { "INSERT INTO L (Id, K, T) VALUES (12, 3, 'twice')", "INSERT INTO L (Id, K, T) VALUES (12, 4, 'once')", "INSERT INTO L (Id, T) VALUES (99, 'no k')" })
            {
                Assert.Equal(Ace(twin, sql), Ace(path, sql));
                Same(Rows(twin, "L"), Rows(path, "L"));
            }
            Same(DaoIndexes(twin, "L"), DaoIndexes(Compacted(path), "L"));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_primary_key_ace_would_refuse_for_the_rows_there_are_is_refused_by_the_engine_with_the_file_as_it_was(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = Unkeyed(general, accessMade);
            var twin = Unkeyed(general, accessMade);
            foreach (var file in new[] { path, twin })
                Execute(file, "INSERT INTO L (Id, K, T) VALUES (7, 9, 'a second 7')", "INSERT INTO L (K, T) VALUES (8, 'no id')");

            // Two rows with one Id, and a row with none; K, which many rows share; T and Id together, one row lacking Id.
            Assert.StartsWith("refused", Ace(twin, "ALTER TABLE L ADD CONSTRAINT PrimaryKey PRIMARY KEY (Id)"));
            Assert.StartsWith("refused", Ace(twin, "ALTER TABLE L ADD CONSTRAINT PrimaryKey PRIMARY KEY (K)"));
            Assert.StartsWith("refused", Ace(twin, "ALTER TABLE L ADD CONSTRAINT PrimaryKey PRIMARY KEY (T, Id)"));

            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                var l = db.GetTable("L");
                Assert.Throws<ConstraintViolationException>(() => l.AddIndex(new IndexDefinition("PrimaryKey", "Id") { IsPrimaryKey = true }));
                Assert.Throws<ConstraintViolationException>(() => l.AddIndex(new IndexDefinition("PrimaryKey", "K") { IsPrimaryKey = true }));
                Assert.Throws<ConstraintViolationException>(() => l.AddIndex(new IndexDefinition("PrimaryKey", "T", "Id") { IsPrimaryKey = true }));
            }
            Assert.Equal(before, File.ReadAllBytes(path));
            Same(DaoIndexes(twin, "L"), DaoIndexes(path, "L"));

            // And a second key, for a table that has one: D.
            Assert.StartsWith("refused", Ace(twin, "ALTER TABLE D ADD CONSTRAINT Another PRIMARY KEY (L)"));
            using (var db = Database.Open(path))
                Assert.Throws<ArgumentException>(() => db.GetTable("D").AddIndex(new IndexDefinition("Another", "L") { IsPrimaryKey = true }));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }
}
