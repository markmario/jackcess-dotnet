using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Changes to a database's schema after its tables are made (qcatco/exportmdb.interop#37): a column added to a
/// table, an index added to a table and built over its rows, and a relationship created between two tables - what
/// Jackcess's <c>TableUpdater</c> and <c>RelationshipCreator</c> do, written to the file as Access was seen to
/// write the same changes.
/// <para>
/// Each change is all or nothing. What can refuse it - a name taken, a type not written, rows that are duplicates
/// in a unique index or refer to no parent, a definition with no room left on its page - is found before anything
/// is written, and what is written is written inside <see cref="PageFile.BeginUndo"/>, so a change that fails
/// part-way is taken back. Either way the file is byte for byte what it was.
/// </para>
/// <para>
/// What differs from Access, deliberately. A new index's pages, and a new long-value column's, are listed in usage
/// maps on a page of their own (as for a table this library creates), where Access finds room on the table's
/// usage-map page first. A column added is never filled in for the rows already there, so an AutoNumber column is
/// added only to a table with no rows - Access numbers the rows it has. And a definition is never let run onto a
/// second page (<see cref="TdefEditor"/>).
/// </para>
/// </summary>
internal static class SchemaChange
{
    private const int MaxNameLength = 64;
    private const int MaxColumns    = 255;

    // ── A column ─────────────────────────────────────────────────────────────

    /// <summary>Adds a copy of <paramref name="given"/> to <paramref name="table"/> and returns its name.</summary>
    internal static string AddColumn(Database db, PageFile file, TableDefinition table, Column given)
    {
        file.EnsureWritable();
        var format = file.Format;
        EnsureReadWhole(table);

        if (string.IsNullOrWhiteSpace(given.Name) || given.Name.Length > MaxNameLength)
            throw new ArgumentException($"A column's name is 1 to {MaxNameLength} characters; '{given.Name}' is not.", nameof(given));
        if (table.Columns.Any(c => string.Equals(c.Name, given.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Table '{table.Name}' already has a column named '{given.Name}'. Nothing was changed.", nameof(given));
        if (table.Columns.Count >= MaxColumns)
            throw new ArgumentException($"Table '{table.Name}' has {MaxColumns} columns, the most a table has. Nothing was changed.", nameof(given));
        if (given.DataType == DataType.Complex || given.IsCalculated || !Enum.IsDefined(typeof(DataType), given.DataType))
            throw new NotSupportedException(
                $"A column of type {given.DataType}{(given.IsCalculated ? " that is calculated" : "")} is not added: this library does not write one. Nothing was changed.");

        byte[] page = file.ReadPage(table.TdefPageNumber);
        if (given.IsAutoNumber)
        {
            // Access numbers the rows a table has when it is given an AutoNumber; nothing here rewrites a row.
            if (ByteUtil.GetInt(page, format.TdefOffsetNumRows) != 0)
                throw new NotSupportedException(
                    $"An AutoNumber column is not added to '{table.Name}', which has rows: Access would number them, and this " +
                    "library rewrites no row when it adds a column. Nothing was changed.");
            if (table.Columns.Any(c => c.IsAutoNumber))
                throw new NotSupportedException($"Table '{table.Name}' already has an AutoNumber column. Nothing was changed.");
        }

        var editor = new TdefEditor(page, format, table.Name);
        bool isLong = given.DataType.IsLongValue();
        editor.EnsureRoomFor(editor.SizeOfColumn(given.Name, isLong));

        // The table's own column, a copy of the caller's: numbered after every column the table has ever had, so
        // that no row written before it has its bit in a null mask, and placed after the values those rows hold.
        var column = given.CopyFor(db.Catalog.TextSortOrder);
        bool isVariable = column.DataType.IsVariableLength();
        int  maxVar     = editor.MaxVarColumnCount;
        column.ColumnNumber     = editor.MaxColumnCount;
        column.IsVariableLength = isVariable;
        column.HasStoredPlace   = true;
        if (isVariable)
        {
            column.VarLenTableIndex = (short)maxVar;
            column.FixedDataOffset  = -1;
        }
        else
        {
            column.VarLenTableIndex = -1;
            // A Yes/No column takes no room: its value is its bit in the null mask, and Access gives it offset 0.
            column.FixedDataOffset  = column.DataType == DataType.Boolean ? (short)0 : (short)new RowLayout(format, table).FixedDataSize;
        }

        // Rows must still be writable by the table's columns with this one among them.
        var layout = new RowLayout(format, table.Columns.Append(column).ToList(), column.ColumnNumber + 1, maxVar + (isVariable ? 1 : 0));
        if (layout.WhyNotWritable is { } why)
            throw new NotSupportedException($"A column is not added to '{table.Name}': {why}. Nothing was changed.");

        int columnId = editor.ColumnIds.DefaultIfEmpty(-1).Max() + 1;
        using (var undo = file.BeginUndo())
        {
            LvalUmapRef? maps = null;
            if (isLong)
            {
                int umapPage = db.Allocator.AllocateUmapPage();
                maps = new LvalUmapRef(umapPage, 0, umapPage, 1);
            }
            editor.AddColumn(column, columnId, maps);
            file.WritePage(table.TdefPageNumber, editor.ToPage());
            undo.Commit();
        }
        Changed(db, file, table.TdefPageNumber);
        return column.Name;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses to change a table whose definition was not read whole - it runs past its first page - since what
    /// would be written back is only what was read.
    /// </summary>
    private static void EnsureReadWhole(TableDefinition table)
    {
        if (table.HasUnreadColumns || table.HasUnreadIndexes)
            throw new NotSupportedException(
                $"Table '{table.Name}' is not changed: its definition runs over more than one page, or names an index column " +
                "or block it has not, and this library reads and writes a definition of one page only. Nothing was changed.");
    }

    private static Column ColumnOf(TableDefinition table, string name, string what)
        => table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{what} column '{name}' was not found in table '{table.Name}'. Nothing was changed.");

    /// <summary>
    /// After a change is written: every <see cref="Table"/> open on the table reads its definition again, and the
    /// relationships are read again by whatever keeps them.
    /// </summary>
    private static void Changed(Database db, PageFile file, int tdefPage)
    {
        file.DefinitionChanged(tdefPage);
        db.SchemaChanged();
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++)
            if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        return a.Length.CompareTo(b.Length);
    }

    // A value as a refusal shows it: whole when short, its beginning when long.
    private static string Shown(object? value)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        if (text.Length > 40) text = text.Substring(0, 40) + "...";
        return value is string ? $"'{text}'" : value is null ? "null" : text;
    }
}
