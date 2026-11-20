using System;
using System.Collections.Generic;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Relationships on Large Number and Date/Time Extended columns (#38): the engine
    /// creates one of each beside DAO in a twin file, and ACE keeps both as it keeps its own - an orphan refused, a
    /// parent's new key and its deletion carried to its children - with keys at the ends of each type's range and
    /// keys one unit apart. Then the engine keeps them, and what it makes of each change is what ACE makes of it in
    /// the twin. Rows with an extended date are read through DAO: ACE's OLE DB provider hands out no such value.
    /// </summary>
    public partial class SchemaChangesInAccessTests
    {
        private const string Stamp = "2020-01-02 03:04:05.1234567", StampAfter = "2020-01-02 03:04:05.1234568";
        private const string FirstDay = "0001-01-01 00:00:00.0000000", LastTick = "9999-12-31 23:59:59.9999999";

        // ACE's UPDATE takes an extended date through a Date/Time, to the whole second, and stores it in a form of
        // its own - the time of day in seconds, not in 100 nanoseconds - and keys it by those bytes, as it keys every
        // value of the type (#51). So a parent ACE re-keyed has its children in that form,
        // and a child with the date in the form ACE's INSERT and the engine write has no parent: it is another key.
        // ACE's own WHERE through an index finds the INSERT's form alone, so a row in the other form is named here by
        // another column.
        private const string LeapInSeconds = "1600-02-29 00:00:01";

        private static DateTime Extended(string text)
        {
            return DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// NP (K Large Number, T) and DP (K Date/Time Extended, T), keyed by K, each with four rows; NC and DC
        /// (Id, K, T), keyed by Id, each with rows that refer to them and one that refers to none. ACE's or the
        /// engine's, in an .accdb.
        /// </summary>
        private string NewTypes(bool accessMade)
        {
            var path = NewDatabase(general: true, accessMade: accessMade);
            var numbers = new[] { "1", "9223372036854775807", "-9223372036854775808", "5000000000" };
            var dates = new[] { Stamp, LastTick, FirstDay, "1600-02-29 00:00:00.0000000" };
            if (accessMade)
            {
                var statements = new List<string>
                {
                    "CREATE TABLE NP (K BIGINT CONSTRAINT pk PRIMARY KEY, T TEXT(20))",
                    "CREATE TABLE NC (Id LONG CONSTRAINT pk PRIMARY KEY, K BIGINT, T TEXT(20))",
                    "CREATE TABLE DP (K DATETIME2 CONSTRAINT pk PRIMARY KEY, T TEXT(20))",
                    "CREATE TABLE DC (Id LONG CONSTRAINT pk PRIMARY KEY, K DATETIME2, T TEXT(20))",
                };
                for (var i = 0; i < 4; i++)
                {
                    statements.Add("INSERT INTO NP (K, T) VALUES (" + numbers[i] + ", 'n" + i + "')");
                    statements.Add("INSERT INTO DP (K, T) VALUES ('" + dates[i] + "', 'd" + i + "')");
                }
                for (var i = 0; i < 20; i++)
                {
                    statements.Add("INSERT INTO NC (Id, K, T) VALUES (" + i + ", " + (i % 5 == 4 ? "NULL" : numbers[i % 5]) + ", 'nc" + i + "')");
                    statements.Add("INSERT INTO DC (Id, K, T) VALUES (" + i + ", " + (i % 5 == 4 ? "NULL" : "'" + dates[i % 5] + "'") + ", 'dc" + i + "')");
                }
                Execute(path, statements.ToArray());
                return path;
            }

            using (var db = Database.Open(path))
            {
                var np = db.CreateTable("NP", new[] { new ColumnDefinition("K", DataType.Int64), Text("T", 20) }, primaryKey: "K");
                var nc = db.CreateTable("NC", new[] { Long("Id"), new ColumnDefinition("K", DataType.Int64), Text("T", 20) }, primaryKey: "Id");
                var dp = db.CreateTable("DP", new[] { new ColumnDefinition("K", DataType.DateTimeExtended), Text("T", 20) }, primaryKey: "K");
                var dc = db.CreateTable("DC", new[] { Long("Id"), new ColumnDefinition("K", DataType.DateTimeExtended), Text("T", 20) }, primaryKey: "Id");
                for (var i = 0; i < 4; i++)
                {
                    np.Insert(new Row { ["K"] = long.Parse(numbers[i]), ["T"] = "n" + i });
                    dp.Insert(new Row { ["K"] = Extended(dates[i]), ["T"] = "d" + i });
                }
                for (var i = 0; i < 20; i++)
                {
                    nc.Insert(new Row { ["Id"] = i, ["K"] = i % 5 == 4 ? (object)null : long.Parse(numbers[i % 5]), ["T"] = "nc" + i });
                    dc.Insert(new Row { ["Id"] = i, ["K"] = i % 5 == 4 ? (object)null : Extended(dates[i % 5]), ["T"] = "dc" + i });
                }
            }
            return path;
        }

        /// <summary>Every row of the tables as DAO reads them, an extended date to its last tick.</summary>
        private static List<string> DaoRead(string path, params string[] tables)
        {
            return tables.SelectMany(table => Written(DaoRows(path, "SELECT * FROM [" + table + "]")
                .Select(row =>
                {
                    var shown = new Values();
                    foreach (var pair in row) shown[pair.Key] = pair.Value is DateTime ? ToTheTick((DateTime)pair.Value) : pair.Value;
                    return shown;
                })).Select(row => table + ": " + row)).ToList();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void relationships_on_large_number_and_extended_date_columns_are_the_ones_dao_creates_and_both_keep_them(bool accessMade)
        {
            SkipUnlessAceHas(LargeNumber);
            SkipUnlessAceHas(DateTimeExtended);
            var path = NewTypes(accessMade);
            var twin = NewTypes(accessMade);

            DaoRelate(twin, "Numbers", "NP", "K", "NC", "K", UpdateCascade | DeleteCascade);
            DaoRelate(twin, "Dates", "DP", "K", "DC", "K", UpdateCascade | DeleteCascade);
            using (var db = Database.Open(path))
            {
                foreach (var name in new[] { "Numbers", "Dates" })
                    db.CreateRelationship(new RelationshipDefinition(name[0] + "P", "K", name[0] + "C", "K")
                    {
                        Name = name, HasReferentialIntegrity = true, CascadeUpdates = true, CascadeDeletes = true,
                    });
            }

            Same(DaoRelations(twin), DaoRelations(path));
            Same(RelationshipRows(twin), RelationshipRows(path));
            foreach (var table in new[] { "NP", "NC", "DP", "DC" })
            {
                Same(DaoIndexes(twin, table), DaoIndexes(path, table));
                Same(Slots(twin, table), Slots(path, table));
            }

            // ACE keeps them in the engine's file as in DAO's: a key one unit from a parent's is an orphan.
            foreach (var sql in new[]
            {
                "INSERT INTO NC (Id, K, T) VALUES (100, 9223372036854775806, 'orphan')",
                "INSERT INTO NC (Id, K, T) VALUES (101, 9223372036854775807, 'child of the most')",
                "INSERT INTO NC (Id, K, T) VALUES (102, NULL, 'child of none')",
                "UPDATE NC SET K = 4999999999 WHERE Id = 3",
                "UPDATE NP SET K = 6000000000 WHERE K = 5000000000",
                "DELETE FROM NP WHERE K = -9223372036854775808",
                "INSERT INTO DC (Id, K, T) VALUES (100, '" + StampAfter + "', 'orphan')",
                "INSERT INTO DC (Id, K, T) VALUES (101, '" + LastTick + "', 'child of the last')",
                "DELETE FROM DP WHERE K = '" + FirstDay + "'",
                // d3 re-keyed by ACE's UPDATE, in its second form, which ACE carries to d3's children; a child of
                // none given that key by UPDATE, in the same form; and a child given it by INSERT, in the other.
                "UPDATE DP SET K = '" + LeapInSeconds + "' WHERE T = 'd3'",
                "UPDATE DC SET K = '" + LeapInSeconds + "' WHERE Id = 4",
                "INSERT INTO DC (Id, K, T) VALUES (103, '" + LeapInSeconds + "', 'another form')",
            })
            {
                var inTwin = Ace(twin, sql);
                Assert.True(inTwin == Ace(path, sql), sql + ": ACE " + inTwin + " it in the file whose relationship DAO made, and not in the engine's");
                Same(DaoRead(twin, "NP", "NC", "DP", "DC"), DaoRead(path, "NP", "NC", "DP", "DC"));
            }
            Assert.Equal("refused: no parent", Ace(path, "INSERT INTO NC (Id, K, T) VALUES (103, 5000000000, 'parent re-keyed')"));
            Assert.Equal("refused: no parent", Ace(path, "INSERT INTO DC (Id, K, T) VALUES (103, '" + LeapInSeconds + "', 'another form')"));
            Assert.Equal(4, Count(path, "NC", "K = 6000000000"));
            Assert.Equal(0, Count(path, "NC", "K = -9223372036854775808"));
            Assert.Equal(4, Count(path, "DC", "K = '" + Stamp + "'"));
            Assert.Equal(5, Count(path, "DC", "K = '" + LastTick + "'"));
            Assert.Equal(0, Count(path, "DC", "K = '" + FirstDay + "'"));

            // The engine keeps them too: what it makes of a change to its file is what ACE makes of it in the twin.
            var changes = new (string Sql, Action<Database> Change)[]
            {
                ("INSERT INTO NC (Id, K, T) VALUES (110, 2, 'orphan')", db => db.GetTable("NC").Insert(new Row { ["Id"] = 110, ["K"] = 2L, ["T"] = "orphan" })),
                ("INSERT INTO NC (Id, K, T) VALUES (111, 1, 'child of 1')", db => db.GetTable("NC").Insert(new Row { ["Id"] = 111, ["K"] = 1L, ["T"] = "child of 1" })),
                ("UPDATE NP SET K = -1 WHERE K = 1", db => db.GetTable("NP").UpdateByPrimaryKey(1L, new Row { ["K"] = -1L })),
                ("DELETE FROM NP WHERE K = 9223372036854775807", db => db.GetTable("NP").DeleteRow("K", long.MaxValue)),
                ("INSERT INTO DC (Id, K, T) VALUES (110, '" + StampAfter + "', 'orphan')", db => db.GetTable("DC").Insert(new Row { ["Id"] = 110, ["K"] = Extended(StampAfter), ["T"] = "orphan" })),
                ("INSERT INTO DC (Id, K, T) VALUES (111, '" + Stamp + "', 'child')", db => db.GetTable("DC").Insert(new Row { ["Id"] = 111, ["K"] = Extended(Stamp), ["T"] = "child" })),
                ("DELETE FROM DP WHERE K = '" + LastTick + "'", db => db.GetTable("DP").DeleteRow("K", Extended(LastTick))),
                // d3's key is in ACE's second form: a child with its date in the engine's form, the first, has no
                // parent; a child in the second form keeps its parent when another of its columns changes; and d3's
                // deletion takes its children, found under the key of their bytes.
                ("INSERT INTO DC (Id, K, T) VALUES (112, '" + LeapInSeconds + "', 'orphan')",
                    db => db.GetTable("DC").Insert(new Row { ["Id"] = 112, ["K"] = Extended(LeapInSeconds + ".0000000"), ["T"] = "orphan" })),
                ("UPDATE DC SET T = 'renamed' WHERE Id = 3", db => db.GetTable("DC").UpdateByPrimaryKey(3, new Row { ["T"] = "renamed" })),
                ("DELETE FROM DP WHERE T = 'd3'", db => db.GetTable("DP").DeleteRow("T", "d3")),
            };
            foreach (var (sql, change) in changes)
            {
                var inTwin = Ace(twin, sql);
                var byEngine = Engine(path, change);
                Assert.True((inTwin == "made") == (byEngine == "made"), sql + ": ACE " + inTwin + " it, and the engine " + byEngine + " it");
                Same(DaoRead(twin, "NP", "NC", "DP", "DC"), DaoRead(path, "NP", "NC", "DP", "DC"));
            }
            Assert.Equal(5, Count(path, "NC", "K = -1"));
            Assert.Equal(5, Count(path, "DC", "K = '" + Stamp + "'"));
            Assert.Equal(0, Count(path, "DC", "K = '" + LastTick + "'"));

            // The engine gives a parent a key one tick on, and its children go with it: ACE finds them all by the
            // new key through the relationship's index, and none by the old.
            using (var db = Database.Open(path))
                db.GetTable("DP").UpdateByPrimaryKey(Extended(Stamp), new Row { ["K"] = Extended(StampAfter) });
            Assert.Equal(5, Count(path, "DC", "K = '" + StampAfter + "'"));
            Assert.Equal(0, Count(path, "DC", "K = '" + Stamp + "'"));
            Assert.Equal(1, Count(path, "DP", "K = '" + StampAfter + "'"));
            Assert.Equal("refused: no parent", Ace(path, "INSERT INTO DC (Id, K, T) VALUES (112, '" + Stamp + "', 'orphan')"));
            Assert.Equal("made", Ace(path, "INSERT INTO DC (Id, K, T) VALUES (113, '" + StampAfter + "', 'child')"));

            // ACE still answers through each relationship's index as a scan answers.
            foreach (var k in new[] { "-1", "6000000000", "9223372036854775807" })
                Assert.Equal(Count(path, "NC", "K + 0 = " + k), Count(path, "NC", "K = " + k));

            var compacted = Compacted(path);
            Same(DaoRelations(twin), DaoRelations(compacted));
            Same(DaoRead(path, "NP", "NC", "DP", "DC"), DaoRead(compacted, "NP", "NC", "DP", "DC"));
            Assert.Equal("refused: no parent", Ace(compacted, "INSERT INTO NC (Id, K, T) VALUES (120, 7, 'orphan')"));
            Assert.Equal("refused: no parent", Ace(compacted, "INSERT INTO DC (Id, K, T) VALUES (120, '" + LastTick + "', 'orphan')"));

            // An extended date ACE's UPDATE has set is in ACE's other form, its time of day in whole seconds: the
            // engine reads it as DAO does. (It read null.)
            Execute(path, "CREATE TABLE Seconds (Id LONG, D DATETIME2)",
                "INSERT INTO Seconds (Id, D) VALUES (1, '" + Stamp + "')", "INSERT INTO Seconds (Id, D) VALUES (2, '" + Stamp + "')",
                "UPDATE Seconds SET D = '" + StampAfter + "' WHERE Id = 2");
            var asDaoReads = DaoRead(path, "Seconds");
            Assert.Contains(asDaoReads, row => row.Contains("Id=2") && row.Contains("2020-01-02 03:04:05.0000000"));
            List<string> asTheEngineReads;
            using (var db = Database.Open(path))
                asTheEngineReads = Written(db.GetTable("Seconds").Rows.Select(row => V("Id", row["Id"], "D", row["D"] == null ? null : ToTheTick((DateTime)row["D"]))))
                    .Select(row => "Seconds: " + row).ToList();
            Same(asDaoReads, asTheEngineReads);
        }
    }
}
