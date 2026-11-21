<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/dao-boundary-definitions.mdb and .accdb: tables Access made whose
    definitions end just before, at and just after the end of a definition page.

.DESCRIPTION
    A table's definition goes on to another page when it no longer fits the pages it has - and Access counts it
    as not fitting from eight bytes before the end of its last page: a definition of 4,089 to 4,096 bytes fills
    no more than its first page, and Access still gives it a second, empty one (#37). These files hold what
    Access wrote on either side of that, for the tests to read, and to change and hand back to Access
    (MultiPageDefinitionTests; SchemaChangesInAccessTests).

    Each file has five tables at the end of the first, the second and the third page of a definition - 4,096,
    8,184 and 12,272 bytes; a page after the first holds 4,088: the last length that fits (eight bytes short of
    the end), the first that does not (seven short), one four short, one that ends with the page, and one a byte
    longer. A table is named for its length: L4089 is the table whose definition is 4,089 bytes long. (Run with
    every length from twelve before each end to twelve after, Access put all 75 definitions, in an .mdb and an
    .accdb, on the fewest pages that hold the length and eight bytes more, with the last page's free space what
    those pages hold less the length and eight.) A table has only Long
    columns and no index, so its length is 65 bytes and, for each column, 27 and twice the characters of its
    name; the names are chosen to make the length. Each table has one row, with 1 in its first column.

    The script checks, from the bytes Access wrote, that each definition has the length its table is named for,
    and prints the pages each is on. It needs the Access Database Engine (for DAO): these were made with
    Microsoft 365's, DAO 16.0. The files are not the same bytes on every run, so they are committed, and this
    script is the record of how. It changes nothing but the two files.

.EXAMPLE
    pwsh tools/boundary-definitions.ps1
#>
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets'))

$ErrorActionPreference = 'Stop'
$pageSize = 4096

# The names of Long columns that make a definition of $length bytes: as many of 64 characters as leave a
# remainder one, two or three shorter names make exactly.
function Get-ColumnNames([int]$length) {
    for ($full = [Math]::Floor(($length - 65) / 155); $full -ge 0; $full--) {
        $rest = $length - 65 - $full * 155
        foreach ($more in 0..3) {
            $characters = ($rest - 27 * $more) / 2
            if ($rest -eq 0 -and $more -eq 0) { $characters = 0 }
            if (($rest - 27 * $more) % 2 -ne 0 -or $characters -lt 4 * $more -or $characters -gt 64 * $more) { continue }
            if ($more -eq 0 -and $rest -ne 0) { continue }
            $lengths = @(64) * $full
            $left = [int]$characters
            for ($i = $more; $i -ge 1; $i--) {
                $take = [Math]::Min(64, $left - 4 * ($i - 1))
                $lengths += $take
                $left -= $take
            }
            if ($lengths.Count -gt 255) { continue }
            $n = 0
            return $lengths | ForEach-Object { $n++; ('c{0:D3}' -f $n) + ('x' * ($_ - 4)) }
        }
    }
    throw "No columns make a definition of $length bytes."
}

# The definition that starts on a page, as the file holds it: its length and the pages it is on.
function Read-Definition([byte[]]$bytes, [int]$page) {
    $pages = @($page)
    $free = @([BitConverter]::ToInt16($bytes, $page * $pageSize + 2))
    $next = [BitConverter]::ToInt32($bytes, $page * $pageSize + 4)
    while ($next -ne 0) {
        $pages += $next
        $free += [BitConverter]::ToInt16($bytes, $next * $pageSize + 2)
        $next = [BitConverter]::ToInt32($bytes, $next * $pageSize + 4)
    }
    [pscustomobject]@{ Length = [BitConverter]::ToInt32($bytes, $page * $pageSize + 8); Pages = $pages; Free = $free }
}

$lengths = foreach ($end in 4096, 8184, 12272) { $end - 8; $end - 7; $end - 4; $end; $end + 1 }
$dbe = New-Object -ComObject DAO.DBEngine.120
try {
    foreach ($kind in @{ Extension = 'mdb'; Version = 64 }, @{ Extension = 'accdb'; Version = 128 }) {
        $path = Join-Path $OutputDirectory "dao-boundary-definitions.$($kind.Extension)"
        Remove-Item $path -ErrorAction SilentlyContinue
        $db = $dbe.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', $kind.Version)
        try {
            foreach ($length in $lengths) {
                $names = @(Get-ColumnNames $length)
                $db.Execute("CREATE TABLE L$length ($(($names | ForEach-Object { "$_ LONG" }) -join ', '))", 128)
                $db.Execute("INSERT INTO L$length ($($names[0])) VALUES (1)", 128)
            }
        }
        finally { $db.Close() }

        # Each table's definition is found by its length, which no other definition of the file has: the first
        # pages of definitions are the pages of type 2 that no other names as its next.
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $count = $bytes.Length / $pageSize
        $later = @{}
        for ($p = 0; $p -lt $count; $p++) {
            if ($bytes[$p * $pageSize] -eq 2 -and [BitConverter]::ToInt32($bytes, $p * $pageSize + 4) -ne 0) { $later[[BitConverter]::ToInt32($bytes, $p * $pageSize + 4)] = $true }
        }
        $byLength = @{}
        for ($p = 0; $p -lt $count; $p++) {
            if ($bytes[$p * $pageSize] -eq 2 -and -not $later.ContainsKey($p)) { $byLength[[BitConverter]::ToInt32($bytes, $p * $pageSize + 8)] = $p }
        }
        foreach ($length in $lengths) {
            if (-not $byLength.ContainsKey($length)) { throw "No table of $path has a definition of $length bytes: L$length was not made as meant." }
            $definition = Read-Definition $bytes $byLength[$length]
            '{0,-6} L{1}: {2} page(s), free space {3}' -f $kind.Extension, $length, $definition.Pages.Count, ($definition.Free -join ', ')
        }
        Write-Host "Wrote $path ($((Get-Item $path).Length) bytes)"
    }
}
finally { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($dbe) }
