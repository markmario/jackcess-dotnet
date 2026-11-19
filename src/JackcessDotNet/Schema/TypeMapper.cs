using System.Globalization;
using System.Numerics;

namespace JackcessDotNet;

/// <summary>
/// The columns an import makes for a source's CLR types, and the values it writes to a column: each one the column
/// holds exactly, or none (#44). Used by <see cref="DatabaseImporter"/>.
/// <para>
/// A column a table is made with holds every value the source has: its type follows the CLR type, and a length,
/// precision or scale that the values need follows the values. A value given to a column that is there already is
/// converted to the column's type only when the column holds it exactly; any other is refused with the reason.
/// </para>
/// </summary>
internal static class TypeMapper
{
    /// <summary>The most characters a Text column holds; a longer string is a Memo's.</summary>
    public const int MaxTextLength = 255;

    /// <summary>The most bytes a Binary column holds; a longer value is an OLE Object's.</summary>
    public const int MaxBinaryLength = 255;

    /// <summary>The most digits a Decimal column holds.</summary>
    public const int MaxDecimalPrecision = 28;

    /// <summary>The precision Access gives a Decimal column it is not told one for, and that the importer gives one at least.</summary>
    public const int DefaultDecimalPrecision = 18;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The type a value of <paramref name="clrType"/> is imported as: a nullable's underlying type, an enum's integer type.</summary>
    public static Type ImportedType(Type clrType)
    {
        Type t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return t.IsEnum ? Enum.GetUnderlyingType(t) : t;
    }

    /// <summary>Whether a column can be made for values of <paramref name="clrType"/>.</summary>
    public static bool IsMappable(Type clrType) => MapClrType(clrType, JetFormat.Jet4) is not null;

    /// <summary>
    /// The type of the column a value of <paramref name="clrType"/> is imported into, before the values are looked
    /// at - a Text column may become a Memo, and a Binary one an OLE Object, for their length; or null for a type a
    /// database has no column for.
    /// <list type="bullet">
    ///   <item><see cref="sbyte"/> is an Integer (<see cref="DataType.Int16"/>), Access's Byte being 0 to 255;
    ///   <see cref="ushort"/> a Long Integer (<see cref="DataType.Int32"/>), Access's Integer being signed.</item>
    ///   <item><see cref="uint"/> is a Large Number in an .accdb and a Decimal of 10 digits in an .mdb, which has no
    ///   Large Number; <see cref="long"/> is a Large Number, which an .mdb refuses
    ///   (<see cref="Database.FormatVersionFor"/>); <see cref="ulong"/> is a Decimal of 20 digits, past what a Large
    ///   Number holds.</item>
    ///   <item><see cref="decimal"/> is a Decimal, whose precision and scale the values give (Currency keeps four
    ///   places after the point, and would round the rest).</item>
    ///   <item><c>byte[]</c> is an OLE Object unless a length that every value fits is known
    ///   (<see cref="ColumnFor"/>).</item>
    /// </list>
    /// </summary>
    public static DataType? MapClrType(Type clrType, JetFormat format)
    {
        if (clrType is null) return null;
        Type t = ImportedType(clrType);

        if (t == typeof(bool))           return DataType.Boolean;
        if (t == typeof(byte))           return DataType.Byte;
        if (t == typeof(sbyte))          return DataType.Int16;
        if (t == typeof(short))          return DataType.Int16;
        if (t == typeof(ushort))         return DataType.Int32;
        if (t == typeof(int))            return DataType.Int32;
        if (t == typeof(uint))           return HasLargeNumber(format) ? DataType.Int64 : DataType.Decimal;
        if (t == typeof(long))           return DataType.Int64;
        if (t == typeof(ulong))          return DataType.Decimal;
        if (t == typeof(float))          return DataType.Single;
        if (t == typeof(double))         return DataType.Double;
        if (t == typeof(decimal))        return DataType.Decimal;
        if (t == typeof(DateTime))       return DataType.DateTime;
        if (t == typeof(DateTimeOffset)) return DataType.DateTime;
        if (t == typeof(Guid))           return DataType.Guid;
        if (t == typeof(string))         return DataType.Text;
        if (t == typeof(char))           return DataType.Text;
        if (t == typeof(byte[]))         return DataType.OleObject;
        return null;
    }

    // An .accdb has the Large Number; an .mdb (Access 97, Access 2000-2003) has not.
    private static bool HasLargeNumber(JetFormat format) => format.Version is not (FileFormat.Access97 or FileFormat.Access2000);

    /// <summary>
    /// The column a table is made with for a source column or property of <paramref name="clrType"/> whose values
    /// are <paramref name="values"/>: one that holds every one of them exactly. Null for a type a database has no
    /// column for.
    /// <list type="bullet">
    ///   <item>Text of the length the source gives (<paramref name="maxLength"/>) when every value fits it, and of 255
    ///   characters when it gives none and every value fits that; otherwise a Memo.</item>
    ///   <item>Binary of the length the source gives when it is 255 bytes or fewer and every value fits it; otherwise
    ///   an OLE Object.</item>
    ///   <item>A Decimal of a <see cref="decimal"/>: its scale the most places after the point a value has, as the
    ///   value holds it, and its precision the digits the values need, 18 (Access's default) at least. When the
    ///   values need more than 28 digits, the places they have only as trailing zeros are given up; when they still
    ///   need more, the column is refused.</item>
    /// </list>
    /// </summary>
    /// <param name="name">The column's name.</param>
    /// <param name="clrType">The CLR type of the source column or property.</param>
    /// <param name="maxLength">The length the source gives a string or a byte array, or null.</param>
    /// <param name="autoNumber">Whether the column is an AutoNumber: an <see cref="int"/> the source numbers.</param>
    /// <param name="required">Whether the source takes no null in the column.</param>
    /// <param name="values">The values the column is to hold.</param>
    /// <param name="format">The format of the file the table is made in.</param>
    /// <exception cref="NotHeldException">
    /// The column is a Decimal and its values need more digits than a Decimal column holds.
    /// </exception>
    public static Column? ColumnFor(string name, Type clrType, int? maxLength, bool autoNumber, bool required,
                                    IReadOnlyCollection<object?> values, JetFormat format)
    {
        var mapped = MapClrType(clrType, format);
        if (mapped is not { } dataType) return null;
        Type t = ImportedType(clrType);
        int? declared = maxLength is > 0 ? maxLength : null;

        ColumnDefinition definition;
        switch (dataType)
        {
            case DataType.Text:
            {
                int longest = values.Select(v => v switch { string s => s.Length, char => 1, _ => 0 }).DefaultIfEmpty(0).Max();
                int length = declared ?? MaxTextLength;
                definition = length <= MaxTextLength && longest <= length
                    ? new ColumnDefinition(name, DataType.Text) { MaxLength = length }
                    : new ColumnDefinition(name, DataType.Memo);
                break;
            }
            case DataType.OleObject:
            {
                int longest = values.OfType<byte[]>().Select(b => b.Length).DefaultIfEmpty(0).Max();
                definition = declared is { } length && length <= MaxBinaryLength && longest <= length
                    ? new ColumnDefinition(name, DataType.Binary) { MaxLength = length }
                    : new ColumnDefinition(name, DataType.OleObject);
                break;
            }
            case DataType.Decimal:
            {
                var (precision, scale) = t == typeof(uint) ? (10, 0)
                                       : t == typeof(ulong) ? (20, 0)
                                       : DecimalShapeOf(values.OfType<decimal>());
                definition = new ColumnDefinition(name, DataType.Decimal) { Precision = (byte)precision, Scale = (byte)scale };
                break;
            }
            default:
                definition = new ColumnDefinition(name, dataType);
                break;
        }

        if (autoNumber && definition.DataType == DataType.Int32)
            definition.IsAutoNumber = true;
        // An AutoNumber has its number in every row, and a Yes/No its value: Access gives neither a Required property.
        definition.IsRequired = required && !definition.IsAutoNumber && definition.DataType != DataType.Boolean;
        return definition.ToColumn(nameof(name));
    }

    /// <summary>
    /// The precision and scale of a Decimal column that holds every one of <paramref name="values"/> exactly.
    /// </summary>
    /// <exception cref="NotHeldException">No Decimal column holds them all: they need more than 28 digits.</exception>
    private static (int Precision, int Scale) DecimalShapeOf(IEnumerable<decimal> values)
    {
        int scale = 0, significantScale = 0, integerDigits = 0;
        decimal widest = 0m;
        foreach (decimal value in values)
        {
            scale = Math.Max(scale, ScaleOf(value));
            significantScale = Math.Max(significantScale, SignificantScaleOf(value));
            int digits = IntegerDigitsOf(value);
            if (digits > integerDigits) { integerDigits = digits; widest = value; }
        }

        // The places a value has only as trailing zeros (1.50 as 1.5) are given up only when keeping them would take
        // more digits than a column holds: the column keeps the source's scale where it can.
        if (integerDigits + scale > MaxDecimalPrecision) scale = significantScale;
        if (integerDigits + scale > MaxDecimalPrecision)
            throw new NotHeldException(
                $"its values need {integerDigits + scale} digits - {widest.ToString(Invariant)} has {integerDigits} before the " +
                $"point, and a value has {scale} after it - and an Access Decimal column holds {MaxDecimalPrecision}: no column " +
                "holds them all exactly");
        return (Math.Max(DefaultDecimalPrecision, integerDigits + scale), scale);
    }

    /// <summary>The places after the point <paramref name="value"/> holds, trailing zeros included.</summary>
    private static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    /// <summary>The places after the point <paramref name="value"/> needs: its scale without trailing zeros.</summary>
    private static int SignificantScaleOf(decimal value)
    {
        int scale = ScaleOf(value);
        while (scale > 0 && decimal.Round(value, scale - 1) == value) scale--;
        return scale;
    }

    /// <summary>The digits of <paramref name="value"/> before the point: 0 for one less than 1 in size.</summary>
    private static int IntegerDigitsOf(decimal value)
    {
        decimal whole = decimal.Truncate(Math.Abs(value));
        int digits = 0;
        while (whole >= 1m)
        {
            whole = decimal.Truncate(whole / 10m);
            digits++;
        }
        return digits;
    }

    // ── A value as its column holds it ──────────────────────────────────────

    /// <summary>
    /// A value a column does not hold exactly, and why: what <see cref="ToColumnValue"/> throws, which the importer
    /// turns into a refusal that names the row.
    /// </summary>
    internal sealed class NotHeldException : Exception
    {
        internal NotHeldException(string reason, int? textLength = null) : base(reason) => TextLength = textLength;

        /// <summary>For a text value longer than its column, the value's length; otherwise null.</summary>
        internal int? TextLength { get; }
    }

    /// <summary>
    /// <paramref name="value"/> as <paramref name="column"/> holds it - a <see cref="bool"/>, the integer type of the
    /// column's size, <see cref="float"/>, <see cref="double"/>, <see cref="decimal"/>, <see cref="DateTime"/>,
    /// <see cref="Guid"/>, <see cref="string"/> or <c>byte[]</c> - when the column holds it exactly; null for a null.
    /// <list type="bullet">
    ///   <item>A number goes to a numeric column when the column holds its value: an integer in range, a
    ///   <see cref="decimal"/> with no more places than a Currency's four or a Decimal's scale, nor more digits than
    ///   its precision. A floating-point value and a <see cref="decimal"/> go to each other's columns when the value
    ///   the column holds converts back to the one given.</item>
    ///   <item>Text goes to a numeric, Yes/No, Date/Time or GUID column when it reads, in the invariant culture, as a
    ///   value the column holds; a value goes to a Text or Memo column as its invariant text, which reads back as
    ///   it (a <see cref="double"/> in its round-trip form, a date in ISO 8601).</item>
    ///   <item>A Text or Binary column of one length (SQL's CHAR and BINARY) takes a value of that length only:
    ///   a shorter one would read back filled with spaces or zeros.</item>
    ///   <item>A Yes/No column takes a <see cref="bool"/> and no null, which it would read as No; no number, of
    ///   which only 0 and 1 would read back as they were.</item>
    ///   <item>A Date/Time column takes a date from 1 January 100, the first it holds, to the millisecond it is
    ///   read and written at; Date/Time Extended any <see cref="DateTime"/>. A <see cref="DateTimeOffset"/> is its
    ///   UTC date and time, and so is text with an offset; text without one is the date and time it says, whatever
    ///   the machine's time zone.</item>
    /// </list>
    /// </summary>
    /// <exception cref="NotHeldException">The column does not hold the value exactly.</exception>
    public static object? ToColumnValue(object? value, Column column)
    {
        if (value is null || value is DBNull)
        {
            if (column.DataType == DataType.Boolean)
                throw new NotHeldException("a Yes/No column holds no null, and Access would read it as No");
            return null;
        }
        if (value.GetType().IsEnum) value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), Invariant);

        return column.DataType switch
        {
            DataType.Boolean          => BooleanOf(value),
            DataType.Byte             => (byte)IntegerOf(value, byte.MinValue, byte.MaxValue, "a Byte column holds 0 to 255"),
            DataType.Int16            => (short)IntegerOf(value, short.MinValue, short.MaxValue, "an Integer column holds -32,768 to 32,767"),
            DataType.Int32            => (int)IntegerOf(value, int.MinValue, int.MaxValue, "a Long Integer column holds -2,147,483,648 to 2,147,483,647"),
            DataType.Int64            => IntegerOf(value, long.MinValue, long.MaxValue, "a Large Number column holds 64-bit integers"),
            DataType.Single           => SingleOf(value),
            DataType.Double           => DoubleOf(value),
            DataType.Currency         => CurrencyOf(value),
            DataType.Decimal          => DecimalOf(value, column.Precision, column.Scale),
            DataType.DateTime         => DateTimeOf(value, extended: false),
            DataType.DateTimeExtended => DateTimeOf(value, extended: true),
            DataType.Guid             => GuidOf(value),
            DataType.Text             => TextOf(value, column),
            DataType.Memo             => TextOf(value, column: null),
            DataType.Binary           => BinaryOf(value, column),
            DataType.OleObject        => value as byte[] ?? throw NotOfType(value, "an OLE Object column holds bytes"),
            _                         => throw new NotHeldException($"a {column.DataType} column is not written by this library"),
        };
    }

    private static NotHeldException NotOfType(object value, string what)
        => new($"{Describe(value)} is a {value.GetType().Name}, and {what}");

    private static string Describe(object value) => value switch
    {
        string s   => $"'{(s.Length > 40 ? s.Substring(0, 40) + "..." : s)}'",
        byte[] b   => $"{b.Length} bytes",
        double d   => d.ToString("R", Invariant),
        float f    => f.ToString("R", Invariant),
        DateTime t => t.ToString("o", Invariant),
        IFormattable f => f.ToString(null, Invariant),
        _          => value.ToString() ?? "",
    };

    private static bool BooleanOf(object value) => value switch
    {
        bool b => b,
        string s when bool.TryParse(s, out bool parsed) => parsed,
        _ => throw NotOfType(value, "a Yes/No column takes a bool, or the text True or False: of a number only 0 and 1 would read back as they were"),
    };

    // A number as a decimal that converts back to it: exactly, for an integer or a decimal; for a floating-point value,
    // the decimal of its digits, when that converts back to the same value (0.1 for the double 0.1, none for 1/3).
    // Text that reads as a number in the invariant culture is that number. Null for anything else.
    private static decimal? AsDecimal(object value) => value switch
    {
        sbyte v  => v, byte v => v, short v => v, ushort v => v, int v => v, uint v => v, long v => v, ulong v => v,
        decimal v => v,
        double v => RoundTripDecimal(v),
        float v  => RoundTripDecimal(v),
        string s => decimal.TryParse(s, NumberStyles.Float, Invariant, out decimal parsed) ? parsed : null,
        _        => null,
    };

    // A double as the decimal of its shortest text that reads back as it - its round-trip form, as text, not the
    // runtime's conversion, which keeps 15 digits on one framework and more on another - when a decimal holds that
    // text; a whole double of a long's range is that long. Null for NaN, the infinities and what no decimal holds.
    private static decimal? RoundTripDecimal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;
        if (Math.Floor(value) == value && Math.Abs(value) < 9.2e18) return (long)value;
        if (Math.Abs(value) >= 7.9e28) return null;
        string text = value.ToString("R", Invariant);
        if (double.Parse(text, NumberStyles.Float, Invariant) != value) text = value.ToString("G17", Invariant);
        if (!decimal.TryParse(text, NumberStyles.Float, Invariant, out decimal parsed)) return null;
        // A decimal keeps 28 places: a double smaller than that reads as some other number, or as 0.
        return double.Parse(parsed.ToString(Invariant), NumberStyles.Float, Invariant) == value ? parsed : null;
    }

    // A float as the decimal of its shortest text that reads back as it, as for a double.
    private static decimal? RoundTripDecimal(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return null;
        if (Math.Floor(value) == value && Math.Abs(value) < 9.2e18f) return (long)value;
        if (Math.Abs(value) >= 7.9e28f) return null;
        string text = value.ToString("R", Invariant);
        if (float.Parse(text, NumberStyles.Float, Invariant) != value) text = value.ToString("G9", Invariant);
        if (!decimal.TryParse(text, NumberStyles.Float, Invariant, out decimal parsed)) return null;
        return float.Parse(parsed.ToString(Invariant), NumberStyles.Float, Invariant) == value ? parsed : null;
    }

    // The double, and the float, nearest a decimal: read from its text, as RoundTripDecimal writes one.
    private static double DoubleNearest(decimal value) => double.Parse(value.ToString(Invariant), NumberStyles.Float, Invariant);
    private static float SingleNearest(decimal value) => float.Parse(value.ToString(Invariant), NumberStyles.Float, Invariant);

    // The number a value is, for a column that takes numbers: refused with why when it is none.
    private static decimal NumberOf(object value, string what)
    {
        if (value is bool or char or not (sbyte or byte or short or ushort or int or uint or long or ulong or decimal or double or float or string))
            throw NotOfType(value, $"{what} takes numbers");
        return AsDecimal(value) ?? throw value switch
        {
            string        => NotOfType(value, $"it does not read as a number, and {what} takes numbers"),
            double d when double.IsNaN(d) || double.IsInfinity(d) => new NotHeldException($"{Describe(value)} is not a number {what} holds"),
            float f when float.IsNaN(f) || float.IsInfinity(f)    => new NotHeldException($"{Describe(value)} is not a number {what} holds"),
            _             => new NotHeldException($"{Describe(value)} has more digits than {what} holds"),
        };
    }

    private static long IntegerOf(object value, long min, long max, string range)
    {
        decimal number = NumberOf(value, "an integer column");
        if (decimal.Truncate(number) != number)
            throw new NotHeldException($"{Describe(value)} is not a whole number, and {range}");
        if (number < min || number > max)
            throw new NotHeldException($"{Describe(value)} is out of range: {range}");
        return (long)number;
    }

    private static float SingleOf(object value)
    {
        switch (value)
        {
            case float f: return f;
            case double d:
            {
                float single = (float)d;
                if ((double)single == d || double.IsNaN(d)) return single;
                throw new NotHeldException($"{Describe(value)} has more digits than a Single column holds: it would read back as {single.ToString("R", Invariant)}");
            }
            case string s when !decimal.TryParse(s, NumberStyles.Float, Invariant, out _)
                            && double.TryParse(s, NumberStyles.Float, Invariant, out double parsed):
                return SingleOf(parsed);   // NaN, Infinity and what is past a decimal's range
        }
        decimal number = NumberOf(value, "a Single column");
        float asSingle = SingleNearest(number);
        if (RoundTripDecimal(asSingle) == number) return asSingle;
        throw new NotHeldException($"{Describe(value)} has more digits than a Single column holds: it would read back as {asSingle.ToString("R", Invariant)}");
    }

    private static double DoubleOf(object value)
    {
        switch (value)
        {
            case double d: return d;
            case float f: return f;
            case string s when !decimal.TryParse(s, NumberStyles.Float, Invariant, out _)
                            && double.TryParse(s, NumberStyles.Float, Invariant, out double parsed):
                return parsed;   // NaN, Infinity and what is past a decimal's range
        }
        decimal number = NumberOf(value, "a Double column");
        double asDouble = DoubleNearest(number);
        if (RoundTripDecimal(asDouble) == number) return asDouble;
        throw new NotHeldException($"{Describe(value)} has more digits than a Double column holds: it would read back as {asDouble.ToString("R", Invariant)}");
    }

    private static readonly decimal CurrencyMax = 922_337_203_685_477.5807m;
    private static readonly decimal CurrencyMin = -922_337_203_685_477.5808m;

    private static decimal CurrencyOf(object value)
    {
        decimal number = NumberOf(value, "a Currency column");
        if (decimal.Round(number, 4) != number)
            throw new NotHeldException($"{Describe(value)} has more than four places after the point, and a Currency column keeps four");
        if (number < CurrencyMin || number > CurrencyMax)
            throw new NotHeldException($"{Describe(value)} is out of range: a Currency column holds -922,337,203,685,477.5808 to 922,337,203,685,477.5807");
        return number;
    }

    private static decimal DecimalOf(object value, int precision, int scale)
    {
        decimal number = NumberOf(value, "a Decimal column");
        if (scale > MaxDecimalPrecision || decimal.Round(number, scale) != number)
            throw new NotHeldException($"{Describe(value)} has more than {scale} places after the point, and the Decimal column keeps {scale}");
        if (precision > 0 && UnscaledAt(number, scale) >= BigInteger.Pow(10, precision))
            throw new NotHeldException($"{Describe(value)} has more digits than the Decimal column's {precision}, {scale} of them after the point");
        return number;
    }

    // |value| without its point, at scale places after it: value has no digit but zeros past them.
    private static BigInteger UnscaledAt(decimal value, int scale)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger mantissa = new BigInteger((uint)bits[2]) << 64 | new BigInteger((uint)bits[1]) << 32 | new BigInteger((uint)bits[0]);
        int own = (bits[3] >> 16) & 0xFF;
        return own >= scale ? mantissa / BigInteger.Pow(10, own - scale) : mantissa * BigInteger.Pow(10, scale - own);
    }

    // The first date a Date/Time column holds: an OLE Automation date starts at 1 January 100.
    private static readonly DateTime FirstDateTime = new(100, 1, 1);

    private static DateTime DateTimeOf(object value, bool extended)
    {
        DateTime date = value switch
        {
            DateTime d       => d,
            DateTimeOffset o => o.UtcDateTime,
            // Text with an offset is the UTC time it denotes, as a DateTimeOffset is; text without one is the time it
            // says. Neither depends on the machine's time zone.
            string s when DateTimeOffset.TryParse(s, Invariant, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) => parsed.UtcDateTime,
            _ => throw NotOfType(value, $"a {(extended ? "Date/Time Extended" : "Date/Time")} column takes dates"),
        };
        if (!extended && date < FirstDateTime)
            throw new NotHeldException($"{Describe(date)} is before 1 January 100, the first date a Date/Time column holds");
        return date;
    }

    private static Guid GuidOf(object value) => value switch
    {
        Guid g => g,
        string s when Guid.TryParse(s, out Guid parsed) => parsed,
        _ => throw NotOfType(value, "a GUID column takes a Guid or its text"),
    };

    private static string TextOf(object value, Column? column)
    {
        string text = value switch
        {
            string s         => s,
            char c           => c.ToString(),
            bool b           => b ? "True" : "False",
            double d         => d.ToString("R", Invariant),
            float f          => f.ToString("R", Invariant),
            DateTime d       => d.ToString("o", Invariant),
            DateTimeOffset o => o.ToString("o", Invariant),
            Guid g           => g.ToString("D"),
            sbyte or byte or short or ushort or int or uint or long or ulong or decimal => Convert.ToString(value, Invariant)!,
            _ => throw NotOfType(value, $"a {(column?.DataType ?? DataType.Memo)} column takes text, or a value with an exact text"),
        };
        if (column is null) return text;

        int length = column.MaxLength;
        if (!column.IsVariableLength && text.Length != length)
            throw new NotHeldException(
                $"{Describe(text)} has {text.Length} characters, and the column holds exactly {length} (SQL's CHAR): it would read back filled with spaces",
                text.Length > length ? text.Length : null);
        if (text.Length > length)
            throw new NotHeldException($"{Describe(text)} has {text.Length} characters, and the column holds {length}", text.Length);
        return text;
    }

    private static byte[] BinaryOf(object value, Column column)
    {
        var bytes = value as byte[] ?? throw NotOfType(value, "a Binary column holds bytes");
        int length = column.LengthInBytes;
        if (!column.IsVariableLength && bytes.Length != length)
            throw new NotHeldException(
                $"{bytes.Length} bytes are not the {length} the column holds (SQL's BINARY): a shorter value would read back filled with zeros");
        if (bytes.Length > length)
            throw new NotHeldException($"{bytes.Length} bytes are more than the {length} the Binary column holds");
        return bytes;
    }
}
