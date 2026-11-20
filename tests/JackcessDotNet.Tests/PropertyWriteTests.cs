using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Properties are written (#27): a table's, a column's and the database's, to the LvProp value of the object's
/// row in MSysObjects, where Access keeps them. What Access wrote goes back byte for byte. That Access reads what
/// is written here is PropertiesInAccessTests' to say, where DAO is the referee; these say what the library
/// itself holds to.
/// Assets/dao-properties.mdb is a database DAO made (tools/dao-properties.ps1).
/// </summary>
public sealed class PropertyWriteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"prop_write_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private void CopyDaoMade() => File.Copy(Asset("dao-properties.mdb"), _path, overwrite: true);

    /// <summary>The LvProp value of every row of the catalog that has one, by the object's name and id.</summary>
    private static Dictionary<string, byte[]> PropertyValues(Database db)
    {
        var values = new Dictionary<string, byte[]>();
        foreach (var row in db.GetTable("MSysObjects").Rows)
            if (row["LvProp"] is byte[] value)
                values[$"{row["Name"]}#{row["Id"]}"] = value;
        return values;
    }

    // ── What Access wrote goes back as it was ────────────────────────────────

    public static IEnumerable<object[]> CorpusFiles()
        => TestCorpus.Files("*.*", "V2000", "V2003", "V2007", "V2010", "V2016", "V2019")
            .Where(f => f.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
            .Select(f => new object[] { Path.Combine(Path.GetFileName(Path.GetDirectoryName(f))!, Path.GetFileName(f)) });

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Every_property_value_Access_wrote_is_written_back_byte_for_byte(string file)
    {
        Database db;
        try { db = Database.Open(Path.Combine(TestCorpus.Root!, file)); }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or UnauthorizedAccessException) { return; }   // a file the library does not open

        using (db)
        {
            int read = 0;
            foreach (var (name, value) in PropertyValues(db))
            {
                var maps = PropertyMapReader.Read(value, db.File.Format);
                Assert.True(maps.UnreadReason is null, $"{name}: {maps.UnreadReason}");
                Assert.True(value.AsSpan().SequenceEqual(PropertyMapWriter.Write(maps)), $"{name} was not written back as it was read");
                read++;
            }
            Assert.True(read > 0, "the file has no property value to read");
        }
    }

    [Fact]
    public void A_property_added_leaves_every_property_Access_wrote_as_it_was()
    {
        CopyDaoMade();
        byte[] before;
        using (var db = Database.Open(_path))
        {
            before = PropertyValues(db).Single(v => v.Key.StartsWith("DaoMade#")).Value;
            var table = db.GetTable("DaoMade");
            table.Columns.Single(c => c.Name == "ReqLong").Properties.Set("Description", "Added by the library");
            table.Columns.Single(c => c.Name == "Id").Properties.Set("Caption", "A column DAO kept no property for");
            table.Properties.Set("NewOnTheTable", 5);
            table.Properties.Save();
        }

        using var read = Database.Open(_path);
        byte[] after = PropertyValues(read).Single(v => v.Key.StartsWith("DaoMade#")).Value;
        var was = PropertyMapReader.Read(before, read.File.Format);
        var now = PropertyMapReader.Read(after, read.File.Format);
        Assert.Null(now.UnreadReason);

        // The names Access listed keep their places, and the new ones follow them.
        Assert.Equal(was.Names, now.Names.Take(was.Names.Count));
        Assert.Equal(new[] { "NewOnTheTable" }, now.Names.Skip(was.Names.Count));

        // Every property Access wrote is there, in its place in its block, with the bytes and flags it had.
        foreach (var map in was)
        {
            var kept = now.Single(m => m.Name == map.Name && m.Type == map.Type);
            Assert.Equal(map.Select(p => p.Name), kept.Take(map.Count).Select(p => p.Name));
            foreach (var property in map)
            {
                Assert.True(kept.TryGetProperty(property.Name, out var same));
                Assert.Equal(property.DataType, same.DataType);
                Assert.Equal(property.Flags, same.Flags);
                Assert.Equal(property.Stored, same.Stored);
            }
        }

        var reqLong = read.GetTable("DaoMade").Columns.Single(c => c.Name == "ReqLong").Properties;
        Assert.Equal(new[] { "Required", "Description" }, reqLong.Select(p => p.Name));
        Assert.Equal("Added by the library", reqLong["Description"]);
        Assert.Equal("A column DAO kept no property for", read.GetTable("DaoMade").Columns.Single(c => c.Name == "Id").Properties["Caption"]);
        Assert.Equal(5, read.GetTable("DaoMade").Properties["NewOnTheTable"]);
        Assert.Equal("A table DAO made", read.GetTable("DaoMade").Properties["Description"]);
    }

    [Fact]
    public void A_file_saved_with_nothing_changed_has_the_property_value_it_had()
    {
        CopyDaoMade();
        Dictionary<string, byte[]> before;
        using (var db = Database.Open(_path))
        {
            before = PropertyValues(db);
            db.GetTable("DaoMade").Properties.Save();
            db.DatabaseProperties.Save();
        }
        using var read = Database.Open(_path);
        var after = PropertyValues(read);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (name, value) in before)
            Assert.True(value.AsSpan().SequenceEqual(after[name]), $"{name} changed");
    }

    // ── Setting, changing and removing ───────────────────────────────────────

    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2007)]
    [InlineData(FileFormat.Access2010)]
    public void Properties_of_every_type_are_read_back_from_a_table_a_column_and_the_database(FileFormat format)
    {
        var date = new DateTime(2026, 11, 19, 10, 20, 30);
        var guid = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        string memo = new string('m', 3000) + "é日";   // longer than a row keeps: it goes to pages of its own

        using (var db = Database.Create(_path, format))
        {
            var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) }, "Id");
            table.Properties.Set("Description", "A table");
            var name = table.Columns.Single(c => c.Name == "Name").Properties;
            name.Set("Caption", "The name");
            name.Set("ABool", true);
            name.Set("AByte", (byte)200);
            name.Set("AShort", (short)-2);
            name.Set("AnInt", 123456);
            name.Set("ASingle", 1.5f);
            name.Set("ADouble", -2.25);
            name.Set("ACurrency", 12.3456m);
            name.Set("ADate", date);
            name.Set("AGuid", guid);
            name.Set("ABlob", new byte[] { 1, 2, 3 });
            name.Set("ABinary", DataType.Binary, new byte[] { 4, 5 });
            name.Set("AMemo", DataType.Memo, memo);
            name.Save();

            db.DatabaseProperties.Set("AppTitle", "A title");
            db.DatabaseProperties.Set("Count", 42);
            db.DatabaseProperties.Save();
        }

        using var read = Database.Open(_path);
        Assert.Equal("A table", read.GetTable("T").Properties["Description"]);
        var column = read.GetTable("T").Columns.Single(c => c.Name == "Name").Properties;
        var expected = new (string Name, DataType Type, object Value)[]
        {
            ("Caption", DataType.Memo, "The name"), ("ABool", DataType.Boolean, true), ("AByte", DataType.Byte, (byte)200),
            ("AShort", DataType.Int16, (short)-2), ("AnInt", DataType.Int32, 123456),
            ("ASingle", DataType.Single, 1.5f), ("ADouble", DataType.Double, -2.25), ("ACurrency", DataType.Currency, 12.3456m),
            ("ADate", DataType.DateTime, date), ("AGuid", DataType.Guid, guid), ("ABlob", DataType.OleObject, new byte[] { 1, 2, 3 }),
            ("ABinary", DataType.Binary, new byte[] { 4, 5 }), ("AMemo", DataType.Memo, memo),
        };
        foreach (var (name, type, value) in expected)
        {
            Assert.True(column.TryGetProperty(name, out var property), name);
            Assert.Equal(type, property.DataType);
            Assert.Equal(value, property.Value);
            Assert.False(property.IsDdl);
        }
        Assert.Equal("A title", read.DatabaseProperties["AppTitle"]);
        Assert.Equal(42, read.DatabaseProperties["Count"]);
    }

    [Fact]
    public void A_property_that_is_there_keeps_its_type_and_one_Access_has_a_type_for_is_given_it()
    {
        CopyDaoMade();
        using (var db = Database.Open(_path))
        {
            var optLong = db.GetTable("DaoMade").Columns.Single(c => c.Name == "OptLong").Properties;
            optLong.Set("DecimalPlaces", 4);                    // DAO made it a Byte, and it stays one
            optLong.Set("caption", "Renamed");                  // the name as the file spells it
            optLong.Set("Format", "0.00");                      // Access keeps Format as Text, a DDL property
            optLong.Set("DefaultValue", "7");                   // and DefaultValue as a Memo
            optLong.Set("Mine", DataType.Int16, 3, isDdl: true);
            Assert.True(optLong.Remove("description"));
            Assert.False(optLong.Remove("NotThere"));
            optLong.Save();
        }

        using var read = Database.Open(_path);
        var column = read.GetTable("DaoMade").Columns.Single(c => c.Name == "OptLong").Properties;
        Assert.Equal(new[] { "Required", "Caption", "DecimalPlaces", "Format", "DefaultValue", "Mine" }, column.Select(p => p.Name));
        Assert.True(column.TryGetProperty("DecimalPlaces", out var places));
        Assert.Equal((DataType.Byte, (object)(byte)4, false), (places.DataType, places.Value, places.IsDdl));
        Assert.Equal("Renamed", column["Caption"]);
        Assert.True(column.TryGetProperty("Format", out var format));
        Assert.Equal((DataType.Text, (object)"0.00", true), (format.DataType, format.Value, format.IsDdl));
        Assert.True(column.TryGetProperty("DefaultValue", out var defaultValue));
        Assert.Equal((DataType.Memo, true), (defaultValue.DataType, defaultValue.IsDdl));
        Assert.True(column.TryGetProperty("Mine", out var mine));
        Assert.Equal((DataType.Int16, (object)(short)3, true), (mine.DataType, mine.Value, mine.IsDdl));
        Assert.False(column.TryGetValue("Description", out _));
    }

    [Fact]
    public void Properties_that_are_all_removed_leave_the_object_with_none()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true } });
            Assert.DoesNotContain(PropertyValues(db), v => v.Key.StartsWith("T#"));   // an AutoNumber has no property
            table.Properties.Set("Description", "Here for a moment");
            table.Properties.Save();
            Assert.Single(PropertyValues(db), v => v.Key.StartsWith("T#"));
            table.Properties.Remove("Description");
            table.Properties.Save();

            // What is left lists the name and has no property, as Access leaves the value of an object whose
            // properties have gone (the SummaryInfo of several corpus files is so).
            var left = PropertyMapReader.Read(PropertyValues(db).Single(v => v.Key.StartsWith("T#")).Value, db.File.Format);
            Assert.Null(left.UnreadReason);
            Assert.True(left.IsEmpty);
            Assert.Equal(new[] { "Description" }, left.Names);
        }
        using var read = Database.Open(_path);
        Assert.Empty(read.GetTable("T").Properties);
        Assert.Empty(read.GetTable("T").Columns.Single().Properties);
    }

    [Fact]
    public void A_table_of_many_columns_keeps_properties_longer_than_a_page()
    {
        // A table made with these columns has properties too long for its row of the catalog, on one page of
        // their own; a description for each column then takes them over several pages.
        var columns = Enumerable.Range(0, 100).Select(i => new ColumnDefinition($"C{i:D3}", DataType.Text) { MaxLength = 5, IsRequired = i % 2 == 0 }).ToList();
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Wide", columns);
            Assert.InRange(PropertyValues(db).Single(v => v.Key.StartsWith("Wide#")).Value.Length, 2000, 4000);
            foreach (var column in table.Columns)
                column.Properties.Set("Description", $"The description of {column.Name}, long enough to matter: {new string('d', 60)}");
            table.Columns[7].Properties.Set("Description", "Changed once it was made");
            table.Properties.Save();
            Assert.True(PropertyValues(db).Single(v => v.Key.StartsWith("Wide#")).Value.Length > 2 * 4096);
        }
        using var read = Database.Open(_path);
        var wide = read.GetTable("Wide");
        Assert.Equal(columns.Select(c => c.IsRequired), wide.Columns.Select(c => c.IsRequired));
        Assert.All(wide.Columns, c => Assert.True(c.AllowZeroLength));
        Assert.Equal("Changed once it was made", wide.Columns[7].Properties["Description"]);
        Assert.Equal(0, wide.RowCount);
    }

    [Fact]
    public void The_tables_of_the_database_are_as_they_were_after_properties_are_saved()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) }, "Id");
            for (int i = 1; i <= 50; i++) table.Insert(new Row { ["Id"] = i, ["Name"] = $"row {i}" });
            db.CreateTable("Other", new[] { new ColumnDefinition("Id", DataType.Int32) });
            for (int round = 0; round < 20; round++)
            {
                table.Properties.Set("Description", new string((char)('a' + round), 40 * (round + 1)));
                table.Properties.Save();
            }
            db.CreateTable("Later", new[] { new ColumnDefinition("Id", DataType.Int32) });
        }
        using var read = Database.Open(_path);
        read.VerifyIndexes = true;
        Assert.Equal(new[] { "T", "Other", "Later" }.OrderBy(n => n), read.GetTableNames().OrderBy(n => n));
        Assert.Equal(new string('t', 800), read.GetTable("T").Properties["Description"]);
        Assert.Equal(50, read.GetTable("T").Rows.Count());
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "MSysObjects", expectedRows: read.GetTable("MSysObjects").Rows.Count());
    }

    // ── The database's own, and its two property sheets ──────────────────────

    [Fact]
    public void The_summary_and_user_defined_properties_Access_wrote_are_read_and_written()
    {
        string source = TestCorpus.File("V2003", "common1V2003.mdb")!;
        File.Copy(source, _path, overwrite: true);
        File.SetAttributes(_path, FileAttributes.Normal);
        using (var db = Database.Open(_path))
        {
            Assert.Equal("test", db.SummaryProperties["Title"]);
            Assert.Equal("tmccune", db.SummaryProperties["Author"]);
            Assert.Equal(true, db.UserDefinedProperties["ReplicateProject"]);
            Assert.Equal("09.50", db.DatabaseProperties["AccessVersion"]);

            db.SummaryProperties.Set("Title", "Retitled");
            db.SummaryProperties.Set("Subject", "Added");
            db.SummaryProperties.Save();
            db.UserDefinedProperties.Set("Project", "A project");
            db.UserDefinedProperties.Set("Count", 9);
            db.UserDefinedProperties.Save();
            db.DatabaseProperties.Set("Mine", true);
            db.DatabaseProperties.Save();
        }
        using var read = Database.Open(_path);
        Assert.Equal(new[] { "Title", "Author", "Company", "Subject" }, read.SummaryProperties.Select(p => p.Name));
        Assert.Equal("Retitled", read.SummaryProperties["Title"]);
        Assert.Equal("tmccune", read.SummaryProperties["Author"]);
        Assert.Equal("Added", read.SummaryProperties["Subject"]);
        Assert.Equal("A project", read.UserDefinedProperties["Project"]);
        Assert.Equal(9, read.UserDefinedProperties["Count"]);
        Assert.Equal(true, read.UserDefinedProperties["ReplicateProject"]);
        Assert.Equal(true, read.DatabaseProperties["Mine"]);
        Assert.Equal("09.50", read.DatabaseProperties["AccessVersion"]);
    }

    [Fact]
    public void A_database_without_the_document_refuses_summary_properties_and_is_not_changed()
    {
        CopyDaoMade();   // DAO makes no SummaryInfo or UserDefined document
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            Assert.Empty(db.SummaryProperties);
            Assert.Empty(db.UserDefinedProperties);
            db.SummaryProperties.Set("Title", "Nowhere to go");
            var refused = Assert.Throws<NotSupportedException>(() => db.SummaryProperties.Save());
            Assert.Contains("SummaryInfo", refused.Message);
            db.UserDefinedProperties.Set("Project", "Nor this");
            Assert.Throws<NotSupportedException>(() => db.UserDefinedProperties.Save());
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused save changed the file");
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_value_no_property_holds_is_refused_when_it_is_set()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var map = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) }).Properties;

        Assert.Throws<ArgumentNullException>(() => map.Set(null!, 1));
        Assert.Throws<ArgumentNullException>(() => map.Set("A", null!));
        Assert.Throws<ArgumentNullException>(() => map.Remove(null!));
        Assert.Throws<ArgumentException>(() => map.Set("", 1));
        Assert.Throws<ArgumentException>(() => map.Set("A", new object()));                       // no type for it
        Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Decimal, 1m));               // no property is a Decimal
        Assert.Throws<ArgumentException>(() => map.Set("A", 1L));                                 // nor a Large Number: DAO makes none
        Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Int64, 1));
        Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Int32, "not a number"));
        Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Byte, 300));
        Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Binary, "not bytes"));
        Assert.Throws<ArgumentException>(() => map.Set("A", new string('x', 16_380)));            // one character too many
        map.Set("A", new string('x', 16_379));
        Assert.Single(map);
    }

    [Fact]
    public void A_value_beyond_its_type_s_range_is_refused_when_it_is_set_and_one_at_the_edge_is_kept()
    {
        const decimal maxCurrency = 922_337_203_685_477.5807m, minCurrency = -922_337_203_685_477.5808m;
        var firstDate = new DateTime(100, 1, 1);
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var map = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) }).Properties;

            // A Currency is a 64-bit count of ten-thousandths.
            Assert.Throws<ArgumentException>(() => map.Set("A", maxCurrency + 0.0001m));
            Assert.Throws<ArgumentException>(() => map.Set("A", minCurrency - 0.0001m));
            Assert.Throws<ArgumentException>(() => map.Set("A", decimal.MaxValue));
            Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Currency, 1e30));
            // Access's dates begin with the year 100.
            Assert.Throws<ArgumentException>(() => map.Set("A", firstDate.AddTicks(-1)));
            Assert.Throws<ArgumentException>(() => map.Set("A", DateTime.MinValue));
            // The whole numbers, and a Single.
            Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Byte, 256));
            Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Byte, -1));
            Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Int16, 32768));
            Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Int32, 2147483648.0));
            Assert.Throws<ArgumentException>(() => map.Set("A", DataType.Single, 1e300));
            Assert.Empty(map);

            map.Set("MaxCurrency", maxCurrency);
            map.Set("MinCurrency", minCurrency);
            map.Set("FirstDate", firstDate);
            map.Set("LastDate", DateTime.MaxValue);
            map.Set("MaxByte", DataType.Byte, 255);
            map.Set("MinShort", DataType.Int16, -32768);
            map.Set("MinInt", DataType.Int32, int.MinValue);
            map.Set("MaxSingle", float.MaxValue);
            map.Set("Infinity", float.PositiveInfinity);
            map.Save();
        }
        using var read = Database.Open(_path);
        var kept = read.GetTable("T").Properties;
        Assert.Equal(maxCurrency, kept["MaxCurrency"]);
        Assert.Equal(minCurrency, kept["MinCurrency"]);
        Assert.Equal(firstDate, kept["FirstDate"]);
        // A date is kept as a Double of days, which holds the last day's last moment to the millisecond.
        Assert.Equal(DateTime.MaxValue, (DateTime)kept["LastDate"]!, TimeSpan.FromMilliseconds(1));
        Assert.Equal((byte)255, kept["MaxByte"]);
        Assert.Equal((short)-32768, kept["MinShort"]);
        Assert.Equal(int.MinValue, kept["MinInt"]);
        Assert.Equal(float.MaxValue, kept["MaxSingle"]);
        Assert.Equal(float.PositiveInfinity, kept["Infinity"]);
    }

    [Fact]
    public void The_properties_of_a_column_in_no_table_cannot_be_saved()
    {
        var column = new ColumnDefinition("Loose", DataType.Text).ToColumn("column");
        column.Properties.Set("Description", "Nowhere to go");
        Assert.Throws<InvalidOperationException>(() => column.Properties.Save());
    }

    [Fact]
    public void An_Access_97_file_refuses_a_save_and_is_not_changed()
    {
        File.Copy(Asset("access97-jet40.mdb"), _path, overwrite: true);
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            db.DatabaseProperties.Set("Mine", 1);
            Assert.Throws<NotSupportedException>(() => db.DatabaseProperties.Save());
            var table = db.GetTable(db.GetTableNames()[0]);
            table.Properties.Set("Description", "No");
            Assert.Throws<NotSupportedException>(() => table.Properties.Save());
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused save changed the file");
    }

    [Fact]
    public void Properties_that_were_not_read_whole_are_not_written_over()
    {
        // A value with bytes after its last block: what was read is not all there is.
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", new[] { new ColumnDefinition("Name", DataType.Text) });
            byte[] whole = PropertyValues(db).Single(v => v.Key.StartsWith("T#")).Value;
            var damaged = PropertyMapReader.Read(whole.Concat(new byte[] { 1, 2, 3 }).ToArray(), db.File.Format);
            Assert.NotNull(damaged.UnreadReason);
            Assert.Equal(false, damaged.Get("Name")!["Required"]);   // what could be read is still read
            Assert.Throws<NotSupportedException>(() => PropertyMapWriter.Write(damaged));

            var unknown = PropertyMapReader.Read(new byte[] { (byte)'X', (byte)'Y', (byte)'Z', 0, 1, 2 }, db.File.Format);
            Assert.True(unknown.IsEmpty);
            Assert.Throws<NotSupportedException>(() => PropertyMapWriter.Write(unknown));
        }
    }

    [Fact]
    public void A_block_of_an_index_s_properties_is_kept_apart_from_its_column_s()
    {
        // Access names an index after its column; an index's properties are a block of type 2.
        var maps = new PropertyMaps();
        maps.GetOrAdd("Code", PropertyMap.ColumnType).Set("Required", true);
        maps.GetOrAdd("Code", 0x02).Set("AnIndexProperty", 7);
        byte[] value = PropertyMapWriter.Write(maps)!;

        var read = PropertyMapReader.Read(value, JetFormat.Jet4);
        Assert.Null(read.UnreadReason);
        Assert.Equal(2, read.Count);
        Assert.Equal(new[] { "Required" }, read.Get("Code")!.Select(p => p.Name));
        Assert.Equal(7, read.Single(m => m.Type == 0x02)["AnIndexProperty"]);
        Assert.True(value.AsSpan().SequenceEqual(PropertyMapWriter.Write(read)));
    }

    // ── A save that fails part-way ───────────────────────────────────────────

    [Fact]
    public void A_save_that_fails_at_any_page_leaves_the_file_as_it_was()
    {
        CopyDaoMade();
        byte[] before = File.ReadAllBytes(_path);
        int failures = 0;
        for (int writes = 0; ; writes++)
        {
            Assert.True(writes < 500, "the save never finished");
            using (var db = Database.Open(_path))
            {
                var table = db.GetTable("DaoMade");
                // Long enough to go to pages of its own, so the save writes several pages.
                table.Properties.Set("Description", new string('d', 9000));
                db.File.WritesBeforeFailure = writes;
                try
                {
                    table.Properties.Save();
                    db.File.WritesBeforeFailure = null;
                    break;
                }
                catch (IOException)
                {
                    db.File.WritesBeforeFailure = null;
                    failures++;
                }
                Assert.Equal(before.Length, db.File.Length);
            }
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), $"the file changed when write {writes + 1} of the save failed");
        }
        Assert.True(failures >= 3, $"the save was made to fail only {failures} times");

        using var read = Database.Open(_path);
        Assert.Equal(new string('d', 9000), read.GetTable("DaoMade").Properties["Description"]);
        Assert.Equal("An optional long", read.GetTable("DaoMade").Columns.Single(c => c.Name == "OptLong").Properties["Description"]);
    }
}
