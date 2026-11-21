using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Text indexes of Access 97 files (the engine's #23). An Access 97 file is only read, and what the engine makes
    /// that order's keys for is to find a row through a text index instead of reading the table for it: a key that
    /// is not the one in the file finds nothing. Microsoft's engine is the referee, and only a 32-bit one makes or
    /// opens an Access 97 file - the ACE 12.0 of Access 2007, or Jet 4.0 - so these run in a 32-bit process
    /// and are skipped, saying so, anywhere else: 64-bit ACE 16, and the 32-bit ACE 16 of the Access Database Engine
    /// 2016 redistributable, refuse such a file ("Cannot open a database created with a previous version of your
    /// application"; AccessReferee.SkipUnlessAceOpensAccess97).
    /// </summary>
    public class Access97InAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        private const int DaoVersion30 = 32;   // dbVersion30: the Jet3 format of Access 97

        /// <summary>
        /// A new Access 97 database made by DAO, or the test skipped where DAO's engine makes none. DAO's engine for
        /// an Access 97 file is ACE's (DAO.DBEngine.120) where it is installed, else Jet 4.0's (DAO.DBEngine.36),
        /// which a 32-bit Windows has; every DAO call of these tests uses <see cref="Dao.NewEngine"/>, which starts
        /// it with the user's workgroup file, so that a test of another class that runs after these in the process
        /// is not kept from a file's system tables.
        /// </summary>
        private string NewAccess97Database()
        {
            var path = _tmp.NewMdbPath();
            var dbe = Dao.Engine;
            var dbeType = dbe.GetType();
            string why = null;
            try
            {
                var db = dbeType.InvokeMember("CreateDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, ";LANGID=0x0409;CP=1252;COUNTRY=0", DaoVersion30 });
                db.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, db, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(db);
            }
            catch (TargetInvocationException e) { why = e.InnerException.Message; }
            finally { Dao.Release(dbe); }
            if (why != null)
                Assert.Skip("DAO's engine in this process makes no Access 97 file, so there is no referee here for one: " + why);
            return path;
        }

        /// <summary>The characters of code page 1252, which is what an Access 97 file's text is kept in: one for each byte but 0.</summary>
        private static List<string> CodePageCharacters()
        {
            var codePage = Encoding.GetEncoding(1252);
            return Enumerable.Range(1, 255).Select(b => codePage.GetString(new[] { (byte)b })).ToList();
        }

        /// <summary>
        /// ACE keys every character of the code page alone, every pair of them, and 5,000 values of 3 to 12 of
        /// them, in an Access 97 file it makes; the engine then finds each of the 70,000 through the index. It
        /// finds a value only where the key it makes is the key ACE wrote - a cursor that has an index to look in
        /// does not read the table - and reading the table for each would take hours, not the minutes allowed.
        /// </summary>
        [Fact]
        public void the_engine_finds_through_the_index_every_value_ace_keyed_in_an_access_97_file()
        {
            var path = NewAccess97Database();

            var characters = CodePageCharacters();
            var values = new List<string>(characters);
            values.AddRange(from a in characters from b in characters select a + b);
            var random = new Random(1997);
            for (var i = 0; i < 5000; i++)
                values.Add(string.Concat(Enumerable.Range(0, random.Next(3, 13)).Select(_ => characters[random.Next(characters.Count)])));
            values = values.Distinct(StringComparer.Ordinal).ToList();

            dynamic dbe = Dao.Engine;
            try
            {
                dynamic db = dbe.OpenDatabase(path);
                try
                {
                    db.Execute("CREATE TABLE K (N LONG, T TEXT(255))");
                    db.Execute("CREATE INDEX ByText ON K (T)");
                    dynamic rs = db.OpenRecordset("K", 1 /* dbOpenTable */);
                    try
                    {
                        dynamic number = rs.Fields["N"], text = rs.Fields["T"];
                        for (var i = 0; i < values.Count; i++)
                        {
                            rs.AddNew();
                            number.Value = i;
                            text.Value = values[i];
                            rs.Update();
                        }
                    }
                    finally { rs.Close(); }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }

            Func<string, string> shown = text => text == null ? "nothing" : "U+" + string.Join(" U+", text.Select(c => ((int)c).ToString("X4")));
            var notFound = new List<string>();
            var clock = Stopwatch.StartNew();
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("K");
                Assert.Equal(values.Count, table.RowCount);
                var cursor = table.CreateIndexCursor("ByText");
                for (var i = 0; i < values.Count; i++)
                {
                    var row = cursor.FindRow("T", values[i]);
                    // A cursor takes a text for another that reads as the same number (the engine's #49): asked
                    // for a tab and 0 it may come back with the row of a line feed and 0, which the index, where
                    // the two have one key, rightly led it to. That row is let pass here; no other is.
                    decimal asked, got;
                    var sameNumber = row != null
                        && decimal.TryParse(values[i], out asked) && decimal.TryParse((string)row["T"], out got) && asked == got;
                    if (row == null || !(Equals(row["N"], i) || sameNumber))
                        notFound.Add(shown(values[i]) + " (row " + i + "): found " + (row == null ? "nothing" : "row " + row["N"] + ", " + shown((string)row["T"])));
                }
            }
            clock.Stop();

            Assert.True(notFound.Count == 0, notFound.Count + " of " + values.Count + " values are not found through the index:\n" + string.Join("\n", notFound.Take(12)));
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(5), values.Count + " rows took " + clock.Elapsed + " to find: the table was read for them");
        }

        private static string CorpusRoot()
        {
            return typeof(Access97InAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "EngineCorpusDir").Value;
        }

        public static IEnumerable<object[]> Access97Files()
        {
            return Directory.EnumerateFiles(Path.Combine(CorpusRoot(), "V1997"), "*.mdb").Select(f => new object[] { Path.GetFileName(f) }).OrderBy(f => (string)f[0], StringComparer.Ordinal);
        }

        /// <summary>
        /// The files Access 97 itself wrote: every row of every table that has an index on one text column is found
        /// by the engine through that index, by the text it has, and ACE, asked for the same text, finds it too.
        /// </summary>
        [Theory]
        [MemberData(nameof(Access97Files))]
        public void the_engine_finds_by_its_text_every_row_of_a_file_access_97_wrote_as_ace_does(string file)
        {
            AccessReferee.SkipUnlessAceOpensAccess97();

            var path = _tmp.NewMdbPath();
            File.Copy(Path.Combine(CorpusRoot(), "V1997", file), path);

            int lookups = 0, asked = 0;
            using (var db = Database.Open(path))
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var name in db.GetTableNames())
                {
                    var table = db.GetTable(name);
                    foreach (var index in table.Indexes.Where(ix => ix.Columns.Count == 1 && ix.Columns[0].Column.DataType == DataType.Text))
                    {
                        var column = index.Columns[0].Column.Name;
                        var texts = table.Rows.Select(row => row[column] as string).Where(text => text != null).Distinct(StringComparer.Ordinal).ToList();
                        var cursor = table.CreateIndexCursor(index.Name);
                        for (var i = 0; i < texts.Count; i++)
                        {
                            var found = cursor.FindRow(column, texts[i]);
                            Assert.True(found != null && Equals(found[column], texts[i]), file + "/" + name + "/" + index.Name + ": the engine does not find '" + texts[i] + "'");
                            lookups++;

                            if (i % 25 != 0) continue;
                            using (var command = new OleDbCommand("SELECT COUNT(*) FROM [" + name + "] WHERE [" + column + "] = ?", connection))
                            {
                                command.Parameters.Add("p", OleDbType.VarWChar, 255).Value = texts[i];
                                Assert.True(Convert.ToInt32(command.ExecuteScalar()) >= 1, file + "/" + name + "/" + index.Name + ": ACE does not find '" + texts[i] + "'");
                            }
                            asked++;
                        }
                    }
                }
            }

            if (file == "indexCodesV1997.mdb")
                Assert.True(lookups > 1000 && asked > 40, file + ": " + lookups + " rows looked for, " + asked + " asked of ACE");
        }
    }
}
