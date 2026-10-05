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
- **`Query` and `Read` are separate rights, but `Read` implies `Query`** (one way, grants only).
  `Query` without `Read` lists rows in a grid whose first column is *not* a link — the right model
  whenever a row has no detail page to open. A bare `Read` grant means the same as `QueryRead`,
  because the type catalogue every page renders from is `Query`-scoped. A *denied* `Read` leaves
  `Query` alone.
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
public partial class PersonActions : DefaultPersistentObjectActions<Person>, IBeforeSave<Person>, IAfterSave<Person>
{
    [Inject] private readonly IMessageBus messageBus;   // ctor generated by the source generator

    public ValueTask OnBeforeSaveAsync(Person entity, SaveContext context) { … }   // before WITH CHECK and the write
    public ValueTask OnAfterSaveAsync(Person entity, SaveContext context) { … }    // after the commit, isolated
}
```

`partial` is required — `[Inject]` generates the constructor. Nullable fields (`IService?`) get a
`= null` default.

The hooks worth knowing:

| Hook | Purpose |
|---|---|
| `OnLoadAsync` | what a detail page loads for an id |
| `MapAsync(obj, existing)` | how the posted object maps onto the entity a save writes |
| `IBeforeSave<T>` / `IAfterSave<T>` / `IBeforeDelete<T>` / `IAfterDelete<T>` | save/delete interceptors — implement them on the Actions class (no registration) or an interceptor class (`spark.AddInterceptor<T>()`); see `docs/guide-interceptors.md` |
| `GetDefaultIncludes` | eager-load references in one round trip |
| `IsAllowedAsync(action, entity)` | **per-row** authorization |
| `GetRowFilterAsync(action)` | row filter pushed **into the query** |
| `GetProtectedAttributesAsync` | per-row attribute redaction |
| `OnRefreshAsync` | reshape the form when a `triggersRefresh` attribute changes |
| `OnDisableActionsAsync(target, context)` | **the only place** an action is disabled — asked at load and enforced at submit (403) |
| `StreamItems` / `StreamItem` | streaming queries over WebSocket |

⚠️ **Disabling an action is one hook (#460, D13).** `OnDisableActionsAsync(IDisablable target,
DisableActionsContext context)` — call `target.DisableActions("Edit", "MyAction")`. The framework asks
it when a detail page or query loads (the answer is `DisabledActions` on the wire) **and again at
submit** for update (`Edit`/`Save`), delete, create (`New`/`Save`) and every custom action (the object
it runs on, its query and each selected row — union), answering `403 { action }` after the row gate.
Decide from `context.Entity` (the **stored** entity), the user and stored state only, or load and
submit disagree. `PersistentObject.DisableActions`, `SparkQueryContext`/`CustomQueryArgs.DisableActions`
and every `IClientAccessor.DisableActions*` overload are deleted. See `docs/guide-custom-actions.md`.

⚠️ **`IsAllowedAsync` runs per row; `GetRowFilterAsync` runs in the database.** Prefer the filter
where the rule is expressible as an expression — it is the difference between reading a page and
reading a collection. And note `GetRowFilterAsync` returning `null` means **unrestricted**, not
"deny": a caller consuming only the filter and ignoring `IsAllowedAsync` sees every row while
believing it applied the rule. Use `ISparkRowRule<T>.ApplyAsync`, which applies both.

⚠️ **Type-level rights gate row rules.** With no grant on the type at all, `GetRowFilterAsync` never
runs and signed-in callers are denied too. To restrict a type, *move* the grant to a narrower group
— never delete it.

**Rules for many types at once** (soft deletion, tenancy, locks) are row policies and interceptors,
not copies in every Actions class — see `docs/guide-row-security.md`:

- `spark.AddSparkRowPolicy<T>()` with `RowFilterPolicy<TEntityOrInterface>` (a predicate, pushed
  down, ANDed with `GetRowFilterAsync`) or `RowCheckPolicy<T>` (per row — switches DB paging off).
  Write absent-field-safe predicates: `x.IsDeleted != true`, **never** `!x.IsDeleted` (measured: a
  document without the field does not match `!x`).
- Persistence interceptors (#482): `spark.AddInterceptor<T>()` with `IBeforeSave`,
  `IAfterSave`, `IBeforeDelete`, `IAfterDelete`, `IDeleteReplacement` (one per type; replaces the hard
  delete before any before-delete interceptor runs), `IAfterMaterialize`, `IAfterLoad`,
  `INaturalIdCollision`. The framework owns the write: no interceptor or Actions class can skip WITH CHECK,
  the expected change vector or the single commit. Cancel with `throw new SparkCancelException()`.
  No numeric order: registration order, plus `InterceptorStage.Finalize` for interceptors that must run last.
- ⚠️ An `OnLoadAsync` override that skips the base also skips the read gate for its type — policies
  included.
- **Soft deletion is a package, not hand-written** — `MintPlayer.Spark.SoftDelete`: entity implements
  `ISoftDeletable` (four **public** properties), host calls `spark.AddSoftDelete()`, grant
  `Restore/T`, `Purge/T`, `ViewDeleted/T` by name. Delete becomes soft, deleted rows vanish from every
  read path, `POST /spark/po/restore` / `/spark/po/purge` are mapped (purge also deletes the
  revisions). Do not write your own soft delete or `IsDeleted` filter; a raw `session.Delete` of an
  `ISoftDeletable` is refused at commit unless inside `SparkRawWrites.Allow()`.
  `IDatabaseAccess` gates a `Restore` save under `Restore/T` + row action `"Restore"` and a `Purge`
  delete under `Purge/T` + `"Purge"`; the disabled-action hook refuses a restore when `Edit`/`Save`
  is withheld and a purge when `Delete` is. README: `libs/soft_delete/MintPlayer.Spark.SoftDelete/README.md`.
  `/spark/po/load` takes `deleted: include|only` (honoured for `ViewDeleted/T` holders) to open a row
  from the recycle bin; `/spark/permissions/{type}` reports `canRestore`, `canPurge`, `canViewDeleted`.
  A sub-query of a deleted row passes `parentDeleted: exclude|include|only` on
  `/spark/queries/execute` and `/spark/queries/distinct-values` (the mode the **parent** is resolved
  under; honoured for `ViewDeleted/{ParentType}` holders, otherwise the missing-parent 404). Actions
  and delete-many always resolve a live parent, and judge live rows only.
- **History / audit is a package** — `MintPlayer.Spark.History`: `spark.AddHistory()`, entity
  implements `IAuditable` (stamped with user **ids**; `CreatedBy` immutable), revisions come from the
  model's `"revisions": { "enabled": true, … }` block (merged into the database at startup — never
  call `ConfigureRevisionsOperation` by hand, it replaces the whole configuration). Grant `History/T`
  and `Revert/T` by name. A revert is `SavePersistentObjectAsync(po, Revert)`: `Revert/T` + `Edit/T`,
  row action `"Revert"`. README: `libs/history/MintPlayer.Spark.History/README.md`.
- Row rules in an Actions class see the **base verb** for the package operations: `"Edit"` for a
  restore or revert, `"Delete"` for a purge (row policies see the real name). Write rules for the
  built-in verbs; do not special-case `"Restore"`/`"Revert"`/`"Purge"` there.
- An add-on package mapping its own `/spark/*` endpoint answers through
  `MintPlayer.Spark.Endpoints.SparkAddOnEndpoints` (envelope, the one refusal, 400, 403, 409) — never
  an invented error shape (#453 oracle). Content core did not load itself (an old revision) is shown
  through `IPersistentObjectPresenter` (breadcrumbs + redaction), never by mapping it raw.

### `OnRefreshAsync` — forms that reshape themselves

Mark an attribute `"triggersRefresh": "Auto"` in the model JSON (hand-set; synchronize preserves it).
The value is an `ERefreshTrigger`: `None` (same as absent), `Auto` (free text on blur, discrete
editors immediately), `ValueChanged` (every change; free text debounced 300 ms, flushed by blur or
save) or `Blur` (on blur; a discrete editor acts as `ValueChanged` and verify-model warns).
When its value changes the client posts the in-progress object to
`/spark/po/refresh`, and the hook may toggle `IsRequired` / `IsReadOnly` /
`ShowedOn`, rewrite `Rules`, replace an attribute's `Options`, or set a dependent value.

```csharp
public override Task OnRefreshAsync(SparkRefreshArgs<Car> args)
{
    var obj = args.PersistentObject;
    var stolen = obj[nameof(Car.Status)].Value?.ToString() == CarStatus.Stolen;

    obj[nameof(Car.PoliceReportNumber)].ShowedOn = stolen ? EShowedOn.PersistentObject : EShowedOn.None;
    obj[nameof(Car.PoliceReportNumber)].IsRequired = stolen;
    obj[nameof(Car.PromoVideoUrl)].ShowedOn = stolen ? EShowedOn.Query : EShowedOn.Query | EShowedOn.PersistentObject;
    return Task.CompletedTask;
}
```

⚠️ **Establish the whole presentation state on every call; never patch the previous one.** Each
invocation is handed a freshly scaffolded object, so a hook that only turns things *on* leaves a
form permanently locked after one stray selection. Set both sides of every flag, as above. Share one
helper between this hook and any load-time shaping.

**State-dependent visibility is the runtime `ShowedOn` (#264, there is no `IsVisible`).** The model
says `"showedOn": "None"`; the same helper sets `attr.ShowedOn` in `OnLoadAsync` and `OnNewAsync` as
well as `OnRefreshAsync` (Fleet's `CarActions.ShapeForStatus`), and the client applies it from the
first render. ⚠️ `ShowedOn` is layout, not protection: hide a value from a group with a
`security.json` attribute deny, and protect it from writes with `isReadOnly` or an `Edit`/`New` deny.

⚠️ **No side effects — it also runs on save.** Spark re-runs the hook while validating a save, once
per triggering attribute, so the rules it establishes are enforced whether or not the client ever
called `/refresh`. That is what makes the feature enforceable rather than decorative, and it means a
hook that writes, notifies or calls out does so on every save too.

⚠️ **It is called far more often than load or save** — potentially on every field blur. Treat
database access inside it as a cost.

⚠️ **A trigger inside an inline detail grid runs against the ROW's type.** A flag on
`CarreerJob.ProfessionId` reaches `CarreerJobActions.OnRefreshAsync`, not the `PersonActions` that
owns the collection, and the row gets its owner as `args.PersistentObject.Parent` (read-only
context). Authorization still uses the owning type from the route — nested AsDetail types are not in
`security.json`. Metadata you change applies to the whole **column**, not one row; values are
per-row.

⚠️ `args.Attribute` is **nullable**: a stale client can name an attribute the model no longer
declares. `--spark-verify-model` fails (exit 3) if a model declares a `triggersRefresh` other than `None` on a type whose
actions class has no override — including a nested AsDetail type, which needs its own actions class.
That check cannot be an analyzer, because the flag lives in JSON outside the compilation.

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
| Wildcards | **none** — `*` is refused at startup and by SPARK021; name every target, use a combined action |

Combined actions expand **symmetrically** — `deny EditNewDelete/Car` denies all three.

**Attribute rights** are `{verb}/{Type}/{Attr}` (`Edit/Song/Lyrics`), for `Query`/`Read`/`Edit`/`New`
and the combined verbs made only of them; anything else with a third segment, an unknown type or an
unknown attribute refuses startup (SPARK014 at build). **The type right is required** — an attribute
right never unlocks it; a mentioned attribute is decided by the type+attribute chain over the same
four tiers, an unmentioned one inherits the type decision. SPARK024 / a posture note flag a group
that restricts some attributes of a type but leaves others on the type grant. Consume via
`IAttributeRights` (scoped, once per request per type and verb; system context unrestricted).
[Details](../../../docs/guide-authorization.md#attribute-level-rights-verbtypeattribute).

**Precedence**, each tier evaluated across the caller's whole group set before the next:
important-denial → important-grant → denial → grant → refuse. **A denial is absolute** unless an
important right overrides it; it cannot be granted around by adding a group, so a denial on
`authenticated` locks out administrators too.

**Groups.** `wellKnown` names the group playing each of two roles: `anonymous` (has *not* signed in)
and `authenticated`. **`anonymous` is not "everyone"** — a right both should have is two grants.
Neither role is assertable from a claim. Every *other* group is matched by its **untranslated
name** (`"<guid>": "Administrators"`) against the caller's group claims, case-insensitively, never
by a translation (#467, D24). Its display label is `security.groups.{name}.label`.

**Actions** live in `App_Data/actions.json`, keyed by action name, with `showedOn` of `"detail"`,
`"query"` or `"both"`. The right is `{ActionName}/{Type}`. The file is a flat map evaluated against
every type, so granting an action on a type that should not offer it renders a stray button. It holds
no text: labels, descriptions and confirmations are `actions.{Name}.label|description|confirmation`
in `translations.json` (`{count}` in a confirmation is the row count).

**`actions.json` is layered** (#467, D7): the core library ships New, Edit and Delete, any library may
ship a layer, and the app's file composes on top per property. `"Edit": null` removes an inherited
action; a property set to `null` resets it. `--spark-print-effective-actions` prints the composed
catalogue with each property's source layer.

**New, Edit and Delete are catalogue entries** (#460 D18, #467 D7). `/spark/actions/list` returns
them as `isDefault` entries for holders of `New/T` / `Edit/T` / `Delete/T`. The defaults are: New
(query, no rule), Edit (`=1`, query and detail), Delete (`>0`, query and detail, confirmation). The
detail page's Edit and Delete buttons are these entries (D8).
- Override their `showedOn`, rule or icon in the app's `actions.json`, without a C# class.
- Never write an `ICustomAction` with one of these names: it is never executed.
- Delete on a selection is `POST /spark/po/delete-many`. It is all or nothing, uses one
  `SaveChanges`, and SoftDelete applies to it. Per-row delete logic is an `IBeforeDelete<T>`; a
  `Retry.Action` prompt from it refuses that row, and a `SparkCancelException` cancels the batch.

**Sub-queries** are the parent type's `persistentObject.queries`. An entry is a bare alias or
`{ "query", "selectionMode", "parentReference" }`, and `selectionMode` can also sit on the query
itself (`auto` by default, derived from the actions the caller can use on the list: built-in Edit and
Delete count, `single` no longer exists, and a row click always opens the row — #467 R1, D9, D10).
- New on a sub-query calls `OnNewAsync(SparkNewArgs<T>)` with `args.Parent` / `ParentType` / `Query`.
- **The base fills the reference to the parent.** If you override `OnNewAsync`, call
  `base.OnNewAsync(args)` or `args.FillParentReference()`, or the reference stays empty.
- Name the attribute with `parentReference` when the row type references the parent more than once.
  A wrong name fails startup.
- A sub-query's search goes to `/spark/queries/execute` as `search` (with `parentId`/`parentType`),
  and to `/spark/queries/distinct-values` as **`querySearch`**, applied the same way, so a column
  filter lists only values from rows the grid shows. (`search` on distinct-values narrows the listed
  values themselves.)

**Accounts** (`spark.AddAuthentication<TUser>()`, `MintPlayer.Spark.Authorization` README has the
route table):

- Sign-in takes an **email or a user name**; a user name containing `@` must be that account's own
  email (enforced on save). Don't read `UserName` as "the email".
- Account mail goes through `IEmailSender<TUser>`; its links point at the SPA (`/confirm-email`,
  `/reset-password`) and need **`Spark:Auth:PublicBaseUrl` outside Development** — without it the mail
  is not sent (never built from the request `Host`).
- `SparkUser.AuthenticatorKey` and token values are stored encrypted (`sdp1:`). Read them through
  `UserManager` (`GetAuthenticatorKeyAsync`, `GetAuthenticationTokenAsync`), never from the property.
- App fields on the profile page: `ISparkProfileContributor<TUser>`; GDPR: `ISparkPersonalDataContributor<TUser>`
  and `ISparkAccountDeletionHandler<TUser>` (idempotent — a failed deletion is retried with every
  handler again). Register them as scoped services.
- External providers: use the presets (`AddGitHub`, `AddSparkGoogle`, …) — they declare whether the
  provider's email can be trusted; a provider without a signal gets an unconfirmed account and a mail.

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

## Messaging (`spark.AddMessaging()`)

Publish with `IMessageBus`; every method is shorthand for `BroadcastAsync(message, BroadcastOptions
{ DeduplicationKey, Delay, MaxAttempts, ExpiresAtUtc, Queue, ScrubPayloadOnTerminal })`. A test fake
implements only that overload (the others are default interface methods).

- **Deduplication ids are hashed and type-namespaced** (`SparkMessages/{readable}.{hash}`): two
  message types may share a key; keys differing only in punctuation or case no longer collide. No
  need to prefix keys per type.
- **`Queue` must be declared** in `Spark:Messaging:Queues:{name}` (or `options.Queues` in code), or
  the publish throws — an undeclared queue has no consumer.
- **Per-queue options** (`SparkQueueOptions`: `MaxPerInterval`/`Interval`, `BatchSize`/
  `MinDelayBetweenBatches`, `MaxConcurrency`, `MaxAttempts`, `Backoff`). ⚠️ Here **configuration beats
  code**: `Spark:Messaging:Queues` (appsettings, env vars `Spark__Messaging__Queues__{name}__…`) is
  applied over code-declared queue settings. Everywhere else in `Spark:Messaging` code wins.
- Throttling defers an over-budget message **once** to a reserved slot (never waits in a lane).
  `MaxPerInterval` is a rate with a burst (GCRA), not a hard per-window cap; bursts are quantised by
  `FallbackPollInterval`. `MaxConcurrency > 1` gives up FIFO and works in `SingleSubscription` only.
- `ExpiresAtUtc` → dead-lettered with `DeadLetterReason = Expired` instead of being handled late.
  `DeadLetterReason` is `MaxAttempts` / `NonRetryable` / `Expired`; the status stays `DeadLettered`.
- In a handler, inject `IMessageContext` (the message id — stable across retries) and
  `IMessageProgress` (`IsDoneAsync`/`MarkDoneAsync` per step, so a retry skips finished steps).

See `libs/messaging/MintPlayer.Spark.Messaging/README.md`.

## Mail (`spark.AddMailManager()`, package `MintPlayer.Spark.MailManager`)

Never send mail inline, never hand-roll SMTP or HTML. Inject `ISparkMailer` (abstractions package)
and queue a `SparkMailRequest { Template, To, Culture?, Data, Sensitive, ExpiresAtUtc }`; campaigns go
through `SendCampaignAsync` (one mail per recipient, never BCC).

- **Everything is `Spark:Mail` configuration**: transport = one of `UseSmtpTransport()` /
  `UseMailpitTransport()` / `UsePickupFolderTransport()` / `AddMailTransport<T>()`, or with none
  registered `Smtp:Host` **or** `PickupFolder`; `From:Address` required, `Smtp:Security`
  `None|Auto|StartTls|SslOnConnect` (no forced STARTTLS). Startup refuses no/two transports, no sender,
  a template without a neutral file (warning in Development), a template that does not parse,
  `Development:RedirectTo` in Production, and in Development a transport whose
  `DeliversToRealRecipients` is true without `Development:RedirectTo`.
- **Templates are files**: `Templates/Mail/{name}.{culture}.mjml` → `{name}.{language}.mjml` →
  `{name}.mjml`, same chain for the optional `.txt` part; subject = `<mj-title>`. The app's folder wins
  over embedded defaults **per file** (Authorization ships `SparkAuth/ConfirmEmail|PasswordReset|LinkConfirmation`
  in `en` + `nl`). Scriban with strict variables (a missing member fails the render too); string
  values are HTML-escaped for you — never `| html.escape` again.
- **Culture is chosen per send**, never from the request: explicit → `ISparkMailRecipientCulture`
  (Authorization: `SparkUser.PreferredCulture`) → `Spark:Mail:DefaultCulture`. Stored on the message.
- **A mail with a token or link that grants something is `Sensitive = true`** (Data Protection in
  the queue, scrubbed on terminal) **and has `ExpiresAtUtc`** before the token dies.
- Lanes `mail-transactional` / `mail-bulk` are declared with defaults; retune them under
  `Spark:Messaging:Queues:{name}`. A registration surface (`LocalCredentials = Full`) without a
  transport refuses startup (D6) unless `Spark:Auth:AllowUnconfirmedRegistration=true`.
- Test every template with `SparkMailTemplateTester.RenderAllAsync(services, sample, "en", "nl")`.

See `libs/mail/MintPlayer.Spark.MailManager/README.md` and `docs/guide-outgoing-mail.md`.

## Moderation (`spark.AddModeration<TUser>()`, package `MintPlayer.Spark.Moderation`)

Votes, reputation, privileges, flags, locks and suspensions are a package — never hand-roll a
`Score` field, a vote counter or an "is moderator" check.

- **Opt in per entity**: implement `IModeratable { AuthorId, PostedAt }` (abstractions package).
  Both are the framework's: stamped on create, restored on every later write. Never set them
  yourself, never put the score on the entity (a vote would move its etag and 409 the author).
- **Configuration is `Spark:Moderation`**, fed by `App_Data/moderation.json` as the
  **lowest-precedence** source — env vars (`Spark__Moderation__Fraud__…`) override it. Startup
  validates the *layered* result: unknown reputation event names, privilege groups that are missing /
  well-known / hold a non-earnable right / have no grant, a destructive `Earnable` entry.
- **Privileges are `security.json` groups by id**, conferred by a composed group-membership provider
  (never a claim). `Lock`, `Suspend`, `Audit`, `Purge`, `Restore`, `Revert`, `ViewDeleted` are never
  earnable. Rights: `Vote/T`, `Downvote/T`, `Flag/T`, `Lock/T`, `Review/Moderation`,
  `Suspend/Moderation`, `Audit/Moderation` — by name. `--spark-init-moderation` prints the grants.
- **All ten fraud measures are on**; tune thresholds in configuration, do not disable them in
  code. The ledger is append-only: a correction is a compensating entry, never an edit or delete.
- A lock refuses save / AsDetail change / custom-action write / revert / delete / restore / purge
  with **400** for everyone without `Lock/T`; a suspension blocks writes on the **next request**
  (document read by id) — the cookie/bearer lifetime after the stamp refresh is measured in the README.
- New accounts over the posting quota get **429** (`SparkThrottledException`, core) — throw the same
  exception for any business quota of your own, never a 400 or 404.

See `libs/moderation/MintPlayer.Spark.Moderation/README.md` and `docs/guide-moderation.md`.

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
| SPARK020 | `[Authorize]` or `.RequireAuthorization(…)` with a policy name or roles (error) — use `[SparkAuthorize("Action", nameof(Target))]` / `.RequireAuthorization(new SparkAuthorizeAttribute("Action", nameof(Target)))` |

---

## Versioning

**The major version states the targeted platform, not our API.** NuGet major = .NET major
(`net10.0` → `10.x.x`); npm major = Angular major. A breaking change in Spark's own API is a
**minor** bump, described in the release notes. Getting this wrong is expensive — a wrongly
published major can never be reused.
