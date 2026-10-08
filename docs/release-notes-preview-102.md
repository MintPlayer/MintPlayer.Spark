# Spark 11.0.0-preview.102 — every endpoint is a typed generator class with `[Inject]`

**Packages:** `MintPlayer.Spark`, `MintPlayer.Spark.Authorization`, `MintPlayer.Spark.IdentityProvider`,
`MintPlayer.Spark.Contributions`, `MintPlayer.Spark.History`, `MintPlayer.Spark.SoftDelete`,
`MintPlayer.Spark.Moderation`, `MintPlayer.Spark.Replication`, `MintPlayer.Spark.MailManager`,
`MintPlayer.Spark.Webhooks.GitHub`, `MintPlayer.Spark.Testing` and `MintPlayer.Spark.AllFeatures` →
`11.0.0-preview.102`. No npm package changed. The majors do not move: the packages still target .NET 11
and Angular 22, so every API break below ships as a minor/preview bump.

Every minimal-API route Spark owns, in libraries and applications, is now a generator endpoint class
(`MintPlayer.AspNetCore.Endpoints` 11.4.0-rc.0). Its dependencies arrive through `[Inject]` fields, and
its input through typed binding (`I…Endpoint<TRequest>`, `[RouteParam]`, `[QueryParam]`). There are no
static handler classes, no `RequestServices` lookups and no `if` around mapping. The route table is
unchanged, except for D7 below (the route-table snapshots in `tests/**/RouteSnapshots/` pin it).
`EndpointConventionsTests` guards it. The remaining hand-mapped routes are `MapGitHubWebhooks`,
the `MapIdentityApi` republish and `MapControllers`.

Decisions and evidence: [endpoints_generator_completion_PRD.md](endpoints_generator_completion_PRD.md)
and [endpoints_generator_completion_plan.md](endpoints_generator_completion_plan.md).

---

## ⚠️ Breaking changes

### 1. `SparkAddOnEndpoints` is the injected service `ISparkAddOnEndpoints`

The public static class `MintPlayer.Spark.Endpoints.SparkAddOnEndpoints` is gone. Its envelope,
refusal, validation and throttling helpers are on the scoped service `ISparkAddOnEndpoints`; take it
with `[Inject] private readonly ISparkAddOnEndpoints addOn;`. See
[guide-concurrency.md](guide-concurrency.md).

### 2. `ReadTypedRequestAsync` is removed; bodies are typed

`SparkAddOnEndpoints.ReadTypedRequestAsync<TRequest>` is removed with no replacement on
`ISparkAddOnEndpoints`, along with the internal readers behind it (`SparkRequestType.ReadAsync`,
`SparkRequestBody`). An add-on endpoint declares its body as
`IPostEndpoint<TRequest>` and resolves the entity type with `addOn.ResolveType(request)`.

### 3. A missing or non-JSON body is refused instead of answering 500 (PRD D3a)

The persistent-object, query, custom-action, SoftDelete, History, moderation, revert and
lookup-reference endpoints used to let a request with no body, no content type or `text/plain` escape
as an unhandled 500. They now answer with each endpoint's normal refusal (400, or Spark's standard
refusal). Every other status and body is unchanged.

### 4. Typed bodies drop `accepts(application/json)`, so a wrong content type is refused after authorization (PRD D7)

Typed body endpoints no longer carry `IAcceptsMetadata`, so routing never answers 415 itself. The
endpoint refuses the wrong content type after authorization and antiforgery have run. Example: an
anonymous `text/plain` POST to `/spark/auth/manage/info` now gets **401** where it used to get 415.
OpenAPI documents lose the request content type on these routes.

### 5. `SparkIdentityEndpoints` keeps only Microsoft's routes

The stand-in types for routes Spark serves itself are removed: `Register`, `ResendConfirmationEmail`,
`ForgotPassword`, `ResetPassword`, `ConfirmEmail`, `UpdateInfo`, `SetPassword`, `AuthenticatorUri`,
`Profile`, `UpdateProfile`, `PersonalData`, `DeleteAccount`. An endpoint carries one
`EndpointTypeMetadata`, and those routes now carry their own endpoint class. `Login`, `Refresh`,
`TwoFactor` and `Info` (Microsoft's `MapIdentityApi` routes) remain.

### 6. `AddIdentityProvider()` requires a user type at startup

The identity provider's user-touching endpoints (token, userinfo, logout, login, two-factor) are
generic over the user type and closed once at startup. A host that calls `AddIdentityProvider()`
without `AddAuthentication<TUser>()` now fails at startup with a clear message. It used to fail at
request time, or fell back to `SparkUser`.

### 7. Log categories are class names

These loggers are `ILogger<T>` now, so filters on the old category strings need updating:

| Old category | New category |
|---|---|
| `SparkLookupReferences` | `MintPlayer.Spark.Endpoints.LookupReferences.AddLookupReferenceValue` / `UpdateLookupReferenceValue` / `DeleteLookupReferenceValue` |
| `SparkStreamingQuery` | `MintPlayer.Spark.Endpoints.Queries.StreamExecuteQuery` |
| `SparkSync` | `MintPlayer.Spark.Replication.Endpoints.SyncApply` |
| `MintPlayer.Spark.Authorization.Extensions.SparkAccountEndpoints` (account deletion) | `MintPlayer.Spark.Authorization.Endpoints.Account.DeleteAccount<TUser>` (closed over the app's user type) |

### Internal-only changes (no consumer impact, listed for reviewers)

- `MapSparkIdentityApi<TUser>` lost its `localCredentials` parameter. The mode comes from
  `IOptions` only, so Microsoft's route filter and each endpoint's `IsEnabled` read the same source.
- `MapPasskeyApi` and `MapExternalLoginManagement` are removed. Those endpoints declare `IsEnabled`.
- `OidcIssuer` is an injected singleton instead of a static class.
- The 16 identity-provider static handlers, `SparkAccountEndpoints`, `ContributionEndpoints` and
  `QnATestSeams.Map` are folded into endpoint classes.

---

## Behaviour that did not change

- Route paths, verbs, authorization, antiforgery, rate limiting and CORS. The only fixture diff is D7's
  lost `accepts(application/json)`.
- `GitHubWebhooksOptions.WebhookPath` and `DevWebSocketPath` stay configurable. The dev WebSocket is
  now a generator endpoint, and `IDevWebSocketService` is registered unconditionally.
- CodeCoverage's `/health` and `/health/ready` keep their paths, anonymous access and the 503 on a
  failed readiness probe.
