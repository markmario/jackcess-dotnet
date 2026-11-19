using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class PropertyMapTests
{
    private readonly ITestOutputHelper _output;
    public PropertyMapTests(ITestOutputHelper output) => _output = output;

    // Corpus lives in-repo under tests/corpus; TestCorpus is the one resolver.
    private static string? CorpusRoot() => TestCorpus.Root;

    [Fact]
    public void PropertyMapReader_DecodesHandCraftedBlob()
    {
        // Minimal property blob: signature "MR2\0", one-entry name list ("Desc"),
        // one default-map value list with Desc = "Hi" (Text).
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { (byte)'M', (byte)'R', (byte)'2', 0x00 });

        var nameBlock = new List<byte>();
        nameBlock.Add(0x08); nameBlock.Add(0x00);
        nameBlock.AddRange(System.Text.Encoding.Unicode.GetBytes("Desc"));
        bytes.AddRange(BitConverter.GetBytes(4 + 2 + nameBlock.Count));
        bytes.AddRange(BitConverter.GetBytes((short)0x0080));
        bytes.AddRange(nameBlock);

        var valueBlock = new List<byte>();
        valueBlock.AddRange(BitConverter.GetBytes(4));
        byte[] hi = System.Text.Encoding.Unicode.GetBytes("Hi");
        valueBlock.AddRange(BitConverter.GetBytes((short)(2 + 1 + 1 + 2 + 2 + hi.Length)));
        valueBlock.Add(0x00);
        valueBlock.Add(0x0A);   // Text
        valueBlock.AddRange(BitConverter.GetBytes((short)0));
        valueBlock.AddRange(BitConverter.GetBytes((short)hi.Length));
        valueBlock.AddRange(hi);
        bytes.AddRange(BitConverter.GetBytes(4 + 2 + valueBlock.Count));
        bytes.AddRange(BitConverter.GetBytes((short)0x0000));
        bytes.AddRange(valueBlock);

        var maps = PropertyMapReader.Read(bytes.ToArray(), JetFormat.Jet4);
        Assert.False(maps.IsEmpty);
        Assert.Equal("Hi", maps.Default["Desc"]);
    }

    [Fact]
    public void Properties_RoundTripFromCorpus_DecodeTableAndColumnProperties()
    {
        // IndexPropertiesV2003.mdb's TableIgnoreNulls1 has property maps for the
        // table itself and for the "row" and "data" columns.
        string? root = CorpusRoot();
        if (root is null) return;
        string path = Path.Combine(root, "V2003", "IndexPropertiesV2003.mdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path);
        var t = db.GetTable("TableIgnoreNulls1");
        var tableProps = t.Properties;
        Assert.NotNull(tableProps);
        Assert.Equal("", tableProps.Name);
        Assert.True(tableProps.Count > 0, "table-level map should expose properties");
        Assert.Equal(tableProps.Count, tableProps.Count());

        // Check at least one well-known per-column property is decoded.
        var rowMap = t.Columns.Single(c => c.Name == "row").Properties;
        Assert.Equal("row", rowMap.Name);
        Assert.NotNull(rowMap["AllowZeroLength"]);
        Assert.True(rowMap.TryGetValue("allowzerolength", out var allow));
        Assert.Equal(rowMap["AllowZeroLength"], allow);
        Assert.Contains(rowMap, p => p.Name == "AllowZeroLength" && Equals(p.Value, allow));

        // A property that is not there, and a column Access keeps none for.
        Assert.False(rowMap.TryGetValue("NoSuchProperty", out var none));
        Assert.Null(none);
        Assert.Null(rowMap["NoSuchProperty"]);
        Assert.Same(rowMap, t.Columns.Single(c => c.Name == "row").Properties);
    }

    [Fact]
    public void Properties_FreshTable_AreItsColumnsOwn()
    {
        string path = Path.Combine(Path.GetTempPath(), $"prop_{Guid.NewGuid():N}.mdb");
        try
        {
            using var db = Database.Create(path, FileFormat.Access2000);
            var t = db.CreateTable("Foo", new[] { new ColumnDefinition("X", DataType.Int32) });
            Assert.Empty(t.Properties);
            // A column is made with the Required property Access keeps for a field (#41), and nothing else.
            var column = t.Columns.Single().Properties;
            var required = Assert.Single(column);
            Assert.Equal(("Required", (object)false), (required.Name, required.Value));
            Assert.Equal("X", column.Name);
            Assert.Same(column, t.Columns.Single().Properties);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Properties_DumpForObservability()
    {
        // Loose observability check — print what we find across many corpus tables.
        string? root = CorpusRoot();
        if (root is null) return;
        string path = Path.Combine(root, "V2003", "IndexPropertiesV2003.mdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path);
        foreach (var name in db.GetTableNames())
        {
            var t = db.GetTable(name);
            var maps = new[] { t.Properties }.Concat(t.Columns.Select(c => c.Properties)).Where(map => map.Count > 0).ToList();
            if (maps.Count == 0) continue;
            _output.WriteLine($"=== {name} ===");
            foreach (var map in maps)
            {
                _output.WriteLine($"  [{(string.IsNullOrEmpty(map.Name) ? "<table>" : map.Name)}] " +
                                  $"{map.Count} prop(s)");
                foreach (var p in map)
                    _output.WriteLine($"    {p.Name} ({p.DataType}) = {(p.Value is byte[] b ? $"byte[{b.Length}]" : p.Value)}");
            }
        }
    }
}
