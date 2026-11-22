using System.Globalization;
using System.Text;

namespace JackcessDotNet;

/// <summary>
/// Makes the bytes Access keeps for a text value in an index entry, in one of the sort orders whose keys this
/// library makes: <see cref="GeneralLegacy"/> (Access 2000 to 2007), <see cref="General"/> (Access 2010 and
/// later) and <see cref="Access97"/> (Access 97's General order, in a file that is only read). The first two
/// differ only in the codes each character has; the layout of a key is the same. Access 97's keys have a layout
/// of their own (<see cref="EncodeAccess97"/>).
///
/// The codes are Jackcess's (<c>GeneralLegacyIndexCodes</c>, <c>GeneralIndexCodes</c> and
/// <c>General97IndexCodes</c>), one line per character in embedded resources: U+0000..U+00FF in one file, read
/// at once, and U+0100..U+FFFF less the surrogates in another, read on first use. A code in those files drops
/// its leading zero ("C" is 0x0C). Where Access writes a key those codes do not give, Access is followed: the
/// golden files of ACE's own keys in the tests say where. The marks that repeat or lengthen the character before them
/// (々, ー, the shadda) have no codes of their own in General - Legacy: they are keyed by that character
/// (<see cref="MarkCodes"/>). In General their keys are not made.
///
/// A General or General - Legacy key's layout:
///   [inline bytes per char]
///   END_TEXT (0x01)
///   [extra/international bytes section, if any]
///   [END_TEXT × 2 if crazy or unprintable]
///   [crazy codes packed into 6-bit groups, if any, then their suffix, which holds the codes of marks after kana]
///   [CRAZY_CODES_UNPRINT_SUFFIX if both]
///   [END_TEXT before unprintable section, if any]
///   [unprintable codes]
///   [END_EXTRA_TEXT then byte-flip of the whole text if DESC]
///   END_EXTRA_TEXT (0x00)
/// </summary>
internal sealed class TextIndexCodes
{
    private const byte EndText                          = 0x01;
    private const byte EndExtraText                     = 0x00;
    private const byte InternationalExtraPlaceholder    = 0x02;
    private const int  UnprintableCountStart            = 7;
    private const int  UnprintableCountMultiplier       = 4;
    private const int  UnprintableOffsetFlags           = 0x8000;
    private const byte UnprintableMidfix                = 0x06;
    private const byte CrazyCodeStart                   = 0x80;
    private const byte CrazyCode1                       = 0x02;
    private const byte CrazyCode2                       = 0x03;
    private const byte CrazyCodesSuffixStart            = 0xFF;
    private const byte CrazyCodesSuffixMidfix           = 0x02;
    private const byte CrazyCodesSuffixRepeat           = 0x80;
    private const int  CrazyCodesPerSuffixRepeat        = 7;
    private const byte CrazyCodesUnprintSuffix          = 0xFF;

    /// <summary>Max text length used as an index key (matches Jet TEXT_FIELD_MAX_LENGTH/UNIT_SIZE).</summary>
    private const int MaxTextIndexCharLength = 255;

    private const int FirstExtChar         = 0x100;
    private const int FirstHighSurrogate   = 0xD800;
    private const int FirstLowSurrogate    = 0xDC00;
    private const int LastLowSurrogate     = 0xDFFF;
    private const int SurrogateRangeCount  = 0xE000 - 0xD800;   // = 2048

    // The extended file covers U+0100..U+FFFF excluding U+D800..U+DFFF surrogates
    // = (0xFFFF - 0x0100 + 1) - 2048 = 65280 - 2048 = 63232 entries.
    private const int ExtCodeCount = 0x10000 - FirstExtChar - SurrogateRangeCount;

    private static readonly CharCodes Ignored = new();

    /// <summary>"General - Legacy", sort order 1033 version 0: Access 2000 to 2007, and a Jet4 file of any age.</summary>
    public static TextIndexCodes GeneralLegacy { get; } = new(
        "index_codes_genleg.txt", "index_codes_ext_genleg.txt", isGeneral: false,
        corrections: new()
        {
            ['\u0E4C'] = "P0",
        },
        contextDependent: "",
        repeatMarks: "\u0651\u3005\u3031\u3032\u309D\u309E\u30FD\u30FE\uFE7C\uFE7D",
        lengthMarks: "\u30FC\uFF70",
        twoLetters: "\u00C6\u00DE\u00DF\u00E6\u00FE\u0132\u0133\u0152\u0153\u01C7\u01C8\u01C9\u01CA\u01CB\u01CC\u01F1\u01F2\u01F3"
                  + "\u05F0\u05F1\u05F2\uFB00\uFB01\uFB02\uFB05\uFB06",
        threeLetters: "\uFB03\uFB04");

    /// <summary>"General", sort order 1033 version 1: what Access 2010 and later can make a new database in.</summary>
    public static TextIndexCodes General { get; } = new(
        "index_codes_gen.txt", "index_codes_ext_gen.txt", isGeneral: true,
        corrections: new()
        {
            ['\u0B3C'] = "P0", ['\u0B3D'] = "P0", ['\u0B70'] = "P0", ['\u0DCA'] = "P0", ['\u0E4C'] = "P0",
            ['\u1DCA'] = "PFF", ['\u1DFF'] = "PFE",
            ['\u0EC6'] = "I41,0", ['\u0EC8'] = "I41,0", ['\u0EC9'] = "I41,0", ['\u0ECA'] = "I41,0", ['\u0ECB'] = "I41,0",
        },
        contextDependent: "\u3005\u3031\u3032\u3033\u3034\u3035\u303B\u309D\u309E\u30A0\u30FC\u30FD\u30FE\uA015\uFF70"
                        + "\u06D6\u06D7\uFD24\uFD6F");

    /// <summary>
    /// Access 97's General order (sort order 1033 in a Jet3 file), its text in the Windows code page 1252, one byte
    /// a character, and its codes are of those 256 bytes: a character beyond U+00FF has the codes of its byte when
    /// the code page has it (€ is 0x80, Š 0x8A), and no key when it has not - no value of such a file holds it. A
    /// file read in another code page has the same codes for the bytes of its own (<see cref="Access97In"/>).
    /// </summary>
    public static TextIndexCodes Access97 { get; } = new("index_codes_gen_97.txt", "index_mappings_ext_gen_97.txt");

    /// <summary>
    /// Access 97's General order in a file whose text is in <paramref name="codePage"/>
    /// (<see cref="DatabaseOpenOptions.Access97CodePage"/>): <see cref="Access97"/> for 1252. Jet keys the bytes of
    /// the text, whatever their code page, with the codes those bytes have in code page 1252 - Jet 4.0 and ACE 12 wrote
    /// the same key for the bytes of "Привет" in code page 1251 as for the same bytes written as "Ïðèâåò" with 1252,
    /// and keyed code page 932's two bytes a character the same way (tools/access97-code-page-fixture.ps1) - so a value
    /// is keyed as the characters of code page 1252 that have its bytes in its own.
    /// </summary>
    internal static TextIndexCodes Access97In(int codePage)
        => codePage == Access97.CodePage ? Access97 : Access97InCodePage.GetOrAdd(codePage, cp => new TextIndexCodes(Access97, Encoding.GetEncoding(cp)));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TextIndexCodes> Access97InCodePage = new();

    private static readonly CharCodes NotInCodePage = new() { IsNotInCodePage = true };

    // Stands, in a value turned into code page 1252's characters, for a character its own code page has not: one
    // Access 97's codes have as not in the code page (0x80 is €, not U+0080).
    private const char NotInCodePageMark = '\u0080';

    private readonly CharCodes[] _basicCodes;
    private readonly Lazy<CharCodes[]> _extCodes;
    private readonly bool _isGeneral;
    private readonly bool _isAccess97;

    // Access 97 in a code page other than 1252: the code page, and the characters of code page 1252 that have the
    // bytes of each character in it, or null for a character it has not.
    private readonly Encoding? _codePage;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<char, string?>? _asCodePage1252;

    // Access 97: the byte of each character from FirstMappedChar on that the code page has; 0 for one it has not.
    private readonly byte[] _mappedBytes = Array.Empty<byte>();
    private const int FirstMappedChar = 338;    // Œ
    private const int LastMappedChar  = 8482;   // ™

    /// <param name="basicResource">The codes of U+0000..U+00FF.</param>
    /// <param name="extResource">The codes of U+0100..U+FFFF less the surrogates.</param>
    /// <param name="isGeneral">
    /// The General order: a letter's weight is two bytes, so the places a character takes follow from its codes,
    /// and each half of a surrogate pair has a weight. In General - Legacy the codes do not say which characters
    /// take two or three places, and surrogates have no weight.
    /// </param>
    /// <param name="corrections">
    /// The lines of the characters ACE keys otherwise than Jackcess's files say, in the files' own form. With
    /// <paramref name="contextDependent"/>, <paramref name="twoLetters"/> and <paramref name="threeLetters"/>, it is
    /// what ACE wrote for every character of the BMP, alone and among other characters, where that differed from
    /// the files.
    /// </param>
    /// <param name="contextDependent">
    /// The characters ACE keys by the characters around them whose keys this library does not make: in General, the
    /// marks that repeat or lengthen the character before them (々, ー, ゝ and the like) and four Arabic ligatures.
    /// </param>
    /// <param name="repeatMarks">
    /// The marks ACE keys as the character before them, with the extra code of their own line (々, ゝ, the shadda).
    /// </param>
    /// <param name="lengthMarks">
    /// The marks ACE keys as the character before them, or after a kana as that kana's vowel (ー and its half-width
    /// form).
    /// </param>
    /// <param name="twoLetters">
    /// In General - Legacy, the characters Access sorts as two letters (Æ as AE, ß as ss).
    /// </param>
    /// <param name="threeLetters">
    /// In General - Legacy, the characters Access sorts as three letters (ﬃ as ffi).
    /// </param>
    private TextIndexCodes(
        string basicResource, string extResource, bool isGeneral, Dictionary<char, string> corrections,
        string contextDependent, string repeatMarks = "", string lengthMarks = "", string twoLetters = "", string threeLetters = "")
    {
        _isGeneral = isGeneral;
        _basicCodes = Load(basicResource, (char)0, FirstExtChar);
        _extCodes = new Lazy<CharCodes[]>(() => Load(extResource, (char)FirstExtChar, ExtCodeCount));

        CharCodes[] Load(string resource, char first, int count)
        {
            var codes = LoadCodes(resource, count);
            bool InTable(char c) => c >= first && (first > 0 || c < FirstExtChar);
            int IndexOf(char c) => first == 0 ? c : ExtIndex(c);

            foreach (var correction in corrections.Where(c => InTable(c.Key)))
                codes[IndexOf(correction.Key)] = ParseCodeLine(correction.Value);
            foreach (char c in contextDependent.Where(InTable))
                codes[IndexOf(c)] = new CharCodes { Mark = MarkKind.Unkeyed };
            // A mark's line holds its key after "a": a's inline code, and the mark's own extra code.
            foreach (char c in repeatMarks.Where(InTable))
                codes[IndexOf(c)] = new CharCodes { Mark = MarkKind.Repeat, Extra = codes[IndexOf(c)].Extra };
            foreach (char c in lengthMarks.Where(InTable))
                codes[IndexOf(c)] = new CharCodes { Mark = MarkKind.Lengthen, Extra = codes[IndexOf(c)].Extra };

            if (isGeneral)
            {
                // Two bytes to a letter, and a place for each letter that has no extra code of its own. An
                // ideograph's four bytes (0xFD 0xFF and its weight) are one letter, which counts as two only
                // where an unprintable character's place is told.
                foreach (var c in codes.Where(c => c.Inline is not null && c != Ignored))
                {
                    bool ideograph = c.Inline!.Length == 4 && c.Inline[0] >= 0xF0;
                    c.Places = ideograph ? 1 : Math.Max(1, c.Inline.Length / 2 - (c.Extra?.Length ?? 1) + 1);
                    c.UnprintablePlaces = ideograph ? 2 : c.Places;
                }
            }
            foreach (char c in twoLetters.Where(InTable)) codes[IndexOf(c)].Places = codes[IndexOf(c)].UnprintablePlaces = 2;
            foreach (char c in threeLetters.Where(InTable)) codes[IndexOf(c)].Places = codes[IndexOf(c)].UnprintablePlaces = 3;
            return codes;
        }
    }

    /// <summary>Access 97's codes: those of the 256 bytes of code page 1252, and the bytes of the characters beyond U+00FF it has.</summary>
    /// <param name="codesResource">The codes of the bytes 0x00..0xFF.</param>
    /// <param name="mappingsResource">A line for each character beyond U+00FF that the code page has: its code point and its byte.</param>
    private TextIndexCodes(string codesResource, string mappingsResource)
    {
        _isAccess97 = true;
        _basicCodes = LoadCodes(codesResource, FirstExtChar);

        // A letter that can have an accent ('G' in the codes) takes a place among the accents when it has none.
        // So does each such letter in a character keyed as two: ACE 12 writes two places for Œ (o and e), Æ and
        // ß, where Jackcess, which takes them for plain characters, writes none.
        var accentable = new HashSet<byte>(_basicCodes.Where(c => c is { Inline.Length: 1, IsSimple: false, Extra: null }).Select(c => c.Inline![0]));
        foreach (var c in _basicCodes)
            c.Places = c.Inline is null || c.Extra is not null ? 0 : c.Inline.Count(accentable.Contains);
        _extCodes   = new Lazy<CharCodes[]>(() => Array.Empty<CharCodes>());
        _mappedBytes = new byte[LastMappedChar - FirstMappedChar + 1];

        string resourcePath = $"JackcessDotNet.Resources.{mappingsResource}";
        using var stream = typeof(TextIndexCodes).Assembly.GetManifestResourceStream(resourcePath)
            ?? throw new InvalidOperationException($"Missing embedded resource '{resourcePath}'.");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            string[] parts = line.Split(',');
            int from = int.Parse(parts[0], CultureInfo.InvariantCulture);
            _mappedBytes[from - FirstMappedChar] = byte.Parse(parts[1], CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Access 97's codes for text in <paramref name="codePage"/>: those of <paramref name="access97"/>, for the bytes of the text in it.</summary>
    private TextIndexCodes(TextIndexCodes access97, Encoding codePage)
    {
        _isAccess97     = true;
        _basicCodes     = access97._basicCodes;
        _extCodes       = access97._extCodes;
        _mappedBytes    = access97._mappedBytes;
        _codePage       = codePage;
        _asCodePage1252 = new System.Collections.Concurrent.ConcurrentDictionary<char, string?>();
    }

    /// <summary>For Access 97's General order, the code page of the text whose bytes it keys.</summary>
    public int CodePage => _codePage?.CodePage ?? CodePage1252.CodePage;

    /// <summary>True for Access 97's General order, in any code page.</summary>
    public bool IsAccess97 => _isAccess97;

    // Code page 1252, through the Access 97 format, which registers the code pages .NET has only from a provider.
    private static Encoding CodePage1252 => JetFormat.Jet3.TextEncoding;

    /// <summary>
    /// <paramref name="value"/> as the characters of code page 1252 that have its bytes in this order's code page,
    /// each character that code page has not as <see cref="NotInCodePageMark"/>; the value itself in code page 1252.
    /// </summary>
    private string AsCodePage1252(string value)
    {
        if (_codePage is null) return value;
        var text = new StringBuilder(value.Length);
        foreach (char c in value) text.Append(InCodePage1252(c) ?? NotInCodePageMark.ToString());
        return text.ToString();
    }

    // The characters of code page 1252 with the bytes c has in this order's code page, or null for a character the code
    // page has not: one whose bytes do not read back as it, a character Windows' best fit or a question mark stands for.
    private string? InCodePage1252(char c)
        => _asCodePage1252!.GetOrAdd(c, ch =>
        {
            byte[] bytes = _codePage!.GetBytes(new[] { ch });
            string back = _codePage.GetString(bytes);
            return back.Length == 1 && back[0] == ch ? CodePage1252.GetString(bytes) : null;
        });

    /// <summary>
    /// The codes of the order a column sorts in, or null when this library does not make that order's keys. A
    /// locale's order whose keys are those of General - Legacy has its codes (<see cref="SortOrder.KeysAsGeneralLegacy"/>).
    /// </summary>
    public static TextIndexCodes? For(SortOrder order)
        => order.KeysAsGeneralLegacy ? GeneralLegacy
         : order.IsGeneral ? General
         : order.IsAccess97General ? Access97In(order.CodePage)
         : null;

    /// <summary>
    /// False for a value whose key <see cref="Encode"/> refuses to make: in General, one with a character Access
    /// keys by the characters around it (ー, 々 and the like) or, in Access 97's order, with a character the code
    /// page of its text (1252 unless the file is read in another) has not.
    /// </summary>
    public bool CanEncode(string value) => !ToIndexCharSequence(AsCodePage1252(value)).Any(c => CodesOf(c) is { Mark: MarkKind.Unkeyed } or { IsNotInCodePage: true });

    /// <summary>Why <see cref="CanEncode"/> is false for <paramref name="value"/>, to follow "No index key is made for ".</summary>
    public string WhyNot(string value)
    {
        if (_codePage is not null)
            foreach (char c in ToIndexCharSequence(value))
                if (InCodePage1252(c) is null)
                    return $"a value with U+{(int)c:X4}: this Access 97 file's text is in code page {_codePage.CodePage}, which has no such character";
        foreach (char c in ToIndexCharSequence(value))
        {
            var codes = CodesOf(c);
            if (codes.Mark == MarkKind.Unkeyed) return $"a value with U+{(int)c:X4}: Access keys that character by the ones around it";
            if (codes.IsNotInCodePage) return $"a value with U+{(int)c:X4}: Access 97 keeps text in code page 1252, which has no such character";
        }
        return "this value";
    }

    /// <summary>The key of <paramref name="value"/>, without the flag byte an index entry starts each column with.</summary>
    /// <exception cref="NotSupportedException">The value is one <see cref="CanEncode"/> is false for.</exception>
    public byte[] Encode(string value, bool ascending)
    {
        if (_codePage is not null && !CanEncode(value))
            throw new NotSupportedException($"No index key is made for {WhyNot(value)}.");
        return EncodeChars(ToIndexCharSequence(AsCodePage1252(value)), ascending);
    }

    /// <summary>
    /// The key of an object's name, as Access compares names in this order (<see cref="ObjectNames"/>): the key an
    /// index entry has for it, its trailing spaces dropped as a value's are, or with <paramref name="whole"/> the key
    /// of the whole name, its trailing spaces kept. Null for a name with a character Access keys by the characters
    /// around it (<see cref="IsContextDependent"/>) or, in Access 97's order, one the code page of its text has not.
    /// </summary>
    internal byte[]? NameKey(string name, bool whole)
    {
        name = AsCodePage1252(name);
        string str = whole ? (name.Length > MaxTextIndexCharLength ? name.Substring(0, MaxTextIndexCharLength) : name) : ToIndexCharSequence(name);
        foreach (char c in str)
            if (CodesOf(c) is { IsContextDependent: true } or { IsNotInCodePage: true }) return null;
        return EncodeChars(str, ascending: true);
    }

    /// <summary>
    /// Whether Access keys <paramref name="c"/> by the characters around it in this order: ー, 々 and the like. In
    /// General - Legacy a value's key is made with such a character (<see cref="Encode"/>); a name with one is
    /// compared by its readings (<see cref="ObjectNames"/>), which DAO was measured by, and not by that key.
    /// </summary>
    internal bool IsContextDependent(char c) => CodesOf(c).IsContextDependent;

    // The key of text that is already as a key takes it: no longer than an entry holds, and trimmed or not.
    private byte[] EncodeChars(string str, bool ascending)
    {
        if (_isAccess97) return EncodeAccess97(str, ascending);

        var key = new List<byte>(str.Length * 2 + 8);
        ExtraCodes? extraCodes = null;
        List<byte>? unprintableCodes = null;
        List<byte>? crazyCodes = null;
        List<byte>? kanaMarkCodes = null;
        int charOffset = 0;         // the place of the character among the extra codes
        int unprintableOffset = 0;  // and where an unprintable character's place is told
        CharCodes? repeated = null; // the character a mark after it repeats or lengthens

        foreach (char c in str)
        {
            var codes = CodesOf(c);
            if (codes.Mark == MarkKind.Unkeyed)
                throw new NotSupportedException($"No index key is made for {WhyNot(str)}.");
            if (codes.Mark != MarkKind.None)
                codes = MarkCodes(codes, repeated);
            else if (codes.Inline is not null || codes.Unprintable is not null)
                repeated = codes;

            int curCharOffset = charOffset;
            int curUnprintableOffset = unprintableOffset;

            if (codes.Inline is not null)
            {
                key.AddRange(codes.Inline);
                charOffset += codes.Places;
                unprintableOffset += codes.UnprintablePlaces;
            }

            if (codes.IsSimple) continue;

            if (codes.Extra is not null || codes.ExtraModifier is not null)
                (extraCodes ??= new ExtraCodes()).Add(curCharOffset, codes.Extra, codes.ExtraModifier ?? 0);

            if (codes.Unprintable is not null)
                AddUnprintableCodes(curUnprintableOffset, codes.Unprintable, unprintableCodes ??= new List<byte>(), extraCodes);

            if (codes.CrazyFlag != 0)
            {
                (crazyCodes ??= new List<byte>()).Add(codes.CrazyFlag);
                (kanaMarkCodes ??= new List<byte>()).Add(codes.KanaMarkCode);
            }
        }

        key.Add(EndText);

        bool hasExtra   = extraCodes is not null && extraCodes.TrimPlaceholders();
        bool hasUnprint = unprintableCodes is not null;
        bool hasCrazy   = crazyCodes is not null;

        if (hasExtra)
            key.AddRange(extraCodes!.Bytes);

        if (hasCrazy || hasUnprint)
        {
            key.Add(EndText);
            key.Add(EndText);

            if (hasCrazy)
            {
                AddCrazyCodes(crazyCodes!, kanaMarkCodes!, key);
                if (hasUnprint)
                    key.Add(CrazyCodesUnprintSuffix);
            }

            if (hasUnprint)
            {
                key.Add(EndText);
                key.AddRange(unprintableCodes!);
            }
        }

        // A descending key is the ascending one with another end marker, every byte flipped.
        if (!ascending)
        {
            key.Add(EndExtraText);
            for (int i = 0; i < key.Count; i++)
                key[i] = (byte)~key[i];
        }

        key.Add(EndExtraText);
        return key.ToArray();
    }

    private const byte Access97ExtraBounds = 0x00;

    /// <summary>
    /// An Access 97 key (Jackcess's <c>General97IndexCodes</c>, and ACE 12's keys where they differ): the inline
    /// bytes of each character, and then either one zero byte or, when a character has an accent, the accents as
    /// half bytes - a zero, then for each accented character a placeholder (2) for every letter since the last
    /// that could have had an accent and had none (<see cref="CharCodes.Places"/>), and its own code; a zero
    /// ends them, and the last byte is filled out with another. A descending key is the same bytes flipped.
    /// </summary>
    private byte[] EncodeAccess97(string str, bool ascending)
    {
        var key = new List<byte>(str.Length + 4);
        List<byte>? accents = null;
        int lettersSince = 0;

        foreach (char c in str)
        {
            var codes = CodesOf(c);
            if (codes.IsNotInCodePage)
                throw new NotSupportedException($"No index key is made for {WhyNot(str)}.");

            if (codes.Inline is not null) key.AddRange(codes.Inline);
            if (codes.Extra is null)
            {
                lettersSince += codes.Places;
                continue;
            }
            accents ??= new List<byte> { Access97ExtraBounds };
            accents.AddRange(Enumerable.Repeat(InternationalExtraPlaceholder, lettersSince));
            accents.Add(codes.Extra[0]);
            lettersSince = 0;
        }

        if (accents is null) key.Add(EndExtraText);
        else
        {
            accents.Add(Access97ExtraBounds);
            for (int i = 0; i < accents.Count; i += 2)
                key.Add((byte)(accents[i] << 4 | (i + 1 < accents.Count ? accents[i + 1] & 0x0F : 0)));
        }

        if (!ascending)
            for (int i = 0; i < key.Count; i++)
                key[i] = (byte)~key[i];
        return key.ToArray();
    }

    private static string ToIndexCharSequence(string? value)
    {
        string str = value ?? string.Empty;
        if (str.Length > MaxTextIndexCharLength)
            str = str.Substring(0, MaxTextIndexCharLength);
        // Trailing spaces are ignored for text index entries.
        return str.TrimEnd(' ');
    }

    // ── A character's codes ───────────────────────────────────────────────────

    private CharCodes CodesOf(char c)
    {
        if (c < FirstExtChar)
        {
            // Code page 1252 gives the bytes 0x80..0x9F to characters beyond U+00FF, all but five it has no
            // character for: those five are read as the control characters of their number, and the other
            // control characters U+0080..U+009F are in no value of an Access 97 file.
            if (_isAccess97 && (int)c is >= 0x80 and <= 0x9F and not (0x81 or 0x8D or 0x8F or 0x90 or 0x9D))
                return NotInCodePage;
            return _basicCodes[c];
        }

        if (_isAccess97)
        {
            byte mapped = c is >= (char)FirstMappedChar and <= (char)LastMappedChar ? _mappedBytes[c - FirstMappedChar] : (byte)0;
            return mapped == 0 ? NotInCodePage : _basicCodes[mapped];
        }

        // The extended file leaves the surrogates out: they have no line of their own.
        if (c >= FirstHighSurrogate && c <= LastLowSurrogate)
            return _isGeneral ? SurrogateCodes(c) : Ignored;

        return _extCodes.Value[ExtIndex(c)];
    }

    /// <summary>Where a character from U+0100 on, not a surrogate, is in the extended codes.</summary>
    private static int ExtIndex(char c) => c - FirstExtChar - (c > LastLowSurrogate ? SurrogateRangeCount : 0);

    /// <summary>
    /// The codes of half a surrogate pair in the General order, which weighs each half by its place: the high
    /// surrogates in three runs (a fourth, U+D880..U+DB7F, has no weight), the low ones by their place in the block.
    /// General - Legacy leaves both halves out of a key.
    /// </summary>
    private static CharCodes SurrogateCodes(char c)
    {
        int weight;
        byte extra = 0x3F;
        if (c >= FirstLowSurrogate)
        {
            int offset = c - FirstLowSurrogate;
            weight = c - (offset < 8 ? 9992 : offset < 8 + 254 ? 9990 : offset < 8 + 254 * 2 ? 9988 : offset < 8 + 254 * 3 ? 9986 : 9984);
        }
        else if (c < 0xD840) weight = c - 10238;
        else if (c < 0xD880) { weight = c + 9666; extra = 0x3E; }
        else if (c < 0xDB80) return Ignored;
        else { weight = c + 9090; extra = 0x3E; }

        return new CharCodes { Inline = new[] { (byte)(weight >> 8), (byte)weight }, Extra = new[] { extra } };
    }

    /// <summary>
    /// What one character adds to a key: bytes in the text itself, and codes in the sections after it. A simple
    /// character has only the first.
    /// </summary>
    private sealed class CharCodes
    {
        public byte[]? Inline { get; init; }
        public byte[]? Extra { get; init; }
        public byte[]? Unprintable { get; init; }

        /// <summary>What the character adds to the extra code before it; 0 still starts the extra codes when none is.</summary>
        public byte? ExtraModifier { get; init; }
        public byte CrazyFlag { get; init; }

        /// <summary>
        /// For a kana (<see cref="CrazyFlag"/>), its code in the run after the kana's packed codes: 1 for a kana,
        /// and for a mark after one 2 when it repeats it and 3 when it lengthens it (<see cref="MarkCodes"/>).
        /// </summary>
        public byte KanaMarkCode { get; init; } = KanaCode;

        public bool IsSimple { get; init; }

        /// <summary>Whether the character is a mark Access keys by the character before it, and how.</summary>
        public MarkKind Mark { get; init; }

        /// <summary>A character Access keys by the characters around it, so that no codes of its own make its key.</summary>
        public bool IsContextDependent => Mark != MarkKind.None;

        /// <summary>In Access 97's order, a character code page 1252 has not: no value of an Access 97 file holds it.</summary>
        public bool IsNotInCodePage { get; init; }

        /// <summary>
        /// The places the character takes among the extra codes: one, except for a character Access sorts as two or
        /// three letters (Æ as AE, ß as ss, ﬃ as ffi), which takes one per letter. In Access 97's order, the
        /// places it takes among the accents when it has none: one for each of its letters that can have one.
        /// </summary>
        public int Places { get; set; } = 1;

        /// <summary>The places it takes where an unprintable character's place is told.</summary>
        public int UnprintablePlaces { get; set; } = 1;
    }

    /// <summary>How Access keys a mark by the character before it.</summary>
    private enum MarkKind : byte
    {
        /// <summary>Not such a mark: the character's own codes make its key.</summary>
        None,

        /// <summary>A mark keyed as the character before it, with its own extra code: 々, ゝ, ゞ, the shadda.</summary>
        Repeat,

        /// <summary>The prolonged sound mark ー and its half-width form: after a kana, that kana's vowel.</summary>
        Lengthen,

        /// <summary>A mark keyed by the characters around it whose key this library does not make, in General.</summary>
        Unkeyed,
    }

    private const byte KanaCode           = 1;
    private const byte KanaRepeatCode     = 2;
    private const byte KanaLengthenCode   = 3;
    private const byte UnprintableScript  = 0x06;
    private const byte KanaScript         = 0x7F;
    private const byte FirstKanaVowel     = 0x02;
    private const byte KanaRowLength      = 8;
    private const byte KanaN              = 0x80;

    // A mark with no character before it to repeat, or after a character Access sorts as several letters.
    private static readonly CharCodes UnrepeatedMark = new() { Inline = new byte[] { 0xFF, 0xFF }, Places = 0, IsSimple = true };

    /// <summary>
    /// The codes of a mark Access keys by <paramref name="repeated"/>, the last character before it that is not such a
    /// mark and has a weight of its own - a character Access leaves out (a surrogate) or one that only changes the
    /// accent before it (the half-width voiced sound mark) is passed over. As ACE 16 keyed the mark after each character
    /// of the BMP (tools/ace-mark-keys.ps1):
    /// <list type="bullet">
    /// <item>with none before it, or after a character Access sorts as several letters (Æ, ß, ǅ, ﬃ), the mark weighs
    /// FF FF, takes no place among the accents and has no extra code;</item>
    /// <item>otherwise it weighs what that character weighs - an unprintable character's weight is 06 and its code -
    /// and takes a place with its own extra code (々 05, ゞ 03), not the character's;</item>
    /// <item>ー and ｰ after a kana weigh that kana's vowel: the kana of a row are a, i, u, e, o from its first code,
    /// eight codes to a row, and ン is itself;</item>
    /// <item>after a kana the mark is a kana too, small or not as it is, and its code in the run after the kana's codes
    /// is 2 for a mark that repeats and 3 for one that lengthens.</item>
    /// </list>
    /// </summary>
    private static CharCodes MarkCodes(CharCodes mark, CharCodes? repeated)
    {
        if (repeated is null || repeated.Places > 1 || repeated.Extra is { Length: > 1 }) return UnrepeatedMark;

        byte[] inline = repeated.Unprintable is { } unprintable ? new[] { UnprintableScript, unprintable[0] } : repeated.Inline!;
        if (repeated.CrazyFlag == 0) return new CharCodes { Inline = inline, Extra = mark.Extra };

        if (mark.Mark == MarkKind.Lengthen && inline[0] == KanaScript && inline[1] < KanaN)
            inline = new[] { KanaScript, (byte)(FirstKanaVowel + (inline[1] - FirstKanaVowel) % KanaRowLength) };
        return new CharCodes
        {
            Inline = inline,
            Extra = mark.Extra,
            CrazyFlag = repeated.CrazyFlag,
            KanaMarkCode = mark.Mark == MarkKind.Lengthen ? KanaLengthenCode : KanaRepeatCode,
        };
    }

    // ── The sections after the text ───────────────────────────────────────────

    /// <summary>
    /// The extra codes of a key: one run per character that has any, placeholders for the characters before it
    /// that have none, and the modifiers some unprintable characters add to the run before them.
    /// </summary>
    private sealed class ExtraCodes
    {
        public List<byte> Bytes { get; } = new();

        /// <summary>The characters the codes stand for so far.</summary>
        public int CharCount { get; private set; }

        /// <summary>1 when the codes start with a modifier that had no run to change, which is no character.</summary>
        public int UnprintablePrefixLength { get; private set; }

        public void Add(int charOffset, byte[]? codes, byte modifier)
        {
            if (CharCount < charOffset)
            {
                Bytes.AddRange(Enumerable.Repeat(InternationalExtraPlaceholder, charOffset - CharCount));
                CharCount = charOffset;
            }

            if (codes is not null)
            {
                Bytes.AddRange(codes);
                CharCount++;
            }
            else if (Bytes.Count > 0)
            {
                Bytes[Bytes.Count - 1] += modifier;
            }
            else
            {
                Bytes.Add(modifier);
                UnprintablePrefixLength = 1;
            }
        }

        /// <summary>Drops the placeholders and zeros at the end; false when nothing is left.</summary>
        public bool TrimPlaceholders()
        {
            int length = Bytes.Count;
            while (length > 0 && Bytes[length - 1] <= InternationalExtraPlaceholder) length--;
            Bytes.RemoveRange(length, Bytes.Count - length);
            return length > 0;
        }
    }

    private static void AddUnprintableCodes(int charOffset, byte[] codes, List<byte> unprintableCodes, ExtraCodes? extraCodes)
    {
        int unprintCharOffset = charOffset;
        if (extraCodes is not null)
            unprintCharOffset += extraCodes.Bytes.Count - extraCodes.CharCount - extraCodes.UnprintablePrefixLength;

        int offset = UnprintableCountStart + UnprintableCountMultiplier * unprintCharOffset | UnprintableOffsetFlags;

        unprintableCodes.Add((byte)(offset >> 8));
        unprintableCodes.Add((byte)offset);
        unprintableCodes.Add(UnprintableMidfix);
        unprintableCodes.AddRange(codes);
    }

    private static void AddCrazyCodes(List<byte> crazyCodes, List<byte> kanaMarkCodes, List<byte> key)
    {
        // The trailing CRAZY_CODE_2s are left out, and the rest packed three to a byte.
        AddPacked(crazyCodes, CrazyCode2, key);

        // The suffix repeats once for every seven crazy characters in the value, wherever they are, counted
        // before the trim. Between its start and its midfix are the codes of the marks after kana, packed as the
        // crazy codes are, a kana's own code 1 and the trailing ones left out: none in a value without such a mark.
        int repeats = (crazyCodes.Count + CrazyCodesPerSuffixRepeat - 1) / CrazyCodesPerSuffixRepeat;
        key.Add(CrazyCodesSuffixStart);
        AddPacked(kanaMarkCodes, KanaCode, key);
        key.Add(CrazyCodesSuffixMidfix);
        key.AddRange(Enumerable.Repeat(CrazyCodesSuffixRepeat, repeats));
        key.Add(CrazyCodesSuffixStart);
        key.AddRange(Enumerable.Repeat(CrazyCodesSuffixRepeat, repeats));
    }

    // Two-bit codes three to a byte after a set top bit, those equal to trailing left out at the end.
    private static void AddPacked(List<byte> codes, byte trailing, List<byte> key)
    {
        int length = codes.Count;
        while (length > 0 && codes[length - 1] == trailing) length--;

        byte packed = CrazyCodeStart;
        int inByte = 0;
        for (int i = 0; i < length; i++)
        {
            packed |= (byte)(codes[i] << (2 - inByte) * 2);
            if (++inByte < 3) continue;
            key.Add(packed);
            packed = CrazyCodeStart;
            inByte = 0;
        }
        if (inByte > 0) key.Add(packed);
    }

    // ── Codes file loader ─────────────────────────────────────────────────────

    private static CharCodes[] LoadCodes(string resourceName, int count)
    {
        string resourcePath = $"JackcessDotNet.Resources.{resourceName}";
        using var stream = typeof(TextIndexCodes).Assembly.GetManifestResourceStream(resourcePath)
            ?? throw new InvalidOperationException($"Missing embedded resource '{resourcePath}'.");
        using var reader = new StreamReader(stream, Encoding.ASCII);

        var codes = new CharCodes[count];
        for (int i = 0; i < count; i++)
        {
            string line = reader.ReadLine()
                ?? throw new InvalidDataException($"'{resourceName}' ends at line {i + 1} of {count}.");
            codes[i] = ParseCodeLine(line);
        }
        return codes;
    }

    /// <summary>
    /// One line of a codes file: a letter for the kind of character, then its codes in hex, separated by commas.
    /// A malformed line throws: the files are embedded, so it is a build defect, and treating the character as
    /// ignored would write keys Access cannot find.
    /// </summary>
    private static CharCodes ParseCodeLine(string line)
    {
        if (line.Length == 0) throw new InvalidDataException("Empty index code line.");
        string[] parts = line.Substring(1).Split(',');

        return line[0] switch
        {
            'X' => Ignored,
            // Simple: inline bytes only.
            'S' => new CharCodes { Inline = HexToBytes(parts[0]), IsSimple = true },
            // Significant: inline bytes, and a place among the extra codes.
            'G' => new CharCodes { Inline = HexToBytes(parts[0]) },
            'U' => new CharCodes { Unprintable = HexToBytes(parts[0]) },
            // A modifier of the extra code before it, which may be 0.
            'P' => new CharCodes { ExtraModifier = HexToBytes(parts[0])[0] },
            'I' => new CharCodes { Inline = HexToBytes(parts[0]), Extra = HexToBytes(parts[1]) },
            // The extra codes may be empty, and then the character adds none (not an empty run, which would
            // still count as a character in the extra codes and shift every later one).
            'Z' => new CharCodes
            {
                Inline = HexToBytes(parts[0]),
                Extra = parts[1].Length == 0 ? null : HexToBytes(parts[1]),
                CrazyFlag = parts[2] == "1" ? CrazyCode1 : CrazyCode2,
            },
            _ => throw new InvalidDataException($"Unknown index code line '{line}'."),
        };
    }

    /// <summary>
    /// The files drop a code's leading zero, so an odd number of digits gets one back. "C" is 0x0C, "S7" (the
    /// space) is 0x07, and "803" is 0x08 0x03. An empty code is a defect.
    /// </summary>
    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length == 0)
            throw new InvalidDataException("Empty index code.");
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return result;
    }
}
