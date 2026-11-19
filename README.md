<!-- qcat-repository-image:start -->
<p align="center">
  <img src=".github/repository-card.png" alt="jackcess-dotnet repository image" width="320" />
</p>
<!-- qcat-repository-image:end -->

# JackcessDotNet

[![NuGet](https://img.shields.io/nuget/v/Jackcess.Net?logo=nuget)](https://www.nuget.org/packages/Jackcess.Net)
[![Ported from](https://img.shields.io/badge/ported%20from-mehran--ghanizadeh%2Fjackcess--dotnet-181717?logo=github)](https://github.com/mehran-ghanizadeh/jackcess-dotnet)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

Pure managed library for reading and writing Microsoft Access (`.mdb` / `.accdb`) files.
No ODBC, no ACE drivers, no native dependencies. It builds for .NET 8, .NET Framework 4.7.2
and .NET Standard 2.0.

This project started from release 1.2.2 of
[`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet) and is now
maintained on its own, with its own package, `Jackcess.Net`. It is not a release of the Jackcess project, and
neither the Jackcess authors nor Mehran Ghanizadeh maintain or endorse it. It differs from that port in two ways:

- **.NET Framework.** The `JackcessDotNet` package on nuget.org ships a `net10.0` assembly and nothing
  else (checked for 2.0.0 and 3.1.0), so nothing on .NET Framework can use it. This one ships `net8.0`,
  `net472` and `netstandard2.0`.
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
- **Foreign keys are enforced** on insert, update and delete, with cascades (`EnforceForeignKeys`,
  on by default).
- **Rows and long values are laid out as Access lays them out**, so Access reads back every value the
  engine writes, a long memo in several pieces included.
- **Tables the engine creates are ones Access lists, reads, seeks in and adds to.**
- **`.accdb` files are created**, in the Access 2007, 2010, 2016 and 2019 formats, where every version asked
  for made an Access 2000 `.mdb`. The two empty `.accdb` databases a new file is copied from were made by
  DAO's `CreateDatabase` (`tools/empty-databases.ps1`), not taken from Jackcess; `empty2003.mdb` is
  Jackcess's.
- **Large Number and Date/Time Extended columns** (Access 2016 and 2019) are read, written, created and
  indexed. A Large Number is a `long`. A Date/Time Extended is a `DateTime`: Access keeps the years 1 to
  9999 to 100 nanoseconds, which is a `DateTime`'s own range and tick, so the value comes back to the tick;
  it has no time zone, and is read with `DateTimeKind.Unspecified`.
- **Pages the engine adds are taken out of the database's free-page map**, so Access does not hand them
  out again and write over them.
- **The test corpus is in the repository**, pinned by checksum. See [tests/corpus](tests/corpus/README.md).

Limits to know about:

- Text keys are written in the General - Legacy and General orders only. The engine refuses, before
  changing anything, to write to a table with an index whose keys it cannot write: text in Access 97's
  order or any other sort order, or a column type it makes no key for. It still reads such a table.
  See [Text index keys](#text-index-keys-sort-orders-and-limits).
- A table can be asked first: `Table.InsertRefusal` is why it takes no row - the message `Insert` would
  throw - or null when it takes rows, and asking changes nothing. A caller that writes to several tables
  asks each before it writes to any.
- Access 97 (Jet 3) files are read and not written, as in Jackcess: one is opened for reading only, and
  creating one, or changing anything in one, is refused with `NotSupportedException`.
- A Large Number or Date/Time Extended column is for an `.accdb`. A table with one is refused in an `.mdb`
  before anything is written, as Access refuses it. In an `.accdb` of an earlier format it is created, and
  the file becomes one of Access 2016's format (a Large Number) or Access 2019's (a Date/Time Extended), as
  it does when Access adds the table: an Access older than that then refuses to open the file. Jackcess
  refuses the table instead.
- `Database.Create` with `Jet16` or `Jet17` makes the Access 2010 file with the later version in its header,
  which is all Access itself changes; Access's compact puts the version back to 2010's while no table has
  such a column.
- An import (`DatabaseImporter`) still makes a `long` property a `Long` column, not a Large Number.

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
2. **Builds and tests** the tagged commit, packs it at that version, and checks what ships: the ID, the three
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
```

## Opening password-protected files

```csharp
// Jet RC4 (.mdb, Access 97 / 2000–2003)
using var db = Database.Open("Confidential.mdb", new DatabaseOpenOptions { Password = "passw0rd" });

// .accdb encryption — Agile (Office 2010+), ECMA Standard (Office 2007),
// RC4 CryptoAPI (Office 2002–2003), or Non-Standard AES — all auto-detected
// from the EncryptionInfo header.
using var db = Database.Open("Confidential.accdb", new DatabaseOpenOptions { Password = "passw0rd" });
```

Wrong passwords throw `InvalidCredentialException` before any data page is
parsed — no cryptic "page X corrupt" errors.

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
  that is the first `Insert`, `UpdateByPrimaryKey` or `Validate` of a session, which reads the table once for all
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
- **What it does not catch.** An entry is still believed for what it points at: the value a row had before its
  primary key was changed is refused as a duplicate, though no row has it. And a row added under a value whose
  only entry points at a row long gone is added beside that entry, where Access does not find it through the
  index until the file is compacted
  ([qcatco/exportmdb.interop#79](https://github.com/qcatco/exportmdb.interop/issues/79)). Nothing here rebuilds
  an index: `DBEngine.CompactDatabase` does.

An entry whose row has gone does no harm either way: each row found is read and compared.

`tests/JackcessDotNet.Tests/Assets/engine-3ad3aed-rows-in-access-tables.mdb` is a database Access made and an
earlier release then wrote to, made by `tools/legacy-incomplete-indexes.ps1`. `engine-3ad3aed-rows-in-unique-indexes.mdb`
beside it is the same for unique indexes and a primary key, made by `tools/legacy-unique-indexes.ps1`.

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
this library does not write, an index that does not hold every row, a parent key that is null) the table is read
from end to end and held in memory until the change is written: some 200 bytes a row, about 60 MB and a second
for a table of 300,000 rows. A change that would rewrite a row of a table Access has deleted a column from is
refused (`NotSupportedException`): rows of such a table are not yet written correctly.

## Changing a table, and creating a relationship

A table that already has rows - one Access made or one this library made - can be given a column or an index,
and two tables a relationship, as Jackcess's `TableUpdater` and `RelationshipBuilder` do
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
```

Each change is all or nothing: what can refuse it is found before anything is written, and what is written is
taken back if writing it fails, so a refused change leaves the file byte for byte as it was. Access is the referee
of what is written (ExportMdb.Interop's `SchemaChangesInAccessTests`): beside the same index made by ACE, or the
same relationship made by DAO, in a twin file, DAO lists the same thing and the two definitions hold the same
index slots.

- **A column** goes after the table's other columns. The rows already there are not rewritten: each reads the
  new column as null - a Yes/No column as No - as Access shows them. So an AutoNumber column is added only to a
  table with no rows (Access would number the rows there are), and a complex or calculated column is not added.
- **An index** is built over the rows already there and kept on every write after. It is refused for what an
  insert would be: two rows with one key in a unique index (rows with a null in it aside), a null in an index
  that requires its values, a column whose keys this library does not write (Decimal, Binary, OLE, Complex, text
  in another sort order), a value no key is made for. An index like one the table has shares its tree. A
  primary key cannot be added to a table that has none.
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
- **A table related to itself** has both of the relationship's indexes: the foreign key's is numbered first and
  the referenced side's after it, and named by that number, as DAO writes them.
- **One to one.** A relationship whose columns are unique on both sides is marked one to one, as Access's
  Relationships window and Jackcess mark it, and its foreign key's index is then unique, as Access makes it
  (Jackcess gives it a plain one).
- **Where Access and Jackcess differ, Access is followed**: a name that is taken is refused, not changed; a
  cycle of cascading relationships is allowed; a table's index slots stay in the order of their names; a new
  index's block carries the counts Access puts there.
- **A table's definition stays on one page**, which is all this library reads of one. A column or an index that
  would take it further is refused, and a table whose definition already runs over pages is not changed: no
  table is left that the library could then not write rows to. (Jackcess writes definitions of several pages.)
- **Usage maps.** A new index's pages, and a new Memo or OLE column's, are listed in usage maps on a page of
  their own - 4 KB each - as for a table this library creates; Access finds room on the table's usage-map page
  first.
- Nothing is dropped or renamed: no column, index or relationship.

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

POCO mapping respects `[Key]`, `[Column("X")]`, `[MaxLength]`, `[NotMapped]`,
and `[DatabaseGenerated(Identity)]`. Long strings (>255 chars) auto-promote
to Memo; large byte arrays (>255 bytes) to OLE. Unmappable types can fall
back to `Memo` via `ImportOptions.FallbackUnmappableToString = true`.

## Exporting back

```csharp
DataTable          dt    = db.GetTable("Customers").ToDataTable();
DataSet            ds    = db.ToDataSet();
IEnumerable<Customer> cs = db.GetTable("Customers").ReadRows<Customer>();
```

The `IEnumerable<T>` path is lazy via `yield`, so `Take(n)` stops early without
materialising the whole table.

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
| Secondary indexes: created with a table, kept on every write, unique enforced | ✅ Byte, Integer, Long, Large Number, Currency, Single, Double, Date/Time, Date/Time Extended, Yes/No, Text and Memo (General - Legacy and General), GUID |
| Memo / OLE long values                            | ✅      |
| PropertyMap & MSysRelationships                   | ✅      |
| Password-protected `.mdb` (Jet RC4 codec)         | ✅      |
| Password-protected `.accdb` (Agile Encryption, Office 2010+) | ✅ Read + write¹ |
| Password-protected `.accdb` (ECMA Standard Encryption, Office 2007) | ✅ Read + write |
| Password-protected `.accdb` (RC4 CryptoAPI, Office 2002–2003) | ✅ Read + write |
| Password-protected `.accdb` (Non-Standard AES, compat mode 0) | ✅ Read + write |
| Password-protected `.accdb` (Extensible Encryption) | ❌ External CSP — non-portable |
| Multi-column primary keys                         | ✅      |
| Foreign-key enforcement, with cascades (on by default) | ✅ |
| Add a column or an index to a table that has rows | ✅ While the definition fits one page; no AutoNumber column for a table with rows; no primary key |
| Create a relationship, with referential integrity and cascades | ✅ |
| Drop or rename a column, index, table or relationship | ❌ Not yet |
| Queries (`MSysQueries`)                           | ❌ Not yet |

¹ Agile write doesn't recompute the DataIntegrity HMAC, so files modified
through this library round-trip cleanly through `Database.Open` with the password
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
