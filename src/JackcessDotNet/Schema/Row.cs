using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// A row of a table: its values by column name. It is the row a caller gives to <see cref="Table.Insert"/> and the row
/// a table or cursor reads.
/// <code>
///   table.Insert(new Row { ["Name"] = "Alice", ["Born"] = new DateTime(1990, 4, 1) });
///
///   foreach (Row row in table.Rows)
///       Console.WriteLine($"{row["Name"]} {row.GetValue&lt;DateTime?&gt;("Born"):d}");
/// </code>
/// <para>
/// A column is found by its name as given, and otherwise by a name Access takes as one with it: the same in another
/// case, in another width, in the other kana, an accent written as one character or as two - as Access compares a
/// table's column names, by General - Legacy's keys, whatever the database sorts in (#63). Names Access keeps apart
/// are two entries: the micro sign and the Greek mu, a letter with and without its accent. When a row has entries for
/// two columns Access takes as one - a file Access let have them - a name finds the entry of exactly that name, or the
/// first of them. The comparison is the same on every runtime.
/// </para>
/// <para>
/// A database null is <c>null</c>. A row that is read has an entry for every column that was read, in the
/// table's column order, <c>null</c> for a column with no value: so <see cref="ContainsKey"/> says whether the
/// table has the column, and <see cref="IsNull"/> whether the row has a value in it. A row a caller makes need
/// not name every column: a column it does not name is written as null, or numbered, as one it names with
/// <c>null</c> is. <see cref="DBNull"/> is taken as null when a row is written, for a row filled from a
/// <c>DataRow</c>, and is never what a read returns.
/// </para>
/// <para>
/// The entries keep the order they were added in. Changing a row changes nothing in the file until it is given
/// to an insert or an update. A row that was read knows where it was read from (<see cref="Id"/>), which is how
/// <see cref="Table.Update"/> and <see cref="Table.Delete"/> find it, and which of its columns have been given a
/// value since - with the indexer or <see cref="Add"/>, whatever the value - which are the columns
/// <see cref="Table.Update"/> assigns. A value changed in place and not given to the row again (a byte array whose
/// bytes are changed) is not one of them.
/// </para>
/// </summary>
public sealed class Row : IDictionary<string, object?>, IReadOnlyDictionary<string, object?>
{
    // The entries in the order they were added, and where the entry of each name is: by the name exactly.
    private readonly List<KeyValuePair<string, object?>> _entries;
    private readonly Dictionary<string, int> _places;

    // How a name that is not an entry's exactly finds one (ObjectNames). A row this library writes from, whose entries
    // have their columns' names exactly, finds a name only exactly: a column it gives no value has none, though another
    // column's name is one with its name to Access.
    private readonly ObjectNames _names;
    private readonly bool _exactOnly;

    // The entries by the hashes of their names' keys (ObjectNames.NameKey.Hashes): a name finds the entries it may be one
    // with through its own, made when such a name is first looked for and kept as entries are added.
    private Dictionary<int, List<int>>? _byHash;

    // The table the row was read from and where in it, for a row that was read.
    private Table? _source;
    private int    _pointer;

    // The entries of a row that was read that have been given a value since - with the indexer or Add, whatever the
    // value - by their names: what an update assigns, as Access's Edit and Update assign the fields given a value in
    // between.
    private HashSet<string>? _assigned;

    private void Assigned(int at)
    {
        if (_source is not null) (_assigned ??= new HashSet<string>(StringComparer.Ordinal)).Add(_entries[at].Key);
    }

    /// <summary>
    /// The entries of the columns given a value since the row was read, in the row's order, with the values they
    /// have now - the value it was read with among them: what <see cref="Table.Update"/> assigns. Empty for a
    /// row nothing has been given to since. A column taken out of the row is not among them.
    /// </summary>
    internal Row AssignedSinceRead()
    {
        var assigned = new Row(0, _names);
        if (_assigned is null) return assigned;
        foreach (var entry in _entries)
            if (_assigned.Contains(entry.Key)) assigned.Put(entry.Key, entry.Value);
        return assigned;
    }

    /// <summary>Whether the entry <paramref name="columnName"/> finds has been given a value since the row was read (<see cref="AssignedSinceRead"/>).</summary>
    internal bool WasAssigned(string columnName) => _assigned is not null && Find(columnName) is >= 0 and var at && _assigned.Contains(_entries[at].Key);

    /// <summary>
    /// Which row of which table the row is, and what it has been given since it was read: kept by an insert, which
    /// makes the row the one it inserts, to give it back as it came when the insert is taken back.
    /// </summary>
    internal (Table? Source, int Pointer, HashSet<string>? Assigned) Identity
    {
        get => (_source, _pointer, _assigned is null ? null : new HashSet<string>(_assigned, StringComparer.Ordinal));
        set => (_source, _pointer, _assigned) = value;
    }

    /// <summary>
    /// Where the row is in the table it was read from or inserted into, or null for a row that is in no table: one
    /// a caller made and has not inserted, a copy of another row, or a row that has been deleted. An update does
    /// not change it. An insert makes it the new row's: a row read from a table and given to
    /// <see cref="Table.Insert"/> as a copy is the copy from then on. Jackcess's <c>Row.getId</c>.
    /// </summary>
    public RowId? Id => _source is null ? null : RowId.FromPointer(_pointer);

    /// <summary>The table the row was read from, or null (<see cref="Id"/>).</summary>
    internal Table? Source => _source;

    /// <summary>The row's place as one number (<see cref="RowPointer"/>); meaningful only with a <see cref="Source"/>.</summary>
    internal int Pointer => _pointer;

    /// <summary>Notes that the row is the one at <paramref name="pointer"/> in <paramref name="source"/>.</summary>
    internal void ReadFrom(Table source, int pointer)
    {
        _source   = source;
        _pointer  = pointer;
        _assigned = null;
    }

    /// <summary>Notes that the row is in no table any longer: it was deleted.</summary>
    internal void Detach() => _source = null;

    /// <summary>Creates a row with no values.</summary>
    public Row() : this(0, ObjectNames.GeneralLegacy) { }

    /// <summary>
    /// A row with room for <paramref name="capacity"/> values, whose names are compared by <paramref name="names"/>:
    /// one that is about to be read, by its table's rule, or copied. With <paramref name="exactOnly"/> a name finds
    /// only the entry of exactly that name: a row written from.
    /// </summary>
    internal Row(int capacity, ObjectNames names, bool exactOnly = false)
    {
        _entries   = new List<KeyValuePair<string, object?>>(capacity);
        _places    = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
        _names     = names;
        _exactOnly = exactOnly;
    }

    /// <summary>
    /// Creates a row with the values of <paramref name="values"/>, in their order: a copy of another row, for one.
    /// A copy of a row that was read is a new row, with no <see cref="Id"/>, and has every entry the row has.
    /// </summary>
    /// <exception cref="ArgumentException">Two of the values are for one column: their names are one to Access.</exception>
    public Row(IEnumerable<KeyValuePair<string, object?>> values) : this(0, (values as Row)?._names ?? ObjectNames.GeneralLegacy)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (values is Row row)
        {
            foreach (var entry in row._entries) Put(entry.Key, entry.Value);
            return;
        }
        foreach (var value in values)
            Add(value.Key, value.Value);
    }

    /// <summary>
    /// A row of <paramref name="values"/>, each the entry of exactly its name, whose names are compared as
    /// <paramref name="like"/>'s are: a copy of some of a row's entries, which may be for columns whose names Access
    /// takes as one.
    /// </summary>
    internal static Row Of(IEnumerable<KeyValuePair<string, object?>> values, Row like)
    {
        var row = new Row(0, like._names, like._exactOnly);
        foreach (var value in values) row.Put(value.Key, value.Value);
        return row;
    }

    /// <summary>
    /// A row of <paramref name="values"/> by their columns' exact names, that finds a name only exactly: values this
    /// library has worked out for a table's columns, and writes.
    /// </summary>
    internal static Row Exactly(IEnumerable<KeyValuePair<string, object?>> values, ObjectNames names)
    {
        var row = new Row(0, names, exactOnly: true);
        foreach (var value in values) row.Put(value.Key, value.Value);
        return row;
    }

    /// <summary>
    /// Gives the entry of exactly <paramref name="columnName"/> <paramref name="value"/>, adding it when the row has
    /// none: how a row is read, an entry for each column, though two columns' names are one to Access.
    /// </summary>
    internal void Put(string columnName, object? value)
    {
        if (_places.TryGetValue(columnName, out int at))
        {
            _entries[at] = new KeyValuePair<string, object?>(columnName, value);
            return;
        }
        Append(columnName, value);
    }

    // A new entry, after the others.
    private void Append(string columnName, object? value)
    {
        int at = _entries.Count;
        _places.Add(columnName, at);
        _entries.Add(new KeyValuePair<string, object?>(columnName, value));
        if (_byHash is not null) Index(at);
    }

    // The entry at `at` among the entries found by the hashes of its name's keys.
    private void Index(int at)
    {
        foreach (int hash in _names.KeyOf(_entries[at].Key).Hashes)
        {
            if (!_byHash!.TryGetValue(hash, out var places)) _byHash[hash] = places = new List<int>(1);
            places.Add(at);
        }
    }

    /// <summary>Whether the row has an entry of exactly <paramref name="columnName"/>.</summary>
    internal bool HasExactly(string columnName) => _places.ContainsKey(columnName);

    /// <summary>The value of the entry of exactly <paramref name="columnName"/>, when the row has one.</summary>
    internal bool TryGetExactly(string columnName, out object? value)
    {
        if (_places.TryGetValue(columnName, out int at))
        {
            value = _entries[at].Value;
            return true;
        }
        value = null;
        return false;
    }

    // Where the entry a name finds is, or -1: the entry of exactly that name, and otherwise the first whose name is
    // one with it by its index key, then the first that is one with it at all (ObjectNames.Find).
    private int Find(string columnName)
    {
        if (_places.TryGetValue(columnName, out int at)) return at;
        if (_exactOnly || _entries.Count == 0) return -1;
        var key = _names.KeyOf(columnName);
        if (_byHash is null)
        {
            _byHash = new Dictionary<int, List<int>>();
            for (int i = 0; i < _entries.Count; i++) Index(i);
        }

        // The entries that have a key's hash in common with the name, in the row's order: the first the name is one
        // with by its index key, else the first it is one with at all.
        int byIndexKey = -1, found = -1;
        foreach (int hash in key.Hashes)
        {
            if (!_byHash.TryGetValue(hash, out var places)) continue;
            foreach (int i in places)
            {
                if (byIndexKey >= 0 && i >= byIndexKey) continue;
                var other = _names.KeyOf(_entries[i].Key);
                if (key.HasIndexKeyOf(other)) byIndexKey = i;
                else if ((found < 0 || i < found) && key.IsOneWith(other)) found = i;
            }
        }
        return byIndexKey >= 0 ? byIndexKey : found;
    }

    /// <summary>
    /// The value of column <paramref name="columnName"/>. Setting it adds the column to the row, after the others,
    /// when the row does not have it; a column the row has keeps the name it was added under.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Reading a column the row has no entry for (<see cref="TryGetValue"/> asks without throwing).</exception>
    public object? this[string columnName]
    {
        get
        {
            if (columnName is null) throw new ArgumentNullException(nameof(columnName));
            int at = Find(columnName);
            return at >= 0
                ? _entries[at].Value
                : throw new KeyNotFoundException($"The row has no column '{columnName}'.");
        }
        set
        {
            if (columnName is null) throw new ArgumentNullException(nameof(columnName));
            int at = Find(columnName);
            if (at >= 0)
            {
                // The entry keeps the name it was added under.
                _entries[at] = new KeyValuePair<string, object?>(_entries[at].Key, value);
            }
            else
            {
                at = _entries.Count;
                Append(columnName, value);
            }
            Assigned(at);
        }
    }

    /// <summary>
    /// The value of column <paramref name="columnName"/> as a <typeparamref name="T"/>: the value itself when it
    /// is one, and otherwise converted as <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/> converts
    /// it, in the invariant culture - an Access Integer (<see cref="short"/>) asked for as an <see cref="int"/>,
    /// a number as a <see cref="string"/>. A column that is null, or that the row has no entry for, gives the
    /// default of <typeparamref name="T"/>: <c>null</c> for a reference type or a nullable one, zero for a
    /// number - ask for <c>int?</c> to tell a null from a zero.
    /// </summary>
    /// <exception cref="InvalidCastException">The value cannot be converted to <typeparamref name="T"/>.</exception>
    /// <exception cref="FormatException">The value is text that does not read as a <typeparamref name="T"/>.</exception>
    /// <exception cref="OverflowException">The value is a number outside the range of <typeparamref name="T"/>.</exception>
    public T? GetValue<T>(string columnName)
    {
        if (!TryGetValue(columnName, out object? value) || RowValues.IsNull(value)) return default;
        if (value is T asIs) return asIs;
        return (T)Convert.ChangeType(value!, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether the row has no value in column <paramref name="columnName"/>: its entry is <c>null</c> (or
    /// <see cref="DBNull"/>), or it has no entry for the column.
    /// </summary>
    public bool IsNull(string columnName) => !TryGetValue(columnName, out object? value) || RowValues.IsNull(value);

    /// <summary>The number of columns the row has an entry for.</summary>
    public int Count => _entries.Count;

    /// <summary>The names of the columns the row has an entry for, in the row's order, as they are when this is asked.</summary>
    public ICollection<string> Keys
    {
        get
        {
            var keys = new string[_entries.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = _entries[i].Key;
            return keys;
        }
    }

    /// <summary>The row's values, in the row's order, as they are when this is asked.</summary>
    public ICollection<object?> Values
    {
        get
        {
            var values = new object?[_entries.Count];
            for (int i = 0; i < values.Length; i++) values[i] = _entries[i].Value;
            return values;
        }
    }

    /// <summary>Adds column <paramref name="columnName"/> with <paramref name="value"/>, after the row's other columns.</summary>
    /// <exception cref="ArgumentException">The row already has an entry for the column: one of that name, or of a name Access takes as one with it.</exception>
    public void Add(string columnName, object? value)
    {
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));
        if (Find(columnName) is >= 0 and var at)
            throw new ArgumentException(
                _entries[at].Key == columnName
                    ? $"The row already has a value for column '{columnName}'."
                    : $"The row already has a value for column '{_entries[at].Key}', which is one name with '{columnName}' to Access.",
                nameof(columnName));
        Append(columnName, value);
        Assigned(_entries.Count - 1);
    }

    /// <summary>Whether the row has an entry for column <paramref name="columnName"/>, null or not.</summary>
    public bool ContainsKey(string columnName)
    {
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));
        return Find(columnName) >= 0;
    }

    /// <summary>
    /// The value of column <paramref name="columnName"/> when the row has an entry for it. The value can be
    /// <c>null</c>: that is a column with no value, where <c>false</c> is a column the row does not have.
    /// </summary>
    public bool TryGetValue(string columnName, [MaybeNullWhen(false)] out object? value)
    {
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));
        int at = Find(columnName);
        if (at >= 0)
        {
            value = _entries[at].Value;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Takes column <paramref name="columnName"/> out of the row, and says whether the row had it.</summary>
    public bool Remove(string columnName)
    {
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));
        int at = Find(columnName);
        if (at < 0) return false;

        _places.Remove(_entries[at].Key);
        _entries.RemoveAt(at);
        _byHash = null;   // the places after it have moved: made again when it is next needed
        for (int i = at; i < _entries.Count; i++)
            _places[_entries[i].Key] = i;
        return true;
    }

    /// <summary>Takes every column out of the row.</summary>
    public void Clear()
    {
        _entries.Clear();
        _places.Clear();
        _byHash = null;
    }

    /// <summary>The row's columns and their values, in the row's order.</summary>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();

    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => Keys;

    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values => Values;

    bool ICollection<KeyValuePair<string, object?>>.IsReadOnly => false;

    void ICollection<KeyValuePair<string, object?>>.Add(KeyValuePair<string, object?> item) => Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<string, object?>>.Contains(KeyValuePair<string, object?> item)
        => TryGetValue(item.Key, out object? value) && Equals(value, item.Value);

    void ICollection<KeyValuePair<string, object?>>.CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
        => _entries.CopyTo(array, arrayIndex);

    bool ICollection<KeyValuePair<string, object?>>.Remove(KeyValuePair<string, object?> item)
        => ((ICollection<KeyValuePair<string, object?>>)this).Contains(item) && Remove(item.Key);
}
