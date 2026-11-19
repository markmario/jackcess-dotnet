using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="JetCryptCodecHandler"/>, the codec of an .mdb whose pages Access encoded, and the header fields it
/// is made from (#46). The key is the four bytes at 0x3E once the header's mask is off; each page but the first is
/// RC4 under that key with the page's number XORed in, least significant byte first - Jackcess-encrypt's
/// <c>JetCryptCodecHandler</c>. No password is part of it: an earlier version derived a key from the bytes at 0x42
/// (the masked database password) and the password given, and opened no encoded file.
/// </summary>
public sealed class JetCryptKeyDerivationTests
{
    // The header's mask over the key's four bytes, at 0x3E (offset 38 of the mask, which starts at 0x18).
    private static readonly byte[] MaskAtKey = { 0xFB, 0x8A, 0xBC, 0x4E };

    /// <summary>A header whose key, once its mask is off, is <paramref name="key"/>.</summary>
    private static byte[] HeaderWithKey(params byte[] key)
    {
        var header = new byte[4096];
        for (int i = 0; i < 4; i++) header[0x3E + i] = (byte)(key[i] ^ MaskAtKey[i]);
        return header;
    }

    [Fact]
    public void A_header_whose_key_is_zero_once_unmasked_has_no_codec()
    {
        Assert.Null(JetCryptCodecHandler.FromDbHeader(HeaderWithKey(0, 0, 0, 0)));
    }

    [Fact]
    public void A_header_whose_key_is_not_zero_once_unmasked_has_a_codec()
    {
        Assert.NotNull(JetCryptCodecHandler.FromDbHeader(HeaderWithKey(0xAF, 0x04, 0xF0, 0xA0)));
        // Zero bytes on disk are the mask's bytes once it is off: a key.
        Assert.NotNull(JetCryptCodecHandler.FromDbHeader(new byte[4096]));
    }

    [Fact]
    public void A_page_is_RC4_under_the_key_with_its_number_XORed_in()
    {
        byte[] key = { 0xAF, 0x04, 0xF0, 0xA0 };
        var codec = JetCryptCodecHandler.FromDbHeader(HeaderWithKey(key))!;
        var page = new byte[4096];
        new Random(42).NextBytes(page);

        int pageNumber = 0x01020304;
        byte[] pageKey = { (byte)(key[0] ^ 0x04), (byte)(key[1] ^ 0x03), (byte)(key[2] ^ 0x02), (byte)(key[3] ^ 0x01) };
        byte[] stream = JetCryptCodecHandler.Rc4Keystream(pageKey, page.Length);
        byte[] expected = page.Select((b, i) => (byte)(b ^ stream[i])).ToArray();

        Assert.Equal(expected, codec.EncodePage(page, pageNumber));
        Assert.Equal(page, codec.DecodePage(expected, pageNumber));
    }

    [Theory]
    [InlineData("dao-jet4-encoded.mdb", 4096)]
    [InlineData("dao-jet4-password-encoded.mdb", 4096)]
    [InlineData("dao-jet3-encoded.mdb", 2048)]
    [InlineData("dao-jet3-password-encoded.mdb", 2048)]
    public void The_pages_of_a_file_Access_encoded_decode_to_its_catalog(string fixture, int pageSize)
    {
        byte[] file = File.ReadAllBytes(ProtectedFileTests.Asset(fixture));
        var codec = JetCryptCodecHandler.FromDbHeader(file.Take(pageSize).ToArray())!;

        byte[] raw = file.Skip(2 * pageSize).Take(pageSize).ToArray();
        Assert.False(raw[0] == 0x02 && raw[1] == 0x01, "The catalog's page is encoded on disk.");
        byte[] catalog = codec.DecodePage(raw, 2);
        Assert.Equal(0x02, catalog[0]);
        Assert.Equal(0x01, catalog[1]);
        Assert.Equal(raw, codec.EncodePage(catalog, 2));
    }

    [Fact]
    public void Different_pages_have_different_keystreams()
    {
        var codec = JetCryptCodecHandler.FromDbHeader(HeaderWithKey(0xC3, 0xC3, 0xC3, 0xC3))!;
        var page = new byte[64];
        Assert.NotEqual(codec.EncodePage(page, 1), codec.EncodePage(page, 2));
    }

    [Fact]
    public void Page_0_is_passed_through()
    {
        // Page 0, the header, is only masked, and the codec leaves it as it is.
        var codec = JetCryptCodecHandler.FromDbHeader(HeaderWithKey(0x55, 0x55, 0x55, 0x55))!;
        var page0 = new byte[4096];
        new Random(99).NextBytes(page0);
        Assert.Equal(page0, codec.EncodePage(page0, pageNumber: 0));
        Assert.Equal(page0, codec.DecodePage(page0, pageNumber: 0));
    }

    [Fact]
    public void Rc4Keystream_DifferentKeys_ProduceDifferentStreams()
    {
        var a = JetCryptCodecHandler.Rc4Keystream(new byte[] { 1, 2, 3 }, 32);
        var b = JetCryptCodecHandler.Rc4Keystream(new byte[] { 1, 2, 4 }, 32);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Rc4Keystream_SameKeyAndLength_IsDeterministic()
    {
        var a = JetCryptCodecHandler.Rc4Keystream(new byte[] { 9, 9, 9 }, 16);
        var b = JetCryptCodecHandler.Rc4Keystream(new byte[] { 9, 9, 9 }, 16);
        Assert.Equal(a, b);
    }
}
