using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// What is taken out of a database's schema (#37): an index, a column, a relationship, a table. Jackcess drops
/// none of these, so Access alone is followed: each is written as DAO was seen to write the same change, in an
/// .mdb and an .accdb.
/// <para>
/// An index: its slot leaves the table's definition, and when no other index shares its tree, so does its block -
/// the blocks after it take its number, as every slot that names one is told - the tree's pages are given back to
/// the file and its usage map is taken off its page. A column: its definition, its name and the entry for a long
/// value's usage maps leave the definition, with the column's properties in the catalog; the rows are not
/// rewritten, and the other columns keep their numbers and places, so the rows go on being read as they were. A
/// relationship: its rows in MSysRelationships, its entry in the catalog and its permissions go, and with
/// referential integrity its index on each table, as an index goes. A table: the relationships it refers by, its
/// catalog entry and permissions, and every page it has - its definition's, whose first page is marked no
/// definition, its rows', its indexes', its long values' and its usage maps' - go back to the file.
/// </para>
/// <para>
/// What Access refuses is refused: an index a relationship refers to or is kept by, a column an index or a
/// relationship is on, a table another table refers to. Each change is all or nothing, as the changes
/// <see cref="SchemaChange"/> makes are: what refuses it is found before anything is written, and what is written
/// is inside <see cref="PageFile.BeginUndo"/>.
/// </para>
/// <para>
/// A page given back keeps its bytes, as Access leaves them, and is free in the database's global usage map:
/// Access hands it out again. This library adds pages at the end of the file and does not use one again itself.
/// </para>
/// </summary>
internal static class SchemaRemoval
{
    private const string CatalogTable = "MSysObjects";
    private const string AcesTable    = "MSysACEs";
    private const short  CatalogTypeRelationship = 8;

    // ── An index ─────────────────────────────────────────────────────────────

    /// <summary>Drops the index named <paramref name="name"/> from <paramref name="table"/>.</summary>
    internal static void DropIndex(Database db, PageFile file, TableDefinition table, string name)
    {
        file.EnsureWritable();
        EnsureNotSystemTable(db, table.Name);
        SchemaChange.EnsureReadWhole(table);

        var index = table.Indexes.FirstOrDefault(ix => string.Equals(ix.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Table '{table.Name}' has no index named '{name}'. Nothing was changed.", nameof(name));
        if (index.IsForeignKey)
            throw new InvalidOperationException(
                $"Index '{index.Name}' of '{table.Name}' is kept for a relationship: drop the relationship, and it goes with it. Nothing was changed.");

        var editor = TdefEditor.Load(file, table.TdefPageNumber, table.Name);
        int position = PositionOf(editor.SlotNames, index.Name);
        int block    = editor.SlotBlocks[position];

        // A relationship refers to the rows of this table through the tree of the unique index on the columns it
        // names, which its own index on this table shares: Access refuses to drop that index ("It is either the
        // current index or is used in a relationship").
        for (int i = 0; i < editor.SlotCount; i++)
        {
            var reference = editor.SlotReference(i);
            if (i != position && editor.SlotBlocks[i] == block
                && reference.IndexType == TdefEditor.IndexTypeForeignKey && reference.Side == TdefEditor.ForeignKeyPrimaryTable)
                throw new InvalidOperationException(
                    $"Index '{index.Name}' of '{table.Name}' is the one a relationship refers to the table's rows by: drop the " +
                    "relationship first. Nothing was changed.");
        }

        using (var undo = file.BeginUndo())
        {
            RemoveSlot(file, editor, position);
            TdefBuffer.Write(file, db.Allocator, table.TdefPageNumber, editor.ToDefinition());
            undo.Commit();
        }
        Dropped(db, file, table.TdefPageNumber);
    }

    /// <summary>
    /// Takes the slot at <paramref name="position"/> out of a definition and, when no other slot shares its
    /// block, the block too: its tree's pages go back to the file and its usage map off its page. The definition
    /// is the caller's to write, and the undo scope the caller's to hold.
    /// </summary>
    private static void RemoveSlot(PageFile file, TdefEditor editor, int position)
    {
        int block = editor.SlotBlocks[position];
        editor.RemoveIndexSlot(position);
        if (editor.SlotBlocks.Contains(block)) return;

        var pages = editor.BlockPages(block);
        editor.RemoveIndexData(block);
        if (pages.UmapPage > 0) UsageMap.Free(file, pages.UmapPage, pages.UmapRow);
        if (pages.RootPage > 0) GlobalUsageMap.MarkFree(file, pages.RootPage);
    }

    // ── A column ─────────────────────────────────────────────────────────────

    /// <summary>Drops the column named <paramref name="name"/> from <paramref name="table"/>.</summary>
    internal static void DropColumn(Database db, PageFile file, TableDefinition table, string name)
    {
        file.EnsureWritable();
        EnsureNotSystemTable(db, table.Name);
        SchemaChange.EnsureReadWhole(table);

        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Table '{table.Name}' has no column named '{name}'. Nothing was changed.", nameof(name));
        if (table.Columns.Count == 1)
            throw new NotSupportedException(
                $"Column '{column.Name}' is the only one '{table.Name}' has: this library leaves no table without a column. Nothing was changed.");
        if (column.DataType == DataType.Complex)
            throw new NotSupportedException(
                $"Column '{column.Name}' of '{table.Name}' is a complex one (attachments or several values), whose values are " +
                "in a table of their own: this library does not drop one. Nothing was changed.");

        // Access: "Cannot delete a field that is part of an index or is needed by the system."
        if (table.Indexes.FirstOrDefault(ix => ix.Columns.Any(c => c.Column.Ordinal == column.Ordinal)) is { } index)
            throw new InvalidOperationException(
                $"Column '{column.Name}' of '{table.Name}' is part of the index '{index.Name}': drop the index first. Nothing was changed.");
        // Access: "Cannot delete field. It is part of one or more relationships." - with or without referential integrity.
        if (RelationshipOn(db, table.Name, column.Name) is { } relationship)
            throw new InvalidOperationException(
                $"Column '{column.Name}' of '{table.Name}' is part of the relationship '{relationship}': drop the relationship first. " +
                "Nothing was changed.");

        var editor = TdefEditor.Load(file, table.TdefPageNumber, table.Name);
        int position = PositionOf(editor.ColumnNames, column.Name);
        if (editor.ColumnNumbers[position] != column.Ordinal)
            throw new NotSupportedException($"Table '{table.Name}' is not changed: its definition is not laid out as it was read. Nothing was changed.");

        bool hasLongValues = table.LvalColumnUmapPages.TryGetValue(column.Name, out var longValueMaps);

        // The column's properties, where the catalog has some for it: asked before anything is written.
        bool hasProperties = db.ColumnHasPropertiesToChange(table, column.Name, "dropped");

        using (var undo = file.BeginUndo())
        {
            editor.RemoveColumn(position);
            TdefBuffer.Write(file, db.Allocator, table.TdefPageNumber, editor.ToDefinition());

            if (hasLongValues)
            {
                // Every page of its long values goes back to the file; then the two maps' rows, the one that
                // lists the pages with room on them after the one that lists them all.
                UsageMap.Free(file, longValueMaps.OwnedPage, longValueMaps.OwnedRow);
                if (longValueMaps.FreePage > 0 && longValueMaps.FreePage < file.PageCount
                    && UsageMap.DeleteRow(file, longValueMaps.FreePage, longValueMaps.FreeRow))
                    GlobalUsageMap.MarkFree(file, longValueMaps.FreePage);
                file.FreedLvalPages.Clear();
            }
            if (hasProperties) db.ChangeColumnProperties(table, column.Name, newName: null);
            undo.Commit();
        }
        Dropped(db, file, table.TdefPageNumber);
    }

    /// <summary>The name of a relationship that has column <paramref name="column"/> of <paramref name="table"/> on either side, or null.</summary>
    private static string? RelationshipOn(Database db, string table, string column)
    {
        foreach (var relationship in db.GetRelationships())
        {
            bool from = string.Equals(relationship.FromTableName, table, StringComparison.OrdinalIgnoreCase)
                        && relationship.FromColumnNames.Contains(column, StringComparer.OrdinalIgnoreCase);
            bool to   = string.Equals(relationship.ToTableName, table, StringComparison.OrdinalIgnoreCase)
                        && relationship.ToColumnNames.Contains(column, StringComparer.OrdinalIgnoreCase);
            if (from || to) return relationship.Name;
        }
        return null;
    }

    // ── A relationship ───────────────────────────────────────────────────────

    /// <summary>Drops the relationship named <paramref name="name"/>.</summary>
    internal static void DropRelationship(Database db, PageFile file, string name)
    {
        file.EnsureWritable();
        var planned = PlanRelationship(db, file, name)
            ?? throw new ArgumentException($"There is no relationship named '{name}' in this database. Nothing was changed.", nameof(name));

        using (var undo = file.BeginUndo())
        {
            RemoveRelationship(db, file, planned);
            undo.Commit();
        }
        foreach (int tdefPage in planned.Tables) Dropped(db, file, tdefPage);
        db.SchemaChanged();
    }

    /// <summary>A relationship about to be dropped: what is to go, found and checked, and nothing written yet.</summary>
    private sealed class PlannedRemoval
    {
        internal string Name = "";
        internal Table? Relationships;
        internal List<int> RelationshipRows = new();
        internal Table? Catalog;
        internal List<int> CatalogRows = new();
        internal Table? Aces;
        internal List<int> AceRows = new();

        /// <summary>The index slots to take out, by the page of the table's definition and the slot's name.</summary>
        internal List<(int TdefPage, string Table, string Slot)> Slots = new();

        /// <summary>The definition pages of the tables whose definitions change.</summary>
        internal IEnumerable<int> Tables => Slots.Select(s => s.TdefPage).Distinct();
    }

    /// <summary>
    /// Finds everything relationship <paramref name="name"/> is in the file - its rows in MSysRelationships, its
    /// entry in the catalog with its permissions, and its index on each of its tables - and refuses, before
    /// anything is written, a system table whose rows this library cannot take out as Access would. Null when
    /// there is no such relationship.
    /// </summary>
    private static PlannedRemoval? PlanRelationship(Database db, PageFile file, string name)
    {
        if (!db.TryGetTable(SchemaChange.RelationshipsTable, out var relationships)) return null;
        var rows = RowsOf(file, relationships, "szRelationship", "szObject", "szReferencedObject", "grbit")
            .Where(r => string.Equals(r.Values["szRelationship"] as string, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0) return null;

        var planned = new PlannedRemoval
        {
            Name             = (string)rows[0].Values["szRelationship"]!,
            Relationships    = relationships,
            RelationshipRows = rows.Select(r => r.RowPointer).ToList(),
        };
        EnsureRowsCanBeChanged(relationships);

        // Its entry in the catalog: an object of type 8 with its name, and the permissions on that object.
        var catalog = db.GetTable(CatalogTable);
        var entries = RowsOf(file, catalog, "Id", "Name", "Type")
            .Where(r => r.Values["Type"] is short type && type == CatalogTypeRelationship
                     && string.Equals(r.Values["Name"] as string, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (entries.Count > 0)
        {
            planned.Catalog     = catalog;
            planned.CatalogRows = entries.Select(r => r.RowPointer).ToList();
            EnsureRowsCanBeChanged(catalog);
            PlanPermissions(db, file, planned, entries.Select(r => (int)r.Values["Id"]!));
        }

        // With referential integrity: the index of the relationship's name on the referring table, and the index
        // on the referenced table that names it as its other side.
        int flags = rows[0].Values["grbit"] is int grbit ? grbit : 0;
        if ((flags & SchemaChange.NoReferentialIntegrityFlag) == 0
            && rows[0].Values["szObject"] is string toName && db.TryGetTable(toName, out var child)
            && rows[0].Values["szReferencedObject"] is string fromName && db.TryGetTable(fromName, out var parent))
        {
            SchemaChange.EnsureReadWhole(child.Definition);
            SchemaChange.EnsureReadWhole(parent.Definition);
            var childEditor = TdefEditor.Load(file, child.Definition.TdefPageNumber, child.Name);
            for (int i = 0; i < childEditor.SlotCount; i++)
            {
                var reference = childEditor.SlotReference(i);
                if (reference.IndexType != TdefEditor.IndexTypeForeignKey || reference.Side != TdefEditor.ForeignKeySecondaryTable
                    || !string.Equals(childEditor.SlotNames[i], planned.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                planned.Slots.Add((child.Definition.TdefPageNumber, child.Name, childEditor.SlotNames[i]));

                var parentEditor = TdefEditor.Load(file, parent.Definition.TdefPageNumber, parent.Name);
                for (int j = 0; j < parentEditor.SlotCount; j++)
                {
                    var other = parentEditor.SlotReference(j);
                    if (other.IndexType == TdefEditor.IndexTypeForeignKey && other.Side == TdefEditor.ForeignKeyPrimaryTable
                        && other.OtherTablePage == child.Definition.TdefPageNumber && other.OtherIndexNumber == childEditor.SlotNumbers[i]
                        && parentEditor.SlotNumbers[j] == reference.OtherIndexNumber)
                        planned.Slots.Add((parent.Definition.TdefPageNumber, parent.Name, parentEditor.SlotNames[j]));
                }
            }
        }
        return planned;
    }

    /// <summary>Adds to <paramref name="planned"/> the rows of MSysACEs that give permissions on the objects <paramref name="ids"/>.</summary>
    private static void PlanPermissions(Database db, PageFile file, PlannedRemoval planned, IEnumerable<int> ids)
    {
        if (!db.TryGetTable(AcesTable, out var aces)) return;
        var wanted = new HashSet<int>(ids);
        var rows = RowsOf(file, aces, "ObjectId").Where(r => r.Values["ObjectId"] is int id && wanted.Contains(id)).ToList();
        if (rows.Count == 0) return;
        planned.Aces = aces;
        planned.AceRows.AddRange(rows.Select(r => r.RowPointer));
        EnsureRowsCanBeChanged(aces);
    }

    /// <summary>Writes a planned removal. The caller holds the undo scope.</summary>
    private static void RemoveRelationship(Database db, PageFile file, PlannedRemoval planned)
    {
        foreach (int row in planned.RelationshipRows) planned.Relationships!.DeleteRowAt(row);
        foreach (int row in planned.CatalogRows) planned.Catalog!.DeleteRowAt(row);
        foreach (int row in planned.AceRows) planned.Aces!.DeleteRowAt(row);

        // One slot at a time, each from the definition as it now is: a table related to itself loses two.
        foreach (var (tdefPage, table, slot) in planned.Slots)
        {
            var editor = TdefEditor.Load(file, tdefPage, table);
            RemoveSlot(file, editor, PositionOf(editor.SlotNames, slot));
            TdefBuffer.Write(file, db.Allocator, tdefPage, editor.ToDefinition());
        }
    }

    // ── A table ──────────────────────────────────────────────────────────────

    /// <summary>Drops the table named <paramref name="name"/>, and returns the page its definition was on.</summary>
    internal static int DropTable(Database db, PageFile file, string name)
    {
        file.EnsureWritable();
        if (!db.TryGetTable(name, out var table))
            throw new ArgumentException($"Table '{name}' was not found in this database. Nothing was changed.", nameof(name));
        var definition = table.Definition;
        int tdefPage   = definition.TdefPageNumber;
        EnsureNotSystemTable(db, table.Name);
        SchemaChange.EnsureReadWhole(definition);
        if (definition.Columns.FirstOrDefault(c => c.DataType == DataType.Complex) is { } complex)
            throw new NotSupportedException(
                $"Table '{table.Name}' has a complex column, '{complex.Name}' (attachments or several values), whose values are in " +
                "a table of their own: this library does not drop such a table. Nothing was changed.");

        // Access drops a table with the relationships it refers to others by - and to itself by - and refuses
        // to drop one another table refers to ("It is either the current index or is used in a relationship").
        var relationships = db.GetRelationships();
        if (relationships.FirstOrDefault(r => string.Equals(r.FromTableName, table.Name, StringComparison.OrdinalIgnoreCase)
                                           && !string.Equals(r.ToTableName, table.Name, StringComparison.OrdinalIgnoreCase)) is { } referring)
            throw new InvalidOperationException(
                $"Table '{table.Name}' is referred to by '{referring.ToTableName}' in the relationship '{referring.Name}': drop the " +
                "relationship first. Nothing was changed.");
        var removals = relationships
            .Where(r => string.Equals(r.ToTableName, table.Name, StringComparison.OrdinalIgnoreCase))
            .Select(r => PlanRelationship(db, file, r.Name))
            .OfType<PlannedRemoval>()
            .ToList();

        // Its entry in the catalog, and the permissions on it.
        var catalog = db.GetTable(CatalogTable);
        var entries = RowsOf(file, catalog, "Id", "Type")
            .Where(r => r.Values["Id"] is int id && id == tdefPage && IsTable(r.Values))
            .ToList();
        if (entries.Count == 0)
            throw new NotSupportedException($"Table '{table.Name}' is not dropped: the catalog has no entry of a table for it. Nothing was changed.");
        var own = new PlannedRemoval { Catalog = catalog, CatalogRows = entries.Select(r => r.RowPointer).ToList() };
        EnsureRowsCanBeChanged(catalog);
        PlanPermissions(db, file, own, new[] { tdefPage });

        var affected = removals.SelectMany(r => r.Tables).Where(page => page != tdefPage).Distinct().ToList();
        using (var undo = file.BeginUndo())
        {
            foreach (var removal in removals) RemoveRelationship(db, file, removal);
            foreach (int row in own.CatalogRows) own.Catalog!.DeleteRowAt(row);
            foreach (int row in own.AceRows) own.Aces!.DeleteRowAt(row);
            FreePagesOf(file, tdefPage, table.Name);
            undo.Commit();
        }
        foreach (int page in affected) Dropped(db, file, page);
        file.FreedLvalPages.Clear();
        file.IndexesChecked.Clear();
        db.SchemaChanged();
        return tdefPage;
    }

    /// <summary>
    /// Gives every page of the table whose definition starts on <paramref name="tdefPage"/> back to the file: the
    /// pages of its rows, of each index's tree and of each long-value column, the usage maps that list them, and
    /// the definition's own. The definition's first page is left as Access leaves it: its type no longer a
    /// definition's (0x08), the rest of it as it was.
    /// </summary>
    private static void FreePagesOf(PageFile file, int tdefPage, string name)
    {
        var format = file.Format;
        byte[] definition = TdefBuffer.Read(file, tdefPage, out var laterPages);
        var info   = TdefReader.Read(definition, format);
        var editor = new TdefEditor(definition, format, name);

        for (int block = 0; block < editor.IndexCount; block++)
        {
            var pages = editor.BlockPages(block);
            if (pages.UmapPage > 0) UsageMap.Free(file, pages.UmapPage, pages.UmapRow);
            if (pages.RootPage > 0) GlobalUsageMap.MarkFree(file, pages.RootPage);
        }
        foreach (var maps in info.LvalColumnUmapPages.Values)
        {
            UsageMap.Free(file, maps.OwnedPage, maps.OwnedRow);
            if (maps.FreePage > 0 && maps.FreePage < file.PageCount && UsageMap.DeleteRow(file, maps.FreePage, maps.FreeRow))
                GlobalUsageMap.MarkFree(file, maps.FreePage);
        }
        UsageMap.Free(file, info.OwnedPagesUmapPage, info.OwnedPagesUmapRow);
        if (info.FreeSpaceUmapPage > 0 && info.FreeSpaceUmapPage < file.PageCount
            && UsageMap.DeleteRow(file, info.FreeSpaceUmapPage, info.FreeSpaceUmapRow))
            GlobalUsageMap.MarkFree(file, info.FreeSpaceUmapPage);

        byte[] first = file.ReadPage(tdefPage);
        first[0] = TdefBuffer.PageTypeDroppedTableDef;
        file.WritePage(tdefPage, first);
        GlobalUsageMap.MarkFree(file, tdefPage);
        foreach (int page in laterPages) GlobalUsageMap.MarkFree(file, page);
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private static bool IsTable(Row values) => values["Type"] is short type && type == JetFormat.CatalogTypeTable;

    /// <summary>
    /// Refuses a change of this kind to one of the database's own tables: Access keeps what it needs in them,
    /// and nothing here has been tried against one that is without a column or an index of its, or under another
    /// name.
    /// </summary>
    internal static void EnsureNotSystemTable(Database db, string table)
    {
        if (db.GetSystemTableNames().Contains(table, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Table '{table}' is one of the database's own: nothing is dropped from a system table or renamed in it, " +
                "and it is neither dropped nor renamed. Nothing was changed.");
    }

    /// <summary>Where <paramref name="name"/> is among <paramref name="names"/>, whatever the case of its letters.</summary>
    internal static int PositionOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new NotSupportedException($"A table's definition has no '{name}' where it was read to have one. Nothing was changed.");
    }

    /// <summary>
    /// Every row of <paramref name="table"/>, with where it is and its values for <paramref name="columns"/>:
    /// how a row of a system table is found to be taken out or rewritten.
    /// </summary>
    internal static List<(int RowPointer, Row Values)> RowsOf(PageFile file, Table table, params string[] columns)
    {
        var format     = file.Format;
        var definition = table.Definition;
        var wanted     = definition.Columns.Where(c => columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var decoder    = new RowDecoder(format, definition.Columns, definition.LvalColumnUmapPages.Count > 0 ? new LvalReader(file) : null);
        var rows       = new List<(int, Row)>();
        foreach (int pageNumber in UsageMap.GetOwnedPages(file.ReadPage(definition.UmapPageNumber), definition.OwnedPagesRow, format, file))
        {
            byte[] page = file.ReadPage(pageNumber);
            int rowCount = ByteUtil.GetShort(page, format.OffsetDataNumRows);
            for (int r = 0; r < rowCount; r++)
            {
                if (DataRows.Read(file, page, r) is not { } rowBytes) continue;
                var row = new Row();
                foreach (string column in columns) row[column] = null;
                foreach (var column in wanted) row[column.Name] = decoder.Decode(rowBytes, column);
                rows.Add((RowPointer.Pack(pageNumber, r), row));
            }
        }
        return rows;
    }

    /// <summary>
    /// Refuses, before anything is written, to take a row out of - or rewrite one in - a system table whose
    /// indexes this library cannot keep: the row's entries would stay behind it.
    /// </summary>
    internal static void EnsureRowsCanBeChanged(Table table)
    {
        if (table.IndexEntries.WhyNotKept() is { } why)
            throw new NotSupportedException(why.Replace("The table is left as it was.", "Nothing was changed."));
    }

    /// <summary>After a removal is written: as <see cref="SchemaChange.Changed"/>, and what was noted of the table's indexes is forgotten.</summary>
    private static void Dropped(Database db, PageFile file, int tdefPage)
    {
        file.IndexesChecked.Clear();
        SchemaChange.Changed(db, file, tdefPage);
    }
}
