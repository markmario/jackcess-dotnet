using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// The rows the managed engine finds for a value - by a cursor over the table and through an index - are the rows
    /// Access's <c>=</c> finds through the same index, in a table the engine wrote (#49).
    /// Text is matched as text and never through a number it reads as, a text primary key whose keys all read as 1
    /// among it; a number as the same number across number types, a double asked of a Currency by its exact value to
    /// four places, half to even. ACE is asked with a parameter of the caller's type. Where ACE's scan and its index
    /// answer differently, the engine gives the exact answer, and ACE is asked the way it gives that one: through a
    /// Double's or a Single's index, which tells -0 from 0 and a single from a double where ACE's scan does not; and
    /// of a copy of a whole-number column with no index, whose scan finds 0 for -0 and nothing for a decimal with a
    /// fraction, where ACE's index finds nothing for the one and rounds the other.
    /// </summary>
    public class LookupsInAccessTests : IDisposable
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

        // The ids of every row a cursor over the table finds with the value, and every row the index has for it.
        private static int[] ByScan(Table table, string column, object value)
        {
            var ids = new List<int>();
            var cursor = table.CreateCursor();
            for (var row = cursor.FindRow(column, value); row != null; row = cursor.FindNextRow(column, value))
                ids.Add((int)row["Id"]);
            return ids.OrderBy(id => id).ToArray();
        }

        private static int[] ByIndex(Table table, string index, object value)
            => table.CreateIndexCursor(index).FindRowsByEntry(value).Select(row => (int)row["Id"]).OrderBy(id => id).ToArray();

        // The ids ACE's = finds, the value given as a parameter of its own type.
        private static int[] AceIds(OleDbConnection connection, string table, string column, object value)
        {
            using (var command = new OleDbCommand("SELECT Id FROM [" + table + "] WHERE [" + column + "] = ?", connection))
            {
                command.Parameters.Add(Parameter(value));
                var ids = new List<int>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) ids.Add(reader.GetInt32(0));
                return ids.OrderBy(id => id).ToArray();
            }
        }

        private static OleDbParameter Parameter(object value)
        {
            switch (value)
            {
                case short s:   return new OleDbParameter("p", OleDbType.SmallInt) { Value = s };
                case int i:     return new OleDbParameter("p", OleDbType.Integer) { Value = i };
                case double d:  return new OleDbParameter("p", OleDbType.Double) { Value = d };
                case float f:   return new OleDbParameter("p", OleDbType.Single) { Value = f };
                case decimal m: return new OleDbParameter("p", OleDbType.Decimal) { Value = m, Precision = 28, Scale = (byte)((decimal.GetBits(m)[3] >> 16) & 0xFF) };
                case string t:  return new OleDbParameter("p", OleDbType.VarWChar) { Value = t };
                default: throw new ArgumentException("No parameter type for " + value.GetType().Name);
            }
        }

        private static string Show(object value) => value is string ? "'" + value + "'" : value + " (" + value.GetType().Name + ")";

        private static string Ids(int[] ids) => "[" + string.Join(", ", ids) + "]";

        [Theory]
        [MemberData(nameof(Formats))]
        public void the_engine_finds_the_text_access_finds_and_no_text_through_a_number_it_reads_as(int daoVersion, string extension)
        {
            var path = NewDatabase(daoVersion, extension);
            using (var db = Database.Open(path))
            {
                var table = db.CreateTable("T", new[]
                {
                    new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Code", DataType.Text) { MaxLength = 20 },
                }, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") });
                var codes = new[] { "1", "5", "1.0", " 5", "01", "+1" };
                for (var i = 0; i < codes.Length; i++) table.Insert(new Row { ["Id"] = i + 1, ["Code"] = codes[i] });
            }

            var asked = new object[] { "1", "1.0", " 1", "01", "+1", "1.00", "5", " 5", "5.0", "005", "1 ", 1, 1.0, 5 };
            // Opened only to be read, so that ACE opens the file beside it (#62).
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                connection.Open();
                var table = db.GetTable("T");
                foreach (var value in asked)
                {
                    var ace = AceIds(connection, "T", "Code", value);
                    Assert.True(ace.SequenceEqual(ByScan(table, "Code", value)), "Code = " + Show(value) + ": ACE " + Ids(ace) + ", the engine by a scan " + Ids(ByScan(table, "Code", value)));
                    Assert.True(ace.SequenceEqual(ByIndex(table, "ByCode", value)), "Code = " + Show(value) + ": ACE " + Ids(ace) + ", the engine through the index " + Ids(ByIndex(table, "ByCode", value)));
                }
                // What the cases are: text is not a number to ACE either.
                Assert.Equal(new[] { 3 }, AceIds(connection, "T", "Code", "1.0"));
                Assert.Equal(new[] { 1 }, AceIds(connection, "T", "Code", 1));
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void a_row_the_engine_finds_updates_and_deletes_by_its_own_text_key_is_the_row_access_has_under_it(int daoVersion, string extension)
        {
            var path = NewDatabase(daoVersion, extension);
            var keys = new[] { "1", "1.0", "01", "+1", " 1" };   // the number 1, five times, to a comparison through decimals
            using (var db = Database.Open(path))
            {
                var table = db.CreateTable("K", new[]
                {
                    new ColumnDefinition("Code", DataType.Text) { MaxLength = 10 }, new ColumnDefinition("Value", DataType.Int32),
                }, "Code");
                for (var i = 0; i < keys.Length; i++) table.Insert(new Row { ["Code"] = keys[i], ["Value"] = i });

                foreach (var key in keys) Assert.Equal(key, table.FindRowByPrimaryKey(key)["Code"]);
                var row = table.FindRowByPrimaryKey("1.0");
                row["Value"] = 100;
                table.Update(row);
                table.Delete(table.FindRowByPrimaryKey("01"));
                var cursor = table.CreateIndexCursor();
                cursor.FindRowByEntry("+1");
                cursor.UpdateCurrentRow(new Row { ["Value"] = 300 });
            }

            // Access has each row under its own key, with the change made to it and to no other.
            var expected = new[] { V("Code", "1", "Value", 0), V("Code", "1.0", "Value", 100), V("Code", "+1", "Value", 300), V("Code", " 1", "Value", 4) };
            AssertSame(Written(expected), Written(AceTable(path, "K")), "K as ACE reads it");
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var row in expected)
                    using (var command = new OleDbCommand("SELECT [Value] FROM K WHERE Code = ?", connection))
                    {
                        command.Parameters.Add(Parameter(row["Code"]));
                        Assert.Equal(row["Value"], command.ExecuteScalar());
                    }
                using (var command = new OleDbCommand("SELECT COUNT(*) FROM K WHERE Code = '01'", connection))
                    Assert.Equal(0, command.ExecuteScalar());
            }
            var compacted = Compact(path);
            try { AssertSame(Written(expected), Written(AceTable(compacted, "K")), "K after DAO compacted the file"); }
            finally { File.Delete(compacted); }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void the_engine_finds_the_numbers_access_finds_as_the_same_number(int daoVersion, string extension)
        {
            var path = NewDatabase(daoVersion, extension);
            using (var db = Database.Open(path))
            {
                var table = db.CreateTable("N", new[]
                {
                    new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("I", DataType.Int16), new ColumnDefinition("L", DataType.Int32),
                    new ColumnDefinition("C", DataType.Currency), new ColumnDefinition("D", DataType.Decimal) { Precision = 18, Scale = 4 },
                    new ColumnDefinition("F", DataType.Double), new ColumnDefinition("S", DataType.Single),
                    new ColumnDefinition("In", DataType.Int16),   // I again, with no index: what ACE's scan finds
                }, new[] { "Id" }, new[] { "I", "L", "C", "D", "F", "S" }.Where(c => c != "D").Select(c => new IndexDefinition("By" + c, c)));
                table.Insert(new Row { ["Id"] = 1, ["I"] = (short)5, ["In"] = (short)5, ["L"] = 5, ["C"] = 1.2346m, ["D"] = 1.5m, ["F"] = 0.1, ["S"] = 0.1f });
                table.Insert(new Row { ["Id"] = 2, ["I"] = (short)6, ["In"] = (short)6, ["L"] = 6, ["C"] = 1.2345m, ["D"] = 0.1m, ["F"] = -0.0, ["S"] = 16777216f });
                table.Insert(new Row { ["Id"] = 3, ["I"] = (short)7, ["In"] = (short)7, ["L"] = int.MaxValue, ["C"] = -1.2346m, ["D"] = 1.2346m, ["F"] = 1.2346, ["S"] = 1.2346f });
                table.Insert(new Row { ["Id"] = 4, ["I"] = (short)0, ["In"] = (short)0, ["C"] = 0.0001m });
                table.Insert(new Row { ["Id"] = 5, ["I"] = (short)-1, ["In"] = (short)-1, ["C"] = 0.0002m });
                var halves = new[] { 0.0312m, 0.0313m, -0.0312m, -0.0313m, 0.0937m, 0.0938m, 562949953421312.5m };
                for (var i = 0; i < halves.Length; i++) table.Insert(new Row { ["Id"] = 6 + i, ["C"] = halves[i] });
            }

            var asked = new (string Column, object Value)[]
            {
                ("I", (short)5), ("I", 5), ("I", 5.0), ("I", 5m), ("I", 5.5), ("I", 70000), ("I", 65541), ("I", 5.000000000000001), ("I", 4.999999999999999), ("I", 5.0000005f),
                ("L", 5.0), ("L", 5.5), ("L", 6.5), ("L", 5.000000000000001), ("L", 2147483647),
                ("C", 1.2346m), ("C", 1.2346), ("C", 1.23456m), ("C", 1.23456), ("C", 1.23455m), ("C", 1.23454m), ("C", 1.234549m),
                ("C", -1.23455m), ("C", -1.23455), ("C", 1.2346f), ("C", 1),
                // A double or a single by its exact value, to four places half to even (#49): just under a half, exact
                // halves, and one of more significant digits than a decimal of 15 keeps.
                ("C", 0.00015), ("C", 1.234549999999999), ("C", 0.03125), ("C", -0.03125), ("C", 0.09375),
                ("C", 0.03125f), ("C", -0.03125f), ("C", 562949953421312.5),
                ("D", 1.5m), ("D", 1.50m), ("D", 1.5), ("D", 0.1), ("D", 0.1f), ("D", 1.23456m), ("D", 1.2346),
                ("F", 0.1), ("F", 0.1m), ("F", 0.1f), ("F", -0.0), ("F", 0.0), ("F", 1.2346m),
                ("S", 0.1f), ("S", 0.1), ("S", (double)0.1f), ("S", 1.2346m), ("S", 16777217.0), ("S", 0f),
            };
            // Opened only to be read, so that ACE opens the file beside it (#62).
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                connection.Open();
                var table = db.GetTable("N");
                foreach (var (column, value) in asked)
                {
                    var what = column + " = " + Show(value);
                    var ace = AceIds(connection, "N", column, value);
                    Assert.True(ace.SequenceEqual(ByScan(table, column, value)), what + ": ACE " + Ids(ace) + ", the engine by a scan " + Ids(ByScan(table, column, value)));
                    if (column != "D")   // a Decimal has no index the engine keeps
                        Assert.True(ace.SequenceEqual(ByIndex(table, "By" + column, value)), what + ": ACE " + Ids(ace) + ", the engine through the index " + Ids(ByIndex(table, "By" + column, value)));
                }

                // What the cases are: a Currency is the value taken to its four places; a number not whole is no
                // Integer's. And where the engine and ACE part: ACE reads text asked of a number column as a number,
                // and the engine does not read text as a number (a rule of its cursors; #49).
                Assert.Equal(new[] { 1 }, AceIds(connection, "N", "C", 1.23456m));
                Assert.Empty(AceIds(connection, "N", "I", 5.000000000000001));
                Assert.Equal(new[] { 1 }, AceIds(connection, "N", "I", "5"));
                Assert.Empty(ByScan(table, "I", "5"));
                Assert.Empty(ByIndex(table, "ByI", "5"));
                // A double asked of a Currency is its exact value, half to even: the double 0.00015, just under its half,
                // is 0.0001 and 1.234549999999999 is 1.2345, where a decimal of 15 digits took them up; 0.03125 is 0.0312.
                Assert.Equal(new[] { 4 }, AceIds(connection, "N", "C", 0.00015));
                Assert.Equal(new[] { 2 }, AceIds(connection, "N", "C", 1.234549999999999));
                Assert.Equal(new[] { 6 }, AceIds(connection, "N", "C", 0.03125));
                Assert.Equal(new[] { 8 }, AceIds(connection, "N", "C", -0.03125));
                Assert.Equal(new[] { 11 }, AceIds(connection, "N", "C", 0.09375));

                // Where ACE's index of a whole-number column and its scan differ, the engine - by a scan and through its
                // index - gives the exact answer, the scan's: -0 is 0, and a decimal with a fraction is no Integer's,
                // where ACE's index finds nothing for -0 and rounds the decimal to a whole number.
                foreach (var (value, exact, aceIndex) in new (object Value, int[] Exact, int[] AceIndex)[]
                {
                    (-0.0, new[] { 4 }, new int[0]), (-0.0f, new[] { 4 }, new int[0]),
                    (5.5m, new int[0], new[] { 2 }), (0.1m, new int[0], new[] { 4 }), (-1.23465m, new int[0], new[] { 5 }),
                })
                {
                    var what = "I = " + Show(value);
                    Assert.True(exact.SequenceEqual(AceIds(connection, "N", "In", value)), what + ": ACE's scan " + Ids(AceIds(connection, "N", "In", value)));
                    Assert.True(aceIndex.SequenceEqual(AceIds(connection, "N", "I", value)), what + ": ACE through the index " + Ids(AceIds(connection, "N", "I", value)));
                    Assert.True(exact.SequenceEqual(ByScan(table, "I", value)), what + ": the engine by a scan " + Ids(ByScan(table, "I", value)));
                    Assert.True(exact.SequenceEqual(ByIndex(table, "ByI", value)), what + ": the engine through the index " + Ids(ByIndex(table, "ByI", value)));
                }
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void a_row_the_engine_changes_by_a_double_asked_of_its_currency_key_is_the_row_access_changes(int daoVersion, string extension)
        {
            var path = NewDatabase(daoVersion, extension);
            var keys = new[] { 0.0001m, 0.0002m, 1.2345m, 1.2346m, 0.0312m, 0.0313m };
            using (var db = Database.Open(path))
            {
                var table = db.CreateTable("K", new[]
                {
                    new ColumnDefinition("C", DataType.Currency), new ColumnDefinition("Value", DataType.Int32),
                }, "C");
                for (var i = 0; i < keys.Length; i++) table.Insert(new Row { ["C"] = keys[i], ["Value"] = i });
            }
            var twin = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            File.Copy(path, twin);

            // The engine changes the rows it finds by three doubles - one just under a half of the fifth place, one an
            // exact half - by its primary key and through an index cursor ...
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("K");
                var row = table.FindRowByPrimaryKey(0.00015);
                row["Value"] = 100;
                table.Update(row);
                table.Delete(table.FindRowByPrimaryKey(1.234549999999999));
                var cursor = table.CreateIndexCursor();
                cursor.FindRowByEntry(0.03125);
                cursor.UpdateCurrentRow(new Row { ["Value"] = 300 });
            }
            // ... and ACE the rows its = finds by the same doubles, in a copy of the file as it was.
            AceUpdate(twin, "K", "C", 0.00015, V("Value", 100));
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(twin)))
            using (var command = new OleDbCommand("DELETE FROM K WHERE C = ?", connection))
            {
                connection.Open();
                command.Parameters.Add(Parameter(1.234549999999999));
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            AceUpdate(twin, "K", "C", 0.03125, V("Value", 300));

            var expected = new[]
            {
                V("C", 0.0001m, "Value", 100), V("C", 0.0002m, "Value", 1), V("C", 1.2346m, "Value", 3),
                V("C", 0.0312m, "Value", 300), V("C", 0.0313m, "Value", 5),
            };
            AssertSame(Written(expected), Written(AceTable(twin, "K")), "K as ACE changed it");
            AssertSame(Written(expected), Written(AceTable(path, "K")), "K as the engine changed it, read by ACE");
        }
    }
}
