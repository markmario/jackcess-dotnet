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

      - DAO (DAO.DBEngine.120): a table-type recordset; a dynaset and a snapshot of the table; a dynaset and a
        snapshot of a query of it, unsorted, sorted by Id and sorted by Name; and a dynaset and a snapshot of a query
        of each row alone;
      - ACE through OLE DB (Microsoft.ACE.OLEDB.16.0): a query of the table, unsorted, sorted by Id and sorted by
        Name; and a query of each row alone, unsorted and sorted by Name.

    A reading that meets a value it cannot read can fail as it opens, so each row is read alone as well. It reads
    only; the file is opened read-only.

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

function Get-Message($exception) { if ($exception.InnerException) { $exception.InnerException.Message } else { $exception.Message } }

"# tools/earlier-filled-text-readers.ps1: Filled.Name as each of Access's readers reads it"
"# reader`tId`tName (code units in hex, or the error)"
$dbe = New-Object -ComObject DAO.DBEngine.120
try {
    "# DAO $($dbe.Version)"
    $db = $dbe.OpenDatabase($File, $false, $true)
    try {
        $reads = @(@('DAO table-type', 'Filled', 1), @('DAO dynaset of the table', 'Filled', 2), @('DAO snapshot of the table', 'Filled', 4))
        foreach ($kind in @(@('dynaset', 2), @('snapshot', 4))) {
            $reads += , @("DAO $($kind[0]) of a query", 'SELECT Id, Name FROM Filled', $kind[1])
            $reads += , @("DAO $($kind[0]) of a query by Id", 'SELECT Id, Name FROM Filled ORDER BY Id', $kind[1])
            $reads += , @("DAO $($kind[0]) of a query by Name", 'SELECT Id, Name FROM Filled ORDER BY Name', $kind[1])
        }
        foreach ($read in $reads) {
            try {
                $rs = $db.OpenRecordset($read[1], $read[2])
                while (-not $rs.EOF) {
                    $id = $rs.Fields.Item('Id').Value
                    try { $units = Format-Units $rs.Fields.Item('Name').Value } catch { $units = "cannot read: $(Get-Message $_.Exception)" }
                    "$($read[0])`t$id`t$units"
                    $rs.MoveNext()
                }
                $rs.Close()
            } catch { "$($read[0])`t`tcannot read: $(Get-Message $_.Exception)" }
        }
        foreach ($kind in @(@('dynaset', 2), @('snapshot', 4))) {
            foreach ($id in 1..8) {
                $what = "DAO $($kind[0]) of a query of one row"
                try {
                    $rs = $db.OpenRecordset("SELECT Id, Name FROM Filled WHERE Id = $id", $kind[1])
                    if ($rs.EOF) { "$what`t$id`tno such row" }
                    else { "$what`t$id`t$(Format-Units $rs.Fields.Item('Name').Value)" }
                    $rs.Close()
                } catch { "$what`t$id`tcannot read: $(Get-Message $_.Exception)" }
            }
        }
    } finally { $db.Close() }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
}

$connection = New-Object System.Data.OleDb.OleDbConnection("Provider=Microsoft.ACE.OLEDB.16.0;Data Source=$File;Mode=Read")
try {
    $connection.Open()
    $queries = @(@('ACE OLE DB query', 'SELECT Id, Name FROM Filled'), @('ACE OLE DB query by Id', 'SELECT Id, Name FROM Filled ORDER BY Id'),
                 @('ACE OLE DB query by Name', 'SELECT Id, Name FROM Filled ORDER BY Name'))
    foreach ($read in $queries) {
        $command = $connection.CreateCommand()
        $command.CommandText = $read[1]
        try {
            $reader = $command.ExecuteReader()
            while ($reader.Read()) {
                try { $units = Format-Units $reader.GetValue(1) } catch { $units = "cannot read: $(Get-Message $_.Exception)" }
                "$($read[0])`t$($reader.GetValue(0))`t$units"
            }
            $reader.Close()
        } catch { "$($read[0])`t`tcannot read: $(Get-Message $_.Exception)" }
    }
    foreach ($order in @(@('', ''), @(' by Name', ' ORDER BY Name'))) {
        foreach ($id in 1..8) {
            $what = "ACE OLE DB query of one row$($order[0])"
            $command = $connection.CreateCommand()
            $command.CommandText = "SELECT Id, Name FROM Filled WHERE Id = $id$($order[1])"
            try {
                $reader = $command.ExecuteReader()
                if ($reader.Read()) { "$what`t$id`t$(Format-Units $reader.GetValue(1))" }
                else { "$what`t$id`tno such row" }
                $reader.Close()
            } catch { "$what`t$id`tcannot read: $(Get-Message $_.Exception)" }
        }
    }
} finally { $connection.Dispose() }
