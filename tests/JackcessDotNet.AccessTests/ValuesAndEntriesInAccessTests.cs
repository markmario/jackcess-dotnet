using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// What the managed engine stores, or refuses, where it once differed from Access, with Access as the referee
    /// of each (#81, #80, #75 and the bytes of #37 and #38).
    /// <para>
    /// <b>A damaged index</b> (#81). The unit tests' Assets/damaged-primary-key.mdb is a file whose primary key holds
    /// row 3's entry under 30 and none under 3. ACE refuses an update of the row's key and a delete of the row on it, and so does the
    /// engine, before anything is written; with its indexes verified the engine makes both, and ACE then reads,
    /// seeks, adds to and compacts the table, its primary key whole.
    /// </para>
    /// <para>
    /// <b>A Decimal with more places than its column holds</b> (#80) is stored as ACE stores it: cut toward zero,
    /// a negative number cut to nothing a negative zero.
    /// </para>
    /// <para>
    /// <b>A bool, text and a date given for a number column</b> (#75) are stored as ACE stores them, where the
    /// engine takes them; what neither takes, neither stores.
    /// </para>
    /// <para>
    /// <b>The bytes of a table definition</b> (#37, #38) the engine writes as DAO writes them, and not as ACE's DDL
    /// does: zeros for a non-text column's sort order, 00 00 for a Date/Time Extended's precision and scale, and
    /// the usage maps of a long-value column or an index on pages of their own. DAO changes such a table's design,
    /// ACE writes to it, and DAO compacts it with every row and index.
    /// </para>
    /// </summary>
    public class ValuesAndEntriesInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        private string EngineAsset(string name)
        {
            var assets = typeof(ValuesAndEntriesInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineAssetsDir").Value;
            var path = _tmp.NewMdbPath();
            File.Copy(Path.Combine(assets, name), path);
            return path;
        }

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(stream));
        }

        // ── A damaged index (#81) ────────────────────────────────────────────

        private static List<Values> DamagedRows(Func<int, int> idOf, int? without = null)
        {
            return Enumerable.Range(1, 5).Where(i => i != without)
                .Select(i => V("Id", idOf(i), "Name", "row " + i)).ToList();
        }

        private static Cursor AtRowThree(Database db)
        {
            var cursor = db.GetTable("T").CreateCursor();
            Assert.NotNull(cursor.FindRow("Name", "row 3"));
            return cursor;
        }

        /// <summary>DAO walks the primary key's own pages, after compacting: a key DAO dropped is no index to walk.</summary>
        private static void AssertKeyWhole(string path, int rows)
        {
            var compacted = Compact(path);
            try { Assert.Equal(rows, DaoRows(compacted, "T", "PrimaryKey").Count); }
            finally { File.Delete(compacted); }
        }

        [Fact]
        public void Access_and_the_engine_refuse_a_change_of_the_key_and_a_delete_of_a_row_a_damaged_key_does_not_hold()
        {
            var path = EngineAsset("damaged-primary-key.mdb");
            var before = Hash(path);

            Assert.Contains("search key", Assert.Throws<OleDbException>(() => Execute(path, "UPDATE T SET Id = 33 WHERE Name = 'row 3'")).Message);
            Assert.Contains("search key", Assert.Throws<OleDbException>(() => Execute(path, "DELETE FROM T WHERE Name = 'row 3'")).Message);
            Assert.Equal(before, Hash(path));

            using (var db = Database.Open(path))
            {
                Assert.Throws<NotSupportedException>(() => AtRowThree(db).UpdateCurrentRow(new Row { ["Id"] = 33 }));
                Assert.Throws<NotSupportedException>(() => AtRowThree(db).DeleteCurrentRow());
            }
            Assert.Equal(before, Hash(path));

            // An update of another column takes no entry out, and both make it.
            using (var db = Database.Open(path))
                AtRowThree(db).UpdateCurrentRow(new Row { ["Name"] = "row 3" });
            Execute(path, "UPDATE T SET Name = 'row 3' WHERE Name = 'row 3'");
            Assert.Equal(Written(DamagedRows(i => i)), Written(AceTable(path, "T")));
        }

        [Fact]
        public void With_its_indexes_verified_the_engine_changes_the_key_and_Access_reads_seeks_adds_to_and_compacts_the_table()
        {
            var path = EngineAsset("damaged-primary-key.mdb");
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                AtRowThree(db).UpdateCurrentRow(new Row { ["Id"] = 33 });
            }
            AssertTableIs(path, "T", "Id", DamagedRows(i => i == 3 ? 33 : i), "after the engine changed row 3's key");
            AssertKeyWhole(path, 5);

            // Neither the old key nor the wrong entry's is held any longer: ACE takes a row under each.
            AceInsert(path, "T", V("Id", 3, "Name", "new 3"));
            AceInsert(path, "T", V("Id", 30, "Name", "new 30"));
            var rows = DamagedRows(i => i == 3 ? 33 : i);
            rows.Add(V("Id", 3, "Name", "new 3"));
            rows.Add(V("Id", 30, "Name", "new 30"));
            AssertTableIs(path, "T", "Id", rows, "after ACE added rows 3 and 30");
            AssertKeyWhole(path, 7);
        }

        [Fact]
        public void With_its_indexes_verified_the_engine_deletes_the_row_and_leaves_no_entry_of_it_for_Access_to_come_to()
        {
            var path = EngineAsset("damaged-primary-key.mdb");
            using (var db = Database.Open(path))
            {
                db.VerifyIndexes = true;
                AtRowThree(db).DeleteCurrentRow();
            }
            AssertTableIs(path, "T", "Id", DamagedRows(i => i, without: 3), "after the engine deleted row 3");
            // An entry left under 30 pointing where the row was would fail ACE's query for 30, and refuse its row.
            Assert.Empty(AceRows(path, "SELECT * FROM T WHERE Id = 30"));
            AceInsert(path, "T", V("Id", 30, "Name", "new 30"));
            var rows = DamagedRows(i => i, without: 3);
            rows.Add(V("Id", 30, "Name", "new 30"));
            AssertTableIs(path, "T", "Id", rows, "after ACE added row 30");
            AssertKeyWhole(path, 5);
        }

        // ── A Decimal with more places than its column holds (#80) ───────────

        private static readonly object[] DecimalsGiven =
        {
            1.015m, -1.015m, -0.001m, 0.009m, 2.675, -2.675, 0.29, 7.999f, "1.019", "-0.004", 123456789.123456789m,
        };

        /// <summary>A Decimal as DAO hands it out, with the sign of a negative zero: "-0.00".</summary>
        private static string Signed(object value)
        {
            var number = (decimal)value;
            return (number == 0m && decimal.GetBits(number)[3] < 0 ? "-" : "") + number.ToString(CultureInfo.InvariantCulture);
        }

        [Fact]
        public void A_Decimal_with_more_places_than_its_column_holds_is_stored_by_the_engine_as_ACE_stores_it()
        {
            var path = _tmp.NewMdbPath();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var columns = new[]
                {
                    new ColumnDefinition("Id", DataType.Int32),
                    new ColumnDefinition("D", DataType.Decimal) { Precision = 18, Scale = 2 },
                    new ColumnDefinition("Z", DataType.Decimal) { Precision = 18, Scale = 0 },
                };
                var engine = db.CreateTable("ByEngine", columns, new[] { "Id" });
                db.CreateTable("ByAce", columns, new[] { "Id" });
                for (var i = 0; i < DecimalsGiven.Length; i++)
                    engine.Insert(new Row { ["Id"] = i, ["D"] = DecimalsGiven[i], ["Z"] = DecimalsGiven[i] });
            }
            for (var i = 0; i < DecimalsGiven.Length; i++)
                AceInsert(path, "ByAce", V("Id", i, "D", DecimalsGiven[i], "Z", DecimalsGiven[i]));

            // DAO reads both, the sign of a negative zero with them; ACE reads both, and finds each row by its value
            // through an index of its own.
            Func<string, List<string>> dao = table => DaoRows(path, "SELECT * FROM " + table + " ORDER BY Id")
                .Select(r => r["Id"] + ": " + Signed(r["D"]) + " " + Signed(r["Z"])).ToList();
            Assert.Equal(dao("ByAce"), dao("ByEngine"));
            Assert.Contains("2: -0.00 -0", dao("ByEngine"));
            Assert.Contains("0: 1.01 1", dao("ByEngine"));

            // Each table with an index of ACE's own on D: the engine's rows are found as ACE's are, through the index
            // and by a scan. (ACE's index tells a negative zero from a zero, its scan does not, in both tables alike.)
            Execute(path, "CREATE INDEX ixD ON ByEngine (D)", "CREATE INDEX ixD ON ByAce (D)");
            Func<string, string, List<object>> ids = (table, where) => AceRows(path, "SELECT Id FROM " + table + " WHERE " + where + " ORDER BY Id").Select(r => r["Id"]).ToList();
            foreach (var value in AceRows(path, "SELECT DISTINCT D FROM ByAce").Select(r => ((decimal)r["D"]).ToString(CultureInfo.InvariantCulture)))
                foreach (var where in new[] { "D = " + value, "D + 0 = " + value })
                    Assert.Equal(ids("ByAce", where), ids("ByEngine", where));
            var compacted = Compact(path);
            try { Assert.Equal(dao("ByAce").Count, DaoRows(compacted, "ByEngine", "ixD").Count); }
            finally { File.Delete(compacted); }
        }

        // ── A bool, text and a date for a number column (#75) ────────────────

        private static readonly Dictionary<string, ColumnDefinition> NumberTypes = new Dictionary<string, ColumnDefinition>
        {
            ["Byte"] = new ColumnDefinition("V", DataType.Byte),
            ["Integer"] = new ColumnDefinition("V", DataType.Int16),
            ["Long"] = new ColumnDefinition("V", DataType.Int32),
            ["LargeNumber"] = new ColumnDefinition("V", DataType.Int64),
            ["Single"] = new ColumnDefinition("V", DataType.Single),
            ["Double"] = new ColumnDefinition("V", DataType.Double),
            ["Currency"] = new ColumnDefinition("V", DataType.Currency),
            ["Decimal"] = new ColumnDefinition("V", DataType.Decimal) { Precision = 18, Scale = 4 },
        };

        private static readonly object[] NumbersGiven =
        {
            true, false, "12", "2.5", "1.5", " 7 ", "-3", "1e2", "(5)", new DateTime(2020, 1, 2, 12, 0, 0),
        };

        // What ACE wraps into a Byte and DAO refuses, and an Integer refuses: the engine refuses each, as DAO does.
        private static bool Refused(string type, object value)
        {
            return type == "Byte" && (Equals(value, true) || Equals(value, "-3") || Equals(value, "(5)") || value is DateTime)
                || type == "Integer" && value is DateTime;
        }

        [Fact]
        public void A_bool_text_and_a_date_are_stored_by_the_engine_in_every_number_type_as_ACE_stores_them()
        {
            AccessReferee.SkipUnlessAceHas(LargeNumber);
            var path = Path.ChangeExtension(_tmp.NewMdbPath(), ".accdb");
            using (var db = Database.Create(path, FileFormat.Access2016))
                foreach (var type in NumberTypes)
                {
                    var engine = db.CreateTable("E" + type.Key, new[] { new ColumnDefinition("Id", DataType.Int32), type.Value }, new[] { "Id" });
                    db.CreateTable("A" + type.Key, new[] { new ColumnDefinition("Id", DataType.Int32), type.Value }, new[] { "Id" });
                    for (var i = 0; i < NumbersGiven.Length; i++)
                    {
                        if (Refused(type.Key, NumbersGiven[i]))
                            Assert.Throws<OverflowException>(() => engine.Insert(new Row { ["Id"] = i, ["V"] = NumbersGiven[i] }));
                        else engine.Insert(new Row { ["Id"] = i, ["V"] = NumbersGiven[i] });
                    }
                    Assert.Contains("'V'", Assert.Throws<ArgumentException>(() => engine.Insert(new Row { ["Id"] = 99, ["V"] = Guid.NewGuid() })).Message);
                }

            foreach (var type in NumberTypes.Keys)
            {
                for (var i = 0; i < NumbersGiven.Length; i++)
                    if (!Refused(type, NumbersGiven[i])) AceInsert(path, "A" + type, V("Id", i, "V", NumbersGiven[i]));
                Assert.Throws<OleDbException>(() => AceInsert(path, "A" + type, V("Id", 99, "V", Guid.NewGuid())));
                AssertSame(Written(AceTable(path, "A" + type)), Written(AceTable(path, "E" + type)), type + ": the engine's rows against ACE's");
                if (type != "Byte")   // true is -1, Access's value: through an index of ACE's own, and by a scan
                {
                    Execute(path, "CREATE INDEX ixV ON E" + type + " (V)");
                    Assert.Equal(new object[] { 0 }, AceRows(path, "SELECT Id FROM E" + type + " WHERE V = -1").Select(r => r["Id"]));
                    Assert.Equal(new object[] { 0 }, AceRows(path, "SELECT Id FROM E" + type + " WHERE V + 0 = -1").Select(r => r["Id"]));
                }
            }

            var compacted = Compact(path);
            try
            {
                foreach (var type in NumberTypes.Keys)
                    AssertSame(Written(AceTable(path, "E" + type)), Written(AceTable(compacted, "E" + type)), type + " after DAO compacted the file");
            }
            finally { File.Delete(compacted); }
        }

        // ── The bytes of a table definition (#37, #38) ───────────────────────

        [Theory]
        [InlineData(FileFormat.Access2000, ".mdb")]
        [InlineData(FileFormat.Access2016, ".accdb")]
        public void DAO_changes_the_design_of_tables_the_engine_made_and_compacts_them_whole(FileFormat format, string extension)
        {
            if (format == FileFormat.Access2016) AccessReferee.SkipUnlessAceHas(LargeNumber);
            var path = Path.ChangeExtension(_tmp.NewMdbPath(), extension);
            var columns = new List<ColumnDefinition>
            {
                new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("eBool", DataType.Boolean),
                new ColumnDefinition("eByte", DataType.Byte), new ColumnDefinition("eInt", DataType.Int16),
                new ColumnDefinition("eCur", DataType.Currency), new ColumnDefinition("eSingle", DataType.Single),
                new ColumnDefinition("eDouble", DataType.Double), new ColumnDefinition("eDate", DataType.DateTime),
                new ColumnDefinition("eBin", DataType.Binary) { MaxLength = 10 }, new ColumnDefinition("eText", DataType.Text) { MaxLength = 10 },
                new ColumnDefinition("eOle", DataType.OleObject), new ColumnDefinition("eMemo", DataType.Memo),
                new ColumnDefinition("eGuid", DataType.Guid), new ColumnDefinition("eDec", DataType.Decimal) { Precision = 10, Scale = 2 },
            };
            if (format == FileFormat.Access2016)
            {
                columns.Add(new ColumnDefinition("eBig", DataType.Int64));
                columns.Add(new ColumnDefinition("eDte", DataType.DateTimeExtended));
            }
            using (var db = Database.Create(path, format))
            {
                db.CreateTable("Made", columns, new[] { "Id" }).Insert(new Row { ["Id"] = 1, ["eMemo"] = new string('m', 3000) });
                var added = db.CreateTable("Added", new[] { new ColumnDefinition("Id", DataType.Int32) }, new[] { "Id" });
                added.Insert(new Row { ["Id"] = 1 });
                foreach (var column in columns.Skip(1)) db.GetTable("Added").AddColumn(column);
                db.GetTable("Added").AddIndex(new IndexDefinition("ixDouble", "eDouble"));
            }

            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            dynamic dbe = Activator.CreateInstance(dbeType);
            try
            {
                dynamic dao = dbe.OpenDatabase(path);
                try
                {
                    foreach (var name in new[] { "Made", "Added" })
                    {
                        dynamic table = dao.TableDefs[name];
                        table.Fields.Append(table.CreateField("daoLong", 4));
                        table.Fields.Append(table.CreateField("daoMemo", 12));
                        dynamic index = table.CreateIndex("ixDao");
                        index.Fields.Append(index.CreateField("eSingle"));
                        table.Indexes.Append(index);
                        table.Fields.Delete("eOle");
                        if (name == "Added") table.Indexes.Delete("ixDouble");
                        dao.Execute("INSERT INTO [" + name + "] (Id, eSingle, eMemo, daoMemo) VALUES (2, 1.5, '" + new string('x', 3000) + "', '" + new string('d', 3000) + "')");
                    }
                }
                finally { dao.Close(); }
            }
            finally { Dao.Release(dbe); }

            foreach (var name in new[] { "Made", "Added" })
            {
                AceInsert(path, name, V("Id", 3, "eSingle", 1.5f, "eMemo", new string('a', 2000)));
                Assert.Equal(2, AceRows(path, "SELECT Id FROM [" + name + "] WHERE eSingle = 1.5").Count);
            }
            var compacted = Compact(path);
            try
            {
                foreach (var name in new[] { "Made", "Added" })
                {
                    Assert.Equal(3, DaoRows(compacted, name, "PrimaryKey").Count);
                    Assert.Equal(3, DaoRows(compacted, name, "ixDao").Count);
                    AssertSame(Written(AceTable(path, name)), Written(AceTable(compacted, name)), name + " after DAO compacted the file");
                }
            }
            finally { File.Delete(compacted); }
        }
    }
}
