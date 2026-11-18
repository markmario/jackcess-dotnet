using System.IO;
using System.Numerics;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Table.FindDecimalsBeyondPrecision"/> and <see cref="Database.FindDecimalsBeyondPrecision"/>: the
/// Decimal values that read with more digits than their column holds, which is how a value written by a version of
/// this library before qcatco/exportmdb.interop#61 can show (qcatco/exportmdb.interop#66).
/// <para>
/// Those versions wrote a Decimal as a sign byte and sixteen bytes, the most significant first; Access, and this
/// library now, keep four 32-bit words, the most significant first, each word's bytes least significant first.
/// This library no longer writes the earlier order, so these tests put it in the file themselves: a row is
/// inserted with a number that stands in, and its seventeen bytes are then written over, in the file, with the
/// sign byte and the sixteen bytes of the number meant in the earlier order (<see cref="StoreInEarlierByteOrder"/>).
/// </para>
/// </summary>
public sealed class DecimalBeyondPrecisionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dec_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();
    private static Column Decimal(string name, byte precision, byte scale) => ColumnBuilder.Numeric(name, precision, scale).Build();

    /// <summary>Prices(Id, Price DECIMAL(5,2), Quantity DECIMAL(18,0)), Id its primary key unless <paramref name="keyed"/> is false.</summary>
    private Table Prices(Database db, bool keyed = true)
        => db.CreateTable("Prices", new[] { Long("Id"), Decimal("Price", 5, 2), Decimal("Quantity", 18, 0) }, keyed ? "Id" : null);

    /// <summary>
    /// Writes <paramref name="meant"/> over the one value in the file that is <paramref name="standIn"/> in a column
    /// of <paramref name="precision"/> and <paramref name="scale"/>, as earlier versions of this library wrote a
    /// Decimal: the sign byte, then the number without its decimal point as sixteen bytes, the most significant
    /// first.
    /// </summary>
    private void StoreInEarlierByteOrder(decimal standIn, byte precision, byte scale, decimal meant)
        => StoreInEarlierByteOrder(standIn, precision, scale, new BigInteger(Math.Abs(meant) * (decimal)Math.Pow(10, scale)), meant < 0);

    /// <summary>As above, the number given without its sign or its decimal point.</summary>
    private void StoreInEarlierByteOrder(decimal standIn, byte precision, byte scale, BigInteger unscaled, bool negative)
    {
        byte[] now = NumericCodec.Encode(standIn, precision, scale, "the stand-in");

        var earlier = new byte[NumericCodec.Size];
        earlier[0] = negative ? (byte)0x80 : (byte)0;
        for (int i = 0; i < 16; i++)
            earlier[16 - i] = (byte)(unscaled >> (8 * i) & 0xFF);

        byte[] file = File.ReadAllBytes(_path);
        var at = Enumerable.Range(0, file.Length - now.Length).Where(i => file.AsSpan(i, now.Length).SequenceEqual(now)).ToList();
        Assert.Single(at);
        earlier.CopyTo(file, at[0]);
        File.WriteAllBytes(_path, file);
    }

    [Fact]
    public void a_value_in_the_earlier_byte_order_is_listed_with_the_number_it_was_meant_to_be()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var prices = Prices(db);
            prices.Insert(new Row { ["Id"] = 1, ["Price"] = 1.23m, ["Quantity"] = 5m });
            prices.Insert(new Row { ["Id"] = 2, ["Price"] = 111.11m, ["Quantity"] = 6m });
            prices.Insert(new Row { ["Id"] = 3, ["Price"] = 222.22m });
        }
        StoreInEarlierByteOrder(111.11m, 5, 2, meant: 1.00m);
        StoreInEarlierByteOrder(222.22m, 5, 2, meant: -12.34m);

        using var read = Database.Open(_path);
        var table = read.GetTable("Prices");
        var found = table.FindDecimalsBeyondPrecision().ToList();

        Assert.Equal(2, found.Count);
        Assert.All(found, f => { Assert.Equal("Prices", f.Table); Assert.Equal("Price", f.Column.Name); Assert.Equal(5, f.Column.Precision); Assert.False(f.IsCut); });

        Assert.Equal(1, found[0].RowIndex);
        Assert.Equal(2, found[0].PrimaryKey["Id"]);
        Assert.Equal(16_777_216.00m, found[0].Value);
        Assert.Equal(1.00m, found[0].ValueInEarlierByteOrder);

        Assert.Equal(2, found[1].RowIndex);
        Assert.Equal(3, found[1].PrimaryKey["Id"]);
        Assert.Equal(-35_234_775.04m, found[1].Value);
        Assert.Equal(-12.34m, found[1].ValueInEarlierByteOrder);

        // Listing changes nothing: the rows read as they did, and the value listed is the value read.
        var rows = table.Rows.ToList();
        Assert.Equal(new object?[] { 1.23m, 16_777_216.00m, -35_234_775.04m }, rows.Select(row => row["Price"]));
        Assert.All(found, f => Assert.Equal(f.Value, rows[f.RowIndex]["Price"]));

        // The database's list is its tables' lists.
        Assert.Equal(found.Select(f => f.ToString()), read.FindDecimalsBeyondPrecision().Select(f => f.ToString()));
        Assert.Equal("Prices.Price, row 1 (Id=2): reads 16777216.00, 1.00 in the earlier byte order", found[0].ToString());
    }

    [Fact]
    public void a_row_of_a_table_without_a_primary_key_is_told_by_its_place_among_the_rows()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var prices = Prices(db, keyed: false);
            prices.Insert(new Row { ["Id"] = 1, ["Price"] = 1.23m });
            prices.Insert(new Row { ["Id"] = 2, ["Price"] = 111.11m });
        }
        StoreInEarlierByteOrder(111.11m, 5, 2, meant: 9.99m);

        using var read = Database.Open(_path);
        var found = Assert.Single(read.FindDecimalsBeyondPrecision());

        Assert.Empty(found.PrimaryKey);
        Assert.Equal(1, found.RowIndex);
        Assert.Equal(2, read.GetTable("Prices").Rows.ToList()[found.RowIndex]["Id"]);
        Assert.Equal(9.99m, found.ValueInEarlierByteOrder);
    }

    [Fact]
    public void values_this_library_writes_are_not_listed()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var prices = Prices(db);
            prices.Insert(new Row { ["Id"] = 1, ["Price"] = 999.99m, ["Quantity"] = 999_999_999_999_999_999m });
            prices.Insert(new Row { ["Id"] = 2, ["Price"] = -999.99m, ["Quantity"] = -999_999_999_999_999_999m });
            prices.Insert(new Row { ["Id"] = 3, ["Price"] = 0m, ["Quantity"] = 16_777_216m });
            prices.Insert(new Row { ["Id"] = 4 });
        }

        using var read = Database.Open(_path);
        Assert.Empty(read.FindDecimalsBeyondPrecision());
    }

    /// <summary>
    /// What the list cannot show. A value in the earlier order that still reads within its column's precision is
    /// one Access could have written: 1 in a column of eighteen digits reads 16,777,216, and 2.56 in a column of
    /// five digits - 256 without its point, a multiple of 256 - reads 655.36.
    /// </summary>
    [Fact]
    public void a_value_in_the_earlier_byte_order_that_reads_within_its_precision_is_not_listed()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var prices = Prices(db);
            prices.Insert(new Row { ["Id"] = 1, ["Price"] = 111.11m, ["Quantity"] = 123_456_789m });
        }
        StoreInEarlierByteOrder(111.11m, 5, 2, meant: 2.56m);
        StoreInEarlierByteOrder(123_456_789m, 18, 0, meant: 1m);

        using var read = Database.Open(_path);
        Assert.Empty(read.FindDecimalsBeyondPrecision());

        var row = Assert.Single(read.GetTable("Prices").Rows.ToList());
        Assert.Equal(655.36m, row["Price"]);
        Assert.Equal(16_777_216m, row["Quantity"]);
    }

    /// <summary>
    /// Sixteen bytes can make a number of more than the 96 bits a <see cref="decimal"/> has, and Access reads of
    /// one: 2^100 in the earlier order has only its first word set. A read gives the number its low 96 bits make,
    /// here 0, as Access does (qcatco/exportmdb.interop#74; <see cref="WideDecimalTests"/>). It is listed, though
    /// 0 is a number the column holds: no number Access wrote runs past 96 bits.
    /// </summary>
    [Fact]
    public void a_value_whose_number_runs_past_96_bits_is_listed_with_the_low_96_bits_a_read_gives()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            Prices(db).Insert(new Row { ["Id"] = 1, ["Price"] = 111.11m });
        StoreInEarlierByteOrder(111.11m, 5, 2, BigInteger.Pow(2, 100), negative: false);

        using var read = Database.Open(_path);
        var found = Assert.Single(read.FindDecimalsBeyondPrecision());

        Assert.Equal(1, found.PrimaryKey["Id"]);
        Assert.True(found.IsCut);
        Assert.Equal(0.00m, found.Value);
        Assert.Null(found.ValueInEarlierByteOrder);
        Assert.Equal(found.Value, read.GetTable("Prices").Rows.ToList()[0]["Price"]);
        Assert.Equal("Prices.Price, row 0 (Id=1): reads 0.00 (the low 96 bits of a longer number), more than a decimal holds in the earlier byte order",
                     found.ToString());
    }

    /// <summary>
    /// No Decimal in the corpus is beyond its column's precision: what is listed elsewhere is not something Access
    /// writes. The corpus has thirteen tables with a Decimal column - "test" in the fixedNumeric files, Table7 and
    /// Table7_desc in the indexCodes files, and Table1 in calcFieldV2010 - and 180 values in them.
    /// </summary>
    [Fact]
    public void no_decimal_in_the_corpus_is_listed()
    {
        var listed = new List<string>();
        var seen = new List<string>();
        int values = 0;
        foreach (string file in TestCorpus.Files("*.*", "V2000", "V2003", "V2007", "V2010", "V2019"))
        {
            Database db;
            try { db = Database.Open(file); }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException or InvalidOperationException) { continue; }   // not one this library opens
            using (db)
            {
                foreach (string name in db.GetTableNames())
                {
                    var table = db.GetTable(name);
                    var decimals = table.Columns.Where(c => c.DataType == DataType.Numeric && !c.IsCalculated).ToList();
                    if (decimals.Count == 0) continue;

                    seen.Add($"{Path.GetFileName(file)}:{name}");
                    values += table.Rows.Sum(row => decimals.Count(c => row.TryGetValue(c.Name, out var value) && value is not null));
                    listed.AddRange(table.FindDecimalsBeyondPrecision().Select(f => $"{Path.GetFileName(file)}: {f}"));
                }
            }
        }

        Assert.True(seen.Count >= 10 && values >= 50, $"only {seen.Count} tables with a Decimal column, {values} values in them: {string.Join(", ", seen)}");
        Assert.Empty(listed);
    }
}
