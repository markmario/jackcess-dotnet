namespace JackcessDotNet;

/// <summary>
/// Encodes a row's bytes: each value in the place the table's definition gives its column (<see cref="RowLayout"/>),
/// as Access and Java Jackcess write a row, and as <see cref="RowDecoder"/> reads one.
/// <para>
/// Rows are encoded for the formats this library writes, which all lay a row out as Jet4 does. None is encoded for
/// Access 97's (<see cref="JetFormat.IsReadOnly"/>): an encoder for it is refused with
/// <see cref="NotSupportedException"/>.
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
        internal PreparedRow(byte[] bytes, List<(int At, LvalWriter Writer, byte[] Data, bool Chain)> longValues)
        {
            Bytes = bytes;
            LongValues = longValues;
        }

        /// <summary>The row, with twelve empty bytes where each of <see cref="LongValues"/>' references goes.</summary>
        internal byte[] Bytes { get; }
        internal List<(int At, LvalWriter Writer, byte[] Data, bool Chain)> LongValues { get; }
    }

    /// <summary>
    /// Everything of encoding <paramref name="row"/> that can refuse it - a value its column cannot hold or that is
    /// not of its type, a row too large - with nothing written. An update prepares the new row before it frees or
    /// deletes the one it replaces, and an insert before any page is touched, so a refused row changes nothing.
    /// <paramref name="keptVariableValues"/> and <paramref name="keptFixedValues"/>
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

        var longValues = new List<(int At, LvalWriter Writer, byte[] Data, bool Chain)>();
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
                (LvalWriter Writer, byte[] Data, bool Chain)? toPages = null;
                if (keptVariableValues is not null && keptVariableValues.TryGetValue(col, out var kept))
                    varData = kept;
                else if (row.TryGetValue(col.Name, out var v) && !RowValues.IsNull(v))
                {
                    if (col.DataType.IsLongValue()) (varData, toPages) = PrepareLongValue(col, v!);
                    else varData = WriteVariableValue(col, v!, ReadsByContent(col, row));
                }

                if (varData is not null || toPages is not null)
                {
                    nullMask.MarkNotNull(col.Ordinal);
                    int length = varData?.Length ?? LongValueReferenceSize;

                    remaining -= length;
                    if (remaining < 0)
                        throw new InvalidOperationException("Row is too large for format.");

                    if (varData is not null) Array.Copy(varData, 0, buffer, pos, length);
                    else longValues.Add((pos, toPages!.Value.Writer, toPages.Value.Data, toPages.Value.Chain));
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
        foreach (var (at, writer, data, chain) in prepared.LongValues)
        {
            byte[] reference = writer.Write(data, chain);
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
    /// there. A Memo value's bytes are those Access writes (<see cref="EncodeMemo"/>): compressed only when it is short
    /// enough for one long-value row, as ACE fails to decode compressed text spread across chunks.
    /// </summary>
    private (byte[]? InRow, (LvalWriter Writer, byte[] Data, bool Chain)? ToPages) PrepareLongValue(Column col, object value)
    {
        LvalWriter? writer = null;
        _lvalWriters?.TryGetValue(col.Name, out writer);

        if (col.DataType == DataType.Memo)
        {
            byte[] textBytes = EncodeMemo(col, Convert.ToString(value) ?? string.Empty);
            // UTF-16LE of more than 3,816 bytes that begins 0xFF 0xFE, in a column with Unicode compression, is a chain
            // as ACE keeps it, which Access reads as UTF-16LE: in one long-value row it would read compressed text (#93).
            bool chain = col.IsCompressedUnicode && textBytes.Length > AccessLongestValueRow
                         && textBytes[0] == 0xFF && textBytes[1] == 0xFE;
            return textBytes.Length > InlineLvalThreshold && writer is not null
                ? (null, (writer, textBytes, chain))
                : (BuildInlineLvRef(textBytes), null);
        }

        if (value is not byte[] bytes)
            throw new InvalidOperationException("OLE column value must be a byte array.");
        return bytes.Length > InlineLvalThreshold && writer is not null
            ? (null, (writer, bytes, false))
            : (BuildInlineLvRef(bytes), null);
    }

    /// <summary>
    /// Throws when the table's definition is not one a row can be written by - a calculated or complex column, a
    /// column placed outside the row, or two in one place - so that nothing is written wrongly.
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
                : _columns.FirstOrDefault(c => c.DataType == DataType.Complex) is { } complex
                    ? $"its column {complex.Name} is a complex one (attachments, several values or version history), " +
                      "for which Access gives each row a number from the table's own counter and keeps the values in a " +
                      "table of their own, neither of which this library writes"
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
                Util.ByteUtil.PutDouble(buffer, offset, DatesAsStored && value is double days ? days : Util.OADate.FromDateTime((DateTime)value));
                return;
            case DataType.Currency:
                // Ten-thousandths, the caller's number taken to them as Access takes it (Util.CurrencyRounding, #65).
                Util.ByteUtil.PutLong(buffer, offset, Util.CurrencyRounding.Units(value));
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
                // A value a row stores - one a relationship's cascade carries from its parent - is written as the
                // parent holds it, in whichever form that is: the child's bytes are then the parent's key's.
                Util.ByteUtil.PutBytes(buffer, offset, value is StoredExtendedDate stored ? stored.Bytes : ExtendedDateTimeCodec.Encode((DateTime)value));
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
            // Text that begins with U+FEFF that Access would read back changed (#93).
            if (!stored && col.DataType is DataType.Text or DataType.Memo
                && WhyNotKept(col, Convert.ToString(value) ?? string.Empty, InRow(col), ReadsByContent(col, row)) is { } why)
                throw new ArgumentException(why + ". Nothing was written.", col.Name);
            if (col.DataType != DataType.Text || col.LengthInBytes <= 0) continue;

            int maxChars = col.LengthInBytes / 2;
            string text = Convert.ToString(value) ?? string.Empty;
            if (text.Length > maxChars)
                throw new ColumnValueTooLongException(col.Name, DataType.Text, maxChars, text.Length, stored);
        }
    }

    /// <summary>
    /// The most bytes of a long value kept in the parent row (THIS_PAGE); a longer one goes to a long-value page.
    /// It is Access's own limit, and Java Jackcess's (MAX_INLINE_LONG_VALUE_SIZE). Earlier versions of this library
    /// kept up to 1,024 bytes in the row: they read them back, and so did other lenient readers, but Access shows
    /// #Error for every such value.
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

    /// <summary>The most characters a Memo value Access compresses has: 1,908, two bytes each uncompressed a long-value row's 3,816.</summary>
    internal const int MaxCompressedMemoChars = 1908;

    /// <summary>
    /// A Memo value's bytes as Access writes them (#87), measured with ACE 16 on a column with Unicode compression:
    /// <list type="bullet">
    ///   <item>no text is no bytes;</item>
    ///   <item>text of 3 to 1,908 characters, every one of them 0x01 to 0xFF, is compressed: 0xFF 0xFE and a byte a
    ///         character - so it is never more than one long-value row, as ACE does not decode compressed text spread
    ///         across a chain;</item>
    ///   <item>anything else - 1 or 2 characters, which compression would not make shorter, longer text, or text with a
    ///         character outside 0x01 to 0xFF - is UTF-16LE.</item>
    /// </list>
    /// ACE also compresses text mixing such characters with others, in runs; this library writes that text as UTF-16LE,
    /// which ACE reads the same. Text that begins with U+FEFF, whose UTF-16LE begins 0xFF 0xFE like compressed text, is
    /// compressed in runs where ACE compresses it (#93): where that is shorter, in a column with Unicode compression up
    /// to 1,908 characters, and in a column without it when its UTF-16LE is short enough to stay in the row.
    /// </summary>
    internal static byte[] EncodeMemo(Column col, string text)
    {
        if (text.Length == 0) return Array.Empty<byte>();
        if (Util.ByteUtil.BeginsWithMarker(text)
            && (col.TextMayBeCompressed ? text.Length <= MaxCompressedMemoChars : 2 * text.Length <= InlineLvalThreshold))
            return Util.ByteUtil.CompressInRuns(text, evenIfNoShorter: false) ?? Util.ByteUtil.EncodeUtf16(text);
        bool compress = col.IsCompressedUnicode && text.Length >= 3 && text.Length <= MaxCompressedMemoChars;
        for (int i = 0; compress && i < text.Length; i++)
            compress = text[i] >= 0x01 && text[i] <= 0xFF;
        if (!compress) return Util.ByteUtil.EncodeUtf16(text);   // the code units it holds, half a surrogate pair among them
        var bytes = new byte[text.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        for (int i = 0; i < text.Length; i++) bytes[i + 2] = (byte)text[i];
        return bytes;
    }

    // Compressed only where the column allows it (see Column.IsCompressedUnicode). As the code units the text holds, as
    // Access writes it and the decoder reads it back (ByteUtil.DecodeUtf16). Where the value is read by its bytes
    // (ReadsByContent), text that begins with U+FEFF is compressed in runs.
    private static byte[] EncodeText(Column col, string text, bool byContent)
        => col.IsCompressedUnicode || byContent && Util.ByteUtil.BeginsWithMarker(text)
            ? Util.ByteUtil.EncodeText(text)
            : Util.ByteUtil.EncodeUtf16(text);

    /// <summary>
    /// Whether <paramref name="col"/>'s value in <paramref name="row"/> is read by its bytes: a column of a table an
    /// earlier version of this library created (<see cref="Column.ReadsTextByContent"/>), and the catalog's Name in
    /// the row of such a table (<see cref="Column.IsCatalogName"/>, #121), as <see cref="RowDecoder"/> reads them.
    /// </summary>
    private static bool ReadsByContent(Column col, Row row)
        => col.ReadsTextByContent || col.IsCatalogName && SystemCatalog.IsAnEarlierVersionsTable(row);

    /// <summary>Whether a long value of so many bytes is kept in the row (<see cref="PrepareLongValue"/>).</summary>
    private Func<int, bool> InRow(Column col)
    {
        bool hasPages = _lvalWriters?.ContainsKey(col.Name) == true;
        return length => length <= InlineLvalThreshold || !hasPages;
    }

    /// <summary>The most bytes ACE keeps in one long-value row: a longer value is a chain of them.</summary>
    internal const int AccessLongestValueRow = 3816;

    /// <summary>
    /// Why <paramref name="text"/> cannot be stored in <paramref name="col"/> so that Access reads it back, or null when
    /// it can (#93). Text that begins with U+FEFF has UTF-16LE that begins 0xFF 0xFE, the mark of compressed text. It is
    /// kept, as ACE keeps it, compressed in runs where that is no longer than UTF-16LE in a Text column and shorter in a
    /// Memo of up to 1,908 characters; otherwise it is UTF-16LE, which Access reads back changed - as compressed text -
    /// where it reads 0xFF 0xFE as that mark (<see cref="Column.TextMayBeCompressed"/>): a Text column with Unicode
    /// compression, a Memo kept in the row (<paramref name="inRow"/> says where a Memo of that many bytes goes), and a
    /// Memo in one long-value row of a column with it. A Memo of more than 3,816 bytes in such a column is a chain,
    /// which Access reads as UTF-16LE, and is kept. Where the value is read by its bytes - in a table an earlier
    /// version created (<see cref="Column.ReadsTextByContent"/>), and the catalog's Name in such a table's row:
    /// <paramref name="byContent"/> - it is read as compressed text wherever it is kept.
    /// </summary>
    internal static string? WhyNotKept(Column col, string text, Func<int, bool> inRow, bool byContent)
    {
        if (!Util.ByteUtil.BeginsWithMarker(text)) return null;
        bool mayBeCompressed = col.IsCompressedUnicode || byContent;
        bool readBack = col.DataType switch
        {
            DataType.Text => !mayBeCompressed || Util.ByteUtil.CompressInRuns(text, evenIfNoShorter: true) is not null,
            DataType.Memo => EncodeMemo(col, text) is var bytes
                             && (bytes.Length != 2 * text.Length                                // compressed in runs
                                 || !inRow(bytes.Length)
                                    && (!mayBeCompressed || !byContent && bytes.Length > AccessLongestValueRow)),
            _ => true,
        };
        return readBack ? null : $"The text for {col.Name} begins with U+FEFF, the byte order mark, and cannot be stored in this column so "
                               + "that it reads back: Access keeps it as UTF-16LE, and reads the bytes 0xFF 0xFE it begins with as the mark "
                               + "of compressed text, here";
    }

    private byte[] WriteVariableValue(Column col, object value, bool byContent)
    {
        switch (col.DataType)
        {
            case DataType.Text:
                return EncodeText(col, Convert.ToString(value) ?? string.Empty, byContent);
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
