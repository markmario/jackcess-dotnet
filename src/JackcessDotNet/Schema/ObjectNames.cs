using System.Text;

namespace JackcessDotNet;

/// <summary>
/// Whether two names of a database's objects of one kind - its tables, a table's columns or indexes, its
/// relationships - are one to Access, which refuses an object a name another of its kind has. Measured with DAO, in an
/// .mdb and an .accdb: Access does not tell apart names that differ only in case, in any script; in the width of a
/// character of the Halfwidth and Fullwidth Forms; as hiragana and katakana; in an accent written as one character or
/// as two; or as ß, æ, œ, þ, ĳ or a Latin ligature and the letters they are written for. It does tell apart names that
/// differ in an accent (e and é), the dotted and dotless i's (I and ı, i and İ), and spaces. Names this library looks
/// up without regard to case (<see cref="StringComparison.OrdinalIgnoreCase"/>) are one as well, so that no two of its
/// columns are taken for each other. Worked out the same on every target: neither .NET's comparison of text (ICU) nor
/// .NET Framework's (NLS) is Access's.
/// </summary>
internal static class ObjectNames
{
    /// <summary>Whether <paramref name="first"/> and <paramref name="second"/> are one name to Access.</summary>
    internal static bool Same(string first, string second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Key(first), Key(second), StringComparison.Ordinal);

    // A name as Access compares it, as far as was measured.
    private static string Key(string name)
    {
        // A character of the Halfwidth and Fullwidth Forms as the character it is a form of: a halfwidth katakana
        // and its voiced mark are then one character, once the name is composed.
        var widths = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c >= '！' && c <= '￮') widths.Append(c.ToString().Normalize(NormalizationForm.FormKC));
            else widths.Append(c);
        }

        string composed = Composed(widths.ToString());
        var key = new StringBuilder(composed.Length);
        foreach (char c in composed)
        {
            // Where the targets' upper case differs, or is not Access's, the character is mapped here.
            switch (c)
            {
                case 'ß': key.Append("SS"); continue;                    // ß
                case 'æ': case 'Æ': key.Append("AE"); continue;     // æ Æ
                case 'œ': case 'Œ': key.Append("OE"); continue;     // œ Œ
                case 'þ': case 'Þ': key.Append("TH"); continue;     // þ Þ
                case 'ĳ': case 'Ĳ': key.Append("IJ"); continue;     // ĳ Ĳ
                case 'ﬀ': key.Append("FF"); continue;                    // the Latin ligatures
                case 'ﬁ': key.Append("FI"); continue;
                case 'ﬂ': key.Append("FL"); continue;
                case 'ﬃ': key.Append("FFI"); continue;
                case 'ﬄ': key.Append("FFL"); continue;
                case 'ς': key.Append('Σ'); continue;                // ς as Σ
                case 'µ': key.Append('Μ'); continue;                // µ as Μ
                case 'ǅ': case 'ǆ': key.Append('Ǆ'); continue; // ǅ ǆ as Ǆ, and the other digraphs
                case 'ǈ': case 'ǉ': key.Append('Ǉ'); continue;
                case 'ǋ': case 'ǌ': key.Append('Ǌ'); continue;
                case 'ǲ': case 'ǳ': key.Append('Ǳ'); continue;
                case 'ı':                                                // the dotless i, which is not I
                case 'ſ':                                                // the long s, which is not S
                case 'K':                                                // the Kelvin sign, which is not K
                case 'Ω':                                                // the Ohm sign, which is not Ω
                    key.Append(c);
                    continue;
            }
            if ((c >= 'ぁ' && c <= 'ゖ') || c == 'ゝ' || c == 'ゞ')
                key.Append((char)(c + 0x60));                                 // hiragana as katakana
            else
                key.Append(char.ToUpperInvariant(c));
        }
        return key.ToString();
    }

    // The name composed (NFC): an accent written as two characters is then one. The Kelvin and Ohm signs are left as
    // they are, which Access keeps apart from K and Ω; the Angstrom sign it takes as Å, as NFC does.
    private static string Composed(string name)
    {
        if (name.IndexOf('K') < 0 && name.IndexOf('Ω') < 0) return name.Normalize(NormalizationForm.FormC);
        var composed = new StringBuilder(name.Length);
        int start = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && name[i] != 'K' && name[i] != 'Ω') continue;
            if (i > start) composed.Append(name.Substring(start, i - start).Normalize(NormalizationForm.FormC));
            if (i < name.Length) composed.Append(name[i]);
            start = i + 1;
        }
        return composed.ToString();
    }
}
