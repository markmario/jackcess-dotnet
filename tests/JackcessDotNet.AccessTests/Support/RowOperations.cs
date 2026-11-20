using System;
using System.Linq;
using JackcessDotNet;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// How these tests name a row of the engine's to change by a value it has, now that a table changes a row by
    /// where it was read (<see cref="Table.Update"/>, <see cref="Table.Delete"/>): each helper finds the row with a
    /// cursor and changes it there, through the public API a caller has. They keep the names of the two members
    /// <see cref="Table"/> had before, so that a test written against those reads as it did.
    /// </summary>
    internal static class RowOperations
    {
        /// <summary>
        /// Gives the row whose primary key is <paramref name="primaryKeyValue"/> the values in
        /// <paramref name="newValues"/>; a column they do not name keeps its value. Only the key's column is read to
        /// find the row.
        /// </summary>
        /// <exception cref="InvalidOperationException">The table has no primary key, or no row has that key.</exception>
        public static void UpdateByPrimaryKey(this Table table, object primaryKeyValue, Row newValues)
        {
            var cursor = table.CreateIndexCursor();
            cursor.ColumnNames = cursor.Index.Columns.Select(c => c.Column.Name).ToArray();
            if (cursor.FindRowByEntry(primaryKeyValue) == null)
                throw new InvalidOperationException("No row with primary key '" + primaryKeyValue + "' found in table '" + table.Name + "'.");
            cursor.UpdateCurrentRow(newValues);
        }

        /// <summary>Deletes the first row, in the order the table stores its rows, whose <paramref name="columnName"/> is <paramref name="value"/>.</summary>
        /// <exception cref="ArgumentException">The table has no column of that name.</exception>
        /// <exception cref="InvalidOperationException">No row has the value in that column.</exception>
        public static void DeleteRow(this Table table, string columnName, object value)
        {
            var cursor = table.CreateCursor();
            cursor.ColumnNames = new[] { columnName };
            if (cursor.FindRow(columnName, value) == null)
                throw new InvalidOperationException("No row with " + columnName + " = '" + value + "' found in table '" + table.Name + "'.");
            cursor.DeleteCurrentRow();
        }
    }
}
