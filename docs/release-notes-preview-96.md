# Spark 11.0.0-preview.96 — the XSRF-TOKEN cookie comes from MintPlayer.AspNetCore.SpaServices.Xsrf (#452)

**Packages:** `MintPlayer.Spark`, `MintPlayer.Spark.Authorization` and `MintPlayer.Spark.Messaging` →
`11.0.0-preview.96`. npm:
`@mintplayer/ng-spark-auth` → `22.18.0`. No other package changed. The majors do not move: the packages still target .NET 11 and Angular 22.

`MintPlayer.Spark` now depends on `MintPlayer.AspNetCore.SpaServices.Xsrf` `11.0.0-rc.3` and calls its
`UseAntiforgeryGenerator()` from `UseSpark()`, instead of minting the `XSRF-TOKEN` cookie with its own
copy of the code. The cookie's flags are unchanged: readable by script (`HttpOnly=false`),
`SameSite=Strict`, `Secure` when the request is HTTPS, `Path=/`. Nothing needs to be configured.

The decisions and their evidence are in `docs/xsrf_minting_PRD.md` and `docs/xsrf_minting_plan.md`.

---

## ⚠️ Behaviour changes

### 1. A response that carries the cookie is never shared-cacheable

When the package adds the cookie to a response, it rewrites that response's `Cache-Control` to
`private`, keeping the directives the endpoint set (`public, max-age=300` becomes
`private, max-age=300`). A per-user `Set-Cookie` in a shared cache would hand one user's token to the
next, so this is the correct default. Spark's own mint stamped `no-cache, no-store` instead, which
was stricter still.

**Who is affected:** an application endpoint that sets `Cache-Control: public` on purpose, for a CDN
or an image proxy, and is served by a pipeline that calls `UseSpark()`.

**Migration.** Opt the endpoint out of the cookie:

```csharp
[SkipXsrfToken]                       // controller or action
public class BadgeController : Controller { … }

app.MapGet("/feed.xml", …).SkipXsrfToken();   // minimal API
```

Both come from `MintPlayer.AspNetCore.SpaServices.Xsrf`, which `MintPlayer.Spark` now references.
Do **not** use `.ShortCircuit()` for this. It skips every middleware after routing, the rate limiter
and authorization included.

### 2. The token is minted after the handler, not before it

The cookie is written in `Response.OnStarting`, after the endpoint ran.

- **Sign-in:** the token on the sign-in response is bound to the user who just signed in, so the
  next mutating call succeeds without `POST /spark/auth/csrf-refresh`. Before, it was bound to the
  anonymous caller and that call was refused with a 400.
- **Sign-out:** unchanged. The next mutating call still needs `csrf-refresh` first, because
  ASP.NET Core's `SignOutAsync` does not reset `HttpContext.User`.

`@mintplayer/ng-spark-auth` keeps calling `csrf-refresh` after sign-in and sign-out, so an Angular
client needs no change. A programmatic client may drop the refresh after sign-in.

### 3. The antiforgery gate's 400 now carries a fresh token

The generator is registered above Spark's antiforgery gate, so a request refused for a missing or
stale token gets a new `XSRF-TOKEN` cookie on the 400 itself. A client can recover by retrying with
the new token. Before, the refusal carried no cookie, and only `csrf-refresh` or another request
could produce one.

### 4. A failing mint no longer fails the response

If generating the token throws, the package logs the exception and sends the response without the
cookie. Before, the exception reached `UseExceptionHandler` and the request became a 500. When the
cookie is written without `Secure`, the package logs one warning (typically a TLS-terminating proxy
whose forwarded headers are not applied).

---

## `@mintplayer/ng-spark-auth` 22.18.0

External sign-in and account linking (`loginWithProvider`, `linkProvider`) now call `csrfRefresh()`
before re-reading the session, like `login()` does. They used to rely on the next response
happening to mint a token. A failing refresh does not fail the sign-in, which already happened in
the popup.

---

## `MintPlayer.Spark.Authorization`: a migration that could fail on a busy server

`M_202610041800_UserDocumentsMovedAssembly` patches by query. RavenDB refuses a bulk operation on a stale
index outright ("Cannot perform bulk operation. Index is stale.") unless the operation allows a wait,
and under load the auto-index behind the query can still be catching up. The migration now waits up
to five minutes for it. The same fix was applied to the CodeCoverage and HR migrations. A local test
sweep hit this as a failure in `ApiTokenRepositoryIdListMigrationTests`.

---

## `MintPlayer.Spark.Messaging`: shutdown could throw

`MessageQueueRouter` is disposed twice on shutdown, once by `MessageSubscriptionManager.StopAsync` and
once by the container. When the two overlapped, the second hit a `NullReferenceException` and host
shutdown reported an error. Disposal now takes ownership of its token source atomically.
`MessageQueueRouterTests.Concurrent_disposal_is_safe` reproduced the exception before the fix.

---

## Repository

- Test projects assert with `MintPlayer.Assertions` only. The remaining xUnit `Assert.*` calls were
  converted, and `Microsoft.CodeAnalysis.BannedApiAnalyzers` now makes `Xunit.Assert` a build error
  (RS0030) in every `*.Tests` project (`Directory.Build.targets`, `BannedSymbols.Tests.txt`).
- 32 assertions (24 already in the suite, 8 produced by the conversion) of the shape `x?.Value.Should().Be(…)` silently did nothing when `x` was null: `?.`
  skips the whole chain, assertion included. Their receivers are now parenthesised, so a null fails.
- CodeCoverage's badge endpoint is marked `[SkipXsrfToken]`, so the anonymous badge stays
  `public, max-age=300` and GitHub's image proxy keeps caching it.
