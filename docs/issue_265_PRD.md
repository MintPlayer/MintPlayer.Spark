# PRD — Issue #265: rate-limiter configurability, placement, and a licence opt-in for `SparkTestDriver`

**Issue:** [#265](https://github.com/MintPlayer/MintPlayer.Spark/issues/265) ·
**PR:** [#266](https://github.com/MintPlayer/MintPlayer.Spark/pull/266) ·
**Ships in:** `10.0.0-preview.52` ·
**Plan:** [issue_265_plan.md](issue_265_plan.md) · **Guide:** [guide-rate-limiting.md](guide-rate-limiting.md)

Upgrade-facing notes, including two breaking changes, are in
[release-notes-preview-52.md](release-notes-preview-52.md).

## Origin

Filed from outside the tree. [MintPlayer/CodeCoverage#10](https://github.com/MintPlayer/CodeCoverage/pull/10)
consumed the published `10.0.0-preview.51` packages, *wanted* `spark.AddRateLimiter()`, and hand-rolled
a `GlobalLimiter` instead. Two gaps caused that; a third item is unrelated ergonomics on
`SparkTestDriver`.

The value of the report is that it is a **first real out-of-tree adopter**. Fleet
(`Demo/Fleet/Fleet/Program.cs:48`) is the only in-tree consumer and it passes `_ => { }` — it exercises
exactly the defaults and therefore could never surface either gap. An options class with two properties
and a hardcoded path test looks complete until somebody has a third path.

---

## F1 — The limiter's scope is not expressible, only its budget

`SparkRateLimiterOptions` exposes `PermitLimit` and `Window`. The *scope* — which requests are metered
at all — is a literal inside the partition factory:

```csharp
// SparkBuilderRateLimiterExtensions.cs:52
var path = httpContext.Request.Path;
if (!path.StartsWithSegments("/spark") && !path.StartsWithSegments("/connect"))
    return RateLimitPartition.GetNoLimiter("no-limit");
```

An app cannot add its own anonymous surface. Coverage has `/api/browse` serving the same documents as
`/spark`, where one endpoint triggers a live GitHub fetch per uncached path. Metering `/spark` and
leaving `/api/browse` open closes one door beside an open one.

**The scope is the more important of the two knobs and it is the one that is absent.** `PermitLimit`
and `Window` tune a control that is already pointed at the right thing; `PathPrefixes` decides whether
it is pointed at anything relevant. Getting that backwards is why the class read as finished.

## F2 — The default's headline benefit can be zero while its cost is real

The doc comment presents `/connect` as the reason to scope beyond `/spark`: it carries interactive
login, two-factor and consent. That reasoning is sound, and it applied to Coverage **not at all** —
Coverage authenticates via GitHub OAuth and never references `MintPlayer.Spark.IdentityProvider`, so
there are no `/connect` endpoints in the app.

Not an argument against the default. It is an argument that **an app can pay the extension's placement
cost (F3) while collecting none of its benefit**, which is what tipped Coverage into hand-rolling.

## F3 — The limiter runs after authentication, and that is the wrong side for ingest

`AddRateLimiter` registers through the builder registry, and `registry.ApplyMiddleware(app)` is the
**last** statement of `UseSpark` (`SparkMiddleware.cs:297`). The middleware therefore lands after:

| Line | Stage |
|---|---|
| 186 | `app.UseAuthentication()` (when a credential scheme or identity user type is registered) |
| 189 | `app.UseAuthorization()` |
| 204–231 | Spark's antiforgery pre-validation + `UseAntiforgery()` |
| 238 | `UseWebSockets()` |
| 285 | `app.UseMiddleware<SparkMiddleware>()` |
| 297 | `registry.ApplyMiddleware(app)` ← the limiter |

For an app whose flood risk is an **authenticated** ingest endpoint this is inverted. Coverage's
`/api/uploads` authenticates a `covt_` token via a database lookup; a flood should be rejected before
that lookup is paid for, not after. A limiter placed behind authentication protects the application
from load it has already absorbed the expensive part of.

**Nothing about a rate limiter requires being behind authentication.** It needs `UseRouting` to have
run, so endpoint-attached policies (`[EnableRateLimiting]` / `[DisableRateLimiting]`) resolve — and
`UseSpark` is already documented as "call after `UseRouting()`". Verified: every in-tree
`UseSpark`/`UseSparkFull` call site (4 demos, 3 test hosts, plus `SparkEndpointFactory.cs:114`) calls
`UseRouting()` first. So the limiter can move to the top of `UseSpark` with no loss of capability.

## F4 — The workaround and the extension cannot be combined, and the doc says the opposite

The current remark:

> The rate-limiter middleware registers itself through the Spark builder registry — no separate
> `app.UseRateLimiter()` call needed when the app uses `UseSpark()` / `UseSparkFull()`.

"No separate call needed" reads as *harmless if you make one*. It is not.

`UseRateLimiter` has **no idempotence marker**. Unlike `UseRouting`, which stashes state on the
builder's properties and returns early, `UseRateLimiter` only calls `VerifyServicesAreRegistered` and
then adds the middleware. `RateLimitingMiddleware.Invoke` sets no feature and no `HttpContext.Items`
entry to record that it ran, and checks for none — it consults only
`DisableRateLimitingAttribute` / `EnableRateLimitingAttribute` on the endpoint. So a second
registration means **every request acquires two leases from the same partition**, and the app gets half
its configured budget.

This is the worst shape a defect can take here: **silent, and it degrades a security control**. There
is no error, no log, and the only symptom is 429s arriving at roughly twice the expected rate — which
reads as "our traffic estimate was wrong", not "we registered the middleware twice".

**We cannot detect it.** A manual `app.UseRateLimiter()` is a call on `IApplicationBuilder` that Spark
never observes, and the middleware leaves no runtime trace to compare against. Startup detection is
impossible for the same reason. So the doc note is not a consolation prize behind a "real" fix — it is
the only available mitigation for the combination, and it is needed *even after* F3 is fixed, because
an app can still call `UseRateLimiter()` by hand for reasons of its own.

## F5 — The registry has no notion of ordering, and six modules depend on it not having one

`SparkModuleRegistry.AddMiddleware` appends to one list; `ApplyMiddleware` replays it in registration
order at one point. Six call sites use it:

| Registrant | What it registers | Correct at end of `UseSpark`? |
|---|---|---|
| `SparkIdentityProviderExtensions.cs:60` | Identity-provider middleware | yes — needs authentication to have run |
| `Messaging/SparkBuilderExtensions.cs:33` | Messaging startup/middleware | yes |
| `SparkMigrationsExtensions.cs:45` | `SparkMigrationRunner.RunAtStartup` — **a startup task, not middleware** | yes — must run after services are built |
| `Replication/ModuleCertificateForwarding.cs:80` | Certificate forwarding | yes — feeds authentication, runs per request |
| `Replication/SparkBuilderExtensions.cs:37` | Replication middleware | yes |
| `SparkBuilderRateLimiterExtensions.cs:68` | `app.UseRateLimiter()` | **no** — F3 |

So the rate limiter is the *only* current registrant that wants a different position, and every other
one is correct where it is. That shapes the fix: the existing position must stay the default, and
"before authentication" must be the opt-in — not the reverse.

Note also that `AddMiddleware` is already overloaded in purpose: Migrations uses it as a
*startup-task* hook, not a middleware hook. Any staging concept has to keep working for that.

## F6 — `SparkTestDriver`'s licence handling is right, and its granularity is wrong

Current shape:

```csharp
static SparkTestDriver()                                   // SparkTestDriver.cs:33
{
    var license = LicenseHelper.LoadOrNull();
    if (license is not null)
        ConfigureServer(new TestServerOptions { Licensing = new() { License = license, EulaAccepted = true } });
}

public virtual async Task InitializeAsync()                // :88
{
    LicenseHelper.EnsureAvailable();                       // :90 — throws when absent
    Store = GetDocumentStore();
    ...
}
```

`EnsureAvailable()` throwing with a message naming both `RAVENDB_LICENSE` and `raven-license.log` is
the correct **default** for a framework, and stays the default.

The gap is fork pull requests: **organization secrets are not exposed to `pull_request` runs from
forks.** A contributor without a licence gets a hard failure on *every* RavenDB test, including the
majority that need no licensed feature at all.

**Why not simply `ThrowOnInvalidOrMissingLicense = false` everywhere.** The flag does not override a
supplied licence — with one present the server uses it regardless — so the cost looks like zero. It is
not: an **invalid** licence would stop being a startup error and become a silent downgrade to
restricted mode, surfacing much later as an obscure "feature not available in this licence" inside
whichever test first touches ETL, encryption or compression. Loud-on-invalid, tolerant-on-absent keeps
both properties, and the two halves are separable because they are triggered by different conditions.

**The static-constructor constraint the issue's suggested shape does not account for.** The issue
proposes `protected virtual bool RequireLicense => true`. `ConfigureServer` is static, called from a
static constructor that runs once per process before any instance exists — so an *instance* virtual
member cannot influence it. The fix therefore has two halves keyed on different things:

- **server tolerance** — unconditional, keyed on the licence being *absent*: always call
  `ConfigureServer`, with `ThrowOnInvalidOrMissingLicense = false` when there is nothing to validate.
  Harmless when a licence is present, because the flag is not consulted.
- **the hard failure** — instance-gated on `RequireLicense`, at `InitializeAsync`.

A useful consequence: a single process may mix `RequireLicense => true` and `=> false` fixtures. The
strict ones still fail loudly at their own `InitializeAsync`, because that half is per-instance.

Verified: `ThrowOnInvalidOrMissingLicense` exists on `ServerOptions.LicensingOptions` in
`RavenDB.Embedded` 7.2.5, the version behind the pinned `RavenDB.TestDriver` 7.2.5
(`libs/testing/MintPlayer.Spark.Testing/MintPlayer.Spark.Testing.csproj`).

`MintPlayer.Spark.Testing` is `IsPackable=true` with `PackageId=MintPlayer.Spark.Testing`, so adding a
`protected virtual` member is a public API surface change on a shipped package — additive, and we are
in preview.

---

## Requirements

| | Requirement |
|---|---|
| R1 | `SparkRateLimiterOptions.PathPrefixes` — the metered prefixes, defaulting to `["/spark", "/connect"]`. Existing callers see no behaviour change. Assigning replaces the defaults rather than adding to them. |
| R2 | Prefixes are normalized so `"api/browse"`, `"/api/browse"` and `"/api/browse/"` are all accepted and all mean the same thing. `StartsWithSegments` needs a leading slash and no trailing one; making the caller know that is a trap, not an interface. |
| R3 | Naming no usable prefix throws at `AddRateLimiter` time, with `PathPrefixes` as the exception's `ParamName`. A limiter configured to meter nothing is a security control that silently does nothing — the one outcome worse than the error. |
| R4 | A bare `"/"` is refused **with its own message**, not reported as an empty configuration (D6). A `"/"` alongside a real prefix is ignored rather than fatal. |
| R5 | All prefixes share one per-IP bucket, as `/spark` and `/connect` already did. Per-prefix budgets are **not** added (D1). |
| R6 | `SparkMiddlewareStage` — a two-value stage on the registry. `AfterSpark` (the enum's zero value, and the default) is exactly the pre-change position; `BeforeAuthentication` is immediately after `UseRouting`, before `UseAuthentication`. |
| R7 | The rate limiter registers at `BeforeAuthentication`. This is the framework's choice, not an app-facing knob (D2). |
| R8 | `AddMiddleware` for a stage that has already been applied **throws**. Registering too late was a silent no-op, and has bitten this repo before — `AddIndexAssembly`'s doc comment records the same class of bug. |
| R9 | `ApplyMiddleware` takes the stage explicitly, with **no default** — a defaulted parameter would let a caller apply one stage and silently drop the other, which is the exact failure R8 exists to prevent. |
| R10 | The misleading `UseRateLimiter` remark is replaced by an explicit warning naming the consequence (double lease, half budget) and stating that Spark cannot detect it (D3). |
| R11 | `SparkTestDriver.RequireLicense` — `protected virtual`, default `true`. When `false`, a missing licence no longer fails the fixture. |
| R12 | An **invalid** licence still fails loudly regardless of `RequireLicense`. |
| R13 | `docs/guide-rate-limiting.md` — the configuration surface, the placement, and the do-not-combine warning in one place, linked from the README. |
| R14 | Release notes name both breaking changes (`ApplyMiddleware`'s signature, `AddMiddleware`'s new throw) and the placement move as a behaviour change for every app already opted in. |

## Decisions

**D1 — one shared bucket, not one per prefix.** Rejected giving each prefix its own partition. It
would silently change the existing default (`/spark` and `/connect` share a bucket today, and an app
upgrading would find its effective budget doubled), and ASP.NET already answers per-endpoint budgets
properly through named policies plus `[EnableRateLimiting]`. A `PartitionByPathPrefix` flag was also
rejected: it is a knob whose right value is not knowable by the framework *or* obvious to the caller.

**D2 — the stage is not a rate-limiter option.** `SparkRateLimiterOptions` gets no `Stage` property.
Before-authentication is strictly better for a limiter — it needs only routing, and rejecting earlier
is the entire point — so there is no second reasonable value to expose. The issue's option (2),
"expose the registration point", is satisfied by `SparkMiddlewareStage` being public on the registry:
an app or module with a genuinely different need can register there directly.

**D3 — F4 is documentation only, and that is not a compromise.** A manual `app.UseRateLimiter()` is
invisible to Spark at startup (a call on the caller's `IApplicationBuilder`) and at runtime
(`RateLimitingMiddleware` leaves no marker). Detection was investigated and is not possible without
reimplementing the middleware. Documented with the mechanism, not just a "don't".

**D4 — `RequireLicense` gates the throw, not the server.** Forced by the static-constructor
constraint in F6. Stated explicitly in the member's doc comment, because the natural reading of the
name is "the server will refuse to start", and it does not.

**D5 — no `IConfiguration` binding for `PathPrefixes`.** `SparkRateLimiterOptions` is code-configured
only (via `AddRateLimiter(configure)` or `SparkFullOptions.RateLimiter`), and `string[]` is the shape
that would bind cleanly if that changes later. Adding a config section now would be a second, untested
path to the same setting.

**D6 — a bare `"/"` is refused, not honoured as meter-everything.** Both were defensible. `"/"` reads as
*the root path* and means the opposite, so honouring it converts a likely misreading into a silently
over-applied limiter — metering static assets, the one outcome the extension's design is explicit about
avoiding. Refusing converts the same misreading into an error that explains itself, and nothing is lost:
an API-only app that wants everything metered writes `["/api"]`, which cannot be misread. What is *not*
defensible is the original behaviour, where `"/"` normalized to empty and was reported as "you named no
prefixes" — wrong for a caller who named exactly one.

**D7 — the routing-order requirement is a contract, not a check.** A guard was built and then removed.
The reasoning for removing it is the useful part:

- Spark's limiter is a `GlobalLimiter` and Spark declares no rate-limiting endpoint metadata anywhere, so
  Spark's own behaviour does not depend on routing order at all. The only thing harmed by a pre-routing
  placement is the *app's* `[DisableRateLimiting]` / named policies on the *app's* endpoints.
- **The placement change did not create or worsen the exposure.** `UseRouting()` lives outside
  `UseSpark()`, so the limiter is on the same side of routing as the rest of `UseSpark` in either ordering.
  Moving it from the end of `UseSpark` to the start changed its side of *authentication*, not of *routing*.
- `UseSpark` calls `UseAuthorization` unconditionally, which carries the identical requirement for
  `[Authorize]` — a strictly more severe silent failure. Neither ASP.NET nor Spark validates that at
  runtime. Guarding the limiter while leaving that unguarded is not a coherent safety story.
- **Middleware ordering is a build-time property, so it belongs to a build-time tool.** ASP.NET's own
  `UseRouting` / `UseAuthentication` / `UseAuthorization` / `UseEndpoints` do not check their own order;
  ordering rules are expressed as analyzers instead. That is the right vehicle for this too. Spark already
  ships an analyzer project (`MintPlayer.Spark.SourceGenerators`, diagnostics `SPARK001`–`SPARK003`), so a
  `UseSpark`-before-`UseRouting` rule could live there as a compile-time diagnostic if it is ever wanted —
  reported where the mistake is, with no runtime cost and no dependence on framework internals.

Recorded because two runtime implementations were tried and both were wrong, and the second failure is the
generalisable one: **whether routing has run cannot be determined from inside the pipeline being built.** No public API
on `IApplicationBuilder` answers it; the private markers (`__EndpointRouteBuilder`,
`__GlobalEndpointRouteBuilder`) are brittle *and* give the wrong answer, because minimal hosting inserts
routing while the pipeline is being built — after `UseSpark()` has returned — so a correct minimal-hosting
app is indistinguishable from a broken one. `IEndpointFeature` is no help either: it is always present, so
its absence proves nothing. The only sound *runtime* detection is per request (an endpoint absent before
`next` and present after proves routing is downstream), which works but was not worth its keep — and, per
the last bullet above, is answering a compile-time question at run time in the first place.

## Out of scope

- Sliding-window, token-bucket or concurrency limiters. The fixed window is deliberate and the issue
  does not ask.
- Named per-endpoint policies shipped by the framework. Apps declare their own; that is the ASP.NET
  seam and it works.
- Partition keys other than client IP (e.g. by authenticated principal). Would want the limiter *after*
  authentication — the opposite of F3 — and no one has asked.
