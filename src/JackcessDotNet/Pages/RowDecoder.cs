using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Decodes individual column values from a row's bytes, each from the place the table's definition gives its
/// column (<see cref="RowLayout"/>).
///
/// Row binary layout (Jet4 example):
///   [0..1]              column count (short, SizeRowColumnCount bytes): the table's when the row was written
///   [2..fixedEnd]       fixed-length values, each at its column's offset
///   [fixedEnd..eod-1]   variable-length values, in the order of their offsets
///   [eod..eod+1]        end-of-data pointer (2 bytes, SizeRowVarColOffset)
///   [..]                variable offsets in REVERSE index order (2 bytes each)
///   [..]                variable column count (2 bytes)
///   [..]                null mask (ceil(colCount/8) bytes), a bit for each column number
///
/// A Jet3 row has one byte where a Jet4 row has two, so an offset there is its low byte only. Between the offsets
/// and the variable column count such a row has a jump table, a byte for each 256 bytes of its length: the number
/// of the first variable-length value whose offset is past that many bytes (<see cref="TryLocateByJumpTable"/>).
/// </summary>
internal sealed class RowDecoder
{
    private readonly JetFormat              _format;
    private readonly IReadOnlyList<Column>  _columns;
    private readonly LvalReader?            _lvalReader;
    private readonly RowLayout              _layout;

    public RowDecoder(JetFormat format, IReadOnlyList<Column> columns,
                      LvalReader? lvalReader = null)
    {
        _lvalReader = lvalReader;
        _format  = format  ?? throw new ArgumentNullException(nameof(format));
        _columns = columns ?? throw new ArgumentNullException(nameof(columns));
        _layout  = new RowLayout(format, columns);
    }

    /// <summary>
    /// As <see cref="Decode(byte[], Column)"/>, for the row's index keys: a Date/Time column's value is the double the row
    /// stores, which is what its key is made of. A <see cref="DateTime"/> does not hold every double Access can
    /// have written, and a key made from one would not be the key Access wrote for the row.
    /// </summary>
    public object? DecodeForKey(byte[] rowBytes, Column col) => Decode(rowBytes, col, datesAsStored: true);

    /// <summary>
    /// Decodes the value of <paramref name="col"/> from <paramref name="rowBytes"/>.
    /// Returns <c>null</c> if the column is NULL, deleted, or the row is malformed.
    /// </summary>
    public object? Decode(byte[] rowBytes, Column col) => Decode(rowBytes, col, datesAsStored: false);

    private object? Decode(byte[] rowBytes, Column col, bool datesAsStored)
    {
        if (!TryReadMask(rowBytes, col, out int maskOffset, out bool notNull))
        {
            // A Yes/No column holds no null: in a row written before the column was added to the table, which
            // has no bit for it, it is No - as Access reads such a row. Any other column is null there.
            return col.DataType == DataType.Boolean && !col.IsCalculated && WasWrittenBefore(rowBytes, col) ? false : null;
        }

        // A calculated column's value is not read: Access keeps the result in a wrapper of its own, as a type the
        // column's properties name and not its definition (qcatco/exportmdb.interop#42). It is null here, where it
        // used to be whatever the wrapper's bytes made as the definition's type.
        if (col.IsCalculated) return null;

        // Boolean: null mask bit IS the value (true/false), never actually null
        if (col.DataType == DataType.Boolean)
            return notNull;

        if (!notNull) return null;
        if (!TryLocate(rowBytes, col, maskOffset, out int start, out int length)) return null;

        switch (col.DataType)
        {
            case DataType.Text:   return ByteUtil.DecodeText(rowBytes, start, length, _format);
            case DataType.Binary: return rowBytes.Slice(start, start + length);
            case DataType.Memo:   return DecodeMemoLvRef(rowBytes, start, length);
            case DataType.OleObject:    return DecodeOleLvRef (rowBytes, start, length);
        }

        // A type of one size, wherever the row keeps it.
        int size = col.DataType.GetFixedSize();
        if (size <= 0 || length < size) return null;
        if (datesAsStored && col.DataType == DataType.DateTime) return ByteUtil.GetDouble(rowBytes, start);
        return ReadFixed(rowBytes, start, col);
    }

    /// <summary>
    /// The bytes of <paramref name="col"/>'s value as the row holds them among its variable-length values - a long
    /// value's reference, not what it refers to - or null when the column is null in the row or is not kept there.
    /// An update writes them back as they are for a column it does not change.
    /// </summary>
    public byte[]? RawVariableValue(byte[] rowBytes, Column col)
    {
        if (_layout.PlaceOf(col) is not { IsVariable: true }) return null;
        if (!TryReadMask(rowBytes, col, out int maskOffset, out bool notNull) || !notNull) return null;
        return TryLocate(rowBytes, col, maskOffset, out int start, out int length) ? rowBytes.Slice(start, start + length) : null;
    }

    /// <summary>
    /// The bytes of <paramref name="col"/>'s value as the row holds them among its fixed-length values, or null
    /// when the column is null in the row, is not kept there, or is a Yes/No (which has no bytes).
    /// </summary>
    public byte[]? RawFixedValue(byte[] rowBytes, Column col)
    {
        if (col.DataType == DataType.Boolean || _layout.PlaceOf(col) is not { IsVariable: false, FixedSize: > 0 }) return null;
        if (!TryReadMask(rowBytes, col, out int maskOffset, out bool notNull) || !notNull) return null;
        return TryLocate(rowBytes, col, maskOffset, out int start, out int length) ? rowBytes.Slice(start, start + length) : null;
    }

    /// <summary>
    /// Finds the row's null mask and <paramref name="col"/>'s bit in it. False for a row too short to hold them, or
    /// written before the column was added to the table: the row's own column count says how many it has.
    /// </summary>
    private bool TryReadMask(byte[] rowBytes, Column col, out int maskOffset, out bool notNull)
    {
        maskOffset = 0;
        notNull    = false;
        if (rowBytes is null || rowBytes.Length < _format.SizeRowColumnCount) return false;

        int colCount = _format.SizeRowColumnCount == 2
            ? ByteUtil.GetShort(rowBytes, 0)
            : rowBytes[0];

        if (col.Ordinal < 0 || col.Ordinal >= colCount) return false;

        int maskSize = (colCount + 7) / 8;
        maskOffset   = rowBytes.Length - maskSize;
        if (maskOffset < _format.SizeRowColumnCount) return false;

        notNull = (rowBytes[maskOffset + col.Ordinal / 8] & (1 << (col.Ordinal % 8))) != 0;
        return true;
    }

    /// <summary>Whether the row was written before <paramref name="col"/> was added to the table: its own column count stops short of it.</summary>
    private bool WasWrittenBefore(byte[] rowBytes, Column col)
    {
        if (rowBytes is null || rowBytes.Length < _format.SizeRowColumnCount) return false;
        int colCount = _format.SizeRowColumnCount == 2 ? ByteUtil.GetShort(rowBytes, 0) : rowBytes[0];
        return col.Ordinal >= colCount && colCount > 0;
    }

    /// <summary>
    /// Where <paramref name="col"/>'s value is in the row: at its offset among the fixed-length values, for the
    /// bytes it takes there, or between its offset and the next among the variable-length ones.
    /// </summary>
    private bool TryLocate(byte[] rowBytes, Column col, int maskOffset, out int start, out int length)
    {
        start  = 0;
        length = 0;
        if (_layout.PlaceOf(col) is not { } place) return false;

        if (!place.IsVariable)
        {
            start  = _format.SizeRowColumnCount + place.FixedOffset;
            length = place.FixedSize;
            return place.FixedOffset >= 0 && start + length <= maskOffset;
        }

        // Trailer layout from the end of the row:
        //   [maskOffset - SizeRowVarColOffset]                = varCount
        //   [maskOffset - (vi+2)*SizeRowVarColOffset]         = varOffsets[vi]  (start)
        //   [maskOffset - (varCount+2)*SizeRowVarColOffset]   = eod
        int vi = place.VarIndex;
        int sz = _format.SizeRowVarColOffset;

        int varCountOffset = maskOffset - sz;
        if (vi < 0 || varCountOffset < _format.SizeRowColumnCount) return false;
        int storedVarCount = sz == 2
            ? ByteUtil.GetShort(rowBytes, varCountOffset)
            : rowBytes[varCountOffset];

        if (vi >= storedVarCount) return false;
        if (sz == 1) return TryLocateByJumpTable(rowBytes, maskOffset, storedVarCount, vi, out start, out length);

        // The offset after this one is where the next value starts, or, after the last, the end of the data.
        int varStartOffset = maskOffset - (vi + 2) * sz;
        int varEndOffset   = varStartOffset - sz;
        if (varEndOffset < _format.SizeRowColumnCount) return false;

        start = ByteUtil.GetShort(rowBytes, varStartOffset);
        int end = ByteUtil.GetShort(rowBytes, varEndOffset);

        length = end - start;
        return length >= 0 && start >= 0 && end <= maskOffset;
    }

    /// <summary>
    /// Where variable-length value <paramref name="vi"/> is in a Jet3 row, whose offsets are a byte each
    /// (Jackcess's <c>readJumpTableVarColOffsets</c>). Trailer layout from the end of the row:
    /// <code>
    ///   [maskOffset - 1]                        = varCount
    ///   [maskOffset - 2 - j]                    = jump j, for each 256 bytes of the row's length
    ///   [maskOffset - 2 - jumps - vi]           = low byte of varOffsets[vi]  (start)
    ///   [maskOffset - 2 - jumps - varCount]     = low byte of eod
    /// </code>
    /// Jump j is the number of the first value whose offset is 256 * (j + 1) or more; the end of the data counts
    /// as the value after the last. A row of 256 bytes or fewer has no jumps, and its offsets are whole.
    /// </summary>
    private bool TryLocateByJumpTable(byte[] rowBytes, int maskOffset, int varCount, int vi, out int start, out int length)
    {
        start  = 0;
        length = 0;

        int jumps       = (rowBytes.Length - 1) / 256;
        int firstJump   = maskOffset - 2;
        int firstOffset = firstJump - jumps;
        int eodOffset   = firstOffset - varCount;
        if (eodOffset < _format.SizeRowColumnCount) return false;

        // A row has a jump for each 256 bytes of its whole length, the trailer's too: when the values end
        // short of the last of them, the last jump is a filler and no value's.
        if (eodOffset / 256 < jumps) jumps--;

        int Offset(int value)
        {
            int passed = 0;
            while (passed < jumps && rowBytes[firstJump - passed] <= value) passed++;
            return rowBytes[firstOffset - value] + passed * 256;
        }

        start   = Offset(vi);
        int end = Offset(vi + 1);
        length  = end - start;
        return length >= 0 && end <= maskOffset;
    }

    /// <summary>
    /// Scans all long-value (Memo/OLE) columns in <paramref name="rowBytes"/> and yields
    /// the LVAL page/row coordinates for any that hold an OTHER_PAGE (0x40) LvRef, with the column holding it.
    /// Used to locate LVAL chains that must be freed before a row is deleted or updated.
    /// </summary>
    public IEnumerable<(Column column, int lvalPage, int lvalRow, bool chained)> GetOtherPageLvRefs(byte[] rowBytes)
    {
        foreach (var col in _columns)
        {
            if (!col.DataType.IsLongValue()) continue;
            if (!TryReadMask(rowBytes, col, out int maskOffset, out bool notNull) || !notNull) continue;
            if (!TryLocate(rowBytes, col, maskOffset, out int varStart, out int varLen) || varLen < 12) continue;

            // Real Jet LvRef: type in the top two bits of the 32-bit length.
            // Only OTHER_PAGE / OTHER_PAGES refs occupy LVAL pages to free.
            int  lengthWithFlags = ByteUtil.GetInt(rowBytes, varStart);
            uint type = (uint)lengthWithFlags & 0xC0000000u;
            if (type == 0x80000000u) continue;                 // inline - nothing to free
            bool chained = type == 0x00000000u;                // OTHER_PAGES
            if (!chained && type != 0x40000000u) continue;     // unknown type

            int lvalRow  = rowBytes[varStart + 4];
            int lvalPage = rowBytes[varStart + 5]
                         | (rowBytes[varStart + 6] << 8)
                         | (rowBytes[varStart + 7] << 16);

            yield return (col, lvalPage, lvalRow, chained);
        }
    }

    // ── Fixed-type readers ────────────────────────────────────────────────────
    private static object? ReadFixed(byte[] row, int offset, Column col) =>
        col.DataType switch
        {
            DataType.Byte         => row[offset],
            DataType.Int16          => ByteUtil.GetShort (row, offset),
            DataType.Int32         => ByteUtil.GetInt   (row, offset),
            DataType.Single        => ByteUtil.GetFloat (row, offset),
            DataType.Double       => ByteUtil.GetDouble(row, offset),
            DataType.DateTime => DecodeShortDateTime(row, offset),
            DataType.Currency        => Math.Round((decimal)ByteUtil.GetLong(row, offset) / 10000m, 4),
            DataType.Guid         => new Guid(row.Slice(offset, offset + 16)),
            DataType.Decimal      => NumericCodec.Decode(row, offset, col.Scale),
            DataType.Int64       => ByteUtil.GetLong  (row, offset),
            DataType.DateTimeExtended => ExtendedDateTimeCodec.Decode(row, offset),
            _                     => null
        };

    // ── Long-value decoders ───────────────────────────────────────────────────

    /// <summary>
    /// Reads the bytes behind a long-value reference in the REAL Jet format
    /// (matches Java Jackcess / ACE): [lengthWithFlags:4][row:1][page:3]
    /// [unknown:4], type in the top two bits of the length -
    /// 0x80000000 THIS_PAGE (inline, data at offset 12), 0x40000000 OTHER_PAGE
    /// (single chunk), 0x00000000 OTHER_PAGES (chunk chain).
    /// Returns null when the ref cannot be decoded.
    /// </summary>
    private byte[]? ReadLvalBytes(byte[] row, int start, int len)
    {
        if (len < 12) return null;
        int  lengthWithFlags = ByteUtil.GetInt(row, start);
        int  dataLen = lengthWithFlags & LvalWriter.LvalLengthMask;
        uint type    = (uint)lengthWithFlags & 0xC0000000u;

        if (type == 0x80000000u)   // THIS_PAGE inline
        {
            int actualLen = Math.Min(dataLen, len - 12);
            var inline = new byte[actualLen];
            Array.Copy(row, start + 12, inline, 0, actualLen);
            return inline;
        }

        if (_lvalReader is null) return null;
        bool chained = type == 0x00000000u;                    // OTHER_PAGES
        if (!chained && type != 0x40000000u) return null;      // unknown type

        int lvalRow  = row[start + 4];
        int lvalPage = row[start + 5] | (row[start + 6] << 8) | (row[start + 7] << 16);
        return _lvalReader.Read(lvalPage, lvalRow, dataLen, chained);
    }

    private string? DecodeMemoLvRef(byte[] row, int start, int len)
    {
        var data = ReadLvalBytes(row, start, len);
        return data is null ? null : ByteUtil.DecodeText(data, 0, data.Length, _format);
    }

    private byte[]? DecodeOleLvRef(byte[] row, int start, int len)
        => ReadLvalBytes(row, start, len);

    /// <summary>
    /// Decodes a Jet ShortDateTime (OLE Automation date = days since 1899-12-30): null for a number no date is
    /// (corruption, sentinels), rather than throw.
    /// </summary>
    private static DateTime? DecodeShortDateTime(byte[] row, int offset) => ReadDate(ByteUtil.GetDouble(row, offset));

    /// <summary>
    /// The <see cref="DateTime"/> a stored Date/Time number is read as, rounded to the millisecond; null for one no
    /// date is. The dates are those Access holds, 1 January 100 to 31 December 9999, each day at any time: a number
    /// above -657435 and below 2958466 (measured with ACE 16: <c>CDate(-657434.99998842593)</c>, 1 January 100 at
    /// 23:59:59, is taken and read as that; <c>CDate(-657435)</c> and <c>CDate(2958466)</c> are refused).
    /// </summary>
    internal static DateTime? ReadDate(double oa)
    {
        if (double.IsNaN(oa) || double.IsInfinity(oa)) return null;
        if (oa <= -657435.0 || oa >= 2958466.0) return null;
        try   { return DateTime.FromOADate(oa); }
        catch (ArgumentException) { return null; }
    }
}
