# Releasing

How a release is made: the tag is the version, and the release workflow checks the tag, builds, tests and checks
the package before it publishes it to nuget.org. How to build and test locally is in
[Building and testing](building-and-testing.md).

## Tagging a release

A release is a `vX.Y.Z` tag on `main`, and the tag is the version: there is no version to edit and no bump
commit. Merge, then tag the merge commit and push the tag. `.github/workflows/publish.yml` then:

1. **Checks the tag** (`scripts/Assert-ReleaseTag.ps1`) before building anything: `vX.Y.Z`, or `vX.Y.Z-suffix`
   for a [pre-release](#pre-releases), on `main`'s own line (the merge commit, not a commit inside a pull
   request), its `X.Y.Z` above every release so far and at or above `MinimumReleaseVersion` in the project file,
   and not already released. A refused tag publishes nothing; delete it, fix what it tripped over, and tag again.
2. **Builds and tests** the tagged commit, packs it at that version, and checks what ships: the ID, the version
   and the commit it names, the four frameworks, `LICENSE`, `NOTICE`, `README.md`, `CHANGELOG.md` and the icon,
   that no assembly is strong-named, and the assembly, file and product versions of each DLL.
3. **Waits for approval** of the `nuget` environment, then pushes the package and its symbols to nuget.org
   through [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) - there is no
   API key in the repository - and makes a GitHub Release.

Every other run of that workflow - a pull request, a push to `main`, a run by hand - rehearses the tag check
against the next version and against a pre-release of it, and packs and checks the package at
`<next version>-ci.<run>`; it publishes nothing, and leaves the tests to `ci.yml`.

## Pre-releases

A tag with a suffix, `vX.Y.Z-suffix`, publishes a pre-release of the version it names: `v2.0.0-preview.1`, then
`v2.0.0-preview.2`, and later `v2.0.0-rc.1`. It goes through the same checks, the same approval and the same
workflow as a release. nuget.org lists it as a pre-release, which a project takes only by naming the version or
asking for pre-releases, and its GitHub Release is marked a pre-release.

- Its `X.Y.Z` is the release it leads up to, so it must be above every release so far: once `v2.0.0` is tagged,
  `v2.0.0-preview.3` is refused, and the next pre-release is of `2.0.1` or later.
- The check does not order pre-releases among themselves. NuGet compares the suffix part by part: numbers as
  numbers, words alphabetically, so `preview.2` is above `preview.1` and `rc.1` is above every `preview`.
- A published version is never replaced, a pre-release included: tag the next one.

## Before tagging

Before a release, move what `src/JackcessDotNet/PublicAPI.Unshipped.txt` lists into `PublicAPI.Shipped.txt`, and
give `CHANGELOG.md`'s Unreleased section the version. A pre-release needs neither: it is a snapshot of `main`
on the way to the release, with the public API still open to change. A change that removes or changes public API
raises `MinimumReleaseVersion` to the next major in the same pull request.

`README.md` is the package's readme, which nuget.org shows, and a relative link does not resolve there: its links
are absolute (`https://github.com/markmario/jackcess-dotnet/blob/main/...`). The pages under `docs/` are not in
the package, and link to each other relatively.

## The retired workflow

**`.github/workflows/nuget-publish.yml` stays disabled** in the repository's Actions settings: never enable it,
never delete it, and never put a workflow back at that path. A tag runs the workflow of the commit it points at,
and every commit before publish.yml carries upstream's release job there, which published any `v*` tag with no
check. Disabling the workflow at that path stops it on every commit, old ones included. publish.yml's
`retired-workflow` job fails while it is enabled, and the publish job waits on it.

[Back to contents](README.md)
