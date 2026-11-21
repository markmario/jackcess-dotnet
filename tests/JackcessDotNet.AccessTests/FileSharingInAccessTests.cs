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
    /// Access and the engine with one file open at once (#62), each order, with ACE and DAO
    /// themselves on Access's side. The engine takes none of Access's locks, so two writers write over each other's
    /// pages: in the review of the engine's #59, ACE holding a connection while the engine inserted left
    /// <c>COUNT(*)</c> at 800 where 708 rows were there, and primary-key seeks found other rows. A file the engine has
    /// open to be written is shared for reading only: ACE and DAO opening it to write, shared or exclusively, are
    /// refused ("file already in use"), and read-only they open it. The engine does not open to write a file ACE or DAO
    /// has open to write, and opened only to be read it opens beside them. Each test ends with every row there, as ACE
    /// reads it - by scan, through the primary key and by a scan it cannot use the key for - and as DAO compacts it.
    /// </summary>
    public class FileSharingInAccessTests : IDisposable
    {
        private readonly TempDb _tmp;

        public FileSharingInAccessTests() { _tmp = new TempDb(); }
        public void Dispose() { _tmp.Dispose(); }

        public static IEnumerable<object[]> Formats() { return new[] { new object[] { ".mdb" }, new object[] { ".accdb" } }; }

        /// <summary>A database DAO made, with a table ACE made: T (Id LONG primary key, V TEXT(100)).</summary>
        private string NewDatabase(string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, extension == ".mdb" ? 64 : 128);
            Execute(path, "CREATE TABLE T (Id LONG CONSTRAINT PK PRIMARY KEY, V TEXT(100))");
            return path;
        }

        private static OleDbConnection Ace(string path, string mode = null)
        {
            var connection = new OleDbConnection(AceVerification.ConnectionString(path) + (mode == null ? "" : ";Mode=" + mode));
            connection.Open();
            return connection;
        }

        // Each statement or insert below is a commit of its own, which costs about a tenth of a second on CI's disk. Rows
        // that only set the scene go in one: ACE's and DAO's in a transaction, the engine's in one InsertRange. Rows
        // written one at a time are the ones written while the other side has the file open, where that is what a test
        // is about - a few of them.
        private const int OneAtATime = 10;

        // How many rows set a scene. AssertTableIs looks each row up twice through ACE, so a scene of hundreds of rows costs
        // as many lookups at the end of the test; twenty make the table the test needs.
        private const int Scene = 20;

        private static string Insert(int id, string what) { return "INSERT INTO T (Id, V) VALUES (" + id + ", '" + what + " " + id + "')"; }

        private static void AceInserts(OleDbConnection connection, List<Values> rows, int from, int count, string what)
        {
            for (var id = from; id < from + count; id++)
            {
                using (var command = new OleDbCommand(Insert(id, what), connection))
                    Assert.Equal(1, command.ExecuteNonQuery());
                rows.Add(V("Id", id, "V", what + " " + id));
            }
        }

        /// <summary>ACE's inserts of rows that set the scene, in one transaction: one commit.</summary>
        private static void AceInsertsAtOnce(OleDbConnection connection, List<Values> rows, int from, int count, string what)
        {
            using (var transaction = connection.BeginTransaction())
            {
                for (var id = from; id < from + count; id++)
                {
                    using (var command = new OleDbCommand(Insert(id, what), connection, transaction))
                        Assert.Equal(1, command.ExecuteNonQuery());
                    rows.Add(V("Id", id, "V", what + " " + id));
                }
                transaction.Commit();
            }
        }

        private static void DaoInserts(dynamic dao, List<Values> rows, int from, int count, string what)
        {
            for (var id = from; id < from + count; id++)
            {
                dao.Execute(Insert(id, what), 128 /* dbFailOnError */);
                rows.Add(V("Id", id, "V", what + " " + id));
            }
        }

        /// <summary>DAO's inserts of rows that set the scene, in one transaction of its default workspace: one commit.</summary>
        private static void DaoInsertsAtOnce(dynamic dbe, dynamic dao, List<Values> rows, int from, int count, string what)
        {
            dbe.BeginTrans();
            DaoInserts(dao, rows, from, count, what);
            dbe.CommitTrans();
        }

        private static void EngineInserts(Database db, List<Values> rows, int from, int count, string what)
        {
            var table = db.GetTable("T");
            for (var id = from; id < from + count; id++)
            {
                table.Insert(new Row { ["Id"] = id, ["V"] = what + " " + id });
                rows.Add(V("Id", id, "V", what + " " + id));
            }
        }

        /// <summary>The engine's inserts of rows that set the scene, in one InsertRange: one change.</summary>
        private static void EngineInsertsAtOnce(Database db, List<Values> rows, int from, int count, string what)
        {
            var batch = new List<Row>();
            for (var id = from; id < from + count; id++)
            {
                batch.Add(new Row { ["Id"] = id, ["V"] = what + " " + id });
                rows.Add(V("Id", id, "V", what + " " + id));
            }
            db.GetTable("T").InsertRange(batch);
        }

        private static int AceCount(OleDbConnection connection)
        {
            using (var command = new OleDbCommand("SELECT COUNT(*) FROM T", connection))
                return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// DAO's <c>OpenDatabase</c> of the file, in one mode; null when it opened (the database closed again), the
        /// message when it was refused.
        /// </summary>
        private static string DaoRefusal(string path, bool exclusive, bool readOnly)
        {
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db;
                try { db = dbe.OpenDatabase(path, exclusive, readOnly); }
                catch (Exception e) { return e.Message; }
                try
                {
                    dynamic counted = db.OpenRecordset("SELECT COUNT(*) FROM T");
                    Assert.True((int)counted.Fields[0].Value >= 0);
                    counted.Close();
                    if (readOnly) Assert.False((bool)db.Updatable);
                }
                finally { db.Close(); }
                return null;
            }
            finally { Dao.Release(dbe); }
        }

        private static string AceRefusal(string path, string mode)
        {
            try { Ace(path, mode).Dispose(); return null; }
            catch (OleDbException e) { return e.Message; }
        }

        // ── The engine first ─────────────────────────────────────────────────

        // The review's second reproduction, as a test: the engine holds the file and goes on writing while Access tries
        // to. ACE and DAO opening it to write - shared, or exclusively - are refused; read-only they open it and read it,
        // and cannot write. Nothing the engine wrote, before or after, is lost, and ACE goes on writing once it has
        // closed. With the file shared for writing, ACE's and DAO's opens were let in.
        [Theory]
        [MemberData(nameof(Formats))]
        public void while_the_engine_has_a_file_open_to_write_ace_and_dao_cannot_write_to_it_and_nothing_is_lost(string extension)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();
            using (var connection = Ace(path)) AceInsertsAtOnce(connection, rows, 1, Scene, "ace before");

            using (var db = Database.Open(path))
            {
                EngineInsertsAtOnce(db, rows, 1000, Scene, "engine");

                Assert.Contains("already in use", AceRefusal(path, null));
                Assert.Contains("already in use", AceRefusal(path, "Share Exclusive"));
                Assert.Contains("already in use", DaoRefusal(path, exclusive: false, readOnly: false));
                Assert.Contains("already in use", DaoRefusal(path, exclusive: true, readOnly: false));

                // Read-only, they read what the engine has written, and cannot write.
                using (var reader = Ace(path, "Read"))
                {
                    Assert.Equal(rows.Count, AceCount(reader));
                    using (var insert = new OleDbCommand("INSERT INTO T (Id, V) VALUES (99999, 'refused')", reader))
                        Assert.Throws<OleDbException>(() => insert.ExecuteNonQuery());
                }
                Assert.Null(DaoRefusal(path, exclusive: false, readOnly: true));

                // The engine goes on writing, a row at a time, having kept Access out.
                EngineInserts(db, rows, 2000, OneAtATime, "engine after");
            }

            AssertTableIs(path, "T", "Id", rows, "after the engine wrote with ACE and DAO kept out");
            using (var connection = Ace(path)) AceInsertsAtOnce(connection, rows, 3000, Scene, "ace after");
            AssertTableIs(path, "T", "Id", rows, "after ACE wrote once the engine had closed");
        }

        // Two Databases on one file: the second is refused, the first writes all it has to, and ACE finds every row. The
        // review's third reproduction had two writers at once lose 116 rows from the primary key.
        [Theory]
        [MemberData(nameof(Formats))]
        public void a_second_database_is_not_opened_to_write_a_file_and_the_first_loses_nothing(string extension)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();

            using (var first = Database.Open(path))
            {
                Assert.Throws<DatabaseInUseException>(() => Database.Open(path).Dispose());
                EngineInsertsAtOnce(first, rows, 1, 300, "first");
                Assert.Throws<DatabaseInUseException>(() => Database.Open(path).Dispose());
            }

            AssertTableIs(path, "T", "Id", rows, "after one Database wrote and a second was refused");
        }

        // Access read-only, then the engine to write: Access read-only shares the file for writing, as a second user of
        // Access would find it, so the engine opens it and writes; ACE reads the rows afterwards.
        [Theory]
        [MemberData(nameof(Formats))]
        public void the_engine_opens_to_write_a_file_ace_and_dao_have_open_read_only(string extension)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();
            using (var connection = Ace(path)) AceInsertsAtOnce(connection, rows, 1, Scene, "ace");

            using (var reader = Ace(path, "Read"))
            {
                Assert.Equal(Scene, AceCount(reader));
                using (var db = Database.Open(path))
                    EngineInserts(db, rows, 1000, OneAtATime, "engine");
                // What a reader that keeps its pages counts now is ACE's affair; it reads on.
                AceCount(reader);
            }

            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic daoReader = dbe.OpenDatabase(path, false, true);
                try
                {
                    using (var db = Database.Open(path))
                        EngineInserts(db, rows, 2000, OneAtATime, "engine beside DAO");
                }
                finally { daoReader.Close(); }
            }
            finally { Dao.Release(dbe); }

            AssertTableIs(path, "T", "Id", rows, "after the engine wrote beside ACE and DAO reading");
        }

        // ── Access first ─────────────────────────────────────────────────────

        public static IEnumerable<object[]> FormatsAndWriters()
        {
            return from extension in new[] { ".mdb", ".accdb" }
                   from writer in new[] { "ACE", "DAO" }
                   select new object[] { extension, writer };
        }

        // The review's first reproduction, as a test: ACE (or DAO) holds the file and writes to it, and the engine is
        // asked to open it. It is refused - DatabaseInUseException, the file as it was - and so is Database.Create over
        // it; Access goes on writing, and the engine adds its rows once Access has closed. Opened only to be read, the
        // engine opens beside Access, refuses to change the file, does not keep Access from writing, and reads only rows
        // Access wrote, each with its value; once Access has closed, it reads every one.
        [Theory]
        [MemberData(nameof(FormatsAndWriters))]
        public void while_ace_or_dao_writes_to_a_file_the_engine_does_not_write_to_it_and_nothing_is_lost(string extension, string writer)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();
            Database reader = null;
            try
            {
                if (writer == "ACE")
                {
                    using (var connection = Ace(path))
                    {
                        AceInsertsAtOnce(connection, rows, 1, Scene, "ace before");
                        reader = EngineIsKeptOutButReads(path, rows);
                        AceInserts(connection, rows, Scene + 1, OneAtATime, "ace beside the engine reading");
                        AssertReadsOnlyRowsWritten(reader, rows, "while ACE wrote beside it");
                    }
                }
                else
                {
                    dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
                    try
                    {
                        dynamic dao = dbe.OpenDatabase(path, false, false);
                        try
                        {
                            DaoInsertsAtOnce(dbe, dao, rows, 1, Scene, "dao");
                            reader = EngineIsKeptOutButReads(path, rows);
                            DaoInserts(dao, rows, Scene + 1, OneAtATime, "dao");
                            AssertReadsOnlyRowsWritten(reader, rows, "while DAO wrote beside it");
                        }
                        finally { dao.Close(); }
                    }
                    finally { Dao.Release(dbe); }
                }

                // Access has closed the file, and so has written all it committed: the reader, still open, reads it all.
                AssertSame(Written(rows), Written(EngineRows(reader)), "T as the engine opened only to be read reads it once " + writer + " has closed");
                Assert.Equal(rows.Last()["V"], reader.GetTable("T").FindRowByPrimaryKey(Convert.ToInt32(rows.Last()["Id"]))["V"]);
            }
            finally { reader?.Dispose(); }

            using (var db = Database.Open(path))
                EngineInsertsAtOnce(db, rows, 5000, Scene, "engine once Access had closed");

            AssertTableIs(path, "T", "Id", rows, "after " + writer + " wrote with the engine kept out, and the engine after it");
        }

        /// <summary>
        /// What the engine does with a file Access has open to write, holding <paramref name="written"/>: it is not opened
        /// to be written, nor replaced; opened only to be read, it opens, reads only rows Access wrote and refuses a change.
        /// Returns that database, open.
        /// </summary>
        private static Database EngineIsKeptOutButReads(string path, List<Values> written)
        {
            var refused = Assert.Throws<DatabaseInUseException>(() => Database.Open(path).Dispose());
            Assert.Contains(path, refused.Message);
            Assert.Throws<DatabaseInUseException>(() => Database.Create(path).Dispose());

            var reader = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true });
            Assert.True(reader.IsReadOnly);
            AssertReadsOnlyRowsWritten(reader, written, "beside Access");
            Assert.Throws<NotSupportedException>(() => reader.GetTable("T").Insert(new Row { ["Id"] = 99999, ["V"] = "refused" }));
            return reader;
        }

        /// <summary>The rows of T as <paramref name="db"/> reads them, a row's Id and V.</summary>
        private static List<Values> EngineRows(Database db)
        {
            return db.GetTable("T").Rows.Select(row => V("Id", row["Id"], "V", row["V"])).ToList();
        }

        /// <summary>
        /// While Access has the file open and is writing to it, a database opened only to be read reads the rows Access
        /// has written to the file: every one a row Access wrote, with its value, none twice - but not always every row
        /// Access has committed. Access writes what it has committed once it has been idle for half a second, and
        /// sometimes keeps the last of it until its next change or until it closes (measured: in 2 of 12 runs of 300
        /// inserts on a connection kept open, the last row was not in the file 15 seconds later).
        /// </summary>
        private static void AssertReadsOnlyRowsWritten(Database reader, List<Values> written, string when)
        {
            var expected = written.ToDictionary(row => Convert.ToInt32(row["Id"]), row => (string)row["V"]);
            var read = EngineRows(reader);
            Assert.True(read.Count > 0, "the engine read no row " + when);
            Assert.Equal(read.Count, read.Select(row => (int)row["Id"]).Distinct().Count());
            foreach (var row in read)
            {
                string value;
                Assert.True(expected.TryGetValue((int)row["Id"], out value), "the engine read a row Access did not write, " + row["Id"] + ", " + when);
                Assert.Equal(value, (string)row["V"]);
            }
        }

        // Access holding the file exclusively keeps the engine out, to write and to read; when it has closed, the engine
        // writes, and nothing Access wrote is lost.
        [Theory]
        [MemberData(nameof(FormatsAndWriters))]
        public void a_file_ace_or_dao_has_open_exclusively_is_not_opened_by_the_engine_at_all(string extension, string writer)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();

            Action engineIsRefused = () =>
            {
                Assert.Throws<DatabaseInUseException>(() => Database.Open(path).Dispose());
                var refused = Assert.Throws<DatabaseInUseException>(() => Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }).Dispose());
                Assert.Contains("exclusively", refused.Message);
                Assert.Throws<DatabaseInUseException>(() => Database.Create(path).Dispose());
            };

            if (writer == "ACE")
            {
                using (var connection = Ace(path, "Share Exclusive"))
                {
                    AceInsertsAtOnce(connection, rows, 1, Scene, "ace exclusively");
                    engineIsRefused();
                }
            }
            else
            {
                dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
                try
                {
                    dynamic dao = dbe.OpenDatabase(path, true, false);
                    try
                    {
                        DaoInsertsAtOnce(dbe, dao, rows, 1, Scene, "dao exclusively");
                        engineIsRefused();
                    }
                    finally { dao.Close(); }
                }
                finally { Dao.Release(dbe); }
            }

            using (var db = Database.Open(path))
                EngineInsertsAtOnce(db, rows, 1000, Scene, "engine");
            AssertTableIs(path, "T", "Id", rows, "after " + writer + " had it exclusively and the engine wrote after");
        }

        // An Access 97 file is only ever read, so the engine opens it beside DAO writing to it, where the header was read
        // without sharing writing and refused it; DAO goes on writing, and every row is there for DAO after. Only a 32-bit
        // engine opens an Access 97 file - ACE 12's DAO, or Jet 4.0's DAO 3.6 - so this runs in a 32-bit process.
        [Fact]
        public void the_engine_reads_an_access_97_file_dao_is_writing_to_and_dao_loses_nothing()
        {
            if (Environment.Is64BitProcess)
                Assert.Skip("64-bit ACE does not open an Access 97 file, so there is no referee here for one: run these tests in a 32-bit process.");

            var path = _tmp.NewMdbPath();
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: false) ?? Type.GetTypeFromProgID("DAO.DBEngine.36", throwOnError: true);
            dynamic dbe = Activator.CreateInstance(dbeType);
            try
            {
                dynamic made = dbe.CreateDatabase(path, ";LANGID=0x0409;CP=1252;COUNTRY=0", 32 /* dbVersion30, Access 97 */);
                made.Execute("CREATE TABLE T (Id LONG CONSTRAINT PK PRIMARY KEY, V TEXT(100))");
                made.Close();

                var rows = new List<Values>();
                using (var db = OpenBesideDao((object)dbe, path, rows))
                {
                    // DAO has closed the file, and so has written all it committed: the engine, still open, reads it all.
                    AssertSame(Written(rows), Written(EngineRows(db)), "T as the engine reads it once DAO has closed");
                    Assert.Equal("dao 400", db.GetTable("T").FindRowByPrimaryKey(400)["V"]);
                }

                dynamic again = dbe.OpenDatabase(path, false, true);
                try
                {
                    dynamic counted = again.OpenRecordset("SELECT COUNT(*) FROM T WHERE V = 'dao ' & Id");
                    Assert.Equal(400, (int)counted.Fields[0].Value);
                    counted.Close();
                }
                finally { again.Close(); }
            }
            finally { Dao.Release(dbe); }
        }

        /// <summary>
        /// DAO (<paramref name="dbe"/>) opens the Access 97 file at <paramref name="path"/> shared and inserts 200 rows; the
        /// engine opens it - for reading only, as it opens every Access 97 file - reads only rows DAO wrote, and refuses a
        /// change; DAO inserts 200 more beside it, and closes. Returns the engine's database, still open; the rows DAO
        /// wrote are added to <paramref name="rows"/>.
        /// </summary>
        private static Database OpenBesideDao(object dbe, string path, List<Values> rows)
        {
            Database db = null;
            dynamic dao = ((dynamic)dbe).OpenDatabase(path, false, false);
            try
            {
                for (var id = 1; id <= 400; id++)
                {
                    dao.Execute("INSERT INTO T (Id, V) VALUES (" + id + ", 'dao " + id + "')", 128);
                    rows.Add(V("Id", id, "V", "dao " + id));
                    if (id != 200) continue;
                    db = Database.Open(path);
                    Assert.True(db.IsReadOnly);
                    AssertReadsOnlyRowsWritten(db, rows, "beside DAO");
                    Assert.Throws<NotSupportedException>(() => db.GetTable("T").Insert(new Row { ["Id"] = 99999, ["V"] = "refused" }));
                }
                AssertReadsOnlyRowsWritten(db, rows, "while DAO wrote beside it");
                return db;
            }
            catch
            {
                db?.Dispose();
                throw;
            }
            finally { dao.Close(); }
        }

        // Database.Create emptied a file ACE had open, under ACE: the file was opened to be replaced sharing writing. It
        // replaces a file only when no one has it open - ACE read-only included - and ACE's rows are all there after.
        [Theory]
        [MemberData(nameof(Formats))]
        public void database_create_does_not_replace_a_file_ace_has_open(string extension)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();
            using (var connection = Ace(path)) AceInsertsAtOnce(connection, rows, 1, Scene, "ace");

            foreach (var mode in new[] { null, "Read" })
                using (var connection = Ace(path, mode))
                {
                    Assert.Throws<DatabaseInUseException>(() => Database.Create(path, FileFormat.Access2000).Dispose());
                    Assert.Equal(Scene, AceCount(connection));
                }

            AssertTableIs(path, "T", "Id", rows, "after Database.Create was refused the file ACE had open");
        }

        // ── A database opened only to be read, keeping writers out ───────────

        private static readonly DatabaseOpenOptions KeepingWritersOut = new DatabaseOpenOptions { ReadOnly = true, KeepWritersOut = true };

        // While the engine has a file open only to be read, keeping writers out (#84), Access cannot write to it: DAO
        // opening it shared or exclusively, and ACE exclusively, are refused ("file already in use"); ACE's default mode
        // opens it read-only, and its INSERT fails. ACE read-only, DAO read-only and DAO exclusively and read-only open
        // it and read it. The file is the same bytes after, and once the engine has closed it ACE writes to it. Measured
        // first by tools/ace-read-only-without-writers.ps1, with a FileStream holding the file as the engine does.
        [Theory]
        [MemberData(nameof(Formats))]
        public void while_the_engine_keeps_writers_out_ace_and_dao_only_read_the_file_and_it_is_unchanged(string extension)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();
            using (var connection = Ace(path)) AceInsertsAtOnce(connection, rows, 1, Scene, "ace before");
            byte[] before = File.ReadAllBytes(path);

            using (var db = Database.Open(path, KeepingWritersOut))
            {
                Assert.True(db.IsReadOnly);
                Assert.Contains("already in use", AceRefusal(path, "Share Exclusive"));
                Assert.Contains("already in use", DaoRefusal(path, exclusive: false, readOnly: false));
                Assert.Contains("already in use", DaoRefusal(path, exclusive: true, readOnly: false));

                // ACE's default mode, which asks to write, is let in only to read.
                using (var connection = Ace(path))
                {
                    Assert.Equal(rows.Count, AceCount(connection));
                    using (var insert = new OleDbCommand("INSERT INTO T (Id, V) VALUES (99999, 'refused')", connection))
                        Assert.Contains("updateable", Assert.Throws<OleDbException>(() => insert.ExecuteNonQuery()).Message);
                }
                using (var reader = Ace(path, "Read"))
                    Assert.Equal(rows.Count, AceCount(reader));
                Assert.Null(DaoRefusal(path, exclusive: false, readOnly: true));
                Assert.Null(DaoRefusal(path, exclusive: true, readOnly: true));

                // A second Database: refused to write, let in to read, either way.
                Assert.Throws<DatabaseInUseException>(() => Database.Open(path).Dispose());
                using (var second = Database.Open(path, KeepingWritersOut))
                    AssertSame(Written(rows), Written(EngineRows(second)), "T as a second engine keeping writers out reads it");

                AssertSame(Written(rows), Written(EngineRows(db)), "T as the engine keeping writers out reads it beside Access reading");
                Assert.Equal("ace before 7", db.GetTable("T").FindRowByPrimaryKey(7)["V"]);
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            using (var connection = Ace(path)) AceInsertsAtOnce(connection, rows, 1000, Scene, "ace after");
            AssertTableIs(path, "T", "Id", rows, "after ACE wrote once the engine keeping writers out had closed");
        }

        // With ACE or DAO holding the file to write, the engine keeping writers out is refused - they cannot be kept out
        // while one has it - and the file is as it was; opened only to be read without it, the engine reads beside the
        // writer, as the refusal says. With ACE or DAO only reading - read-only, or exclusively and read-only - it opens.
        [Theory]
        [MemberData(nameof(FormatsAndWriters))]
        public void the_engine_keeping_writers_out_is_refused_beside_ace_or_dao_writing_and_opens_beside_them_reading(string extension, string writer)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();
            using (var connection = Ace(path)) AceInsertsAtOnce(connection, rows, 1, Scene, "ace");

            Action refusedButReadsBeside = () =>
            {
                var refused = Assert.Throws<DatabaseInUseException>(() => Database.Open(path, KeepingWritersOut).Dispose());
                Assert.Contains("KeepWritersOut", refused.Message);
                using (var beside = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
                    AssertReadsOnlyRowsWritten(beside, rows, "beside " + writer + " writing");
            };
            Action opensAndReads = () =>
            {
                using (var db = Database.Open(path, KeepingWritersOut))
                    AssertSame(Written(rows), Written(EngineRows(db)), "T as the engine keeping writers out reads it beside " + writer + " reading");
            };

            if (writer == "ACE")
            {
                using (var connection = Ace(path))
                {
                    refusedButReadsBeside();
                    AceInserts(connection, rows, Scene + 1, OneAtATime, "ace beside the refusal");
                }
                foreach (var mode in new[] { "Read", "Share Deny Write" })
                    using (var reader = Ace(path, mode))
                    {
                        opensAndReads();
                        Assert.Equal(rows.Count, AceCount(reader));
                    }
            }
            else
            {
                dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
                try
                {
                    dynamic dao = dbe.OpenDatabase(path, false, false);
                    try
                    {
                        refusedButReadsBeside();
                        DaoInserts(dao, rows, Scene + 1, OneAtATime, "dao beside the refusal");
                    }
                    finally { dao.Close(); }

                    foreach (var exclusive in new[] { false, true })
                    {
                        dynamic reader = dbe.OpenDatabase(path, exclusive, true);
                        try { opensAndReads(); }
                        finally { reader.Close(); }
                    }
                }
                finally { Dao.Release(dbe); }
            }

            AssertTableIs(path, "T", "Id", rows, "after " + writer + " wrote beside the engine refused to keep writers out");
        }

        // ── A database opened only to be read, beside ACE writing ────────────

        // The review of the engine's #73, with ACE as the writer: a read-only Database's index cursor comes to the last
        // row of a data page, and ACE gives the next row a key at the end of the index. The cursor kept the leaf it had
        // read, which still held the row's old entry, and gave the row there and again at its new key - one row more than
        // the table has. It reads the index again at each move, and gives the row once, at its new key: a row the writer
        // moves ahead of a cursor before the cursor has come to it is given where it is. Measured, in an .mdb and an
        // .accdb alike: ACE's 1,000 rows are on 8 data pages, the second beginning at row 149, and the primary key has 2
        // leaves - the row's old entry is in the first, and its new key goes to the last.
        [Theory]
        [MemberData(nameof(Formats))]
        public void a_read_only_index_cursor_gives_once_a_row_ace_moves_ahead_of_it(string extension)
        {
            var path = NewDatabase(extension);
            var rows = new List<Values>();
            using (var connection = Ace(path)) AceInsertsAtOnce(connection, rows, 1, 1000, "row");

            int moved;
            using (var reader = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                // The first row of the table's second data page. The primary key's index has more than one leaf, so the
                // row's entry is in a leaf before the last, where its new key goes.
                var pages = reader.GetTable("T").Rows.Select(r => new { Id = (int)r["Id"], r.Id.Value.PageNumber }).OrderBy(p => p.Id).ToList();
                moved = pages.First(p => p.PageNumber != pages[0].PageNumber).Id;
                var layout = PagesOf(path, pages[0].PageNumber);
                Assert.True(layout.Leaves > 1, "the primary key's index of " + rows.Count + " rows has " + layout.Leaves + " leaf: the test needs more");

                var cursor = reader.GetTable("T").CreateIndexCursor();
                var given = new List<int>();
                while (cursor.MoveNext())
                {
                    given.Add((int)cursor.Current["Id"]);
                    if (given[given.Count - 1] == moved - 1) break;
                }
                // ACE closes, and so has written the change to the file, before the cursor moves on.
                Execute(path, "UPDATE T SET Id = 1000000 WHERE Id = " + moved);
                while (cursor.MoveNext()) given.Add((int)cursor.Current["Id"]);

                Assert.Equal(rows.Count, given.Count);
                Assert.Single(given, id => id == 1000000);
                Assert.DoesNotContain(moved, given);
                Assert.Equal(1000000, given[given.Count - 1]);
                Assert.Equal("row " + moved, reader.GetTable("T").FindRowByPrimaryKey(1000000)["V"]);
            }

            rows[rows.FindIndex(r => Convert.ToInt32(r["Id"]) == moved)] = V("Id", 1000000, "V", "row " + moved);
            AssertTableIs(path, "T", "Id", rows, "after ACE gave a row a key ahead of a reader's index cursor");
        }

        /// <summary>
        /// How many of the file's pages are the data pages and the index leaves of the table <paramref name="dataPage"/>,
        /// one of its data pages, is of: Jet's page type is a page's first byte (1 a data page, 4 an index leaf), and the
        /// table's definition page is at offset 4 of both.
        /// </summary>
        private static (int DataPages, int Leaves) PagesOf(string path, int dataPage)
        {
            const int pageSize = 4096;
            byte[] file;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                file = new byte[stream.Length];
                int read = 0;
                while (read < file.Length) read += stream.Read(file, read, file.Length - read);
            }
            int definition = BitConverter.ToInt32(file, dataPage * pageSize + 4);
            int dataPages = 0, leaves = 0;
            for (int at = 0; at + pageSize <= file.Length; at += pageSize)
            {
                if (BitConverter.ToInt32(file, at + 4) != definition) continue;
                if (file[at] == 0x01) dataPages++;
                else if (file[at] == 0x04) leaves++;
            }
            return (dataPages, leaves);
        }

        /// <summary>
        /// A database DAO made with a table ACE made, M (Id LONG primary key, M MEMO), and five rows ACE inserted: each
        /// memo 300 of one letter, 'a' for row 1 to 'e' - a long value each, kept outside its row.
        /// </summary>
        private string FiveMemos(string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, extension == ".mdb" ? 64 : 128);
            Execute(path, "CREATE TABLE M (Id LONG CONSTRAINT PK PRIMARY KEY, M MEMO)");
            using (var connection = Ace(path))
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var i in Enumerable.Range(1, 5))
                    using (var command = new OleDbCommand("INSERT INTO M (Id, M) VALUES (" + i + ", '" + Memo((char)('a' + i - 1)) + "')", connection, transaction))
                        command.ExecuteNonQuery();
                transaction.Commit();
            }
            return path;
        }

        private static string Memo(char letter) { return new string(letter, 300); }

        public static IEnumerable<object[]> FormatsAndChanges()
        {
            return from extension in new[] { ".mdb", ".accdb" }
                   from change in new[] { "UPDATE M SET M = 'short' WHERE Id = 5", "DELETE FROM M WHERE Id = 5" }
                   select new object[] { extension, change };
        }

        // The review of the engine's #73, with ACE as the writer: a read-only Database's table cursor is on row 1, and ACE
        // changes row 5 - its memo made short enough for its row, or the row deleted - which frees row 5's long value,
        // and inserts row 6, whose memo can take the place it had. The cursor kept the data page it had read, and could
        // read row 5 through the reference that page still held. It reads its page again at each move: what it gives of
        // rows 2 to 6 is each as it is now, with its own value - row 5 short, or not given once deleted - and row 6, which
        // ACE adds behind the cursor or on a page the cursor did not list, may not be given at all.
        [Theory]
        [MemberData(nameof(FormatsAndChanges))]
        public void a_read_only_table_cursor_gives_each_row_ace_changes_with_its_own_value(string extension, string change)
        {
            var path = FiveMemos(extension);
            var now = new Dictionary<int, string> { { 1, Memo('a') }, { 2, Memo('b') }, { 3, Memo('c') }, { 4, Memo('d') }, { 6, Memo('z') } };
            if (change.StartsWith("UPDATE")) now[5] = "short";

            using (var reader = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                var cursor = reader.GetTable("M").CreateCursor();
                Assert.True(cursor.MoveNext());
                Assert.Equal(1, cursor.Current["Id"]);

                // ACE closes, and so has written both changes to the file, before the cursor moves on.
                Execute(path, change, "INSERT INTO M (Id, M) VALUES (6, '" + Memo('z') + "')");

                var given = new List<int>();
                while (cursor.MoveNext())
                {
                    var id = (int)cursor.Current["Id"];
                    given.Add(id);
                    string value;
                    Assert.True(now.TryGetValue(id, out value), "the cursor gave row " + id + ", which is not there now");
                    Assert.Equal(value, (string)cursor.Current["M"]);
                }
                Assert.Equal(given.Distinct().Count(), given.Count);
                Assert.Equal(new[] { 2, 3, 4 }, given.Where(id => id >= 2 && id <= 4));
                Assert.Equal(now.ContainsKey(5), given.Contains(5));

                // A reading begun now, with ACE gone, gives every row with its value.
                AssertSame(
                    Written(now.Select(p => V("Id", p.Key, "M", p.Value))),
                    Written(reader.GetTable("M").Rows.Select(r => V("Id", r["Id"], "M", r["M"]))),
                    "M as the reader reads it once ACE has closed");
            }

            AssertTableIs(path, "M", "Id", now.Select(p => V("Id", p.Key, "M", p.Value)), "after ACE changed rows beside a reader's table cursor");
        }
    }
}
