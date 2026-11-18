<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/engine-3ad3aed-rows-in-access-tables.mdb: a database Access made, with
    rows added and changed by this library as it was at commit 3ad3aed, before it kept every index.

.DESCRIPTION
    Up to that commit (the one in ExportMdb.Interop 2.1.2) this library kept a table's primary key and no other
    index: a row it inserted had no entry in the others, and a row it updated kept, in each, the entry of the row
    it had been. A relationship's rows are found through the index on the foreign key, so such a row is not found
    there (qcatco/exportmdb.interop#64). Since then an index is searched for a relationship only once it has been
    seen to hold every row (LegacyIncompleteIndexTests).

    The fixture is such a file, made by Access and by the earlier code itself, not by a reconstruction of either.
    DAO makes the database, its tables, their rows and two relationships:

      Parent     K LONG primary key, Name TEXT(20).                      Rows 1, 2, 3 and 4.
      Followers  Id LONG primary key, K LONG, Name TEXT(20).             K refers to Parent.K, cascading updates
                                                                         and deletes. Rows 10 and 11 of parent 1,
                                                                         20 of parent 2, 40 of parent 4.
      Movers     the same columns and the same kind of relationship.     Row 30, of parent 4.
      Holders    the same columns; K refers to Parent.K, no cascade.     Row 1, of no parent.

    Then this script checks commit 3ad3aed out into a worktree of its own and has that code

      insert  Followers 5, of parent 1 (it gains no entry in the index on K);
      update  Movers 30 to be of parent 3 (its entry in the index on K stays as it was, under 4: the index
              still has one entry and the table one row);
      insert  Holders 5, of parent 2.

    The file is not the same bytes on every run (it holds the time it was made), so the fixture is committed, and
    this script is the record of how. It needs the .NET SDK, Git and the Access Database Engine (for DAO); it
    changes nothing but the fixture.

.EXAMPLE
    pwsh tools/legacy-incomplete-indexes.ps1
#>
[CmdletBinding()]
param(
    # Where to write the database.
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\engine-3ad3aed-rows-in-access-tables.mdb')
)

$ErrorActionPreference = 'Stop'
$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worktree = Join-Path ([IO.Path]::GetTempPath()) ("jackcess-3ad3aed-" + [Guid]::NewGuid().ToString('N'))
$outPath  = [IO.Path]::GetFullPath($Out)

# ── Access's part ────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null
if (Test-Path $outPath) { Remove-Item $outPath }

$dbVersion40 = 64
$dbLangGeneral = ';LANGID=0x0409;CP=1252;COUNTRY=0'
$dbRelationUpdateCascade = 0x100
$dbRelationDeleteCascade = 0x1000

$engine = New-Object -ComObject DAO.DBEngine.120
$db = $engine.CreateDatabase($outPath, $dbLangGeneral, $dbVersion40)
try {
    $db.Execute('CREATE TABLE Parent (K LONG CONSTRAINT PrimaryKey PRIMARY KEY, Name TEXT(20))')
    $db.Execute('CREATE TABLE Followers (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, K LONG, Name TEXT(20))')
    $db.Execute('CREATE TABLE Movers (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, K LONG, Name TEXT(20))')
    $db.Execute('CREATE TABLE Holders (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, K LONG, Name TEXT(20))')

    foreach ($made in @(@('ParentFollowers', 'Followers', ($dbRelationUpdateCascade -bor $dbRelationDeleteCascade)),
                        @('ParentMovers', 'Movers', ($dbRelationUpdateCascade -bor $dbRelationDeleteCascade)),
                        @('ParentHolders', 'Holders', 0))) {
        $relation = $db.CreateRelation($made[0], 'Parent', $made[1], $made[2])
        $field = $relation.CreateField('K')
        $field.ForeignName = 'K'
        $relation.Fields.Append($field)
        $db.Relations.Append($relation)
    }

    foreach ($k in 1..4) { $db.Execute("INSERT INTO Parent (K, Name) VALUES ($k, 'p$k')") }
    foreach ($row in @(@(10, 1), @(11, 1), @(20, 2), @(40, 4))) {
        $db.Execute("INSERT INTO Followers (Id, K, Name) VALUES ($($row[0]), $($row[1]), 'f$($row[0])')")
    }
    $db.Execute("INSERT INTO Movers (Id, K, Name) VALUES (30, 4, 'm30')")
    $db.Execute("INSERT INTO Holders (Id, Name) VALUES (1, 'h1')")
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
        using var db = Database.Open(Environment.GetEnvironmentVariable("LEGACY_INCOMPLETE_INDEXES_OUT")!);
        db.GetTable("Followers").Insert(new Row { ["Id"] = 5, ["K"] = 1, ["Name"] = "f5" });
        db.GetTable("Movers").UpdateByPrimaryKey(30, new Row { ["K"] = 3 });
        db.GetTable("Holders").Insert(new Row { ["Id"] = 5, ["K"] = 2, ["Name"] = "h5" });
    }
}
'@
    $env:LEGACY_INCOMPLETE_INDEXES_OUT = $outPath
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
    $env:LEGACY_INCOMPLETE_INDEXES_OUT = $null
    git -C $repo worktree remove --force $worktree | Out-Null
}
"{0} ({1:N0} bytes), made by Access and written to by this library at 3ad3aed" -f $outPath, (Get-Item $outPath).Length
