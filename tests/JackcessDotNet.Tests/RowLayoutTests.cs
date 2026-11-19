using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Rows read and written by the table's own definition (<see cref="RowLayout"/>): each column where the definition
/// keeps it, a Decimal in Access's byte order, a row found where an overflow pointer leads, and a long value's
/// reference carrying what its first page has. What Access makes of the result is refereed where Access is, in
/// qcatco/exportmdb.interop's suite; these hold the same ground without it, on the files Access wrote in the corpus.
/// </summary>
public sealed class RowLayoutTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"layout_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void CopyOf(string version, string file)
        => File.Copy(TestCorpus.File(version, file) ?? throw new FileNotFoundException($"{file} is not in the corpus."), _path, overwrite: true);

    // Each row with the columns it has a value in.
    private static List<string> Rows(Table table)
        => table.Rows.ToList()
            .Select(row => string.Join(", ", row.Where(v => v.Value is not null).OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}")))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

    // ── Deleted columns ──────────────────────────────────────────────────────

    public static IEnumerable<object[]> FilesWithDeletedColumns() => new[]
    {
        new object[] { "V2000", "delColV2000.mdb" }, new object[] { "V2003", "delColV2003.mdb" },
        new object[] { "V2000", "indexV2000.mdb" }, new object[] { "V2003", "indexV2003.mdb" },
    };

    [Theory]
    [MemberData(nameof(FilesWithDeletedColumns))]
    public void A_row_added_to_a_table_that_has_had_a_column_deleted_reads_back_as_it_was_given(string version, string file)
    {
        // Access leaves the deleted column's number and its room in the row behind. Written by the columns that are
        // left, numbered and packed again, a row read back with its values in one another's places: in indexV2003's
        // Table1 an id of 1000 came back as 5.
        CopyOf(version, file);
        List<string> before;
        var added = new Row();
        using (var db = Database.Open(_path))
        {
            // The row added below names no row of the tables Table1 refers to: this is about where its values go.
            db.EnforceForeignKeys = false;
            var table = db.GetTable("Table1");
            Assert.True(table.Definition.MaxColumnCount > table.Columns.Count, "the table has had no column deleted");
            before = Rows(table);
            var template = table.Rows.ToList()[0];
            foreach (var column in table.Columns)
                added[column.Name] = column.DataType switch
                {
                    DataType.Int32 => 1000,
                    DataType.Text => "added",
                    DataType.Memo => "added memo",
                    _             => template.TryGetValue(column.Name, out var value) ? value : null,
                };
            table.Insert(added);
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Table1");
            var expected = before.Append(string.Join(", ", added.Where(v => v.Value is not null).OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}")))
                                 .OrderBy(line => line, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, Rows(table));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table1");
        }
    }

    [Fact]
    public void A_row_is_written_with_the_column_count_and_offsets_the_definition_has()
    {
        // delColV2003's Table1: six columns have been made and four are left.
        CopyOf("V2003", "delColV2003.mdb");
        using var db = Database.Open(_path);
        var table = db.GetTable("Table1");
        var layout = new RowLayout(db.File.Format, table.Definition);
        Assert.Equal(6, layout.MaxColumnCount);
        Assert.Equal(4, table.Columns.Count);
        Assert.Null(layout.WhyNotWritable);

        byte[] row = new RowEncoder(db.File.Format, table.Definition).Encode(new Row());
        Assert.Equal(6, ByteUtil.GetShort(row, 0));                               // every column there has been
        Assert.Equal(layout.MaxVarColumnCount, ByteUtil.GetShort(row, row.Length - 1 - 2));   // and every offset
    }

    // ── Where a column is kept is the definition's to say ────────────────────

    [Fact]
    public void A_table_this_library_creates_is_laid_out_as_it_always_was()
    {
        // A byte for a Yes/No column, which Access gives none: Access reads rows by the offsets the definition
        // gives, so it reads these, and files made by earlier releases have them.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Flag", DataType.Boolean), new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 10 }, new ColumnDefinition("Other", DataType.Boolean),
            new ColumnDefinition("When", DataType.DateTime), new ColumnDefinition("Notes", DataType.Memo),
        });

        Assert.Equal(new[] { "Flag fixed 0", "Id fixed 1", "Name variable 0", "Other fixed 5", "When fixed 6", "Notes variable 1" },
                     table.Columns.Select(c => c.IsVariableLength ? $"{c.Name} variable {c.VarLenTableIndex}" : $"{c.Name} fixed {c.FixedDataOffset}"));

        byte[] row = new RowEncoder(db.File.Format, table.Definition).Encode(
            new Row { ["Flag"] = true, ["Id"] = 0x01020304, ["Name"] = "ab", ["Other"] = true, ["When"] = new DateTime(1899, 12, 31) });
        Assert.Equal(
            "06-00-" + "00-04-03-02-01-00-00-00-00-00-00-00-F0-3F-" + "FF-FE-61-62-" + "14-00-14-00-10-00-02-00-" + "1F",
            BitConverter.ToString(row));

        // And read from the file again, the columns are where they were written.
        table.Insert(new Row { ["Flag"] = true, ["Id"] = 7, ["Name"] = "seven", ["Other"] = false, ["When"] = new DateTime(2020, 1, 2), ["Notes"] = "n" });
        var reopened = db.GetTable("T");
        Assert.Equal(table.Columns.Select(c => (c.Name, c.IsVariableLength, c.FixedDataOffset, c.VarLenTableIndex)),
                     reopened.Columns.Select(c => (c.Name, c.IsVariableLength, c.FixedDataOffset, c.VarLenTableIndex)));
        Assert.Equal("Flag=True, Id=7, Name=seven, Notes=n, Other=False, When=" + new DateTime(2020, 1, 2), Rows(reopened).Single());
    }

    [Fact]
    public void A_table_with_no_variable_length_column_has_at_least_two_bytes_of_fixed_length_values()
    {
        // As Access writes a table of one Yes/No or one Byte column: the row is long enough to hold, in its place,
        // where it went if it ever has to move.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var flags = db.CreateTable("Flags", new[] { new ColumnDefinition("A", DataType.Boolean) });
        var bytes = db.CreateTable("Bytes", new[] { new ColumnDefinition("B", DataType.Byte) });

        Assert.Equal("01-00-00-00-01", BitConverter.ToString(new RowEncoder(db.File.Format, flags.Definition).Encode(new Row { ["A"] = true })));
        Assert.Equal("01-00-07-00-01", BitConverter.ToString(new RowEncoder(db.File.Format, bytes.Definition).Encode(new Row { ["B"] = (byte)7 })));
    }

    [Fact]
    public void A_definition_that_puts_two_columns_in_one_place_is_not_written_by()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("T", new[] { new ColumnDefinition("A", DataType.Int32), new ColumnDefinition("B", DataType.Int32) }, primaryKey: "A")
              .Insert(new Row { ["A"] = 1, ["B"] = 2 });
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            table.Columns[1].FixedDataOffset = 2;   // as a damaged definition would have it: B over the end of A
            var refused = Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["A"] = 3, ["B"] = 4 }));
            Assert.Contains("same place", refused.Message);
            Assert.Throws<NotSupportedException>(() => table.Validate(new Row { ["A"] = 3 }));
            Assert.Throws<NotSupportedException>(() => table.UpdateByPrimaryKey(1, new Row { ["B"] = 5 }));
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_table_whose_columns_were_not_all_read_is_read_and_not_written_to()
    {
        // A definition that ends before its columns' names do: a damaged one. (One that goes on to a second page,
        // as common2V2003's MSP_PROJECTS does, is read whole and written to: MultiPageDefinitionTests.)
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("T", new[] { new ColumnDefinition("A", DataType.Int32) }).Insert(new Row { ["A"] = 1 });
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            table.Definition.HasUnreadColumns = true;
            Assert.Single(table.Rows);
            Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["A"] = 2 }));
            var refused = Assert.Throws<NotSupportedException>(() => new RowEncoder(db.File.Format, table.Definition).Encode(new Row()));
            Assert.Contains("ends before its columns' names do", refused.Message);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // ── Decimal ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("123.4567", 18, 4, "00-00-00-00-00-00-00-00-00-00-00-00-00-87-D6-12-00")]
    [InlineData("-99999999999999.9999", 18, 4, "80-00-00-00-00-00-00-00-00-B3-B6-E0-0D-FF-FF-63-A7")]
    [InlineData("0", 18, 4, "00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00")]
    [InlineData("9999999999999999999999999999", 28, 0, "00-00-00-00-00-5E-CE-4F-20-61-02-25-3E-FF-FF-FF-0F")]
    public void A_Decimal_is_four_words_the_most_significant_first_as_Access_writes_it(string value, int precision, int scale, string expected)
    {
        // The first two are the bytes ACE wrote for these numbers in a DECIMAL(18,4) column.
        decimal number = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        byte[] bytes = NumericCodec.Encode(number, precision, scale, "N");

        Assert.Equal(expected, BitConverter.ToString(bytes));
        Assert.Equal(number, NumericCodec.Decode(bytes, 0, scale));
    }

    [Fact]
    public void A_Decimal_with_more_digits_than_its_column_holds_is_refused_before_anything_is_written()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo),
            new ColumnDefinition("N", DataType.Decimal) { Precision = 5, Scale = 2 },
        });
        table.Insert(new Row { ["Id"] = 1, ["N"] = 999.99m });
        long written = db.File.WriteCount;

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Insert(new Row { ["Id"] = 2, ["Notes"] = new string('n', 5000), ["N"] = 1000m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Validate(new Row { ["Id"] = 2, ["N"] = -1000m }));

        Assert.Equal(written, db.File.WriteCount);   // not the long value either
        Assert.Equal(999.99m, table.Rows.Single()["N"]);
    }

    // ── Overflow rows ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("V2000", "overflowV2000.mdb")]
    [InlineData("V2003", "overflowV2003.mdb")]
    public void Rows_Access_moved_to_another_page_are_read_rewritten_and_deleted(string version, string file)
    {
        CopyOf(version, file);
        int moved;
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Table1");
            moved = OverflowSlots(db, table);
            Assert.True(moved > 0, "the file has no overflow row");
            Assert.Equal(table.RowCount, table.Rows.Count());     // none passed over
            Assert.Equal(table.RowCount, table.CreateCursor().Count());
        }

        // The first of them rewritten and the rest deleted, as any row is: the others are as they were, the indexes
        // hold exactly the rows, and no slot is left holding a moved row's values.
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Table1");
            var before = Rows(table);
            var pointers = OverflowPointers(db, table).ToList();
            var textColumn = table.Columns.First(c => c.DataType == DataType.Text);

            var rewritten = table.ReadRowAt(pointers[0], table.Columns)!;
            string was = Line(rewritten);
            table.RewriteRowAt(pointers[0], new Row { [textColumn.Name] = "rewritten" });
            rewritten[textColumn.Name] = "rewritten";
            var expected = before.Where(line => line != was).Append(Line(rewritten)).ToList();

            foreach (int pointer in pointers.Skip(1))
            {
                expected.Remove(Line(table.ReadRowAt(pointer, table.Columns)!));
                table.DeleteRowAt(pointer);
            }

            expected.Sort(StringComparer.Ordinal);
            Assert.Equal(expected, Rows(table));
            Assert.Equal(expected.Count, table.RowCount);
            Assert.Equal(0, OverflowSlots(db, table));
            Assert.Equal(0, SlotsHoldingMovedValues(db, table));
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table1");
        }
    }

    private static string Line(Row row)
        => string.Join(", ", row.Where(v => v.Value is not null).OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"));

    /// <summary>Slots marked as a moved row's values (0x8000 alone): after its row is deleted or rewritten, none is.</summary>
    private static int SlotsHoldingMovedValues(Database db, Table table)
    {
        var format = db.File.Format;
        int count = 0;
        foreach (int page in UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow, format, db.File))
        {
            byte[] bytes = db.File.ReadPage(page);
            for (int row = 0; row < ByteUtil.GetShort(bytes, format.OffsetDataNumRows); row++)
                if ((ByteUtil.GetUShort(bytes, format.OffsetDataRowTable + row * JetFormat.SizeRowEntry) & JetFormat.DeletedRowFlags) == JetFormat.DeletedRowFlag)
                    count++;
        }
        return count;
    }

    private static IEnumerable<int> OverflowPointers(Database db, Table table)
    {
        var format = db.File.Format;
        var pages = UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow, format, db.File);
        foreach (int page in pages)
        {
            byte[] bytes = db.File.ReadPage(page);
            for (int row = 0; row < ByteUtil.GetShort(bytes, format.OffsetDataNumRows); row++)
                if ((ByteUtil.GetUShort(bytes, format.OffsetDataRowTable + row * JetFormat.SizeRowEntry) & JetFormat.DeletedRowFlags) == JetFormat.OverflowRowFlag)
                    yield return RowPointer.Pack(page, row);
        }
    }

    private static int OverflowSlots(Database db, Table table) => OverflowPointers(db, table).Count();

    [Fact]
    public void A_scan_of_a_table_with_no_overflow_row_reads_each_of_its_pages_once()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 } });
        for (int i = 0; i < 5_000; i++)
            table.Insert(new Row { ["Id"] = i, ["Name"] = "row " + i });
        int pages = UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow, db.File.Format, db.File).Count;

        long before = db.File.ReadCount;
        Assert.Equal(5_000, table.Rows.Count());
        long read = db.File.ReadCount - before;

        Assert.True(read <= pages + 2, $"{read} pages were read to scan {pages}");
    }

    [Fact]
    public void A_deleted_row_is_marked_as_Access_marks_one()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) }, primaryKey: "Id");
        table.Insert(new Row { ["Id"] = 1 });
        table.Insert(new Row { ["Id"] = 2 });
        table.DeleteRow("Id", 1);
        table.UpdateByPrimaryKey(2, new Row { ["Id"] = 3 });

        int page = UsageMap.GetLastPage(db.File, table.Definition.UmapPageNumber, table.Definition.OwnedPagesRow);
        byte[] bytes = db.File.ReadPage(page);
        ushort Slot(int row) => ByteUtil.GetUShort(bytes, db.File.Format.OffsetDataRowTable + row * JetFormat.SizeRowEntry);
        Assert.Equal(JetFormat.DeletedRowFlags, Slot(0) & JetFormat.DeletedRowFlags);   // deleted
        Assert.Equal(0, Slot(1) & JetFormat.DeletedRowFlags);                           // updated where it is
        Assert.Equal(2, ByteUtil.GetShort(bytes, db.File.Format.OffsetDataNumRows));    // and no slot more for it
        Assert.Equal(3, table.Rows.Single()["Id"]);
    }

    // ── Long values ──────────────────────────────────────────────────────────

    private static (Table table, Column blob) BlobTable(Database db)
    {
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
            new ColumnDefinition("Blob", DataType.OleObject),
        }, primaryKey: "Id");
        return (table, table.Columns[2]);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>The 12 bytes a row holds for its long value: length and kind, row, page, stamp.</summary>
    private static byte[] Reference(Database db, Table table, Column blob, int id)
    {
        var decoder = new RowDecoder(db.File.Format, table.Columns);
        var format = db.File.Format;
        foreach (int page in UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow, format, db.File))
        {
            byte[] bytes = db.File.ReadPage(page);
            for (int row = 0; row < ByteUtil.GetShort(bytes, format.OffsetDataNumRows); row++)
                if (DataRows.Read(db.File, bytes, row) is { } rowBytes && Equals(decoder.Decode(rowBytes, table.Columns[0]), id))
                    return decoder.RawVariableValue(rowBytes, blob)!;
        }
        throw new InvalidOperationException($"No row {id}.");
    }

    [Fact]
    public void A_chain_s_reference_carries_the_stamp_of_the_page_it_starts_on()
    {
        // Access compares the last four bytes of a chain's reference with four bytes of the chain's first page, and
        // does not read the value when they differ. A page Access stamped, emptied by a delete and taken for a new
        // chain, keeps its stamp: the reference must have it, where it had zeros.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var (table, blob) = BlobTable(db);
        table.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(9_000, 1) });
        byte[] first = Reference(db, table, blob, 1);
        Assert.Equal(0, ByteUtil.GetInt(first, 8));               // a page this library made has none
        int page = ByteUtil.Get3ByteInt(first, 5);

        byte[] stamped = db.File.ReadPage(page);
        ByteUtil.PutInt(stamped, LvalWriter.OffsetLvalPageStamp, 0x0A5D6365);   // as Access stamps one
        db.File.WritePage(page, stamped);
        table.DeleteRow("Id", 1);
        table.Insert(new Row { ["Id"] = 2, ["Blob"] = Bytes(9_000, 2) });

        byte[] second = Reference(db, table, blob, 2);
        Assert.Equal(page, ByteUtil.Get3ByteInt(second, 5));      // the emptied page, taken again
        Assert.Equal(0x0A5D6365, ByteUtil.GetInt(second, 8));
        Assert.Equal(Bytes(9_000, 2), table.Rows.Single()["Blob"]);
    }

    [Fact]
    public void An_update_of_another_column_leaves_a_long_value_where_it_is()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var (table, blob) = BlobTable(db);
        table.Insert(new Row { ["Id"] = 1, ["Name"] = "one", ["Blob"] = Bytes(50_000, 1) });
        byte[] reference = Reference(db, table, blob, 1);
        var lval = table.Definition.LvalColumnUmapPages["Blob"];
        var pages = UsageMap.GetOwnedPages(db.File.ReadPage(lval.OwnedPage), lval.OwnedRow, db.File.Format, db.File);
        var contents = pages.Select(p => db.File.ReadPage(p)).ToList();
        long read = db.File.ReadCount;

        table.UpdateByPrimaryKey(1, new Row { ["Name"] = "renamed" });

        Assert.Equal(reference, Reference(db, table, blob, 1));   // the same reference, to the same pages
        Assert.Equal(pages, UsageMap.GetOwnedPages(db.File.ReadPage(lval.OwnedPage), lval.OwnedRow, db.File.Format, db.File));
        Assert.All(pages.Zip(contents, (p, was) => (p, was)), x => Assert.Equal(x.was, db.File.ReadPage(x.p)));
        var row = table.Rows.Single();
        Assert.Equal("renamed", row["Name"]);
        Assert.Equal(Bytes(50_000, 1), row["Blob"]);

        // And a change of the long value itself frees the old one and writes the new.
        table.UpdateByPrimaryKey(1, new Row { ["Blob"] = Bytes(100, 2) });
        Assert.Equal(Bytes(100, 2), table.Rows.Single()["Blob"]);
        Assert.NotEqual(reference, Reference(db, table, blob, 1));
    }

    // ── What a review of the first cut asked for ─────────────────────────────

    [Fact]
    public void A_row_an_earlier_release_deleted_stays_deleted()
    {
        // Releases before this marked a deleted row with 0x8000 alone, which is also the mark of a moved row's
        // values. Such a slot is only ever read by following a pointer to it, and nothing points at a deleted row:
        // it is passed over, as it always was.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var (table, _) = BlobTable(db);
        for (int i = 1; i <= 6; i++)
            table.Insert(new Row { ["Id"] = i, ["Name"] = "row " + i, ["Blob"] = Bytes(100 * i, i) });
        table.DeleteRow("Id", 2);
        table.DeleteRow("Id", 5);

        // The marking as it was: every deleted slot of the table's pages back to 0x8000 alone.
        var format = db.File.Format;
        foreach (int page in UsageMap.GetOwnedPages(db.File.ReadPage(table.Definition.UmapPageNumber), table.Definition.OwnedPagesRow, format, db.File))
        {
            byte[] bytes = db.File.ReadPage(page);
            for (int row = 0; row < ByteUtil.GetShort(bytes, format.OffsetDataNumRows); row++)
            {
                int at = format.OffsetDataRowTable + row * JetFormat.SizeRowEntry;
                if ((ByteUtil.GetUShort(bytes, at) & JetFormat.DeletedRowFlags) == JetFormat.DeletedRowFlags)
                    ByteUtil.PutUShort(bytes, at, (ushort)(ByteUtil.GetUShort(bytes, at) & ~JetFormat.OverflowRowFlag));
            }
            db.File.WritePage(page, bytes);
        }

        Assert.Equal(new[] { 1, 3, 4, 6 }, table.Rows.Select(r => (int)r["Id"]!));
        Assert.Equal(new[] { 1, 3, 4, 6 }, table.CreateCursor().Select(r => (int)r["Id"]!));
        Assert.Null(table.FindRowByPrimaryKey(2));
        table.Insert(new Row { ["Id"] = 2, ["Name"] = "again" });   // its key is free
        table.UpdateByPrimaryKey(3, new Row { ["Name"] = "three" });
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, table.Rows.Select(r => (int)r["Id"]!).OrderBy(i => i));
    }

    [Fact]
    public void An_overflow_pointer_to_a_page_of_another_table_is_a_damaged_file()
    {
        CopyOf("V2003", "overflowV2003.mdb");
        using var db = Database.Open(_path);
        var table = db.GetTable("Table1");
        int pointer = OverflowPointers(db, table).First();
        byte[] page = db.File.ReadPage(RowPointer.Page(pointer));
        int start = ByteUtil.GetUShort(page, db.File.Format.OffsetDataRowTable + RowPointer.Row(pointer) * JetFormat.SizeRowEntry) & JetFormat.RowOffsetMask;
        ByteUtil.Put3ByteInt(page, start + 1, JetFormat.PageSystemCatalog + 1);   // some page that is not the table's
        db.File.WritePage(RowPointer.Page(pointer), page);

        var damaged = Assert.Throws<InvalidDataException>(() => table.Rows.ToList());
        Assert.Contains("not a page of its table", damaged.Message);
    }

    [Fact]
    public void An_update_that_makes_a_row_too_large_is_refused_with_nothing_changed()
    {
        // The size was checked only as the new row was written, after the old one was marked deleted and its long
        // values freed: the update threw and the row was gone (qcatco/exportmdb.interop#56).
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var columns = new List<ColumnDefinition> { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) };
            for (int i = 0; i < 20; i++)
                columns.Add(new ColumnDefinition("T" + i, DataType.Text) { MaxLength = 255 });
            var table = db.CreateTable("T", columns, primaryKey: "Id");
            var row = new Row { ["Id"] = 1, ["Notes"] = new string('n', 9_000) };
            for (int i = 0; i < 7; i++) row["T" + i] = new string((char)(0x3A9 + i), 255);   // 510 bytes each: 3,570
            table.Insert(row);
        }
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            var tooLarge = Assert.Throws<InvalidOperationException>(() => table.UpdateByPrimaryKey(1, new Row { ["T7"] = new string('\u03B1', 255), ["T8"] = new string('\u03B2', 255) }));
            Assert.Contains("too large", tooLarge.Message);
            Assert.Equal(new string('n', 9_000), table.FindRowByPrimaryKey(1)!["Notes"]);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void An_update_moves_a_long_value_kept_in_the_row_past_what_Access_keeps_there_to_pages_of_its_own()
    {
        // Releases before ExportMdb.Interop 2.1.2 kept up to 1,024 bytes of a long value in the row, where Access
        // keeps 64 and shows #Error for more (qcatco/exportmdb.interop#29). An update of another column wrote the
        // value again, which put it right; keeping an unchanged value's reference must not keep that.
        using var db = Database.Create(_path, FileFormat.Access2000);
        var (table, blob) = BlobTable(db);
        table.Insert(new Row { ["Id"] = 1, ["Name"] = "short", ["Blob"] = Bytes(30, 1) });
        table.Insert(new Row { ["Id"] = 2, ["Name"] = "overlong", ["Blob"] = Bytes(30, 2) });

        // Row 2 as such a release wrote it: 500 bytes in the row. (No long-value writers: everything stays in the row.)
        byte[] overlong = new RowEncoder(db.File.Format, table.Definition).Encode(new Row { ["Id"] = 2, ["Name"] = "overlong", ["Blob"] = Bytes(500, 2) });
        int page = UsageMap.GetLastPage(db.File, table.Definition.UmapPageNumber, table.Definition.OwnedPagesRow);
        byte[] bytes = db.File.ReadPage(page);
        var format = db.File.Format;
        int rows = ByteUtil.GetShort(bytes, format.OffsetDataNumRows);
        int free = ByteUtil.GetShort(bytes, JetFormat.OffsetDataFreeSpace);
        int at = format.OffsetDataRowTable + rows * JetFormat.SizeRowEntry + free - overlong.Length;
        Array.Copy(overlong, 0, bytes, at, overlong.Length);
        ByteUtil.PutShort(bytes, format.OffsetDataRowTable + rows * JetFormat.SizeRowEntry, (short)at);
        ByteUtil.PutShort(bytes, format.OffsetDataNumRows, (short)(rows + 1));
        ByteUtil.PutShort(bytes, JetFormat.OffsetDataFreeSpace, (short)(free - overlong.Length - JetFormat.SizeRowEntry));
        ByteUtil.PutUShort(bytes, format.OffsetDataRowTable + 1 * JetFormat.SizeRowEntry,
            (ushort)(ByteUtil.GetUShort(bytes, format.OffsetDataRowTable + 1 * JetFormat.SizeRowEntry) | JetFormat.DeletedRowFlags));
        db.File.WritePage(page, bytes);
        int pointer = RowPointer.Pack(page, rows);
        Assert.Equal(LvalWriter.LvalTypeThisPage | 500, ByteUtil.GetInt(Reference(db, table, blob, 2), 0));

        // No index entry is moved here: the crafted row has none of its own.
        int moved = new DataPageWriter(db.File, new PageAllocator(db.File)).RewriteRowAt(table.Definition, pointer, new Row { ["Name"] = "renamed" });

        byte[] reference = Reference(db, table, blob, 2);
        Assert.Equal(LvalWriter.LvalTypeOtherPage | 500, ByteUtil.GetInt(reference, 0));
        var row = table.ReadRowAt(moved, table.Columns)!;
        Assert.Equal("renamed", row["Name"]);
        Assert.Equal(Bytes(500, 2), row["Blob"]);

        // One kept in the row as Access keeps it stays in the row, untouched.
        byte[] kept = Reference(db, table, blob, 1);
        table.UpdateByPrimaryKey(1, new Row { ["Name"] = "still short" });
        Assert.Equal(kept, Reference(db, table, blob, 1));
    }

    [Theory]
    [InlineData("-0.001", 2)]
    [InlineData("-0.004", 2)]
    [InlineData("-0.4", 0)]
    public void A_Decimal_that_rounds_to_zero_has_no_sign(string value, int scale)
    {
        byte[] bytes = NumericCodec.Encode(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), 18, scale, "N");
        Assert.All(bytes, b => Assert.Equal(0, b));
        Assert.Equal(0x80, NumericCodec.Encode(-0.005m, 18, 2, "N")[0]);   // rounds to -0.01
    }

    [Fact]
    public void Long_values_placed_as_Access_places_them_cost_a_page_more_each_and_no_more_reads_as_the_table_grows()
    {
        // 5,000 characters, not compressed, are 10,000 bytes: three chunks. Each has a page to itself, where the
        // earlier placement put the last one (1,856 bytes) with another value's - two to a page: three pages a
        // value against two and a half. Each insert reads a handful of pages, the same for the 400th as the 40th.
        (int pages, long earlyReads, long lateReads) Fill(bool share)
        {
            string path = Path.Combine(Path.GetTempPath(), $"layout_{Guid.NewGuid():N}.mdb");
            try
            {
                using var db = Database.Create(path, FileFormat.Access2000);
                db.ShareLongValuePages = share;
                var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) });
                long early = 0, late = 0;
                for (int i = 1; i <= 400; i++)
                {
                    long before = db.File.ReadCount;
                    table.Insert(new Row { ["Id"] = i, ["Notes"] = new string((char)(0x3A9 + i % 20), 5_000) });
                    if (i is > 30 and <= 40) early += db.File.ReadCount - before;
                    if (i > 390) late += db.File.ReadCount - before;
                }
                var lval = table.Definition.LvalColumnUmapPages["Notes"];
                return (UsageMap.GetOwnedPages(db.File.ReadPage(lval.OwnedPage), lval.OwnedRow, db.File.Format, db.File).Count, early, late);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        var own = Fill(share: false);
        var shared = Fill(share: true);

        Assert.Equal(1_200, own.pages);      // three pages a value
        Assert.Equal(1_000, shared.pages);   // two and a half: a fifth fewer
        Assert.True(own.lateReads <= own.earlyReads + 20, $"ten inserts read {own.earlyReads} pages early and {own.lateReads} late");
        Assert.True(own.lateReads <= shared.lateReads, $"ten inserts read {own.lateReads} pages, and {shared.lateReads} when values share pages");
    }
}