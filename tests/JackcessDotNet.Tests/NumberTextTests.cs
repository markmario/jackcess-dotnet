using System.Globalization;
using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Text given for a Double or a Single, and a double or a float given for text, taken as Access takes them, the
/// same on every target (#124). <c>Assets/ace-text-to-float.tsv</c> is what ACE 16 and DAO stored, on a machine
/// whose settings are en-AU (<c>tools/ace-text-to-float.ps1</c>): for text at and next to the half between two
/// doubles, or two singles, of every range, the double of its first 20 significant digits and the single of its
/// first 12, a tie to the even significand; for a number put in a Text column, 15 significant digits for a double
/// and 7 for a single, a half away from zero. The runtime's own parse rounded every digit on .NET, and on .NET
/// Framework took some text to a third double in a 64-bit process and another in a 32-bit one, and its float
/// through a double; its own text was the round-trip form on .NET and 1E-05 for 0.00001 on .NET Framework.
/// </summary>
public sealed class NumberTextTests : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"number_text_{Guid.NewGuid():N}.accdb");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static List<string[]> Lines(string kind)
    {
        var lines = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Assets", "ace-text-to-float.tsv")).ToList();
        Assert.StartsWith("# culture en-AU;", lines[0]);
        return lines.Where(l => l.StartsWith(kind + "\t", StringComparison.Ordinal)).Select(l => l.Split('\t')).ToList();
    }

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16", Invariant);
    private static string Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString("X8", Invariant);

    // What this library stores for text in a column of the type: the bits, or "refused".
    private static string Stored(DataType type, string text)
    {
        try
        {
            object number = RowValues.NumberOf(type, text);
            return type == DataType.Single ? Bits((float)number) : Bits((double)number);
        }
        catch (Exception e) when (e is FormatException or OverflowException) { return "refused"; }
    }

    private static T InCulture<T>(string name, Func<T> work)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(name);
        try { return work(); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    /// <summary>
    /// Every text of the file is stored in a Double and a Single column as ACE stored it, as a literal in an
    /// <c>INSERT</c>, and as a parameter and DAO stored it too - but for some text of doubles from 1E-308 to 1E-304
    /// and under 1E-321, where ACE stored the double one place from the one its first 20 digits are nearest: ACE's
    /// arithmetic there was not found, and this library stores the nearest (#136).
    /// </summary>
    [Fact]
    public void Text_is_stored_in_a_Double_and_a_Single_column_as_ACE_and_DAO_store_it()
    {
        var rows = Lines("text");
        Assert.True(rows.Count > 2000, $"{rows.Count} texts");
        var wrong = new List<string>();
        int leastDoubles = 0;
        InCulture("en-AU", () =>
        {
            foreach (var r in rows)
            {
                string text = r[3];
                string[] aceDouble = { r[5], r[6], r[7] }, aceSingle = { r[11], r[12], r[13] };
                string expectedDouble = aceDouble[0].StartsWith("refused", StringComparison.Ordinal) ? "refused" : aceDouble[0];
                string expectedSingle = aceSingle[0].StartsWith("refused", StringComparison.Ordinal) ? "refused" : aceSingle[0];
                // A literal, a parameter and DAO store one number, and refuse alike.
                if (aceDouble.Any(a => (a.StartsWith("refused", StringComparison.Ordinal) ? "refused" : a) != expectedDouble)
                    || aceSingle.Any(a => (a.StartsWith("refused", StringComparison.Ordinal) ? "refused" : a) != expectedSingle))
                    wrong.Add($"{text}: ACE's literal, parameter and DAO differ");

                string mine = Stored(DataType.Double, text);
                if (mine != expectedDouble)
                {
                    double magnitude = mine == "refused" ? double.NaN : Math.Abs(BitConverter.Int64BitsToDouble(long.Parse(mine, NumberStyles.HexNumber, Invariant)));
                    bool least = mine != "refused" && expectedDouble != "refused"
                        && Math.Abs(long.Parse(mine, NumberStyles.HexNumber, Invariant) - long.Parse(expectedDouble, NumberStyles.HexNumber, Invariant)) == 1
                        && (magnitude < 1E-321 || (magnitude >= 1E-308 && magnitude < 1E-304));
                    if (least) leastDoubles++;
                    else wrong.Add($"Double {r[1]} {r[2]} {text}: stored {mine}, ACE {expectedDouble}");
                }
                string mineSingle = Stored(DataType.Single, text);
                if (mineSingle != expectedSingle) wrong.Add($"Single {r[1]} {r[2]} {text}: stored {mineSingle}, ACE {expectedSingle}");
            }
            return 0;
        });
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(50)));
        // 88 of the file's texts, every one in those ranges.
        Assert.Equal(88, leastDoubles);
    }

    /// <summary>
    /// The text of the issue, an exact half, is stored as the double ACE stores, -8563549392855794, the even one:
    /// .NET Framework's parse in a 64-bit process gave -8563549392855793. Through a row, its index and a cursor.
    /// </summary>
    [Fact]
    public void The_half_of_the_issue_is_stored_and_keyed_as_ACE_stores_it()
    {
        using var db = Database.Create(_path, FileFormat.Access2016);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.Double), new ColumnDefinition("S", DataType.Single) }, new[] { "Id" });
        table.AddIndex(new IndexDefinition("ixD", "D"));
        InCulture("en-AU", () =>
        {
            table.Insert(new Row { ["Id"] = 1, ["D"] = "-8.5635493928557935E15", ["S"] = "1.06355545164752418596521E+0" });
            return 0;
        });
        var row = table.Rows.Single();
        Assert.Equal("C33E6C80B793E2F2", Bits((double)row["D"]!));
        Assert.Equal("3F882296", Bits((float)row["S"]!));
        Assert.Equal(1, (int)table.CreateIndexCursor("ixD").FindRowByEntry(-8563549392855794d)!["Id"]!);
    }

    /// <summary>
    /// Text cut to its first 20 digits for a Double and 12 for a Single, as Access cuts it: the digits after those
    /// make no difference, however many there are, and the leading zeros are not among them.
    /// </summary>
    [Theory]
    [InlineData("1.063555451647524186", "3FF10452B8A4F022")]              // 19 digits, past the half: up
    [InlineData("1.0635554516475241859", "3FF10452B8A4F021")]             // 20 digits, short of it: down
    [InlineData("1.06355545164752418597", "3FF10452B8A4F021")]            // the 21st digit is not read
    [InlineData("8163849454763484.500000000000000000001", "433D00FA52C03DDC")] // a half, to even
    [InlineData("000000000000000000001234567890123456789012345", "44F056E0F36A6444")]
    [InlineData("-0", "8000000000000000")]
    [InlineData("-1E-400", "8000000000000000")]
    [InlineData("1.7976931348623158E+308", "7FEFFFFFFFFFFFFF")]
    [InlineData("1.79769313486232E+308", "refused")]
    public void A_Double_reads_the_first_20_digits_of_text(string text, string bits)
        => Assert.Equal(bits, InCulture("en-AU", () => Stored(DataType.Double, text)));

    [Theory]
    [InlineData("-70.3356513977", "C28CABDA")]                 // short of the half, -70.335651397705078125
    [InlineData("-70.33565139771", "C28CABDA")]                // past it in the 13th digit, which is not read
    [InlineData("-70.3356513978", "C28CABDB")]                 // past it in the 12th
    [InlineData("3.40282356779733661637539395458142568448E+38", "7F7FFFFF")]   // the half past the greatest
    [InlineData("3.4028235678E+38", "refused")]
    [InlineData("-0", "80000000")]
    public void A_Single_reads_the_first_12_digits_of_text(string text, string bits)
        => Assert.Equal(bits, InCulture("en-AU", () => Stored(DataType.Single, text)));

    /// <summary>
    /// A double and a float put in a Text column are written as ACE and DAO write them: 15 significant digits for a
    /// double and 7 for a single, a half away from zero, written out unless they are 10^15 (10^7) or more or have
    /// more places than that after the point.
    /// </summary>
    [Fact]
    public void A_double_and_a_float_are_written_as_text_as_ACE_and_DAO_write_them()
    {
        var rows = Lines("tostring");
        Assert.True(rows.Count > 50);
        var wrong = new List<string>();
        InCulture("en-AU", () =>
        {
            foreach (var r in rows)
            {
                bool single = r[1] == "single";
                object value = single
                    ? BitConverter.ToSingle(BitConverter.GetBytes(int.Parse(r[2], NumberStyles.HexNumber, Invariant)), 0)
                    : (object)BitConverter.Int64BitsToDouble(long.Parse(r[2], NumberStyles.HexNumber, Invariant));
                string ace = r[4];
                if (r[5] != ace || r[6] != ace) wrong.Add($"{r[2]}: ACE's parameter, its INSERT ... SELECT and DAO differ");
                string mine = RowValues.AsText(value);
                if (mine != ace) wrong.Add($"{r[1]} {r[2]} ({r[3]}): wrote {mine}, ACE {ace}");
                // DAO's Text property takes it as its field does; the property is converted in the invariant culture.
                string property = (string)PropertyMapWriter.Convert(value, DataType.Text, "P");
                if (property != r[7]) wrong.Add($"{r[1]} {r[2]} ({r[3]}): property {property}, DAO {r[7]}");
            }
            return 0;
        });
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// Text given as a Double or a Single property's value is stored as DAO stores it, read in the invariant culture
    /// as a property's text always is. The forms a property's text is not read in - a currency symbol, parentheses, a
    /// sign after the number - are refused with <see cref="ArgumentException"/>, where DAO stores them (#138).
    /// </summary>
    [Fact]
    public void Text_given_for_a_Double_or_a_Single_property_is_stored_as_DAO_stores_it()
    {
        var rows = Lines("property");
        Assert.True(rows.Count > 50);
        var wrong = new List<string>();
        foreach (var r in rows)
        {
            foreach (var (type, dao) in new[] { (DataType.Double, r[5]), (DataType.Single, r[7]) })
            {
                string mine;
                try
                {
                    object value = PropertyMapWriter.Convert(r[3], type, "P");
                    mine = type == DataType.Single ? Bits((float)value) : Bits((double)value);
                }
                catch (ArgumentException) { mine = "refused"; }
                string expected = r[3] is "(1.5)" or "$1.5" or "1.5-" or "-$1.5" ? "refused"
                                : dao.StartsWith("refused", StringComparison.Ordinal) ? "refused" : dao;
                if (mine != expected) wrong.Add($"{type} {r[1]} {r[2]} {r[3]}: {mine}, DAO {expected}");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// A row given a double for a Text column holds Access's text for it, its index is keyed with that text, and the
    /// double finds it, by a scan and through the index alike.
    /// </summary>
    [Fact]
    public void A_double_written_to_a_Text_column_is_found_by_the_double()
    {
        using var db = Database.Create(_path, FileFormat.Access2016);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("T", DataType.Text) { MaxLength = 255 } }, new[] { "Id" });
        table.AddIndex(new IndexDefinition("ixT", "T"));
        double sum = 0.1 + 0.2;
        InCulture("en-AU", () =>
        {
            table.Insert(new Row { ["Id"] = 1, ["T"] = sum });
            table.Insert(new Row { ["Id"] = 2, ["T"] = 1e-5 });
            table.Insert(new Row { ["Id"] = 3, ["T"] = 16777216f });
            Assert.Equal(new[] { "0.3", "0.00001", "1.677722E+07" }, table.Rows.OrderBy(r => (int)r["Id"]!).Select(r => (string)r["T"]!));
            Assert.Equal(1, (int)table.CreateIndexCursor("ixT").FindRow("T", sum)!["Id"]!);
            Assert.Equal(2, (int)table.CreateCursor().FindRow("T", 1e-5)!["Id"]!);
            return 0;
        });
    }

    /// <summary>
    /// The digits of text are read as the runtime reads them in every culture it has: number and currency forms,
    /// group and decimal separators - a no-break space among them typed as a space too - signs and parentheses. Each
    /// number has nine digits or fewer, which every runtime reads exactly, so the digits read here must make the
    /// runtime's double, and every text the runtime reads must be read to its end.
    /// </summary>
    [Fact]
    public void Text_in_every_culture_is_read_as_the_runtime_reads_it()
    {
        var wrong = new List<string>();
        double[] numbers = { 1234567.25, -0.0625, 1.5E-7, -98765.4321, 0, 1E+20, 7 };
        string[] formats = { "N", "N4", "C", "C0", "G", "E3", "F2", "R" };
        int read = 0;
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            foreach (double number in numbers)
                foreach (string format in formats)
                {
                    string formatted = number.ToString(format, culture);
                    foreach (string text in new[] { formatted, formatted.Replace(' ', ' ').Replace(' ', ' ') }.Distinct())
                    {
                        double runtime;
                        try { runtime = double.Parse(text, NumberStyles.Any, culture); }
                        catch (FormatException) { continue; }   // a form the runtime does not read back
                        read++;
                        if (!ExactNumber.TryRead(text, NumberStyles.Any, culture.NumberFormat, out ExactNumber exact))
                            wrong.Add($"{culture.Name} {format} '{text}': not read to its end");
                        else if (exact.NearestDouble() is not double mine || (Bits(mine) != Bits(runtime) && !(mine == 0 && runtime == 0)))
                            wrong.Add($"{culture.Name} {format} '{text}': {exact.NearestDouble()}, the runtime {runtime}");
                    }
                }
        }
        Assert.True(read > 1000, $"{read} texts read");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(40)));
    }

    [Theory]
    [InlineData("fr-FR", "1 234,5", 1234.5)]
    [InlineData("fr-FR", "-1 234 567,25 €", -1234567.25)]
    [InlineData("de-DE", "1.234,5", 1234.5)]
    [InlineData("en-US", "($1,234.50)", -1234.5)]
    [InlineData("en-US", "1.5-", -1.5)]
    public void Text_with_a_culture_s_separators_is_read_whole(string culture, string text, double expected)
    {
        var info = new CultureInfo(culture);
        try { double.Parse(text, NumberStyles.Any, info); }
        catch (FormatException) { return; }   // a form this runtime's culture data does not take
        Assert.True(ExactNumber.TryRead(text, NumberStyles.Any, info.NumberFormat, out ExactNumber exact), "not read to its end");
        Assert.Equal(expected, exact.NearestDouble());
        Assert.Equal(expected, NumberText.ToDouble(text, NumberStyles.Any, info));
    }
}
