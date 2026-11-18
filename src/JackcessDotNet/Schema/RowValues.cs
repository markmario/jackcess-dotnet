using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// What a value a caller puts in a <see cref="Row"/> means once it is written: the one reading of it that the
/// row's bytes and its index keys are both made from, so the two cannot disagree.
/// </summary>
internal static class RowValues
{
    /// <summary>True for no value: <c>null</c>, or <see cref="DBNull"/> as a <c>DataRow</c> has it.</summary>
    public static bool IsNull(object? value) => value is null or DBNull;

    /// <summary>
    /// The Yes/No a value stands for, as Java Jackcess reads one: a <see cref="bool"/> is itself, a number is Yes
    /// unless it is zero (Access's own rule), text is Yes when it says "true" in any case, and no value is No -
    /// a Yes/No column holds no null.
    /// </summary>
    public static bool ToBoolean(object? value) => value switch
    {
        null or DBNull => false,
        bool b         => b,
        string s       => bool.TryParse(s, out bool parsed) && parsed,
        IConvertible c => c.GetTypeCode() switch
        {
            TypeCode.Single or TypeCode.Double => c.ToDouble(CultureInfo.InvariantCulture) != 0d,
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Decimal => c.ToDecimal(CultureInfo.InvariantCulture) != 0m,
            _ => false,
        },
        _ => false,
    };

    /// <summary>
    /// <paramref name="row"/> as it will be written to a table of <paramref name="columns"/>: no
    /// <see cref="DBNull"/> (a column without a value has none), and a Yes/No column's value as the
    /// <see cref="bool"/> it stands for. The row itself when it is already so; otherwise a copy - the caller's
    /// row is not changed.
    /// </summary>
    public static Row AsWritten(Row row, IReadOnlyList<Column> columns)
    {
        Row? written = null;
        foreach (var column in columns)
        {
            if (!row.TryGetValue(column.Name, out var value)) continue;
            object? normal = value is DBNull ? null
                : column.DataType == DataType.Boolean && value is not (null or bool) ? ToBoolean(value)
                : value;
            if (ReferenceEquals(normal, value)) continue;
            written ??= new Row(row);
            written[column.Name] = normal;
        }
        return written ?? row;
    }
}
