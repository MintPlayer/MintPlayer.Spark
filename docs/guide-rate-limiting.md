# Rate limiting

Spark ships a fixed-window rate limiter that apps opt into. The framework does not impose
rate-limiting policy (security audit finding L-3) — it provides the wiring, and the app decides the
budget and the scope.

```csharp
builder.Services.AddSpark(spark => spark.AddRateLimiter());
```

or, for `AddSparkFull` consumers:

```csharp
options.RateLimiter = _ => { };
```

That is the whole opt-in. No `app.UseRateLimiter()` call — see [Do not also call
`UseRateLimiter()`](#do-not-also-call-useratelimiter), which is not merely unnecessary but harmful.

## What it does by default

| | |
|---|---|
| Budget | 150 requests per 10 seconds |
| Partition | client IP |
| Scope | `/spark` and `/connect` |
| Over limit | HTTP 429 |

`/connect` is in the defaults alongside `/spark` because it carries the interactive login,
two-factor and consent pages. Scoping to `/spark` alone would leave an app that opted in with an
unthrottled password endpoint, and account lockout does nothing against an attacker spreading
attempts across many accounts.

An app that does not use `MintPlayer.Spark.IdentityProvider` simply has no `/connect` endpoints, and
the entry costs nothing.

## Configuring it

```csharp
spark.AddRateLimiter(options =>
{
    options.PermitLimit = 300;
    options.Window = TimeSpan.FromSeconds(30);
    options.PathPrefixes = ["/spark", "/connect", "/api/browse"];
});
```

**`PathPrefixes` replaces the defaults rather than adding to them.** List `/spark` explicitly if
Spark's own endpoints should stay metered — the example above does.

Prefixes match whole path segments, so `/api` covers `/api/browse` but not `/apidocs`. Leading and
trailing slashes are normalized: `"api/browse"`, `"/api/browse"` and `"/api/browse/"` are the same
thing.

An empty `PathPrefixes` **throws at startup**. A limiter scoped to no paths meters no requests, which
would leave the app unprotected with nothing to indicate it — the one outcome worse than a startup
error.

A bare `"/"` is **also refused**, with its own message. It reads like "the root path" but means *every
request* — static assets and SPA bundles included — which starves browser asset loads rather than
protecting an endpoint. An API-only app that genuinely wants everything metered says so by naming its
own prefixes:

```csharp
options.PathPrefixes = ["/api"];   // explicit, and impossible to misread
```

A `"/"` alongside a real prefix is simply ignored, since it adds nothing to the scope.

### One bucket per caller, not per route

Every metered prefix draws on the same per-IP bucket. The budget is a per-caller allowance, so an app
with `["/spark", "/api/browse"]` and a limit of 150 gives each IP 150 requests across both, not 150
each.

For a distinct budget on one route, declare a named ASP.NET policy and attach it to that endpoint:

```csharp
builder.Services.AddRateLimiter(rl => rl.AddFixedWindowLimiter("uploads", o =>
{
    o.PermitLimit = 10;
    o.Window = TimeSpan.FromMinutes(1);
}));

app.MapPost("/api/uploads", Handler).RequireRateLimiting("uploads");
```

Named policies compose with Spark's limiter — they are registered on the same
`RateLimiterOptions` and resolved per endpoint. Adding `AddRateLimiter` calls is fine; adding a
second `UseRateLimiter()` is not.

## Where the middleware sits

At the **top** of `UseSpark()` — after the app's `UseRouting()`, and **before**
`UseAuthentication()`.

That placement is deliberate. An app whose flood risk is an authenticated ingest endpoint may pay a
database lookup per credential; a limiter behind authentication would only protect it from load it
had already absorbed the expensive part of. Rejecting first is the point.

Routing has already run at that stage, so `[EnableRateLimiting]` / `[DisableRateLimiting]` on an
endpoint still resolve normally.

The mechanism is `SparkMiddlewareStage.BeforeAuthentication` on the Spark module registry. A module
or app with a genuinely similar need — reject before authenticating — can register there too:

```csharp
builder.Registry.AddMiddleware(app => app.UseMiddleware<MyGate>(),
                               SparkMiddlewareStage.BeforeAuthentication);
```

Nothing at that stage may read `HttpContext.User`: no credential has been validated yet, so every
request looks anonymous. Middleware that needs the principal belongs in the default
`SparkMiddlewareStage.AfterSpark`.

### `UseRouting()` must come first, and this is enforced

The stage's contract is that routing has already run — that is what makes endpoint-attached policies
resolve. `UseSpark()` has always been documented as "call after `UseRouting()`", and when anything is
registered at `BeforeAuthentication` it is now **checked** rather than trusted:

```csharp
app.UseSpark();       // throws: the limiter would sit ahead of endpoint selection
app.UseRouting();
```

Get the order wrong and the limiter runs before an endpoint has been selected, so
`[EnableRateLimiting]` and `[DisableRateLimiting]` silently stop applying and metering falls back to
global-only — a limiter quietly doing less than configured, which is the exact failure this guide's
last section is about. An app with no early middleware cannot be affected and is not checked.

## Do not also call `UseRateLimiter()`

**Combining `spark.AddRateLimiter()` with a manual `app.UseRateLimiter()` silently halves your
configured budget.**

`UseRateLimiter` has no idempotence marker. Unlike `UseRouting`, it does not detect a previous
registration and return early — it verifies its services are registered and adds the middleware.
`RateLimitingMiddleware` records nothing on the request to say it ran, and checks for nothing; it
consults only `[EnableRateLimiting]` / `[DisableRateLimiting]` on the endpoint.

So two registrations mean **every request acquires two leases from the same partition**. An app
configured for 150 requests per window gets 75. There is no error and no log entry — the only symptom
is 429s arriving about twice as often as expected, which reads as a bad traffic estimate rather than a
duplicated middleware.

**Spark cannot detect this and warn you.** A manual `app.UseRateLimiter()` is a call on your own
`IApplicationBuilder`, invisible to Spark at startup, and the middleware leaves no runtime trace to
compare against.

If you were calling `UseRateLimiter()` by hand to get the limiter ahead of authentication, or to meter
your own paths, both are now configuration — delete the manual call.

## Testing a limiter

A fixed window partitioned by IP means every test in a shared collection draws on the same bucket
(`127.0.0.1`). A test that saturates the bucket must let the window roll over before the next one
runs, or the next test inherits its 429s. `tests/MintPlayer.Spark.E2E.Tests/Security/RateLimitTests.cs`
does exactly this and is worth copying.

For unit-level work, set `PermitLimit = 1` and a long `Window` — the first request is admitted and
everything after it is rejected, with no timing sensitivity at all.
