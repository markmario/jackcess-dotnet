using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A row keeps its place when it is updated, as Access keeps it (#29): rewritten in its slot when it fits its
/// page, and otherwise moved to another page with its slot left pointing there. So a row's id does not change,
/// and updating rows while going through them - with a cursor, a joiner or <see cref="Table.Rows"/> - comes to
/// each row once. With that: dates looked for as they read, text as its sort order has it, and what an update
/// and a lookup read.
/// </summary>
public sealed class StableRowTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"stable_rows_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string Wide(int id, int length) => new((char)(0x3A9 + id % 20), length);   // not compressible

    private static ushort Slot(Database db, RowId id)
        => ByteUtil.GetUShort(db.File.ReadPage(id.PageNumber), db.File.Format.OffsetDataRowTable + id.RowNumber * JetFormat.SizeRowEntry);

    // ── Where an updated row is ──────────────────────────────────────────────

    [Fact]
    public void An_updated_row_keeps_its_slot_whether_it_shrinks_grows_or_no_longer_fits_its_page()
    {
        using (var db = Database.Create(_path))
        {
            var table = db.CreateTable("T", new[]
            {
                new ColumnDefinition("Id", DataType.Int32),
                new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
                new ColumnDefinition("B", DataType.Text) { MaxLength = 255 },
                new ColumnDefinition("C", DataType.Text) { MaxLength = 255 },
            }, new[] { "Id" }, new[] { new IndexDefinition("ByA", "A") });
            for (int i = 1; i <= 40; i++) table.Insert(new Row { ["Id"] = i, ["A"] = Wide(i, 100) });
            var ids = table.Rows.ToDictionary(r => (int)r["Id"]!, r => r.Id!.Value);
            int firstPage = ids[1].PageNumber;
            Assert.True(ids.Values.Count(id => id.PageNumber == firstPage) > 5);

            void Update(int id, Row values)
            {
                var row = table.FindRowByPrimaryKey(id)!;
                foreach (var v in values) row[v.Key] = v.Value;
                table.Update(row);
                Assert.Equal(ids[id], row.Id);
                Assert.Equal(ids[id], table.FindRowByPrimaryKey(id)!.Id);
            }
            int Free(int page) => ByteUtil.GetShort(db.File.ReadPage(page), JetFormat.OffsetDataFreeSpace);

            // Smaller: the page has the room the row gave up, and no slot more.
            int free = Free(firstPage), slots = ByteUtil.GetShort(db.File.ReadPage(firstPage), db.File.Format.OffsetDataNumRows);
            Update(2, new Row { ["A"] = Wide(2, 10) });
            Assert.Equal(free + 180, Free(firstPage));
            Assert.Equal(0, Slot(db, ids[2]) & 0xC000);

            // Larger, within the page's room.
            Update(2, new Row { ["A"] = Wide(2, 60) });
            Assert.Equal(free + 80, Free(firstPage));
            Assert.Equal(slots, ByteUtil.GetShort(db.File.ReadPage(firstPage), db.File.Format.OffsetDataNumRows));

            // Larger than the page's room: the slot points to where the row's values now are.
            Update(3, new Row { ["A"] = Wide(3, 255), ["B"] = Wide(4, 255), ["C"] = Wide(5, 255) });
            Assert.Equal(JetFormat.OverflowRowFlag, Slot(db, ids[3]) & 0xC000);
            byte[] page = db.File.ReadPage(firstPage);
            int at = Slot(db, ids[3]) & JetFormat.RowOffsetMask;
            var there = new RowId(page[at + 1] | page[at + 2] << 8 | page[at + 3] << 16, page[at]);
            Assert.NotEqual(firstPage, there.PageNumber);
            Assert.Equal(JetFormat.DeletedRowFlag, Slot(db, there) & 0xC000);        // kept out of a scan
            Assert.Equal(40, table.Rows.Count());
            Assert.Equal(40, table.CreateCursor().Count());

            // Changed where its values are; and home again once it fits there, the place it left emptied.
            Update(3, new Row { ["C"] = Wide(6, 200) });
            Assert.Equal(JetFormat.OverflowRowFlag, Slot(db, ids[3]) & 0xC000);
            Update(3, new Row { ["A"] = "home", ["B"] = null, ["C"] = null });
            Assert.Equal(0, Slot(db, ids[3]) & 0xC000);
            Assert.Equal(JetFormat.DeletedRowFlags, Slot(db, there) & 0xC000);

            // Away again, and deleted from there.
            Update(3, new Row { ["A"] = Wide(3, 255), ["B"] = Wide(4, 255), ["C"] = Wide(5, 255) });
            Update(4, new Row { ["A"] = Wide(4, 255), ["B"] = Wide(4, 255), ["C"] = Wide(5, 255) });
            table.Delete(table.FindRowByPrimaryKey(4)!);
            Assert.Equal(39, table.RowCount);
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            Assert.Equal(39, table.Rows.Count());
            Assert.Equal(Wide(2, 60), table.FindRowByPrimaryKey(2)!["A"]);
            Assert.Equal(Wide(5, 255), table.FindRowByPrimaryKey(3)!["C"]);
            Assert.Equal(3, table.CreateIndexCursor("ByA").FindRowByKey(Wide(3, 255))!["Id"]);
            Assert.Null(table.FindRowByPrimaryKey(4));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 39);
        }
    }

    [Fact]
    public void An_update_that_fails_while_it_is_written_is_taken_back()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
            new ColumnDefinition("Notes", DataType.Memo),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByA", "A") });
        for (int i = 1; i <= 30; i++) table.Insert(new Row { ["Id"] = i, ["A"] = Wide(i, 120), ["Notes"] = "n" });
        var before = IndexChecks.AllPages(db);

        for (int writes = 0; writes < 500; writes++)
        {
            var row = table.FindRowByPrimaryKey(5)!;
            row["A"] = Wide(9, 255);
            row["Notes"] = new string('m', 9000);
            db.File.WritesBeforeFailure = writes;
            var thrown = Record.Exception(() => table.Update(row));
            db.File.WritesBeforeFailure = null;
            if (thrown is null) break;
            Assert.IsType<IOException>(thrown);
            IndexChecks.AssertUnchanged(before, db);
        }
        Assert.Equal(Wide(9, 255), table.FindRowByPrimaryKey(5)!["A"]);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 30);
    }

    // ── Updating rows while going through them ───────────────────────────────

    private Table Counted(Database db, int rows)
    {
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32),
            new ColumnDefinition("N", DataType.Int32), new ColumnDefinition("Pad", DataType.Text) { MaxLength = 200 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByK", "K") });
        for (int i = 0; i < rows; i++)
            table.Insert(new Row { ["Id"] = (i * 37) % rows + 1, ["K"] = ((i * 37) % rows + 1) * 10, ["N"] = 0, ["Pad"] = "p" });
        return table;
    }

    private static void AssertEachUpdatedOnce(Database db, int rows, int updates)
    {
        Assert.Equal(rows, updates);
        Assert.All(db.GetTable("T").Rows, r => Assert.Equal(1, r["N"]));
        Assert.Equal(rows, db.GetTable("T").RowCount);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", rows);
    }

    public static IEnumerable<object[]> Loops()
    {
        foreach (string index in new[] { "PrimaryKey", "ByK" })
            foreach (bool forwards in new[] { true, false })
                foreach (int change in new[] { 0, 100_000, -100_000 })
                    foreach (bool throughTable in new[] { true, false })
                        yield return new object[] { index, forwards, change, throughTable };
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void Each_row_is_updated_once_through_an_index_cursor_whatever_the_update_does_to_its_key(string index, bool forwards, int change, bool throughTable)
    {
        const int Rows = 300;
        using var db = Database.Create(_path);
        var table  = Counted(db, Rows);
        var cursor = table.CreateIndexCursor(index);
        string key = index == "ByK" ? "K" : "Id";

        // Each row's key in the cursor's index is raised, lowered or left, and grows the row past its page's room.
        int updates = 0;
        if (!forwards) cursor.MoveAfterLast();
        while (forwards ? cursor.MoveNext() : cursor.MovePrevious())
        {
            Assert.True(++updates <= Rows * 2, "the cursor keeps coming to rows it has updated");
            var row = cursor.Current!;
            var values = new Row { [key] = (int)row[key]! + change, ["N"] = (int)row["N"]! + 1, ["Pad"] = Wide(updates, 150) };
            if (throughTable)
            {
                foreach (var v in values) row[v.Key] = v.Value;
                table.Update(row);
            }
            else cursor.UpdateCurrentRow(values);
        }
        AssertEachUpdatedOnce(db, Rows, updates);
    }

    [Fact]
    public void Each_row_is_updated_once_going_through_Rows_and_through_a_table_cursor()
    {
        const int Rows = 300;
        using var db = Database.Create(_path);
        var table = Counted(db, Rows);

        int updates = 0;
        foreach (var row in table.Rows)
        {
            row["N"] = (int)row["N"]! + 1;
            row["Pad"] = Wide(updates, 180);
            table.Update(row);
            Assert.True(++updates <= Rows * 2);
        }
        AssertEachUpdatedOnce(db, Rows, updates);

        updates = 0;
        var cursor = table.CreateCursor();
        while (cursor.MoveNext())
        {
            cursor.UpdateCurrentRow(new Row { ["N"] = 1, ["K"] = -(int)cursor.Current!["K"]!, ["Pad"] = "small again " + updates });
            Assert.True(++updates <= Rows * 2);
        }
        AssertEachUpdatedOnce(db, Rows, updates);
    }

    [Fact]
    public void Each_row_a_joiner_gives_is_updated_once()
    {
        using var db = Database.Create(_path);
        var customers = db.CreateTable("C", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id");
        var orders = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32),
            new ColumnDefinition("N", DataType.Int32), new ColumnDefinition("Pad", DataType.Text) { MaxLength = 200 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByK", "K") });
        for (int c = 1; c <= 20; c++) customers.Insert(new Row { ["Id"] = c });
        for (int i = 1; i <= 300; i++) orders.Insert(new Row { ["Id"] = i, ["K"] = i % 20 + 1, ["N"] = 0, ["Pad"] = "p" });

        var ordersOf = new Joiner(customers, new[] { "Id" }, orders, new[] { "K" });
        int updates = 0;
        foreach (var customer in customers.Rows)
            foreach (var order in ordersOf.FindRows(customer))
            {
                Assert.True(++updates <= 600, "the joiner keeps giving rows that were updated");
                order["N"] = (int)order["N"]! + 1;
                order["Pad"] = Wide(updates, 190);
                orders.Update(order);
            }
        AssertEachUpdatedOnce(db, 300, updates);

        // And through one cursor's rows of an entry, the entry's own key changed under it: each row once, none left.
        updates = 0;
        var cursor = orders.CreateIndexCursor("ByK");
        foreach (var order in cursor.FindRowsByKey(7))
        {
            Assert.True(++updates <= 100);
            order["K"] = 8;
            orders.Update(order);
        }
        Assert.Equal(15, updates);
        Assert.Empty(orders.CreateIndexCursor("ByK").FindRowsByKey(7));
        Assert.Equal(30, orders.CreateIndexCursor("ByK").FindRowsByKey(8).Count());
    }

    // ── Dates ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_date_that_was_read_finds_its_row_again_whatever_number_the_row_stores()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("D", DataType.DateTime), new ColumnDefinition("Id", DataType.Int32),
        }, new[] { "D" }, new[] { new IndexDefinition("ById", "Id") });
        var other = db.CreateTable("O", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTime),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByD", "D") });

        // Dates as Access computes them: a day, and the hours, minutes and seconds added as fractions of one.
        // A third of them are not the number DateTime.ToOADate gives for the date they read as. Among them dates
        // before 30 December 1899, where a number's fraction counts forwards from a day that counts backwards.
        var random = new Random(35);
        var stored = new List<double>();
        while (stored.Count < 400)
        {
            int day = stored.Count % 5 == 0 ? -random.Next(2, 30_000) : random.Next(2, 60_000);
            double sign = day < 0 ? -1 : 1;
            double number = ((day + sign * random.Next(24) / 24.0) + sign * random.Next(60) / 1440.0) + sign * random.Next(60) / 86400.0;
            if (!stored.Contains(number)) stored.Add(number);
        }
        for (int i = 0; i < stored.Count; i++)
        {
            var read = ValueMatch.ReadsAs(stored[i])!.Value;
            table.Insert(new Row { ["D"] = read.AddDays(100_000), ["Id"] = i });
            table.RewriteRowAt(table.CreateIndexCursor("ById").FindRowByKey(i)!.Pointer, new Row { ["D"] = stored[i] });
            other.Insert(new Row { ["Id"] = i, ["D"] = read });
            other.RewriteRowAt(other.FindRowByPrimaryKey(i)!.Pointer, new Row { ["D"] = stored[i] });
        }
        Assert.True(stored.Count(s => BitConverter.DoubleToInt64Bits(ValueMatch.ReadsAs(s)!.Value.ToOADate()) != BitConverter.DoubleToInt64Bits(s)) > 40);

        var join   = new Joiner(table, new[] { "D" }, other, new[] { "D" });
        var cursor = table.CreateIndexCursor();
        foreach (var row in table.Rows.ToList())
        {
            var date = (DateTime)row["D"]!;
            int id = (int)row["Id"]!;
            Assert.Equal(id, table.FindRowByPrimaryKey(date)?["Id"]);
            Assert.Equal(id, table.CreateCursor().FindRow("D", date)?["Id"]);
            Assert.Equal(id, cursor.FindRowByKey(date)?["Id"]);
            Assert.Equal(id, cursor.FindClosestRowByKey(date)?["Id"]);
            Assert.Equal(new[] { id }, table.CreateIndexCursor(new IndexRange { StartKey = new object?[] { date }, EndKey = new object?[] { date } }).Select(r => (int)r["Id"]!));
            Assert.Empty(table.CreateIndexCursor(new IndexRange { StartKey = new object?[] { date }, IsStartInclusive = false, EndKey = new object?[] { date.AddMilliseconds(1) }, IsEndInclusive = false }));
            Assert.Equal(id, join.FindFirstRow(row)?["Id"]);
            Assert.True(join.HasRows(row));

            // The number itself, which the cursor gives, finds the row and no other.
            Assert.NotNull(cursor.FindRowByKey(date));
            double number = cursor.GetCurrentRowDateNumber("D")!.Value;
            Assert.Equal(stored[id], number);
            Assert.Equal(id, table.FindRowByPrimaryKey(number)?["Id"]);
            Assert.Null(table.FindRowByPrimaryKey(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(number) + 1)));
        }
        Assert.Throws<ArgumentException>(() => cursor.GetCurrentRowDateNumber("Id"));

        // A join is on the stored numbers: a row whose number is another that reads as the same date is not joined.
        var one = table.FindRowByPrimaryKey(stored[7])!;
        other.RewriteRowAt(other.FindRowByPrimaryKey(7)!.Pointer, new Row { ["D"] = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(stored[7]) + 1) });
        Assert.False(join.HasRows(one));
        Assert.Equal(7, other.CreateIndexCursor("ByD").FindRowByKey(one["D"])?["Id"]);   // though its date finds it
    }

    [Fact]
    public void A_time_of_day_is_found_whichever_sign_its_number_has()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("D", DataType.DateTime),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByD", "D") });
        var six = new DateTime(1899, 12, 30, 6, 0, 0);
        table.Insert(new Row { ["Id"] = 1, ["D"] = six });
        table.Insert(new Row { ["Id"] = 2, ["D"] = six.AddHours(1) });
        table.Insert(new Row { ["Id"] = 3, ["D"] = six.AddHours(2) });
        table.RewriteRowAt(table.FindRowByPrimaryKey(3)!.Pointer, new Row { ["D"] = -0.25 });   // six o'clock too

        Assert.Equal(new object[] { six, six.AddHours(1), six }, table.Rows.Select(r => r["D"]!));
        Assert.Equal(new[] { 3, 1 }, table.CreateIndexCursor("ByD").FindRowsByKey(six).Select(r => (int)r["Id"]!));
        Assert.Equal(new[] { 1, 3 }, new[] { table.CreateCursor().FindRow("D", six)!, table.CreateCursor().FindRow(new Row { ["D"] = six, ["Id"] = 3 })! }.Select(r => (int)r["Id"]!));
        Assert.Equal(3, table.CreateIndexCursor("ByD").FindRowByKey(-0.25)!["Id"]);
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> SortOrders() => new[] { new object[] { FileFormat.Access2000 }, new object[] { FileFormat.Access2010 } };

    [Theory]
    [MemberData(nameof(SortOrders))]
    public void Text_is_found_as_its_sort_order_has_it_by_a_table_cursor_and_an_index_cursor_alike(FileFormat format)
    {
        using var db = Database.Create(_path, format);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Code", DataType.Text) { MaxLength = 50 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") });
        string[] held = { "7", "abc", "e", "a", "ss", "ae", "co-op", "it's", "a b" };
        for (int i = 0; i < held.Length; i++) table.Insert(new Row { ["Id"] = i, ["Code"] = held[i] });

        // What Access's = says of each pair, measured with ACE 16 in General - Legacy and in General alike.
        var asked = new (string Value, string? Finds)[]
        {
            ("7", "7"), ("007", null), ("7.0", null), ("ABC", "abc"), ("é", null), ("a ", "a"), (" a", null),
            ("ß", "ss"), ("æ", "ae"), ("coop", null), ("its", null), ("ab", null), ("á", null), ("E", "e"),
        };
        foreach (var (value, finds) in asked)
        {
            var byScan  = table.CreateCursor().FindRow("Code", value);
            var byIndex = table.CreateIndexCursor("ByCode").FindRow("Code", value);
            var byEntry = table.CreateIndexCursor("ByCode").FindRowByKey(value);
            var byOther = table.CreateIndexCursor().FindRow("Code", value);     // an index cursor going through its rows
            Assert.Equal(finds, (string?)byScan?["Code"]);
            Assert.Equal(finds, (string?)byIndex?["Code"]);
            Assert.Equal(finds, (string?)byEntry?["Code"]);
            Assert.Equal(finds, (string?)byOther?["Code"]);
        }

        // A number looked for in a text column is the text that writes it; text looked for in a number column is no number.
        Assert.Equal("7", table.CreateCursor().FindRow("Code", 7)!["Code"]);
        Assert.Equal("7", table.CreateIndexCursor("ByCode").FindRowByKey(7)!["Code"]);
        Assert.Null(table.CreateCursor().FindRow("Id", "3"));
        Assert.Null(table.CreateIndexCursor().FindRowByKey("3"));
        Assert.Equal(3, table.CreateCursor().FindRow("Id", 3L)!["Id"]);
        Assert.Equal(3, table.CreateIndexCursor().FindRowByKey((short)3)!["Id"]);
    }

    // ── The columns' own rules ───────────────────────────────────────────────

    [Fact]
    public void An_update_of_a_row_a_cursor_s_update_and_a_batch_keep_a_column_s_Required_and_AllowZeroLength()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
            new ColumnDefinition("Other", DataType.Text) { MaxLength = 20 },
        }, "Id");
        table.Insert(new Row { ["Id"] = 1, ["Name"] = "one", ["Other"] = "o" });
        table.Columns.First(c => c.Name == "Name").Properties.Set("Required", true);
        table.Columns.First(c => c.Name == "Name").Properties.Set("AllowZeroLength", false);
        table.Properties.Save();
        var before = IndexChecks.AllPages(db);

        var row = table.FindRowByPrimaryKey(1)!;
        row["Name"] = null;
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));
        row["Name"] = "";
        Assert.Throws<ConstraintViolationException>(() => table.Update(row));

        var cursor = table.CreateCursor();
        Assert.True(cursor.MoveNext());
        Assert.Throws<ConstraintViolationException>(() => cursor.UpdateCurrentRow(new Row { ["Name"] = null }));
        Assert.Throws<ConstraintViolationException>(() => cursor.UpdateCurrentRow(new Row { ["Other"] = "p", ["Name"] = "" }));

        var batch = Assert.Throws<BatchInsertException>(() => table.InsertRange(new[] { new Row { ["Id"] = 2, ["Name"] = "two" }, new Row { ["Id"] = 3 } }));
        Assert.Equal(1, batch.RowIndex);
        Assert.IsType<ConstraintViolationException>(batch.InnerException);
        IndexChecks.AssertUnchanged(before, db);

        // Another column of the row is updated, and the column itself to a value it takes.
        cursor.UpdateCurrentRow(new Row { ["Other"] = "p" });
        row = table.FindRowByPrimaryKey(1)!;
        row["Name"] = "uno";
        table.Update(row);
        Assert.Equal("uno|p", table.Rows.Select(r => r["Name"] + "|" + r["Other"]).Single());
    }

    // ── What is read ─────────────────────────────────────────────────────────

    [Fact]
    public void An_update_does_not_read_a_long_value_it_does_not_change()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
            new ColumnDefinition("Notes", DataType.Memo),
        }, "Id");
        table.Insert(new Row { ["Id"] = 1, ["Name"] = "one", ["Notes"] = new string('m', 400_000) });
        table.Insert(new Row { ["Id"] = 2, ["Name"] = "two", ["Notes"] = "short" });

        long Reads(int id, bool readNotes)
        {
            var cursor = table.CreateIndexCursor();
            if (!readNotes) cursor.ColumnNames = new[] { "Id", "Name" };
            var row = cursor.FindRowByKey(id)!;
            row["Name"] = "renamed " + id + readNotes;
            long before = db.File.ReadCount;
            table.Update(row);
            return db.File.ReadCount - before;
        }
        long small = Reads(2, true);
        Assert.True(Reads(1, true) <= small + 2, "the Memo was read to update another column of a row read whole");
        Assert.True(Reads(1, false) <= small + 2, "the Memo was read to update another column");

        var cursorOn = table.CreateIndexCursor();
        cursorOn.ColumnNames = new[] { "Id" };
        Assert.NotNull(cursorOn.FindRowByKey(1));
        long reads = db.File.ReadCount;
        cursorOn.UpdateCurrentRow(new Row { ["Name"] = "by the cursor" });
        Assert.True(db.File.ReadCount - reads <= small + 3, $"{db.File.ReadCount - reads} pages read by a cursor's update of another column");
        Assert.Equal(400_000, ((string)table.FindRowByPrimaryKey(1)!["Notes"]!).Length);
        Assert.Equal("by the cursor", table.FindRowByPrimaryKey(1)!["Name"]);
    }

    [Fact]
    public void A_lookup_through_an_index_costs_the_same_in_a_large_table_as_in_a_small_one()
    {
        using var db = Database.Create(_path);
        long Cost(string name, int rows)
        {
            var table = db.CreateTable(name, new[]
            {
                new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Pad", DataType.Text) { MaxLength = 200 },
            }, "Id");
            table.InsertRange(Enumerable.Range(1, rows).Select(i => new Row { ["Id"] = i, ["Pad"] = Wide(i, 150) }));
            long before = db.File.ReadCount;
            Assert.Equal(rows / 2, table.FindRowByPrimaryKey(rows / 2)!["Id"]);
            Assert.Equal(rows / 2, table.CreateIndexCursor().FindRowByKey(rows / 2)!["Id"]);
            return db.File.ReadCount - before;
        }
        long small = Cost("Small", 200), large = Cost("Large", 40_000);
        Assert.True(large <= small + 4, $"two lookups read {large} pages in a table of 40,000 rows and {small} in one of 200");
    }
}
