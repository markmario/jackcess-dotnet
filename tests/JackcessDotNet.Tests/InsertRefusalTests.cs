using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table can be asked whether it takes rows, before anything is written: a caller
/// that writes to several tables asks each first, and writes to none when one would refuse.
/// <see cref="Table.InsertRefusal"/> is what <see cref="Table.Insert"/> throws, so the two cannot differ; each
/// case here holds them to it, for a table that takes rows and for each kind of table that does not, and holds
/// that asking leaves the file the same bytes.
/// </summary>
public sealed class InsertRefusalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"asked_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void CopyOf(string version, string file)
        => File.Copy(TestCorpus.File(version, file) ?? throw new FileNotFoundException($"{file} is not in the corpus."), _path, overwrite: true);

    // The table's definition page, and where its column definitions begin (Jet4).
    private static (int Tdef, byte[] Page, int ColumnDefs) DefinitionOf(Database db, string table)
    {
        int tdef = db.GetTable(table).Definition.TdefPageNumber;
        byte[] page = db.File.ReadPage(tdef);
        return (tdef, page, 63 + ByteUtil.GetInt(page, 51) * 12);
    }

    // What is asked of a table that takes no row: the answer, the same answer thrown by Insert and by Validate,
    // and a file that asking, and being refused, left as it was.
    private void AssertRefuses(string tableName, string because, Action<Table>? damage = null)
    {
        byte[] before = File.ReadAllBytes(_path);
        string? asked;
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable(tableName);
            damage?.Invoke(table);
            asked = table.InsertRefusal;
            Assert.NotNull(asked);
            Assert.Contains(because, asked);
            Assert.Equal(asked, table.InsertRefusal);   // asked twice, answered the same
        }
        Assert.Equal(before, File.ReadAllBytes(_path));

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable(tableName);
            damage?.Invoke(table);
            var row = new Row { [table.Columns[0].Name] = 1 };
            Assert.Equal(asked, Assert.Throws<NotSupportedException>(() => table.Insert(row)).Message);
            Assert.Equal(asked, Assert.Throws<NotSupportedException>(() => table.Validate(row)).Message);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_table_that_takes_rows_has_no_refusal_and_asking_changes_nothing()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("T", new[]
            {
                new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true },
                new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
                new ColumnDefinition("When", DataType.DateTime),
                new ColumnDefinition("Notes", DataType.Memo),
            }, new[] { "Id" }, new[] { new IndexDefinition("ByName", "Name"), new IndexDefinition("ByWhen", "When") });
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
            Assert.Null(db.GetTable("T").InsertRefusal);
        Assert.Equal(before, File.ReadAllBytes(_path));

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            table.Insert(new Row { ["Name"] = "one", ["When"] = new DateTime(2026, 9, 14), ["Notes"] = "n" });
            Assert.Null(table.InsertRefusal);
            Assert.Equal("one", table.CreateIndexCursor("ByName").FindRow("Name", "one")!["Name"]);
        }
    }

    // Tables Access made that the engine does not write to, each for a reason of its own.
    [Theory]
    [InlineData("V2007", "blobV2007.accdb", "Table1", "Complex column, whose keys this library does not write")]   // an index on an attachment column
    [InlineData("V2003", "indexCodesV2003.mdb", "Table7", "Decimal column, whose keys this library does not write")] // a Decimal index
    [InlineData("V1997", "common1V1997.mdb", "Table1", "Access 97 (Jet 3) files are read-only in this library")]   // an Access 97 file: that before any other reason
    [InlineData("V2010", "calcFieldV2010.accdb", "Table1", "is calculated")]
    public void A_table_Access_made_that_takes_no_row_says_what_insert_throws(string version, string file, string table, string because)
    {
        CopyOf(version, file);
        AssertRefuses(table, because);
    }

    // A complex column with no index. Access gives every row a number for each complex column, from the table's own
    // counter, whether or not the row has values, and deletes a row's values with the row; it keeps a unique index
    // on each complex column and does not let it be dropped ("The unique index on the 'Files' column cannot be
    // deleted"), so the tables it makes are refused for that index (above). Measured with ACE 16:
    // tools/dao-complex-rows.ps1. A table without the index is refused for the column itself, and none of its rows
    // is changed or deleted either (#152).
    [Fact]
    public void A_table_with_a_complex_column_and_no_index_takes_no_row_and_changes_none()
    {
        using (var db = Database.Create(_path, FileFormat.Access2007))
        {
            db.CreateTable("T", new[] { new ColumnDefinition("A", DataType.Int32), new ColumnDefinition("Files", DataType.Int32) })
              .Insert(new Row { ["A"] = 1, ["Files"] = 7 });

            var (tdef, page, columnDefs) = DefinitionOf(db, "T");
            page[columnDefs + 1 * 25] = (byte)DataType.Complex;   // Files is a complex column
            db.File.WritePage(tdef, page);
        }

        AssertRefuses("T", "its column Files is a complex one");

        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            var row = Assert.Single(table.Rows);
            Assert.Null(row["Files"]);
            row["A"] = 2;
            Assert.Contains("'Files' is a complex one", Assert.Throws<NotSupportedException>(() => table.Update(row)).Message);
            Assert.Contains("'Files' is a complex one", Assert.Throws<NotSupportedException>(() => table.Delete(row)).Message);
            var cursor = table.CreateCursor();
            Assert.True(cursor.MoveNext());
            Assert.Throws<NotSupportedException>(() => cursor.DeleteCurrentRow());
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // A text index in an order the engine writes no keys for (here 1055, Access's Turkish order).
    [Fact]
    public void A_table_with_a_text_index_in_another_language_s_order_says_what_insert_throws()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("Coded", new[]
            {
                new ColumnDefinition("N", DataType.Int32),
                new ColumnDefinition("Code", DataType.Text) { MaxLength = 10 },
            }, new[] { "N" }, new[] { new IndexDefinition("ByCode", "Code") })
              .Insert(new Row { ["N"] = 1, ["Code"] = "a" });

            // The indexed column now says sort order 1055.
            var (tdef, page, columnDefs) = DefinitionOf(db, "Coded");
            page[columnDefs + 1 * 25 + 11] = 0x1F;
            db.File.WritePage(tdef, page);
        }

        AssertRefuses("Coded", "Coded's index ByCode cannot be kept: Code sorts in");
    }

    // Access numbers Long and Replication ID columns only; the flag on a column of another type is one a damaged
    // definition has, and the engine has no number to give such a column.
    [Fact]
    public void A_table_with_an_AutoNumber_the_engine_does_not_generate_says_what_insert_throws()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("T", new[]
            {
                new ColumnDefinition("Id", DataType.Int32),
                new ColumnDefinition("Small", DataType.Int16),
            }).Insert(new Row { ["Id"] = 1, ["Small"] = (short)2 });

            var (tdef, page, columnDefs) = DefinitionOf(db, "T");
            page[columnDefs + 1 * 25 + db.File.Format.OffsetColumnFlags] |= 0x04;   // Small is flagged an AutoNumber
            db.File.WritePage(tdef, page);
        }

        AssertRefuses("T", "T.Small is an AutoNumber of type Int");
    }

    [Fact]
    public void A_table_whose_definition_puts_two_columns_in_one_place_says_what_insert_throws()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("T", new[] { new ColumnDefinition("A", DataType.Int32), new ColumnDefinition("B", DataType.Int32) }, primaryKey: "A")
              .Insert(new Row { ["A"] = 1, ["B"] = 2 });

        // As a damaged definition would have it: B over the end of A.
        AssertRefuses("T", "same place", table => table.Columns[1].FixedDataOffset = 2);
    }

    // A definition that ends before its columns' names do is a damaged one: here a table is marked as one whose
    // columns were not all read. (A definition over several pages is read whole, and its table written to: #37.)
    [Fact]
    public void A_table_whose_columns_were_not_all_read_says_what_insert_throws()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("T", new[] { new ColumnDefinition("A", DataType.Int32) }).Insert(new Row { ["A"] = 1 });

        AssertRefuses("T", "ends before its columns' names do", table => table.Definition.HasUnreadColumns = true);
    }

    // The refusals come in the order an insert meets them, so the one asked for is the one thrown: here a table
    // whose indexes and whose columns were not all read says the indexes.
    [Fact]
    public void A_table_refused_for_two_reasons_says_the_one_insert_throws()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("T", new[] { new ColumnDefinition("A", DataType.Int32) }, primaryKey: "A").Insert(new Row { ["A"] = 1 });

        AssertRefuses("T", "indexes cannot be kept: they were not all read", table =>
        {
            table.Definition.HasUnreadColumns = true;
            table.Definition.HasUnreadIndexes = true;
        });
    }
}
