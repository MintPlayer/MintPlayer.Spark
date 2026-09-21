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
| R15 | The `UseSpark()`-after-`UseRouting()` requirement is enforced at **compile time** by analyzer `SPARK004`, not by middleware (D7). Reports only when both calls are visible in one body and provably misordered; silent when `UseRouting()` is absent, which is the correct minimal-hosting shape. |

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

> ### ✅ D5 SUPERSEDED — 2026-09-21, on `issue-422-forge-abstraction`
>
> **`SparkRateLimiterOptions` now binds from `Spark:RateLimiter`, configuration first and the
> `configure` lambda second** — the same order `AddReplication` and `AddMessaging` already use.
>
> D5 did not forbid this; it deferred it, and said so: *"`string[]` is the shape that would bind
> cleanly **if that changes later**."* This is later, and the evidence D5 lacked is below. Its stated
> objection — "a second, untested path to the same setting" — is answered by testing the path rather
> than by avoiding it.
>
> **What forced it.** The E2E suite is 88 tests in a single serialized collection sharing one bucket
> keyed on `127.0.0.1`. Ordinary load is ~2–5 requests/second against a 15/second allowance, so the
> budget is not globally too small — it fails on **bursts**: ~25 fast API tests at ~6 requests each
> land 150 requests inside one 10-second window. The repository had already recorded this
> independently, in `ViewerTimezoneRenderingTests.cs:26-31`, where adding **one** extra browser test
> *"pushed unrelated tests elsewhere in the suite into 429s"* — with `RateLimitTests` untouched. So
> the flake is structural, not the burst test's fault.
>
> **What was rejected, and why it matters more than what was chosen:**
>
> | Rejected | Reason |
> |---|---|
> | An `Enabled = false` flag | A limiter that silently does nothing is the exact outcome `NormalizePrefixes` **throws** to prevent (`SparkBuilderRateLimiterExtensions.cs:167-179`). Adding a quiet second route to it contradicts the design, and would give a production app a way to ship unmetered by accident. |
> | Per-test partitions via `X-Forwarded-For` | It works — but only because Fleet clears `KnownNetworks`/`KnownProxies`. Building CI on that cements a misconfiguration as load-bearing, and tightening it later would revive the flake **silently**. See SP-R1. |
> | `Window = 1s` instead of a raised limit | Makes `RateLimitTests` depend on pushing 150 requests through loopback inside one second — precisely the timing sensitivity `guide-rate-limiting.md` warns against. |
>
> ⚠ **Ordering trap, deliberately accepted.** Configuration binds first and code wins, matching the
> other two modules. Fleet passes `_ => { }`, which sets nothing, so the test override survives — but
> the day anyone adds `options.PermitLimit = X` to that lambda, the E2E override stops working with
> no failure to announce it. The comment at the binding site says so.

---

## Spikes behind the D5 reversal — 2026-09-21

Three read-only investigations. Recorded because two of them found things worth more than the
decision they were run for.

### SP-R1 — can a test get its own limiter partition? ⚠ It can, and that is a production finding

The partition key is *exactly* `httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"`
(`SparkBuilderRateLimiterExtensions.cs:99-101`). Nothing else — no path, no user, no header.

ASP.NET's forwarded-headers middleware performs its known-proxy check **only** when
`KnownProxies.Count > 0 || KnownNetworks.Count > 0`. When both lists are empty no trust check runs
at all, and any caller's `X-Forwarded-For` overwrites `Connection.RemoteIpAddress` before the
limiter reads it.

That was the state of `apps/CodeCoverage/CodeCoverage/Program.cs` until PR #436 — see the fix
below. `apps/Fleet/Fleet/Program.cs:16-21`, `apps/DemoApp` and `apps/HR` still clear both lists,
deliberately (`ModuleCertificateForwarding.cs:28-32`), and are not deployed.

⚠ **On production this meant every IP-keyed limit could be bypassed by rotating one header**, including
the `browse` policy (300/min) whose stated purpose is protecting the GitHub App's shared API budget
from an unmetered crawler, and `uploads` (60/min) whenever the caller has no `covt_` token.

✅ **FIXED for `apps/CodeCoverage` (PR #436).** ⚠️ And the reason it stayed open for weeks is the
part worth keeping: it was parked on *"the fix needs the real proxy address in `KnownProxies`,
which is deployment knowledge, not a code change"* — the sentence this paragraph used to end with.
That premise was false, and the refutation was in the repository the whole time.

`apps/CodeCoverage/docker-compose.yml` gives `coverage-app` **no host ports** and puts it on the
external `web` network where **Traefik is the sole ingress**. The transport peer is therefore
always a container address on a Docker bridge, so the proxy's address never had to be known:
trusting the private ranges is exactly as tight as naming the container, because nothing on a
public address can reach port 8080 to be trusted at all.

```csharp
options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
// … 10/8, 192.168/16, 127/8, ::1, fc00::/7
options.ForwardLimit = 1;   // spelled out, not defaulted
```

`ForwardLimit = 1` is what makes a spoofed header harmless rather than merely validated: Traefik
**appends** the real peer, so `X-Forwarded-For: 1.2.3.4` arrives as `1.2.3.4, <real>` and taking
one entry from the right reads Traefik's value.

The demo apps (`Fleet`, `DemoApp`, `HR`) still clear both lists. That is deliberate, documented at
`ModuleCertificateForwarding.cs:28-32`, and they are not deployed — so the rejection recorded above
still stands: per-test partitions via `X-Forwarded-For` remain the wrong thing to build CI on.

⚠️ Loopback is in the trust list, so in local dev and E2E `X-Forwarded-For` **is** honoured from
127.0.0.1. Safe only because of the topology, which is the whole argument — not the address family.

### SP-R2 — is configuration binding idiomatic here? Yes; the limiter was the exception

`Spark` (`SparkMiddleware.cs:42`), `Spark:Replication` and `Spark:Messaging` all bind already, and
messaging's doc comment states the rule: *"Options bind from the `Spark:Messaging` configuration
section first, then `configure` runs — so code wins over configuration."* `AddSparkFull` already
receives `IConfiguration`, so no signature changes.

Also found: Spark gates behaviour by environment in several places, but **always** on
`IsDevelopment()` — there is no `IsEnvironment("Test")` anywhere, and the strongest precedent
(`SparkMiddleware.cs:147-148`) pairs an environment check with an explicit config flag rather than
relying on the environment alone.

### SP-R3 — what would a disable cost? One test, and the suite is burst-bound not volume-bound

Only `RateLimitTests` asserts a 429 against a real app. Everything else about the limiter — defaults,
prefix scoping and normalization, one-bucket-per-caller, fail-fast on a no-op configuration, and
placement ahead of authentication — is already proven by `SparkBuilderRateLimiterExtensionsTests`
and `RateLimiterPlacementTests` against their own hosts. No analyzer depends on it.

Measured: ~600–900 requests across 88 tests; a browser boot is a dozen-plus `/spark` calls; and
`BrowserSignIn.WaitForAuthenticatedAsync` polls `/spark/auth/me` every 250 ms for up to 10 s
(`BrowserSignIn.cs:41-51`) — up to 40 extra requests, which is its own amplifier under load.

⚠ A second defect found while reading: `RateLimitTests.cs:40-47` performs its 11-second drain
**after** the assertion with no `try`/`finally`, so the run that leaves the bucket most saturated —
a failing one — is exactly the run that skips the cooldown.

**D6 — a bare `"/"` is refused, not honoured as meter-everything.** Both were defensible. `"/"` reads as
*the root path* and means the opposite, so honouring it converts a likely misreading into a silently
over-applied limiter — metering static assets, the one outcome the extension's design is explicit about
avoiding. Refusing converts the same misreading into an error that explains itself, and nothing is lost:
an API-only app that wants everything metered writes `["/api"]`, which cannot be misread. What is *not*
defensible is the original behaviour, where `"/"` normalized to empty and was reported as "you named no
prefixes" — wrong for a caller who named exactly one.

**D7 — the routing-order requirement is enforced by an analyzer, not by middleware.** A runtime guard was
built and removed first; the reasoning is kept because it is what pointed at the right answer.

Why not middleware:

- Spark's limiter is a `GlobalLimiter` and Spark declares no rate-limiting endpoint metadata, so Spark's
  own behaviour never depended on routing order. Only the *app's* attributes on the *app's* endpoints do.
- **The placement change did not create the exposure.** `UseRouting()` lives outside `UseSpark()`, so the
  limiter is on the same side of routing as the rest of `UseSpark` in either ordering; moving it from the
  end of `UseSpark` to the start changed its side of *authentication*, not of *routing*.
- `UseSpark` calls `UseAuthorization` unconditionally, which carries the identical requirement for
  `[Authorize]` — a strictly worse silent failure. No middleware validates that, in ASP.NET or in Spark.
- **Ordering is a property of the code, not of a request**, so a runtime check answers a compile-time
  question. Two runtime implementations were tried and both were wrong; the second failure generalises:
  **whether routing has run cannot be determined from inside the pipeline being built.** No public API on
  `IApplicationBuilder` answers it, and the private markers (`__EndpointRouteBuilder`,
  `__GlobalEndpointRouteBuilder`) are brittle *and* give the wrong answer — minimal hosting inserts routing
  while the pipeline is being built, after `UseSpark()` has returned, so a correct minimal-hosting app is
  indistinguishable from a broken one. `IEndpointFeature` is no help either: always present, so its
  absence proves nothing.

ASP.NET Core already answers this class of rule the right way: `ASP0001` — *"The call to UseAuthorization
should appear between app.UseRouting() and app.UseEndpoints(..) for authorization to be correctly
evaluated"* — in `sdk/<ver>/Sdks/Microsoft.NET.Sdk.Web/analyzers/cs/Microsoft.AspNetCore.Analyzers.dll`.

`SPARK004` (`MiddlewareOrderAnalyzer`) does it at compile time instead, in the analyzer project that already
ships `SPARK001`–`SPARK003`. It reports at the `UseSpark` call — where the mistake is written — with no
runtime cost and no dependence on framework internals. Conservative by construction: both calls must be
visible in one body; they are ordered by *name token*, so a chained `app.UseSpark().UseRouting()` is read
the way the runtime composes it; and it says nothing when `UseRouting()` is absent, which is both the
correct minimal-hosting shape and the case where routing may be configured in a helper it cannot see.

**D8 — `SPARK004` is a warning, not an error.** It is a heuristic over source, so a build-breaking false
positive on a correct pipeline would be worse than the silent degradation it warns about. `ASP0001` makes
the same call for the same reason. An app that wants it fatal can escalate it in `.editorconfig`.

## Out of scope

- Sliding-window, token-bucket or concurrency limiters. The fixed window is deliberate and the issue
  does not ask.
- Named per-endpoint policies shipped by the framework. Apps declare their own; that is the ASP.NET
  seam and it works.
- Partition keys other than client IP (e.g. by authenticated principal). Would want the limiter *after*
  authentication — the opposite of F3 — and no one has asked.
