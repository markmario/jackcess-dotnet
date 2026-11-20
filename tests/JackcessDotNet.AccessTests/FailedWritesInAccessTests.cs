using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// What the managed engine leaves when the file fails a change part-way - a full disk - checked by Access. The
    /// engine's own tests fail every page write of a change in turn and find the file byte for byte as it was; here
    /// ACE and DAO open the file and read it after each such failure, in an .mdb and an .accdb DAO made, and then
    /// read, write to and compact what the same file takes once the change goes through.
    /// <list type="bullet">
    /// <item>A table created (#60), and an import into a new table, which creates its table
    /// the same way. The engine wrote both outside any undo scope: a failure part-way left pages added and, past the
    /// catalog row, a table listed that Access in an .mdb would not read ("Record(s) cannot be read; no read
    /// permission on 'New'"): its MSysACEs rows were never written.</item>
    /// <item>An insert whose first write fails (#61). It had taken two pages a delete
    /// emptied for its long value and forgot them for the rest of the session; the long values written after it now
    /// go on those pages, which ACE reads, frees again and fills.</item>
    /// </list>
    /// </summary>
    public class FailedWritesInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        /// <summary>DAO's version of the database, and its extension: Access 2000-2003's .mdb, and Access 2007's .accdb.</summary>
        public static IEnumerable<object[]> Formats()
        {
            yield return new object[] { 64, ".mdb" };
            yield return new object[] { 128, ".accdb" };
        }

        /// <summary>
        /// Has the engine's next <paramref name="writes"/> page writes go through and the one after them fail, as a
        /// full disk would (PageFile.WritesBeforeFailure, which the engine keeps for its tests); null for none.
        /// </summary>
        private static void FailAfter(Database db, int? writes)
        {
            var file = typeof(Database).GetProperty("File", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(db);
            file.GetType().GetProperty("WritesBeforeFailure", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(file, writes);
        }

        /// <summary>A database DAO made, with a table ACE made and filled - long values among them - and emptied in part.</summary>
        private string AccessDatabase(int daoVersion, string extension, out List<Values> rows)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, daoVersion);
            Execute(path, "CREATE TABLE Existing (Id LONG CONSTRAINT pk PRIMARY KEY, Name TEXT(50), Notes MEMO)");
            rows = new List<Values>();
            for (var i = 1; i <= 12; i++)
            {
                var row = V("Id", i, "Name", "row " + i, "Notes", new string((char)('a' + i), i * 500));
                AceInsert(path, "Existing", row);
                rows.Add(row);
            }
            Execute(path, "DELETE FROM Existing WHERE Id IN (3, 6, 9)");
            rows.RemoveAll(r => new[] { 3, 6, 9 }.Contains((int)r["Id"]));
            return path;
        }

        /// <summary>The database's own tables, as ACE lists them.</summary>
        private static List<string> AceTableNames(string path)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                return connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null).Rows.Cast<DataRow>()
                    .Where(r => (string)r["TABLE_TYPE"] == "TABLE").Select(r => (string)r["TABLE_NAME"]).OrderBy(n => n).ToList();
            }
        }

        /// <summary>The database's own tables, as DAO lists them: its table definitions that are not system tables.</summary>
        private static List<string> DaoTableNames(string path)
        {
            var engine = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try { return TableDefNames(engine, path); }
            finally { Dao.Release(engine); }
        }

        // Its own method, so nothing DAO handed out is still in a local when the engine is released.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<string> TableDefNames(object engine, string path)
        {
            dynamic db = ((dynamic)engine).OpenDatabase(path, false, true);
            try
            {
                var names = new List<string>();
                foreach (dynamic table in db.TableDefs)
                {
                    var name = (string)table.Name;
                    if (((int)table.Attributes & unchecked((int)0x80000002)) == 0) names.Add(name);
                }
                return names.OrderBy(n => n).ToList();
            }
            finally { db.Close(); }
        }

        /// <summary>
        /// Makes <paramref name="create"/> fail at its first page write, then its second, and so on until it has the
        /// writes it needs, each in a session of its own; after each failure the file is byte for byte what it was,
        /// and ACE and DAO list the tables Access made and no other and read each of them whole. Returns how many
        /// writes it failed at.
        /// </summary>
        private static int FailAtEveryWrite(string path, string table, List<Values> existing, Action<Database> create)
        {
            var before = File.ReadAllBytes(path);
            for (var writes = 0; ; writes++)
            {
                Assert.True(writes < 1000, "the table was never made");
                using (var db = Database.Open(path))
                {
                    FailAfter(db, writes);
                    try
                    {
                        create(db);
                        FailAfter(db, null);
                        return writes;   // it had the writes it needed
                    }
                    catch (IOException)
                    {
                        FailAfter(db, null);
                    }
                    Assert.DoesNotContain(table, db.GetTableNames(), StringComparer.OrdinalIgnoreCase);
                }

                var when = "after write " + (writes + 1) + " of " + table + " failed";
                Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), "the file changed " + when);
                Assert.Equal(new[] { "Existing" }, AceTableNames(path));
                AssertSame(Written(existing), Written(AceTable(path, "Existing")), "Existing as ACE reads it " + when);
                Assert.Equal(new[] { "Existing" }, DaoTableNames(path));
                AssertSame(Written(existing), Written(DaoRows(path, "Existing")), "Existing as DAO reads it " + when);
            }
        }

        private static readonly ColumnDefinition[] NewColumns =
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("Code", DataType.Text) { MaxLength = 20, IsRequired = true, AllowZeroLength = false },
            new ColumnDefinition("Notes", DataType.Memo),
            new ColumnDefinition("Amount", DataType.Currency),
        };

        private static Values NewRow(int id) => V("Id", id, "Code", "code " + id, "Notes", new string('n', id * 400), "Amount", id * 1.25m);

        [Theory]
        [MemberData(nameof(Formats))]
        public void access_reads_the_file_after_every_failed_write_of_a_table_and_reads_writes_and_compacts_the_table_made(int daoVersion, string extension)
        {
            List<Values> existing;
            var path = AccessDatabase(daoVersion, extension, out existing);

            var failures = FailAtEveryWrite(path, "New", existing,
                db => db.CreateTable("New", NewColumns, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") { IsUnique = true } }));
            Assert.True(failures >= 10, "only " + failures + " writes failed: a table with two indexes and a long-value column has more");

            // The table made takes rows, which Access reads, adds to, changes and keeps through a compact.
            AssertTableIs(path, "New", "Id", new List<Values>(), "once it was made");
            var subject = new Subject(path, "New", "Id");
            subject.EngineInserts(Enumerable.Range(1, 15).Select(NewRow).ToArray());
            subject.Check("after the engine wrote to it");
            subject.AceInserts(V("Id", 100, "Code", "ace", "Notes", new string('a', 6000), "Amount", 9.5m));
            subject.AceUpdates(3, V("Notes", new string('u', 5000)));
            subject.AceDeletes(4, 5);
            subject.Check("after ACE wrote to it");
            AssertSame(Written(existing), Written(AceTable(path, "Existing")), "Existing at the end");
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void access_reads_the_file_after_every_failed_write_of_an_import_s_new_table_and_reads_writes_and_compacts_the_table_made(int daoVersion, string extension)
        {
            List<Values> existing;
            var path = AccessDatabase(daoVersion, extension, out existing);

            // The import's table, with no rows: an import that fails among its rows is taken back by the importer itself.
            // A decimal column with no value to take places from is a Decimal of none (#59), so
            // the amounts written to it after are whole.
            var source = new DataTable("Source");
            source.Columns.Add("Id", typeof(int));
            source.Columns.Add(new DataColumn("Name", typeof(string)) { MaxLength = 30 });
            source.Columns.Add(new DataColumn("Notes", typeof(string)) { MaxLength = 10000 });
            source.Columns.Add("Amount", typeof(decimal));

            var failures = FailAtEveryWrite(path, "Imported", existing,
                db => db.ImportTable(source, new ImportOptions { TableName = "Imported", PrimaryKey = new[] { "Id" } }));
            Assert.True(failures >= 5, "only " + failures + " writes failed");

            var subject = new Subject(path, "Imported", "Id");
            subject.EngineInserts(Enumerable.Range(1, 10).Select(i => V("Id", i, "Name", "name " + i, "Notes", new string('i', i * 700), "Amount", i * 5m)).ToArray());
            subject.Check("after the engine wrote to it");
            subject.AceInserts(V("Id", 50, "Name", "ace", "Notes", new string('a', 4500), "Amount", 225m));
            subject.AceDeletes(2);
            subject.Check("after ACE wrote to it");
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void access_reads_frees_and_fills_the_pages_an_insert_that_failed_at_its_first_write_left_to_the_long_values_after_it(int daoVersion, string extension)
        {
            // A Memo of 5,000 characters is a chain of two chunks, each on a page of its own (Access's placement).
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, daoVersion);
            Execute(path, "CREATE TABLE L (Id LONG CONSTRAINT pk PRIMARY KEY, Notes MEMO)");
            var subject = new Subject(path, "L", "Id");
            subject.EngineInserts(Enumerable.Range(1, 6).Select(i => V("Id", i, "Notes", new string((char)('a' + i), 5000))).ToArray());

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("L");
                foreach (var id in new[] { 2, 4, 6 }) table.Delete(table.FindRowByPrimaryKey(id));
                var length = new FileInfo(path).Length;

                // The insert takes two of the pages the deletes emptied for its value, and fails at its first write.
                FailAfter(db, 0);
                Assert.Throws<IOException>(() => table.Insert(new Row { ["Id"] = 7, ["Notes"] = new string('x', 5000) }));
                FailAfter(db, null);

                // The three values after it go where the three deleted were: the file does not grow.
                for (var id = 7; id <= 9; id++) table.Insert(new Row { ["Id"] = id, ["Notes"] = new string((char)('a' + id), 5000) });
                Assert.Equal(length, new FileInfo(path).Length);
            }

            var rows = new Subject(path, "L", "Id");
            Assert.Equal(new long[] { 1, 3, 5, 7, 8, 9 }, rows.Ids);
            rows.Check("after the engine's values went on the pages its deletes emptied");

            // Access frees the pages of two of them, by replacing one value and deleting another, and fills them.
            rows.AceUpdates(7, V("Notes", new string('r', 7000)));
            rows.AceDeletes(8);
            rows.AceInserts(V("Id", 20, "Notes", new string('s', 9000)), V("Id", 21, "Notes", new string('t', 5000)));
            rows.Check("after ACE changed them");
        }
    }
}
