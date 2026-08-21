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
    spark.AddAuthorization();

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
attaches none — and MVC's own `[ValidateAntiForgeryToken]` implements a *different* interface
(`IAntiforgeryPolicy`, from `Mvc.ViewFeatures`), which this gate never sees. So the obviously-correct
annotation compiles, reads as protection, and does nothing.

Name the paths to protect:

```csharp
spark.AddAntiforgeryProtection(a =>
{
    a.PathPrefixes = ["/spark", "/connect", "/api"];
    a.RequireAntiforgery = true;
});
```

Inside those prefixes, a mutating request carrying an **ambient** credential (a cookie) is checked
with no per-endpoint annotation.

| Caller | Checked? |
|---|---|
| Cookie-authenticated `POST` | yes |
| Bearer / API-token `POST` | no — CSRF is an attack on ambient authority, and such a caller has no `XSRF-TOKEN` cookie to echo |
| Anonymous `POST` | no — there is no authority to ride |
| Anything with explicit metadata | whatever the metadata says, in both directions |

`RequireAntiforgery` defaults to **false** this preview and becomes true at the next major. Find out
what would break first:

```csharp
a.RequireAntiforgery = true;
a.WarnOnly = true;   // logs what would have been rejected, and lets it through
```

Per endpoint, the framework's own `[RequireAntiforgeryToken]` / `[RequireAntiforgeryToken(false)]`
work in both directions and always win over the default. `[IgnoreAntiforgeryToken]` does **not** —
wrong interface.

This inverts a default rather than stamping metadata because no MVC convention reaches a minimal-API
`MapPost` you wrote. A metadata-based design would cover controllers and leave the rest silently
open, which is the shape of the problem rather than its fix.

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

### What does *not* work

- `[Authorize(Policy = "…")]` **throws at request time**. `UseSpark()` registers a bare
  `AddAuthorization()` with no policies.
- `[Authorize(Roles = "…")]` reads `ClaimTypes.Role`, i.e. ASP.NET Identity roles — **not** Spark
  groups. A group carried as a `group` claim (what the identity provider, the E2E fixtures and module
  certificates all use) is invisible to it. This is worth stating plainly because it is inconsistent:
  test it against a role-shaped fixture and you will conclude interop already works.
- A bare `[Authorize]` does work: it requires an authenticated caller and nothing more.

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
