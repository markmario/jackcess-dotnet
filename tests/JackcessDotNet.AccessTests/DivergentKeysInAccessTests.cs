using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// A file ACE made, with unique indexes whose keys the engine does not make as Access does, or did not
    /// (reviews of #52): Date/Time Extended values in the second form ACE stores (#51), and
    /// keys of a Text and a Long cut at 510 bytes (#56), among them two that differ only in the last byte cut off. The
    /// engine judges an entry a row's by making the row's key; where its key is not Access's it must believe the
    /// entry, or it takes out an entry Access wrote, and ACE can then neither find, update nor delete the row, and
    /// compacting drops the index. The engine changes rows of each table, trusting its indexes and verifying them;
    /// ACE then seeks every row through its unique index, updates every row, DAO compacts the file with every index
    /// kept, and ACE deletes every row. And a relationship on a Date/Time Extended column ACE keyed in the second
    /// form: after the engine's changes, ACE answers as it does when it has made them itself.
    /// <para>
    /// The files are the engine's Assets/ace-divergent-keys.accdb and ace-second-form-foreign-key.accdb (its
    /// tools/ace-divergent-keys.ps1 and ace-second-form-foreign-key.ps1).
    /// </para>
    /// </summary>
    public class DivergentKeysInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        private static readonly string[] Tables = { "Ext", "Cut", "Dbl", "Dt", "Pair" };

        private string DivergentFile() => EngineAsset("ace-divergent-keys.accdb");

        /// <summary>A copy of the engine's test asset <paramref name="name"/>.</summary>
        private string EngineAsset(string name)
        {
            var assets = typeof(DivergentKeysInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineAssetsDir").Value;
            return CopyOf(Path.Combine(assets, name));
        }

        private string CopyOf(string file)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + Path.GetExtension(file));
            File.Copy(file, path);
            File.SetAttributes(path, FileAttributes.Normal);
            return path;
        }

        private static T WithDao<T>(string path, Func<dynamic, T> use)
        {
            dynamic dbe = Dao.Engine;
            try
            {
                dynamic db = dbe.OpenDatabase(path);
                try { return use(db); }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
        }

        /// <summary>Each table's indexes, by name, as DAO lists them.</summary>
        private static List<string> Indexes(string path) => IndexesOf(path, Tables);

        private static List<string> IndexesOf(string path, params string[] tables)
        {
            return WithDao(path, db =>
            {
                var names = new List<string>();
                foreach (var table in tables)
                    foreach (dynamic index in db.TableDefs[table].Indexes)
                        names.Add(table + "." + (string)index.Name);
                names.Sort(StringComparer.Ordinal);
                return names;
            });
        }

        /// <summary>
        /// DAO, through a table recordset in its primary key's order: <paramref name="change"/> for each row of
        /// <paramref name="table"/>; what failed, by row.
        /// </summary>
        private static List<string> DaoEach(string path, string table, Action<dynamic> change)
        {
            return WithDao(path, db =>
            {
                var ids = new List<int>();
                dynamic read = db.OpenRecordset("SELECT Id FROM [" + table + "] ORDER BY Id", 4);
                while (!(bool)read.EOF) { ids.Add(Convert.ToInt32(read.Fields["Id"].Value)); read.MoveNext(); }
                read.Close();

                var failed = new List<string>();
                dynamic rs = db.OpenRecordset(table, 1);
                try
                {
                    rs.Index = "PrimaryKey";
                    foreach (var id in ids)
                    {
                        rs.Seek("=", id);
                        try { change(rs); }
                        catch (Exception e) when (e is System.Runtime.InteropServices.COMException || e is TargetInvocationException)
                        {
                            try { rs.CancelUpdate(); } catch (Exception) { }
                            failed.Add(table + " row " + id + ": " + (e.InnerException ?? e).Message);
                        }
                    }
                }
                finally { rs.Close(); }
                return failed;
            });
        }

        /// <summary>
        /// The rows DAO does not find through <paramref name="index"/> by the values of its columns, read by
        /// <paramref name="select"/> (every column, when null): a date ACE computed is read as its double, since
        /// .NET's DateTime does not hold every double DAO's date does.
        /// </summary>
        private static List<string> NotSought(string path, string table, string index, string[] columns, string select = null)
        {
            return WithDao(path, db =>
            {
                var rows = new List<object[]>();
                dynamic read = db.OpenRecordset(select ?? "SELECT * FROM [" + table + "]", 4);
                while (!(bool)read.EOF)
                {
                    rows.Add(new object[] { read.Fields["Id"].Value }.Concat(columns.Select(c => (object)read.Fields[c].Value)).ToArray());
                    read.MoveNext();
                }
                read.Close();

                var missed = new List<string>();
                dynamic rs = db.OpenRecordset(table, 1);
                try
                {
                    rs.Index = index;
                    foreach (var row in rows)
                    {
                        if (columns.Length == 1) rs.Seek("=", row[1]);
                        else rs.Seek("=", row[1], row[2]);
                        if ((bool)rs.NoMatch || Convert.ToInt32(rs.Fields["Id"].Value) != Convert.ToInt32(row[0]))
                            missed.Add(table + " row " + row[0]);
                    }
                }
                finally { rs.Close(); }
                return missed;
            });
        }

        private static void Update(object recordset, string column, Func<object, object> to)
        {
            dynamic rs = recordset;
            rs.Edit();
            rs.Fields[column].Value = to(rs.Fields[column].Value);
            rs.Update();
        }

        // The engine adds, changes and deletes rows of each table, its indexes trusted or verified - not the rows
        // of Ext whose values are in ACE's second form, which it keys otherwise than Access (#51). ACE then finds
        // every row through each unique index, changes every row's key, compacts the file with every index, and
        // deletes every row. Pair holds (Ω, 0) and (Ω, 1), which ACE took: the key the digest before #56 gives
        // (Ω, 1), (Ω, 2) and (Ω, 255) is ACE's key of (Ω, 0), and the one it gives (Ω, 8356114) ACE's key of (Ω, 1) -
        // which the engine took, at 78deb30, for duplicates, and refused.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void access_seeks_updates_and_deletes_every_row_and_compacts_every_index_after_the_engine_changed_rows(bool verify)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = DivergentFile();
            var indexes = Indexes(path);
            Assert.Equal(10, indexes.Count);

            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = verify;
                var ext = db.GetTable("Ext");
                ext.Insert(new Row { ["Id"] = 10, ["Nt"] = "new", ["D"] = new DateTime(2021, 5, 5) });
                ext.UpdateByPrimaryKey(1, new Row { ["Nt"] = "one again" });

                var cut = db.GetTable("Cut");
                cut.Insert(new Row { ["Id"] = 10, ["A"] = "x", ["K"] = 1 });
                cut.Insert(new Row { ["Id"] = 11, ["A"] = new string('Ω', 255), ["K"] = 4 });   // beside rows 1, 2 and 6
                cut.UpdateByPrimaryKey(4, new Row { ["K"] = 9 });
                cut.DeleteRow("Id", 6);

                var dbl = db.GetTable("Dbl");
                dbl.Insert(new Row { ["Id"] = 10, ["X"] = 2.5 });
                dbl.Insert(new Row { ["Id"] = 11, ["X"] = 4.5 });
                dbl.UpdateByPrimaryKey(2, new Row { ["X"] = 3.5 });
                dbl.DeleteRow("Id", 11);

                var dt = db.GetTable("Dt");
                dt.Insert(new Row { ["Id"] = 10, ["T"] = new DateTime(2021, 5, 5) });
                dt.UpdateByPrimaryKey(4, new Row { ["T"] = new DateTime(2022, 2, 2, 3, 4, 5) });
                dt.DeleteRow("Id", 1);

                var pair = db.GetTable("Pair");
                pair.UpdateByPrimaryKey(2, new Row { ["Nt"] = "engine" });
                foreach (var (id, x) in new[] { (3, 2), (4, 255), (5, 8356114) })
                    pair.Insert(new Row { ["Id"] = id, ["A"] = new string('Ω', 255), ["X"] = x, ["Nt"] = "engine" });
            }

            Assert.Empty(NotSought(path, "Cut", "ByAK", new[] { "A", "K" }));
            Assert.Empty(NotSought(path, "Dbl", "XU", new[] { "X" }));
            Assert.Empty(NotSought(path, "Dt", "TU", new[] { "Days" }, "SELECT Id, CDbl(T) AS Days FROM Dt"));
            Assert.Empty(NotSought(path, "Pair", "ByAX", new[] { "A", "X" }));

            Assert.Empty(DaoEach(path, "Ext", rs => Update((object)rs, "Nt", v => "changed")));
            Assert.Empty(DaoEach(path, "Cut", rs => Update((object)rs, "K", v => Convert.ToInt32(v) + 100)));
            Assert.Empty(DaoEach(path, "Dbl", rs => Update((object)rs, "X", v => Convert.ToDouble(v) + 100)));
            Assert.Empty(DaoEach(path, "Dt", rs => Update((object)rs, "T", v => ((DateTime)v).AddDays(1))));
            Assert.Empty(DaoEach(path, "Pair", rs => Update((object)rs, "X", v => Convert.ToInt32(v) + 1000)));

            var compacted = AccessReferee.Compact(path);
            Assert.Equal(indexes, Indexes(compacted));

            foreach (var table in Tables)
                Assert.Empty(DaoEach(path, table, rs => rs.Delete()));
        }

        // ── A relationship on a Date/Time Extended column ───────────────────

        // In ace-second-form-foreign-key.accdb P is keyed by D, a Date/Time Extended column, and C's PD is related to
        // it; p2's key and child 2's foreign key are in the second form ACE's SQL UPDATE stores, under keys of those
        // bytes (#51), which the engine does not make. Each probe is a change that asks ACE about p2, made on a copy
        // of the file of its own.
        private static readonly string[] Probes =
        {
            "DELETE FROM P WHERE Nm = 'p2'",
            "UPDATE C SET PD = PD WHERE Id = 2",
            "INSERT INTO C (Id, PD) SELECT 3, PD FROM C WHERE Id = 2",
            "UPDATE C SET PD = '2020-01-02 03:04:05' WHERE Id = 1",
            "INSERT INTO C (Id, PD) VALUES (4, '2020-01-02 03:04:05')",
        };

        /// <summary>
        /// What ACE answers of the file at <paramref name="path"/>: P's and C's rows in the order of each of their
        /// indexes, which DAO reads by walking the index; whether it takes or refuses each change of
        /// <see cref="Probes"/>; and the indexes DAO keeps when it compacts the file.
        /// </summary>
        private List<string> AceAnswers(string path)
        {
            var answers = new List<string>();
            foreach (var (table, index) in new[] { ("P", "PK"), ("C", "CPK"), ("C", "FKPD") })
                answers.Add(table + " by " + index + ": " + Answer(() => string.Join("; ", AccessReferee.DaoRows(path, table, index).Select(Shown))));
            foreach (var probe in Probes)
            {
                var copy = CopyOf(path);
                answers.Add(probe + ": " + Answer(() => WithDao(copy, db => { db.Execute(probe, 128); return "taken"; })));
            }
            answers.Add("compacted: " + Answer(() => string.Join(", ", IndexesOf(AccessReferee.Compact(CopyOf(path)), "P", "C"))));
            return answers;
        }

        private static string Answer(Func<string> ask)
        {
            try { return ask(); }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException || e is TargetInvocationException
                                      || e is Xunit.Sdk.XunitException)
            {
                return "refused: " + (e.InnerException ?? e).Message;
            }
        }

        private static string Shown(AccessReferee.Values row)
            => string.Join(" ", row.Select(v => v.Key + "=" + (v.Value is DateTime date
                ? date.ToString("o", CultureInfo.InvariantCulture)
                : Convert.ToString(v.Value, CultureInfo.InvariantCulture))));

        private static string AnswerTo(List<string> answers, string probe)
            => answers.Single(a => a.StartsWith(probe + ": ", StringComparison.Ordinal)).Substring(probe.Length + 2);

        // The engine, enforcing foreign keys, refuses to delete p2, which child 2 refers to, as ACE refuses it, its
        // indexes trusted or verified; ACE then answers everything as it did of the file before. At 78deb30, verified,
        // the engine searched C's index under the key it makes of p2's date, missed child 2, and deleted p2 from under
        // it, leaving p2's entries in P's indexes pointing at no row; trusting the index, so did main.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void access_answers_as_before_when_the_engine_refuses_to_delete_a_parent_whose_child_ace_keyed_in_the_second_form(bool verify)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EngineAsset("ace-second-form-foreign-key.accdb");
            var before = AceAnswers(path);
            Assert.StartsWith("refused", AnswerTo(before, "DELETE FROM P WHERE Nm = 'p2'"));

            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                db.VerifyIndexes = verify;
                Assert.Throws<ConstraintViolationException>(() => db.GetTable("P").DeleteRow("Nm", "p2"));
            }

            Assert.Equal(before, AceAnswers(path));
        }

        // The engine deletes child 2 and then p2, its indexes trusted or verified; ACE then answers everything as it
        // does of the file when it has made those deletes itself - p1 and child 1, through every index, and a child of
        // p2 refused. Each row's entries are found by the row they point at; found under the engine's key, at
        // 78deb30, p2's were left pointing at no row.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void access_answers_as_after_its_own_deletes_when_the_engine_deletes_a_child_and_a_parent_ace_keyed_in_the_second_form(bool verify)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EngineAsset("ace-second-form-foreign-key.accdb");
            var aceDeleted = CopyOf(path);
            WithDao(aceDeleted, db =>
            {
                db.Execute("DELETE FROM C WHERE Id = 2", 128);
                db.Execute("DELETE FROM P WHERE Nm = 'p2'", 128);
                return 0;
            });
            var expected = AceAnswers(aceDeleted);
            Assert.StartsWith("refused", AnswerTo(expected, "UPDATE C SET PD = '2020-01-02 03:04:05' WHERE Id = 1"));

            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                db.VerifyIndexes = verify;
                db.GetTable("C").DeleteRow("Id", 2);
                db.GetTable("P").DeleteRow("Nm", "p2");
            }

            Assert.Equal(expected, AceAnswers(path));
        }

        private static readonly DateTime Second = new DateTime(2020, 1, 2, 3, 4, 5);

        // The engine finds p2 by its primary key and child 2 through the relationship's index, each keyed by ACE in
        // its second form (#51; the review of #52's first finding: both "not found"), gives p2 another name, deletes
        // child 2 through the cursor that found it and then p2, the row found, and adds p3 and a child of it with the
        // date in the form ACE's INSERT writes - its indexes trusted or verified. ACE then answers everything as it
        // does of a copy where it made those changes itself: the rows through every index as DAO walks it, the five
        // changes that ask about the date, and the indexes compaction keeps. Nothing the engine left in an index is
        // one ACE trips on.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void access_answers_as_after_its_own_changes_when_the_engine_finds_changes_and_deletes_rows_ace_keyed_in_the_second_form(bool verify)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EngineAsset("ace-second-form-foreign-key.accdb");
            var aceChanged = CopyOf(path);
            WithDao(aceChanged, db =>
            {
                foreach (var sql in new[]
                {
                    "UPDATE P SET Nm = 'p2 again' WHERE Nm = 'p2'",
                    "DELETE FROM C WHERE Id = 2",
                    "DELETE FROM P WHERE Nm = 'p2 again'",
                    "INSERT INTO P (Nm, D) VALUES ('p3', '2020-01-02 03:04:05')",
                    "INSERT INTO C (Id, PD) VALUES (4, '2020-01-02 03:04:05')",
                })
                    db.Execute(sql, 128);
                return 0;
            });
            var expected = AceAnswers(aceChanged);

            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                db.VerifyIndexes = verify;
                var p = db.GetTable("P");
                var c = db.GetTable("C");
                var p2 = p.FindRowByPrimaryKey(Second);
                Assert.Equal("p2", p2?["Nm"]);
                p2["Nm"] = "p2 again";
                p.Update(p2);

                var children = c.CreateIndexCursor("FKPD");
                Assert.Equal(2, children.FindRow("PD", Second)?["Id"]);
                children.DeleteCurrentRow();
                p.Delete(p.FindRowByPrimaryKey(Second));

                p.Insert(new Row { ["Nm"] = "p3", ["D"] = Second });
                c.Insert(new Row { ["Id"] = 4, ["PD"] = Second });
            }

            Assert.Equal(expected, AceAnswers(path));
        }

        // ── Date/Time Extended values in every form ─────────────────────────

        private static readonly string[] FormTables = { "T", "U", "V", "W" };

        /// <summary>
        /// What ACE answers of a copy of ace-extended-date-forms.accdb: every row of each table through each of its
        /// indexes, as DAO walks the index's pages; the rows ACE's WHERE finds through the index on K for each date -
        /// it looks for the form its INSERT writes (DAO's Seek refuses a Date/Time Extended value: "Data type
        /// conversion error", measured); and the indexes DAO keeps, and the rows it reads through each, when it
        /// compacts the file.
        /// </summary>
        private List<string> FormsAnswers(string path)
        {
            var answers = new List<string>();
            void Walk(string file, string label)
            {
                foreach (var index in IndexesOf(file, FormTables))
                {
                    var (table, name) = (index.Substring(0, 1), index.Substring(2));
                    answers.Add(label + index + ": " + Answer(() => string.Join("; ", AccessReferee.DaoRows(file, table, name).Select(Shown))));
                }
            }
            Walk(path, "");
            var dates = new[]
            {
                "2020-01-02 03:04:05", "2020-01-02 03:04:06", "2020-01-02 00:00:00", "2020-01-02 00:00:01",
                "2020-01-02 00:00:00.0000001", "2020-01-02 03:04:05.1234567", "2020-01-01 23:59:59",
            };
            foreach (var table in FormTables)
                foreach (var date in dates)
                    answers.Add(table + " where K = '" + date + "': " + Answer(() => string.Join(",",
                        AccessReferee.DaoRows(path, "SELECT Id FROM [" + table + "] WHERE K = '" + date + "' ORDER BY Id").Select(r => Convert.ToString(r["Id"], CultureInfo.InvariantCulture)))));
            Walk(AccessReferee.Compact(CopyOf(path)), "compacted ");
            return answers;
        }

        // Assets/ace-extended-date-forms.accdb (the engine's tools/ace-extended-date-forms.ps1): rows ACE wrote in the
        // two forms it writes and in three patched in, with indexes ACE made over them, and W with none. The engine
        // adds rows, changes rows of every form in another column, deletes rows in each form, adds a unique index
        // over T - one date in seven forms - and a primary key over W - one date in four - its indexes trusted or
        // verified; ACE makes the same changes in a copy. ACE then answers the two alike: every row of every table
        // through each index, as DAO walks it; what its WHERE finds through the index on K; and DAO compacts both
        // with every index. Keyed by the dates they read, the unique index and the primary key were refused.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void access_answers_through_every_index_alike_when_the_engine_and_ace_changed_rows_in_every_form(bool verify)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EngineAsset("ace-extended-date-forms.accdb");
            var twin = CopyOf(path);
            WithDao(twin, db =>
            {
                foreach (var sql in new[]
                {
                    "INSERT INTO T (Id, TForm, K) VALUES (20, 'new', '2020-01-02 03:04:05.5')",
                    "INSERT INTO T (Id, TForm, K) VALUES (21, 'new a second on', '2020-01-02 03:04:06')",
                    "UPDATE T SET TForm = 'changed' WHERE Id IN (1, 2, 5, 7, 9)",
                    "DELETE FROM T WHERE Id IN (3, 11, 14)",
                    "CREATE UNIQUE INDEX KUnique ON T (K)",
                    "ALTER TABLE W ADD CONSTRAINT PrimaryKey PRIMARY KEY (K)",
                    "INSERT INTO U (Id, UForm, K) VALUES (6, 'a second on', '2020-01-02 03:04:06')",
                    "DELETE FROM U WHERE Id = 2",
                })
                    db.Execute(sql, 128);
                return 0;
            });

            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = verify;
                var t = db.GetTable("T");
                var u = db.GetTable("U");
                t.Insert(new Row { ["Id"] = 20, ["TForm"] = "new", ["K"] = Second.AddMilliseconds(500) });
                t.Insert(new Row { ["Id"] = 21, ["TForm"] = "new a second on", ["K"] = Second.AddSeconds(1) });
                foreach (var id in new[] { 1, 2, 5, 7, 9 }) t.UpdateByPrimaryKey(id, new Row { ["TForm"] = "changed" });
                foreach (var id in new[] { 3, 11, 14 }) t.DeleteRow("Id", id);
                t.AddIndex(new IndexDefinition("KUnique", "K") { IsUnique = true });
                db.GetTable("W").AddIndex(new IndexDefinition("PrimaryKey", "K") { IsPrimaryKey = true });
                u.Insert(new Row { ["Id"] = 6, ["UForm"] = "a second on", ["K"] = Second.AddSeconds(1) });
                u.DeleteRow("Id", 2);
            }

            Assert.Equal(FormsAnswers(twin), FormsAnswers(path));
        }

        // Assets/engine-c89bfe4-extended-date-keys.accdb (the engine's tools/earlier-extended-date-keys.ps1): a copy of
        // the forms file this library changed at c89bfe4, which keyed the date it read - W's index ByK, added over the
        // forms, and T's rows 2 and 3, rewritten, are under the key of the first form of their date. The engine deletes
        // W's rows 2 to 4 and T's row 3 and gives T's row 2 another date, its indexes trusted or verified: DAO then
        // walks every index of both tables to exactly the rows a scan reads, and compacts the file with every index.
        // Each entry c89bfe4 made is found under its key and goes with its row; left behind, it pointed at no row.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void access_walks_and_compacts_every_index_after_the_engine_changed_rows_an_earlier_version_keyed_by_their_date(bool verify)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EngineAsset("engine-c89bfe4-extended-date-keys.accdb");
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = verify;
                var w = db.GetTable("W");
                var t = db.GetTable("T");
                foreach (var id in new[] { 2, 3, 4 }) w.DeleteRow("Id", id);
                t.UpdateByPrimaryKey(2, new Row { ["K"] = new DateTime(2020, 1, 4) });
                t.DeleteRow("Id", 3);
            }

            foreach (var index in IndexesOf(path, "T", "W"))
            {
                var (table, name) = (index.Substring(0, 1), index.Substring(2));
                var scanned = AccessReferee.DaoRows(path, "SELECT * FROM [" + table + "]").Select(Shown).OrderBy(s => s, StringComparer.Ordinal);
                var walked = AccessReferee.DaoRows(path, table, name).Select(Shown).OrderBy(s => s, StringComparer.Ordinal);
                Assert.Equal(scanned, walked);
            }
            Assert.Equal(IndexesOf(path, "T", "W"), IndexesOf(AccessReferee.Compact(CopyOf(path)), "T", "W"));
        }

        /// <summary>
        /// What goes wrong when ACE and DAO use <paramref name="tables"/> of the file at <paramref name="path"/>:
        /// nothing, when DAO walks each index, in its order, to exactly the rows a scan reads; compacts a copy with
        /// every index; and deletes every row, a table at a time in the order given, through a table recordset - the
        /// row's entries found by its keys - after which every index walks to no row.
        /// </summary>
        private List<string> AccessWalksCompactsAndDeletes(string path, params string[] tables)
        {
            var wrong = new List<string>();
            void Walk(string when)
            {
                foreach (var index in IndexesOf(path, tables))
                {
                    var (table, name) = (index.Substring(0, index.IndexOf('.')), index.Substring(index.IndexOf('.') + 1));
                    var scanned = Answer(() => string.Join("; ", AccessReferee.DaoRows(path, "SELECT * FROM [" + table + "]").Select(Shown).OrderBy(s => s, StringComparer.Ordinal)));
                    var walked = Answer(() => string.Join("; ", AccessReferee.DaoRows(path, table, name).Select(Shown).OrderBy(s => s, StringComparer.Ordinal)));
                    if (walked != scanned) wrong.Add(when + index + " walked [" + walked + "], scanned [" + scanned + "]");
                }
            }
            Walk("");
            var compacted = Answer(() => string.Join(", ", IndexesOf(AccessReferee.Compact(CopyOf(path)), tables)));
            if (compacted != string.Join(", ", IndexesOf(path, tables))) wrong.Add("compacted: " + compacted);
            foreach (var table in tables)
                wrong.AddRange(WithDao(path, db =>
                {
                    var refused = new List<string>();
                    dynamic rs = db.OpenRecordset(table, 1);
                    try
                    {
                        while (!(bool)rs.EOF)
                        {
                            var id = Convert.ToString(rs.Fields["Id"].Value, CultureInfo.InvariantCulture);
                            try { rs.Delete(); }
                            catch (Exception e) when (e is System.Runtime.InteropServices.COMException || e is TargetInvocationException)
                            {
                                refused.Add("deleting " + table + " row " + id + ": " + (e.InnerException ?? e).Message);
                            }
                            rs.MoveNext();
                        }
                    }
                    finally { rs.Close(); }
                    return refused;
                }));
            Walk("deleted, ");
            return wrong;
        }

        private static void AssertNothingWentWrong(List<string> wrong)
            => Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));

        // U in the engine's earlier-version files (its tools/earlier-extended-date-keys.ps1): row 2, in the second form,
        // is in the unique KU under the key of no value, where 913240c entered it when it gave the row a new UForm, and
        // every row is in the unique KForm (K descending, UForm) under the key of its date as each version read it. The
        // engine, verifying its indexes, gives row 2 a new date (913240c's file) or a new UForm (c89bfe4's): DAO then
        // walks every index of U to exactly the rows a scan reads, compacts the file with every index, and deletes every
        // row, after which every index walks to none. The update entered row 2 under its new key a second time, after
        // the check that moved its wrong entry had (the review of #66): DAO walked it twice.
        [Theory]
        [InlineData("engine-913240c-extended-date-keys.accdb", "K")]
        [InlineData("engine-c89bfe4-extended-date-keys.accdb", "UForm")]
        public void access_walks_compacts_and_deletes_every_row_after_the_engine_verifying_its_indexes_changed_a_row_an_earlier_version_keyed_by_its_date(string asset, string column)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EngineAsset(asset);
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                var cursor = db.GetTable("U").CreateCursor();
                Assert.NotNull(cursor.FindRow("Id", 2));
                cursor.UpdateCurrentRow(column == "K" ? new Row { ["K"] = new DateTime(2020, 1, 3, 3, 4, 5) } : new Row { ["UForm"] = "changed" });
            }

            AssertNothingWentWrong(AccessWalksCompactsAndDeletes(path, "U"));
        }

        // The engine's c89bfe4 file: T's row 2, in the second form, is under the key of the first form of its date in
        // each of T's four indexes on K. The engine deletes T's rows but 1 and 2, makes T V's child on K - each of T's
        // rows has a parent with its bytes - with its updates cascading, and gives V's row 2, row 2's parent, another
        // date, its indexes trusted or verified. T's row 2 follows, and DAO then walks every index of T and V to exactly
        // the rows a scan reads, compacts the file with every index, and deletes every row, after which every index
        // walks to none. Verified, the update entered row 2 in KAsc - which the relationship's index VT shares - a
        // second time (the review of #66): DAO walked it twice, and once it had deleted row 2,
        // ACE refused to delete V's row 2, the entry left pointing at no row being a child of it. Trusting its indexes,
        // the engine looked for the child under the key of its parent's bytes alone, and left it with the date its
        // parent no longer has.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void access_walks_compacts_and_deletes_every_row_after_a_new_date_cascaded_to_a_child_an_earlier_version_keyed_by_its_date(bool verify)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EarlierVersionsChildOfV(cascade: true);
            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                db.VerifyIndexes = verify;
                var parent = db.GetTable("V").CreateCursor();
                Assert.NotNull(parent.FindRow("Id", 2));
                parent.UpdateCurrentRow(new Row { ["K"] = new DateTime(2020, 1, 3, 3, 4, 5) });
            }

            Assert.Equal(new[] { "Id=1 K=2020-01-02T03:04:05.0000000", "Id=2 K=2020-01-03T03:04:05.0000000" },
                         AccessReferee.DaoRows(path, "SELECT Id, K FROM T ORDER BY Id").Select(Shown));
            AssertNothingWentWrong(AccessWalksCompactsAndDeletes(path, "T", "V"));
        }

        /// <summary>
        /// A copy of the engine's c89bfe4 file with T's rows but 1 and 2 deleted and T made V's child on K, with
        /// referential integrity, its updates cascading when <paramref name="cascade"/> is set - each of T's rows has a
        /// parent with its bytes - all by the engine.
        /// </summary>
        private string EarlierVersionsChildOfV(bool cascade)
        {
            var path = EngineAsset("engine-c89bfe4-extended-date-keys.accdb");
            using (var db = Database.Open(path))
            {
                var t = db.GetTable("T");
                foreach (var id in Enumerable.Range(3, 13)) t.DeleteRow("Id", id);
                db.CreateRelationship(new RelationshipDefinition("V", "K", "T", "K") { Name = "VT", HasReferentialIntegrity = true, CascadeUpdates = cascade });
            }
            return path;
        }

        // The same child, its relationship's deletes not cascading. ACE, asked to delete V's row 2 - the parent of T's
        // row 2 - looks for the child under the key of the parent's bytes, does not find it there, and deletes the parent
        // from under it (measured): the child's entry is where c89bfe4 put it, under the key of its date. The engine
        // knows that key, finds the child there, and refuses, its indexes trusted or verified, leaving the file as it
        // was - which ACE goes on reading as c89bfe4 left it: it does not delete T's row 2 either ("The search key was
        // not found in any record", measured), and takes the row for a child of V's row 1, whose bytes are the key c89bfe4
        // made. Trusting its indexes and looking under the key of the parent's bytes alone, the engine deleted the
        // parent, as ACE does (the review of #66).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void the_engine_refuses_to_delete_a_parent_whose_child_an_earlier_version_keyed_by_its_date_which_access_does_not_find(bool verify)
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EarlierVersionsChildOfV(cascade: false);
            var aceDeletes = Answer(() => WithDao(CopyOf(path), db => { db.Execute("DELETE FROM V WHERE Id = 2", 128); return "taken"; }));
            var before = File.ReadAllBytes(path);

            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                db.VerifyIndexes = verify;
                var parent = db.GetTable("V").CreateCursor();
                Assert.NotNull(parent.FindRow("Id", 2));
                Assert.Throws<ConstraintViolationException>(() => parent.DeleteCurrentRow());
            }

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal("taken", aceDeletes);
        }
    }
}
