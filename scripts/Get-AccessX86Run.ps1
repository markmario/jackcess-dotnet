#Requires -Version 7.0
# Says whether a CI run has the 32-bit Access referee shards (.github/workflows/ci.yml, access-build and access-tests),
# and why: x86=true or x86=false, to $env:GITHUB_OUTPUT when there is one, and a line to the log.
#
# They take two jobs of about seven minutes, so a pull request has them only when it changes what they exercise:
#
#   - a file under tests/JackcessDotNet.AccessTests/, the tests themselves;
#   - scripts/Invoke-AccessTests.ps1, which runs them, and this script, which says when;
#   - ci.yml's access-build and access-tests jobs, compared as text, from the line that starts access-build to the job
#     after access-tests, without lines that are only a comment - a change elsewhere in ci.yml (the framework jobs,
#     say), or to a comment, does not count;
#
# or when it has the ci:access-x86 label. The labels are asked of GitHub as the job runs, not read from the event that
# started the run: `gh pr create --label` adds the label after the pull request is opened, so the event of its first run
# may not carry it (#150's did not), and a run made again after labelling sees the label too. A label added to an open
# pull request starts no run of its own: push, or run the last run again. A push to main, the nightly run and a run by
# hand always have them. A change
# under src/ alone does not bring them: nearly every pull request changes src/, and main's run, which has them, is the
# net for what such a change does in 32 bits.
#
# Asking GitHub can fail. Then the run has them, with a warning: a run longer than it needed to be, rather than a
# change that went unrefereed.
#
#   ./scripts/Get-AccessX86Run.ps1 -Event pull_request -Repository markmario/jackcess-dotnet -PullRequest 146 `
#       -BaseSha <sha> -HeadSha <sha>
param(
    [Parameter(Mandatory)] [string] $Event,
    [string] $Labelled = 'false',
    [string] $Repository,
    [string] $PullRequest,
    [string] $BaseSha,
    [string] $HeadSha
)
$ErrorActionPreference = 'Stop'

function Write-Answer([bool] $x86, [string] $why) {
    Write-Host "The 32-bit Access shards: $(if ($x86) { 'yes' } else { 'no' }), $why."
    if ($env:GITHUB_OUTPUT) { "x86=$($x86.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    exit 0
}

if ($Event -ne 'pull_request') { Write-Answer $true "on a $Event" }
if ($Labelled -eq 'true') { Write-Answer $true 'the pull request has the ci:access-x86 label' }
try {
    $labels = @(& gh api "repos/$Repository/issues/$PullRequest/labels" --jq '.[].name')
    if ($LASTEXITCODE) { throw "gh api ... /issues/$PullRequest/labels ended with $LASTEXITCODE" }
    if ($labels -contains 'ci:access-x86') { Write-Answer $true 'the pull request has the ci:access-x86 label (added after the event that started this run)' }
} catch {
    Write-Host "::warning::The pull request's labels could not be asked of GitHub ($($_.Exception.Message)), so this run has the 32-bit Access shards."
    Write-Answer $true 'as GitHub could not be asked'
}

# ci.yml's Access jobs: from access-build's line to the line of the job after access-tests.
function Get-AccessJobs([string] $text) {
    $lines = $text -split '\r?\n'
    $start = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -eq '  access-build:' })
    $tests = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -eq '  access-tests:' })
    if ($start -lt 0 -or $tests -lt 0) { return $null }
    $end = $lines.Length
    for ($i = $tests + 1; $i -lt $lines.Length; $i++) { if ($lines[$i] -match '^  [A-Za-z][\w-]*:\s*$') { $end = $i; break } }
    # Without lines that are only a comment, or empty: the comments at the end are the next job's, and a comment
    # changes nothing a run does.
    @($lines[$start..($end - 1)] | Where-Object { $_ -notmatch '^\s*(#.*)?$' }) -join "`n"
}

try {
    $files = @(& gh api "repos/$Repository/pulls/$PullRequest/files" --paginate --jq '.[].filename')
    if ($LASTEXITCODE) { throw "gh api ... /pulls/$PullRequest/files ended with $LASTEXITCODE" }
    $touched = @($files | Where-Object { $_.StartsWith('tests/JackcessDotNet.AccessTests/') -or $_ -in 'scripts/Invoke-AccessTests.ps1', 'scripts/Get-AccessX86Run.ps1' })
    if ($touched.Count) { Write-Answer $true "the pull request changes $(($touched | Select-Object -First 3) -join ', ')$(if ($touched.Count -gt 3) { ", and $($touched.Count - 3) more" })" }
    if ($files -contains '.github/workflows/ci.yml') {
        # Against where the branch left the base, as the list of files is: what main gained since is not this change.
        $since = & gh api "repos/$Repository/compare/$BaseSha...$HeadSha" --jq '.merge_base_commit.sha'
        if ($LASTEXITCODE -or -not $since) { throw "the merge base of $BaseSha and $HeadSha could not be found ($LASTEXITCODE)" }
        $before = & gh api "repos/$Repository/contents/.github/workflows/ci.yml?ref=$since" -H 'Accept: application/vnd.github.raw'
        if ($LASTEXITCODE) { throw "the base's ci.yml could not be read ($LASTEXITCODE)" }
        $after = & gh api "repos/$Repository/contents/.github/workflows/ci.yml?ref=$HeadSha" -H 'Accept: application/vnd.github.raw'
        if ($LASTEXITCODE) { throw "the head's ci.yml could not be read ($LASTEXITCODE)" }
        $was = Get-AccessJobs ($before -join "`n")
        $is = Get-AccessJobs ($after -join "`n")
        if ($null -eq $was -or $null -eq $is -or $was -ne $is) { Write-Answer $true "the pull request changes ci.yml's Access jobs" }
    }
    Write-Answer $false "the pull request changes none of what they run ($($files.Count) files; the ci:access-x86 label asks for them)"
} catch {
    Write-Host "::warning::Whether the pull request changes the Access tests could not be asked of GitHub ($($_.Exception.Message)), so this run has the 32-bit Access shards."
    Write-Answer $true 'as GitHub could not be asked'
}
