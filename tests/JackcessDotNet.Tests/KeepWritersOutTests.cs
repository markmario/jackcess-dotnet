using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A database opened only to be read that keeps writers out (<see cref="DatabaseOpenOptions.KeepWritersOut"/>, #84):
/// its file is held for reading and shared for reading only, as Access holds a database it opens exclusively and
/// read-only (DAO's OpenDatabase exclusive and read-only, ACE's Mode=Share Deny Write; measured with
/// tools/ace-read-only-without-writers.ps1). No one can write to the file while it is held, so it reads it as a
/// database opened to be written does - keeping the page a cursor is on from one move to the next - and not again at
/// every move, as a database opened only to be read beside a writer must. Here Access is a <see cref="FileStream"/>
/// opened as ACE and DAO open the file; FileSharingInAccessTests has ACE and DAO themselves.
/// </summary>
public sealed class KeepWritersOutTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string NewPath(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"keepout_{Guid.NewGuid():N}{extension}");
        _paths.Add(path);
        return path;
    }

    public static IEnumerable<object[]> Formats() => new[] { new object[] { FileFormat.Access2000 }, new object[] { FileFormat.Access2010 } };

    private static DatabaseOpenOptions ReadOnly => new() { ReadOnly = true };
    private static DatabaseOpenOptions KeepingWritersOut => new() { ReadOnly = true, KeepWritersOut = true };

    /// <summary>
    /// T (Id, the primary key; V, text; M, a Memo) of 3,000 rows, "row 1" to "row 3000", every tenth with a memo of
    /// 300 characters, kept outside its row.
    /// </summary>
    private string ThreeThousandRows(FileFormat format)
    {
        string path = NewPath(format == FileFormat.Access2000 ? ".mdb" : ".accdb");
        using var db = Database.Create(path, format);
        db.CreateTable("T", new[]
            {
                new ColumnDefinition("Id", DataType.Int32),
                new ColumnDefinition("V", DataType.Text) { MaxLength = 50 },
                new ColumnDefinition("M", DataType.Memo),
            }, "Id")
          .InsertRange(Enumerable.Range(1, 3000).Select(i => new Row
          {
              ["Id"] = i, ["V"] = "row " + i, ["M"] = i % 10 == 0 ? new string((char)('a' + i % 26), 300) : null,
          }));
        return path;
    }

    // The file as ACE and DAO hold it, measured from the sharing their own handles allow: a database open in Access
    // shared; open read-only; open exclusively; open exclusively and read-only (as this option holds it).
    private static FileStream AsAccessShares(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
    private static FileStream AsAccessReads(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    private static FileStream AsAccessHasItExclusively(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    private static FileStream AsAccessReadsExclusively(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static readonly int SharingViolation = unchecked((int)0x80070020);

    /// <summary>
    /// Passes over a test of what the operating system's keeping to a handle's sharing gives - writers refused - where
    /// it does not keep to it: anywhere but Windows.
    /// </summary>
    private static void OnlyWhereSharingIsKeptTo()
    {
        if (!PageFile.OSEnforcesSharing)
            Assert.Skip("This operating system does not keep to a handle's sharing, so no writer is kept out here.");
    }

    private static DatabaseInUseException AssertInUse(Action open, string path)
    {
        var refused = Assert.Throws<DatabaseInUseException>(open);
        Assert.Contains(path, refused.Message);
        Assert.Equal(SharingViolation, refused.HResult);
        return refused;
    }

    private static void AssertRefusedByWindows(Action open)
        => Assert.Equal(SharingViolation, Assert.ThrowsAny<IOException>(open).HResult);

    // ── What it reads ────────────────────────────────────────────────────────

    /// <summary>The pages <paramref name="read"/> reads of the database at <paramref name="path"/> opened with <paramref name="options"/>.</summary>
    private static long PagesRead(string path, DatabaseOpenOptions options, Func<Table, int> read, int rows)
    {
        using var db = Database.Open(path, options);
        var table = db.GetTable("T");
        long before = db.File.ReadCount;
        Assert.Equal(rows, read(table));
        return db.File.ReadCount - before;
    }

    public static IEnumerable<object[]> FormatsAndReadings()
        => from format in new[] { FileFormat.Access2000, FileFormat.Access2010 }
           from reading in new[] { "Table.Rows", "a table cursor", "an index cursor" }
           select new object[] { format, reading };

    private static int Read(Table table, string reading)
    {
        // Every row whole, its memo read too: a long value is read where its reference says, whichever way.
        static int Count(IEnumerable<Row> rows) => rows.Count(r => r["M"] is null or string { Length: 300 });
        return reading switch
        {
            "Table.Rows" => Count(table.Rows),
            "a table cursor" => Count(table.CreateCursor()),
            "an index cursor" => Count(table.CreateIndexCursor()),
            _ => throw new ArgumentException(reading),
        };
    }

    // Opened to keep writers out, a reading reads the pages a database opened to be written reads - every one - and not
    // the pages a database opened only to be read beside a writer reads again at each move: a page or more a row.
    // Counted in pages read, which are the same on every machine. (Where the operating system does not keep writers out,
    // it reads as one beside a writer does: Where_sharing_is_not_kept_to_....)
    [Theory]
    [MemberData(nameof(FormatsAndReadings))]
    public void Kept_writers_out_a_reading_reads_the_pages_one_opened_to_be_written_reads(FileFormat format, string reading)
    {
        string path = ThreeThousandRows(format);
        long written  = PagesRead(path, new DatabaseOpenOptions(), t => Read(t, reading), 3000);
        long shared   = PagesRead(path, ReadOnly, t => Read(t, reading), 3000);
        long keptOut  = PagesRead(path, KeepingWritersOut, t => Read(t, reading), 3000);

        OnlyWhereSharingIsKeptTo();
        Assert.Equal(written, keptOut);
        Assert.True(shared >= 3000, $"read-only beside a writer read {shared} pages for 3,000 rows: a page a row was expected");
        Assert.True(keptOut * 5 < shared, $"kept writers out, {keptOut} pages were read; beside a writer, {shared}");
    }

    // An Access 97 file is only ever read, and is opened beside a writer - its pages read again at every move - unless
    // it is opened only to be read keeping writers out; then a cursor reads each of its data pages once, and the file is
    // left as it was.
    [Fact]
    public void An_Access_97_file_opened_to_keep_writers_out_reads_each_page_once()
    {
        string path = NewPath(".mdb");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "access97-jet40.mdb"), path);
        File.SetAttributes(path, FileAttributes.Normal);

        int dataPages;
        using (var db = Database.Open(path))
            dataPages = db.GetTable("Keys").Rows.Select(r => r.Id!.Value.PageNumber).Distinct().Count();

        byte[] before = File.ReadAllBytes(path);
        OnlyWhereSharingIsKeptTo();
        long beside = PagesReadOfKeys(path, new DatabaseOpenOptions());
        long keptOut = PagesReadOfKeys(path, KeepingWritersOut);
        Assert.InRange(beside, 1500, 1500 + dataPages + 2);
        Assert.InRange(keptOut, dataPages, dataPages + 2);
        Assert.Equal(before, File.ReadAllBytes(path));

        static long PagesReadOfKeys(string path, DatabaseOpenOptions options)
        {
            using var db = Database.Open(path, options);
            Assert.True(db.IsReadOnly);
            var cursor = db.GetTable("Keys").CreateCursor();
            long before = db.File.ReadCount;
            int rows = 0;
            while (cursor.MoveNext()) rows++;
            Assert.Equal(1500, rows);
            return db.File.ReadCount - before;
        }
    }

    // ── Who opens the file meanwhile ─────────────────────────────────────────

    // While it is held, Access cannot open the file to write - shared, or exclusively - and a Database opened to be
    // written is refused; readers open it: Access read-only, Access exclusively and read-only, a Database opened only to
    // be read, either way. Once it is disposed, a writer opens it and writes.
    [Theory]
    [MemberData(nameof(Formats))]
    public void While_it_is_open_every_writer_is_kept_out_and_readers_are_let_in(FileFormat format)
    {
        OnlyWhereSharingIsKeptTo();
        string path = ThreeThousandRows(format);
        byte[] before = File.ReadAllBytes(path);

        using (var db = Database.Open(path, KeepingWritersOut))
        {
            Assert.True(db.IsReadOnly);
            Assert.False(db.File.SharedWithWriters);

            AssertRefusedByWindows(() => AsAccessShares(path).Dispose());
            AssertRefusedByWindows(() => AsAccessHasItExclusively(path).Dispose());
            AssertInUse(() => Database.Open(path).Dispose(), path);

            using (AsAccessReads(path)) { }
            using (AsAccessReadsExclusively(path)) { }
            using (var reader = Database.Open(path, ReadOnly))
                Assert.Equal(3000, reader.GetTable("T").RowCount);
            using (var second = Database.Open(path, KeepingWritersOut))
                Assert.Equal("row 7", second.GetTable("T").FindRowByPrimaryKey(7)!["V"]);

            Assert.Equal(3000, db.GetTable("T").Rows.Count());
        }
        Assert.Equal(before, File.ReadAllBytes(path));

        using (var writer = Database.Open(path))
            writer.GetTable("T").Insert(new Row { ["Id"] = 3001, ["V"] = "row 3001" });
        using (var again = Database.Open(path, KeepingWritersOut))
            Assert.Equal("row 3001", again.GetTable("T").FindRowByPrimaryKey(3001)!["V"]);
    }

    public static IEnumerable<object[]> Writers() => new[]
    {
        new object[] { "Access shares it" }, new object[] { "Access has it exclusively" }, new object[] { "a Database writes to it" },
    };

    private static IDisposable Hold(string holder, string path) => holder switch
    {
        "Access shares it" => AsAccessShares(path),
        "Access has it exclusively" => AsAccessHasItExclusively(path),
        "a Database writes to it" => Database.Open(path),
        _ => throw new ArgumentException(holder),
    };

    // A file a writer has open is not opened to keep writers out - they cannot be kept out while one has it - and is left
    // as it was; the caller can open it again only to be read, beside the writer, as the refusal says (but not beside
    // Access holding it exclusively, which keeps out every reader).
    [Theory]
    [MemberData(nameof(Writers))]
    public void A_file_a_writer_has_open_is_refused_and_left_as_it_was(string holder)
    {
        OnlyWhereSharingIsKeptTo();
        string path = ThreeThousandRows(FileFormat.Access2010);
        byte[] before = File.ReadAllBytes(path);

        using (Hold(holder, path))
        {
            var refused = AssertInUse(() => Database.Open(path, KeepingWritersOut).Dispose(), path);
            if (holder == "Access has it exclusively")
            {
                Assert.Contains("exclusively", refused.Message);
                AssertInUse(() => Database.Open(path, ReadOnly).Dispose(), path);
            }
            else
            {
                Assert.Contains(nameof(DatabaseOpenOptions.KeepWritersOut), refused.Message);
                using var beside = Database.Open(path, ReadOnly);
                Assert.True(beside.File.SharedWithWriters);
                Assert.Equal(3000, beside.GetTable("T").RowCount);
            }
        }
        Assert.Equal(before, File.ReadAllBytes(path));

        // No one has it now, and it opens.
        using var db = Database.Open(path, KeepingWritersOut);
        Assert.Equal(3000, db.GetTable("T").Rows.Count());
    }

    // Readers do not keep it out: Access read-only, exclusively and read-only, and a Database opened only to be read,
    // either way.
    [Fact]
    public void A_file_readers_have_open_is_opened()
    {
        string path = ThreeThousandRows(FileFormat.Access2000);
        foreach (var hold in new Func<string, IDisposable>[]
                 {
                     p => AsAccessReads(p), p => AsAccessReadsExclusively(p),
                     p => Database.Open(p, ReadOnly), p => Database.Open(p, KeepingWritersOut),
                 })
        {
            using (hold(path))
            using (var db = Database.Open(path, KeepingWritersOut))
                Assert.Equal(3000, db.GetTable("T").Rows.Count());
        }
    }

    // ── Nothing is written ───────────────────────────────────────────────────

    // Every way of reading the file leaves it the same bytes, and a change asked of it is refused before anything
    // happens, as a database opened only to be read refuses one.
    [Theory]
    [MemberData(nameof(Formats))]
    public void Reading_it_leaves_the_file_as_it_was_and_a_change_is_refused(FileFormat format)
    {
        string path = ThreeThousandRows(format);
        byte[] before = File.ReadAllBytes(path);

        using (var db = Database.Open(path, KeepingWritersOut))
        {
            var t = db.GetTable("T");
            Assert.Equal(3000, t.Rows.Count());
            Assert.Equal(3000, t.CreateCursor().Count());
            Assert.Equal(3000, t.CreateIndexCursor().Count());
            Assert.Equal(new string((char)('a' + 1500 % 26), 300), t.FindRowByPrimaryKey(1500)!["M"]);
            var cursor = t.CreateCursor();
            Assert.True(cursor.MoveNext());
            var first = cursor.GetSavepoint();
            Assert.True(cursor.MoveNext());
            cursor.RestoreSavepoint(first);
            Assert.Equal(1, cursor.Current!["Id"]);

            var refused = Assert.Throws<NotSupportedException>(() => t.Insert(new Row { ["Id"] = 5000, ["V"] = "refused" }));
            Assert.Contains("read-only", refused.Message);
            Assert.Throws<NotSupportedException>(() => t.DeleteRow("Id", 1));
            Assert.Throws<NotSupportedException>(() => db.CreateTable("U", new[] { new ColumnDefinition("Id", DataType.Int32) }));
            Assert.Equal(0, db.File.WriteCount);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Where the operating system does not keep to a handle's sharing - anywhere but Windows - a file opened to keep
    // writers out can be written to by another program all the same, so it is read as one opened with ReadOnly alone is:
    // its pages again at every move. The seam stands in for such a system here; CI's Linux jobs run on one.
    [Theory]
    [MemberData(nameof(FormatsAndReadings))]
    public void Where_sharing_is_not_kept_to_a_reading_reads_again_as_one_beside_a_writer_does(FileFormat format, string reading)
    {
        string path = ThreeThousandRows(format);
        long shared = PagesRead(path, ReadOnly, t => Read(t, reading), 3000);
        PageFile.OSEnforcesSharingForTests = false;
        try
        {
            using (var db = Database.Open(path, KeepingWritersOut))
                Assert.True(db.File.SharedWithWriters);
            Assert.Equal(shared, PagesRead(path, KeepingWritersOut, t => Read(t, reading), 3000));
        }
        finally { PageFile.OSEnforcesSharingForTests = null; }
    }

    public static IEnumerable<object[]> FilesOfEveryFormat() => new[]
    {
        new object[] { "Access 2000" }, new object[] { "Access 2010" }, new object[] { "Access 97" },
    };

    private string FileOf(string kind)
    {
        if (kind != "Access 97") return ThreeThousandRows(kind == "Access 2000" ? FileFormat.Access2000 : FileFormat.Access2010);
        string path = NewPath(".mdb");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "access97-jet40.mdb"), path);
        File.SetAttributes(path, FileAttributes.Normal);
        return path;
    }

    // KeepWritersOut keeps writers out of a file opened only to be read: set without ReadOnly it is refused, in every
    // format, before the file is opened - here a file Access holds exclusively, which any handle would be refused -
    // and the file is as it was, and opens.
    [Theory]
    [MemberData(nameof(FilesOfEveryFormat))]
    public void Without_ReadOnly_it_is_refused_before_the_file_is_opened(string kind)
    {
        string path = FileOf(kind);
        byte[] before = File.ReadAllBytes(path);
        using (AsAccessHasItExclusively(path))
        {
            var refused = Assert.Throws<ArgumentException>(() => Database.Open(path, new DatabaseOpenOptions { KeepWritersOut = true }).Dispose());
            Assert.Contains(nameof(DatabaseOpenOptions.KeepWritersOut), refused.Message);
            Assert.Contains(nameof(DatabaseOpenOptions.ReadOnly), refused.Message);
        }
        Assert.Throws<ArgumentException>(() => Database.Open(path + ".none", new DatabaseOpenOptions { KeepWritersOut = true }).Dispose());
        Assert.Equal(before, File.ReadAllBytes(path));
        using (var db = Database.Open(path, KeepingWritersOut))
            Assert.True(db.IsReadOnly);
        using (var db = Database.Open(path))
            Assert.Equal(kind == "Access 97", db.IsReadOnly);
    }

    // Opened so, a file of any format - one marked read-only among them - opens and reads, and is left as it was: no
    // lock file is made, its bytes and its last write time are the same, and nothing is written through the handle.
    [Theory]
    [MemberData(nameof(FilesOfEveryFormat))]
    public void A_file_of_any_format_or_marked_read_only_is_read_and_left_as_it_was(string kind)
    {
        string path = FileOf(kind);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            byte[] before = File.ReadAllBytes(path);
            DateTime written = File.GetLastWriteTimeUtc(path);
            string lockFile = Path.ChangeExtension(path, kind == "Access 2010" ? ".laccdb" : ".ldb");

            using (var db = Database.Open(path, KeepingWritersOut))
            {
                Assert.True(db.IsReadOnly);
                Assert.Equal(PageFile.OSEnforcesSharing, !db.File.SharedWithWriters);
                foreach (string name in db.GetTableNames())
                {
                    var table = db.GetTable(name);
                    Assert.Equal(table.RowCount, table.Rows.Count());
                    Assert.Equal(table.RowCount, table.CreateCursor().Count());
                }
                Assert.False(File.Exists(lockFile));
                Assert.Equal(0, db.File.WriteCount);
            }

            Assert.False(File.Exists(lockFile));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
}
