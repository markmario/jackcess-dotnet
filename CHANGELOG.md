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
- **Files Access accepts.** Every change listed under "What this fork changes" in the README: files up to the
  format's 2 GB, Access's own text index keys, every index kept on every write, AutoNumbers from the table's
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
  units and a count of 7, its SQL `UPDATE` and DAO's `Edit` the time in whole seconds and a count of 0 - and keys
  each by its bytes, not by the date they say: one date in two forms is two keys, which a unique index and a
  primary key take, and a child whose bytes are another form of its parent's date has no parent (Access refuses its
  insert, and a relationship created over it). This library keyed the date it read, in the first form. So,
  enforcing foreign keys, a parent was deleted from under a child ACE had keyed in the second form, and its entries
  left pointing at no row; a child with a date its parent has in another form was taken; verifying its indexes, it
  refused a row ACE takes beside one in the second form; and an index or a primary key added over rows in another
  form held them under keys ACE does not look under, or refused a primary key over two forms of one date. Now a
  row's key is made of its bytes, in whichever form they are, and a caller's `DateTime` is keyed in the first form,
  which is what Access's `INSERT` writes and this library writes: every entry ACE made over rows in its two forms,
  and in three more it reads a date from but does not write, is the key this library makes. Such an index is
  searched for a relationship's rows and a unique index's duplicates as an index of any other type is. A
  `DateTime` looked for through one - `Table.FindRowByPrimaryKey`, an index cursor's `FindRow`, `FindRowByEntry`
  and `FindRowsByEntry` - is looked for under the key of each form, so a row ACE stored in its second form is
  found, as a cursor over the table finds it (it was not); in a range's entry and given to
  `FindClosestRowByEntry` it is the first form's key, as Access's `BETWEEN` through an index has it. A `Joiner`
  joins a row read from a table on the bytes it stores. A row in the second form is deleted, or its date
  rewritten, for what a Date/Time row costs, its entry found under the key of its bytes: at 5,000 rows 6 pages
  where 86 were read, every leaf of the index. An entry a version of this library since 2930d3f made for a row in
  another form - when it added the index or rewrote the row - is under the key of its date in the first form, or of
  no value, and is looked for there, one way down the tree each, when the row has none under the key of its bytes.

- **A long-value page is listed as having room from 258 bytes**, where it was 264 (#35). ACE, writing OLE
  Object values of chosen lengths, lists a page left with 258 bytes or more and none left with 257 or fewer; the
  sizes between could not be made with text. A value goes onto a page Access left with 258 to 263 bytes where
  Access would put it, and such a page is no longer taken out of the map.

- **Text keys in the orders of nine more locales, and Access 97's text indexes searched** (#23). A database
  made in another locale has that locale's LCID for the sort order of its text. ACE keys the text of Arabic,
  German, Greek, Hebrew, Italian, Dutch, Russian, British English and Portuguese databases exactly as it keys
  General - Legacy - compared over 1,013,302 values each - and tables whose text sorts in one of those orders
  are written to, where they were refused. Every other order is refused as before; the README lists the ones
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
- **An import makes a `long` a Large Number** (#38). A `long` column or property was a Long Integer, and a
  value past 32 bits stopped the import at its row. It is imported into an `.accdb` whole; in an `.mdb`, which
  has no Large Number, the table is refused with `NotSupportedException` before it is made.
- **A Date/Time Extended value Access wrote in whole seconds is read as its date** (#38): the form Access's SQL
  `UPDATE` leaves in a row, which was read as null. Its index key is made of its bytes, as Access makes it (#51).
- **A new table or relationship has its owner's entry in `MSysACEs`**, with the others its container passes
  on, as Access gives them (#37). Without it, Access in an `.mdb` did not read a table this library made in a
  database DAO had created, nor drop a relationship it made.
- **A primary key made with a table is unique and required** (its block's flags `0x89`), and **an index that
  is no relationship's has 4 in its slot's two cascade bytes**, as Access writes both (#37).

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
  lookup goes through the cursor's own index: `FindRowByEntry` with more values than the index has columns
  throws `ArgumentException` (it looked in another index, or in the table's leading columns), and with fewer
  finds the first row with those leading values. `FindRow(column, value)` is `Cursor`'s, finds the first row in
  the cursor's order, and throws `ArgumentException` for a column the table does not have (it returned null).
  A lookup that finds a row leaves the cursor on it, and one that finds none leaves the cursor where it was;
  a row that cannot be read is an `InvalidDataException`, not "no such row". `CreateIndexCursor(null)` needs
  a cast, `(string)null`, now that there is an overload taking an `IndexRange`. Rows can be inserted, updated
  and deleted while an index cursor goes through them: going one way it gives each row once, wherever updates
  move rows in its index meanwhile, and a cursor that turns back begins again from where it is. A row it went
  past without looking at it - outside its range, left out by a range's date, looked past by
  `FindClosestRowByEntry` - is given once an update moves it ahead of the cursor. A savepoint keeps what the
  cursor had come to, as rows move. What a cursor holds of that stays a few stretches of its index however many
  rows it moves, and is told only once a change is in the file for good: a batch taken back tells it nothing.
- **A Date/Time is matched by the number a row stores when a number is given** (#35): a `double` finds the row
  that stores exactly it, and `Cursor.GetCurrentRowDateNumber` gives a row's. A `DateTime` - taken first to
  what it reads as once stored, to the millisecond, so that `DateTime.Now` finds the row written with it - finds
  every row that reads as it, through an index as by reading the table: a lookup through an index used to look
  for `DateTime.ToOADate()` alone, and missed the dates Access computes to another number. In a range's entry,
  in whichever place, and given to `FindClosestRowByEntry`, a `DateTime` is put against a row by the date the
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
- **`DataType` members are renamed** (their values are not): `Int` is `Int16`, `Long` is `Int32`, `BigInt` is
  `Int64`, `Float` is `Single`, `Numeric` is `Decimal`, `ShortDateTime` is `DateTime`, `Money` is `Currency`,
  `Ole` is `OleObject`, `ExtendedDateTime` is `DateTimeExtended`.
- **`Column.Length` is `LengthInBytes`** (with `MaxLength` in characters for Text beside it) and
  **`ColumnNumber` is `Ordinal`**. `Table.Properties` is the table's own `PropertyMap` and a column's are
  `Column.Properties`; `PropertyMaps` is internal, `PropertyMap` is the collection of its properties, and
  `PropertyMap.Property` is a class, not a record.
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

### Added

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
- **Index cursors** (#29): a cursor over the rows between two entries of an index
  (`CreateIndexCursor(name, new IndexRange { ... })`), `FindClosestRowByEntry` for the first row at or after
  an entry, and `FindRowsByEntry` for every row of an entry - for every type an index is kept for, and refused
  with `NotSupportedException` for a value no key is made for.
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
  whether it is a DDL property. `Database.DatabaseProperties`, `SummaryProperties` and `UserDefinedProperties`
  are the database's own, read and written the same way. A property Access wrote and the caller did not change
  goes back byte for byte.
- `NOTICE`, crediting the works this is derived from, in the repository and the package.
- Releases from `vX.Y.Z` tags, checked before anything is built and published through nuget.org Trusted
  Publishing (`.github/workflows/publish.yml`). Upstream's tag-triggered `nuget-publish.yml` is retired.
- XML documentation in the package, `JackcessDotNet.xml` beside each assembly, so an editor shows it. Every
  public type and member has it, and the build fails on one that does not (CS1591).

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
