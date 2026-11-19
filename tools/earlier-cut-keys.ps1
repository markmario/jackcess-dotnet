<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/engine-913240c-cut-keys.mdb: a database written by this library as it
    was at commit 913240c, before it cut keys of several columns as Access cuts them.

.DESCRIPTION
    Access cuts an index key longer than 510 bytes there and ends it in a two-byte digest of every byte it cut
    off. From 6184654 until #56 this library left the last of those bytes out of the digest, as Jackcess does.
    That byte is 0 in a key that ends in text, so the digests are the same; in a key of several columns that ends
    in a number after text that filled the entry they are not, and Access cannot update or delete such a row
    ("The search key was not found in any record"). A file such a version wrote holds those entries, and this
    library must find, move and remove them, and a unique index must see them (CutKeyTests).

    The fixture is such a file, made by the earlier code itself: this script checks commit 913240c out into a
    worktree of its own, has that code create a table and fill it, and copies the database out.

      Pairs   Id LONG primary key, A TEXT(255), K LONG, N LONG; ByAK (A, K), ByKA (K, A), UniqueAN (A, N) unique.
              Nine rows: 'short'; 255 of U+03A9 with K 1, 70000 and 256 (whose key ends in 0); 250 of U+00E9 and
              five of 'a', with K 1 and 70000 (its key fills 508 bytes, and the cut falls at K); 248 of U+00E9 and
              seven of 'a' (the cut falls inside K); 200 of U+03AA; 255 of 'a' (not cut). N is the row's number
              times 1,000.

    The file is not the same bytes on every run (it holds the time it was made), so the fixture is committed, and
    this script is the record of how. It needs the .NET SDK and Git; it changes nothing but the fixture.

.EXAMPLE
    pwsh tools/earlier-cut-keys.ps1
#>
[CmdletBinding()]
param(
    # Where to write the database.
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\engine-913240c-cut-keys.mdb')
)

$ErrorActionPreference = 'Stop'
$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worktree = Join-Path ([IO.Path]::GetTempPath()) ("jackcess-913240c-" + [Guid]::NewGuid().ToString('N'))
$outPath  = [IO.Path]::GetFullPath($Out)

git -C $repo worktree add --detach $worktree 913240c | Out-Null
try {
    # The earlier code is driven from its own test project, which sees its internals and has its runner.
    Set-Content (Join-Path $worktree 'tests\JackcessDotNet.Tests\MakeEarlierCutKeys.cs') @'
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class MakeEarlierCutKeys
{
    [Fact]
    public void Make()
    {
        string path = Environment.GetEnvironmentVariable("EARLIER_CUT_KEYS_OUT")!;
        if (File.Exists(path)) File.Delete(path);
        using var db = Database.Create(path, FileFormat.Access2000);
        var table = db.CreateTable("Pairs", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("A", DataType.Text) { MaxLength = 255 },
            new ColumnDefinition("K", DataType.Int32), new ColumnDefinition("N", DataType.Int32),
        }, new[] { "Id" }, new[]
        {
            new IndexDefinition("ByAK", "A", "K"), new IndexDefinition("ByKA", "K", "A"),
            new IndexDefinition("UniqueAN", "A", "N") { IsUnique = true },
        });
        string omega = new string('Ω', 255), e250 = new string('é', 250) + "aaaaa";
        var rows = new (string A, int K)[]
        {
            ("short", 1), (omega, 1), (omega, 70000), (omega, 256), (e250, 1), (e250, 70000),
            (new string('é', 248) + "aaaaaaa", 1), (new string('Ϊ', 200), 1), (new string('a', 255), 1),
        };
        for (int i = 0; i < rows.Length; i++)
            table.Insert(new Row { ["Id"] = i + 1, ["A"] = rows[i].A, ["K"] = rows[i].K, ["N"] = (i + 1) * 1000 });
    }
}
'@
    New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null
    $env:EARLIER_CUT_KEYS_OUT = $outPath
    dotnet build (Join-Path $worktree 'tests\JackcessDotNet.Tests\JackcessDotNet.Tests.csproj') --configuration Release --framework net10.0 --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'The earlier code did not build.' }
    & (Join-Path $worktree 'tests\JackcessDotNet.Tests\bin\Release\net10.0\JackcessDotNet.Tests.exe') -method 'JackcessDotNet.Tests.MakeEarlierCutKeys.Make' | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outPath)) { throw 'The earlier code did not write the database.' }
}
finally {
    $env:EARLIER_CUT_KEYS_OUT = $null
    git -C $repo worktree remove --force $worktree | Out-Null
}
"{0} ({1:N0} bytes), written by this library at 913240c" -f $outPath, (Get-Item $outPath).Length
