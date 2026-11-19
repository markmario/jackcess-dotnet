<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/dao-odd-names.accdb: a file Access made with names a comparison of text
    may stumble on, or take as one where Access does not.

.DESCRIPTION
    DAO 16 makes an Access 2007 .accdb with these tables, written below with code points so that the script's own
    encoding does not matter:

      F_FDD0   Id LONG, and a field named x followed by U+FDD0, a noncharacter
      F_FFFE   Id LONG, and a field named x followed by U+FFFE, a noncharacter
      F_FFFF   Id LONG, and a field named x followed by U+FFFF, a noncharacter
      F_D800   Id LONG, and a field named x followed by U+D800, a high surrogate with no low one after it
      F_DC00   Id LONG, and a field named x, U+DC00 - a low surrogate with no high one before it - and y
      xU+FDD0  a table so named, Id LONG
      xU+DC00y a table so named, Id LONG (DAO refuses a table the names x and U+FFFE, x and U+FFFF, x and U+D800)
      xU+00B5  Id LONG: x and the micro sign
      xU+03BC  Id LONG: x and the Greek small mu. Access keeps the name apart from the one before (DAO takes both);
               .NET's comparison without case, which this library looks names up with, takes the two as one, and
               .NET Framework's does not
      xU+F900  Id LONG: x and a CJK compatibility ideograph
      xU+8C48  Id LONG: x and the ideograph U+F900 decomposes to, a name Access keeps apart from the one before

    and two relationships, not enforced: MicroFFFE from xU+00B5's Id to F_FFFE's, and MuFDD0 from xU+03BC's Id to
    F_FDD0's, each naming its table as the catalog does.

    ObjectNameTests makes a table, adds a column and imports in it; SchemaRenameTests renames its tables. The file
    holds the time it was made, so it is not the same bytes on every run.

.EXAMPLE
    pwsh tools/odd-names-fixture.ps1
#>
[CmdletBinding()]
param(
    # Where to write the file (tests/JackcessDotNet.Tests/Assets when not given).
    [string]$Out
)

$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\tests\JackcessDotNet.Tests\Assets' }
$outDir = [IO.Path]::GetFullPath($Out)
if (-not [Environment]::Is64BitProcess) { throw 'Run this in a 64-bit PowerShell: it makes the file with DAO 16.' }

function Text([int[]] $codes) { -join ($codes | ForEach-Object { [char]$_ }) }
function Codes([string] $text) { ($text.ToCharArray() | ForEach-Object { 'U+{0:X4}' -f [int]$_ }) -join ' ' }

$engine = New-Object -ComObject DAO.DBEngine.120
New-Item -ItemType Directory -Force $outDir | Out-Null
$path = Join-Path $outDir 'dao-odd-names.accdb'
if (Test-Path $path) { Remove-Item $path }
$dbVersion120 = 128; $dbLong = 4; $dbText = 10; $dbRelationDontEnforce = 2
$micro = 'x' + (Text 0xB5); $mu = 'x' + (Text 0x3BC)
$db = $engine.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', $dbVersion120)
try {
    foreach ($table in @(@('F_FDD0', ('x' + (Text 0xFDD0))), @('F_FFFE', ('x' + (Text 0xFFFE))), @('F_FFFF', ('x' + (Text 0xFFFF))),
                         @('F_D800', ('x' + (Text 0xD800))), @('F_DC00', ('x' + (Text 0xDC00) + 'y')))) {
        $definition = $db.CreateTableDef($table[0])
        $definition.Fields.Append($definition.CreateField('Id', $dbLong))
        $definition.Fields.Append($definition.CreateField($table[1], $dbText, 20))
        $db.TableDefs.Append($definition)
        "table $($table[0]), field [$(Codes $table[1])]"
    }
    foreach ($name in @(('x' + (Text 0xFDD0)), ('x' + (Text 0xDC00) + 'y'), $micro, $mu, ('x' + (Text 0xF900)), ('x' + (Text 0x8C48)))) {
        $definition = $db.CreateTableDef($name)
        $definition.Fields.Append($definition.CreateField('Id', $dbLong))
        $db.TableDefs.Append($definition)
        "table [$(Codes $name)]"
    }
    foreach ($relation in @(@('MicroFFFE', $micro, 'F_FFFE'), @('MuFDD0', $mu, 'F_FDD0'))) {
        $made = $db.CreateRelation($relation[0], $relation[1], $relation[2], $dbRelationDontEnforce)
        $field = $made.CreateField('Id')
        $field.ForeignName = 'Id'
        $made.Fields.Append($field)
        $db.Relations.Append($made)
        "relationship $($relation[0]): [$(Codes $relation[1])].Id to $($relation[2]).Id"
    }
}
finally { $db.Close() }
"$([IO.Path]::GetFileName($path)): DAO $($engine.Version), $((Get-Item $path).Length) bytes"
