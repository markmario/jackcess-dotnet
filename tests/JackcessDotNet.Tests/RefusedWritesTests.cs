using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A row that cannot be written changes nothing. An update writes its new row in place of the old - the old one
/// marked deleted, its long values freed, the new one added - so whatever can refuse the new row has to do so
/// before the first of those: a value its column cannot hold, refused only as the new row was encoded, cost the
/// row it was to replace (qcatco/exportmdb.interop#56). Each case here leaves the file the same bytes, and the row
/// where every index has it.
/// </summary>
public sealed class RefusedWritesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"refused_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static readonly Guid AGuid = new("11111111-2222-3333-4444-555555555555");

    private Table Create(Database db)
        => db.CreateTable("T", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
            new ColumnBuilder("B", DataType.Byte).Build(),
            new ColumnBuilder("N", DataType.Int).Build(),
            new ColumnBuilder("D", DataType.ShortDateTime).Build(),
            new ColumnBuilder("G", DataType.Guid).Build(),
            new ColumnBuilder("Blob", DataType.Ole).Build(),
            new ColumnBuilder("Notes", DataType.Memo).Build(),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByName", "Name") });

    private static Row Whole(int id) => new()
    {
        ["Id"] = id, ["Name"] = "name" + id, ["B"] = (byte)7, ["N"] = (short)12, ["D"] = new DateTime(2026, 9, 14),
        ["G"] = AGuid, ["Blob"] = Enumerable.Range(0, 6_000).Select(i => (byte)(i * id)).ToArray(),
        ["Notes"] = new string((char)('a' + id), 9_000),
    };

    // A value for each column that the column cannot hold or that is not of its type.
    public static IEnumerable<object[]> ValuesNoColumnHolds() => new[]
    {
        new object[] { "B", 300 },                         // more than a Byte
        new object[] { "N", 70_000 },                      // more than an Integer
        new object[] { "D", "not a date" },
        new object[] { "G", "not a guid" },
        new object[] { "Blob", "text, not bytes" },
        new object[] { "Name", new string('x', 51) },      // longer than the column
    };

    private void AssertRowsAreWhereTheirIndexesHaveThem(Table table, params int[] ids)
    {
        Assert.Equal(ids, table.ReadAllRows().Select(r => (int)r["Id"]!).OrderBy(i => i));
        Assert.Equal(ids.Length, table.RowCount);
        foreach (int id in ids)
        {
            var byKey = table.NewIndexCursor().FindRowByPrimaryKey(id);
            Assert.NotNull(byKey);
            Assert.Equal(new string((char)('a' + id), 9_000), byKey!["Notes"]);
            var byName = table.NewIndexCursor("ByName").FindRow("Name", "name" + id);
            Assert.NotNull(byName);
            Assert.Equal(id, byName!["Id"]);
            Assert.Equal(Whole(id)["Blob"], byName["Blob"]);
        }
    }

    [Theory]
    [MemberData(nameof(ValuesNoColumnHolds))]
    public void An_update_with_a_value_its_column_cannot_hold_changes_nothing(string column, object value)
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = Create(db);
            table.Insert(Whole(1));
            table.Insert(Whole(2));
        }
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            Assert.ThrowsAny<Exception>(() => table.UpdateByPrimaryKey(1, new Row { [column] = value, ["Notes"] = "replaced" }));
            AssertRowsAreWhereTheirIndexesHaveThem(table, 1, 2);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Theory]
    [MemberData(nameof(ValuesNoColumnHolds))]
    public void An_insert_with_a_value_its_column_cannot_hold_changes_nothing(string column, object value)
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = Create(db);
            table.Insert(Whole(1));
            table.Insert(Whole(2));
        }
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            var row = Whole(3);
            row[column] = value;
            Assert.ThrowsAny<Exception>(() => table.Insert(row));
            AssertRowsAreWhereTheirIndexesHaveThem(table, 1, 2);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));

        // And the table takes the next row as if nothing had been tried.
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            table.Insert(Whole(3));
            AssertRowsAreWhereTheirIndexesHaveThem(table, 1, 2, 3);
        }
    }

    [Fact]
    public void An_update_leaves_the_bytes_of_every_column_it_does_not_name()
    {
        // The merged row used to be made of the old one read and written again, and a value does not always come
        // back from that as it went: a Date/Time is a double, read to the millisecond, and one DateTime does not
        // hold is read as null. An update that names neither writes them back as they are.
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Create(db);
        table.Insert(Whole(1));
        var decoder = new RowDecoder(db.File.Format, table.Columns);
        var date = table.Columns.Single(c => c.Name == "D");

        foreach (double stored in new[] { 45000.123456789012, 3_000_000.5 /* past the year 9999 */, -700_000.25 /* before the year 100 */ })
        {
            int pointer = new IndexWriter(db.File, new PageAllocator(db.File)).FindRowByPrimaryKey(table.Definition, 1)!.Value;
            byte[] page = db.File.ReadPage(RowPointer.Page(pointer));
            int slot = ByteUtil.GetUShort(page, db.File.Format.OffsetDataRowTable + RowPointer.Row(pointer) * JetFormat.SizeRowEntry) & JetFormat.RowOffsetMask;
            int at = slot + db.File.Format.SizeRowColumnCount + date.FixedDataOffset;
            Array.Copy(BitConverter.GetBytes(stored), 0, page, at, 8);
            db.File.WritePage(RowPointer.Page(pointer), page);

            table.UpdateByPrimaryKey(1, new Row { ["Name"] = "name1" });

            int moved = new IndexWriter(db.File, new PageAllocator(db.File)).FindRowByPrimaryKey(table.Definition, 1)!.Value;
            byte[] row = JetPages.RowBytes(db.File, RowPointer.Page(moved), RowPointer.Row(moved));
            Assert.Equal(stored, decoder.DecodeForKey(row, date));
        }
        AssertRowsAreWhereTheirIndexesHaveThem(table, 1);
    }

    // ── Overflow pointers that lead nowhere ──────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_overflow_pointer_that_leads_nowhere_is_a_damaged_file_not_a_missing_row(bool outsideTheFile)
    {
        File.Copy(TestCorpus.File("V2003", "overflowV2003.mdb") ?? throw new FileNotFoundException("overflowV2003.mdb is not in the corpus."), _path, overwrite: true);
        using var db = Database.Open(_path);
        var table = db.GetTable("Table1");
        var format = db.File.Format;

        // The first overflow row of the table: a slot with 0x4000 alone.
        (int Page, int Start)? pointer = null;
        foreach (int number in UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow, format, db.File))
        {
            byte[] data = db.File.ReadPage(number);
            for (int r = 0; r < ByteUtil.GetShort(data, format.OffsetDataNumRows) && pointer is null; r++)
            {
                ushort slot = ByteUtil.GetUShort(data, format.OffsetDataRowTable + r * JetFormat.SizeRowEntry);
                if ((slot & 0xC000) == JetFormat.OverflowRowFlag) pointer = (number, slot & JetFormat.RowOffsetMask);
            }
        }
        Assert.NotNull(pointer);

        byte[] page = db.File.ReadPage(pointer!.Value.Page);
        if (outsideTheFile) ByteUtil.Put3ByteInt(page, pointer.Value.Start + 1, db.File.PageCount + 5);
        else                page[pointer.Value.Start] = 250;   // a row the page it names does not have
        db.File.WritePage(pointer.Value.Page, page);

        var damaged = Assert.Throws<InvalidDataException>(() => table.ReadAllRows());
        Assert.Contains(outsideTheFile ? "no such page" : "no such row", damaged.Message);
    }

    // ── Calculated columns (qcatco/exportmdb.interop#42) ─────────────────────

    [Fact]
    public void A_calculated_column_reads_as_null_and_its_table_is_not_written_to()
    {
        File.Copy(TestCorpus.File("V2010", "calcFieldV2010.accdb") ?? throw new FileNotFoundException("calcFieldV2010.accdb is not in the corpus."), _path, overwrite: true);
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Table1");

            // The fields Access shows an expression for (DAO, in qcatco/exportmdb.interop's CorpusInAccessTests).
            Assert.Equal(
                new[] { "AllNames", "BigNumTest", "BoolTest", "DecimalTest", "FloatTest", "IsRich", "LastFirst", "LastFirstLen", "MonthlySalary", "SalaryTest", "WeeklySalary" },
                table.Columns.Where(c => c.IsCalculated).Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));

            var rows = table.ReadAllRows();
            Assert.Equal(4, rows.Count);
            foreach (var row in rows)
            {
                foreach (var column in table.Columns.Where(c => c.IsCalculated))
                    Assert.Null(row.TryGetValue(column.Name, out var value) ? value : null);
                Assert.NotNull(row["FirstName"]);   // and the columns beside them are read
                Assert.NotNull(row["Salary"]);
            }
            Assert.Equal("Wayne", rows.Single(r => (int)r["ID"]! == 1)["LastName"]);

            var insert = Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["ID"] = 99, ["FirstName"] = "New" }));
            Assert.Contains("calculated", insert.Message);
            var update = Assert.Throws<NotSupportedException>(() => table.UpdateByPrimaryKey(1, new Row { ["FirstName"] = "Changed" }));
            Assert.Contains("calculated", update.Message);
            var delete = Assert.Throws<NotSupportedException>(() => table.DeleteRow("ID", 1));
            Assert.Contains("calculated", delete.Message);

            Assert.Equal(4, table.ReadAllRows().Count);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }
}
