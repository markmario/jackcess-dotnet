using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Properties the managed engine writes, and what a column's Required and Allow Zero Length properties refuse,
    /// checked by Access (#27, #41). Each case runs in an .mdb and in an .accdb, on a table
    /// Access (DAO) made and on a table the engine made.
    /// <para>
    /// Properties: DAO lists every property the engine wrote on a table, on a column and on the database, with its
    /// type and value, beside the ones Access had written there, before and after it compacts the file.
    /// </para>
    /// <para>
    /// Required and Allow Zero Length: DAO reports of each field the engine made what its definition asked, the
    /// engine reports of each field DAO made what DAO was asked, and a row ACE refuses is a row the engine refuses -
    /// the same rows given to both, added and updated.
    /// </para>
    /// </summary>
    public class PropertiesInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        /// <summary>An .mdb or an .accdb, and whether Access or the engine makes the database and its table.</summary>
        public static IEnumerable<object[]> Kinds()
        {
            foreach (var accdb in new[] { false, true })
                foreach (var accessMade in new[] { true, false })
                    yield return new object[] { accdb, accessMade };
        }

        private string NewDatabase(bool accdb, bool accessMade)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + (accdb ? ".accdb" : ".mdb"));
            if (accessMade) DaoCreate(path, accdb ? 128 : 64);
            else
            {
                // The engine's .accdb is Access 2010's format, which 32-bit ACE 12 does not open.
                if (accdb) SkipUnlessAceOpensAccess2010();
                Database.Create(path, accdb ? FileFormat.Access2010 : FileFormat.Access2000).Dispose();
            }
            return path;
        }

        // ── DAO ──────────────────────────────────────────────────────────────

        private const int DaoBoolean = 1, DaoByte = 2, DaoInteger = 3, DaoLong = 4, DaoCurrency = 5, DaoSingle = 6, DaoDouble = 7,
            DaoDate = 8, DaoBinary = 9, DaoText = 10, DaoLongBinary = 11, DaoMemo = 12, DaoGuid = 15;
        private const int DaoAutoIncrement = 16;

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

        private static void DaoAddField(dynamic table, string name, int type, int size = 0, bool required = false, bool allowZeroLength = false, int attributes = 0)
        {
            dynamic field = size > 0 ? table.CreateField(name, type, size) : table.CreateField(name, type);
            if (attributes != 0) field.Attributes = attributes;
            if (required) field.Required = true;
            if (allowZeroLength) field.AllowZeroLength = true;
            table.Fields.Append(field);
        }

        private static void DaoAddProperty(dynamic owner, string name, int type, object value)
        {
            owner.Properties.Append(owner.CreateProperty(name, type, value));
        }

        /// <summary>A value as the tests compare it, whoever read it.</summary>
        private static string Shown(object value)
        {
            if (value is byte[] bytes) return BitConverter.ToString(bytes);
            if (value is DateTime date) return date.ToString("o", CultureInfo.InvariantCulture);
            if (value is Guid guid) return guid.ToString("D");
            // DAO gives a Replication ID as "{guid {11223344-...}}".
            if (value is string text && text.StartsWith("{guid {", StringComparison.OrdinalIgnoreCase))
                return Guid.Parse(text.Substring(6, text.Length - 7)).ToString("D");
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The properties DAO lists for an object that are not its built-in ones, as "name (DAO type) = value", in
        /// name order. Built-in properties have no place in the file and are the same for every object of a kind.
        /// </summary>
        private static List<string> Listed(dynamic owner, params string[] builtIn)
        {
            var listed = new List<string>();
            foreach (dynamic property in owner.Properties)
            {
                string name = property.Name;
                if (builtIn.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                object value;
                try { value = property.Value; }
                catch (Exception) { continue; }   // a built-in property that has no value for this object
                listed.Add(name + " (" + (int)property.Type + ") = " + Shown(value));
            }
            listed.Sort(StringComparer.Ordinal);
            return listed;
        }

        private static readonly string[] TableBuiltIn =
            { "Name", "Updatable", "DateCreated", "LastUpdated", "Connect", "Attributes", "SourceTableName", "RecordCount", "ValidationRule", "ValidationText", "ConflictTable", "ReplicaFilter" };

        private static readonly string[] FieldBuiltIn =
        {
            "Value", "Attributes", "CollatingOrder", "Type", "Name", "OrdinalPosition", "Size", "SourceField", "SourceTable",
            "ValidateOnSet", "DataUpdatable", "ForeignName", "DefaultValue", "ValidationRule", "ValidationText", "Required",
            "AllowZeroLength", "FieldSize", "OriginalValue", "VisibleValue", "ColumnWidth", "ColumnOrder", "ColumnHidden",
            "AppendOnly", "Expression", "Precision", "Scale", "ComplexType", "IsComplex",
        };

        private static readonly string[] DatabaseBuiltIn =
        {
            "Name", "Connect", "Transactions", "Updatable", "CollatingOrder", "QueryTimeout", "Version", "RecordsAffected",
            "ReplicaID", "DesignMasterID", "Connection", "ANSI Query Mode", "Themed Form Controls", "Use Microsoft Access 2007 compatible cache",
            "Clear Cache on Close", "Never Cache", "AccessVersion", "Build", "ProjVer", "NavPane Category", "NavPane Closed", "NavPane Width",
            "NavPane View By", "NavPane Sort By", "Track Name AutoCorrect Info", "Perform Name AutoCorrect", "HasOfflineLists",
            "Picture Property Storage Format", "Show Navigation Pane Search Bar", "NavPane Category Name", "UseMDIMode", "ShowDocumentTabs",
            "WebDesignMode", "CheckTruncatedNumFields", "Property Sheet Label Width", "Theme Resource Name", "DesignWithData",
        };

        private sealed class Read
        {
            public List<string> Table, Name, Id, Database;
        }

        private static Read DaoProperties(string path)
        {
            return WithDao(path, db => new Read
            {
                Table    = Listed(db.TableDefs["T"], TableBuiltIn),
                Name     = Listed(db.TableDefs["T"].Fields["Name"], FieldBuiltIn),
                Id       = Listed(db.TableDefs["T"].Fields["Id"], FieldBuiltIn),
                Database = Listed(db, DatabaseBuiltIn),
            });
        }

        // ── Properties ───────────────────────────────────────────────────────

        private static readonly DateTime ADate = new DateTime(2026, 11, 19, 10, 20, 30);
        private static readonly Guid AGuid = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        private static readonly string ALongText = new string('t', 1000);   // Text, of more characters than a Text column holds
        private static readonly string AMemo = new string('m', 3000) + "é日";   // too long for its row of the catalog

        [Theory]
        [MemberData(nameof(Kinds))]
        public void dao_lists_every_property_the_engine_writes_beside_the_ones_Access_wrote(bool accdb, bool accessMade)
        {
            var path = NewDatabase(accdb, accessMade);
            if (accessMade)
                WithDao(path, db =>
                {
                    dynamic table = db.CreateTableDef("T");
                    DaoAddField(table, "Id", DaoLong, attributes: DaoAutoIncrement);
                    DaoAddField(table, "Name", DaoText, 20);
                    DaoAddField(table, "Notes", DaoMemo);
                    db.TableDefs.Append(table);
                    DaoAddProperty(db.TableDefs["T"], "Description", DaoText, "A table Access made");
                    DaoAddProperty(db.TableDefs["T"].Fields["Name"], "Caption", DaoText, "Access's caption");
                    DaoAddProperty(db.TableDefs["T"].Fields["Name"], "AccessCount", DaoLong, 7);
                    DaoAddProperty(db, "AccessTitle", DaoText, "Access's own");
                    return 0;
                });
            else
                using (var db = Database.Open(path))
                    db.CreateTable("T", new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true },
                        new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
                        new ColumnDefinition("Notes", DataType.Memo),
                    });
            var before = DaoProperties(path);

            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                table.Properties.Set("EngineTableText", "On the table");
                table.Properties.Set("EngineTableLong", 77);
                table.Properties.Set("EngineLongText", ALongText);
                if (!accessMade) table.Properties.Set("Description", "A table the engine made");

                var name = table.Columns.Single(c => c.Name == "Name").Properties;
                name.Set("Description", "The name");
                name.Set("EngineBool", true);
                name.Set("EngineByte", (byte)200);
                name.Set("EngineShort", (short)-2);
                name.Set("EngineLong", 123456);
                name.Set("EngineSingle", 1.5f);
                name.Set("EngineDouble", -2.25);
                name.Set("EngineCurrency", 12.3456m);
                name.Set("EngineDate", ADate);
                name.Set("EngineGuid", AGuid);
                name.Set("EngineOle", new byte[] { 1, 2, 3 });
                name.Set("EngineBinary", DataType.Binary, new byte[] { 4, 5 });
                name.Set("EngineMemo", DataType.Memo, AMemo);
                table.Columns.Single(c => c.Name == "Id").Properties.Set("Caption", "An AutoNumber Access keeps no property for");
                table.Properties.Save();

                db.Properties.Set("EngineTitle", "The engine's own");
                db.Properties.Set("EngineCount", 42);
                db.Properties.Save();
            }

            var expected = new Read
            {
                Table = before.Table.Concat(new[] { "EngineTableLong (4) = 77", "EngineTableText (10) = On the table", "EngineLongText (10) = " + ALongText })
                    .Concat(accessMade ? new string[0] : new[] { "Description (10) = A table the engine made" }).ToList(),
                Name = before.Name.Concat(new[]
                {
                    "Description (10) = The name",
                    "EngineBool (1) = True", "EngineByte (2) = 200", "EngineShort (3) = -2", "EngineLong (4) = 123456",
                    "EngineSingle (6) = 1.5", "EngineDouble (7) = -2.25", "EngineCurrency (5) = 12.3456",
                    "EngineDate (8) = " + Shown(ADate), "EngineGuid (15) = " + Shown(AGuid), "EngineOle (11) = 01-02-03",
                    "EngineBinary (9) = 04-05", "EngineMemo (12) = " + AMemo,
                }).ToList(),
                Id = before.Id.Concat(new[] { "Caption (12) = An AutoNumber Access keeps no property for" }).ToList(),
                Database = before.Database.Concat(new[] { "EngineCount (4) = 42", "EngineTitle (10) = The engine's own" }).ToList(),
            };
            foreach (var list in new[] { expected.Table, expected.Name, expected.Id, expected.Database }) list.Sort(StringComparer.Ordinal);
            if (accessMade)
            {
                Assert.Contains("Description (10) = A table Access made", before.Table);
                Assert.Contains("AccessCount (4) = 7", before.Name);
                Assert.Contains("Caption (10) = Access's caption", before.Name);
                Assert.Contains("AccessTitle (10) = Access's own", before.Database);
            }

            foreach (var file in new[] { path, Compact(path) })
            {
                var read = DaoProperties(file);
                Assert.Equal(expected.Table, read.Table);
                Assert.Equal(expected.Name, read.Name);
                Assert.Equal(expected.Id, read.Id);
                Assert.Equal(expected.Database, read.Database);
            }

            // Access changes and removes what the engine wrote, and adds its own beside it; the engine reads the result.
            WithDao(path, db =>
            {
                dynamic field = db.TableDefs["T"].Fields["Name"];
                field.Properties["EngineLong"].Value = 654321;
                field.Properties.Delete("EngineByte");
                DaoAddProperty(field, "AccessLater", DaoText, "Added after the engine");
                db.TableDefs["T"].Properties["EngineTableText"].Value = "Changed by Access";
                db.Properties["EngineCount"].Value = 43;
                return 0;
            });
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("T");
                var name = table.Columns.Single(c => c.Name == "Name").Properties;
                Assert.Equal(654321, name["EngineLong"]);
                Assert.False(name.TryGetValue("EngineByte", out _));
                Assert.Equal("Added after the engine", name["AccessLater"]);
                Assert.Equal(AMemo, name["EngineMemo"]);
                Assert.Equal(AGuid, name["EngineGuid"]);
                Assert.Equal("Changed by Access", table.Properties["EngineTableText"]);
                Assert.Equal(43, db.Properties["EngineCount"]);
            }
        }

        [Fact]
        public void dao_lists_the_summary_and_user_defined_properties_the_engine_writes()
        {
            // The engine's own new .mdb has the two documents Access's interface gives a database; DAO alone makes neither.
            var path = NewDatabase(accdb: false, accessMade: false);
            Func<string, List<string>> documents = file => WithDao(file, db =>
            {
                var listed = new List<string>();
                foreach (var document in new[] { "SummaryInfo", "UserDefined" })
                    foreach (var line in Listed(db.Containers["Databases"].Documents[document],
                                 "Name", "Owner", "UserName", "Permissions", "AllPermissions", "Container", "DateCreated", "LastUpdated"))
                        listed.Add(document + ": " + line);
                return listed;
            });
            var before = documents(path);
            Assert.Contains(before, line => line.StartsWith("SummaryInfo: Author (10) = "));

            using (var db = Database.Open(path))
            {
                db.SummaryProperties.Set("Title", "Set by the engine");
                db.SummaryProperties.Set("Subject", "Added by the engine");
                db.SummaryProperties.Save();
                db.UserDefinedProperties.Set("Project", "A project");
                db.UserDefinedProperties.Set("Count", 9);
                db.UserDefinedProperties.Save();
            }

            var expected = before.Where(line => !line.StartsWith("SummaryInfo: Title (")).Concat(new[]
            {
                "SummaryInfo: Title (10) = Set by the engine", "SummaryInfo: Subject (10) = Added by the engine",
                "UserDefined: Project (10) = A project", "UserDefined: Count (4) = 9",
            }).OrderBy(line => line, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, documents(path).OrderBy(line => line, StringComparer.Ordinal));
            Assert.Equal(expected, documents(Compact(path)).OrderBy(line => line, StringComparer.Ordinal));
        }

        // ── Required and Allow Zero Length ───────────────────────────────────

        private sealed class Field
        {
            public string Name; public DataType Type; public int Dao; public bool Required, AllowZeroLength;
        }

        private static readonly Field[] Fields =
        {
            new Field { Name = "OptLong",     Type = DataType.Int32,    Dao = DaoLong },
            new Field { Name = "ReqLong",     Type = DataType.Int32,    Dao = DaoLong,    Required = true },
            new Field { Name = "TextZero",    Type = DataType.Text,     Dao = DaoText,    AllowZeroLength = true },
            new Field { Name = "TextNoZero",  Type = DataType.Text,     Dao = DaoText },
            new Field { Name = "TextReq",     Type = DataType.Text,     Dao = DaoText,    Required = true },
            new Field { Name = "TextReqZero", Type = DataType.Text,     Dao = DaoText,    Required = true, AllowZeroLength = true },
            new Field { Name = "MemoZero",    Type = DataType.Memo,     Dao = DaoMemo,    AllowZeroLength = true },
            new Field { Name = "MemoNoZero",  Type = DataType.Memo,     Dao = DaoMemo },
            new Field { Name = "BoolReq",     Type = DataType.Boolean,  Dao = DaoBoolean, Required = true },
            new Field { Name = "DateReq",     Type = DataType.DateTime, Dao = DaoDate,    Required = true },
        };

        private static ColumnDefinition Definition(Field field)
        {
            return new ColumnDefinition(field.Name, field.Type)
            {
                MaxLength = field.Type == DataType.Text ? 20 : (int?)null,
                IsRequired = field.Required,
                AllowZeroLength = field.AllowZeroLength,
            };
        }

        /// <summary>R, with a field of every kind a rule can be asked of: made by DAO, or by the engine - its last two fields added afterwards.</summary>
        private string Ruled(bool accdb, bool accessMade)
        {
            var path = NewDatabase(accdb, accessMade);
            if (accessMade)
                WithDao(path, db =>
                {
                    dynamic table = db.CreateTableDef("R");
                    DaoAddField(table, "Id", DaoLong, attributes: DaoAutoIncrement);
                    foreach (var field in Fields)
                        DaoAddField(table, field.Name, field.Dao, field.Dao == DaoText ? 20 : 0, field.Required, field.AllowZeroLength);
                    db.TableDefs.Append(table);
                    db.Execute("CREATE UNIQUE INDEX PrimaryKey ON R (Id) WITH PRIMARY");
                    return 0;
                });
            else
                using (var db = Database.Open(path))
                {
                    var made = Fields.Take(Fields.Length - 2).Select(Definition).ToList();
                    made.Insert(0, new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true });
                    var table = db.CreateTable("R", made, "Id");
                    foreach (var field in Fields.Skip(Fields.Length - 2))
                        table.AddColumn(Definition(field));
                }
            return path;
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void dao_and_the_engine_say_the_same_of_each_field(bool accdb, bool accessMade)
        {
            var path = Ruled(accdb, accessMade);
            var asked = Fields.Select(f => f.Name + ": required=" + f.Required + " allowZeroLength=" + f.AllowZeroLength).ToList();

            foreach (var file in new[] { path, Compact(path) })
            {
                var dao = WithDao(file, db =>
                {
                    var said = new List<string>();
                    foreach (var field in Fields)
                    {
                        dynamic daoField = db.TableDefs["R"].Fields[field.Name];
                        said.Add(field.Name + ": required=" + (bool)daoField.Required + " allowZeroLength=" + (bool)daoField.AllowZeroLength);
                    }
                    return said;
                });
                Assert.Equal(asked, dao);

                using (var db = Database.Open(file))
                {
                    var table = db.GetTable("R");
                    Assert.Equal(asked, Fields.Select(f =>
                    {
                        var column = table.Columns.Single(c => c.Name == f.Name);
                        return f.Name + ": required=" + column.IsRequired + " allowZeroLength=" + column.AllowZeroLength;
                    }));
                }
            }
        }

        private static Values Whole()
        {
            return V("ReqLong", 1, "TextReq", "x", "TextReqZero", "x", "DateReq", new DateTime(2026, 1, 2));
        }

        private static Values With(params object[] namesAndValues)
        {
            var row = Whole();
            for (var i = 0; i < namesAndValues.Length; i += 2)
                row[(string)namesAndValues[i]] = namesAndValues[i + 1];
            return row;
        }

        private static Values Without(string name)
        {
            var row = Whole();
            row.Remove(name);
            return row;
        }

        private static bool AceTakes(Action write)
        {
            try { write(); return true; }
            catch (OleDbException) { return false; }
        }

        private static bool EngineTakes(Action write)
        {
            try { write(); return true; }
            catch (ConstraintViolationException) { return false; }
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void the_engine_refuses_the_rows_ace_refuses(bool accdb, bool accessMade)
        {
            var path = Ruled(accdb, accessMade);

            // Each row, whether Access takes it, and why.
            var rows = new (string What, Values Row, bool Taken)[]
            {
                ("every required field given but the Yes/No, which is No", Whole(), true),
                ("no value for a required Long", Without("ReqLong"), false),
                ("a null for a required Long", With("ReqLong", null), false),
                ("no value for required Text", Without("TextReq"), false),
                ("no value for a required Date/Time", Without("DateReq"), false),
                ("a zero-length string where it is allowed", With("TextZero", "", "MemoZero", ""), true),
                ("a zero-length string in Text that does not allow it", With("TextNoZero", ""), false),
                ("a zero-length string in a Memo that does not allow it", With("MemoNoZero", ""), false),
                ("a zero-length string in required Text that does not allow it", With("TextReq", ""), false),
                ("a zero-length string in required Text that allows it", With("TextReqZero", ""), true),
                ("spaces where a zero-length string is not allowed", With("TextNoZero", "  ", "MemoNoZero", " "), true),
            };

            var expected = new List<string>();
            foreach (var (what, row, taken) in rows)
            {
                bool ace = AceTakes(() => AceInsert(path, "R", row));
                bool engine;
                using (var db = Database.Open(path))
                    engine = EngineTakes(() => db.GetTable("R").Insert(EngineRow(row)));
                Assert.True(ace == taken, "ACE " + (ace ? "took" : "refused") + " a row with " + what);
                Assert.True(engine == taken, "the engine " + (engine ? "took" : "refused") + " a row with " + what);
                if (taken) { expected.Add(what); expected.Add(what); }
            }
            Assert.Equal(expected.Count, AceTable(path, "R").Count);

            // An update is held to the fields it gives a value, and to no other: the first two rows are whole,
            // one for ACE to change and one for the engine.
            var ids = AceRows(path, "SELECT Id FROM R ORDER BY Id").Select(r => r["Id"]).Take(2).ToList();
            var changes = new (string What, Values Change, bool Taken)[]
            {
                ("another field", V("OptLong", 5), true),
                ("a null for a required Long", V("ReqLong", null), false),
                ("a null for required Text, with another field", V("OptLong", 6, "TextReq", null), false),
                ("a zero-length string in Text that does not allow it", V("TextNoZero", ""), false),
                ("a zero-length string in a Memo that does not allow it", V("MemoNoZero", ""), false),
                ("a zero-length string where it is allowed", V("TextZero", "", "TextReqZero", ""), true),
                ("a value for a required field", V("ReqLong", 9), true),
            };
            foreach (var (what, change, taken) in changes)
            {
                bool ace = AceTakes(() => AceUpdate(path, "R", "Id", ids[0], change));
                bool engine;
                using (var db = Database.Open(path))
                    engine = EngineTakes(() => db.GetTable("R").UpdateByPrimaryKey(ids[1], EngineRow(change)));
                Assert.True(ace == taken, "ACE " + (ace ? "took" : "refused") + " an update with " + what);
                Assert.True(engine == taken, "the engine " + (engine ? "took" : "refused") + " an update with " + what);
            }

            // The two changed rows are the same row but for their ids, and the table is what both read, before and
            // after DAO compacts it.
            var both = AssertEngineReadsWhatAceReads(path, "R").Where(r => ids.Contains(r["Id"])).ToList();
            Assert.Equal(2, both.Count);
            foreach (var row in both) row.Remove("Id");
            Assert.Equal(Written(both.Take(1)), Written(both.Skip(1)));
            var compacted = Compact(path);
            Assert.Equal(expected.Count, AssertEngineReadsWhatAceReads(compacted, "R").Count);
        }

        // ── A value a cascade gives a column ─────────────────────────────────

        public static IEnumerable<object[]> Cascades()
        {
            foreach (var kind in Kinds())
                foreach (var scenario in new[] { "zero length refused", "zero length allowed in a required column", "null refused", "null allowed" })
                    yield return new[] { kind[0], kind[1], scenario };
        }

        private const int DaoCascadeUpdates = 256, DaoCascadeNull = 8192;

        /// <summary>
        /// P, keyed by its text K, with rows 'a' and 'b'; C, whose K refers to it and cascades updates and
        /// cascades a delete to null, with two rows of 'a' and one of 'b'. Made by DAO, or by the engine.
        /// </summary>
        private string ParentAndChild(bool accdb, bool accessMade, bool childAllowsZeroLength, bool childRequired)
        {
            var path = NewDatabase(accdb, accessMade);
            if (accessMade)
                WithDao(path, db =>
                {
                    dynamic parent = db.CreateTableDef("P");
                    DaoAddField(parent, "K", DaoText, 20, allowZeroLength: true);
                    DaoAddField(parent, "T", DaoText, 20);
                    db.TableDefs.Append(parent);
                    db.Execute("CREATE UNIQUE INDEX PrimaryKey ON P (K) WITH PRIMARY");
                    dynamic child = db.CreateTableDef("C");
                    DaoAddField(child, "Id", DaoLong);
                    DaoAddField(child, "K", DaoText, 20, childRequired, childAllowsZeroLength);
                    db.TableDefs.Append(child);
                    db.Execute("CREATE UNIQUE INDEX PrimaryKey ON C (Id) WITH PRIMARY");
                    dynamic relation = db.CreateRelation("PC", "P", "C", DaoCascadeUpdates + DaoCascadeNull);
                    dynamic field = relation.CreateField("K");
                    field.ForeignName = "K";
                    relation.Fields.Append(field);
                    db.Relations.Append(relation);
                    return 0;
                });
            else
                using (var db = Database.Open(path))
                {
                    db.CreateTable("P", new[] { new ColumnDefinition("K", DataType.Text) { MaxLength = 20 }, new ColumnDefinition("T", DataType.Text) { MaxLength = 20 } }, "K");
                    db.CreateTable("C", new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32),
                        new ColumnDefinition("K", DataType.Text) { MaxLength = 20, AllowZeroLength = childAllowsZeroLength, IsRequired = childRequired },
                    }, "Id");
                    db.CreateRelationship(new RelationshipDefinition("P", "K", "C", "K") { HasReferentialIntegrity = true, CascadeUpdates = true, CascadeNullOnDelete = true });
                }
            Execute(path, "INSERT INTO P (K, T) VALUES ('a', 'one')", "INSERT INTO P (K, T) VALUES ('b', 'two')",
                "INSERT INTO C (Id, K) VALUES (1, 'a')", "INSERT INTO C (Id, K) VALUES (2, 'a')", "INSERT INTO C (Id, K) VALUES (3, 'b')");
            return path;
        }

        /// <summary>
        /// A cascade that would give a child's column what its properties refuse is refused whole by ACE - the
        /// parent is not changed either - and by the engine; one the column takes is made by both, with the same
        /// rows after. The same change is made by ACE in one copy of the file and by the engine in another.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cascades))]
        public void a_cascade_is_held_to_the_child_column_s_rules_by_ace_and_by_the_engine(bool accdb, bool accessMade, string scenario)
        {
            bool update = scenario.StartsWith("zero length");
            bool taken = scenario.Contains("allowed");
            var path = ParentAndChild(accdb, accessMade,
                childAllowsZeroLength: scenario != "zero length refused",
                childRequired: scenario == "zero length allowed in a required column" || scenario == "null refused");
            var twin = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            File.Copy(path, twin);

            bool ace = AceTakes(() => Execute(path, update ? "UPDATE P SET K = '' WHERE K = 'a'" : "DELETE FROM P WHERE K = 'a'"));
            bool engine;
            using (var db = Database.Open(twin))
                engine = EngineTakes(() =>
                {
                    if (update) db.GetTable("P").UpdateByPrimaryKey("a", new Row { ["K"] = "" });
                    else db.GetTable("P").DeleteRow("K", "a");
                });
            Assert.True(ace == taken, "ACE " + (ace ? "made" : "refused") + " the change: " + scenario);
            Assert.True(engine == taken, "the engine " + (engine ? "made" : "refused") + " the change: " + scenario);

            string child = !taken ? "a" : update ? "" : null;
            foreach (var file in new[] { path, twin, Compact(twin) })
            {
                var parents = AssertEngineReadsWhatAceReads(file, "P").Select(r => (string)r["K"]).OrderBy(k => k, StringComparer.Ordinal).ToList();
                Assert.Equal(!taken ? new[] { "a", "b" } : update ? new[] { "", "b" } : new[] { "b" }, parents);
                var children = AssertEngineReadsWhatAceReads(file, "C").OrderBy(r => (int)r["Id"]).Select(r => (string)r["K"]).ToList();
                Assert.Equal(new[] { child, child, "b" }, children);
            }
        }

        // ── A required column added to a table that has rows ─────────────────

        /// <summary>
        /// Access takes a required column on a table that has rows - by SQL and by DAO - and leaves the rows with no
        /// value in it; so does the engine. The rows are then updated in another column by both, and a new row
        /// without a value there is refused by both.
        /// </summary>
        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_required_column_is_added_to_a_table_with_rows_by_access_and_by_the_engine(bool accdb, bool accessMade)
        {
            var path = NewDatabase(accdb, accessMade);
            Execute(path, "CREATE TABLE T (Id LONG CONSTRAINT pk PRIMARY KEY, Name TEXT(20))",
                "INSERT INTO T (Id, Name) VALUES (1, 'one')", "INSERT INTO T (Id, Name) VALUES (2, 'two')");

            Execute(path, "ALTER TABLE T ADD COLUMN BySql LONG NOT NULL");
            WithDao(path, db =>
            {
                DaoAddField(db.TableDefs["T"], "ByDao", DaoText, 10, required: true);
                return 0;
            });
            using (var db = Database.Open(path))
                db.GetTable("T").AddColumn(new ColumnDefinition("ByEngine", DataType.Int32) { IsRequired = true });

            Assert.All(AssertEngineReadsWhatAceReads(path, "T"), row => Assert.Equal("//", row["BySql"] + "/" + row["ByDao"] + "/" + row["ByEngine"]));
            Assert.Equal(new[] { true, true, true }, WithDao(path, db => new[] { "BySql", "ByDao", "ByEngine" }.Select(name => (bool)db.TableDefs["T"].Fields[name].Required).ToArray()));

            AceUpdate(path, "T", "Id", 1, V("Name", "ace"));
            using (var db = Database.Open(path))
                db.GetTable("T").UpdateByPrimaryKey(2, new Row { ["Name"] = "engine" });

            foreach (var missing in new[] { "BySql", "ByDao", "ByEngine" })
            {
                var row = V("Id", 3, "Name", "new", "BySql", 1, "ByDao", "x", "ByEngine", 1);
                row.Remove(missing);
                Assert.False(AceTakes(() => AceInsert(path, "T", row)), "ACE took a row with no value in " + missing);
                using (var db = Database.Open(path))
                    Assert.False(EngineTakes(() => db.GetTable("T").Insert(EngineRow(row))), "the engine took a row with no value in " + missing);
            }
            AceInsert(path, "T", V("Id", 3, "Name", "new", "BySql", 1, "ByDao", "x", "ByEngine", 1));
            using (var db = Database.Open(path))
                db.GetTable("T").Insert(new Row { ["Id"] = 4, ["Name"] = "new", ["BySql"] = 1, ["ByDao"] = "x", ["ByEngine"] = 1 });
            Assert.Equal(4, AssertEngineReadsWhatAceReads(Compact(path), "T").Count);
        }

        // ── A database whose catalog the engine does not rewrite ─────────────

        /// <summary>
        /// A database DAO makes in the Swedish sort order keys its catalog's names in an order the engine writes no
        /// keys for, so the engine writes no property there. A column that is what was asked for with no property
        /// is still added - Access takes a column without Required and AllowZeroLength as not required and as one
        /// that takes a zero-length string, which ACE shows here by taking such rows - and a column only a property
        /// can describe is refused with the file as it was.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void a_column_is_added_without_properties_where_they_cannot_be_written_and_access_takes_it_as_asked(bool accdb)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + (accdb ? ".accdb" : ".mdb"));
            SwedishDatabase(path, accdb ? 128 : 64);
            Execute(path, "INSERT INTO People (Id, Name) VALUES (1, 'one')");

            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                var people = db.GetTable("People");
                Assert.Throws<NotSupportedException>(() => people.AddColumn(new ColumnDefinition("Req", DataType.Int32) { IsRequired = true }));
                Assert.Throws<NotSupportedException>(() => people.AddColumn(new ColumnDefinition("NoZero", DataType.Text) { AllowZeroLength = false }));
            }
            Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), "a refused column changed the file");

            using (var db = Database.Open(path))
            {
                var people = db.GetTable("People");
                people.AddColumn(new ColumnDefinition("ZzNumber", DataType.Int32));
                people.AddColumn(new ColumnDefinition("ZzText", DataType.Text) { MaxLength = 10 });
                people.Insert(new Row { ["Id"] = 2, ["Name"] = "two", ["ZzNumber"] = 7, ["ZzText"] = "" });
            }

            // ACE takes a row with no value in the new columns, and one with a zero-length string.
            AceInsert(path, "People", V("Id", 3, "Name", "three"));
            AceInsert(path, "People", V("Id", 4, "Name", "four", "ZzNumber", 9, "ZzText", ""));
            foreach (var file in new[] { path, Compact(path) })
            {
                var rows = AssertEngineReadsWhatAceReads(file, "People").OrderBy(r => (int)r["Id"]).ToList();
                Assert.Equal(new[] { "//", "7//", "//", "9//" }, rows.Select(r => r["ZzNumber"] + "/" + (r["ZzText"] == null ? "null" : (string)r["ZzText"]) + "/").Select(s => s.Replace("null", "")).ToArray());
                Assert.Equal(new object[] { null, "", null, "" }, rows.Select(r => r["ZzText"]).ToArray());
                Assert.Equal(new[] { false, false }, WithDao(file, db => new[] { "ZzNumber", "ZzText" }.Select(name => (bool)db.TableDefs["People"].Fields[name].Required).ToArray()));
            }
        }

        // Its own method, so nothing DAO handed out is still in a local when the engine is released.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SwedishDatabase(string path, int version)
        {
            dynamic engine = Dao.Engine;
            try { MakeSwedish(engine, path, version); }
            finally { Dao.Release(engine); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void MakeSwedish(dynamic engine, string path, int version)
        {
            dynamic db = engine.CreateDatabase(path, ";LANGID=0x041D;CP=1252;COUNTRY=0", version);
            try
            {
                dynamic table = db.CreateTableDef("People");
                DaoAddField(table, "Id", DaoLong);
                DaoAddField(table, "Name", DaoText, 30);
                db.TableDefs.Append(table);
            }
            finally { db.Close(); }
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_row_that_already_breaks_a_rule_is_updated_in_another_field_by_both(bool accdb, bool accessMade)
        {
            // Rows written before the rules were there, as the engine's earlier versions wrote rows into required
            // fields: R is made without the rules, given two rows that would break them, and then given the rules -
            // by DAO or by the engine, whichever made the table.
            var path = NewDatabase(accdb, accessMade);
            if (accessMade)
                Execute(path, "CREATE TABLE R (Id LONG CONSTRAINT pk PRIMARY KEY, OptLong LONG, ReqLong LONG, TextNoZero TEXT(20))");
            else
                using (var db = Database.Open(path))
                    db.CreateTable("R", new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("OptLong", DataType.Int32),
                        new ColumnDefinition("ReqLong", DataType.Int32), new ColumnDefinition("TextNoZero", DataType.Text) { MaxLength = 20 },
                    }, "Id");
            Execute(path, "INSERT INTO R (Id, OptLong, TextNoZero) VALUES (1, 1, '')", "INSERT INTO R (Id, OptLong, TextNoZero) VALUES (2, 1, '')");
            if (accessMade)
                WithDao(path, db =>
                {
                    db.TableDefs["R"].Fields["ReqLong"].Required = true;
                    db.TableDefs["R"].Fields["TextNoZero"].AllowZeroLength = false;
                    return 0;
                });
            else
                using (var db = Database.Open(path))
                {
                    var table = db.GetTable("R");
                    table.Columns.Single(c => c.Name == "ReqLong").Properties.Set("Required", true);
                    table.Columns.Single(c => c.Name == "TextNoZero").Properties.Set("AllowZeroLength", false);
                    table.Properties.Save();
                }

            var changes = new (string What, Values Change, bool Taken)[]
            {
                ("another field", V("OptLong", 5), true),
                ("the zero-length string it has", V("TextNoZero", ""), false),
                ("the null it has", V("ReqLong", null), false),
                ("values that keep the rules", V("ReqLong", 3, "TextNoZero", "now"), true),
            };
            foreach (var (what, change, taken) in changes)
            {
                bool ace = AceTakes(() => AceUpdate(path, "R", "Id", 1, change));
                bool engine;
                using (var db = Database.Open(path))
                    engine = EngineTakes(() => db.GetTable("R").UpdateByPrimaryKey(2, EngineRow(change)));
                Assert.True(ace == taken, "ACE " + (ace ? "took" : "refused") + " an update giving " + what);
                Assert.True(engine == taken, "the engine " + (engine ? "took" : "refused") + " an update giving " + what);
            }
            var rows = AssertEngineReadsWhatAceReads(path, "R");
            Assert.All(rows, row => Assert.Equal("5/3/now", row["OptLong"] + "/" + row["ReqLong"] + "/" + row["TextNoZero"]));
        }
    }
}
