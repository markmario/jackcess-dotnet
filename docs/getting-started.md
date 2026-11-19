# Getting started

Installing the package, which of its assemblies each framework gets, referencing the source in place of the
package, and opening or creating a database. The [quick start](../README.md#quick-start) is a first program; the
other pages, listed in the [contents](README.md), go into each part.

## Installing

```
dotnet add package Jackcess.Net
```

The package ID is not the assembly name. The assembly and the namespace are `JackcessDotNet`, as in the
port this started from, so `using JackcessDotNet;` is unchanged; `dotnet add package JackcessDotNet` installs
that other port's package, not this one. Versions start at 2.0.0, because the public API changed after the
1.2.2 this started from. If you distribute a build, the [Apache License 2.0](../LICENSE) and [NOTICE](../NOTICE)
have to travel with it; the package carries both.

## Supported frameworks

The package has four assemblies, and NuGet gives an application the nearest one:

| The application runs on | It gets | Package dependencies | The tests run it on |
|---|---|---|---|
| .NET 10 | `net10.0` | none | .NET 10 |
| .NET 11 and later | `net11.0` | none | .NET 11 |
| .NET Framework 4.8 and 4.8.1 | `net48` | `System.Memory`, `Microsoft.Bcl.HashCode` | .NET Framework 4.8 |
| .NET 8 and 9, .NET Framework 4.6.2 to 4.7.2, anything else on .NET Standard 2.0 (Mono, Unity) | `netstandard2.0` | those two, `System.ComponentModel.Annotations` and `System.Text.Encoding.CodePages` | .NET 10 and .NET Framework 4.8 |

Every row is the whole test suite, on every pull request. The `netstandard2.0` assembly is not run on .NET 8 or
9, on .NET Framework 4.7.2, or on Mono or Unity themselves: .NET 10 and .NET Framework 4.8 stand in for them
([Testing](building-and-testing.md#testing)).

There is no `net472` assembly: an application on .NET Framework 4.7.2 gets the `netstandard2.0` one, and
.NET Framework 4.8 is the oldest with an assembly of its own. `net48` rather than `net481` because 4.8 runs on
more versions of Windows and the code is the same. The public API is the same in all four.

## Referencing the source

It can also be referenced from source. QCAT's ExportMdb.Interop takes this repository as a git submodule,
references `src/JackcessDotNet/JackcessDotNet.csproj`, and merges the built assembly into its own:

```
git submodule add https://github.com/markmario/jackcess-dotnet.git ext/jackcess-dotnet
```

```xml
<ProjectReference Include="..\..\ext\jackcess-dotnet\src\JackcessDotNet\JackcessDotNet.csproj" />
```

What a build from source writes, and the version it has: [Building](building-and-testing.md#building).

## Opening and creating a database

- `Database.Open(path)` opens an `.mdb` or an `.accdb`, of whichever format its header says
  ([What a file is](file-formats.md#what-a-file-is)). `Database.Open(path, new DatabaseOpenOptions { ... })`
  gives the password of a protected file ([Protected files](protected-files.md)) and the format to read a file as
  when its header does not say.
- `Database.Create(path)` makes an Access 2000-2003 `.mdb`, and `Database.Create(path, format)` an `.accdb` of
  Access 2007's, 2010's, 2016's or 2019's format ([Creating a file](file-formats.md#creating-a-file)).
- `Database.IsReadOnly` says whether a database can be written to: an Access 97 file, for one, is only read
  ([Access 97 files are read-only](file-formats.md#access-97-files-are-read-only)).
- `db.GetTable(name)` gives a table. `Database.TryGetTable` asks for a table without an exception when there is
  none.
- While a `Database` has a file open, Access should not be given it:
  [Sharing a file with Access](sharing.md).

[Back to contents](README.md)
