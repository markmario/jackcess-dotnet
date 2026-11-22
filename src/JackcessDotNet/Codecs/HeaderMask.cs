namespace JackcessDotNet;

/// <summary>
/// The fields of a database's first page that say how the file is protected, read as Access reads them.
/// <para>
/// Access masks the first page from offset 0x18 on - 126 bytes of it in an Access 97 file, 128 in a later one - with
/// a fixed XOR mask: Jackcess's <c>BASE_HEADER_MASK</c>, which its <c>PageChannel</c> takes off as it reads the page
/// and puts back as it writes it. This library reads and writes the page as the file holds it, masked, and takes the
/// mask off only the fields read here, all of which are inside the 126 bytes every format masks.
/// </para>
/// </summary>
internal static class HeaderMask
{
    private const int MaskedFrom = 0x18;

    /// <summary>
    /// Where a Jet file keeps the key its pages are encoded with, and an .accdb the key its encryption mixes with
    /// each page's number (Jackcess's OFFSET_ENCODING_KEY).
    /// </summary>
    internal const int EncodingKeyAt = 0x3E;

    /// <summary>The bytes of the encoding key.</summary>
    internal const int EncodingKeyLength = 4;

    // Where a Jet file keeps its database password (Jackcess's OFFSET_PASSWORD), and how many characters it holds:
    // 20, in the database's code page in an Access 97 file and in UTF-16 in an Access 2000-2003 one. DAO refuses a
    // longer password.
    private const int PasswordAt = 0x42;
    private const int PasswordLength = 20;

    // Where an Access 2000-2003 file keeps the date it was made, a double; its whole number of days, as four bytes,
    // is a second mask over the password (Jackcess's OFFSET_HEADER_DATE and getPasswordMask). Access 97 has none.
    private const int CreationDateAt = 0x72;

    private static readonly byte[] Mask =
    {
        0xB5, 0x6F, 0x03, 0x62, 0x61, 0x08, 0xC2, 0x55, 0xEB, 0xA9, 0x67, 0x72, 0x43, 0x3F, 0x00, 0x9C,
        0x7A, 0x9F, 0x90, 0xFF, 0x80, 0x9A, 0x31, 0xC5, 0x79, 0xBA, 0xED, 0x30, 0xBC, 0xDF, 0xCC, 0x9D,
        0x63, 0xD9, 0xE4, 0xC3, 0x7B, 0x42, 0xFB, 0x8A, 0xBC, 0x4E, 0x86, 0xFB, 0xEC, 0x37, 0x5D, 0x44,
        0x9C, 0xFA, 0xC6, 0x5E, 0x28, 0xE6, 0x13, 0xB6, 0x8A, 0x60, 0x54, 0x94, 0x7B, 0x36, 0xF5, 0x72,
        0xDF, 0xB1, 0x77, 0xF4, 0x13, 0x43, 0xCF, 0xAF, 0xB1, 0x33, 0x34, 0x61, 0x79, 0x5B, 0x92, 0xB5,
        0x7C, 0x2A, 0x05, 0xF1, 0x7C, 0x99, 0x01, 0x1B, 0x98, 0xFD, 0x12, 0x4F, 0x4A, 0x94, 0x6C, 0x3E,
        0x60, 0x26, 0x5F, 0x95, 0xF8, 0xD0, 0x89, 0x24, 0x85, 0x67, 0xC6, 0x1F, 0x27, 0x44, 0xD2, 0xEE,
        0xCF, 0x65, 0xED, 0xFF, 0x07, 0xC7, 0x46, 0xA1, 0x78, 0x16, 0x0C, 0xED, 0xE9, 0x2D, 0x62, 0xD4,
    };

    /// <summary>
    /// The key the file's pages are encoded or encrypted with, as the header holds it under the mask: all zero for
    /// a file whose pages are neither.
    /// </summary>
    internal static byte[] EncodingKey(byte[] header) => Unmasked(header, EncodingKeyAt, EncodingKeyLength);

    /// <summary>
    /// The database password of an Access 97 or Access 2000-2003 file - Access's Set Database Password, DAO's
    /// <c>;pwd=</c> - or an empty string for a file that has none. Access checks it when it opens the file, and it
    /// changes nothing in a page: a file with a password and no encoding is read without it.
    /// </summary>
    internal static string DatabasePassword(byte[] header, JetFormat format)
    {
        if (format.Version == FileFormat.Access97)
            return UpToNull(format.TextEncoding.GetString(Unmasked(header, PasswordAt, PasswordLength)));

        byte[] password = Unmasked(header, PasswordAt, PasswordLength * 2);
        var dateMask = new byte[4];
        Util.ByteUtil.PutInt(dateMask, 0, WholeDays(Util.ByteUtil.GetDouble(Unmasked(header, CreationDateAt, 8), 0)));
        for (int i = 0; i < password.Length; i++)
            password[i] ^= dateMask[i % dateMask.Length];
        return UpToNull(System.Text.Encoding.Unicode.GetString(password));
    }

    /// <summary>
    /// Whether <paramref name="password"/> opens the file as Access opens it: the file has no database password, or
    /// this is it, exactly. An Access 97 file keeps its password in its code page, converted as Windows converts text
    /// to it - a character the code page lacks is the one Windows' best fit gives for it (L for Ł), or a question mark
    /// (Cyrillic, Chinese) - and Jet 3 checks a password given after the same conversion, case and all: passwords
    /// that convert alike open the file alike (<c>tools/jet3-password-fixture.ps1</c>). The conversion of the password
    /// given is in the code page the file is read in, as Jet's is in the machine's
    /// (<c>tools/access97-code-page-fixture.ps1</c>). An Access 2000-2003 file keeps its password in UTF-16.
    /// </summary>
    internal static bool OpensWith(byte[] header, JetFormat format, string? password)
    {
        if (format.Version == FileFormat.Access97)
        {
            byte[] stored = Unmasked(header, PasswordAt, PasswordLength);
            int length = Array.IndexOf(stored, (byte)0);
            if (length < 0) length = stored.Length;
            if (length == 0) return true;
            return password is not null && AsWindowsConverts(password, format.TextEncoding).SequenceEqual(stored.Take(length));
        }
        string expected = DatabasePassword(header, format);
        return expected.Length == 0 || string.Equals(password, expected, StringComparison.Ordinal);
    }

    // The characters for which .NET's best fit for code page 1252 is a question mark, and Windows' - the
    // WideCharToMultiByte Jet 3 converts a password with - is a character of the code page: every difference between
    // the two, measured over every character on .NET and .NET Framework, and confirmed with DAO 3.6
    // (tools/jet3-password-fixture.ps1). Surrogates they convert alike.
    private static readonly Dictionary<char, char> WindowsBestFit = new()
    {
        [' '] = ' ', [' '] = ' ',
        [' '] = ' ', [' '] = ' ', [' '] = ' ',
        ['‒'] = '-', ['―'] = '-',
        ['‛'] = '\'',
        ['‟'] = '"', ['″'] = '"', ['‶'] = '"',
    };

    // Text in code page 1252 (an Access 97 file's) as Windows converts it, best fit and all, the same on every target:
    // the code page's encoding, which converts as Windows does but for the characters above. A file read in another
    // code page (DatabaseOpenOptions.Access97CodePage) has its password converted by that code page's encoding, whose
    // best fit for a character the code page has not was not compared with Windows'.
    private static byte[] AsWindowsConverts(string text, System.Text.Encoding codePage)
    {
        char[] chars = text.ToCharArray();
        if (codePage.CodePage == 1252)
            for (int i = 0; i < chars.Length; i++)
                if (WindowsBestFit.TryGetValue(chars[i], out char fit)) chars[i] = fit;
        return codePage.GetBytes(chars);
    }

    // A double's whole number as Java's (int) cast takes it, which Jackcess masks the password with: toward zero,
    // and held to an int's range (0 for NaN). A file's creation date is a few tens of thousands of days.
    private static int WholeDays(double days)
        => double.IsNaN(days) ? 0
         : days >= int.MaxValue ? int.MaxValue
         : days <= int.MinValue ? int.MinValue
         : (int)days;

    private static string UpToNull(string text)
    {
        int end = text.IndexOf('\0');
        return end < 0 ? text : text.Substring(0, end);
    }

    private static byte[] Unmasked(byte[] header, int offset, int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(header[offset + i] ^ Mask[offset + i - MaskedFrom]);
        return bytes;
    }
}
