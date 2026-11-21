using System;
using System.Collections.Generic;
using System.Data.OleDb;
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
    /// A Binary value one byte longer than its column is refused by Access, and by the managed engine before anything
    /// is written, with <see cref="ColumnValueTooLongException"/> (#45); the values Access
    /// takes - the column's length and less - the engine writes as Access reads them. In an .mdb and an .accdb DAO
    /// made, on columns of both forms a definition has: kept among the variable-length values (ACE's VARBINARY, DAO's
    /// dbBinary, what the engine makes) and among the fixed-length ones (ACE's BINARY, dbBinary with dbFixedField),
    /// whose values are filled to the column's length with zeros. The engine took a variable-length value of any
    /// length, and refused a fixed-length one with an ArgumentOutOfRangeException.
    /// </summary>
    public class BinaryLengthInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        public static IEnumerable<object[]> Formats()
        {
            yield return new object[] { 64, ".mdb" };
            yield return new object[] { 128, ".accdb" };
        }

        private static byte[] Bytes(int length, byte seed = 1) => Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();

        // What a column of the fixed-length form holds of a value: the value, then zeros to the column's length.
        private static byte[] AsHeld(byte[] value, int length, bool isFixed)
            => isFixed ? value.Concat(new byte[length - value.Length]).ToArray() : value;

        /// <summary>DAO makes table D: a Long, and a Binary of 10 bytes of each form.</summary>
        private static void DaoTable(string path)
        {
            var engine = Dao.Engine;
            try { MakeDaoTable(engine, path); }
            finally { Dao.Release(engine); }
        }

        // Its own method, so nothing DAO handed out is still in a local when the engine is released.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void MakeDaoTable(object engine, string path)
        {
            const int dbLong = 4, dbBinary = 9, dbFixedField = 1;
            dynamic db = ((dynamic)engine).OpenDatabase(path);
            try
            {
                dynamic table = db.CreateTableDef("D");
                table.Fields.Append(table.CreateField("Id", dbLong));
                table.Fields.Append(table.CreateField("VB", dbBinary, 10));
                dynamic fixedField = table.CreateField("FB", dbBinary, 10);
                fixedField.Attributes = dbFixedField;
                table.Fields.Append(fixedField);
                db.TableDefs.Append(table);
            }
            finally { db.Close(); }
        }

        [Theory]
        [MemberData(nameof(Formats))]
        public void the_engine_refuses_the_binary_values_access_refuses_and_writes_the_ones_it_takes_as_access_reads_them(int daoVersion, string extension)
        {
            var path = Path.Combine(_tmp.Dir, Guid.NewGuid().ToString("N") + extension);
            DaoCreate(path, daoVersion);
            Execute(path, "CREATE TABLE A (Id LONG CONSTRAINT pk PRIMARY KEY, VB VARBINARY(10), FB BINARY(10), WB VARBINARY(255))");
            DaoTable(path);
            Execute(path, "CREATE INDEX pk ON D (Id) WITH PRIMARY");
            using (var db = Database.Open(path))
                db.CreateTable("E", new[]
                {
                    new ColumnDefinition("Id", DataType.Int32),
                    new ColumnDefinition("VB", DataType.Binary) { MaxLength = 10 },
                    new ColumnDefinition("WB", DataType.Binary) { MaxLength = 255 },
                }, "Id");
            // A row of every table with every column, null, so that the rows written below are compared whole.
            Execute(path, "INSERT INTO A (Id) VALUES (999)", "INSERT INTO D (Id) VALUES (999)", "INSERT INTO E (Id) VALUES (999)");

            var columns = new[]
            {
                new { Table = "A", Column = "VB", Length = 10, Fixed = false }, new { Table = "A", Column = "FB", Length = 10, Fixed = true },
                new { Table = "A", Column = "WB", Length = 255, Fixed = false }, new { Table = "D", Column = "VB", Length = 10, Fixed = false },
                new { Table = "D", Column = "FB", Length = 10, Fixed = true }, new { Table = "E", Column = "VB", Length = 10, Fixed = false },
                new { Table = "E", Column = "WB", Length = 255, Fixed = false },
            };
            var subjects = new[] { "A", "D", "E" }.ToDictionary(t => t, t => new Subject(path, t, "Id"));
            var id = 0;
            foreach (var c in columns)
            {
                var what = c.Table + "." + c.Column + " (" + c.Length + " bytes, " + (c.Fixed ? "fixed" : "variable") + ")";
                using (var db = Database.Open(path))
                    Assert.True(db.GetTable(c.Table).Columns.Single(k => k.Name == c.Column).IsVariableLength == !c.Fixed, what + ": not the form the test is for");

                // One byte over: Access refuses it...
                var over = Bytes(c.Length + 1);
                var refused = Assert.Throws<OleDbException>(() => AceInsert(path, c.Table, V("Id", 1000, c.Column, over)));
                Assert.Contains("too small to accept the amount of data", refused.Message);

                // ...and so does the engine, inserted and updated, before a byte of the file changes.
                subjects[c.Table].EngineInserts(V("Id", ++id, c.Column, Bytes(1)));
                var before = File.ReadAllBytes(path);
                using (var db = Database.Open(path))
                {
                    var table = db.GetTable(c.Table);
                    var tooLong = Assert.Throws<ColumnValueTooLongException>(() => table.Insert(new Row { ["Id"] = 1000, [c.Column] = over }));
                    Assert.Equal(c.Column, tooLong.ColumnName);
                    Assert.Equal(c.Length, tooLong.MaxLength);
                    Assert.Equal(c.Length + 1, tooLong.Length);
                    var row = table.FindRowByPrimaryKey(id);
                    row[c.Column] = over;
                    Assert.Throws<ColumnValueTooLongException>(() => table.Update(row));
                }
                Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), what + ": the file changed when the engine refused a value one byte over");

                // The column's length and less are taken: written by the engine and by ACE, read as each other reads them.
                foreach (var length in new[] { c.Length, c.Length - 1 })
                {
                    subjects[c.Table].EngineInserts(V("Id", ++id, c.Column, Bytes(length, 3)));
                    subjects[c.Table][id][c.Column] = AsHeld(Bytes(length, 3), c.Length, c.Fixed);
                    subjects[c.Table].AceInserts(V("Id", ++id, c.Column, Bytes(length, 7)));
                    subjects[c.Table][id][c.Column] = AsHeld(Bytes(length, 7), c.Length, c.Fixed);
                }
                // The value of 1 byte written first, which the update left as it was.
                subjects[c.Table][id - 4][c.Column] = AsHeld(Bytes(1), c.Length, c.Fixed);
            }

            foreach (var subject in subjects)
                subject.Value.Check("after the engine and ACE wrote values of every length Access takes, and the engine refused one longer");
        }
    }
}
