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
    /// Every index of a table, kept by the managed engine and checked by ACE: the indexes of a table the engine
    /// creates - on one column and several, unique or not - and those of tables Access made. After the engine inserts, deletes and
    /// updates, ACE answers a WHERE and a COUNT on each indexed column as a scan of the rows answers them, adds rows
    /// of its own into the engine's index pages, refuses a duplicate in a unique index the engine filled, and DAO
    /// compacts the file with every row kept. ACE answers a condition on an indexed column from the index, so a row
    /// without its entry is one these queries miss, and an entry without its row is one they count: against an
    /// engine that keeps only the primary key they fail.
    /// </summary>
    public class EngineIndexesInAccessTests : IDisposable
    {
        private TempDb _tmp;

        public EngineIndexesInAccessTests() { _tmp = new TempDb(); }
        public void Dispose() { _tmp.Dispose(); }

        private static ColumnDefinition Long(string name) { return new ColumnDefinition(name, DataType.Int32); }
        private static ColumnDefinition Int(string name) { return new ColumnDefinition(name, DataType.Int16); }
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

        private static int Count(string path, string from, string where)
        {
            return Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM " + from + " WHERE " + where));
        }

        private static void Execute(OleDbConnection connection, string sql)
        {
            using (var command = new OleDbCommand(sql, connection))
                command.ExecuteNonQuery();
        }

        private static void Execute(string path, params string[] statements)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var sql in statements)
                    Execute(connection, sql);
            }
        }

        private static void AssertAceRefusesADuplicate(string path, string sql)
        {
            var refused = Assert.Throws<OleDbException>(() => Execute(path, sql));
            Assert.Contains("duplicate values", refused.Message);
        }

        // The same condition answered from the index and from the rows: "+ 0" and "& ''" keep ACE off the index.
        private static void AssertIndexAgreesWithScan(string path, string table, string column, string literal, int expected, bool text)
        {
            var throughIndex = Count(path, table, "[" + column + "] = " + literal);
            var throughRows = Count(path, table, (text ? "[" + column + "] & ''" : "[" + column + "] + 0") + " = " + literal);
            Assert.True(throughIndex == expected && throughRows == expected,
                table + "." + column + " = " + literal + ": " + throughIndex + " through the index, " + throughRows + " by scan, expected " + expected);
        }

        private static string Compact(string path, string dir)
        {
            var compacted = Path.Combine(dir, Guid.NewGuid().ToString("N") + "-compacted.mdb");
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Dao.Engine;
            try { dbeType.InvokeMember("CompactDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, compacted }); }
            finally { Dao.Release(dbe); }
            return compacted;
        }

        // ── A table the engine creates ───────────────────────────────────────

        private sealed class Item
        {
            public int Id;
            public string Code;
            public int Grp;
            public short Seq;
        }

        private static Item ItemOf(int i)
        {
            return new Item { Id = i, Code = "code " + i.ToString("D6"), Grp = i % 17, Seq = (short)(i % 300) };
        }

        private static Row RowOf(Item item)
        {
            return new Row { ["Id"] = item.Id, ["Code"] = item.Code, ["Grp"] = item.Grp, ["Seq"] = item.Seq };
        }

        private static Table CreateItems(Database db)
        {
            return db.CreateTable("Items", new[] { Long("Id"), Text("Code", 40), Long("Grp"), Int("Seq") }, new[] { "Id" }, new[]
            {
                new IndexDefinition("ByCode", "Code") { IsUnique = true },
                new IndexDefinition("ByGroup", "Grp"),
                new IndexDefinition("ByGroupAndSeq", "Grp", "Seq") { DescendingColumns = { "Seq" } },
            });
        }

        // 1..count in an order that is key order in no index.
        private static IEnumerable<int> Shuffled(int count)
        {
            return Enumerable.Range(1, count).OrderBy(k => (k * 7919L) % 10007);
        }

        // Inserts, deletes and updates by the engine, mirrored in the dictionary the assertions are made from.
        private static Dictionary<int, Item> FillItems(string path)
        {
            var items = new Dictionary<int, Item>();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = CreateItems(db);
                foreach (var i in Shuffled(3000))
                {
                    items[i] = ItemOf(i);
                    table.Insert(RowOf(items[i]));
                }
                for (var i = 5; i <= 3000; i += 5)
                {
                    table.DeleteRow("Id", i);
                    items.Remove(i);
                }
                for (var i = 3; i <= 3000; i += 7)
                {
                    if (!items.ContainsKey(i)) continue;
                    items[i].Code = "changed " + i;
                    items[i].Grp = 100 + i % 3;
                    table.UpdateByPrimaryKey(i, new Row { ["Code"] = items[i].Code, ["Grp"] = items[i].Grp });
                }
            }
            return items;
        }

        private static void AssertAceAnswersForItems(string path, Dictionary<int, Item> items)
        {
            Assert.Equal(items.Count, Scalar(path, "SELECT COUNT(*) FROM Items"));
            foreach (var grp in new[] { 0, 5, 16, 100, 101, 102, 999 })
                AssertIndexAgreesWithScan(path, "Items", "Grp", grp.ToString(), items.Values.Count(x => x.Grp == grp), text: false);
            foreach (var code in new[] { "code 000001", "code 000005", "code 000003", "changed 3", "changed 10", "code 002999", "CODE 002999", "ace 3500", "nothing" })
                AssertIndexAgreesWithScan(path, "Items", "Code", "'" + code + "'",
                    items.Values.Count(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)), text: true);
            foreach (var id in new[] { 1, 3, 5, 2999, 3000, 3500, 4000 })
                AssertIndexAgreesWithScan(path, "Items", "Id", id.ToString(), items.ContainsKey(id) ? 1 : 0, text: false);

            // Both columns of the two-column index, and a range on its first.
            Assert.Equal(items.Values.Count(x => x.Grp == 4 && x.Seq == 123), Count(path, "Items", "Grp = 4 AND Seq = 123"));
            Assert.Equal(items.Values.Count(x => x.Grp >= 3 && x.Grp <= 6), Count(path, "Items", "Grp BETWEEN 3 AND 6"));
            Assert.Equal(items.Values.Count(x => string.CompareOrdinal(x.Code, "code 0010") >= 0 && string.CompareOrdinal(x.Code, "code 0011") < 0),
                         Count(path, "Items", "Code >= 'code 0010' AND Code < 'code 0011'"));
            // In index order, from either end.
            Assert.Equal(items.Values.Select(x => x.Code).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).First(),
                         Scalar(path, "SELECT TOP 1 Code FROM Items ORDER BY Code"));
            Assert.Equal(items.Values.Max(x => x.Grp), Scalar(path, "SELECT TOP 1 Grp FROM Items ORDER BY Grp DESC, Id"));
        }

        [Fact]
        public void ace_answers_through_every_index_of_a_table_the_engine_created_and_changed()
        {
            var path = _tmp.NewMdbPath();
            var items = FillItems(path);

            AssertAceAnswersForItems(path, items);
        }

        [Fact]
        public void ace_adds_to_and_enforces_the_indexes_the_engine_built_and_the_engine_goes_on()
        {
            var path = _tmp.NewMdbPath();
            var items = FillItems(path);

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 3001; i <= 4000; i++)   // ACE writes into, and splits, the leaves the engine wrote
                {
                    items[i] = new Item { Id = i, Code = "ace " + i, Grp = i % 17, Seq = (short)(i % 300) };
                    Execute(connection, "INSERT INTO Items (Id, Code, Grp, Seq) VALUES (" + i + ", 'ace " + i + "', " + items[i].Grp + ", " + items[i].Seq + ")");
                }
                Execute(connection, "DELETE FROM Items WHERE Grp = 2");
                foreach (var gone in items.Values.Where(x => x.Grp == 2).Select(x => x.Id).ToList())
                    items.Remove(gone);
            }
            AssertAceRefusesADuplicate(path, "INSERT INTO Items (Id, Code, Grp, Seq) VALUES (9001, 'code 000001', 1, 1)");   // the engine's entry
            AssertAceRefusesADuplicate(path, "INSERT INTO Items (Id, Code, Grp, Seq) VALUES (1, 'not taken', 1, 1)");
            AssertAceAnswersForItems(path, items);

            // The engine reads what ACE wrote through the indexes, refuses what they refuse, and adds more.
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Items");
                Assert.Equal(items.Count, table.Rows.Count());
                Assert.Equal(3500, table.CreateIndexCursor("ByCode").FindRow("Code", "ace 3500")["Id"]);
                Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 9002, ["Code"] = "ACE 3500", ["Grp"] = 1, ["Seq"] = (short)1 }));
                Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 3500, ["Code"] = "not taken", ["Grp"] = 1, ["Seq"] = (short)1 }));
                for (var i = 5001; i <= 5600; i++)
                {
                    items[i] = ItemOf(i);
                    table.Insert(RowOf(items[i]));
                }
                table.DeleteRow("Id", 3500);
                items.Remove(3500);
            }
            AssertAceAnswersForItems(path, items);
        }

        [Fact]
        public void dao_compacts_a_table_with_the_engine_s_indexes_and_keeps_every_row()
        {
            var path = _tmp.NewMdbPath();
            var items = FillItems(path);
            Execute(path, "INSERT INTO Items (Id, Code, Grp, Seq) VALUES (3500, 'ace 3500', 5, 7)");
            items[3500] = new Item { Id = 3500, Code = "ace 3500", Grp = 5, Seq = 7 };

            var compacted = Compact(path, _tmp.Dir);

            AssertAceAnswersForItems(compacted, items);
            AssertAceRefusesADuplicate(compacted, "INSERT INTO Items (Id, Code, Grp, Seq) VALUES (9001, 'code 000001', 1, 1)");
            using (var db = Database.Open(compacted))
                Assert.Equal(new[] { "ByCode", "ByGroup", "ByGroupAndSeq", "PrimaryKey" },
                             db.GetTable("Items").Indexes.Select(ix => ix.Name).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void ace_seeks_in_and_adds_to_indexes_of_several_levels_the_engine_built()
        {
            var path = _tmp.NewMdbPath();
            const int count = 12000;
            Func<int, string> name = i => ((i * 7919L) % 100003).ToString("D6") + " " + new string((char)('a' + i % 26), 90) + " " + i;
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                // Long keys that share little: few to a page, so both trees have nodes above nodes.
                var table = db.CreateTable("Deep", new[] { Long("Id"), Text("Name", 120), Long("Grp") }, new[] { "Id" }, new[]
                {
                    new IndexDefinition("ByName", "Name") { IsUnique = true },
                    new IndexDefinition("ByGroupAndName", "Grp", "Name"),
                });
                foreach (var i in Shuffled(count))
                    table.Insert(new Row { ["Id"] = i, ["Name"] = name(i), ["Grp"] = i % 5 });
            }

            Assert.Equal(count, Scalar(path, "SELECT COUNT(*) FROM Deep"));
            foreach (var i in new[] { 1, 2, 4321, 6000, 11999, 12000 })
            {
                Assert.Equal(i, Scalar(path, "SELECT Id FROM Deep WHERE Name = '" + name(i) + "'"));
                Assert.Equal(1, Count(path, "Deep", "Grp = " + i % 5 + " AND Name = '" + name(i) + "'"));
            }
            AssertIndexAgreesWithScan(path, "Deep", "Name", "'" + name(count + 1) + "'", 0, text: true);
            Assert.Equal(count / 5, Count(path, "Deep", "Grp = 3"));
            Assert.Equal(Enumerable.Range(1, count).Select(name).OrderBy(n => n, StringComparer.Ordinal).First(),
                         Scalar(path, "SELECT TOP 1 Name FROM Deep ORDER BY Name"));

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = count + 1; i <= count + 2000; i++)
                    Execute(connection, "INSERT INTO Deep (Id, Name, Grp) VALUES (" + i + ", '" + name(i) + "', " + i % 5 + ")");
                Execute(connection, "DELETE FROM Deep WHERE Id <= 4000");
            }
            AssertAceRefusesADuplicate(path, "INSERT INTO Deep (Id, Name, Grp) VALUES (99999, '" + name(5000).ToUpperInvariant() + "', 1)");
            Assert.Equal(count - 4000 + 2000, Scalar(path, "SELECT COUNT(*) FROM Deep"));
            Assert.Equal(0, Count(path, "Deep", "Name = '" + name(4000) + "'"));
            Assert.Equal(count + 1500, Scalar(path, "SELECT Id FROM Deep WHERE Name = '" + name(count + 1500) + "'"));

            var compacted = Compact(path, _tmp.Dir);
            Assert.Equal(count - 4000 + 2000, Scalar(compacted, "SELECT COUNT(*) FROM Deep"));
            Assert.Equal(7777, Scalar(compacted, "SELECT Id FROM Deep WHERE Name = '" + name(7777) + "'"));
        }

        [Fact]
        public void ace_reads_a_table_the_engine_gave_thirty_two_indexes()
        {
            // More index usage maps than one page of them holds: the engine puts the rest on a second.
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var indexes = Enumerable.Range(1, 31).Select(i => new IndexDefinition("Index " + i, i % 2 == 0 ? "Code" : "Grp")).ToArray();
                var table = db.CreateTable("Many", new[] { Long("Id"), Text("Code", 40), Long("Grp") }, new[] { "Id" }, indexes);
                for (var i = 1; i <= 500; i++)
                    table.Insert(new Row { ["Id"] = i, ["Code"] = "code " + i, ["Grp"] = i % 10 });
            }

            AssertIndexAgreesWithScan(path, "Many", "Grp", "7", 50, text: false);
            AssertIndexAgreesWithScan(path, "Many", "Code", "'code 250'", 1, text: true);
            Execute(path, "INSERT INTO Many (Id, Code, Grp) VALUES (501, 'ace', 7)");
            AssertIndexAgreesWithScan(path, "Many", "Grp", "7", 51, text: false);

            var compacted = Compact(path, _tmp.Dir);
            AssertIndexAgreesWithScan(compacted, "Many", "Grp", "7", 51, text: false);
            using (var db = Database.Open(compacted))
                Assert.Equal(32, db.GetTable("Many").Indexes.Count);
        }

        [Fact]
        public void ace_and_the_engine_agree_on_nulls_in_a_unique_index()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Pairs", new[] { Long("A"), Long("B"), Text("T", 20) }, null, new[]
                {
                    new IndexDefinition("AB", "A", "B") { IsUnique = true },
                    new IndexDefinition("T", "T") { IsUnique = true },
                    new IndexDefinition("Sparse", "B") { IgnoreNulls = true },
                });
                table.Insert(new Row { ["A"] = 1, ["B"] = null, ["T"] = "x" });
                table.Insert(new Row { ["A"] = 1, ["B"] = null });      // a null in the pair: no duplicate, for the engine
                table.Insert(new Row());
                table.Insert(new Row { ["A"] = 1, ["B"] = 2 });
                Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["A"] = 1, ["B"] = 2 }));
            }

            // ...and none for ACE, which refuses what the engine refused.
            Execute(path, "INSERT INTO Pairs (A, B) VALUES (1, NULL)", "INSERT INTO Pairs (A, B, T) VALUES (NULL, NULL, NULL)");
            AssertAceRefusesADuplicate(path, "INSERT INTO Pairs (A, B) VALUES (1, 2)");
            AssertAceRefusesADuplicate(path, "INSERT INTO Pairs (A, B, T) VALUES (7, 7, 'X')");
            Assert.Equal(6, Scalar(path, "SELECT COUNT(*) FROM Pairs"));
            Assert.Equal(5, Count(path, "Pairs", "B IS NULL"));
            Assert.Equal(3, Count(path, "Pairs", "A = 1 AND B IS NULL"));
            Assert.Equal(1, Count(path, "Pairs", "B = 2"));
            Assert.Equal(5, Count(path, "Pairs", "T IS NULL"));

            var compacted = Compact(path, _tmp.Dir);
            Assert.Equal(6, Scalar(compacted, "SELECT COUNT(*) FROM Pairs"));
            Assert.Equal(3, Count(compacted, "Pairs", "A = 1 AND B IS NULL"));
        }

        // DBNull is no value and a Yes/No given as a number or as text is the Yes or No it stands for - in the row and
        // in its key alike (#9): ACE finds through each index what it finds by reading the rows.
        [Fact]
        public void ace_finds_dbnull_and_yes_no_values_through_the_index_as_the_rows_have_them()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("Given", new[] { Long("Id"), Text("Code", 20), new ColumnDefinition("Flag", DataType.Boolean) }, new[] { "Id" }, new[]
                {
                    new IndexDefinition("ByCode", "Code") { IsUnique = true },
                    new IndexDefinition("ByFlag", "Flag"),
                });
                table.Insert(new Row { ["Id"] = 1, ["Code"] = DBNull.Value, ["Flag"] = 1 });
                table.Insert(new Row { ["Id"] = 2, ["Code"] = "", ["Flag"] = "true" });
                table.Insert(new Row { ["Id"] = 3, ["Code"] = null, ["Flag"] = 0 });
                table.Insert(new Row { ["Id"] = 4, ["Code"] = "x", ["Flag"] = DBNull.Value });
                Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 5, ["Code"] = "" }));
            }

            Assert.Equal(2, Count(path, "Given", "Code IS NULL"));
            Assert.Equal(2, Count(path, "Given", "IsNull(Code)"));                 // by the rows
            Assert.Equal(1, Count(path, "Given", "Code = ''"));
            Assert.Equal(3, Count(path, "Given", "Len(Code & '') = 0"));           // by the rows: the two nulls and the empty string
            Assert.Equal(2, Scalar(path, "SELECT Id FROM Given WHERE Code = ''"));
            Assert.Equal(2, Count(path, "Given", "Flag = True"));
            Assert.Equal(2, Count(path, "Given", "Flag + 0 = -1"));                // by the rows
            Assert.Equal(2, Count(path, "Given", "Flag = False"));
            Assert.Equal(2, Count(path, "Given", "Flag + 0 = 0"));
            Assert.Equal(2, Count(path, "Given", "Flag = True AND Id < 3"));
            AssertAceRefusesADuplicate(path, "INSERT INTO Given (Id, Code, Flag) VALUES (5, 'X', False)");
            Execute(path, "INSERT INTO Given (Id, Code, Flag) VALUES (6, NULL, True)", "DELETE FROM Given WHERE Id = 1");
            Assert.Equal(2, Count(path, "Given", "Code IS NULL"));
            Assert.Equal(2, Count(path, "Given", "Flag = True"));
            Assert.Equal(4, Scalar(Compact(path, _tmp.Dir), "SELECT COUNT(*) FROM Given"));
        }

        // ── Tables ACE makes: every column type the engine keys ──────────────

        private sealed class Typed
        {
            public int K;
            public byte? B;
            public short? I;
            public int? L;
            public decimal? C;
            public float? F;
            public double? D;
            public DateTime? T;
            public bool Y;
            public string M;
        }

        // A null in each column in turn; negative, zero and positive numbers; a Memo past a key's 255 characters.
        private static Typed TypedOf(int k)
        {
            return new Typed
            {
                K = k,
                B = k % 7 == 0 ? (byte?)null : (byte)(k % 200),
                I = k % 7 == 1 ? (short?)null : (short)(k % 50 - 25),
                L = k % 7 == 2 ? (int?)null : k * 37 - 4000,
                C = k % 7 == 3 ? (decimal?)null : (k % 60 - 30) * 1.25m,
                F = k % 7 == 4 ? (float?)null : (k % 40 - 20) * 0.5f,
                D = k % 7 == 5 ? (double?)null : (k % 80 - 40) * 0.25,
                T = k % 7 == 6 ? (DateTime?)null : new DateTime(2020, 1, 1).AddMinutes(k % 90 * 61).AddSeconds(k % 60),
                Y = k % 3 == 0,
                M = k % 5 == 0 ? null : k % 4 == 0 ? new string('m', 300) + k % 10 : "memo " + k % 20,
            };
        }

        private static readonly string[] TypedColumns = { "b", "i", "l", "c", "f", "d", "t", "y", "m" };

        private static Row RowOf(Typed v)
        {
            return new Row
            {
                ["k"] = v.K, ["b"] = v.B, ["i"] = v.I, ["l"] = v.L, ["c"] = v.C, ["f"] = v.F, ["d"] = v.D, ["t"] = v.T, ["y"] = v.Y, ["m"] = v.M,
            };
        }

        private static void AceInsert(OleDbConnection connection, string table, Typed v)
        {
            using (var command = new OleDbCommand("INSERT INTO " + table + " (k, b, i, l, c, f, d, t, y, m) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", connection))
            {
                Action<OleDbType, object> add = (type, value) => command.Parameters.Add("p" + command.Parameters.Count, type).Value = value ?? DBNull.Value;
                add(OleDbType.Integer, v.K);
                add(OleDbType.UnsignedTinyInt, v.B);
                add(OleDbType.SmallInt, v.I);
                add(OleDbType.Integer, v.L);
                add(OleDbType.Currency, v.C);
                add(OleDbType.Single, v.F);
                add(OleDbType.Double, v.D);
                add(OleDbType.Date, v.T);
                add(OleDbType.Boolean, v.Y);
                add(OleDbType.LongVarWChar, v.M);
                command.ExecuteNonQuery();
            }
        }

        // ACE makes the table: an AutoNumber key and an index on each column, ascending or descending. (No GUID
        // column: see the_engine_refuses_a_replication_id_column_ace_made_by_sql.)
        private static void AceCreateTyped(string path, string table, bool descending)
        {
            var statements = new List<string>
            {
                "CREATE TABLE " + table + " (id COUNTER CONSTRAINT pk PRIMARY KEY, k LONG, b BYTE, i SHORT, l LONG, c CURRENCY, f SINGLE, d DOUBLE, t DATETIME, y YESNO, m MEMO)",
            };
            statements.AddRange(TypedColumns.Select(c => "CREATE INDEX ix_" + c + " ON " + table + " (" + c + (descending ? " DESC" : "") + ")"));
            Execute(path, statements.ToArray());
        }

        private static string Invariant(IFormattable value) { return value.ToString(null, System.Globalization.CultureInfo.InvariantCulture); }

        // A condition on each column, answered through its index and - where an expression keeps ACE off the index - by
        // a scan, against what the rows in hand say.
        private static void AssertAceAnswersForTyped(string path, string table, List<Typed> rows)
        {
            Action<string, string, int> same = (indexed, scanned, expected) =>
            {
                var throughIndex = Count(path, table, indexed);
                var throughRows = scanned == null ? expected : Count(path, table, scanned);
                Assert.True(throughIndex == expected && throughRows == expected,
                    table + " WHERE " + indexed + ": " + throughIndex + " through the index, " + throughRows + " by scan, expected " + expected);
            };

            Assert.Equal(rows.Count, Scalar(path, "SELECT COUNT(*) FROM " + table));
            Assert.Equal(rows.Count, Scalar(path, "SELECT COUNT(*) FROM (SELECT DISTINCT id FROM " + table + ")"));
            foreach (byte b in new byte[] { 1, 57, 199 })
                same("b = " + b, "b + 0 = " + b, rows.Count(r => r.B == b));
            foreach (short i in new short[] { -25, -1, 0, 24 })
                same("i = " + i, "i + 0 = " + i, rows.Count(r => r.I == i));
            foreach (int l in new[] { -4000 + 37, 0 * 37 - 4000, 110 * 37 - 4000, 450 * 37 - 4000 })
                same("l = " + l, "l + 0 = " + l, rows.Count(r => r.L == l));
            foreach (decimal c in new[] { -37.5m, -1.25m, 0m, 36.25m, 999.5m })
                same("c = " + Invariant(c), "c + 0 = " + Invariant(c), rows.Count(r => r.C == c));
            foreach (float f in new[] { -10f, -0.5f, 0f, 9.5f })
                same("f = " + Invariant(f), "f + 0 = " + Invariant(f), rows.Count(r => r.F == f));
            foreach (double d in new[] { -10d, -0.25, 0d, 9.75, 999.75 })
                same("d = " + Invariant(d), "d + 0 = " + Invariant(d), rows.Count(r => r.D == d));
            foreach (int k in new[] { 0, 1, 59, 89 })
            {
                var t = new DateTime(2020, 1, 1).AddMinutes(k * 61).AddSeconds(k % 60);
                var literal = "#" + t.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "#";
                same("t = " + literal, "t + 0 = " + literal, rows.Count(r => r.T == t));
            }
            same("y = True", "y + 0 = -1", rows.Count(r => r.Y));
            same("y = False", "y + 0 = 0", rows.Count(r => !r.Y));
            // (Not a Memo longer than a key's 255 characters: asked for one by "=", ACE finds none through the index
            // even among rows it wrote itself. They are counted below, by what they begin with.)
            foreach (var m in new[] { "memo 3", "MEMO 19", "changed" })
                same("m = '" + m + "'", "m & '' = '" + m + "'", rows.Count(r => string.Equals(r.M, m, StringComparison.OrdinalIgnoreCase)));
            same("m LIKE 'mmmm%'", "m & '' LIKE 'mmmm%'", rows.Count(r => r.M != null && r.M.StartsWith("mmmm")));

            // Nulls, and ranges across zero.
            same("b IS NULL", null, rows.Count(r => r.B == null));
            same("i IS NULL", null, rows.Count(r => r.I == null));
            same("l IS NULL", null, rows.Count(r => r.L == null));
            same("c IS NULL", null, rows.Count(r => r.C == null));
            same("f IS NULL", null, rows.Count(r => r.F == null));
            same("d IS NULL", null, rows.Count(r => r.D == null));
            same("t IS NULL", null, rows.Count(r => r.T == null));
            same("m IS NULL", null, rows.Count(r => r.M == null));
            same("i < 0", "i + 0 < 0", rows.Count(r => r.I < 0));
            same("c BETWEEN -5 AND 5", "c + 0 BETWEEN -5 AND 5", rows.Count(r => r.C >= -5 && r.C <= 5));
            same("f < -3.25", "f + 0 < -3.25", rows.Count(r => r.F < -3.25f));
            same("d > 2.1", "d + 0 > 2.1", rows.Count(r => r.D > 2.1));
            same("t >= #2020-01-03#", "t + 0 >= #2020-01-03#", rows.Count(r => r.T >= new DateTime(2020, 1, 3)));
        }

        // ACE writes rows and their keys; the engine adds rows, deletes rows ACE wrote - which takes ACE's own entries
        // out only if the engine makes the keys ACE made - and updates; ACE adds and deletes again. Ascending and
        // descending: the corpus has no descending index on an Integer or a Date/Time column.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ace_and_the_engine_keep_an_index_on_every_column_type_between_them(bool descending)
        {
            var path = _tmp.NewMdbPath();
            using (Database.Create(path, FileFormat.Access2000)) { }
            AceCreateTyped(path, "Typed", descending);

            var rows = Enumerable.Range(0, 100).Select(TypedOf).ToList();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var row in rows)
                    AceInsert(connection, "Typed", row);
            }
            AssertAceAnswersForTyped(path, "Typed", rows);

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Typed");
                Assert.Equal(10, table.Indexes.Count);
                Assert.All(table.Indexes.Where(ix => !ix.IsPrimaryKey), ix => Assert.Equal(!descending, ix.Columns.Single().IsAscending));
                var ids = new Dictionary<int, int>();
                for (var k = 100; k < 400; k++)
                {
                    var row = RowOf(TypedOf(k));
                    table.Insert(row);
                    ids[k] = (int)row["id"];
                    rows.Add(TypedOf(k));
                }
                Assert.Equal(Enumerable.Range(101, 300), ids.Values);   // after ACE's hundred
                for (var k = 1; k < 100; k += 4)                         // rows ACE wrote: id k + 1
                {
                    table.DeleteRow("id", k + 1);
                    rows.RemoveAll(r => r.K == k);
                }
                for (var k = 102; k < 400; k += 9)
                {
                    var changed = rows.Single(r => r.K == k);
                    changed.D = 999.75; changed.C = 999.5m; changed.M = "changed"; changed.Y = !changed.Y; changed.T = null;
                    table.UpdateByPrimaryKey(ids[k], new Row { ["d"] = changed.D, ["c"] = changed.C, ["m"] = changed.M, ["y"] = changed.Y, ["t"] = null });
                }
                for (var k = 2; k < 100; k += 10)                        // rows ACE wrote, rewritten by the engine
                {
                    var changed = rows.Single(r => r.K == k);
                    changed.F = -10f; changed.I = 24; changed.B = null;
                    table.UpdateByPrimaryKey(k + 1, new Row { ["f"] = changed.F, ["i"] = changed.I, ["b"] = null });
                }
            }
            AssertAceAnswersForTyped(path, "Typed", rows);

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var k = 400; k < 450; k++)
                {
                    AceInsert(connection, "Typed", TypedOf(k));
                    rows.Add(TypedOf(k));
                }
                Execute(connection, "DELETE FROM Typed WHERE k BETWEEN 200 AND 259");   // rows the engine wrote
                rows.RemoveAll(r => r.K >= 200 && r.K <= 259);
            }
            AssertAceAnswersForTyped(path, "Typed", rows);
            AssertAceAnswersForTyped(Compact(path, _tmp.Dir), "Typed", rows);
        }

        // A Replication ID (GUID) column ACE makes by SQL is kept among the row's variable-length values. The engine
        // read a GUID only from the fixed-length ones, so it refused an index on such a column rather than key rows
        // by bytes that were not their values. It reads the column where the table's
        // definition says it is, and keeps the index: ACE answers through it.
        [Fact]
        public void ace_answers_through_an_index_on_a_replication_id_column_it_made_by_sql()
        {
            var path = _tmp.NewMdbPath();
            using (Database.Create(path, FileFormat.Access2000)) { }
            Execute(path, "CREATE TABLE G (id COUNTER CONSTRAINT pk PRIMARY KEY, k LONG, g GUID)", "CREATE INDEX ixg ON G (g)",
                "INSERT INTO G (k, g) VALUES (1, {guid {00000001-0001-0002-0304-05060708090A}})",
                "INSERT INTO G (k, g) VALUES (2, {guid {00000002-0001-0002-0304-05060708090A}})");

            var added = new Guid("0f0e0d0c-0b0a-0908-0706-050403020100");
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("G");
                Assert.Equal(new Guid("00000001-0001-0002-0304-05060708090A"), table.Rows.ToList()[0]["g"]);
                table.Insert(new Row { ["k"] = 3, ["g"] = added });
                table.Insert(new Row { ["k"] = 4, ["g"] = added });
                table.DeleteRow("k", 1);
                table.UpdateByPrimaryKey(2, new Row { ["g"] = new Guid("00000009-0001-0002-0304-05060708090A") });
                Assert.Equal(3, table.CreateIndexCursor("ixg").FindRow("g", added)["k"]);
            }

            Assert.Equal(0, Count(path, "G", "g = {guid {00000001-0001-0002-0304-05060708090A}}"));
            Assert.Equal(0, Count(path, "G", "g = {guid {00000002-0001-0002-0304-05060708090A}}"));
            Assert.Equal(1, Count(path, "G", "g = {guid {00000009-0001-0002-0304-05060708090A}}"));
            Assert.Equal(2, Count(path, "G", "g = {guid {0f0e0d0c-0b0a-0908-0706-050403020100}}"));
            Execute(path, "INSERT INTO G (k, g) VALUES (5, {guid {0f0e0d0c-0b0a-0908-0706-050403020100}})");
            Assert.Equal(3, Count(Compact(path, _tmp.Dir), "G", "g = {guid {0f0e0d0c-0b0a-0908-0706-050403020100}}"));
        }

        // Access keys a floating-point number by its bits: -0 is a key of its own, below 0; a NaN (sign bit set, as
        // .NET's is) is below -infinity. ACE stores all of them, and the engine's keys are ACE's: each deletes the
        // other's rows of them, and the index answers are the same whoever wrote the row.
        [Fact]
        public void ace_and_the_engine_key_negative_zero_nan_and_the_infinities_alike()
        {
            var path = _tmp.NewMdbPath();
            using (Database.Create(path, FileFormat.Access2000)) { }
            Execute(path, "CREATE TABLE S (name TEXT(20), x DOUBLE, f SINGLE)",
                "CREATE INDEX ix ON S (x)", "CREATE INDEX ixd ON S (x DESC)", "CREATE INDEX iff ON S (f)", "CREATE INDEX iffd ON S (f DESC)");
            var numbers = new Dictionary<string, double>
            {
                { "zero", 0d }, { "negzero", -0d }, { "one", 1d }, { "negone", -1d },
                { "nan", double.NaN }, { "inf", double.PositiveInfinity }, { "neginf", double.NegativeInfinity },
            };
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var number in numbers)
                    using (var command = new OleDbCommand("INSERT INTO S (name, x, f) VALUES (?, ?, ?)", connection))
                    {
                        command.Parameters.Add("n", OleDbType.VarWChar).Value = number.Key;
                        command.Parameters.Add("x", OleDbType.Double).Value = number.Value;
                        command.Parameters.Add("f", OleDbType.Single).Value = (float)number.Value;
                        command.ExecuteNonQuery();
                    }
            }
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("S");
                foreach (var number in numbers)
                    table.Insert(new Row { ["name"] = "e-" + number.Key, ["x"] = number.Value, ["f"] = (float)number.Value });
            }

            // Whoever wrote the row: zero is found and negative zero is not; NaN is below every number.
            Action<int, int, int, int> counts = (zero, atMostZero, aboveOne, belowMinusOne) =>
            {
                foreach (var column in new[] { "x", "f" })
                {
                    Assert.Equal(zero, Count(path, "S", column + " = 0"));
                    Assert.Equal(atMostZero, Count(path, "S", column + " <= 0"));
                    Assert.Equal(aboveOne, Count(path, "S", column + " > 1"));
                    Assert.Equal(belowMinusOne, Count(path, "S", column + " < -1"));
                }
            };
            counts(2, 10, 2, 4);

            using (var db = Database.Open(path))   // the engine takes out ACE's: their entries are under the keys it makes
            {
                var table = db.GetTable("S");
                foreach (var name in new[] { "negzero", "nan", "inf", "neginf" })
                    table.DeleteRow("name", name);
            }
            counts(2, 7, 1, 2);

            Execute(path, "DELETE FROM S WHERE name LIKE 'e-%'");   // and ACE the engine's
            counts(1, 2, 0, 0);
            Assert.Equal(3, Scalar(path, "SELECT COUNT(*) FROM S"));
            Assert.Equal(3, Scalar(Compact(path, _tmp.Dir), "SELECT COUNT(*) FROM S"));
        }

        // In a table ACE wrote, a long value the engine writes after it has deleted a row with one goes into the space
        // the delete freed. ACE could not read it: the value is a chain of chunks, the
        // page its chain starts on carries a stamp ACE put there for the value it held before, and the engine's
        // reference to the new value had zeros where ACE looks for that stamp. The reference carries the page's stamp
        // now. This is the test of freed space being used again in an Access-written file that the engine's own
        // corpus cannot give (its one file of long values, blobV2007.accdb, has an index on an attachment column, so
        // its table is not written to at all).
        [Fact]
        public void ace_reads_a_long_value_the_engine_writes_where_a_delete_freed_space_in_a_table_ace_wrote()
        {
            var path = _tmp.NewMdbPath();
            using (Database.Create(path, FileFormat.Access2000)) { }
            Execute(path, "CREATE TABLE Blobs (id COUNTER CONSTRAINT pk PRIMARY KEY, name TEXT(20), stamp DATETIME, data LONGBINARY)",
                "CREATE INDEX ix_stamp ON Blobs (stamp)", "CREATE UNIQUE INDEX ix_name ON Blobs (name)");
            Func<int, byte[]> bytes = seed => Enumerable.Range(0, 200000).Select(i => (byte)(i * 31 + seed)).ToArray();
            var day = new DateTime(2021, 3, 4, 5, 6, 7);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 1; i <= 6; i++)
                    using (var command = new OleDbCommand("INSERT INTO Blobs (name, stamp, data) VALUES (?, ?, ?)", connection))
                    {
                        command.Parameters.Add("n", OleDbType.VarWChar).Value = "blob " + i;
                        command.Parameters.Add("s", OleDbType.Date).Value = day.AddDays(i);
                        command.Parameters.Add("d", OleDbType.LongVarBinary).Value = bytes(i);
                        command.ExecuteNonQuery();
                    }
            }

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Blobs");
                table.Insert(new Row { ["name"] = "added", ["stamp"] = day.AddDays(20), ["data"] = bytes(20) });   // no delete before it
                table.DeleteRow("name", "blob 3");
                var row = new Row { ["name"] = "blob 3", ["stamp"] = day.AddDays(30), ["data"] = bytes(77) };
                table.Insert(row);
                Assert.Equal(8, row["id"]);
                Assert.Equal(bytes(77), table.CreateIndexCursor().FindRow("name", "blob 3")["data"]);   // the engine reads it back
            }

            // The rows, their keys and their indexes are right: ACE answers through each index.
            Assert.Equal(7, Scalar(path, "SELECT COUNT(*) FROM Blobs"));
            Assert.Equal(1, Count(path, "Blobs", "stamp = #2021-03-05 05:06:07#"));
            Assert.Equal(0, Count(path, "Blobs", "stamp = #2021-03-07 05:06:07#"));   // the deleted row's
            Assert.Equal(1, Count(path, "Blobs", "stamp = #2021-04-03 05:06:07#"));   // the new row's
            Assert.Equal(8, Scalar(path, "SELECT id FROM Blobs WHERE name = 'blob 3'"));
            AssertAceRefusesADuplicate(path, "INSERT INTO Blobs (name) VALUES ('blob 3')");
            foreach (var i in new[] { 1, 2, 4, 5, 6 })
                Assert.Equal(bytes(i), (byte[])Scalar(path, "SELECT data FROM Blobs WHERE name = 'blob " + i + "'"));
            Assert.Equal(bytes(20), (byte[])Scalar(path, "SELECT data FROM Blobs WHERE name = 'added'"));

            // And the value written into the freed space, which ACE could not read; then DAO compacts the file with it.
            Assert.Equal(bytes(77), (byte[])Scalar(path, "SELECT data FROM Blobs WHERE name = 'blob 3'"));
            Assert.Equal(bytes(77), (byte[])Scalar(Compact(path, _tmp.Dir), "SELECT data FROM Blobs WHERE name = 'blob 3'"));
        }
    }
}
