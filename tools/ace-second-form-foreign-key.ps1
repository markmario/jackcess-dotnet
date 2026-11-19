<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/ace-second-form-foreign-key.accdb: a parent keyed by a Date/Time Extended
    column and a child related to it, whose values ACE stored in the second form its SQL UPDATE leaves.

.DESCRIPTION
    ACE stores a Date/Time Extended value an INSERT gives it in one form, and one an SQL UPDATE gives it in another,
    and keys each by its stored bytes (#51). This library keys a value in the first form only, so an index entry
    of a row ACE stored in the second form is under a key this library does not make: a search of the index by
    its key misses the row, and a removal by it leaves the entry behind (AccessEntriesTests).

      P   Nm TEXT(10), D DATETIME2 primary key PK. Rows p1 (2020-01-01) and p2, inserted as 2020-01-05 and then
          changed by SQL UPDATE to 2020-01-02 03:04:05: the second form.
      C   Id LONG primary key CPK, PD DATETIME2, with the relationship FKPD from P.D (ACE gives PD an index of that
          name). Rows 1 and 2, both of p1; 2 then changed by SQL UPDATE to p2's value: the second form.

    ACE refuses to delete p2, which child 2 refers to. Made by ACE 16 (DAO.DBEngine.120) as an Access 2016 .accdb.
    The file holds the time it was made, so the fixture is committed, and this script is the record of how.

.EXAMPLE
    pwsh tools/ace-second-form-foreign-key.ps1
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\ace-second-form-foreign-key.accdb')
)

$ErrorActionPreference = 'Stop'
$outPath = [IO.Path]::GetFullPath($Out)
if (Test-Path $outPath) { Remove-Item $outPath }
$dbVersion140 = 256; $dbFailOnError = 128

$engine = New-Object -ComObject DAO.DBEngine.120
try {
    $db = $engine.CreateDatabase($outPath, ';LANGID=0x0409;CP=1252;COUNTRY=0', $dbVersion140)
    try {
        foreach ($sql in @(
            'CREATE TABLE P (Nm TEXT(10), D DATETIME2 CONSTRAINT PK PRIMARY KEY)',
            'CREATE TABLE C (Id LONG CONSTRAINT CPK PRIMARY KEY, PD DATETIME2)',
            'ALTER TABLE C ADD CONSTRAINT FKPD FOREIGN KEY (PD) REFERENCES P (D)',
            "INSERT INTO P (Nm, D) VALUES ('p1', '2020-01-01 00:00:00')",
            "INSERT INTO P (Nm, D) VALUES ('p2', '2020-01-05 00:00:00')",
            "UPDATE P SET D = '2020-01-02 03:04:05' WHERE Nm = 'p2'",
            "INSERT INTO C (Id, PD) VALUES (1, '2020-01-01 00:00:00')",
            "INSERT INTO C (Id, PD) VALUES (2, '2020-01-01 00:00:00')",
            "UPDATE C SET PD = '2020-01-02 03:04:05' WHERE Id = 2")) { $db.Execute($sql, $dbFailOnError) }
    }
    finally { $db.Close() }
}
finally {
    $db = $null
    try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine) } catch { }
    $engine = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}
"{0} ({1:N0} bytes), made by ACE" -f $outPath, (Get-Item $outPath).Length
