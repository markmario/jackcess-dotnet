using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// An index cursor goes through its index and looks rows up in it and no other (#43), over the whole index or
/// between two of its entries, and finds the closest row to an entry (#29). Date/Time values are matched by the
/// number a row stores (#35).
/// </summary>
public sealed class IndexCursorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"index_cursor_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // ── The cursor's own index (#43) ─────────────────────────────────────────

    private Table Things(Database db)
    {
        var table = db.CreateTable("Things", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("A", DataType.Int32),
            new ColumnDefinition("B", DataType.Int32),
            new ColumnDefinition("Code", DataType.Text) { MaxLength = 20 },
        }, new[] { "Id" }, new[]
        {
            new IndexDefinition("ByA", "A"),
            new IndexDefinition("ByAB", "A", "B"),
            new IndexDefinition("ByCode", "Code"),
        });
        // A is the row's tens, B its units, in an order that is no index's.
        for (int i = 0; i < 100; i++)
        {
            int id = (i * 37) % 100;
            table.Insert(new Row { ["Id"] = id, ["A"] = id / 10, ["B"] = id % 10, ["Code"] = $"code {99 - id:00}" });
        }
        return table;
    }

    [Fact]
    public void A_lookup_by_entry_keeps_to_the_cursor_s_index()
    {
        using var db = Database.Create(_path);
        var table = Things(db);

        // The primary key's cursor looks in the primary key, though ByA comes to hand first with one column: the
        // row whose Id is 5, not the first row whose A is 5.
        var key = table.CreateIndexCursor();
        var found = key.FindRowByKey(5)!;
        Assert.Equal(5, found["Id"]);
        Assert.Same(found, key.Current);

        // A cursor for an index of one column, given two values, does not turn to the index that has two.
        var byA = table.CreateIndexCursor("ByA");
        var refused = Assert.Throws<ArgumentException>(() => byA.FindRowByKey(5, 3));
        Assert.Equal("keyValues", refused.ParamName);
        Assert.Contains("ByA", refused.Message);
        Assert.Throws<ArgumentException>(() => key.FindRowByKey(5, 3));
        Assert.Throws<ArgumentException>(() => byA.FindRowByKey());
        Assert.Throws<ArgumentException>(() => byA.FindRowByKey(null!));

        // Fewer values than the index has columns: the first row, in the index's order, with those leading values.
        var byAB = table.CreateIndexCursor("ByAB");
        Assert.Equal(50, byAB.FindRowByKey(5)!["Id"]);
        Assert.Equal(53, byAB.FindRowByKey(5, 3)!["Id"]);
        Assert.Null(byAB.FindRowByKey(5, 30));
        Assert.Equal(53, byAB.Current!["Id"]);            // left where it was

        // The values are the index's columns' and not the table's leading columns': Id 3 has A 0, B 3.
        Assert.Equal(3, byAB.FindRowByKey(0, 3)!["Id"]);

        // A null is never a match, and a value that is not one the column holds matches no row.
        Assert.Null(byA.FindRowByKey(new object?[] { null }));
        Assert.Null(byA.FindRowByKey("five"));
        Assert.Null(table.CreateIndexCursor("ByCode").FindRowByKey(5));   // text is matched as text: "5"
    }

    [Fact]
    public void A_lookup_by_column_keeps_to_the_cursor_s_order()
    {
        using var db = Database.Create(_path);
        var table = Things(db);

        // By the index's first column, through the index: the first of the rows with A 5 in the index's order.
        var byAB = table.CreateIndexCursor("ByAB");
        Assert.Equal(50, byAB.FindRow("A", 5)!["Id"]);
        Assert.Equal(51, byAB.FindNextRow("A", 5)!["Id"]);
        long reads = db.File.ReadCount;
        Assert.Equal(70, byAB.FindRow("A", 7)!["Id"]);
        Assert.True(db.File.ReadCount - reads <= 6, $"{db.File.ReadCount - reads} pages read to find a row through its index");

        // By another column: the first in the index's order all the same. B 3 comes first with A 0.
        Assert.Equal(3, byAB.FindRow("B", 3)!["Id"]);
        Assert.Equal(13, byAB.FindNextRow("B", 3)!["Id"]);

        // In ByCode's order - the reverse of Id's - the first row with B 3 is 93.
        var byCode = table.CreateIndexCursor("ByCode");
        Assert.Equal(93, byCode.FindRow("B", 3)!["Id"]);
        Assert.Equal(83, byCode.FindNextRow("B", 3)!["Id"]);

        // A column the table does not have is refused, not passed over.
        Assert.Throws<ArgumentException>(() => byCode.FindRow("Nope", 3));
    }

    [Fact]
    public void A_damaged_row_is_not_read_as_no_row()
    {
        using var db = Database.Create(_path);
        var table = Things(db);
        var key = table.CreateIndexCursor();
        var row = key.FindRowByKey(42)!;

        // The row's slot is made to point past the end of its page: the row cannot be read, and the lookup says
        // so instead of saying there is no such row.
        // (The slot is marked as one of a row whose values are elsewhere, and where the values were now names a
        // page the file has not.)
        var id = row.Id!.Value;
        byte[] page = db.File.ReadPage(id.PageNumber);
        int slot  = db.File.Format.OffsetDataRowTable + id.RowNumber * 2;
        int start = (page[slot] | page[slot + 1] << 8) & 0x1FFF;
        page[slot + 1] |= 0x40;
        page[start] = 0; page[start + 1] = 0xFF; page[start + 2] = 0xFF; page[start + 3] = 0x7F;
        db.File.WritePage(id.PageNumber, page);

        Assert.Throws<InvalidDataException>(() => table.CreateIndexCursor().FindRowByKey(42));
        Assert.Throws<InvalidDataException>(() => table.FindRowByPrimaryKey(42));
    }

    [Fact]
    public void An_index_cursor_goes_through_its_rows_in_the_index_s_order()
    {
        using var db = Database.Create(_path);
        var table = Things(db);

        Assert.Equal(Enumerable.Range(0, 100), table.CreateIndexCursor().Select(r => (int)r["Id"]!));
        Assert.Equal(Enumerable.Range(0, 100).Reverse(), table.CreateIndexCursor("ByCode").Select(r => (int)r["Id"]!));
        Assert.Equal(Enumerable.Range(0, 100), table.CreateIndexCursor("ByAB").Select(r => (int)r["Id"]!));
        Assert.Equal(Enumerable.Range(0, 100).Reverse(), table.CreateIndexCursor("ByAB").EnumerateBackward().Select(r => (int)r["Id"]!));

        // Rows with the same key come in the order they are stored.
        var byA = table.CreateIndexCursor("ByA").ToList();
        Assert.Equal(Enumerable.Range(0, 100).Select(i => i / 10), byA.Select(r => (int)r["A"]!));
        foreach (var group in byA.GroupBy(r => (int)r["A"]!))
            Assert.Equal(group.Select(r => r.Id!.Value).OrderBy(id => id), group.Select(r => r.Id!.Value));

        // Rows inserted and deleted while the cursor is on its way are in the index when it gets to them.
        var cursor = table.CreateIndexCursor();
        Assert.NotNull(cursor.FindRowByKey(10));
        table.Insert(new Row { ["Id"] = 1000, ["A"] = 1, ["B"] = 1, ["Code"] = "late" });
        table.Delete(table.FindRowByPrimaryKey(11)!);
        Assert.True(cursor.MoveNext());
        Assert.Equal(12, cursor.Current!["Id"]);
        while (cursor.MoveNext()) { }
        cursor.MovePrevious();
        Assert.Equal(1000, cursor.Current!["Id"]);
    }

    [Fact]
    public void A_descending_index_and_nulls_are_gone_through_as_the_index_holds_them()
    {
        using var db = Database.Create(_path);
        var down = new IndexDefinition("ByNDown", "N");
        down.DescendingColumns.Add("N");
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("N", DataType.Int32),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByN", "N"), down });
        var values = new int?[] { 5, null, 3, 9, null, 1, 7 };
        for (int i = 0; i < values.Length; i++) table.Insert(new Row { ["Id"] = i, ["N"] = values[i] });

        Assert.Equal(new int?[] { null, null, 1, 3, 5, 7, 9 }, table.CreateIndexCursor("ByN").Select(r => (int?)r["N"]));
        Assert.Equal(new int?[] { 9, 7, 5, 3, 1, null, null }, table.CreateIndexCursor("ByNDown").Select(r => (int?)r["N"]));

        // A range in a descending index runs from the greater value to the lesser.
        var range = new IndexRange { StartKey = new object?[] { 7 }, EndKey = new object?[] { 3 } };
        Assert.Equal(new int?[] { 7, 5, 3 }, table.CreateIndexCursor("ByNDown", range).Select(r => (int?)r["N"]));
        Assert.Empty(table.CreateIndexCursor("ByN", range));

        // A null is the value an index holds for no value: a range can begin or end at it.
        var nulls = new IndexRange { EndKey = new object?[] { null } };
        Assert.Equal(new[] { 1, 4 }, table.CreateIndexCursor("ByN", nulls).Select(r => (int)r["Id"]!));
        var afterNulls = new IndexRange { StartKey = new object?[] { null }, IsStartInclusive = false, EndKey = new object?[] { 3 } };
        Assert.Equal(new int?[] { 1, 3 }, table.CreateIndexCursor("ByN", afterNulls).Select(r => (int?)r["N"]));
    }

    // ── Ranges and the closest row (#29) ─────────────────────────────────────

    [Fact]
    public void A_cursor_over_a_range_has_the_rows_between_two_entries()
    {
        using var db = Database.Create(_path);
        var table = Things(db);

        List<int> Ids(string index, IndexRange range) => table.CreateIndexCursor(index, range).Select(r => (int)r["Id"]!).ToList();
        object?[] E(params object?[] values) => values;

        // Both ends in, as they are unless it is said otherwise; then each end out.
        Assert.Equal(Enumerable.Range(20, 21), Ids("PrimaryKey", new IndexRange { StartKey = E(20), EndKey = E(40) }));
        Assert.Equal(Enumerable.Range(21, 20), Ids("PrimaryKey", new IndexRange { StartKey = E(20), IsStartInclusive = false, EndKey = E(40) }));
        Assert.Equal(Enumerable.Range(20, 20), Ids("PrimaryKey", new IndexRange { StartKey = E(20), EndKey = E(40), IsEndInclusive = false }));
        Assert.Equal(Enumerable.Range(21, 19), Ids("PrimaryKey", new IndexRange { StartKey = E(20), IsStartInclusive = false, EndKey = E(40), IsEndInclusive = false }));

        // One end only, and ends that are no row's.
        Assert.Equal(Enumerable.Range(95, 5), Ids("PrimaryKey", new IndexRange { StartKey = E(95) }));
        Assert.Equal(Enumerable.Range(0, 3), Ids("PrimaryKey", new IndexRange { EndKey = E(2) }));
        Assert.Equal(Enumerable.Range(0, 100), Ids("PrimaryKey", new IndexRange { StartKey = E(-50), EndKey = E(5000) }));
        Assert.Empty(Ids("PrimaryKey", new IndexRange { StartKey = E(40), EndKey = E(20) }));
        Assert.Empty(Ids("PrimaryKey", new IndexRange { StartKey = E(200) }));
        Assert.Equal(new[] { 7 }, Ids("PrimaryKey", new IndexRange { StartKey = E(7), EndKey = E(7) }));

        // An index of two columns: whole entries, and entries of its first column, which stand for every row with it.
        Assert.Equal(Enumerable.Range(27, 9), Ids("ByAB", new IndexRange { StartKey = E(2, 7), EndKey = E(3, 5) }));
        Assert.Equal(Enumerable.Range(20, 20), Ids("ByAB", new IndexRange { StartKey = E(2), EndKey = E(3) }));
        Assert.Equal(Enumerable.Range(30, 10), Ids("ByAB", new IndexRange { StartKey = E(2), IsStartInclusive = false, EndKey = E(3) }));
        Assert.Equal(Enumerable.Range(20, 10), Ids("ByAB", new IndexRange { StartKey = E(2), EndKey = E(3), IsEndInclusive = false }));
        Assert.Equal(Enumerable.Range(25, 15), Ids("ByAB", new IndexRange { StartKey = E(2, 5), EndKey = E(3) }));

        // Text, in the index's order.
        Assert.Equal(new[] { 89, 88, 87 }, Ids("ByCode", new IndexRange { StartKey = E("code 10"), EndKey = E("code 12") }));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => 9 - i), Ids("ByCode", new IndexRange { StartKey = E("code 9") }));

        // A cursor over a range goes both ways, stays in its range, and looks up only within it.
        var cursor = table.CreateIndexCursor(new IndexRange { StartKey = E(20), EndKey = E(40) });
        Assert.Equal(Enumerable.Range(20, 21).Reverse(), cursor.EnumerateBackward().Select(r => (int)r["Id"]!));
        Assert.True(cursor.IsBeforeFirst);
        Assert.Equal(30, cursor.FindRowByKey(30)!["Id"]);
        Assert.Null(cursor.FindRowByKey(50));
        Assert.Null(cursor.FindRowByKey(10));
        Assert.Equal(30, cursor.Current!["Id"]);
        Assert.Equal(20, cursor.FindRow("B", 0)!["Id"]);
        Assert.Equal(30, cursor.FindNextRow("B", 0)!["Id"]);
        Assert.Equal(40, cursor.FindNextRow("B", 0)!["Id"]);
        Assert.Null(cursor.FindNextRow("B", 0));
        Assert.Equal(20, cursor.FindClosestRowByKey(3)!["Id"]);           // before the range: its first row
        Assert.Null(cursor.FindClosestRowByKey(41));
        Assert.True(cursor.IsAfterLast);

        // What a range cannot be.
        Assert.Throws<ArgumentNullException>(() => table.CreateIndexCursor((IndexRange)null!));
        Assert.Throws<ArgumentNullException>(() => table.CreateIndexCursor("ByA", null!));
        Assert.Throws<ArgumentException>(() => table.CreateIndexCursor("ByA", new IndexRange { StartKey = E(1, 2) }));
        Assert.Throws<ArgumentException>(() => table.CreateIndexCursor("ByA", new IndexRange { EndKey = E() }));
        Assert.Throws<ArgumentException>(() => table.CreateIndexCursor("ByA", new IndexRange { StartKey = E("one") }));
        Assert.Throws<ArgumentException>(() => table.CreateIndexCursor("Nope", new IndexRange()));
    }

    [Fact]
    public void The_closest_row_to_an_entry_is_the_first_at_or_after_it()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("N", DataType.Int32),
            new ColumnDefinition("S", DataType.Text) { MaxLength = 20 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByN", "N"), new IndexDefinition("ByNS", "N", "S") });
        for (int i = 1; i <= 50; i++)
            table.Insert(new Row { ["Id"] = i, ["N"] = i * 10, ["S"] = "s" + (char)('a' + i % 5) });

        var byN = table.CreateIndexCursor("ByN");
        Assert.Equal(100, byN.FindClosestRowByKey(100)!["N"]);             // the entry itself
        var closest = byN.FindClosestRowByKey(101)!;                       // the next after it
        Assert.Equal(110, closest["N"]);
        Assert.Same(closest, byN.Current);
        Assert.Equal(10, byN.FindClosestRowByKey(-7)!["N"]);
        Assert.True(byN.MoveNext());
        Assert.Equal(20, byN.Current!["N"]);                                 // the cursor goes on from there
        Assert.Null(byN.FindClosestRowByKey(501));
        Assert.True(byN.IsAfterLast);
        Assert.True(byN.MovePrevious());
        Assert.Equal(500, byN.Current!["N"]);

        var byNS = table.CreateIndexCursor("ByNS");
        Assert.Equal(11, byNS.FindClosestRowByKey(105)!["Id"]);
        Assert.Equal(10, byNS.FindClosestRowByKey(100, "sa")!["Id"]);
        Assert.Equal(11, byNS.FindClosestRowByKey(100, "sb")!["Id"]);     // row 10 has "sa": the next entry is row 11's

        Assert.Throws<ArgumentException>(() => byN.FindClosestRowByKey());
        Assert.Throws<ArgumentException>(() => byN.FindClosestRowByKey(1, 2));
        Assert.Throws<ArgumentException>(() => byN.FindClosestRowByKey("ten"));
    }

    public static IEnumerable<object[]> KeyTypes() => new[]
    {
        new object[] { DataType.Byte,     new object[] { (byte)1, (byte)7, (byte)200 },                                   (byte)8 },
        new object[] { DataType.Int16,    new object[] { (short)-300, (short)0, (short)300 },                             (short)1 },
        new object[] { DataType.Int32,    new object[] { -70000, 5, 70000 },                                             6 },
        new object[] { DataType.Int64,    new object[] { -5_000_000_000L, 5L, 5_000_000_000L },                          6L },
        new object[] { DataType.Single,   new object[] { -1.5f, 0.25f, 3e10f },                                          0.5f },
        new object[] { DataType.Double,   new object[] { -1e100, 0.1, 1e100 },                                           0.2 },
        new object[] { DataType.Currency, new object[] { -12.3456m, 0.0001m, 99999.9999m },                              0.0002m },
        new object[] { DataType.DateTime, new object[] { new DateTime(1850, 1, 2), new DateTime(2024, 2, 29, 12, 0, 0), new DateTime(2999, 12, 31) }, new DateTime(2024, 3, 1) },
        new object[] { DataType.DateTimeExtended, new object[] { new DateTime(1, 1, 2), new DateTime(2024, 2, 29).AddTicks(1), new DateTime(9999, 12, 31) }, new DateTime(2024, 2, 29).AddTicks(2) },
        new object[] { DataType.Boolean,  new object[] { true, false },                                                   false },
        new object[] { DataType.Text,     new object[] { "apple", "Banana", "cherry" },                                   "banana split" },
        new object[] { DataType.Guid,     new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new Guid("80000000-0000-0000-0000-000000000000"), new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff") }, new Guid("80000000-0000-0000-0000-000000000001") },
    };

    [Theory]
    [MemberData(nameof(KeyTypes))]
    public void Ranges_and_the_closest_row_work_for_every_type_a_key_is_made_for(DataType type, object[] sorted, object between)
    {
        var format = type is DataType.Int64 or DataType.DateTimeExtended ? FileFormat.Access2019 : FileFormat.Access2000;
        using var db = Database.Create(Path.ChangeExtension(_path, format == FileFormat.Access2019 ? ".accdb" : ".mdb"), format);
        try
        {
            var down = new IndexDefinition("Down", "V");
            down.DescendingColumns.Add("V");
            var table = db.CreateTable("T", new[]
            {
                new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("V", type) { MaxLength = type == DataType.Text ? 30 : null },
            }, new[] { "Id" }, new[] { new IndexDefinition("Up", "V"), down });
            // Inserted from the last to the first, so that the order is the index's and not the table's.
            for (int i = sorted.Length - 1; i >= 0; i--) table.Insert(new Row { ["Id"] = i, ["V"] = sorted[i] });

            Assert.Equal(sorted, table.CreateIndexCursor("Up").Select(r => r["V"]));
            Assert.Equal(Enumerable.Reverse(sorted), table.CreateIndexCursor("Down").Select(r => r["V"]));

            // From the second value on; up to it; and it alone.
            Assert.Equal(sorted.Skip(1), table.CreateIndexCursor("Up", new IndexRange { StartKey = new[] { sorted[1] } }).Select(r => r["V"]));
            Assert.Equal(sorted.Take(2), table.CreateIndexCursor("Up", new IndexRange { EndKey = new[] { sorted[1] } }).Select(r => r["V"]));
            Assert.Equal(sorted.Take(1), table.CreateIndexCursor("Up", new IndexRange { EndKey = new[] { sorted[1] }, IsEndInclusive = false }).Select(r => r["V"]));
            Assert.Equal(sorted.Take(2).Reverse(), table.CreateIndexCursor("Down", new IndexRange { StartKey = new[] { sorted[1] } }).Select(r => r["V"]));

            // Each value is found, and is the closest row to itself.
            foreach (object value in sorted)
            {
                Assert.Equal(value, table.CreateIndexCursor("Up").FindRowByKey(value)!["V"]);
                Assert.Equal(value, table.CreateIndexCursor("Down").FindClosestRowByKey(value)!["V"]);
            }

            // A value between the second and the one after it finds no row; the closest is the next in the index's
            // order. (A Yes/No has two values, and nothing lies between them.)
            if (type != DataType.Boolean)
            {
                Assert.Null(table.CreateIndexCursor("Up").FindRowByKey(between));
                Assert.Equal(sorted[2], table.CreateIndexCursor("Up").FindClosestRowByKey(between)?["V"]);
                Assert.Equal(sorted[1], table.CreateIndexCursor("Down").FindClosestRowByKey(between)?["V"]);
            }
        }
        finally
        {
            db.Dispose();
            File.Delete(Path.ChangeExtension(_path, ".accdb"));
        }
    }

    [Fact]
    public void A_range_or_a_closest_row_is_refused_for_a_key_this_library_does_not_make()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 255 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByName", "Name") });
        string longName = new string('\u00E9', 255);
        Assert.True(IndexKeys.IsCut(table.Indexes.Single(ix => ix.Name == "ByName").Columns, new object?[] { longName }));
        table.Insert(new Row { ["Id"] = 1, ["Name"] = longName });
        table.Insert(new Row { ["Id"] = 2, ["Name"] = "short" });

        // A key longer than an entry holds is cut, and a cut key is no place in the index's order.
        var cursor = table.CreateIndexCursor("ByName");
        Assert.Throws<NotSupportedException>(() => cursor.FindClosestRowByKey(longName));
        Assert.Throws<NotSupportedException>(() => table.CreateIndexCursor("ByName", new IndexRange { StartKey = new[] { longName } }));

        // The row is still found: the index's rows are gone through, each compared.
        Assert.Equal(1, cursor.FindRowByKey(longName)!["Id"]);
        Assert.Equal(1, cursor.FindRow("Name", longName)!["Id"]);
        Assert.Null(cursor.FindRowByKey(longName.Substring(1) + "y"));
        Assert.Equal(2, cursor.FindClosestRowByKey("s")!["Id"]);
    }

    [Fact]
    public void Some_of_the_columns_of_a_long_key_are_looked_for_by_what_a_cut_entry_keeps()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
            new ColumnDefinition("B", DataType.Text) { MaxLength = 255 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByAB", "A", "B") });
        var index = table.Indexes.Single(ix => ix.Name == "ByAB");
        var first = index.Columns.Take(1).ToList();

        // A first column whose key alone is 509 or 510 bytes: within what an entry holds, and reaching the two
        // bytes a cut entry ends in.
        string? a = null;
        for (int length = 200; length <= 255 && a is null; length++)
            foreach (char letter in new[] { 'x', '\u00E9' })
            {
                string candidate = new string(letter, length);
                int key = IndexKeys.EncodeWhole(first, new object?[] { candidate }).Length;
                if (key is 509 or 510) a = candidate;
            }
        Assert.NotNull(a);
        table.Insert(new Row { ["Id"] = 1, ["A"] = a, ["B"] = "a second column that makes the entry longer than it holds" });
        table.Insert(new Row { ["Id"] = 2, ["A"] = "short", ["B"] = "b" });
        Assert.True(IndexKeys.IsCut(index.Columns, new object?[] { a, "a second column that makes the entry longer than it holds" }));

        // Found by its first column all the same: the index's rows are gone through.
        var cursor = table.CreateIndexCursor("ByAB");
        Assert.Equal(1, cursor.FindRowByKey(a)!["Id"]);
        Assert.Equal(1, cursor.FindRow("A", a)!["Id"]);
        Assert.Equal(new[] { 1 }, cursor.FindRowsByKey(a).Select(r => (int)r["Id"]!));

        // And no place in the index's order for a range or a closest row.
        Assert.Throws<NotSupportedException>(() => cursor.FindClosestRowByKey(a));
        Assert.Throws<NotSupportedException>(() => table.CreateIndexCursor("ByAB", new IndexRange { StartKey = new object?[] { a } }));
    }

    [Fact]
    public void A_lookup_far_from_the_last_goes_down_the_tree_and_not_along_the_leaves()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Code", DataType.Text) { MaxLength = 60 },
        }, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") });
        table.InsertRange(Enumerable.Range(0, 20_000).Select(i => new Row { ["Id"] = i, ["Code"] = $"a code long enough that a leaf holds few of them {i:00000}" }));

        var cursor = table.CreateIndexCursor("ByCode");
        cursor.ColumnNames = new[] { "Id" };
        Assert.Equal(3, cursor.FindRowByKey("a code long enough that a leaf holds few of them 00003")!["Id"]);
        long reads = db.File.ReadCount;
        Assert.Equal(19_990, cursor.FindRowByKey("a code long enough that a leaf holds few of them 19990")!["Id"]);
        Assert.True(db.File.ReadCount - reads <= 8, $"{db.File.ReadCount - reads} pages read to find a row at the other end of the index");

        reads = db.File.ReadCount;
        Assert.Equal(4, cursor.FindClosestRowByKey("a code long enough that a leaf holds few of them 00004")!["Id"]);
        Assert.True(cursor.MovePrevious());
        Assert.Equal(3, cursor.Current!["Id"]);
        Assert.True(db.File.ReadCount - reads <= 10, $"{db.File.ReadCount - reads} pages read to come back to the beginning of the index");
    }

    // ── Dates by the number stored (#35) ─────────────────────────────────────

    [Fact]
    public void Two_dates_less_than_a_millisecond_apart_are_told_apart()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("When", DataType.DateTime),
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
        }, new[] { "When" }, new[] { new IndexDefinition("ByName", "Name") });

        var when = new DateTime(2024, 5, 6, 7, 8, 9);
        double first  = when.ToOADate();
        double second = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(first) + 1);   // the next double after it
        table.Insert(new Row { ["When"] = when, ["Name"] = "first" });
        table.Insert(new Row { ["When"] = when.AddDays(1), ["Name"] = "second" });

        // The second row is given the date a hair after the first's: both read as the same DateTime.
        var row = table.FindRowByPrimaryKey(when.AddDays(1))!;
        table.RewriteRowAt(row.Pointer, new Row { ["When"] = second });
        db.Dispose();

        using var read = Database.Open(_path);
        table = read.GetTable("T");
        Assert.Equal(new object[] { when, when }, table.Rows.Select(r => r["When"]!).ToArray());
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "T", 2);

        // Through the key's own index: a DateTime is the number it converts to, and a number is itself.
        Assert.Equal("first", table.FindRowByPrimaryKey(when)!["Name"]);
        Assert.Equal("first", table.FindRowByPrimaryKey(first)!["Name"]);
        Assert.Equal("second", table.FindRowByPrimaryKey(second)!["Name"]);
        Assert.Equal(new[] { "first", "second" }, table.CreateIndexCursor().Select(r => (string)r["Name"]!));

        // And by going through the rows.
        var byName = table.CreateIndexCursor("ByName");
        // A DateTime finds every row that reads as it; a number the row that stores it.
        Assert.Equal("first", byName.FindRow("When", when)!["Name"]);
        Assert.Equal("second", byName.FindNextRow("When", when)!["Name"]);
        Assert.Null(byName.FindNextRow("When", when));
        Assert.Equal("first", byName.FindRow("When", first)!["Name"]);    // a number is the stored number itself
        Assert.Equal("second", byName.FindRow("When", second)!["Name"]);
        Assert.Equal("second", table.CreateCursor().FindRow(new Row { ["When"] = second })!["Name"]);
        Assert.Null(byName.FindRow("When", "2024-05-06"));               // text is not a date

        // Each is updated and deleted on its own, by where it is.
        var hair = byName.FindRow("When", second)!;
        hair["Name"] = "the later one";
        table.Update(hair);
        table.Delete(byName.FindRow("When", first)!);
        Assert.Equal("the later one", Assert.Single(table.Rows)["Name"]);
        Assert.Equal(second, table.ReadRowAt(table.Rows.Single().Pointer, table.Columns)!["When"]);
    }

    [Fact]
    public void A_date_is_found_through_its_index_by_the_number_stored()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("When", DataType.DateTime),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByWhen", "When") });
        var start = new DateTime(2024, 1, 1);
        for (int i = 0; i < 500; i++)
            table.Insert(new Row { ["Id"] = i, ["When"] = start.AddSeconds(i * 7919) });

        var byWhen = table.CreateIndexCursor("ByWhen");
        for (int i = 0; i < 500; i += 23)
        {
            var when = start.AddSeconds(i * 7919);
            Assert.Equal(i, byWhen.FindRowByKey(when)!["Id"]);
            Assert.Equal(i, byWhen.FindRowByKey(when.ToOADate())!["Id"]);
            Assert.Null(byWhen.FindRowByKey(when.AddMilliseconds(1)));
            Assert.Equal(i + 1, byWhen.FindClosestRowByKey(when.AddMilliseconds(1))!["Id"]);
        }

        var range = new IndexRange { StartKey = new object?[] { start.AddSeconds(10 * 7919) }, EndKey = new object?[] { start.AddSeconds(20 * 7919) }, IsEndInclusive = false };
        Assert.Equal(Enumerable.Range(10, 10), table.CreateIndexCursor("ByWhen", range).Select(r => (int)r["Id"]!));
    }
}
