<#
.SYNOPSIS
    Makes the password-protected and encoded databases in tests/JackcessDotNet.Tests/Assets that the tests of
    Database.Open read (ProtectedFileTests, OfficeCryptTests), and dao-protected-rows.tsv beside them: the rows of
    each, as the engine that made the file reads them.

.DESCRIPTION
    Each file has one table, Items, made with DAO and filled by it: Id LONG primary key, Name TEXT(50), Notes MEMO,
    Amount CURRENCY, Ratio DOUBLE, Stamp DATETIME and Flag YESNO, 60 rows. Every value comes from the row's Id
    (Get-Row below), Notes are null in every tenth row and run to 9,000 characters, so a file has several data
    pages and long values that take several pages of their own. Every protected page is one Access protected.

      dao-jet4-password.mdb          Access 2000-2003, a database password (CreateDatabase ";pwd=").
      dao-jet4-encoded.mdb           Access 2000-2003, encoded (CreateDatabase's dbEncrypt), no password.
      dao-jet4-password-encoded.mdb  both.
      dao-jet3-password.mdb          Access 97, the same three, made by Jet 4.0 (DAO.DBEngine.36, 32-bit):
      dao-jet3-encoded.mdb           ACE 16 neither makes nor opens an Access 97 file.
      dao-jet3-password-encoded.mdb
      dao-accdb2007-password.accdb   dbVersion120, with a password: ACE 16 encrypts it (Agile, AES-256 and SHA-512),
                                     and gives it the Access 2010 version byte, 0x03, as the encryption needs.
      dao-accdb2010-password.accdb   dbVersion140, with a password: the same encryption.

    The password of every protected file is P{a-umlaut}ssw{euro}rd7: nine characters, two outside ASCII, all of
    them in code page 1252, which an Access 97 file keeps its password in. A Jet password is kept in the file's
    header (in Unicode in Access 2000-2003, in the code page in Access 97) and Access checks it exactly, case and
    all. Encoding does not use it: the key is in the header, and Access opens an encoded file without a password.

    Once each file is closed, the engine that made it opens it again with the password and reads Items back; a
    value that is not the one written stops the script. The rows of dao-jet4-password.mdb as DAO 16 reads them go
    to dao-protected-rows.tsv, and every file is checked to read the same.

    The files are not the same bytes on every run (each holds the time it was made, and an encrypted one its own
    salt), so they are committed, and this script is how they were made. It needs DAO 16 (Microsoft Access, or the
    Access Database Engine) for a 64-bit PowerShell, and Jet 4.0's DAO 3.6, which is part of 32-bit Windows, for
    the Access 97 files: run in a 64-bit PowerShell, it runs itself again in the 32-bit Windows PowerShell for them.

.EXAMPLE
    pwsh tools/protected-fixtures.ps1
#>
[CmdletBinding()]
param(
    # Where to write the files (tests/JackcessDotNet.Tests/Assets when not given).
    [string]$Out,
    # Makes the Access 97 files only; the 64-bit run passes it to the 32-bit one.
    [switch]$Jet3
)

$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\tests\JackcessDotNet.Tests\Assets' }
$outDir = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $outDir | Out-Null

$password  = 'P' + [char]0x00E4 + 'ssw' + [char]0x20AC + 'rd7'
$general   = ';LANGID=0x0409;CP=1252;COUNTRY=0'
$dbEncrypt = 2
$alphabet  = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
$invariant = [Globalization.CultureInfo]::InvariantCulture

# The values of row $id, in column order.
function Get-Row([int]$id) {
    $name = 'Item ' + $id + ' Gr' + [char]0x00F6 + [char]0x00DF + 'e ' + [char]0x00E9 + [char]0x00E8
    $length = switch ($id) { 57 { 3000 } 58 { 5000 } 59 { 9000 } default { ($id * 37) % 200 } }
    $notes = $null
    if ($id % 10 -ne 0) {
        $chars = New-Object char[] $length
        for ($p = 0; $p -lt $length; $p++) { $chars[$p] = $alphabet[($id * 31 + $p * 7) % 62] }
        # A plain string: Windows PowerShell hands DAO one New-Object makes as a wrapper, which DAO stores as null.
        $notes = -join $chars
    }
    $amount = [decimal]$id * [decimal]1234.5678
    $ratio  = [double]$id / 7
    $stamp  = (New-Object DateTime 2026, 1, 1).AddDays($id).AddMinutes($id * 7)
    $flag   = ($id % 3) -eq 0
    , @($id, $name, $notes, $amount, $ratio, $stamp, $flag)
}

function Format-Row($values) {
    $cells = foreach ($v in $values) {
        if ($null -eq $v -or $v -is [DBNull]) { '\N' }
        elseif ($v -is [double]) { $v.ToString('R', $invariant) }
        elseif ($v -is [DateTime]) { $v.ToString('yyyy-MM-ddTHH:mm:ss', $invariant) }
        elseif ($v -is [decimal]) { $v.ToString('0.############################', $invariant) }
        else { [string]$v }
    }
    $cells -join "`t"
}

function Set-FieldValue($field, $value) {
    [void]$field.GetType().InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $field, @($value))
}

function Get-FieldValue($field) {
    $field.GetType().InvokeMember('Value', [Reflection.BindingFlags]::GetProperty, $null, $field, $null)
}

function New-Fixture($engine, [string]$file, [int]$version, [bool]$withPassword, [bool]$encoded) {
    $path = Join-Path $outDir $file
    if (Test-Path $path) { Remove-Item $path }
    $locale = $general
    if ($withPassword) { $locale = $general + ';pwd=' + $password }
    $options = $version
    if ($encoded) { $options = $version + $dbEncrypt }
    $db = $engine.CreateDatabase($path, $locale, $options)
    try {
        $db.Execute('CREATE TABLE Items (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Name TEXT(50), Notes MEMO, Amount CURRENCY, Ratio DOUBLE, Stamp DATETIME, Flag YESNO)')
        $rows = $db.OpenRecordset('Items', 1)
        foreach ($id in 1..60) {
            $values = Get-Row $id
            $rows.AddNew()
            # Through IDispatch: PowerShell 7's binder keeps the type of the first value a call site sets a field's
            # Value to, and refuses every other type there.
            for ($c = 0; $c -lt 7; $c++) {
                if ($null -ne $values[$c]) { Set-FieldValue $rows.Fields.Item($c) $values[$c] }
            }
            $rows.Update()
        }
        $rows.Close()
    }
    finally { $db.Close() }
    $path
}

# What the engine reads of Items, a line a row in Id order; stops at a value that is not the one written.
function Read-Fixture($engine, [string]$path, [bool]$withPassword) {
    $connect = ''
    if ($withPassword) { $connect = ';pwd=' + $password }
    $db = $engine.OpenDatabase($path, $false, $true, $connect)
    try {
        $lines = New-Object System.Collections.ArrayList
        $rows = $db.OpenRecordset('SELECT Id, Name, Notes, Amount, Ratio, Stamp, Flag FROM Items ORDER BY Id')
        while (-not $rows.EOF) {
            $read = for ($c = 0; $c -lt 7; $c++) { Get-FieldValue $rows.Fields.Item($c) }
            $line = Format-Row $read
            $expected = Format-Row (Get-Row ([int]$read[0]))
            if ($line -ne $expected) { throw "$([IO.Path]::GetFileName($path)): row $($read[0]) reads $line, not $expected." }
            [void]$lines.Add($line)
            $rows.MoveNext()
        }
        $rows.Close()
    }
    finally { $db.Close() }
    if ($lines.Count -ne 60) { throw "$([IO.Path]::GetFileName($path)): $($lines.Count) rows, not 60." }
    , $lines
}

function Get-Header([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    'version byte 0x{0:X2}, {1} bytes' -f $b[0x14], $b.Length
}

if ($Jet3) {
    if ([Environment]::Is64BitProcess) { throw 'Jet 4.0 is 32-bit: the Access 97 files are made in the 32-bit Windows PowerShell.' }
    $engine = New-Object -ComObject DAO.DBEngine.36
    $dbVersion30 = 32
    foreach ($f in @(@('dao-jet3-password.mdb', $true, $false), @('dao-jet3-encoded.mdb', $false, $true), @('dao-jet3-password-encoded.mdb', $true, $true))) {
        $path = New-Fixture $engine $f[0] $dbVersion30 $f[1] $f[2]
        [void](Read-Fixture $engine $path $f[1])
        "$($f[0]): DAO $($engine.Version), $(Get-Header $path)"
    }
    return
}

if (-not [Environment]::Is64BitProcess) { throw 'Run this in a 64-bit PowerShell: it makes the Access 2000 and .accdb files with DAO 16.' }
$engine = New-Object -ComObject DAO.DBEngine.120
$dbVersion40 = 64; $dbVersion120 = 128; $dbVersion140 = 256
$lines = $null
try {
    foreach ($f in @(@('dao-jet4-password.mdb', $dbVersion40, $true, $false), @('dao-jet4-encoded.mdb', $dbVersion40, $false, $true),
                     @('dao-jet4-password-encoded.mdb', $dbVersion40, $true, $true),
                     @('dao-accdb2007-password.accdb', $dbVersion120, $true, $false), @('dao-accdb2010-password.accdb', $dbVersion140, $true, $false))) {
        $path = New-Fixture $engine $f[0] $f[1] $f[2] $f[3]
        $read = Read-Fixture $engine $path $f[2]
        if ($null -eq $lines) { $lines = $read }
        "$($f[0]): DAO $($engine.Version), $(Get-Header $path)"
    }
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

$tsv = Join-Path $outDir 'dao-protected-rows.tsv'
$header = '# Id, Name, Notes, Amount, Ratio, Stamp, Flag of every row of Items in the dao-*-password*.mdb/.accdb and dao-*-encoded.mdb files, as DAO 16 reads dao-jet4-password.mdb with its password; every other file reads the same. \N is a null. Made by tools/protected-fixtures.ps1.'
[IO.File]::WriteAllText($tsv, (@($header) + $lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))

$self = $MyInvocation.MyCommand.Path
& "$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $self -Out $outDir -Jet3
if ($LASTEXITCODE -ne 0) { throw "The Access 97 files were not made (exit code $LASTEXITCODE)." }
