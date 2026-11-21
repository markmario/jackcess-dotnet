using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Text given for a Double or a Single column, and a double or a float given for a Text column, are written as
    /// Access writes them (#124): the double of the text's first 20 significant digits, the single of its first 12,
    /// and 15 or 7 significant digits of text. The engine writes text at and next to the half between two doubles,
    /// or two singles, of every range, and ACE writes the same with text parameters beside it; ACE reads the engine's
    /// rows as its own, finds each through the indexes the engine wrote, adds rows of its own to the engine's table,
    /// and reads them all again once DAO has compacted the file. A sample of the numbers is drawn unless
    /// JACKCESS_FULL_SWEEPS=1. Text of doubles from 1E-308 to 1E-304, and under 1E-321, is left out: there ACE stores
    /// some numbers one place from the double their first 20 digits are nearest, which the engine does not (#136).
    /// 32-bit ACE reads some text otherwise than 64-bit ACE 16, which the engine follows on every target: in such a
    /// process the stored values may differ only as that engine was measured to differ (<see cref="Otherwise"/>), and
    /// everything else is asserted as in a 64-bit one; with a 32-bit ACE not measured, that one comparison is skipped,
    /// naming the engine.
    /// </summary>
    public class NumberTextInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static IEnumerable<object[]> Formats()
        {
            yield return new object[] { 64, ".mdb" };
            yield return new object[] { 128, ".accdb" };
        }

        // ── The text ─────────────────────────────────────────────────────────────

        // K x 2^e as its digits and the power of ten of its last digit.
        private static (string Digits, int Power) Decimal(BigInteger k, int e)
        {
            BigInteger unscaled = e >= 0 ? k << e : k * BigInteger.Pow(5, -e);
            int power = e >= 0 ? 0 : e;
            return (unscaled.ToString(Invariant), power);
        }

        // The digits written with an exponent: d.dddE+x.
        private static string Scientific(string digits, int power, bool negative)
        {
            digits = digits.TrimStart('0');
            int exponent = digits.Length - 1 + power;
            string mantissa = digits.Length > 1 ? digits.Substring(0, 1) + "." + digits.Substring(1) : digits;
            return (negative ? "-" : "") + mantissa + "E" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString(Invariant);
        }

        // The half between two neighbours, and text either side of it: cut to n digits (short of it) and one more in
        // the nth digit (past it), for n either side of the digits Access reads.
        private static IEnumerable<string> Around((string Digits, int Power) half, bool negative, int read)
        {
            yield return Scientific(half.Digits, half.Power, negative);
            foreach (int n in new[] { read - 1, read, read + 1, read + 5 })
            {
                if (half.Digits.Length <= n) continue;
                int dropped = half.Digits.Length - n;
                var cut = BigInteger.Parse(half.Digits.Substring(0, n), Invariant);
                yield return Scientific(cut.ToString(Invariant), half.Power + dropped, negative);
                yield return Scientific((cut + 1).ToString(Invariant), half.Power + dropped, negative);
            }
        }

        private static List<string> Texts()
        {
            var random = new Random(124);
            int picks = Sweeps.Full ? 120 : 16;
            var texts = new List<string> { "-8.5635493928557935E15", "-0", "1.7976931348623158E+308", "3.40282356779733661637539395458142568448E+38" };
            for (int i = 0; i < picks; i++)
            {
                // A double of any range, subnormal ones among them, but none of the two ranges where ACE was found to
                // read text otherwise (#136): from 1E-308 to 1E-304, and under 1E-321.
                int field = i % 6 == 0 ? 0 : random.Next(15, 2047);
                long fraction = (((long)random.Next() << 31) + random.Next()) & 0xFFFFFFFFFFFFFL | (field == 0 ? 1L << 40 : 0);
                BigInteger m = field == 0 ? fraction : fraction | (1L << 52);
                texts.AddRange(Around(Decimal(2 * m + 1, (field == 0 ? -1074 : field - 1075) - 1), random.Next(2) == 1, 20));

                // A single of any range.
                int singleField = i % 6 == 0 ? 0 : random.Next(1, 255);
                int singleFraction = random.Next() & 0x7FFFFF | (singleField == 0 ? 1 << 10 : 0);
                BigInteger s = singleField == 0 ? singleFraction : singleFraction | (1 << 23);
                texts.AddRange(Around(Decimal(2 * s + 1, (singleField == 0 ? -149 : singleField - 150) - 1), random.Next(2) == 1, 12));
            }
            return texts;
        }

        // Doubles and floats for a Text column: every form Access writes them in.
        private static readonly object[] Numbers =
        {
            0.1 + 0.2, 1.0 / 3, 1e-5, 1.23456789012345E-5, 1e15, 999999999999999.5, 1000000000000005.0, -1000000000000005.0,
            1e-15, 1e-16, 0.000123456789012345, double.MaxValue, double.Epsilon, -0.0, 123.456, 2.675,
            0.1f, 1f / 3, 16777216f, 1e-7f, 1.234567e-5f, 1234567.5f, 12345665f, -1.5f,
        };

        private static string Bits(object value)
            => value is float f ? BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("X8", Invariant)
             : value is double d ? BitConverter.DoubleToInt64Bits(d).ToString("X16", Invariant)
             : value is DBNull || value == null ? "null" : "a " + value.GetType().Name;

        private static OleDbParameter Parameter(object value)
        {
            if (value is double) return new OleDbParameter("p", OleDbType.Double) { Value = value };
            if (value is float) return new OleDbParameter("p", OleDbType.Single) { Value = value };
            if (value is int) return new OleDbParameter("p", OleDbType.Integer) { Value = value };
            return new OleDbParameter("p", OleDbType.VarWChar) { Value = value };
        }

        private static void AceInsert(OleDbConnection connection, string table, int id, string column, object value)
        {
            using (var command = new OleDbCommand("INSERT INTO [" + table + "] (Id, " + column + ") VALUES (?, ?)", connection))
            {
                command.Parameters.Add(Parameter(id));
                command.Parameters.Add(Parameter(value));
                command.ExecuteNonQuery();
            }
        }

        // What each row holds, by Id: D's and S's bits and T's text.
        private static Dictionary<int, string> Stored(string path, string table)
            => AceRows(path, "SELECT Id, D, S, T FROM [" + table + "]").ToDictionary(
                r => Convert.ToInt32(r["Id"]),
                r => Bits(r["D"]) + " " + Bits(r["S"]) + " " + (r["T"] as string ?? "null"));

        /// <summary>
        /// What the ACE in this process was measured to store otherwise than 64-bit ACE 16, which the engine follows
        /// (docs/data-types.md, tools/ace-text-to-float.ps1): 32-bit ACE reads some text for a Double one unit in the
        /// last place from the double of its first 20 digits - its arithmetic is the x87's, and rounds twice, but not
        /// exactly as rounding to 64 bits and then to 53 would - and writes -0 and 999999999999999.5 as text otherwise;
        /// Access 2007's 32-bit ACE 12 also refuses text of the largest double. Null where nothing differs.
        /// </summary>
        private sealed class Otherwise
        {
            public string Engine;
            public bool DoubleOneUnit;
            public bool RefusesLargest;
            public Dictionary<string, string> Text = new Dictionary<string, string>();
        }

        private static string ThisAce => (Environment.Is64BitProcess ? "64" : "32") + "-bit ACE " + AceBuild;

        // Null for 64-bit ACE, which the engine follows exactly; the measured differences of a 32-bit ACE; or a skip of
        // the comparison of stored values, naming the engine, for a 32-bit ACE not measured.
        private static Otherwise MeasuredOtherwise()
        {
            if (Environment.Is64BitProcess) return null;
            if (AceVersion == 12)
                return new Otherwise
                {
                    Engine = ThisAce, DoubleOneUnit = true, RefusesLargest = true,
                    Text = { ["-0"] = "0", ["1000000000000000"] = "1E+15" },
                };
            if (AceBuild == "16.0.5011.1000")
                return new Otherwise { Engine = ThisAce, DoubleOneUnit = true, Text = { ["-0"] = "0", ["1000000000000000"] = "1E+15" } };
            return null;
        }

        private static bool Measured => Environment.Is64BitProcess || AceVersion == 12 || AceBuild == "16.0.5011.1000";

        // Whether ACE's value of a column is the engine's, or differs from it only as this ACE was measured to.
        private static bool Allowed(Otherwise otherwise, string column, object engine, object ace)
        {
            if (Bits(engine) + (engine as string) == Bits(ace) + (ace as string)) return true;
            if (otherwise == null) return false;
            if (column == "D" && otherwise.DoubleOneUnit && engine is double e && ace is double a)
            {
                long p = BitConverter.DoubleToInt64Bits(e), q = BitConverter.DoubleToInt64Bits(a);
                return (p < 0) == (q < 0) && Math.Abs(p - q) == 1;
            }
            return column == "T" && engine is string et && otherwise.Text.TryGetValue(et, out var at) && at == (ace as string);
        }

        // ACE finds each row of the engine's table through the index on its column, by the value the engine stored.
        private static void AssertAceFindsEach(string path, int rows, string when)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                var own = AceRows(connection, "SELECT Id, D, S, T FROM E").ToDictionary(r => Convert.ToInt32(r["Id"]));
                for (int id = 0; id < rows; id++)
                {
                    if (!own.TryGetValue(id, out var row)) continue;   // refused
                    string column = row["D"] is double ? "D" : row["S"] is float ? "S" : "T";
                    using (var command = new OleDbCommand("SELECT Id FROM E WHERE " + column + " = ?", connection))
                    {
                        command.Parameters.Add(Parameter(row[column]));
                        var found = new List<int>();
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) found.Add(reader.GetInt32(0));
                        Assert.True(found.Contains(id), column + " of row " + id + " (" + Bits(row[column]) + (row[column] as string) + ") " + when + ": ACE finds " + string.Join(", ", found));
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void ace_reads_finds_and_compacts_the_numbers_and_text_the_engine_wrote(int daoVersion, string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, daoVersion);
            var texts = Texts();

            // Each text goes to D in one row and to S in the next; each number to T in a row of its own. A text a
            // column refuses is refused by both, and that row is not written.
            var values = new List<(string Column, object Value)>();
            foreach (var text in texts) { values.Add(("D", text)); values.Add(("S", text)); }
            foreach (var number in Numbers) values.Add(("T", number));

            var refusedByEngine = new HashSet<int>();
            using (var db = Database.Open(path))
            {
                db.CreateTable("E", new[]
                {
                    new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.Double),
                    new ColumnDefinition("S", DataType.Single), new ColumnDefinition("T", DataType.Text) { MaxLength = 255 },
                }, new[] { "Id" }, new[] { new IndexDefinition("ByD", "D"), new IndexDefinition("ByS", "S"), new IndexDefinition("ByT", "T") });
                var table = db.GetTable("E");
                for (int id = 0; id < values.Count; id++)
                {
                    try { table.Insert(new Row { ["Id"] = id, [values[id].Column] = values[id].Value }); }
                    catch (OverflowException) { refusedByEngine.Add(id); }
                }
            }
            Execute(path, "CREATE TABLE A (Id LONG CONSTRAINT pkA PRIMARY KEY, D FLOAT, S REAL, T TEXT(255))",
                "CREATE INDEX ByDA ON A (D)", "CREATE INDEX BySA ON A (S)", "CREATE INDEX ByTA ON A (T)");
            var refusedByAce = new HashSet<int>();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (int id = 0; id < values.Count; id++)
                {
                    try { AceInsert(connection, "A", id, values[id].Column, values[id].Value); }
                    catch (OleDbException) { refusedByAce.Add(id); }
                }
            }
            var otherwise = MeasuredOtherwise();
            var engineRows = AceRows(path, "SELECT Id, D, S, T FROM E").ToDictionary(r => Convert.ToInt32(r["Id"]));
            var aceRows = AceRows(path, "SELECT Id, D, S, T FROM A").ToDictionary(r => Convert.ToInt32(r["Id"]));

            // ACE refuses what the engine refuses - and ACE 12 in a 32-bit process also text of the largest double,
            // which the engine stores as it.
            var refusedOtherwise = refusedByAce.Except(refusedByEngine).ToList();
            Assert.True(refusedByEngine.IsSubsetOf(refusedByAce), "the engine refused " + string.Join(", ", refusedByEngine.Except(refusedByAce).Select(i => values[i].Value)) + ", which " + ThisAce + " stores");
            Assert.True(refusedOtherwise.All(i => otherwise != null && otherwise.RefusesLargest
                    && values[i].Column == "D" && Math.Abs((double)engineRows[i]["D"]) == double.MaxValue),
                ThisAce + " refused " + string.Join(", ", refusedOtherwise.Select(i => values[i].Value)) + ", which the engine stores");

            // The engine wrote what ACE wrote, or what this ACE was measured to write otherwise; an ACE not measured is
            // not compared, and says so once the rest of the test has run.
            int differed = 0;
            var wrong = new List<string>();
            foreach (var p in aceRows)
            {
                string column = values[p.Key].Column;
                object mine = engineRows[p.Key][column], its = p.Value[column];
                if (Bits(mine) + (mine as string) == Bits(its) + (its as string)) continue;
                differed++;
                if (!Measured || !Allowed(otherwise, column, mine, its))
                    wrong.Add(column + " " + values[p.Key].Value + ": the engine wrote " + Bits(mine) + (mine as string) + ", " + ThisAce + " " + Bits(its) + (its as string));
            }
            if (Measured) Assert.True(wrong.Count == 0, wrong.Count + " values differ from " + ThisAce + "'s:\n" + string.Join("\n", wrong));
            AssertAceFindsEach(path, values.Count, "in the file the engine wrote");

            var engine = Stored(path, "E");
            var compacted = Compact(path);
            try
            {
                Assert.Equal(engine.OrderBy(p => p.Key), Stored(compacted, "E").OrderBy(p => p.Key));
                AssertAceFindsEach(compacted, values.Count, "after DAO compacted the file");
            }
            finally { File.Delete(compacted); }

            // ACE adds the same values to the engine's table, through its indexes; the engine finds both rows of each
            // through its index and by a scan.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (int id = 0; id < values.Count; id++)
                    if (!refusedByAce.Contains(id)) AceInsert(connection, "E", id + 100000, values[id].Column, values[id].Value);
            }
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                var table = db.GetTable("E");
                var rows = table.Rows.ToDictionary(r => (int)r["Id"]);
                for (int id = 0; id < values.Count; id++)
                {
                    if (refusedByAce.Contains(id)) continue;
                    var (column, _) = values[id];
                    // Each row is found through the engine's index by its own value: ACE's row holds what ACE stores,
                    // which is the engine's value or differs from it as compared above.
                    object stored = rows[id][column], added = rows[id + 100000][column];
                    var byIndex = table.CreateIndexCursor("By" + column).FindRowsByEntry(stored).Select(r => (int)r["Id"]).ToList();
                    var itsByIndex = table.CreateIndexCursor("By" + column).FindRowsByEntry(added).Select(r => (int)r["Id"]).ToList();
                    Assert.True(byIndex.Contains(id) && itsByIndex.Contains(id + 100000),
                        column + " " + values[id].Value + ": the engine's index finds " + string.Join(", ", byIndex) + " and " + string.Join(", ", itsByIndex));
                    if (Measured)
                        Assert.True(Allowed(otherwise, column, stored, added),
                            column + " " + values[id].Value + ": the engine wrote " + Bits(stored) + (stored as string) + ", " + ThisAce + " " + Bits(added) + (added as string));
                }
            }
            var again = Compact(path);
            try { Assert.Equal(2 * values.Count - refusedByAce.Count - refusedByEngine.Count, AceRows(again, "SELECT Id FROM E").Count); }
            finally { File.Delete(again); }

            if (!Measured)
                Assert.Skip("Everything but one comparison ran. What " + ThisAce + " stores for text in a Double or a Single, and "
                    + "writes for a number as text, has not been measured, so its values were not compared with the engine's ("
                    + differed + " differed); 64-bit ACE 16, 32-bit ACE 12 and 32-bit ACE 16.0.5011.1000 have been.");
        }
    }
}
