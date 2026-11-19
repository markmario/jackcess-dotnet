// What the tests call that .NET Framework 4.8 does not have, so that one test source compiles and runs on every
// framework the library targets. Each member here has the name, arguments and behaviour of the .NET member it
// stands in for.
#if NETFRAMEWORK
using System.Text;

namespace JackcessDotNet.Tests;

internal static class FrameworkPolyfills
{
    extension(Convert)
    {
        /// <summary>Convert.ToHexString (.NET 5): upper-case hexadecimal, two digits a byte.</summary>
        public static string ToHexString(byte[] bytes) => ToHexString(bytes, 0, bytes.Length);

        /// <summary>Convert.ToHexString (.NET 5) of part of an array.</summary>
        public static string ToHexString(byte[] bytes, int offset, int length)
        {
            var text = new StringBuilder(length * 2);
            for (var i = offset; i < offset + length; i++) text.Append(bytes[i].ToString("X2"));
            return text.ToString();
        }

        /// <summary>Convert.ToHexString (.NET 5) of a span.</summary>
        public static string ToHexString(ReadOnlySpan<byte> bytes) => ToHexString(bytes.ToArray());

        /// <summary>Convert.FromHexString (.NET 5): the bytes that hexadecimal digits of either case spell.</summary>
        public static byte[] FromHexString(string hex)
        {
            if (hex.Length % 2 != 0) throw new FormatException("The hexadecimal text has an odd number of digits.");
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }

    /// <summary>Enumerable.Zip (.NET Core 3.0) without a selector: the pairs, as tuples.</summary>
    public static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(
        this IEnumerable<TFirst> first, IEnumerable<TSecond> second)
        => first.Zip(second, (a, b) => (a, b));
}
#endif
