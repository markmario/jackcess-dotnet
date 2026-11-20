# Sharing a file with Access

Opening a file to be written or only to be read while Access, or another `Database`, has it open: who can open it
meanwhile and what is refused, what a reading made beside a writer can meet, what reading only costs, and what was
measured. This library takes none of Access's locks; a file it writes is shared as Access shares a database it has
open exclusively, except with readers.

## Opening a file to write, or only to read

```csharp
// Opened to be written (the default): no other program or Database writes to the file while it is open.
using var db = Database.Open("Orders.accdb");

// Opened only to be read: nothing is written to the file, and it opens while Access has the database open.
using var reader = Database.Open("Orders.accdb", new DatabaseOpenOptions { ReadOnly = true });
```

This library takes none of the locks Access takes on a database it shares with other users, so it cannot write to a
file another program is writing to: each would write over pages the other keeps track of. Two writers did ([#62]):
with ACE holding a connection while the library inserted, `COUNT(*)` gave 800 rows where 708 were there and
primary-key seeks returned other rows; two `Database`s inserting at once failed on a page ("Data page 47 has only
23 free bytes") and left 116 rows out of a primary key. So a file is shared as Access shares a database it has open
exclusively - except with readers, as Access's own read-only open shares one:

| The file is opened | Shared with | While it is open |
| --- | --- | --- |
| to be written: `Database.Open` | readers only | Access cannot open it to write, shared or exclusively: "Could not use '...'; file already in use" (error 3045). Access read-only - ACE's `Mode=Read`, DAO's read-only `OpenDatabase` - and a `Database` opened `ReadOnly` open it and read it. A second `Database` opened to be written is refused. |
| made anew: `Database.Create` | no one | Nothing opens it, Access in no mode and no `Database`, until the new database is disposed - as DAO holds a database its `CreateDatabase` made, without a lock file. Opened again with `Database.Open`, it is shared with readers. |
| only to be read: `DatabaseOpenOptions.ReadOnly` | readers and writers | Access opens it in any mode but exclusively, and writes to it; so does a `Database`. |

And the other way about, with Access - or another `Database` - there first:

| Access has the database open | `Database.Open` | `Database.Open`, `ReadOnly` | `Database.Create` over it |
| --- | --- | --- | --- |
| shared (ACE's default mode, DAO's `OpenDatabase`) | `DatabaseInUseException` | opens | `DatabaseInUseException` |
| read-only (ACE's `Mode=Read`, DAO's read-only) | opens | opens | `DatabaseInUseException` |
| exclusively | `DatabaseInUseException` | `DatabaseInUseException` | `DatabaseInUseException` |

## What each open does

- `DatabaseInUseException` is an `IOException`, with the HResult of a sharing violation (0x80070020) - what Windows
  gives for a file in use - so code that waits for a file someone holds for a moment goes on waiting. Nothing of the
  file is changed.
- The sharing is Windows', where Access runs, on a local disk and on a network share alike. Elsewhere .NET keeps to a
  file's sharing only by advisory locks, which do not keep every second writer out - and on iOS and tvOS by none - so
  there a second `Database` opened to be written is not sure to be refused: keep to one writer there.
- A database opened `ReadOnly` is held by a handle that cannot write, and every change - a row inserted, updated or
  deleted, a table or column made, dropped or renamed, a property saved, an import - is refused with
  `NotSupportedException` before anything happens (`Database.IsReadOnly`). A file marked read-only opens this way.
- `Database.Create` replaces a file only when no one has it open, readers included. It emptied a file Access had
  open, under Access. The handle that finds the file free is the one that empties it, and it is kept until the new
  database is disposed, so no one comes between to read a file that has become another.
- An Access 97 file, which is only ever read, is opened as one opened `ReadOnly` is: beside Access. DAO and ACE can
  write to it meanwhile, so it is read as a `ReadOnly` database is, its pages read again at every move, opened with
  `ReadOnly` or not - and pays what that costs. Measured by the review of #73 over the ten Access 97 files of the
  test corpus, every table and index walked 20 times: 78,420 pages read before, 612,280 now; about 0.55 s and 2.8 s
  on .NET 10, 0.85 s and 6.9 s on .NET Framework. What else is particular to an Access 97 file:
  [Access 97 files are read-only](file-formats.md#access-97-files-are-read-only).
- Access's lock file (`.ldb`, `.laccdb`) is neither made nor read. Measured with ACE 16 and DAO 16, and DAO 3.6 for an
  Access 97 file: Access makes one when it opens a database shared or read-only - 64 bytes a user, the machine's name
  and the user's - and deletes it when the last user closes; opening a database exclusively it makes none, and leaves
  one that no one holds as it is; one left by a process that was killed is taken over by the next user. Access with a
  database open shared holds the file open for writing, so the file's own sharing tells a writer Access is there.

## Reading beside a writer

**What a `ReadOnly` database reads while another program writes** is the file as it is at each move of a cursor, and at
each row of `Table.Rows`, by the columns and indexes a table had when it was first got: the pages a move needs are read
again, as the other program may have written to them since the last. No cursor or scan is told of the rows the other
program inserts, as one is of its own database's, so a row it inserts where a scan has still to come, on a page the
scan listed, is read. Access does not write a change to the file as it commits it. Measured with ACE and DAO, in
both formats: a row just inserted reached the file about half a second later - Access writes what it has committed
once it has been idle for half a second - but not always; in 2 of 12 runs of 300 inserts on a connection kept open,
the last row was still not in the file 15 seconds later, and reached it with the next inserts. So a reading can miss
the last rows Access committed for as long as Access keeps them; once Access has closed the file, everything it
committed is in it.

This library takes none of Access's locks, so nothing keeps such a reading from meeting another program's change
part-way, or from seeing at one move what was changed after the last. **What a reading beside a writer can do:** miss
rows - ones the writer adds or moves behind a cursor, and ones Access has not yet written to the file; give a row
twice - one the writer moves ahead of a cursor after the cursor has given it, or one on a page read while the writer
was part-way through writing it (the review of #73 saw a table scan beside ACE give a row twice, its Id unchanged,
once in about 2,000 readings); give a Memo or OLE value that is not the row's own - another row's, or part of one -
where the writer gives the place of the row's value to another between the reading of the row and the reading of its
value; and fail: a value that is not where its row says, its place deleted or too short for it, is refused with
`InvalidDataException`, and the reading can be made again. **What it cannot do:** change the file - its handle cannot
write; give a row as an earlier move read it; or give a value read from a place that has been deleted or is too short
for it. A writer - Access with the database open shared, or a `Database` - neither waits for it nor is kept out by
it; what needs the file to itself is, as the tables above have it: Access opening the database exclusively, and so
Access's Compact and Repair, and `Database.Create` replacing the file. **A reading that agrees with itself** - every
row once, each with its own values - needs no writer while it is made: read the file when no other program has it
open for writing. A database opened to be written keeps every writer out while it is open.

## What reading only costs

Reading again at each move costs a `ReadOnly` database time that one opened to be written - the only writer of its
file, which keeps a page from one move to the next - does not spend. A walk of 50,000 rows, 5,000 of them with a
300-character Memo, best of five, opened to be written and opened `ReadOnly`: an index cursor 79 ms and 1,176 ms
(5,549 pages read and 155,167: an index's root, its leaf and a data page at each move), a table cursor 72 ms and 215
ms (5,383 and 55,383), `Table.Rows` 28 ms and 168 ms (5,383 and 55,001). Measured by the review of #73, `Table.Rows`
over 500,000 rows: 53,694 pages read opened to be written, 550,002 opened `ReadOnly`; about 0.3 s and 1.7 s on .NET
10, 0.5 s and 2.1 s on .NET Framework. An Access 97 file is read so too ([What each open does](#what-each-open-does)).

## What was measured

Measured too, on the reading as it is now: while ACE inserted 145,000 rows, updating and deleting some as it went
(each statement its own transaction, and in transactions of 200), a `ReadOnly` database read the table 1,483 times,
in an .mdb and an .accdb. No reading failed or had a row twice or a value of another row's; two of 517,000 lookups
through the primary key missed a row the reading had found a moment before, which ACE may have deleted between the
two; a reading made while ACE was writing a transaction of 200 to the file saw part of it; and once ACE had closed,
the same open database read every row. That is what was seen, not a promise. (Access reading with `Mode=Read` while
this library wrote 2.1 million rows found nothing amiss either, in 521 readings.)

## Compared with Java Jackcess

Java Jackcess takes no lock, says that a file must not be written by two programs at once, and opens one read-only when
asked (`DatabaseBuilder.withReadOnly`); it also opens a file it cannot write to read-only without being asked, which
this library leaves to the caller.

[Back to contents](README.md)

[#62]: https://github.com/markmario/jackcess-dotnet/issues/62
