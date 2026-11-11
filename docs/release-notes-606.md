# SDK updates: tracks, companions, readiness, one channel feed, PHP/Ruby/Rust (#606)

**Packages:** none. The change is in `apps/CodeCoverage` (deployed from master to coverage.mintplayer.com) and in the
dependency action (`apps/CodeCoverage/dependency-action`), which repositories consume by its pinned commit.

The SDK updater already existed as the platform groups of the rolling dependency pull request (#419). #606 fills its
gaps. Decisions and evidence: [code-coverage/sdk-updates-PRD.md](code-coverage/sdk-updates-PRD.md) (§9 decision log,
§10 implementation log). User guide: [code-coverage/dependency-updates.md](code-coverage/dependency-updates.md) §4–§6.

---

## Breaking changes

1. **The lookup workflow needs the `platforms` input.** The bot now passes its channel table to `dependency-lookup.yml`
   as a required input, and the action no longer fetches release feeds itself. A lookup workflow from before #606 ends
   the run as `workflow-outdated`. Copy the current template and its action pin into each repository. There is no
   fallback.
2. **Previews follow `track`, not the old implicit rule.** Previously a preview was followed only when the repository
   was already on one. Now `platforms.<sdk>.track` (`lts | stable | preview`, default `stable`) decides. A repository
   already on a newer phase is still never moved back.
3. **One `PlatformRelease` row per channel** (`PlatformReleases/dotnet/10.0`, `PlatformReleases/node/24`). The per-major
   ids and the `nodejs` platform name are deleted by the next daily collection.

## New

4. **Tracks** for SDKs and package rules (`stable | preview`; `lts` only for .NET, Node.js and Java), as an account rule
   default on `platform:<sdk>` with a per-repository override. An enforced account rule is the widest track allowed.
5. **Companions:** `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore*`,
   `Microsoft.NET.*` and `System.*` move with .NET's major, and `@types/node` moves with Node's, in the same group.
   `platforms.<sdk>.companions` overrides the list.
6. **Readiness:** an SDK move waits (*pending*) while a companion has no release at the target major. This is shown in
   the pull request and in the account's platform readiness. `approve platform:<sdk> <channel> --force` takes it anyway.
7. **PHP, Ruby and Rust** SDK moves. Ranges (`require.php`, Gemfile ranges, `required_ruby_version`, `rust-version`,
   `requires-python`) are reported and never edited.
8. **More locations move with the SDK:**
   - workflow `container:`, `image:` and `uses: docker://`;
   - matrix entries on the old channel.

   MSBuild conditions that test the old TFM are reported.
9. **Producers without release lines** get a warning in the platform group: the previous platform's consumers stop
   receiving releases.
10. **The repository panel lists each SDK** with its next stable and preview channel.

## Fixed

11. **Framework preview majors were never collected.** The collector asked npm for `/-/package/<package>` without
    `/dist-tags`, and npm answers that with 404 (checked 2026-11-09). As a result, no Angular/React/Vue preview major
    reached the platform list after #568 M3.
12. **PHP previews.** The reader required `"enabled": true`, which php.net never writes on published release candidates.
    The collector also uses php.net's current `pre-release-builds.php` URL, because the old one redirects.
