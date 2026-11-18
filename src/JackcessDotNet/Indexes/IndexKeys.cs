using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// The bytes an index entry's key is made of, as Access writes them: for each
/// column of the index, a flag (0x7F ascending, 0x80 descending; 0x00 or 0xFF
/// for a null) and then the value's bytes, each column with its own flag. The
/// value's bytes follow the column's type, whatever CLR type the value arrives
/// as: a Long column's key is four bytes even for a value passed as a short.
/// </summary>
internal static class IndexKeys
{
    private const byte AscStartFlag  = 0x7F;
    private const byte DescStartFlag = 0x80;
    private const byte AscNullFlag   = 0x00;
    private const byte DescNullFlag  = 0xFF;

    /// <summary>True for the column types <see cref="Encode"/> can write a key for.</summary>
    public static bool CanEncode(DataType type)
        => type is DataType.Byte or DataType.Int or DataType.Long or DataType.Money
                or DataType.Float or DataType.Double or DataType.ShortDateTime
                or DataType.Boolean or DataType.Text or DataType.Memo or DataType.Guid
                or DataType.BigInt or DataType.ExtendedDateTime;

    /// <summary>
    /// True when <see cref="Encode"/> writes the keys Access writes for <paramref name="column"/>: a type it
    /// encodes and, for text, a column that sorts in an order whose keys it writes (<see cref="TextIndexCodes.For"/>).
    /// </summary>
    public static bool CanEncode(Column column)
        => CanEncode(column.DataType)
        && (column.DataType is not (DataType.Text or DataType.Memo) || TextIndexCodes.For(column.SortOrder) is not null);

    /// <summary>
    /// True when <see cref="Encode"/> writes the key Access writes for <paramref name="value"/> in
    /// <paramref name="column"/>: a column it writes keys for and, for text, a value with no character Access keys
    /// by the characters around it (<see cref="TextIndexCodes.CanEncode"/>).
    /// </summary>
    public static bool CanEncode(Column column, object? value)
        => CanEncode(column)
        && (column.DataType is not (DataType.Text or DataType.Memo) || value is null
            || TextIndexCodes.For(column.SortOrder)!.CanEncode(Convert.ToString(value) ?? string.Empty));

    /// <summary>Why <paramref name="column"/>'s keys cannot be written (<see cref="CanEncode(Column)"/> is false).</summary>
    public static string WhyNot(Column column)
        => !CanEncode(column.DataType)
            ? $"{column.Name} is a {column.DataType} column, whose keys this library does not write"
            : $"{column.Name} sorts in {column.SortOrder}, and this library writes text keys only in {SortOrder.GeneralLegacy} and {SortOrder.General}";

    /// <summary>Why no key is made for <paramref name="value"/> in a column whose keys are written.</summary>
    public static string WhyNot(Column column, object? value)
        => $"Access keys a character of {column.Name}'s value '{value}' by the characters around it, and this library makes no such key";

    /// <summary>
    /// The key of an entry whose columns hold <paramref name="values"/>, in the index's column order. Refuses a
    /// column whose keys it does not write as Access does (<see cref="CanEncode(Column)"/>), rather than make a key
    /// no entry of that index has, and a text value whose key it does not make (<see cref="CanEncode(Column, object?)"/>).
    /// </summary>
    public static byte[] Encode(IReadOnlyList<IndexColumn> columns, IReadOnlyList<object?> values)
    {
        if (values.Count != columns.Count)
            throw new ArgumentException($"The index has {columns.Count} columns; {values.Count} values were given.", nameof(values));
        foreach (var column in columns)
            if (!CanEncode(column.Column))
                throw new NotSupportedException($"No key is made for this index: {WhyNot(column.Column)}.");
        return Truncate(EncodeWhole(columns, values));
    }

    /// <summary>
    /// True when the key of <paramref name="values"/> is longer than an entry holds, so that <see cref="Encode"/>
    /// cuts it. An index may then hold the row under the cut key, as Access and this library write it, or under
    /// the whole key, as versions of this library before the cut and Jackcess before it wrote it.
    /// </summary>
    public static bool IsCut(IReadOnlyList<IndexColumn> columns, IReadOnlyList<object?> values)
        => EncodeWhole(columns, values).Length > MaxKeyLength;

    /// <summary>The key of <paramref name="values"/> whole, however long: what <see cref="Truncate"/> cuts.</summary>
    internal static byte[] EncodeWhole(IReadOnlyList<IndexColumn> columns, IReadOnlyList<object?> values)
    {
        var key = new List<byte>();
        for (int i = 0; i < columns.Count; i++)
            key.AddRange(EncodeColumn(columns[i].Column, columns[i].IsAscending, values[i]));
        return key.ToArray();
    }

    /// <summary>
    /// True when a row with <paramref name="values"/> in <paramref name="columns"/> is surely at the key
    /// <see cref="Encode"/> makes, so that a search of the index that misses means there is no such row: every
    /// value is one a key is made for (<see cref="CanEncode(Column, object?)"/>), and the key is no longer than an
    /// entry holds. Access cuts a longer key and ends it in a digest, and so does <see cref="Encode"/>, but
    /// Jackcess and versions of this library before the digest wrote such a key whole, where a search by the cut
    /// key does not find it. A caller scans the table for values this is false for.
    /// </summary>
    public static bool CanSearch(IReadOnlyList<IndexColumn> columns, IReadOnlyList<object?> values)
    {
        if (values.Count != columns.Count) return false;
        for (int i = 0; i < columns.Count; i++)
            if (!CanEncode(columns[i].Column, values[i])) return false;
        return EncodeWhole(columns, values).Length <= MaxKeyLength;
    }

    /// <summary>
    /// A key as an entry holds it: whole up to <see cref="MaxKeyLength"/> bytes, and a longer one cut to that
    /// length, its last two bytes a digest of what was cut off (all of it but the last byte), as Access writes a
    /// long text key. The digest is CRC-16 with the polynomial 0x8005, its register kept in the order the entry
    /// holds it.
    /// </summary>
    public static byte[] Truncate(byte[] key)
    {
        if (key.Length <= MaxKeyLength) return key;

        const int kept = MaxKeyLength - 2;
        int digest = 0;
        for (int i = kept; i < key.Length - 1; i++)
            digest = (digest >> 8) ^ DigestTable[digest & 0xFF] ^ key[i];

        byte[] truncated = key.Slice(0, MaxKeyLength);
        truncated[kept] = (byte)(digest >> 8);
        truncated[kept + 1] = (byte)digest;
        return truncated;
    }

    /// <summary>The most bytes an index entry's key has.</summary>
    internal const int MaxKeyLength = 510;

    private static readonly ushort[] DigestTable = BuildDigestTable();

    private static ushort[] BuildDigestTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < table.Length; i++)
        {
            int register = i << 8;
            for (int bit = 0; bit < 8; bit++)
                register = (register << 1 & 0xFFFF) ^ ((register & 0x8000) != 0 ? 0x8005 : 0);
            table[i] = (ushort)((register & 0xFF) << 8 | register >> 8);
        }
        return table;
    }

    /// <summary>
    /// One column's part of a key: its flag, then its value's bytes. Text is keyed in the column's sort order, and
    /// refused in an order whose keys this library does not write. A Memo is keyed as text, by the characters a
    /// key holds.
    /// </summary>
    public static byte[] EncodeColumn(Column column, bool ascending, object? value)
    {
        if (column.DataType is not (DataType.Text or DataType.Memo))
            return EncodeColumn(column.DataType, ascending, value);
        if (value is null)
            return new[] { ascending ? AscNullFlag : DescNullFlag };

        var codes = TextIndexCodes.For(column.SortOrder)
            ?? throw new NotSupportedException($"No key is made for this column: {WhyNot(column)}.");
        return EncodeText(codes, ascending, Convert.ToString(value) ?? string.Empty);
    }

    /// <summary>
    /// A text column's part of a key in the order of <paramref name="codes"/>: its flag, then the text's codes,
    /// which hold their own end markers, flipped with the rest for a descending column.
    /// </summary>
    public static byte[] EncodeText(TextIndexCodes codes, bool ascending, string value)
        => Prepend(ascending ? AscStartFlag : DescStartFlag, codes.Encode(value, ascending));

    /// <summary>One column's part of a key, for a type whose key does not depend on a sort order: any but Text and Memo.</summary>
    public static byte[] EncodeColumn(DataType type, bool ascending, object? value)
    {
        if (RowValues.IsNull(value) && type != DataType.Boolean)
            return new[] { ascending ? AscNullFlag : DescNullFlag };

        byte[] bytes;
        switch (type)
        {
            case DataType.Byte:
                bytes = new[] { Convert.ToByte(value) };
                break;
            case DataType.Int:
                bytes = SignFlipped(BigEndian(Convert.ToInt16(value)));
                break;
            case DataType.Long:
                bytes = SignFlipped(BigEndian(Convert.ToInt32(value)));
                break;
            case DataType.Money:
                // The number the row stores: ten-thousandths, in eight bytes.
                bytes = SignFlipped(BigEndian(MoneyUnits(Convert.ToDecimal(value))));
                break;
            case DataType.BigInt:
                bytes = SignFlipped(BigEndian(Convert.ToInt64(value)));
                break;
            case DataType.ExtendedDateTime:
                // The 42 bytes the row stores, in 8-byte segments each followed by a length byte: 9 for a
                // segment with more after it, and after the last, which is filled out with zeros, the bytes it
                // has of the value - 2. Text, so the keys sort as the dates do.
                byte[] stored = ExtendedDateTimeCodec.Encode((DateTime)value!);
                bytes = new byte[(stored.Length / 8 + 1) * 9];
                for (int segment = 0; segment * 8 < stored.Length; segment++)
                {
                    int length = Math.Min(8, stored.Length - segment * 8);
                    Array.Copy(stored, segment * 8, bytes, segment * 9, length);
                    bytes[segment * 9 + 8] = length == 8 ? (byte)9 : (byte)length;
                }
                if (!ascending)
                {
                    // The length byte of a segment with more after it stays as it is.
                    for (int i = 0; i < bytes.Length; i++)
                        if (i % 9 != 8 || i == bytes.Length - 1) bytes[i] = (byte)~bytes[i];
                }
                return Prepend(ascending ? AscStartFlag : DescStartFlag, bytes);
            case DataType.Boolean:
                // No is the greater: Yes is 0x00 and No 0xFF, and the other way round descending. A Yes/No
                // column holds no null, and its value is read as the row encoder reads it (RowValues).
                return new[] { ascending ? AscStartFlag : DescStartFlag, RowValues.ToBoolean(value) == ascending ? (byte)0x00 : (byte)0xFF };
            case DataType.Float:
                bytes = BitConverter.GetBytes(Convert.ToSingle(value));
                if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                return Prepend(ascending ? AscStartFlag : DescStartFlag, FloatingPoint(bytes, ascending));
            case DataType.Double:
                bytes = BigEndian(BitConverter.DoubleToInt64Bits(Convert.ToDouble(value)));
                return Prepend(ascending ? AscStartFlag : DescStartFlag, FloatingPoint(bytes, ascending));
            case DataType.ShortDateTime:
                // The double the row stores: days since 30 December 1899. A date read from a row comes as that
                // double itself, since a DateTime does not hold every double Access can have written.
                double days = value is DateTime date ? date.ToOADate() : Convert.ToDouble(value);
                bytes = BigEndian(BitConverter.DoubleToInt64Bits(days));
                return Prepend(ascending ? AscStartFlag : DescStartFlag, FloatingPoint(bytes, ascending));
            case DataType.Guid:
                // Big-endian, in 8-byte segments each followed by a length byte:
                // 9 for a segment with more after it, 8 for the last.
                byte[] g = BigEndian(value is Guid guid ? guid : Guid.Parse(Convert.ToString(value)!));
                bytes = new byte[18];
                Array.Copy(g, 0, bytes, 0, 8);
                bytes[8] = 9;
                Array.Copy(g, 8, bytes, 9, 8);
                bytes[17] = 8;
                if (!ascending)
                {
                    // The length byte of a segment with more after it stays as it is.
                    for (int i = 0; i < bytes.Length; i++)
                        if (i != 8) bytes[i] = (byte)~bytes[i];
                }
                return Prepend(ascending ? AscStartFlag : DescStartFlag, bytes);
            default:
                throw new NotSupportedException($"Index keys on {type} columns are not supported.");
        }
        if (!ascending)
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)~bytes[i];
        return Prepend(ascending ? AscStartFlag : DescStartFlag, bytes);
    }

    /// <summary>A Currency value as its row stores it: ten-thousandths, rounded as the row encoder rounds.</summary>
    internal static long MoneyUnits(decimal value) => (long)Math.Round(value * 10000m, 0, MidpointRounding.AwayFromZero);

    // IEEE bytes, big-endian, made to sort as the numbers do: a positive number's sign bit is set, a negative
    // number's bytes are all inverted - and the other way round descending.
    private static byte[] FloatingPoint(byte[] bigEndian, bool ascending)
    {
        bool negative = (bigEndian[0] & 0x80) != 0;
        if (!negative) bigEndian[0] ^= 0x80;
        if (negative == ascending)
            for (int i = 0; i < bigEndian.Length; i++) bigEndian[i] = (byte)~bigEndian[i];
        return bigEndian;
    }

    private static byte[] Prepend(byte flag, byte[] bytes)
    {
        var result = new byte[bytes.Length + 1];
        result[0] = flag;
        Array.Copy(bytes, 0, result, 1, bytes.Length);
        return result;
    }

    private static byte[] SignFlipped(byte[] bigEndian)
    {
        bigEndian[0] ^= 0x80;
        return bigEndian;
    }

    private static byte[] BigEndian(short v) => new[] { (byte)(v >> 8), (byte)v };

    private static byte[] BigEndian(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] BigEndian(long v) => new[]
    {
        (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32), (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v,
    };

    // A GUID's bytes in the order its text shows them (.NET's own array keeps the
    // first three groups little-endian).
    private static byte[] BigEndian(Guid g)
    {
        byte[] b = g.ToByteArray();
        return new[] { b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15] };
    }
}
