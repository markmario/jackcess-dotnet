namespace JackcessDotNet;

/// <summary>
/// Encodes a row's bytes: each value in the place the table's definition gives its column (<see cref="RowLayout"/>),
/// as Access and Java Jackcess write a row, and as <see cref="RowDecoder"/> reads one.
/// </summary>
public sealed class RowEncoder
{
    private readonly JetFormat _format;
    private readonly IReadOnlyList<Column> _columns;
    private readonly RowLayout _layout;
    private readonly IReadOnlyDictionary<string, LvalWriter>? _lvalWriters;
    private readonly bool _unreadColumns;

    public RowEncoder(JetFormat format, IReadOnlyList<Column> columns)
        : this(format, columns, null) { }

    internal RowEncoder(JetFormat format, IReadOnlyList<Column> columns,
                        IReadOnlyDictionary<string, LvalWriter>? lvalWriters)
    {
        _lvalWriters = lvalWriters;
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _columns = columns ?? throw new ArgumentNullException(nameof(columns));
        _layout = new RowLayout(format, columns);
    }

    /// <summary>
    /// Whether a Date/Time column's value may come as the double a row stores - days since 30 December 1899 -
    /// instead of a <see cref="DateTime"/>, and is then written as it is. A <see cref="DateTime"/> does not hold
    /// every double Access writes, so a row rewritten from what was read as one would have its dates changed.
    /// </summary>
    internal bool DatesAsStored { get; set; }

    /// <summary>For a table's rows: the definition also says how many columns the table has ever had.</summary>
    internal RowEncoder(JetFormat format, TableDefinition table,
                        IReadOnlyDictionary<string, LvalWriter>? lvalWriters = null)
    {
        _lvalWriters = lvalWriters;
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _columns = (table ?? throw new ArgumentNullException(nameof(table))).Columns;
        _layout = new RowLayout(format, table);
        _unreadColumns = table.HasUnreadColumns;
    }

    /// <summary>
    /// The fewest bytes of fixed-length values in a row of a table with no variable-length column: Access writes
    /// two for a table of one Yes/No or one Byte column, which leaves a row long enough for the four bytes that
    /// say where it went, should it ever have to move.
    /// </summary>
    private const int MinFixedDataWithoutVariableColumns = 2;

    public byte[] Encode(Row row) => Encode(row, null);

    /// <summary>
    /// Encodes <paramref name="row"/>. <paramref name="keptVariableValues"/> holds, for columns an update leaves
    /// as they are, the bytes the old row held among its variable-length values: they are written back as they
    /// are - a long value's reference with them, so the value it refers to is neither read nor written again.
    /// Everything that can refuse the row does so before a long value is written to its pages.
    /// </summary>
    /// <exception cref="NotSupportedException">The table's definition is not one rows can be written by.</exception>
    internal byte[] Encode(Row row, IReadOnlyDictionary<Column, byte[]>? keptVariableValues)
    {
        if (row is null)
            throw new ArgumentNullException(nameof(row));
        EnsureWritable();
        Validate(row);

        int maxRowSize = _format.MaxRowSize;
        var buffer = new byte[maxRowSize];
        int pos = 0;

        // Column count: every column the table has had, as Access writes it.
        int columnCount = _layout.MaxColumnCount;
        if (_format.SizeRowColumnCount == 2)
        {
            Util.ByteUtil.PutShort(buffer, pos, (short)columnCount);
            pos += 2;
        }
        else if (_format.SizeRowColumnCount == 1)
        {
            if (columnCount > 255)
                throw new InvalidOperationException("Jet3 rows with >255 columns are not supported.");
            buffer[pos++] = (byte)columnCount;
        }
        else
        {
            throw new InvalidOperationException("Unsupported row column count size.");
        }

        var nullMask = new NullMask(columnCount);

        // Fixed-length values, each at its column's offset. A null one leaves its room empty.
        int fixedDataStart = pos;
        foreach (var col in _layout.FixedColumns)
        {
            // DBNull is no value, and a Yes/No is read as RowValues reads one: what the row's index keys go by.
            object? value = row.TryGetValue(col.Name, out var v) && !RowValues.IsNull(v) ? v : null;

            if (col.DataType == DataType.Boolean)
            {
                if (RowValues.ToBoolean(value))
                    nullMask.MarkNotNull(col.ColumnNumber);
                continue;
            }

            if (value != null)
            {
                nullMask.MarkNotNull(col.ColumnNumber);
                var place = _layout.PlaceOf(col)!.Value;
                WriteFixedValue(buffer, fixedDataStart + place.FixedOffset, col, value, place.FixedSize);
            }
        }

        int fixedDataSize = _layout.FixedDataSize;
        if (_layout.MaxVarColumnCount == 0 && fixedDataSize < MinFixedDataWithoutVariableColumns)
            fixedDataSize = MinFixedDataWithoutVariableColumns;
        pos = fixedDataStart + fixedDataSize;

        int varColumnCount = _layout.MaxVarColumnCount;
        if (varColumnCount > 0)
        {
            int trailerSize = nullMask.ByteSize + 4 + varColumnCount * _format.SizeRowVarColOffset;
            int remaining = maxRowSize - pos - trailerSize;
            if (remaining < 0)
                throw new InvalidOperationException("Row is too large for format.");

            // What each column's value takes in the row is known before anything is written, so a row too large
            // is refused before a long value goes to its pages.
            var values = PlanVariableValues(row, keptVariableValues, remaining);

            var varOffsets = new short[varColumnCount];
            int varOffsetIndex = 0;

            foreach (var (col, value, plain) in values)
            {
                short offset = (short)pos;
                if (value != null)
                {
                    nullMask.MarkNotNull(col.ColumnNumber);
                    byte[] varData = plain ?? WriteVariableValue(col, value);

                    if (_format.SizeRowVarColOffset == 1 && (offset > 255 || varData.Length > 255))
                        throw new NotSupportedException("Jet3 rows with var offsets >255 are not supported yet.");

                    Array.Copy(varData, 0, buffer, pos, varData.Length);
                    pos += varData.Length;
                }

                // A deleted column's offset is the next column's: it holds nothing.
                int index = _layout.PlaceOf(col)!.Value.VarIndex;
                while (varOffsetIndex <= index)
                {
                    varOffsets[varOffsetIndex++] = offset;
                }
            }

            while (varOffsetIndex < varOffsets.Length)
            {
                varOffsets[varOffsetIndex++] = (short)pos;
            }

            int eod = pos;
            Util.ByteUtil.PutShort(buffer, pos, (short)eod);
            pos += 2;

            for (int i = varColumnCount - 1; i >= 0; i--)
            {
                if (_format.SizeRowVarColOffset == 2)
                {
                    Util.ByteUtil.PutShort(buffer, pos, varOffsets[i]);
                    pos += 2;
                }
                else if (_format.SizeRowVarColOffset == 1)
                {
                    buffer[pos++] = (byte)varOffsets[i];
                }
            }

            Util.ByteUtil.PutShort(buffer, pos, (short)varColumnCount);
            pos += 2;
        }

        nullMask.WriteTo(buffer, pos);
        pos += nullMask.ByteSize;

        var result = new byte[pos];
        Array.Copy(buffer, 0, result, 0, pos);
        return result;
    }

    /// <summary>
    /// Each variable-length column's value and, for all but a long value still to be written, its bytes - or
    /// throws when together they are more than the <paramref name="remaining"/> bytes a row has for them.
    /// </summary>
    private List<(Column Column, object? Value, byte[]? Bytes)> PlanVariableValues(
        Row row, IReadOnlyDictionary<Column, byte[]>? keptVariableValues, int remaining)
    {
        var values = new List<(Column Column, object? Value, byte[]? Bytes)>(_layout.VariableColumns.Count);
        int needed = 0;
        foreach (var col in _layout.VariableColumns)
        {
            object? value = row.TryGetValue(col.Name, out var v) && !RowValues.IsNull(v) ? v : null;
            byte[]? bytes = null;
            if (value != null)
            {
                if (keptVariableValues is not null && keptVariableValues.TryGetValue(col, out var kept))
                    bytes = kept;
                else if (!col.DataType.IsLongValue())
                    bytes = WriteVariableValue(col, value);
                needed += bytes?.Length ?? LongValueSizeInRow(col, value);
            }
            values.Add((col, value, bytes));
        }
        if (remaining < 0 || needed > remaining)
            throw new InvalidOperationException("Row is too large for format.");
        return values;
    }

    /// <summary>
    /// Throws what <see cref="Encode(Row, IReadOnlyDictionary{Column, byte[]})"/> would for a row too large for
    /// the format, without writing anything: an update asks before it frees or deletes what it replaces, so a
    /// refused one leaves the row as it was (qcatco/exportmdb.interop#56).
    /// </summary>
    internal void EnsureFits(Row row, IReadOnlyDictionary<Column, byte[]>? keptVariableValues)
    {
        if (_layout.MaxVarColumnCount == 0) return;
        int fixedEnd = _format.SizeRowColumnCount + _layout.FixedDataSize;
        int trailer  = (_layout.MaxColumnCount + 7) / 8 + 4 + _layout.MaxVarColumnCount * _format.SizeRowVarColOffset;
        PlanVariableValues(row, keptVariableValues, _format.MaxRowSize - fixedEnd - trailer);
    }

    /// <summary>
    /// Throws when the table's definition is not one a row can be written by - a column placed outside the row, or
    /// two in one place - so that nothing is written wrongly.
    /// </summary>
    internal void EnsureWritable()
    {
        string? why = _unreadColumns
            ? "its definition runs past its first page, and the columns on the pages after it were not read"
            : _columns.FirstOrDefault(c => c.IsCalculated) is { } calculated
                ? $"its column {calculated.Name} is calculated, and this library works no expression out"
                : _layout.WhyNotWritable;
        if (why is not null)
            throw new NotSupportedException($"No row of this table is written: {why}. Nothing was changed.");
    }

    private void WriteFixedValue(byte[] buffer, int offset, Column col, object value, int size)
    {
        switch (col.DataType)
        {
            case DataType.Byte:
                buffer[offset] = Convert.ToByte(value);
                return;
            case DataType.Int:
                Util.ByteUtil.PutShort(buffer, offset, Convert.ToInt16(value));
                return;
            case DataType.Long:
                Util.ByteUtil.PutInt(buffer, offset, Convert.ToInt32(value));
                return;
            case DataType.Float:
                Util.ByteUtil.PutFloat(buffer, offset, Convert.ToSingle(value));
                return;
            case DataType.Double:
                Util.ByteUtil.PutDouble(buffer, offset, Convert.ToDouble(value));
                return;
            case DataType.ShortDateTime:
                // A row rewritten with its dates as stored brings the double itself for a date it does not change.
                Util.ByteUtil.PutDouble(buffer, offset, DatesAsStored && value is double days ? days : ((DateTime)value).ToOADate());
                return;
            case DataType.Money:
                decimal dec = Convert.ToDecimal(value);
                long scaled = (long)Math.Round(dec * 10000m, 0, MidpointRounding.AwayFromZero);
                Util.ByteUtil.PutLong(buffer, offset, scaled);
                return;
            case DataType.Guid:
                var guidBytes = (value is Guid guid ? guid : Guid.Parse(Convert.ToString(value)!)).ToByteArray();
                Util.ByteUtil.PutBytes(buffer, offset, guidBytes);
                return;
            case DataType.Numeric:
                Util.ByteUtil.PutBytes(buffer, offset, NumericCodec.Encode(Convert.ToDecimal(value), col.Precision, col.Scale, col.Name));
                return;
            case DataType.Text:
            {
                // Text kept among the fixed-length values (SQL's CHAR): never compressed, and filled to the
                // column's length with spaces, as Access fills it.
                var encoding = _format.Version == JetVersion.Jet3 ? _format.TextEncoding : System.Text.Encoding.Unicode;
                byte[] text = encoding.GetBytes(Convert.ToString(value) ?? string.Empty);
                byte[] space = encoding.GetBytes(" ");
                Util.ByteUtil.PutBytes(buffer, offset, text, 0, Math.Min(text.Length, size));
                for (int at = text.Length; at + space.Length <= size; at += space.Length)
                    Util.ByteUtil.PutBytes(buffer, offset + at, space);
                return;
            }
            case DataType.Binary:
            {
                // Binary kept among the fixed-length values (SQL's BINARY): filled to the column's length with zeros.
                var bytes = (byte[])value;
                Util.ByteUtil.PutBytes(buffer, offset, bytes, 0, Math.Min(bytes.Length, size));
                return;
            }
            default:
                throw new NotSupportedException($"Unsupported fixed data type {col.DataType}.");
        }
    }
    // ── Long-value helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Throws when a Text value is longer than its column allows, as Access does, before anything of the row is
    /// written. Written whole, ACE reads such a value cut to the column's length and a typed reader refuses the table.
    /// A Jet4 Text column's length is in bytes, two to a character; a Jet3 one's is in characters. An update calls it
    /// before it deletes the row it replaces.
    /// </summary>
    internal void Validate(Row row)
    {
        foreach (var col in _columns)
        {
            if (!row.TryGetValue(col.Name, out var value) || RowValues.IsNull(value)) continue;

            // A Decimal with more digits than its column holds, and Binary longer than a column of one length.
            if (col.DataType == DataType.Numeric)
                NumericCodec.Encode(Convert.ToDecimal(value), col.Precision, col.Scale, col.Name);
            if (col.DataType == DataType.Binary && !col.IsVariableLength && value is byte[] binary && binary.Length > col.Length)
                throw new ArgumentOutOfRangeException(nameof(row), binary.Length,
                    $"The Binary column '{col.Name}' holds {col.Length} bytes; {binary.Length} were given.");
            if (col.DataType != DataType.Text || col.Length <= 0) continue;

            int maxChars = _format.Version == JetVersion.Jet3 ? col.Length : col.Length / 2;
            string text = Convert.ToString(value) ?? string.Empty;
            if (text.Length > maxChars)
                throw new ColumnValueTooLongException(col.Name, maxChars, text.Length);
        }
    }

    /// <summary>
    /// The most bytes of a long value kept in the parent row (THIS_PAGE); a longer one goes to a long-value page.
    /// It is Access's own limit, and Java Jackcess's (MAX_INLINE_LONG_VALUE_SIZE). The engine kept up to 1,024 bytes
    /// in the row: it read them back, and so did other lenient readers, but Access showed #Error for every such
    /// value (qcatco/exportmdb.interop#29).
    /// </summary>
    internal const int InlineLvalThreshold = 64;

    /// <summary>
    /// Builds a THIS_PAGE inline LvRef in the REAL Jet format (matches Java
    /// Jackcess / what ACE writes): the type lives in the top two bits of the
    /// 32-bit length ([lengthWithFlags:4][lval_dp:4][unknown:4][data]).
    /// </summary>
    private static byte[] BuildInlineLvRef(byte[] data)
    {
        var lvRef = new byte[12 + data.Length];
        Util.ByteUtil.PutInt(lvRef, 0, unchecked(data.Length | (int)0x80000000)); // THIS_PAGE
        // bytes 4..11: lval_dp + unknown, left zero
        Array.Copy(data, 0, lvRef, 12, data.Length);
        return lvRef;
    }

    // Compressed only where the column allows it (see Column.IsCompressedUnicode).
    private static byte[] EncodeText(Column col, string text)
        => col.IsCompressedUnicode ? Util.ByteUtil.EncodeText(text) : System.Text.Encoding.Unicode.GetBytes(text);

    private byte[] WriteVariableValue(Column col, object value)
    {
        switch (col.DataType)
        {
            case DataType.Text:
                if (_format.Version == JetVersion.Jet3)
                    return _format.TextEncoding.GetBytes(Convert.ToString(value) ?? string.Empty);
                return EncodeText(col, Convert.ToString(value) ?? string.Empty);
            case DataType.Binary:
                return (byte[])value;
            case DataType.Memo:
            {
                string text = Convert.ToString(value) ?? string.Empty;
                byte[] textBytes = _format.Version == JetVersion.Jet3
                    ? _format.TextEncoding.GetBytes(text)
                    : EncodeText(col, text);
                // Values Access itself keeps in the row (THIS_PAGE) are stored
                // there; anything longer goes to LVAL pages. LVAL-bound text is stored as plain
                // UTF-16LE without the 0xFF 0xFE compression marker - that is
                // what Access itself writes for chained memos, and ACE fails to
                // decode compressed content spread across LVAL chunks.
                if (textBytes.Length > InlineLvalThreshold
                    && _lvalWriters is not null
                    && _lvalWriters.TryGetValue(col.Name, out var memoWriter))
                    return memoWriter.Write(System.Text.Encoding.Unicode.GetBytes(text));
                return BuildInlineLvRef(textBytes);
            }
            case DataType.Ole:
            {
                if (value is not byte[] oleBytes)
                    throw new InvalidOperationException("OLE column value must be a byte array.");
                if (oleBytes.Length > InlineLvalThreshold
                    && _lvalWriters is not null
                    && _lvalWriters.TryGetValue(col.Name, out var oleWriter))
                    return oleWriter.Write(oleBytes);
                return BuildInlineLvRef(oleBytes);
            }
            default:
            {
                // A type of one size that the definition keeps among the variable-length values: the same bytes.
                int size = col.DataType.GetFixedSize();
                if (size <= 0 || col.DataType == DataType.Boolean)
                    throw new NotSupportedException($"Unsupported variable data type {col.DataType}.");
                var bytes = new byte[size];
                WriteFixedValue(bytes, 0, col, value, size);
                return bytes;
            }
        }
    }

    /// <summary>
    /// The bytes a long value will take in the row, known before it is written: its reference, and the value
    /// itself when it is short enough to stay in the row.
    /// </summary>
    private int LongValueSizeInRow(Column col, object value)
    {
        int length;
        if (col.DataType == DataType.Memo)
        {
            string text = Convert.ToString(value) ?? string.Empty;
            length = _format.Version == JetVersion.Jet3 ? _format.TextEncoding.GetByteCount(text) : EncodeText(col, text).Length;
        }
        else
        {
            length = value is byte[] bytes ? bytes.Length : 0;
        }
        bool inRow = length <= InlineLvalThreshold || _lvalWriters is null || !_lvalWriters.ContainsKey(col.Name);
        return 12 + (inRow ? length : 0);
    }
}
