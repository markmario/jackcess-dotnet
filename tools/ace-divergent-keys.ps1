<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/ace-divergent-keys.accdb: tables ACE made and filled, with unique
    indexes whose entries include keys this library does not make as Access does, or did not.

.DESCRIPTION
    An entry is judged to be a row's by making the row's key and comparing. Where this library's key for a row is
    not Access's, an entry Access wrote looks like one that is no row's; the index repair must then leave it as it
    is, and count it as a duplicate (TableIndexes, AccessEntriesTests). The file holds such entries, made by ACE:

      Ext   Id LONG primary key, Nt TEXT(20), D DATETIME2 with a unique index DUnique. Three rows inserted; then
            rows 2 and 3 changed by SQL UPDATE, which stores a Date/Time Extended value in the second form ACE
            writes, keyed by Access from its stored bytes (#51): this library keys the date it reads as the first.
      Cut   Id LONG primary key, A TEXT(255), K LONG with a unique index ByAK (A, K). Six rows whose key is cut at
            510 bytes and ends in K: the digest this library left the last byte out of before #56.
      Dbl   Id LONG primary key, X DOUBLE with a unique index XU: -0.0 and 1.5.
      Dt    Id LONG primary key, T DATETIME with a unique index TU: dates ACE computed, not whole milliseconds.
      Pair  Id LONG primary key, A TEXT(255), X LONG, Nt TEXT(10) with a unique index ByAX (A, X): 255 of U+03A9
            with X 0 and with X 1, whose keys differ only in the last byte cut off. The digest this library used
            before #56 left that byte out, so the key it gives the second is Access's key of the first.

    Made by ACE 16 (DAO.DBEngine.120) as an Access 2016 .accdb, which has Date/Time Extended. The file holds the
    time it was made, so the fixture is committed, and this script is the record of how.

.EXAMPLE
    pwsh tools/ace-divergent-keys.ps1
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\ace-divergent-keys.accdb')
)

$ErrorActionPreference = 'Stop'
$outPath = [IO.Path]::GetFullPath($Out)
if (Test-Path $outPath) { Remove-Item $outPath }
$dbVersion140 = 256; $dbFailOnError = 128

$engine = New-Object -ComObject DAO.DBEngine.120
try {
    $db = $engine.CreateDatabase($outPath, ';LANGID=0x0409;CP=1252;COUNTRY=0', $dbVersion140)
    try {
        $db.Execute('CREATE TABLE Ext (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Nt TEXT(20), D DATETIME2)', $dbFailOnError)
        $db.Execute('CREATE UNIQUE INDEX DUnique ON Ext (D)', $dbFailOnError)
        $db.Execute("INSERT INTO Ext (Id, D, Nt) VALUES (1, '2020-01-01 00:00:00', 'one')", $dbFailOnError)
        $db.Execute("INSERT INTO Ext (Id, D, Nt) VALUES (2, '2020-01-02 03:04:05.1234567', 'two')", $dbFailOnError)
        $db.Execute("INSERT INTO Ext (Id, D, Nt) VALUES (3, '2020-01-03 00:00:00', 'three')", $dbFailOnError)
        $db.Execute("UPDATE Ext SET D = '2020-01-02 03:04:05.1234568' WHERE Id = 2", $dbFailOnError)
        $db.Execute("UPDATE Ext SET D = '2020-01-03 01:02:03' WHERE Id = 3", $dbFailOnError)

        $db.Execute('CREATE TABLE Cut (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, A TEXT(255), K LONG)', $dbFailOnError)
        $db.Execute('CREATE UNIQUE INDEX ByAK ON Cut (A, K)', $dbFailOnError)
        $rs = $db.OpenRecordset('Cut', 1)
        $omega = [string]::new([char]0x03A9, 255)
        foreach ($row in @(@(1, $omega, 1), @(2, $omega, 2), @(3, [string]::new([char]0x03B1, 255), 1),
                           @(4, [string]::new([char]'a', 255), 1), @(5, [string]::new([char]0x03AA, 200), 1), @(6, $omega, 3))) {
            $rs.AddNew()
            # Each value cast where it is set: PowerShell otherwise hands a Field's Value the type of the last one set.
            $rs.Fields.Item('Id').Value = [int]$row[0]
            $rs.Fields.Item('A').Value = [string]$row[1]
            $rs.Fields.Item('K').Value = [int]$row[2]
            $rs.Update()
        }
        $rs.Close()

        $db.Execute('CREATE TABLE Dbl (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, X DOUBLE)', $dbFailOnError)
        $db.Execute('CREATE UNIQUE INDEX XU ON Dbl (X)', $dbFailOnError)
        $rs = $db.OpenRecordset('Dbl', 1)
        foreach ($row in @(@(1, [BitConverter]::Int64BitsToDouble([long]::MinValue)), @(2, 1.5))) {
            $rs.AddNew()
            $rs.Fields.Item('Id').Value = [int]$row[0]
            $rs.Fields.Item('X').Value = [double]$row[1]
            $rs.Update()
        }
        $rs.Close()

        $db.Execute('CREATE TABLE Dt (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, T DATETIME)', $dbFailOnError)
        $db.Execute('CREATE UNIQUE INDEX TU ON Dt (T)', $dbFailOnError)
        $db.Execute('INSERT INTO Dt (Id, T) VALUES (1, #2020-01-01# + 1/7)', $dbFailOnError)
        $db.Execute('INSERT INTO Dt (Id, T) VALUES (2, CDate(43831.1234567891234))', $dbFailOnError)
        $db.Execute("INSERT INTO Dt (Id, T) VALUES (3, DateAdd('s', 1, #2020-01-01 00:00:00#) + 0.000000001)", $dbFailOnError)
        $db.Execute('INSERT INTO Dt (Id, T) VALUES (4, CDate(-0.5))', $dbFailOnError)

        $db.Execute('CREATE TABLE Pair (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, A TEXT(255), X LONG, Nt TEXT(10))', $dbFailOnError)
        $db.Execute('CREATE UNIQUE INDEX ByAX ON Pair (A, X)', $dbFailOnError)
        $rs = $db.OpenRecordset('Pair', 1)
        foreach ($row in @(@(1, 0), @(2, 1))) {
            $rs.AddNew()
            $rs.Fields.Item('Id').Value = [int]$row[0]
            $rs.Fields.Item('A').Value = [string]$omega
            $rs.Fields.Item('X').Value = [int]$row[1]
            $rs.Fields.Item('Nt').Value = [string]'ace'
            $rs.Update()
        }
        $rs.Close()
    }
    finally { $db.Close() }
}
finally {
    $db = $null; $rs = $null
    try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine) } catch { }
    $engine = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}
"{0} ({1:N0} bytes), made by ACE" -f $outPath, (Get-Item $outPath).Length
