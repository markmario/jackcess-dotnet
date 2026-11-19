<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/engine-c89bfe4-table-type-keys.accdb: a copy of
    Assets/ace-extended-date-table-type.accdb that this library, as it was at c89bfe4, changed.

.DESCRIPTION
    Versions of this library before #51 keyed the date they read, and every one of them read the layout DAO's
    table-type recordset writes - the day in fifteen digits (#64) - as no value: so an index entry such a version made
    for a row in that layout is under the key of no value, not under the key of the row's bytes, which is where ACE
    keys it. A file such a version wrote holds those entries, and this library must find them when it deletes or
    rewrites the row (ExtendedDateTableTypeTests).

    The fixture is such a file, made by the earlier code itself: this script checks c89bfe4 out into a worktree of its
    own, has that code open a copy of ace-extended-date-table-type.accdb and change it, with VerifyIndexes, and copies
    it out:

      W   given the index ByK (K) over its four rows: 1 and 4 the date in Access's first and second forms, which that
          version keyed as their date in the first form, and 2 and 3 in the third layout, which it keyed as no value.
      T   rows 4, 7 and 9, in the third layout, given a new How: their entries in KAsc, KDesc and KHow were found by the
          row they point at, taken out, and each row entered under the key of no value in K. Row 9's bytes after the
          value begin with a 7, in the place of the first form's count.

    The file is not the same bytes on every run (it holds the time it was made), so the fixture is committed, and this
    script is the record of how. It needs the .NET SDK and Git; it changes nothing but the fixture.

.EXAMPLE
    pwsh tools/earlier-extended-date-table-type-keys.ps1
#>
[CmdletBinding()]
param(
    # Where to write the database.
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$Commit   = 'c89bfe4'
$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assets   = Join-Path $repo 'tests\JackcessDotNet.Tests\Assets'
$worktree = Join-Path ([IO.Path]::GetTempPath()) ("jackcess-$Commit-" + [Guid]::NewGuid().ToString('N'))
$outPath  = [IO.Path]::GetFullPath($(if ($Out) { $Out } else { Join-Path $assets "engine-$Commit-table-type-keys.accdb" }))

Copy-Item (Join-Path $assets 'ace-extended-date-table-type.accdb') $outPath -Force
Set-ItemProperty $outPath -Name IsReadOnly -Value $false
git -C $repo worktree add --detach $worktree $Commit | Out-Null
try {
    # The earlier code is driven from its own test project, which sees its internals and has its runner.
    Set-Content (Join-Path $worktree 'tests\JackcessDotNet.Tests\MakeEarlierTableTypeKeys.cs') @'
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class MakeEarlierTableTypeKeys
{
    [Fact]
    public void Make()
    {
        string path = Environment.GetEnvironmentVariable("EARLIER_TABLE_TYPE_KEYS_OUT")!;
        using var db = Database.Open(path);
        db.VerifyIndexes = true;
        db.GetTable("W").AddIndex(new IndexDefinition("ByK", "K"));
        var t = db.GetTable("T");
        foreach (int id in new[] { 4, 7, 9 })
            t.UpdateByPrimaryKey(id, new Row { ["How"] = "earlier " + id });
    }
}
'@
    $env:EARLIER_TABLE_TYPE_KEYS_OUT = $outPath
    dotnet build (Join-Path $worktree 'tests\JackcessDotNet.Tests\JackcessDotNet.Tests.csproj') --configuration Release --framework net10.0 --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'The earlier code did not build.' }
    & (Join-Path $worktree 'tests\JackcessDotNet.Tests\bin\Release\net10.0\JackcessDotNet.Tests.exe') -method 'JackcessDotNet.Tests.MakeEarlierTableTypeKeys.Make' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The earlier code did not change the database.' }
}
finally {
    $env:EARLIER_TABLE_TYPE_KEYS_OUT = $null
    git -C $repo worktree remove --force $worktree | Out-Null
}
"{0} ({1:N0} bytes), changed by this library at {2}" -f $outPath, (Get-Item $outPath).Length, $Commit
