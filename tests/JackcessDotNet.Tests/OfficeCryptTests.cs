using System.IO;
using System.Security.Authentication;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// End-to-end tests for the Office codec of an encrypted .accdb, and the Jet codec of an encoded .mdb.
/// <para>
/// The .accdb files ACE makes with a password (Assets/dao-accdb2007-password.accdb and dao-accdb2010-password.accdb,
/// made by tools/protected-fixtures.ps1) are Agile-encrypted, and every test of that scheme runs on them.
/// </para>
/// <para>
/// The schemes ACE no longer makes - RC4 CryptoAPI (Access 2007's own), and the non-standard AES Jackcess-encrypt
/// reads - and files Access itself made are in the jackcess-encrypt project's test data, which is not committed here.
/// Set <c>JACKCESS_ENCRYPT_FIXTURES</c> to that repository's <c>src/test/data</c> folder to run the tests of them;
/// without it they are skipped.
/// </para>
/// </summary>
public sealed class OfficeCryptTests
{
    private const string Password = ProtectedFileTests.Password;
    private const string AgileFixture = "dao-accdb2010-password.accdb";

    private static readonly string? FixtureRoot = Environment.GetEnvironmentVariable("JACKCESS_ENCRYPT_FIXTURES");

    /// <summary>The path of a jackcess-encrypt fixture; the test is skipped when the fixtures are not there.</summary>
    private static string External(string name)
    {
        string? path = FixtureRoot is null ? null : Path.Combine(FixtureRoot, name);
        if (path is null || !File.Exists(path))
            Assert.Skip($"{name} is jackcess-encrypt's test data: set JACKCESS_ENCRYPT_FIXTURES to its src/test/data folder.");
        return path!;
    }

    private static byte[] ReadHeaderPage(string path) => File.ReadAllBytes(path).Take(4096).ToArray();

    // ── Agile Encryption: the .accdb files ACE makes ─────────────────────────

    [Theory]
    [InlineData("dao-accdb2007-password.accdb")]
    [InlineData("dao-accdb2010-password.accdb")]
    public void Open_AgileAccdb_WithCorrectPassword_ReadsEveryRow(string fixture)
    {
        using var db = Database.Open(ProtectedFileTests.Asset(fixture), new DatabaseOpenOptions { Password = Password });
        ProtectedFileTests.AssertItemsAsDaoReadsThem(db, fixture);
    }

    [Fact]
    public void Open_AgileAccdb_WithWrongPassword_Throws()
    {
        Assert.Throws<InvalidCredentialException>(
            () => Database.Open(ProtectedFileTests.Asset(AgileFixture), new DatabaseOpenOptions { Password = "definitely-not-it" }));
    }

    [Fact]
    public void Open_AgileAccdb_WithoutPassword_GivesClearError()
    {
        // Opening an encrypted .accdb without a password says so, rather than failing on a page it cannot parse.
        var ex = Assert.Throws<InvalidCredentialException>(() => Database.Open(ProtectedFileTests.Asset(AgileFixture)));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_UnencryptedAccdb_WithBogusPassword_StillOpens()
    {
        // Password should be silently ignored when the encoding-key slot is zero
        // (i.e. the file isn't encrypted). Use any unencrypted .accdb from the
        // main jackcess corpus.
        string? path = TestCorpus.Files("*.accdb", "V2007", "V2010", "V2019").FirstOrDefault();
        if (path is null) Assert.Skip("The corpus has no .accdb.");

        using var db = Database.Open(path!, new DatabaseOpenOptions { Password = "this-password-is-ignored" });
        Assert.NotEmpty(db.GetTableNames().Concat(db.GetSystemTableNames()));
    }

    [Fact]
    public void Write_Then_Reopen_Agile_PreservesAddedRow()
        => DoWriteThenReopen(ProtectedFileTests.Asset(AgileFixture), Password);

    [Fact]
    public void Encode_RoundTrips_ThroughTheCodec_Agile()
    {
        var codec = OfficeCryptCodecHandler.FromDbHeader(ReadHeaderPage(ProtectedFileTests.Asset(AgileFixture)), Password);
        Assert.NotNull(codec);

        byte[] plaintext = new byte[4096];
        new Random(456).NextBytes(plaintext);
        byte[] encoded = codec!.EncodePage(plaintext, pageNumber: 17);
        byte[] decoded = codec.DecodePage(encoded, pageNumber: 17);

        Assert.Equal(plaintext, decoded);
        Assert.NotEqual(plaintext, encoded);
    }

    [Fact]
    public void Encode_Page0_IsPassedThrough()
    {
        var codec = OfficeCryptCodecHandler.FromDbHeader(ReadHeaderPage(ProtectedFileTests.Asset(AgileFixture)), Password);
        Assert.NotNull(codec);

        byte[] page0 = new byte[4096];
        new Random(7).NextBytes(page0);
        Assert.Equal(page0, codec!.EncodePage(page0, pageNumber: 0));
        Assert.Equal(page0, codec.DecodePage(page0, pageNumber: 0));
    }

    [Fact]
    public void Codec_AdvertisesActiveSchemeOnAccessor()
    {
        var codec = OfficeCryptCodecHandler.FromDbHeader(ReadHeaderPage(ProtectedFileTests.Asset(AgileFixture)), Password);
        Assert.NotNull(codec);
        Assert.Contains("Agile", codec!.Scheme);
    }

    [Fact]
    public void Write_ManyRows_UnderAgileEncryption_AllSurviveReopen()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"bulk_{Guid.NewGuid():N}.accdb");
        File.Copy(ProtectedFileTests.Asset(AgileFixture), tmp);
        try
        {
            using (var db = Database.Open(tmp, new DatabaseOpenOptions { Password = Password }))
            {
                var t = db.GetTable("Items");
                for (int i = 0; i < 50; i++)
                    t.Insert(new Row { ["Id"] = 100 + i, ["Name"] = $"bulk-{i}" });
            }

            using var reopen = Database.Open(tmp, new DatabaseOpenOptions { Password = Password });
            var rows = reopen.GetTable("Items").Rows.ToList();
            Assert.Equal(60 + 50, rows.Count);
            Assert.Equal(50, rows.Count(r => (r.GetValueOrDefault("Name") as string)?.StartsWith("bulk-") == true));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // ── Header-mask demask edge case ────────────────────────────────────────

    [Fact]
    public void FromDbHeader_EncodingKeyEqualsHeaderMask_TreatedAsUnencrypted()
    {
        // Synthetic page: bytes at 0x3E are exactly the header mask, so after
        // demasking the encoding key is all zero → file is not encrypted.
        // Real-world example: linkeeTest.accdb from the main jackcess corpus.
        byte[] page = new byte[4096];
        page[0x3E] = 0xFB; page[0x3F] = 0x8A;     // these are HEADER_MASK[38..42]
        page[0x40] = 0xBC; page[0x41] = 0x4E;
        // EncryptionInfo struct left all-zero, so the version field reads 0.0.

        var codec = OfficeCryptCodecHandler.FromDbHeader(page, "any-password");
        Assert.Null(codec);
        Assert.False(OfficeCryptCodecHandler.IsEncrypted(page));
    }

    // ── jackcess-encrypt's fixtures (optional) ───────────────────────────────

    [Fact]
    public void Open_Db2013Enc_WithCorrectPassword_Succeeds()
    {
        using var db = Database.Open(External("db2013-enc.accdb"), new DatabaseOpenOptions { Password = "1234" });
        Assert.Contains("Customers", db.GetTableNames());
        Assert.NotEmpty(db.GetTable("Customers").Rows.ToList());
    }

    [Fact]
    public void Open_Db2013Enc_WithWrongPassword_Throws()
        => Assert.Throws<InvalidCredentialException>(
            () => Database.Open(External("db2013-enc.accdb"), new DatabaseOpenOptions { Password = "definitely-not-1234" }));

    [Theory]
    [InlineData("db2007-enc.accdb")]
    [InlineData("db2007-oldenc.accdb")]
    public void Open_StandardEncryption2007_WithCorrectPassword_Succeeds(string fixtureName)
    {
        using var db = Database.Open(External(fixtureName), new DatabaseOpenOptions { Password = "Test123" });
        Assert.Contains("Table1", db.GetTableNames());
        Assert.NotNull(db.GetTable("Table1").Rows.ToList());
    }

    [Theory]
    [InlineData("db2007-enc.accdb")]
    [InlineData("db2007-oldenc.accdb")]
    public void Open_StandardEncryption2007_WithWrongPassword_Throws(string fixtureName)
        => Assert.Throws<InvalidCredentialException>(
            () => Database.Open(External(fixtureName), new DatabaseOpenOptions { Password = "definitely-wrong" }));

    [Theory]
    [InlineData("db2007-enc.accdb", "Test123")]
    [InlineData("db2007-oldenc.accdb", "Test123")]
    [InlineData("db-nonstandard.accdb", "password")]
    [InlineData("db2013-enc.accdb", "1234")]
    public void Write_Then_Reopen_PreservesAddedRow(string fixtureName, string password)
        => DoWriteThenReopen(External(fixtureName), password);

    [Fact]
    public void Encode_RoundTrips_ThroughTheCodec_Rc4CryptoApi()
    {
        var codec = OfficeCryptCodecHandler.FromDbHeader(ReadHeaderPage(External("db2007-oldenc.accdb")), "Test123");
        Assert.NotNull(codec);

        byte[] plaintext = new byte[4096];
        new Random(123).NextBytes(plaintext);
        byte[] encoded = codec!.EncodePage(plaintext, pageNumber: 42);
        Assert.Equal(plaintext, codec.DecodePage(encoded, pageNumber: 42));
        Assert.NotEqual(plaintext, encoded);
    }

    [Fact]
    public void Open_NonStandardAesAccdb_WithCorrectPassword_Succeeds()
    {
        // db-nonstandard.accdb has FAES_FLAG clear yet uses AES; falls back
        // to the "compat mode 0" path (AES with 0 hash iterations).
        using var db = Database.Open(External("db-nonstandard.accdb"), new DatabaseOpenOptions { Password = "password" });
        Assert.NotEmpty(db.GetTableNames());
    }

    [Fact]
    public void Open_NonStandardAesAccdb_WithWrongPassword_Throws()
        => Assert.Throws<InvalidCredentialException>(
            () => Database.Open(External("db-nonstandard.accdb"), new DatabaseOpenOptions { Password = "wrong" }));

    [Theory]
    [InlineData("db2013-enc.accdb",    "1234",    "Agile")]
    [InlineData("db2007-enc.accdb",    "Test123", "Agile")]      // Office 2007 file but actually Agile
    [InlineData("db2007-oldenc.accdb", "Test123", "RC4")]        // true RC4 CryptoAPI
    [InlineData("db-nonstandard.accdb","password","Non-Standard")]
    public void Codec_Scheme_NamesActualEncryptionFlavor(string fixtureName, string password, string expectedSchemeFragment)
    {
        var codec = OfficeCryptCodecHandler.FromDbHeader(ReadHeaderPage(External(fixtureName)), password);
        Assert.NotNull(codec);
        Assert.Contains(expectedSchemeFragment, codec!.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_Db2013Enc_Customers_HasSevenSeededRows_WithExpectedField1Values()
    {
        // From doCheckOffice2013Db in upstream Java: Customers table seeded with
        // ID 1-7 / Field1 values { "Test","Test2","a",null,"c","d","f" }.
        using var db = Database.Open(External("db2013-enc.accdb"), new DatabaseOpenOptions { Password = "1234" });
        var rows = db.GetTable("Customers").Rows.ToList().OrderBy(r => Convert.ToInt32(r["ID"])).ToList();
        Assert.Equal(new[] { "Test", "Test2", "a", null, "c", "d", "f" }, rows.Select(r => r.GetValueOrDefault("Field1")));
    }

    [Theory]
    [InlineData("db2007-enc.accdb")]
    [InlineData("db2007-oldenc.accdb")]
    public void Read_Db2007_Table1_HasOneSeededRow_FooField1(string fixtureName)
    {
        // From doCheckOfficeDb in upstream Java: Table1 seeded with ID=1, Field1="foo" - Agile in db2007-enc, RC4
        // CryptoAPI in db2007-oldenc.
        using var db = Database.Open(External(fixtureName), new DatabaseOpenOptions { Password = "Test123" });
        var row = db.GetTable("Table1").Rows.Single();
        Assert.Equal(1, Convert.ToInt32(row["ID"]));
        Assert.Equal("foo", row["Field1"]);
    }

    [Fact]
    public void Read_NonStandardAccdb_TableOne_HasIdColumn()
    {
        // db-nonstandard.accdb's table is "Table_One" (not "Table1") with an "ID"
        // column — matches upstream testNonStandardProvider.
        using var db = Database.Open(External("db-nonstandard.accdb"), new DatabaseOpenOptions { Password = "password" });
        Assert.Contains(db.GetTable("Table_One").Columns, c => c.Name.Equals("ID", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("db-enc.mdb")]
    [InlineData("db97-enc.mdb")]
    public void Read_EncodedMdb_WithNoPassword_Table1_AsJackcessEncryptReadsIt(string fixtureName)
    {
        // From doCheckJetDb in upstream Java: an encoded Access 2000 file and an Access 97 one, which Jackcess-encrypt
        // opens with no password - the key is in the header.
        using var db = Database.Open(External(fixtureName));
        var rows = db.GetTable("Table1").Rows.OrderBy(r => Convert.ToInt32(r["ID"])).ToList();
        Assert.Equal(new object?[] { "hello", "world" }, rows.Select(r => r["col1"]));
        Assert.Equal(new[] { 0, 42 }, rows.Select(r => Convert.ToInt32(r["col2"])));
    }

    [Theory]
    [InlineData("pwd-set.mdb")]
    [InlineData("pwd2-set.mdb")]
    public void Open_MdbWithDatabasePassword_NeedsIt(string fixtureName)
    {
        string path = External(fixtureName);
        Assert.Throws<InvalidCredentialException>(() => Database.Open(path));
        Assert.Throws<InvalidCredentialException>(() => Database.Open(path, new DatabaseOpenOptions { Password = "TestPassword" }));
        using var db = Database.Open(path, new DatabaseOpenOptions { Password = "testpassword" });
        Assert.NotEmpty(db.GetTableNames());
    }

    // ── Write / round-trip ───────────────────────────────────────────────────

    /// <summary>
    /// Shared write-roundtrip: open the fixture, insert a row built from the
    /// real column list (skipping auto-num PKs), reopen, verify the row count
    /// increased. Column-name-agnostic so it works on whatever schema the
    /// fixture happens to ship with.
    /// </summary>
    private static void DoWriteThenReopen(string src, string password)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"rw_{Guid.NewGuid():N}.accdb");
        File.Copy(src, tmp);
        try
        {
            string tableName;
            int initialCount;
            using (var db = Database.Open(tmp, new DatabaseOpenOptions { Password = password }))
            {
                tableName = db.GetTableNames().FirstOrDefault()
                    ?? throw new InvalidOperationException("Fixture has no user tables.");
                var t = db.GetTable(tableName);
                initialCount = t.Rows.Count();

                var row = new Row();
                foreach (var col in t.Columns)
                {
                    if (col.IsAutoNumber) continue;
                    row[col.Name] = DummyValueFor(col);
                }
                t.Insert(row);
            }

            using var reopen = Database.Open(tmp, new DatabaseOpenOptions { Password = password });
            int newCount = reopen.GetTable(tableName).Rows.Count();
            Assert.Equal(initialCount + 1, newCount);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static object? DummyValueFor(Column col) => col.DataType switch
    {
        DataType.Int32          => 1_000_001,
        DataType.Int16           => (short)1,
        DataType.Byte          => (byte)1,
        DataType.Double        => 1.0,
        DataType.Single         => 1.0f,
        DataType.Currency         => 1m,
        DataType.Decimal       => 1m,
        DataType.Boolean       => true,
        DataType.Text          => "x",
        DataType.Memo          => "x",
        DataType.Binary        => new byte[] { 1 },
        DataType.Guid          => Guid.NewGuid(),
        DataType.DateTime => new DateTime(2024, 1, 1),
        _                      => null,
    };
}
