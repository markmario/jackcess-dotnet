namespace JackcessDotNet;

/// <summary>
/// The slots of the rows deleted in this session, by page: the last of them on each page. A <see cref="Row"/>, a cursor
/// or a savepoint that read a row before it was deleted still names the row's slot, and is to find no row there - an
/// update or a delete through it refused, a savepoint restored to no row - so a page begun again
/// (<see cref="DataPageWriter"/>) keeps each such slot, deleted, with the slots before it, and its rows go to the slots
/// after them. A row deleted in an earlier session is named by nothing a caller holds.
/// <para>
/// A change kept for undoing (<see cref="PageFile.BeginUndo"/>) notes what takes back what it noted here: a delete
/// taken back leaves its row where it was, and its slot not one of these.
/// </para>
/// </summary>
internal sealed class DeletedRowSlots
{
    private readonly PageFile _file;

    // By page, the last slot of a row deleted from it in this session.
    private readonly Dictionary<int, int> _last = new();

    public DeletedRowSlots(PageFile file) => _file = file ?? throw new ArgumentNullException(nameof(file));

    /// <summary>Notes that the row in slot <paramref name="row"/> of page <paramref name="page"/> has been deleted.</summary>
    public void Deleted(int page, int row)
    {
        bool had = _last.TryGetValue(page, out int last);
        if (had && last >= row) return;
        _last[page] = row;
        _file.Journal(had ? () => _last[page] = last : () => _last.Remove(page));
    }

    /// <summary>
    /// How many slots page <paramref name="page"/> keeps when it is begun again: up to the last a row deleted in this
    /// session had, or none.
    /// </summary>
    public int Kept(int page) => _last.TryGetValue(page, out int last) ? last + 1 : 0;
}
