# Guidance for coding agents

Coding agents read this at the start of a session: Codex and others read this file, and Claude Code reads it
through `CLAUDE.md`, which imports it. It says how work is done in this repository. What the library does is in
[docs](docs/README.md), and what is still to do is in the
[issues](https://github.com/markmario/jackcess-dotnet/issues). "The owner" below is the repository's owner, who
decides its releases.

## What this is

Jackcess.Net reads and writes Microsoft Access files (`.mdb` and `.accdb`) in managed code, with no ACE or ODBC
driver. It is a C# port of Java Jackcess ([`spannm/jackcess`](https://github.com/spannm/jackcess)), started from
release 1.2.2 of `mehran-ghanizadeh/jackcess-dotnet` and maintained on its own.

- The package is `Jackcess.Net`. The assembly and the namespace are `JackcessDotNet`.
- It builds for `net10.0`, `net11.0`, `net48` and `netstandard2.0`, with the same public API in all four.
- The standard it is held to: a file it writes is one Access opens, reads, queries through its indexes, writes
  to and compacts. Being right by Access comes before speed and before new features.
- The repository is public. `main` is the only long-lived branch, and a merged pull request's branch is deleted.

## How work is done here

1. **Access is the referee, and Java Jackcess comes second.** Behaviour follows Microsoft Access's own engine -
   ACE, through OLE DB, and DAO - and then Java Jackcess. Where the two differ, follow Access and say so in
   [Differences](docs/differences.md). Read Jackcess's source to learn what it does; do not guess. Other
   implementations of the format, such as
   [JustyBase.UCanAccessCs](https://github.com/justybase/JustyBase.UCanAccessCs) and mdbtools' `HACKING` notes,
   are places to learn from when stuck. They are not referees, and no code is copied from them.
2. **Measure before writing code.** For anything about the bytes of a file: make the thing in Access (DAO through
   the test helpers, or a script like those in `tools/`), read what Access wrote, and only then write code. The
   pull request says what was measured, with which Access, and what that decided. "Measured" has one meaning
   here: [Words these pages use](docs/README.md#words-these-pages-use).
3. **Reading a file back with this library proves nothing about Access.** This library, and other readers, have
   read back files that Access showed `#Error` for or would not open. Only Access reading a file shows that
   Access accepts it, so whatever is written to a file has an Access referee test: Access reads it by scan and
   through each index involved, writes to it, and reads it again after compacting.
4. **Refuse before writing, and never half-write.** What cannot be written as Access would write it is refused
   with a clear exception before a byte changes. A change of several pages that can fail part-way runs inside an
   undo scope (`PageFile.BeginUndo`; `Tables/SchemaChange.cs` shows how), so a failure leaves the file byte for
   byte as it was. `PageFile.WritesBeforeFailure` lets a test fail a chosen write.
5. **C# and .NET idiom, not transliterated Java.** Port the behaviour and write it as C#: no `Impl` class beside
   an interface with one implementation, no pairs of get and set methods, no builders. The public API's
   conventions:
   - definitions (`ColumnDefinition`, `IndexDefinition`) and options objects (`DatabaseOpenOptions`), not
     builders or optional parameters;
   - `ArgumentException` for a caller's bad argument, such as a table, column or index that is not there;
     `ConstraintViolationException` for data that breaks a rule of the database; `NotSupportedException` for a
     file or feature the library does not handle;
   - `Try...` for the form that does not throw; null, never `DBNull`; no `[Obsolete]` member that forwards to a
     new name.
6. **Correctness first.** Index correctness, everything written matching what Access writes, and bug fixes come
   before new features. Work is ranked by what the applications that depend on this library use. A feature
   nothing uses yet waits, with its state recorded on its issue.
7. **A smaller change that is right beats a larger one that is not refereed.** When a task turns out to be more
   than can be done correctly, do the part that can be proven against Access, refuse the rest cleanly, and say
   exactly what is left.
8. **Tests that can fail, and that are fast.** Every fix has a test that fails without it. No test writes a very
   large file or proves a size limit at its real size: a limit is measured once by hand and recorded in the pull
   request and the docs with the script that measured it, and its refusal is tested in milliseconds against a
   lowered limit (`PageFile.MaxLength`, for the 2 GB limit). A referee test runs in seconds. Once a point is
   proven, deliver: do not add more proof of it.
9. **No regressions and no exclusions.** Existing tests pass unchanged, unless what one pins is the defect being
   fixed; then it changes in the same commit and the pull request says so. A test that fails only on `net48` or
   against the `netstandard2.0` assembly is a finding about the library on that target: fix the library, and
   never leave a test out of a configuration to make it pass.
10. **Outstanding work is an issue.** Whatever is unresolved when a piece of work ends - a follow-up, a known
    defect, a decision waiting on the owner - becomes an issue in this repository, cited by its number wherever
    it is mentioned. It does not live in a file, a commit message or a closing summary.
11. **Report plainly.** Say what was measured, what was run and how it ended, what failed, and what was not run.
    A skipped test is not a passed one, and a result that was not checked is said to be unchecked.

## Building and testing

The .NET 11 SDK builds every target; `global.json` asks for 11.0.100 or later. The whole of it is in
[Building and testing](docs/building-and-testing.md). The commands used most:

```
dotnet build JackcessDotNet.slnx --configuration Release
dotnet test JackcessDotNet.slnx --configuration Release
dotnet test --project tests/JackcessDotNet.Tests --configuration Release --framework net48 --filter-class "*UsageMapTests"
dotnet test --project tests/JackcessDotNet.AccessTests --configuration Release --filter-class "*CutKeysInAccessTests"
pwsh ./scripts/Invoke-AccessTests.ps1 -Parallel 2
pwsh ./tools/corpus.ps1
```

Before a pull request, on its final state: a clean build with no warnings, the whole of
`tests/JackcessDotNet.Tests` in each of the six configurations CI runs, and the Access referee tests.

| Configuration | After `dotnet test --project tests/JackcessDotNet.Tests --configuration Release` |
| --- | --- |
| .NET 10 | `--framework net10.0` |
| .NET 11 | `--framework net11.0` |
| .NET Framework 4.8 | `--framework net48` |
| The `netstandard2.0` assembly on .NET 10 | `--framework net10.0 -p:NetStandardAsset=true` |
| The `netstandard2.0` assembly on .NET Framework 4.8 | `--framework net48 -p:NetStandardAsset=true` |
| .NET Framework 4.8 in a 32-bit process | `--framework net48 -p:TestX86=true` |

- **The options are Microsoft Testing Platform's and xUnit.net v3's, not VSTest's:** `--filter-class`,
  `--filter-method`, `--report-xunit-trx`.
- **`--no-build` runs whatever was built last.** Build again, or delete `bin` and `obj`, before reading a result
  from it.
- **What needs Windows.** The .NET Framework 4.8 tests and the Access referee tests run on Windows only. The
  referee tests also need Access, or the Access Database Engine 2016 redistributable, in 64 bits. Without it
  every one of them is skipped, saying what is missing. Then say that the referee tests were not run, and leave
  them to CI, which installs the redistributable and sets `JACKCESS_ACCESS_REQUIRED=1` so that a missing referee
  fails.
- **One process to a referee class.** ACE refuses two connections opened at once in one process, so the tests in
  a process run one at a time, and `scripts/Invoke-AccessTests.ps1` gives each class a process of its own.
- **The redistributable sometimes dies.** It has killed a test process (exit code -1073740771, or an
  `AccessViolationException` under `System.Data.OleDb` with no test failed) and failed a statement with a
  message whose text is missing (" in query expression '?'"). The script runs such a process again. Run it
  again; do not change code to get round it.
- **The long sweeps.** A referee test that sweeps many values runs a sample unless `JACKCESS_FULL_SWEEPS=1`. A
  pull request runs the samples; a push to `main`, the nightly run and a run by hand that asks run them whole.
- **Test code compiles for .NET Framework 4.8 too:** `tests/JackcessDotNet.Tests/FrameworkPolyfills.cs`.
- **A local build is `0.0.0-local`, and stays so.** Never pass `-p:Version` to a local build or test run, and
  never make a package with a version a release could have: the machine's NuGet packages folder is shared, and a
  local package with a real version is restored in place of the published one, with nothing in any log to show
  it.

## Where things are

- `src/JackcessDotNet`: the library. `IO` has the page file and its undo; `Pages` the rows, long values and usage
  maps; `Indexes` the index trees and their keys; `Tables` the `Database`, the `Table`, schema changes and
  foreign keys; `Cursors`, `Schema`, `Properties` and `Codecs` what their names say. `PublicAPI.Shipped.txt` and
  `PublicAPI.Unshipped.txt` record the public API.
- `tests/JackcessDotNet.Tests`: the library's own tests, which see its internals.
- `tests/JackcessDotNet.AccessTests`: the Access referee tests (`*InAccessTests.cs`), with their helpers in
  `Support/` (`AccessReferee`, `Dao`, `Sweeps`).
- `tests/corpus`: files Access and others made, pinned by checksum.
- `tools/`: the scripts that made test files with Access, ACE or DAO. They are the record of what was measured.
- `scripts/`: what CI and a release run.
- `docs/`: one page to a topic.
- `.github/workflows`: `ci.yml` builds and tests, `publish.yml` releases, and `nuget-publish.yml` is retired and
  kept disabled.

## What a change carries

- A test that fails without it, and an Access referee test for anything written to a file.
- `PublicAPI.Unshipped.txt`, changed in the same commit as the public API: the build fails when the two
  disagree. A change that removes or changes public API a release has shipped also raises
  `MinimumReleaseVersion` in the project file to the next major.
- XML documentation on every public type and member, without which the build fails: what it does, returns and
  throws, in plain English and the present tense.
- A line under "Unreleased" in `CHANGELOG.md`, in the file's style.
- Its topic page under `docs/`. [Where a change is documented](docs/README.md#where-a-change-is-documented) says
  which page, and how headings, links and issue citations are written. `README.md` is the package's readme on
  nuget.org: its links are absolute, and it changes only for a new page or a new headline feature.
- Comments that say what the code does and why. XML documentation and comments carry no history ("now", "no
  longer") and no talk of tasks, reviews or agents. An issue of this repository is cited as `#NN`, and no other
  tracker is cited.
- Text files are LF (`.gitattributes`). A file's byte-order mark stays as it is.
- Nothing else: no rename, reformatting or move that the task does not need.

## Pull requests, review and merging

- Work on a branch from `main` and open a pull request. Nothing is pushed to `main` directly.
- Commits are reviewable steps, one concern each. The subject says what changed; the body says why, and what was
  measured. No person's name and no email address goes in a commit message, a pull request or an issue.
- Where commit signing is set up, never bypass or disable it. If a commit cannot be signed, leave the change
  staged and say so.
- Write `Fixes #NN` only where the issue is wholly done, and `Part of #NN` otherwise, with exactly what remains.
  GitHub closes an issue only for a keyword before each number: write `Fixes #36, fixes #56`.
- The description says what a caller can observe, what was measured from Access, the test totals in each
  configuration on the final state, and anything that failed or was not run.
- Every pull request is reviewed before it merges, by a reviewer that did not write it and that is given the
  change, not the author's conclusion. Where an automated reviewer comments, each finding is fixed or shown to
  be wrong. A defect that is reported is fixed, or becomes an issue.
- A pull request merges when it is reviewed and CI is green, with a merge commit. An agent merges only when the
  owner has said that reviewed, green work is to be merged.
- When `main` moves under a branch, merge it in. Do not rebase a branch that is under review.
- CI runs on GitHub-hosted runners only: do not add or target a self-hosted one. It is kept fast by splitting
  and sampling, not by leaving tests out.
- Never cancel a run on `main`: each commit there has a result. A job that failed can be run again.

## Releases

How a release is made is in [Releasing](docs/releasing.md). What matters to an agent:

- The tag is the version. There is no version to edit and no bump commit.
- The owner decides releases. An agent never pushes a release tag (`vX.Y.Z`). A pre-release tag
  (`vX.Y.Z-suffix`) is pushed only with the owner's leave, on `main`'s merge commit, by the session coordinating
  the work and never by a sub-agent, and the owner is told the tag and the commit. A tag is never moved or used
  again, and a published version is never replaced.
- Publishing waits for a person to approve the `nuget` environment. An agent never approves or rejects it,
  whatever its credentials allow.
- `.github/workflows/nuget-publish.yml` stays disabled: never enable it, delete it or put a workflow back at
  that path.
- `.github/workflows`, `scripts/`, `LICENSE` and `NOTICE` decide what is published and on what terms. Change
  them only when that is the task, and say so in the pull request.

## When several agents work at once

- One session coordinates. It splits the work into streams, and gives each a written brief, a clone or worktree
  of its own and a branch of its own. No agent writes in another's working copy, or in the owner's.
- A stream keeps its change to what its issues need, so that the branches merge.
- A stream does not merge, tag or change a workflow. When a permission system refuses a command, it does not
  work round the refusal: it reports it.
- A stream ends with a report, which is all the coordinator sees: pull request numbers and head commits; the
  test totals in each configuration on the final state; what was measured from Access and what that decided;
  every change a caller can observe; every judgement call; what remains of each issue; and anything it is unsure
  of.
- The coordinator checks a report against the repository before passing it on. A report is a claim, not a
  result.
