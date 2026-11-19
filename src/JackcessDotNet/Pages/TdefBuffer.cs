using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// A table's definition as one run of bytes, whatever number of pages it is on, as Jackcess reads and writes it
/// (<c>TableImpl.loadCompleteTableDefinitionBuffer</c>, <c>writeTableDefinitionBuffer</c>) and as Access was seen
/// to keep it (#37).
/// <para>
/// A definition too long for a page goes on to another, named in bytes 4-7 of the page before it. Each page after
/// the first has eight bytes of its own - the page type 0x02, 0x01, the page's free space and the next page - and
/// then the definition where the last page left it. The definition's length, in its bytes 8-11, counts the first
/// page from its start and the others without their eight bytes. So the bytes here are the first page whole, then
/// each later page from its byte 8: an offset into them is an offset into the definition.
/// </para>
/// <para>
/// How many pages a definition is on is Access's to say, and it asks for eight bytes more than the length: a
/// definition is on the fewest pages that hold its length and eight - the first holding a page, each one after it
/// eight less - so one that ends within eight bytes of the end of a page has another page after it, with nothing
/// of the definition on it. (Measured: of the 75 tables DAO made in each of an .mdb, an Access 2007 .accdb and an
/// Access 2010 .accdb, with definitions from twelve bytes short of the first, second and third page's end to
/// twelve past it, those of 4,089 to 4,096 bytes have a second page, of 8,177 to 8,184 a third, of 12,265 to
/// 12,272 a fourth.) Jackcess puts such a definition on the pages it fills, one fewer, and Access opens nothing
/// of a file with one written that way: "Unrecognized database format". The free space of the last page is what
/// the pages hold less the length and eight - 4,087 for a definition of 4,089 bytes on two pages, 4 for one of
/// 4,084 on one - and of every page before it 0.
/// </para>
/// </summary>
internal static class TdefBuffer
{
    private const int OffsetFreeSpace = 2;
    private const int OffsetNextPage  = 4;
    private const int OffsetLength    = 8;
    private const int PageHeaderSize  = 8;

    /// <summary>What a definition's first page becomes when Access drops its table: no longer a definition page.</summary>
    internal const byte PageTypeDroppedTableDef = 0x08;

    /// <summary>
    /// The definition that starts on page <paramref name="firstPage"/>: that page, and the bytes of every page
    /// it goes on to.
    /// </summary>
    internal static byte[] Read(PageFile file, int firstPage) => Read(file, firstPage, out _, out _);

    /// <summary>As <see cref="Read(PageFile, int)"/>, with the pages after the first that the definition is on, in order.</summary>
    internal static byte[] Read(PageFile file, int firstPage, out List<int> laterPages) => Read(file, firstPage, out laterPages, out _);

    /// <summary>
    /// As <see cref="Read(PageFile, int)"/>, saying in <paramref name="isBroken"/> whether the pages stop short:
    /// one names as its next a page the file has not, a page already read, or a page that is no definition page.
    /// What was read up to there is returned, so the table can still be opened and what the first pages say of
    /// it read; it is then a table whose definition was not read whole, which nothing is written to.
    /// </summary>
    internal static byte[] Read(PageFile file, int firstPage, out List<int> laterPages, out bool isBroken)
    {
        int    pageSize = file.Format.PageSize;
        byte[] first    = file.ReadPage(firstPage);
        laterPages = new List<int>();
        isBroken   = false;

        int next = ByteUtil.GetInt(first, OffsetNextPage);
        if (next == 0 || first[0] != JetFormat.PageTypeTableDef) return first;

        var parts = new List<byte[]>();
        var seen  = new HashSet<int> { firstPage };
        while (next != 0)
        {
            byte[]? page = next > 0 && next < file.PageCount && seen.Add(next) ? file.ReadPage(next) : null;
            if (page is null || page[0] != JetFormat.PageTypeTableDef)
            {
                isBroken = true;
                break;
            }
            laterPages.Add(next);
            parts.Add(page);
            next = ByteUtil.GetInt(page, OffsetNextPage);
        }

        var whole = new byte[pageSize + parts.Count * (pageSize - PageHeaderSize)];
        Array.Copy(first, whole, pageSize);
        for (int i = 0; i < parts.Count; i++)
            Array.Copy(parts[i], PageHeaderSize, whole, pageSize + i * (pageSize - PageHeaderSize), pageSize - PageHeaderSize);
        return whole;
    }

    /// <summary>The length a definition says it has, when that is a length <paramref name="definition"/> can have; else all of it.</summary>
    internal static int LengthOf(byte[] definition, JetFormat format)
    {
        int length = ByteUtil.GetInt(definition, OffsetLength);
        return length >= format.SizeTdefHeader && length <= definition.Length ? length : definition.Length;
    }

    /// <summary>
    /// Writes <paramref name="definition"/> - a definition whose bytes 8-11 are its length - to the table whose
    /// definition starts on <paramref name="firstPage"/>: on that page and, when Access would have it on more
    /// (the class summary says when), on the pages the definition was on before, in their order, and on new ones
    /// after them. A page the definition no longer needs is given back to the file
    /// (<see cref="GlobalUsageMap.MarkFree"/>), as Access gives it back.
    /// The caller holds the undo scope: several pages are written.
    /// </summary>
    internal static void Write(PageFile file, PageAllocator allocator, int firstPage, byte[] definition)
    {
        var format   = file.Format;
        int pageSize = format.PageSize;
        int length   = ByteUtil.GetInt(definition, OffsetLength);
        if (length < format.SizeTdefHeader || length > definition.Length)
            throw new InvalidOperationException($"A table definition of {definition.Length} bytes says it is {length} long.");

        // The pages the definition is on now, to be used again.
        var reusable = new Queue<int>();
        if (firstPage < file.PageCount)
        {
            byte[] current = file.ReadPage(firstPage);
            if (current[0] == JetFormat.PageTypeTableDef && ByteUtil.GetInt(current, OffsetNextPage) != 0)
            {
                Read(file, firstPage, out var later);
                foreach (int page in later) reusable.Enqueue(page);
            }
        }

        // The fewest pages that hold the length and eight bytes more, and what they hold between them.
        int laterRoom = pageSize - PageHeaderSize;
        int pages     = length + PageHeaderSize <= pageSize ? 1 : 1 + (length + PageHeaderSize - pageSize + laterRoom - 1) / laterRoom;
        int capacity  = pageSize + (pages - 1) * laterRoom;

        int written    = 0;
        int pageNumber = firstPage;
        for (int i = 0; i < pages; i++)
        {
            var page = new byte[pageSize];
            int room = i == 0 ? pageSize : laterRoom;
            int take = Math.Max(Math.Min(room, length - written), 0);   // none, on a last page Access asks for and the length does not reach
            if (i == 0)
            {
                Array.Copy(definition, 0, page, 0, take);
            }
            else
            {
                page[0] = JetFormat.PageTypeTableDef;
                page[1] = 0x01;
                Array.Copy(definition, written, page, PageHeaderSize, take);
            }
            written += take;

            bool isLast = i == pages - 1;
            int next = isLast ? 0 : reusable.Count > 0 ? reusable.Dequeue() : allocator.AllocatePage();
            ByteUtil.PutInt(page, OffsetNextPage, next);
            ByteUtil.PutShort(page, OffsetFreeSpace, (short)(isLast ? capacity - length - PageHeaderSize : 0));
            file.WritePage(pageNumber, page);
            pageNumber = next;
        }

        while (reusable.Count > 0)
            GlobalUsageMap.MarkFree(file, reusable.Dequeue());
    }
}
