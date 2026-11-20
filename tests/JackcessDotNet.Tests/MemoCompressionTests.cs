using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A Memo value's bytes are the ones Access writes (#87). Measured with ACE 16 on an export template, whose
/// Memo columns have Unicode compression: "" is no bytes; 1 or 2 characters are UTF-16LE; 3 to 1,908 characters that
/// are all 0x01-0xFF are 0xFF 0xFE and a byte each; 1,909 and more are UTF-16LE, a chain from 3,818 bytes. Text mixing
/// those characters with others ACE compresses in runs; this library writes it as UTF-16LE, which ACE reads the same.
/// The values are read back from the file's own bytes. What ACE makes of them is refereed by tests still to come
/// into this repository (#96).
/// </summary>
public sealed class MemoCompressionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"memo_compression_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // The bytes the file keeps for the value: in the row, or on its long-value page.
    private static byte[] Stored(Database db, string text)
    {
        var table = db.GetTable("M");
        var row = new Row { ["Id"] = table.RowCount + 1, ["Notes"] = text };
        table.Insert(row);
        var info = TdefReader.Read(TdefBuffer.Read(db.File, IndexChecks.TdefOf(db, "M")), db.File.Format);
        var column = info.Columns.Single(c => c.Name == "Notes");
        byte[] rowBytes = JetPages.RowBytes(db.File, row.Id!.Value.PageNumber, row.Id!.Value.RowNumber);
        int columns = rowBytes[0] | rowBytes[1] << 8;
        int varLenAt = rowBytes.Length - (columns + 7) / 8 - 2;
        int Offset(int k) => rowBytes[varLenAt - 2 * (k + 1)] | rowBytes[varLenAt - 2 * (k + 1) + 1] << 8;
        byte[] header = rowBytes.Skip(Offset(column.VarLenTableIndex)).Take(Offset(column.VarLenTableIndex + 1) - Offset(column.VarLenTableIndex)).ToArray();
        int length = header[0] | header[1] << 8 | header[2] << 16;
        if ((header[3] & 0x80) != 0) return header.Skip(12).ToArray();
        Assert.True((header[3] & 0x40) != 0, "a chain: these values are each one long-value row");
        return JetPages.RowBytes(db.File, header[5] | header[6] << 8 | header[7] << 16, header[4]).Take(length).ToArray();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "6100")]
    [InlineData("aa", "61006100")]
    [InlineData("aaa", "FFFE616161")]
    [InlineData("é€x", "E900AC207800")]          // € is not 0x01-0xFF
    [InlineData("ααα", "B103B103B103")]
    [InlineData("x\0y", "780000007900")]            // a NUL is the compressed stream's toggle
    public void A_short_memo_is_kept_as_access_keeps_it(string text, string hex)
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        db.CreateTable("M", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) }, "Id");

        Assert.Equal(hex, Convert.ToHexString(Stored(db, text)));
        Assert.Equal(text, db.GetTable("M").FindRowByPrimaryKey(1)!["Notes"]);
    }

    [Theory]
    [InlineData(255, 257)]
    [InlineData(1907, 1909)]
    [InlineData(1908, 1910)]
    public void A_memo_of_up_to_1908_characters_is_compressed_on_its_long_value_page(int chars, int bytes)
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        db.CreateTable("M", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) }, "Id");
        string text = new('a', chars);

        var stored = Stored(db, text);
        Assert.Equal(bytes, stored.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, stored.Take(2).ToArray());
        Assert.Equal(text, db.GetTable("M").FindRowByPrimaryKey(1)!["Notes"]);
    }

    [Fact]
    public void A_memo_of_1909_characters_or_more_is_not_compressed()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        db.CreateTable("M", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) }, "Id");
        var table = db.GetTable("M");
        string text = new('a', 1909);
        table.Insert(new Row { ["Id"] = 1, ["Notes"] = text });

        Assert.Equal(text, table.FindRowByPrimaryKey(1)!["Notes"]);
        var column = new ColumnDefinition("Notes", DataType.Memo).ToColumn("column");
        Assert.Equal(3818, RowEncoder.EncodeMemo(column, text).Length);
        Assert.Equal(1910, RowEncoder.EncodeMemo(column, new string('a', 1908)).Length);
    }
}
