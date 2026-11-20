# Building and testing

What a build from source needs, how the tests run on every framework and against the `netstandard2.0` assembly,
the Access referee tests, the test corpus, and where to report a problem. How a release is made is in
[Releasing](releasing.md).

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
- The engine's tests read back what the engine wrote. Whether Access accepts a file is what the
  [Access referee tests](#the-access-referee-tests) are for.

## The Access referee tests

`tests/JackcessDotNet.AccessTests` has Microsoft Access's own engine referee this library: ACE, through OLE DB, and
DAO read what the library wrote - by scan, through its indexes, and after DAO has compacted the file - write to it
and change it in turn, and the library reads what Access wrote. They run on .NET Framework 4.8 in a 64-bit process,
on Windows, and need one of:

- the [Access Database Engine 2016 redistributable](https://www.microsoft.com/download/details.aspx?id=54920), x64
  (what CI installs: `accessdatabaseengine_X64.exe /quiet`), or
- Access itself, 64-bit (Microsoft 365's, say).

```
dotnet build tests/JackcessDotNet.AccessTests --configuration Release
pwsh ./scripts/Invoke-AccessTests.ps1 -Parallel 2
```

`Invoke-AccessTests.ps1` runs each test class in a process of its own (two at a time here), checks that every test
listed ran, and prints the seconds each took. One class runs as any other test project:
`dotnet test --project tests/JackcessDotNet.AccessTests --configuration Release --filter-class "*CutKeysInAccessTests"`.
Run the tests one process at a time: ACE fails when two connections are opened at once in one process, so the test
project runs its tests one after another.

- **Without ACE and DAO** every test is skipped, saying what is missing. `JACKCESS_ACCESS_REQUIRED=1` makes that a
  failure instead; CI sets it, so a broken install cannot pass unrefereed.
- **The full sweeps.** A few tests sweep many values or rows and take minutes. Without `JACKCESS_FULL_SWEEPS=1` each
  runs a sample chosen so that a change to what the sweep proves still fails it; with it, every case. CI runs the
  samples on a pull request and the full sweeps on a push to `main`, every night, and in a run by hand that asks.
  The full sweeps take over an hour of test processes.
- **What the redistributable lacks.** Tests of Large Number and Date/Time Extended columns need an ACE that makes
  them (Access 2016 and 2019 and later); with the 2016 redistributable they are skipped, saying so.
- **Access 97 files** open in 32-bit ACE only, so their tests are skipped in the 64-bit process.
- `JACKCESS_ACE_PROVIDER` names another OLE DB provider than `Microsoft.ACE.OLEDB.12.0` (`Microsoft.ACE.OLEDB.16.0`,
  say).

The redistributable has crashed a test process from a thread of its own (exit code -1073740771, or an
`AccessViolationException` under `System.Data.OleDb` with no test failed), and has failed a statement with a message
whose text is missing (" in query expression '?'"). `Invoke-AccessTests.ps1` runs such a process again, and nothing
else; neither has been seen with Access itself.

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
