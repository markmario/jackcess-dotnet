# Data types

The names `DataType` gives Access's column types, and what is particular to reading and writing some of them:
Large Number and Date/Time Extended, Currency, Decimal, Date/Time, text, binary values, Yes/No, calculated and
complex columns. How a lookup compares a caller's value with each type is in
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

## A bool, text or a date for a number

**A `bool`, text and a date given for a number column are what Access stores for them**, in every number type
alike - Byte, Integer, Long Integer, Large Number, Single, Double, Currency and Decimal ([#75]; measured with ACE
16's typed parameters and DAO's `AddNew`, `tools/ace-number-kinds.ps1`):

- **`true` is -1** and `false` 0, Access's own values.
- **Text is read in the current culture**, as Access reads it by the machine's settings, in the forms Access reads:
  a currency symbol ("$5" is 5), parentheses for a negative number ("(5)" is -5), an exponent ("1e2" is 100), group
  separators and spaces around it ("1,5" is 15 where the comma groups digits). A whole-number column takes "2.5" as
  2 and "1.5" as 2, half to even, as Access does. Text is read as a `decimal` for a column other than a Single or a
  Double. Text that is no number ("True", "abc", "") is refused with `FormatException`, as Access refuses it.
- **Text for a Double or a Single is the number Access stores for it, on every target** ([#124]): Access reads the
  text's first 20 significant digits for a Double and its first 12 for a Single, leading zeros not among them, and
  stores the double or the single nearest the number they make, a tie to the even significand; the digits after
  those make no difference. "1.0635554516475241859652" is the double of 1.0635554516475241859, and "-0" is -0.0.
  Measured with ACE 16 - text literals and text parameters in an `INSERT` - and DAO's `AddNew`, which store the
  same, for 13,276 texts, most at and next to the half between two doubles, or two singles, of every range
  (`tools/ace-text-to-float.ps1`). The runtime's own parse did not do that on any target: of those texts .NET
  stored another double for 1,025 and another single for 1,793, rounding every digit; .NET Framework another double
  for 1,620 in a 64-bit process and 402 in a 32-bit one, and another single for 1,882, read through its double. A
  Double or Single property given text takes it the same way, as DAO does, in the invariant culture - though in
  fewer forms than DAO reads ([#138]).
- **A `DateTime` is its day number**, days since 30 December 1899, as Access and Java Jackcess take one: 2 January
  2020 12:00 is 43832.5 in a Double, and 43832 in a Long Integer.
- **A number the column does not hold is refused** with `OverflowException`: `true`, which is -1, and a date in a
  Byte, a date in an Integer. DAO refuses them too; ACE wraps a negative number into a Byte (-1 is 255).
- **A number past what a Single holds is refused** with `OverflowException`, as ACE refuses it ("Overflow"): a finite
  number, or text of one, whose nearest Single is not finite - 1e39, 3.5e38. It was stored as an infinity.
- **A `Guid`, bytes, a `char` or any other kind is refused** with `ArgumentException`, naming the column, before
  anything is written. Access refuses a GUID and bytes.

**Where Access differs:** ACE reads the text "&H10" as 16, a hexadecimal number in Visual Basic's form; this library
does not read that form, and refuses such text with `FormatException`. And in two ranges of the least doubles,
from 1E-308 to 1E-304 and under about 1E-321, ACE stores some text one place from the double nearest the number of
its first 20 digits: 73 of the 13,276 texts measured, all in those ranges. "2.4703282292062328E-324", past the half
between 0 and the least double, is 0 to ACE. Its arithmetic there was not found; this library stores the nearest
([#136]).

**32-bit ACE reads some text otherwise.** All of the above is 64-bit ACE 16, today's Access, which this library
follows on every target and in a process of either bitness: a file holds one number for a text, whatever wrote it.
32-bit ACE computes with the x87 unit, and stores some text for a Double one unit in the last place from that
number, anywhere in the range and not only near a half:
- Access 2007's 32-bit ACE 12 (this repository's measuring machine; `tools/ace-text-to-float.ps1 -Picks 3` in the
  32-bit Windows PowerShell): 377 of 3,957 texts one unit off as a Double, 2 of 311 as a property's value, and none
  as a Single. It also refuses text of the largest double, which 64-bit ACE stores (13 texts), and writes as text -0.0
  as "0", 999999999999999.5 and the double before 1E+15 as "1E+15", and NaN and the infinities as "-1.#IND",
  "1.#QNAN", "1.#INF" and "-1.#INF".
- The Access Database Engine 2016 redistributable's 32-bit ACE 16 (16.0.5011.1000, on CI's runners, by the referee
  test's sample): every text for a Double it stored otherwise one unit off, 39 seen; no Single otherwise; and as text
  -0.0 as "0" and 999999999999999.5 as "1E+15", as ACE 12 writes them.

Neither is rounding the text's first 20 digits to the x87's 64 bits and then to a double's 53: that fits 38 of the 39
of ACE 16, and leaves 199 of ACE 12's 3,957 texts unexplained, so this library does not imitate either.

A range or the closest row through an index takes these values the same way; an equality takes text and a `bool`
for no number ([Numbers](cursors-and-rows.md#numbers)). The importer keeps its own rule: only a value a column holds
exactly ([type round-trip cheat sheet](importing-and-exporting.md#type-round-trip-cheat-sheet)).

## Currency

**A Currency is the caller's number taken to four places as Access takes one** - its exact value, half to even -
wherever one is made: a row, its index keys, a relationship's check, a property, an import ([#65]). Measured with
ACE 16 and DAO, which write alike through every path tried: the double 0.03125 is 0.0312, the double 0.00015 -
just under the half it is written as - is 0.0001, 562949953421312.5 is itself, and the decimal 1.23465 is 1.2346.
Text, a `bool` and a date are taken as every number column takes them
([A bool, text or a date for a number](#a-bool-text-or-a-date-for-a-number)) and the number they make is taken to
four places so. A `Guid`, bytes or a `char` is refused with `ArgumentException`, and text that reads as no number
with `FormatException`. NaN, an infinity and a number past -922,337,203,685,477.5808 to
922,337,203,685,477.5807 are refused with `OverflowException`; Access refuses the last two ("Overflow"). 64-bit ACE
16 writes NaN as the least Currency, which this library does not; 32-bit ACE 12, and the 32-bit ACE 16 of the 2016
redistributable, refuse it ("Overflow"). How a caller's number is taken to one when it is
looked for: [Currency](cursors-and-rows.md#currency).

**A file an earlier version wrote holds that version's rounding.** 1.2.2, and this library until [#65], rounded a
Currency half away from zero, after `Convert.ToDecimal`, which on .NET Framework and .NET up to 10 keeps 15
significant digits of a `double` and 7 of a `float`. The values are consistent with one another and with Access,
and nothing needs mending, but some rows are no longer found by the number they were written from: a lookup, a
range, `FindClosestRowByKey`, a relationship's check and a unique index all take the number as Access does now,
and come to the neighbouring row or to none. The numbers that differ: a `decimal` at a half of the fifth place
(1.23465m was written as 1.2347 and is now 1.2346); a `double` or `float` that is an exact binary half of the
fifth place, such as 0.03125 (written as 0.0313, now 0.0312); and on .NET Framework and .NET up to 10, a `double`
whose 15-digit form is such a half (0.00015, written as 0.0002, now 0.0001) and a `float` of more than 7
significant digits (98765432f, written as 98765430). So `FindRowByPrimaryKey(1.23465m)` in such a file finds the
row of 1.2346, a `Delete` of it deletes that row, and a child given 0.03125 has no parent written from 0.03125.
**Find such rows by the value stored - as a row reads it back - and not by the number they were written from.**
The [CHANGELOG's breaking changes](../CHANGELOG.md#breaking-changes-to-the-public-api) give more examples.

## Decimal

- **A number with more places than the column holds is stored as ACE stores it** ([#80]): the places past the
  column's scale are cut off, toward zero - 1.015 is 1.01 in a column of scale 2, -2.5 is -2 in one of scale 0 - and
  a negative number cut to nothing is a negative zero, as ACE writes it; a zero given, of either sign, has no sign.
  The same on every runtime. Measured with ACE 16 for 5,080 numbers - typed Decimal, Double, Single and text
  parameters, every scale from 0 to 28, both signs, in an `.mdb` and an `.accdb` (`tools/ace-decimal-places.ps1`).
  DAO refuses every one of them ("Scaling of decimal value resulted in data truncation"); ACE is the referee here,
  as for every value written. Earlier versions rounded half away from zero. A lookup compares the number given with
  the one stored exactly ([Double, Single and Decimal](cursors-and-rows.md#double-single-and-decimal)), and the
  importer, which takes only a value a column holds exactly, refuses such a number, as DAO does.
- **Decimal values an earlier version wrote read back as different numbers.** A Decimal is stored as four
  32-bit words; earlier versions (the `JackcessDotNet` package up to 1.2.2 among them:
  [Which files](earlier-versions.md#which-files)) wrote and read sixteen bytes in the other order, so they and
  Access never agreed (a 1 written by an earlier version is 16,777,216 to Access, and to this library too). Such a
  value cannot be told from one Access wrote; one that reads with more digits than its column's precision is
  almost certainly one. Access-written Decimals, which earlier versions misread, read correctly.

  `DecimalByteOrder.Find(database)`, and the same for a `Table`, in the `JackcessDotNet.Diagnostics` namespace,
  lists the values that read with more digits than their column holds: the table's name (`TableName`), the
  column, the row (its primary key, and its place among the table's rows), the value as it reads and the value its
  bytes make in the earlier order. It changes nothing - a read returns what it did - and repairs nothing:

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

The number a row stores - days since 30 December 1899 - is read and written as `DateTime.FromOADate` and `ToOADate`
convert it in a 64-bit process, but in integers, so that every runtime and every process converts it alike ([#100]).
Those two compute in doubles, which a 32-bit .NET Framework process computes with the x87 unit, and ACE's OLE DB
provider leaves the thread that ran a statement with that unit at 64 bits of precision instead of 53 (measured with
32-bit ACE 12). There, of 15 million numbers chosen either side of a millisecond's half, 241 read as the millisecond
beside the one a 64-bit process reads; of 3.4 million dates, 783 were written as the double beside. As a 64-bit
process reads it, a number's product with 86,400,000 is rounded to a double before the half is added, so a number a
hair under a half can read as the next millisecond: 5.7870370370370361E-09 reads as 00:00:00.001 on 30 December 1899.
A 64-bit process's runtime computes doubles with SSE2, not the x87 unit, and there the runtime's two conversions give
exactly what the integers do, five to ten times as fast: a 64-bit process converts with them, and a 32-bit one with
the integers ([#125]). A number out of range is refused alike by both, with
an `ArgumentException` of the same message and no parameter name.

## Text and Memo

- **Fixed-length text (SQL `CHAR(n)`) is returned filled to its length with spaces**, as Access returns it, and
  is written that way.
- **Text is read and written as the UTF-16 code units it holds**, half of a surrogate pair alone among them, as
  Access keeps a value or a name with one: a Text value, a Memo in its row or on pages of its own, and the names
  of tables, columns and indexes. `Encoding.Unicode` would read and write U+FFFD in its place ([#63]).
- **Text whose bytes begin 0xFF is read and written as Access reads and writes it** ([#93]), measured with
  Microsoft 365's ACE 16 and DAO by `tools/ace-text-marker.ps1`. 0xFF 0xFE is the mark of compressed text - a byte
  a character, until a 0x00 switches to UTF-16LE, read two bytes at a time until a pair begins with 0x00 - and the
  UTF-16LE of U+FEFF is 0xFF 0xFE too.
  - Text that begins with U+FEFF is written as ACE writes it: compressed in runs, characters 0x01 to 0xFF a byte
    each and the rest two, where that is no longer than UTF-16LE in a Text column with Unicode compression, and
    where it is shorter in a Memo of up to 1,908 characters (in a column without Unicode compression, a Memo kept
    in its row). Otherwise it is UTF-16LE.
  - Bytes that begin 0xFF 0xFE are compressed text in a Text column with Unicode compression, in a Memo kept in
    its row, whatever its column, and in a Memo in one long-value row of a column with Unicode compression. In a
    Text column without it, on a long-value page of such a column, and in a chain of long-value rows, they are
    UTF-16LE.
  - **This library reads such bytes as Access's table-type recordset does** ([#129]): in a Text column without
    Unicode compression, as UTF-16LE. Not every way Access reads gives that. Measured with Microsoft 365's ACE 16
    and DAO 16.0 (16.0.20625.20102, 64-bit) by `tools/earlier-filled-text-readers.ps1`, on
    `engine-8e41563-filled-table.mdb`, whose `Filled` has such values
    ([Files written by earlier versions](earlier-versions.md#other-things-an-earlier-version-wrote)). The Name of
    row 7 is `FF FE 4C 6F 6E 67`, and of row 1 `Alice`'s `FF FE 41 6C 69 63 65`, an odd number of bytes:
    - Row 7 reads as UTF-16LE, U+FEFF U+6F4C U+676E, and row 1 as U+FEFF U+6C41 U+6369, through a DAO table-type
      recordset; a DAO dynaset of the table, of a query of it, of a query sorted by Id, and of a query of the one
      row; and an ACE query of the table, and of the one row.
    - Row 7 reads as compressed text, `Long`, and row 1 fails with "Number of bytes of a UNICODE string is not
      even", through a DAO snapshot of a query of the one row, and an ACE query of the one row sorted by Name.
    - Every other DAO snapshot - of the table, of a query of it, sorted or not - a DAO dynaset of a query sorted by
      Name, and an ACE query of the table sorted by Id or by Name, fail as they open, with the same message.

    So the line falls neither between a dynaset and a snapshot nor between a query that sorts and one that does
    not. The table holds the bytes as written.
  - So Access reads back changed the text that begins with U+FEFF that it keeps as UTF-16LE where it reads
    compressed text, and **this library refuses such text**, with an `ArgumentException` (by insert, update, batch
    insert and import) before anything is written. The rule: text that begins with U+FEFF is refused
    - in a Text column with Unicode compression, unless compressed in runs it is no longer than UTF-16LE and holds
      no NUL and no character above 0xFF with a low byte of 0x00 (U+0100, U+3000): so U+FEFF alone, U+FEFF and up
      to three characters 0x01 to 0xFF, and U+FEFF followed mostly by other characters are refused;
    - in a Memo kept in its row (64 bytes or fewer, in any column) or in one long-value row of a column with Unicode
      compression (up to 3,816 bytes), unless compressed in runs it is shorter than UTF-16LE. ACE fails outright to
      read one of these, of 3,814 bytes.

    It is kept in a Text column without Unicode compression, on a long-value page of a Memo column without it,
    and in a chain. A Memo of more than 3,816 bytes of UTF-16LE that begins with U+FEFF, in a column with Unicode
    compression, is written as a chain, as ACE writes it - this library otherwise keeps up to 4,076 bytes in one
    long-value row - which Access and this library read as UTF-16LE.
  - 0xFF followed by any byte but 0xFE is UTF-16LE: `"\u01FFab"` is `FF 01 61 00 62 00`.
  - A table an earlier version of this library created - its catalog row has ParentId 0 and no owner, where Access
    gives a table the Tables container and an owner - has its name and its Text and Memo values read by their bytes:
    as compressed text wherever they begin 0xFF 0xFE, as those versions wrote them
    ([Files written by earlier versions](earlier-versions.md#other-things-an-earlier-version-wrote)). A name that
    begins with U+FEFF given to such a table is written and refused as its Text values are ([#129]).
- **A `double` or a `float` given for a Text or a Memo column is the text Access writes for it, on every target**
  ([#124]): 15 significant digits for a double and 7 for a single, a half away from zero, in the current culture's
  decimal separator and negative sign. The number is written out unless it is 10^15 or more (10^7 for a single)
  before it is rounded, or has more than 15 places after the point (7) once it is: then with an exponent of two
  digits or more. 0.1 + 0.2 is "0.3", 1E-05 is "0.00001", 1.23456789012345E-05 stays so, 999999999999999.5 is
  "1000000000000000" and 1E+15 is "1E+15"; 16777216f is "1.677722E+07"; -0.0 is "-0"; NaN is "-nan(ind)" and the
  infinities "inf" and "-inf". Measured with ACE 16 - a Double or Single parameter, and a Double or Single column
  copied by `INSERT ... SELECT` - and DAO's `AddNew` and a Text property, which all write the same
  (`tools/ace-text-to-float.ps1`). The runtime wrote its own: .NET the round-trip form ("0.30000000000000004"), .NET
  Framework 15 digits but "1E-05" and "0" for -0.0. A lookup of a Text column by a double or a float
  looks for that text, so a row an earlier version wrote with the runtime's text - "0.30000000000000004" for
  0.1 + 0.2 - is not found by the double, but by its text
  ([Files written by earlier versions](earlier-versions.md#other-things-an-earlier-version-wrote)). A Text property
  given one is written the same, in the invariant culture.
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

## Complex columns

An attachment, a multi-value field and a column's version history (`DataType.Complex`, Access 2007 and later) keep
their values in a hidden table of their own, which this library does not read ([#24]).

- **A complex column reads as null**, in every row and however the table is read: `Table.Rows`, a cursor, an index
  cursor, `FindRowByPrimaryKey`. The row holds a number for the column, the key of the row's values in the hidden
  table, and that number is not the column's value, so it is not given either.
- **In a `DataTable`** (`ToDataTable`, `ToDataSet`) a complex column is a column of type `object`, each row's value
  `DBNull.Value`. An object made by `ReadRows<T>` keeps whatever its property for the column already holds.
- **A table with a complex column is not written to.** Access gives every row a number for its complex columns
  from a counter of the table's own, whether or not the row has values, and deletes a row's values with it. This
  library does neither, so an insert, an update and a delete are refused with `NotSupportedException`, and
  `Table.InsertRefusal` says why. Access keeps a unique index on each complex column, which it does not let be
  dropped, so a table Access made is refused for that index first.
  Measured with ACE 16 (`tools/dao-complex-rows.ps1`).

A complex column is not added, dropped or renamed: [Tables and schema changes](tables-and-schema.md).

[Back to contents](README.md)

[#24]: https://github.com/markmario/jackcess-dotnet/issues/24
[#63]: https://github.com/markmario/jackcess-dotnet/issues/63
[#64]: https://github.com/markmario/jackcess-dotnet/issues/64
[#65]: https://github.com/markmario/jackcess-dotnet/issues/65
[#75]: https://github.com/markmario/jackcess-dotnet/issues/75
[#80]: https://github.com/markmario/jackcess-dotnet/issues/80
[#93]: https://github.com/markmario/jackcess-dotnet/issues/93
[#100]: https://github.com/markmario/jackcess-dotnet/issues/100
[#124]: https://github.com/markmario/jackcess-dotnet/issues/124
[#125]: https://github.com/markmario/jackcess-dotnet/issues/125
[#129]: https://github.com/markmario/jackcess-dotnet/issues/129
[#136]: https://github.com/markmario/jackcess-dotnet/issues/136
[#138]: https://github.com/markmario/jackcess-dotnet/issues/138
