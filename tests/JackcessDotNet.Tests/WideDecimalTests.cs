using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A Decimal whose stored number runs past 96 bits (qcatco/exportmdb.interop#74). A Decimal is a sign byte and four
/// 32-bit words, the most significant first. Access writes none with the first word set - its 28 digits are under
/// 2^96 - and reads one as if the word were not there: the number the other three make, with its sign. This
/// library read null. Java Jackcess gives all 128 bits as a BigDecimal, which a <see cref="decimal"/> has no room
/// for; Access is followed.
/// <para>
/// Nothing writes such a value, so these tests put the bytes in the file themselves (<see cref="Store"/>): a row is
/// inserted with a number that stands in, and its seventeen bytes are written over. The numbers expected are the
/// ones ACE 12 gave for the same bytes, through its OLE DB provider and through DAO - they are held to ACE itself
/// in ExportMdb.Interop's EarlierEngineFilesInAccessTests.
/// </para>
/// </summary>
public sealed class WideDecimalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wide_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>
    /// Writes a sign byte and the four words, each least significant byte first, over the one value in the file
    /// that is <paramref name="standIn"/> in a column of <paramref name="precision"/> and <paramref name="scale"/>.
    /// </summary>
    private void Store(decimal standIn, byte precision, byte scale, bool negative, uint first, uint second, uint third, uint fourth)
    {
        byte[] now = NumericCodec.Encode(standIn, precision, scale, "the stand-in");

        var wide = new byte[NumericCodec.Size];
        wide[0] = negative ? (byte)0x80 : (byte)0;
        uint[] words = { first, second, third, fourth };
        for (int word = 0; word < 4; word++)
            BitConverter.GetBytes(words[word]).CopyTo(wide, 1 + word * 4);

        byte[] file = File.ReadAllBytes(_path);
        var at = Enumerable.Range(0, file.Length - now.Length).Where(i => file.AsSpan(i, now.Length).SequenceEqual(now)).ToList();
        Assert.Single(at);
        wide.CopyTo(file, at[0]);
        File.WriteAllBytes(_path, file);
    }

    [Fact]
    public void a_decimal_with_its_first_word_set_reads_as_the_number_its_other_three_words_make()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("D", new[]
            {
                new ColumnBuilder("Id", DataType.Long).Build(),
                ColumnBuilder.Numeric("Small", 5, 2).Build(),
                ColumnBuilder.Numeric("Big", 28, 0).Build(),
            }, "Id");
            for (int id = 1; id <= 4; id++)
                table.Insert(new Row { ["Id"] = id, ["Small"] = id * 111.11m, ["Big"] = id * 11_111m + 1 });
        }
        Store(111.11m, 5, 2, negative: false, 0x10000000, 0, 0, 0);                             // 2^124: nothing in the low 96 bits
        Store(222.22m, 5, 2, negative: false, 1, 0, 0, 12_345);                                 // 2^96 + 12345
        Store(333.33m, 5, 2, negative: true, 0xFFFFFFFF, 0, 0, 12_345);
        Store(444.44m, 5, 2, negative: false, 1, 0, 0, 1_234_567);                              // more than the column's five digits, too
        Store(11_112m, 28, 0, negative: false, 1, 0, 0, 12_345);
        Store(22_223m, 28, 0, negative: false, 1, 1, 2, 3);                                     // 2^96 + 2^64 + 2 * 2^32 + 3
        Store(33_334m, 28, 0, negative: false, 1, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF);          // 2^97 - 1: the low 96 bits all set

        using var read = Database.Open(_path);
        var rows = read.GetTable("D").ReadAllRows();

        Assert.Equal(new object?[] { 0.00m, 123.45m, -123.45m, 12_345.67m }, rows.Select(row => row["Small"]));
        Assert.Equal(new object?[] { 12_345m, 18_446_744_082_299_486_211m, decimal.MaxValue, 44_445m }, rows.Select(row => row["Big"]));
        Assert.Equal("0.00", ((decimal)rows[0]["Small"]!).ToString(System.Globalization.CultureInfo.InvariantCulture));

        // A cursor reads what ReadAllRows reads.
        var cursor = read.GetTable("D").NewCursor();
        Assert.Equal(0.00m, cursor.GetNextRow()!["Small"]);
        Assert.Equal(123.45m, cursor.GetNextRow()!["Small"]);

        // Each is listed, with the number a read gives - the three that fit their columns as much as the rest.
        var listed = read.FindDecimalsBeyondPrecision().ToList();
        Assert.Equal(new[] { "1 Small", "1 Big", "2 Small", "2 Big", "3 Small", "3 Big", "4 Small" },
                     listed.Select(found => $"{found.PrimaryKey["Id"]} {found.Column.Name}"));
        Assert.All(listed, found =>
        {
            Assert.True(found.IsCut);
            Assert.Equal(rows[found.RowIndex][found.Column.Name], found.Value);
        });
    }

    /// <summary>A number of 96 bits or fewer reads as it did, and is not said to be cut.</summary>
    [Fact]
    public void a_decimal_within_96_bits_reads_whole()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("D", new[] { new ColumnBuilder("Id", DataType.Long).Build(), ColumnBuilder.Numeric("Big", 28, 0).Build() }, "Id");
            table.Insert(new Row { ["Id"] = 1, ["Big"] = 11_112m });
            table.Insert(new Row { ["Id"] = 2, ["Big"] = 9_999_999_999_999_999_999_999_999_999m });
        }
        Store(11_112m, 28, 0, negative: true, 0, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF);

        using var read = Database.Open(_path);
        Assert.Equal(new object?[] { decimal.MinValue, 9_999_999_999_999_999_999_999_999_999m }, read.GetTable("D").ReadAllRows().Select(row => row["Big"]));

        var found = Assert.Single(read.FindDecimalsBeyondPrecision());   // 29 digits in a column of 28
        Assert.False(found.IsCut);
        Assert.Equal(decimal.MinValue, found.Value);
    }
}
