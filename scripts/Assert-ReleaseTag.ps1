<#
.SYNOPSIS
    Refuses a release tag that must not publish.

.DESCRIPTION
    A tag push publishes a package, so the release workflow runs this before anything is
    packed or published. It throws for a tag that is:

      - not a release tag: <prefix>X.Y.Z, with a -suffix only where prereleases are allowed
        (the prefix is case-sensitive, and a number has no leading zero);
      - not a commit on the default branch's own line (its first-parent history): a tag on a
        branch, or on a commit inside a merged pull request, would publish an unreviewed tree;
      - not above every release tagged so far on the default branch: a release is never re-cut
        or back-dated, so a tag on an older commit cannot publish an old major, or any lower
        version, over what consumers already take;
      - below the MinimumReleaseVersion the project declares - the floor a breaking change
        raises in the same change, so a breaking change cannot ship as a minor or patch
        update of the previous major;
      - already released: a tag that has a GitHub Release was published, and a release is
        never re-cut or re-pointed. A run that pushed the package but failed before the
        release existed can be re-run.

    What runs on a tag is the workflow at the tagged commit, so this protects every commit
    from the one that added it; a commit older than that keeps whatever check it had. A
    refused tag publishes nothing; delete it, fix what it tripped over, and tag again.

    Needs the full history and every tag (actions/checkout with fetch-depth: 0), and gh with a
    token that can read the repository's releases (GH_TOKEN) for the last check. On success it
    prints the version the tag names - the tag without its prefix - and leaves the exit code at
    0, which the runner's step wrapper reads.

.PARAMETER Tag
    The tag being built, e.g. v4.1.0.
.PARAMETER Sha
    The commit the tag points at.
.PARAMETER DefaultBranch
    The branch a release must be on. main by default.
.PARAMETER TagPrefix
    What precedes the version in a release tag. v by default.
.PARAMETER AllowPrerelease
    Accept <prefix>X.Y.Z-suffix too. A prerelease must still be above every release so far.
.PARAMETER FloorFile
    The MSBuild file (a csproj or Directory.Build.props) that declares MinimumReleaseVersion,
    exactly once, as a plain X.Y.Z.
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Sha,
    [string]$DefaultBranch = 'main',
    [string]$TagPrefix = 'v',
    [switch]$AllowPrerelease,
    [Parameter(Mandatory)] [string]$FloorFile
)

$ErrorActionPreference = 'Stop'
# Defensive: the native exit codes below are read by hand. The preference is off by default,
# and a profile that turned it on would make git's "no" a terminating error of its own.
$PSNativeCommandUseErrorActionPreference = $false

$prefix = [regex]::Escape($TagPrefix)
$number = '(0|[1-9]\d*)'
$suffix = if ($AllowPrerelease) { '(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?' } else { '' }
if ($Tag -cnotmatch "^$prefix$number\.$number\.$number$suffix$") {
    $shape = if ($AllowPrerelease) { "${TagPrefix}X.Y.Z or ${TagPrefix}X.Y.Z-suffix" } else { "${TagPrefix}X.Y.Z" }
    throw "Refused: '$Tag' is not a release tag like $shape (the prefix is case-sensitive, and a number has no leading zero)."
}
$core = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"

$ownLine = @(git rev-list --first-parent "origin/$DefaultBranch")
if ($ownLine -cnotcontains $Sha) {
    throw "Refused: '$Tag' is not on $DefaultBranch's own line ($Sha). A release is cut from the merge commit on $DefaultBranch after its change merged, not from a branch or from a commit inside a merged pull request."
}

$highest = git tag --merged "origin/$DefaultBranch" --list "$TagPrefix*" |
    ForEach-Object { if ($_ -cne $Tag -and $_ -cmatch "^$prefix$number\.$number\.$number$") { [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" } } |
    Sort-Object | Select-Object -Last 1
if ($highest -and $core -le $highest) {
    throw "Refused: '$Tag' is not above the latest release, ${TagPrefix}$highest. A release is never re-cut or back-dated: tag the next version on $DefaultBranch instead."
}

$floors = @(([xml](Get-Content -LiteralPath $FloorFile -Raw)).Project.PropertyGroup |
    ForEach-Object { $_.MinimumReleaseVersion } |
    ForEach-Object { if ($_ -is [System.Xml.XmlElement]) { $_.InnerText } else { $_ } } |
    Where-Object { $_ })
if ($floors.Count -ne 1) {
    throw "Refused: $FloorFile must declare MinimumReleaseVersion exactly once (found $($floors.Count)). It is the floor a release is checked against, raised by a breaking change, and no release is cut without it."
}
$floorText = $floors[0].Trim()
if ($floorText -notmatch '^\d+\.\d+\.\d+$') {
    throw "Refused: MinimumReleaseVersion '$floorText' in $FloorFile is not a plain X.Y.Z."
}
$floor = [version]$floorText
if ($core -lt $floor) {
    throw "Refused: '$Tag' is below MinimumReleaseVersion $floor. $DefaultBranch carries a breaking change, so the next release is $floor or later: tag that, or lower the floor only with the change that justifies it."
}

$existing = gh release view $Tag --json tagName 2>&1 | Out-String
if ($LASTEXITCODE -eq 0) {
    throw "Refused: '$Tag' already has a GitHub Release, so it was published. A release is never re-cut or re-pointed: tag the next version on $DefaultBranch instead."
}
if ($existing -notmatch 'release not found') {
    throw "Could not check whether '$Tag' is already released (gh release view): $($existing.Trim())"
}

# The runner's step wrapper exits with the last native exit code, and gh's "not found" above
# left it at 1: a tag that passed every check ends at 0.
$global:LASTEXITCODE = 0
$Tag.Substring($TagPrefix.Length)
