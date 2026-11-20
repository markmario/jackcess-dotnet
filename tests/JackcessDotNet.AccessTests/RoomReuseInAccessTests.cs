using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Room on a table's pages that the engine uses again, with Access as the referee (#58).
    /// The engine left a deleted row's bytes on its page and put no row where a delete or a row's values moving back
    /// had left room, so a row updated back and forth grew the file without end; and it left the table's free-space
    /// map, where Access looks for room, empty. Measured with DAO 16 and ACE 16, the engine now puts rows where Access
    /// puts them. Here ACE does the same steps as the engine on a copy of the file, and every page of the two copies'
    /// tables must have the same room and slots, and their maps list the same pages; ACE and DAO read, seek through
    /// every index, add to and compact what the engine left; and the engine
    /// uses the room Access left, as Access would. In .mdb and .accdb files, in tables DAO made and tables the engine
    /// made.
    /// </summary>
    public class RoomReuseInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();

        public void Dispose() { _tmp.Dispose(); }

        private const string CreateR =
            "CREATE TABLE R (Id LONG CONSTRAINT pk PRIMARY KEY, A TEXT(255), B TEXT(255), C TEXT(255), D TEXT(255), N LONG)";

        /// <summary>
        /// Text of <paramref name="chars"/> characters, the row's id first: Greek letters are not compressible, so a
        /// row is 19 bytes and two a character (21 with N). Null for none.
        /// </summary>
        private static string Text(int id, int chars) { return chars == 0 ? null : id.ToString("D3") + new string('Ω', chars - 3); }

        private static Values Row(int id, int a, int b = 0, int c = 0, int d = 0)
        {
            return V("Id", id, "A", Text(id, a), "B", Text(id, b), "C", Text(id, c), "D", Text(id, d), "N", id * 10);
        }

        /// <summary>A database with table R, its primary key and an index on each of A and N, made by DAO or by the engine.</summary>
        private string NewDatabase(int daoVersion, bool engineMade)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + (daoVersion == 64 ? ".mdb" : ".accdb"));
            if (engineMade)
            {
                using (var db = Database.Create(path, daoVersion == 64 ? FileFormat.Access2000 : FileFormat.Access2007))
                    db.CreateTable("R", new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32),
                        new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
                        new ColumnDefinition("B", DataType.Text) { MaxLength = 255 },
                        new ColumnDefinition("C", DataType.Text) { MaxLength = 255 },
                        new ColumnDefinition("D", DataType.Text) { MaxLength = 255 },
                        new ColumnDefinition("N", DataType.Int32),
                    }, new[] { "Id" }, new[] { new IndexDefinition("ByA", "A"), new IndexDefinition("ByN", "N") });
            }
            else
            {
                DaoCreate(path, daoVersion);
                Execute(path, CreateR, "CREATE INDEX ByA ON R (A)", "CREATE INDEX ByN ON R (N)");
            }
            return path;
        }

        // ── The table's pages, as the file has them ──────────────────────────────

        private static int DefinitionPage(string path, string table)
        {
            using (var db = Database.Open(path))
            {
                var definition = typeof(Table).GetProperty("Definition", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(db.GetTable(table));
                return (int)definition.GetType().GetProperty("TdefPageNumber").GetValue(definition);
            }
        }

        private static int U16(byte[] file, int at) { return file[at] | file[at + 1] << 8; }
        private static int U24(byte[] file, int at) { return file[at] | file[at + 1] << 8 | file[at + 2] << 16; }

        /// <summary>The pages a usage map lists, inline or by reference to map pages, as Access writes both.</summary>
        private static List<int> MapPages(byte[] file, int page, int row)
        {
            var pages = new List<int>();
            int at = page * 4096;
            int start = U16(file, at + 14 + row * 2) & 0x1FFF;
            int end = row == 0 ? 4096 : U16(file, at + 14 + (row - 1) * 2) & 0x1FFF;
            if (file[at + start] == 0)
            {
                int first = BitConverter.ToInt32(file, at + start + 1);
                for (int i = 0; i < (end - start - 5) * 8; i++)
                    if ((file[at + start + 5 + i / 8] & (1 << (i % 8))) != 0) pages.Add(first + i);
            }
            else
            {
                const int perMapPage = (4096 - 4) * 8;
                for (int k = 0; k < (end - start - 1) / 4; k++)
                {
                    int map = BitConverter.ToInt32(file, at + start + 1 + k * 4);
                    if (map <= 0) continue;
                    for (int i = 0; i < perMapPage; i++)
                        if ((file[map * 4096 + 4 + i / 8] & (1 << (i % 8))) != 0) pages.Add(k * perMapPage + i);
                }
            }
            return pages;
        }

        /// <summary>The table's pages and the pages its free-space map lists, as two lists of page numbers.</summary>
        private static (List<int> Owned, List<int> Listed) Maps(string path, string table)
        {
            int t = DefinitionPage(path, table) * 4096;
            var file = File.ReadAllBytes(path);
            return (MapPages(file, U24(file, t + 56), file[t + 55]), MapPages(file, U24(file, t + 60), file[t + 59]));
        }

        /// <summary>
        /// The table's data pages - each one's room, and each slot's flags and bytes, where a moved row's values went -
        /// and the pages its free-space map lists: every page by its place among the table's pages, so that two files
        /// whose pages came from different places in them compare.
        /// </summary>
        private static string Layout(string path, string table)
        {
            var (owned, listed) = Maps(path, table);
            var file = File.ReadAllBytes(path);
            var place = owned.Select((page, i) => (page, i)).ToDictionary(p => p.page, p => p.i);
            var text = new StringBuilder();
            foreach (int page in owned)
            {
                int at = page * 4096;
                int rows = U16(file, at + 12);
                text.Append("page " + place[page] + ": room " + U16(file, at + 2) + ";");
                for (int row = 0; row < rows; row++)
                {
                    int slot = U16(file, at + 14 + row * 2);
                    int start = slot & 0x1FFF;
                    int end = row == 0 ? 4096 : U16(file, at + 14 + (row - 1) * 2) & 0x1FFF;
                    text.Append(" " + (slot >> 14) + ":" + (end - start));
                    if ((slot & 0xC000) == 0x4000)
                    {
                        int to = U24(file, at + start + 1);
                        text.Append("->" + file[at + start] + "/" + (place.ContainsKey(to) ? place[to].ToString() : "page " + to));
                    }
                }
                text.AppendLine();
            }
            text.Append("listed: " + string.Join(",", listed.Select(page => place.ContainsKey(page) ? place[page].ToString() : "page " + page)));
            return text.ToString();
        }

        /// <summary>Data pages of the table holding slots and no row: what a row updated back and forth left behind.</summary>
        private static int PagesOfDeletedSlotsOnly(string path, string table)
        {
            var file = File.ReadAllBytes(path);
            return Maps(path, table).Owned.Count(page =>
            {
                int rows = U16(file, page * 4096 + 12);
                return rows > 0 && Enumerable.Range(0, rows).All(row => (U16(file, page * 4096 + 14 + row * 2) & 0xC000) == 0xC000);
            });
        }

        // ── The same steps by ACE and by the engine ──────────────────────────────

        /// <summary>Table R in two copies of one file: ACE changes one, the engine the other, a session to each step.</summary>
        private sealed class Twin
        {
            private readonly string _ace;
            private readonly string _engine;

            public Twin(string path)
            {
                _ace = path;
                _engine = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                File.Copy(path, _engine);
            }

            public string Engine { get { return _engine; } }

            public void Insert(int id, int a, int b = 0, int c = 0, int d = 0)
            {
                var row = Row(id, a, b, c, d);
                AceInsert(_ace, "R", row);
                using (var db = Database.Open(_engine)) db.GetTable("R").Insert(EngineRow(row));
            }

            /// <summary>Gives row <paramref name="id"/> text of the lengths given: column name, characters, ...</summary>
            public void Update(int id, params object[] columnsAndChars)
            {
                var changes = new Values();
                for (int i = 0; i < columnsAndChars.Length; i += 2)
                    changes[(string)columnsAndChars[i]] = Text(id, (int)columnsAndChars[i + 1]);
                AceUpdate(_ace, "R", "Id", id, changes);
                using (var db = Database.Open(_engine)) db.GetTable("R").UpdateByPrimaryKey(id, EngineRow(changes));
            }

            public void Delete(int id)
            {
                Execute(_ace, "DELETE FROM R WHERE Id = " + id);
                using (var db = Database.Open(_engine)) db.GetTable("R").DeleteRow("Id", id);
            }

            public void AssertSamePages(string when)
            {
                string ace = Layout(_ace, "R"), engine = Layout(_engine, "R");
                Assert.True(ace == engine, "The engine's pages are not ACE's " + when + ".\nACE:\n" + ace + "\nthe engine:\n" + engine);
            }
        }

        [Theory]
        [InlineData(64, false)]
        [InlineData(128, false)]
        [InlineData(64, true)]
        [InlineData(128, true)]
        public void the_engine_puts_rows_where_ace_puts_them_and_lists_the_pages_ace_lists(int daoVersion, bool engineMade)
        {
            var twin = new Twin(NewDatabase(daoVersion, engineMade));

            for (int id = 1; id <= 30; id++) twin.Insert(id, 200);
            twin.AssertSamePages("after 30 rows");

            // Deletes, and rows into the room they left: the lowest page listed, a page without room leaving the map.
            twin.Delete(2);
            twin.Delete(3);
            twin.Delete(21);
            twin.Insert(31, 200);
            twin.Insert(32, 200);
            twin.Insert(33, 200);
            twin.Delete(11);
            twin.Insert(34, 50);
            twin.AssertSamePages("after deletes and rows into the room they left");

            // A row grown past its page, grown again where its values went, and home; another moved on.
            twin.Update(12, "B", 255, "C", 255);
            twin.Update(12, "D", 100);
            twin.Update(12, "B", 0, "C", 0, "D", 0);
            for (int id = 35; id <= 42; id++) twin.Insert(id, 200);
            twin.Update(20, "B", 255, "C", 255);
            for (int id = 43; id <= 48; id++) twin.Insert(id, 200);
            twin.Update(20, "D", 255);
            twin.Update(14, "A", 30);                          // smaller where it is: nothing listed
            twin.AssertSamePages("after rows moved to other pages, home, and on");

            // A row too large for half a page, a row whose values had moved deleted, and the rows after them.
            twin.Insert(49, 255, 255, 255, 255);
            twin.Insert(50, 10);
            twin.Delete(20);
            twin.Update(16, "B", 255, "C", 255);
            twin.Delete(15);
            twin.Insert(51, 200);
            twin.Insert(52, 200);
            twin.AssertSamePages("after a row of more than half a page and the deletes after it");
        }

        // ── What Access makes of what the engine left ────────────────────────────

        /// <summary>
        /// Every row of R is found by ACE through each of its indexes - the primary key, ByA and ByN, and a scan it
        /// cannot use them for - and DAO walks each index in its own order to the same rows.
        /// </summary>
        private static void AssertFoundThroughEveryIndex(string path, Subject r, string when)
        {
            var expected = r.Ids.Select(id => r[id]).ToList();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var row in expected)
                {
                    foreach (var (where, value) in new[] { ("A = ?", row["A"]), ("N = ?", row["N"]), ("A & '' = ?", row["A"]) })
                    {
                        if (value == null) continue;
                        using (var command = new OleDbCommand("SELECT Id FROM R WHERE " + where, connection))
                        {
                            command.Parameters.Add(value is string
                                ? new OleDbParameter("p", OleDbType.VarWChar) { Value = value }
                                : new OleDbParameter("p", value));
                            var ids = new List<int>();
                            using (var reader = command.ExecuteReader())
                                while (reader.Read()) ids.Add(reader.GetInt32(0));
                            Assert.True(ids.SequenceEqual(new[] { Convert.ToInt32(row["Id"]) }),
                                "ACE's WHERE " + where + " for row " + row["Id"] + " found " + string.Join(",", ids) + " " + when);
                        }
                    }
                }
            }
            var written = Written(expected);
            List<string> indexes;
            using (var db = Database.Open(path)) indexes = db.GetTable("R").Indexes.Select(index => index.Name).ToList();
            Assert.Equal(3, indexes.Count);
            foreach (var index in indexes)
                AssertSame(written, Written(DaoRows(path, "R", index)), "R as DAO walks index " + index + " " + when);
        }

        [Theory]
        [InlineData(64, false)]
        [InlineData(128, false)]
        [InlineData(64, true)]
        [InlineData(128, true)]
        public void ace_and_dao_read_seek_add_to_and_compact_a_table_whose_room_the_engine_used_again(int daoVersion, bool engineMade)
        {
            var path = NewDatabase(daoVersion, engineMade);
            var r = new Subject(path, "R", "Id");
            for (int id = 1; id <= 40; id++) r.EngineInserts(Row(id, 200));
            r.EngineDeletes(2, 3, 11, 25, 26);
            for (int id = 41; id <= 45; id++) r.EngineInserts(Row(id, 200));

            // Back and forth: a row grows past its page, grows again and comes home, and now and then another does.
            for (int i = 1; i <= 100; i++)
            {
                r.EngineUpdates(12, V("B", Text(12, 255), "C", Text(12, 255)));
                r.EngineUpdates(12, V("D", Text(12, 100)));
                r.EngineUpdates(12, V("B", null, "C", null, "D", null));
                if (i % 10 == 0)
                {
                    bool grow = i % 20 == 10;
                    r.EngineUpdates(17, V("B", grow ? Text(17, 255) : null, "C", grow ? Text(17, 255) : null, "D", grow ? Text(17, 255) : null));
                }
            }
            Assert.Equal(0, PagesOfDeletedSlotsOnly(path, "R"));

            // Two rows' values on a page of their own, which the engine begins again when both have come home, and
            // the next row goes to.
            int before = Maps(path, "R").Owned.Count;
            r.EngineUpdates(30, V("B", Text(30, 255), "C", Text(30, 255), "D", Text(30, 255)));
            r.EngineUpdates(31, V("B", Text(31, 255), "C", Text(31, 255), "D", Text(31, 255)));
            r.EngineUpdates(30, V("B", null, "C", null, "D", null));
            r.EngineUpdates(31, V("B", null, "C", null, "D", null));
            r.EngineInserts(Row(46, 200));
            Assert.InRange(Maps(path, "R").Owned.Count - before, 0, 1);

            r.Check("after the engine used the room deletes and updates left");
            AssertFoundThroughEveryIndex(path, r, "after the engine used the room deletes and updates left");

            // ACE adds to the table, grows rows past their pages, brings them home and deletes rows, on the pages the
            // engine listed and began again; then the engine goes on.
            for (int id = 60; id <= 70; id++) r.AceInserts(Row(id, 150));
            r.AceUpdates(13, V("B", Text(13, 255), "C", Text(13, 255)));
            r.AceUpdates(46, V("B", Text(46, 255), "C", Text(46, 255), "D", Text(46, 255)));
            r.AceDeletes(4, 5, 60);
            r.AceUpdates(13, V("B", null, "C", null));
            r.Check("after ACE added to it and changed it");
            for (int id = 71; id <= 80; id++) r.EngineInserts(Row(id, 120));
            r.EngineUpdates(46, V("B", null, "C", null, "D", null));
            r.EngineDeletes(6, 61);
            r.Check("after the engine went on from where ACE left it");
            AssertFoundThroughEveryIndex(path, r, "after ACE and the engine changed it in turn");
        }

        /// <summary>
        /// A page the engine begins again, in a session that deleted a row from it, keeps that row's slot and the slots
        /// before it, deleted and empty, and takes its next rows after them: a page as Access leaves one whose rows it
        /// deletes. ACE and DAO read it, seek through every index, add to it and compact it, and the engine reads what
        /// ACE added.
        /// </summary>
        [Theory]
        [InlineData(64, false)]
        [InlineData(128, false)]
        [InlineData(64, true)]
        [InlineData(128, true)]
        public void ace_and_dao_read_seek_add_to_and_compact_a_page_the_engine_began_again_after_a_delete(int daoVersion, bool engineMade)
        {
            var path = NewDatabase(daoVersion, engineMade);
            new Subject(path, "R", "Id").EngineInserts(Enumerable.Range(1, 18).Select(id => Row(id, 200)).ToArray());

            // One session: rows 3 and 4 grown to a page of their own, row 19 put there and deleted, rows 3 and 4 home -
            // the page left with no row, begun again after row 19's slot - and row 20 put there.
            int away;
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("R");
                foreach (int id in new[] { 3, 4 })
                    table.UpdateByPrimaryKey(id, EngineRow(V("B", Text(id, 255), "C", Text(id, 255))));
                table.Insert(EngineRow(Row(19, 200)));
                away = table.FindRowByPrimaryKey(19).Id.Value.PageNumber;
                table.DeleteRow("Id", 19);
                foreach (int id in new[] { 3, 4 })
                    table.UpdateByPrimaryKey(id, EngineRow(V("B", null, "C", null)));
                table.Insert(EngineRow(Row(20, 200)));
                Assert.Equal(new RowId(away, 3), table.FindRowByPrimaryKey(20).Id.Value);
            }
            var (owned, _) = Maps(path, "R");
            string page = Layout(path, "R").Split('\n')[owned.IndexOf(away)].Trim();
            Assert.EndsWith("; 3:0 3:0 3:0 0:423", page);

            var r = new Subject(path, "R", "Id");
            Assert.Equal(Enumerable.Range(1, 18).Concat(new[] { 20 }).Select(id => (long)id), r.Ids);
            r.Check("after the engine began a page again after a row deleted in the session");
            AssertFoundThroughEveryIndex(path, r, "after the engine began a page again after a row deleted in the session");

            // ACE adds rows to that page, after the slots the engine kept, and deletes one; the engine goes on.
            for (int id = 60; id <= 64; id++) r.AceInserts(Row(id, 150));
            r.AceDeletes(20);
            r.Check("after ACE added to the page and deleted from it");
            r.EngineInserts(Row(65, 150));
            r.EngineDeletes(61);
            r.Check("after the engine went on from where ACE left it");
            AssertFoundThroughEveryIndex(path, r, "after ACE and the engine changed the page in turn");
        }

        // ── Room Access left, used by the engine ──────────────────────────────────

        [Theory]
        [InlineData(64)]
        [InlineData(128)]
        public void the_engine_uses_the_room_ace_left_where_ace_would(int daoVersion)
        {
            var path = NewDatabase(daoVersion, engineMade: false);
            for (int id = 1; id <= 30; id++) AceInsert(path, "R", Row(id, 200));
            Execute(path, "DELETE FROM R WHERE Id = 2", "DELETE FROM R WHERE Id = 12");
            AceUpdate(path, "R", "Id", 20, V("B", Text(20, 255), "C", Text(20, 255)));
            for (int id = 31; id <= 37; id++) AceInsert(path, "R", Row(id, 200));
            AceUpdate(path, "R", "Id", 20, V("B", null, "C", null));
            var (owned, listed) = Maps(path, "R");
            Assert.True(listed.Count >= 2, "ACE listed " + listed.Count + " pages");

            // The same steps after it, by ACE on one copy and the engine on the other.
            var twin = new Twin(path);
            twin.Insert(38, 200);
            twin.Insert(39, 200);
            twin.Update(22, "B", 255, "C", 255);
            twin.Insert(40, 200);
            twin.Delete(5);
            twin.Insert(41, 150);
            twin.AssertSamePages("after the rows that went into the room ACE had left");

            var r = new Subject(twin.Engine, "R", "Id");
            r.Check("after the engine used the room ACE had left");
            AssertFoundThroughEveryIndex(twin.Engine, r, "after the engine used the room ACE had left");
        }

        [Theory]
        [InlineData(64)]
        [InlineData(128)]
        public void the_engine_takes_rows_to_no_page_ace_gave_back_to_the_file(int daoVersion)
        {
            // Access gives back to the file a page an update leaves with no row: it is in neither of the table's maps,
            // and is the database's to hand out, which the engine must not write to as the table's.
            var path = NewDatabase(daoVersion, engineMade: false);
            for (int id = 1; id <= 18; id++) AceInsert(path, "R", Row(id, 200));
            AceUpdate(path, "R", "Id", 3, V("B", Text(3, 255), "C", Text(3, 255)));
            AceUpdate(path, "R", "Id", 4, V("B", Text(4, 255), "C", Text(4, 255)));
            int pages = Maps(path, "R").Owned.Count;
            AceUpdate(path, "R", "Id", 3, V("B", null, "C", null));
            AceUpdate(path, "R", "Id", 4, V("B", null, "C", null));
            var (owned, _) = Maps(path, "R");
            Assert.Equal(pages - 1, owned.Count);

            var r = new Subject(path, "R", "Id");
            for (int id = 19; id <= 40; id++) r.EngineInserts(Row(id, 200));
            r.EngineUpdates(7, V("B", Text(7, 255), "C", Text(7, 255)));
            r.Check("after the engine added rows to a table ACE had given a page of back");
            AssertFoundThroughEveryIndex(path, r, "after the engine added rows to a table ACE had given a page of back");
        }

        // ── The catalog ───────────────────────────────────────────────────────────

        [Theory]
        [InlineData(64)]
        [InlineData(128)]
        public void access_lists_reads_and_compacts_the_tables_after_the_engine_drops_and_makes_them_in_the_catalog_s_room(int daoVersion)
        {
            // A table's rows in MSysObjects and MSysACEs leave their pages when it is dropped, and the next table's go
            // to the room they left, as Access's do.
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + (daoVersion == 64 ? ".mdb" : ".accdb"));
            DaoCreate(path, daoVersion);
            var made = new List<string>();
            using (var db = Database.Open(path))
            {
                for (int round = 0; round < 4; round++)
                {
                    for (int i = 0; i < 6; i++)
                    {
                        string name = "Kept" + round + "_" + i;
                        var table = db.CreateTable(name, new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 } }, "Id");
                        table.Insert(new Row { ["Id"] = 1, ["Name"] = name });
                        made.Add(name);
                    }
                    foreach (var name in made.Where(n => n.EndsWith("_1") || n.EndsWith("_3") || n.EndsWith("_4")).ToList())
                    {
                        db.DropTable(name);
                        made.Remove(name);
                    }
                }
            }

            var tables = new List<string>();
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = dbe.OpenDatabase(path);
                try
                {
                    foreach (dynamic definition in db.TableDefs)
                    {
                        string name = definition.Name;
                        if (!name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)) tables.Add(name);
                    }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            Assert.Equal(made.OrderBy(n => n).ToList(), tables.OrderBy(n => n).ToList());

            foreach (var name in made)
                Assert.Equal(name, Scalar(path, "SELECT Name FROM [" + name + "] WHERE Id = 1"));
            Execute(path, "CREATE TABLE ByAce (Id LONG CONSTRAINT pk PRIMARY KEY)", "INSERT INTO ByAce (Id) VALUES (1)");
            var compacted = Compact(path);
            try
            {
                foreach (var name in made)
                    Assert.Equal(name, Scalar(compacted, "SELECT Name FROM [" + name + "] WHERE Id = 1"));
                Assert.Equal(1, Convert.ToInt32(Scalar(compacted, "SELECT COUNT(*) FROM ByAce")));
            }
            finally { File.Delete(compacted); }
        }
    }
}
