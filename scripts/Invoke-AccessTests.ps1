#Requires -Version 7.0
# Runs the built Access referee tests (tests/JackcessDotNet.AccessTests), one test class - or piece of one - to a
# process, and runs a process again if it died inside the Access Database Engine.
#
# It runs the test executable itself (bin\Release\net48\JackcessDotNet.AccessTests.exe, xUnit.net v3's own runner),
# so it needs the build and no .NET SDK: CI builds once and hands the binaries to the shards. Each process writes an
# xUnit.net XML report, which is what is read for the number of tests it ran and for how each failed.
#
#   dotnet build tests/JackcessDotNet.AccessTests -c Release
#   ./scripts/Invoke-AccessTests.ps1 -Parallel 2
#
# The Access Database Engine 2016 redistributable - what CI installs - has killed test processes with exit code
# -1073740771 (0xC000041D) and no test failed. A crash dump showed where: an access violation in
# mso99Lwin32client.dll, called from mso40uiwin32client.dll, in a window callback on a thread ACEOLEDB.DLL starts for
# itself. None of this repository's code nor the test platform's is on that thread, and it has not happened on a
# machine with Microsoft 365's Access.
#
# The longer a process uses that engine the likelier it dies, so each test class has a process of its own. A death
# costs that class alone, and it is run again, up to twice, with a warning on the run each time. A test that fails, or
# any other way of ending, fails the step the first time; the processes still to run are run, so one run reports
# every piece that fails.
#
# The engine has a second way of dying: an access violation inside OleDbConnection.Open
# (System.Data.OleDb.DataSourceWrapper.InitializeAndCreateSession), which .NET Framework raises as an
# AccessViolationException that ends the process with exit code -532462766, every test before it having passed. That
# code is any unhandled exception's, so it is run again only when the output names that exception, raised under
# System.Data.OleDb, and no test failed.
#
# And one way of failing a test: an OleDbException whose message ACE did not write - it begins with a space,
# " in query expression '?'." - raised by ACE's own work, on a file ACE made, and passing when run again. A piece whose
# only failures are such is run once more, with a warning naming each test. Any other OleDbException fails at once: a
# message ACE wrote, about a file the engine wrote, is what these tests are for.
#
# Nothing is left out by running class by class: the tests are listed first, every process must run the number of
# tests the listing gives it, and all of them together the number listed.
#
# A few classes take minutes, and run in pieces ($pieces below): some of their test methods in one process, the cases of
# a theory whose display names end alike in another, and the rest of the class in a last one. A method a piece no
# longer names - renamed, say - is run with the rest of its class, so a stale entry costs balance and nothing else.
#
# -Parallel runs that many processes at once, the longest first. ACE refuses two connections opened at once in one
# process ("Non-NULL controlling IUnknown"), which is why the tests in a process run one at a time; processes of their
# own, on files of their own, do not share it. Each process has a temporary folder of its own (TEMP and TMP), where its
# tests make their databases and ACE its temporary files, removed when it ends. Every line a process prints is printed
# as it comes, after the name of its piece.
#
# -Shard and -Shards split the pieces between that many runs, so CI can run them on runners side by side. Every run
# lists the same tests and splits them the same way, so the shards cover every piece once between them. The split
# balances the pieces' running times from $seconds below, as measured on CI, for the samples a pull request runs and for
# the full sweeps (JACKCESS_FULL_SWEEPS=1, see tests/JackcessDotNet.AccessTests/Support/Sweeps.cs). A piece missing
# there is taken to be slow, and named in a warning; one that takes far longer than its number is too, in the job's
# summary, which lists every piece's time beside its number. The numbers only balance the shards: a wrong or stale one
# makes one shard longer, and leaves nothing out.
#
# The redistributable can also hang: a DAO or OLE DB call that never returns. A process that runs far longer than its
# number (five times it, and at least ten minutes) is ended and counted as one that died inside the engine, so it is run
# again, up to twice; a test that truly never ends still fails, on its third attempt.
#
# -ProcessorsEach keeps each process to that many logical processors of its own (0, the default, leaves them all): the
# redistributable's deaths are in its own threads, and CI gives each process one processor.
param(
    [string] $TrxName = 'access-tests.trx',
    [ValidateRange(1, 64)] [int] $Shard = 1,
    [ValidateRange(1, 64)] [int] $Shards = 1,
    [ValidateRange(1, 8)] [int] $Parallel = 1,
    [ValidateRange(0, 64)] [int] $ProcessorsEach = 0
)
if ($Shard -gt $Shards) { throw "Shard $Shard of $Shards does not exist." }

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'tests\JackcessDotNet.AccessTests\bin\Release\net48\JackcessDotNet.AccessTests.exe'
$results = Join-Path $root 'TestResults'
$namespace = 'JackcessDotNet.AccessTests.'
$crashedInAce = -1073740771            # 0xC000041D
$crashedInOleDb = -532462766           # an unhandled .NET exception; which one is read from the output
$attempts = 3
$unknownSeconds = 240                  # what a piece missing from the table is taken to take
$full = $env:JACKCESS_FULL_SWEEPS -eq '1'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "::error::$exe is not there: build tests/JackcessDotNet.AccessTests (Release) first."
    exit 1
}
New-Item -ItemType Directory -Force $results | Out-Null

# The classes run in pieces. Each piece names test methods of its class (Methods), or one theory (Method) and the ends
# of the display names of the cases it takes (Cases). A theory's cases no piece takes run in a piece of their own, and
# the methods no piece names in the class's last piece, under the class's own name.
$walk = 'dao_walks_the_engine_s_index_in_the_order_of_ace_s_own_in_a_database_of_a_locale_keyed_as_general_legacy'
$rows = 'rows_updated_deleted_and_added_in_a_table_of_any_key_are_the_rows_access_has'
$moved = 'rows_the_engine_updates_where_they_are_and_moves_off_their_page_are_rows_access_reads_finds_updates_and_deletes'
$pieces = @{
    # Two minutes and more a locale in the full sweep.
    SortOrdersInAccessTests = @(
        @{ Name = 'WalkArabic'; Method = $walk; Cases = 'name: "Arabic")' }
        @{ Name = 'WalkGerman'; Method = $walk; Cases = 'name: "German")' }
        @{ Name = 'WalkGreek'; Method = $walk; Cases = 'name: "Greek")' }
        @{ Name = 'WalkHebrew'; Method = $walk; Cases = 'name: "Hebrew")' }
        @{ Name = 'WalkItalian'; Method = $walk; Cases = 'name: "Italian")' }
        @{ Name = 'WalkDutch'; Method = $walk; Cases = 'name: "Dutch")' }
        @{ Name = 'WalkCyrillic'; Method = $walk; Cases = 'name: "Cyrillic")' }
        @{ Name = 'WalkEnglishUK'; Method = $walk; Cases = 'name: "English (United Kingdom)")' }
        @{ Name = 'WalkPortuguese'; Method = $walk; Cases = 'name: "Portuguese")' }
        @{ Name = 'Seeks'; Methods = 'ace_seeks_and_adds_to_a_text_key_the_engine_built_in_a_database_of_a_locale_keyed_as_general_legacy' }
    )
    SchemaChangesInAccessTests = @(
        @{ Name = 'Boundaries'; Methods = @(
            'tables_the_engine_makes_with_definitions_ending_near_a_pages_end_are_ones_access_reads_adds_to_and_compacts'
            'columns_the_engine_adds_that_end_a_definition_near_a_pages_end_leave_tables_access_reads_adds_to_and_compacts'
            'tables_access_made_with_definitions_ending_near_a_pages_end_are_still_accesss_after_the_engine_changes_them') }
        @{ Name = 'ColumnDrops'; Methods = 'columns_the_engine_drops_are_gone_as_the_ones_ace_drops_and_both_write_on' }
        @{ Name = 'RelationshipDrops'; Methods = 'a_relationship_the_engine_drops_is_gone_as_the_one_dao_drops' }
        @{ Name = 'Drops'; Methods = @(
            'an_index_the_engine_drops_is_gone_as_the_one_ace_drops_and_ace_writes_on'
            'an_index_dao_does_not_drop_is_not_dropped_by_the_engine_with_the_file_as_it_was'
            'a_column_of_a_relationship_is_not_dropped_by_dao_or_by_the_engine'
            'a_table_that_has_had_255_columns_takes_no_more_from_ace_or_from_the_engine_until_dao_compacts_it'
            'an_index_takes_the_number_a_dropped_index_left_as_ace_gives_it'
            'a_relationship_the_engine_creates_is_one_dao_drops_and_the_engine_drops_one_of_a_table_to_itself'
            'a_table_the_engine_drops_is_gone_as_the_one_ace_drops_and_ace_uses_its_pages_again'
            'a_wide_table_and_a_table_related_to_itself_are_dropped_as_ace_drops_them') }
        @{ Name = 'Relationships'; Methods = @(
            'a_relationship_the_engine_creates_is_the_one_dao_creates_and_ace_and_the_engine_keep_it'
            'a_relationship_to_a_unique_foreign_key_is_one_to_one_with_a_unique_index_as_dao_makes_it'
            'a_relationship_is_one_to_one_or_not_as_the_caller_says_as_dao_makes_it'
            'a_table_related_to_itself_has_the_two_indexes_dao_gives_it'
            'a_relationship_dao_refuses_is_refused_by_the_engine_with_the_file_as_it_was') }
        @{ Name = 'Renames'; Methods = @(
            'columns_indexes_and_tables_the_engine_renames_are_named_as_the_ones_dao_renames'
            'a_new_name_dao_refuses_is_refused_by_the_engine_with_the_file_as_it_was') }
    )
    ForeignKeysInAccessTests = @(
        @{ Name = 'Cascades'; Methods = @(
            'a_cascade_finds_the_children_through_the_index_for_a_key_of_any_type'
            'text_keys_with_accents_and_mixed_case_cascade_alike_in_either_sort_order'
            'a_yes_no_foreign_key_given_null_is_no') }
        @{ Name = 'Keys'; Methods = @(
            'a_key_of_two_columns_matches_null_for_null_in_each'
            'a_relationship_of_two_columns_refuses_what_matches_no_parent_in_both'
            'a_child_can_only_be_given_a_parent_that_is_there'
            'a_text_key_matches_without_regard_to_case_and_a_change_of_case_is_a_change'
            'a_foreign_key_null_in_part_refers_to_a_key_null_in_the_same_part'
            'a_date_is_the_double_access_stored_not_the_date_time_it_reads_as'
            'a_table_that_refers_to_itself_refuses_what_would_leave_a_row_without_its_parent'
            'zero_and_minus_zero_are_two_keys'
            'a_parent_that_has_a_key_never_touches_a_child_with_no_foreign_key'
            'a_parent_with_no_key_and_a_child_with_no_foreign_key_are_parent_and_child_to_access'
            'a_parent_whose_key_is_null_has_the_rows_whose_foreign_key_is_null_for_children') }
    )
    EngineIndexesInAccessTests = @(
        @{ Name = 'EveryType'; Methods = 'ace_and_the_engine_keep_an_index_on_every_column_type_between_them' }
    )
    CorpusInAccessTests = @(
        @{ Name = 'ReadsMdb'; Method = 'the_engine_reads_every_table_of_a_file_access_wrote_as_a_second_reader_reads_it'; Cases = '.mdb")' }
        @{ Name = 'ReadsAccdb'; Method = 'the_engine_reads_every_table_of_a_file_access_wrote_as_a_second_reader_reads_it'; Cases = '.accdb")' }
    )
    RowLayoutInAccessTests = @(
        @{ Name = 'ReplicationIdIndex'; Methods = 'ace_answers_through_an_index_on_a_replication_id_made_by_sql_after_the_engine_changes_the_table' }
    )
    LongValuesInAccessTests = @(
        @{ Name = 'Mdb'; Method = 'ace_reads_every_long_value_the_engine_writes_in_a_table_ace_made'; Cases = 'extension: ".mdb")' }
        @{ Name = 'Accdb'; Method = 'ace_reads_every_long_value_the_engine_writes_in_a_table_ace_made'; Cases = 'extension: ".accdb")' }
    )
    # Seven minutes a case on CI, where a read-only open reads the index again at each of 3,000 moves; 25 s with Access.
    FileSharingInAccessTests = @(
        @{ Name = 'AheadMdb'; Method = 'a_read_only_index_cursor_gives_once_a_row_ace_moves_ahead_of_it'; Cases = 'extension: ".mdb")' }
        @{ Name = 'AheadAccdb'; Method = 'a_read_only_index_cursor_gives_once_a_row_ace_moves_ahead_of_it'; Cases = 'extension: ".accdb")' }
    )
    # Eighteen minutes in one process in the full sweep: four and two minutes a case.
    CursorsAndRowsInAccessTests = @(
        @{ Name = 'TAccessKeyed'; Method = $rows; Cases = 'accessMade: True, keyed: True)' }
        @{ Name = 'TAccessUnkeyed'; Method = $rows; Cases = 'accessMade: True, keyed: False)' }
        @{ Name = 'TEngineKeyed'; Method = $rows; Cases = 'accessMade: False, keyed: True)' }
        @{ Name = 'TEngineUnkeyed'; Method = $rows; Cases = 'accessMade: False, keyed: False)' }
        @{ Name = 'UAccess'; Method = $moved; Cases = 'accessMade: True)' }
        @{ Name = 'UEngine'; Method = $moved; Cases = 'accessMade: False)' }
    )
}

# Seconds each piece took in a process of its own on a windows-latest runner, for the samples and for the full sweeps.
# The first lines of each are classes with no sweep, the same in both.
$table = @{
    samples = @{
        BinaryLengthInAccessTests = 20; CurrencyInAccessTests = 11; ExtendedDateLayoutsInAccessTests = 4; FailedWritesInAccessTests = 69
        FileSharingInAccessTests = 60; 'FileSharingInAccessTests.AheadMdb' = 430; 'FileSharingInAccessTests.AheadAccdb' = 430
        ImporterInAccessTests = 19; LookupsInAccessTests = 6; ProtectedFilesInAccessTests = 44
        RoomReuseInAccessTests = 188
        AccdbFormatsInAccessTests = 41; Access97InAccessTests = 3; CorpusInAccessTests = 12; 'CorpusInAccessTests.ReadsAccdb' = 129
        'CorpusInAccessTests.ReadsMdb' = 48; 'CorpusInAccessTests.the_engine_reads-others' = 7; CursorsAndRowsInAccessTests = 32
        'CursorsAndRowsInAccessTests.TAccessKeyed' = 107; 'CursorsAndRowsInAccessTests.TAccessUnkeyed' = 57
        'CursorsAndRowsInAccessTests.TEngineKeyed' = 101; 'CursorsAndRowsInAccessTests.TEngineUnkeyed' = 62
        'CursorsAndRowsInAccessTests.UAccess' = 46; 'CursorsAndRowsInAccessTests.UEngine' = 50; CutKeysInAccessTests = 65
        DivergentKeysInAccessTests = 1; EarlierEngineFilesInAccessTests = 18; EngineIndexesInAccessTests = 71
        'EngineIndexesInAccessTests.EveryType' = 61; EngineTablesInAccessTests = 47; ForeignKeysInAccessTests = 87
        'ForeignKeysInAccessTests.Cascades' = 76; 'ForeignKeysInAccessTests.Keys' = 91; LongValuesInAccessTests = 10
        'LongValuesInAccessTests.Accdb' = 64; 'LongValuesInAccessTests.Mdb' = 67; OverflowRowsInAccessTests = 47
        PropertiesInAccessTests = 33; RowLayoutInAccessTests = 73; 'RowLayoutInAccessTests.ReplicationIdIndex' = 106
        SchemaChangesInAccessTests = 158; 'SchemaChangesInAccessTests.Boundaries' = 41; 'SchemaChangesInAccessTests.ColumnDrops' = 112
        'SchemaChangesInAccessTests.Drops' = 102; 'SchemaChangesInAccessTests.RelationshipDrops' = 27
        'SchemaChangesInAccessTests.Relationships' = 82; 'SchemaChangesInAccessTests.Renames' = 108; SortOrdersInAccessTests = 4
        'SortOrdersInAccessTests.Seeks' = 51; 'SortOrdersInAccessTests.WalkArabic' = 9; 'SortOrdersInAccessTests.WalkCyrillic' = 13
        'SortOrdersInAccessTests.WalkDutch' = 8; 'SortOrdersInAccessTests.WalkEnglishUK' = 9; 'SortOrdersInAccessTests.WalkGerman' = 11
        'SortOrdersInAccessTests.WalkGreek' = 11; 'SortOrdersInAccessTests.WalkHebrew' = 12; 'SortOrdersInAccessTests.WalkItalian' = 9
        'SortOrdersInAccessTests.WalkPortuguese' = 7
    }
    full = @{
        BinaryLengthInAccessTests = 20; CurrencyInAccessTests = 11; ExtendedDateLayoutsInAccessTests = 4; FailedWritesInAccessTests = 69
        FileSharingInAccessTests = 60; 'FileSharingInAccessTests.AheadMdb' = 430; 'FileSharingInAccessTests.AheadAccdb' = 430
        ImporterInAccessTests = 19; LookupsInAccessTests = 6; ProtectedFilesInAccessTests = 44
        RoomReuseInAccessTests = 188
        AccdbFormatsInAccessTests = 28; Access97InAccessTests = 2; CorpusInAccessTests = 24; 'CorpusInAccessTests.ReadsAccdb' = 131
        'CorpusInAccessTests.ReadsMdb' = 56; 'CorpusInAccessTests.the_engine_reads-others' = 6; CursorsAndRowsInAccessTests = 49
        'CursorsAndRowsInAccessTests.TAccessKeyed' = 256; 'CursorsAndRowsInAccessTests.TAccessUnkeyed' = 231
        'CursorsAndRowsInAccessTests.TEngineKeyed' = 300; 'CursorsAndRowsInAccessTests.TEngineUnkeyed' = 163
        'CursorsAndRowsInAccessTests.UAccess' = 132; 'CursorsAndRowsInAccessTests.UEngine' = 127; CutKeysInAccessTests = 72
        DivergentKeysInAccessTests = 3; EarlierEngineFilesInAccessTests = 25; EngineIndexesInAccessTests = 66
        'EngineIndexesInAccessTests.EveryType' = 75; EngineTablesInAccessTests = 63; ForeignKeysInAccessTests = 111
        'ForeignKeysInAccessTests.Cascades' = 92; 'ForeignKeysInAccessTests.Keys' = 128; LongValuesInAccessTests = 10
        'LongValuesInAccessTests.Accdb' = 64; 'LongValuesInAccessTests.Mdb' = 101; OverflowRowsInAccessTests = 48
        PropertiesInAccessTests = 58; RowLayoutInAccessTests = 84; 'RowLayoutInAccessTests.ReplicationIdIndex' = 109
        SchemaChangesInAccessTests = 276; 'SchemaChangesInAccessTests.Boundaries' = 102; 'SchemaChangesInAccessTests.ColumnDrops' = 119
        'SchemaChangesInAccessTests.Drops' = 100; 'SchemaChangesInAccessTests.RelationshipDrops' = 126
        'SchemaChangesInAccessTests.Relationships' = 151; 'SchemaChangesInAccessTests.Renames' = 88; SortOrdersInAccessTests = 11
        'SortOrdersInAccessTests.Seeks' = 304; 'SortOrdersInAccessTests.WalkArabic' = 93; 'SortOrdersInAccessTests.WalkCyrillic' = 179
        'SortOrdersInAccessTests.WalkDutch' = 184; 'SortOrdersInAccessTests.WalkEnglishUK' = 125; 'SortOrdersInAccessTests.WalkGerman' = 160
        'SortOrdersInAccessTests.WalkGreek' = 99; 'SortOrdersInAccessTests.WalkHebrew' = 125; 'SortOrdersInAccessTests.WalkItalian' = 161
        'SortOrdersInAccessTests.WalkPortuguese' = 172
    }
}

$listing = @(& $exe -list tests/json -noLogo -noColor -preEnumerateTheories 2>&1)
if ($LASTEXITCODE -ne 0) {
    Write-Host "::error::The tests could not be listed: $($listing -join ' ')"
    exit 1
}
$listed = @(($listing -join "`n") | ConvertFrom-Json)
# Every test the runner lists, by its display name. One named otherwise (a DisplayName of its own) would be in no piece
# and so run by no process, and nothing else would notice: it is refused here instead.
$strays = @($listed | Where-Object { -not $_.StartsWith($namespace) })
if ($strays.Count -or $listed.Count -eq 0) {
    Write-Host "::error::$($listed.Count) tests are listed, and $($strays.Count) of them are not named $namespace<class>.<method>, which is how this script finds a test's class: $($strays -join '; ')"
    exit 1
}
# A listed test is its display name: the class, the method and, for a theory's case, its arguments in brackets.
$tests = @($listed | ForEach-Object {
    $name = $_.Substring($namespace.Length)
    $bracket = $name.IndexOf('(')
    $head = if ($bracket -ge 0) { $name.Substring(0, $bracket) } else { $name }
    $dot = $head.IndexOf('.')
    [pscustomobject]@{ Name = $_; Class = $head.Substring(0, $dot); Method = $head.Substring($dot + 1) }
})
$classes = @($tests.Class | Sort-Object -Unique)
$mode = if ($full) { 'full' } else { 'samples' }
Write-Host "$($listed.Count) tests in $($classes.Count) classes ($(if ($full) { 'the full sweeps' } else { 'the samples; JACKCESS_FULL_SWEEPS=1 runs the full sweeps' }))."

# Every piece: its name, the filter that gives it to the test runner, and the tests of the listing it is to run.
$units = [System.Collections.Generic.List[object]]::new()
foreach ($class in $classes) {
    $ofClass = @($tests | Where-Object Class -eq $class)
    $qualified = "$namespace$class"
    if (-not $pieces.ContainsKey($class)) {
        $units.Add([pscustomobject]@{ Name = $class; Filter = @('-class', $qualified); Tests = $ofClass })
        continue
    }
    $inPiece = @{}
    $named = [System.Collections.Generic.List[string]]::new()
    $theories = [ordered]@{}
    foreach ($piece in $pieces[$class]) {
        $filter = [System.Collections.Generic.List[string]]::new()
        if ($piece.Cases) {
            $ends = @($piece.Cases)
            $mine = @($ofClass | Where-Object { $test = $_; $test.Method -eq $piece.Method -and ($ends | Where-Object { $test.Name.EndsWith($_) }) })
            $filter.AddRange([string[]]@('-method', "$qualified.$($piece.Method)"))
            foreach ($end in $ends) { $filter.AddRange([string[]]@('-displayName', "*$end")) }
            if (-not $theories.Contains($piece.Method)) { $theories[$piece.Method] = [System.Collections.Generic.List[string]]::new() }
            $theories[$piece.Method].AddRange([string[]]$ends)
        } else {
            $mine = @($ofClass | Where-Object { @($piece.Methods) -contains $_.Method })
            foreach ($method in @($piece.Methods)) { $filter.AddRange([string[]]@('-method', "$qualified.$method")); $named.Add($method) }
        }
        foreach ($test in $mine) {
            if ($inPiece.ContainsKey($test.Name)) { throw "$($test.Name) is in two pieces of $class." }
            $inPiece[$test.Name] = $true
        }
        if ($mine.Count) { $units.Add([pscustomobject]@{ Name = "$class.$($piece.Name)"; Filter = $filter.ToArray(); Tests = $mine }) }
    }
    foreach ($method in $theories.Keys) {
        $rest = @($ofClass | Where-Object { $_.Method -eq $method -and -not $inPiece.ContainsKey($_.Name) })
        if (-not $rest.Count) { continue }
        $filter = [System.Collections.Generic.List[string]]::new()
        $filter.AddRange([string[]]@('-method', "$qualified.$method"))
        foreach ($end in $theories[$method]) { $filter.AddRange([string[]]@('-displayName-', "*$end")) }
        foreach ($test in $rest) { $inPiece[$test.Name] = $true }
        $units.Add([pscustomobject]@{ Name = "$class.$((($method -split '_') | Select-Object -First 3) -join '_')-others"; Filter = $filter.ToArray(); Tests = $rest })
    }
    $rest = @($ofClass | Where-Object { -not $inPiece.ContainsKey($_.Name) -and -not $theories.Contains($_.Method) })
    if ($rest.Count) {
        $filter = [System.Collections.Generic.List[string]]::new()
        $filter.AddRange([string[]]@('-class', $qualified))
        foreach ($method in @($named) + @($theories.Keys)) { $filter.AddRange([string[]]@('-method-', "$qualified.$method")) }
        $units.Add([pscustomobject]@{ Name = $class; Filter = $filter.ToArray(); Tests = $rest })
    }
}
$covered = ($units | ForEach-Object { $_.Tests.Count } | Measure-Object -Sum).Sum
if ($covered -ne $listed.Count) { throw "The pieces take $covered of the $($listed.Count) tests listed." }

$seconds = $table[$mode]
function Get-Seconds([string] $unit) { if ($seconds.ContainsKey($unit)) { $seconds[$unit] } else { $unknownSeconds } }

# Longest first, each to the shard with least so far; ties by name and by shard number, so every run splits alike.
$ordered = @($units | Sort-Object @{ Expression = { Get-Seconds $_.Name }; Descending = $true }, @{ Expression = { $_.Name } })
if ($Shards -gt 1) {
    $load = New-Object 'int[]' $Shards
    $mine = [System.Collections.Generic.List[object]]::new()
    foreach ($unit in $ordered) {
        $least = 0
        for ($i = 1; $i -lt $Shards; $i++) { if ($load[$i] -lt $load[$least]) { $least = $i } }
        $load[$least] += Get-Seconds $unit.Name
        if ($least -eq $Shard - 1) { $mine.Add($unit) }
    }
    $ordered = @($mine)
    Write-Host "Shard $Shard of $Shards`: $(($ordered | ForEach-Object { $_.Tests.Count } | Measure-Object -Sum).Sum) tests in $($ordered.Count) processes, about $($load[$Shard - 1]) s of them: $(($ordered.Name | Sort-Object) -join ', ')"
    foreach ($unit in ($ordered | Where-Object { -not $seconds.ContainsKey($_.Name) })) {
        Write-Host "::warning::$($unit.Name) has no time in scripts/Invoke-AccessTests.ps1 ($mode), so it is taken to take $unknownSeconds s. Add the time this run measured (the job's summary)."
    }
}
$expected = ($ordered | ForEach-Object { $_.Tests.Count } | Measure-Object -Sum).Sum
if ($Parallel -gt 1) { Write-Host "$Parallel processes at a time, the longest first." }

# A process of the test runner, its output gathered line by line as it comes, from both of its streams, with
# temporary folders of its own.
if (-not ('AccessTestProcess' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

public sealed class AccessTestProcess : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();

    public AccessTestProcess(string fileName, IEnumerable<string> arguments, string workingDirectory, string temp)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["TEMP"] = temp;
        info.Environment["TMP"] = temp;
        _process = new Process { StartInfo = info };
        _process.OutputDataReceived += (sender, e) => { if (e.Data != null) _lines.Enqueue(e.Data); };
        _process.ErrorDataReceived += (sender, e) => { if (e.Data != null) _lines.Enqueue(e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public bool HasExited { get { return _process.HasExited; } }

    /// <summary>Keeps the process to the logical processors of <paramref name="mask"/>.</summary>
    public void Confine(long mask) { _process.ProcessorAffinity = new IntPtr(mask); }

    /// <summary>Ends the process and every process it started.</summary>
    public void Kill() { try { _process.Kill(true); } catch (InvalidOperationException) { } }

    /// <summary>The exit code, once the process has ended and all its output has been read.</summary>
    public int Finish()
    {
        _process.WaitForExit();
        return _process.ExitCode;
    }

    /// <summary>The lines that have come since the last call.</summary>
    public string[] Take()
    {
        var lines = new List<string>();
        string line;
        while (_lines.TryDequeue(out line)) lines.Add(line);
        return lines.ToArray();
    }

    public void Dispose() { _process.Dispose(); }
}
'@ }

$queue = [System.Collections.Generic.Queue[object]]::new()
$index = 0
foreach ($unit in $ordered) { $queue.Enqueue([pscustomobject]@{ Unit = $unit; Attempt = 1; Index = ++$index; Flaked = $false }) }
$running = [System.Collections.Generic.List[object]]::new()
$ran = 0
$failed = [System.Collections.Generic.List[string]]::new()
$failCode = 0
$took = [ordered]@{}
$notes = @{}
$aceWroteNoMessage = '^System\.Data\.OleDb\.OleDbException : (\s|$)'

function Start-Unit($entry) {
    $unit = $entry.Unit
    # Short, because the tests' own folders and files go under it and ACE refuses a path longer than 260 characters.
    $temp = Join-Path ([IO.Path]::GetTempPath()) "jat-$PID-$($entry.Index)-$($entry.Attempt)"
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $temp | Out-Null
    $report = Join-Path $results ([IO.Path]::GetFileNameWithoutExtension($TrxName) + "-$($unit.Name)")
    Remove-Item -LiteralPath "$report.xml" -ErrorAction SilentlyContinue
    $arguments = @($unit.Filter) + @('-noLogo', '-noColor', '-preEnumerateTheories', '-reporter', 'verbose', '-result-xml', "$report.xml", '-result-trx', "$report.trx")
    Write-Host "[$($unit.Name)] started: $($unit.Tests.Count) tests$(if ($entry.Attempt -gt 1) { ", attempt $($entry.Attempt)" })"
    $slot = @(0..($Parallel - 1) | Where-Object { $s = $_; -not ($running | Where-Object Slot -eq $s) })[0]
    $process = [AccessTestProcess]::new($exe, [string[]]$arguments, $root, $temp)
    if ($ProcessorsEach -gt 0) {
        $count = [Environment]::ProcessorCount
        $mask = [long]0
        for ($i = 0; $i -lt $ProcessorsEach; $i++) { $mask = $mask -bor ([long]1 -shl (($slot * $ProcessorsEach + $i) % $count)) }
        $process.Confine($mask)
    }
    $running.Add([pscustomobject]@{
        Entry = $entry; Temp = $temp; Report = "$report.xml"; Output = [System.Collections.Generic.List[string]]::new(); Slot = $slot
        Clock = [Diagnostics.Stopwatch]::StartNew(); Process = $process; Limit = [math]::Max(600, 5 * (Get-Seconds $unit.Name)); Hung = $false
    })
}

function Write-Lines($run) {
    foreach ($line in $run.Process.Take()) { $run.Output.Add($line); Write-Host "[$($run.Entry.Unit.Name)] $line" }
}

# The tests of an xUnit.net XML report: how many ran, how many were skipped, and the name and failure of each that failed.
function Read-Report([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    [xml] $xml = Get-Content -LiteralPath $path -Raw
    $assembly = $xml.SelectSingleNode('/assemblies/assembly')
    $failures = @($xml.SelectNodes('//test[@result="Fail"]') | ForEach-Object {
        [pscustomobject]@{ Name = $_.GetAttribute('name'); Type = $_.SelectSingleNode('failure').GetAttribute('exception-type'); Message = $_.SelectSingleNode('failure/message').InnerText }
    })
    [pscustomobject]@{ Total = [int]$assembly.GetAttribute('total'); Skipped = [int]$assembly.GetAttribute('skipped'); Errors = [int]$assembly.GetAttribute('errors'); Failures = $failures }
}

$skipped = 0
while ($queue.Count -or $running.Count) {
    while ($queue.Count -and $running.Count -lt $Parallel) { Start-Unit $queue.Dequeue() }
    Start-Sleep -Milliseconds 250
    foreach ($run in @($running)) {
        Write-Lines $run
        if (-not $run.Process.HasExited) {
            if ($run.Clock.Elapsed.TotalSeconds -lt $run.Limit) { continue }
            Write-Host "[$($run.Entry.Unit.Name)] has run $([math]::Round($run.Clock.Elapsed.TotalSeconds)) s, past its limit of $($run.Limit) s: ended."
            $run.Hung = $true
            $run.Process.Kill()
        }
        $code = $run.Process.Finish()
        Write-Lines $run
        $run.Process.Dispose()
        $running.Remove($run) | Out-Null
        $entry = $run.Entry
        $unit = $entry.Unit
        $output = $run.Output
        # A process that died left its databases behind, and the Access engine its own temporary files; a later process
        # then found no room ("not enough temporary storage space on the disk"). Each process's are in its own folder.
        Remove-Item -LiteralPath $run.Temp -Recurse -Force -ErrorAction SilentlyContinue
        $free = [math]::Round((New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($run.Temp))).AvailableFreeSpace / 1GB, 1)
        $elapsed = [math]::Round($run.Clock.Elapsed.TotalSeconds)
        $took[$unit.Name] = $elapsed + $(if ($took.Contains($unit.Name)) { $took[$unit.Name] } else { 0 })
        $report = Read-Report $run.Report

        if ($code -eq 0 -and $report -and -not $report.Errors) {
            $ran += $report.Total
            $skipped += $report.Skipped
            Write-Host "[$($unit.Name)] ran $($report.Total) tests ($($report.Skipped) skipped) in $elapsed s; $free GB free."
            if ($report.Total -ne $unit.Tests.Count) {
                Write-Host "::error::$($unit.Name) ran $($report.Total) tests where $($unit.Tests.Count) were listed for it."
                $failed.Add($unit.Name); if (-not $failCode) { $failCode = 1 }
            }
            continue
        }

        $retry = $null
        if ($run.Hung) {
            $retry = "hung, and was ended after $elapsed s"
            $limit = $attempts
        } elseif ($code -eq $crashedInAce) {
            $retry = "died inside the Access Database Engine (exit $code)"
            $limit = $attempts
        } elseif ($code -eq $crashedInOleDb) {
            # The same engine's other death: an access violation inside OleDbConnection.Open, in the provider's own
            # code, which .NET Framework raises as an exception no test can catch and ends the process on. Only that is
            # run again: that exception, raised under System.Data.OleDb, with no test failed. A test that failed before
            # the process died is on a line of its own as it ran, its name and then [FAIL].
            $text = $output -join "`n"
            if ($text -match 'Unhandled Exception:[^\n]*System\.AccessViolationException[^\n]*\n\s*at System\.Data\.OleDb\.' -and
                -not ($output | Where-Object { $_ -match '\[FAIL\]' })) {
                $retry = "died of an access violation in OleDbConnection.Open (exit $code)"
                $limit = $attempts
            }
        } elseif ($code -eq 1 -and $report -and -not $report.Errors -and $report.Failures.Count -and -not $entry.Flaked -and
                  -not ($report.Failures | Where-Object { $_.Type -ne 'System.Data.OleDb.OleDbException' -or $_.Message -notmatch $aceWroteNoMessage })) {
            $retry = "failed only where ACE raised an OleDbException with no message of its own: $(($report.Failures | ForEach-Object { "$($_.Name) - $($_.Message)" }) -join '; ')"
            $limit = $entry.Attempt + 1
            $entry.Flaked = $true
        }
        if ($retry -and $entry.Attempt -lt $limit) {
            Write-Host "::warning::The test process of $($unit.Name) $retry. Running it again: attempt $($entry.Attempt + 1)."
            $notes[$unit.Name] = (@($notes[$unit.Name], "run again: $retry") | Where-Object { $_ }) -join '; '
            $queue.Enqueue([pscustomobject]@{ Unit = $unit; Attempt = $entry.Attempt + 1; Index = $entry.Index; Flaked = $entry.Flaked })
            continue
        }
        if ($retry) { Write-Host "::error::The test process of $($unit.Name) $retry, $($entry.Attempt) times running." }
        else { Write-Host "::error::$($unit.Name) failed (exit $code) after $elapsed s; $free GB free." }
        $notes[$unit.Name] = (@($notes[$unit.Name], "failed (exit $code)") | Where-Object { $_ }) -join '; '
        $failed.Add($unit.Name)
        if (-not $failCode) { $failCode = if ($code) { $code } else { 1 } }
    }
}

# What each process took, in the log and in the job's summary beside the table's number.
Write-Host "Seconds each process took: $(($took.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key) = $($_.Value)" }) -join '; ')"
$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("### Access referee tests, shard $Shard of $Shards (the $(if ($full) { 'full sweeps' } else { 'samples' }), $Parallel at a time)")
$summary.Add('')
$summary.Add('| Piece | Tests | Seconds | In the table | |')
$summary.Add('|---|---|---|---|---|')
foreach ($unit in ($ordered | Sort-Object { if ($took.Contains($_.Name)) { $took[$_.Name] } else { 0 } } -Descending)) {
    $measured = if ($took.Contains($unit.Name)) { $took[$unit.Name] } else { '' }
    $number = if ($seconds.ContainsKey($unit.Name)) { $seconds[$unit.Name] } else { 'none' }
    $note = if ($notes.ContainsKey($unit.Name)) { $notes[$unit.Name] } else { '' }
    if ($Shards -gt 1 -and $measured -ne '' -and $number -ne 'none' -and $measured -gt 2 * $number + 30) {
        Write-Host "::warning::$($unit.Name) took $measured s, and scripts/Invoke-AccessTests.ps1 has $number s ($mode). Update the number, or the shards will not be balanced."
        $note = (@($note, 'slower than its number') | Where-Object { $_ }) -join '; '
    }
    $summary.Add("| $($unit.Name) | $($unit.Tests.Count) | $measured | $number | $note |")
}
if ($env:GITHUB_STEP_SUMMARY) { ($summary + '') | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }

if ($failed.Count) {
    Write-Host "::error::$($failed.Count) of $($ordered.Count) processes failed: $($failed -join ', ')"
    exit $failCode
}
if ($ran -ne $expected) {
    Write-Host "::error::$expected tests were listed and $ran ran."
    exit 1
}
Write-Host "All $ran tests ran, $skipped of them skipped, in $($ordered.Count) processes."
exit 0
