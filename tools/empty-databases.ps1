<#
.SYNOPSIS
    Makes src/JackcessDotNet/Resources/empty2007.accdb and empty2010.accdb: the empty .accdb databases
    Database.Create starts a new file from.

.DESCRIPTION
    A new database is a copy of an empty one Access's own engine made, as in Jackcess, which ships one for each
    format it creates. These two are made here by DAO's CreateDatabase, not copied from Jackcess:

      empty2007.accdb  dbVersion120 (128): the Access 2007 format. Version byte 0x02; its catalog, and so the
                       tables made in it, sort in General - Legacy.
      empty2010.accdb  dbVersion140 (256): the Access 2010 format, which Access 2013 and later also write.
                       Version bytes 0x03 0x01; sorts in General.

    They hold the system tables the engine itself makes and nothing of Access the application (no navigation
    pane, no themes: Access adds those the first time it opens a file), so they are half the size of Jackcess's.

    No DAO constant makes the Access 2016 or Access 2019 format. Access makes one out of the 2010 format by
    setting the header's version to 0x05 0x00 when a table first gets a Large Number column, and to 0x06 0x00
    when one first gets a Date/Time Extended column; nothing else in the header changes. Database.Create does
    the same to a copy of empty2010.accdb (qcatco/exportmdb.interop#39), so there is no third or fourth file.

    The files are not the same bytes on every run (they hold the time they were made), so they are committed,
    and this script is the record of how. It needs the Access Database Engine (for DAO): these were made with
    Microsoft 365's, DAO 16.0. It changes nothing but the two files.

.EXAMPLE
    pwsh tools/empty-databases.ps1
#>
[CmdletBinding()]
param(
    # Where to write the databases.
    [string]$Out = (Join-Path $PSScriptRoot '..\src\JackcessDotNet\Resources')
)

$ErrorActionPreference = 'Stop'
$outDir = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $outDir | Out-Null

$dbLangGeneral = ';LANGID=0x0409;CP=1252;COUNTRY=0'
$formats = @(
    @{ File = 'empty2007.accdb'; Version = 128; Header = @(0x02, 0x00) },
    @{ File = 'empty2010.accdb'; Version = 256; Header = @(0x03, 0x01) }
)

$engine = New-Object -ComObject DAO.DBEngine.120
try {
    Write-Host "DAO $($engine.Version)"
    foreach ($format in $formats) {
        $path = Join-Path $outDir $format.File
        if (Test-Path $path) { Remove-Item $path }
        $db = $engine.CreateDatabase($path, $dbLangGeneral, $format.Version)
        $db.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
    }
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

foreach ($format in $formats) {
    $path  = Join-Path $outDir $format.File
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes[0x14] -ne $format.Header[0] -or $bytes[0x15] -ne $format.Header[1]) {
        throw ('{0} has the version bytes {1:X2} {2:X2}, not {3:X2} {4:X2}: this DAO does not make the format.' -f `
            $format.File, $bytes[0x14], $bytes[0x15], $format.Header[0], $format.Header[1])
    }
    Write-Host ('{0}: {1} bytes, version {2:X2} {3:X2}' -f $format.File, $bytes.Length, $bytes[0x14], $bytes[0x15])
}
