<#
.SYNOPSIS
    Records what Access reads of the catalog row of a table an earlier version of this library created.

.DESCRIPTION
    A table the JackcessDotNet packages to 1.2.2 or a build of this repository before 442bbf8 created has a catalog
    row (MSysObjects) with ParentId 0 and no Owner, and its name stored compressed - 0xFF 0xFE and a byte each - in
    the catalog's Name column, which has no Unicode compression (#93, #121). This script reads MSysObjects of the
    files tools/earlier-created-tables.ps1 made, with DAO (DAO.DBEngine.120: a table-type recordset, a dynaset and a
    snapshot of a query) and with ACE through OLE DB (Microsoft.ACE.OLEDB.16.0, a query), and writes, for each row of
    a table whose ParentId is 0, the code units of the Name each read, or why it could not. It reads only; the files
    are opened read-only.

    Without a workgroup file neither DAO nor ACE may read MSysObjects ("no read permission"), so both are given the
    one Access keeps for the user, System.mdw, as Access itself is.

    Run it in PowerShell 7 (pwsh), with Access in 64 bits, from the repository's root:
      pwsh -Command "& tools\earlier-created-catalog-names.ps1 -Files '<full path>', '<full path>'"
#>
param(
    [Parameter(Mandatory = $true)] [string[]] $Files,
    [string] $SystemDb = (Join-Path $env:APPDATA 'Microsoft\Access\System.mdw')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $SystemDb)) { throw "No workgroup file at ${SystemDb}: start Access once, or give -SystemDb." }

function Format-Units([object] $value) {
    if ($value -is [DBNull] -or $null -eq $value) { return 'null' }
    return ($value.ToString().ToCharArray() | ForEach-Object { '{0:X4}' -f [int]$_ }) -join ' '
}

"# tools/earlier-created-catalog-names.ps1: the Name of each table's catalog row with ParentId 0, as Access reads it"
"# file`treader`tId`tName (code units in hex, or the error)"
$dbe = New-Object -ComObject DAO.DBEngine.120
try {
    $dbe.SystemDB = $SystemDb
    foreach ($file in $Files) {
        $name = Split-Path $file -Leaf
        $db = $dbe.OpenDatabase($file, $false, $true)
        try {
            foreach ($read in @(@('DAO table-type', 'MSysObjects', 1), @('DAO dynaset', 'MSysObjects', 2),
                                @('DAO snapshot of a query', 'SELECT Id, Name, ParentId, Type FROM MSysObjects', 4))) {
                try {
                    $rs = $db.OpenRecordset($read[1], $read[2])
                    $found = 0
                    while (-not $rs.EOF) {
                        if ($rs.Fields.Item('ParentId').Value -eq 0 -and $rs.Fields.Item('Type').Value -eq 1) {
                            "$name`t$($read[0])`t$($rs.Fields.Item('Id').Value)`t$(Format-Units $rs.Fields.Item('Name').Value)"
                            $found++
                        }
                        $rs.MoveNext()
                    }
                    $rs.Close()
                    if ($found -eq 0) { "$name`t$($read[0])`t`tno such row" }
                } catch { "$name`t$($read[0])`t`tcannot read: $($_.Exception.Message)" }
            }
        } finally { $db.Close() }

        $connection = New-Object System.Data.OleDb.OleDbConnection(
            "Provider=Microsoft.ACE.OLEDB.16.0;Data Source=$file;Mode=Read;Jet OLEDB:System database=$SystemDb")
        try {
            $connection.Open()
            $command = $connection.CreateCommand()
            # Every row, filtered here: a query that selects by ParentId, Id or Name goes through an index that does
            # not hold the row.
            $command.CommandText = 'SELECT Id, Name, ParentId, Type FROM MSysObjects'
            $reader = $command.ExecuteReader()
            $found = 0
            while ($reader.Read()) {
                if ($reader.GetValue(2) -eq 0 -and $reader.GetValue(3) -eq 1) {
                    "$name`tACE OLE DB query`t$($reader.GetValue(0))`t$(Format-Units $reader.GetValue(1))"
                    $found++
                }
            }
            $reader.Close()
            if ($found -eq 0) { "$name`tACE OLE DB query`t`tno such row" }
            $command.CommandText = 'SELECT Id, Name FROM MSysObjects WHERE ParentId = 0 AND Type = 1'
            $reader = $command.ExecuteReader()
            $found = 0
            while ($reader.Read()) { "$name`tACE OLE DB query by ParentId`t$($reader.GetValue(0))`t$(Format-Units $reader.GetValue(1))"; $found++ }
            $reader.Close()
            if ($found -eq 0) { "$name`tACE OLE DB query by ParentId`t`tno such row" }
        } catch { "$name`tACE OLE DB query`t`tcannot read: $($_.Exception.Message)" }
        finally { $connection.Dispose() }
    }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
}
