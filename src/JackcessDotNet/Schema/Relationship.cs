namespace JackcessDotNet;

/// <summary>
/// A relationship between two tables, as Access's Relationships window shows one: the table referred to and its
/// columns, the table that refers to it and the columns that do. Read from MSysRelationships, which has a row
/// for each pair of columns.
///
/// One is created with <see cref="Database.CreateRelationship"/>; none can be changed or dropped. One that has
/// referential integrity is enforced while <see cref="Database.EnforceForeignKeys"/> is set.
/// </summary>
public sealed class Relationship
{
    /// <summary>Relationship name (e.g. "{guid}", "Customers_Orders").</summary>
    public string Name { get; }

    /// <summary>The name of the table referred to, the parent: szReferencedObject in MSysRelationships.</summary>
    public string FromTableName { get; }

    /// <summary>The name of the table that refers to it, the child: szObject in MSysRelationships.</summary>
    public string ToTableName { get; }

    /// <summary>
    /// The names of the columns of <see cref="FromTableName"/> that are referred to, in the relationship's order:
    /// one for a relationship of one column. Each is matched by the name at the same place in
    /// <see cref="ToColumnNames"/>.
    /// </summary>
    public IReadOnlyList<string> FromColumnNames { get; }

    /// <summary>
    /// The names of the columns of <see cref="ToTableName"/> that refer - the foreign key - in the relationship's
    /// order, each matching the name at the same place in <see cref="FromColumnNames"/>.
    /// </summary>
    public IReadOnlyList<string> ToColumnNames { get; }

    /// <summary>The matched columns, pair by pair, in icolumn order.</summary>
    internal IReadOnlyList<(string FromColumn, string ToColumn)> ColumnPairs { get; }

    /// <summary>Raw grbit flags from MSysRelationships.</summary>
    internal int Flags { get; }

    /// <summary>Whether a row of the referred-to table has at most one row referring to it.</summary>
    public bool IsOneToOne     => (Flags & 0x00000001) != 0;

    /// <summary>
    /// Whether Access enforces the relationship. One without referential integrity only tells the query designer how
    /// the tables join, and its cascade flags mean nothing.
    /// </summary>
    public bool HasReferentialIntegrity => (Flags & 0x00000002) == 0;

    /// <summary>A change to the parent's key is made to the rows that refer to it.</summary>
    public bool CascadeUpdates => (Flags & 0x00000100) != 0;
    /// <summary>Deleting a parent row deletes the rows that refer to it.</summary>
    public bool CascadeDeletes => (Flags & 0x00001000) != 0;
    /// <summary>Deleting a parent row sets the columns that refer to it to null.</summary>
    public bool CascadeNullOnDelete => (Flags & 0x00002000) != 0;

    /// <summary>How a query Access designs joins the two tables. It changes nothing about which rows are allowed.</summary>
    public JoinType JoinType
        => (Flags & 0x01000000) != 0 ? JoinType.LeftOuter
         : (Flags & 0x02000000) != 0 ? JoinType.RightOuter
         : JoinType.Inner;

    internal Relationship(
        string name, string fromTable, string toTable, int flags,
        IReadOnlyList<(string From, string To)> columnPairs)
    {
        Name            = name;
        FromTableName   = fromTable;
        ToTableName     = toTable;
        Flags           = flags;
        ColumnPairs     = columnPairs;
        FromColumnNames = columnPairs.Select(pair => pair.From).ToArray();
        ToColumnNames   = columnPairs.Select(pair => pair.To).ToArray();
    }

    /// <summary>The relationship as "name: from(columns) → to(columns)".</summary>
    public override string ToString()
        => $"{Name}: {FromTableName}({string.Join(",", FromColumnNames)})" +
           $" → {ToTableName}({string.Join(",", ToColumnNames)})";
}
