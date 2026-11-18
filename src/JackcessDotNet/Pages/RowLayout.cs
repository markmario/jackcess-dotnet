namespace JackcessDotNet;

/// <summary>
/// Where each column of a table is kept in a row: what <see cref="RowEncoder"/> writes by and
/// <see cref="RowDecoder"/> reads by, so the two cannot differ.
/// <para>
/// A table's definition says it, column by column: whether the value is among the row's fixed-length values or its
/// variable-length ones (<see cref="Column.IsVariableLength"/>, a flag of the column, not a property of its type),
/// its offset among the first (<see cref="Column.FixedDataOffset"/>) or its place in the table of offsets of the
/// second (<see cref="Column.VarLenTableIndex"/>), and its number (<see cref="Column.ColumnNumber"/>), which is its
/// bit in the row's null mask. None of these can be worked out from the columns in order. Access leaves a deleted
/// column's number, its room among the fixed-length values and its place among the variable-length ones behind, and
/// gives a later column the room; it gives a Yes/No column, whose value is its null-mask bit, no room at all; and
/// it keeps a Long, a Date/Time, a Replication ID or a Decimal among the variable-length values, and Text or Binary
/// among the fixed-length ones (SQL's CHAR and BINARY), when the column's flag says so.
/// </para>
/// <para>
/// Columns that have not been through a definition yet (a list given to <see cref="RowEncoder"/> by hand) have no
/// place of their own: they are laid out in column-number order, fixed-length values one after another, each taking
/// its length, as <see cref="TableDefinition.Serialize"/> lays out a new table.
/// </para>
/// </summary>
internal sealed class RowLayout
{
    private readonly Dictionary<Column, Place> _places = new();

    /// <summary>Where one column is kept.</summary>
    internal readonly record struct Place(bool IsVariable, int FixedOffset, int VarIndex, int FixedSize);

    /// <summary>The columns kept among the fixed-length values, the Yes/No columns among them.</summary>
    public IReadOnlyList<Column> FixedColumns { get; }

    /// <summary>The columns kept among the variable-length values, in the order of their offsets.</summary>
    public IReadOnlyList<Column> VariableColumns { get; }

    /// <summary>The column count a row is written with: one more than the highest column number the table has
    /// ever had, deleted columns included.</summary>
    public int MaxColumnCount { get; }

    /// <summary>The number of offsets a row's variable-length values are written with, deleted columns' included.</summary>
    public int MaxVarColumnCount { get; }

    /// <summary>The bytes the fixed-length values take: to the end of the one that ends last.</summary>
    public int FixedDataSize { get; }

    /// <summary>
    /// Why a row cannot be written by this layout, or null when it can: a definition that puts a column outside the
    /// row, or two columns in one place, is not one to write rows by.
    /// </summary>
    public string? WhyNotWritable { get; }

    public RowLayout(JetFormat format, IReadOnlyList<Column> columns, int? maxColumnCount = null, int? maxVarColumnCount = null)
    {
        bool stored = columns.Count > 0 && columns.All(c => c.HasStoredPlace);
        var fixedColumns = new List<Column>();
        var variable     = new List<Column>();

        int packedOffset = 0, packedIndex = 0;
        foreach (var column in columns.OrderBy(c => c.ColumnNumber))
        {
            bool isVariable = stored ? column.IsVariableLength : column.DataType.IsVariableLength();
            int  size       = isVariable ? 0 : FixedSizeOf(column);
            if (isVariable)
            {
                _places[column] = new Place(true, -1, stored ? column.VarLenTableIndex : packedIndex++, 0);
                variable.Add(column);
            }
            else
            {
                _places[column] = new Place(false, stored ? column.FixedDataOffset : packedOffset, -1, size);
                // A new table's Yes/No column is given a byte it never uses, as this library always has.
                packedOffset += column.Length;
                fixedColumns.Add(column);
            }
        }

        FixedColumns    = fixedColumns;
        VariableColumns = variable.OrderBy(c => _places[c].VarIndex).ToList();
        // A Yes/No column takes no room, wherever the definition says it is.
        FixedDataSize   = fixedColumns.Where(c => _places[c].FixedSize > 0).Select(c => _places[c].FixedOffset + _places[c].FixedSize).DefaultIfEmpty(0).Max();
        MaxColumnCount  = Math.Max(maxColumnCount ?? 0, columns.Count == 0 ? 0 : columns.Max(c => c.ColumnNumber) + 1);
        MaxVarColumnCount = Math.Max(maxVarColumnCount ?? 0, variable.Count == 0 ? 0 : variable.Max(c => _places[c].VarIndex) + 1);

        WhyNotWritable = FindFault(format, columns);
    }

    public RowLayout(JetFormat format, TableDefinition table)
        : this(format, table.Columns, table.MaxColumnCount, table.MaxVarColumnCount) { }

    /// <summary>Where <paramref name="column"/> is kept, or null for a column that is not one of the table's.</summary>
    public Place? PlaceOf(Column column)
    {
        if (_places.TryGetValue(column, out var place)) return place;
        // A column object from another reading of the same definition: found by its number.
        foreach (var known in _places)
            if (known.Key.ColumnNumber == column.ColumnNumber && known.Key.DataType == column.DataType)
                return known.Value;
        return null;
    }

    /// <summary>
    /// The bytes a column takes among the fixed-length values: none for a Yes/No column, whose value is its bit in
    /// the null mask; its type's size; and for Text or Binary kept there, the column's length.
    /// </summary>
    private static int FixedSizeOf(Column column)
    {
        if (column.DataType == DataType.Boolean) return 0;
        int size = column.DataType.GetFixedSize();
        return size > 0 ? size : Math.Max(column.Length, 0);
    }

    private string? FindFault(JetFormat format, IReadOnlyList<Column> columns)
    {
        var numbers = new HashSet<int>();
        foreach (var column in columns)
            if (column.ColumnNumber < 0 || !numbers.Add(column.ColumnNumber))
                return $"its definition gives the column number {column.ColumnNumber} to more than one column, or to none";

        var indexes = new HashSet<int>();
        foreach (var column in VariableColumns)
        {
            if (_places[column].VarIndex < 0 || !indexes.Add(_places[column].VarIndex))
                return $"its definition gives {column.Name} a place among the variable-length values that another column has";
            if (column.DataType == DataType.Boolean)
                return $"its definition keeps the {column.DataType} column {column.Name} among the variable-length values";
        }

        // Fixed-length values may not overlap: two columns in one place cannot both be written. (A Yes/No column
        // takes no room, so it overlaps nothing.)
        var taken = FixedColumns.Select(c => _places[c]).Where(p => p.FixedSize > 0).OrderBy(p => p.FixedOffset).ToList();
        for (int i = 0; i < taken.Count; i++)
        {
            if (taken[i].FixedOffset < 0 || taken[i].FixedOffset + taken[i].FixedSize > format.MaxRowSize)
                return "its definition puts a fixed-length column outside the row";
            if (i > 0 && taken[i].FixedOffset < taken[i - 1].FixedOffset + taken[i - 1].FixedSize)
                return "its definition puts two fixed-length columns in the same place";
        }
        foreach (var column in FixedColumns)
            if (column.DataType.IsLongValue())
                return $"its definition keeps the {column.DataType} column {column.Name} among the fixed-length values";
        return null;
    }
}
