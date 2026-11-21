using System.Numerics;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// A Decimal (Numeric) value as a row holds it: 17 bytes, a sign byte (0x80 for a negative number) and then the
/// number without its decimal point as a 128-bit integer - four 32-bit words, the most significant first, each
/// word's own bytes least significant first. That is how Access writes it, and Java Jackcess (its
/// fixNumericByteOrder). Read as sixteen bytes most significant first, which this library did, 1 was 16,777,216:
/// Access read the library's numbers wrong, and the library Access's.
/// </summary>
internal static class NumericCodec
{
    internal const int Size = 17;
    private const byte NegativeSign = 0x80;

    /// <summary>
    /// The value at <paramref name="offset"/>, with <paramref name="scale"/> digits after the point; null for a
    /// scale past 28, which a <see cref="decimal"/> cannot hold.
    /// <para>
    /// The number is the last three words, 96 bits, and the first is not read: that is how Access reads one.
    /// Access writes no number with the first word set - its 28 digits are under
    /// 2^96 - so one is there only in bytes something else wrote, and ACE's OLE DB provider and DAO both give
    /// the number its low 96 bits make, with its sign, and no error: 2^96 + 12345 in a DECIMAL(5,2) column is
    /// 123.45. Java Jackcess gives all 128 bits as a BigDecimal; a <see cref="decimal"/> has no room for them.
    /// </para>
    /// </summary>
    public static decimal? Decode(byte[] row, int offset, int scale)
    {
        if (scale > 28) return null;
        int high = ByteUtil.GetInt(row, offset + 5);
        int mid  = ByteUtil.GetInt(row, offset + 9);
        int low  = ByteUtil.GetInt(row, offset + 13);
        return new decimal(low, mid, high, row[offset] != 0, (byte)scale);
    }

    /// <summary>Whether the value at <paramref name="offset"/> is negative, as <see cref="Decode"/> reads its sign.</summary>
    public static bool IsNegative(byte[] row, int offset) => row[offset] != 0;

    /// <summary>
    /// The number at <paramref name="offset"/> without its sign or its decimal point, as the row stores it: all
    /// 128 bits, of which <see cref="Decode"/> reads the low 96.
    /// </summary>
    public static BigInteger Unscaled(byte[] row, int offset)
    {
        var unscaled = BigInteger.Zero;
        for (int word = 0; word < 4; word++)
            unscaled = unscaled << 32 | (uint)ByteUtil.GetInt(row, offset + 1 + word * 4);
        return unscaled;
    }

    /// <summary>
    /// The same bytes as earlier versions of this library wrote and read them: sixteen bytes, the most
    /// significant first.
    /// </summary>
    public static BigInteger UnscaledInEarlierByteOrder(byte[] row, int offset)
    {
        var unscaled = BigInteger.Zero;
        for (int i = 1; i < Size; i++)
            unscaled = unscaled << 8 | row[offset + i];
        return unscaled;
    }

    /// <summary>
    /// <paramref name="unscaled"/> with its sign and <paramref name="scale"/> digits after the point; null for a
    /// number a <see cref="decimal"/> cannot hold: one of more than 96 bits, or a scale past 28.
    /// </summary>
    public static decimal? ToDecimal(BigInteger unscaled, bool negative, int scale)
    {
        if (unscaled >> 96 != 0 || scale > 28) return null;
        return new decimal(unchecked((int)(uint)(unscaled & uint.MaxValue)),
                           unchecked((int)(uint)(unscaled >> 32 & uint.MaxValue)),
                           unchecked((int)(uint)(unscaled >> 64)), negative, (byte)scale);
    }

    /// <summary>
    /// <paramref name="value"/> as a column of <paramref name="precision"/> digits, <paramref name="scale"/> of
    /// them after the point, holds it: the places past the scale cut off, toward zero, and the sign kept
    /// (<see cref="AsStored"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The number has more digits than the column holds.</exception>
    public static byte[] Encode(decimal value, int precision, int scale, string columnName)
    {
        var unscaled = Unscaled(value, scale);
        if (precision > 0 && unscaled >= BigInteger.Pow(10, precision) || unscaled >= BigInteger.One << 128)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{value} has more digits than the Decimal column '{columnName}' holds ({precision}, {scale} of them after the point).");

        var bytes = new byte[Size];
        // The number's sign, as ACE writes it: a negative number cut to nothing is a negative zero (#80). A zero a
        // caller gives has none once a row takes it (AsStored), and a negative zero read from a row is written back
        // as the row stores it.
        bytes[0] = decimal.GetBits(value)[3] < 0 ? NegativeSign : (byte)0;
        for (int word = 3; word >= 0; word--)
        {
            ByteUtil.PutInt(bytes, 1 + word * 4, unchecked((int)(uint)(unscaled & uint.MaxValue)));
            unscaled >>= 32;
        }
        return bytes;
    }

    /// <summary>
    /// <paramref name="value"/> as a Decimal column of <paramref name="scale"/> places stores it, and reads it back:
    /// the places past the scale cut off, toward zero, as ACE stores such a number (#80; measured with ACE 16 for
    /// typed Decimal, Double, Single and text parameters, scales 0 to 28, both signs, in an .mdb and an .accdb). A
    /// negative number cut to nothing is a negative zero, as ACE stores it; a zero given, of either sign, is ACE's
    /// zero, which has none. Any other number with no more places than the scale is returned as it is. The digits
    /// are worked on as an integer, so every runtime cuts alike.
    /// </summary>
    public static decimal AsStored(decimal value, int scale)
    {
        int[] bits = decimal.GetBits(value);
        int ownScale = (bits[3] >> 16) & 0xFF;
        if (ownScale <= scale || scale > 28) return value == 0m ? new decimal(0, 0, 0, false, (byte)ownScale) : value;
        var cut = Magnitude(bits) / BigInteger.Pow(10, ownScale - scale);
        return ToDecimal(cut, value < 0m, scale)!.Value;
    }

    /// <summary>|<paramref name="value"/>| cut to <paramref name="scale"/> places, toward zero, without its decimal point.</summary>
    private static BigInteger Unscaled(decimal value, int scale)
    {
        int[] bits = decimal.GetBits(value);
        int ownScale = (bits[3] >> 16) & 0xFF;
        var integer = Magnitude(bits);
        if (scale > 28) return integer * BigInteger.Pow(10, scale - ownScale);
        return ownScale <= scale ? integer * BigInteger.Pow(10, scale - ownScale) : integer / BigInteger.Pow(10, ownScale - scale);
    }

    // A decimal's digits without its point and its sign, from its bits.
    private static BigInteger Magnitude(int[] bits)
        => new BigInteger((uint)bits[2]) << 64 | new BigInteger((uint)bits[1]) << 32 | new BigInteger((uint)bits[0]);
}
