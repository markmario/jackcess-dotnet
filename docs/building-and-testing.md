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

**Line endings.** Text files are LF in the repository and in every working copy, Windows included
(`.gitattributes`), and `.editorconfig` tells an editor so; a batch file, should one be added, is CRLF. A clone
made before d9078d0, which made them LF, can still hold CRLF copies, and `git add` of one then stops with "CRLF
would be replaced by LF" where `core.safecrlf` is set. In such a clone, with no changes of your own in it,
`git rm -r --cached -q . && git reset --hard` makes every file LF once. `git blame` skips d9078d0 on GitHub
(`.git-blame-ignore-revs`), and locally after `git config blame.ignoreRevsFile .git-blame-ignore-revs`.

## Testing

- The tests are xUnit.net v3 and run on .NET 10, .NET 11 and .NET Framework 4.8: `dotnet test` runs all three,
  and `--framework net48` runs one.
- The `netstandard2.0` assembly is not one a test project can target, so the same tests run against it with
  `-p:NetStandardAsset=true`, on .NET 10 (as an application on .NET 8 or 9 loads it) and on .NET Framework 4.8
  (as one on 4.7.2 does). `LibraryAssemblyTests` fails if the assembly loaded is not the one the build was for.
  That build goes to `bin/Release-netstandard`, beside the ordinary one:
  `dotnet test --project tests/JackcessDotNet.Tests --configuration Release --framework net10.0 -p:NetStandardAsset=true`
- `-p:TestX86=true` runs the .NET Framework 4.8 tests in a 32-bit process, where .NET Framework computes doubles
  with the x87 unit, whose precision another component in the process can change (ACE leaves it at 64 bits; #98).
  That build goes to `bin/Release-x86`:
  `dotnet test --project tests/JackcessDotNet.Tests --configuration Release --framework net48 -p:TestX86=true`.
  The Access referee tests take the same switch, and then need 32-bit ACE: Access itself, 32-bit, or the
  redistributable's x86 installer, which CI installs for its 32-bit shards. Where that is Access 2007's ACE 12, which
  some machines have beside a 64-bit ACE 16, a test that needs an Access 2010 file is skipped, saying so; a test that
  pins what ACE does pins what the process's own ACE does, or is skipped under one not measured (#101).
  `Invoke-AccessTests.ps1 -X86` runs that build.
- Those are the six configurations, and CI runs each on every pull request, the .NET Framework ones split into
  shards that run side by side (`scripts/Invoke-TestShard.ps1`); which assembly each framework gets is in
  [Supported frameworks](getting-started.md#supported-frameworks).
- The shards are balanced by tables of the seconds each test class took on CI, in `scripts/Invoke-TestShard.ps1`
  and, for the Access referee tests, `scripts/Invoke-AccessTests.ps1`. A new test class needs a row in each table
  that runs it: on a pull request, a class with none fails its job once its tests have run, with an error that
  names the row to add and gives the seconds the class took.
- Options are the platform's and xUnit's, not VSTest's. One class: `dotnet test --filter-class "*UsageMapTests"`.
  A TRX report: `--report-xunit-trx --report-xunit-trx-filename test-results.trx`, written to `TestResults/`.
- A few tests are skipped unless an environment variable gives them what they need, each saying so:
  `JACKCESS_ENCRYPT_FIXTURES` names the `src/test/data` folder of the jackcess-encrypt project, whose protected
  files are not in this repository, and `JACKCESS_PAST_65536_PAGES`, set to anything, runs the tests that grow a
  file past 270 MB.
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

CI runs them in a 32-bit process too, built with `-p:TestX86=true`, on runners of their own with the
redistributable's x86 installer (`accessdatabaseengine.exe /quiet`): 32-bit ACE 16 and DAO 16.

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
- **Access 97 files** open in 32-bit ACE 12 only, so their tests are skipped, saying so, in a 64-bit process and
  under the redistributable's 32-bit ACE 16, which says "Cannot open a database created with a previous version of
  your application" of one. CI has no ACE that opens one, so they run only on a machine with Access 2007's ACE 12.
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
versions of the engine. Neither the solution nor CI builds it; it uses only the public API, and is run on its
own:

```
dotnet run --project tools/CorpusDump --configuration Release -- tests/corpus <output folder>
```

## Reporting problems

Report problems and track work in this repository's [issues](https://github.com/markmario/jackcess-dotnet/issues).

[Back to contents](README.md)
