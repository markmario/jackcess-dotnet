# Runs one shard of the test project on one framework: CI splits the test classes between -Shards jobs that run side
# by side (.github/workflows/ci.yml), each class to one of them.
#
# Every shard lists the tests the same way and splits the classes the same way, so the shards run every class once
# between them. The split balances the classes' running times from $seconds below, as measured on CI; a class missing
# there counts as a second. The numbers only balance the shards: a wrong or stale one makes one shard longer, and leaves
# nothing out. A shard runs its classes in one process, and fails unless every test method the listing gives them ran,
# as its report says, and nothing else did. Methods, not tests: a theory whose data cannot be listed case by case is
# listed once and runs each case, so a shard runs more tests than it lists.
param(
    [Parameter(Mandatory)] [string] $Framework,
    [Parameter(Mandatory)] [string] $TrxName,
    [ValidateRange(1, 64)] [int] $Shard = 1,
    [ValidateRange(1, 64)] [int] $Shards = 1,
    # MSBuild properties the build was given (-p:NetStandardAsset=true), so that the same build is listed and run.
    [string[]] $Properties = @()
)
if ($Shard -gt $Shards) { throw "Shard $Shard of $Shards does not exist." }

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests'
$results = Join-Path $PSScriptRoot '..\TestResults'
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

# Seconds a class took on a windows-latest runner (run 44515421617, 2026-11-19): on .NET Framework (net48, and the
# netstandard2.0 assembly on it), and on .NET (net10.0, net11.0, and the netstandard2.0 assembly on net10.0). Only the
# slow ones. ExtendedDateFormsTests and ProtectedFileTests came after that run: theirs are a local run's, scaled by how
# much slower CI was for the rest. Each shard's summary shows what each class took beside its number here.
$seconds = if ($Framework -like 'net4*') {
    @{
        CursorThirdReviewTests = 117; SecondaryIndexTests = 68; AccessMadeIndexTests = 56; LongValueRoomTests = 46
        EngineReviewTests = 45; StableRowTests = 33; UniqueIndexTrustTests = 22; ForeignKeyCascadeTestsThroughIndexes = 22
        SecondReviewTests = 21; ExtendedDateFormsTests = 20; TrustedIndexTests = 17; IndexCursorTests = 14
        ForeignKeyCascadeTestsByScan = 14; ProtectedFileTests = 14; IncompleteIndexTests = 8; AddIndexTests = 8
        PrimaryKeyMaintenanceTests = 7; CursorOperationTests = 6; AccdbCreationTests = 6; ReviewFindingsTests = 6
        AccessEntriesTests = 6; SchemaChangeUndoTests = 6; LargeNumberExtendedDateTests = 6; SchemaRemovalTests = 4
        EntriesOfNoRowTests = 4; AccessCatalogTests = 3; AddPrimaryKeyTests = 3; GeneralSortOrderTests = 3
    }
} else {
    @{
        SecondaryIndexTests = 50; CursorThirdReviewTests = 47; LongValueRoomTests = 36; EngineReviewTests = 30
        ForeignKeyCascadeTestsThroughIndexes = 10; UniqueIndexTrustTests = 9; StableRowTests = 8; AccessMadeIndexTests = 7
        ForeignKeyCascadeTestsByScan = 7; TrustedIndexTests = 7; ReviewFindingsTests = 6; PrimaryKeyMaintenanceTests = 5
        IncompleteIndexTests = 4; IndexCursorTests = 4; AccessCatalogTests = 4; AddIndexTests = 3; SchemaRemovalTests = 3
        SecondReviewTests = 3; SchemaChangeUndoTests = 3
    }
}
function Get-Seconds([string] $class) { $short = $class.Substring($namespace.Length); if ($seconds.ContainsKey($short)) { $seconds[$short] } else { 1 } }

# Longest first, each to the shard with least so far; ties by name and by shard number, so every run splits alike.
$load = New-Object 'int[]' $Shards
$mine = [System.Collections.Generic.List[string]]::new()
foreach ($class in ($classes | Sort-Object @{ Expression = { Get-Seconds $_ }; Descending = $true }, @{ Expression = { $_ } })) {
    $least = 0
    for ($i = 1; $i -lt $Shards; $i++) { if ($load[$i] -lt $load[$least]) { $least = $i } }
    $load[$least] += Get-Seconds $class
    if ($least -eq $Shard - 1) { $mine.Add($class) }
}
$expected = @($listed | Where-Object { $mine.Contains((& $classOf $_)) })
$methods = @($expected | ForEach-Object { & $methodOf $_ } | Sort-Object -Unique)
Write-Host "$Framework, shard $Shard of $Shards`: $($expected.Count) of the $($listed.Count) tests listed, in $($mine.Count) of the $($classes.Count) classes, about $($load[$Shard - 1]) s: $(($mine | ForEach-Object { $_.Substring($namespace.Length) }) -join ', ')"

$filters = @($mine | ForEach-Object { '--filter-class'; $_ })
& dotnet test --project $project --configuration Release --no-build --framework $Framework @Properties @filters `
    --results-directory $results --report-xunit-trx --report-xunit-trx-filename $TrxName
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# What ran, from the report: each result's class and method.
[xml] $report = Get-Content -LiteralPath (Join-Path $results $TrxName) -Raw
$ns = New-Object Xml.XmlNamespaceManager $report.NameTable
$ns.AddNamespace('t', $report.DocumentElement.NamespaceURI)
$ran = @($report.SelectNodes('//t:UnitTest/t:TestMethod', $ns) | ForEach-Object { "$($_.className).$($_.name)" })
$missing = @($methods | Where-Object { $ran -notcontains $_ })
$others = @($ran | Sort-Object -Unique | Where-Object { $methods -notcontains $_ })

# How long each class took, beside what $seconds says, in the job's summary; a class far slower than its number - a
# new one, say - is named in a warning, since it makes its shard the run's longest.
$classOfTest = @{}
foreach ($test in $report.SelectNodes('//t:UnitTest', $ns)) { $classOfTest[$test.id] = $test.SelectSingleNode('t:TestMethod', $ns).className }
$took = @{}
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
if ($missing.Count -or $others.Count) {
    if ($missing.Count) { Write-Host "::error::$Framework, shard $Shard of $Shards`: $($missing.Count) test methods listed for it did not run: $($missing -join ', ')" }
    if ($others.Count) { Write-Host "::error::$Framework, shard $Shard of $Shards`: $($others.Count) test methods not listed for it ran: $($others -join ', ')" }
    exit 1
}
Write-Host "$Framework, shard $Shard of $Shards`: all $($methods.Count) test methods listed for it ran, $($ran.Count) tests."
exit 0
