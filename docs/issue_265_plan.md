# Plan — Issue #265: rate-limiter configurability, placement, and `SparkTestDriver.RequireLicense`

**PRD:** [issue_265_PRD.md](issue_265_PRD.md) ·
**Branch:** `feat/issue-265-rate-limiter-config` · **Ships in:** `10.0.0-preview.52`

Three independent deliverables from one issue. They touch disjoint files, so each is its own commit and
each can be reverted alone.

| | Milestone | Requirements | Commit |
|---|---|---|---|
| M0 | Spikes — settle the two load-bearing unknowns | — | |
| M1 | `PathPrefixes` on `SparkRateLimiterOptions` | R1–R4 | |
| M2 | Middleware stages + limiter moves ahead of authentication + doc-comment fix | R5–R9 | |
| M3 | `SparkTestDriver.RequireLicense` | R10, R11 | |
| M4 | Guide, version bump, PRD/plan finalisation | R12 | |

---

## M0 — Spikes

Two things the rest of the plan rests on, both cheap to settle and expensive to be wrong about.

### S1 — does `ThrowOnInvalidOrMissingLicense` exist on the pinned RavenDB?

**Status: PASSED** (settled during investigation.) The symbol is present in
`~/.nuget/packages/ravendb.embedded/7.2.5/lib/netstandard2.0/Raven.Embedded.dll`, alongside
`LicensingOptions`. `RavenDB.TestDriver` 7.2.5 is the pinned version in
`libs/testing/MintPlayer.Spark.Testing/MintPlayer.Spark.Testing.csproj`.

M3 is unblocked. Had this failed, M3 would have needed a package bump and a separate risk assessment.

### S2 — does a licence-less embedded server actually boot and serve CRUD?

The entire premise of M3. If `ThrowOnInvalidOrMissingLicense = false` yields a server that starts but
cannot store a document, `RequireLicense => false` buys a fork contributor nothing and M3 should be
dropped rather than shipped as a false promise.

**Status: PASSED.** A standalone `RavenTestDriver` subclass, built and run **outside the repo tree** so
that `LicenseHelper.TryReadRepoRootLicense`'s eight-level walk up from `AppContext.BaseDirectory` finds
nothing (a `raven-license.log` *is* present at this repo's root, so an in-tree spike would have proved
nothing), with `RAVENDB_LICENSE` unset and `Licensing = new() { ThrowOnInvalidOrMissingLicense = false }`
as its only server configuration:

```
RAVENDB_LICENSE set: False
server booted, store initialized
store: ok
query: 2 docs
load: alpha
indexed query by field: beta.Count=2
update + query: ok
```

Store, auto-index query, load, and update all work with no licence at all. The restricted mode a
licence-less server drops into does not touch anything a typical fixture needs, which is precisely the
premise the issue rests on — so `RequireLicense => false` is a real capability for a fork contributor,
not a promise that fails on the first `SaveChanges`.

The decision rule was: boots and round-trips → M3 proceeds; boots but cannot do basic CRUD → M3 dropped
and the PRD amended to say the issue's suggestion cannot work. The first branch was taken.

### Not spiked: the double-`UseRateLimiter` double-charge (F4)

Deliberate. The reporter verified it against the ASP.NET Core source, the mechanism is stated precisely
enough to check by reading (`RateLimitingMiddleware.Invoke` sets no marker; `UseRateLimiter` has no
early-return), and **the fix does not depend on the answer**: R9's warning is worth writing even if the
double-charge were somehow benign, because two limiters on one partition is not a configuration anyone
intends. Spiking it would cost a timing-sensitive test to confirm something we would not act differently
on.

---

## M1 — `PathPrefixes` (R1–R4)

**Files:** `libs/spark/MintPlayer.Spark/Extensions/SparkRateLimiterOptions.cs`,
`libs/spark/MintPlayer.Spark/Extensions/SparkBuilderRateLimiterExtensions.cs`

- `public string[] PathPrefixes { get; set; } = ["/spark", "/connect"];`
- Normalise once in `AddRateLimiter`, **outside** the partition lambda: trim, ensure a leading `/`, drop
  any trailing `/`, project to `PathString[]`. Doing it per request would put string work on every
  request for a value that cannot change after startup.
- Empty (or all-blank) → `ArgumentException` naming the property (R3).
- The partition factory loops the normalised array; the partition key stays the client IP alone, so all
  prefixes continue to share one bucket (R4/D1).

**Tests** — `tests/MintPlayer.Spark.Tests/Extensions/SparkBuilderRateLimiterExtensionsTests.cs`:
default value is `["/spark", "/connect"]`; the three normalisation forms all match; empty throws.
Per-request path matching is asserted in M2's integration test, where a real pipeline exists.

## M2 — Middleware stages, and the limiter moves (R5–R9)

**Files:** `libs/spark/MintPlayer.Spark.Abstractions/Builder/SparkModuleRegistry.cs`,
new `SparkMiddlewareStage.cs` in the same folder, `libs/spark/MintPlayer.Spark/SparkMiddleware.cs`,
`libs/spark/MintPlayer.Spark/Extensions/SparkBuilderRateLimiterExtensions.cs`

- `enum SparkMiddlewareStage { AfterSpark = 0, BeforeAuthentication = 1 }`. `AfterSpark` takes the zero
  value on purpose: `default(SparkMiddlewareStage)` must mean today's behaviour, not a silent move.
- `AddMiddleware(action, stage = AfterSpark)` — the default keeps all six existing registrants
  (PRD F5) exactly where they are, unchanged and un-recompiled.
- `ApplyMiddleware(app, stage)` — **stage required, no default** (R8). One appliable-once guard per
  stage; `AddMiddleware` into an already-applied stage throws (R7).
- `UseSpark` gains `registry.ApplyMiddleware(app, BeforeAuthentication)` as its **first** statement,
  ahead of the `UseAuthentication` branch; the existing line 297 call becomes
  `ApplyMiddleware(app, AfterSpark)`.
- The limiter registers with `BeforeAuthentication`.
- Rewrite the class doc comment: drop "no separate `app.UseRateLimiter()` call needed", state where the
  middleware sits and why, and warn that combining it with a manual `UseRateLimiter()` double-charges
  every request for half the configured budget with no error — and that Spark cannot detect this (R9).

**Known collateral:** `SparkBuilderRateLimiterExtensionsTests` calls
`builder.Registry.ApplyMiddleware(app)` directly. R8 makes that a compile error — which is the point:
that test would otherwise keep passing while applying an empty stage. Updated to apply both stages.

**Tests:** registry-level — a `BeforeAuthentication` registrant is not run by an `AfterSpark` apply and
vice versa; re-registering after apply throws. Pipeline-level — the limiter's middleware is reached on a
host where authentication is configured, and a 429 is returned without the authentication handler having
run (the assertion that actually pins F3, rather than pinning an ordering integer).

## M3 — `SparkTestDriver.RequireLicense` (R10, R11)

**Files:** `libs/testing/MintPlayer.Spark.Testing/SparkTestDriver.cs`

- Static constructor calls `ConfigureServer` **unconditionally**; `Licensing` carries the licence when
  one is found and `ThrowOnInvalidOrMissingLicense = false` when none is. An invalid licence is still
  supplied and still validated, so R11 holds without any extra code.
- `protected virtual bool RequireLicense => true`, consulted at `InitializeAsync` to gate
  `LicenseHelper.EnsureAvailable()`.
- Doc comment states the split from D4 plainly: this gates *the fixture's* hard failure, not the
  server's. Without that, the name promises something it does not do.

**Tests:** a fixture overriding `RequireLicense => false` initialises and round-trips a document with no
licence in the environment (this is S2, promoted from spike to committed regression test — it is the
only thing that would catch RavenDB changing this behaviour on a future bump). Default-`true` behaviour
is left to the existing suite, which is entirely made of fixtures that inherit it.

## M4 — Guide, version, docs (R12)

- New `docs/guide-rate-limiting.md`: what is metered by default, how to extend the scope, where the
  middleware sits, and the do-not-combine warning — one page an adopter can be pointed at instead of a
  doc comment.
- All 21 package `<Version>` values `10.0.0-preview.51` → `10.0.0-preview.52`, in lockstep. CI publishes
  on merge to master, so the bump rides the PR; nothing is pushed by hand.
- PRD and this plan filled in with commits and spike results.

---

## Verification

Test suites run **once**, after M3, per the batching rule — intermediate milestones are verified by
reading and by build. Targeted runs: `SparkBuilderRateLimiterExtensionsTests`, the new registry tests,
`SparkTestDriver` licence test, and `RateLimitTests` (E2E, Fleet).

`RateLimitTests` is the one to watch. It hammers `/spark/auth/me` for a 429 against Fleet, which opts in
with `_ => { }` — so it exercises both the unchanged defaults (M1) and the new placement (M2) against a
real host. It also sleeps 11 s afterwards to let the fixed window roll over, since every test in the
collection shares `127.0.0.1` as its partition key; that coupling is pre-existing and untouched, but it
means a failure there can be bucket contamination rather than a regression. Per the repo's standing
caution, a red test from the full suite gets re-run in isolation before being called a regression.
