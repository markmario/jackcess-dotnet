using System;
using System.IO;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Overflow rows, with ACE as the referee. When an update makes a row too long for
    /// the room its page has, Access moves the row's values to another page and leaves, at the row's slot, four bytes
    /// saying where. The engine passed such a slot over: the row was missing from a read of the table, could not be
    /// found to update or delete, and an index entry for it led nowhere. ACE makes the overflow rows here, the engine
    /// reads, finds, updates and deletes them, and ACE reads the table and answers through its indexes afterwards.
    /// </summary>
    public class OverflowRowsInAccessTests : IDisposable
    {
        private TempDb _tmp;

        public OverflowRowsInAccessTests() { _tmp = new TempDb(); }
        public void Dispose() { _tmp.Dispose(); }

        // Not compressible, so a row is as long as it looks.
        private static string Wide(int id, int length) { return id.ToString("D3") + new string('Ω', length - 3); }

        /// <summary>The slots of <paramref name="table"/>'s pages that hold a pointer to where their row went.</summary>
        private static int OverflowRows(string path, string table)
        {
            // The page of the table's definition, which the engine keeps to itself.
            int definition;
            using (var db = Database.Open(path))
            {
                var itsDefinition = typeof(Table).GetProperty("Definition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(db.GetTable(table));
                definition = (int)itsDefinition.GetType().GetProperty("TdefPageNumber").GetValue(itsDefinition);
            }
            var file = File.ReadAllBytes(path);
            var count = 0;
            for (var at = 0; at + 4096 <= file.Length; at += 4096)
            {
                if (file[at] != 1 || BitConverter.ToInt32(file, at + 4) != definition) continue;
                for (var row = 0; row < BitConverter.ToInt16(file, at + 12); row++)
                    if ((BitConverter.ToUInt16(file, at + 14 + row * 2) & 0xC000) == 0x4000) count++;
            }
            return count;
        }

        private static int CountWhere(string path, string where)
        {
            return Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM O WHERE " + where));
        }

        [Fact]
        public void the_engine_reads_finds_updates_and_deletes_the_rows_ace_moved_to_another_page()
        {
            var path = _tmp.NewMdbPath();
            using (Database.Create(path, FileFormat.Access2000)) { }
            Execute(path, "CREATE TABLE O (Id LONG CONSTRAINT pk PRIMARY KEY, T TEXT(255), U TEXT(255), N LONG)",
                          "CREATE INDEX ByT ON O (T)", "CREATE INDEX ByN ON O (N)");
            for (var id = 1; id <= 60; id++)
                AceInsert(path, "O", V("Id", id, "T", Wide(id, 43), "U", null, "N", id * 10));
            // Rows on the first, full page, made too long for it.
            foreach (var id in new[] { 5, 7, 20, 21 })
                AceUpdate(path, "O", "Id", id, V("T", Wide(id, 250), "U", Wide(id + 500, 255)));
            Assert.Equal(4, OverflowRows(path, "O"));

            var o = new Subject(path, "O", "Id");   // the engine reads all 60, as ACE does
            Assert.Equal(60, o.Ids.Count);

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("O");
                Assert.Equal(60, table.CreateCursor().Count());
                // Through each index: the entry names the row's own slot, and the row is where the pointer leads.
                Assert.Equal(Wide(505, 255), table.FindRowByPrimaryKey(5)["U"]);
                Assert.Equal(7, table.CreateIndexCursor("ByT").FindRow("T", Wide(7, 250))["Id"]);
                Assert.Equal(Wide(20, 250), table.CreateIndexCursor("ByN").FindRow("N", 200)["T"]);
            }

            o.EngineUpdates(5, V("N", 5555));                           // an overflow row, another column
            o.EngineUpdates(20, V("T", Wide(20, 60), "U", null));       // an overflow row, made short again
            o.EngineDeletes(7);                                         // an overflow row
            o.EngineUpdates(6, V("U", Wide(6, 200)));                   // an ordinary row beside them
            o.EngineInserts(V("Id", 61, "T", Wide(61, 100), "U", Wide(62, 100), "N", 610));
            o.Check("after the engine changed the rows ACE had moved");
            // 21's, which was not touched, and 5's, which is changed where ACE put it: an update leaves a row its
            // slot, as ACE does, and brings it home only when it fits its own page again.
            Assert.True(OverflowRows(path, "O") >= 2, OverflowRows(path, "O") + " rows of O have their values on another page");

            // ACE's indexes agree with its rows: the moved rows' entries followed them.
            Assert.Equal(1, CountWhere(path, "T = '" + Wide(5, 250) + "'"));
            Assert.Equal(0, CountWhere(path, "T = '" + Wide(7, 250) + "'"));
            Assert.Equal(0, CountWhere(path, "T = '" + Wide(20, 250) + "'"));
            Assert.Equal(1, CountWhere(path, "T = '" + Wide(20, 60) + "'"));
            Assert.Equal(1, CountWhere(path, "N = 5555"));
            Assert.Equal(0, CountWhere(path, "N = 50"));
            Assert.Equal(0, CountWhere(path, "N = 70"));
            Assert.Equal(1, CountWhere(path, "N = 210"));

            // And ACE moves rows the engine wrote, which the engine then reads and changes.
            o.AceUpdates(61, V("T", Wide(61, 255), "U", Wide(61, 255)));
            o.AceUpdates(5, V("U", null));
            o.AceUpdates(1, V("T", Wide(1, 255), "U", Wide(1, 255)));
            o.EngineUpdates(1, V("N", 11));
            o.EngineUpdates(21, V("N", 2121));
            o.EngineDeletes(61);
            o.Check("after ACE and the engine changed it in turn");
        }

        // A moved row's values are at another slot, which carries the mark Access gives values kept out of a scan
        // (0x8000). The engine deleted or rewrote such a row by marking only the row's own slot, leaving the slot
        // of its values holding the references to its long values with nothing pointing at it; both are marked
        // deleted now (the engine's own tests count the slots). What this test holds is that ACE goes on with such
        // a table - edits rows on those pages, adds, compacts - with every long value whole. It passes with the
        // values' slot left as it was, too: ACE was not seen to act on one.
        [Fact]
        public void ace_leaves_every_long_value_whole_after_the_engine_changes_rows_it_had_moved()
        {
            var path = _tmp.NewMdbPath();
            using (Database.Create(path, FileFormat.Access2000)) { }
            Execute(path, "CREATE TABLE M (Id LONG CONSTRAINT pk PRIMARY KEY, T TEXT(255), U TEXT(255), N LONG, Notes MEMO)");
            Func<int, string> note = id => id.ToString("D4") + new string((char)('a' + id % 26), 2996);
            for (var id = 1; id <= 40; id++)
                AceInsert(path, "M", V("Id", id, "T", Wide(id, 60), "U", null, "N", id, "Notes", note(id)));
            foreach (var id in new[] { 5, 7, 9 })
                AceUpdate(path, "M", "Id", id, V("T", Wide(id, 250), "U", Wide(id + 500, 255)));
            Assert.Equal(3, OverflowRowsOf(path, "M"));
            var m = new Subject(path, "M", "Id");

            m.EngineUpdates(5, V("N", 5555));                 // another column: the Memo stays where it is
            m.EngineDeletes(7);                               // its Memo's pages are freed
            m.EngineInserts(V("Id", 41, "T", Wide(41, 60), "U", null, "N", 41, "Notes", note(41)),   // and used again
                            V("Id", 42, "T", Wide(42, 60), "U", null, "N", 42, "Notes", note(42)));
            Assert.Equal(2, OverflowRowsOf(path, "M"));   // 9's, and 5's: changed where ACE had put it
            m.Check("after the engine changed the moved rows");

            // ACE edits rows on those pages, which is when it tidies them.
            foreach (var id in new[] { 4, 6, 8, 10 })
                m.AceUpdates(id, V("T", Wide(id, 200), "U", Wide(id, 200)));
            m.AceDeletes(3, 11);
            for (var id = 50; id < 60; id++)
                m.AceInserts(V("Id", id, "T", Wide(id, 60), "U", null, "N", id, "Notes", note(id)));
            m.AceUpdates(5, V("U", null));
            m.Check("after ACE edited the pages the moved rows were on");
            Assert.Equal(note(5), Scalar(path, "SELECT Notes FROM M WHERE Id = 5"));
            Assert.Equal(note(41), Scalar(path, "SELECT Notes FROM M WHERE Id = 41"));
        }

        private static int OverflowRowsOf(string path, string table)
        {
            return OverflowRows(path, table);
        }
    }
}
