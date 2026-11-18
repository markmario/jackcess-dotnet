namespace JackcessDotNet;

/// <summary>
/// A foreign-key / join relationship between two tables. Parsed from
/// MSysRelationships; one row per column pair (a composite relationship is
/// reassembled from rows that share <see cref="Name"/>).
///
/// One is created with <see cref="Database.CreateRelationship"/>; none can be changed or dropped. One that has referential integrity is enforced when
/// <see cref="Database.EnforceForeignKeys"/> is set.
/// </summary>
public sealed class Relationship
{
    /// <summary>Relationship name (e.g. "{guid}", "Customers_Orders").</summary>
    public string Name { get; }

    /// <summary>Parent (referenced) table — szReferencedObject in MSysRelationships.</summary>
    public string FromTable { get; }

    /// <summary>Child (referencing) table — szObject in MSysRelationships.</summary>
    public string ToTable { get; }

    /// <summary>
    /// Column pairs in this relationship, in icolumn order. For a single-column
    /// FK this list has one entry; composites have multiple.
    /// </summary>
    public IReadOnlyList<(string FromColumn, string ToColumn)> ColumnPairs { get; }

    /// <summary>Raw grbit flags from MSysRelationships.</summary>
    public int Flags { get; }

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
    public bool CascadeNull    => (Flags & 0x00002000) != 0;
    public bool LeftOuterJoin  => (Flags & 0x01000000) != 0;
    public bool RightOuterJoin => (Flags & 0x02000000) != 0;

    internal Relationship(
        string name, string fromTable, string toTable, int flags,
        IReadOnlyList<(string From, string To)> columnPairs)
    {
        Name        = name;
        FromTable   = fromTable;
        ToTable     = toTable;
        Flags       = flags;
        ColumnPairs = columnPairs;
    }

    public override string ToString()
        => $"{Name}: {FromTable}({string.Join(",", ColumnPairs.Select(p => p.FromColumn))})" +
           $" → {ToTable}({string.Join(",", ColumnPairs.Select(p => p.ToColumn))})";
}
