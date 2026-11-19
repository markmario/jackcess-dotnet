namespace JackcessDotNet;

/// <summary>
/// Encodes a row's bytes: each value in the place the table's definition gives its column (<see cref="RowLayout"/>),
/// as Access and Java Jackcess write a row, and as <see cref="RowDecoder"/> reads one.
/// <para>
/// Rows are encoded for the formats this library writes, which all lay a row out as Jet4 does. None is encoded for
/// Access 97's (<see cref="JetFormat.IsReadOnly"/>): an encoder for it is refused with
/// <see cref="NotSupportedException"/>. This class once had a Jet3 form of a row, with two bytes for the end of
/// the data and for the count of variable-length columns where Jet3 has one (qcatco/exportmdb.interop#67).
/// </para>
/// </summary>
internal sealed class RowEncoder
{
    private readonly JetFormat _format;
    private readonly IReadOnlyList<Column> _columns;
    private readonly RowLayout _layout;
    private readonly IReadOnlyDictionary<string, LvalWriter>? _lvalWriters;
    private readonly bool _unreadColumns;

    /// <exception cref="NotSupportedException"><paramref name="format"/> is Access 97's, which is only read.</exception>
    public RowEncoder(JetFormat format, IReadOnlyList<Column> columns)
        : this(format, columns, null) { }

    internal RowEncoder(JetFormat format, IReadOnlyList<Column> columns,
                        IReadOnlyDictionary<string, LvalWriter>? lvalWriters)
    {
        _lvalWriters = lvalWriters;
        _format = Written(format);
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
        _format = Written(format);
        _columns = (table ?? throw new ArgumentNullException(nameof(table))).Columns;
        _layout = new RowLayout(format, table);
        _unreadColumns = table.HasUnreadColumns;
    }

    /// <summary><paramref name="format"/>, when it is one rows are written in.</summary>
    private static JetFormat Written(JetFormat format)
    {
        if (format is null) throw new ArgumentNullException(nameof(format));
        if (format.IsReadOnly) throw PageFile.ReadOnlyFormat("no row is encoded for one.");
        return format;
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
        => Complete(Prepare(row, keptVariableValues, null));

    /// <summary>
    /// A row encoded as far as it can be without writing to the file: every value converted and in its place, the
    /// row's size settled, and only the references to the long values that go to pages of their own still to come.
    /// </summary>
    internal sealed class PreparedRow
    {
        internal PreparedRow(byte[] bytes, List<(int At, LvalWriter Writer, byte[] Data)> longValues)
        {
            Bytes = bytes;
            LongValues = longValues;
        }

        /// <summary>The row, with twelve empty bytes where each of <see cref="LongValues"/>' references goes.</summary>
        internal byte[] Bytes { get; }
        internal List<(int At, LvalWriter Writer, byte[] Data)> LongValues { get; }
    }

    /// <summary>
    /// Everything of encoding <paramref name="row"/> that can refuse it - a value its column cannot hold or that is
    /// not of its type, a row too large - with nothing written. An update prepares the new row before it frees or
    /// deletes the one it replaces, and an insert before any page is touched, so a refused row changes nothing
    /// (qcatco/exportmdb.interop#56). <paramref name="keptVariableValues"/> and <paramref name="keptFixedValues"/>
    /// are the old row's own bytes for the columns an update does not name, written back as they are: an update
    /// does not change a value it was not given, whatever reading it and writing it again would make of it.
    /// </summary>
    internal PreparedRow Prepare(Row row, IReadOnlyDictionary<Column, byte[]>? keptVariableValues,
                                 IReadOnlyDictionary<Column, byte[]>? keptFixedValues)
    {
        if (row is null)
            throw new ArgumentNullException(nameof(row));
        EnsureWritable();
        Validate(row, keptVariableValues, keptFixedValues);

        int maxRowSize = _format.MaxRowSize;
        var buffer = new byte[maxRowSize];
        int pos = 0;

        // Column count: every column the table has had, as Access writes it.
        int columnCount = _layout.MaxColumnCount;
        Util.ByteUtil.PutShort(buffer, pos, (short)columnCount);
        pos += RowColumnCountSize;

        var nullMask = new NullMask(columnCount);

        // Fixed-length values, each at its column's offset. A null one leaves its room empty.
        int fixedDataStart = pos;
        foreach (var col in _layout.FixedColumns)
        {
            var place = _layout.PlaceOf(col)!.Value;
            if (keptFixedValues is not null && keptFixedValues.TryGetValue(col, out var keptBytes))
            {
                nullMask.MarkNotNull(col.Ordinal);
                Array.Copy(keptBytes, 0, buffer, fixedDataStart + place.FixedOffset, Math.Min(keptBytes.Length, place.FixedSize));
                continue;
            }

            // DBNull is no value, and a Yes/No is read as RowValues reads one: what the row's index keys go by.
            object? value = row.TryGetValue(col.Name, out var v) && !RowValues.IsNull(v) ? v : null;

            if (col.DataType == DataType.Boolean)
            {
                if (RowValues.ToBoolean(value))
                    nullMask.MarkNotNull(col.Ordinal);
                continue;
            }

            if (value != null)
            {
                nullMask.MarkNotNull(col.Ordinal);
                WriteFixedValue(buffer, fixedDataStart + place.FixedOffset, col, value, place.FixedSize);
            }
        }

        int fixedDataSize = _layout.FixedDataSize;
        if (_layout.MaxVarColumnCount == 0 && fixedDataSize < MinFixedDataWithoutVariableColumns)
            fixedDataSize = MinFixedDataWithoutVariableColumns;
        pos = fixedDataStart + fixedDataSize;

        var longValues = new List<(int At, LvalWriter Writer, byte[] Data)>();
        int varColumnCount = _layout.MaxVarColumnCount;
        if (varColumnCount > 0)
        {
            int trailerSize = nullMask.ByteSize + 2 * VarColOffsetSize + varColumnCount * VarColOffsetSize;
            int remaining = maxRowSize - pos - trailerSize;
            if (remaining < 0)
                throw new InvalidOperationException("Row is too large for format.");

            var varOffsets = new short[varColumnCount];
            int varOffsetIndex = 0;

            foreach (var col in _layout.VariableColumns)
            {
                short offset = (short)pos;

                // The value's bytes in the row: the old row's own, for a column an update leaves alone; or the
                // value encoded; or, for a long value bound for pages of its own, room for its reference.
                byte[]? varData = null;
                (LvalWriter Writer, byte[] Data)? toPages = null;
                if (keptVariableValues is not null && keptVariableValues.TryGetValue(col, out var kept))
                    varData = kept;
                else if (row.TryGetValue(col.Name, out var v) && !RowValues.IsNull(v))
                {
                    if (col.DataType.IsLongValue()) (varData, toPages) = PrepareLongValue(col, v!);
                    else varData = WriteVariableValue(col, v!);
                }

                if (varData is not null || toPages is not null)
                {
                    nullMask.MarkNotNull(col.Ordinal);
                    int length = varData?.Length ?? LongValueReferenceSize;

                    remaining -= length;
                    if (remaining < 0)
                        throw new InvalidOperationException("Row is too large for format.");

                    if (varData is not null) Array.Copy(varData, 0, buffer, pos, length);
                    else longValues.Add((pos, toPages!.Value.Writer, toPages.Value.Data));
                    pos += length;
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
                Util.ByteUtil.PutShort(buffer, pos, varOffsets[i]);
                pos += VarColOffsetSize;
            }

            Util.ByteUtil.PutShort(buffer, pos, (short)varColumnCount);
            pos += 2;
        }

        nullMask.WriteTo(buffer, pos);
        pos += nullMask.ByteSize;

        var result = new byte[pos];
        Array.Copy(buffer, 0, result, 0, pos);
        return new PreparedRow(result, longValues);
    }

    /// <summary>
    /// Writes the long values of <paramref name="prepared"/> that go to pages of their own, puts their references
    /// in the row and returns its bytes. Nothing here refuses the row: only the file can fail.
    /// </summary>
    internal byte[] Complete(PreparedRow prepared)
    {
        foreach (var (at, writer, data) in prepared.LongValues)
        {
            byte[] reference = writer.Write(data);
            Array.Copy(reference, 0, prepared.Bytes, at, LongValueReferenceSize);
        }
        return prepared.Bytes;
    }

    // Two bytes each in every format rows are written in: the column count that begins a row, and each entry of
    // its trailer - the end of the data, every variable-length value's offset, and their count.
    private const int RowColumnCountSize = 2;
    private const int VarColOffsetSize   = 2;

    /// <summary>The bytes a row holds for a long value kept on pages of its own: its reference.</summary>
    private const int LongValueReferenceSize = 12;

    /// <summary>
    /// A long value as the row will hold it: its bytes with their reference, when it is short enough to stay in the
    /// row (or the column has no pages of its own), or else the bytes to go to pages and the writer that puts them
    /// there. Text bound for pages is plain UTF-16LE, without the compression marker: that is what Access writes
    /// for a chained Memo, and ACE fails to decode compressed text spread across chunks.
    /// </summary>
    private (byte[]? InRow, (LvalWriter Writer, byte[] Data)? ToPages) PrepareLongValue(Column col, object value)
    {
        LvalWriter? writer = null;
        _lvalWriters?.TryGetValue(col.Name, out writer);

        if (col.DataType == DataType.Memo)
        {
            string text = Convert.ToString(value) ?? string.Empty;
            byte[] textBytes = EncodeText(col, text);
            return textBytes.Length > InlineLvalThreshold && writer is not null
                ? (null, (writer, System.Text.Encoding.Unicode.GetBytes(text)))
                : (BuildInlineLvRef(textBytes), null);
        }

        if (value is not byte[] bytes)
            throw new InvalidOperationException("OLE column value must be a byte array.");
        return bytes.Length > InlineLvalThreshold && writer is not null
            ? (null, (writer, bytes))
            : (BuildInlineLvRef(bytes), null);
    }

    /// <summary>
    /// Throws when the table's definition is not one a row can be written by - a column placed outside the row, or
    /// two in one place - so that nothing is written wrongly.
    /// </summary>
    internal void EnsureWritable()
    {
        if (WhyNotWritable() is { } why) throw new NotSupportedException(why);
    }

    /// <summary>What <see cref="EnsureWritable"/> refuses the table with, or null when its definition is one to write rows by.</summary>
    internal string? WhyNotWritable()
    {
        string? why = _unreadColumns
            ? "its definition ends before its columns' names do, so its columns were not all read"
            : _columns.FirstOrDefault(c => c.IsCalculated) is { } calculated
                ? $"its column {calculated.Name} is calculated, and this library works no expression out"
                : _layout.WhyNotWritable;
        return why is null ? null : $"No row of this table is written: {why}. Nothing was changed.";
    }

    private void WriteFixedValue(byte[] buffer, int offset, Column col, object value, int size)
    {
        switch (col.DataType)
        {
            case DataType.Byte:
                buffer[offset] = Convert.ToByte(value);
                return;
            case DataType.Int16:
                Util.ByteUtil.PutShort(buffer, offset, Convert.ToInt16(value));
                return;
            case DataType.Int32:
                Util.ByteUtil.PutInt(buffer, offset, Convert.ToInt32(value));
                return;
            case DataType.Single:
                Util.ByteUtil.PutFloat(buffer, offset, Convert.ToSingle(value));
                return;
            case DataType.Double:
                Util.ByteUtil.PutDouble(buffer, offset, Convert.ToDouble(value));
                return;
            case DataType.DateTime:
                // A row rewritten with its dates as stored brings the double itself for a date it does not change.
                Util.ByteUtil.PutDouble(buffer, offset, DatesAsStored && value is double days ? days : ((DateTime)value).ToOADate());
                return;
            case DataType.Currency:
                decimal dec = Convert.ToDecimal(value);
                long scaled = (long)Math.Round(dec * 10000m, 0, MidpointRounding.AwayFromZero);
                Util.ByteUtil.PutLong(buffer, offset, scaled);
                return;
            case DataType.Guid:
                var guidBytes = (value is Guid guid ? guid : Guid.Parse(Convert.ToString(value)!)).ToByteArray();
                Util.ByteUtil.PutBytes(buffer, offset, guidBytes);
                return;
            case DataType.Decimal:
                // A double or a float as Access takes one to a Decimal, on every runtime (Util.AutomationDecimal).
                Util.ByteUtil.PutBytes(buffer, offset, NumericCodec.Encode(Util.AutomationDecimal.From(value, provider: null), col.Precision, col.Scale, col.Name));
                return;
            case DataType.Int64:
                Util.ByteUtil.PutLong(buffer, offset, Convert.ToInt64(value));
                return;
            case DataType.DateTimeExtended:
                Util.ByteUtil.PutBytes(buffer, offset, ExtendedDateTimeCodec.Encode((DateTime)value));
                return;
            case DataType.Text:
            {
                // Text kept among the fixed-length values (SQL's CHAR): never compressed, and filled to the
                // column's length with spaces, as Access fills it.
                var encoding = System.Text.Encoding.Unicode;
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
    /// Throws when a Text or Binary value is longer than its column allows, as Access does, before anything of the
    /// row is written. Written whole, ACE reads such a text cut to the column's length and a typed reader refuses the
    /// table. A Text column's length is in bytes, two to a character; a Binary column's in bytes. An update calls it
    /// before it deletes the row it replaces.
    /// </summary>
    internal void Validate(Row row) => Validate(row, null, null);

    /// <summary>
    /// As <see cref="Validate(Row)"/>, for an update: the values of the columns in <paramref name="keptVariableValues"/>
    /// and <paramref name="keptFixedValues"/> are the row's own, which the update does not name. Such a value is held
    /// to its column too - the row is written whole - and one an earlier version of this library wrote longer than
    /// its column is refused with a message that says it is the stored value, and that giving that column a value
    /// that fits lets the update through.
    /// </summary>
    private void Validate(Row row, IReadOnlyDictionary<Column, byte[]>? keptVariableValues, IReadOnlyDictionary<Column, byte[]>? keptFixedValues)
    {
        foreach (var col in _columns)
        {
            if (!row.TryGetValue(col.Name, out var value) || RowValues.IsNull(value)) continue;
            bool stored = keptVariableValues?.ContainsKey(col) == true || keptFixedValues?.ContainsKey(col) == true;

            // A Decimal with more digits than its column holds.
            if (col.DataType == DataType.Decimal)
                NumericCodec.Encode(Util.AutomationDecimal.From(value, provider: null), col.Precision, col.Scale, col.Name);
            // Binary longer than its column, whichever area the definition keeps it in: Access refuses one byte over
            // its length, through ACE and DAO, in an .mdb and an .accdb, kept among the variable-length values
            // (VARBINARY, DAO's dbBinary) or the fixed-length ones (BINARY, dbFixedField) (#45).
            if (col.DataType == DataType.Binary && col.LengthInBytes > 0 && value is byte[] binary && binary.Length > col.LengthInBytes)
                throw new ColumnValueTooLongException(col.Name, DataType.Binary, col.LengthInBytes, binary.Length, stored);
            if (col.DataType != DataType.Text || col.LengthInBytes <= 0) continue;

            int maxChars = col.LengthInBytes / 2;
            string text = Convert.ToString(value) ?? string.Empty;
            if (text.Length > maxChars)
                throw stored
                    ? new ColumnValueTooLongException(col.Name, DataType.Text, maxChars, text.Length, stored: true)
                    : new ColumnValueTooLongException(col.Name, maxChars, text.Length);
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
                return EncodeText(col, Convert.ToString(value) ?? string.Empty);
            case DataType.Binary:
                return (byte[])value;
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
}
