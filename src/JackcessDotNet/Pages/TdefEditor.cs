using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Changes a table's definition where it is: a column, an index's block or an index's slot is put into the bytes of
/// the definition page among those already there, as Jackcess's <c>TableImpl.mutateAddColumn</c>,
/// <c>mutateAddIndexData</c> and <c>mutateAddIndex</c> do, so everything Access wrote that this library does not
/// model - the counts it keeps for each index, a slot's foreign-key reference, a column's bytes of unknown meaning -
/// stays as it was. Nothing here writes to the file: each method returns the page as it would be.
/// <para>
/// A definition is changed only when all of it is on one page, before and after: this library reads no more of one
/// (<see cref="TdefReader"/>), so a definition it let run onto a second page would be a table it could no longer
/// write rows to. One that is already longer, or would become so, is refused with
/// <see cref="NotSupportedException"/> (qcatco/exportmdb.interop#37).
/// </para>
/// <para>
/// The layout, as <see cref="TdefReader"/> reads it and Access writes it: the header; a block of counts for each
/// index; the column definitions; the column names; a block for each index's columns, pages and flags; a slot for
/// each index's name and number; the index names; the usage maps of the long-value columns; 0xFFFF. The header's
/// length is of all of it, from the start of the page, and the page's free space (its bytes 2-3) is what is left
/// less eight - both as Access was seen to keep them, and as Jackcess writes them.
/// </para>
/// </summary>
internal sealed class TdefEditor
{
    private const int MagicTableNumber = 1625;
    private const int MagicIndexNumber = 1923;
    private const int OffsetLength     = 8;
    private const int OffsetFreeSpace  = 2;
    private const int OffsetNextPage   = 4;
    private const int LvalEntrySize    = 10;

    /// <summary>A slot's index type: an ordinary index, a primary key, or one side of a relationship.</summary>
    internal const byte IndexTypeForeignKey = 2;

    /// <summary>A slot's "other table" type: this table is the relationship's primary (referenced) table.</summary>
    internal const byte ForeignKeyPrimaryTable = 1;
    /// <summary>A slot's "other table" type: this table is the relationship's secondary (referring) table.</summary>
    internal const byte ForeignKeySecondaryTable = 2;

    private readonly JetFormat _format;
    private readonly string    _table;
    private readonly byte[]    _page;

    // Where each part of the definition starts, and where the last ends.
    private int _length;
    private int _columnCount, _indexCount, _slotCount;
    private int _columnNames, _columnNamesEnd, _slots, _slotNames, _end;

    /// <summary>The names of the index slots, in the order the definition lists them.</summary>
    internal IReadOnlyList<string> SlotNames { get; private set; } = Array.Empty<string>();

    /// <summary>The number each slot's index has, in the order the definition lists them.</summary>
    internal IReadOnlyList<int> SlotNumbers { get; private set; } = Array.Empty<int>();

    /// <summary>The ids of the columns, in the order the definition lists them: Access never gives one twice.</summary>
    internal IReadOnlyList<int> ColumnIds { get; private set; } = Array.Empty<int>();

    /// <summary>How many index blocks the definition has: the number the next one is given.</summary>
    internal int IndexCount => _indexCount;

    /// <summary>The count of columns the table has ever had: the number the next one is given.</summary>
    internal int MaxColumnCount => ByteUtil.GetShort(_page, _format.TdefOffsetNumVarCols - 2);

    /// <summary>The count of variable-length places a row has: the place the next variable-length column is given.</summary>
    internal int MaxVarColumnCount => ByteUtil.GetShort(_page, _format.TdefOffsetNumVarCols);

    /// <summary>
    /// Reads the layout of the definition on <paramref name="page"/>, a copy of which is changed.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The definition runs over more than one page, or is not laid out as its header says: it is not changed.
    /// </exception>
    internal TdefEditor(byte[] page, JetFormat format, string table)
    {
        _format = format;
        _table  = table;
        _page   = (byte[])page.Clone();
        if (format.IsReadOnly)
            throw PageFile.ReadOnlyFormat($"the definition of table '{table}' was not changed.");
        if (page[0] != JetFormat.PageTypeTableDef || ByteUtil.GetInt(page, OffsetNextPage) != 0)
            throw new NotSupportedException(
                $"Table '{table}' is not changed: its definition runs over more than one page, and this library reads " +
                "and writes a definition of one page only. Nothing was changed.");
        Parse();
    }

    private NotSupportedException Unexpected(string what)
        => new($"Table '{_table}' is not changed: its definition is not laid out as this library expects ({what}). Nothing was changed.");

    private void Parse()
    {
        _length      = ByteUtil.GetInt(_page, OffsetLength);
        _columnCount = ByteUtil.GetShort(_page, _format.TdefOffsetNumCols);
        _slotCount   = ByteUtil.GetInt(_page, _format.TdefOffsetNumIndexSlots);
        _indexCount  = ByteUtil.GetInt(_page, _format.TdefOffsetNumIndexes);
        if (_length < _format.SizeTdefHeader || _length > _page.Length || _columnCount < 0 || _slotCount < 0 || _indexCount < 0)
            throw Unexpected("its header's counts");

        int columns = _format.SizeTdefHeader + _indexCount * _format.SizeIndexDefinition;
        _columnNames = columns + _columnCount * _format.SizeColumnHeader;
        if (_columnNames > _length) throw Unexpected("its columns");

        var ids = new List<int>(_columnCount);
        for (int i = 0; i < _columnCount; i++)
            ids.Add(ByteUtil.GetShort(_page, columns + i * _format.SizeColumnHeader + _format.OffsetColumnVarTableIndex + 2));
        ColumnIds = ids;

        int pos = _columnNames;
        for (int i = 0; i < _columnCount; i++) ReadName(ref pos);
        _columnNamesEnd = pos;

        _slots     = _columnNamesEnd + _indexCount * _format.SizeIndexColumnBlock;
        _slotNames = _slots + _slotCount * _format.SizeIndexInfoBlock;
        if (_slotNames > _length) throw Unexpected("its indexes");

        var numbers = new List<int>(_slotCount);
        for (int i = 0; i < _slotCount; i++)
            numbers.Add(ByteUtil.GetInt(_page, _slots + i * _format.SizeIndexInfoBlock + _format.SkipBeforeIndexSlot));
        SlotNumbers = numbers;

        pos = _slotNames;
        var names = new List<string>(_slotCount);
        for (int i = 0; i < _slotCount; i++) names.Add(ReadName(ref pos));
        SlotNames = names;

        // The long-value columns' usage maps, to the 0xFFFF that ends the definition.
        while (true)
        {
            if (pos + 2 > _length) throw Unexpected("its end");
            if (ByteUtil.GetUShort(_page, pos) == 0xFFFF) break;
            pos += LvalEntrySize;
        }
        _end = pos + 2;
        if (_end != _length) throw Unexpected("its length");
    }

    private string ReadName(ref int pos)
    {
        if (pos + 2 > _length) throw Unexpected("a name");
        int length = ByteUtil.GetShort(_page, pos);
        if (length < 0 || pos + 2 + length > _length) throw Unexpected("a name");
        string name = Encoding.Unicode.GetString(_page, pos + 2, length);
        pos += 2 + length;
        return name;
    }

    /// <summary>The bytes of a name as a definition holds one: its length in bytes, then UTF-16.</summary>
    internal static byte[] NameBytes(string name)
    {
        byte[] text  = Encoding.Unicode.GetBytes(name);
        var    bytes = new byte[2 + text.Length];
        ByteUtil.PutShort(bytes, 0, (short)text.Length);
        Array.Copy(text, 0, bytes, 2, text.Length);
        return bytes;
    }

    /// <summary>
    /// Refuses, before anything is changed, a change that would make the definition longer than its page by adding
    /// <paramref name="added"/> bytes to it.
    /// </summary>
    internal void EnsureRoomFor(int added)
    {
        if (_length + added > _format.PageSize)
            throw new NotSupportedException(
                $"Table '{_table}' is not changed: its definition would take {_length + added} bytes, more than its page " +
                $"holds ({_format.PageSize}), and this library reads and writes a definition of one page only. Nothing was changed.");
    }

    /// <summary>The bytes a new column adds to the definition.</summary>
    internal int SizeOfColumn(string name, bool isLongValue)
        => _format.SizeColumnHeader + NameBytes(name).Length + (isLongValue ? LvalEntrySize : 0);

    /// <summary>The bytes a new index block adds to the definition.</summary>
    internal int SizeOfIndexData => _format.SizeIndexDefinition + _format.SizeIndexColumnBlock;

    /// <summary>The bytes a new index slot adds to the definition.</summary>
    internal int SizeOfIndexSlot(string name) => _format.SizeIndexInfoBlock + NameBytes(name).Length;

    // Makes room for data at a place in the definition, moving what follows it along, and puts it there.
    private void Insert(int at, byte[] data)
    {
        EnsureRoomFor(data.Length);
        Array.Copy(_page, at, _page, at + data.Length, _length - at);
        Array.Copy(data, 0, _page, at, data.Length);
        _length += data.Length;
        ByteUtil.PutInt(_page, OffsetLength, _length);
        ByteUtil.PutShort(_page, OffsetFreeSpace, (short)Math.Max(_format.PageSize - _length - 8, 0));
    }

    // ── A column ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds <paramref name="column"/> after the table's other columns, with the place in a row it has been given
    /// (its number, and its offset among the fixed-length values or its index among the variable-length ones), and
    /// for a long-value column the usage maps of its pages.
    /// </summary>
    internal void AddColumn(Column column, int columnId, LvalUmapRef? longValueMaps)
    {
        bool   isLong = column.DataType.IsLongValue();
        byte[] name   = NameBytes(column.Name);
        EnsureRoomFor(_format.SizeColumnHeader + name.Length + (isLong ? LvalEntrySize : 0));

        int maxVar = MaxVarColumnCount;
        var def = new byte[_format.SizeColumnHeader];
        def[0] = (byte)column.DataType;
        ByteUtil.PutInt(def, 1, MagicTableNumber);
        ByteUtil.PutShort(def, _format.OffsetColumnNumber, (short)column.Ordinal);
        // Access gives a fixed-length column, here, the count of variable-length places there were when it was made.
        ByteUtil.PutShort(def, _format.OffsetColumnVarTableIndex, column.IsVariableLength ? column.VarLenTableIndex : (short)maxVar);
        ByteUtil.PutShort(def, _format.OffsetColumnVarTableIndex + 2, (short)columnId);
        if (column.DataType is DataType.Text or DataType.Memo)
            column.SortOrder.Write(def, _format.OffsetColumnSortOrder, _format);
        else if (column.DataType == DataType.Decimal)
        {
            def[_format.OffsetColumnPrecision] = column.Precision;
            def[_format.OffsetColumnScale]     = column.Scale;
        }

        byte flags = 0x02;                                   // updatable
        if (!column.IsVariableLength) flags |= 0x01;         // among the fixed-length values
        if (column.IsAutoNumber) flags |= column.DataType == DataType.Guid ? (byte)0x40 : (byte)0x04;
        def[_format.OffsetColumnFlags] = flags;
        // The extended flags: bit 0 is "Unicode Compression".
        def[_format.OffsetColumnFlags + 1] =
            (byte)(column.DataType is DataType.Text or DataType.Memo && column.IsCompressedUnicode ? 0x01 : 0x00);
        ByteUtil.PutShort(def, _format.OffsetColumnFixedDataOffset, column.IsVariableLength ? (short)0 : column.FixedDataOffset);
        ByteUtil.PutShort(def, _format.OffsetColumnLength, isLong ? (short)0 : (short)column.LengthInBytes);

        Insert(_columnNames, def);
        Insert(_columnNamesEnd + def.Length, name);
        if (isLong)
        {
            var maps  = longValueMaps ?? throw new ArgumentNullException(nameof(longValueMaps));
            var entry = new byte[LvalEntrySize];
            ByteUtil.PutShort(entry, 0, (short)column.Ordinal);
            entry[2] = (byte)maps.OwnedRow;
            ByteUtil.Put3ByteInt(entry, 3, maps.OwnedPage);
            entry[6] = (byte)maps.FreeRow;
            ByteUtil.Put3ByteInt(entry, 7, maps.FreePage);
            Insert(_end + def.Length + name.Length - 2, entry);   // before the 0xFFFF that ends the definition
        }

        ByteUtil.PutShort(_page, _format.TdefOffsetNumVarCols - 2, (short)(column.Ordinal + 1));
        ByteUtil.PutShort(_page, _format.TdefOffsetNumVarCols, (short)(maxVar + (column.IsVariableLength ? 1 : 0)));
        ByteUtil.PutShort(_page, _format.TdefOffsetNumCols, (short)(_columnCount + 1));
        Parse();
    }

    // ── An index ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a block for an index's tree after the table's other blocks and returns its number: its columns, the
    /// usage map of its pages, its root page and its flags, and the counts Access keeps of it - how many entries
    /// it was built with, and how many keys they had between them.
    /// </summary>
    internal int AddIndexData(IReadOnlyList<IndexColumn> columns, byte flags, int rootPage, int umapPage, int umapRow,
                              int entries, int distinctKeys)
    {
        EnsureRoomFor(SizeOfIndexData);

        var counts = new byte[_format.SizeIndexDefinition];
        ByteUtil.PutInt(counts, 0, entries);
        ByteUtil.PutInt(counts, 4, distinctKeys);

        var block = new byte[_format.SizeIndexColumnBlock];
        if (_format.SkipBeforeIndex >= 4) ByteUtil.PutInt(block, 0, MagicIndexNumber);
        int p = _format.SkipBeforeIndex;
        for (int c = 0; c < IndexDefinition.MaxColumns; c++)
        {
            ByteUtil.PutShort(block, p, c < columns.Count ? (short)columns[c].Column.Ordinal : unchecked((short)0xFFFF));
            block[p + 2] = c < columns.Count ? columns[c].Flags : (byte)0;
            p += 3;
        }
        block[p] = (byte)umapRow;
        ByteUtil.Put3ByteInt(block, p + 1, umapPage);
        ByteUtil.PutInt(block, p + 4, rootPage);
        block[p + 8 + _format.SkipBeforeIndexFlags] = flags;

        int number = _indexCount;
        Insert(_format.SizeTdefHeader + _indexCount * _format.SizeIndexDefinition, counts);
        Insert(_slots + counts.Length, block);
        ByteUtil.PutInt(_page, _format.TdefOffsetNumIndexes, _indexCount + 1);
        Parse();
        return number;
    }

    /// <summary>What an index slot says of the relationship it is one side of.</summary>
    internal readonly record struct ForeignKeyReference(
        byte OtherTableType, int OtherIndexNumber, int OtherTablePage, bool CascadeUpdates, bool CascadeDeletes, bool CascadeNull);

    /// <summary>
    /// Adds a slot - an index's name, its number and the block that holds its tree - at <paramref name="position"/>
    /// among the table's slots, and its name at the same place among their names.
    /// </summary>
    internal void AddIndexSlot(int position, string name, int indexNumber, int indexDataNumber, byte indexType,
                               ForeignKeyReference? foreignKey)
    {
        byte[] nameBytes = NameBytes(name);
        EnsureRoomFor(_format.SizeIndexInfoBlock + nameBytes.Length);
        if (position < 0 || position > _slotCount) throw new ArgumentOutOfRangeException(nameof(position));

        var slot = new byte[_format.SizeIndexInfoBlock];
        if (_format.SkipBeforeIndexSlot >= 4) ByteUtil.PutInt(slot, 0, MagicTableNumber);
        int p = _format.SkipBeforeIndexSlot;
        ByteUtil.PutInt(slot, p, indexNumber);
        ByteUtil.PutInt(slot, p + 4, indexDataNumber);
        if (foreignKey is { } reference)
        {
            slot[p + 8] = reference.OtherTableType;
            ByteUtil.PutInt(slot, p + 9, reference.OtherIndexNumber);
            ByteUtil.PutInt(slot, p + 13, reference.OtherTablePage);
            slot[p + 17] = (byte)(reference.CascadeUpdates ? 1 : 0);
            slot[p + 18] = (byte)((reference.CascadeDeletes ? 1 : 0) | (reference.CascadeNull ? 2 : 0));
        }
        else
        {
            ByteUtil.PutInt(slot, p + 9, -1);   // no other index
        }
        slot[p + 19] = indexType;

        int nameAt = _slotNames;
        for (int i = 0; i < position; i++) ReadName(ref nameAt);

        Insert(_slots + position * _format.SizeIndexInfoBlock, slot);
        Insert(nameAt + slot.Length, nameBytes);
        ByteUtil.PutInt(_page, _format.TdefOffsetNumIndexSlots, _slotCount + 1);
        Parse();
    }

    /// <summary>The definition page as changed.</summary>
    internal byte[] ToPage() => (byte[])_page.Clone();
}
