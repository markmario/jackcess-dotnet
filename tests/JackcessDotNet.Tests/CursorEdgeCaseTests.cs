using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Edge-case behaviour of <see cref="Cursor"/> at iteration boundaries and on
/// empty tables. Companion to <see cref="CursorTests"/> which covers the
/// happy-path forward/backward walks.
/// </summary>
public sealed class CursorEdgeCaseTests : IDisposable
{
    private readonly string _path;
    public CursorEdgeCaseTests()
        => _path = Path.Combine(Path.GetTempPath(), $"cursoredge_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private (Database, Table) NewEmptyTable()
    {
        var db = Database.Create(_path, FileFormat.Access2000);
        var t = db.CreateTable("People", new[]
        {
            new ColumnDefinition("Id",   DataType.Int32),
            new ColumnDefinition("Name", DataType.Text) { MaxLength = 20 },
        });
        return (db, t);
    }

    [Fact]
    public void EmptyTable_MoveNext_False()
    {
        using var db = NewEmptyTable().Item1;
        var c = db.GetTable("People").CreateCursor();
        Assert.False(c.MoveNext());
    }

    [Fact]
    public void EmptyTable_Enumeration_YieldsNothing()
    {
        using var db = NewEmptyTable().Item1;
        var c = db.GetTable("People").CreateCursor();
        Assert.Empty(c);
    }

    [Fact]
    public void Current_BeforeIteration_IsNull()
    {
        using var db = NewEmptyTable().Item1;
        var c = db.GetTable("People").CreateCursor();
        Assert.Null(c.Current);
    }

    [Fact]
    public void Current_AfterMoveNext_IsTheRowMovedTo()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        t.Insert(new Row { ["Id"] = 1, ["Name"] = "A" });
        t.Insert(new Row { ["Id"] = 2, ["Name"] = "B" });

        var c = t.CreateCursor();
        Assert.True(c.MoveNext());
        var first = c.Current;
        Assert.Equal(1, first!["Id"]);
        Assert.Same(first, c.Current);

        Assert.True(c.MoveNext());
        var second = c.Current;
        Assert.Equal(2, second!["Id"]);
        Assert.Same(second, c.Current);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Current_AfterIterationEnds_IsNull()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        t.Insert(new Row { ["Id"] = 1, ["Name"] = "A" });

        var c = t.CreateCursor();
        Assert.True(c.MoveNext());
        Assert.False(c.MoveNext());
        Assert.Null(c.Current);
    }

    [Fact]
    public void MoveAfterLast_ThenMovePrevious_WalksBackward()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        for (int i = 1; i <= 3; i++)
            t.Insert(new Row { ["Id"] = i, ["Name"] = $"P{i}" });

        var c = t.CreateCursor();
        c.MoveAfterLast();
        Assert.True(c.MovePrevious());
        var r3 = c.Current;
        Assert.True(c.MovePrevious());
        var r2 = c.Current;
        Assert.True(c.MovePrevious());
        var r1 = c.Current;
        Assert.False(c.MovePrevious());
        var nada = c.Current;

        Assert.Equal(3, Convert.ToInt32(r3!["Id"]));
        Assert.Equal(2, Convert.ToInt32(r2!["Id"]));
        Assert.Equal(1, Convert.ToInt32(r1!["Id"]));
        Assert.Null(nada);
    }

    [Fact]
    public void Enumeration_ResetsToBeforeFirst_EachCall()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        for (int i = 1; i <= 3; i++)
            t.Insert(new Row { ["Id"] = i, ["Name"] = $"P{i}" });

        var c = t.CreateCursor();
        Assert.Equal(3, c.Count());
        // Foreach again — should re-iterate from the start.
        Assert.Equal(3, c.Count());
    }

    [Fact]
    public void Table_PropertyAccessors_AreImmutableAfterCreate()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        Assert.Equal("People", t.Name);
        Assert.Equal(2, t.Columns.Count);
        Assert.NotNull(t.Indexes);   // not null, just empty (no PK was configured)
    }

    [Fact]
    public void IndexCursor_FindRow_EmptyTable_ReturnsNull()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        var c = t.CreateCursor();
        Assert.Null(c.FindRow("Name", "Nobody"));
    }

    [Fact]
    public void IndexCursor_FindRowByEntry_NoArgs_Throws()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        var keyed = db.CreateTable("Keyed", new[] { new ColumnDefinition("Id", DataType.Int32) }, primaryKey: "Id");
        Assert.Throws<ArgumentException>(() => keyed.CreateIndexCursor().FindRowByEntry());

        // The table with no primary key has no index to make the cursor for.
        Assert.Throws<InvalidOperationException>(() => t.CreateIndexCursor());
    }
}
