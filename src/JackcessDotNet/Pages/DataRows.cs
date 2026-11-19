using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// A row's bytes, found from its slot on a data page - the one place a row's slot is read, so every reader takes
/// the same rows to be there.
/// <para>
/// A slot's two top bits say what is at it. Neither: the row. The first (0x8000) alone: the values of a row whose
/// slot is elsewhere, kept out of a scan - or, in files this library wrote before, a deleted row. Both: a deleted
/// row. The second (0x4000) alone: an overflow row. Access writes one when an update makes a row too long for the
/// room its page has: the row's values go to another page, and its slot - which every index entry and every
/// pointer to the row still names - keeps four bytes saying where, a row number and a page. Such a row is one of
/// the table's like any other, read by following the pointer; passed over, as it was, it was missing from the
/// table, could not be found to update or delete, and was not there for a foreign key to be checked against
/// (qcatco/exportmdb.interop#63).
/// </para>
/// </summary>
internal static class DataRows
{
    /// <summary>How many pointers are followed from one slot: a row moved more often than that is a loop.</summary>
    private const int MaxOverflowHops = 16;

    /// <summary>
    /// The bytes of the row whose slot is <paramref name="rowIndex"/> on <paramref name="page"/>, following the
    /// pointer of an overflow row to where its values are; null when no row is there. Only an overflow row costs
    /// a page read.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// An overflow pointer leads outside the file, to a page that is not one of the row's table, or past that
    /// page's rows: the row is there and cannot be read, which is not the same as no row.
    /// </exception>
    public static byte[]? Read(PageFile file, byte[] page, int rowIndex) => Read(file, page, rowIndex, null);

    /// <summary>
    /// As <see cref="Read(PageFile, byte[], int)"/>, noting in <paramref name="moved"/> every slot an overflow
    /// pointer led to - the last holds the row's values. They carry the mark of values kept out of a scan (0x8000):
    /// when the row is deleted, or its values are written elsewhere, they are emptied and marked deleted
    /// (<see cref="DataPageWriter"/>).
    /// </summary>
    public static byte[]? Read(PageFile file, byte[] page, int rowIndex, List<(int Page, int Row)>? moved)
    {
        var format = file.Format;
        if (rowIndex < 0 || rowIndex >= ByteUtil.GetShort(page, format.OffsetDataNumRows)) return null;
        ushort slot = Slot(page, rowIndex, format);
        if ((slot & JetFormat.DeletedRowFlag) != 0) return null;

        for (int hops = 0; ; hops++)
        {
            int start = slot & JetFormat.RowOffsetMask;
            int end   = rowIndex == 0
                ? format.PageSize
                : Slot(page, rowIndex - 1, format) & JetFormat.RowOffsetMask;
            if (end > format.PageSize || start > end) return null;

            if ((slot & JetFormat.OverflowRowFlag) == 0)
                return end - start <= 0 ? null : page.Slice(start, end);

            // An overflow row: [row:1][page:3], where its values are. The slot there is marked deleted so that a
            // scan passes it; that mark is not looked at again here.
            if (end - start < 4 || hops >= MaxOverflowHops) return null;
            int toRow  = page[start];
            int toPage = ByteUtil.Get3ByteInt(page, start + 1);
            if (toPage <= 0 || toPage >= file.PageCount)
                throw new InvalidDataException(
                    $"The file is damaged: a row's values were moved to page {toPage}, and the file has no such page.");
            int table = ByteUtil.GetInt(page, JetFormat.OffsetDataTdefPage);
            page = file.ReadPage(toPage);
            if (page[0] != JetFormat.PageTypeData || ByteUtil.GetInt(page, JetFormat.OffsetDataTdefPage) != table)
                throw new InvalidDataException(
                    $"The file is damaged: a row's values were moved to page {toPage}, which is not a page of its table.");
            if (toRow >= ByteUtil.GetShort(page, format.OffsetDataNumRows))
                throw new InvalidDataException(
                    $"The file is damaged: a row's values were moved to row {toRow} of page {toPage}, which has no such row.");
            moved?.Add((toPage, toRow));
            rowIndex = toRow;
            slot = (ushort)(Slot(page, rowIndex, format) & ~JetFormat.DeletedRowFlag);
        }
    }

    private static ushort Slot(byte[] page, int rowIndex, JetFormat format)
        => ByteUtil.GetUShort(page, format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry);
}
