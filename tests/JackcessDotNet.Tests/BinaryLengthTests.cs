using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A Binary value longer than its column is refused before anything is written, with
/// <see cref="ColumnValueTooLongException"/>, as Access refuses it (#45). Measured through ACE and DAO, in an .mdb and
/// in .accdb files of Access 2007's and 2010's formats, for a column kept among the variable-length values
/// (SQL's VARBINARY, DAO's dbBinary) and one kept among the fixed-length ones (SQL's BINARY, dbBinary with
/// dbFixedField): a value of the column's length or less is taken - a fixed-length one filled to its length with
/// zeros - and one byte more is refused ("The field is too small to accept the amount of data you attempted to add"),
/// on an insert and on an update, the row left as it was. Access referees what this library writes in
/// qcatco/exportmdb.interop's suite.
/// </summary>
public sealed class BinaryLengthTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"binary_length_{Guid.NewGuid():N}");
    private string? _file;

    public void Dispose()
    {
        if (_file is not null && File.Exists(_file)) File.Delete(_file);
    }

    private static byte[] Bytes(int length) => Enumerable.Range(1, length).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2010)]
    public void A_value_longer_than_a_Binary_column_is_refused_before_anything_is_written(FileFormat format)
    {
        _file = _path + (format == FileFormat.Access2000 ? ".mdb" : ".accdb");
        using var db = Database.Create(_file, format);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("B", DataType.Binary) { MaxLength = 10 },
            new ColumnDefinition("Widest", DataType.Binary) { MaxLength = 255 },
        }, "Id");
        Assert.True(table.Columns[1].IsVariableLength, "the library makes a Binary column among the variable-length values");

        // The column's length is taken, and less.
        table.Insert(new Row { ["Id"] = 1, ["B"] = Bytes(10), ["Widest"] = Bytes(255) });
        table.Insert(new Row { ["Id"] = 2, ["B"] = Bytes(9), ["Widest"] = Bytes(1) });
        var pages = IndexChecks.AllPages(db);

        // One byte more is not, however it comes: insert, update, a batch, or asked first.
        var over = Assert.Throws<ColumnValueTooLongException>(() => table.Insert(new Row { ["Id"] = 3, ["B"] = Bytes(11) }));
        Assert.Equal("B", over.ColumnName);
        Assert.Equal(10, over.MaxLength);
        Assert.Equal(11, over.Length);
        Assert.Equal("Column 'B' holds at most 10 bytes; the value has 11.", over.Message);
        Assert.Equal(255, Assert.Throws<ColumnValueTooLongException>(() => table.Insert(new Row { ["Id"] = 3, ["Widest"] = Bytes(256) })).MaxLength);
        Assert.Throws<ColumnValueTooLongException>(() => table.Validate(new Row { ["Id"] = 3, ["B"] = Bytes(11) }));

        var row = table.FindRowByPrimaryKey(2)!;
        row["B"] = Bytes(11);
        Assert.Throws<ColumnValueTooLongException>(() => table.Update(row));
        var cursor = table.CreateCursor();
        cursor.FindRow("Id", 1);
        Assert.Throws<ColumnValueTooLongException>(() => cursor.UpdateCurrentRow(new Row { ["Widest"] = Bytes(256) }));

        var batch = Assert.Throws<BatchInsertException>(() => table.InsertRange(new[]
        {
            new Row { ["Id"] = 3, ["B"] = Bytes(1) }, new Row { ["Id"] = 4, ["B"] = Bytes(11) },
        }));
        Assert.Equal(1, batch.RowIndex);
        Assert.IsType<ColumnValueTooLongException>(batch.InnerException);

        IndexChecks.AssertUnchanged(pages, db);
        Assert.Equal(new[] { Bytes(10), Bytes(9) }, table.Rows.Select(r => (byte[])r["B"]!));
        Assert.Equal(new[] { Bytes(255), Bytes(1) }, table.Rows.Select(r => (byte[])r["Widest"]!));
    }

    [Fact]
    public void A_fixed_length_Binary_column_refuses_a_longer_value_with_the_same_exception()
    {
        // The library makes no fixed-length Binary column; Access's DDL does (BINARY(n)). Its rows are held to the
        // same rule, by the same check: before #45 the fixed form threw ArgumentOutOfRangeException, the
        // variable-length form nothing.
        var column = new ColumnDefinition("Fixed", DataType.Binary) { MaxLength = 4 }.ToColumn("columns");
        column.IsVariableLength = false;
        var encoder = new RowEncoder(JetFormat.Jet4, new[] { column });

        encoder.Validate(new Row { ["Fixed"] = Bytes(4) });
        encoder.Validate(new Row { ["Fixed"] = Bytes(3) });
        var over = Assert.Throws<ColumnValueTooLongException>(() => encoder.Validate(new Row { ["Fixed"] = Bytes(5) }));
        Assert.Equal(4, over.MaxLength);
        Assert.Equal(5, over.Length);
        Assert.Equal("Column 'Fixed' holds at most 4 bytes; the value has 5.", over.Message);
    }
}
