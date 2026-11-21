<#
.SYNOPSIS
    Records how each of Access's ways of reading a table reads compressed text in a column without Unicode compression.

.DESCRIPTION
    Versions of this library before 442bbf8 wrote Text values of characters 0x01 to 0xFF compressed - 0xFF 0xFE and
    a byte each - whatever the column's Unicode compression (#93). Access reads a Text column without Unicode
    compression as UTF-16LE when it reads the table directly, and some of its other ways of reading read the same
    bytes as compressed text (#129). This script reads the Text column Name of the table Filled in
    engine-8e41563-filled-table.mdb (tools/earlier-created-tables.ps1), which DAO made without Unicode compression and
    8e41563 filled, in each of these ways, and writes for each row the code units each read, or why it could not:

      - DAO (DAO.DBEngine.120): a table-type recordset, a dynaset of the table, a snapshot of the table, and a
        snapshot of a query of it, unsorted and sorted;
      - ACE through OLE DB (Microsoft.ACE.OLEDB.16.0): a query of the table, unsorted and sorted.

    It reads only; the file is opened read-only.

    Run it in PowerShell 7 (pwsh), with Access in 64 bits, from the repository's root:
      pwsh -Command "& tools\earlier-filled-text-readers.ps1 -File '<full path>'"
#>
param(
    [Parameter(Mandatory = $true)] [string] $File
)
$ErrorActionPreference = 'Stop'

function Format-Units([object] $value) {
    if ($value -is [DBNull] -or $null -eq $value) { return 'null' }
    return ($value.ToString().ToCharArray() | ForEach-Object { '{0:X4}' -f [int]$_ }) -join ' '
}

"# tools/earlier-filled-text-readers.ps1: Filled.Name as each of Access's readers reads it"
"# reader`tId`tName (code units in hex, or the error)"
$dbe = New-Object -ComObject DAO.DBEngine.120
try {
    "# DAO $($dbe.Version)"
    $db = $dbe.OpenDatabase($File, $false, $true)
    try {
        foreach ($read in @(@('DAO table-type', 'Filled', 1), @('DAO dynaset', 'Filled', 2), @('DAO snapshot of the table', 'Filled', 4),
                            @('DAO snapshot of a query', 'SELECT Id, Name FROM Filled', 4),
                            @('DAO snapshot of a query by Id', 'SELECT Id, Name FROM Filled ORDER BY Id', 4))) {
            try {
                $rs = $db.OpenRecordset($read[1], $read[2])
                while (-not $rs.EOF) {
                    $id = $rs.Fields.Item('Id').Value
                    try { $units = Format-Units $rs.Fields.Item('Name').Value } catch { $units = "cannot read: $($_.Exception.Message)" }
                    "$($read[0])`t$id`t$units"
                    $rs.MoveNext()
                }
                $rs.Close()
            } catch { "$($read[0])`t`tcannot read: $($_.Exception.Message)" }
        }
        # A snapshot that meets a value it cannot read fails as it opens, so each row is read by a snapshot of its own.
        foreach ($id in 1..8) {
            try {
                $rs = $db.OpenRecordset("SELECT Id, Name FROM Filled WHERE Id = $id", 4)
                if ($rs.EOF) { "DAO snapshot of a query of one row`t$id`tno such row" }
                else { "DAO snapshot of a query of one row`t$id`t$(Format-Units $rs.Fields.Item('Name').Value)" }
                $rs.Close()
            } catch { "DAO snapshot of a query of one row`t$id`tcannot read: $($_.Exception.Message)" }
        }
    } finally { $db.Close() }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
}

$connection = New-Object System.Data.OleDb.OleDbConnection("Provider=Microsoft.ACE.OLEDB.16.0;Data Source=$File;Mode=Read")
try {
    $connection.Open()
    foreach ($read in @(@('ACE OLE DB query', 'SELECT Id, Name FROM Filled'), @('ACE OLE DB query by Id', 'SELECT Id, Name FROM Filled ORDER BY Id'))) {
        $command = $connection.CreateCommand()
        $command.CommandText = $read[1]
        try {
            $reader = $command.ExecuteReader()
            while ($reader.Read()) {
                try { $units = Format-Units $reader.GetValue(1) } catch { $units = "cannot read: $($_.Exception.Message)" }
                "$($read[0])`t$($reader.GetValue(0))`t$units"
            }
            $reader.Close()
        } catch { "$($read[0])`t`tcannot read: $($_.Exception.Message)" }
    }
    foreach ($id in 1..8) {
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT Id, Name FROM Filled WHERE Id = $id ORDER BY Name"
        try {
            $reader = $command.ExecuteReader()
            if ($reader.Read()) { "ACE OLE DB query of one row by Name`t$id`t$(Format-Units $reader.GetValue(1))" }
            else { "ACE OLE DB query of one row by Name`t$id`tno such row" }
            $reader.Close()
        } catch { "ACE OLE DB query of one row by Name`t$id`tcannot read: $($_.Exception.InnerException.Message)" }
    }
} finally { $connection.Dispose() }
