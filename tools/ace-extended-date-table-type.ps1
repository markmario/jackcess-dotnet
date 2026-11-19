<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/ace-extended-date-table-type.accdb: Date/Time Extended values in the
    layout DAO's table-type recordset writes, beside the forms Access's SQL writes, keyed by ACE in its own indexes.

.DESCRIPTION
    DAO's AddNew and Edit on a table-type recordset - what DAO's OpenRecordset gives a table of the database when it
    is asked for no other kind - store a Date/Time Extended value in a layout of their own (#64): the day in fifteen
    digits, the time of day in 100-nanosecond units, ":7", a zero byte, and four bytes that are whatever DAO's buffer
    held, which differ from row to row:

      0000000000000737425:0000000110450000000:7\0      2 January 2020, 03:04:05, as INSERT writes it
      0000000000000737425:0000000000000011045:0\0      the same, as UPDATE and DAO's dynaset write it
      000000000737425:0000000110450000000:7\0 ....     the same, as DAO's table-type recordset writes it

    ACE keys each row by its 42 bytes, the four after the zero byte among them, whether the index was there when DAO
    added the row or was made after; DAO reads each as its date. The file holds:

      T   Id LONG primary key TPK, How TEXT(40), K DATETIME2; indexes KAsc (K), KDesc (K DESC) and KHow (K, How), made
          before the rows, so that ACE keyed each as it was added:
             1  2020-01-02 03:04:05             INSERT                       the first form
             2  2020-01-02 03:04:05             UPDATE                       the second form
             3  2020-01-02 03:04:05             AddNew, dynaset              the second form
             4  2020-01-02 03:04:05             AddNew, table-type, text     the third layout
             5  2020-01-02 03:04:05             AddNew, table-type, a Date   the third layout
             6  2020-01-02 03:04:05             Edit, table-type, text       the third layout
             7  2020-01-02 03:04:05.1234567     AddNew, table-type           the third layout
             8  2020-01-02 03:04:05.1234567     INSERT                       the first form
             9  2020-01-02 00:00:00             AddNew, table-type           the third layout
            10  2020-01-02 00:00:00             INSERT                       the first form
            11  2020-01-02 03:04:06             AddNew, table-type           the third layout
            12  2020-01-01 23:59:59             AddNew, table-type           the third layout
            13  0001-01-01 00:00:00             AddNew, table-type           the third layout
            14  9999-12-31 23:59:59.9999999     AddNew, table-type           the third layout
          Without an index on K, ACE's WHERE K = '2020-01-02 03:04:05' finds rows 1 to 6; through KAsc, row 1 alone.
      P/C P (Nm TEXT(10), D DATETIME2 primary key PPK) and C (Id LONG primary key CPK, PD DATETIME2), with the
          relationship FKPD from C.PD to P.D, with referential integrity. p1 is 2 January 2020 03:04:05 by a
          table-type AddNew, p2 the same by INSERT and p3 by UPDATE: three keys. Children 1 and 4 are copied from p1
          by INSERT ... SELECT, which copies the 42 bytes as they are; 2 is given the date by INSERT, so p2's; 3 is
          copied from p3.
      U   Id LONG primary key UPK, UHow TEXT(40), K DATETIME2, and the unique index KU made before the rows: row 1,
          2 January 2020 03:04:05 by a table-type AddNew.
      W   Id LONG primary key WPK, How TEXT(40), K DATETIME2, and no index on K: 1 the date by INSERT, 2 and 3 by a
          table-type AddNew (the second a fraction on), 4 by UPDATE - for an index to be added over by an earlier
          version of this library (tools/earlier-extended-date-keys.ps1).

    Made by ACE 16 (DAO.DBEngine.120) as an Access 2016 .accdb. The file holds the time it was made, and the four
    bytes DAO leaves after a value differ from run to run, so the fixture is committed, and this script is the
    record of how.

.EXAMPLE
    pwsh tools/ace-extended-date-table-type.ps1
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\ace-extended-date-table-type.accdb')
)

$ErrorActionPreference = 'Stop'
$outPath = [IO.Path]::GetFullPath($Out)
if (Test-Path $outPath) { Remove-Item $outPath }
$dbVersion140 = 256; $dbFailOnError = 128; $dbOpenTable = 1; $dbOpenDynaset = 2
$D0 = '2020-01-02 03:04:05'

# A field's value, given as the type it is (text as text, a DateTime as a Date): PowerShell's own assignment can
# convert it on the way.
function Set-Field($recordset, [string]$name, $value) {
    $field = $recordset.Fields.Item($name)
    [void]$field.GetType().InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $field, @($value))
}

function Add-Row($db, [string]$table, [int]$type, [hashtable]$values) {
    $rs = $db.OpenRecordset($table, $type)
    try {
        $rs.AddNew()
        foreach ($name in $values.Keys) { Set-Field $rs $name $values[$name] }
        $rs.Update()
    }
    finally { $rs.Close() }
}

$engine = New-Object -ComObject DAO.DBEngine.120
try {
    $db = $engine.CreateDatabase($outPath, ';LANGID=0x0409;CP=1252;COUNTRY=0', $dbVersion140)
    try {
        foreach ($sql in @(
            'CREATE TABLE T (Id LONG CONSTRAINT TPK PRIMARY KEY, How TEXT(40), K DATETIME2)',
            'CREATE INDEX KAsc ON T (K)', 'CREATE INDEX KDesc ON T (K DESC)', 'CREATE INDEX KHow ON T (K, How)')) { $db.Execute($sql, $dbFailOnError) }

        $db.Execute("INSERT INTO T (Id, How, K) VALUES (1, 'INSERT', '$D0')", $dbFailOnError)
        $db.Execute("INSERT INTO T (Id, How, K) VALUES (2, 'UPDATE', '2020-01-09 00:00:00')", $dbFailOnError)
        $db.Execute("UPDATE T SET K = '$D0' WHERE Id = 2", $dbFailOnError)
        Add-Row $db 'T' $dbOpenDynaset @{ Id = 3; How = 'AddNew, dynaset'; K = $D0 }
        Add-Row $db 'T' $dbOpenTable @{ Id = 4; How = 'AddNew, table-type, text'; K = $D0 }
        Add-Row $db 'T' $dbOpenTable @{ Id = 5; How = 'AddNew, table-type, a Date'; K = [datetime]::new(2020, 1, 2, 3, 4, 5) }
        $db.Execute("INSERT INTO T (Id, How, K) VALUES (6, 'Edit, table-type, text', '2020-01-09 00:00:00')", $dbFailOnError)
        $rs = $db.OpenRecordset('T', $dbOpenTable)
        try { $rs.Index = 'TPK'; $rs.Seek('=', 6); $rs.Edit(); Set-Field $rs 'K' $D0; $rs.Update() } finally { $rs.Close() }
        Add-Row $db 'T' $dbOpenTable @{ Id = 7; How = 'AddNew, table-type'; K = '2020-01-02 03:04:05.1234567' }
        $db.Execute("INSERT INTO T (Id, How, K) VALUES (8, 'INSERT', '2020-01-02 03:04:05.1234567')", $dbFailOnError)
        Add-Row $db 'T' $dbOpenTable @{ Id = 9; How = 'AddNew, table-type'; K = '2020-01-02 00:00:00' }
        $db.Execute("INSERT INTO T (Id, How, K) VALUES (10, 'INSERT', '2020-01-02 00:00:00')", $dbFailOnError)
        Add-Row $db 'T' $dbOpenTable @{ Id = 11; How = 'AddNew, table-type'; K = '2020-01-02 03:04:06' }
        Add-Row $db 'T' $dbOpenTable @{ Id = 12; How = 'AddNew, table-type'; K = '2020-01-01 23:59:59' }
        Add-Row $db 'T' $dbOpenTable @{ Id = 13; How = 'AddNew, table-type'; K = '0001-01-01 00:00:00' }
        Add-Row $db 'T' $dbOpenTable @{ Id = 14; How = 'AddNew, table-type'; K = '9999-12-31 23:59:59.9999999' }

        foreach ($sql in @(
            'CREATE TABLE P (Nm TEXT(10), D DATETIME2 CONSTRAINT PPK PRIMARY KEY)',
            'CREATE TABLE C (Id LONG CONSTRAINT CPK PRIMARY KEY, PD DATETIME2)',
            'ALTER TABLE C ADD CONSTRAINT FKPD FOREIGN KEY (PD) REFERENCES P (D)')) { $db.Execute($sql, $dbFailOnError) }
        Add-Row $db 'P' $dbOpenTable @{ Nm = 'p1'; D = $D0 }
        $db.Execute("INSERT INTO P (Nm, D) VALUES ('p2', '$D0')", $dbFailOnError)
        $db.Execute("INSERT INTO P (Nm, D) VALUES ('p3', '2020-01-09 00:00:00')", $dbFailOnError)
        $db.Execute("UPDATE P SET D = '$D0' WHERE Nm = 'p3'", $dbFailOnError)
        foreach ($sql in @(
            "INSERT INTO C (Id, PD) SELECT 1, D FROM P WHERE Nm = 'p1'",
            "INSERT INTO C (Id, PD) VALUES (2, '$D0')",
            "INSERT INTO C (Id, PD) SELECT 3, D FROM P WHERE Nm = 'p3'",
            "INSERT INTO C (Id, PD) SELECT 4, D FROM P WHERE Nm = 'p1'")) { $db.Execute($sql, $dbFailOnError) }

        $db.Execute('CREATE TABLE U (Id LONG CONSTRAINT UPK PRIMARY KEY, UHow TEXT(40), K DATETIME2)', $dbFailOnError)
        $db.Execute('CREATE UNIQUE INDEX KU ON U (K)', $dbFailOnError)
        Add-Row $db 'U' $dbOpenTable @{ Id = 1; UHow = 'AddNew, table-type'; K = $D0 }

        $db.Execute('CREATE TABLE W (Id LONG CONSTRAINT WPK PRIMARY KEY, How TEXT(40), K DATETIME2)', $dbFailOnError)
        $db.Execute("INSERT INTO W (Id, How, K) VALUES (1, 'INSERT', '$D0')", $dbFailOnError)
        Add-Row $db 'W' $dbOpenTable @{ Id = 2; How = 'AddNew, table-type'; K = $D0 }
        Add-Row $db 'W' $dbOpenTable @{ Id = 3; How = 'AddNew, table-type'; K = '2020-01-02 03:04:05.1234567' }
        $db.Execute("INSERT INTO W (Id, How, K) VALUES (4, 'UPDATE', '2020-01-09 00:00:00')", $dbFailOnError)
        $db.Execute("UPDATE W SET K = '$D0' WHERE Id = 4", $dbFailOnError)
    }
    finally { $db.Close() }
}
finally {
    $db = $null; $rs = $null
    try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine) } catch { }
    $engine = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}
"{0} ({1:N0} bytes), made by ACE" -f $outPath, (Get-Item $outPath).Length
