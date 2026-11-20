using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The 2 GB an Access file can be (#82). ACE reads a longer file and cannot write to it (measured, ACE 16: an insert
/// into a file of 2,160,779,264 bytes the engine wrote failed as "Cannot open database ''... may be corrupt"; one into
/// 2,140,618,752 bytes did not), so every write that would take the file past the limit is refused with
/// <see cref="DatabaseFullException"/>, whatever writes it, and the change is taken back; and a file already past it is
/// read whole, every write to it refused, the file left as it is. The limit is lowered here to the file's own length,
/// so that the next page any change adds is past it; ExportMdb.Interop's ExportSizeLimitTests write real 2 GB files.
/// </summary>
public sealed class FileSizeLimitTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"size_limit_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // The file's bytes, read beside the Database that has it open to write.
    private byte[] Bytes()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        int read = 0;
        while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
        return bytes;
    }

    private static Row RowOf(int i, int note = 10) => new() { ["Id"] = i, ["Code"] = $"code {i:D6}", ["Note"] = new string('n', note) };

    // A table T with a primary key, a unique index on Code and a memo, and 20 rows on one page.
    private void Create()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("Code", DataType.Text) { MaxLength = 40 },
            new ColumnDefinition("Note", DataType.Memo),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") { IsUnique = true } });
        for (int i = 1; i <= 20; i++) table.Insert(RowOf(i));
    }

    private void AssertRefusedAndUnchanged(Action<Database> change, long? limit = null)
    {
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path))
        {
            db.File.MaxLength = limit ?? before.Length;
            var refused = Record.Exception(() => change(db));
            Assert.NotNull(refused);
            var full = refused as DatabaseFullException ?? refused.InnerException as DatabaseFullException;
            Assert.True(full is not null, $"refused with {refused.GetType().Name}: {refused.Message}");
        }
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused change changed the file");
    }

    [Fact]
    public void Every_write_that_would_grow_the_file_past_the_limit_is_refused_and_taken_back()
    {
        Create();
        // Rows one by one: those that fit the table's page go in, and the one that needs a page is refused, its own
        // change taken back.
        using (var db = Database.Open(_path))
        {
            db.File.MaxLength = new FileInfo(_path).Length;
            for (int i = 100; ; i++)
            {
                Assert.True(i < 10_000, "no row was refused");
                byte[] before = Bytes();
                var refused = Record.Exception(() => db.GetTable("T").Insert(RowOf(i)));
                if (refused is null) continue;
                Assert.IsType<DatabaseFullException>(refused);
                Assert.True(before.AsSpan().SequenceEqual(Bytes()), $"the refused row {i} changed the file");
                Assert.Null(db.GetTable("T").FindRowByPrimaryKey(i));
                break;
            }
        }
        // A long value that needs a long-value page.
        AssertRefusedAndUnchanged(db => db.GetTable("T").Insert(RowOf(30, note: 20_000)));
        // An update that makes a row's long value need a page.
        AssertRefusedAndUnchanged(db =>
        {
            var row = db.GetTable("T").FindRowByPrimaryKey(3)!;
            row["Note"] = new string('u', 20_000);
            db.GetTable("T").Update(row);
        });
        // A batch, a load, an index, a table.
        AssertRefusedAndUnchanged(db => db.GetTable("T").InsertRange(Enumerable.Range(20_000, 2_000).Select(i => RowOf(i))));
        AssertRefusedAndUnchanged(db => db.GetTable("T").BulkInsert(Enumerable.Range(20_000, 2_000).Select(i => RowOf(i))));
        AssertRefusedAndUnchanged(db => db.GetTable("T").AddIndex(new IndexDefinition("ByNote", "Code", "Id")));
        AssertRefusedAndUnchanged(db => db.CreateTable("U", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id"));
    }

    [Fact]
    public void A_file_already_past_the_limit_is_read_whole_and_every_write_to_it_refused()
    {
        Create();
        long length = new FileInfo(_path).Length;
        long limit = length - 4096;   // the file is a page past it, as one an earlier version wrote past 2 GB

        using (var db = Database.Open(_path))
        {
            db.File.MaxLength = limit;
            var rows = db.GetTable("T").Rows.ToList();
            Assert.Equal(20, rows.Count);
            Assert.Equal("code 000007", db.GetTable("T").CreateIndexCursor("ByCode").FindRow("Code", "code 000007")!["Code"]);
        }

        // Writes that would fit the file's pages are refused too: Access does not write to such a file.
        AssertRefusedAndUnchanged(db => db.GetTable("T").Insert(RowOf(21)), limit);
        AssertRefusedAndUnchanged(db =>
        {
            var row = db.GetTable("T").FindRowByPrimaryKey(2)!;
            row["Code"] = "changed";
            db.GetTable("T").Update(row);
        }, limit);
        AssertRefusedAndUnchanged(db => db.GetTable("T").Delete(db.GetTable("T").FindRowByPrimaryKey(4)!), limit);
        AssertRefusedAndUnchanged(db => db.GetTable("T").AddColumn(new ColumnDefinition("Extra", DataType.Int32)), limit);
        AssertRefusedAndUnchanged(db => db.GetTable("T").DropIndex("ByCode"), limit);
    }
}
