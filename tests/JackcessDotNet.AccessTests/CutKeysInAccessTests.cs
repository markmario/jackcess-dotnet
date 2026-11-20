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
    /// Keys of several columns that Access cuts at 510 bytes (#56), refereed by Access.
    /// Access ends a cut key in a digest of every byte it cut off. The engine left the last of them out, which for
    /// a key that ends in a number after text that filled the entry is another digest: Access, which looks for the
    /// entry it would write itself, then could neither update nor delete the row ("The search key was not found in
    /// any record"), nor find it by Seek. The engine's tests hold its keys to ACE's byte for byte; here DAO seeks,
    /// updates and deletes the rows the engine writes, the engine leaves the entries Access wrote as they are, and
    /// a file the engine wrote with the earlier digest is one Access can change again once the engine has
    /// rewritten its rows.
    /// </summary>
    public class CutKeysInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        // ── The table ────────────────────────────────────────────────────────

        // Values of A whose keys end either side of 508 and 510 bytes, in General - Legacy (é then a) and in
        // General (a, and é then a), so that the cut falls in A or in the K after it, and longer ones.
        private static string Run(char c, int n) { return new string(c, n); }

        private static readonly string[] AValues =
        {
            "short", Run('Ω', 255), Run('Ϊ', 200), Run('a', 255),
            Run('é', 247) + Run('a', 8), Run('é', 248) + Run('a', 7), Run('é', 249) + Run('a', 6), Run('é', 250) + Run('a', 5),
            Run('é', 251) + Run('a', 4), Run('é', 252) + Run('a', 3), Run('é', 253) + Run('a', 2),
            Run('a', 252), Run('a', 253), "é" + Run('a', 251), "é" + Run('a', 252), "é" + Run('a', 253),
        };

        // Each index by name, and its columns in key order: text cut in the first, middle and last column, before
        // a Long ascending and descending, after another text, and unique.
        private static readonly Dictionary<string, string[]> Indexes = new Dictionary<string, string[]>
        {
            { "ByAK", new[] { "A", "K" } }, { "ByADescK", new[] { "A", "K" } }, { "ByAKDesc", new[] { "A", "K" } },
            { "ByKA", new[] { "K", "A" } }, { "ByKAJ", new[] { "K", "A", "J" } }, { "ByABK", new[] { "A", "B", "K" } },
            { "UniqueAJ", new[] { "A", "J" } },
        };

        /// <summary>Each value of A twice: with K 1 and B 'b', and with K 70000 and B 255 of è. J is three times Id.</summary>
        private static List<Values> Rows(int firstId = 1)
        {
            var rows = new List<Values>();
            for (var i = 0; i < AValues.Length * 2; i++)
            {
                var id = firstId + i;
                var second = i % 2 == 1;
                rows.Add(V("Id", id, "A", AValues[i / 2], "B", second ? Run('è', 255) : "b", "K", second ? 70000 : 1, "J", id * 3));
            }
            return rows;
        }

        /// <summary>A copy of an Access 2010 database in the General order, from the engine's corpus.</summary>
        private string GeneralDatabase()
        {
            var corpus = typeof(CutKeysInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineCorpusDir").Value;
            var path = Path.Combine(_tmp.Dir, "general-" + Guid.NewGuid().ToString("N") + ".accdb");
            File.Copy(Path.Combine(corpus, "V2010", "common1V2010.accdb"), path);
            File.SetAttributes(path, FileAttributes.Normal);
            return path;
        }

        /// <summary>
        /// A database with an empty table Cut and its seven indexes: made by ACE (DAO's database, or the General
        /// copy, and ACE's DDL) or by the engine.
        /// </summary>
        private string NewDatabase(bool general, bool accessMade)
        {
            var path = general ? GeneralDatabase() : _tmp.NewMdbPath();
            if (accessMade)
            {
                if (!general) DaoCreate(path, 64);
                Execute(path,
                    "CREATE TABLE Cut (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, A TEXT(255), B TEXT(255), K LONG, J LONG)",
                    "CREATE INDEX ByAK ON Cut (A, K)", "CREATE INDEX ByADescK ON Cut (A DESC, K)", "CREATE INDEX ByAKDesc ON Cut (A, K DESC)",
                    "CREATE INDEX ByKA ON Cut (K, A)", "CREATE INDEX ByKAJ ON Cut (K, A, J)", "CREATE INDEX ByABK ON Cut (A, B, K)",
                    "CREATE UNIQUE INDEX UniqueAJ ON Cut (A, J)");
                return path;
            }
            using (var db = general ? Database.Open(path) : Database.Create(path, FileFormat.Access2000))
            {
                Func<string, string[], string[], IndexDefinition> index = (name, columns, descending) =>
                {
                    var definition = new IndexDefinition(name, columns);
                    foreach (var column in descending) definition.DescendingColumns.Add(column);
                    return definition;
                };
                db.CreateTable("Cut", new[]
                {
                    new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
                    new ColumnDefinition("B", DataType.Text) { MaxLength = 255 }, new ColumnDefinition("K", DataType.Int32),
                    new ColumnDefinition("J", DataType.Int32),
                }, new[] { "Id" }, new[]
                {
                    index("ByAK", new[] { "A", "K" }, new string[0]), index("ByADescK", new[] { "A", "K" }, new[] { "A" }),
                    index("ByAKDesc", new[] { "A", "K" }, new[] { "K" }), index("ByKA", new[] { "K", "A" }, new string[0]),
                    index("ByKAJ", new[] { "K", "A", "J" }, new string[0]), index("ByABK", new[] { "A", "B", "K" }, new string[0]),
                    new IndexDefinition("UniqueAJ", "A", "J") { IsUnique = true },
                });
            }
            return path;
        }

        // ── DAO ──────────────────────────────────────────────────────────────

        private static T WithDao<T>(string path, Func<dynamic, T> use)
        {
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = dbe.OpenDatabase(path);
                try { return use(db); }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
        }

        /// <summary>
        /// The rows DAO does not find through each index by the values of all its columns, as "index row Id": DAO's
        /// Seek goes down the index by the key Access makes of the values.
        /// </summary>
        private static List<string> NotSought(string path, string table, IList<Values> rows, Dictionary<string, string[]> indexes)
        {
            return WithDao(path, db =>
            {
                var missed = new List<string>();
                foreach (var index in indexes)
                {
                    dynamic recordset = db.OpenRecordset(table, 1);   // dbOpenTable
                    try
                    {
                        recordset.Index = index.Key;
                        foreach (var row in rows)
                        {
                            var key = index.Value.Select(c => row[c]).ToArray();
                            if (key.Length == 2) recordset.Seek("=", key[0], key[1]);
                            else recordset.Seek("=", key[0], key[1], key[2]);
                            if ((bool)recordset.NoMatch || Convert.ToInt32(recordset.Fields["Id"].Value) != Convert.ToInt32(row["Id"]))
                                missed.Add(index.Key + " row " + row["Id"]);
                        }
                    }
                    finally { recordset.Close(); }
                }
                return missed;
            });
        }

        /// <summary>
        /// DAO changes each row, found by its Id through <paramref name="byIndex"/>, through a table recordset; what
        /// failed, by row.
        /// </summary>
        private static List<string> DaoEach(string path, string table, IEnumerable<int> ids, Action<dynamic> change, string byIndex = "PrimaryKey")
        {
            return WithDao(path, db =>
            {
                var failed = new List<string>();
                dynamic recordset = db.OpenRecordset(table, 1);
                try
                {
                    recordset.Index = byIndex;
                    foreach (var id in ids)
                    {
                        recordset.Seek("=", id);
                        Assert.False((bool)recordset.NoMatch, table + " has no row " + id);
                        try { change(recordset); }
                        catch (Exception e) when (e is System.Runtime.InteropServices.COMException || e is TargetInvocationException)
                        {
                            try { recordset.CancelUpdate(); } catch (Exception) { }
                            failed.Add("row " + id + ": " + (e.InnerException ?? e).Message);
                        }
                    }
                }
                finally { recordset.Close(); }
                return failed;
            });
        }

        private static void DaoUpdate(dynamic recordset, string column, int by)
        {
            recordset.Edit();
            recordset.Fields[column].Value = Convert.ToInt32(recordset.Fields[column].Value) + by;
            recordset.Update();
        }

        // ── The engine writes, Access changes ────────────────────────────────

        // The engine writes the rows into a table it made or ACE made, in either order; DAO seeks each through every
        // index by all its values, updates every row (K and J, in six of the seven indexes) and deletes half; the
        // engine then rewrites and adds rows beside Access's, and Access finds them all, reads them as the engine
        // does, compacts the file whole, and deletes every row.
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void rows_the_engine_writes_with_cut_keys_are_rows_access_seeks_updates_and_deletes(bool general, bool accessMade)
        {
            var path = NewDatabase(general, accessMade);
            var rows = Rows();
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Cut");
                foreach (var row in rows) table.Insert(EngineRow(row));
            }
            Assert.Empty(NotSought(path, "Cut", rows, Indexes));

            Assert.Empty(DaoEach(path, "Cut", rows.Select(r => (int)r["Id"]), r => { DaoUpdate(r, "K", 2); DaoUpdate(r, "J", 1000); }));
            foreach (var row in rows) { row["K"] = (int)row["K"] + 2; row["J"] = (int)row["J"] + 1000; }
            var deleted = rows.Where(r => (int)r["Id"] % 2 == 0).ToList();
            Assert.Empty(DaoEach(path, "Cut", deleted.Select(r => (int)r["Id"]), r => r.Delete()));
            rows = rows.Except(deleted).ToList();
            Assert.Empty(NotSought(path, "Cut", rows, Indexes));

            // The engine after Access: the rows Access changed rewritten, and new ones beside them.
            var added = Rows(firstId: 101);
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Cut");
                foreach (var row in rows)
                {
                    row["K"] = (int)row["K"] + 2;
                    table.UpdateByPrimaryKey((int)row["Id"], new Row { ["K"] = row["K"] });
                }
                foreach (var row in added) table.Insert(EngineRow(row));
            }
            rows.AddRange(added);
            Assert.Empty(NotSought(path, "Cut", rows, Indexes));
            AssertTableIs(path, "Cut", "Id", rows, "after DAO and the engine changed it");

            Assert.Empty(DaoEach(path, "Cut", rows.Select(r => (int)r["Id"]), r => r.Delete()));
            Assert.Equal(0, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM Cut")));
        }

        // ── Indexes the engine adds to a table that has its rows ─────────────

        // The engine makes the table with no index, fills it, and then adds a primary key and the other indexes
        // over its rows (Table.AddIndex). DAO seeks every row through each, updates every row and deletes it.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void indexes_and_a_primary_key_the_engine_adds_over_rows_are_ones_access_seeks_updates_and_deletes_through(bool general)
        {
            var path = general ? GeneralDatabase() : _tmp.NewMdbPath();
            var rows = Rows();
            using (var db = general ? Database.Open(path) : Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Cut", new[]
                {
                    new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
                    new ColumnDefinition("B", DataType.Text) { MaxLength = 255 }, new ColumnDefinition("K", DataType.Int32),
                    new ColumnDefinition("J", DataType.Int32),
                });
                foreach (var row in rows) table.Insert(EngineRow(row));

                table.AddIndex(new IndexDefinition("PrimaryKey", "A", "J") { IsPrimaryKey = true });
                table.AddIndex(new IndexDefinition("ById", "Id") { IsUnique = true });
                foreach (var index in Indexes)
                {
                    var definition = new IndexDefinition(index.Key, index.Value) { IsUnique = index.Key.StartsWith("Unique", StringComparison.Ordinal) };
                    if (index.Key == "ByADescK") definition.DescendingColumns.Add("A");
                    if (index.Key == "ByAKDesc") definition.DescendingColumns.Add("K");
                    table.AddIndex(definition);
                }
            }

            var withKey = new Dictionary<string, string[]>(Indexes) { { "PrimaryKey", new[] { "A", "J" } } };
            Assert.Empty(NotSought(path, "Cut", rows, withKey));
            Assert.Empty(DaoEach(path, "Cut", rows.Select(r => (int)r["Id"]), r => { DaoUpdate(r, "K", 2); DaoUpdate(r, "J", 1000); }, byIndex: "ById"));
            foreach (var row in rows) { row["K"] = (int)row["K"] + 2; row["J"] = (int)row["J"] + 1000; }
            Assert.Empty(NotSought(path, "Cut", rows, withKey));
            AssertTableIs(path, "Cut", "Id", rows, "after DAO changed every row");
            Assert.Empty(DaoEach(path, "Cut", rows.Select(r => (int)r["Id"]), r => r.Delete(), byIndex: "ById"));
        }

        // ── Access writes, the engine changes other rows ─────────────────────

        // Access writes every row. The engine, verifying the indexes it uses - which reads the unique index whole
        // and takes out every entry it judges no row's - adds a row beside Access's, changes one and deletes one.
        // Every entry Access wrote is still there to be found, so DAO seeks, updates and deletes every row: an
        // entry Access wrote is never taken for one that is no row's.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void the_entries_access_wrote_are_left_when_the_engine_verifying_its_indexes_changes_other_rows(bool general)
        {
            var path = NewDatabase(general, accessMade: true);
            var rows = Rows();
            foreach (var row in rows) AceInsert(path, "Cut", row);

            var beside = V("Id", 100, "A", AValues[1], "B", "c", "K", 2, "J", 10);   // beside rows 3 and 4
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                var table = db.GetTable("Cut");
                table.Insert(EngineRow(beside));
                table.UpdateByPrimaryKey(5, new Row { ["K"] = 3 });
                table.DeleteRow("Id", 6);
            }
            rows.Single(r => (int)r["Id"] == 5)["K"] = 3;
            rows.RemoveAll(r => (int)r["Id"] == 6);
            rows.Add(beside);

            Assert.Empty(NotSought(path, "Cut", rows, Indexes));
            AssertTableIs(path, "Cut", "Id", rows, "after the engine changed three rows");
            Assert.Empty(DaoEach(path, "Cut", rows.Select(r => (int)r["Id"]), r => { DaoUpdate(r, "K", 2); DaoUpdate(r, "J", 1000); }));
            Assert.Empty(DaoEach(path, "Cut", rows.Select(r => (int)r["Id"]), r => r.Delete()));
        }

        // ── A file the engine wrote with the earlier digest ──────────────────

        // The engine's tests keep a file it wrote at 913240c, before #56: table Pairs, whose cut keys that end in a
        // number have the earlier digest. Access cannot delete such a row. Once the engine has rewritten the rows,
        // each is under the key Access makes, and DAO seeks, updates and deletes every one.
        [Fact]
        public void a_file_written_with_the_earlier_digest_is_one_access_changes_once_the_engine_has_rewritten_its_rows()
        {
            var assets = typeof(CutKeysInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineAssetsDir").Value;
            var path = _tmp.NewMdbPath();
            File.Copy(Path.Combine(assets, "engine-913240c-cut-keys.mdb"), path);
            var pairs = new Dictionary<string, string[]>
            {
                { "ByAK", new[] { "A", "K" } }, { "ByKA", new[] { "K", "A" } }, { "UniqueAN", new[] { "A", "N" } },
            };

            // As the earlier engine left it: Access misses the row through the index, and cannot delete it.
            var rows = AceTable(path, "Pairs");
            Assert.Equal(9, rows.Count);
            Assert.Contains("ByAK row 2", NotSought(path, "Pairs", rows, pairs));
            Assert.Contains("search key was not found", Assert.Single(DaoEach(path, "Pairs", new[] { 2 }, r => r.Delete())));

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Pairs");
                foreach (var row in rows)
                {
                    row["N"] = (int)row["N"] + 1;
                    table.UpdateByPrimaryKey((int)row["Id"], new Row { ["N"] = row["N"] });
                }
            }

            Assert.Empty(NotSought(path, "Pairs", rows, pairs));
            AssertTableIs(path, "Pairs", "Id", rows, "after the engine rewrote every row");
            Assert.Empty(DaoEach(path, "Pairs", rows.Select(r => (int)r["Id"]), r => DaoUpdate(r, "K", 1)));
            Assert.Empty(DaoEach(path, "Pairs", rows.Select(r => (int)r["Id"]), r => r.Delete()));
        }
    }
}
