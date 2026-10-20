# Controllers under Spark's rules

Spark maps its own endpoints and governs them completely — authentication schemes, `security.json`
rights, row rules, attribute redaction, CSRF. An application's own controllers sit *beside* that
unless you say otherwise: they authenticate and authorize, but nothing Spark configures is scoped to
them.

This guide is about closing that gap. It ships in `MintPlayer.Spark.Controllers`.

## Mounting

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MyContext>();

    spark.AddControllers();     // MVC services
    spark.UseControllers();     // mount them through Spark
});

var app = builder.Build();
app.UseRouting();
app.UseSpark();
app.MapSpark();                 // controllers are mounted here, with everything else
```

`AddControllers` takes an `Action<IMvcBuilder>`, so MVC's own configuration surface stays reachable:

```csharp
spark.AddControllers(mvc => mvc.AddJsonOptions(o => o.JsonSerializerOptions.WriteIndented = true));
```

Calling `builder.Services.AddControllers()` yourself beforehand is fine — MVC's registration is
idempotent and your configuration survives.

### Do not also call `MapControllers()`

Analyzer **SPARK010** reports it. Controllers mapped with the framework's own extension are
invisible to Spark: the antiforgery gate is scoped to paths Spark was told about, and there is no way
to authorize an action against a `security.json` right.

This is a compile-time rule because there is no runtime one to write. By the time `UseSpark()`
executes, `MapControllers()` has already run on your own endpoint builder and the resulting endpoints
are indistinguishable from any others — nothing Spark can inspect says whether you opted in. At
compile time the call is an ordinary invocation in your own source. (SPARK004 is the same asymmetry
in the other direction: middleware *order* is invisible at runtime and plain at compile time.)

Suppress it if you genuinely want the raw call. That is the entire difference from before, where the
same decision was made by not knowing.

## CSRF

Spark's antiforgery gate fires on endpoints carrying `IAntiforgeryMetadata`. `AddControllers()`
attaches none, and MVC's own `[ValidateAntiForgeryToken]` does not either — it implements
`IFilterFactory, IOrderedFilter`, and the filter it resolves from DI carries an
`IAntiforgeryPolicy` marker that exists only for MVC's own filter resolution. Routing never sees any
of it. So the obviously-correct annotation compiles, reads as protection, and historically did
nothing.

Since 11.0.0 the gate also fires by default on any mutating request inside `PathPrefixes` that
carries an ambient credential, so the only thing most applications need to configure is which paths
count as theirs — the default is Spark's own surfaces, `/spark` and `/connect`:

```csharp
spark.AddAntiforgeryProtection(a =>
{
    a.PathPrefixes = ["/spark", "/connect", "/api"];   // assigning REPLACES the defaults
});
```

Inside those prefixes, a mutating request carrying an **ambient** credential (a cookie) is checked
with no per-endpoint annotation.

| Caller | Checked? |
|---|---|
| Cookie-authenticated `POST` | yes |
| Bearer / API-token `POST` | no — CSRF is an attack on ambient authority, and such a caller has no `XSRF-TOKEN` cookie to echo |
| Anonymous `POST` with a valid [visitor cookie](#visitors) | **yes** — the cookie is a guest's basket and queue entry, and a cross-site page can make the browser send it |
| Anonymous `POST` without one (or with junk in its place) | no — there is no authority to ride |
| Anything with explicit metadata | whatever the metadata says, in both directions |

The visitor test reads a cookie that *unprotected*, never the raw header, so attaching junk can neither
switch the check on nor off. An anonymous browser already holds an `XSRF-TOKEN` (it is minted on every
response) and Angular's `HttpClient` echoes it, so a guest's calls pass without any client change.

⚠️ **`RequireAntiforgery` defaults to `true` as of 11.0.0.** It was `false` through the 10.x line, so
on upgrade a mutating request you map yourself inside those prefixes starts being checked. If that
breaks something, see what would be rejected before you decide:

```csharp
a.WarnOnly = true;   // logs what would have been rejected, and lets it through
```

⚠️ `WarnOnly` only does anything while `RequireAntiforgery` is on — it lives inside that branch. For
the whole 10.x line it therefore logged nothing at all, so "the log is clean" was never evidence.

Per endpoint, the framework's own `[RequireAntiforgeryToken]` / `[RequireAntiforgeryToken(false)]`
work in both directions and always win over the default.

⚠️ **`[IgnoreAntiforgeryToken]` works on .NET 11 and did not before.** `AntiforgeryApplicationModelProvider`
now maps it onto `AntiforgeryMetadata.ValidationNotRequired`, so on a controller it is equivalent to
`[RequireAntiforgeryToken(false)]`. Prefer the framework attribute anyway, for one concrete reason:
applying `[ValidateAntiForgeryToken]` and `[RequireAntiforgeryToken]` to the same action **throws at
startup** on .NET 11, and keeping to one vocabulary is how you avoid meeting that.

This inverts a default rather than stamping metadata because no MVC convention reaches a minimal-API
`MapPost` you wrote. A metadata-based design would cover controllers and leave the rest silently
open, which is the shape of the problem rather than its fix.

## Visitors

A guest's state has to survive a refresh before anyone signs in: a basket, a place in a waiting room
(commerce PRD X1). Spark gives every request an `ISparkVisitor`, injected or read from
`HttpContext.Features`:

```csharp
app.MapPost("/api/basket/lines", async (AddLine line, ISparkVisitor visitor, Baskets baskets) =>
{
    var owner = visitor.UserId is { } userId
        ? "user:" + userId
        : "visitor:" + await visitor.EnsureAsync();   // issues the cookie on the first action
    await baskets.AddAsync(owner, line);
    return Results.Ok();
});
```

| Member | |
|---|---|
| `VisitorId` | the id from a visitor cookie that unprotected; `null` otherwise |
| `UserId` | the signed-in user, if any |
| `IdentityKey` | `user:{id}`, else `visitor:{id}`, else `ip:{address}` (IPv6 grouped to its /64) — what per-identity limits key on |
| `EnsureAsync()` | the visitor id, issuing the cookie first if there is none |

- **The cookie** `.Spark.Visitor` is HttpOnly, `SameSite=Lax`, `Path=/`, Secure on HTTPS, and lives
  `Spark:Visitor:Lifetime` (180 days). Its value is protected by Spark's Data Protection key ring
  (purpose `MintPlayer.Spark.Visitor.v1`), so it survives a redeploy with the ring, and a forged,
  tampered or expired value is simply no visitor.
- **Only an action issues it.** `EnsureAsync()` throws on `GET`, `HEAD` and `OPTIONS`: it is a
  strictly necessary cookie, set when the guest does something that needs it, never by visiting a
  page. It also throws for a signed-in request, which already has `UserId`.
- **It is not an identity.** A visitor stays anonymous to every authorization rule, the
  `[SparkAuthorize]` default and the live socket's ticket expiry. It is ambient authority over the
  guest's own state, though, which is why an anonymous `POST` that carries it is
  [checked for CSRF](#csrf).
- **Payment-provider return URLs** that `POST` cross-site do not carry a `Lax` cookie. Identify those
  callers with a signed order token, not the visitor.

### Merging into the account on sign-in

Implement `ISparkVisitorMergeHandler` to move what a guest built up onto the account it signs in to,
and register it (`services.AddScoped<ISparkVisitorMergeHandler, BasketMerge>()`; several may be
registered):

```csharp
internal sealed class BasketMerge : ISparkVisitorMergeHandler
{
    public async Task MergeAsync(string visitorId, string userId, IAsyncDocumentSession session, CancellationToken ct)
    {
        // Load both baskets through `session`, keep the union of lines, store. Do not SaveChanges.
    }
}
```

- **When:** on every fresh sign-in, from Authorization's sign-in hook (password, second factor,
  recovery code, external login, passkey), which then deletes the visitor cookie. A hook failure never
  refuses a sign-in, so Spark also merges **on sight**: the first signed-in request that still carries
  a visitor cookie merges it, then deletes the cookie.
- **Exactly once:** the handlers write through one **cluster-wide** session that also creates the
  compare-exchange value `visitor-merge/{visitorId}`. When the hook and the next request race, the
  first commit wins and the other's writes never land. The record expires after the cookie's
  lifetime.
- **What the session allows:** documents and compare-exchange values
  (`session.Advanced.ClusterTransaction`); a cluster-wide session refuses patches, counters,
  attachments and time series. Do not call `SaveChangesAsync`.
- A handler that throws leaves nothing recorded and keeps the cookie, so the next signed-in request
  tries again. Keep handlers idempotent all the same.

## Authorizing against `security.json`

```csharp
[HttpPost("tokens")]
[SparkAuthorize("New", nameof(UploadToken))]
public Task<IActionResult> Create() { … }
```

It demands the same right string the persistent-object endpoints check, so a controller and its Spark
equivalent provably agree rather than agreeing by convention — and an operator can change who holds
the right without a redeploy.

A group form exists and is secondary, because a group is an implementation detail of who holds a
right:

```csharp
[SparkAuthorize(Group = "Administrators")]
```

`[AllowAnonymous]` overrides both, as it does for any authorization policy.

⚠️ **An `anonymous` grant takes effect** (since #453). The attribute names its own policy,
`SparkAuthorizeAttribute.PolicyName`, whose only requirement always passes, so security.json alone
decides — anonymous callers included. Before, it named no policy, ASP.NET Core folded in its default
require-authenticated policy, and every anonymous caller got a 401 before security.json was consulted.
A refused anonymous caller is still challenged (401). So check what the `anonymous` group holds before
putting `[SparkAuthorize]` on an endpoint that used to rely on that accident; pair it with a bare
`[Authorize]` if sign-in must be required regardless.

`AddSpark` registers that policy. A host that wires `AddAuthorization` by hand without `AddSpark`
(unit tests, typically) must call `options => SparkAuthorizeAttribute.AddPolicy(options)` itself, or the
first request throws "policy not found".

### What does *not* work

- `[Authorize(Policy = "…")]` **throws at request time**. `UseSpark()` registers
  ASP.NET Core's own `AddAuthorization()` with none of your policies (only `[SparkAuthorize]`'s own).
- `[Authorize(Roles = "…")]` reads `ClaimTypes.Role`, i.e. ASP.NET Identity roles — **not** Spark
  groups. A group carried as a `group` claim (what the identity provider, the E2E fixtures and module
  certificates all use) is invisible to it. This is worth stating plainly because it is inconsistent:
  test it against a role-shaped fixture and you will conclude interop already works.
- A bare `[Authorize]` does work: it requires an authenticated caller and nothing more. So does a
  schemes-only `[Authorize(AuthenticationSchemes = "…")]`, which only picks how the caller is
  authenticated.

**SPARK020** (error) enforces the first two at build time: ASP.NET Core's own `AuthorizeAttribute`
with a policy (constructor argument or `Policy =`) or `Roles =` fails the build, on a class, a
method or a route-handler lambda. It judges only that exact type — `[SparkAuthorize]` derives from
it and sets `Policy` itself, and a subclass of your own is your decision.

The same rule covers the endpoint-convention form, on a route and on a `MapGroup(…)` group alike:

```csharp
app.MapGet("/uploads", …).RequireAuthorization("Administrators");                          // SPARK020
group.RequireAuthorization(new AuthorizeAttribute { Roles = "Administrators" });          // SPARK020
app.MapGet("/uploads", …).RequireAuthorization(new SparkAuthorizeAttribute("New", nameof(Upload))); // fine
app.MapGet("/me", …).RequireAuthorization();                                              // fine: sign-in only
```

Any policy name is reported, whatever expression produces it, as is a `new AuthorizeAttribute(…)`
argument carrying a policy or `Roles`. Not reported: `RequireAuthorization()` with no arguments (the
default policy, i.e. sign-in only), a schemes-only `AuthorizeAttribute`, and the `AuthorizationPolicy`
/ `Action<AuthorizationPolicyBuilder>` overloads, which hand over a policy object instead of looking
one up by name. Be aware that `policy.RequireRole(…)` inside such a builder has exactly the
`Roles` blindness described above — it sees role claims, not Spark's `group` claims — it is only
not reported, because a policy built in code is an explicit choice. Only ASP.NET Core's own
`RequireAuthorization` is judged; a method of yours with the same name is not.

## Reusing a row rule

An entity's row rule lives on its actions class and used to be reachable only through `/spark`. An
application with a mixed `/spark` + `/api` surface therefore wrote the same predicate two or three
times and kept the copies in step by hand.

```csharp
public sealed class RepositoriesController(
    ISparkRowRule<Repository> rule,
    IAsyncDocumentSession session) : ControllerBase
{
    [HttpGet]
    [SparkAuthorize("Query", nameof(Repository))]
    public async Task<IActionResult> List()
        => Ok(await rule.ApplyAsync(session.Query<Repository>(), "Query"));
}
```

`ApplyAsync` pushes the filter into the query where it is translatable, then narrows what comes back
by the compiled predicate **and** the per-row hook.

**Use it rather than composing the filter yourself.** `GetFilterAsync` returns the raw predicate, and
`null` means *unrestricted* — never coalesce it to `x => false`, which inverts the rule. Worse,
`null` does not mean "this type has no rule": a type that expresses its policy through
`IsAllowedAsync` alone returns `null` and is not unrestricted at all. A caller composing the filter
by hand is correct until someone adds an `IsAllowedAsync` override, at which point it silently stops
filtering.

For a projecting query — a static index, a `[FromIndex]` view type — pass it directly:

```csharp
await rule.ApplyAsync(session.Query<VRepository, Repositories_Overview>().ProjectInto<VRepository>(), "Query");
```

The rule is written against the document, so it cannot compose into a projection; the documents
behind the surviving rows are batch-loaded and judged instead. That is a real cost on a large
collection and it is the only correct answer — judging ownership from a partial view is how a filter
silently passes everything. A projected row must carry an `Id`, or nothing can be correlated back and
no rows are returned.

Hooks are invoked at most once per `(type, action)` per request, shared with the Spark pipeline's own
memo. A request that hits both a controller and `/spark` for the same type pays for one invocation,
not two — which matters, because RavenDB caps a session at 30 requests.

**`ISparkRowRule<T>` governs which *rows* a caller may see. It does not redact attributes of your own
DTOs.** `GetProtectedAttributesAsync` will tell you what must be hidden, but applying that to a shape
Spark did not map is yours to do. Nothing here makes an arbitrary API endpoint safe on its own.
