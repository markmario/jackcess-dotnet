using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JackcessDotNet;

// CorpusDump <corpus folder> <output folder>
//
// Reads every table of every .mdb and .accdb under the corpus folder, system tables included, and writes one text
// file for each: the tables in name order, each with its columns (name, type, length), the number of rows read and
// the number its definition counts, and every row as it was read, in the order it was read. Only the engine's
// public reading API is used, so the same source builds against any version of the engine: run it with two and
// compare the folders to see every value one reads differently from the other.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: CorpusDump <corpus folder> <output folder>");
    return 2;
}

string corpus = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
int files = 0, tables = 0, failures = 0;
foreach (string file in Directory.EnumerateFiles(corpus, "*.*", SearchOption.AllDirectories)
             .Where(f => f.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
             .OrderBy(f => f, StringComparer.Ordinal))
{
    string relative = Path.GetRelativePath(corpus, file);
    string target = Path.Combine(output, relative + ".txt");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

    // A copy is read, never the corpus file itself.
    string copy = Path.Combine(Path.GetTempPath(), $"corpusdump_{Guid.NewGuid():N}{Path.GetExtension(file)}");
    File.Copy(file, copy);
    var text = new StringBuilder();
    try
    {
        using var db = Database.Open(copy);
        foreach (string name in db.ListTables(includeSystem: true).OrderBy(n => n, StringComparer.Ordinal))
        {
            tables++;
            text.Append("TABLE ").Append(name).Append('\n');
            try
            {
                var table = db.GetTable(name);
                foreach (var column in table.Columns)
                    text.Append("  COLUMN ").Append(column.Name).Append(' ').Append(column.DataType).Append(' ').Append(column.Length).Append('\n');
                var rows = table.ReadAllRows();
                text.Append("  ROWS ").Append(rows.Count).Append(" read, ").Append(table.RowCount).Append(" in the definition\n");
                foreach (var row in rows)
                    text.Append("  ROW ").Append(string.Join(" | ", table.Columns.Select(c =>
                        c.Name + "=" + Written(row.TryGetValue(c.Name, out var value) ? value : null)))).Append('\n');
            }
            catch (Exception e)
            {
                failures++;
                text.Append("  FAILED ").Append(e.GetType().Name).Append(": ").Append(e.Message.Replace('\n', ' ')).Append('\n');
            }
        }
    }
    catch (Exception e)
    {
        failures++;
        text.Append("FILE FAILED ").Append(e.GetType().Name).Append(": ").Append(e.Message.Replace('\n', ' ')).Append('\n');
    }
    finally
    {
        File.Delete(copy);
    }
    File.WriteAllText(target, text.ToString());
    files++;
}
Console.WriteLine($"{files} files, {tables} tables, {failures} not read");
return 0;

// One value, written the same way whatever version read it.
static string Written(object? value) => value switch
{
    null        => "null",
    bool b      => b ? "yes" : "no",
    float f     => "single " + f.ToString("R", CultureInfo.InvariantCulture),
    double d    => "double " + d.ToString("R", CultureInfo.InvariantCulture),
    decimal m   => "decimal " + m.ToString(CultureInfo.InvariantCulture),
    DateTime t  => t.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
    Guid g      => g.ToString("D"),
    string s    => s.Length <= 60 ? "'" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "'" : $"text of {s.Length} #{Hash(Encoding.Unicode.GetBytes(s))}",
    byte[] b    => b.Length <= 24 ? "0x" + Convert.ToHexString(b) : $"bytes of {b.Length} #{Hash(b)}",
    IFormattable n => n.GetType().Name + " " + n.ToString(null, CultureInfo.InvariantCulture),
    _           => value.GetType().Name + " " + value,
};

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes), 0, 8);