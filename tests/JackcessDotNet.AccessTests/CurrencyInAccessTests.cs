using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
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
    /// The Currency the managed engine makes of a caller's number is the one Access makes of it
    /// (#65): its exact value taken to four places, half to even. ACE writes the same numbers
    /// beside the engine, with parameters of their own types, and has to read the engine's rows as its own, find each
    /// through the index the engine wrote by the number it was written with - <c>=</c>, <c>BETWEEN</c> and the first row
    /// <c>&gt;=</c> it, as it finds its own - and read and find them again once DAO has compacted the file. The engine,
    /// the other way, finds the rows ACE wrote by the same numbers, through ACE's index, in a range and as the closest
    /// row. What a write refuses, ACE refuses too, but for NaN, which ACE writes as the least Currency. Text and a Yes/No
    /// both take as numbers, the engine as it takes them for every number column: True as -1, as ACE writes it (#75).
    /// </summary>
    public class CurrencyInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        public static IEnumerable<object[]> Formats()
        {
            yield return new object[] { 64, ".mdb" };
            yield return new object[] { 128, ".accdb" };
        }

        private string NewDatabase(int daoVersion, string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, daoVersion);
            return path;
        }

        private static double NextUp(double d)
        {
            if (d == 0) return double.Epsilon;
            var bits = BitConverter.DoubleToInt64Bits(d);
            return BitConverter.Int64BitsToDouble(d > 0 ? bits + 1 : bits - 1);
        }

        private static double NextDown(double d) => -NextUp(-d);

        /// <summary>
        /// Numbers that tell one rounding from another: halves of the fifth place, as exact doubles, singles and
        /// decimals, in both signs; doubles just either side of a half, and written as one but not one; doubles of
        /// more significant digits than 15; the ends of a Currency's range; and whole numbers.
        /// </summary>
        private static readonly object[] Numbers =
        {
            0.03125, -0.03125, 0.09375, -0.09375, 1.03125, 0.15625, NextDown(0.03125), NextUp(0.03125), NextDown(-1.03125),
            NextUp(-1.03125), 0.00015, 0.00025, 1.23465, 1.234549999999999, 1.23455, 562949953421312.5, -562949953421312.5,
            900719925474.0992, 922337203685477.5, -922337203685477.5, 35184372088832.0078125, 0.1, 0.0, -0.0,
            0.03125f, -0.03125f, 0.09375f, 0.00015f, 1.23465f, 131072.015625f,
            0.03125m, -0.03125m, 0.09375m, 1.23465m, -1.23465m, 0.00015m, 0.00025m, 1.23455m, 0.000150000000000000000000001m,
            0.000149999999999999999999999m, 922337203685477.5807m, -922337203685477.5808m, 922337203685477.58065m,
            -922337203685477.58075m, 12345678901234.56785m, 1.5m, 7, -5,
        };

        private static OleDbParameter Parameter(object value)
        {
            if (value is double) return new OleDbParameter("p", OleDbType.Double) { Value = value };
            if (value is float) return new OleDbParameter("p", OleDbType.Single) { Value = value };
            if (value is int) return new OleDbParameter("p", OleDbType.Integer) { Value = value };
            if (value is decimal)
                return new OleDbParameter("p", OleDbType.Decimal)
                {
                    Value = value, Precision = 28, Scale = (byte)((decimal.GetBits((decimal)value)[3] >> 16) & 0xFF),
                };
            if (value is string) return new OleDbParameter("p", OleDbType.VarWChar) { Value = value };
            if (value is bool) return new OleDbParameter("p", OleDbType.Boolean) { Value = value };
            throw new ArgumentException("No parameter type for " + value.GetType().Name);
        }

        private static string Show(object value)
        {
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture) + " (double)";
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture) + " (float)";
            return Convert.ToString(value, CultureInfo.InvariantCulture) + " (" + value.GetType().Name + ")";
        }

        private static string Join<T>(IEnumerable<T> values) => "[" + string.Join(", ", values) + "]";

        // What ACE answers, through the connection given: the Ids of the rows, or the first row's C.
        private static List<int> AceIds(OleDbConnection connection, string sql, params object[] parameters)
        {
            using (var command = new OleDbCommand(sql, connection))
            {
                foreach (var parameter in parameters) command.Parameters.Add(Parameter(parameter));
                var ids = new List<int>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) ids.Add(reader.GetInt32(0));
                ids.Sort();
                return ids;
            }
        }

        private static decimal? AceFirst(OleDbConnection connection, string sql, params object[] parameters)
        {
            using (var command = new OleDbCommand(sql, connection))
            {
                foreach (var parameter in parameters) command.Parameters.Add(Parameter(parameter));
                var found = command.ExecuteScalar();
                return found == null || found is DBNull ? (decimal?)null : (decimal)found;
            }
        }

        // ACE inserts each number in a row of its own, with a parameter of its type: Id is its place in the list, after
        // firstId.
        private static void AceWrites(string path, string table, IList<object> numbers, int firstId = 0)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 0; i < numbers.Count; i++)
                    using (var command = new OleDbCommand("INSERT INTO [" + table + "] (Id, C) VALUES (?, ?)", connection))
                    {
                        command.Parameters.Add(Parameter(firstId + i));
                        command.Parameters.Add(Parameter(numbers[i]));
                        command.ExecuteNonQuery();
                    }
            }
        }

        private static Dictionary<int, decimal> AceCurrencies(string path, string table)
            => AceRows(path, "SELECT Id, C FROM [" + table + "]").ToDictionary(r => Convert.ToInt32(r["Id"]), r => (decimal)r["C"]);

        private static void CreateKeyed(Database db, string table, string index)
            => db.CreateTable(table, new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("C", DataType.Currency) },
                              new[] { "Id" }, new[] { new IndexDefinition(index, "C") });

        // ACE's answers, through the index on C of each table, for every number: the rows of =, of BETWEEN it and
        // itself, and the first row at or after it.
        private static void AssertAceFindsAlike(string path, string engineTable, string aceTable, string when)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var number in Numbers)
                {
                    var what = Show(number) + " " + when;
                    var mine = AceIds(connection, "SELECT Id FROM [" + engineTable + "] WHERE C = ?", number);
                    var its = AceIds(connection, "SELECT Id FROM [" + aceTable + "] WHERE C = ?", number);
                    Assert.True(its.SequenceEqual(mine), "C = " + what + ": ACE finds " + Join(mine) + " in the engine's rows and " + Join(its) + " in its own");
                    Assert.Contains(Array.IndexOf(Numbers, number), mine);
                    mine = AceIds(connection, "SELECT Id FROM [" + engineTable + "] WHERE C BETWEEN ? AND ?", number, number);
                    its = AceIds(connection, "SELECT Id FROM [" + aceTable + "] WHERE C BETWEEN ? AND ?", number, number);
                    Assert.True(its.SequenceEqual(mine), "C BETWEEN " + what + ": ACE finds " + Join(mine) + " in the engine's rows and " + Join(its) + " in its own");
                    var first = AceFirst(connection, "SELECT TOP 1 C FROM [" + engineTable + "] WHERE C >= ? ORDER BY C", number);
                    var itsFirst = AceFirst(connection, "SELECT TOP 1 C FROM [" + aceTable + "] WHERE C >= ? ORDER BY C", number);
                    Assert.True(itsFirst == first, "C >= " + what + ": ACE's first row is " + first + " in the engine's rows and " + itsFirst + " in its own");
                }
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void ace_reads_finds_and_compacts_the_currency_the_engine_wrote_for_each_number(int daoVersion, string extension)
        {
            var path = NewDatabase(daoVersion, extension);
            using (var db = Database.Open(path))
            {
                CreateKeyed(db, "E", "ByC");
                var table = db.GetTable("E");
                for (var i = 0; i < Numbers.Length; i++) table.Insert(new Row { ["Id"] = i, ["C"] = Numbers[i] });
            }
            Execute(path, "CREATE TABLE A (Id LONG CONSTRAINT pkA PRIMARY KEY, C CURRENCY)", "CREATE INDEX ByCA ON A (C)");
            AceWrites(path, "A", Numbers);

            // The engine wrote each number as ACE writes it, and ACE finds every row by it through the index the engine
            // wrote, as it finds its own.
            var engine = AceCurrencies(path, "E");
            var ace = AceCurrencies(path, "A");
            for (var i = 0; i < Numbers.Length; i++)
                Assert.True(ace[i] == engine[i], Show(Numbers[i]) + ": the engine wrote " + engine[i] + ", ACE " + ace[i]);
            AssertAceFindsAlike(path, "E", "A", "in the file the engine wrote");

            // What the cases are: a double by its exact value and a decimal by its digits, half to even.
            Assert.Equal(0.0312m, ace[Array.IndexOf(Numbers, 0.03125)]);
            Assert.Equal(0.0001m, ace[Array.IndexOf(Numbers, 0.00015)]);
            Assert.Equal(562949953421312.5m, ace[Array.IndexOf(Numbers, 562949953421312.5)]);
            Assert.Equal(1.2346m, ace[Array.IndexOf(Numbers, 1.23465m)]);
            Assert.Equal(0.0002m, ace[Array.IndexOf(Numbers, 0.00015m)]);

            var compacted = Compact(path);
            try
            {
                Assert.Equal(engine.OrderBy(p => p.Key), AceCurrencies(compacted, "E").OrderBy(p => p.Key));
                AssertAceFindsAlike(compacted, "E", "A", "after DAO compacted the file");
            }
            finally { File.Delete(compacted); }

            // ACE adds the same numbers to the engine's table, through the index the engine wrote: then ACE and the
            // engine each find, by every number, the engine's row and ACE's.
            AceWrites(path, "E", Numbers, firstId: 1000);
            // Opened only to be read, so that ACE opens the file beside it (#62).
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                connection.Open();
                var table = db.GetTable("E");
                foreach (var number in Numbers)
                {
                    var own = AceIds(connection, "SELECT Id FROM A WHERE C = ?", number);
                    var both = own.Concat(own.Select(id => id + 1000)).ToList();
                    Assert.Equal(both, AceIds(connection, "SELECT Id FROM E WHERE C = ?", number));
                    Assert.Equal(both, table.CreateIndexCursor("ByC").FindRowsByKey(number).Select(r => (int)r["Id"]).OrderBy(id => id).ToList());
                }
            }
            var again = Compact(path);
            try { Assert.Equal(2 * Numbers.Length, AceCurrencies(again, "E").Count); }
            finally { File.Delete(again); }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void the_engine_finds_the_rows_ace_wrote_by_the_numbers_ace_wrote_them_with(int daoVersion, string extension)
        {
            var path = NewDatabase(daoVersion, extension);
            Execute(path, "CREATE TABLE A (Id LONG CONSTRAINT pkA PRIMARY KEY, C CURRENCY)", "CREATE INDEX ByCA ON A (C)");
            AceWrites(path, "A", Numbers);

            // Opened only to be read, so that ACE opens the file beside it (#62).
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                connection.Open();
                var table = db.GetTable("A");
                foreach (var number in Numbers)
                {
                    var what = Show(number);
                    var ace = AceIds(connection, "SELECT Id FROM A WHERE C = ?", number);
                    var byIndex = table.CreateIndexCursor("ByCA").FindRowsByKey(number).Select(r => (int)r["Id"]).OrderBy(id => id).ToList();
                    Assert.True(ace.SequenceEqual(byIndex), "C = " + what + ": ACE " + Join(ace) + ", the engine through ACE's index " + Join(byIndex));
                    var scan = new List<int>();
                    var cursor = table.CreateCursor();
                    for (var row = cursor.FindRow("C", number); row != null; row = cursor.FindNextRow("C", number)) scan.Add((int)row["Id"]);
                    scan.Sort();
                    Assert.True(ace.SequenceEqual(scan), "C = " + what + ": ACE " + Join(ace) + ", the engine by a scan " + Join(scan));

                    var between = AceIds(connection, "SELECT Id FROM A WHERE C BETWEEN ? AND ?", number, number);
                    var range = table.CreateIndexCursor("ByCA", new IndexRange { StartKey = new[] { number }, EndKey = new[] { number } })
                        .Select(r => (int)r["Id"]).OrderBy(id => id).ToList();
                    Assert.True(between.SequenceEqual(range), "C BETWEEN " + what + ": ACE " + Join(between) + ", the engine's range " + Join(range));

                    var first = AceFirst(connection, "SELECT TOP 1 C FROM A WHERE C >= ? ORDER BY C", number);
                    var closest = table.CreateIndexCursor("ByCA").FindClosestRowByKey(number);
                    Assert.True(first == (closest == null ? (decimal?)null : (decimal)closest["C"]),
                        "C >= " + what + ": ACE's first row " + first + ", the engine's closest " + (closest == null ? "none" : closest["C"]));
                }
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void a_range_of_the_engine_s_has_the_rows_ace_s_comparisons_have_through_the_index(int daoVersion, string extension)
        {
            // Every four-place value around each number, so that a bound rounded one way or the other gives other rows.
            var held = new SortedSet<decimal> { -922337203685477.5808m, 922337203685477.5807m };
            foreach (var number in Numbers)
            {
                // A double or a single by its round-trip text, which keeps its digits past the fifteenth.
                var near = Math.Round(number is double || number is float
                    ? decimal.Parse(((IFormattable)number).ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture)
                    : Convert.ToDecimal(number, CultureInfo.InvariantCulture), 4);
                for (var step = -2; step <= 2; step++)
                {
                    var value = near + step * 0.0001m;
                    if (value >= -922337203685477.5808m && value <= 922337203685477.5807m) held.Add(value);
                }
            }
            var path = NewDatabase(daoVersion, extension);
            using (var db = Database.Open(path))
            {
                CreateKeyed(db, "E", "ByC");
                var table = db.GetTable("E");
                var id = 0;
                foreach (var value in held) table.Insert(new Row { ["Id"] = id++, ["C"] = value });
            }

            // Opened only to be read, so that ACE opens the file beside it (#62).
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                connection.Open();
                var table = db.GetTable("E");
                Func<IndexRange, List<int>> engine = range => table.CreateIndexCursor("ByC", range).Select(r => (int)r["Id"]).OrderBy(i => i).ToList();
                Action<string, List<int>, List<int>> same = (what, ace, mine) =>
                    Assert.True(ace.SequenceEqual(mine), what + ": ACE " + Join(ace) + ", the engine's range " + Join(mine));
                foreach (var bound in Numbers)
                {
                    var what = Show(bound);
                    var b = new[] { bound };
                    same("C >= " + what, AceIds(connection, "SELECT Id FROM E WHERE C >= ?", bound), engine(new IndexRange { StartKey = b }));
                    same("C > " + what, AceIds(connection, "SELECT Id FROM E WHERE C > ?", bound), engine(new IndexRange { StartKey = b, IsStartInclusive = false }));
                    same("C <= " + what, AceIds(connection, "SELECT Id FROM E WHERE C <= ?", bound), engine(new IndexRange { EndKey = b }));
                    same("C < " + what, AceIds(connection, "SELECT Id FROM E WHERE C < ?", bound), engine(new IndexRange { EndKey = b, IsEndInclusive = false }));
                    // (Not BETWEEN, which takes its bounds in either order: an index range runs from its start to its end.)
                    same("C >= 0.03125 AND C <= " + what, AceIds(connection, "SELECT Id FROM E WHERE C >= ? AND C <= ?", 0.03125, bound),
                        engine(new IndexRange { StartKey = new object[] { 0.03125 }, EndKey = b }));
                }
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void what_the_engine_refuses_to_write_is_refused_by_ace_too_but_nan(int daoVersion, string extension)
        {
            // What ACE does with NaN for a Currency was measured with ACE 16 in 64 bits (Microsoft 365's and the
            // redistributable's), which writes it, and in 32 bits with ACE 12 and with the redistributable's ACE 16, build
            // 16.0.5011.1000, which refuse it (#101). Any other 32-bit build of ACE 16, and any other version, is not pinned.
            var measured = Environment.Is64BitProcess ? AceVersion == 16 : AceVersion == 12 || AceBuild == "16.0.5011.1000";
            if (!measured)
                Assert.Skip("What " + (Environment.Is64BitProcess ? "64" : "32") + "-bit ACE " + AceBuild + " does with NaN for a Currency has not been measured: 64-bit ACE 16 writes it, 32-bit ACE 12 and 16.0.5011.1000 refuse it.");
            var aceWritesNaN = Environment.Is64BitProcess;
            var path = NewDatabase(daoVersion, extension);
            using (var db = Database.Open(path))
            {
                CreateKeyed(db, "E", "ByC");
                db.GetTable("E").Insert(new Row { ["Id"] = 0, ["C"] = 1.5m });
            }
            Execute(path, "CREATE TABLE A (Id LONG CONSTRAINT pkA PRIMARY KEY, C CURRENCY)", "CREATE INDEX ByCA ON A (C)");
            var before = File.ReadAllBytes(path);

            // Past the range once taken to four places, or no number: ACE says "Overflow", and the engine writes nothing.
            var past = new object[]
            {
                922337203685477.625, -922337203685477.625, 1e20, double.PositiveInfinity, double.NegativeInfinity, float.PositiveInfinity,
                922337203685477.5808m, -922337203685477.5809m, 922337203685477.58075m,
            };
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("E");
                foreach (var number in past)
                    Assert.Throws<OverflowException>(() => table.Insert(new Row { ["Id"] = 1, ["C"] = number }));
                // NaN the engine refuses as no Currency.
                Assert.Throws<OverflowException>(() => table.Insert(new Row { ["Id"] = 1, ["C"] = double.NaN }));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 0; i < past.Length; i++)
                {
                    var e = Record.Exception(() => AceWrites(path, "A", new[] { past[i] }));
                    Assert.True(e is OleDbException, Show(past[i]) + ": ACE " + (e == null ? "wrote it" : e.GetType().Name + " " + e.Message));
                    Assert.Contains("Overflow", e.Message);
                }
                // 64-bit ACE 16 writes NaN as the least Currency, and its = finds that row for NaN. 32-bit ACE, 12 or 16,
                // says "Overflow" to it, as the engine refuses it (#101), and the least Currency is then written as itself.
                // ACE reads text as a number, by the machine's locale (text of a whole number reads alike in every one),
                // and a Yes/No's True as -1.
                if (aceWritesNaN) AceWrites(path, "A", new object[] { double.NaN, "2", true });
                else
                {
                    var nan = Record.Exception(() => AceWrites(path, "A", new object[] { double.NaN }));
                    Assert.True(nan is OleDbException && nan.Message.Contains("Overflow"), "NaN: ACE " + AceVersion + " "
                        + (nan == null ? "wrote it" : nan.GetType().Name + " " + nan.Message));
                    AceWrites(path, "A", new object[] { -922337203685477.5808m, "2", true });
                }
                Assert.Equal(new[] { -922337203685477.5808m, 2m, -1m }, AceCurrencies(path, "A").OrderBy(p => p.Key).Select(p => p.Value));
                if (aceWritesNaN) Assert.Equal(new[] { 0 }, AceIds(connection, "SELECT Id FROM A WHERE C = ?", double.NaN));
            }
            using (var db = Database.Open(path))
            {
                Assert.Equal(-922337203685477.5808m, db.GetTable("A").FindRowByPrimaryKey(0)["C"]);
                Assert.Empty(db.GetTable("A").CreateIndexCursor("ByCA").FindRowsByKey(double.NaN));

                // The engine takes text and a Yes/No for a Currency as it takes them for every number column, and as
                // ACE takes them: the text as a number, True as -1 (#75). Access reads its rows as written.
                var table = db.GetTable("E");
                table.Insert(new Row { ["Id"] = 1, ["C"] = "2" });
                table.Insert(new Row { ["Id"] = 2, ["C"] = true });
            }
            Assert.Equal(new[] { 1.5m, 2m, -1m }, AceCurrencies(path, "E").OrderBy(p => p.Key).Select(p => p.Value));
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Equal(new[] { 1 }, AceIds(connection, "SELECT Id FROM E WHERE C = ?", 2));
                Assert.Equal(new[] { 2 }, AceIds(connection, "SELECT Id FROM E WHERE C = ?", -1));
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void a_child_keyed_by_a_number_is_the_child_ace_takes_it_for(int daoVersion, string extension)
        {
            var path = NewDatabase(daoVersion, extension);
            Execute(path,
                "CREATE TABLE P (K CURRENCY CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (Id LONG CONSTRAINT pkC PRIMARY KEY, K CURRENCY, CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) ON UPDATE CASCADE)",
                "INSERT INTO P VALUES (0.0312, 'a')", "INSERT INTO P VALUES (1.2346, 'b')");
            var twin = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            File.Copy(path, twin);

            // A child named by a number the parent's key is that number taken to four places; one that is not, refused.
            var children = new object[] { 0.03125, 0.03125m, 1.23465m, 1.23455, 0.03125f, 0.031251 };
            var aceTook = new List<bool>();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(twin)))
            {
                connection.Open();
                for (var i = 0; i < children.Length; i++)
                    using (var command = new OleDbCommand("INSERT INTO C (Id, K) VALUES (?, ?)", connection))
                    {
                        command.Parameters.Add(Parameter(i));
                        command.Parameters.Add(Parameter(children[i]));
                        aceTook.Add(Record.Exception(() => command.ExecuteNonQuery()) == null);
                    }
                using (var command = new OleDbCommand("UPDATE P SET K = ? WHERE T = 'a'", connection))
                {
                    command.Parameters.Add(Parameter(0.09375));
                    command.ExecuteNonQuery();
                }
            }
            var engineTook = new List<bool>();
            using (var db = Database.Open(path))
            {
                var child = db.GetTable("C");
                for (var i = 0; i < children.Length; i++)
                    engineTook.Add(Record.Exception(() => child.Insert(new Row { ["Id"] = i, ["K"] = children[i] })) == null);
                var parent = db.GetTable("P");
                var a = parent.FindRowByPrimaryKey(0.03125);
                a["K"] = 0.09375;
                parent.Update(a);
            }

            Assert.Equal(new[] { true, true, true, true, true, false }, aceTook);
            Assert.Equal(aceTook, engineTook);
            AssertSame(Written(AceTable(twin, "C")), Written(AceTable(path, "C")), "the children, the engine's against ACE's");
            AssertSame(Written(AceTable(twin, "P")), Written(AceTable(path, "P")), "the parents, the engine's against ACE's");
            Assert.Equal(3, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM C WHERE K = 0.0938")));
            var compacted = Compact(path);
            try { AssertSame(Written(AceTable(twin, "C")), Written(AceTable(compacted, "C")), "the children after DAO compacted the engine's file"); }
            finally { File.Delete(compacted); }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void dao_reads_a_currency_property_and_an_imported_currency_as_access_makes_them(int daoVersion, string extension)
        {
            var path = NewDatabase(daoVersion, extension);
            Execute(path, "CREATE TABLE A (Id LONG CONSTRAINT pkA PRIMARY KEY, C CURRENCY)", "CREATE INDEX ByCA ON A (C)");
            // The numbers an import puts in a Currency column: those whose round-trip form has four places or fewer.
            var imported = new object[] { 0.1, 35184372088832.0078125, 562949953421312.5, -922337203685477.5, 131072.015625f, -2048.000244140625f, 0.5f };
            using (var db = Database.Open(path))
            {
                CreateKeyed(db, "I", "ByCI");
                foreach (var type in new[] { typeof(double), typeof(float) })
                {
                    var source = new DataTable("I");
                    source.Columns.Add("Id", typeof(int));
                    source.Columns.Add("C", type);
                    for (var i = 0; i < imported.Length; i++)
                        if (imported[i].GetType() == type) source.Rows.Add(i, imported[i]);
                    db.ImportTable(source, new ImportOptions { AppendIfExists = true });
                }

                var table = db.GetTable("A");
                table.Properties.Set("Half", DataType.Currency, 0.03125);
                table.Properties.Set("UnderAHalf", DataType.Currency, 0.00015);
                table.Properties.Set("Digits", DataType.Currency, 1.23465m);
                table.Properties.Save();
            }
            AceWrites(path, "A", imported);
            Assert.Equal(AceCurrencies(path, "A").OrderBy(p => p.Key), AceCurrencies(path, "I").OrderBy(p => p.Key));

            // DAO's own property of the same double, beside the engine's. (DAO takes no decimal for a property's value -
            // it reads a VT_DECIMAL as Null, as its Seek reads one as 0 - so the decimal's is held to what ACE's INSERT
            // writes for it.)
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Dao.Engine;
            try
            {
                dynamic db = dbeType.InvokeMember("OpenDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path });
                try
                {
                    dynamic tableDef = db.TableDefs["A"];
                    foreach (var pair in new[] { Tuple.Create("Half", 0.03125), Tuple.Create("UnderAHalf", 0.00015) })
                    {
                        dynamic own = tableDef.CreateProperty("Dao" + pair.Item1, 5 /* dbCurrency */, pair.Item2);
                        tableDef.Properties.Append(own);
                        object engine = tableDef.Properties[pair.Item1].Value, dao = tableDef.Properties["Dao" + pair.Item1].Value;
                        Assert.True(Equals(dao, engine), pair.Item1 + ": DAO reads the engine's property as " + engine + " and makes its own " + dao);
                    }
                    Assert.Equal(0.0312m, (decimal)tableDef.Properties["Half"].Value);
                    Assert.Equal(0.0001m, (decimal)tableDef.Properties["UnderAHalf"].Value);
                    Assert.Equal(1.2346m, (decimal)tableDef.Properties["Digits"].Value);
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            AceWrites(path, "A", new object[] { 1.23465m }, firstId: 100);
            Assert.Equal(1.2346m, AceCurrencies(path, "A")[100]);
        }
    }
}
