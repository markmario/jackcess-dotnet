# Jackcess.Net documentation

Jackcess.Net reads and writes Microsoft Access files, `.mdb` and `.accdb`, in managed code, with no ODBC or ACE
driver. These pages describe what it does, topic by topic, and how what it writes was checked against Access
itself. The project's [README](../README.md) has a quick start and a summary; the [CHANGELOG](../CHANGELOG.md) has
every change.

## Contents

### Using the library

- [Getting started](getting-started.md): installing the package, which assembly each framework gets, referencing
  the source, and opening or creating a database.
- [Rows and cursors](cursors-and-rows.md): reading, inserting, updating and deleting rows; cursors, index cursors
  and joins; how a lookup matches the values it is given; and what makes a change refused.
- [Data types](data-types.md): the `DataType` names, and Large Number, Date/Time Extended, Currency, Decimal,
  Date/Time, text, binary, Yes/No and calculated columns.
- [Tables and schema changes](tables-and-schema.md): creating a table and its primary key; adding, dropping and
  renaming columns, indexes and tables; the names Access allows.
- [Indexes and keys](indexes.md): which indexes are kept and when one is trusted; text index keys in each sort
  order; keys longer than 510 bytes.
- [Relationships and foreign keys](relationships.md): enforcement and cascades, how keys are matched, what it
  costs, and creating and dropping a relationship.
- [Properties](properties.md): a table's, a column's and the database's properties; Required and Allow Zero
  Length; Replication ID AutoNumbers.
- [Importing and exporting](importing-and-exporting.md): `DataTable`, `DataSet` and `IEnumerable<T>` in and out,
  and the type round-trip cheat sheet.

### Files

- [File formats](file-formats.md): the formats read and written, creating a file, a column that changes a file's
  format, how a file's format is decided, and Access 97 files, which are only read.
- [Protected files](protected-files.md): database passwords, encoded `.mdb` files and encrypted `.accdb` files.
- [Sharing a file with Access](sharing.md): opening a file to write or only to read while Access or another
  `Database` has it open, what a reading beside a writer can meet, and what reading only costs.
- [Storage: rows, long values and file growth](storage.md): where rows and long values go on a file's pages,
  what a delete leaves, and how a file grows.
- [Files written by earlier versions](earlier-versions.md): what an earlier version of this code left in a file,
  `Database.VerifyIndexes`, and what to do about each.

### The project

- [What is and is not supported](supported-features.md): the status of each feature, the limits to know about,
  and what is not done.
- [Differences from Java Jackcess and from upstream](differences.md): where this started, what this fork
  changes, and where it follows Access rather than Jackcess.
- [Building and testing](building-and-testing.md): the SDK, the tests and their five configurations, the Access
  referee tests, the test corpus, and reporting a problem.
- [Releasing](releasing.md): the release tag, the workflow that publishes it, and what it checks.
- [CHANGELOG](../CHANGELOG.md): every change, with the breaking changes to the public API since 1.2.2.

## Words these pages use

- **Jet** is the database engine of Access up to Access 2003: Jet 3 is Access 97's, Jet 4 that of Access 2000 to
  2003. An `.mdb` is a Jet file.
- **ACE**, the Access Database Engine, is the engine of Access 2007 and later; it writes `.accdb` files, and
  `.mdb` files of Access 2000 and later. ACE 12 is Access 2007's, ACE 16 that of Access 2016 and later. "ACE's
  `=`" is a query's `=` as ACE answers it: through an index ("ACE's index") or by reading the table ("ACE's
  scan").
- **DAO** (Data Access Objects) is the interface Access's own code uses to the engine: DAO 3.6 goes with Jet 4.0,
  DAO 16 with ACE 16.
- **Compacting** a file is Access's Compact and Repair (DAO's `DBEngine.CompactDatabase`): Access writes the file
  again, rebuilding its indexes and leaving out the room deleted rows and freed pages took.
- **Measured** means made, read or written by Access's own engine - ACE or DAO, on Windows - and compared with
  what this library makes. The scripts that made the test files are in `tools/`.

## Where a change is documented

A change to what the library does is described on the page of its topic, beside the rule it changes, with what
was measured to settle it; it also has a line under "Unreleased" in the [CHANGELOG](../CHANGELOG.md).

| A change to | Goes on |
| --- | --- |
| How a value of a type is read, written or converted | [Data types](data-types.md), under the type |
| How a lookup, a range, a closest row or a join matches a value | [How a lookup matches values](cursors-and-rows.md#how-a-lookup-matches-values), under the type |
| Reading, inserting, updating or deleting rows; cursors and index cursors; what refuses a change | [Rows and cursors](cursors-and-rows.md) |
| Which indexes are kept, built or trusted; index keys, sort orders, keys longer than 510 bytes | [Indexes and keys](indexes.md) |
| Building an index or a primary key over the rows a table has | [Changing a table that has rows](tables-and-schema.md#changing-a-table-that-has-rows), and how its keys are made in [Indexes and keys](indexes.md) |
| Creating, changing, dropping or renaming a table, a column or an index | [Tables and schema changes](tables-and-schema.md) |
| Which names are allowed, and which two names are one | [Names](tables-and-schema.md#names) |
| Relationships and foreign-key enforcement | [Relationships and foreign keys](relationships.md) |
| Properties, Required and Allow Zero Length | [Properties](properties.md) |
| Importing and exporting | [Importing and exporting](importing-and-exporting.md) |
| File formats, creating a file, Access 97 files | [File formats](file-formats.md) |
| Passwords and encryption | [Protected files](protected-files.md) |
| Opening a file beside Access or another `Database` | [Sharing a file with Access](sharing.md) |
| Where rows and long values go on a page, and how a file grows | [Storage](storage.md) |
| What a file an earlier version wrote holds, and what this library does with it | [Files written by earlier versions](earlier-versions.md) |
| A feature added, or a limit lifted or found | its topic page, and [What is and is not supported](supported-features.md) |
| Something that now differs from Java Jackcess, or from the port this started from | its topic page, and [Differences](differences.md) |
| Building, testing and CI | [Building and testing](building-and-testing.md) |
| Releasing and publishing | [Releasing](releasing.md) |

A new page goes in the contents above and in the README's list of pages. A heading is an anchor that other pages
link to, so a heading is renamed only together with every link to it. An issue is cited as a reference link,
`[#NN]`, defined at the foot of the page. The README is the package's readme on nuget.org, so its links are
absolute.

[Back to the README](../README.md)
