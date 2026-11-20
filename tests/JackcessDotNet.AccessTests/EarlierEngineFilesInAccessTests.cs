using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using JackcessDotNet.Diagnostics;
using Xunit;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// What Access makes of things earlier releases of the managed engine left in files, and what the engine of
    /// today makes of them beside it.
    /// <para>
    /// <b>The engine trusts an index, as Access and Java Jackcess do</b>: with a file whose indexes do not
    /// hold every row it does what ACE does with that file, which is held here for both files below. A caller
    /// with such a file sets the engine's <see cref="Database.VerifyIndexes"/>, and the rest of what is said of
    /// the engine below is with it set.
    /// </para>
    /// <para>
    /// <b>Rows that are in no index but the primary key</b>. The engine's tests keep a database DAO made -
    /// Parent, and Followers, Movers and Holders, each with a relationship to it - that the engine at its commit
    /// 3ad3aed (a build from before the release that kept every index) then wrote to: it inserted Followers 5 (of parent 1) and Holders
    /// 5 (of parent 2), and changed Movers 30 from parent 4 to parent 3, keeping the primary keys and no other index.
    /// ACE keeps a relationship through the index on its foreign key and trusts it, so with that file it leaves
    /// orphans. Asked to verify its indexes, the engine checks an index before it trusts one, and comes to what
    /// ACE comes to once DAO has compacted the file, which rebuilds its indexes.
    /// </para>
    /// <para>
    /// <b>Rows that a unique index does not hold</b>. A second database of the same making has a row the
    /// engine at 3ad3aed inserted, a row whose indexed value it changed and a row whose primary key it changed. ACE
    /// trusts a unique index as it trusts any: it adds a second row with such a row's values, and DAO then cannot
    /// compact the file whole - the table loses the index, or its primary key. Asked to verify its indexes, the
    /// engine refuses the row, as ACE does once the file is compacted.
    /// </para>
    /// <para>
    /// <b>Entries that are no row's</b> (#36). The same database has an entry that points where a row was, and one
    /// under the value a row had before the earlier release changed it. ACE follows both, to a failed query and to
    /// the wrong row. The engine takes out an entry that points at no row when it changes an entry under its key -
    /// or, asked to verify its indexes, whenever it next changes the table - so that ACE finds the rows through the
    /// index afterwards. An entry that points at a row it believes, as ACE does, and refuses a row with its key;
    /// only verifying its indexes, with a key it makes as Access does, does it take the entry for the row's wrong
    /// one and move it to the row's own key.
    /// </para>
    /// <para>
    /// <b>Decimals in the byte order earlier releases wrote</b>. One that reads with more digits than its
    /// column holds is what the engine's <see cref="DecimalByteOrder.Find(Database)"/> lists. That rests on two
    /// things about Access, held here: it reads such a value as the engine does, and it writes none. A number
    /// that runs past 96 bits, which those bytes can make, Access reads as its low 96 bits, and so does the engine.
    /// </para>
    /// </summary>
    public class EarlierEngineFilesInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        // ── Rows an earlier release left out of an index ─────────────────────

        private static readonly string[] Tables = { "Parent", "Followers", "Movers", "Holders" };

        /// <summary>A copy of a database DAO made and the engine at 3ad3aed wrote to.</summary>
        private string EarlierEngineFile(string name = "engine-3ad3aed-rows-in-access-tables.mdb")
        {
            var assets = typeof(EarlierEngineFilesInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineAssetsDir").Value;
            var path = _tmp.NewMdbPath();
            File.Copy(Path.Combine(assets, name), path);
            return path;
        }

        /// <summary>A table's rows as "Id of K" ("K" alone for Parent), in order, read by ACE without a WHERE: no index is asked.</summary>
        private static List<string> Rows(string path, string table)
        {
            return table == "Parent"
                ? AccessReferee.AceRows(path, "SELECT K FROM Parent").Select(r => Convert.ToInt32(r["K"])).OrderBy(k => k)
                    .Select(k => k.ToString(CultureInfo.InvariantCulture)).ToList()
                : AccessReferee.AceRows(path, "SELECT Id, K FROM [" + table + "]").OrderBy(r => Convert.ToInt32(r["Id"]))
                    .Select(r => r["Id"] + " of " + (r["K"] ?? "none")).ToList();
        }

        private static bool AceRefuses(string path, string sql)
        {
            try { AccessReferee.Execute(path, sql); return false; }
            catch (OleDbException) { return true; }
        }

        /// <summary>
        /// Whether the engine, keeping relationships, refuses <paramref name="change"/> - checking each index before
        /// it trusts it when <paramref name="verifyIndexes"/>, and trusting it as Access does when not.
        /// </summary>
        private static bool EngineRefuses(string path, Action<Database> change, bool verifyIndexes = true)
        {
            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                db.VerifyIndexes = verifyIndexes;
                try { change(db); return false; }
                catch (InvalidOperationException) { return true; }
            }
        }

        [Fact]
        public void access_itself_does_not_find_the_rows_an_earlier_release_left_out_of_an_index()
        {
            var path = EarlierEngineFile();

            // Movers 30 is parent 3's, and the index has it under 4: the delete cascades to nothing.
            Assert.False(AceRefuses(path, "DELETE FROM Parent WHERE K = 3"));
            Assert.Equal(new[] { "30 of 3" }, Rows(path, "Movers"));

            // Holders 5 refers to parent 2 and is in no index: nothing forbids the delete.
            Assert.False(AceRefuses(path, "DELETE FROM Parent WHERE K = 2"));
            Assert.Contains("5 of 2", Rows(path, "Holders"));

            // Followers 5 is parent 1's and is in no index: the cascade passes it by.
            Assert.False(AceRefuses(path, "UPDATE Parent SET K = 9 WHERE K = 1"));
            Assert.Equal(new[] { "5 of 1", "10 of 9", "11 of 9", "40 of 4" }, Rows(path, "Followers"));
        }

        [Fact]
        public void the_engine_finds_them_and_comes_to_what_access_does_once_the_file_is_compacted()
        {
            // ACE, with the indexes DAO rebuilt by compacting...
            var compacted = AccessReferee.Compact(EarlierEngineFile());
            Assert.False(AceRefuses(compacted, "DELETE FROM Parent WHERE K = 3"));
            Assert.True(AceRefuses(compacted, "DELETE FROM Parent WHERE K = 2"));
            Assert.False(AceRefuses(compacted, "UPDATE Parent SET K = 9 WHERE K = 1"));

            // ...and the engine, with the file as the earlier release left it.
            var path = EarlierEngineFile();
            Assert.False(EngineRefuses(path, db => db.GetTable("Parent").DeleteRow("K", 3)));
            Assert.True(EngineRefuses(path, db => db.GetTable("Parent").DeleteRow("K", 2)));
            Assert.False(EngineRefuses(path, db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { { "K", 9 } })));

            foreach (var table in Tables)
                AccessReferee.AssertSame(Rows(compacted, table), Rows(path, table), table);

            // What that is, so that the two agreeing on the wrong thing fails too.
            Assert.Equal(new[] { "2", "4", "9" }, Rows(path, "Parent"));
            Assert.Equal(new[] { "5 of 9", "10 of 9", "11 of 9", "20 of 2", "40 of 4" }, Rows(path, "Followers"));
            Assert.Empty(Rows(path, "Movers"));
            Assert.Equal(new[] { "1 of none", "5 of 2" }, Rows(path, "Holders"));

            // A row the engine rewrote has its entry: ACE finds Followers 5 through the index now, in the engine's file.
            var found = AccessReferee.AceRows(path, "SELECT Id FROM Followers WHERE K = 9").Select(r => Convert.ToInt32(r["Id"])).OrderBy(id => id);
            Assert.Equal(new[] { 5, 10, 11 }, found);
        }

        /// <summary>
        /// The engine as it is unless asked otherwise: the three changes ACE makes above, made by the engine in
        /// another copy of the file, leave the rows ACE leaves - the orphans among them.
        /// </summary>
        [Fact]
        public void trusting_its_indexes_the_engine_does_with_the_file_what_access_does()
        {
            var ace = EarlierEngineFile();
            var engine = EarlierEngineFile();

            Assert.Equal(AceRefuses(ace, "DELETE FROM Parent WHERE K = 3"),
                         EngineRefuses(engine, db => db.GetTable("Parent").DeleteRow("K", 3), verifyIndexes: false));
            Assert.Equal(AceRefuses(ace, "DELETE FROM Parent WHERE K = 2"),
                         EngineRefuses(engine, db => db.GetTable("Parent").DeleteRow("K", 2), verifyIndexes: false));
            Assert.Equal(AceRefuses(ace, "UPDATE Parent SET K = 9 WHERE K = 1"),
                         EngineRefuses(engine, db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { { "K", 9 } }), verifyIndexes: false));

            foreach (var table in Tables)
                AccessReferee.AssertSame(Rows(ace, table), Rows(engine, table), table);

            // What that is: Movers 30 without its parent, Holders 5 without its, Followers 5 left behind.
            Assert.Equal(new[] { "4", "9" }, Rows(engine, "Parent"));
            Assert.Equal(new[] { "30 of 3" }, Rows(engine, "Movers"));
            Assert.Equal(new[] { "1 of none", "5 of 2" }, Rows(engine, "Holders"));
            Assert.Equal(new[] { "5 of 1", "10 of 9", "11 of 9", "40 of 4" }, Rows(engine, "Followers"));
        }

        // ── Rows an earlier release left out of a unique index ───────────────

        private const string UniqueIndexes = "engine-3ad3aed-rows-in-unique-indexes.mdb";

        /// <summary>Codes' rows as "Id has Code", in Id order, read by ACE without a WHERE: no index is asked.</summary>
        private static List<string> Codes(string path)
        {
            return AccessReferee.AceRows(path, "SELECT Id, Code FROM Codes").OrderBy(r => Convert.ToInt32(r["Id"]))
                .Select(r => r["Id"] + " has " + r["Code"]).ToList();
        }

        /// <summary>The Ids of Renamed's rows, in order, read by ACE without a WHERE.</summary>
        private static List<int> Renamed(string path)
        {
            return AccessReferee.AceRows(path, "SELECT Id FROM Renamed").Select(r => Convert.ToInt32(r["Id"])).OrderBy(id => id).ToList();
        }

        /// <summary>The names of <paramref name="table"/>'s indexes, in order, as DAO lists them.</summary>
        private static List<string> DaoIndexes(string path, string table)
        {
            var names = new List<string>();
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = dbe.OpenDatabase(path, false, true);
                try
                {
                    foreach (dynamic index in db.TableDefs[table].Indexes)
                        names.Add((string)index.Name);
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>
        /// Whether the engine refuses <paramref name="change"/> as a duplicate - checking each unique index before
        /// it trusts it when <paramref name="verifyIndexes"/>, and trusting it as Access does when not.
        /// </summary>
        private static bool EngineRefusesADuplicate(string path, Action<Database> change, bool verifyIndexes = true)
        {
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = verifyIndexes;
                try { change(db); return false; }
                catch (InvalidOperationException e) when (e.Message.Contains("duplicate")) { return true; }
            }
        }

        private static Row Code(int id, int code) { return new Row { { "Id", id }, { "Code", code }, { "Name", "c" + id } }; }

        /// <summary>
        /// The second such database (the engine's tools/legacy-unique-indexes.ps1 made it): DAO made Codes(Id primary
        /// key, Code in the unique index CodeUnique, Name) with (1, 100) and (2, 200), and Renamed(Id primary key,
        /// Name) with 1 and 2. The engine at 3ad3aed then inserted Codes (3, 300), which CodeUnique does not hold;
        /// changed Codes 2 to Code 250, which CodeUnique has under 200, pointing where the row was; and changed
        /// Renamed 2 to Id 7, which the primary key has under 2. ACE trusts each index: a second row with Code 300,
        /// Code 250 or Id 7 is added. And then the file cannot be compacted whole - DAO cannot build an index its
        /// rows are duplicates in, and leaves the table without it.
        /// </summary>
        [Fact]
        public void access_adds_a_duplicate_of_a_row_a_unique_index_does_not_hold_and_compacting_then_drops_the_index()
        {
            var path = EarlierEngineFile(UniqueIndexes);

            Assert.False(AceRefuses(path, "INSERT INTO Codes (Id, Code, Name) VALUES (4, 300, 'c4')"));
            Assert.False(AceRefuses(path, "INSERT INTO Codes (Id, Code, Name) VALUES (5, 250, 'c5')"));
            Assert.False(AceRefuses(path, "INSERT INTO Renamed (Id, Name) VALUES (7, 'r7')"));
            Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300", "4 has 300", "5 has 250" }, Codes(path));
            Assert.Equal(new[] { 1, 7, 7 }, Renamed(path));

            // What an index says is there, ACE believes as well: values no row has.
            Assert.True(AceRefuses(path, "INSERT INTO Codes (Id, Code, Name) VALUES (6, 200, 'c6')"));
            Assert.True(AceRefuses(path, "INSERT INTO Renamed (Id, Name) VALUES (2, 'r2')"));

            var compacted = AccessReferee.Compact(path);
            Assert.Equal(new[] { "PrimaryKey" }, DaoIndexes(compacted, "Codes"));
            Assert.Empty(DaoIndexes(compacted, "Renamed"));
            Assert.Equal(new[] { "Codes", "Renamed" },
                AccessReferee.AceRows(compacted, "SELECT ErrorTable FROM MSysCompactError").Select(r => (string)r["ErrorTable"]).OrderBy(t => t));
            Assert.False(AceRefuses(compacted, "INSERT INTO Codes (Id, Code, Name) VALUES (9, 300, 'c9')"));
            Assert.False(AceRefuses(compacted, "INSERT INTO Renamed (Id, Name) VALUES (7, 'r7 again')"));
        }

        /// <summary>
        /// The engine as it is unless asked otherwise: each row ACE is given above, given to the engine in another
        /// copy of the file, is added or refused as ACE adds or refuses it, and the tables are left the same. (A
        /// second row with Code 200 is not among them: ACE refuses it for an entry that points at no row, and the
        /// engine, which does not believe such an entry, adds it - #36, below.)
        /// </summary>
        [Fact]
        public void trusting_its_indexes_the_engine_adds_the_duplicates_access_adds()
        {
            var ace = EarlierEngineFile(UniqueIndexes);
            var engine = EarlierEngineFile(UniqueIndexes);

            Assert.Equal(AceRefuses(ace, "INSERT INTO Codes (Id, Code, Name) VALUES (4, 300, 'c4')"),
                         EngineRefusesADuplicate(engine, db => db.GetTable("Codes").Insert(Code(4, 300)), verifyIndexes: false));
            Assert.Equal(AceRefuses(ace, "INSERT INTO Codes (Id, Code, Name) VALUES (5, 250, 'c5')"),
                         EngineRefusesADuplicate(engine, db => db.GetTable("Codes").Insert(Code(5, 250)), verifyIndexes: false));
            Assert.Equal(AceRefuses(ace, "INSERT INTO Codes (Id, Code, Name) VALUES (8, 100, 'c8')"),
                         EngineRefusesADuplicate(engine, db => db.GetTable("Codes").Insert(Code(8, 100)), verifyIndexes: false));
            Assert.Equal(AceRefuses(ace, "INSERT INTO Renamed (Id, Name) VALUES (7, 'r7')"),
                         EngineRefusesADuplicate(engine, db => db.GetTable("Renamed").Insert(new Row { { "Id", 7 }, { "Name", "r7" } }), verifyIndexes: false));
            Assert.Equal(AceRefuses(ace, "INSERT INTO Renamed (Id, Name) VALUES (2, 'r2')"),
                         EngineRefusesADuplicate(engine, db => db.GetTable("Renamed").Insert(new Row { { "Id", 2 }, { "Name", "r2" } }), verifyIndexes: false));

            AccessReferee.AssertSame(Codes(ace), Codes(engine), "Codes");
            Assert.Equal(Renamed(ace), Renamed(engine));

            // What that is: two rows with Code 300, two with Code 250 and two with Id 7.
            Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300", "4 has 300", "5 has 250" }, Codes(engine));
            Assert.Equal(new[] { 1, 7, 7 }, Renamed(engine));
        }

        [Fact]
        public void the_engine_refuses_such_a_duplicate_as_access_does_once_the_file_is_compacted_and_leaves_a_file_that_compacts_whole()
        {
            // ACE, with the indexes DAO rebuilt by compacting...
            var compacted = AccessReferee.Compact(EarlierEngineFile(UniqueIndexes));
            Assert.Equal(new[] { "CodeUnique", "PrimaryKey" }, DaoIndexes(compacted, "Codes"));
            Assert.True(AceRefuses(compacted, "INSERT INTO Codes (Id, Code, Name) VALUES (4, 300, 'c4')"));
            Assert.True(AceRefuses(compacted, "INSERT INTO Codes (Id, Code, Name) VALUES (5, 250, 'c5')"));
            Assert.True(AceRefuses(compacted, "INSERT INTO Renamed (Id, Name) VALUES (7, 'r7')"));
            Assert.True(AceRefuses(compacted, "UPDATE Codes SET Code = 300 WHERE Id = 1"));
            Assert.False(AceRefuses(compacted, "INSERT INTO Codes (Id, Code, Name) VALUES (6, 200, 'c6')"));

            // ...and the engine, verifying its indexes, with the file as the earlier release left it.
            var path = EarlierEngineFile(UniqueIndexes);
            Assert.True(EngineRefusesADuplicate(path, db => db.GetTable("Codes").Insert(Code(4, 300))));
            Assert.True(EngineRefusesADuplicate(path, db => db.GetTable("Codes").Insert(Code(5, 250))));
            Assert.True(EngineRefusesADuplicate(path, db => db.GetTable("Renamed").Insert(new Row { { "Id", 7 }, { "Name", "r7" } })));
            Assert.True(EngineRefusesADuplicate(path, db => db.GetTable("Codes").UpdateByPrimaryKey(1, new Row { { "Code", 300 } })));
            Assert.False(EngineRefusesADuplicate(path, db => db.GetTable("Codes").Insert(Code(6, 200))));

            AccessReferee.AssertSame(Codes(compacted), Codes(path), "Codes");
            AccessReferee.AssertSame(Renamed(compacted).Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(),
                                     Renamed(path).Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(), "Renamed");
            Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300", "6 has 200" }, Codes(path));
            Assert.Equal(new[] { 1, 7 }, Renamed(path));

            // No row is Renamed 2, and compacted, ACE takes one. In the file as it is the primary key has an entry
            // for 2, which points at the row that is now 7: the engine, verifying its indexes, sees that row's key is
            // 7, a Long it makes as Access does, takes the entry for the row's wrong one, and takes the row too (#36).
            Assert.False(AceRefuses(compacted, "INSERT INTO Renamed (Id, Name) VALUES (2, 'r2')"));
            Assert.False(EngineRefusesADuplicate(path, db => db.GetTable("Renamed").Insert(new Row { { "Id", 2 }, { "Name", "r2" } })));
            Assert.Equal(Renamed(compacted), Renamed(path));

            // The engine's file has no duplicates in it: DAO compacts it with every index, and ACE then refuses them.
            var after = AccessReferee.Compact(path);
            Assert.Equal(new[] { "CodeUnique", "PrimaryKey" }, DaoIndexes(after, "Codes"));
            Assert.Equal(new[] { "PrimaryKey" }, DaoIndexes(after, "Renamed"));
            Assert.True(AceRefuses(after, "SELECT ErrorTable FROM MSysCompactError"));   // DAO had nothing to record
            foreach (var code in new[] { 100, 250, 300, 200 })
                Assert.True(AceRefuses(after, "INSERT INTO Codes (Id, Code, Name) VALUES (9, " + code + ", 'c9')"));
        }

        // ── Entries an earlier release left that are no row's (#36) ──────────

        /// <summary>What ACE answers <paramref name="sql"/> with, as text, or its refusal.</summary>
        private static string Ace(string path, string sql)
        {
            try
            {
                return string.Join("; ", AccessReferee.AceRows(path, sql)
                    .Select(row => string.Join(" ", row.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))));
            }
            catch (OleDbException e) { return "refused: " + e.Message; }
        }

        /// <summary>
        /// What ACE makes of an entry that is no row's, in the file as the earlier release left it. CodeUnique's
        /// entry for 200 points where a row was: ACE fails a query that comes to it, for the key or for a range
        /// that passes it. The primary key of Renamed has an entry for 2 that points at the row that is now 7:
        /// asked for 2 through the key, ACE answers with that row, and asked for 7, with none.
        /// </summary>
        [Fact]
        public void access_fails_a_query_that_comes_to_an_entry_pointing_at_no_row_and_answers_with_the_row_an_entry_points_at()
        {
            var path = EarlierEngineFile(UniqueIndexes);

            Assert.Equal("refused: Not a valid bookmark.", Ace(path, "SELECT Id FROM Codes WHERE Code = 200"));
            Assert.Equal("refused: Not a valid bookmark.", Ace(path, "SELECT Id FROM Codes WHERE Code >= 100"));
            Assert.Equal("1", Ace(path, "SELECT Id FROM Codes WHERE Code = 100"));

            Assert.Equal("7 r2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 2"));
            Assert.Equal("", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 7"));
            Assert.Equal("7 r2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id + 0 = 7"));   // by reading the table
        }

        /// <summary>
        /// The engine, trusting its indexes, adds a row with Code 200. It takes the entry that points at no row out
        /// as it adds the row's (the engine's #36), so ACE finds the row through the index: left in, ACE went on
        /// failing the query, and the row was found only by reading the table.
        /// </summary>
        [Fact]
        public void access_finds_through_the_index_a_row_the_engine_added_under_a_key_whose_entry_pointed_at_no_row()
        {
            var path = EarlierEngineFile(UniqueIndexes);

            using (var db = Database.Open(path))
                db.GetTable("Codes").Insert(Code(6, 200));

            Assert.Equal("6", Ace(path, "SELECT Id FROM Codes WHERE Code = 200"));
            Assert.Equal("1; 6", Ace(path, "SELECT Id FROM Codes WHERE Code >= 100 ORDER BY Id"));   // the rows the index holds
            Assert.True(AceRefuses(path, "INSERT INTO Codes (Id, Code, Name) VALUES (9, 200, 'c9')"));

            // ACE goes on from there: the row gives the code up, and another takes it.
            AccessReferee.Execute(path, "UPDATE Codes SET Code = 201 WHERE Code = 200",
                                        "INSERT INTO Codes (Id, Code, Name) VALUES (9, 200, 'c9')");
            Assert.Equal("9", Ace(path, "SELECT Id FROM Codes WHERE Code = 200"));
            Assert.Equal("6", Ace(path, "SELECT Id FROM Codes WHERE Code = 201"));
            Assert.Equal(new[] { "1 has 100", "2 has 250", "3 has 300", "6 has 201", "9 has 200" }, Codes(path));
        }

        /// <summary>
        /// The engine, verifying its indexes, adds a row with Id 2, which no row has. The entry for 2 that pointed at
        /// the row that is 7 is taken out, and that row entered under 7: ACE then answers for 2 with the new row and
        /// for 7 with the row that is 7, refuses a second of either, and DAO compacts the file with the key.
        /// Trusting its indexes, the engine refuses the row, as ACE does, and changes nothing.
        /// </summary>
        [Fact]
        public void access_finds_the_right_row_under_each_key_once_the_engine_has_added_a_row_under_a_key_whose_entry_pointed_at_another()
        {
            var path = EarlierEngineFile(UniqueIndexes);
            var before = File.ReadAllBytes(path);
            Assert.True(EngineRefusesADuplicate(path, db => db.GetTable("Renamed").Insert(new Row { { "Id", 2 }, { "Name", "new 2" } }), verifyIndexes: false));
            Assert.Equal(before, File.ReadAllBytes(path));

            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                db.GetTable("Renamed").Insert(new Row { { "Id", 2 }, { "Name", "new 2" } });
            }

            Assert.Equal("2 new 2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 2"));
            Assert.Equal("7 r2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 7"));
            Assert.Equal("1 r1; 2 new 2; 7 r2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id >= 1 ORDER BY Id"));
            Assert.True(AceRefuses(path, "INSERT INTO Renamed (Id, Name) VALUES (2, 'another')"));
            Assert.True(AceRefuses(path, "INSERT INTO Renamed (Id, Name) VALUES (7, 'another')"));

            var compacted = AccessReferee.Compact(path);
            Assert.Equal(new[] { "PrimaryKey" }, DaoIndexes(compacted, "Renamed"));
            Assert.Equal(new[] { 1, 2, 7 }, Renamed(compacted));
        }

        /// <summary>
        /// Asked to verify its indexes, the engine reads each before it believes it, and what it saw then to be no
        /// row's it takes out when it next changes the table, whatever the row: a range ACE failed, for the entry
        /// that pointed at no row, it then answers.
        /// </summary>
        [Fact]
        public void access_answers_a_range_once_the_engine_verifying_its_indexes_has_changed_the_table()
        {
            var path = EarlierEngineFile(UniqueIndexes);

            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                db.GetTable("Codes").Insert(Code(6, 600));
                db.GetTable("Renamed").Insert(new Row { { "Id", 9 }, { "Name", "r9" } });
            }

            Assert.Equal("", Ace(path, "SELECT Id FROM Codes WHERE Code = 200"));
            Assert.Equal("1; 6", Ace(path, "SELECT Id FROM Codes WHERE Code >= 100 ORDER BY Id"));   // the rows the index holds
            Assert.Equal("", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 2"));
            Assert.Equal("7 r2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 7"));
            Assert.False(AceRefuses(path, "INSERT INTO Codes (Id, Code, Name) VALUES (9, 200, 'c9')"));
            Assert.False(AceRefuses(path, "INSERT INTO Renamed (Id, Name) VALUES (2, 'r2')"));
            Assert.True(AceRefuses(path, "INSERT INTO Renamed (Id, Name) VALUES (7, 'another')"));
        }

        /// <summary>
        /// The engine, verifying its indexes, gives the row that is 7 - under 2 in the primary key - the Id 77. The
        /// entry under 2 is taken out and the row entered under 77, once: ACE then finds it under 77 alone, counts the
        /// table's two rows through the key, DAO compacts the file with the key, and ACE deletes the row and counts one.
        /// The update entered the row under 77 a second time, after the check that moved its entry had
        /// (#66), and DAO compacted the file without its primary key.
        /// </summary>
        [Fact]
        public void access_counts_and_deletes_through_the_key_a_row_the_engine_verifying_its_indexes_gave_a_new_key()
        {
            var path = EarlierEngineFile(UniqueIndexes);

            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                var cursor = db.GetTable("Renamed").CreateCursor();
                Assert.NotNull(cursor.FindRow("Id", 7));
                cursor.UpdateCurrentRow(new Row { { "Id", 77 } });
            }

            Assert.Equal("77 r2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 77"));
            Assert.Equal("", Ace(path, "SELECT Id FROM Renamed WHERE Id = 2"));
            Assert.Equal("", Ace(path, "SELECT Id FROM Renamed WHERE Id = 7"));
            Assert.Equal("2", Ace(path, "SELECT COUNT(*) FROM Renamed WHERE Id >= 1"));
            Assert.Equal("1 r1; 77 r2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id >= 1 ORDER BY Id"));

            var compacted = AccessReferee.Compact(path);
            Assert.Equal(new[] { "PrimaryKey" }, DaoIndexes(compacted, "Renamed"));
            Assert.Equal(new[] { 1, 77 }, Renamed(compacted));

            AccessReferee.Execute(path, "DELETE FROM Renamed WHERE Id = 77");
            Assert.Equal("1", Ace(path, "SELECT COUNT(*) FROM Renamed WHERE Id >= 1"));
            Assert.Equal("1 r1", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id >= 1 ORDER BY Id"));
        }

        /// <summary>
        /// Another row is 7 already when the engine, verifying its indexes, adds a row with Id 2: the row found under
        /// 2 is entered under 7 beside it, the two being duplicates whatever the key says. ACE answers for 2 with the
        /// new row, and for 7 through the key without failing.
        /// </summary>
        [Fact]
        public void access_answers_for_each_key_when_the_row_moved_to_its_own_key_has_a_duplicate_there()
        {
            var path = EarlierEngineFile(UniqueIndexes);

            using (var db = Database.Open(path))
                db.GetTable("Renamed").Insert(new Row { { "Id", 7 }, { "Name", "second 7" } });   // added, as ACE adds it: the key has nothing under 7
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                db.GetTable("Renamed").Insert(new Row { { "Id", 2 }, { "Name", "new 2" } });
            }

            Assert.Equal("2 new 2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 2"));
            Assert.Equal("7 r2", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id = 7 ORDER BY Name"));   // a unique key: ACE stops at its first entry
            Assert.Equal("1 r1; 2 new 2; 7 r2; 7 second 7", Ace(path, "SELECT Id, Name FROM Renamed WHERE Id >= 1 ORDER BY Id, Name"));
        }

        /// <summary>
        /// A key an earlier release wrote whole (the engine's 696ca52-long-keys file), with the row of 255 é taken
        /// out of the table and not out of the key: what a delete that looked for the cut key left. The entry
        /// points at no row, under a key of 513 bytes that is not beside the 510-byte key Access and the engine of
        /// today make for that code. The engine adds a row with the code, takes the entry out, and ACE finds the
        /// row through the key and reads the whole table by it.
        /// </summary>
        [Fact]
        public void access_finds_a_row_the_engine_added_under_a_long_key_whose_whole_entry_pointed_at_no_row()
        {
            var path = EarlierEngineFile("engine-696ca52-long-keys-row-deleted.mdb");
            var code = new string('é', 255);

            Assert.Equal("", Ace(path, "SELECT N FROM Coded WHERE Code = '" + code + "'"));
            // With the file as it is ACE finds no row with the code, and counts through the key a row the table
            // has not: 25 entries, for 24 rows.
            Assert.Equal("25", Ace(path, "SELECT COUNT(*) FROM Coded WHERE Code >= 'a'"));
            Assert.Equal(24, AccessReferee.AceRows(path, "SELECT N FROM Coded WHERE N + 0 >= 0").Count);

            using (var db = Database.Open(path))
                db.GetTable("Coded").Insert(new Row { { "Code", code }, { "N", 99 } });

            Assert.Equal("99", Ace(path, "SELECT N FROM Coded WHERE Code = '" + code + "'"));
            Assert.Equal("25", Ace(path, "SELECT COUNT(*) FROM Coded WHERE Code >= 'a'"));
            Assert.True(AceRefuses(path, "INSERT INTO Coded (Code, N) VALUES ('" + code + "', 100)"));
        }

        // ── Decimals in the byte order earlier releases wrote ────────────────

        /// <summary>
        /// Prices(Id, Price DECIMAL(5,2)), made and filled by ACE: 1.23 for row 1, and for row 2 the seventeen bytes
        /// an earlier release of the engine wrote for 1.00 - a sign byte and then the number without its point, 100,
        /// as sixteen bytes with the most significant first. They are put there by this test: ACE writes 111.11,
        /// and its bytes (a sign byte and four 32-bit words, the most significant first, each least significant
        /// byte first) are written over in the file.
        /// </summary>
        private string PricesWithOneInTheEarlierByteOrder()
        {
            var path = _tmp.NewMdbPath();
            AccessReferee.DaoCreate(path, 64);
            AccessReferee.Execute(path,
                "CREATE TABLE Prices (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Price DECIMAL(5,2))",
                "INSERT INTO Prices (Id, Price) VALUES (1, 1.23)",
                "INSERT INTO Prices (Id, Price) VALUES (2, 111.11)");

            var asAccessWrote = new byte[17];
            asAccessWrote[13] = 0x67; asAccessWrote[14] = 0x2B;   // 11111 = 0x2B67, the last word, least significant byte first
            var asEarlierReleasesWrote = new byte[17];
            asEarlierReleasesWrote[16] = 100;                     // 100, the last of sixteen bytes

            var file = File.ReadAllBytes(path);
            var at = Enumerable.Range(0, file.Length - 17).Where(i => asAccessWrote.Select((b, j) => file[i + j] == b).All(same => same)).ToList();
            Assert.Single(at);
            asEarlierReleasesWrote.CopyTo(file, at[0]);
            File.WriteAllBytes(path, file);
            return path;
        }

        [Fact]
        public void access_reads_a_decimal_beyond_its_precision_as_the_engine_does_and_the_engine_lists_it()
        {
            var path = PricesWithOneInTheEarlierByteOrder();

            // ACE computes with the number the engine reads: 16,777,216 where 1.00 was meant.
            Assert.Equal("16777216", Convert.ToString(AccessReferee.Scalar(path, "SELECT CStr(Price) FROM Prices WHERE Id = 2"), CultureInfo.InvariantCulture));
            Assert.Equal(2, Convert.ToInt32(AccessReferee.Scalar(path, "SELECT Id FROM Prices WHERE Price = 16777216")));
            Assert.Null(AccessReferee.Scalar(path, "SELECT Id FROM Prices WHERE Price = 1"));

            using (var db = Database.Open(path))
            {
                var rows = db.GetTable("Prices").Rows.ToList();
                Assert.Equal(new object[] { 1.23m, 16777216.00m }, rows.OrderBy(r => (int)r["Id"]).Select(r => r["Price"]));

                var listed = Assert.Single(DecimalByteOrder.Find(db));
                Assert.Equal("Prices", listed.Table);
                Assert.Equal("Price", listed.Column.Name);
                Assert.Equal(2, listed.PrimaryKey["Id"]);
                Assert.Equal(16777216.00m, listed.Value);
                Assert.Equal(1.00m, listed.ValueInEarlierByteOrder);
            }

            // Its OLE DB provider cannot hand such a value over, and a query that sorts its rows is refused.
            Assert.ThrowsAny<Exception>(() => AccessReferee.AceRows(path, "SELECT Price FROM Prices WHERE Id = 2"));
            Assert.Throws<OleDbException>(() => AccessReferee.AceRows(path, "SELECT Id, Price FROM Prices ORDER BY Id"));
        }

        [Fact]
        public void access_writes_no_decimal_beyond_its_precision()
        {
            var path = PricesWithOneInTheEarlierByteOrder();

            Assert.True(AceRefuses(path, "INSERT INTO Prices (Id, Price) VALUES (3, 1000)"));
            Assert.True(AceRefuses(path, "UPDATE Prices SET Price = 16777216 WHERE Id = 1"));
            Assert.True(AceRefuses(path, "UPDATE Prices SET Price = Price WHERE Id = 2"));   // not even the one that is there
            Assert.False(AceRefuses(path, "INSERT INTO Prices (Id, Price) VALUES (3, 999.99)"));

            using (var db = Database.Open(path))
                Assert.Equal(new[] { 2 }, DecimalByteOrder.Find(db).Select(listed => (int)listed.PrimaryKey["Id"]));
        }

        // ── A Decimal whose number runs past 96 bits ─────────────────────────

        /// <summary>A Decimal as Access stores one: a sign byte and four 32-bit words, the most significant first, each least significant byte first.</summary>
        private static byte[] StoredDecimal(bool negative, uint first, uint second, uint third, uint fourth)
        {
            var stored = new byte[17];
            stored[0] = negative ? (byte)0x80 : (byte)0;
            var words = new[] { first, second, third, fourth };
            for (var word = 0; word < 4; word++)
                BitConverter.GetBytes(words[word]).CopyTo(stored, 1 + word * 4);
            return stored;
        }

        /// <summary>
        /// D(Id, Small DECIMAL(5,2), Big DECIMAL(28,0)), made and filled by ACE, and then seven of its values written
        /// over, in the file, with numbers whose first word is not zero - which Access itself never writes, 28
        /// digits being under 2^96. Each value ACE wrote is found by its seventeen bytes.
        /// </summary>
        private string DecimalsWithTheFirstWordSet()
        {
            var path = _tmp.NewMdbPath();
            AccessReferee.DaoCreate(path, 64);
            AccessReferee.Execute(path, "CREATE TABLE D (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Small DECIMAL(5,2), Big DECIMAL(28,0))");
            for (var id = 1; id <= 4; id++)
                AccessReferee.Execute(path, "INSERT INTO D (Id, Small, Big) VALUES (" + id + ", " + id * 111 + "." + id * 11 + ", " + (id * 11111 + 1) + ")");

            var file = File.ReadAllBytes(path);
            Action<uint, byte[]> store = (standIn, wide) =>
            {
                var was = StoredDecimal(false, 0, 0, 0, standIn);
                var at = Enumerable.Range(0, file.Length - 17).Where(i => was.Select((b, j) => file[i + j] == b).All(same => same)).ToList();
                Assert.Single(at);
                wide.CopyTo(file, at[0]);
            };
            store(11111, StoredDecimal(false, 0x10000000, 0, 0, 0));                           // Small of 1: 2^124, nothing in the low 96 bits
            store(22222, StoredDecimal(false, 1, 0, 0, 12345));                                // Small of 2: 2^96 + 12345
            store(33333, StoredDecimal(true, 0xFFFFFFFF, 0, 0, 12345));                        // Small of 3
            store(44444, StoredDecimal(false, 1, 0, 0, 1234567));                              // Small of 4: more than five digits, too
            store(11112, StoredDecimal(false, 1, 0, 0, 12345));                                // Big of 1
            store(22223, StoredDecimal(false, 1, 1, 2, 3));                                    // Big of 2: 2^96 + 2^64 + 2 * 2^32 + 3
            store(33334, StoredDecimal(false, 1, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF));         // Big of 3: 2^97 - 1
            File.WriteAllBytes(path, file);
            return path;
        }

        /// <summary>
        /// D's Small and Big, by Id, as DAO hands them over - from the table itself: a recordset that copies or sorts
        /// its rows is refused for a number beyond its column's precision.
        /// </summary>
        private static List<decimal[]> DaoDecimals(string path)
        {
            var rows = new SortedDictionary<int, decimal[]>();
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = dbe.OpenDatabase(path, false, true);
                try
                {
                    dynamic recordset = db.OpenRecordset("D", 1 /* dbOpenTable */);
                    try
                    {
                        while (!recordset.EOF)
                        {
                            rows.Add(Convert.ToInt32((object)recordset.Fields["Id"].Value),
                                     new[] { Convert.ToDecimal((object)recordset.Fields["Small"].Value), Convert.ToDecimal((object)recordset.Fields["Big"].Value) });
                            recordset.MoveNext();
                        }
                    }
                    finally { recordset.Close(); }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return rows.Values.ToList();
        }

        /// <summary>
        /// Access reads such a number as if its first word were not there: the number the other three words make,
        /// 96 bits, with its sign, and no error - through DAO, and in what its SQL computes with. The engine read
        /// null; it reads what Access reads. (Java Jackcess gives all 128 bits as a BigDecimal, which a .NET decimal
        /// has no room for.)
        /// </summary>
        [Fact]
        public void access_reads_a_decimal_past_96_bits_as_its_low_96_bits_and_so_does_the_engine()
        {
            var path = DecimalsWithTheFirstWordSet();
            var expected = new[]
            {
                new[] { 0.00m, 12345m },
                new[] { 123.45m, 18446744082299486211m },
                new[] { -123.45m, 79228162514264337593543950335m },
                new[] { 12345.67m, 44445m },
            };

            // DAO hands each over.
            var dao = DaoDecimals(path);
            Assert.Equal(expected.Select(row => row[0]), dao.Select(row => row[0]));
            Assert.Equal(expected.Select(row => row[1]), dao.Select(row => row[1]));

            // ACE's SQL computes with them, and finds them by them.
            for (var id = 1; id <= 4; id++)
            {
                var row = AccessReferee.AceRows(path, "SELECT CStr(Small) AS S, CStr(Big) AS B FROM D WHERE Id = " + id).Single();
                Assert.Equal(expected[id - 1][0], decimal.Parse(((string)row["S"]).Replace(',', '.'), CultureInfo.InvariantCulture));
                Assert.Equal(expected[id - 1][1], decimal.Parse((string)row["B"], CultureInfo.InvariantCulture));
            }
            Assert.Equal(2, Convert.ToInt32(AccessReferee.Scalar(path, "SELECT Id FROM D WHERE Small = 123.45")));
            Assert.Equal(1, Convert.ToInt32(AccessReferee.Scalar(path, "SELECT Id FROM D WHERE Small = 0")));
            Assert.Equal(1, Convert.ToInt32(AccessReferee.Scalar(path, "SELECT Id FROM D WHERE Big = 12345")));

            // Its OLE DB provider hands over those that fit their column.
            Assert.Equal(new object[] { 0.00m, 123.45m, -123.45m },
                AccessReferee.AceRows(path, "SELECT Small FROM D WHERE Id < 4 ORDER BY Id").Select(r => r["Small"]));
            Assert.Equal(new object[] { 12345m, 18446744082299486211m },
                AccessReferee.AceRows(path, "SELECT Big FROM D WHERE Id < 3 ORDER BY Id").Select(r => r["Big"]));

            using (var db = Database.Open(path))
            {
                var rows = db.GetTable("D").Rows.OrderBy(r => (int)r["Id"]).ToList();
                Assert.Equal(expected.Select(row => (object)row[0]), rows.Select(r => r["Small"]));
                Assert.Equal(expected.Select(row => (object)row[1]), rows.Select(r => r["Big"]));

                // Each is listed as a number Access did not write, with what a read gives.
                var listed = DecimalByteOrder.Find(db).ToList();
                Assert.Equal(new[] { "1 Small", "1 Big", "2 Small", "2 Big", "3 Small", "3 Big", "4 Small" },
                    listed.Select(found => found.PrimaryKey["Id"] + " " + found.Column.Name));
                Assert.All(listed, found =>
                {
                    Assert.True(found.IsCut);
                    Assert.Equal(expected[(int)found.PrimaryKey["Id"] - 1][found.Column.Name == "Small" ? 0 : 1], found.Value);
                });
            }
        }
    }
}
