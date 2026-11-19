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
- **Inserts cost the same however large the table.** A row goes to the table's last data page, so rows
  also stay in insertion order. A page holds at most 255 rows ([Where a new row goes](storage.md#where-a-new-row-goes)).
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
- **Tables the engine creates are ones Access lists, reads, seeks in and adds to.**
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
- **A one-to-one relationship's foreign key** has a unique index, as Access makes it, where Jackcess gives it a
  plain one ([Creating a relationship](relationships.md#creating-a-relationship)).
- **Properties** Access wrote go back byte for byte, where Jackcess lists the names again and encodes every value
  again ([Properties](properties.md#reading-and-writing-properties)).
- **A Large Number or Date/Time Extended column** in an `.accdb` of an earlier format is created, and the file
  becomes one of a later format, where Jackcess refuses the table
  ([A column that changes a file's format](file-formats.md#a-column-that-changes-a-files-format)).
- **A key longer than 510 bytes** is cut with Access's digest; the versions of Jackcess that cut keys leave the
  last byte out, and Jackcess before 5.2.0 wrote such keys whole
  ([Keys longer than 510 bytes](indexes.md#keys-longer-than-510-bytes)).
- **A Decimal past 96 bits** reads as its low 96 bits, as Access reads it; Java Jackcess returns all 128 bits as a
  `BigDecimal`, which a `decimal` cannot hold ([Decimal](data-types.md#decimal)).
- **A batch insert** goes in whole or not at all, where Jackcess's `addRows` leaves the rows before a refused one
  in the table ([CHANGELOG](../CHANGELOG.md#added)).
- **Cursors** have no column matchers of a caller's own (Jackcess's `ColumnMatcher`), and do not find a null by
  looking for null, where Jackcess finds one ([What cursors do not do](cursors-and-rows.md#what-cursors-do-not-do)).

And where the two agree: an Access 97 file is only read, as in Jackcess
([Access 97 files are read-only](file-formats.md#access-97-files-are-read-only)); `EnforceForeignKeys` is on by
default, as in Jackcess ([Relationships and foreign keys](relationships.md)); an index is trusted to hold every row,
as Access and Jackcess trust one ([An index is trusted](indexes.md#an-index-is-trusted)); text in a sort order
whose keys are not made is not written, as Jackcess writes none of them either ([Refused](indexes.md#refused)); and
an import makes a Large Number only in a format that has one, as Java Jackcess does
([Type round-trip cheat sheet](importing-and-exporting.md#type-round-trip-cheat-sheet)).

[Back to contents](README.md)
