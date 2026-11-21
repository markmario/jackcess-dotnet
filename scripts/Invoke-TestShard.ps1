# Runs one shard of the test project on one framework: CI splits the test classes between -Shards jobs that run side
# by side (.github/workflows/ci.yml), each class to one of them.
#
# Every shard lists the tests the same way and splits the classes the same way, so the shards run every class once
# between them. The split balances the classes' running times from $timings below, as measured on CI; a class missing
# there counts as a second, and is named in a warning. The numbers only balance the shards: a wrong or stale one makes
# one shard longer, and leaves nothing out. A shard runs its classes in one process, and fails unless every test
# method the listing gives them ran, as its report says, and nothing else did. Methods, not tests: a theory whose data
# cannot be listed case by case is listed once and runs each case, so a shard runs more tests than it lists.
#
# -RequireTimings, which CI gives a pull request's run, makes a class with no row a failure rather than a warning, so
# that the table cannot fall behind the tests unnoticed. The shard still runs its tests first, and its error gives the
# seconds the class took here, which is the number its row wants in this configuration's column.
param(
    [Parameter(Mandatory)] [string] $Framework,
    [Parameter(Mandatory)] [string] $TrxName,
    [ValidateRange(1, 64)] [int] $Shard = 1,
    [ValidateRange(1, 64)] [int] $Shards = 1,
    # MSBuild properties the build was given (-p:NetStandardAsset=true), so that the same build is listed and run.
    [string[]] $Properties = @(),
    [switch] $RequireTimings
)
if ($Shard -gt $Shards) { throw "Shard $Shard of $Shards does not exist." }

$ErrorActionPreference = 'Stop'
# Forward slashes, which Windows takes too: the Linux job runs this script as well.
$project = Join-Path $PSScriptRoot '../tests/JackcessDotNet.Tests'
$results = Join-Path $PSScriptRoot '../TestResults'
$namespace = 'JackcessDotNet.Tests.'
$Properties = @($Properties | Where-Object { $_ })

# A terminal's colour codes, which the platform writes on a build agent too, are taken off first.
$listing = @(& dotnet test --project $project --configuration Release --no-build --framework $Framework @Properties --list-tests 2>&1 |
    ForEach-Object { (([string]$_) -replace '\x1b\[[0-9;]*m', '').Trim() })
if ($LASTEXITCODE -ne 0) {
    Write-Host "::error::The tests of $Framework could not be listed."
    exit 1
}
$listed = @($listing | Where-Object { $_.StartsWith($namespace) })
# The platform says how many tests it found ("Discovered 2443 tests."). A test named otherwise than by its class and
# method - a DisplayName of its own - is not among those taken above, so its class would be split into no shard and run
# by none, with nothing else to notice: a listing that does not add up is refused.
# The last such line is the platform's total; one before it is an assembly's.
$said = @($listing | Where-Object { $_ -match '^Discovered\s+[\d,.]+\s+tests?\b' })
$discovered = @($said | Select-Object -Last 1 | ForEach-Object { [int](($_ -split '\s+')[1] -replace '\D', '') })
if ($discovered.Count -ne 1 -or $discovered[0] -ne $listed.Count -or $listed.Count -eq 0) {
    $found = if ($discovered.Count -eq 1) { $discovered[0] } else { 'no number it said of' }
    Write-Host "::error::The test platform found $found tests on $Framework, and $($listed.Count) are named $namespace<class>.<method>, which is how this script finds a test's class."
    Write-Host "What the platform said of the count: $(@($listing | Where-Object { $_ -match 'Discovered' }) -join ' | ')"
    exit 1
}
# A listed test is its display name: the class, the method and, for a theory's case, its arguments in brackets.
$methodOf = {
    param([string] $name)
    $bracket = $name.IndexOf('(')
    if ($bracket -ge 0) { $name.Substring(0, $bracket) } else { $name }
}
$classOf = { param([string] $name) $method = & $methodOf $name; $method.Substring(0, $method.LastIndexOf('.')) }
$classes = @($listed | ForEach-Object { & $classOf $_ } | Sort-Object -Unique)

# Seconds each class took on a windows-latest runner, in each configuration: the mean of runs 44859335561, 44860125207
# and 44860480279 (2026-11-21), from their test reports; OADateTests the mean of runs 44864425043, 44865291603 and
# 44866192633; NumberTextTests as it ran on a developer's machine, until a run's report times it. ns- is the netstandard2.0 assembly on that framework, and x86 the 32-bit process. Every class has a row,
# in the order of the names: one that is missing is counted as 1 s and named in a warning, or with -RequireTimings an
# error. Each shard's summary shows what each class took beside its number.
$timings = @'
Class                                     net10.0    net11.0      net48  net48-x86 ns-net10.0   ns-net48
AccdbCreationTests                            1.8        1.9        4.4        5.9        1.7        5.0
Access97ReadOnlyTests                         0.6        0.6        0.7        0.9        0.6        1.1
Access97ReadTests                             1.0        0.9        1.6        2.2        1.0        1.9
Access97TextKeyTests                          0.2        0.2        0.2        0.2        0.1        0.2
AccessCatalogTests                            1.5        1.5        2.4        3.1        3.2        2.7
AccessControlEntryTests                       0.0        0.0        0.0        0.0        0.0        0.0
AccessEntriesTests                            1.7        2.1        5.9        5.5        1.3        4.5
AccessInteropRegressionTests                  0.1        0.1        0.1        0.1        0.1        0.1
AccessLargeNumberExtendedDateTests            0.0        0.0        0.1        0.0        0.0        0.0
AccessMadeIndexTests                          7.1        7.2       48.4       47.1        5.9       45.6
AddColumnNewTypesTests                        0.0        0.0        0.0        0.0        0.1        0.0
AddColumnTests                                0.3        0.3        0.3        0.8        0.3        0.4
AddIndexTests                                 1.5        1.4        5.0        5.1        1.2        4.5
AddPrimaryKeyTests                            0.1        0.1        0.1        0.2        0.1        0.2
AutomationDecimalTests                        0.4        0.0        1.0        0.6        0.2        0.9
AutoNumberTests                               0.4        0.2        0.3        0.6        0.1        0.2
BinaryLengthTests                             0.2        0.0        0.0        0.0        0.0        0.0
BulkInsertTests                               6.8        6.6       16.4       18.7        6.1       17.4
CodecTests                                    0.0        0.0        0.0        0.0        0.0        0.0
ColumnDefinitionTests                         0.2        0.2        0.2        0.2        0.1        0.3
ColumnRuleTests                               0.1        0.1        0.1        0.6        0.3        0.1
CompositePrimaryKeyTests                      0.0        0.0        0.0        0.0        0.0        0.1
CorpusTests                                   5.6        5.2        8.9        7.9        4.6        6.7
CreateRelationshipTests                       0.6        0.6        1.0        1.1        0.5        0.9
CreateTableUndoTests                          1.1        1.2        1.7        1.7        0.9        1.8
CrossSessionFreedSpaceTests                   0.0        0.0        0.4        0.0        0.0        0.0
CurrencyRoundingTests                         0.0        0.0        0.0        0.0        0.1        0.0
CurrencyValueTests                            1.1        1.1        1.1        1.1        1.0        0.9
CursorEdgeCaseTests                           0.0        0.0        0.0        0.0        0.0        0.1
CursorOperationTests                          2.2        1.9        3.9        5.7        2.0        6.3
CursorPassModelTests                          0.9        0.9        1.2        1.6        0.7        1.5
CursorReviewTests                             0.2        0.2        0.4        0.3        0.1        0.2
CursorTests                                   0.0        0.0        0.0        0.0        0.0        0.0
CursorThirdReviewTests                       31.7       28.5       71.9      103.5       25.9       81.5
CutKeyTests                                   0.5        0.2        0.6        0.8        0.5        0.9
DamagedIndexTests                             0.6        0.7        0.6        0.8        1.1        0.8
DatabaseAndTableShapeTests                    0.3        0.2        0.8        0.7        0.2        0.5
DatabaseExporterTests                         0.1        0.3        1.2        0.3        0.1        0.2
DatabaseHeaderTests                           0.0        0.0        0.0        0.0        0.0        0.0
DatabaseImporterTests                         0.3        0.2        1.0        0.9        0.3        0.5
DataTypeExtensionsTests                       0.0        0.0        0.0        0.0        0.0        0.0
DataTypeRoundTripTests                        0.1        0.1        0.1        0.1        0.1        0.4
DecimalBeyondPrecisionTests                   0.1        0.1        0.1        0.1        0.1        0.1
DecimalPlacesTests                            1.6        1.6        1.9        2.6        1.3        2.0
EarlierVersionTablesTests                     0.0        0.0        0.0        0.0        0.0        0.0
EcmaStandardTests                             0.3        0.4        1.5        1.6        0.3        1.2
EngineReviewTests                            34.9       30.8       32.2       55.3       30.6       47.7
EntriesOfNoRowTests                           1.5        1.4        2.7        4.2        1.5        4.2
ExtendedDateFormsTests                        6.0        5.9       11.5       15.9        4.9       17.0
ExtendedDateReadingTests                      0.0        0.0        0.0        0.0        0.0        0.0
ExtendedDateTableTypeTests                    4.4        4.4        8.0       11.2        3.7        9.3
FileSharingTests                              0.1        0.1        0.1        0.1        0.1        0.1
FileSizeLimitTests                            0.1        0.0        0.1        0.1        0.1        0.1
FloatingKeyTests                              0.0        0.0        0.1        0.1        0.0        0.1
ForeignKeyCascadeTestsByScan                  6.8        6.5       15.9       17.9        6.4       14.5
ForeignKeyCascadeTestsThroughIndexes         10.3        9.4       29.2       28.4        9.8       21.7
ForeignKeyEnforcementTests                    0.0        0.0        0.0        0.0        0.0        0.0
FreedSpaceTests                               0.5        0.5        1.3        1.2        0.5        1.1
GeneralForeignKeyTests                        0.1        0.1        0.2        0.2        0.1        0.2
GeneralSortOrderTests                         0.7        0.7        2.9        2.6        0.7        2.2
ImportConversionTests                         2.3        2.6        1.1        1.6        2.3        2.0
ImportExactnessTests                          0.4        0.3        0.4        0.4        0.5        0.4
IncompleteIndexTests                          3.5        2.9        5.8        8.3        2.6        8.3
IndexAndRelationshipFlagTests                 0.0        0.0        0.0        0.0        0.0        0.0
IndexCursorTests                              2.0        0.8        0.9        1.8        0.7        2.0
IndexMetadataTests                            0.0        0.0        0.0        0.0        0.0        0.0
IndexReaderTests                              1.2        1.4        1.5        1.5        1.2        2.2
InsertRangeTests                              0.2        0.2        0.3        0.4        0.3        0.8
InsertRefusalTests                            0.1        0.1        0.1        0.1        0.1        0.1
JetCryptKeyDerivationTests                    0.0        0.0        0.0        0.0        0.0        0.1
JetFormatTests                                0.0        0.0        0.0        0.0        0.0        0.0
JoinerTests                                   0.1        0.1        0.1        0.1        0.1        0.1
KeepWritersOutTests                           2.8        4.8        6.7        6.5        2.4        5.2
KitchenSinkTests                              0.0        0.0        0.0        0.0        0.0        0.0
LargeFileTests                                0.7        0.7        0.8        0.9        0.6        1.1
LargeNumberExtendedDateTests                  2.7        1.6        6.6        5.7        1.3        5.2
LegacyIncompleteIndexTests                    0.0        0.0        0.0        0.0        0.0        0.1
LegacyLongKeyTests                            0.2        0.2        0.9        1.3        0.2        0.7
LegacyUniqueIndexTests                        0.0        0.0        0.0        0.0        0.0        0.0
LibraryAssemblyTests                          0.0        0.0        0.0        0.0        0.0        0.0
LocaleSortOrderTests                          0.0        0.0        0.0        0.0        0.0        0.0
LongValueRoomTests                           38.6       43.8       37.7       50.6       35.4       55.2
LookupMatchTests                              0.0        0.1        0.1        0.1        0.0        0.2
MemoCompressionTests                          0.0        0.0        0.1        0.1        0.0        0.0
MultiPageDefinitionTests                      1.1        1.0        2.6        1.2        1.1        1.5
NewTypeForeignKeyTests                        0.0        0.0        0.1        0.2        0.0        0.1
NumberKindsTests                              0.1        0.1        0.1        0.1        0.1        0.5
NumberTextTests                               1.0        1.0        1.5        1.5        1.0        1.5
OADateChoiceTests                             0.9        0.8        0.7        2.3        0.7        0.9
OADateTests                                   0.3        0.4        0.5        0.6        0.3        0.6
ObjectNameTests                               0.4        0.4        0.7        0.8        0.4        0.9
OfficeCryptTests                              3.5        2.5        6.6       10.7        2.3        9.5
OneToOneRelationshipTests                     0.0        0.0        0.2        0.0        0.0        0.1
PkIndexRoundTripTests                         0.4        0.3        0.9        0.9        0.3        0.7
PrimaryKeyMaintenanceTests                    5.1        5.0        9.2        8.5        4.5        6.9
PropertyMapAccessTests                        0.0        0.0        0.0        0.0        0.0        0.0
PropertyMapTests                              0.0        0.0        0.0        0.0        0.0        0.0
PropertyReviewTests                           0.1        0.1        0.1        0.1        0.0        0.1
PropertyWriteTests                            0.2        0.1        0.3        0.2        0.1        0.2
ProtectedFileTests                            4.5        4.6       12.4       18.4        3.8       13.8
ReaderBesideWriterTests                       0.7        0.7        1.4        1.6        0.7        1.3
ReadOnlyOpenTests                             3.5        2.4        2.3        3.4        2.1        3.4
RefusalTypeTests                              0.0        0.0        0.0        0.0        0.0        0.1
RefusedWritesTests                            0.2        0.3        0.4        0.4        0.2        0.3
RelationshipTests                             0.0        0.0        0.0        0.0        0.0        0.0
ReviewFindingsTests                           5.4        5.3        5.8        5.1        5.5        5.5
RoomReuseTests                               10.1        9.8       61.5       76.9       10.0       89.8
RowLayoutTests                                0.6        0.5        0.5        0.8        1.2        0.6
RowTests                                      0.0        0.0        0.1        0.0        0.0        0.0
RowUpdateDeleteTests                          0.1        0.2        0.3        0.8        0.1        0.6
SchemaChangeUndoTests                         0.2        0.2        0.3        0.3        0.2        0.2
SchemaRemovalTests                            1.7        1.7        3.2        3.3        1.5        2.6
SchemaRenameTests                             0.2        0.2        0.7        0.2        0.4        0.2
SecondaryIndexTests                          59.6       60.0       91.6       86.6       49.9       69.5
SecondReviewTests                             2.3        2.4       23.0       20.1        1.9       18.9
SmokeTests                                    0.1        0.1        0.1        0.1        0.1        0.1
StableRowTests                                5.6        4.0        7.9        8.7        3.9        7.4
StoredRangeTests                              0.0        0.0        0.0        0.0        0.0        0.0
TextColumnLengthTests                         0.0        0.0        0.0        0.0        0.0        0.0
TextIndexKeyTests                             0.0        0.0        0.0        0.0        0.0        0.1
TextMarkerTests                               0.1        0.1        0.1        0.1        0.5        0.7
TrustedIndexTests                             6.4        5.4       20.8       20.4        4.8       15.6
UndoJournalTests                              0.1        0.1        0.2        0.2        0.1        0.1
UniqueIndexTrustTests                         8.0        9.3       22.4       24.8        6.4       19.9
UsageMapTests                                 0.0        0.0        0.0        0.0        0.0        0.0
WideDecimalTests                              0.1        0.1        0.0        0.0        0.1        0.1
'@ -split '\r?\n'
$columns = @($timings[0].Trim() -split '\s+' | Select-Object -Skip 1)
$configuration = $Framework
if ($Properties -match '^[-/]p(roperty)?:TestX86=true$') { $configuration = "$configuration-x86" }
if ($Properties -match '^[-/]p(roperty)?:NetStandardAsset=true$') { $configuration = "ns-$configuration" }
# A configuration CI does not run is balanced by the net48 column on .NET Framework and the net10.0 column otherwise.
if ($columns -notcontains $configuration) { $configuration = if ($Framework -like 'net4*') { 'net48' } else { 'net10.0' } }
$column = [array]::IndexOf($columns, $configuration) + 1
$seconds = @{}
foreach ($row in ($timings | Select-Object -Skip 1)) { $cells = $row.Trim() -split '\s+'; $seconds[$cells[0]] = [double] $cells[$column] }
function Get-Seconds([string] $class) { $short = $class.Substring($namespace.Length); if ($seconds.ContainsKey($short)) { $seconds[$short] } else { 1 } }
$untimed = @($classes | ForEach-Object { $_.Substring($namespace.Length) } | Where-Object { -not $seconds.ContainsKey($_) })
if ($untimed.Count -and -not $RequireTimings) { Write-Host "::warning::$($untimed.Count) test classes have no row in the timing table of scripts/Invoke-TestShard.ps1 and are counted as 1 s each, so the shards may not be balanced: $($untimed -join ', ')" }

# Longest first, each to the shard with least so far; ties by name and by shard number, so every run splits alike.
$load = New-Object 'double[]' $Shards
$mine = [System.Collections.Generic.List[string]]::new()
foreach ($class in ($classes | Sort-Object @{ Expression = { Get-Seconds $_ }; Descending = $true }, @{ Expression = { $_ } })) {
    $least = 0
    for ($i = 1; $i -lt $Shards; $i++) { if ($load[$i] -lt $load[$least]) { $least = $i } }
    $load[$least] += Get-Seconds $class
    if ($least -eq $Shard - 1) { $mine.Add($class) }
}
$expected = @($listed | Where-Object { $mine.Contains((& $classOf $_)) })
$methods = @($expected | ForEach-Object { & $methodOf $_ } | Sort-Object -Unique)
Write-Host "$Framework, shard $Shard of $Shards`: $($expected.Count) of the $($listed.Count) tests listed, in $($mine.Count) of the $($classes.Count) classes, about $([math]::Round($load[$Shard - 1])) s: $(($mine | ForEach-Object { $_.Substring($namespace.Length) }) -join ', ')"

$filters = @($mine | ForEach-Object { '--filter-class'; $_ })
# A report a run before this one left is not this run's: it goes first, so that only this run's is read.
$trx = Join-Path $results $TrxName
Remove-Item -LiteralPath $trx -ErrorAction SilentlyContinue
& dotnet test --project $project --configuration Release --no-build --framework $Framework @Properties @filters `
    --results-directory $results --report-xunit-trx --report-xunit-trx-filename $TrxName
# A test that failed fails the shard, but after what the report says of each class's time, so that a missing row is
# named in the same run. A report that does not parse - one the test process did not finish - is none, and the run's
# own exit code is the shard's.
$testExit = $LASTEXITCODE
$report = $null
if (Test-Path -LiteralPath $trx) {
    try { [xml] $report = Get-Content -LiteralPath $trx -Raw }
    catch { Write-Host "::warning::$trx does not parse, and is not read: $($_.Exception.Message)"; $report = $null }
}

# How long each class took, beside what $seconds says, in the job's summary; a class far slower than its number - a
# new one, say - is named in a warning, since it makes its shard the run's longest.
$took = @{}
if ($report) {
    $ns = New-Object Xml.XmlNamespaceManager $report.NameTable
    $ns.AddNamespace('t', $report.DocumentElement.NamespaceURI)
    $classOfTest = @{}
    foreach ($test in $report.SelectNodes('//t:UnitTest', $ns)) { $classOfTest[$test.id] = $test.SelectSingleNode('t:TestMethod', $ns).className }
    foreach ($result in $report.SelectNodes('//t:UnitTestResult', $ns)) {
        if ($result.duration) { $took[$classOfTest[$result.testId]] += [TimeSpan]::Parse($result.duration).TotalSeconds }
    }
    $rows = $took.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
        $table = Get-Seconds $_.Key
        if ($_.Value -gt 2 * $table + 10) { Write-Host "::warning::$($_.Key.Substring($namespace.Length)) took $([math]::Round($_.Value)) s on $Framework; scripts/Invoke-TestShard.ps1 has it at $table s. Update the number, or the shards will not be balanced." }
        "| $($_.Key.Substring($namespace.Length)) | $([math]::Round($_.Value)) | $table |"
    }
    if ($env:GITHUB_STEP_SUMMARY) {
        @("### $Framework$(if ($Properties) { " ($($Properties -join ' '))" }), shard $Shard of $Shards", '', '| Class | Seconds | In the table |', '|---|---|---|') + @($rows) + '' |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
}
if ($RequireTimings) {
    foreach ($class in $untimed) {
        $here = if ($took.ContainsKey("$namespace$class")) { "$([math]::Round($took["$namespace$class"], 1)) in the $($columns[$column - 1]) column, the seconds it took in this job" }
                else { "in the $($columns[$column - 1]) column the seconds it took in the shard of this configuration that ran it, whose log gives them" }
        Write-Host "::error::$class has no row in the timing table of scripts/Invoke-TestShard.ps1. Add one, in the order of the names, with $here. The other columns are in the other configurations' errors; a configuration that does not compile the class names it in none, and has 0 in its column."
    }
}
if ($testExit -ne 0) { exit $testExit }
if (-not $report) {
    Write-Host "::error::$Framework, shard $Shard of $Shards`: the tests wrote no report, $trx."
    exit 1
}

# What ran, from the report: each result's class and method.
$ran = @($report.SelectNodes('//t:UnitTest/t:TestMethod', $ns) | ForEach-Object { "$($_.className).$($_.name)" })
$missing = @($methods | Where-Object { $ran -notcontains $_ })
$others = @($ran | Sort-Object -Unique | Where-Object { $methods -notcontains $_ })
if ($missing.Count -or $others.Count -or ($RequireTimings -and $untimed.Count)) {
    if ($missing.Count) { Write-Host "::error::$Framework, shard $Shard of $Shards`: $($missing.Count) test methods listed for it did not run: $($missing -join ', ')" }
    if ($others.Count) { Write-Host "::error::$Framework, shard $Shard of $Shards`: $($others.Count) test methods not listed for it ran: $($others -join ', ')" }
    exit 1
}
Write-Host "$Framework, shard $Shard of $Shards`: all $($methods.Count) test methods listed for it ran, $($ran.Count) tests."
exit 0
