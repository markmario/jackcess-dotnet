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
    /// The value at <paramref name="offset"/>, with <paramref name="scale"/> digits after the point; null for one
    /// a <see cref="decimal"/> cannot hold (more than 96 bits, or a scale past 28).
    /// </summary>
    public static decimal? Decode(byte[] row, int offset, int scale)
    {
        if (ByteUtil.GetInt(row, offset + 1) != 0 || scale > 28) return null;
        int high = ByteUtil.GetInt(row, offset + 5);
        int mid  = ByteUtil.GetInt(row, offset + 9);
        int low  = ByteUtil.GetInt(row, offset + 13);
        return new decimal(low, mid, high, row[offset] != 0, (byte)scale);
    }

    /// <summary>
    /// <paramref name="value"/> as a column of <paramref name="precision"/> digits, <paramref name="scale"/> of
    /// them after the point, holds it: rounded to the scale, half away from zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The number has more digits than the column holds.</exception>
    public static byte[] Encode(decimal value, int precision, int scale, string columnName)
    {
        var unscaled = Unscaled(value, scale);
        if (precision > 0 && unscaled >= BigInteger.Pow(10, precision) || unscaled >= BigInteger.One << 128)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{value} has more digits than the Decimal column '{columnName}' holds ({precision}, {scale} of them after the point).");

        var bytes = new byte[Size];
        // A number that rounds to nothing at the column's scale is zero, and zero has no sign.
        bytes[0] = value < 0m && !unscaled.IsZero ? NegativeSign : (byte)0;
        for (int word = 3; word >= 0; word--)
        {
            ByteUtil.PutInt(bytes, 1 + word * 4, unchecked((int)(uint)(unscaled & uint.MaxValue)));
            unscaled >>= 32;
        }
        return bytes;
    }

    /// <summary>|<paramref name="value"/>| rounded to <paramref name="scale"/> places, without its decimal point.</summary>
    private static BigInteger Unscaled(decimal value, int scale)
    {
        decimal rounded = scale <= 28 ? Math.Round(Math.Abs(value), scale, MidpointRounding.AwayFromZero) : Math.Abs(value);
        int[] bits = decimal.GetBits(rounded);
        int ownScale = (bits[3] >> 16) & 0xFF;
        var integer = new BigInteger((uint)bits[2]) << 64 | new BigInteger((uint)bits[1]) << 32 | new BigInteger((uint)bits[0]);
        return integer * BigInteger.Pow(10, scale - ownScale);
    }
}
