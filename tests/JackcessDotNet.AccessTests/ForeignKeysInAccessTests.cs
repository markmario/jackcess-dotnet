using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// The managed engine's foreign-key enforcement, refereed by ACE. Each case builds
    /// the same small database twice - tables, relationships and rows all made by ACE - then makes one change through
    /// ACE in the first and the same change through the engine, with <see cref="Database.EnforceForeignKeys"/> set, in
    /// the second. Both must refuse it or both must make it, and every table must then hold the same rows, whichever
    /// of the two reads the engine's file. Each test also says what the outcome is, so two engines agreeing on the
    /// wrong thing fails too.
    /// <para>
    /// Rows read back are not the whole of it: ACE keeps a relationship through the index on its foreign key, and a
    /// row a cascade moved without its index entry is one ACE no longer finds - it then lets the row's parent be
    /// deleted. So after every change the engine's file is also asked what only its indexes can answer - every
    /// primary key and every relationship's columns (<see cref="AssertThePrimaryKeysAnswer"/>,
    /// <see cref="AssertTheIndexesAnswer"/>) - compacted by DAO and asked again, and then put through further changes
    /// by ACE itself, beside the copy ACE alone has touched: ACE must refuse and allow the same things in both
    /// (<see cref="AssertAccessGoesOnTheSame"/>).
    /// </para>
    /// </summary>
    public class ForeignKeysInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        // ── The two sides ────────────────────────────────────────────────────

        /// <summary>What a change came to: refused or made, and every table's rows after it.</summary>
        private sealed class Outcome
        {
            public bool Refused;
            public string Message;
            public SortedDictionary<string, List<string>> Tables;

            public List<string> Rows(string table) { return Tables[table]; }
        }

        /// <summary>
        /// Whether the files a case builds are copies of an Access 2010 database whose text sorts in General
        /// (#23), from the engine's corpus, and not new ones in General - Legacy.
        /// </summary>
        private bool _inGeneral;

        private string Build(IEnumerable<string> setup)
        {
            string path;
            if (_inGeneral)
            {
                AccessReferee.SkipUnlessAceOpensAccess2010();
                var corpus = typeof(ForeignKeysInAccessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Single(a => a.Key == "EngineCorpusDir").Value;
                path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + ".accdb");
                File.Copy(Path.Combine(corpus, "V2010", "common1V2010.accdb"), path);

                // Its own tables go: a case is about the tables it makes.
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
                {
                    connection.Open();
                    var tables = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null).Rows.Cast<System.Data.DataRow>()
                        .Where(r => (string)r["TABLE_TYPE"] == "TABLE").Select(r => (string)r["TABLE_NAME"]).ToList();
                    foreach (var table in tables)
                        using (var command = new OleDbCommand("DROP TABLE [" + table + "]", connection))
                            command.ExecuteNonQuery();
                }
            }
            else
            {
                path = _tmp.NewMdbPath();
                Database.Create(path, FileFormat.Access2000).Dispose();
            }
            var steps = setup.ToList();
            foreach (var table in steps.Where(step => step.StartsWith(DaoTable)))
                CreateWithDao(path, table.Substring(DaoTable.Length));
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var sql in steps.Where(step => !step.StartsWith(DaoTable)))
                    using (var command = new OleDbCommand(sql, connection))
                        command.ExecuteNonQuery();
            }
            return path;
        }

        /// <summary>
        /// A setup step that makes a table through DAO, as Access's table designer does, and not by SQL: "name: column
        /// TYPE, column TYPE". It matters for one type. A Replication ID column made by SQL is kept among a row's
        /// variable-length values, where the engine does not write one; made by DAO it is kept as the engine keeps it.
        /// </summary>
        private const string DaoTable = "DAO TABLE ";

        private static void CreateWithDao(string path, string definition)
        {
            const BindingFlags Call = BindingFlags.InvokeMethod, Get = BindingFlags.GetProperty;
            var types = new Dictionary<string, int> { { "LONG", 4 }, { "TEXT", 10 }, { "GUID", 15 } };   // dbLong, dbText, dbGUID
            var name = definition.Substring(0, definition.IndexOf(':')).Trim();
            var columns = definition.Substring(definition.IndexOf(':') + 1).Split(',').Select(c => c.Trim().Split(' ')).ToList();

            var held = new List<object>();
            Func<object, string, BindingFlags, object[], object> on = (target, member, how, arguments) =>
            {
                var result = target.GetType().InvokeMember(member, how, null, target, arguments);
                if (result != null && System.Runtime.InteropServices.Marshal.IsComObject(result)) held.Add(result);
                return result;
            };
            var engine = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            held.Add(engine);
            try
            {
                var database = on(engine, "OpenDatabase", Call, new object[] { path });
                try
                {
                    var table = on(database, "CreateTableDef", Call, new object[] { name });
                    var fields = on(table, "Fields", Get, null);
                    foreach (var column in columns)
                        on(fields, "Append", Call, new[] { on(table, "CreateField", Call, new object[] { column[0], types[column[1]] }) });
                    on(on(database, "TableDefs", Get, null), "Append", Call, new[] { table });
                }
                finally { on(database, "Close", Call, null); }
            }
            finally
            {
                held.Reverse();
                foreach (var com in held) System.Runtime.InteropServices.Marshal.ReleaseComObject(com);
                Dao.Release(null);
            }
        }

        // A value as text, exactly: a Double or Single to its last bit, and a zero with its sign.
        private static string Show(object value)
        {
            if (value == null || value is DBNull) return "null";
            if (value is float single) value = (double)single;
            if (value is double number)
                return number == 0 && double.IsNegativeInfinity(1 / number) ? "-0" : number.ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static SortedDictionary<string, List<string>> ReadWithAccess(string path)
        {
            var tables = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                var names = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null).Rows.Cast<System.Data.DataRow>()
                    .Where(r => (string)r["TABLE_TYPE"] == "TABLE")
                    .Select(r => (string)r["TABLE_NAME"])
                    .ToList();
                foreach (var name in names)
                {
                    var rows = new List<string>();
                    // Every column, so no index covers the query and ACE reads the rows themselves.
                    using (var command = new OleDbCommand("SELECT * FROM [" + name + "]", connection))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            rows.Add(string.Join(", ", Enumerable.Range(0, reader.FieldCount)
                                .Select(i => reader.GetName(i) + "=" + Show(reader.GetValue(i)))));
                    }
                    rows.Sort(StringComparer.Ordinal);
                    tables[name] = rows;
                }
            }
            return tables;
        }

        private static SortedDictionary<string, List<string>> ReadWithEngine(string path)
        {
            var tables = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            using (var db = Database.Open(path))
            {
                foreach (var name in db.GetTableNames())
                {
                    var table = db.GetTable(name);
                    var rows = table.Rows.ToList()
                        .Select(row => string.Join(", ", table.Columns
                            .Select(c => c.Name + "=" + Show(row.TryGetValue(c.Name, out var value) ? value : null))))
                        .ToList();
                    rows.Sort(StringComparer.Ordinal);
                    tables[name] = rows;
                }
            }
            return tables;
        }

        private static string Describe(SortedDictionary<string, List<string>> tables)
        {
            return string.Join(Environment.NewLine, tables.Select(t => t.Key + ": " + string.Join(" | ", t.Value)));
        }

        /// <summary>
        /// Makes one change through ACE and the same change through the engine, each in its own copy of the database
        /// <paramref name="setup"/> builds, and requires the two to agree. Returns what both came to.
        /// </summary>
        private Outcome Referee(IEnumerable<string> setup, string sql, Action<Database> change)
        {
            var steps = setup.ToList();

            var accessPath = Build(steps);
            var before = KeysIn(accessPath, Relations(accessPath));
            var access = new Outcome();
            try
            {
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(accessPath)))
                using (var command = new OleDbCommand(sql, connection))
                {
                    connection.Open();
                    command.ExecuteNonQuery();
                }
            }
            catch (OleDbException refused)
            {
                access.Refused = true;
                access.Message = refused.Message;
            }
            access.Tables = ReadWithAccess(accessPath);

            var enginePath = Build(steps);
            var engine = new Outcome();
            try
            {
                using (var db = Database.Open(enginePath))
                {
                    db.EnforceForeignKeys = true;
                    change(db);
                }
            }
            catch (InvalidOperationException refused)
            {
                engine.Refused = true;
                engine.Message = refused.Message;
            }
            engine.Tables = ReadWithEngine(enginePath);

            Assert.True(access.Refused == engine.Refused,
                sql + ": ACE " + (access.Refused ? "refused it (" + access.Message + ")" : "made the change")
                + ", the engine " + (engine.Refused ? "refused it (" + engine.Message + ")" : "made the change"));
            Assert.Equal(Describe(access.Tables), Describe(engine.Tables));
            // And ACE reads from the engine's file what the engine does.
            Assert.Equal(Describe(access.Tables), Describe(ReadWithAccess(enginePath)));

            // Through the indexes, where a row the engine moved without its entry would be lost; then the same of
            // the file DAO makes of the engine's; then ACE's own further changes, in both copies.
            var relations = Relations(enginePath);
            var keys = before.Concat(KeysIn(accessPath, relations)).Distinct().ToList();
            AssertThePrimaryKeysAnswer(accessPath, enginePath, keys, sql);
            AssertTheIndexesAnswer(accessPath, enginePath, relations, keys, sql);
            // Compacted, beside ACE's own copy compacted: DAO makes the same of both. (Where ACE itself left a row
            // without its parent - it does, nulling a Yes/No - that is the same complaint in both.)
            var compacted = Compact(enginePath);
            var accessCompacted = Compact(accessPath);
            Assert.Equal(Describe(ReadWithAccess(accessCompacted)), Describe(ReadWithAccess(compacted)));
            AssertThePrimaryKeysAnswer(accessCompacted, compacted, keys, sql + ", compacted");
            AssertTheIndexesAnswer(accessCompacted, compacted, relations, keys, sql + ", compacted");
            AssertAccessGoesOnTheSame(accessPath, enginePath, relations, keys, sql);
            return access;
        }

        // ── What only an index can answer ────────────────────────────────────

        /// <summary>One side of a relationship ACE enforces: a table and the columns of its key or foreign key.</summary>
        private sealed class Relation
        {
            public string Name, Parent, Child;
            public List<string> ParentColumns, ChildColumns;
        }

        /// <summary>A value some row has, or had, in a column of a relationship, as SQL writes it.</summary>
        private sealed class KeyValue : IEquatable<KeyValue>
        {
            public string Table, Column, Literal;

            /// <summary>
            /// The value as a condition ACE answers by reading the rows, {0} standing for the column: "+ 0", "&amp; ''"
            /// and the like keep it off the column's index.
            /// </summary>
            public string ScanFormat;

            public string ByIndex(string column) { return "[" + column + "] = " + Literal; }
            public string ByScan(string column) { return ScanFormat.Replace("{0}", "[" + column + "]"); }

            public bool Equals(KeyValue other) { return other != null && Table == other.Table && Column == other.Column && Literal == other.Literal; }
            public override bool Equals(object obj) { return Equals(obj as KeyValue); }
            public override int GetHashCode() { return (Table + "\n" + Column + "\n" + Literal).GetHashCode(); }
        }

        private static List<Relation> Relations(string path)
        {
            using (var db = Database.Open(path))
                return db.GetRelationships().Where(r => r.HasReferentialIntegrity)
                    .Select(r => new Relation
                    {
                        Name = r.Name, Parent = r.FromTableName, Child = r.ToTableName,
                        ParentColumns = r.FromColumnNames.ToList(),
                        ChildColumns = r.ToColumnNames.ToList(),
                    })
                    .ToList();
        }

        private static List<string> Query(OleDbConnection connection, string sql)
        {
            var rows = new List<string>();
            using (var command = new OleDbCommand(sql, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    rows.Add(string.Join(", ", Enumerable.Range(0, reader.FieldCount)
                        .Select(i => reader.GetName(i) + "=" + Show(reader.GetValue(i)))));
            }
            rows.Sort(StringComparer.Ordinal);
            return rows;
        }

        /// <summary>Every table's primary-key columns, as the engine reads them from the table's definition.</summary>
        private static List<KeyValuePair<string, string>> PrimaryKeyColumns(string path)
        {
            using (var db = Database.Open(path))
                return db.GetTableNames()
                    .SelectMany(table => db.GetTable(table).Indexes.Where(ix => ix.IsPrimaryKey).Take(1)
                        .SelectMany(ix => ix.Columns.Select(c => new KeyValuePair<string, string>(table, c.Column.Name))))
                    .ToList();
        }

        /// <summary>
        /// Every value the relationships' columns, and every table's primary-key columns, hold in the database at
        /// <paramref name="path"/>.
        /// </summary>
        private static List<KeyValue> KeysIn(string path, List<Relation> relations)
        {
            var keys = new List<KeyValue>();
            var primaryKeys = PrimaryKeyColumns(path);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                var columns = relations.SelectMany(r => r.ParentColumns.Select(c => new { Table = r.Parent, Column = c })
                        .Concat(r.ChildColumns.Select(c => new { Table = r.Child, Column = c })))
                    .Concat(primaryKeys.Select(k => new { Table = k.Key, Column = k.Value }))
                    .Distinct();
                foreach (var column in columns)
                {
                    // A date as the double it is stored as: the DateTime a reader gives does not hold every one.
                    bool isDate;
                    using (var command = new OleDbCommand("SELECT [" + column.Column + "] FROM [" + column.Table + "] WHERE 1 = 0", connection))
                    using (var reader = command.ExecuteReader())
                        isDate = reader.GetFieldType(0) == typeof(DateTime);

                    var select = isDate ? "IIf([" + column.Column + "] Is Null, Null, CDbl([" + column.Column + "]))" : "[" + column.Column + "]";
                    using (var command = new OleDbCommand("SELECT DISTINCT " + select + " FROM [" + column.Table + "]", connection))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0)) continue;
                            var key = KeyOf(reader.GetValue(0), isDate);
                            key.Table = column.Table;
                            key.Column = column.Column;
                            keys.Add(key);
                        }
                    }
                }
            }
            return keys;
        }

        private static KeyValue KeyOf(object value, bool isDate)
        {
            var text = value as string;
            if (text != null)
            {
                var quoted = "'" + text.Replace("'", "''") + "'";
                return new KeyValue { Literal = quoted, ScanFormat = "{0} & '' = " + quoted };
            }
            if (value is bool)
                return new KeyValue { Literal = (bool)value ? "True" : "False", ScanFormat = "{0} + 0 = " + ((bool)value ? "-1" : "0") };
            if (value is Guid)
            {
                var braced = ((Guid)value).ToString("B").ToUpperInvariant();
                return new KeyValue { Literal = "{guid " + braced + "}", ScanFormat = "StrComp({0} & '', '" + braced + "', 1) = 0" };
            }
            var number = Show(value);
            // A Single is compared as a Single: plus 0, or widened, it does not equal the Double written out for it.
            if (value is float)
                return new KeyValue { Literal = number, ScanFormat = "IIf({0} Is Null, Null, CSng({0})) = CSng(" + number + ")" };
            return isDate
                ? new KeyValue { Literal = "CDate(" + number + ")", ScanFormat = "{0} + 0 = " + number }
                : new KeyValue { Literal = number, ScanFormat = "{0} + 0 = " + number };
        }

        /// <summary>
        /// For every value a primary-key column of any table holds or held, the rows ACE finds in
        /// <paramref name="enginePath"/> through the primary key are the rows it finds by reading the table, and the
        /// rows the copy ACE changed has: a row the engine rewrote, or re-keyed, is where its key says it is, and a
        /// key it gave up finds nothing.
        /// </summary>
        private static void AssertThePrimaryKeysAnswer(string accessPath, string enginePath, List<KeyValue> keys, string after)
        {
            var primaryKeys = PrimaryKeyColumns(enginePath);   // read before ACE has the file open
            using (var access = new OleDbConnection(AceVerification.ConnectionString(accessPath)))
            using (var engine = new OleDbConnection(AceVerification.ConnectionString(enginePath)))
            {
                access.Open();
                engine.Open();
                foreach (var column in primaryKeys)
                    foreach (var key in keys.Where(k => k.Table == column.Key && k.Column == column.Value))
                    {
                        var what = "after " + after + ": [" + key.Table + "] WHERE [" + key.Column + "] = " + key.Literal + " (its primary key)";
                        var from = "SELECT * FROM [" + key.Table + "] WHERE ";
                        var byIndex = key.ByIndex(key.Column);
                        var byScan = key.ByScan(key.Column);

                        // What ACE's own copy answers, each way. (The two differ in one case, ACE's own: its index
                        // holds 0 and -0 as two keys, and its arithmetic holds them equal.)
                        var expected = string.Join(" | ", Query(access, from + byIndex));
                        string found;
                        try { found = string.Join(" | ", Query(engine, from + byIndex)); }
                        catch (OleDbException e) { throw new Xunit.Sdk.XunitException(what + ": ACE could not answer through the index: " + e.Message); }
                        Assert.True(expected == found, what + ": through the index '" + found + "', expected '" + expected + "'");
                        var scanned = string.Join(" | ", Query(access, from + byScan));
                        Assert.True(scanned == string.Join(" | ", Query(engine, from + byScan)), what + ": by scan, expected '" + scanned + "'");
                    }
            }
        }

        /// <summary>
        /// For every value a column of a relationship holds or held - the keys the change removed among them - the rows
        /// ACE finds in <paramref name="enginePath"/> through the column's index are the rows it finds by reading the
        /// table, and the rows the copy ACE changed has, asked the same way. ("+ 0" and "&amp; ''" keep ACE off the
        /// index.) A stale entry shows as a row missing, a row too many, or "Not a valid bookmark".
        /// </summary>
        private static void AssertTheIndexesAnswer(string accessPath, string enginePath, List<Relation> relations, List<KeyValue> keys, string after)
        {
            using (var access = new OleDbConnection(AceVerification.ConnectionString(accessPath)))
            using (var engine = new OleDbConnection(AceVerification.ConnectionString(enginePath)))
            {
                access.Open();
                engine.Open();
                foreach (var relation in relations)
                {
                    for (var i = 0; i < relation.ParentColumns.Count; i++)
                    {
                        // The pair's values on either side: a parent's old key is asked of the child, and a child's of the parent.
                        var literals = keys
                            .Where(k => (k.Table == relation.Parent && k.Column == relation.ParentColumns[i])
                                     || (k.Table == relation.Child && k.Column == relation.ChildColumns[i]))
                            .GroupBy(k => k.Literal).Select(g => g.First()).ToList();
                        var sides = new[]
                        {
                            new { Table = relation.Parent, Column = relation.ParentColumns[i] },
                            new { Table = relation.Child, Column = relation.ChildColumns[i] },
                        };
                        foreach (var side in sides)
                        {
                            var column = side.Column;
                            foreach (var key in literals)
                            {
                                var what = "after " + after + ": [" + side.Table + "] WHERE [" + column + "] = " + key.Literal;
                                var from = "SELECT * FROM [" + side.Table + "] WHERE ";
                                var byIndex = key.ByIndex(column);
                                var byScan = key.ByScan(column);

                                var expected = string.Join(" | ", Query(access, from + byIndex));
                                List<string> found;
                                try { found = Query(engine, from + byIndex); }
                                catch (OleDbException e) { throw new Xunit.Sdk.XunitException(what + ": ACE could not answer through the index: " + e.Message); }
                                Assert.True(expected == string.Join(" | ", found), what + ": through the index '" + string.Join(" | ", found) + "', expected '" + expected + "'");
                                var scanned = string.Join(" | ", Query(access, from + byScan));
                                Assert.True(scanned == string.Join(" | ", Query(engine, from + byScan)), what + ": by scan, expected '" + scanned + "'");

                                using (var count = new OleDbCommand("SELECT COUNT(*) FROM [" + side.Table + "] WHERE " + byIndex, engine))
                                    Assert.True(found.Count == Convert.ToInt32(count.ExecuteScalar()), what + ": COUNT through the index is not " + found.Count);
                            }
                        }
                    }
                }
            }
        }

        private string Compact(string path)
        {
            var compacted = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + "-compacted.mdb");
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Activator.CreateInstance(dbeType);
            try { dbeType.InvokeMember("CompactDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, compacted }); }
            finally { Dao.Release(dbe); }
            return compacted;
        }

        /// <summary>
        /// ACE's own next changes, made in the copy the engine changed and in the copy ACE changed, one statement
        /// at a time: each must be refused in both or made in both, and leave the same rows. For every relationship,
        /// every child row is pointed at each key its parent has or had - a key the change removed must be refused,
        /// in a file whose indexes say what its rows say - and then every parent row is deleted, which ACE refuses
        /// or cascades by looking the children up through the foreign key's index.
        /// </summary>
        private static void AssertAccessGoesOnTheSame(string accessPath, string enginePath, List<Relation> relations, List<KeyValue> keys, string after)
        {
            var statements = new List<string>();
            foreach (var relation in relations)
            {
                // Each column's values in turn; for a key of several columns, the first value of each of the others with it.
                var perColumn = relation.ParentColumns
                    .Select(c => keys.Where(k => k.Table == relation.Parent && k.Column == c).Select(k => k.Literal).Distinct().ToList())
                    .ToList();
                if (perColumn.All(values => values.Count > 0))
                    for (var i = 0; i < perColumn.Count; i++)
                        foreach (var literal in perColumn[i])
                            statements.Add("UPDATE [" + relation.Child + "] SET " + string.Join(", ",
                                relation.ChildColumns.Select((c, at) => "[" + c + "] = " + (at == i ? literal : perColumn[at][0]))));
            }
            foreach (var relation in relations)
                statements.Add("DELETE FROM [" + relation.Parent + "]");

            using (var access = new OleDbConnection(AceVerification.ConnectionString(accessPath)))
            using (var engine = new OleDbConnection(AceVerification.ConnectionString(enginePath)))
            {
                access.Open();
                engine.Open();
                foreach (var statement in statements.Distinct())
                {
                    var inAccess = Refusal(access, statement);
                    var inEngine = Refusal(engine, statement);
                    Assert.True((inAccess == null) == (inEngine == null),
                        "after " + after + ", ACE's " + statement + ": in its own copy " + (inAccess ?? "made") + ", in the engine's " + (inEngine ?? "made"));
                }
            }
            Assert.Equal(Describe(ReadWithAccess(accessPath)), Describe(ReadWithAccess(enginePath)));
        }

        private static string Refusal(OleDbConnection connection, string sql)
        {
            try
            {
                using (var command = new OleDbCommand(sql, connection))
                    command.ExecuteNonQuery();
                return null;
            }
            catch (OleDbException refused)
            {
                return "refused (" + refused.Message + ")";
            }
        }

        private static Action<Database> Update(string table, object key, string column, object value)
        {
            return db => db.GetTable(table).UpdateByPrimaryKey(key, new Row { [column] = value });
        }

        private static Action<Database> Update(string table, object key, Row values)
        {
            return db => db.GetTable(table).UpdateByPrimaryKey(key, values);
        }

        private static Action<Database> Delete(string table, string column, object value)
        {
            return db => db.GetTable(table).DeleteRow(column, value);
        }

        private static Action<Database> Insert(string table, Row row)
        {
            return db => db.GetTable(table).Insert(row);
        }

        private static string[] Seq(params string[] items) { return items; }

        // ── One parent, one child ────────────────────────────────────────────

        /// <summary>P(K, T) with 1, 2, 3 and C(Id, K, T) referring to it: two children of 1, one of 2, one of nothing.</summary>
        private static string[] ParentAndChild(string options, string childKey = "K LONG")
        {
            return Seq(
                "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, " + childKey + ", T TEXT(10), "
                    + "CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) " + options + ")",
                "INSERT INTO P VALUES (1, 'p1')",
                "INSERT INTO P VALUES (2, 'p2')",
                "INSERT INTO P VALUES (3, 'p3')",
                "INSERT INTO C VALUES (10, 1, 'c10')",
                "INSERT INTO C VALUES (11, 1, 'c11')",
                "INSERT INTO C VALUES (20, 2, 'c20')");
        }

        [Fact]
        public void a_parent_key_changed_with_cascade_update_takes_its_children_with_it()
        {
            var both = Referee(ParentAndChild("ON UPDATE CASCADE"), "UPDATE P SET K = 7 WHERE K = 1", Update("P", 1, "K", 7));

            Assert.False(both.Refused);
            Assert.Equal(Seq("K=2, T=p2", "K=3, T=p3", "K=7, T=p1"), both.Rows("P"));
            Assert.Equal(Seq("Id=10, K=7, T=c10", "Id=11, K=7, T=c11", "Id=20, K=2, T=c20"), both.Rows("C"));
        }

        /// <summary>
        /// The case that showed it: the engine changed P's 1 to 7 and moved the children with it, their rows right and
        /// the foreign key's index not - so ACE found no child of 7, failed looking for one of 1, and let 7 be deleted
        /// from under its children.
        /// </summary>
        [Fact]
        public void after_a_cascade_access_finds_the_children_through_the_index_and_still_protects_their_parent()
        {
            var path = Build(ParentAndChild("ON UPDATE CASCADE"));
            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                db.GetTable("P").UpdateByPrimaryKey(1, new Row { ["K"] = 7 });
            }

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Equal(Seq("Id=10", "Id=11"), Query(connection, "SELECT Id FROM C WHERE K = 7"));
                Assert.Empty(Query(connection, "SELECT Id FROM C WHERE K = 1"));
                Assert.Equal(Seq("Id=20"), Query(connection, "SELECT Id FROM C WHERE K = 2"));

                var refused = Refusal(connection, "DELETE FROM P WHERE K = 7");
                Assert.True(refused != null && refused.Contains("related records"), "DELETE FROM P WHERE K = 7: " + (refused ?? "made"));
                Assert.NotNull(Refusal(connection, "INSERT INTO C VALUES (40, 1, 'c40')"));   // there is no 1 any more
                Assert.Null(Refusal(connection, "INSERT INTO C VALUES (41, 7, 'c41')"));
                Assert.Equal(3, Query(connection, "SELECT Id FROM C WHERE K = 7").Count);
            }
        }

        [Fact]
        public void after_a_cascade_delete_access_refuses_a_child_for_the_parent_that_went()
        {
            var path = Build(ParentAndChild("ON DELETE CASCADE"));
            using (var db = Database.Open(path))
            {
                db.EnforceForeignKeys = true;
                db.GetTable("P").DeleteRow("K", 1);
            }

            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                Assert.Empty(Query(connection, "SELECT Id FROM C WHERE K = 1"));
                Assert.NotNull(Refusal(connection, "INSERT INTO C VALUES (40, 1, 'c40')"));
                // 2 still has a child, and nothing cascades an update: ACE refuses to re-key it.
                var refused = Refusal(connection, "UPDATE P SET K = 8 WHERE K = 2");
                Assert.True(refused != null && refused.Contains("related records"), "UPDATE P SET K = 8 WHERE K = 2: " + (refused ?? "made"));
            }
        }

        [Fact]
        public void a_parent_key_changed_without_cascade_update_is_refused_while_it_has_children()
        {
            Assert.True(Referee(ParentAndChild(""), "UPDATE P SET K = 7 WHERE K = 1", Update("P", 1, "K", 7)).Refused);
            Assert.True(Referee(ParentAndChild("ON DELETE CASCADE"), "UPDATE P SET K = 7 WHERE K = 1", Update("P", 1, "K", 7)).Refused);

            var childless = Referee(ParentAndChild(""), "UPDATE P SET K = 9 WHERE K = 3", Update("P", 3, "K", 9));
            Assert.False(childless.Refused);
            Assert.Contains("K=9, T=p3", childless.Rows("P"));

            var otherColumn = Referee(ParentAndChild(""), "UPDATE P SET T = 'new' WHERE K = 1", Update("P", 1, "T", "new"));
            Assert.False(otherColumn.Refused);
            Assert.Contains("K=1, T=new", otherColumn.Rows("P"));
        }

        [Fact]
        public void a_parent_deleted_with_cascade_delete_takes_its_children_with_it()
        {
            var both = Referee(ParentAndChild("ON DELETE CASCADE"), "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));

            Assert.False(both.Refused);
            Assert.Equal(Seq("K=2, T=p2", "K=3, T=p3"), both.Rows("P"));
            Assert.Equal(Seq("Id=20, K=2, T=c20"), both.Rows("C"));
        }

        [Fact]
        public void a_parent_deleted_with_cascade_to_null_leaves_its_children_referring_to_nothing()
        {
            var both = Referee(ParentAndChild("ON DELETE SET NULL"), "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));

            Assert.False(both.Refused);
            Assert.Equal(Seq("K=2, T=p2", "K=3, T=p3"), both.Rows("P"));
            Assert.Equal(Seq("Id=10, K=null, T=c10", "Id=11, K=null, T=c11", "Id=20, K=2, T=c20"), both.Rows("C"));
        }

        [Fact]
        public void a_cascade_to_null_is_refused_when_the_column_is_required()
        {
            var both = Referee(ParentAndChild("ON DELETE SET NULL", childKey: "K LONG NOT NULL"),
                "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));

            Assert.True(both.Refused);
            Assert.Equal(3, both.Rows("P").Count);
        }

        [Fact]
        public void a_parent_deleted_with_no_cascade_is_refused_while_it_has_children()
        {
            Assert.True(Referee(ParentAndChild(""), "DELETE FROM P WHERE K = 1", Delete("P", "K", 1)).Refused);
            Assert.True(Referee(ParentAndChild("ON UPDATE CASCADE"), "DELETE FROM P WHERE K = 1", Delete("P", "K", 1)).Refused);

            var childless = Referee(ParentAndChild(""), "DELETE FROM P WHERE K = 3", Delete("P", "K", 3));
            Assert.False(childless.Refused);
            Assert.Equal(Seq("K=1, T=p1", "K=2, T=p2"), childless.Rows("P"));
        }

        [Fact]
        public void a_child_can_only_be_given_a_parent_that_is_there()
        {
            var setup = ParentAndChild("");

            Assert.True(Referee(setup, "UPDATE C SET K = 99 WHERE Id = 10", Update("C", 10, "K", 99)).Refused);
            Assert.True(Referee(setup, "INSERT INTO C VALUES (40, 99, 'c40')", Insert("C", new Row { ["Id"] = 40, ["K"] = 99, ["T"] = "c40" })).Refused);

            var moved = Referee(setup, "UPDATE C SET K = 3 WHERE Id = 10", Update("C", 10, "K", 3));
            Assert.False(moved.Refused);
            Assert.Contains("Id=10, K=3, T=c10", moved.Rows("C"));

            var freed = Referee(setup, "UPDATE C SET K = NULL WHERE Id = 10", Update("C", 10, "K", null));
            Assert.False(freed.Refused);
            Assert.Contains("Id=10, K=null, T=c10", freed.Rows("C"));

            var added = Referee(setup, "INSERT INTO C VALUES (40, 3, 'c40')", Insert("C", new Row { ["Id"] = 40, ["K"] = 3, ["T"] = "c40" }));
            Assert.False(added.Refused);
            Assert.Equal(4, added.Rows("C").Count);

            var parentless = Referee(setup, "INSERT INTO C VALUES (41, NULL, 'c41')", Insert("C", new Row { ["Id"] = 41, ["T"] = "c41" }));
            Assert.False(parentless.Refused);
            Assert.Contains("Id=41, K=null, T=c41", parentless.Rows("C"));
        }

        [Fact]
        public void a_change_that_would_give_two_rows_one_key_is_refused()
        {
            Assert.True(Referee(ParentAndChild("ON UPDATE CASCADE"), "UPDATE P SET K = 2 WHERE K = 1", Update("P", 1, "K", 2)).Refused);

            // The child's key is (K, N): its rows move to the parent's new key, which no row of either table has.
            var setup = Seq(
                "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (K LONG, N LONG, T TEXT(10), CONSTRAINT pk PRIMARY KEY (K, N), "
                    + "CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) ON UPDATE CASCADE)",
                "INSERT INTO P VALUES (1, 'p1')",
                "INSERT INTO P VALUES (2, 'p2')",
                "INSERT INTO C VALUES (1, 1, 'c11')",
                "INSERT INTO C VALUES (2, 1, 'c21')");
            Assert.True(Referee(setup, "UPDATE P SET K = 2 WHERE K = 1", Update("P", 1, "K", 2)).Refused);

            var moved = Referee(setup, "UPDATE P SET K = 3 WHERE K = 1", Update("P", 1, "K", 3));
            Assert.False(moved.Refused);
            Assert.Equal(Seq("K=2, N=1, T=c21", "K=3, N=1, T=c11"), moved.Rows("C"));
        }

        [Fact]
        public void no_part_of_a_primary_key_can_be_made_null()
        {
            // With cascade update the null would go on to the children; without it they forbid the change anyway.
            Assert.True(Referee(ParentAndChild("ON UPDATE CASCADE"), "UPDATE P SET K = NULL WHERE K = 1", Update("P", 1, "K", null)).Refused);
            Assert.True(Referee(ParentAndChild(""), "UPDATE P SET K = NULL WHERE K = 3", Update("P", 3, "K", null)).Refused);
            Assert.True(Referee(ParentAndChild(""), "UPDATE C SET Id = NULL WHERE Id = 10", Update("C", 10, "Id", null)).Refused);
        }

        [Fact]
        public void a_cascade_leaves_the_rest_of_the_row_as_it_was()
        {
            // The children are rewritten whole: what they hold beside the key - a Memo, a date with an index of its
            // own, a Yes/No - comes through, and the date's index with it. The Memo is a long one, on pages of its
            // own: ACE could not read one after the engine rewrote its row in a table ACE made.
            var setup = Seq(
                "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, M MEMO, W DATETIME, Y YESNO, "
                    + "CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) ON UPDATE CASCADE ON DELETE SET NULL)",
                "CREATE INDEX byW ON C (W)",
                "INSERT INTO P VALUES (1, 'p1')",
                "INSERT INTO P VALUES (2, 'p2')",
                "INSERT INTO C VALUES (10, 1, '" + new string('m', 3000) + "', #2026-09-14 10:30:00#, True)",
                "INSERT INTO C VALUES (20, 2, NULL, NULL, False)");

            var updated = Referee(setup, "UPDATE P SET K = 7 WHERE K = 1", Update("P", 1, "K", 7));
            Assert.False(updated.Refused);
            Assert.Contains(updated.Rows("C"), row => row.StartsWith("Id=10, K=7, M=mmm") && row.EndsWith("Y=True"));

            var nulled = Referee(setup, "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));
            Assert.False(nulled.Refused);
            Assert.Contains(nulled.Rows("C"), row => row.StartsWith("Id=10, K=null, M=mmm") && row.EndsWith("Y=True"));
        }

        /// <summary>
        /// A cascade never writes an AutoNumber, so there is no counter for one to move. ACE will not make a
        /// relationship that cascades updates into an AutoNumber foreign key at all, and one that cascades nulls into
        /// it is refused when a row would be reached ("Field cannot be updated"). The engine refuses the same.
        /// </summary>
        [Fact]
        public void a_cascade_does_not_write_an_autonumber()
        {
            var parent = "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))";
            var path = Build(Seq(parent));
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                var refused = Refusal(connection, "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K COUNTER, T TEXT(10), "
                    + "CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) ON UPDATE CASCADE)");
                Assert.True(refused != null && refused.Contains("Invalid field definition"), "ACE made a relationship that cascades updates into an AutoNumber: " + refused);
            }

            var setup = Seq(
                parent,
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K COUNTER, T TEXT(10), CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) ON DELETE SET NULL)",
                "INSERT INTO P VALUES (1, 'p1')",
                "INSERT INTO P VALUES (2, 'p2')",
                "INSERT INTO P VALUES (3, 'p3')",
                "INSERT INTO C (Id, T) VALUES (10, 'numbered 1')");

            var reached = Referee(setup, "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));
            Assert.True(reached.Refused);
            Assert.Contains("Field cannot be updated", reached.Message);
            Assert.Equal(Seq("Id=10, K=1, T=numbered 1"), reached.Rows("C"));

            // A parent no row refers to goes.
            var unreached = Referee(setup, "DELETE FROM P WHERE K = 3", Delete("P", "K", 3));
            Assert.False(unreached.Refused);
            Assert.Equal(Seq("K=1, T=p1", "K=2, T=p2"), unreached.Rows("P"));
        }

        // ── Text keys ────────────────────────────────────────────────────────

        private static string[] TextParentAndChild(string options)
        {
            return Seq(
                "CREATE TABLE P (K TEXT(10) CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K TEXT(10), T TEXT(10), CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) " + options + ")",
                "INSERT INTO P VALUES ('abc', 'p')",
                "INSERT INTO P VALUES ('lone', 'q')",
                "INSERT INTO C VALUES (1, 'ABC', 'upper')",
                "INSERT INTO C VALUES (2, 'abc', 'lower')");
        }

        [Fact]
        public void a_text_key_matches_without_regard_to_case_and_a_change_of_case_is_a_change()
        {
            var cascade = TextParentAndChild("ON UPDATE CASCADE ON DELETE CASCADE");

            var recased = Referee(cascade, "UPDATE P SET K = 'Abc' WHERE K = 'abc'", Update("P", "abc", "K", "Abc"));
            Assert.False(recased.Refused);
            Assert.Equal(Seq("Id=1, K=Abc, T=upper", "Id=2, K=Abc, T=lower"), recased.Rows("C"));

            var renamed = Referee(cascade, "UPDATE P SET K = 'xyz' WHERE K = 'abc'", Update("P", "abc", "K", "xyz"));
            Assert.Equal(Seq("Id=1, K=xyz, T=upper", "Id=2, K=xyz, T=lower"), renamed.Rows("C"));

            var deleted = Referee(cascade, "DELETE FROM P WHERE T = 'p'", Delete("P", "T", "p"));
            Assert.Empty(deleted.Rows("C"));

            var child = Referee(cascade, "UPDATE C SET K = 'LONE' WHERE Id = 1", Update("C", 1, "K", "LONE"));
            Assert.False(child.Refused);
            Assert.Contains("Id=1, K=LONE, T=upper", child.Rows("C"));

            var restrict = TextParentAndChild("");
            Assert.True(Referee(restrict, "UPDATE P SET K = 'Abc' WHERE K = 'abc'", Update("P", "abc", "K", "Abc")).Refused);
            Assert.True(Referee(restrict, "DELETE FROM P WHERE T = 'p'", Delete("P", "T", "p")).Refused);
        }

        // ── Text with accents, in either sort order (#23) ──

        private static string[] AccentedParentAndChild(string options)
        {
            return Seq(
                "CREATE TABLE P (K TEXT(255) CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K TEXT(255), T TEXT(10), CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) " + options + ")",
                "INSERT INTO P VALUES ('résumé', 'p')",
                "INSERT INTO P VALUES ('Æther', 'q')",
                "INSERT INTO P VALUES ('Zürich', 'r')",
                "INSERT INTO P VALUES ('日本', 's')",
                "INSERT INTO C VALUES (1, 'RÉSUMÉ', 'upper')",
                "INSERT INTO C VALUES (2, 'résumé', 'lower')",
                "INSERT INTO C VALUES (3, 'æther', 'lower')",
                "INSERT INTO C VALUES (4, 'ZÜRICH', 'upper')",
                "INSERT INTO C VALUES (5, '日本', 'same')");
        }

        // A parent and child keyed on text, in a database that sorts in General and in one that sorts in General
        // - Legacy: the keys the engine compares and writes are those of the database's order. The children name
        // their parents in other case; cascades reach them, and what does not match an accent is refused, in the
        // engine as in ACE - which then answers through the indexes of the engine's file as it does of its own,
        // compacts it, and goes on the same in both.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void text_keys_with_accents_and_mixed_case_cascade_alike_in_either_sort_order(bool general)
        {
            _inGeneral = general;
            var cascade = AccentedParentAndChild("ON UPDATE CASCADE ON DELETE CASCADE");

            var renamed = Referee(cascade, "UPDATE P SET K = 'Curriculum Vitæ' WHERE K = 'résumé'", Update("P", "résumé", "K", "Curriculum Vitæ"));
            Assert.False(renamed.Refused);
            Assert.Contains("Id=1, K=Curriculum Vitæ, T=upper", renamed.Rows("C"));
            Assert.Contains("Id=2, K=Curriculum Vitæ, T=lower", renamed.Rows("C"));

            var recased = Referee(cascade, "UPDATE P SET K = 'ZÜRICH' WHERE K = 'Zürich'", Update("P", "Zürich", "K", "ZÜRICH"));
            Assert.False(recased.Refused);

            var deleted = Referee(cascade, "DELETE FROM P WHERE K = 'Æther'", Delete("P", "K", "Æther"));
            Assert.Equal(4, deleted.Rows("C").Count);
            Assert.DoesNotContain(deleted.Rows("C"), row => row.Contains("Id=3"));

            // A child's new parent: in any case, and by text Access keys alike (Æ as AE), but not without the accent.
            Assert.False(Referee(cascade, "INSERT INTO C VALUES (9, 'ÆTHER', 'new')", Insert("C", new Row { ["Id"] = 9, ["K"] = "ÆTHER", ["T"] = "new" })).Refused);
            Assert.False(Referee(cascade, "UPDATE C SET K = 'Aether' WHERE Id = 5", Update("C", 5, "K", "Aether")).Refused);
            Assert.True(Referee(cascade, "UPDATE C SET K = 'resume' WHERE Id = 2", Update("C", 2, "K", "resume")).Refused);
            Assert.True(Referee(cascade, "INSERT INTO C VALUES (9, 'Zurich', 'new')", Insert("C", new Row { ["Id"] = 9, ["K"] = "Zurich", ["T"] = "new" })).Refused);

            var restrict = AccentedParentAndChild("");
            Assert.True(Referee(restrict, "DELETE FROM P WHERE K = 'résumé'", Delete("P", "K", "résumé")).Refused);
            Assert.True(Referee(restrict, "UPDATE P SET K = 'Résumé' WHERE K = 'résumé'", Update("P", "résumé", "K", "Résumé")).Refused);
        }

        // A cascade to a key of 255 accented letters, whose index entry is cut at 510 bytes and ended in a digest:
        // ACE makes it and so does the engine, the children's entries cut alike. ACE does not answer a WHERE
        // through a cut key as its own scan does, so its answers through the indexes are compared between its own
        // file and the engine's, not with a scan; then ACE cascades a second change through the cut entries of
        // both, and deletes through them, and the two files still read alike.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void a_cascade_to_a_key_longer_than_an_index_entry_holds_is_made_as_ace_makes_it(bool general)
        {
            _inGeneral = general;
            var longKey = new string('Æ', 100) + new string('é', 155);
            var setup = AccentedParentAndChild("ON UPDATE CASCADE ON DELETE CASCADE").ToList();

            var accessPath = Build(setup);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(accessPath)))
            using (var command = new OleDbCommand("UPDATE P SET K = '" + longKey + "' WHERE K = 'résumé'", connection))
            {
                connection.Open();
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var enginePath = Build(setup);
            using (var db = Database.Open(enginePath))
            {
                db.EnforceForeignKeys = true;
                db.GetTable("P").UpdateByPrimaryKey("résumé", new Row { ["K"] = longKey });
                // The long parent is found by reading the table: a child may name it, in any case.
                db.GetTable("C").Insert(new Row { ["Id"] = 9, ["K"] = longKey.ToLowerInvariant(), ["T"] = "new" });
            }
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(accessPath)))
            using (var command = new OleDbCommand("INSERT INTO C VALUES (9, '" + longKey.ToLowerInvariant() + "', 'new')", connection))
            {
                connection.Open();
                command.ExecuteNonQuery();
            }

            var access = ReadWithAccess(accessPath);
            Assert.Contains("Id=1, K=" + longKey + ", T=upper", access["C"]);
            Assert.Contains("Id=2, K=" + longKey + ", T=lower", access["C"]);
            Assert.Equal(Describe(access), Describe(ReadWithAccess(enginePath)));
            Assert.Equal(Describe(access), Describe(ReadWithEngine(enginePath)));

            Func<string, List<string>> throughIndexes = path =>
            {
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
                {
                    connection.Open();
                    return Seq("P", "C").SelectMany(table => Seq(longKey, longKey.ToLowerInvariant(), "Æther", "æther", "nobody").Select(key =>
                        table + " " + (key.Length > 20 ? "long" : key) + ": " + string.Join(" | ", Query(connection, "SELECT COUNT(*) FROM [" + table + "] WHERE [K] = '" + key + "'")))).ToList();
                }
            };
            Assert.Equal(throughIndexes(accessPath), throughIndexes(enginePath));
            var compacted = Compact(enginePath);
            Assert.Equal(Describe(access), Describe(ReadWithAccess(compacted)));
            Assert.Equal(throughIndexes(accessPath), throughIndexes(compacted));

            // ACE goes on, through the cut entries: a second cascade, a refusal, and a cascade delete.
            foreach (var path in Seq(accessPath, enginePath))
                using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
                {
                    connection.Open();
                    Assert.NotNull(Refusal(connection, "INSERT INTO C VALUES (10, 'no such parent', 'x')"));
                    Assert.Null(Refusal(connection, "UPDATE P SET K = 'short again' WHERE T = 'p'"));
                    Assert.Null(Refusal(connection, "DELETE FROM P WHERE K = 'Æther'"));
                }
            var further = ReadWithAccess(accessPath);
            Assert.Contains("Id=9, K=short again, T=new", further["C"]);
            Assert.DoesNotContain(further["C"], row => row.Contains("Id=3"));
            Assert.Equal(Describe(further), Describe(ReadWithAccess(enginePath)));
            Assert.Equal(Describe(further), Describe(ReadWithEngine(enginePath)));
        }
        // A cascade to a key with a character Access keys by the character before it (ー): ACE makes the change and
        // carries it to the children. The engine makes no key for such a value, so it refuses the change - as not
        // supported, at the parent, before anything is written - and its file is byte for byte as it was.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void a_cascade_to_a_key_the_engine_cannot_make_is_made_by_ace_and_refused_whole_by_the_engine(bool general)
        {
            _inGeneral = general;
            var setup = AccentedParentAndChild("ON UPDATE CASCADE ON DELETE CASCADE").ToList();

            var accessPath = Build(setup);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(accessPath)))
            using (var command = new OleDbCommand("UPDATE P SET K = 'データ' WHERE K = 'résumé'", connection))
            {
                connection.Open();
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var access = ReadWithAccess(accessPath);
            Assert.Contains("Id=1, K=データ, T=upper", access["C"]);
            Assert.Contains("Id=2, K=データ, T=lower", access["C"]);

            var enginePath = Build(setup);
            var before = File.ReadAllBytes(enginePath);
            using (var db = Database.Open(enginePath))
            {
                db.EnforceForeignKeys = true;
                Assert.Throws<NotSupportedException>(() => db.GetTable("P").UpdateByPrimaryKey("résumé", new Row { ["K"] = "データ" }));
                Assert.Throws<NotSupportedException>(() => db.GetTable("C").UpdateByPrimaryKey(5, new Row { ["K"] = "ー" }));
            }
            Assert.Equal(before, File.ReadAllBytes(enginePath));

            // In ACE's file, where the rows now have such keys, the engine reads them, finds the parent of a child
            // by reading the table, and will not delete or rewrite a row whose entries it could not take out.
            using (var db = Database.Open(accessPath))
            {
                db.EnforceForeignKeys = true;
                var child = db.GetTable("C");
                child.Insert(new Row { ["Id"] = 9, ["K"] = "Æther", ["T"] = "new" });
                Assert.Throws<NotSupportedException>(() => db.GetTable("P").DeleteRow("K", "データ"));
                Assert.Throws<NotSupportedException>(() => child.DeleteRow("Id", 1));
            }
            var after = ReadWithAccess(accessPath);
            Assert.Equal(access["P"], after["P"]);
            Assert.Equal(access["C"].Count + 1, after["C"].Count);
        }

        // ── Two columns ──────────────────────────────────────────────────────

        /// <summary>
        /// Region(Id, Country, Code, T), unique on (Country, Code), and City(Id, Country, Code, T) referring to that
        /// pair. Region's primary key is Id: the engine's update finds a row by a one-column key.
        /// </summary>
        private static string[] RegionsAndCities(string options)
        {
            return Seq(
                "CREATE TABLE Region (Id LONG CONSTRAINT pk PRIMARY KEY, Country TEXT(2), Code LONG, T TEXT(12), CONSTRAINT uq UNIQUE (Country, Code))",
                "CREATE TABLE City (Id LONG CONSTRAINT pk PRIMARY KEY, Country TEXT(2), Code LONG, T TEXT(12), "
                    + "CONSTRAINT fk FOREIGN KEY (Country, Code) REFERENCES Region (Country, Code) " + options + ")",
                "INSERT INTO Region VALUES (1, 'NZ', 1, 'Otago')",
                "INSERT INTO Region VALUES (2, 'NZ', 2, 'Canterbury')",
                "INSERT INTO Region VALUES (3, 'AU', 1, 'Victoria')",
                "INSERT INTO City VALUES (1, 'NZ', 1, 'Dunedin')",
                "INSERT INTO City VALUES (2, 'NZ', 2, 'Timaru')",
                "INSERT INTO City VALUES (3, 'AU', 1, 'Geelong')",
                "INSERT INTO City VALUES (4, NULL, NULL, 'Atlantis')");
        }

        [Fact]
        public void a_relationship_of_two_columns_cascades_to_the_rows_that_match_both()
        {
            var updated = Referee(RegionsAndCities("ON UPDATE CASCADE"), "UPDATE Region SET Code = 5 WHERE Id = 1", Update("Region", 1, "Code", 5));
            Assert.False(updated.Refused);
            Assert.Equal(Seq("Id=1, Country=NZ, Code=5, T=Dunedin", "Id=2, Country=NZ, Code=2, T=Timaru",
                             "Id=3, Country=AU, Code=1, T=Geelong", "Id=4, Country=null, Code=null, T=Atlantis"), updated.Rows("City"));

            var deleted = Referee(RegionsAndCities("ON DELETE CASCADE"), "DELETE FROM Region WHERE Id = 1", Delete("Region", "Id", 1));
            Assert.False(deleted.Refused);
            Assert.Equal(Seq("Id=2, Country=NZ, Code=2, T=Timaru", "Id=3, Country=AU, Code=1, T=Geelong",
                             "Id=4, Country=null, Code=null, T=Atlantis"), deleted.Rows("City"));

            var nulled = Referee(RegionsAndCities("ON DELETE SET NULL"), "DELETE FROM Region WHERE Id = 3", Delete("Region", "Id", 3));
            Assert.False(nulled.Refused);
            Assert.Contains("Id=3, Country=null, Code=null, T=Geelong", nulled.Rows("City"));
        }

        [Fact]
        public void a_relationship_of_two_columns_refuses_what_matches_no_parent_in_both()
        {
            var setup = RegionsAndCities("");

            Assert.True(Referee(setup, "UPDATE Region SET Country = 'FJ' WHERE Id = 1", Update("Region", 1, "Country", "FJ")).Refused);
            Assert.True(Referee(setup, "DELETE FROM Region WHERE Id = 1", Delete("Region", "Id", 1)).Refused);
            // AU is a country and 2 is a code, but no region is (AU, 2).
            Assert.True(Referee(setup, "INSERT INTO City VALUES (9, 'AU', 2, 'x')",
                Insert("City", new Row { ["Id"] = 9, ["Country"] = "AU", ["Code"] = 2, ["T"] = "x" })).Refused);
            Assert.True(Referee(setup, "UPDATE City SET Country = 'AU', Code = 2 WHERE Id = 1",
                Update("City", 1, new Row { ["Country"] = "AU", ["Code"] = 2 })).Refused);
            // A foreign key that is null in part refers to no row.
            Assert.True(Referee(setup, "UPDATE City SET Code = NULL WHERE Id = 1", Update("City", 1, "Code", null)).Refused);

            var freed = Referee(setup, "UPDATE City SET Country = NULL, Code = NULL WHERE Id = 1",
                Update("City", 1, new Row { ["Country"] = null, ["Code"] = null }));
            Assert.False(freed.Refused);
            Assert.Contains("Id=1, Country=null, Code=null, T=Dunedin", freed.Rows("City"));
        }

        // ── Chains ───────────────────────────────────────────────────────────

        /// <summary>P(K) ← C(K), whose key is its foreign key ← G(Id, K): a change to P's key goes down two levels.</summary>
        private static string[] Chain(string parentChild, string childGrandchild)
        {
            return Seq(
                "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10), CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) " + parentChild + ")",
                "CREATE TABLE G (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, T TEXT(10), CONSTRAINT fkg FOREIGN KEY (K) REFERENCES C (K) " + childGrandchild + ")",
                "INSERT INTO P VALUES (1, 'p1')",
                "INSERT INTO P VALUES (2, 'p2')",
                "INSERT INTO C VALUES (1, 'c1')",
                "INSERT INTO C VALUES (2, 'c2')",
                "INSERT INTO G VALUES (1, 1, 'g1')",
                "INSERT INTO G VALUES (2, 1, 'g2')",
                "INSERT INTO G VALUES (3, 2, 'g3')");
        }

        [Fact]
        public void a_cascade_goes_on_down_to_the_grandchildren()
        {
            const string All = "ON UPDATE CASCADE ON DELETE CASCADE";

            var updated = Referee(Chain(All, All), "UPDATE P SET K = 5 WHERE K = 1", Update("P", 1, "K", 5));
            Assert.False(updated.Refused);
            Assert.Equal(Seq("K=2, T=c2", "K=5, T=c1"), updated.Rows("C"));
            Assert.Equal(Seq("Id=1, K=5, T=g1", "Id=2, K=5, T=g2", "Id=3, K=2, T=g3"), updated.Rows("G"));

            var deleted = Referee(Chain(All, All), "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));
            Assert.False(deleted.Refused);
            Assert.Equal(Seq("K=2, T=c2"), deleted.Rows("C"));
            Assert.Equal(Seq("Id=3, K=2, T=g3"), deleted.Rows("G"));

            var nulled = Referee(Chain(All, "ON DELETE SET NULL"), "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));
            Assert.False(nulled.Refused);
            Assert.Equal(Seq("Id=1, K=null, T=g1", "Id=2, K=null, T=g2", "Id=3, K=2, T=g3"), nulled.Rows("G"));
        }

        [Fact]
        public void a_cascade_is_refused_whole_when_a_grandchild_forbids_it()
        {
            const string All = "ON UPDATE CASCADE ON DELETE CASCADE";

            var updated = Referee(Chain(All, ""), "UPDATE P SET K = 5 WHERE K = 1", Update("P", 1, "K", 5));
            Assert.True(updated.Refused);
            Assert.Equal(Seq("K=1, T=p1", "K=2, T=p2"), updated.Rows("P"));

            var deleted = Referee(Chain(All, ""), "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));
            Assert.True(deleted.Refused);
            Assert.Equal(Seq("K=1, T=c1", "K=2, T=c2"), deleted.Rows("C"));

            // A cascade that would give the child a key another child has.
            var setup = Chain(All, All).Concat(Seq("UPDATE P SET K = 6 WHERE K = 2"));
            Assert.True(Referee(setup, "UPDATE P SET K = 1 WHERE K = 6", Update("P", 6, "K", 1)).Refused);
        }

        [Fact]
        public void a_cascade_is_refused_when_it_leaves_the_row_without_a_parent_in_another_relationship()
        {
            var setup = Seq(
                "CREATE TABLE P1 (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE P2 (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, T TEXT(10), "
                    + "CONSTRAINT fk1 FOREIGN KEY (K) REFERENCES P1 (K) ON UPDATE CASCADE ON DELETE SET NULL, "
                    + "CONSTRAINT fk2 FOREIGN KEY (K) REFERENCES P2 (K))",
                "INSERT INTO P1 VALUES (1, 'p1')",
                "INSERT INTO P2 VALUES (1, 'p2')",
                "INSERT INTO P2 VALUES (2, 'p2b')",
                "INSERT INTO C VALUES (1, 1, 'c')");

            Assert.True(Referee(setup, "UPDATE P1 SET K = 3 WHERE K = 1", Update("P1", 1, "K", 3)).Refused);   // there is no P2 3

            var moved = Referee(setup, "UPDATE P1 SET K = 2 WHERE K = 1", Update("P1", 1, "K", 2));             // there is a P2 2
            Assert.False(moved.Refused);
            Assert.Equal(Seq("Id=1, K=2, T=c"), moved.Rows("C"));

            var nulled = Referee(setup, "DELETE FROM P1 WHERE K = 1", Delete("P1", "K", 1));
            Assert.False(nulled.Refused);
            Assert.Equal(Seq("Id=1, K=null, T=c"), nulled.Rows("C"));
        }

        [Fact]
        public void one_parent_with_a_cascading_child_and_a_forbidding_child_is_refused_whole()
        {
            var setup = Seq(
                "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C1 (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, CONSTRAINT f1 FOREIGN KEY (K) REFERENCES P (K) ON DELETE CASCADE ON UPDATE CASCADE)",
                "CREATE TABLE C2 (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, CONSTRAINT f2 FOREIGN KEY (K) REFERENCES P (K))",
                "INSERT INTO P VALUES (1, 'p')",
                "INSERT INTO C1 VALUES (1, 1)",
                "INSERT INTO C2 VALUES (1, 1)");

            var deleted = Referee(setup, "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));
            Assert.True(deleted.Refused);
            Assert.Equal(Seq("Id=1, K=1"), deleted.Rows("C1"));
            Assert.True(Referee(setup, "UPDATE P SET K = 2 WHERE K = 1", Update("P", 1, "K", 2)).Refused);
        }

        /// <summary>
        /// X ← Y and X ← Z cascade deletes, and Z ← Y forbids. Deleting the X row deletes its Y and Z rows; whether the
        /// Y row is still there to forbid the Z row's deletion depends on which relationship is taken first, and ACE
        /// takes them in the order they were made.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void relationships_are_taken_in_the_order_they_were_made(bool yFirst)
        {
            const string Y = "CREATE TABLE Y (Id LONG CONSTRAINT pk PRIMARY KEY, X LONG, Z LONG, CONSTRAINT fyx FOREIGN KEY (X) REFERENCES X (Id) ON DELETE CASCADE)";
            const string Z = "CREATE TABLE Z (Id LONG CONSTRAINT pk PRIMARY KEY, X LONG, CONSTRAINT fzx FOREIGN KEY (X) REFERENCES X (Id) ON DELETE CASCADE)";
            var setup = Seq("CREATE TABLE X (Id LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))")
                .Concat(yFirst ? Seq(Y, Z) : Seq(Z, Y))
                .Concat(Seq(
                    "ALTER TABLE Y ADD CONSTRAINT fyz FOREIGN KEY (Z) REFERENCES Z (Id)",
                    "INSERT INTO X VALUES (1, 'x')",
                    "INSERT INTO Z VALUES (1, 1)",
                    "INSERT INTO Y VALUES (1, 1, 1)"));

            var both = Referee(setup, "DELETE FROM X WHERE Id = 1", Delete("X", "Id", 1));

            Assert.Equal(!yFirst, both.Refused);
            Assert.Equal(yFirst ? 0 : 1, both.Rows("Y").Count);
        }

        [Fact]
        public void a_cycle_of_cascade_deletes_ends()
        {
            var setup = Seq(
                "CREATE TABLE A (Id LONG CONSTRAINT pk PRIMARY KEY, B LONG, T TEXT(10))",
                "CREATE TABLE B (Id LONG CONSTRAINT pk PRIMARY KEY, A LONG, T TEXT(10), CONSTRAINT fkb FOREIGN KEY (A) REFERENCES A (Id) ON DELETE CASCADE)",
                "INSERT INTO A VALUES (1, NULL, 'a1')",
                "INSERT INTO B VALUES (1, 1, 'b1')",
                "ALTER TABLE A ADD CONSTRAINT fka FOREIGN KEY (B) REFERENCES B (Id) ON DELETE CASCADE",
                "UPDATE A SET B = 1 WHERE Id = 1",
                "INSERT INTO A VALUES (2, 1, 'a2')",
                "INSERT INTO A VALUES (3, NULL, 'a3')");

            var one = Referee(setup, "DELETE FROM A WHERE Id = 2", Delete("A", "Id", 2));
            Assert.Equal(Seq("Id=1, B=1, T=a1", "Id=3, B=null, T=a3"), one.Rows("A"));

            var all = Referee(setup, "DELETE FROM A WHERE Id = 1", Delete("A", "Id", 1));
            Assert.False(all.Refused);
            Assert.Equal(Seq("Id=3, B=null, T=a3"), all.Rows("A"));
            Assert.Empty(all.Rows("B"));
        }

        // ── A table that refers to itself ────────────────────────────────────

        /// <summary>E(Id, Boss, T), Boss referring to E.Id: 1 is her own boss, 2 and 3 report to 1, 4 to 2, 5 to no one.</summary>
        private static string[] Staff(string options)
        {
            return Seq(
                "CREATE TABLE E (Id LONG CONSTRAINT pk PRIMARY KEY, Boss LONG, T TEXT(10), CONSTRAINT fk FOREIGN KEY (Boss) REFERENCES E (Id) " + options + ")",
                "INSERT INTO E VALUES (1, 1, 'top')",
                "INSERT INTO E VALUES (2, 1, 'two')",
                "INSERT INTO E VALUES (3, 1, 'three')",
                "INSERT INTO E VALUES (4, 2, 'four')",
                "INSERT INTO E VALUES (5, NULL, 'five')");
        }

        [Fact]
        public void a_table_that_refers_to_itself_cascades_through_itself()
        {
            var updated = Referee(Staff("ON UPDATE CASCADE"), "UPDATE E SET Id = 10 WHERE Id = 1", Update("E", 1, "Id", 10));
            Assert.False(updated.Refused);
            Assert.Equal(Seq("Id=10, Boss=10, T=top", "Id=2, Boss=10, T=two", "Id=3, Boss=10, T=three", "Id=4, Boss=2, T=four", "Id=5, Boss=null, T=five"),
                         updated.Rows("E"));

            var deleted = Referee(Staff("ON DELETE CASCADE"), "DELETE FROM E WHERE Id = 1", Delete("E", "Id", 1));
            Assert.False(deleted.Refused);
            Assert.Equal(Seq("Id=5, Boss=null, T=five"), deleted.Rows("E"));

            var nulled = Referee(Staff("ON DELETE SET NULL"), "DELETE FROM E WHERE Id = 1", Delete("E", "Id", 1));
            Assert.False(nulled.Refused);
            Assert.Equal(Seq("Id=2, Boss=null, T=two", "Id=3, Boss=null, T=three", "Id=4, Boss=2, T=four", "Id=5, Boss=null, T=five"), nulled.Rows("E"));
        }

        [Fact]
        public void a_table_that_refers_to_itself_refuses_what_would_leave_a_row_without_its_parent()
        {
            var setup = Staff("");

            Assert.True(Referee(setup, "DELETE FROM E WHERE Id = 1", Delete("E", "Id", 1)).Refused);              // 2 and 3 report to 1
            Assert.True(Referee(setup, "UPDATE E SET Id = 20 WHERE Id = 2", Update("E", 2, "Id", 20)).Refused);   // 4 reports to 2
            Assert.True(Referee(setup, "UPDATE E SET Boss = 99 WHERE Id = 4", Update("E", 4, "Boss", 99)).Refused);
            Assert.True(Referee(setup, "INSERT INTO E VALUES (6, 99, 'six')", Insert("E", new Row { ["Id"] = 6, ["Boss"] = 99, ["T"] = "six" })).Refused);
            Assert.True(Referee(setup, "UPDATE E SET Id = 10 WHERE Id = 1", Update("E", 1, "Id", 10)).Refused);   // she would still report to 1
        }

        [Fact]
        public void a_row_may_refer_to_itself()
        {
            var setup = Staff("");

            var added = Referee(setup, "INSERT INTO E VALUES (6, 6, 'six')", Insert("E", new Row { ["Id"] = 6, ["Boss"] = 6, ["T"] = "six" }));
            Assert.False(added.Refused);
            Assert.Contains("Id=6, Boss=6, T=six", added.Rows("E"));

            var withSix = setup.Concat(Seq("INSERT INTO E VALUES (6, 6, 'six')")).ToList();
            var rekeyed = Referee(withSix, "UPDATE E SET Id = 60, Boss = 60 WHERE Id = 6", Update("E", 6, new Row { ["Id"] = 60, ["Boss"] = 60 }));
            Assert.False(rekeyed.Refused);
            Assert.Contains("Id=60, Boss=60, T=six", rekeyed.Rows("E"));

            var deleted = Referee(withSix, "DELETE FROM E WHERE Id = 6", Delete("E", "Id", 6));                   // only she refers to herself
            Assert.False(deleted.Refused);
            Assert.Equal(5, deleted.Rows("E").Count);

            var ownBoss = Referee(setup, "UPDATE E SET Boss = 4 WHERE Id = 4", Update("E", 4, "Boss", 4));
            Assert.False(ownBoss.Refused);
            Assert.Contains("Id=4, Boss=4, T=four", ownBoss.Rows("E"));
        }

        /// <summary>Part(Id, A, B, ParentA, ParentB), (ParentA, ParentB) referring to Part's own unique (A, B).</summary>
        private static string[] Parts()
        {
            return Seq(
                "CREATE TABLE Part (Id LONG CONSTRAINT pk PRIMARY KEY, A LONG, B LONG, ParentA LONG, ParentB LONG, T TEXT(10), "
                    + "CONSTRAINT uq UNIQUE (A, B), CONSTRAINT fk FOREIGN KEY (ParentA, ParentB) REFERENCES Part (A, B))",
                "INSERT INTO Part VALUES (1, 1, 1, NULL, NULL, 'root')",
                "INSERT INTO Part VALUES (2, 5, NULL, NULL, NULL, 'half')");
        }

        /// <summary>
        /// A foreign key that is null in part is not exempt, as one that is all null is: it must match a parent's key,
        /// and ACE holds null to match null. So it matches a row whose key is null in the same part - the row itself
        /// included - and nothing else.
        /// </summary>
        [Fact]
        public void a_foreign_key_null_in_part_refers_to_a_key_null_in_the_same_part()
        {
            var whole = Referee(Parts(), "INSERT INTO Part VALUES (3, 7, 8, 7, 8, 'self')",
                Insert("Part", new Row { ["Id"] = 3, ["A"] = 7, ["B"] = 8, ["ParentA"] = 7, ["ParentB"] = 8, ["T"] = "self" }));
            Assert.False(whole.Refused);
            Assert.Equal(3, whole.Rows("Part").Count);

            // (7, null) referring to (7, null): itself.
            var itself = Referee(Parts(), "INSERT INTO Part VALUES (3, 7, NULL, 7, NULL, 'self')",
                Insert("Part", new Row { ["Id"] = 3, ["A"] = 7, ["ParentA"] = 7, ["T"] = "self" }));
            Assert.False(itself.Refused);
            Assert.Contains("Id=3, A=7, B=null, ParentA=7, ParentB=null, T=self", itself.Rows("Part"));

            // (5, null) referring to row 2, whose key is (5, null).
            var other = Referee(Parts(), "INSERT INTO Part VALUES (3, 7, 8, 5, NULL, 'child')",
                Insert("Part", new Row { ["Id"] = 3, ["A"] = 7, ["B"] = 8, ["ParentA"] = 5, ["T"] = "child" }));
            Assert.False(other.Refused);

            // (9, null) and (1, null): no row's key is either. Row 1's is (1, 1).
            Assert.True(Referee(Parts(), "INSERT INTO Part VALUES (3, 7, 8, 9, NULL, 'child')",
                Insert("Part", new Row { ["Id"] = 3, ["A"] = 7, ["B"] = 8, ["ParentA"] = 9, ["T"] = "child" })).Refused);
            Assert.True(Referee(Parts(), "INSERT INTO Part VALUES (3, 7, 8, 1, NULL, 'child')",
                Insert("Part", new Row { ["Id"] = 3, ["A"] = 7, ["B"] = 8, ["ParentA"] = 1, ["T"] = "child" })).Refused);
            Assert.True(Referee(Parts(), "UPDATE Part SET ParentA = 1 WHERE Id = 2", Update("Part", 2, "ParentA", 1)).Refused);
        }

        // ── A parent key that is null ────────────────────────────────────────

        /// <summary>P's U is unique but may be null, and C.U refers to it: ACE holds a null key and a null foreign key to match.</summary>
        private static string[] NullableKey(string options)
        {
            return Seq(
                "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, U LONG CONSTRAINT uq UNIQUE, T TEXT(10))",
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, U LONG, T TEXT(10), CONSTRAINT f FOREIGN KEY (U) REFERENCES P (U) " + options + ")",
                "INSERT INTO P VALUES (1, 10, 'p')",
                "INSERT INTO P VALUES (2, NULL, 'nullkey')",
                "INSERT INTO C VALUES (1, 10, 'c')",
                "INSERT INTO C VALUES (2, NULL, 'cnull')");
        }

        [Fact]
        public void a_parent_whose_key_is_null_has_the_rows_whose_foreign_key_is_null_for_children()
        {
            Assert.True(Referee(NullableKey(""), "DELETE FROM P WHERE K = 2", Delete("P", "K", 2)).Refused);
            Assert.True(Referee(NullableKey(""), "UPDATE P SET U = 5 WHERE K = 2", Update("P", 2, "U", 5)).Refused);

            var deleted = Referee(NullableKey("ON DELETE CASCADE"), "DELETE FROM P WHERE K = 2", Delete("P", "K", 2));
            Assert.False(deleted.Refused);
            Assert.Equal(Seq("Id=1, U=10, T=c"), deleted.Rows("C"));

            var updated = Referee(NullableKey("ON UPDATE CASCADE"), "UPDATE P SET U = 5 WHERE K = 2", Update("P", 2, "U", 5));
            Assert.False(updated.Refused);
            Assert.Equal(Seq("Id=1, U=10, T=c", "Id=2, U=5, T=cnull"), updated.Rows("C"));

            var nulled = Referee(NullableKey("ON UPDATE CASCADE"), "UPDATE P SET U = NULL WHERE K = 1", Update("P", 1, "U", null));
            Assert.False(nulled.Refused);
            Assert.Equal(Seq("Id=1, U=null, T=c", "Id=2, U=null, T=cnull"), nulled.Rows("C"));
        }

        // ── A null key, in full ──────────────────────────────────────────────

        /// <summary>
        /// Parent(Id, K) with K unique but not required, Child(Id, K) referring to it. Row 1 of each has no K; row 2
        /// of each has 20.
        /// </summary>
        private static string[] NullAndNot(string options)
        {
            return Seq(
                "CREATE TABLE Parent (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG CONSTRAINT uq UNIQUE, T TEXT(10))",
                "CREATE TABLE Child (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, T TEXT(10), CONSTRAINT fk FOREIGN KEY (K) REFERENCES Parent (K) " + options + ")",
                "INSERT INTO Parent VALUES (1, NULL, 'no key')",
                "INSERT INTO Parent VALUES (2, 20, 'twenty')",
                "INSERT INTO Child VALUES (1, NULL, 'no key')",
                "INSERT INTO Child VALUES (2, 20, 'twenty')");
        }

        /// <summary>
        /// What ACE does with a parent whose key is null and a child whose foreign key is null. It holds them to be
        /// parent and child: its index on the foreign key has an entry for the null, and the parent's null finds it.
        /// So deleting that parent deletes the child under cascade delete and is refused without it, and giving the
        /// parent a key gives it to the child under cascade update. A review of the engine asked for the opposite -
        /// that a null foreign key never be matched - on the ground that an insert treats it as referring to
        /// nothing. ACE does both, and so does the engine.
        /// </summary>
        [Fact]
        public void a_parent_with_no_key_and_a_child_with_no_foreign_key_are_parent_and_child_to_access()
        {
            // (a) cascade delete: the child goes with it.
            var deleted = Referee(NullAndNot("ON DELETE CASCADE"), "DELETE FROM Parent WHERE Id = 1", Delete("Parent", "Id", 1));
            Assert.False(deleted.Refused);
            Assert.Equal(Seq("Id=2, K=20, T=twenty"), deleted.Rows("Child"));

            // (b) no cascade: the child forbids it.
            var restricted = Referee(NullAndNot(""), "DELETE FROM Parent WHERE Id = 1", Delete("Parent", "Id", 1));
            Assert.True(restricted.Refused);
            Assert.Contains("related records", restricted.Message);
            Assert.True(Referee(NullAndNot(""), "UPDATE Parent SET K = 5 WHERE Id = 1", Update("Parent", 1, "K", 5)).Refused);

            // (c) cascade update: the child is given the key.
            var keyed = Referee(NullAndNot("ON UPDATE CASCADE"), "UPDATE Parent SET K = 5 WHERE Id = 1", Update("Parent", 1, "K", 5));
            Assert.False(keyed.Refused);
            Assert.Equal(Seq("Id=1, K=5, T=no key", "Id=2, K=20, T=twenty"), keyed.Rows("Child"));

            // Cascade-to-null: the child's null is made null, which changes nothing.
            var nulled = Referee(NullAndNot("ON DELETE SET NULL"), "DELETE FROM Parent WHERE Id = 1", Delete("Parent", "Id", 1));
            Assert.False(nulled.Refused);
            Assert.Equal(Seq("Id=1, K=null, T=no key", "Id=2, K=20, T=twenty"), nulled.Rows("Child"));
        }

        [Fact]
        public void a_parent_that_has_a_key_never_touches_a_child_with_no_foreign_key()
        {
            var deleted = Referee(NullAndNot("ON DELETE CASCADE"), "DELETE FROM Parent WHERE Id = 2", Delete("Parent", "Id", 2));
            Assert.False(deleted.Refused);
            Assert.Equal(Seq("Id=1, K=null, T=no key"), deleted.Rows("Child"));

            var keyed = Referee(NullAndNot("ON UPDATE CASCADE"), "UPDATE Parent SET K = 21 WHERE Id = 2", Update("Parent", 2, "K", 21));
            Assert.False(keyed.Refused);
            Assert.Equal(Seq("Id=1, K=null, T=no key", "Id=2, K=21, T=twenty"), keyed.Rows("Child"));

            var nulled = Referee(NullAndNot("ON DELETE SET NULL"), "DELETE FROM Parent WHERE Id = 2", Delete("Parent", "Id", 2));
            Assert.False(nulled.Refused);
            Assert.Equal(Seq("Id=1, K=null, T=no key", "Id=2, K=null, T=twenty"), nulled.Rows("Child"));

            // And a child with no foreign key is added, and made, whatever parents there are.
            var added = Referee(NullAndNot(""), "INSERT INTO Child VALUES (3, NULL, 'another')", Insert("Child", new Row { ["Id"] = 3, ["T"] = "another" }));
            Assert.False(added.Refused);
            var freed = Referee(NullAndNot(""), "UPDATE Child SET K = NULL WHERE Id = 2", Update("Child", 2, "K", null));
            Assert.False(freed.Refused);
        }

        /// <summary>
        /// The same of a key of two columns: parents (null, null), (1, null) and (1, 1), and a child referring to each.
        /// Each parent's children are the rows whose foreign key is null where its key is and equal where it is not.
        /// </summary>
        private static string[] NullsInTwoColumns(string options)
        {
            return Seq(
                "CREATE TABLE Parent (Id LONG CONSTRAINT pk PRIMARY KEY, A LONG, B LONG, CONSTRAINT uq UNIQUE (A, B))",
                "CREATE TABLE Child (Id LONG CONSTRAINT pk PRIMARY KEY, A LONG, B LONG, CONSTRAINT fk FOREIGN KEY (A, B) REFERENCES Parent (A, B) " + options + ")",
                "INSERT INTO Parent VALUES (1, NULL, NULL)",
                "INSERT INTO Parent VALUES (2, 1, NULL)",
                "INSERT INTO Parent VALUES (3, 1, 1)",
                "INSERT INTO Child VALUES (10, NULL, NULL)",
                "INSERT INTO Child VALUES (11, 1, NULL)",
                "INSERT INTO Child VALUES (12, 1, 1)");
        }

        [Theory]
        [InlineData(1, "Id=11, A=1, B=null | Id=12, A=1, B=1")]            // (null, null): the child with neither
        [InlineData(2, "Id=10, A=null, B=null | Id=12, A=1, B=1")]         // (1, null): the child null in the same part
        [InlineData(3, "Id=10, A=null, B=null | Id=11, A=1, B=null")]      // (1, 1)
        public void a_key_of_two_columns_matches_null_for_null_in_each(int parent, string childrenLeft)
        {
            var delete = "DELETE FROM Parent WHERE Id = " + parent;

            var deleted = Referee(NullsInTwoColumns("ON DELETE CASCADE"), delete, Delete("Parent", "Id", parent));
            Assert.False(deleted.Refused);
            Assert.Equal(childrenLeft, string.Join(" | ", deleted.Rows("Child")));

            Assert.True(Referee(NullsInTwoColumns(""), delete, Delete("Parent", "Id", parent)).Refused);

            var keyed = Referee(NullsInTwoColumns("ON UPDATE CASCADE"), "UPDATE Parent SET A = 7, B = 7 WHERE Id = " + parent,
                Update("Parent", parent, new Row { ["A"] = 7, ["B"] = 7 }));
            Assert.False(keyed.Refused);
            Assert.Equal(3, keyed.Rows("Child").Count);
            Assert.Contains("Id=" + (9 + parent) + ", A=7, B=7", keyed.Rows("Child"));
        }

        // ── Keys compared as they are stored ─────────────────────────────────

        /// <summary>P(Id, K) with K unique, C(Id, K) referring to it, K of the type and with the values given.</summary>
        private static string[] Keyed(string type, string options, params string[] values)
        {
            // A Replication ID is made through DAO, as the table designer makes it; its constraints by SQL.
            var setup = type != "GUID"
                ? new List<string>
                {
                    "CREATE TABLE P (Id LONG CONSTRAINT pk PRIMARY KEY, K " + type + " CONSTRAINT uq UNIQUE)",
                    "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K " + type + ", CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) " + options + ")",
                }
                : new List<string>
                {
                    DaoTable + "P: Id LONG, K GUID",
                    DaoTable + "C: Id LONG, K GUID",
                    "ALTER TABLE P ADD CONSTRAINT pk PRIMARY KEY (Id)",
                    "ALTER TABLE P ADD CONSTRAINT uq UNIQUE (K)",
                    "ALTER TABLE C ADD CONSTRAINT pk PRIMARY KEY (Id)",
                    "ALTER TABLE C ADD CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) " + options,
                };
            for (var i = 0; i < values.Length; i++)
            {
                setup.Add("INSERT INTO P VALUES (" + (i + 1) + ", " + values[i] + ")");
                setup.Add("INSERT INTO C VALUES (" + (i + 1) * 10 + ", " + values[i] + ")");
            }
            return setup.ToArray();
        }

        private const string NextDoubleAfterOne = "1 + 2^-52";   // 1.0000000000000002: one bit from 1
        private const string NextSingleAfterOne = "CSng(1 + 2^-23)";
        private const string MinusZero = "CDbl(0) * -1";

        [Fact]
        public void a_double_and_the_next_double_after_it_are_two_keys()
        {
            const double Next = 1.0000000000000002;

            var refused = Referee(Keyed("DOUBLE", "", "1"), "UPDATE P SET K = " + NextDoubleAfterOne + " WHERE Id = 1", Update("P", 1, "K", Next));
            Assert.True(refused.Refused);
            Assert.Equal(Seq("Id=1, K=1"), refused.Rows("P"));
            Assert.Equal(Seq("Id=10, K=1"), refused.Rows("C"));

            var followed = Referee(Keyed("DOUBLE", "ON UPDATE CASCADE", "1"), "UPDATE P SET K = " + NextDoubleAfterOne + " WHERE Id = 1", Update("P", 1, "K", Next));
            Assert.False(followed.Refused);
            Assert.Equal(Seq("Id=1, K=1.0000000000000002"), followed.Rows("P"));
            Assert.Equal(Seq("Id=10, K=1.0000000000000002"), followed.Rows("C"));

            // Each is the key of its own children only.
            var setup = Keyed("DOUBLE", "ON DELETE CASCADE", "1", NextDoubleAfterOne);
            var deleted = Referee(setup, "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1));
            Assert.Equal(Seq("Id=20, K=1.0000000000000002"), deleted.Rows("C"));
            Assert.True(Referee(setup, "INSERT INTO C VALUES (30, 1 + 2^-51)", Insert("C", new Row { ["Id"] = 30, ["K"] = 1.0000000000000004 })).Refused);
        }

        [Fact]
        public void a_single_and_the_next_single_after_it_are_two_keys()
        {
            const float Next = 1.00000012f;

            Assert.True(Referee(Keyed("REAL", "", "1"), "UPDATE P SET K = " + NextSingleAfterOne + " WHERE Id = 1", Update("P", 1, "K", Next)).Refused);

            var followed = Referee(Keyed("REAL", "ON UPDATE CASCADE", "1"), "UPDATE P SET K = " + NextSingleAfterOne + " WHERE Id = 1", Update("P", 1, "K", Next));
            Assert.False(followed.Refused);
            Assert.Equal(Seq("Id=10, K=" + ((double)Next).ToString("R", CultureInfo.InvariantCulture)), followed.Rows("C"));

            var deleted = Referee(Keyed("REAL", "ON DELETE CASCADE", "1", NextSingleAfterOne), "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1));
            Assert.Equal(1, deleted.Rows("C").Count);
        }

        /// <summary>
        /// 0 and -0: equal as numbers, two keys to ACE, whose index holds a Double by its bits. A unique index takes
        /// both; each is the parent of the children that have its own zero; and 0 changed to -0 is a change.
        /// </summary>
        [Fact]
        public void zero_and_minus_zero_are_two_keys()
        {
            var both = Keyed("DOUBLE", "ON UPDATE CASCADE ON DELETE CASCADE", "0", MinusZero);
            Assert.Equal(Seq("Id=1, K=0", "Id=2, K=-0"), ReadWithAccess(Build(both))["P"]);   // ACE took both

            var deleted = Referee(both, "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1));
            Assert.False(deleted.Refused);
            Assert.Equal(Seq("Id=20, K=-0"), deleted.Rows("C"));

            // 0 to -0 is a change: here to the key row 2 has.
            Assert.True(Referee(both, "UPDATE P SET K = " + MinusZero + " WHERE Id = 1", Update("P", 1, "K", -0.0)).Refused);

            var alone = Keyed("DOUBLE", "ON UPDATE CASCADE", "0");
            var followed = Referee(alone, "UPDATE P SET K = " + MinusZero + " WHERE Id = 1", Update("P", 1, "K", -0.0));
            Assert.False(followed.Refused);
            Assert.Equal(Seq("Id=10, K=-0"), followed.Rows("C"));

            Assert.True(Referee(Keyed("DOUBLE", "", "0"), "UPDATE P SET K = " + MinusZero + " WHERE Id = 1", Update("P", 1, "K", -0.0)).Refused);
            Assert.True(Referee(Keyed("DOUBLE", "", "0"), "INSERT INTO C VALUES (30, " + MinusZero + ")", Insert("C", new Row { ["Id"] = 30, ["K"] = -0.0 })).Refused);
        }

        /// <summary>
        /// A Date/Time is a Double of days, and ACE writes ones a DateTime does not hold: read as a DateTime and keyed
        /// again, 40000.123456789 is another key, and the child it looks for is not found. These are found.
        /// </summary>
        [Fact]
        public void a_date_is_the_double_access_stored_not_the_date_time_it_reads_as()
        {
            const string Odd = "CDate(40000.123456789)", Other = "CDate(45000.123456789)";
            var when = new DateTime(2020, 1, 2, 3, 4, 5);

            // First, that the questions the referee asks of a date are ones with an answer: ACE stored the double
            // given, a reader's DateTime has lost it, and both the indexed condition and the scanned one find the row
            // by it - and neither by the double the DateTime would make.
            var stored = Build(Keyed("DATETIME", "", Odd, Other));
            var key = KeysIn(stored, Relations(stored)).Single(k => k.Table == "C" && k.Column == "K" && k.Literal == "CDate(40000.123456789)");
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(stored)))
            {
                connection.Open();
                Assert.Equal(Seq("Expr1000=40000.123456789"), Query(connection, "SELECT CDbl(K) FROM C WHERE Id = 10"));
                Assert.Equal(Seq("Id=10"), Query(connection, "SELECT Id FROM C WHERE " + key.ByIndex("K")));
                Assert.Equal(Seq("Id=10"), Query(connection, "SELECT Id FROM C WHERE " + key.ByScan("K")));
                Assert.Equal(Seq("Id=1"), Query(connection, "SELECT Id FROM P WHERE " + key.ByIndex("K")));
                Assert.Empty(Query(connection, "SELECT Id FROM C WHERE K = CDate(40000.12345679398)"));
                Assert.Empty(Query(connection, "SELECT Id FROM C WHERE K + 0 = 40000.12345679398"));
            }

            Assert.True(Referee(Keyed("DATETIME", "", Odd, Other), "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1)).Refused);
            Assert.True(Referee(Keyed("DATETIME", "", Odd, Other), "UPDATE P SET K = #2020-01-02 03:04:05# WHERE Id = 1", Update("P", 1, "K", when)).Refused);

            var deleted = Referee(Keyed("DATETIME", "ON DELETE CASCADE", Odd, Other), "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1));
            Assert.False(deleted.Refused);
            Assert.Equal(1, deleted.Rows("C").Count);

            var followed = Referee(Keyed("DATETIME", "ON UPDATE CASCADE", Odd, Other), "UPDATE P SET K = #2020-01-02 03:04:05# WHERE Id = 1", Update("P", 1, "K", when));
            Assert.False(followed.Refused);
            Assert.Contains(followed.Rows("C"), row => row.StartsWith("Id=10, K=01/02/2020 03:04:05"));

            var nulled = Referee(Keyed("DATETIME", "ON DELETE SET NULL", Odd, Other), "DELETE FROM P WHERE Id = 2", Delete("P", "Id", 2));
            Assert.Contains("Id=20, K=null", nulled.Rows("C"));
        }

        [Fact]
        public void a_row_a_cascade_rewrites_keeps_the_dates_it_did_not_change_to_the_last_bit()
        {
            // The child refers to a date and to a number. Re-keying the number's parent rewrites the child; its date,
            // which a DateTime does not hold, must still be its date's parent's - by ACE's index and by ACE's rules.
            var setup = Seq(
                "CREATE TABLE P (Id LONG CONSTRAINT pk PRIMARY KEY, K DATETIME CONSTRAINT uq UNIQUE)",
                "CREATE TABLE Q (N LONG CONSTRAINT pk PRIMARY KEY)",
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K DATETIME, N LONG, "
                    + "CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K), CONSTRAINT fq FOREIGN KEY (N) REFERENCES Q (N) ON UPDATE CASCADE)",
                "INSERT INTO P VALUES (1, CDate(40000.123456789))",
                "INSERT INTO Q VALUES (1)",
                "INSERT INTO C VALUES (10, CDate(40000.123456789), 1)");

            var rewritten = Referee(setup, "UPDATE Q SET N = 9 WHERE N = 1", Update("Q", 1, "N", 9));

            Assert.False(rewritten.Refused);
            Assert.Contains(rewritten.Rows("C"), row => row.EndsWith("N=9"));
        }

        /// <summary>
        /// A Yes/No holds no null: a null given it is stored No. So a child given null refers to the parent whose key
        /// is No, and is refused when there is none - on insert and on update - and a cascade-to-null leaves the
        /// child No, which ACE does too, with no parent to answer for it.
        /// </summary>
        [Fact]
        public void a_yes_no_foreign_key_given_null_is_no()
        {
            var setup = Keyed("YESNO", "", "True");

            Assert.True(Referee(setup, "UPDATE C SET K = NULL WHERE Id = 10", Update("C", 10, "K", null)).Refused);
            Assert.True(Referee(setup, "UPDATE C SET K = False WHERE Id = 10", Update("C", 10, "K", false)).Refused);
            Assert.True(Referee(setup, "INSERT INTO C VALUES (11, NULL)", Insert("C", new Row { ["Id"] = 11, ["K"] = null })).Refused);
            Assert.True(Referee(setup, "INSERT INTO C (Id) VALUES (11)", Insert("C", new Row { ["Id"] = 11 })).Refused);
            Assert.True(Referee(setup, "INSERT INTO C VALUES (11, False)", Insert("C", new Row { ["Id"] = 11, ["K"] = false })).Refused);
            Assert.False(Referee(setup, "INSERT INTO C VALUES (11, True)", Insert("C", new Row { ["Id"] = 11, ["K"] = true })).Refused);
            Assert.True(Referee(setup, "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1)).Refused);

            // With a parent whose key is No, each of those is allowed.
            var withNo = Keyed("YESNO", "", "True").Concat(Seq("INSERT INTO P VALUES (2, False)")).ToList();
            var toNo = Referee(withNo, "UPDATE C SET K = NULL WHERE Id = 10", Update("C", 10, "K", null));
            Assert.False(toNo.Refused);
            Assert.Equal(Seq("Id=10, K=False"), toNo.Rows("C"));
            Assert.False(Referee(withNo, "INSERT INTO C (Id) VALUES (11)", Insert("C", new Row { ["Id"] = 11 })).Refused);

            // Cascade-to-null: ACE makes the relationship, deletes the parent and leaves the child No.
            var nulled = Referee(Keyed("YESNO", "ON DELETE SET NULL", "True"), "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1));
            Assert.False(nulled.Refused);
            Assert.Empty(nulled.Rows("P"));
            Assert.Equal(Seq("Id=10, K=False"), nulled.Rows("C"));

            var followed = Referee(Keyed("YESNO", "ON UPDATE CASCADE", "True"), "UPDATE P SET K = False WHERE Id = 1", Update("P", 1, "K", false));
            Assert.False(followed.Refused);
            Assert.Equal(Seq("Id=10, K=False"), followed.Rows("C"));
        }

        /// <summary>
        /// For every type of column the engine keys: values ACE wrote, in a parent's unique key and in a child's
        /// foreign key. The engine finds the children of one parent through the foreign key's index - there is no
        /// reading the table after an index has answered - and no others: deleting the parent deletes them under
        /// cascade delete and is refused without it.
        /// </summary>
        [Theory]
        [InlineData("BYTE", "7", "200")]
        [InlineData("SHORT", "-5", "300")]
        [InlineData("LONG", "-70000", "70000")]
        [InlineData("CURRENCY", "1.2345", "-9.5")]
        [InlineData("REAL", "CSng(1.1)", "-2.5")]
        [InlineData("DOUBLE", "0.1", NextDoubleAfterOne)]
        [InlineData("DATETIME", "CDate(40000.123456789)", "#2020-01-02 03:04:05#")]
        [InlineData("YESNO", "True", "False")]
        [InlineData("TEXT(20)", "'abc'", "'Straße'")]
        [InlineData("MEMO", "'a memo'", "'another memo'")]
        [InlineData("GUID", "{guid {11111111-2222-3333-4444-555555555555}}", "{guid {66666666-7777-8888-9999-00000000ABCD}}")]
        public void a_cascade_finds_the_children_through_the_index_for_a_key_of_any_type(string type, string one, string other)
        {
            var cascading = Keyed(type, "ON DELETE CASCADE", one, other).Concat(Seq("INSERT INTO C VALUES (11, " + one + ")")).ToList();

            // The questions the referee asks of this type's values have answers: each finds the first parent's two children.
            var built = Build(cascading);
            var first = KeysIn(built, Relations(built)).First(k => k.Table == "P" && k.Column == "K");
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(built)))
            {
                connection.Open();
                Assert.True(Query(connection, "SELECT Id FROM C WHERE " + first.ByIndex("K")).Count >= 1, type + ": " + first.ByIndex("K") + " finds nothing");
                Assert.Equal(string.Join(" | ", Query(connection, "SELECT Id FROM C WHERE " + first.ByIndex("K"))),
                             string.Join(" | ", Query(connection, "SELECT Id FROM C WHERE " + first.ByScan("K"))));
            }

            var deleted = Referee(cascading, "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1));
            Assert.False(deleted.Refused);
            Assert.Equal(1, deleted.Rows("P").Count);
            Assert.True(deleted.Rows("C").Count == 1 && deleted.Rows("C")[0].StartsWith("Id=20, "), type + ": " + string.Join(" | ", deleted.Rows("C")));

            var restricted = Referee(Keyed(type, "", one, other), "DELETE FROM P WHERE Id = 1", Delete("P", "Id", 1));
            Assert.True(restricted.Refused, type + ": the parent was deleted from under its child");
            Assert.Equal(2, restricted.Rows("C").Count);
        }

        // ── Where the engine does not do as ACE does ─────────────────────────

        /// <summary>
        /// ACE keeps a Replication ID column made by SQL among a row's variable-length values. The engine did not
        /// write one there, and refused a cascade that would rewrite such a row; it
        /// writes rows by the table's definition now, and makes the cascade as ACE does.
        /// </summary>
        [Fact]
        public void a_cascade_rewrites_rows_that_hold_a_replication_id_made_by_sql()
        {
            var setup = Seq(
                "CREATE TABLE P (K LONG CONSTRAINT pk PRIMARY KEY, T TEXT(10))",
                "CREATE TABLE C (Id LONG CONSTRAINT pk PRIMARY KEY, K LONG, G GUID, Y YESNO, W DATETIME, "
                    + "CONSTRAINT fk FOREIGN KEY (K) REFERENCES P (K) ON UPDATE CASCADE ON DELETE CASCADE)",
                "CREATE INDEX byG ON C (G)",
                "INSERT INTO P VALUES (1, 'p1')",
                "INSERT INTO P VALUES (2, 'p2')",
                "INSERT INTO C VALUES (10, 1, '{11111111-2222-3333-4444-555555555555}', True, #2026-09-14 10:30:00#)",
                "INSERT INTO C VALUES (11, 1, NULL, False, NULL)",
                "INSERT INTO C VALUES (20, 2, '{66666666-7777-8888-9999-000000000000}', False, #2026-09-15#)");

            var updated = Referee(setup, "UPDATE P SET K = 7 WHERE K = 1", Update("P", 1, "K", 7));
            Assert.False(updated.Refused);
            Assert.Equal(3, updated.Rows("C").Count);
            Assert.Contains(updated.Rows("C"), row => row.StartsWith("Id=10, K=7, G=11111111-2222-3333-4444-555555555555"));

            var deleted = Referee(setup, "DELETE FROM P WHERE K = 1", Delete("P", "K", 1));
            Assert.False(deleted.Refused);
            Assert.Single(deleted.Rows("C"));
        }

        /// <summary>
        /// ACE refuses an UPDATE that names a key column other rows refer to, when the relationship does not cascade
        /// updates - even one that writes the value the column already has. The engine compares the values: a key
        /// written again as it is changes nothing, so a caller who reads a row, changes a name and writes the whole
        /// row back is not refused. No row is left without a parent either way.
        /// </summary>
        [Fact]
        public void a_key_written_again_unchanged_is_refused_by_access_and_allowed_by_the_engine()
        {
            var setup = ParentAndChild("");

            var accessPath = Build(setup);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(accessPath)))
            using (var command = new OleDbCommand("UPDATE P SET K = 1, T = 'new' WHERE K = 1", connection))
            {
                connection.Open();
                Assert.Throws<OleDbException>(() => command.ExecuteNonQuery());
            }

            var enginePath = Build(setup);
            using (var db = Database.Open(enginePath))
            {
                db.EnforceForeignKeys = true;
                db.GetTable("P").UpdateByPrimaryKey(1, new Row { ["K"] = 1, ["T"] = "new" });
            }
            Assert.Contains("K=1, T=new", ReadWithAccess(enginePath)["P"]);
            Assert.Equal(3, ReadWithAccess(enginePath)["C"].Count);
        }
    }
}
