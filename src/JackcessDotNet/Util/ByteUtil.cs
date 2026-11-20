using System.Text;

namespace JackcessDotNet.Util;

/// <summary>
/// Low-level byte manipulation helpers for reading/writing Jet format data.
/// All Jet format values are little-endian.
/// </summary>
internal static class ByteUtil
{
    // ── Read helpers ─────────────────────────────────────────────────────────

    public static byte GetByte(byte[] buf, int offset) => buf[offset];

    public static short GetShort(byte[] buf, int offset)
        => (short)(buf[offset] | (buf[offset + 1] << 8));

    public static ushort GetUShort(byte[] buf, int offset)
        => (ushort)(buf[offset] | (buf[offset + 1] << 8));

    public static int GetInt(byte[] buf, int offset)
        => buf[offset]
         | (buf[offset + 1] << 8)
         | (buf[offset + 2] << 16)
         | (buf[offset + 3] << 24);

    public static uint GetUInt(byte[] buf, int offset)
        => (uint)(buf[offset]
                | (buf[offset + 1] << 8)
                | (buf[offset + 2] << 16)
                | (buf[offset + 3] << 24));

    public static long GetLong(byte[] buf, int offset)
        => (long)(uint)GetInt(buf, offset)
         | ((long)(uint)GetInt(buf, offset + 4) << 32);

    public static double GetDouble(byte[] buf, int offset)
        => BitConverter.Int64BitsToDouble(GetLong(buf, offset));

    public static float GetFloat(byte[] buf, int offset)
#if NETFRAMEWORK || NETSTANDARD2_0
        => System.Buffers.Binary.BinaryPrimitivesCompat.Int32BitsToSingle(GetInt(buf, offset));
#else
        => BitConverter.Int32BitsToSingle(GetInt(buf, offset));
#endif

    // ── Write helpers ─────────────────────────────────────────────────────────

    public static void PutByte(byte[] buf, int offset, byte value)
        => buf[offset] = value;

    public static void PutShort(byte[] buf, int offset, short value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void PutUShort(byte[] buf, int offset, ushort value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void PutInt(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public static void PutUInt(byte[] buf, int offset, uint value)
        => PutInt(buf, offset, (int)value);

    public static void PutLong(byte[] buf, int offset, long value)
    {
        PutInt(buf, offset,     (int)(value & 0xFFFFFFFFL));
        PutInt(buf, offset + 4, (int)((value >> 32) & 0xFFFFFFFFL));
    }

    public static int Get3ByteInt(byte[] buf, int offset)
        => buf[offset]
         | (buf[offset + 1] << 8)
         | (buf[offset + 2] << 16);

    public static void Put3ByteInt(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    public static void PutDouble(byte[] buf, int offset, double value)
        => PutLong(buf, offset, BitConverter.DoubleToInt64Bits(value));

    public static void PutFloat(byte[] buf, int offset, float value)
#if NETFRAMEWORK || NETSTANDARD2_0
        => PutInt(buf, offset, System.Buffers.Binary.BinaryPrimitivesCompat.SingleToInt32Bits(value));
#else
        => PutInt(buf, offset, BitConverter.SingleToInt32Bits(value));
#endif

    public static void PutBytes(byte[] buf, int offset, byte[] src)
        => Array.Copy(src, 0, buf, offset, src.Length);

    public static void PutBytes(byte[] buf, int offset, byte[] src, int srcOffset, int length)
        => Array.Copy(src, srcOffset, buf, offset, length);

    // ── String helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// A Text value's bytes in a column with Unicode compression, as ACE writes them. Text of characters 0x01 to 0xFF
    /// is compressed: 0xFF 0xFE and a byte a character. Text that begins with U+FEFF, whose UTF-16LE begins 0xFF 0xFE
    /// like compressed text, is written as ACE writes it (#93): compressed in runs where that is no longer than
    /// UTF-16LE (<see cref="CompressInRuns"/>), and otherwise UTF-16LE, which ACE then reads as compressed text, as it
    /// reads its own. Any other text is UTF-16LE; ACE compresses text that mixes characters in runs too, and reads the
    /// UTF-16LE the same.
    /// </summary>
    public static byte[] EncodeText(string value)
    {
        // NUL is left out: inside compressed text 0x00 switches between one byte a character and two.
        bool canCompress = value.All(c => c >= 0x01 && c <= 0xFF);
        if (canCompress)
        {
            var bytes = new byte[value.Length + 2];
            bytes[0] = 0xFF;
            bytes[1] = 0xFE;
            for (int i = 0; i < value.Length; i++)
                bytes[i + 2] = (byte)value[i];
            return bytes;
        }
        if (BeginsWithMarker(value) && CompressInRuns(value, evenIfNoShorter: true) is { } runs) return runs;
        return EncodeUtf16(value);
    }

    /// <summary>Whether the text's UTF-16LE begins 0xFF 0xFE, the mark of compressed text: it begins with U+FEFF.</summary>
    internal static bool BeginsWithMarker(string text) => text.Length > 0 && text[0] == '\uFEFF';

    /// <summary>
    /// Text compressed in runs, as ACE compresses it (measured with ACE 16 and DAO, #93): 0xFF 0xFE, then each run of
    /// characters 0x01 to 0xFF a byte a character and each run of other characters two bytes a character, with a 0x00
    /// between runs; the first run is of one byte a character, and empty when the text begins with another. Null when
    /// that is longer than UTF-16LE (or as long, unless <paramref name="evenIfNoShorter"/>: ACE compresses a Text value
    /// as long either way, and a Memo value only when it is shorter), or when the text holds a character no run can:
    /// NUL, or one above 0xFF with a low byte of 0x00, either of which reads as the 0x00 between runs.
    /// </summary>
    internal static byte[]? CompressInRuns(string text, bool evenIfNoShorter)
    {
        int length = 2;
        bool oneByte = true;
        foreach (char c in text)
        {
            if (c == 0 || c > 0xFF && (c & 0xFF) == 0) return null;
            bool latin = c <= 0xFF;
            if (latin != oneByte) { length++; oneByte = latin; }
            length += latin ? 1 : 2;
        }
        int plain = 2 * text.Length;
        if (length > plain || length == plain && !evenIfNoShorter) return null;

        var bytes = new byte[length];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        int at = 2;
        oneByte = true;
        foreach (char c in text)
        {
            bool latin = c <= 0xFF;
            if (latin != oneByte) { bytes[at++] = 0x00; oneByte = latin; }
            bytes[at++] = (byte)c;
            if (!latin) bytes[at++] = (byte)(c >> 8);
        }
        return bytes;
    }

    /// <summary>
    /// A Jet4 text value from bytes that may be compressed: bytes that begin 0xFF 0xFE are compressed text
    /// (<see cref="DecodeCompressed"/>), and any others UTF-16LE.
    /// </summary>
    public static string DecodeText(byte[] data, int offset, int length)
    {
        if (length >= 2 && data[offset] == 0xFF && data[offset + 1] == 0xFE)
            return DecodeCompressed(data, offset, length);
        return DecodeUtf16(data, offset, length);
    }

    /// <summary>
    /// Compressed text as ACE reads it (measured with ACE 16 and DAO, #93): after 0xFF 0xFE, a byte a character with a
    /// high byte of 0x00, until a 0x00 byte, which switches to two bytes a character (UTF-16LE); two bytes at a time
    /// until a pair begins with 0x00, which switches back. A byte left alone at the end is U+FFFD, as
    /// <see cref="DecodeUtf16"/> reads one.
    /// </summary>
    private static string DecodeCompressed(byte[] data, int offset, int length)
    {
        var text = new StringBuilder(length);
        int end = offset + length;
        bool oneByte = true;
        for (int at = offset + 2; at < end;)
        {
            if (data[at] == 0x00) { oneByte = !oneByte; at++; }
            else if (oneByte) text.Append((char)data[at++]);
            else if (at + 1 < end) { text.Append((char)(data[at] | data[at + 1] << 8)); at += 2; }
            else { text.Append('\uFFFD'); at++; }
        }
        return text.ToString();
    }

    /// <summary>
    /// UTF-16LE text as the code units it holds, half of a surrogate pair alone among them: Access keeps a name or a
    /// value with one as it is given, and <see cref="Encoding.Unicode"/> reads each as U+FFFD (#63). An odd byte at
    /// the end is U+FFFD, as <see cref="Encoding.Unicode"/> reads one.
    /// </summary>
    public static string DecodeUtf16(byte[] data, int offset, int length)
    {
        int units = length / 2;
        var chars = new char[units + length % 2];
        for (int i = 0; i < units; i++)
            chars[i] = (char)(data[offset + 2 * i] | data[offset + 2 * i + 1] << 8);
        if (length % 2 != 0) chars[units] = '�';
        return new string(chars);
    }

    /// <summary>Text as UTF-16LE, each code unit as it is, half of a surrogate pair alone among them: what <see cref="DecodeUtf16"/> reads.</summary>
    public static byte[] EncodeUtf16(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            bytes[2 * i]     = (byte)text[i];
            bytes[2 * i + 1] = (byte)(text[i] >> 8);
        }
        return bytes;
    }

    /// <summary>
    /// A text value in a file of <paramref name="format"/>. Access 97 keeps text in the database's code page. A later
    /// format keeps it as UTF-16LE, or compressed where ACE reads it as compressed (<paramref name="mayBeCompressed"/>):
    /// a Text value in a column with Unicode compression, a Memo value kept in its row, and a Memo value in one
    /// long-value row of a column with Unicode compression. A Text value in a column without it, a Memo value on a
    /// long-value page of such a column, and a Memo value in a chain of long-value rows are UTF-16LE however they
    /// begin, as ACE 16 reads them (#93).
    /// </summary>
    public static string DecodeText(byte[] data, int offset, int length, JetFormat format, bool mayBeCompressed)
    {
        if (length == 0) return string.Empty;
        if (format.Version == FileFormat.Access97)
            return format.TextEncoding.GetString(data, offset, length);
        return mayBeCompressed ? DecodeText(data, offset, length) : DecodeUtf16(data, offset, length);
    }

    // ── Misc ──────────────────────────────────────────────────────────────────

    public static void Fill(byte[] buf, int offset, int count, byte value)
    {
        for (int i = 0; i < count; i++)
            buf[offset + i] = value;
    }

    public static void Clear(byte[] buf, int offset, int count)
        => Fill(buf, offset, count, 0x00);
}
