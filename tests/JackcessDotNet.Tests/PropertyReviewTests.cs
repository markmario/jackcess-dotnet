using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What a review of the property writer found (#27, #41), each pinned: a column is added where properties cannot
/// be written, unless only a property can say what it was asked to be; a save writes what was changed and
/// nothing else, and nothing when nothing was; the names and values Access gives no property are refused; and
/// what the library does by a column's properties follows the file, not a change still to be saved.
/// Assets/dao-swedish.mdb is a database DAO made in the Swedish sort order (tools/dao-properties.ps1).
/// </summary>
public sealed class PropertyReviewTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"prop_review_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void Copy(string asset) => File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), _path, overwrite: true);

    private static Dictionary<string, byte[]> PropertyValues(Database db)
    {
        var values = new Dictionary<string, byte[]>();
        foreach (var row in db.GetTable("MSysObjects").Rows)
            if (row["LvProp"] is byte[] value)
                values[$"{row["Name"]}#{row["Id"]}"] = value;
        return values;
    }

    private static Column Column(Table table, string name) => table.Columns.Single(c => c.Name == name);

    private void AssertFileIs(byte[] before, string after)
        => Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), $"the file changed {after}");

    // ── A column added where properties cannot be written ────────────────────

    [Fact]
    public void A_column_that_needs_no_property_is_added_where_the_catalog_s_keys_cannot_be_written()
    {
        // The catalog's names sort in Swedish: no row of MSysObjects is rewritten, so no property is written.
        Copy("dao-swedish.mdb");
        byte[] properties;
        using (var db = Database.Open(_path))
        {
            Assert.NotNull(db.GetTable("MSysObjects").InsertRefusal);
            properties = PropertyValues(db).Single(v => v.Key.StartsWith("People#")).Value;
            var people = db.GetTable("People");

            var added = people.AddColumn(new ColumnDefinition("ZzNew", DataType.Int32));
            Assert.False(added.IsRequired);
            var text = people.AddColumn(new ColumnDefinition("ZzText", DataType.Text) { MaxLength = 10 });
            Assert.True(text.AllowZeroLength);
            people.AddColumn(new ColumnDefinition("ZzNumber", DataType.Int32) { IsAutoNumber = false });
            people.Insert(new Row { ["Id"] = 2, ["Name"] = "Bo", ["ZzNew"] = 7, ["ZzText"] = "" });
        }
        using (var read = Database.Open(_path))
        {
            var people = read.GetTable("People");
            Assert.Equal(new[] { "Id", "Name", "ZzNew", "ZzText", "ZzNumber" }, people.Columns.Select(c => c.Name));
            Assert.Null(people.Rows.First()["ZzNew"]);   // the row that was there has no value in a new column
            var row = people.Rows.Last();
            Assert.Equal((7, ""), ((int)row["ZzNew"]!, (string)row["ZzText"]!));
            // The table's properties are the bytes DAO wrote: nothing was written for the new columns.
            Assert.True(properties.AsSpan().SequenceEqual(PropertyValues(read).Single(v => v.Key.StartsWith("People#")).Value));
            Assert.Empty(Column(people, "ZzNew").Properties);
        }
    }

    [Fact]
    public void A_column_only_a_property_can_describe_is_refused_there_before_anything_is_written()
    {
        Copy("dao-swedish.mdb");
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var people = db.GetTable("People");
            var required = Assert.Throws<NotSupportedException>(() => people.AddColumn(new ColumnDefinition("R", DataType.Int32) { IsRequired = true }));
            Assert.Contains("IsRequired", required.Message);
            var noZero = Assert.Throws<NotSupportedException>(() => people.AddColumn(new ColumnDefinition("Z", DataType.Text) { AllowZeroLength = false }));
            Assert.Contains("AllowZeroLength", noZero.Message);
            Assert.Equal(new[] { "Id", "Name" }, people.Columns.Select(c => c.Name));

            // A property saved there is refused too, as it was.
            people.Properties.Set("Description", "No");
            Assert.Throws<NotSupportedException>(() => people.Properties.Save());
        }
        AssertFileIs(before, "when a column was refused");
    }

    /// <summary>A copy of the DAO-made database with DaoGuid's properties made a value the library does not read.</summary>
    private void CopyWithUnreadProperties()
    {
        Copy("dao-properties.mdb");
        byte[] value;
        using (var db = Database.Open(_path))
            value = PropertyValues(db).Single(v => v.Key.StartsWith("DaoGuid#")).Value;
        byte[] file = File.ReadAllBytes(_path);
        int at = file.AsSpan().IndexOf(value);
        Assert.True(at > 0 && file.AsSpan(at + 1).IndexOf(value) < 0, "the value is not in the file once");
        file[at] = (byte)'X';   // not the signature a property value begins with
        File.WriteAllBytes(_path, file);
    }

    [Fact]
    public void A_table_whose_properties_were_not_read_whole_takes_a_column_that_needs_none_and_refuses_one_that_does()
    {
        CopyWithUnreadProperties();
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("DaoGuid");
            Assert.NotNull(table.PropertyMaps.UnreadReason);
            var refused = Assert.Throws<NotSupportedException>(() => table.AddColumn(new ColumnDefinition("R", DataType.Int32) { IsRequired = true }));
            Assert.Contains("were not all read", refused.Message);
            Assert.Equal(new[] { "Key", "Plain", "Txt" }, table.Columns.Select(c => c.Name));
        }
        AssertFileIs(before, "when a column was refused");

        byte[] unread;
        using (var db = Database.Open(_path))
        {
            unread = PropertyValues(db).Single(v => v.Key.StartsWith("DaoGuid#")).Value;
            db.GetTable("DaoGuid").AddColumn(new ColumnDefinition("Plain2", DataType.Int32));
        }
        using var read = Database.Open(_path);
        Assert.Equal(new[] { "Key", "Plain", "Txt", "Plain2" }, read.GetTable("DaoGuid").Columns.Select(c => c.Name));
        Assert.True(unread.AsSpan().SequenceEqual(PropertyValues(read).Single(v => v.Key.StartsWith("DaoGuid#")).Value));
    }

    // ── Nothing is written until Save, and only what was changed ─────────────

    [Fact]
    public void A_column_added_does_not_write_what_was_changed_in_the_table_s_properties_and_not_saved()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) }, "Id");
            Column(table, "Name").Properties.Set("Required", true);
            table.Properties.Set("Description", "Not saved");

            var added = table.AddColumn(new ColumnDefinition("Later", DataType.Int32) { IsRequired = true });
            Assert.True(added.IsRequired);
            Assert.Equal(true, added.Properties["Required"]);
            // The change is still there to be saved, and still not what the file says.
            Assert.Equal(true, Column(table, "Name").Properties["Required"]);
            Assert.False(Column(table, "Name").IsRequired);
        }
        using (var read = Database.Open(_path))
        {
            var table = read.GetTable("T");
            Assert.False(Column(table, "Name").IsRequired);
            Assert.True(Column(table, "Later").IsRequired);
            Assert.Empty(table.Properties);

            // Saved after the column was added, the change and the column's own properties are both there.
            Column(table, "Name").Properties.Set("Required", true);
            table.AddColumn(new ColumnDefinition("Last", DataType.Text) { AllowZeroLength = false });
            table.Properties.Save();
        }
        using var again = Database.Open(_path);
        Assert.Equal(new[] { "Name", "Later" }, again.GetTable("T").Columns.Where(c => c.IsRequired).Select(c => c.Name));
        Assert.False(Column(again.GetTable("T"), "Last").AllowZeroLength);
    }

    [Fact]
    public void A_row_is_held_to_a_column_s_properties_as_the_file_has_them_not_as_they_are_about_to_be()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) }, "Id");
        var name = Column(table, "Name");

        name.Properties.Set("Required", true);
        name.Properties.Set("AllowZeroLength", false);
        Assert.False(name.IsRequired);
        Assert.True(name.AllowZeroLength);
        table.Insert(new Row { ["Id"] = 1 });               // Access, reading the file, takes these rows too
        table.Insert(new Row { ["Id"] = 2, ["Name"] = "" });

        name.Properties.Save();
        Assert.True(name.IsRequired);
        Assert.False(name.AllowZeroLength);
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 3 }));
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 3, ["Name"] = "" }));

        name.Properties.Remove("Required");                 // and a property taken away counts once it is saved
        Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 3 }));
        name.Properties.Save();
        table.Insert(new Row { ["Id"] = 3 });
    }

    [Fact]
    public void A_save_with_nothing_changed_writes_nothing()
    {
        Copy("dao-properties.mdb");
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("DaoMade");
            _ = db.Properties.Count;
            long writes = db.File.WriteCount;
            table.Properties.Save();
            Column(table, "OptLong").Properties.Save();
            db.Properties.Save();
            db.SummaryProperties.Save();                    // no document, and nothing to write to it
            Assert.Equal(writes, db.File.WriteCount);

            table.Properties.Set("Description", "Changed");
            table.Properties.Save();
            long afterOne = db.File.WriteCount;
            Assert.True(afterOne > writes);
            table.Properties.Save();                        // saved already
            Assert.Equal(afterOne, db.File.WriteCount);
        }
        Assert.False(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)));
    }

    [Theory]
    [InlineData("V2003", "common1V2003.mdb")]
    [InlineData("V2007", "common1V2007.accdb")]
    [InlineData("V2010", "common1V2010.accdb")]
    [InlineData("V2003", "IndexPropertiesV2003.mdb")]
    public void Properties_Access_wrote_are_the_same_bytes_in_the_file_after_a_save_rewrites_them(string version, string name)
    {
        File.Copy(TestCorpus.File(version, name)!, _path, overwrite: true);
        File.SetAttributes(_path, FileAttributes.Normal);
        Dictionary<string, byte[]> before;
        int saved = 0;
        using (var db = Database.Open(_path))
        {
            before = PropertyValues(db);
            foreach (string tableName in db.GetTableNames())
            {
                var table = db.GetTable(tableName);
                if (table.PropertyMaps.IsEmpty) continue;
                table.PropertyMaps.Changed();               // as if a property had been set to the value it has
                table.Properties.Save();
                saved++;
            }
            long writes = db.File.WriteCount;
            Assert.True(saved > 0 && writes > 0, "no table's properties were rewritten");
        }
        using var read = Database.Open(_path);
        read.VerifyIndexes = true;
        var after = PropertyValues(read);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (key, value) in before)
            Assert.True(value.AsSpan().SequenceEqual(after[key]), $"{key} is not the bytes it was");
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "MSysObjects");
    }

    // ── A column whose adding fails part-way ─────────────────────────────────

    [Fact]
    public void A_column_whose_adding_fails_at_any_page_leaves_the_file_and_the_table_s_properties_as_they_were()
    {
        Copy("dao-properties.mdb");
        byte[] before = File.ReadAllBytes(_path);
        int failures = 0;
        for (int writes = 0; ; writes++)
        {
            Assert.True(writes < 500, "the column was never added");
            using (var db = Database.Open(_path))
            {
                var table = db.GetTable("DaoMade");
                Column(table, "OptLong").Properties.Set("Caption", "Changed and not saved");
                db.File.WritesBeforeFailure = writes;
                try
                {
                    table.AddColumn(new ColumnDefinition("Added", DataType.Memo) { IsRequired = true, AllowZeroLength = false });
                    db.File.WritesBeforeFailure = null;
                    break;
                }
                catch (IOException)
                {
                    db.File.WritesBeforeFailure = null;
                    failures++;
                }
                // The table has no such column, its properties none for it, and the change made before is
                // still the only one to be saved.
                Assert.DoesNotContain(table.Columns, c => c.Name == "Added");
                Assert.Null(table.PropertyMaps.Get("Added"));
                Assert.True(table.PropertyMaps.IsDirty);
                Assert.Equal("Changed and not saved", Column(table, "OptLong").Properties["Caption"]);
            }
            AssertFileIs(before, $"when write {writes + 1} of adding the column failed");
        }
        Assert.True(failures >= 3, $"adding the column was made to fail only {failures} times");

        using var read = Database.Open(_path);
        var added = Column(read.GetTable("DaoMade"), "Added");
        Assert.True(added.IsRequired);
        Assert.False(added.AllowZeroLength);
        Assert.Equal("Optional", Column(read.GetTable("DaoMade"), "OptLong").Properties["Caption"]);
    }

    // ── Names and values Access gives no property ────────────────────────────

    [Fact]
    public void A_name_Access_gives_no_property_is_refused()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var map = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) }).Properties;

        // Asked of DAO: 65 characters is "Invalid argument"; the rest "... is not a valid name".
        foreach (string name in new[] { new string('n', 65), " lead", "a.b", "a!b", "a`b", "a[b", "a]b", "a\tb", "a\nb", "a\u0001b", "   " })
        {
            Assert.Throws<ArgumentException>(() => map.Set(name, "v"));
            Assert.Throws<ArgumentException>(() => map.Set(name, DataType.Text, "v"));
            Assert.Throws<ArgumentException>(() => map.Set(name, DataType.Text, "v", isDdl: true));
        }
        Assert.Empty(map);

        // And what it takes.
        string[] taken = { new string('n', 64), "a b", "trail ", "a\"b", "a'b", "1abc", "café日", "a=b", "a/b", "a:b", "a#b", "x" };
        foreach (string name in taken) map.Set(name, "v");
        map.Save();
        Assert.Equal(taken, PropertyMapReader.Read(PropertyValues(db).Single(v => v.Key.StartsWith("T#")).Value, db.File.Format).Default.Select(p => p.Name));
    }

    [Fact]
    public void A_value_of_no_length_is_refused_when_it_is_set_and_read_as_empty_when_a_file_has_one()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var map = db.CreateTable("T", new[] { new ColumnDefinition("Name", DataType.Text) }).Columns.Single().Properties;

        // Asked of DAO: "User-defined properties do not support a Null value", for '' given a new property and
        // for '' given one that is there.
        Assert.Throws<ArgumentException>(() => map.Set("Caption", ""));
        Assert.Throws<ArgumentException>(() => map.Set("AMemo", DataType.Memo, ""));
        Assert.Throws<ArgumentException>(() => map.Set("Bytes", Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => map.Set("Binary", DataType.Binary, Array.Empty<byte>()));
        Assert.False(map.TryGetValue("Caption", out _));

        // A file can hold one all the same (DAO reads it as ''): it is read as empty, not as no value.
        var maps = new PropertyMaps();
        var column = maps.GetOrAdd("Name", PropertyMap.ColumnType);
        column.Add(new PropertyMap.Property("Caption", DataType.Text, null, flags: 0, stored: Array.Empty<byte>()));
        column.Add(new PropertyMap.Property("AMemo", DataType.Memo, null, flags: 0, stored: Array.Empty<byte>()));
        column.Add(new PropertyMap.Property("Bytes", DataType.OleObject, null, flags: 0, stored: Array.Empty<byte>()));
        column.Add(new PropertyMap.Property("ALong", DataType.Int32, null, flags: 0, stored: Array.Empty<byte>()));
        byte[] value = PropertyMapWriter.Write(maps)!;

        var read = PropertyMapReader.Read(value, JetFormat.Jet4).Get("Name")!;
        Assert.Equal("", read["Caption"]);
        Assert.Equal("", read["AMemo"]);
        Assert.Equal(Array.Empty<byte>(), read["Bytes"]);
        Assert.Null(read["ALong"]);
        Assert.True(value.AsSpan().SequenceEqual(PropertyMapWriter.Write(PropertyMapReader.Read(value, JetFormat.Jet4))));
    }
}
