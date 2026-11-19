namespace JackcessDotNet;

/// <summary>
/// A batch of rows that was not inserted (<see cref="Table.InsertRange"/>) because one of its rows was not: it
/// says which row, and <see cref="Exception.InnerException"/> is what the insert of that row threw - a
/// <see cref="ConstraintViolationException"/> for a row that breaks a rule of the database, a
/// <see cref="ColumnValueTooLongException"/> for text or bytes longer than its column, an <see cref="System.IO.IOException"/>
/// for a file that could not be written. What Jackcess reports with a <c>BatchUpdateException</c>.
/// <para>
/// Where Jackcess leaves the rows before the failing one in the table and counts them, this library takes them
/// out again: nothing of the batch is in the file, so there is no count to report, and the batch can be put
/// right and inserted again whole.
/// </para>
/// </summary>
public sealed class BatchInsertException : Exception
{
    internal BatchInsertException(int rowIndex, int rowCount, Exception innerException)
        : base($"Row {rowIndex + 1} of the {rowCount} to insert was not inserted, and none of them is in the table: {innerException.Message}",
               innerException)
    {
        RowIndex = rowIndex;
    }

    /// <summary>The place, from 0, of the row that was not inserted among the rows given.</summary>
    public int RowIndex { get; }
}
