# Package versioning in Docker builds (#565): PRD and plan

Issue: https://github.com/MintPlayer/MintPlayer.Spark/issues/565, "package-versioning shouldn't happen during docker builds".
Status: **implemented (option A + guard test), 2026-11-05**, on `fix/issue565-docker-versioning-skip`. See §8 for the evidence.

## 1. Problem

Every image build logs MPV003 warnings like this one:

```
MintPlayer.Versioning.MSBuild.targets(41,3): warning MPV003: git tag failed (fatal: not a git repository
(or any of the parent directories): .git); the version stays 0.0.0-PLACEHOLDER. [.../MintPlayer.Spark.Authorization.csproj]
```

**Measured on run 41959409302**, job 127021920683 ("Build and push the MintPlayer image", `mintplayer-deploy.yml`):
- The job **passed**. It logged **38 MPV003 lines and no other MPV code**: 19 `libs/**` projects, each warned twice.
- The first warning comes from `dotnet restore` (`apps/MintPlayer/MintPlayer/Dockerfile:57`), the second from
  `dotnet publish` (`:71`).

The warning is noise: it reports a missing git repository that the image build excludes on purpose. It also spends 19 × 2
`git` process launches that can only fail.

## 2. Why it happens (evidence)

| Fact | Where |
|---|---|
| The versioning targets are imported **from source** for every project | `Directory.Build.targets:262` |
| `.dockerignore` excludes `**/.git`, so the build context has no repository | `.dockerignore` |
| `COPY . .` brings `Directory.Build.props/targets` into the image, so `publish` runs the target | every .NET Dockerfile |
| The target runs for every project that isn't `IsPackable=false` (the libs). The apps and tests are skipped | `MintPlayer.Versioning.MSBuild.targets:34` |
| The task starts at `0.0.0-PLACEHOLDER`, then runs `git tag --merged HEAD`. Both "git could not be run" and a non-zero exit are MPV003 **warnings** that return `true` | `MintPlayer.Versioning.MSBuild.cs:60, 89-103` |
| A global `Version`/`PackageVersion` returns before git is run (#544 D1) | `MintPlayer.Versioning.MSBuild.cs:63-78` |
| The only opt-out is `MintPlayerVersioningSkip=true`. The task reads no other environment signal than `GITHUB_ACTIONS`/`GITHUB_EVENT_NAME` | `.targets:12, 34` |
| No `TreatWarningsAsErrors` in the repo, only `WarningsAsErrors;RS0030`, so MPV003 can't fail a build today | `Directory.Build.targets:229` |
| MPV004/MPV005 (placeholder pack) are errors, but only on `pack`, which no Dockerfile runs | `.targets` guard targets |

**Dockerfiles affected.** They all use the same pattern: restore the csproj, `COPY . .`, then `dotnet publish -c $BUILD_CONFIGURATION
/p:UseAppHost=false` (some add `/p:EnableSpaBuilder=false`). None passes a version.

| Dockerfile | restore | publish | workflow(s) |
|---|---|---|---|
| `apps/CodeCoverage/CodeCoverage/Dockerfile` | :55 | :79 | code-coverage-deploy, image-check |
| `apps/CodeCoverage/CodeCoverage.GitMirror/Dockerfile` | :32 | :38 | idem |
| `apps/CodeCoverage/CodeCoverage.GitWriter/Dockerfile` | :51 | :57 | idem |
| `apps/DemoApp/DemoApp/Dockerfile` | :41 | :57 | demo-images-check |
| `apps/MintPlayer/MintPlayer/Dockerfile` | :57 | :71 | mintplayer-deploy, image-check |
| `apps/SeoTester/SeoTester/Dockerfile` | :52 | :66 | seotester-deploy, image-check |
| `apps/SeoTester/SeoTester.Fetcher/Dockerfile` | :22 | :28 | idem |

`apps/SparkSchemas`, `apps/MintPlayer/opensearch` and `apps/SeoTester/chromium` contain no .NET and are not affected.

**What the images report as a version today.** The libs inside every image are `0.0.0-PLACEHOLDER`. The only runtime
reader found is the MCP server's `ServerInfo.Version`, which comes from `AssemblyInformationalVersion`
(`libs/mcp/MintPlayer.Spark.Mcp/SparkMcpExtensions.cs:74`). In production it reports `0.0.0-PLACEHOLDER`.
`CodeCoverage.Versioning/Versions/VersionTag.cs:118` also reads an assembly version, but that project is `IsPackable=false`, so the target never
touches it. There is no version endpoint, footer or OpenTelemetry `service.version`.

**Docs.** No versioning document mentions Docker or builds without `.git`. PRD D1 accepted "outside a tagged commit you get
the last tag or PLACEHOLDER" for *CI/local* builds. D9 defines `0.0.0-untagged` vs PLACEHOLDER. Neither decided anything
for a context with no repository.

## 3. Goal / non-goals

**Goals**
- G1. An image build emits **no MPV00x warning** and launches no `git` for versioning.
- G2. The packing safety net stays intact: a `pack` with no tags still fails (MPV004/MPV005). A CI or local build inside
  a real checkout still warns when git really fails.
- G3. The image output does not change, apart from optional version stamping (§5 Q3).

**Non-goals**
- Putting `.git` into the build context. It would bust the layer cache on every commit and enlarge the context.
- Changing how packages are versioned or published (#153, #544, #552).

## 4. Options

| | Option | Where the change lives | Consumer repos affected | Notes |
|---|---|---|---|---|
| A | **Dockerfiles opt out**: `ENV MintPlayerVersioningSkip=true` in each .NET build stage. MSBuild reads environment variables as properties, so it covers restore and publish | 7 Dockerfiles | no | Explicit and scoped. Each new Dockerfile must remember it, and a guard test could enforce that (S4) |
| B | **The targets detect "no git work tree"**: a target-level condition (no `.git` dir/file found walking up from the project) skips the task, or the task downgrades MPV003 to a low-importance message in that case | `.targets` / `.cs` → ships in the next `MintPlayer.Versioning.MSBuild` release | **yes**, the four onboarded repos (D17) get it through `build/` and `buildMultiTargeting/` | Fixes everyone's images with no Dockerfile edit. The risk is hiding a real misconfiguration, but MPV004/MPV005 still stop a placeholder pack |
| C | Skip when `DOTNET_RUNNING_IN_CONTAINER=true` (set by the official SDK images) | `.targets` | yes | **Rejected**: a dev container with the repo mounted *has* `.git` and should version normally |
| D | **Stamp the image**: `ARG VERSION` + `-p:Version=$VERSION` (or `-p:InformationalVersion`) from the workflow, e.g. the commit sha | Dockerfiles + deploy workflows | no | An explicit version already skips git (#544 D1), so this fixes G1 *and* gives the MCP `ServerInfo` a meaningful value. `-p:Version` applies to every project in the publish, which is fine for an app image |

**Recommendation: A, or D if the owner wants images stamped (Q3).** Both stay inside this repository, leave the published package
alone and are explicit. B is the better general answer for consumer repos, but it changes a published package's
behaviour, so it needs the owner's decision (Q1) and S3's evidence first.

## 5. Open decisions (owner)

- ~~**Q1.**~~ **Decided: A only.** The fix goes into this repository's Dockerfiles. The published targets stay unchanged.
- ~~**Q2.**~~ Moot, because B is not chosen.
- ~~**Q3.**~~ **Decided: no stamping (D not chosen).** The libs in the images stay `0.0.0-PLACEHOLDER`, as they are today.
- ~~**Q4.**~~ **Decided: yes, add a guard test** (owner, 2026-11-05).

## 6. Spikes

Each spike is short, local and evidence-producing. None needs Docker, a CI push or a full test run.

- **S1: does the environment-variable opt-out reach both restore and publish?** In a `git archive HEAD` copy (no `.git`),
  run `dotnet publish apps/MintPlayer/MintPlayer/MintPlayer.csproj -c Release /p:UseAppHost=false /p:EnableSpaBuilder=false`
  once plain and once with `MintPlayerVersioningSkip=true` in the environment. Count MPV003 in each log (expect 19 → 0),
  and diff the publish folders' file lists and the libs' `AssemblyInformationalVersion` (expect identical:
  `0.0.0-PLACEHOLDER` from the csproj).
- **S2: does anything pack during publish?** Grep for `GeneratePackageOnBuild` and for a `Pack` hooked to build.
  With option A, the guard target never runs during publish. Confirm that no project relies on it there.
- ~~**S3**~~ and ~~**S5**~~ are dropped, because B and D were not chosen. They are kept below for the record.
- **S3: is there a reliable no-work-tree signal for option B?** Check walking up for `.git` (a directory, or a *file* in
  worktrees and submodules) against `git rev-parse --is-inside-work-tree`, in four places: the archive copy, an agent worktree
  (`.claude/worktrees/*`), a normal checkout and a shallow `actions/checkout`. Only a signal that is right in all four is usable.
- **S4: the guard-test shape (if Q4).** Find the existing repository guard tests (e.g. the CSRF surface and route snapshot
  tests) and decide where a "every .NET Dockerfile opts out / passes a version" test belongs.
- **S5: the version source for D (if Q3).** See what the deploy workflows have at `docker/build-push-action` time
  (`github.sha`, run number). Check that `-p:Version` with a `+metadata` or prerelease string publishes cleanly across all
  libs, including the multi-targeted ones.

## 7. Plan (after the decisions)

One PR (repo rule: one PR per task).

1. **M1: the opt-out (option A).** Add `ENV MintPlayerVersioningSkip=true` to the build stage of the 7 Dockerfiles, *before*
   the `dotnet restore` line, with a comment pointing at this document.
2. ~~**M2: the targets.**~~ Dropped (Q1 = A only).
3. **M3 (only if Q4 = yes): the guard test** from S4.
4. **M4: docs.** Record the decision with its evidence here (the before/after MPV003 counts from S1, and the image-check job log
   after the change), and note the knob in `docs/code-coverage/` deploy docs if they describe the Dockerfiles.
5. **Verify once, at the end:** the S1 repro on the final code, plus the affected versioning tests. Read the MPV count in the PR's
   image-check job logs. That job already runs on PRs, so no extra push is needed.

## 8. Decision log

| # | Decision | Evidence |
|---|---|---|
| — | Option C rejected | Dev containers carry `.git` and set `DOTNET_RUNNING_IN_CONTAINER`, so this signal is wrong for them |
| S2 | Nothing packs during publish, so the skipped guard targets lose nothing | `git grep GeneratePackageOnBuild` over every csproj, props and targets file: no hits |
| S1 | **The opt-out removes every warning and changes no output** | Publish of `MintPlayer.csproj` in a `git archive HEAD` copy (`git rev-parse` exit 128). Without the opt-out: restore logged 19 MPV003 and publish 38, the same 38 as CI run 41959409302. With `MintPlayerVersioningSkip=true` in the environment: 0 and 0. The published file lists are identical (135 files), and so are the product and file versions of all 22 `MintPlayer.Spark*.dll` (`0.0.0-PLACEHOLDER`) |
| Q4 | Guard test `DockerfileVersioningSkipTests` in `CodeCoverage.Versioning.Tests/MSBuild`. It checks every stage that runs `dotnet restore/build/publish`, in every `Dockerfile` under `apps/` (fixtures and `*.Tests` skipped), and requires the ENV to be in effect, following `FROM <stage>` inheritance. To be falsifiable, it also asserts that it found 7 Dockerfiles carrying the opt-out, and a self-test proves the parser flags a missing ENV, including in a fresh base stage | Owner, 2026-11-05 |
| Q1 | **Option A: each Dockerfile opts out with `ENV MintPlayerVersioningSkip=true`.** B (published targets) and D (stamping) are not done | Owner preference, 2026-11-05. A is explicit, stays inside this repository and leaves the published `MintPlayer.Versioning.MSBuild` package alone. S1 is to confirm 19 → 0 MPV003 with identical publish output |
