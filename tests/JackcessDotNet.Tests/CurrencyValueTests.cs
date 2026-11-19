using System.Data;
using System.Globalization;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A Currency made from a caller's number is the one Access makes (#65) wherever it is made: a row written with it and
/// the keys the row is written with, a relationship's check, a property, an import, and every lookup - by a cursor
/// over the table, through an index, a range and the closest row. So a row written with a number is found by that
/// number, on every runtime. What each case expects is what ACE 16 and DAO stored and found for the number
/// (<see cref="CurrencyRoundingTests"/>); ExportMdb.Interop's suite has ACE referee the same cases in a file this
/// library wrote.
/// </summary>
public sealed class CurrencyValueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"currency_{Guid.NewGuid():N}");
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

    private static string Shown(object? value) => value switch
    {
        double d => d.ToString("R", CultureInfo.InvariantCulture) + " (double)",
        float f  => f.ToString("R", CultureInfo.InvariantCulture) + " (float)",
        null     => "null",
        _        => Convert.ToString(value, CultureInfo.InvariantCulture) + " (" + value.GetType().Name + ")",
    };

    // Numbers given, and what ACE stores for each.
    private static readonly (object Given, decimal Stored)[] Written =
    {
        (0.03125, 0.0312m), (-0.03125, -0.0312m), (0.09375, 0.0938m), (0.00015, 0.0001m), (0.00025, 0.0003m),
        (1.23465, 1.2347m), (1.234549999999999, 1.2345m), (562949953421312.5, 562949953421312.5m),
        (922337203685477.5, 922337203685477.5m), (-922337203685477.5, -922337203685477.5m),
        (35184372088832.0078125, 35184372088832.0078m), (0.03125f, 0.0312m), (0.00015f, 0.0002m),
        (1.23465m, 1.2346m), (-1.23465m, -1.2346m), (0.00015m, 0.0002m), (0.00025m, 0.0002m),
        (922337203685477.58065m, 922337203685477.5806m), (922337203685477.5807m, 922337203685477.5807m),
        (-922337203685477.5808m, -922337203685477.5808m), (7, 7m), (-922337203685477L, -922337203685477m), ((short)-3, -3m),
    };

    private static Table CreateKeyed(Database db)
        => db.CreateTable("K", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("C", DataType.Currency) },
                          new[] { "Id" }, new[] { new IndexDefinition("ByC", "C") });

    private static List<int> Ids(IEnumerable<Row> rows) => rows.Select(r => (int)r["Id"]!).OrderBy(id => id).ToList();

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_row_is_written_with_the_Currency_Access_writes_and_found_by_the_number_it_was_written_with(FileFormat format)
    {
        using (var db = Create(format))
        {
            var table = CreateKeyed(db);
            for (int i = 0; i < Written.Length; i++) table.Insert(new Row { ["Id"] = i, ["C"] = Written[i].Given });
        }

        using (var db = Database.Open(_file!))
        {
            var table = db.GetTable("K");
            var stored = table.Rows.ToDictionary(r => (int)r["Id"]!, r => (decimal)r["C"]!);
            for (int i = 0; i < Written.Length; i++)
                Assert.True(Written[i].Stored == stored[i], $"{Shown(Written[i].Given)} was written as {stored[i]}, where Access writes {Written[i].Stored}");

            for (int i = 0; i < Written.Length; i++)
            {
                var (given, held) = Written[i];
                var rows = stored.Where(p => p.Value == held).Select(p => p.Key).OrderBy(id => id).ToList();
                string what = Shown(given);

                // Through the index, by a cursor over the table, in a range of the number alone, and as the closest row.
                Assert.True(rows.SequenceEqual(Ids(table.CreateIndexCursor("ByC").FindRowsByEntry(given))), what + " through the index");
                var scan = new List<int>();
                var cursor = table.CreateCursor();
                for (var row = cursor.FindRow("C", given); row is not null; row = cursor.FindNextRow("C", given)) scan.Add((int)row["Id"]!);
                Assert.True(rows.SequenceEqual(scan.OrderBy(id => id)), what + " by a cursor over the table");
                var range = table.CreateIndexCursor("ByC", new IndexRange { StartEntry = new[] { given }, EndEntry = new[] { given } });
                Assert.True(rows.SequenceEqual(Ids(range)), what + " in a range of it alone");
                Assert.Equal(held, (decimal)table.CreateIndexCursor("ByC").FindClosestRowByEntry(given)!["C"]!);
            }
            Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "K", expectedRows: Written.Length));
        }
    }

    [Fact]
    public void Ranges_and_the_closest_row_take_a_number_to_the_Currency_a_row_written_with_it_holds()
    {
        decimal[] held =
        {
            0.0001m, 0.0002m, 0.0003m, 0.0311m, 0.0312m, 0.0313m, 0.0937m, 0.0938m, 0.0939m, 1.2345m, 1.2346m, 1.2347m,
            -922337203685477.5m, 922337203685477.5m, -922337203685477.5808m, 922337203685477.5807m,
        };
        using var db = Create(FileFormat.Access2010);
        var table = CreateKeyed(db);
        for (int i = 0; i < held.Length; i++) table.Insert(new Row { ["Id"] = i, ["C"] = held[i] });

        decimal[] In(IndexRange range) => table.CreateIndexCursor("ByC", range).Select(r => (decimal)r["C"]!).ToArray();

        Assert.Equal(new[] { 0.0312m }, In(new IndexRange { StartEntry = new object[] { 0.03125 }, EndEntry = new object[] { 0.03125 } }));
        Assert.Equal(new[] { 0.0001m, 0.0002m, 0.0003m }, In(new IndexRange { StartEntry = new object[] { 0.00015 }, EndEntry = new object[] { 0.00025 } }));
        Assert.Equal(new[] { 0.0939m, 1.2345m, 1.2346m, 1.2347m },
            In(new IndexRange { StartEntry = new object[] { 0.09375 }, IsStartInclusive = false, EndEntry = new object[] { 1.23465 } }));
        Assert.Equal(new[] { 0.0001m, 0.0002m, 0.0003m, 0.0311m },
            In(new IndexRange { StartEntry = new object[] { 0.0001m }, EndEntry = new object[] { 0.03125 }, IsEndInclusive = false }));
        Assert.Equal(new[] { 1.2346m }, In(new IndexRange { StartEntry = new object[] { 1.23465m }, EndEntry = new object[] { 1.23465m } }));
        Assert.Equal(held.Length - 2,
            In(new IndexRange { StartEntry = new object[] { -922337203685477.5 }, EndEntry = new object[] { 922337203685477.5 } }).Length);

        foreach (var (asked, closest) in new (object, decimal)[]
                 {
                     (0.00015, 0.0001m), (0.03125, 0.0312m), (0.09375f, 0.0938m), (1.23465m, 1.2346m), (1.234549999999999, 1.2345m),
                     (-922337203685477.5, -922337203685477.5m), (922337203685477.5, 922337203685477.5m),
                     (922337203685477.5807m, 922337203685477.5807m), (-922337203685477.58075m, -922337203685477.5808m), (0, 0.0001m),
                 })
            Assert.True(closest == (decimal)table.CreateIndexCursor("ByC").FindClosestRowByEntry(asked)!["C"]!, $"the closest row to {Shown(asked)}");

        // A value no Currency holds, or one that is not a number, is not one the column holds.
        var cursor = table.CreateIndexCursor("ByC");
        Assert.NotNull(cursor.FindRowByEntry(0.0312m));
        foreach (object bad in new object[] { double.NaN, double.PositiveInfinity, 922337203685477.625, 1e20, 922337203685477.5808m, "0.0312", true })
        {
            Assert.Throws<ArgumentException>(() => table.CreateIndexCursor("ByC", new IndexRange { StartEntry = new[] { bad } }));
            Assert.Throws<ArgumentException>(() => table.CreateIndexCursor("ByC", new IndexRange { EndEntry = new[] { bad } }));
            Assert.Throws<ArgumentException>(() => cursor.FindClosestRowByEntry(bad));
            Assert.Equal(0.0312m, cursor.Current!["C"]);
            Assert.Empty(table.CreateIndexCursor("ByC").FindRowsByEntry(bad));
            Assert.Null(table.CreateCursor().FindRow("C", bad));
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_value_a_Currency_column_does_not_hold_is_refused_before_anything_is_written(FileFormat format)
    {
        using (var db = Create(format))
            CreateKeyed(db).Insert(new Row { ["Id"] = 1, ["C"] = 1.5m });
        byte[] before = File.ReadAllBytes(_file!);

        object[] notHeld =
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, float.NaN, 922337203685477.625, -922337203685477.625,
            1e20, 922337203685477.5808m, -922337203685477.58095m, 922337203685478L, ulong.MaxValue,
        };
        object[] notNumbers = { "1.5", "1,5", "", true, 'x', new DateTime(2026, 11, 20), Guid.NewGuid(), new byte[] { 1 }, DayOfWeek.Monday };
        using (var db = Database.Open(_file!))
        {
            var table = db.GetTable("K");
            foreach (object bad in notHeld.Concat(notNumbers))
            {
                bool number = notHeld.Contains(bad);
                void Refused(Action change)
                {
                    var e = Record.Exception(change);
                    Assert.True(number ? e is OverflowException : e is ArgumentException, $"{Shown(bad)}: {e?.GetType().Name ?? "no exception"}");
                    Assert.Contains("'C'", e!.Message);
                }
                Refused(() => table.Insert(new Row { ["Id"] = 2, ["C"] = bad }));
                Refused(() => table.Validate(new Row { ["Id"] = 2, ["C"] = bad }));
                var row = table.FindRowByPrimaryKey(1)!;
                row["C"] = bad;
                Refused(() => table.Update(row));
                var cursor = table.CreateCursor();
                Assert.NotNull(cursor.FindRow("Id", 1));
                Refused(() => cursor.UpdateCurrentRow(new Row { ["C"] = bad }));
            }
            Assert.Equal(0, db.File.WriteCount);

            var batch = Assert.Throws<BatchInsertException>(() => table.InsertRange(new[]
            {
                new Row { ["Id"] = 2, ["C"] = 2m }, new Row { ["Id"] = 3, ["C"] = double.NaN },
            }));
            Assert.Equal(1, batch.RowIndex);
            Assert.IsType<OverflowException>(batch.InnerException);
            Assert.Equal(new[] { 1.5m }, table.Rows.Select(r => (decimal)r["C"]!));
        }
        Assert.Equal(before, File.ReadAllBytes(_file!));
    }

    [Fact]
    public void A_relationship_compares_a_Currency_key_as_a_row_written_with_the_number_stores_it()
    {
        using var db = Create(FileFormat.Access2010);
        var parent = db.CreateTable("P", new[]
        {
            new ColumnDefinition("K", DataType.Currency), new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
        }, "K");
        var child = db.CreateTable("C", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Currency) },
            new[] { "Id" }, new[] { new IndexDefinition("ByK", "K") });
        db.CreateRelationship(new RelationshipDefinition("P", "K", "C", "K") { HasReferentialIntegrity = true, CascadeUpdates = true });
        parent.Insert(new Row { ["K"] = 0.0312m, ["Name"] = "a" });
        parent.Insert(new Row { ["K"] = 1.2346m, ["Name"] = "b" });

        // Each child names its parent by a number Access takes to the parent's key: rounded half away from zero, the
        // first three were 0.0313, 0.0313 and 1.2347, and had no parent.
        child.Insert(new Row { ["Id"] = 1, ["K"] = 0.03125 });
        child.Insert(new Row { ["Id"] = 2, ["K"] = 0.03125m });
        child.Insert(new Row { ["Id"] = 3, ["K"] = 1.23465m });
        child.Insert(new Row { ["Id"] = 4, ["K"] = 1.23455 });
        Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["Id"] = 5, ["K"] = 0.031251 }));
        Assert.Equal(new[] { 0.0312m, 0.0312m, 1.2346m, 1.2346m }, child.Rows.OrderBy(r => (int)r["Id"]!).Select(r => (decimal)r["K"]!));

        // A parent re-keyed by a number takes its children to the key a row written with that number holds.
        var a = parent.FindRowByPrimaryKey(0.03125)!;
        Assert.Equal("a", a["Name"]);
        a["K"] = 0.09375;
        parent.Update(a);
        Assert.Equal(0.0938m, parent.FindRowByPrimaryKey(0.0938m)!["K"]);
        Assert.Equal(new[] { 1, 2 }, Ids(child.CreateIndexCursor("ByK").FindRowsByEntry(0.09375)));
        Assert.Throws<ConstraintViolationException>(() => parent.Delete(parent.FindRowByPrimaryKey(1.23465m)!));
    }

    [Fact]
    public void A_Currency_property_is_the_number_taken_to_four_places_as_Access_takes_it()
    {
        using (var db = Create(FileFormat.Access2010))
        {
            var table = CreateKeyed(db);
            table.Properties.Set("Half", DataType.Currency, 0.03125);
            table.Properties.Set("UnderAHalf", DataType.Currency, 0.00015);
            table.Properties.Set("Long", DataType.Currency, 562949953421312.5);
            table.Properties.Set("Decimal", DataType.Currency, 1.23465m);
            table.Properties.Set("Text", DataType.Currency, "0.03125");
            table.Properties.Set("Kept", 0.00025m);
            foreach (object bad in new object[] { double.NaN, double.PositiveInfinity, 1e20, 922337203685477.58075m })
                Assert.Throws<ArgumentException>(() => table.Properties.Set("Bad", DataType.Currency, bad));
            table.Properties.Save();
        }
        using (var db = Database.Open(_file!))
        {
            var properties = db.GetTable("K").Properties;
            Assert.Equal(0.0312m, properties["Half"]);
            Assert.Equal(0.0001m, properties["UnderAHalf"]);
            Assert.Equal(562949953421312.5m, properties["Long"]);
            Assert.Equal(1.2346m, properties["Decimal"]);
            Assert.Equal(0.0312m, properties["Text"]);
            Assert.Equal(0.0002m, properties["Kept"]);
            Assert.Null(properties["Bad"]);
        }
    }

    [Fact]
    public void An_import_takes_a_double_or_a_float_to_a_Currency_column_as_Access_does_and_refuses_one_it_does_not_hold()
    {
        using (var db = Create(FileFormat.Access2010))
            db.CreateTable("T", new[]
            {
                new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.Currency), new ColumnDefinition("F", DataType.Currency),
            }, "Id");

        // Past 2^38 or so, the round-trip form of a double can be another Currency than the one Access makes of it,
        // a digit further on: 35184372088832.0078125 reads back from 35184372088832.01, and was imported as that.
        var source = new DataTable("T");
        source.Columns.Add("Id", typeof(int));
        source.Columns.Add("D", typeof(double));
        source.Columns.Add("F", typeof(float));
        source.Rows.Add(1, 35184372088832.0078125, 131072.015625f);
        source.Rows.Add(2, 0.1, 0.1f);
        source.Rows.Add(3, -562949953421312.5, -2048.000244140625f);
        using (var db = Database.Open(_file!))
            db.ImportTable(source, new ImportOptions { AppendIfExists = true });

        using (var db = Database.Open(_file!))
        {
            var rows = db.GetTable("T").Rows.OrderBy(r => (int)r["Id"]!).Select(r => ((decimal)r["D"]!, (decimal)r["F"]!)).ToList();
            Assert.Equal(new[] { (35184372088832.0078m, 131072.0156m), (0.1m, 0.1m), (-562949953421312.5m, -2048.0002m) }, rows);

            // A number the Currency Access makes of does not read back as is refused, as it was: it has more places.
            foreach (object bad in new object[] { 0.03125, 0.00015, 1.234549999999999 })
            {
                var more = new DataTable("T");
                more.Columns.Add("Id", typeof(int));
                more.Columns.Add("D", typeof(double));
                more.Rows.Add(9, bad);
                var e = Assert.Throws<ArgumentException>(() => db.ImportTable(more, new ImportOptions { AppendIfExists = true }));
                Assert.Contains("four places", e.Message);
            }
            Assert.Equal(3, db.GetTable("T").Rows.Count());
        }
    }

    /// <summary>
    /// The doubles an import puts in a Currency column are those it put there before #65 - whose round-trip form has
    /// four places or fewer, or whose exact value has - each now as the Currency Access makes of it. Checked over
    /// doubles of every magnitude a Currency holds, near and away from halves of the fifth place.
    /// </summary>
    [Fact]
    public void An_import_takes_the_doubles_it_took_and_refuses_those_it_refused()
    {
        var random = new Random(65);
        var column = new ColumnDefinition("C", DataType.Currency).ToColumn("T");
        int taken = 0;
        for (int i = 0; i < 20_000; i++)
        {
            // A number of up to six places at a magnitude from 10^-6 to 10^15, as text, and the double nearest it.
            int places = random.Next(0, 7);
            double magnitude = Math.Pow(10, random.Next(-6, 16));
            double number = Math.Round(random.NextDouble() * magnitude, places) * (random.Next(2) == 0 ? 1 : -1);
            if (i % 3 == 0 && number != 0) number = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(number) + random.Next(-2, 3));

            // What the import took before: the number exactly or its round-trip form, whichever has four places or
            // fewer and is in the range.
            bool before = new[] { ExactNumber.Of(number), ExactNumber.Shortest(number) }.Any(n =>
                n.Scale <= 4 && n.ToDecimal() is decimal d && d >= -922337203685477.5808m && d <= 922337203685477.5807m);
            object? now;
            try { now = TypeMapper.ToColumnValue(number, column); }
            catch (TypeMapper.NotHeldException) { now = null; }

            Assert.True(before == (now is not null), $"{number:R}: taken before {before}, now {now is not null}");
            if (now is decimal held)
            {
                taken++;
                Assert.True(Util.CurrencyRounding.TryUnits(number, out long units) && held == Util.CurrencyRounding.ToDecimal(units), $"{number:R} is {held}");
            }
        }
        Assert.True(taken > 5_000, $"{taken} doubles taken");
    }
}
