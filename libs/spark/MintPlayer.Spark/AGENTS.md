---
remarks: "Distributed by the MintPlayer.Spark NuGet package and copied into each consuming project on build. In a consuming project this file is kept in sync with the package on every build — do NOT edit it there; edit the source in MintPlayer.Spark. Commit it to source control."
generated-by: "MintPlayer.Spark build target (CopySparkAgentsGuide)"
---

# MintPlayer.Spark — agent guide

How this framework works, for someone writing application code against it. Read the **Hard rules**
first; most of them are places where the obvious thing compiles, runs, and is silently wrong.

For writing *tests*, the `MintPlayer.Spark.Testing` package ships its own `AGENTS.md`.

---

## Hard rules (read first)

- **The model is a set of JSON files, and it is generated.** `App_Data/Model/*.json` is written by
  `--spark-synchronize-model` from your entity classes and `SparkContext`. Hand-edits to *generated*
  fields are overwritten. [Details](#the-model).
- **A hand-edited model file that nobody regenerates will stop the app from starting.**
  `App_Data/modelHashes.json` must match; outside Development a mismatch is fatal.
- **`App_Data/security.json` is mandatory.** Authorization is not optional and there is no code-level
  way to switch it off — a missing or malformed file refuses startup.
  [Details](#authorization).
- **`Query` and `Read` are separate rights**, and the difference is visible: `Query` without `Read`
  lists rows in a grid whose first column is *not* a link. That is the right model whenever a row
  has no detail page to open.
- **A 404 does not mean "not found".** Spark answers 404 for *denied* as well as *absent*, so it is
  not an existence oracle. Never write client code that infers absence from a 404.
- **`{EntityName}Actions` is discovered by simple type name across every loaded assembly**, and the
  answer is cached process-wide. Two classes with the same simple name race. Name them uniquely.
- **A query alias identifies exactly one query.** Collisions throw at startup. An omitted alias is
  *derived* from the name (`GetStocks` → `stocks`), so it can collide with one you declared
  elsewhere.
- **Do not call `UseAuthentication()`, `UseAuthorization()`, `UseAntiforgery()` or `MapControllers()`
  yourself.** `UseSpark()` orders all of it; adding your own copy changes that order. Analyzers
  SPARK004 and SPARK010 catch the common cases.

---

## The shape of an application

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();   // required — the entity surface
    spark.AddActions();                    // generated: discovers your {Entity}Actions classes
    // optional modules:
    // spark.AddAuthentication<SparkUser>();  spark.AddControllers();  spark.UseControllers();
    // spark.AddMessaging();  spark.AddCron();  spark.AddMigrations();  spark.AddReplication();
    // spark.AddIdentityProvider();  spark.AddRateLimiter();  spark.AddGithubWebhooks();
});

// Build-time commands. Each returns true when it handled the invocation and the host should stop.
if (builder.SynchronizeSparkModelsIfRequested(args)) return;    // --spark-synchronize-model / --spark-verify-model
if (builder.InitializeSparkSecurityIfRequested(args)) return;   // --spark-init-security
if (builder.VerifySparkSecurityIfRequested(args)) return;       // --spark-verify-security / --spark-synchronize-security

var app = builder.Build();
app.UseRouting();
app.UseSpark();                            // authn, authz, antiforgery, XSRF cookie — in order
app.UseEndpoints(e => e.MapSpark());
```

The build-time commands open **no database connection**, which is what lets them run in CI.

### The context

```csharp
public class MySparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Car> Cars => Session.Query<Car>();
}
```

Each property is a queryable collection. The synchronizer reflects over the property **types** and
never invokes a getter, so it needs no session — but that also means a property here is what makes
a type part of the model. A type not reachable from the context is not a PersistentObject.

---

## The model

`App_Data/Model/{Entity}.json` describes each entity type: its attributes, tabs, groups, queries,
breadcrumb, and its `id`. Routes use that **id**, not the type name.

```
dotnet run -- --spark-synchronize-model    # regenerate from the entity classes
dotnet run -- --spark-verify-model         # CI gate; exits 3 on drift, writes nothing
```

**Synchronization is a fixed point, and must stay one.** Running it twice produces byte-identical
output. Anything that derives-on-load and then writes back breaks that, and the damage is invisible:
an undeclared JSON property is destroyed on the first synchronize and runs 2–3 are identical, so the
loss is itself a fixed point that no gate can see. There is no `[JsonExtensionData]` anywhere.

**Hand-editable fields are preserved** — display names, `showedOn`, aliases, query definitions,
explicit `alias` values. Generated structural fields are not. If you need a field to survive, it has
to be a *declared* property on the model type.

**`modelHashes.json` gates startup.** Outside Development a mismatch stops the process before it
serves a request, because a drifted model surfaces as missing columns and values silently dropped on
save — data loss wearing a configuration mistake's clothes. In Development it warns instead.

---

## Actions classes

`{EntityName}Actions : DefaultPersistentObjectActions<TEntity>`, discovered by convention and
registered by the generated `AddActions()`.

```csharp
public partial class PersonActions : DefaultPersistentObjectActions<Person>
{
    [Inject] private readonly IMessageBus messageBus;   // ctor generated by the source generator

    public override Task OnBeforeSaveAsync(PersistentObject obj, Person entity) { … }
    public override Task OnAfterSaveAsync(PersistentObject obj, Person entity) { … }
}
```

`partial` is required — `[Inject]` generates the constructor. Nullable fields (`IService?`) get a
`= null` default.

The hooks worth knowing:

| Hook | Purpose |
|---|---|
| `OnLoadAsync` | what a detail page loads for an id |
| `OnSaveAsync` / `OnBeforeSaveAsync` / `OnAfterSaveAsync` | write pipeline |
| `OnDeleteAsync` / `OnBeforeDeleteAsync` | delete pipeline |
| `GetDefaultIncludes` | eager-load references in one round trip |
| `IsAllowedAsync(action, entity)` | **per-row** authorization |
| `GetRowFilterAsync(action)` | row filter pushed **into the query** |
| `GetProtectedAttributesAsync` | per-row attribute redaction |
| `OnRefreshAsync` | reshape the form when a `triggersRefresh` attribute changes |
| `StreamItems` / `StreamItem` | streaming queries over WebSocket |

⚠️ **`IsAllowedAsync` runs per row; `GetRowFilterAsync` runs in the database.** Prefer the filter
where the rule is expressible as an expression — it is the difference between reading a page and
reading a collection. And note `GetRowFilterAsync` returning `null` means **unrestricted**, not
"deny": a caller consuming only the filter and ignoring `IsAllowedAsync` sees every row while
believing it applied the rule. Use `ISparkRowRule<T>.ApplyAsync`, which applies both.

⚠️ **Type-level rights gate row rules.** With no grant on the type at all, `GetRowFilterAsync` never
runs and signed-in callers are denied too. To restrict a type, *move* the grant to a narrower group
— never delete it.

### `OnRefreshAsync` — forms that reshape themselves

Mark an attribute `"triggersRefresh": true` in the model JSON (hand-set; synchronize preserves it).
When its value changes the client posts the in-progress object to
`/spark/po/{objectTypeId}/refresh`, and the hook may toggle `IsRequired` / `IsReadOnly` /
`IsVisible`, rewrite `Rules`, replace an attribute's `Options`, or set a dependent value.

```csharp
public override Task OnRefreshAsync(SparkRefreshArgs<Car> args)
{
    var obj = args.PersistentObject;
    var stolen = obj[nameof(Car.Status)].Value?.ToString() == CarStatus.Stolen;

    obj[nameof(Car.PoliceReportNumber)].IsVisible = stolen;
    obj[nameof(Car.PoliceReportNumber)].IsRequired = stolen;
    obj[nameof(Car.PromoVideoUrl)].IsVisible = !stolen;
    return Task.CompletedTask;
}
```

⚠️ **Establish the whole presentation state on every call; never patch the previous one.** Each
invocation is handed a freshly scaffolded object, so a hook that only turns things *on* leaves a
form permanently locked after one stray selection. Set both sides of every flag, as above. Share one
helper between this hook and any load-time shaping.

⚠️ **No side effects — it also runs on save.** Spark re-runs the hook while validating a save, once
per triggering attribute, so the rules it establishes are enforced whether or not the client ever
called `/refresh`. That is what makes the feature enforceable rather than decorative, and it means a
hook that writes, notifies or calls out does so on every save too.

⚠️ **It is called far more often than load or save** — potentially on every field blur. Treat
database access inside it as a cost.

⚠️ `args.Attribute` is **nullable**: a stale client can name an attribute the model no longer
declares. `--spark-verify-model` fails (exit 3) if a model declares `triggersRefresh` on a type whose
actions class has no override — that check cannot be an analyzer, because the flag lives in JSON
outside the compilation.

---

## Authorization

`App_Data/security.json`, read by Spark core. Every application has one.

```
dotnet run -- --spark-init-security    # writes a starter that grants nothing
```

A right is `{action}/{target}`:

| | |
|---|---|
| Actions | `Query`, `Read`, `New`, `Edit`, `Delete`, plus any custom action name |
| Combined | `QueryRead`, `ReadEdit`, `EditNew`, `NewDelete`, `EditNewDelete`, `ReadEditNew`, `QueryReadEdit`, `ReadEditNewDelete`, `QueryReadEditNew`, `QueryReadEditNewDelete` |
| Wildcards | `*` on either half — `Read/*`, `*/Person`, `*/*` |

Combined actions expand **symmetrically** — `deny EditNewDelete/Car` denies all three.

**Precedence**, each tier evaluated across the caller's whole group set before the next:
important-denial → important-grant → denial → grant → refuse. **A denial is absolute** unless an
important right overrides it; it cannot be granted around by adding a group, so a denial on
`authenticated` locks out administrators too.

**Groups.** `wellKnown` names the group playing each of two roles: `anonymous` (has *not* signed in)
and `authenticated`. **`anonymous` is not "everyone"** — a right both should have is two grants.
Neither role is assertable from a claim. Every *other* group is matched by **name** against the
caller's group claims, in any translation, so display names are load-bearing.

**Custom actions** live in `App_Data/customActions.json`, keyed by action name, with `showedOn` of
`"detail"`, `"query"` or `"both"`. The right is `{ActionName}/{Type}`. `customActions.json` is a flat
map evaluated against every type, so granting an action on a type that should not offer it renders a
stray button.

**The startup posture report** prints what an anonymous caller can reach on every boot, including
when that is nothing. `--spark-verify-security` compares it against a committed
`App_Data/securityPosture.txt` and exits 3 if it moved — because widening that file is a one-line
diff that reads no differently from narrowing it.

---

## Queries

Declared on the entity's model file. `source` is the convention that decides how they run:

- **`Database.{ContextProperty}`** — a straight collection query.
- **`Custom.{MethodName}`** — resolved to a method on the Actions class. Rows may be fabricated;
  the framework cannot tell, which is why click-through is decided by the `Read` right rather than
  by inspecting the query.

`indexName` binds a query to a RavenDB index; `[DefaultIndex]` and `[FromIndex]` declare the
binding on the types. An unregistered projection silently returns null computed fields, with no
error — if an index-computed field is null in a test but right in the app, suspect that first.

`isStreamingQuery` sends the client to a WebSocket instead of paging over HTTP.

**Sorting on a searchable text field needs a companion.** A field that is tokenized for search
cannot be sorted; the pattern is a `{Name}Sort` companion with no `Index()` call. Adding `Exact` to
the searchable field is a measured regression on both sort and equality.

---

## Things that look right and are not

**Absent JSON field ≠ `== false`.** Add a boolean and query its default, and every pre-existing
document is silently excluded — a missing property does not match `== false`. Use `!= true`.

**Subscriptions cannot evaluate `now()`.** A subscription is change-vector-driven: a document is
tested against the query when it is *written*, and time passing is not a write. Gate on a boolean a
sweeper sets. RavenDB ≤7.2.1 answered a silent false; ≥7.2.2 rejects the query outright.

**`LoadAsync<object>` depends on `@Raven-Clr-Type` metadata.** It returns the entity when the
metadata resolves and a `JObject` when it does not — raw put, bulk insert, Smuggler import, ETL, or
a type since renamed or moved.

**Querying a static index through a raw session needs `ProjectInto<TView>()`**, and the index must
store the fields. Without it Raven materialises from the *source document*, so anything the index
computes comes back null — no error, no warning. Spark's own pipeline projects for you; this bites
in hand-written session queries.

**`UseRateLimiter` is not idempotent.** Registering it twice silently halves the budget.

**`TranslatedString` persists nested.** The flat form is wire-only.

---

## Analyzers

The framework ships Roslyn diagnostics; they exist because the mistakes they catch are undetectable
at runtime.

| | |
|---|---|
| SPARK001–003 | the source-generator package reference is missing or wired wrongly |
| SPARK004 | middleware ordering — `UseSpark()` relative to routing |
| SPARK005–006 | sort companions |
| SPARK007–009 | index / projection / query-index declarations |
| SPARK010 | `MapControllers()` outside Spark's pipeline |

---

## Versioning

**The major version states the targeted platform, not our API.** NuGet major = .NET major
(`net10.0` → `10.x.x`); npm major = Angular major. A breaking change in Spark's own API is a
**minor** bump, described in the release notes. Getting this wrong is expensive — a wrongly
published major can never be reused.
