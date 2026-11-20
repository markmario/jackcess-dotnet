# Jackcess.Net

[![NuGet](https://img.shields.io/nuget/v/Jackcess.Net?logo=nuget)](https://www.nuget.org/packages/Jackcess.Net)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](https://github.com/markmario/jackcess-dotnet/blob/main/LICENSE)

Pure managed library for reading and writing Microsoft Access (`.mdb` / `.accdb`) files.
No ODBC, no ACE drivers, no native dependencies. It builds for .NET 10, .NET 11, .NET Framework 4.8
and .NET Standard 2.0. It is a C# port of the [`spannm/jackcess`](https://github.com/spannm/jackcess) Java
project, and the files it writes are checked against Access's own engine (ACE), not only read back by the library
that wrote them.

This project started from release 1.2.2 of
[`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet) and is now
maintained on its own, with its own package, `Jackcess.Net`. It is not a release of the Jackcess project, and
neither the Jackcess authors nor Mehran Ghanizadeh maintain or endorse it. How it differs from both:
[Differences from Java Jackcess and from upstream](https://github.com/markmario/jackcess-dotnet/blob/main/docs/differences.md).

## Installing

```
dotnet add package Jackcess.Net
```

The package ID is not the assembly name: the assembly and the namespace are `JackcessDotNet`
(`using JackcessDotNet;`), and `dotnet add package JackcessDotNet` installs another port's package, not this one.
It has assemblies for .NET 10 and later (`net10.0`, `net11.0`) and .NET Framework 4.8 and 4.8.1 (`net48`), and
a `netstandard2.0` one for .NET 8 and 9, .NET Framework 4.6.2 to 4.7.2 and anything else on .NET Standard 2.0:
[Getting started](https://github.com/markmario/jackcess-dotnet/blob/main/docs/getting-started.md) has which
assembly each gets, where each is tested, and what a distributed build must carry.

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

// A row that is read is updated or deleted by where it was read, whatever key its table has.
read["Born"] = new DateTime(1990, 4, 1);
people.Update(read);
```

## What it does

- **Files.** Access 97 to Access 2019 files are read; `.mdb` (Access 2000-2003) and `.accdb` (Access 2007, 2010,
  2016 and 2019) files are created and written. An Access 97 file is only read.
- **Rows.** Inserted, updated in their place and deleted, through a table or a cursor; a batch goes in whole or
  not at all. Cursors go both ways, and index cursors through an index's rows, between two entries or all.
- **Indexes.** Every index is kept on every write, unique indexes are enforced, and text keys are the ones
  Access writes.
- **Relationships.** Foreign keys are enforced, with cascades, unless turned off; relationships are created and
  dropped.
- **Schema changes.** A table with rows is given columns, indexes and a primary key; columns, indexes, tables
  and relationships are dropped, and columns, indexes and tables renamed.
- **Properties.** A table's, a column's and the database's are read and written; Required and Allow Zero
  Length are held to.
- **Protected files.** An `.mdb` with a database password or encoded by Access, and an `.accdb` encrypted with
  a password.
- **Sharing.** A file open to be written keeps Access and other writers out; one opened `ReadOnly` opens beside
  Access.
- **Import and export.** `DataTable`, `DataSet` and `IEnumerable<T>`.
- **Not yet.** Saved queries are not read, and a few things are refused rather than written as Access would not
  write them: the list is in
  [What is and is not supported](https://github.com/markmario/jackcess-dotnet/blob/main/docs/supported-features.md).

## Documentation

The [documentation](https://github.com/markmario/jackcess-dotnet/blob/main/docs/README.md) has a page for each
topic:

- [Getting started](https://github.com/markmario/jackcess-dotnet/blob/main/docs/getting-started.md)
- [Rows and cursors](https://github.com/markmario/jackcess-dotnet/blob/main/docs/cursors-and-rows.md)
- [Data types](https://github.com/markmario/jackcess-dotnet/blob/main/docs/data-types.md)
- [Tables and schema changes](https://github.com/markmario/jackcess-dotnet/blob/main/docs/tables-and-schema.md)
- [Indexes and keys](https://github.com/markmario/jackcess-dotnet/blob/main/docs/indexes.md)
- [Relationships and foreign keys](https://github.com/markmario/jackcess-dotnet/blob/main/docs/relationships.md)
- [Properties](https://github.com/markmario/jackcess-dotnet/blob/main/docs/properties.md)
- [Importing and exporting](https://github.com/markmario/jackcess-dotnet/blob/main/docs/importing-and-exporting.md)
- [File formats](https://github.com/markmario/jackcess-dotnet/blob/main/docs/file-formats.md)
- [Protected files](https://github.com/markmario/jackcess-dotnet/blob/main/docs/protected-files.md)
- [Sharing a file with Access](https://github.com/markmario/jackcess-dotnet/blob/main/docs/sharing.md)
- [Storage: rows, long values and file growth](https://github.com/markmario/jackcess-dotnet/blob/main/docs/storage.md)
- [Files written by earlier versions](https://github.com/markmario/jackcess-dotnet/blob/main/docs/earlier-versions.md)
- [What is and is not supported](https://github.com/markmario/jackcess-dotnet/blob/main/docs/supported-features.md)
- [Differences from Java Jackcess and from upstream](https://github.com/markmario/jackcess-dotnet/blob/main/docs/differences.md)
- [Building and testing](https://github.com/markmario/jackcess-dotnet/blob/main/docs/building-and-testing.md)
- [Releasing](https://github.com/markmario/jackcess-dotnet/blob/main/docs/releasing.md)

## Building

The .NET 11 SDK builds every target: `dotnet build JackcessDotNet.slnx` builds them, and `dotnet test
JackcessDotNet.slnx` runs the tests on .NET 10, .NET 11 and .NET Framework 4.8.
[Building and testing](https://github.com/markmario/jackcess-dotnet/blob/main/docs/building-and-testing.md) has
how the `netstandard2.0` assembly is tested, and the rest. Problems are reported in this repository's
[issues](https://github.com/markmario/jackcess-dotnet/issues).

## Version history

[CHANGELOG.md](https://github.com/markmario/jackcess-dotnet/blob/main/CHANGELOG.md). Up to 1.2.2 it is the history
of the port this started from; what has changed since is in
[What this fork changes](https://github.com/markmario/jackcess-dotnet/blob/main/docs/differences.md#what-this-fork-changes)
and in this repository's pull requests.

## Credits

This project would not exist without the years of reverse-engineering work
done by James Ahlborn (original Jackcess) and Markus Spann (maintained fork
at [`spannm/jackcess`](https://github.com/spannm/jackcess)). The Jet binary
format is documented effectively only through their Java source. The C# port
this repository started from is Mehran Ghanizadeh's
[`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet).

## License

[Apache License 2.0](https://github.com/markmario/jackcess-dotnet/blob/main/LICENSE) — same as the upstream
Jackcess project. [NOTICE](https://github.com/markmario/jackcess-dotnet/blob/main/NOTICE) records the works this
is derived from.
