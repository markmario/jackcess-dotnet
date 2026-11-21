#Requires -Version 7.0
# TRIAL for #145, removed before the pull request is ready: runs CursorsAndRowsInAccessTests.UEngine -Times times,
# -Parallel at a time, each in a process of its own, with JACKCESS_DAO_TRIAL=-Trial, and counts how each ended.
param([string] $Trial = 'baseline', [int] $Times = 30, [int] $Parallel = 3, [switch] $X86)
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "tests\JackcessDotNet.AccessTests\bin\$(if ($X86) { 'Release-x86' } else { 'Release' })\net48\JackcessDotNet.AccessTests.exe"
$method = 'JackcessDotNet.AccessTests.CursorsAndRowsInAccessTests.rows_the_engine_updates_where_they_are_and_moves_off_their_page_are_rows_access_reads_finds_updates_and_deletes'
$out = Join-Path $root 'TestResults'
New-Item -ItemType Directory -Force $out | Out-Null
$running = @()
$results = [System.Collections.Generic.List[object]]::new()
$next = 1
while ($next -le $Times -or $running.Count) {
    while ($next -le $Times -and $running.Count -lt $Parallel) {
        $temp = Join-Path ([IO.Path]::GetTempPath()) "jat-trial-$next"
        New-Item -ItemType Directory -Force $temp | Out-Null
        $log = Join-Path $out "trial-$Trial-$next.log"
        $info = [Diagnostics.ProcessStartInfo]::new($exe)
        foreach ($a in @('-method', $method, '-displayName', '*accessMade: False)', '-noLogo', '-noColor', '-preEnumerateTheories')) { $info.ArgumentList.Add($a) }
        $info.UseShellExecute = $false; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $info.Environment['TEMP'] = $temp; $info.Environment['TMP'] = $temp; $info.Environment['JACKCESS_DAO_TRIAL'] = $Trial
        $p = [Diagnostics.Process]::Start($info)
        $running += [pscustomobject]@{ N = $next; P = $p; Log = $log; Temp = $temp; Out = $p.StandardOutput.ReadToEndAsync(); Err = $p.StandardError.ReadToEndAsync(); Clock = [Diagnostics.Stopwatch]::StartNew() }
        $next++
    }
    Start-Sleep -Milliseconds 500
    foreach ($r in @($running)) {
        if (-not $r.P.HasExited) {
            if ($r.Clock.Elapsed.TotalMinutes -lt 5) { continue }
            $r.P.Kill($true); $r.P.WaitForExit()
            $kind = 'hung'
        } else {
            $r.P.WaitForExit()
            $text = $r.Out.Result + $r.Err.Result
            Set-Content -LiteralPath $r.Log -Value $text
            $code = $r.P.ExitCode
            $kind = if ($code -eq 0) { 'passed' } elseif ($text -match 'RPC_E_SERVERFAULT') { 'RPC_E_SERVERFAULT' } elseif ($text -match '\[FAIL\]') { "failed (exit $code)" } else { "died (exit $code)" }
        }
        $results.Add([pscustomobject]@{ N = $r.N; Kind = $kind; Seconds = [math]::Round($r.Clock.Elapsed.TotalSeconds) })
        Write-Host "[$Trial $($r.N)] $kind after $([math]::Round($r.Clock.Elapsed.TotalSeconds)) s"
        Remove-Item -LiteralPath $r.Temp -Recurse -Force -ErrorAction SilentlyContinue
        $running = @($running | Where-Object { $_ -ne $r })
    }
}
$summary = ($results | Group-Object Kind | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' '
Write-Host "TRIAL $Trial, $Times runs, $Parallel at a time: $summary"
if ($env:GITHUB_STEP_SUMMARY) { "### Trial $Trial`n`n$summary`n" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
exit 0
