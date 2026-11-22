using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Rows of tables Microsoft Access made, read and written by the managed engine with ACE as the referee.
    /// A table's definition says where each column is kept in a row, and it
    /// cannot be worked out from the columns in order: Access gives a Yes/No column no room, leaves a deleted
    /// column's number and room behind, keeps a Replication ID made by SQL among the variable-length values and SQL's
    /// CHAR among the fixed-length ones. The engine read and wrote by a layout of its own, so it wrote such tables
    /// wrongly and read some wrongly. Each test has ACE make the table and rows, has the engine read them, then has
    /// the engine insert, update and delete, and ACE read the result by scan, through the primary key and after a DAO
    /// compact - and then has ACE change the file again.
    /// </summary>
    public class RowLayoutInAccessTests : IDisposable
    {
        private TempDb _tmp;

        public RowLayoutInAccessTests() { _tmp = new TempDb(); }
        public void Dispose() { _tmp.Dispose(); }

        /// <summary>A new Access 2000 file with the tables <paramref name="statements"/> have ACE make.</summary>
        private string AceMade(params string[] statements)
        {
            var path = _tmp.NewMdbPath();
            using (Database.Create(path, FileFormat.Access2000)) { }
            Execute(path, statements);
            return path;
        }

        // ── An update changes what it names, and a refused one nothing ───────

        // A Date/Time is a double, and Access keeps whatever double it is given. The engine made an update's new row
        // of the old one read and written again, and it reads a date to the millisecond: an update of the name
        // moved the date. And a value the column could not hold was refused only after the old row was marked
        // deleted, so the update threw and the row was gone, its key still in the index.
        [Fact]
        public void ace_reads_what_it_wrote_after_the_engine_updates_another_column_and_after_an_update_it_refuses()
        {
            var path = AceMade(
                "CREATE TABLE T (Id LONG CONSTRAINT pk PRIMARY KEY, Name TEXT(20), B BYTE, D DATETIME, Notes MEMO)",
                "INSERT INTO T (Id, Name, B, D, Notes) VALUES (1, 'one', 7, CDate(45000.1234567891), String(9000, 'n'))",
                "INSERT INTO T (Id, Name, B, D, Notes) VALUES (2, 'two', 8, CDate(-1.7512345678), String(300, 'm'))");
            Func<int, double> stored = id => Convert.ToDouble(Scalar(path, "SELECT CDbl(D) FROM T WHERE Id = " + id));
            double first = stored(1), second = stored(2);
            Assert.NotEqual(first, DateTime.FromOADate(first).ToOADate());   // finer than DateTime reads it

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                table.UpdateByPrimaryKey(1, new Row { ["Name"] = "uno" });
                table.UpdateByPrimaryKey(2, new Row { ["Notes"] = new string('k', 5000) });

                Assert.ThrowsAny<Exception>(() => table.UpdateByPrimaryKey(1, new Row { ["B"] = 300, ["Notes"] = "replaced" }));
                Assert.ThrowsAny<Exception>(() => table.UpdateByPrimaryKey(2, new Row { ["D"] = "not a date" }));
                Assert.ThrowsAny<Exception>(() => table.Insert(new Row { ["Id"] = 3, ["Name"] = "three", ["B"] = -1 }));
            }

            Assert.Equal(first, stored(1));
            Assert.Equal(second, stored(2));
            foreach (var file in new[] { path, Compact(path) })
            {
                Assert.Equal(2, Convert.ToInt32(Scalar(file, "SELECT COUNT(*) FROM T")));
                Assert.Equal("uno", Scalar(file, "SELECT Name FROM T WHERE Id = 1"));
                Assert.Equal(new string('n', 9000), Scalar(file, "SELECT Notes FROM T WHERE Id = 1"));
                Assert.Equal(7, Convert.ToInt32(Scalar(file, "SELECT B FROM T WHERE Id = 1")));
                Assert.Equal(new string('k', 5000), Scalar(file, "SELECT Notes FROM T WHERE Id = 2"));
                Assert.Equal(first, Convert.ToDouble(Scalar(file, "SELECT CDbl(D) FROM T WHERE Id = 1")));
            }
            AssertEngineReadsWhatAceReads(path, "T");
        }

        // A Double primary key can hold a number and the next Double after it, and both zeros: they are different
        // keys to Access. The engine confirmed the row an update or delete named by comparing as decimals, where
        // each pair is equal, and changed the first of the two it came to.
        [Fact]
        public void ace_sees_the_engine_s_update_and_delete_on_the_row_a_double_key_names_and_not_on_its_neighbour()
        {
            var path = AceMade("CREATE TABLE T (K DOUBLE CONSTRAINT pk PRIMARY KEY, Name TEXT(20))");
            var next = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1.0) + 1);
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                table.Insert(new Row { ["K"] = 1.0, ["Name"] = "one" });
                table.Insert(new Row { ["K"] = next, ["Name"] = "next" });
                table.Insert(new Row { ["K"] = 0.0, ["Name"] = "zero" });
                table.Insert(new Row { ["K"] = -0.0, ["Name"] = "minus zero" });
                table.Insert(new Row { ["K"] = 2.0, ["Name"] = "two" });
                table.Insert(new Row { ["K"] = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(2.0) + 1), ["Name"] = "after two" });

                table.UpdateByPrimaryKey(next, new Row { ["Name"] = "next changed" });
                table.UpdateByPrimaryKey(-0.0, new Row { ["Name"] = "minus zero changed" });
                table.DeleteRow("K", BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(2.0) + 1));
            }

            foreach (var file in new[] { path, Compact(path) })
            {
                Assert.Equal(5, Convert.ToInt32(Scalar(file, "SELECT COUNT(*) FROM T")));
                Assert.Equal("one", Scalar(file, "SELECT Name FROM T WHERE K = 1"));
                Assert.Equal("next changed", Scalar(file, "SELECT Name FROM T WHERE K > 1 AND K < 1.5"));
                Assert.Equal("two", Scalar(file, "SELECT Name FROM T WHERE K >= 2"));
                Assert.Equal(new[] { "minus zero changed", "zero" },
                    AceRows(file, "SELECT Name FROM T WHERE K > -1 AND K < 1").Select(v => (string)v["Name"]).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            }
            AssertEngineReadsWhatAceReads(path, "T");
        }

        // ── Yes/No columns ───────────────────────────────────────────────────

        // Access gives a Yes/No column no room among the fixed-length values: its value is its bit in the null mask.
        // The engine gave it a byte, so every fixed-length value after it was written one byte along: ACE read A = 9
        // as 2304 and could not read the date at all.
        [Fact]
        public void ace_reads_the_rows_the_engine_writes_to_a_table_with_yes_no_columns_before_other_columns()
        {
            var path = AceMade("CREATE TABLE T (Id LONG CONSTRAINT pk PRIMARY KEY, F YESNO, A LONG, D DATETIME, B YESNO, X TEXT(10), G YESNO, N DOUBLE)",
                               "INSERT INTO T (Id, F, A, D, B, X, G, N) VALUES (1, True, 7, #2020-01-02 03:04:05#, False, 'ace', True, 1.5)");
            var t = new Subject(path, "T", "Id");

            t.EngineInserts(
                V("Id", 2, "F", false, "A", 9, "D", new DateTime(2021, 3, 4), "B", true, "X", "y", "G", false, "N", 2.5),
                V("Id", 3, "F", true, "A", null, "D", null, "B", false, "X", null, "G", true, "N", null),
                V("Id", 4, "F", true, "A", int.MinValue, "D", new DateTime(1999, 12, 31, 23, 59, 59), "B", true, "X", "0123456789", "G", true, "N", -0.125));
            t.Check("after the engine inserted");

            t.EngineUpdates(1, V("A", 70, "F", false));
            t.EngineUpdates(2, V("B", false, "G", true, "D", new DateTime(2022, 5, 6, 7, 8, 9)));
            t.EngineDeletes(3);
            t.EngineInserts(V("Id", 5, "F", false, "A", 5, "D", new DateTime(2005, 5, 5), "B", false, "X", "five", "G", false, "N", 5.0));
            t.Check("after the engine updated and deleted");

            t.AceUpdates(2, V("A", 99, "F", true));
            t.AceDeletes(4);
            t.AceInserts(V("Id", 6, "F", true, "A", 6, "D", new DateTime(2006, 6, 6), "B", true, "X", "six", "G", false, "N", 6.0));
            t.Check("after ACE changed the engine's rows");
        }

        [Theory]
        [InlineData("a YESNO")]
        [InlineData("a YESNO, b YESNO, c YESNO, d YESNO, e YESNO, f YESNO, g YESNO, h YESNO, i YESNO")]
        [InlineData("a BYTE")]
        [InlineData("a YESNO, b BYTE")]
        [InlineData("b BYTE, a YESNO")]
        [InlineData("a YESNO, t TEXT(5)")]
        [InlineData("a YESNO, n LONG, b YESNO, s SHORT")]
        public void the_engine_writes_a_row_of_fixed_length_values_byte_for_byte_as_ace_writes_it(string columns)
        {
            // Two tables of the same columns, one row each of the same values: ACE's, and the engine's. A table with
            // no variable-length column has at least two bytes of fixed-length values, which Access writes for a
            // table of a single Yes/No or Byte column too.
            var path = AceMade("CREATE TABLE ByAce (" + columns + ")", "CREATE TABLE ByEngine (" + columns + ")",
                               "INSERT INTO ByAce (a) VALUES (1)");
            using (var db = Database.Open(path))
                db.GetTable("ByEngine").Insert(new Row { ["a"] = columns.Contains("a BYTE") ? (object)(byte)1 : true });

            // The bytes are ACE 16's. ACE 12 writes a row that has a Yes/No and no variable-length column with 28 00
            // where ACE 16 writes 00 00, after the column count (#101), so under ACE 12 that row's bytes are not
            // compared; ACE still reads the engine's row as its own.
            if (AceVersion == 12 && columns.Contains("YESNO") && !columns.Contains("TEXT"))
                TestContext.Current.TestOutputHelper?.WriteLine("ACE 12 writes this row otherwise than ACE 16, whose bytes are the engine's: the bytes are not compared.");
            else
                Assert.Equal(BitConverter.ToString(OnlyRow(path, "ByAce")), BitConverter.ToString(OnlyRow(path, "ByEngine")));
            AssertSame(Written(AceTable(path, "ByAce")), Written(AceTable(path, "ByEngine")), "ACE's reading of the engine's row against its own");
        }

        /// <summary>The bytes of the one row of <paramref name="table"/>, from its data page.</summary>
        private static byte[] OnlyRow(string path, string table)
        {
            int definition;
            using (var db = Database.Open(path))
                definition = TdefPage(db.GetTable(table));
            var file = File.ReadAllBytes(path);
            for (var page = 0; page < file.Length / 4096; page++)
            {
                var at = page * 4096;
                if (file[at] != 1 || BitConverter.ToInt32(file, at + 4) != definition || BitConverter.ToInt16(file, at + 12) != 1) continue;
                var start = BitConverter.ToUInt16(file, at + 14) & 0x1FFF;
                if (start < 4000) continue;   // a page Access freed, with what it held before
                return file.Skip(at + start).Take(4096 - start).ToArray();
            }
            throw new InvalidOperationException("No page of " + table + " holds exactly one row.");
        }

        private static int TdefPage(Table table)
        {
            var definition = typeof(Table).GetProperty("Definition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(table);
            return (int)definition.GetType().GetProperty("TdefPageNumber").GetValue(definition);
        }

        /// <summary>Where a row keeps the column among its fixed-length values, which the engine keeps to itself.</summary>
        private static short FixedDataOffset(Column column)
        {
            return (short)typeof(Column).GetProperty("FixedDataOffset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(column);
        }

        // ── Deleted columns ──────────────────────────────────────────────────

        // Access leaves a deleted column's number, its room among the fixed-length values and its place among the
        // variable-length ones behind, and gives a column added later the room. The engine numbered and packed the
        // columns that were left, so a row it wrote read back with its values in one another's places.
        [Fact]
        public void ace_reads_the_rows_the_engine_writes_to_a_table_that_has_had_columns_deleted()
        {
            var path = AceMade(
                "CREATE TABLE D (c0 LONG, Id LONG CONSTRAINT pk PRIMARY KEY, t1 TEXT(20), mid DOUBLE, t2 TEXT(20), b SHORT, flag YESNO, m MEMO, cz LONG, t3 TEXT(10))",
                "INSERT INTO D VALUES (100, 1, 'tee one', 3.5, 'tee two', 4, True, 'memo one', 5, 'tee3')",
                "ALTER TABLE D DROP COLUMN c0",      // the first
                "ALTER TABLE D DROP COLUMN mid",     // one in the middle
                "ALTER TABLE D DROP COLUMN t2",      // a variable-length one in the middle
                "ALTER TABLE D DROP COLUMN t3",      // the last
                "INSERT INTO D (Id, t1, b, flag, m, cz) VALUES (2, 'after', 14, False, 'memo two', 15)",
                "ALTER TABLE D ADD COLUMN added LONG",
                "ALTER TABLE D ADD COLUMN addedt TEXT(10)",
                "INSERT INTO D (Id, t1, b, flag, m, cz, added, addedt) VALUES (3, 'later', 24, True, 'memo three', 25, 26, 'x')");
            var d = new Subject(path, "D", "Id");

            d.EngineInserts(
                V("Id", 4, "t1", "engine", "b", (short)44, "flag", true, "m", "memo four", "cz", 45, "added", 46, "addedt", "e"),
                V("Id", 5, "flag", false),
                V("Id", 6, "t1", null, "b", (short)-1, "flag", false, "m", new string('m', 3000), "cz", int.MaxValue, "added", int.MinValue, "addedt", "0123456789"));
            d.Check("after the engine inserted");

            d.EngineUpdates(1, V("added", 11, "addedt", "now set"));   // a row written before the columns were added
            d.EngineUpdates(2, V("cz", 150, "flag", true));
            d.EngineUpdates(4, V("m", null, "t1", "changed"));
            d.EngineDeletes(3, 5);
            d.EngineInserts(V("Id", 7, "t1", "seven", "b", (short)7, "flag", true, "m", "memo seven", "cz", 7, "added", 7, "addedt", "seven"));
            d.Check("after the engine updated and deleted");

            Execute(path, "ALTER TABLE D DROP COLUMN b", "ALTER TABLE D ADD COLUMN extra DOUBLE");   // and Access goes on changing the table
            d = new Subject(path, "D", "Id");
            d.EngineInserts(V("Id", 8, "t1", "eight", "flag", false, "m", "memo eight", "cz", 8, "added", 8, "addedt", "eight", "extra", 8.5));
            d.EngineUpdates(7, V("extra", 7.5));
            d.AceUpdates(8, V("cz", 80));
            d.AceInserts(V("Id", 9, "t1", "nine", "flag", true, "m", "memo nine", "cz", 9, "added", 9, "addedt", "nine", "extra", 9.5));
            d.Check("after Access deleted and added columns again");
        }

        // ── Decimal, Replication ID, and every type together ─────────────────

        private static readonly Guid G1 = new Guid("01234567-89ab-cdef-0123-456789abcdef");
        private static readonly Guid G2 = new Guid("fedcba98-7654-3210-fedc-ba9876543210");

        private const string EveryType =
            "Id LONG CONSTRAINT pk PRIMARY KEY, Flag1 YESNO, Bte BYTE, Sml SHORT, Cur CURRENCY, Sng SINGLE, Flag2 YESNO, Dbl DOUBLE, Dte DATETIME, "
            + "Gid GUID, N28 DECIMAL(28,0), N184 DECIMAL(18,4), N2828 DECIMAL(28,28), N52 DECIMAL(5,2), Txt TEXT(50), Mem MEMO, Ole LONGBINARY, Vbin VARBINARY(20), "
            + "Chr CHAR(6), Fbin BINARY(4), Lng LONG";

        private static Values Full(int id, int seed)
        {
            return V("Id", id, "Flag1", seed % 2 == 0, "Bte", (byte)(seed * 37 % 256), "Sml", (short)(seed * 1000 - 5000), "Cur", seed * 1234.5678m - 4000m,
                     "Sng", seed * 1.25f - 3f, "Flag2", seed % 3 == 0, "Dbl", seed * Math.PI - 9, "Dte", new DateTime(2000, 1, 1).AddDays(seed * 400).AddSeconds(seed * 7777),
                     "Gid", new Guid(seed, (short)(seed * 3), (short)(seed * 5), 1, 2, 3, 4, 5, 6, 7, (byte)seed),
                     "N28", seed * 1111111111111111111111111m - 3333333333333333333333333m, "N184", seed * 12345.6789m - 40000m,
                     "N2828", seed % 2 == 0 ? 0.0000000000000000000000000001m * seed : -0.1234567890123456789012345678m,
                     "N52", seed * 111.11m - 400m, "Txt", "text " + seed + " é", "Mem", "memo " + new string((char)('a' + seed), seed * 40),
                     "Ole", Enumerable.Range(0, seed * 50).Select(i => (byte)(i + seed)).ToArray(), "Vbin", new byte[] { (byte)seed, 2, 3 },
                     "Chr", ("c" + seed).PadRight(6), "Fbin", new byte[] { 1, 2, 3, (byte)seed }, "Lng", seed * 100000 - 1);
        }

        private static Values Extremes(int id, bool negative)
        {
            decimal sign = negative ? -1m : 1m;
            return V("Id", id, "Flag1", negative, "Bte", negative ? byte.MinValue : byte.MaxValue, "Sml", negative ? short.MinValue : short.MaxValue,
                     "Cur", sign * 922337203685477.5807m, "Sng", negative ? float.MinValue : float.MaxValue, "Flag2", !negative,
                     "Dbl", negative ? double.MinValue : double.MaxValue, "Dte", negative ? new DateTime(100, 1, 1) : new DateTime(9999, 12, 31, 23, 59, 59),
                     "Gid", negative ? Guid.Empty : new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                     "N28", sign * 9999999999999999999999999999m, "N184", sign * 99999999999999.9999m, "N2828", sign * 0.9999999999999999999999999999m,
                     "N52", sign * 999.99m, "Txt", new string(negative ? 'Ω' : 'z', 50), "Mem", "", "Ole", new byte[0], "Vbin", Enumerable.Repeat((byte)0xFF, 20).ToArray(),
                     "Chr", negative ? "ΩΩΩΩΩΩ" : "zzzzzz", "Fbin", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "Lng", negative ? int.MinValue : int.MaxValue);
        }

        private static Values OnlyOne(int id, Values from, string column)
        {
            // Every column null but the key, a Yes/No (which holds no null) and one other.
            var values = V("Id", id, "Flag1", false, "Flag2", false);
            values[column] = from[column];
            return values;
        }

        // SQL's GUID is kept among the variable-length values and SQL's CHAR and BINARY among the fixed-length ones;
        // a Decimal is four 32-bit words, the most significant first. The engine read a Decimal as sixteen bytes in
        // order (a stored 1 as 16,777,216), a Replication ID from the wrong place, and CHAR as the Text after it.
        private void EveryTypeIsRefereed(string path)
        {
            for (var i = 1; i <= 3; i++)
                AceInsert(path, "E", Full(i, i));
            AceInsert(path, "E", Extremes(4, negative: false));
            AceInsert(path, "E", Extremes(5, negative: true));
            AceInsert(path, "E", V("Id", 6));
            var e = new Subject(path, "E", "Id");
            Assert.Equal(Written(Full(2, 2)["N184"]), Written(e[2]["N184"]));
            Assert.Equal(Written(Full(2, 2)["Gid"]), Written(e[2]["Gid"]));

            e.EngineInserts(Full(11, 4), Full(12, 5), Full(13, 6), Extremes(14, negative: false), Extremes(15, negative: true), V("Id", 16, "Flag1", false, "Flag2", false));
            var template = Full(0, 7);
            var id = 20;
            foreach (var column in template.Keys.Where(k => k != "Id" && k != "Flag1" && k != "Flag2"))
                e.EngineInserts(OnlyOne(id++, template, column));   // a null in every column but one, for each column
            e.Check("after the engine inserted");

            var changed = Full(0, 9);
            changed.Remove("Id");
            e.EngineUpdates(1, changed);                                      // every column of a row ACE wrote
            e.EngineUpdates(2, V("N184", -0.0001m, "Gid", G1));
            e.EngineUpdates(11, V("N28", null, "Gid", null, "Chr", null, "Fbin", null, "Dte", null));   // to null
            e.EngineUpdates(16, V("N28", 1m, "Gid", G2, "Chr", "char  ", "Fbin", new byte[] { 9, 8, 7, 6 }, "N2828", 0.5m));   // from null
            e.EngineDeletes(3, 12, 20);
            e.EngineInserts(Full(17, 8));
            e.Check("after the engine updated and deleted");

            e.AceUpdates(13, V("N184", 1.5m, "Gid", G1, "Mem", new string('q', 500)));
            e.AceDeletes(14);
            e.AceInserts(Full(18, 3));
            e.Check("after ACE changed the engine's rows");
        }

        [Fact]
        public void ace_and_the_engine_read_each_other_s_rows_of_every_type_in_a_table_made_by_sql()
        {
            EveryTypeIsRefereed(AceMade("CREATE TABLE E (" + EveryType + ")"));
        }

        [Theory]
        [InlineData(128, ".accdb")]   // Access 2007
        [InlineData(256, ".accdb")]   // Access 2010 and later
        [InlineData(64, ".mdb")]      // Access 2000, the file DAO's own, not the engine's blank one
        public void ace_and_the_engine_read_each_other_s_rows_of_every_type_in_a_file_dao_made(int version, string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, version);
            Execute(path, "CREATE TABLE E (" + EveryType + ")");
            EveryTypeIsRefereed(path);
        }

        // A Replication ID as DAO makes it (Access's table designer goes through DAO) is kept among the fixed-length
        // values: the same type, the other place.
        [Fact]
        public void ace_and_the_engine_read_each_other_s_replication_ids_in_a_table_dao_made()
        {
            var path = AceMade();
            dynamic dbe = Dao.Engine;
            try
            {
                dynamic db = dbe.OpenDatabase(path);
                try
                {
                    dynamic table = db.CreateTableDef("G");
                    table.Fields.Append(table.CreateField("Id", 4));        // dbLong
                    table.Fields.Append(table.CreateField("Flag", 1));      // dbBoolean
                    table.Fields.Append(table.CreateField("Gid", 15));      // dbGUID
                    // A Text field DAO makes does not allow a zero-length string unless asked, and the engine holds a
                    // row to that as ACE does (#41): row 5 below has one.
                    dynamic text = table.CreateField("Txt", 10, 30);        // dbText
                    text.AllowZeroLength = true;
                    table.Fields.Append(text);
                    table.Fields.Append(table.CreateField("Gid2", 15));
                    db.TableDefs.Append(table);
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            Execute(path, "CREATE UNIQUE INDEX pk ON G (Id) WITH PRIMARY", "CREATE INDEX ByGuid ON G (Gid)");
            AceInsert(path, "G", V("Id", 1, "Flag", true, "Gid", G1, "Txt", "ace", "Gid2", G2));
            AceInsert(path, "G", V("Id", 2, "Flag", false));
            var g = new Subject(path, "G", "Id");

            g.EngineInserts(V("Id", 3, "Flag", true, "Gid", G2, "Txt", "engine", "Gid2", G1), V("Id", 4, "Flag", false),
                            V("Id", 5, "Flag", false, "Gid", Guid.Empty, "Txt", "", "Gid2", null));
            g.EngineUpdates(2, V("Gid", G2, "Txt", "seven"));
            g.EngineUpdates(1, V("Gid", null, "Gid2", G1));
            g.EngineDeletes(4);
            g.Check("after the engine changed it");
            AssertGuidIndexAgreesWithScan(path, "G", "Gid", G2, 2);
            AssertGuidIndexAgreesWithScan(path, "G", "Gid", G1, 0);

            g.AceUpdates(3, V("Gid", G1));
            g.AceInserts(V("Id", 6, "Flag", true, "Gid", G1, "Txt", "six", "Gid2", G2));
            g.Check("after ACE changed the engine's rows");
            AssertGuidIndexAgreesWithScan(path, "G", "Gid", G1, 2);
        }

        // The same condition answered from the index and from the rows.
        private static void AssertGuidIndexAgreesWithScan(string path, string table, string column, Guid value, int expected)
        {
            var literal = "{guid {" + value.ToString("D") + "}}";
            var throughIndex = Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM [" + table + "] WHERE [" + column + "] = " + literal));
            var throughRows = AceRows(path, "SELECT [" + column + "] FROM [" + table + "]").Count(r => Equals(r[column], value));
            Assert.True(throughIndex == expected && throughRows == expected,
                table + "." + column + " = " + value + ": " + throughIndex + " through the index, " + throughRows + " by scan, expected " + expected);
        }

        // An index on a Replication ID made by SQL was refused, because the engine could not read the column: the
        // table could not be written to at all. It is keyed now, and ACE answers through it.
        [Fact]
        public void ace_answers_through_an_index_on_a_replication_id_made_by_sql_after_the_engine_changes_the_table()
        {
            var path = AceMade("CREATE TABLE S (Id LONG CONSTRAINT pk PRIMARY KEY, Gid GUID, Txt TEXT(20))", "CREATE INDEX ByGuid ON S (Gid)",
                               "CREATE UNIQUE INDEX ByGuidAndId ON S (Gid, Id)");
            AceInsert(path, "S", V("Id", 1, "Gid", G1, "Txt", "ace"));
            AceInsert(path, "S", V("Id", 2, "Gid", G2, "Txt", "ace"));
            var s = new Subject(path, "S", "Id");

            for (var i = 10; i < 400; i++)
                s.EngineInserts(V("Id", i, "Gid", i % 3 == 0 ? (object)G1 : i % 3 == 1 ? (object)new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) : null, "Txt", "engine " + i));
            s.EngineUpdates(2, V("Gid", G1));
            s.EngineUpdates(12, V("Gid", G2));
            s.EngineDeletes(1, 15);
            s.Check("after the engine changed it");

            AssertGuidIndexAgreesWithScan(path, "S", "Gid", G1, 1 + Enumerable.Range(10, 390).Count(i => i % 3 == 0 && i != 12 && i != 15));
            AssertGuidIndexAgreesWithScan(path, "S", "Gid", G2, 1);
            AssertGuidIndexAgreesWithScan(path, "S", "Gid", new Guid(100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), 1);
            s.AceInserts(V("Id", 500, "Gid", G2, "Txt", "ace"));
            s.AceUpdates(100, V("Gid", G2));
            AssertGuidIndexAgreesWithScan(path, "S", "Gid", G2, 3);
            s.Check("after ACE added to the engine's index");
        }

        // ── Tables the engine creates ────────────────────────────────────────

        // A table the engine creates gives a Yes/No column a byte among the fixed-length values, which Access does
        // not. Access reads and writes rows by the offsets the definition gives, so it reads these and writes its own
        // rows the same way: the layout is unusual, not wrong, and tables made by earlier releases have it.
        [Fact]
        public void ace_reads_and_adds_to_a_table_the_engine_created_with_yes_no_columns_before_other_columns()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("M", new[]
                {
                    new ColumnDefinition("F", DataType.Boolean), new ColumnDefinition("Id", DataType.Int32),
                    new ColumnDefinition("B", DataType.Boolean), new ColumnDefinition("D", DataType.DateTime),
                    new ColumnDefinition("X", DataType.Text) { MaxLength = 10 }, new ColumnDefinition("G", DataType.Boolean),
                    new ColumnDefinition("N", DataType.Decimal) { Precision = 18, Scale = 4 }, new ColumnDefinition("U", DataType.Guid),
                }, primaryKey: "Id");
                // The definition as this library has always written it: a byte for each Yes/No column.
                Assert.Equal(new[] { "F@0", "Id@1", "B@5", "D@6", "G@14", "N@15", "U@32" },
                             table.Columns.Where(c => !c.IsVariableLength).Select(c => c.Name + "@" + FixedDataOffset(c)).ToArray());
                table.Insert(EngineRow(V("F", true, "Id", 1, "B", false, "D", new DateTime(2021, 3, 4, 5, 6, 7), "X", "one", "G", true, "N", 123.4567m, "U", G1)));
            }
            var m = new Subject(path, "M", "Id");
            Assert.Equal(Written(new[] { V("B", false, "D", new DateTime(2021, 3, 4, 5, 6, 7), "F", true, "G", true, "Id", 1, "N", 123.4567m, "U", G1, "X", "one") }).Single(),
                         Written(new[] { m[1] }).Single());

            m.AceInserts(V("F", false, "Id", 2, "B", true, "D", new DateTime(2022, 2, 2), "X", "two", "G", false, "N", -2.5m, "U", G2));
            m.EngineInserts(V("F", true, "Id", 3, "B", true, "D", null, "X", null, "G", true, "N", null, "U", null));
            m.AceUpdates(1, V("B", true, "N", 0.0001m));
            m.EngineUpdates(2, V("F", true, "D", new DateTime(2023, 3, 3)));
            m.Check("after both changed it");
        }

        // ── Rows earlier releases deleted ────────────────────────────────────

        // The engine marks a deleted row with both of a slot's flag bits (0xC000), as Access and Java Jackcess do.
        // Releases before this set the first alone (0x8000), which is also what a moved row's values carry, and
        // files they deleted from are in the field. Such a slot is reached only through a pointer, and nothing
        // points at a deleted row: the engine and ACE both pass it over, and ACE goes on using the table.
        [Fact]
        public void rows_an_earlier_release_deleted_stay_deleted_for_the_engine_and_for_ace()
        {
            var path = AceMade("CREATE TABLE Old (Id LONG CONSTRAINT pk PRIMARY KEY, Name TEXT(20), Notes MEMO)", "CREATE INDEX ByName ON Old (Name)");
            for (var id = 1; id <= 8; id++)
                AceInsert(path, "Old", V("Id", id, "Name", "row " + id, "Notes", new string((char)('a' + id), id % 2 == 0 ? 3000 : 300)));
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Old");
                table.DeleteRow("Id", 2);
                table.DeleteRow("Id", 5);
                table.UpdateByPrimaryKey(7, new Row { ["Name"] = "seven" });   // an update leaves the row in its slot
            }

            // The marking as it was: each slot the engine marked deleted, in the table's pages and its long values'.
            int definition;
            using (var db = Database.Open(path))
                definition = TdefPage(db.GetTable("Old"));
            var file = File.ReadAllBytes(path);
            var remarked = 0;
            for (var at = 0; at + 4096 <= file.Length; at += 4096)
            {
                if (file[at] != 1 || BitConverter.ToInt32(file, at + 4) != definition) continue;
                for (var row = 0; row < BitConverter.ToInt16(file, at + 12); row++)
                {
                    var slot = BitConverter.ToUInt16(file, at + 14 + row * 2);
                    if ((slot & 0xC000) != 0xC000) continue;
                    file[at + 14 + row * 2 + 1] &= 0xBF;   // 0xC000 -> 0x8000
                    remarked++;
                }
            }
            Assert.Equal(2, remarked);
            File.WriteAllBytes(path, file);

            var old = new Subject(path, "Old", "Id");                       // ACE and the engine read the same rows
            Assert.Equal(new long[] { 1, 3, 4, 6, 7, 8 }, old.Ids);
            Assert.Equal(0, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM Old WHERE Name = 'row 2'")));
            Assert.Equal(1, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM Old WHERE Name = 'seven'")));

            old.AceInserts(V("Id", 2, "Name", "two again", "Notes", new string('z', 3000)));
            old.AceUpdates(4, V("Notes", new string('y', 5000)));
            old.AceDeletes(1);
            old.EngineInserts(V("Id", 9, "Name", "nine", "Notes", new string('n', 3000)));
            old.EngineDeletes(3);
            old.Check("after ACE and the engine went on with it");
        }

        // ── Long text keys ───────────────────────────────────────────────────

        // A text key longer than 510 bytes is cut, as Access cuts it. Before the engine cut keys, a row whose key
        // was longer was not found through the index, and deleting it left its entry behind: ACE went on counting
        // the row. ACE's own WHERE through a cut key does not always answer as its scan does, so what ACE finds
        // in the engine's file is compared with what it finds in a twin file where ACE made the same changes.
        [Fact]
        public void the_engine_finds_and_deletes_rows_with_long_text_keys_as_ace_does()
        {
            Func<char, int, string> value = (c, length) => length.ToString("D3") + new string(c, length - 3);
            var values = new List<KeyValuePair<int, string>>();
            var kind = 0;
            foreach (var c in new[] { '\u03B0', '\u03A9', 'x' })
            {
                kind++;
                foreach (var length in new[] { 150, 200, 255 })
                    values.Add(new KeyValuePair<int, string>(kind * 1000 + length, value(c, length)));
            }

            var paths = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                var path = AceMade("CREATE TABLE K (Id LONG CONSTRAINT pk PRIMARY KEY, T TEXT(255))", "CREATE INDEX ByT ON K (T)");
                foreach (var v in values)
                    AceInsert(path, "K", V("Id", v.Key, "T", v.Value));
                paths.Add(path);
            }
            string engines = paths[0], aces = paths[1];

            using (var db = Database.Open(engines))
            {
                var table = db.GetTable("K");
                foreach (var v in values)
                {
                    var found = table.CreateIndexCursor("ByT").FindRow("T", v.Value);
                    Assert.True(found != null && Equals(found["Id"], v.Key), "the engine did not find the row whose key is " + v.Value.Length + " of U+" + ((int)v.Value[3]).ToString("X4"));
                }
            }

            // The 200- and 255-character rows of each kind go: by the engine in one file, by ACE in the other.
            var deleted = values.Where(v => v.Key % 1000 != 150).ToList();
            using (var db = Database.Open(engines))
                foreach (var v in deleted)
                    db.GetTable("K").DeleteRow("Id", v.Key);
            foreach (var v in deleted)
                Execute(aces, "DELETE FROM K WHERE Id = " + v.Key);

            foreach (var v in values)
            {
                var gone = deleted.Contains(v);
                var what = v.Value.Length + " of U+" + ((int)v.Value[3]).ToString("X4");
                var throughIndex = Convert.ToInt32(Scalar(engines, "SELECT COUNT(*) FROM K WHERE T = '" + v.Value + "'"));
                var twin = Convert.ToInt32(Scalar(aces, "SELECT COUNT(*) FROM K WHERE T = '" + v.Value + "'"));
                var byScan = AceRows(engines, "SELECT T FROM K").Count(r => Equals(r["T"], v.Value));
                Assert.True(throughIndex == twin, what + ": ACE counts " + throughIndex + " through the index the engine kept and " + twin + " through its own");
                Assert.True(byScan == (gone ? 0 : 1), what + ": " + byScan + " rows by scan");
            }
            AssertSame(Written(AceTable(aces, "K")), Written(AceTable(engines, "K")), "the engine's file against ACE's twin");
            var compacted = Compact(engines);
            AssertSame(Written(AceTable(aces, "K")), Written(AceTable(compacted, "K")), "the engine's file, compacted, against ACE's twin");
            using (var db = Database.Open(engines))
            {
                var table = db.GetTable("K");
                foreach (var v in values)
                    Assert.Equal(deleted.Contains(v), table.CreateIndexCursor("ByT").FindRow("T", v.Value) == null);
            }
        }
    }
}
