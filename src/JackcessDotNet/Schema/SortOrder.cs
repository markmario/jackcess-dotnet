using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// The order a Text or Memo column's values sort in, in its indexes, as its definition in the TDEF holds it: the
/// sort order's code (1033 for Access's "General" orders) and, in Jet4's four bytes, its version. Access 2000 to
/// 2007 sort in "General - Legacy" (version 0), Access 2010 and later make new databases in "General" (version 1),
/// and Access 97 has an order of its own. A database made in another locale has that locale's LCID for its code.
/// An index entry's key is the text in its column's order, so a key can be made only in an order whose keys this
/// library makes: General - Legacy and the locales' orders that have its keys, General, and Access 97's General
/// order, which is only searched (<see cref="TextIndexCodes.For"/>).
/// </summary>
internal readonly record struct SortOrder(short Code, byte Version)
{
    private const short GeneralCode = 1033;

    // Not a version Jet4 writes: Access 97's two-byte sort orders have none.
    private const byte Access97Version = 0xFF;

    /// <summary>"General - Legacy": the order of Access 2000 to 2007, and of a new database in any later Access.</summary>
    public static SortOrder GeneralLegacy { get; } = new(GeneralCode, 0);

    /// <summary>"General": the order Access 2010 and later can make a new database in.</summary>
    public static SortOrder General { get; } = new(GeneralCode, 1);

    /// <summary>Access 97's General order: sort order 1033 in a Jet3 file, its text in code page 1252.</summary>
    public static SortOrder Access97General { get; } = new(GeneralCode, Access97Version) { CodePage = 1252 };

    /// <summary>
    /// The code page of the text of an Access 97 order - the one its file is read in
    /// (<see cref="DatabaseOpenOptions.Access97CodePage"/>) - and 0 for any other order. Access 97's General order
    /// keys the bytes of the text in that code page, not its characters (tools/access97-code-page-fixture.ps1), so
    /// the keys of a value depend on it.
    /// </summary>
    public int CodePage { get; init; }

    public bool IsGeneralLegacy => this == GeneralLegacy;

    public bool IsGeneral => this == General;

    /// <summary>One of Access 97's orders, read from a Jet3 column: Jet4 has no form for it.</summary>
    public bool IsAccess97 => Version == Access97Version;

    /// <summary>Access 97's General order, in whatever code page (<see cref="CodePage"/>).</summary>
    public bool IsAccess97General => Code == GeneralCode && IsAccess97;

    /// <summary>
    /// Whether text in this order has the keys it has in General - Legacy: that order itself, and the orders of
    /// the locales in <see cref="LocalesKeyedAsGeneralLegacy"/>, in the version Access 2000 to 2007 gave them.
    /// </summary>
    public bool KeysAsGeneralLegacy => Version == 0 && (Code == GeneralCode || Array.IndexOf(LocalesKeyedAsGeneralLegacy, Code) >= 0);

    /// <summary>
    /// The locales, other than 1033, whose order's keys are those of General - Legacy. DAO's CreateDatabase, given
    /// a locale, gives the database's text that locale's LCID for its sort order, and ACE 16 was had to index
    /// 1,013,302 values in a database of each (tools/ace-text-keys.ps1 with -Locale): every character of the BMP
    /// alone and between two letters; every pair of letters of Latin-1 and Latin Extended-A, and of characters
    /// within the Greek, Cyrillic, Hebrew, Arabic and Thai blocks; letters with each combining mark; three-letter
    /// and punctuation runs of ASCII; 500,000 values of two to eight characters; and the values of the General -
    /// Legacy golden files. For these nine every key is the one ACE writes in a database of locale 1033. They are
    /// the locales of DAO's dbLangArabic, dbLangCyrillic, dbLangDutch, dbLangGreek and dbLangHebrew, and German,
    /// Italian, British English and Portuguese, which DAO has under dbLangGeneral.
    /// <para>
    /// Measured to differ, and so not here: Chinese (1028, 2052), Czech, Danish and Norwegian, French, Hungarian,
    /// Icelandic, Japanese, Korean, Lithuanian, Polish, Romanian, Slovak, Slovenian, Croatian, Spanish (1034,
    /// 3082), Swedish and Finnish, Thai, Turkish, Ukrainian and Vietnamese. A locale not measured is not here
    /// either: Thai's keys are those of General - Legacy for every character alone and differ for two together.
    /// </para>
    /// </summary>
    private static readonly short[] LocalesKeyedAsGeneralLegacy =
    {
        0x0401,   // Arabic
        0x0407,   // German
        0x0408,   // Greek
        0x040D,   // Hebrew
        0x0410,   // Italian
        0x0413,   // Dutch
        0x0419,   // Russian: DAO's dbLangCyrillic
        0x0809,   // English (United Kingdom)
        0x0816,   // Portuguese
    };

    /// <summary>
    /// The sort order a column definition holds at <paramref name="at"/>: the code, then in Jet4 a byte this
    /// library does not use and the version. A code of 0 is what versions of this library before sort orders
    /// wrote, and they keyed such a column in General - Legacy, as Jackcess reads it.
    /// </summary>
    public static SortOrder Read(byte[] page, int at, JetFormat format)
    {
        short code = ByteUtil.GetShort(page, at);
        if (format.SizeSortOrder < 4) return new(code, Access97Version) { CodePage = format.TextEncoding.CodePage };
        if (code == 0) return GeneralLegacy;
        return new(code, page[at + 3]);
    }

    /// <summary>Writes this sort order where a column definition holds it, as <see cref="Read"/> reads it.</summary>
    public void Write(byte[] page, int at, JetFormat format)
    {
        ByteUtil.PutShort(page, at, Code);
        if (format.SizeSortOrder < 4) return;
        page[at + 2] = 0;
        page[at + 3] = Version;
    }

    public override string ToString()
        => IsGeneralLegacy ? "General - Legacy"
         : IsGeneral ? "General"
         : IsAccess97General ? (CodePage == Access97General.CodePage ? "Access 97's General order" : $"Access 97's General order, in code page {CodePage}")
         : Version == Access97Version ? $"Access 97's order {Code}"
         : $"order {Code}, version {Version}";
}
