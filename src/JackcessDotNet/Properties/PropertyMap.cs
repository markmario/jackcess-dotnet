using System.Collections;
using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// The properties Access keeps for a table, for one of its columns, or for the database, by name: what
/// <see cref="Table.Properties"/>, <see cref="Column.Properties"/> and <see cref="Database.DatabaseProperties"/>
/// return, as in Jackcess. It is the collection of its properties, in the order the file lists them.
///
/// Access stores well-known property names like:
///   "Description", "Caption", "Format", "DefaultValue", "ValidationRule",
///   "InputMask", "Required", "AllowZeroLength", "DecimalPlaces"
///
/// <para>
/// A property is added or changed with <see cref="Set(string, object)"/> and taken away with
/// <see cref="Remove"/>; neither writes to the file. <see cref="Save"/> writes them. The properties of a table
/// and of its columns are kept together in the file, so saving one of those maps saves the changes made to the
/// others too. Until they are saved the changes are this map's alone: what the library does by a column's
/// properties (<see cref="Column.IsRequired"/>, <see cref="Column.AllowZeroLength"/>) follows the file, as Access
/// would see it.
/// </para>
/// </summary>
public sealed class PropertyMap : IReadOnlyCollection<PropertyMap.Property>
{
    /// <summary>The block type of a table's own properties, and of a database's.</summary>
    internal const short DefaultType = 0x00;
    /// <summary>The block type of a column's properties.</summary>
    internal const short ColumnType = 0x01;

    // The longest name Access gives a property, and the characters it gives none.
    private const int MaxNameLength = 64;
    private static readonly char[] RefusedInNames = { '.', '!', '`', '[', ']' };

    // A property's value is a 16-bit length of bytes inside an entry whose own length is 16 bits.
    private const int MaxValueBytes = short.MaxValue - 8;

    /// <summary>
    /// The name of what the properties belong to: the empty string for a table's own properties and for a
    /// database's, the column's name for a column's.
    /// </summary>
    public string Name { get; }

    /// <summary>The block type from the file: 0x00 a table's own, 0x01 a column's, 0x02 an index's.</summary>
    internal short Type { get; }

    /// <summary>The maps this one is kept with, which save it; null for the map of a column that is in no table.</summary>
    internal PropertyMaps? Owner { get; set; }

    /// <summary>Whether the file has a block for this map with no property in it: written again as it is while the map is empty.</summary>
    internal bool IsEmptyInFile { get; set; }

    // By name, whatever its case, and in the order they were read.
    private readonly Dictionary<string, Property> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Property> _inOrder = new();

    internal PropertyMap(string name, short type) { Name = name; Type = type; }

    /// <summary>
    /// The value of the property named <paramref name="propertyName"/>, whatever the case of its letters, or
    /// null when there is none (<see cref="TryGetValue"/> tells a property that is not there from one whose
    /// value is null).
    /// </summary>
    public object? this[string propertyName]
        => _byName.TryGetValue(propertyName, out var p) ? p.Value : null;

    /// <summary>The value of the property named <paramref name="name"/> when there is one.</summary>
    /// <returns>Whether there is a property of that name; <paramref name="value"/> is its value.</returns>
    public bool TryGetValue(string name, out object? value)
    {
        if (_byName.TryGetValue(name, out var p))
        {
            value = p.Value;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>
    /// The property named <paramref name="name"/>, whatever the case of its letters, when there is one: its
    /// type and whether it is a DDL property as well as its value.
    /// </summary>
    /// <returns>Whether there is a property of that name; <paramref name="property"/> is it, or null.</returns>
    public bool TryGetProperty(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Property? property)
        => _byName.TryGetValue(name, out property);

    /// <summary>The number of properties: 0 for a table or column Access keeps none for.</summary>
    public int Count => _inOrder.Count;

    /// <summary>The properties, in the order the file lists them; one that is added follows them.</summary>
    public IEnumerator<Property> GetEnumerator() => _inOrder.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _inOrder.GetEnumerator();

    internal void Add(Property prop)
    {
        // A name the file lists twice keeps its place and takes the later value.
        if (_byName.TryGetValue(prop.Name, out var earlier))
            _inOrder[_inOrder.IndexOf(earlier)] = prop;
        else
            _inOrder.Add(prop);
        _byName[prop.Name] = prop;
        Owner?.Changed();
    }

    /// <summary>
    /// Gives the property named <paramref name="name"/> the value <paramref name="value"/>. A property the map
    /// has keeps its type and whether it is a DDL property, and <paramref name="value"/> is converted to that
    /// type. A property it does not have is added, with the type Access keeps a property of that name as
    /// (<c>DefaultValue</c> a Memo, <c>Required</c> a Yes/No, <c>Format</c> Text and so on, as in Jackcess, but
    /// that <c>Description</c> is Text, as Access's table designer writes it) or, for any other name, the type
    /// of the value: a <see cref="string"/> is Text, a
    /// <see cref="bool"/> Yes/No, a <see cref="byte"/> Byte, a <see cref="short"/> Integer, an <see cref="int"/>
    /// Long Integer, a <see cref="float"/> Single, a <see cref="double"/> Double, a <see cref="decimal"/>
    /// Currency, a <see cref="DateTime"/> Date/Time, a <see cref="Guid"/> Replication ID and a byte array an OLE
    /// Object. A <see cref="long"/> has no type: Access keeps no property as a Large Number. Nothing is written
    /// until <see cref="Save"/>.
    /// </summary>
    /// <param name="name">The property's name: 1 to 64 characters.</param>
    /// <param name="value">
    /// The property's value. A property has no null value, and no text or bytes of no length, which Access
    /// takes for none ("User-defined properties do not support a Null value"): <see cref="Remove"/> takes a
    /// property away.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The name is not one Access gives a property: empty, longer than 64 characters, beginning with a space, or
    /// with a period, an exclamation mark, a grave accent, a square bracket or a control character in it. Or the
    /// value is of a type no property is kept as, cannot be converted to the property's type, is beyond the
    /// type's range, is text or bytes of no length, or is longer than a property holds (32,759 bytes: 16,379
    /// characters of text).
    /// </exception>
    public void Set(string name, object value)
    {
        CheckName(name);
        if (value is null) throw new ArgumentNullException(nameof(value));

        if (_byName.TryGetValue(name, out var existing))
        {
            Put(existing.Name, existing.DataType, value, existing.Flags);
            return;
        }
        if (PropertyMapWriter.WellKnown(name) is { } known)
            Put(name, known.Type, value, known.IsDdl ? Property.DdlFlag : (byte)0);
        else
            Put(name, PropertyMapWriter.TypeOf(value, name), value, 0);
    }

    /// <summary>
    /// Gives the property named <paramref name="name"/> the value <paramref name="value"/>, kept as
    /// <paramref name="dataType"/>, whatever type a property of that name had. A property the map has keeps
    /// whether it is a DDL property; one that is added is a DDL property when Access keeps a property of that
    /// name as one. Nothing is written until <see cref="Save"/>.
    /// </summary>
    /// <param name="name">The property's name: 1 to 64 characters.</param>
    /// <param name="dataType">
    /// The type the value is kept as: Yes/No, Byte, Integer, Long Integer, Currency, Single, Double, Date/Time,
    /// Replication ID, Text, Memo, Binary or OLE Object.
    /// </param>
    /// <param name="value">The property's value, converted to <paramref name="dataType"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The name is not one Access gives a property (<see cref="Set(string, object)"/> says which);
    /// <paramref name="dataType"/> is not a type a property is kept as; or the value cannot be converted to it, is
    /// beyond its range, has no length, or is longer than a property holds.
    /// </exception>
    public void Set(string name, DataType dataType, object value)
    {
        CheckName(name);
        if (value is null) throw new ArgumentNullException(nameof(value));
        byte flags = _byName.TryGetValue(name, out var existing) ? existing.Flags
            : PropertyMapWriter.WellKnown(name) is { IsDdl: true } ? Property.DdlFlag : (byte)0;
        Put(existing?.Name ?? name, dataType, value, flags);
    }

    /// <summary>
    /// Gives the property named <paramref name="name"/> the value <paramref name="value"/>, kept as
    /// <paramref name="dataType"/>, and says whether it is a DDL property: one Access lets only a user who may
    /// change the object's design change. Nothing is written until <see cref="Save"/>.
    /// </summary>
    /// <param name="name">The property's name: 1 to 64 characters.</param>
    /// <param name="dataType">The type the value is kept as (<see cref="Set(string, DataType, object)"/> lists them).</param>
    /// <param name="value">The property's value, converted to <paramref name="dataType"/>.</param>
    /// <param name="isDdl">Whether the property is a DDL property (<see cref="Property.IsDdl"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The name is not one Access gives a property (<see cref="Set(string, object)"/> says which);
    /// <paramref name="dataType"/> is not a type a property is kept as; or the value cannot be converted to it, is
    /// beyond its range, has no length, or is longer than a property holds.
    /// </exception>
    public void Set(string name, DataType dataType, object value, bool isDdl)
    {
        CheckName(name);
        if (value is null) throw new ArgumentNullException(nameof(value));
        // The other bits of the flag byte are Access's own, and stay as the file had them.
        byte flags = _byName.TryGetValue(name, out var existing) ? existing.Flags : (byte)0;
        flags = isDdl ? (byte)(flags | Property.DdlFlag) : (byte)(flags & ~Property.DdlFlag);
        Put(existing?.Name ?? name, dataType, value, flags);
    }

    private void Put(string name, DataType dataType, object value, byte flags)
    {
        object kept = PropertyMapWriter.Convert(value, dataType, name);
        int length = PropertyMapWriter.Encode(kept, dataType).Length;
        // DAO refuses a property a zero-length value, on making it and on changing it.
        if (length == 0)
            throw new ArgumentException(
                $"The value of property '{name}' has no length, which Access takes for no value: a property has one, or is removed.",
                nameof(value));
        if (length > MaxValueBytes)
            throw new ArgumentException(
                $"The value of property '{name}' is longer than a property holds ({MaxValueBytes} bytes; text is two bytes a character).",
                nameof(value));
        Add(new Property(name, dataType, kept, flags, stored: null));
    }

    private static void CheckName(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (name.Length == 0) throw new ArgumentException("A property's name cannot be empty.", nameof(name));
        // What DAO refuses a property's name for ("... is not a valid name"). A longer name it cut to 64
        // characters when it read one this library had written, and lost the value.
        if (name.Length > MaxNameLength)
            throw new ArgumentException($"A property's name is at most {MaxNameLength} characters; '{name}' has {name.Length}.", nameof(name));
        if (name[0] == ' ' || name.IndexOfAny(RefusedInNames) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException(
                $"'{name}' is not a name Access gives a property: a name has no period, exclamation mark, grave accent, square bracket " +
                "or control character in it, and no space at its start.", nameof(name));
    }

    /// <summary>
    /// Takes away the property named <paramref name="name"/>, whatever the case of its letters. Nothing is
    /// written until <see cref="Save"/>.
    /// </summary>
    /// <returns>Whether there was a property of that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool Remove(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (!_byName.TryGetValue(name, out var property)) return false;
        _byName.Remove(name);
        _inOrder.Remove(property);
        Owner?.Changed();
        return true;
    }

    /// <summary>
    /// Writes the properties to the file, where Access keeps them: in the <c>LvProp</c> value of the object's
    /// row in the catalog (<c>MSysObjects</c>). A table's own properties and its columns' are one value, so the
    /// changes made to any of those maps are written together. A property that was read from the file and not
    /// changed is written with the bytes it had, and the names the file lists keep their places, so Access finds
    /// what it wrote as it wrote it. The value is written whole or, when writing it fails, not at all. When
    /// nothing has been changed since the properties were read or last saved, nothing is written.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The map is of a column that is in no table of an open database, so there is nowhere to write it.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is opened read-only or is an Access 97 file; the database has no object to keep the properties
    /// with (a file with no <c>SummaryInfo</c> or <c>UserDefined</c> document: this library does not add one);
    /// the properties in the file are not all ones this library read, so writing them would lose some; or the
    /// catalog has an index whose keys this library does not write. Nothing is written.
    /// </exception>
    public void Save()
    {
        if (Owner is null)
            throw new InvalidOperationException(
                $"The properties of '{Name}' belong to no table of an open database, so they cannot be saved.");
        Owner.Save();
    }

    /// <summary>The map's name ("&lt;default&gt;" for a table's own) and its properties.</summary>
    public override string ToString()
        => $"{(string.IsNullOrEmpty(Name) ? "<default>" : Name)} {{ {string.Join(", ", _inOrder)} }}";

    /// <summary>One named, typed property within a <see cref="PropertyMap"/>.</summary>
    public sealed class Property
    {
        /// <summary>The bit of a property's flags that marks a DDL property.</summary>
        internal const byte DdlFlag = 0x01;

        internal Property(string name, DataType dataType, object? value, bool isDdl)
            : this(name, dataType, value, isDdl ? DdlFlag : (byte)0, stored: null)
        {
        }

        internal Property(string name, DataType dataType, object? value, byte flags, byte[]? stored)
        {
            Name     = name;
            DataType = dataType;
            Value    = value;
            Flags    = flags;
            Stored   = stored;
        }

        /// <summary>The property's name.</summary>
        public string Name { get; }

        /// <summary>The type the property's value is stored as.</summary>
        public DataType DataType { get; }

        /// <summary>The property's value, read as a column of <see cref="DataType"/> is read; null when it has none.</summary>
        public object? Value { get; }

        /// <summary>
        /// Whether Access marks the property as one set through data-definition statements (DDL) rather than
        /// through its own interface. Jackcess's <c>isDdl</c>.
        /// </summary>
        public bool IsDdl => (Flags & DdlFlag) != 0;

        /// <summary>The property's flag byte as the file has it: bit 0 is <see cref="IsDdl"/>, the rest Access's own.</summary>
        internal byte Flags { get; }

        /// <summary>
        /// The value's bytes as the file had them, for a property read from a file: written back as they are,
        /// whatever this library made of them. Null for a property a caller set.
        /// </summary>
        internal byte[]? Stored { get; }

        /// <summary>The property as "name(type)=value".</summary>
        public override string ToString() => $"{Name}({DataType})={Value}";
    }
}

/// <summary>
/// All property maps attached to a single catalog object (its default map plus one map per column with
/// properties), as one <c>LvProp</c> value holds them. A map is known by its name, whatever its case, and its
/// block type: Access names an index after its column, and an index's properties are a block of their own.
/// </summary>
internal sealed class PropertyMaps : System.Collections.Generic.IEnumerable<PropertyMap>
{
    private readonly Dictionary<(string Name, short Type), PropertyMap> _maps = new(KeyComparer.Instance);
    private readonly List<PropertyMap> _inOrder = new();

    /// <summary>The property names the file lists, in its order: a property's entry names it by its place here.</summary>
    internal List<string> Names { get; } = new();

    /// <summary>
    /// Why the maps cannot be written back, or null when they can: the value in the file was not one this
    /// library read whole, so writing what it did read would lose the rest.
    /// </summary>
    internal string? UnreadReason { get; set; }

    /// <summary>What writes the maps to the file; null for maps that belong to no open database.</summary>
    internal Action<PropertyMaps>? Saver { get; set; }

    /// <summary>Whether a map has been changed since the maps were read or last written.</summary>
    internal bool IsDirty { get; private set; }

    /// <summary>Counts the times the maps were read or written, for what is worked out from the file's state to know it is stale.</summary>
    internal int Version { get; private set; }

    internal void Changed() => IsDirty = true;

    // What each column's properties say of it in the file: Required, and Allow Zero Length when it has one.
    private readonly Dictionary<string, (bool Required, bool? AllowZeroLength)> _saved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the file has the column as required. A change not yet saved does not count: Access does not see it.</summary>
    internal bool SavedRequired(string column) => _saved.TryGetValue(column, out var rules) && rules.Required;

    /// <summary>What the file's AllowZeroLength property of the column says, or null when it has none.</summary>
    internal bool? SavedAllowZeroLength(string column) => _saved.TryGetValue(column, out var rules) ? rules.AllowZeroLength : null;

    private static (bool, bool?) RulesOf(PropertyMap map)
        => (map["Required"] is true, map.TryGetValue("AllowZeroLength", out var allow) ? allow is not false : null);

    /// <summary>Notes that the maps are as the file has them: just read, or just written.</summary>
    internal void Saved()
    {
        _saved.Clear();
        foreach (var map in _inOrder)
            if (map.Type == PropertyMap.ColumnType) _saved[map.Name] = RulesOf(map);
        IsDirty = false;
        Version++;
    }

    /// <summary>
    /// Takes in the properties of a column that were written to the file apart from these maps - a column just
    /// added - with the names the file now lists. What had been changed here and not saved stays so.
    /// </summary>
    internal void Adopt(PropertyMap written, IReadOnlyList<string> names)
    {
        bool wasDirty = IsDirty;
        var map = GetOrAdd(written.Name, written.Type);
        foreach (var property in map.ToList()) map.Remove(property.Name);
        foreach (var property in written) map.Add(property);
        Names.Clear();
        Names.AddRange(names);
        _saved[written.Name] = RulesOf(written);
        IsDirty = wasDirty;
        Version++;
    }

    /// <summary>
    /// Takes the map of column <paramref name="column"/> out - the column was dropped - and says whether there
    /// was one. The names the file lists stay: Access leaves them when it drops a column.
    /// </summary>
    internal bool RemoveColumn(string column)
    {
        if (!_maps.TryGetValue((column, PropertyMap.ColumnType), out var map)) return false;
        _maps.Remove((column, PropertyMap.ColumnType));
        _inOrder.Remove(map);
        _saved.Remove(column);
        Changed();
        return true;
    }

    /// <summary>
    /// Gives the map of column <paramref name="column"/> the name <paramref name="newName"/> - the column was
    /// renamed - where it is among the maps, every property of it as it was, and says whether there was one.
    /// </summary>
    internal bool RenameColumn(string column, string newName)
    {
        if (!_maps.TryGetValue((column, PropertyMap.ColumnType), out var map)) return false;
        var renamed = new PropertyMap(newName, map.Type) { IsEmptyInFile = map.IsEmptyInFile };
        foreach (var property in map) renamed.Add(property);
        renamed.Owner = this;

        _maps.Remove((column, PropertyMap.ColumnType));
        if (_maps.TryGetValue((newName, PropertyMap.ColumnType), out var leftover)) _inOrder.Remove(leftover);
        _inOrder[_inOrder.IndexOf(map)] = renamed;
        _maps[(newName, PropertyMap.ColumnType)] = renamed;
        if (_saved.TryGetValue(column, out var rules))
        {
            _saved.Remove(column);
            _saved[newName] = rules;
        }
        Changed();
        return true;
    }

    /// <summary>
    /// Makes the same change to these maps - a table's open ones - as was written to the file apart from them
    /// when a column was dropped (<paramref name="newName"/> null) or renamed. What had been changed here and
    /// not saved stays so.
    /// </summary>
    internal void AdoptColumnChange(string column, string? newName)
    {
        bool wasDirty = IsDirty;
        if (newName is null) RemoveColumn(column);
        else RenameColumn(column, newName);
        IsDirty = wasDirty;
        Version++;
    }

    /// <summary>The unnamed (table-level) property map, made when there is none.</summary>
    public PropertyMap Default => GetOrAdd(string.Empty, PropertyMap.DefaultType);

    /// <summary>Returns the column map for <paramref name="name"/> (a column name) or null.</summary>
    public PropertyMap? Get(string name)
        => _maps.TryGetValue((name, PropertyMap.ColumnType), out var m) ? m : null;

    /// <summary>Whether there is a map of that name and block type.</summary>
    internal bool Has(string name, short type) => _maps.ContainsKey((name, type));

    /// <summary>The map of that name and block type, made - empty, and not in the file - when there is none.</summary>
    internal PropertyMap GetOrAdd(string name, short type)
    {
        if (!_maps.TryGetValue((name, type), out var map))
            Add(map = new PropertyMap(name, type));
        return map;
    }

    /// <summary>True if there are no property maps at all.</summary>
    public bool IsEmpty => _maps.Count == 0;

    /// <summary>Number of maps (default + per-column).</summary>
    public int Count => _maps.Count;

    internal void Add(PropertyMap map)
    {
        if (_maps.TryGetValue((map.Name, map.Type), out var earlier))
            _inOrder[_inOrder.IndexOf(earlier)] = map;
        else
            _inOrder.Add(map);
        _maps[(map.Name, map.Type)] = map;
        map.Owner = this;
    }

    /// <summary>Notes that the maps are in the file as they now are, with <paramref name="names"/> the names it lists.</summary>
    internal void Written(IReadOnlyList<string> names)
    {
        Names.Clear();
        Names.AddRange(names);
        Saved();
    }

    internal void Save()
    {
        if (Saver is null)
            throw new InvalidOperationException("The properties belong to no open database, so they cannot be saved.");
        // Nothing changed, nothing written: a row of the catalog rewritten moves, and leaves its old place dead.
        if (!IsDirty) return;
        Saver(this);
    }

    public System.Collections.Generic.IEnumerator<PropertyMap> GetEnumerator()
        => _inOrder.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();

    private sealed class KeyComparer : IEqualityComparer<(string Name, short Type)>
    {
        internal static readonly KeyComparer Instance = new();

        public bool Equals((string Name, short Type) x, (string Name, short Type) y)
            => x.Type == y.Type && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, short Type) key)
            => StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name) ^ key.Type;
    }
}
