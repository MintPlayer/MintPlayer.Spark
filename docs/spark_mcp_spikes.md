# Spark MCP — spike results S1, S2, S4 (live) and S3, S11 (source-read)

**Date:** 2026-10-12 · **Branch:** `spike/spark-mcp` (local only, never pushed) · **PRD:** `docs/spark_mcp_PRD.md` §0, §4, §6.1, decision log round 4
Throwaway evidence, not production code. Logs quoted below were captured to files and filtered afterwards.

- **S1, S2, S4** were run live against SparkId (sections below).
- **S3, S11** were answered by reading client source code and documentation only. The live sign-ins with
  Codex ≥ 0.148 and Gemini CLI 0.63 are still pending (owner); see [the last section](#s3--s11-source-read-live-confirmation-pending).

## Environment and versions

| Item | Version |
|---|---|
| .NET SDK | 11.0.100-rc.1.26425.128, app targets `net11.0` |
| `ModelContextProtocol.AspNetCore` (server) | **2.2.0** (newest on nuget.org on 2026-10-12; versions after 2.0.0 are 2.1.0, 2.2.0). The package ships `net8.0`/`net9.0`/`net10.0` libs; net11 consumes the `net10.0` build without warnings. |
| `ModelContextProtocol` (scripted client) | 2.2.0. Supported protocol versions: `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, `2026-07-28` (from the package XML docs, `McpClientOptions.ProtocolVersion`). |
| Claude Code | 2.1.296 |
| RavenDB | 7.2.6 local (`localhost:8080`), throwaway database `SparkIdMcpSpike` |

### Which app, and why

**SparkId** (`apps/SparkId`) was used for all three spikes instead of DemoApp/HR/SeoTester:

- **DemoApp** has no Authorization package (CLAUDE.md: "core only, no Authorization"), so it has no composite scheme to test.
- **SparkId** has `spark.AddAuthentication<SparkUser>` with local passwords, which gives the Identity cookie, and `AddIdentityProvider`. Adding `AddSparkResourceServer` makes the Spark composite scheme hold both **cookie and bearer**, which is the PRD's setup 3 (embedded authorization server) in one app.
- **SeoTester** would have needed a GitHub app (S2 asks for a local password account instead) and it has no IdP wired today.
- The question S2 asks is whether the IdP runs inside an app with its own cookie sign-in. SparkId answers it directly: the app's `.SparkAuth.SparkId` cookie is the same sign-in `/connect/authorize` relies on.

### Spike code (all on the branch)

- `apps/SparkId/SparkId/Spike/SpikeMcp.cs` contains:
  - tools `echo`, `whoami`, `permissions`;
  - matching web endpoints `/spike/whoami` and `/spike/permissions`;
  - Development-only `/spike/seed`, `/spike/role` and `/spike/signin` (UserManager/SignInManager, so no SPA or antiforgery dance is needed);
  - the bearer-only `/mcp` policy.
- `apps/SparkId/SparkId/Spike/SpikeStoreGroups.cs` is the S4 prototype: an `IGroupMembershipProvider` that loads `SparkUser.Roles` per request for bearer callers.
- `apps/SparkId/SparkId/Migrations/M_202610120900_SpikeMcpClient.cs` seeds two things:
  - public client `mcp-spike` (PKCE, loopback redirect `http://127.0.0.1:53682/callback`, implicit consent);
  - API resource **named** `https://localhost:5011/mcp`.
- `Program.cs` adds `AddSparkResourceServer("https://localhost:5011", "https://localhost:5011/mcp")`, `AddMcpServer().WithHttpTransport(...)` and `MapMcp("/mcp")`.
- Config switches: `Spike:SessionMode`, `Spike:CookieGuard`, `Spike:StoreGroups`, `Spike:NoSpa`.
- `App_Data/security.json` gets two spike rights:
  - `Read/SpikeDevelopersOnly` → group Developers;
  - `Read/SpikeSignedIn` → the authenticated group.
- `docs/spark_mcp_spikes_assets/` holds the scripted client (`client/Program.cs`, which reads secrets from files and never prints them) and the driver scripts (`s2_flow.sh`, `s12_calls.sh`, `s4.sh`, `cc.sh`).

Run command (SPA off, throwaway DB):
`Spike__NoSpa=true Spark__RavenDb__Database=SparkIdMcpSpike dotnet run --no-build --launch-profile https`.

---

## S1 — the SDK in a Spark host on net11, composite scheme untouched

**Question.** Does `ModelContextProtocol.AspNetCore` 2.x run in a Spark host on net11 with both protocol revisions, stateless? Does a cookie-only request run as anonymous on `/mcp`? Do the `/spark/*` endpoints still behave?

**Result: PASS**, with three findings that change wording in the PRD (below).

(The plan said DemoApp; SparkId was used instead, see "Which app, and why" above. The MCP Inspector was not run.)

### S1a — both protocol revisions, stateless: PASS

The server ran with `SessionMode = Stateless`. The client was the 2.2.0 SDK with an explicit `ProtocolVersion`. Wire log excerpts (`s1a_*.log`):

```
== 2025-11-25
WIRE POST initialize MCP-Protocol-Version=- -> 200 sessionIdHeader=no
WIRE GET  MCP-Protocol-Version=2025-11-25 -> 405 sessionIdHeader=no
NEGOTIATED protocol=2025-11-25 session=(none) server=SparkId
CALL echo isError= => echo: hi 2025-11-25
== 2026-07-28
WIRE POST server/discover MCP-Protocol-Version=2026-07-28 -> 200 sessionIdHeader=no
NEGOTIATED protocol=2026-07-28 session=(none) server=SparkId
CALL echo isError= => echo: hi 2026-07-28
== auto (client default) -> negotiated 2026-07-28
```

**Pure `Stateless` mode already serves 2025-11-25 clients.** Those clients do their `initialize` handshake, get no `Mcp-Session-Id`, and get 405 on GET. **Hybrid** mode (`HttpServerSessionMode.StatefulForInitializeClients`) was run as well (`s12_guard_off.log`): there, 2025-11-25 clients get a session (`session=fqbJ7zlS9OyAW3ZpgXBN9A`) and 2026-07-28 clients get none. The SDK documents that hybrid mode needs **session affinity** for the stateful half (XML doc on `HttpServerSessionMode.Stateful`/`StatefulForInitializeClients`).

**Claude Code 2.1.296** connected to the stateless endpoint: `claude mcp add --transport http spike http://localhost:5012/mcp --scope local`, then `claude mcp list` → `spike: http://localhost:5012/mcp (HTTP) - ✔ Connected` (`cc_http.log`). The entry was removed afterwards and confirmed gone.
- Over `https://localhost:5011` it failed with `UNABLE_TO_VERIFY_LEAF_SIGNATURE`. Node does not trust the ASP.NET dev certificate. This is a local-dev doc note, not a product issue.
- The protocol revision Claude Code picked was not captured.

### S1b — cookie ignored on `/mcp`: PASS

**How it was done.** `MapMcp("/mcp").RequireAuthorization(policy)` with a policy that names **only** the bearer scheme and lets everybody through:

```csharp
o.AddPolicy("Spike:McpBearerOnly", p => p.AddAuthenticationSchemes("Spark:JwtBearer").RequireAssertion(_ => true));
```

ASP.NET's `PolicyEvaluator.AuthenticateAsync` re-authenticates with the policy's schemes only and **replaces `HttpContext.User`**. When no bearer is present, `User` becomes an empty principal. The composite default scheme is not changed.

| Request (`s12_guard_on.log`, then `s12_final.log` to the same effect) | `/mcp` result |
|---|---|
| cookie only, 2026-07-28 and 2025-11-25 | `isAuthenticated:false`, `Read/SpikeSignedIn:false` → **anonymous** |
| bearer only | authenticated as `sub`, `aud:["https://localhost:5011/mcp"]` |
| bearer + cookie | the **bearer** principal (`authenticationType: AuthenticationTypes.Federation`) |
| same cookie on `/spike/whoami` (default composite) | `isAuthenticated:true, name: mcpspike` (control: the cookie itself is valid) |

**Control run without the policy** (`Spike:CookieGuard=false`, `s12_guard_off.log`):
- A cookie-only `/mcp` call runs **as the signed-in user**, with `Read/SpikeDevelopersOnly:true`.
- With **bearer + cookie, the cookie wins**: the composite tries `Identity.Application` first and adopts the first success.

So D3 is not met by default. The per-endpoint policy is what meets it.

### S1c — existing endpoints: PASS

- `SparkId.Tests.RouteTableSnapshotTests` was run as a single class (`test_routes.log`). It "fails" only because routes were **added**:
  ```
  + POST /mcp | accepts(application/json) | authorize(policy=Spike:McpBearerOnly;roles=;schemes=)
  + GET /spike/permissions  + POST /spike/role  + POST /spike/seed  + POST /spike/signin  + GET /spike/whoami
  ```
  No `/spark/*` or `/connect/*` route changed or disappeared.
- Stateless mode maps only `POST /mcp`; there is no GET or DELETE.
- `/spark/auth/me` answered 200 to cookie, bearer and no credential alike, and the cookie and bearer web calls behaved as before.

### S1 findings

1. **`ISparkAuthenticatedSchemeFeature` goes stale.** The composite sets it in middleware (`SparkCompositeAuthenticationHandler.cs:63-69`); the endpoint policy then replaces `User` but not the feature.
   - On `/mcp`, cookie-only: `whoami` shows `isAuthenticated:false` with `compositeScheme:"Identity.Application"`.
   - The antiforgery gate decides from this feature (`SparkAntiforgeryMiddleware.cs:56-70`). It does not run on `/mcp` because `/mcp` is outside the `/spark` prefixes, but `AddMcp` must **clear or reset the feature** when it swaps the principal. Better: authenticate `/mcp` in its own middleware/endpoint filter and skip the composite there, rather than relying on the policy evaluator.
2. **Analyzer SPARK020 is a false positive for this use.**
   - It fires on `RequireAuthorization("<policy name>")` with the message "Spark registers no named policies (a Policy throws at request time)".
   - The policy did **not** throw: the policy was registered by the app, and it ran on every call.
   - `MintPlayer.Spark.Mcp` registers its own policy, so either exempt it in `AuthorizeAttributeAnalyzer.cs` or avoid named policies (inline `AuthorizationPolicy` / endpoint filter).
3. **Origin validation was not probed.** The scripted client sends no `Origin` header and was accepted. Whether the SDK rejects a foreign `Origin` is unverified, so D3's Origin check is planned as our own code.

---

## S2 — the IdentityProvider inside an app with its own cookie sign-in; token for `/mcp`

**Question.** Can authorization code + PKCE (S256) produce an access token whose audience is the `/mcp` canonical URI, with `sub` equal to the local user id? Does `IPermissionService` answer identically for the cookie and the bearer request?

**Result: PARTIAL.**
- The flow works and `sub` is the local id.
- **Parity FAILS as the code stands**: a bearer caller gets **no security.json groups**, and `ISparkCurrentUser.Id` is null for it.

### Steps (`s2_flow.sh`, log `s2_flow.log`)

1. `POST /spike/seed` creates `mcpspike` (throwaway password, `@example.invalid` mail). It became `SparkUsers/dcd9f198-a5c7-4a4b-b497-67276429f9f2`.
2. `POST /spike/role ... Developers add`, then `POST /spike/signin` (`SignInManager.PasswordSignInAsync`) gives the cookie `.SparkAuth.SparkId`.
3. `GET /connect/authorize?response_type=code&client_id=mcp-spike&redirect_uri=http://127.0.0.1:53682/callback&scope=openid https://localhost:5011/mcp.read&code_challenge=<S256>&code_challenge_method=S256&resource=https://localhost:5011/mcp` with the cookie:
   ```
   authorize status: HTTP/1.1 302 Found
   redirect: http://127.0.0.1:53682/callback?code=<redacted>&state=s1&iss=https%3A%2F%2Flocalhost%3A5011
   ```
   RFC 9207 `iss` is present.
4. `POST /connect/token` (public client, `code_verifier`, `resource`) returns 200:
   ```
   jwt header:  {"alg":"RS256","kid":"rsa-…","typ":"at+jwt"}
   jwt payload: {"iss":"https://localhost:5011","aud":["https://localhost:5011/mcp"],
                 "sub":"SparkUsers/dcd9f198-a5c7-4a4b-b497-67276429f9f2","client_id":"mcp-spike",
                 "scope":"openid https://localhost:5011/mcp.read","group":null,"role":null}
   ```
   The response keys were `access_token, expires_in, id_token, token_type`. There was **no refresh token**, because the IdP issues one only when `offline_access` was requested (`Endpoints/Token.cs:259-260`).
5. `/mcp` `whoami` with the bearer, at both revisions: `isAuthenticated:true`, `sub` = the local id, `aud` = the canonical URI. **`sub` equals the local user id: PASS.**

### Parity: identical answers? No (`s12_guard_on.log`)

The same user, the same moment, with the Developers role present:

| Check | cookie → `/spike/permissions` | bearer → `/spike/permissions` | bearer → `/mcp permissions` |
|---|---|---|---|
| `Read/SpikeDevelopersOnly` | **true** | **false** | **false** |
| `Read/SpikeSignedIn` | true | true | true |
| `Read/Nothing` | false | false | false |

Root causes:
- **Groups come from principal claims only.** The path is:
  1. `SecurityFileAccessControl.ResolveDecisionAsync` (`libs/spark/MintPlayer.Spark/Services/SecurityFileAccessControl.cs:54-76`)
  2. `SparkGroupMembership.GetAsync`
  3. `ClaimsGroupMembershipProvider` (`Services/ClaimsGroupMembershipProvider.cs:19-43`), which reads `group`/`groups`/role-URI/`groupsid` claims.

  The cookie has role claims baked in at sign-in (`AddRoles<SparkRole>`, `libs/authorization/.../Extensions/SparkAuthenticationExtensions.cs:64`). The access token deliberately carries none (`IdentityProvider/Services/OidcTokenGenerator.cs:139-164`).
- **`ISparkCurrentUser.Id` is null for the bearer.** `HttpContextSparkCurrentUser.Id` reads `ClaimTypes.NameIdentifier` (`libs/spark/MintPlayer.Spark/Services/HttpContextSparkCurrentUser.cs:25-27`). The resource server keeps `sub` verbatim (`MapInboundClaims=false`, `NameClaimType="sub"`, `ResourceServer/SparkResourceServerExtensions.cs:65-77`). `whoami` confirms `nameIdentifier:null`. Row security keyed on the current user id therefore fails closed for every bearer caller. `Identity.Name` is the id, not the user name.
- **With the S4 prototype provider (store groups for bearer callers), parity holds**: bearer `Read/SpikeDevelopersOnly:true` before removal (`s4_store_groups.log`, `s4_final.log`).

### Gap list for `ForMcp()` (IdP = `libs/identity_provider/MintPlayer.Spark.IdentityProvider/`)

| # | Item | State | Evidence |
|---|---|---|---|
| G1 | RFC 8414 `/.well-known/oauth-authorization-server` | **missing**: only OIDC discovery | `Endpoints/Discovery.cs:10-15` (path `/openid-configuration`), group `Endpoints/Oidc/Groups.cs:15` |
| G2 | RFC 9728 `/.well-known/oauth-protected-resource[/mcp]` + `WWW-Authenticate: Bearer resource_metadata=…` | **missing** everywhere (no match for `oauth-protected-resource` in libs or apps) | grep |
| G3 | RFC 9207 `iss` on the authorization response | **present, verified on the wire** (`&iss=https%3A%2F%2Flocalhost%3A5011`) | `Services/OidcAuthorizationResponse.cs:23`, `Endpoints/Discovery.cs:91` |
| G4 | RFC 8707 resource → audience | **partial, works when the resource is pre-seeded**; see the details below this table | `Services/OidcAuthorizeParameters.cs:65`, `Services/OidcAuthorizeHandler.cs:294`, `Services/OidcTokenGenerator.cs:167-211`, `Endpoints/TokenGrants.cs:154`, `Services/OidcScopeCatalog.cs:22-25` |
| G5 | Client ID Metadata Documents | **missing** | client lookup is an exact `ClientId` query (`Services/OidcAuthorizationFlow.cs:235-244`); generated ids are random 22-character strings (`Services/OidcClientIdReservation.cs:25`) |
| G6 | Anonymous loopback DCR | **missing** | `/connect/register` returns 401 without an initial access token from an approved developer (`Endpoints/Register.cs:37-42`). New clients start in `Development` mode, members only (`Register.cs:48`). Redirect URIs match exactly, port included (`Services/OidcAuthorizeHandler.cs:73`, `Endpoints/Par.cs:70`), so there is no RFC 8252 any-port loopback. Loopback http is accepted only at save time (`Interceptors/OidcApplicationInterceptors.cs:246-264`). |
| G7 | PKCE S256 + public clients | **present, verified** | S256 only (`OidcAuthorizeHandler.cs:95-98`); a public client with no secret authenticates as `none` (`Services/OidcClientAuthenticator.cs:155`) |
| G8 | Refresh tokens for agents | **present but gated** | only with `offline_access` plus a `refresh_token` grant type (`Endpoints/Token.cs:259-260, 374-375`). Rotation with reuse detection exists (`Token.cs:394-430, 529-594`). ForMcp must either add `offline_access` implicitly or document it (relevant to S3: does each client ask for it?). |
| G9 | Groups and user id for bearer callers | **missing** (S2 parity, S4) | `ClaimsGroupMembershipProvider.cs:19-43`, `HttpContextSparkCurrentUser.cs:25-27`, `OidcTokenGenerator.cs:139-164` |
| G10 | `sub` must be the local id | pairwise subjects exist (`OidcSubjects.For`, `OidcTokenGenerator.cs:141`) | `ForMcp()` clients must be forced to public `sub`, or D12's natural ids and S4's store lookup break |
| G11 | `mcp:read` / `mcp:write` scope names | **not expressible**: scope ownership is "text before the first dot" (`OidcScopeCatalog.cs:24-25`), so a scope with a colon belongs to no resource | needs an explicit scope → resource link on `OidcApiScope` |

G4 details (RFC 8707 resource → audience):
- `aud` is the `Name` of an API `OidcResource`. The resource name had to be the URL `https://localhost:5011/mcp` and the scope `https://localhost:5011/mcp.read`.
- That works only because `localhost:5011` contains no dot. Scope ownership is the text before the first dot, so `seotester.mintplayer.com/mcp.read` would be owned by "https://seotester".
- An unknown `resource` does **not** return `invalid_target`. The audience silently falls back to `client_id` (`OidcTokenGenerator.cs:210`).
- `/connect/token` reads `resource` only for token exchange.

### Consequences of S2

- **D4 (setup 3) is feasible.** The IdP runs beside the app's own cookie sign-in, the flow completes with a public PKCE client, and the token is `at+jwt` with `aud` = the canonical URI. G1, G2, G4, G5, G6, G8, G10 and G11 are `ForMcp()` work items.
- **D9 / D13 (parity) need a new prerequisite:** a store-backed group provider and a `sub` → `NameIdentifier` mapping for agent principals (see S4). Without them every bearer caller is "signed-in, no groups, no id".

---

## S4 — do group changes reach a live token?

**Question.** After sign-in, remove the user from a group that grants a read right, then call again with the same token. Is the next call denied without a new sign-in?

**Result:**
- **As-is: FAIL.** The scenario cannot even start: the bearer never had the right, because the token carries no groups.
- **With the per-request store provider prototype: PASS.**

### Runs (`s4.sh`; the same token and cookie throughout, no new sign-in)

As-is, claims only (`s4_claims_only.log`):
```
BEFORE  cookie: SpikeDevelopersOnly=true   bearer /mcp: SpikeDevelopersOnly=false
role remove: {"succeeded":true,"roles":[]}
AFTER   cookie: SpikeDevelopersOnly=true   bearer /mcp: SpikeDevelopersOnly=false
```

With `Spike:StoreGroups=true` (`SpikeStoreGroupProvider` loads `SparkUser.Roles` by `sub` per request, for bearer principals only), final config stateless + cookie guard (`s4_final.log`):
```
BEFORE (11:20:44)  cookie: true   bearer /spike: true   bearer /mcp: true
role remove: {"succeeded":true,"roles":[],"stampHash":"4AE199C3"}
AFTER  (11:20:45)  cookie: true   bearer /spike: false  bearer /mcp: false
```

**PASS for the bearer:** the next call is denied, with no new sign-in and no cache delay.

**The cookie stays stale.** `UserManager.RemoveFromRoleAsync` does **not** change the security stamp (the stamp hash is unchanged), and the cookie's role claims are re-read only when Identity's `SecurityStampValidator` re-validates. That validator is registered by `AddIdentityApiEndpoints` (`SparkAuthenticationExtensions.cs:60`) and no `ValidationInterval` is configured anywhere, so the ASP.NET default of **30 min** applies.

### Where groups are resolved today, and cache lifetime

- Path: `SecurityFileAccessControl.ResolveDecisionAsync` (`SecurityFileAccessControl.cs:54-76`) → `SparkGroupMembership.GetAsync` (primary + composed `IGroupMembershipProvider`s, registered by `AddGroupMembershipProvider`, `libs/spark/MintPlayer.Spark/Extensions/SparkBuilderGroupMembershipExtensions.cs:61`) → `ClaimsGroupMembershipProvider.cs:19-43`.
- Group names are matched case-insensitively to security.json names. The store keeps `DEVELOPERS` (normalized by UserManager), and it still matched.
- Membership is stored as `SparkUser.Roles` (`libs/authorization/MintPlayer.Spark.Authorization.Abstractions/Identity/SparkUser.cs:66`), changed only through `UserStore.AddToRoleAsync`/`RemoveFromRoleAsync` (`Identity/UserStore.cs:311-323`). No endpoint in libs or apps changes membership.
- Caches:
  - per request only: group ids keyed by principal (`SecurityFileAccessControl.cs:127-147`), and decisions per scope in `PermissionService`;
  - no `IMemoryCache`;
  - the cookie's claims live until the 30-minute stamp re-validation or a new sign-in;
  - the bearer has no group cache at all. The prototype costs one `LoadAsync<SparkUser>` per request, which the session caches within the request.
- Composition is a **union**. A store provider can add groups, but it cannot remove a stale cookie claim, so the prototype acts only for bearer principals.

### Consequences of S4

- PRD §4.6 "groups are resolved per request from the user store" is **not true today**; it must be **built**. The prototype's 20 lines show the shape: an `IGroupMembershipProvider` in the Mcp or Authorization package, keyed on the token `sub`, active for agent principals.
- D22 (external server) needs the same provider after the linked-login mapping.
- Web and agent can disagree for up to 30 minutes after a membership change, with the **agent stricter**. Parity tests (D9) must seed the cookie *after* the membership is set. Optionally, `RemoveFromRoleAsync` callers should bump the security stamp.

---

## Consequences for PRD decisions

| Decision | Change? | What the spikes say |
|---|---|---|
| **D2** SDK 2.2.x, stateless, "hybrid mode for 2025-11-25 clients" | **Reword** | 2.2.0 works on net11. **Plain `Stateless` already serves 2025-11-25 clients** (initialize without a session) and 2026-07-28 clients. "Hybrid" in SDK terms (`StatefulForInitializeClients`) gives old clients real sessions and needs **session affinity**. Pick `Stateless` (the default), and use hybrid only if a session-only feature is needed. Pin `ModelContextProtocol.AspNetCore` **2.2.0**. |
| **D3** bearer only, cookies ignored | **Keep, specify the mechanism** | Without extra work, a cookie authenticates `/mcp` and **wins over a bearer** (composite order). A bearer-only endpoint policy fixes it (verified). `AddMcp` must also reset `ISparkAuthenticatedSchemeFeature`, and SPARK020 must not flag it. Origin validation is ours to write. |
| **D4** three setups | **Keep** | Setup 3 is proven end to end (code + PKCE S256, public client, `aud` = canonical URI, `iss`). Add the prerequisite "agent principal → local user (NameIdentifier + store groups)" for setups 1–3, and gap items G1, G2, G4, G8, G10 and G11 to `ForMcp()`. |
| **D5** CIMD + anonymous loopback DCR | **Keep; confirmed as new work** | Neither exists. DCR needs an initial access token and starts clients in Development mode; redirect matching is exact (no any-port loopback). CIMD needs a new lookup path in `OidcAuthorizationFlow`. Also decide on `offline_access` (G8). |
| **D17** anonymous and signed-in follow security.json exactly | **Keep, but it depends on new work** | A cookie-only `/mcp` call is anonymous (verified), and `Read/SpikeSignedIn` is false there, which is the expected anonymous answer. But a **signed-in bearer caller today gets only the `authenticated` group**: no named groups, null user id. D17 holds only after the store-group provider and id mapping exist. |

## Housekeeping

- Every host started was stopped with TaskStop plus `Stop-Process SparkId`, and the process count was verified at 0.
- The Claude Code `spike` entry was removed (`(no spike entry)`).
- The local database `SparkIdMcpSpike` (localhost:8080) was left in place. It holds only the spike user, client and resource and can be deleted.
- Not done: Origin probing, MCP Inspector, the 401 / `WWW-Authenticate` challenge (needs G2), and other clients (S3).

---

## S3 / S11 (source-read, live confirmation pending)

> **Source-read only.** Nothing in this section was run against a server. It comes from reading client
> source and documentation on 2026-10-12. The owner will run the live sign-ins with Codex ≥ 0.148 and
> Gemini CLI 0.63 (plan M7, manual checklist); until then every row is **[inferred]**, not verified.

**Versions read:** Claude Code 2.1.296, Codex 0.162.1 (CIMD since 0.148.0), Gemini CLI 0.63.0.
**Sources:** openai/codex `a9b0815` + rmcp `4048ac40`; google-gemini/gemini-cli `9b6e026`; Claude Code
documentation, strings in the 2.1.296 binary, issues #90370 and #42765.

| | Claude Code | Codex | Gemini CLI |
|---|---|---|---|
| CIMD | yes: `https://claude.ai/oauth/claude-code-client-metadata`; the document lists portless loopback redirects | yes, only if the AS metadata has `client_id_metadata_document_supported` and the `none` auth method | no (#25724, not planned) |
| DCR | yes | yes (fallback) | yes, public client |
| Static bearer | `--header` / headersHelper | `--bearer-token-env-var` / `http_headers` | `headers` / `-H` |
| Redirect URI | `http://localhost:<port>/callback` | `http://127.0.0.1:<port>/callback/<id>` (`/callback` if the AS advertises the `iss` parameter) | `http://localhost:<port>/oauth/callback` |
| `resource` parameter | yes (from the PRM) | always | yes |
| Scope | pinned or minimal, + `offline_access`; step-up prompt on 403 | discovered `scopes_supported`; no step-up | all of the AS's `scopes_supported`; no step-up |
| 401 mid-session, no token | marks the server "needs auth" and adds an authenticate stub tool (unverified for initialize-200-then-401) | error "Authentication required", no sign-in | error naming `/mcp auth <name>` |
| Voluntary sign-in | `claude mcp login <name>` (menu: unverified) | `codex mcp login <name>`; `mcp add --url` signs in automatically when a PRM exists | `/mcp auth <name>` |

### S3 consequences

- **D5:** loopback redirect matching accepts `localhost` and `127.0.0.1`, any port (RFC 8252 §7.3; the
  port is ignored for CIMD documents listing portless loopback URIs too), any path.
- The AS metadata must advertise `client_id_metadata_document_supported: true`,
  `token_endpoint_auth_methods_supported` including `none`, and
  `authorization_response_iss_parameter_supported: true`.
- Gemini requests every `scopes_supported` entry, so it always asks for `mcp:write`. That is harmless
  only because the grant (the user's toggle) decides, not the scope (D6). Keep it that way.
- Claude Code appends `offline_access` when the AS advertises it; advertise it so it gets a refresh token
  (S2 G8).

### S11: FAIL by source reading

No client opens a sign-in on a mid-session 401 when it holds no token. Two measures:

1. **Error text that tells the agent how to start the client's own sign-in** (owner idea, 2026-10-12;
   whether the model actually follows it is to be tested live):
   "Sign-in required. Claude Code: call the authenticate tool or run `claude mcp login <name>`; Codex: run
   `codex mcp login <name>`, then reconnect; Gemini: ask the user to run `/mcp auth <name>`."
   - It works only through the client's own OAuth flow: the token must land in the client's store. A
     browser opened on the site's login page gives a cookie, which `/mcp` ignores (D3).
   - URL-mode elicitation is not a way out: the spec does not allow it for authorizing the client to the
     server itself.
2. **Per-app `RequireSignIn` switch** (default off, so anonymous use keeps working): every
   unauthenticated `/mcp` request, `initialize` included, answers 401 with the challenge, which all three
   clients handle at connect time.

Step-up on 403 (`insufficient_scope`) works only in Claude Code.
