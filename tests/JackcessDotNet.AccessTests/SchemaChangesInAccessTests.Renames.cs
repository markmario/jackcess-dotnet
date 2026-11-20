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
    /// What the engine renames - a column, an index, a table (#37) - beside the same thing
    /// renamed by DAO in a twin file. Java Jackcess renames none of them, so Access is the only referee: DAO lists
    /// the same fields, indexes and relationships in both files, the definitions hold the same index slots in the
    /// same order and MSysRelationships the same rows, a column's properties are read under its new name, ACE
    /// writes by the new names and keeps the relationships, and DAO compacts the file. A name DAO refuses, the
    /// engine refuses, leaving its file as it was.
    /// </summary>
    public partial class SchemaChangesInAccessTests
    {
        /// <summary>
        /// A listing in an order of its own. The rows of MSysRelationships are compared so after a rename: the
        /// engine writes a changed row where the table next has room, as it writes any updated row, and Access
        /// was seen to leave these where they were. The table has no order of its own to keep.
        /// </summary>
        private static List<string> InAnyOrder(List<string> listing)
        {
            return listing.OrderBy(line => line, StringComparer.Ordinal).ToList();
        }

        /// <summary>P, C and U with indexes on C's T and K, the relationships PC (cascading) and Loose (not enforced), and descriptions.</summary>
        private string Related(bool general, bool accessMade)
        {
            var path = ParentAndChild(general, accessMade);
            Execute(path, "CREATE INDEX ixT ON C (T)", "CREATE INDEX ixK ON C (K)");
            DaoRelate(path, "PC", "P", "K", "C", "K", UpdateCascade | DeleteCascade);
            DaoRelate(path, "Loose", "P", "T", "C", "T", DontEnforce);
            DaoDescribe(path, "C", null, "the children");
            DaoDescribe(path, "C", "T", "about T");
            DaoDescribe(path, "C", "K", "about K");
            return path;
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void columns_indexes_and_tables_the_engine_renames_are_named_as_the_ones_dao_renames(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = Related(general, accessMade);
            var twin = Related(general, accessMade);

            // ── Columns: one in a relationship without integrity, the foreign key, and the key referred to.
            Assert.Equal("made", DaoDoes(twin, db => db.TableDefs["C"].Fields["T"].Name = "Title"));
            Assert.Equal("made", DaoDoes(twin, db => db.TableDefs["C"].Fields["K"].Name = "ParentKey"));
            Assert.Equal("made", DaoDoes(twin, db => db.TableDefs["P"].Fields["K"].Name = "Key"));
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                c.RenameColumn("T", "Title");
                c.RenameColumn("K", "ParentKey");
                db.GetTable("P").RenameColumn("K", "Key");
                Assert.Equal(new[] { "Id", "ParentKey", "Title" }, c.Columns.Select(column => column.Name));
            }
            foreach (var table in new[] { "P", "C" })
            {
                Same(DaoFields(twin, table), DaoFields(path, table));
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }
            Assert.Equal(new[] { "Id:4", "ParentKey:4", "Title:10" }, DaoFields(path, "C"));
            Same(DaoRelations(twin), DaoRelations(path));
            Assert.Contains("PC: P -> C attributes " + (UpdateCascade | DeleteCascade) + " fields Key>ParentKey", DaoRelations(path));
            Assert.Contains("Loose: P -> C attributes " + DontEnforce + " fields T>Title", DaoRelations(path));
            Same(InAnyOrder(RelationshipRows(twin)), InAnyOrder(RelationshipRows(path)));

            // The properties Access keeps for a column are read under its new name.
            Assert.Equal("about T", DaoDescription(path, "C", "Title"));
            Assert.Equal("about K", DaoDescription(path, "C", "ParentKey"));
            Assert.Equal("the children", DaoDescription(path, "C", null));
            Assert.Equal(DaoDescription(twin, "C", "Title"), DaoDescription(path, "C", "Title"));

            // ── Indexes: to names that sort before and after the others, and the primary key.
            Assert.Equal("made", DaoDoes(twin, db => db.TableDefs["C"].Indexes["ixT"].Name = "aaa"));
            Assert.Equal("made", DaoDoes(twin, db => db.TableDefs["C"].Indexes["ixK"].Name = "zzz"));
            var key = accessMade ? "pk" : "PrimaryKey";
            Assert.Equal("made", DaoDoes(twin, db => db.TableDefs["C"].Indexes[key].Name = "mmm"));
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                c.RenameIndex("ixT", "aaa");
                c.RenameIndex("ixK", "zzz");
                c.RenameIndex(key, "mmm");
            }
            Same(DaoIndexes(twin, "C"), DaoIndexes(path, "C"));
            Same(Slots(twin, "C"), Slots(path, "C"));
            Assert.Equal(17, DaoSeek(path, "C", "aaa", "c17"));
            Assert.Equal(17, DaoSeek(path, "C", "mmm", 17));
            Assert.NotNull(DaoSeek(path, "C", "zzz", 4));

            // ── Tables: the one that refers and the one referred to.
            Assert.Equal("made", DaoDoes(twin, db => db.TableDefs["C"].Name = "Children"));
            Assert.Equal("made", DaoDoes(twin, db => db.TableDefs["P"].Name = "Parents"));
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                db.RenameTable("C", "Children");
                db.RenameTable("P", "Parents");
                Assert.Equal("Children", c.Name);
            }
            Same(DaoTables(twin), DaoTables(path));
            Assert.Equal(new[] { "Children", "Parents", "U" }, DaoTables(path));
            Same(DaoRelations(twin), DaoRelations(path));
            Assert.Contains("PC: Parents -> Children attributes " + (UpdateCascade | DeleteCascade) + " fields Key>ParentKey", DaoRelations(path));
            Same(InAnyOrder(RelationshipRows(twin)), InAnyOrder(RelationshipRows(path)));
            Same(CatalogRelationships(twin), CatalogRelationships(path));
            foreach (var table in new[] { "Parents", "Children" })
            {
                Same(DaoFields(twin, table), DaoFields(path, table));
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Rows(twin, table), Rows(path, table));
            }
            Assert.Equal("the children", DaoDescription(path, "Children", null));
            Assert.Equal("about T", DaoDescription(path, "Children", "Title"));

            // ── ACE writes by the new names and keeps the relationship, in the engine's file as in DAO's.
            foreach (var sql in new[]
            {
                "INSERT INTO Children (Id, ParentKey, Title) VALUES (900, 99, 'orphan')",
                "INSERT INTO Children (Id, ParentKey, Title) VALUES (901, 2, 'child of 2')",
                "UPDATE Parents SET [Key] = 50 WHERE [Key] = 5",
                "DELETE FROM Parents WHERE [Key] = 1",
                "INSERT INTO C (Id, K, T) VALUES (902, 2, 'old names')",
            })
            {
                var inTwin = Ace(twin, sql);
                Assert.True(inTwin == Ace(path, sql), sql + ": ACE " + inTwin + " it in the file DAO renamed in, and not in the engine's");
                Same(Rows(twin, "Parents", "Children"), Rows(path, "Parents", "Children"));
            }
            Assert.Equal(12, Count(path, "Children", "ParentKey = 50"));
            Assert.Equal(0, Count(path, "Children", "ParentKey = 1"));
            foreach (var k in new[] { 2, 3, 50 })
                Assert.Equal(Count(path, "Children", "ParentKey + 0 = " + k), Count(path, "Children", "ParentKey = " + k));
            Assert.Equal(901, DaoSeek(path, "Children", "aaa", "child of 2"));

            // And the engine keeps it too, by the new names.
            using (var db = Database.Open(path))
            {
                var children = db.GetTable("Children");
                Assert.Throws<ConstraintViolationException>(() => children.Insert(new Row { ["Id"] = 910, ["ParentKey"] = 97, ["Title"] = "orphan" }));
                children.Insert(new Row { ["Id"] = 911, ["ParentKey"] = 3, ["Title"] = "child of 3" });
                db.GetTable("Parents").UpdateByPrimaryKey(4, new Row { ["Key"] = 40 });
                Assert.Equal(911, children.CreateIndexCursor("aaa").FindRow("Title", "child of 3")["Id"]);
            }
            Assert.Equal(911, DaoSeek(path, "Children", "aaa", "child of 3"));
            Assert.Equal(Count(path, "Children", "ParentKey + 0 = 40"), Count(path, "Children", "ParentKey = 40"));
            Assert.True(Count(path, "Children", "ParentKey = 40") >= 12);
            AssertEngineReadsWhatAceReads(path, "Children");
            AssertEngineReadsWhatAceReads(path, "Parents");

            var compacted = Compacted(path);
            Assert.Equal(new[] { "Children", "Parents", "U" }, DaoTables(compacted));
            Same(DaoRelations(path), DaoRelations(compacted));
            Same(DaoFields(path, "Children"), DaoFields(compacted, "Children"));
            Same(DaoIndexes(path, "Children"), DaoIndexes(compacted, "Children"));
            Same(Rows(path, "Parents", "Children"), Rows(compacted, "Parents", "Children"));
            Assert.Equal("about T", DaoDescription(compacted, "Children", "Title"));
            Assert.Equal("refused: no parent", Ace(compacted, "INSERT INTO Children (Id, ParentKey, Title) VALUES (920, 96, 'orphan')"));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void a_new_name_dao_refuses_is_refused_by_the_engine_with_the_file_as_it_was(bool general, bool accessMade)
        {
            if (general) SkipUnlessAceOpensAccess2010();
            var path = Related(general, accessMade);
            var twin = Related(general, accessMade);
            var key = accessMade ? "pk" : "PrimaryKey";

            // A name that is taken, of each kind; and the names Access allows nothing.
            Assert.StartsWith("refused", DaoDoes(twin, db => db.TableDefs["C"].Fields["T"].Name = "K"));
            Assert.StartsWith("refused", DaoDoes(twin, db => db.TableDefs["C"].Indexes["ixT"].Name = "ixK"));
            Assert.StartsWith("refused", DaoDoes(twin, db => db.TableDefs["C"].Name = "P"));
            var before = File.ReadAllBytes(path);
            using (var db = Database.Open(path))
            {
                var c = db.GetTable("C");
                Assert.Throws<ArgumentException>(() => c.RenameColumn("T", "K"));
                Assert.Throws<ArgumentException>(() => c.RenameIndex("ixT", "ixK"));
                Assert.Throws<ArgumentException>(() => db.RenameTable("C", "P"));
                Assert.Throws<ArgumentException>(() => c.RenameColumn("Nothing", "Something"));
                Assert.Throws<ArgumentException>(() => c.RenameIndex("Nothing", "Something"));
                Assert.Throws<ArgumentException>(() => db.RenameTable("Nothing", "Something"));
                Assert.Throws<InvalidOperationException>(() => c.RenameIndex("PC", "Other"));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            foreach (var name in new[] { "a.b", "wow!", "[bracketed]", "gra`ve", " leading", "tab\tbed", new string('n', 65) })
            {
                Assert.Contains("not a valid name", DaoDoes(twin, db => db.TableDefs["C"].Fields["T"].Name = name));
                Assert.Contains("not a valid name", DaoDoes(twin, db => db.TableDefs["C"].Indexes["ixT"].Name = name));
                Assert.Contains("not a valid name", DaoDoes(twin, db => db.TableDefs["C"].Name = name));
                using (var db = Database.Open(path))
                {
                    var c = db.GetTable("C");
                    Assert.Throws<ArgumentException>(() => c.RenameColumn("T", name));
                    Assert.Throws<ArgumentException>(() => c.RenameIndex("ixT", name));
                    Assert.Throws<ArgumentException>(() => db.RenameTable("C", name));
                }
                Assert.Equal(before, File.ReadAllBytes(path));
            }

            // What DAO takes, the engine takes: another case, a space inside or at the end, 64 characters.
            var names = new[] { "t", new string('n', 64), "trailing ", "quo\"te", "plain name" };
            var column = "T";
            var index = "ixT";
            var table = "C";
            foreach (var name in names)
            {
                string was = column, wasIndex = index, wasTable = table;
                Assert.Equal("made", DaoDoes(twin, db => db.TableDefs[wasTable].Fields[was].Name = name));
                Assert.Equal("made", DaoDoes(twin, db => db.TableDefs[wasTable].Indexes[wasIndex].Name = name));
                Assert.Equal("made", DaoDoes(twin, db => db.TableDefs[wasTable].Name = name));
                using (var db = Database.Open(path))
                {
                    db.GetTable(table).RenameColumn(column, name);
                    db.GetTable(table).RenameIndex(index, name);
                    db.RenameTable(table, name);
                }
                column = index = table = name;
                Same(DaoTables(twin), DaoTables(path));
                Same(DaoFields(twin, table), DaoFields(path, table));
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(DaoRelations(twin), DaoRelations(path));
                Same(InAnyOrder(RelationshipRows(twin)), InAnyOrder(RelationshipRows(path)));
                Func<string, string> seek = file =>
                {
                    try { return "found " + DaoSeek(file, table, index, "c17"); }
                    catch (Exception e) { return "failed: " + e.GetBaseException().Message; }
                };
                Assert.True(seek(twin) == seek(path), "'" + name + "': DAO's seek through the renamed index " + seek(twin) + " in DAO's file and " + seek(path) + " in the engine's");
            }
            Assert.Equal(17, DaoSeek(path, table, index, "c17"));
            Assert.NotNull(DaoSeek(path, table, key, 17));
            Same(DaoRelations(twin), DaoRelations(Compacted(path)));
        }
    }
}
