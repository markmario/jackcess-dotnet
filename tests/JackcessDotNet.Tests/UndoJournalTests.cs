using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What a change notes in memory of the file's pages, as it is taken back (#61): a change that fails at its first
/// write takes back what it noted before that write, as one that fails later does; and what it learnt of the file
/// before it wrote anything is still true, and is kept.
/// </summary>
public sealed class UndoJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"undo_journal_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // A Memo of 5,000 characters is a chain of two chunks, each on a page of its own (Access's placement).
    private static readonly string Chain = new('c', 5_000);

    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2010)]
    public void An_insert_whose_first_write_fails_leaves_the_pages_a_delete_emptied_to_the_next_long_value(FileFormat format)
    {
        using var db = Database.Create(_path, format);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) }, "Id");
        for (int i = 1; i <= 5; i++) table.Insert(new Row { ["Id"] = i, ["Notes"] = Chain });
        foreach (var row in table.Rows.ToList()) table.Delete(row);   // ten pages emptied, noted for the column's next chains
        long length = db.File.Length;

        // The insert takes two of the emptied pages for its chain, and its first write - the chain's first chunk -
        // fails. Nothing is written, and the two pages are what they were: empty, and noted as empty.
        db.File.WritesBeforeFailure = 0;
        Assert.Throws<IOException>(() => table.Insert(new Row { ["Id"] = 6, ["Notes"] = Chain }));
        db.File.WritesBeforeFailure = null;
        Assert.Equal(length, db.File.Length);

        // So five chains go where the five deleted were, and the file does not grow: before #61 the two pages were
        // forgotten for the rest of the session, and two new ones were added.
        for (int i = 6; i <= 10; i++) table.Insert(new Row { ["Id"] = i, ["Notes"] = Chain });
        Assert.Equal(length, db.File.Length);
        Assert.Equal(Enumerable.Repeat(Chain, 5), table.Rows.Select(r => (string)r["Notes"]!));
    }

    [Fact]
    public void A_failure_at_the_first_write_takes_back_what_the_change_noted_and_a_later_one_too()
    {
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Notes", DataType.Memo) }, "Id");
        for (int i = 1; i <= 5; i++) table.Insert(new Row { ["Id"] = i, ["Notes"] = Chain });
        foreach (var row in table.Rows.ToList()) table.Delete(row);
        long length = db.File.Length;

        // Every write of the insert fails in turn: none leaves a page forgotten, whichever it is.
        int writes = 0;
        while (true)
        {
            db.File.WritesBeforeFailure = writes;
            try
            {
                table.Insert(new Row { ["Id"] = 6, ["Notes"] = Chain });
                break;
            }
            catch (IOException)
            {
                writes++;
            }
            finally
            {
                db.File.WritesBeforeFailure = null;
            }
            Assert.Equal(length, db.File.Length);
            Assert.Empty(table.Rows);
        }
        Assert.True(writes >= 3, $"the insert failed at {writes} writes: its chain alone is two");

        for (int i = 7; i <= 10; i++) table.Insert(new Row { ["Id"] = i, ["Notes"] = Chain });
        Assert.Equal(length, db.File.Length);
    }

    [Fact]
    public void A_change_refused_before_it_writes_keeps_what_verifying_the_index_found()
    {
        using (var db = Database.Create(_path))
        {
            var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 } }, "Id");
            table.InsertRange(Enumerable.Range(1, 3_000).Select(i => new Row { ["Id"] = i, ["Name"] = $"name {i}" }));
        }

        using (var db = Database.Open(_path))
        {
            db.VerifyIndexes = true;
            var table = db.GetTable("T");

            // The first change reads the table to verify its primary key, and is then refused as a duplicate,
            // before it writes anything: what it found is of the file as it is, and is kept...
            long before = db.File.ReadCount;
            Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Id"] = 1, ["Name"] = "again" }));
            long verifying = db.File.ReadCount - before;
            Assert.True(verifying > 30, $"the first change read {verifying} pages: it reads the table's");

            // ...so the next change does not read the table again.
            before = db.File.ReadCount;
            table.Insert(new Row { ["Id"] = 3_001, ["Name"] = "new" });
            long next = db.File.ReadCount - before;
            Assert.True(next < 20, $"the change after a refused one read {next} pages, where verifying the index read {verifying}");
        }
    }
}
