namespace JackcessDotNet;

/// <summary>
/// The properties a column is given when this library makes it, which is where Access keeps what a
/// <see cref="ColumnDefinition"/> asks of a column beyond its type: nothing of it is in the table's definition.
/// They are the ones DAO gives a field it makes (#41): every column but an AutoNumber has <c>Required</c>, and a
/// Text or Memo column <c>AllowZeroLength</c> before it - Yes/No values, marked as DDL properties. An AutoNumber
/// has none.
/// </summary>
internal static class ColumnProperties
{
    /// <summary>Gives <paramref name="maps"/> the properties of <paramref name="column"/>, a column being made.</summary>
    internal static void Add(PropertyMaps maps, Column column)
    {
        if (column.IsAutoNumber) return;

        var map = maps.GetOrAdd(column.Name, PropertyMap.ColumnType);
        if (column.DataType is DataType.Text or DataType.Memo)
            map.Set("AllowZeroLength", column.AllowZeroLength);
        map.Set("Required", column.IsRequired);
    }

    /// <summary>The properties of a table being made with <paramref name="columns"/>.</summary>
    internal static PropertyMaps For(IEnumerable<Column> columns)
    {
        var maps = new PropertyMaps();
        foreach (var column in columns)
            Add(maps, column);
        return maps;
    }
}
