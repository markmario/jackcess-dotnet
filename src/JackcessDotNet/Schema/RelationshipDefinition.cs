namespace JackcessDotNet;

/// <summary>How a query Access designs joins the two tables of a relationship. Jackcess's <c>Relationship.JoinType</c>.</summary>
public enum JoinType
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
/// <see cref="Database.CreateRelationship"/>; the relationship the database then has is a
/// <see cref="Relationship"/>.
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
    /// <param name="fromTableName">The table referred to (the "one" side, the parent).</param>
    /// <param name="fromColumnName">Its column: with referential integrity, one with a unique index or primary key.</param>
    /// <param name="toTableName">The table that refers to it (the "many" side, the child).</param>
    /// <param name="toColumnName">Its column, the foreign key: of the same type as <paramref name="fromColumnName"/>.</param>
    public RelationshipDefinition(string fromTableName, string fromColumnName, string toTableName, string toColumnName)
        : this(fromTableName, new[] { fromColumnName }, toTableName, new[] { toColumnName }) { }

    /// <summary>A relationship of one column or several.</summary>
    /// <param name="fromTableName">The table referred to (the "one" side, the parent).</param>
    /// <param name="fromColumnNames">
    /// Its columns, in key order: one to <see cref="IndexDefinition.MaxColumns"/>. With referential integrity,
    /// columns with a unique index or primary key on exactly them.
    /// </param>
    /// <param name="toTableName">The table that refers to it (the "many" side, the child).</param>
    /// <param name="toColumnNames">
    /// Its columns, the foreign key: as many as <paramref name="fromColumnNames"/>, each of the same type as the
    /// column at the same place there.
    /// </param>
    public RelationshipDefinition(string fromTableName, IEnumerable<string> fromColumnNames,
                                  string toTableName, IEnumerable<string> toColumnNames)
    {
        if (string.IsNullOrWhiteSpace(fromTableName)) throw new ArgumentException("A relationship needs the table it refers to.", nameof(fromTableName));
        if (string.IsNullOrWhiteSpace(toTableName))   throw new ArgumentException("A relationship needs the table that refers.", nameof(toTableName));
        if (fromColumnNames is null) throw new ArgumentNullException(nameof(fromColumnNames));
        if (toColumnNames is null)   throw new ArgumentNullException(nameof(toColumnNames));

        string[] from = fromColumnNames.ToArray();
        string[] to   = toColumnNames.ToArray();
        if (from.Length == 0)
            throw new ArgumentException("A relationship needs at least one pair of columns.", nameof(fromColumnNames));
        if (from.Length != to.Length)
            throw new ArgumentException(
                $"A relationship matches each column referred to with one that refers to it; {from.Length} and {to.Length} were given.",
                nameof(toColumnNames));
        if (from.Length > IndexDefinition.MaxColumns)
            throw new ArgumentException($"A relationship has at most {IndexDefinition.MaxColumns} pairs of columns; {from.Length} were given.", nameof(fromColumnNames));
        if (from.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A relationship's column names must not be empty.", nameof(fromColumnNames));
        if (to.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A relationship's column names must not be empty.", nameof(toColumnNames));

        FromTableName   = fromTableName;
        ToTableName     = toTableName;
        FromColumnNames = from;
        ToColumnNames   = to;
        ColumnPairs     = from.Select((column, i) => (column, to[i])).ToArray();
    }

    /// <summary>The name of the table referred to: <c>szReferencedObject</c> in MSysRelationships.</summary>
    public string FromTableName { get; }

    /// <summary>The name of the table that refers to it: <c>szObject</c> in MSysRelationships.</summary>
    public string ToTableName { get; }

    /// <summary>The names of the columns of <see cref="FromTableName"/> that are referred to, in key order.</summary>
    public IReadOnlyList<string> FromColumnNames { get; }

    /// <summary>
    /// The names of the columns of <see cref="ToTableName"/> that refer, each matching the name at the same place
    /// in <see cref="FromColumnNames"/>.
    /// </summary>
    public IReadOnlyList<string> ToColumnNames { get; }

    /// <summary>The matched columns, pair by pair.</summary>
    internal IReadOnlyList<(string FromColumn, string ToColumn)> ColumnPairs { get; }

    /// <summary>
    /// The relationship's name, unique among the database's relationships and, with referential integrity, among
    /// the indexes of <see cref="ToTableName"/>, where Access gives the foreign key's index this name. Null (the
    /// default) for a name made of the two tables' names, as Access's Relationships window makes one, with a
    /// number after it when that is taken.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Whether Access enforces the relationship: a row of <see cref="ToTableName"/> must then refer to a row of
    /// <see cref="FromTableName"/> or to nothing. Creating it is refused when a row already there does neither.
    /// </summary>
    public bool HasReferentialIntegrity { get; set; }

    /// <summary>A change to a referenced key is made to the rows that refer to it. Needs <see cref="HasReferentialIntegrity"/>.</summary>
    public bool CascadeUpdates { get; set; }

    /// <summary>Deleting a referenced row deletes the rows that refer to it. Needs <see cref="HasReferentialIntegrity"/>.</summary>
    public bool CascadeDeletes { get; set; }

    /// <summary>Deleting a referenced row sets the columns that refer to it to null. Needs <see cref="HasReferentialIntegrity"/>.</summary>
    public bool CascadeNullOnDelete { get; set; }

    /// <summary>How a query Access designs joins the two tables. It changes nothing about which rows are allowed.</summary>
    public JoinType JoinType { get; set; }

    /// <summary>
    /// Whether the relationship is marked one to one (<see cref="Relationship.IsOneToOne"/>). Null (the default)
    /// marks it when the columns on each side have a unique index or primary key of their own, as Access's
    /// Relationships window does. <c>true</c> marks it whatever indexes there are, as DAO does when told to: with
    /// <see cref="HasReferentialIntegrity"/> the foreign key's index is then a unique one, built over the rows
    /// already there, so creating the relationship is refused when two of them refer to one row. <c>false</c>
    /// leaves it unmarked, and the foreign key's index an ordinary one, though both sides are unique.
    /// </summary>
    public bool? IsOneToOne { get; set; }
}
