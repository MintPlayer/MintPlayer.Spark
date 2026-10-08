# Plan — Finish the Endpoints-generator adoption, with `[Inject]` and typed endpoints

Companion to [`endpoints_generator_completion_PRD.md`](endpoints_generator_completion_PRD.md); the
decisions are in its §6 (locked 2026-10-07). **One unit of work:** an upstream
`MintPlayer.AspNetCore.Tools` release (published first) plus **one** Spark pull request. Test suites
run once, at the end (M9). Intermediate milestones are verified by build and by reading the code.

**Status:** decisions locked. The upstream release is planned as
[MintPlayer.AspNetCore.Tools#41](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/pull/41)
(11.4.0-rc.0, PRD Draft 3 accepts all of Spark's review); its implementation has not started. Spark:
nothing implemented. Next: the Spark spikes S2–S4, run against a local build of the #41 branch or the
published rc.

---

## Spikes

Each spike ends with a written verdict appended here, with numbers, file:line references, and
red→green evidence where relevant. Spike code is throwaway unless a milestone adopts it.

### S1 — Typed endpoints through `MapEndpoint<T<TUser>>()`: ✅ resolved upstream, not run here
Upstream spike S4 (PR #41, R6) found no difference between the two mapping paths across 48 request
cases. It mapped `X<User>` both through the generated method and through `MapEndpoint<X<User>>()`,
with and without MVC, and sent each one a valid body, a malformed body (400), the wrong content type
(415), an empty body, and a form-urlencoded `BindRequestAsync` override. Binding lives in
`EndpointBase<T>`/`BodyEndpoint<T>`, so the mapper makes no difference, and no fix is needed.

What remains for Spark, checked in M3 and M5 rather than in a spike:
- every typed generic endpoint is `partial` (otherwise MPEP001);
- the status codes and response bodies stay as they are today. Typed endpoints return 415 for an
  empty body with no content type, and a bare 400 for a JSON `null`. Compare both with today's
  minimal-API `[FromBody]` responses for `register` (`AccountRouteClassificationTests` plus a JSON
  check).

### S2 — An identity_provider handler folded into a generic class closed at startup (D2)
1. Fold `Token` into its endpoint class as a typed endpoint with a **form-urlencoded**
   `BindRequestAsync` override, and `[Inject]` `IDocumentStore`, `OidcTokenGenerator` and
   `OidcSigningKeyService`. Drop the static handler and the `Results.Empty` wrapper.
2. Make `Login<TUser>` with `[Inject] SignInManager<TUser>`. Close it in the identity_provider map
   callback from `registry.IdentityUserType` (`MakeGenericType` plus one reflective
   `MapEndpoint<T>()`), and fail at startup when it is null.

**Pass:** the OIDC E2E flow (authorize → login → consent → token → userinfo) is unchanged; a
form-encoded token request with a missing `grant_type` gives the same RFC 6749 error as today.

### S3 — Account routes with endpoint-level `IsEnabled` (Spark side of upstream R5)
The API itself is specified upstream (R5): it is evaluated after the group chain, and a disabled
endpoint has no `GetPath`/`Configure` call and `IsEndpointMapped<T>` false. In Spark, model the
account routes with one `SparkAuthManageGroup` (which carries `RequireAuthorization` in
`Configure(group, services)`) plus a `LocalCredentialMode` helper used by each endpoint's `IsEnabled`.
**Pass:** for each of the three modes, the account route table matches today's (M0 snapshot).

### S4 — #36 and #37 in Spark (consumer side of upstream R1, R2)
- **#36:** rewrite `OidcCors.Apply` on `Configure(group, services)` and delete the
  `IEndpointRouteBuilder` cast (`Groups.cs:75-85`).
- **#37:** convert the dev WebSocket to an endpoint class:
  - `GetPath` returns `options.DevWebSocketPath`, whose default references the endpoint's `Path`
    constant;
  - `IsEnabled` checks `DevelopmentAppId`;
  - `[Inject]` `IGitHubClientFactory`, `IOptions<GitHubWebhooksOptions>` and `IDevWebSocketService`.

  Check that `IDevWebSocketService` (registered conditionally) is never resolved when the endpoint is
  disabled. `DevWebSocketEndpointTests` must stay green, the CSWSH guard (`SparkMiddleware.cs:387`)
  must still apply, and a path with mismatched route parameters must fail at startup (upstream
  R2.10).

---

## Milestones

### U — Upstream release (MintPlayer.AspNetCore.Tools, published before Spark adopts)
PR #41 delivers PRD §6a items 1–5 (typed binding needs only regression tests). Then publish
11.4.0-rc.0, which closes #36 and #37 (#40 is already closed as not planned; see PRD D6). Check that
it does not force a `MintPlayer.SourceGenerators` bump.

### M0 — Snapshot the route table (on `master`, before any Spark change)
A committed test fixture dumping `EndpointDataSource` for Fleet, QnA, HR, DemoApp and CodeCoverage,
and for each local-credentials mode. For each route it records the verb and pattern, plus these
metadata types:
- `IAuthorizeData`, `IAllowAnonymous`;
- `IAntiforgeryMetadata`;
- rate limiting;
- CORS;
- `RequestSizeLimit`;
- `ResponseCache`;
- `IAcceptsMetadata`.

Every later milestone diffs against it; the only allowed difference is dead code that was removed.
#455 skipped this fixture (see its plan's M0), so this time it is mandatory.

### M1 — Adopt the upstream rc
- Bump every Endpoints reference to 11.4.0-rc.0. There is no Directory.Packages.props, so this is per
  csproj.
- Migrate every `Configure` hook to the two-argument form: 51 explicit
  `static void IEndpointBase.Configure(RouteHandlerBuilder)` and `IEndpointGroup.Configure(RouteGroupBuilder)`
  implementations (they fail with CS0539), plus the static helper `ModerationEndpoint.Configure`
  (`ModerationEndpoints.cs:62`) that 15 moderation endpoints delegate to.
- MPEP036 (a class that is both group and endpoint) fires nowhere: checked 2026-10-07, no Spark class
  is both.

The build must be green with zero MPEP diagnostics, and the M0 snapshot must be unchanged.

### M2 — Contributions and Spark core service-locator holdouts
- `RevertContribution : IPostEndpoint<…>` with 7 `[Inject]` fields. It needs a package reference,
  `[assembly: EndpointsMethodName("MapSparkContributionsEndpoints")]`, and antiforgery set the same
  way as the other `/spark/po` add-ons. Delete `ContributionEndpoints`.
- `SparkAddOnEndpoints` becomes an injected instance service.
- `ILoggerFactory` lookups become `[Inject] ILogger<T>`.

### M3 — Typed endpoints across existing classes (D3)
Convert the ~27 endpoint files that parse input by hand (list in PRD §6 D3) to typed endpoints with
`[RouteParam]`/`[QueryParam]`. Do it one assembly per commit: spark core, moderation, replication,
then authorization (external login, passkeys). The response shapes must not change; the protocol
client (`libs/client/MintPlayer.Spark.Client/SparkClient.Endpoints.cs`) is the consumer contract.

### M4 — identity_provider (D2, D3, D6)
- Fold all 16 static handlers into typed endpoint classes with `[Inject]`.
- The five user-touching endpoints (`Login`, `Logout`, `Token`, `TwoFactor`, `UserInfo`) become
  generic over the user type and are closed once at startup.
- `Token`, `Revocation` and `Introspection` override `BindRequestAsync` to read form-urlencoded
  bodies.
- `OidcCors.Apply` moves to #36.
- Delete `Endpoints/*.cs` static handlers.
- Add a test asserting `frame-ancestors 'none'` and `X-Frame-Options: DENY` on a real
  `/connect/authorize` response (D6).
- Change the anti-framing middleware (`SparkIdentityProviderExtensions.cs:113`) to match
  `StartsWithSegments(OidcConnectGroup.Prefix)` instead of the literal `"/connect"` (D6).

### M5 — Authorization account routes (A, J)
- The 15 `SparkAccountEndpoints` routes become typed generic classes with `[Inject]`, mapped with
  `MapEndpoint<X<TUser>>()` inside `MapSparkIdentityApi<TUser>`.
- One `SparkAuthManageGroup` carries `RequireAuthorization`.
- Each mode-dependent endpoint declares `IsEnabled` through `LocalCredentialMode`; there is no `if`
  around mapping.
- Replace the existing `if`s around the passkey and external-login `MapEndpoint` calls
  (`SparkAuthenticationExtensions.cs:169-180,231-240`, `PasskeyEndpoints.cs:70-81`) with group or
  endpoint `IsEnabled`.
- `GetAuthCapabilities` and `ExternalLoginCallback.cs:104` move to `[Inject]`.
- Delete `SparkAccountEndpoints` and the dead `authGroup` (`:150`).

### M6 — Applications (D1b)
- **CodeCoverage:** add the Endpoints package and an `AssemblyInfo`. Turn `/health` and
  `/health/ready` into endpoint classes; keep the paths, anonymous access and the 503 behaviour, with
  `[Inject] IGitHubAppReadinessService`.
- **QnA test seams:** a `/qna-test/moderation` group with `IsEnabled` on the `testSeams` flag and
  antiforgery `true`. Keep the throw in Production. Delete the static `QnATestSeams.Map`.
- Controllers are untouched (D1).

### M7 — Webhooks (D4)
The dev WebSocket becomes an endpoint class (per S4), and the webhook POST stays on Octokit. Rewrite
`endpoints_generator_webhooks_exception.md`: the blocker is Octokit owning the signature check, not
the path.

### M8 — Guard against regression
Add a test, or a BannedApiAnalyzers rule keyed on the project **name** rather than `IsTestProject`
(BannedApi config keyed on `IsTestProject` was never restored, per a memory note). It must fail on:
- `RequestServices.Get*`, `[FromServices]` or `ReadFromJsonAsync` inside generator endpoint classes
  (libs and apps);
- a static class holding endpoint logic;
- `MapGet`/`MapPost`/… in libraries outside the allow-list.

The allow-list holds the §5 exceptions, each with its reason: Octokit webhooks, the `MapIdentityApi`
republish, and `MapControllers`.

### M9 — Docs and the sweep
- Supersede #455's PRD D3 (static handlers accepted) in `endpoints_generator_adoption_PRD.md`.
- Record `MapIdentityApi` as an exception.
- Update the README route tables.
- Version diff check: no major bump; API breaks are minor bumps with a release note.

Then one full local sweep, writing the log raw:
`RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected > <scratchpad>/sweep.log 2>&1; echo "EXIT: $?"`.
The M0 snapshot diff must be empty. Then push for CI.
