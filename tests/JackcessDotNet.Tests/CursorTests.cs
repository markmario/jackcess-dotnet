using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class CursorTests : IDisposable
{
    private readonly string _path;

    public CursorTests()
        => _path = Path.Combine(Path.GetTempPath(), $"cursor_test_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static IReadOnlyList<ColumnDefinition> PeopleColumns() => new[]
    {
        new ColumnDefinition("Id",   DataType.Int32),
        new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
        new ColumnDefinition("Age",  DataType.Int16 ),
    };

    /// <summary>
    /// Creates a fresh db, builds "People" with 5 rows, returns both the live Database
    /// (caller must dispose) and the original Table reference (which retains in-memory
    /// state like PrimaryKeyColumnName that isn't yet persisted in the TDEF).
    /// </summary>
    private (Database db, Table table) OpenWithPeople(string? primaryKey = null)
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var t  = primaryKey is null
            ? db.CreateTable("People", PeopleColumns())
            : db.CreateTable("People", PeopleColumns(), primaryKey);
        for (int i = 1; i <= 5; i++)
            t.Insert(new Row { ["Id"] = i, ["Name"] = $"P{i}", ["Age"] = (short)(20 + i) });
        return (db, t);
    }

    [Fact]
    public void Cursor_IteratesAllRowsInOrder()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.CreateCursor();

        var ids = new List<int>();
        foreach (var row in c)
            ids.Add(Convert.ToInt32(row["Id"]));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    [Fact]
    public void Cursor_MoveNext_IsFalseAfterLast()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.CreateCursor();

        for (int i = 0; i < 5; i++) Assert.True(c.MoveNext());
        Assert.False(c.MoveNext());
        Assert.False(c.MoveNext());   // idempotent
    }

    [Fact]
    public void Cursor_MovePrevious_WalksBackwards()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.CreateCursor();

        c.MoveAfterLast();
        var ids = new List<int>();
        while (c.MovePrevious())
            ids.Add(Convert.ToInt32(c.Current!["Id"]));

        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, ids);
    }

    [Fact]
    public void Cursor_MoveBeforeFirst_ResetsIteration()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.CreateCursor();

        Assert.True(c.MoveNext());
        Assert.True(c.MoveNext());
        c.MoveBeforeFirst();
        Assert.True(c.MoveNext());
        Assert.Equal(1, Convert.ToInt32(c.Current!["Id"]));
    }

    [Fact]
    public void IndexCursor_FindRow_ByPrimaryKey()
    {
        var (db, t) = OpenWithPeople(primaryKey: "Id");
        using var _ = db;
        var c = t.CreateIndexCursor();

        Row? r = c.Table.FindRowByPrimaryKey(3);

        Assert.NotNull(r);
        Assert.Equal("P3", r!["Name"]);
    }

    [Fact]
    public void IndexCursor_FindRow_ByArbitraryColumn_FallsBackToScan()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.CreateCursor();

        Row? r = c.FindRow("Name", "P4");

        Assert.NotNull(r);
        Assert.Equal(4, Convert.ToInt32(r!["Id"]));
    }

    [Fact]
    public void IndexCursor_FindRow_NoMatch_ReturnsNull()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.CreateCursor();

        Assert.Null(c.FindRow("Name", "DoesNotExist"));
    }

    [Fact]
    public void Table_makes_its_cursors_and_finds_a_row_by_its_primary_key()
    {
        var (db, t) = OpenWithPeople(primaryKey: "Id");
        using var _ = db;

        var cursor = t.CreateCursor();
        Assert.Same(t, cursor.Table);
        Assert.Equal(5, System.Linq.Enumerable.Count(cursor));

        var pkCursor = t.CreateIndexCursor();
        Assert.True(pkCursor.Index!.IsPrimaryKey);
        Assert.Equal("P2", pkCursor.FindRow("Id", 2)!["Name"]);

        var found = t.FindRowByPrimaryKey(5);
        Assert.Equal("P5", found!["Name"]);
        Assert.Null(t.FindRowByPrimaryKey(6));
    }

    [Fact]
    public void Cursor_SkipsDeletedRows()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        t.DeleteRow("Id", 3);

        var ids = new List<int>();
        foreach (var row in t.CreateCursor())
            ids.Add(Convert.ToInt32(row["Id"]));

        Assert.Equal(new[] { 1, 2, 4, 5 }, ids);
    }

    // ── Corpus-driven cursor smoke test (V2000 + V2003) ───────────────────────

    public static IEnumerable<object[]> CorpusFiles()
    {
        foreach (string file in TestCorpus.Files("common1*.mdb", "V2000", "V2003"))
            yield return new object[] { Path.GetFileName(Path.GetDirectoryName(file)!),
                                        Path.GetFileName(file), file };
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Cursor_OnCorpusFile_IteratesAllRows(string version, string filename, string path)
    {
        using var db = Database.Open(path);
        foreach (var name in db.GetTableNames())
        {
            var t = db.GetTable(name);
            var viaScan   = t.Rows.ToList();
            var viaCursor = new List<Row>();
            foreach (var r in t.CreateCursor()) viaCursor.Add(r);

            Assert.True(viaScan.Count == viaCursor.Count,
                $"{version}/{filename} table '{name}': scan={viaScan.Count} cursor={viaCursor.Count}");
        }
    }
}
