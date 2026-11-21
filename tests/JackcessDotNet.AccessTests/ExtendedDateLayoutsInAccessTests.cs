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
    /// Date/Time Extended values in each layout Access writes, and in the shapes Access reads a date from
    /// (#64). ACE's SQL <c>INSERT</c> writes the day in nineteen digits and the time of day in
    /// 100-nanosecond units; its <c>UPDATE</c> and DAO's <c>AddNew</c> and <c>Edit</c> on a dynaset the time in whole
    /// seconds; and DAO's <c>AddNew</c> and <c>Edit</c> on a table-type recordset - what <c>OpenRecordset</c> gives a
    /// table when it is asked for no other kind - the day in fifteen digits and four bytes after the value that are
    /// whatever DAO's buffer held. ACE keys each row by its 42 bytes, and reads them as three numbers. DAO writes the
    /// rows here, through both kinds of recordset and through SQL: the engine reads each as DAO reads it, finds by a date
    /// through its cursors and a <see cref="Joiner"/> the rows ACE finds by reading the table, and updates and deletes
    /// rows in every layout; DAO then reads the rows, walks each index, seeks every row through the primary key and
    /// compacts the file, and ACE counts through the index. (DAO's <c>Seek</c> refuses a Date/Time Extended value,
    /// measured: its walk of an index is the referee of that index.)
    /// <para>
    /// An ACE without the column type - CI's redistributable - skips them.
    /// </para>
    /// </summary>
    public class ExtendedDateLayoutsInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        private const int DbOpenTable = 1, DbOpenDynaset = 2, DbOpenSnapshot = 4, DbFailOnError = 128;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private static readonly DateTime D0 = new DateTime(2020, 1, 2, 3, 4, 5);
        private static readonly DateTime D1 = D0.AddTicks(1234567);
        private static readonly DateTime Leap = new DateTime(1600, 2, 29, 1, 2, 3).AddTicks(4567891);

        /// <summary>A date as ACE's SQL is given one: seven digits of the fraction, which it reads as so many ticks.</summary>
        private static string Literal(DateTime date) => date.ToString("yyyy-MM-dd HH:mm:ss.fffffff", Invariant);

        // ── DAO ─────────────────────────────────────────────────────────────

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

        private static void WithDao(string path, Action<dynamic> use) => WithDao<object>(path, db => { use(db); return null; });

        /// <summary>DAO adds a row through a recordset of <paramref name="type"/>, each value given as the type it is.</summary>
        private static void AddNew(dynamic db, string table, int type, params object[] namesAndValues)
        {
            dynamic rs = db.OpenRecordset(table, type);
            try
            {
                rs.AddNew();
                for (int i = 0; i < namesAndValues.Length; i += 2) rs.Fields[(string)namesAndValues[i]].Value = namesAndValues[i + 1];
                rs.Update();
            }
            finally { rs.Close(); }
        }

        /// <summary>DAO gives T's row <paramref name="id"/> a value of K through a recordset of <paramref name="type"/>.</summary>
        private static void Edit(dynamic db, int type, int id, object value)
        {
            dynamic rs = db.OpenRecordset("T", type);
            try
            {
                if (type == DbOpenTable)
                {
                    rs.Index = "TPK";
                    rs.Seek("=", id);
                }
                else rs.FindFirst("Id = " + id);
                rs.Edit();
                rs.Fields["K"].Value = value;
                rs.Update();
            }
            finally { rs.Close(); }
        }

        /// <summary>What DAO reads of each row's K, by Id: the text it hands out, null for a Null and for Empty, which it gives for bytes it reads no value from.</summary>
        private static Dictionary<int, string> DaoReads(string path, string table)
        {
            return WithDao(path, db =>
            {
                var read = new Dictionary<int, string>();
                dynamic rs = db.OpenRecordset("SELECT Id, K FROM [" + table + "]", DbOpenSnapshot);
                try
                {
                    while (!(bool)rs.EOF)
                    {
                        object value = rs.Fields["K"].Value;
                        read[Convert.ToInt32(rs.Fields["Id"].Value)] = value as string;
                        rs.MoveNext();
                    }
                }
                finally { rs.Close(); }
                return read;
            });
        }

        /// <summary>The Ids DAO walks <paramref name="index"/> of <paramref name="table"/> in: its own pages, in its order.</summary>
        private static List<int> DaoWalk(string path, string table, string index)
        {
            return WithDao(path, db =>
            {
                var ids = new List<int>();
                dynamic rs = db.OpenRecordset(table, DbOpenTable);
                try
                {
                    rs.Index = index;
                    while (!(bool)rs.EOF)
                    {
                        ids.Add(Convert.ToInt32(rs.Fields["Id"].Value));
                        rs.MoveNext();
                    }
                }
                finally { rs.Close(); }
                return ids;
            });
        }

        /// <summary>The Ids DAO's Seek finds through <paramref name="table"/>'s primary key <paramref name="index"/>, of those asked for.</summary>
        private static List<int> DaoSeeks(string path, string table, string index, IEnumerable<int> ids)
        {
            return WithDao(path, db =>
            {
                var found = new List<int>();
                dynamic rs = db.OpenRecordset(table, DbOpenTable);
                try
                {
                    rs.Index = index;
                    foreach (int id in ids)
                    {
                        rs.Seek("=", id);
                        if (!(bool)rs.NoMatch) found.Add(Convert.ToInt32(rs.Fields["Id"].Value));
                    }
                }
                finally { rs.Close(); }
                return found;
            });
        }

        private static List<string> DaoIndexes(string path, string table)
        {
            return WithDao(path, db =>
            {
                var names = new List<string>();
                foreach (dynamic index in db.TableDefs[table].Indexes) names.Add((string)index.Name);
                names.Sort(StringComparer.Ordinal);
                return names;
            });
        }

        // ── ACE and the engine ───────────────────────────────────────────────

        private static List<int> AceIds(string path, string sql)
            => AccessReferee.AceRows(path, sql).Select(r => Convert.ToInt32(r["Id"])).OrderBy(id => id).ToList();

        private static int AceCount(string path, string sql) => Convert.ToInt32(AccessReferee.Scalar(path, sql));

        private static Dictionary<int, DateTime?> EngineReads(string path, string table)
        {
            using (var db = Database.Open(path))
                return db.GetTable(table).Rows.ToDictionary(r => Convert.ToInt32(r["Id"]), r => (DateTime?)r["K"]);
        }

        private static List<int> IdsOf(IEnumerable<Row> rows) => rows.Select(r => Convert.ToInt32(r["Id"])).ToList();

        private string CopyOf(string file)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + Path.GetExtension(file));
            File.Copy(file, path);
            File.SetAttributes(path, FileAttributes.Normal);
            return path;
        }

        private string EngineAsset(string name)
        {
            var assets = typeof(ExtendedDateLayoutsInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineAssetsDir").Value;
            return CopyOf(Path.Combine(assets, name));
        }

        // ── The database ─────────────────────────────────────────────────────

        /// <summary>T's rows: Id, how it was written, and the date DAO reads it as.</summary>
        private static readonly (int Id, string How, DateTime Reads)[] Made =
        {
            (1,  "INSERT",                    D0),
            (2,  "INSERT",                    D1),
            (3,  "UPDATE",                    D0),
            (4,  "AddNew, dynaset, text",     D0),   // D1 given: a dynaset takes the date to the whole second
            (5,  "AddNew, dynaset, a Date",   D0),
            (6,  "Edit, dynaset, text",       D0),
            (7,  "AddNew, table-type, text",  D0),
            (8,  "AddNew, table-type, a Date", D0),
            (9,  "AddNew, table-type, text",  D1),
            (10, "Edit, table-type, text",    D1),
            (11, "Edit, table-type, a Date",  D0),
            (12, "AddNew, table-type, text",  Leap),
            (13, "AddNew, table-type, text",  DateTime.MinValue),
            (14, "AddNew, table-type, text",  DateTime.MaxValue),
            (15, "INSERT",                    DateTime.MaxValue),
        };

        private static bool IsTableType(int id) => id >= 7 && id <= 14;

        /// <summary>
        /// A database DAO makes, with T: KAsc (K) and KDesc (K DESC) made before the rows, so that ACE keys each as it is
        /// added, and the rows of <see cref="Made"/>, each written as its How says.
        /// </summary>
        private string MakeT()
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + ".accdb");
            AccessReferee.DaoCreate(path, 256);
            WithDao(path, db =>
            {
                foreach (var sql in new[]
                {
                    "CREATE TABLE T (Id LONG CONSTRAINT TPK PRIMARY KEY, How TEXT(40), K DATETIME2)",
                    "CREATE INDEX KAsc ON T (K)", "CREATE INDEX KDesc ON T (K DESC)",
                    "INSERT INTO T (Id, How, K) VALUES (1, 'INSERT', '" + Literal(D0) + "')",
                    "INSERT INTO T (Id, How, K) VALUES (2, 'INSERT', '" + Literal(D1) + "')",
                    "INSERT INTO T (Id, How, K) VALUES (3, 'UPDATE', '2020-01-09 00:00:00')",
                    "UPDATE T SET K = '" + Literal(D0) + "' WHERE Id = 3",
                    "INSERT INTO T (Id, How, K) VALUES (6, 'Edit, dynaset, text', '2020-01-09 00:00:00')",
                    "INSERT INTO T (Id, How, K) VALUES (10, 'Edit, table-type, text', '2020-01-09 00:00:00')",
                    "INSERT INTO T (Id, How, K) VALUES (11, 'Edit, table-type, a Date', '2020-01-09 00:00:00')",
                    "INSERT INTO T (Id, How, K) VALUES (15, 'INSERT', '" + Literal(DateTime.MaxValue) + "')",
                })
                    db.Execute(sql, DbFailOnError);
                AddNew(db, "T", DbOpenDynaset, "Id", 4, "How", "AddNew, dynaset, text", "K", Literal(D1));
                AddNew(db, "T", DbOpenDynaset, "Id", 5, "How", "AddNew, dynaset, a Date", "K", D0);
                Edit(db, DbOpenDynaset, 6, Literal(D0));
                AddNew(db, "T", DbOpenTable, "Id", 7, "How", "AddNew, table-type, text", "K", Literal(D0));
                AddNew(db, "T", DbOpenTable, "Id", 8, "How", "AddNew, table-type, a Date", "K", D0);
                AddNew(db, "T", DbOpenTable, "Id", 9, "How", "AddNew, table-type, text", "K", Literal(D1));
                Edit(db, DbOpenTable, 10, Literal(D1));
                Edit(db, DbOpenTable, 11, D0);
                AddNew(db, "T", DbOpenTable, "Id", 12, "How", "AddNew, table-type, text", "K", Literal(Leap));
                AddNew(db, "T", DbOpenTable, "Id", 13, "How", "AddNew, table-type, text", "K", Literal(DateTime.MinValue));
                AddNew(db, "T", DbOpenTable, "Id", 14, "How", "AddNew, table-type, text", "K", Literal(DateTime.MaxValue));
            });
            return path;
        }

        // The layouts as text: the first form, and the 38 bytes the third begins with.
        private static string FirstForm(DateTime date)
            => (date.Ticks / TimeSpan.TicksPerDay).ToString("D19", Invariant) + ":" + (date.Ticks % TimeSpan.TicksPerDay).ToString("D19", Invariant) + ":7\0";

        private static string TableTypeStart(DateTime date)
            => (date.Ticks / TimeSpan.TicksPerDay).ToString("D15", Invariant) + ":" + (date.Ticks % TimeSpan.TicksPerDay).ToString("D19", Invariant) + ":7\0";

        private static int IndexOf(byte[] file, byte[] bytes, int from = 0)
        {
            for (int i = from; i <= file.Length - bytes.Length; i++)
            {
                int j = 0;
                while (j < bytes.Length && file[i + j] == bytes[j]) j++;
                if (j == bytes.Length) return i;
            }
            return -1;
        }

        private static byte[] Latin(string text) => text.Select(c => checked((byte)c)).ToArray();

        // ── Reading ──────────────────────────────────────────────────────────

        /// <summary>
        /// DAO reads each row as the date it was given - a dynaset's to the whole second - and the engine reads each as
        /// DAO does. Rows 7 to 14 are in the third layout: Leap's date, which only row 12 has, is in the file as that
        /// layout begins and not in the first form. The engine read every row in it as null.
        /// </summary>
        [Fact]
        public void the_engine_reads_every_value_dao_wrote_through_either_kind_of_recordset_as_dao_reads_it()
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = MakeT();

            var file = File.ReadAllBytes(path);
            Assert.True(IndexOf(file, Latin(TableTypeStart(Leap))) >= 0, "Row 12 is not in the layout DAO's table-type recordset was measured to write.");
            Assert.True(IndexOf(file, Latin(FirstForm(Leap))) < 0, "Row 12 is in the first form.");

            var dao = DaoReads(path, "T");
            var engine = EngineReads(path, "T");
            foreach (var (id, how, reads) in Made)
            {
                Assert.Equal(reads, AccessReferee.ExtendedDate(dao[id]));
                Assert.True(reads == engine[id], "Row " + id + " (" + how + "): DAO reads " + dao[id] + ", the engine " + (engine[id]?.ToString("o") ?? "null"));
            }
        }

        // ── Lookups ──────────────────────────────────────────────────────────

        /// <summary>
        /// For each date, the engine finds through KAsc and KDesc, and by a cursor over the table, the rows ACE's
        /// <c>WHERE K = date</c> finds by reading the table - in a copy with no index on K - in every layout. Through the
        /// index ACE's own <c>=</c> finds the first form alone: the rows its <c>INSERT</c> wrote. The engine found no
        /// row in the third layout, through an index or otherwise.
        /// </summary>
        [Fact]
        public void the_engine_finds_by_a_date_the_rows_ace_finds_by_reading_the_table()
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = MakeT();
            var scanned = CopyOf(path);
            AccessReferee.Execute(scanned, "DROP INDEX KAsc ON T", "DROP INDEX KDesc ON T");

            // Opened only to be read, so that ACE opens the file beside it (#62).
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                var t = db.GetTable("T");
                foreach (var date in Made.Select(m => m.Reads).Distinct())
                {
                    var byScan = AceIds(scanned, "SELECT Id FROM T WHERE K = '" + Literal(date) + "'");
                    Assert.Equal(Made.Where(m => m.Reads == date).Select(m => m.Id), byScan);
                    Assert.Equal(Made.Where(m => m.Reads == date && m.How == "INSERT").Select(m => m.Id),
                                 AceIds(path, "SELECT Id FROM T WHERE K = '" + Literal(date) + "'"));

                    Assert.Equal(byScan, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(date)).OrderBy(id => id));
                    Assert.Equal(byScan, IdsOf(t.CreateIndexCursor("KDesc").FindRowsByEntry(date)).OrderBy(id => id));
                    var cursor = t.CreateCursor();
                    var found = new List<int>();
                    for (var row = cursor.FindRow("K", date); row != null; row = cursor.FindNextRow("K", date)) found.Add(Convert.ToInt32(row["Id"]));
                    Assert.Equal(byScan, found.OrderBy(id => id));   // in the table's order, which is not the Ids'
                }
            }
        }

        /// <summary>
        /// P is keyed by D, 2 January 2020 03:04:05 in each layout: pt by a table-type AddNew, pi by INSERT, pu by UPDATE.
        /// C's children 1 and 2 are pt's, copied from it by <c>INSERT ... SELECT</c> - ACE refuses any other child beside it,
        /// whose four bytes after the value would be its own - child 3 is pi's, given the date by INSERT, and child 4 pu's,
        /// copied. The engine's Joiner from each parent finds the children ACE's cascade deletes with it (each in a copy of
        /// its own), and a date given finds all four. The Joiner found no child of pt.
        /// </summary>
        [Fact]
        public void the_joiner_joins_a_parent_to_the_children_ace_deletes_with_it()
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + ".accdb");
            AccessReferee.DaoCreate(path, 256);
            WithDao(path, db =>
            {
                db.Execute("CREATE TABLE P (PNm TEXT(10), D DATETIME2 CONSTRAINT PPK PRIMARY KEY)", DbFailOnError);
                db.Execute("CREATE TABLE C (Id LONG CONSTRAINT CPK PRIMARY KEY, PD DATETIME2)", DbFailOnError);
                AddNew(db, "P", DbOpenTable, "PNm", "pt", "D", Literal(D0));
                db.Execute("INSERT INTO P (PNm, D) VALUES ('pi', '" + Literal(D0) + "')", DbFailOnError);
                db.Execute("INSERT INTO P (PNm, D) VALUES ('pu', '2020-01-09 00:00:00')", DbFailOnError);
                db.Execute("UPDATE P SET D = '" + Literal(D0) + "' WHERE PNm = 'pu'", DbFailOnError);
            });
            AccessReferee.Execute(path, "ALTER TABLE C ADD CONSTRAINT FKPD FOREIGN KEY (PD) REFERENCES P (D) ON DELETE CASCADE");
            WithDao(path, db =>
            {
                foreach (var sql in new[]
                {
                    "INSERT INTO C (Id, PD) SELECT 1, D FROM P WHERE PNm = 'pt'",
                    "INSERT INTO C (Id, PD) SELECT 2, D FROM P WHERE PNm = 'pt'",
                    "INSERT INTO C (Id, PD) VALUES (3, '" + Literal(D0) + "')",
                    "INSERT INTO C (Id, PD) SELECT 4, D FROM P WHERE PNm = 'pu'",
                })
                    db.Execute(sql, DbFailOnError);
            });

            var children = new Dictionary<string, List<int>>();
            foreach (var parent in new[] { "pt", "pi", "pu" })
            {
                var copy = CopyOf(path);
                AccessReferee.Execute(copy, "DELETE FROM P WHERE PNm = '" + parent + "'");
                children[parent] = new[] { 1, 2, 3, 4 }.Except(AceIds(copy, "SELECT Id FROM C")).ToList();
            }
            Assert.Equal(new[] { 1, 2 }, children["pt"]);
            Assert.Equal(new[] { 3 }, children["pi"]);
            Assert.Equal(new[] { 4 }, children["pu"]);

            using (var db = Database.Open(path))
            {
                var (p, c) = (db.GetTable("P"), db.GetTable("C"));
                var childrenOf = new Joiner(p, new[] { "D" }, c, new[] { "PD" });
                foreach (var parent in p.Rows)
                    Assert.Equal(children[(string)parent["PNm"]], IdsOf(childrenOf.FindRows(parent)).OrderBy(id => id));
                Assert.Equal(new[] { 1, 2, 3, 4 }, IdsOf(childrenOf.FindRows(new Dictionary<string, object> { ["D"] = D0 })).OrderBy(id => id));

                var parentOf = childrenOf.CreateReverse();
                foreach (var child in c.Rows)
                {
                    int id = Convert.ToInt32(child["Id"]);
                    Assert.Equal(children.Single(pair => pair.Value.Contains(id)).Key, (string)parentOf.FindFirstRow(child)["PNm"]);
                }
            }
        }

        // ── Updates and deletes ──────────────────────────────────────────────

        /// <summary>
        /// The engine gives rows in the third layout another How, which keeps their bytes, the four after the value among
        /// them; gives row 10 another date, which it writes in the first form; deletes a row in each layout and adds one.
        /// DAO then reads every row as the engine does; walks KAsc, KDesc and the primary key over every row once, as
        /// they hold every row and no entry of a row that has gone; seeks every row through the primary key; ACE finds row
        /// 10 through KAsc by its new date; and DAO compacts the file with every index, which it walks again. Then DAO
        /// adds a row through a table-type recordset, and the engine reads it and finds it through KAsc.
        /// </summary>
        [Fact]
        public void rows_in_every_layout_the_engine_changes_are_read_walked_sought_and_compacted_by_access()
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = MakeT();
            var before = File.ReadAllBytes(path);
            var moved = D0.AddDays(5);

            using (var db = Database.Open(path))
            {
                var t = db.GetTable("T");
                var cursor = t.CreateCursor();
                foreach (int id in new[] { 7, 8, 9, 12, 13, 14 })
                {
                    Assert.NotNull(cursor.FindRow("Id", id));
                    cursor.UpdateCurrentRow(new Row { ["How"] = "kept " + id });
                }
                Assert.NotNull(cursor.FindRow("Id", 10));
                cursor.UpdateCurrentRow(new Row { ["K"] = moved });
                foreach (int id in new[] { 11, 6, 2 })
                {
                    Assert.NotNull(cursor.FindRow("Id", id));
                    cursor.DeleteCurrentRow();
                }
                t.Insert(new Row { ["Id"] = 20, ["How"] = "the engine", ["K"] = D0 });
            }

            var after = File.ReadAllBytes(path);
            Assert.True(IndexOf(after, Latin(TableTypeStart(Leap))) >= 0, "Row 12's bytes were not kept.");
            Assert.True(IndexOf(before, Latin(FirstForm(moved))) < 0 && IndexOf(after, Latin(FirstForm(moved))) >= 0, "Row 10's new date is not in the first form.");

            var expected = Made.Where(m => m.Id != 11 && m.Id != 6 && m.Id != 2)
                .ToDictionary(m => m.Id, m => (DateTime?)(m.Id == 10 ? moved : m.Reads));
            expected[20] = D0;
            var ids = expected.Keys.OrderBy(id => id).ToList();

            void AccessHolds(string file)
            {
                var dao = DaoReads(file, "T");
                Assert.Equal(ids, dao.Keys.OrderBy(id => id));
                foreach (var id in ids) Assert.Equal(expected[id], AccessReferee.ExtendedDate(dao[id]));
                Assert.Equal(EngineReads(file, "T").OrderBy(r => r.Key), expected.OrderBy(r => r.Key));
                foreach (var index in new[] { "KAsc", "KDesc", "TPK" })
                    Assert.Equal(ids, DaoWalk(file, "T", index).OrderBy(id => id));
                Assert.Equal(ids, DaoSeeks(file, "T", "TPK", ids));
                Assert.Equal(new[] { 10 }, AceIds(file, "SELECT Id FROM T WHERE K = '" + Literal(moved) + "'"));
                Assert.Equal(new[] { 1, 20 }, AceIds(file, "SELECT Id FROM T WHERE K = '" + Literal(D0) + "'"));
            }
            AccessHolds(path);
            var compacted = AccessReferee.Compact(path);
            Assert.Equal(new[] { "KAsc", "KDesc", "TPK" }, DaoIndexes(compacted, "T"));
            AccessHolds(compacted);

            WithDao(compacted, db => { AddNew(db, "T", DbOpenTable, "Id", 21, "How", "AddNew, table-type, after", "K", Literal(D1)); });
            using (var db = Database.Open(compacted))
            {
                var t = db.GetTable("T");
                Assert.Equal(new[] { 9, 21 }, IdsOf(t.CreateIndexCursor("KAsc").FindRowsByEntry(D1)).OrderBy(id => id));
                Assert.Equal(D1, (DateTime?)t.FindRowByPrimaryKey(21)["K"]);
            }
        }

        // ── Every shape of the bytes ─────────────────────────────────────────

        /// <summary>
        /// Bytes in every shape the rules found (the engine's ExtendedDateReadingTests), each patched into a row of a
        /// file DAO made, in place of a date of its own: the engine reads each as DAO does. Where DAO hands out a date,
        /// the engine reads that date, ACE's <c>=</c> finding the row by it (for a year past 31, which ACE's text reads as
        /// a year whatever the machine's order of a date); where DAO gives Empty, or fails to read the value, the engine
        /// reads null; and so it does where DAO shows what is no date - hours past 24, a fraction of more than seven
        /// places, or 30 December 1899 for a day after 31 December 9999, which ACE's <c>=</c> does not find as that date.
        /// </summary>
        [Fact]
        public void the_engine_reads_every_shape_of_the_bytes_as_dao_reads_it()
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var cases = new[]
            {
                "0000000000000737425:0000000110451234567:7\0",
                "0000000000000737425:0000000000000011045:0\0",
                "000000000737425:0000000110451234567:7\0\0\0\0\0",
                "000000000737425:0000000110451234567:7\0\0\0®\u0002",
                "000000000737425:0000000110451234567:7\0\0\0ta",
                "000000000737425:0000000110451234567:7\0ÿÿÿÿ",
                "000000000737425:0000000110451234567:7\0:5",
                "737425:110451234567\0:7",
                "737425:110451234567:7",
                "0000000000000000000737425:0110451234567:7",
                "737425:000000000000000000110451234567:7",
                "737425:110451234567:junk:7",
                "73742:110451234567:7:5",
                "0000000000000737425 0000000110451234567:7\0",
                "0000000000000737425:0000000110451234567 7\0",
                ":0000000110451234567:7\0",
                "0000000000000737425::7\0",
                "             737425:0000000110451234567:7\0",
                "+000000000000737425:0000000110451234567:7\0",
                "\t000000000000737425:0000000110451234567:7\0",
                "\r000000000000737425:0000000110451234567:7\0",
                "-000000000000737425:0000000110451234567:7\0",
                "x000000000000737425:0000000110451234567:7\0",
                " 000000000000737425:0000000110451234567:7\0",
                "\u001C000000000000737425:0000000110451234567:7\0",
                "000000000000073742X:0000000110451234567:7\0",
                "                   :0000000110451234567:7\0",
                "+ 00000000000737425:0000000110451234567:7\0",
                "  -0000000000737425:0000000110451234567:7\0",
                "0000000000000737425:00000001104512345X7:7\0",
                "0000000000000737425:                   :7\0",
                "000000000737425:0000000110451234567: 7",
                "000000000737425:0000000110451234567:+7",
                "000000000737425:0000000110451234567:7x",
                "000000000737425:0000000110451234567:-7",
                "000000000737425:0000000110451234567:x",
                "0000000000000737425:0000000110451234567:7ÿ",
                "4295704721:110451234567:7",
                "9223372036855513233:110451234567:7",
                "2147483648:0:7",
                "18446744073709551616:0:7",
                "737425:110451234567:4294967303",
                "737425:110451234567:18446744073709551623",
                "000000000737425:0000000000000110451:1",
                "000000000737425:0000000000011045123:3",
                "000000000737425:0000000011045123456:6",
                "000000000737425:0000001104500000010:8",
                "000000000737425:0000011045000000001:9",
                "000000000737425:0000000110451234567:10",
                "0000000000000737425:0000000110451234567:75",
                "0000000000003652058:0000000863999999999:7\0",
                "0000000000003652059:0000000000000000000:7\0",
                "0000000000000737425:0000000864000000000:7\0",
                "0000000000000737425:0000000000000086400:0\0",
                "0000000000000737425:0000000110451234567:0\0",
                "000000000737425:0000000110451234567:3",
                "000000000737425:0000001104512345678:8",
                "0:0:7",
                "",
            };

            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + ".accdb");
            AccessReferee.DaoCreate(path, 256);
            // Each row a date of its own, in the first form, found in the file and replaced by its case.
            Func<int, DateTime> placeholder = id => new DateTime(1990, 1, 1).AddDays(id).AddTicks(id * 1234567L);
            WithDao(path, db =>
            {
                db.Execute("CREATE TABLE B (Id LONG CONSTRAINT BPK PRIMARY KEY, K DATETIME2)", DbFailOnError);
                for (int id = 1; id <= cases.Length; id++)
                    db.Execute("INSERT INTO B (Id, K) VALUES (" + id + ", '" + Literal(placeholder(id)) + "')", DbFailOnError);
            });
            var file = File.ReadAllBytes(path);
            for (int id = 1; id <= cases.Length; id++)
            {
                var stored = Latin(FirstForm(placeholder(id)));
                int at = IndexOf(file, stored);
                Assert.True(at >= 0 && IndexOf(file, stored, at + 1) < 0, "Row " + id + "'s date is not in the file once.");
                var bytes = new byte[42];
                Latin(cases[id - 1]).CopyTo(bytes, 0);
                bytes.CopyTo(file, at);
            }
            File.WriteAllBytes(path, file);

            // What DAO reads of each row, through a query of its own: the text it hands out, or null where it reads no value.
            // That is Empty for most bytes Access reads no value from; for a count of 10 or more - where ACE's = says
            // "Attempted to divide by zero" - reading the value fails in this process (RPC_E_SERVERFAULT), and DAO reads
            // the next row as ever.
            var dao = new Dictionary<int, string>();
            WithDao(path, db =>
            {
                for (int id = 1; id <= cases.Length; id++)
                {
                    try
                    {
                        dynamic rs = db.OpenRecordset("SELECT K FROM B WHERE Id = " + id, DbOpenSnapshot);
                        try
                        {
                            object value = rs.Fields["K"].Value;
                            dao[id] = value as string;
                        }
                        finally { rs.Close(); }
                    }
                    catch (System.Runtime.InteropServices.COMException) { dao[id] = null; }
                }
            });
            var engine = EngineReads(path, "B");
            for (int id = 1; id <= cases.Length; id++)
            {
                string text = dao[id];
                DateTime? expected = null;
                if (text != null && DateTime.TryParseExact(text, "MM/dd/yyyy HH:mm:ss.fffffff", Invariant, DateTimeStyles.None, out var date)
                    && (date.Year <= 31 || AceCount(path, "SELECT COUNT(*) FROM B WHERE Id = " + id + " AND K = '" + Literal(date) + "'") == 1))
                    expected = date;
                Assert.True(expected == engine[id],
                    "Bytes [" + cases[id - 1].Replace("\0", "\\0") + "]: DAO reads " + (text ?? "no value") + ", the engine " + (engine[id]?.ToString("o") ?? "null"));
            }
        }

        // ── A file an earlier version of the engine wrote ────────────────────

        /// <summary>
        /// The engine's Assets/engine-c89bfe4-table-type-keys.accdb: the engine at c89bfe4, which read the third layout as
        /// no value, added W's index ByK over rows 2 and 3 in that layout and rewrote T's rows 4, 7 and 9 in it, keying each
        /// under the key of no value. The engine deletes W's rows 2 to 4 and T's rows 7 and 9, and gives T's row 4 another
        /// date: DAO then walks every index of both tables over every row once - an entry left under the key of no value
        /// points at a row that has gone - and so does it after compacting the file with every index, and ACE finds row 4
        /// through KAsc by its new date.
        /// </summary>
        [Fact]
        public void access_walks_every_index_once_the_engine_has_changed_rows_an_earlier_version_keyed_as_no_value()
        {
            AccessReferee.SkipUnlessAceHas(AccessReferee.DateTimeExtended);
            var path = EngineAsset("engine-c89bfe4-table-type-keys.accdb");
            var moved = D0.AddDays(2);
            using (var db = Database.Open(path))
            {
                var (w, t) = (db.GetTable("W"), db.GetTable("T"));
                var cursor = w.CreateCursor();
                foreach (int id in new[] { 2, 3, 4 })
                {
                    Assert.NotNull(cursor.FindRow("Id", id));
                    cursor.DeleteCurrentRow();
                }
                cursor = t.CreateCursor();
                Assert.NotNull(cursor.FindRow("Id", 4));
                cursor.UpdateCurrentRow(new Row { ["K"] = moved });
                foreach (int id in new[] { 7, 9 })
                {
                    Assert.NotNull(cursor.FindRow("Id", id));
                    cursor.DeleteCurrentRow();
                }
            }

            var tIds = Enumerable.Range(1, 14).Where(id => id != 7 && id != 9).ToList();
            void AccessHolds(string file)
            {
                foreach (var index in new[] { "KAsc", "KDesc", "KHow", "TPK" })
                    Assert.Equal(tIds, DaoWalk(file, "T", index).OrderBy(id => id));
                foreach (var index in new[] { "ByK", "WPK" })
                    Assert.Equal(new[] { 1 }, DaoWalk(file, "W", index));
                Assert.Equal(new[] { 4 }, AceIds(file, "SELECT Id FROM T WHERE K = '" + Literal(moved) + "'"));
            }
            AccessHolds(path);
            var compacted = AccessReferee.Compact(path);
            Assert.Equal(new[] { "KAsc", "KDesc", "KHow", "TPK" }, DaoIndexes(compacted, "T"));
            Assert.Equal(new[] { "ByK", "WPK" }, DaoIndexes(compacted, "W"));
            AccessHolds(compacted);
        }
    }
}
