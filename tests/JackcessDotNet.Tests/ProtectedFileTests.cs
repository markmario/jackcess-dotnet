using System.Globalization;
using System.IO;
using System.Security.Authentication;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Password-protected and encoded files that Access made, opened as Access opens them (#46). The fixtures are
/// Assets/dao-*-password*.mdb/.accdb and dao-*-encoded.mdb, made by tools/protected-fixtures.ps1: an Access 2000-2003
/// file and an Access 97 one each with a database password, encoded (DAO's dbEncrypt), and both, and two .accdb
/// files with a password, which ACE encrypts. Each has one table, Items, of 60 rows; Assets/dao-protected-rows.tsv
/// is Items as DAO reads it, the same in every file.
/// <para>
/// What DAO was measured to do, and so what is asked here: a database password is checked exactly, letter case and
/// all, and a file that has one is not opened without it; an encoded file holds the key to its pages in its header
/// and opens with any password or none; an encrypted .accdb opens with its password and no other.
/// </para>
/// </summary>
public sealed class ProtectedFileTests : IDisposable
{
    /// <summary>The password of every protected fixture: P, a-umlaut, ssw, the euro sign, rd7.</summary>
    internal const string Password = "Pässw€rd7";

    private readonly List<string> _copies = new();

    public void Dispose()
    {
        foreach (string path in _copies)
            if (File.Exists(path)) File.Delete(path);
    }

    internal static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    /// <summary>A copy of the fixture, to write to.</summary>
    private string CopyOf(string fixture)
    {
        string copy = Path.Combine(Path.GetTempPath(), $"protected_{Guid.NewGuid():N}{Path.GetExtension(fixture)}");
        File.Copy(Asset(fixture), copy);
        _copies.Add(copy);
        return copy;
    }

    private static readonly string[] PasswordFixtures =
    {
        "dao-jet4-password.mdb", "dao-jet4-password-encoded.mdb", "dao-jet3-password.mdb", "dao-jet3-password-encoded.mdb",
        "dao-accdb2007-password.accdb", "dao-accdb2010-password.accdb",
    };

    private static readonly string[] EncodedFixtures = { "dao-jet4-encoded.mdb", "dao-jet3-encoded.mdb" };

    public static IEnumerable<object[]> WithPassword() => PasswordFixtures.Select(name => new object[] { name });

    public static IEnumerable<object[]> EncodedOnly() => EncodedFixtures.Select(name => new object[] { name });

    public static IEnumerable<object[]> Every() => PasswordFixtures.Concat(EncodedFixtures).Select(name => new object[] { name });

    private static bool HasPassword(string fixture) => fixture.Contains("password");

    private static Database OpenFixture(string path, string fixture)
        => HasPassword(fixture) ? Database.Open(path, new DatabaseOpenOptions { Password = Password }) : Database.Open(path);

    /// <summary>Items as DAO reads it: Id, Name, Notes, Amount, Ratio, Stamp, Flag; null for \N.</summary>
    internal static List<object?[]> RowsAsDaoReadsThem()
        => File.ReadAllLines(Asset("dao-protected-rows.tsv"), System.Text.Encoding.UTF8)
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => line.Split('\t'))
            .Select(cells => new object?[]
            {
                int.Parse(cells[0], CultureInfo.InvariantCulture),
                cells[1],
                cells[2] == @"\N" ? null : cells[2],
                decimal.Parse(cells[3], CultureInfo.InvariantCulture),
                double.Parse(cells[4], CultureInfo.InvariantCulture),
                DateTime.ParseExact(cells[5], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                bool.Parse(cells[6]),
            })
            .ToList();

    internal static readonly string[] ItemColumns = { "Id", "Name", "Notes", "Amount", "Ratio", "Stamp", "Flag" };

    internal static void AssertItemsAsDaoReadsThem(Database db, string what)
    {
        var expected = RowsAsDaoReadsThem();
        var read = db.GetTable("Items").Rows.OrderBy(r => (int)r["Id"]!).ToList();
        Assert.Equal(expected.Count, read.Count);
        for (int i = 0; i < expected.Count; i++)
            for (int c = 0; c < ItemColumns.Length; c++)
                Assert.True(Equals(expected[i][c], read[i][ItemColumns[c]]),
                    $"{what}, row {expected[i][0]}, {ItemColumns[c]}: DAO reads {Describe(expected[i][c])}, this library {Describe(read[i][ItemColumns[c]])}.");
    }

    private static string Describe(object? value)
        => value switch
        {
            null => "null",
            string text => $"{text.Length} characters beginning '{text.Substring(0, Math.Min(12, text.Length))}'",
            _ => $"{value} ({value.GetType().Name})",
        };

    // ── Reading ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Every))]
    public void Every_protected_file_reads_with_its_password_as_DAO_reads_it(string fixture)
    {
        using var db = OpenFixture(Asset(fixture), fixture);
        Assert.Equal(new[] { "Items" }, db.GetTableNames());
        AssertItemsAsDaoReadsThem(db, fixture);
    }

    [Theory]
    [MemberData(nameof(WithPassword))]
    public void A_file_with_a_password_is_refused_without_it_and_with_any_other(string fixture)
    {
        string path = Asset(fixture);
        Assert.Throws<InvalidCredentialException>(() => Database.Open(path));
        Assert.Throws<InvalidCredentialException>(() => Database.Open(path, new DatabaseOpenOptions()));
        Assert.Throws<InvalidCredentialException>(() => Database.Open(path, new DatabaseOpenOptions { Password = "" }));
        foreach (string other in new[] { "wrong", Password.ToUpperInvariant(), Password.ToLowerInvariant(), Password + " ",
                                         Password.Substring(0, Password.Length - 1), Password + "7" })
            Assert.Throws<InvalidCredentialException>(() => Database.Open(path, new DatabaseOpenOptions { Password = other }));

        // Nothing is left holding the file: it can be deleted.
        string copy = CopyOf(fixture);
        Assert.Throws<InvalidCredentialException>(() => Database.Open(copy, new DatabaseOpenOptions { Password = "wrong" }));
        File.Delete(copy);
    }

    [Theory]
    [MemberData(nameof(EncodedOnly))]
    public void An_encoded_file_without_a_password_opens_with_any_password_or_none(string fixture)
    {
        foreach (var open in new Func<string, Database>[]
                 {
                     path => Database.Open(path),
                     path => Database.Open(path, new DatabaseOpenOptions { Password = "anything at all" }),
                     path => Database.Open(path, new DatabaseOpenOptions { Password = Password }),
                 })
        {
            using var db = open(Asset(fixture));
            AssertItemsAsDaoReadsThem(db, fixture);
        }
    }

    [Theory]
    [InlineData("dao-jet4-password.mdb", Password, false)]
    [InlineData("dao-jet4-encoded.mdb", "", true)]
    [InlineData("dao-jet4-password-encoded.mdb", Password, true)]
    [InlineData("dao-jet3-password.mdb", Password, false)]
    [InlineData("dao-jet3-encoded.mdb", "", true)]
    [InlineData("dao-jet3-password-encoded.mdb", Password, true)]
    public void The_header_says_what_password_and_key_a_jet_file_has(string fixture, string password, bool encoded)
    {
        byte[] header = File.ReadAllBytes(Asset(fixture)).Take(4096).ToArray();
        var format = PageFile.FormatOf(Asset(fixture))!;
        Assert.Equal(password, HeaderMask.DatabasePassword(header, format));
        Assert.Equal(encoded, HeaderMask.EncodingKey(header).Any(b => b != 0));
        Assert.Equal(encoded, JetCryptCodecHandler.FromDbHeader(header) is not null);
    }

    [Fact]
    public void A_file_with_no_protection_has_no_password_or_key_in_its_header()
    {
        string path = Path.Combine(Path.GetTempPath(), $"plain_{Guid.NewGuid():N}.mdb");
        _copies.Add(path);
        using (Database.Create(path)) { }
        byte[] header = File.ReadAllBytes(path).Take(4096).ToArray();
        Assert.Equal("", HeaderMask.DatabasePassword(header, JetFormat.Jet4));
        Assert.All(HeaderMask.EncodingKey(header), b => Assert.Equal(0, b));
        Assert.Null(JetCryptCodecHandler.FromDbHeader(header));
    }

    [Theory]
    [InlineData("dao-accdb2007-password.accdb")]
    [InlineData("dao-accdb2010-password.accdb")]
    public void ACE_encrypts_a_database_it_makes_with_a_password_with_Agile_Encryption(string fixture)
    {
        byte[] header = File.ReadAllBytes(Asset(fixture)).Take(4096).ToArray();
        // ACE 16 gives a database asked for in the Access 2007 format the Access 2010 version, which the encryption
        // needs: both files have version 0x03.
        Assert.Equal(0x03, header[0x14]);
        Assert.True(OfficeCryptCodecHandler.IsEncrypted(header));
        Assert.Contains("Agile", OfficeCryptCodecHandler.FromDbHeader(header, Password)!.Scheme);
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("dao-jet4-password.mdb")]
    [InlineData("dao-jet4-encoded.mdb")]
    [InlineData("dao-jet4-password-encoded.mdb")]
    [InlineData("dao-accdb2007-password.accdb")]
    [InlineData("dao-accdb2010-password.accdb")]
    public void Rows_and_a_table_written_to_a_protected_file_are_read_back(string fixture)
    {
        string path = CopyOf(fixture);
        using (var db = OpenFixture(path, fixture))
        {
            var items = db.GetTable("Items");
            for (int id = 1001; id <= 1040; id++)
                items.Insert(new Row
                {
                    ["Id"] = id, ["Name"] = $"Written {id}", ["Notes"] = new string((char)('a' + id % 26), id * 3),
                    ["Amount"] = id * 1.25m, ["Ratio"] = id / 3.0, ["Stamp"] = new DateTime(2026, 11, 19).AddHours(id), ["Flag"] = id % 2 == 0,
                });
            var made = db.CreateTable("Made", new[] { new ColumnDefinition("K", DataType.Int32), new ColumnDefinition("V", DataType.Memo) }, "K");
            for (int k = 1; k <= 20; k++) made.Insert(new Row { ["K"] = k, ["V"] = new string((char)('A' + k), 6000 + k) });
        }

        using (var db = OpenFixture(path, fixture))
        {
            var items = db.GetTable("Items");
            Assert.Equal(100, items.Rows.Count());
            for (int id = 1001; id <= 1040; id++)
            {
                var row = items.FindRowByPrimaryKey(id)!;
                Assert.Equal($"Written {id}", row["Name"]);
                Assert.Equal(new string((char)('a' + id % 26), id * 3), row["Notes"]);
                Assert.Equal(id * 1.25m, row["Amount"]);
                Assert.Equal(id / 3.0, row["Ratio"]);
            }
            var made = db.GetTable("Made");
            for (int k = 1; k <= 20; k++)
                Assert.Equal(new string((char)('A' + k), 6000 + k), made.FindRowByPrimaryKey(k)!["V"]);
        }

        // The pages written are encoded or encrypted as the file's others are: without its codec, the catalog's
        // page of a protected file does not read.
        if (!fixture.Contains("jet4-password.mdb"))
        {
            byte[] raw = File.ReadAllBytes(path);
            int pageSize = 4096, pages = raw.Length / pageSize;
            int plainTableDefinitions = Enumerable.Range(1, pages - 1).Count(p => raw[p * pageSize] == 0x02 && raw[p * pageSize + 1] == 0x01);
            Assert.True(plainTableDefinitions == 0, $"{plainTableDefinitions} pages of {fixture} read as table definitions without its codec.");
        }
    }

    [Theory]
    [InlineData("dao-jet3-password.mdb")]
    [InlineData("dao-jet3-encoded.mdb")]
    [InlineData("dao-jet3-password-encoded.mdb")]
    public void A_protected_Access_97_file_is_read_and_not_written(string fixture)
    {
        string path = CopyOf(fixture);
        byte[] before = File.ReadAllBytes(path);
        using (var db = OpenFixture(path, fixture))
        {
            Assert.True(db.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => db.GetTable("Items").Insert(new Row { ["Id"] = 999 }));
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ── What is not a password's doing ───────────────────────────────────────

    [Fact]
    public void A_file_whose_catalog_does_not_read_is_damaged_whatever_password_is_given()
    {
        string path = Path.Combine(Path.GetTempPath(), $"damaged_{Guid.NewGuid():N}.mdb");
        _copies.Add(path);
        using (var db = Database.Create(path))
            db.CreateTable("X", new[] { new ColumnDefinition("A", DataType.Int32) });
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 2 * 4096;
            stream.Write(new byte[] { 0xA7, 0x5C, 0x13, 0xE9 }, 0, 4);
        }

        // The header names no password and no encoding: a password changes nothing, and the file is damaged.
        foreach (var open in new Func<Database>[]
                 {
                     () => Database.Open(path),
                     () => Database.Open(path, new DatabaseOpenOptions { Password = "not the password" }),
                 })
        {
            var refusal = Assert.Throws<NotSupportedException>(open);
            Assert.Contains("encrypted or corrupt", refusal.Message);
        }
        File.Delete(path);
    }
}
