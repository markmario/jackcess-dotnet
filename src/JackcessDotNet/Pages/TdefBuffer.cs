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
/// page from its start and the others without their eight bytes; a page that is full has free space 0, and the
/// last has what is left of it less eight. So the bytes here are the first page whole, then each later page from
/// its byte 8: an offset into them is an offset into the definition.
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
    /// <exception cref="InvalidDataException">The pages lead back to one already read, or to no definition page.</exception>
    internal static byte[] Read(PageFile file, int firstPage) => Read(file, firstPage, out _);

    /// <summary>As <see cref="Read(PageFile, int)"/>, with the pages after the first that the definition is on, in order.</summary>
    internal static byte[] Read(PageFile file, int firstPage, out List<int> laterPages)
    {
        int    pageSize = file.Format.PageSize;
        byte[] first    = file.ReadPage(firstPage);
        laterPages = new List<int>();

        int next = ByteUtil.GetInt(first, OffsetNextPage);
        if (next == 0 || first[0] != JetFormat.PageTypeTableDef) return first;

        var parts = new List<byte[]>();
        var seen  = new HashSet<int> { firstPage };
        while (next != 0)
        {
            if (next < 0 || next >= file.PageCount || !seen.Add(next))
                throw new InvalidDataException(
                    $"The table definition on page {firstPage} goes on to page {next}, which is not a page it can be on.");
            byte[] page = file.ReadPage(next);
            if (page[0] != JetFormat.PageTypeTableDef)
                throw new InvalidDataException(
                    $"The table definition on page {firstPage} goes on to page {next}, which is not a definition page.");
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
    /// definition starts on <paramref name="firstPage"/>: on that page and, when it is longer than a page, on the
    /// pages the definition was on before, in their order, and on new ones after them. A page the definition no
    /// longer reaches is given back to the file (<see cref="GlobalUsageMap.MarkFree"/>), as Access gives it back.
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

        int written    = 0;
        int pageNumber = firstPage;
        bool isFirst   = true;
        while (true)
        {
            var page = new byte[pageSize];
            int room = isFirst ? pageSize : pageSize - PageHeaderSize;
            int take = Math.Min(room, length - written);
            if (isFirst)
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

            int next = 0;
            if (written < length)
                next = reusable.Count > 0 ? reusable.Dequeue() : allocator.AllocatePage();
            ByteUtil.PutInt(page, OffsetNextPage, next);
            // What is left of the page, less eight, as Access and Jackcess count it; 0 for a page that is full.
            ByteUtil.PutShort(page, OffsetFreeSpace, (short)Math.Max(room - take - PageHeaderSize, 0));
            file.WritePage(pageNumber, page);

            if (next == 0) break;
            pageNumber = next;
            isFirst    = false;
        }

        while (reusable.Count > 0)
            GlobalUsageMap.MarkFree(file, reusable.Dequeue());
    }
}
