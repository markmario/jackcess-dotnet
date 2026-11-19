using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Changes to the bytes Access keeps an object's properties in (MSysObjects.LvProp) that go with a change to a
/// table's columns (#37): the block of a column that is dropped is taken out, and the block of a column that is
/// renamed has its name changed - as Access was seen to do for each. Everything else is left byte for byte: the
/// list of property names (which goes on naming the properties of a block that is gone, as Access leaves it),
/// and every other block.
/// <para>
/// The bytes, as <see cref="PropertyMapReader"/> reads them: "MR2\0", then chunks of a 4-byte length (itself
/// counted), a 2-byte type and data. A chunk of type 1 is a column's: its data begins with a 4-byte length (itself
/// counted), a 2-byte length of the column's name, and the name in UTF-16.
/// </para>
/// </summary>
internal static class PropertyBlob
{
    private const short BlockTypeColumn = 0x0001;

    /// <summary>
    /// <paramref name="blob"/> without the block of the column named <paramref name="column"/>, or null when
    /// it has no such block - or is not laid out as expected - and so is left as it is.
    /// </summary>
    internal static byte[]? WithoutColumn(byte[]? blob, string column)
    {
        if (Find(blob, column) is not { } found) return null;
        var result = new byte[blob!.Length - found.Length];
        Array.Copy(blob, 0, result, 0, found.Start);
        Array.Copy(blob, found.Start + found.Length, result, found.Start, blob.Length - found.Start - found.Length);
        return result;
    }

    /// <summary>
    /// <paramref name="blob"/> with the block of the column named <paramref name="column"/> named
    /// <paramref name="newName"/> instead, or null when it has no such block and so is left as it is.
    /// </summary>
    internal static byte[]? WithColumnRenamed(byte[]? blob, string column, string newName)
    {
        if (Find(blob, column) is not { } found) return null;
        byte[] name  = Encoding.Unicode.GetBytes(newName);
        int    delta = name.Length - found.NameLength;
        int    nameAt = found.Start + 6 + 6;

        var result = new byte[blob!.Length + delta];
        Array.Copy(blob, 0, result, 0, nameAt);
        Array.Copy(name, 0, result, nameAt, name.Length);
        Array.Copy(blob, nameAt + found.NameLength, result, nameAt + name.Length, blob.Length - nameAt - found.NameLength);
        ByteUtil.PutInt(result, found.Start, found.Length + delta);
        ByteUtil.PutInt(result, found.Start + 6, ByteUtil.GetInt(blob, found.Start + 6) + delta);
        ByteUtil.PutShort(result, found.Start + 10, (short)name.Length);
        return result;
    }

    // The chunk that holds a column's properties: where it starts, its length, and the length of the name in it.
    private static (int Start, int Length, int NameLength)? Find(byte[]? blob, string column)
    {
        if (blob is null || blob.Length < 4 || blob[0] != (byte)'M' || blob[1] != (byte)'R' || blob[2] != (byte)'2' || blob[3] != 0)
            return null;

        int pos = 4;
        while (pos + 6 <= blob.Length)
        {
            int length = ByteUtil.GetInt(blob, pos);
            if (length <= 6 || pos + length > blob.Length) return null;
            if (ByteUtil.GetShort(blob, pos + 4) == BlockTypeColumn && length >= 12)
            {
                int nameBlock  = ByteUtil.GetInt(blob, pos + 6);
                int nameLength = ByteUtil.GetShort(blob, pos + 10);
                if (nameBlock == 6 + nameLength && nameLength >= 0 && 12 + nameLength <= length
                    && string.Equals(Encoding.Unicode.GetString(blob, pos + 12, nameLength), column, StringComparison.OrdinalIgnoreCase))
                    return (pos, length, nameLength);
            }
            pos += length;
        }
        return null;
    }
}
