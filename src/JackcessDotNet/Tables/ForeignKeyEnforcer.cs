using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;

namespace JackcessDotNet;

/// <summary>
/// Keeps the relationships that have referential integrity, when <see cref="Database.EnforceForeignKeys"/> is set:
/// refuses an insert, update or delete that would leave a row referring to no parent, and carries out what a
/// relationship asks for - cascade update, cascade delete, cascade-to-null - as Access does and as Java Jackcess's
/// FKEnforcer does.
/// <para>
/// A change is worked out whole before any of it is written. The rows it reaches are read into memory and the
/// change is made to them there, cascades and all, each check seeing the changes made before it - the checks the
/// table's indexes make of a row among them (<see cref="TableIndexes"/>: a null a key does not take, a key too
/// long, a duplicate in a unique index). Only a change that passes every check is written, so a refused one leaves
/// the file untouched; and it is written inside <see cref="PageFile.BeginUndo"/>, so one that fails while being
/// written is taken back. Every row written keeps its entry in every index of its table, as any other update or
/// delete does.
/// </para>
/// <para>
/// Rows are found through the tables' indexes: Access gives each relationship an index on the foreign key, and the
/// key it refers to is a primary key or a unique index, so the rows referring to a parent, and the parent a row
/// refers to, cost a few pages each, and only the rows the change reaches are held. Where no index on the columns
/// can be searched - a table made without one, an index whose keys this library does not make, a key that is null
/// - the table is read from end to end, once, and all its rows are held until the change is written.
/// </para>
/// <para>
/// An index is trusted to hold every row of its table, as Access trusts one, unless
/// <see cref="Database.VerifyIndexes"/> is set. Then it is searched only once it has been seen to
/// (<see cref="TableIndexes.RowPointersFor(IReadOnlyList{string}, IReadOnlyList{object?})"/>): the first search
/// through each index, while the database is open, reads its table to see. Versions of this library before it
/// kept every index left rows out of all but the primary key, and a row that is not in the index on its foreign
/// key is not found as a child (qcatco/exportmdb.interop#64). An index seen not to hold every row is not
/// searched: its table is read, as one with no index is.
/// </para>
/// <para>
/// Every value is compared as the row stores it (<see cref="Stored"/>), never as a caller reads or gives it: a
/// Double or Single by its bits, so 1 and the next Double after it are two keys and so are 0 and -0, as in
/// Access's indexes; a Date/Time as the double stored, which a <see cref="DateTime"/> does not always hold; a
/// Yes/No, which holds no null, as <see cref="RowValues"/> reads it - no value being No. A key made of anything else is not the key the
/// index holds, and a row looked for under it is not found.
/// </para>
/// <para>
/// A null in a key matches a null, as in Access, whose indexes hold an entry for it: a parent whose key is null -
/// a unique index allows that - has the rows whose foreign key is null for children. Deleting it deletes them or
/// is refused by them, and re-keying it takes them with it; a parent whose key is not null never touches them.
/// Only an insert, or a change to a foreign key, treats a foreign key that is all null as referring to nothing.
/// </para>
/// <para>
/// Where Access and Java Jackcess differ, Access is followed: a row may refer to itself; a text key that changes
/// only in case is a change; a cascade is refused when it leaves the changed row without a parent in another
/// relationship, or a duplicate in a unique index; and relationships are taken in the order MSysRelationships
/// lists them, each cascade followed to its end before the next relationship is looked at.
/// </para>
/// </summary>
internal sealed class ForeignKeyEnforcer
{
    private readonly Database _database;
    private readonly IReadOnlyList<Relationship> _relationships;

    internal ForeignKeyEnforcer(Database database)
    {
        _database = database;
        var tables = new HashSet<string>(database.Catalog.GetTableNames(includeSystem: true), StringComparer.OrdinalIgnoreCase);
        // One whose child table is gone has no rows to keep. One whose parent table is gone is kept: nothing can
        // satisfy it, as before.
        _relationships = database.GetRelationships()
            .Where(r => r.HasReferentialIntegrity && r.ColumnPairs.Count > 0 && tables.Contains(r.ToTableName))
            .ToList();
    }

    /// <summary>True when a relationship with referential integrity has <paramref name="table"/> on either side.</summary>
    internal bool Involves(Table table)
        => _relationships.Any(r => IsTable(r.FromTableName, table.Name) || IsTable(r.ToTableName, table.Name));

    // ── Insert ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses <paramref name="row"/>, about to be added to <paramref name="child"/>, when it refers to a parent
    /// row that is not there. A foreign key whose columns are all null refers to nothing. One that is null in part
    /// must match a parent's key like any other, null matching null, as in Access: so it is refused wherever the
    /// parent's key cannot be null. Nothing is written or changed here.
    /// </summary>
    internal void CheckInsert(Table child, Row row)
    {
        foreach (var relationship in _relationships.Where(r => IsTable(r.ToTableName, child.Name)))
        {
            // As the row will store them: a Yes/No given no value, or null, is stored No, and refers to a parent
            // whose key is No.
            object?[] values = relationship.ColumnPairs.Select(pair => StoredIn(child, row, pair.ToColumn)).ToArray();
            if (values.All(v => v is null)) continue;

            // A row may refer to itself: Access adds it. By a key that is null in part, too: Access holds a null in
            // the foreign key to match a null in the key, so (1, null) refers to a row whose key is (1, null).
            if (IsTable(relationship.FromTableName, child.Name))
            {
                var types = ParentColumns(relationship, child);
                var own = relationship.ColumnPairs.Select(pair => StoredIn(child, row, pair.FromColumn));
                if (KeyOf(types, own).AsSpan().SequenceEqual(KeyOf(types, values))) continue;
            }

            if (!ParentExists(relationship, values))
                throw Violation(relationship,
                    $"'{child.Name}' ({ChildColumnNames(relationship)}) = ({Show(values)}) has no matching row in '{relationship.FromTableName}'");
        }
    }

    /// <summary>
    /// Whether a row of the relationship's parent table has <paramref name="values"/> in its key, for an insert:
    /// through the index on that key - a few pages, however many rows the parent has - or, without one, by reading
    /// the table.
    /// </summary>
    private bool ParentExists(Relationship relationship, object?[] values)
    {
        // No such table: nothing can be referred to.
        if (!_database.TryGetTable(relationship.FromTableName, out var parent)) return false;

        var types = ParentColumns(relationship, parent);
        byte[] key = KeyOf(types, values);
        bool Matches(Row row)
            => KeyOf(types, relationship.ColumnPairs.Select(pair => row.TryGetValue(pair.FromColumn, out var value) ? value : null))
                .AsSpan().SequenceEqual(key);

        // The index finds 'ABC' for 'abc', as Access's does; the row an entry points at is read to see that it is
        // there and is the one.
        if (parent.IndexEntries.RowPointersFor(ParentColumnList(relationship), values) is { } pointers)
            return pointers.Any(pointer => parent.ReadRowAt(pointer, types) is { } row && Matches(row));

        var cursor = parent.CreateCursor();
        cursor.ColumnsToRead = types;
        cursor.ValuesAsStored = true;
        return cursor.Any(Matches);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Table.Update"/> for a table in a relationship: the row at <paramref name="pointer"/> is given
    /// <paramref name="newValues"/>, and with it the rows a cascade reaches are changed, or nothing is. Returns
    /// where the row is afterwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">No row of the table is at <paramref name="pointer"/>.</exception>
    internal int Update(Table table, int pointer, Row newValues)
    {
        var changes = new Changes(this);
        var rows    = changes.RowsOf(table);
        var row     = rows.At(pointer) ?? throw Table.NoRowAt(table.Name, pointer);

        // Everything given is written to the row; what the relationships and indexes care about is worked through first.
        row.Changes = new Row(newValues);
        var tracked = new Row(newValues.Where(v => rows.Tracks(v.Key)));
        Run(() => Change(changes, rows, row, tracked, cascade: null));
        changes.Write();
        return row.WrittenAt ?? pointer;
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Table.Delete"/> for a table in a relationship: the row at <paramref name="pointer"/> is deleted,
    /// and with it the rows a cascade reaches, or nothing is.
    /// </summary>
    /// <exception cref="InvalidOperationException">No row of the table is at <paramref name="pointer"/>.</exception>
    internal void Delete(Table table, int pointer)
    {
        var changes = new Changes(this);
        var rows    = changes.RowsOf(table);
        var row     = rows.At(pointer) ?? throw Table.NoRowAt(table.Name, pointer);

        Run(() => Remove(changes, rows, row));
        changes.Write();
    }

    // ── Working a change through ─────────────────────────────────────────────

    /// <summary>
    /// One step of a change, which yields the steps that follow from it. Cascades are run from a stack of these and
    /// not by one method calling the next, so a chain of rows of any length - each the parent of the next - does
    /// not use up the call stack.
    /// </summary>
    private delegate IEnumerable<Step> Step();

    private static void Run(Step first)
    {
        var pending = new Stack<IEnumerator<Step>>();
        try
        {
            pending.Push(first().GetEnumerator());
            while (pending.Count > 0)
            {
                // Depth first: what follows from a step is finished before the step goes on to its next.
                var current = pending.Peek();
                if (current.MoveNext()) pending.Push(current.Current().GetEnumerator());
                else pending.Pop().Dispose();
            }
        }
        finally
        {
            foreach (var step in pending) step.Dispose();
        }
    }

    /// <summary>
    /// Gives <paramref name="row"/> the values in <paramref name="newValues"/>. <paramref name="cascade"/> is the
    /// relationship the change came down, or null for the change the caller asked for.
    /// </summary>
    private IEnumerable<Step> Change(Changes changes, TableRows rows, TrackedRow row, Row newValues, Relationship? cascade)
    {
        // A value written again as it is changes nothing. Access compares what is stored: 'abc' to 'ABC' is a
        // change, and so is a Double to the next Double after it.
        var changed = new Row(newValues.Where(v => !SameValue(rows.Value(row, v.Key), rows.Stored(v.Key, v.Value))));
        if (changed.Count == 0) yield break;

        if (cascade is not null)
            foreach (var value in changed)
            {
                // A cascade does not write an AutoNumber: Access will not make a relationship that cascades updates
                // into one, and refuses a cascade-to-null that reaches one ("Field cannot be updated").
                if (rows.IsAutoNumber(value.Key))
                    throw Violation(cascade,
                        $"the cascade would change '{rows.Table.Name}.{value.Key}', an AutoNumber, which cannot be changed");
                // Nor null a column that requires a value. (What an index requires is the indexes' to say, below.)
                if (rows.Stored(value.Key, value.Value) is null && rows.IsRequired(value.Key))
                    throw Violation(cascade,
                        $"the cascade would make '{rows.Table.Name}.{value.Key}' null, which the column does not allow");
            }

        object?[] before = (object?[])row.Values.Clone();
        rows.Change(row, changed);
        CheckIndexes(rows, row, changed, cascade);

        // As a child: the row's new values need a parent, in every relationship but the one the change came down
        // (whose parent it is following).
        foreach (var relationship in _relationships)
        {
            if (ReferenceEquals(relationship, cascade) || !IsTable(relationship.ToTableName, rows.Table.Name)) continue;
            if (!relationship.ColumnPairs.Any(pair => changed.ContainsKey(pair.ToColumn))) continue;

            object?[] values = relationship.ColumnPairs.Select(pair => rows.Value(row, pair.ToColumn)).ToArray();
            if (values.All(v => v is null)) continue;
            if (!changes.ParentExists(relationship, values))
                throw Violation(relationship,
                    $"'{rows.Table.Name}' ({ChildColumnNames(relationship)}) = ({Show(values)}) has no matching row in '{relationship.FromTableName}'");
        }

        // As a parent: the rows that referred to the old key follow it, or forbid the change.
        foreach (var relationship in _relationships)
        {
            if (!IsTable(relationship.FromTableName, rows.Table.Name)) continue;
            if (!relationship.ColumnPairs.Any(pair => changed.ContainsKey(pair.FromColumn))) continue;

            var children = changes.RowsOf(relationship.ToTableName);
            object?[] oldValues = relationship.ColumnPairs.Select(pair => rows.Value(before, pair.FromColumn)).ToArray();
            byte[] oldKey = KeyOf(changes.ParentColumns(relationship), oldValues);
            var referring = children.Referring(relationship, oldKey, oldValues);
            if (referring.Count == 0) continue;

            if (!relationship.CascadeUpdates)
                throw Violation(relationship,
                    $"'{rows.Table.Name}' ({ParentColumnNames(relationship)}) cannot be changed while rows of '{relationship.ToTableName}' refer to it");

            foreach (var child in referring)
            {
                if (!children.StillRefers(relationship, child, oldKey)) continue;   // a cascade got there first
                var follow = new Row(relationship.ColumnPairs.Select(pair =>
                    new KeyValuePair<string, object?>(pair.ToColumn, rows.Value(row, pair.FromColumn))));
                yield return () => Change(changes, children, child, follow, relationship);
            }
        }
    }

    /// <summary>
    /// What the table's indexes refuse of <paramref name="row"/> as the change leaves it - the one rule, theirs
    /// (<see cref="TableIndexes"/>): a null the primary key or a required index does not take, a key too long for
    /// an index page, a duplicate in a unique index. The duplicate is looked for among the rows as the change
    /// leaves them, not as the file has them: a row the change deletes or re-keys no longer holds its old key.
    /// </summary>
    private static void CheckIndexes(TableRows rows, TrackedRow row, Row changed, Relationship? cascade)
    {
        var indexes = rows.Table.IndexEntries;
        indexes.EnsureCanBeKept();
        string? refusal = null;
        // Whether the refusal is a rule of the table's the row would break - a duplicate, a null a key does not
        // take - and not some other failure to work its entries out.
        bool brokenRule = true;
        try
        {
            foreach (var entry in indexes.EntriesFor(rows.RowOf(row), enforce: true))
            {
                if (!entry.Tree.IsUnique || entry.HasNull) continue;
                if (!entry.Tree.Columns.Any(c => changed.ContainsKey(c.Column.Name))) continue;
                if (rows.HoldersOf(entry).Any(other => !ReferenceEquals(other, row)))
                {
                    refusal = indexes.DuplicateIn(entry) + " Nothing was changed.";
                    break;
                }
            }
        }
        catch (InvalidOperationException e)
        {
            refusal    = e.Message;
            brokenRule = e is ConstraintViolationException;
        }
        if (refusal is null) return;
        throw cascade is null
            ? brokenRule ? new ConstraintViolationException(refusal) : new InvalidOperationException(refusal)
            : Violation(cascade, $"the cascade would leave a row of '{rows.Table.Name}' that cannot be written. {refusal.TrimEnd('.')}");
    }

    /// <summary>Deletes <paramref name="row"/>, and deletes, nulls or is refused by the rows that refer to it.</summary>
    private IEnumerable<Step> Remove(Changes changes, TableRows rows, TrackedRow row)
    {
        if (row.Deleted) yield break;
        // Gone first, so a row that refers to itself does not forbid its own deletion.
        row.Deleted = true;

        foreach (var relationship in _relationships)
        {
            if (!IsTable(relationship.FromTableName, rows.Table.Name)) continue;

            var children = changes.RowsOf(relationship.ToTableName);
            object?[] values = relationship.ColumnPairs.Select(pair => rows.Value(row, pair.FromColumn)).ToArray();
            byte[] key = KeyOf(changes.ParentColumns(relationship), values);
            var referring = children.Referring(relationship, key, values);
            if (referring.Count == 0) continue;

            if (!relationship.CascadeDeletes && !relationship.CascadeNullOnDelete)
                throw Violation(relationship,
                    $"the row of '{rows.Table.Name}' cannot be deleted while rows of '{relationship.ToTableName}' refer to it");

            foreach (var child in referring)
            {
                if (!children.StillRefers(relationship, child, key)) continue;   // a cascade got there first
                if (relationship.CascadeDeletes)
                {
                    yield return () => Remove(changes, children, child);
                }
                else
                {
                    var nulls = new Row(relationship.ColumnPairs.Select(pair => new KeyValuePair<string, object?>(pair.ToColumn, null)));
                    yield return () => Change(changes, children, child, nulls, relationship);
                }
            }
        }
    }

    // ── A relationship about to be created ───────────────────────────────────

    /// <summary>
    /// The first row of <paramref name="child"/> that refers to no row of <paramref name="parent"/> by
    /// <paramref name="relationship"/>, as a refusal names it, or null when every row refers to a parent or to
    /// nothing: what Access looks for before it gives a relationship referential integrity ("Existing data in
    /// table ... violates referential integrity rules"). Keys are matched as an insert's are
    /// (<see cref="CheckInsert"/>): a foreign key that is all null refers to nothing, and a row may refer to itself.
    /// Both tables are read once, the parent's keys held while the child is read; nothing is changed.
    /// </summary>
    internal static string? FirstOrphan(Relationship relationship, Table parent, Table child)
    {
        var types = ParentColumns(relationship, parent);
        var childColumns = relationship.ColumnPairs
            .Select(pair => child.Columns.First(c => c.Name.Equals(pair.ToColumn, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var parents = parent.CreateCursor();
        parents.ColumnsToRead  = types;
        parents.ValuesAsStored = true;
        foreach (var row in parents)
            keys.Add(Convert.ToBase64String(KeyOf(types, types.Select(c => row.TryGetValue(c.Name, out var value) ? value : null))));

        var children = child.CreateCursor();
        children.ColumnsToRead  = childColumns;
        children.ValuesAsStored = true;
        foreach (var row in children)
        {
            object?[] values = childColumns.Select(c => Stored(c, row.TryGetValue(c.Name, out var value) ? value : null)).ToArray();
            if (values.All(v => v is null)) continue;
            if (!keys.Contains(Convert.ToBase64String(KeyOf(types, values))))
                return $"'{child.Name}' ({ChildColumnNames(relationship)}) = ({Show(values)}) has no matching row in '{parent.Name}'";
        }
        return null;
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    /// <summary>The parent table's columns of the relationship: both sides' values are compared as that column's type.</summary>
    private static IReadOnlyList<Column> ParentColumns(Relationship relationship, Table parent)
        => relationship.ColumnPairs
            .Select(pair => parent.Columns.FirstOrDefault(c => c.Name.Equals(pair.FromColumn, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Relationship '{relationship.Name}' names column '{pair.FromColumn}', which '{parent.Name}' does not have."))
            .ToList();

    /// <summary>
    /// A key's values as bytes, equal for two keys exactly when Access holds them equal: null equals null, as an
    /// index entry does, and text is compared as its index compares it (without regard to case).
    /// </summary>
    private static byte[] KeyOf(IReadOnlyList<Column> types, IEnumerable<object?> values)
    {
        using var key = new MemoryStream();
        int i = 0;
        foreach (object? value in values)
        {
            byte[] part = KeyPart(types[i++], value);
            // Each part with its length before it, so no two different keys run together into the same bytes.
            key.WriteByte((byte)part.Length);
            key.WriteByte((byte)(part.Length >> 8));
            key.Write(part, 0, part.Length);
        }
        return key.ToArray();
    }

    private static byte[] KeyPart(Column type, object? value)
    {
        if (value is null) return Array.Empty<byte>();
        // (Text Access keys by the characters around it has no key here: it is compared as text, below. Only
        // Access can have written it in a key, and a change that would write it is refused by the table's indexes.)
        if (IndexKeys.CanEncode(type, value))
        {
            // The bytes the column's index would hold for the value, whole - text in the column's sort order, so
            // in a database that sorts in General as General holds it. An ascending key never begins with 0xFF.
            try { return IndexKeys.EncodeColumn(type, ascending: true, value); }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
            {
                // Not a value the column can hold: it equals none that is.
            }
        }
        string text = value switch
        {
            string s      => s.ToUpperInvariant(),
            byte[] bytes  => Convert.ToBase64String(bytes),
            DateTime date => date.Ticks.ToString(CultureInfo.InvariantCulture),
            Guid guid     => guid.ToString("D"),
            bool flag     => flag ? "1" : "0",
            IConvertible number when TryDecimal(number, out decimal d) => d.ToString(CultureInfo.InvariantCulture),
            IFormattable other => other.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        byte[] encoded = Encoding.UTF8.GetBytes(text);
        var part = new byte[encoded.Length + 1];
        part[0] = 0xFF;
        encoded.CopyTo(part, 1);
        return part;
    }

    /// <summary>Keys by their bytes.</summary>
    internal sealed class KeyComparer : IEqualityComparer<byte[]>
    {
        internal static readonly KeyComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
            => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] key)
        {
            unchecked
            {
                uint hash = 2166136261;   // FNV-1a
                foreach (byte b in key) hash = (hash ^ b) * 16777619;
                return (int)hash;
            }
        }
    }

    private static bool TryDecimal(object value, out decimal result)
    {
        result = 0;
        if (value is string or DateTime or bool or char) return false;
        try { result = Convert.ToDecimal(value, CultureInfo.InvariantCulture); return true; }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return false; }
    }

    /// <summary>
    /// <paramref name="value"/> as a row of <paramref name="column"/> stores it: the one form values are compared
    /// and keyed in. It is what <see cref="RowEncoder"/> writes for the value and what
    /// <see cref="RowDecoder.DecodeForKey"/> reads back - a number in the column's own type, a Currency rounded to
    /// its four places, a Date/Time as its double, and a Yes/No as the true or false it stands for, by the one
    /// reading of a caller's value that rows and keys are made from (<see cref="RowValues"/>): the column holds no
    /// null, and no value is No. A value the column cannot hold is left as it is, for the row encoder to refuse.
    /// </summary>
    private static object? Stored(Column column, object? value)
    {
        if (column.DataType == DataType.Boolean) return RowValues.ToBoolean(value);
        if (RowValues.IsNull(value)) return null;
        try
        {
            return column.DataType switch
            {
                DataType.Byte          => Convert.ToByte(value),
                DataType.Int16           => Convert.ToInt16(value),
                DataType.Int32          => Convert.ToInt32(value),
                DataType.Int64        => Convert.ToInt64(value),
                DataType.Single        => Convert.ToSingle(value),
                DataType.Double        => Convert.ToDouble(value),
                DataType.Currency         => IndexKeys.MoneyUnits(Convert.ToDecimal(value)) / 10000m,
                DataType.DateTime => value is DateTime date ? date.ToOADate() : value,
                DataType.Guid          => value is Guid ? value : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
                _                      => value,
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return value;
        }
    }

    /// <summary>What <paramref name="row"/> will store in <paramref name="column"/> of <paramref name="table"/>; null for a column it has not.</summary>
    private static object? StoredIn(Table table, Row row, string column)
    {
        row.TryGetValue(column, out var value);
        return table.Columns.FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)) is { } found
            ? Stored(found, value)
            : null;
    }

    /// <summary>
    /// Whether two values, each as a row stores it (<see cref="Stored"/>), are the same stored value. Floating-point
    /// values are compared by their bits: converted to anything else, two Doubles that differ can come out equal.
    /// </summary>
    private static bool SameValue(object? stored, object? given) => (stored, given) switch
    {
        (null, null)               => true,
        (null, _) or (_, null)     => false,
        (double a, double b)       => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
        (float a, float b)         => BitConverter.GetBytes(a).AsSpan().SequenceEqual(BitConverter.GetBytes(b)),
        (string a, string b)       => string.Equals(a, b, StringComparison.Ordinal),
        (byte[] a, byte[] b)       => a.AsSpan().SequenceEqual(b),
        _                          => Equals(stored, given),
    };

    private static bool IsTable(string name, string other) => string.Equals(name, other, StringComparison.OrdinalIgnoreCase);

    // ── Messages ─────────────────────────────────────────────────────────────

    private static ConstraintViolationException Violation(Relationship relationship, string what)
        => new($"Foreign-key violation in relationship '{relationship.Name}': {what}. Nothing was changed.");

    private static IReadOnlyList<string> ParentColumnList(Relationship relationship) => relationship.ColumnPairs.Select(p => p.FromColumn).ToList();
    private static IReadOnlyList<string> ChildColumnList(Relationship relationship)  => relationship.ColumnPairs.Select(p => p.ToColumn).ToList();
    private static string ParentColumnNames(Relationship relationship) => string.Join(", ", ParentColumnList(relationship));
    private static string ChildColumnNames(Relationship relationship)  => string.Join(", ", ChildColumnList(relationship));
    private static string Show(IEnumerable<object?> values)            => string.Join(", ", values.Select(v => v ?? "null"));

    // ── A change being worked out ────────────────────────────────────────────

    /// <summary>The tables one change has reached, their rows as the change leaves them, until it is written.</summary>
    private sealed class Changes
    {
        private readonly ForeignKeyEnforcer _enforcer;
        private readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TableRows> _rows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Relationship, IReadOnlyList<Column>> _parentColumns = new();

        internal Changes(ForeignKeyEnforcer enforcer) => _enforcer = enforcer;

        private Table Table(string name)
        {
            if (!_tables.TryGetValue(name, out var table))
                _tables[name] = table = _enforcer._database.GetTable(name);
            return table;
        }

        /// <summary>The table of that name, when the database has one.</summary>
        private bool TryTable(string name, [NotNullWhen(true)] out Table? table)
        {
            if (_tables.TryGetValue(name, out table)) return true;
            if (!_enforcer._database.TryGetTable(name, out table)) return false;
            _tables[name] = table;
            return true;
        }

        internal IReadOnlyList<Column> ParentColumns(Relationship relationship)
        {
            if (!_parentColumns.TryGetValue(relationship, out var columns))
                _parentColumns[relationship] = columns = ForeignKeyEnforcer.ParentColumns(relationship, Table(relationship.FromTableName));
            return columns;
        }

        /// <summary>The table's rows in this change: none read until one is asked for.</summary>
        internal TableRows RowsOf(string name) => _rows.TryGetValue(name, out var rows) ? rows : RowsOf(Table(name));

        internal TableRows RowsOf(Table table)
        {
            _tables[table.Name] = table;
            // What a check looks at: the columns a relationship names on this table's side, and every column of an
            // index - the primary key's among them - for the row's entries in them.
            var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in table.Indexes)
                foreach (var column in index.Columns)
                    tracked.Add(column.Column.Name);
            foreach (var relationship in _enforcer._relationships)
                foreach (var (from, to) in relationship.ColumnPairs)
                {
                    if (IsTable(relationship.FromTableName, table.Name)) tracked.Add(from);
                    if (IsTable(relationship.ToTableName, table.Name)) tracked.Add(to);
                }
            foreach (string column in table.Definition.EffectivePrimaryKeyColumns) tracked.Add(column);
            return _rows[table.Name] = new TableRows(this, table, tracked);
        }

        /// <summary>
        /// Whether a row of the relationship's parent table has <paramref name="values"/> in its key, as this
        /// change leaves that table.
        /// </summary>
        internal bool ParentExists(Relationship relationship, object?[] values)
        {
            if (!_rows.TryGetValue(relationship.FromTableName, out var parent))
            {
                // No such table: nothing can be referred to.
                if (!TryTable(relationship.FromTableName, out var table)) return false;
                parent = RowsOf(table);
            }
            return parent.Referred(relationship, KeyOf(ParentColumns(relationship), values), values).Count > 0;
        }

        /// <summary>
        /// Writes the change: every check has passed. Whatever can still refuse a row does so before the first is
        /// written, and a failure after that takes back what was written.
        /// </summary>
        internal void Write()
        {
            var changed = _rows.Values
                .Select(rows => (rows.Table, Rows: rows.Read.Where(r => r.Deleted || r.Changes is not null).OrderBy(r => (uint)r.Pointer).ToList()))
                .Where(t => t.Rows.Count > 0)
                .ToList();
            if (changed.Count == 0) return;

            foreach (var (table, rows) in changed)
            {
                table.IndexEntries.EnsureCanBeKept();
                if (rows.All(r => r.Deleted)) continue;
                // A definition rows cannot be written by refuses the change here, before anything is written.
                var encoder = new RowEncoder(table.File.Format, table.Definition);
                encoder.EnsureWritable();
                foreach (var row in rows.Where(r => !r.Deleted))
                    encoder.Validate(row.Changes!);
            }

            using var undo = changed[0].Table.File.BeginUndo();
            foreach (var (table, rows) in changed)
                foreach (var row in rows.Where(r => r.Deleted))
                    table.DeleteRowAt(row.Pointer);
            foreach (var (table, rows) in changed)
                foreach (var row in rows.Where(r => !r.Deleted))
                    row.WrittenAt = table.RewriteRowAt(row.Pointer, row.Changes!);
            undo.Commit();
        }
    }

    /// <summary>A row as a change leaves it: where it is in the file, and the values the checks look at.</summary>
    private sealed class TrackedRow
    {
        internal TrackedRow(int pointer, object?[] values) { Pointer = pointer; Values = values; }

        /// <summary>Where the row is (<see cref="RowPointer"/>); also its place in the table's order.</summary>
        internal int Pointer { get; }
        /// <summary>The tracked columns' values as the change leaves them, in <see cref="TableRows"/>' order of them.</summary>
        internal object?[] Values { get; }
        /// <summary>What is to be written to the row, or null when it is left as it is.</summary>
        internal Row? Changes { get; set; }
        internal bool Deleted { get; set; }
        /// <summary>Where the row is once its changes are written (a rewritten row moves); null until then.</summary>
        internal int? WrittenAt { get; set; }
    }

    /// <summary>
    /// One table's rows in a change: those the change has reached, read where an index pointed, or all of them
    /// once the table has had to be read from end to end. A row is read once and is from then on what the change
    /// has made of it.
    /// </summary>
    private sealed class TableRows
    {
        private readonly Changes _changes;
        private readonly Dictionary<string, int> _tracked = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Column> _columns = new();
        private readonly Dictionary<int, TrackedRow> _read = new();
        private readonly Dictionary<object, Finder> _finders = new();
        private bool _allRead;

        internal TableRows(Changes changes, Table table, IEnumerable<string> tracked)
        {
            _changes = changes;
            Table    = table;
            // Only the tracked columns are ever decoded: a row's other values, long ones among them, are not read.
            foreach (string name in tracked)
                if (table.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } column)
                {
                    _tracked[column.Name] = _columns.Count;
                    _columns.Add(column);
                }
        }

        internal Table Table { get; }

        /// <summary>Every row read so far.</summary>
        internal IEnumerable<TrackedRow> Read => _read.Values;

        internal bool Tracks(string column) => _tracked.ContainsKey(column);
        internal object? Value(TrackedRow row, string column) => Value(row.Values, column);
        /// <summary>A tracked column's value among <paramref name="values"/>; null for a column the table does not have.</summary>
        internal object? Value(object?[] values, string column) => _tracked.TryGetValue(column, out int at) ? values[at] : null;

        /// <summary>The row's tracked values as a <see cref="Row"/>: what its index entries are made of.</summary>
        internal Row RowOf(TrackedRow row)
        {
            var values = new Row();
            for (int i = 0; i < _columns.Count; i++)
                if (row.Values[i] is { } value)
                    values[_columns[i].Name] = value;
            return values;
        }

        internal bool IsAutoNumber(string column)
            => Table.Columns.First(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)).IsAutoNumber;

        /// <summary>
        /// Whether the column must have a value. Access records that as the column's Required property, not in the
        /// table definition, so a column of a table read from a file is asked there.
        /// </summary>
        internal bool IsRequired(string column)
        {
            var asked = Table.Columns.First(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
            return asked.IsRequired || Table.PropertyMaps.Get(asked.Name)?["Required"] is true;
        }

        // ── Reading rows ─────────────────────────────────────────────────────

        // row: the row's tracked values as stored (RowDecoder.DecodeForKey).
        private TrackedRow Keep(int pointer, Row row)
        {
            if (_read.TryGetValue(pointer, out var kept)) return kept;   // as the change has it, not as the file does
            var values = new object?[_columns.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = ForeignKeyEnforcer.Stored(_columns[i], row.TryGetValue(_columns[i].Name, out var value) ? value : null);
            return _read[pointer] = new TrackedRow(pointer, values);
        }

        /// <summary><paramref name="value"/> as a row stores it in <paramref name="column"/>; itself, for a column not tracked.</summary>
        internal object? Stored(string column, object? value)
            => _tracked.TryGetValue(column, out int at) ? ForeignKeyEnforcer.Stored(_columns[at], value) : value;

        /// <summary>The row an index entry points at, or null when no row of the table is there.</summary>
        private TrackedRow? ReadAt(int pointer)
        {
            if (_read.TryGetValue(pointer, out var kept)) return kept;
            return Table.ReadRowAt(pointer, _columns) is { } row ? Keep(pointer, row) : null;
        }

        /// <summary>Reads the whole table, keeping every row: no index answers what is being asked.</summary>
        private void ReadAll()
        {
            if (_allRead) return;
            var cursor = Table.CreateCursor();
            cursor.ColumnsToRead = _columns;
            cursor.ValuesAsStored = true;
            while (cursor.NextRow() is { } row)
                Keep(cursor.CurrentRowPointer, row);
            _allRead = true;
            foreach (var finder in _finders.Values)
                foreach (var row in _read.Values)
                    finder.Add(row);
        }

        /// <summary>The row at <paramref name="pointer"/>, the one a caller's change begins at, or null when no row of the table is there.</summary>
        internal TrackedRow? At(int pointer) => ReadAt(pointer);

        // ── Finding rows by a key ────────────────────────────────────────────

        /// <summary>The rows whose foreign key in <paramref name="relationship"/> is <paramref name="key"/>, in table order.</summary>
        internal IReadOnlyList<TrackedRow> Referring(Relationship relationship, byte[] key, object?[] values)
            => Find(ChildFinder(relationship), key, () => Table.IndexEntries.RowPointersFor(ChildColumnList(relationship), values));

        /// <summary>The rows whose key <paramref name="relationship"/> refers to is <paramref name="key"/>.</summary>
        internal IReadOnlyList<TrackedRow> Referred(Relationship relationship, byte[] key, object?[] values)
        {
            var types   = _changes.ParentColumns(relationship);
            var columns = ParentColumnList(relationship);
            var finder  = FinderFor("parent:" + relationship.Name, row => KeyOf(types, columns.Select(column => Value(row, column))));
            return Find(finder, key, () => Table.IndexEntries.RowPointersFor(columns, values));
        }

        /// <summary>Whether <paramref name="row"/> is still there and its foreign key still <paramref name="key"/>.</summary>
        internal bool StillRefers(Relationship relationship, TrackedRow row, byte[] key)
            => ChildFinder(relationship).Has(row, key);

        /// <summary>The rows that hold <paramref name="entry"/>'s key in its unique index, as the change leaves them.</summary>
        internal IReadOnlyList<TrackedRow> HoldersOf(TableIndexes.Entry entry)
        {
            var tree = entry.Tree;
            byte[]? KeyIn(TrackedRow row)
            {
                foreach (var held in Table.IndexEntries.EntriesFor(RowOf(row), enforce: false))
                    if (ReferenceEquals(held.Tree, tree))
                        return held.HasNull ? null : held.Key;   // a null in a unique index is no other row's duplicate
                return null;
            }
            return Find(FinderFor(tree, KeyIn), entry.Key, () => Table.IndexEntries.RowPointersFor(entry));
        }

        private IReadOnlyList<TrackedRow> Find(Finder finder, byte[] key, Func<IEnumerable<int>?> entries)
        {
            var found = new List<TrackedRow>();
            if (!_allRead)
            {
                // What the file's index holds for the key, each row as the change has it by now...
                if (entries() is { } pointers)
                    foreach (int pointer in pointers)
                    {
                        if (ReadAt(pointer) is { } row) found.Add(row);
                    }
                else
                    ReadAll();
            }
            // ...and the rows the change has given the key (or every row, once the table has been read).
            found.AddRange(finder.Listed(key));
            return found.Where(row => finder.Has(row, key)).Distinct().OrderBy(row => (uint)row.Pointer).ToList();
        }

        internal void Change(TrackedRow row, Row newValues)
        {
            row.Changes ??= new Row();
            foreach (var value in newValues)
            {
                // Tracked as the row will store it; written as it was given.
                if (_tracked.TryGetValue(value.Key, out int at)) row.Values[at] = ForeignKeyEnforcer.Stored(_columns[at], value.Value);
                row.Changes[value.Key] = value.Value;
            }
            foreach (var finder in _finders.Values) finder.Add(row);
        }

        private Finder ChildFinder(Relationship relationship)
        {
            var types   = _changes.ParentColumns(relationship);
            var columns = ChildColumnList(relationship);
            return FinderFor("child:" + relationship.Name, row => KeyOf(types, columns.Select(column => Value(row, column))));
        }

        private Finder FinderFor(object what, Func<TrackedRow, byte[]?> keyOf)
        {
            if (!_finders.TryGetValue(what, out var finder))
            {
                _finders[what] = finder = new Finder(keyOf);
                // The rows an index of the file would not give under the key they have now: those the change has
                // changed - and, once the table has been read whole, all of them.
                foreach (var row in _read.Values)
                    if (_allRead || row.Changes is not null)
                        finder.Add(row);
            }
            return finder;
        }
    }

    /// <summary>
    /// Rows by a key of theirs. A row is listed under every key it has been added with, and looked at again when it
    /// is found: one that is deleted, or whose key has since changed, is passed over. Nothing is ever taken out, so
    /// a change to a row costs no search.
    /// </summary>
    private sealed class Finder
    {
        private readonly Func<TrackedRow, byte[]?> _keyOf;
        // A key's row, or its rows in a List<TrackedRow> when more than one has had it: most keys have one.
        private readonly Dictionary<byte[], object> _rows = new(KeyComparer.Instance);

        internal Finder(Func<TrackedRow, byte[]?> keyOf) => _keyOf = keyOf;

        internal bool Has(TrackedRow row, byte[] key) => !row.Deleted && _keyOf(row) is { } has && KeyComparer.Instance.Equals(has, key);

        /// <summary>Lists <paramref name="row"/> under the key it has now, if it has one.</summary>
        internal void Add(TrackedRow row)
        {
            if (_keyOf(row) is not { } key) return;
            if (!_rows.TryGetValue(key, out object? held)) _rows[key] = row;
            else if (held is List<TrackedRow> rows) { if (!ReferenceEquals(rows[rows.Count - 1], row)) rows.Add(row); }
            else if (!ReferenceEquals(held, row)) _rows[key] = new List<TrackedRow> { (TrackedRow)held, row };
        }

        /// <summary>The rows listed under <paramref name="key"/>, whatever has become of them since.</summary>
        internal IEnumerable<TrackedRow> Listed(byte[] key)
            => !_rows.TryGetValue(key, out object? held) ? Array.Empty<TrackedRow>()
             : held as IEnumerable<TrackedRow> ?? new[] { (TrackedRow)held };
    }
}
