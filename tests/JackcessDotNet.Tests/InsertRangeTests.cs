using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Table.InsertRange"/>: a batch of rows goes in whole, or not at all with the row that stopped it
/// named (#29).
/// </summary>
public sealed class InsertRangeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"insert_range_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Table Create(Database db)
        => db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true },
            new ColumnDefinition("Code", DataType.Text) { MaxLength = 20 },
            new ColumnDefinition("Notes", DataType.Memo),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByCode", "Code") { IsUnique = true } });

    private static List<Row> Batch(int from, int count)
        => Enumerable.Range(from, count).Select(i => new Row { ["Code"] = $"code {i:0000}", ["Notes"] = new string('n', i % 7 * 300) }).ToList();

    [Fact]
    public void A_batch_is_inserted_in_its_order_and_each_row_has_its_number()
    {
        using (var db = Database.Create(_path))
        {
            var table = Create(db);
            var rows = Batch(1, 500);
            table.InsertRange(rows);

            Assert.Equal(Enumerable.Range(1, 500), rows.Select(r => (int)r["Id"]!));
            Assert.Equal(500, table.RowCount);
            Assert.Equal(500, table.LastAutoNumber);

            // An enumerable that is not a list is gone through once.
            int enumerated = 0;
            IEnumerable<Row> More()
            {
                enumerated++;
                foreach (var row in Batch(501, 20)) yield return row;
            }
            table.InsertRange(More());
            Assert.Equal(1, enumerated);

            long writes = db.File.WriteCount;
            table.InsertRange(Array.Empty<Row>());
            Assert.Equal(writes, db.File.WriteCount);
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            Assert.Equal(Enumerable.Range(1, 520).Select(i => $"code {i:0000}"), table.Rows.Select(r => (string)r["Code"]!));
            Assert.Equal(520, table.LastAutoNumber);
            IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 520);
        }
    }

    [Fact]
    public void A_batch_with_a_row_the_table_refuses_leaves_the_file_as_it_was_and_says_which_row()
    {
        using var db = Database.Create(_path);
        var table = Create(db);
        table.InsertRange(Batch(1, 50));
        var before = IndexChecks.AllPages(db);

        // The 301st row has the code of a row already there.
        var rows = Batch(1000, 400);
        rows[300]["Code"] = "code 0007";
        rows[10]["Id"] = 77;                               // an AutoNumber value that is not taken: given back as it came
        var refused = Assert.Throws<BatchInsertException>(() => table.InsertRange(rows));
        Assert.Equal(300, refused.RowIndex);
        Assert.IsType<ConstraintViolationException>(refused.InnerException);
        Assert.Contains("Row 301 of the 400", refused.Message);
        Assert.Contains("ByCode", refused.Message);

        IndexChecks.AssertUnchanged(before, db);
        Assert.Equal(50, table.RowCount);
        Assert.Equal(50, table.LastAutoNumber);
        Assert.All(rows.Where((_, i) => i != 10), r => Assert.False(r.ContainsKey("Id")));
        Assert.Equal(77, rows[10]["Id"]);

        // Two rows of the batch with one code: the second is the one refused.
        rows = Batch(2000, 10);
        rows[6]["Code"] = rows[2]["Code"];
        Assert.Equal(6, Assert.Throws<BatchInsertException>(() => table.InsertRange(rows)).RowIndex);

        // Text longer than its column.
        rows = Batch(3000, 10);
        rows[9]["Code"] = new string('x', 21);
        refused = Assert.Throws<BatchInsertException>(() => table.InsertRange(rows));
        Assert.Equal(9, refused.RowIndex);
        Assert.IsType<ColumnValueTooLongException>(refused.InnerException);
        IndexChecks.AssertUnchanged(before, db);

        // Put right, the batch goes in, numbered from where the table was.
        rows[9]["Code"] = "fits";
        table.InsertRange(rows);
        Assert.Equal(Enumerable.Range(51, 10), rows.Select(r => (int)r["Id"]!));
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 60);
    }

    [Fact]
    public void A_batch_that_fails_while_it_is_written_is_taken_back()
    {
        using var db = Database.Create(_path);
        var table = Create(db);
        table.InsertRange(Batch(1, 50));
        var before = IndexChecks.AllPages(db);

        foreach (int writes in new[] { 0, 1, 7, 40, 150 })
        {
            var rows = Batch(1000, 200);
            db.File.WritesBeforeFailure = writes;
            var failed = Assert.Throws<BatchInsertException>(() => table.InsertRange(rows));
            db.File.WritesBeforeFailure = null;

            Assert.IsType<IOException>(failed.InnerException);
            IndexChecks.AssertUnchanged(before, db);
            Assert.All(rows, r => Assert.False(r.ContainsKey("Id")));
        }

        table.InsertRange(Batch(1000, 200));
        Assert.Equal(250, table.RowCount);
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "T", 250);
    }

    [Fact]
    public void A_batch_keeps_the_relationships_an_insert_keeps()
    {
        using var db = Database.Create(_path);
        var parent = db.CreateTable("Parent", new[] { new ColumnDefinition("Id", DataType.Int32) }, "Id");
        var child  = db.CreateTable("Child", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Parent", DataType.Int32) }, "Id");
        db.CreateRelationship(new RelationshipDefinition("Parent", "Id", "Child", "Parent") { HasReferentialIntegrity = true });
        db.EnforceForeignKeys = true;
        parent.InsertRange(Enumerable.Range(1, 5).Select(i => new Row { ["Id"] = i }));
        var before = IndexChecks.AllPages(db);

        var rows = Enumerable.Range(1, 20).Select(i => new Row { ["Id"] = i, ["Parent"] = i == 15 ? 99 : i % 5 + 1 }).ToList();
        var refused = Assert.Throws<BatchInsertException>(() => child.InsertRange(rows));
        Assert.Equal(14, refused.RowIndex);
        Assert.IsType<ConstraintViolationException>(refused.InnerException);
        IndexChecks.AssertUnchanged(before, db);

        rows[14]["Parent"] = 1;
        child.InsertRange(rows);
        Assert.Equal(20, child.RowCount);
    }

    [Fact]
    public void What_is_not_a_batch_is_refused_before_anything_is_written()
    {
        using var db = Database.Create(_path);
        var table = Create(db);
        var before = IndexChecks.AllPages(db);

        Assert.Throws<ArgumentNullException>(() => table.InsertRange(null!));
        var withNull = Assert.Throws<ArgumentException>(() => table.InsertRange(new[] { new Row { ["Code"] = "a" }, null! }));
        Assert.Equal("rows", withNull.ParamName);
        IndexChecks.AssertUnchanged(before, db);

        // Inside another change that is being kept for undoing, a batch cannot be taken back on its own.
        using (db.File.BeginUndo())
            Assert.Throws<InvalidOperationException>(() => table.InsertRange(Batch(1, 2)));
        IndexChecks.AssertUnchanged(before, db);
    }
}
