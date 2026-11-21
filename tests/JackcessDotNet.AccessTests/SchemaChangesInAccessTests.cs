using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Tables the managed engine changes after they are made, and relationships it creates, checked by Access.
    /// Each case runs in a database that sorts in General - Legacy (.mdb) and in one
    /// that sorts in General (.accdb), on tables Access made and on tables the engine made.
    /// <para>
    /// A column the engine adds: ACE lists it, reads the rows that were there as null and the rows written since
    /// with their values, adds and changes rows itself, and DAO compacts the file with every row kept. An index the
    /// engine adds: DAO lists it with the attributes of the same index made by ACE in a twin file, seeks through it
    /// by name, ACE adds to it and refuses a duplicate in it, and it survives compacting. A relationship the engine
    /// creates: beside the same relationship made by DAO in a twin file, DAO lists the same relationship, the two
    /// files hold the same MSysRelationships rows and the same index slots on both tables, ACE refuses an orphan
    /// and cascades as the relationship says - the same statements refused or made in both files, with the same
    /// rows after - and the engine's own enforcement does what ACE does to the twin.
    /// </para>
    /// </summary>
    public partial class SchemaChangesInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        /// <summary>General or General - Legacy, and whether Access or the engine makes the tables.</summary>
        public static IEnumerable<object[]> Kinds()
        {
            foreach (var general in new[] { false, true })
                foreach (var accessMade in new[] { true, false })
                    yield return new object[] { general, accessMade };
        }

        private static ColumnDefinition Long(string name) { return new ColumnDefinition(name, DataType.Int32); }
        private static ColumnDefinition Text(string name, int chars) { return new ColumnDefinition(name, DataType.Text) { MaxLength = chars }; }

        // ── The databases ────────────────────────────────────────────────────

        /// <summary>
        /// An empty database: a copy of an Access 2010 one whose text sorts in General, from the engine's corpus,
        /// with its tables dropped; or a new one in General - Legacy, made by DAO for tables Access is to make and
        /// by the engine for tables the engine is to make.
        /// </summary>
        private string NewDatabase(bool general, bool accessMade)
        {
            if (!general)
            {
                var path = _tmp.NewMdbPath();
                if (accessMade) DaoCreate(path, 64);
                else Database.Create(path, FileFormat.Access2000).Dispose();
                return path;
            }

            SkipUnlessAceOpensAccess2010();
            var corpus = typeof(SchemaChangesInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "EngineCorpusDir").Value;
            var copy = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + ".accdb");
            File.Copy(Path.Combine(corpus, "V2010", "common1V2010.accdb"), copy);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(copy)))
            {
                connection.Open();
                var tables = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null).Rows.Cast<System.Data.DataRow>()
                    .Where(r => (string)r["TABLE_TYPE"] == "TABLE").Select(r => (string)r["TABLE_NAME"]).ToList();
                foreach (var table in tables)
                    using (var command = new OleDbCommand("DROP TABLE [" + table + "]", connection))
                        command.ExecuteNonQuery();
            }
            return copy;
        }

        /// <summary>
        /// P (K, T) keyed by K, with rows 1 to 5; C (Id, K, T) keyed by Id, with sixty rows that refer to P by K; and
        /// U (Id, K), keyed by Id and unique in K, with three rows. The tables and their rows are ACE's or the
        /// engine's.
        /// </summary>
        private string ParentAndChild(bool general, bool accessMade)
        {
            var path = NewDatabase(general, accessMade);
            if (accessMade)
            {
                var statements = new List<string>
                {
                    "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(20))",
                    "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, T TEXT(20))",
                    "CREATE TABLE U (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, CONSTRAINT uq UNIQUE (K))",
                };
                for (var k = 1; k <= 5; k++) statements.Add("INSERT INTO P (K, T) VALUES (" + k + ", 'p" + k + "')");
                for (var i = 1; i <= 60; i++) statements.Add("INSERT INTO C (Id, K, T) VALUES (" + i + ", " + (i % 5 + 1) + ", 'c" + i + "')");
                for (var i = 1; i <= 3; i++) statements.Add("INSERT INTO U (Id, K) VALUES (" + i + ", " + i + ")");
                Execute(path, statements.ToArray());
                return path;
            }

            using (var db = Database.Open(path))
            {
                var p = db.CreateTable("P", new[] { Long("K"), Text("T", 20) }, primaryKey: "K");
                var c = db.CreateTable("C", new[] { Long("Id"), Long("K"), Text("T", 20) }, primaryKey: "Id");
                var u = db.CreateTable("U", new[] { Long("Id"), Long("K") }, new[] { "Id" }, new[] { new IndexDefinition("uq", "K") { IsUnique = true } });
                for (var k = 1; k <= 5; k++) p.Insert(new Row { ["K"] = k, ["T"] = "p" + k });
                for (var i = 1; i <= 60; i++) c.Insert(new Row { ["Id"] = i, ["K"] = i % 5 + 1, ["T"] = "c" + i });
                for (var i = 1; i <= 3; i++) u.Insert(new Row { ["Id"] = i, ["K"] = i });
            }
            return path;
        }

        // ── DAO ──────────────────────────────────────────────────────────────

        /// <summary>What <paramref name="use"/> makes of the database, opened by a DAO engine that is let go of after.</summary>
        private static T WithDao<T>(string path, Func<dynamic, T> use)
        {
            var engine = Dao.Engine;
            try { return Opened(engine, path, use); }
            finally { Dao.Release(engine); }
        }

        // Its own method, so nothing DAO handed out is still in a local when the engine is released.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T Opened<T>(object engine, string path, Func<dynamic, T> use)
        {
            dynamic database = ((dynamic)engine).OpenDatabase(path);
            try { return use(database); }
            finally { database.Close(); }
        }

        /// <summary>The table's fields as DAO lists them: name and type.</summary>
        private static List<string> DaoFields(string path, string table)
        {
            return WithDao(path, db =>
            {
                var fields = new List<string>();
                foreach (dynamic field in db.TableDefs[table].Fields)
                    fields.Add((string)field.Name + ":" + (int)field.Type);
                return fields;
            });
        }

        /// <summary>
        /// The table's indexes as DAO lists them, by name: what each is, and its fields in order (- for descending).
        /// Whether a primary key requires its values is not asked: it does, and for one the engine made with a
        /// table - whose block does not carry the flag Access gives its own - DAO was seen to answer either way for
        /// the same file, depending on what the process had opened before.
        /// </summary>
        private static List<string> DaoIndexes(string path, string table)
        {
            return WithDao(path, db =>
            {
                var indexes = new List<string>();
                foreach (dynamic index in db.TableDefs[table].Indexes)
                {
                    var fields = new List<string>();
                    foreach (dynamic field in index.Fields)
                        fields.Add((((int)field.Attributes & 1) != 0 ? "-" : "+") + (string)field.Name);   // dbDescending
                    indexes.Add((string)index.Name + ": unique=" + (bool)index.Unique + " primary=" + (bool)index.Primary
                        + " foreign=" + (bool)index.Foreign + " ignoreNulls=" + (bool)index.IgnoreNulls
                        + " required=" + ((bool)index.Primary ? "as a primary key" : ((bool)index.Required).ToString())
                        + " fields=" + string.Join(",", fields));
                }
                indexes.Sort(StringComparer.Ordinal);
                return indexes;
            });
        }

        /// <summary>The database's relationships as DAO lists them, in its order.</summary>
        private static List<string> DaoRelations(string path)
        {
            return WithDao(path, db =>
            {
                var relations = new List<string>();
                foreach (dynamic relation in db.Relations)
                {
                    var fields = new List<string>();
                    foreach (dynamic field in relation.Fields)
                        fields.Add((string)field.Name + ">" + (string)field.ForeignName);
                    relations.Add((string)relation.Name + ": " + (string)relation.Table + " -> " + (string)relation.ForeignTable
                        + " attributes " + (int)relation.Attributes + " fields " + string.Join(",", fields));
                }
                return relations;
            });
        }

        /// <summary>DAO creates a relationship of one column, as Access's Relationships window does.</summary>
        private static void DaoRelate(string path, string name, string table, string field, string foreignTable, string foreignField, int attributes)
        {
            WithDao(path, db =>
            {
                dynamic relation = db.CreateRelation(name, table, foreignTable, attributes);
                dynamic pair = relation.CreateField(field);
                pair.ForeignName = foreignField;
                relation.Fields.Append(pair);
                db.Relations.Append(relation);
                return 0;
            });
        }

        /// <summary>
        /// DAO seeks <paramref name="key"/> in the index named <paramref name="index"/>, which only the index's own
        /// tree can answer, and returns the Id of the row it comes to, or null when the index has no such key.
        /// </summary>
        private static int? DaoSeek(string path, string table, string index, params object[] key)
        {
            return WithDao(path, db =>
            {
                dynamic rows = db.OpenRecordset(table, 1 /* dbOpenTable */);
                try
                {
                    rows.Index = index;
                    if (key.Length == 1) rows.Seek("=", key[0]);
                    else rows.Seek("=", key[0], key[1]);
                    return (bool)rows.NoMatch ? (int?)null : Convert.ToInt32(rows.Fields["Id"].Value);
                }
                finally { rows.Close(); }
            });
        }

        // ── What the files hold ──────────────────────────────────────────────

        /// <summary>The page of each table's definition, by the table's name, from the catalog as the engine reads it.</summary>
        private static Dictionary<int, string> TablePages(string path)
        {
            using (var db = Database.Open(path))
                return db.GetTable("MSysObjects").Rows.ToList()
                    .Where(r => Convert.ToInt32(r["Type"]) == 1 && r["Name"] != null)
                    .ToDictionary(r => Convert.ToInt32(r["Id"]), r => (string)r["Name"]);
        }

        /// <summary>
        /// The index slots of <paramref name="table"/>'s definition, read from the file's bytes, in the order the
        /// definition has them: each index's name, number, the block that holds its tree, its type, and what it says
        /// of a relationship - which side it is, the other side's index and table, and the cascades. The other
        /// table is given by name, since the twin files need not have their tables on the same pages.
        /// </summary>
        private static List<string> Slots(string path, string table)
        {
            var pages = TablePages(path);
            var tdef = pages.Single(p => p.Value == table).Key;
            var p0 = DefinitionBytes(path, tdef).Item1;
            int columns = BitConverter.ToInt16(p0, 45), slots = BitConverter.ToInt32(p0, 47), blocks = BitConverter.ToInt32(p0, 51);
            var pos = 63 + blocks * 12 + columns * 25;
            for (var i = 0; i < columns; i++) pos += 2 + BitConverter.ToInt16(p0, pos);
            var blockFlags = Enumerable.Range(0, blocks).Select(b => p0[pos + b * 52 + 4 + 30 + 8 + 4]).ToList();
            pos += blocks * 52;
            var names = pos + slots * 28;
            var result = new List<string>();
            for (var i = 0; i < slots; i++)
            {
                var s = pos + i * 28 + 4;
                int length = BitConverter.ToInt16(p0, names);
                var name = System.Text.Encoding.Unicode.GetString(p0, names + 2, length);
                names += 2 + length;
                var otherTable = BitConverter.ToInt32(p0, s + 13);
                var data = BitConverter.ToInt32(p0, s + 4);
                result.Add(name + ": number " + BitConverter.ToInt32(p0, s) + " block " + data + " flags 0x" + blockFlags[data].ToString("X2")
                    + " type " + p0[s + 19] + " side " + p0[s + 8] + " other index " + BitConverter.ToInt32(p0, s + 9)
                    + " other table " + (otherTable == 0 ? "none" : pages[otherTable])
                    + " cascade updates " + p0[s + 17] + " deletes " + p0[s + 18]);
            }
            return result;
        }

        /// <summary>
        /// The definition that starts on page <paramref name="tdef"/>, read from the file's bytes, and the pages it
        /// is on: the first page whole, and each page it goes on to (named in bytes 4-7 of the page before) from
        /// its byte 8.
        /// </summary>
        private static Tuple<byte[], List<int>> DefinitionBytes(string path, int tdef)
        {
            const int pageSize = 4096;
            var bytes = new List<byte>();
            var on = new List<int>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var page = new byte[pageSize];
                for (var next = tdef; next != 0; next = BitConverter.ToInt32(page, 4))
                {
                    Assert.True(on.Count < 64, "the definition's pages lead round in a ring");
                    stream.Seek((long)next * pageSize, SeekOrigin.Begin);
                    Assert.Equal(pageSize, stream.Read(page, 0, pageSize));
                    Assert.Equal(2, page[0]);
                    bytes.AddRange(on.Count == 0 ? page : page.Skip(8));
                    on.Add(next);
                }
            }
            return Tuple.Create(bytes.ToArray(), on);
        }

        /// <summary>The rows of MSysRelationships, as the engine reads them, in the table's order.</summary>
        private static List<string> RelationshipRows(string path)
        {
            using (var db = Database.Open(path))
                return db.GetTable("MSysRelationships").Rows.ToList()
                    .Select(r => string.Join(", ", r.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + r[k])))
                    .ToList();
        }

        /// <summary>
        /// The relationships the catalog lists, as the engine reads MSysObjects: each one's name, type, id, flags,
        /// and whether it is in the Relationships container and has an owner.
        /// </summary>
        private static List<string> CatalogRelationships(string path)
        {
            using (var db = Database.Open(path))
            {
                var catalog = db.GetTable("MSysObjects").Rows.ToList();
                var container = catalog.Single(r => Convert.ToInt32(r["Type"]) == 3 && (string)r["Name"] == "Relationships")["Id"];
                return catalog.Where(r => Convert.ToInt32(r["Type"]) == 8)
                    .Select(r => r["Name"] + ": type " + r["Type"] + " id " + r["Id"] + " flags " + r["Flags"]
                        + " in the container " + Equals(r["ParentId"], container) + " owned " + (r["Owner"] != null))
                    .ToList();
            }
        }

        /// <summary>Every row of the tables, as ACE reads them.</summary>
        private static List<string> Rows(string path, params string[] tables)
        {
            return tables.SelectMany(table => Written(AceTable(path, table)).Select(row => table + ": " + row)).ToList();
        }

        /// <summary>What ACE makes of a statement: "made", or the kind of refusal.</summary>
        private static string Ace(string path, string sql)
        {
            try { Execute(path, sql); return "made"; }
            catch (OleDbException e)
            {
                return e.Message.Contains("related record is required") ? "refused: no parent"
                     : e.Message.Contains("related records") ? "refused: has children"
                     : e.Message.Contains("duplicate values") ? "refused: duplicate"
                     : "refused: " + e.Message;
            }
        }

        /// <summary>What the engine, keeping relationships, makes of a change: "made", or that it was refused.</summary>
        private static string Engine(string path, Action<Database> change)
        {
            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                try { change(db); return "made"; }
                catch (InvalidOperationException) { return "refused"; }
            }
        }

        /// <summary>Two listings are the same, line for line; a failure shows both whole.</summary>
        private static void Same(List<string> expected, List<string> actual)
        {
            Assert.True(expected.SequenceEqual(actual),
                "expected:\n  " + string.Join("\n  ", expected) + "\nactual:\n  " + string.Join("\n  ", actual));
        }

        private static int Count(string path, string from, string where)
        {
            return Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM " + from + " WHERE " + where));
        }

        // ── A column ─────────────────────────────────────────────────────────

        private static readonly DateTime When = new DateTime(2026, 11, 18, 9, 30, 0);

        private static Values NewRow(int id, string by)
        {
            return V("Id", id, "K", id % 5 + 1, "T", by + id, "X", id * 10, "S", by + " " + id, "M", new string((char)('a' + id % 26), 30 + id * 70),
                     "Y", id % 2 == 0, "D", When.AddDays(id), "W", id / 8.0);
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void ace_lists_the_columns_the_engine_adds_reads_old_rows_as_null_and_new_rows_values_and_goes_on_writing(bool general, bool accessMade)
        {
            var path = ParentAndChild(general, accessMade);
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                c.AddColumn(Long("X"));
                c.AddColumn(Text("S", 20));
                c.AddColumn(new ColumnDefinition("M", DataType.Memo));
                c.AddColumn(new ColumnDefinition("Y", DataType.Boolean));
                c.AddColumn(new ColumnDefinition("D", DataType.DateTime));
                c.AddColumn(new ColumnDefinition("W", DataType.Double));
                for (var i = 61; i <= 70; i++) c.Insert(EngineRow(NewRow(i, "engine")));
            }

            // dbLong 4, dbText 10, dbMemo 12, dbBoolean 1, dbDate 8, dbDouble 7
            Assert.Equal(new[] { "Id:4", "K:4", "T:10", "X:4", "S:10", "M:12", "Y:1", "D:8", "W:7" }, DaoFields(path, "C"));

            // The sixty rows that were there: null in every new column, and No in the Yes/No one.
            Assert.Equal(60, Count(path, "C", "X IS NULL AND S IS NULL AND M IS NULL AND D IS NULL AND W IS NULL AND Y = False"));
            Assert.Equal(10, Count(path, "C", "X IS NOT NULL"));

            // The engine reads every row as ACE reads it, and from here both write.
            var subject = new Subject(path, "C", "Id");
            Assert.Equal("null", Written(subject[7]["X"]));
            Assert.Equal("no", Written(subject[7]["Y"]));
            Assert.Equal(Written(NewRow(66, "engine")["M"]), Written(subject[66]["M"]));
            Assert.Equal(Written(new[] { NewRow(66, "engine") }), Written(new[] { subject[66] }));

            for (var i = 71; i <= 80; i++) subject.AceInserts(NewRow(i, "ace"));
            subject.AceUpdates(2, V("X", 222, "S", "set by ace", "M", new string('z', 5000), "Y", true, "D", When, "W", 2.5));
            subject.EngineUpdates(3, V("X", 333, "S", "set by engine", "M", new string('y', 5000), "Y", true, "D", When, "W", 3.5));
            subject.EngineUpdates(71, V("X", null, "M", null, "Y", false));
            subject.AceUpdates(61, V("S", null, "D", null));
            subject.EngineInserts(NewRow(81, "engine"));
            subject.AceDeletes(4, 62, 72);
            subject.EngineDeletes(5, 63, 73);
            subject.Check("after ACE and the engine both wrote the columns the engine added");

            Assert.Equal(new[] { "Id:4", "K:4", "T:10", "X:4", "S:10", "M:12", "Y:1", "D:8", "W:7" }, DaoFields(Compacted(path), "C"));
        }

        private string Compacted(string path)
        {
            return Compact(path);   // beside the file, in the test's own folder, which goes with the test
        }

        // ── An index ─────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Kinds))]
        public void ace_answers_through_the_indexes_the_engine_adds_adds_to_them_and_keeps_them_unique(bool general, bool accessMade)
        {
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);

            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                c.AddIndex(new IndexDefinition("ixT", "T"));
                c.AddIndex(new IndexDefinition("uxKT", "K", "T") { IsUnique = true, DescendingColumns = { "T" } });
                c.AddIndex(new IndexDefinition("ixKign", "K") { IgnoreNulls = true });
            }
            Execute(twin,
                "CREATE INDEX ixT ON C (T)",
                "CREATE UNIQUE INDEX uxKT ON C (K, T DESC)",
                "CREATE INDEX ixKign ON C (K) WITH IGNORE NULL");

            // DAO lists the engine's indexes as it lists the ones ACE made.
            Same(DaoIndexes(twin, "C"), DaoIndexes(path, "C"));

            // A seek is answered by the index's tree alone.
            Assert.Equal(17, DaoSeek(path, "C", "ixT", "c17"));
            Assert.Equal(17, DaoSeek(path, "C", "uxKT", 3, "C17"));
            Assert.Null(DaoSeek(path, "C", "ixT", "c61"));
            Assert.Null(DaoSeek(path, "C", "uxKT", 4, "c17"));
            Assert.NotNull(DaoSeek(path, "C", "ixKign", 5));

            // ACE writes into the trees the engine built, between its keys, and they stay unique.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 61; i <= 900; i++)
                    using (var command = new OleDbCommand("INSERT INTO C (Id, K, T) VALUES (" + i + ", " + (i % 5 + 1) + ", 'c" + i + "')", connection))
                        command.ExecuteNonQuery();
            }
            Assert.Equal("refused: duplicate", Ace(path, "INSERT INTO C (Id, K, T) VALUES (5000, 3, 'C17')"));
            Assert.Equal("made", Ace(path, "INSERT INTO C (Id, K, T) VALUES (5001, 4, 'C17')"));
            Assert.Equal("made", Ace(path, "INSERT INTO C (Id, K, T) VALUES (5002, NULL, 'no k')"));

            Assert.Equal(617, DaoSeek(path, "C", "ixT", "c617"));
            Assert.Equal(617, DaoSeek(path, "C", "uxKT", 3, "c617"));
            Assert.Equal(5001, DaoSeek(path, "C", "uxKT", 4, "c17"));
            foreach (var literal in new[] { "'c17'", "'c617'", "'c61'", "'nothing'" })
                Assert.Equal(Count(path, "C", "T & '' = " + literal), Count(path, "C", "T = " + literal));
            for (var k = 1; k <= 5; k++)
                Assert.Equal(Count(path, "C", "K + 0 = " + k), Count(path, "C", "K = " + k));
            Assert.Equal(1, Count(path, "C", "K IS NULL"));

            // The engine goes on keeping them: it finds ACE's rows through them, adds its own, and refuses a duplicate.
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                Assert.Equal(617, c.CreateIndexCursor("ixT").FindRow("T", "c617")["Id"]);
                c.Insert(new Row { ["Id"] = 6000, ["K"] = 2, ["T"] = "engine row" });
                Assert.Throws<ConstraintViolationException>(() => c.Insert(new Row { ["Id"] = 6001, ["K"] = 2, ["T"] = "ENGINE ROW" }));
                c.DeleteRow("Id", 618);
                c.UpdateByPrimaryKey(619, new Row { ["T"] = "moved" });
            }
            Assert.Equal(6000, DaoSeek(path, "C", "ixT", "engine row"));
            Assert.Equal(6000, DaoSeek(path, "C", "uxKT", 2, "engine row"));
            Assert.Null(DaoSeek(path, "C", "ixT", "c618"));
            Assert.Null(DaoSeek(path, "C", "ixT", "c619"));
            Assert.Equal(619, DaoSeek(path, "C", "ixT", "moved"));
            AssertEngineReadsWhatAceReads(path, "C");

            var compacted = Compacted(path);
            Same(DaoIndexes(twin, "C"), DaoIndexes(compacted, "C"));
            Same(Rows(path, "C"), Rows(compacted, "C"));
            Assert.Equal(902, Count(compacted, "C", "Id > 0"));
            Assert.Equal(617, DaoSeek(compacted, "C", "ixT", "c617"));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void an_index_ace_would_refuse_for_the_rows_there_are_is_refused_by_the_engine_with_the_file_as_it_was(bool general, bool accessMade)
        {
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            foreach (var file in new[] { path, twin })
                Execute(file, "UPDATE C SET T = 'C7' WHERE Id = 8", "UPDATE C SET K = NULL WHERE Id = 9");

            Assert.Equal("refused: duplicate", Ace(twin, "CREATE UNIQUE INDEX uxT ON C (T)"));
            Assert.StartsWith("refused", Ace(twin, "CREATE INDEX ixKreq ON C (K) WITH DISALLOW NULL"));

            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                Assert.Throws<ConstraintViolationException>(() => c.AddIndex(new IndexDefinition("uxT", "T") { IsUnique = true }));
                Assert.Throws<ConstraintViolationException>(() => c.AddIndex(new IndexDefinition("ixKreq", "K") { IsRequired = true }));
            }
            Assert.Equal(before, File.ReadAllBytes(path));
            Same(DaoIndexes(twin, "C"), DaoIndexes(path, "C"));
        }

        // ── A relationship ───────────────────────────────────────────────────

        private const int DontEnforce = 2, UpdateCascade = 256, DeleteCascade = 4096, DeleteSetNull = 8192, LeftJoin = 16777216;

        /// <summary>
        /// Each kind of database with each kind of relationship: cascades, enforced alone, set to null, and not
        /// enforced. Without <see cref="Sweeps.Full"/>, four of the sixteen: each kind of database once and each kind
        /// of relationship once, so that a change to how the engine writes a relationship's attributes, or to what it
        /// makes of one in either sort order or on either maker's tables, still fails one of them.
        /// </summary>
        public static IEnumerable<object[]> Relationships()
        {
            var each = new[] { UpdateCascade | DeleteCascade, 0, DeleteSetNull, DontEnforce | LeftJoin };
            var k = 0;
            foreach (var kind in Kinds())
            {
                var turn = k++;
                for (var a = 0; a < each.Length; a++)
                    if (Sweeps.Full || a == turn)
                        yield return new[] { kind[0], kind[1], each[a] };
            }
        }

        private static RelationshipDefinition Definition(int attributes)
        {
            return new RelationshipDefinition("P", "K", "C", "K")
            {
                Name = "PC",
                HasReferentialIntegrity = (attributes & DontEnforce) == 0,
                CascadeUpdates = (attributes & UpdateCascade) != 0,
                CascadeDeletes = (attributes & DeleteCascade) != 0,
                CascadeNullOnDelete = (attributes & DeleteSetNull) != 0,
                JoinType = (attributes & LeftJoin) != 0 ? JoinType.LeftOuter : JoinType.Inner,
            };
        }

        [Theory]
        [MemberData(nameof(Relationships))]
        public void a_relationship_the_engine_creates_is_the_one_dao_creates_and_ace_and_the_engine_keep_it(bool general, bool accessMade, int attributes)
        {
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            var enforced = (attributes & DontEnforce) == 0;

            // An index on the foreign key that is not a plain one: Access gives the relationship one of its own.
            foreach (var file in new[] { path, twin })
                Execute(file, "CREATE INDEX ixKign ON C (K) WITH IGNORE NULL");

            DaoRelate(twin, "PC", "P", "K", "C", "K", attributes);
            using (var db = Database.Open(path))
            {
                var created = db.CreateRelationship(Definition(attributes));
                // The attributes as the file holds them, which the engine keeps to itself.
                Assert.Equal(attributes, (int)typeof(Relationship).GetProperty("Flags", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(created));
            }

            // The same relationship, to DAO and in the files.
            Same(DaoRelations(twin), DaoRelations(path));
            Assert.Equal(new[] { "PC: P -> C attributes " + attributes + " fields K>K" }, DaoRelations(path));
            Same(RelationshipRows(twin), RelationshipRows(path));
            Same(CatalogRelationships(twin), CatalogRelationships(path));
            Assert.Single(CatalogRelationships(path));
            foreach (var table in new[] { "P", "C" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }
            Assert.Equal(enforced, Slots(path, "C").Any(slot => slot.StartsWith("PC: ") && slot.Contains(" type 2 side 2 ")));
            Assert.Equal(enforced, Slots(path, "P").Any(slot => slot.StartsWith(".r") && slot.Contains(" type 2 side 1 ") && slot.Contains("other table C")));
            if (enforced) Assert.NotNull(DaoSeek(path, "C", "PC", 4));

            // ACE keeps it in the engine's file as in its own: each statement refused or made in both, the rows the same after.
            foreach (var sql in new[]
            {
                "INSERT INTO C (Id, K, T) VALUES (900, 99, 'orphan')",
                "INSERT INTO C (Id, K, T) VALUES (901, 2, 'child of 2')",
                "INSERT INTO C (Id, K, T) VALUES (902, NULL, 'child of none')",
                "UPDATE C SET K = 77 WHERE Id = 1",
                "UPDATE P SET K = 50 WHERE K = 5",
                "DELETE FROM P WHERE K = 1",
            })
            {
                var inTwin = Ace(twin, sql);
                Assert.True(inTwin == Ace(path, sql), sql + ": ACE " + inTwin + " it in the file whose relationship DAO made, and not in the engine's");
                Same(Rows(twin, "P", "C"), Rows(path, "P", "C"));
            }
            Assert.Equal(enforced ? "refused: no parent" : "made", Ace(path, "INSERT INTO C (Id, K, T) VALUES (903, 98, 'orphan')"));
            Assert.Equal(enforced ? "refused: no parent" : "made", Ace(twin, "INSERT INTO C (Id, K, T) VALUES (903, 98, 'orphan')"));
            if (attributes == (UpdateCascade | DeleteCascade))
            {
                Assert.Equal(12, Count(path, "C", "K = 50"));
                Assert.Equal(0, Count(path, "C", "K = 1 OR K = 5"));
            }
            if (attributes == DeleteSetNull) Assert.Equal(13, Count(path, "C", "K IS NULL"));

            // The engine keeps it too: what it makes of a change to its file is what ACE makes of it in the twin.
            var changes = new (string Sql, Action<Database> Change)[]
            {
                ("INSERT INTO C (Id, K, T) VALUES (910, 97, 'orphan')", db => db.GetTable("C").Insert(new Row { ["Id"] = 910, ["K"] = 97, ["T"] = "orphan" })),
                ("INSERT INTO C (Id, K, T) VALUES (911, 3, 'child of 3')", db => db.GetTable("C").Insert(new Row { ["Id"] = 911, ["K"] = 3, ["T"] = "child of 3" })),
                ("UPDATE P SET K = 40 WHERE K = 4", db => db.GetTable("P").UpdateByPrimaryKey(4, new Row { ["K"] = 40 })),
                ("DELETE FROM P WHERE K = 2", db => db.GetTable("P").DeleteRow("K", 2)),
            };
            foreach (var (sql, change) in changes)
            {
                var inTwin = Ace(twin, sql);
                var byEngine = Engine(path, change);
                Assert.True((inTwin == "made") == (byEngine == "made"), sql + ": ACE " + inTwin + " it, and the engine " + byEngine + " it");
                Same(Rows(twin, "P", "C"), Rows(path, "P", "C"));
                AssertEngineReadsWhatAceReads(path, "C");
            }

            // And after all that ACE still answers through the relationship's index as a scan answers.
            if (enforced)
                foreach (var k in new[] { 2, 3, 40, 50, 77 })
                    Assert.Equal(Count(path, "C", "K + 0 = " + k), Count(path, "C", "K = " + k));

            var compacted = Compacted(path);
            Same(DaoRelations(twin), DaoRelations(compacted));
            Same(Rows(path, "P", "C"), Rows(compacted, "P", "C"));
            Assert.Equal(enforced ? "refused: no parent" : "made", Ace(compacted, "INSERT INTO C (Id, K, T) VALUES (920, 96, 'orphan')"));
        }

        // The foreign key is unique too, so the relationship is one to one. Access's Relationships window marks one so
        // (dbRelationUnique) and so does the engine, as Jackcess does; DAO marks it only when told to, so the twin
        // is told. With the mark Access makes the foreign key's index a unique one, which shares the tree of the
        // unique index already on the column - and so does the engine. (Jackcess gives it a plain index.)
        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_relationship_to_a_unique_foreign_key_is_one_to_one_with_a_unique_index_as_dao_makes_it(bool general, bool accessMade)
        {
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);

            DaoRelate(twin, "PU", "P", "K", "U", "K", 1 /* dbRelationUnique */);
            using (var db = Database.Open(path))
            {
                var created = db.CreateRelationship(new RelationshipDefinition("P", "K", "U", "K") { Name = "PU", HasReferentialIntegrity = true });
                Assert.True(created.IsOneToOne);
            }

            Same(DaoRelations(twin), DaoRelations(path));
            Same(RelationshipRows(twin), RelationshipRows(path));
            foreach (var table in new[] { "P", "U" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }
            Assert.Contains(Slots(path, "U"), slot => slot.StartsWith("PU: ") && slot.Contains(" flags 0x81 "));

            foreach (var sql in new[] { "INSERT INTO U (Id, K) VALUES (4, 9)", "INSERT INTO U (Id, K) VALUES (5, 3)", "INSERT INTO U (Id, K) VALUES (6, 4)", "DELETE FROM P WHERE K = 4" })
            {
                Assert.Equal(Ace(twin, sql), Ace(path, sql));
                Same(Rows(twin, "P", "U"), Rows(path, "P", "U"));
            }
        }

        // DAO marks a relationship one to one when it is told to, whatever indexes there are: the foreign key then
        // gets a unique index, built over its rows, and two rows that refer to one parent refuse the relationship.
        // And told nothing, DAO leaves a relationship between two unique columns unmarked, its index an ordinary
        // one. RelationshipDefinition.IsOneToOne says either (#37).
        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_relationship_is_one_to_one_or_not_as_the_caller_says_as_dao_makes_it(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);

            // C.K has many rows for each parent: asked to be one to one, the relationship is refused by both.
            var dao = Assert.ThrowsAny<Exception>(() => DaoRelate(twin, "PC", "P", "K", "C", "K", 1 /* dbRelationUnique */));
            Assert.DoesNotContain("made", dao.Message);
            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
                Assert.Throws<ConstraintViolationException>(() => db.CreateRelationship(
                    new RelationshipDefinition("P", "K", "C", "K") { Name = "PC", HasReferentialIntegrity = true, IsOneToOne = true }));
            Assert.Equal(before, File.ReadAllBytes(path));

            // With one row for each parent, it is made: the foreign key's index a unique one, built for it.
            foreach (var file in new[] { path, twin })
                Execute(file, "DELETE FROM C WHERE Id > 5");
            DaoRelate(twin, "PC", "P", "K", "C", "K", 1);
            using (var db = Database.Open(path))
            {
                var created = db.CreateRelationship(new RelationshipDefinition("P", "K", "C", "K") { Name = "PC", HasReferentialIntegrity = true, IsOneToOne = true });
                Assert.True(created.IsOneToOne);
            }

            // U.K is unique, as P.K is: told not to mark it, the engine leaves it as DAO leaves it untold.
            DaoRelate(twin, "PU", "P", "K", "U", "K", 0);
            using (var db = Database.Open(path))
            {
                var created = db.CreateRelationship(new RelationshipDefinition("P", "K", "U", "K") { Name = "PU", HasReferentialIntegrity = true, IsOneToOne = false });
                Assert.False(created.IsOneToOne);
            }

            Same(DaoRelations(twin), DaoRelations(path));
            Assert.Equal(new[] { "PC: P -> C attributes 1 fields K>K", "PU: P -> U attributes 0 fields K>K" }, DaoRelations(path));
            Same(RelationshipRows(twin), RelationshipRows(path));
            foreach (var table in new[] { "P", "C", "U" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }
            Assert.Contains(Slots(path, "C"), slot => slot.StartsWith("PC: ") && slot.Contains(" flags 0x81 "));

            foreach (var sql in new[]
            {
                "INSERT INTO C (Id, K, T) VALUES (100, 3, 'a second child of 3')", "INSERT INTO C (Id, K, T) VALUES (101, 99, 'orphan')",
                "INSERT INTO C (Id, K, T) VALUES (102, NULL, 'child of none')", "INSERT INTO U (Id, K) VALUES (4, 4)", "INSERT INTO U (Id, K) VALUES (5, 4)",
                "DELETE FROM P WHERE K = 5",
            })
            {
                Assert.Equal(Ace(twin, sql), Ace(path, sql));
                Same(Rows(twin, "P", "C", "U"), Rows(path, "P", "C", "U"));
            }
            using (var db = Database.Open(path))
                Assert.Throws<ConstraintViolationException>(() => db.GetTable("C").Insert(new Row { ["Id"] = 103, ["K"] = 2, ["T"] = "a second child of 2" }));
            Same(DaoRelations(twin), DaoRelations(Compacted(path)));
        }

        /// <summary>S (Id, M), keyed by Id, its rows referring to one another by M: ACE's or the engine's.</summary>
        private string Staff(bool general, bool accessMade)
        {
            var path = NewDatabase(general, accessMade);
            if (accessMade)
            {
                Execute(path,
                    "CREATE TABLE S (Id LONG CONSTRAINT pk PRIMARY KEY, M LONG)",
                    "INSERT INTO S (Id, M) VALUES (1, NULL)", "INSERT INTO S (Id, M) VALUES (2, 1)", "INSERT INTO S (Id, M) VALUES (3, 3)");
                return path;
            }
            using (var db = Database.Open(path))
            {
                var s = db.CreateTable("S", new[] { Long("Id"), Long("M") }, primaryKey: "Id");
                s.Insert(new Row { ["Id"] = 1 });
                s.Insert(new Row { ["Id"] = 2, ["M"] = 1 });
                s.Insert(new Row { ["Id"] = 3, ["M"] = 3 });
            }
            return path;
        }

        // A table related to itself has both of the relationship's indexes. DAO numbers the foreign key's first and
        // names the referenced side's by its own number after it; the engine wrote them the other way about.
        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_table_related_to_itself_has_the_two_indexes_dao_gives_it(bool general, bool accessMade)
        {
            var path = Staff(general, accessMade);
            var twin = Staff(general, accessMade);

            DaoRelate(twin, "Reports", "S", "Id", "S", "M", UpdateCascade);
            using (var db = Database.Open(path))
                db.CreateRelationship(new RelationshipDefinition("S", "Id", "S", "M") { Name = "Reports", HasReferentialIntegrity = true, CascadeUpdates = true });

            Same(DaoRelations(twin), DaoRelations(path));
            Same(RelationshipRows(twin), RelationshipRows(path));
            Same(DaoIndexes(twin, "S"), DaoIndexes(path, "S"));
            Same(Slots(twin, "S"), Slots(path, "S"));
            Assert.Equal(Slots(path, "S").Count, Slots(path, "S").Select(slot => slot.Substring(0, slot.IndexOf(':'))).Distinct().Count());

            foreach (var sql in new[]
            {
                "INSERT INTO S (Id, M) VALUES (4, 9)", "INSERT INTO S (Id, M) VALUES (5, 2)", "INSERT INTO S (Id, M) VALUES (6, 6)",
                "UPDATE S SET Id = 10 WHERE Id = 1", "DELETE FROM S WHERE Id = 2", "DELETE FROM S WHERE Id = 5",
            })
            {
                Assert.Equal(Ace(twin, sql), Ace(path, sql));
                Same(Rows(twin, "S"), Rows(path, "S"));
            }
            Same(DaoRelations(twin), DaoRelations(Compacted(path)));
        }

        // Access takes no name with a period, an exclamation mark, a grave accent or a square bracket in it, nor one
        // that begins with a space - so nothing can be given the name Access gives a referenced table's index for a
        // relationship (.rB, .rC...). The engine took any name: ".rB" for a table related to itself, whose own
        // index would be named so, left two indexes of one name in the table.
        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_name_dao_does_not_allow_is_refused_by_the_engine_with_the_file_as_it_was(bool general, bool accessMade)
        {
            var path = Staff(general, accessMade);
            var twin = Staff(general, accessMade);

            foreach (var name in new[] { ".rB", ".rC", "a.b", "wow!", "[bracketed]", "gra`ve", " leading", "tab\tbed" })
            {
                var dao = Assert.ThrowsAny<Exception>(() => DaoRelate(twin, name, "S", "Id", "S", "M", 0));
                Assert.Contains("not a valid name", dao.GetBaseException().Message);
                // (ACE's SQL takes the space off the front of a bracketed name, and so makes that index.)
                if (!name.StartsWith(" "))
                    Assert.StartsWith("refused", Ace(twin, "CREATE INDEX [" + name.Replace("]", "]]") + "] ON S (M)"));

                var before = File.ReadAllBytes(path);
                using (var db = Database.Open(path))
                {
                    Assert.Throws<ArgumentException>(() => db.CreateRelationship(
                        new RelationshipDefinition("S", "Id", "S", "M") { Name = name, HasReferentialIntegrity = true }));
                    Assert.Throws<ArgumentException>(() => db.CreateRelationship(new RelationshipDefinition("S", "Id", "S", "M") { Name = name }));
                    Assert.Throws<ArgumentException>(() => db.GetTable("S").AddIndex(new IndexDefinition(name, "M")));
                }
                Assert.Equal(before, File.ReadAllBytes(path));
            }
            Assert.Empty(DaoRelations(twin));
            Assert.Empty(DaoRelations(path));
            Same(DaoIndexes(twin, "S"), DaoIndexes(path, "S"));

            // What DAO does allow in a name, the engine allows: a space inside or at the end, quotes, a slash, a colon.
            foreach (var name in new[] { "plain name", "trailing ", "quo\"te", "apos'trophe", "sl/ash", "co:lon" })
            {
                DaoRelate(twin, name, "S", "Id", "S", "M", DontEnforce);
                using (var db = Database.Open(path))
                    db.CreateRelationship(new RelationshipDefinition("S", "Id", "S", "M") { Name = name });
            }
            Same(DaoRelations(twin), DaoRelations(path));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_relationship_dao_refuses_is_refused_by_the_engine_with_the_file_as_it_was(bool general, bool accessMade)
        {
            var path = ParentAndChild(general, accessMade);
            var twin = ParentAndChild(general, accessMade);
            foreach (var file in new[] { path, twin })
                Execute(file, "INSERT INTO C (Id, K, T) VALUES (700, 99, 'orphan')");

            // Rows that refer to no parent, which is the data breaking the rule asked for; a referenced column with
            // no unique index and columns of different types, which are relationships that cannot be made at all.
            var refusals = new (string Table, string Field, string ForeignTable, string ForeignField, Type Refusal)[]
            {
                ("P", "K", "C", "K", typeof(ConstraintViolationException)),
                ("C", "K", "U", "K", typeof(InvalidOperationException)),
                ("P", "T", "C", "K", typeof(InvalidOperationException)),
            };
            var before = File.ReadAllBytes(path);
            foreach (var (table, field, foreignTable, foreignField, refusal) in refusals)
            {
                var dao = Assert.ThrowsAny<Exception>(() => DaoRelate(twin, "Refused", table, field, foreignTable, foreignField, 0));
                Assert.DoesNotContain("made", dao.Message);
                using (var db = Database.Open(path))
                    Assert.Throws(refusal, () => db.CreateRelationship(
                        new RelationshipDefinition(table, field, foreignTable, foreignField) { Name = "Refused", HasReferentialIntegrity = true }));
                Assert.Equal(before, File.ReadAllBytes(path));
            }
            Assert.Empty(DaoRelations(twin));
            Assert.Empty(DaoRelations(path));
            Same(DaoIndexes(twin, "C"), DaoIndexes(path, "C"));
        }

        // A Decimal key is one Access indexes and the engine writes no keys for: asked to index one, or to make a
        // relationship that needs an index on one, it refuses and changes nothing; ACE goes on using the file.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void an_index_or_a_relationship_on_a_decimal_column_is_refused_with_the_file_as_it_was(bool general)
        {
            var path = NewDatabase(general, accessMade: true);
            Execute(path,
                "CREATE TABLE DP (K DECIMAL(10, 0) CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE DC (Id LONG, K DECIMAL(10, 0), T TEXT(10))",
                "INSERT INTO DP (K, T) VALUES (1, 'one')",
                "INSERT INTO DC (Id, K, T) VALUES (1, 1, 'child')");

            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                Assert.Throws<NotSupportedException>(() => db.GetTable("DC").AddIndex(new IndexDefinition("ixK", "K")));
                Assert.Throws<NotSupportedException>(() => db.CreateRelationship(
                    new RelationshipDefinition("DP", "K", "DC", "K") { Name = "Decimals", HasReferentialIntegrity = true }));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            // What the engine does add to that table - a column, an index on another - ACE takes.
            using (var db = Database.Open(path))
            {
                var dc = db.GetTable("DC");
                dc.AddColumn(Long("X"));
                dc.AddIndex(new IndexDefinition("ixId", "Id") { IsUnique = true });
                dc.Insert(new Row { ["Id"] = 2, ["K"] = 2m, ["T"] = "engine", ["X"] = 20 });
            }
            Assert.Equal("refused: duplicate", Ace(path, "INSERT INTO DC (Id, K, T, X) VALUES (2, 3, 'twice', 30)"));
            Assert.Equal("made", Ace(path, "INSERT INTO DC (Id, K, T, X) VALUES (3, 3, 'ace', 30)"));
            Assert.Equal(2, DaoSeek(path, "DC", "ixId", 2));
            AssertEngineReadsWhatAceReads(path, "DC");
        }
    }
}
