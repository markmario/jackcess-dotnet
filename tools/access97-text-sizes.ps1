<#
.SYNOPSIS
    Lists the Size Jet 4.0's DAO reports for every Text column of Access 97 files.

.DESCRIPTION
    An Access 97 (Jet 3) Text column is as long as its bytes in the database's code page (#85). This script opens
    each file given, read-only and from a copy, with 32-bit DAO 3.6 (DAO.DBEngine.36), and writes one line for each
    Text column of each table that is not a system table: the file's name, the table, the column and its Size. The
    corpus's Access 97 files and tests/JackcessDotNet.Tests/Assets/access97-jet40.mdb give 175 lines, each the
    Column.MaxLength the library reads. Nothing of this library takes part.

    DAO 3.6 is 32-bit only, so run it in 32-bit Windows PowerShell:
      C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -File tools\access97-text-sizes.ps1 -Files (Get-ChildItem tests\corpus\V1997\*.mdb).FullName
#>
param(
    [Parameter(Mandatory = $true)] [string[]] $Files
)
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 4) { throw "DAO 3.6 is 32-bit: run this script in 32-bit Windows PowerShell." }

$dbe = New-Object -ComObject DAO.DBEngine.36
foreach ($file in $Files) {
    $copy = Join-Path ([IO.Path]::GetTempPath()) ('access97-' + [guid]::NewGuid().ToString('N') + '.mdb')
    Copy-Item $file $copy
    try {
        $db = $dbe.OpenDatabase($copy, $false, $true)
        try {
            foreach ($table in $db.TableDefs) {
                if ($table.Name -like 'MSys*') { continue }
                foreach ($field in $table.Fields) {
                    if ($field.Type -eq 10) { (Split-Path $file -Leaf) + "`t" + $table.Name + "`t" + $field.Name + "`t" + $field.Size }
                }
            }
        } finally { $db.Close() }
    } finally { Remove-Item $copy }
}
