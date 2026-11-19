using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Which row a lookup takes for the value asked for (#49), by a cursor over the table and through an index alike,
/// through the public API. Text is matched as text and never through a number it reads as; a number as the same
/// number, across number types. What each case expects is what ACE's <c>=</c> gave for it (with a parameter of
/// the caller's type, through an index and without one), and, for a Currency, what DAO's <c>Seek</c> gave too:
/// the measurement is in the pull request for #49, and qcatco/exportmdb.interop's suite has ACE referee the cases
/// in a file this library wrote.
/// </summary>
public sealed class LookupMatchTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lookup_match_{Guid.NewGuid():N}");
    private string? _file;

    public void Dispose()
    {
        if (_file is not null && File.Exists(_file)) File.Delete(_file);
    }

    private Database Create(FileFormat format)
    {
        _file = _path + (format == FileFormat.Access2000 ? ".mdb" : ".accdb");
        return Database.Create(_file, format);
    }

    public static IEnumerable<object[]> Formats() => new[] { new object[] { FileFormat.Access2000 }, new object[] { FileFormat.Access2010 } };

    // The ids of every row a cursor over the table finds with the value, and every row the index has for it.
    private static int[] ByScan(Table table, string column, object value)
    {
        var ids = new List<int>();
        var cursor = table.CreateCursor();
        for (var row = cursor.FindRow(column, value); row is not null; row = cursor.FindNextRow(column, value))
            ids.Add((int)row["Id"]!);
        return ids.OrderBy(id => id).ToArray();
    }

    private static int[] ByIndex(Table table, string index, object value)
        => table.CreateIndexCursor(index).FindRowsByEntry(value).Select(row => (int)row["Id"]!).OrderBy(id => id).ToArray();

    [Theory]
    [MemberData(nameof(Formats))]
    public void Text_that_reads_as_the_same_number_is_other_text_by_a_scan_and_through_an_index(FileFormat format)
    {
        using var db = Create(format);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Code", DataType.Text) { MaxLength = 20 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") });
        table.Insert(new Row { ["Id"] = 1, ["Code"] = "1" });
        table.Insert(new Row { ["Id"] = 2, ["Code"] = "5" });

        // Each of these is the number 1 or 5 to Convert.ToDecimal, and other text to Access.
        foreach (string asked in new[] { "1.0", "01", "+1", " 1", "1.00", " 5", "5.0", "005" })
        {
            Assert.Empty(ByScan(table, "Code", asked));
            Assert.Empty(ByIndex(table, "ByCode", asked));
        }

        // With a row of its own, each finds that row, and the row it reads as the same number as does not.
        table.Insert(new Row { ["Id"] = 3, ["Code"] = "1.0" });
        table.Insert(new Row { ["Id"] = 4, ["Code"] = " 5" });
        Assert.Equal(new[] { 3 }, ByScan(table, "Code", "1.0"));
        Assert.Equal(new[] { 3 }, ByIndex(table, "ByCode", "1.0"));
        Assert.Equal(new[] { 4 }, ByScan(table, "Code", " 5"));
        Assert.Equal(new[] { 4 }, ByIndex(table, "ByCode", " 5"));
        Assert.Equal(new[] { 1 }, ByScan(table, "Code", "1"));
        Assert.Equal(new[] { 1 }, ByIndex(table, "ByCode", "1"));

        // A space at the end is no difference to Access's =, and a number looked for is the text that writes it.
        Assert.Equal(new[] { 1 }, ByScan(table, "Code", "1 "));
        Assert.Equal(new[] { 1 }, ByIndex(table, "ByCode", "1 "));
        Assert.Equal(new[] { 1 }, ByScan(table, "Code", 1));
        Assert.Equal(new[] { 1 }, ByIndex(table, "ByCode", 1.0));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_row_is_found_updated_and_deleted_by_its_own_text_key_where_another_row_s_key_reads_as_the_same_number(FileFormat format)
    {
        string[] keys = { "1", "1.0", "01", "+1", " 1" };   // the number 1, five times, to a comparison through decimals
        using (var db = Create(format))
        {
            var table = db.CreateTable("K", new[]
            {
                new ColumnDefinition("Code", DataType.Text) { MaxLength = 10 }, new ColumnDefinition("Value", DataType.Int32),
            }, "Code");
            for (int i = 0; i < keys.Length; i++) table.Insert(new Row { ["Code"] = keys[i], ["Value"] = i });

            foreach (string key in keys)
                Assert.Equal(key, table.FindRowByPrimaryKey(key)!["Code"]);

            // Updated and deleted by the row its own key finds, by the table and by a cursor over either kind.
            var row = table.FindRowByPrimaryKey("1.0")!;
            row["Value"] = 100;
            table.Update(row);
            table.Delete(table.FindRowByPrimaryKey("01")!);

            var byIndex = table.CreateIndexCursor();
            Assert.Equal("+1", byIndex.FindRowByEntry("+1")!["Code"]);
            byIndex.UpdateCurrentRow(new Row { ["Value"] = 300 });
            var byScan = table.CreateCursor();
            Assert.Equal(" 1", byScan.FindRow("Code", " 1")!["Code"]);
            byScan.DeleteCurrentRow();
        }

        using (var db = Database.Open(_file!))
        {
            var table = db.GetTable("K");
            Assert.Equal(new[] { "+1=300", "1.0=100", "1=0" },
                table.Rows.Select(r => $"{r["Code"]}={r["Value"]}").OrderBy(s => s, StringComparer.Ordinal));
            Assert.Null(table.FindRowByPrimaryKey("01"));
            Assert.Null(table.FindRowByPrimaryKey(" 1"));
            Assert.Equal(1, IndexChecks.AssertEveryIndexHoldsTheRows(db, "K", expectedRows: 3));
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_double_looked_for_in_a_Currency_is_its_exact_value_to_four_places_half_to_even_as_Access_takes_it(FileFormat format)
    {
        // Measured (ACE's = through an index and without, DAO's Seek, all alike): a double or a float by its exact
        // binary value, rounded to ten-thousandths, half to even. Taken through a decimal it was cut to 15 significant
        // digits first (on .NET Framework and .NET up to 10), so 0.00015 - just under the half it is written as - was
        // the half, and was rounded away from zero to the row above.
        using var db = Create(format);
        var table = db.CreateTable("K", new[]
        {
            new ColumnDefinition("C", DataType.Currency), new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
        }, "C");
        decimal[] held = { 0.0001m, 0.0002m, 1.2345m, 1.2346m, 0.0312m, 0.0313m, -0.0312m, -0.0313m, 0.0937m, 0.0938m };
        foreach (decimal c in held) table.Insert(new Row { ["C"] = c, ["Name"] = c.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        var asked = new (object Value, decimal? Finds)[]
        {
            (0.00015, 0.0001m), (1.234549999999999, 1.2345m), (0.000150000000000000001, 0.0002m),   // either side of a half
            (0.03125, 0.0312m), (-0.03125, -0.0312m), (0.09375, 0.0938m),                           // exact halves, to even
            (0.03125f, 0.0312m), (-0.03125f, -0.0312m),
            (0.00025, null),                                                                         // just over the half: 0.0003
            (double.NaN, null), (1e30, null), (double.NegativeInfinity, null),                       // no Currency
        };
        foreach (var (value, finds) in asked)
        {
            string what = $"C = {value} ({value.GetType().Name})";
            Assert.True(finds == (decimal?)table.FindRowByPrimaryKey(value)?["C"], $"{what} by the primary key: {table.FindRowByPrimaryKey(value)?["C"]}");
            Assert.True(finds == (decimal?)table.CreateCursor().FindRow("C", value)?["C"], $"{what} by a scan: {table.CreateCursor().FindRow("C", value)?["C"]}");
            Assert.Equal(finds is null ? Array.Empty<decimal>() : new[] { finds.Value },
                table.CreateIndexCursor().FindRowsByEntry(value).Select(r => (decimal)r["C"]!));
        }

        // An update and a delete by those lookups change the rows Access's would.
        var row = table.FindRowByPrimaryKey(0.00015)!;
        row["Name"] = "changed";
        table.Update(row);
        table.Delete(table.FindRowByPrimaryKey(1.234549999999999)!);
        var cursor = table.CreateCursor();
        cursor.FindRow("C", 0.03125);
        cursor.DeleteCurrentRow();
        Assert.Equal("changed", table.FindRowByPrimaryKey(0.0001m)!["Name"]);
        Assert.Equal("0.0002", table.FindRowByPrimaryKey(0.0002m)!["Name"]);
        Assert.Equal(held.Except(new[] { 1.2345m, 0.0312m }).OrderBy(c => c), table.Rows.Select(r => (decimal)r["C"]!).OrderBy(c => c));
    }

    [Fact]
    public void Numbers_are_matched_as_the_same_number_by_a_scan_and_through_an_index()
    {
        using var db = Create(FileFormat.Access2010);   // an .accdb, for the Large Number
        var table = db.CreateTable("N", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("I", DataType.Int16),
            new ColumnDefinition("L", DataType.Int32), new ColumnDefinition("B", DataType.Int64),
            new ColumnDefinition("C", DataType.Currency), new ColumnDefinition("D", DataType.Decimal) { Precision = 18, Scale = 4 },
            new ColumnDefinition("F", DataType.Double), new ColumnDefinition("S", DataType.Single),
        }, new[] { "Id" }, new[] { "I", "L", "B", "C", "F", "S" }.Select(c => new IndexDefinition("By" + c, c)));
        table.Insert(new Row { ["Id"] = 1, ["I"] = (short)5, ["L"] = 5, ["B"] = 1L << 60, ["C"] = 1.2346m, ["D"] = 1.5m, ["F"] = 0.1, ["S"] = 0.1f });
        table.Insert(new Row { ["Id"] = 2, ["I"] = (short)6, ["L"] = 6, ["B"] = 1234567890123460000L, ["C"] = 1.2345m, ["D"] = 0.1m, ["F"] = -0.0, ["S"] = 16777216f });
        table.Insert(new Row { ["Id"] = 3, ["I"] = (short)7, ["L"] = int.MaxValue, ["B"] = 1234567890123460096L, ["C"] = 562949953421312.5m, ["D"] = 1.2346m, ["F"] = 1.2346, ["S"] = 1.2346f });
        table.Insert(new Row { ["Id"] = 4, ["I"] = (short)0, ["L"] = 0, ["B"] = long.MinValue });

        var asked = new (string Column, object Value, int[] Finds)[]
        {
            // A whole-number column: the same whole number of any type, and no number that is not one.
            ("I", (short)5, new[] { 1 }), ("I", 5, new[] { 1 }), ("I", 5L, new[] { 1 }), ("I", 5.0, new[] { 1 }), ("I", 5m, new[] { 1 }),
            ("I", 5.5, Array.Empty<int>()), ("I", 70000, Array.Empty<int>()), ("I", 65541, Array.Empty<int>()),
            ("I", 5.000000000000001, Array.Empty<int>()), ("I", 6.5f, Array.Empty<int>()),
            ("L", 5.0, new[] { 1 }), ("L", 5.5, Array.Empty<int>()), ("L", 2147483647L, new[] { 3 }), ("L", 4294967301L, Array.Empty<int>()),
            // Where ACE's index and its scan differ (measured), the exact answer, which is the scan's: -0.0 is 0, and a
            // decimal with a fraction is no whole number - ACE's index finds nothing for -0.0, and rounds 5.5m to 6
            // and 0.1m to 0. NaN is no number, where ACE's scan finds every row.
            ("I", -0.0, new[] { 4 }), ("L", -0.0f, new[] { 4 }),
            ("I", 5.5m, Array.Empty<int>()), ("L", 0.1m, Array.Empty<int>()), ("I", -1.23465m, Array.Empty<int>()),
            ("I", double.NaN, Array.Empty<int>()), ("L", float.NaN, Array.Empty<int>()),
            // A Large Number: a double that is the number exactly finds it, past 15 significant digits too, and -2^63,
            // which ACE's index does not find (its scan does); 2^63 is no Large Number.
            ("B", 1L << 60, new[] { 1 }), ("B", (double)(1L << 60), new[] { 1 }), ("B", 1234567890123460096.0, new[] { 3 }),
            ("B", 1234567890123460000m, new[] { 2 }), ("B", 1234567890123460001m, Array.Empty<int>()),
            ("B", -9223372036854775808.0, new[] { 4 }), ("B", 9223372036854775808.0, Array.Empty<int>()),
            // A Currency: the number asked taken to the column's four places.
            ("C", 1.2346m, new[] { 1 }), ("C", 1.2346, new[] { 1 }), ("C", 1.23456m, new[] { 1 }), ("C", 1.23456, new[] { 1 }),
            ("C", 1.23455, new[] { 1 }), ("C", 1.23454m, new[] { 2 }), ("C", 1, Array.Empty<int>()), ("C", 562949953421312.5m, new[] { 3 }),
            // A double of more significant digits than 15 is its exact value, as ACE takes it: through a decimal of 15
            // digits it was 562949953421313, and found nothing.
            ("C", 562949953421312.5, new[] { 3 }),
            // A decimal at a half of the fifth place is taken half away from zero, as a row is written with it: 1.2347,
            // which no row holds, where ACE's index takes 1.2346 (#65).
            ("C", 1.23465m, Array.Empty<int>()),
            // A Double and a Single: the number asked as the column stores it, by its bits.
            ("F", 0.1, new[] { 1 }), ("F", 0.1m, new[] { 1 }), ("F", 0.1f, Array.Empty<int>()), ("F", -0.0, new[] { 2 }),
            ("F", 0.0, Array.Empty<int>()), ("F", 1.2346m, new[] { 3 }),
            ("S", 0.1f, new[] { 1 }), ("S", 0.1, new[] { 1 }), ("S", (double)0.1f, new[] { 1 }), ("S", 1.2346m, new[] { 3 }),
            ("S", 16777217.0, new[] { 2 }), ("S", 0f, Array.Empty<int>()),
            // Text is no number.
            ("I", "5", Array.Empty<int>()), ("C", "1.2346", Array.Empty<int>()),
        };
        foreach (var (column, value, finds) in asked)
        {
            string what = $"{column} = {value} ({value.GetType().Name})";
            Assert.True(finds.SequenceEqual(ByScan(table, column, value)), $"{what} by a scan: [{string.Join(", ", ByScan(table, column, value))}]");
            Assert.True(finds.SequenceEqual(ByIndex(table, "By" + column, value)), $"{what} through its index: [{string.Join(", ", ByIndex(table, "By" + column, value))}]");
        }

        // A Decimal has no index this library keeps: a scan, exactly as decimals compare - a caller's double taken
        // to a decimal as Access takes one, to 15 significant digits.
        foreach (var (value, finds) in new (object, int[])[]
                 {
                     (1.5m, new[] { 1 }), (1.50m, new[] { 1 }), (1.5, new[] { 1 }), (0.1, new[] { 2 }), (0.1f, new[] { 2 }),
                     (1.23456m, Array.Empty<int>()), (1.2346, new[] { 3 }), ("1.5", Array.Empty<int>()),
                 })
            Assert.True(finds.SequenceEqual(ByScan(table, "D", value)), $"D = {value} ({value.GetType().Name}): [{string.Join(", ", ByScan(table, "D", value))}]");
    }
}
