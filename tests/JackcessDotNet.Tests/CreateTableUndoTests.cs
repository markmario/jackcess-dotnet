using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table whose creation fails while it is being written is not made, and the file is as it was (#60), as a schema
/// change is (<see cref="SchemaChangeUndoTests"/>). Every page write <see cref="Database.CreateTable(string, IEnumerable{ColumnDefinition}, IEnumerable{string}, IEnumerable{IndexDefinition})"/>
/// makes is failed in turn (<see cref="PageFile.WritesBeforeFailure"/>), in one session, and after each failure
/// the file is byte for byte what it was, the session sees the database a fresh open of the file sees, and the table
/// is not listed. An import into a new table creates its table so, and is held to the same. A new database whose
/// writing fails leaves no file.
/// </summary>
public sealed class CreateTableUndoTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"create_undo_{Guid.NewGuid():N}");
    private string? _file;

    public void Dispose()
    {
        if (_file is not null && File.Exists(_file)) File.Delete(_file);
    }

    public static IEnumerable<object[]> Formats()
        => new[] { FileFormat.Access2000, FileFormat.Access2007, FileFormat.Access2010 }.Select(f => new object[] { f });

    // A table of what CreateTable writes besides its definition: an AutoNumber, a primary key and a unique index, the
    // usage maps of two long-value columns, and properties (Required, Allow Zero Length), which go in its catalog row.
    private static readonly ColumnDefinition[] Columns =
    {
        new("Id", DataType.Int32) { IsAutoNumber = true },
        new("Code", DataType.Text) { MaxLength = 20, IsRequired = true, AllowZeroLength = false },
        new("Notes", DataType.Memo),
        new("Raw", DataType.OleObject),
        new("Amount", DataType.Currency),
    };

    private static readonly IndexDefinition[] Indexes = { new("ByCode", "Code") { IsUnique = true } };

    /// <summary>
    /// A database with what a failed creation has to leave as it was: a table with rows and long values, some of
    /// whose pages a delete emptied, and room in the catalog's long values that a dropped table's properties left.
    /// </summary>
    private string Prepare(FileFormat format)
    {
        _file = _path + (format == FileFormat.Access2000 ? ".mdb" : ".accdb");
        using var db = Database.Create(_file, format);
        var existing = db.CreateTable("Existing", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
            new ColumnDefinition("Notes", DataType.Memo),
        }, "Id");
        for (int i = 1; i <= 40; i++)
            existing.Insert(new Row { ["Id"] = i, ["Name"] = $"row {i}", ["Notes"] = new string((char)('a' + i % 26), i * 150) });
        foreach (var row in existing.Rows.Where(r => (int)r["Id"]! % 4 == 0).ToList()) existing.Delete(row);
        db.CreateTable("Dropped", Columns, new[] { "Id" }, Indexes);
        db.DropTable("Dropped");
        return _file;
    }

    private static byte[] Bytes(string path)
    {
        // The session has the file open: read it as another reader of it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        int read = 0;
        while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
        return bytes;
    }

    /// <summary>
    /// What a database shows a caller of itself, as text: its tables and system tables, each with its columns,
    /// indexes, counts and rows, and its relationships.
    /// </summary>
    internal static string ViewOf(Database db)
    {
        var view = new StringBuilder();
        view.AppendLine("tables: " + string.Join(", ", db.GetTableNames()));
        view.AppendLine("system tables: " + string.Join(", ", db.GetSystemTableNames()));
        foreach (string name in db.GetTableNames().Concat(db.GetSystemTableNames()))
        {
            var table = db.GetTable(name);
            view.AppendLine($"table {table.Name}: {table.RowCount} rows, AutoNumber at {table.LastAutoNumber}");
            foreach (var c in table.Columns)
                view.AppendLine($"  column {c.Name} {c.DataType} {c.LengthInBytes} bytes auto={c.IsAutoNumber} required={c.IsRequired} zero-length={c.AllowZeroLength}");
            foreach (var ix in table.Indexes)
                view.AppendLine($"  index {ix.Name} ({string.Join(", ", ix.Columns.Select(c => c.Column.Name))}) unique={ix.IsUnique} primary={ix.IsPrimaryKey}");
            try
            {
                foreach (var row in table.Rows)
                    view.AppendLine("  row " + string.Join(" | ", table.Columns.Select(c => Show(row[c.Name]))));
            }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException)
            {
                view.AppendLine("  rows not read: " + e.GetType().Name);
            }
        }
        foreach (var relationship in db.GetRelationships())
            view.AppendLine($"relationship {relationship.Name}: {relationship.FromTableName} -> {relationship.ToTableName}");
        return view.ToString();
    }

    private static string Show(object? value) => value switch
    {
        null           => "null",
        byte[] bytes   => "0x" + BitConverter.ToString(bytes).Replace("-", ""),
        DateTime date  => date.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _              => value.ToString() ?? "",
    };

    /// <summary>
    /// Makes <paramref name="create"/> fail at its first page write, then its second, and so on until it has the
    /// writes it needs, all in one session; checks the file and the session after each failure, and returns how many
    /// writes it failed at.
    /// </summary>
    private static int FailAtEveryWrite(Database db, string path, string table, Action<Database> create)
    {
        byte[] before = Bytes(path);
        string view = ViewOf(db);
        using (var fresh = Database.Open(CopyOf(path))) Assert.Equal(view, ViewOf(fresh));

        for (int writes = 0; ; writes++)
        {
            Assert.True(writes < 2_000, "the table was never made");
            db.File.WritesBeforeFailure = writes;
            try
            {
                create(db);
                return writes;   // it had the writes it needed
            }
            catch (IOException)
            {
            }
            finally
            {
                db.File.WritesBeforeFailure = null;
            }

            Assert.True(before.AsSpan().SequenceEqual(Bytes(path)), $"the file changed when write {writes + 1} of the creation failed");
            Assert.DoesNotContain(table, db.GetTableNames(), StringComparer.OrdinalIgnoreCase);
            Assert.False(db.TryGetTable(table, out _), $"the session has the table after write {writes + 1} failed");
            Assert.Equal(view, ViewOf(db));
        }
    }

    // Main's Database.Open does not open a file another Database has open for writing: the fresh open is of a copy.
    private static string CopyOf(string path)
    {
        string copy = path + ".copy" + Path.GetExtension(path);
        File.WriteAllBytes(copy, Bytes(path));
        return copy;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_table_whose_writing_fails_at_any_page_is_not_made_and_the_file_is_as_it_was(FileFormat format)
    {
        string path = Prepare(format);
        try
        {
            using var db = Database.Open(path);
            var existing = db.GetTable("Existing");
            int failures = FailAtEveryWrite(db, path, "New", d => d.CreateTable("New", Columns, new[] { "Id" }, Indexes));
            Assert.True(failures >= 10, $"only {failures} writes failed: a table with two indexes and two long-value columns has more");

            // The session goes on from where the failures left it: the table is made and takes rows, and so does the
            // table that was there, its long values going where the deletes left room.
            var made = db.GetTable("New");
            for (int i = 0; i < 20; i++)
                made.Insert(new Row { ["Code"] = $"code {i}", ["Notes"] = new string('n', i * 300), ["Raw"] = new byte[i * 100], ["Amount"] = i * 1.25m });
            for (int i = 41; i <= 50; i++)
                existing.Insert(new Row { ["Id"] = i, ["Name"] = $"row {i}", ["Notes"] = new string('e', i * 100) });
            Assert.Throws<ConstraintViolationException>(() => made.Insert(new Row { ["Code"] = "code 3" }));
        }
        finally
        {
            File.Delete(path + ".copy" + Path.GetExtension(path));
        }

        using var after = Database.Open(path);
        var table = after.GetTable("New");
        Assert.Equal(Enumerable.Range(1, 20), table.Rows.Select(r => (int)r["Id"]!));
        Assert.Equal(new string('n', 19 * 300), table.FindRowByPrimaryKey(20)!["Notes"]);
        Assert.Equal(40 - 10 + 10, after.GetTable("Existing").RowCount);
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "New", expectedRows: 20));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "MSysObjects"));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void An_import_into_a_new_table_whose_writing_fails_at_any_page_is_not_made_and_the_file_is_as_it_was(FileFormat format)
    {
        // The import's table only: an import that fails among its rows is taken back by the import itself.
        var source = new DataTable("Source");
        source.Columns.Add("Id", typeof(int));
        source.Columns.Add(new DataColumn("Name", typeof(string)) { MaxLength = 30 });
        source.Columns.Add(new DataColumn("Notes", typeof(string)) { MaxLength = 10_000 });   // a Memo
        source.Columns.Add("Amount", typeof(decimal));
        source.Columns.Add("When", typeof(DateTime));

        string path = Prepare(format);
        try
        {
            using var db = Database.Open(path);
            int failures = FailAtEveryWrite(db, path, "Imported",
                d => d.ImportTable(source, new ImportOptions { TableName = "Imported", PrimaryKey = new[] { "Id" } }));
            Assert.True(failures >= 5, $"only {failures} writes failed");

            var imported = db.GetTable("Imported");
            imported.Insert(new Row { ["Id"] = 1, ["Name"] = "one", ["Notes"] = new string('x', 5_000), ["Amount"] = 1.5m, ["When"] = new DateTime(2026, 11, 20) });
        }
        finally
        {
            File.Delete(path + ".copy" + Path.GetExtension(path));
        }

        using var after = Database.Open(path);
        var row = Assert.Single(after.GetTable("Imported").Rows);
        Assert.Equal(new string('x', 5_000), row["Notes"]);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_table_made_in_a_larger_change_that_is_taken_back_is_forgotten_and_refuses_what_is_asked_of_it(FileFormat format)
    {
        string path = Prepare(format);
        try
        {
            using var db = Database.Open(path);
            byte[] before = Bytes(path);
            string view = ViewOf(db);

            // A larger change - the table, then its rows - fails part-way and is taken back whole.
            Table made = null!;
            Assert.Throws<IOException>(() =>
            {
                using var change = db.File.BeginUndo();
                made = db.CreateTable("New", Columns, new[] { "Id" }, Indexes);
                for (int i = 0; i < 20; i++)
                    made.Insert(new Row { ["Code"] = $"code {i}", ["Notes"] = new string('n', i * 300), ["Amount"] = i * 1.25m });
                Assert.Same(made, db.GetTable("New"));
                db.File.WritesBeforeFailure = 0;
                try { made.Insert(new Row { ["Code"] = "the one that fails" }); }
                finally { db.File.WritesBeforeFailure = null; }
            });

            // The file, and what the session shows of it, are as they were: the table is not listed, held or handed out.
            Assert.True(before.AsSpan().SequenceEqual(Bytes(path)), "the file changed");
            Assert.DoesNotContain("New", db.GetTableNames());
            Assert.DoesNotContain(made, db.HeldTables);
            Assert.False(db.TryGetTable("New", out _));
            Assert.Throws<ArgumentException>(() => db.GetTable("New"));
            Assert.Equal(view, ViewOf(db));

            // The Table handed out is marked dropped, and refuses everything asked of it rather than read or write pages
            // that are not its own.
            Assert.Contains("Table 'New'", Assert.Throws<InvalidOperationException>(() => made.Insert(new Row { ["Code"] = "after" })).Message);
            Assert.Throws<InvalidOperationException>(() => made.Rows.Count());
            Assert.Throws<InvalidOperationException>(() => made.RowCount);
            Assert.Throws<InvalidOperationException>(() => made.Columns);
            Assert.Throws<InvalidOperationException>(() => made.FindRowByPrimaryKey(1));
            Assert.Throws<InvalidOperationException>(() => made.CreateCursor());

            // A table of that name is made again, in pages the one taken back had; the Table taken back is not it, and
            // still refuses.
            var again = db.CreateTable("New", Columns, new[] { "Id" }, Indexes);
            Assert.NotSame(made, again);
            Assert.Same(again, db.GetTable("New"));
            again.Insert(new Row { ["Code"] = "kept", ["Notes"] = new string('k', 3_000), ["Amount"] = 2.5m });
            Assert.Throws<InvalidOperationException>(() => made.Insert(new Row { ["Code"] = "after" }));
            Assert.Throws<InvalidOperationException>(() => made.Rows.Count());

            // A larger change that is kept keeps its table.
            Table kept;
            using (var change = db.File.BeginUndo())
            {
                kept = db.CreateTable("Kept", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id");
                kept.Insert(new Row { ["Id"] = 1 });
                change.Commit();
            }
            Assert.Same(kept, db.GetTable("Kept"));
            kept.Insert(new Row { ["Id"] = 2 });
        }
        finally
        {
            File.Delete(path + ".copy" + Path.GetExtension(path));
        }

        using var after = Database.Open(path);
        Assert.Equal(new[] { "Existing", "Kept", "New" }, after.GetTableNames().OrderBy(n => n, StringComparer.Ordinal));
        var row = Assert.Single(after.GetTable("New").Rows);
        Assert.Equal(("kept", 1), ((string)row["Code"]!, (int)row["Id"]!));
        Assert.Equal(new[] { 1, 2 }, after.GetTable("Kept").Rows.Select(r => (int)r["Id"]!));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "New", expectedRows: 1));
        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(after, "MSysObjects"));
    }

    [Theory]
    [InlineData(FileFormat.Access2000, 50)]
    [InlineData(FileFormat.Access2007, 42)]
    [InlineData(FileFormat.Access2010, 42)]
    [InlineData(FileFormat.Access2016, 43)]
    [InlineData(FileFormat.Access2019, 43)]
    public void A_database_whose_writing_fails_at_any_page_leaves_no_file(FileFormat format, int pages)
    {
        _file = _path + (format == FileFormat.Access2000 ? ".mdb" : ".accdb");
        File.WriteAllBytes(_file, new byte[] { 1, 2, 3 });   // a file there already, which a new database replaces

        int writes = 0;
        for (; ; writes++)
        {
            Assert.True(writes < 1_000, "the database was never made");
            var file = new PageFile(_file, JetFormat.For(format), FileMode.Create) { WritesBeforeFailure = writes };
            try
            {
                using var db = Database.CreateIn(file);
                file.WritesBeforeFailure = null;
                db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id").Insert(new Row { ["Id"] = 1 });
                break;
            }
            catch (IOException)
            {
            }
            // Nothing is left at the path, and nothing holds it.
            Assert.False(File.Exists(_file), $"a file was left when write {writes + 1} of the database failed");
        }
        // The template's pages, and the header's version again for a format a file becomes by its columns.
        Assert.Equal(pages, writes);

        using var made = Database.Open(_file);
        Assert.Equal(format, made.FileFormat);
        Assert.Equal(1, Assert.Single(made.GetTable("T").Rows)["Id"]);
    }

    [Fact]
    public void A_database_whose_writing_fails_and_whose_file_cannot_be_removed_says_the_file_is_left()
    {
        _file = _path + ".mdb";
        var file = new PageFile(_file, JetFormat.For(FileFormat.Access2000), FileMode.Create) { WritesBeforeFailure = 3 };
        // The file made read-only while it is being written: the incomplete file cannot be removed. (No other handle
        // can hold it meanwhile: a file made anew is shared with no one until it is closed, as DAO holds a database
        // it creates - #62. An attribute is set without one.)
        File.SetAttributes(_file, FileAttributes.ReadOnly);
        try
        {
            var left = Assert.Throws<IOException>(() => Database.CreateIn(file));
            Assert.Contains($"'{_file}' could not be written, and the incomplete file it left there could not be removed", left.Message);
            var failure = Assert.IsType<IOException>(left.InnerException);
            Assert.Contains("a test asked for the write to fail", failure.Message);
            Assert.Equal(3 * 4096, new FileInfo(_file).Length);   // what was written before the failure
        }
        finally
        {
            File.SetAttributes(_file, FileAttributes.Normal);
        }
        // The file is the caller's to delete, and nothing of this library holds it.
        Assert.True(File.Exists(_file));
        File.Delete(_file);
    }
}
