<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/engine-3ad3aed-rows-in-unique-indexes.mdb: a database Access made, with
    rows added and changed by this library as it was at commit 3ad3aed, which a unique index of theirs does not
    hold - a primary key among them.

.DESCRIPTION
    Up to that commit (the one in ExportMdb.Interop 2.1.2) this library kept a table's primary key and no other
    index: a row it inserted had no entry in the others, and a row it updated kept, in each, the entry of the row
    it had been. And it kept the primary key under the value a row was found by: a row whose primary key it
    changed is in the key under the value it had before. A unique index that does not hold a row cannot say that
    a second row with that row's values is a duplicate (qcatco/exportmdb.interop#73). Since then a unique index
    answers for duplicates only with the rows it has been seen not to hold (LegacyUniqueIndexTests).

    The fixture is such a file, made by Access and by the earlier code itself, not by a reconstruction of either.
    DAO makes the database, its tables and their rows:

      Codes     Id LONG primary key, Code LONG in the unique index CodeUnique, Name TEXT(20).
                Rows (1, 100) and (2, 200).
      Renamed   Id LONG primary key, Name TEXT(20).   Rows 1 and 2.

    Then this script checks commit 3ad3aed out into a worktree of its own and has that code

      insert  Codes (3, 300): it gains no entry in CodeUnique;
      update  Codes 2 to Code 250: its entry in CodeUnique stays as it was, under 200, pointing where the row
              was, and it gains none under 250;
      update  Renamed 2 to Id 7: its entry in the primary key is under 2, pointing at the row that is now 7.

    The file is not the same bytes on every run (it holds the time it was made), so the fixture is committed, and
    this script is the record of how. It needs the .NET SDK, Git and the Access Database Engine (for DAO); it
    changes nothing but the fixture.

.EXAMPLE
    pwsh tools/legacy-unique-indexes.ps1
#>
[CmdletBinding()]
param(
    # Where to write the database.
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\engine-3ad3aed-rows-in-unique-indexes.mdb'),
    # Where the earlier code is checked out, and removed from afterwards.
    [string]$Worktree = (Join-Path ([IO.Path]::GetTempPath()) ("jackcess-3ad3aed-" + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worktree = [IO.Path]::GetFullPath($Worktree)
$outPath  = [IO.Path]::GetFullPath($Out)

# ── Access's part ────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null
if (Test-Path $outPath) { Remove-Item $outPath }

$dbVersion40 = 64
$dbLangGeneral = ';LANGID=0x0409;CP=1252;COUNTRY=0'

$engine = New-Object -ComObject DAO.DBEngine.120
$db = $engine.CreateDatabase($outPath, $dbLangGeneral, $dbVersion40)
try {
    $db.Execute('CREATE TABLE Codes (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Code LONG, Name TEXT(20))')
    $db.Execute('CREATE UNIQUE INDEX CodeUnique ON Codes (Code)')
    $db.Execute('CREATE TABLE Renamed (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Name TEXT(20))')

    $db.Execute("INSERT INTO Codes (Id, Code, Name) VALUES (1, 100, 'c1')")
    $db.Execute("INSERT INTO Codes (Id, Code, Name) VALUES (2, 200, 'c2')")
    $db.Execute("INSERT INTO Renamed (Id, Name) VALUES (1, 'r1')")
    $db.Execute("INSERT INTO Renamed (Id, Name) VALUES (2, 'r2')")
}
finally {
    $db.Close()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($engine)
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

# ── The earlier code's part ──────────────────────────────────────────────────
git -C $repo worktree add --detach $worktree 3ad3aed | Out-Null
try {
    # The earlier code is driven from its own test project, which has its runner.
    Set-Content (Join-Path $worktree 'tests\JackcessDotNet.Tests\WriteToAccessTables.cs') @'
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class WriteToAccessTables
{
    [Fact]
    public void Write()
    {
        using var db = Database.Open(Environment.GetEnvironmentVariable("LEGACY_UNIQUE_INDEXES_OUT")!);
        db.GetTable("Codes").Insert(new Row { ["Id"] = 3, ["Code"] = 300, ["Name"] = "c3" });
        db.GetTable("Codes").UpdateByPrimaryKey(2, new Row { ["Code"] = 250 });
        db.GetTable("Renamed").UpdateByPrimaryKey(2, new Row { ["Id"] = 7 });
    }
}
'@
    $env:LEGACY_UNIQUE_INDEXES_OUT = $outPath
    $before = (Get-FileHash $outPath).Hash
    # From the worktree, so that its own global.json says how its tests are run.
    Push-Location $worktree
    try {
        dotnet test 'tests\JackcessDotNet.Tests\JackcessDotNet.Tests.csproj' --configuration Release --nologo -v quiet `
            --filter 'FullyQualifiedName=JackcessDotNet.Tests.WriteToAccessTables.Write'
    }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash $outPath).Hash -eq $before) { throw 'The earlier code did not write to the database.' }
}
finally {
    $env:LEGACY_UNIQUE_INDEXES_OUT = $null
    git -C $repo worktree remove --force $worktree | Out-Null
}
"{0} ({1:N0} bytes), made by Access and written to by this library at 3ad3aed" -f $outPath, (Get-Item $outPath).Length
