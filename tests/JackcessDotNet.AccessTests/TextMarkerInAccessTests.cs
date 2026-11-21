using System;
using System.Collections.Generic;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Text whose bytes begin 0xFF (#93), written by the engine into a table ACE made, in an .mdb and an .accdb DAO
    /// made: a Text column and a Memo column with Unicode compression and without, a Memo kept in the row, in one
    /// long-value row and in a chain. 0xFF 0xFE is the mark of compressed text, and the UTF-16LE of U+FEFF is 0xFF 0xFE
    /// too: the engine writes text that begins with U+FEFF as ACE writes it, and text that begins with another
    /// character whose low byte is 0xFF as UTF-16LE, which it read as text of one byte a character. ACE, the engine
    /// and DAO each read back what was written, by scan, through the primary key and after a compact. Text that
    /// begins with U+FEFF that Access itself does not keep - it writes UTF-16LE and reads it back as compressed text -
    /// the engine reads as ACE reads it.
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

        private const string Table = "CREATE TABLE T (Id LONG CONSTRAINT pk PRIMARY KEY, TC VARCHAR(255) WITH COMPRESSION, TU VARCHAR(255), "
                                   + "MC LONGTEXT WITH COMPRESSION, MU LONGTEXT)";

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
            Bom + Of('a', 3000),
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

        private string NewFile(int daoVersion, string extension)
        {
            var path = System.IO.Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, daoVersion);
            Execute(path, Table);
            return path;
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void Access_reads_back_the_text_the_engine_wrote(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension);
            int count = new[] { TextCompressed.Length, TextPlain.Length, MemoCompressed.Length, MemoPlain.Length }.Max();
            var rows = Enumerable.Range(0, count).Select(Row).ToList();
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                foreach (var row in rows) table.Insert(EngineRow(row));
            }

            AssertTableIs(path, "T", "Id", rows, "after the engine inserted");
            AssertSame(Written(rows), Written(DaoRows(path, "T", "pk")), "T as DAO reads it from the table");

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
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void Text_Access_does_not_keep_is_read_as_Access_reads_it(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension);
            var given = new[]
            {
                V("Id", 1, "TC", Bom.ToString(), "MC", Bom + "aaaa", "MU", Bom + "abc"),
                V("Id", 2, "TC", Bom + "abc", "MC", Bom + Of('\u30C6', 40), "MU", Bom + Of('\u30C6', 3)),
                V("Id", 3, "TC", Bom + "ab\u30C6Q2Z", "MC", Bom + Of('\u4141', 50), "MU", Bom + "\u0100aaaaaa"),
                V("Id", 4, "TC", Bom + Of('\u30C6', 3), "MC", Bom + "aaa" + "\u0000aaa", "MU", Bom + "aaaa"),
            };
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                foreach (var row in given) table.Insert(EngineRow(row));
            }

            var ace = AssertEngineReadsWhatAceReads(path, "T");
            AssertSame(Written(ace), Written(DaoRows(path, "T", "pk")), "T as DAO reads it from the table");
            // None of it is read back as it was given.
            foreach (var row in ace)
                foreach (var column in new[] { "TC", "MC", "MU" })
                    Assert.NotEqual(given[(int)row["Id"] - 1][column], row[column]);
        }
    }
}
