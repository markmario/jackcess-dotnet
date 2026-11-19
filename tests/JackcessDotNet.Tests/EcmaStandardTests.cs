using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// ECMA Standard Encryption (MS-OFFCRYPTO 2.3.4.5-9), which no version of Access is known to give an .accdb: Access
/// 2007 encrypts with RC4 CryptoAPI, and Access 2010 and later with Agile Encryption, or RC4 CryptoAPI as their legacy
/// encryption; jackcess-encrypt's test files have none either. A file of it is made here from a plain .accdb,
/// encrypted as the specification says by the test's own code. The engine reads it, works out the key's 50,000
/// hashes once for the file, and refuses to write to it: what it wrote could not be checked against Access.
/// </summary>
public sealed class EcmaStandardTests : IDisposable
{
    private const string Password = "Standard 2007";
    private const int PageSize = 4096;
    private const int Rows = 200;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ecma_{Guid.NewGuid():N}.accdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string NoteOf(int id) => new((char)('a' + id % 26), 100 + id * 20);

    // A plain .accdb of 200 rows with long values, then encrypted in ECMA Standard Encryption with AES-128.
    private void MakeFile()
    {
        using (var db = Database.Create(_path, FileFormat.Access2010))
        {
            var table = db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("Note", DataType.Memo) }, "Id");
            for (int id = 1; id <= Rows; id++) table.Insert(new Row { ["Id"] = id, ["Note"] = NoteOf(id) });
        }
        byte[] file = File.ReadAllBytes(_path);

        // The encoding key, four bytes at 0x3E under the header's mask.
        for (int i = 0; i < 4; i++) file[0x3E + i] = (byte)(0x5A + i * 0x11);
        byte[] encodingKey = HeaderMask.EncodingKey(file.Take(PageSize).ToArray());

        byte[] salt = Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();
        byte[] hashed = Hashed(salt, Encoding.Unicode.GetBytes(Password));
        byte[] verifier = Enumerable.Range(0, 16).Select(i => (byte)(200 - i)).ToArray();
        byte[] verifierHash = new byte[32];
        Array.Copy(Sha1(verifier), verifierHash, 20);
        byte[] verifierKey = KeyFor(hashed, new byte[4]);

        // EncryptionInfo (MS-OFFCRYPTO 2.3.4.5): version 3.2, the EncryptionHeader and the EncryptionVerifier.
        var info = new MemoryStream();
        var writer = new BinaryWriter(info);
        writer.Write((ushort)3);
        writer.Write((ushort)2);
        const int flags = 0x04 | 0x20;   // fCryptoAPI, fAES
        writer.Write(flags);
        byte[] provider = Encoding.Unicode.GetBytes("Microsoft Enhanced RSA and AES Cryptographic Provider\0");
        writer.Write(32 + provider.Length);
        foreach (int field in new[] { flags, 0, 0x660E, 0x8004, 128, 0x18, 0, 0 }) writer.Write(field);   // AES-128, SHA-1
        writer.Write(provider);
        writer.Write(16);
        writer.Write(salt);
        writer.Write(Aes(verifierKey, verifier));
        writer.Write(20);
        writer.Write(Aes(verifierKey, verifierHash));
        byte[] blob = info.ToArray();
        BitConverter.GetBytes((short)blob.Length).CopyTo(file, 0x299);
        blob.CopyTo(file, 0x29B);

        // Every page but the header, under the key of its block: the encoding key with the page's number XORed in.
        for (int page = 1; page < file.Length / PageSize; page++)
        {
            var block = new byte[4];
            for (int i = 0; i < 4; i++) block[i] = (byte)(encodingKey[i] ^ (page >> (8 * i)));
            Aes(KeyFor(hashed, block), file, page * PageSize);
        }
        File.WriteAllBytes(_path, file);
    }

    // H0 = SHA1(salt, password); Hn = SHA1(n, Hn-1) for n from 0 to 49,999 (MS-OFFCRYPTO 2.3.4.7).
    private static byte[] Hashed(byte[] salt, byte[] password)
    {
        byte[] hash = Sha1(salt.Concat(password).ToArray());
        for (int n = 0; n < 50_000; n++) hash = Sha1(BitConverter.GetBytes(n).Concat(hash).ToArray());
        return hash;
    }

    // The key of a block: Hfinal = SHA1(H, block), stretched by the two pads and cut to 128 bits (2.3.4.7).
    private static byte[] KeyFor(byte[] hashed, byte[] block)
    {
        byte[] final = Sha1(hashed.Concat(block).ToArray());
        byte[] Pad(byte fill) => Enumerable.Range(0, 64).Select(i => (byte)(fill ^ (i < final.Length ? final[i] : 0))).ToArray();
        return Sha1(Pad(0x36)).Concat(Sha1(Pad(0x5C))).Take(16).ToArray();
    }

    private static byte[] Sha1(byte[] data)
    {
        using var sha1 = SHA1.Create();
        return sha1.ComputeHash(data);
    }

    private static byte[] Aes(byte[] key, byte[] plain)
    {
        var copy = (byte[])plain.Clone();
        Aes(key, copy, 0, copy.Length);
        return copy;
    }

    private static void Aes(byte[] key, byte[] data, int offset, int length = PageSize)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        byte[] cipher = encryptor.TransformFinalBlock(data, offset, length);
        Buffer.BlockCopy(cipher, 0, data, offset, length);
    }

    [Fact]
    public void A_file_in_ECMA_Standard_Encryption_reads_with_its_password_and_no_other()
    {
        MakeFile();
        using (var db = Database.Open(_path, new DatabaseOpenOptions { Password = Password }))
        {
            var rows = db.GetTable("T").Rows.OrderBy(r => (int)r["Id"]!).ToList();
            Assert.Equal(Enumerable.Range(1, Rows), rows.Select(r => (int)r["Id"]!));
            Assert.All(rows, r => Assert.Equal(NoteOf((int)r["Id"]!), r["Note"]));
        }
        Assert.Throws<InvalidCredentialException>(() => Database.Open(_path, new DatabaseOpenOptions { Password = "wrong" }));
        Assert.Throws<InvalidCredentialException>(() => Database.Open(_path));
    }

    // The 50,000 hashes every page's key starts from are worked out once for the file, not once a page.
    [Fact]
    public void The_keys_50000_hashes_are_worked_out_once_for_the_file()
    {
        MakeFile();
        using var db = Database.Open(_path, new DatabaseOpenOptions { Password = Password });
        Assert.Equal(Rows, db.GetTable("T").Rows.Count(r => r["Note"] is string));
        Assert.True(db.File.PageCount > 20);
        Assert.Equal(1, Assert.IsType<OfficeCryptCodecHandler>(db.File.Codec).HashesWorkedOut);
    }

    // Read only, refused before anything is written, as an Access 97 file is.
    [Fact]
    public void A_file_in_ECMA_Standard_Encryption_is_not_written()
    {
        MakeFile();
        byte[] before = File.ReadAllBytes(_path);
        using (var db = Database.Open(_path, new DatabaseOpenOptions { Password = Password }))
        {
            Assert.True(db.IsReadOnly);
            var refused = Assert.Throws<NotSupportedException>(() => db.GetTable("T").Insert(new Row { ["Id"] = 1000, ["Note"] = "x" }));
            Assert.Contains("ECMA Standard", refused.Message);
            Assert.Throws<NotSupportedException>(() => db.CreateTable("U", new[] { new ColumnDefinition("A", DataType.Int32) }));
            Assert.Throws<NotSupportedException>(() => db.GetTable("T").AddColumn(new ColumnDefinition("B", DataType.Int32)));
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }
}
