<#
.SYNOPSIS
    Makes a table with an attachment and a multi-value column in Access's own engine, adds rows to it, and tries to
    drop the index Access gives a complex column: what a row of such a table holds for the column, and whether a
    complex column can be without an index (#152).

.DESCRIPTION
    The database is an Access 2007 one (dbVersion120), with one table, T:

      Id     Long, AutoNumber
      Name   Text(20)
      Files  Attachment (dbAttachment, 101)
      Tags   multi-value Text(20) (dbComplexText, 109)

    Four rows are added: 'a' and 'b' by a DAO recordset and 'c' by INSERT, none with an attachment or a tag; and
    'd' by a recordset, with one tag. Then, given its name, the index Access made on Files is dropped by DROP INDEX.

    Measured on 2026-11-22 with Microsoft 365's Access, 64-bit (DAO 16.0, build 16.0.20625), and read with this
    library's row decoder:

      - Each row holds a number in Files and in Tags - 1, 2, 3 and 4, the same in both columns of a row - whether
        or not it has an attachment or a tag; neither column is null in any row. The table's counter of those
        numbers, the four bytes at offset 28 of its definition (Jackcess's OFFSET_NEXT_COMPLEX_AUTO_NUMBER), is 4.
      - Access gives Files and Tags each an index of its own, named for the column and a GUID, which DAO's
        TableDef.Indexes does not list. DROP INDEX is refused: "The unique index on the 'Files' column cannot be
        deleted." So every complex column in a table Access made has an index.

    What it decided: a complex column reads as null (its values are not read, #24), and no row of a table with
    one is inserted, updated or deleted, since this library hands out none of those numbers and writes nothing in
    the column's own table.

    The file is not kept: the corpus's complexDataV2007.accdb and complexDataV2010.accdb hold the same, and the
    tests read those. It needs DAO (Microsoft Access, or the Access Database Engine redistributable) of the
    bitness of the PowerShell that runs it.

.PARAMETER Path
    The file to make. An existing file is replaced.

.EXAMPLE
    pwsh tools/dao-complex-rows.ps1 -Path $env:TEMP\complex.accdb
    pwsh tools/dao-complex-rows.ps1 -Path $env:TEMP\complex.accdb -IndexName Files_85C84616F5644534B707475BACF385E5
#>
param(
    [Parameter(Mandatory)] [string]$Path,
    # The name of the index on Files in the file at Path, made by an earlier run, as this library's Table.Indexes
    # gives it: it is the column's name and a GUID Access chooses, and DAO does not list it. Given it, the script
    # makes nothing and only tries to drop that index.
    [string]$IndexName
)

$ErrorActionPreference = 'Stop'
$Path = [IO.Path]::GetFullPath($Path)

$dbLong = 4; $dbText = 10; $dbAttachment = 101; $dbComplexText = 109
$dbAutoIncrField = 16; $dbVersion120 = 128; $dbOpenDynaset = 2
$general = ';LANGID=0x0409;CP=1252;COUNTRY=0'

$engine = New-Object -ComObject DAO.DBEngine.120
if ($IndexName) {
    try {
        $db = $engine.OpenDatabase($Path)
        try { $db.Execute("DROP INDEX [$IndexName] ON T"); "dropped $IndexName" }
        catch { "DROP INDEX refused: $($_.Exception.InnerException.Message ?? $_.Exception.Message)" }
        $db.Close()
    }
    finally {
        $db = $null
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
    return
}

if (Test-Path $Path) { Remove-Item $Path }
try {
    $db = $engine.CreateDatabase($Path, $general, $dbVersion120)
    $table = $db.CreateTableDef('T')
    $field = $table.CreateField('Id', $dbLong); $field.Attributes = $dbAutoIncrField; $table.Fields.Append($field)
    $table.Fields.Append($table.CreateField('Name', $dbText, 20))
    $table.Fields.Append($table.CreateField('Files', $dbAttachment))
    $table.Fields.Append($table.CreateField('Tags', $dbComplexText, 20))
    $db.TableDefs.Append($table)

    $rs = $db.OpenRecordset('T', $dbOpenDynaset)
    $rs.AddNew(); $rs.Fields.Item('Name').Value = 'a'; $rs.Update()
    $rs.AddNew(); $rs.Fields.Item('Name').Value = 'b'; $rs.Update()
    $rs.Close()
    $db.Execute("INSERT INTO T (Name) VALUES ('c')")

    $rs = $db.OpenRecordset('T', $dbOpenDynaset)
    $rs.AddNew(); $rs.Fields.Item('Name').Value = 'd'
    $tags = $rs.Fields.Item('Tags').Value
    $tags.AddNew(); $tags.Fields.Item(0).Value = 'x'; $tags.Update(); $tags.Close()
    $rs.Update()
    $rs.Close()

    "DAO lists $($db.TableDefs.Item('T').Indexes.Count) indexes on T"
    $db.Close()
    "made $Path"
}
finally {
    $tags = $null; $rs = $null; $field = $null; $table = $null; $db = $null
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}
