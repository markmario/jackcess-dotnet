<#
.SYNOPSIS
    Has Microsoft's own database engine store numbers in a Currency column, and writes what it stored to a .tsv file.

.DESCRIPTION
    tests/JackcessDotNet.Tests/Assets/ace-currency-writes.tsv is made by this script (#65). Microsoft's engine is the
    referee for the Currency a number becomes: this library must store the same ten-thousandths for it, or a row it
    writes with a number is not the row Access writes with that number, and is not found by it.

    The script creates a database with DAO, a table in it with a Long key and a Currency column, and has ACE's OLE DB
    provider INSERT each number into it with a parameter of the number's own type - a Double, a Single or a Decimal -
    one row each. It then reads every row back and writes, for each number, what the row holds, or that ACE refused
    the INSERT. Nothing of this library takes part, so the file is evidence of what Microsoft's engine does and of
    nothing else.

    The numbers are those that tell one rounding from another: a double or a single near each half of the fifth place
    after the point (written x.xxxx5) at magnitudes from 0 to 5.6E+10, every exact half there is (an odd number of
    thirty-seconds) with the doubles just either side of it, in both signs; doubles of more significant digits than
    15; decimals of the same and of 28 digits; the ends of a Currency's range and past them; NaN and the infinities.
    The same numbers are written in an .mdb (-DaoVersion 64) and an .accdb (128) alike, as the script was run to see.

    Run it in Windows PowerShell 5.1, which has System.Data.OleDb, with a 64-bit ACE:
      powershell.exe -File tools\ace-currency.ps1 -Out tests\JackcessDotNet.Tests\Assets\ace-currency-writes.tsv

.PARAMETER Out
    The .tsv to write: a header of # lines, then one line per number: its kind (double, single or decimal), the number
    (a double's or a single's bits in hex, a decimal as text) and what ACE stored - the ten-thousandths, as a whole
    number - or "refused".

.PARAMETER DaoVersion
    The database's format, as DAO's CreateDatabase takes it: 64 for an .mdb (Access 2000), 128 for an .accdb.

.PARAMETER Provider
    The OLE DB provider: Microsoft.ACE.OLEDB.16.0 by default.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [ValidateSet(64, 128, 256)] [int] $DaoVersion = 128,
    [string] $Provider = 'Microsoft.ACE.OLEDB.16.0'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
$inv = [Globalization.CultureInfo]::InvariantCulture

function Next-Up([double] $d) {
    if ([double]::IsNaN($d) -or [double]::IsPositiveInfinity($d)) { return $d }
    if ($d -eq 0) { return [double]::Epsilon }
    $bits = [BitConverter]::DoubleToInt64Bits($d)
    if ($d -gt 0) { $bits++ } else { $bits-- }
    return [BitConverter]::Int64BitsToDouble($bits)
}
function Next-Down([double] $d) { return -(Next-Up (-$d)) }

# ── The numbers ────────────────────────────────────────────────────────────

$halves = '0', '1', '7', '123', '9999', '123456', '98765432', '4503599627', '56294995342'
$doubles = New-Object 'System.Collections.Generic.List[double]'
foreach ($whole in $halves) {
    for ($j = 0; $j -lt 10000; $j += 37) { $doubles.Add([double]::Parse($whole + '.' + $j.ToString('0000', $inv) + '5', $inv)) }
}
# Every exact half of the fifth place is an odd number of thirty-seconds: those, and the doubles either side of each.
foreach ($whole in 0.0, 1.0, 1000.0, [math]::Pow(2, 30), [math]::Pow(2, 40), [math]::Pow(2, 45)) {
    for ($k = 1; $k -lt 64; $k += 2) {
        $half = $whole + $k / 32.0
        foreach ($x in $half, (Next-Down $half), (Next-Up $half)) { $doubles.Add($x); $doubles.Add(-$x) }
    }
}
foreach ($text in '562949953421312.5', '-562949953421312.5', '900719925474.0992', '922337203685477.375', '922337203685477.5',
                  '-922337203685477.5', '922337203685477.625', '-922337203685477.625', '35184372088832.0078125', '131072.015625',
                  '1.2345678901234567', '0.30000000000000004', '0.1', '1.234549999999999', '0.00015', '0.00025', '1.23465',
                  '1E15', '-1E15', '1E20', '1E300', '-0', '4.9406564584124654E-324', '1E-300', '12345678901.23455', '123456789012.34565') {
    $doubles.Add([double]::Parse($text, $inv))
}
$doubles.Add([double]::NaN); $doubles.Add([double]::PositiveInfinity); $doubles.Add([double]::NegativeInfinity)

$singles = New-Object 'System.Collections.Generic.List[single]'
for ($i = 0; $i -lt $doubles.Count; $i += 3) { if ([math]::Abs($doubles[$i]) -lt 1E9) { $singles.Add([single]$doubles[$i]) } }
foreach ($x in [single]0.03125, [single](-0.03125), [single]0.09375, [single]1.03125, [single]0.00015, [single]131072.015625,
               [single]2048.000244140625, [single]16777216, [single]1E15, [single]3.4E38, [single]::NaN,
               [single]::PositiveInfinity, [single]::NegativeInfinity) { $singles.Add($x) }

$decimals = New-Object 'System.Collections.Generic.List[decimal]'
foreach ($whole in '0', '1', '123', '98765432', '56294995342') {
    for ($j = 0; $j -lt 10000; $j += 37) {
        $decimals.Add([decimal]::Parse($whole + '.' + $j.ToString('0000', $inv) + '5', $inv))
        $decimals.Add([decimal]::Parse('-' + $whole + '.' + $j.ToString('0000', $inv) + '5', $inv))
    }
}
foreach ($text in '0.03125', '-0.03125', '0.09375', '1.03125', '1.23465', '-1.23465', '0.00015', '0.00025',
                  '0.000150000000000000000000001', '0.000149999999999999999999999', '0.0002500000000000000000000001',
                  '922337203685477.5807', '922337203685477.5808', '-922337203685477.5808', '-922337203685477.5809',
                  '922337203685477.58065', '922337203685477.58074', '922337203685477.58075', '-922337203685477.58075',
                  '-922337203685477.58085', '-922337203685477.58095', '12345678901234.56785', '1.23456789',
                  '100000000000000000000', '0.1', '79228162514264337593543950335') {
    $decimals.Add([decimal]::Parse($text, $inv))
}

# ── ACE writes them ────────────────────────────────────────────────────────

$extension = '.accdb'
if ($DaoVersion -eq 64) { $extension = '.mdb' }
$dir = Join-Path ([IO.Path]::GetTempPath()) ('ace-currency-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$path = Join-Path $dir ('currency' + $extension)

$dbe = New-Object -ComObject DAO.DBEngine.120
$db = $dbe.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', $DaoVersion)
$db.Close()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
[GC]::Collect(); [GC]::WaitForPendingFinalizers()

$rows = New-Object 'System.Collections.Generic.List[object]'
foreach ($d in $doubles) { $rows.Add(@('double', [System.Data.OleDb.OleDbType]::Double, $d, [BitConverter]::DoubleToInt64Bits($d).ToString('X16'))) }
foreach ($s in $singles) { $rows.Add(@('single', [System.Data.OleDb.OleDbType]::Single, $s, [BitConverter]::ToInt32([BitConverter]::GetBytes($s), 0).ToString('X8'))) }
foreach ($m in $decimals) { $rows.Add(@('decimal', [System.Data.OleDb.OleDbType]::Decimal, $m, $m.ToString($inv))) }

$connection = New-Object System.Data.OleDb.OleDbConnection("Provider=$Provider;OLE DB Services=-4;Data Source=$path")
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandText = 'CREATE TABLE W (Id LONG CONSTRAINT pkW PRIMARY KEY, C CURRENCY)'
    [void]$command.ExecuteNonQuery()

    $refused = @{}
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $insert = $connection.CreateCommand()
        $insert.CommandText = 'INSERT INTO W (Id, C) VALUES (?, ?)'
        $id = New-Object System.Data.OleDb.OleDbParameter('id', [System.Data.OleDb.OleDbType]::Integer)
        $id.Value = $i
        $value = New-Object System.Data.OleDb.OleDbParameter('c', $rows[$i][1])
        $value.Value = $rows[$i][2]
        if ($rows[$i][0] -eq 'decimal') {
            $value.Precision = 28
            $value.Scale = [byte](([decimal]::GetBits($rows[$i][2])[3] -shr 16) -band 0xFF)
        }
        [void]$insert.Parameters.Add($id); [void]$insert.Parameters.Add($value)
        try { [void]$insert.ExecuteNonQuery() } catch { $refused[$i] = $true }
    }

    $stored = @{}
    $select = $connection.CreateCommand()
    $select.CommandText = 'SELECT Id, C FROM W'
    $reader = $select.ExecuteReader()
    while ($reader.Read()) { $stored[$reader.GetInt32(0)] = [long]($reader.GetDecimal(1) * 10000) }
    $reader.Close()
} finally { $connection.Dispose() }

$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add("# What ACE stores in a Currency column for a number an INSERT gives it as a parameter of the number's own type (#65).")
$lines.Add("# Made by tools/ace-currency.ps1 with $Provider, in a database DAO made with version $DaoVersion ($extension).")
$lines.Add("# kind<TAB>number<TAB>stored: a double's or a single's bits in hex, a decimal as text; the ten-thousandths stored, or refused.")
for ($i = 0; $i -lt $rows.Count; $i++) {
    $result = 'refused'
    if (-not $refused.ContainsKey($i)) { $result = $stored[$i].ToString($inv) }
    $lines.Add($rows[$i][0] + "`t" + $rows[$i][3] + "`t" + $result)
}
[IO.File]::WriteAllText($Out, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host ("{0} numbers, {1} refused, written to {2}" -f $rows.Count, $refused.Count, $Out)

# ACE lets go of the file a moment after its connection is closed.
for ($try = 0; $try -lt 50 -and (Test-Path $dir); $try++) {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    try { Remove-Item -Recurse -Force $dir -ErrorAction Stop } catch { Start-Sleep -Milliseconds 200 }
}
if (Test-Path $dir) { Write-Warning "The scratch database is still held, and is left at $dir" }
