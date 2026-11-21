using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// How a database shares its file with other programs (#62). Measured with ACE 16 and DAO 16 in an .mdb and an
/// .accdb, and with DAO 3.6 in an Access 97 file: Access asks to write to a database file in every mode but a
/// read-only one - sharing it for reading and writing when it opens a database shared, and not at all when it opens
/// one exclusively - and asks only to read when it opens one read-only, sharing it for reading and writing. A database
/// opened to be written shares the file for reading only, so every open of Access's that would write is refused while
/// it is open, and it is not opened while one has the file; a database opened only to be read
/// (<see cref="DatabaseOpenOptions.ReadOnly"/>) shares it as Access's read-only open does.
/// <para>
/// Here Access is a <see cref="FileStream"/> opened as ACE and DAO open the file in each mode - what their own handles
/// were measured to ask for and to share. FileSharingInAccessTests has ACE and DAO themselves on
/// the other side.
/// </para>
/// </summary>
public sealed class FileSharingTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string NewPath(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sharing_{Guid.NewGuid():N}{extension}");
        _paths.Add(path);
        return path;
    }

    public static IEnumerable<object[]> Formats() => new[] { new object[] { FileFormat.Access2000 }, new object[] { FileFormat.Access2010 } };

    /// <summary>A database of one table, T, of ten rows: Id (the primary key) and V, "row 1" to "row 10".</summary>
    private string NewDatabase(FileFormat format)
    {
        string path = NewPath(format == FileFormat.Access2000 ? ".mdb" : ".accdb");
        using var db = Database.Create(path, format);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("V", DataType.Text) { MaxLength = 50 } }, "Id");
        for (int i = 1; i <= 10; i++) table.Insert(new Row { ["Id"] = i, ["V"] = "row " + i });
        return path;
    }

    private static DatabaseOpenOptions ReadOnly => new() { ReadOnly = true };

    // The file as ACE and DAO hold it, measured from the sharing their own handles allow: a database open in Access,
    // shared (ACE's default mode, DAO's OpenDatabase); open read-only (ACE's Mode=Read, DAO's read-only); open
    // exclusively (ACE's Mode=Share Exclusive, DAO's exclusive).
    private static FileStream AsAccessShares(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
    private static FileStream AsAccessReads(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    private static FileStream AsAccessHasItExclusively(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static readonly int SharingViolation = unchecked((int)0x80070020);

    /// <summary>The refusal of a file in use: its own type, an <see cref="IOException"/> with a sharing violation's HResult.</summary>
    private static DatabaseInUseException AssertInUse(Action open, string path)
    {
        var refused = Assert.Throws<DatabaseInUseException>(open);
        Assert.Contains(path, refused.Message);
        Assert.Equal(SharingViolation, refused.HResult);
        Assert.IsAssignableFrom<IOException>(refused);
        Assert.IsAssignableFrom<IOException>(refused.InnerException);
        return refused;
    }

    // EWOULDBLOCK on Linux: the HResult of the IOException .NET gives where the advisory lock it takes for a handle's
    // sharing is held by another handle (measured with .NET 10, #122).
    private const int LinuxLockHeld = 11;

    /// <summary>
    /// The operating system refusing a program's open of the file, as Windows refuses ACE's and DAO's: a sharing
    /// violation on Windows, and on Linux the advisory lock .NET takes held by another handle.
    /// </summary>
    private static void AssertRefusedByTheSystem(Action open)
    {
        var refused = Assert.ThrowsAny<IOException>(open);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Assert.Equal(SharingViolation, refused.HResult);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) Assert.Equal(LinuxLockHeld, refused.HResult);
    }

    // Whether the operating system keeps to a handle's sharing for reading only, so that such a handle keeps writers
    // out: Windows does. On Linux .NET takes a shared advisory lock for every handle that shares the file at all, so a
    // handle that shares it for reading only lets a writer in, and only one that shares nothing keeps anyone out; the
    // On_Linux_ test pins that. Where it does not, a test asserts only what holds there.
    private static bool SharingIsKeptTo => PageFile.OSEnforcesSharing;

    // ── A database opened to be written ──────────────────────────────────────

    // While a database has the file open to be written, Access cannot open it to write - shared or exclusively - and
    // a second Database is refused; Access with it open read-only, and a Database opened only to be read, can open it.
    // The file was shared for writing, so Access's opens to write were let in and wrote over the pages the database
    // kept track of (two writers lost rows and broke a primary key in the review of #59). On Linux the shared open and
    // the second Database are let in, and only the rest is asserted.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_database_opened_to_be_written_keeps_every_other_writer_out_and_lets_readers_in(FileFormat format)
    {
        string path = NewDatabase(format);
        using (var db = Database.Open(path))
        {
            var table = db.GetTable("T");
            AssertRefusedByTheSystem(() => AsAccessHasItExclusively(path).Dispose());
            if (SharingIsKeptTo)
            {
                AssertRefusedByTheSystem(() => AsAccessShares(path).Dispose());
                AssertInUse(() => Database.Open(path).Dispose(), path);
            }

            using (AsAccessReads(path)) { }
            using (var reader = Database.Open(path, ReadOnly))
            {
                Assert.Equal(10, reader.GetTable("T").Rows.Count());

                // The writer goes on, and the reader reads what has been written.
                table.Insert(new Row { ["Id"] = 11, ["V"] = "row 11" });
                Assert.Equal(11, reader.GetTable("T").Rows.Count());
                Assert.Equal("row 11", reader.GetTable("T").FindRowByPrimaryKey(11)!["V"]);
            }
        }

        // Closed, it is anyone's.
        using (AsAccessShares(path)) { }
        using (var db = Database.Open(path))
            Assert.Equal(11, db.GetTable("T").RowCount);
    }

    // On Linux .NET keeps to a handle's sharing by an advisory lock, shared for every handle that shares the file at
    // all: a database opened to be written lets a second writer in - Access's shared open, and a second Database, which
    // writes - and keeps out only a handle that shares nothing, and Create. Keep to one writer there: the first keeps
    // the pages it has read, and does not know of the second's. Measured with .NET 10 on Linux (#122).
    [Theory]
    [MemberData(nameof(Formats))]
    public void On_Linux_a_second_writer_is_let_in_and_only_a_handle_sharing_nothing_is_kept_out(FileFormat format)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Assert.Skip("What a database opened to be written lets in off Windows was measured on Linux; Windows keeps every other writer out.");
        string path = NewDatabase(format);

        using var db = Database.Open(path);
        using (AsAccessShares(path)) { }
        using (var second = Database.Open(path))
            second.GetTable("T").Insert(new Row { ["Id"] = 11, ["V"] = "row 11" });
        AssertRefusedByTheSystem(() => AsAccessHasItExclusively(path).Dispose());
        AssertInUse(() => Database.Create(path).Dispose(), path);
    }

    // A database just created has the file to itself until it is disposed, as DAO holds a database it creates: writers
    // and readers are kept out, Access in every mode and every Database. Disposed, it is anyone's, and opened again it is
    // shared with readers. It shared the file with readers, through a handle opened after another, sharing nothing, had
    // found the file free and been closed: a reader that opened between the two had the file emptied under it (Codex's
    // review of #73). The handle that empties the file now shares nothing, and Windows checks it before it empties the
    // file (Create_does_not_replace_a_file_someone_has_open), so there is no between.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_database_just_created_has_the_file_to_itself_until_it_is_disposed(FileFormat format)
    {
        string path = NewPath(format == FileFormat.Access2000 ? ".mdb" : ".accdb");
        using (var db = Database.Create(path, format))
        {
            AssertRefusedByTheSystem(() => AsAccessShares(path).Dispose());
            AssertRefusedByTheSystem(() => AsAccessReads(path).Dispose());
            AssertInUse(() => Database.Open(path).Dispose(), path);
            AssertInUse(() => Database.Open(path, ReadOnly).Dispose(), path);
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id");
        }

        using (var db = Database.Open(path))
        using (var reader = Database.Open(path, ReadOnly))
            Assert.Equal(new[] { "T" }, reader.GetTableNames());
    }

    // A file Access has open to write - shared, or exclusively - is not opened to be written, whatever the options
    // say, and is left as it was. It was refused only because its header was read without sharing writing, with a plain
    // IOException.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_file_Access_has_open_to_write_is_not_opened_to_be_written_and_is_left_as_it_was(FileFormat format)
    {
        string path = NewDatabase(format);
        byte[] before = File.ReadAllBytes(path);

        // On Linux Access's shared open does not keep a writer out (the On_Linux_ test); its exclusive one does.
        var holders = SharingIsKeptTo
            ? new Func<string, FileStream>[] { AsAccessShares, AsAccessHasItExclusively }
            : new Func<string, FileStream>[] { AsAccessHasItExclusively };
        foreach (var hold in holders)
        {
            using (hold(path))
            {
                var refused = AssertInUse(() => Database.Open(path).Dispose(), path);
                AssertInUse(() => Database.Open(path, new DatabaseOpenOptions { Password = "not looked at" }).Dispose(), path);
                AssertInUse(() => Database.Open(path, new DatabaseOpenOptions { FallbackFormat = FileFormat.Access2000 }).Dispose(), path);
                if (hold == AsAccessShares)
                    Assert.Contains(nameof(DatabaseOpenOptions.ReadOnly), refused.Message);
            }
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    // Access with the database open read-only shares the file for writing, as a Database opened only to be read does:
    // a writer opens it beside them, as a second user of Access would.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_file_Access_has_open_read_only_is_opened_to_be_written(FileFormat format)
    {
        string path = NewDatabase(format);
        using (AsAccessReads(path))
        using (var db = Database.Open(path))
            db.GetTable("T").Insert(new Row { ["Id"] = 11, ["V"] = "row 11" });

        using var again = Database.Open(path);
        Assert.Equal("row 11", again.GetTable("T").FindRowByPrimaryKey(11)!["V"]);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Holders() => new[]
    {
        new object[] { "Access shares it" }, new object[] { "Access reads it" }, new object[] { "Access has it exclusively" },
        new object[] { "a Database writes to it" }, new object[] { "a Database reads it" },
    };

    private static IDisposable Hold(string holder, string path) => holder switch
    {
        "Access shares it" => AsAccessShares(path),
        "Access reads it" => AsAccessReads(path),
        "Access has it exclusively" => AsAccessHasItExclusively(path),
        "a Database writes to it" => Database.Open(path),
        "a Database reads it" => Database.Open(path, ReadOnly),
        _ => throw new ArgumentException(holder),
    };

    // Create emptied a file Access had open, under it: the file was opened to be replaced sharing writing, and Access
    // shared it for writing. A file is replaced only when no one has it open - not even to read, as no one should go on
    // reading a file that has become another.
    [Theory]
    [MemberData(nameof(Holders))]
    public void Create_does_not_replace_a_file_someone_has_open(string holder)
    {
        string path = NewDatabase(FileFormat.Access2010);
        byte[] before = File.ReadAllBytes(path);

        using (Hold(holder, path))
        {
            var refused = AssertInUse(() => Database.Create(path, FileFormat.Access2000).Dispose(), path);
            Assert.Contains("not replaced", refused.Message);
            AssertInUse(() => Database.Create(path).Dispose(), path);
        }
        Assert.Equal(before, File.ReadAllBytes(path));

        // No one has it now, and it is replaced.
        using (var db = Database.Create(path, FileFormat.Access2000))
            Assert.Empty(db.GetTableNames());
    }

    // ── A database opened only to be read ────────────────────────────────────

    // Opened only to be read, a file opens while Access has it open to write, and reads what Access has written - as
    // Access's own read-only open does. Only a program holding it exclusively keeps it out.
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_file_opened_only_to_be_read_opens_beside_a_writer_but_not_beside_one_holding_it_exclusively(FileFormat format)
    {
        string path = NewDatabase(format);
        byte[] before = File.ReadAllBytes(path);

        using (AsAccessShares(path))
        using (var db = Database.Open(path, ReadOnly))
        {
            Assert.True(db.IsReadOnly);
            Assert.Equal(10, db.GetTable("T").Rows.Count());
            Assert.Equal("row 7", db.GetTable("T").FindRowByPrimaryKey(7)!["V"]);
        }
        using (AsAccessReads(path))
        using (var db = Database.Open(path, ReadOnly))
            Assert.Equal(10, db.GetTable("T").Rows.Count());
        using (AsAccessHasItExclusively(path))
        {
            var refused = AssertInUse(() => Database.Open(path, ReadOnly).Dispose(), path);
            Assert.Contains("exclusively", refused.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // An Access 97 file is only ever read, and opens as a file opened only to be read does: while Access has it open
    // to write. It was refused, as one to be written would be, because its header was read without sharing writing.
    [Fact]
    public void An_Access_97_file_opens_while_Access_has_it_open_to_write()
    {
        string path = NewPath(".mdb");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "access97-jet40.mdb"), path);

        using (AsAccessShares(path))
        using (var db = Database.Open(path))
        {
            Assert.True(db.IsReadOnly);
            Assert.Equal(1500, db.GetTable("Keys").Rows.Count());
            Assert.NotNull(db.GetTable("Keys").FindRowByPrimaryKey(7));
        }
    }
}
