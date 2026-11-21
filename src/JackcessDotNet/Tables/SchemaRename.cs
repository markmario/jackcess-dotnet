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

        var column = table.FindColumn(name)
            ?? throw new ArgumentException($"Table '{table.Name}' has no column named '{name}'. Nothing was changed.", nameof(name));
        EnsureNewName(newName, "a column", nameof(newName));
        SchemaChange.EnsureNewName(newName, "column", nameof(newName));
        db.EnsureNamesCompared($"Column '{column.Name}' of '{table.Name}' was not renamed", newName, table.Columns.Where(c => c != column).Select(c => c.Name));
        // The new name is compared with every other column's: the column is left out by what it is, so it may be given
        // a name Access takes as one with its own (another case, say), as Access renames one.
        if (db.ColumnNames.OneWith(table.Columns.Where(c => c != column).Select(c => c.Name), newName) is { } taken)
            throw new ArgumentException($"Table '{table.Name}' already has a column named {ObjectNames.Taken(taken, newName)}. Nothing was changed.", nameof(newName));
        if (column.DataType == DataType.Complex)
            throw new NotSupportedException(
                $"Column '{column.Name}' of '{table.Name}' is a complex one (attachments or several values), whose values are " +
                "in a table of their own: this library does not rename one. Nothing was changed.");
        if (column.Name == newName) return;

        var editor = TdefEditor.Load(file, table.TdefPageNumber, table.Name);
        int position = SchemaRemoval.PositionOf(editor.ColumnNames, column.Name);

        // The rows of MSysRelationships that name the column, on the side of the relationship it is on: those whose
        // table and column names a lookup finds this table and this column by.
        var changes = new List<(int RowPointer, Row Change)>();
        Table? relationships = null;
        if (db.TryGetTable(SchemaChange.RelationshipsTable, out var found))
        {
            relationships = found;
            var names = TableLookup(db);
            bool NamesColumn(object? named) => named is string columnName && table.FindColumn(columnName) == column;
            foreach (var (pointer, row) in SchemaRemoval.RowsOf(file, found, "szObject", "szColumn", "szReferencedObject", "szReferencedColumn"))
            {
                var change = new Row();
                if (names(row["szObject"]) == table.Name && NamesColumn(row["szColumn"])) change["szColumn"] = newName;
                if (names(row["szReferencedObject"]) == table.Name && NamesColumn(row["szReferencedColumn"])) change["szReferencedColumn"] = newName;
                if (change.Count > 0) changes.Add((pointer, change));
            }
            if (changes.Count > 0) SchemaRemoval.EnsureRowsCanBeChanged(found);
        }

        // The column's properties, where the catalog has some for it: asked before anything is written.
        bool hasProperties = db.ColumnHasPropertiesToChange(table, column.Name, "renamed");

        using (var undo = file.BeginUndo())
        {
            editor.RenameColumn(position, newName);
            TdefBuffer.Write(file, db.Allocator, table.TdefPageNumber, editor.ToDefinition());
            foreach (var (pointer, change) in changes) relationships!.RewriteRowAt(pointer, change);
            if (hasProperties) db.ChangeColumnProperties(table, column.Name, newName);
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

        var index = table.FindIndex(name)
            ?? throw new ArgumentException($"Table '{table.Name}' has no index named '{name}'. Nothing was changed.", nameof(name));
        EnsureNewName(newName, "an index", nameof(newName));
        SchemaChange.EnsureNewName(newName, "index", nameof(newName));
        db.EnsureNamesCompared($"Index '{index.Name}' of '{table.Name}' was not renamed", newName, table.Indexes.Where(ix => ix != index).Select(ix => ix.Name));
        if (db.ColumnNames.OneWith(table.Indexes.Where(ix => ix != index).Select(ix => ix.Name), newName) is { } taken)
            throw new ArgumentException($"Table '{table.Name}' already has an index named {ObjectNames.Taken(taken, newName)}. Nothing was changed.", nameof(newName));
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
        SchemaChange.EnsureNewName(newName, "table", nameof(newName));
        if (oldName == newName) return tdefPage;

        // A name another object has is refused - one Access takes as one with it (ObjectNames) - and one no catalog key
        // is made for: what refuses a new table. Every object but this table is compared, and the table is left out by
        // what it is - its definition's page - not by its name: a file can hold two names Access takes as one, and a
        // table renamed to the name of another would be the other's twin.
        try { db.Catalog.EnsureCanRename(tdefPage, newName); }
        catch (InvalidOperationException e) { throw new ArgumentException(e.Message + " Nothing was changed.", nameof(newName)); }

        var catalog = db.GetTable(CatalogTable);
        var catalogRows = SchemaRemoval.RowsOf(file, catalog, "Id", "Type", "Name");
        var entry = catalogRows
            .FirstOrDefault(r => r.Values["Id"] is int id && id == tdefPage && r.Values["Type"] is short type && type == JetFormat.CatalogTypeTable);
        if (entry.Values is null)
            throw new NotSupportedException($"Table '{oldName}' is not renamed: the catalog has no entry of a table for it. Nothing was changed.");
        // The name of a table an earlier version of this library created is read by its bytes (#121): one that begins
        // with U+FEFF is written as such text is in the table's rows, compressed in runs, and refused as it is there
        // where it would read back changed (#129).
        if (db.Catalog.IsAnEarlierVersionsTable(tdefPage)
            && catalog.Columns.FirstOrDefault(c => c.IsCatalogName) is { } nameColumn
            && RowEncoder.WhyNotKept(nameColumn, newName, _ => true, byContent: true) is { } why)
            throw new ArgumentException($"Table '{oldName}' was not renamed. {why}. Nothing was changed.", nameof(newName));
        SchemaRemoval.EnsureRowsCanBeChanged(catalog);

        // The rows of MSysRelationships that name the table: those whose name a lookup among the catalog's tables finds
        // this table by - its name as the catalog has it, or else one Access takes as one with it that is no other
        // table's - never a table whose name is only one with this one's.
        var lookup = TableLookup(db);
        bool NamesTable(object? value) => lookup(value) == oldName;

        var changes = new List<(int RowPointer, Row Change)>();
        Table? relationships = null;
        if (db.TryGetTable(SchemaChange.RelationshipsTable, out var found))
        {
            relationships = found;
            foreach (var (pointer, row) in SchemaRemoval.RowsOf(file, found, "szObject", "szReferencedObject"))
            {
                var change = new Row();
                if (NamesTable(row["szObject"])) change["szObject"] = newName;
                if (NamesTable(row["szReferencedObject"])) change["szReferencedObject"] = newName;
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

    /// <summary>
    /// What a lookup of a name among the catalog's tables finds, as the catalog spells it - the table of exactly that
    /// name, or else the first whose name Access takes as one with it (<see cref="ObjectNames.Find"/>) - or null for a
    /// name no table has, or no name.
    /// </summary>
    private static Func<object?, string?> TableLookup(Database db)
    {
        var tables = db.Catalog.GetTableNames(includeSystem: true);
        var names  = db.Catalog.Names;
        var found  = new Dictionary<string, string?>(StringComparer.Ordinal);
        return value =>
        {
            if (value is not string name) return null;
            if (!found.TryGetValue(name, out var listed))
            {
                int at = names.Find(tables, name);
                found[name] = listed = at < 0 ? null : tables[at];
            }
            return listed;
        };
    }

    /// <summary>Refuses a name Access would not take for a column, an index or a table.</summary>
    private static void EnsureNewName(string newName, string ofWhat, string parameter)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Length > SchemaChange.MaxNameLength)
            throw new ArgumentException(
                $"The name of {ofWhat} is 1 to {SchemaChange.MaxNameLength} characters; '{newName}' is not. Nothing was changed.", parameter);
        SchemaChange.EnsureValidName(newName, ofWhat, parameter);
    }
}
