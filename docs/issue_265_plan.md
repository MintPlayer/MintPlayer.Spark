# Plan — Issue #265: rate-limiter configurability, placement, and `SparkTestDriver.RequireLicense`

**PRD:** [issue_265_PRD.md](issue_265_PRD.md) ·
**PR:** [#266](https://github.com/MintPlayer/MintPlayer.Spark/pull/266) ·
**Ships in:** `10.0.0-preview.52`

Delivered as a single squashed commit, so this describes the work as it lands on `master` rather than the
order it was built in. Three independent deliverables from one issue, touching disjoint files.

| | Work | Requirements |
|---|---|---|
| W1 | `PathPrefixes` on `SparkRateLimiterOptions` | R1–R5 |
| W2 | Middleware stages and the limiter ahead of authentication | R6–R10 |
| W3 | `SparkTestDriver.RequireLicense` | R11, R12 |
| W4 | Guide, release notes, version bump | R13, R14 |

Two spikes settled load-bearing unknowns before any of it was written; both are recorded because a
negative result on either would have changed the shape of the work.

---

## Spikes

### S1 — does `ThrowOnInvalidOrMissingLicense` exist on the pinned RavenDB? PASSED

Present in `~/.nuget/packages/ravendb.embedded/7.2.5/lib/netstandard2.0/Raven.Embedded.dll` alongside
`LicensingOptions`. `RavenDB.TestDriver` 7.2.5 is the pinned version in
`libs/testing/MintPlayer.Spark.Testing/MintPlayer.Spark.Testing.csproj`.

Had it failed, W3 would have needed a package bump and a separate risk assessment.

### S2 — does a licence-less embedded server actually boot and serve CRUD? PASSED

The entire premise of W3. If `ThrowOnInvalidOrMissingLicense = false` yields a server that starts but
cannot store a document, `RequireLicense => false` buys a fork contributor nothing and should not ship.

Run as a standalone `RavenTestDriver` subclass **outside the repo tree**, so that
`LicenseHelper.TryReadRepoRootLicense`'s eight-level walk up from `AppContext.BaseDirectory` finds
nothing — a `raven-license.log` *is* present at this repo's root, so an in-tree spike would have proved
nothing:

```
RAVENDB_LICENSE set: False
server booted, store initialized
store: ok
query: 2 docs
load: alpha
indexed query by field: beta.Count=2
update + query: ok
```

Store, auto-index query, load and update all work with no licence at all. The restricted mode a
licence-less server drops into does not touch anything a typical fixture needs, which is exactly the
premise the issue rests on.

### Not spiked: the double-`UseRateLimiter` double-charge (PRD F4)

Deliberate. The reporter verified it against the ASP.NET Core source, the mechanism is stated precisely
enough to check by reading (`RateLimitingMiddleware.Invoke` sets no marker; `UseRateLimiter` has no
early-return), and **the fix does not depend on the answer** — R10's warning is worth writing even if the
double-charge were somehow benign, because two limiters on one partition is not a configuration anyone
intends. Spiking it would cost a timing-sensitive test to confirm something we would not act differently
on.

---

## W1 — `PathPrefixes` (R1–R5)

**Files:** `libs/spark/MintPlayer.Spark/Extensions/SparkRateLimiterOptions.cs`,
`libs/spark/MintPlayer.Spark/Extensions/SparkBuilderRateLimiterExtensions.cs`

- `PathPrefixes`, defaulting to `["/spark", "/connect"]`.
- Normalised once in `AddRateLimiter`, **outside** the partition lambda: trim, ensure a leading slash,
  drop any trailing slash, project to `PathString[]`. Doing it per request would put string work on every
  request for a value that cannot change after startup.
- The partition factory loops the normalised array; the partition key stays the client IP alone, so all
  prefixes share one bucket (R5 / PRD D1).
- Naming no usable prefix throws, with `PathPrefixes` as `ParamName` — not an internal parameter name,
  which would mean nothing to a caller who set the property.
- A bare `"/"` is tracked separately (`sawRoot`) so it gets its own message rather than being reported as
  an empty configuration (PRD D6). `["/", "/api"]` is honoured: the root contributes nothing to the scope,
  so with a usable prefix present there is no ambiguity to refuse.

**Tests:** defaults; all three normalisation forms; assignment replaces rather than extends; one bucket
shared across prefixes; every spelling of "no prefixes" throws with the right `ParamName`; `"/"` refused
on its own terms; `"/"` plus a real prefix accepted.

Path scoping is a per-request decision inside the partition factory, so those tests boot a `TestServer`
pipeline and read status codes back rather than asserting against DI.

## W2 — Stages and placement (R6–R10)

**Files:** `libs/spark/MintPlayer.Spark.Abstractions/Builder/SparkMiddlewareStage.cs` (new),
`SparkModuleRegistry.cs`, `libs/spark/MintPlayer.Spark/SparkMiddleware.cs`,
`SparkBuilderRateLimiterExtensions.cs`

- `enum SparkMiddlewareStage { AfterSpark = 0, BeforeAuthentication = 1 }`. `AfterSpark` takes the zero
  value on purpose: `default(SparkMiddlewareStage)` must mean the pre-change behaviour, not a silent move.
- `AddMiddleware(action, stage = AfterSpark)` — the default keeps the five registrants that predate stages
  (PRD F5, everything but the limiter) exactly where they are, unchanged and un-recompiled. That includes
  Migrations, which uses this hook for a *startup task* rather than middleware, and must still run after
  `CreateSparkIndexes`.
- `ApplyMiddleware(app, stage)` — stage required, no default (R9). Applied-once guard per stage;
  `AddMiddleware` into an already-applied stage throws (R8).
- `UseSpark` applies `BeforeAuthentication` as its **first** statement, ahead of the `UseAuthentication`
  branch; the pre-existing final call becomes `ApplyMiddleware(app, AfterSpark)` at exactly the same
  position, so nothing that was correct behind authentication moves.
- The limiter registers with `BeforeAuthentication`.
- The class doc comment drops "no separate `app.UseRateLimiter()` call needed", states where the
  middleware sits and why, and warns that combining it with a manual `UseRateLimiter()` double-charges
  every request for half the configured budget with no error — and that Spark cannot detect this (R10).

### The routing-order requirement is a contract, not a check

`BeforeAuthentication` assumes routing has already run, which is what makes endpoint-attached
`[EnableRateLimiting]` / `[DisableRateLimiting]` resolve. `UseSpark` has documented "call after
`UseRouting()`" all along, and that is where it stays. **No runtime validation ships.**

A guard was built and removed; PRD D7 carries the reasoning. The three points that decided it:

- Spark's limiter is a `GlobalLimiter` and Spark declares no rate-limiting endpoint metadata, so Spark's
  own behaviour never depended on routing order. Only the *app's* attributes on the *app's* endpoints do.
- `UseRouting()` is outside `UseSpark()`, so **the placement change did not affect the exposure**. Moving
  the limiter from the end of `UseSpark` to the start changed its side of *authentication*, not of
  *routing*; both positions sit on the same side of routing in either ordering.
- `UseAuthorization`, which `UseSpark` also calls, carries the identical requirement for `[Authorize]` — a
  worse silent failure, and no middleware validates it in ASP.NET or in Spark. Guarding the milder case at
  run time while leaving that alone is incoherent.

And the framing that generalises: **ordering is a compile-time property, so it belongs to an analyzer —
and ASP.NET already does it that way.** `UseRouting` / `UseAuthentication` / `UseAuthorization` /
`UseEndpoints` perform no runtime order checks; the rule ships as **`ASP0001`** — *"The call to
UseAuthorization should appear between app.UseRouting() and app.UseEndpoints(..) for authorization to be
correctly evaluated"* — in
`sdk/<ver>/Sdks/Microsoft.NET.Sdk.Web/analyzers/cs/Microsoft.AspNetCore.Analyzers.dll`. First-party
precedent for this exact class of rule, on the exact case above.

Spark already ships `MintPlayer.Spark.SourceGenerators` with `SPARK001`–`SPARK003`, so a
`UseSpark`-before-`UseRouting` diagnostic could live there — reported where the mistake is written, no
runtime cost, no framework internals. Left as a possible follow-up, deliberately out of scope for #265.

### Collateral, all in tests and all intended

R9 turned every bare `ApplyMiddleware(app)` into a compile error, which is the point: the rate-limiter
test would otherwise have kept passing while wiring an empty pipeline. Seven call sites across four files
now name their stage (`SparkModuleRegistryTests` x4, `CertificateForwardingTrustTests`,
`SparkBuilderMessagingExtensionsTests`, `SparkBuilderReplicationExtensionsTests`) — all `AfterSpark`,
matching what they register.

One failure the compiler could *not* catch: `SparkMigrationsExtensionsTests` counts middleware
registrations by reflecting on the private `middlewareActions` field and casting it to `IList`, which is
now a `Dictionary` keyed by stage. Fixed by summing across every stage rather than reading one —
deliberately, because counting only the default stage would let a later change relocate the migrations
hook without the idempotency assertions noticing, and "wired exactly once" is a claim about the registry
rather than one bucket of it. The reflection stays: that test avoids *running* the action on purpose,
since `SparkMigrationRunner.RunAtStartup` needs a live document store.

## W3 — `SparkTestDriver.RequireLicense` (R11, R12)

**Files:** `libs/testing/MintPlayer.Spark.Testing/SparkTestDriver.cs`

- The static constructor calls `ConfigureServer` **unconditionally**; `Licensing` carries the licence when
  one is found and `ThrowOnInvalidOrMissingLicense = false` when none is. An invalid licence is still
  supplied and still validated, so R12 holds with no extra code.
- `protected virtual bool RequireLicense => true`, consulted at `InitializeAsync` to gate
  `LicenseHelper.EnsureAvailable()`.
- The doc comment states the split plainly (PRD D4): this gates *the fixture's* hard failure, not the
  server's. Without that, the name promises something it does not do.

**An honest limit on the test.** The committed test cannot reach the licence-less branch.
`ConfigureServer` is static and runs once per process before any fixture exists, and this repository has a
`raven-license.log` at its root that `LicenseHelper` finds — so an in-tree test always runs against a
licensed server. Forcing it would mean renaming a developer's licence file from inside a test:
destructive, and hostile to parallel runs.

`SparkTestDriverLicenseTests` therefore asserts the observable half — a relaxed fixture gets a working
store and round-trips a document, and the strict default is pinned against an accidental flip. The
licence-less path was verified **manually**, by temporarily moving the repo-root licence aside with a
shell trap guaranteeing restoration:

- a `RequireLicense => false` fixture stored, queried and asserted successfully with no licence present;
- a strict fixture (`UseSparkOptionsTests`) still failed with the `RavenDB license not found` message.

The second is the one that matters — it proves the opt-in did not quietly relax the default. Standing
coverage for the licence-less path is a fork CI run, which genuinely has no licence and is the exact
scenario the option exists for.

## W4 — Guide, release notes, version (R13, R14)

- `docs/guide-rate-limiting.md`, linked from the README's guide table.
- `docs/release-notes-preview-52.md`, following the `preview-42` precedent and leading with the two
  breaking changes rather than the features.
- All 21 package `<Version>` values `10.0.0-preview.51` to `10.0.0-preview.52`, in lockstep. CI publishes
  on merge to master, so the bump rides the PR; nothing is pushed by hand.
  - Deliberately **not** bumped: the `preview.51` in `README.md:273`, which is prose about when
    `modelHashes.json` was introduced. A blanket search-and-replace would have silently rewritten a
    historical fact into a false one.

---

## Verification

Test suites run once, after the implementation was complete, per the batching rule — intermediate work was
verified by reading and by a clean solution-wide `dotnet build`.

**`MintPlayer.Spark.Tests` — 1491 passed, 0 failed.**

Two checks aimed at whether the work could be *fake* rather than at coverage:

- **The placement test discriminates.** Re-run with the limiter put back on `AfterSpark`,
  `A_rate_limited_request_is_rejected_before_authentication_runs` **fails**. It asserts that a 429 costs no
  credential validation — behaviour, not a stage constant. A test passing on both placements would repeat
  #258's F3, where a negative assertion held because the feature was entirely dead.
- **The licence opt-in did not relax the default**, per W3 above.

`RateLimitTests` (E2E, Fleet) was **not** run locally — it needs a live host, and CI covers it. It is the
one to watch there: it hammers `/spark/auth/me` for a 429 against Fleet, which opts in with `_ => { }`, so
it exercises both the unchanged defaults and the new placement against a real host. It also sleeps 11 s
afterwards to let the fixed window roll over, since every test in the collection shares `127.0.0.1` as its
partition key. That coupling is pre-existing and untouched, but it means a failure there can be bucket
contamination rather than a regression — per the repo's standing caution, a red test from the full suite
gets re-run in isolation before being called one.
