<#
.SYNOPSIS
    Makes Access 97 files whose database passwords have characters code page 1252 lacks, in
    tests/JackcessDotNet.Tests/Assets, and asks Jet which passwords open them.

.DESCRIPTION
    Jet 3 keeps a database password in the database's code page, 1252 here, converted as Windows converts text to
    it: a character the code page lacks is the character Windows' best fit gives for it (L for an L with a stroke, z
    for a z with an acute accent) or a question mark (Cyrillic, Chinese). A password given to open the file is
    converted the same way, and checked case and all. So passwords that convert alike open the file alike.

    dao-jet3-password-outside-1252.mdb: the password is Пароль密码Łódź.

    dao-jet3-password-best-fit.mdb: the password is a, then eleven characters for which .NET's best fit for code page
    1252 is a question mark and Windows' is not - U+2007 and U+202F (a no-break space, A0), U+2008, U+2009 and U+200A
    (a space), U+2012 and U+2015 (a hyphen-minus), U+201B (an apostrophe), U+201F, U+2033 and U+2036 (a quotation
    mark) - then z.

    Passwords are written below with code points, so that the script's own encoding does not matter. Each file has one
    table, T, of one row. The script prints each password tried and whether DAO opened the file with it;
    ProtectedFileTests asks the same of the engine.

    DAO 3.6, part of 32-bit Windows, makes the files: run in a 64-bit PowerShell, the script runs itself again in the
    32-bit Windows PowerShell. A file holds the time it was made, so it is not the same bytes on every run: -Make makes
    one of them alone.

.EXAMPLE
    pwsh tools/jet3-password-fixture.ps1 -Make BestFit
#>
[CmdletBinding()]
param(
    # Where to write the files (tests/JackcessDotNet.Tests/Assets when not given).
    [string]$Out,
    # Which file to make: both when not given.
    [ValidateSet('All', 'Outside1252', 'BestFit')]
    [string]$Make = 'All'
)

$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\tests\JackcessDotNet.Tests\Assets' }
$outDir = [IO.Path]::GetFullPath($Out)

if ([Environment]::Is64BitProcess) {
    & "$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -Out $outDir -Make $Make
    if ($LASTEXITCODE -ne 0) { throw "The Access 97 files were not made (exit code $LASTEXITCODE)." }
    return
}

function Text([int[]] $codes) { -join ($codes | ForEach-Object { [char]$_ }) }
function Codes([string] $text) { ($text.ToCharArray() | ForEach-Object { 'U+{0:X4}' -f [int]$_ }) -join ' ' }

$engine = New-Object -ComObject DAO.DBEngine.36
New-Item -ItemType Directory -Force $outDir | Out-Null
$dbVersion30 = 32

# Makes the file, with the password, and prints which of the tries open it.
function New-Jet3File([string] $name, [string] $password, [string[]] $tries) {
    $path = Join-Path $outDir $name
    if (Test-Path $path) { Remove-Item $path }
    $db = $engine.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0;pwd=' + $password, $dbVersion30)
    $db.Execute('CREATE TABLE T (A LONG)')
    $db.Execute('INSERT INTO T VALUES (1)')
    $db.Close()
    foreach ($try in $tries) {
        try { $opened = $engine.OpenDatabase($path, $false, $true, ';pwd=' + $try); $opened.Close(); $result = 'opens' }
        catch { $result = 'refused' }
        '{0,-8} [{1}]' -f $result, (Codes $try)
    }
    "$([IO.Path]::GetFileName($path)): DAO $($engine.Version), $((Get-Item $path).Length) bytes"
}

if ($Make -in 'All', 'Outside1252') {
    $cyrillic = Text 0x41F, 0x430, 0x440, 0x43E, 0x43B, 0x44C          # Пароль
    $chinese  = Text 0x5BC6, 0x7801                                    # 密码
    $password = $cyrillic + $chinese + (Text 0x141, 0xF3, 0x64, 0x17A) # Łódź
    New-Jet3File 'dao-jet3-password-outside-1252.mdb' $password @(
        $password,
        ((Text 0x41F, 0x410, 0x420, 0x41E, 0x41B, 0x42C) + $chinese + (Text 0x141, 0xF3, 0x64, 0x17A)),  # ПАРОЛЬ密码Łódź
        ('????????' + (Text 0x4C, 0xF3, 0x64, 0x7A)),                                                    # ????????Lódz
        ($cyrillic + $chinese + (Text 0x4C, 0xF3, 0x64, 0x7A)),                                          # Пароль密码Lódz
        ($cyrillic + $chinese + 'Lodz'),                                                                 # an o without the acute
        ($cyrillic + $chinese + (Text 0x142, 0xF3, 0x64, 0x17A)),                                        # a lower-case ł
        '????????',
        ''
    )
}

if ($Make -in 'All', 'BestFit') {
    $eleven = Text 0x2007, 0x2008, 0x2009, 0x200A, 0x2012, 0x2015, 0x201B, 0x201F, 0x202F, 0x2033, 0x2036
    $windows = Text 0xA0, 0x20, 0x20, 0x20, 0x2D, 0x2D, 0x27, 0x22, 0xA0, 0x22, 0x22   # Windows' best fit for each
    New-Jet3File 'dao-jet3-password-best-fit.mdb' ('a' + $eleven + 'z') @(
        ('a' + $eleven + 'z'),
        ('a' + $windows + 'z'),                       # as the file holds it
        ('a' + ('?' * 11) + 'z'),                     # .NET's best fit for each
        ('a' + $windows.Replace([string][char]0xA0, ' ') + 'z'),   # a space for each no-break space
        ('a' + $eleven + 'Z')
    )
}
