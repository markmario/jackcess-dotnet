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
    /// Long values (Memo and OLE) the managed engine writes into tables Microsoft Access made, with ACE as the
    /// referee. A value too long for one page is a chain of chunks, and the row's
    /// reference to it ends in four bytes Access compares with four bytes of the page the chain starts on. The
    /// engine wrote zeros in the reference, which match a page of its own and not one Access had stamped: once a
    /// delete, or any update of the row, had emptied such a page and the engine had started a chain on it, ACE could
    /// not read the value. Every length that is kept differently - in the row, in one chunk, in a chain - is written
    /// by ACE and by the engine, after an insert, after an update of another column, after an update of the value
    /// itself (to a shorter one and a longer one) and after deletes, and each time ACE reads every row by scan,
    /// through the primary key and after a DAO compact. Then ACE changes and deletes what the engine wrote, which
    /// is what showed that Access needs every page of a chain to be that value's alone.
    /// </summary>
    public class LongValuesInAccessTests : IDisposable
    {
        private TempDb _tmp;

        public LongValuesInAccessTests() { _tmp = new TempDb(); }
        public void Dispose() { _tmp.Dispose(); }

        // In the row to 64 bytes; one chunk to 4,076 (Access itself makes a chain from about 3,500); a chain after that.
        private static readonly int[] Lengths = { 0, 60, 64, 65, 300, 1022, 2000, 3000, 5000, 70000 };

        /// <summary>Text of <paramref name="length"/> characters, different for each <paramref name="seed"/>; every third one has characters past Latin-1.</summary>
        private static string Text(int length, int seed)
        {
            var text = new char[length];
            for (var i = 0; i < length; i++)
                text[i] = seed % 3 == 0 && i % 7 == 0 ? (char)(0x3A9 + (i + seed) % 20) : (char)('a' + (i * 7 + seed) % 26);
            return new string(text);
        }

        private static byte[] Bytes(int length, int seed)
        {
            return Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();
        }

        private static Values Row(int id, int length, int seed)
        {
            return V("Id", id, "Name", "row " + id, "Notes", Text(length, seed), "Data", Bytes(length, seed), "More", Text(length / 2, seed + 1));
        }

        private const string Table = "CREATE TABLE L (Id LONG CONSTRAINT pk PRIMARY KEY, Name TEXT(20), Notes MEMO, Data LONGBINARY, More MEMO)";

        public static IEnumerable<object[]> Files()
        {
            yield return new object[] { 0, ".mdb" };        // the engine's own blank Access 2000 file
            yield return new object[] { 64, ".mdb" };       // DAO's Access 2000 file
            yield return new object[] { 128, ".accdb" };    // Access 2007
            yield return new object[] { 256, ".accdb" };    // Access 2010 and later
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void ace_reads_every_long_value_the_engine_writes_in_a_table_ace_made(int daoVersion, string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            if (daoVersion == 0) using (Database.Create(path, FileFormat.Access2000)) { }
            else DaoCreate(path, daoVersion);
            Execute(path, Table);
            for (var i = 0; i < Lengths.Length; i++)
                AceInsert(path, "L", Row(1 + i, Lengths[i], seed: i));
            var l = new Subject(path, "L", "Id");

            // After an insert.
            for (var i = 0; i < Lengths.Length; i++)
                l.EngineInserts(Row(11 + i, Lengths[i], seed: 20 + i));
            l.Check("after the engine inserted");

            // After an update of another column: the long values stay where they are.
            foreach (var id in l.Ids)
                l.EngineUpdates(id, V("Name", "renamed " + id));
            l.Check("after the engine updated another column of every row");

            // After an update of the long value itself: each row takes the next length, so all but one get longer
            // and one gets shorter; then some are emptied and some set where there was none.
            foreach (var id in l.Ids)
            {
                var next = Lengths[(int)(id % 10 + 1) % Lengths.Length];
                l.EngineUpdates(id, V("Notes", Text(next, (int)id + 40), "Data", Bytes(next, (int)id + 40)));
            }
            l.Check("after the engine updated every long value");
            l.EngineUpdates(9, V("Notes", null, "Data", null, "More", Text(70000, 99)));
            l.EngineUpdates(19, V("Notes", Text(10, 1), "Data", Bytes(10, 1), "More", null));
            l.EngineUpdates(1, V("More", Text(5000, 98)));
            l.Check("after the engine emptied some long values and filled others");

            // After deletes: the new values go where the deleted ones were.
            l.EngineDeletes(1, 3, 5, 7, 9, 12, 14, 16, 18, 20);
            l.Check("after the engine deleted");
            for (var i = 0; i < Lengths.Length; i++)
                l.EngineInserts(Row(21 + i, Lengths[Lengths.Length - 1 - i], seed: 60 + i));
            l.Check("after the engine inserted where it had deleted");

            // And ACE goes on: it changes and deletes what the engine wrote, and the engine adds after it.
            l.AceUpdates(21, V("Notes", Text(3000, 77), "Data", Bytes(123456, 77)));
            l.AceUpdates(11, V("Name", "ace's"));
            l.Check("after ACE updated the engine's rows");
            l.AceDeletes(22, 13, 2);
            l.Check("after ACE deleted the engine's rows");
            l.AceInserts(Row(31, 70000, 80), Row(32, 300, 81), Row(33, 5000, 82));
            l.Check("after ACE inserted");
            l.EngineInserts(Row(41, 70000, 90));
            l.EngineUpdates(31, V("Notes", Text(4000, 91)));
            l.EngineDeletes(33);
            l.EngineInserts(Row(42, 5000, 92));
            l.Check("after ACE and the engine changed it in turn");
        }

        /// <summary>
        /// Where the next one-chunk value goes. Access lists a page of such values in the column's free-space map
        /// while it has 258 bytes of room or more, and puts the next value that fits on a listed page; with 257 or
        /// fewer the page is not listed and the value starts a new one (the engine's #35: it took 264 for the
        /// number, and so started a page where Access did not). ACE leaves a page with a chosen amount of room -
        /// 2,000 bytes on it, then what fills it to that - and a third value of 200 bytes is then added by ACE in
        /// one copy and by the engine in another: the two files are the same length, and no longer than before
        /// when the page was listed.
        /// </summary>
        [Theory]
        [InlineData(257, false)]
        [InlineData(258, true)]
        [InlineData(260, true)]
        [InlineData(263, true)]
        [InlineData(264, true)]
        public void the_engine_puts_a_one_chunk_value_on_the_page_access_would(int room, bool onThePageWithRoom)
        {
            var path = _tmp.NewMdbPath();
            DaoCreate(path, 64);
            Execute(path, "CREATE TABLE R (Id LONG CONSTRAINT pk PRIMARY KEY, Data LONGBINARY)");
            // A page has 4,082 bytes after its header, and each value takes its bytes and two for its slot.
            AceInsert(path, "R", V("Id", 1, "Data", Bytes(2000, 1)));
            AceInsert(path, "R", V("Id", 2, "Data", Bytes(4082 - 2002 - 2 - room, 2)));
            var before = new FileInfo(path).Length;

            var ace = _tmp.NewMdbPath();
            var engine = _tmp.NewMdbPath();
            File.Copy(path, ace);
            File.Copy(path, engine);
            var third = V("Id", 3, "Data", Bytes(200, 3));
            AceInsert(ace, "R", third);
            using (var db = Database.Open(engine))
                db.GetTable("R").Insert(EngineRow(third));

            Assert.Equal(before + (onThePageWithRoom ? 0 : 4096), new FileInfo(ace).Length);
            Assert.Equal(new FileInfo(ace).Length, new FileInfo(engine).Length);
            AssertSame(Written(AceTable(ace, "R")), Written(AceTable(engine, "R")), "R as ACE reads it after the engine added the value");
            AssertSame(Written(AceTable(ace, "R")), Written(EngineTable(engine, "R")), "R as the engine reads it");

            // And ACE goes on from where the engine left the page: a fourth value, in both.
            var fourth = V("Id", 4, "Data", Bytes(300, 4));
            AceInsert(ace, "R", fourth);
            AceInsert(engine, "R", fourth);
            Assert.Equal(new FileInfo(ace).Length, new FileInfo(engine).Length);
            AssertSame(Written(AceTable(ace, "R")), Written(AceTable(engine, "R")), "R after ACE added to the engine's file");
        }

        // A value too long for one chunk is a chain, and Access takes every page of a chain to be that value's
        // alone: deleting the value, it gives them all back to the file, whatever else is on them. The engine put
        // the last chunk of a chain on a page with other values' chunks, so one long value deleted through Access
        // left its neighbour's last chunk on a page Access then wrote over. The engine gives each chunk of a chain a
        // page of its own now. The earlier placement is still there to ask for, for a caller that needs a
        // file to stay byte for byte what it was (Database.ShareLongValuePages): what it costs is pinned
        // here, as it is, for whoever decides that.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ace_deleting_a_long_value_leaves_the_others_only_when_each_chain_has_its_pages_to_itself(bool sharePages)
        {
            var path = _tmp.NewMdbPath();
            var written = new List<Values>();
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                db.ShareLongValuePages = sharePages;
                var table = db.CreateTable("N", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) }, primaryKey: "Id");
                for (var i = 1; i <= 6; i++)
                {
                    written.Add(V("Id", i, "Notes", Text(3000, i)));
                    table.Insert(EngineRow(written[i - 1]));
                }
            }
            AssertSame(Written(written), Written(AceTable(path, "N")), "the six notes as ACE reads them");

            Execute(path, "DELETE FROM N WHERE Id = 1", "DELETE FROM N WHERE Id = 3");
            written.RemoveAll(row => Equals(row["Id"], 1) || Equals(row["Id"], 3));

            // What ACE adds next goes onto the pages it took back.
            bool intact;
            try
            {
                for (var i = 10; i < 16; i++)
                {
                    written.Add(V("Id", i, "Notes", Text(3000, i)));
                    AceInsert(path, "N", written[written.Count - 1]);
                }
                intact = Written(written).SequenceEqual(Written(AceTable(path, "N")));
            }
            catch (Exception e) when (e is System.Data.OleDb.OleDbException || e is InvalidOperationException)
            {
                intact = false;   // "Cannot open database ''. It may not be a database that your application recognizes, or the file may be corrupt."
            }

            Assert.Equal(!sharePages, intact);
        }
    }
}
