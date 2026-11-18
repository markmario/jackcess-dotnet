namespace JackcessDotNet;

/// <summary>
/// The type of a column, or of a property's value. A member is named for the CLR type of exactly that meaning
/// where there is one, and otherwise by Access's word; each says what Access and Jackcess call it. The values
/// are the bytes a table definition holds.
/// </summary>
public enum DataType : byte
{
    /// <summary>A <see cref="bool"/>: Access's Yes/No, Jackcess's <c>BOOLEAN</c>. Holds no null: a row without a value is No.</summary>
    Boolean = 0x01,

    /// <summary>A <see cref="byte"/>, 0 to 255: Access's Byte, Jackcess's <c>BYTE</c>.</summary>
    Byte = 0x02,

    /// <summary>A <see cref="short"/>, 16 bits: Access's Integer, Jackcess's <c>INT</c>.</summary>
    Int16 = 0x03,

    /// <summary>An <see cref="int"/>, 32 bits: Access's Long Integer, Jackcess's <c>LONG</c>. The type of a counting AutoNumber.</summary>
    Int32 = 0x04,

    /// <summary>A <see cref="decimal"/> of four decimal places, kept as a 64-bit integer: Access's Currency, Jackcess's <c>MONEY</c>.</summary>
    Currency = 0x05,

    /// <summary>A <see cref="float"/>, 32 bits: Access's Single, Jackcess's <c>FLOAT</c>.</summary>
    Single = 0x06,

    /// <summary>A <see cref="double"/>, 64 bits: Access's Double, Jackcess's <c>DOUBLE</c>.</summary>
    Double = 0x07,

    /// <summary>
    /// A <see cref="System.DateTime"/>, kept as an OLE Automation date (a double: days since 30 December 1899):
    /// Access's Date/Time, Jackcess's <c>SHORT_DATE_TIME</c>.
    /// </summary>
    DateTime = 0x08,

    /// <summary>A <c>byte[]</c> of up to 255 bytes: Access's Binary, Jackcess's <c>BINARY</c>.</summary>
    Binary = 0x09,

    /// <summary>A <see cref="string"/> of up to 255 characters: Access's Short Text (Text before Access 2013), Jackcess's <c>TEXT</c>.</summary>
    Text = 0x0A,

    /// <summary>A <c>byte[]</c> of any length, kept on pages of its own: Access's OLE Object, Jackcess's <c>OLE</c>.</summary>
    OleObject = 0x0B,

    /// <summary>A <see cref="string"/> of any length, kept on pages of its own: Access's Long Text (Memo before Access 2013), Jackcess's <c>MEMO</c>.</summary>
    Memo = 0x0C,

    /// <summary>A <see cref="System.Guid"/>: Access's Replication ID, Jackcess's <c>GUID</c>. An AutoNumber of this type is given a new GUID.</summary>
    Guid = 0x0F,

    /// <summary>
    /// A <see cref="decimal"/> of a precision and scale the column chooses, up to 28 digits: Access's Decimal,
    /// Jackcess's <c>NUMERIC</c>.
    /// </summary>
    Decimal = 0x10,

    /// <summary>
    /// An attachment or multi-value field (Access 2007 and later), Jackcess's <c>COMPLEX_TYPE</c>: its values are
    /// in a table of their own. A table with one is read, though not the values of that column, and it is
    /// not written.
    /// </summary>
    Complex = 0x12,

    /// <summary>
    /// A <see cref="long"/>, 64 bits: Access's Large Number (Access 2016 and later; .accdb only), Jackcess's
    /// <c>BIG_INT</c>.
    /// </summary>
    Int64 = 0x13,

    /// <summary>
    /// Date and time from 1 January 0001 to 31 December 9999, to 100 nanoseconds: Access's Date/Time Extended
    /// (Access 2019 and later; .accdb only), Jackcess's <c>EXT_DATE_TIME</c>, kept as 42 bytes of text. Read and
    /// written as a <see cref="System.DateTime"/>, whose range and tick are the same, so nothing is lost either
    /// way; the value has no time zone, and is read with <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    DateTimeExtended = 0x14,
}

/// <summary>
/// Extension methods and metadata for <see cref="DataType"/>.
/// </summary>
internal static class DataTypeExtensions
{
    private static readonly Dictionary<DataType, DataTypeInfo> _info = new()
    {
        [DataType.Boolean]       = new(FixedSize: 1,  IsVariable: false, IsLong: false),
        [DataType.Byte]          = new(FixedSize: 1,  IsVariable: false, IsLong: false),
        [DataType.Int16]           = new(FixedSize: 2,  IsVariable: false, IsLong: false),
        [DataType.Int32]          = new(FixedSize: 4,  IsVariable: false, IsLong: false),
        [DataType.Currency]         = new(FixedSize: 8,  IsVariable: false, IsLong: false),
        [DataType.Single]         = new(FixedSize: 4,  IsVariable: false, IsLong: false),
        [DataType.Double]        = new(FixedSize: 8,  IsVariable: false, IsLong: false),
        [DataType.DateTime] = new(FixedSize: 8,  IsVariable: false, IsLong: false),
        [DataType.Guid]          = new(FixedSize: 16, IsVariable: false, IsLong: false),
        [DataType.Decimal]       = new(FixedSize: 17, IsVariable: false, IsLong: false),
        [DataType.Binary]        = new(FixedSize: 0,  IsVariable: true,  IsLong: false, MaxSize: 255),
        [DataType.Text]          = new(FixedSize: 0,  IsVariable: true,  IsLong: false, MaxSize: 510),  // 255 chars * 2 bytes UTF-16
        [DataType.OleObject]           = new(FixedSize: 0,  IsVariable: true,  IsLong: true),
        [DataType.Memo]          = new(FixedSize: 0,  IsVariable: true,  IsLong: true),
        // Complex columns (multi-value fields, attachments, version history): the
        // value lives in a separate complex-column-data table; what's stored in
        // the row is a 4-byte ID. Decoding the linked table isn't implemented yet,
        // but registering the type as "fixed 4-byte" lets the column be parsed and
        // tables that contain it can still be opened + iterated.
        [DataType.Complex]       = new(FixedSize: 4,  IsVariable: false, IsLong: false),
        [DataType.Int64]        = new(FixedSize: 8,  IsVariable: false, IsLong: false),
        [DataType.DateTimeExtended] = new(FixedSize: ExtendedDateTimeCodec.Size, IsVariable: false, IsLong: false),
    };

    private static readonly DataTypeInfo UnknownInfo =
        new(FixedSize: 0, IsVariable: true, IsLong: false);

    /// <summary>
    /// Returns the layout info for <paramref name="dt"/>. Unknown types (newer
    /// Access generations introduce new codes from time to time) fall back to
    /// a variable-length placeholder so the column can be skipped rather than
    /// crashing the whole TDEF parse.
    /// </summary>
    public static DataTypeInfo GetInfo(this DataType dt)
        => _info.TryGetValue(dt, out var info) ? info : UnknownInfo;

    public static bool IsVariableLength(this DataType dt) => GetInfo(dt).IsVariable;
    public static bool IsFixedLength(this DataType dt) => !GetInfo(dt).IsVariable;
    public static bool IsLongValue(this DataType dt) => GetInfo(dt).IsLong;

    /// <summary>Fixed storage size in bytes; 0 for variable-length types.</summary>
    public static int GetFixedSize(this DataType dt) => GetInfo(dt).FixedSize;

    /// <summary>
    /// Default column size: for fixed types returns the fixed size,
    /// for Text returns the default max char count (50), for Binary returns 50.
    /// </summary>
    public static int GetDefaultSize(this DataType dt)
    {
        var info = GetInfo(dt);
        if (!info.IsVariable) return info.FixedSize;
        if (dt == DataType.Text) return 100; // 50 chars * 2 bytes
        if (dt == DataType.Binary) return 50;
        return 0; // long types
    }

    public static int GetMaxSize(this DataType dt) => GetInfo(dt).MaxSize;
}

/// <param name="FixedSize">Storage size in bytes for fixed-length types (0 if variable).</param>
/// <param name="IsVariable">True if the column stores data in the variable-length area.</param>
/// <param name="IsLong">True for MEMO/OLE types stored in overflow pages.</param>
/// <param name="MaxSize">Max bytes for variable-length types (0 = unlimited/overflow).</param>
internal record DataTypeInfo(int FixedSize, bool IsVariable, bool IsLong, int MaxSize = 0);
