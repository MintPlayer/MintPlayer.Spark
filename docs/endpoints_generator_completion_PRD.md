# PRD — Finish the Endpoints-generator adoption, with `[Inject]` dependency injection

Companion plan: [`endpoints_generator_completion_plan.md`](endpoints_generator_completion_plan.md).
Predecessor: [`endpoints_generator_adoption_PRD.md`](endpoints_generator_adoption_PRD.md) (PR #455).
Status: **implemented, 2026-10-08**, in PR #492 (squash-merged). D1–D6 were grilled with the owner on
2026-10-07; D3a, D7 and D8 were added during implementation (§6). The investigation ran four parallel
read-only passes over `master` at `c05e7a52`. The evidence for each milestone is in the plan.

---

## 1. Problem

PR #455 claimed "every hand-mapped route is now a generator endpoint class except the two documented
webhook exceptions". That was true for the routes that existed then, but on today's `master`:

1. **Routes added after #455 were hand-mapped again**, in static classes:
   - `SparkAccountEndpoints` (authorization, #461, changed in #466/#486) — **15 routes**,
     `static Map<TUser>`, every handler resolves services through `[FromServices] IServiceProvider`
     (~40 `GetRequiredService` calls).
   - `ContributionEndpoints` (contributions, #465) — **1 route**, `POST /spark/po/revert-contribution`,
     static `RevertAsync(HttpContext)` resolving **7** services via `RequestServices`.
2. **"Converted" is sometimes only a wrapper.** `MintPlayer.Spark.IdentityProvider`'s 16 endpoint
   classes each forward to one of **16 `internal static class` handlers**
   (`Endpoints/*.cs`), which make **33** `RequestServices` lookups and **0** use `[Inject]`
   (e.g. `ConnectEndpoints.cs:14-17` → `Authorize.Handle(httpContext)`). This was a deliberate #455
   choice (old PRD D3), and it is exactly the "static classes" pattern this PRD removes.
3. **Service-locator holdouts inside otherwise-converted code:**
   - `ILoggerFactory` via `RequestServices` in `LookupReferences/AddValue.cs:52`, `DeleteValue.cs:41`,
     `UpdateValue.cs:49`, `Queries/StreamExecuteQuery.cs:127` (same classes already use `[Inject]`).
   - `SparkAddOnEndpoints` (`libs/spark/MintPlayer.Spark/Endpoints/SparkAddOnEndpoints.cs:23`,
     `public static class`) resolves `IRowPolicyRequestState` at `:98`, `:107`; history, moderation,
     soft-delete and contributions all go through it.
   - `GetAuthCapabilities.cs:36-67` resolves manually; `ExternalLoginCallback.cs:104` passes
     `httpContext.RequestServices` to `SparkExternalProviderPolicies.For`.
4. **Apps never adopted the generator** (no `apps/` project references it):
   CodeCoverage `/health` + `/health/ready` (`Program.cs:509,514`), QnA test seams
   (`QnATestSeams.cs:42,47,52`, static class, mapped only when `testSeams`), and CodeCoverage's
   6 MVC controllers (19 actions) — **these stay MVC (D1)**.

## 2. Goal

Every minimal-API endpoint Spark owns, in libraries and applications, is a **typed** generator
endpoint class whose dependencies arrive through `[Inject]` fields. That means no static handler
classes, no `RequestServices`/`IServiceProvider` service location, no handler-parameter injection,
no hand-parsed bodies, routes or queries, and no `if` around mapping. The only exceptions are listed
in §5, each with its evidence. MVC controllers in applications are out of scope (D1).

### Why `[Inject]` is viable (verified, not assumed)

- The generated map code builds one `ActivatorUtilities.CreateFactory<T>(Type.EmptyTypes)` per
  endpoint and calls `factory(ctx.RequestServices, null)` **per request**, disposing afterwards
  (upstream `EndpointGenerator.Producer.cs:99,469,476-477`). So constructor injection receives
  **request-scoped** services (`IAsyncDocumentSession`, `UserManager<TUser>`).
- The two generators compose: `ActivatorUtilities` finds the `[Inject]`-generated ctor by reflection
  at run time, so generator ordering is irrelevant.
- Already the norm: ~20 endpoint classes use it, including generic ones —
  `Passkeys/PasskeySignIn.cs:23-29` (`PasskeySignIn<TUser>` with `[Inject] SignInManager<TUser>`).
  Moderation 15/15, History 3/3, Replication 2/2, SoftDelete 2/2, MailManager 2/2,
  Authorization 10/13.
- **The one trap:** `IEndpointGroup.IsEnabled(IServiceProvider)` and group `Configure` run at
  **map time on the root provider** — never resolve scoped services there.
- Avoid two public constructors on one class (ActivatorUtilities can't choose).

## 3. Non-goals

- Upgrading the Endpoints package — `11.3.0-rc.0` is pinned everywhere and is the newest release.
- Middleware that is cross-cutting rather than path-terminating: SPA `UseWhen` blocks, CORS split,
  antiforgery/rate-limit gates, the `/connect` framing-header middleware
  (`SparkIdentityProviderExtensions.cs:111-120`, unless D6 decides otherwise), CodeCoverage's
  `ForkUploadsPartitionKey` raw-path parsing (runs before endpoint selection on purpose).
- Inline `MapGet`/`MapPost` fixtures inside unit tests (~43 in 18 files).
- Any route path, verb, auth, antiforgery or rate-limit change. This is a refactor: the route table
  must be identical before and after (except dead code removed).

## 4. Inventory (what moves)

| # | Area | Routes | Today | Target |
|---|---|---|---|---|
| A | `SparkAccountEndpoints` (authorization) | 15 | static generic `Map<TUser>`, `[FromBody]`, typed `Results<Ok, ValidationProblem>`, 3 mode tiers, nested `/manage` + `RequireAuthorization` | `partial` generic classes `X<TUser>` + `[Inject]`, mapped with `MapEndpoint<X<TUser>>()` inside `MapSparkIdentityApi<TUser>` (old PRD D1 pattern); `SparkAuthManageGroup` |
| B | IdentityProvider handlers | 16 (already classes) | wrapper → static handler, 33 lookups, 5 resolve TUser by reflection (`MakeGenericType`, `SparkModuleRegistry.IdentityUserType`, e.g. `Login.cs:87-97`) | fold each handler into its endpoint class with `[Inject]`; TUser question = **D2** |
| C | `ContributionEndpoints` | 1 | static, 7 lookups, no package ref | `partial IPostEndpoint` + `[Inject]`, `AssemblyInfo` `MapSparkContributionsEndpoints` |
| D | `SparkAddOnEndpoints` helper | — | static, `RequestServices` lookups | instance service (injected) or explicit argument |
| E | Logger + capabilities holdouts | — | `RequestServices` | `[Inject] ILogger<T>` etc. |
| F | Webhooks dev WebSocket | 1 | static, conditional, configurable path, 3 lookups | endpoint class, endpoint `IsEnabled` on `DevelopmentAppId`, configurable path via `GetPath` (D4, #37) |
| G | CodeCoverage `/health`, `/health/ready` | 2 | inline lambdas | endpoint classes; **paths and anonymous access are production contracts** |
| H | QnA test seams | 3 | static, conditional, antiforgery group | group with `IsEnabled` reading the `testSeams` flag |
| I | CodeCoverage MVC controllers | 19 actions | MVC, already `[Inject]` | **unchanged (D1)** — apps choose their own style |
| J | Dead `authGroup` | — | `SparkAuthenticationExtensions.cs:150`, mapped onto by nobody | delete |

## 5. Exceptions (stay hand-mapped)

| Route | Why | Evidence |
|---|---|---|
| GitHub webhook POST (`SparkBuilderExtensions.cs:51`) | Octokit's `MapGitHubWebhooks` owns the request-level HMAC (`X-Hub-Signature-256`) refusal. *Superseded (2026-10-08):* "and event dispatch" and the static-`Path` reason; dispatch is public API Spark already calls, and #37 shipped `GetPath` | `endpoints_generator_webhooks_exception.md`; **D4** |
| `MapIdentityApi<TUser>()` re-publish (`LocalCredentialEndpointFilter.cs:87-100`) | Microsoft's monolithic mapper; filtered and republished via `FixedEndpointDataSource` | **new exception — undocumented in #455's PRD; record it** |
| `MapControllers` (`SparkControllersExtensions.cs:73`) | MVC by design; applications may keep controllers | D1 (owner): the generator exists because libraries cannot declare controllers |

## 6. Decisions (grilled with the owner, 2026-10-07)

All are owner decisions taken in a grill session; the evidence each one rests on is cited inline.

- **D1 — MVC controllers in applications stay as they are.** *Principle (owner):* the generator
  exists because **a class library cannot declare controllers**; applications choose their own style.
  CodeCoverage's 6 controllers / 19 actions (already `[Inject]`), `MintPlayer.Spark.Controllers`,
  `AddControllers/UseControllers` and `WeatherForecastController` are untouched. `MapControllers`
  is a permanent §5 exception.
- **D1b — Application minimal-API routes DO convert** (owner chose "convert anyway" over the
  recommended "out of scope"): CodeCoverage `/health` + `/health/ready`, QnA test seams. M6 stays.
  The M8 guard covers generator endpoint classes in libs **and** apps, never controllers.
- **D3 — Typed endpoints everywhere** (owner chose "typed everywhere" over the recommended
  "typed only for body routes"). Every endpoint that reads its body, route or query by hand moves to
  typed endpoints (`I…Endpoint<TReq[,TResp]>`, `[RouteParam]`, `[QueryParam]`). Measured scope:
  **~27 endpoint files** read input by hand today (`ReadFromJsonAsync`/`Request.Body`/`ReadFormAsync`/
  `RouteValues`/`Request.Query[`), across spark core, authorization, identity_provider, moderation,
  replication — plus the 15 account routes and the contributions route. The OIDC token, revocation
  and introspection routes take **form-urlencoded** bodies (RFC 6749), so they override
  `BindRequestAsync`.
- **D3b — If typed binding does not work through `MapEndpoint<T>()`, fix it upstream** in
  `MintPlayer.AspNetCore.Tools`, publish an rc first, adopt it in this PR (see §6a).
- **Q5 — Upstream #36 and #37 both ship in that release.** #36:
  `IEndpointGroup.Configure(RouteGroupBuilder, IServiceProvider)`, which removes the cast in
  `OidcCors.Apply` (`Groups.cs:75-85`). #37 uses a **nullable sentinel** (owner, 2026-10-07):
  `static virtual string? GetPath(IServiceProvider services) => null;`, and mappers use
  `T.GetPath(sp) ?? T.Path`. `Path` stays `static abstract`, so a missing path is still a compile
  error. *Superseded:* `=> Path` as the default does not compile (CS8926: the default body has no type
  parameter through which to reach the implementer's static abstract `Path`). Rejected alternatives:
  - a virtual `Path`, because a missing path would become a runtime throw;
  - an opt-in `IConfiguredPath`, because it needs a second constrained helper per endpoint and breaks
    the `IsEnabled` pattern;
  - instance or DI path providers.

  Constraints:
  1. `null` means "use `Path`", never "unmapped", and a test proves it.
  2. For an overriding endpoint, the literal `Path` is only the default. The generator detects the
     override syntactically, reports it at Info, and leaves out the typed link, or marks the
     descriptor as configurable. Literal-route checks may stay.
  3. Root provider only.
  4. `MapEndpoint<T>()` honours it.

  [Decision comment on #37](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/issues/37#issuecomment-6045543220).
  *Superseded:* an earlier draft of this decision dropped `WebhookPath`/`DevWebSocketPath`. Both
  stay configurable, so there is no public API break.
- **D4 — The GitHub webhook POST stays on Octokit** as a documented exception. The blocker is
  **Octokit owning the request-level `X-Hub-Signature-256` refusal**, not the path. `#37` does not
  change it, and the exception doc must say so. *Superseded (2026-10-08, spike S4 finding a):* an
  earlier wording also named "dispatch". Dispatch is not a blocker:
  `WebhookEventProcessor.ProcessWebhookAsync` is public, the dev-tunnel clients already call it, and
  Spark re-verifies the signature itself inside it (`SparkWebhookEventProcessor.cs:45-56`), where it
  can only drop the event. The decision stands. The dev WebSocket converts, keeping its configurable
  path through `GetPath` (which returns `options.DevWebSocketPath`, whose default references the
  endpoint's `Path` constant, so the default lives in one place). It is gated by `IsEnabled` on
  `DevelopmentAppId`.
- **D2 — identity_provider's user-touching endpoints become generic** (`Login<TUser>`, …), with
  `[Inject] SignInManager<TUser>` / `UserManager<TUser>`. The identity_provider map callback closes
  them **once at startup** from `registry.IdentityUserType` (5 reflective `MapEndpoint<T>()` calls)
  instead of per request (`Login.cs:89`, `Logout.cs:23`, `Token.cs:687`, `TwoFactor.cs:58`,
  `UserInfo.cs:69`). The app-facing API is unchanged (`AddIdentityProvider` stays non-generic). If
  `IdentityUserType` is null, it **fails at startup** with a clear message, replacing the
  request-time throw at `Token.cs:689`. Rejected: `AddIdentityProvider<TUser>()` (duplication plus an
  API break), and a non-generic facade (it would hide ASP.NET Identity).
- **D5 — Conditional mapping is `IsEnabled` everywhere; no `if` around mapping.** *Owner rule:* a
  group is entirely enabled or disabled; a condition that applies to one endpoint lives **on that
  endpoint**. That needs a new upstream API (verified missing at `1f79658`: `IsEnabled` exists only
  on `IEndpointGroup.cs:40`, checked per group at `EndpointGenerator.Producer.cs:166` and
  `EndpointRouteBuilderExtensions.cs:78`):
  `static virtual bool IsEnabled(IServiceProvider) => true` on `IEndpointBase`, honoured by the
  generated mapping and by `MapEndpoint<T>()`. Account routes: a single `SparkAuthManageGroup` that
  carries `RequireAuthorization` once, with each mode-dependent route declaring its condition through
  a shared helper (e.g. `LocalCredentialMode.IsFull(services)`).
- **D6 — Anti-framing on `/connect` stays as middleware** (`SparkIdentityProviderExtensions.cs:111-120`).
  An endpoint filter would miss 404, 405 and short-circuited responses. M4 adds a test that asserts
  the headers on a real `/connect/authorize` response. The middleware matches
  `StartsWithSegments(OidcConnectGroup.Prefix)` instead of the literal `"/connect"`, so it cannot
  drift from the groups (`Prefix` is static; `Groups.cs:24`).
  *Superseded:* group-scoped middleware in the generator
  ([MintPlayer.AspNetCore.Tools#40](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/issues/40))
  was filed and then **closed as not planned** (owner, 2026-10-07), for three reasons:
  - middleware order cannot be expressed through one generated call per assembly;
  - three groups share the `/connect` prefix (`Groups.cs:24,45,61`), so groups are not a pipeline
    unit;
  - the static `Prefix` already solves the drift.
- **D7 — Typed body endpoints drop `accepts(application/json)`** (owner, 2026-10-08, after spike
  S3). Endpoints 11.4 declares the request body with no content types
  (`EndpointDocumentation.DeclareRequestBody`), so routing never answers 415 itself. Instead the
  endpoint refuses a wrong content type, after authorization and antiforgery have run. For example,
  an anonymous `text/plain` POST to `/manage/info` now gets 401 where it used to get 415. Status
  codes and bodies are otherwise unchanged (pinned by
  `AccountFlowTests.Unbindable_requests_answer_a_bare_status_as_before`). Route fixtures are
  updated for every typed body endpoint (M3, M4, M5). No endpoint adds `.Accepts<T>(…)` to restore
  the routing-level 415.
- **D3a — D3 also covers the `SparkRequestType.ReadAsync` readers** (owner, 2026-10-08, after M3).
  - D3's measured list missed about 14 core endpoints that read their body through the shared
    `SparkRequestType.ReadAsync`/`SparkRequestBody` helper, because its regex does not match that
    helper. They are:
    - persistent objects: load, create, update, delete, delete-many, delete-row, new, refresh and
      the subquery's new parent;
    - queries: get, execute and distinct;
    - custom actions: list and execute;
    - SoftDelete restore and purge, and History revisions, revision and revert.
  - **They are typed too.** As a result, a missing or non-JSON content type gets a refusal where it
    used to escape as a 500.
  - **Mail:** `ReceiveBounce` and `Unsubscribe` move their `?recipient` and `?t` reads to
    `[QueryParam]`. The bounce **body** stays a raw read, as a stated exception: the secret is
    checked before any parse, and there is a size cap.
- **D8 — How a typed handler reaches `HttpContext`** (implementation choice, 2026-10-08). The typed
  base class exposes no `HttpContext`.
  - An endpoint that already overrides `BindRequestAsync` keeps the context it receives there, as
    the token endpoint does.
  - Every other endpoint uses `[Inject] IHttpContextAccessor`, as the account endpoints do.

### 6a. The upstream release (lands first, same unit of work)

**`MintPlayer.AspNetCore.Endpoints` 11.4.0-rc.0**, which is
[MintPlayer.AspNetCore.Tools#41](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/pull/41)
(PRD Draft 3, accepted after Spark's
[review](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/pull/41#issuecomment-6046759977)).
It is a **breaking** minor; the owner allows breaking changes. It contains:
1. **#36:** `Configure(RouteGroupBuilder, IServiceProvider)` and
   `Configure(RouteHandlerBuilder, IServiceProvider)` **replace** the one-argument hooks, and both
   receive the root provider. An implicit old hook gets MPEP035 (Error), an explicit one fails with
   CS0539.
2. **#37:** `static virtual string? GetPath(IServiceProvider) => null` (nullable sentinel; see Q5),
   reported with MPEP034 (Info) and the `IsPathConfigurable` descriptor flag. If a configured path's
   route parameters differ from its default's, **startup fails** (R2.10).
3. **Endpoint-level `static virtual bool IsEnabled(IServiceProvider) => true`** (R5), evaluated after
   the group chain. A disabled endpoint never has its `GetPath`, `Configure` or parameter check
   called, `MapEndpoint<T>()` leaves it unmapped, and `IsEndpointMapped<T>` returns false.
4. **MPEP036 (Error):** a class is a group or an endpoint, never both. Checked against Spark on
   2026-10-07: no Spark class is both.
5. **Typed binding through `MapEndpoint<T>()`: verified, with no fix needed** (upstream spike S4).
   Binding lives in `EndpointBase<T>`/`BodyEndpoint<T>`, not in the mappers. 48 request cases gave the
   same result through both mapping paths: valid, malformed (400), wrong content type (415), empty
   body, and a form-urlencoded `BindRequestAsync` override. The PR adds regression tests only. This
   replaces Spark spike S1.
   - A typed generic endpoint must be `partial` (MPEP001), or derive from `PostEndpoint<T>` explicitly.
   - Typed endpoints return 415 for an empty body with no content type, and a bare 400 for a JSON
     `null`. Wherever this differs from today's minimal-API `[FromBody]` responses, M3 and M5 must
     check it.

It must not drag in a `MintPlayer.SourceGenerators` bump that Spark is not ready for (version-split
lockstep).

## 7. Acceptance criteria

1. No `internal/public static class` under `libs/**/Endpoints` that holds handler logic; no
   `RequestServices.GetService`/`GetRequiredService` and no `[FromServices]` in endpoint code
   (enforced by a test or analyzer — see plan M8; `BannedSymbols` is a candidate).
2. Every remaining hand-mapped route is listed in §5 with evidence.
3. `RouteTableCompletenessTests` + a before/after dump of `EndpointDataSource` (verb, pattern,
   metadata types: authorization, antiforgery, rate limiting, CORS) shows **no difference** except
   §4-J.
4. Full local sweep (`npm run test:affected`, Developer licence) green, then CI green.
5. No package major bump (CLAUDE.md versioning rule); breaking API change → minor.

## 8. Risks

- **Silent route loss.** A class that is not mapped returns 404 with no build error (e.g. a generic
  class never passed to `MapEndpoint`, or a missing `[MemberOf]` maps at root). Mitigation: M0
  snapshot + `IsEndpointMapped<T>()` (11.3) assertions.
- **Security metadata drift** — antiforgery and authorization on the account routes are the most
  sensitive surface in the repo; snapshot diff includes metadata, not only patterns.
- **Root-provider resolution** in `IsEnabled`/`Configure` of a scoped service → captive dependency.
- **CodeCoverage is production** — `/health` paths are polled by compose and the deploy workflow.
