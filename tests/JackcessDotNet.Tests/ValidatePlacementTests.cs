using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Table.Validate"/> refuses a row exactly when <see cref="Table.Insert"/> does where the answer depends on
/// where a long value goes - in the row, in one long-value row, or a chain - and on whether the row fits its page
/// (#141); and it writes nothing. Memo values either side of each boundary, beginning with U+FEFF and not, of
/// characters that compress and of characters that do not: in a table this library creates in an .mdb and an .accdb,
/// in Memo columns with Unicode compression and without in a table Access made (Assets/ace-text-marker.mdb), and in
/// a Memo column with no pages of its own (a table the 1.2.2 package created, Assets/engine-1.2.2-created-table.mdb).
/// </summary>
public sealed class ValidatePlacementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"validate_placement_{Guid.NewGuid():N}");

    public ValidatePlacementTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Lengths after the mark either side of 64 bytes in the row, of 1,908 characters compressed and 3,816 bytes in one
    // long-value row, and of the most one long-value row holds.
    private static readonly int[] Lengths =
        { 1, 3, 4, 5, 30, 31, 32, 33, 60, 200, 1000, 1906, 1907, 1908, 1909, 2037, 2038, 2039, 3000, 4000 };

    private static IEnumerable<string> Values()
    {
        foreach (string mark in new[] { "", "﻿" })
        foreach (char c in new[] { 'x', 'м' })
        foreach (int length in Lengths)
            yield return mark + new string(c, length);
    }

    private static string Shown(string text)
        => (text.StartsWith("﻿", StringComparison.Ordinal) ? "U+FEFF and " : "") + text.TrimStart('﻿').Length
           + " of U+" + ((int)text[text.Length - 1]).ToString("X4");

    private string Copy(string asset)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + Path.GetExtension(asset));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), path);
        return path;
    }

    private string Created(FileFormat format)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + (format == FileFormat.Access2000 ? ".mdb" : ".accdb"));
        using var db = Database.Create(path, format);
        db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("M", DataType.Memo) });
        return path;
    }

    private static string? Refusal(Action work)
    {
        try { work(); return null; }
        catch (Exception e) { return e.GetType().Name; }
    }

    /// <summary>
    /// Validates each row in one session, which leaves the file byte for byte as it was; then inserts each, and holds
    /// what <see cref="Table.Validate"/> said of each row to what <see cref="Table.Insert"/> did.
    /// </summary>
    private static void AssertValidateAnswersAsInsert(string path, string table, IReadOnlyList<(string What, Func<Row> Row)> rows)
    {
        byte[] before = File.ReadAllBytes(path);
        var validated = new List<string?>();
        using (var db = Database.Open(path))
        {
            var t = db.GetTable(table);
            foreach (var (_, row) in rows) validated.Add(Refusal(() => t.Validate(row())));
        }
        Assert.Equal(before, File.ReadAllBytes(path));

        var inserted = new List<string?>();
        using (var db = Database.Open(path))
        {
            var t = db.GetTable(table);
            foreach (var (_, row) in rows) inserted.Add(Refusal(() => t.Insert(row())));
        }

        var differ = Enumerable.Range(0, rows.Count).Where(i => validated[i] != inserted[i])
            .Select(i => $"{rows[i].What}: Validate {validated[i] ?? "accepts"}, Insert {inserted[i] ?? "accepts"}").ToList();
        Assert.True(differ.Count == 0, $"{differ.Count} of {rows.Count} rows:\n" + string.Join("\n", differ.Take(20)));
        // The sweep reaches both answers.
        Assert.Contains(inserted, refusal => refusal is null);
    }

    private static List<(string, Func<Row>)> MemoRows(string memo, int firstId)
        => Values().Select((text, i) => (Shown(text), (Func<Row>)(() => new Row { ["Id"] = firstId + i, [memo] = text }))).ToList();

    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2010)]
    public void In_a_table_this_library_creates(FileFormat format)
        => AssertValidateAnswersAsInsert(Created(format), "T", MemoRows("M", 1));

    [Theory]
    [InlineData("MC")]   // with Unicode compression
    [InlineData("MU")]   // without
    public void In_a_table_Access_made(string memo)
        => AssertValidateAnswersAsInsert(Copy("ace-text-marker.mdb"), "T", MemoRows(memo, 1));

    /// <summary>A Memo column with no pages of its own keeps every value in the row, and refuses a row too large for its page.</summary>
    [Fact]
    public void In_a_memo_column_with_no_pages_of_its_own()
        => AssertValidateAnswersAsInsert(Copy("engine-1.2.2-created-table.mdb"), "People", MemoRows("Notes", 100));

    /// <summary>A row of ten Text values too large for its page is refused by both, and one that fits by neither.</summary>
    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2010)]
    public void A_row_too_large_for_its_page(FileFormat format)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + (format == FileFormat.Access2000 ? ".mdb" : ".accdb"));
        using (var db = Database.Create(path, format))
            db.CreateTable("W", new[] { new ColumnDefinition("Id", DataType.Int32) }
                .Concat(Enumerable.Range(0, 10).Select(i => new ColumnDefinition("T" + i, DataType.Text) { MaxLength = 255 })).ToArray());

        var rows = new List<(string, Func<Row>)>();
        int id = 1;
        foreach (char c in new[] { 'x', 'м' })
        foreach (int columns in new[] { 5, 7, 8, 10 })
        {
            int rowId = id++;
            rows.Add(($"{columns} of 255 U+{(int)c:X4}", () =>
            {
                var row = new Row { ["Id"] = rowId };
                for (int i = 0; i < columns; i++) row["T" + i] = new string(c, 255);
                return row;
            }));
        }
        AssertValidateAnswersAsInsert(path, "W", rows);
    }
}
