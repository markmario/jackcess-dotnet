using System.Data;
using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Large Number and Date/Time Extended columns as this library writes them (qcatco/exportmdb.interop#40): a table
/// is created with them, a value comes back as it went in - a <see cref="long"/>, and a <see cref="DateTime"/> to
/// its last tick - indexes on them are kept, the file becomes one of the format that has the column, as Access
/// makes it, and an .mdb, which has neither, refuses them. What Access makes of such a file is refereed by ACE in
/// ExportMdb.Interop's tests; what Access itself wrote is read in <see cref="AccessLargeNumberExtendedDateTests"/>.
/// </summary>
public sealed class LargeNumberExtendedDateTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string NewPath(string extension = ".accdb")
    {
        string path = Path.Combine(Path.GetTempPath(), $"large_numbers_{Guid.NewGuid():N}{extension}");
        _paths.Add(path);
        return path;
    }

    private static readonly long?[] Numbers = AccessLargeNumberExtendedDateTests.Numbers;
    private static readonly DateTime?[] Dates = AccessLargeNumberExtendedDateTests.Dates.Select(AccessLargeNumberExtendedDateTests.Date).ToArray();

    private static Column[] Columns() => new[]
    {
        ColumnBuilder.Long("Id").Build(),
        ColumnBuilder.BigInt("N").Build(),
        ColumnBuilder.ExtendedDateTime("D").Build(),
        ColumnBuilder.Text("Txt", 20).Build(),
    };

    private static IndexDefinition[] Indexes() => new[]
    {
        new IndexDefinition("NAsc", "N"),
        new IndexDefinition("NDesc", "N") { DescendingColumns = new[] { "N" } },
        new IndexDefinition("DAsc", "D"),
        new IndexDefinition("DDesc", "D") { DescendingColumns = new[] { "D" } },
        new IndexDefinition("NThenD", "N", "D") { DescendingColumns = new[] { "D" } },
    };

    private static Row RowOf(int id, long? number, DateTime? date) => new() { ["Id"] = id, ["N"] = number, ["D"] = date, ["Txt"] = "row " + id };

    private static object? Value(Row row, string column) => row.TryGetValue(column, out var value) ? value : null;

    // ── The value itself ─────────────────────────────────────────────────────

    [Fact]
    public void An_extended_date_is_the_42_bytes_Access_writes()
    {
        // 2 January 2020, 03:04:05.1234567, as Access stored it in the fixture.
        var value = new DateTime(2020, 1, 2, 3, 4, 5).AddTicks(1234567);
        byte[] bytes = ExtendedDateTimeCodec.Encode(value);

        Assert.Equal("0000000000000737425:0000000110451234567:7\0", Encoding.ASCII.GetString(bytes));
        Assert.Equal(value, ExtendedDateTimeCodec.Decode(bytes, 0));
        Assert.Equal("0000000000000000000:0000000000000000000:7\0", Encoding.ASCII.GetString(ExtendedDateTimeCodec.Encode(DateTime.MinValue)));
        Assert.Equal("0000000000003652058:0000000863999999999:7\0", Encoding.ASCII.GetString(ExtendedDateTimeCodec.Encode(DateTime.MaxValue)));
        Assert.Equal(DateTime.MaxValue, ExtendedDateTimeCodec.Decode(ExtendedDateTimeCodec.Encode(DateTime.MaxValue), 0));

        // It has no time zone: a UTC or a local DateTime is stored as its own date and time.
        Assert.Equal(bytes, ExtendedDateTimeCodec.Encode(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        Assert.Equal(bytes, ExtendedDateTimeCodec.Encode(DateTime.SpecifyKind(value, DateTimeKind.Local)));
    }

    [Theory]
    [InlineData("0000000000003652059:0000000000000000000:7\0")]   // the day after the last
    [InlineData("0000000000000737425:0000000864000000000:7\0")]   // a time of day a day long
    [InlineData("000000000000073742X:0000000110451234567:7\0")]   // not a digit
    [InlineData("9999999999999999999:0000000000000000000:7\0")]   // more than any number of days
    public void Bytes_that_are_no_extended_date_are_read_as_null(string stored)
        => Assert.Null(ExtendedDateTimeCodec.Decode(Encoding.ASCII.GetBytes(stored), 0));

    // The whole shape is looked at: two numbers that are dates were read as one whatever stood between and
    // after them. What Access makes of each (DAO 16.0, the bytes changed in a file it made) is beside it.
    [Theory]
    [InlineData("0000000000000737425 0000000110451234567:7\0")]   // the first colon a space: no value
    [InlineData("000000000000073742500000000110451234567:7\0")]   // the first colon a digit: no value
    [InlineData("0000000000000737425:0000000110451234567 7\0")]   // the second colon a space: no value
    [InlineData("0000000000000737425:0000000110451234567:x\0")]   // no count of digits: no value
    [InlineData("0000000000000737425:0000000110451234567:71")]    // a count of 71: no value
    [InlineData("-000000000000000001:0000000110451234567:7\0")]   // a day before the first: no value
    [InlineData("0000000000000737425:0000000110451234567:3\0")]   // a count of 3: Access shows 30680:53:54.0000567
    [InlineData("0000000000000737425:0000000000011045123:3\0")]   // a count of 3: Access shows 03:04:05.0000123
    [InlineData("0000000000000737425:0000000110451234567:9\0")]   // a count of 9: finer than a tick
    [InlineData("0000000000000737425:00000001104512345X7:7\0")]   // not a digit: Access reads up to it
    [InlineData("                   :0000000110451234567:7\0")]   // no digit: Access reads day 0
    public void Bytes_in_another_shape_than_a_date_s_are_read_as_null(string stored)
    {
        Assert.Equal(ExtendedDateTimeCodec.Size, stored.Length);
        Assert.Null(ExtendedDateTimeCodec.Decode(Encoding.ASCII.GetBytes(stored), 0));
    }

    // And the forms Access reads the same date from as from its own are read as that date.
    [Theory]
    [InlineData("0000000000000737425:0000000110451234567:7\0")]   // as Access writes it
    [InlineData("0000000000000737425:0000000110451234567:7 ")]    // anything but a digit in the last place
    [InlineData("0000000000000737425:0000000110451234567:7\t")]
    [InlineData("0000000000000737425:0000000110451234567:7x")]
    [InlineData("             737425:0000000110451234567:7\0")]   // spaces before a number
    [InlineData("0000000000000737425:       110451234567:7\0")]
    [InlineData("000000000000737425 :000000110451234567 :7\0")]   // and after it
    [InlineData("+000000000000737425:+000000110451234567:7\0")]   // a plus sign
    public void The_forms_Access_reads_a_date_from_are_read_as_that_date(string stored)
    {
        Assert.Equal(ExtendedDateTimeCodec.Size, stored.Length);
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5).AddTicks(1234567), ExtendedDateTimeCodec.Decode(Encoding.ASCII.GetBytes(stored), 0));
    }

    [Fact]
    public void Their_keys_are_the_ones_Access_writes()
    {
        // A Large Number: eight bytes, the most significant first, the sign bit turned over.
        Assert.Equal("7F7FFFFFFFFFFFFFFF", Convert.ToHexString(IndexKeys.EncodeColumn(DataType.BigInt, ascending: true, -1L)));
        Assert.Equal("7F8000000000000001", Convert.ToHexString(IndexKeys.EncodeColumn(DataType.BigInt, ascending: true, 1)));
        Assert.Equal("807FFFFFFFFFFFFFFE", Convert.ToHexString(IndexKeys.EncodeColumn(DataType.BigInt, ascending: false, 1L)));
        Assert.Equal("00", Convert.ToHexString(IndexKeys.EncodeColumn(DataType.BigInt, ascending: true, null)));

        // An extended date: its 42 bytes, eight at a time with a 9 after each, the last two filled out with
        // zeros and followed by a 2. Descending, every byte but those 9s is turned over.
        var value = new DateTime(2020, 1, 2, 3, 4, 5).AddTicks(1234567);
        string text = "0000000000000737425:0000000110451234567:7\0";
        var expected = new List<byte> { 0x7F };
        for (int at = 0; at < 40; at += 8)
        {
            expected.AddRange(Encoding.ASCII.GetBytes(text.Substring(at, 8)));
            expected.Add(9);
        }
        expected.AddRange(new byte[] { (byte)'7', 0, 0, 0, 0, 0, 0, 0, 2 });
        Assert.Equal(expected.ToArray(), IndexKeys.EncodeColumn(DataType.ExtendedDateTime, ascending: true, value));

        byte[] descending = IndexKeys.EncodeColumn(DataType.ExtendedDateTime, ascending: false, value);
        Assert.Equal(0x80, descending[0]);
        for (int i = 1; i < expected.Count; i++)
            Assert.Equal(i % 9 == 0 && i != expected.Count - 1 ? (byte)9 : (byte)~expected[i], descending[i]);
    }

    // ── Tables ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(JetVersion.Jet12)]
    [InlineData(JetVersion.Jet14)]
    [InlineData(JetVersion.Jet16)]
    [InlineData(JetVersion.Jet17)]
    public void Values_come_back_as_they_went_in_and_every_index_holds_them(JetVersion version)
    {
        string path = NewPath();
        using (var db = Database.Create(path, version))
        {
            var table = db.CreateTable("T", Columns(), new[] { "Id" }, Indexes());
            for (int i = 0; i < Numbers.Length; i++)
                table.Insert(RowOf(i + 1, Numbers[i], Dates[i]));
            // Enough rows for the indexes to have more than one page, the dates a tick apart.
            for (int i = 100; i < 1100; i++)
                table.Insert(RowOf(i, (i - 600) * 1_000_000_007L, new DateTime(1850, 1, 1).AddTicks(i * 7_919_000_000_001L)));
        }

        using (var db = Database.Open(path))
        {
            var table = db.GetTable("T");
            Assert.Equal(DataType.BigInt, table.Columns[1].DataType);
            Assert.Equal(8, table.Columns[1].Length);
            Assert.Equal(DataType.ExtendedDateTime, table.Columns[2].DataType);
            Assert.Equal(42, table.Columns[2].Length);

            var rows = table.ReadAllRows().ToDictionary(r => (int)r["Id"]!);
            Assert.Equal(Numbers.Length + 1000, rows.Count);
            for (int i = 0; i < Numbers.Length; i++)
            {
                Assert.Equal(Numbers[i], (long?)Value(rows[i + 1], "N"));
                Assert.Equal(Dates[i]?.Ticks, ((DateTime?)Value(rows[i + 1], "D"))?.Ticks);
            }
            for (int i = 100; i < 1100; i++)
            {
                Assert.Equal((i - 600) * 1_000_000_007L, rows[i]["N"]);
                Assert.Equal(new DateTime(1850, 1, 1).AddTicks(i * 7_919_000_000_001L), rows[i]["D"]);
                Assert.Equal(DateTimeKind.Unspecified, ((DateTime)rows[i]["D"]!).Kind);
            }
            Assert.Equal(6, IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: rows.Count));

            // Found through the indexes, by a long, by an int, and by a date to the tick.
            Assert.Equal(1, table.NewIndexCursor("NAsc").FindRow("N", long.MaxValue)!["Id"]);
            Assert.Equal(9, table.NewIndexCursor("NAsc").FindRow("N", 256)!["Id"]);
            Assert.Equal(5, table.NewIndexCursor("DAsc").FindRow("D", Dates[4]!.Value)!["Id"]);
            Assert.Equal(6, table.NewIndexCursor("DAsc").FindRow("D", Dates[4]!.Value.AddTicks(1))!["Id"]);
            Assert.Null(table.NewIndexCursor("DAsc").FindRow("D", Dates[4]!.Value.AddTicks(2)));
            Assert.Equal(7, table.NewIndexCursor("NThenD").FindRowByEntry(-4294967296L, Dates[6]!.Value)!["Id"]);

            // Changed and deleted, each index following.
            table.UpdateByPrimaryKey(3, new Row { ["N"] = long.MinValue + 1, ["D"] = DateTime.MaxValue.AddTicks(-1) });
            table.UpdateByPrimaryKey(4, new Row { ["N"] = null, ["D"] = null });
            table.UpdateByPrimaryKey(5, new Row { ["Txt"] = "kept" });
            table.DeleteRow("N", 4294967296L);
            table.DeleteRow("D", Dates[0]!.Value);
        }

        using (var db = Database.Open(path))
        {
            var table = db.GetTable("T");
            var rows = table.ReadAllRows().ToDictionary(r => (int)r["Id"]!);
            Assert.Equal(Numbers.Length + 1000 - 2, rows.Count);
            Assert.Equal(long.MinValue + 1, rows[3]["N"]);
            Assert.Equal(DateTime.MaxValue.AddTicks(-1), rows[3]["D"]);
            Assert.Null(Value(rows[4], "N"));
            Assert.Null(Value(rows[4], "D"));
            Assert.Equal(1L, rows[5]["N"]);
            Assert.Equal(Dates[4], rows[5]["D"]);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", expectedRows: rows.Count);
        }
    }

    [Fact]
    public void A_large_number_is_a_primary_key_and_a_unique_index_refuses_a_second_of_either()
    {
        string path = NewPath();
        using var db = Database.Create(path, JetVersion.Jet17);
        var table = db.CreateTable("Keyed", new[] { ColumnBuilder.BigInt("K").Build(), ColumnBuilder.ExtendedDateTime("At").Build() },
            new[] { "K" }, new[] { new IndexDefinition("OneAt", "At") { IsUnique = true } });
        var at = new DateTime(2026, 11, 18, 9, 30, 0);
        table.Insert(new Row { ["K"] = long.MaxValue, ["At"] = at });
        table.Insert(new Row { ["K"] = long.MaxValue - 1, ["At"] = at.AddTicks(1) });

        var before = IndexChecks.AllPages(db);
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["K"] = long.MaxValue, ["At"] = at.AddTicks(2) }));
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["K"] = 5L, ["At"] = at.AddTicks(1) }));
        // A value that is not of the column's type refuses the row before anything is written.
        Assert.Throws<InvalidCastException>(() => table.Insert(new Row { ["K"] = 6L, ["At"] = "2026-11-18" }));
        Assert.Throws<OverflowException>(() => table.Insert(new Row { ["K"] = 9223372036854775808m, ["At"] = at.AddTicks(3) }));
        Assert.Equal(before, IndexChecks.AllPages(db));

        Assert.Equal(at.AddTicks(1), table.NewIndexCursor().FindRowByPrimaryKey(long.MaxValue - 1)!["At"]);
        table.UpdateByPrimaryKey(long.MaxValue, new Row { ["At"] = at.AddTicks(9) });
        Assert.Equal(at.AddTicks(9), table.NewIndexCursor().FindRowByPrimaryKey(long.MaxValue)!["At"]);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Keyed", expectedRows: 2);
    }

    [Fact]
    public void Rows_are_added_to_tables_Access_made_between_the_rows_Access_wrote()
    {
        string path = NewPath();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "ace-large-numbers-extended-dates.accdb"), path);

        using (var db = Database.Open(path))
            foreach (string name in new[] { "BySql", "ByDao" })
            {
                var table = db.GetTable(name);
                for (int i = 100; i < 400; i++)
                    table.Insert(new Row { ["Id"] = i, ["N"] = (i - 250) * 3_000_000_019L, ["D"] = new DateTime(1066, 10, 14).AddTicks(i * 99_999_999_999_977L) });
                table.UpdateByPrimaryKey(2, new Row { ["N"] = 7L });
                table.DeleteRow("Id", 9);
            }

        using (var db = Database.Open(path))
            foreach (string name in new[] { "BySql", "ByDao" })
            {
                var rows = db.GetTable(name).ReadAllRows().ToDictionary(r => (int)r["Id"]!);
                Assert.Equal(Numbers.Length + 300 - 1, rows.Count);
                Assert.Equal(7L, rows[2]["N"]);
                Assert.Equal(Dates[1], rows[2]["D"]);   // not named by the update: as Access stored it
                Assert.Equal((399 - 250) * 3_000_000_019L, rows[399]["N"]);
                Assert.Equal(6, IndexChecks.AssertEveryIndexHoldsTheRows(db, name, expectedRows: rows.Count));
            }
    }

    // ── The file's format ────────────────────────────────────────────────────

    // Access gives the file the version of the format that has the column when a table first gets one, and
    // never takes it back down: 05 00 for a Large Number, 06 00 for a Date/Time Extended.
    [Theory]
    [InlineData(JetVersion.Jet12, DataType.BigInt, 0x05)]
    [InlineData(JetVersion.Jet14, DataType.BigInt, 0x05)]
    [InlineData(JetVersion.Jet16, DataType.BigInt, 0x05)]
    [InlineData(JetVersion.Jet17, DataType.BigInt, 0x06)]
    [InlineData(JetVersion.Jet12, DataType.ExtendedDateTime, 0x06)]
    [InlineData(JetVersion.Jet14, DataType.ExtendedDateTime, 0x06)]
    [InlineData(JetVersion.Jet16, DataType.ExtendedDateTime, 0x06)]
    [InlineData(JetVersion.Jet17, DataType.ExtendedDateTime, 0x06)]
    public void A_table_with_such_a_column_makes_the_file_one_of_the_format_that_has_it(JetVersion created, DataType type, byte version)
    {
        string path = NewPath();
        using (var db = Database.Create(path, created))
        {
            // A table without one leaves the version as it was.
            byte[] asCreated = db.File.ReadPage(0).AsSpan(0x14, 2).ToArray();
            db.CreateTable("Plain", new[] { ColumnBuilder.Long("Id").Build() });
            Assert.Equal(asCreated, db.File.ReadPage(0).AsSpan(0x14, 2).ToArray());

            db.CreateTable("Newer", new[] { ColumnBuilder.Long("Id").Build(), new ColumnBuilder("V", type).Build() });
            Assert.Equal(new byte[] { version, 0x00 }, db.File.ReadPage(0).AsSpan(0x14, 2).ToArray());
        }

        using (var db = Database.Open(path))
        {
            Assert.Equal(version == 0x05 ? JetVersion.Jet16 : JetVersion.Jet17, db.File.Format.Version);
            Assert.Equal(new[] { "Newer", "Plain" }, db.ListTables().OrderBy(n => n));
        }
    }

    [Theory]
    [InlineData(DataType.BigInt)]
    [InlineData(DataType.ExtendedDateTime)]
    public void An_mdb_has_no_such_column_and_the_table_is_refused_with_nothing_written(DataType type)
    {
        string path = NewPath(".mdb");
        using var db = Database.Create(path, JetVersion.Jet4);
        var before = IndexChecks.AllPages(db);

        var refused = Assert.Throws<NotSupportedException>(() =>
            db.CreateTable("Newer", new[] { ColumnBuilder.Long("Id").Build(), new ColumnBuilder("V", type).Build() }));

        Assert.Contains(".mdb", refused.Message);
        Assert.Equal(before, IndexChecks.AllPages(db));
        Assert.Empty(db.ListTables());
    }

    // ── Other ways in and out ────────────────────────────────────────────────

    [Fact]
    public void They_export_as_long_and_DateTime_and_neither_is_an_AutoNumber()
    {
        string path = NewPath();
        using var db = Database.Create(path, JetVersion.Jet17);
        var table = db.CreateTable("T", Columns(), primaryKey: "Id");
        table.Insert(RowOf(1, long.MinValue, DateTime.MaxValue));

        DataTable exported = db.ExportToDataTable("T");
        Assert.Equal(typeof(long), exported.Columns["N"]!.DataType);
        Assert.Equal(typeof(DateTime), exported.Columns["D"]!.DataType);
        Assert.Equal(long.MinValue, exported.Rows[0]["N"]);
        Assert.Equal(DateTime.MaxValue, exported.Rows[0]["D"]);

        Assert.Equal(5L, TypeMapper.CoerceForJet(5, DataType.BigInt));
        Assert.Throws<InvalidOperationException>(() => ColumnBuilder.BigInt("N").WithAutoNumber());
    }
}
