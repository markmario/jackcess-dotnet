using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// What a value a caller puts in a <see cref="Row"/> means once it is written: the one reading of it that the
/// row's bytes and its index keys are both made from, so the two cannot disagree.
/// </summary>
internal static class RowValues
{
    /// <summary>True for no value: <c>null</c>, or <see cref="DBNull"/> as a <c>DataRow</c> has it.</summary>
    public static bool IsNull(object? value) => value is null or DBNull;

    /// <summary>
    /// The Yes/No a value stands for, as Java Jackcess reads one: a <see cref="bool"/> is itself, a number is Yes
    /// unless it is zero (Access's own rule), text is Yes when it says "true" in any case, and no value is No -
    /// a Yes/No column holds no null.
    /// </summary>
    public static bool ToBoolean(object? value) => value switch
    {
        null or DBNull => false,
        bool b         => b,
        string s       => bool.TryParse(s, out bool parsed) && parsed,
        IConvertible c => c.GetTypeCode() switch
        {
            TypeCode.Single or TypeCode.Double => c.ToDouble(CultureInfo.InvariantCulture) != 0d,
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Decimal => c.ToDecimal(CultureInfo.InvariantCulture) != 0m,
            _ => false,
        },
        _ => false,
    };

    /// <summary>
    /// <paramref name="row"/> as it will be written to <paramref name="table"/>: an entry for each column it gives a
    /// value, by the column's own name; no <see cref="DBNull"/> (a column without a value has none), a Yes/No
    /// column's value as the <see cref="bool"/> it stands for, a number column's value as the number it stands for
    /// (<see cref="AsNumber"/>), and a Currency column's and a Decimal column's number as the <see cref="decimal"/> the
    /// row stores (<see cref="AsCurrency"/>, <see cref="AsDecimal"/>). Each value goes to one column: the column of
    /// exactly its name, or else the first whose name Access takes as one with it and that no other value is for
    /// (<see cref="ObjectNames"/>) - so a table with two columns whose names are one to Access, which a file can have,
    /// takes a value in each. A value no column is for is left out. The row itself when it is already so; otherwise a
    /// copy - the caller's row is not changed.
    /// </summary>
    /// <exception cref="ArgumentException">A number column's value is of a kind no number is made of.</exception>
    /// <exception cref="FormatException">A number column's value is text that does not read as a number.</exception>
    /// <exception cref="OverflowException">
    /// A number column's value is text of a number past what it is read as, or a Currency column's a number no
    /// Currency holds.
    /// </exception>
    public static Row AsWritten(Row row, TableDefinition table)
    {
        var columns = table.Columns;
        var exact   = table.ColumnNameSet;
        List<KeyValuePair<string, object?>>? loose = null;
        foreach (var entry in row)
            if (!exact.Contains(entry.Key)) (loose ??= new()).Add(entry);

        // The values given by a name of another spelling, each for the first column whose name Access takes as one
        // with it that is neither given a value by its exact name nor taken by an earlier such value.
        Dictionary<string, object?>? byRule = null;
        if (loose is not null)
        {
            var columnKeys = new ObjectNames.NameKey?[columns.Count];
            foreach (var entry in loose)
            {
                var key = table.Names.KeyOf(entry.Key);
                for (int i = 0; i < columns.Count; i++)
                {
                    string name = columns[i].Name;
                    if (row.HasExactly(name) || byRule?.ContainsKey(name) == true) continue;
                    if (!key.IsOneWith(columnKeys[i] ??= table.Names.KeyOf(name))) continue;
                    (byRule ??= new Dictionary<string, object?>(StringComparer.Ordinal)).Add(name, entry.Value);
                    break;
                }
            }
        }

        // A row written from has its columns' names exactly, and finds a column it gives no value at once: the caller's
        // row when it names every column exactly (and the table has no two columns whose names are one), and otherwise
        // a copy that finds names only exactly - there a column the row gives no value has none, where the rule would
        // look for one, and might find the other column's.
        bool complete = loose is null && row.Count == columns.Count;
        Row? written = complete && !table.HasColumnsThatAreOne ? null : new Row(columns.Count, table.Names, exactOnly: true);
        foreach (var column in columns)
        {
            object? value;
            if (row.HasExactly(column.Name)) value = row[column.Name];
            else if (byRule is not null && byRule.TryGetValue(column.Name, out var given)) value = given;
            else continue;

            object? normal = value is DBNull ? null
                : column.DataType == DataType.Boolean && value is not (null or bool) ? ToBoolean(value)
                : value is null || !IsNumberType(column.DataType) ? value
                : column.DataType == DataType.Currency ? AsCurrency(column, value)
                : column.DataType == DataType.Decimal ? AsDecimal(column, AsNumber(column, value))
                : column.DataType == DataType.Single ? AsSingle(column, AsNumber(column, value))
                : AsNumber(column, value);
            if (written is null)
            {
                if (ReferenceEquals(normal, value)) continue;
                written = new Row(row);
            }
            written.Put(column.Name, normal);
        }
        return written ?? row;
    }

    /// <summary>
    /// <paramref name="value"/>, a number given for the Decimal column <paramref name="column"/>, as the row stores
    /// it: a double or a float taken to a decimal as Access takes one (<see cref="Util.AutomationDecimal"/>), and the
    /// places past the column's scale cut off, toward zero, as ACE stores them (<see cref="NumericCodec.AsStored"/>,
    /// #80). The value itself when it is that decimal already. A number with more digits than the column holds is
    /// refused when the row is encoded.
    /// </summary>
    /// <exception cref="OverflowException"><paramref name="value"/> is a number past what a decimal holds.</exception>
    public static object AsDecimal(Column column, object value)
    {
        decimal stored = NumericCodec.AsStored(Util.AutomationDecimal.From(value, CultureInfo.InvariantCulture), column.Scale);
        return value is decimal given && decimal.GetBits(given).AsSpan().SequenceEqual(decimal.GetBits(stored)) ? value : stored;
    }

    /// <summary>
    /// <paramref name="value"/>, given for the Currency column <paramref name="column"/>, as the row stores it: the
    /// number taken to four places as Access takes it - its exact value, half to even (<see cref="Util.CurrencyRounding"/>,
    /// #65) - as a <see cref="decimal"/>. A value that is not one of .NET's numbers is first taken for the number every
    /// number column takes it for (<see cref="AsNumber"/>). A decimal that is the stored value already is returned as
    /// it is. Refused, before anything is written, when it makes no number, or a number no Currency holds: NaN, an
    /// infinity, or one past the range once taken to four places.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is of a kind no number is made of: a <see cref="Guid"/>, bytes, a <see cref="char"/>.
    /// </exception>
    /// <exception cref="FormatException"><paramref name="value"/> is text that does not read as a number.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is a number no Currency holds.</exception>
    public static object AsCurrency(Column column, object value)
    {
        object number = AsNumber(column, value);
        if (!Util.CurrencyRounding.TryUnits(number, out long units))
            throw new OverflowException(
                $"Column '{column.Name}' is a Currency, and {Util.CurrencyRounding.NotHeld(number)}. Nothing was written.");
        decimal stored = Util.CurrencyRounding.ToDecimal(units);
        return value is decimal given && given == stored ? value : stored;
    }

    /// <summary>
    /// <paramref name="number"/>, given for the Single column <paramref name="column"/>, refused when it is finite and
    /// its nearest Single is not - past 3.4028235E+38 by half a Single's last place or more - as ACE refuses it
    /// ("Overflow", measured for a Double, a Decimal and text parameter). Converted, it was an infinity. The number
    /// itself otherwise.
    /// </summary>
    /// <exception cref="OverflowException"><paramref name="number"/> is past what a Single holds.</exception>
    public static object AsSingle(Column column, object number)
    {
        if (number is float) return number;
        double value = Convert.ToDouble(number, CultureInfo.InvariantCulture);
        if (float.IsInfinity((float)value) && !double.IsInfinity(value))
            throw new OverflowException(
                $"Column '{column.Name}' is a Single, and {ExactNumber.Text(value)} is past what a Single holds, -3.4028235E+38 to 3.4028235E+38. Nothing was written.");
        return number;
    }

    /// <summary>Whether a column of <paramref name="type"/> holds a number: Byte to Large Number, Single, Double, Currency, Decimal.</summary>
    public static bool IsNumberType(DataType type)
        => type is DataType.Byte or DataType.Int16 or DataType.Int32 or DataType.Int64
                or DataType.Single or DataType.Double or DataType.Currency or DataType.Decimal;

    /// <summary>
    /// <paramref name="value"/>, given for the number column <paramref name="column"/>, as the number it stands for
    /// (<see cref="NumberOf"/>): one of .NET's numbers itself, and otherwise what Access stores for it, the one rule
    /// of every number type (#75). Refused, before anything is written, with the column named.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is of a kind no number is made of: a <see cref="Guid"/>, bytes, a <see cref="char"/>.
    /// </exception>
    /// <exception cref="FormatException"><paramref name="value"/> is text that does not read as a number.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is text of a number past what it is read as.</exception>
    public static object AsNumber(Column column, object value)
    {
        if (Util.CurrencyRounding.IsNumber(value)) return value;
        string type = Util.CurrencyRounding.Describe(column.DataType);
        try { return NumberOf(column.DataType, value); }
        catch (InvalidCastException e)
        {
            throw new ArgumentException(
                $"Column '{column.Name}' is a {type}, which holds a number, and {Util.CurrencyRounding.NotANumber(value)}. Nothing was written.", e);
        }
        catch (FormatException e)
        {
            throw new FormatException(
                $"Column '{column.Name}' is a {type}, which holds a number, and {Util.CurrencyRounding.NotANumber(value)}. Nothing was written.", e);
        }
        catch (OverflowException e)
        {
            throw new OverflowException(
                $"Column '{column.Name}' is a {type}, and '{value}' is a number past what it is read as. Nothing was written.", e);
        }
    }

    /// <summary>
    /// The number <paramref name="value"/>, which is not one of .NET's numbers, stands for in a column of
    /// <paramref name="type"/>, as Access stores it there (measured with ACE 16's typed parameters and DAO's
    /// <c>AddNew</c>, for every number type, #75; Java Jackcess takes a <c>Boolean</c> and a date alike):
    /// <list type="bullet">
    /// <item>a <see cref="bool"/> is -1 for true and 0 for false, Access's own values, as an <see cref="int"/>: a
    /// column that holds no -1, a Byte, then refuses true as it refuses -1, as DAO does;</item>
    /// <item>text is read in the current culture, as Access reads it by the machine's settings, in the forms Access
    /// reads: a currency symbol, parentheses for a negative number, group separators and an exponent among them
    /// ("$5" is 5, "(5)" is -5, "1e2" is 100, and with this machine's settings "1,5" is 15). It is read as a
    /// <see cref="double"/> for a Single or a Double column and as a <see cref="decimal"/> for any other, so that a
    /// whole-number column takes "2.5" as it takes 2.5m - to 2, half to even, as Access takes it;</item>
    /// <item>a <see cref="DateTime"/> is its day number, days since 30 December 1899, as a <see cref="double"/>, the
    /// number a Date/Time column stores for it: Access stores 2 January 2020 12:00 as 43832.5 in a Double and 43832
    /// in a Long Integer;</item>
    /// <item>an enum is its own number.</item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidCastException">
    /// <paramref name="value"/> is of a kind no number is made of: a <see cref="Guid"/>, bytes, a <see cref="char"/>
    /// or anything else. Access refuses a GUID and bytes.
    /// </exception>
    /// <exception cref="FormatException"><paramref name="value"/> is text that does not read as a number.</exception>
    /// <exception cref="OverflowException">
    /// <paramref name="value"/> is text of a number past what a <see cref="decimal"/> holds, or for a Single or a
    /// Double column past what a <see cref="double"/> holds, or not a number at all (NaN, an infinity).
    /// </exception>
    public static object NumberOf(DataType type, object value)
    {
        switch (value)
        {
            case bool b:
                return b ? -1 : 0;
            case string text when type is DataType.Single or DataType.Double:
                double number = double.Parse(text, NumberStyles.Any, CultureInfo.CurrentCulture);
                // .NET reads a number past a double's range as an infinity, and .NET Framework refuses it: the same here.
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new OverflowException($"'{text}' is not a number a {Util.CurrencyRounding.Describe(type)} holds.");
                return number;
            case string text:
                return decimal.Parse(text, NumberStyles.Any, CultureInfo.CurrentCulture);
            case DateTime date:
                return date.ToOADate();
            case Enum:
                return Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture);
            default:
                throw new InvalidCastException($"A {value.GetType().Name} is no number.");
        }
    }
}
