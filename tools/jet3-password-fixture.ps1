<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/dao-jet3-password-outside-1252.mdb, an Access 97 file whose database
    password has characters code page 1252 lacks, and asks Jet 4.0 which passwords open it.

.DESCRIPTION
    Jet 3 keeps a database password in the database's code page, 1252 here, converted as Windows converts text to
    it: a character the code page lacks is the character Windows' best fit gives for it (L for an L with a stroke, z
    for a z with an acute accent) or a question mark (Cyrillic, Chinese). A password given to open the file is
    converted the same way, and checked case and all. So passwords that convert alike open the file alike.

    The password is Пароль密码Łódź, written below with code points so that the script's own encoding does not matter.
    The file has one table, T, of one row. The script prints each password tried and whether DAO opened the file
    with it; ProtectedFileTests asks the same of the engine.

    DAO 3.6, part of 32-bit Windows, makes the file: run in a 64-bit PowerShell, the script runs itself again in the
    32-bit Windows PowerShell. The file holds the time it was made, so it is not the same bytes on every run.

.EXAMPLE
    pwsh tools/jet3-password-fixture.ps1
#>
[CmdletBinding()]
param(
    # Where to write the file (tests/JackcessDotNet.Tests/Assets when not given).
    [string]$Out
)

$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\tests\JackcessDotNet.Tests\Assets' }
$outDir = [IO.Path]::GetFullPath($Out)

if ([Environment]::Is64BitProcess) {
    & "$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -Out $outDir
    if ($LASTEXITCODE -ne 0) { throw "The Access 97 file was not made (exit code $LASTEXITCODE)." }
    return
}

function Text([int[]] $codes) { -join ($codes | ForEach-Object { [char]$_ }) }
function Codes([string] $text) { ($text.ToCharArray() | ForEach-Object { 'U+{0:X4}' -f [int]$_ }) -join ' ' }

$cyrillic = Text 0x41F, 0x430, 0x440, 0x43E, 0x43B, 0x44C          # Пароль
$chinese  = Text 0x5BC6, 0x7801                                    # 密码
$password = $cyrillic + $chinese + (Text 0x141, 0xF3, 0x64, 0x17A) # Łódź

$engine = New-Object -ComObject DAO.DBEngine.36
New-Item -ItemType Directory -Force $outDir | Out-Null
$path = Join-Path $outDir 'dao-jet3-password-outside-1252.mdb'
if (Test-Path $path) { Remove-Item $path }
$dbVersion30 = 32
$db = $engine.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0;pwd=' + $password, $dbVersion30)
$db.Execute('CREATE TABLE T (A LONG)')
$db.Execute('INSERT INTO T VALUES (1)')
$db.Close()

$tries = @(
    $password,
    ((Text 0x41F, 0x410, 0x420, 0x41E, 0x41B, 0x42C) + $chinese + (Text 0x141, 0xF3, 0x64, 0x17A)),  # ПАРОЛЬ密码Łódź
    ('????????' + (Text 0x4C, 0xF3, 0x64, 0x7A)),                                                    # ????????Lódz
    ($cyrillic + $chinese + (Text 0x4C, 0xF3, 0x64, 0x7A)),                                          # Пароль密码Lódz
    ($cyrillic + $chinese + 'Lodz'),                                                                 # an o without the acute
    ($cyrillic + $chinese + (Text 0x142, 0xF3, 0x64, 0x17A)),                                        # a lower-case ł
    '????????',
    ''
)
foreach ($try in $tries) {
    try { $opened = $engine.OpenDatabase($path, $false, $true, ';pwd=' + $try); $opened.Close(); $result = 'opens' }
    catch { $result = 'refused' }
    '{0,-8} [{1}]' -f $result, (Codes $try)
}
"$([IO.Path]::GetFileName($path)): DAO $($engine.Version), $((Get-Item $path).Length) bytes"
