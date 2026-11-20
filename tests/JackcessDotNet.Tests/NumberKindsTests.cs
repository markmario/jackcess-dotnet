using System.Globalization;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A <see cref="bool"/>, text, a date and values of other kinds given for a number column (#75), every number type
/// following one rule, Access's. <c>Assets/ace-number-kinds.tsv</c> is what ACE 16 (typed parameters) and DAO
/// (<c>AddNew</c>) stored for each in a column of each type, on a machine whose settings are en-AU
/// (<c>tools/ace-number-kinds.ps1</c>):
/// <list type="bullet">
/// <item>True is -1 and False 0, in every type that holds -1. In a Byte, ACE wraps -1 to 255, as it wraps every
/// negative number, and DAO refuses it; this library refuses a number a Byte does not hold, -1 among them.</item>
/// <item>Text is read in the machine's locale, as a number of any of the forms "$5", "(5)", "1e2", " 7 " and "1,5"
/// (15 there) take, and taken to a whole number half to even ("2.5" is 2, "1.5" 2). Text that is no number - "True",
/// "abc", "", "1 000" - both refuse.</item>
/// <item>A date is its day number: 2 January 2020 12:00 is 43832.5 in a Single, a Double, a Currency and a Decimal,
/// 43832 in a Long Integer and a Large Number, and refused by an Integer (and, by DAO, a Byte; ACE wraps it).</item>
/// <item>A GUID both refuse, and bytes ACE refuses; this library refuses them, and a <see cref="char"/>, with
/// <see cref="ArgumentException"/> naming the column.</item>
/// </list>
/// The engine took True as 1, a date as no number in every type, and threw <see cref="InvalidCastException"/> for
/// a GUID, bytes or a date in every type but Currency.
/// </summary>
public sealed class NumberKindsTests : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"number_kinds_{Guid.NewGuid():N}.accdb");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static readonly Dictionary<string, ColumnDefinition> Types = new()
    {
        ["Byte"]        = new ColumnDefinition("V", DataType.Byte),
        ["Integer"]     = new ColumnDefinition("V", DataType.Int16),
        ["Long"]        = new ColumnDefinition("V", DataType.Int32),
        ["LargeNumber"] = new ColumnDefinition("V", DataType.Int64),
        ["Single"]      = new ColumnDefinition("V", DataType.Single),
        ["Double"]      = new ColumnDefinition("V", DataType.Double),
        ["Currency"]    = new ColumnDefinition("V", DataType.Currency),
        ["Decimal"]     = new ColumnDefinition("V", DataType.Decimal) { Precision = 18, Scale = 4 },
    };

    private static object Given(string kind, string text) => kind switch
    {
        "bool"  => text == "True",
        "text"  => text == "(empty)" ? "" : text,
        "date"  => new DateTime(2020, 1, 2, 12, 0, 0),
        "guid"  => new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
        "bytes" => new byte[] { 1, 2 },
        _       => throw new ArgumentException(kind),
    };

    /// <summary>What the engine stores, written as the .tsv writes ACE's: the value read back in the invariant culture, or why it refused.</summary>
    private static string Stored(Table table, int id)
        => Convert.ToString(table.Rows.Single(r => (int)r["Id"]! == id)["V"], Invariant)!;

    [Fact]
    public void Every_number_type_stores_a_bool_text_and_a_date_as_ACE_stores_them_and_refuses_what_it_refuses()
    {
        var lines = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Assets", "ace-number-kinds.tsv")).ToList();
        Assert.StartsWith("# culture en-AU;", lines[0]);
        var ace = lines.Skip(1).Select(l => l.Split('\t')).Where(p => p[0] == "ace").ToList();
        var dao = lines.Skip(1).Select(l => l.Split('\t')).Where(p => p[0] == "dao")
            .ToDictionary(p => (p[1], p[2], p[3]), p => p[4]);
        Assert.Equal(8 * 18, ace.Count);

        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-AU");   // the measuring machine's settings
        var wrong = new List<string>();
        try
        {
            using var db = Database.Create(_path, FileFormat.Access2016);
            foreach (var type in Types)
                db.CreateTable(type.Key, new[] { new ColumnDefinition("Id", DataType.Int32), type.Value }, new[] { "Id" });

            int id = 0;
            foreach (var p in ace)
            {
                string typeName = p[1], kind = p[2], text = p[3], aceStored = p[4];
                var table = db.GetTable(typeName);
                string mine;
                try
                {
                    table.Insert(new Row { ["Id"] = ++id, ["V"] = Given(kind, text) });
                    mine = Stored(table, id);
                }
                catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
                {
                    if (kind is "guid" or "bytes" && e.GetType() != typeof(ArgumentException))
                        wrong.Add($"{typeName} {kind} {text}: {e.GetType().Name}, not ArgumentException");
                    // A number past what its column holds is refused as it always was, by the conversion itself.
                    if (e is not OverflowException && !e.Message.Contains("'V'")) wrong.Add($"{typeName} {kind} {text}: the refusal does not name the column: {e.Message}");
                    mine = "refused";
                }

                // Where ACE and DAO differ - ACE wraps a negative number, and a date, into a Byte; DAO refuses them -
                // this library refuses, as DAO does.
                string expected = aceStored.StartsWith("refused") ? "refused"
                                : dao[(typeName, kind, text)].StartsWith("refused") ? "refused"
                                : aceStored;
                if (Comparable(mine) != Comparable(expected))
                    wrong.Add($"{typeName} {kind} {text}: stored {mine}, ACE {aceStored}, DAO {dao[(typeName, kind, text)]}");
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // A number as both write it: 12 and 12.0000 are one number.
    private static string Comparable(string value)
        => decimal.TryParse(value, NumberStyles.Float, Invariant, out decimal number) ? number.ToString("0.####", Invariant) : value;

    [Theory]
    [InlineData(DataType.Byte)]
    [InlineData(DataType.Int16)]
    [InlineData(DataType.Int32)]
    [InlineData(DataType.Int64)]
    [InlineData(DataType.Single)]
    [InlineData(DataType.Double)]
    [InlineData(DataType.Currency)]
    [InlineData(DataType.Decimal)]
    public void A_char_a_Guid_and_bytes_are_refused_with_ArgumentException_naming_the_column_and_nothing_is_written(DataType type)
    {
        using var db = Database.Create(_path, FileFormat.Access2016);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Amount", type) }, new[] { "Id" });
        foreach (object value in new object[] { 'a', Guid.NewGuid(), new byte[] { 1 }, TimeSpan.FromHours(1) })
        {
            var refused = Assert.Throws<ArgumentException>(() => table.Insert(new Row { ["Id"] = 1, ["Amount"] = value }));
            Assert.Contains("'Amount'", refused.Message);
            Assert.Throws<ArgumentException>(() => table.Validate(new Row { ["Id"] = 1, ["Amount"] = value }));
        }
        Assert.Equal(0, table.RowCount);
    }

    // A bool and a date are keyed as the row is written with them.
    [Fact]
    public void A_row_written_with_true_is_found_by_minus_one_through_its_index()
    {
        using var db = Database.Create(_path, FileFormat.Access2016);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("N", DataType.Int32) }, new[] { "Id" });
        table.AddIndex(new IndexDefinition("ixN", "N"));
        table.Insert(new Row { ["Id"] = 1, ["N"] = true });
        table.Insert(new Row { ["Id"] = 2, ["N"] = new DateTime(2020, 1, 2) });
        var cursor = table.CreateIndexCursor("ixN");
        Assert.Equal(1, (int)cursor.FindRowByEntry(-1)!["Id"]!);
        Assert.Equal(2, (int)cursor.FindRowByEntry(43832)!["Id"]!);
        // A range takes true for the number a row is written with for it; an equality takes a bool for no number.
        Assert.Equal(new[] { 1 }, table.CreateIndexCursor("ixN", new IndexRange { StartEntry = new object[] { true }, EndEntry = new object[] { true } }).Select(r => (int)r["Id"]!));
        Assert.Null(table.CreateIndexCursor("ixN").FindRowByEntry(true));
    }
}
