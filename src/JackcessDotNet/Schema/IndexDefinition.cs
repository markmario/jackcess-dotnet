namespace JackcessDotNet;

/// <summary>
/// An index to give a table when it is created, beside its primary key: its name, its columns in key order, and
/// whether it refuses duplicates. Pass it to <see cref="Database.CreateTable(string, IReadOnlyList{Column}, IReadOnlyList{string}?, IReadOnlyList{IndexDefinition})"/>
/// or <see cref="TableBuilder.WithIndex(IndexDefinition)"/>. The index is kept as rows are inserted, updated and
/// deleted, as Access keeps one.
/// <code>
///   new IndexDefinition("ByName", "LastName", "FirstName") { IsUnique = true }
/// </code>
/// </summary>
public sealed class IndexDefinition
{
    /// <summary>The most columns an index has (Jet's limit).</summary>
    public const int MaxColumns = 10;

    /// <summary>The most indexes a table has (Jet's limit), its primary key among them.</summary>
    public const int MaxIndexesPerTable = 32;

    /// <param name="name">The index's name, unique among the table's indexes.</param>
    /// <param name="columns">The names of the columns it is on, in key order: one to <see cref="MaxColumns"/>.</param>
    public IndexDefinition(string name, params string[] columns)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Index name must not be empty.", nameof(name));
        if (columns is null || columns.Length == 0)
            throw new ArgumentException("An index needs at least one column.", nameof(columns));
        if (columns.Length > MaxColumns)
            throw new ArgumentException($"An index has at most {MaxColumns} columns; {columns.Length} were given.", nameof(columns));
        if (columns.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("An index column name must not be empty.", nameof(columns));
        if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length)
            throw new ArgumentException("An index names each of its columns once.", nameof(columns));

        Name    = name;
        Columns = columns.ToArray();
    }

    /// <summary>The index's name.</summary>
    public string Name { get; }

    /// <summary>The names of the index's columns, in key order.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The columns, of <see cref="Columns"/>, that sort descending; the rest sort ascending.</summary>
    public IReadOnlyCollection<string> DescendingColumns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// True for an index that refuses a second row with the same values. As in Access, a row with a null in
    /// any of the index's columns is never a duplicate.
    /// </summary>
    public bool IsUnique { get; set; }

    /// <summary>True for an index that refuses a row with a null in any of its columns.</summary>
    public bool IsRequired { get; set; }

    /// <summary>True for an index that holds no entry for a row whose index columns are all null.</summary>
    public bool IgnoreNulls { get; set; }
}
