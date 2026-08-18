# Release notes — `10.0.0-preview.52`

Packages: all `MintPlayer.Spark.*` at `10.0.0-preview.52`. No Angular package changes.

All from [#265](https://github.com/MintPlayer/MintPlayer.Spark/issues/265): the rate limiter becomes
configurable and moves ahead of authentication, a new analyzer (`SPARK004`) catches `UseSpark()` called
before `UseRouting()` at build time, and `SparkTestDriver` gains an opt-out for the RavenDB licence
requirement. See [Rate Limiting](./guide-rate-limiting.md) for the full configuration surface.

**Two breaking changes**, both in the middleware registry and both deliberate. Neither is visible from
the feature summary, so they are first.

---

## Breaking — `SparkModuleRegistry.ApplyMiddleware` changed signature

```csharp
// before
registry.ApplyMiddleware(app);

// now
registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);
```

`ApplyMiddleware` is public on a public type in `MintPlayer.Spark.Abstractions`, so **any external
caller stops compiling.** There is no overload, on purpose.

Middleware registration now has a *stage* — `BeforeAuthentication` or `AfterSpark` — and
`ApplyMiddleware` runs one stage at a time. A defaulted parameter would let a caller apply one stage
and silently drop the other, losing middleware with no error. That is the precise failure the staging
work exists to remove, so the parameter is required and every call site has to say what it is building.

**What to do:** pass `SparkMiddlewareStage.AfterSpark` to get the previous behaviour. If you were
replicating Spark's pipeline, apply both stages in the order `UseSpark` does —
`BeforeAuthentication` at the top, `AfterSpark` at the end.

Almost nobody calls this directly; it is framework-internal in practice. Named here because "public
type in an Abstractions package" means we cannot know that for certain.

## Breaking — `AddMiddleware` now throws where it previously did nothing

Registering middleware for a stage that has already been applied throws `InvalidOperationException`.
Previously it appended to a list nobody would read again — a **silent no-op**.

An app or module that registered too late (typically from inside another `AddMiddleware` callback,
which runs while the pipeline is being built) was previously "working" only in the sense of not
crashing: its middleware never ran. It now fails at startup.

**This can turn a starting app into a non-starting one.** That is the improvement — the middleware was
not running either way, and now you find out at startup rather than from behaviour that never happens.
The same class of mistake is already documented on `AddIndexAssembly`, where a late declaration meant
indexes were never created.

**What to do:** move the registration into the module's own `AddXxx(...)` body, during service
configuration.

---

## Rate limiter: `PathPrefixes`

The limiter's *scope* is now configurable. It previously tested `/spark` and `/connect` as literals,
so an app could tune the budget but not point the limiter at its own surface.

```csharp
spark.AddRateLimiter(options =>
{
    options.PermitLimit = 300;
    options.Window = TimeSpan.FromSeconds(30);
    options.PathPrefixes = ["/spark", "/connect", "/api/browse"];
});
```

- Defaults to `["/spark", "/connect"]` — **existing callers see no change.**
- Assigning **replaces** the defaults. List `/spark` explicitly to keep Spark's endpoints metered.
- Slashes are normalized: `"api/browse"`, `"/api/browse"`, `"/api/browse/"` are equivalent.
- All prefixes share one bucket per client IP, as `/spark` and `/connect` already did. For a distinct
  budget on one route, use a named ASP.NET policy with `[EnableRateLimiting]`.
- Naming no usable prefix throws at startup rather than metering nothing. A bare `"/"` is refused
  separately and says why: it would meter every request including static assets.

## Rate limiter: the middleware moved ahead of authentication

It was the last thing `UseSpark()` added — behind `UseAuthentication`, `UseAuthorization`, antiforgery
and `SparkMiddleware`. It now runs at the **top** of `UseSpark()`, after the app's `UseRouting()`.

**This changes behaviour for every app that already opted in**, and in the intended direction: a 429
now costs no credential validation. If your flood risk is an authenticated ingest endpoint that
authenticates via a database lookup, that lookup is no longer paid before the request is rejected.

Routing has already run at that point, so `[EnableRateLimiting]` / `[DisableRateLimiting]` on an
endpoint still resolve.

**If you were calling `app.UseRateLimiter()` by hand** to get the limiter early, or to meter your own
paths — both are now configuration. Delete the manual call, and read the next section for why leaving
it in is worse than redundant.

### Do not combine with a manual `app.UseRateLimiter()`

The old doc comment said "no separate `app.UseRateLimiter()` call needed", which read as *harmless if
you do*. It is not, and this is worth stating plainly because it is silent.

`UseRateLimiter` has no idempotence marker — unlike `UseRouting` it does not detect a previous
registration and return early — and `RateLimitingMiddleware` records nothing on the request to say it
ran. Two registrations mean **every request acquires two leases from the same partition**, so an app
configured for 150 requests per window gets 75. No error, no log; the only symptom is 429s at roughly
twice the expected rate, which reads as a bad traffic estimate.

**Spark cannot detect this.** A manual `UseRateLimiter()` is a call on your own `IApplicationBuilder`,
invisible at startup, leaving no runtime trace to compare against.

## New analyzer: `SPARK004` — `UseSpark()` must follow `UseRouting()`

Middleware added by `UseSpark` reads endpoint metadata — the rate limiter's `[EnableRateLimiting]` /
`[DisableRateLimiting]`, and `UseAuthorization`'s `[Authorize]`. Before routing has run no endpoint is
selected, so all of it is silently ignored. `SPARK004` now catches that at build time, at the `UseSpark()`
call.

It follows `ASP0001` (the equivalent rule for `UseAuthorization`) in both approach and severity: a warning,
not an error, and silent whenever it cannot be certain — notably when `UseRouting()` is absent from the
body, which is the correct minimal-hosting shape. Escalate it in `.editorconfig` to make it fatal.

**No runtime order checking ships.** Ordering is a property of your code, not of a request.

## `SparkTestDriver.RequireLicense`

New `protected virtual bool RequireLicense => true`. **The default is unchanged**: a missing RavenDB
licence still fails the fixture with a message naming `RAVENDB_LICENSE` and `raven-license.log`.

Override it to `false` for a suite that must survive without one. The motivating case is fork pull
requests — organization secrets are not exposed to `pull_request` runs from forks, so a contributor
without a licence otherwise fails every RavenDB test, including the majority that touch no licensed
feature. A licence-less embedded server does support store, load, query and update.

An **invalid** licence still fails at startup regardless of this property: a supplied licence is always
validated. The property gates *the fixture's* hard failure, not the server's tolerance — server
tolerance is decided once per process from whether a licence was found at all, so one test run may
freely mix strict and relaxed fixtures.
