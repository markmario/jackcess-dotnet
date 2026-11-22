using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Reflection;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Tables the managed engine creates, checked by ACE: listed, read, sought through
    /// their primary keys, added to by ACE - which then writes into the engine's index pages, between the engine's own
    /// keys - counted correctly after the engine deletes, and kept whole when DAO compacts the file.
    /// </summary>
    public class EngineTablesInAccessTests : System.IDisposable
    {
        private TempDb _tmp;

        public EngineTablesInAccessTests() { _tmp = new TempDb(); }
        public void Dispose() { _tmp.Dispose(); }

        private static ColumnDefinition[] Columns(params ColumnDefinition[] columns) { return columns; }
        private static ColumnDefinition Long(string name) { return new ColumnDefinition(name, DataType.Int32); }
        private static ColumnDefinition Text(string name, int chars) { return new ColumnDefinition(name, DataType.Text) { MaxLength = chars }; }

        private static object Scalar(string path, string sql)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var command = new OleDbCommand(sql, connection))
            {
                connection.Open();
                return command.ExecuteScalar();
            }
        }

        private static void Execute(string path, string sql)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Execute(connection, sql);
            }
        }

        private static void Execute(OleDbConnection connection, string sql)
        {
            using (var command = new OleDbCommand(sql, connection))
                command.ExecuteNonQuery();
        }

        // The keys the tests give: spread over 1..100,002 and all different, the first 3,000 for the engine and the
        // next for ACE, so ACE's land between the engine's, in the leaves the engine wrote.
        private static int Key(int i) { return (int)((i * 7919L) % 100003); }

        private static List<string> ListedTables(string path)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                return connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null).Rows.Cast<System.Data.DataRow>()
                    .Where(r => (string)r["TABLE_TYPE"] == "TABLE")
                    .Select(r => (string)r["TABLE_NAME"])
                    .ToList();
            }
        }

        private static readonly string[] Names =
            { "Plain", "Table With Spaces", "#Hash", "$Dollar", "Café", "naïve résumé", "ÆØÅ", "Ελληνικά", "O'Brien" };

        [Fact]
        public void ace_lists_and_reads_tables_the_engine_creates()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                foreach (var name in Names)
                {
                    var table = db.CreateTable(name, Columns(Long("Id"), Text("Txt", 100)));
                    for (var i = 1; i <= 3; i++)
                        table.Insert(new Row { ["Id"] = i, ["Txt"] = "row " + i + " é" });
                }
            }

            var listed = ListedTables(path);
            Assert.All(Names, name => Assert.Contains(name, listed));
            foreach (var name in Names)
            {
                var rows = Scalar(path, "SELECT COUNT(*) FROM [" + name + "]");
                Assert.True(Convert.ToInt64(rows) == 3, name + ": " + rows + " rows, expected 3");
                var text = Scalar(path, "SELECT Txt FROM [" + name + "] WHERE Id = 2");
                Assert.True(Equals(text, "row 2 é"), name + ": read '" + text + "', expected 'row 2 é'");
            }
        }

        [Fact]
        public void ace_seeks_and_adds_to_a_primary_key_the_engine_built()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Keyed", Columns(Long("Id"), Text("Txt", 30)), primaryKey: "Id");
                for (var i = 1; i <= 3000; i++)
                    table.Insert(new Row { ["Id"] = Key(i), ["Txt"] = "v" + i });
            }

            Assert.Equal("v10", Scalar(path, "SELECT Txt FROM Keyed WHERE Id = " + Key(10)));
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 3001; i <= 5000; i++)   // between the engine's keys: ACE writes into and splits its leaves
                    Execute(connection, "INSERT INTO Keyed (Id, Txt) VALUES (" + Key(i) + ", 'ace" + i + "')");
                var refused = Assert.Throws<OleDbException>(() => Execute(connection, "INSERT INTO Keyed (Id, Txt) VALUES (" + Key(10) + ", 'twice')"));
                Assert.Contains("duplicate values", refused.Message);
            }
            Assert.Equal(5000, Scalar(path, "SELECT COUNT(*) FROM Keyed"));
            Assert.Equal("ace4234", Scalar(path, "SELECT Txt FROM Keyed WHERE Id = " + Key(4234)));
            Assert.Equal("v2999", Scalar(path, "SELECT Txt FROM Keyed WHERE Id = " + Key(2999)));

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Keyed");
                Assert.Equal(5000, table.Rows.Count());
                Assert.Equal("ace4234", table.FindRowByPrimaryKey(Key(4234))["Txt"]);
                Assert.Equal("v17", table.FindRowByPrimaryKey(Key(17))["Txt"]);
            }
        }

        [Fact]
        public void ace_counts_the_rows_the_engine_leaves_after_deletes_and_updates()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Kept", Columns(Long("Id"), Text("Txt", 30)), primaryKey: "Id");
                for (var i = 1; i <= 1000; i++)
                    table.Insert(new Row { ["Id"] = i, ["Txt"] = "v" + i });
                for (var i = 1; i <= 1000; i += 2)
                    table.DeleteRow("Id", i);
                table.UpdateByPrimaryKey(500, new Row { ["Txt"] = "updated" });
            }

            Assert.Equal(500, Scalar(path, "SELECT COUNT(*) FROM Kept"));
            Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM Kept WHERE Id = 3"));
            Assert.Equal("updated", Scalar(path, "SELECT Txt FROM Kept WHERE Id = 500"));
            // Through the rows, not the key: the deleted rows are gone from their pages too.
            Assert.Equal(499, Scalar(path, "SELECT COUNT(*) FROM Kept WHERE Txt LIKE 'v%'"));
            using (var db = Database.Open(path))
                Assert.Equal(500, db.GetTable("Kept").Rows.Count());
        }

        [Fact]
        public void dao_compacts_a_file_the_engine_made_tables_in_and_keeps_every_row()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                for (var t = 0; t < 300; t++)   // enough to split MSysObjects' ParentIdName and MSysACEs' ObjectId
                    db.CreateTable("Many" + t.ToString("D4"), Columns(Long("Id")));
                var table = db.CreateTable("Keyed", Columns(Long("Id"), new ColumnDefinition("Notes", DataType.Memo)), primaryKey: "Id");
                for (var i = 1; i <= 500; i++)
                    table.Insert(new Row { ["Id"] = i, ["Notes"] = new string((char)('a' + i % 26), 3000) });
            }
            Execute(path, "INSERT INTO Keyed (Id, Notes) VALUES (501, 'added by ACE')");

            var compacted = Path.Combine(_tmp.Dir, "compacted.mdb");
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Dao.Engine;
            try { dbeType.InvokeMember("CompactDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, compacted }); }
            finally { Dao.Release(dbe); }

            Assert.Equal(300, ListedTables(compacted).Count(t => t.StartsWith("Many")));
            Assert.Equal(501, Scalar(compacted, "SELECT COUNT(*) FROM Keyed"));
            Assert.Equal(new string((char)('a' + 250 % 26), 3000), Scalar(compacted, "SELECT Notes FROM Keyed WHERE Id = 250"));
        }

        // ── AutoNumbers: the engine and ACE count from one counter ──

        private static ColumnDefinition AutoNumber(string name) { return new ColumnDefinition(name, DataType.Int32) { IsAutoNumber = true }; }

        private static List<int> Ids(string path, string table)
        {
            var ids = new List<int>();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var command = new OleDbCommand("SELECT Id FROM [" + table + "] ORDER BY Id", connection))
            {
                connection.Open();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        ids.Add(Convert.ToInt32(reader[0]));
            }
            return ids;
        }

        // The table's key is its AutoNumber, so a number handed out twice is one ACE refuses outright.
        [Fact]
        public void ace_numbers_on_from_the_engine_s_AutoNumber_and_the_engine_from_ace_s()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Numbered", Columns(AutoNumber("Id"), Text("Txt", 30)), primaryKey: "Id");
                for (var i = 1; i <= 3; i++)
                    table.Insert(new Row { ["Txt"] = "engine" + i });
            }

            Execute(path, "INSERT INTO Numbered (Txt) VALUES ('ace4')");
            Execute(path, "INSERT INTO Numbered (Txt) VALUES ('ace5')");
            Assert.Equal(4, Scalar(path, "SELECT Id FROM Numbered WHERE Txt = 'ace4'"));
            Assert.Equal(5, Scalar(path, "SELECT Id FROM Numbered WHERE Txt = 'ace5'"));

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Numbered");
                Assert.Equal(5, table.LastAutoNumber);
                var row = new Row { ["Txt"] = "engine6" };
                table.Insert(row);
                Assert.Equal(6, row["Id"]);
            }

            Execute(path, "INSERT INTO Numbered (Txt) VALUES ('ace7')");
            Assert.Equal(7, Scalar(path, "SELECT Id FROM Numbered WHERE Txt = 'ace7'"));
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, Ids(path, "Numbered"));
        }

        [Fact]
        public void a_number_given_to_ace_or_to_the_engine_moves_the_counter_for_both()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Numbered", Columns(AutoNumber("Id"), Text("Txt", 30)), primaryKey: "Id");
                table.Insert(new Row { ["Txt"] = "engine1" });
            }

            // ACE given a number: the engine goes on from it.
            Execute(path, "INSERT INTO Numbered (Id, Txt) VALUES (20, 'ace20')");
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Numbered");
                var row = new Row { ["Txt"] = "engine21" };
                table.Insert(row);
                Assert.Equal(21, row["Id"]);

                // The engine given a number: ACE goes on from it.
                table.AllowAutoNumberInsert = true;
                table.Insert(new Row { ["Id"] = 50, ["Txt"] = "engine50" });
            }
            Execute(path, "INSERT INTO Numbered (Txt) VALUES ('ace51')");

            Assert.Equal(51, Scalar(path, "SELECT Id FROM Numbered WHERE Txt = 'ace51'"));
            Assert.Equal(new[] { 1, 20, 21, 50, 51 }, Ids(path, "Numbered"));
        }

        [Fact]
        public void ace_takes_the_number_an_insert_the_engine_refused_did_not_use()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Numbered", Columns(AutoNumber("Id"), Text("Txt", 5)), primaryKey: "Id");
                table.Insert(new Row { ["Txt"] = "one" });
                table.Insert(new Row { ["Txt"] = "two" });
                Assert.Throws<ColumnValueTooLongException>(() => table.Insert(new Row { ["Txt"] = "longer than five" }));
            }

            Execute(path, "INSERT INTO Numbered (Txt) VALUES ('ace')");

            Assert.Equal(3, Scalar(path, "SELECT Id FROM Numbered WHERE Txt = 'ace'"));
            using (var db = Database.Open(path))
            {
                var row = new Row { ["Txt"] = "four" };
                db.GetTable("Numbered").Insert(row);
                Assert.Equal(4, row["Id"]);
            }
            Assert.Equal(new[] { 1, 2, 3, 4 }, Ids(path, "Numbered"));
        }

        // A GUID AutoNumber. Access marks the column as the engine does, but ACE fills it from the column's
        // DefaultValue property, GenGUID() - seen in a table Access made - not from the mark. The engine gives the
        // column that property when it makes it, with a table or added to one (#33), so a
        // row ACE adds gets a GUID as the engine's rows do, and DAO reports the default.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ace_reads_the_guids_the_engine_generates_and_generates_its_own_in_a_table_the_engine_created(bool added)
        {
            var path = _tmp.NewMdbPath();
            var engines = new List<Guid>();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var key = new ColumnDefinition("Key", DataType.Guid) { IsAutoNumber = true };
                Table table;
                if (added)
                {
                    table = db.CreateTable("Guids", Columns(Text("Txt", 30)));
                    table.AddColumn(key);
                }
                else
                    table = db.CreateTable("Guids", Columns(key, Text("Txt", 30)));
                for (var i = 1; i <= 2; i++)
                {
                    var row = new Row { ["Txt"] = "engine" + i };
                    table.Insert(row);
                    engines.Add((Guid)row["Key"]);
                }
            }

            Assert.NotEqual(engines[0], engines[1]);
            Assert.Equal(engines[0], Scalar(path, "SELECT [Key] FROM Guids WHERE Txt = 'engine1'"));
            Assert.Equal(engines[1], Scalar(path, "SELECT [Key] FROM Guids WHERE Txt = 'engine2'"));

            Execute(path, "INSERT INTO Guids (Txt) VALUES ('ace1')");
            Execute(path, "INSERT INTO Guids (Txt) VALUES ('ace2')");
            var aces = new[] { "ace1", "ace2" }.Select(txt => Scalar(path, "SELECT [Key] FROM Guids WHERE Txt = '" + txt + "'")).ToList();
            Assert.All(aces, key => Assert.IsType<Guid>(key));
            Assert.Equal(4, engines.Concat(aces.Cast<Guid>()).Where(key => key != Guid.Empty).Distinct().Count());

            // DAO reports the default, and the rows and the default are there once it has compacted the file.
            var compacted = AccessReferee.Compact(path);
            foreach (var file in new[] { path, compacted })
            {
                var dbe = Dao.Engine;
                try { Assert.Equal("GenGUID()", DefaultValueOf(dbe, file, "Guids", "Key")); }
                finally { Dao.Release(dbe); }
                Assert.Equal(4L, Convert.ToInt64(Scalar(file, "SELECT COUNT(*) FROM Guids WHERE [Key] IS NOT NULL")));
            }
            Execute(compacted, "INSERT INTO Guids (Txt) VALUES ('ace3')");
            Assert.IsType<Guid>(Scalar(compacted, "SELECT [Key] FROM Guids WHERE Txt = 'ace3'"));
            // ACE reads the row once the engine has closed the file: while it is open to be written, ACE is kept out of
            // it (#62).
            var inserted = new Row { ["Txt"] = "engine3" };
            using (var db = Database.Open(compacted))
                db.GetTable("Guids").Insert(inserted);
            Assert.Equal(inserted["Key"], Scalar(compacted, "SELECT [Key] FROM Guids WHERE Txt = 'engine3'"));
        }

        // Its own method, so nothing DAO handed out is still in a local when the engine is released.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string DefaultValueOf(object dbe, string path, string table, string field)
        {
            dynamic database = ((dynamic)dbe).OpenDatabase(path);
            try { return (string)database.TableDefs[table].Fields[field].DefaultValue; }
            finally { database.Close(); }
        }

        // ── A database that sorts in General ─────────────────────────────────────
        //
        // Access 2010 and later can make a database whose text sorts in "General" (sort order 1033, version 1), where
        // a text key is other bytes than in General - Legacy (#23). The engine keys a table
        // it creates there, and the table's name in the catalog, in that order. ACE is the referee: it finds the
        // engine's entries where it looks for them, and puts its own between them.

        // A copy of an Access 2010 database in the General order, from the engine's corpus: neither DAO's
        // CreateDatabase nor Access's default for a new database makes one.
        private string GeneralDatabase()
        {
            AccessReferee.SkipUnlessAceOpensAccess2010();
            var corpus = typeof(EngineTablesInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineCorpusDir").Value;
            var path = Path.Combine(_tmp.Dir, "general-" + Guid.NewGuid().ToString("N") + ".accdb");
            File.Copy(Path.Combine(corpus, "V2010", "common1V2010.accdb"), path);
            return path;
        }

        // Text whose keys differ between the two orders, and between each other in every part of a key: accents, a
        // character sorted as two letters, an ideograph, a surrogate pair, punctuation Access weighs apart.
        private static readonly string[] Words =
            { "résumé", "Æther", "naïve", "Zürich", "日本", "smörgåsbord", "a-b", "O'Brien", "ß", "Ελληνικά", "\U0001F600", "Łódź" };

        private static string Code(int i) { return Words[i % Words.Length] + " " + Key(i).ToString("D6"); }

        private static object Com(object target, string member, params object[] args)
        {
            return target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, args);
        }

        private static void Insert(OleDbConnection connection, string table, string code, int n)
        {
            using (var command = new OleDbCommand("INSERT INTO [" + table + "] (Code, N) VALUES (?, ?)", connection))
            {
                command.Parameters.Add("Code", OleDbType.VarWChar, 255).Value = code;
                command.Parameters.Add("N", OleDbType.Integer).Value = n;
                command.ExecuteNonQuery();
            }
        }

        private static object NumberOf(OleDbConnection connection, string table, string code)
        {
            using (var command = new OleDbCommand("SELECT N FROM [" + table + "] WHERE Code = ?", connection))
            {
                command.Parameters.Add("Code", OleDbType.VarWChar, 255).Value = code;
                return command.ExecuteScalar();
            }
        }

        /// <summary>
        /// The codes among <paramref name="codes"/> that DAO does not find by a Seek on the table's primary key:
        /// a Seek goes down the index by the key DAO makes for the value, so it finds an entry only where ACE keeps it.
        /// </summary>
        private static List<string> NotFoundBySeek(string path, string table, IEnumerable<string> codes)
        {
            var missing = new List<string>();
            var dbe = Dao.Engine;
            try
            {
                var db = Com(dbe, "OpenDatabase", path);
                try
                {
                    var rs = Com(db, "OpenRecordset", table, 1);   // dbOpenTable
                    var primary = ((System.Collections.IEnumerable)Com(Com(db, "TableDefs", table), "Indexes")).Cast<object>()
                        .Single(index => (bool)Com(index, "Primary"));
                    rs.GetType().InvokeMember("Index", BindingFlags.SetProperty, null, rs, new[] { Com(primary, "Name") });
                    foreach (var code in codes)
                    {
                        Com(rs, "Seek", "=", code);
                        if ((bool)Com(rs, "NoMatch") || !Equals(Com(Com(rs, "Fields", "Code"), "Value"), code)) missing.Add(code);
                    }
                    Com(rs, "Close");
                }
                finally { Com(db, "Close"); }
            }
            finally { Dao.Release(dbe); }
            return missing;
        }

        [Fact]
        public void ace_lists_and_reads_tables_the_engine_creates_in_a_database_that_sorts_in_General()
        {
            var path = GeneralDatabase();
            using (var db = Database.Open(path))
            {
                foreach (var name in Names)
                {
                    var table = db.CreateTable(name, Columns(Long("Id"), Text("Txt", 100)));
                    for (var i = 1; i <= 3; i++)
                        table.Insert(new Row { ["Id"] = i, ["Txt"] = "row " + i + " é" });
                }
            }

            var listed = ListedTables(path);
            Assert.All(Names, name => Assert.Contains(name, listed));
            foreach (var name in Names)
            {
                var rows = Scalar(path, "SELECT COUNT(*) FROM [" + name + "]");
                Assert.True(Convert.ToInt64(rows) == 3, name + ": " + rows + " rows, expected 3");
                var text = Scalar(path, "SELECT Txt FROM [" + name + "] WHERE Id = 2");
                Assert.True(Equals(text, "row 2 é"), name + ": read '" + text + "', expected 'row 2 é'");
            }
            // The name is already there for ACE, which looks for it by its key in the catalog.
            Assert.Contains("already exists", Assert.Throws<OleDbException>(() => Execute(path, "CREATE TABLE [Café] (Id LONG)")).Message);
        }

        [Fact]
        public void ace_seeks_and_adds_to_a_text_key_the_engine_built_in_a_database_that_sorts_in_General()
        {
            var path = GeneralDatabase();
            using (var db = Database.Open(path))
            {
                var table = db.CreateTable("Coded", Columns(Text("Code", 60), Long("N")), primaryKey: "Code");
                for (var i = 1; i <= 3000; i++)
                    table.Insert(new Row { ["Code"] = Code(i), ["N"] = i });
            }

            // Every entry the engine wrote is where ACE looks for it.
            Assert.Empty(NotFoundBySeek(path, "Coded", Enumerable.Range(1, 3000).Select(Code)));

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Equal(10, NumberOf(connection, "Coded", Code(10)));
                Assert.Equal(11, NumberOf(connection, "Coded", Code(11)));
                for (var i = 3001; i <= 5000; i++)   // between the engine's keys: ACE writes into and splits its leaves
                    Insert(connection, "Coded", Code(i), i);
                foreach (var i in Enumerable.Range(1, Words.Length))   // a key the engine wrote is one ACE finds taken
                    Assert.Contains("duplicate values", Assert.Throws<OleDbException>(() => Insert(connection, "Coded", Code(i), -1)).Message);
            }
            Assert.Equal(5000, Scalar(path, "SELECT COUNT(*) FROM Coded"));

            // The engine finds ACE's rows and its own, and adds to the pages ACE wrote and split.
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Coded");
                Assert.Equal(5000, table.Rows.Count());
                Assert.Equal(4234, table.FindRowByPrimaryKey(Code(4234))["N"]);
                Assert.Equal(17, table.FindRowByPrimaryKey(Code(17))["N"]);
                for (var i = 5001; i <= 5500; i++)
                    table.Insert(new Row { ["Code"] = Code(i), ["N"] = i });
                table.DeleteRow("Code", Code(20));
                table.DeleteRow("Code", Code(4000));
            }
            Assert.Equal(5498, Scalar(path, "SELECT COUNT(*) FROM Coded"));
            var kept = Enumerable.Range(1, 5500).Where(i => i != 20 && i != 4000).Select(Code).ToList();
            Assert.Empty(NotFoundBySeek(path, "Coded", kept));
            Assert.Equal(new[] { Code(20), Code(4000) }, NotFoundBySeek(path, "Coded", new[] { Code(20), Code(4000) }));
        }

        // Key values the engine makes no key for, or whose key a search of the index cannot be trusted with: ー, 々
        // and the Arabic shadda are keyed by the character before them, and a key longer than an entry holds is cut
        // (Jackcess and earlier versions of the engine wrote it whole). ACE writes rows with such keys; the engine
        // must find each - by scanning, as its own key for it is none or not to be trusted - and must leave a row
        // whose key it cannot make where it is.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void the_engine_finds_rows_whose_keys_only_ace_makes(bool general)
        {
            var path = general ? GeneralDatabase() : _tmp.NewMdbPath();
            using (var db = general ? Database.Open(path) : Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Coded", Columns(Text("Code", 255), Long("N")), primaryKey: "Code");
                for (var i = 1; i <= 500; i++)
                    table.Insert(new Row { ["Code"] = Code(i), ["N"] = i });
            }

            var longKey = new string('é', 255);
            string[] keyedByNeighbours = { "データ", "コンピューター", "日々", "ー", "\u0645\u062D\u0645\u0651\u062F" };   // the last: Muhammad, with a shadda
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 0; i < keyedByNeighbours.Length; i++)
                    Insert(connection, "Coded", keyedByNeighbours[i], -1 - i);
                Insert(connection, "Coded", longKey, -100);
            }

            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Coded");
                Assert.Equal(506, table.Rows.Count());
                for (var i = 0; i < keyedByNeighbours.Length; i++)
                {
                    Assert.Equal(-1 - i, table.FindRowByPrimaryKey(keyedByNeighbours[i])["N"]);
                    Assert.Equal(-1 - i, table.CreateIndexCursor().FindRow("Code", keyedByNeighbours[i])["N"]);
                    Assert.Equal(-1 - i, table.CreateIndexCursor().FindRowByKey(keyedByNeighbours[i])["N"]);
                }
                Assert.Equal(-100, table.FindRowByPrimaryKey(longKey)["N"]);
                Assert.Equal(250, table.FindRowByPrimaryKey(Code(250))["N"]);
                Assert.Null(table.FindRowByPrimaryKey("データー"));

                // In General - Legacy the engine makes no key for any of the five; in General the shadda has a key
                // of its own. A row whose key it cannot make is not deleted or rewritten with its entry left behind.
                Assert.Throws<NotSupportedException>(() => table.DeleteRow("Code", "データ"));
                Assert.Throws<NotSupportedException>(() => table.UpdateByPrimaryKey("日々", new Row { ["N"] = 7 }));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            // The long key is one the engine makes as ACE does, cut and ended in a digest: it takes the row ACE
            // wrote out of the key, and ACE no longer finds it.
            using (var db = Database.Open(path))
                db.GetTable("Coded").DeleteRow("Code", longKey);
            Assert.Equal(505, Scalar(path, "SELECT COUNT(*) FROM Coded"));
            Assert.Equal(new[] { longKey }, NotFoundBySeek(path, "Coded", new[] { longKey }));
            Assert.Empty(NotFoundBySeek(path, "Coded", keyedByNeighbours.Concat(Enumerable.Range(1, 500).Select(Code))));
        }

        [Fact]
        public void dao_compacts_a_database_that_sorts_in_General_with_the_engines_tables_and_keeps_every_row()
        {
            var path = GeneralDatabase();
            using (var db = Database.Open(path))
            {
                for (var t = 0; t < 300; t++)   // enough to split MSysObjects' ParentIdName, keyed in General
                    db.CreateTable(Words[t % Words.Length] + " " + t.ToString("D4"), Columns(Long("Id")));
                var table = db.CreateTable("Coded", Columns(Text("Code", 60), Long("N")), primaryKey: "Code");
                for (var i = 1; i <= 2000; i++)
                    table.Insert(new Row { ["Code"] = Code(i), ["N"] = i });
            }
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 2001; i <= 2500; i++)
                    Insert(connection, "Coded", Code(i), i);
            }

            var compacted = Path.Combine(_tmp.Dir, "compacted.accdb");
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Dao.Engine;
            try { dbeType.InvokeMember("CompactDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, compacted }); }
            finally { Dao.Release(dbe); }

            var listed = ListedTables(compacted);
            Assert.All(Enumerable.Range(0, 300), t => Assert.Contains(Words[t % Words.Length] + " " + t.ToString("D4"), listed));
            Assert.DoesNotContain("MSysCompactError", listed);
            Assert.Equal(2500, Scalar(compacted, "SELECT COUNT(*) FROM Coded"));
            Assert.Empty(NotFoundBySeek(compacted, "Coded", Enumerable.Range(1, 2500).Select(Code)));

            // The compacted file is ACE's own, still in General, and the engine reads and adds to it.
            using (var db = Database.Open(compacted))
            {
                var table = db.GetTable("Coded");
                Assert.Equal(2500, table.Rows.Count());
                Assert.Equal(2222, table.FindRowByPrimaryKey(Code(2222))["N"]);
                table.Insert(new Row { ["Code"] = Code(2501), ["N"] = 2501 });
            }
            Assert.Empty(NotFoundBySeek(compacted, "Coded", new[] { Code(1), Code(2500), Code(2501) }));
        }

        // ── Every text index, in either order ─────────

        private static int CountWhere(OleDbConnection connection, string table, string where, params string[] values)
        {
            using (var command = new OleDbCommand("SELECT COUNT(*) FROM [" + table + "] WHERE " + where, connection))
            {
                foreach (var value in values)
                    command.Parameters.Add("p" + command.Parameters.Count, value.Length > 255 ? OleDbType.LongVarWChar : OleDbType.VarWChar, Math.Max(1, value.Length)).Value = value;
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static void Exec(OleDbConnection connection, string sql, params object[] values)
        {
            using (var command = new OleDbCommand(sql, connection))
            {
                foreach (var value in values)
                {
                    if (value is string text) command.Parameters.Add("p" + command.Parameters.Count, text.Length > 255 ? OleDbType.LongVarWChar : OleDbType.VarWChar, Math.Max(1, text.Length)).Value = text;
                    else command.Parameters.Add("p" + command.Parameters.Count, OleDbType.Integer).Value = value;
                }
                command.ExecuteNonQuery();
            }
        }

        private sealed class Person
        {
            public string Code, Family, Given, Notes;
        }

        private static Person PersonOf(int i)
        {
            return new Person
            {
                Code   = Words[i % Words.Length] + " " + i.ToString("D5"),
                Family = Words[i % 7],
                Given  = i % 11 == 0 ? null : Words[i % 5] + " " + (i % 13),
                Notes  = i % 4 == 0 ? new string('é', 300) + (i % 6) : Words[i % Words.Length] + " note " + (i % 9),   // past a key's 255 characters, and short
            };
        }

        private static Row RowOf(int id, Person p)
        {
            return new Row { ["Id"] = id, ["Code"] = p.Code, ["Family"] = p.Family, ["Given"] = p.Given, ["Notes"] = p.Notes };
        }

        private static Table CreatePeople(Database db)
        {
            return db.CreateTable("People",
                Columns(Long("Id"), Text("Code", 60), Text("Family", 60), Text("Given", 60), new ColumnDefinition("Notes", DataType.Memo)),
                new[] { "Id" },
                new[]
                {
                    new IndexDefinition("ByCode", "Code") { IsUnique = true },
                    new IndexDefinition("ByName", "Family", "Given"),
                    new IndexDefinition("ByNotes", "Notes"),
                });
        }

        // Each condition answered from the index and from the rows ("& ''" keeps ACE off the index), and both as
        // the rows the test knows of say.
        private static void AssertAceAnswersForPeople(string path, Dictionary<int, Person> people)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Equal(people.Count, CountWhere(connection, "People", "Id >= 0"));
                foreach (var p in people.Values.Where((_, n) => n % 37 == 0).Concat(people.Values.Take(Words.Length)))
                {
                    Assert.Equal(1, CountWhere(connection, "People", "[Code] = ?", p.Code));
                    Assert.Equal(1, CountWhere(connection, "People", "[Code] & '' = ?", p.Code));

                    var family = people.Values.Count(o => o.Family == p.Family);
                    Assert.Equal(family, CountWhere(connection, "People", "[Family] = ?", p.Family));
                    Assert.Equal(family, CountWhere(connection, "People", "[Family] & '' = ?", p.Family));

                    if (p.Given != null)
                    {
                        var named = people.Values.Count(o => o.Family == p.Family && o.Given == p.Given);
                        Assert.Equal(named, CountWhere(connection, "People", "[Family] = ? AND [Given] = ?", p.Family, p.Given));
                        Assert.Equal(named, CountWhere(connection, "People", "[Family] & '' = ? AND [Given] & '' = ?", p.Family, p.Given));
                    }

                    // A Memo of more than 255 characters is asked for from the rows only: its key is cut, and ACE
                    // does not answer an equality through a cut key as its own scan does, whoever wrote the entry
                    // (ace_and_the_engine_cut_a_long_key_alike compares ACE's answers over its entries and the engine's).
                    var noted = people.Values.Count(o => o.Notes == p.Notes);
                    if (p.Notes.Length <= 255)
                        Assert.Equal(noted, CountWhere(connection, "People", "[Notes] = ?", p.Notes));
                    Assert.Equal(noted, CountWhere(connection, "People", "[Notes] & '' = ?", p.Notes));
                }
                Assert.Equal(0, CountWhere(connection, "People", "[Code] = ?", "nobody"));
            }
        }

        // A table the engine creates with a unique text index, an index on two text columns and one on a Memo, in
        // a database that sorts in General and in one that sorts in General - Legacy. After the engine inserts,
        // updates and deletes, ACE answers through each index as a scan answers; ACE adds and deletes rows of its
        // own and refuses a duplicate of a value the engine wrote; the engine goes on, and refuses a duplicate of
        // ACE's; and DAO compacts the file with every row kept.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ace_answers_through_adds_to_and_compacts_text_and_memo_indexes_the_engine_keeps(bool general)
        {
            var path = general ? GeneralDatabase() : _tmp.NewMdbPath();
            var people = new Dictionary<int, Person>();
            using (var db = general ? Database.Open(path) : Database.Create(path, FileFormat.Access2000))
            {
                var table = CreatePeople(db);
                for (var i = 1; i <= 1500; i++)
                {
                    people[i] = PersonOf(i);
                    table.Insert(RowOf(i, people[i]));
                }
                for (var i = 3; i <= 1500; i += 7)
                {
                    table.DeleteRow("Id", i);
                    people.Remove(i);
                }
                for (var i = 4; i <= 1500; i += 7)
                {
                    people[i] = PersonOf(i + 5000);
                    table.UpdateByPrimaryKey(i, RowOf(i, people[i]));
                }
            }
            AssertAceAnswersForPeople(path, people);

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 2001; i <= 2300; i++)   // between the engine's keys, in each index
                {
                    people[i] = PersonOf(i);
                    Exec(connection, "INSERT INTO People (Id, Code, Family, Given, Notes) VALUES (?, ?, ?, ?, ?)", i, people[i].Code, people[i].Family, (object)people[i].Given ?? DBNull.Value, people[i].Notes);
                }
                for (var i = 5; i <= 1500; i += 70)   // rows and entries the engine wrote
                {
                    Exec(connection, "DELETE FROM People WHERE Id = ?", i);
                    people.Remove(i);
                }
                Exec(connection, "DELETE FROM People WHERE Id = ?", 2010);
                people.Remove(2010);

                // A value the engine wrote is taken, in whatever case: the index is where ACE looks.
                var refused = Assert.Throws<OleDbException>(() => Exec(connection, "INSERT INTO People (Id, Code) VALUES (?, ?)", 9001, people[1].Code));
                Assert.Contains("duplicate values", refused.Message);
                Assert.Throws<OleDbException>(() => Exec(connection, "INSERT INTO People (Id, Code) VALUES (?, ?)", 9002, people[2].Code.ToUpperInvariant()));
            }
            AssertAceAnswersForPeople(path, people);

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("People");
                Assert.Equal(people.Count, table.Rows.Count());
                Assert.Throws<ConstraintViolationException>(() => table.Insert(RowOf(9003, new Person { Code = people[2200].Code })));          // ACE's value
                Assert.Throws<ConstraintViolationException>(() => table.Insert(RowOf(9004, new Person { Code = people[2201].Code.ToUpperInvariant() })));
                for (var i = 3001; i <= 3200; i++)   // into the pages ACE wrote and split
                {
                    people[i] = PersonOf(i);
                    table.Insert(RowOf(i, people[i]));
                }
                table.DeleteRow("Id", 2100);          // a row and entries ACE wrote
                people.Remove(2100);
                people[2101] = PersonOf(7101);
                table.UpdateByPrimaryKey(2101, RowOf(2101, people[2101]));
            }
            AssertAceAnswersForPeople(path, people);

            var compacted = Path.Combine(_tmp.Dir, "people-compacted" + Path.GetExtension(path));
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Dao.Engine;
            try { dbeType.InvokeMember("CompactDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, compacted }); }
            finally { Dao.Release(dbe); }
            Assert.DoesNotContain("MSysCompactError", ListedTables(compacted));
            AssertAceAnswersForPeople(compacted, people);
        }

        // Keys longer than an entry holds. Access keeps 510 bytes of a key and ends it in a digest of the rest,
        // whether the key is one column's, a Memo's or several columns' together, and the engine writes the same
        // entry. Two files are made alike, row for row: in one the engine creates the table and writes the first
        // rows, in the other ACE does; ACE then adds the same rows to both.
        //   • DAO seeks the same rows in both files by both columns of an index whose key is some 800 bytes whole:
        //     all of them in General - Legacy.
        //   • ACE gives the same answer to each question in both files, through each index. (It is not always
        //     the answer a scan gives: asked for both columns of a cut key, or in General for the first alone,
        //     ACE misses or miscounts rows it wrote itself. The engine's entries make it answer no otherwise.)
        //   • The engine takes ACE's entries out by the keys it makes, and ACE in the other file the same rows.
        //   • Two values that differ only where the key is cut, and whose digests are the same, are one entry:
        //     ACE refuses the second in a unique index as a duplicate, and so does the engine, each of the other's.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ace_and_the_engine_cut_a_long_key_alike_and_call_two_values_cut_alike_duplicates(bool general)
        {
            Func<int, string> a = i => new string('é', 200) + " " + (i % 5);
            Func<int, string> b = i => new string('è', 200) + " " + i.ToString("D3");
            Func<int, string> m = i => new string('é', 300) + " " + (i % 4);
            var start = new string('Æ', 100) + new string('é', 115);
            string first = start + "èèèéèêéèéèëëéëêèèèêêèëéééëëëèêêéèëééëëéè", second = start + "èèëêèééêèèëêéêéëéêêéêêéèéèëèèééèëêéëéééé";
            const string insert = "INSERT INTO Wide (Id, A, B, U, M) VALUES (?, ?, ?, ?, ?)";

            // The engine's file.
            var path = general ? GeneralDatabase() : _tmp.NewMdbPath();
            using (var db = general ? Database.Open(path) : Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Wide", Columns(Long("Id"), Text("A", 255), Text("B", 255), Text("U", 255), new ColumnDefinition("M", DataType.Memo)),
                    new[] { "Id" },
                    new[] { new IndexDefinition("ByAB", "A", "B"), new IndexDefinition("ByU", "U") { IsUnique = true }, new IndexDefinition("ByM", "M") });
                for (var i = 1; i <= 40; i++)
                    table.Insert(new Row { ["Id"] = i, ["A"] = a(i), ["B"] = b(i), ["U"] = "u " + i, ["M"] = m(i) });
                table.Insert(new Row { ["Id"] = 100, ["A"] = "x", ["B"] = "y", ["U"] = first, ["M"] = "m" });
                Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 101, ["A"] = "x", ["B"] = "y", ["U"] = second, ["M"] = "m" }));
            }

            // ACE's file: the same table and rows, all ACE's.
            var twin = general ? GeneralDatabase() : _tmp.NewMdbPath();
            if (!general) Database.Create(twin, FileFormat.Access2000).Dispose();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(twin)))
            {
                connection.Open();
                Exec(connection, "CREATE TABLE Wide (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, A TEXT(255), B TEXT(255), U TEXT(255), M MEMO)");
                Exec(connection, "CREATE INDEX ByAB ON Wide (A, B)");
                Exec(connection, "CREATE UNIQUE INDEX ByU ON Wide (U)");
                Exec(connection, "CREATE INDEX ByM ON Wide (M)");
                for (var i = 1; i <= 40; i++)
                    Exec(connection, insert, i, a(i), b(i), "u " + i, m(i));
                Exec(connection, insert, 100, "x", "y", first, "m");
            }

            foreach (var file in new[] { path, twin })
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(file)))
                {
                    connection.Open();
                    for (var i = 41; i <= 80; i++)
                        Exec(connection, insert, i, a(i), b(i), "u " + i, m(i));

                    // The value in the index is taken, and so is the other value that is cut to its entry.
                    Assert.Contains("duplicate values", Assert.Throws<OleDbException>(() => Exec(connection, insert, 102, "x", "y", first, "m")).Message);
                    Assert.Contains("duplicate values", Assert.Throws<OleDbException>(() => Exec(connection, insert, 103, "x", "y", second, "m")).Message);
                }

            Func<string, List<string>> answers = file =>
            {
                var given = new List<string>();
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(file)))
                {
                    connection.Open();
                    given.Add("rows " + CountWhere(connection, "Wide", "Id >= 0"));
                    foreach (var i in new[] { 1, 2, 17, 40, 41, 45, 66, 80 })
                    {
                        // From the rows: what is there.
                        given.Add(i + " rows: " + CountWhere(connection, "Wide", "[A] & '' = ? AND [B] & '' = ?", a(i), b(i))
                                    + " " + CountWhere(connection, "Wide", "[A] & '' = ?", a(i)) + " " + CountWhere(connection, "Wide", "[M] & '' = ?", m(i)));
                        // Through the indexes: what ACE makes of the entries.
                        given.Add(i + " index: " + CountWhere(connection, "Wide", "[A] = ? AND [B] = ?", a(i), b(i))
                                    + " " + CountWhere(connection, "Wide", "[A] = ?", a(i)) + " " + CountWhere(connection, "Wide", "[B] = ?", b(i))
                                    + " " + CountWhere(connection, "Wide", "[M] = ?", m(i)) + " " + CountWhere(connection, "Wide", "[U] = ?", "u " + i)
                                    + " " + CountWhere(connection, "Wide", "[A] >= ? AND [A] <= ?", a(i), a(i) + "z"));
                    }
                    given.Add("cut alike: " + CountWhere(connection, "Wide", "[U] = ?", first) + " " + CountWhere(connection, "Wide", "[U] = ?", second)
                                    + " " + CountWhere(connection, "Wide", "[U] & '' = ?", first) + " " + CountWhere(connection, "Wide", "[U] & '' = ?", second));
                }
                return given;
            };
            var fromAce = answers(twin);
            Assert.Equal(fromAce, answers(path));
            Assert.Contains("1 rows: 1 16 20", fromAce);
            Assert.Contains("41 rows: 1 16 20", fromAce);

            // DAO goes down the index by the key it makes of both values: it finds an entry only where it is ACE's.
            // In General - Legacy it finds every row; in General it misses some of ACE's own, and the same of the engine's.
            var all = Enumerable.Range(1, 80).Select(i => Tuple.Create(a(i), b(i))).ToList();
            var notSought = RowsNotSoughtByTwo(twin, "Wide", "ByAB", all);
            Assert.Equal(notSought, RowsNotSoughtByTwo(path, "Wide", "ByAB", all));
            if (!general) Assert.Empty(notSought);

            // The same rows go and change in both files: by the engine's keys in one, by ACE in the other.
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Wide");
                Assert.Equal(81, table.Rows.Count());
                for (var i = 2; i <= 80; i += 3)       // its own entries and ACE's
                    table.DeleteRow("Id", i);
                table.UpdateByPrimaryKey(45, new Row { ["B"] = b(945), ["M"] = m(946) });
                table.DeleteRow("Id", 100);
                table.Insert(new Row { ["Id"] = 101, ["A"] = "x", ["B"] = "y", ["U"] = second, ["M"] = "m" });   // the entry is free again
            }
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(twin)))
            {
                connection.Open();
                for (var i = 2; i <= 80; i += 3)
                    Exec(connection, "DELETE FROM Wide WHERE Id = ?", i);
                Exec(connection, "UPDATE Wide SET B = ?, M = ? WHERE Id = 45", b(945), m(946));
                Exec(connection, "DELETE FROM Wide WHERE Id = ?", 100);
                Exec(connection, insert, 101, "x", "y", second, "m");
            }

            var gone = Enumerable.Range(1, 80).Where(i => i % 3 == 2).ToList();
            fromAce = answers(twin);
            Assert.Equal(fromAce, answers(path));
            Assert.Contains("rows " + (81 - gone.Count), fromAce);
            Assert.Contains("2 rows: 0 10 14", fromAce);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Equal(1, CountWhere(connection, "Wide", "[A] & '' = ? AND [B] & '' = ?", a(45), b(945)));
                Assert.Contains("duplicate values", Assert.Throws<OleDbException>(() => Exec(connection, insert, 104, "x", "y", first, "m")).Message);
            }
            var kept = Enumerable.Range(1, 80).Where(i => i % 3 != 2 && i != 45).Select(i => Tuple.Create(a(i), b(i))).Concat(new[] { Tuple.Create(a(45), b(945)) }).ToList();
            var sought = kept.Concat(gone.Select(i => Tuple.Create(a(i), b(i)))).ToList();
            notSought = RowsNotSoughtByTwo(twin, "Wide", "ByAB", sought);
            Assert.Equal(notSought, RowsNotSoughtByTwo(path, "Wide", "ByAB", sought));
            if (!general) Assert.Equal(gone.Select(i => Tuple.Create(a(i), b(i))), notSought);   // only the rows that are gone

            var compacted = Path.Combine(_tmp.Dir, "wide-compacted" + Path.GetExtension(path));
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Dao.Engine;
            try { dbeType.InvokeMember("CompactDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, compacted }); }
            finally { Dao.Release(dbe); }
            Assert.DoesNotContain("MSysCompactError", ListedTables(compacted));
            Assert.Equal(81 - gone.Count, Scalar(compacted, "SELECT COUNT(*) FROM Wide"));
        }
        // ── Long keys an earlier release of the engine wrote whole (review of #10) ──
        //
        // The engine cut no key before it wrote keys in either order: a file it wrote then holds keys of more than
        // 510 bytes whole, where Access - and the engine now - holds them cut and ended in a digest. The engine's
        // tests have such a file, written by the engine at its commit 696ca52. Here the engine of today deletes
        // from it, updates in it and adds to it - taking each whole entry out by the key it is stored under, and
        // writing cut ones - and ACE referees the result against a file of the same rows that ACE alone wrote:
        // the same answers through the primary keys, the same rows found by a DAO Seek, the same duplicates
        // refused, and a compaction that keeps every row.
        [Fact]
        public void ace_reads_what_the_engine_makes_of_a_file_whose_long_keys_an_earlier_release_wrote_whole()
        {
            var assets = typeof(EngineTablesInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineAssetsDir").Value;
            var path = _tmp.NewMdbPath();
            File.Copy(Path.Combine(assets, "engine-696ca52-long-keys.mdb"), path, overwrite: true);

            var long255 = new string('é', 255);
            var long254e = new string('é', 254) + "e";
            var start = new string('Æ', 100) + new string('é', 115);
            string cutAlike1 = start + "èèèéèêéèéèëëéëêèèèêêèëéééëëëèêêéèëééëëéè", cutAlike2 = start + "èèëêèééêèèëêéêéëéêêéêêéèéèëèèééèëêéëéééé";
            var longAe = new string('Æ', 100) + new string('é', 155);
            var longU = new string('ü', 255);
            Func<int, string> pairA = i => new string('é', 200) + " " + (i % 3);
            Func<int, string> pairB = i => new string('è', 200) + " " + i.ToString("D2");

            // As the earlier release left it, the file is one ACE does not seek in: it looks for the cut key, and
            // the entry is whole. The row is there for a scan, and for the engine. Only a rewrite mends that, so
            // every row with a long key is rewritten or deleted below before ACE is asked to agree with its own file.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Equal(0, CountWhere(connection, "Coded", "[Code] = ?", cutAlike1));
                Assert.Equal(1, CountWhere(connection, "Coded", "[Code] & '' = ?", cutAlike1));
                Assert.Equal(1, CountWhere(connection, "Coded", "[Code] = ?", "short 07"));
            }

            List<Row> coded, paired;
            string pairedKey;
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Coded");
                Assert.Equal(25, table.Rows.Count());
                Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Code"] = long255.ToUpperInvariant(), ["N"] = 99 }));   // held whole, and seen
                table.DeleteRow("Code", long255);                                       // a whole entry goes with its row
                table.DeleteRow("Code", cutAlike2);                                     // one of two rows cut to one entry
                table.UpdateByPrimaryKey(long254e, new Row { ["N"] = 220 });            // another column: the entry is cut now
                table.UpdateByPrimaryKey(longAe, new Row { ["Code"] = longU });         // the key, to another long one
                table.UpdateByPrimaryKey("short 03", new Row { ["Code"] = long255 });   // the value is free again
                table.Insert(new Row { ["Code"] = new string('é', 254) + "x", ["N"] = 26 });
                table.UpdateByPrimaryKey(cutAlike1, new Row { ["N"] = 230 });           // the last whole key of the table

                var pairs = db.GetTable("Paired");
                pairedKey = pairs.Indexes.Single().Name;
                Assert.Throws<ConstraintViolationException>(() => pairs.Insert(new Row { ["A"] = pairA(4), ["B"] = pairB(4), ["N"] = 99 }));
                for (var i = 1; i <= 12; i++)             // every whole key of two columns goes with its row
                    pairs.DeleteRow("N", i);
                for (var i = 2; i <= 12; i += 2)          // and half come back, cut
                    pairs.Insert(new Row { ["A"] = pairA(i), ["B"] = pairB(i), ["N"] = i });
                pairs.Insert(new Row { ["A"] = pairA(4), ["B"] = pairB(13), ["N"] = 13 });

                coded = table.Rows.ToList();
                paired = pairs.Rows.ToList();
            }
            Assert.Equal(24, coded.Count);
            Assert.Equal(9, paired.Count);

            // The same rows, written by ACE alone.
            var twin = _tmp.NewMdbPath();
            Database.Create(twin, FileFormat.Access2000).Dispose();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(twin)))
            {
                connection.Open();
                Exec(connection, "CREATE TABLE Coded (Code TEXT(255) CONSTRAINT [" + pairedKey + "] PRIMARY KEY, N LONG)");
                Exec(connection, "CREATE TABLE Paired (A TEXT(255), B TEXT(255), N LONG, CONSTRAINT [" + pairedKey + "] PRIMARY KEY (A, B))");
                foreach (var row in coded)
                    Exec(connection, "INSERT INTO Coded (Code, N) VALUES (?, ?)", (string)row["Code"], (int)row["N"]);
                foreach (var row in paired)
                    Exec(connection, "INSERT INTO Paired (A, B, N) VALUES (?, ?, ?)", (string)row["A"], (string)row["B"], (int)row["N"]);
            }

            var codes = coded.Select(r => (string)r["Code"]).Concat(new[] { cutAlike2, longAe, "short 03", "nobody" }).ToList();
            var pairsAsked = Enumerable.Range(1, 13).Select(i => Tuple.Create(pairA(i), pairB(i))).Concat(new[] { Tuple.Create("a", "b"), Tuple.Create("a", "c") }).ToList();
            Func<string, List<string>> answers = file =>
            {
                var given = new List<string>();
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(file)))
                {
                    connection.Open();
                    given.Add("rows " + CountWhere(connection, "Coded", "N >= 0") + " " + CountWhere(connection, "Paired", "N >= 0"));
                    foreach (var code in codes)
                        given.Add("Coded " + (code.Length > 20 ? code.Substring(0, 3) + code.Substring(250) : code) + ": index "
                                  + CountWhere(connection, "Coded", "[Code] = ?", code) + ", rows " + CountWhere(connection, "Coded", "[Code] & '' = ?", code));
                    foreach (var pair in pairsAsked)
                        given.Add("Paired " + pair.Item1.Substring(pair.Item1.Length - 1) + "/" + pair.Item2.Substring(Math.Max(0, pair.Item2.Length - 2)) + ": index "
                                  + CountWhere(connection, "Paired", "[A] = ? AND [B] = ?", pair.Item1, pair.Item2) + " " + CountWhere(connection, "Paired", "[A] = ?", pair.Item1)
                                  + ", rows " + CountWhere(connection, "Paired", "[A] & '' = ? AND [B] & '' = ?", pair.Item1, pair.Item2));
                    // A value in the key is taken, in ACE's file and in the engine's.
                    given.Add("duplicates: " + (Refused(connection, "INSERT INTO Coded (Code, N) VALUES (?, ?)", long254e, 99) ? "refused" : "taken")
                              + " " + (Refused(connection, "INSERT INTO Coded (Code, N) VALUES (?, ?)", cutAlike2, 99) ? "refused" : "taken")
                              + " " + (Refused(connection, "INSERT INTO Paired (A, B, N) VALUES (?, ?, ?)", pairA(4), pairB(4), 99) ? "refused" : "taken"));
                }
                return given;
            };
            var fromAce = answers(twin);
            Assert.Equal(fromAce, answers(path));
            Assert.Contains("rows 24 9", fromAce);
            Assert.Contains("duplicates: refused refused refused", fromAce);
            Assert.Contains("Coded short 07: index 1, rows 1", fromAce);

            // DAO goes down each key by the key it makes: every row is found, and the rows that went are not.
            var gone = new[] { cutAlike2, longAe, "short 03", "nobody" };
            Assert.Equal(gone, NotFoundBySeek(twin, "Coded", codes));
            Assert.Equal(gone, NotFoundBySeek(path, "Coded", codes));
            Assert.Equal(RowsNotSoughtByTwo(twin, "Paired", pairedKey, pairsAsked), RowsNotSoughtByTwo(path, "Paired", pairedKey, pairsAsked));

            var compacted = Path.Combine(_tmp.Dir, "whole-keys-compacted.mdb");
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Dao.Engine;
            try { dbeType.InvokeMember("CompactDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, compacted }); }
            finally { Dao.Release(dbe); }
            Assert.DoesNotContain("MSysCompactError", ListedTables(compacted));
            Assert.Equal(fromAce, answers(compacted));
        }

        private static bool Refused(OleDbConnection connection, string sql, params object[] values)
        {
            try { Exec(connection, sql, values); return false; }
            catch (OleDbException refused) when (refused.Message.Contains("duplicate values")) { return true; }
        }
        /// <summary>The pairs DAO does not find by a Seek on a two-column index.</summary>
        private static List<Tuple<string, string>> RowsNotSoughtByTwo(string path, string table, string index, IEnumerable<Tuple<string, string>> pairs)
        {
            var missing = new List<Tuple<string, string>>();
            var dbe = Dao.Engine;
            try
            {
                var db = Com(dbe, "OpenDatabase", path);
                try
                {
                    var rs = Com(db, "OpenRecordset", table, 1);   // dbOpenTable
                    rs.GetType().InvokeMember("Index", BindingFlags.SetProperty, null, rs, new object[] { index });
                    foreach (var pair in pairs)
                    {
                        Com(rs, "Seek", "=", pair.Item1, pair.Item2);
                        if ((bool)Com(rs, "NoMatch") || !Equals(Com(Com(rs, "Fields", "B"), "Value"), pair.Item2)) missing.Add(pair);
                    }
                    Com(rs, "Close");
                }
                finally { Com(db, "Close"); }
            }
            finally { Dao.Release(dbe); }
            return missing;
        }
    }
}
