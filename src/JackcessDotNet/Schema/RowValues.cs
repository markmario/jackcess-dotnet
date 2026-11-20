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
    /// column's value as the <see cref="bool"/> it stands for, and a Currency column's as the <see cref="decimal"/>
    /// the row stores (<see cref="AsCurrency"/>). Each value goes to one column: the column of exactly its name, or
    /// else the first whose name Access takes as one with it and that no other value is for (<see cref="ObjectNames"/>)
    /// - so a table with two columns whose names are one to Access, which a file can have, takes a value in each. A
    /// value no column is for is left out. The row itself when it is already so; otherwise a copy - the caller's row
    /// is not changed.
    /// </summary>
    /// <exception cref="ArgumentException">A Currency column's value is of a kind no number is made of.</exception>
    /// <exception cref="FormatException">A Currency column's value is text that does not read as a number.</exception>
    /// <exception cref="OverflowException">A Currency column's value is a number no Currency holds.</exception>
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
                : column.DataType == DataType.Currency && value is not null ? AsCurrency(column, value)
                : value;
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
    /// <paramref name="value"/>, given for the Currency column <paramref name="column"/>, as the row stores it: the
    /// number taken to four places as Access takes it - its exact value, half to even (<see cref="Util.CurrencyRounding"/>,
    /// #65) - as a <see cref="decimal"/>. A value that is not one of .NET's numbers is first taken for the number every
    /// number column takes it for (<see cref="Util.CurrencyRounding.NumberOf"/>: text read in the current culture, a
    /// <see cref="bool"/> as 1 or 0). A decimal that is the stored value already is returned as it is. Refused, before
    /// anything is written, when it makes no number, or a number no Currency holds: NaN, an infinity, or one past the
    /// range once taken to four places.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is of a kind no number is made of: a date, a <see cref="Guid"/>, bytes, a <see cref="char"/>.
    /// </exception>
    /// <exception cref="FormatException"><paramref name="value"/> is text that does not read as a number.</exception>
    /// <exception cref="OverflowException"><paramref name="value"/> is a number no Currency holds.</exception>
    public static object AsCurrency(Column column, object value)
    {
        object number = value;
        if (!Util.CurrencyRounding.IsNumber(value))
        {
            try { number = Util.CurrencyRounding.NumberOf(value); }
            catch (InvalidCastException e)
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' is a Currency, which holds a number, and {Util.CurrencyRounding.NotANumber(value)}. Nothing was written.", e);
            }
            catch (FormatException e)
            {
                throw new FormatException(
                    $"Column '{column.Name}' is a Currency, which holds a number, and {Util.CurrencyRounding.NotANumber(value)}. Nothing was written.", e);
            }
            catch (OverflowException e)
            {
                throw new OverflowException(
                    $"Column '{column.Name}' is a Currency, and '{value}' is a number past what a decimal holds. Nothing was written.", e);
            }
        }
        if (!Util.CurrencyRounding.TryUnits(number, out long units))
            throw new OverflowException(
                $"Column '{column.Name}' is a Currency, and {Util.CurrencyRounding.NotHeld(number)}. Nothing was written.");
        decimal stored = Util.CurrencyRounding.ToDecimal(units);
        return value is decimal given && given == stored ? value : stored;
    }
}
