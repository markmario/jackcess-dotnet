namespace JackcessDotNet;

/// <summary>
/// One of the properties Access keeps for a table, a column or the database - its name, the type its value is kept
/// as, and the value - as a <see cref="PropertyMap"/> lists it. <see cref="PropertyMap.Set(string, object)"/> and
/// its overloads change one.
/// </summary>
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
