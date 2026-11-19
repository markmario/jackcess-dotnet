using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// A row of a table: its values by column name, whatever the case of the name's letters. It is the row a caller
/// gives to <see cref="Table.Insert"/> and the row a table or cursor reads.
/// <code>
///   table.Insert(new Row { ["Name"] = "Alice", ["Born"] = new DateTime(1990, 4, 1) });
///
///   foreach (Row row in table.Rows)
///       Console.WriteLine($"{row["Name"]} {row.GetValue&lt;DateTime?&gt;("Born"):d}");
/// </code>
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
    // The entries in the order they were added, and where each column's entry is.
    private readonly List<KeyValuePair<string, object?>> _entries;
    private readonly Dictionary<string, int> _places;

    // The table the row was read from and where in it, for a row that was read.
    private Table? _source;
    private int    _pointer;

    // The columns of a row that was read that have been given a value since - with the indexer or Add, whatever
    // the value: what an update assigns, as Access's Edit and Update assign the fields given a value in between.
    private HashSet<string>? _assigned;

    private void Assigned(string columnName)
    {
        if (_source is not null) (_assigned ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(columnName);
    }

    /// <summary>
    /// The entries of the columns given a value since the row was read, in the row's order, with the values they
    /// have now - the value it was read with among them: what <see cref="Table.Update"/> assigns. Empty for a
    /// row nothing has been given to since. A column taken out of the row is not among them.
    /// </summary>
    internal Row AssignedSinceRead()
    {
        var assigned = new Row();
        if (_assigned is null) return assigned;
        foreach (var entry in _entries)
            if (_assigned.Contains(entry.Key)) assigned.Add(entry.Key, entry.Value);
        return assigned;
    }

    /// <summary>Whether <paramref name="columnName"/> has been given a value since the row was read (<see cref="AssignedSinceRead"/>).</summary>
    internal bool WasAssigned(string columnName) => _assigned?.Contains(columnName) == true && _places.ContainsKey(columnName);

    /// <summary>
    /// Where the row is in the table it was read from, or null for a row that was not read from a table: one a
    /// caller made, a copy of another row, or a row that has been deleted. An update does not change it.
    /// Jackcess's <c>Row.getId</c>.
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
    public Row()
    {
        _entries = new List<KeyValuePair<string, object?>>();
        _places  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A row with room for <paramref name="capacity"/> values: one that is about to be read.</summary>
    internal Row(int capacity)
    {
        _entries = new List<KeyValuePair<string, object?>>(capacity);
        _places  = new Dictionary<string, int>(capacity, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a row with the values of <paramref name="values"/>, in their order: a copy of another row, for one.
    /// A copy of a row that was read is a new row, with no <see cref="Id"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Two of the values are for one column.</exception>
    public Row(IEnumerable<KeyValuePair<string, object?>> values) : this()
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        foreach (var value in values)
            Add(value.Key, value.Value);
    }

    /// <summary>
    /// The value of column <paramref name="columnName"/>. Setting it adds the column to the row, after the others,
    /// when the row does not have it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Reading a column the row has no entry for (<see cref="TryGetValue"/> asks without throwing).</exception>
    public object? this[string columnName]
    {
        get
        {
            if (columnName is null) throw new ArgumentNullException(nameof(columnName));
            return _places.TryGetValue(columnName, out int at)
                ? _entries[at].Value
                : throw new KeyNotFoundException($"The row has no column '{columnName}'.");
        }
        set
        {
            if (columnName is null) throw new ArgumentNullException(nameof(columnName));
            Assigned(columnName);
            if (_places.TryGetValue(columnName, out int at))
            {
                // The entry keeps the name it was added under.
                _entries[at] = new KeyValuePair<string, object?>(_entries[at].Key, value);
            }
            else
            {
                _places.Add(columnName, _entries.Count);
                _entries.Add(new KeyValuePair<string, object?>(columnName, value));
            }
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
    /// <exception cref="ArgumentException">The row already has an entry for the column.</exception>
    public void Add(string columnName, object? value)
    {
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));
        if (_places.ContainsKey(columnName))
            throw new ArgumentException($"The row already has a value for column '{columnName}'.", nameof(columnName));
        Assigned(columnName);
        _places.Add(columnName, _entries.Count);
        _entries.Add(new KeyValuePair<string, object?>(columnName, value));
    }

    /// <summary>Whether the row has an entry for column <paramref name="columnName"/>, null or not.</summary>
    public bool ContainsKey(string columnName)
    {
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));
        return _places.ContainsKey(columnName);
    }

    /// <summary>
    /// The value of column <paramref name="columnName"/> when the row has an entry for it. The value can be
    /// <c>null</c>: that is a column with no value, where <c>false</c> is a column the row does not have.
    /// </summary>
    public bool TryGetValue(string columnName, [MaybeNullWhen(false)] out object? value)
    {
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));
        if (_places.TryGetValue(columnName, out int at))
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
        if (!_places.TryGetValue(columnName, out int at)) return false;

        _entries.RemoveAt(at);
        _places.Remove(columnName);
        for (int i = at; i < _entries.Count; i++)
            _places[_entries[i].Key] = i;
        return true;
    }

    /// <summary>Takes every column out of the row.</summary>
    public void Clear()
    {
        _entries.Clear();
        _places.Clear();
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
