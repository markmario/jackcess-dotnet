namespace JackcessDotNet;

/// <summary>
/// An index to give a table when it is created, beside its primary key: its name, its columns in key order, and
/// whether it refuses duplicates. Pass it to <see cref="Database.CreateTable(string, IEnumerable{ColumnDefinition}, IEnumerable{string}?, IEnumerable{IndexDefinition})"/>
/// or <see cref="Table.AddIndex"/>. The index is kept as rows are inserted, updated and deleted, as Access keeps
/// one; the index the table then has is an <see cref="Index"/>.
/// <code>
///   new IndexDefinition("ByName", "LastName", "FirstName") { IsUnique = true }
///   new IndexDefinition("Latest", "Customer", "Date") { DescendingColumns = { "Date" } }
/// </code>
/// </summary>
public sealed class IndexDefinition
{
    /// <summary>The most columns an index has (Jet's limit).</summary>
    public const int MaxColumns = 10;

    /// <summary>The most indexes a table has (Jet's limit), its primary key among them.</summary>
    public const int MaxIndexesPerTable = 32;

    /// <summary>
    /// Describes an index of a name on one column or several: not unique, taking nulls, every column ascending,
    /// until its properties say otherwise.
    /// </summary>
    /// <param name="name">The index's name, unique among the table's indexes.</param>
    /// <param name="columns">The names of the columns it is on, in key order: one to <see cref="MaxColumns"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is null, empty or white space; or <paramref name="columns"/> is null or has no
    /// names, more than <see cref="MaxColumns"/>, an empty name, or one name twice, whatever its case.
    /// </exception>
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

    /// <summary>
    /// The columns, of <see cref="Columns"/>, that sort descending, to add to; the rest sort ascending. A name
    /// here that is not one of the index's columns is refused when the index is made.
    /// </summary>
    public ICollection<string> DescendingColumns { get; } = new List<string>();

    /// <summary>
    /// True for an index that refuses a second row with the same values. As in Access, a row with a null in
    /// any of the index's columns is never a duplicate.
    /// </summary>
    public bool IsUnique { get; set; }

    /// <summary>True for an index that refuses a row with a null in any of its columns.</summary>
    public bool IsRequired { get; set; }

    /// <summary>True for an index that holds no entry for a row whose index columns are all null.</summary>
    public bool IgnoreNulls { get; set; }

    /// <summary>
    /// True for the table's primary key, to give a table that has none with <see cref="Table.AddIndex"/>: an index
    /// that refuses a second row with the same values and a row with a null in any of its columns, whatever
    /// <see cref="IsUnique"/> and <see cref="IsRequired"/> say. Access names a primary key "PrimaryKey". A table
    /// is created with its primary key by naming the key's columns to
    /// <see cref="Database.CreateTable(string, IEnumerable{ColumnDefinition}, IEnumerable{string}?, IEnumerable{IndexDefinition})"/>,
    /// which refuses a definition that has this set.
    /// </summary>
    public bool IsPrimaryKey { get; set; }
}
