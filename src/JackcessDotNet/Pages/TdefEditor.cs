using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Changes a table's definition where it is: a column, an index's block or an index's slot is put into the bytes of
/// the definition among those already there, as Jackcess's <c>TableImpl.mutateAddColumn</c>,
/// <c>mutateAddIndexData</c> and <c>mutateAddIndex</c> do - or taken out from among them, or its name changed - so
/// everything Access wrote that this library does not model - the counts it keeps for each index, a slot's
/// foreign-key reference, a column's bytes of unknown meaning - stays as it was. Nothing here writes to the file:
/// <see cref="ToDefinition"/> gives the definition as it would be, for <see cref="TdefBuffer.Write"/> to put on
/// its pages.
/// <para>
/// The definition is the whole of it, whatever number of pages it is on (<see cref="TdefBuffer"/>), and it grows
/// onto another page when what is added no longer fits the pages it has (#37).
/// </para>
/// <para>
/// The layout, as <see cref="TdefReader"/> reads it and Access writes it: the header; a block of counts for each
/// index; the column definitions; the column names; a block for each index's columns, pages and flags; a slot for
/// each index's name and number; the index names; the usage maps of the long-value columns; 0xFFFF. The header's
/// length is of all of it, from the start of the first page - as Access was seen to keep it, and as Jackcess
/// writes it.
/// </para>
/// </summary>
internal sealed class TdefEditor
{
    private const int MagicTableNumber = 1625;
    private const int MagicIndexNumber = 1923;
    private const int OffsetLength     = 8;
    private const int LvalEntrySize    = 10;

    /// <summary>A slot's index type: an ordinary index, a primary key, or one side of a relationship.</summary>
    internal const byte IndexTypeForeignKey = 2;

    /// <summary>
    /// The two cascade bytes of a slot that is no relationship's, as Access writes them: 4 in each, in every
    /// such slot of the tables it makes (#37). A relationship's slot has its cascades there.
    /// </summary>
    internal const byte NoRelationshipCascade = 4;

    /// <summary>A slot's "other table" type: this table is the relationship's primary (referenced) table.</summary>
    internal const byte ForeignKeyPrimaryTable = 1;
    /// <summary>A slot's "other table" type: this table is the relationship's secondary (referring) table.</summary>
    internal const byte ForeignKeySecondaryTable = 2;

    private readonly JetFormat _format;
    private readonly string    _table;
    // The definition's bytes, with room after them: more room is made when it grows.
    private byte[]             _page;

    // Where each part of the definition starts, and where the last ends.
    private int _length;
    private int _columnCount, _indexCount, _slotCount;
    private int _columns, _columnNames, _columnNamesEnd, _blocks, _slots, _slotNames, _slotNamesEnd, _end;

    /// <summary>The names of the columns, in the order the definition lists them.</summary>
    internal IReadOnlyList<string> ColumnNames { get; private set; } = Array.Empty<string>();

    /// <summary>The number each column has in a row, in the order the definition lists them.</summary>
    internal IReadOnlyList<int> ColumnNumbers { get; private set; } = Array.Empty<int>();

    /// <summary>The block that holds each slot's tree, in the order the definition lists the slots.</summary>
    internal IReadOnlyList<int> SlotBlocks { get; private set; } = Array.Empty<int>();

    /// <summary>How many index slots the definition has.</summary>
    internal int SlotCount => _slotCount;

    /// <summary>
    /// What the slot at <paramref name="position"/> says of itself beside its name, number and block: its type (0
    /// an ordinary index, 1 a primary key, 2 a relationship's), and for a relationship's which side of it the
    /// table is (<see cref="ForeignKeyPrimaryTable"/>, <see cref="ForeignKeySecondaryTable"/>), the number of the
    /// other side's index and the page of the other table's definition.
    /// </summary>
    internal (byte IndexType, byte Side, int OtherIndexNumber, int OtherTablePage) SlotReference(int position)
    {
        if (position < 0 || position >= _slotCount) throw new ArgumentOutOfRangeException(nameof(position));
        int p = _slots + position * _format.SizeIndexInfoBlock + _format.SkipBeforeIndexSlot;
        return (_page[p + 19], _page[p + 8], ByteUtil.GetInt(_page, p + 9), ByteUtil.GetInt(_page, p + 13));
    }

    /// <summary>The usage map that lists the pages of the tree of block <paramref name="number"/>, and the tree's root page.</summary>
    internal (int UmapPage, int UmapRow, int RootPage) BlockPages(int number)
    {
        if (number < 0 || number >= _indexCount) throw new ArgumentOutOfRangeException(nameof(number));
        int p = _blocks + number * _format.SizeIndexColumnBlock + _format.SkipBeforeIndex + IndexDefinition.MaxColumns * 3;
        return (ByteUtil.Get3ByteInt(_page, p + 1), _page[p], ByteUtil.GetInt(_page, p + 4));
    }

    /// <summary>The numbers of the columns block <paramref name="number"/> is on, in key order.</summary>
    internal IReadOnlyList<int> BlockColumns(int number)
    {
        if (number < 0 || number >= _indexCount) throw new ArgumentOutOfRangeException(nameof(number));
        int p = _blocks + number * _format.SizeIndexColumnBlock + _format.SkipBeforeIndex;
        var columns = new List<int>();
        for (int c = 0; c < IndexDefinition.MaxColumns; c++)
        {
            int column = ByteUtil.GetUShort(_page, p + c * 3);
            if (column != 0xFFFF) columns.Add(column);
        }
        return columns;
    }

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
    /// Reads the layout of <paramref name="definition"/> - a whole definition, as
    /// <see cref="TdefBuffer.Read(PageFile, int)"/> gives it - a copy of which is changed.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The definition is not laid out as its header says: it is not changed.
    /// </exception>
    internal TdefEditor(byte[] definition, JetFormat format, string table)
    {
        _format = format;
        _table  = table;
        _page   = (byte[])definition.Clone();
        if (format.IsReadOnly)
            throw PageFile.ReadOnlyFormat($"the definition of table '{table}' was not changed.");
        if (definition[0] != JetFormat.PageTypeTableDef)
            throw Unexpected("its page is not a definition page");
        Parse();
    }

    /// <summary>The definition of the table whose definition starts on <paramref name="tdefPage"/>, read whole.</summary>
    /// <exception cref="NotSupportedException">The definition's pages stop short, or it is not laid out as its header says: it is not changed.</exception>
    internal static TdefEditor Load(PageFile file, int tdefPage, string table)
    {
        byte[] definition = TdefBuffer.Read(file, tdefPage, out _, out bool isBroken);
        if (isBroken)
            throw new NotSupportedException(
                $"Table '{table}' is not changed: its definition goes on to a page that is not one of its own, so it was not read whole. Nothing was changed.");
        return new TdefEditor(definition, file.Format, table);
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
        _columns     = columns;
        _columnNames = columns + _columnCount * _format.SizeColumnHeader;
        if (_columnNames > _length) throw Unexpected("its columns");

        var ids = new List<int>(_columnCount);
        var columnNumbers = new List<int>(_columnCount);
        for (int i = 0; i < _columnCount; i++)
        {
            ids.Add(ByteUtil.GetShort(_page, columns + i * _format.SizeColumnHeader + _format.OffsetColumnVarTableIndex + 2));
            columnNumbers.Add(ByteUtil.GetShort(_page, columns + i * _format.SizeColumnHeader + _format.OffsetColumnNumber));
        }
        ColumnIds     = ids;
        ColumnNumbers = columnNumbers;

        int pos = _columnNames;
        var columnNames = new List<string>(_columnCount);
        for (int i = 0; i < _columnCount; i++) columnNames.Add(ReadName(ref pos));
        ColumnNames     = columnNames;
        _columnNamesEnd = pos;

        _blocks    = _columnNamesEnd;
        _slots     = _columnNamesEnd + _indexCount * _format.SizeIndexColumnBlock;
        _slotNames = _slots + _slotCount * _format.SizeIndexInfoBlock;
        if (_slotNames > _length) throw Unexpected("its indexes");

        var numbers = new List<int>(_slotCount);
        var blocks  = new List<int>(_slotCount);
        for (int i = 0; i < _slotCount; i++)
        {
            int slot = _slots + i * _format.SizeIndexInfoBlock + _format.SkipBeforeIndexSlot;
            numbers.Add(ByteUtil.GetInt(_page, slot));
            blocks.Add(ByteUtil.GetInt(_page, slot + 4));
        }
        SlotNumbers = numbers;
        SlotBlocks  = blocks;

        pos = _slotNames;
        var names = new List<string>(_slotCount);
        for (int i = 0; i < _slotCount; i++) names.Add(ReadName(ref pos));
        SlotNames     = names;
        _slotNamesEnd = pos;

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
        // As the definition's reader reads it (TdefReader): the names are compared with its exactly.
        string name = ByteUtil.DecodeUtf16(_page, pos + 2, length);
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

    // Makes room for data at a place in the definition, moving what follows it along, and puts it there.
    private void Insert(int at, byte[] data)
    {
        if (_length + data.Length > _page.Length)
            Array.Resize(ref _page, Math.Max(_length + data.Length, _page.Length + _format.PageSize));
        Array.Copy(_page, at, _page, at + data.Length, _length - at);
        Array.Copy(data, 0, _page, at, data.Length);
        _length += data.Length;
        ByteUtil.PutInt(_page, OffsetLength, _length);
    }

    // Takes bytes out of the definition, moving what follows them back, and clears what the end of it leaves behind.
    private void Remove(int at, int count)
    {
        Array.Copy(_page, at + count, _page, at, _length - at - count);
        Array.Clear(_page, _length - count, count);
        _length -= count;
        ByteUtil.PutInt(_page, OffsetLength, _length);
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
            // What Access writes in the two cascade bytes of an index that is no relationship's.
            slot[p + 17] = NoRelationshipCascade;
            slot[p + 18] = NoRelationshipCascade;
        }
        slot[p + 19] = indexType;

        int nameAt = _slotNames;
        for (int i = 0; i < position; i++) ReadName(ref nameAt);

        Insert(_slots + position * _format.SizeIndexInfoBlock, slot);
        Insert(nameAt + slot.Length, nameBytes);
        ByteUtil.PutInt(_page, _format.TdefOffsetNumIndexSlots, _slotCount + 1);
        Parse();
    }

    // ── Taking out, and renaming ─────────────────────────────────────────────

    /// <summary>
    /// Takes the column at <paramref name="position"/> among the definition's columns out of it: its definition,
    /// its name and, for a long-value column, the entry that names its usage maps - as Access was seen to drop a
    /// column. The other columns keep their numbers and their places in a row, and the counts of the columns and
    /// the variable-length places a row has ever had stay as they were: the rows are not rewritten, and go on
    /// being read by them.
    /// </summary>
    internal void RemoveColumn(int position)
    {
        if (position < 0 || position >= _columnCount) throw new ArgumentOutOfRangeException(nameof(position));
        int number = ColumnNumbers[position];

        // From the end back, so the places worked out before stay good: the long-value entry, the name, the definition.
        for (int pos = _slotNamesEnd; pos + 2 <= _length && ByteUtil.GetUShort(_page, pos) != 0xFFFF; pos += LvalEntrySize)
            if (ByteUtil.GetUShort(_page, pos) == (ushort)number)
            {
                Remove(pos, LvalEntrySize);
                break;
            }

        int nameAt = _columnNames;
        for (int i = 0; i < position; i++) ReadName(ref nameAt);
        int nameEnd = nameAt;
        ReadName(ref nameEnd);
        Remove(nameAt, nameEnd - nameAt);

        Remove(_columns + position * _format.SizeColumnHeader, _format.SizeColumnHeader);
        ByteUtil.PutShort(_page, _format.TdefOffsetNumCols, (short)(_columnCount - 1));
        Parse();
    }

    /// <summary>
    /// Takes the slot at <paramref name="position"/> among the definition's slots out of it, and its name. The
    /// other slots keep their numbers, which is what a relationship's slot in another table names one by.
    /// </summary>
    internal void RemoveIndexSlot(int position)
    {
        if (position < 0 || position >= _slotCount) throw new ArgumentOutOfRangeException(nameof(position));

        int nameAt = _slotNames;
        for (int i = 0; i < position; i++) ReadName(ref nameAt);
        int nameEnd = nameAt;
        ReadName(ref nameEnd);
        Remove(nameAt, nameEnd - nameAt);

        Remove(_slots + position * _format.SizeIndexInfoBlock, _format.SizeIndexInfoBlock);
        ByteUtil.PutInt(_page, _format.TdefOffsetNumIndexSlots, _slotCount - 1);
        Parse();
    }

    /// <summary>
    /// Takes the block numbered <paramref name="number"/> out of the definition - its columns, pages and flags, and
    /// the counts Access keeps of it - and has every slot of a block after it name its block by the number it now
    /// has, one less: as Access was seen to drop an index. No slot may still name the block.
    /// </summary>
    internal void RemoveIndexData(int number)
    {
        if (number < 0 || number >= _indexCount) throw new ArgumentOutOfRangeException(nameof(number));
        if (SlotBlocks.Contains(number))
            throw new InvalidOperationException($"Table '{_table}' still has an index on the block that was to be taken out of its definition.");

        for (int i = 0; i < _slotCount; i++)
        {
            int at = _slots + i * _format.SizeIndexInfoBlock + _format.SkipBeforeIndexSlot + 4;
            int block = ByteUtil.GetInt(_page, at);
            if (block > number) ByteUtil.PutInt(_page, at, block - 1);
        }
        Remove(_blocks + number * _format.SizeIndexColumnBlock, _format.SizeIndexColumnBlock);
        Remove(_format.SizeTdefHeader + number * _format.SizeIndexDefinition, _format.SizeIndexDefinition);
        ByteUtil.PutInt(_page, _format.TdefOffsetNumIndexes, _indexCount - 1);
        Parse();
    }

    /// <summary>Gives the column at <paramref name="position"/> among the definition's columns another name.</summary>
    internal void RenameColumn(int position, string name)
    {
        if (position < 0 || position >= _columnCount) throw new ArgumentOutOfRangeException(nameof(position));
        ReplaceName(_columnNames, position, name);
    }

    /// <summary>Gives the slot at <paramref name="position"/> among the definition's slots another name, where it is.</summary>
    internal void RenameIndexSlot(int position, string name)
    {
        if (position < 0 || position >= _slotCount) throw new ArgumentOutOfRangeException(nameof(position));
        ReplaceName(_slotNames, position, name);
    }

    private void ReplaceName(int namesAt, int position, string name)
    {
        int nameAt = namesAt;
        for (int i = 0; i < position; i++) ReadName(ref nameAt);
        int nameEnd = nameAt;
        ReadName(ref nameEnd);
        Remove(nameAt, nameEnd - nameAt);
        Insert(nameAt, NameBytes(name));
        Parse();
    }

    /// <summary>
    /// Takes the slot at <paramref name="position"/> out and puts it back, as its bytes are, at
    /// <paramref name="newPosition"/> among the slots that are left, under <paramref name="name"/>: Access keeps a
    /// table's slots in the order of their names, so a renamed index's slot moves to where its new name sorts.
    /// </summary>
    internal void MoveIndexSlot(int position, int newPosition, string name)
    {
        if (position < 0 || position >= _slotCount) throw new ArgumentOutOfRangeException(nameof(position));
        if (newPosition < 0 || newPosition >= _slotCount) throw new ArgumentOutOfRangeException(nameof(newPosition));
        var slot = new byte[_format.SizeIndexInfoBlock];
        Array.Copy(_page, _slots + position * _format.SizeIndexInfoBlock, slot, 0, slot.Length);
        RemoveIndexSlot(position);

        int nameAt = _slotNames;
        for (int i = 0; i < newPosition; i++) ReadName(ref nameAt);
        Insert(_slots + newPosition * _format.SizeIndexInfoBlock, slot);
        Insert(nameAt + slot.Length, NameBytes(name));
        ByteUtil.PutInt(_page, _format.TdefOffsetNumIndexSlots, _slotCount + 1);
        Parse();
    }

    /// <summary>The definition as changed: its bytes, whole, with its length in bytes 8-11.</summary>
    internal byte[] ToDefinition() => (byte[])_page.Clone();
}
