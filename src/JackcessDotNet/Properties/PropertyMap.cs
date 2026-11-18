using System.Collections;

namespace JackcessDotNet;

/// <summary>
/// The properties Access keeps for a table or for one of its columns, by name: what
/// <see cref="Table.Properties"/> and <see cref="Column.Properties"/> return, as in Jackcess. It is the
/// collection of its properties, in the order the file lists them.
///
/// Access stores well-known property names like:
///   "Description", "Caption", "Format", "DefaultValue", "ValidationRule",
///   "InputMask", "Required", "AllowZeroLength", "DecimalPlaces"
///
/// Properties are read; this library does not write them.
/// </summary>
public sealed class PropertyMap : IReadOnlyCollection<PropertyMap.Property>
{
    /// <summary>
    /// The name of what the properties belong to: the empty string for a table's own properties, the column's
    /// name for a column's.
    /// </summary>
    public string Name { get; }

    /// <summary>Block-type byte from the on-disk stream (0x00 default, 0x01 column).</summary>
    internal byte Type { get; }

    // By name, whatever its case, and in the order they were read.
    private readonly Dictionary<string, Property> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Property> _inOrder = new();

    internal PropertyMap(string name, byte type) { Name = name; Type = type; }

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

    /// <summary>The number of properties: 0 for a table or column Access keeps none for.</summary>
    public int Count => _inOrder.Count;

    /// <summary>The properties, in the order the file lists them.</summary>
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
    }

    /// <summary>The map's name ("&lt;default&gt;" for a table's own) and its properties.</summary>
    public override string ToString()
        => $"{(string.IsNullOrEmpty(Name) ? "<default>" : Name)} {{ {string.Join(", ", _inOrder)} }}";

    /// <summary>One named, typed property within a <see cref="PropertyMap"/>.</summary>
    public sealed class Property
    {
        internal Property(string name, DataType dataType, object? value, bool isDdl)
        {
            Name     = name;
            DataType = dataType;
            Value    = value;
            IsDdl    = isDdl;
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
        public bool IsDdl { get; }

        /// <summary>The property as "name(type)=value".</summary>
        public override string ToString() => $"{Name}({DataType})={Value}";
    }
}

/// <summary>
/// All property maps attached to a single catalog object (its default map plus
/// one map per column-with-properties). Keyed by map name (case-insensitive).
/// </summary>
internal sealed class PropertyMaps : System.Collections.Generic.IEnumerable<PropertyMap>
{
    private readonly Dictionary<string, PropertyMap> _maps =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The unnamed (table-level) property map.</summary>
    public PropertyMap Default
        => _maps.TryGetValue(string.Empty, out var m) ? m : _empty;
    private static readonly PropertyMap _empty = new(string.Empty, 0x00);

    /// <summary>Returns the map for <paramref name="name"/> (typically a column name) or null.</summary>
    public PropertyMap? Get(string name)
        => _maps.TryGetValue(name, out var m) ? m : null;

    /// <summary>True if there are no property maps at all.</summary>
    public bool IsEmpty => _maps.Count == 0;

    /// <summary>Number of maps (default + per-column).</summary>
    public int Count => _maps.Count;

    internal void Add(PropertyMap map) => _maps[map.Name] = map;

    public System.Collections.Generic.IEnumerator<PropertyMap> GetEnumerator()
        => _maps.Values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}
