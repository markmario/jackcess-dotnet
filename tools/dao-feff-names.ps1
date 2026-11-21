<#
.SYNOPSIS
    Records whether Access takes, and how it keeps, the name of a table, a field or an index that begins with U+FEFF.

.DESCRIPTION
    The UTF-16LE of U+FEFF, the byte order mark, is 0xFF 0xFE, the mark of compressed text (#93). The catalog's Name
    column, and the names in a table's definition, have no Unicode compression. This script makes an Access 2000 .mdb
    with DAO (DAO.DBEngine.120) and asks Access for names that begin with U+FEFF:

      - a table created with that name (TableDefs.Append), and one created by DDL (CREATE TABLE);
      - a table renamed to such a name (TableDef.Name);
      - a field and an index created with such names, and a field and an index renamed to them.

    For each it writes what DAO did - took it or refused it, with the message - and the code units of the name DAO
    then lists, and what an ACE query of a table named so reads. It leaves the file at -OutFile, for this library's
    tests to read the names Access stored.

    Run it in PowerShell 7 (pwsh), with Access in 64 bits, from the repository's root:
      pwsh -Command "& tools\dao-feff-names.ps1 -OutFile '<full path>.mdb'"
#>
param(
    [Parameter(Mandatory = $true)] [string] $OutFile
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($OutFile)) { throw "OutFile must be a full path." }
if (Test-Path $OutFile) { Remove-Item $OutFile }

function Format-Units([string] $value) { ($value.ToCharArray() | ForEach-Object { '{0:X4}' -f [int]$_ }) -join ' ' }
$bom = [string][char]0xFEFF

"# tools/dao-feff-names.ps1: names that begin with U+FEFF, as DAO takes and lists them"
$dbe = New-Object -ComObject DAO.DBEngine.120
try {
    "# DAO $($dbe.Version)"
    $db = $dbe.CreateDatabase($OutFile, ';LANGID=0x0409;CP=1252;COUNTRY=0', 64)   # dbVersion40
    try {
        function Step([string] $what, [scriptblock] $do) {
            try { & $do; "$what`ttaken" } catch { "$what`trefused: $($_.Exception.InnerException.Message ?? $_.Exception.Message)" }
        }

        Step 'create table U+FEFF abc by TableDefs.Append' {
            $t = $db.CreateTableDef($bom + 'abc')
            $t.Fields.Append($t.CreateField('Id', 4))
            $db.TableDefs.Append($t)
        }
        Step 'create table U+FEFF ddl by CREATE TABLE' { $db.Execute("CREATE TABLE [$($bom)ddl] (Id LONG)") }
        Step 'create table Plain' {
            $t = $db.CreateTableDef('Plain')
            $t.Fields.Append($t.CreateField('Id', 4))
            $t.Fields.Append($t.CreateField('Other', 10, 50))
            $t.Fields.Append($t.CreateField($bom + 'f', 10, 50))
            $ix = $t.CreateIndex($bom + 'i')
            $ix.Fields.Append($ix.CreateField('Id'))
            $t.Indexes.Append($ix)
            $ix = $t.CreateIndex('ByOther')
            $ix.Fields.Append($ix.CreateField('Other'))
            $t.Indexes.Append($ix)
            $db.TableDefs.Append($t)
        }
        Step 'create table ToRename' {
            $t = $db.CreateTableDef('ToRename')
            $t.Fields.Append($t.CreateField('Id', 4))
            $db.TableDefs.Append($t)
        }
        Step 'rename table ToRename to U+FEFF xyz' { $db.TableDefs.Item('ToRename').Name = $bom + 'xyz' }
        Step 'rename field Plain.Other to U+FEFF g' { $db.TableDefs.Item('Plain').Fields.Item('Other').Name = $bom + 'g' }
        Step 'rename index Plain.ByOther to U+FEFF j' { $db.TableDefs.Item('Plain').Indexes.Item('ByOther').Name = $bom + 'j' }

        $db.TableDefs.Refresh()
        foreach ($t in $db.TableDefs) {
            if ($t.Name.StartsWith('MSys')) { continue }
            "table`t$(Format-Units $t.Name)"
            foreach ($f in $t.Fields) { "  field`t$(Format-Units $f.Name)" }
            foreach ($ix in $t.Indexes) { "  index`t$(Format-Units $ix.Name)" }
        }
    } finally { $db.Close() }

    $db = $dbe.OpenDatabase($OutFile)
    try {
        foreach ($name in @(($bom + 'abc'), ($bom + 'xyz'))) {
            try {
                $rs = $db.OpenRecordset($name, 1)
                "DAO table-type recordset of $(Format-Units $name)`topened"
                $rs.Close()
            } catch { "DAO table-type recordset of $(Format-Units $name)`tcannot: $($_.Exception.InnerException.Message ?? $_.Exception.Message)" }
        }
    } finally { $db.Close() }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
}

$connection = New-Object System.Data.OleDb.OleDbConnection("Provider=Microsoft.ACE.OLEDB.16.0;Data Source=$OutFile")
try {
    $connection.Open()
    foreach ($name in @(($bom + 'abc'), ($bom + 'xyz'))) {
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT COUNT(*) FROM [$name]"
        try { "ACE query of $(Format-Units $name)`t$($command.ExecuteScalar()) rows" }
        catch { "ACE query of $(Format-Units $name)`tcannot: $($_.Exception.InnerException.Message ?? $_.Exception.Message)" }
    }
    $tables = $connection.GetSchema('Tables')
    foreach ($row in $tables.Rows) { if ($row['TABLE_TYPE'] -eq 'TABLE') { "ACE schema lists`t$(Format-Units $row['TABLE_NAME'])" } }
} finally { $connection.Dispose() }
