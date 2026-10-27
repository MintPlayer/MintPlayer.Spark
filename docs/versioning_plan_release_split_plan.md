# Versioning plan/release split — plan

**PRD:** [versioning_plan_release_split_PRD.md](versioning_plan_release_split_PRD.md) · **Issue:** #552 · **Parent:**
#153 · **Branch:** `feat/issue552-versioning-plan-release` · **one pull request**

## Status

- **2026-10-27:** investigated, then grilled. The first draft was too complicated (owner). Decided: D1–D9 in the
  PRD.
- **Next:** spike S1, then M1–M6.

## Spikes

| Spike | Question | Blocks | Result |
|---|---|---|---|
| S1 | Can `compile-ts-action` commit the `dotnet publish` output onto the `versioning-v1.x.y` tag commit only? If not, what does our own publish step look like? Size and cold `dotnet <dll>` time on ubuntu-latest | M5 | — |
| S2 | After merge: is ng-bootstrap's master run under 5 min with the new shape? | post-merge | — |

## Milestones

### M1 — Engine/CLI: `run --plan-only` and `push-tags` (D1–D3)

- `RecordingTagWriter : ITagWriter` records each `TagToWrite` and returns `Created`.
- `Program.cs` changes:
  - `run --plan-only` writes the plan JSON (today's response plus each tag's commit and annotation, the dist-tags
    and the D3 fingerprint).
  - `push-tags --repo --plan --out [--remote]`: the HEAD and fingerprint check, then `TagWriter.WriteAsync` of
    exactly the plan's tags.
- `TagHistoryFingerprint` (sha256 via `DotnetApiListing.Hash`, excluding the given tags).
- Tests in `CodeCoverage.Versioning.Tests/Releases`:
  - a plan writes no tag
  - push-tags writes byte-identical tags to `run` (same annotation)
  - a stale HEAD is refused
  - a foreign tag is refused
  - a re-release with its own tags present is accepted

### M2 — Action `phase` (D4, D5, D7)

- `action.yml`: the `phase` input (default `all`), the `plan` input and the `plan` output.
- `main.ts`: `plan` = today's flow with `--plan-only` and no Releases or cuts. `release` = check `GITHUB_SHA`,
  `push-tags`, Releases, cuts, `fetchTags`, `writeNpm`.
- vitest: phase routing, `all` unchanged (existing tests untouched), stale-plan surfaced as a failed step with the
  "Re-run all jobs" hint, and release re-stamping.
- Rebuild `dist` **in the main checkout** (CLAUDE.md, ncc bundles).

### M3 — Server: plan and release endpoints (D6)

- Sidecar: `/jobs/versioning/prepare` (recording writer) and `/jobs/versioning/release` (fingerprint check +
  `TagWriter`), next to `run` in `VersioningEndpoints.cs`.
- App: `POST api/versioning/plan` and `POST api/versioning/release` in `VersioningController`, sharing the claim
  checks with `run`. Seal via `IDataProtector` (purpose `CodeCoverage.Versioning.Plan.v1`). Release records the
  `VersioningRun`.
- Tests in `VersioningControllerTests`:
  - a broken seal is refused
  - another repository is refused
  - another run id is refused
  - another sha is refused
  - attempt 2 is accepted
  - a second release is idempotent

  Plus a GitWriter test for prepare → release.

### M4 — Action server client (D6, D7)

- `server.ts`: `planOnServer` / `releaseOnServer`, falling back to local on 5xx. A server release that fails
  releases the same plan locally. vitest with the existing fake server.

### M5 — Prebuilt engine (D8, per S1)

- `versioning-action-publish.yml`: `dotnet publish` onto the tag commit.
- `engine.ts`: prefer `dotnet <dll>`, else `dotnet run`.
- Record the measured per-call time in the PRD.

### M6 — Documentation

- `docs/code-coverage/versioning.md`, "The workflow step": the two-phase shape with full YAML (plan in build,
  release after tests, the plan as a job output); when `all` is enough; the stale-plan recovery ("Re-run all jobs");
  and for a free package's hand-pushed major, push the tag before the run plans (PRD D3).
- `docs/code-coverage/versioning.md`, "server or local": the plan and release endpoints and the seal.
- `apps/CodeCoverage/versioning-action/README.md`: `phase`, `plan` (input and output), and the prebuilt engine.
- This PRD: record the spike results and the measured times.
- Memory entry update.
- File the ng-bootstrap issue (D9).

### Verification (after M6, one batch)

These are the targeted classes only:
- `CodeCoverage.Versioning.Tests` `Releases/*`
- `CodeCoverage.GitWriter.Tests` `VersioningConformanceTests`
- `CodeCoverage.Tests` `VersioningControllerTests`
- the action's vitest

The PR's CI run is the sweep.

### Post-merge

- Deploy coverage.mintplayer.com (the server endpoints).
- Run `versioning-action-publish` with a **minor** bump, since the new inputs are backwards-compatible.
- S2 in ng-bootstrap through its issue.
