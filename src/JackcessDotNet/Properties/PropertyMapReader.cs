using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Decodes the binary property-map blob stored in the "LvProp" column of
/// MSysObjects rows. Direct port of Jackcess Java's
/// <c>PropertyMaps.Handler.read</c>.
///
/// Wire format (Access 2000+):
///   [0..3]  signature "MR2\0" (Jet4+) or "KKD\0" (Jet3)
///   then a stream of chunks, each:
///     [4 bytes] chunk length (includes itself)
///     [2 bytes] block type:
///                 0x80 — property-name list
///                 0x00 — default-property-value list (table-level)
///                 0x01 — column-property-value list (one block per column)
///     [length-6 bytes] block data
///
///   Name-list block: sequence of (short length, name bytes) pairs.
///   Value-list block:
///     [4 bytes] map-name sub-block length (read a name inside if len > 6)
///     then per-property:
///         [2 bytes] value-entry length
///         [1 byte ] isDdl flag
///         [1 byte ] DataType code
///         [2 bytes] index into the name list
///         [2 bytes] value byte count
///         [N bytes] raw value bytes
/// </summary>
internal static class PropertyMapReader
{
    private static readonly byte[] SignatureJet4 = { (byte)'M', (byte)'R', (byte)'2', 0x00 };
    private static readonly byte[] SignatureJet3 = { (byte)'K', (byte)'K', (byte)'D', 0x00 };
    private const short BlockTypeNameList     = 0x0080;
    private const short BlockTypeDefaultValue = 0x0000;
    private const short BlockTypeColumnValue  = 0x0001;

    /// <summary>
    /// Decodes <paramref name="propBytes"/> into a <see cref="PropertyMaps"/>.
    /// Returns an empty <see cref="PropertyMaps"/> on null/empty input or when
    /// the signature is unrecognised. What is read is kept as it was stored - the names in their order, each
    /// value's bytes and flags - so that <see cref="PropertyMapWriter"/> writes back the value it was read from;
    /// a value that was not read whole says why in <see cref="PropertyMaps.UnreadReason"/>, and is not written.
    /// </summary>
    public static PropertyMaps Read(byte[]? propBytes, JetFormat format)
    {
        var maps = ReadBlocks(propBytes, format);
        maps.Saved();   // they are what the file has
        return maps;
    }

    private static PropertyMaps ReadBlocks(byte[]? propBytes, JetFormat format)
    {
        var maps = new PropertyMaps();
        if (propBytes is null || propBytes.Length == 0) return maps;

        int pos = 0;
        if (Matches(propBytes, 0, SignatureJet4)) pos = 4;
        else if (Matches(propBytes, 0, SignatureJet3)) pos = 4;
        else
        {
            maps.UnreadReason = "the value does not begin as a property value does";
            return maps;   // unknown signature — give up gracefully
        }

        bool namesRead = false;
        while (pos + 6 <= propBytes.Length)
        {
            int   chunkLen   = ByteUtil.GetInt  (propBytes, pos);
            short blockType  = ByteUtil.GetShort(propBytes, pos + 4);
            int   blockStart = pos + 6;
            int   blockEnd   = pos + chunkLen;
            if (chunkLen < 6 || blockEnd > propBytes.Length) break;

            if (blockType == BlockTypeNameList)
            {
                // One list of names, before the values that use it, is all a value this library writes back has.
                if (namesRead || !maps.IsEmpty) maps.UnreadReason ??= "the value lists its property names more than once";
                namesRead = true;
                if (!ReadNameList(propBytes, blockStart, blockEnd, format, maps.Names))
                    maps.UnreadReason ??= "the list of property names is not whole";
            }
            else if (namesRead)
                ReadValueBlock(propBytes, blockStart, blockEnd, blockType, maps.Names, format, maps);
            else
                maps.UnreadReason ??= "the value has properties before it has their names";

            pos = blockEnd;
        }
        if (pos != propBytes.Length) maps.UnreadReason ??= "the value has bytes that are not a block of properties";

        return maps;
    }

    private static bool ReadNameList(byte[] buf, int start, int end, JetFormat format, List<string> names)
    {
        int pos = start;
        while (pos + 2 <= end)
        {
            int nameLen = ByteUtil.GetShort(buf, pos);
            pos += 2;
            if (nameLen < 0 || pos + nameLen > end) return false;
            names.Add(DecodeName(buf, pos, nameLen, format));
            pos += nameLen;
        }
        return pos == end;
    }

    private static void ReadValueBlock(
        byte[] buf, int start, int end, short blockType,
        List<string> propNames, JetFormat format, PropertyMaps maps)
    {
        int pos = start;

        // The block begins with a sub-block carrying the map name (or empty
        // for the default/table-level map). Layout: 4-byte sub-length, then if
        // sub-length > 6, a length-prefixed name.
        string mapName = string.Empty;
        if (pos + 4 <= end)
        {
            int subLen = ByteUtil.GetInt(buf, pos);
            int subEnd = pos + subLen;
            int nameLen = subLen >= 6 && subEnd <= end ? ByteUtil.GetShort(buf, pos + 4) : -1;
            if (nameLen >= 0 && pos + 6 + nameLen <= end)
                mapName = DecodeName(buf, pos + 6, nameLen, format);
            // Written back, the sub-block is its length, the name's length and the name, and nothing else.
            if (subLen < 4 || subEnd > end || nameLen < 0 || subLen != 6 + nameLen)
                maps.UnreadReason ??= $"the name of a block of properties ('{mapName}') is not laid out as this library writes one";
            pos = subEnd;
        }
        else
            maps.UnreadReason ??= "a block of properties has no name";

        if (maps.Has(mapName, blockType))
            maps.UnreadReason ??= $"the value has two blocks of properties for '{mapName}'";
        var map = new PropertyMap(mapName, blockType);

        while (pos + 8 <= end)
        {
            int  valLen   = ByteUtil.GetShort(buf, pos);
            int  valEnd   = pos + valLen;
            if (valLen < 8 || valEnd > end) break;
            byte flags    = buf[pos + 2];
            byte dtByte   = buf[pos + 3];
            int  nameIdx  = ByteUtil.GetShort(buf, pos + 4);
            int  dataSize = ByteUtil.GetShort(buf, pos + 6);
            int  dataStart = pos + 8;
            if (dataSize < 0 || dataStart + dataSize > end) break;
            if (valLen != 8 + dataSize)
                maps.UnreadReason ??= $"a property of '{mapName}' has bytes after its value";

            if (nameIdx >= 0 && nameIdx < propNames.Count)
            {
                string propName = propNames[nameIdx];
                var dataType = (DataType)dtByte;
                object? value = DecodeValue(buf, dataStart, dataSize, dataType, format);
                if (map.TryGetProperty(propName, out _))
                    maps.UnreadReason ??= $"'{mapName}' has the property '{propName}' twice";
                map.Add(new PropertyMap.Property(propName, dataType, value, flags, buf.Slice(dataStart, dataStart + dataSize)));
            }
            else
                maps.UnreadReason ??= $"a property of '{mapName}' has a name the value does not list";

            pos = valEnd;
        }
        if (pos != end) maps.UnreadReason ??= $"the properties of '{mapName}' are not whole";

        map.IsEmptyInFile = map.Count == 0;
        maps.Add(map);
    }

    private static string DecodeName(byte[] buf, int pos, int len, JetFormat format)
    {
        // Property names use the database charset (cp1252 for Jet3, UTF-16LE for Jet4+).
        return format.Version == FileFormat.Access97
            ? format.TextEncoding.GetString(buf, pos, len)
            : Encoding.Unicode.GetString(buf, pos, len);
    }

    private static object? DecodeValue(byte[] buf, int pos, int len, DataType dt, JetFormat format)
    {
        // A value of no length is Text or bytes of none, as DAO reads it (''), and no value of any other type.
        if (len == 0)
            return dt switch
            {
                DataType.Text or DataType.Memo        => string.Empty,
                DataType.Binary or DataType.OleObject => Array.Empty<byte>(),
                _                                     => null,
            };
        return dt switch
        {
            DataType.Boolean       => buf[pos] != 0,
            DataType.Byte          => buf[pos],
            DataType.Int16           => ByteUtil.GetShort (buf, pos),
            DataType.Int32          => ByteUtil.GetInt   (buf, pos),
            DataType.Currency         => Math.Round((decimal)ByteUtil.GetLong(buf, pos) / 10000m, 4),
            DataType.Single         => ByteUtil.GetFloat (buf, pos),
            DataType.Double        => ByteUtil.GetDouble(buf, pos),
            DataType.DateTime => TryFromOADate(ByteUtil.GetDouble(buf, pos)),
            DataType.Guid          => new Guid(buf.Slice(pos, pos + Math.Min(16, len))),
            DataType.Text          => format.Version == FileFormat.Access97
                                          ? format.TextEncoding.GetString(buf, pos, len)
                                          : Encoding.Unicode.GetString(buf, pos, len),
            DataType.Memo          => format.Version == FileFormat.Access97
                                          ? format.TextEncoding.GetString(buf, pos, len)
                                          : Encoding.Unicode.GetString(buf, pos, len),
            DataType.Binary        => buf.Slice(pos, pos + len),
            DataType.OleObject           => buf.Slice(pos, pos + len),
            DataType.Int64        => ByteUtil.GetLong  (buf, pos),
            _                      => null,
        };
    }

    private static DateTime? TryFromOADate(double oa)
    {
        if (double.IsNaN(oa) || double.IsInfinity(oa)) return null;
        if (oa < -657434.0 || oa > 2958465.99999999) return null;
        try   { return DateTime.FromOADate(oa); }
        catch { return null; }
    }

    private static bool Matches(byte[] buf, int offset, byte[] expected)
    {
        if (offset + expected.Length > buf.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (buf[offset + i] != expected[i]) return false;
        return true;
    }
}
