namespace JackcessDotNet;

/// <summary>
/// An index a table has: one Access made, or one the table was given (<see cref="IndexDefinition"/>). Read from
/// a table (<see cref="Table.Indexes"/>) and never made by a caller.
/// <para>
/// Each of <see cref="Columns"/> is part of the index's key, in key order. The index is kept as the table's
/// rows change, and searched by <see cref="IndexCursor"/>, when its keys are ones this library writes.
/// </para>
/// </summary>
public sealed class Index
{
    /// <summary>Index name (e.g. "PrimaryKey", "ByLastName"); never null.</summary>
    public string Name { get; }

    /// <summary>Columns participating in the sort key, in declaration order.</summary>
    public IReadOnlyList<IndexColumn> Columns { get; }

    /// <summary>Page number of the index's root page (node or leaf).</summary>
    internal int RootPageNumber { get; }

    /// <summary>
    /// Ordinal of this index inside the TDEF (matches the index slot's
    /// <c>indexDataNumber</c>, which the foreign-key reference points at).
    /// </summary>
    internal int IndexNumber { get; }

    /// <summary>Raw index-flags byte from the index column block.</summary>
    internal byte Flags { get; }

    /// <summary>Index type byte from the logical-index slot (1 = primary key, 2 = foreign key, 0 = regular).</summary>
    internal byte IndexType { get; }

    /// <summary>
    /// The index's block among the TDEF's index blocks (its index data number):
    /// where its root page is recorded. Not the same as <see cref="IndexNumber"/>:
    /// MSysObjects lists Id first and keeps ParentIdName's block first.
    /// </summary>
    internal int IndexDataNumber { get; }

    /// <summary>The usage map listing the index's pages: its page (0 when it has none) and row.</summary>
    internal int UsedPagesUmapPage { get; }
    internal int UsedPagesUmapRow  { get; }

    /// <summary>Whether the index is the table's primary key.</summary>
    public bool IsPrimaryKey => IndexType == 1;
    /// <summary>Whether the index is one Access keeps for a relationship, on either of its tables.</summary>
    public bool IsForeignKey => IndexType == 2;
    /// <summary>Whether the index refuses a second row with the same values: a primary key always does.</summary>
    public bool IsUnique     => IsPrimaryKey || (Flags & 0x01) != 0;
    /// <summary>Whether the index holds no entry for a row whose index columns are all null.</summary>
    public bool IgnoreNulls  => (Flags & 0x02) != 0;
    /// <summary>Whether the index refuses a row with a null in any of its columns.</summary>
    public bool IsRequired   => (Flags & 0x08) != 0;

    internal Index(string name, IReadOnlyList<IndexColumn> columns, int rootPageNumber,
                   int indexNumber, byte flags, byte indexType,
                   int indexDataNumber = 0, int usedPagesUmapPage = 0, int usedPagesUmapRow = 0)
    {
        Name              = name;
        Columns           = columns;
        RootPageNumber    = rootPageNumber;
        IndexNumber       = indexNumber;
        Flags             = flags;
        IndexType         = indexType;
        IndexDataNumber   = indexDataNumber;
        UsedPagesUmapPage = usedPagesUmapPage;
        UsedPagesUmapRow  = usedPagesUmapRow;
    }

    /// <summary>The index as its name, kind, columns and root page.</summary>
    public override string ToString()
        => $"{Name} [{IndexType switch { 1 => "PK", 2 => "FK", _ => "IDX" }}] " +
           $"on ({string.Join(", ", Columns)}) root=p{RootPageNumber}";
}

/// <summary>
/// One column participating in an index's composite sort key.
/// </summary>
public sealed class IndexColumn
{
    /// <summary>The table column being indexed.</summary>
    public Column Column { get; }

    /// <summary>Raw flags byte from the index column block.</summary>
    internal byte Flags { get; }

    /// <summary>Whether the column sorts ascending in the index; <c>false</c> when it sorts descending.</summary>
    public bool IsAscending => (Flags & 0x01) != 0;

    internal IndexColumn(Column column, byte flags)
    {
        Column = column;
        Flags  = flags;
    }

    /// <summary>The column's name and the way it sorts.</summary>
    public override string ToString()
        => $"{Column.Name}{(IsAscending ? " ASC" : " DESC")}";
}
