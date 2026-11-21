using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Password-protected and encoded files, with Access as the referee (#46). DAO makes each
    /// kind it can - an Access 2000-2003 file with a database password, one encoded (dbEncrypt), one with both, and an
    /// .accdb with a password in each format, which ACE encrypts with Agile Encryption - and fills a table of it. The
    /// engine opens each as Access does (a password exactly, letter case and all; an encoded file with any password or
    /// none) and reads every row as DAO reads it; then it inserts, updates and deletes rows, makes a table of long
    /// values and imports another, and DAO reads every row as the engine was given it, ACE finds each through the
    /// primary key, DAO adds a row, compacts the file and reads it again, and the engine reads what DAO wrote.
    /// <para>
    /// The kinds DAO on this machine does not make - Access 2007's RC4 CryptoAPI and the non-standard AES Jackcess-encrypt
    /// reads, and files Access itself made - are jackcess-encrypt's test data: set JACKCESS_ENCRYPT_FIXTURES to its
    /// src/test/data folder to referee writes to them as well; without it those tests are skipped.
    /// </para>
    /// </summary>
    public class ProtectedFilesInAccessTests : IDisposable
    {
        /// <summary>P, a-umlaut, ssw, the euro sign, rd7: two characters past ASCII, all in code page 1252.</summary>
        private const string Password = "Pässw€rd7";

        private readonly TempDb _tmp = new TempDb();

        public void Dispose() { _tmp.Dispose(); }

        public static IEnumerable<object[]> Kinds()
        {
            yield return new object[] { "a database password", 64, true, false, ".mdb" };
            yield return new object[] { "encoded", 64, false, true, ".mdb" };
            yield return new object[] { "a database password, encoded", 64, true, true, ".mdb" };
            yield return new object[] { "the Access 2007 format asked for, a password", 128, true, false, ".accdb" };
            yield return new object[] { "the Access 2010 format, a password", 256, true, false, ".accdb" };
        }

        private static string Text(int length, int seed)
        {
            var text = new char[length];
            for (var i = 0; i < length; i++)
                text[i] = seed % 3 == 0 && i % 7 == 0 ? (char)(0x3A9 + (i + seed) % 20) : (char)('a' + (i * 7 + seed) % 26);
            return new string(text);
        }

        private static byte[] Bytes(int length, int seed)
        {
            return Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();
        }

        // Every length a long value is kept differently at: in the row, in one chunk, in a chain of several.
        private static readonly int[] Lengths = { 0, 40, 64, 65, 1000, 4000, 9000 };

        private static Values Row(int id, int seed)
        {
            int length = Lengths[(id + seed) % Lengths.Length];
            return V("Id", id, "Name", "row " + id, "Notes", length == 0 ? null : Text(length, seed), "Data", length == 0 ? null : Bytes(length * 2, seed),
                     "Amount", id * 1.25m, "Ratio", id / 7.0, "Stamp", new DateTime(2026, 1, 1).AddDays(id).AddMinutes(id), "Flag", id % 2 == 0);
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void the_engine_reads_and_writes_a_protected_file_access_made_as_access_does(string kind, int version, bool withPassword, bool encoded, string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            string password = withPassword ? Password : null;
            var rows = new Dictionary<int, Values>();
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = dbe.CreateDatabase(path, ";LANGID=0x0409;CP=1252;COUNTRY=0" + (withPassword ? ";pwd=" + Password : ""), version + (encoded ? 2 : 0));
                try
                {
                    db.Execute("CREATE TABLE P (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Name TEXT(50), Notes MEMO, Data LONGBINARY, Amount CURRENCY, Ratio DOUBLE, Stamp DATETIME, Flag YESNO)");
                    dynamic recordset = db.OpenRecordset("P", 1 /* dbOpenTable */);
                    for (var id = 1; id <= 40; id++)
                    {
                        var row = Row(id, 0);
                        recordset.AddNew();
                        foreach (var pair in row)
                            if (pair.Value != null) recordset.Fields[pair.Key].Value = pair.Value;
                        recordset.Update();
                        rows[id] = row;
                    }
                    recordset.Close();
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }

            // Opened as Access opens it.
            if (withPassword)
            {
                Assert.Throws<InvalidCredentialException>(() => Database.Open(path));
                foreach (var other in new[] { "wrong", Password.ToUpperInvariant(), Password.Substring(1), Password + " " })
                    Assert.Throws<InvalidCredentialException>(() => Database.Open(path, new DatabaseOpenOptions { Password = other }));
            }
            else
            {
                using (Database.Open(path)) { }
                using (Database.Open(path, new DatabaseOpenOptions { Password = "anything" })) { }
            }

            // Read as DAO reads it.
            AssertSame(Written(rows.Values), Written(DaoTable(path, password, "P")), kind + ": P as DAO reads it");
            AssertSame(Written(rows.Values), Written(EngineTable(path, password, "P")), kind + ": P as the engine reads it");

            // Written by the engine: rows inserted, updated and deleted, a table of long values, an imported one.
            var made = new List<Values>();
            using (var db = Open(path, password))
            {
                var p = db.GetTable("P");
                for (var id = 101; id <= 130; id++)
                {
                    rows[id] = Row(id, 5);
                    p.Insert(EngineRow(rows[id]));
                }
                foreach (var id in new[] { 3, 17, 105 })
                {
                    var changes = V("Name", "changed " + id, "Notes", Text(5000 + id, id));
                    p.UpdateByPrimaryKey(id, EngineRow(changes));
                    foreach (var pair in changes) rows[id][pair.Key] = pair.Value;
                }
                foreach (var id in new[] { 2, 40, 110 })
                {
                    p.DeleteRow("Id", id);
                    rows.Remove(id);
                }

                var longs = db.CreateTable("L", new[] { new ColumnDefinition("K", DataType.Int32), new ColumnDefinition("V", DataType.Memo), new ColumnDefinition("B", DataType.OleObject) }, "K");
                for (var k = 1; k <= 20; k++)
                {
                    made.Add(V("K", k, "V", Text(3000 * k, k), "B", Bytes(2500 * k, k)));
                    longs.Insert(EngineRow(made[k - 1]));
                }

                var imported = new DataTable("Imported");
                imported.Columns.Add("Id", typeof(int));
                imported.Columns.Add("Price", typeof(decimal));
                imported.PrimaryKey = new[] { imported.Columns["Id"] };
                imported.Rows.Add(1, 1.23456m);
                imported.Rows.Add(2, -98765.4321m);
                db.ImportTable(imported);
            }

            AssertSame(Written(rows.Values), Written(DaoTable(path, password, "P")), kind + ": P as DAO reads it after the engine wrote");
            AssertSame(Written(made), Written(DaoTable(path, password, "L")), kind + ": L as DAO reads it");
            AssertSame(Written(new[] { V("Id", 1, "Price", 1.23456m), V("Id", 2, "Price", -98765.4321m) }), Written(DaoTable(path, password, "Imported")),
                       kind + ": Imported as DAO reads it");
            foreach (var id in rows.Keys)
                AssertSame(Written(new[] { rows[id] }), Written(AceQuery(path, password, "SELECT * FROM P WHERE Id = " + id)), kind + ": the row ACE finds through the key for Id " + id);
            Assert.Equal(rows.Count, DaoSeekCount(path, password, "P", rows.Keys));

            // Access goes on with it: adds a row, compacts the file, and the engine reads what it did.
            rows[999] = V("Id", 999, "Name", "dao's", "Notes", Text(7000, 999), "Data", null, "Amount", 9.99m, "Ratio", 0.5, "Stamp", new DateTime(2026, 11, 19), "Flag", true);
            DaoAdd(path, password, "P", rows[999]);
            AssertSame(Written(rows.Values), Written(EngineTable(path, password, "P")), kind + ": P as the engine reads it after DAO added a row");
            var compacted = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCompact(path, compacted, password);
            AssertSame(Written(rows.Values), Written(DaoTable(compacted, password, "P")), kind + ": P after DAO compacted the file");
            AssertSame(Written(made), Written(EngineTable(compacted, password, "L")), kind + ": L as the engine reads the compacted file");
        }

        /// <summary>
        /// Files of the kinds DAO here does not make, from jackcess-encrypt's test data: what the engine writes to each is
        /// read by DAO, found through the key, and compacted.
        /// </summary>
        [Theory]
        [InlineData("db2007-oldenc.accdb", "Test123", "Table1")]       // RC4 CryptoAPI, Access 2007's own encryption
        [InlineData("db-nonstandard.accdb", "password", "Table_One")]   // AES with no hash iterations
        [InlineData("db2007-enc.accdb", "Test123", "Table1")]           // Agile, from Access
        [InlineData("db2013-enc.accdb", "1234", "Customers")]           // Agile, from Access 2013
        [InlineData("db-enc.mdb", null, "Table1")]                      // an Access 2000 file Access encoded
        [InlineData("pwd-set.mdb", "testpassword", null)]               // an Access 2000 file with a database password Access set
        public void what_the_engine_writes_to_a_protected_file_access_itself_made_is_read_by_access(string fixture, string password, string table)
        {
            var root = Environment.GetEnvironmentVariable("JACKCESS_ENCRYPT_FIXTURES");
            var source = root == null ? null : Path.Combine(root, fixture);
            if (source == null || !File.Exists(source))
                Assert.Skip(fixture + " is jackcess-encrypt's test data: set JACKCESS_ENCRYPT_FIXTURES to its src/test/data folder.");
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + Path.GetExtension(fixture));
            File.Copy(source, path);

            List<Values> before = null;
            if (table != null)
            {
                before = DaoTable(path, password, table);
                AssertSame(Written(before), Written(EngineTable(path, password, table)), fixture + ": " + table + " as the engine reads it");
            }

            var made = new List<Values>();
            using (var db = Open(path, password))
            {
                var longs = db.CreateTable("Made", new[] { new ColumnDefinition("K", DataType.Int32), new ColumnDefinition("V", DataType.Memo) }, "K");
                for (var k = 1; k <= 30; k++)
                {
                    made.Add(V("K", k, "V", Text(1500 * k, k)));
                    longs.Insert(EngineRow(made[k - 1]));
                }
            }

            AssertSame(Written(made), Written(DaoTable(path, password, "Made")), fixture + ": Made as DAO reads it");
            if (before != null) AssertSame(Written(before), Written(DaoTable(path, password, table)), fixture + ": " + table + " as DAO reads it after the engine wrote");
            Assert.Equal(made.Count, DaoSeekCount(path, password, "Made", made.Select(r => (int)r["K"]), key: "K"));
            var compacted = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + Path.GetExtension(fixture));
            DaoCompact(path, compacted, password);
            AssertSame(Written(made), Written(DaoTable(compacted, password, "Made")), fixture + ": Made after DAO compacted the file");
        }

        /// <summary>
        /// A file Access has open is refused to the engine opening it to write: with an ACE connection open on it,
        /// Database.Open throws DatabaseInUseException, with options or without, and the file is left as it was. Let in, the
        /// engine was a second writer: ACE lost rows it wrote, and found others through the key.
        /// </summary>
        [Fact]
        public void a_file_ace_has_open_is_refused_to_the_engine_and_left_as_it_was()
        {
            SkipUnlessAceOpensAccess2010();
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + ".accdb");
            using (var db = Database.Create(path, FileFormat.Access2010))
                db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id").Insert(EngineRow(V("Id", 1)));

            using (var ace = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                ace.Open();
                using (var count = new OleDbCommand("SELECT COUNT(*) FROM T", ace))
                    Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar()));
                var before = SharedBytes(path);
                Assert.Throws<DatabaseInUseException>(() => Database.Open(path).Dispose());
                Assert.Throws<DatabaseInUseException>(() => Database.Open(path, new DatabaseOpenOptions { Password = "not looked at" }).Dispose());
                Assert.Throws<DatabaseInUseException>(() => Database.Open(path, new DatabaseOpenOptions { FallbackFormat = FileFormat.Access2010 }).Dispose());
                Assert.Equal(before, SharedBytes(path));
            }

            // ACE gone, the engine opens the file.
            using (var db = Database.Open(path))
                Assert.Equal(1, db.GetTable("T").RowCount);
        }

        // The file's bytes, read as a reader that lets ACE go on holding it.
        private static byte[] SharedBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                return bytes;
            }
        }

        /// <summary>
        /// An encrypted file the engine grows past 65,536 pages, where a page's number no longer fits in two bytes - an
        /// .mdb with a password, encoded, and an .accdb ACE encrypts with Agile Encryption: DAO reads the last rows as the
        /// engine wrote them, and finds them through the key; it compacts the file and reads them again; and the engine
        /// reads the compacted file. Each file grows past 270 MB, so the test runs only when JACKCESS_PAST_65536_PAGES is
        /// set; the engine's ProtectedFileTests grows its own fixtures under the same variable.
        /// </summary>
        [Theory]
        [InlineData(64, true, ".mdb")]
        [InlineData(256, false, ".accdb")]
        public void access_reads_and_compacts_an_encrypted_file_the_engine_grew_past_65536_pages(int version, bool encoded, string extension)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JACKCESS_PAST_65536_PAGES")))
                Assert.Skip("Grows a file past 270 MB: set JACKCESS_PAST_65536_PAGES to run it.");
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try { dbe.CreateDatabase(path, ";LANGID=0x0409;CP=1252;COUNTRY=0;pwd=" + Password, version + (encoded ? 2 : 0)).Close(); }
            finally { Dao.Release(dbe); }

            var last = 0;
            using (var db = Open(path, Password))
            {
                var big = db.CreateTable("Big", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Blob", DataType.OleObject) }, "Id");
                while (new FileInfo(path).Length < (65536L + 400) * 4096)
                {
                    var rows = new List<Row>(1000);
                    for (var i = 0; i < 1000; i++)
                    {
                        last++;
                        rows.Add(EngineRow(V("Id", last, "Blob", Blob(last))));
                    }
                    big.InsertRange(rows);
                }
            }

            var lastRows = Enumerable.Range(last - 49, 50).Select(id => V("Id", id, "Blob", Blob(id))).ToList();
            var sql = "SELECT Id, Blob FROM Big WHERE Id > " + (last - 50) + " ORDER BY Id";
            AssertSame(Written(lastRows), Written(DaoQuery(path, Password, sql)), "the last rows as DAO reads them");
            Assert.Equal(50, DaoSeekCount(path, Password, "Big", lastRows.Select(r => (int)r["Id"])));
            var compacted = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCompact(path, compacted, Password);
            AssertSame(Written(lastRows), Written(DaoQuery(compacted, Password, sql)), "the last rows after DAO compacted the file");
            using (var db = Open(compacted, Password))
            {
                var big = db.GetTable("Big");
                Assert.Equal(last, big.RowCount);
                foreach (var id in Enumerable.Range(last - 49, 50))
                    Assert.Equal(Blob(id), (byte[])big.FindRowByPrimaryKey(id)["Blob"]);
            }
        }

        // A value about a page long, different for every row, its id in its first four bytes.
        private static byte[] Blob(int id)
        {
            var bytes = new byte[3000];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(id * 31 + i * 7);
            BitConverter.GetBytes(id).CopyTo(bytes, 0);
            return bytes;
        }

        private static Database Open(string path, string password)
        {
            return password == null ? Database.Open(path) : Database.Open(path, new DatabaseOpenOptions { Password = password });
        }

        private static List<Values> EngineTable(string path, string password, string table)
        {
            using (var db = Open(path, password))
            {
                var opened = db.GetTable(table);
                return opened.Rows.Select(row =>
                {
                    var values = new Values();
                    foreach (var column in opened.Columns)
                        values[column.Name] = row[column.Name];
                    return values;
                }).ToList();
            }
        }

        private static dynamic DaoOpen(dynamic dbe, string path, string password, bool readOnly)
        {
            return dbe.OpenDatabase(path, false, readOnly, password == null ? "" : ";pwd=" + password);
        }

        /// <summary>Every row of <paramref name="table"/> as DAO reads it, a zero-length binary value as DAO hands it out: Null.</summary>
        private static List<Values> DaoTable(string path, string password, string table)
        {
            return DaoQuery(path, password, "SELECT * FROM [" + table + "]");
        }

        /// <summary>The rows DAO reads with <paramref name="sql"/>, a zero-length binary value as DAO hands it out: Null.</summary>
        private static List<Values> DaoQuery(string path, string password, string sql)
        {
            var rows = new List<Values>();
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = DaoOpen(dbe, path, password, readOnly: true);
                try
                {
                    dynamic recordset = db.OpenRecordset(sql, 4 /* dbOpenSnapshot */);
                    try
                    {
                        int fields = recordset.Fields.Count;
                        while (!recordset.EOF)
                        {
                            var row = new Values();
                            for (var i = 0; i < fields; i++)
                            {
                                dynamic field = recordset.Fields[i];
                                object value = field.Value;
                                row[(string)field.Name] = value is DBNull ? null : value;
                            }
                            rows.Add(row);
                            recordset.MoveNext();
                        }
                    }
                    finally { recordset.Close(); }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return rows;
        }

        /// <summary>How many of <paramref name="ids"/> DAO's Seek finds through the primary key of <paramref name="table"/>.</summary>
        private static int DaoSeekCount(string path, string password, string table, IEnumerable<int> ids, string key = "Id")
        {
            int found = 0;
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = DaoOpen(dbe, path, password, readOnly: true);
                try
                {
                    dynamic recordset = db.OpenRecordset(table, 1 /* dbOpenTable */);
                    try
                    {
                        recordset.Index = "PrimaryKey";
                        foreach (var id in ids)
                        {
                            recordset.Seek("=", id);
                            if (!(bool)recordset.NoMatch && (int)recordset.Fields[key].Value == id) found++;
                        }
                    }
                    finally { recordset.Close(); }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return found;
        }

        private static void DaoAdd(string path, string password, string table, Values row)
        {
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = DaoOpen(dbe, path, password, readOnly: false);
                try
                {
                    dynamic recordset = db.OpenRecordset(table, 1 /* dbOpenTable */);
                    recordset.AddNew();
                    foreach (var pair in row)
                        if (pair.Value != null) recordset.Fields[pair.Key].Value = pair.Value;
                    recordset.Update();
                    recordset.Close();
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
        }

        /// <summary>DAO compacts <paramref name="path"/> into <paramref name="compacted"/>, with the password it has.</summary>
        private static void DaoCompact(string path, string compacted, string password)
        {
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                string locale = ";LANGID=0x0409;CP=1252;COUNTRY=0" + (password == null ? "" : ";pwd=" + password);
                dbe.CompactDatabase(path, compacted, locale, 0, password == null ? "" : ";pwd=" + password);
            }
            finally { Dao.Release(dbe); }
        }

        /// <summary>The rows ACE reads with <paramref name="sql"/>, the file opened with its password.</summary>
        private static List<Values> AceQuery(string path, string password, string sql)
        {
            var rows = new List<Values>();
            var connection = AceVerification.ConnectionString(path) + (password == null ? "" : ";Jet OLEDB:Database Password=" + password);
            using (var open = new OleDbConnection(connection))
            using (var command = new OleDbCommand(sql, open))
            {
                open.Open();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var row = new Values();
                        for (var i = 0; i < reader.FieldCount; i++)
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        rows.Add(row);
                    }
            }
            return rows;
        }
    }
}
