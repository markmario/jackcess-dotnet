using System.IO;
using System.Text;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Text whose bytes begin 0xFF (#93). 0xFF 0xFE is the mark of compressed text, and the UTF-16LE of U+FEFF is 0xFF
/// 0xFE too. <c>Assets/ace-text-marker.tsv</c> is what ACE 16 and DAO wrote for such text, and read back from the
/// table, in a Text column and a Memo column with Unicode compression and without (TC, TU, MC, MU), in an Access 2000
/// and an Access 2007 file (tools/ace-text-marker.ps1). Here the engine writes each value into the same table, as DAO
/// made it (<c>Assets/ace-text-marker.mdb</c>): it keeps the bytes Access keeps, where Access keeps them, and reads
/// back what Access reads - the value given, or, for text Access itself does not keep, the same other value.
/// </summary>
public sealed class TextMarkerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"text_marker_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    /// <summary>Each measured line: format, column, writer, value, place, stored, ACE's reading, DAO's reading.</summary>
    private static List<string[]> Lines() => File.ReadLines(Asset("ace-text-marker.tsv"))
        .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
        .Select(line => line.Split('\t'))
        .ToList();

    /// <summary>The value of a line: its UTF-16 code units in hex, "0061*3" for three of one.</summary>
    private static string Text(string spec)
    {
        var text = new StringBuilder();
        foreach (var token in spec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('*');
            text.Append((char)Convert.ToInt32(parts[0], 16), parts.Length > 1 ? int.Parse(parts[1]) : 1);
        }
        return text.ToString();
    }

    /// <summary>What ACE read: "same" for the value written, or the code units it read in hex.</summary>
    private static string Read(string text, string read) => read == "same"
        ? text
        : new string(read.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(unit => (char)Convert.ToInt32(unit, 16)).ToArray());

    /// <summary>The runs ACE compresses text in: 0xFF 0xFE, a byte a character 0x01-0xFF, two for others, 0x00 between.</summary>
    private static byte[] Runs(string text)
    {
        var bytes = new List<byte> { 0xFF, 0xFE };
        bool oneByte = true;
        foreach (char c in text)
        {
            bool latin = c is >= '\u0001' and <= '\u00FF';
            if (latin != oneByte) { bytes.Add(0); oneByte = latin; }
            bytes.Add((byte)c);
            if (!latin) bytes.Add((byte)(c >> 8));
        }
        return bytes.ToArray();
    }

    /// <summary>One line for each column and value: the formats, and the two writers, measured the same.</summary>
    public static IEnumerable<object[]> Measured() => Lines()
        .Where(line => line[0] == "mdb" && line[2] == "ace")
        .Select(line => new object[] { line[1], line[3], line[4], line[5], line[6] });

    [Fact]
    public void Access_2000_and_2007_files_ACE_and_DAO_keep_and_read_the_text_alike()
    {
        var lines = Lines();
        Assert.Equal(344, lines.Count);
        foreach (var group in lines.GroupBy(line => line[1] + " " + line[3]))
        {
            Assert.Single(group.Select(line => line[4] + " " + line[5]).Distinct());
            // One value ACE fails to read, and DAO reads as null.
            Assert.True(group.SelectMany(line => new[] { line[6], line[7] }).Where(read => read != "error" && read != "null").Distinct().Count() <= 1, group.Key);
        }
    }

    [Theory]
    [MemberData(nameof(Measured))]
    public void The_engine_keeps_and_reads_the_text_as_Access_does(string column, string value, string place, string stored, string read)
    {
        string text = Text(value);
        File.Copy(Asset("ace-text-marker.mdb"), _path);
        using var db = Database.Open(_path);
        var table = db.GetTable("T");
        var row = new Row { ["Id"] = 1, [column] = text };
        table.Insert(row);

        var (enginePlace, bytes) = Stored(db, row, column);
        var engineRead = (string?)table.FindRowByPrimaryKey(1)![column];
        if (place == "chain" && text.Length * 2 <= 4076)
        {
            // ACE keeps a value of more than 3,816 bytes as a chain, and the engine keeps one of up to 4,076 bytes in
            // one long-value row. In a column with Unicode compression ACE reads that row as compressed text, and so
            // does the engine: text that begins with U+FEFF, of 1,909 to 2,038 characters, is read back changed there
            // (#93). Elsewhere the engine reads the row as ACE reads the chain.
            Assert.Equal("page", enginePlace);
            Assert.Equal(Encoding.Unicode.GetBytes(text), bytes);
            Assert.Equal(column == "MC" ? text.Substring(1) : text, engineRead);
            return;
        }
        Assert.Equal(place, enginePlace);
        // Text that does not begin with U+FEFF is written as before: in a column without Unicode compression ACE
        // compresses a Memo kept in the row, and the engine does not.
        if (text.StartsWith("\uFEFF", StringComparison.Ordinal) || column != "MU")
            Assert.Equal(stored == "plain" ? Encoding.Unicode.GetBytes(text) : Runs(text), bytes);
        // The value ACE cannot read: it fails, and DAO reads null. The engine reads it as compressed text.
        if (read != "error")
            Assert.Equal(Read(text, read), engineRead);
    }

    /// <summary>Where the row keeps a value (row, inline, page or chain), and its bytes.</summary>
    private static (string Place, byte[] Bytes) Stored(Database db, Row row, string column)
    {
        var info = TdefReader.Read(TdefBuffer.Read(db.File, IndexChecks.TdefOf(db, "T")), db.File.Format);
        var col = info.Columns.Single(c => c.Name == column);
        byte[] rowBytes = JetPages.RowBytes(db.File, row.Id!.Value.PageNumber, row.Id!.Value.RowNumber);
        int columns = rowBytes[0] | rowBytes[1] << 8;
        int varLenAt = rowBytes.Length - (columns + 7) / 8 - 2;
        int Offset(int k) => rowBytes[varLenAt - 2 * (k + 1)] | rowBytes[varLenAt - 2 * (k + 1) + 1] << 8;
        byte[] value = rowBytes.Skip(Offset(col.VarLenTableIndex)).Take(Offset(col.VarLenTableIndex + 1) - Offset(col.VarLenTableIndex)).ToArray();
        if (col.DataType == DataType.Text) return ("row", value);

        int length = value[0] | value[1] << 8 | value[2] << 16;
        int lvalRow = value[4], lvalPage = value[5] | value[6] << 8 | value[7] << 16;
        switch (value[3] & 0xC0)
        {
            case 0x80: return ("inline", value.Skip(12).Take(length).ToArray());
            case 0x40: return ("page", JetPages.RowBytes(db.File, lvalPage, lvalRow).Take(length).ToArray());
        }
        var bytes = new List<byte>();
        while (bytes.Count < length)
        {
            byte[] chunk = JetPages.RowBytes(db.File, lvalPage, lvalRow);
            lvalRow = chunk[0];
            lvalPage = chunk[1] | chunk[2] << 8 | chunk[3] << 16;
            bytes.AddRange(chunk.Skip(4));
        }
        return ("chain", bytes.Take(length).ToArray());
    }

    [Theory]
    [InlineData("FF0161006200", "\u01FFab")]       // 0xFF and another byte: UTF-16LE, not a mark
    [InlineData("FF0061006200", "\u00FFab")]
    [InlineData("FF", "\uFFFD")]                    // one byte alone, as UTF-16LE reads it
    [InlineData("FFFE", "")]
    [InlineData("FFFE414200C630", "AB\u30C6")]
    [InlineData("FFFE510034005A00", "Q4Z")]         // after a switch, two bytes at a time: 0x00 as a high byte is no switch
    public void Bytes_that_may_be_compressed_are_read_as_ACE_reads_them(string hex, string text)
    {
        var bytes = Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(2 * i, 2), 16)).ToArray();
        Assert.Equal(text, ByteUtil.DecodeText(bytes, 0, bytes.Length));
    }

    [Theory]
    [InlineData("\uFEFFabQ1Z", "FFFE00FFFE00616251315A")]          // runs one byte shorter than UTF-16LE
    [InlineData("\uFEFFaaaa", "FFFE00FFFE0061616161")]            // as long: a Text value is compressed all the same
    [InlineData("\uFEFFabc", "FFFE610062006300")]                 // a byte longer: UTF-16LE
    [InlineData("\uFEFF\u0100aaaaaa", "FFFE0001610061006100610061006100")]   // U+0100 is no run's
    public void A_Text_value_that_begins_with_U_FEFF_is_written_as_ACE_writes_it(string text, string hex)
    {
        Assert.Equal(hex, Convert.ToHexString(ByteUtil.EncodeText(text)));
    }
}
