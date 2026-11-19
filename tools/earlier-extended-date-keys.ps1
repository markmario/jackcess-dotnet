<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/engine-<commit>-extended-date-keys.accdb: a copy of
    Assets/ace-extended-date-forms.accdb that this library, as it was at an earlier commit, changed.

.DESCRIPTION
    Access keys a Date/Time Extended value by the 42 bytes its row stores (#51). Versions of this library before
    that was so keyed the date they read, in the form Access's INSERT writes: from 2930d3f until db6f93b they read
    a value in Access's second form - the time of day in whole seconds - as no value, and from db6f93b as its date.
    So an index entry such a version made for a row in another form than the first is under another key than the
    row's bytes': under the key of no value, or of the first form of its date, which is also the key of a row that
    has that date in the first form. A file such a version wrote holds those entries, and this library must find
    them when it deletes or rewrites the row (ExtendedDateFormsTests).

    The fixture is such a file, made by the earlier code itself: this script checks the commit out into a worktree
    of its own, has that code open a copy of ace-extended-date-forms.accdb and change it, and copies it out:

      W   given the index ByK (K), over rows 1 to 4: the date as INSERT and as UPDATE write it, a space for the day's
          first digit, 'x' in the last byte.
      T   rows 2 (the second form) and 3 (a space for the day's first digit) given a new TForm: at 913240c the row
          moved, and was entered under the key of the date read, in each index on K; at c89bfe4, with
          VerifyIndexes - without it the entries of a key that did not change were left as they were - its entries
          were found by the row they point at, taken out, and the row entered under the key of the date read.

    -Commit 913240c is from the first span (the second form read as no value), c89bfe4 from the second. The file is
    not the same bytes on every run (it holds the time it was made), so the fixtures are committed, and this script
    is the record of how. It needs the .NET SDK and Git; it changes nothing but the fixture.

.EXAMPLE
    pwsh tools/earlier-extended-date-keys.ps1 -Commit 913240c
    pwsh tools/earlier-extended-date-keys.ps1 -Commit c89bfe4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('913240c', 'c89bfe4')] [string]$Commit,
    # Where to write the database.
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assets   = Join-Path $repo 'tests\JackcessDotNet.Tests\Assets'
$worktree = Join-Path ([IO.Path]::GetTempPath()) ("jackcess-$Commit-" + [Guid]::NewGuid().ToString('N'))
$outPath  = [IO.Path]::GetFullPath($(if ($Out) { $Out } else { Join-Path $assets "engine-$Commit-extended-date-keys.accdb" }))

Copy-Item (Join-Path $assets 'ace-extended-date-forms.accdb') $outPath -Force
Set-ItemProperty $outPath -Name IsReadOnly -Value $false
git -C $repo worktree add --detach $worktree $Commit | Out-Null
try {
    # The earlier code is driven from its own test project, which sees its internals and has its runner.
    Set-Content (Join-Path $worktree 'tests\JackcessDotNet.Tests\MakeEarlierExtendedDateKeys.cs') @'
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class MakeEarlierExtendedDateKeys
{
    [Fact]
    public void Make()
    {
        string path = Environment.GetEnvironmentVariable("EARLIER_EXTENDED_DATE_KEYS_OUT")!;
        using var db = Database.Open(path);
        db.VerifyIndexes = Environment.GetEnvironmentVariable("EARLIER_EXTENDED_DATE_KEYS_VERIFY") == "1";
        db.GetTable("W").AddIndex(new IndexDefinition("ByK", "K"));
        var t = db.GetTable("T");
        foreach (int id in new[] { 2, 3 })
            t.UpdateByPrimaryKey(id, new Row { ["TForm"] = "earlier " + id });
    }
}
'@
    $env:EARLIER_EXTENDED_DATE_KEYS_OUT = $outPath
    $env:EARLIER_EXTENDED_DATE_KEYS_VERIFY = $(if ($Commit -eq 'c89bfe4') { '1' } else { '0' })
    dotnet build (Join-Path $worktree 'tests\JackcessDotNet.Tests\JackcessDotNet.Tests.csproj') --configuration Release --framework net10.0 --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'The earlier code did not build.' }
    & (Join-Path $worktree 'tests\JackcessDotNet.Tests\bin\Release\net10.0\JackcessDotNet.Tests.exe') -method 'JackcessDotNet.Tests.MakeEarlierExtendedDateKeys.Make' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The earlier code did not change the database.' }
}
finally {
    $env:EARLIER_EXTENDED_DATE_KEYS_OUT = $null
    $env:EARLIER_EXTENDED_DATE_KEYS_VERIFY = $null
    git -C $repo worktree remove --force $worktree | Out-Null
}
"{0} ({1:N0} bytes), changed by this library at {2}" -f $outPath, (Get-Item $outPath).Length, $Commit
