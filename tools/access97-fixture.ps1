<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/access97-jet40.mdb and access97-jet40-long-rows.tsv: an Access 97
    (Jet3) database written by Microsoft's own engine, with the rows and index pages the corpus's Access 97 files
    do not have.

.DESCRIPTION
    This library reads Access 97 files and does not write them, so a fixture for reading them has to come from an
    engine that does. Jet 4.0 (DAO.DBEngine.36, part of Windows, 32-bit only) creates a Jet3 database and keeps
    its tables and indexes; the 32-bit ACE 12.0 of Access 2007 (-EngineProgId DAO.DBEngine.120) does too. ACE 16
    neither makes nor opens one.

      LongRows  Id LONG primary key, A, B, C, D TEXT(255), N LONG. A Jet3 row gives each variable-length value's
                offset in one byte, so a row longer than 256 bytes has a jump table: the numbers of the first
                values past each 256 bytes. The rows here are of every length a row can have for six bytes
                either side of 256, 512 and 768 bytes, the text divided among the columns four ways (filled from
                the first column, from the last, evenly, and with B null), with a few more: every column null,
                every column full, one column alone. Every value is letters and digits that depend on the row,
                the column and the place in the value, so a value read from the wrong place is not the value
                written. The corpus has one Access 97 row over 256 bytes, with no value past them.
      Keys      Id LONG primary key, K1..K10 LONG with a unique index, ByAll, on all ten. 1,500 rows, added with
                both indexes in place. The primary key has leaf pages under a node page of several entries;
                ByAll, with entries of some fifty bytes, has node pages whose entries share a prefix, under a
                node page of its own. No Access 97
                file of the corpus has a node page with a shared prefix (one has two entries, the rest one), or
                an index more than two levels deep.
                Row i (1 to 1,500) has K1 = (i * 7919) mod 1500 and, for j = 2 to 10,
                Kj = ((i * (j + 1) * 7919 + j * 104729) mod 2000003) - 1000001 (KeysOf in Access97ReadTests).

    Once the file is written and closed, the engine that wrote it opens it again and reads it back: every row of
    LongRows goes to the .tsv as that engine reads it (Id, A, B, C, D, N; \N for a null), and every row of Keys
    is checked against the formula above and found again through each index with Seek. The tests compare what
    this library reads with those.

    The file is not the same bytes on every run, so the fixture is committed, and this script is the record of
    how it was made. It needs a 32-bit Windows PowerShell:
      C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File tools\access97-fixture.ps1

.EXAMPLE
    C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File tools\access97-fixture.ps1
#>
[CmdletBinding()]
param(
    # Where to write the database (tests/JackcessDotNet.Tests/Assets/access97-jet40.mdb when not given); the rows
    # of LongRows go beside it, in <name>-long-rows.tsv.
    [string]$Out,
    # The DAO engine, by ProgID: DAO.DBEngine.36 is Jet 4.0, DAO.DBEngine.120 the ACE beside it.
    [string]$EngineProgId = 'DAO.DBEngine.36'
)

$ErrorActionPreference = 'Stop'
if ([Environment]::Is64BitProcess) { throw 'An engine that makes an Access 97 file is 32-bit: run this in the 32-bit Windows PowerShell.' }

if (-not $Out) { $Out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\tests\JackcessDotNet.Tests\Assets\access97-jet40.mdb' }
$outPath = [IO.Path]::GetFullPath($Out)
$tsvPath = [IO.Path]::ChangeExtension($outPath, $null).TrimEnd('.') + '-long-rows.tsv'
if (Test-Path $outPath) { Remove-Item $outPath }

$dbVersion30 = 32   # Jet 3.x: Access 97
$dbOpenTable = 1
$alphabet    = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
$textColumns = 'A', 'B', 'C', 'D'

function Get-Text([int]$id, [int]$column, [int]$length) {
    $chars = New-Object char[] $length
    for ($p = 0; $p -lt $length; $p++) { $chars[$p] = $alphabet[($id * 31 + $column * 7 + $p) % 62] }
    New-Object string (, $chars)
}

# The lengths of A, B, C and D for a row with $total characters of text; -1 is a null.
function Get-Lengths([int]$total, [string]$how) {
    $lengths = @(0, 0, 0, 0)
    switch ($how) {
        'first' { $left = $total; foreach ($c in 0..3) { $lengths[$c] = [Math]::Min($left, 255); $left -= $lengths[$c] } }
        'last'  { $left = $total; foreach ($c in 3..0) { $lengths[$c] = [Math]::Min($left, 255); $left -= $lengths[$c] } }
        'even'  { foreach ($c in 0..3) { $lengths[$c] = [Math]::Floor($total / 4) }; $lengths[0] += $total % 4 }
        'noB'   { $left = $total; foreach ($c in 0, 2, 3) { $lengths[$c] = [Math]::Min($left, 255); $left -= $lengths[$c] }; $lengths[1] = -1 }
    }
    # A column DAO made does not take an empty string: a column with nothing is null.
    foreach ($c in 0..3) { if ($lengths[$c] -eq 0) { $lengths[$c] = -1 } }
    , $lengths
}

function Get-Keys([int]$i) {
    $keys = New-Object int[] 10
    $keys[0] = ($i * 7919) % 1500
    foreach ($j in 2..10) { $keys[$j - 1] = (([long]$i * ($j + 1) * 7919 + $j * 104729) % 2000003) - 1000001 }
    , $keys
}

$engine = New-Object -ComObject $EngineProgId
$db = $engine.CreateDatabase($outPath, ';LANGID=0x0409;CP=1252;COUNTRY=0', $dbVersion30)
try {
    $db.Execute('CREATE TABLE LongRows (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, A TEXT(255), B TEXT(255), C TEXT(255), D TEXT(255), N LONG)')
    $db.Execute('CREATE TABLE Keys (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, ' + ((1..10 | ForEach-Object { "K$_ LONG" }) -join ', ') + ')')
    $db.Execute('CREATE UNIQUE INDEX ByAll ON Keys (' + ((1..10 | ForEach-Object { "K$_" }) -join ', ') + ')')

    # A row is its column count (1), Id and N (8), the text, the end of the data, four offsets and their count
    # (6), a jump for each 256 bytes, and the null mask (1).
    $shapes = New-Object System.Collections.ArrayList
    foreach ($boundary in 256, 512, 768) {
        foreach ($total in ($boundary - 26)..($boundary - 6)) {
            foreach ($how in 'first', 'last', 'even', 'noB') { [void]$shapes.Add((Get-Lengths $total $how)) }
        }
    }
    [void]$shapes.Add(@(-1, -1, -1, -1))
    [void]$shapes.Add(@(255, 255, 255, 255))
    [void]$shapes.Add(@(-1, -1, -1, 255))
    [void]$shapes.Add(@(-1, 255, 255, 255))
    [void]$shapes.Add(@(255, -1, -1, -1))
    [void]$shapes.Add(@(3, 2, 1, 4))
    [void]$shapes.Add(@(255, 1, 255, 1))
    [void]$shapes.Add(@(1, 255, 1, 255))

    $rows = $db.OpenRecordset('LongRows', $dbOpenTable)
    $id = 0
    foreach ($lengths in $shapes) {
        $id++
        $rows.AddNew()
        $rows.Fields.Item('Id').Value = $id
        foreach ($c in 0..3) {
            if ($lengths[$c] -ge 0) { $rows.Fields.Item($textColumns[$c]).Value = [string](Get-Text $id $c $lengths[$c]) }
        }
        $rows.Fields.Item('N').Value = -$id
        $rows.Update()
    }
    $rows.Close()
    $longRows = $id

    $keys = $db.OpenRecordset('Keys', $dbOpenTable)
    foreach ($i in 1..1500) {
        $k = Get-Keys $i
        $keys.AddNew()
        $keys.Fields.Item('Id').Value = $i
        foreach ($j in 1..10) { $keys.Fields.Item("K$j").Value = $k[$j - 1] }
        $keys.Update()
    }
    $keys.Close()
}
finally { $db.Close() }

# What the engine reads of the file it wrote.
$db = $engine.OpenDatabase($outPath, $false, $true)
try {
    $format = $db.Version
    $lines = New-Object System.Collections.ArrayList
    $rows = $db.OpenRecordset('SELECT Id, A, B, C, D, N FROM LongRows ORDER BY Id')
    while (-not $rows.EOF) {
        $cells = foreach ($name in 'Id', 'A', 'B', 'C', 'D', 'N') {
            $value = $rows.Fields.Item($name).Value
            if ($value -is [DBNull] -or $null -eq $value) { '\N' } else { [string]$value }
        }
        [void]$lines.Add($cells -join "`t")
        $rows.MoveNext()
    }
    $rows.Close()
    if ($lines.Count -ne $longRows) { throw "LongRows: $longRows rows written, $($lines.Count) read." }

    $keys = $db.OpenRecordset('Keys', $dbOpenTable)
    foreach ($i in 1..1500) {
        $k = Get-Keys $i
        $keys.Index = 'PrimaryKey'
        $keys.Seek('=', $i)
        if ($keys.NoMatch) { throw "Keys: no row for Id $i through PrimaryKey." }
        foreach ($j in 1..10) {
            if ($keys.Fields.Item("K$j").Value -ne $k[$j - 1]) { throw "Keys: row $i has K$j = $($keys.Fields.Item("K$j").Value), not $($k[$j - 1])." }
        }
        $keys.Index = 'ByAll'
        $keys.Seek('=', $k[0], $k[1], $k[2], $k[3], $k[4], $k[5], $k[6], $k[7], $k[8], $k[9])
        if ($keys.NoMatch -or $keys.Fields.Item('Id').Value -ne $i) { throw "Keys: no row for the keys of row $i through ByAll." }
    }
    if ($keys.RecordCount -ne 1500) { throw "Keys: $($keys.RecordCount) rows, not 1500." }
    $keys.Close()
}
finally { $db.Close() }

$header = "# Id, A, B, C, D, N of every row of LongRows in $([IO.Path]::GetFileName($outPath)), a Jet $format database, as $EngineProgId $($engine.Version) (32-bit), which wrote it, reads it. \N is a null. Made by tools/access97-fixture.ps1."
[IO.File]::WriteAllText($tsvPath, (@($header) + $lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))

"$longRows rows in LongRows, 1500 in Keys, Jet $format, $EngineProgId $($engine.Version) -> $outPath"
