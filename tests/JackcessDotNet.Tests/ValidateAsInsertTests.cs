using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Table.Validate"/> refuses a row exactly when <see cref="Table.Insert"/> does, with the same exception,
/// and writes nothing (#141, #143).
/// <list type="bullet">
/// <item>Where the answer depends on where a long value goes - in the row, in one long-value row, or a chain - and on
/// whether the row fits its page: Memo values either side of each boundary, beginning with U+FEFF and not, of
/// characters that compress and of characters that do not, in a table this library creates in an .mdb and an
/// .accdb, in Memo columns with Unicode compression and without in a table Access made
/// (Assets/ace-text-marker.mdb), and in a Memo column with no pages of its own (a table the 1.2.2 package created,
/// Assets/engine-1.2.2-created-table.mdb).</item>
/// <item>Rows made at random, of every type, some with one fault and some with two, some breaking a relationship:
/// in tables made here and in files earlier versions of this library and Access wrote.</item>
/// </list>
/// </summary>
public sealed class ValidateAsInsertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"validate_as_insert_{Guid.NewGuid():N}");

    public ValidateAsInsertTests() => Directory.CreateDirectory(_dir);

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
        foreach (string mark in new[] { "", "\uFEFF" })
        foreach (char c in new[] { 'x', '\u043C' })
        foreach (int length in Lengths)
            yield return mark + new string(c, length);
    }

    private static string Shown(string text)
        => (text.StartsWith("\uFEFF", StringComparison.Ordinal) ? "U+FEFF and " : "") + text.TrimStart('\uFEFF').Length
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
        foreach (char c in new[] { 'x', '\u043C' })
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

    // ── Rows made at random ─────────────────────────────────────────────────

    // A value a column holds, made at random. Int32 values are small, so that keys meet and foreign keys refer to rows
    // that are there and to rows that are not.
    private static object? Valid(Column column, Random random) => column.DataType switch
    {
        DataType.Boolean  => random.Next(2) == 0,
        DataType.Byte     => (byte)random.Next(256),
        DataType.Int16    => (short)random.Next(-1000, 1000),
        DataType.Int32    => random.Next(0, 12),
        DataType.Int64    => (long)random.Next(),
        DataType.Currency => random.Next(-100_000, 100_000) / 100m,
        DataType.Single   => (float)(random.NextDouble() * 1000),
        DataType.Double   => random.NextDouble() * 1e6,
        DataType.DateTime => new DateTime(1990, 1, 1).AddMinutes(random.Next(0, 20_000_000)),
        DataType.Guid     => new Guid(Bytes(random, 16)),
        DataType.Text     => Letters(random, random.Next(0, Math.Min(column.MaxLength, 30) + 1)),
        DataType.Memo     => (random.Next(4) == 0 ? "\uFEFF" : "") + Letters(random, random.Next(0, 4000)),
        DataType.OleObject => Bytes(random, random.Next(0, 300)),
        DataType.Binary   => Bytes(random, random.Next(0, Math.Max(1, column.LengthInBytes) + 1)),
        DataType.Decimal  => random.Next(0, 100),
        _ => null,
    };

    // A value an insert refuses for the column, or null where there is none to make: null is a fault itself in a
    // required column or a key.
    private static object? Fault(Column column, Random random) => column.DataType switch
    {
        DataType.Byte     => 256,
        DataType.Int16    => 32768,
        DataType.Int32    => long.MaxValue,
        DataType.Int64    => "not a number",
        DataType.Currency => double.NaN,
        DataType.Single or DataType.Double => "not a number",
        DataType.DateTime => "notadate",
        DataType.Guid     => 5,
        DataType.Text     => new string('y', column.MaxLength + 1),
        DataType.Memo     => "\uFEFFabc",
        DataType.OleObject => "not bytes",
        DataType.Binary   => new byte[column.LengthInBytes + 1],
        DataType.Decimal  => 1e25m,
        _ => null,
    };

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static string Letters(Random random, int length)
        => new string(Enumerable.Range(0, length).Select(_ => random.Next(3) == 0 ? (char)(0x0430 + random.Next(32)) : (char)('a' + random.Next(26))).ToArray());

    // A row of the table at random: most columns a value, some none, and no fault, one or two.
    private static Row RandomRow(Table table, Random random)
    {
        var columns = table.Columns.Where(c => !c.IsAutoNumber && !c.IsCalculated && Valid(c, random) is not null).ToList();
        var row = new Row();
        foreach (var column in columns)
            if (random.Next(10) != 0) row[column.Name] = Valid(column, random);
        int faults = random.Next(3);
        for (int i = 0; i < faults && columns.Count > 0; i++)
        {
            var column = columns[random.Next(columns.Count)];
            row[column.Name] = Fault(column, random);
        }
        return row;
    }

    /// <summary>
    /// Validates and then inserts each of <paramref name="count"/> random rows of each table of <paramref name="path"/>
    /// that takes rows, and holds the exception each throws, or none, to the other's.
    /// </summary>
    private static void AssertRandomRowsValidateAsTheyInsert(string path, int seed, int count, bool verifyIndexes = false)
    {
        var random = new Random(seed);
        var differ = new List<string>();
        int refused = 0, total = 0;
        using var db = Database.Open(path);
        db.VerifyIndexes = verifyIndexes;
        foreach (var name in db.GetTableNames())
        {
            var table = db.GetTable(name);
            if (table.InsertRefusal is not null) continue;
            for (int i = 0; i < count; i++)
            {
                var row = RandomRow(table, random);
                string? validated = Refusal(() => table.Validate(new Row(row)));
                string? inserted = Refusal(() => table.Insert(new Row(row)));
                total++;
                if (inserted is not null) refused++;
                if (validated != inserted)
                    differ.Add($"{Path.GetFileName(path)}/{name} row {i}: Validate {validated ?? "accepts"}, Insert {inserted ?? "accepts"}: "
                               + string.Join(", ", row.Select(kv => kv.Key + "=" + (kv.Value is string s && s.Length > 12 ? s.Substring(0, 12) + "..." : kv.Value))));
            }
        }
        Assert.True(differ.Count == 0, $"{differ.Count} of {total} rows:\n" + string.Join("\n", differ.Take(15)));
        Assert.True(refused > 0 && refused < total, $"{refused} of {total} rows refused: the sweep should reach both answers");
    }

    /// <summary>
    /// A parent with a column of every type, a primary key, a required column and a unique index, and a child with a
    /// relationship to it that enforces referential integrity.
    /// </summary>
    private string EveryType(FileFormat format)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + (format == FileFormat.Access2000 ? ".mdb" : ".accdb"));
        using var db = Database.Create(path, format);
        db.CreateTable("Parent", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("Code", DataType.Text) { MaxLength = 10, IsRequired = true },
            new ColumnDefinition("Yes", DataType.Boolean),
            new ColumnDefinition("B", DataType.Byte),
            new ColumnDefinition("I", DataType.Int16),
            new ColumnDefinition("Money", DataType.Currency),
            new ColumnDefinition("S", DataType.Single),
            new ColumnDefinition("D", DataType.Double),
            new ColumnDefinition("When", DataType.DateTime),
            new ColumnDefinition("G", DataType.Guid),
            new ColumnDefinition("Dec", DataType.Decimal) { Precision = 5, Scale = 2 },
            new ColumnDefinition("Bin", DataType.Binary) { MaxLength = 8 },
            new ColumnDefinition("Notes", DataType.Memo),
            new ColumnDefinition("Picture", DataType.OleObject),
        }.Concat(format == FileFormat.Access2000
            ? Array.Empty<ColumnDefinition>()
            : new[] { new ColumnDefinition("Big", DataType.Int64) }),   // Large Number: not in an .mdb
        primaryKey: new[] { "Id" }, indexes: new[] { new IndexDefinition("ByCode", "Code") { IsUnique = true } });
        db.CreateTable("Child", new[]
        {
            new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true },
            new ColumnDefinition("ParentId", DataType.Int32),
            new ColumnDefinition("Note", DataType.Text) { MaxLength = 20 },
        }, "Id");
        db.CreateRelationship(new RelationshipDefinition("Parent", "Id", "Child", "ParentId") { HasReferentialIntegrity = true });
        return path;
    }

    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2010)]
    public void Random_rows_in_tables_made_here(FileFormat format)
        => AssertRandomRowsValidateAsTheyInsert(EveryType(format), seed: 141, count: 150);

    [Theory]
    [InlineData("engine-1.2.2-created-table.mdb", false)]
    [InlineData("engine-8e41563-created-table.mdb", true)]
    [InlineData("engine-3ad3aed-rows-in-access-tables.mdb", false)]
    [InlineData("engine-3ad3aed-rows-in-access-tables.mdb", true)]
    [InlineData("engine-3ad3aed-rows-in-unique-indexes.mdb", true)]
    [InlineData("ace-text-marker.mdb", false)]
    public void Random_rows_in_files_earlier_versions_and_Access_wrote(string asset, bool verifyIndexes)
        => AssertRandomRowsValidateAsTheyInsert(Copy(asset), seed: 143, count: 60, verifyIndexes);

    [Theory]
    [InlineData("V2000", "common1V2000.mdb")]
    [InlineData("V2000", "indexV2000.mdb")]
    [InlineData("V2007", "common1V2007.accdb")]
    public void Random_rows_in_files_of_the_corpus(string version, string file)
    {
        string source = TestCorpus.File(version, file) ?? throw new FileNotFoundException(file);
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + Path.GetExtension(file));
        File.Copy(source, path);
        AssertRandomRowsValidateAsTheyInsert(path, seed: 2000, count: 30);
    }

    /// <summary>A row whose foreign key refers to no row is refused by both, with referential integrity enforced (#143).</summary>
    [Fact]
    public void A_row_that_breaks_a_relationship_is_refused_by_both()
    {
        string path = EveryType(FileFormat.Access2000);
        using var db = Database.Open(path);
        db.GetTable("Parent").Insert(new Row { ["Id"] = 1, ["Code"] = "a" });
        var child = db.GetTable("Child");
        child.Validate(new Row { ["ParentId"] = 1 });
        Assert.Throws<ConstraintViolationException>(() => child.Validate(new Row { ["ParentId"] = 2 }));
        Assert.Throws<ConstraintViolationException>(() => child.Insert(new Row { ["ParentId"] = 2 }));
    }

    /// <summary>
    /// Many rows validated, and then one inserted, leave the file byte for byte as the insert alone leaves it: no
    /// AutoNumber is taken, no long-value page is written or claimed.
    /// </summary>
    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2010)]
    public void An_insert_after_many_validations_writes_what_the_insert_alone_writes(FileFormat format)
    {
        string alone = EveryType(format);
        string afterValidations = Path.Combine(_dir, Guid.NewGuid().ToString("N") + Path.GetExtension(alone));
        File.Copy(alone, afterValidations);
        Row Inserted() => new Row { ["Id"] = 7, ["Code"] = "seven", ["Notes"] = "\uFEFF" + new string('x', 3000), ["Picture"] = new byte[500] };

        using (var db = Database.Open(alone))
        {
            db.GetTable("Parent").Insert(Inserted());
            db.GetTable("Child").Insert(new Row { ["ParentId"] = 7 });
        }
        using (var db = Database.Open(afterValidations))
        {
            var random = new Random(7);
            var parent = db.GetTable("Parent");
            var child = db.GetTable("Child");
            for (int i = 0; i < 200; i++)
            {
                Refusal(() => parent.Validate(RandomRow(parent, random)));
                Refusal(() => child.Validate(RandomRow(child, random)));
            }
            parent.Insert(Inserted());
            child.Insert(new Row { ["ParentId"] = 7 });
        }
        Assert.Equal(File.ReadAllBytes(alone), File.ReadAllBytes(afterValidations));
    }
}
