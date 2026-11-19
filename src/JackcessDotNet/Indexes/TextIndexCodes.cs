using System.Globalization;
using System.Text;

namespace JackcessDotNet;

/// <summary>
/// Makes the bytes Access keeps for a text value in an index entry, in one of the sort orders whose keys this
/// library writes: <see cref="GeneralLegacy"/> (Access 2000 to 2007) and <see cref="General"/> (Access 2010 and
/// later). The two differ only in the codes each character has; the layout of a key is the same.
///
/// The codes are Jackcess's (<c>GeneralLegacyIndexCodes</c> and <c>GeneralIndexCodes</c>), one line per character
/// in embedded resources: U+0000..U+00FF in one file, read at once, and U+0100..U+FFFF less the surrogates in
/// another, read on first use. A code in those files drops its leading zero ("C" is 0x0C). Where Access writes a
/// key those codes do not give, Access is followed: the golden files of ACE's own keys in the tests say where.
///
/// A key's layout:
///   [inline bytes per char]
///   END_TEXT (0x01)
///   [extra/international bytes section, if any]
///   [END_TEXT × 2 if crazy or unprintable]
///   [crazy codes packed into 6-bit groups, if any]
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
        contextDependent: "\u0651\u3005\u3031\u3032\u309D\u309E\u30FC\u30FD\u30FE\uFE7C\uFE7D\uFF70",
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

    private readonly CharCodes[] _basicCodes;
    private readonly Lazy<CharCodes[]> _extCodes;
    private readonly bool _isGeneral;

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
    /// The characters ACE keys by the characters around them, whose keys this library therefore does not make: the
    /// marks that repeat or lengthen the character before them (々, ー, ゝ and the like), and in General four Arabic
    /// ligatures.
    /// </param>
    /// <param name="twoLetters">
    /// In General - Legacy, the characters Access sorts as two letters (Æ as AE, ß as ss).
    /// </param>
    /// <param name="threeLetters">
    /// In General - Legacy, the characters Access sorts as three letters (ﬃ as ffi).
    /// </param>
    private TextIndexCodes(
        string basicResource, string extResource, bool isGeneral, Dictionary<char, string> corrections,
        string contextDependent, string twoLetters = "", string threeLetters = "")
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
                codes[IndexOf(c)] = new CharCodes { IsContextDependent = true };

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

    /// <summary>
    /// The codes of the order a column sorts in, or null when this library does not write that order's keys.
    /// </summary>
    public static TextIndexCodes? For(SortOrder order)
        => order.IsGeneralLegacy ? GeneralLegacy
         : order.IsGeneral ? General
         : null;

    /// <summary>
    /// False for a value with a character Access keys by the characters around it (ー, 々 and the like), whose key
    /// <see cref="Encode"/> therefore refuses to make.
    /// </summary>
    public bool CanEncode(string value) => !ToIndexCharSequence(value).Any(c => CodesOf(c).IsContextDependent);

    /// <summary>The key of <paramref name="value"/>, without the flag byte an index entry starts each column with.</summary>
    /// <exception cref="NotSupportedException">The value is one <see cref="CanEncode"/> is false for.</exception>
    public byte[] Encode(string value, bool ascending)
    {
        string str = ToIndexCharSequence(value);

        var key = new List<byte>(str.Length * 2 + 8);
        ExtraCodes? extraCodes = null;
        List<byte>? unprintableCodes = null;
        List<byte>? crazyCodes = null;
        int charOffset = 0;         // the place of the character among the extra codes
        int unprintableOffset = 0;  // and where an unprintable character's place is told

        foreach (char c in str)
        {
            var codes = CodesOf(c);
            if (codes.IsContextDependent)
                throw new NotSupportedException(
                    $"No index key is made for a value with U+{(int)c:X4}: Access keys that character by the ones around it.");

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
                (crazyCodes ??= new List<byte>()).Add(codes.CrazyFlag);
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
                AddCrazyCodes(crazyCodes!, key);
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
        if (c < FirstExtChar) return _basicCodes[c];

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
        public bool IsSimple { get; init; }

        /// <summary>A character Access keys by the characters around it, so that no codes of its own make its key.</summary>
        public bool IsContextDependent { get; init; }

        /// <summary>
        /// The places the character takes among the extra codes: one, except for a character Access sorts as two or
        /// three letters (Æ as AE, ß as ss, ﬃ as ffi), which takes one per letter.
        /// </summary>
        public int Places { get; set; } = 1;

        /// <summary>The places it takes where an unprintable character's place is told.</summary>
        public int UnprintablePlaces { get; set; } = 1;
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

    private static void AddCrazyCodes(List<byte> crazyCodes, List<byte> key)
    {
        // The trailing CRAZY_CODE_2s are left out, and the rest packed three to a byte.
        int length = crazyCodes.Count;
        while (length > 0 && crazyCodes[length - 1] == CrazyCode2) length--;

        byte packed = CrazyCodeStart;
        int inByte = 0;
        for (int i = 0; i < length; i++)
        {
            packed |= (byte)(crazyCodes[i] << (2 - inByte) * 2);
            if (++inByte < 3) continue;
            key.Add(packed);
            packed = CrazyCodeStart;
            inByte = 0;
        }
        if (inByte > 0) key.Add(packed);

        // The suffix repeats once for every seven crazy characters in the value, wherever they are, counted
        // before the trim.
        int repeats = (crazyCodes.Count + CrazyCodesPerSuffixRepeat - 1) / CrazyCodesPerSuffixRepeat;
        key.Add(CrazyCodesSuffixStart);
        key.Add(CrazyCodesSuffixMidfix);
        key.AddRange(Enumerable.Repeat(CrazyCodesSuffixRepeat, repeats));
        key.Add(CrazyCodesSuffixStart);
        key.AddRange(Enumerable.Repeat(CrazyCodesSuffixRepeat, repeats));
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
