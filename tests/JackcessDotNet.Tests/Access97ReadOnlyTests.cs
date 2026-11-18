using System.Data;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// An Access 97 (Jet3) file is read and never written, as in Jackcess, whose Jet3 format is read-only
/// (qcatco/exportmdb.interop#67). This library used to write to one - a row's end-of-data offset and variable
/// column count in two bytes each, where Jet3 has one, so the row read back with its text null - and to create
/// one. Now everything that would change such a file, or make one, is refused with
/// <see cref="NotSupportedException"/> before anything is done: the file is the same bytes afterwards, and what
/// the caller passed is as it was.
/// <para>
/// The files written to here are copies of Assets/access97-jet40.mdb (see <see cref="Access97ReadTests"/>), whose
/// tables have no index this library could not keep - so nothing else refuses the change first - and of a file
/// of the corpus that Access 97 wrote.
/// </para>
/// </summary>
public sealed class Access97ReadOnlyTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            if (!File.Exists(path)) continue;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private string NewPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"access97_{Guid.NewGuid():N}.mdb");
        _paths.Add(path);
        return path;
    }

    /// <summary>A copy of the Access 97 database Jet 4.0 wrote.</summary>
    private string CopyOfFixture()
    {
        string path = NewPath();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "access97-jet40.mdb"), path);
        return path;
    }

    private static void AssertRefused(Action change)
    {
        var refused = Assert.Throws<NotSupportedException>(change);
        Assert.Contains("Access 97", refused.Message);
        Assert.Contains("read-only", refused.Message);
        Assert.Contains("Jackcess", refused.Message);
    }

    private static Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();
    private static Column Text(string name) => new ColumnBuilder(name, DataType.Text).MaxLength(50).Build();

    private static DataTable Things(string name)
    {
        var things = new DataTable(name);
        things.Columns.Add("Id", typeof(int));
        things.Columns.Add("Name", typeof(string));
        things.Rows.Add(1, "one");
        return things;
    }

    private sealed class Thing
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    private static Row KeysRow(int id)
    {
        var row = new Row { ["Id"] = id };
        for (int j = 1; j <= 10; j++) row["K" + j] = -id - j;
        return row;
    }

    // ── Creating ─────────────────────────────────────────────────────────────

    /// <summary>Each way a database is created, asked for an Access 97 one at the path given.</summary>
    public static readonly Dictionary<string, Action<string>> WaysToCreate = new()
    {
        ["Database.Create"]            = path => Database.Create(path, FileFormat.Access97).Dispose(),
        ["CreateFromDataTable"]        = path => DatabaseImporter.CreateFromDataTable(path, Things("Things"), FileFormat.Access97).Dispose(),
        ["CreateFromDataSet"]          = path => DatabaseImporter.CreateFromDataSet(path, new DataSet { Tables = { Things("Things") } }, FileFormat.Access97).Dispose(),
        ["CreateFromCollection"]       = path => DatabaseImporter.CreateFromCollection(path, new[] { new Thing { Id = 1, Name = "one" } }, FileFormat.Access97).Dispose(),
        ["PageFile, FileMode.Create"]  = path => new PageFile(path, JetFormat.Jet3, FileMode.Create).Dispose(),
        ["PageFile, FileMode.CreateNew"] = path => new PageFile(path, JetFormat.Jet3, FileMode.CreateNew).Dispose(),
        ["PageFile, FileMode.OpenOrCreate"] = path => new PageFile(path, JetFormat.Jet3, FileMode.OpenOrCreate, FileAccess.ReadWrite).Dispose(),
    };

    public static IEnumerable<object[]> NamesOfWaysToCreate() => WaysToCreate.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(NamesOfWaysToCreate))]
    public void Creating_an_Access_97_database_is_refused_and_makes_no_file(string way)
    {
        string path = NewPath();

        AssertRefused(() => WaysToCreate[way](path));

        Assert.False(File.Exists(path), "A file was left where the database was refused.");
    }

    [Theory]
    [MemberData(nameof(NamesOfWaysToCreate))]
    public void Creating_an_Access_97_database_over_a_file_leaves_the_file(string way)
    {
        // Creating a database replaces what is at its path: a refused one must not have begun to.
        string path = NewPath();
        byte[] before = { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(path, before);

        AssertRefused(() => WaysToCreate[way](path));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void The_format_of_Access_97_is_the_one_that_is_read_only()
    {
        Assert.True(JetFormat.Jet3.IsReadOnly);
        Assert.All(new[] { JetFormat.Jet4, JetFormat.Jet12, JetFormat.Jet14, JetFormat.Jet16, JetFormat.Jet17 },
                   format => Assert.False(format.IsReadOnly));
    }

    // ── Changing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything that changes a database, done to the Access 97 one Jet 4.0 wrote. Keys has a Long primary key
    /// and an index on ten Long columns, LongRows a Long primary key and text with no index: nothing about either
    /// refuses a row but the file's format.
    /// </summary>
    public static readonly Dictionary<string, Action<Database>> Changes = new()
    {
        ["insert"]                  = db => db.GetTable("Keys").Insert(KeysRow(5000)),
        ["insert with text"]        = db => db.GetTable("LongRows").Insert(new Row { ["Id"] = 5000, ["A"] = "text", ["N"] = 1 }),
        ["validate"]                = db => db.GetTable("Keys").Validate(KeysRow(5000)),
        ["update"]                  = db => db.GetTable("Keys").UpdateByPrimaryKey(7, new Row { ["K10"] = 1 }),
        ["update of text"]          = db => db.GetTable("LongRows").UpdateByPrimaryKey(7, new Row { ["A"] = "text" }),
        ["delete"]                  = db => db.GetTable("Keys").DeleteRow("Id", 7),
        ["delete of a long row"]    = db => db.GetTable("LongRows").DeleteRow("Id", 200),
        ["insert, foreign keys kept"] = db => { db.EnforceForeignKeys = true; db.GetTable("Keys").Insert(KeysRow(5000)); },
        ["update, foreign keys kept"] = db => { db.EnforceForeignKeys = true; db.GetTable("Keys").UpdateByPrimaryKey(7, new Row { ["K10"] = 1 }); },
        ["delete, foreign keys kept"] = db => { db.EnforceForeignKeys = true; db.GetTable("Keys").DeleteRow("Id", 7); },
        ["create table"]            = db => db.CreateTable("New", new[] { Long("Id"), Long("N") }),
        ["create table with a primary key"] = db => db.CreateTable("New", new[] { Long("Id"), Long("N") }, primaryKey: "Id"),
        ["create table with a primary key of two columns"] = db => db.CreateTable("New", new[] { Long("Id"), Long("N") }, new[] { "Id", "N" }),
        ["create table with indexes"] = db => db.CreateTable("New", new[] { Long("Id"), Long("N") }, new[] { "Id" }, new[] { new IndexDefinition("ByN", "N") }),
        ["create table with text"]  = db => db.CreateTable("New", new[] { Long("Id"), Text("Name") }),
        ["table builder"]           = db => new TableBuilder("New").AddColumn(Long("Id")).WithPrimaryKey("Id").ToTable(db),
        ["import a DataTable"]      = db => db.ImportTable(Things("Things")),
        ["import a DataTable into a table"] = db => db.ImportTable(KeysAsDataTable(), options: new ImportOptions { AppendIfExists = true }),
        ["import a DataSet"]        = db => db.ImportTables(new DataSet { Tables = { Things("Things"), Things("More") } }),
        ["import a collection"]     = db => db.ImportTable(new[] { new Thing { Id = 1, Name = "one" } }, tableName: "Things"),
        // What the public operations come down to, and what the foreign-key enforcer calls.
        ["rewrite a row in place"]  = db => db.GetTable("Keys").RewriteRowAt(FirstRowOf(db, "Keys"), new Row { ["K10"] = 1 }),
        ["delete a row in place"]   = db => db.GetTable("Keys").DeleteRowAt(FirstRowOf(db, "Keys")),
        ["insert through DataPageWriter"] = db => new DataPageWriter(db.File, new PageAllocator(db.File)).InsertRow(db.GetTable("Keys").Definition, KeysRow(5000)),
        ["update through DataPageWriter"] = db => new DataPageWriter(db.File, new PageAllocator(db.File)).UpdateRowByPrimaryKey(db.GetTable("Keys").Definition, 7, new Row { ["K10"] = 1 }),
        ["delete through DataPageWriter"] = db => new DataPageWriter(db.File, new PageAllocator(db.File)).DeleteRow(db.GetTable("Keys").Definition, "Id", 7),
        ["count a row through DataPageWriter"] = db => new DataPageWriter(db.File, new PageAllocator(db.File)).IncrementTdefRowCount(db.GetTable("Keys").Definition.TdefPageNumber),
        ["raise the AutoNumber"]    =db => db.GetTable("Keys").RaiseLastAutoNumber(9000),
        ["write a page"]            = db => db.File.WritePage(1, new byte[db.File.Format.PageSize]),
        ["write a page past the end"] = db => db.File.WritePage(db.File.PageCount, new byte[db.File.Format.PageSize]),
        ["allocate a page"]         = db => new PageAllocator(db.File).AllocatePage(),
        ["allocate a data page"]    = db => new PageAllocator(db.File).AllocateDataPage(db.GetTable("Keys").Definition.TdefPageNumber),
        ["begin a change to undo"]  = db => db.File.BeginUndo().Dispose(),
        ["replace the file"]        = db => db.File.ReplaceWith(new MemoryStream(new byte[db.File.Format.PageSize])),
        ["write a header"]          = db => DatabaseHeader.Create(JetFormat.Jet3).WriteTo(db.File),
        ["write a Jet4 header"]     = db => DatabaseHeader.Create(JetFormat.Jet4).WriteTo(db.File),
        ["an index entry"]          = db => new IndexWriter(db.File, new PageAllocator(db.File)).InsertPrimaryKey(db.GetTable("Keys").Definition, 5000, FirstRowOf(db, "Keys")),
    };

    public static IEnumerable<object[]> NamesOfChanges() => Changes.Keys.Select(name => new object[] { name });

    private static DataTable KeysAsDataTable()
    {
        var keys = new DataTable("Keys");
        keys.Columns.Add("Id", typeof(int));
        for (int j = 1; j <= 10; j++) keys.Columns.Add("K" + j, typeof(int));
        keys.Rows.Add(new object[] { 5000 }.Concat(Enumerable.Range(1, 10).Select(j => (object)(-5000 - j))).ToArray());
        return keys;
    }

    private static int FirstRowOf(Database db, string tableName)
    {
        var table = db.GetTable(tableName);
        return new IndexWriter(db.File, new PageAllocator(db.File)).EnumerateRowPointersForKey(table.Definition, 1).First();
    }

    [Theory]
    [MemberData(nameof(NamesOfChanges))]
    public void A_change_to_an_Access_97_file_is_refused_and_the_file_is_the_same_bytes(string change)
    {
        string path = CopyOfFixture();
        byte[] before = File.ReadAllBytes(path);
        DateTime written = File.GetLastWriteTimeUtc(path);

        using (var db = Database.Open(path))
        {
            Assert.Equal(FileFormat.Access97, db.File.Format.Version);
            AssertRefused(() => Changes[change](db));
            Assert.Equal(0, db.File.WriteCount);

            // The database is still one to read, and holds what it held.
            Assert.Equal(new[] { "Keys", "LongRows" }, db.GetTableNames().OrderBy(name => name));
            Assert.Equal(1500, db.GetTable("Keys").Rows.Count());
            Assert.Equal(260, db.GetTable("LongRows").Rows.Count());
            Assert.Equal(1500, db.GetTable("Keys").RowCount);
            Assert.NotNull(db.GetTable("Keys").FindRowByPrimaryKey(7));
            Assert.Null(db.GetTable("Keys").FindRowByPrimaryKey(5000));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void A_refused_insert_leaves_the_row_as_the_caller_gave_it()
    {
        using var db = Database.Open(CopyOfFixture());
        var row = new Row { ["A"] = "text" };

        AssertRefused(() => db.GetTable("LongRows").Insert(row));

        Assert.Equal(new[] { new KeyValuePair<string, object?>("A", "text") }, row);
    }

    [Fact]
    public void A_file_Access_97_wrote_is_refused_changes_however_it_is_opened()
    {
        string? source = TestCorpus.File("V1997", "delV1997.mdb");
        Assert.NotNull(source);
        string path = NewPath();
        File.Copy(source!, path);
        File.SetAttributes(path, FileAttributes.Normal);
        byte[] before = File.ReadAllBytes(path);

        foreach (var open in new Func<Database>[]
                 {
                     () => Database.Open(path),
                     () => Database.Open(path, new DatabaseOpenOptions { FallbackFormat = FileFormat.Access97 }),
                     () => Database.Open(path, new DatabaseOpenOptions { Password = "a password the file does not need" }),
                 })
        {
            using var db = open();
            // A table without indexes: nothing else about it refuses a row.
            var table = db.GetTableNames().Select(db.GetTable)
                .First(t => t.Columns.Any(c => c.DataType == DataType.Text) && t.Indexes.Count == 0 && !t.Definition.HasUnreadIndexes);
            var text = table.Columns.First(c => c.DataType == DataType.Text);
            int rows = table.Rows.Count();

            AssertRefused(() => table.Insert(new Row { [text.Name] = "x" }));
            AssertRefused(() => db.CreateTable("New", new[] { Long("Id") }));

            Assert.Equal(0, db.File.WriteCount);
            Assert.Equal(rows, table.Rows.Count());
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void An_Access_97_file_that_cannot_be_written_to_is_opened_and_read()
    {
        // Nothing is asked of the file but to be read, so a file marked read-only opens like any other.
        string path = CopyOfFixture();
        File.SetAttributes(path, FileAttributes.ReadOnly);

        using var db = Database.Open(path);

        Assert.Equal(1500, db.GetTable("Keys").Rows.Count());
        AssertRefused(() => db.GetTable("Keys").Insert(KeysRow(5000)));
    }

    // ── The file says what it is, not the caller ─────────────────────────────

    public static IEnumerable<object[]> OtherVersions()
        => new[] { FileFormat.Access2000, FileFormat.Access2007, FileFormat.Access2010, FileFormat.Access2016, FileFormat.Access2019 }
            .Select(version => new object[] { version });

    public static IEnumerable<object[]> ChangesUnderOtherVersions()
        => from version in new[] { FileFormat.Access2000, FileFormat.Access2007, FileFormat.Access2019 }
           from change in new[] { "insert", "insert with text", "update", "delete", "create table", "import a DataTable", "write a page", "allocate a page" }
           select new object[] { version, change };

    /// <summary>
    /// <see cref="Database.Open(string, FileFormat)"/> took the caller's word for the format: an Access 97 file
    /// opened as Jet4 was not one to refuse changes to, and was read in pages of the wrong size. What a file is
    /// is in its header, as Jackcess takes it; the version a caller names does not change it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ChangesUnderOtherVersions))]
    public void An_Access_97_file_opened_as_another_version_is_read_only_all_the_same(FileFormat said, string change)
    {
        string path = CopyOfFixture();
        byte[] before = File.ReadAllBytes(path);

        foreach (var open in new Func<Database>[] { () => Database.Open(path, new DatabaseOpenOptions { FallbackFormat = said }) })
        {
            using var db = open();
            AssertRefused(() => Changes[change](db));
            Assert.Equal(0, db.File.WriteCount);
            Assert.Equal(FileFormat.Access97, db.File.Format.Version);

            // And it is read as the Access 97 file it is.
            Assert.Equal(new[] { "Keys", "LongRows" }, db.GetTableNames().OrderBy(name => name));
            Assert.Equal(1500, db.GetTable("Keys").Rows.Count());
            Assert.NotNull(db.GetTable("Keys").FindRowByPrimaryKey(7));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [MemberData(nameof(OtherVersions))]
    public void A_file_Access_97_wrote_opened_as_another_version_is_read_only_all_the_same(FileFormat said)
    {
        string path = NewPath();
        File.Copy(TestCorpus.File("V1997", "delV1997.mdb")!, path);
        File.SetAttributes(path, FileAttributes.Normal);
        byte[] before = File.ReadAllBytes(path);

        List<string> tables;
        using (var asItIs = Database.Open(path)) tables = asItIs.GetTableNames().ToList();

        using (var db = Database.Open(path, new DatabaseOpenOptions { FallbackFormat = said }))
        {
            Assert.Equal(FileFormat.Access97, db.File.Format.Version);
            Assert.Equal(tables, db.GetTableNames());

            // A table without indexes: nothing else about it refuses a row.
            var table = db.GetTableNames().Select(db.GetTable)
                .First(t => t.Columns.Any(c => c.DataType == DataType.Text) && t.Indexes.Count == 0 && !t.Definition.HasUnreadIndexes);
            var text = table.Columns.First(c => c.DataType == DataType.Text);
            var rows = table.Rows.ToList();
            var held = rows.First(row => row.ContainsKey(text.Name));

            AssertRefused(() => table.Insert(new Row { [text.Name] = "x" }));
            AssertRefused(() => table.UpdateByPrimaryKey(1, new Row { [text.Name] = "x" }));
            AssertRefused(() => table.DeleteRow(text.Name, held[text.Name]!));
            AssertRefused(() => db.CreateTable("New", new[] { Long("Id") }));

            Assert.Equal(0, db.File.WriteCount);
            Assert.Equal(rows.Count, table.Rows.Count());
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(FileMode.Open)]
    [InlineData(FileMode.OpenOrCreate)]
    public void A_page_file_opened_on_an_Access_97_file_as_Jet4_writes_nothing(FileMode mode)
    {
        // Under every Database is a PageFile, and it is a public class: it too goes by the file.
        string path = CopyOfFixture();
        byte[] before = File.ReadAllBytes(path);

        using (var file = new PageFile(path, JetFormat.Jet4, mode))
        {
            AssertRefused(() => file.WritePage(1, new byte[JetFormat.Jet4.PageSize]));
            AssertRefused(() => file.ReplaceWith(new MemoryStream(new byte[JetFormat.Jet4.PageSize])));
            AssertRefused(() => new PageAllocator(file).AllocatePage());
            Assert.Equal(0, file.WriteCount);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(FileMode.Truncate)]
    [InlineData(FileMode.Append)]
    public void A_page_file_is_not_opened_to_empty_or_add_to_an_Access_97_file(FileMode mode)
    {
        // Opening a file to truncate it is the truncation: the file is asked what it is before any handle is opened.
        string path = CopyOfFixture();
        byte[] before = File.ReadAllBytes(path);

        AssertRefused(() => new PageFile(path, JetFormat.Jet4, mode, FileAccess.Write).Dispose());

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_Jet4_database_can_be_created_where_an_Access_97_file_was()
    {
        // Creating a database replaces what is at its path, whatever that was: only a new Access 97 one is refused.
        string path = CopyOfFixture();

        using (var db = Database.Create(path, FileFormat.Access2000))
            db.CreateTable("Things", new[] { Long("Id") }).Insert(new Row { ["Id"] = 1 });

        using var again = Database.Open(path);
        Assert.Equal(FileFormat.Access2000, again.File.Format.Version);
        Assert.Equal(new[] { "Things" }, again.GetTableNames());
    }

    [Fact]
    public void A_Jet4_database_opened_as_Access_97_is_the_Jet4_database_it_is()
    {
        // The other way about does no harm: the file is read as what it is, and is written to as before.
        string path = NewPath();
        using (var db = Database.Create(path, FileFormat.Access2000))
            db.CreateTable("Things", new[] { Long("Id"), Text("Name") }, primaryKey: "Id").Insert(new Row { ["Id"] = 1, ["Name"] = "one" });

        using (var db = Database.Open(path, new DatabaseOpenOptions { FallbackFormat = FileFormat.Access97 }))
        {
            Assert.Equal(FileFormat.Access2000, db.File.Format.Version);
            var things = db.GetTable("Things");
            Assert.Equal("one", things.FindRowByPrimaryKey(1)!["Name"]);
            things.Insert(new Row { ["Id"] = 2, ["Name"] = "two" });
            things.UpdateByPrimaryKey(1, new Row { ["Name"] = "uno" });
        }

        using var again = Database.Open(path);
        Assert.Equal(new[] { "uno", "two" }, again.GetTable("Things").Rows.OrderBy(row => row["Id"]).Select(row => row["Name"]));
    }

    [Fact]
    public void A_page_file_said_to_be_Access_97_s_is_not_written_to_whatever_the_file_is()
    {
        // A Jet4 file under a PageFile given Access 97's format: the pages are not Jet3 pages, and none is written.
        string path = NewPath();
        Database.Create(path, FileFormat.Access2000).Dispose();
        byte[] before = File.ReadAllBytes(path);

        using (var file = new PageFile(path, JetFormat.Jet3, FileMode.Open))
            AssertRefused(() => file.WritePage(1, new byte[JetFormat.Jet3.PageSize]));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ── Nothing makes an Access 97 row or table definition ───────────────────

    [Fact]
    public void No_row_is_encoded_for_Access_97()
    {
        // RowEncoder is a public class. It made a Jet3 row with two bytes where the format has one.
        AssertRefused(() => new RowEncoder(JetFormat.Jet3, new[] { Long("Id"), Text("Name") }));
        AssertRefused(() => new RowEncoder(JetFormat.Jet3, new TableDefinition("T", new[] { Long("Id"), Text("Name") })));

        using var db = Database.Open(CopyOfFixture());
        AssertRefused(() => new RowEncoder(db.File.Format, db.GetTable("LongRows").Definition));
    }

    [Fact]
    public void No_table_definition_is_made_for_Access_97()
    {
        AssertRefused(() => new TableDefinition("T", new[] { Long("Id"), Text("Name") }).Serialize(JetFormat.Jet3));
    }

    [Fact]
    public void A_Jet4_database_is_still_created_and_written()
    {
        // The refusal is of Access 97's format alone.
        string path = NewPath();
        using (var db = Database.Create(path, FileFormat.Access2000))
        {
            var table = db.CreateTable("Things", new[] { Long("Id"), Text("Name") }, primaryKey: "Id");
            table.Insert(new Row { ["Id"] = 1, ["Name"] = "one" });
            table.UpdateByPrimaryKey(1, new Row { ["Name"] = "uno" });
        }

        using var again = Database.Open(path);
        Assert.Equal("uno", again.GetTable("Things").FindRowByPrimaryKey(1)!["Name"]);
    }
}
