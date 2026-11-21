using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The stored numbers a lookup for a date finds (<see cref="ValueMatch.StoredRange"/>), on every runtime (#98). The
/// range's ends are found by halving the distance between a number that reads as the date and one that does not; in
/// a 32-bit .NET Framework process the halving never ended for a date on the first day Access holds, so a lookup for
/// it through a Date/Time index spun for ever. Each end is the last double that reads as the date: the next one past
/// it does not. Dates before 30 December 1899 are stored as negative numbers, and a time of that day as a number
/// either side of zero; a range for a time of that day near midnight has numbers of both signs (StoredRange), and is not
/// asked here which is the next double past it.
/// <para>
/// The halving went on only where the process had left the x87 unit computing with 64 bits of precision, as ACE's OLE DB
/// provider leaves the thread that ran a statement (measured with Microsoft 365's ACE, 32-bit: 53 bits before an INSERT, 64 after it). So in a 32-bit process each test puts its thread in that state first (<see cref="X87.At64Bits(Action)"/>).
/// </para>
/// </summary>
public sealed class StoredRangeTests
{
    public static TheoryData<DateTime> Dates() => new()
    {
        new DateTime(100, 1, 1),
        new DateTime(100, 1, 1, 6, 0, 0),
        new DateTime(100, 1, 1, 12, 30, 0),
        new DateTime(100, 1, 1, 23, 59, 59),
        new DateTime(100, 1, 1, 23, 59, 59, 998),
        new DateTime(1066, 10, 14, 9, 0, 0, 1),
        new DateTime(1899, 12, 29, 23, 59, 59, 999),
        new DateTime(1899, 12, 30),
        new DateTime(1899, 12, 30, 6, 0, 0),
        new DateTime(1899, 12, 31),
        new DateTime(2026, 11, 20, 17, 1, 40, 123),
        new DateTime(9999, 12, 31),
        new DateTime(9999, 12, 31, 12, 0, 0),
        new DateTime(9999, 12, 31, 23, 59, 59),
        new DateTime(9999, 12, 31, 23, 59, 59, 999),
    };

    [Theory]
    [MemberData(nameof(Dates))]
    public async Task each_end_of_a_date_s_range_is_the_last_double_that_reads_as_it(DateTime date)
    {
        (double Least, double Greatest) range = default;
        DateTime stored = default;
        DateTime? least = null, greatest = null, belowLeast = null, aboveGreatest = null;
        // On a thread of its own, so that a halving that does not end fails the test rather than hold the run. What
        // a number reads as is asked in the same state of the unit as the range was found in: in a 32-bit process
        // the reading of a number within a millisecond's rounding of the next can differ between the two states.
        var found = await EndsInTime(() =>
        {
            range = ValueMatch.StoredRange(date);
            stored = ValueMatch.AsStored(date);
            least = ValueMatch.ReadsAs(range.Least);
            greatest = ValueMatch.ReadsAs(range.Greatest);
            belowLeast = ValueMatch.ReadsAs(Down(range.Least));
            aboveGreatest = ValueMatch.ReadsAs(Up(range.Greatest));
        });
        Assert.True(found, "StoredRange(" + date.ToString("o") + ") did not end in 20 seconds");

        double exact = stored.ToOADate();
        Assert.True(range.Least <= exact && exact <= range.Greatest, "the range " + range + " holds " + exact);
        Assert.Equal(stored, least);
        Assert.Equal(stored, greatest);
        Assert.NotEqual(stored, belowLeast);
        Assert.NotEqual(stored, aboveGreatest);
    }

    [Fact]
    public async Task a_lookup_through_a_date_time_index_on_the_first_and_the_last_day_ends()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stored_range_{Guid.NewGuid():N}.mdb");
        try
        {
            var first = new DateTime(100, 1, 1);
            var last = new DateTime(9999, 12, 31, 23, 59, 59);
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("E", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTime) },
                    new[] { "Id" }, new[] { new IndexDefinition("ByD", "D") });
                table.Insert(new Row { ["Id"] = 1, ["D"] = first.AddHours(6) });
                table.Insert(new Row { ["Id"] = 2, ["D"] = last });
            }
            using (var db = Database.Open(path))
            {
                var table = db.GetTable("E");
                object? one = null, two = null;
                var found = await EndsInTime(() =>
                {
                    one = table.CreateIndexCursor("ByD").FindRowByEntry(first.AddHours(6))?["Id"];
                    two = table.CreateIndexCursor("ByD").FindRowByEntry(last)?["Id"];
                });
                Assert.True(found, "the lookups did not end in 20 seconds");
                Assert.Equal(1, one);
                Assert.Equal(2, two);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Runs 'action' on a thread of its own, with the x87 unit as ACE leaves it (X87.At64Bits), and says whether it
    // ended within 20 seconds; an exception it throws is thrown here.
    private static async Task<bool> EndsInTime(Action action)
    {
        var cancel = TestContext.Current.CancellationToken;
        var work = Task.Run(() => X87.At64Bits(action), cancel);
        if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20), cancel)) != work) return false;
        await work;
        return true;
    }

    // The next double towards plus and minus infinity: .NET Framework has no Math.BitIncrement.
    private static double Up(double value) =>
        value == 0 ? double.Epsilon
        : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + (value > 0 ? 1 : -1));

    private static double Down(double value) => -Up(-value);
}
