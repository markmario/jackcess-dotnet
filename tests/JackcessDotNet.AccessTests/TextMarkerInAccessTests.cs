using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Text whose bytes begin 0xFF (#93), written by the engine into a table ACE made, in an .mdb and an .accdb DAO
    /// made: a Text column and a Memo column with Unicode compression and without, a Memo kept in the row, in one
    /// long-value row and in a chain, with an index on each Text column. 0xFF 0xFE is the mark of compressed text, and
    /// the UTF-16LE of U+FEFF is 0xFF 0xFE too: the engine writes text that begins with U+FEFF as ACE writes it, and
    /// text that begins with another character whose low byte is 0xFF as UTF-16LE. ACE, the engine and DAO each read
    /// back what was written, by scan, through the primary key and through each Text column's index, and after a
    /// compact. Text Access itself would read back changed the engine refuses (TextMarkerTests); bytes ACE did not
    /// write itself, planted by the engine, ACE and the engine read alike.
    /// </summary>
    public class TextMarkerInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        public static IEnumerable<object[]> Formats()
        {
            yield return new object[] { 64, ".mdb" };
            yield return new object[] { 128, ".accdb" };
        }

        private static readonly string[] Table =
        {
            "CREATE TABLE T (Id LONG CONSTRAINT pk PRIMARY KEY, TC VARCHAR(255) WITH COMPRESSION, TU VARCHAR(255), "
            + "MC LONGTEXT WITH COMPRESSION, MU LONGTEXT)",
            "CREATE INDEX ixTC ON T (TC)",
            "CREATE INDEX ixTU ON T (TU)",
        };

        private const char Bom = '\uFEFF';
        private static string Of(char c, int count) => new string(c, count);

        // What each column keeps as it was given, written as ACE writes it.
        private static readonly string[] TextCompressed =
        {
            Bom + "abQ1Z",                                  // compressed in runs: U+FEFF in a run of two bytes a character
            Bom + "aaaa",                                   // in runs, as long as UTF-16LE
            Bom + "\u30C6E\u30C6EEEEEE",                    // runs of each kind, twice over
            Bom + Of('\u30C6', 40) + "aaaa",
            Bom + Of(Bom, 1) + "aaaa",
            "\u01FFab",                                     // 0xFF 0x01: UTF-16LE
            "\u01FFQ9Z\u30C6",
        };
        private static readonly string[] TextPlain =
        {
            Bom.ToString(), Bom + "a", Bom + "abc", Bom + "ab\u30C6Q2Z", Bom + Of('\u30C6', 40), Bom + Of('\u4141', 50),
            Bom + "\u0100aaaaaa", "\u01FFab",
        };
        private static readonly string[] MemoCompressed =
        {
            Bom + "abQ1Z",                                  // in the row, in runs
            Bom + "\u30C6E\u30C6EEEEEE",
            Bom + Of('a', 1000),                            // one long-value row, in runs
            Bom + Of('a', 1907),                            // 1,908 characters: the most ACE compresses
            Bom + Of('a', 1950),                            // UTF-16LE of 3,902 bytes: a chain, as ACE makes it
            Bom + Of('\u30C6', 1908),                       // 3,818 bytes: a chain
            Bom + Of('\u30C6', 3000),                       // a chain, UTF-16LE
            "\u01FFab",
        };
        private static readonly string[] MemoPlain =
        {
            Bom + "abQ1Z",                                  // in the row, in runs
            Bom + Of('\u30C6', 40),                         // one long-value row, UTF-16LE
            Bom + Of('\u4141', 50),                         // the bytes of "A" 100 times, compressed
            Bom + Of('a', 1907),
            Bom + Of('\u30C6', 3000),                       // a chain
            "\u01FFab",
            "\u01FFQ9Z\u30C6",
        };

        private static Values Row(int i)
        {
            return V("Id", i + 1,
                     "TC", i < TextCompressed.Length ? TextCompressed[i] : null,
                     "TU", i < TextPlain.Length ? TextPlain[i] : null,
                     "MC", i < MemoCompressed.Length ? MemoCompressed[i] : null,
                     "MU", i < MemoPlain.Length ? MemoPlain[i] : null);
        }

        private string NewFile(int daoVersion, string extension, params string[] statements)
        {
            var path = System.IO.Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, daoVersion);
            Execute(path, statements);
            return path;
        }

        // The ids of the rows ACE finds with "column = text": through the column's index, as a parameter.
        private static List<int> AceFinds(string path, string column, string text)
        {
            var found = new List<int>();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var command = new OleDbCommand("SELECT Id FROM T WHERE [" + column + "] = ?", connection))
            {
                command.Parameters.Add(new OleDbParameter("p", OleDbType.VarWChar) { Value = text });
                connection.Open();
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) found.Add(reader.GetInt32(0));
            }
            return found;
        }

        // Each Text value is found through its column's index, by ACE in the file and in a compacted copy, and by the
        // engine.
        private static void AssertFoundThroughIndexes(string path, List<Values> rows, string when)
        {
            var lookups = (from row in rows
                           from pair in new[] { ("TC", "ixTC"), ("TU", "ixTU") }
                           where row[pair.Item1] is string
                           let text = (string)row[pair.Item1]
                           select (Column: pair.Item1, Index: pair.Item2, Text: text,
                                   Expected: rows.Where(r => r[pair.Item1] as string == text).Select(r => (int)r["Id"]).OrderBy(id => id).ToList()))
                          .ToList();
            var compacted = Compact(path);
            var aceFound = new Dictionary<(string, string), List<int>>();
            try
            {
                foreach (var l in lookups)
                {
                    aceFound[(l.Column, l.Text)] = AceFinds(path, l.Column, l.Text);
                    // Every row of the value, and perhaps others: text whose characters General - Legacy gives no code,
                    // such as U+FEFF and U+4141, is one to Access whatever they are.
                    foreach (var file in new[] { path, compacted })
                    {
                        var found = AceFinds(file, l.Column, l.Text);
                        Assert.True(l.Expected.All(found.Contains), l.Column + " " + string.Join(" ", l.Text.Take(8).Select(c => ((int)c).ToString("X4")))
                            + " found by ACE through " + l.Index + " in rows " + string.Join(",", found) + ", not " + string.Join(",", l.Expected)
                            + (file == path ? ", " : ", after a compact, ") + when);
                    }
                }
            }
            finally { System.IO.File.Delete(compacted); }
            OleDbConnection.ReleaseObjectPool();
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                foreach (var l in lookups)
                    Assert.Contains((int)table.CreateIndexCursor(l.Index).FindRow(l.Column, l.Text)["Id"], aceFound[(l.Column, l.Text)]);
            }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void Access_reads_back_the_text_the_engine_wrote(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension, Table);
            int count = new[] { TextCompressed.Length, TextPlain.Length, MemoCompressed.Length, MemoPlain.Length }.Max();
            var rows = Enumerable.Range(0, count).Select(Row).ToList();
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                foreach (var row in rows) table.Insert(EngineRow(row));
            }

            AssertTableIs(path, "T", "Id", rows, "after the engine inserted");
            AssertSame(Written(rows), Written(DaoRows(path, "T", "pk")), "T as DAO reads it from the table");
            AssertFoundThroughIndexes(path, rows, "after the engine inserted");

            // The other way round on every row: each value in the next row's place.
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                foreach (var row in rows)
                {
                    var next = Row(((int)row["Id"]) % count);
                    next.Remove("Id");
                    table.UpdateByPrimaryKey((int)row["Id"], EngineRow(next));
                    foreach (var pair in next) row[pair.Key] = pair.Value;
                }
            }
            AssertTableIs(path, "T", "Id", rows, "after the engine updated every row");
            AssertSame(Written(rows), Written(DaoRows(path, "T", "pk")), "T as DAO reads it from the table, after the update");
            AssertFoundThroughIndexes(path, rows, "after the engine updated every row");
        }

        /// <summary>
        /// Bytes ACE does not write itself, planted by the engine as UTF-16LE in a Text column without Unicode
        /// compression - which ACE reads as they are - and read again once ACE has given the column Unicode
        /// compression, which leaves the bytes as they are and reads them as compressed text: after a switch to
        /// UTF-16LE two bytes at a time, a pair that begins 0x00 switching back, and 0x00 as a pair's second byte
        /// none. The engine reads them as ACE does, before and after.
        /// </summary>
        [Theory]
        [MemberData(nameof(Formats))]
        public void Planted_bytes_are_read_as_ACE_reads_them_before_and_after_ACE_gives_the_column_compression(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension, "CREATE TABLE P (Id LONG CONSTRAINT pk PRIMARY KEY, V VARCHAR(255))");
            var planted = new[]
            {
                ("\uFEFFaA\u6200c", "aAbc"),          // FF FE 61 00 41 00 00 62 63 00
                ("\uFEFFa\u30C6\u4100", "a\u30C6A"),            // FF FE 61 00 C6 30 00 41
                ("\uFEFFQ4Z", "Q4Z"),                 // FF FE 51 00 34 00 5A 00
            };
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("P");
                for (int i = 0; i < planted.Length; i++) table.Insert(new Row { ["Id"] = i + 1, ["V"] = planted[i].Item1 });
            }

            var before = AssertEngineReadsWhatAceReads(path, "P");
            foreach (var row in before) Assert.Equal(planted[(int)row["Id"] - 1].Item1, row["V"]);

            Execute(path, "ALTER TABLE P ALTER COLUMN V VARCHAR(255) WITH COMPRESSION");
            var after = AssertEngineReadsWhatAceReads(path, "P");
            foreach (var row in after) Assert.Equal(planted[(int)row["Id"] - 1].Item2, row["V"]);
        }
    }
}
