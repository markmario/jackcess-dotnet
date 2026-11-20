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
    /// The .accdb files the managed engine creates and the two column types that came
    /// with them, Large Number and Date/Time Extended, checked by Access's own engine. For each format the
    /// engine creates, ACE lists and reads the tables the engine made and filled in it, DAO goes down their keys,
    /// ACE adds rows through the engine's indexes, and DAO compacts the file keeping every row. For the two types,
    /// ACE reads the values the engine wrote at the ends of their ranges, DAO walks each index on them in the
    /// index's own order, ACE adds between the engine's keys and refuses the duplicates, and the engine reads what
    /// ACE wrote.
    /// <para>
    /// ACE older than a column type has no say about a file that has it, and such a test is skipped with the
    /// reason (<see cref="AccessReferee.SkipUnlessAceHas"/>): it neither fails nor passes there.
    /// </para>
    /// </summary>
    public class AccdbFormatsInAccessTests : IDisposable
    {
        private TempDb _tmp;

        public AccdbFormatsInAccessTests() { _tmp = new TempDb(); }
        public void Dispose() { _tmp.Dispose(); }

        private string NewPath(string name) { return Path.Combine(_tmp.Dir, name + "-" + Guid.NewGuid().ToString("N") + ".accdb"); }

        private static ColumnDefinition Long(string name) { return new ColumnDefinition(name, DataType.Int32); }
        private static ColumnDefinition Text(string name, int chars) { return new ColumnDefinition(name, DataType.Text) { MaxLength = chars }; }

        private static List<string> ListedTables(string path)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                return connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null).Rows.Cast<System.Data.DataRow>()
                    .Where(r => (string)r["TABLE_TYPE"] == "TABLE")
                    .Select(r => (string)r["TABLE_NAME"])
                    .ToList();
            }
        }

        private static byte[] Version(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[0x16];
                stream.Read(head, 0, head.Length);
                return new[] { head[0x14], head[0x15] };
            }
        }

        // ── Each format the engine creates ───────────────────────────────────────

        private static readonly string[] Names =
            { "Plain", "Table With Spaces", "#Hash", "$Dollar", "Café", "naïve résumé", "ÆØÅ", "Ελληνικά", "O'Brien" };

        // Text whose keys differ between General - Legacy, which the Access 2007 format sorts in, and General,
        // which the later ones do.
        private static readonly string[] Words =
            { "résumé", "Æther", "naïve", "Zürich", "日本", "smörgåsbord", "a-b", "O'Brien", "ß", "Ελληνικά", "\U0001F600", "Łódź" };

        private static int Key(int i) { return (int)((i * 7919L) % 100003); }
        private static string Code(int i) { return Words[i % Words.Length] + " " + Key(i).ToString("D6"); }
        private static string Notes(int i) { return i % 7 == 0 ? new string((char)('a' + i % 26), 3000) : "note " + i; }

        /// <summary>The codes DAO does not find by a Seek on Coded's primary key: it goes down the index by the key it makes.</summary>
        private static List<string> NotFoundBySeek(string path, IEnumerable<string> codes)
        {
            var missing = new List<string>();
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = dbe.OpenDatabase(path, false, true);
                try
                {
                    dynamic rs = db.OpenRecordset("Coded", 1 /* dbOpenTable */);
                    rs.Index = "PrimaryKey";
                    foreach (var code in codes)
                    {
                        rs.Seek("=", code);
                        if ((bool)rs.NoMatch || !Equals((string)rs.Fields["Code"].Value, code)) missing.Add(code);
                    }
                    rs.Close();
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return missing;
        }

        private static void InsertCoded(OleDbConnection connection, int i)
        {
            using (var command = new OleDbCommand("INSERT INTO Coded (Code, N, Notes) VALUES (?, ?, ?)", connection))
            {
                command.Parameters.Add("Code", OleDbType.VarWChar, 255).Value = Code(i);
                command.Parameters.Add("N", OleDbType.Integer).Value = i;
                command.Parameters.Add("Notes", OleDbType.LongVarWChar).Value = Notes(i);
                command.ExecuteNonQuery();
            }
        }

        private static int Count(OleDbConnection connection, string sql)
        {
            using (var command = new OleDbCommand(sql, connection))
                return Convert.ToInt32(command.ExecuteScalar());
        }

        [Theory]
        [InlineData(FileFormat.Access2007, null)]
        [InlineData(FileFormat.Access2010, null)]
        [InlineData(FileFormat.Access2016, LargeNumber)]
        [InlineData(FileFormat.Access2019, DateTimeExtended)]
        public void ace_lists_reads_seeks_and_adds_to_a_database_the_engine_created_and_dao_compacts_it(FileFormat version, string needs)
        {
            var path = NewPath("created-" + version);
            using (var db = Database.Create(path, version))
            {
                foreach (var name in Names)
                {
                    var plain = db.CreateTable(name, new[] { Long("Id"), Text("Txt", 100) });
                    for (var i = 1; i <= 3; i++)
                        plain.Insert(new Row { ["Id"] = i, ["Txt"] = "row " + i + " é" });
                }
                var table = db.CreateTable("Coded", new[] { Text("Code", 60), Long("N"), new ColumnDefinition("Notes", DataType.Memo) },
                    new[] { "Code" }, new[] { new IndexDefinition("ByN", "N") });
                for (var i = 1; i <= 3000; i++)
                    table.Insert(new Row { ["Code"] = Code(i), ["N"] = i, ["Notes"] = Notes(i) });
            }
            // The Access 2016 and 2019 formats are for an Access that has the column they came with.
            if (needs != null) SkipUnlessAceHas(needs);

            // Listed and read.
            var listed = ListedTables(path);
            Assert.All(Names.Concat(new[] { "Coded" }), name => Assert.Contains(name, listed));
            foreach (var name in Names)
            {
                Assert.Equal(3, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM [" + name + "]")));
                Assert.Equal("row 2 é", Scalar(path, "SELECT Txt FROM [" + name + "] WHERE Id = 2"));
            }
            // The name is already there for ACE, which looks for it by its key in the catalog.
            Assert.Contains("already exists", Assert.Throws<OleDbException>(() => Execute(path, "CREATE TABLE [Café] (Id LONG)")).Message);
            var read = AceRows(path, "SELECT Code, N, Notes FROM Coded");
            AssertSame(Written(Enumerable.Range(1, 3000).Select(i => V("Code", Code(i), "N", i, "Notes", Notes(i)))), Written(read), "Coded as ACE reads it");

            // Sought: every entry the engine wrote is where DAO looks for it.
            Assert.Empty(NotFoundBySeek(path, Enumerable.Range(1, 3000).Select(Code)));

            // Added to, through the engine's indexes and between its keys; a key the engine wrote is one ACE finds taken.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 3001; i <= 5000; i++)
                    InsertCoded(connection, i);
                foreach (var i in Enumerable.Range(1, Words.Length))
                    Assert.Contains("duplicate values", Assert.Throws<OleDbException>(() => InsertCoded(connection, i)).Message);
                Assert.Equal(5000, Count(connection, "SELECT COUNT(*) FROM Coded"));
                // Through the index on N, and by a scan ACE cannot use it for.
                foreach (var n in new[] { 1, 17, 2999, 3000, 3001, 4234, 5000 })
                {
                    Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM Coded WHERE N = " + n));
                    Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM Coded WHERE N + 0 = " + n));
                }
                Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM Coded WHERE N = 5001"));
            }

            // The engine finds ACE's rows and its own, and adds to and deletes from the pages ACE wrote and split.
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("Coded");
                Assert.Equal(5000, table.Rows.Count());
                Assert.Equal(4234, table.FindRowByPrimaryKey(Code(4234))["N"]);
                Assert.Equal(Notes(4235), table.FindRowByPrimaryKey(Code(4235))["Notes"]);
                for (var i = 5001; i <= 5500; i++)
                    table.Insert(new Row { ["Code"] = Code(i), ["N"] = i, ["Notes"] = Notes(i) });
                table.DeleteRow("Code", Code(20));
                table.DeleteRow("Code", Code(4000));
            }
            var kept = Enumerable.Range(1, 5500).Where(i => i != 20 && i != 4000).ToList();
            Assert.Empty(NotFoundBySeek(path, kept.Select(Code)));
            Assert.Equal(new[] { Code(20), Code(4000) }, NotFoundBySeek(path, new[] { Code(20), Code(4000) }));

            // Compacted, every row kept.
            var compacted = Compact(path);
            listed = ListedTables(compacted);
            Assert.All(Names.Concat(new[] { "Coded" }), name => Assert.Contains(name, listed));
            Assert.DoesNotContain("MSysCompactError", listed);
            AssertSame(Written(kept.Select(i => V("Code", Code(i), "N", i, "Notes", Notes(i)))), Written(AceRows(compacted, "SELECT Code, N, Notes FROM Coded")),
                "Coded after DAO compacted the file");
            Assert.Empty(NotFoundBySeek(compacted, kept.Select(Code)));

            // The compacted file is ACE's own, and the engine reads and adds to it.
            using (var db = Database.Open(compacted))
            {
                var table = db.GetTable("Coded");
                Assert.Equal(5498, table.Rows.Count());
                table.Insert(new Row { ["Code"] = Code(5501), ["N"] = 5501, ["Notes"] = Notes(5501) });
            }
            Assert.Empty(NotFoundBySeek(compacted, new[] { Code(1), Code(5500), Code(5501) }));
        }

        // ── Large Number and Date/Time Extended ──────────────────────────────────

        private static readonly long?[] Numbers =
        {
            long.MaxValue, long.MinValue, -1, 0, 1, 4294967296, -4294967296, 255, 256, -256, 2147483648, null,
        };

        // Either side of 30 December 1899, where a Date/Time counts from; years a Date/Time has none of; and the
        // last tick of a second, of a leap day and of the range.
        private static readonly string[] DateTexts =
        {
            "0001-01-01 00:00:00.0000000", "9999-12-31 23:59:59.9999999", "1899-12-29 23:59:59.9999999",
            "1899-12-30 00:00:00.0000000", "2020-01-02 03:04:05.1234567", "2020-01-02 03:04:05.1234568",
            "1066-10-14 09:00:00.0000001", "0100-06-14 12:45:00.1234567", "2024-02-29 23:59:59.9999999",
            null, "1000-01-01 00:00:00.0000000", "0099-12-31 23:59:59.9999999",
        };

        private static DateTime? Date(string text)
        {
            return text == null ? (DateTime?)null : DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        }

        private static string Sql(long? number) { return number == null ? "NULL" : number.Value.ToString(CultureInfo.InvariantCulture); }

        // A date as Access's SQL takes one that has more than whole seconds: as text.
        private static string Sql(DateTime? date)
        {
            return date == null ? "NULL" : "'" + date.Value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'";
        }

        /// <summary>A row of T as it is compared: the number, and the date to its last tick.</summary>
        private static Values Typed(int id, long? number, DateTime? date)
        {
            return V("Id", id, "N", number, "D", date == null ? null : ToTheTick(date.Value));
        }

        /// <summary>
        /// Which of the two column types a file has. Each is refereed in a file of its own as well as together, so
        /// that an ACE that has Large Number and no Date/Time Extended still referees the Large Number.
        /// </summary>
        private sealed class Has
        {
            public Has(bool numbers, bool dates) { Numbers = numbers; Dates = dates; }
            public bool Numbers { get; }
            public bool Dates { get; }

            /// <summary>T's columns after Id, for a query.</summary>
            public string Columns { get { return (Numbers ? ", N" : "") + (Dates ? ", D" : ""); } }

            /// <summary>Skips the test, with the reason, unless ACE on this machine makes each type the file has.</summary>
            public void SkipUnlessAceHasThem()
            {
                if (Numbers) SkipUnlessAceHas(LargeNumber);
                if (Dates) SkipUnlessAceHas(DateTimeExtended);
            }

            /// <summary>The version ACE gives a file with these columns: Access 2019's for a date, else Access 2016's.</summary>
            public byte[] Version { get { return new byte[] { Dates ? (byte)0x06 : (byte)0x05, 0x00 }; } }

            /// <summary>A row as the file holds it: without the value of a column the file has not.</summary>
            public Values RowOf(int id, long? number, DateTime? date)
            {
                return AccdbFormatsInAccessTests.Typed(id, Numbers ? number : null, Dates ? date : (DateTime?)null);
            }

            /// <summary>Rows a reader gave, as they are compared.</summary>
            public List<Values> Typed(IEnumerable<Values> rows)
            {
                return rows.Select(r => AccdbFormatsInAccessTests.Typed(Convert.ToInt32(r["Id"]),
                    Numbers && r["N"] != null ? Convert.ToInt64(r["N"]) : (long?)null,
                    Dates ? (DateTime?)r["D"] : null)).ToList();
            }

            public Row EngineRow(Values typed)
            {
                var row = new Row { ["Id"] = typed["Id"] };
                if (Numbers) row["N"] = typed["N"];
                if (Dates) row["D"] = Date(Day(typed)?.Substring("extended ".Length));
                return row;
            }

            /// <summary>The values of a row for ACE's INSERT INTO T (Id, N, D).</summary>
            public string SqlValues(Values typed)
            {
                return typed["Id"] + (Numbers ? ", " + Sql(Number(typed)) : "") + (Dates ? ", " + (Day(typed) == null ? "NULL" : "'" + Day(typed).Substring("extended ".Length) + "'") : "");
            }

            public ColumnDefinition[] TableColumns()
            {
                var columns = new List<ColumnDefinition> { Long("Id") };
                if (Numbers) columns.Add(new ColumnDefinition("N", DataType.Int64));
                if (Dates) columns.Add(new ColumnDefinition("D", DataType.DateTimeExtended));
                return columns.ToArray();
            }

            public IndexDefinition[] Indexes()
            {
                var indexes = new List<IndexDefinition>();
                if (Numbers) indexes.Add(new IndexDefinition("NAsc", "N"));
                if (Numbers) indexes.Add(new IndexDefinition("NDesc", "N") { DescendingColumns = { "N" } });
                if (Dates) indexes.Add(new IndexDefinition("DAsc", "D"));
                if (Dates) indexes.Add(new IndexDefinition("DDesc", "D") { DescendingColumns = { "D" } });
                if (Numbers && Dates) indexes.Add(new IndexDefinition("NThenD", "N", "D") { DescendingColumns = { "D" } });
                return indexes.ToArray();
            }
        }

        // The rows the engine writes: the ends of both ranges, then enough for every index to have several pages -
        // numbers either side of zero, dates a few days and one tick apart from 1850 - and rows that share a
        // number, which the index on the two columns keeps by the date, latest first.
        private static IEnumerable<Values> RowsTheEngineWrites(Has has)
        {
            for (var i = 0; i < Numbers.Length; i++)
                yield return has.RowOf(i + 1, Numbers[i], Date(DateTexts[i]));
            for (var i = 100; i < 1300; i++)
                yield return has.RowOf(i, i % 10 == 0 ? 42 : (i - 700) * 1000000007L, new DateTime(1850, 1, 1).AddTicks(i * 7919000000001L));
        }

        // What ACE adds: between the engine's keys in every index.
        private static IEnumerable<Values> RowsAceAdds(Has has)
        {
            for (var i = 2000; i < 2400; i++)
                yield return has.RowOf(i, i % 10 == 0 ? 42 : (i - 2200) * 1000000007L + 500000003L, new DateTime(1850, 1, 1).AddTicks(i * 4421000000003L));
        }

        private static long? Number(Values row) { return (long?)row["N"]; }
        private static string Day(Values row) { return (string)row["D"]; }

        // The order each index keeps its rows in, a null first ascending and last descending. A date as it is
        // compared sorts as text the way the dates do.
        private static readonly Dictionary<string, Func<IEnumerable<Values>, IEnumerable<Values>>> IndexOrders =
            new Dictionary<string, Func<IEnumerable<Values>, IEnumerable<Values>>>
            {
                { "NAsc", rows => rows.OrderBy(r => Number(r) != null).ThenBy(Number) },
                { "NDesc", rows => rows.OrderBy(r => Number(r) == null).ThenByDescending(Number) },
                { "DAsc", rows => rows.OrderBy(r => Day(r) != null).ThenBy(Day, StringComparer.Ordinal) },
                { "DDesc", rows => rows.OrderBy(r => Day(r) == null).ThenByDescending(Day, StringComparer.Ordinal) },
                { "NThenD", rows => rows.OrderBy(r => Number(r) != null).ThenBy(Number).ThenBy(r => Day(r) == null).ThenByDescending(Day, StringComparer.Ordinal) },
            };

        /// <summary>
        /// T holds exactly <paramref name="expected"/>: as DAO reads it, as the engine reads it, in the order DAO
        /// walks each index - it reads the index's own pages, so a row missing from one, or out of place in it,
        /// shows - and for ACE's WHERE on each column, which finds the rows the values have and no others.
        /// </summary>
        private static void AssertTableIs(string path, Has has, List<Values> expected, string when)
        {
            AssertSame(Written(expected), Written(has.Typed(DaoRows(path, "SELECT Id" + has.Columns + " FROM T"))), "T as DAO reads it " + when);
            AssertSame(Written(expected), Written(has.Typed(EngineTable(path, "T"))), "T as the engine reads it " + when);
            // ACE's OLE DB provider reads the numbers; it hands out no Date/Time Extended value.
            if (has.Numbers)
                AssertSame(Written(expected.Select(r => V("Id", r["Id"], "N", r["N"]))), Written(AceRows(path, "SELECT Id, N FROM T")), "T's numbers as ACE reads them " + when);

            var walks = 0;
            foreach (var index in has.Indexes().Select(i => i.Name))
            {
                // By the indexed values, not the rows: rows with one key are in the order they were added.
                Func<Values, string> shown = r => index == "NThenD" ? Written(r["N"]) + " " + Written(r["D"])
                                                : index.StartsWith("N", StringComparison.Ordinal) ? Written(r["N"]) : Written(r["D"]);
                var walked = has.Typed(DaoRows(path, "T", index)).Select(shown).ToList();
                var inOrder = IndexOrders[index](expected).Select(shown).ToList();
                Assert.True(inOrder.SequenceEqual(walked), "T." + index + " as DAO walks it " + when + ": " + walked.Count + " entries for " + inOrder.Count
                    + " rows; first difference at " + Enumerable.Range(0, Math.Min(walked.Count, inOrder.Count)).FirstOrDefault(i => walked[i] != inOrder[i]));
                walks++;
            }
            Assert.Equal(has.Numbers && has.Dates ? 5 : 2, walks);

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var row in expected.Where(r => Convert.ToInt32(r["Id"]) <= Numbers.Length || Convert.ToInt32(r["Id"]) % 97 == 0))
                {
                    if (Number(row) != null)
                        Assert.Equal(expected.Count(r => Number(r) == Number(row)), Count(connection, "SELECT COUNT(*) FROM T WHERE N = " + Sql(Number(row))));
                    if (Day(row) != null)
                        Assert.Equal(Convert.ToInt32(row["Id"]), Count(connection, "SELECT MAX(Id) FROM T WHERE D = '" + Day(row).Substring("extended ".Length) + "'"));
                }
                Assert.Equal(expected.Count, Count(connection, "SELECT COUNT(*) FROM T"));
                if (has.Numbers) Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM T WHERE N = 43"));
                if (has.Dates) Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM T WHERE D = '2020-01-02 03:04:05.1234569'"));
            }
        }

        /// <summary>The numbers DAO does not find by a Seek on T's index of them.</summary>
        private static List<long> NumbersNotFoundBySeek(string path, IEnumerable<long> numbers)
        {
            var missing = new List<long>();
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = dbe.OpenDatabase(path, false, true);
                try
                {
                    dynamic rs = db.OpenRecordset("T", 1 /* dbOpenTable */);
                    rs.Index = "NAsc";
                    foreach (var number in numbers)
                    {
                        rs.Seek("=", number);
                        if ((bool)rs.NoMatch || Convert.ToInt64(rs.Fields["N"].Value) != number) missing.Add(number);
                    }
                    rs.Close();
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return missing;
        }

        private static OleDbException Refused(OleDbConnection connection, string sql)
        {
            return Assert.Throws<OleDbException>(() => { using (var command = new OleDbCommand(sql, connection)) command.ExecuteNonQuery(); });
        }

        // Each type in a file of its own, and the two together, where an index is on both. The file with only
        // Large Numbers is one of Access 2016's format, which an Access without Date/Time Extended still opens.
        [Theory]
        [InlineData(FileFormat.Access2010, true, false)]
        [InlineData(FileFormat.Access2010, false, true)]
        [InlineData(FileFormat.Access2007, true, true)]
        [InlineData(FileFormat.Access2010, true, true)]
        [InlineData(FileFormat.Access2019, true, true)]
        public void ace_reads_walks_adds_to_and_compacts_large_numbers_and_extended_dates_the_engine_wrote(FileFormat version, bool numbers, bool dates)
        {
            var has = new Has(numbers, dates);
            var path = NewPath("typed-" + version + (numbers ? "-numbers" : "") + (dates ? "-dates" : ""));
            var expected = RowsTheEngineWrites(has).ToList();
            using (var db = Database.Create(path, version))
            {
                var table = db.CreateTable("T", has.TableColumns(), new[] { "Id" }, has.Indexes());
                foreach (var row in expected)
                    table.Insert(has.EngineRow(row));

                if (numbers)
                {
                    var keyed = db.CreateTable("KeyedByNumber", new[] { new ColumnDefinition("K", DataType.Int64), Long("V") }, primaryKey: "K");
                    keyed.Insert(new Row { ["K"] = long.MaxValue, ["V"] = 1 });
                    keyed.Insert(new Row { ["K"] = long.MinValue, ["V"] = 2 });
                }
                if (dates)
                {
                    var keyed = db.CreateTable("OnceADate", new[] { Long("Id"), new ColumnDefinition("Stamp", DataType.DateTimeExtended) },
                        new[] { "Id" }, new[] { new IndexDefinition("OneStamp", "Stamp") { IsUnique = true } });
                    keyed.Insert(new Row { ["Id"] = 1, ["Stamp"] = Date(DateTexts[4]) });
                    keyed.Insert(new Row { ["Id"] = 2, ["Stamp"] = DateTime.MaxValue });
                }
            }
            has.SkipUnlessAceHasThem();

            // The file is one of the format that has its columns, as ACE makes a file it gives such a table.
            Assert.Equal(has.Version, Version(path));
            AssertTableIs(path, has, expected, "as the engine wrote it");
            if (numbers)
            {
                Assert.Empty(NumbersNotFoundBySeek(path, expected.Where(r => Number(r) != null).Select(r => Number(r).Value).Distinct()));
                Assert.Equal(new[] { 43L }, NumbersNotFoundBySeek(path, new[] { 43L }));
            }

            // ACE adds between the engine's keys, in every index, and finds the engine's unique keys taken.
            var added = RowsAceAdds(has).ToList();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var row in added)
                    using (var command = new OleDbCommand("INSERT INTO T (Id" + has.Columns + ") VALUES (" + has.SqlValues(row) + ")", connection))
                        Assert.Equal(1, command.ExecuteNonQuery());

                if (numbers)
                {
                    Assert.Contains("duplicate values", Refused(connection, "INSERT INTO KeyedByNumber (K, V) VALUES (9223372036854775807, 3)").Message);
                    Assert.Contains("duplicate values", Refused(connection, "INSERT INTO KeyedByNumber (K, V) VALUES (-9223372036854775808, 3)").Message);
                    using (var command = new OleDbCommand("INSERT INTO KeyedByNumber (K, V) VALUES (9223372036854775806, 3)", connection))
                        Assert.Equal(1, command.ExecuteNonQuery());
                }
                if (dates)
                {
                    Assert.Contains("duplicate values", Refused(connection, "INSERT INTO OnceADate (Id, Stamp) VALUES (5, '2020-01-02 03:04:05.1234567')").Message);
                    Assert.Contains("duplicate values", Refused(connection, "INSERT INTO OnceADate (Id, Stamp) VALUES (5, '9999-12-31 23:59:59.9999999')").Message);
                    using (var command = new OleDbCommand("INSERT INTO OnceADate (Id, Stamp) VALUES (3, '2020-01-02 03:04:05.1234566')", connection))
                        Assert.Equal(1, command.ExecuteNonQuery());
                }
            }
            expected.AddRange(added);
            AssertTableIs(path, has, expected, "after ACE added to it");

            // The engine reads ACE's rows, finds them through the indexes ACE added them to, and goes on writing.
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                foreach (var row in added.Where(r => Convert.ToInt32(r["Id"]) % 50 == 1))
                {
                    if (numbers) Assert.Equal(row["Id"], table.CreateIndexCursor("NAsc").FindRow("N", Number(row).Value)["Id"]);
                    if (dates) Assert.Equal(row["Id"], table.CreateIndexCursor("DAsc").FindRow("D", has.EngineRow(row)["D"])["Id"]);
                }
                var more = Enumerable.Range(3000, 200).Select(i => has.RowOf(i, i * 3000000019L, new DateTime(2200, 1, 1).AddTicks(i * 99999999977L))).ToList();
                foreach (var row in more)
                    table.Insert(has.EngineRow(row));
                expected.AddRange(more);

                var changed = has.RowOf(2, 7L, DateTime.MaxValue.AddTicks(-1));
                var changes = has.EngineRow(changed);
                changes.Remove("Id");
                table.UpdateByPrimaryKey(2, changes);
                expected[expected.FindIndex(r => Convert.ToInt32(r["Id"]) == 2)] = changed;
                table.DeleteRow("Id", 2001);
                table.DeleteRow("Id", 1);
                expected.RemoveAll(r => Convert.ToInt32(r["Id"]) == 2001 || Convert.ToInt32(r["Id"]) == 1);

                // A key ACE wrote is one the engine finds taken.
                if (numbers)
                {
                    var keyed = db.GetTable("KeyedByNumber");
                    Assert.Equal(3, keyed.Rows.Count());
                    Assert.Throws<ConstraintViolationException>(() => keyed.Insert(new Row { ["K"] = 9223372036854775806L, ["V"] = 4 }));
                }
                if (dates)
                {
                    var keyed = db.GetTable("OnceADate");
                    Assert.Equal(3, keyed.Rows.Count());
                    Assert.Throws<ConstraintViolationException>(() => keyed.Insert(new Row { ["Id"] = 6, ["Stamp"] = Date(DateTexts[4]).Value.AddTicks(-1) }));
                }
            }
            AssertTableIs(path, has, expected, "after the engine added to, changed and deleted from what ACE wrote");

            // Compacted: DAO reads every row and builds every index again from them.
            var compacted = Compact(path);
            Assert.DoesNotContain("MSysCompactError", ListedTables(compacted));
            Assert.Equal(has.Version, Version(compacted));
            AssertTableIs(compacted, has, expected, "after DAO compacted the file");
            if (numbers) Assert.Equal(3, Convert.ToInt32(Scalar(compacted, "SELECT COUNT(*) FROM KeyedByNumber")));
            if (dates) Assert.Equal(3, Convert.ToInt32(Scalar(compacted, "SELECT COUNT(*) FROM OnceADate")));
        }

        // Access gives a file the version of the format that has the column when a table first gets one, whatever
        // .accdb format the file was made in, and refuses the column in an .mdb. The engine does the same.
        [Theory]
        [InlineData(128, LargeNumber, DataType.Int64)]
        [InlineData(256, LargeNumber, DataType.Int64)]
        [InlineData(128, DateTimeExtended, DataType.DateTimeExtended)]
        [InlineData(256, DateTimeExtended, DataType.DateTimeExtended)]
        public void a_table_with_such_a_column_gives_the_file_the_version_ace_gives_it(int daoFormat, string sqlType, DataType type)
        {
            SkipUnlessAceHas(sqlType);
            var byAce = NewPath("ace-" + daoFormat);
            var byEngine = NewPath("engine-" + daoFormat);
            DaoCreate(byAce, daoFormat);
            File.Copy(byAce, byEngine);
            var asMade = Version(byAce);

            Execute(byAce, "CREATE TABLE Plain (Id LONG)");
            using (var db = Database.Open(byEngine))
                db.CreateTable("Plain", new[] { Long("Id") });
            Assert.Equal(asMade, Version(byAce));
            Assert.Equal(asMade, Version(byEngine));

            Execute(byAce, "CREATE TABLE Newer (Id LONG, V " + sqlType + ")");
            using (var db = Database.Open(byEngine))
                db.CreateTable("Newer", new[] { Long("Id"), new ColumnDefinition("V", type) });
            Assert.NotEqual(asMade, Version(byAce));
            Assert.Equal(Version(byAce), Version(byEngine));

            // And ACE goes on with the engine's file as with its own.
            Execute(byEngine, "INSERT INTO Newer (Id) VALUES (1)", "CREATE TABLE After (Id LONG)");
            Assert.Equal(1, Convert.ToInt32(Scalar(byEngine, "SELECT COUNT(*) FROM Newer")));
        }

        [Theory]
        [InlineData(LargeNumber, DataType.Int64)]
        [InlineData(DateTimeExtended, DataType.DateTimeExtended)]
        public void ace_and_the_engine_both_refuse_such_a_column_in_an_mdb(string sqlType, DataType type)
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);

            Assert.Throws<OleDbException>(() => Execute(path, "CREATE TABLE Newer (Id LONG, V " + sqlType + ")"));
            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
                Assert.Throws<NotSupportedException>(() => db.CreateTable("Newer", new[] { Long("Id"), new ColumnDefinition("V", type) }));

            // Refused before a byte of the file changed.
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.DoesNotContain("Newer", ListedTables(path));
        }
    }
}
