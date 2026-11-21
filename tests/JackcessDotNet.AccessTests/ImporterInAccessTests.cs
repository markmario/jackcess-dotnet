using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// The managed engine's importer, with Access as the referee (#44): a table an import
    /// makes, and rows it adds to a table Access made, hold every value of the source exactly. DAO and ACE read every
    /// value back as the source had it - a decimal to its last digit, a long binary whole, a signed byte, an unsigned
    /// long and NaN as they were - DAO reports the column a type became and Access holds a required column to its
    /// rule; ACE finds every row through the key the import carried over, and reads it all again after DAO compacts
    /// the file. A refused import leaves a file Access made byte for byte as it was.
    /// </summary>
    public class ImporterInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();

        public void Dispose() { _tmp.Dispose(); }

        /// <summary>A type of every kind the importer maps.</summary>
        public sealed class Everything
        {
            [Key] public int Id { get; set; }
            public sbyte Signed { get; set; }
            public ushort Unsigned { get; set; }
            public uint Big { get; set; }
            public long Long { get; set; }
            public ulong Huge { get; set; }
            public float F { get; set; }
            public double D { get; set; }
            public decimal Price { get; set; }
            public decimal? Wide { get; set; }
            [MaxLength(10)] public byte[] Small { get; set; }
            public byte[] Blob { get; set; }
            public string Name { get; set; }
            public string Story { get; set; }
            [Required] public string Code { get; set; }
            public bool Flag { get; set; }
            public DateTime When { get; set; }
            public Guid Ref { get; set; }
            public int? Optional { get; set; }
        }

        private static string Text(int length, int seed)
        {
            var text = new char[length];
            for (var i = 0; i < length; i++)
                text[i] = seed % 2 == 0 && i % 5 == 0 ? (char)(0x3A9 + (i + seed) % 20) : (char)('a' + (i * 7 + seed) % 26);
            return new string(text);
        }

        private static byte[] Bytes(int length, int seed)
        {
            return Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();
        }

        private static Everything[] Objects()
        {
            return new[]
            {
                new Everything
                {
                    Id = 1, Signed = sbyte.MinValue, Unsigned = 0, Big = 0, Long = long.MinValue, Huge = 0, F = float.MinValue, D = double.MinValue,
                    Price = -0.00001m, Wide = -1234567890123456789012345678m, Small = new byte[0], Blob = new byte[0],
                    Name = "", Story = "", Code = "a", Flag = false, When = new DateTime(100, 1, 1), Ref = Guid.Empty, Optional = null,
                },
                new Everything
                {
                    Id = 2, Signed = sbyte.MaxValue, Unsigned = ushort.MaxValue, Big = uint.MaxValue, Long = long.MaxValue, Huge = ulong.MaxValue,
                    F = float.MaxValue, D = double.MaxValue, Price = 12345678901234.56789m, Wide = 1234567890123456789012345678m,
                    Small = Bytes(10, 2), Blob = Bytes(100000, 2), Name = Text(255, 2), Story = Text(70000, 2), Code = "b",
                    Flag = true, When = new DateTime(9999, 12, 31, 23, 59, 59, 999), Ref = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
                    Optional = int.MinValue,
                },
                new Everything
                {
                    Id = 3, Signed = -1, Unsigned = 32768, Big = 2147483648, Long = 5000000000, Huge = 9223372036854775808, F = float.NaN,
                    D = double.NegativeInfinity, Price = 0.1m, Wide = null, Small = null, Blob = null, Name = null, Story = null,
                    Code = Text(300, 3), Flag = true, When = new DateTime(2026, 11, 19, 7, 8, 9, 123), Ref = Guid.NewGuid(), Optional = 42,
                },
                new Everything
                {
                    Id = 4, Signed = 0, Unsigned = 1, Big = 1, Long = -1, Huge = 1, F = -0.0f, D = double.Epsilon, Price = 1.50m, Wide = 0m,
                    Small = Bytes(1, 4), Blob = Bytes(3000, 4), Name = "name", Story = Text(65, 4), Code = "d", Flag = false,
                    When = new DateTime(1899, 12, 30), Ref = Guid.NewGuid(), Optional = 0,
                },
            };
        }

        /// <summary>
        /// What the source has for a row, in the form every reader's is compared in: in these files, none of Access 2016's
        /// format, a uint and a long are Decimals, there being no Large Number.
        /// </summary>
        private static Values Source(Everything o)
        {
            return V("Id", o.Id, "Signed", (short)o.Signed, "Unsigned", (int)o.Unsigned,
                     "Big", (decimal)o.Big, "Long", (decimal)o.Long, "Huge", (decimal)o.Huge, "F", o.F, "D", o.D,
                     "Price", o.Price, "Wide", o.Wide, "Small", o.Small, "Blob", o.Blob, "Name", o.Name, "Story", o.Story,
                     "Code", o.Code, "Flag", o.Flag, "When", o.When, "Ref", o.Ref, "Optional", o.Optional);
        }

        public static IEnumerable<object[]> Files()
        {
            yield return new object[] { 0, ".mdb" };        // the engine's own blank Access 2000 file
            yield return new object[] { 64, ".mdb" };       // DAO's Access 2000 file
            yield return new object[] { 128, ".accdb" };    // Access 2007
            yield return new object[] { 256, ".accdb" };    // Access 2010 and later
        }

        private string NewFile(int daoVersion, string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            if (daoVersion == 0) using (Database.Create(path, FileFormat.Access2000)) { }
            else DaoCreate(path, daoVersion);
            return path;
        }

        private class Sized
        {
            public int Id { get; set; }
            public byte[] Unsized { get; set; }
            [MaxLength(64)] public byte[] Of64 { get; set; }
            [MaxLength(255)] public byte[] Of255 { get; set; }
            [MaxLength(300)] public byte[] Of300 { get; set; }
        }

        /// <summary>
        /// A byte[] the source gives no length - a DataTable's, whose DataColumn takes none, or a property without
        /// [MaxLength] - is an OLE Object, and ACE reads every value of it whole: 51, 64 and 300 bytes. One given a length
        /// of 255 bytes or fewer is a Binary of it, and one given more an OLE Object. It was Binary(50), which ACE could not
        /// SELECT once a value overran it.
        /// </summary>
        [Theory]
        [MemberData(nameof(Files))]
        public void access_reads_every_byte_of_an_imported_byte_array_whatever_its_length(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension);
            var source = new DataTable("Blobs");
            source.Columns.Add("Id", typeof(int));
            source.Columns.Add("Data", typeof(byte[]));
            source.Rows.Add(1, Bytes(64, 1));
            source.Rows.Add(2, Bytes(300, 2));
            source.Rows.Add(3, Bytes(51, 3));
            var objects = new[]
            {
                new Sized { Id = 1, Unsized = Bytes(51, 4), Of64 = Bytes(64, 5), Of255 = Bytes(255, 6), Of300 = Bytes(300, 7) },
                new Sized { Id = 2, Unsized = Bytes(64, 8), Of64 = Bytes(10, 9), Of255 = Bytes(1, 10), Of300 = Bytes(12, 11) },
                new Sized { Id = 3, Unsized = Bytes(300, 12), Of64 = Bytes(64, 13), Of255 = Bytes(255, 14), Of300 = Bytes(299, 15) },
            };
            using (var db = Database.Open(path))
            {
                db.ImportTable(source);
                db.ImportTable(objects, new ImportOptions { TableName = "Objects" });
            }

            Assert.Equal("128 OLE Object", AceColumns(path, "Blobs")["Data"].Type);
            var columns = AceColumns(path, "Objects");
            Assert.Equal("128 OLE Object", columns["Unsized"].Type);
            Assert.Equal("128 Binary(64)", columns["Of64"].Type);
            Assert.Equal("128 Binary(255)", columns["Of255"].Type);
            Assert.Equal("128 OLE Object", columns["Of300"].Type);

            AssertSame(Written(new[] { V("Id", 1, "Data", Bytes(64, 1)), V("Id", 2, "Data", Bytes(300, 2)), V("Id", 3, "Data", Bytes(51, 3)) }),
                       Written(AceRows(path, "SELECT Id, Data FROM Blobs ORDER BY Id")), "Blobs as ACE reads it");
            AssertSame(Written(objects.Select(o => V("Id", o.Id, "Unsized", o.Unsized, "Of64", o.Of64, "Of255", o.Of255, "Of300", o.Of300))),
                       Written(AceRows(path, "SELECT Id, Unsized, Of64, Of255, Of300 FROM Objects ORDER BY Id")), "Objects as ACE reads it");
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void access_reads_every_value_an_import_made_a_table_for_as_the_source_had_it(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension);
            byte version = File.ReadAllBytes(path)[0x14];
            var objects = Objects();
            using (var db = Database.Open(path))
                db.ImportTable(objects);

            // The file's format is as it was: no Large Number raised it to Access 2016's.
            Assert.Equal(version, File.ReadAllBytes(path)[0x14]);

            // Every value as the source had it, for ACE and for DAO.
            var expected = Written(objects.Select(Source));
            AssertSame(expected, Written(AceTable(path, "Everything")), "Everything as ACE reads it");
            AssertSame(Written(objects.Select(o => AsDaoHandsOut(Source(o)))), Written(DaoValues(DaoRows(path, "Everything"))), "Everything as DAO reads it");
            Assert.Equal(1, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM Everything WHERE Small IS NOT NULL AND LenB(Small) = 0 AND LenB(Blob) = 0")));
            foreach (var o in objects)
                AssertSame(Written(new[] { Source(o) }), Written(AceRows(path, "SELECT * FROM Everything WHERE Id = " + o.Id)),
                           "the row ACE finds through the primary key for Id " + o.Id);

            // The decimals to their last digit, at the scale the column has.
            var prices = AceRows(path, "SELECT Price FROM Everything ORDER BY Id").Select(r => ((decimal)r["Price"]).ToString(CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(new[] { "-0.00001", "12345678901234.56789", "0.10000", "1.50000" }, prices);

            // The columns as Access describes them.
            var columns = AceColumns(path, "Everything");
            Assert.Equal("2 Integer", columns["Signed"].Type);
            Assert.Equal("3 Long Integer", columns["Unsigned"].Type);
            Assert.Equal("131 Decimal(10,0)", columns["Big"].Type);
            Assert.Equal("131 Decimal(19,0)", columns["Long"].Type);
            Assert.Equal("131 Decimal(20,0)", columns["Huge"].Type);
            Assert.Equal("131 Decimal(19,5)", columns["Price"].Type);
            Assert.Equal("131 Decimal(28,0)", columns["Wide"].Type);
            Assert.Equal("128 Binary(10)", columns["Small"].Type);
            Assert.Equal("128 OLE Object", columns["Blob"].Type);
            Assert.Equal("130 Text(255)", columns["Name"].Type);
            Assert.Equal("130 Memo", columns["Story"].Type);
            Assert.Equal("130 Memo", columns["Code"].Type);   // a value of 300 characters

            // Required, as DAO reads the property, and as Access holds a row to it.
            var required = DaoRequired(path, "Everything");
            Assert.True(required["Code"]);
            Assert.True(required["Signed"]);
            Assert.True(required["Price"]);
            Assert.False(required["Wide"]);
            Assert.False(required["Name"]);
            Assert.False(required["Optional"]);
            Assert.False(required["Flag"]);
            var refused = Assert.Throws<OleDbException>(() => Execute(path,
                "INSERT INTO Everything (Id, Signed, Unsigned, Big, [Long], Huge, F, D, Price, Flag, [When], Ref) VALUES (9, 0, 0, 0, 0, 0, 0, 0, 0, 0, #2026-01-01#, {guid {0f8fad5b-d9cb-469f-a165-70867728950e}})"));
            Assert.Contains("Code", refused.Message);

            var compacted = Compact(path);
            try { AssertSame(expected, Written(AceTable(compacted, "Everything")), "Everything after DAO compacted the file"); }
            finally { File.Delete(compacted); }
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void access_finds_rows_through_a_key_of_several_columns_an_import_carried_over(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension);
            var lines = new DataTable("Lines");
            lines.Columns.Add("OrderId", typeof(int));
            lines.Columns.Add("Line", typeof(short));
            lines.Columns.Add(new DataColumn("Item", typeof(string)) { AllowDBNull = false, MaxLength = 20 });
            lines.PrimaryKey = new[] { lines.Columns["OrderId"], lines.Columns["Line"] };
            for (var order = 1; order <= 30; order++)
                for (short line = 1; line <= 3; line++)
                    lines.Rows.Add(order, line, "item " + order + "/" + line);
            using (var db = Database.Open(path))
                db.ImportTable(lines);

            // The key is the DataTable's, in its order, and Access finds every row through it.
            var key = DaoRows(path, "Lines", index: "PrimaryKey").Select(r => (int)r["OrderId"] * 10 + (short)r["Line"]).ToList();
            Assert.Equal(key.OrderBy(k => k), key);
            Assert.Equal(90, key.Count);
            foreach (var order in new[] { 1, 17, 30 })
                Assert.Equal("item " + order + "/2", AceRows(path, "SELECT Item FROM Lines WHERE OrderId = " + order + " AND Line = 2").Single()["Item"]);
            Assert.True(DaoRequired(path, "Lines")["Item"]);
            Assert.Throws<OleDbException>(() => Execute(path, "INSERT INTO Lines (OrderId, Line, Item) VALUES (1, 2, 'again')"));
            Assert.Throws<OleDbException>(() => Execute(path, "INSERT INTO Lines (OrderId, Line) VALUES (99, 1)"));
        }

        // A table Access made, of every type an append converts to.
        private const string Made =
            "CREATE TABLE Made (Id LONG CONSTRAINT pk PRIMARY KEY, B BYTE, I SHORT, L LONG, S SINGLE, D DOUBLE, C CURRENCY, N DECIMAL(10,2), " +
            "Y YESNO, X TEXT(5), Bin BINARY(4), M MEMO, O LONGBINARY, W DATETIME, G GUID)";

        [Theory]
        [MemberData(nameof(Files))]
        public void access_reads_rows_an_import_added_to_a_table_access_made_as_the_column_holds_them(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension);
            Execute(path, Made);
            AceInsert(path, "Made", V("Id", 1, "Y", true, "X", "ace"));

            var source = new DataTable("Made");
            foreach (var column in new[] { ("Id", typeof(long)), ("B", typeof(int)), ("I", typeof(double)), ("L", typeof(string)),
                                           ("S", typeof(decimal)), ("D", typeof(float)), ("C", typeof(double)), ("N", typeof(string)),
                                           ("Y", typeof(string)), ("X", typeof(int)), ("Bin", typeof(byte[])), ("M", typeof(string)),
                                           ("O", typeof(byte[])), ("W", typeof(DateTimeOffset)), ("G", typeof(string)) })
                source.Columns.Add(column.Item1, column.Item2);
            var guid = Guid.NewGuid();
            var when = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(11));
            source.Rows.Add(2L, 255, -32768.0, "-2147483648", 0.1m, 0.1f, 0.1, "12345678.90", "True", 42, Bytes(4, 7), Text(5000, 7), Bytes(50000, 7), when, guid.ToString("B"));
            source.Rows.Add(3L, 0, 7.0, "7", 2.5m, 1.5f, 9e14, "-0.01", "False", -1, Bytes(4, 8), "", new byte[0], when.AddDays(1), guid.ToString("D"));
            using (var db = Database.Open(path))
                db.ImportTable(source, new ImportOptions { AppendIfExists = true });

            var expected = new[]
            {
                V("Id", 1, "B", null, "I", null, "L", null, "S", null, "D", null, "C", null, "N", null, "Y", true, "X", "ace", "Bin", null, "M", null, "O", null, "W", null, "G", null),
                V("Id", 2, "B", (byte)255, "I", (short)-32768, "L", int.MinValue, "S", 0.1f, "D", (double)0.1f, "C", 0.1m, "N", 12345678.90m,
                  "Y", true, "X", "42", "Bin", Bytes(4, 7), "M", Text(5000, 7), "O", Bytes(50000, 7), "W", when.UtcDateTime, "G", guid),
                V("Id", 3, "B", (byte)0, "I", (short)7, "L", 7, "S", 2.5f, "D", 1.5, "C", 900000000000000m, "N", -0.01m,
                  "Y", false, "X", "-1", "Bin", Bytes(4, 8), "M", "", "O", new byte[0], "W", when.AddDays(1).UtcDateTime, "G", guid),
            };
            AssertSame(Written(expected), Written(AceTable(path, "Made")), "Made as ACE reads it");
            AssertSame(Written(expected.Select(AsDaoHandsOut)), Written(DaoValues(DaoRows(path, "Made"))), "Made as DAO reads it");
            Assert.Equal(1, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM Made WHERE O IS NOT NULL AND LenB(O) = 0")));
            AssertSame(Written(expected.Skip(1)), Written(AceRows(path, "SELECT * FROM Made WHERE Id IN (2, 3)")), "the rows ACE finds through the key");

            var compacted = Compact(path);
            try { AssertSame(Written(expected), Written(AceTable(compacted, "Made")), "Made after DAO compacted the file"); }
            finally { File.Delete(compacted); }
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void a_refused_import_leaves_a_file_access_made_as_it_was(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension);
            Execute(path, Made);
            AceInsert(path, "Made", V("Id", 1, "Y", true, "X", "ace"));
            byte[] before = File.ReadAllBytes(path);

            // A value the column does not hold exactly, in the last row: nothing of the rows before it is added.
            var notHeld = new DataTable("Made");
            notHeld.Columns.Add("Id", typeof(int));
            notHeld.Columns.Add("C", typeof(decimal));
            notHeld.Rows.Add(2, 1.25m);
            notHeld.Rows.Add(3, 1.23456m);
            // A row the table refuses, after rows it takes: a duplicate in its key.
            var duplicate = new DataTable("Made");
            duplicate.Columns.Add("Id", typeof(int));
            duplicate.Rows.Add(5);
            duplicate.Rows.Add(1);
            // A table to make, whose last row its key refuses.
            var fresh = new DataTable("Fresh");
            fresh.Columns.Add("Id", typeof(int));
            fresh.Rows.Add(1);
            fresh.Rows.Add(1);
            // Two columns whose names differ only in case.
            var twoOfOne = new DataTable("Twice") { CaseSensitive = true };
            twoOfOne.Columns.Add("Id", typeof(int));
            twoOfOne.Columns.Add("Name", typeof(string));
            twoOfOne.Columns.Add("NAME", typeof(string));
            twoOfOne.Rows.Add(1, "upper", "lower");

            using (var db = Database.Open(path))
            {
                Assert.Throws<ArgumentException>(() => db.ImportTable(notHeld, new ImportOptions { AppendIfExists = true }));
                Assert.Throws<ConstraintViolationException>(() => db.ImportTable(duplicate, new ImportOptions { AppendIfExists = true }));
                Assert.Throws<ConstraintViolationException>(() => db.ImportTable(fresh, new ImportOptions { PrimaryKey = new[] { "Id" } }));
                Assert.Throws<ArgumentException>(() => db.ImportTable(twoOfOne));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            // Access opens it as it was, and goes on.
            Assert.Single(AceTable(path, "Made"));
            AceInsert(path, "Made", V("Id", 2, "Y", false));
            Assert.Equal(2, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM Made")));
        }

        // An empty byte array the engine inserts, and one an import adds or makes a table with, is a value of no bytes,
        // as ACE's own INSERT writes one: ACE reads it as such - IS NULL false, LenB 0 - and so does the engine; DAO's
        // Field.Value hands it out as Null, and DAO writes an empty array as Null.
        [Theory]
        [MemberData(nameof(Files))]
        public void an_empty_byte_array_inserted_or_imported_is_no_bytes_to_ace_and_the_engine_and_null_to_dao(int daoVersion, string extension)
        {
            var path = NewFile(daoVersion, extension);
            var appended = new DataTable("Bytes");
            appended.Columns.Add("Id", typeof(int));
            appended.Columns.Add("O", typeof(byte[]));
            appended.Columns.Add("Bin", typeof(byte[]));
            appended.Rows.Add(2, new byte[0], new byte[0]);
            var made = new[] { new EmptyBytes { Id = 3, O = new byte[0], Bin = new byte[0] } };
            using (var db = Database.Open(path))
            {
                db.CreateTable("Bytes", new[]
                {
                    new ColumnDefinition("Id", JackcessDotNet.DataType.Int32), new ColumnDefinition("O", JackcessDotNet.DataType.OleObject),
                    new ColumnDefinition("Bin", JackcessDotNet.DataType.Binary) { MaxLength = 10 },
                }, "Id").Insert(new Row { ["Id"] = 1, ["O"] = new byte[0], ["Bin"] = new byte[0] });
                db.ImportTable(appended, new ImportOptions { AppendIfExists = true });
                db.ImportTable(made, new ImportOptions { TableName = "Made" });
            }
            var madeColumns = AceColumns(path, "Made");
            Assert.Equal("128 OLE Object", madeColumns["O"].Type);
            Assert.Equal("128 Binary(10)", madeColumns["Bin"].Type);

            foreach (var (table, id) in new[] { ("Bytes", 1), ("Bytes", 2), ("Made", 3) })
            {
                var ace = AceRows(path, "SELECT O, Bin, O IS NULL AS ONull, LenB(O) AS OLength, Bin IS NULL AS BinNull, LenB(Bin) AS BinLength FROM " + table + " WHERE Id = " + id).Single();
                Assert.Equal(new byte[0], ace["O"]);
                Assert.Equal(new byte[0], ace["Bin"]);
                Assert.Equal(0, Convert.ToInt32(ace["ONull"]));
                Assert.Equal(0, Convert.ToInt32(ace["OLength"]));
                Assert.Equal(0, Convert.ToInt32(ace["BinNull"]));
                Assert.Equal(0, Convert.ToInt32(ace["BinLength"]));
                var dao = DaoRows(path, table).Single(r => Convert.ToInt32(r["Id"]) == id);
                Assert.Null(dao["O"]);
                Assert.Null(dao["Bin"]);
            }
            using (var db = Database.Open(path))
                foreach (var row in db.GetTable("Bytes").Rows.Concat(db.GetTable("Made").Rows))
                {
                    Assert.Equal(new byte[0], row["O"]);
                    Assert.Equal(new byte[0], row["Bin"]);
                }
        }

        public sealed class EmptyBytes
        {
            [Key] public int Id { get; set; }
            public byte[] O { get; set; }
            [MaxLength(10)] public byte[] Bin { get; set; }
        }

        public sealed class Wide
        {
            [Key] public int Id { get; set; }
            public uint U { get; set; }
            public long L { get; set; }
        }

        // In a file of Access 2016's format, which has the Large Number, a uint and a long are Large Numbers - keys among
        // them - and ACE reads them back whole.
        [Fact]
        public void access_reads_a_uint_and_a_long_an_import_made_large_numbers_in_a_file_of_access_2016s_format()
        {
            SkipUnlessAceHas(LargeNumber);
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + ".accdb");
            using (Database.Create(path, FileFormat.Access2016)) { }
            byte version = File.ReadAllBytes(path)[0x14];
            using (var db = Database.Open(path))
                db.ImportTable(new[] { new Wide { Id = 1, U = uint.MaxValue, L = long.MinValue }, new Wide { Id = 2, U = 0, L = long.MaxValue } });

            Assert.Equal(version, File.ReadAllBytes(path)[0x14]);
            var columns = AceColumns(path, "Wide");
            Assert.Equal("20 Large Number", columns["U"].Type);
            Assert.Equal("20 Large Number", columns["L"].Type);
            AssertSame(Written(new[] { V("Id", 1, "U", (long)uint.MaxValue, "L", long.MinValue), V("Id", 2, "U", 0L, "L", long.MaxValue) }),
                       Written(AceTable(path, "Wide")), "Wide as ACE reads it");
        }

        /// <summary>
        /// A row as DAO's Field.Value hands it out: a zero-length binary value as Null - for one ACE wrote as for one the
        /// engine wrote; SQL's IS NULL and LenB say the value is there and empty.
        /// </summary>
        private static Values AsDaoHandsOut(Values row)
        {
            var copy = new Values();
            foreach (var pair in row)
                copy[pair.Key] = pair.Value is byte[] bytes && bytes.Length == 0 ? null : pair.Value;
            return copy;
        }

        /// <summary>DAO's values as the other readers give them: DAO hands out a GUID as the text "{guid {...}}".</summary>
        private static List<Values> DaoValues(List<Values> rows)
        {
            foreach (var row in rows)
                foreach (var name in row.Keys.ToList())
                    if (row[name] is string text && text.StartsWith("{guid {", StringComparison.Ordinal))
                        row[name] = new Guid(text.Substring(6, text.Length - 7));
            return rows;
        }

        /// <summary>Each column of <paramref name="table"/> as ACE's schema describes it: its OLE DB type and what Access calls it.</summary>
        private static Dictionary<string, (string Type, bool Nullable)> AceColumns(string path, string table)
        {
            var columns = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, new object[] { null, null, table, null });
                foreach (DataRow column in schema.Rows)
                {
                    int type = Convert.ToInt32(column["DATA_TYPE"]);
                    long length = column["CHARACTER_MAXIMUM_LENGTH"] is DBNull ? 0 : Convert.ToInt64(column["CHARACTER_MAXIMUM_LENGTH"]);
                    string name;
                    switch (type)
                    {
                        case 2: name = "Integer"; break;
                        case 3: name = "Long Integer"; break;
                        case 20: name = "Large Number"; break;
                        case 131: name = "Decimal(" + column["NUMERIC_PRECISION"] + "," + column["NUMERIC_SCALE"] + ")"; break;
                        case 128: name = length > 0 && length <= 255 ? "Binary(" + length + ")" : "OLE Object"; break;
                        case 130: name = length > 0 && length <= 255 ? "Text(" + length + ")" : "Memo"; break;
                        default: name = "type"; break;
                    }
                    columns[(string)column["COLUMN_NAME"]] = (type + " " + name, (bool)column["IS_NULLABLE"]);
                }
            }
            return columns;
        }

        /// <summary>Each field of <paramref name="table"/>'s Required property, as DAO reads it.</summary>
        private static Dictionary<string, bool> DaoRequired(string path, string table)
        {
            var required = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            dynamic dbe = Dao.Engine;
            try
            {
                dynamic db = dbe.OpenDatabase(path, false, true);
                try
                {
                    dynamic fields = db.TableDefs[table].Fields;
                    for (var i = 0; i < (int)fields.Count; i++)
                        required[(string)fields[i].Name] = (bool)fields[i].Required;
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return required;
        }
    }
}
