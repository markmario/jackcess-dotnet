namespace JackcessDotNet.Tests;

/// <summary>
/// The definitions a caller writes to make a table with the columns another table has: the name, type and
/// field properties of each, as <see cref="Column"/> reports them.
/// </summary>
internal static class ColumnDefinitions
{
    public static ColumnDefinition Of(Column column)
        => new(column.Name, column.DataType)
        {
            MaxLength       = column.DataType is DataType.Text or DataType.Binary ? column.MaxLength : null,
            IsRequired      = column.IsRequired,
            IsAutoNumber    = column.IsAutoNumber,
            AllowZeroLength = column.AllowZeroLength,
            Precision       = column.Precision,
            Scale           = column.Scale,
        };

    public static List<ColumnDefinition> Of(IEnumerable<Column> columns) => columns.Select(Of).ToList();
}
