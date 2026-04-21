# CodeCov PR #123 — Remediation Options

Per `docs/codecov-pr123-diagnosis.md`: ~174 LOC of new production code (H-2/H-3 row-level authz, M-7 optimistic concurrency) is exercised only by `MintPlayer.Spark.E2E.Tests/Security/*`. Those tests run Fleet as an **out-of-process** `dotnet run` subprocess (see `MintPlayer.Spark.E2E.Tests/_Infrastructure/FleetTestHost.cs:262`), so coverlet in the test process captures zero coverage of `MintPlayer.Spark.dll`. That — plus the fact that CI's nx filter doesn't invoke the E2E project at all — accounts for both the patch 26.28% and the project −5.80%.

Three options below, then a recommendation.

---

## Option A — Add targeted unit tests in `MintPlayer.Spark.Tests`

The project already exists (xunit + FluentAssertions + NSubstitute + coverlet.collector) and has `InternalsVisibleTo` for `MintPlayer.Spark.Tests` (`MintPlayer.Spark/MintPlayer.Spark.csproj:37`) — so `internal sealed SparkConcurrencyException` is reachable without surface changes.

| File / method to cover | What to test | Effort |
| --- | --- | --- |
| `MintPlayer.Spark/Exceptions/SparkConcurrencyException.cs` (new, 19 LOC) | Constructor sets `ExpectedEtag`/`ActualEtag`; message shape with and without `actualEtag`. | small (~15 LOC) |
| `MintPlayer.Spark/Actions/DefaultPersistentObjectActions.cs` — `IsAllowedAsync(string, T)` default (+25 LOC, mostly doc comments) | Default returns `true` for any action/entity; subclass override is honoured. Parameterize over `"Read"`, `"Query"`, `"Edit"`, `"Delete"`, `"New"`. | small (~20 LOC) |
| `MintPlayer.Spark/Services/DatabaseAccess.cs` — `IsAllowedEntityViaActionsAsync` + `FilterByRowLevelAuthAsync` (+88 LOC) | Single-load returns null when hook denies; list filter drops denied rows, keeps allowed, handles empty and all-denied lists; projection path loads base entity. Hit via NSubstitute on `IActionsResolver` + in-memory Raven session (`MintPlayer.Spark.Testing` already available). | **medium** (~120 LOC; Raven session setup is the fixed cost) |
| `MintPlayer.Spark/Endpoints/PersistentObject/Update.cs` — new 409 catch block (+4 LOC) | Unit test would require mocking the endpoint pipeline; NOT worth it — already exercised by `ConcurrencyTests` e2e and effectively covered once Option C lands. | skip |
| `MintPlayer.Spark/Endpoints/Queries/Execute.cs` — parent-fetch gate (+38 LOC) | Same story as Update.cs: the handler is endpoint-shaped; cheaper under Option C. | skip |

**Total realistic effort**: medium — ~2–4 hours to write, plus CI flake surface from adding Raven-backed unit tests. Would lift patch coverage on the framework-side changes but leaves ~42 LOC in `Update.cs`/`Queries/Execute.cs` uncovered unless the E2E path is also hooked in.

**Tradeoff**: Adds durable regression tests that are faster than e2e, but it's real engineering work mid-security-audit and doesn't address the structural gap (next security PR will hit the same wall).

---

## Option B — Add `codecov.yml` matching current reality

No `codecov.yml` exists today (repo runs on codecov defaults, which derive `target: auto` from the base). Adding one lets us set explicit, defensible thresholds and ignore rules.

```yaml
# codecov.yml
coverage:
  status:
    project:
      default:
        target: auto
        threshold: 2%           # allow small regressions without failing CI
        informational: false
    patch:
      default:
        target: 30%             # realistic floor for this repo's unit-test reach;
                                # raise to 50% once E2E coverage is wired in (Option C)
        threshold: 0%
        informational: false

comment:
  layout: "reach, diff, flags, files"
  behavior: default
  require_changes: true

ignore:
  - "Demo/**"                            # demo apps, excluded from CI already
  - "**/obj/**"
  - "**/bin/**"
  - "**/*.Generated.cs"
  - "**/Generated/**"
  - "MintPlayer.Spark.SourceGenerators/**"   # tested by its own .Tests project;
                                              # source-gen files are notoriously noisy
  - "MintPlayer.Spark.E2E.Tests/**"          # test project itself shouldn't count as source
  - "MintPlayer.Spark.Tests/**"
  - "MintPlayer.Spark.SourceGenerators.Tests/**"
  - "node_packages/**"                       # Angular libs measured separately
  - "**/*.Designer.cs"
```

**Effort**: small (~15 min). Single new file, no code changes.

**Tradeoff**: Unblocks the PR immediately and sets honest, repo-wide expectations. Does NOT improve actual coverage — if a later PR introduces truly uncovered code, `patch: 30%` still catches it. The `ignore:` list also plugs a real blind spot (today the demo code under `Demo/**` isn't failing codecov only because it happens not to be included in any test project's cobertura output; if that ever changes, coverage tanks). Risk: a floor that's too low becomes permanent — call out in the PR description that this is a staging target, not a final one.

---

## Option C — Wire the E2E project into CI coverage

**Viability check**: the diagnosis got this half right. The e2e tests use Playwright, but more importantly they launch Fleet as a separate subprocess via `dotnet run` (`FleetTestHost.cs:262`). So this is **out-of-process** — coverlet.collector attached to the test process sees test-assembly code only. `WebApplicationFactory` is NOT in use.

Making Option C work therefore requires cross-process instrumentation, roughly:

1. Replace `dotnet run` with `dotnet test --collect:"XPlat Code Coverage"` on a test-harness project that hosts Fleet in-process via `WebApplicationFactory<Program>` — a meaningful refactor of `FleetTestHost`. Or,
2. Use `coverlet.msbuild` on the Fleet build, pass `/p:CollectCoverage=true /p:CoverletOutputFormat=cobertura` through `dotnet run`, and let Fleet write cobertura on shutdown. Then merge cobertura files from both processes before upload. Moderate wiring + a coverage-merge step in the workflow.
3. Much simpler sub-option: just include `MintPlayer.Spark.E2E.Tests` in the nx affected test target (so its `coverlet.collector` runs and at minimum contributes **test-assembly** coverage, plus anything reachable in-process — `_Infrastructure/FleetTestHost` itself, `SparkTestDriver`, and any library code the test assertions call directly). This closes some of the patch gap without the cross-process plumbing, but NOT the 174 LOC of framework code running inside the Fleet subprocess.

**Effort**: large for the full solution (#1 or #2). Medium for the partial (#3) — one line change in `pull-request.yml` to drop the implicit E2E exclusion + a `project.json`/nx target for the E2E project.

**Tradeoff**: Full solution is architecturally the right answer but it's a CI infrastructure project in the middle of a security audit. Partial solution helps marginally and risks flaky coverage numbers (E2E flakes on Raven seeding + Playwright). This is the correct long-term fix but the wrong PR for it.

---

## Recommendation: **Option B now, Option C follow-up**

Add `codecov.yml` as described in Option B on this PR. Rationale: the diagnosis establishes that the 174 uncovered LOC is a **measurement gap, not a quality gap** — every added line is behaviourally tested by the new Security/*.cs e2e suite (29 pass / 0 skip / 0 fail). A `patch: 30%` floor with `ignore:` rules matches what CI actually measures today, unblocks the security PR, and doesn't require writing any code. The user is mid-security-audit with more findings to ship; time-to-merge matters more than chasing instrumentation parity on this PR.

**Secondary**: file a follow-up issue for Option C (full E2E coverage wiring — path #1 or #2) and, once it lands, raise the `patch` target from 30% → 50%. Option A is NOT recommended for this PR — 2–4 hours of Raven-backed unit tests duplicates work the e2e suite already does and doesn't scale (the next security PR hits the same 26% wall unless Option C is solved structurally).

---

## Files to change if B is approved

- Add `codecov.yml` at repo root (content above).
- Add one line to PR #123 description noting the new floor is a staging target pending Option C follow-up.

No code changes. No CI workflow changes.
