namespace JackcessDotNet;

/// <summary>
/// Long-value pages with room on them, kept by the column whose values they hold - the owned-pages usage map that
/// lists them - with the room each had when last seen, for that column's later values to use (LvalWriter). The
/// pages are also in the column's free-space usage map, as Access keeps them, and the first write to a column in
/// a session notes what that map lists (<see cref="FirstLookFor"/>).
/// <para>
/// A column of an export comes to have a page here for nearly every page of notes it has written - each is left
/// with less room than the next note needs - so nothing may be done to all of them for one value. They are held
/// by page, and by room: a page with room for a value is found without looking at one that has none
/// (<see cref="TryFindRoom"/>), and <see cref="CandidatesVisited"/> counts the pages a write did look at.
/// </para>
/// </summary>
internal sealed class FreedLvalPages
{
    /// <summary>A page with less room than this is not worth reading for it, so it is not kept.</summary>
    internal const int MinUsefulFreeSpace = 64;

    /// <summary>The room noted for a page the column's free-space map lists but this session has not read.</summary>
    internal const int NotYetRead = int.MaxValue;

    private sealed class Pages
    {
        public readonly SortedDictionary<int, int> RoomOf = new();              // page -> room, lowest page first
        public readonly SortedSet<(int Room, int Page)> ByRoom = new();         // the same, least room first
        public readonly HashSet<int> Listed = new();                            // pages known to be in the free-space map
    }

    private readonly Dictionary<(int MapPage, int MapRow), Pages> _byColumn = new();
    private readonly HashSet<(int MapPage, int MapRow)> _looked = new();

    /// <summary>The pages a write has looked at for room, since the file was opened: for tests of what a write costs.</summary>
    internal long CandidatesVisited { get; set; }

    /// <summary>
    /// True the first time it is asked for the column whose owned-pages map is at (<paramref name="mapPage"/>,
    /// <paramref name="mapRow"/>) - when its free-space map is to be looked at - and false after.
    /// </summary>
    public bool FirstLookFor(int mapPage, int mapRow) => _looked.Add((mapPage, mapRow));

    /// <summary>
    /// Notes that <paramref name="page"/>, listed by the owned-pages map at (<paramref name="mapPage"/>,
    /// <paramref name="mapRow"/>), has <paramref name="freeSpace"/> bytes free - forgetting it when that is too little
    /// to use.
    /// </summary>
    public void Set(int mapPage, int mapRow, int page, int freeSpace)
    {
        var column = (mapPage, mapRow);
        _byColumn.TryGetValue(column, out var pages);
        if (pages is not null && pages.RoomOf.TryGetValue(page, out int had))
        {
            pages.ByRoom.Remove((had, page));
            pages.RoomOf.Remove(page);
        }
        if (freeSpace >= MinUsefulFreeSpace)
        {
            if (pages is null) _byColumn[column] = pages = new Pages();
            pages.RoomOf[page] = freeSpace;
            pages.ByRoom.Add((freeSpace, page));
        }
    }

    /// <summary>Forgets everything noted: the file was put back as it was before the notes were made.</summary>
    public void Clear()
    {
        _byColumn.Clear();
        _looked.Clear();
    }

    /// <summary>
    /// Notes the room <paramref name="page"/> has now if the column has it noted already - a page it freed - and leaves
    /// any other page alone.
    /// </summary>
    public void Renew(int mapPage, int mapRow, int page, int freeSpace)
    {
        if (_byColumn.TryGetValue((mapPage, mapRow), out var pages) && pages.RoomOf.ContainsKey(page))
            Set(mapPage, mapRow, page, freeSpace);
    }

    /// <summary>
    /// The pages of the column whose owned-pages map is at (<paramref name="mapPage"/>, <paramref name="mapRow"/>),
    /// lowest first, each with the room it had when last seen. Not to be enumerated across a <see cref="Set"/>.
    /// Every page: for the placement of earlier releases, which walks them, and for tests; nothing else should.
    /// </summary>
    public IEnumerable<KeyValuePair<int, int>> Of(int mapPage, int mapRow)
        => _byColumn.TryGetValue((mapPage, mapRow), out var pages)
            ? pages.RoomOf
            : Enumerable.Empty<KeyValuePair<int, int>>();

    /// <summary>
    /// A page of the column noted as having at least <paramref name="needed"/> bytes free: the one with the least
    /// room that will do, so that a page with more is left for a longer value, and the lowest of those. False when
    /// there is none. No page without the room is looked at.
    /// </summary>
    public bool TryFindRoom(int mapPage, int mapRow, int needed, out int page, out int room)
    {
        page = 0;
        room = 0;
        if (!_byColumn.TryGetValue((mapPage, mapRow), out var pages) || pages.ByRoom.Count == 0) return false;

        var enough = pages.ByRoom.GetViewBetween((needed, 0), (NotYetRead - 1, int.MaxValue));
        if (enough.Count == 0) return false;
        (room, page) = enough.Min;
        return true;
    }

    /// <summary>A page of the column noted as having exactly <paramref name="room"/> bytes free - an empty one. False when there is none.</summary>
    public bool TryFindWithExactly(int mapPage, int mapRow, int room, out int page)
    {
        page = 0;
        if (!_byColumn.TryGetValue((mapPage, mapRow), out var pages)) return false;
        var exact = pages.ByRoom.GetViewBetween((room, 0), (room, int.MaxValue));
        if (exact.Count == 0) return false;
        page = exact.Min.Page;
        return true;
    }

    /// <summary>The room noted for <paramref name="page"/>, when it is noted.</summary>
    public bool TryGetRoom(int mapPage, int mapRow, int page, out int room)
    {
        room = 0;
        return _byColumn.TryGetValue((mapPage, mapRow), out var pages) && pages.RoomOf.TryGetValue(page, out room);
    }

    /// <summary>Whether <paramref name="page"/> is known to be in the column's free-space map, as this session read or wrote it.</summary>
    public bool IsListed(int mapPage, int mapRow, int page)
        => _byColumn.TryGetValue((mapPage, mapRow), out var pages) && pages.Listed.Contains(page);

    /// <summary>Records that <paramref name="page"/> is, or is no longer, in the column's free-space map.</summary>
    public void SetListed(int mapPage, int mapRow, int page, bool listed)
    {
        if (!_byColumn.TryGetValue((mapPage, mapRow), out var pages))
        {
            if (!listed) return;
            _byColumn[(mapPage, mapRow)] = pages = new Pages();
        }
        if (listed) pages.Listed.Add(page);
        else pages.Listed.Remove(page);
    }
}
