using System.Globalization;

namespace JackcessDotNet;

/// <summary>
/// Where a row is in its table's file: the number of the data page it is on and its number among that page's
/// rows, as Jet addresses a row and as Jackcess's <c>RowId</c> holds it. A row that was read has one
/// (<see cref="Row.Id"/>), and it is what <see cref="Table.Update"/> and <see cref="Table.Delete"/> find the row
/// by, whatever key the table has or lacks.
/// <para>
/// An id names a place, not a row for ever: a row that is updated is written again elsewhere and has another id
/// from then on (the <see cref="Row"/> given to the update is told it), and compacting a database in Access
/// gives every row a new one. Ids are ordered by page and then by row, which is the order
/// <see cref="Table.Rows"/> reads rows in.
/// </para>
/// </summary>
public readonly struct RowId : IEquatable<RowId>, IComparable<RowId>
{
    /// <summary>Creates the id of the row numbered <paramref name="rowNumber"/> on page <paramref name="pageNumber"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageNumber"/> is not 0 to 16,777,215, or <paramref name="rowNumber"/> is not 0 to 255: the
    /// three bytes and the one byte Jet holds them in.
    /// </exception>
    public RowId(int pageNumber, int rowNumber)
    {
        if (pageNumber < 0 || pageNumber > RowPointer.MaxPage)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "A row is on a page of 0 to 16,777,215.");
        if (rowNumber < 0 || rowNumber > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(rowNumber), rowNumber, "A page's rows are numbered 0 to 255.");
        PageNumber = pageNumber;
        RowNumber  = rowNumber;
    }

    /// <summary>The number of the data page the row is on.</summary>
    public int PageNumber { get; }

    /// <summary>The row's number among the rows of its page, from 0.</summary>
    public int RowNumber { get; }

    /// <summary>The id as one number (<see cref="RowPointer"/>), as an index entry holds it.</summary>
    internal int Pointer => RowPointer.Pack(PageNumber, RowNumber);

    /// <summary>The id an index entry's pointer is.</summary>
    internal static RowId FromPointer(int pointer) => new(RowPointer.Page(pointer), RowPointer.Row(pointer));

    /// <summary>Whether <paramref name="other"/> is the same page and row.</summary>
    public bool Equals(RowId other) => PageNumber == other.PageNumber && RowNumber == other.RowNumber;

    /// <summary>Whether <paramref name="obj"/> is a <see cref="RowId"/> of the same page and row.</summary>
    public override bool Equals(object? obj) => obj is RowId other && Equals(other);

    /// <summary>A hash of the page and row.</summary>
    public override int GetHashCode() => unchecked(PageNumber * 397 ^ RowNumber);

    /// <summary>
    /// Orders ids by page and then by row: less than zero when this id comes before <paramref name="other"/>,
    /// zero when they are the same, more than zero when it comes after.
    /// </summary>
    public int CompareTo(RowId other)
        => PageNumber != other.PageNumber ? PageNumber.CompareTo(other.PageNumber) : RowNumber.CompareTo(other.RowNumber);

    /// <summary>The id as "page:row".</summary>
    public override string ToString()
        => PageNumber.ToString(CultureInfo.InvariantCulture) + ":" + RowNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether two ids are the same page and row.</summary>
    public static bool operator ==(RowId left, RowId right) => left.Equals(right);

    /// <summary>Whether two ids differ in page or row.</summary>
    public static bool operator !=(RowId left, RowId right) => !left.Equals(right);
}
