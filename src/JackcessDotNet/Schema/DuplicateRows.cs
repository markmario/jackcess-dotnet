namespace JackcessDotNet;

/// <summary>
/// Rows that a unique index or primary key would refuse as duplicates of one another, or of a row the table already
/// holds: what <see cref="Table.FindDuplicates"/> returns, one for each key that more than one row would have. The
/// key is the index's, so rows Access does not tell apart - text that differs only in the case of its letters, in
/// trailing spaces, or in a character Access sorts as another - are duplicates here as in Access.
/// </summary>
public sealed class DuplicateRows
{
    internal DuplicateRows(string indexName, IReadOnlyList<int> rowIndexes, bool inTable)
    {
        IndexName  = indexName;
        RowIndexes = rowIndexes;
        InTable    = inTable;
    }

    /// <summary>The name of the index the rows would be duplicates in: the primary key's index, or a unique index.</summary>
    public string IndexName { get; }

    /// <summary>The places, from 0 and in order, of the rows among those looked at that have the key.</summary>
    public IReadOnlyList<int> RowIndexes { get; }

    /// <summary>Whether a row the table already holds has the key too.</summary>
    public bool InTable { get; }

    /// <inheritdoc/>
    public override string ToString()
        => $"{IndexName}: rows {string.Join(", ", RowIndexes)}{(InTable ? " and a row of the table" : "")}";
}
