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
- **Files of any size.** A table's usage map grows past its first window of pages, as Access's does.
  Before, a file could not be written past about 2 MB.
- **Inserts cost the same however large the table.** A row goes to the table's last data page, so rows
  also stay in insertion order. A page holds at most 255 rows.
- **A memo or OLE value is kept in its row only up to 64 bytes**, Access's own limit. The engine kept up
  to 1,024 bytes there, and Access showed `#Error` for those values.
- **Text index keys are the ones Access writes**, for the General Legacy sort order (Access 2000-2007).
- **Tables the engine creates are ones Access lists, reads, seeks in and adds to.**
- **Pages the engine adds are taken out of the database's free-page map**, so Access does not hand them
  out again and write over them.
- **The test corpus is in the repository**, pinned by checksum. See [tests/corpus](tests/corpus/README.md).

Limits to know about:

- The engine keeps a table's **primary key** index up to date. It maintains **no other index** on
  insert, and has no API for one. Access trusts an index when one covers a query, so a table with other
  indexes gives wrong answers in Access after the engine adds rows to it.
- Text keys are written only in the General Legacy order. The engine refuses, before changing anything,
  to create a table in a file whose catalog sorts in Access 2010's General order, and to insert, update
  or delete in a table whose primary key it cannot write keys for: text in another order, or a type
  other than Byte, Integer, Long, Text or GUID.

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

## Foreign-key enforcement on insert (opt-in)

```csharp
using var db = Database.Open("Northwind.mdb");
db.EnforceForeignKeys = true;                  // default: false

// MSysRelationships drives validation. Any Insert whose FK column doesn't
// match an existing parent row throws InvalidOperationException.
orders.Insert(new Row { ["CustomerID"] = "INVALID", ... });
//                                                    ^ throws "Foreign-key violation in
//                                                              relationship 'CustomersOrders'..."
```

Null FK values are allowed (SQL semantics). Restrict-only — no cascade.

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
| Create new `.mdb` files (Jet 4 / Jet 3)           | ✅      |
| Row CRUD + primary-key B-tree index               | ✅      |
| Other (secondary) indexes kept up to date on write | ❌ See [limits](#what-this-fork-changes) |
| Memo / OLE long values                            | ✅      |
| PropertyMap & MSysRelationships                   | ✅      |
| Password-protected `.mdb` (Jet RC4 codec)         | ✅      |
| Password-protected `.accdb` (Agile Encryption, Office 2010+) | ✅ Read + write¹ |
| Password-protected `.accdb` (ECMA Standard Encryption, Office 2007) | ✅ Read + write |
| Password-protected `.accdb` (RC4 CryptoAPI, Office 2002–2003) | ✅ Read + write |
| Password-protected `.accdb` (Non-Standard AES, compat mode 0) | ✅ Read + write |
| Password-protected `.accdb` (Extensible Encryption) | ❌ External CSP — non-portable |
| Multi-column primary keys                         | ✅      |
| Foreign-key enforcement on insert (opt-in)        | ✅ Restrict-only |
| Queries (`MSysQueries`)                           | ❌ Not yet |

¹ Agile write doesn't recompute the DataIntegrity HMAC, so files modified
through this library round-trip cleanly through `Database.Open(path, password)`
but Office Access may flag a stale integrity hash. Matches the upstream
`jackcess-encrypt` limitation.

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
