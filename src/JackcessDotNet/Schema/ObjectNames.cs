namespace JackcessDotNet;

/// <summary>
/// Which two names of a database's objects of one kind are one name to Access - two tables, two columns or two
/// indexes of a table, two relationships - as DAO 16 and Jet 4.0 were measured to decide it (#63): when the names'
/// keys, in the order Access compares them by, are equal. A key is either the one an index entry has for the name,
/// its trailing spaces dropped, or the key of the whole name. Access refuses a second name whose index key is a first
/// one's, as the catalog's index on names does, and its lookups - DAO's collections, SQL - take as one names whose
/// whole keys are equal: Access makes a table named "x" and a space and another named "x" and an ideographic space,
/// and then cannot delete either by name. Both count here, so this library makes no name Access's lookups confuse
/// with another.
/// <para>
/// The order: a table's or a relationship's name - which the catalog keeps under an index - by the order its catalog
/// sorts in, and a column's or an index's by General - Legacy's keys in a Jet4 or ACE file. In a database that sorts in
/// General, each is compared by General's keys and General - Legacy's: DAO makes two tables of names General's keys keep
/// apart and General - Legacy's take as one (Ȥ and ȥ), and then cannot tell them apart by name; and it makes two
/// fields of names General's keys take as one (ƀ and Ƀ), which ACE then took as one - refusing the second as a field
/// already there, and finding the first by the name of the second - in a process that had used such a database through
/// DAO before. That also takes as one some names Access keeps apart there - Ü and U with a combining diaeresis, "x"
/// and "x" with a character General - Legacy has no code for (U+03F3, U+FEFF).
/// In an Access 97 file, which this library only reads, a table's name is compared by the index keys of Access 97's
/// General order alone - DAO keeps apart there a name ending in a space and one ending in a no-break space, whose whole
/// keys are equal - and a column's or an index's by those and by General - Legacy's keys, by which DAO's Fields and
/// Indexes take some names as one there (Þ and TH).
/// </para>
/// <para>
/// A name with a character Access keys by the characters around it (ー, 々, ゝ, the shadda) is taken as one with
/// another when any of its readings is: each mark as itself, with the marks Access takes as the same (the prolonged
/// sound mark and its half-width form); each as the character before it, as Access reads ー after a Latin letter; and
/// the Arabic marks General keys by context, left out. That takes as two no names Access takes as one, and takes as one
/// some Access keeps apart (人々 and 人人).
/// </para>
/// <para>
/// A comparison throws for no string, and a name already in a file stands in the way of nothing but a new name it is
/// one with. A lookup by name (<see cref="Find"/>) takes the name given exactly first.
/// </para>
/// </summary>
internal sealed class ObjectNames
{
    /// <summary>Names compared by General - Legacy's keys: a Jet4 or ACE file's columns and indexes, and a row a caller makes.</summary>
    internal static ObjectNames GeneralLegacy { get; } = new(TextIndexCodes.GeneralLegacy, byWholeName: true, alsoBy: null);

    /// <summary>
    /// The names a catalog keeps in General, Access 2010's order: by its keys, and by General - Legacy's, which DAO's
    /// own lookups take some names as one by there - Ȥ and ȥ, two tables General's keys keep apart and DAO then cannot
    /// tell apart by name.
    /// </summary>
    internal static ObjectNames General { get; } = new(TextIndexCodes.General, byWholeName: true, alsoBy: GeneralLegacy);

    /// <summary>The names an Access 97 file's catalog keeps - its tables' - by the index keys of its General order.</summary>
    internal static ObjectNames Access97 { get; } = new(TextIndexCodes.Access97, byWholeName: false, alsoBy: null);

    /// <summary>
    /// The names of an Access 97 file's columns and indexes: by the index keys of its General order, and by General -
    /// Legacy's keys, by which DAO's Fields and Indexes take some names as one there (Þ and TH).
    /// </summary>
    internal static ObjectNames Access97Columns { get; } = new(TextIndexCodes.Access97, byWholeName: false, alsoBy: GeneralLegacy);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, ObjectNames> Access97Tables = new(), Access97ColumnsIn = new();

    /// <summary>
    /// <see cref="Access97"/>, or with <paramref name="columns"/> <see cref="Access97Columns"/>, for an Access 97 file
    /// whose text is in <paramref name="codePage"/>: by the index keys of the bytes of the names in it
    /// (<see cref="TextIndexCodes.Access97In"/>). DAO, with the code page 1251, found a table named "Города" by "ГОРОДА"
    /// and "города", as those keys have it (tools/access97-code-page-fixture.ps1).
    /// </summary>
    internal static ObjectNames Access97In(int codePage, bool columns)
        => codePage == TextIndexCodes.Access97.CodePage ? (columns ? Access97Columns : Access97)
         : (columns ? Access97ColumnsIn : Access97Tables).GetOrAdd(codePage,
               cp => new(TextIndexCodes.Access97In(cp), byWholeName: false, alsoBy: columns ? GeneralLegacy : null));

    /// <summary>
    /// The rule for the names a catalog sorting in <paramref name="order"/> keeps - tables, queries, relationships - or
    /// null for an order whose keys this library does not make.
    /// </summary>
    internal static ObjectNames? ForCatalog(SortOrder order)
        => order.KeysAsGeneralLegacy ? GeneralLegacy
         : order.IsGeneral ? General
         : order.IsAccess97General ? Access97In(order.CodePage, columns: false)
         : null;

    /// <summary>
    /// The rule for the names of a table's columns and indexes in a file of <paramref name="format"/> whose catalog sorts
    /// in <paramref name="catalogOrder"/>: General - Legacy's keys; in a database that sorts in General, General's as well,
    /// as its tables' names are compared there; in an Access 97 file, Access 97's and General - Legacy's.
    /// </summary>
    internal static ObjectNames ForColumns(JetFormat format, SortOrder catalogOrder)
        => format.SizeSortOrder < 4 ? Access97In(format.TextEncoding.CodePage, columns: true) : catalogOrder.IsGeneral ? General : GeneralLegacy;

    /// <summary>
    /// Whether this library compares the names of a table's columns and indexes as Access does in a database whose
    /// text sorts in <paramref name="order"/>: one whose keys it makes. Access compares them in another by that order's
    /// own keys - in Swedish an index "xʎ" is one with "xÜ", in Czech "xℯ" with "xė", in Japanese "xー" with "x―", in
    /// Turkish "xİ" with "xi", each two to General - Legacy's keys.
    /// </summary>
    internal static bool ComparesColumnsAsGeneralLegacy(SortOrder order) => ForCatalog(order) is not null;

    private readonly TextIndexCodes _codes;
    private readonly bool _byWholeName;
    private readonly ObjectNames? _alsoBy;

    // The keys of the names compared lately, by the name exactly: a table's columns are looked up row after row, and the
    // catalog's names scan after scan. A key is a few dozen bytes; the cache is emptied when it holds this many.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NameKey> _keys = new(StringComparer.Ordinal);
    private const int MostKeysKept = 4096;

    private ObjectNames(TextIndexCodes codes, bool byWholeName, ObjectNames? alsoBy)
    {
        _codes       = codes;
        _byWholeName = byWholeName;
        _alsoBy      = alsoBy;
    }

    /// <summary>The keys <paramref name="name"/> is compared by: worked out once, for a name compared with many.</summary>
    internal NameKey KeyOf(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (_keys.TryGetValue(name, out var key)) return key;
        key = new NameKey(this, name);
        if (_keys.Count >= MostKeysKept) _keys.Clear();
        _keys.TryAdd(name, key);
        return key;
    }

    /// <summary>Whether <paramref name="first"/> and <paramref name="second"/> are one name to Access.</summary>
    internal bool Same(string first, string second) => first == second || KeyOf(first).IsOneWith(KeyOf(second));

    /// <summary>
    /// Where among <paramref name="names"/> a lookup of <paramref name="name"/> finds its object, or -1: the name given
    /// exactly, when one of them is; otherwise the first that is one with it by its index key, and then the first that
    /// is one with it at all - as Access, whose lookups take the first of names that are one.
    /// </summary>
    internal int Find(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
            if (names[i] == name) return i;
        if (names.Count == 0) return -1;
        var key = KeyOf(name);
        int found = -1;
        for (int i = 0; i < names.Count; i++)
        {
            var other = KeyOf(names[i]);
            if (key.HasIndexKeyOf(other)) return i;
            if (found < 0 && key.IsOneWith(other)) found = i;
        }
        return found;
    }

    /// <summary>The first of <paramref name="names"/> that is one with an earlier one, and that earlier one; null when no two are one.</summary>
    internal (string First, string Second)? FirstTwoThatAreOne(IReadOnlyList<string> names)
    {
        // Each name is compared with the earlier names of its text, and with those that have the hash of one of its keys:
        // two names that are one have a key in common (NameKey.Hashes).
        var byText = new Dictionary<string, int>(StringComparer.Ordinal);
        var byHash = new Dictionary<int, List<int>>();
        var keys   = new NameKey[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            keys[i] = KeyOf(names[i]);
            int first = byText.TryGetValue(names[i], out int same) ? same : -1;
            foreach (int hash in keys[i].Hashes)
                if (byHash.TryGetValue(hash, out var earlier))
                    foreach (int j in earlier)
                        if ((first < 0 || j < first) && keys[j].IsOneWith(keys[i])) first = j;
            if (first >= 0) return (names[first], names[i]);

            byText[names[i]] = i;
            foreach (int hash in keys[i].Hashes)
            {
                if (!byHash.TryGetValue(hash, out var earlier)) byHash[hash] = earlier = new List<int>(1);
                earlier.Add(i);
            }
        }
        return null;
    }

    /// <summary>The first of <paramref name="names"/> that is one with <paramref name="name"/>, or null.</summary>
    internal string? OneWith(IEnumerable<string> names, string name)
    {
        NameKey? key = null;
        foreach (string other in names)
        {
            if (other == name) return other;
            key ??= KeyOf(name);
            if (key.IsOneWith(KeyOf(other))) return other;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="taken"/>, a name an object has, quoted for a message that refuses <paramref name="given"/> for
    /// it: with what makes the two one, when they are not the same text.
    /// </summary>
    internal static string Taken(string taken, string given)
        => taken == given ? $"'{taken}'" : $"'{taken}', which is one name with '{given}' to Access";

    // ── The keys ─────────────────────────────────────────────────────────────

    // The first private-use character the marks Access keys by their neighbours stand in for: one for each kind of
    // mark, so that a name with such a mark is one only with a name with a mark of the same kind there. A name with
    // one of these characters itself is taken as one with such a name: a refusal, never a name made twice.
    private const char FirstStandIn = '\uE000';

    // The kind of a mark Access keys by the characters around it: the marks Access takes as the same have one kind.
    private static int KindOfMark(char c) => c switch
    {
        '\u30FC' or '\uFF70'                         => 0,   // the prolonged sound mark, and its half-width form
        '\u309D' or '\u309E' or '\u30FD' or '\u30FE' => 1,   // the kana iteration marks, plain and voiced (a voiced one read with U+3099)
        '\u3005' or '\u303B'                         => 2,   // the ideographic iteration marks
        >= '\u3031' and <= '\u3035'                  => 3,   // the vertical kana repeat marks
        '\u30A0'                                     => 4,   // the double hyphen
        '\uA015'                                     => 5,   // the Yi iteration mark
        '\u0651' or '\uFE7C' or '\uFE7D'             => 6,   // the shadda
        _                                            => 7,   // the Arabic marks General keys by context
    };

    private const int LeftOutKind = 7;

    // The readings of a name, each a text this library keys: the name itself, or for a name with marks Access keys by
    // the characters around them, the marks as themselves, as the character before each, and - for the Arabic marks
    // General keys by context - left out.
    private List<string> Readings(string name)
    {
        int first = -1;
        for (int i = 0; i < name.Length && first < 0; i++)
            if (_codes.IsContextDependent(name[i])) first = i;
        if (first < 0) return new List<string>(1) { name };

        var standIn = new System.Text.StringBuilder(name.Length + 1);
        var repeat  = name.ToCharArray();
        var left    = new System.Text.StringBuilder(name.Length);
        bool leftOut = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!_codes.IsContextDependent(c))
            {
                standIn.Append(c);
                left.Append(c);
                continue;
            }
            int kind = KindOfMark(c);
            char mark = (char)(FirstStandIn + kind);
            // A voiced kana iteration mark is its canonical decomposition, the plain mark and U+3099, the combining
            // voiced sound mark: Access takes ゞ as one with ゝ and U+3099.
            string reading = c is '\u309E' or '\u30FE' ? new string(new[] { mark, '\u3099' }) : mark.ToString();
            standIn.Append(reading);
            repeat[i] = i > 0 ? repeat[i - 1] : mark;
            if (kind == LeftOutKind) leftOut = true;
            else left.Append(reading);
        }
        var readings = new List<string>(3) { standIn.ToString(), new(repeat) };
        if (leftOut) readings.Add(left.ToString());
        return readings;
    }

    /// <summary>
    /// The keys a name is compared by: for each of its readings, its index key and - where the rule compares whole names
    /// - its whole key, in the rule's order and in the order its lookups also go by.
    /// </summary>
    internal sealed class NameKey
    {
        private readonly List<byte[]> _indexKeys = new(2);
        private readonly List<byte[]> _wholeKeys = new(2);
        private readonly NameKey? _alsoBy;

        internal NameKey(ObjectNames rule, string name)
        {
            foreach (string reading in rule.Readings(name))
            {
                if (rule._codes.NameKey(reading, whole: false) is { } index) _indexKeys.Add(index);
                if (rule._byWholeName && rule._codes.NameKey(reading, whole: true) is { } whole) _wholeKeys.Add(whole);
            }
            if (rule._alsoBy is { } also) _alsoBy = new NameKey(also, name);
            var hashes = new HashSet<int>();
            for (var key = this; key is not null; key = key._alsoBy)
            {
                foreach (var bytes in key._indexKeys) hashes.Add(Hash(bytes, 1));
                foreach (var bytes in key._wholeKeys) hashes.Add(Hash(bytes, 2));
            }
            Hashes = hashes.ToArray();
        }

        /// <summary>
        /// A hash of each of the name's keys: two names that are one have a key in common, and so a hash, which is how a
        /// row finds the entries a name may be one with (<see cref="Row"/>) without comparing it with every other.
        /// </summary>
        internal int[] Hashes { get; }

        // FNV-1a over the key's bytes, after the kind of key: the same on every target.
        private static int Hash(byte[] bytes, int kind)
        {
            unchecked
            {
                int hash = (int)2166136261 ^ kind;
                foreach (byte b in bytes) hash = (hash ^ b) * 16777619;
                return hash;
            }
        }

        /// <summary>Whether this name and <paramref name="other"/>'s are one to Access: any of their keys equal.</summary>
        internal bool IsOneWith(NameKey other)
            => AnyEqual(_indexKeys, other._indexKeys) || AnyEqual(_wholeKeys, other._wholeKeys)
            || (_alsoBy is not null && other._alsoBy is not null && _alsoBy.IsOneWith(other._alsoBy));

        /// <summary>Whether this name's first reading has <paramref name="other"/>'s index key: a name the catalog's index takes as the same.</summary>
        internal bool HasIndexKeyOf(NameKey other)
            => _indexKeys.Count > 0 && other._indexKeys.Count > 0 && _indexKeys[0].AsSpan().SequenceEqual(other._indexKeys[0]);

        private static bool AnyEqual(List<byte[]> first, List<byte[]> second)
        {
            foreach (var a in first)
                foreach (var b in second)
                    if (a.AsSpan().SequenceEqual(b)) return true;
            return false;
        }
    }
}
