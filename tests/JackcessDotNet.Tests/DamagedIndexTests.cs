using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A change that must take a row's entry out of an index that does not hold it under the row's key (#81): the
/// primary key of T has row 3's entry under 30, as an earlier version of this library or another program could
/// leave it. Access, given this very file, refuses an update of the row's key and a delete of the row ("The search
/// key was not found in any record") and changes nothing, and makes an update of its other columns (measured with
/// ACE 16 and DAO). Made, the update left the row in the index twice, under 30 and 33, and DAO's compaction copied
/// the row twice and dropped the primary key. With <see cref="Database.VerifyIndexes"/> the change is made, and
/// mends the index (#66). ExportMdb.Interop's DamagedIndexInAccessTests has Access read, seek, add to and compact
/// what is left.
/// <para>
/// The file is Assets/damaged-primary-key.mdb, which <see cref="Make"/> made (and
/// <see cref="The_file_is_the_one_Make_makes"/> holds it to).
/// </para>
/// </summary>
public sealed class DamagedIndexTests : IDisposable
{
    private const string Asset = "damaged-primary-key.mdb";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"damaged_index_{Guid.NewGuid():N}.mdb");

    public DamagedIndexTests()
        => File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", Asset), _path);

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>
    /// Makes the file at <paramref name="path"/>: T, with a Long primary key on Id and rows 1 to 5, whose key holds
    /// row 3's entry under 30 and none under 3.
    /// </summary>
    internal static void Make(string path)
    {
        using var db = Database.Create(path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 } }, new[] { "Id" });
        foreach (int id in new[] { 1, 2, 3, 4, 5 })
            table.Insert(new Row { ["Id"] = id, ["Name"] = "row " + id });

        var tree   = table.Indexes.Single();
        var target = IndexWriter.Target.For(table.Definition.TdefPageNumber, tree);
        var writer = new IndexWriter(db.File, new PageAllocator(db.File));
        int three  = PointerOf(db, 3);
        writer.Remove(target, Key(table, 3), three);
        writer.Insert(target, Key(table, 30), three);
    }

    [Fact]
    public void The_file_is_the_one_Make_makes()
    {
        string made = Path.Combine(Path.GetTempPath(), $"damaged_index_made_{Guid.NewGuid():N}.mdb");
        try
        {
            Make(made);
            using var asset = Database.Open(_path);
            using var fresh = Database.Open(made);
            foreach (var db in new[] { asset, fresh })
            {
                Assert.Equal(new[] { 3 }, Under(db, 30));
                Assert.Empty(Under(db, 3));
                Assert.Equal(new[] { 1, 2, 3, 4, 5 }, db.GetTable("T").Rows.Select(r => (int)r["Id"]!).OrderBy(i => i));
            }
        }
        finally { if (File.Exists(made)) File.Delete(made); }
    }

    private static byte[] Key(Table table, int id) => IndexKeys.Encode(table.Indexes.Single().Columns, new object?[] { id });

    private static int PointerOf(Database db, int id)
    {
        var cursor = db.GetTable("T").CreateCursor();
        while (cursor.MoveNext())
            if ((int)cursor.Current!["Id"]! == id) return cursor.CurrentRowPointer;
        throw new InvalidOperationException($"T has no row {id}.");
    }

    /// <summary>The Ids of the rows the primary key holds under <paramref name="id"/>, by the row each entry points at.</summary>
    private static List<int> Under(Database db, int id)
    {
        var table = db.GetTable("T");
        var ids = new Dictionary<int, int>();
        var cursor = table.CreateCursor();
        while (cursor.MoveNext()) ids[cursor.CurrentRowPointer] = (int)cursor.Current!["Id"]!;
        return new IndexWriter(db.File, new PageAllocator(db.File))
            .RowPointersForKey(table.Indexes.Single().RootPageNumber, Key(table, id))
            .Select(pointer => ids.TryGetValue(pointer, out int at) ? at : -1).ToList();
    }

    // The row, found by reading the table, as Access's WHERE Name = 'row 3' finds it.
    private static Cursor AtRowThree(Database db)
    {
        var cursor = db.GetTable("T").CreateCursor();
        Assert.NotNull(cursor.FindRow("Name", "row 3"));
        return cursor;
    }

    [Fact]
    public void An_update_of_the_key_of_a_row_the_index_does_not_hold_under_it_is_refused_and_nothing_changes()
    {
        using var db = Database.Open(_path);
        var before = IndexChecks.AllPages(db);
        var cursor = AtRowThree(db);
        var refused = Assert.Throws<NotSupportedException>(() => cursor.UpdateCurrentRow(new Row { ["Id"] = 33 }));
        Assert.Contains("VerifyIndexes", refused.Message);
        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(new[] { 3 }, Under(db, 30));
        Assert.Empty(Under(db, 33));
    }

    [Fact]
    public void A_delete_of_a_row_the_index_does_not_hold_under_its_key_is_refused_and_nothing_changes()
    {
        using var db = Database.Open(_path);
        var before = IndexChecks.AllPages(db);
        var cursor = AtRowThree(db);
        Assert.Throws<NotSupportedException>(() => cursor.DeleteCurrentRow());
        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(5, db.GetTable("T").RowCount);
    }

    [Fact]
    public void A_delete_through_the_table_of_such_a_row_is_refused_too()
    {
        using var db = Database.Open(_path);
        var before = IndexChecks.AllPages(db);
        Assert.Throws<NotSupportedException>(() => db.GetTable("T").DeleteRow("Name", "row 3"));
        IndexChecks.AssertUnchanged(before, db);
    }

    // Access makes it: the key does not change, so no entry is taken out, and the entry under 30 stays as it was.
    [Fact]
    public void An_update_of_another_column_of_such_a_row_is_made_and_leaves_its_entry_where_it_was()
    {
        using (var db = Database.Open(_path))
            AtRowThree(db).UpdateCurrentRow(new Row { ["Name"] = "x" });

        using var read = Database.Open(_path);
        Assert.Equal(new[] { 3 }, Under(read, 30));
        Assert.Equal("x", read.GetTable("T").Rows.Single(r => (int)r["Id"]! == 3)["Name"]);
    }

    [Fact]
    public void With_indexes_verified_the_update_is_made_and_the_row_is_in_the_index_once()
    {
        using (var db = Database.Open(_path))
        {
            db.VerifyIndexes = true;
            AtRowThree(db).UpdateCurrentRow(new Row { ["Id"] = 33 });
        }

        using var read = Database.Open(_path);
        Assert.Empty(Under(read, 30));
        Assert.Empty(Under(read, 3));
        Assert.Equal(new[] { 33 }, Under(read, 33));
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "T", expectedRows: 5);
    }

    [Fact]
    public void With_indexes_verified_the_delete_is_made_and_leaves_no_entry_of_the_row()
    {
        using (var db = Database.Open(_path))
        {
            db.VerifyIndexes = true;
            AtRowThree(db).DeleteCurrentRow();
        }

        using var read = Database.Open(_path);
        Assert.Empty(Under(read, 30));
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "T", expectedRows: 4);
    }

    /// <summary>
    /// In a sound index the check costs no page: the pages the update's and the delete's removal go down are the ones
    /// it read, kept by its writer. The counts are those of a primary key three levels deep, measured on the engine
    /// before the check (2bc4307): an update of the key, an update of another column, and a delete found by reading
    /// the table.
    /// </summary>
    [Fact]
    public void In_a_sound_index_the_check_reads_no_page_more()
    {
        string path = Path.Combine(Path.GetTempPath(), $"damaged_index_sound_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(path, FileFormat.Access2000))
            {
                var table = db.CreateTable("S", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 200 } }, new[] { "Id" });
                table.InsertRange(Enumerable.Range(1, 20000).Select(i => new Row { ["Id"] = i, ["Name"] = new string('n', 150) + i }));
            }
            using var open = Database.Open(path);
            var s = open.GetTable("S");
            long Reads(Action change) { long at = open.File.ReadCount; change(); return open.File.ReadCount - at; }

            long key   = Reads(() => s.UpdateByPrimaryKey(10000, new Row { ["Id"] = 30000 }));
            long other = Reads(() => s.UpdateByPrimaryKey(10001, new Row { ["Name"] = "short" }));
            long gone  = Reads(() => s.DeleteRow("Id", 10002));
            Assert.Equal((MainKey, MainOther, MainDelete), (key, other, gone));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private const long MainKey = 21, MainOther = 9, MainDelete = 439;
}
