namespace JackcessDotNet;

/// <summary>
/// The pages each table's free-space map lists - the second usage map a table's definition names, the data pages where
/// Access looks for room for a row (#58) - as this session has read and written the map: read once, the first time the
/// table is written to in the session, and kept as rows are inserted, moved and deleted (<see cref="DataPageWriter"/>).
/// A row then goes to the lowest listed page without the map being read again, however large the table or its map.
/// <para>
/// What is kept is what the map lists: a page is listed and taken out here only as it is listed and taken out of the
/// map, the map first. A change kept for undoing (<see cref="PageFile.BeginUndo"/>) notes what takes back each change
/// made here, and a dropped table's maps are forgotten (<see cref="Clear"/>).
/// </para>
/// </summary>
internal sealed class ListedDataPages
{
    private readonly PageFile _file;

    // By the table's definition page: the pages its free-space map lists, or null for a table whose map this library
    // does not keep - its definition names none, or one that is not a usage map it reads.
    private readonly Dictionary<int, SortedSet<int>?> _listed = new();

    // Pages found in their table's owned-pages map (by the table's definition page), so that each is looked up once.
    private readonly HashSet<(int Table, int Page)> _owned = new();

    public ListedDataPages(PageFile file) => _file = file ?? throw new ArgumentNullException(nameof(file));

    /// <summary>
    /// The pages <paramref name="table"/>'s free-space map lists, lowest first: read from the map the first time they
    /// are asked for in the session. Null when the table has no such map this library keeps. Not to be changed by the
    /// caller: <see cref="List"/> and <see cref="Delist"/> change the map and this together.
    /// </summary>
    public SortedSet<int>? Of(TableDefinition table)
    {
        int key = table.TdefPageNumber;
        if (_listed.TryGetValue(key, out var listed)) return listed;
        listed = Read(table);
        _listed[key] = listed;
        _file.Journal(() => _listed.Remove(key));
        return listed;
    }

    private SortedSet<int>? Read(TableDefinition table)
    {
        int mapPage = table.FreeSpaceUmapPage;
        if (mapPage <= 0 || mapPage >= _file.PageCount) return null;
        byte[] page = _file.ReadPage(mapPage);
        if (!UsageMap.IsLiveRow(page, table.FreeSpaceRow, _file.Format)) return null;
        SortedSet<int> listed;
        try
        {
            listed = new SortedSet<int>(UsageMap.GetOwnedPages(page, table.FreeSpaceRow, _file.Format, _file));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException)
        {
            return null;   // a map of a kind this library does not read: the table is written to as one with none
        }

        // Which of them the owned-pages map holds, read once with the map, so that no row's insert reads it for them.
        // Access keeps that map on the same page, as this library does: an inline one is read from the bytes in hand.
        try
        {
            byte[]? ownedMap = table.UmapPageNumber == mapPage ? page
                : table.UmapPageNumber > 0 && table.UmapPageNumber < _file.PageCount ? _file.ReadPage(table.UmapPageNumber)
                : null;
            if (ownedMap is not null && UsageMap.IsLiveRow(ownedMap, table.OwnedPagesRow, _file.Format))
                foreach (int owned in UsageMap.GetOwnedPages(ownedMap, table.OwnedPagesRow, _file.Format, _file))
                    if (listed.Contains(owned)) Owned(table, owned);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException)
        {
            // An owned-pages map this library does not read: each page is looked up as it is tried (IsOwned).
        }
        return listed;
    }

    /// <summary>
    /// Lists <paramref name="page"/> in <paramref name="table"/>'s free-space map, when the table has one this library
    /// keeps and the page is not listed already.
    /// </summary>
    public void List(TableDefinition table, int page, PageAllocator allocator)
    {
        if (Of(table) is not { } listed || listed.Contains(page)) return;
        UsageMap.AddPage(_file, allocator, table.FreeSpaceUmapPage, table.FreeSpaceRow, page);
        listed.Add(page);
        _file.Journal(() => listed.Remove(page));
    }

    /// <summary>Takes <paramref name="page"/> out of <paramref name="table"/>'s free-space map, when it is listed.</summary>
    public void Delist(TableDefinition table, int page)
    {
        if (Of(table) is not { } listed || !listed.Contains(page)) return;
        UsageMap.RemovePage(_file, table.FreeSpaceUmapPage, table.FreeSpaceRow, page);
        listed.Remove(page);
        _file.Journal(() => listed.Add(page));
    }

    /// <summary>
    /// Whether <paramref name="page"/> is in <paramref name="table"/>'s owned-pages map: what a page its free-space map
    /// lists is checked against before a row goes onto it. Each page is looked up once a session.
    /// </summary>
    public bool IsOwned(TableDefinition table, int page)
    {
        var key = (table.TdefPageNumber, page);
        if (_owned.Contains(key)) return true;
        if (!UsageMap.Contains(_file, table.UmapPageNumber, table.OwnedPagesRow, page)) return false;
        Owned(table, page);
        return true;
    }

    /// <summary>Notes that <paramref name="page"/> is in <paramref name="table"/>'s owned-pages map: one just added to it.</summary>
    public void Owned(TableDefinition table, int page)
    {
        var key = (table.TdefPageNumber, page);
        if (_owned.Add(key)) _file.Journal(() => _owned.Remove(key));
    }

    /// <summary>
    /// Forgets every table's maps: a table has been dropped. A change taken back gives back what was known before it,
    /// the same records, so that what the change noted before this is taken back in them too.
    /// </summary>
    public void Clear()
    {
        if (_listed.Count == 0 && _owned.Count == 0) return;
        var listed = new Dictionary<int, SortedSet<int>?>(_listed);
        var owned  = new HashSet<(int, int)>(_owned);
        _file.Journal(() =>
        {
            _listed.Clear();
            foreach (var table in listed) _listed.Add(table.Key, table.Value);
            _owned.Clear();
            _owned.UnionWith(owned);
        });
        _listed.Clear();
        _owned.Clear();
    }
}
