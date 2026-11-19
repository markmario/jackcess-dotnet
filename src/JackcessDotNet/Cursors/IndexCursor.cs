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
/// type it writes index entries for, and for text in the sort orders whose keys it makes. A value it
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
/// A Date/Time Extended value is keyed by the bytes its row stores, and Access writes a date in two forms: its
/// <c>INSERT</c> in one, its SQL <c>UPDATE</c> and DAO's <c>Edit</c> in another. A <see cref="DateTime"/> looked for
/// in such a column is looked for under the key of each form, so a lookup finds the rows in either - where Access's
/// own <c>=</c> through an index finds the first form alone. In a range's entry or given to
/// <see cref="FindClosestRowByEntry"/>, a <see cref="DateTime"/> for such a column is the key of the first form, and
/// rows are put against it by their keys, as Access's <c>BETWEEN</c> through an index puts them: a row in the second
/// form is where its bytes are, which is not always where its date is.
/// </para>
/// <para>
/// Rows can be inserted, updated and deleted while the cursor goes through them, through the cursor itself or
/// anything else: another cursor, <see cref="Table.Update"/>. A row whose key in the index changes moves in the
/// cursor's order, and the cursor is told. Going one way, the cursor gives each row once: a row it has come to
/// is passed if an update has moved its entry ahead of the cursor, and a row it has not come to yet is given when
/// the cursor comes to its entry, wherever updates have moved that entry in the meantime. A search comes to the
/// rows it looks at, as a move comes to the row it gives. An entry the cursor goes past without looking at it is
/// not come to: one before its range's start or after its end, one a range's date leaves out, one
/// <see cref="FindClosestRowByEntry"/> looks past on its way to the row it gives, and those a search jumps over
/// to the part of the index its values are in - so a row that is one of them is given when an update moves it
/// ahead of the cursor, into its range. A cursor that turns back begins again from where it is: going the other
/// way it gives again the rows it has passed, as any cursor does, but not the row it is on. Put before the first
/// row or after the last, or on the row a lookup finds, it begins again from there. A savepoint keeps what the
/// cursor had come to, and is told of rows that move as the cursor is.
/// </para>
/// <para>
/// What a cursor holds of the rows it has come to is the stretches of the index it has gone over, and the rows
/// updates have moved into or out of them since: a pass that moves the rows it gives - ahead of itself, or behind
/// - holds a few stretches however many rows it goes through, and the table forgets a cursor once nothing holds
/// it, at its next change.
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

    // Whether the row of the entry the cursor has just come to was given already in the pass (Passes).
    private bool _passing;

    // The positions savepoints were taken at, each for as long as its savepoint is held: told of entries that move.
    private List<WeakReference<Saved>>? _saved;

    // While FindClosestRowByEntry looks: the values it was given, and whether it looks for an entry that is one of
    // them (passing the others, and stopping where none can be) or for one at or after them.
    private (EntryBound Values, bool Equal)? _seeking;

    // While a search looks through a part of the index (Seek): the key the part ends before. The cursor stops short
    // of an entry there, not coming to it, so that the next part begins with it.
    private byte[]? _partEnd;

    /// <summary>How many times the cursor has been told of an entry of its index that was added, moved or taken out: for tests.</summary>
    internal int TimesTold { get; private set; }

    /// <summary>
    /// How much the cursor's pass holds of what it has come to: its stretches of the index, and the rows it holds
    /// apart from them - for tests that what a pass keeps stays bounded.
    /// </summary>
    internal int PassSize => _pass.Size;

    /// <summary>The places compared to keep what the cursor has come to as rows move: for tests of what a pass costs.</summary>
    internal long PlacesCompared { get; private set; }

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
        _pass  = new Pass();
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

            // A search through a part of the index stops short of the next part: the cursor is put just before
            // the entry, without coming to it.
            if (forward && _partEnd is { } partEnd && IndexEntryWalker.CompareKeys(next.Key, partEnd) >= 0)
            {
                (_where, _key, _pointer) = (Where.AtEntry, next.Key, next.RowPointer - 1);
                return null;
            }

            _where   = Where.AtEntry;
            _key     = next.Key;
            _pointer = next.RowPointer;

            // An entry between the range's keys that its start or end leaves out - one of a date's group with
            // other values after it - is passed; so is one a closest row's lookup is not looking for. Neither is
            // come to.
            if (!InRange(next.Key)) continue;
            if (forward && _seeking is { } seeking)
            {
                if (seeking.Equal && seeking.Values.EndFence is { } end && IndexEntryWalker.CompareKeys(next.Key, end) >= 0) break;
                int against = seeking.Values.Compare(next.Key);
                if (seeking.Equal ? against != 0 : against < 0) continue;
            }

            // The entry is come to: whether its row was given is what the pass held before it reached it.
            _passing = Given(_pass, next.Key, next.RowPointer, out bool held);
            if (held) (_pass.Met ??= new List<int>()).Add(next.RowPointer);
            Reach(_pass, next.Key, next.RowPointer, forward);
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

    // Whether an entry within the range's keys is one of the range's: not left out by a start or an end with a date.
    private bool InRange(byte[] key)
    {
        if (_start is { HasDates: true } && _start.Compare(key) is var fromStart && (_startInclusive ? fromStart < 0 : fromStart <= 0))
            return false;
        if (_end is { HasDates: true } && _end.Compare(key) is var toEnd && (_endInclusive ? toEnd > 0 : toEnd >= 0))
            return false;
        return true;
    }

    // Whether an entry is one of the cursor's: within the range's keys, and not left out by a date.
    private bool Within(byte[] key)
        => !_none
           && (_low is null || IndexEntryWalker.CompareKeys(key, _low) >= 0)
           && (_high is null || IndexEntryWalker.CompareKeys(key, _high) < 0)
           && InRange(key);

    private protected override object Place()
        => new Snapshot(_where, _key, _pointer, _pass);

    private protected override void Restore(object place)
    {
        var snapshot = (Snapshot)place;
        (_where, _key, _pointer, _pass) = (snapshot.Where, snapshot.Key, snapshot.Pointer, snapshot.Pass);
        snapshot.PutBack();
    }

    private protected override void Settle() => Settle(_pass);

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

    /// <summary>A stretch of the index's order, from one place to another, both in: an entry's key and row pointer.</summary>
    private sealed class Run
    {
        internal byte[] LowKey;
        internal int    LowPointer;
        internal byte[] HighKey;
        internal int    HighPointer;

        internal Run(byte[] key, int pointer) => (LowKey, LowPointer, HighKey, HighPointer) = (key, pointer, key, pointer);

        internal Run Copy() => (Run)MemberwiseClone();
    }

    /// <summary>
    /// The moves a cursor makes one way from where it began them - put before the first row or after the last,
    /// turned, or put on the row a lookup found - and the rows it has given on its way: Jackcess's cursor gives each
    /// entry it comes to, and this one gives a row once in a pass.
    /// <para>
    /// What the pass has given is held as stretches of the index (<see cref="Runs"/>, <see cref="Open"/>): each
    /// from the first entry the cursor came to after it began - a move, a jump past entries a search does not look
    /// at, a turn - to the last, every one of the cursor's entries in it being of a row given. An entry the cursor
    /// passes without coming to it - outside its range, left out by a date, looked past by a closest row's lookup -
    /// is not in one, nor is anything before the first entry come to. Updates move entries into and out of the
    /// stretches. A row given whose entry moves out of them is held in one of its own, or in one apart from the
    /// cursor's that it comes up against with no other entry between, which is stretched to it; a stretch that was
    /// the one place a row's entry has left holds nothing, and goes. The rows that are not as their places say are
    /// held apart (<see cref="Away"/>, <see cref="Missed"/>): a row given whose entry moves where no stretch can
    /// hold it - out of the range, out of the index, or as one of the rows of a larger change, which are told of
    /// once all are in - and a row not given whose entry moves into a stretch. A row is dropped from them when it
    /// is deleted, and when the cursor comes to it.
    /// </para>
    /// </summary>
    private sealed class Pass
    {
        /// <summary>Which way the pass goes: null before its first move, when it has come to nothing.</summary>
        internal bool? Forward;

        /// <summary>The stretches gone over, in the index's order, none touching another, but for the one the cursor is in.</summary>
        internal readonly List<Run> Runs = new();

        /// <summary>The stretch the cursor is in, which grows as it goes; null before its first entry, and after a jump.</summary>
        internal Run? Open;

        /// <summary>Stretches closed by a jump during a move or a search, put among <see cref="Runs"/> once it is done.</summary>
        internal List<Run>? Closing;

        /// <summary>Rows given whose entries are not in a stretch.</summary>
        internal HashSet<int>? Away;

        /// <summary>Rows not given whose entries are in a stretch.</summary>
        internal HashSet<int>? Missed;

        /// <summary>Rows of <see cref="Away"/> or <see cref="Missed"/> the cursor has come to during a move or a search: dropped once it is done.</summary>
        internal List<int>? Met;

        internal int Size => Runs.Count + (Open is null ? 0 : 1) + (Away?.Count ?? 0) + (Missed?.Count ?? 0);

        internal Pass Copy()
        {
            var copy = new Pass
            {
                Forward = Forward,
                Open    = Open?.Copy(),
                Away    = Away is { Count: > 0 } ? new HashSet<int>(Away) : null,
                Missed  = Missed is { Count: > 0 } ? new HashSet<int>(Missed) : null,
            };
            foreach (var run in Runs) copy.Runs.Add(run.Copy());
            return copy;
        }
    }

    /// <summary>
    /// Where the cursor was before a move or a lookup, to put it back when one fails or finds nothing. A move or a
    /// lookup begins a new pass, or adds to what one has come to: the stretch the cursor is in grows, a jump closes
    /// it and begins another, and the rows held apart that it comes to are noted. It changes nothing else, which
    /// only an update or its end does, and no update is made while one runs.
    /// </summary>
    private sealed class Snapshot
    {
        private readonly bool?   _forward;
        private readonly Run?    _open;
        private readonly byte[]? _openLowKey;
        private readonly int     _openLowPointer;
        private readonly byte[]? _openHighKey;
        private readonly int     _openHighPointer;
        private readonly int     _closing;
        private readonly int     _met;

        internal Snapshot(Where where, byte[] key, int pointer, Pass pass)
        {
            (Where, Key, Pointer, Pass) = (where, key, pointer, pass);
            _forward = pass.Forward;
            _open    = pass.Open;
            if (_open is { } open)
                (_openLowKey, _openLowPointer, _openHighKey, _openHighPointer) = (open.LowKey, open.LowPointer, open.HighKey, open.HighPointer);
            _closing = pass.Closing?.Count ?? 0;
            _met     = pass.Met?.Count ?? 0;
        }

        internal Where  Where   { get; }
        internal byte[] Key     { get; }
        internal int    Pointer { get; }
        internal Pass   Pass    { get; }

        internal void PutBack()
        {
            Pass.Forward = _forward;
            Pass.Open    = _open;
            if (_open is { } open)
                (open.LowKey, open.LowPointer, open.HighKey, open.HighPointer) = (_openLowKey!, _openLowPointer, _openHighKey!, _openHighPointer);
            if (Pass.Closing is { } closing && closing.Count > _closing) closing.RemoveRange(_closing, closing.Count - _closing);
            if (Pass.Met is { } met && met.Count > _met) met.RemoveRange(_met, met.Count - _met);
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
            Forward = forward,
            Away    = _where == Where.AtEntry && _pointer != 0 ? new HashSet<int> { _pointer } : null,
        };

    // Puts the cursor before every entry of the key and after, going forwards: where a lookup begins. The entries
    // between where the cursor was and there are not come to: the stretch it was in ends where it was.
    private void JumpTo(byte[] key)
    {
        if (_pass.Forward == false) Turn(forward: true);
        if (_pass.Open is { } open)
        {
            (_pass.Closing ??= new List<Run>()).Add(open);
            _pass.Open = null;
        }
        (_where, _key, _pointer) = (Where.AtEntry, key, 0);
    }

    private protected override bool Passes(int pointer, bool forward) => _passing;

    /// <summary>
    /// Told by the table that the entry of the row at <paramref name="rowPointer"/> in the index tree numbered
    /// <paramref name="tree"/> was at <paramref name="oldKey"/> and is at <paramref name="newKey"/> - null for no
    /// entry: one an insert added, or an update added or took out of an index that ignores nulls - by this cursor
    /// or by anything else. What the cursor, and each of its savepoints, has come to follows the row.
    /// <paramref name="alone"/>: the change is the only one of its kind told now, so the index is as it left it.
    /// </summary>
    internal void EntryMoved(int tree, int rowPointer, byte[]? oldKey, byte[]? newKey, bool alone)
    {
        if (tree != _index.IndexDataNumber) return;
        TimesTold++;
        Follow(_pass, rowPointer, oldKey, newKey, alone);
        ForEachSaved(saved => Follow(saved.Pass, rowPointer, oldKey, newKey, alone));
    }

    /// <summary>Told by the table that the row at <paramref name="rowPointer"/> was deleted: no pass holds anything of it any longer.</summary>
    internal void RowDeleted(int rowPointer)
    {
        Forget(_pass, rowPointer);
        ForEachSaved(saved => Forget(saved.Pass, rowPointer));

        static void Forget(Pass pass, int row)
        {
            pass.Away?.Remove(row);
            pass.Missed?.Remove(row);
        }
    }

    // Each savepoint still held; those let go of are dropped from the list.
    private void ForEachSaved(Action<Saved> act)
    {
        if (_saved is null) return;
        int held = 0;
        for (int i = 0; i < _saved.Count; i++)
        {
            if (!_saved[i].TryGetTarget(out var saved)) continue;
            _saved[held++] = _saved[i];
            act(saved);
        }
        _saved.RemoveRange(held, _saved.Count - held);
    }

    // Whether the row whose entry is at (key, pointer) was given in the pass, worked out from where its entry is;
    // and whether the row is one the pass holds apart.
    private bool Given(Pass pass, byte[] key, int pointer, out bool heldApart)
    {
        if (Covers(pass, key, pointer))
        {
            heldApart = pass.Missed?.Contains(pointer) == true;
            return !heldApart;
        }
        heldApart = pass.Away?.Contains(pointer) == true;
        return heldApart;
    }

    // Whether an entry at the place is in a stretch the pass has gone over, and is one of the cursor's.
    private bool Covers(Pass pass, byte[] key, int pointer)
    {
        if (!Within(key)) return false;
        if (pass.Open is { } open && Holds(open, key, pointer)) return true;
        if (pass.Closing is { } closing)
            foreach (var run in closing)
                if (Holds(run, key, pointer)) return true;
        return RunAt(pass.Runs, key, pointer) >= 0;
    }

    private bool Holds(Run run, byte[] key, int pointer)
        => Compare(key, pointer, run.LowKey, run.LowPointer) >= 0 && Compare(key, pointer, run.HighKey, run.HighPointer) <= 0;

    // The stretch of runs (in order, apart) that holds the place, or -1.
    private int RunAt(List<Run> runs, byte[] key, int pointer)
    {
        int at = LastStartingAtOrBefore(runs, key, pointer);
        return at >= 0 && Compare(key, pointer, runs[at].HighKey, runs[at].HighPointer) <= 0 ? at : -1;
    }

    // The last of runs (in order, apart) that begins at or before the place, or -1.
    private int LastStartingAtOrBefore(List<Run> runs, byte[] key, int pointer)
    {
        int lo = 0, hi = runs.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Compare(runs[mid].LowKey, runs[mid].LowPointer, key, pointer) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo - 1;
    }

    // The cursor has come to the entry at (key, pointer): the stretch it is in reaches it, or one begins there.
    private void Reach(Pass pass, byte[] key, int pointer, bool forward)
    {
        if (pass.Open is not { } open)
        {
            pass.Open = new Run(key, pointer);
            return;
        }
        if (forward && Compare(key, pointer, open.HighKey, open.HighPointer) > 0) (open.HighKey, open.HighPointer) = (key, pointer);
        else if (!forward && Compare(key, pointer, open.LowKey, open.LowPointer) < 0) (open.LowKey, open.LowPointer) = (key, pointer);
    }

    // Once a move or a search is done: the stretches it closed join the others, the stretch the cursor is in takes
    // in those it has reached into, and the rows held apart that it came to are as their places say.
    private void Settle(Pass pass)
    {
        if (pass.Closing is { Count: > 0 } closing)
        {
            foreach (var run in closing) Absorb(pass.Runs, run, insert: true);
            closing.Clear();
        }
        if (pass.Open is { } open) Absorb(pass.Runs, open, insert: false);
        if (pass.Met is { Count: > 0 } met)
        {
            foreach (int row in met)
            {
                pass.Away?.Remove(row);
                pass.Missed?.Remove(row);
            }
            met.Clear();
        }
    }

    // Takes into run every one of runs (in order, apart) that it overlaps, and puts it among them when insert.
    private void Absorb(List<Run> runs, Run run, bool insert)
    {
        int at = Math.Max(0, LastStartingAtOrBefore(runs, run.LowKey, run.LowPointer));
        while (at < runs.Count && Compare(runs[at].HighKey, runs[at].HighPointer, run.LowKey, run.LowPointer) < 0) at++;
        while (at < runs.Count && Compare(runs[at].LowKey, runs[at].LowPointer, run.HighKey, run.HighPointer) <= 0)
        {
            var other = runs[at];
            if (Compare(other.LowKey, other.LowPointer, run.LowKey, run.LowPointer) < 0) (run.LowKey, run.LowPointer) = (other.LowKey, other.LowPointer);
            if (Compare(other.HighKey, other.HighPointer, run.HighKey, run.HighPointer) > 0) (run.HighKey, run.HighPointer) = (other.HighKey, other.HighPointer);
            runs.RemoveAt(at);
        }
        if (insert) runs.Insert(at, run);
    }

    // Whether the row had been given, worked out from where its entry was, is kept as its entry moves: a row given
    // whose entry leaves the stretches gone over, or one not given whose entry enters them, is held apart - but for
    // a row given whose entry is one of the cursor's, which a stretch is made for, or stretched to.
    private void Follow(Pass pass, int row, byte[]? oldKey, byte[]? newKey, bool alone)
    {
        bool given = oldKey is not null ? Given(pass, oldKey, row, out _) : pass.Away?.Contains(row) == true;
        pass.Away?.Remove(row);
        pass.Missed?.Remove(row);
        // A stretch of the one entry that has left it holds nothing any longer.
        if (oldKey is not null) DropEmptied(pass, oldKey, row);
        if (newKey is null)
        {
            if (given) (pass.Away ??= new HashSet<int>()).Add(row);
            return;
        }
        bool isIn = Covers(pass, newKey, row);
        if (given && !isIn)
        {
            if (alone && Within(newKey)) Stretch(pass, newKey, row);
            else (pass.Away ??= new HashSet<int>()).Add(row);
        }
        else if (!given && isIn) (pass.Missed ??= new HashSet<int>()).Add(row);
    }

    // Drops the stretch that is the one place (key, pointer) - whose entry has moved - whether held apart or the one
    // the cursor is in: what a search that moves each row it finds leaves behind it.
    private void DropEmptied(Pass pass, byte[] key, int pointer)
    {
        if (pass.Open is { } open && IsOnly(open, key, pointer))
        {
            pass.Open = null;
            return;
        }
        int at = LastStartingAtOrBefore(pass.Runs, key, pointer);
        if (at >= 0 && IsOnly(pass.Runs[at], key, pointer)) pass.Runs.RemoveAt(at);
    }

    private bool IsOnly(Run run, byte[] key, int pointer)
        => Compare(run.LowKey, run.LowPointer, key, pointer) == 0 && Compare(run.HighKey, run.HighPointer, key, pointer) == 0;

    // Takes the place of a row given into the stretches held apart from the one the cursor is in: the one before it
    // reaches it, or the one after it begins there, when none of the cursor's entries is between; or it is one of
    // its own. The cursor's own is not stretched: the rows that come next to it are as likely to be rows it has not
    // given (another's moved behind it) as rows it has. The index is as the change that moved the row left it, so
    // what is between is what the index holds.
    private void Stretch(Pass pass, byte[] key, int pointer)
    {
        int at = LastStartingAtOrBefore(pass.Runs, key, pointer);
        Run? before = at >= 0 ? pass.Runs[at] : null;
        Run? after  = at + 1 < pass.Runs.Count ? pass.Runs[at + 1] : null;
        bool reachesBefore = before is not null && NothingBetween(before.HighKey, before.HighPointer, key, pointer);
        bool reachesAfter  = after is not null && NothingBetween(key, pointer, after.LowKey, after.LowPointer);
        if (reachesBefore && reachesAfter)
        {
            (before!.HighKey, before.HighPointer) = (after!.HighKey, after.HighPointer);
            pass.Runs.RemoveAt(at + 1);
        }
        else if (reachesBefore) (before!.HighKey, before.HighPointer) = (key, pointer);
        else if (reachesAfter) (after!.LowKey, after.LowPointer) = (key, pointer);
        else pass.Runs.Insert(at + 1, new Run(key, pointer));
    }

    // Whether none of the cursor's entries is between two places, the first before the second. A few entries that
    // are not the cursor's are looked past; more, and the answer is no.
    private bool NothingBetween(byte[] fromKey, int fromPointer, byte[] toKey, int toPointer)
    {
        var (key, pointer) = (fromKey, fromPointer);
        for (int looked = 0; looked < 8; looked++)
        {
            if (_entries.After(key, pointer) is not { } next || Compare(next.Key, next.RowPointer, toKey, toPointer) >= 0) return true;
            if (Within(next.Key)) return false;
            (key, pointer) = (next.Key, next.RowPointer);
        }
        return false;
    }

    // Two places in the index's order: by key, then by row pointer.
    private int Compare(byte[] a, int aPointer, byte[] b, int bPointer)
    {
        PlacesCompared++;
        return CompareEntries(a, aPointer, b, bPointer);
    }

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
    /// Decimal, Binary, OLE or Complex column, or text in a sort order whose keys it does not make),
    /// a value has a character Access keys by the characters around it, or the key is longer than an entry
    /// holds. The cursor is left where it was.
    /// </exception>
    public Row? FindClosestRowByEntry(params object?[] entryValues)
    {
        if (entryValues is null) throw new ArgumentException("Entry values are required.", nameof(entryValues));
        var sought = BoundFor(entryValues, nameof(entryValues), "sought");
        if (_none) { MoveAfterLast(); return null; }

        // From the first key an entry with the values can have, within the range: first a row that has them, as
        // far as one can be; then the first row after them. Each lookup begins a pass with the row it gives: the
        // entries it looks past are not come to. A row that cannot be read leaves the cursor as it was.
        object place = BeginMoving();
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
            DoneMoving(restoreTo: null);
            return row;
        }
        catch
        {
            DoneMoving(restoreTo: place);
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
    /// it - and, for a time on 30 December 1899, which a number and its negative both read as, there are two. Nor
    /// is one looked for in a Date/Time Extended column: Access keys a value by its bytes, and writes a date in two
    /// forms (<see cref="ExtendedDateTimeCodec.FormsOf"/>), so there is a part for each form's key. The first form's
    /// is also the key versions of this library from db6f93b made of a row in either form. Those from 2930d3f made the
    /// key of no value of a row in the second form, which is not looked under for a date: that part holds every row
    /// with no value, and each would be read for every date looked for.
    /// </para>
    /// <para>
    /// Bytes of a Date/Time Extended value in another form than the first, as a row stores them - a <see cref="Joiner"/>
    /// looks for a row's - are looked for under their key and under each key those versions made of such bytes
    /// (<see cref="StoredExtendedDate.AsEarlierVersionsRead"/>), a part each, where the rows are matched by their
    /// bytes: the rows of no value are read only for bytes in such a form.
    /// </para>
    /// </summary>
    private List<(byte[] From, byte[]? Before)>? PartsFor(IReadOnlyList<(Column Column, object? Value)> pattern)
    {
        var columns = new List<IndexColumn>();
        var values  = new List<object?>();
        DateTime? date = null, extended = null;
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
            if (indexColumn.Column.DataType == DataType.DateTimeExtended && pattern[at].Value is DateTime given)
            {
                extended = given;
                break;
            }
        }
        if (columns.Count == 0) return null;

        var parts = new List<(byte[] From, byte[]? Before)>();
        try
        {
            byte[]? KeyOf(object?[] given)
            {
                if (!IndexKeys.CanSearch(columns, given)) return null;
                byte[] key = IndexKeys.EncodeWhole(columns, given);
                return key.Length <= LongestKeyOf(columns.Count) ? key : null;
            }
            byte[]? Key(object? last)
            {
                var given = values.ToArray();
                given[given.Length - 1] = last;
                return KeyOf(given);
            }

            if (extended is { } when)
            {
                // Each form Access writes the date in is a key of its own, and a row with the date is under one of
                // them: ACE's INSERT writes the first, its SQL UPDATE and DAO's Edit the second (#51).
                foreach (byte[] form in ExtendedDateTimeCodec.FormsOf(when))
                {
                    if (Key(new StoredExtendedDate(form)) is not { } key) return null;
                    parts.Add((key, IndexEntryWalker.AfterAllBeginningWith(key)));
                }
                parts.Sort((x, y) => IndexEntryWalker.CompareKeys(x.From, y.From));
                return parts;
            }
            if (date is not { } looked)
            {
                if (Key(values[values.Count - 1]) is not { } key) return null;
                parts.Add((key, IndexEntryWalker.AfterAllBeginningWith(key)));
                // Bytes of a Date/Time Extended value in another form than the first - a row's, as a Joiner looks
                // for it - are also under the keys earlier versions made of the date they read, each a part of its
                // own, where the rows are matched by their bytes (TableIndexes.EarlierVersionsEntriesOf).
                foreach (var earlier in StoredExtendedDate.AsEarlierVersionsRead(values))
                    if (KeyOf(earlier) is { } other && !parts.Any(part => IndexEntryWalker.CompareKeys(part.From, other) == 0))
                        parts.Add((other, IndexEntryWalker.AfterAllBeginningWith(other)));
                parts.Sort((x, y) => IndexEntryWalker.CompareKeys(x.From, y.From));
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
        // in turn - unless it is in the part already, or past it - and no further than the part's last, stopping
        // short of the entry after it, which is the next part's to look at.
        if (fromStart) ToStart();
        foreach (var (from, before) in parts)
        {
            if (_where == Where.AfterLast) break;
            if (_where == Where.AtEntry && before is not null && IndexEntryWalker.CompareKeys(_key, before) >= 0) continue;
            if (_where == Where.BeforeFirst || IndexEntryWalker.CompareKeys(_key, from) < 0)
            {
                byte[] start = _low is not null && IndexEntryWalker.CompareKeys(from, _low) < 0 ? _low : from;
                // A jump goes past the entries before the part, which the search does not look at. With none
                // there, going on is going to the part: the cursor steps on, and what it has gone over stays one
                // stretch.
                if (!(_where == Where.AtEntry && _pass.Forward == true
                      && EntryAfterPlace() is { } next && IndexEntryWalker.CompareKeys(next.Key, start) >= 0))
                    JumpTo(start);
            }
            _partEnd = before;
            try
            {
                while (MoveToRow(forward: true) is { } row)
                    if (Matches(row, pattern)) return row.Pointer;
            }
            finally
            {
                _partEnd = null;
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
/// <para>
/// A <see cref="DateTime"/> for a Date/Time Extended column is the key of the bytes Access's <c>INSERT</c> writes for
/// it, and rows are put against it by their keys, as Access's <c>BETWEEN</c> through an index puts them (measured):
/// a row Access stored in its other form, the time of day in whole seconds, is where its bytes are, which is not
/// always where its date is.
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
