# Spark 10.0.0-preview.60 — controllers under Spark's rules, an honest group vocabulary, opt-in auth routes

**Packages:** every `MintPlayer.Spark.*` package → `10.0.0-preview.60`, plus a new
`MintPlayer.Spark.Controllers`. `@mintplayer/ng-spark-auth` → **23.0.0** (major),
`@mintplayer/ng-spark` → 22.2.0.

**Issues:** [#298](https://github.com/MintPlayer/MintPlayer.Spark/issues/298),
[#300](https://github.com/MintPlayer/MintPlayer.Spark/issues/300),
[#301](https://github.com/MintPlayer/MintPlayer.Spark/issues/301),
[#302](https://github.com/MintPlayer/MintPlayer.Spark/issues/302),
[#303](https://github.com/MintPlayer/MintPlayer.Spark/issues/303).

One theme: **Spark's rules used to stop at Spark's own surface, and everything outside it was
silently on its own.**

---

## ⚠️ Breaking changes

### 1. `sparkAuthRoutes()` mounts nothing unless you ask

The pages used to mount unless you opted *out*, so every application shipped a registration form
whether or not it wanted one. They are opted into individually now.

```ts
// before
...sparkAuthRoutes()
...sparkAuthRoutes({ localCredentials: 'disabled' })

// after
...sparkAuthRoutes(withLocalLogin(), withRegistration())
...sparkAuthRoutes(withExternalLogin(githubProvider()))
```

`withLocalLogin()` mounts login, two-factor, forgot-password and reset-password — one feature,
because they form a star centred on the login page and removing any proper subset leaves a dangling
link. `withRegistration()` and `withExternalLogin(...)` are separate.

`SPARK_AUTH_ROUTE_PATHS` is now **partial**: it names only the pages that were mounted. Templates
that link across features must guard, because a `[routerLink]` bound to `undefined` silently
navigates to the current route instead of failing.

### 2. `SparkLocalCredentials` defaults to `Disabled`

It was `Full`. Both defaults moved together so the client and server agree; leaving the server at
`Full` while the client mounts nothing is exactly the mismatch `SparkSignInComponent`'s dev-mode
warning exists to catch.

```csharp
spark.AddAuthentication<SparkUser>(
    configure: auth => auth.LocalCredentials = SparkLocalCredentials.Full);
```

An application with neither local credentials nor an external provider now **fails startup**. That
is correct, and loud.

### 3. `security.json`: `Everyone` is gone

Well-known groups are declared **by id**, in a new `wellKnown` block:

```json
{
  "wellKnown": {
    "anonymous":     "00000000-0000-0000-0000-000000000000",
    "authenticated": "a1b2c3d4-0000-0000-0000-00000000000f"
  },
  "groups": { … }
}
```

They used to be matched by *display name*, through `TranslatedString.GetDefaultValue()` — which
returns the first translation in **file order**, not the English one. So `{"en":"Everyone",
"nl":"Iedereen"}` matched and `{"nl":"Iedereen","en":"Everyone"}` did not: reordering two JSON keys
silently changed who could reach what. Meanwhile membership resolution matched a claim against *any*
translation. Two matching rules, sixty lines apart.

A file still declaring a group named `Everyone` and no `wellKnown` block **fails to load**, with the
migration instructions inline.

> Every right you granted to `Everyone` was granted to the public internet. Decide, per right,
> whether that was intended. If it was, move it to `anonymous`. If it was not, move it to
> `authenticated` — **do not delete it**, because type-level rights gate row rules and a deleted
> grant denies signed-in users too.

⚠️ **`Everyone` was the floor for every caller, so moving a grant to `anonymous` alone narrows it.**
The mechanical, behaviour-preserving migration is **one grant becomes two**. Fleet granted
`QueryRead/Company` through `Everyone` and nowhere else, so the anonymous-only migration would have
locked every signed-in Fleet user out of Company.

A reserved id is now excluded from claim-derived membership, so no `IGroupMembershipProvider` can
hand a caller a well-known group by naming it. A comment shipped in #306 claimed this already held;
it did not.

### 4. `IPersistentObjectActions<T>` gains three members

`IsAllowedAsync`, `GetRowFilterAsync` and `GetProtectedAttributesAsync` are on the interface now.
Classes deriving from `DefaultPersistentObjectActions<T>` are unaffected — that is every actions
class the source generator emits and every one in the demos. Hand-written implementers must add the
three permissive defaults.

---

## Controllers under Spark's rules (#300, #301)

New package **`MintPlayer.Spark.Controllers`**:

```csharp
builder.Services.AddSpark(spark =>
{
    spark.AddControllers();
    spark.UseControllers();
});
```

Do not also call `endpoints.MapControllers()`. New analyzer **SPARK010** reports it: the call leaves
no runtime trace Spark can inspect, so a compile-time diagnostic is the only place it can be caught.
It is a warning rather than an error — measured, a second `MapControllers()` reuses MVC's single
endpoint data source, so the route table stays correct; what is lost is Spark's scoping.

### Antiforgery now covers what you write

Spark's CSRF gate fired only on endpoints carrying `IAntiforgeryMetadata`. `AddControllers()`
attaches none, and MVC's own `[ValidateAntiForgeryToken]` implements a *different* interface
(`IAntiforgeryPolicy`), so the obviously-correct annotation did nothing.

```csharp
spark.AddAntiforgeryProtection(a =>
{
    a.PathPrefixes = ["/spark", "/connect", "/api"];
    a.RequireAntiforgery = true;   // a.WarnOnly = true to find out what would break first
});
```

Inside the named prefixes, a mutating request carrying an **ambient** credential is checked with no
per-endpoint annotation. Explicit metadata still wins in both directions. Bearer and API-token
callers stay exempt — CSRF is an attack on ambient authority, and demanding a token of a caller that
constructed its own `Authorization` header protects nothing.

The default is **off this preview** and becomes on at the next major, so nobody's controller writes
start 400ing on upgrade.

This inverts a default rather than stamping metadata, deliberately: no MVC convention reaches a
minimal-API `MapPost` you wrote, so a stamping design would cover controllers and leave the rest
silently open — the shape of the defect rather than its fix.

### Reuse a row rule outside `/spark`

```csharp
public MyController(ISparkRowRule<Repository> rule) { … }

var visible = await rule.ApplyAsync(session.Query<Repository>(), "Query");
```

`ApplyAsync` pushes the filter into the query where it is translatable, then applies the compiled
predicate **and** the per-row hook. One call, because the two halves cannot be forgotten separately:
`GetRowFilterAsync` returns `null` — meaning *unrestricted* — for a type that expresses its policy
through `IsAllowedAsync` alone, so a caller consuming only the filter sees every row while believing
it applied the rule. `GetFilterAsync` remains available as a documented sharp tool.

It delegates into the same request-scoped `RowSecurity`, so hooks stay bounded at one invocation per
`(type, action)` per request — the bound that keeps an I/O-doing hook clear of RavenDB's 30-request
session cap.

**Scope:** this governs which *rows* a caller sees. It does not redact attributes of your own DTOs.

### `[SparkAuthorize]`

```csharp
[HttpPost("tokens")]
[SparkAuthorize("New", nameof(UploadToken))]
public Task<IActionResult> Create() { … }
```

Authorizes against the same `security.json` right the persistent-object endpoints check — the same
string, so a controller and its Spark equivalent provably agree. A `Group = "…"` form exists and is
secondary: rights are the product's model and are changeable without a redeploy.

Note that `[Authorize(Roles = …)]` reads ASP.NET Identity roles, **not** Spark groups. A group
carried as a `group` claim is invisible to `RequireRole`, and a bare `[Authorize(Policy = …)]` throws
because `UseSpark()` registers no policies.

---

## Visibility (#298)

- `GET /spark/permissions/{type}` reports **`canQuery`**. `Query` and `Read` are independently
  grantable — `Query/Person` alone lists rows while refusing a by-id load — and the combined
  `QueryRead` bundled them invisibly, so the one right it added beyond a reader's expectation was
  precisely the one introspection never mentioned.
- Every startup prints which rights an anonymous caller holds, **including when the answer is
  nothing**. Silence is indistinguishable from the check not running.
- `--spark-verify-security` fails CI when that surface changes, against a baseline written by
  `--spark-synchronize-security`. Both run without a database.
- A combined action used in a **denial** is now rejected at load. Expansion is grant-only, so
  `EditNewDelete/Person` as a denial denied nothing — symmetric syntax, asymmetric semantics.
- Malformed resources and duplicate right ids are rejected at load.

## Sign-in page (#302, #303)

`SparkSignInComponent` gains:

- a **`providerTemplate`** input consumed via `*ngTemplateOutlet`, rendered once per provider with
  `{ $implicit: provider, signIn: () => void }`. Passing the closure is the point: a consumer never
  touches `provider.scheme`, so #303's failure mode is unreachable by construction. Reachable when
  you *host* the component — the router projects no content into a routed component.
- a **`returnUrl`** input, falling back to a validated `?returnUrl=` query parameter.
- provider **presentation** declarations — `withExternalLogin(githubProvider())` supplies an icon, a
  label and an order, keyed by scheme. It decorates; the server stays authoritative over which
  providers exist. A scheme the server reports with no declaration gets a default button, so adding
  a provider server-side never yields a blank page.

## Fixed in passing

- HR's `security.json` had two rights sharing an id. Harmless today because nothing reads
  `Right.Id`, which is why it is now rejected at load.
