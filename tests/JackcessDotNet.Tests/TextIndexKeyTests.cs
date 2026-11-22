using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The key a text value gets in an index must be the one Access writes, byte for
/// byte, or Access cannot find the row (or the table, when the value is a table
/// name in MSysObjects' ParentIdName index). The files under IndexKeys hold the keys
/// ACE 16 wrote, ascending and descending, in each sort order the engine writes:
///
///   general-legacy-ace16.tsv       General - Legacy: every Latin-1 character alone and in three settings, the
///                                  characters Access sorts as two or three letters, a sample of the rest of the
///                                  BMP, supplementary characters, and names (1,863 values).
///   general-legacy-more-ace16.tsv  General - Legacy: what the first file does not hold - values with many kana, keys
///                                  longer than an entry holds, and the characters Access keys by their neighbours.
///   general-legacy-marks-ace16.tsv General - Legacy: the twelve marks Access keys by the character before them
///                                  (ー, 々, ゝ, the shadda), after characters of every kind (tools/ace-mark-keys.ps1).
///   general-ace16.tsv              General (Access 2010 and later): the first file's values, and the second's
///                                  kinds, with surrogates alone and in pairs, which that order weighs.
///
///   access97-ace12.tsv             Access 97's General order, in which the engine searches and does not write:
///   access97-more-ace12.tsv        the keys the 32-bit ACE 12.0 wrote into a Jet3 file (Jet 4.0 writes the
///                                  same). Access97TextKeyTests reads them.
///
/// Each value was indexed by ACE and its entries read back, by tools/ace-text-keys.ps1. A value with a character
/// Access keys by the characters around it is in the files with ACE's key: in General the engine must refuse it.
/// </summary>
public sealed class TextIndexKeyTests
{
    private static TextIndexCodes Legacy => TextIndexCodes.GeneralLegacy;

    private static TextIndexCodes General => TextIndexCodes.General;

    public static TheoryData<string, string, int, int> GoldenFiles() => new()
    {
        { "general-legacy-ace16.tsv", "General - Legacy", 1863, 0 },
        { "general-legacy-more-ace16.tsv", "General - Legacy", 337, 0 },
        { "general-legacy-marks-ace16.tsv", "General - Legacy", 6163, 0 },
        { "general-ace16.tsv", "General", 2328, 79 },
    };

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Every_value_encodes_as_ACE_16_wrote_it(string file, string order, int values, int refused)
    {
        var codes = order == "General" ? General : Legacy;
        var failures = new List<string>();
        int checkedValues = 0, refusedValues = 0;
        foreach (var (value, codePoints, ascending, descending) in GoldenKeys(file))
        {
            checkedValues++;
            if (!codes.CanEncode(value))
            {
                refusedValues++;
                Assert.Throws<NotSupportedException>(() => codes.Encode(value, ascending: true));
                continue;
            }
            string asc  = Hex(IndexKeys.Truncate(IndexKeys.EncodeText(codes, ascending: true, value)));
            string desc = Hex(IndexKeys.Truncate(IndexKeys.EncodeText(codes, ascending: false, value)));
            if (asc != ascending)   failures.Add($"{codePoints} ascending: ACE {ascending}, engine {asc}");
            if (desc != descending) failures.Add($"{codePoints} descending: ACE {descending}, engine {desc}");
        }

        Assert.Equal(values, checkedValues);
        Assert.True(failures.Count == 0,
            $"{failures.Count} keys differ from ACE's:\n" + string.Join("\n", failures.Take(25)));
        Assert.Equal(refused, refusedValues);
    }

    // ── Both orders ─────────────────────────────────────────────────────────

    // The codes files drop a code's leading zero: '#' is "SC", 0x0C. Read as no
    // bytes at all, '#', '$' and the space vanished from keys.
    [Fact]
    public void A_code_written_without_its_leading_zero_gets_it_back()
    {
        Assert.Equal("0C0100", Hex(Legacy.Encode("#", ascending: true)));
        Assert.Equal("4A07510102020E00", Hex(Legacy.Encode("a é", ascending: true)));
        Assert.Equal("0E0207020E210102020E00", Hex(General.Encode("a é", ascending: true)));
    }

    // U+3041's line is "Z7F02,,1": no extra codes. Counted as a character with an
    // empty run, it moved the accent of every later character one place left.
    [Fact]
    public void A_character_without_extra_codes_adds_none()
    {
        Assert.Equal("7F025101020E0101A0FF0280FF8000", Hex(Legacy.Encode("\u3041é", ascending: true)));
        Assert.Equal("7F020E2101020E0101A0FF0280FF8000", Hex(General.Encode("\u3041é", ascending: true)));
    }

    // Access sorts Æ as AE and ﬃ as ffi, and gives each letter its own place in the
    // accents that follow: é after "aÆ" is at the fourth place, not the third.
    [Fact]
    public void A_character_sorted_as_several_letters_takes_a_place_for_each()
    {
        Assert.Equal("4A4A5151010202020E00", Hex(Legacy.Encode("aÆé", ascending: true)));
        Assert.Equal("4A5353595101020202020E00", Hex(Legacy.Encode("a\uFB03é", ascending: true)));
        Assert.Equal("0E020E020E210E21010202020E00", Hex(General.Encode("aÆé", ascending: true)));
        // In General Ü is two letters as well: U and E.
        Assert.Equal("0E020E9F0E210E21010202020E00", Hex(General.Encode("aÜé", ascending: true)));
    }

    // The suffix after the kana's packed codes repeats once for every seven kana in the value, wherever in it
    // they are. With one suffix whatever the count, a value with eight or more kana had a key Access does not write.
    [Fact]
    public void The_suffix_after_kana_repeats_for_every_seven_of_them()
    {
        Assert.EndsWith("010101" + "AAAAA0" + "FF0280FF80" + "00", Hex(Legacy.Encode(new string('\u3041', 7), ascending: true)));
        Assert.EndsWith("010101" + "AAAAA8" + "FF028080FF8080" + "00", Hex(Legacy.Encode(new string('\u3041', 8), ascending: true)));
        Assert.EndsWith("010101" + "AAAAA8" + "FF028080FF8080" + "00", Hex(General.Encode(new string('\u3041', 8), ascending: true)));
        // Not next to each other: four kana, a letter, four more.
        Assert.EndsWith("010101" + "AAAAA8" + "FF028080FF8080" + "00", Hex(Legacy.Encode("ぁぁぁぁxぁぁぁぁ", ascending: true)));
    }

    // An entry's key is at most 510 bytes: Access cuts a longer one there and ends it with two bytes made from
    // what it cut off. 255 of é in General - Legacy is 255 letters, a mark, 255 accents and an end: 513 bytes
    // with its flag.
    [Fact]
    public void A_key_longer_than_an_entry_holds_ends_in_a_digest_of_the_rest()
    {
        byte[] whole = IndexKeys.EncodeText(Legacy, ascending: true, new string('é', 255));
        Assert.Equal(513, whole.Length);

        byte[] key = IndexKeys.Truncate(whole);
        Assert.Equal(IndexKeys.MaxKeyLength, key.Length);
        Assert.Equal(whole.Take(508), key.Take(508));
        Assert.NotEqual(whole.Skip(508).Take(2), key.Skip(508));

        byte[] atTheLimit = whole.Take(IndexKeys.MaxKeyLength).ToArray();
        Assert.Same(atTheLimit, IndexKeys.Truncate(atTheLimit));
    }

    // 々, ー, ゝ and their like repeat or lengthen the character before them, and Access keys them by that
    // character: ー after カ is ア, after a is a, and alone is FF FF. In General those keys are not made.
    [Theory]
    [InlineData("\u30FC")]
    [InlineData("データ")]
    [InlineData("日々")]
    [InlineData("a\uFF70")]
    public void A_value_with_a_character_keyed_by_its_neighbours_is_refused_in_General(string value)
    {
        Assert.False(General.CanEncode(value));
        Assert.Throws<NotSupportedException>(() => General.Encode(value, ascending: true));
        Assert.Throws<NotSupportedException>(() => General.Encode(value, ascending: false));
        Assert.True(Legacy.CanEncode(value));
        Assert.True(General.CanEncode("テスト"));
    }

    // In General - Legacy such a mark weighs what the character before it weighs, with its own accent (々 05) and
    // not that character's; ー after a kana weighs the kana's vowel. With nothing before it, or after a character
    // Access sorts as several letters, it weighs FF FF and takes no place among the accents. After a kana it is a
    // kana too, and the run between the kana's packed codes and their suffix gives it 2 (it repeats) or 3 (it
    // lengthens): the run is empty for kana alone. The keys are ACE 16's (general-legacy-marks-ace16.tsv).
    [Theory]
    [InlineData("ー", "FFFF0100")]                                         // ー alone
    [InlineData("aー", "4A4A0100")]
    [InlineData("éー", "5151010E00")]                                 // é's accent is not repeated
    [InlineData("Æー", "4A51FFFF0100")]                               // after two letters
    [InlineData("-ー", "0682010101018007068200")]                          // after a hyphen, its weight 06 82
    [InlineData("日々", "B756B75601020500")]                           // 々's own accent, 05
    [InlineData("々é", "FFFF51010E00")]                               // no place: é's accent is the first
    [InlineData("々\u0001", "FFFF01010101800B060300")]                     // but a place where a control character's is told
    [InlineData("a\U0001F600々é", "4A4A510102050E00")]                // the pair is passed over
    [InlineData("بّ", "79AC79AC010800")]                             // the shadda after beh
    [InlineData("カー", "7F0A7F02010101FF9C0280FF8000")]               // カ's vowel, ア; the run 1 3
    [InlineData("データ", "7F1D7F057F1A01030101FF9C0280FF8000")]   // デ's vowel, エ, without デ's accent
    [InlineData("ッー", "7F1C7F04010101A8FF9C0280FF8000")]             // small, as ッ is
    [InlineData("ｶﾞー", "7F0A7F0201030101FF9C0280FF8000")]       // ﾞ changes ｶ's accent, and is passed over
    [InlineData("カ々", "7F0A7F0A0102050101FF980280FF8000")]           // the run 1 2
    [InlineData("カ々ー", "7F0A7F0A7F020102050101FF9B0280FF8000")] // each mark by カ: the run 1 2 3
    [InlineData("ュカキクケコサー",
                "7F3C7F0A7F0B7F0C7F0D7F0E7F127F02010101A0FF95959C028080FF808000")]   // eight kana: the suffix twice
    public void A_character_keyed_by_its_neighbours_has_ACE_s_key_in_General_Legacy(string value, string key)
    {
        Assert.True(Legacy.CanEncode(value));
        Assert.Equal(key, Hex(Legacy.Encode(value, ascending: true)));
    }

    // ── General - Legacy ────────────────────────────────────────────────────

    // ACE leaves a surrogate pair out of a General - Legacy key altogether.
    [Fact]
    public void General_Legacy_leaves_a_surrogate_pair_out()
    {
        Assert.Equal(
            Hex(Legacy.Encode("aé", ascending: true)),
            Hex(Legacy.Encode("a\U0001F600é", ascending: true)));
    }

    // ── General ─────────────────────────────────────────────────────────────

    // Each half of a pair has a weight of its own, by its place among the surrogates, and an extra code.
    [Fact]
    public void General_weighs_each_half_of_a_surrogate_pair()
    {
        string plain = Hex(General.Encode("aé", ascending: true));
        string withPair = Hex(General.Encode("a\U0001F600é", ascending: true));
        Assert.Equal("0E020E2101020E00", plain);
        Assert.NotEqual(plain, withPair);
        Assert.StartsWith("0E02", withPair);
        Assert.EndsWith("0E21" + "01" + "023F3F0E" + "00", withPair);
    }

    // An ideograph's four bytes are one letter among the accents, and two where a control character's place is
    // told: 0x13 for the control character after é and 一, where two letters would give 0x0F.
    [Fact]
    public void General_counts_an_ideograph_once_among_accents_and_twice_before_a_control_character()
    {
        Assert.Equal("0E02" + "FDFF3C6A" + "0E21" + "01" + "02020E" + "00", Hex(General.Encode("a\u4E00é", ascending: true)));
        Assert.Equal("0E21" + "FDFF3C6A" + "FDFF3C6A" + "01" + "0E" + "010101" + "8013" + "0603" + "00",
            Hex(General.Encode("é\u4E00\u0001\u4E00", ascending: true)));
    }

    // Where ACE's key is not the one Jackcess's codes give: marks that are no character of their own but change
    // the accent before them by nothing (and so start the accents when nothing is before them), by -1 and by -2,
    // and Lao marks whose accent is 0.
    [Fact]
    public void The_characters_ACE_keys_otherwise_than_the_codes_files_are_keyed_as_ACE_keys_them()
    {
        Assert.Equal("0E2101000E00", Hex(General.Encode("\u0E4Cé", ascending: true)));
        Assert.Equal("0E020E2101020E00", Hex(General.Encode("a\u0E4Cé", ascending: true)));
        Assert.Equal("01FF00", Hex(General.Encode("\u1DCA", ascending: true)));
        Assert.Equal("0E020E2101010E00", Hex(General.Encode("a\u1DCAé", ascending: true)));
        Assert.Equal("0E020E2101000E00", Hex(General.Encode("a\u1DFFé", ascending: true)));
        Assert.Equal("0E02410E210102000E00", Hex(General.Encode("a\u0EC6é", ascending: true)));
        Assert.Equal("5101000E00", Hex(Legacy.Encode("\u0E4Cé", ascending: true)));
    }

    private static IEnumerable<(string Value, string CodePoints, string Ascending, string Descending)> GoldenKeys(string file)
    {
        using var stream = typeof(TextIndexKeyTests).Assembly
            .GetManifestResourceStream("JackcessDotNet.Tests." + file)
            ?? throw new InvalidOperationException($"The golden keys resource {file} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t');
            var value = new StringBuilder();
            foreach (string cp in fields[0].Split(' '))
            {
                // A surrogate on its own is a code unit, not a code point.
                int codePoint = int.Parse(cp, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                value.Append(codePoint is >= 0xD800 and <= 0xDFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint));
            }
            yield return (value.ToString(), fields[0], fields[1], fields[2]);
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
