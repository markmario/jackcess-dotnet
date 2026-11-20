using System.Globalization;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A number with more places than its Decimal column holds (#80). <c>Assets/ace-decimal-places.tsv</c> is what ACE 16
/// stored for 5,080 such numbers an <c>INSERT</c> gave it as typed parameters - a Decimal, a Double, a Single and
/// text - in columns of every scale from 0 to 28, both signs (<c>tools/ace-decimal-places.ps1</c>, in an .mdb; an
/// .accdb stores the same): the places past the scale cut off, toward zero, every one, and a negative number cut to
/// nothing a negative zero. DAO refuses every one of them ("Scaling of decimal value resulted in data truncation").
/// ACE is the referee, as it is of every value this library writes: what it stores for a number is what a row
/// written with that number must hold, and DAO's refusal is the stricter engine's, which ACE's own writes do not
/// share. This library rounded half away from zero.
/// <para>
/// ACE's <c>=</c> with the same parameter compares it with the stored value exactly, through the column's index and
/// without one: it finds the row only where the number given is the number stored - none of these but a double
/// whose 15 significant digits leave no place past the scale. A lookup here does the same. (A negative zero is not among
/// these lookups: ACE's index tells it from zero, and its scan does not. A lookup here takes it for zero.)
/// </para>
/// </summary>
public sealed class DecimalPlacesTests : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"decimal_places_{Guid.NewGuid():N}.mdb");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private sealed class Measured
    {
        internal Measured(int scale, string kind, string text, string bits, string stored, bool found)
        {
            Scale = scale; Kind = kind; Text = text; Bits = bits; Stored = stored; Found = found;
        }

        internal int Scale { get; }
        internal string Kind { get; }
        internal string Text { get; }
        internal string Bits { get; }
        internal string Stored { get; }
        internal bool Found { get; }
    }

    private static List<Measured> AceWrites()
    {
        var measured = new List<Measured>();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Assets", "ace-decimal-places.tsv")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            Assert.Equal(parts[5], parts[6]);   // ACE's = finds the same through the index and without one
            measured.Add(new Measured(int.Parse(parts[0], Invariant), parts[1], parts[2], parts[3], parts[4], parts[5] == "yes"));
        }
        return measured;
    }

    private static object Number(Measured m) => m.Kind switch
    {
        "decimal" => decimal.Parse(m.Text, Invariant),
        // The very double or single ACE was given: .NET Framework's parse of the text is not always the nearest.
        "double"  => BitConverter.Int64BitsToDouble(long.Parse(m.Bits, NumberStyles.HexNumber, Invariant)),
        "single"  => BitConverter.ToSingle(BitConverter.GetBytes(int.Parse(m.Bits, NumberStyles.HexNumber, Invariant)), 0),
        _         => m.Text,
    };

    // A stored value as the .tsv writes it: "-0" before a negative zero's digits.
    private static string Written(decimal value)
        => (value == 0m && decimal.GetBits(value)[3] < 0 ? "-" : "") + value.ToString(Invariant);

    [Fact]
    public void A_number_with_more_places_than_its_column_holds_is_stored_as_ACE_stores_it()
    {
        var measured = AceWrites();
        Assert.Equal(5080, measured.Count);
        Assert.DoesNotContain(measured, m => m.Stored == "refused");

        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = Invariant;   // the text is written with a point
        try
        {
            using (var db = Database.Create(_path, FileFormat.Access2000))
                foreach (var scale in measured.GroupBy(m => m.Scale))
                {
                    var table = db.CreateTable("T" + scale.Key, new[]
                    {
                        new ColumnDefinition("Id", DataType.Int32),
                        new ColumnDefinition("D", DataType.Decimal) { Precision = 28, Scale = (byte)scale.Key },
                    }, new[] { "Id" });
                    int id = 0;
                    foreach (var m in scale) table.Insert(new Row { ["Id"] = ++id, ["D"] = Number(m) });
                }

            using var read = Database.Open(_path);
            var wrong = new List<string>();
            foreach (var scale in measured.GroupBy(m => m.Scale))
            {
                var table = read.GetTable("T" + scale.Key);
                var rows = table.Rows.ToDictionary(r => (int)r["Id"]!, r => (decimal)r["D"]!);
                int id = 0;
                foreach (var m in scale)
                {
                    string stored = Written(rows[++id]);
                    if (stored != m.Stored) wrong.Add($"scale {m.Scale}, {m.Kind} {m.Text}: stored {stored}, ACE {m.Stored}");
                    bool found = table.CreateCursor().FindRow(new Dictionary<string, object?> { ["Id"] = id, ["D"] = Number(m) }) is not null;
                    if (found != m.Found) wrong.Add($"scale {m.Scale}, {m.Kind} {m.Text}: found {found}, ACE {m.Found}");
                }
            }
            Assert.True(wrong.Count == 0, $"{wrong.Count} differ from ACE:\n" + string.Join("\n", wrong.Take(20)));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData("1.015", 2, "1.01")]
    [InlineData("-1.015", 2, "-1.01")]
    [InlineData("2.5", 0, "2")]
    [InlineData("-2.5", 0, "-2")]
    [InlineData("0.999", 2, "0.99")]
    [InlineData("-0.001", 2, "-0.00")]
    [InlineData("0.001", 2, "0.00")]
    [InlineData("-0.00", 2, "0.00")]
    [InlineData("12345678901234567890.12345678", 4, "12345678901234567890.1234")]
    public void The_places_past_the_scale_are_cut_off_toward_zero(string given, int scale, string stored)
    {
        decimal value = decimal.Parse(given, Invariant);
        Assert.Equal(stored, Written(NumericCodec.AsStored(value, scale)));
        byte[] bytes = NumericCodec.Encode(NumericCodec.AsStored(value, scale), 28, scale, "D");
        Assert.Equal(stored, Written(NumericCodec.Decode(bytes, 0, scale)!.Value));
    }

    // The update path takes a number as the insert does, and so does the row's check before it is written.
    [Fact]
    public void An_update_and_a_validation_take_such_a_number_as_an_insert_does()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("D", DataType.Decimal) { Precision = 5, Scale = 2 },
        }, new[] { "Id" });
        table.Insert(new Row { ["Id"] = 1, ["D"] = 1m });
        table.UpdateByPrimaryKey(1, new Row { ["D"] = 999.999m });   // 999.99: five digits, as the column holds
        Assert.Equal(999.99m, (decimal)table.Rows.Single()["D"]!);
        table.Validate(new Row { ["Id"] = 2, ["D"] = -999.999 });
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Validate(new Row { ["Id"] = 2, ["D"] = 1000.001m }));
    }
}
