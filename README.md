<!-- qcat-repository-image:start -->
<p align="center">
  <img src=".github/repository-card.png" alt="jackcess-dotnet repository image" width="320" />
</p>
<!-- qcat-repository-image:end -->

# JackcessDotNet

[![NuGet](https://img.shields.io/nuget/v/JackcessDotNet?logo=nuget&label=NuGet)](https://www.nuget.org/packages/JackcessDotNet)
[![Downloads](https://img.shields.io/nuget/dt/JackcessDotNet?logo=nuget&label=Downloads)](https://www.nuget.org/packages/JackcessDotNet)
[![GitHub](https://img.shields.io/badge/GitHub-mehran--ghanizadeh-181717?logo=github)](https://github.com/mehran-ghanizadeh)
[![Repo](https://img.shields.io/badge/repo-jackcess--dotnet-181717?logo=github)](https://github.com/mehran-ghanizadeh/jackcess-dotnet)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

Pure .NET 8 library for reading and writing Microsoft Access (`.mdb` / `.accdb`) files.
No ODBC, no ACE drivers, no native dependencies — runs anywhere .NET 8 runs
(Windows, Linux, macOS, containers).

This is a C# port of the [`spannm/jackcess`](https://github.com/spannm/jackcess) Java
project (a maintained fork of the original [Jackcess](https://jackcess.sourceforge.io/)
by James Ahlborn). The upstream Java project remains the source of truth for the Jet
file format; this port tracks its behaviour and reuses its test corpus for
verification.

## Install

```
dotnet add package JackcessDotNet
```

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
| Create new `.mdb` files (Jet 4 / Jet 3)           | ✅      |
| Row CRUD + B-tree indexes (single-column PK)      | ✅      |
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
  (qcatco/exportmdb.interop#26).
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

A key longer than 510 bytes - of one column or of several together - is cut
there and ended in a two-byte digest, as Access writes it. Two values cut to
the same entry are duplicates to a unique index, as they are to Access. A lookup of such a value scans the table instead of trusting
the index, since Jackcess and earlier versions of this library wrote the key
whole.

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

See [CHANGELOG.md](CHANGELOG.md) — also shipped inside the `.nupkg`.

## Credits

This project would not exist without the years of reverse-engineering work
done by James Ahlborn (original Jackcess) and Markus Spann (maintained fork
at [`spannm/jackcess`](https://github.com/spannm/jackcess)). The Jet binary
format is documented effectively only through their Java source.

## License

[Apache License 2.0](LICENSE) — same as the upstream Jackcess project.
