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
| Budget | 150 requests per 10 seconds, per caller |
| Caller | the signed-in user, else the visitor cookie, else the network address |
| Address ceiling | 1500 requests per 10 seconds (10 × the budget), shared by every caller behind one address |
| Address | an IPv4 address, or an IPv6 address's **/64** |
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

### Who a caller is

Every request spends two budgets, and needs a permit from both (commerce PRD X10):

| Budget | Keyed on | Size |
|---|---|---|
| The caller's | `user:{id}` when signed in, else `visitor:{id}` for a valid [visitor cookie](guide-controllers.md#visitors), else `ip:{address}` | `PermitLimit` |
| The address ceiling | the network address | `PermitLimitPerAddress`, default 10 × `PermitLimit` |

Many people share one address behind a carrier's NAT, a campus or an office. Keyed on the address
alone, a hundred fans waiting for the same sale were one caller with one budget. The ceiling still
bounds what one address can do with many accounts or many visitor cookies.

An **address** is an IPv4 address, or the **/64** of an IPv6 address. One IPv6 subscriber is given a
whole /64 (often a /56 or a /48), and privacy extensions (RFC 8981) rotate the low 64 bits on their
own. Keyed on the exact address, one host could walk through as many budgets as it liked without
changing networks (defect F5). An IPv4-mapped IPv6 address (`::ffff:203.0.113.7`) counts as the IPv4
address it carries. The key is `SparkClientAddress.Key(IPAddress)`, public in
`MintPlayer.Spark.Abstractions.Visitor`, for any other per-address limit an app keeps.

```csharp
spark.AddRateLimiter(options =>
{
    options.PermitLimit = 150;              // per user, visitor or bare address
    options.PermitLimitPerAddress = 3000;   // everyone behind one address together
});
```

A `PermitLimitPerAddress` below `PermitLimit` throws at startup: the ceiling is shared by every caller
behind the address, so it cannot be smaller than one caller's budget.

### One bucket per caller, not per route

Every metered prefix draws on the same per-caller bucket. The budget is a per-caller allowance, so an
app with `["/spark", "/api/browse"]` and a limit of 150 gives each caller 150 requests across both,
not 150 each.

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

**One part runs after authentication, by necessity.** Who a caller is cannot be known before the
credential is validated. So a request that **presents a credential** (an `Authorization` header, a
client certificate, or the cookie of a cookie authentication scheme) spends only the address ceiling
ahead of authentication. Its caller budget is taken at `SparkMiddlewareStage.AfterSpark`, keyed on the
user it turned out to be, or on its visitor cookie or address when the credential did not
authenticate. A junk credential therefore buys at most the address ceiling's worth of authentication
work, never a fresh budget. A request with no credential (an anonymous visitor, a guest with a visitor
cookie) spends both budgets ahead of authentication, as before.

The mechanism is `SparkMiddlewareStage.BeforeAuthentication` on the Spark module registry. A module
or app with a genuinely similar need — reject before authenticating — can register there too:

```csharp
builder.Registry.AddMiddleware(app => app.UseMiddleware<MyGate>(),
                               SparkMiddlewareStage.BeforeAuthentication);
```

Nothing at that stage may read `HttpContext.User`: no credential has been validated yet, so every
request looks anonymous. Middleware that needs the principal belongs in the default
`SparkMiddlewareStage.AfterSpark`.

### Call `UseRouting()` before `UseSpark()`

Spark documents this for `UseSpark()` as a whole, and it matters to the limiter for the same reason it
matters to `UseAuthorization`: middleware that runs before routing sees no selected endpoint, so
endpoint-attached metadata is invisible. For the limiter that means `[EnableRateLimiting]` and
`[DisableRateLimiting]` stop applying and every metered path falls back to the global budget — an
exemption you declared simply does not happen.

```csharp
app.UseRouting();
app.UseSpark();      // correct
```

Minimal hosting needs nothing extra: `WebApplication` inserts routing at the front of the pipeline, so an
app that never calls `UseRouting()` explicitly is already correctly ordered.

This is a contract rather than a runtime check. `UseRouting()` lives outside `UseSpark()`, so the limiter
is on the same side of routing as the rest of `UseSpark` either way — moving it ahead of authentication
changed nothing about that — and `UseAuthorization`, which `UseSpark` also calls, carries the identical
requirement for `[Authorize]`.

**This is enforced at compile time, by analyzer `SPARK004`.** Get the order wrong and the build warns at
the `UseSpark()` call:

```
warning SPARK004: 'UseSpark()' is called before 'UseRouting()'. Endpoint metadata such as [Authorize],
[EnableRateLimiting] and [DisableRateLimiting] is silently ignored by middleware that runs before
routing — move 'app.UseRouting()' above 'app.UseSpark()'
```

Ordering is a property of the code you wrote, not of a request, so it is analyzer territory rather than
middleware — which is how ASP.NET handles it too: `ASP0001` is the same rule for `UseAuthorization`. Spark
does no runtime order checking.

`SPARK004` is deliberately quiet when it cannot be certain. It says nothing when `UseRouting()` is absent
from the body — the correct minimal-hosting shape, and also the case where you configure routing in a
helper method — and it is a warning rather than an error, so escalate it in `.editorconfig` if you want it
fatal.

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

A fixed window partitioned by caller means every anonymous test in a shared collection draws on the
same bucket (`127.0.0.1`), and every test signed in as the same user on the same one. Tests signed in
as different users no longer share a bucket, but they all share the address ceiling.

⚠ **Do not solve this with a cooldown. It does not work, and this guide used to recommend it.**
Letting the window roll over after a saturating test protects only the *next* test; everything
already running inside that same window still inherits the 429s. Worse, a cooldown placed after an
assertion is skipped on failure — the run that saturates the bucket most is the run that does not
drain it. Measured on this repository: 88 serialized E2E tests average ~2-5 requests/second against
a 15/second allowance, so the suite is not too big for the budget; it fails in **bursts**, where ~25
fast tests at ~6 requests each fill a 10-second window between them. One browser test is a dozen or
more `/spark` calls on its own.

**Raise the budget for the test environment instead, and leave the limiter wired.** Since
`SparkRateLimiterOptions` binds from the `Spark:RateLimiter` configuration section, a test host can
set `PermitLimit` in the `appsettings.{Environment}.json` it generates. The middleware stays in the
pipeline at the same position and still returns 429, so a test that *wants* a 429 still gets one —
it just has to burst past the **configured** limit rather than a hard-coded number, which also stops
the test being silently defeated the next time that limit changes.

For unit-level work, set `PermitLimit = 1` and a long `Window` — the first request is admitted and
everything after it is rejected, with no timing sensitivity at all.
