namespace JackcessDotNet;

/// <summary>
/// A value longer than its column allows - text with more characters than a Text column's length, or a
/// <c>byte[]</c> with more bytes than a Binary column's - refused before anything of its row is written, as Access
/// refuses one. Its own type, so a caller can tell it from any other argument error and say which record held the
/// value. Its <see cref="Exception.Message"/> names the column and gives both lengths, in characters for a Text column
/// and in bytes for a Binary one.
/// </summary>
public sealed class ColumnValueTooLongException : ArgumentException
{
    /// <summary>
    /// The exception with a message of the caller's, which says where the value came from: an import's row.
    /// <paramref name="maxLength"/> and <paramref name="length"/> are in the column's units
    /// (<see cref="Column.MaxLength"/>): characters for a Text column, bytes for a Binary one.
    /// </summary>
    internal ColumnValueTooLongException(string columnName, int maxLength, int length, string message)
        : base(message)
    {
        ColumnName = columnName;
        MaxLength = maxLength;
        Length = length;
    }

    /// <summary>The column's name.</summary>
    public string ColumnName { get; }

    /// <summary>
    /// The most the column holds, in its type's units (<see cref="Column.MaxLength"/>): characters for a Text column,
    /// bytes for a Binary one.
    /// </summary>
    public int MaxLength { get; }

    /// <summary>The value's length, in the same units as <see cref="MaxLength"/>.</summary>
    public int Length { get; }

    /// <summary>
    /// The exception for a value of <paramref name="length"/> units in a column of <paramref name="type"/> that holds
    /// <paramref name="maxLength"/>: bytes for a Binary column, characters for a Text one. A value
    /// <paramref name="stored"/> is the row's own, in a column an update does not name - one an earlier version of
    /// this library wrote longer than its column - and the message says so, and that giving the column a value that
    /// fits lets the update through.
    /// </summary>
    internal ColumnValueTooLongException(string columnName, DataType type, int maxLength, int length, bool stored = false)
        : base(MessageFor(columnName, type == DataType.Binary ? "bytes" : "characters", maxLength, length, stored))
    {
        ColumnName = columnName;
        MaxLength = maxLength;
        Length = length;
    }

    private static string MessageFor(string columnName, string units, int maxLength, int length, bool stored)
        => stored
            ? $"The row's stored value in column '{columnName}' is {length} {units}, longer than the {maxLength} the column holds, " +
              $"so the row is not rewritten with it: give column '{columnName}' a value that fits, in this update or on its own."
            : $"Column '{columnName}' holds at most {maxLength} {units}; the value has {length}.";
}
