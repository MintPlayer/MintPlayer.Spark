# Spark 11.0.0-preview.87 — CSRF protection stops being opt-in

**Packages:** `MintPlayer.Spark` → `11.0.0-preview.87`, `MintPlayer.Spark.Authorization` →
`11.0.0-preview.86`, `MintPlayer.Spark.Client.Authorization` → `11.0.0-preview.85`,
`MintPlayer.Spark.Webhooks.GitHub` → `11.0.0-preview.85`. No npm package changes.

One theme: **Spark's antiforgery gate protected the endpoints that asked for it and nothing else,
and the switch that was supposed to cover everything else had never been turned on.**

---

## ⚠️ Breaking changes

### 1. `SparkAntiforgeryOptions.RequireAntiforgery` now defaults to `true`

This was always the plan — the option's own documentation said "the default becomes `true` at the
next major", and the move to `net11.0` is that major.

Within `PathPrefixes` (`/spark` and `/connect` by default), a mutating request that carries an
**ambient** credential — a cookie the browser attaches by itself — is now checked without any
per-endpoint annotation. Explicit metadata still wins in both directions, so
`[RequireAntiforgeryToken(false)]` and `DisableAntiforgery()` remain the escape hatches.

**Who this affects:** any application that maps its own cookie-authenticated `POST`/`PUT`/`PATCH`/
`DELETE` under `/spark` or `/connect`, or that has added its own prefix. Those calls will start
answering **400** unless the client echoes the `XSRF-TOKEN` cookie in `X-XSRF-TOKEN`. An Angular
client built on `@mintplayer/ng-spark-auth` already does — `withSparkAuth()` configures it.

**Why it is not a smaller change.** The alternative was to stamp metadata onto framework endpoints
and leave the default alone. Metadata can only be attached by something that knows the endpoint
exists, so that design covers the framework's own routes and leaves *your* `MapPost` open — which is
the shape of the problem rather than its fix.

**Migration.** Turn it off, deploy, look, turn it back on:

```csharp
spark.AddAntiforgeryProtection(antiforgery =>
{
    antiforgery.PathPrefixes = ["/spark", "/connect", "/api"];
    antiforgery.WarnOnly = true;   // logs what would have been rejected, and allows it
});
```

⚠️ **`WarnOnly` genuinely works now.** It lives inside the branch guarded by `RequireAntiforgery`, so
while that was `false` it logged nothing and always would have. An application that was waiting for a
clean log before turning the flag on was waiting for a log that could never be written.

### 2. `POST /spark/auth/login` requires an antiforgery token

It was deliberately excluded, on the reasoning that an anonymous caller has no `XSRF-TOKEN` cookie to
present. That premise was wrong: `UseSpark()` mints the cookie on **every** response, anonymous ones
included, so anyone who has loaded the SPA at all is holding one.

What the exclusion left open is **login CSRF** — the attacker's page POSTs the attacker's own
credentials, the victim's browser quietly acquires a session belonging to the attacker, and
everything the victim does next lands in an account the attacker can log into and read. It is the
one forgery worth mounting against an endpoint nobody is signed in to yet, because its *effect* is to
create ambient authority in the victim's browser.

- **Browser clients need no change.** The login POST carries the anonymous-bound cookie already.
- **`SparkClient.LoginAsync` needs no change** — it now primes a token itself (one warmup `GET
  /spark`, cached for the client's lifetime).
- ⚠️ **A hand-rolled client that POSTs `/spark/auth/login` directly must now obtain a token first.**
  Issue any GET against the host, read the `XSRF-TOKEN` cookie, echo it in `X-XSRF-TOKEN`.

`/register`, `/refresh` and `/resendConfirmationEmail` are deliberately **not** gated: they are
anonymous and carry no ambient authority, so a forged request achieves nothing an attacker could not
achieve by calling them from their own server.

### 3. `/spark/github/dev-ws` answers GET only

It was mapped with a bare `endpoints.Map(...)`, which constrains no method, so it matched every
mutating verb. A WebSocket handshake is a GET by definition. Nothing was exploitable through it — the
handler already refused non-WebSocket requests — but it was a mutating endpoint under `/spark` that
no gate could ever cover.

---

## Fixed

### `POST /spark/auth/csrf-refresh` is now explicitly exempt

It used to be exempt by accident, through carrying no metadata. Under the new default it is a
cookie-authenticated POST under `/spark`, so the inverted default would have caught it — and that
would have been unrecoverable rather than merely strict.

An antiforgery token is bound to the principal it was minted for. A client that has just signed in or
out is holding a stale one by definition, and `csrf-refresh` exists to replace it. Requiring a valid
token in order to be issued a valid token is a deadlock: that client cannot make another mutating
call for the rest of its session.

### Three comments that were teaching the wrong thing

- `SparkMiddleware.cs` and `SparkAntiforgeryMiddleware` claimed the built-in `UseAntiforgery()` "was
  narrowed in 8.0.1 to validate only form-content bodies". There was no 8.0.1 source change, and the
  middleware has never keyed on content type — it filters by HTTP method (POST/PUT/PATCH), which
  shipped in 8.0.0. **A JSON POST is validated by it.** The real reasons Spark ships its own gate are
  that the built-in one *never rejects* (it records `IAntiforgeryValidationFeature` and calls the
  next delegate, leaving rejection to whoever binds the form) and that it skips `DELETE` entirely.
- `SparkAntiforgeryOptions` said `[ValidateAntiForgeryToken]` "implements `IAntiforgeryPolicy`". It
  implements `IFilterFactory, IOrderedFilter` — neither marker. The filter it resolves from DI
  carries `IAntiforgeryPolicy`. The conclusion was right; the mechanism was one indirection off.

---

## `MintPlayer.Spark.Testing` — the local test suite stops losing tests to a timeout

**`MintPlayer.Spark.Testing` → `11.0.0-preview.85`.** `SparkTestDriver` now deletes its test database
itself with `TimeToWaitForConfirmation = TimeSpan.Zero` before disposing the store.

Without it, a full local run loses 20-35 tests out of ~2,500 — always in teardown, never the same
tests twice, and never the tests that were actually changed:

```
RavenException : TimeoutException: Waited for 00:00:15 for task with index N to complete.
Last commit index is: M.
```

**The cause is CPU starvation**, established by reproducing it on demand. It takes three ingredients
together, and removing any one stops it reproducing: a saturated machine, concurrent teardowns (four
workers, matching `maxParallelThreads: 0.5x` on eight cores), and — the one that is easy to miss —
**each database carrying an index, documents and a query**. An idle database deletes in 0.1 s even on
a fully starved machine; one with an index takes **21.8 s**. Deleting is cheap; deleting something
the server is still working on is not.

⚠️ **Zero is not a short timeout — it means "don't wait".** Server-side,
`WaitForDeletionToComplete` computes `remaining = timeout - elapsed`, which with zero is already
negative, so the confirmation wait is never entered. The raft command is still submitted and the
database is still deleted; only the acknowledgement is skipped, and the exception is never raised
rather than caught. Safe because the database name is unique per test case and the embedded server is
in-memory and dies with the process.

⚠️ **The 15 s is not configurable from the server.** The obvious reading —
`Cluster.OperationTimeoutInSec`, which also defaults to 15 — governs database *creation*.
`AdminDatabasesHandler.WaitForDeletionToComplete` resolves its budget from
`parameters.TimeToWaitForConfirmation ?? TimeSpan.FromSeconds(15)`, a hard-coded fallback, and
`RavenTestDriver`'s `AfterDispose` handler passes no parameter. Sending the operation yourself is the
only way to set it; the driver's own delete then throws `DatabaseDoesNotExistException`, which its
handler already swallows.

⚠️ **Do not re-propose these — each was measured and rejected:** skipping the delete entirely (17%
slower; undeleted databases cost more than the deletes saved), backgrounding `Dispose()` in a
`Task.Run` (breaks the suite on an idle machine, 89-147 unrelated failures per run), serialising
deletes behind a `SemaphoreSlim` (teardown 16-19 s → 35-47 s), and catching the timeout rather than
preventing it.

---

## Worth knowing on .NET 11

- **`[RequireAntiforgeryToken]` is now the supported annotation on MVC controllers.**
  `AntiforgeryApplicationModelProvider` bridges endpoint metadata into an MVC filter that actually
  rejects. ⚠️ Applying it *together with* `[ValidateAntiForgeryToken]` on the same action **throws at
  startup**.
- **A second CSRF middleware ships in `DefaultBuilder`** (`CsrfProtectionMiddleware`,
  origin/`Sec-Fetch-Site`-based). Like the antiforgery middleware, it records a verdict and calls
  `next` without rejecting.

---

## Tests you can read instead of this document

- `tests/MintPlayer.Spark.Tests/Extensions/XsrfSurfaceTests.cs` — every mutating framework endpoint
  and its position, as exact sets. It fails when an endpoint is *added* without stating one.
- `tests/MintPlayer.Spark.E2E.Tests/Security/XsrfEnforcementTests.cs` — the same claims driven over
  real HTTPS with a real cookie jar, with a failing control beside every passing case.
