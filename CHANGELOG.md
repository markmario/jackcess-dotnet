# Changelog

All notable changes to JackcessDotNet (the `Jackcess.Net` package) are documented in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

The first release of this repository as a package of its own, `Jackcess.Net`. Everything below
1.2.2 is the history of [`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet),
where this started; nothing from it was released under this ID. It will be 2.0.0, because the public API has
changed since 1.2.2.

### Changed

- **Breaking: a complex column - an attachment, a multi-value field, version history - is a `DataColumn` of type
  `object` in a `DataTable`** (#152), where 1.2.2 and the 2.0.0 previews to preview 6 made it one of type `int`.
  Its value there is `DBNull.Value` in every row, as it was, and a row read from the table has null for it, as it
  had: the column's values are not read (#24), and the number the row holds for it, which is not its value, is not
  given. A column of `object` can hold whatever those values are read as without changing its type. A table with a
  complex column takes no insert, update or delete, and `Table.InsertRefusal` says why: Access gives each row a
  number for the column from a counter of the table's own and deletes a row's values with it, neither of which this
  library does. A table Access made was refused already, for the unique index Access keeps on every complex column
  and does not let be dropped (measured with ACE 16, `tools/dao-complex-rows.ps1`); one without that index took
  rows, with the column null, which Access never writes.
- **Breaking for code written against a 2.0.0 preview: `Database.DatabaseProperties` is `Database.Properties`**
  (#34), as a table's are `Table.Properties` and a column's `Column.Properties`. 1.2.2 had neither.
- **Breaking for code written against a 2.0.0 preview: `ColumnValueTooLongException`, `ConstraintViolationException`
  and `DatabaseInUseException` have no public constructor** (#34), as `BatchInsertException` and
  `DatabaseFullException` have none: the library is what throws them. Catching one and reading it are as they were.
  1.2.2 had none of the three.
- **Breaking: `PropertyMap.Property` is `Property`** (#34), a type of its own rather than one inside
  `PropertyMap`: what a table's, a column's or the database's `Properties` lists. Its members are as they were.
- **Breaking: an index cursor finds rows by key, as `Table.FindRowByPrimaryKey` does** (#34).
  `IndexCursor.FindRowByEntry`, which 1.2.2 had, is `FindRowByKey`; and, for code written against a 2.0.0
  preview, `FindRowsByEntry` is `FindRowsByKey`, `FindClosestRowByEntry` is `FindClosestRowByKey`, and
  `IndexRange.StartEntry` and `EndEntry` are `StartKey` and `EndKey`. Each lookup's parameter is `keyValues`.
- **Breaking for code written against a 2.0.0 preview: `Diagnostics.DecimalBeyondPrecision.Table` is `TableName`**
  (#34), as a relationship's `FromTableName` is: it is the table's name, not a `Table`. 1.2.2 had no such type.
- **A `double` or a `float` given for a Text or a Memo column is written as Access writes it, on every target, and
  a lookup by one looks for that text** (#124). It is 15 significant digits for a double and 7 for a single, a half
  away from zero, written out below 10^15 (10^7) unless that takes more than 15 (7) places after the point, and
  otherwise with an exponent of two digits or more. It was the runtime's own text: .NET's round-trip form,
  "0.30000000000000004" for 0.1 + 0.2 where Access writes "0.3", and on .NET Framework "1E-05" where Access writes
  "0.00001", and "0" for -0.0, which Access writes "-0". Measured with ACE 16 (a Double or Single parameter, and
  `INSERT ... SELECT` from a Double or Single column) and DAO (`AddNew`, and a Text property), which write the same.
  The row, its index keys and a lookup of the column by a double take that text. **So a row an earlier version wrote
  with a double is not found by it**: `Cursor.FindRow`, an index cursor's `FindRow` and `FindRowByKey` given
  0.1 + 0.2 do not find `0.30000000000000004`, nor 1E-05 `1E-05`; such a value is found by its text
  ([Files written by earlier versions](docs/earlier-versions.md#other-things-an-earlier-version-wrote)). A Text
  property given a double or a float is written the same, in the invariant culture.
- **Breaking: text whose bytes begin 0xFF is read and written as Access reads and writes it** (#93). 0xFF 0xFE is
  the mark of compressed text, and the UTF-16LE of U+FEFF is 0xFF 0xFE too. Measured with Microsoft 365's ACE 16
  and DAO (`tools/ace-text-marker.ps1`):
  - Bytes that begin 0xFF 0xFE are read as compressed text where ACE reads them so - a Text column with Unicode
    compression, a Memo kept in the row, a Memo in one long-value row of a column with Unicode compression - reading
    two bytes at a time after a switch to UTF-16LE; and as UTF-16LE in a Text column without it, on a long-value
    page of such a Memo column and in a chain. Earlier versions read every such value as compressed text.
  - **Which files read differently:** a table Access made whose Text column has no Unicode compression (DAO's
    `CreateField`, DDL without `WITH COMPRESSION`), to which the JackcessDotNet packages to 1.2.2 or this
    repository's builds before 442bbf8 wrote, compressing every value of characters 0x01 to 0xFF. Earlier versions
    read `Alice` there; this one reads what Access reads from the table, U+FEFF U+6C41 U+6369 (and U+FFFD for the
    odd byte at the end). Nothing tells such a value from Access's own. That is what a DAO table-type recordset
    reads, and a DAO dynaset of the table and an ACE query of it; some other readings - a DAO snapshot of a query
    of one row, an ACE query of one row sorted by Name - read the bytes as compressed text, and others fail on them
    (`tools/earlier-filled-text-readers.ps1`, #129).
  - A table those versions created - its catalog row has ParentId 0 and no owner - is read by its bytes, as they
    wrote it: it is listed and found by its name, which Access cannot do, and its values read as written. Its
    row of MSysObjects read as a table gives that name too, as earlier versions gave it, and a refused rename
    quotes it (#121). Renamed to a name that begins with U+FEFF, which Access takes for a table, a column and an
    index (`tools/dao-feff-names.ps1`), such a table's name is written compressed in runs, as its text is, so that
    it is found by it, and refused with an `ArgumentException`, with nothing changed, where it would read back
    changed (#129).
  - Text that begins with U+FEFF is written as ACE writes it: compressed in runs where that is no longer than
    UTF-16LE in a Text column with Unicode compression, and shorter in a Memo; `"\uFEFFabQ1Z"` was UTF-16LE, which
    ACE read back as `"abQ1Z"`. Over 3,816 bytes of UTF-16LE in a Memo of such a column, it is a chain, as ACE makes
    it.
  - Text that begins with U+FEFF that Access would read back changed is refused with an `ArgumentException`, by
    insert, update, batch insert and import, before anything is written: in a Text column with Unicode
    compression unless compressed in runs it is no longer than UTF-16LE (U+FEFF alone, or with three Latin
    characters or fewer, or followed mostly by other characters, is refused); in a Memo kept in the row, or in one
    long-value row of a column with Unicode compression, unless compressed in runs it is shorter.
  - 0xFF followed by another byte was read as the mark of text a byte a character, which no version of this
    repository wrote: `"\u01FFab"`, stored as `FF 01 61 00 62 00`, read as other characters. It is UTF-16LE.

- **A memo's text is stored as Access stores it** (#87). It was UTF-16LE whenever it went to a long-value page,
  and a zero-length memo the two-byte compression marker. Measured with ACE 16: no text is no bytes, 1 or 2
  characters UTF-16LE, and 3 to 1,908 characters that are all 0x01 to 0xFF compressed, a byte a character after
  0xFF 0xFE; longer text, or text with another character, UTF-16LE. A Latin memo of up to 1,908 characters is half
  the bytes it was. Text mixing the two, which ACE compresses in runs, is still UTF-16LE.
- **A file is not grown past 2 GB** (#82). Access reads a longer file this library wrote, and cannot write to
  it: ACE refused an insert into one of 2,160,779,264 bytes, "Cannot open database ''... may be corrupt", and took
  one into a file of 2,140,618,752. The page that would pass 2,147,483,648 bytes is refused with the new
  `DatabaseFullException` (an `IOException`, with `MaxLength`), and the change it was part of is taken back, as one
  that fails part-way is. The engine wrote such files before: a table's usage map refused to grow only past
  17 map pages, about 2.28 GB.
- **Each index's count of its keys is written** (#82). Access keeps, in the table's definition (bytes 4-7 of each
  tree's block of counts, Java Jackcess's `uniqueEntryCount`), a count of keys it plans queries by. The engine left
  it as it was, so every index it filled was one Access took to have no keys. It now writes, for a tree built whole,
  the number of its keys, and adds one for each row inserted under a key no row had, in the write of the definition
  an insert makes anyway: no page more. An update or delete changes nothing. Beside ACE's twin of an export the
  counts are equal in all 35 of the template's indexes; Access's own updates and deletes move them in ways not fully
  measured.
- **A long change no longer holds a copy of a kept page for every write of it** (#82). A change kept for undoing
  noted the header's and the global usage map's pages as they were at every write of them, and the global usage
  map is written for every page a change adds: a batch of a million rows held a copy of it for each page it
  added, 300 MB for an export of 50,000 documents. It notes each as it was before the change first wrote it, which
  is all taking the change back needs.
- **Package ID `Jackcess.Net`.** nuget.org's `JackcessDotNet` is the other port's package. The
  assembly and namespace are still `JackcessDotNet`.
- **Targets `net10.0`, `net11.0`, `net48` and `netstandard2.0`** (it was `net8.0`, `net472` and
  `netstandard2.0`). **Breaking for an application on .NET Framework 4.7.2:** there is no `net472` assembly, so
  it gets the `netstandard2.0` one, with that assembly's package dependencies. What each application gets:
  - .NET 10: `net10.0`. .NET 11 and later: `net11.0`. Neither has a package dependency.
  - .NET Framework 4.8 and 4.8.1: `net48`.
  - .NET 8 and 9, and .NET Framework 4.6.2 to 4.7.2: `netstandard2.0`.
- **The `netstandard2.0` assembly opens a database on .NET.** It did not register the provider of the legacy code
  pages, so on .NET 8 or 9 every `Database.Open` and `Database.Create` failed with a
  `TypeInitializationException` ("No data is available for encoding 1252") unless the application had registered
  the provider itself. It depends on `System.Text.Encoding.CodePages` and registers it.
- **The tests run on every target**: on .NET 10, .NET 11 and .NET Framework 4.8, and against the
  `netstandard2.0` assembly on .NET 10 and on .NET Framework 4.8. They ran on .NET 8 only. CI runs the five side
  by side, and checks the packages each target of the package depends on.
- **The Access referee tests are in this repository** (`tests/JackcessDotNet.AccessTests`): Access's own engine,
  ACE and DAO, reads, queries, writes to and compacts what this library writes. CI installs the Access Database
  Engine 2016 redistributable and runs them in shards on every pull request; on a machine without Access they are
  skipped, saying why. See [Building and testing](https://github.com/markmario/jackcess-dotnet/blob/main/docs/building-and-testing.md#the-access-referee-tests).
- **Files Access accepts.** Every change listed under
  [What this fork changes](https://github.com/markmario/jackcess-dotnet/blob/main/docs/differences.md#what-this-fork-changes):
  files up to the format's 2 GB, Access's own text index keys, every index kept on every write, AutoNumbers from the table's
  counter, rows and long values laid out as Access lays them out, `.accdb` creation, Large Number and Date/Time
  Extended columns, and foreign keys enforced.
- **Public API.** Pages, formats and codecs are internal; the public API is recorded in
  `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` and the build checks it. A broken rule of the database
  has its own exception, and `TryGetTable` was added.
- **A column's Required and Allow Zero Length are Access's** (#41). They are properties of the column, not
  part of the table's definition: `Column.IsRequired` and `Column.AllowZeroLength` read them (they said `false`
  of every table that was opened), and `CreateTable` and `AddColumn` write them, as DAO writes them for a field
  it makes. **Rows are held to them, as Access holds them**: `Insert` refuses, with
  `ConstraintViolationException`, a row with no value in a required column or a zero-length string in a Text or
  Memo column that does not allow one, and an update refuses the same of the columns it is given a
  value for. A table made by an earlier version has no such properties and takes the rows it took.
  `Column.AllowZeroLength` is `false` for a column that is not Text or Memo.
- **A Replication ID AutoNumber has the `DefaultValue` property `GenGUID()`** when it is made with a table or
  added to one (#33), so Access generates the GUID of a row it adds; it left the column null.
- **An insert and a delete are written whole or not at all**, as an update is: a failure while one is being
  written takes back what it wrote, and an insert gives the row back with the AutoNumber values it came with. A
  delete of a row whose values are on another page left one page changed, and an insert could leave a page added
  or a row without its index entries.
- **A change that fails at its first write takes back what it noted** (#61), as one that fails later does: an
  insert that took a page a delete had emptied for its long value, and then could not write - a full disk - left
  the session without that page, so later values went to new pages until the database was opened again. What a
  change learnt of the file before it wrote anything is kept, as it is still true: a change refused before it
  writes, a duplicate among them, does not have the next read the table again for `Database.VerifyIndexes`. This
  fixes a defect on `main`.
- **A table is created whole or not at all** (#60), as every other change is written. `CreateTable` - and an
  import into a new table, which makes its table the same way - wrote outside any undo scope: a failure part-way, a
  full disk, left pages added to the file and, once the catalog row was written, a half-made table that
  `GetTableNames` listed and that Access, in an `.mdb`, refused to read ("no read permission"). What it writes is
  now taken back if writing fails: the file is byte for byte as it was, and the table is not there. A table made
  inside a larger change that is taken back is forgotten with it, and the `Table` handed out for it is marked
  dropped: it refuses whatever is asked of it with `InvalidOperationException`, where it read past the end of the
  file and, once a table was made again in its pages, wrote rows into that one.
  **`Database.Create` leaves no file when writing the new one fails**: it is closed and deleted. It was left at the
  path, part of a database, and held open. When it cannot be deleted - another process holds it - an `IOException`
  says the incomplete file is still at the path, with the failure as its `InnerException`.
- **A Binary value longer than its column is refused** (#45), before anything of its row is written, with
  `ColumnValueTooLongException`, as a Text value is: for a column kept among the variable-length values (what this
  library and DAO's `dbBinary` make, SQL's `VARBINARY`), which took the value whole, and for one kept among the
  fixed-length ones (SQL's `BINARY`). Access refuses one byte over its length in both, through ACE and through
  DAO, on an insert and an update, in an `.mdb` and in `.accdb` files of Access 2007's and 2010's formats ("The
  field is too small to accept the amount of data you attempted to add"). The exception's `MaxLength` and `Length`
  are in bytes for a Binary column, as `Column.MaxLength` is. An update writes the row whole, so a Binary value an
  earlier version stored longer than its column refuses an update of any column of its row, as a Text value stored
  so already did (and Jackcess's write of a Text value does); the message then says the value is the stored one and
  that giving that column a value that fits lets the update through.
- **A number looked for is matched as exactly the same number** (#49), by a cursor over a table and through an
  index alike. A `double` or `float` looked for in a Byte, Integer, Long or Large Number column finds a row only
  when it is that whole number exactly: compared through a `decimal`, which keeps 15 significant digits,
  5.000000000000001 found an Integer's 5, and the double 1234567890123460096 found a Large Number's
  1234567890123460000 by a scan and nothing through the index, where 2^60 as a double did not find 2^60. ACE's
  `=` does not answer alike through an index and without one; where its answers differ, this library gives the
  exact one: as ACE's scan, -0.0 finds 0, -2^63 as a double finds a Large Number's `long.MinValue`, and a decimal
  with a fraction finds nothing, where ACE's index finds nothing for -0.0 and -2^63 and rounds the decimal (5.5m
  finds 6, 0.1m finds 0, -1.23465m finds -1); as ACE's index, 2^53 as a double does not find a Large Number's
  2^53 + 1, and a Double's or a Single's +0.0 and -0.0 are two numbers, where ACE's scan compares as doubles and
  finds both. A Currency is the number looked for taken to its four places as a row is written with it, as Access
  takes it (#65, below): 0.03125 finds 0.0312, and the double 0.00015, which is just under that half, finds
  0.0001, where taken through a `decimal` they found 0.0313 and, on .NET Framework and .NET up to 10, 0.0002; and
  1.23456m finds the row of 1.2346, where it found none. Text is matched as text, as it was: `"1.0"`,
  `" 1"` and `"01"` do not find `"1"`, as a text primary key does not find a row whose key reads as the same
  number.
- **A `double` or `float` written to a Decimal column, or looked for in one, is the decimal Access makes of it, on
  every runtime** (#49): rounded to 15 significant digits, or 7 for a `float`, as OLE Automation rounds it - an `=`
  with the double 0.1, or the single 0.1, finds Access's Decimal 0.1. `Convert.ToDecimal` rounds so on .NET
  Framework and .NET up to 10, and on .NET 11 converts exactly: there a double was written to a Decimal column of
  more places than 15 as its binary expansion (0.1 as 0.10000000000000000555), and a lookup by a double found no
  Decimal it should. This fixes a defect of the `net11.0` target on `main`.
- **A Currency is made from a caller's number as Access makes one, wherever one is made** (#65): the number's exact
  value - a `double`'s or `float`'s binary value, a `decimal`'s digits, an integer - taken to four places, half to
  even, the same on every runtime. Measured with ACE 16 and DAO, in an .mdb and an .accdb, and alike for ACE's
  `INSERT` and `UPDATE` with a parameter of the number's type, DAO's `AddNew` and `Edit` on a dynaset and on a
  table, and ACE's `=`, `BETWEEN`, `>=`, `>`, `<=` and `<` through an index (`tools/ace-currency.ps1` writes 7,198
  numbers; the tests hold this library to what ACE stored for each). A row is written with that Currency, keyed
  with it, compared with it for a relationship and found by it - by a cursor over the table, through an index, in
  a range and as the closest row - and a property and an imported value are made the same way. So a row written
  with a number is found by that number. What changes:
  - **Writes and their keys.** A `double` or a `float` went through `Convert.ToDecimal` and was rounded half away
    from zero: 0.03125 was written as 0.0313 and 0.00015 as 0.0002, where Access writes 0.0312 and 0.0001, and such
    a row was not found by the number it was written with. On .NET Framework and .NET up to 10, where
    `Convert.ToDecimal` keeps 15 significant digits of a `double` and 7 of a `float`, 562949953421312.5 was written
    as 562949953421312 and ±922337203685477.5 were refused, which Access writes, and the float 98765432 was written
    as 98765430, 123456.79 as 123456.8 and 1234.5677 as 1234.568, where Access writes 98765432, 123456.7891 and
    1234.5677. A `decimal` is taken half to even, as Access writes one: 1.23465 is 1.2346 (it was 1.2347). Against
    what ACE stored for the 7,196 numbers of `tools/ace-currency.ps1` (NaN aside), the earlier rule gave another
    Currency for 2,005 of 3,619 doubles, 530 of 841 floats and 1,369 of 2,736 decimals on .NET Framework and .NET up
    to 10, and for 201, 107 and 1,369 of them on .NET 11, whose `Convert.ToDecimal` is exact. A file an earlier
    version wrote keeps the values it was written with: see "Breaking changes" below.
  - **Ranges and `FindClosestRowByKey`** keyed a `double` as a write did: a range of 0.03125 alone had the rows
    of 0.0313, where `=` found 0.0312's, and on .NET Framework and .NET up to 10 a bound past 15 significant digits
    was refused with `ArgumentException` or found no row. A bound no Currency holds - NaN, an infinity, a number past
    the range - is refused with `ArgumentException`, as Access refuses one past the range ("Overflow").
  - **Relationships** compared a child's key with its parent's as a write rounded it, so the double 0.03125 had no
    parent 0.0312. **A property** took a `double` through a `decimal` of 15 digits. **An import** that adds a
    `double` or `float` to a Currency column (`AppendIfExists`) takes the numbers it took and refuses those it
    refused, but each as the Currency Access makes of it: past 2^38 or so it took the number the round-trip form
    says, which can be another Currency (35184372088832.0078125 was 35184372088832.01; it is 35184372088832.0078).
  - **Text, a `bool` and a date are taken as every number column takes them** (#75, below), and the number they
    make is taken to four places by the same rule. **A write refuses, before anything is written,** NaN, the
    infinities and a number past the range once taken to four places with `OverflowException`, as a number past the
    range was refused; a `Guid`, bytes or a `char` with `ArgumentException`, where it was an `InvalidCastException`;
    and text that reads as no number with `FormatException`, as before, now naming the column.

  Where Access differs: ACE and DAO write NaN as the least Currency (-922,337,203,685,477.5808), the integer a
  processor makes of NaN, and their `=` finds that row for NaN; and ACE's scan, with no index to answer, takes a `decimal` through a `double` - 0.00015m is 0.0001
  there - so it does not find the row its own `INSERT` wrote with that decimal (0.0002). ACE's index, its writes and
  DAO's take a decimal exactly, and so does this library, by a scan as through an index.
- **A Date/Time on 1 January 100 is read at any time of day**, as Access reads it; it was null unless midnight.

- **Entries that point at no row are taken out** (#36). A version of this library that did not keep every index
  left an entry where a row had been, and an entry under the value a row had before its primary key was changed.
  ACE fails a query that comes to the first ("Not a valid bookmark") and answers one for the second's key with
  the wrong row. An entry that points at no row - a slot deleted or never used, a page that is not the table's -
  is no row's duplicate, and goes when an entry is added or taken out under its key; with
  `Database.VerifyIndexes`, every such entry an index was seen to hold goes when the table is next changed. An
  entry that points at a row of the table is believed, as Access believes it: a row with its key is refused, and
  the entry stays, whatever this library makes of the row's values - its key for them may not be the one Access
  wrote, and an entry Access wrote taken out on that judgement is a row Access can then neither find nor update.
  Only with `Database.VerifyIndexes`, and where the row's key is one this library makes as Access does (not a key
  cut at 510 bytes in an index of several columns other than Text and Long), is such an entry the row's wrong one:
  a row with its key is taken, the entry goes, and the row is entered under its own key. A removal that takes out
  a leaf's last entry reads the next leaf's first, one page more, for another entry under the key. An index with
  no such entry costs nothing more to write to. An update of a row whose wrong entry is moved enters the row under
  its new key once, and an entry an index holds already is never added again; this fixes a defect on `main`, where
  such a row was entered twice - by the move, which read the row as it was being written, and by the update - and
  Access walked it twice, refused to delete a parent of it once it had deleted the row, and DAO compacted a file
  whose primary key held such an entry without the key.
- **A Date/Time Extended value is keyed by the 42 bytes its row stores, as Access keys it** (#51); this fixes a
  defect on `main`. Access writes a date in more than one form - its `INSERT` the time of day in 100-nanosecond
  units and a count of 7, its SQL `UPDATE` and DAO's `AddNew` and `Edit` on a dynaset the time in whole seconds and
  a count of 0 - and keys each by its bytes, not by the date they say: one date in two forms is two keys, which a
  unique index and a primary key take, and a child whose bytes are another form of its parent's date has no parent
  (Access refuses its insert, and a relationship created over it). This library keyed the date it read, in the
  first form. So,
  enforcing foreign keys, a parent was deleted from under a child ACE had keyed in the second form, and its entries
  left pointing at no row; a child with a date its parent has in another form was taken; verifying its indexes, it
  refused a row ACE takes beside one in the second form; and an index or a primary key added over rows in another
  form held them under keys ACE does not look under, or refused a primary key over two forms of one date. Now a
  row's key is made of its bytes, in whichever form they are, and a caller's `DateTime` is keyed in the first form,
  which is what Access's `INSERT` writes and this library writes: every entry ACE made over rows in its two forms,
  and in three more it reads a date from but does not write, is the key this library makes. Such an index is
  searched for a relationship's rows and a unique index's duplicates as an index of any other type is. A
  `DateTime` looked for through one - `Table.FindRowByPrimaryKey`, an index cursor's `FindRow`, `FindRowByKey`
  and `FindRowsByKey` - is looked for under the key of each form, so a row ACE stored in its second form is
  found, as a cursor over the table finds it (it was not); in a range's start or end key and given to
  `FindClosestRowByKey` it is the first form's key, as Access's `BETWEEN` through an index has it. A `Joiner`
  joins a row read from a table on the bytes it stores. A row in the second form is deleted, or its date
  rewritten, for what a Date/Time row costs, its entry found under the key of its bytes: at 5,000 rows 6 pages
  where 86 were read, every leaf of the index. An entry a version of this library since 2930d3f made for a row in
  another form - when it added the index or rewrote the row - is under the key of its date in the first form, or of
  no value, and is looked for there, one way down the tree each, when the row has none under the key of its bytes.
  A relationship's search for such a row, a unique index's for its duplicate and a `Joiner`'s by its bytes look
  under the key of its date in the first form as well, each row found there read and compared by its bytes: looked
  for under the key of its bytes alone, the child of a parent being deleted was missed, trusting the index - the
  parent was deleted from under it, and a new date did not cascade to it - where `main` found it under the key it
  had made. No search looks under the key of no value, under which every row with no value is, each of which would
  be read: a row the versions from 2930d3f to db6f93b entered there is found when it is deleted or its date
  rewritten, and by a search with `Database.VerifyIndexes`, and not by a search that trusts the index.
- **A Date/Time Extended value is read as Access reads it** (#64); this fixes a defect on `main`. Access reads the
  42 bytes as three numbers, not as fields at fixed places: the day from the start, the time of day after the first
  colon and the count of the fraction's digits after the last, each read up to its first byte that is not a digit.
  DAO's `AddNew` and `Edit` on a table-type recordset - what DAO's `OpenRecordset` gives a table when it is asked
  for no other kind - write a layout of their own, the day in fifteen digits and four bytes after the value that are
  whatever DAO's buffer held. Such a value was read as null. It is read as its date, and a lookup by a `DateTime`
  finds it: by a cursor over the table, and through an index among the keys that begin with the layout's other bytes,
  which Access's own `=` through an index does not (measured; it finds the first form alone); a `Joiner` joins it by
  its bytes. The values Access reads a date from in other shapes are read as that date too, as measured with DAO 16
  and ACE 16 on bytes changed in a file Access made: a number read up to a byte that is not a digit (a day of
  `73742X` is day 73742), no digit read as 0, a count of 1 to 9 dividing the time into seconds and a remainder in
  units of 100 nanoseconds (`11045123` with a count of 3 is 03:04:05.0000123). An error in Access - DAO gives Empty,
  and ACE's `=` fails - is read as null, and so is a value no `DateTime` holds. Writes are as they were: the first
  form, as Access's `INSERT` writes it, and an update that gives the column no new value keeps its bytes, the four
  after the value among them, as Access's `UPDATE` and both kinds of recordset keep them (measured). An entry a
  version of this library that keyed the date it read (#51) made for such a row is under the key of no value, which
  those versions read it as: it is found when the row is deleted or its date rewritten, and no search looks under
  that key for the row. On `main`, which read such a row as null, a relationship's search for its bytes, a unique
  index's for its duplicate and a `Joiner` from it read every row with no value: 40,053 pages to change another
  column of such a row beside 20,000 rows of no value, where 8 are read.

- **A long-value page is listed as having room from 258 bytes**, where it was 264 (#35). ACE, writing OLE
  Object values of chosen lengths, lists a page left with 258 bytes or more and none left with 257 or fewer; the
  sizes between could not be made with text. A value goes onto a page Access left with 258 to 263 bytes where
  Access would put it, and such a page is no longer taken out of the map.

- **Text keys in the orders of nine more locales, and Access 97's text indexes searched** (#23). A database
  made in another locale has that locale's LCID for the sort order of its text. ACE keys the text of Arabic,
  German, Greek, Hebrew, Italian, Dutch, Russian, British English and Portuguese databases exactly as it keys
  General - Legacy - compared over 1,013,302 values each - and tables whose text sorts in one of those orders
  are written to, where they were refused. Every other order is refused as before;
  [Indexes and keys](https://github.com/markmario/jackcess-dotnet/blob/main/docs/indexes.md#refused) lists the ones
  measured to differ. An Access 97 file, which is only read, has rows found through its text indexes: its
  General order's keys are made (Jackcess's `General97IndexCodes`, and ACE 12's keys where they differ for Œ, Æ
  and ß among accented letters).

- **A key of several columns cut at 510 bytes is Access's** (#56); this fixes a defect on `main`, which no
  release has had. Access ends a cut key in a digest of every byte it cut off; this library left the last of
  them out, as Jackcess does. That byte is 0 in a key that ends in text, so keys of one column, a Memo's and keys
  that end in a text column were Access's; a key that ends in a number, a date or the like after text that
  filled the entry was not, and Access could neither update nor delete such a row ("The search key was not
  found in any record") nor find it by `Seek`. In a unique index two rows whose values differ only in the last
  byte of such a key were taken for duplicates, where Access takes both. The digest is now Access's: compared
  with the entries ACE 16 wrote, in General - Legacy and in General, 448 of them in seven indexes (a text key cut
  in the first, middle and last column, before a Long ascending and descending, after another text, and
  unique), and DAO seeks, updates and deletes the rows this library writes. An entry a version since the cut
  wrote the earlier way is found, and taken out with its row; a rewritten row is entered under Access's key,
  and with `Database.VerifyIndexes` an index of Text and Long columns that is looked at has such entries
  replaced when the table is next changed. The earlier way's key of a value is Access's key of another - the
  same with the last byte cut off 0, so (A, 1)'s and (A, 255)'s are (A, 0)'s - and an entry found under it alone
  is counted as a duplicate, or followed to a row, only when the row it points at has the value: such rows are
  both taken, in a file of Access's or of this library's, and a second row with either value is refused.

- **Table definitions of more than one page** are read whole, written and changed (#37). A table whose
  definition Access put on several pages has all its columns' names and takes rows; a column, an index or a
  relationship that takes a definition past its page is added, where it was refused; and a table whose
  definition is longer than a page is created.
- **A definition that ends within eight bytes of the end of a page has the page Access gives it after that
  one** (#37); this fixes a defect on main. Access puts a definition on the fewest pages that hold its length
  and eight bytes more, so one of 4,089 to 4,096 bytes has a second page with none of it, one of 8,177 to 8,184
  a third. A table created with a definition of 4,089 to 4,096 bytes, or a column added that took one there,
  was written on its one page, as Jackcess writes it, and Access then opened nothing of the file, `.mdb` or
  `.accdb`: "Unrecognized database format".
  Each page's free space is Access's too: the last page's is what the pages hold less the length and eight
  bytes, and every other page's 0.
- **A table whose definition goes on to a page that is not its own** - one the file has not, one already read,
  or one that is no definition page - is opened, and read by the columns its sound pages name; nothing is
  written to it or changed in it (#37).
- **An import holds a `long` whole** (#38). A `long` column or property was a Long Integer, and a value past 32
  bits stopped the import at its row. It is a Large Number in a file of Access 2016's format or later, and a
  `Decimal(19, 0)` in an older one - an `.mdb`, or an `.accdb` of Access 2007's or 2010's format. **An import does
  not change a file's format**: a Large Number column makes a file one of Access 2016's format, which Access 2010
  and 2013 do not open, so the importer makes one only where the file is of that format already. A key on such a
  column in an older file is a Decimal key, refused before anything is written.
- **An import holds every value of its source exactly, or refuses before anything is written** (#44).
  - **Whole or not at all.** Every value is converted to what its column holds before anything is written, and
    a value no column holds exactly is refused then, with `ArgumentException` naming the column and the row. What
    is written is written inside one undo scope, so a row the database refuses part-way - a duplicate in a key, a
    null in a required column - takes back the table, the rows before it and, for `ImportTables`, every table
    before it: a refused import leaves the file byte for byte as it was. The table and the rows before a refused
    row stayed. The session that refused it holds nothing it made: a table taken back is not listed or found, and
    its name and its pages are free - a table made after it on its definition's page is that table, not the one
    taken back, which refuses whatever is asked of it. `ImportTable<T>` reads every object before it writes; it
    read them as it inserted.
  - **Columns that hold every value.** `sbyte` is an Integer (it was a Byte, and a negative value stopped the
    import), `ushort` a Long Integer (an Integer), `uint` a Large Number in a file of Access 2016's format and a
    `Decimal(10, 0)` in an older one, `ulong` a `Decimal(20, 0)` (both were Long Integers, and a value past 31
    bits stopped the import).
    `decimal` is a Decimal whose scale is the most places a value has and whose precision the digits the values
    need, 18 at least - refused past 28 - where Currency rounded past four places. A `string` with no length is
    Text(255) when every value fits and a Memo otherwise, and one with a length longer than a value is a Memo (a
    longer value stopped the import). A `byte[]` is Binary only where a length is given and every value fits it,
    and an OLE Object otherwise: it was Binary(50), which took a longer value that ACE could not then read. A
    `byte[]` the source gives no length - every `DataTable`'s, whose `DataColumn.MaxLength` takes none - is an OLE
    Object, as Java's importer makes a BLOB one: it holds every value, and any added later.
  - **A null for a Yes/No column, and a date before 1 January 100, are refused.** They were stored as No, and
    `DateTime.MinValue` as 30 December 1899. An empty byte array is written as `Table.Insert` writes one: a value
    of no bytes, as ACE's own INSERT writes it, which ACE's OLE DB and SQL read back as one, and DAO as Null.
  - **Required, and keys of several columns.** A `DataColumn` with `AllowDBNull = false`, and a property with
    `[Required]` or of a value type that is not nullable, makes a required column. A `DataTable`'s primary key of
    several columns is the table's key, as are several `[Key]` properties, in the order of their
    `[Column(Order = n)]` and then as declared; only a key of one column, or the first `[Key]`, was carried over.
  - **Adding to a table converts a value exactly or refuses it** (`AppendIfExists`): a value went through
    `Convert`, which rounded a `double` into an integer column, a `decimal` into Currency, and text in the current
    culture. Text with an offset is the UTC time it denotes, as a `DateTimeOffset` is, and text without one the
    time it says, whatever the machine's time zone; a time without a date, `"10:30"`, is on 30 December 1899, as
    ACE inserts one in a Date/Time or Date/Time Extended column - it was on the day of the import. A `double` or
    `float` goes to Text as its round-trip form, the same text on every target: .NET Framework's round-trip format
    does not always read back as the double.
    Whether a number goes to a numeric column is worked out exactly, so it is the same on every target. Text goes
    to a numeric column as the number it denotes, only when the column holds exactly that number; NaN, the
    infinities and a number past the column's range, or too near 0 for it, are refused, whatever the size of the
    exponent it is written with. Bytes longer than a Binary column are refused as text longer than its column is,
    with `ColumnValueTooLongException`.
- **A Date/Time Extended value Access wrote in whole seconds is read as its date** (#38): the form Access's SQL
  `UPDATE` leaves in a row, which was read as null. Its index key is made of its bytes, as Access makes it (#51).
- **A new table or relationship has its owner's entry in `MSysACEs`**, with the others its container passes
  on, as Access gives them (#37). Without it, Access in an `.mdb` did not read a table this library made in a
  database DAO had created, nor drop a relationship it made.
- **A primary key made with a table is unique and required** (its block's flags `0x89`), and **an index that
  is no relationship's has 4 in its slot's two cascade bytes**, as Access writes both (#37).
- **Two names are one as Access takes them** (#44, #63). A table, column, index or relationship is refused a name
  that is one with another of its kind, before anything is written, as Access refuses it: by the names' text index
  keys, in the order Access compares names by, as DAO 16 and Jet 4.0 were measured to decide it for 18,140 pairs of
  names ([Names](https://github.com/markmario/jackcess-dotnet/blob/main/docs/tables-and-schema.md#names)). Names
  in another case, width or kana, or with an accent written as one character or as two, are one; `xµ` and `xμ`,
  and `I` and `ı`, are two, on every target. In a database that sorts in General, a name is compared by General's
  keys and General - Legacy's: Access compares some names there by the one and some by the other, a column's as its
  process has gone, so some names DAO makes there are refused. Names were compared as .NET compares them without
  regard to case, which kept apart names Access takes as one and took as one names Access keeps apart. `CreateTable`
  made a table of two columns named `A` and `a`, and a row then kept one value for the two; it now refuses them, as
  an import does. A call that made such names now throws: a change toward refusing, which writes nothing wrong.
- **A lookup by name finds the name given exactly first** (#63) - `GetTable`, `TryGetTable`, a table's columns
  and indexes, a `Row`'s entries, a join's columns, an export's properties and an import's columns - and only then
  the first name Access takes as one with it. In a file Access made with tables `xµ` and `xμ`, on .NET, either could
  be found for the other, and so dropped, renamed or written to in its place. A row of a table with two columns
  whose names are one, which Access lets a file have, has an entry for each, and an insert writes each value to its
  own column; the foreign keys enforced, a table's long-value maps and its property maps are kept by the exact
  name too. `RenameTable` compares the new name with every other object's, leaving the table out by what it is, not
  by its name, and changes the relationships that name the table and no other's.
- **A new column, index or relationship name in a database of another order** (#63): in a database whose text
  sorts in an order whose keys this library does not make, Access compares such names by that order's keys, and
  takes as one names General - Legacy keeps apart (in Swedish, an index `xʎ` beside `xÜ`). There a new name is
  compared with the others by General - Legacy's keys and without regard to case (`OrdinalIgnoreCase`), and refused
  with `ArgumentException`, before anything is written, only when it is one with another by either: not that
  order's own rule, which this library does not have
  ([Names](https://github.com/markmario/jackcess-dotnet/blob/main/docs/tables-and-schema.md#names)). It was compared
  without regard to case alone.
- **Text with half of a surrogate pair alone in it is read and written as it is** (#63): a name or a value Access
  kept with one was read with U+FFFD in its place, so a table or column of such a name was found by no name, and a
  value read and written back lost it. A new name with one is refused with `NotSupportedException`.
- **Protected files open as Access opens them** (#46). Measured against files ACE 16 and Jet 4.0 made
  (`tools/protected-fixtures.ps1`, whose files the tests now read in place of returning early):
  - **An .mdb's database password is checked**, exactly, as Access checks it: opened without it, or with
    another, an Access 97 or 2000-2003 file that has one throws `InvalidCredentialException`. It opened with any
    password, or none. An Access 97 file's password is compared, as Jet 3 compares it, in the file's code page:
    the password given is converted to it as Windows converts text, a character the code page lacks becoming its
    best fit or a question mark, as the stored one was - Windows' best fit, on every target, where .NET's makes a
    question mark of eleven characters Windows does not.
  - **An encoded .mdb opens** (Access's Encrypt Database, DAO's `dbEncrypt`), with no password, as Access opens
    one, and is read and written: its pages are RC4 under the four bytes its header holds at 0x3E, as
    Jackcess-encrypt reads them. None opened before: the key was taken from the password, which the encoding does
    not use.
  - **An encrypted .accdb opened without a password throws `InvalidCredentialException`**; it was
    `NotSupportedException`. A file whose catalog does not read, when its header names no protection, is refused
    as damaged with `NotSupportedException` whatever password is given; with a password it was
    `InvalidCredentialException`. So is an .accdb whose header describes its encryption in a way that does not
    read, opened with its password: it threw `FormatException`, `InvalidDataException` or
    `CryptographicException`, as the damage fell. Its Agile description's sizes - salt, block, key, hash - and its
    spin count, at most 10,000,000, are checked as the file is opened: a block size of -1 threw
    `OverflowException` at the first page read, and a spin count of 2^31 - 1 was spun, for hours.
  - **An .accdb in ECMA Standard Encryption is read and not written**: `IsReadOnly`, and a write is refused with
    `NotSupportedException` before anything is written, as in an Access 97 file. No Access is known to give an
    .accdb this encryption (Access 2007 uses RC4 CryptoAPI, and later versions Agile Encryption), and no file of
    it was found, so what this library wrote to one could not be checked against Access; reading one follows the
    specification and is checked only against a file made by the tests from it. Its key's 50,000 SHA-1 iterations
    are worked out once for the file; they were worked out again for every page read or written.
  - What this library writes to an .mdb with a password, an encoded .mdb and an Agile-encrypted .accdb is read,
    searched, added to and compacted by Access; so is what it writes to jackcess-encrypt's RC4 CryptoAPI and
    non-standard AES files. Access's descriptor for an encrypted database has no data-integrity element, so the
    "stale integrity hash" the README of 1.2.2 warned of does not arise.
- **A file open to be written is kept from every other writer, and is not opened while another has it** (#62).
  This library takes none of Access's locks. The file was shared for writing, so Access opened it to write while a
  `Database` had it open and wrote over pages the `Database` kept track of: rows were lost and a primary key led to
  other rows. It is shared for reading only, as Access shares a database it has open exclusively, but for readers:
  Access opening it to write - shared or exclusively - is refused ("Could not use '...'; file already in use"), and
  so is a second `Database`; Access read-only (ACE's `Mode=Read`, DAO's read-only) and a `Database` opened
  `ReadOnly` open it and read it. **What a caller sees:** a program that opened, to write, a file a `Database` had
  open is refused; and `Database.Open` of a file another program or `Database` has open to write throws
  `DatabaseInUseException` - an `IOException` with a sharing violation's HResult, as the `IOException` it threw was -
  where it was refused only because the file's header was read without sharing writing. Access's lock file (`.ldb`,
  `.laccdb`) is neither made nor read, as Access makes none for a database it opens exclusively (measured with ACE 16,
  DAO 16 and DAO 3.6).
- **`Database.Create` replaces a file only when no one has it open** (#62), readers included: it emptied a file
  Access had open, under Access. `DatabaseInUseException`, the file as it was. The new database has the file to
  itself until it is disposed, as DAO's `CreateDatabase` holds one: no other program or `Database` opens it
  meanwhile, even to read. Opened again with `Database.Open`, it is shared with readers.
- **An Access 97 file opens while Access has it open** (#62). It is only read, and is opened as one opened
  `ReadOnly` is; it was refused, as a file to be written is. DAO and ACE can write to it meanwhile, so it is read as a
  `ReadOnly` database is, its pages read again at every move, whether `ReadOnly` is set or not, and costs what that
  costs: over the ten Access 97 files of the test corpus, every table and index walked 20 times, 612,280 pages read
  where 78,420 were - about 2.8 s where 0.55 s was on .NET 10, 6.9 s where 0.85 s was on .NET Framework (measured by
  the review of #73).
- **A Memo or OLE value that is not where its row's reference says is refused** (#62), with `InvalidDataException`
  saying what does not fit: a page the file does not have or that holds no values, a row its page does not have or
  has deleted, a place shorter than the value, a chain of pieces that ends before it. It was read from whatever the
  place held - a deleted value's bytes, or a shorter value made up with zeros - and two such references were refused
  with `InvalidOperationException`. A reading beside a writer meets one when the writer changes a row between the
  reading of the row and of its value; so does a reading of a damaged file.
- **Room on a table's pages is used again as Access uses it** (#58). A deleted row's bytes stayed on its page, and
  no row was put where a delete, or a row's values moving back from another page, had left room: a row updated back
  and forth past its page's room grew the file by a page every few hundred changes, without end - 13 pages in 3,000
  cycles, where Access's grew by one with each change a session of its own (in one session, by hundreds). This
  library now does what Access was measured to do (DAO 16 and ACE 16, `.mdb`
  and `.accdb`; [Storage](https://github.com/markmario/jackcess-dotnet/blob/main/docs/storage.md#where-a-new-row-goes)
  has the rules): a row goes to the lowest page the table's
  free-space map lists, a page without room for it leaving the map; a new page is listed when it has room for
  another row of the same size; a delete takes the row's bytes off its page and lists the page when it had no room
  for the row before, as a row coming home lists the page it leaves; and a page an update leaves with no row, which
  Access gives back to the file and takes again, is kept for the table, begun again, and listed. It keeps the slots
  of the rows deleted from it in the session, so that a `Row`, a cursor or a savepoint that read one of them finds no
  row there and an update or a delete through it is still refused - until the session ends, so rows inserted and
  deleted on such a page in one session can fill it, and the table then takes a new page. The free-space map was left
  empty, so Access added
  a page for the first row it wrote to a table this library had filled. A table only inserted into, as an export
  is, still takes its rows on its last page, in insertion order; after a delete, or an update that moves a row's
  values to another page and back, a row can go to an earlier page, as in Access, and is read there in the table's
  order. A scan in that order - `Table.Rows`, a cursor - gives no row inserted after it began, whichever page the row
  goes to: one inserted into room a delete left can be on a page the scan has still to come to, as before only a row
  on the last page could. Finding room costs no more as a table grows: the map is read once a session, and a listed
  page tried without room for the row leaves it, so a run of inserts tries at most as many pages as it has inserts
  and the map lists pages. One insert can try every listed page, when none has room for it - a row larger than any
  room a delete left, in a table with rows deleted all over it - and takes each off the map, as Access does: with
  2,000 pages listed, 3,012 pages read and 1,008 written for that one row.
- **A long value's chain goes to pages an earlier session emptied** (#58). Only a value of one chunk looked at the
  column's free-space map in a new session, so a Memo or OLE value made longer and shorter, a session to each
  change, grew the file by two pages each time it became a chain - 602 pages in 300 such changes, where Access's
  grew by 3. It grows by a page at most.
- **A change a damaged index cannot take is refused, as Access refuses it** (#81). When an index, taken at its word,
  does not hold a row's entry under the row's key - an entry under another key, or none, as an earlier version of
  this library or another program can leave - an update that changes the row's key in that index, and a delete of
  the row, throw `NotSupportedException` before anything is written. Access refuses both on such a file ("The search
  key was not found in any record") and changes nothing; it makes an update of the row's other columns, and so does
  this library. The update was made: the row's new entry went in beside its wrong one, the row was in the index
  twice, and DAO's compaction copied the row twice and dropped the primary key. With `Database.VerifyIndexes` the
  change is made and mends the index: an update moves the row's entry to its new key, and a delete takes out the
  entry the row had under another key, which it left pointing at no row. In a sound index the check reads no page
  more: the pages it reads are the ones the entry's removal goes down.
- **A Decimal with more places than its column holds is cut, as ACE cuts it** (#80). The places past the column's
  scale are cut off toward zero, where they were rounded half away from zero: 1.015 is 1.01 in a column of scale 2
  (it was 1.02), and 2.5 is 2 in a column of scale 0 (it was 3). A negative number cut to nothing is a negative zero,
  as ACE stores it (-0.001 at scale 2); a zero given, of either sign, has no sign, as in ACE. ACE 16 was measured
  for 5,080 numbers - typed Decimal, Double, Single and text parameters, every scale from 0 to 28, both signs, in an
  .mdb and an .accdb - and cut every one (`tools/ace-decimal-places.ps1`, `Assets/ace-decimal-places.tsv`). DAO
  refuses every one of them ("Scaling of decimal value resulted in data truncation"); ACE is the referee, as it is
  for every value this library writes. An insert, an update, `Validate` and the relationships an insert or update
  is checked against all take the number as the row stores it. A lookup is unchanged: it compares the number given
  with the stored value exactly, as ACE's `=` does, so 1.015 finds no row of 1.01. A negative zero is zero to it, as
  to ACE's `=` without an index; ACE's index tells -0.00 from 0.00, and `WHERE D = 0` through one finds only the row of
  0.00. The importer is unchanged too: it takes a value only into a column that holds it exactly, and refuses this
  one, as DAO does.
- **A `bool`, text and a date are taken for a number as Access takes them, by every number type alike** (#75).
  Measured with ACE 16 (typed parameters) and DAO (`AddNew`) for Byte, Integer, Long Integer, Large Number, Single,
  Double, Currency and Decimal (`tools/ace-number-kinds.ps1`, `Assets/ace-number-kinds.tsv`):
  - **`true` is -1**, Access's value, and `false` 0. It was 1. A Byte, which holds no -1, refuses `true` with
    `OverflowException`, as it refuses -1 and as DAO refuses it; ACE wraps every negative number into a Byte (-1 is
    255, -3 is 253), which this library does not.
  - **Text is read in the current culture**, as before and as Access reads it by the machine's settings, and now in
    every form Access reads: a currency symbol ("$5" is 5 where the culture's symbol is "$"), parentheses for a
    negative number ("(5)" is -5), an exponent ("1e2" is 100), group separators and spaces around it. A whole-number
    column takes "2.5" as 2 and "1.5" as 2, half to even, as Access does; it refused them (`FormatException`). Text
    is read as a `double` for a Single or a Double column and as a `decimal` for any other. Text that is no number
    is refused with `FormatException`, naming the column, as Access refuses it ("Data type mismatch").
  - **A `DateTime` is its day number**, days since 30 December 1899, as Access and Java Jackcess take one: 2 January
    2020 12:00 is 43832.5 in a Double, a Single, a Currency or a Decimal, and 43832 in a Long Integer or a Large
    Number; an Integer and a Byte refuse it with `OverflowException`, as Access does (ACE wraps it into a Byte).
  - **A number past what a Single holds is refused** with `OverflowException`, naming the column, as ACE refuses it
    ("Overflow", measured for a Double and a text parameter): a finite number whose nearest Single is not finite, such
    as 1e39 or 3.5e38, given as a number or as text. A `double` was stored as an infinity on every runtime, and text
    on .NET; .NET Framework's parse refused text.
  - A range or the closest row looked for through an index of a number column takes these values the same way. A
    lookup for one row by its value still takes text and a `bool` for no number, as before. The importer keeps its
    own rule: it takes only a value a column holds exactly, text read in the invariant culture, and no `bool`.

### Breaking changes to the public API

Code written against 1.2.2 does not compile against this without the changes below. Nothing a file holds
changes with them.

- **`JetVersion` is `FileFormat`**, its members named for the Access version: `Jet3`, `Jet4`, `Jet12`, `Jet14`,
  `Jet16`, `Jet17` are `Access97`, `Access2000`, `Access2007`, `Access2010`, `Access2016`, `Access2019`.
  `Database.Create(path)` makes an Access 2000 file.
- **`Database.Open(path, password)` and `Open(path, version)` are `Open(path, DatabaseOpenOptions)`**, with
  `Password` and `FallbackFormat`.
- **`Database.ListTables(bool)` is `GetTableNames()` and `GetSystemTableNames()`.**
- **`Database.GetTable` throws `ArgumentException`** for a table that is not there (it threw
  `InvalidOperationException`), and returns the same `Table` for a table for as long as the database is open.
- **`Database.EnforceForeignKeys` is `true` by default** (it was `false`), as in Jackcess.
- **`Table.ReadAllRows()` is `Table.Rows`**, read as it is enumerated; `Rows.ToList()` gives the list.
- **`Table.NewCursor()` and `NewIndexCursor(name)` are `CreateCursor()`, `CreateIndexCursor()` and
  `CreateIndexCursor(name)`.** The name is an index's name and nothing else: one that is no index of the table
  throws `ArgumentException` (the cursor read the table instead), and the name of the primary key's column no
  longer stands for the primary key. `IndexCursor.FindRowByPrimaryKey` is `Table.FindRowByPrimaryKey`, which
  takes a key of several columns too.
- **A cursor moves with `MoveNext()` and `MovePrevious()` and has `Current`**, in place of `GetNextRow()`,
  `GetPreviousRow()` and `GetCurrentRow()`; `BeforeFirst()` and `AfterLast()` are `MoveBeforeFirst()` and
  `MoveAfterLast()`.
- **`Table.UpdateByPrimaryKey(key, values)` and `Table.DeleteRow(column, value)` are removed:
  `Table.Update(row)` and `Table.Delete(row)`** take a row that was read from the table - by `Rows`, a cursor
  or `FindRowByPrimaryKey` - and find it by where it was read (`Row.Id`), so a table with a key of several
  columns, or with none, is updated and deleted like any other (#34). `Update` assigns the columns the `Row`
  has been given a value in since it was read, as Access's `Edit` and `Update` do: each is held to its column's
  Required and Allow Zero Length even when the value is the one the row already has, as Access holds it, and is
  written when it is not what the file holds now. A row that was not read from the table is refused with
  `ArgumentException`, and one that has been deleted since with `InvalidOperationException`.
- **A row keeps its place when it is updated** (#29), as Access keeps it: rewritten in its slot when it still
  fits its page, and otherwise moved to another page with its slot left pointing there (an overflow row), to
  come home when it fits again. An update used to delete the row and add it again at the end of the table, so
  its address and its place in the table's order changed. A row's id is now the same for as long as the row is
  there, rows can be updated while they are gone through, and an update is taken back whole if writing it
  fails.
- **An index cursor is a cursor over its index** (#43, #29). `CreateIndexCursor()` throws
  `InvalidOperationException` for a table with no primary key (it made a cursor that read the table), and
  `IndexCursor.Index` is never null. Its rows come in the index's order (they came in the table's). Every
  lookup goes through the cursor's own index: `FindRowByEntry` is `FindRowByKey`, which with more values than the
  index has columns throws `ArgumentException` (it looked in another index, or in the table's leading columns),
  and with fewer finds the first row with those leading values. `FindRow(column, value)` is `Cursor`'s, finds the
  first row in the cursor's order, and throws `ArgumentException` for a column the table does not have (it returned null).
  A lookup that finds a row leaves the cursor on it, and one that finds none leaves the cursor where it was;
  a row that cannot be read is an `InvalidDataException`, not "no such row". `CreateIndexCursor(null)` needs
  a cast, `(string)null`, now that there is an overload taking an `IndexRange`. Rows can be inserted, updated
  and deleted while an index cursor goes through them: going one way it gives each row once, wherever updates
  move rows in its index meanwhile, and a cursor that turns back begins again from where it is. A row it went
  past without looking at it - outside its range, left out by a range's date, looked past by
  `FindClosestRowByKey` - is given once an update moves it ahead of the cursor. A savepoint keeps what the
  cursor had come to, as rows move. What a cursor holds of that stays a few stretches of its index however many
  rows it moves, and is told only once a change is in the file for good: a batch taken back tells it nothing.
- **A Date/Time is matched by the number a row stores when a number is given** (#35): a `double` finds the row
  that stores exactly it, and `Cursor.GetCurrentRowDateNumber` gives a row's. A `DateTime` - taken first to
  what it reads as once stored, to the millisecond, so that `DateTime.Now` finds the row written with it - finds
  every row that reads as it, through an index as by reading the table: a lookup through an index used to look
  for `DateTime.ToOADate()` alone, and missed the dates Access computes to another number. In a range's key,
  in whichever place, and given to `FindClosestRowByKey`, a `DateTime` is put against a row by the date the
  row's number reads as, a time of 30 December 1899 stored as a negative number among them; Access's `BETWEEN`
  goes by the numbers, as a `double` in a range does.
- **Text is found as its column's sort order has it**, which is Access's `=`: without regard to case or to
  spaces at the end, by a cursor over a table and a cursor over an index alike. It was matched character for
  character once found, and by a table cursor a number-like text matched any text that was the same number
  (`"007"` found `"7"`). Text is not read as a number, nor a number as text, except that a number looked for
  in a text column is the text that writes it.
- **A table or column that is not there is an `ArgumentException`** in `CreateRelationship`, `AddIndex` and
  `CreateTable` (an index's column), and so is a primary key of more than ten columns; they threw
  `InvalidOperationException`. A name that is taken stays an `InvalidOperationException` where it was one:
  `CreateTable`, an import under a table's name, a relationship's name.
- **`DatabaseBuilder`, `CursorBuilder`, `TableBuilder` and `ColumnBuilder` are removed.** A column is described
  by a `ColumnDefinition` - `new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 }` - and
  `Database.CreateTable` and `Table.AddColumn` take definitions; `CreateTable`'s primary key is
  `params string[]`. What the builder refused with `InvalidOperationException` as it was built, `CreateTable`
  refuses with `ArgumentException`, as it does a primary key column that is not among the columns.
- **`Row` no longer derives from `Dictionary<string, object?>`.** It implements `IDictionary` and
  `IReadOnlyDictionary`, so the indexer, initialisers and `TryGetValue` are as they were; `Row.Set` and
  `Row.FromValues` are removed, and `GetValue<T>` and `IsNull` added. **A row that is read has an entry for
  every column, `null` for a column with no value** (it had no entry for one), so `ContainsKey` no longer says
  whether a value is null.
- **A `Row` a caller makes holds each value under the name given, exactly** (#63). This compiles unchanged; it is
  here because a row given one column's value by two names does what it did not. The indexer's set, `Add` and
  `Remove` go by the exact name alone, so `row["Name"]` and `row["NAME"]` are two entries; a value is still read by
  a name in another case. A name finds its column where the row meets a table: `Insert`, `InsertRange`, `Update`
  and `Validate` give each name the column of exactly that name, and otherwise the one a lookup of it finds, and
  refuse two names that go to one column with `ArgumentException`, naming both and the column, before anything is
  written. Such a row kept the value set last: `new Row { ["Id"] = 1, ["ID"] = 2 }` inserted 2, and now throws. An
  AutoNumber is given back under the row's name for its column, by `InsertRange` and `BulkInsert` too. A row read
  from a table sends a name of another spelling to the column a lookup of it finds in the table, as an insert does,
  whichever columns it was read with.
- **`DataType` members are renamed** (their values are not): `Int` is `Int16`, `Long` is `Int32`, `BigInt` is
  `Int64`, `Float` is `Single`, `Numeric` is `Decimal`, `ShortDateTime` is `DateTime`, `Money` is `Currency`,
  `Ole` is `OleObject`, `ExtendedDateTime` is `DateTimeExtended`.
- **`Column.Length` is `LengthInBytes`** (with `MaxLength` in characters for Text beside it) and
  **`ColumnNumber` is `Ordinal`**. `Table.Properties` is the table's own `PropertyMap` and a column's are
  `Column.Properties`; `PropertyMaps` is internal, `PropertyMap` is the collection of its properties, and
  `PropertyMap.Property` is `Property`, a class of its own, not a record.
- **`Index.IgnoresNulls` is `IgnoreNulls`;** `IndexDefinition.DescendingColumns` is a collection to add to.
- **`Relationship.FromTable`, `ToTable` and `ColumnPairs` are `FromTableName`, `ToTableName`,
  `FromColumnNames` and `ToColumnNames`;** `CascadeNull` is `CascadeNullOnDelete`; `LeftOuterJoin` and
  `RightOuterJoin` are `JoinType`. `RelationshipDefinition` has the same names, takes each side's columns as a
  list, and `RelationshipJoinType` is `JoinType`.
- **An import takes its source alone, or its source and an `ImportOptions`.** `ImportTable` and `ImportTables`
  have no optional parameters: `ImportOptions.TableName` and `ImportOptions.PrimaryKey` are in place of
  `tableName` and `primaryKey`. `PrimaryKey` is the key's columns, so a key of several can be asked for, and one
  with no names asks for a table without a key. `ImportTables` refuses options that set either, with
  `ArgumentException`: they describe one table. The properties of `ImportOptions` are set, not `init`, and
  `ImportOptions.Default` is removed. `CreateFromDataTable`, `CreateFromDataSet` and `CreateFromCollection` are
  removed: `Database.Create(path)` and then `ImportTable`.
- **`ExportToDataTable(name)`, `ExportToDataSet()` and `ExportToCollection<T>(name)` are `Table.ToDataTable()`,
  `Database.ToDataSet()` and `Table.ReadRows<T>()`.** `ToDataSet` reads the database's own tables; a system
  table is read with `GetTable(name).ToDataTable()`.
- **`Database.FindDecimalsBeyondPrecision()` and `Table.FindDecimalsBeyondPrecision()` are
  `DecimalByteOrder.Find(database)` and `DecimalByteOrder.Find(table)`,** in the namespace
  `JackcessDotNet.Diagnostics`, where `DecimalBeyondPrecision` is too.
- **A Binary value longer than its column throws `ColumnValueTooLongException`** (#45). 1.2.2 wrote it whole;
  since then a column kept among the fixed-length values threw `ArgumentOutOfRangeException` for one. Both are
  `ArgumentException`s: code that caught `ArgumentOutOfRangeException` for it catches `ColumnValueTooLongException`.
- **A Currency row an earlier version wrote is found by the value it holds, and not always by the number it was
  written from** (#65). This compiles unchanged; it is here because code that finds rows by the numbers it wrote
  them with can come to another row. 1.2.2, and this library until #65, wrote a Currency rounded half away from
  zero, after `Convert.ToDecimal`, which on .NET Framework and .NET up to 10 keeps 15 significant digits of a
  `double` and 7 of a `float`; a number is now taken as Access takes it (above). A file such a version wrote keeps
  the values it was written with, and they are consistent with one another and with Access: nothing in the file
  needs mending. The numbers whose Currency is not what it was:
  - a `decimal` at a half of the fifth place: 1.23465m was written as 1.2347, and is now taken as 1.2346;
  - a `double` or a `float` that is an exact binary half of the fifth place - an odd number of thirty-seconds, such
    as 0.03125, written as 0.0313 and now taken as 0.0312;
  - on .NET Framework and .NET up to 10, a `double` whose 15-digit form is such a half (0.00015, written as 0.0002
    and now 0.0001; 1.234549999999999, written as 1.2346 and now 1.2345), and a `float` of more than 7 significant
    digits (98765432f, written as 98765430 and now 98765432).

  For a row written from such a number, a lookup by the number - `FindRowByPrimaryKey`, an index cursor's
  `FindRowByKey` and `FindRowsByKey`, a cursor's `FindRow` - a range of it and `FindClosestRowByKey` go by the
  Currency the number makes now, and come to the neighbouring row or to none. In a file an earlier version wrote,
  `FindRowByPrimaryKey(1.23465m)` finds the row of 1.2346, not the row of 1.2347 written for 1.23465m, and a
  `Delete` of what it finds deletes that row, and with a cascade its children; a range of 0.03125 alone, of 0.00015,
  of 1.234549999999999 or of 922337203685477.58065m misses the row written from it. A relationship takes the
  number the same way: a child given 1.23465m is a child of 1.2346, and one given 0.03125 is refused though its
  parent was written from 0.03125 (it holds 0.0313). A unique index refuses the number when its neighbour holds what
  it now makes, as 1.23465m beside the row of 1.234549999999999. **Find such rows by the value stored - as a row
  reads it back - and not by the number they were written from.**
- **A value of a kind no number is made of, given for a number column, throws `ArgumentException`** naming the
  column, in every number type (#75): a `Guid`, bytes, a `char`, a `TimeSpan` or any other. It threw
  `InvalidCastException` from `Convert` in every number column but Currency. `ArgumentException` is the engine's
  exception for a caller's bad argument; code that caught `InvalidCastException` for it catches `ArgumentException`.
  `Insert`, `InsertRange` (inside its `BatchInsertException`), `Update`, a cursor's `UpdateCurrentRow` and
  `Validate` all throw it, before anything is written. With it, what a number column makes of other values changes
  (above): `true` is -1 where it was 1, so a row written with `true` holds -1 and a Byte refuses `true` with
  `OverflowException`; a `DateTime` is its day number where it was refused; and text such as "$5", "(5)" or "2.5"
  for a whole-number column is a number where it was refused with `FormatException`.
- **A row an earlier version left out of an index cannot be deleted, nor its key in that index changed, unless the
  database is opened with `Database.VerifyIndexes`** (#81). Versions up to 1.2.2 kept a table's primary key and no
  other index, so a file they wrote rows into can hold rows with no entry in an index. Such a delete or key change
  went through, and now throws `NotSupportedException` before anything is written, as Access refuses it ("The
  search key was not found in any record"); its message says that `VerifyIndexes` makes the change and mends the
  index. Compacting the file in Access rebuilds its indexes, after which nothing is refused.

### Added

- **A value with ー, 々, ゝ or the shadda is a key in General - Legacy** (#156). The twelve marks Access keys by the
  character before them - ー and ｰ, 々, ゝ ゞ, ヽ ヾ, 〱 〲 and the shadda with its two presentation forms - are keyed
  as Access keys them: as that character, or after a kana as its vowel, with the mark's own accent; FF FF with
  nothing before it or after a character Access sorts as several letters; and after a kana a code of its own among
  the codes Access adds for kana. So a row with データ, コンピューター, 佐々木 or an Arabic name with a shadda in an
  indexed Text or Memo column is written, found through the index, updated and deleted, where it was refused with
  `NotSupportedException`; an index is built over such values; and a unique index takes aー and aa as one, as Access
  does. Measured with ACE 16 (`tools/ace-mark-keys.ps1`): each mark after every character of the BMP, every
  character after a mark, and some 6,000 values more - some 960,000 keys, each the one this library makes. In
  General such a value is still refused, and a table or relationship is still not given a name with a mark its
  order keys so ([Refused](docs/indexes.md#refused)). A file that version 1.2.2 or Jackcess wrote such a value
  into holds it under another key: the row is not found through that index, as Access does not find it, where
  the lookup was refused; [`VerifyIndexes`](docs/earlier-versions.md#indexes-that-do-not-hold-every-row) is for
  such a file.
- **Row ids.** `Row.Id` is a `RowId` - the page a row is on and its number there, Jackcess's `RowId` - for a
  row that was read or inserted, and null for one a caller made (#34). A row given to `Insert` or `InsertRange`
  is the row inserted afterwards - one read from a table and inserted as a copy among them - so an update of it
  changes the new row; one that was refused, or whose batch was taken back, is as it came.
- **`Table.InsertRange(rows)`** inserts a batch whole or not at all: when a row is refused the rows before it
  are taken out again and a `BatchInsertException` names the row (`RowIndex`) and carries what refused it. What
  Jackcess does with `addRows` and `BatchUpdateException`, which leaves the rows before it in the table (#29).
- **Cursors** (#29): `UpdateCurrentRow` and `DeleteCurrentRow`, which keep every index and enforced
  relationship as `Table.Update` and `Table.Delete` do, and assign what `Table.Update` assigns - of a row read
  from the table, the columns given a value since; `FindRow` and `FindNextRow`, by one column or by a
  pattern of several; `GetCurrentRowValue` for one column of the current row; `ColumnNames` to read some of the
  columns; `EnumerateBackward`; `IsBeforeFirst` and `IsAfterLast`; `GetSavepoint` and `RestoreSavepoint`.
- **Index cursors** (#29): a cursor over the rows between two keys of an index
  (`CreateIndexCursor(name, new IndexRange { ... })`), `FindClosestRowByKey` for the first row at or after
  a key, and `FindRowsByKey` for every row of a key - for every type an index is kept for, and refused
  with `NotSupportedException` for a value no key is made for.
- **Indexes built once, page after page** (#82). `Table.InsertRange` into a table with no rows, and the new
  `Table.BulkInsert`, put the rows in without index entries and then build each index from the table's rows:
  sorted, and written leaf after leaf as Access builds an index when it compacts a file. `Table.AddIndex` builds
  its tree the same way. Keeping every index row by row cost an export of 13 rows a document into a template with
  35 indexes 11.5 ms a document; loaded so it costs 0.6 ms, against 0.46 ms for the same rows with no index. A
  duplicate in a unique index or primary key is refused when the trees are built, naming the row row by row would
  name - the earliest that duplicates one before it - and nothing written; a row refused for something else is named
  ahead of it. A failure while the trees are built that is no row's is thrown as itself, an `IOException`. While a batch is going in the table's indexes hold none of its rows, so a sequence
  of rows that looks in the table through an index does not find them; a table that has rows, a table in an
  enforced relationship and one with an index cursor open are filled row by row as before. Keys past 64 MB are
  sorted in runs in temporary files; the last entry of each page written is held for the levels above, about 75
  bytes a row with 510-byte keys (measured live: 34 MB at 50,000 rows, 91 MB at 450,000).
- **`Table.BulkInsert(rows)`** (#82) inserts rows all or none, as `InsertRange` does, going through them once and
  holding none of them, for loading many rows. A refused batch leaves the rows inserted before the refusal
  holding the AutoNumber values they were given; `InsertRange`, which holds every row, gives them back as they
  came.
- **`Table.FindDuplicates(rows)`** (#82) names every set of rows a unique index or primary key would refuse as
  duplicates, of one another or of a row already in the table, by the index's key as Access compares them: text
  that differs only in case, in trailing spaces, or in a character Access sorts as another (a full-width letter,
  "ß" and "ss", "æ" and "ae") is one key. `DuplicateRows` gives the index, the rows' places, and whether a row of
  the table has the key. `Validate` looks at one row against the table; this looks at a batch.
- **`Joiner`** finds the rows of one table that go with a row of another through an index, either way (#29).
- **A primary key for a table that has rows**: `Table.AddIndex` with `IndexDefinition.IsPrimaryKey` (#37).
- **Dropping and renaming** (#37): `Table.DropColumn`, `DropIndex`, `RenameColumn` and `RenameIndex`;
  `Database.DropRelationship`, `DropTable` and `RenameTable`. Jackcess has none of them: each is written as
  Access was measured to write it, and what Access refuses is refused.
- **`RelationshipDefinition.IsOneToOne`** (#37): a relationship is marked one to one, or left unmarked, as the
  caller says, where it was marked only when both sides were unique. Marked, its foreign key's index is a
  unique one, built over the rows.
- **Properties are written** (#27). `PropertyMap.Set`, `Remove` and `Save` change the properties of a table
  (`Table.Properties`) and of a column (`Column.Properties`), and `TryGetProperty` gives a property's type and
  whether it is a DDL property. `Database.Properties`, `SummaryProperties` and `UserDefinedProperties`
  are the database's own, read and written the same way. A property Access wrote and the caller did not change
  goes back byte for byte.
- **`DatabaseOpenOptions.ReadOnly`** (#32), Java's `DatabaseBuilder.withReadOnly`: the file is opened only to be
  read, by a handle that cannot write, and every change is refused with `NotSupportedException` before anything
  happens (`Database.IsReadOnly`). It opens beside Access, or a `Database`, writing to the file, as Access's own
  read-only open does; only a program holding the file exclusively keeps it out. A file marked read-only opens this
  way. What it reads while another program writes is the file as it is at each move of a cursor, and at each row of
  `Table.Rows`: it keeps no page from one move to the next, as the file can change between them, so a walk of a table
  reads a page or more at every row - ten to thirty times the pages one opened to be written reads, which keeps the
  page it is on. `Table.Rows` over 500,000 rows reads 550,002 pages where one opened to be written reads 53,694:
  about 1.7 s where 0.3 s is on .NET 10, 2.1 s where 0.5 s is on .NET Framework (measured by the review of #73). An
  Access 97 file is read so too. Such a reading can miss rows, give a row twice - one moved ahead of a cursor, or one
  on a page read while the writer was part-way through writing it - or with a Memo or OLE value of another row's, or
  fail. It cannot change the file, and a writer neither waits for it nor is kept out by it; it keeps out only what
  needs the file to itself - Access opening the database exclusively, and so Compact and Repair, and
  `Database.Create` replacing the file. A reading that agrees with itself needs no writer while it is made.
  [Sharing a file with Access](https://github.com/markmario/jackcess-dotnet/blob/main/docs/sharing.md) has what was
  measured.
- **`DatabaseOpenOptions.KeepWritersOut`** (#84): a file opened only to be read keeps every writer out while it is
  open, sharing the file for reading only, as Access holds a database it opens exclusively and read-only (DAO's
  `OpenDatabase` with both set, ACE's `Mode=Share Deny Write`). Measured with ACE 16 and DAO 16, in an .mdb and an
  .accdb: while it is open, DAO shared or exclusive and ACE exclusive are refused ("file already in use"), ACE's
  default mode opens the database read-only and cannot write, and Access read-only opens it; the file is the same
  bytes after. On Windows no one can write meanwhile, so it reads as a database opened to be written does, keeping
  the page a cursor is on from one move to the next: over 500,000 rows, `Table.Rows` reads 61,109 pages where `ReadOnly` alone
  reads 550,002, and an index cursor 61,941 where it reads 2,050,834 - the pages one opened to be written reads. A
  file a writer has open is refused with `DatabaseInUseException`, unchanged; open it again with `ReadOnly` alone to
  read it beside the writer. Elsewhere than Windows .NET's advisory locks do not keep a writer out, so there it reads
  as `ReadOnly` alone does, its pages again at every move. It needs `ReadOnly`: set without it,
  `ArgumentException`, before the file is opened, in every format; an Access 97 file opened with both keeps writers
  out. `ReadOnly` alone, and an Access 97 file opened without either, are as they were.
- **`DatabaseOpenOptions.MaxFileLength`** (#34): the longest, in bytes, a database's file may grow to while it is
  open, below the 2 GB an Access file can be. A change that would pass it is refused with `DatabaseFullException`,
  whose `MaxLength` is the value given, and taken back, as at 2 GB; a value under the file's length when it is opened
  refuses every change to it, as a file past 2 GB is refused. A value that is not positive, or is past 2 GB, is an
  `ArgumentOutOfRangeException` from `Database.Open`, before the file is opened. So a caller's tests reach the limit
  without writing 2 GB.
- **`Table.Validate(row, checkRelationships)`** (#34): `false` makes every check `Validate(row)` makes but the
  relationships, for a batch whose rows refer to each other - parents and children - checked whole before any of it
  is inserted. Each row's insert still checks its relationships, so a child whose parent is never inserted is
  refused when it is inserted. `Validate(row)` is `Validate(row, true)`.
- **`DatabaseInUseException`** (#62), an `IOException`: the file is open in another program or `Database` in a way
  that does not let this one in - open to write, when this one would write; open exclusively, when it would read.
- `NOTICE`, crediting the works this is derived from, in the repository and the package.
- Releases from `vX.Y.Z` tags, checked before anything is built and published through nuget.org Trusted
  Publishing (`.github/workflows/publish.yml`). Upstream's tag-triggered `nuget-publish.yml` is retired.
- XML documentation in the package, `JackcessDotNet.xml` beside each assembly, so an editor shows it. Every
  public type and member has it, and the build fails on one that does not (CS1591).

### Fixed

- **An Access 97 Text column is as long as Access says, and its file exports to a `DataSet`** (#85). Access 97
  keeps text a byte a character in the database's code page, and the engine took a Text column's length as two
  bytes a character, as in later formats: `Column.MaxLength` of a Text(255) was 127, and `ToDataSet` and
  `ToDataTable` gave its `DataColumn` that `MaxLength` and failed on a longer value ("violates the MaxLength limit"),
  as on `access97-jet40.mdb` and the corpus's `indexCodesV1997.mdb`. It is 255, the Size 32-bit DAO 3.6 reports;
  the two agree for all 175 Text columns of the corpus's Access 97 files and that one (`tools/access97-text-sizes.ps1`).
- **A lookup of a date through a Date/Time index ends in a 32-bit .NET Framework process** (#98). The stored numbers
  a date's lookup finds were worked out by halving the distance between a number that reads as the date and one that
  does not, until the middle came out equal to an end. Where something in the process had left the x87 unit computing
  with 64 bits of precision - ACE's OLE DB provider leaves the thread that ran a statement so (measured with Microsoft
  365's ACE, 32-bit: 53 bits before an INSERT, 64 after it) - the middle of two neighbouring doubles was a number
  between them, and the halving never ended: FindRowByKey, a range or a join on a Date/Time spun for ever. The
  halving is now of the doubles' places in order, integers, and ends within 64 steps on every runtime. The tests run
  in a 32-bit process too (-p:TestX86=true, and a CI job).
- **A Date/Time is read and written alike whatever the state of the x87 unit** (#100). `DateTime.FromOADate` and
  `ToOADate`, which the engine read and wrote a Date/Time with, compute in doubles. In a 32-bit .NET Framework
  process with the unit at 64 bits of precision, as ACE leaves the thread that ran a statement (measured with 32-bit
  ACE 12), of 15 million stored numbers chosen either side of a millisecond's half 241 read as the millisecond
  beside the one a 64-bit process reads (5.7870370370370361E-09 as midnight on 30 December 1899, not 00:00:00.001),
  and a lookup by that date missed the row; of 3.4 million dates 783 were written, in the row and in the index key,
  as the double beside. At 53 bits nothing differed. The engine converts in integers, giving exactly what a 64-bit
  process gives: the same conversions agree in all three states, and a table of 20,000 rows is written byte for byte
  as a 64-bit process writes it. A double or a float to a Decimal, a double to a Currency, and a decimal to a Double
  or a Single were measured alike and do not depend on the unit. A 64-bit process, whose runtime computes doubles
  with SSE2 and not the x87 unit, converts with the runtime's own, which gives the same results over millions of
  inputs in every configuration the tests run in; any other process converts with the integers, which in a 32-bit
  .NET Framework process take 0.31 to 0.36 of the time they first took (#125). A scan of 200,000 rows of four
  Date/Time columns on .NET 10 takes what it took with the runtime's conversion.
- **Text given for a Double or a Single column is stored as Access stores it, on every target** (#124). It was read
  by the runtime's parse, which stored another number on each kind of target: .NET rounded every digit, and a
  Single through no double; .NET Framework took some text to one double in a 64-bit process and another in a 32-bit
  one, neither of them always Access's, and a Single through its double (`-8.5635493928557935E15` was
  -8563549392855793 in a 64-bit process, -8563549392855794 elsewhere). Access reads the first 20 significant digits
  of text for a Double and the first 12 for a Single and stores the nearest number to them, a tie to the even
  significand; this library now reads text so on every target, in the forms and the culture it read before. ACE 16
  (text literals and text parameters in an `INSERT`) and DAO (`AddNew`, and a property's value) were measured for
  13,276 texts, most at and next to the half between two doubles, or two singles, of every range, and stored the
  same (`tools/ace-text-to-float.ps1`; a sample is `Assets/ace-text-to-float.tsv`). Of those texts, .NET's parse
  stored a double other than ACE's for 1,025 and a single for 1,793; .NET Framework's a double for 1,620 in a 64-bit
  process and 402 in a 32-bit one, and a single for 1,882. In two ranges of the least doubles, from 1E-308 to 1E-304
  and under 1E-321, ACE stored 73 of the texts one place from that double; this library does not follow it there
  (#136). Text of `-0` is -0.0, as in ACE, where .NET Framework's parse gave 0. A Double or Single property given
  text takes it the same way, in the invariant culture as before. A number given as a `double`, a `float`, a
  `decimal` or an integer does not go this way, and is stored as before.
- **On Linux, a file in use is refused with `DatabaseInUseException`** (#122). There .NET refuses an open by the
  advisory lock it takes for a handle's sharing, with an `IOException` whose HResult is the errno, 11, and not
  Windows' sharing violation: `Database.Create` beside any open handle, and any open beside Access holding the file
  exclusively or beside a database just created, threw that `IOException` as it was. They are
  `DatabaseInUseException`, with the sharing violation's HResult, as on Windows. What Linux keeps out and what it
  lets in, measured in CI's Linux job: [Sharing a file with Access](docs/sharing.md#what-each-open-does).
- **`Table.Validate` refuses what `Insert` refuses, with the same exception** (#141, #143). The two share one
  sequence of checks, in the insert's order - the columns' rules, the relationships, the indexes, the row's
  encoding - which `Insert` then writes and `Validate` does not. Before:
  - `Validate` checked the row with an encoder that had none of the table's long-value pages, so it took every Memo
    as kept in the row: it refused a Memo that begins with U+FEFF long enough to be a chain, 1,909 characters or
    more with the mark, which `Insert` writes and Access reads back whole.
  - It checked some values without encoding the row, so it accepted what `Insert` refuses: a row too large for its page
    and an OLE Object value that is not bytes (`InvalidOperationException`); a Byte of 256, an Integer of 32,768
    and an Int32 given `long.MaxValue` (`OverflowException`); text that is not a date in a Date/Time column
    (`InvalidCastException`); and a number for a Guid (`FormatException`). It refuses them now.
  - It did not check relationships: with `EnforceForeignKeys` on, it accepted a row whose foreign key refers to no
    row, which `Insert` refuses with `ConstraintViolationException`. It answers for the table as it is, so it
    refuses a row that refers to one not yet inserted. An import that keeps its rows' numbers, which checks a
    batch before inserting any of it, leaves relationships to each row's insert, so a row may still refer to an
    earlier row of its batch.
  - **Breaking for a caller that checks parents and children together before inserting any of them**, in a
    database whose relationships are enforced: 2.0.0-preview.4 accepted every child of such a batch, and from
    2.0.0-preview.5 `Validate(row)` refuses each child whose parent is in the batch with
    `ConstraintViolationException`, as the parent is not in the file yet. Check such a batch with
    `Validate(row, checkRelationships: false)` (#34), which makes every other check and leaves the relationships
    to each row's insert, as `Validate(row)` did in 2.0.0-preview.4.
  - It encoded before it checked the columns' rules and the indexes, so a row with two faults could be refused
    with another exception than `Insert`'s.

## [1.2.2]

### Added

- **Package icon.** `jackcess.png` (256×256, ~48 KB) wired via
  `<PackageIcon>` so the nuget.org listing renders an icon next to the
  package title.

No library code changes vs. 1.2.1.

## [1.2.1]

### Changed

- **Package metadata.** Added `PackageProjectUrl`, `RepositoryUrl`, and
  `RepositoryType` so the nuget.org package page surfaces the GitHub repo
  under "Project website" and "Source repository".
- **SourceLink.** Added `Microsoft.SourceLink.GitHub` (PrivateAssets="All").
  Consumers can now debug-step from the JackcessDotNet symbols into the
  exact source on GitHub matching the published commit SHA.
- **Deterministic builds in CI.** `ContinuousIntegrationBuild` enabled when
  `GITHUB_ACTIONS=true` so the .nupkg shipped from CI is reproducible.
- **README badges.** Added NuGet (version + downloads), GitHub (author + repo),
  and license badges visible on both GitHub and the nuget.org package page.

No library code changes vs. 1.2.0 — same `.dll` byte-for-byte aside from
embedded SourceLink metadata.

## [1.2.0]

### Added

- **Office Agile Encryption write support** (Office 2010+ `.accdb`). Pages
  encrypt on write using the same AES-CBC + per-page IV scheme as the read
  path, so round-trips through `Database.Open(path, password)` are lossless.
  Note: the DataIntegrity HMAC isn't recomputed — Office Access may flag a
  stale integrity hash on modified files. Matches the upstream `jackcess-encrypt`
  limitation.
- **ECMA Standard Encryption write support** (Office 2007 `.accdb`). AES-ECB
  encrypt on the same per-page key-derivation path as decrypt.
- **RC4 CryptoAPI Encryption** (Office 2002–2003 `.accdb`, `vMinor=2` with
  `FAES_FLAG` clear and `algId=0x6801`). Read + write — RC4 is symmetric.
  Per MS-OFFCRYPTO §2.3.5.2, including the 40-bit-key → 128-bit padding quirk.
- **Non-Standard AES encryption** (compat mode 0). Activates when a file
  advertises RC4 in the flags but actually contains AES in the algorithm ID.
  Falls back automatically with hash iterations set to 0.
- **Multi-column primary keys** via `Database.CreateTable(name, columns,
  primaryKeyColumns: new[] { "A", "B" })`. Up to 10 columns per composite key
  (Jet's hard limit). The composite key encoder writes a single leading
  ascending-flag byte then concatenates per-column value bytes in declared
  order.
- **Foreign-key enforcement on insert** (opt-in). Set
  `Database.EnforceForeignKeys = true`, and `Table.Insert` validates that each
  FK column value references an existing row in the parent table per
  `MSysRelationships`. Restrict-only — no cascade. Null FK values pass
  (SQL semantics).
- **`ImportOptions.AppendIfExists`** for the wrapper. When set, importing to
  a name that already exists in the database appends rows into the existing
  schema instead of throwing. Source columns/properties are matched to target
  columns by name (case-insensitive); unmatched source columns are dropped
  silently, unmatched target columns are left null.
- **`OfficeCryptCodecHandler.Scheme`** accessor — string label that names the
  active scheme (e.g. `"Agile Encryption (Office 2010+)"`,
  `"RC4 CryptoAPI Encryption (RC4-128)"`). Useful for diagnostics.
- **CHANGELOG.md** shipped inside the `.nupkg`.

### Fixed

- **`.accdb` page decryption was producing garbage** for every Office Crypto
  variant. The encoding key at header offset `0x3E` is XOR-masked against
  `BASE_HEADER_MASK` at rest (Jackcess Java applies this de-obfuscation at
  the PageChannel layer, but this port has no such layer). The codec now
  un-masks the encoding key before deriving per-page IVs / per-page RC4 keys.
- **Unencrypted `.accdb` files were rejected** when the bytes at offset
  `0x3E` happened to contain random non-zero data (e.g. the `linkeeTest.accdb`
  fixture from the upstream corpus). The "blank key" check now runs against
  the *un-masked* bytes and short-circuits when the `EncryptionInfo.version`
  field is `0.0`.
- **NonStandard AES dispatch escape**. `BuildStandardOrRc4` previously called
  `StandardEncryptionInfo.Read` *before* dispatching to the RC4-or-NonStandard
  fallback, so a `NotSupportedException` thrown by `ValidateRc4Header`
  escaped past the try/catch ladder. The Read is now deferred into the
  individual sub-paths, each with its own validation.
- **`Database.GetTable` no longer returns `null` PK metadata for composite
  PKs**. The reader rebuilds `PrimaryKeyColumnNames` from the on-disk index
  column block when more than one column is registered.

### Changed

- `OfficeCryptCodecHandler.HashBytes` switched from `HashAlgorithm.TransformBlock`
  + `TransformFinalBlock` to .NET 8's static one-shot methods
  (`SHA512.HashData` etc.). ~10× faster on the 100,000-iteration spin loop
  and removes a class of `HashAlgorithm` state subtleties.
- `OfficeCryptCodecHandler.AgileDescriptor.KeyData` (nested type) renamed to
  `KeyParams` to resolve CS0102 ("property and nested type can't share a name").
  No external behavior change — the type is internal.

## [1.1.0]

### Added

- **Office Crypto codec for `.accdb`** — read-only support for:
  - **Agile Encryption** (Office 2010+, `vMajor=4, vMinor=4`). MS-OFFCRYPTO
    §2.3.4.10–13 with PBKDF + AES-CBC and configurable hash. Verifies the
    password before returning a codec; throws `UnauthorizedAccessException`
    on mismatch rather than feeding garbage to the row parser.
  - **ECMA Standard Encryption** (Office 2007, `vMajor∈{2,3,4} / vMinor=2`).
    MS-OFFCRYPTO §2.3.4.5–9 with 50,000-iteration SHA-1 derivation and
    AES-ECB page crypto.

## [1.0.0]

### Added

- Initial release.
- **Read** Jet 3 (Access 97) and Jet 4 (Access 2000–2003) `.mdb` files.
- **Read** ACE 12 / 14 / 16 / 17 (`.accdb`, Access 2007 through 2019).
- **Create** new `.mdb` files at Jet 4 (and Jet 3 — read path only on Jet 3
  schema, write path on Jet 4).
- **Row CRUD**: `Insert`, `UpdateByPrimaryKey`, `DeleteRow`, table-scan cursor,
  index cursor with `FindRowByPrimaryKey` for single-column PKs.
- **B-tree primary-key index** with split-and-promote: root starts as a leaf,
  flips to a 2-level tree on overflow.
- **Memo / OLE long-value** storage via LVAL pages.
- **PropertyMap** reader (table-level + per-column properties from
  `MSysObjects.LvProp`).
- **MSysRelationships** parser exposed via `Database.GetRelationships()`.
- **Password-protected `.mdb`** via the Jet RC4 codec (Access 97 / 2000–2003).
- **`DatabaseImporter`** wrapper: `ImportTable(DataTable)`, `ImportTables(DataSet)`,
  and `ImportTable<T>(IEnumerable<T>)`. Schema inferred from source type;
  POCO reflection honours `[Key]`, `[Column]`, `[MaxLength]`, `[NotMapped]`,
  `[DatabaseGenerated(Identity)]`. `ImportOptions.FallbackUnmappableToString`
  stores unmappable CLR types as Memo via `ToString()`.
- **`DatabaseExporter`** wrapper: `ExportToDataTable(name)`, `ExportToDataSet()`,
  and lazy `ExportToCollection<T>(name?)`.
- Folder-organized source layout (`Codecs/`, `Cursors/`, `Indexes/`, `IO/`,
  `Pages/`, `Properties/`, `Schema/`, `Tables/`, `Util/`).
- NuGet package generated on every build (`GeneratePackageOnBuild`); README
  and LICENSE shipped inside the `.nupkg`; symbols emitted as `.snupkg`.
- GitHub Actions workflow that builds + tests on every push, and pushes to
  nuget.org on `v*` tags.
