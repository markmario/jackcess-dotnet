using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A database DAO or Access made in another locale has that locale's LCID for the sort order of its text, in
/// place of 1033 (#23). ACE keys the text of nine such locales exactly as it keys General - Legacy - measured
/// with tools/ace-text-keys.ps1 -Locale over 1,013,302 values each, and held to in ExportMdb.Interop's
/// SortOrdersInAccessTests, where ACE seeks what this library wrote in a database of each - and the text of the
/// rest otherwise, or unmeasured. So the nine are written with General - Legacy's keys and every other is
/// refused, as it was.
/// <para>
/// No file here is of such a locale: the order is written into the definition of a table this library made,
/// which is all a locale's database differs by where its keys are the same.
/// </para>
/// </summary>
public sealed class LocaleSortOrderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"locale_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Theory]
    [InlineData(0x0401)]   // Arabic
    [InlineData(0x0407)]   // German
    [InlineData(0x0408)]   // Greek
    [InlineData(0x040D)]   // Hebrew
    [InlineData(0x0410)]   // Italian
    [InlineData(0x0413)]   // Dutch
    [InlineData(0x0419)]   // Russian
    [InlineData(0x0809)]   // English (United Kingdom)
    [InlineData(0x0816)]   // Portuguese
    public void The_order_of_a_locale_ACE_keys_as_General_Legacy_has_its_codes(int lcid)
    {
        var order = new SortOrder((short)lcid, 0);
        Assert.True(order.KeysAsGeneralLegacy);
        Assert.Same(TextIndexCodes.GeneralLegacy, TextIndexCodes.For(order));

        // In the version Access 2010 gives a new database's order, the locale's keys were not measured.
        Assert.Null(TextIndexCodes.For(new SortOrder((short)lcid, 1)));
        // And an Access 97 file's order of that number is not Access 97's General order.
        Assert.Null(TextIndexCodes.For(new SortOrder((short)lcid, 0xFF)));
    }

    [Theory]
    [InlineData(0x0404)]   // Chinese (Traditional)
    [InlineData(0x0405)]   // Czech
    [InlineData(0x0406)]   // Danish
    [InlineData(0x040A)]   // Spanish, traditional
    [InlineData(0x040B)]   // Finnish
    [InlineData(0x040C)]   // French
    [InlineData(0x040E)]   // Hungarian
    [InlineData(0x040F)]   // Icelandic
    [InlineData(0x0411)]   // Japanese
    [InlineData(0x0412)]   // Korean
    [InlineData(0x0414)]   // Norwegian
    [InlineData(0x0415)]   // Polish
    [InlineData(0x0418)]   // Romanian
    [InlineData(0x041A)]   // Croatian
    [InlineData(0x041B)]   // Slovak
    [InlineData(0x041D)]   // Swedish
    [InlineData(0x041E)]   // Thai
    [InlineData(0x041F)]   // Turkish
    [InlineData(0x0422)]   // Ukrainian
    [InlineData(0x0424)]   // Slovenian
    [InlineData(0x0427)]   // Lithuanian
    [InlineData(0x042A)]   // Vietnamese
    [InlineData(0x0804)]   // Chinese (Simplified)
    [InlineData(0x0C0A)]   // Spanish, modern
    [InlineData(0x0C09)]   // English (Australia): not measured
    public void The_order_of_a_locale_ACE_keys_otherwise_or_that_was_not_measured_has_no_codes(int lcid)
    {
        var order = new SortOrder((short)lcid, 0);
        Assert.False(order.KeysAsGeneralLegacy);
        Assert.Null(TextIndexCodes.For(order));
    }

    private static int SortOrderAt(byte[] tdef, int column) => 63 + ByteUtil.GetInt(tdef, 51) * 12 + column * 25 + 11;

    private static void SetSortOrder(PageFile file, int tdefPage, int column, short lcid)
    {
        byte[] tdef = file.ReadPage(tdefPage);
        ByteUtil.PutShort(tdef, SortOrderAt(tdef, column), lcid);
        tdef[SortOrderAt(tdef, column) + 2] = 0;
        tdef[SortOrderAt(tdef, column) + 3] = 0;
        file.WritePage(tdefPage, tdef);
    }

    /// <summary>A table with a text primary key and a text index, both columns in the order of <paramref name="lcid"/>, with three rows.</summary>
    private void TableIn(short lcid)
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("Coded", new[]
        {
            new ColumnDefinition("Code", DataType.Text) { MaxLength = 20 },
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 40 },
        }, new[] { "Code" }, new[] { new IndexDefinition("ByName", "Name") });
        foreach (string code in new[] { "б", "a", "Ω" })
            table.Insert(new Row { ["Code"] = code, ["Name"] = "name of " + code });

        int tdef = IndexChecks.TdefOf(db, "Coded");
        SetSortOrder(db.File, tdef, column: 0, lcid);
        SetSortOrder(db.File, tdef, column: 1, lcid);
    }

    [Fact]
    public void A_table_whose_text_sorts_in_such_a_locale_s_order_is_written_to_and_its_indexes_kept()
    {
        TableIn(0x0419);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Coded");
            Assert.Equal(new SortOrder(0x0419, 0), table.Columns[0].SortOrder);
            Assert.Null(table.InsertRefusal);

            foreach (string code in new[] { "Привет", "résumé", "Æther", "שלום", "日本" })
                table.Insert(new Row { ["Code"] = code, ["Name"] = "name of " + code });
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Code"] = "ПРИВЕТ", ["Name"] = "again" }));
            table.UpdateByPrimaryKey("a", new Row { ["Code"] = "я" });
            table.DeleteRow("Code", "Ω");

            Assert.Equal("name of Привет", table.CreateIndexCursor().FindRow("Code", "Привет")!["Name"]);
            Assert.Equal("я", table.CreateIndexCursor("ByName").FindRow("Name", "name of a")!["Code"]);
        }

        using var read = Database.Open(_path);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(read, "Coded", expectedRows: 7));
    }

    [Fact]
    public void A_table_whose_text_sorts_in_another_locale_s_order_is_still_refused_and_read()
    {
        TableIn(0x041F);   // Turkish: ACE keys I, Ç, Ö, Ü and others apart

        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Coded");
            Assert.Contains("order 1055", table.InsertRefusal);
            var refused = Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["Code"] = "x", ["Name"] = "n" }));
            Assert.Contains("the orders whose keys are the same", refused.Message);
            Assert.Throws<NotSupportedException>(() => table.DeleteRow("Code", "a"));

            Assert.Equal(3, table.Rows.Count());
            Assert.Equal("name of a", table.CreateIndexCursor().FindRow("Code", "a")!["Name"]);
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused change altered the file");
    }
}
