# Data types

The names `DataType` gives Access's column types, and what is particular to reading and writing some of them:
Large Number and Date/Time Extended, Currency, Decimal, Date/Time, text, binary values, Yes/No and calculated
columns. How a lookup compares a caller's value with each type is in
[How a lookup matches values](cursors-and-rows.md#how-a-lookup-matches-values), and which column an import makes
for each CLR type, and what comes back, in the
[type round-trip cheat sheet](importing-and-exporting.md#type-round-trip-cheat-sheet).

## DataType names

`DataType` members are named for the CLR type of that meaning where there is one - `Int16` (Access's
Integer), `Int32` (Long Integer), `Int64` (Large Number), `Single`, `Double`, `Decimal`, `DateTime`, `Guid`
(Replication ID) - and otherwise by Access's word: `Currency`, `Text`, `Memo`, `OleObject`, `Binary`,
`DateTimeExtended`.

## Large Number and Date/Time Extended

Large Number and Date/Time Extended columns (Access 2016 and 2019) are read, written, created and indexed. A
Large Number is a `long`. A Date/Time Extended is a `DateTime`: Access keeps the years 1 to 9999 to 100
nanoseconds, which is a `DateTime`'s own range and tick, so the value comes back to the tick; it has no time
zone, and is read with `DateTimeKind.Unspecified`. Its index key is made of the 42 bytes the row stores, in
whichever form Access wrote them, as Access makes it. The bytes are read as Access reads them, not as fields at
fixed places, so a value DAO stored through a table-type recordset (`dbOpenTable`), whose `AddNew` and `Edit`
write a layout of their own, is read as its date ([#64]). Measured with DAO 16 and ACE 16, on bytes changed in a
file Access made: the value ends at its first zero byte; the day is read from its start, the time of day after the
first colon and the count of the fraction's digits after the last, and fewer than two colons are an error; a number
begins with a digit, white space or a plus sign - anything else there, or nothing, is an error - and its digits are
read up to the first byte that is not one, to 64 bits; the day and the count are taken to their low 32 bits, and a
count of 10 or more is an error; the time divided by 10 to the count is in seconds, and the remainder in units of
100 nanoseconds whatever the count. An error is read as null, and so is a value no `DateTime` holds, which Access
does not show as a date of its own: a day after 31 December 9999, a time of day of a day or more, a remainder of
more than seven digits.

Either column is for an `.accdb`, and a table with one makes an older `.accdb` one of a later format:
[A column that changes a file's format](file-formats.md#a-column-that-changes-a-files-format). How a Date/Time
Extended value is found: [Date/Time Extended](cursors-and-rows.md#datetime-extended); how it is matched as a key:
[How keys are matched](relationships.md#how-keys-are-matched).

## Currency

**A Currency is the caller's number taken to four places as Access takes one** - its exact value, half to even -
wherever one is made: a row, its index keys, a relationship's check, a property, an import ([#65]). Measured with
ACE 16 and DAO, which write alike through every path tried: the double 0.03125 is 0.0312, the double 0.00015 -
just under the half it is written as - is 0.0001, 562949953421312.5 is itself, and the decimal 1.23465 is 1.2346.
Text and a `bool` are taken as every number column takes them - text read in the current culture, a `bool` as 1 or
0 - and the number they make is taken to four places so; Access reads text by the machine's locale and writes
True as -1. A date, a `Guid`, bytes or a `char` is refused with `ArgumentException`, and text that reads as no
number with `FormatException`. NaN, an infinity and a number past -922,337,203,685,477.5808 to
922,337,203,685,477.5807 are refused with `OverflowException`; Access refuses the last two ("Overflow"), and
writes NaN as the least Currency, which this library does not. How a caller's number is taken to one when it is
looked for: [Currency](cursors-and-rows.md#currency).

**A file an earlier version wrote holds that version's rounding.** 1.2.2, and this library until [#65], rounded a
Currency half away from zero, after `Convert.ToDecimal`, which on .NET Framework and .NET up to 10 keeps 15
significant digits of a `double` and 7 of a `float`. The values are consistent with one another and with Access,
and nothing needs mending, but some rows are no longer found by the number they were written from: a lookup, a
range, `FindClosestRowByEntry`, a relationship's check and a unique index all take the number as Access does now,
and come to the neighbouring row or to none. The numbers that differ: a `decimal` at a half of the fifth place
(1.23465m was written as 1.2347 and is now 1.2346); a `double` or `float` that is an exact binary half of the
fifth place, such as 0.03125 (written as 0.0313, now 0.0312); and on .NET Framework and .NET up to 10, a `double`
whose 15-digit form is such a half (0.00015, written as 0.0002, now 0.0001) and a `float` of more than 7
significant digits (98765432f, written as 98765430). So `FindRowByPrimaryKey(1.23465m)` in such a file finds the
row of 1.2346, a `Delete` of it deletes that row, and a child given 0.03125 has no parent written from 0.03125.
**Find such rows by the value stored - as a row reads it back - and not by the number they were written from.**
The [CHANGELOG's breaking changes](../CHANGELOG.md#breaking-changes-to-the-public-api) give more examples.

## Decimal

- **Decimal values an earlier version wrote read back as different numbers.** A Decimal is stored as four
  32-bit words; earlier versions (the `JackcessDotNet` package up to 1.2.2 among them:
  [Which files](earlier-versions.md#which-files)) wrote and read sixteen bytes in the other order, so they and
  Access never agreed (a 1 written by an earlier version is 16,777,216 to Access, and to this library too). Such a
  value cannot be told from one Access wrote; one that reads with more digits than its column's precision is
  almost certainly one. Access-written Decimals, which earlier versions misread, read correctly.

  `DecimalByteOrder.Find(database)`, and the same for a `Table`, in the `JackcessDotNet.Diagnostics` namespace,
  lists the values that read with more digits than their column holds: the table, the column, the row (its
  primary key, and its place among the table's rows), the value as it reads and the value its bytes make in the
  earlier order. It changes nothing - a read returns what it did - and repairs nothing:

  ```csharp
  foreach (var suspect in DecimalByteOrder.Find(db))
      Console.WriteLine(suspect);
  // Prices.Price, row 1 (Id=2): reads 16777216.00, 1.00 in the earlier byte order
  ```

  It finds some such values, not all: a number written in the earlier order can still read within its column's
  precision. In a column of precision 7 or less, every number under 4,294,967,296 (before its decimal point is
  placed) that is not a multiple of 256 is listed; of precision 8 or 9, some; of precision 10 or more - 18 is
  the default, Access's and this library's - none, and only larger numbers can be. An empty list does not say
  the file has no such value.
- **A stored number that runs past 96 bits** - the first of the four words set, which Access never writes - reads
  as the number its low 96 bits make, with its sign, as Access reads it through its OLE DB provider and through
  DAO; earlier versions read it as null. 2^96 + 12345 in a `DECIMAL(5,2)` column is 123.45. Such a value is
  always listed by `DecimalByteOrder.Find`, with `IsCut` set, whatever its low 96 bits make. Java Jackcess returns
  all 128 bits as a `BigDecimal`, which a `decimal` cannot hold.
- **Neither Access nor Java Jackcess checks a Decimal against its column's precision when it reads one.** Jackcess
  returns the number. In Access, DAO returns it and SQL computes with it (the 1 above is found by
  `WHERE Price = 16777216`), but the OLE DB provider cannot hand it over - reading the column fails with
  "Conversion failed because the Decimal data value overflowed the type specified" - and a query that sorts its
  rows, or writes the value back, is refused: "The decimal field's precision is too small to accept the numeric
  you attempted to add."

A Decimal column has no index keys this library writes: [Indexes kept on every write](indexes.md#indexes-kept-on-every-write).

## Date/Time

A Date/Time is read and written to the millisecond. An update of another column leaves a Date/Time Access stored
finer than that, or outside what `DateTime` holds, as it is
([Reading, updating and deleting rows](cursors-and-rows.md#reading-updating-and-deleting-rows)), and a lookup
finds every row whose number reads as the `DateTime` given ([Date/Time](cursors-and-rows.md#datetime)).

## Text and Memo

- **Fixed-length text (SQL `CHAR(n)`) is returned filled to its length with spaces**, as Access returns it, and
  is written that way.
- Text longer than its column is refused:
  [When a change is refused](cursors-and-rows.md#when-a-change-is-refused). How text is compared and keyed, by its
  column's sort order: [Text](cursors-and-rows.md#text) and
  [Text index keys](indexes.md#text-index-keys-sort-orders-and-limits). Where a Memo's value is kept:
  [Long values](storage.md#long-values).

## Binary and OLE Object

- **An empty byte array is a value of no bytes** in a Binary or OLE Object column: written as ACE's OLE DB writes
  one, and read back as `byte[0]`, as ACE's OLE DB and SQL read it (`IS NULL` is false, `LenB` is 0). DAO - and so
  Access's own forms - reads it as Null, and DAO writes an empty array as Null. An import writes one as an insert
  does.
- A `byte[]` longer than a Binary column is refused:
  [When a change is refused](cursors-and-rows.md#when-a-change-is-refused).

## Yes/No

A Yes/No holds no null: what a null given one becomes, and how it is matched as a key, is in
[How keys are matched](relationships.md#how-keys-are-matched); an import refuses a null for one
([type round-trip cheat sheet](importing-and-exporting.md#type-round-trip-cheat-sheet)).

## Calculated columns

**A calculated column reads as null**, and a table with one is not written to: an insert, an update and a delete
are each refused with `NotSupportedException`.

[Back to contents](README.md)

[#64]: https://github.com/markmario/jackcess-dotnet/issues/64
[#65]: https://github.com/markmario/jackcess-dotnet/issues/65
