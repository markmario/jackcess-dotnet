namespace JackcessDotNet;

/// <summary>
/// New names for what a database's schema already has (#37): a column, an index, a table. Jackcess renames none
/// of these, so Access alone is followed: each is written as DAO was seen to write the same change, in an .mdb
/// and an .accdb.
/// <para>
/// A column's name is changed in its table's definition, in the rows of MSysRelationships that name it on either
/// side of a relationship, and in the properties the catalog keeps for it. An index's name is changed in the
/// definition, and its slot moves to where the new name sorts among the table's, which is the order Access keeps
/// them in. A table's name is changed in its row of the catalog - whose index on names follows - and in the rows
/// of MSysRelationships that name it; its definition does not hold its name. Nothing else that may name them is
/// changed, by Access or here: a saved query goes on naming a column by the name it had.
/// </para>
/// <para>
/// A name is refused for what Access refuses one for - more than 64 characters, a period, an exclamation mark, a
/// grave accent, a square bracket or a control character in it, a space at its start, or one already taken - and
/// a name that differs from the old one only in the case of its letters is taken, as in Access. Each change is
/// all or nothing, as the changes <see cref="SchemaChange"/> makes are.
/// </para>
/// </summary>
internal static class SchemaRename
{
    private const string CatalogTable = "MSysObjects";

    /// <summary>Gives the column named <paramref name="name"/> of <paramref name="table"/> the name <paramref name="newName"/>.</summary>
    internal static void RenameColumn(Database db, PageFile file, TableDefinition table, string name, string newName)
    {
        file.EnsureWritable();
        SchemaRemoval.EnsureNotSystemTable(db, table.Name);
        SchemaChange.EnsureReadWhole(table);

        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Table '{table.Name}' has no column named '{name}'. Nothing was changed.", nameof(name));
        EnsureNewName(newName, "a column", nameof(newName));
        if (table.Columns.Any(c => c != column && string.Equals(c.Name, newName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Table '{table.Name}' already has a column named '{newName}'. Nothing was changed.", nameof(newName));
        if (column.DataType == DataType.Complex)
            throw new NotSupportedException(
                $"Column '{column.Name}' of '{table.Name}' is a complex one (attachments or several values), whose values are " +
                "in a table of their own: this library does not rename one. Nothing was changed.");
        if (column.Name == newName) return;

        var editor = TdefEditor.Load(file, table.TdefPageNumber, table.Name);
        int position = SchemaRemoval.PositionOf(editor.ColumnNames, column.Name);

        // The rows of MSysRelationships that name the column, on the side of the relationship it is on.
        var changes = new List<(int RowPointer, Row Change)>();
        Table? relationships = null;
        if (db.TryGetTable(SchemaChange.RelationshipsTable, out var found))
        {
            relationships = found;
            foreach (var (pointer, row) in SchemaRemoval.RowsOf(file, found, "szObject", "szColumn", "szReferencedObject", "szReferencedColumn"))
            {
                var change = new Row();
                if (Same(row["szObject"], table.Name) && Same(row["szColumn"], column.Name)) change["szColumn"] = newName;
                if (Same(row["szReferencedObject"], table.Name) && Same(row["szReferencedColumn"], column.Name)) change["szReferencedColumn"] = newName;
                if (change.Count > 0) changes.Add((pointer, change));
            }
            if (changes.Count > 0) SchemaRemoval.EnsureRowsCanBeChanged(found);
        }

        // The column's properties, where the catalog has some for it.
        var catalog = db.GetTable(CatalogTable);
        var entry = SchemaRemoval.RowsOf(file, catalog, "Id", "Type", "LvProp")
            .FirstOrDefault(r => r.Values["Id"] is int id && id == table.TdefPageNumber && r.Values["Type"] is short type && type == JetFormat.CatalogTypeTable);
        byte[]? properties = entry.Values is null ? null : PropertyBlob.WithColumnRenamed(entry.Values["LvProp"] as byte[], column.Name, newName);
        if (properties is not null) SchemaRemoval.EnsureRowsCanBeChanged(catalog);

        using (var undo = file.BeginUndo())
        {
            editor.RenameColumn(position, newName);
            TdefBuffer.Write(file, db.Allocator, table.TdefPageNumber, editor.ToDefinition());
            foreach (var (pointer, change) in changes) relationships!.RewriteRowAt(pointer, change);
            if (properties is not null) catalog.RewriteRowAt(entry.RowPointer, new Row { ["LvProp"] = properties });
            undo.Commit();
        }
        SchemaChange.Changed(db, file, table.TdefPageNumber);
    }

    /// <summary>Gives the index named <paramref name="name"/> of <paramref name="table"/> the name <paramref name="newName"/>.</summary>
    internal static void RenameIndex(Database db, PageFile file, TableDefinition table, string name, string newName)
    {
        file.EnsureWritable();
        SchemaRemoval.EnsureNotSystemTable(db, table.Name);
        SchemaChange.EnsureReadWhole(table);

        var index = table.Indexes.FirstOrDefault(ix => string.Equals(ix.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Table '{table.Name}' has no index named '{name}'. Nothing was changed.", nameof(name));
        EnsureNewName(newName, "an index", nameof(newName));
        if (table.Indexes.Any(ix => ix != index && string.Equals(ix.Name, newName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Table '{table.Name}' already has an index named '{newName}'. Nothing was changed.", nameof(newName));
        if (index.IsForeignKey)
            throw new InvalidOperationException(
                $"Index '{index.Name}' of '{table.Name}' is kept for a relationship, and has the name Access gave it for that. Nothing was changed.");
        if (index.Name == newName) return;

        var editor = TdefEditor.Load(file, table.TdefPageNumber, table.Name);
        int position = SchemaRemoval.PositionOf(editor.SlotNames, index.Name);
        var others = editor.SlotNames.Where((_, i) => i != position).ToList();
        int newPosition = SchemaChange.SlotPosition(others, newName, db.Catalog.TextSortOrder);

        using (var undo = file.BeginUndo())
        {
            editor.MoveIndexSlot(position, newPosition, newName);
            TdefBuffer.Write(file, db.Allocator, table.TdefPageNumber, editor.ToDefinition());
            undo.Commit();
        }
        SchemaChange.Changed(db, file, table.TdefPageNumber);
    }

    /// <summary>
    /// Gives the table named <paramref name="name"/> the name <paramref name="newName"/>, and returns the page of
    /// its definition, which is what the table is known by.
    /// </summary>
    internal static int RenameTable(Database db, PageFile file, string name, string newName)
    {
        file.EnsureWritable();
        if (!db.TryGetTable(name, out var table))
            throw new ArgumentException($"Table '{name}' was not found in this database. Nothing was changed.", nameof(name));
        string oldName = table.Name;
        int tdefPage   = table.Definition.TdefPageNumber;
        SchemaRemoval.EnsureNotSystemTable(db, oldName);
        EnsureNewName(newName, "a table", nameof(newName));
        if (oldName == newName) return tdefPage;

        // A name that is another object's is refused, and one no catalog key is made for: what refuses a new table.
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            try { db.Catalog.EnsureCanRegister(newName); }
            catch (InvalidOperationException e) { throw new ArgumentException(e.Message + " Nothing was changed.", nameof(newName)); }
        }

        var catalog = db.GetTable(CatalogTable);
        var entry = SchemaRemoval.RowsOf(file, catalog, "Id", "Type")
            .FirstOrDefault(r => r.Values["Id"] is int id && id == tdefPage && r.Values["Type"] is short type && type == JetFormat.CatalogTypeTable);
        if (entry.Values is null)
            throw new NotSupportedException($"Table '{oldName}' is not renamed: the catalog has no entry of a table for it. Nothing was changed.");
        SchemaRemoval.EnsureRowsCanBeChanged(catalog);

        var changes = new List<(int RowPointer, Row Change)>();
        Table? relationships = null;
        if (db.TryGetTable(SchemaChange.RelationshipsTable, out var found))
        {
            relationships = found;
            foreach (var (pointer, row) in SchemaRemoval.RowsOf(file, found, "szObject", "szReferencedObject"))
            {
                var change = new Row();
                if (Same(row["szObject"], oldName)) change["szObject"] = newName;
                if (Same(row["szReferencedObject"], oldName)) change["szReferencedObject"] = newName;
                if (change.Count > 0) changes.Add((pointer, change));
            }
            if (changes.Count > 0) SchemaRemoval.EnsureRowsCanBeChanged(found);
        }

        using (var undo = file.BeginUndo())
        {
            catalog.RewriteRowAt(entry.RowPointer, new Row { ["Name"] = newName, ["DateUpdate"] = DateTime.Now });
            foreach (var (pointer, change) in changes) relationships!.RewriteRowAt(pointer, change);
            undo.Commit();
        }
        db.SchemaChanged();
        return tdefPage;
    }

    private static bool Same(object? value, string name) => string.Equals(value as string, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Refuses a name Access would not take for a column, an index or a table.</summary>
    private static void EnsureNewName(string newName, string ofWhat, string parameter)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Length > SchemaChange.MaxNameLength)
            throw new ArgumentException(
                $"The name of {ofWhat} is 1 to {SchemaChange.MaxNameLength} characters; '{newName}' is not. Nothing was changed.", parameter);
        SchemaChange.EnsureValidName(newName, ofWhat, parameter);
    }
}
