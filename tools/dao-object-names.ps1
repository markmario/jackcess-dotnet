<#
.SYNOPSIS
    Asks DAO which pairs of names are one name to Access, as a field's, a table's and an index's, and writes its
    verdicts into tests/JackcessDotNet.Tests/Assets/dao-object-names.tsv, which ObjectNameTests holds ObjectNames to.

.DESCRIPTION
    Each line of the file is a pair of names, written as UTF-16 code units in hex so that no file's encoding matters,
    what makes them a pair (the second is the first's other case, its canonical or compatibility form, its other kana,
    the letters it sorts as...), and then, for each of three orders, DAO's verdict as a field, a table and an index
    name - three characters:

      1   one: DAO refuses the second name (3191 "Cannot define field more than once", 3010 "Table already exists",
          3284 "Index already exists")
      c   two that DAO's own lookups confuse: it makes both, and then a lookup by one of the names - in the
          TableDef's Fields or Indexes, in the database's TableDefs, or in SQL - finds the other, or finds several
          ("There are several tables with that name"), or the table takes no row
      2   two: DAO makes both, and each lookup finds its own
      -   not asked, or DAO refuses a name for another reason (a period in it, say)

    The orders are the file's fourth, fifth and sixth columns:

      GeneralLegacy  an .mdb DAO.DBEngine.120 makes (CreateDatabase, dbLangGeneral): General - Legacy, 1033 version 0
      General        a copy of tests/corpus/V2010/common1V2010.accdb, an Access 2010 database that sorts in General
                     (1033 version 1), its relationships, queries and tables dropped: DAO makes no database in General
      Access97       an Access 97 .mdb DAO.DBEngine.36 (Jet 4.0) makes: run in 32-bit Windows PowerShell

    For each pair the script makes the two names in a database of the order and takes them away again: two fields of
    a table it then appends, two tables, two indexes of a table. It reads the pairs from -Pairs - the asset's first
    three columns - and rewrites the order's column, leaving the others as they are.

    The sweep's pairs were made from every character of the BMP from U+0020 that has another form on .NET 10 or .NET
    Framework 4.8 (its upper and lower case, its NFC, NFD, NFKC and NFKD forms and their case, its other kana, the
    letters it sorts as) as "x" + c and "x" + form, each pair once; the names of code page 1252 against their letters
    without an accent, for the Access 97 order; trailing and inner spaces and the characters General - Legacy has no
    codes for; and names with the marks Access keys by the characters around them (ー, 々, ゝ, the shadda).

    Measured with DAO 16 (Microsoft 365) and Jet 4.0, 2026-11: 18,140 pairs, 18,065 of them asked in each Jet4 order
    and 135 in Access 97's, about an hour an order; the pull request of markmario/jackcess-dotnet#63 has the results.
    The asset keeps 743 of them, which this script asks about again in seconds: every pair DAO's lookups confused,
    every pair asked in Access 97, every pair of spaces, marks and letters, and a sample of the rest. Give -Pairs a
    file of the whole sweep's pairs to ask about it all.

.EXAMPLE
    pwsh tools/dao-object-names.ps1 -Order GeneralLegacy
    C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -File tools\dao-object-names.ps1 -Order Access97
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('GeneralLegacy', 'General', 'Access97')][string]$Order,
    # The asset (its first three columns are read and the order's column rewritten).
    [string]$Pairs,
    # A folder for the databases the script makes and removes.
    [string]$Work = [IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Pairs) { $Pairs = Join-Path $root 'tests\JackcessDotNet.Tests\Assets\dao-object-names.tsv' }
$column = @{ GeneralLegacy = 3; General = 4; Access97 = 5 }[$Order]
if ($Order -eq 'Access97' -and [Environment]::Is64BitProcess) { throw 'Access 97 files are made by Jet 4.0, which is 32-bit: run in 32-bit Windows PowerShell.' }

function ConvertFrom-Hex([string] $hex) { -join ($hex.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [char][Convert]::ToInt32($_, 16) }) }
function Same([string] $a, [string] $b) { [string]::Equals($a, $b, [StringComparison]::Ordinal) }

# The DAO error number of what a call threw.
function Get-DaoError($record) {
    $e = $record.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    if ($e -is [Runtime.InteropServices.COMException]) { return $e.ErrorCode -band 0xFFFF }
    return -1
}

# The database.
$path = Join-Path $Work ("dao-object-names-$Order-" + [Guid]::NewGuid().ToString('N') + $(if ($Order -eq 'General') { '.accdb' } else { '.mdb' }))
$engine = New-Object -ComObject $(if ($Order -eq 'Access97') { 'DAO.DBEngine.36' } else { 'DAO.DBEngine.120' })
switch ($Order) {
    'GeneralLegacy' { $engine.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', 64).Close() }
    'Access97'      { $engine.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', 32).Close() }
    'General' {
        Copy-Item (Join-Path $root 'tests\corpus\V2010\common1V2010.accdb') $path
        Set-ItemProperty $path IsReadOnly $false
        $db = $engine.OpenDatabase($path)
        for ($i = $db.Relations.Count - 1; $i -ge 0; $i--) { $db.Relations.Delete($db.Relations.Item($i).Name) }
        for ($i = $db.QueryDefs.Count - 1; $i -ge 0; $i--) { $db.QueryDefs.Delete($db.QueryDefs.Item($i).Name) }
        for ($i = $db.TableDefs.Count - 1; $i -ge 0; $i--) { $n = $db.TableDefs.Item($i).Name; if (-not $n.StartsWith('MSys')) { $db.TableDefs.Delete($n) } }
        $db.Close()
    }
}
$db = $engine.OpenDatabase($path)

# Every table but the system's, each by its place and a name of its own: a name DAO looks up can find several.
function Remove-UserTables {
    $db.TableDefs.Refresh()
    for ($i = $db.TableDefs.Count - 1; $i -ge 0; $i--) {
        $td = $db.TableDefs.Item($i)
        if ($td.Name.StartsWith('MSys')) { continue }
        $temp = 'zz' + [Guid]::NewGuid().ToString('N')
        try { $td.Name = $temp; $db.TableDefs.Delete($temp) } catch { }
    }
}

# Whether a lookup finds the object of the name: 'self', or anything else.
function Find-Self([scriptblock] $find, [string] $name) {
    try { if (Same (& $find) $name) { 'self' } else { 'other' } } catch { 'other' }
}

function Test-Sql([string] $query, [scriptblock] $isSelf) {
    $rs = $null
    try { $rs = $db.OpenRecordset($query); if (& $isSelf $rs) { 'self' } else { 'other' } }
    catch { 'other' }
    finally { if ($rs) { try { $rs.Close() } catch { } } }
}

$n = 0
function Test-Fields([string] $a, [string] $b) {
    $script:n++
    $table = "T$script:n"
    $td = $db.CreateTableDef($table)
    try { $td.Fields.Append($td.CreateField($a, 10, 10)) } catch { return '-' }
    try { $td.Fields.Append($td.CreateField($b, 10, 10)) } catch { if ((Get-DaoError $_) -eq 3191) { return '1' } else { return '-' } }
    try { $db.TableDefs.Append($td) } catch { if ((Get-DaoError $_) -eq 3191) { return '1' } else { return '-' } }
    try {
        $made = $db.TableDefs.Item($table)
        $lookups = @((Find-Self { $made.Fields.Item($a).Name } $a), (Find-Self { $made.Fields.Item($b).Name } $b))
        $rs = $null
        try { $rs = $db.OpenRecordset($table); $rs.AddNew(); $rs.Fields.Item(0).Value = '1'; $rs.Fields.Item(1).Value = '2'; $rs.Update(); $filled = $true }
        catch { $filled = $false }
        finally { if ($rs) { try { $rs.Close() } catch { } } }
        if ($filled) {
            $lookups += Test-Sql "SELECT [$a] FROM [$table]" { param($q) [string]$q.Fields.Item(0).Value -eq '1' }
            $lookups += Test-Sql "SELECT [$b] FROM [$table]" { param($q) [string]$q.Fields.Item(0).Value -eq '2' }
        } else { $lookups += 'other' }
        if ($lookups -contains 'other') { 'c' } else { '2' }
    }
    finally { try { $db.TableDefs.Delete($table) } catch { Remove-UserTables } }
}

function Test-Tables([string] $a, [string] $b) {
    try { $t1 = $db.CreateTableDef($a); $t1.Fields.Append($t1.CreateField('A', 4)); $db.TableDefs.Append($t1) } catch { return '-' }
    try {
        try { $t2 = $db.CreateTableDef($b); $t2.Fields.Append($t2.CreateField('B', 4)); $db.TableDefs.Append($t2) }
        catch { if ((Get-DaoError $_) -eq 3010) { return '1' } else { return '-' } }
        $lookups = @(
            (Find-Self { $db.TableDefs.Item($a).Name } $a), (Find-Self { $db.TableDefs.Item($b).Name } $b),
            (Test-Sql "SELECT * FROM [$a]" { param($q) $q.Fields.Item(0).Name -eq 'A' }),
            (Test-Sql "SELECT * FROM [$b]" { param($q) $q.Fields.Item(0).Name -eq 'B' }))
        if ($lookups -contains 'other') { 'c' } else { '2' }
    }
    finally { Remove-UserTables }
}

function Test-Indexes([string] $a, [string] $b) {
    $script:n++
    $table = "H$script:n"
    $td = $db.CreateTableDef($table); $td.Fields.Append($td.CreateField('Id', 4)); $db.TableDefs.Append($td)
    try {
        $holder = $db.TableDefs.Item($table)
        try { $i1 = $holder.CreateIndex($a); $i1.Fields.Append($i1.CreateField('Id')); $holder.Indexes.Append($i1) } catch { return '-' }
        try { $i2 = $holder.CreateIndex($b); $i2.Fields.Append($i2.CreateField('Id')); $holder.Indexes.Append($i2) }
        catch { if ((Get-DaoError $_) -eq 3284) { return '1' } else { return '-' } }
        $made = $db.TableDefs.Item($table)
        $lookups = @((Find-Self { $made.Indexes.Item($a).Name } $a), (Find-Self { $made.Indexes.Item($b).Name } $b))
        if ($lookups -contains 'other') { 'c' } else { '2' }
    }
    finally { try { $db.TableDefs.Delete($table) } catch { Remove-UserTables } }
}

#  An Access 97 file keeps a name in code page 1252: a pair with a character it has not is not asked about there.
$cp1252 = [Text.Encoding]::GetEncoding(1252, [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
function Test-Holds([string] $name) { if ($Order -ne 'Access97') { return $true }; try { [void]$cp1252.GetBytes($name); $true } catch { $false } }

$lines = [IO.File]::ReadAllLines($Pairs)
$clock = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].Length -eq 0 -or $lines[$i][0] -eq '#') { continue }
    $cells = $lines[$i].Split("`t")
    $a = ConvertFrom-Hex $cells[0]; $b = ConvertFrom-Hex $cells[1]
    $cells[$column] = if ((Test-Holds $a) -and (Test-Holds $b)) { [string](Test-Fields $a $b) + (Test-Tables $a $b) + (Test-Indexes $a $b) } else { '---' }
    $lines[$i] = $cells -join "`t"
    if ($i % 1000 -eq 0) { Write-Host "$i of $($lines.Count), $([int]$clock.Elapsed.TotalSeconds) s" }
}
$db.Close()
# As the repository keeps it: UTF-8 without a byte order mark, each line ended by LF.
[IO.File]::WriteAllText($Pairs, ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
Remove-Item $path
"$Order written to $Pairs in $([int]$clock.Elapsed.TotalSeconds) s"
