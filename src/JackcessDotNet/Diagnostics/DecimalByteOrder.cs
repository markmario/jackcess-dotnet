namespace JackcessDotNet.Diagnostics;

/// <summary>
/// Looks for Decimal values that were stored with their bytes in an order Access does not use, as earlier
/// versions of this library stored them (the <c>JackcessDotNet</c> package up to 1.2.2 among them). Nothing is
/// changed, in the file or in what a read returns.
/// <para>
/// Those versions wrote a Decimal's sixteen bytes in an order of their own, so every value they wrote reads as a
/// different number - a 1 as 16,777,216 - in Access and in this library. Nothing in the file says who wrote a
/// value. What can be seen is a value its column could not have been given: Access refuses a number with more
/// digits than the column's precision, and so does this library, so one that reads that way was almost
/// certainly written in the earlier order, and <see cref="DecimalBeyondPrecision.ValueInEarlierByteOrder"/> is
/// what it was meant to be.
/// </para>
/// <para>
/// Some such values are found, not all, and how many depends on the precision. A number under 4,294,967,296
/// (before its decimal point is placed) written in the earlier order reads as one of at most ten digits, at
/// least eight when it is not a multiple of 256. So in a column of precision 7 or less every such number that
/// is not a multiple of 256 is listed; of precision 8 or 9, some are; and of precision 10 or more - 18 is what
/// Access and this library give a column by default - none is, and only numbers of ten digits or more can be.
/// A table with nothing listed may still hold values written in the earlier order.
/// </para>
/// <para>
/// A stored number that runs past 96 bits is always listed: it is past every precision, and Access writes
/// none. A read gives its low 96 bits, as Access does (<see cref="DecimalBeyondPrecision.IsCut"/>), and those
/// can be a number the column would hold - the listing says what the row holds, not only how it reads.
/// </para>
/// </summary>
public static class DecimalByteOrder
{
    /// <summary>
    /// Lists the Decimal values in the database's tables - not its system tables - whose stored number has more
    /// digits than their column's precision allows: what <see cref="Find(Table)"/> lists for each table, in the
    /// order <see cref="Database.GetTableNames"/> names them.
    /// </summary>
    /// <param name="database">The database to look through.</param>
    /// <returns>
    /// The values found, a table at a time. The tables' names are read at the call; each table is opened and
    /// read as the result is enumerated.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="database"/> is disposed.</exception>
    public static IEnumerable<DecimalBeyondPrecision> Find(Database database)
    {
        if (database is null) throw new ArgumentNullException(nameof(database));
        return database.GetTableNames().SelectMany(name => database.GetTable(name).FindDecimalsBeyondPrecision());
    }

    /// <summary>
    /// Lists the Decimal values in the table whose stored number has more digits than their column's precision
    /// allows, in the order of the table's rows and, within a row, of its columns. A calculated column is not
    /// looked at.
    /// </summary>
    /// <param name="table">The table to look through.</param>
    /// <returns>The values found. The table is read as the result is enumerated, a row at a time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    public static IEnumerable<DecimalBeyondPrecision> Find(Table table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        return table.FindDecimalsBeyondPrecision();
    }
}
