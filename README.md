# Jackcess.Net

[![NuGet](https://img.shields.io/nuget/v/Jackcess.Net?logo=nuget)](https://www.nuget.org/packages/Jackcess.Net)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

Pure managed library for reading and writing Microsoft Access (`.mdb` / `.accdb`) files.
No ODBC, no ACE drivers, no native dependencies. It builds for .NET 10, .NET 11, .NET Framework 4.8
and .NET Standard 2.0.

This project started from release 1.2.2 of
[`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet) and is now
maintained on its own, with its own package, `Jackcess.Net`. It is not a release of the Jackcess project, and
neither the Jackcess authors nor Mehran Ghanizadeh maintain or endorse it. It differs from that port in two ways:

- **.NET Framework.** The `JackcessDotNet` package on nuget.org ships a `net10.0` assembly and nothing
  else (checked for 2.0.0 and 3.1.0), so nothing on .NET Framework can use it. This one ships `net10.0`,
  `net11.0`, `net48` and `netstandard2.0`. See [Supported frameworks](#supported-frameworks).
- **Files Microsoft Access accepts.** The files this library writes are checked against Access's own engine
  (ACE), not only read back by the library that wrote them. See [What this fork changes](#what-this-fork-changes).

The library is a C# port of the [`spannm/jackcess`](https://github.com/spannm/jackcess) Java
project (a maintained fork of the original [Jackcess](https://jackcess.sourceforge.io/)
by James Ahlborn). The Java project remains the source of truth for the Jet
file format; this port tracks its behaviour and reuses its test corpus for
verification.

## Installing

```
dotnet add package Jackcess.Net
```

The package ID is not the assembly name. The assembly and the namespace are `JackcessDotNet`, as in the
port this started from, so `using JackcessDotNet;` is unchanged; `dotnet add package JackcessDotNet` installs
that other port's package, not this one. Versions start at 2.0.0, because the public API changed after the
1.2.2 this started from. If you distribute a build, the [Apache License 2.0](LICENSE) and [NOTICE](NOTICE)
have to travel with it; the package carries both.

### Supported frameworks

The package has four assemblies, and NuGet gives an application the nearest one:

| The application runs on | It gets | Package dependencies | The tests run it on |
|---|---|---|---|
| .NET 10 | `net10.0` | none | .NET 10 |
| .NET 11 and later | `net11.0` | none | .NET 11 |
| .NET Framework 4.8 and 4.8.1 | `net48` | `System.Memory`, `Microsoft.Bcl.HashCode` | .NET Framework 4.8 |
| .NET 8 and 9, .NET Framework 4.6.2 to 4.7.2, anything else on .NET Standard 2.0 (Mono, Unity) | `netstandard2.0` | those two, `System.ComponentModel.Annotations` and `System.Text.Encoding.CodePages` | .NET 10 and .NET Framework 4.8 |

Every row is the whole test suite, on every pull request. The `netstandard2.0` assembly is not run on .NET 8 or
9, on .NET Framework 4.7.2, or on Mono or Unity themselves: .NET 10 and .NET Framework 4.8 stand in for them.

There is no `net472` assembly: an application on .NET Framework 4.7.2 gets the `netstandard2.0` one, and
.NET Framework 4.8 is the oldest with an assembly of its own. `net48` rather than `net481` because 4.8 runs on
more versions of Windows and the code is the same. The public API is the same in all four.

It can also be referenced from source. QCAT's ExportMdb.Interop takes this repository as a git submodule,
references `src/JackcessDotNet/JackcessDotNet.csproj`, and merges the built assembly into its own:

```
git submodule add https://github.com/markmario/jackcess-dotnet.git ext/jackcess-dotnet
```

```xml
<ProjectReference Include="..\..\ext\jackcess-dotnet\src\JackcessDotNet\JackcessDotNet.csproj" />
```

A build here writes `Jackcess.Net.0.0.0-local.nupkg` to `artifacts/`: only a release build has a
real version (see [Releasing](#releasing)).

## Quick start

```csharp
using JackcessDotNet;

// Open an existing .mdb / .accdb (auto-detects version)
using var db = Database.Open("Northwind.mdb");

foreach (var name in db.GetTableNames())
    Console.WriteLine(name);

var customers = db.GetTable("Customers");
foreach (var row in customers.Rows)             // read as they are enumerated
    Console.WriteLine($"{row["CustomerID"]}  {row["CompanyName"]}");

Row? alfki = customers.FindRowByPrimaryKey("ALFKI");

// A cursor moves both ways.
var cursor = customers.CreateCursor();
while (cursor.MoveNext())
    Console.WriteLine(cursor.Current!["CompanyName"]);

// Create a new database, define a schema, insert rows. With no format it is an Access 2000-2003 .mdb;
// FileFormat.Access2007, Access2010, Access2016 and Access2019 are .accdb.
using var fresh = Database.Create("MyData.mdb");
using var accdb = Database.Create("MyData.accdb", FileFormat.Access2010);
var people = fresh.CreateTable("People", new[]
{
    new ColumnDefinition("Id",   DataType.Int32) { IsAutoNumber = true },
    new ColumnDefinition("Name", DataType.Text)  { MaxLength = 50 },
    new ColumnDefinition("Born", DataType.DateTime),
}, "Id");
var alice = new Row { ["Name"] = "Alice" };
people.Insert(alice);                           // alice["Id"] is now 1

// A row that is read has every column, null for one with no value.
Row read = people.FindRowByPrimaryKey(1)!;
string? name   = read.GetValue<string>("Name");
DateTime? born = read.GetValue<DateTime?>("Born");   // null

// A row that is read is updated or deleted by where it was read, whatever key its table has.
read["Born"] = new DateTime(1990, 4, 1);
people.Update(read);
```

## Cursors and rows

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

// A cursor finds rows by their values, and updates or deletes the row it is on.
var cursor = people.CreateCursor();
for (var row = cursor.FindRow("Name", "Alice"); row is not null; row = cursor.FindNextRow("Name", "Alice"))
    cursor.UpdateCurrentRow(new Row { ["Name"] = "Alicia" });
cursor.ColumnNames = new[] { "Id", "Name" };    // read these columns only
foreach (Row row in cursor.EnumerateBackward()) { }
object? born = cursor.FindRow("Id", 1) is null ? null : cursor.GetCurrentRowValue("Born");
var here = cursor.GetSavepoint();               // ... and later: cursor.RestoreSavepoint(here);

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

- **A row keeps its place when it is updated**, as Access keeps it, so its `Row.Id` does not change and every
  index entry that names it stays good. A row that still fits its page is rewritten in its slot, the page's
  other rows moved up or down around it; one that no longer fits goes to another page, and its slot is left
  four bytes that say where (Access's overflow row); it comes home when it fits again. Measured from Access
  (DAO, ACE 16) and written the same.
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
- **An update assigns what the `Row` was given.** `Update(row)` assigns the columns given a value since the row
  was read - the value it had among them - as Access's `Edit` and `Update` assign a field: each is held to its
  column's Required and Allow Zero Length (DAO refuses a field given the `""` it holds once the column takes
  none; so does this), and written when it is not what the file holds now. Every other column is left as the
  file has it, unread: a long value is neither read nor written again, and a Date/Time keeps every digit Access
  stored. (A byte array changed in place and not given to the row again is not assigned.) An insert, an update
  and a delete each change the row, its long values and its index entries together or not at all.
- **An index cursor keeps to its index.** Its rows are the index's entries, in their order, and every lookup
  goes through that index or refuses the values: an entry has at most as many values as the index has columns.
  With fewer it stands for every row with those values in its leading columns.
- **A cursor over a table and a cursor over an index find the same rows**, by one rule:
  - *Text* is equal as its column's sort order has it, which is what Access's `=` and a unique index go by
    (measured in General - Legacy and General): `'abc'` is `'ABC'`, `'a'` is `'a '`, `'ss'` is `'ß'`; `'e'` is
    not `'é'`, and `'7'` is not `'007'`. Where this library makes no key for the text (another sort order, a
    character keyed by its neighbours) it is equal when it is the same text. Text is never read as a number.
  - *Numbers* are equal across number types when they are exactly the same number. A Byte, Integer, Long or
    Large Number column's 5 is a caller's 5L, 5m or 5.0, and no number that is not that whole number (5.5, 5.5m,
    a double 5.000000000000001) is in it. ACE's `=` does not always answer so, and not alike through an index
    and without one; where its two answers differ, this library gives the exact one, whichever that is. As
    ACE's scan does, -0.0 finds 0, -2^63 as a double finds a Large Number's `long.MinValue`, and a decimal with
    a fraction finds nothing (in a Large Number, ACE's scan found 0 for every decimal), where ACE's index finds
    nothing for -0.0 and -2^63 and rounds the decimal to a whole number (5.5m finds 6, 0.1m finds 0, -1.23465m
    finds -1). As ACE's index does, a double finds only the Large Number it is exactly - 2^53 does not find
    2^53 + 1 - where ACE's scan compares the two as doubles.
  - *A Currency* is the caller's number taken to its four places as a row written with it stores it - its exact
    value, half to even, as Access takes one ([#65](https://github.com/markmario/jackcess-dotnet/issues/65)) - by
    an equality, in a range and for `FindClosestRowByEntry` alike, as ACE's `=`, `BETWEEN`, `>=`, `>`, `<=` and
    `<` through an index and DAO's `Seek` take it: the double 0.03125 finds 0.0312, the double 0.00015, which is
    just under that half, finds 0.0001, and 1.23465m finds 1.2346. ACE's scan, with no index to answer, takes a
    decimal through a double, and finds 1.2347 for it. Text is no number to an equality, as in every number
    column, and a range takes it as a write does. NaN, an infinity or a number past the range is no Currency: it
    finds no row, and a range or `FindClosestRowByEntry` refuses it with `ArgumentException`, as ACE's refuses a
    number past the range. A row an earlier version wrote holds that version's rounding, and some such rows are
    found by the value they hold and not by the number they were written from (see
    [Rows and long values](#rows-and-long-values-what-to-know)).
  - *A Double or a Single* is the caller's number as the column stores it, by its bits, as ACE's index has it:
    +0.0 and -0.0 are two numbers, where ACE's scan finds both for either. *A Decimal* is the same number
    exactly, a caller's `double` or `float` taken to a decimal as Access takes one - to 15 or 7 significant
    digits, as OLE Automation rounds it - so the double 0.1 finds the Decimal 0.1, and is written as 0.1, on
    every runtime (.NET 11's own conversion is exact). NaN finds nothing in a number column, where ACE's scan
    finds every row for it, and ACE's `=` a Currency column's least value.
  - *A Date/Time* is the number the row stores. A `DateTime` - taken first to what it reads as once stored, to
    the millisecond, so `DateTime.Now` finds the row written with it - finds every row whose number **reads
    as** that `DateTime`: so the date read from a row finds the row again, which `DateTime.ToOADate()` does not
    for about a third of the dates Access computes (`DateAdd`, a date plus fractions of a day). Through an index
    that is the small range of numbers that read as it (two, for a time of 30 December 1899, which -0.25 and
    0.25 both read as). In a range's entry, in any place, and given to `FindClosestRowByEntry`, a `DateTime` is
    put against a row by the date its number reads as: a range of dates has the rows whose dates are in it.
    A `double` is the number itself: it finds the row that stores exactly that number, and
    `cursor.GetCurrentRowDateNumber(column)` gives it, so two dates within a millisecond are told apart; in a
    range it is put against the numbers, as Access's `BETWEEN` and an index's `Seek` put a date literal
    (measured: `BETWEEN #12/30/1899 05:00# AND #12/30/1899 07:00#` leaves out a 06:00 stored as -0.25). The rows
    come in the index's order, which is the numbers', as Access's `ORDER BY` has them: before 31 December 1899 a
    day's times run back from its midnight. A `Joiner` joins a row that was read on its stored number, as
    Access's join does, unless the row has been given a date in that column since.
  - *A Date/Time Extended* is the 42 bytes the row stores, which Access keys and orders a value by
    ([#51](https://github.com/markmario/jackcess-dotnet/issues/51)): its `INSERT` writes a date in one form, and its
    SQL `UPDATE` and DAO's `AddNew` and `Edit` on a dynaset in another, the time of day in whole seconds (measured;
    on a table-type recordset the two write a layout of their own, which is read as null:
    [#64](https://github.com/markmario/jackcess-dotnet/issues/64)). A `DateTime` finds every row whose bytes say that
    date: through an index under the key of each form, where Access's own `=` through an index finds the first form
    alone (measured; without an index it finds both). A value in a form that reads as its
    date and that Access was not seen to write - a space or a plus sign among its digits - is found by a cursor over
    the table, and not through an index, which holds it under a key of its own. In a range's entry and given to
    `FindClosestRowByEntry`, a `DateTime` is the first form's key and a row is put against it by its key, as
    Access's `BETWEEN` through an index puts it (measured): a row in the second form is where its bytes are. A
    `Joiner` joins a row that was read on its stored bytes, as a relationship does, unless the row has been given a
    date in that column since.
  - A null is never a match.
- **A range and `FindClosestRowByEntry` need the key Access keeps for the values**, and are refused with
  `NotSupportedException` where this library makes none: a Decimal, Binary or OLE column, text in a sort order
  whose keys it does not make (below), a character Access keys by its neighbours, or a key longer than an entry
  holds (below). `FindRowByEntry` and `FindRow` still find such a row, by going through the index's rows.
- **Room a delete or an update leaves is used again where Access uses it** (#58; see
  [Rows and long values](#rows-and-long-values-what-to-know)). A deleted row's bytes leave its page and the rows
  after them close up; its slot stays, marked deleted: a `Row`, a cursor or a savepoint that read the row finds no
  row there, and an update or a delete through it is refused with `InvalidOperationException`, however the page is
  used again.
- **Not there:** column matchers of a caller's own (Jackcess's `ColumnMatcher`); a null is not found by
  looking for null, where Jackcess finds one.

## Opening password-protected files

```csharp
// An .mdb with a database password, or an .accdb encrypted with a password: what the file is, its header says.
using var db = Database.Open("Confidential.mdb", new DatabaseOpenOptions { Password = "passw0rd" });
```

A file is protected as its header says, and its password is checked as Access checks it: exactly, letter case and
all (#46). Each kind below that Access's engine still makes was made by it (DAO 16, and Jet 4.0 for Access 97, in
`tools/protected-fixtures.ps1`), opened here with its password, a wrong one and none, and compared table by table
with what DAO reads; and what this library wrote to each was read, searched through its primary key, added to and
compacted by Access (ExportMdb.Interop's `ProtectedFilesInAccessTests`).

| The file | Opens | Written |
| --- | --- | --- |
| An .mdb (Access 2000-2003) with a database password | with that password and no other | ✅ as any .mdb: the password changes no page |
| An .mdb whose pages Access encoded (its Encrypt Database command, DAO's `dbEncrypt`) | with any password or none, as Access opens it - unless it has a database password too | ✅ each page is decoded as it is read and encoded as it is written: RC4 under the key the header holds |
| An Access 97 .mdb with a database password, or encoded | as an Access 2000-2003 one | ❌ read-only, as every Access 97 file |
| An .accdb with a password: Agile Encryption, which Access 2010 and later make, and ACE 16 for either .accdb format | with that password and no other | ✅ each page is decrypted as it is read and encrypted as it is written |
| An .accdb in RC4 CryptoAPI (Access 2007's), or the non-standard AES Jackcess-encrypt reads | with that password and no other | ✅ the same; refereed with jackcess-encrypt's files of them, as ACE 16 makes neither |
| An .accdb in ECMA Standard Encryption | with that password and no other; read as the specification says, which is unverified: no Access is known to make such a file, and none was found | ❌ read-only (`NotSupportedException` before anything is written): what this library would write could not be checked against Access |
| An .accdb encrypted by an extensible (external) provider | ❌ `NotSupportedException` | |

- A file with a password opened with another, or with none, throws `InvalidCredentialException` before anything
  past its header is read.
- A file that is not protected opens whatever password is given: the password is not looked at.
- A file whose catalog does not read, with nothing in its header to account for it, is refused as damaged with
  `NotSupportedException`, whatever password is given; so is an .accdb whose header describes its encryption in a
  way that does not read (a length past the page, XML that does not parse, a value that is not one), given its
  password, or with a value MS-OFFCRYPTO does not allow or AES and its hash do not take - a salt, block, key or hash
  of another size, a spin count past 10,000,000 - which is refused as the file is opened, not at a page, and not
  spun.
- An .mdb's password is at most 20 characters, which is all DAO takes; an Access 97 file keeps it in the code page
  of its text (1252), converted as Windows converts text to it: a character the code page lacks is the one Windows'
  best fit gives for it (L for Ł) or a question mark (Cyrillic, Chinese). Jet 3 checks a password given after the
  same conversion, and so does this library: `Пароль密码` opens such a file with `ПАРОЛЬ密码` too, and with
  `????????` (`tools/jet3-password-fixture.ps1`). The conversion is Windows', the same on every target: .NET's own
  best fit for the code page makes a question mark of eleven characters - spaces of other widths, dashes, quotation
  marks - that Windows gives a character of the code page for.
- The descriptor Access writes for an Agile-encrypted database has no data-integrity element, so there is no hash
  for a write to leave stale: Access reads what this library wrote as it reads its own.

## Composite (multi-column) primary keys

```csharp
var orders = db.CreateTable("Orders", new[]
{
    new ColumnDefinition("CustomerId", DataType.Int32),
    new ColumnDefinition("OrderId",    DataType.Int32),
    new ColumnDefinition("Total",      DataType.Currency),
}, "CustomerId", "OrderId");

Row? order = orders.FindRowByPrimaryKey(42, 7);
```

Up to 10 columns per composite key (Jet's hard limit), named in key order after the columns; none for a
table without a primary key.

`DataType` members are named for the CLR type of that meaning where there is one - `Int16` (Access's
Integer), `Int32` (Long Integer), `Int64` (Large Number), `Single`, `Double`, `Decimal`, `DateTime`, `Guid`
(Replication ID) - and otherwise by Access's word: `Currency`, `Text`, `Memo`, `OleObject`, `Binary`,
`DateTimeExtended`.

## Foreign-key enforcement

```csharp
using var db = Database.Open("Northwind.mdb");
// db.EnforceForeignKeys is true unless it is set false, as in Jackcess; Access always enforces.

// The relationships in MSysRelationships that have referential integrity are kept.
orders.Insert(new Row { ["CustomerID"] = "INVALID", ... });
//   throws ConstraintViolationException: "Foreign-key violation in relationship 'CustomersOrders'..."

Row customer = customers.FindRowByPrimaryKey("ALFKI")!;
customer["CustomerID"] = "ALFKA";
customers.Update(customer);
//   cascade update: the customer's orders take the new key - or, without it, refused while it has orders

customers.Delete(customer);
//   cascade delete: its orders go too, and their order lines; cascade-to-null: their CustomerID becomes null;
//   neither: refused while it has orders
```

`Insert`, `Update` and `Delete` - and a cursor's `UpdateCurrentRow` and `DeleteCurrentRow` - refuse what would leave a row referring to no parent, and carry out
the cascades a relationship asks for, as Access does. A foreign key whose columns are all null refers to nothing.
A refused change leaves the file untouched: everything is checked, cascades included, before anything is written,
and a change that fails while being written is taken back. With the property off, nothing is checked and nothing
cascades.

A change the data does not allow - a row that would refer to no parent, a duplicate in a primary key or a unique
index, a null in a key - throws `ConstraintViolationException`, with or without the property. It derives from
`InvalidOperationException`, which is what these threw before it, so a handler for that still catches one.
`Database.TryGetTable` asks for a table without an exception when there is none.

What it costs: rows are found through the tables' indexes. Access gives every relationship an index on its
foreign key, so an insert reads about a dozen pages to find its parent, and an update or delete reads the rows it
reaches and the index pages that lead to them - a cascade delete of five rows about fifty pages, whether the child
has a thousand rows or a million - and holds only those rows in memory. Every row a cascade rewrites or deletes
keeps its entry in every index of its table, so Access still finds it, and still protects its parent, afterwards.

An index is trusted to hold every row of its table, as Access and Java Jackcess trust one
([qcatco/exportmdb.interop#78](https://github.com/qcatco/exportmdb.interop/issues/78)): rows are found through it,
and a unique index that has no entry for a value is taken to mean no row has it. Nothing is read to check.

**`Database.VerifyIndexes`: for files an earlier version wrote to.** Versions of this library before it kept every index (those in ExportMdb.Interop up to 2.1.2) kept a table's
primary key and no other index, and kept the primary key under the value a row was *found by*. In a table Access
made with indexes, a row one of them inserted is in no index but the primary key; a row one of them updated is in
each other index where it was before the update; and a row whose primary key one of them changed is in the key
under the value it had. Nothing in a file says that one of them wrote to it - a count of entries does not (after
such an update an index has an entry for every row, by number), nor does the number an index's definition keeps,
which is of keys ever added.

With such a file, and by default, this library does what Access does with it
([#64](https://github.com/qcatco/exportmdb.interop/issues/64),
[#73](https://github.com/qcatco/exportmdb.interop/issues/73)):

- enforcing foreign keys, a row that is not in the index on its foreign key is no one's child - its parent is
  deleted or re-keyed and the row left an orphan;
- a second row with the values of a row a unique index does not hold is added. The next time the file is
  compacted Access cannot build that index, and leaves the table without it, or without its primary key.

**Set `db.VerifyIndexes = true` for a file one of those versions wrote rows into and that has not been compacted
in Access since.** Compacting rebuilds the indexes, and the option is then needless. With it set:

- **The first change that looks for rows through an index reads its table once**, while the database is open, and
  compares the two row by row: every row's key must be in the index, pointing at that row. For a unique index
  that is the first `Insert`, `Update` or `Validate` of a session, which reads the table once for all
  the table's unique indexes together; for a relationship, the first change that keeps it.
- **The rows an index does not hold are kept beside it** while the database is open. A row is compared with them
  as well as with the unique index, so the duplicate is refused - as Access refuses it once the file is
  compacted. An index that lacks rows is not searched for a relationship: its table is read instead, as for a
  table with no index (below), so a change is slower and its outcome right. A row this library rewrites gains
  its entries and a row it deletes is gone, and the index is searched once none of them is left.
- **What it costs.** Measured on a table of 1,000,000 rows (30,304 pages) with the file in the operating
  system's cache: with a primary key the first insert read 33,644 pages in half a second and allocated 620 MB in
  passing; with a primary key and a unique index, 36,965 pages in 0.8 to 0.9 seconds and 990 MB. Every insert
  after it, and the first without the option, read 8 and 11 pages in 0.1 and 0.2 milliseconds. For a
  relationship, on a child of 1,000,000 rows and a parent of 200,000, the first cascade delete read 34,860 pages
  in about a second and the deletes after it 65 pages and a millisecond each. Memory is needed in proportion to
  the table's rows while it is read.
- **Nothing is read** for a table with no unique index, for a row with a null in its unique index's columns (it
  is no row's duplicate), or to fill a table made in the same session: it is looked at when it is empty.
- **Every entry seen to point at no row is taken out** when the table's indexes are next changed, whatever the
  key, and an entry seen to be a row's wrong one is moved to the row's key where that key is certain (below). A
  row with no entry at all is not given one: nothing here rebuilds an index, and `DBEngine.CompactDatabase` does.

**Entries that are no row's** ([#36](https://github.com/markmario/jackcess-dotnet/issues/36)). Those versions also
left entries behind: one that points where a row was, and one under the value a row had before its primary key
was changed, pointing at the row under the value it has. Access follows both. Measured with ACE on such a file:
a query that comes to the first fails with "Not a valid bookmark" - for the key, and for any range that passes
it - and goes on failing when the row that has the key has an entry beside it; a query for the second's key is
answered with the row that no longer has it, and that row is not found under the value it has.

- **An entry that points at no row** - a slot deleted or never used, a row number past the page's rows, a page
  that is not one of the table's - is no row's duplicate, and when an entry is added or taken out under a key a
  unique index compares and the tree holds another under that key, it is taken out. A long key an earlier
  version wrote whole is looked for in that form too, and a removal that takes out a leaf's last entry reads the
  next leaf's first, one page more, since the next leaf can begin under the same key.
- **An entry that points at a row of the table is believed**, as Access believes it, whatever this library
  makes of that row's values: a row with its key is refused as a duplicate, and the entry stays. This library's
  key for a row may not be the one Access wrote - a key cut at 510 bytes in an index of several columns other than
  Text and Long, which was not measured - and an entry Access wrote, taken out on that judgement, is a row Access
  can then neither find, update nor delete. A row an index does not hold is a duplicate of nothing, as it is to
  Access, unless `VerifyIndexes` is set.
- **With `VerifyIndexes`**, every entry an index was seen to hold that points at no row goes when the table's
  indexes are next changed, whatever the key; and an entry that points at a row whose key is another, where that
  key is one this library makes as Access does, is that row's wrong one: a row with the entry's key is taken,
  the entry goes, and the row is entered under its own key, so that no row is left without an entry for having
  had a wrong one. What is under that key already is looked at the same way, an entry that is itself a wrong one
  being moved in its turn; beside a row that does have the key the row is entered all the same - the two are
  duplicates whatever the index says. An entry goes and its row's own comes, or neither: a failure part-way is
  undone. An update of the row itself enters it under its new key once: an entry an index holds already is never
  added again. A key is one this library makes as Access does for every type it writes - a Date/Time Extended
  value's made of the bytes its row stores, as Access makes it (below) - and, cut at 510 bytes in an index of
  several columns, only where those columns are Text and Long, the shapes that were measured.
- **A key this library may not make as Access does** - one cut at 510 bytes in an index of several columns other
  than Text and Long - is never looked for through an index, with or without `VerifyIndexes`: the table is read
  for it, so a relationship's child or parent is found however Access keyed it. When a row with such a key is
  deleted or rewritten, its entry is looked for under this library's key, as any row's is, and only when the row has
  no entry there are its entries found by the row they point at - every leaf of the index read - so that none is
  left pointing where the row was.
- **A row whose Date/Time Extended value is in another form than the one this library writes** may have its entry
  where a version of this library before [#51](https://github.com/markmario/jackcess-dotnet/issues/51) put it, when
  such a version added the index or rewrote the row: under the key of the date it read, in the first form, or of no
  value - for Access's second form, before db6f93b. When the row is deleted or its date rewritten and it has no
  entry under the key of its bytes, the entry is looked for under each of those keys, one way down the tree each to
  the key and the row, and taken out when it points at the row. A relationship's search for such a row, a unique
  index's for its duplicate, and a `Joiner`'s by its bytes look under the first form's key too, and read each row
  found there - the row looked for only when it has the very bytes, the others the rows with the date in the first
  form - and a `DateTime` looked for through an index is looked for under that key, which is its first form's. No
  search looks under the key of no value: every row with no value is under it, as an index that does not ignore
  nulls holds them (Access's default for a unique index and a relationship's), and each would be read. So a row the
  versions from 2930d3f to db6f93b entered there - ten hours of `main`, never released - is found when it is deleted
  or its date rewritten, and with `VerifyIndexes`, which reads the table when an index holds a row under another key
  than its own; trusting the index, a relationship, a unique index and a `Joiner` do not find it, as Access does
  not. With `VerifyIndexes`, in an index it looks at (a unique index, or a relationship's), such an entry is the
  row's wrong one, and is moved to the key of the row's bytes.

An index that is as its rows are has none of these entries, and nothing is read to look for one: the pages an
insert reads are the ones it read before. An index that is not unique is not looked at this way without
`VerifyIndexes` - its entries under one key are its rows' - and an entry whose row has gone does no harm to this
library either way: each row found is read and compared.

`tests/JackcessDotNet.Tests/Assets/engine-3ad3aed-rows-in-access-tables.mdb` is a database Access made and an
earlier release then wrote to, made by `tools/legacy-incomplete-indexes.ps1`. `engine-3ad3aed-rows-in-unique-indexes.mdb`
beside it is the same for unique indexes and a primary key, made by `tools/legacy-unique-indexes.ps1`.
`ace-extended-date-forms.accdb` holds Date/Time Extended values in every form Access reads a date from, with
indexes ACE made over them (`tools/ace-extended-date-forms.ps1`); `engine-913240c-extended-date-keys.accdb` and
`engine-c89bfe4-extended-date-keys.accdb` are copies of it that this library changed at those commits
(`tools/earlier-extended-date-keys.ps1`).

Keys are matched as Access matches them - as its indexes hold them:

- **A null matches a null.** A parent whose key is null (a unique index allows one) has the rows whose foreign
  key is null for children: deleting it deletes them under cascade delete and is refused without it, and giving
  it a key gives them the key under cascade update. A parent that has a key never touches them, and a row can
  always be added, or changed, to have no foreign key. In a key of several columns, null matches null column by
  column.
- **Numbers are matched as stored.** A Double or Single by its bits: 0 and -0 are two keys, and so are a Double
  and the next Double after it. A Date/Time by the double the row stores, which a `DateTime` does not always hold.
- **A Date/Time Extended by the 42 bytes the row stores**, as Access matches it
  ([#51](https://github.com/markmario/jackcess-dotnet/issues/51)). Access writes a date in more than one form - its
  `INSERT` with the time of day in 100-nanosecond units, its SQL `UPDATE` and DAO's `AddNew` and `Edit` on a dynaset
  in whole seconds - and a child whose bytes are another form of its parent's date has no parent: Access refuses its
  insert, and a relationship created over it. A `DateTime` a caller gives is the form `INSERT` writes, which is the
  form this library writes.
- **A Yes/No holds no null.** A null given it is stored No and checked as No: refused when no parent's key is No.
  A cascade-to-null of a Yes/No foreign key leaves the row No, as Access leaves it, parent or no parent.

What enforcement does not check:

- **A key written again unchanged**: Access's `UPDATE` refuses to assign a key other rows refer to when the
  relationship does not cascade updates, even the value it already has; here an unchanged key is not a change.
- **Relationships without referential integrity**: they only tell Access's query designer how tables join.

Where no index on the columns can be searched (a table made without one on its foreign key, an index whose keys
this library does not write, an index that does not hold every row, a parent key that is null) the table is read
from end to end and held in memory until the change is written: some 200 bytes a row, about 60 MB and a second
for a table of 300,000 rows. A change that would rewrite a row of a table Access has deleted a column from is
refused (`NotSupportedException`): rows of such a table are not yet written correctly.

## Changing a table, and creating a relationship

A table that already has rows - one Access made or one this library made - can be given a column, an index or
a primary key, and two tables a relationship, as Jackcess's `TableUpdater` and `RelationshipBuilder` do
([qcatco/exportmdb.interop#37](https://github.com/qcatco/exportmdb.interop/issues/37)):

```csharp
using var db = Database.Open("Shop.mdb");
var orders = db.GetTable("Orders");

orders.AddColumn(new ColumnDefinition("Shipped", DataType.DateTime));
orders.AddIndex(new IndexDefinition("ByCustomerAndDate", "CustomerId", "Placed") { IsUnique = true });

db.CreateRelationship(new RelationshipDefinition("Customers", "Id", "Orders", "CustomerId")
{
    HasReferentialIntegrity = true,
    CascadeDeletes = true,
});

db.GetTable("Notes").AddIndex(new IndexDefinition("PrimaryKey", "Id") { IsPrimaryKey = true });

orders.RenameColumn("Placed", "PlacedOn");
orders.RenameIndex("ByCustomerAndDate", "ByCustomer");
orders.DropIndex("ByCustomer");
orders.DropColumn("Shipped");
db.DropRelationship("CustomersOrders");
db.RenameTable("Orders", "Sales");
db.DropTable("Notes");
```

Each change is all or nothing, and so is the creation of a table (`CreateTable`): what can refuse it is found before
anything is written, and what is written is taken back if writing it fails, so a refused change leaves the file byte
for byte as it was. Access is the referee
of what is written (ExportMdb.Interop's `SchemaChangesInAccessTests`): beside the same index made by ACE, or the
same relationship made by DAO, in a twin file, DAO lists the same thing and the two definitions hold the same
index slots.

- **A column** goes after the table's other columns. The rows already there are not rewritten: each reads the
  new column as null - a Yes/No column as No - as Access shows them. So an AutoNumber column is added only to a
  table with no rows (Access would number the rows there are), and a complex or calculated column is not added.
- **An index** is built over the rows already there and kept on every write after. It is refused for what an
  insert would be: two rows with one key in a unique index (rows with a null in it aside), a null in an index
  that requires its values, a column whose keys this library does not write (Decimal, Binary, OLE, Complex, text
  in another sort order), a value no key is made for. An index like one the table has shares its tree.
- **A primary key** is an index with `IsPrimaryKey` set, added to a table that has none: unique, and requiring
  its values, as Access writes one. It is refused when two rows have one key or a row has a null in it, and
  when the table has a primary key already.
- **A relationship** is a row of `MSysRelationships` for each pair of columns and an entry in the catalog. With
  referential integrity it also has the two indexes Access keeps for one: on the referenced table one named
  `.rB`, `.rC`... that shares the tree of the unique index on the referenced columns - there must be one - and
  on the referring table one with the relationship's name on the foreign key, which shares the tree of a plain
  index on exactly those columns or is built. It is refused when a row already there refers to no parent, when
  the matched columns differ in type, and when its name is a relationship's or an index's of the referring
  table. `EnforceForeignKeys` keeps it from then on.
- **Names.** An index or a relationship is refused a name Access does not allow: one with a period, an
  exclamation mark, a grave accent, a square bracket or a control character in it, or a space at its start. So
  nothing can take the name Access gives a referenced table's index (`.rB`, `.rC`...). A relationship given no
  name is named for its two tables; when their names make no valid name, one has to be given.
- **A name another object of its kind has is taken, without regard to case.** A table, or a column, index or
  relationship, is refused a name another of its kind has, the two compared as .NET compares them without regard
  to case (`StringComparison.OrdinalIgnoreCase`) - the comparison this library looks names up with, so that no
  two objects are taken for each other. `CreateTable` is refused two columns of one name so (`ArgumentException`,
  before anything is written): it made both, and a row then kept one value for the two. The comparison throws for
  no name: one already in a file - a noncharacter in it, say - stands in the way only of a new name equal to it.
  What is renamed is compared with every other object of its kind, and left out by what it is, not by its name; a
  table's rename changes the relationships that name it, and no other table's. A table asked for by a name one has
  exactly is that table, though another's is the same without regard to case.
  This is not yet Access's own rule. Measured with DAO, Access also takes as one names that differ in width, in
  kana, or in an accent written as one character or as two, which are two names here. And it keeps apart names
  .NET takes as one without regard to case - `xµ` and `xμ` (the micro sign and the Greek mu), and letters of
  other scripts that .NET's newer case tables pair - which are one name here on .NET, and two on .NET Framework.
- **A table related to itself** has both of the relationship's indexes: the foreign key's is numbered first and
  the referenced side's after it, and named by that number, as DAO writes them.
- **One to one.** A relationship whose columns are unique on both sides is marked one to one, as Access's
  Relationships window and Jackcess mark it, and its foreign key's index is then unique, as Access makes it
  (Jackcess gives it a plain one). `RelationshipDefinition.IsOneToOne` says otherwise: `true` marks a
  relationship whatever indexes there are, as DAO does when told to, and the foreign key's unique index is
  built over the rows - refused when two of them refer to one parent; `false` leaves it unmarked.
- **Where Access and Jackcess differ, Access is followed**: a name that is taken is refused, not changed; a
  cycle of cascading relationships is allowed; a table's index slots stay in the order of their names; a new
  index's block carries the counts Access puts there.
- **A table's definition goes on to as many pages as Access gives it**: a table with many columns or long names
  is created, read, written to and changed like any other, up to the 255 columns and 32 indexes a table has.
  Access puts a definition on the fewest pages that hold its length and eight bytes more, so one that ends in
  the last eight bytes of a page has a page after it with none of it. Jackcess writes such a definition on the
  page it fills, and Access then opens nothing of the file; here Access is followed.
- **Usage maps.** A new index's pages, and a new Memo or OLE column's, are listed in usage maps on a page of
  their own - 4 KB each - as for a table this library creates; Access finds room on the table's usage-map page
  first.
- **Permissions.** A new table or relationship has, in `MSysACEs`, the entries its container passes on, its
  owner's among them, as Access gives them: without the owner's, Access in an `.mdb` would not change what
  this library made.

A column, an index, a relationship or a table can be dropped, and a column, an index or a table renamed (#37).
Jackcess does neither, so what is dropped or renamed is written as Access (DAO) was measured to write it; Access
is the only referee, and what it refuses is refused:

- **Dropping an index** takes it out of the table's definition. Its tree's pages go back to the file, unless
  another index of the table shares the tree. A primary key is dropped by its index's name. An index a
  relationship keeps, and the unique index or primary key a relationship with referential integrity refers to
  a table's rows by, are not dropped while the relationship is there.
- **Dropping a column** takes it out of the definition, with the properties Access keeps for it, and gives a
  Memo or OLE column's long values back to the file. The rows are not rewritten - Access does not rewrite them
  either: each keeps the bytes it had for the column, which nothing reads, until the file is compacted. A column
  in an index or in a relationship, with or without referential integrity, is not dropped. Access drops a
  table's last column; this library refuses to, as it does a complex column. A dropped column keeps its number
  until the file is compacted, so a table that has had 255 columns takes no more, however few it has: Access
  refuses the next ("Too many fields defined") and so does `AddColumn`, until Access compacts the file.
- **Dropping a relationship** takes out its rows in `MSysRelationships`, its entry in the catalog and, with
  referential integrity, its index on each table.
- **Dropping a table** takes its entry out of the catalog and the relationships in which it refers to another
  table, or to itself, with it. A table another table refers to is not dropped. A `Table` got for it before
  throws `InvalidOperationException` from then on.
- **Renaming** a column changes its name in the definition, in its relationships and in its properties; an
  index's, in the definition, where its slot moves to where the new name sorts; a table's, in the catalog and
  in its relationships. A name Access does not allow, or that is taken, is refused; one that differs only in
  case is taken. A saved query goes on naming a column or table by the name it had, as it does when Access's
  engine renames one. A relationship's index keeps its name, and a system table its own.
- **Pages given back** are free in the database's own map of its pages, with their bytes left as they were, as
  Access leaves them: Access uses them again, and compacting the file removes them. This library adds pages
  at the end of the file and does not itself take a freed one.

## Properties, Required and Allow Zero Length

Access keeps a table's properties, its columns' and the database's own as named, typed values in the catalog.
They are read, changed and written, as Jackcess's `PropertyMap` is (#27):

```csharp
using var db = Database.Open("Shop.mdb");
var orders = db.GetTable("Orders");

orders.Properties.Set("Description", "One row an order");
orders.Columns.First(c => c.Name == "Placed").Properties.Set("Caption", "Placed on");
orders.Properties.Save();                         // the table's and its columns', together

db.DatabaseProperties.Set("AppTitle", "The shop");
db.DatabaseProperties.Save();
string? title = (string?)db.SummaryProperties["Title"];
```

- **`Set` and `Remove` change the map; `Save` writes it.** A table's own properties and its columns' are one
  value in the file, so saving any of those maps saves them all. A save is written whole or not at all, and a
  save with nothing changed writes nothing. Until a change is saved it is the map's alone: `Column.IsRequired`,
  `Column.AllowZeroLength` and what rows are refused for follow the file, as Access would see it.
- **A save rewrites the object's row in the catalog** where the row is, as any row this library updates: the
  properties themselves, a long value, are written to pages of their own again, and the room the old ones had
  is noted for later long values to use.
- **Names and values are Access's.** A property's name is 1 to 64 characters, with no period, exclamation
  mark, grave accent, square bracket or control character and no space at its start; a value is not text or
  bytes of no length, which Access takes for no value. `Set` refuses the others, as DAO refuses them.
- **What Access wrote goes back as it was.** A property that was read and not changed is written with the bytes
  and flags it had, and the names the file lists keep their places: a value saved with nothing changed is the
  same bytes. (Jackcess lists the names again and encodes every value again.)
- **Types.** A property that is there keeps its type. A new one takes the type Access keeps that name as
  (`DefaultValue` a Memo, `Required` a Yes/No, `Format` Text, and so on) or else the type of the value; an
  overload names the type. Access keeps no property as a Large Number or a Decimal - DAO refuses to make one,
  and reads a Large Number property as 0 - so neither is written.
- **The database's three sets**: `DatabaseProperties` (what DAO's `Database.Properties` lists beyond its
  built-in ones), `SummaryProperties` and `UserDefinedProperties` (the Summary and Custom sheets). The last
  two are kept with documents that Access's own interface adds to a database. A database made by DAO alone,
  or by this library as an `.accdb`, has neither, and saving to one is refused: the library does not add the
  document. Its new `.mdb` has both.

**Required and Allow Zero Length are properties too** (#41): nothing of them is in a table's definition.
`Column.IsRequired` and `Column.AllowZeroLength` read them, `CreateTable` and `AddColumn` write them from the
`ColumnDefinition` - as DAO writes them for a field it makes - and rows are held to them as Access holds them,
with `ConstraintViolationException`:

- a row with no value in a required column is refused. A Yes/No always has a value, and an AutoNumber its number;
- a zero-length string is refused in a Text or Memo column whose `AllowZeroLength` property is there and false.
  A column with no such property takes one: that is what Access does with a column made by SQL's `CREATE TABLE`,
  though DAO reports the property as false. Spaces are not a zero-length string;
- an insert is checked in every column, an update only in the columns it gives a value: a row that already
  breaks a rule in another column is still updated, as Access updates it;
- a cascade is held to them too: an update or delete of a parent whose cascade would give a child's column a
  null it requires a value in place of, or a zero-length string it does not take, is refused whole, parent and
  all, as Access refuses it ("Cannot perform cascading operation");
- a required column can be added to a table that has rows, as in Access: the rows have no value in it, are
  still updated in other columns, and a new row needs one;
- a table made by an earlier version of this library has no properties, so nothing new is refused in it.

Where a table's properties cannot be written - a database whose catalog sorts in an order other than General
and General - Legacy, or a table whose properties were not all read - `AddColumn` still adds a column that is
what was asked for with no property at all (not required, and for Text and Memo allowing a zero-length string:
the default definition), and refuses, before anything is written, one that is required, does not allow a
zero-length string or is a Replication ID AutoNumber. `CreateTable` is refused in such a database already,
whatever its columns: the table's row cannot be added to the catalog.

`ColumnDefinition.AllowZeroLength` is `true` unless set, as for a field made in Access's table designer; a field
made with DAO does not allow a zero-length string unless asked. A column's default value is not filled in: a
required column with a `DefaultValue` and no value in the row is refused here, where Access would use the
default.

**A Replication ID AutoNumber** is given the `DefaultValue` property `GenGUID()` (#33): Access fills the column
of a row it adds from that, not from the column's flag. A Replication ID column Access made with that default
and without the flag (DAO and SQL make them so) is not an AutoNumber to this library, which fills in no default.

## Importing from `DataTable`, `DataSet`, or `IEnumerable<T>`

Skip the schema boilerplate — the importer infers columns from the source type:

```csharp
using var db = Database.Create("out.mdb");
db.ImportTable(myDataTable);

// a table under another name, with a primary key of its own
db.ImportTable(otherDataTable, new ImportOptions { TableName = "Extra", PrimaryKey = new[] { "Id" } });
db.ImportTables(myDataSet);

db.ImportTable(customers, new ImportOptions { PrimaryKey = new[] { "Id" } });

// Append rows into a pre-existing table (instead of failing):
db.ImportTable(moreRows, new ImportOptions { AppendIfExists = true });
```

An import holds every value of its source exactly, or refuses before anything is written (#44):

- **Every value is converted to what its column holds before anything is written**, and a value no column holds
  exactly is refused then: `ArgumentException`, naming the column and the row (`ColumnValueTooLongException` for
  text longer than a column that is there).
- **What is written is written whole or not at all**, in one undo scope: a row the database refuses part-way - a
  duplicate in a key, a null in a required column - takes back the table, the rows before it and, with
  `ImportTables`, every table before it. A refused import leaves the file byte for byte as it was.
- **A column a table is made with holds every value of its source** (the cheat sheet below): its type follows the
  CLR type, and a length, precision or scale the values need follows the values.
- **Nullability and keys are carried over.** A `DataColumn` with `AllowDBNull = false`, and a property with
  `[Required]` or of a value type that is not nullable, makes a required column - Access's Required property, which
  Access and this library hold rows to; an AutoNumber and a Yes/No are not given it. A `DataTable`'s primary key of
  several columns is the table's key, in its order; several `[Key]` properties make a key of several columns, in the
  order of their `[Column(Order = n)]` where they give one, and then as they are declared. A key on strings that run
  past 255 characters is on a Memo, as Access keeps one: its keys are a value's first 255 characters, so two values
  equal in those are a duplicate, as Access takes them (measured with DAO).
- **Adding to a table that is there** (`AppendIfExists`) converts a value to its column's type only when the column
  holds it exactly: a number to a numeric column whose range, places and digits hold it (a `double` to a Decimal
  column as the number it is when the column holds that, and otherwise as the number its round-trip form denotes;
  a `double` or `float` to a Currency column as the Currency Access makes of it - its exact value to four places,
  half to even - when that reads back as the number given, which it does when the number's round-trip form has
  four places or fewer; a `decimal` or an integer to a Double column when the double nearest it is that number, or has
  it as its round-trip form); text to a numeric column as the number it denotes in the invariant culture, held as
  a number would be - exactly: `"1e-30"` goes to a Double column and is refused by a Currency one, and NaN, the
  infinities and a number past the column's range are refused; text to a Yes/No,
  Date/Time or GUID column when it reads as one in the invariant culture (text with an offset to a Date/Time
  column is the UTC time it denotes, and text without one the time it says, whatever the machine's time zone; a
  time without a date, `"10:30"`, is on 30 December 1899, as Access gives one);
  any of these to Text or Memo as its
  invariant text (a `double` or `float` in its round-trip form - the fewest digits that read back as it, written
  as .NET writes them, the same text on every target - and a date in ISO 8601) when that fits; bytes to a Binary column
  that holds that many. A Yes/No column takes no null, which Access would read as No, and no number.
- **An empty byte array is written as `Table.Insert` writes one**, for a table made and for one that is there: a
  value of no bytes, as ACE's own INSERT writes it, which ACE reads back as one and DAO shows as Null (see
  [Rows and long values](#rows-and-long-values-what-to-know)).
- **Two columns whose names are one without regard to case are refused** - `A` and `a` of a case-sensitive
  `DataTable`, a property and a `[Column("a")]` - for a table made, which would have one column of the name, and
  for one that is there, whose column would keep one of their values.
- An `IEnumerable<T>` is read whole, every object, before anything is written.

POCO mapping respects `[Key]`, `[Column("X")]`, `[Column(Order = n)]`, `[MaxLength]`, `[Required]`, `[NotMapped]`
and `[DatabaseGenerated(Identity)]`. Unmappable types can fall back to `Memo` via
`ImportOptions.FallbackUnmappableToString = true`.

## Exporting back

```csharp
DataTable          dt    = db.GetTable("Customers").ToDataTable();
DataSet            ds    = db.ToDataSet();
IEnumerable<Customer> cs = db.GetTable("Customers").ReadRows<Customer>();
```

The `IEnumerable<T>` path is lazy via `yield`, so `Take(n)` stops early without
materialising the whole table.

## Type round-trip cheat sheet

How the importer maps a CLR type to a column of a table it makes, and what comes back. Each column holds every
value of its type, or of the values the source has (#44):

| CLR type | `DataType` | Notes |
| --- | --- | --- |
| `bool` | `Boolean` (Yes/No) | a Yes/No holds no null: a null is refused |
| `byte` | `Byte` | |
| `sbyte` / `short` | `Int16` (Integer) | both come back as `short`: Access's Byte is 0 to 255 |
| `ushort` / `int` | `Int32` (Long Integer) | both come back as `int`: Access's Integer is signed |
| `uint` | `Int64` (Large Number) in a file of Access 2016's format or later, `Decimal(10, 0)` in an older one | comes back as `long`, or `decimal` |
| `long` | `Int64` (Large Number) in a file of Access 2016's format or later, `Decimal(19, 0)` in an older one | comes back as `long`, or `decimal` |
| `ulong` | `Decimal(20, 0)` | past a Large Number's range; comes back as `decimal` |
| `float` / `double` | `Single` / `Double` | every bit, NaN and the infinities included |
| `decimal` | `Decimal(p, s)` | `s` the most places after the point a value has, `p` the digits the values need and 18 at least; refused when they need more than 28 |
| `DateTime` | `DateTime` | to the millisecond, the precision a Date/Time is read and written at; `Kind` is dropped; a date before 1 January 100 (`DateTime.MinValue` among them) is refused |
| `DateTimeOffset` | `DateTime` | stored as UTC |
| `Guid` | `Guid` | |
| `string` / `char` | `Text` | the source's maximum length, or 255 when it has none, when every value fits it |
| `string` that does not fit | `Memo` | |
| `byte[]` | `Binary` | the source's maximum length, when it has one of 255 or less and every value fits it |
| `byte[]` otherwise | `OleObject` | a `byte[]` without a length - a `DataTable`'s, which `DataColumn.MaxLength` gives none - as Java's importer makes a BLOB: it holds any value, now and later |
| `Nullable<T>` | as `T` | |
| `enum` | its underlying type | comes back as the enum on read |

A `decimal` key, a `ulong` key, and a `long` or `uint` key in a file before Access 2016's format, are refused
before the table is made, with `NotSupportedException`: they are Decimal columns, whose index keys this library
does not write. A `long` or `uint` key is a Large Number in a file created as `FileFormat.Access2016` or later.

**An import does not change a file's format.** A Large Number column makes a file of an older format one of
Access 2016's, which Access 2010 and 2013 do not open; the importer makes one only where the file is of that
format already. Java Jackcess likewise makes a Large Number only in a format that has it (its `BIGINT` is a Long
Integer otherwise, which does not hold every value).

## Status

| Feature                                           | Status |
| ------------------------------------------------- | ------ |
| Read Jet 3 (Access 97) / Jet 4 (Access 2000–2003) | ✅      |
| Read ACE 12 / 14 / 16 / 17 (`.accdb`)             | ✅      |
| Create new `.mdb` files (Jet 4)                   | ✅      |
| Create new `.accdb` files (Access 2007, 2010, 2016, 2019) | ✅ `FileFormat.Access2007`, `Access2010`, `Access2016`, `Access2019` |
| Large Number (`DataType.Int64`) and Date/Time Extended (`DataType.DateTimeExtended`) columns | ✅ Read, written, created and indexed; `.accdb` only |
| Create or write Jet 3 (Access 97) files           | ❌ Read-only, as in Jackcess |
| Row CRUD + primary-key B-tree index               | ✅      |
| Update or delete a row by where it was read (`Table.Update`, `Table.Delete`), whatever key the table has | ✅ In its place, as Access updates one: a row's id does not change |
| Batch insert, all rows or none (`Table.InsertRange`) | ✅ |
| Cursors: both ways, find first and next, update or delete the current row, one column, some columns, savepoints | ✅ No column matchers |
| Index cursors: the index's order, ranges between two entries, closest row, rows of an entry; `Joiner` | ✅ For every type an index is kept for |
| Secondary indexes: created with a table, kept on every write, unique enforced | ✅ Byte, Integer, Long, Large Number, Currency, Single, Double, Date/Time, Date/Time Extended, Yes/No, Text and Memo (General - Legacy and General), GUID |
| Memo / OLE long values                            | ✅      |
| PropertyMap & MSysRelationships                   | ✅      |
| Properties written: a table's, a column's, the database's own, Summary and Custom | ✅ Summary and Custom only where the database has their documents |
| A column's Required and Allow Zero Length: read, written, enforced | ✅ |
| Password-protected `.mdb`: a database password, checked as Access checks it | ✅ Read + write (Access 97: read) |
| Encoded `.mdb` (Access's Encrypt Database, Jet RC4) | ✅ Read + write (Access 97: read), no password needed |
| Password-protected `.accdb` (Agile Encryption, Access 2010+) | ✅ Read + write |
| Password-protected `.accdb` (RC4 CryptoAPI, Access 2007) | ✅ Read + write |
| Password-protected `.accdb` (Non-Standard AES, compat mode 0) | ✅ Read + write |
| Password-protected `.accdb` (ECMA Standard Encryption) | ✅ Read, unverified against a file Access made (none found); ❌ write |
| Password-protected `.accdb` (Extensible Encryption) | ❌ External CSP — non-portable |
| Multi-column primary keys                         | ✅      |
| Foreign-key enforcement, with cascades (on by default) | ✅ |
| Add a column, an index or a primary key to a table that has rows | ✅ No AutoNumber column for a table with rows |
| Table definitions of more than one page | ✅ Read, written and changed |
| Create a relationship, with referential integrity and cascades | ✅ |
| Drop a column, index, table or relationship; rename a column, index or table | ✅ As Access writes each; not a table's last column, nor a complex column |
| Queries (`MSysQueries`)                           | ❌ Not yet |

## What this fork changes

Each of these was found by having Access or ACE open a file the engine wrote.

- **Four target frameworks:** `net10.0`, `net11.0`, `net48` and `netstandard2.0`, each of them tested.
- **Files up to the format's own limit.** A table's usage map grows past its first window of pages, as
  Access's does, up to the 2 GB a Jet file can be. Before, a file could not be written past about 2 MB.
- **Rows go where Access puts them, and finding room costs no more as a table grows.** A row goes to the
  lowest page the table's free-space map lists with room for it, as in Access - in a table only inserted into, as
  an export is, its last page, so rows stay in insertion order - and room a delete or an update frees is used
  again as Access uses it (#58): after a delete, or an update that moves a row's values to another page and back,
  a row can go to an earlier page. A listed page tried without room for the row leaves the map, so a run of
  inserts tries at most as many pages as it has inserts and the map lists pages, at any size of table; one insert
  can try every listed page, when none has room for it, as Access's can (below). A page holds at most 255 rows.
- **A memo or OLE value is kept in its row only up to 64 bytes**, Access's own limit. The engine kept up
  to 1,024 bytes there, and Access showed `#Error` for those values.
- **Text index keys are the ones Access writes**, for the General - Legacy sort order (Access 2000-2007),
  for the orders of nine other locales that have its keys, and for General (Access 2010 and later). An
  Access 97 file's text indexes are searched, in Access 97's General order.
- **Every index of a table is kept on every write**, not the primary key alone, and unique indexes are
  enforced. Access answers through them after the engine adds, changes or deletes rows.
- **An insert fills AutoNumber columns from the table's own counter**, as Access does.
- **Foreign keys are enforced** on insert, update and delete, with cascades (`EnforceForeignKeys`,
  on by default).
- **Rows and long values are laid out as Access lays them out**, so Access reads back every value the
  engine writes, a long memo in several pieces included.
- **A Currency is the number Access makes of a caller's**: its exact value to four places, half to even, for a
  row, its keys and every lookup, on every runtime. The engine rounded half away from zero, after taking a `double`
  through a `decimal` of 15 significant digits and a `float` through one of 7 on .NET Framework and .NET up to 10,
  so a row was not found by the number it was written with. A file it wrote keeps those values: see
  [Rows and long values](#rows-and-long-values-what-to-know).
- **Tables the engine creates are ones Access lists, reads, seeks in and adds to.**
- **Properties are written**, a table's, a column's and the database's, with what Access wrote left byte for
  byte as it was; **a column's Required and Allow Zero Length are read, written and held to**, as Access holds
  rows to them; and **Access generates the GUID** of a Replication ID AutoNumber the engine made. See
  [Properties](#properties-required-and-allow-zero-length).
- **`.accdb` files are created**, in the Access 2007, 2010, 2016 and 2019 formats, where every version asked
  for made an Access 2000 `.mdb`. The two empty `.accdb` databases a new file is copied from were made by
  DAO's `CreateDatabase` (`tools/empty-databases.ps1`), not taken from Jackcess; `empty2003.mdb` is
  Jackcess's.
- **Large Number and Date/Time Extended columns** (Access 2016 and 2019) are read, written, created and
  indexed. A Large Number is a `long`. A Date/Time Extended is a `DateTime`: Access keeps the years 1 to
  9999 to 100 nanoseconds, which is a `DateTime`'s own range and tick, so the value comes back to the tick;
  it has no time zone, and is read with `DateTimeKind.Unspecified`. Its index key is made of the 42 bytes the row
  stores, in whichever form Access wrote them, as Access makes it. A value DAO stored through a table-type
  recordset (`dbOpenTable`), whose `AddNew` and `Edit` write a layout of their own, is read as null
  ([#64](https://github.com/markmario/jackcess-dotnet/issues/64)).
- **Pages the engine adds are taken out of the database's free-page map**, so Access does not hand them
  out again and write over them.
- **Protected files open as Access opens them**: an .mdb's database password is checked, an encoded .mdb is
  read and written with the key its header holds, and every kind of protection Access makes is tested against a
  file it made. See [Opening password-protected files](#opening-password-protected-files).
- **The test corpus is in the repository**, pinned by checksum. See [tests/corpus](tests/corpus/README.md).

Limits to know about:

- Text keys are written in the General - Legacy and General orders, and in the orders of the locales whose
  keys are General - Legacy's. The engine refuses, before changing anything, to write to a table with an
  index whose keys it cannot write: text in any other sort order, or a column type it makes no key for. It
  still reads such a table. See [Text index keys](#text-index-keys-sort-orders-and-limits).
- A table can be asked first: `Table.InsertRefusal` is why it takes no row - the message `Insert` would
  throw - or null when it takes rows, and asking changes nothing. A caller that writes to several tables
  asks each before it writes to any.
- Access 97 (Jet 3) files are read and not written, as in Jackcess: one is opened for reading only, and
  creating one, or changing anything in one, is refused with `NotSupportedException`.
- One writer at a time. This library takes no lock on a file, as Access does with its lock file: `Database.Open`
  refuses, with `IOException`, a file another process - Access holding it - or another `Database` has open for
  writing, and leaves it as it was. A file this library has open is not kept from Access, which should not be
  given it until the `Database` is disposed.
- A Large Number or Date/Time Extended column is for an `.accdb`. A table with one is refused in an `.mdb`
  before anything is written, as Access refuses it. In an `.accdb` of an earlier format it is created, and
  the file becomes one of Access 2016's format (a Large Number) or Access 2019's (a Date/Time Extended), as
  it does when Access adds the table: an Access older than that then refuses to open the file. Jackcess
  refuses the table instead.
- `Database.Create` with `Jet16` or `Jet17` makes the Access 2010 file with the later version in its header,
  which is all Access itself changes; Access's compact puts the version back to 2010's while no table has
  such a column.
- An import (`DatabaseImporter`) does not change a file's format: a `long` or `uint` column or property is a
  Large Number in a file of Access 2016's format or later - one created as `FileFormat.Access2016`, or given such
  a column before - and a `Decimal(19, 0)` or `Decimal(10, 0)` in an older one, an `.mdb` included.

## Text index keys: sort orders and limits

An index entry's key for text depends on the column's sort order: a code, which
is a locale's LCID, and in an Access 2000 or later file a version. The library
makes the keys of the orders below, each checked byte for byte against the keys
Microsoft's own engine writes.

Written, and searched by:

- **General - Legacy** (sort order 1033, version 0): Access 2000 to 2007, and
  what a new database gets by default in later versions.
- **General** (1033, version 1): what Access 2010 and later can make a new
  database in. A table created in such a file sorts as the file does.
- **The orders of nine other locales, version 0**, whose keys are those of
  General - Legacy: Arabic (1025), German (1031), Greek (1032), Hebrew (1037),
  Italian (1040), Dutch (1043), Russian (1049, DAO's `dbLangCyrillic`), British
  English (2057) and Portuguese (2070). A database made in such a locale - by
  DAO's `CreateDatabase` with that locale, or by Access with it as the "New
  database sort order" - gives every Text and Memo column the locale's LCID in
  place of 1033. ACE 16 was had to index 1,013,302 values in a database of each
  (`tools/ace-text-keys.ps1 -Locale`): every character of the BMP alone and
  between two letters, every pair of letters of Latin-1 and Latin Extended-A and
  of characters within the Greek, Cyrillic, Hebrew, Arabic and Thai blocks,
  letters with each combining mark, runs of ASCII, 500,000 values of two to
  eight characters, and the values of the General - Legacy golden files. Every
  key is the one ACE writes for the value in a database of locale 1033. A table
  created in such a file sorts as the file does.

Searched by, in a file that is only read:

- **Access 97's General order** (1033 in an Access 97 file). An Access 97 file
  is not written to at all (see
  [Access 97 files are read-only](#access-97-files-are-read-only)); a row is
  found through a text index of one instead of by reading its table. The codes
  are Jackcess's `General97IndexCodes`, with what ACE writes otherwise: a
  character keyed as two letters (Œ, Æ, ß) takes a place for each among the
  accents. Access 97 keeps text in code page 1252, and a value with a character
  that code page has not, or whose key is longer than the 255 bytes an entry
  there holds, is looked for by reading the table. The keys are those the 32-bit
  ACE 12.0 and Jet 4.0 write in an Access 97 file (ACE 16 opens none): every
  character of the code page alone and in every pair, and 40,000 longer values,
  were compared, and every text key in the nine files Access 97 itself wrote, in
  the corpus, is the key made of its row's values.

Refused, rather than write a key Access cannot find:

- **Any other sort order.** ACE's keys were measured to differ from General -
  Legacy's for Chinese (1028, 2052), Czech, Danish and Norwegian, French,
  Hungarian, Icelandic, Japanese, Korean, Lithuanian, Polish, Romanian, Slovak,
  Slovenian, Croatian, Spanish (1034, 3082), Swedish and Finnish, Thai, Turkish,
  Ukrainian and Vietnamese - a few letters in most, and in Thai and Ukrainian
  only among other characters, which is why a locale that was not measured is
  refused too. The version 1 order of a locale other than 1033, which Access
  2010 and later can make a database in and DAO cannot, was not measured, nor
  were Access 97's other orders. A file whose catalog sorts in such an order is
  not given a table, and a table with an index on a Text or Memo column that
  sorts in one is not inserted into, updated or deleted from. Such a file is
  still read; its text indexes are not searched, its tables are scanned. Jackcess
  writes none of them either.
- **A value in an indexed Text or Memo column, or a table name, with a
  character Access keys by the character before it.** These are the Japanese length and iteration marks (ー U+30FC,
  々 U+3005, ゝ U+309D, ゞ U+309E, ヽ U+30FD, ヾ U+30FE, 〱 U+3031, 〲 U+3032,
  ｰ U+FF70; in General also U+3033-3035, 〻 U+303B, ゠ U+30A0 and ꀕ U+A015) and,
  in General - Legacy, the Arabic shadda (U+0651, U+FE7C, U+FE7D). So a Japanese
  name with ー (データ, コンピューター) or an Arabic word with a shadda cannot be a
  value of an indexed column (the primary key or any other index) or a table name: `Insert`, `Update`,
  `Delete` and `CreateTable` throw `NotSupportedException` before anything
  is written. In General the same goes for four Arabic ligatures (U+06D6,
  U+06D7, U+FD24, U+FD6F). Any text is fine in a column that is no key, and a
  row Access wrote with such a key is still found: the index's rows are gone through for it.

### Keys longer than 510 bytes

Access keeps no index key longer than 510 bytes. It cuts a longer one there
and ends it in a two-byte digest of every byte it cut off (their remainder by
the CRC-16 polynomial 0x8005) - a key of one column, of a Memo (its first 255
characters) or of several columns together. A file can hold four kinds of
entry for such a key:

| Written by | Stored as | Read / lookup | Insert, update | Delete |
| --- | --- | --- | --- | --- |
| Access (Jet 4.0, ACE 12, ACE 16 alike) | cut, with the digest | found | a duplicate in a unique index is refused | entry removed |
| This library | the same bytes as Access | found | the same | entry removed |
| This library from when it cut keys until #56 (never released), and the versions of Jackcess that cut keys | cut, with a digest that leaves the last byte out: Access's for a key that ends in text, **another** for a key of several columns that ends in a number, a date or the like after text that filled the entry - Access's key of the same values with the last byte 0 | found | seen by a unique index when the row it points at has the value; a rewritten row's entry is removed and Access's written | entry removed, under the key it has |
| This library before that, and Jackcess before 5.2.0 | **whole**, over 510 bytes | found | seen by a unique index, so a duplicate is refused; a rewritten row's whole entry is removed and a cut one written | entry removed, under the whole key |

- **Lookups** of a value whose key is cut do not search the index: an index
  cursor goes through the index's rows, comparing each, and any other lookup
  reads the table, so a row is found whichever way its entry is stored. A
  range, or the closest row to such a value, is refused.
- **Writes** always store Access's cut key. Updating a row whose entry was
  whole, or cut with the earlier digest, replaces it with Access's; nothing
  converts rows that are not written, except that with
  `Database.VerifyIndexes` an index that is looked at (a unique one, or a
  relationship's) of Text and Long columns has entries cut with the earlier
  digest replaced when its table is next changed.
- **Uniqueness** follows Access: two values cut to the same entry are
  duplicates, whichever way the existing one is stored. A file an earlier
  release wrote may hold two such rows (their whole keys differed); neither
  can then be rewritten until the other is deleted. A key cut with the earlier
  digest is Access's key of another value - with the same text, (A, 1)'s and
  (A, 255)'s are (A, 0)'s - so an entry found under it alone is a duplicate only
  when the row it points at has the new row's value: the row is read.
- **Deletes and updates** remove the entry the row actually has, cut either
  way or whole, matched by its row. A row with no entry at all - earlier
  releases kept only the primary key - has nothing to remove, and that is not
  an error.
- Access itself does not seek a whole key: until such a row is rewritten,
  Access finds it by a scan but not through the index. Nor does it find, update
  or delete a row whose entry is cut with the earlier digest ("The search key
  was not found in any record") until the row is rewritten here or the file is
  compacted in Access.
- ACE's SQL does not find a row by `=` through an index whose key for the value
  is cut - a row Access wrote itself no more than one this library wrote - where
  DAO's `Seek` through the same index does.

`tests/JackcessDotNet.Tests/Assets/engine-696ca52-long-keys.mdb` is a file an
earlier release wrote, made by `tools/legacy-long-keys.ps1`, and
`engine-913240c-cut-keys.mdb` one this library wrote with the earlier digest,
made by `tools/earlier-cut-keys.ps1`. `IndexKeys/cut-keys-*.tsv` hold the
entries ACE wrote for keys of several columns, made by `tools/ace-cut-keys.ps1`.

## Rows and long values: what to know

Rows are read and written by the table's own definition - where it keeps each column, including after columns
have been deleted - and a row Access moved to another page is read where its pointer leads.

- **Decimal values written by releases before this read back as different numbers.** A Decimal is stored as
  four 32-bit words; earlier releases wrote and read sixteen bytes in the other order, so they and Access never
  agreed (a 1 written by an earlier release is 16,777,216 to Access, and now to this library too). Such a value
  cannot be told from one Access wrote; one that reads with more digits than its column's precision is almost
  certainly one. Access-written Decimals, which earlier releases misread, now read correctly.

  `DecimalByteOrder.Find(database)`, and the same for a `Table`, in the `JackcessDotNet.Diagnostics` namespace,
  lists the values that read with more digits
  than their column holds: the table, the column, the row (its primary key, and its place among the table's rows),
  the value as it reads and the value its bytes make in the earlier order
  ([qcatco/exportmdb.interop#66](https://github.com/qcatco/exportmdb.interop/issues/66)). It changes nothing - a
  read returns what it did - and repairs nothing:

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

  A stored number that runs past 96 bits - the first of the four words set, which Access never writes - reads as
  the number its low 96 bits make, with its sign, as Access reads it through its OLE DB provider and through DAO
  ([qcatco/exportmdb.interop#74](https://github.com/qcatco/exportmdb.interop/issues/74)); it read as null before.
  2^96 + 12345 in a `DECIMAL(5,2)` column is 123.45. Such a value is always listed, with `IsCut` set, whatever
  its low 96 bits make. Java Jackcess returns all 128 bits as a `BigDecimal`, which a `decimal` cannot hold.

  Neither Access nor Java Jackcess checks a Decimal against its column's precision when it reads one. Jackcess
  returns the number. In Access, DAO returns it and SQL computes with it (the 1 above is found by
  `WHERE Price = 16777216`), but the OLE DB provider cannot hand it over - reading the column fails with
  "Conversion failed because the Decimal data value overflowed the type specified" - and a query that sorts its
  rows, or writes the value back, is refused: "The decimal field's precision is too small to accept the numeric
  you attempted to add."
- **A Currency is the caller's number taken to four places as Access takes one** - its exact value, half to even -
  wherever one is made: a row, its index keys, a relationship's check, a property, an import
  ([#65](https://github.com/markmario/jackcess-dotnet/issues/65)). Measured with ACE 16 and DAO, which write alike
  through every path tried: the double 0.03125 is 0.0312, the double 0.00015 - just under the half it is written
  as - is 0.0001, 562949953421312.5 is itself, and the decimal 1.23465 is 1.2346. Text and a `bool` are taken as
  every number column takes them - text read in the current culture, a `bool` as 1 or 0 - and the number they make
  is taken to four places so; Access reads text by the machine's locale and writes True as -1. A date, a `Guid`,
  bytes or a `char` is refused with `ArgumentException`, and text that reads as no number with `FormatException`.
  NaN, an infinity and a number past -922,337,203,685,477.5808 to 922,337,203,685,477.5807 are refused with
  `OverflowException`; Access refuses the last two ("Overflow"), and writes NaN as the least Currency, which this
  library does not.

  **A file an earlier version wrote holds that version's rounding.** 1.2.2, and this library until #65, rounded a
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
- **Fixed-length text (SQL `CHAR(n)`) is returned filled to its length with spaces**, as Access returns it, and
  is written that way.
- **An empty byte array is a value of no bytes** in a Binary or OLE Object column: written as ACE's OLE DB writes
  one, and read back as `byte[0]`, as ACE's OLE DB and SQL read it (`IS NULL` is false, `LenB` is 0). DAO - and so
  Access's own forms - reads it as Null, and DAO writes an empty array as Null. An import writes one as an insert
  does.
- **A calculated column reads as null**, and a table with one is not written to: an insert, an update and a
  delete are each refused with `NotSupportedException`.
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
- **An update leaves the stored bytes of every column it does not name.** A Date/Time Access wrote finer than a
  millisecond, or outside what `DateTime` holds, is not rounded or lost by an update of another column. Each of
  those values is still held to its column's length, as the row is written whole: a Text or Binary value stored
  longer than its column - an earlier version of this library could write one - refuses an update of the row with
  `ColumnValueTooLongException`, naming that column and saying the value is the stored one, until the update
  gives that column a value that fits.
- **An overflow pointer that leads outside the file, or to a row its page does not have, is a damaged file**
  (`InvalidDataException` naming the page), not a missing row.
- **A deleted row is marked as Access marks one** (both flag bits). Rows deleted by earlier releases carry the
  first bit alone; they stay deleted for this library and for Access, which reads, edits and compacts such a
  table as any other.
- **Where a row goes, and the room a delete or an update leaves, are Access's** (#58), as measured with DAO 16 and
  ACE 16 in `.mdb` and `.accdb` files, on tables Access made and tables this library made, in one session or many.
  ExportMdb.Interop's suite has Access place the same rows and compares every page's room and slots, and the map:
  - A new row - and the values of a row an update makes too long for its page - goes to the lowest page the
    table's free-space map lists (the second usage map of its definition). A listed page without room for the row
    and its slot, or with 255 rows, leaves the map and the next is tried: a page leaves the map so, never because a
    row filled it. With none, the row goes to a new page, which the map lists only when it has room, the row on it,
    for another row of the same size: a page a row of more than half a page began is not listed, so the next row,
    however small, goes to another page - a new one when no other is listed - in Access as here.
  - A delete takes the row's bytes off its page - the rows after them close up - and leaves its slot, marked
    deleted and empty; a new row takes a slot of its own, not a deleted row's. The page is listed when, before the
    delete, it had no room for the row it lost and a slot, whatever room the delete leaves it (to the byte, for rows
    of 101 to 401 bytes). A page a delete leaves with no row stays the table's.
  - An update that fits where the row is lists nothing. A row that comes home lists the page its values leave by the
    same rule as a delete; values that move on to another page do not list the page they leave.
  - Access gives back to the file a page an update leaves with no row - out of the table's maps - and takes it again
    for the next page it needs, its slots begun again from the first: a row updated back and forth past its page's
    room adds a page to Access's file at most when each change is a session of its own (one in 3,000 such cycles,
    measured). In one session Access did not take such pages back: its file grew by about three pages in four cycles
    (26 to 774 pages in 1,000), its table keeping its 4. This library adds every page at the end of the file and takes
    none back from it, so it keeps such a page for the table, begun again and listed: the next row, or values moved,
    go to it. Every slot on such a page was a deleted one - values moved there and gone again, or a row deleted with
    its index entries. A row deleted in the session is still named by what read it, a `Row`, a cursor or a savepoint,
    so until the session ends the page keeps that row's slot, deleted, with the slots before it, and is begun again
    after them: a slot number is used again only where nothing names it, and what read a deleted row finds no row in
    its slot. So the file grows by a page at most from one session to the next, and in one session while no row is
    deleted from that page; rows inserted and deleted on it in one session fill it with kept slots, up to 255, and the
    table then takes a new page. Over 3,000 such cycles in one session, each also inserting and deleting a small row
    on that page, this library's file went from 64 to 76 pages, and Access's from 64 to 8,931, its table keeping its 4
    pages (from 64 to 2,804 without the small row).
  - Finding a page reads the pages tried and no other: the map is read once a session, and kept. A page tried
    without room for the row leaves the map, as in Access, so each listed page is tried once while it is listed: a
    run of inserts tries at most as many pages as it has inserts and the map lists pages, a few pages read for each,
    however large the table. One insert tries every listed page when none has room for it - a row larger than the
    room any delete left - and takes each off the map, about three pages read and one written for each (2,000 pages
    listed, a row of 1,039 bytes: 3,012 read, 1,008 written), so that the inserts after it try none of them again.
  - Where Access does not use room, this library does not either: a page with 255 slots takes no more rows, however
    many of them are deleted ones; a row that shrinks where it is, or whose values move to another page, lists no
    page. Compacting the file in Access gives all of it back.
- **Long values are placed as Access places them**: every chunk of a value too long for one page (4,076 bytes)
  has a page to itself. Access gives every page of such a value back to the file when the value is deleted or
  replaced, so a page shared with another value loses that value. `Database.ShareLongValuePages = true` keeps the
  placement of earlier releases - the last chunk on a page with other values' - which is smaller (a fifth
  smaller for 5,000-character Memos, nearly half for values just over one page) and reads the same, but is not
  safe for Access to edit long values in. A file written that way keeps its shared pages when it is added to.
  Added to in the default placement, such a file gets no value on a page that may hold the end of a chain.
- **A value of one chunk goes to the page with the least room that will take it**, found by its room: an insert
  looks at no page that cannot take the value, however many the column has. A page is listed in the column's
  free-space map while it has 258 bytes or more free, which is where ACE lists one (it lists none with 257 or
  fewer), and only once a value is on it. The first long value written to a column in a session, of one chunk or
  a chain, reads the pages its map lists, once: a chain's chunks go to pages emptied in an earlier session too (#58).
  A value made longer and shorter again and again adds a page to its file at most, as in Access; when each change
  was a session of its own, every chain a session wrote first went to new pages.

`tools/CorpusDump` writes what the engine reads of every table of a folder of files as text, for comparing two
versions of the engine.

## Access 97 files are read-only

An Access 97 (Jet 3) file is read and never written, as in Java Jackcess, whose Jet 3 format is read-only
(`JetFormat.READ_ONLY`; `JetFormat.IsReadOnly` here). `Database.Open` opens one for reading only - a file marked
read-only, or on a read-only share, opens like any other - and everything that would change it is refused with
`NotSupportedException` before anything is written: inserting, updating or deleting a row, creating or importing
a table. `Database.Create(path, FileFormat.Access97)` is refused the same way, and makes no file. What a file is,
is what its own header says, as in Jackcess: an Access 97 file opened with a `DatabaseOpenOptions.FallbackFormat`
of `FileFormat.Access2000` is still read as an Access 97 file and still read-only, and an Access 2000 file opened
with a fallback of `FileFormat.Access97` is the Access 2000 file it is, read and written like any other. The
fallback format is used only for a file whose header does not say. `Database.IsReadOnly` says which a database
is. No row or table definition is made in Access 97's format either: `RowEncoder` and
`TableDefinition.Serialize` refuse it. Earlier versions
wrote to such a file, and wrote rows Access 97's format does not have: the end of a row's data and its count of
variable-length columns in two bytes each, where Jet 3 has one, so that the row's text read back as null
(qcatco/exportmdb.interop#67).

What is read of one:

- **Rows of any length.** A Jet 3 row gives the offset of each variable-length value in one byte, and a row
  longer than 256 bytes has a jump table - the number of the first value past each 256 bytes - as Jackcess reads
  it (`readJumpTableVarColOffsets`). Without it, the values of such a row past its first 256 bytes read as null
  or from the wrong place.
- **Indexes of any depth.** A Jet 3 index page has the length of the prefix its entries share in one byte, at
  offset 20, and the page's level in the next; Jet 4 has two bytes and then the level. Read as two bytes, the
  prefix of every page above the leaves was 256 too long for each level, and a lookup through such a page could
  miss its row (qcatco/exportmdb.interop#59). Jackcess reads two bytes there and keeps no level for Jet 3; the
  pages Access 97 wrote have 0 in the second on every leaf and 1 on every page above the leaves, and those Jet
  4.0 wrote have 2 on the page above those.
- **The rows of one key, wherever they are.** An index that is not unique can hold the entries of one key on
  more leaves than one; a lookup follows them from the leaf the tree leads to into the leaves after it (in a
  file of any format).

The evidence: the nine Access 97 files of the corpus, which Access 97 wrote, read as 32-bit ACE 12 and DAO read
them; and `tests/JackcessDotNet.Tests/Assets/access97-jet40.mdb`, a Jet 3 database that Jet 4.0 (32-bit DAO 3.6)
wrote, with rows either side of 256, 512 and 768 bytes and an index three levels deep, which reads as that
engine read it back (`tools/access97-fixture.ps1` made it; `Access97ReadTests`). The corpus alone does not show
all of this: its one row over 256 bytes has no value past them, none of its index pages above the leaves has a
shared prefix (one has two entries, the rest one), and no index of it is more than two levels deep. ACE 16
neither makes nor opens an Access 97 file.

## Building and testing

You need the **.NET 11 SDK**, which builds every target, `net11.0` included; an earlier SDK cannot restore the
project, and `global.json` asks for 11.0.100 or later. The tests run on Microsoft Testing Platform (`global.json`
selects it), which `dotnet test` runs from the 10 SDK on. The .NET Framework 4.8 tests run on Windows only.

```
dotnet build JackcessDotNet.slnx --configuration Release
dotnet test JackcessDotNet.slnx --configuration Release
```

- The tests are xUnit.net v3 and run on .NET 10, .NET 11 and .NET Framework 4.8: `dotnet test` runs all three,
  and `--framework net48` runs one.
- The `netstandard2.0` assembly is not one a test project can target, so the same tests run against it with
  `-p:NetStandardAsset=true`, on .NET 10 (as an application on .NET 8 or 9 loads it) and on .NET Framework 4.8
  (as one on 4.7.2 does). `LibraryAssemblyTests` fails if the assembly loaded is not the one the build was for.
  That build goes to `bin/Release-netstandard`, beside the ordinary one:
  `dotnet test --project tests/JackcessDotNet.Tests --configuration Release --framework net10.0 -p:NetStandardAsset=true`
- Options are the platform's and xUnit's, not VSTest's. One class: `dotnet test --filter-class "*UsageMapTests"`.
  A TRX report: `--report-xunit-trx --report-xunit-trx-filename test-results.trx`, written to `TestResults/`.
- `./tools/corpus.ps1` checks every corpus file against `tests/corpus/MANIFEST.sha256`. CI runs it before building.
- The engine's tests read back what the engine wrote. Whether Access accepts a file is tested where ACE
  is installed, in ExportMdb.Interop's suite.

Report problems and track work in this repository's [issues](https://github.com/markmario/jackcess-dotnet/issues).
Older issue numbers in comments and in this README, such as `qcatco/exportmdb.interop#64`, are in
ExportMdb.Interop's tracker, where the work was tracked until this repository had its own.

## Releasing

A release is a `vX.Y.Z` tag on `main`, and the tag is the version: there is no version to edit and no bump
commit. Merge, then tag the merge commit and push the tag. `.github/workflows/publish.yml` then:

1. **Checks the tag** (`scripts/Assert-ReleaseTag.ps1`) before building anything: a plain `vX.Y.Z`, on `main`'s
   own line (the merge commit, not a commit inside a pull request), above every release so far, at or above
   `MinimumReleaseVersion` in the project file, and not already released. A refused tag publishes nothing;
   delete it, fix what it tripped over, and tag again.
2. **Builds and tests** the tagged commit, packs it at that version, and checks what ships: the ID, the four
   frameworks, `LICENSE` and `NOTICE`, and the assembly, file and product versions of each DLL.
3. **Waits for approval** of the `nuget` environment, then pushes the package and its symbols to nuget.org
   through [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) - there is no
   API key in the repository - and makes a GitHub Release.

Every other run of that workflow - a pull request, a push to `main`, a run by hand - rehearses the same steps at
`<next version>-ci.<run>` and publishes nothing.

Before tagging, move what `src/JackcessDotNet/PublicAPI.Unshipped.txt` lists into `PublicAPI.Shipped.txt`, and give
`CHANGELOG.md`'s Unreleased section the version. A change that removes or changes public API raises
`MinimumReleaseVersion` to the next major in the same pull request.

**`.github/workflows/nuget-publish.yml` stays disabled** in the repository's Actions settings: never enable it,
never delete it, and never put a workflow back at that path. A tag runs the workflow of the commit it points at,
and every commit before publish.yml carries upstream's release job there, which published any `v*` tag with no
check. Disabling the workflow at that path stops it on every commit, old ones included. publish.yml's
`retired-workflow` job fails while it is enabled, and the publish job waits on it.

## Version history

[CHANGELOG.md](CHANGELOG.md). Up to 1.2.2 it is the history of the port this started from; what has changed
since is in [What this fork changes](#what-this-fork-changes) and in this repository's pull requests.

## Credits

This project would not exist without the years of reverse-engineering work
done by James Ahlborn (original Jackcess) and Markus Spann (maintained fork
at [`spannm/jackcess`](https://github.com/spannm/jackcess)). The Jet binary
format is documented effectively only through their Java source. The C# port
this repository started from is Mehran Ghanizadeh's
[`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet).

## License

[Apache License 2.0](LICENSE) — same as the upstream Jackcess project. [NOTICE](NOTICE) records the
works this is derived from.
