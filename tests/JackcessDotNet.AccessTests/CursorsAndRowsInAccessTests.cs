using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// The engine's cursors and row operations, with Access as the referee (#34, #43, #29,
    /// #35). A row is updated or deleted by where it was read, whatever key its table has; a cursor updates and
    /// deletes the row it is on; a batch goes in whole or not at all. After each, ACE reads the table, answers
    /// through every index, writes to it, and DAO walks the indexes and compacts the file. An index cursor goes
    /// through an index's rows in the order DAO walks them, and a range of it holds the rows ACE's WHERE gives.
    /// <para>
    /// ACE is asked through each index for every row in the full sweep (<see cref="Sweeps.Full"/>), a connection a
    /// question, which is most of the time these tests take. Without it, for a sample of the rows that has every kind
    /// the tests make - rows the engine updated by where they were read and through a cursor, rows it added, rows ACE
    /// added and changed, rows no one touched (<see cref="AskedThroughEachIndexT"/>, <see cref="AskedThroughEachIndexU"/>);
    /// every row is still read by a scan, counted, and walked through every index by DAO and by the engine's cursor.
    /// </para>
    /// </summary>
    public class CursorsAndRowsInAccessTests : IDisposable
    {
        private TempDb _tmp;

        public CursorsAndRowsInAccessTests() { _tmp = new TempDb(); }
        public void Dispose() { _tmp.Dispose(); }

        private static string Sql(object value)
        {
            if (value == null) return "NULL";
            var text = value as string;
            if (text != null) return "'" + text.Replace("'", "''") + "'";
            if (value is DateTime) return "#" + ((DateTime)value).ToString("M/d/yyyy H:mm:ss", CultureInfo.InvariantCulture) + "#";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Insert(string table, Values row)
        {
            return "INSERT INTO [" + table + "] (" + string.Join(", ", row.Keys.Select(k => "[" + k + "]")) + ") VALUES ("
                 + string.Join(", ", row.Values.Select(Sql)) + ")";
        }

        private static Values Copy(Values row)
        {
            var copy = new Values();
            foreach (var pair in row) copy[pair.Key] = pair.Value;
            return copy;
        }

        // ── Rows by where they were read, in a table with any key ────────────────

        private static Values T(int b) { return V("A", b / 10, "B", b, "Name", "name " + b.ToString("0000"), "Notes", "notes " + b); }

        /// <summary>A table T of 300 rows with indexes ByName and ByB, and a key of (A, B) or none, made by Access or by the engine.</summary>
        private string MakeT(bool accessMade, bool keyed, Dictionary<int, Values> rows)
        {
            var path = _tmp.NewMdbPath();
            for (var b = 1; b <= 300; b++) rows[b] = T((b * 77) % 300 + 1);
            var inOrder = rows.Values.ToList();
            rows.Clear();
            foreach (var row in inOrder) rows[(int)row["B"]] = row;

            if (accessMade)
            {
                DaoCreate(path, 64);
                Execute(path, "CREATE TABLE T (A LONG, B LONG, Name TEXT(50), Notes MEMO" + (keyed ? ", CONSTRAINT pk PRIMARY KEY (A, B)" : "") + ")",
                              "CREATE INDEX ByName ON T (Name)", "CREATE INDEX ByB ON T (B)");
                Execute(path, inOrder.Select(row => Insert("T", row)).ToArray());
            }
            else
            {
                using (var db = Database.Create(path, FileFormat.Access2000))
                {
                    var table = db.CreateTable("T", new[]
                    {
                        new ColumnDefinition("A", DataType.Int32), new ColumnDefinition("B", DataType.Int32),
                        new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 }, new ColumnDefinition("Notes", DataType.Memo),
                    }, keyed ? new[] { "A", "B" } : new string[0], new[] { new IndexDefinition("ByName", "Name"), new IndexDefinition("ByB", "B") });
                    table.InsertRange(inOrder.Select(EngineRow));
                }
            }
            return path;
        }

        /// <summary>
        /// The rows of T that ACE is asked for through each index: all of them in the full sweep, and in the sample the
        /// tenth of every ten (untouched; 100, which the scan changed; ACE's 5000; every fifth of the batch), the third
        /// (updated by where they were read; 13, which ACE updated) and the fifth (updated through the cursor) - three
        /// rows in ten. The rows deleted are gone from <c>rows</c>, and so from every sample.
        /// </summary>
        private static bool AskedThroughEachIndexT(Values row)
        {
            var b = (int)row["B"];
            return Sweeps.Full || b % 5 == 0 || b % 10 == 3;
        }

        /// <summary>ACE and DAO read T as <paramref name="rows"/>: by scan, through each index, and walking each index.</summary>
        private static void AssertAccessReadsT(string path, Dictionary<int, Values> rows, bool keyed, string when)
        {
            var expected = Written(rows.Values);
            AssertSame(expected, Written(AceTable(path, "T")), "T as ACE reads it " + when);
            AssertSame(expected, Written(EngineTable(path, "T")), "T as the engine reads it " + when);
            Assert.Equal(rows.Count, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM T")));

            // Through each index: ACE answers a WHERE on an indexed column from the index.
            foreach (var row in rows.Values.Where(AskedThroughEachIndexT))
            {
                AssertSame(Written(new[] { row }), Written(AceRows(path, "SELECT * FROM T WHERE B = " + row["B"])), "T WHERE B = " + row["B"] + " " + when);
                AssertSame(Written(new[] { row }), Written(AceRows(path, "SELECT * FROM T WHERE Name = " + Sql(row["Name"]))), "T WHERE Name = " + row["Name"] + " " + when);
                if (keyed)
                    AssertSame(Written(new[] { row }), Written(AceRows(path, "SELECT * FROM T WHERE A = " + row["A"] + " AND B = " + row["B"])), "T by its key " + when);
            }
            Assert.Equal(0, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM T WHERE B = 100000")));

            // DAO walks each index's own pages: every row once, in the index's order - and the engine's cursor over
            // the index goes through the same rows in the same order. (The key's index has the name its maker gave it.)
            List<string> indexes;
            using (var db = Database.Open(path))
                indexes = db.GetTable("T").Indexes.Select(ix => ix.Name).ToList();
            Assert.Equal(keyed ? 3 : 2, indexes.Count);
            foreach (var index in indexes)
            {
                var walked = DaoRows(path, "T", index);
                AssertSame(expected, Written(walked), "T as DAO walks " + index + " " + when);
                var inB = walked.Select(row => Convert.ToInt32(row["B"])).ToList();
                if (index == "ByB") Assert.Equal(inB.OrderBy(b => b).ToList(), inB);

                using (var db = Database.Open(path))
                    Assert.Equal(inB, db.GetTable("T").CreateIndexCursor(index).Select(row => (int)row["B"]).ToList());
            }
        }

        public static IEnumerable<object[]> Tables()
        {
            foreach (var accessMade in new[] { true, false })
                foreach (var keyed in new[] { true, false })
                    yield return new object[] { accessMade, keyed };
        }

        [Theory]
        [MemberData(nameof(Tables))]
        public void rows_updated_deleted_and_added_in_a_table_of_any_key_are_the_rows_access_has(bool accessMade, bool keyed)
        {
            var rows = new Dictionary<int, Values>();
            var path = MakeT(accessMade, keyed, rows);
            AssertAccessReadsT(path, rows, keyed, "as it was made");

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");

                // By where they were read: a rename, a long value, and - in the table with a key - a change of key.
                foreach (var row in table.Rows.ToList())
                {
                    var b = (int)row["B"];
                    Assert.NotNull(row.Id);
                    if (b % 10 == 3)
                    {
                        row["Name"] = "renamed " + b.ToString("0000");
                        if (b % 20 == 3) row["Notes"] = new string((char)('a' + b % 26), 3000 + b);
                        if (keyed && b % 40 == 3) row["A"] = 1000 + b;
                        table.Update(row);
                        foreach (var column in new[] { "A", "Name", "Notes" }) rows[b][column] = row[column];
                    }
                    else if (b % 10 == 7)
                    {
                        table.Delete(row);
                        Assert.Null(row.Id);
                        rows.Remove(b);
                    }
                }

                // Through a cursor over an index: the row it is on is updated, or deleted.
                var cursor = table.CreateIndexCursor("ByName");
                while (cursor.MoveNext())
                {
                    var b = (int)cursor.Current["B"];
                    if (b % 10 == 5)
                    {
                        cursor.UpdateCurrentRow(new Row { ["Notes"] = null, ["Name"] = "zz " + b.ToString("0000") });
                        rows[b]["Notes"] = null;
                        rows[b]["Name"] = "zz " + b.ToString("0000");
                    }
                    else if (b % 10 == 9)
                    {
                        cursor.DeleteCurrentRow();
                        rows.Remove(b);
                    }
                }

                // And through one over the table: found by a value, then changed.
                var scan = table.CreateCursor();
                Assert.NotNull(scan.FindRow("Name", "name 0100"));
                scan.UpdateCurrentRow(new Row { ["Name"] = "found and changed" });
                rows[100]["Name"] = "found and changed";
                Assert.NotNull(scan.FindRow(new Row { ["B"] = 200, ["Name"] = "name 0200" }));
                scan.DeleteCurrentRow();
                rows.Remove(200);

                // A batch that is refused leaves nothing behind; one that is not goes in whole.
                var batch = Enumerable.Range(1001, 50).Select(b => T(b)).ToList();
                var refused = batch.Select(Copy).ToList();
                refused[40]["Name"] = new string('x', 51);                    // longer than the column
                var failure = Assert.Throws<BatchInsertException>(() => table.InsertRange(refused.Select(EngineRow)));
                Assert.Equal(40, failure.RowIndex);
                Assert.IsType<ColumnValueTooLongException>(failure.InnerException);
                if (keyed)
                {
                    refused[40]["Name"] = "fits";
                    refused[49]["A"] = 0; refused[49]["B"] = 1;               // the key of a row that is there
                    failure = Assert.Throws<BatchInsertException>(() => table.InsertRange(refused.Select(EngineRow)));
                    Assert.Equal(49, failure.RowIndex);
                    Assert.IsType<ConstraintViolationException>(failure.InnerException);
                }
                table.InsertRange(batch.Select(EngineRow));
                foreach (var row in batch) rows[(int)row["B"]] = row;
                Assert.Equal(rows.Count, table.RowCount);
            }
            AssertAccessReadsT(path, rows, keyed, "after the engine changed it");

            // Access goes on writing to the table: an insert, an update and a delete through its indexes.
            var added = T(5000);
            Execute(path, Insert("T", added), "UPDATE T SET Name = 'ace changed' WHERE B = 13", "DELETE FROM T WHERE B = 23");
            rows[5000] = added;
            rows[13]["Name"] = "ace changed";
            rows.Remove(23);
            AssertAccessReadsT(path, rows, keyed, "after ACE changed it in turn");

            // And DAO compacts it, building every index again from the rows.
            var compacted = Compact(path);
            try { AssertAccessReadsT(compacted, rows, keyed, "after DAO compacted the file"); }
            finally { File.Delete(compacted); }
        }

        // ── A cursor keeps the relationships Access made ─────────────────────────

        [Fact]
        public void a_cursor_s_update_and_delete_carry_out_the_cascades_of_a_relationship_access_made()
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            Execute(path, "CREATE TABLE P (A LONG, B LONG, Name TEXT(20), CONSTRAINT pk PRIMARY KEY (A, B))",
                          "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, A LONG, B LONG, Remark TEXT(20), " +
                          "CONSTRAINT PC FOREIGN KEY (A, B) REFERENCES P (A, B) ON UPDATE CASCADE ON DELETE CASCADE)");
            var parents = new Dictionary<int, Values>();
            var children = new Dictionary<int, Values>();
            for (var b = 1; b <= 20; b++) parents[b] = V("A", 1, "B", b, "Name", "p" + b);
            for (var id = 1; id <= 60; id++) children[id] = V("Id", id, "A", 1, "B", (id - 1) % 20 + 1, "Remark", "c" + id);
            Execute(path, parents.Values.Select(row => Insert("P", row)).Concat(children.Values.Select(row => Insert("C", row))).ToArray());

            using (var db = Database.Open(path))
            {
                Assert.True(db.EnforceForeignKeys);
                var cursor = db.GetTable("P").CreateIndexCursor();

                // The key of a parent changes: its children follow it.
                Assert.NotNull(cursor.FindRowByKey(1, 2));
                cursor.UpdateCurrentRow(new Row { ["B"] = 200 });
                Assert.Equal(200, cursor.Current["B"]);
                parents[2]["B"] = 200;
                foreach (var child in children.Values.Where(c => (int)c["B"] == 2).ToList()) child["B"] = 200;

                // A parent goes: its children go with it.
                Assert.NotNull(cursor.FindRowByKey(1, 3));
                cursor.DeleteCurrentRow();
                parents.Remove(3);
                foreach (var id in children.Where(c => (int)c.Value["B"] == 3).Select(c => c.Key).ToList()) children.Remove(id);

                // What Access refuses is refused, and changes nothing: another parent's key, and a child with no parent.
                Assert.NotNull(cursor.FindRowByKey(1, 4));
                Assert.Throws<ConstraintViolationException>(() => cursor.UpdateCurrentRow(new Row { ["B"] = 5 }));
                var child7 = db.GetTable("C").CreateIndexCursor();
                Assert.NotNull(child7.FindRowByKey(7));
                Assert.Throws<ConstraintViolationException>(() => child7.UpdateCurrentRow(new Row { ["B"] = 999 }));

                // A child is moved to another parent, and one is deleted, through the cursor.
                child7.UpdateCurrentRow(new Row { ["B"] = 10 });
                children[7]["B"] = 10;
                Assert.NotNull(child7.FindRowByKey(8));
                child7.DeleteCurrentRow();
                children.Remove(8);
            }

            foreach (var read in new[] { path, Compact(path) })
            {
                var when = read == path ? "after the engine's cursors" : "after DAO compacted the file";
                AssertSame(Written(parents.Values), Written(AceTable(read, "P")), "P as ACE reads it " + when);
                AssertSame(Written(children.Values), Written(AceTable(read, "C")), "C as ACE reads it " + when);
                AssertSame(Written(children.Values), Written(DaoRows(read, "C", "PC")), "C as DAO walks the relationship's index " + when);
                Assert.Equal(0, Convert.ToInt32(Scalar(read,
                    "SELECT COUNT(*) FROM C LEFT JOIN P ON (C.A = P.A AND C.B = P.B) WHERE P.A IS NULL")));
                foreach (var parent in parents.Values)
                    Assert.Equal(children.Values.Count(c => (int)c["B"] == (int)parent["B"]),
                                 Convert.ToInt32(Scalar(read, "SELECT COUNT(*) FROM C WHERE A = 1 AND B = " + parent["B"])));

                // Access still keeps the relationship: a child of the re-keyed parent goes in, one of the old key does not.
                Execute(read, "INSERT INTO C (Id, A, B) VALUES (1000, 1, 200)");
                Assert.Throws<OleDbException>(() => Execute(read, "INSERT INTO C (Id, A, B) VALUES (1001, 1, 2)"));
                Execute(read, "DELETE FROM C WHERE Id = 1000");
                if (read != path) File.Delete(read);
            }
        }

        // ── An index cursor's order and ranges are Access's ──────────────────────

        [Fact]
        public void an_index_cursor_goes_through_an_index_as_dao_walks_it_and_a_range_holds_what_ace_s_where_gives()
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            Execute(path, "CREATE TABLE X (Id LONG CONSTRAINT pk PRIMARY KEY, T TEXT(30), N LONG, D DATETIME, M CURRENCY)",
                          "CREATE INDEX ByT ON X (T)", "CREATE INDEX ByN ON X (N)", "CREATE INDEX ByNDown ON X (N DESC)",
                          "CREATE INDEX ByD ON X (D)", "CREATE INDEX ByTN ON X (T, N)", "CREATE INDEX ByM ON X (M)");
            var words = new[] { "apple", "Apple", "banana", "Banana split", "cherry", "date", "élan", "Zebra", "zebra crossing", "b", "d" };
            var random = new Random(20261119);
            var statements = new List<string>();
            for (var id = 1; id <= 400; id++)
            {
                var row = V("Id", id,
                            "T", id % 9 == 0 ? null : words[random.Next(words.Length)] + (id % 3 == 0 ? " " + random.Next(50) : ""),
                            "N", id % 11 == 0 ? (object)null : random.Next(-100, 100),
                            "D", id % 13 == 0 ? (object)null : new DateTime(2020, 1, 1).AddSeconds(random.Next(0, 100000000)),
                            "M", random.Next(-50000, 50000) / 100m);
                statements.Add(Insert("X", row));
            }
            Execute(path, statements.ToArray());

            // Opened only to be read, so that ACE and DAO open the file beside it (#62).
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                var table = db.GetTable("X");
                Func<IEnumerable<Row>, List<int>> ids = rows => rows.Select(row => (int)row["Id"]).ToList();
                Func<string, List<int>> ace = sql => AceRows(path, sql).Select(row => Convert.ToInt32(row["Id"])).ToList();

                // Each index, forwards and backwards.
                foreach (var index in new[] { "pk", "ByT", "ByN", "ByNDown", "ByD", "ByTN", "ByM" })
                {
                    var walked = DaoRows(path, "X", index).Select(row => Convert.ToInt32(row["Id"])).ToList();
                    Assert.Equal(400, walked.Count);
                    Assert.Equal(walked, ids(table.CreateIndexCursor(index)));
                    walked.Reverse();
                    Assert.Equal(walked, ids(table.CreateIndexCursor(index).EnumerateBackward()));
                }

                // Ranges: the rows ACE's WHERE gives, in the index's order.
                Func<object, object[]> e = value => new[] { value };
                Assert.Equal(ace("SELECT Id FROM X WHERE N BETWEEN -20 AND 35 ORDER BY N, Id"),
                             ids(table.CreateIndexCursor("ByN", new IndexRange { StartKey = e(-20), EndKey = e(35) })));
                Assert.Equal(ace("SELECT Id FROM X WHERE N > -20 AND N < 35 ORDER BY N, Id"),
                             ids(table.CreateIndexCursor("ByN", new IndexRange { StartKey = e(-20), IsStartInclusive = false, EndKey = e(35), IsEndInclusive = false })));
                // (A descending index holds its nulls last, and a range with no end runs to them.)
                Assert.Equal(ace("SELECT Id FROM X WHERE N <= 0 ORDER BY N DESC, Id").Concat(ace("SELECT Id FROM X WHERE N IS NULL ORDER BY Id")).ToList(),
                             ids(table.CreateIndexCursor("ByNDown", new IndexRange { StartKey = e(0) })));
                Assert.Equal(ace("SELECT Id FROM X WHERE N <= 0 AND N > -40 ORDER BY N DESC, Id"),
                             ids(table.CreateIndexCursor("ByNDown", new IndexRange { StartKey = e(0), EndKey = e(-40), IsEndInclusive = false })));
                Assert.Equal(ace("SELECT Id FROM X WHERE N IS NULL ORDER BY Id"),
                             ids(table.CreateIndexCursor("ByN", new IndexRange { EndKey = e(null) })));
                Assert.Equal(ace("SELECT Id FROM X WHERE T >= 'b' AND T < 'd' ORDER BY T, Id").OrderBy(i => i).ToList(),
                             ids(table.CreateIndexCursor("ByT", new IndexRange { StartKey = e("b"), EndKey = e("d"), IsEndInclusive = false })).OrderBy(i => i).ToList());
                Assert.Equal(ace("SELECT Id FROM X WHERE T = 'Apple' AND N >= 0 ORDER BY N, Id"),
                             ids(table.CreateIndexCursor("ByTN", new IndexRange { StartKey = new object[] { "apple", 0 }, EndKey = e("APPLE") })));
                Assert.Equal(ace("SELECT Id FROM X WHERE D >= #1/1/2021# AND D < #1/1/2022# ORDER BY D, Id"),
                             ids(table.CreateIndexCursor("ByD", new IndexRange { StartKey = e(new DateTime(2021, 1, 1)), EndKey = e(new DateTime(2022, 1, 1)), IsEndInclusive = false })));
                Assert.Equal(ace("SELECT Id FROM X WHERE M BETWEEN -10.5 AND 250.25 ORDER BY M, Id"),
                             ids(table.CreateIndexCursor("ByM", new IndexRange { StartKey = e(-10.5m), EndKey = e(250.25m) })));

                // The closest row: the least value at or after the one asked for; the rows of one entry: ACE's WHERE.
                foreach (var n in new[] { -1000, -57, 0, 13, 99, 1000 })
                {
                    var least = Scalar(path, "SELECT MIN(N) FROM X WHERE N >= " + n);
                    var closest = table.CreateIndexCursor("ByN").FindClosestRowByKey(n);
                    Assert.Equal(least is DBNull ? null : least, closest == null ? null : closest["N"]);
                    Assert.Equal(ace("SELECT Id FROM X WHERE N = " + n + " ORDER BY Id"), ids(table.CreateIndexCursor("ByN").FindRowsByKey(n)).OrderBy(i => i).ToList());
                }
                // ACE's = on text is the index's, without regard to case, and so is the engine's.
                Assert.Equal(ace("SELECT Id FROM X WHERE T = 'Apple' ORDER BY Id"), ids(table.CreateIndexCursor("ByT").FindRowsByKey("Apple")).OrderBy(i => i).ToList());
            }
        }

        // ── A row keeps its place ────────────────────────────────────────────────

        // Not compressible, so a row is as long as it looks.
        private static string Wide(int id, int length) { return id.ToString("D3") + new string('\u03A9', length - 3); }

        /// <summary>How many of <paramref name="table"/>'s slots hold a pointer to where their row's values went.</summary>
        private static int OverflowRows(string path, string table)
        {
            int definition;
            using (var db = Database.Open(path))
                definition = (int)db.GetTable("MSysObjects").Rows.First(r => (string)r["Name"] == table)["Id"] & 0x00FFFFFF;
            var file = File.ReadAllBytes(path);
            var count = 0;
            for (var at = 0; at + 4096 <= file.Length; at += 4096)
            {
                if (file[at] != 1 || BitConverter.ToInt32(file, at + 4) != definition) continue;
                for (var row = 0; row < BitConverter.ToInt16(file, at + 12); row++)
                    if ((BitConverter.ToUInt16(file, at + 14 + row * 2) & 0xC000) == 0x4000) count++;
            }
            return count;
        }

        /// <summary>
        /// The rows of U that ACE is asked for through each index: all of them in the full sweep, and in the sample the
        /// rows the tests move and change (2 to 8, and 20 to 22) and every fifth of the others.
        /// </summary>
        private static bool AskedThroughEachIndexU(Values row)
        {
            var id = (int)row["Id"];
            return Sweeps.Full || id <= 8 || (id >= 20 && id <= 22) || id % 5 == 0;
        }

        private static void AssertAccessReadsU(string path, Dictionary<int, Values> rows, string when)
        {
            var expected = Written(rows.Values);
            AssertSame(expected, Written(AceTable(path, "U")), "U as ACE reads it " + when);
            AssertSame(expected, Written(EngineTable(path, "U")), "U as the engine reads it " + when);
            Assert.Equal(rows.Count, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM U")));
            foreach (var row in rows.Values.Where(AskedThroughEachIndexU))
            {
                var one = Written(new[] { row });
                AssertSame(one, Written(AceRows(path, "SELECT * FROM U WHERE Id = " + row["Id"])), "U by its key " + when);
                AssertSame(one, Written(AceRows(path, "SELECT * FROM U WHERE N = " + row["N"])), "U through ByN " + when);
                AssertSame(one, Written(AceRows(path, "SELECT * FROM U WHERE A = " + Sql(row["A"]))), "U through ByA " + when);
                AssertSame(one, Written(AceRows(path, "SELECT * FROM U WHERE D = " + Sql(row["D"]))), "U through ByD " + when);
                // (Through the index of two columns only where its key is whole: a key Access cuts - a Long and 255
                // characters of this text - it does not find by both values, for a row it wrote itself either.)
                if (((string)row["A"]).Length <= 100)
                    AssertSame(one, Written(AceRows(path, "SELECT * FROM U WHERE N = " + row["N"] + " AND A = " + Sql(row["A"]))), "U through ByNA " + when);
            }
            List<string> indexes;
            using (var db = Database.Open(path))
                indexes = db.GetTable("U").Indexes.Select(ix => ix.Name).ToList();
            Assert.Equal(5, indexes.Count);
            foreach (var index in indexes)
            {
                var walked = DaoRows(path, "U", index);
                AssertSame(expected, Written(walked), "U as DAO walks " + index + " " + when);
                using (var db = Database.Open(path))
                    Assert.Equal(walked.Select(row => Convert.ToInt32(row["Id"])).ToList(),
                                 db.GetTable("U").CreateIndexCursor(index).Select(row => (int)row["Id"]).ToList());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void rows_the_engine_updates_where_they_are_and_moves_off_their_page_are_rows_access_reads_finds_updates_and_deletes(bool accessMade)
        {
            var path = _tmp.NewMdbPath();
            var rows = new Dictionary<int, Values>();
            for (var id = 1; id <= 60; id++)
                rows[id] = V("Id", id, "A", Wide(id, 60), "B", null, "C", null, "N", id * 10, "D", new DateTime(2024, 1, 1).AddMinutes(id * 61), "M", "memo " + id);
            if (accessMade)
            {
                DaoCreate(path, 64);
                Execute(path, "CREATE TABLE U (Id LONG CONSTRAINT pk PRIMARY KEY, A TEXT(255), B TEXT(255), C TEXT(255), N LONG, D DATETIME, M MEMO)",
                              "CREATE INDEX ByA ON U (A)", "CREATE UNIQUE INDEX ByN ON U (N)", "CREATE INDEX ByD ON U (D)", "CREATE INDEX ByNA ON U (N, A)");
                foreach (var row in rows.Values) AceInsert(path, "U", row);
            }
            else
            {
                using (var db = Database.Create(path, FileFormat.Access2000))
                {
                    var made = db.CreateTable("U", new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
                        new ColumnDefinition("B", DataType.Text) { MaxLength = 255 }, new ColumnDefinition("C", DataType.Text) { MaxLength = 255 },
                        new ColumnDefinition("N", DataType.Int32), new ColumnDefinition("D", DataType.DateTime), new ColumnDefinition("M", DataType.Memo),
                    }, new[] { "Id" }, new[]
                    {
                        new IndexDefinition("ByA", "A"), new IndexDefinition("ByN", "N") { IsUnique = true },
                        new IndexDefinition("ByD", "D"), new IndexDefinition("ByNA", "N", "A"),
                    });
                    made.InsertRange(rows.Values.Select(EngineRow));
                }
            }
            AssertAccessReadsU(path, rows, "as it was made");

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("U");
                var ids = table.Rows.ToDictionary(r => (int)r["Id"], r => r.Id.Value);
                Action<int, Values> update = (id, changes) =>
                {
                    var row = table.FindRowByPrimaryKey(id);
                    foreach (var pair in changes) row[pair.Key] = pair.Value;
                    table.Update(row);
                    Assert.Equal(ids[id], row.Id.Value);
                    foreach (var pair in changes) rows[id][pair.Key] = pair.Value;
                };

                // Smaller, larger within the page, a key changed, a long value given: each where the row is.
                update(2, V("A", Wide(2, 10)));
                update(2, V("A", Wide(2, 40), "N", 2000));
                update(3, V("N", -3, "D", new DateTime(1999, 12, 31, 23, 59, 58)));
                update(4, V("M", new string('m', 9000), "A", Wide(4, 80)));
                // Larger than the page's room: rows of the first page go to others, one of them twice over.
                foreach (var id in new[] { 5, 6, 7, 8 })
                    update(id, V("A", Wide(id, 255), "B", Wide(id + 100, 255), "C", Wide(id + 200, 255)));
                update(6, V("C", Wide(600, 200), "N", 6000));         // changed where its values now are
                update(7, V("A", Wide(7, 20), "B", null, "C", null)); // small again: home
                update(8, V("M", new string('k', 5000)));
            }
            Assert.True(OverflowRows(path, "U") >= 3, "no row of U was moved off its page");
            AssertAccessReadsU(path, rows, "after the engine's updates");

            // Access updates and deletes those same rows, through the indexes.
            AceUpdate(path, "U", "Id", 5, V("C", Wide(555, 100), "N", 5005));
            AceUpdate(path, "U", "Id", 6, V("A", Wide(6, 30), "B", null, "C", null));
            AceUpdate(path, "U", "Id", 2, V("B", Wide(22, 255)));
            rows[5]["C"] = Wide(555, 100); rows[5]["N"] = 5005;
            rows[6]["A"] = Wide(6, 30); rows[6]["B"] = null; rows[6]["C"] = null;
            rows[2]["B"] = Wide(22, 255);
            Execute(path, "DELETE FROM U WHERE N = 80", "DELETE FROM U WHERE Id = 4");
            rows.Remove(8); rows.Remove(4);
            AssertAccessReadsU(path, rows, "after ACE updated and deleted the rows the engine had moved");

            // And the other way: the engine updates rows Access has moved off their page - further, and home.
            foreach (var id in new[] { 20, 21, 22 })
            {
                AceUpdate(path, "U", "Id", id, V("A", Wide(id, 255), "B", Wide(id + 300, 255), "C", Wide(id + 400, 255)));
                rows[id]["A"] = Wide(id, 255); rows[id]["B"] = Wide(id + 300, 255); rows[id]["C"] = Wide(id + 400, 255);
            }
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("U");
                var cursor = table.CreateIndexCursor("ByN");
                Assert.NotNull(cursor.FindRowByKey(200));
                var was = cursor.Current.Id;
                cursor.UpdateCurrentRow(new Row { ["C"] = Wide(999, 255), ["N"] = 20000 });
                Assert.Equal(was, cursor.Current.Id);
                rows[20]["C"] = Wide(999, 255); rows[20]["N"] = 20000;
                Assert.NotNull(cursor.FindRowByKey(210));
                cursor.UpdateCurrentRow(new Row { ["A"] = "home", ["B"] = null, ["C"] = null });
                rows[21]["A"] = "home"; rows[21]["B"] = null; rows[21]["C"] = null;
                table.Delete(table.FindRowByPrimaryKey(22));
                rows.Remove(22);
            }
            AssertAccessReadsU(path, rows, "after the engine changed the rows ACE had moved");

            var compacted = Compact(path);
            try { AssertAccessReadsU(compacted, rows, "after DAO compacted the file"); }
            finally { File.Delete(compacted); }
        }

        // ── Dates Access computes ────────────────────────────────────────────────

        [Fact]
        public void dates_access_computed_are_found_by_the_date_they_read_as_and_joined_as_access_joins_them()
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            Execute(path, "CREATE TABLE W1 (D DATETIME CONSTRAINT pk PRIMARY KEY, Id LONG)",
                          "CREATE TABLE W2 (Id LONG CONSTRAINT pk PRIMARY KEY, D DATETIME)", "CREATE INDEX ByD ON W2 (D)");
            // Minutes added to a date, and a date with fractions of a day added: what Access stores for these is
            // often not the number DateTime.ToOADate() gives for the date they read as.
            var statements = new List<string>();
            for (var k = 1; k <= 200; k++)
            {
                statements.Add("INSERT INTO W1 (D, Id) VALUES (DateAdd('n', " + (k * 7919) + ", #1/1/2020#), " + k + ")");
                statements.Add("INSERT INTO W1 (D, Id) VALUES (#3/4/1985# + " + (k % 24) + "/24 + " + (k % 60) + "/1440 + " + ((k * 7) % 60) + "/86400 + " + k + ", " + (1000 + k) + ")");
            }
            statements.Add("INSERT INTO W2 (Id, D) SELECT Id, D FROM W1");
            Execute(path, statements.ToArray());
            var joined = Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM W1 INNER JOIN W2 ON W1.D = W2.D"));
            Assert.Equal(400, joined);

            // Opened only to be read, so that ACE opens the file beside it (#62).
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                var w1 = db.GetTable("W1");
                var w2 = db.GetTable("W2");
                var join = new Joiner(w1, new[] { "D" }, w2, new[] { "D" });
                var cursor = w1.CreateIndexCursor();
                int notTheDateTimesNumber = 0, joinedByEngine = 0;
                foreach (var row in w1.Rows.ToList())
                {
                    var date = (DateTime)row["D"];
                    var id = (int)row["Id"];
                    Assert.Equal(id, w1.FindRowByPrimaryKey(date)["Id"]);
                    Assert.Equal(id, w2.CreateIndexCursor("ByD").FindRowByKey(date)["Id"]);
                    Assert.Equal(id, w1.CreateIndexCursor(new IndexRange { StartKey = new object[] { date }, EndKey = new object[] { date } }).Single()["Id"]);
                    Assert.Equal(id, cursor.FindClosestRowByKey(date)["Id"]);
                    var number = cursor.GetCurrentRowDateNumber("D").Value;
                    if (BitConverter.DoubleToInt64Bits(number) != BitConverter.DoubleToInt64Bits(date.ToOADate())) notTheDateTimesNumber++;
                    Assert.Equal(id, w1.FindRowByPrimaryKey(number)["Id"]);

                    var matches = join.FindRows(row).ToList();
                    Assert.Equal(id, matches.Single()["Id"]);
                    joinedByEngine += matches.Count;
                }
                Assert.Equal(joined, joinedByEngine);
                Assert.True(notTheDateTimesNumber > 40, "only " + notTheDateTimesNumber + " of 400 dates Access computed differ from their DateTime's number: the test no longer shows what it is for");

                // The number ACE gives for a row is the number the engine gives.
                var ace = AceRows(path, "SELECT Id, CDbl(D) AS V FROM W1").ToDictionary(r => Convert.ToInt32(r["Id"]), r => (double)r["V"]);
                foreach (var pair in ace.Take(50))
                    Assert.Equal(pair.Key, w1.FindRowByPrimaryKey(pair.Value)["Id"]);
            }
        }

        // ── Dates by the number stored ───────────────────────────────────────────

        [Fact]
        public void a_date_access_stored_is_found_by_the_number_it_stored_and_two_within_a_millisecond_are_told_apart()
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            Execute(path, "CREATE TABLE W (D DATETIME CONSTRAINT pk PRIMARY KEY, Name TEXT(20))");

            // Dates as Access makes them from text, to the second: each is found, through the key's index, by the
            // DateTime it reads as - the number Access stored is the number DateTime.ToOADate() gives.
            var random = new Random(35);
            var dates = new HashSet<DateTime>();
            while (dates.Count < 3000) dates.Add(new DateTime(1900, 1, 1).AddSeconds(random.Next(0, int.MaxValue) * 2L));
            Execute(path, dates.Select((date, i) => Insert("W", V("D", date, "Name", "n" + i))).ToArray());

            // Two more, a third of a millisecond apart: both read as the same DateTime.
            var when = new DateTime(2024, 5, 6, 7, 8, 9);
            Execute(path, "INSERT INTO W (D, Name) VALUES (" + Sql(when) + ", 'on the second')",
                          "INSERT INTO W (D, Name) VALUES (" + Sql(when) + " + 0.000000004, 'a hair after')");
            var stored = AceRows(path, "SELECT CDbl(D) AS V, Name FROM W WHERE Name IN ('on the second', 'a hair after')")
                .ToDictionary(row => (string)row["Name"], row => (double)row["V"]);
            Assert.NotEqual(stored["on the second"], stored["a hair after"]);

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("W");
                var missed = dates.Where(date => table.FindRowByPrimaryKey(date) == null).ToList();
                Assert.True(missed.Count == 0, missed.Count + " of 3000 dates Access stored are not the number DateTime.ToOADate() gives, the first " + missed.FirstOrDefault().ToString("o"));

                Assert.Equal(2, table.Rows.Count(row => (DateTime)row["D"] == when));
                Assert.Equal("on the second", table.FindRowByPrimaryKey(when)["Name"]);
                Assert.Equal("on the second", table.FindRowByPrimaryKey(stored["on the second"])["Name"]);
                Assert.Equal("a hair after", table.FindRowByPrimaryKey(stored["a hair after"])["Name"]);
                var cursor = table.CreateIndexCursor();
                Assert.Equal("on the second", cursor.FindClosestRowByKey(when)["Name"]);
                Assert.True(cursor.MoveNext());
                Assert.Equal("a hair after", cursor.Current["Name"]);

                // Each is changed on its own: the first renamed, the second deleted.
                var first = table.FindRowByPrimaryKey(when);
                first["Name"] = "renamed";
                table.Update(first);
                table.Delete(table.FindRowByPrimaryKey(stored["a hair after"]));
            }

            foreach (var read in new[] { path, Compact(path) })
            {
                Assert.Equal(3001, Convert.ToInt32(Scalar(read, "SELECT COUNT(*) FROM W")));
                var left = AceRows(read, "SELECT CDbl(D) AS V, Name FROM W WHERE D >= " + Sql(when) + " AND D < " + Sql(when.AddSeconds(1)));
                Assert.Equal(1, left.Count);
                Assert.Equal("renamed", left[0]["Name"]);
                Assert.Equal(stored["on the second"], (double)left[0]["V"]);       // the date itself was not touched
                Assert.Equal("renamed", Scalar(read, "SELECT Name FROM W WHERE D = " + Sql(when)));
                Assert.Equal(3001, DaoRows(read, "W", "pk").Count);
                if (read != path) File.Delete(read);
            }
        }

        // ── The first and the last day a date can be ─────────────────────────────

        [Fact]
        public void a_date_on_the_first_or_the_last_day_access_holds_is_read_at_any_time_and_written_as_access_reads_it()
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            Execute(path, "CREATE TABLE E (Id LONG CONSTRAINT pk PRIMARY KEY, D DATETIME)", "CREATE INDEX ByD ON E (D)",
                          "INSERT INTO E (Id, D) VALUES (1, CDate(-657434.25))",
                          "INSERT INTO E (Id, D) VALUES (2, #1/1/100 23:59:59#)",
                          "INSERT INTO E (Id, D) VALUES (3, CDate(2958465.99998842593))",
                          "INSERT INTO E (Id, D) VALUES (4, #1/1/100#)");

            var first = new DateTime(100, 1, 1);
            using (var db = Database.Open(path))
            {
                // Read as Access reads them: the engine read every time of 1 January 100 but midnight as null.
                var table = db.GetTable("E");
                Assert.Equal(first.AddHours(6), table.FindRowByPrimaryKey(1)["D"]);
                Assert.Equal(first.AddSeconds(86399), table.FindRowByPrimaryKey(2)["D"]);
                Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59), table.FindRowByPrimaryKey(3)["D"]);
                Assert.Equal(1, table.CreateIndexCursor("ByD").FindRowByKey(first.AddHours(6))["Id"]);

                // And written by the engine: inserted, and given by an update.
                table.Insert(new Row { ["Id"] = 5, ["D"] = first.AddHours(12).AddMinutes(30) });
                table.Insert(new Row { ["Id"] = 6, ["D"] = new DateTime(9999, 12, 31, 12, 0, 0) });
                var row = table.FindRowByPrimaryKey(4);
                row["D"] = first.AddHours(18);
                table.Update(row);
            }

            foreach (var read in new[] { path, Compact(path) })
            {
                var when = read == path ? "as the engine left it" : "after DAO compacted the file";
                var text = AceRows(read, "SELECT Id, Format(D, 'yyyy-mm-dd hh:nn:ss') AS T FROM E")
                    .ToDictionary(r => Convert.ToInt32(r["Id"]), r => (string)r["T"]);
                Assert.Equal(new[] { "0100-01-01 06:00:00", "0100-01-01 23:59:59", "9999-12-31 23:59:59", "0100-01-01 18:00:00", "0100-01-01 12:30:00", "9999-12-31 12:00:00" },
                             Enumerable.Range(1, 6).Select(id => text[id]));
                // Through the index, by Access's literals.
                Assert.Equal(5, Convert.ToInt32(Scalar(read, "SELECT Id FROM E WHERE D = #1/1/100 12:30:00#")));
                Assert.Equal(4, Convert.ToInt32(Scalar(read, "SELECT Id FROM E WHERE D = #1/1/100 18:00:00#")));
                Assert.True(6 == DaoRows(read, "E", "ByD").Count, "E as DAO walks ByD " + when);
                if (read != path) File.Delete(read);
            }
        }

        // ── Dates a hair from a millisecond's rounding ───────────────────────────

        // Numbers a hair from a millisecond's half, and the date a 64-bit process reads each as; dates, and the number a
        // 64-bit process writes for each. In a 32-bit process with the x87 unit at 64 bits of precision, as ACE leaves
        // the thread that ran a statement, the runtime's own conversions read each number as the millisecond beside it
        // and wrote each date as the double beside it (#100).
        private static readonly (long Number, DateTime Date)[] NumbersAHairFromAHalf =
        {
            (0x3E38DAEA1D7F4CF6, new DateTime(1899, 12, 30, 0, 0, 0, 1)),
            (unchecked((long)0xC11E17619CC339D9), new DateTime(550, 3, 1, 9, 40, 26, 844)),
            (unchecked((long)0xC0E35DB93D517005), new DateTime(1791, 5, 29, 18, 55, 46, 714)),
            (0x410B2163ECCE6C58, new DateTime(2508, 7, 2, 11, 46, 30, 267)),
            (0x4133B9623D5E4EC8, new DateTime(5439, 2, 17, 5, 45, 11, 831)),
            (0x41467E048670F3B2, new DateTime(9971, 8, 20, 1, 12, 27, 822)),
        };

        private static readonly (DateTime Date, long Number)[] DatesWrittenAHairApart =
        {
            (new DateTime(104, 11, 5, 9, 4, 37, 269), unchecked((long)0xC1240262C1A4A099)),
            (new DateTime(1518, 7, 23, 18, 50, 2, 758), unchecked((long)0xC10101B6472D2E2F)),
            (new DateTime(2132, 9, 10, 22, 31, 7, 4), 0x40F4BFFF032D2E2F),
            (new DateTime(5349, 3, 17, 19, 33, 48, 314), 0x41333917D0AD2E2F),
            (new DateTime(9988, 6, 22, 9, 17, 58, 627), 0x41468A083199144B),
        };

        [Fact]
        public void a_number_a_hair_from_a_millisecond_s_half_and_a_date_written_after_ace_ran_a_statement_are_those_of_a_64_bit_process()
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            Execute(path, "CREATE TABLE A (Id LONG CONSTRAINT pk PRIMARY KEY, D DATETIME)", "CREATE INDEX ByD ON A (D)",
                          "CREATE TABLE E (Id LONG CONSTRAINT pk PRIMARY KEY, D DATETIME)", "CREATE INDEX ByD ON E (D)");

            // ACE writes the numbers, given as doubles: it keeps each as it is. Read through OLE DB, each is the date a
            // 64-bit process reads; in a 32-bit one OLE DB converts with the runtime's own conversion, in the state ACE
            // has left the unit in, so that is asked only of a 64-bit process.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 0; i < NumbersAHairFromAHalf.Length; i++)
                    using (var insert = new OleDbCommand("INSERT INTO A (Id, D) VALUES (?, ?)", connection))
                    {
                        insert.Parameters.Add("Id", OleDbType.Integer).Value = i;
                        insert.Parameters.Add("D", OleDbType.Double).Value = BitConverter.Int64BitsToDouble(NumbersAHairFromAHalf[i].Number);
                        insert.ExecuteNonQuery();
                    }
                foreach (var row in AceRows(connection, "SELECT Id, CDbl(D) AS V, D FROM A"))
                {
                    var expected = NumbersAHairFromAHalf[Convert.ToInt32(row["Id"])];
                    Assert.Equal(expected.Number, BitConverter.DoubleToInt64Bits((double)row["V"]));
                    if (Environment.Is64BitProcess) Assert.Equal(expected.Date, row["D"]);
                }
            }

            // The engine, on this thread - where, in a 32-bit process, ACE has left the unit at 64 bits - reads ACE's
            // numbers as those dates, finds each through ACE's index by its date, and writes the dates.
            using (var db = Database.Open(path))
            {
                var a = db.GetTable("A");
                for (var i = 0; i < NumbersAHairFromAHalf.Length; i++)
                {
                    Assert.Equal(NumbersAHairFromAHalf[i].Date, a.FindRowByPrimaryKey(i)["D"]);
                    Assert.Equal(i, a.CreateIndexCursor("ByD").FindRowByKey(NumbersAHairFromAHalf[i].Date)["Id"]);
                }
                var e = db.GetTable("E");
                for (var i = 0; i < DatesWrittenAHairApart.Length; i++)
                    e.Insert(new Row { ["Id"] = i, ["D"] = DatesWrittenAHairApart[i].Date });
            }

            // ACE reads the numbers the engine wrote, by a scan and through the engine's index by each number, and again
            // after DAO has compacted the file.
            foreach (var read in new[] { path, Compact(path) })
            {
                var numbers = AceRows(read, "SELECT Id, CDbl(D) AS V FROM E").ToDictionary(r => Convert.ToInt32(r["Id"]), r => (double)r["V"]);
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(read)))
                {
                    connection.Open();
                    for (var i = 0; i < DatesWrittenAHairApart.Length; i++)
                    {
                        var number = BitConverter.Int64BitsToDouble(DatesWrittenAHairApart[i].Number);
                        Assert.Equal(DatesWrittenAHairApart[i].Number, BitConverter.DoubleToInt64Bits(numbers[i]));
                        using (var find = new OleDbCommand("SELECT Id FROM E WHERE D = ?", connection))
                        {
                            find.Parameters.Add("D", OleDbType.Double).Value = number;
                            Assert.Equal(i, Convert.ToInt32(find.ExecuteScalar()));
                        }
                    }
                }
                Assert.Equal(DatesWrittenAHairApart.Length, DaoRows(read, "E", "ByD").Count);
                if (read != path) File.Delete(read);
            }
        }

        // ── Ranges of dates and of numbers ───────────────────────────────────────

        [Fact]
        public void a_range_of_numbers_holds_what_between_gives_and_a_range_of_dates_the_rows_access_reads_a_date_in_it_for()
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            // Times of 30 December 1899 stored as negative numbers too, and days before it, whose times run back
            // from their midnight: CDate keeps the number it is given.
            var numbers = new[] { -2.75, -2.25, -1.75, -1.25, -1.0, -0.75, -0.5, -0.25, 0.0, 0.25, 0.5, 0.75, 1.25, 2.25 };
            var statements = new List<string> { "CREATE TABLE N (Id LONG CONSTRAINT pk PRIMARY KEY, D DATETIME)", "CREATE INDEX ByD ON N (D)" };
            for (var i = 0; i < numbers.Length; i++)
                statements.Add("INSERT INTO N (Id, D) VALUES (" + (i + 1) + ", CDate(" + numbers[i].ToString("R", CultureInfo.InvariantCulture) + "))");
            Execute(path, statements.ToArray());

            // The date Access reads a row as, in seconds from 30 December 1899: its day, and its time of day.
            const string Seconds = "(CDbl(DateValue(D)) * 86400 + Hour(D) * 3600 + Minute(D) * 60 + Second(D))";
            Func<DateTime, string> secondsOf = date => ((date.Date - new DateTime(1899, 12, 30)).Days * 86400.0 + date.TimeOfDay.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            Func<IEnumerable<Values>, List<int>> ids = rows => rows.Select(r => Convert.ToInt32(r["Id"])).OrderBy(id => id).ToList();

            // Opened only to be read, so that ACE and DAO open the file beside it (#62).
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                var table = db.GetTable("N");
                // The cursor's order is the index's as DAO walks it: the numbers'.
                Assert.Equal(DaoRows(path, "N", "ByD").Select(r => Convert.ToInt32(r["Id"])), table.CreateIndexCursor("ByD").Select(r => (int)r["Id"]));

                var day = new DateTime(1899, 12, 30);
                var ranges = new[]
                {
                    Tuple.Create(day.AddHours(5), day.AddHours(7)), Tuple.Create(day.AddHours(6), day.AddHours(6)),
                    Tuple.Create(day.AddHours(12), day.AddDays(3)), Tuple.Create(day.AddDays(-3), day.AddDays(1)),
                    Tuple.Create(day.AddDays(-2).AddHours(12), day.AddDays(-1).AddHours(3)), Tuple.Create(day.AddDays(-1), day.AddDays(-1).AddHours(12)),
                };
                foreach (var range in ranges)
                {
                    DateTime from = range.Item1, to = range.Item2;
                    var what = from.ToString("s") + " to " + to.ToString("s");

                    // Numbers: what BETWEEN gives, which takes its two ends either way round.
                    var between = ids(AceRows(path, "SELECT Id FROM N WHERE D BETWEEN " + Sql(from) + " AND " + Sql(to)));
                    double low = Math.Min(from.ToOADate(), to.ToOADate()), high = Math.Max(from.ToOADate(), to.ToOADate());
                    var byNumbers = table.CreateIndexCursor("ByD", new IndexRange { StartKey = new object[] { low }, EndKey = new object[] { high } })
                        .Select(r => (int)r["Id"]).OrderBy(id => id).ToList();
                    Assert.True(between.SequenceEqual(byNumbers), what + " as numbers: BETWEEN gives " + string.Join(",", between) + ", the engine " + string.Join(",", byNumbers));

                    // Dates: the rows Access reads a date in the range for.
                    var reading = ids(AceRows(path, "SELECT Id FROM N WHERE " + Seconds + " BETWEEN " + secondsOf(from) + " AND " + secondsOf(to)));
                    var byDates = table.CreateIndexCursor("ByD", new IndexRange { StartKey = new object[] { from }, EndKey = new object[] { to } })
                        .Select(r => (int)r["Id"]).OrderBy(id => id).ToList();
                    Assert.True(reading.SequenceEqual(byDates), what + " as dates: Access reads " + string.Join(",", reading) + ", the engine gives " + string.Join(",", byDates));
                }

                // Where the two part: six in the morning of 30 December 1899 stored as -0.25 (row 8) is not between
                // five and seven to BETWEEN, and is to a range of dates.
                Assert.DoesNotContain(8, ids(AceRows(path, "SELECT Id FROM N WHERE D BETWEEN #12/30/1899 5:00:00# AND #12/30/1899 7:00:00#")));
                Assert.Contains(8, table.CreateIndexCursor("ByD", new IndexRange { StartKey = new object[] { day.AddHours(5) }, EndKey = new object[] { day.AddHours(7) } })
                    .Select(r => (int)r["Id"]));

                // The closest row to a date the rows have: the first in the index's order Access reads it for.
                var first = AceRows(path, "SELECT Id FROM N WHERE " + Seconds + " = " + secondsOf(day.AddHours(6)) + " ORDER BY D");
                Assert.Equal(Convert.ToInt32(first[0]["Id"]), table.CreateIndexCursor("ByD").FindClosestRowByKey(day.AddHours(6))["Id"]);
            }
        }

        // ── Rows written before a column was dropped ─────────────────────────────

        /// <summary>Text of one Greek letter, which of them by <paramref name="i"/>: two bytes a character in a row.</summary>
        private static string Greek(int i, int length) { return new string((char)(0x3A9 + i % 20), length); }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void rows_written_before_a_column_was_dropped_and_updated_by_the_engine_are_rows_access_reads(bool accessDropped)
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            var statements = new List<string>
            {
                "CREATE TABLE T (Id LONG CONSTRAINT pk PRIMARY KEY, A TEXT(255), B TEXT(255), Notes MEMO, D TEXT(255), N LONG)",
                "CREATE INDEX ByA ON T (A)",
            };
            // Thirty rows that fill their pages; the text past Latin-1, so a row is as long as it looks.
            for (var i = 1; i <= 30; i++)
                statements.Add("INSERT INTO T (Id, A, B, Notes, N) VALUES (" + i + ", 'a" + i + "' & String(100, ChrW(" + (0x3A9 + i % 20) + ")), "
                               + "String(100, ChrW(" + (0x3A9 + (i + 1) % 20) + ")), String(2000, '" + (char)('a' + i % 26) + "'), " + i + ")");
            // Access leaves each row's bytes for a dropped column, as the engine's DropColumn does.
            if (accessDropped) statements.AddRange(new[] { "ALTER TABLE T DROP COLUMN B", "ALTER TABLE T DROP COLUMN Notes" });
            Execute(path, statements.ToArray());

            var expected = Enumerable.Range(1, 30).ToDictionary(i => i, i => V("A", "a" + i + Greek(i, 100), "D", null, "N", i));
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                if (!accessDropped)
                {
                    table.DropColumn("B");
                    table.DropColumn("Notes");
                }
                Action<int, string, string> set = (id, a, d) =>
                {
                    var row = table.FindRowByPrimaryKey(id);
                    row["A"] = a;
                    row["D"] = d;
                    table.Update(row);
                    expected[id]["A"] = a;
                    expected[id]["D"] = d;
                };
                // Past its page's room, back, smaller and larger where it is; one deleted, one added.
                set(3, "a3" + Greek(3, 253), Greek(4, 255));
                set(3, "home", null);
                set(2, "x", null);
                set(5, "a5" + Greek(5, 140), null);
                set(9, "a9" + Greek(9, 253), Greek(10, 255));
                table.Delete(table.FindRowByPrimaryKey(7));
                expected.Remove(7);
                table.Insert(new Row { ["Id"] = 31, ["A"] = "after", ["N"] = 31 });
                expected[31] = V("A", "after", "D", null, "N", 31);
            }

            foreach (var read in new[] { path, Compact(path) })
            {
                var when = read == path ? "as the engine left it" : "after DAO compacted the file";
                var ace = AceRows(read, "SELECT Id, A, D, N FROM T").ToDictionary(r => Convert.ToInt32(r["Id"]));
                Assert.True(expected.Keys.OrderBy(k => k).SequenceEqual(ace.Keys.OrderBy(k => k)), "the rows of T " + when);
                foreach (var pair in expected)
                {
                    Assert.True(Equals(pair.Value["A"], ace[pair.Key]["A"]), "A of row " + pair.Key + " " + when);
                    Assert.True(Equals(pair.Value["D"], ace[pair.Key]["D"]), "D of row " + pair.Key + " " + when);
                    Assert.Equal(pair.Value["N"], Convert.ToInt32(ace[pair.Key]["N"]));
                    // Through the index on A, where its key is whole: ACE's = finds no row whose key it cuts - 255
                    // characters of these letters - though its Seek does, for a row it wrote itself too (measured).
                    if (((string)pair.Value["A"]).Length <= 200)
                        Assert.Equal(pair.Key, Convert.ToInt32(Scalar(read, "SELECT Id FROM T WHERE A = '" + (string)pair.Value["A"] + "'")));
                }
                // DAO walks each index: every row once, with its values.
                foreach (var index in new[] { "ByA", "pk" })
                {
                    var walked = DaoRows(read, "T", index);
                    Assert.True(expected.Keys.OrderBy(k => k).SequenceEqual(walked.Select(r => Convert.ToInt32(r["Id"])).OrderBy(k => k)), "T as DAO walks " + index + " " + when);
                    foreach (var row in walked)
                        Assert.True(Equals(expected[Convert.ToInt32(row["Id"])]["A"], row["A"]), "A of row " + row["Id"] + " as DAO walks " + index + " " + when);
                }
                if (read != path) File.Delete(read);
            }

            // And ACE updates and deletes them in turn.
            Execute(path, "UPDATE T SET D = 'by ACE' WHERE Id IN (2, 3, 9)", "DELETE FROM T WHERE Id = 5");
            Assert.Equal(29, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM T")));
            Assert.Equal(3, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM T WHERE D = 'by ACE'")));
        }
    }
}
