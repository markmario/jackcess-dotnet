using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Writes long-value (Memo / OLE) data to dedicated LVAL data pages,
/// splitting large values into a chain of chunks when they exceed one page's capacity.
///
/// Each chunk row stored on a LVAL page contains:
///   [0 .. chunkDataLen-1]   actual data bytes
///   [chunkDataLen .. +3]    4-byte chain suffix:
///                             0xFF × 4                              → last (or only) chunk
///                             [3-byte LE nextPage][1-byte nextRow]  → link to next chunk
///
/// The 9-byte OTHER_PAGE LvRef written into the parent row's variable-length area:
///   [0..3]  4-byte LE total data byte count
///   [4]     0x40  (OTHER_PAGE marker)
///   [5..7]  3-byte LE page number of the first chunk
///   [8]     1-byte row index of the first chunk on that page
/// </summary>
internal sealed class LvalWriter
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;
    private readonly LvalUmapRef   _umap;   // owned/free usage maps for this LVAL column
    private readonly JetFormat     _format;

    public LvalWriter(PageFile file, PageAllocator allocator, LvalUmapRef umap)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _umap      = umap;
        _format    = file.Format;
    }

    /// <summary>
    /// Writes <paramref name="data"/> to LVAL storage and returns the 12-byte
    /// LvRef (REAL Jet format, matching Java Jackcess / ACE):
    ///   [lengthWithFlags:4 LE]  type in top two bits:
    ///                             0x40000000 OTHER_PAGE  - single chunk row
    ///                             0x00000000 OTHER_PAGES - chunk chain
    ///   [firstRow:1][firstPage:3 LE][unknown:4 = 0]
    /// OTHER_PAGE chunk rows contain raw data. OTHER_PAGES chunk rows are
    /// [nextRow:1][nextPage:3][payload], nextPage == 0 terminating the chain.
    /// </summary>
    public byte[] Write(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (!_file.ShareLongValuePages) return WriteAsAccessDoes(data);

        // Access's own chunk rows are PageSize - 20 bytes (4076 for Jet4) -
        // matches Java Jackcess MAX_LONG_VALUE_ROW_SIZE. Larger rows make ACE
        // fail to materialize the value.
        int maxChunkRowSize = _format.PageSize - 20;
        int totalLen = data.Length;

        if (totalLen <= maxChunkRowSize)
        {
            // Single chunk - OTHER_PAGE. The chunk row is the raw data.
            var single = new PagePicker(this);
            (int page, _) = single.Reserve(totalLen + JetFormat.SizeRowEntry);
            int row = WriteChunkRowOnPage(page, data, out _);
            single.Done();
            return BuildLvRef(totalLen, LvalTypeOtherPage, page, row, stamp: 0);
        }

        // Chunk chain - OTHER_PAGES, laid out the way Access does it: chunks in
        // FORWARD order. Placement is planned up front (so each chunk can embed
        // its successor's address) by a PagePicker.
        int chunkCapacity = maxChunkRowSize - 4;
        int numChunks = (totalLen + chunkCapacity - 1) / chunkCapacity;

        var placements = new (int page, int row)[numChunks];
        var picker = new PagePicker(this);
        for (int i = 0; i < numChunks; i++)
        {
            int len = Math.Min(chunkCapacity, totalLen - i * chunkCapacity);
            placements[i] = picker.Reserve(4 + len + JetFormat.SizeRowEntry);
        }

        int stamp = 0;
        for (int i = 0; i < numChunks; i++)
        {
            int start = i * chunkCapacity;
            int len   = Math.Min(chunkCapacity, totalLen - start);
            (int nextPage, int nextRow) = i + 1 < numChunks ? placements[i + 1] : (0, 0);

            var chunk = new byte[4 + len];
            chunk[0] = (byte)nextRow;
            chunk[1] = (byte) nextPage;
            chunk[2] = (byte)(nextPage >> 8);
            chunk[3] = (byte)(nextPage >> 16);
            Array.Copy(data, start, chunk, 4, len);
            // Each chunk names the next by the row it was planned to take: a chunk landing elsewhere would leave the
            // chain pointing at the wrong row, so that is an error, not a value read back wrong later.
            int row = WriteChunkRowOnPage(placements[i].page, chunk, out int pageStamp);
            if (i == 0) stamp = pageStamp;
            if (row != placements[i].row)
                throw new InvalidOperationException(
                    $"LVAL chunk {i} took row {row} of page {placements[i].page}, not the row {placements[i].row} its chain names.");
        }
        picker.Done();
        return BuildLvRef(totalLen, LvalTypeOtherPages, placements[0].page, placements[0].row, stamp);
    }

    // ── Access's way ─────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="data"/> where Access would. A value that fits one chunk goes on a page of such
    /// values: one the column's free-space map lists, with room, or a new one, which the map then lists until it
    /// is full. A longer value is a chain of chunks, each on a page that holds nothing else - a page a delete
    /// emptied, or a new one - and none of them is in the free-space map, so nothing is ever added to them.
    /// <para>
    /// Access depends on it. Freeing a chain, it gives every page of it back to the file without looking at what
    /// else is on them; with the last chunk on a page other values shared, as this library placed it, deleting
    /// one long value through Access left the others on pages Access then wrote over.
    /// </para>
    /// </summary>
    private byte[] WriteAsAccessDoes(byte[] data)
    {
        int maxChunkRowSize = _format.PageSize - 20;
        if (data.Length <= maxChunkRowSize)
        {
            int page = PageOfSingleChunks(data.Length + JetFormat.SizeRowEntry);
            int row  = WriteChunkRowOnPage(page, data, out _, out int room);
            NoteRoomLeft(page, MayEndAChain(data, 0, data.Length) ? 0 : room, row + 1);
            return BuildLvRef(data.Length, LvalTypeOtherPage, page, row, stamp: 0);
        }

        int chunkCapacity = maxChunkRowSize - 4;
        int numChunks = (data.Length + chunkCapacity - 1) / chunkCapacity;
        var pages = new int[numChunks];
        for (int i = 0; i < numChunks; i++)
            pages[i] = PageOfItsOwn();

        int stamp = 0;
        for (int i = 0; i < numChunks; i++)
        {
            int start = i * chunkCapacity;
            int len   = Math.Min(chunkCapacity, data.Length - start);
            int next  = i + 1 < numChunks ? pages[i + 1] : 0;   // its chunk is the only row there: row 0

            var chunk = new byte[4 + len];
            chunk[1] = (byte) next;
            chunk[2] = (byte)(next >> 8);
            chunk[3] = (byte)(next >> 16);
            Array.Copy(data, start, chunk, 4, len);
            int row = WriteChunkRowOnPage(pages[i], chunk, out int pageStamp);
            if (i == 0) stamp = pageStamp;
            if (row != 0)
                throw new InvalidOperationException(
                    $"LVAL chunk {i} took row {row} of page {pages[i]}, which was to hold nothing else.");
        }
        return BuildLvRef(data.Length, LvalTypeOtherPages, pages[0], 0, stamp);
    }

    /// <summary>
    /// The least room a page has for Access to list it in the column's free-space map: ACE lists a page left with
    /// 258 bytes or more and none left with 257 or fewer, whatever is on it - two values, three or thirty, in an
    /// Access 2000 file and an Access 2007 one (#35). One value alone never leaves 258 to 263: ACE keeps a value
    /// of more than 3,816 bytes as a chain, on a page of its own that it does not list. A page with less is
    /// neither listed nor noted here: what a session knows of a column's pages is then what the map says, so a
    /// value goes to the same page whether the file was written in one session or in several.
    /// </summary>
    internal const int MinListedFreeSpace = 258;

    /// <summary>
    /// The first look in a session at the column's free-space map: the pages it lists are read, once, for the room
    /// each has - a page Access or an earlier session left with room, or emptied - and noted. After it, every page's
    /// room is known, to a value of one chunk and to a chain alike: a chain's pages are the emptied ones it lists.
    /// </summary>
    private void LookAtTheMapOnce()
    {
        var notes = _file.FreedLvalPages;
        if (_umap.FreePage <= 0 || !notes.FirstLookFor(_umap.OwnedPage, _umap.OwnedRow)) return;
        foreach (int listed in UsageMap.GetOwnedPages(_file.ReadPage(_umap.FreePage), _umap.FreeRow, _format, _file))
        {
            notes.SetListed(_umap.OwnedPage, _umap.OwnedRow, listed, true);
            if (notes.TryGetRoom(_umap.OwnedPage, _umap.OwnedRow, listed, out _)) continue;
            if (RoomOn(listed) is { } has && has >= MinListedFreeSpace)
                notes.Set(_umap.OwnedPage, _umap.OwnedRow, listed, has);
            else
                Forget(listed);   // not this column's, or without the room: it leaves the map
        }
    }

    /// <summary>A page with room for a row of <paramref name="needed"/> bytes among other one-chunk values.</summary>
    private int PageOfSingleChunks(int needed)
    {
        var notes = _file.FreedLvalPages;
        LookAtTheMapOnce();

        // The page with the least room that will do, the lowest of them: found by its room, so no page known to
        // have too little is looked at, however many there are. What is noted is checked against the page itself.
        while (notes.TryFindRoom(_umap.OwnedPage, _umap.OwnedRow, needed, out int candidate, out _))
        {
            notes.CandidatesVisited++;
            int? room = RoomOn(candidate);
            if (room >= needed)
                return candidate;
            if (room is null || room < MinListedFreeSpace)
                Forget(candidate);
            else
                notes.Set(_umap.OwnedPage, _umap.OwnedRow, candidate, room.Value);
        }

        // A new page: listed in the free-space map once the value is on it, if it still has room (NoteRoomLeft).
        int page = _allocator.AllocateLvalPage();
        UsageMap.AddPage(_file, _allocator, _umap.OwnedPage, _umap.OwnedRow, page);
        return page;
    }
    /// <summary>
    /// A page for one chunk of a chain and nothing else: an emptied one - the column's free-space map lists such a page,
    /// so one emptied in an earlier session is found too (#58) - or a new one.
    /// </summary>
    private int PageOfItsOwn()
    {
        var notes = _file.FreedLvalPages;
        LookAtTheMapOnce();
        while (notes.TryFindWithExactly(_umap.OwnedPage, _umap.OwnedRow, _format.DataPageInitialFreeSpace, out int candidate))
        {
            notes.CandidatesVisited++;
            bool empty = RoomOn(candidate, mustBeEmpty: true) is not null;
            Forget(candidate);   // taken, or not what it was noted as; a chain's page is not listed either way
            if (empty) return candidate;
        }

        int page = _allocator.AllocateLvalPage();
        UsageMap.AddPage(_file, _allocator, _umap.OwnedPage, _umap.OwnedRow, page);
        return page;
    }

    /// <summary>
    /// The room on <paramref name="page"/> for another row, or null when it is not a long-value page of this
    /// column, holds as many rows as a page can, or - when it <paramref name="mustBeEmpty"/> - holds any row.
    /// </summary>
    private int? RoomOn(int page, bool mustBeEmpty = false)
    {
        if (page <= 0 || page >= _file.PageCount || !UsageMap.Contains(_file, _umap.OwnedPage, _umap.OwnedRow, page))
            return null;
        byte[] dp = _file.ReadPage(page);
        if (dp[0] != JetFormat.PageTypeData || dp[4] != (byte)'L' || dp[5] != (byte)'V' || dp[6] != (byte)'A' || dp[7] != (byte)'L')
            return null;
        int rows = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);
        if (rows >= DataPageWriter.MaxRowsPerPage || mustBeEmpty && rows != 0) return null;

        // A page with what may be the end of a chain on it takes no other value: Access gives back every page of
        // a chain it frees, with whatever else is on them. This placement puts nothing beside a chain's chunk, but
        // a file an earlier release wrote has such pages, and lists them as having room.
        for (int r = 0; r < rows; r++)
        {
            ushort slot = ByteUtil.GetUShort(dp, _format.OffsetDataRowTable + r * JetFormat.SizeRowEntry);
            if ((slot & JetFormat.DeletedRowFlag) != 0) continue;
            int start = slot & JetFormat.RowOffsetMask;
            int end   = r == 0 ? _format.PageSize : ByteUtil.GetUShort(dp, _format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry) & JetFormat.RowOffsetMask;
            if (MayEndAChain(dp, start, end - start)) return null;
        }
        return ByteUtil.GetShort(dp, JetFormat.OffsetDataFreeSpace);
    }

    /// <summary>
    /// Whether a long-value row may be the last chunk of a chain: it begins with where the next chunk is, and for
    /// the last that is page 0. A row does not say which it is, so a value of one chunk that begins the same way -
    /// a byte and three zeros - is taken for one too, and gets a page nothing is added to. Text never does: that
    /// would be a character of zero.
    /// </summary>
    internal static bool MayEndAChain(byte[] bytes, int start, int length)
        => length >= 4 && bytes[start + 1] == 0 && bytes[start + 2] == 0 && bytes[start + 3] == 0;

    /// <summary>
    /// After a one-chunk value went onto <paramref name="page"/>, which then had <paramref name="room"/> bytes
    /// free and <paramref name="rows"/> rows: the page is noted with its room and listed in the column's free-space
    /// map while another value could go onto it, and leaves both when none could. The map is written only when the
    /// page enters or leaves it.
    /// </summary>
    private void NoteRoomLeft(int page, int room, int rows)
    {
        var notes = _file.FreedLvalPages;
        if (rows >= DataPageWriter.MaxRowsPerPage || room < MinListedFreeSpace)
        {
            Forget(page);
            return;
        }
        notes.Set(_umap.OwnedPage, _umap.OwnedRow, page, room);
        if (_umap.FreePage > 0 && !notes.IsListed(_umap.OwnedPage, _umap.OwnedRow, page))
        {
            UsageMap.AddPage(_file, _allocator, _umap.FreePage, _umap.FreeRow, page);
            notes.SetListed(_umap.OwnedPage, _umap.OwnedRow, page, true);
        }
    }

    /// <summary>Takes <paramref name="page"/> out of the notes and, if it is there, out of the column's free-space map.</summary>
    private void Forget(int page)
    {
        var notes = _file.FreedLvalPages;
        notes.Set(_umap.OwnedPage, _umap.OwnedRow, page, 0);
        if (_umap.FreePage > 0 && notes.IsListed(_umap.OwnedPage, _umap.OwnedRow, page))
        {
            UsageMap.RemovePage(_file, _umap.FreePage, _umap.FreeRow, page);
            notes.SetListed(_umap.OwnedPage, _umap.OwnedRow, page, false);
        }
    }
    // ── The way of earlier releases (Database.ShareLongValuePages) ───────────

    /// <summary>
    /// Chooses the LVAL pages a value's chunk rows go to, and the row each takes there: pages of this column whose
    /// space a deleted value freed earlier in the session (<see cref="PageFile.FreedLvalPages"/>), then the column's
    /// last page, and a new page when none has room. A freed page is read only when it was last seen with room for the
    /// chunk, at most once a value, and the room left on the pages the column has noted is noted again when the value
    /// is written. Looking through every
    /// page the column owned for room read the whole column on every write, so each write was slower than the last;
    /// the freed pages keep what that bought - a deleted value's space is used again.
    /// </summary>
    private sealed class PagePicker
    {
        private readonly LvalWriter _writer;
        private IEnumerator<KeyValuePair<int, int>>? _freed;   // this column's freed pages, lowest first, and their room
        private int _freedWalkedFor = int.MaxValue;            // the smallest chunk they have been walked for
        private readonly HashSet<int> _freedRead = new();      // the freed pages read
        private readonly List<int> _open = new();              // pages read or allocated, with room left
        private readonly Dictionary<int, int> _free = new();
        private readonly Dictionary<int, int> _rows = new();
        private bool _lastPageRead;

        public PagePicker(LvalWriter writer)
        {
            _writer = writer;
        }

        /// <summary>A page with room for a chunk row of <paramref name="needed"/> bytes, its slot included, and the
        /// index the row takes there - allocating the page when none of the candidates has room.</summary>
        public (int page, int row) Reserve(int needed)
        {
            int target = OpenPageWithRoom(needed);
            if (target < 0) target = FreedPageWithRoom(needed);
            if (target < 0) target = LastPageWithRoom(needed);
            if (target < 0) target = NewPage();

            int row = _rows[target];
            _rows[target] = row + 1;
            _free[target] -= needed;
            if (_free[target] < FreedLvalPages.MinUsefulFreeSpace)
                _open.Remove(target);
            return (target, row);
        }

        private bool HasRoom(int page, int needed)
            => _free[page] >= needed && _rows[page] < DataPageWriter.MaxRowsPerPage;

        private int OpenPageWithRoom(int needed)
        {
            foreach (int page in _open)
            {
                if (HasRoom(page, needed))
                    return page;
            }
            return -1;
        }

        // A value's chunks never grow - full ones, then the last - so the freed pages are walked once for the full
        // chunks, and again for a last chunk smaller than them: a page passed over as too small for a full chunk may take
        // the last. Nothing is noted while a value is planned, so a walk sees the records as the first one did, and a
        // page already read is not read again.
        private int FreedPageWithRoom(int needed)
        {
            if (_freed is null)
                NoteFreeSpaceMap();
            if (_freed is null || needed < _freedWalkedFor)
            {
                _freed = _writer._file.FreedLvalPages.Of(_writer._umap.OwnedPage, _writer._umap.OwnedRow).GetEnumerator();
                _freedWalkedFor = needed;
            }
            while (_freed.MoveNext())
            {
                (int page, int room) = (_freed.Current.Key, _freed.Current.Value);
                if (room < needed || _free.ContainsKey(page) || !_freedRead.Add(page))
                    continue;
                // Freed from a value of this column, so on its map - unless the file says otherwise.
                if (UsageMap.Contains(_writer._file, _writer._umap.OwnedPage, _writer._umap.OwnedRow, page)
                    && Read(page) && HasRoom(page, needed))
                    return page;
            }
            return -1;
        }

        // The first look in a session notes the pages the column's free-space map lists - room a delete left in an
        // earlier session - to be read for their room when a chunk could fit.
        private void NoteFreeSpaceMap()
        {
            var umap = _writer._umap;
            var records = _writer._file.FreedLvalPages;
            if (umap.FreePage <= 0 || !records.FirstLookFor(umap.OwnedPage, umap.OwnedRow))
                return;
            var noted = new HashSet<int>(records.Of(umap.OwnedPage, umap.OwnedRow).Select(p => p.Key));
            byte[] mapPage = _writer._file.ReadPage(umap.FreePage);
            foreach (int page in UsageMap.GetOwnedPages(mapPage, umap.FreeRow, _writer._format, _writer._file))
                if (!noted.Contains(page))
                    records.Set(umap.OwnedPage, umap.OwnedRow, page, FreedLvalPages.NotYetRead);
        }

        private int LastPageWithRoom(int needed)
        {
            if (_lastPageRead)
                return -1;
            _lastPageRead = true;
            int last = UsageMap.GetLastPage(_writer._file, _writer._umap.OwnedPage, _writer._umap.OwnedRow);
            return last >= 0 && (_free.ContainsKey(last) || Read(last)) && HasRoom(last, needed) ? last : -1;
        }

        private int NewPage()
        {
            // A fresh LVAL data page (it carries the "LVAL" signature Access requires at bytes 4-7).
            // It is not added to the column's free-space map: the column's last page is always
            // looked at, and only the pages a delete freed are listed there.
            int page = _writer._allocator.AllocateLvalPage();
            UsageMap.AddPage(_writer._file, _writer._allocator, _writer._umap.OwnedPage, _writer._umap.OwnedRow, page);
            _free[page] = _writer._format.DataPageInitialFreeSpace;
            _rows[page] = 0;
            _open.Add(page);
            return page;
        }

        /// <summary>
        /// Reads the room and rows of <paramref name="page"/> - false, with nothing noted, unless it is a long-value
        /// page: type 0x01 with "LVAL" at bytes 4-7, as AllocateLvalPage writes one. A map that lists any other page -
        /// another table's data page - must not have chunks written onto it.
        /// </summary>
        private bool Read(int page)
        {
            byte[] dp = _writer._file.ReadPage(page);
            if (dp[0] != JetFormat.PageTypeData || dp[4] != (byte)'L' || dp[5] != (byte)'V' || dp[6] != (byte)'A' || dp[7] != (byte)'L')
                return false;
            _free[page] = ByteUtil.GetShort(dp, JetFormat.OffsetDataFreeSpace);
            _rows[page] = ByteUtil.GetShort(dp, _writer._format.OffsetDataNumRows);
            if (_free[page] >= FreedLvalPages.MinUsefulFreeSpace)
                _open.Add(page);
            return true;
        }

        /// <summary>
        /// Once the value's chunks are written: forgets a freed page that was not this column's long-value page, and
        /// notes the room left on every page read or written that the column has noted - a freed page reached as the
        /// column's last page too - as none on a page holding as many rows as a page can.
        /// </summary>
        public void Done()
        {
            var umap = _writer._umap;
            var records = _writer._file.FreedLvalPages;
            var noted = new HashSet<int>(records.Of(umap.OwnedPage, umap.OwnedRow).Select(p => p.Key));
            foreach (int page in _freedRead)
            {
                if (!_free.ContainsKey(page))
                    Forget(page);
            }
            foreach (var entry in _free)
            {
                int room = _rows[entry.Key] < DataPageWriter.MaxRowsPerPage ? entry.Value : 0;
                if (room < FreedLvalPages.MinUsefulFreeSpace && noted.Contains(entry.Key))
                    Forget(entry.Key);
                else
                    records.Renew(umap.OwnedPage, umap.OwnedRow, entry.Key, room);
            }

            // A page with no room worth reading, or not this column's, leaves the notes and the free-space map.
            void Forget(int page)
            {
                records.Set(umap.OwnedPage, umap.OwnedRow, page, 0);
                if (umap.FreePage > 0)
                    UsageMap.RemovePage(_writer._file, umap.FreePage, umap.FreeRow, page);
            }
        }
    }

    internal const int LvalTypeThisPage   = unchecked((int)0x80000000);
    internal const int LvalTypeOtherPage  = 0x40000000;
    internal const int LvalTypeOtherPages = 0x00000000;
    internal const int LvalLengthMask     = 0x3FFFFFFF;

    /// <summary>Where a long-value page keeps the stamp a reference to a chain starting on it must carry.</summary>
    internal const int OffsetLvalPageStamp = 8;

    /// <summary>
    /// The 12-byte reference a row holds to a long value on pages of its own. Its last four bytes are a stamp:
    /// for a chain of chunks (OTHER_PAGES) Access compares them with the four bytes at
    /// <see cref="OffsetLvalPageStamp"/> of the page the chain starts on, and will not read the value when they
    /// differ ("the provider could not determine the value"; in Access itself, "you and another user are attempting
    /// to change the same data"). Access writes a number of its own there for each such value; a page this library
    /// makes has zeros, and so had every reference it wrote - which was wrong as soon as a chain started on a page
    /// Access had stamped and a delete or update had since emptied (qcatco/exportmdb.interop#60). The reference
    /// carries whatever the page has. A value in one chunk (OTHER_PAGE) has zeros, as Access writes it, and Access
    /// does not compare them.
    /// </summary>
    private static byte[] BuildLvRef(int totalLen, int typeFlags, int page, int row, int stamp)
    {
        var lvRef = new byte[12];
        ByteUtil.PutInt(lvRef, 0, unchecked(totalLen | typeFlags));
        lvRef[4] = (byte)row;
        lvRef[5] = (byte) page;
        lvRef[6] = (byte)(page >> 8);
        lvRef[7] = (byte)(page >> 16);
        ByteUtil.PutInt(lvRef, 8, stamp);
        return lvRef;
    }

    // Appends one chunk row to the GIVEN LVAL page; returns the row index, and the page's stamp.
    private int WriteChunkRowOnPage(int lvalPage, byte[] chunk, out int pageStamp) => WriteChunkRowOnPage(lvalPage, chunk, out pageStamp, out _);

    // As above, also saying how many bytes the page has free once the row is on it.
    private int WriteChunkRowOnPage(int lvalPage, byte[] chunk, out int pageStamp, out int roomLeft)
    {
        int needed = chunk.Length + JetFormat.SizeRowEntry;

        byte[] page      = _file.ReadPage(lvalPage);
        pageStamp        = ByteUtil.GetInt(page, OffsetLvalPageStamp);
        int    rowCount  = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
        int    freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
        if (freeSpace < needed || rowCount >= DataPageWriter.MaxRowsPerPage)
            throw new InvalidOperationException(
                $"LVAL page {lvalPage} has {freeSpace} free bytes and {rowCount} rows; a {chunk.Length}-byte chunk does not fit.");
        int    cursor    = _format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry + freeSpace;
        int    rowStart  = cursor - chunk.Length;

        Array.Copy(chunk, 0, page, rowStart, chunk.Length);

        ByteUtil.PutShort(page,
            _format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry,
            (short)rowStart);
        ByteUtil.PutShort(page, _format.OffsetDataNumRows,   (short)(rowCount + 1));
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)(freeSpace - needed));
        roomLeft = freeSpace - needed;

        _file.WritePage(lvalPage, page);
        return rowCount;   // rowCount was the 0-based index before increment
    }

    private static byte[] BuildOtherPageLvRef(int totalLen, int page, int row)
    {
        var lvRef = new byte[9];
        ByteUtil.PutInt(lvRef, 0, totalLen);   // 4-byte LE total data length
        lvRef[4] = 0x40;                        // OTHER_PAGE marker
        lvRef[5] = (byte) page;
        lvRef[6] = (byte)(page >>  8);
        lvRef[7] = (byte)(page >> 16);
        lvRef[8] = (byte) row;
        return lvRef;
    }
}

/// <summary>
/// Reads long-value data from LVAL data pages by following the chunk chain forward.
/// </summary>
internal sealed class LvalReader
{
    private readonly PageFile  _file;
    private readonly JetFormat _format;

    public LvalReader(PageFile file)
    {
        _file   = file ?? throw new ArgumentNullException(nameof(file));
        _format = file.Format;
    }

    /// <summary>
    /// Reconstructs the LVAL value starting at (<paramref name="lvalPage"/>,
    /// <paramref name="lvalRow"/>). For OTHER_PAGE (<paramref name="chained"/>
    /// false) the single chunk row IS the data; for OTHER_PAGES each chunk row
    /// is [nextRow:1][nextPage:3][payload] with nextPage == 0 ending the chain.
    /// <para>
    /// A value that is not where its reference says does not read: a page that is not a data page, a row the page
    /// does not have or has deleted, one chunk shorter than the value, a chain that ends before it. Such a reference
    /// is one a reader held while another program changed the file (#62) - the row read a moment before the change -
    /// or a damaged file's, and was read as another value's bytes, the old bytes of one deleted, or zeros.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidDataException">The value is not where its reference says.</exception>
    public byte[] Read(int lvalPage, int lvalRow, int totalLen, bool chained)
    {
        var result = new byte[totalLen];
        if (totalLen == 0) return result;

        int written = 0;
        int curPage = lvalPage;
        int curRow  = lvalRow;

        while (written < totalLen)
        {
            if (curPage <= 0 || curPage >= _file.PageCount)
                throw NotWhereItSays(lvalPage, lvalRow, $"it goes on to page {curPage}, which the file does not have");
            byte[] page = _file.ReadPage(curPage);
            if (page[0] != JetFormat.PageTypeData)
                throw NotWhereItSays(lvalPage, lvalRow, $"page {curPage} is not a page of values");
            int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

            if (curRow >= rowCount)
                throw NotWhereItSays(lvalPage, lvalRow, $"page {curPage} has {rowCount} rows, and row {curRow} is asked for");
            if ((ByteUtil.GetUShort(page, _format.OffsetDataRowTable + curRow * JetFormat.SizeRowEntry) & JetFormat.DeletedRowFlag) != 0)
                throw NotWhereItSays(lvalPage, lvalRow, $"row {curRow} of page {curPage} has been deleted");

            (int rowStart, int rowEnd) = GetRowBounds(page, curRow, rowCount);
            int rowLen = rowEnd - rowStart;

            if (!chained)
            {
                if (rowLen < totalLen)
                    throw NotWhereItSays(lvalPage, lvalRow, $"its row holds {Math.Max(rowLen, 0)} bytes of a value of {totalLen}");
                Array.Copy(page, rowStart, result, 0, totalLen);
                break;
            }

            if (rowLen < 4)
                throw NotWhereItSays(lvalPage, lvalRow,
                    $"row {curRow} of page {curPage}, a piece of its chain, is {Math.Max(rowLen, 0)} bytes, fewer than the 4 that say where the next piece is");

            int nextRow  = page[rowStart];
            int nextPage = page[rowStart + 1] | (page[rowStart + 2] << 8) | (page[rowStart + 3] << 16);
            int copyLen  = Math.Min(rowLen - 4, totalLen - written);
            Array.Copy(page, rowStart + 4, result, written, copyLen);
            written += copyLen;

            if (nextPage == 0)
            {
                if (written < totalLen)
                    throw NotWhereItSays(lvalPage, lvalRow, $"its chain ends after {written} bytes of a value of {totalLen}");
                break;
            }
            curPage = nextPage;
            curRow  = nextRow;
        }

        return result;
    }

    private static InvalidDataException NotWhereItSays(int page, int row, string why)
        => new($"The long value at row {row} of page {page} is not where its reference says: {why}. A database opened only to be read " +
               "reads a row as another program changes it, and can hold a reference that is no longer the row's; otherwise the file is damaged.");

    private (int start, int end) GetRowBounds(byte[] page, int rowIndex, int rowCount)
    {
        int slotOff  = _format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = rowIndex == 0
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowIndex - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        return (rowStart, rowEnd);
    }
}

/// <summary>
/// Frees (marks as deleted) a chain of LVAL chunks, then compacts each affected LVAL page
/// by reclaiming space from any deleted tail rows.
///
/// Tail compaction: rows are packed from the end of the page (row 0 = highest address,
/// row rowCount-1 = lowest address = most recently inserted).  If the most recently
/// inserted row is deleted we can shrink rowCount and increase freeSpace, making that
/// space immediately available to the next write.  We repeat until the last row is live.
///
/// Because LvalWriter writes chunks in REVERSE order, chunk 0 (first in the chain) is
/// always the LAST row written to its LVAL page and therefore the most eligible for tail
/// compaction.  Following the forward chain (chunk 0 → 1 → … → last) and compacting each
/// page in turn fully reclaims the space when all chunks were the sole occupants of their
/// respective pages.
/// </summary>
internal sealed class LvalFree
{
    private readonly PageFile  _file;
    private readonly JetFormat _format;

    public LvalFree(PageFile file)
    {
        _file   = file ?? throw new ArgumentNullException(nameof(file));
        _format = file.Format;
    }

    /// <summary>
    /// Marks every chunk starting at (<paramref name="lvalPage"/>,
    /// <paramref name="lvalRow"/>) as deleted and tail-compacts each page.
    /// <paramref name="chained"/> selects OTHER_PAGES chain walking
    /// ([nextRow:1][nextPage:3] prefix, nextPage == 0 ends) vs a single
    /// OTHER_PAGE chunk. A page left with more room is noted in <see cref="PageFile.FreedLvalPages"/> for the column
    /// whose usage maps <paramref name="owner"/> names, when it has them, and added to that column's free-space map
    /// as Access lists such a page.
    /// </summary>
    public void FreeChain(int lvalPage, int lvalRow, bool chained, LvalUmapRef? owner)
    {
        int curPage = lvalPage;
        int curRow  = lvalRow;

        while (true)
        {
            byte[] page     = _file.ReadPage(curPage);
            int    rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

            if (curRow >= rowCount) break;   // safety: malformed chain

            (int rowStart, int rowEnd) = GetRowBounds(page, curRow, rowCount);
            int rowLen = rowEnd - rowStart;

            // Read chain prefix BEFORE modifying the page.
            int  nextPage = 0, nextRow = 0;
            bool hasNext  = false;
            if (chained && rowLen >= 4)
            {
                nextRow  = page[rowStart];
                nextPage = page[rowStart + 1] | (page[rowStart + 2] << 8) | (page[rowStart + 3] << 16);
                hasNext  = nextPage != 0;
            }

            // Mark this chunk's slot as deleted.
            int slotOff = _format.OffsetDataRowTable + curRow * JetFormat.SizeRowEntry;
            ByteUtil.PutUShort(page, slotOff,
                (ushort)(ByteUtil.GetUShort(page, slotOff) | JetFormat.DeletedRowFlags));

            short freeBefore = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
            TrimDeletedTail(page);

            _file.WritePage(curPage, page);
            short freeAfter = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
            int listedFrom = _file.ShareLongValuePages ? FreedLvalPages.MinUsefulFreeSpace : LvalWriter.MinListedFreeSpace;
            if (owner is { } column && freeAfter > freeBefore)
            {
                _file.FreedLvalPages.Set(column.OwnedPage, column.OwnedRow, curPage, freeAfter >= listedFrom ? freeAfter : 0);
                if (column.FreePage > 0 && freeAfter >= listedFrom)
                {
                    UsageMap.AddPage(_file, new PageAllocator(_file), column.FreePage, column.FreeRow, curPage);
                    _file.FreedLvalPages.SetListed(column.OwnedPage, column.OwnedRow, curPage, true);
                }
            }

            if (!hasNext) break;
            curPage = nextPage;
            curRow  = nextRow;
        }
    }

    // Pops deleted rows from the high-slot-index end of the page (the "bottom" of the
    // data area), increasing freeSpace for each reclaimed row.
    private void TrimDeletedTail(byte[] page)
    {
        int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

        while (rowCount > 0)
        {
            int    slotOff = _format.OffsetDataRowTable + (rowCount - 1) * JetFormat.SizeRowEntry;
            ushort slotVal = ByteUtil.GetUShort(page, slotOff);
            if ((slotVal & 0x8000) == 0) break;   // last row is live — stop

            // Row (rowCount-1):
            //   start = slotVal & 0x1FFF
            //   end   = slot[rowCount-2] (or pageSize for the only row)
            int rowStart = slotVal & JetFormat.RowOffsetMask;
            int rowEnd   = rowCount == 1
                ? _format.PageSize
                : (ByteUtil.GetUShort(page,
                       _format.OffsetDataRowTable + (rowCount - 2) * JetFormat.SizeRowEntry)
                   & JetFormat.RowOffsetMask);
            int rowLen = rowEnd - rowStart;

            rowCount--;
            ByteUtil.PutShort(page, _format.OffsetDataNumRows, (short)rowCount);

            short freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
            ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace,
                (short)(freeSpace + rowLen + JetFormat.SizeRowEntry));
        }
    }

    private (int start, int end) GetRowBounds(byte[] page, int rowIndex, int rowCount)
    {
        int slotOff  = _format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = rowIndex == 0
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowIndex - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        return (rowStart, rowEnd);
    }
}
