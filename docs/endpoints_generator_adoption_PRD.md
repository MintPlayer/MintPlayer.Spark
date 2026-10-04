# PRD — Use the Endpoints source-generator everywhere

Branch: `feat/upgrade-generators-and-endpoints` (supersedes `feat/endpoints-generator-everywhere`,
which held only this document).

**Status: unblocked 2026-09-26.** Both blockers are fixed upstream and published. The work is now a
package upgrade plus a contained migration of 32 hand-mapped routes.

---

## Intent

`MintPlayer.AspNetCore.Endpoints` gives a class per endpoint: `Path`, `Methods`, group membership,
metadata via `static IEndpointBase.Configure`, constructor injection, and source-generated
discovery. Spark uses it for about half its surface and hand-writes the rest. The two halves have
drifted, and the drift is not cosmetic — it is where the CSRF work in #451 kept finding gaps.

**Goal:** every endpoint Spark maps is a generator endpoint, so that "what is this endpoint's path,
verb, group and metadata" is answered the same way everywhere.

---

## How Spark maps endpoints today — the architecture this work builds on

All three opted-in libraries already run the generator, already get a generated map method, and
already call it themselves. **Applications need nothing, and never have.**

| Assembly | `[assembly: EndpointsMethodName]` | Called from |
|---|---|---|
| `MintPlayer.Spark` | `MapSparkCoreEndpoints` (`AssemblyInfo.cs:5`) | `SparkMiddleware.cs:448`, inside `MapSpark` |
| `MintPlayer.Spark.Authorization` | `MapSparkAuthEndpoints` (`AssemblyInfo.cs:5`) | `SparkAuthenticationExtensions.cs:106` |
| `MintPlayer.Spark.Replication` | `MapSparkReplicationEndpoints` (`AssemblyInfo.cs:2`) | `SparkReplicationExtensions.cs:157` |

The latter two are registered through `SparkModuleRegistry.AddEndpoints(...)`
(`SparkModuleRegistry.cs:99`) into `endpointActions` (`:9`) and invoked by `MapEndpoints(...)`
(`:151`). One `MapSpark()` call in an app drives the lot.

⚠️ **This is load-bearing for every decision below.** An earlier revision of this PRD assumed the
*application* had to close generics and map them — imported from the upstream attribute's doc
comment — and grew four requirements on apps (a `PackageReference`, an assembly attribute, a map
call, `public` widening) plus a silent-failure mode and a bespoke analyzer to patch it. **None of
that applies.** It is recorded here only so the wrong turn is not taken again.

---

## Measured starting point

Re-measured 2026-09-26. Three previously-recorded figures were wrong and are corrected.

| Assembly | State |
|---|---|
| `MintPlayer.Spark` | ✅ generator — 27 endpoint classes |
| `MintPlayer.Spark.Replication` | ✅ generator — 2 endpoints |
| `MintPlayer.Spark.Authorization` | ⚠️ **mixed** — 4 generator classes; **14 hand-mapped** (7 passkey, 7 external-login) |
| `MintPlayer.Spark.IdentityProvider` | ❌ not opted in — **16 hand-mapped** (was recorded as ~12) |
| `MintPlayer.Spark.Webhooks.GitHub` | ❌ not opted in — **2 hand-mapped** |

**33** generator endpoint classes (not 32) against **32** hand-mapped routes.

---

## The blockers are gone

### B1 — Generic endpoints ✅ RESOLVED by Endpoints 11.2.0-rc.0

MintPlayer.AspNetCore.Tools#34, fixed in PR #35 (`3e4b2de`). Previously an open-generic endpoint made
the generator emit a *phantom non-generic twin* of the same name: the real `Passkeys<TUser>` never
got its generated base class or binder, and the assembly failed **CS0246** where non-generic
generated code named `TUser`. The generated partial now repeats the type parameters
(`EndpointGenerator.Producer.cs:238-253`), so typed bases and binders land on the generic class.

The type argument is supplied by an assembly attribute whose types ride as *symbols*:

```csharp
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class EndpointTypeArgumentAttribute<TConstraint, TArgument> : Attribute
    where TArgument : TConstraint;
```

The generator reads `compilation.Assembly.GetAttributes()` and takes
`attributeClass.TypeArguments[0]/[1]` (`EndpointClosing.cs:72-97`). Binding is **exact symbol
identity against the type parameter's declared `ConstraintTypes`** (`:203-206`), which is what
answers "which type arguments across all endpoint classes are the same or different": same
constraint → same argument, one declaration; anything needing an exception uses the explicit
`typeof(Echo<>)` form. Substitution is real Roslyn construction (`GenericTypes.Construct`), so a
closed generic flows through the ordinary pipeline. The closed name is `{Name}_{TypeArguments}`,
which changes name metadata and `operationId` but **not the route path**.

⚠️ The attribute is read from **the assembly being compiled**. A library's generated map method
therefore cannot see an attribute declared by a downstream app — the library compiles first. That is
what D1 resolves.

### B2 — Conditional registration ✅ RESOLVED by the same release

`IEndpointGroup` gains `static virtual bool IsEnabled(IServiceProvider) => true`
(`IEndpointGroup.cs:40`). Evaluated **once at map time** against the **root** provider, and it
short-circuits: a nested group's `IsEnabled` is never called when its parent returned false
(`EndpointGenerator.Producer.cs:166-184`). This matches today's gates exactly — they also read
`endpoints.ServiceProvider` at registration, the same provider at the same instant.

---

## D1 — The 14 generic auth routes map with `MapEndpoint<T<TUser>>()`

**Decided.** They become ordinary endpoint classes, generic over `TUser`, and are mapped from inside
`SparkAuthenticationExtensions.MapSparkIdentityApi<TUser>` (`:94`, `where TUser : SparkUser, new()`)
— immediately beside the existing `endpoints.MapSparkAuthEndpoints()` at `:106`:

```csharp
endpoints.MapSparkAuthEndpoints();          // the 4 non-generic classes, as today
endpoints.MapEndpoint<Passkeys<TUser>>();   // …and the generic ones, TUser already concrete here
```

`TUser` is a real type argument at that call site, supplied by the app through
`AddAuthentication<TUser>`. So the app's user type flows through untouched, **no `EndpointTypeArgument`
is needed anywhere**, and nothing changes for consumers.

**Why not the two alternatives:**

- **Library self-closes** (`[assembly: EndpointTypeArgument<SparkUser, SparkUser>]` in the
  Authorization assembly). Works, and upstream supports it — but it pins `TUser` to `SparkUser`
  forever. An app could then never extend its user: it would have to put its own fields in a separate
  class in a separate collection and write a fan-out index to rejoin them, because you cannot add a
  navigation property to a class you do not own.
- **Application closes and maps.** Architecturally unnecessary here (see *How Spark maps endpoints
  today*), and it would push a `PackageReference`, an attribute and a map call onto every consumer,
  widen `SparkAuthGroup` and the endpoints to `public`, and introduce a failure mode where omitting
  the attribute 404s the whole auth surface with **no diagnostic anywhere**
  (`EndpointClosing.cs:98-99` short-circuits before any reference is read).

### What `MapEndpoint<T>()` costs Spark — quantified, not assumed

Upstream documents six deficits versus generated mapping (`EndpointRouteBuilderExtensions.cs:34-58`).
Against Spark's actual surface — **100% raw, arity-0**: every endpoint implements an arity-0
interface and writes `Task<IResult> HandleAsync(HttpContext)`, reading route values and bodies by
hand — four are structurally inapplicable:

| Deficit | Cost to Spark |
|---|---|
| `Produces<TResponse>` | **None.** Emitted only for typed endpoints; the generated path emits nothing either. |
| request-body 400/415 | **None.** Typed-only, and both paths call the same helper. |
| typed links / client contract | **None.** `GenerateEndpointsClient` is never set; `EndpointRoutes` is unused. |
| MPEP012 duplicate-name check | **Negligible.** Computed over `mappable`, which already excludes open generics — never in force for these. |
| route/query OpenAPI parameters | **Real but latent.** The generated path *does* emit these for raw endpoints (`ShadowParameters.For` has no level check). But Spark references no OpenAPI package at all, and only 2 of 7 passkey routes are templated. |
| generated `Endpoints` descriptor list | **None that matters.** `GetAuthCapabilities.cs:32` reads the runtime `EndpointDataSource`, which does include manually mapped routes. |

It delivers the whole point of the migration: group chain with nested prefixes, each group's
`Configure`, `IsEnabled`, attribute metadata, the endpoint's own `Configure`, and `WithName` — with
group `Configure` running before endpoint `Configure`, matching generated order
(`EndpointRouteBuilderExtensions.cs:76-134`). Route and name are byte-identical by construction.

No double-mapping: `MapSparkAuthEndpoints()` omits open generics (`EndpointMappingPlan.cs:152-153`),
so the manual call adds them rather than duplicating. MPEP025 is **Info** only, so the library still
compiles clean.

⚠️ One benign divergence: each `MapEndpoint` call builds its own `RouteGroupBuilder`, so group
`Configure` runs once per endpoint rather than once per group. D2 keeps group `Configure` empty, so
the resulting metadata is identical.

### D1 implementation — verified shape

✅ **The one blocker risk is closed.** Spark's `[Inject]` generator handles generic partials —
`InjectSourceGenerator.Producer.cs:87-93` emits `partial class {ClassName}{GenericTypeParameters}`
plus constraints, shipped in 10.13.0 (Spark is on 12.1.0). Existence proof in the *same assembly*
with the *same constraint*: `SparkExternalLoginLinker<TUser> where TUser : SparkUser, new()`
(`Identity/SparkExternalLoginLinker.cs:96`) carries four `[Inject]` fields, one itself generic.

Three partials of one generic class compose legally: the Endpoints part deliberately omits
constraints (`EndpointGenerator.Producer.cs:239-241` — "Constraints need not be repeated on a partial
part"), the `[Inject]` part repeats them fully qualified, and only the Endpoints part adds a base
clause, so nothing collides.

Shape, matching house style (`Endpoints/CsrfRefresh.cs:35-47`, `Logout.cs:8-22`):
`internal sealed partial class X<TUser> : I{Verb}Endpoint, [MemberOf<SparkAuthGroup>]`,
`public static string Path => "/…"` (relative), `static void IEndpointBase.Configure(...)`,
`[Inject]` fields, `Task<IResult> HandleAsync(HttpContext)`. `Methods` comes free from the verb
interface.

- ⚠️ **Each class must restate `where TUser : SparkUser, new()`** — constraints are not inherited
  from the mapping method.
- `new TUser()` (`SparkAuthenticationExtensions.cs:234`) is needed on **one** class,
  `ExternalLoginCallback<TUser>`; `SparkUser` alone suffices for the other 13.
- The 14 need `partial`; the existing four do not have it because they have no `[Inject]`.
- Route and query values are read raw off `HttpContext`, house style
  (`EntityTypes/Get.cs:17-19`), and bodies via `ReadFromJsonAsync<T>()`
  (`LookupReferences/AddValue.cs:33`).
- `internal sealed` stays correct — MPEP029's `public` requirement applies only to an endpoint
  declared in a *referenced* assembly being closed by an application.
- Proposed layout: `Endpoints/Passkeys/` (7 classes + the shared helpers and DTOs moved out of
  `PasskeyEndpoints.cs:39-61,304-354,361-368`) and `Endpoints/ExternalLogin/` (7 classes + the
  relocated `SanitizeReturnUrl` / `ExternalLoginOutcome` / `LinkOrRefuseAsync`).

⚠️ **R1 — the one real new cost.** `MapEndpoint<T>()` cannot declare route parameters, so a templated
route is documented without its path params, which OpenAPI treats as an invalid document
(`EndpointRouteBuilderExtensions.cs:37-47`). It hits exactly two routes, `/passkeys/{id}` and
`/passkeys/{id}/name`. Spark references no OpenAPI package, so the cost is nil today and becomes real
the day one is added.

**Behavioural net:** 71 existing tests in `tests/MintPlayer.Spark.Tests/Authorization/Extensions/`
exercise these routes over a built app rather than the mapping mechanism, plus a real-browser
ceremony in `tests/MintPlayer.Spark.E2E.Tests/PasskeyCeremonyTests.cs`. Only
`MapSparkIdentityApiTests.cs` is likely to need edits, since it asserts on the mapping surface itself.

---

## D2 — The group tree for the conditional routes

✅ **DECIDED: plain `if`s here; `IsEnabled` groups in M7 (D3).**

Because D1 puts the `MapEndpoint<T>()` calls at a call site *we* control, the three existing gates
stay as `if`s around those calls, exactly where they are today —
`PasskeyEndpoints.cs:70-78` (passkeys disabled), `SparkAuthenticationExtensions.cs:360-370` (linking
disabled) and `:449` (`WhenSignedIn`). That removes `SparkPasskeyGroup`,
`SparkExternalLoginLinkingGroup` and `SparkExternalLoginSelfLinkGroup` from the design: three fewer
classes, behaviour provably identical, and a much smaller diff to review.

The group tree below was designed for the case where the *generator* maps everything and cannot see
an `if` — which remains true for `MintPlayer.Spark.IdentityProvider`, so `IsEnabled` still gets its
first real use there. `SparkAuthGroup` supplies the `/spark/auth` prefix either way, and all 14
classes carry `[MemberOf<SparkAuthGroup>]`.

The tree is kept below only as the recorded alternative, should the declarative form ever be
preferred.

`Prefix => ""` composes cleanly — verified, not assumed (`ComposedRoute.Join` skips the separator for
an empty part, `ComposedRoute.cs:48-55`). That matters because the `WhenSignedIn` tier spans two
unrelated path stems, so no non-empty prefix could cover it. Empty-prefix groups are pure gates and
every path stays byte-identical.

```
SparkAuthGroup                          Prefix "/spark/auth"   (exists, unchanged)
├─ the existing 4: /me · /logout · /capabilities · /csrf-refresh
├─ ExternalLoginChallenge   "/external-login"
├─ ExternalLoginCallback    "/external-login-callback"
├─ ConfirmExternalLink      "/confirm-external-link"
│
├─ SparkPasskeyGroup                    Prefix ""   [MemberOf<SparkAuthGroup>]
│     IsEnabled → options.Passkeys != SparkPasskeys.Disabled
│  └─ the 7 passkey endpoints
│
└─ SparkExternalLoginLinkingGroup       Prefix ""   [MemberOf<SparkAuthGroup>]
      IsEnabled → options.ExternalLoginLinking != Disabled
   ├─ ListExternalLogins "/external-logins" · UnlinkExternalLogin "/external-logins/unlink"
   └─ SparkExternalLoginSelfLinkGroup   Prefix ""   [MemberOf<SparkExternalLoginLinkingGroup>]
         IsEnabled → options.ExternalLoginLinking == WhenSignedIn
      └─ LinkExternalLogin "/external-logins/link" · LinkExternalLoginCallback "/link-external-login-callback"
```

The enum is only `Disabled | WhenSignedIn | ConfirmByEmail`, so tier 2 ⊂ tier 1 and the nesting is
exact.

**The three "root-mapped" routes join `SparkAuthGroup`** — see DF2. Their `Path` must be written
**relative** or MPEP010 fires and they land at `/spark/auth/spark/auth/…`.

**Metadata stays per-endpoint; group `Configure` stays empty.** Hoisting `RequireAuthorization()`
onto `SparkPasskeyGroup` would de-anonymise the two deliberately-anonymous sign-in routes
(`PasskeyEndpoints.cs:252-262`). Keeping it empty keeps metadata sets byte-identical, which matters
because `SparkAntiforgeryMiddleware` decides on metadata.

⚠️ If group-level option-dependent metadata is ever needed, `group.ServiceProvider` is a **compile
error** — `RouteGroupBuilder` implements `IEndpointRouteBuilder`'s members explicitly, so it must be
`((IEndpointRouteBuilder)group).ServiceProvider`.

---

## ⚠️ The Endpoints migration is far smaller than recorded

A previous revision claimed all 33 endpoint classes were affected by an eight-item breaking-change
list. **That was wrong.** Verified by grep across `libs/`, `tests/` and `apps/`:

| Symbol | Hits |
|---|---|
| `IGetEndpoint<` · `IDeleteEndpoint<` · `IPostEndpoint<` | 0 |
| `BindRequestAsync` · `NonBodyEndpoint` · `EndpointBase<` · `BodyEndpoint<` | 0 |
| `RouteParam` · `QueryParam` · `ValidatableType` · `EndpointName` | 0 |
| **`IMemberOf<`** | **42** |

Because Spark is entirely raw arity-0, the arity-1 request→response flip, the `NonBodyEndpoint`
deletion, the `BindRequestAsync` semantics shift, `[RouteParam]`, `[ValidatableType]` and the
`EndpointName` rename **cannot apply**.

The existing-code migration is **one mechanical change across 42 sites** — delete `, IMemberOf<G>`,
add `[MemberOf<G>]` — plus two test files. Double-membership (CS0579) risk is nil: no Spark endpoint
or group has a base class.

⚠️ **Under D1 a stale `IMemberOf` is worse than a compile error**: 11.2.0-rc.0 resolves membership
from the attribute only, so an unmigrated class would map at the **root with no prefix** rather than
fail. This is why M0's route snapshot exists.

⚠️ **Sequencing hazard.** `tests/…/Endpoints/RouteTableCompletenessTests.cs:99` reflects over
`IMemberOf<>` to compose prefixes — it is the safety net that would catch a route regression, and it
is rewritten by the same change it polices.
`Endpoints/Authorization/SparkAuthGroupTests.cs:17-19` asserts assignability three times.

---

## ⚠️ The real cost is the surrounding package upgrade

Endpoints 11.2.0-rc.0 is built on `MintPlayer.SourceGenerators.Tools` 12.1.0, so these cannot be
separated.

**SourceGenerators 10.2x → 12.1.0.** `12.0.0` deleted a runtime: the whole
`MintPlayer.SourceGenerators.Tools.ValueComparers` namespace is gone (13 Spark generator files import
it), `.WithComparer()` / `.WithNullableComparer()` / `ComparerRegistry.For<T>()` are removed (8 call
sites), `[AutoValueComparer]` → `[GenerateEquality]` with no alias (~25 usages), and
`IncrementalGenerator.Initialize` loses its third parameter. Pipeline steps building a collection
must return `EquatableArray<T>`; derived and containing types of a `[GenerateEquality]` type must be
`partial` (MINT003/MINT006); dictionaries now compare order-insensitively. ✅ F1
(`IncludeRuntimeDependency`) is a pure win — Spark hits none of the documented side-effect conditions.

**Assertions 1.0.0 → 11.0.0-rc.5.** All five test projects. ⚠️ A **vacuity guard** now throws where an
equivalency comparison contributes zero comparable members — **156 `BeEquivalentTo` sites**. ⚠️
**MPA0001 ships at severity `Error`** and can fail the build.

**⚠️ Lockstep is mandatory.** Every generator package bundles
`MintPlayer.SourceGenerators.Tools.dll` and the SDK keeps the **highest** `AssemblyVersion`. Versions
10.20.2–12.0.1 all shipped a bogus **99.9.9.0**, so a mixed project silently runs every generator
against the old Tools. A partial upgrade is worse than none.

**✅ Measured.** The bump failed at **restore** (NU1605/NU1107) before any C# error — Tools 12.1.0
carries Roslyn 5.9.0 against Spark's 5.3.0 pins. Nine pins moved to 5.9.0; the build then reported
**91 compile errors**, all in the `ValueComparers`/`Initialize` family. The Roslyn **5.9** floor is
met (`global.json` and both CI workflows pin 11.0.100-rc.1); on an older SDK the generator is
*silently skipped* and the build fails with an unexplained CS1061.

---

## D3 — `MintPlayer.Spark.IdentityProvider`: four groups, and CORS via group `Configure`

All 16 handlers are `internal static class X` with `public static Task Handle…(HttpContext)` and
**no type parameters** — five resolve the user type reflectively at runtime from
`Registry.IdentityUserType`. So this assembly needs **no closing machinery at all**; it is strictly
simpler than `MintPlayer.Spark.Authorization`.

⚠️ The handlers return `Task`, not `Task<IResult>` — they write the response themselves. The
translation is `{ await X.Handle(httpContext); return Results.Empty; }`, a no-op after the response
is written. Class count is **16, not 12**: GET and POST on one route are two classes.

```
OidcWellKnownGroup          Prefix "/.well-known"   Configure → conditional RequireCors
OidcConnectGroup            Prefix "/connect"       (no Configure)
OidcLocalCredentialsGroup   Prefix "/connect"       IsEnabled → LocalCredentialsOf(sp) != Disabled
OidcConnectCorsGroup        Prefix "/connect"       Configure → conditional RequireCors
```

Three sibling root groups spelling `"/connect"` rather than nesting with `Prefix => ""` — the
generator emits one `MapGroup(app, TGroup.Prefix)` per group, so three `RouteGroupBuilder`s over one
prefix, which is legal and avoids relying on `MapGroup("")`. Every `Path` is written group-relative
or MPEP010 fires.

`IsEnabled` fits the local-credentials gate **exactly**: `LocalCredentialsOf` (`:126-128`) already
takes an `IServiceProvider`, and `IsEnabled` is evaluated once at map time against
`app.ServiceProvider` — the same moment and provider as today's `if` at `:206`. This is Spark's first
use of `IsEnabled`.

**`WithOidcCors` applies to exactly five endpoints** — discovery, jwks, token, userinfo, revoke;
`/connect/introspect` is deliberately excluded (`:170-172`). ⚠️ **`IsEnabled` cannot express it** — it
would *unmap* `/connect/token` when CORS is off, the opposite of the requirement. The five partition
cleanly across two groups, so a group `Configure` reproduces it exactly:

```csharp
static void IEndpointGroup.Configure(RouteGroupBuilder group)
{
    var services = ((IEndpointRouteBuilder)group).ServiceProvider;
    if (services.GetRequiredService<SparkIdentityProviderOptions>().EnableDynamicCors)
        group.RequireCors(CorsPolicy);
}
```

The cast is required because `RouteGroupBuilder` implements `IEndpointRouteBuilder`'s members
explicitly. ⚠️ **This works but is not a sanctioned Endpoints shape** — the library's documented
answer to "options-dependent" is `IsEnabled`, and `Configure` is declared as taking only the builder.
It is load-bearing on public ASP.NET Core API, not on generator behaviour. A per-endpoint fallback
exists via `builder.Add(e => … e.ApplicationServices …)`, which is what `RequireCors` does internally.

→ **Upstream ask:** `IEndpointGroup.Configure(RouteGroupBuilder, IServiceProvider)`.

## D4 — `MintPlayer.Spark.Webhooks.GitHub`: the documented exception

**Both routes stay hand-mapped**, and the assembly does **not** take a `PackageReference` to the
Endpoints package — there would be nothing to generate. This is the exception to "adopt everywhere",
and it is recorded rather than quietly skipped.

First, a correction to a widely-repeated assumption: **`Path` does *not* have to be a compile-time
constant.** Both mapping paths read `TEndpoint.Path` at run time
(`EndpointGenerator.Producer.cs:471`, `EndpointRouteBuilderExtensions.cs:91`). A computed `Path` maps
fine; what it costs is build-time analysis — `RouteLiteral.Read` returns null, MPEP007–MPEP010 are
skipped, and MPEP011 fires at **Info** ("Nothing is wrong with a computed Path, which is why this is
Info").

The real blocker is different: **`Path` is `static` and receives no `IServiceProvider`**
(`IEndpointBase.cs:25`). A per-registration options value can only reach it through a static mutable
field — process-global, and wrong the moment two hosts share a process. A configuration-driven path
therefore cannot be expressed, and `MapEndpoint<T>()` does not help because it reads the same static.

- **Route 1, `MapGitHubWebhooks`** (`SparkBuilderExtensions.cs:51`): two independent blockers. It is a
  third-party Octokit extension that maps its own route *and* installs a `RequestDelegate` doing HMAC
  validation over the raw body — converting it would mean reimplementing that validation, i.e.
  changing what the endpoint does, which is out of scope. And `options.WebhookPath` is genuinely
  configured, not a de-facto constant (`SparkBuilderExtensionsTests.cs:56` sets `/custom/hook`).
- **Route 2, the dev WebSocket** (`:64-72`): the `DevelopmentAppId.HasValue` gate maps perfectly onto
  `IsEnabled`, but the path does not. ⚠️ **And the motivation for converting it is stale** — the "bare
  `Map()` matching every mutating verb with no metadata" complaint has already been fixed: `:72` is
  `MapGet`, with an explanatory comment at `:65-71`, recorded in
  `docs/release-notes-preview-87.md:71`. There is no remaining metadata hole; a class would add
  `WithName`/OpenAPI, near-worthless for a WebSocket upgrade.

Route 2 *could* convert if `DevWebSocketPath`'s configurability were dropped — a public behaviour
change to `GitHubWebhooksOptions`, needing an explicit decision. Route 1 cannot convert under any
option.

→ **Upstream ask:** a provider-aware or instance `Path`, or a group-level path override, so a library
can honour a configured route prefix.

---

## Rejected alternatives

- **Library self-closes** / **application closes** — see D1.
- **`IdentityUser<Guid>` + marker attribute.** Rejected: the user id is a published external
  identifier in three places outside our control — the **WebAuthn user handle** on users' own
  authenticators (`UserStore.Passkeys.cs:88-96`), the **OIDC `sub`**
  (`OidcTokenGenerator.cs:32,90,214`), and **compare/exchange** uniqueness values
  (`UserStore.cs:92-95`) — so a `Guid` key invalidates every enrolled passkey and forks every relying
  party's account. It also cannot carry what the store needs: `SparkUser` is an embedded aggregate
  with 6 `List<>` collections plus `AuthenticatorKey` (`SparkUser.cs:35-63`) read in 27 places, none
  of which `IdentityUser<TKey>` declares. And it buys nothing — Spark already uses Identity fully
  *without* `IdentityUser`, because the generic surface constrains only `where TUser : class, new()`.
- **Drop the generics, resolve the user type at runtime.** The existing precedent
  (`Token.cs`, `UserInfo.cs`, `Login.cs`, `Logout.cs`, `TwoFactor.cs`) is raw `MakeGenericType` +
  `MethodInfo.Invoke` + `dynamic`, and works only because it is 5 calls. The 14 endpoints need ~32,
  including `SignInManager` overload pairs needing hand-written parameter-type arrays — reflection in
  the most security-sensitive code in the repo, where the wrong overload is a silent auth bug. The
  `ISparkUserService` variant is a ~31-method grab-bag with no invariant of its own.

---

## Defects found along the way — in scope

Per the one-PR rule, these land with this work.

### DF1 — ⚠️ `IdentityUserType`'s fallback breaks any app with a derived user type

Five IdentityProvider sites resolve the user type at runtime from `Registry.IdentityUserType` and
handle the null case **inconsistently**: `Login.cs:84`, `Logout.cs:26`, `TwoFactor.cs:60` fail closed;
`Token.cs:679` and `UserInfo.cs:64` do `?? typeof(SparkUser)`. The fallback resolves
`UserManager<SparkUser>` from a container in which `AddIdentityApiEndpoints<TUser>` registered
`UserManager<AppUser>`, so **any app with a derived user type throws at request time**. Unreachable
today only because no app derives one — which is precisely the extensibility D1 preserves. Make all
five fail closed.

### DF2 — the stale root-mapping comment

`SparkAuthenticationExtensions.cs:147-148` justifies mapping three routes on the root builder "to
avoid group-level auth configuration from `MapIdentityApi`". `MapIdentityApi` is applied to its own
separate `RouteGroupBuilder` (`LocalCredentialEndpointFilter.cs:84`) and cannot reach `authGroup`.
The comment goes when the routes move into `SparkAuthGroup`.

---

## Out of scope

- ~~⚠️ **`MintPlayer.AspNetCore.SpaServices.Xsrf` is NOT being adopted, and this is settled** — no
  `Secure`, no `SameSite`; `XsrfCookieFlagTests` would fail the swap. Declined on evidence twice.
  `11.0.0-rc.2` is published and the swap is tracked in MintPlayer.Spark#452; the pin stays at 10.5.0.~~
  **Superseded 2026-10-04:** adopted in #452 at `11.0.0-rc.3`, outside this PRD's scope. The
  package now sets `Secure` and `SameSite=Strict`, and `XsrfCookieFlagTests` passes.
- **Spark's own XSRF mint placement** — `docs/xsrf_minting_plan.md` M1-M3.
- **Modernising the 7 hand-read route values and 4 hand-deserialised bodies** to `[RouteParam]` /
  typed request endpoints. Legal today (MPEP008 skips raw endpoints) and tempting, but not this change.
- **Changing what any endpoint does.**
- **Publishing new Spark package majors.** Spark's majors track net11.0 and do not move because a
  dependency's did. Upstream deliberately uses a different convention for the generator family —
  semver-major on a `netstandard2.0` package — so 12.x is consumable from net11.0.

---

## Spikes

**SP1 — does the generated mapping change for raw endpoints?** Spark is 100% `EndpointLevel.Raw` and
the generator has a distinct Raw branch (`Producer.cs:218,255,472`). Build before and after and
`git diff` the emitted `EndpointMapping.g.cs` for the three libs. This is what proves the 42-site
membership flip is metadata-neutral.

**SP2 ✅ RESOLVED — the two webhook routes stay hand-mapped.** See D4.

**SP3 ✅ RESOLVED — `IsEnabled` cannot express `WithOidcCors`; a group `Configure` can.** See D3.

**SP4 — how many of the 156 `BeEquivalentTo` sites are vacuous?** Every one the new guard throws on is
a test that was asserting nothing. Triage as **findings**, not noise to silence with
`AllowingVacuousComparison()`.

**SP5 — `localCredentials` is a method parameter, not an option.**
`MapSparkIdentityApi<TUser>(…, SparkLocalCredentials localCredentials = Full)` is closed over by two
of the 14 routes. Production passes `options.LocalCredentials`, but three test files pass it
independently (`AuthCapabilitiesTests.cs:50`, `ExternalLoginManagementTests.cs:113`,
`GitHubChallengeShapeTests.cs:62`). An endpoint class must read `IOptions<>` instead, so those tests
can diverge — check each before assuming the migration is behaviour-neutral.
