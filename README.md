<!-- qcat-repository-image:start -->
<p align="center">
  <img src=".github/repository-card.png" alt="jackcess-dotnet repository image" width="320" />
</p>
<!-- qcat-repository-image:end -->

# JackcessDotNet

[![Upstream](https://img.shields.io/badge/upstream-mehran--ghanizadeh%2Fjackcess--dotnet-181717?logo=github)](https://github.com/mehran-ghanizadeh/jackcess-dotnet)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

Pure managed library for reading and writing Microsoft Access (`.mdb` / `.accdb`) files.
No ODBC, no ACE drivers, no native dependencies. It builds for .NET 8, .NET Framework 4.7.2
and .NET Standard 2.0.

**This is QCAT's fork of [`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet).**
It exists for two reasons:

- **.NET Framework.** The `JackcessDotNet` package on nuget.org ships a `net10.0` assembly and nothing
  else (checked for 2.0.0 and 3.1.0). QCAT's consumers run on .NET Framework 4.8.1, so they cannot use it.
- **Files Microsoft Access accepts.** The files this fork writes are checked against Access's own engine
  (ACE), not only read back by the library that wrote them. See [What this fork changes](#what-this-fork-changes).

The fork publishes no package. See [Using this fork](#using-this-fork).

The library is a C# port of the [`spannm/jackcess`](https://github.com/spannm/jackcess) Java
project (a maintained fork of the original [Jackcess](https://jackcess.sourceforge.io/)
by James Ahlborn). The Java project remains the source of truth for the Jet
file format; this port tracks its behaviour and reuses its test corpus for
verification.

## Using this fork

Reference the project from source. QCAT's ExportMdb.Interop takes this repository as a git submodule,
references `src/JackcessDotNet/JackcessDotNet.csproj`, and merges the built assembly into its own.

```
git submodule add https://github.com/qcatco/jackcess-dotnet.git ext/jackcess-dotnet
```

```xml
<ProjectReference Include="..\..\ext\jackcess-dotnet\src\JackcessDotNet\JackcessDotNet.csproj" />
```

`dotnet add package JackcessDotNet` installs the upstream package, not this fork. The version in the
project file (1.2.2) and [CHANGELOG.md](CHANGELOG.md) are upstream's, from the release the fork started
at; a build here also writes a `JackcessDotNet.1.2.2.nupkg` to `artifacts/`, which is not the nuget.org
package of that version and is not published anywhere. If you distribute a build of this fork, the
[Apache License 2.0](LICENSE) has to travel with it.

## What this fork changes

Each of these was found by having Access or ACE open a file the engine wrote.

- **Three target frameworks:** `net8.0`, `net472` and `netstandard2.0`.
- **Files up to the format's own limit.** A table's usage map grows past its first window of pages, as
  Access's does, up to the 2 GB a Jet file can be. Before, a file could not be written past about 2 MB.
- **Inserts cost the same however large the table.** A row goes to the table's last data page, so rows
  also stay in insertion order. A page holds at most 255 rows.
- **A memo or OLE value is kept in its row only up to 64 bytes**, Access's own limit. The engine kept up
  to 1,024 bytes there, and Access showed `#Error` for those values.
- **Text index keys are the ones Access writes**, for the General - Legacy sort order (Access 2000-2007)
  and for General (Access 2010 and later).
- **Every index of a table is kept on every write**, not the primary key alone, and unique indexes are
  enforced. Access answers through them after the engine adds, changes or deletes rows.
- **An insert fills AutoNumber columns from the table's own counter**, as Access does.
- **Foreign keys can be enforced** on insert, update and delete, with cascades (`EnforceForeignKeys`,
  off by default).
- **Rows and long values are laid out as Access lays them out**, so Access reads back every value the
  engine writes, a long memo in several pieces included.
- **Tables the engine creates are ones Access lists, reads, seeks in and adds to.**
- **Pages the engine adds are taken out of the database's free-page map**, so Access does not hand them
  out again and write over them.
- **The test corpus is in the repository**, pinned by checksum. See [tests/corpus](tests/corpus/README.md).

Limits to know about:

- Text keys are written in the General - Legacy and General orders only. The engine refuses, before
  changing anything, to write to a table with an index whose keys it cannot write: text in Access 97's
  order or any other sort order, or a column type it makes no key for. It still reads such a table.
  See [Text index keys](#text-index-keys-sort-orders-and-limits).
- Access 97 (Jet 3) files are read and not written, as in Jackcess: one is opened for reading only, and
  creating one, or changing anything in one, is refused with `NotSupportedException`.

## Building and testing

You need the **.NET 10 SDK**. The tests run on Microsoft Testing Platform (`global.json` selects it), and
only the 10 SDK's `dotnet test` runs that.

```
dotnet build JackcessDotNet.slnx --configuration Release
dotnet test JackcessDotNet.slnx --configuration Release
```

- The tests are xUnit.net v3 and run on .NET 8 only; the library's other two targets are built, not tested here.
- Options are the platform's and xUnit's, not VSTest's. One class: `dotnet test --filter-class "*UsageMapTests"`.
  A TRX report: `--report-xunit-trx --report-xunit-trx-filename test-results.trx`, written to `TestResults/`.
- `./tools/corpus.ps1` checks every corpus file against `tests/corpus/MANIFEST.sha256`. CI runs it before building.
- The engine's tests read back what the engine wrote. Whether Access accepts a file is tested where ACE
  is installed, in ExportMdb.Interop's suite.

Issues are disabled on this repository. QCAT tracks work on the fork with ExportMdb.Interop's.

## Quick start

```csharp
using JackcessDotNet;

// Open an existing .mdb / .accdb (auto-detects version)
using var db = Database.Open("Northwind.mdb");

foreach (var name in db.ListTables())
    Console.WriteLine(name);

var customers = db.GetTable("Customers");
foreach (var row in customers.NewCursor())
    Console.WriteLine($"{row["CustomerID"]}  {row["CompanyName"]}");

// Create a new database, define a schema, insert rows
using var fresh = Database.Create("MyData.mdb", JetVersion.Jet4);
var people = fresh.CreateTable("People", new[]
{
    new ColumnBuilder("Id",   DataType.Long).Build(),
    new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
}, primaryKey: "Id");
people.Insert(new Row { ["Id"] = 1, ["Name"] = "Alice" });
```

## Opening password-protected files

```csharp
// Jet RC4 (.mdb, Access 97 / 2000–2003)
using var db = Database.Open("Confidential.mdb", "passw0rd");

// .accdb encryption — Agile (Office 2010+), ECMA Standard (Office 2007),
// RC4 CryptoAPI (Office 2002–2003), or Non-Standard AES — all auto-detected
// from the EncryptionInfo header.
using var db = Database.Open("Confidential.accdb", "passw0rd");

// Inspect which scheme the codec picked (useful when triaging an unfamiliar file).
byte[] page0 = File.ReadAllBytes("Confidential.accdb").Take(4096).ToArray();
var codec = OfficeCryptCodecHandler.FromDbHeader(page0, "passw0rd");
Console.WriteLine(codec?.Scheme);   // → "Agile Encryption (Office 2010+)"
```

Wrong passwords throw `UnauthorizedAccessException` before any data page is
parsed — no cryptic "page X corrupt" errors.

## Composite (multi-column) primary keys

```csharp
db.CreateTable("Orders", new[]
{
    new ColumnBuilder("CustomerId", DataType.Long).Build(),
    new ColumnBuilder("OrderId",    DataType.Long).Build(),
    new ColumnBuilder("Total",      DataType.Money).Build(),
}, primaryKeyColumns: new[] { "CustomerId", "OrderId" });
```

Up to 10 columns per composite key (Jet's hard limit). The single-column
`primaryKey:` overload still works for the common case.

## Foreign-key enforcement (opt-in)

```csharp
using var db = Database.Open("Northwind.mdb");
db.EnforceForeignKeys = true;                  // default: false

// The relationships in MSysRelationships that have referential integrity are kept.
orders.Insert(new Row { ["CustomerID"] = "INVALID", ... });
//   throws InvalidOperationException: "Foreign-key violation in relationship 'CustomersOrders'..."

customers.UpdateByPrimaryKey("ALFKI", new Row { ["CustomerID"] = "ALFKA" });
//   cascade update: the customer's orders take the new key - or, without it, refused while it has orders

customers.DeleteRow("CustomerID", "ALFKA");
//   cascade delete: its orders go too, and their order lines; cascade-to-null: their CustomerID becomes null;
//   neither: refused while it has orders
```

`Insert`, `UpdateByPrimaryKey` and `DeleteRow` refuse what would leave a row referring to no parent, and carry out
the cascades a relationship asks for, as Access does. A foreign key whose columns are all null refers to nothing.
A refused change leaves the file untouched: everything is checked, cascades included, before anything is written,
and a change that fails while being written is taken back. With the property off, nothing is checked and nothing
cascades.

What it costs: rows are found through the tables' indexes. Access gives every relationship an index on its
foreign key, so an insert reads about a dozen pages to find its parent, and an update or delete reads the rows it
reaches and the index pages that lead to them - a cascade delete of five rows about fifty pages, whether the child
has a thousand rows or a million - and holds only those rows in memory. Every row a cascade rewrites or deletes
keeps its entry in every index of its table, so Access still finds it, and still protects its parent, afterwards.

An index is trusted to hold every row, as Access trusts it. An entry whose row has gone is harmless (each row found
is read and compared), but a row with no entry is not found. Versions of this library before it kept every index
(those in ExportMdb.Interop up to 2.1.2) added a row to its table's primary key and to no other index, so a row one
of them inserted into an Access-made table is missing from the index on its foreign key: enforcement does not see
it as a child, and its parent can be deleted or re-keyed leaving it an orphan. Access makes the same mistake with
the same file, which is already inconsistent to it
([qcatco/exportmdb.interop#64](https://github.com/qcatco/exportmdb.interop/issues/64)).

Keys are matched as Access matches them - as its indexes hold them:

- **A null matches a null.** A parent whose key is null (a unique index allows one) has the rows whose foreign
  key is null for children: deleting it deletes them under cascade delete and is refused without it, and giving
  it a key gives them the key under cascade update. A parent that has a key never touches them, and a row can
  always be added, or changed, to have no foreign key. In a key of several columns, null matches null column by
  column.
- **Numbers are matched as stored.** A Double or Single by its bits: 0 and -0 are two keys, and so are a Double
  and the next Double after it. A Date/Time by the double the row stores, which a `DateTime` does not always hold.
- **A Yes/No holds no null.** A null given it is stored No and checked as No: refused when no parent's key is No.
  A cascade-to-null of a Yes/No foreign key leaves the row No, as Access leaves it, parent or no parent.

What enforcement does not check:

- **A column's Required property**, for a caller's own update: `UpdateByPrimaryKey` may null a column Access would
  require a value for, exactly as with enforcement off. (A cascade-to-null into such a column is refused.)
- **A key written again unchanged**: Access's `UPDATE` refuses to assign a key other rows refer to when the
  relationship does not cascade updates, even the value it already has; here an unchanged key is not a change.
- **Relationships without referential integrity**: they only tell Access's query designer how tables join.

Where no index on the columns can be searched (a table made without one on its foreign key, an index whose keys
this library does not write, a parent key that is null) the table is read from end to end and held in memory until
the change is written: some 200 bytes a row, about 60 MB and a second for a table of 300,000 rows. A change that
would rewrite a row of a table Access has deleted a column from is refused (`NotSupportedException`): rows of
such a table are not yet written correctly.

## Importing from `DataTable`, `DataSet`, or `IEnumerable<T>`

Skip the schema boilerplate — the importer infers columns from the source type:

```csharp
using var db = DatabaseImporter.CreateFromDataTable("out.mdb", myDataTable);

// or add into an existing database
db.ImportTable(otherDataTable, tableName: "Extra", primaryKey: "Id");
db.ImportTables(myDataSet);

db.ImportTable<Customer>(customers, primaryKey: "Id");

// Append rows into a pre-existing table (instead of failing):
db.ImportTable(moreRows, options: new ImportOptions { AppendIfExists = true });
```

POCO mapping respects `[Key]`, `[Column("X")]`, `[MaxLength]`, `[NotMapped]`,
and `[DatabaseGenerated(Identity)]`. Long strings (>255 chars) auto-promote
to Memo; large byte arrays (>255 bytes) to OLE. Unmappable types can fall
back to `Memo` via `ImportOptions.FallbackUnmappableToString = true`.

## Exporting back

```csharp
DataTable          dt    = db.ExportToDataTable("Customers");
DataSet            ds    = db.ExportToDataSet();
IEnumerable<Customer> cs = db.ExportToCollection<Customer>("Customers");
```

The `IEnumerable<T>` path is lazy via `yield`, so `Take(n)` stops early without
materialising the whole table.

## Status

| Feature                                           | Status |
| ------------------------------------------------- | ------ |
| Read Jet 3 (Access 97) / Jet 4 (Access 2000–2003) | ✅      |
| Read ACE 12 / 14 / 16 / 17 (`.accdb`)             | ✅      |
| Create new `.mdb` files (Jet 4)                   | ✅      |
| Create or write Jet 3 (Access 97) files           | ❌ Read-only, as in Jackcess |
| Row CRUD + primary-key B-tree index               | ✅      |
| Secondary indexes: created with a table, kept on every write, unique enforced | ✅ Byte, Integer, Long, Currency, Single, Double, Date/Time, Yes/No, Text and Memo (General - Legacy and General), GUID |
| Memo / OLE long values                            | ✅      |
| PropertyMap & MSysRelationships                   | ✅      |
| Password-protected `.mdb` (Jet RC4 codec)         | ✅      |
| Password-protected `.accdb` (Agile Encryption, Office 2010+) | ✅ Read + write¹ |
| Password-protected `.accdb` (ECMA Standard Encryption, Office 2007) | ✅ Read + write |
| Password-protected `.accdb` (RC4 CryptoAPI, Office 2002–2003) | ✅ Read + write |
| Password-protected `.accdb` (Non-Standard AES, compat mode 0) | ✅ Read + write |
| Password-protected `.accdb` (Extensible Encryption) | ❌ External CSP — non-portable |
| Multi-column primary keys                         | ✅      |
| Foreign-key enforcement, with cascades (opt-in)   | ✅      |
| Queries (`MSysQueries`)                           | ❌ Not yet |

¹ Agile write doesn't recompute the DataIntegrity HMAC, so files modified
through this library round-trip cleanly through `Database.Open(path, password)`
but Office Access may flag a stale integrity hash. Matches the upstream
`jackcess-encrypt` limitation.

## Text index keys: sort orders and limits

An index entry's key for text depends on the column's sort order. The library
writes, and searches by, the keys of two orders, each checked byte for byte
against the keys Microsoft's own engine (ACE 16) writes:

- **General - Legacy** (sort order 1033, version 0): Access 2000 to 2007, and
  what a new database gets by default in later versions.
- **General** (1033, version 1): what Access 2010 and later can make a new
  database in. A table created in such a file sorts as the file does.

What it does not do, and refuses rather than write a key Access cannot find:

- **Access 97's order, and any other sort order** (for example 1049, Russian).
  A file whose catalog sorts in one is not given a table, and a table with an
  index on a Text or Memo column that sorts in one is not inserted into,
  updated or deleted from.
  Such a file is still read; its text indexes are not searched, its tables are
  scanned. ACE 12's keys for Access 97's order are kept in
  `tests/JackcessDotNet.Tests/IndexKeys` for a later port
  (qcatco/exportmdb.interop#26). An Access 97 file is not written to at all:
  see [Access 97 files are read-only](#access-97-files-are-read-only).
- **A value in an indexed Text or Memo column, or a table name, with a
  character Access keys by the character before it.** These are the Japanese length and iteration marks (ー U+30FC,
  々 U+3005, ゝ U+309D, ゞ U+309E, ヽ U+30FD, ヾ U+30FE, 〱 U+3031, 〲 U+3032,
  ｰ U+FF70; in General also U+3033-3035, 〻 U+303B, ゠ U+30A0 and ꀕ U+A015) and,
  in General - Legacy, the Arabic shadda (U+0651, U+FE7C, U+FE7D). So a Japanese
  name with ー (データ, コンピューター) or an Arabic word with a shadda cannot be a
  value of an indexed column (the primary key or any other index) or a table name: `Insert`, `UpdateByPrimaryKey`,
  `DeleteRow` and `CreateTable` throw `NotSupportedException` before anything
  is written. In General the same goes for four Arabic ligatures (U+06D6,
  U+06D7, U+FD24, U+FD6F). Any text is fine in a column that is no key, and a
  row Access wrote with such a key is still found: the table is scanned for it.

### Keys longer than 510 bytes

Access keeps no index key longer than 510 bytes. It cuts a longer one there
and ends it in a two-byte digest of the rest - a key of one column, of a Memo
(its first 255 characters) or of several columns together. A file can hold
three kinds of entry for such a key:

| Written by | Stored as | Read / lookup | Insert, update | Delete |
| --- | --- | --- | --- | --- |
| Access (Jet 4.0, ACE 12, ACE 16 alike) | cut, with the digest | found | a duplicate in a unique index is refused | entry removed |
| This library, since it cut keys | the same bytes as Access | found | the same | entry removed |
| This library before that, and Jackcess before 5.2.0 | **whole**, over 510 bytes | found | seen by a unique index, so a duplicate is refused; a rewritten row's whole entry is removed and a cut one written | entry removed, under the whole key |

- **Lookups** of a value whose key is cut read the table instead of searching
  the index, so a row is found whichever way its entry is stored.
- **Writes** always store the cut key. Updating a row whose entry was whole
  replaces it with a cut one; nothing converts rows that are not written.
- **Uniqueness** follows Access: two values cut to the same entry are
  duplicates, whichever way the existing one is stored. A file an earlier
  release wrote may hold two such rows (their whole keys differed); neither
  can then be rewritten until the other is deleted.
- **Deletes and updates** remove the entry the row actually has, cut or whole,
  matched by its row. A row with no entry at all - earlier releases kept only
  the primary key - has nothing to remove, and that is not an error.
- Access itself does not seek a whole key: until such a row is rewritten,
  Access finds it by a scan but not through the index.

`tests/JackcessDotNet.Tests/Assets/engine-696ca52-long-keys.mdb` is a file an
earlier release wrote, made by `tools/legacy-long-keys.ps1`.

## Rows and long values: what to know

Rows are read and written by the table's own definition - where it keeps each column, including after columns
have been deleted - and a row Access moved to another page is read where its pointer leads.

- **Decimal values written by releases before this read back as different numbers.** A Decimal is stored as
  four 32-bit words; earlier releases wrote and read sixteen bytes in the other order, so they and Access never
  agreed (a 1 written by an earlier release is 16,777,216 to Access, and now to this library too). Such a value
  cannot be told from one Access wrote; one that reads with more digits than its column's precision is almost
  certainly one. Access-written Decimals, which earlier releases misread, now read correctly.
- **Fixed-length text (SQL `CHAR(n)`) is returned filled to its length with spaces**, as Access returns it, and
  is written that way.
- **A calculated column reads as null**, and a table with one is not written to: an insert, an update and a
  delete are each refused with `NotSupportedException`.
- **A row that cannot be written changes nothing.** The whole of a new row is encoded before anything is
  written - for an update, before the row it replaces is touched - so a value its column cannot hold, a value
  not of its type, or a row too large leaves the file byte for byte as it was. Only a failure of the file itself
  (the disk, the stream) can leave a change half made; an update outside a foreign-key cascade is not undone.
- **An update leaves the stored bytes of every column it does not name.** A Date/Time Access wrote finer than a
  millisecond, or outside what `DateTime` holds, is not rounded or lost by an update of another column.
- **An overflow pointer that leads outside the file, or to a row its page does not have, is a damaged file**
  (`InvalidDataException` naming the page), not a missing row.
- **A deleted row is marked as Access marks one** (both flag bits). Rows deleted by earlier releases carry the
  first bit alone; they stay deleted for this library and for Access, which reads, edits and compacts such a
  table as any other.
- **Long values are placed as Access places them**: every chunk of a value too long for one page (4,076 bytes)
  has a page to itself. Access gives every page of such a value back to the file when the value is deleted or
  replaced, so a page shared with another value loses that value. `Database.ShareLongValuePages = true` keeps the
  placement of earlier releases - the last chunk on a page with other values' - which is smaller (a fifth
  smaller for 5,000-character Memos, nearly half for values just over one page) and reads the same, but is not
  safe for Access to edit long values in. A file written that way keeps its shared pages when it is added to.
  Added to in the default placement, such a file gets no value on a page that may hold the end of a chain.
- **A value of one chunk goes to the page with the least room that will take it**, found by its room: an insert
  looks at no page that cannot take the value, however many the column has. A page is listed in the column's
  free-space map while it has 264 bytes or more free, which is where ACE was seen to list one (it listed none
  with 258 or fewer), and only once a value is on it. The first long value written to a column in a session
  reads the pages its map lists, once.

`tools/CorpusDump` writes what the engine reads of every table of a folder of files as text, for comparing two
versions of the engine.

## Access 97 files are read-only

An Access 97 (Jet 3) file is read and never written, as in Java Jackcess, whose Jet 3 format is read-only
(`JetFormat.READ_ONLY`; `JetFormat.IsReadOnly` here). `Database.Open` opens one for reading only - a file marked
read-only, or on a read-only share, opens like any other - and everything that would change it is refused with
`NotSupportedException` before anything is written: inserting, updating or deleting a row, creating or importing
a table. `Database.Create(path, JetVersion.Jet3)` is refused the same way, and makes no file. Earlier versions
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

## Type round-trip cheat sheet

| CLR type          | Jet column        | Notes                                  |
| ----------------- | ----------------- | -------------------------------------- |
| `bool`            | `Boolean`         |                                        |
| `byte`            | `Byte`            |                                        |
| `short`/`ushort`  | `Int` (16-bit)    | both come back as `short`              |
| `int`/`uint`      | `Long` (32-bit)   | both come back as `int`                |
| `long`/`ulong`    | `Long` (32-bit)   | **truncated to 32 bits**               |
| `float`/`double`  | `Float`/`Double`  |                                        |
| `decimal`         | `Money`           | 4-decimal precision; use `Numeric` for more |
| `DateTime`        | `ShortDateTime`   | `Kind` is dropped                      |
| `DateTimeOffset`  | `ShortDateTime`   | stored as UTC                          |
| `Guid`            | `Guid`            |                                        |
| `string` ≤255     | `Text`            |                                        |
| `string` >255     | `Memo`            | auto-promoted on `MaxLength`           |
| `byte[]` ≤255     | `Binary`          |                                        |
| `byte[]` >255     | `Ole`             | auto-promoted on `MaxLength`           |
| `Nullable<T>`     | underlying `T`    |                                        |
| `enum`            | underlying type   | round-trips back to the enum on read   |

## Version history

[CHANGELOG.md](CHANGELOG.md) is upstream's, up to 1.2.2, where this fork started. What the fork has
changed since is in [What this fork changes](#what-this-fork-changes) and in this repository's pull requests.

## Credits

This project would not exist without the years of reverse-engineering work
done by James Ahlborn (original Jackcess) and Markus Spann (maintained fork
at [`spannm/jackcess`](https://github.com/spannm/jackcess)). The Jet binary
format is documented effectively only through their Java source.

## License

[Apache License 2.0](LICENSE) — same as the upstream Jackcess project.
