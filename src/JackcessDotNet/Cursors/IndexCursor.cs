namespace JackcessDotNet;

/// <summary>
/// A cursor over the rows of one table in the order of one of its indexes, which also finds rows by the values
/// they have in the index's columns: made by <see cref="Table.CreateIndexCursor()"/> for the table's primary key,
/// or by <see cref="Table.CreateIndexCursor(string)"/> for the index of a name; with an
/// <see cref="IndexRange"/>, over the rows between two entries of the index only. Jackcess's
/// <c>IndexCursor</c>. To find one row by its primary key, <see cref="Table.FindRowByPrimaryKey"/> does it in
/// one call.
/// <para>
/// The rows are those the index has entries for, in the entries' order: by the index's columns, each ascending
/// or descending as the index has it, nulls first in an ascending column, and rows with the same values in the
/// order they are stored. An index is taken at its word, as Access takes one. Every lookup -
/// <see cref="FindRowByEntry"/>, <see cref="FindRowsByEntry"/>, <see cref="FindClosestRowByEntry"/>, and
/// <see cref="Cursor.FindRow(string, object?)"/> for the index's first column - goes through this index and no
/// other, and leaves the cursor on the row it found.
/// </para>
/// <para>
/// A row is looked for by the key Access keeps for its values, where this library makes that key: for every
/// type it writes index entries for, and for text in the sort orders General - Legacy and General. A value it
/// makes no key for - text in another sort order, a character Access keys by the characters around it, a key
/// longer than an entry holds - is still found by <see cref="FindRowByEntry"/> and
/// <see cref="Cursor.FindRow(string, object?)"/>, which then go through the index's rows comparing each; a range
/// and <see cref="FindClosestRowByEntry"/> need the key, and refuse such a value. Whichever way a row is found,
/// it is compared with the values asked for before it is returned (<see cref="Cursor.FindRow(string, object?)"/>
/// says how), and a null is never a match. A <see cref="DateTime"/> in a range's entry or given to
/// <see cref="FindClosestRowByEntry"/> is put against a row by the date the row's number reads as, in whichever
/// column of the index it is; a <see cref="double"/> by the number itself, as Access's <c>BETWEEN</c> and an
/// index's <c>Seek</c> put a date literal against a row.
/// </para>
/// <para>
/// Rows can be inserted, updated and deleted while the cursor goes through them, through the cursor itself or
/// anything else: another cursor, <see cref="Table.Update"/>. A row whose key in the index changes moves in the
/// cursor's order, and the cursor is told. Going one way, the cursor gives each row once: a row it has come to
/// is passed if an update has moved its entry ahead of the cursor, and a row it has not come to yet is given when
/// the cursor comes to its entry, wherever updates have moved that entry in the meantime. A search comes to the
/// rows it looks at, as a move comes to the row it gives. A cursor that turns back begins again from where it is:
/// going the other way it gives again the rows it has passed, as any cursor does, but not the row it is on.
/// Put before the first row or after the last, or on the row a lookup finds, it begins again from there. A
/// savepoint keeps what the cursor had come to, and is told of rows that move as the cursor is.
/// </para>
/// </summary>
public sealed class IndexCursor : Cursor
{
    private enum Where { BeforeFirst, AtEntry, AfterLast }

    private readonly Index            _index;
    private readonly IndexEntryWalker _entries;

    // The range: its start and end, the keys its entries are from (_low) and before (_high) - null for no bound -
    // and whether an entry that is one the start or the end stands for is in it. _none: a range no key can be in.
    private readonly EntryBound? _start;
    private readonly EntryBound? _end;
    private readonly bool        _startInclusive;
    private readonly bool        _endInclusive;
    private readonly byte[]?     _low;
    private readonly byte[]?     _high;
    private readonly bool        _none;

    // Where the cursor is: an entry's key and row pointer, which stay a place in the index's order whatever
    // becomes of the entry; and what it has come to in its pass.
    private Where  _where = Where.BeforeFirst;
    private byte[] _key   = Array.Empty<byte>();
    private int    _pointer;
    private Pass   _pass  = new();

    // The positions savepoints were taken at, each for as long as its savepoint is held: told of entries that move.
    private List<WeakReference<Saved>>? _saved;

    // While FindClosestRowByEntry looks: the values it was given, and whether it looks for an entry that is one of
    // them (passing the others, and stopping where none can be) or for one at or after them.
    private (EntryBound Values, bool Equal)? _seeking;

    internal IndexCursor(Table table, PageFile file, TableDefinition definition, Index index, IndexRange? range, bool watched = true)
        : base(table, file, definition)
    {
        _index = index;
        if (index.RootPageNumber <= 0 || index.Columns.Count == 0)
            throw new NotSupportedException(
                $"Index '{index.Name}' of table '{definition.Name}' cannot be read: its definition gives it no column or no page.");
        _entries = new IndexEntryWalker(file, index.RootPageNumber);

        if (range?.StartEntry is { } start)
        {
            _start          = BoundFor(start, nameof(range), "start");
            _startInclusive = range.IsStartInclusive;
            // An exclusive start whose keys decide begins after every entry it stands for.
            _low  = _startInclusive || _start.HasDates ? _start.StartFence : _start.EndFence;
            _none = _low is null;
        }
        if (range?.EndEntry is { } end)
        {
            _end          = BoundFor(end, nameof(range), "end");
            _endInclusive = range.IsEndInclusive;
            // An exclusive end whose keys decide ends before every entry it stands for.
            _high = _endInclusive || _end.HasDates ? _end.EndFence : _end.StartFence;
        }
        if (watched) table.Watch(this);
    }

    /// <summary>
    /// The index the cursor was made for: the table's primary key for <see cref="Table.CreateIndexCursor()"/>,
    /// and the index of that name for <see cref="Table.CreateIndexCursor(string)"/>. Jackcess's <c>getIndex</c>.
    /// </summary>
    public Index Index => _index;

    // ── The cursor's order ────────────────────────────────────────────────────

    private protected override void ToStart()
    {
        _where = Where.BeforeFirst;
        _pass  = new Pass();
    }

    private protected override void ToEnd()
    {
        _where = Where.AfterLast;
        _pass  = new Pass { StartWhere = Where.AfterLast };
    }

    private protected override bool AtStart => _where == Where.BeforeFirst;

    private protected override bool AtEnd => _where == Where.AfterLast;

    private protected override int? Step(bool forward)
    {
        if (_pass.Forward is { } going && going != forward) Turn(forward);
        else _pass.Forward = forward;

        while (!_none)
        {
            var entry = forward ? EntryAfterPlace() : EntryBeforePlace();
            if (entry is not { } next) break;
            _where   = Where.AtEntry;
            _key     = next.Key;
            _pointer = next.RowPointer;

            // An entry between the range's keys that its start or end leaves out - one of a date's group with
            // other values after it - is passed; so is one a closest row's lookup is not looking for.
            if (!InRange(next.Key)) continue;
            if (forward && _seeking is { } seeking)
            {
                if (seeking.Equal && seeking.Values.EndFence is { } end && IndexEntryWalker.CompareKeys(next.Key, end) >= 0) break;
                int against = seeking.Values.Compare(next.Key);
                if (seeking.Equal ? against != 0 : against < 0) continue;
            }
            return _pointer;
        }
        _where = forward ? Where.AfterLast : Where.BeforeFirst;
        return null;
    }

    // The entry after the cursor's place, within the range's keys; null at their end.
    private (byte[] Key, int RowPointer)? EntryAfterPlace()
    {
        var entry = _where switch
        {
            Where.AtEntry when _low is null || IndexEntryWalker.CompareKeys(_key, _low) >= 0 => _entries.After(_key, _pointer),
            Where.AfterLast => null,
            _ => _low is null ? _entries.First() : _entries.After(_low, 0),
        };
        return entry is { } found && _high is not null && IndexEntryWalker.CompareKeys(found.Key, _high) >= 0 ? null : entry;
    }

    // The entry before the cursor's place, within the range's keys; null at their beginning.
    private (byte[] Key, int RowPointer)? EntryBeforePlace()
    {
        var entry = _where switch
        {
            Where.AtEntry when _high is null || IndexEntryWalker.CompareKeys(_key, _high) < 0 => _entries.Before(_key, _pointer),
            Where.BeforeFirst => null,
            _ => _high is null ? _entries.Last() : _entries.Before(_high, 0),
        };
        return entry is { } found && _low is not null && IndexEntryWalker.CompareKeys(found.Key, _low) < 0 ? null : entry;
    }

    // Whether an entry is one of the range's: within its keys, and not left out by a start or an end with a date.
    private bool InRange(byte[] key)
    {
        if (_start is { HasDates: true } && _start.Compare(key) is var fromStart && (_startInclusive ? fromStart < 0 : fromStart <= 0))
            return false;
        if (_end is { HasDates: true } && _end.Compare(key) is var toEnd && (_endInclusive ? toEnd > 0 : toEnd >= 0))
            return false;
        return true;
    }

    private protected override object Place()
        => new Snapshot(_where, _key, _pointer, _pass);

    private protected override void Restore(object place)
    {
        var snapshot = (Snapshot)place;
        (_where, _key, _pointer, _pass) = (snapshot.Where, snapshot.Key, snapshot.Pointer, snapshot.Pass);
        snapshot.PutBack();
    }

    private protected override object SavePosition()
    {
        var saved = new Saved(_where, _key, _pointer, _pass.Copy());
        _saved ??= new List<WeakReference<Saved>>();
        if (_saved.Count >= 32 && _saved.Count % 32 == 0) _saved.RemoveAll(held => !held.TryGetTarget(out _));
        _saved.Add(new WeakReference<Saved>(saved));
        return saved;
    }

    private protected override void RestorePosition(object position)
    {
        // The savepoint's own position stays as it was, and is still told of rows that move: it can be restored again.
        var saved = (Saved)position;
        (_where, _key, _pointer, _pass) = (saved.Where, saved.Key, saved.Pointer, saved.Pass.Copy());
    }

    private protected override int? PointerAt() => _where == Where.AtEntry && _pointer != 0 ? _pointer : null;

    // ── What the cursor has come to ───────────────────────────────────────────

    /// <summary>
    /// The moves a cursor makes one way from where it began them - put before the first row or after the last,
    /// turned, or put on the row a lookup found - and the rows it has come to on its way. Those are the rows whose
    /// entries are between where the pass began and where the cursor is, but for the parts of the index a lookup
    /// went over (<see cref="Skipped"/>), as long as no update moves an entry: what updates have moved across that
    /// span is kept apart (<see cref="CameAway"/>, <see cref="MissedInside"/>).
    /// </summary>
    private sealed class Pass
    {
        /// <summary>Which way the pass goes: null before its first move, when it has come to nothing.</summary>
        internal bool? Forward;

        internal Where  StartWhere = Where.BeforeFirst;
        internal byte[] StartKey   = Array.Empty<byte>();
        internal int    StartPointer;

        /// <summary>The parts of the index a lookup went over without coming to them, each between two places (null: the index's beginning).</summary>
        internal List<(byte[]? FromKey, int FromPointer, byte[] ToKey)>? Skipped;

        /// <summary>Rows come to whose entries an update has moved out of the span the pass has gone over.</summary>
        internal HashSet<int>? CameAway;

        /// <summary>Rows not come to whose entries an update has moved into the span the pass has gone over.</summary>
        internal HashSet<int>? MissedInside;

        internal Pass Copy() => new()
        {
            Forward      = Forward,
            StartWhere   = StartWhere,
            StartKey     = StartKey,
            StartPointer = StartPointer,
            Skipped      = Skipped is null ? null : new List<(byte[]?, int, byte[])>(Skipped),
            CameAway     = CameAway is null ? null : new HashSet<int>(CameAway),
            MissedInside = MissedInside is null ? null : new HashSet<int>(MissedInside),
        };
    }

    /// <summary>
    /// Where the cursor was before a move or a lookup, to put it back when one fails or finds nothing. A move or a
    /// lookup begins a new pass, or changes where one began and what it went over; it changes nothing the pass
    /// knows of rows that moved, which only an update does, and no update is made while one runs.
    /// </summary>
    private sealed class Snapshot
    {
        private readonly bool?  _forward;
        private readonly Where  _startWhere;
        private readonly byte[] _startKey;
        private readonly int    _startPointer;
        private readonly int    _skipped;

        internal Snapshot(Where where, byte[] key, int pointer, Pass pass)
        {
            (Where, Key, Pointer, Pass) = (where, key, pointer, pass);
            (_forward, _startWhere, _startKey, _startPointer) = (pass.Forward, pass.StartWhere, pass.StartKey, pass.StartPointer);
            _skipped = pass.Skipped?.Count ?? 0;
        }

        internal Where  Where   { get; }
        internal byte[] Key     { get; }
        internal int    Pointer { get; }
        internal Pass   Pass    { get; }

        internal void PutBack()
        {
            (Pass.Forward, Pass.StartWhere, Pass.StartKey, Pass.StartPointer) = (_forward, _startWhere, _startKey, _startPointer);
            if (Pass.Skipped is { } skipped && skipped.Count > _skipped) skipped.RemoveRange(_skipped, skipped.Count - _skipped);
        }
    }

    /// <summary>A savepoint's position: the cursor's place and its pass, kept up as rows move.</summary>
    private sealed class Saved
    {
        internal Saved(Where where, byte[] key, int pointer, Pass pass) => (Where, Key, Pointer, Pass) = (where, key, pointer, pass);

        internal Where  Where   { get; }
        internal byte[] Key     { get; }
        internal int    Pointer { get; }
        internal Pass   Pass    { get; }
    }

    // A turn begins a new pass where the cursor is: going the other way, the rows it has passed are given again,
    // but for the row it is on, which it has just given wherever its entry now is.
    private void Turn(bool forward)
        => _pass = new Pass
        {
            Forward      = forward,
            StartWhere   = _where,
            StartKey     = _key,
            StartPointer = _pointer,
            CameAway     = _where == Where.AtEntry && _pointer != 0 ? new HashSet<int> { _pointer } : null,
        };

    // Puts the cursor before every entry of the key and after, going forwards: where a lookup begins. The entries
    // between where the cursor was and there are not come to.
    private void JumpTo(byte[] key)
    {
        if (_pass.Forward is null)
        {
            // The pass has come to nothing yet: it begins there.
            (_pass.StartWhere, _pass.StartKey, _pass.StartPointer) = (Where.AtEntry, key, 0);
        }
        else
        {
            if (_pass.Forward == false) Turn(forward: true);
            (_pass.Skipped ??= new List<(byte[]?, int, byte[])>()).Add((_where == Where.AtEntry ? _key : null, _pointer, key));
        }
        (_where, _key, _pointer) = (Where.AtEntry, key, 0);
    }

    private protected override bool Passes(int pointer, bool forward) => _pass.CameAway?.Contains(pointer) == true;

    /// <summary>
    /// Told by the table that the entry of the row at <paramref name="rowPointer"/> in the index tree numbered
    /// <paramref name="tree"/> was at <paramref name="oldKey"/> and is at <paramref name="newKey"/> - null for no
    /// entry: one an insert added, or an update added or took out of an index that ignores nulls - by this cursor
    /// or by anything else. What the cursor, and each of its savepoints, has come to follows the row.
    /// </summary>
    internal void EntryMoved(int tree, int rowPointer, byte[]? oldKey, byte[]? newKey)
    {
        if (tree != _index.IndexDataNumber) return;
        Follow(_where, _key, _pointer, _pass, rowPointer, oldKey, newKey);
        if (_saved is null) return;
        foreach (var held in _saved)
            if (held.TryGetTarget(out var saved))
                Follow(saved.Where, saved.Key, saved.Pointer, saved.Pass, rowPointer, oldKey, newKey);
    }

    // Whether the row had been come to, worked out from where its entry was, is kept as its entry moves: a row
    // come to whose entry leaves the span gone over is remembered, as is one not come to whose entry enters it.
    private static void Follow(Where where, byte[] key, int pointer, Pass pass, int row, byte[]? oldKey, byte[]? newKey)
    {
        bool wasIn = oldKey is not null && IsGoneOver(where, key, pointer, pass, oldKey, row);
        bool isIn  = newKey is not null && IsGoneOver(where, key, pointer, pass, newKey, row);
        bool cameTo = wasIn ? pass.MissedInside?.Contains(row) != true : pass.CameAway?.Contains(row) == true;
        pass.CameAway?.Remove(row);
        pass.MissedInside?.Remove(row);
        if (cameTo && !isIn) (pass.CameAway ??= new HashSet<int>()).Add(row);
        else if (!cameTo && isIn) (pass.MissedInside ??= new HashSet<int>()).Add(row);
    }

    // Whether an entry is in the span the pass has gone over: from where it began to where the cursor is, in its
    // direction, both ends in, but for what a lookup went over.
    private static bool IsGoneOver(Where where, byte[] key, int pointer, Pass pass, byte[] entryKey, int entryPointer)
    {
        if (pass.Forward is not { } forward) return false;
        bool fromStart = forward
            ? AtOrAfter(entryKey, entryPointer, pass.StartWhere, pass.StartKey, pass.StartPointer)
            : AtOrBefore(entryKey, entryPointer, pass.StartWhere, pass.StartKey, pass.StartPointer);
        bool toPlace = forward
            ? AtOrBefore(entryKey, entryPointer, where, key, pointer)
            : AtOrAfter(entryKey, entryPointer, where, key, pointer);
        if (!fromStart || !toPlace) return false;
        if (pass.Skipped is { } skipped)
            foreach (var (fromKey, fromPointer, toKey) in skipped)
                if ((fromKey is null || CompareEntries(entryKey, entryPointer, fromKey, fromPointer) > 0)
                    && CompareEntries(entryKey, entryPointer, toKey, 0) < 0)
                    return false;
        return true;
    }

    private static bool AtOrAfter(byte[] key, int pointer, Where where, byte[] placeKey, int placePointer)
        => where switch
        {
            Where.BeforeFirst => true,
            Where.AfterLast   => false,
            _                 => CompareEntries(key, pointer, placeKey, placePointer) >= 0,
        };

    private static bool AtOrBefore(byte[] key, int pointer, Where where, byte[] placeKey, int placePointer)
        => where switch
        {
            Where.BeforeFirst => false,
            Where.AfterLast   => true,
            _                 => CompareEntries(key, pointer, placeKey, placePointer) <= 0,
        };

    // Two places in the index's order: by key, then by row pointer.
    private static int CompareEntries(byte[] a, int aPointer, byte[] b, int bPointer)
    {
        int byKey = IndexEntryWalker.CompareKeys(a, b);
        return byKey != 0 ? byKey : ((uint)aPointer).CompareTo((uint)bPointer);
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves to the first row, in the index's order, whose values in the index's first columns are
    /// <paramref name="entryValues"/>, and returns it; when there is none, returns null and leaves the cursor
    /// where it was. Jackcess's <c>findRowByEntry</c>. A value is given for each of the index's columns, in the
    /// index's order, or for the first of them only: fewer values than the index has columns find the first row
    /// that has those in its leading columns.
    /// </summary>
    /// <param name="entryValues">The values to look for, in the order of the index's columns. A null among them finds no row.</param>
    /// <returns>The row, which is then <see cref="Cursor.Current"/>, or null when no row has those values.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entryValues"/> is null, has no values, or has more values than the index has columns.
    /// </exception>
    public Row? FindRowByEntry(params object?[] entryValues)
        => Find(EntryPattern(entryValues, nameof(entryValues)), fromStart: true);

    /// <summary>
    /// The rows whose values in the index's first columns are <paramref name="entryValues"/>, in the index's
    /// order: Jackcess's <c>newEntryIterable</c>. The rows are read as they are enumerated, and enumerating moves
    /// the cursor: it is left on the last of them, or where it was when there is none.
    /// </summary>
    /// <param name="entryValues">The values to look for, in the order of the index's columns. A null among them finds no row.</param>
    /// <returns>The rows, each of which is <see cref="Cursor.Current"/> as it is given.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entryValues"/> is null, has no values, or has more values than the index has columns.
    /// </exception>
    public IEnumerable<Row> FindRowsByEntry(params object?[] entryValues)
    {
        var pattern = EntryPattern(entryValues, nameof(entryValues));
        return Rows();

        IEnumerable<Row> Rows()
        {
            for (var row = Find(pattern, fromStart: true); row is not null; row = Find(pattern, fromStart: false))
                yield return row;
        }
    }

    /// <summary>
    /// Moves to the row with <paramref name="entryValues"/> in the index's first columns - the first of them in the
    /// index's order when there are several - or, when there is none, to the first row in the index's order whose
    /// entry is after the values, and returns it. Jackcess's <c>findClosestRowByEntry</c>. When no row of the
    /// cursor is at or after the values, the cursor is left after its last row and null is returned. A value is
    /// given for each of the index's columns, in the index's order, or for the first of them only; a null is the
    /// value an index holds for a column with none.
    /// <para>
    /// A <see cref="DateTime"/> is put against a row's Date/Time by the date the row's number reads as, to the
    /// millisecond, wherever the column is in the index, and a <see cref="double"/> by the number itself. The index
    /// orders the rows by the numbers they store, as Access's index and its <c>ORDER BY</c> do, and that is not
    /// always the order of the dates they read as: so when no row has the values, the first row after them in the
    /// index's order need not be the one with the nearest values. It is not where a day before 31 December 1899
    /// is asked for - its times run back from its midnight, and 30 December 1899's are stored as negative numbers
    /// as well as positive ones (-0.25 reads as six in the morning) - and it is not where a date is followed by
    /// other columns of the index and rows whose dates read as one are stored as different numbers, as Access
    /// computes some (a date plus fractions of a day): those rows come in the order of their numbers first, and of
    /// the columns after the date only within one number. With rows (1 January 2021, 1) stored as
    /// 44196.999999994 and (1 January 2021, 0) stored as 44197, the row after (1 January 2021, -1) is the first.
    /// </para>
    /// </summary>
    /// <param name="entryValues">The values to look for, in the order of the index's columns.</param>
    /// <returns>The row, which is then <see cref="Cursor.Current"/>, or null when there is none at or after the values.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entryValues"/> is null, has no values, has more values than the index has columns, or has
    /// a value that is not one its column holds.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// No key is made for the values: the index is on a column whose keys this library does not make (a
    /// Decimal, Binary, OLE or Complex column, or text in a sort order other than General - Legacy and General),
    /// a value has a character Access keys by the characters around it, or the key is longer than an entry
    /// holds. The cursor is left where it was.
    /// </exception>
    public Row? FindClosestRowByEntry(params object?[] entryValues)
    {
        if (entryValues is null) throw new ArgumentException("Entry values are required.", nameof(entryValues));
        var sought = BoundFor(entryValues, nameof(entryValues), "sought");
        if (_none) { MoveAfterLast(); return null; }

        // From the first key an entry with the values can have, within the range: first a row that has them, as
        // far as one can be; then the first row after them. Each lookup begins a pass where it begins. A row that
        // cannot be read leaves the cursor as it was.
        object place = Place();
        var current  = CurrentRow;
        try
        {
            byte[] from = _low is not null && IndexEntryWalker.CompareKeys(sought.StartFence, _low) < 0 ? _low : sought.StartFence;
            Row? row = null;
            foreach (bool equal in new[] { true, false })
            {
                ToStart();
                JumpTo(from);
                _seeking = (sought, equal);
                try
                {
                    row = MoveToRow(forward: true);
                }
                finally
                {
                    _seeking = null;
                }
                if (row is not null) break;
            }
            return row;
        }
        catch
        {
            Restore(place);
            CurrentRow = current;
            throw;
        }
    }

    private (Column Column, object? Value)[] EntryPattern(object?[] entryValues, string parameterName)
    {
        if (entryValues is null || entryValues.Length == 0)
            throw new ArgumentException("Entry values are required.", parameterName);
        if (entryValues.Length > _index.Columns.Count)
            throw new ArgumentException(
                $"Index '{_index.Name}' has {_index.Columns.Count} column(s) ({ColumnNamesOf(_index)}); " +
                $"{entryValues.Length} values were given.", parameterName);
        var pattern = new (Column, object?)[entryValues.Length];
        for (int i = 0; i < pattern.Length; i++)
            pattern[i] = (ColumnNamed(_index.Columns[i].Column.Name, parameterName), entryValues[i]);
        return pattern;
    }

    private static string ColumnNamesOf(Index index) => string.Join(", ", index.Columns.Select(c => c.Column.Name));

    /// <summary>
    /// The entries that have <paramref name="values"/> in the index's first columns, as a range's start or end or
    /// the entry a closest row is looked for at (<see cref="EntryBound"/>).
    /// </summary>
    private EntryBound BoundFor(IReadOnlyList<object?> values, string parameterName, string what)
    {
        if (values.Count == 0 || values.Count > _index.Columns.Count)
            throw new ArgumentException(
                $"Index '{_index.Name}' has {_index.Columns.Count} column(s) ({ColumnNamesOf(_index)}); " +
                $"{values.Count} {what} values were given.", parameterName);

        var columns = _index.Columns.Take(values.Count).ToList();
        foreach (var column in columns)
            if (!IndexKeys.CanEncode(column.Column))
                throw new NotSupportedException(
                    $"No key is made for index '{_index.Name}': {IndexKeys.WhyNot(column.Column)}.");
        try
        {
            for (int i = 0; i < columns.Count; i++)
                if (!IndexKeys.CanEncode(columns[i].Column, values[i]))
                    throw new NotSupportedException(
                        $"No key is made for index '{_index.Name}': {IndexKeys.WhyNot(columns[i].Column, values[i])}.");

            var bound = new EntryBound(columns, values);
            if (bound.Length > LongestKeyOf(columns.Count))
                throw new NotSupportedException(
                    $"No key is made for index '{_index.Name}': the {what} values' key is longer than an index entry holds.");
            return bound;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException(
                $"A {what} value is not one its column of index '{_index.Name}' holds: {e.Message}", parameterName, e);
        }
    }

    /// <summary>
    /// Values of an index's first columns - a range's start or end, or the entry a closest row is looked for at -
    /// and the entries they stand for: those with the values in those columns. A <see cref="DateTime"/> for a
    /// Date/Time column stands for every number that reads as it, wherever the column is in the index, and is put
    /// against an entry by the date the entry's number reads as (<see cref="ValueMatch.ReadsAs"/>); any other
    /// value by the bytes of its key, as the index orders it. The keys decide alone when no value is such a date.
    /// </summary>
    private sealed class EntryBound
    {
        private static readonly DateTime FirstDay = new(1899, 12, 30);
        private const double OneSecond = 1.0 / 86_400;

        // Each value's part of a key, or null for a date put against an entry by what the entry reads as; the
        // date, and the column's direction.
        private readonly byte[]?[]  _parts;
        private readonly DateTime[] _dates;
        private readonly bool[]     _ascending;

        internal EntryBound(IReadOnlyList<IndexColumn> columns, IReadOnlyList<object?> values)
        {
            _parts     = new byte[]?[columns.Count];
            _dates     = new DateTime[columns.Count];
            _ascending = columns.Select(c => c.IsAscending).ToArray();
            int firstDate = -1;
            for (int i = 0; i < columns.Count; i++)
            {
                var column = columns[i].Column;
                if (values[i] is DateTime date && column.DataType == DataType.DateTime)
                {
                    // The date as a row stores it, to the millisecond (ValueMatch.AsStored).
                    _dates[i] = ValueMatch.AsStored(date);
                    if (firstDate < 0) firstDate = i;
                }
                else _parts[i] = IndexKeys.EncodeColumn(column, columns[i].IsAscending, values[i]);
            }

            // How long the key of the values is, a date as the number it is written as: what an entry holds.
            Length = Enumerable.Range(0, columns.Count).Sum(i => _parts[i]?.Length
                ?? IndexKeys.EncodeColumn(DataType.DateTime, _ascending[i], _dates[i].ToOADate()).Length);

            if (firstDate < 0)
            {
                byte[] key = Concat(_parts);
                StartFence = key;
                EndFence   = IndexEntryWalker.AfterAllBeginningWith(key);
                return;
            }

            // Every entry the values stand for, or that is after (or before) them all, has a number in the first
            // date's column from the least that can read as that date or later on (or the greatest that can read
            // as it or earlier), under the key of the columns before it.
            HasDates = true;
            byte[] prefix = Concat(_parts.Take(firstDate));
            byte[] least    = Concat(new[] { prefix, IndexKeys.EncodeColumn(DataType.DateTime, _ascending[firstDate], LeastReadingAtOrAfter(_dates[firstDate])) });
            byte[] greatest = Concat(new[] { prefix, IndexKeys.EncodeColumn(DataType.DateTime, _ascending[firstDate], GreatestReadingAtOrBefore(_dates[firstDate])) });
            if (!_ascending[firstDate]) (least, greatest) = (greatest, least);
            StartFence = least;
            EndFence   = IndexEntryWalker.AfterAllBeginningWith(greatest);
        }

        /// <summary>Whether a value is a date put against entries by what they read as, so that the keys do not decide alone.</summary>
        internal bool HasDates { get; }

        /// <summary>The first key an entry the values stand for, or one after them, can have.</summary>
        internal byte[] StartFence { get; }

        /// <summary>The first key after every entry the values stand for or that is before them; null when no key is.</summary>
        internal byte[]? EndFence { get; }

        /// <summary>The length of the values' key, a date written as its own number.</summary>
        internal int Length { get; }

        /// <summary>
        /// Where an entry is against the values, in the index's order: below 0 before every entry they stand for,
        /// 0 for one of them, above 0 after them all.
        /// </summary>
        internal int Compare(byte[] key)
        {
            int at = 0;
            for (int i = 0; i < _parts.Length; i++)
            {
                if (_parts[i] is { } part)
                {
                    for (int j = 0; j < part.Length; j++)
                    {
                        if (at + j >= key.Length) return -1;
                        if (key[at + j] != part[j]) return key[at + j] < part[j] ? -1 : 1;
                    }
                    at += part.Length;
                    continue;
                }

                // A date's part: its flag, then the number's eight bytes - or the flag alone for no value, which
                // comes first in an ascending column and last in a descending one.
                bool ascending = _ascending[i];
                if (at >= key.Length) return -1;
                if (key[at] == (ascending ? 0x00 : 0xFF)) return ascending ? -1 : 1;
                if (at + 9 > key.Length) return -1;
                double stored = StoredNumber(key, at + 1, ascending);
                int order = ValueMatch.ReadsAs(stored) is { } read
                    ? read.CompareTo(_dates[i])
                    : stored.CompareTo(_dates[i].ToOADate());
                if (order != 0) return (order < 0) == ascending ? -1 : 1;
                at += 9;
            }
            return 0;
        }

        // The number a date's part of a key holds: IndexKeys.EncodeColumn undone. Its bytes were inverted when
        // the number was negative (ascending) or positive (descending), which leaves the top bit clear either way;
        // and a positive number's sign bit was set before that.
        private static double StoredNumber(byte[] key, int at, bool ascending)
        {
            bool topSet = (key[at] & 0x80) != 0;
            long bits = 0;
            for (int i = 0; i < 8; i++)
                bits = bits << 8 | (byte)(topSet ? key[at + i] : ~key[at + i]);
            if (topSet == ascending) bits ^= long.MinValue;
            return BitConverter.Int64BitsToDouble(bits);
        }

        // The least number that can read as the date or as a later one. From 31 December 1899 on the numbers read
        // as the dates do, in order. Before it a day's times run back from its midnight, and 30 December 1899's are
        // read from negative numbers as well as positive ones, so for an earlier date the numbers from the day
        // before on are taken in.
        private static double LeastReadingAtOrAfter(DateTime date)
            => date.ToOADate() >= 1.0 ? ValueMatch.StoredRange(date).Least : (date.Date - FirstDay).Days - 1 - OneSecond;

        // The greatest number that can read as the date or as an earlier one: for a date before 30 December 1899,
        // the number of its midnight, which is its day's greatest.
        private static double GreatestReadingAtOrBefore(DateTime date)
            => date.ToOADate() >= 0.0 ? ValueMatch.StoredRange(date).Greatest : (date.Date - FirstDay).Days + OneSecond;

        private static byte[] Concat(IEnumerable<byte[]?> parts)
        {
            var key = new List<byte>();
            foreach (var part in parts) key.AddRange(part!);
            return key.ToArray();
        }
    }

    /// <summary>
    /// The parts of the index every entry matching <paramref name="pattern"/> is in, in the index's order, each
    /// from a key to the key it ends before (null: the end of the index) - or null when the pattern has no value
    /// for the first column or no key is made for its values: the rows are then gone through one by one. An
    /// empty list is a value that is not one its column holds, so that no row has it.
    /// <para>
    /// The values of the leading columns make one key, and the entries that begin with it are the part. A
    /// <see cref="DateTime"/> looked for in a Date/Time column is not one key: every number that reads as it is
    /// looked for, so the part runs from the least of them to the greatest, under the key of the columns before
    /// it - and, for a time on 30 December 1899, which a number and its negative both read as, there are two.
    /// </para>
    /// </summary>
    private List<(byte[] From, byte[]? Before)>? PartsFor(IReadOnlyList<(Column Column, object? Value)> pattern)
    {
        var columns = new List<IndexColumn>();
        var values  = new List<object?>();
        DateTime? date = null;
        foreach (var indexColumn in _index.Columns)
        {
            int at = -1;
            for (int i = 0; i < pattern.Count && at < 0; i++)
                if (pattern[i].Column.Ordinal == indexColumn.Column.Ordinal) at = i;
            if (at < 0) break;
            columns.Add(indexColumn);
            values.Add(pattern[at].Value);
            if (indexColumn.Column.DataType == DataType.DateTime && pattern[at].Value is DateTime asked)
            {
                date = asked;
                break;
            }
        }
        if (columns.Count == 0) return null;

        var parts = new List<(byte[] From, byte[]? Before)>();
        try
        {
            byte[]? Key(object? last)
            {
                var given = values.ToArray();
                given[given.Length - 1] = last;
                if (!IndexKeys.CanSearch(columns, given)) return null;
                byte[] key = IndexKeys.EncodeWhole(columns, given);
                return key.Length <= LongestKeyOf(columns.Count) ? key : null;
            }

            if (date is not { } looked)
            {
                if (Key(values[values.Count - 1]) is not { } key) return null;
                parts.Add((key, IndexEntryWalker.AfterAllBeginningWith(key)));
                return parts;
            }

            var (least, greatest) = ValueMatch.StoredRange(looked);
            var ranges = new List<(double, double)> { (least, greatest) };
            if (least > 0 && greatest < 1) ranges.Add((-greatest, -least));
            foreach (var (low, high) in ranges)
            {
                if (Key(low) is not { } a || Key(high) is not { } b) return null;
                if (IndexEntryWalker.CompareKeys(a, b) > 0) (a, b) = (b, a);
                parts.Add((a, IndexEntryWalker.AfterAllBeginningWith(b)));
            }
            parts.Sort((x, y) => IndexEntryWalker.CompareKeys(x.From, y.From));
            return parts;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return new List<(byte[] From, byte[]? Before)>();
        }
    }

    /// <summary>
    /// The longest key of the index's first <paramref name="columns"/> columns that every entry of a row with
    /// those values begins with. An entry longer than it holds is cut, and its last two bytes are a digest
    /// (<see cref="IndexKeys.Truncate"/>): a key of some of the columns that reaches them is not what the cut
    /// entry of the whole row has there.
    /// </summary>
    private int LongestKeyOf(int columns)
        => columns < _index.Columns.Count ? IndexKeys.MaxKeyLength - 2 : IndexKeys.MaxKeyLength;

    private protected override int? Seek(IReadOnlyList<(Column Column, object? Value)> pattern, bool fromStart)
    {
        var parts = PartsFor(pattern);
        if (_none || parts is { Count: 0 }) return null;
        if (parts is null) return base.Seek(pattern, fromStart);

        // Every row that matches has its entry in one of the parts: the cursor goes to the first entry of each
        // in turn - unless it is in the part already, or past it - and no further than the part's last.
        if (fromStart) ToStart();
        foreach (var (from, before) in parts)
        {
            if (_where == Where.AfterLast) break;
            if (_where == Where.AtEntry && before is not null && IndexEntryWalker.CompareKeys(_key, before) >= 0) continue;
            if (_where == Where.BeforeFirst || IndexEntryWalker.CompareKeys(_key, from) < 0)
                JumpTo(_low is not null && IndexEntryWalker.CompareKeys(from, _low) < 0 ? _low : from);
            while (MoveToRow(forward: true) is { } row)
            {
                if (before is not null && IndexEntryWalker.CompareKeys(_key, before) >= 0)
                {
                    // Past the part: the entry come to is the next part's to look at, so the cursor is put
                    // just before it.
                    _pointer--;
                    break;
                }
                if (Matches(row, pattern)) return row.Pointer;
            }
        }
        return null;
    }
}

/// <summary>
/// The part of an index an <see cref="IndexCursor"/> goes through: the rows from one entry of the index to
/// another, in the index's order. What Jackcess's <c>CursorBuilder</c> takes as a start and an end entry. Given
/// to <see cref="Table.CreateIndexCursor(IndexRange)"/> or <see cref="Table.CreateIndexCursor(string, IndexRange)"/>.
/// <code>
///   // The orders of customer 7 placed in 2024, through an index on (Customer, Placed).
///   var cursor = orders.CreateIndexCursor("ByCustomerAndDate", new IndexRange
///   {
///       StartEntry = new object?[] { 7, new DateTime(2024, 1, 1) },
///       EndEntry   = new object?[] { 7, new DateTime(2025, 1, 1) },
///       IsEndInclusive = false,
///   });
/// </code>
/// An entry is a value for each of the index's columns, in the index's order, or for the first of them only:
/// with fewer values than the index has columns it stands for every row that has those values in its leading
/// columns - an inclusive start begins at the first such row and an inclusive end stops after the last. A null
/// is the value an index holds for a column with none. The order is the index's own, so in a descending column
/// the start is the greater value.
/// <para>
/// A <see cref="DateTime"/> for a Date/Time column, in whichever place of the entry, stands for every row whose
/// number reads as that date, to the millisecond (a date finer than that is taken to what it reads as once
/// stored), and is put against a row by the date its number reads as: a range from one date to another has the
/// rows whose dates are between them. A <see cref="double"/> is the number a row stores, put against a row's
/// number as Access's <c>BETWEEN</c> puts a date against one. (Access measured: <c>BETWEEN</c> and an index's
/// <c>Seek</c> go by the numbers, so a time of 30 December 1899 stored as a negative number - -0.25 reads as six
/// in the morning - is not between five and seven in the morning to Access; to a range of
/// <see cref="DateTime"/> values it is.) The rows come in the index's order, the order of the numbers stored, as in
/// Access's <c>ORDER BY</c>: before 31 December 1899 not the dates' order, since a day's times run back from its
/// midnight there; and rows whose dates read as one but are stored as different numbers in the order of the
/// numbers first and of the columns after the date within one number.
/// </para>
/// </summary>
public sealed class IndexRange
{
    /// <summary>Where the range begins, or null (the default) for the index's first row.</summary>
    public IReadOnlyList<object?>? StartEntry { get; set; }

    /// <summary>Whether rows with the values of <see cref="StartEntry"/> are in the range. True by default.</summary>
    public bool IsStartInclusive { get; set; } = true;

    /// <summary>Where the range ends, or null (the default) for the index's last row.</summary>
    public IReadOnlyList<object?>? EndEntry { get; set; }

    /// <summary>Whether rows with the values of <see cref="EndEntry"/> are in the range. True by default.</summary>
    public bool IsEndInclusive { get; set; } = true;
}
