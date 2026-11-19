namespace JackcessDotNet;

/// <summary>
/// The codec of an Access 97 or Access 2000-2003 file (.mdb) whose pages Access encoded: what Access's Encrypt
/// Database command and DAO's <c>dbEncrypt</c> option make, and what Jackcess-encrypt's <c>JetCryptCodecHandler</c>
/// reads and writes. Every page but the first is RC4 under the four-byte key the header holds at 0x3E
/// (<see cref="HeaderMask.EncodingKey"/>), with the page's number XORed into it, least significant byte first. The
/// first page is the header, which only its mask hides.
/// <para>
/// The key is in the file, so no password is needed to read or write such a file: Access opens one with any
/// password, or none, when it has no database password. A database password is a different thing, which Access
/// checks and which changes no page (<see cref="HeaderMask.DatabasePassword"/>). Measured against files DAO 16 and
/// Jet 4.0 made (tools/protected-fixtures.ps1): the pages decode to what Access reads, and pages encoded here are
/// read by Access.
/// </para>
/// </summary>
internal sealed class JetCryptCodecHandler : ICodecHandler
{
    private readonly byte[] _encodingKey;

    private JetCryptCodecHandler(byte[] encodingKey) => _encodingKey = encodingKey;

    /// <summary>
    /// The codec for a file whose first page is <paramref name="header"/>, as the file holds it; null when the
    /// file's pages are not encoded (its key is all zero), and the file is read and written as it is.
    /// </summary>
    public static JetCryptCodecHandler? FromDbHeader(byte[] header)
    {
        byte[] key = HeaderMask.EncodingKey(header);
        return key.Any(b => b != 0) ? new JetCryptCodecHandler(key) : null;
    }

    public byte[] DecodePage(byte[] rawPage, int pageNumber) => Transform(rawPage, pageNumber);

    public byte[] EncodePage(byte[] page, int pageNumber) => Transform(page, pageNumber);

    /// <summary>An encoded .mdb is written to: Access reads what this library writes to one.</summary>
    public string? WriteRefusal => null;

    // RC4 is its own inverse: the same keystream decodes a page and encodes it.
    private byte[] Transform(byte[] page, int pageNumber)
    {
        if (pageNumber == 0) return page;

        byte[] keystream = Rc4Keystream(PageKey(pageNumber), page.Length);
        var result = new byte[page.Length];
        for (int i = 0; i < page.Length; i++)
            result[i] = (byte)(page[i] ^ keystream[i]);
        return result;
    }

    /// <summary>The key of page <paramref name="pageNumber"/>: the file's key with the page's number XORed in, least significant byte first.</summary>
    private byte[] PageKey(int pageNumber)
    {
        var key = new byte[HeaderMask.EncodingKeyLength];
        for (int i = 0; i < key.Length; i++)
            key[i] = (byte)(_encodingKey[i] ^ (pageNumber >> (8 * i)));
        return key;
    }

    /// <summary>
    /// Pure RC4 stream cipher (RFC compliant). Keys may be any length 1..256;
    /// returns the first <paramref name="length"/> bytes of the keystream.
    /// </summary>
    internal static byte[] Rc4Keystream(byte[] key, int length)
    {
        // Key schedule.
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j2 = 0;
        for (int i = 0; i < 256; i++)
        {
            j2 = (j2 + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j2]) = (s[j2], s[i]);
        }

        // PRGA.
        var output = new byte[length];
        int x = 0, y = 0;
        for (int n = 0; n < length; n++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[n] = s[(s[x] + s[y]) & 0xFF];
        }
        return output;
    }
}
