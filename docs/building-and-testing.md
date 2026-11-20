# Building and testing

What a build from source needs, how the tests run on every framework and against the `netstandard2.0` assembly,
the test corpus, and where to report a problem. How a release is made is in [Releasing](releasing.md).

## Building

You need the **.NET 11 SDK**, which builds every target, `net11.0` included; an earlier SDK cannot restore the
project, and `global.json` asks for 11.0.100 or later. The tests run on Microsoft Testing Platform (`global.json`
selects it), which `dotnet test` runs from the 10 SDK on. The .NET Framework 4.8 tests run on Windows only.

```
dotnet build JackcessDotNet.slnx --configuration Release
dotnet test JackcessDotNet.slnx --configuration Release
```

A build here writes `Jackcess.Net.0.0.0-local.nupkg` to `artifacts/`: only a release build has a real version
(see [Releasing](releasing.md)).

## Testing

- The tests are xUnit.net v3 and run on .NET 10, .NET 11 and .NET Framework 4.8: `dotnet test` runs all three,
  and `--framework net48` runs one.
- The `netstandard2.0` assembly is not one a test project can target, so the same tests run against it with
  `-p:NetStandardAsset=true`, on .NET 10 (as an application on .NET 8 or 9 loads it) and on .NET Framework 4.8
  (as one on 4.7.2 does). `LibraryAssemblyTests` fails if the assembly loaded is not the one the build was for.
  That build goes to `bin/Release-netstandard`, beside the ordinary one:
  `dotnet test --project tests/JackcessDotNet.Tests --configuration Release --framework net10.0 -p:NetStandardAsset=true`
- Those are the five configurations, and CI runs each on every pull request, each split into shards that run side
  by side (`scripts/Invoke-TestShard.ps1`); which assembly each framework gets is in
  [Supported frameworks](getting-started.md#supported-frameworks).
- Options are the platform's and xUnit's, not VSTest's. One class: `dotnet test --filter-class "*UsageMapTests"`.
  A TRX report: `--report-xunit-trx --report-xunit-trx-filename test-results.trx`, written to `TestResults/`.
- The engine's tests read back what the engine wrote. Whether Access accepts a file is tested where ACE
  is installed, in ExportMdb.Interop's suite.

## The test corpus

`./tools/corpus.ps1` checks every corpus file against `tests/corpus/MANIFEST.sha256`. CI runs it before building.
Where the corpus comes from and why it is in the repository: [tests/corpus](../tests/corpus/README.md). The test
files Access and earlier versions of this library made are described beside what they test: in
[Files written by earlier versions](earlier-versions.md#test-files),
[Keys longer than 510 bytes](indexes.md#keys-longer-than-510-bytes),
[Access 97 files are read-only](file-formats.md#access-97-files-are-read-only) and
[Protected files](protected-files.md).

## Comparing two versions of the engine

`tools/CorpusDump` writes what the engine reads of every table of a folder of files as text, for comparing two
versions of the engine. Nothing builds it, and it does not compile against the public API: it calls
`ListTables` and `ReadAllRows`, whose places `GetTableNames` and `GetSystemTableNames`, and `Table.Rows`, have
taken ([#47]).

## Reporting problems

Report problems and track work in this repository's [issues](https://github.com/markmario/jackcess-dotnet/issues).
Some comments in the source still cite issues of ExportMdb.Interop's tracker (`qcatco/exportmdb.interop`), which
is private: the work was tracked there until this repository had its own, and [#47] replaces those citations with
this repository's issues.

[Back to contents](README.md)

[#47]: https://github.com/markmario/jackcess-dotnet/issues/47
