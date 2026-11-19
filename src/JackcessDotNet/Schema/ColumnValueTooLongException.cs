namespace JackcessDotNet;

/// <summary>
/// A value longer than its column allows, refused before anything of its row is written - as Access refuses one.
/// Its own type, so a caller can tell it from any other argument error and say which record held the value.
/// </summary>
public sealed class ColumnValueTooLongException : ArgumentException
{
    /// <summary>
    /// Creates the exception for a value of <paramref name="length"/> characters given to a column that holds
    /// <paramref name="maxLength"/>. Its <see cref="Exception.Message"/> names the column and both numbers.
    /// </summary>
    /// <param name="columnName">The column's name.</param>
    /// <param name="maxLength">The most characters the column holds.</param>
    /// <param name="length">The characters the value has.</param>
    public ColumnValueTooLongException(string columnName, int maxLength, int length)
        : this(columnName, maxLength, length, $"Column '{columnName}' holds at most {maxLength} characters; the value has {length}.")
    {
    }

    /// <summary>The exception with a message of the caller's, which says where the value came from: an import's row.</summary>
    internal ColumnValueTooLongException(string columnName, int maxLength, int length, string message)
        : base(message)
    {
        ColumnName = columnName;
        MaxLength = maxLength;
        Length = length;
    }

    /// <summary>The column's name.</summary>
    public string ColumnName { get; }

    /// <summary>The most characters the column holds.</summary>
    public int MaxLength { get; }

    /// <summary>The characters the value has.</summary>
    public int Length { get; }
}
