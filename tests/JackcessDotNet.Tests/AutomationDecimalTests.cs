using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A double or a float as a decimal, as OLE Automation makes one (<see cref="AutomationDecimal"/>): rounded to 15
/// and to 7 significant digits, which is how Access takes such a number to a Decimal and how
/// <see cref="Convert.ToDecimal(double)"/> converts on .NET Framework and .NET up to 10. .NET 11's conversion is
/// exact, and the library rounds for itself there: the same numbers on every runtime (#49).
/// </summary>
public sealed class AutomationDecimalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"automation_decimal_{Guid.NewGuid():N}.accdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // Whether this runtime's own conversion is the one OLE Automation makes, to hold the library's to it.
    private static bool RuntimeRounds => Convert.ToDecimal(0.1) == 0.1m;

    [Fact]
    public void A_double_and_a_float_are_the_decimals_access_makes_of_them_on_every_runtime()
    {
        Assert.Equal(0.1m, AutomationDecimal.From(0.1));
        Assert.Equal(-0.1m, AutomationDecimal.From(-0.1));
        Assert.Equal(1.2346m, AutomationDecimal.From(1.2346));
        Assert.Equal(0.3m, AutomationDecimal.From(0.1 + 0.2));
        Assert.Equal(5m, AutomationDecimal.From(5.000000000000001));
        Assert.Equal(562949953421312m, AutomationDecimal.From(562949953421312.5));   // the 15th digit, half to even
        Assert.Equal(1152921504606850000m, AutomationDecimal.From((double)(1L << 60)));
        Assert.Equal(0m, AutomationDecimal.From(1e-30));
        Assert.Equal(0m, AutomationDecimal.From(-0.0));
        Assert.Equal(0.1m, AutomationDecimal.From(0.1f));
        Assert.Equal(1.2346m, AutomationDecimal.From(1.2346f));
        Assert.Equal(16777220m, AutomationDecimal.From(16777217f));
        Assert.Throws<OverflowException>(() => AutomationDecimal.From(1e30));
        Assert.Throws<OverflowException>(() => AutomationDecimal.From(double.NaN));
        Assert.Throws<OverflowException>(() => AutomationDecimal.From(double.PositiveInfinity));
        Assert.Equal(1.5m, AutomationDecimal.From("1.5", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(7m, AutomationDecimal.From(7L, null));

        // The rounding the library does itself is these numbers on every runtime, whichever conversion it uses.
        Assert.Equal(0.1m, AutomationDecimal.Rounded(0.1, 15));
        Assert.Equal(0.3m, AutomationDecimal.Rounded(0.1 + 0.2, 15));
        Assert.Equal(562949953421312m, AutomationDecimal.Rounded(562949953421312.5, 15));
        Assert.Equal(0.1m, AutomationDecimal.Rounded(0.1f, 7));
    }

    [Fact]
    public void The_rounding_is_the_conversion_of_a_runtime_that_rounds()
    {
        // Held to the runtime's own conversion where it rounds (.NET Framework, .NET up to 10): on .NET 11 there is
        // nothing to hold it to, and the same rounding is what is used.
        if (!RuntimeRounds) return;

        var random = new Random(4911);
        var bytes = new byte[8];
        int compared = 0;
        for (int i = 0; i < 300_000; i++)
        {
            double value;
            switch (i % 3)
            {
                case 0:   // any bits, of a size a decimal holds
                    random.NextBytes(bytes);
                    value = BitConverter.ToDouble(bytes, 0);
                    if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) >= 7.9e28) continue;
                    break;
                case 1:   // a number of a few digits, as a caller writes one
                    value = Math.Round(random.NextDouble() * Math.Pow(10, random.Next(-6, 16)), random.Next(0, 8));
                    break;
                default:  // one ending in 5 at a place the rounding comes to
                    value = (random.Next(1, 1_000_000) * 10 + 5) * Math.Pow(10, -random.Next(1, 20));
                    break;
            }
            Assert.True(Convert.ToDecimal(value) == AutomationDecimal.Rounded(value, 15),
                $"{value:R}: the runtime makes {Convert.ToDecimal(value)}, the library {AutomationDecimal.Rounded(value, 15)}");
            float single = (float)value;
            if (!float.IsInfinity(single) && Math.Abs(single) < 7.9e28f)
                Assert.True(Convert.ToDecimal(single) == AutomationDecimal.Rounded(single, 7),
                    $"{single:R}f: the runtime makes {Convert.ToDecimal(single)}, the library {AutomationDecimal.Rounded(single, 7)}");
            compared++;
        }
        Assert.True(compared > 200_000, $"only {compared} numbers were compared");
    }

    [Fact]
    public void A_double_written_to_a_Decimal_is_stored_as_access_stores_it_and_found_by_that_double()
    {
        using var db = Database.Create(_path, FileFormat.Access2010);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("D", DataType.Decimal) { Precision = 28, Scale = 20 },
        }, "Id");
        table.Insert(new Row { ["Id"] = 1, ["D"] = 0.1 });
        table.Insert(new Row { ["Id"] = 2, ["D"] = 0.1f });
        table.Insert(new Row { ["Id"] = 3, ["D"] = 0.1 + 0.2 });

        // Kept as the decimal Access makes of the number, not the double's binary expansion.
        Assert.Equal(new[] { 0.1m, 0.1m, 0.3m }, table.Rows.Select(r => (decimal)r["D"]!));
        var cursor = table.CreateCursor();
        Assert.Equal(1, cursor.FindRow("D", 0.1)!["Id"]);
        Assert.Equal(2, cursor.FindNextRow("D", 0.1f)!["Id"]);
        Assert.Equal(3, table.CreateCursor().FindRow("D", 0.1 + 0.2)!["Id"]);
    }
}
