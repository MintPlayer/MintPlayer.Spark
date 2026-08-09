# Findings — replication mTLS / cross-module authentication

**Date:** 2026-08-08
**Status:** Investigated, not yet actioned. **Not** in scope of [PRD-CoverageHandoff.md](./PRD-CoverageHandoff.md).
**Origin:** a design question — should client-certificate auth be generalized so Spark↔Spark calls and external POSTs share one authorization pipeline? Investigating that surfaced defects in the mTLS that already exists.

**Headline:** Spark already has mTLS (added by `ae37fed`, Security Audit Round 2, for R2-C1/R2-C2/R2-H7). It guards `/spark/etl/deploy` and `/spark/sync/apply`. It is **substantially broken in ways that make it ineffective in production**, and fixing what exists is higher-value than generalizing it.

---

## F1 — `Development` mode is an authentication no-op (High)

`ModuleCertificateValidator.cs:65-78`. The comment at `:68-69` claims *"Requesting module must still exist in SparkModules — that's free identity-level sanity."* **No such check exists** — no `LoadAsync`, no store access. The code tests `string.IsNullOrEmpty(requestingModule)`, logs a warning, and returns `Ok`.

`guide-replication-mtls.md:29` repeats the same false claim ("still verify the module is registered").

Consequence: in `Development` mode, `{"RequestingModule": "anything"}` from an unauthenticated caller reaches `/spark/sync/apply`, which can insert, update, or delete any document in any collection.

**Same defect class as `OnQueryAsync`** (PRD §5): a comment asserting a guarantee the code doesn't implement — worse than no comment, because it stops reviewers looking.

### The tests don't cover it, and one is accidentally vacuous

- `ModuleCertificateValidatorTests.cs:33` constructs the validator with `registrationService: null!`, commenting that it's *"only dereferenced on the Production thumbprint-lookup path."* Yet `:54-60` (`Auto_in_Development_with_known_module_is_Ok_without_a_cert`) passes `"HR"` and asserts `Ok`. **A null service cannot check registration** — the test name says "known module" while proving nothing is looked up.
- `ReplicationEndpointAuthTests.cs:24-50` posts `RequestingModule = "Attacker-Not-Registered"` and asserts `BeOneOf([Forbidden, Unauthorized])`. Its docstring claims the host runs in Development mode; under Development that input returns `Ok`/200 and the test would fail. It passes because the E2E host is **not** Development (`Auto`→`Production`→`MissingCertificate`→401). **The assertion is loose enough to pass under either mode, so it pins neither.**

### How you reach the permissive branch

A *missing* `ASPNETCORE_ENVIRONMENT` is safe — ASP.NET defaults to Production, so `Auto` (the enum default) resolves to `Production` (`:45-51`). The realistic trigger is a **set** value: `ASPNETCORE_ENVIRONMENT=Development` in a container for dev-time diagnostics silently converts the framework's two most dangerous endpoints into unauthenticated ones. Nothing about that variable suggests "disables mTLS."

`Disabled` mode (`:59-63`) returns `Ok` with **no log line at all** — a silent kill switch, one JSON string away, leaving no trace.

## F2 — The documented configuration binds to nothing (High, operator-facing)

`guide-replication-mtls.md:99-114` instructs operators to configure `Spark:Replication:ClientCertificate:*`. The demos hand-map only four properties from a `SparkReplication` section (`Demo/HR/HR/Program.cs:31-40`, `Demo/Fleet/Fleet/Program.cs:18-27`), and `Demo/HR/HR/appsettings.json:15-20` has no `ClientCertificate` node at all.

An operator can follow the guide exactly — set `Mode`, `Thumbprint`, `CertificateFile` — restart, and get **zero behaviour change and no error**.

## F3 — TLS termination makes mTLS structurally impossible in this repo's own deployment (High)

`Demo/WebhooksDemo/docker-compose.yml` runs behind Traefik terminating TLS at the edge (`traefik.http.routers.spark-webhooks.tls.certresolver=letsencrypt`), forwarding plain HTTP to the container. `context.Connection.ClientCertificate` (`ModuleCertificateValidator.cs:84`) is therefore **always null** there.

All four demos configure `ForwardedHeaders`, but only `XForwardedFor | XForwardedProto | XForwardedHost`. `ForwardedHeadersMiddleware` has no client-cert concept — that is `Microsoft.AspNetCore.Certificate.Forwarding` / `AddCertificateForwarding`, which is **not referenced anywhere**. Zero hits for `X-ARR-ClientCert`, `X-Client-Cert`, `ssl-client-cert`.

The guide never mentions proxies, and its troubleshooting table (`:174`) points at Kestrel's `ClientCertificateMode`, which is inapplicable behind a terminating proxy.

**Operator's actual path:** follow guide → config binds to nothing (F2) → fix binding, set `Production` → every cross-module call 401s → troubleshooting doesn't apply → escape via `Development` or `Disabled` (F1). That path from "following the docs" to "authentication off" is short and unguarded.

### Sharp edge for whoever implements forwarding

Every demo calls `options.KnownNetworks.Clear(); options.KnownProxies.Clear();` — trust `X-Forwarded-*` from anyone. Tolerable for scheme/host. **If cert forwarding is added under that same posture, any client can forge a cert header and impersonate any module.** Certificate forwarding must ship with a trusted-proxy allowlist, not inherit this default.

## F4 — Authenticated modules are omnipotent; the sync write path bypasses authorization (High)

`SyncApply.cs:83,107` → `SyncActionHandler.cs:41,61` → `SaveEntityViaActionsAsync` (`:237-248`) reflectively invokes `OnSaveAsync`/`OnDeleteAsync` **directly**, bypassing the `DatabaseAccess` chokepoint where every normal CRUD path calls `EnsureAuthorizedAsync` (`DatabaseAccess.cs:83,115,195,256`).

So the cert check is all-or-nothing: once a module authenticates, there is no notion of *which* module may touch *what*.

**This is a third instance of the root cause behind PRD §5** — a data path that doesn't route through the single enforcement point. The first two are the query/stream read paths and the dead `OnQueryAsync`. Whatever chokepoint M5 establishes should be designed to cover **write** paths too, or this recurs.

## F12 — `/spark/etl/deploy` has no read authorization at all (High) — raised 2026-08-09

F4 covers the **write** direction. This is the **read** direction, and this document did not mention `/spark/etl/deploy` at all.

A consumer POSTs `EtlScriptRequest { RequestingModule, TargetDatabase, TargetUrls[], Scripts[] }`. Each `EtlScriptItem` carries a free-text `SourceCollection` and a raw RavenDB JS transform. `EtlDeploy.cs` runs the mTLS gate and then calls `EtlTaskManager.DeployAsync` **unconditionally**; the manager builds `RavenEtlConfiguration.Transforms` verbatim from the request. The only scope limit anywhere is the self-loop refusal (`TargetDatabase == documentStore.Database`), which is an infinite-loop guard, not a security control.

**So the certificate answered "who are you" and nothing ever answered "what may you read."** Any module holding a valid pinned certificate could name `SourceCollection = "SparkUsers"` and have the owner stand up a *continuous* RavenDB ETL task pushing every user document — through a transform the caller wrote, which can `loadDocument` to pivot into other collections — into a database the caller controls.

Three things make this worse than the equivalent one-shot read:

1. **It is ongoing.** An ETL task keeps running and keeps pushing new and changed documents until someone removes it.
2. **`[Replicated]` gives false confidence.** It reads like a declaration of what may be shared. It lives on the *consumer*, is consumed only by `EtlScriptCollector` when building the outbound request, and **the owner never sees it** — so "what gets replicated" was entirely the requester's say-so.
3. **The target is attacker-supplied**, the same property R2-C1 flagged for the write path.

**Fixed 2026-08-09.** `EtlDeploy` now checks `IPermissionService.IsAllowedAsync("Replicate", SourceCollection)` per script, after establishing the module identity. An owner declares what it will share by granting `Module:{Name}` the right `Replicate/{Collection}` in `security.json`.

## F13 — The replication endpoints validated a caller without authenticating it (High) — raised and fixed 2026-08-09

Found immediately after M11 shipped, and it is a defect **in M11's own fix**.

`SyncApply` and `EtlDeploy` call `IModuleCertificateValidator.ValidateAsync`, which decides whether the request proceeds — and never touches `HttpContext.User`. Validating is not authenticating. That was harmless while the endpoints did their own gating, and became a defect the moment M11 routed cross-module writes through `IPermissionService`: the writes arrived **anonymous**, holding only `Everyone`'s rights, so every cross-module sync would have been refused. The gate said "yes, this is HR" and the permission check was never told.

No test caught it, for the reason M11's own notes had already recorded: **nothing exercises a successful cross-module sync end to end.** The routing was unit-tested against a substitute; the identity it depended on was not.

Both endpoints now call `HttpContext.EstablishModuleIdentity(module)` after validation, emitting the same `Module:{Name}` group claim as the certificate authentication scheme — so an operator writes one `security.json` entry regardless of which path established the identity. Trusting the body's module name is sound *there* precisely because validation has already run: in Production it was checked against that module's pinned thumbprint.

## F5 — Dead code and a non-functional documented feature (Medium)

`IReplicationHttpClientProvider` is registered (`SparkReplicationExtensions.cs:38`) and **never resolved**. Both outbound paths use named clients instead — `EtlScriptDeploymentRecipient.cs:31` (`"spark-etl"`), `SyncActionSubscriptionWorker.cs:56` (`"spark-sync"`) — built by `BuildDefaultHandler` (`:57-69`), which only ever attaches the default certificate.

Therefore **`PerTargetOverrides` is entirely non-functional**, despite being documented as supported at `guide-replication-mtls.md:129-155`.

## F6 — Per-request `DocumentStore` construction (Medium)

`ModuleCertificateValidator` is a singleton (`:33`) that constructs and initializes a fresh RavenDB `DocumentStore` on **every** Production validation (`:91`) — i.e. unauthenticated requests drive store creation and teardown.

---

## If we do generalize it into an authentication scheme

The refactor itself is small and the payoff is direct, because `ClaimsGroupMembershipProvider.cs:28-44` reads groups off `HttpContext.User` claims and nothing else. Emitting `new Claim("group", "Module:HR")` from `OnCertificateValidated` lands a module in `AccessControlService` → `security.json` with **zero framework changes**.

**Separability is good.** `ModuleCertificateValidator`'s only real coupling is `registrationService.CreateModulesStore()` (`:91`), itself a self-contained 8-line factory (`ModuleRegistrationService.cs:25-34`). No ETL entanglement.

**Correction to the obvious plan:** looking modules up *by thumbprint* would need a reverse index — `ModuleInformation` is keyed `moduleInformations/{name}` with the thumbprint as a field (`ModuleRegistrationService.cs:58`). **Prefer reading `CN` from the certificate subject** (the guide's own generation recipe sets `CN=$MODULE`, `:65-66`) and keeping the by-name load plus pin verification. That still removes the request-body trust step without a schema change.

**Two implementation notes:**
- `AllowedCertificateTypes = CertificateTypes.All` and `ValidateCertificateUse = false` are required, or the framework's chain validation rejects the self-signed CA the guide tells operators to create (`:59-68`).
- A certificate authenticates the **connection**, so under keep-alive/HTTP-2 every request on it reuses the ticket — de-registration or a revoked pin would only take effect on the next connection. The current inline design accidentally gets this right by hitting Raven every call; keep `OnCertificateValidated` a live lookup or give it an explicit short TTL.

**Correcting an earlier assumption:** a certificate scheme **can** abstain. `CertificateAuthenticationHandler` returns `NoResult()` when the request isn't HTTPS or carries no client cert — so the `NoResult()` discipline that lets cookie and PAT coexist applies to certs too. Cert and token key off disjoint inputs (TLS connection vs `Authorization` header) and never contend. "Cert AND token" is not the default and needs an explicit policy.

**Scope→group mapping** is the identical question as PRD §2 risk 2, with the identical answer. Solving it once for both schemes is a real argument for doing them together.

---

# Part 2 — Making *any* external credential work

The first investigation asked whether mTLS could be generalized. The second asked what it would take for every credential type to share one authorization pipeline. The answer to the second reframes the first.

**The authorization *decision* layer is ready. The authentication *plumbing* is not.** "Map every credential to group claims, then let `IAccessControl` do the rest" is correct and needs **zero** changes to `AccessControlService`, `PermissionService`, `ClaimsGroupMembershipProvider`, or the `security.json` format. But two things sit outside that, and both are hard blockers.

## F7 — Spark endpoints have no authorization metadata, so extra schemes never run (High — this is the blocker)

Grepping all of `libs/` for `RequireAuthorization`, `[Authorize]`, `AuthenticationSchemes` returns **zero hits on any Spark endpoint**. Spark's endpoints are anonymous at the ASP.NET layer and check inside the handler via `IPermissionService`, translating `SparkAccessDeniedException` to 401/403 themselves (`Endpoints/PersistentObject/Create.cs:76-82`).

`UseAuthentication()` (`SparkMiddleware.cs:161-164`) populates `HttpContext.User` from the **default authenticate scheme only**. Extra schemes run only when something explicitly asks — `[Authorize(AuthenticationSchemes=…)]`, `RequireAuthorization`, or a manual `AuthenticateAsync("X")`.

Today's default is `"Identity.BearerAndApplication"`, set implicitly by `AddIdentityApiEndpoints` inside `AddSparkAuthentication` (`SparkAuthenticationExtensions.cs:44-49`). **Spark never overrides it**, and no demo sets one.

**Consequence:** registering an extra scheme the way Coverage does draws no water on Spark's own endpoints. A `covt_…` bearer sent to `POST /spark/{objectTypeId}` authenticates as **nobody** — the composite Identity handler doesn't recognise it, `User` stays anonymous, the group provider returns empty, and the caller gets `Everyone`-only rights. **No error, no "unrecognised credential" log.** Silent, not loud.

Coverage's two-scheme setup works only because `UploadsController.cs:23` opts in explicitly — a hand-written MVC controller *outside* Spark.

## F8 — Antiforgery blocks external POSTs regardless of scheme (High)

`Create.cs:17-20` stamps `RequireAntiforgeryTokenAttribute(true)`, and `SparkMiddleware.cs:181-201` enforces double-submit on any mutating request carrying that metadata — **unconditionally, whatever authenticated it**. An external caller with a bearer token or client certificate has no `XSRF-TOKEN` cookie, so it gets a bare **400 with no body**.

CSRF is a threat only for *ambient* credentials (cookies). Demanding it of a bearer or certificate caller is pure obstruction, and it must be fixed before external POSTs can work through the standard endpoints at all.

## F9 — `security.json` models machine callers fine, with two caveats (Medium)

Good news first: **a non-human caller needs no schema change.** `groups` is `Dictionary<Guid, TranslatedString>` — a name per language, no user list, no membership, no personhood. Membership lives entirely in the claim, resolved by string match (`AccessControlService.ResolveGroupIds`, `:100-118`). Declaring `"…-0009": {"en":"Coverage CI"}` and granting `"New/CoverageReport"` is the whole exercise. The model stretched to machines the moment membership became a claim.

Two caveats:

- **`Everyone` is added unconditionally** (`AccessControlService.cs:48-54`), *before* the no-groups check and regardless of auth state. Every machine caller — and every anonymous one — inherits Everyone's rights. Fleet grants `QueryRead/Company` to Everyone. **A machine identity is never a clean slate.**
- **Property-level rights are documented but dead.** `Right.Resource`'s doc comment advertises `"Edit/DemoApp.Person/Salary"`, but `MatchesResource` is exact string equality (`:120-123`) and nothing in Spark ever constructs a three-segment resource. **Third instance today of a comment promising a guarantee the code doesn't implement** (cf. F1, and `OnQueryAsync` in PRD §5).
- No row-level scoping exists, so "this app may create reports *only for repos it owns*" is inexpressible. Coverage works around it with `covt:account`/`covt:repoid` claims checked in hand-written controllers. That's a genuine model extension — scope separately.

## F10 — The IdP's `client_credentials` is a merge for issuing, a build for accepting (corrects an earlier read)

`feat/identity-provider` implements RFC 6749 §4.4 properly: `HandleClientCredentialsGrant` requires client_id+secret, checks `AllowedGrantTypes`, verifies the secret, validates requested scopes against `AllowedScopes`, and issues an access token only (no id_token, no refresh token, `Subject = "client:{ClientId}"`). Plus introspection (RFC 7662), revocation, JWKS, discovery, and a token-cleanup service. `OidcApplication` supports **secret rotation** — `List<ClientSecret>` with per-secret expiry, any-of-N match.

But three things stop it being a drop-in:

1. **Crypto must not merge as-is.** `VerifyClientSecret` uses unsalted single-round SHA-256 and compares with `string.Equals(Ordinal)` — **not constant-time**. Compare `libs/webhooks/.../SignatureService.cs:36`, which correctly uses `CryptographicOperations.FixedTimeEquals`. The missing salt is defensible for a high-entropy random secret; the comparison is a straight bug. Both are ~10-line fixes.
2. **Tokens authorize as nobody.** `OidcTokenGenerator.GenerateAccessToken` emits application claims prefixed: `new Claim($"client_{cc.Type}", cc.Value)`. So `{Type:"group"}` becomes **`client_group`**, which is not in `ClaimsGroupMembershipProvider.GroupClaimTypes` — a machine token resolves to zero groups. One-line fix, but a deliberate one.
3. **It only issues; nothing consumes.** The resource-server half — a JWT-bearer scheme validating against the IdP's JWKS — **does not exist on the branch**. `AddOidcLogin` there is interactive login, not resource-server validation.

**Corrected verdict:** issuing is a merge; accepting is a build — and the accepting half is the same work whichever credential you choose. Don't block the external-POST case on merging a 13-endpoint OIDC server with consent screens and signing-key management. Do treat `OidcApplication` as the right long-term data model: if an interim local ClientId/Secret store is written, shape it identically so the later migration is a re-point, not a rewrite.

## F11 — Two existing external-caller paths already bypass the pipeline

GitHub webhook signature validation (`SignatureService.cs:11-37`) has **correct crypto** — HMAC-SHA256 over the raw body, fail-closed on empty secret, `FixedTimeEquals`. Worth copying. But like replication mTLS it returns a `bool` and drops the delivery: no `ClaimsPrincipal`, `IPermissionService` never consulted, downstream work runs with `HttpContext.User` anonymous.

**Two external-caller paths on `master`, two bypasses of the authorization pipeline.** The duplication this whole exercise aims to prevent has already happened twice. The one correctly-shaped precedent is in the consuming app, not the framework: Coverage's `ApiTokenAuthenticationHandler` emits a real principal and returns `NoResult()` for foreign credentials.

---

## Recommended ordering

### Immediate, cheap, independent of any redesign

1. **F1 and F2** — a mode that silently disables authentication, and a guide whose configuration binds to nothing. These are the difference between the feature working and merely appearing to. Also fix the two tests that pin nothing.
2. **F9's dead property-level rights** and **F10's crypto** — correct the comments or the code; don't leave documented guarantees unimplemented.

### Phase A — scheme plumbing (prerequisite; nothing else functions without it)

1. A Spark-owned **composite authenticate scheme**: try each registered credential scheme, first `Success` wins, `NoResult` falls through — mirroring the framework's own `CompositeIdentityHandler`. Set it as `DefaultAuthenticateScheme` in `AddSparkAuthentication`. (`AddPolicyScheme` + `ForwardDefaultSelector` is cheaper but sniffs and selects only one; the composite is the honest fit, since handlers already return `NoResult`.)
2. A builder surface to register credential schemes into it — e.g. `spark.AddCredentialScheme<THandler>("Name")`.
3. **Exempt non-cookie-authenticated requests from the antiforgery gate** (F8).

~2–3 days including tests. **Risk: changes the default authenticate scheme and the antiforgery gate for every existing Spark app, and the failure mode is silent** (wrong scheme → anonymous principal → `Everyone` rights) rather than a build break. This needs its own PR and its own regression sweep.

### Phase B — one handler per credential type (small, independent, strictly after A)

Each handler's entire integration with authorization is `new Claim("group", "…")`.

- **Client certificate** — `AddCertificate()` + `OnCertificateValidated`, lifting the pinning and mode ladder out of `ModuleCertificateValidator.cs:45-110`. ~60 LOC. Requires F3's forwarding work to be usable behind a proxy.
- **ClientId/Secret** — either (i) `AddJwtBearer` against the IdP's JWKS: configuration only, zero Spark code, *if* F10's `client_group` issue is fixed; or (ii) a local `SparkClient` document + handler, ~150 LOC modelled on `OidcApplication` but using PBKDF2/Argon2 with a salt and `FixedTimeEquals`.
- **API token** — promote Coverage's handler into the framework, adding group claims.

### Phase C — retire the bypasses

Route `/spark/sync/apply` (F4) and the webhook processor (F11) through the same principal + `IPermissionService`. **This is the phase that actually delivers "no duplication per credential type"** — A and B only prevent *new* duplication. Breaking change for replication users; needs a migration note.

### Phase D — row-level scoping (F9)

Open design question. Overlaps PRD §5. Do not attempt inline.

---

**Blunt version:** the authorization model is ready and stretched to machine callers the moment membership became a claim — that's the part people expect to be hard, and it isn't. The hard parts are that Spark's endpoints carry no authorization metadata (so extra schemes never run), that antiforgery blocks external POSTs regardless of credential, and that two external-caller paths already bypass the pipeline entirely. A `ClaimsPrincipal` that nothing consults buys nothing, so Phase A gates everything.
