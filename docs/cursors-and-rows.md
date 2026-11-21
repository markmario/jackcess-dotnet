# Rows and cursors

How rows are read, inserted, updated and deleted; how a cursor goes through a table or an index and finds rows;
how a lookup matches the values it is given with the values a row stores; and what makes a change refused. Where
rows go on the file's pages is in [Storage](storage.md), and the types of a row's values are in
[Data types](data-types.md).

## Reading, updating and deleting rows

```csharp
// Every row that is read knows where it is (Row.Id: its page and its row on the page). Update and Delete find
// the row by it, so a table with a key of several columns, or with none, is changed like any other.
Row order = orders.FindRowByPrimaryKey(42, 7)!;
order["Total"] = 99.5m;
orders.Update(order);                           // only what changed is written
orders.Delete(order);

// A batch goes in whole or not at all.
try { people.InsertRange(rows); }
catch (BatchInsertException e) { Console.WriteLine($"row {e.RowIndex}: {e.InnerException!.Message}"); }

// Many rows - an export of millions - go in once, all or none, without being held. Every set of duplicates a
// unique index would refuse can be found first.
foreach (var duplicate in people.FindDuplicates(rows)) Console.WriteLine(duplicate);   // "ByEmail: rows 3, 17"
int loaded = people.BulkInsert(ReadRowsFromSomewhere());
```

- **A row a caller makes holds each value under the name given**, exactly: `row["Name"]` and `row["NAME"]` are two
  entries. An insert, an update and `Validate` give each name the column of exactly that name, or else the one a
  lookup of it finds, and refuse two names that go to one column with `ArgumentException` before anything is
  written ([Names](tables-and-schema.md#names)).
- **Into a table with no rows, a batch's indexes are built once its rows are in** (`InsertRange` and
  `BulkInsert`), as [Indexes built page after page](indexes.md#indexes-built-page-after-page) describes.
  `BulkInsert` goes through the rows once and holds none of them; `InsertRange` holds them all, so that a refused
  batch can give each back as it came. A refused `BulkInsert` leaves a row inserted before the refusal holding
  the AutoNumber values it was given, though none of the rows is in the table.
- **`FindDuplicates(rows)`** names every set of rows a unique index or primary key would refuse - two or more of
  them under one key, or one with a row already in the table - by the index's key, as Access compares: text that
  differs only in case, in trailing spaces, or in a character Access sorts as another (a full-width letter or
  digit, "ß" and "ss", "æ" and "ae") is one key; a leading space, an accent or other punctuation is another.
  `Validate` looks at one row against the table; this looks at a batch.
- **`Validate(row)` encodes the row as `Insert` would, and writes nothing**: each Memo or OLE value is placed where
  the insert would put it - in the row, in one long-value row, or a chain - by the table's own long-value pages, so
  a Memo that begins with U+FEFF is refused only where the insert refuses it, and a row too large for its page is
  refused with `InvalidOperationException` as the insert refuses it ([#141]).

- **A row keeps its place when it is updated**, as Access keeps it, so its `Row.Id` does not change and every
  index entry that names it stays good. A row that still fits its page is rewritten in its slot, the page's
  other rows moved up or down around it; one that no longer fits goes to another page, and its slot is left
  four bytes that say where (Access's overflow row); it comes home when it fits again. Measured from Access
  (DAO, ACE 16) and written the same. How rows are laid out on pages: [Storage](storage.md#rows-on-a-page).
- **An update assigns what the `Row` was given.** `Update(row)` assigns the columns given a value since the row
  was read - the value it had among them - as Access's `Edit` and `Update` assign a field: each is held to its
  column's Required and Allow Zero Length (DAO refuses a field given the `""` it holds once the column takes
  none; so does this), and written when it is not what the file holds now. Every other column is left as the
  file has it, unread: a long value is neither read nor written again, and a Date/Time keeps every digit Access
  stored. (A byte array changed in place and not given to the row again is not assigned.) An insert, an update
  and a delete each change the row, its long values and its index entries together or not at all.
- **An update leaves the stored bytes of every column it does not name.** A Date/Time Access wrote finer than a
  millisecond, or outside what `DateTime` holds, is not rounded or lost by an update of another column. Each of
  those values is still held to its column's length, as the row is written whole: a Text or Binary value stored
  longer than its column - an earlier version of this library could write one
  ([Files written by earlier versions](earlier-versions.md#other-things-an-earlier-version-wrote)) - refuses an
  update of the row with `ColumnValueTooLongException`, naming that column and saying the value is the stored
  one, until the update gives that column a value that fits.
- **Room a delete or an update leaves is used again where Access uses it** ([#58]; see
  [Where a new row goes](storage.md#where-a-new-row-goes)). A deleted row's bytes leave its page and the rows
  after them close up; its slot stays, marked deleted: a `Row`, a cursor or a savepoint that read the row finds no
  row there, and an update or a delete through it is refused with `InvalidOperationException`, however the page is
  used again.

## Cursors

```csharp
// A cursor finds rows by their values, and updates or deletes the row it is on.
var cursor = people.CreateCursor();
for (var row = cursor.FindRow("Name", "Alice"); row is not null; row = cursor.FindNextRow("Name", "Alice"))
    cursor.UpdateCurrentRow(new Row { ["Name"] = "Alicia" });
cursor.ColumnNames = new[] { "Id", "Name" };    // read these columns only
foreach (Row row in cursor.EnumerateBackward()) { }
object? born = cursor.FindRow("Id", 1) is null ? null : cursor.GetCurrentRowValue("Born");
var here = cursor.GetSavepoint();               // ... and later: cursor.RestoreSavepoint(here);
```

- **Rows can be updated while they are gone through** - with `Table.Rows`, a cursor, an index cursor's rows of
  an entry, or a `Joiner` - and each is come to once. In a table's own order no row moves. In an index's order
  a row moves when an update changes its key in that index, and a cursor over the index is told, by whoever made
  the update (an insert, and an entry an index that ignores nulls gains or loses, too). Going one way the
  cursor gives each row once: a row it has given is passed if its entry has moved ahead of the cursor, and a
  row it has not given is given when the cursor comes to its entry, wherever it has been meanwhile - a row
  outside its range, or one it looked past, among them. A cursor that turns back begins again from where it is,
  giving again the rows it passed but not the row it is on; a savepoint keeps what the cursor had given, kept up
  as rows move. A cursor is told of a change once it is in the file for good, and holds a few stretches of its
  index however many rows it moves.
- **A scan in a table's own order gives the rows the table had when it began** - `Table.Rows`, and a cursor from
  when it lists the table's pages, which it does again each time it is put before the first row or after the last.
  A row inserted while it goes is not given, whichever page the row goes to: a new one, or one where a delete or an
  update left room, which can be a page the scan has still to come to. A row deleted while it goes is passed.
  These two are of changes made through the `Database` being read. Another program's are told to no cursor or scan:
  what a `ReadOnly` database gives while one writes is under
  [Reading beside a writer](sharing.md#reading-beside-a-writer).

## Index cursors and joins

```csharp
// An index cursor goes through its index's rows in the index's order: all of them, or those between two entries.
var byDate = orders.CreateIndexCursor("ByCustomerAndDate", new IndexRange
{
    StartEntry = new object?[] { 42, new DateTime(2024, 1, 1) },
    EndEntry   = new object?[] { 42, new DateTime(2025, 1, 1) },
    IsEndInclusive = false,
});
foreach (Row row in byDate) { }
Row? first   = byDate.FindRowByEntry(42, new DateTime(2024, 3, 1));          // that entry, or null
Row? closest = byDate.FindClosestRowByEntry(42, new DateTime(2024, 3, 1));   // that entry, or the next after it
foreach (Row row in orders.CreateIndexCursor("ByCustomerAndDate").FindRowsByEntry(42)) { }   // every order of customer 42

// A joiner finds the rows of one table that go with a row of another, through an index.
var ordersOf = new Joiner(customers, new[] { "CustomerID" }, orders, new[] { "CustomerID" });
foreach (Row row in ordersOf.FindRows(alfki!)) { }
```

- **An index cursor keeps to its index.** Its rows are the index's entries, in their order, and every lookup
  goes through that index or refuses the values: an entry has at most as many values as the index has columns.
  With fewer it stands for every row with those values in its leading columns.

Which indexes there are and how their keys are made: [Indexes and keys](indexes.md).

## How a lookup matches values

A cursor over a table and a cursor over an index find the same rows, by one rule, set out below for each kind of
value. A relationship matches keys by a rule of its own, as Access's indexes hold them:
[How keys are matched](relationships.md#how-keys-are-matched).

### Text

Text is equal as its column's sort order has it, which is what Access's `=` and a unique index go by (measured in
General - Legacy and General): `'abc'` is `'ABC'`, `'a'` is `'a '`, `'ss'` is `'ß'`; `'e'` is not `'é'`, and
`'7'` is not `'007'`. Where this library makes no key for the text (another sort order, a character keyed by its
neighbours) it is equal when it is the same text. Text is never read as a number.

### Numbers

Numbers are equal across number types when they are exactly the same number. A Byte, Integer, Long or Large
Number column's 5 is a caller's 5L, 5m or 5.0, and no number that is not that whole number (5.5, 5.5m, a double
5.000000000000001) is in it. ACE's `=` does not always answer so, and not alike through an index and without one;
where its two answers differ, this library gives the exact one, whichever that is. As ACE's scan does, -0.0 finds
0, -2^63 as a double finds a Large Number's `long.MinValue`, and a decimal with a fraction finds nothing (in a
Large Number, ACE's scan found 0 for every decimal), where ACE's index finds nothing for -0.0 and -2^63 and rounds
the decimal to a whole number (5.5m finds 6, 0.1m finds 0, -1.23465m finds -1). As ACE's index does, a double
finds only the Large Number it is exactly - 2^53 does not find 2^53 + 1 - where ACE's scan compares the two as
doubles.

### Currency

A Currency is the caller's number taken to its four places as a row written with it stores it - its exact value,
half to even, as Access takes one ([#65]) - by an equality, in a range and for `FindClosestRowByEntry` alike, as
ACE's `=`, `BETWEEN`, `>=`, `>`, `<=` and `<` through an index and DAO's `Seek` take it: the double 0.03125 finds
0.0312, the double 0.00015, which is just under that half, finds 0.0001, and 1.23465m finds 1.2346. ACE's scan,
with no index to answer, takes a decimal through a double, and finds 1.2347 for it. Text is no number to an
equality, as in every number column, and a range takes it as a write does. NaN, an infinity or a number past the
range is no Currency: it finds no row, and a range or `FindClosestRowByEntry` refuses it with `ArgumentException`,
as ACE's refuses a number past the range. A row an earlier version wrote holds that version's rounding, and some
such rows are found by the value they hold and not by the number they were written from (see
[Currency](data-types.md#currency)).

### Double, Single and Decimal

A Double or a Single is the caller's number as the column stores it, by its bits, as ACE's index has it: +0.0
and -0.0 are two numbers, where ACE's scan finds both for either. A Decimal is the same number exactly, a
caller's `double` or `float` taken to a decimal as Access takes one - to 15 or 7 significant digits, as OLE
Automation rounds it - so the double 0.1 finds the Decimal 0.1, and is written as 0.1, on every runtime (.NET
11's own conversion is exact). A number with more places than a Decimal column holds finds no row - 1.015 is not
the row of 1.01 a write of 1.015 stores ([Decimal](data-types.md#decimal)) - as ACE's `=` with the same parameter
finds none, through an index and without one (measured, [#80]). A negative zero, which a negative number cut to
nothing is stored as, is zero here, as to ACE's scan: ACE's index tells -0.00 from 0.00, so through one `WHERE D = 0`
finds the row of 0.00 alone, and without one both. NaN finds nothing in a number column, where ACE's
scan finds every row for it, and ACE's `=` a Currency column's least value.

### Date/Time

A Date/Time is the number the row stores. A `DateTime` - taken first to what it reads as once stored, to the
millisecond, so `DateTime.Now` finds the row written with it - finds every row whose number **reads as** that
`DateTime`: so the date read from a row finds the row again, which `DateTime.ToOADate()` does not for about a
third of the dates Access computes (`DateAdd`, a date plus fractions of a day). Through an index that is the
small range of numbers that read as it (two, for a time of 30 December 1899, which -0.25 and 0.25 both read as).
In a range's entry, in any place, and given to `FindClosestRowByEntry`, a `DateTime` is put against a row by the
date its number reads as: a range of dates has the rows whose dates are in it. A `double` is the number itself:
it finds the row that stores exactly that number, and `cursor.GetCurrentRowDateNumber(column)` gives it, so two
dates within a millisecond are told apart; in a range it is put against the numbers, as Access's `BETWEEN` and
an index's `Seek` put a date literal (measured: `BETWEEN #12/30/1899 05:00# AND #12/30/1899 07:00#` leaves out a
06:00 stored as -0.25). The rows come in the index's order, which is the numbers', as Access's `ORDER BY` has
them: before 31 December 1899 a day's times run back from its midnight. A `Joiner` joins a row that was read on
its stored number, as Access's join does, unless the row has been given a date in that column since.

### Date/Time Extended

A Date/Time Extended is the 42 bytes the row stores, which Access keys and orders a value by ([#51]), and reads as
Access reads them ([Large Number and Date/Time Extended](data-types.md#large-number-and-datetime-extended)). Access
writes a date in three layouts (measured): its `INSERT` in one; its SQL `UPDATE`, DAO's `AddNew` and `Edit` on a
dynaset, and its own forms and datasheets in another, the time of day in whole seconds; and DAO's `AddNew` and
`Edit` on a table-type recordset - what DAO's `OpenRecordset` gives a table when it is asked for no other kind - in
a third, the day in fifteen digits and four bytes after the value that are whatever DAO's buffer held ([#64]). A
`DateTime` finds every row whose bytes say that date: through an index under the key of each of the first two and
among the keys that begin with the third's other bytes, where Access's own `=` through an index finds the first
alone (measured; without an index it finds all three). A value in a shape that reads as its date and that Access
was not seen to write - a space or a plus sign among its digits, fewer digits, a count other than 7 or 0 - is found
by a cursor over the table, and not through an index, which holds it under a key of its own. In a range's entry and
given to `FindClosestRowByEntry`, a `DateTime` is the first form's key and a row is put against it by its key, as
Access's `BETWEEN` through an index puts it (measured): a row in another layout is where its bytes are. A row in the
third comes after every row of the other two from the year 2 on in an ascending index (before them in a descending
one), so ACE's own `K > '2020-01-02 03:04:05'` through an ascending index gives the rows in the third layout of that
very date and of earlier ones, which without an index it does not (measured). A `Joiner` joins a row that was read
on its stored bytes, as a relationship does, unless the row has been given a date in that column since.

### Null

A null is never a match.

## Ranges and the closest row

A range and `FindClosestRowByEntry` need the key Access keeps for the values, and are refused with
`NotSupportedException` where this library makes none: a Decimal, Binary or OLE column, text in a sort order
whose keys it does not make ([Refused](indexes.md#refused)), a character Access keys by its neighbours, or a key
longer than an entry holds ([Keys longer than 510 bytes](indexes.md#keys-longer-than-510-bytes)).
`FindRowByEntry` and `FindRow` still find such a row, by going through the index's rows.

## When a change is refused

- **A change the data does not allow** - a row that would refer to no parent, a duplicate in a primary key or a
  unique index, a null in a key - throws `ConstraintViolationException`, with or without
  `Database.EnforceForeignKeys` ([Relationships and foreign keys](relationships.md)). It derives from
  `InvalidOperationException`, which is what these threw before it, so a handler for that still catches one.
- **A change a damaged index cannot take is refused** ([#81]). When an index, taken at its word, does not hold a
  row's entry under the row's key - an entry under another key, or none, as an earlier version or another program
  can leave - an update that changes the row's key in that index, and a delete of the row, throw
  `NotSupportedException` before anything is written. Access refuses both on such a file ("The search key was not
  found in any record", measured with ACE and DAO) and changes nothing; an update of the row's other columns it
  makes, and so does this library. Made, the update left the row in the index twice, and DAO's compaction copied
  the row twice and dropped the key. The check costs a sound index no page: the pages it reads are the ones the
  entry's removal goes down. [`VerifyIndexes`](earlier-versions.md#indexes-that-do-not-hold-every-row) makes such
  a change, and mends the index.
- **A row that cannot be written changes nothing.** The whole of a new row is encoded before anything is
  written - for an update, before the row it replaces is touched - so a value its column cannot hold, a value
  not of its type, or a row too large leaves the file byte for byte as it was. A failure of the file itself (the
  disk, the stream) while a change is being written is taken back too: an insert, an update, a delete, a table
  created, or a change to a table's definition or properties leaves the file byte for byte as it was.
- **A value longer than its column is refused** before anything of its row is written, with
  `ColumnValueTooLongException`, as Access refuses it ("The field is too small to accept the amount of data you
  attempted to add"): text with more characters than a Text column's length, and a `byte[]` with more bytes than a
  Binary column's, whether the definition keeps the column among the variable-length values (SQL's `VARBINARY`,
  DAO's `dbBinary`, what this library makes) or the fixed-length ones (SQL's `BINARY`, filled with zeros to its
  length).
- **A table can be asked first**: `Table.InsertRefusal` is why it takes no row - the message `Insert` would
  throw - or null when it takes rows, and asking changes nothing. A caller that writes to several tables asks
  each before it writes to any.

## What cursors do not do

Column matchers of a caller's own (Jackcess's `ColumnMatcher`); and a null is not found by looking for null,
where Jackcess finds one.

[Back to contents](README.md)

[#51]: https://github.com/markmario/jackcess-dotnet/issues/51
[#58]: https://github.com/markmario/jackcess-dotnet/issues/58
[#64]: https://github.com/markmario/jackcess-dotnet/issues/64
[#65]: https://github.com/markmario/jackcess-dotnet/issues/65
[#80]: https://github.com/markmario/jackcess-dotnet/issues/80
[#81]: https://github.com/markmario/jackcess-dotnet/issues/81
[#141]: https://github.com/markmario/jackcess-dotnet/issues/141
