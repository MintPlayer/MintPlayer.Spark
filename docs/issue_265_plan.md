# Plan — Issue #265: rate-limiter configurability, placement, and `SparkTestDriver.RequireLicense`

**PRD:** [issue_265_PRD.md](issue_265_PRD.md) ·
**Branch:** `feat/issue-265-rate-limiter-config` · **Ships in:** `10.0.0-preview.52`

Three independent deliverables from one issue. They touch disjoint files, so each is its own commit and
each can be reverted alone.

**All milestones complete.**

| | Milestone | Requirements | Commit |
|---|---|---|---|
| M0 | Spikes — settle the two load-bearing unknowns | — | `0396985` |
| M1 | `PathPrefixes` on `SparkRateLimiterOptions` | R1–R4 | `1dd49b4` |
| M2 | Middleware stages + limiter moves ahead of authentication + doc-comment fix | R5–R9 | `4ee489c` |
| M3 | `SparkTestDriver.RequireLicense` | R10, R11 | `ff82a0d` |
| M4 | Guide, version bump, PRD/plan finalisation | R12 | `50613b3` |
| M5 | PR #266 review round — error-message accuracy, routing precondition, release notes | R13–R16 | `2a48d66` |
| M6 | Second review round — the routing guard's minimal-hosting false positive | R17 | this commit |

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
- `AddMiddleware(action, stage = AfterSpark)` — the default keeps the five registrants that predate
  stages (PRD F5, everything but the limiter) exactly where they are, unchanged and un-recompiled.
- `ApplyMiddleware(app, stage)` — **stage required, no default** (R8). One appliable-once guard per
  stage; `AddMiddleware` into an already-applied stage throws (R7).
- `UseSpark` gains `registry.ApplyMiddleware(app, BeforeAuthentication)` as its **first** statement,
  ahead of the `UseAuthentication` branch; the existing line 297 call becomes
  `ApplyMiddleware(app, AfterSpark)`.
- The limiter registers with `BeforeAuthentication`.
- Rewrite the class doc comment: drop "no separate `app.UseRateLimiter()` call needed", state where the
  middleware sits and why, and warn that combining it with a manual `UseRateLimiter()` double-charges
  every request for half the configured budget with no error — and that Spark cannot detect this (R9).

**Collateral, all in tests and all intended.** R8 turned every bare `ApplyMiddleware(app)` into a
compile error, which is the point: `SparkBuilderRateLimiterExtensionsTests` would otherwise have kept
passing while wiring an empty pipeline. Seven call sites across four files now name their stage
(`SparkModuleRegistryTests` ×4, `CertificateForwardingTrustTests`, `SparkBuilderMessagingExtensionsTests`,
`SparkBuilderReplicationExtensionsTests`) — all `AfterSpark`, matching what they register.

One failure the compiler could *not* catch, found by the sweep: `SparkMigrationsExtensionsTests`
counts middleware registrations by reflecting on the private `middlewareActions` field and casting it
to `IList`, which is now a `Dictionary` keyed by stage — an `InvalidCastException` at runtime. Fixed by
summing across every stage rather than reading one, deliberately: counting only the default stage would
let a later change relocate the migrations hook without the idempotency assertions noticing, and "wired
exactly once" is a claim about the registry, not about one bucket of it. The reflection itself stays —
that test avoids *running* the action on purpose, since `SparkMigrationRunner.RunAtStartup` needs a live
document store.

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

**Tests, and an honest limit on them.** The intent was to promote S2 into a committed regression test.
That is not fully possible, and the test says so rather than implying coverage it does not have:
`ConfigureServer` is static and runs once per process before any fixture exists, and this repository has
a `raven-license.log` at its root that `LicenseHelper` finds — so an in-tree test *always* runs against
a licensed server and cannot reach the `ThrowOnInvalidOrMissingLicense = false` branch at all. Making it
reach that branch would mean renaming the developer's licence file from inside a test: destructive, and
hostile to parallel runs.

So `SparkTestDriverLicenseTests` asserts the observable half — a relaxed fixture gets a working store
and round-trips a document, and the strict default is pinned against an accidental flip. The
licence-less branch was instead verified **manually**, twice, by temporarily moving the repo-root
licence aside (with a shell trap guaranteeing restoration) and running the real `SparkTestDriver`:

- a `RequireLicense => false` fixture stored, queried and asserted successfully with no licence present;
- a strict fixture (`UseSparkOptionsTests`) still failed with the `RavenDB license not found` message.

That second run is the one that matters most — it proves the opt-in did not quietly relax the default.
The standing coverage for the licence-less path is a fork CI run, which genuinely has no licence and is
the exact scenario the option exists for.

## M4 — Guide, version, docs (R12)

- New `docs/guide-rate-limiting.md`: what is metered by default, how to extend the scope, where the
  middleware sits, and the do-not-combine warning — one page an adopter can be pointed at instead of a
  doc comment. Linked from the README's guide table.
- All 21 package `<Version>` values `10.0.0-preview.51` → `10.0.0-preview.52`, in lockstep. CI publishes
  on merge to master, so the bump rides the PR; nothing is pushed by hand.
  - Deliberately **not** bumped: the `preview.51` in `README.md:273`, which is prose about when
    `modelHashes.json` was introduced. A blanket search-and-replace would have silently rewritten a
    historical fact into a false one.
- PRD and this plan filled in with commits, spike results, and the two places reality diverged from
  the written intent (M2's collateral, M3's testability limit).

---

## Verification

Test suites run **once**, after M3, per the batching rule — intermediate milestones were verified by
reading and by build (solution-wide `dotnet build`, clean).

**Result: `MintPlayer.Spark.Tests` — 1483 passed, 0 failed.**

The first sweep was 1481/2, both in `SparkMigrationsExtensionsTests` and both mine — see M2's collateral
note. Green after the fix, re-run in full rather than in isolation.

Two checks worth naming separately, because each was aimed at a specific way this work could have been
fake rather than at coverage:

- **The placement test discriminates.** `A_rate_limited_request_is_rejected_before_authentication_runs`
  was re-run with the limiter temporarily put back on `AfterSpark`, and it **failed**. A test that
  passes on both placements would assert nothing about F3 — the same trap as #258's F3, where a negative
  assertion held because the feature was entirely dead.
- **The licence opt-in did not relax the default.** Verified by hiding the repo-root licence: the
  relaxed fixture passed, and a strict fixture still failed with `RavenDB license not found`.

`RateLimitTests` is the one to watch. It hammers `/spark/auth/me` for a 429 against Fleet, which opts in
with `_ => { }` — so it exercises both the unchanged defaults (M1) and the new placement (M2) against a
real host. It also sleeps 11 s afterwards to let the fixed window roll over, since every test in the
collection shares `127.0.0.1` as its partition key; that coupling is pre-existing and untouched, but it
means a failure there can be bucket contamination rather than a regression. Per the repo's standing
caution, a red test from the full suite gets re-run in isolation before being called a regression.

---

## M5 — PR #266 review round

Four points from the reporter's review of the diff. Two were message-accuracy one-liners; one added a
guard; one was documentation. The review also confirmed the three structural things this change could
most plausibly have got wrong — `AfterSpark` applying at exactly the old call site (so the Migrations
startup task still runs after index creation), the placement test discriminating, and the
`RequireLicense` split — so those are left as they were.

### R13 — `["/"]` reported the wrong problem ✅

A bare root normalized to empty and was dropped, so a caller who named exactly one prefix was told they
had named none. The *reason* — `"/"` means meter everything, including static assets — existed only in a
code comment.

Kept as a refusal rather than accepting it as meter-everything. `"/"` reads like "the root path" and
means the opposite, so honouring it would turn a likely misreading into a silently over-applied limiter;
an app that genuinely wants everything metered can always say `["/api"]`, which is explicit and cannot
be misread. Now refused with its own message, and documented on the property, in the `<exception>` tag,
and in the guide. A `"/"` alongside a real prefix is ignored rather than fatal — there is no ambiguity
to refuse once the scope is expressed.

### R14 — the `ArgumentException` named an internal parameter ✅

`nameof(configured)` surfaced as `(Parameter 'configured')`, meaningless to a caller who set
`PathPrefixes`, and contrary to R3's own wording. Now `nameof(SparkRateLimiterOptions.PathPrefixes)`,
asserted on `ParamName` rather than only on the message text — the old test passed on the message and
would not have caught this.

### R15 — `BeforeAuthentication`'s routing precondition is now enforced ✅

The stage promises routing has run; `UseSpark()` is documented as "call after `UseRouting()`" and nothing
checked it. An app getting the order wrong placed the limiter ahead of endpoint selection, where
`[EnableRateLimiting]`/`[DisableRateLimiting]` silently stop applying and metering falls back to
global-only — the same *quietly doing less than configured* failure this whole change exists to remove,
which is why it was worth fixing now rather than deferring as the reviewer allowed.

`UseSpark` throws when routing has not run **and** something is registered early, gated on a new
`SparkModuleRegistry.HasMiddleware(stage)`. Gating matters: an app with no early middleware cannot be
affected by the ordering, so failing it would impose a new hard requirement for no benefit.

Detection is `app.Properties["__EndpointRouteBuilder"]` — the key `UseRouting` stamps and `UseEndpoints`
reads for its own ordering check. Verified empirically rather than from memory: a probe printed
`[application.Services, server.Features]` before `UseRouting()` and
`[application.Services, server.Features, __EndpointRouteBuilder, __UseRouting]` after.

### R16 — the two breaking changes are named ✅

New `docs/release-notes-preview-52.md`, following the `preview-42` precedent, leading with both breaking
changes rather than the features:

- `ApplyMiddleware` changed signature with no overload — public on a public `Abstractions` type, so an
  external caller stops compiling. The no-default reasoning stands (R8); it is a compile break, not an
  addition, and saying so is separate from defending it.
- `AddMiddleware` throws where it previously no-op'd — an app that registered late did not crash before
  and will not start now. The middleware was never running either way; the change is *when you find out*.

Also documents the placement move as a behaviour change for every app already opted in, and the new
`UseRouting` refusal.

---

## M6 — the routing guard had a false positive (R17)

The reviewer flagged that `VerifyRoutingHasRun` keys off a private ASP.NET Core detail and **fails
closed**, and predicted a concrete false positive: a minimal-hosting app that never calls `UseRouting()`
and relies on `WebApplication` inserting routing for itself. They were explicit that they could not read
`ApplicationBuilder.Build()`'s auto-insert logic and were reasoning from documented behaviour plus one
detail — that `WebApplication` stamps `__GlobalEndpointRouteBuilder` instead.

**Measured, and they were right on both counts.** Two probes:

1. A minimal-hosting app's `IApplicationBuilder.Properties` after `Build()` and after `MapGet`:

   ```
   after Build()  : [__GlobalEndpointRouteBuilder, application.Services, server.Features]
   after MapGet() : [__GlobalEndpointRouteBuilder, application.Services, server.Features]
   has __EndpointRouteBuilder      : False
   has __GlobalEndpointRouteBuilder: True
   ```

2. Whether such an app is *actually* mis-ordered, or merely unrecognised — the question that decides
   whether this is a false positive or a true one. A middleware registered straight after `Build()`,
   with no `UseRouting()` anywhere:

   ```
   PROBE endpoint at middleware time: HTTP: GET /exempt
   PROBE DisableRateLimiting metadata visible: True
   ```

   The endpoint is selected and its metadata is visible. The pipeline is correct; the guard was refusing
   a working app.

**Fix:** accept either marker. `__EndpointRouteBuilder` (explicit `UseRouting()`, and what `UseEndpoints`
reads for its own check) or `__GlobalEndpointRouteBuilder` (`WebApplication`, stamped at construction).

**Also fixed: the diagnostic.** The reviewer's deeper point was about failure *direction* — if a key is
ever renamed, every opted-in app stops starting with a message telling them to do something they already
did. Refusing on a heuristic while asserting the caller made a mistake is the wrong shape. The message
now names the possibility that the check has failed to recognise a valid pipeline, and gives the
unblocking workaround (an explicit `UseRouting()`), so the failure is self-aware rather than misleading.

Kept as fail-closed rather than downgraded to a warning: the true positive it catches is silent, and a
warning in startup logs is exactly what nobody reads.

**Test:** `Minimal_hosting_without_an_explicit_UseRouting_is_not_refused` builds a real
`WebApplication`, asserts the key situation that caused the bug, and calls `UseSpark()` through it.
Verified to **fail** with the single-key version restored — the first draft of this test asserted against
`ApplyMiddleware`, which never invokes the guard at all, and would have passed either way.