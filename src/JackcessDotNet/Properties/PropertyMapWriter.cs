using System.Globalization;
using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Encodes a <see cref="PropertyMaps"/> as the value Access keeps in the "LvProp" column of an MSysObjects row:
/// the layout <see cref="PropertyMapReader"/> describes, as Jackcess's <c>PropertyMaps.Handler.write</c> writes
/// it. What was read from a file goes back as it was read - the names the file listed, in their places, and each
/// value's own bytes and flags - so a value read and written with nothing changed is the same bytes, and one
/// with a property added still holds every property Access wrote, byte for byte. (Jackcess lists the names
/// again from the properties in use, and encodes every value again.)
/// </summary>
internal static class PropertyMapWriter
{
    private static readonly byte[] SignatureJet4 = { (byte)'M', (byte)'R', (byte)'2', 0x00 };
    private const short BlockTypeNameList = 0x0080;

    /// <summary>
    /// The value for <paramref name="maps"/>, or null when they hold no property and the file had no value for
    /// them: a catalog row with no properties has no value. Maps whose properties have all been taken away
    /// leave a value that lists names and has no property, as Access leaves one.
    /// </summary>
    /// <exception cref="NotSupportedException">The maps were read from a value this library did not read whole.</exception>
    public static byte[]? Write(PropertyMaps maps) => Write(maps, out _);

    /// <summary>As <see cref="Write(PropertyMaps)"/>, with the names the value lists, in its order, in <paramref name="names"/>.</summary>
    public static byte[]? Write(PropertyMaps maps, out List<string> names)
    {
        if (maps.UnreadReason is { } why)
            throw new NotSupportedException(
                $"The properties are not written: the ones in the file were not all read ({why}), and writing those that were would lose the rest. Nothing was changed.");

        // The names the file listed keep their places; a name it did not list follows them.
        names = new List<string>(maps.Names);
        var written = maps.Where(m => m.Count > 0 || m.IsEmptyInFile).ToList();
        if (written.Count == 0 && names.Count == 0) return null;

        foreach (var map in written)
            foreach (var property in map)
                if (IndexOf(names, property.Name) < 0)
                    names.Add(property.Name);

        using var buffer = new MemoryStream();
        buffer.Write(SignatureJet4, 0, SignatureJet4.Length);

        long block = BeginBlock(buffer, BlockTypeNameList);
        foreach (string listed in names)
            PutName(buffer, listed);
        EndBlock(buffer, block);

        foreach (var map in written)
        {
            block = BeginBlock(buffer, map.Type);

            // The map's name, in a block of its own: its length, then the name as every name is written.
            long nameBlock = buffer.Position;
            PutInt(buffer, 0);
            PutName(buffer, map.Name);
            EndBlock(buffer, nameBlock);

            foreach (var property in map)
            {
                byte[] data = property.Stored ?? Encode(property.Value!, property.DataType);
                PutShort(buffer, 8 + data.Length);
                buffer.WriteByte(property.Flags);
                buffer.WriteByte((byte)property.DataType);
                PutShort(buffer, IndexOf(names, property.Name));
                PutShort(buffer, data.Length);
                buffer.Write(data, 0, data.Length);
            }
            EndBlock(buffer, block);
        }
        return buffer.ToArray();
    }

    // A name's place in the list: as it is spelled when it is there so, and otherwise whatever its case.
    private static int IndexOf(List<string> names, string name)
    {
        int exact = names.IndexOf(name);
        return exact >= 0 ? exact : names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    private static long BeginBlock(Stream buffer, short type)
    {
        long start = buffer.Position;
        PutInt(buffer, 0);
        PutShort(buffer, type);
        return start;
    }

    // A block's first four bytes are its length, itself included.
    private static void EndBlock(Stream buffer, long start)
    {
        long end = buffer.Position;
        buffer.Position = start;
        PutInt(buffer, (int)(end - start));
        buffer.Position = end;
    }

    // A name as the code units it holds: a map's name is its column's, which a file can have with half of a surrogate
    // pair alone in it, and Encoding.Unicode would write U+FFFD in its place (ByteUtil.DecodeUtf16 reads it back).
    private static void PutName(Stream buffer, string name)
    {
        byte[] bytes = ByteUtil.EncodeUtf16(name);
        PutShort(buffer, bytes.Length);
        buffer.Write(bytes, 0, bytes.Length);
    }

    private static void PutInt(Stream buffer, int value)
    {
        var bytes = new byte[4];
        ByteUtil.PutInt(bytes, 0, value);
        buffer.Write(bytes, 0, 4);
    }

    private static void PutShort(Stream buffer, int value)
    {
        buffer.WriteByte((byte)value);
        buffer.WriteByte((byte)(value >> 8));
    }

    // ── What a property is kept as ───────────────────────────────────────────

    /// <summary>
    /// The type Access keeps a property of a well-known name as, and whether it is a DDL property: Jackcess's
    /// list, but that <c>Description</c> is Text and a DDL property, as Access's table designer writes it.
    /// </summary>
    private static readonly Dictionary<string, (DataType Type, bool IsDdl)> KnownProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AccessVersion"]   = (DataType.Text, false),
        ["Title"]           = (DataType.Text, false),
        ["Author"]          = (DataType.Text, false),
        ["Company"]         = (DataType.Text, false),
        ["DefaultValue"]    = (DataType.Memo, true),
        ["Required"]        = (DataType.Boolean, true),
        ["AllowZeroLength"] = (DataType.Boolean, true),
        ["DecimalPlaces"]   = (DataType.Byte, true),
        ["Format"]          = (DataType.Text, true),
        ["InputMask"]       = (DataType.Text, true),
        ["Caption"]         = (DataType.Memo, false),
        ["ValidationRule"]  = (DataType.Text, true),
        ["ValidationText"]  = (DataType.Text, true),
        ["GUID"]            = (DataType.Binary, true),
        ["Description"]     = (DataType.Text, true),
        ["ResultType"]      = (DataType.Byte, true),
        ["Expression"]      = (DataType.Memo, true),
        ["DisplayControl"]  = (DataType.Int16, false),
        ["TextFormat"]      = (DataType.Byte, false),
        ["IMEMode"]         = (DataType.Byte, false),
        ["IMESentenceMode"] = (DataType.Byte, false),
    };

    /// <summary>What a property named <paramref name="name"/> is kept as when Access has a type for the name; null otherwise.</summary>
    internal static (DataType Type, bool IsDdl)? WellKnown(string name)
        => KnownProperties.TryGetValue(name, out var known) ? known : null;

    /// <summary>
    /// The type a property is kept as when only its value says: the value's own. A <see cref="long"/> has none:
    /// DAO refuses to make a Large Number property, and reads one this library wrote as 0 (Jackcess writes one).
    /// </summary>
    /// <exception cref="ArgumentException">The value is of a type no property is kept as.</exception>
    internal static DataType TypeOf(object value, string name) => value switch
    {
        string   => DataType.Text,
        bool     => DataType.Boolean,
        byte     => DataType.Byte,
        short    => DataType.Int16,
        int      => DataType.Int32,
        float    => DataType.Single,
        double   => DataType.Double,
        decimal  => DataType.Currency,
        DateTime => DataType.DateTime,
        Guid     => DataType.Guid,
        byte[]   => DataType.OleObject,
        _ => throw new ArgumentException(
            $"The value of property '{name}' is a {value.GetType().Name}, which no property is kept as: give its DataType, or a value of a type that has one.",
            nameof(value)),
    };

    /// <summary>
    /// <paramref name="value"/> as a property of <paramref name="dataType"/> holds it: the value a property that
    /// is read back has.
    /// </summary>
    /// <exception cref="ArgumentException">The type is not one a property is kept as, or the value cannot be converted to it.</exception>
    internal static object Convert(object value, DataType dataType, string name)
    {
        var invariant = CultureInfo.InvariantCulture;
        try
        {
            switch (dataType)
            {
                case DataType.Boolean:  return System.Convert.ToBoolean(value, invariant);
                case DataType.Byte:     return System.Convert.ToByte(value, invariant);
                case DataType.Int16:    return System.Convert.ToInt16(value, invariant);
                case DataType.Int32:    return System.Convert.ToInt32(value, invariant);
                case DataType.Single:
                {
                    // Text is the single DAO stores for it, on every target (Util.NumberText, #124). A number too
                    // large for a Single converts to infinity without a word; it is refused instead.
                    float single = value is string singleText
                        ? Util.NumberText.ToSingle(singleText, NumberStyles.Float | NumberStyles.AllowThousands, invariant)
                        : System.Convert.ToSingle(value, invariant);
                    if (float.IsInfinity(single) && !(value is float or double && double.IsInfinity(System.Convert.ToDouble(value, invariant))))
                        throw OutOfRange(name, value, dataType, "it is beyond what a Single holds");
                    return single;
                }
                case DataType.Double:
                    // Text is the double DAO stores for it, on every target (Util.NumberText, #124).
                    return value is string doubleText
                        ? Util.NumberText.ToDouble(doubleText, NumberStyles.Float | NumberStyles.AllowThousands, invariant)
                        : System.Convert.ToDouble(value, invariant);
                case DataType.Currency:
                {
                    // A Currency is a 64-bit count of ten-thousandths. A number is taken to them as Access takes one, its
                    // exact value, half to even, as a row's is (Util.CurrencyRounding, #65); any other value as the decimal
                    // it converts to - text in the invariant culture.
                    object number = Util.CurrencyRounding.IsNumber(value) ? value : System.Convert.ToDecimal(value, invariant);
                    if (!Util.CurrencyRounding.TryUnits(number, out long units))
                        throw new ArgumentException(
                            $"The value of property '{name}' cannot be kept as {dataType}: {Util.CurrencyRounding.NotHeld(number)}.", nameof(value));
                    return Util.CurrencyRounding.ToDecimal(units);
                }
                case DataType.DateTime:
                {
                    // Access's dates begin with the year 100, and so does what a row stores (Util.OADate).
                    DateTime date = System.Convert.ToDateTime(value, invariant);
                    if (date < MinDate)
                        throw OutOfRange(name, value, dataType, "a Date/Time is from 1 January 100 to 31 December 9999");
                    return date;
                }
                case DataType.Guid:
                    return value switch
                    {
                        Guid guid                   => guid,
                        byte[] { Length: 16 } bytes => new Guid(bytes),
                        _                           => Guid.Parse(System.Convert.ToString(value, invariant)!),
                    };
                case DataType.Text:
                case DataType.Memo:
                    // A double or a float as Access writes one as text, on every target (Util.NumberText, #124).
                    return value switch
                    {
                        double d => Util.NumberText.Text(d, invariant),
                        float f  => Util.NumberText.Text(f, invariant),
                        _        => System.Convert.ToString(value, invariant) ?? string.Empty,
                    };
                case DataType.Binary:
                case DataType.OleObject:
                    if (value is byte[] given) return (byte[])given.Clone();
                    break;
                default:
                    throw new ArgumentException(
                        $"Property '{name}' cannot be kept as {dataType}: a property is a Yes/No, a Byte, an Integer, a Long Integer, " +
                        "a Single, a Double, a Currency, a Date/Time, a Replication ID, Text, a Memo, Binary or an OLE Object. " +
                        "Access keeps none as a Large Number or a Decimal.", nameof(dataType));
            }
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException(
                $"The value of property '{name}' ({value.GetType().Name}) cannot be kept as {dataType}: {e.Message}", nameof(value), e);
        }
        throw new ArgumentException(
            $"The value of property '{name}' ({value.GetType().Name}) cannot be kept as {dataType}: it is not a byte array.", nameof(value));
    }

    private static readonly DateTime MinDate = new DateTime(100, 1, 1);

    private static ArgumentException OutOfRange(string name, object value, DataType dataType, string why)
        => new ArgumentException(
            $"The value of property '{name}' ({System.Convert.ToString(value, CultureInfo.InvariantCulture)}) cannot be kept as {dataType}: {why}.", nameof(value));

    /// <summary>The bytes of a value <see cref="Convert"/> returned, as Access stores a property of that type.</summary>
    internal static byte[] Encode(object value, DataType dataType)
    {
        switch (dataType)
        {
            // Yes is 1 as Access's table designer and its SQL write it (DAO writes 0xFF; every reader takes
            // any byte but 0 for Yes).
            case DataType.Boolean: return new[] { (bool)value ? (byte)1 : (byte)0 };
            case DataType.Byte:    return new[] { (byte)value };
            case DataType.Int16:   return BitConverter.GetBytes((short)value).LittleEndian();
            case DataType.Int32:   return BitConverter.GetBytes((int)value).LittleEndian();
            case DataType.Single:  return BitConverter.GetBytes((float)value).LittleEndian();
            case DataType.Double:  return BitConverter.GetBytes((double)value).LittleEndian();
            case DataType.Currency:
                return BitConverter.GetBytes(decimal.ToInt64((decimal)value * 10000m)).LittleEndian();
            case DataType.DateTime: return BitConverter.GetBytes(OADate.FromDateTime((DateTime)value)).LittleEndian();
            case DataType.Guid:    return ((Guid)value).ToByteArray();
            case DataType.Text:
            case DataType.Memo:    return Encoding.Unicode.GetBytes((string)value);
            default:               return (byte[])value;
        }
    }

    private static byte[] LittleEndian(this byte[] bytes)
    {
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }
}
