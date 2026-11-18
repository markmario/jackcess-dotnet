using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What every index of a table must hold for Access to answer through it: an entry for each row and no other,
/// under the key of the row's values, in a tree that keeps Access's rules and whose pages its usage map lists.
/// Read from the file's pages by <see cref="JetPages"/>, independently of the engine's index code.
/// </summary>
internal static class IndexChecks
{
    /// <summary>The page of <paramref name="table"/>'s definition, from the catalog.</summary>
    public static int TdefOf(Database db, string table) => (int)JetPages.Rows(db.File, JetFormat.PageSystemCatalog)
        .Single(r => r.Values.TryGetValue("Name", out var name) && (string?)name == table
                  && r.Values.TryGetValue("Type", out var type) && Convert.ToInt32(type) == JetFormat.CatalogTypeTable).Values["Id"]!;

    /// <summary>
    /// Asserts that each index tree of <paramref name="tableName"/> holds exactly its rows' entries, and returns
    /// the number of trees checked. An index that ignores nulls has no entry for a row whose columns are all null.
    /// </summary>
    public static int AssertEveryIndexHoldsTheRows(Database db, string tableName, int? expectedRows = null)
    {
        var file  = db.File;
        var table = db.GetTable(tableName);
        int tdef  = TdefOf(db, tableName);
        var rows  = JetPages.Rows(file, tdef, forKeys: true);
        if (expectedRows is { } count) Assert.Equal(count, rows.Count);

        var trees = table.Indexes.GroupBy(ix => ix.IndexDataNumber).Select(g => g.First()).ToList();
        foreach (var index in trees)
        {
            var expected = new List<string>();
            foreach (var (page, row, values) in rows)
            {
                var key = index.Columns.Select(c => values.TryGetValue(c.Column.Name, out var v) ? v : null).ToArray();
                if (index.IgnoresNulls && key.All(v => v is null)) continue;
                expected.Add($"{Convert.ToHexString(IndexKeys.Encode(index.Columns, key))} -> {page}/{row}");
            }
            var actual = JetPages.LeafEntries(file, index.RootPageNumber).Select(e => $"{e.Hex} -> {e.RowPage}/{e.Row}").ToList();
            expected.Sort(StringComparer.Ordinal);
            var sortedActual = actual.OrderBy(e => e, StringComparer.Ordinal).ToList();
            Assert.True(expected.SequenceEqual(sortedActual),
                $"{tableName}.{index.Name}: {expected.Count} rows' entries expected, {actual.Count} found; " +
                $"missing [{string.Join("; ", expected.Except(sortedActual).Take(3))}], " +
                $"not a row's [{string.Join("; ", sortedActual.Except(expected).Take(3))}]");

            Assert.Empty(JetPages.RuleBreaks(file, index.RootPageNumber, tdef));

            // The index's usage map lists exactly its tree's pages.
            var block = JetPages.IndexBlocks(file, tdef)[index.IndexDataNumber];
            Assert.Equal(index.RootPageNumber, block.Root);
            Assert.Equal(JetPages.TreePages(file, block.Root).Select(p => p.Number).OrderBy(p => p),
                         UsageMap.GetOwnedPages(file.ReadPage(block.UmapPage), block.UmapRow, file.Format, file).OrderBy(p => p));
        }
        return trees.Count;
    }

    /// <summary>Every page of the file, to show that a refused change changed nothing.</summary>
    public static List<byte[]> AllPages(Database db)
        => Enumerable.Range(0, db.File.PageCount).Select(db.File.ReadPage).ToList();

    public static void AssertUnchanged(List<byte[]> before, Database db)
    {
        Assert.Equal(before.Count, db.File.PageCount);
        for (int i = 0; i < before.Count; i++)
            Assert.True(before[i].AsSpan().SequenceEqual(db.File.ReadPage(i)), $"page {i} changed");
    }

    /// <summary>The height of the tree rooted at <paramref name="root"/>: 0 for a single leaf.</summary>
    public static int Height(Database db, int root) => JetPages.ReadIndexPage(db.File, root).Level;
}
