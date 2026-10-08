# Plan — Finish the Endpoints-generator adoption, with `[Inject]` and typed endpoints

Companion to [`endpoints_generator_completion_PRD.md`](endpoints_generator_completion_PRD.md); the
decisions are in its §6 (locked 2026-10-07). **One unit of work:** an upstream
`MintPlayer.AspNetCore.Tools` release (published first) plus **one** Spark pull request. Test suites
run once, at the end (M9). Intermediate milestones are verified by build and by reading the code.

**Status (2026-10-08):** upstream 11.4.0-rc.0 is published (#41). M0 and M1 are done. Spikes S2–S4
pass, and their code is adopted: S2 starts M4, S3 completes M5, and S4 is M7's dev WebSocket. The
merged tree builds with 0 errors. The `accepts(application/json)` fixture change is accepted (PRD
D7), and D8 settles how typed handlers reach `HttpContext`. Next: M2, M3, the rest of M4, M6, M7's leftovers, M8, M9.

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

**Verdict (2026-10-08, adopted by M4): ✅ pass.**
- **`/connect/token`** is `OidcTokenEndpoint<TUser> : IPostEndpoint<OidcTokenRequest>`. It has:
  - a form-urlencoded `BindRequestAsync`;
  - an `OnBindFailedAsync` that answers in the RFC 6749 §5.2 shape;
  - `[Inject]` `IDocumentStore`, `OidcTokenGenerator` and `UserManager<TUser>`. `OidcSigningKeyService` is not injected, because only `OidcTokenGenerator` uses it.
- **`POST /connect/login`** is `OidcLoginSubmit<TUser>` with `[Inject] SignInManager<TUser>`. The GET is `OidcLoginPage`.
- **Closing at startup:** `OidcUserEndpoints.Map` closes both endpoints once at startup through one reflective `MakeGenericMethod(userType)` call into a typed `MapFor<TUser>()`, and throws when `IdentityUserType` is null. Each `MapEndpoint<T>()` builds its own group chain, so `/connect`, `OidcCors` and the local-credentials group's `IsEnabled` all still apply. The generator leaves the open generics out of the generated method.
- **Evidence:**
  - 350/351 tests passed across `IdentityProvider.*`, `RouteTableSnapshotTests` and `SignInIdentifierTests`. The one failure was the new startup test asserting in the wrong place; after the fix it passes (`OidcLocalCredentialModeTests` 4/4).
  - `OidcTokenEndpointGuardTests` shows the missing-`grant_type` response (400 `unsupported_grant_type`), the JSON-body response and the no-body response (400 `invalid_request`) are byte-identical before and after. There is no 415.
  - All 10 route snapshots are unchanged.
  - The build has 0 errors.
  - Fleet's snapshot also passes after the change.
- **For M4:**
  - **(a)** Because of failing at startup, a host that adds the identity provider must also register a user type. `OidcAdminRouteTests` needed `AddAuthentication<SparkUser>`. The no-`AddAuthentication` fallback in `SparkIdentityProviderExtensions.LocalCredentialsOf` is now dead; delete it.
  - **(b)** A generic endpoint must not share a model's name. `OidcToken<TUser>` hid `Models.OidcToken` from every other file in that namespace (CS0117/CS0104).
  - **(c)** A typed handler gets no `HttpContext`, so `BindRequestAsync` keeps it in a field. That is safe because endpoints are created per request.
  - **(d)** `OidcLoginSubmit<TUser>` still reads its form by hand; make it typed per D3.
  - **(e)** Every refusal is now `Results.Json(…, statusCode:)` with the same body.
  - **(f)** Logout, TwoFactor and UserInfo join `MapFor<TUser>()`.
  - The E2E `JwtBearerCredentialTests` (Fleet) was not run; it is part of the M9 sweep.

### S3 — Account routes with endpoint-level `IsEnabled` (Spark side of upstream R5)
The API itself is specified upstream (R5): it is evaluated after the group chain, and a disabled
endpoint has no `GetPath`/`Configure` call and `IsEndpointMapped<T>` false. In Spark, model the
account routes with one `SparkAuthManageGroup` (which carries `RequireAuthorization` in
`Configure(group, services)`) plus a `LocalCredentialMode` helper used by each endpoint's `IsEnabled`.
**Pass:** for each of the three modes, the account route table matches today's (M0 snapshot).

**Verdict (2026-10-08; it also completes every M5 bullet): ✅ pass, with one fixture change that the owner must confirm.**
- **Endpoints:** the 15 routes are 14 generic classes in `Authorization/Endpoints/Account/`.
  - Typed: `Register`, `ResendConfirmationEmail`, `ForgotPassword`, `ResetPassword`, `UpdateInfo`, `SetPassword`, `ConfirmEmail`, `UpdateProfile` and `ConfirmEmailLink` (`[QueryParam] required`).
  - Raw: `Profile`, `AuthenticatorUri`, `PersonalData` and `DeleteAccount`. `DeleteAccount` keeps its optional body read by hand, a stated exception: a typed DELETE body would be required.
- **Mapping:** `MapSparkIdentityApi<TUser>` makes only unconditional `MapEndpoint<…>()` calls.
  - `SparkAuthManageGroup` (`/manage`) carries `RequireAuthorization`.
  - `LocalCredentialMode` and `SparkAuthFeatures` read root-provider `IOptions` only. The `localCredentials` parameter is gone, so Microsoft's filter and `IsEnabled` share one source.
- **The rest of M5:**
  - Passkey and linking endpoints have their own `IsEnabled`, so `MapPasskeyApi` and `MapExternalLoginManagement` are gone.
  - `GetAuthCapabilities` and `ExternalLoginCallback` use `[Inject]`.
  - The dead `authGroup` is deleted.
  - The `SparkIdentityEndpoints` stand-ins for routes Spark owns are deleted, because an endpoint carries only one `EndpointTypeMetadata`. This is a minor API break.
- **Fixture change (accepted by the owner 2026-10-08, PRD D7):** the 8 typed POSTs lose `accepts(application/json)`. That is 40 fixture lines in Spark.Tests and 26 in the app projects; routes, authorization and antiforgery are otherwise identical.
  - Cause: the Endpoints rc deliberately declares the body with no content types (`EndpointDocumentation.DeclareRequestBody`).
  - Effect: the 415 for a wrong content type now comes from the endpoint, after authorization and antiforgery have run, so an anonymous `text/plain` POST to `/manage/info` gets 401 where it got 415.
  - Every typed body endpoint in M3 and M4 will show the same diff.
  - The alternative is `.Accepts<T>("application/json")` in each endpoint's `Configure`.
- **Response shapes, measured before and after:**
  - A missing, empty, `null` or malformed body, or a missing query value, gets a bare 400. Any other content type gets a bare 415.
  - Keeping these needs `OnBindFailedAsync → SparkAccount.BindFailed`, because the typed binder would answer 415 or problem+json.
  - Pinned by `AccountFlowTests.Unbindable_requests_answer_a_bare_status_as_before`.
  - Trap: a `[QueryParam]` with an initializer becomes optional.
- **Evidence:** 659/659 tests passed across `Authorization.*`, `XsrfSurfaceTests`, `OidcLocalCredentialModeTests` and `RouteTableSnapshotTests`; the four app snapshot projects passed 1/1 each; the build has 0 errors.
- **Open for M3/M9:**
  - Typed handlers reach `HttpContext` through `[Inject] IHttpContextAccessor`; S2 instead keeps it from `BindRequestAsync`. Pick one in M3.
  - With `AddControllers`, typed bodies bind through MVC's JSON options.
  - The logger category for account deletion changed.
  - `QnAAccountTests` (E2E) and `Client.Tests` were not run.

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

**Verdict (2026-10-08, adopted by M7): ✅ pass.**
- **#36:** no `IEndpointRouteBuilder` cast is left in identity_provider (done in M1).
- **#37:** the dev WebSocket is `DevWebSocketEndpoint`: `IGetEndpoint`, `partial`, with `[Inject]` `IGitHubClientFactory`, `IOptions<GitHubWebhooksOptions>` and `IDevWebSocketService`.
  - `GetPath` returns `options.DevWebSocketPath`, whose default is `DevWebSocketEndpoint.DefaultPath`.
  - `IsEnabled` checks `DevelopmentAppId`.
  - The generated `MapSparkWebhooksGitHubEndpoints()` maps it; the webhook POST stays on `MapGitHubWebhooks`.
- **Evidence:**
  - A disabled endpoint never resolves `IDevWebSocketService`. `DevWebSocketEndpointMappingTests` registers a factory that throws and passes; it fails red when the gate is forced open. `GetPath` is not called while the endpoint is disabled, also shown red→green.
  - A configured path containing `{tenant}` throws `InvalidOperationException` at startup.
  - The cross-origin upgrade still gets 403 through `UseSpark()` (`DevWebSocketOriginGuardTests`), with a 101 control.
  - The `webhooks` and `webhooks-dev-app` snapshots are unchanged.
  - 88/88 tests passed across `Webhooks.GitHub.*` and `RouteTableSnapshotTests`.
- **For M7/M9:**
  - **(a)** PRD D4's reason must drop "dispatch". `ProcessWebhookAsync` is public, and Spark re-verifies the signature itself (`SparkWebhookEventProcessor.cs:45-56`). Octokit owns only the request-level refusal. The decision stands.
  - **(b)** `IDevWebSocketService` is registered from the options passed to `AddGithubWebhooks`, while `IsEnabled` reads `IOptions`. They diverge when an app sets `DevelopmentAppId` through `Configure`. Register the service unconditionally.
  - **(c)** In a test process with freshly built binaries, the first Octokit `User.Current()` call to WireMock took 8.9 s against the 10 s bound of `DevWebSocketEndpointTests.A_developer_outside_the_allow_list_is_closed`; warm, it takes 0.7–1.0 s. S4 did not change that code. Locate the cause before M9; do not call it a flake.

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

**M0 — as built (2026-10-08, fixtures generated from code equal to master `c05e7a52`).**
- **Format** (`libs/testing/MintPlayer.Spark.Testing/RouteTableSnapshot.cs`): one line per
  `RouteEndpoint`, `METHODS /pattern | fact | fact`, sorted by pattern then methods. Facts: every
  distinct `IAuthorizeData` and `AuthorizationPolicy`, `allow-anonymous`, and the *effective* (last
  wins) antiforgery, rate limiting, CORS, request size limit, response/output cache, plus accepted
  content types. No display names, handler types, endpoint-type metadata or order. Patterns are
  normalised (leading `/`, no doubled or trailing `/`), the spellings routing treats as equal.
- **Library hosts** (`tests/MintPlayer.Spark.Tests/Endpoints/RouteTableSnapshotTests.cs`,
  `SparkEndpointFactory`, Development env, fixtures in `Endpoints/RouteSnapshots/`): `core`;
  `auth-{full,signinonly,disabled}` (defaults); `auth-{…}-all-options` (passkeys, external-login linking
  `WhenSignedIn`, email change, identity provider with dynamic CORS); `modules` (soft-delete, history,
  contributions, moderation, replication, mail); `webhooks` and `webhooks-dev-app` (`DevelopmentAppId`
  adds `/spark/github/dev-ws`).
- **Applications**, each its real `Program` in-process through `WebApplicationFactory`, environment
  `RouteSnapshot` (so no `UseAngularCliServer`), the app directory as content root, embedded RavenDB,
  every hosted service except `GenericWebHostService` removed (`tests/Shared/SparkAppRouteHost.cs`):
  `apps/{Fleet,QnA,HR,DemoApp}/{App}.Tests/RouteSnapshots/{App}.txt` with the E2E hosts' settings
  (Fleet with issuer + JWT bearer, HR with issuer, QnA with test seams on), and
  `apps/CodeCoverage/CodeCoverage.Tests/RouteSnapshots/CodeCoverage.txt` through the existing
  `CoverageWebHostFixture`. MVC controller routes are included.
- **Why four new test projects** rather than one: `SparkLayerCatalog` composes library layers once per
  process from the whole dependency closure. With all four apps referenced by one test project, Fleet and
  HR failed startup demanding QnA's `moderation:reviewers` binding and DemoApp's model hash did not
  match. One application per process is the only faithful shape. No substitution was needed.
- **Falsifiable:** dropping `RequireAntiforgeryTokenAttribute(true)` from `Logout` failed 7 of the 10
  library hosts with `- POST /spark/auth/logout | antiforgery=required` / `+ POST /spark/auth/logout`.
  Adding `.RequireAuthorization()` to CodeCoverage's `/health` failed its snapshot with
  `+ GET /health | authorize(policy=;roles=;schemes=)`. Both changes were reverted.
- **Run / update:** `dotnet test <project> --filter FullyQualifiedName~RouteTableSnapshotTests` (the app
  projects contain only this test). To accept an intended change, set `SPARK_UPDATE_ROUTE_SNAPSHOT=1`,
  run the same, and review the fixture diff like code. All six projects take about 65 s wall in total, of
  which booting the four apps is most.

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

**M1 — ✅ as built (2026-10-08).**
- `MintPlayer.AspNetCore.Endpoints` 11.3.0-rc.0 → 11.4.0-rc.0 in the 8 csproj that reference it
  (Spark, Authorization, History, IdentityProvider, MailManager, Moderation, Replication, SoftDelete).
  No other package moved; `MintPlayer.SourceGenerators` stays 12.1.1.
- **52 hooks** gained `IServiceProvider services` (the plan's 51 was one short): Spark 16,
  Moderation 15, Authorization 8, IdentityProvider 6 (2 group + 4 endpoint), History 3, MailManager 2,
  SoftDelete 2, Replication 0. The static helper `ModerationEndpoint.Configure(RouteHandlerBuilder)` keeps
  its signature; it is `internal` and so not a hook (README: a non-public `Configure` is not flagged).
- **OidcCors (#36):** `OidcCors.Apply(group, services)` takes the provider from
  `IEndpointGroup.Configure(group, services)`; the `((IEndpointRouteBuilder)group).ServiceProvider` cast and
  its "upstream would remove this" remark are gone. Same root provider, same map-time moment, so no
  behaviour change; the `auth-*-all-options` snapshots (`cors=policy:SparkOidcCors`) prove it.
- Two test helpers (`ExecuteCustomActionTests`, `LogoutTests`) invoked `TEndpoint.Configure(builder)`
  directly and now pass `app.Services`.
- README "Upgrading to 11.4" checks: no endpoint declares `IsEnabled`/`GetPath` that would turn into an
  implicit hook (the only `IsEnabled` is `QnATestSeams.IsEnabled(IConfiguration, IHostEnvironment)`, not
  an endpoint); nothing constructs or deconstructs `EndpointDescriptor`; no class is both group and
  endpoint (MPEP036).
- **Evidence:** `dotnet build MintPlayer.Spark.slnx` 0 errors, 0 `MPEP0` lines in the log. The six
  route-snapshot projects in compare mode: 10 + 1 + 1 + 1 + 1 + 1 passed, fixtures unchanged.

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

**As built (2026-10-08), M2 ✅ and M3 ✅ for D3's measured list:**
- **M2:**
  - `RevertContribution : IPostEndpoint<RevertContributionRequest>` has 9 `[Inject]` fields and is mapped through `ContributionsPersistentObjectGroup`.
  - `ISparkAddOnEndpoints` is a scoped service, which is a minor API break; `docs/guide-concurrency.md` is updated.
  - The loggers are `ILogger<T>`, so the log categories for lookup references, the streaming query and sync are now class names.
- **M3:**
  - Moderation (15), replication, lookup-reference add/update, revert and passkey sign-in are typed. Each overrides `OnBindFailedAsync`.
  - Five unbindable bodies were measured before and after on each endpoint and pinned in a `…Unbindable…as_before` test.
  - **Deliberate changes:** three request classes that used to escape as unhandled 500s now get the endpoint's normal 400 or refusal:
    - empty or malformed JSON on lookup-reference add and update;
    - a missing content type or `text/plain` on moderation;
    - a missing content type or `text/plain` on revert.
  - Raw endpoints with `[RouteParam]`/`[QueryParam]` are used where binding cannot fail: lookup-reference get and delete, types, permissions, the query stream, and external login.
- **Evidence:**
  - 642/642 targeted tests and Client.Tests 108/108 passed.
  - All 15 snapshots are unchanged: no converted endpoint had `accepts` metadata to lose.
  - The merged tree (M2–M7) builds with 0 errors.
- **For M8:** forbid `Request.Query[`, `RouteValues`, `ReadFromJsonAsync` and `RequestServices` inside endpoint classes, rather than requiring `I…Endpoint<T>`. The legitimate remaining uses are `SparkDenial`'s registry lookup and `ExternalLoginOutcome`'s `popup` check.
- **Next: M3b (PRD D3a):** the ~14 `SparkRequestType.ReadAsync` endpoints, SoftDelete and History, and mail's query reads.

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

**As built (2026-10-08): ✅ done.**
- **Endpoints:** every handler lives in its endpoint class.
  - Generic over the user type and closed in `OidcUserEndpoints.MapFor<TUser>()`: token, userinfo, logout, login submit and two-factor submit.
  - Typed form bodies: token, login, two-factor, revoke, introspect, consent and the application revoke. Typed GETs with `[QueryParam]`: authorize, consent, applications, login and two-factor pages, and logout.
  - Raw: discovery, jwks and userinfo.
- **Other changes:**
  - `OidcIssuer` is now an injected singleton.
  - The shared grant and code rules live in the session-only `OidcAuthorizationFlow`.
  - Anti-framing keys on `OidcConnectGroup.Prefix`; its test covers `/connect/authorize`, an unmapped `/connect/…`, and `/connectx` (which is not framed).
  - The no-`AddAuthentication` fallback is deleted.
- **Evidence:**
  - `OidcResponseShapeTests` (14 tests) pinned status, content type, `Location` and body on the old handlers and passes unchanged on the new classes.
  - 365/365 tests passed across `IdentityProvider.*`, `RouteTableSnapshotTests` and `SignInIdentifierTests`.
  - E2E `JwtBearerCredentialTests` passed 3/3, and the Fleet and HR snapshots passed.
  - No fixture changed: the `/connect` POSTs never carried `accepts` metadata.
- **One accepted ordering change:** `/connect/applications/revoke` now binds the form before it checks sign-in. This is unobservable for form posts.
- **For M8:**
  - Allow-list the pure static helpers: `ConnectPage`/`ConnectResults`, `ConnectPageTheme`, `RedirectUrl`, `InteractiveUserExtensions`, `Token` (the scope and secret helpers), `OidcAuthorizationFlow`, `OidcCors` and `OidcUserEndpoints`.
  - A new user-generic endpoint that is not added to `MapFor` compiles but is never mapped, because MPEP025 is only Info. The snapshots are the safety net.

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

**As built (2026-10-08): ✅ done.**
- **CodeCoverage:** `/health` and `/health/ready` are generator endpoints (`Health/`, `[Inject] IGitHubAppReadinessService`, `MapCodeCoverageEndpoints()`).
  - The snapshot is unchanged.
  - The 503 for a failed readiness probe was untested before; it is now pinned by `ReadinessEndpointTests` (5).
- **QnA:**
  - The seams are `QnATestSeamsGroup`. Its `IsEnabled` reads configuration and environment from the root provider, the Production throw happens at startup, and antiforgery is set on the group.
  - The group holds `CreditSeam`, `DetectSeam` and a typed `RecomputeSeam`. `QnATestSeams.Map` and the `if` are gone.
  - The only fixture diff is D7's on `/recompute`. `TestSeamsGroupTests` (4) pins the gate.
- **Other apps:** no other app has minimal-API routes (grep for `.Map*(`).
- **Tests:** CodeCoverage.Tests 7/7 and QnA.Tests 5/5 passed. The QnA E2E tests were not run; the M9 sweep covers them.

### M7 — Webhooks (D4)
The dev WebSocket becomes an endpoint class (per S4), and the webhook POST stays on Octokit. Rewrite
`endpoints_generator_webhooks_exception.md`: the blocker is Octokit owning the signature check, not
the path.

**As built (2026-10-08): ✅ done.**
- **Dev WebSocket:** it is S4's `DevWebSocketEndpoint`. `IDevWebSocketService` is now registered unconditionally (red→green: a `DevelopmentAppId` set through `Configure` used to map an endpoint that could not be activated). Registering it starts nothing.
- **S4 finding (c), located:** the cause is WireMock.Net 2.15's first-request plugin scan, not Octokit, DNS or the proxy.
  - `TypeLoader.TryFindTypeInDlls` calls `Assembly.Load` and `GetTypes()` on every DLL in the test project's bin (168 of them) to find `WireMock.Net.MimePart`.
  - It costs 0.9 s warm and 10.8–24.6 s on freshly written binaries.
  - `WireMockWarmUp` pays it once per process before `DevWebSocketEndpointTests` start their clock. The bounds are unchanged; on cold binaries the class passes and its slowest test takes 766 ms.
- **Tests:** 89/89 across `Webhooks.GitHub.*` and `RouteTableSnapshotTests`.
- **For M9:** the webhook POST still reads `WebhookPath` and `WebhookSecret` from the local options copy; it is inside the D4 exception, so say so in the exception doc. Add the WireMock finding to `docs/test-suite-performance-PRD.md` next to WPAD.

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
