namespace JackcessDotNet;

/// <summary>
/// The scans of tables in their own order that may still go on in this session - a <see cref="Cursor"/> over a table,
/// an enumeration of <see cref="Table.Rows"/> - each with what it gives of the table's pages (<see cref="TableScan"/>):
/// the rows they held when it listed them. A row inserted after that is not one it gives, whichever page the row goes
/// to: a page the table gains, which the scan does not go to, or one it does, where a delete or an update left room.
/// <para>
/// Each scan of a table is told of every slot added to one of its pages after it listed them
/// (<see cref="DataPageWriter"/>), and gives no row in the first such slot of the page or after it: a page begun again
/// takes its rows in slots added to it too. Nothing here is taken back with a change that fails: a slot from which a
/// scan gives no row held none of its rows, whatever is written there afterwards.
/// </para>
/// </summary>
internal sealed class TableScans
{
    // By the table's definition page, its scans, each for as long as something holds it.
    private readonly Dictionary<int, Scans> _scans = new();

    private sealed class Scans
    {
        public readonly List<WeakReference<TableScan>> Held = new();
        public int LookAgainAt = 32;
    }

    /// <summary>A scan of the table whose definition is on <paramref name="tdefPage"/>, told from now on of the rows it gains.</summary>
    public TableScan Begin(int tdefPage)
    {
        if (!_scans.TryGetValue(tdefPage, out var scans)) _scans[tdefPage] = scans = new Scans();
        if (scans.Held.Count >= scans.LookAgainAt)
        {
            scans.Held.RemoveAll(held => !held.TryGetTarget(out _));
            scans.LookAgainAt = Math.Max(32, scans.Held.Count * 2);
        }
        var scan = new TableScan();
        scans.Held.Add(new WeakReference<TableScan>(scan));
        return scan;
    }

    /// <summary>Stops telling <paramref name="scan"/> of the rows its table gains: it is over.</summary>
    public void End(int tdefPage, TableScan scan)
    {
        if (!_scans.TryGetValue(tdefPage, out var scans)) return;
        scans.Held.RemoveAll(held => !held.TryGetTarget(out var other) || ReferenceEquals(other, scan));
        if (scans.Held.Count == 0) _scans.Remove(tdefPage);
    }

    /// <summary>
    /// Tells the scans of the table whose definition is on <paramref name="tdefPage"/> that slot <paramref name="slot"/>
    /// has been added to page <paramref name="page"/>: it holds none of their rows.
    /// </summary>
    public void NewFrom(int tdefPage, int page, int slot)
    {
        if (_scans.Count == 0 || !_scans.TryGetValue(tdefPage, out var scans)) return;
        foreach (var held in scans.Held)
            if (held.TryGetTarget(out var scan)) scan.NewFrom(page, slot);
    }
}

/// <summary>
/// What one scan of a table in its own order gives (<see cref="TableScans"/>): the rows of the pages it listed, each
/// page's slots as they were when it listed them.
/// </summary>
internal sealed class TableScan
{
    // The pages listed, lowest first, as a usage map lists them; and, by page, the first slot that holds none of the
    // scan's rows.
    private List<int> _pages = new();
    private Dictionary<int, int>? _firstNew;

    /// <summary>Begins the scan again, over <paramref name="pages"/>, lowest first: the rows they hold now are its rows.</summary>
    public void Restart(List<int> pages)
    {
        _pages    = pages;
        _firstNew = null;
    }

    /// <summary>How many of the <paramref name="slots"/> page <paramref name="page"/> has now can hold rows the scan gives.</summary>
    public int Slots(int page, int slots)
        => _firstNew is not null && _firstNew.TryGetValue(page, out int first) ? Math.Min(first, slots) : slots;

    /// <summary>Notes that slot <paramref name="slot"/> of page <paramref name="page"/>, and every slot after it, holds none of the scan's rows.</summary>
    public void NewFrom(int page, int slot)
    {
        if (_pages.BinarySearch(page) < 0) return;   // a page the scan does not go to
        _firstNew ??= new Dictionary<int, int>();
        if (!_firstNew.TryGetValue(page, out int first) || slot < first) _firstNew[page] = slot;
    }
}
