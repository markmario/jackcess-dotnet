<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/ace-large-numbers-extended-dates.accdb: a database Access made, with
    Large Number and Date/Time Extended columns, their values at the ends of their ranges, and indexes on them.

.DESCRIPTION
    The corpus has one file with Date/Time Extended values (V2019/extDateV2019.accdb) and none with a Large
    Number. This one has both, with the values a reader or a key is likeliest to get wrong, so that what this
    library reads, and the index keys it makes, are held to what Access wrote (LargeNumberExtendedDateTests).

    DAO makes an Access 2010 database and two tables of the same twelve rows:

      BySql  CREATE TABLE ... (Id LONG primary key, N BIGINT, D DATETIME2). SQL's BIGINT is kept among a row's
             variable-length values.
      ByDao  the same columns, made through a TableDef (dbBigInt 16, dbDateTimeExtended 26): there a Large
             Number is kept among the fixed-length values. Filled from BySql.

    Each has five indexes: N ascending and descending, D ascending and descending, and N with D descending.
    The first Large Number column makes the file's version 0x05 and the first Date/Time Extended column 0x06.

    The file is not the same bytes on every run (it holds the time it was made), so the fixture is committed,
    and this script is the record of how. It needs the Access Database Engine, Microsoft 365's or Access 2019's
    (for DAO, and for the two column types); it changes nothing but the fixture.

.EXAMPLE
    pwsh tools/large-number-extended-date-fixture.ps1
#>
[CmdletBinding()]
param(
    # Where to write the database.
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\ace-large-numbers-extended-dates.accdb')
)

$ErrorActionPreference = 'Stop'
$outPath = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null
if (Test-Path $outPath) { Remove-Item $outPath }

$dbVersion140  = 256
$dbLangGeneral = ';LANGID=0x0409;CP=1252;COUNTRY=0'
$dbLong = 4; $dbBigInt = 16; $dbDateTimeExtended = 26
$dbFailOnError = 128

# The rows: LargeNumberExtendedDateTests has the same list. A date is written as text, which is how Access's SQL
# takes one with more than whole seconds.
$numbers = @('9223372036854775807', '-9223372036854775808', '-1', '0', '1', '4294967296', '-4294967296',
             '255', '256', '-256', '2147483648', 'NULL')
$dates   = @("'0001-01-01 00:00:00'", "'9999-12-31 23:59:59.9999999'", "'1899-12-29 23:59:59.9999999'",
             "'1899-12-30 00:00:00'", "'2020-01-02 03:04:05.1234567'", "'2020-01-02 03:04:05.1234568'",
             "'1066-10-14 09:00:00.0000001'", "'0100-06-14 12:45:00.1234567'", "'2024-02-29 23:59:59.9999999'",
             'NULL', "'1000-01-01 00:00:00'", "'1999-12-31 00:00:00'")

function Add-Indexes($db, [string]$table) {
    $db.Execute("CREATE INDEX NAsc ON $table (N)", $dbFailOnError)
    $db.Execute("CREATE INDEX NDesc ON $table (N DESC)", $dbFailOnError)
    $db.Execute("CREATE INDEX DAsc ON $table (D)", $dbFailOnError)
    $db.Execute("CREATE INDEX DDesc ON $table (D DESC)", $dbFailOnError)
    $db.Execute("CREATE INDEX NThenD ON $table (N, D DESC)", $dbFailOnError)
}

$engine = New-Object -ComObject DAO.DBEngine.120
try {
    Write-Host "DAO $($engine.Version)"
    $db = $engine.CreateDatabase($outPath, $dbLangGeneral, $dbVersion140)
    try {
        $db.Execute('CREATE TABLE BySql (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, N BIGINT, D DATETIME2)', $dbFailOnError)
        Add-Indexes $db 'BySql'
        for ($i = 0; $i -lt $numbers.Count; $i++) {
            $db.Execute("INSERT INTO BySql (Id, N, D) VALUES ($($i + 1), $($numbers[$i]), $($dates[$i]))", $dbFailOnError)
        }

        $definition = $db.CreateTableDef('ByDao')
        $definition.Fields.Append($definition.CreateField('Id', $dbLong))
        $definition.Fields.Append($definition.CreateField('N', $dbBigInt))
        $definition.Fields.Append($definition.CreateField('D', $dbDateTimeExtended))
        $db.TableDefs.Append($definition)
        $db.Execute('CREATE UNIQUE INDEX PrimaryKey ON ByDao (Id) WITH PRIMARY', $dbFailOnError)
        Add-Indexes $db 'ByDao'
        $db.Execute('INSERT INTO ByDao (Id, N, D) SELECT Id, N, D FROM BySql', $dbFailOnError)
        if ($db.RecordsAffected -ne $numbers.Count) { throw "ByDao was given $($db.RecordsAffected) rows, not $($numbers.Count)." }
    }
    finally { $db.Close() }
    $definition = $null; $db = $null
}
finally {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

$bytes = [IO.File]::ReadAllBytes($outPath)
if ($bytes[0x14] -ne 0x06) { throw ('The file has the version byte {0:X2}, not 06: this Access has no Date/Time Extended.' -f $bytes[0x14]) }
Write-Host ('{0}: {1} bytes, version {2:X2} {3:X2}' -f (Split-Path $outPath -Leaf), $bytes.Length, $bytes[0x14], $bytes[0x15])
