<#
.SYNOPSIS
    Has Microsoft's own database engine store numbers with more places than a Decimal column holds, and writes what
    it stored to a .tsv file.

.DESCRIPTION
    tests/JackcessDotNet.Tests/Assets/ace-decimal-places.tsv is made by this script (#80). Microsoft's engine is the
    referee for the Decimal a number with more places than its column's scale becomes: this library must store the
    same bytes for it.

    The script creates a database with DAO, and in it a table for each scale from 0 to 28, each with a Long key, an
    indexed Decimal column D and an unindexed Decimal column E of precision 28 and that scale. ACE's OLE DB provider
    INSERTs each number into both columns, with a parameter of the number's own kind - a Decimal, a Double, a Single
    or text - one row each, and then looks for the row with = and the same parameter, in D (through its index) and in
    E (with none). DAO reads every row back, since it hands out a Decimal's sign where ACE's provider hands out a
    negative zero as zero; and DAO's AddNew is given each number too. Nothing of this library takes part.

    The numbers: for each scale, a 5, a 9, a 1, 4999, 5001, 15, 25 and 05 in the places past it, after a whole part
    of 0, 1 or 7 where the column has the digits, in both signs; and a few more at scales 0 and 2.

    Run it in Windows PowerShell 5.1, which has System.Data.OleDb, with a 64-bit ACE:
      powershell.exe -File tools\ace-decimal-places.ps1 -Out <full path>\ace-decimal-places.tsv

.PARAMETER Out
    The .tsv to write: a header of # lines, then one line per number and kind: scale, kind, the number as text, what
    ACE stored as DAO reads it (with a minus sign for a negative zero) or "refused", whether ACE's = found the row
    through the index and without one, and what DAO's AddNew did.

.PARAMETER DaoVersion
    The database's format, as DAO's CreateDatabase takes it: 64 for an .mdb (Access 2000), 128 for an .accdb.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [ValidateSet(64, 128, 256)] [int] $DaoVersion = 64,
    [string] $Provider = 'Microsoft.ACE.OLEDB.16.0'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($Out)) { throw "Out must be a full path." }
Add-Type -AssemblyName System.Data
$inv = [Globalization.CultureInfo]::InvariantCulture

function Show-Decimal([decimal] $m) {
    $t = $m.ToString($inv)
    if ($m -eq 0 -and [decimal]::GetBits($m)[3] -lt 0) { $t = '-' + $t }
    return $t
}

function Values-For([int] $s) {
    $list = New-Object 'System.Collections.Generic.List[string]'
    foreach ($tail in '5', '9', '1', '4999', '5001', '15', '25', '05') {
        if ($s + $tail.Length -gt 28) { continue }
        $frac = ('0' * $s) + $tail
        foreach ($whole in '0', '1', '7') {
            if ($whole -ne '0' -and $s + $tail.Length -gt 27) { continue }
            $list.Add($whole + '.' + $frac); $list.Add('-' + $whole + '.' + $frac)
        }
    }
    if ($s -eq 2) { foreach ($x in '1.015', '-1.015', '-0.001', '0.001', '2.675', '-2.675') { $list.Add($x) } }
    if ($s -eq 0) { foreach ($x in '2.5', '-2.5', '0.5', '-0.5', '3.5', '-0.4') { $list.Add($x) } }
    return $list
}

function Parameter-Of([string] $kind, [string] $text) {
    $p = $null
    switch ($kind) {
        'decimal' { $m = [decimal]::Parse($text, $inv); $p = New-Object System.Data.OleDb.OleDbParameter('v', [System.Data.OleDb.OleDbType]::Decimal); $p.Value = $m; $p.Precision = 28; $p.Scale = [byte](([decimal]::GetBits($m)[3] -shr 16) -band 0xFF) }
        'double'  { $p = New-Object System.Data.OleDb.OleDbParameter('v', [System.Data.OleDb.OleDbType]::Double); $p.Value = [double]::Parse($text, $inv) }
        'single'  { $p = New-Object System.Data.OleDb.OleDbParameter('v', [System.Data.OleDb.OleDbType]::Single); $p.Value = [single]::Parse($text, $inv) }
        'text'    { $p = New-Object System.Data.OleDb.OleDbParameter('v', [System.Data.OleDb.OleDbType]::VarWChar); $p.Value = $text }
    }
    return $p
}

function Value-Of([string] $kind, [string] $text) {
    switch ($kind) {
        'decimal' { return [decimal]::Parse($text, $inv) }
        'double'  { return [double]::Parse($text, $inv) }
        'single'  { return [single]::Parse($text, $inv) }
        default   { return $text }
    }
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ('ace-decimal-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$ext = '.accdb'; if ($DaoVersion -eq 64) { $ext = '.mdb' }
$path = Join-Path $dir ('decimal' + $ext)
$dbe = New-Object -ComObject DAO.DBEngine.120
$db = $dbe.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', $DaoVersion)
$db.Close()

$cases = New-Object 'System.Collections.Generic.List[object]'
$connection = New-Object System.Data.OleDb.OleDbConnection("Provider=$Provider;OLE DB Services=-4;Data Source=$path")
$connection.Open()
$id = 0
try {
    for ($s = 0; $s -le 28; $s++) {
        $c = $connection.CreateCommand()
        $c.CommandText = "CREATE TABLE T$s (Id LONG CONSTRAINT pk$s PRIMARY KEY, D DECIMAL(28, $s), E DECIMAL(28, $s))"
        [void]$c.ExecuteNonQuery()
        $c.CommandText = "CREATE INDEX ixD$s ON T$s (D)"
        [void]$c.ExecuteNonQuery()
        foreach ($text in (Values-For $s)) {
            foreach ($kind in 'decimal', 'double', 'single', 'text') {
                $id++
                $ins = $connection.CreateCommand()
                $ins.CommandText = "INSERT INTO T$s (Id, D, E) VALUES (?, ?, ?)"
                $idParam = New-Object System.Data.OleDb.OleDbParameter('id', [System.Data.OleDb.OleDbType]::Integer); $idParam.Value = $id
                [void]$ins.Parameters.Add($idParam); [void]$ins.Parameters.Add((Parameter-Of $kind $text)); [void]$ins.Parameters.Add((Parameter-Of $kind $text))
                $refused = $false
                try { [void]$ins.ExecuteNonQuery() } catch { $refused = $true }
                $found = @('', '')
                if (-not $refused) {
                    $i = 0
                    foreach ($col in 'D', 'E') {
                        $q = $connection.CreateCommand()
                        $q.CommandText = "SELECT Id FROM T$s WHERE $col = ?"
                        [void]$q.Parameters.Add((Parameter-Of $kind $text))
                        $r = $q.ExecuteReader(); $found[$i] = 'no'
                        while ($r.Read()) { if ($r.GetInt32(0) -eq $id) { $found[$i] = 'yes' } }
                        $r.Close(); $i++
                    }
                }
                $cases.Add(@($s, $kind, $text, $id, $refused, $found[0], $found[1]))
            }
        }
    }
} finally { $connection.Dispose() }

$setValue = { param($field, $value) [void]$field.GetType().InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $field, @($value)) }
$db = $dbe.OpenDatabase($path)
$lines = New-Object 'System.Collections.Generic.List[string]'
try {
    # What ACE stored, as DAO reads it.
    $stored = @{}
    for ($s = 0; $s -le 28; $s++) {
        $r = $db.OpenRecordset("SELECT Id, D FROM T$s", 4)
        while (-not $r.EOF) { $stored[[int]$r.Fields.Item('Id').Value] = Show-Decimal ([decimal]$r.Fields.Item('D').Value); $r.MoveNext() }
        $r.Close()
    }
    $lines.Add("# What ACE ($Provider) stores in a Decimal column of precision 28 and scale s for a number with more places, given as a typed parameter (#80),")
    $lines.Add("# and what DAO's AddNew does with it. Made by tools/ace-decimal-places.ps1 in a database DAO made with version $DaoVersion ($ext).")
    $lines.Add("# scale<TAB>kind<TAB>number as text<TAB>a double's or a single's bits in hex, as this script parsed the text (.NET Framework's parse, which is not always the nearest)<TAB>ACE stored (as DAO reads it; -0 a negative zero) or refused<TAB>ACE's = found it through the index<TAB>without one<TAB>DAO's AddNew")
    $n = 1000000
    foreach ($case in $cases) {
        $s = $case[0]; $kind = $case[1]; $text = $case[2]
        $n++
        $dao = ''
        $rs = $db.OpenRecordset("T$s", 1)
        try {
            $rs.AddNew()
            & $setValue $rs.Fields.Item('Id') ([int]$n)
            & $setValue $rs.Fields.Item('D') (Value-Of $kind $text)
            $rs.Update()
            $dao = 'stored'
        } catch {
            $m = $_.Exception.InnerException.Message; if (-not $m) { $m = $_.Exception.Message }
            $dao = 'refused: ' + $m
            try { $rs.CancelUpdate() } catch {}
        }
        $rs.Close()
        $ace = 'refused'; if (-not $case[4]) { $ace = $stored[[int]$case[3]] }
        $bits = ''
        if ($kind -eq 'double') { $bits = [BitConverter]::DoubleToInt64Bits((Value-Of $kind $text)).ToString('X16') }
        if ($kind -eq 'single') { $bits = [BitConverter]::ToInt32([BitConverter]::GetBytes([single](Value-Of $kind $text)), 0).ToString('X8') }
        $lines.Add("$s`t$kind`t$text`t$bits`t$ace`t$($case[5])`t$($case[6])`t$dao")
    }
} finally { $db.Close() }

[IO.File]::WriteAllText($Out, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "$($cases.Count) numbers written to $Out"
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
for ($try = 0; $try -lt 50 -and (Test-Path $dir); $try++) {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    try { Remove-Item -Recurse -Force $dir -ErrorAction Stop } catch { Start-Sleep -Milliseconds 200 }
}
if (Test-Path $dir) { Write-Warning "The scratch database is still held, and is left at $dir" }
