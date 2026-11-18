namespace JackcessDotNet;

/// <summary>How a query Access designs joins the two tables of a relationship.</summary>
public enum RelationshipJoinType
{
    /// <summary>Only the rows where the joined columns of both tables are equal.</summary>
    Inner,

    /// <summary>Every row of the referenced ("from") table, and the rows of the referring table that match.</summary>
    LeftOuter,

    /// <summary>Every row of the referring ("to") table, and the rows of the referenced table that match.</summary>
    RightOuter,
}

/// <summary>
/// A relationship to create between two tables: the table referred to and its columns, the table that refers to it
/// and the columns that do, and what Access is to do about it. Pass it to
/// <see cref="Database.CreateRelationship"/>.
/// <code>
///   db.CreateRelationship(new RelationshipDefinition("Customers", "Id", "Orders", "CustomerId")
///   {
///       HasReferentialIntegrity = true,
///       CascadeDeletes = true,
///   });
/// </code>
/// As in Access's Relationships window and Jackcess's <c>RelationshipBuilder</c>, a relationship has no
/// referential integrity unless it is asked for: it then only tells Access's query designer how the tables join.
/// </summary>
public sealed class RelationshipDefinition
{
    /// <summary>A relationship of one column.</summary>
    /// <param name="fromTable">The table referred to (the "one" side, the parent).</param>
    /// <param name="fromColumn">Its column: with referential integrity, one with a unique index or primary key.</param>
    /// <param name="toTable">The table that refers to it (the "many" side, the child).</param>
    /// <param name="toColumn">Its column, the foreign key: of the same type as <paramref name="fromColumn"/>.</param>
    public RelationshipDefinition(string fromTable, string fromColumn, string toTable, string toColumn)
        : this(fromTable, toTable, (fromColumn, toColumn)) { }

    /// <summary>A relationship of one column or several.</summary>
    /// <param name="fromTable">The table referred to (the "one" side, the parent).</param>
    /// <param name="toTable">The table that refers to it (the "many" side, the child).</param>
    /// <param name="columnPairs">
    /// Each column of <paramref name="fromTable"/> with the column of <paramref name="toTable"/> that refers to
    /// it, in key order: one pair to <see cref="IndexDefinition.MaxColumns"/>.
    /// </param>
    public RelationshipDefinition(string fromTable, string toTable, params (string FromColumn, string ToColumn)[] columnPairs)
    {
        if (string.IsNullOrWhiteSpace(fromTable)) throw new ArgumentException("A relationship needs the table it refers to.", nameof(fromTable));
        if (string.IsNullOrWhiteSpace(toTable))   throw new ArgumentException("A relationship needs the table that refers.", nameof(toTable));
        if (columnPairs is null || columnPairs.Length == 0)
            throw new ArgumentException("A relationship needs at least one pair of columns.", nameof(columnPairs));
        if (columnPairs.Length > IndexDefinition.MaxColumns)
            throw new ArgumentException($"A relationship has at most {IndexDefinition.MaxColumns} pairs of columns; {columnPairs.Length} were given.", nameof(columnPairs));
        if (columnPairs.Any(p => string.IsNullOrWhiteSpace(p.FromColumn) || string.IsNullOrWhiteSpace(p.ToColumn)))
            throw new ArgumentException("A relationship's column names must not be empty.", nameof(columnPairs));

        FromTable   = fromTable;
        ToTable     = toTable;
        ColumnPairs = columnPairs.ToArray();
    }

    /// <summary>The table referred to: <c>szReferencedObject</c> in MSysRelationships.</summary>
    public string FromTable { get; }

    /// <summary>The table that refers to it: <c>szObject</c> in MSysRelationships.</summary>
    public string ToTable { get; }

    /// <summary>Each column of <see cref="FromTable"/> with the column of <see cref="ToTable"/> that refers to it.</summary>
    public IReadOnlyList<(string FromColumn, string ToColumn)> ColumnPairs { get; }

    /// <summary>
    /// The relationship's name, unique among the database's relationships and, with referential integrity, among
    /// the indexes of <see cref="ToTable"/>, where Access gives the foreign key's index this name. Null (the
    /// default) for a name made of the two tables' names, as Access's Relationships window makes one, with a
    /// number after it when that is taken.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Whether Access enforces the relationship: a row of <see cref="ToTable"/> must then refer to a row of
    /// <see cref="FromTable"/> or to nothing. Creating it is refused when a row already there does neither.
    /// </summary>
    public bool HasReferentialIntegrity { get; set; }

    /// <summary>A change to a referenced key is made to the rows that refer to it. Needs <see cref="HasReferentialIntegrity"/>.</summary>
    public bool CascadeUpdates { get; set; }

    /// <summary>Deleting a referenced row deletes the rows that refer to it. Needs <see cref="HasReferentialIntegrity"/>.</summary>
    public bool CascadeDeletes { get; set; }

    /// <summary>Deleting a referenced row sets the columns that refer to it to null. Needs <see cref="HasReferentialIntegrity"/>.</summary>
    public bool CascadeNull { get; set; }

    /// <summary>How a query Access designs joins the two tables. It changes nothing about which rows are allowed.</summary>
    public RelationshipJoinType JoinType { get; set; }
}
