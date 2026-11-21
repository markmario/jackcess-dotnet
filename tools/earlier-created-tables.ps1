<#
.SYNOPSIS
    Has versions of this library create a table and fill one Access made, and records what Access reads of them.

.DESCRIPTION
    Versions of this library before 442bbf8 - the JackcessDotNet packages 1.0.0, 1.2.0 and 1.2.2 on nuget.org, and
    builds of this repository up to 8e41563 - wrote every Text and Memo value compressed when its characters were all
    0x01 to 0xFF, 0xFF 0xFE and a byte each, whatever the column's Unicode compression; and a table they created had
    no Unicode compression on its columns and a catalog row (MSysObjects) with ParentId 0 and no Owner, where Access
    gives a table the Tables container's Id and an owner. Such a table is one Access neither lists nor reads (#93).

    For each version asked for, this script makes an Access 2000 .mdb with DAO (DAO.DBEngine.120), with a table
    Filled (Id Long, Name Text(50), Notes Memo, and no index: the 1.x versions fail on a table DAO made with one) whose
    Text and Memo have no Unicode compression, as DAO's CreateField makes them. It then builds a small program against that version - a package from nuget.org, or a commit of this
    repository checked out beside it - which creates a database with the table People of the same columns, and writes
    the same rows into People and into Filled. Last it reads both tables back with DAO, directly from the table (a table-type recordset), and
    writes what DAO read, or why it could not, to a .tsv beside the files. Nothing of the library under test reads
    the result.

    Each version writes two files: engine-<version>-created-table.mdb, a database it created with the table People,
    and engine-<version>-filled-table.mdb, the database DAO made with the rows written into Filled. The tests keep
    some of them (tests/JackcessDotNet.Tests/Assets).

    Run it in PowerShell 7 (pwsh), with a 64-bit ACE and the .NET SDK, from the repository's root:
      pwsh -File tools\earlier-created-tables.ps1 -OutDir <full path> -Versions '1.2.2','8e41563'
#>
param(
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [string[]] $Versions = @('1.0.0', '1.2.0', '1.2.2', '8e41563', '442bbf8', 'v2.0.0-preview.1', 'v2.0.0-preview.3')
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($OutDir)) { throw "OutDir must be a full path." }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('earlier-created-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null

# The rows each version writes: Id, Name, Notes. Escapes are C#'s.
$rows = @'
            (1, "Alice", null),
            (2, "Bob", "Short note"),
            (3, "Zo\u00EB", "Zo\u00EB's note"),
            (4, "\u01FFab", "\u01FFab"),
            (5, "\u65E5\u672C\u8A9E", "\u65E5\u672C\u8A9E\u306E\u30E1\u30E2"),
            (6, "\uFEFFab", "\uFEFFab"),
            (7, "Long", string.Concat(Enumerable.Repeat("abcdefghij", 500))),
            (8, "Mixed", "abc\u65E5\u672C"),
'@

# The API of 1.x and of this repository up to the previews, and the previews' own.
$builderApi = @'
using JackcessDotNet;
var rows = new (int Id, string Name, string? Notes)[] {
ROWS
};
if (args[0] == "create")
{
    using var db = Database.Create(args[1], JetVersion.Jet4);
    var people = db.CreateTable("People", new[]
    {
        new ColumnBuilder("Id", DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        new ColumnBuilder("Notes", DataType.Memo).Build(),
    }, primaryKey: "Id");
    foreach (var r in rows) people.Insert(new Row { ["Id"] = r.Id, ["Name"] = r.Name, ["Notes"] = r.Notes });
}
else
{
    using var db = Database.Open(args[1]);
    var filled = db.GetTable("Filled");
    foreach (var r in rows) filled.Insert(new Row { ["Id"] = r.Id, ["Name"] = r.Name, ["Notes"] = r.Notes });
}
'@
$definitionApi = @'
using JackcessDotNet;
var rows = new (int Id, string Name, string? Notes)[] {
ROWS
};
if (args[0] == "create")
{
    using var db = Database.Create(args[1], FileFormat.Access2000);
    var people = db.CreateTable("People", new[]
    {
        new ColumnDefinition("Id", DataType.Int32),
        new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 },
        new ColumnDefinition("Notes", DataType.Memo),
    }, "Id");
    foreach (var r in rows) people.Insert(new Row { ["Id"] = r.Id, ["Name"] = r.Name, ["Notes"] = r.Notes });
}
else
{
    using var db = Database.Open(args[1]);
    var filled = db.GetTable("Filled");
    foreach (var r in rows) filled.Insert(new Row { ["Id"] = r.Id, ["Name"] = r.Name, ["Notes"] = r.Notes });
}
'@

$dbe = New-Object -ComObject DAO.DBEngine.120
$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add("# tools/earlier-created-tables.ps1: what DAO.DBEngine.120 reads, from the table, of what each version wrote")
$lines.Add("# version`ttable`tid`tcolumn`tread (code units in hex, null, or the error)")
try {
    foreach ($version in $Versions) {
        $file = Join-Path $OutDir "engine-$version-created-table.mdb"
        $filledFile = Join-Path $OutDir "engine-$version-filled-table.mdb"
        foreach ($f in $file, $filledFile) { if (Test-Path $f) { Remove-Item $f } }
        $db = $dbe.CreateDatabase($filledFile, ';LANGID=0x0409;CP=1252;COUNTRY=0', 64)
        $td = $db.CreateTableDef('Filled')
        $td.Fields.Append($td.CreateField('Id', 4))
        $td.Fields.Append($td.CreateField('Name', 10, 50))
        $td.Fields.Append($td.CreateField('Notes', 12))
        $db.TableDefs.Append($td)
        $db.Close()

        $project = Join-Path $work $version
        New-Item -ItemType Directory $project | Out-Null
        if ($version -match '^\d+\.\d+\.\d+$') {
            $reference = "<PackageReference Include=`"JackcessDotNet`" Version=`"$version`" />"
            $api = $builderApi
        } elseif ($version -like 'v2.0.0-preview.*') {
            $reference = "<PackageReference Include=`"Jackcess.Net`" Version=`"$($version.Substring(1))`" />"
            $api = $definitionApi
        } else {
            $source = Join-Path $work "src-$version"
            $ErrorActionPreference = 'Continue'
            git -C $repo worktree add --detach $source $version 2>&1 | Out-Null
            # Built on its own and referenced as an assembly, with the packages it depends on.
            $built = & dotnet build "$source\src\JackcessDotNet\JackcessDotNet.csproj" -c Release -f net8.0 -p:NuGetAudit=false -p:TreatWarningsAsErrors=false -o "$source\out" 2>&1
            $ErrorActionPreference = 'Stop'
            if ($LASTEXITCODE -ne 0) { throw "building $version failed: $(($built | ForEach-Object { "$_" } | Where-Object { $_ -match 'error' } | Select-Object -First 3) -join ' ')" }
            $reference = "<Reference Include=`"JackcessDotNet`"><HintPath>$source\out\JackcessDotNet.dll</HintPath></Reference><PackageReference Include=`"System.Text.Encoding.CodePages`" Version=`"8.0.0`" />"
            $api = $builderApi
        }
        Set-Content -Encoding UTF8 (Join-Path $project 'program.csproj') @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><NoWarn>`$(NoWarn);CS0618;NU1903;NU1902</NoWarn><RollForward>Major</RollForward></PropertyGroup>
  <ItemGroup>$reference</ItemGroup>
</Project>
"@
        Set-Content -Encoding UTF8 (Join-Path $project 'Program.cs') ($api.Replace('ROWS', $rows))
        $results = @{}
        foreach ($step in @(@('create', $file), @('fill', $filledFile))) {
            $ErrorActionPreference = 'Continue'
            $output = & dotnet run --project $project -c Release -p:NuGetAudit=false -- $step[0] $step[1] 2>&1
            $ErrorActionPreference = 'Stop'
            $results[$step[0]] = if ($LASTEXITCODE -eq 0) { $null } else { (($output | ForEach-Object { "$_" } | Where-Object { $_ -match 'Exception|error' } | Select-Object -First 1) -as [string]) }
        }

        foreach ($pair in @(@('People', $file, 'create'), @('Filled', $filledFile, 'fill'))) {
            $table = $pair[0]
            if ($results[$pair[2]]) { $lines.Add("$version`t$table`t`t`tthe version failed: $($results[$pair[2]].Trim())"); continue }
            $db = $dbe.OpenDatabase($pair[1], $false, $true)
            try {
                try {
                    $rs = $db.OpenRecordset($table, 1)
                    while (-not $rs.EOF) {
                        $id = $rs.Fields.Item('Id').Value
                        foreach ($column in 'Name', 'Notes') {
                            $v = $rs.Fields.Item($column).Value
                            $read = if ($v -is [DBNull]) { 'null' } else { ($v.ToCharArray() | ForEach-Object { '{0:X4}' -f [int]$_ }) -join ' ' }
                            if ($read.Length -gt 200) { $read = 'length ' + $v.Length + ': ' + $read.Substring(0, 200) }
                            $lines.Add("$version`t$table`t$id`t$column`t$read")
                        }
                        $rs.MoveNext()
                    }
                    $rs.Close()
                } catch { $lines.Add("$version`t$table`t`t`tcannot open: $($_.Exception.Message)") }
            } finally { $db.Close() }
        }
    }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
    $ErrorActionPreference = 'Continue'
    foreach ($tree in Get-ChildItem $work -Directory -Filter 'src-*') { git -C $repo worktree remove --force $tree.FullName 2>&1 | Out-Null }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
$tsv = Join-Path $OutDir 'earlier-created-tables.tsv'
[IO.File]::WriteAllText($tsv, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "written $($lines.Count) lines to $tsv"
