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
    /// A Memo value stored as Access stores it (#87), in a column with Unicode compression, refereed by Access on each
    /// side of every boundary of how it is kept: no bytes for "", 1 or 2 characters as UTF-16LE, 3 to 1,908 characters
    /// of 0x01 to 0xFF compressed to a byte each in one long-value row, 1,909 and more as UTF-16LE, a chain from 3,818
    /// bytes, and text with a character past 0xFF as UTF-16LE. The unit tests read the bytes in the file
    /// (MemoCompressionTests); here ACE reads every such value the engine writes, in a table the engine made and in one
    /// ACE made, by scan and after DAO compacts the file, and the engine reads each one ACE writes. Then each value is
    /// moved across its boundary - by the engine and by ACE in turn - and both read what the other wrote.
    /// </summary>
    public class MemoStorageInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        /// <summary>Text of 0x01 to 0xFF of <paramref name="length"/> characters, different for each <paramref name="seed"/>, with letters past ASCII.</summary>
        private static string Latin(int length, int seed)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 .,;:!?'\"-()éüßñÅÿ©µ";
            return new string(Enumerable.Range(0, length).Select(i => alphabet[(i * 7 + seed) % alphabet.Length]).ToArray());
        }

        /// <summary>The values, each by its id: the boundaries of how a memo is kept, and a value either side of each.</summary>
        private static readonly string[] Memos =
        {
            "",
            "a",
            "aa",
            "aaa",
            Latin(255, 1),
            Latin(1907, 2),
            Latin(1908, 3),
            Latin(1909, 4),
            Latin(5000, 5),                                 // a chain
            "é€x",                                // the euro sign is past 0xFF
            "ααα",
            new string('Ω', 1908),
            "ÿ" + Latin(1906, 6) + "Ā",           // 1,908 characters, the last past 0xFF
            "abc α def α ghi",                    // mixed, which ACE compresses in runs and the engine does not
        };

        /// <summary>The value each one is moved to: the other side of its boundary.</summary>
        private static string Across(string memo)
        {
            switch (memo.Length)
            {
                case 0: return "aaa";
                case 1: return "";
                case 2: return "aaa";
                case 3: return memo[0] == 'a' ? "aa" : memo + "α";
                case 1907: return memo + "z";
                case 1908: return memo[0] == 'Ω' || memo[memo.Length - 1] == 'Ā' ? memo.Substring(0, 1907) : memo + "z";
                case 1909: return memo.Substring(0, 1908);
                case 5000: return memo.Substring(0, 1000);
                default: return memo.Length > 3 && memo.All(c => c >= 1 && c <= 0xFF) ? memo + "€" : Latin(memo.Length, 7);
            }
        }

        public static IEnumerable<object[]> Kinds()
        {
            foreach (var extension in new[] { ".mdb", ".accdb" })
                foreach (var accessMade in new[] { false, true })
                    yield return new object[] { extension, accessMade };
        }

        /// <summary>
        /// A database of <paramref name="extension"/>'s format the engine makes, with the table <paramref name="table"/>
        /// (Id, Notes) made by the engine - whose Memo columns have Unicode compression, as Access's new ones do - or by
        /// ACE's DDL, asked for it.
        /// </summary>
        private static void NewTable(string path, string table, bool accessMade)
        {
            if (!File.Exists(path))
                Database.Create(path, Path.GetExtension(path) == ".mdb" ? FileFormat.Access2000 : FileFormat.Access2007).Dispose();
            if (accessMade)
            {
                Execute(path, "CREATE TABLE [" + table + "] (Id LONG CONSTRAINT pk PRIMARY KEY, Notes MEMO WITH COMPRESSION)");
                return;
            }
            using (var db = Database.Open(path))
                db.CreateTable(table, new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) }, "Id");
        }

        private static List<Values> Rows(Func<string, string> change = null)
        {
            return Memos.Select((memo, i) => V("Id", i + 1, "Notes", change == null ? memo : change(memo))).ToList();
        }

        /// <summary>ACE reads each memo as <paramref name="expected"/> holds it, character for character.</summary>
        private static void AssertAceReads(string path, string table, List<Values> expected, string when)
        {
            var read = AceRows(path, "SELECT Id, Notes, Len(Notes) AS Chars FROM [" + table + "] ORDER BY Id");
            Assert.Equal(expected.Count, read.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var memo = (string)expected[i]["Notes"];
                var got = read[i]["Notes"] as string ?? "";
                Assert.True(memo == got && Convert.ToInt32(read[i]["Chars"] ?? 0) == memo.Length,
                    when + ": ACE reads the memo of " + memo.Length + " characters of row " + expected[i]["Id"] + " as one of " + got.Length
                    + (memo == got ? "" : ", differing first at " + Enumerable.Range(0, Math.Min(memo.Length, got.Length)).FirstOrDefault(k => memo[k] != got[k])));
            }
        }

        private static void AssertEngineReads(string path, string table, List<Values> expected, string when)
        {
            using (var db = Database.Open(path))
            {
                var read = db.GetTable(table).Rows.ToDictionary(r => (int)r["Id"], r => r["Notes"] as string ?? "");
                foreach (var row in expected)
                    Assert.True((string)row["Notes"] == read[(int)row["Id"]],
                        when + ": the engine reads the memo of " + ((string)row["Notes"]).Length + " characters of row " + row["Id"] + " as one of " + read[(int)row["Id"]].Length);
            }
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void ace_reads_every_memo_the_engine_writes_and_the_engine_every_one_ace_writes_on_each_side_of_every_boundary(string extension, bool accessMade)
        {
            var path = Path.ChangeExtension(_tmp.NewMdbPath(), extension);
            NewTable(path, "ByEngine", accessMade);
            NewTable(path, "ByAce", accessMade);
            var rows = Rows();
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("ByEngine");
                foreach (var row in rows) table.Insert(EngineRow(row));
            }
            foreach (var row in rows) AceInsert(path, "ByAce", row);

            AssertAceReads(path, "ByEngine", rows, "as the engine wrote it");
            AssertEngineReads(path, "ByAce", rows, "as ACE wrote it");
            AssertEngineReads(path, "ByEngine", rows, "as the engine wrote it");

            var compacted = Compact(path);
            try
            {
                AssertAceReads(compacted, "ByEngine", rows, "after DAO compacted the file");
                AssertEngineReads(compacted, "ByEngine", rows, "after DAO compacted the file");
                AssertEngineReads(compacted, "ByAce", rows, "after DAO compacted the file");
            }
            finally { File.Delete(compacted); }
        }

        [Theory]
        [InlineData(".mdb")]
        [InlineData(".accdb")]
        public void a_memo_moved_across_its_boundary_by_the_engine_or_by_ace_is_read_by_the_other(string extension)
        {
            var path = Path.ChangeExtension(_tmp.NewMdbPath(), extension);
            NewTable(path, "M", accessMade: false);
            var rows = Rows();
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("M");
                foreach (var row in rows) table.Insert(EngineRow(row));
            }

            // The engine moves each across its boundary, and ACE reads it.
            var moved = Rows(Across);
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("M");
                var cursor = table.CreateCursor();
                foreach (var row in moved)
                {
                    Assert.NotNull(cursor.FindRow("Id", row["Id"]));
                    cursor.UpdateCurrentRow(new Row { ["Notes"] = row["Notes"] });
                }
            }
            AssertAceReads(path, "M", moved, "after the engine moved each memo across its boundary");

            // ACE moves each back over the value the engine wrote, and the engine reads it.
            foreach (var row in rows) AceUpdate(path, "M", "Id", row["Id"], V("Notes", row["Notes"]));
            AssertEngineReads(path, "M", rows, "after ACE moved each memo back");

            // The engine moves each across again over the value ACE wrote, and ACE reads it, before and after DAO compacts.
            using (var db = Database.Open(path))
            {
                var cursor = db.GetTable("M").CreateCursor();
                foreach (var row in moved)
                {
                    Assert.NotNull(cursor.FindRow("Id", row["Id"]));
                    cursor.UpdateCurrentRow(new Row { ["Notes"] = row["Notes"] });
                }
            }
            AssertAceReads(path, "M", moved, "after the engine moved each memo ACE wrote across its boundary");
            var compacted = Compact(path);
            try { AssertAceReads(compacted, "M", moved, "after DAO compacted the file"); }
            finally { File.Delete(compacted); }
        }
    }
}
