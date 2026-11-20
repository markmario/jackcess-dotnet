using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Indexes built in bulk (#82): rows loaded into an empty table go in with no index entries, and each index's tree
    /// is then built from the table's rows, page after page, with its count of keys. Refereed by Access, on a table of
    /// eight indexes - a primary key, a unique text index, text, number, date, Yes/No, one that ignores nulls and one
    /// of two columns, one of them descending - that the engine or ACE made, in a database that sorts in General -
    /// Legacy and in one that sorts in General. ACE answers a condition on every indexed column through the index as a
    /// scan answers it, with ranges and order; DAO walks each index to the table's rows and seeks through the unique
    /// one; DAO compacts the file, and every index keeps its rows and has the count of keys Access gives the index it
    /// builds again. ACE then inserts, updates, deletes and refuses a duplicate, and the engine goes on after it, entry
    /// by entry. A load whose trees refuse a duplicate leaves the file as it was, and ACE writes to it.
    /// </summary>
    public class BulkIndexesInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        // Enough rows for trees of more than one level: an index of 3,000 keys runs to many leaf pages.
        private const int Rows = 3000;

        public static IEnumerable<object[]> Kinds()
        {
            foreach (var general in new[] { false, true })
                foreach (var accessMade in new[] { true, false })
                    yield return new object[] { general, accessMade };
        }

        // ── The table ────────────────────────────────────────────────────────

        // Words whose keys differ in case, accent and width, in both orders.
        private static readonly string[] Words = { "alpha", "Alpha", "Ärger", "ärger", "zeta", "émile", "Emile", "Ａwide", "b" };

        private static Values Row(int i)
        {
            return V("Id", i, "Code", "C" + i.ToString("D5", CultureInfo.InvariantCulture),
                "Name", Words[i % Words.Length] + " " + (i % 53).ToString(CultureInfo.InvariantCulture),
                "Amount", (i % 211) * 1.25 - 100, "Made", new DateTime(2020, 1, 1).AddHours(i), "Done", i % 3 == 0,
                "Part", i % 10 == 0 ? (object)null : i % 37, "Notes", i % 5 == 0 ? "note " + i : null);
        }

        /// <summary>
        /// An empty database: in General - Legacy one the engine makes, in General a copy of an Access 2010 one from
        /// the engine's corpus with its tables dropped. In it the table T, made by ACE's DDL or by the engine.
        /// </summary>
        private string NewTable(bool general, bool accessMade)
        {
            string path;
            if (!general)
            {
                path = _tmp.NewMdbPath();
                Database.Create(path, FileFormat.Access2000).Dispose();
            }
            else
            {
                SkipUnlessAceOpensAccess2010();
                var corpus = typeof(BulkIndexesInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Single(a => a.Key == "EngineCorpusDir").Value;
                path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + ".accdb");
                File.Copy(Path.Combine(corpus, "V2010", "common1V2010.accdb"), path);
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
                {
                    connection.Open();
                    var tables = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null).Rows.Cast<System.Data.DataRow>()
                        .Where(r => (string)r["TABLE_TYPE"] == "TABLE").Select(r => (string)r["TABLE_NAME"]).ToList();
                    foreach (var table in tables)
                        using (var command = new OleDbCommand("DROP TABLE [" + table + "]", connection))
                            command.ExecuteNonQuery();
                }
            }

            if (accessMade)
            {
                Execute(path,
                    "CREATE TABLE T (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Code TEXT(20), Name TEXT(100), Amount DOUBLE, Made DATETIME, Done YESNO, Part LONG, Notes MEMO)",
                    "CREATE UNIQUE INDEX Code ON T (Code)",
                    "CREATE INDEX Name ON T (Name)",
                    "CREATE INDEX Amount ON T (Amount)",
                    "CREATE INDEX Made ON T (Made)",
                    "CREATE INDEX Done ON T (Done)",
                    "CREATE INDEX Part ON T (Part) WITH IGNORE NULL",
                    "CREATE INDEX NameAmount ON T (Name, Amount DESC)");
                return path;
            }

            var nameAmount = new IndexDefinition("NameAmount", "Name", "Amount");
            nameAmount.DescendingColumns.Add("Amount");
            using (var db = Database.Open(path))
                db.CreateTable("T",
                    new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Code", DataType.Text) { MaxLength = 20 },
                        new ColumnDefinition("Name", DataType.Text) { MaxLength = 100 }, new ColumnDefinition("Amount", DataType.Double),
                        new ColumnDefinition("Made", DataType.DateTime), new ColumnDefinition("Done", DataType.Boolean),
                        new ColumnDefinition("Part", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo),
                    },
                    new[] { "Id" },
                    new[]
                    {
                        new IndexDefinition("Code", "Code") { IsUnique = true }, new IndexDefinition("Name", "Name"),
                        new IndexDefinition("Amount", "Amount"), new IndexDefinition("Made", "Made"), new IndexDefinition("Done", "Done"),
                        new IndexDefinition("Part", "Part") { IgnoreNulls = true }, nameAmount,
                    });
            return path;
        }

        private static List<Row> EngineRows(IEnumerable<int> ids)
        {
            return ids.Select(i => EngineRow(Row(i))).ToList();
        }

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(stream));
        }

        // ── ACE ──────────────────────────────────────────────────────────────

        private static int Count(OleDbConnection connection, string where)
        {
            using (var command = new OleDbCommand("SELECT COUNT(*) FROM T WHERE " + where, connection))
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static object First(OleDbConnection connection, string sql)
        {
            using (var command = new OleDbCommand(sql, connection))
                return command.ExecuteScalar();
        }

        /// <summary>
        /// The same condition answered through the index and from the rows: "+ 0" and "&amp; ''" keep ACE off the
        /// index. With <paramref name="expected"/>, the number of rows it holds as well.
        /// </summary>
        private static void AssertIndexAgreesWithScan(OleDbConnection connection, string column, string op, string literal, bool text, int? expected = null)
        {
            var throughIndex = Count(connection, "[" + column + "] " + op + " " + literal);
            var throughRows = Count(connection, (text ? "[" + column + "] & ''" : "[" + column + "] + 0") + " " + op + " " + literal);
            Assert.True(throughIndex == throughRows && (expected == null || expected == throughIndex),
                column + " " + op + " " + literal + ": " + throughIndex + " through the index, " + throughRows + " by scan"
                + (expected == null ? "" : ", expected " + expected));
        }

        private static string Quoted(string text) { return "'" + text.Replace("'", "''") + "'"; }

        /// <summary>ACE answers, through each indexed column, what a scan answers, of rows 1 to <paramref name="rows"/>.</summary>
        private static void AssertAceAnswersThroughEveryIndex(string path, int rows)
        {
            var all = Enumerable.Range(1, rows).Select(Row).ToList();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Equal(rows, Convert.ToInt32(First(connection, "SELECT COUNT(*) FROM T"), CultureInfo.InvariantCulture));

                AssertIndexAgreesWithScan(connection, "Id", "=", "1", false, 1);
                AssertIndexAgreesWithScan(connection, "Id", "BETWEEN", "100 AND 199", false, 100);
                AssertIndexAgreesWithScan(connection, "Id", ">", (rows - 10).ToString(CultureInfo.InvariantCulture), false, 10);

                foreach (var i in new[] { 1, 2, rows / 2, rows - 1, rows })
                    AssertIndexAgreesWithScan(connection, "Code", "=", Quoted((string)Row(i)["Code"]), true, 1);
                AssertIndexAgreesWithScan(connection, "Code", "=", Quoted(((string)Row(7)["Code"]).ToLowerInvariant()), true, 1);
                AssertIndexAgreesWithScan(connection, "Code", "=", Quoted("none"), true, 0);
                AssertIndexAgreesWithScan(connection, "Code", "BETWEEN", "'C01000' AND 'C01999'", true, 1000);
                AssertIndexAgreesWithScan(connection, "Code", ">=", Quoted((string)Row(rows - 99)["Code"]), true, 100);

                foreach (var word in Words)
                {
                    AssertIndexAgreesWithScan(connection, "Name", "=", Quoted(word + " 0"), true);
                    AssertIndexAgreesWithScan(connection, "Name", "LIKE", Quoted(word + " 1%"), true);
                    AssertIndexAgreesWithScan(connection, "Name", "<", Quoted(word), true);
                }

                AssertIndexAgreesWithScan(connection, "Amount", "=", "0", false, all.Count(r => (double)r["Amount"] == 0));
                AssertIndexAgreesWithScan(connection, "Amount", "<", "0", false, all.Count(r => (double)r["Amount"] < 0));
                AssertIndexAgreesWithScan(connection, "Amount", "BETWEEN", "-10 AND 10.5", false, all.Count(r => (double)r["Amount"] >= -10 && (double)r["Amount"] <= 10.5));

                AssertIndexAgreesWithScan(connection, "Made", "=", "#2020-01-02 03:00:00#", false, 1);
                AssertIndexAgreesWithScan(connection, "Made", "BETWEEN", "#2020-01-10# AND #2020-01-20#", false, 241);

                AssertIndexAgreesWithScan(connection, "Done", "=", "True", false, all.Count(r => (bool)r["Done"]));
                AssertIndexAgreesWithScan(connection, "Done", "=", "False", false, all.Count(r => !(bool)r["Done"]));

                Assert.Equal(all.Count(r => r["Part"] == null), Count(connection, "Part IS NULL"));
                AssertIndexAgreesWithScan(connection, "Part", "=", "5", false, all.Count(r => Equals(r["Part"], 5)));
                AssertIndexAgreesWithScan(connection, "Part", ">", "30", false, all.Count(r => r["Part"] != null && (int)r["Part"] > 30));

                // The index of two columns, the second descending.
                Assert.Equal(Count(connection, "Name & '' = 'zeta 4' AND Amount + 0 > 0"), Count(connection, "Name = 'zeta 4' AND Amount > 0"));
                Assert.Equal(Count(connection, "Name & '' >= 'alpha' AND Name & '' < 'b'"), Count(connection, "Name >= 'alpha' AND Name < 'b'"));

                // In order, at both ends.
                Assert.Equal(Row(1)["Code"], First(connection, "SELECT TOP 1 Code FROM T ORDER BY Code"));
                Assert.Equal(Row(rows)["Code"], First(connection, "SELECT TOP 1 Code FROM T ORDER BY Code DESC"));
                Assert.Equal(rows, Convert.ToInt32(First(connection, "SELECT TOP 1 Id FROM T ORDER BY Made DESC"), CultureInfo.InvariantCulture));
            }
        }

        // ── DAO ──────────────────────────────────────────────────────────────

        private static T WithDao<T>(string path, Func<dynamic, T> use)
        {
            var engine = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try { return Opened(engine, path, use); }
            finally { Dao.Release(engine); }
        }

        // Its own method, so nothing DAO handed out is still in a local when the engine is released.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T Opened<T>(object engine, string path, Func<dynamic, T> use)
        {
            dynamic database = ((dynamic)engine).OpenDatabase(path, false, true);
            try { return use(database); }
            finally { database.Close(); }
        }

        /// <summary>
        /// Each index of T as DAO has it - its name, the rows DAO reaches walking it from end to end, and its count of
        /// keys (DistinctCount) - by name.
        /// </summary>
        private static SortedDictionary<string, string> Indexes(string path)
        {
            return WithDao(path, db =>
            {
                var indexes = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var names = new List<string>();
                foreach (dynamic index in db.TableDefs["T"].Indexes)
                    names.Add((string)index.Name + "|" + (int)index.DistinctCount);
                foreach (var entry in names)
                {
                    var name = entry.Split('|')[0];
                    dynamic recordset = db.OpenRecordset("T", 1 /* dbOpenTable */);
                    int rows = 0;
                    try
                    {
                        recordset.Index = name;
                        if (!(bool)recordset.EOF)
                        {
                            recordset.MoveFirst();
                            while (!(bool)recordset.EOF) { rows++; recordset.MoveNext(); }
                        }
                    }
                    finally { recordset.Close(); }
                    indexes[name] = rows + " rows, " + entry.Split('|')[1] + " keys";
                }
                return indexes;
            });
        }

        /// <summary>Every index of T reaches each of its rows: all of them, or those with a Part for the index that ignores nulls.</summary>
        private static void AssertEveryIndexReachesItsRows(SortedDictionary<string, string> indexes, int rows, int withPart)
        {
            Assert.Equal(new[] { "Amount", "Code", "Done", "Made", "Name", "NameAmount", "Part", "PrimaryKey" }, indexes.Keys);
            foreach (var index in indexes)
                Assert.StartsWith((index.Key == "Part" ? withPart : rows) + " rows,", index.Value);
        }

        private static string Reached(string index) { return index.Substring(0, index.IndexOf(',')); }

        private static int Keys(string index) { return int.Parse(index.Split(' ')[2], CultureInfo.InvariantCulture); }

        /// <summary>The codes DAO's Seek does not find through the unique index.</summary>
        private static List<string> NotFoundBySeek(string path, IEnumerable<string> codes)
        {
            return WithDao(path, db =>
            {
                var missing = new List<string>();
                dynamic recordset = db.OpenRecordset("T", 1);
                try
                {
                    recordset.Index = "Code";
                    foreach (var code in codes)
                    {
                        recordset.Seek("=", code);
                        if ((bool)recordset.NoMatch || !string.Equals((string)recordset.Fields["Code"].Value, code, StringComparison.OrdinalIgnoreCase))
                            missing.Add(code);
                    }
                }
                finally { recordset.Close(); }
                return missing;
            });
        }

        /// <summary>
        /// DAO compacts the file, building every index again: each keeps its rows and has the count of keys it had, and
        /// ACE answers through each as before.
        /// </summary>
        private static void AssertCompactedAlike(string path, int rows)
        {
            var before = Indexes(path);
            var compacted = Compact(path);
            try
            {
                Assert.Equal(before, Indexes(compacted));
                AssertAceAnswersThroughEveryIndex(compacted, rows);
            }
            finally { File.Delete(compacted); }
        }

        // ── The tests ────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Kinds))]
        public void indexes_the_engine_builds_in_bulk_are_ones_ace_answers_through_dao_walks_and_seeks_and_compacts(bool general, bool accessMade)
        {
            var path = NewTable(general, accessMade);
            using (var db = Database.Open(path))
                Assert.Equal(Rows, db.GetTable("T").BulkInsert(EngineRows(Enumerable.Range(1, Rows))));

            AssertAceAnswersThroughEveryIndex(path, Rows);
            var withPart = Enumerable.Range(1, Rows).Count(i => Row(i)["Part"] != null);
            AssertEveryIndexReachesItsRows(Indexes(path), Rows, withPart);
            Assert.Empty(NotFoundBySeek(path, Enumerable.Range(1, Rows).Where(i => i % 97 == 1).Select(i => (string)Row(i)["Code"])));
            AssertCompactedAlike(path, Rows);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ace_changes_a_table_whose_indexes_were_built_in_bulk_and_the_engine_goes_on_after_it(bool general)
        {
            var path = NewTable(general, accessMade: false);
            using (var db = Database.Open(path))
                db.GetTable("T").InsertRange(EngineRows(Enumerable.Range(1, Rows)));

            // ACE adds a row, changes two and deletes one, and refuses a code that is one already but for its case.
            AceInsert(path, "T", Row(Rows + 1));
            AceUpdate(path, "T", "Id", 5, V("Name", "changed by ACE", "Amount", 999.5));
            AceUpdate(path, "T", "Id", 8, V("Code", "ACE00008", "Part", null));
            Execute(path, "DELETE FROM T WHERE Id = 6");
            Assert.Throws<OleDbException>(() => AceInsert(path, "T", V("Id", Rows + 9, "Code", "c00007")));
            var afterAce = Indexes(path);
            TestContext.Current.TestOutputHelper?.WriteLine("after ACE's changes: " + string.Join("; ", afterAce.Select(i => i.Key + " " + i.Value)));

            // The engine after ACE: two rows, entry by entry, and a code it refuses as ACE did.
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                table.Insert(EngineRow(Row(Rows + 2)));
                table.Insert(EngineRow(Row(Rows + 3)));
                Assert.Throws<ConstraintViolationException>(() => table.Insert(EngineRow(V("Id", Rows + 9, "Code", "ace00008"))));
            }

            var rows = Rows + 2;
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Equal(rows, Convert.ToInt32(First(connection, "SELECT COUNT(*) FROM T"), CultureInfo.InvariantCulture));
                AssertIndexAgreesWithScan(connection, "Name", "=", Quoted("changed by ACE"), true, 1);
                AssertIndexAgreesWithScan(connection, "Amount", "=", "999.5", false, 1);
                AssertIndexAgreesWithScan(connection, "Code", "=", Quoted("ACE00008"), true, 1);
                AssertIndexAgreesWithScan(connection, "Code", "=", Quoted((string)Row(8)["Code"]), true, 0);
                AssertIndexAgreesWithScan(connection, "Id", "=", "6", false, 0);
                AssertIndexAgreesWithScan(connection, "Code", "=", Quoted((string)Row(6)["Code"]), true, 0);
                foreach (var i in new[] { Rows + 1, Rows + 2, Rows + 3 })
                {
                    AssertIndexAgreesWithScan(connection, "Code", "=", Quoted((string)Row(i)["Code"]), true, 1);
                    AssertIndexAgreesWithScan(connection, "Id", "=", i.ToString(CultureInfo.InvariantCulture), false, 1);
                }
                Assert.Equal(Row(Rows + 3)["Code"], First(connection, "SELECT TOP 1 Code FROM T ORDER BY Code DESC"));
            }

            // Every index reaches its rows, as it does once DAO has compacted the file.
            var withPart = Enumerable.Range(1, Rows + 3).Where(i => i != 6 && i != 8).Count(i => Row(i)["Part"] != null);
            var afterEngine = Indexes(path);
            AssertEveryIndexReachesItsRows(afterEngine, rows, withPart);
            var compacted = Compact(path);
            try { Assert.Equal(afterEngine.Values.Select(Reached), Indexes(compacted).Values.Select(Reached)); }
            finally { File.Delete(compacted); }

            // The engine's two rows add a key each where their values are new - their codes, times, ids, and names
            // with amounts - to the counts ACE left. (ACE's own changes do not keep the counts exact: its delete takes
            // no key off, its update adds none, and the insert it refused added one to the primary key's. So the
            // counts here are not the ones DAO gives the indexes it builds again.)
            var newKeys = new[] { "Code", "Made", "NameAmount", "PrimaryKey" };
            foreach (var index in afterAce)
                Assert.Equal(Keys(index.Value) + (newKeys.Contains(index.Key) ? 2 : 0), Keys(afterEngine[index.Key]));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void a_load_whose_unique_index_refuses_a_duplicate_leaves_the_file_as_it_was_for_ace_to_write_to(bool general)
        {
            var path = NewTable(general, accessMade: false);
            var before = Hash(path);
            var rows = EngineRows(Enumerable.Range(1, Rows));
            rows[2000]["Code"] = ((string)rows[100]["Code"]).ToLowerInvariant();
            using (var db = Database.Open(path))
            {
                var refused = Assert.Throws<BatchInsertException>(() => db.GetTable("T").BulkInsert(rows));
                Assert.Equal(2000, refused.RowIndex);
            }
            Assert.Equal(before, Hash(path));

            AceInsert(path, "T", Row(1));
            AssertTableIs(path, "T", "Id", new[] { Row(1) }, "after ACE added a row to the table the load left empty");
            AssertEveryIndexReachesItsRows(Indexes(path), 1, 1);
        }
    }
}
