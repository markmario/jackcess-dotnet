# Differences from Java Jackcess and from upstream

Where this library came from, what it changes in the port it started from, and where it does what Access does
rather than what Java Jackcess does. What changed in the public API since that port's 1.2.2 is in the
[CHANGELOG](../CHANGELOG.md#breaking-changes-to-the-public-api).

## Where this started

This project started from release 1.2.2 of
[`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet) and is now
maintained on its own, with its own package, `Jackcess.Net`. It is not a release of the Jackcess project, and
neither the Jackcess authors nor Mehran Ghanizadeh maintain or endorse it. It differs from that port in two ways:

- **.NET Framework.** The `JackcessDotNet` package on nuget.org ships a `net10.0` assembly and nothing
  else (checked for 2.0.0 and 3.1.0), so nothing on .NET Framework can use it. This one ships `net10.0`,
  `net11.0`, `net48` and `netstandard2.0`. See [Supported frameworks](getting-started.md#supported-frameworks).
- **Files Microsoft Access accepts.** The files this library writes are checked against Access's own engine
  (ACE), not only read back by the library that wrote them. See [What this fork changes](#what-this-fork-changes).

The library is a C# port of the [`spannm/jackcess`](https://github.com/spannm/jackcess) Java
project (a maintained fork of the original [Jackcess](https://jackcess.sourceforge.io/)
by James Ahlborn). The Java project is where this port learned the Jet file format: it tracks the Java
project's behaviour and reuses its test corpus for verification, and where Access and Jackcess differ, it follows
Access ([below](#where-this-library-and-java-jackcess-differ)).

## What this fork changes

Each of these was found by having Access or ACE open a file the engine wrote.

- **Four target frameworks:** `net10.0`, `net11.0`, `net48` and `netstandard2.0`, each of them tested
  ([Supported frameworks](getting-started.md#supported-frameworks)).
- **Files up to the format's own limit.** A table's usage map grows past its first window of pages, as
  Access's does, up to the 2 GB a Jet file can be. Before, a file could not be written past about 2 MB
  ([How a file grows](storage.md#how-a-file-grows)).
- **Rows go where Access puts them, and finding room costs no more as a table grows.** A row goes to the
  lowest page the table's free-space map lists with room for it, as in Access - in a table only inserted into, as
  an export is, its last page, so rows stay in insertion order - and room a delete or an update frees is used
  again as Access uses it ([#58]): after a delete, or an update that moves a row's values to another page and back,
  a row can go to an earlier page. A listed page tried without room for the row leaves the map, so a run of
  inserts tries at most as many pages as it has inserts and the map lists pages, at any size of table; one insert
  can try every listed page, when none has room for it, as Access's can
  ([Where a new row goes](storage.md#where-a-new-row-goes)). A page holds at most 255 rows.
- **A memo or OLE value is kept in its row only up to 64 bytes**, Access's own limit. The engine kept up
  to 1,024 bytes there, and Access showed `#Error` for those values ([Long values](storage.md#long-values)).
- **Text index keys are the ones Access writes**, for the General - Legacy sort order (Access 2000-2007),
  for the orders of nine other locales that have its keys, and for General (Access 2010 and later). An
  Access 97 file's text indexes are searched, in Access 97's General order
  ([Text index keys](indexes.md#text-index-keys-sort-orders-and-limits)).
- **Every index of a table is kept on every write**, not the primary key alone, and unique indexes are
  enforced. Access answers through them after the engine adds, changes or deletes rows
  ([Indexes kept on every write](indexes.md#indexes-kept-on-every-write)).
- **An insert fills AutoNumber columns from the table's own counter**, as Access does.
- **Foreign keys are enforced** on insert, update and delete, with cascades (`EnforceForeignKeys`,
  on by default) ([Relationships and foreign keys](relationships.md)).
- **Rows and long values are laid out as Access lays them out**, so Access reads back every value the
  engine writes, a long memo in several pieces included ([Storage](storage.md)).
- **A Currency is the number Access makes of a caller's**: its exact value to four places, half to even, for a
  row, its keys and every lookup, on every runtime. The engine rounded half away from zero, after taking a `double`
  through a `decimal` of 15 significant digits and a `float` through one of 7 on .NET Framework and .NET up to 10,
  so a row was not found by the number it was written with. A file it wrote keeps those values: see
  [Currency](data-types.md#currency).
- **A Decimal with more places than its column holds is stored as ACE stores it**, cut toward zero, where the
  engine rounded half away from zero ([Decimal](data-types.md#decimal)).
- **A `bool`, text and a date for a number column are what Access stores for them**, `true` as -1, in every number
  type, and text for a Double or a Single the same number on every target
  ([A bool, text or a date for a number](data-types.md#a-bool-text-or-a-date-for-a-number)).
- **Tables the engine creates are ones Access lists, reads, seeks in and adds to.**
- **Two names are one as Access takes them**: by their text index keys, in the order Access compares names by,
  measured with DAO on 18,140 pairs. A name one with another of its kind is refused, and a lookup finds the name
  given exactly first. The engine took two names as one when they were equal without regard to case
  ([Names](tables-and-schema.md#names)).
- **Properties are written**, a table's, a column's and the database's, with what Access wrote left byte for
  byte as it was; **a column's Required and Allow Zero Length are read, written and held to**, as Access holds
  rows to them; and **Access generates the GUID** of a Replication ID AutoNumber the engine made. See
  [Properties](properties.md).
- **`.accdb` files are created**, in the Access 2007, 2010, 2016 and 2019 formats, where every version asked
  for made an Access 2000 `.mdb`, from empty databases DAO made ([Creating a file](file-formats.md#creating-a-file)).
- **Large Number and Date/Time Extended columns** (Access 2016 and 2019) are read, written, created and
  indexed ([Large Number and Date/Time Extended](data-types.md#large-number-and-datetime-extended)).
- **Pages the engine adds are taken out of the database's free-page map**, so Access does not hand them
  out again and write over them ([How a file grows](storage.md#how-a-file-grows)).
- **Protected files open as Access opens them**: an .mdb's database password is checked, an encoded .mdb is
  read and written with the key its header holds, and every kind of protection Access makes is tested against a
  file it made. See [Protected files](protected-files.md).
- **A file open to be written is kept from Access**, which opened it beside the library and wrote over it; a file
  opened only to be read opens beside Access. See [Sharing a file with Access](sharing.md).
- **The test corpus is in the repository**, pinned by checksum. See [tests/corpus](../tests/corpus/README.md).

What this fork does not do yet, and its limits: [What is and is not supported](supported-features.md).

## Where this library and Java Jackcess differ

Where Access and Jackcess differ, Access is followed. Each of these is described on the page it links to:

- **Schema changes.** A name that is taken is refused, not changed; a cycle of cascading relationships is
  allowed; a table's index slots stay in the order of their names; a new index's block carries the counts Access
  puts there; a definition that ends in the last eight bytes of a page has the page after it Access gives it,
  where Jackcess writes it on the page it fills and Access then opens nothing of the file
  ([How a table is written](tables-and-schema.md#how-a-table-is-written)).
- **Dropping and renaming** a column, an index, a relationship or a table, which Jackcess does not do
  ([Dropping and renaming](tables-and-schema.md#dropping-and-renaming)).
- **Names** are one as Access takes them, by their text index keys, where Jackcess takes two as one when they are
  equal without regard to case: names in another width or kana, or with an accent written as one character or as
  two, are one here, and `xµ` and `xμ` are two ([Names](tables-and-schema.md#names)).
- **A one-to-one relationship's foreign key** has a unique index, as Access makes it, where Jackcess gives it a
  plain one ([Creating a relationship](relationships.md#creating-a-relationship)).
- **Properties** Access wrote go back byte for byte, where Jackcess lists the names again and encodes every value
  again ([Properties](properties.md#reading-and-writing-properties)).
- **A Large Number or Date/Time Extended column** in an `.accdb` of an earlier format is created, and the file
  becomes one of a later format, where Jackcess refuses the table
  ([A column that changes a file's format](file-formats.md#a-column-that-changes-a-files-format)).
- **ー, 々, ゝ, the shadda and the other marks Access keys by the character before them** have that character's
  weight in a General - Legacy key, or after a kana its vowel, as Access keys them; Jackcess gives each the key its
  codes file has for it, which is its key after "a", wherever it is, so its keys for データ and 日々 are not the ones
  Access writes ([The marks keyed by the character before them](indexes.md#the-marks-keyed-by-the-character-before-them)).
- **A key longer than 510 bytes** is cut with Access's digest; the versions of Jackcess that cut keys leave the
  last byte out, and Jackcess before 5.2.0 wrote such keys whole
  ([Keys longer than 510 bytes](indexes.md#keys-longer-than-510-bytes)).
- **A Decimal past 96 bits** reads as its low 96 bits, as Access reads it; Java Jackcess returns all 128 bits as a
  `BigDecimal`, which a `decimal` cannot hold ([Decimal](data-types.md#decimal)).
- **Text whose bytes begin 0xFF 0xFE** is read as ACE reads it: as compressed text only in a Text column with
  Unicode compression, a Memo kept in its row and a Memo in one long-value row of a compressed column, and after a
  switch to UTF-16LE two bytes at a time. Java Jackcess reads such bytes as compressed text in every column and
  switches at every 0x00 byte. Text that begins with U+FEFF is written in runs where ACE writes it so, where
  Jackcess writes it as UTF-16LE ([Text and Memo](data-types.md#text-and-memo)).
- **An Access 97 file's text** is read in code page 1252 on every machine, unless
  `DatabaseOpenOptions.Access97CodePage` names another. Access reads it in the code page of the machine it runs on,
  which the file does not record; Java Jackcess reads it in the JVM's default charset (UTF-8 from Java 18), unless
  `DatabaseBuilder.withCharset`, `Database.setCharset` or the system property `jackcess.charset.VERSION_3` names
  another. A text index of such a file is searched here by the bytes of a value in its code page, as Jet keys it
  ([Text in a code page](file-formats.md#text-in-a-code-page)).
- **Text given for a Double or a Single** is the number Access stores for it, the nearest to its first 20 or 12
  significant digits, where Java Jackcess reads it with `Double.valueOf`, every digit, and a Single through that
  double; and **a number given for a Text column** is written as Access writes it, 15 or 7 significant digits, where
  Jackcess writes Java's own text of it, `1.0E-5` for what Access writes `0.00001`
  ([A bool, text or a date for a number](data-types.md#a-bool-text-or-a-date-for-a-number),
  [Text and Memo](data-types.md#text-and-memo)). The Access followed is 64-bit ACE 16: 32-bit ACE stores some text
  for a Double one unit in the last place from it, and writes some numbers as text otherwise, which this
  library does not follow in a process of either bitness
  ([A bool, text or a date for a number](data-types.md#a-bool-text-or-a-date-for-a-number)).
- **A batch insert** goes in whole or not at all, where Jackcess's `addRows` leaves the rows before a refused one
  in the table ([CHANGELOG](../CHANGELOG.md#added)).
- **Cursors** have no column matchers of a caller's own (Jackcess's `ColumnMatcher`), and do not find a null by
  looking for null, where Jackcess finds one ([What cursors do not do](cursors-and-rows.md#what-cursors-do-not-do)).
  An index cursor finds rows by key, the word `Table.FindRowByPrimaryKey` uses: Jackcess's `findRowByEntry`,
  `findClosestRowByEntry` and `newEntryIterable` are `IndexCursor.FindRowByKey`, `FindClosestRowByKey` and
  `FindRowsByKey`, and the start and end entries `CursorBuilder` takes are an `IndexRange`'s `StartKey` and `EndKey`
  ([Index cursors and joins](cursors-and-rows.md#index-cursors-and-joins)).
- **A file open to be written** is kept from other writers by the file's sharing, where Java Jackcess takes no lock
  and says a file must not be written by two programs at once; both open a file read-only when asked, and Java
  Jackcess also opens one it cannot write to read-only without being asked, which this library leaves to the
  caller ([Compared with Java Jackcess](sharing.md#compared-with-java-jackcess)).
- **A complex column** - an attachment, a multi-value field, version history - reads as null and a table with one
  is not written to, because this library does not read or write the column's values ([#24]). Java Jackcess reads
  the number the row holds as a `ComplexValueForeignKey`, through which the values are read and changed, and gives
  a new row the next number of the table's counter, as Access does ([Complex columns](data-types.md#complex-columns)).
- **A validation rule** is not applied: a row that breaks it is written ([#25]). Java Jackcess evaluates the rule,
  unless expression evaluation is switched off, and refuses the row, as Access does
  ([Required and Allow Zero Length](properties.md#required-and-allow-zero-length)).
- **A linked table** is not followed, and is not among a database's tables ([#28]). Java Jackcess opens the
  database it points to through a `LinkResolver` and gives the table
  ([Opening and creating a database](getting-started.md#opening-and-creating-a-database)).
- **A database is opened from a path** ([#32]), where Java Jackcess also takes a `FileChannel`, which its
  `MemFileChannel` makes of a file held in memory.

And where the two agree: an Access 97 file is only read, as in Jackcess
([Access 97 files are read-only](file-formats.md#access-97-files-are-read-only)); `EnforceForeignKeys` is on by
default, as in Jackcess ([Relationships and foreign keys](relationships.md)); an index is trusted to hold every row,
as Access and Jackcess trust one ([An index is trusted](indexes.md#an-index-is-trusted)); text in a sort order
whose keys are not made is not written, as Jackcess writes none of them either ([Refused](indexes.md#refused)); and
an import makes a Large Number only in a format that has one, as Java Jackcess does
([Type round-trip cheat sheet](importing-and-exporting.md#type-round-trip-cheat-sheet)).

[Back to contents](README.md)

[#24]: https://github.com/markmario/jackcess-dotnet/issues/24
[#25]: https://github.com/markmario/jackcess-dotnet/issues/25
[#28]: https://github.com/markmario/jackcess-dotnet/issues/28
[#32]: https://github.com/markmario/jackcess-dotnet/issues/32
[#58]: https://github.com/markmario/jackcess-dotnet/issues/58
