<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/engine-696ca52-long-keys.mdb: a database written by this library as it
    was at commit 696ca52, before it cut long index keys.

.DESCRIPTION
    Access keeps no index key longer than 510 bytes: it cuts a longer one there and ends it in a two-byte digest
    of the rest. This library does the same; as it was at 696ca52 it wrote such a key
    whole, as Jackcess did before it cut keys, so a file made by an earlier release can hold whole keys of more
    than 510 bytes, and this library must find, move and remove those entries too (LegacyLongKeyTests).

    The fixture is such a file, made by the earlier code itself and not by a reconstruction of it: this script
    checks commit 696ca52 out into a worktree of its own, has that code create two tables and fill them, and
    copies the database out.

      Coded   Code TEXT(255) primary key, N LONG. 20 short codes, and five whose keys are over 510 bytes whole:
              255 of é (513 bytes); 254 of é and an e; two values that differ only where a key is cut and whose
              digests are the same, so that their cut keys are one key (rows 23 and 24); and 100 of Æ with 155 of é.
      Paired  A TEXT(255), B TEXT(255) primary key together, N LONG. Two short pairs, and twelve whose key is some
              800 bytes whole.

    The file is not the same bytes on every run (it holds the time it was made), so the fixture is committed, and
    this script is the record of how. It needs the .NET SDK and Git; it changes nothing but the fixture.

.EXAMPLE
    pwsh tools/legacy-long-keys.ps1
#>
[CmdletBinding()]
param(
    # Where to write the database.
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\engine-696ca52-long-keys.mdb')
)

$ErrorActionPreference = 'Stop'
$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worktree = Join-Path ([IO.Path]::GetTempPath()) ("jackcess-696ca52-" + [Guid]::NewGuid().ToString('N'))
$outPath  = [IO.Path]::GetFullPath($Out)

git -C $repo worktree add --detach $worktree 696ca52 | Out-Null
try {
    # The earlier code is driven from its own test project, which sees its internals and has its runner.
    Set-Content (Join-Path $worktree 'tests\JackcessDotNet.Tests\MakeLongKeys.cs') @'
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class MakeLongKeys
{
    [Fact]
    public void Make()
    {
        string path = Environment.GetEnvironmentVariable("LEGACY_LONG_KEYS_OUT")!;
        if (File.Exists(path)) File.Delete(path);
        using var db = Database.Create(path, JetVersion.Jet4);

        Column Text(string name) => new ColumnBuilder(name, DataType.Text).MaxLength(255).Build();
        Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();

        var coded = db.CreateTable("Coded", new[] { Text("Code"), Long("N") }, primaryKey: "Code");
        for (int i = 1; i <= 20; i++)
            coded.Insert(new Row { ["Code"] = $"short {i:D2}", ["N"] = i });
        string start = new string('Æ', 100) + new string('é', 115);
        coded.Insert(new Row { ["Code"] = new string('é', 255), ["N"] = 21 });
        coded.Insert(new Row { ["Code"] = new string('é', 254) + "e", ["N"] = 22 });
        coded.Insert(new Row { ["Code"] = start + "èèèéèêéèéèëëéëêèèèêêèëéééëëëèêêéèëééëëéè", ["N"] = 23 });
        coded.Insert(new Row { ["Code"] = start + "èèëêèééêèèëêéêéëéêêéêêéèéèëèèééèëêéëéééé", ["N"] = 24 });
        coded.Insert(new Row { ["Code"] = new string('Æ', 100) + new string('é', 155), ["N"] = 25 });

        var paired = db.CreateTable("Paired", new[] { Text("A"), Text("B"), Long("N") }, new[] { "A", "B" });
        paired.Insert(new Row { ["A"] = "a", ["B"] = "b", ["N"] = 100 });
        paired.Insert(new Row { ["A"] = "a", ["B"] = "c", ["N"] = 101 });
        for (int i = 1; i <= 12; i++)
            paired.Insert(new Row { ["A"] = new string('é', 200) + " " + (i % 3), ["B"] = new string('è', 200) + " " + i.ToString("D2"), ["N"] = i });
    }
}
'@
    New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null
    $env:LEGACY_LONG_KEYS_OUT = $outPath
    dotnet build (Join-Path $worktree 'tests\JackcessDotNet.Tests\JackcessDotNet.Tests.csproj') --configuration Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'The earlier code did not build.' }
    & (Join-Path $worktree 'tests\JackcessDotNet.Tests\bin\Release\net8.0\JackcessDotNet.Tests.exe') -method 'JackcessDotNet.Tests.MakeLongKeys.Make' | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outPath)) { throw 'The earlier code did not write the database.' }
}
finally {
    $env:LEGACY_LONG_KEYS_OUT = $null
    git -C $repo worktree remove --force $worktree | Out-Null
}
"{0} ({1:N0} bytes), written by this library at 696ca52" -f $outPath, (Get-Item $outPath).Length
