# PRD — MintPlayer.Spark.Mcp: an MCP server inside a Spark app, acting as the user

**Status:** built (M1–M7a, PR #509); live checks in progress — Claude Code via setup 2 passed, the other clients
pending; fixes found during the live checks listed in the plan's M7. No open design questions.
Spikes S1, S2 and S4 done live; S3 and S11 answered by source reading, live sign-ins pending (round 4,
[spark_mcp_spikes.md](spark_mcp_spikes.md)). Implementation started (M1).
**Date:** 2026-10-12
**Origin:** owner request (2026-10-12). App developers host a Spark website and let its users connect AI
agents (Claude Code, Codex, Gemini) to their data. SeoTester: agents fix the SEO findings in the site's
repository. CodeCoverage: agents ask where coverage is cheapest to raise. A user who asks an agent "give me
a list of all contracts" must sign in first and then see only the contracts they are allowed to see.
**Plan:** [spark_mcp_plan.md](spark_mcp_plan.md)
**Docs (drafts, written against this design):** [guide-mcp.md](guide-mcp.md) (developers),
[guide-mcp-agents.md](guide-mcp-agents.md) (end users), [seotester/mcp.md](seotester/mcp.md),
[code-coverage/mcp.md](code-coverage/mcp.md)
**Packages:** new `MintPlayer.Spark.Mcp` (NuGet `11.0.0-preview.*`, major locked to .NET); changes to
`MintPlayer.Spark`, `MintPlayer.Spark.Authorization`, `MintPlayer.Spark.IdentityProvider`,
`@mintplayer/ng-spark` (minor bump only).

Evidence tags: **[verified]** read in this repo (file:line) · **[cited]** external source (URL) ·
**[inferred → Sn]** a belief spike Sn must confirm or refute · **owner** decided in the grill (§10).
Where §0 and the body disagree, §0 wins and the body is fixed.

---

## 0. Decisions

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | One new package, `MintPlayer.Spark.Mcp` (`libs/mcp/`), registered with `spark.AddMcp(...)` and mapped through `Registry.AddEndpoints`. | The `ISparkBuilder` + `Registry.AddEndpoints` pattern [verified `SparkModuleRegistry.cs:141`, `SparkDashboardsExtensions.cs:41-72`]. |
| D2 | Built on the official C# SDK `ModelContextProtocol.AspNetCore`, **pinned to 2.2.0**, plain **`Stateless`** HTTP mode, which serves 2025-11-25 clients too (they `initialize` without a session). **No hybrid mode, no session affinity.** ~~"2.2.x, stateless HTTP, hybrid mode for 2025-11-25 clients"~~ superseded in round 4 (S1). | 2.2.0 stable 2026-08-13, spec 2026-07-28, targets net10 [cited nuget.org/packages/ModelContextProtocol.AspNetCore]. **S1 [verified live]:** 2.2.0 runs on net11; in `Stateless` a 2025-11-25 client initializes, gets no `Mcp-Session-Id`, 405 on GET, and calls tools; 2026-07-28 uses `server/discover`. The SDK's hybrid (`StatefulForInitializeClients`) gives old clients real sessions and needs session affinity [spark_mcp_spikes.md S1a]. |
| D3 | `/mcp` accepts bearer tokens only. Cookies are ignored on it; `Origin` is validated. A call **without** a token runs as `anonymous` (D17). **Mechanism (round 4, S1):** `AddMcp` builds the bearer-only endpoint authorization policy itself (only the bearer scheme(s), lets every caller through, so `HttpContext.User` becomes the bearer principal or an empty one; cookies ignored). After the policy swaps the user, `AddMcp` **resets the composite's `ISparkAuthenticatedSchemeFeature`**, which otherwise still names the cookie scheme. It must not trip analyzer **SPARK020**: fix its false positive on named policies (M1). The **Origin check is our own code**, not the SDK's. | Spec: token only in `Authorization`, audience = canonical URI, no passthrough, Origin MUST [cited modelcontextprotocol.io/specification/2026-07-28/basic/authorization]. No ambient credential → no CSRF surface; bearer requests are antiforgery-exempt [verified `SparkAntiforgeryMiddleware.cs:56-70,155-164`]. **S1 [verified live]:** without the policy a cookie-only `/mcp` call runs as the signed-in user, and with bearer + cookie **the cookie wins** (composite order); with the policy, cookie-only is anonymous and bearer + cookie is the bearer. The stale feature: `SparkCompositeAuthenticationHandler.cs:63-69`. SPARK020 fired although the app-registered policy ran on every call. The SDK's Origin handling was not probed, so we do not rely on it [spark_mcp_spikes.md S1b, S1 findings]. |
| D4 | **Three setups, flags `EAgentAuthentication { PersonalAccessToken, ExternalAuthorizationServer, EmbeddedAuthorizationServer }`, default all.** Effective = flags ∩ what is wired up; an explicitly set flag without its wiring fails at startup. See §4.2. **owner** (R1-Q16). **Prerequisite for every setup (round 4, S2/S4):** agent bearer callers get their groups **from the user store, per request** (a store-backed `IGroupMembershipProvider`, active for bearer principals only) and `ISparkCurrentUser.Id` maps the bearer's `sub` to the local user id. Without it a signed-in bearer caller has only the authenticated group and a null user id. See §4.6. | Only an OAuth server that speaks MCP's OAuth (PKCE, `resource`, CIMD/DCR) can give an agent browser sign-in; GitHub/Google/personal Microsoft can't, so CodeCoverage and SeoTester need the embedded one. The resource server and external-login links are already in the Authorization package [verified `libs/authorization/.../ResourceServer/SparkResourceServerExtensions.cs`, `Endpoints/ExternalLogin/ExternalLoginCallback.cs`]. **S2 [verified live]:** setup 3 works end to end beside the app's own cookie sign-in (code + PKCE S256, public client, `at+jwt`, `aud` = canonical URI, RFC 9207 `iss`, `sub` = local id), but the bearer got no named group: groups come only from claims (`ClaimsGroupMembershipProvider.cs:19-43`), tokens carry none by design (`OidcTokenGenerator.cs:139-164`), and `HttpContextSparkCurrentUser.cs:25-27` reads `NameIdentifier`, absent on the bearer (`MapInboundClaims=false`). **S4 [verified live]:** a per-request store provider prototype restores parity and denies the next call after a role removal [spark_mcp_spikes.md S2, S4]. |
| D5 | Client registration (embedded setup): **Client ID Metadata Documents from any HTTPS host** with SSRF guards (public addresses only, size cap, timeout, cache), **plus anonymous DCR limited to loopback redirect URIs**, rate-limited per IP, pruned after 30 days unused. The consent screen shows client name and redirect host; DCR clients are labelled **unverified**. **owner** (R1-Q8). **Round 4 (S2, S3):** loopback redirect matching accepts both `localhost` and `127.0.0.1`, **any port** (RFC 8252 §7.3) and **any path**, for anonymous DCR and for CIMD documents that list portless loopback URIs. The AS metadata advertises `client_id_metadata_document_supported: true`, `token_endpoint_auth_methods_supported` including `none`, and `authorization_response_iss_parameter_supported: true`. The IdP gaps S2 found are M3b work items: RFC 8414 metadata; RFC 9728 PRM + `WWW-Authenticate: Bearer resource_metadata=…`; CIMD; anonymous loopback DCR (today needs an initial access token); any-port loopback matching; RFC 8707 `resource` → `aud` for `/mcp` with **no fallback to `client_id`** for the MCP resource (today an unknown `resource` silently yields `aud` = `client_id`); scopes `mcp:read`/`mcp:write` expressible (today a scope's resource is the text before its first dot); refresh tokens issued to MCP clients (today only with `offline_access`: **advertise `offline_access`**, Claude Code appends it when advertised); pairwise `sub` off for MCP clients. | Spec 2026-07-28: CIMD SHOULD, DCR deprecated but kept [cited …/authorization/client-registration]. Claude Code and Codex do both, Gemini CLI only DCR [cited code.claude.com/docs/en/mcp, learn.chatgpt.com/docs/extend/mcp.md, geminicli.com/docs/tools/mcp-server]. Today's DCR needs an initial access token [verified `Endpoints/Register.cs:13-41`]. **S2 gap list G1–G11 [verified, file:line]** and **S3 redirect URIs per client [inferred, source-read]**: Claude Code `http://localhost:<port>/callback`, Codex `http://127.0.0.1:<port>/callback/<id>`, Gemini `http://localhost:<port>/oauth/callback`; Codex uses CIMD only when the metadata advertises it with `none` [spark_mcp_spikes.md S2 gap list, S3]. |
| D6 | **One `[Flags] EAgentPermission { None, Read, Write }` at every layer.** Effective = **app(agent) ∩ type ∩ grant ∩ security.json rights**. App: `mcp.AgentPermissions` is a `Func<AgentInformation, EAgentPermission>` (a plain value converts implicitly), evaluated per call, synchronous. Type: model JSON `"agentPermissions"` (default `None` = not exposed; **replaces `exposeToAgents`**). Grant: chosen by the user per type. `Write` without `Read` on a type fails at startup. **The grant, not the token's scope, decides**: a token's `mcp:read`/`mcp:write` scope is never a cap, so a grant edited upward applies without a new sign-in (R1-Q2). Scopes exist only so a missing `Write` can answer 403 `insufficient_scope`, whose re-consent updates the grant. **owner** (R1-Q11, Q12) | Same enum convention as `EShowedOn` [verified `libs/model/MintPlayer.Spark.Model/EShowedOn.cs:9`]. |
| D7 | `AgentInformation` carries only what can be trusted, named accordingly: `Kind` (`EAgentKind`: `Anonymous`, `PersonalAccessToken`, `ExternalAuthorizationServer`, `MetadataDocument`, `DynamicRegistration`, `PreRegistered`), `ClientId`, `VerifiedHost` (CIMD only), `UnverifiedName`, `User`. **owner** (R1-Q12) | Client names are self-declared; a CIMD client id's host was fetched over HTTPS. |
| D8 | Generic tools `query`, `get`, `discover`, `describe`, `propose_changes`, `apply_changes`. Writes are **propose → apply** with plans in an **`McpPlans` collection**, native document `@expires` 10 min, one-shot via optimistic concurrency. `apply_changes(planId, changes)` **must echo the readable diff** so the host's approval prompt shows what will run; the server checks it matches. Applied plans lose their 10-min expiry and are kept `Spark:Mcp:PlanRetention` (default 1 year) as the write audit. **Proposing requires a signed-in caller**: an anonymous propose would let anyone write plan documents. **owner** (R1-Q6; anonymous rule from consistency review R2) | The approval prompt shows call arguments; an opaque id alone would hide the change. |
| D9 | **Authorization parity is a hard requirement.** Every check that today lives only in an HTTP endpoint moves into an internal service both call; `InternalsVisibleTo("MintPlayer.Spark.Mcp")`. Parity tests prove identical rows and attributes. | Endpoint-only today: type/query list filtering [verified `Endpoints/EntityTypes/List.cs:30,56`, `Queries/List.cs:28`, `Queries/Get.cs:54`], save validation + etag [verified `Endpoints/PersistentObject/Update.cs:52,81-93`], custom-action gates [verified `Endpoints/Actions/ExecuteCustomAction.cs:84,91,314`]; `ICustomActionResolver` bypasses all of it [verified `CustomActionResolver.cs:7-29`]. |
| D10 | Results are serialized with **Spark's HTTP `JsonOptions`** (the redaction net), **fenced as untrusted data** in a random per-call tag, paged, capped at about 10k tokens. | Redaction net lives in HTTP `JsonOptions` [verified `SparkMiddleware.cs:102-104`]. Crawled content is an injection channel → S6. Claude Code warns at 10k output tokens [cited code.claude.com/docs/en/mcp]. |
| D11 | **Personal access tokens** live in the **Authorization package** (all setups): created on the account page with the same per-type choice and read/changes choice, prefix `spat_`, shown once, stored hashed, **mandatory expiry, default 90 days, max 1 year**, revoked on security-stamp change. **owner** (R1-Q4, Q16) | All three clients accept a static bearer header [cited D5 sources]. Precedent: CodeCoverage's hashed `covt_` tokens [verified `CodeCoverage.Library/Entities/ApiToken.cs`]. |
| D12 | **`AgentGrant` collection in the Authorization package**, one per user and agent, **natural ids**: `AgentGrants/{userId}/pat/{tokenId}`, `AgentGrants/{userId}/ext/{sha256(issuer + "\|" + clientId)[..32]}`, `AgentGrants/{userId}/app/{clientId}`. Holds per-type permissions ("all exposed" or a chosen list), read/changes, `LastUsedAt`, and for tokens the hash and expiry. **It is the source of truth for access**; in the embedded setup `OidcGrant` keeps OAuth bookkeeping and revoking either revokes the other. Read per call, cached a few seconds, invalidated on edit. **owner** (R1-Q2, Q5, Q17) | A grant edited on the server takes effect on the next call without a new sign-in, in both directions. `OidcGrant` exists only with the IdentityProvider [verified `libs/identity_provider/.../Models/OidcGrant.cs:8-46`]. |
| D13 | **Per-type grant restrictions are enforced inside `IPermissionService`** for agent requests, so every tool, generic or app-written, honours them. **owner** (R1-Q2). *M3a:* applied by Mcp's agent restriction from the grant the token package describes (§10, M3a). | Query rights are already `Query/{type}` [verified `QueryReadPath.cs:97-101`]. |
| D14 | A **Connected applications** section in the Authorization package's account area (all setups) lists each grant: agent, kind, verified/unverified, allowed types, read/changes, created, last used, token expiry; edit, revoke, create token. Linked from `/account` through `SPARK_ACCOUNT_OVERVIEW_LINKS`. The IdentityProvider references the Authorization package, not the reverse, so the section has an **extension point** through which the IdentityProvider contributes its non-agent OAuth grants (sign-in at other websites); its server-rendered `/connect/applications` stays. **owner** (R1-Q4, Q5) | Account page link mechanism [verified `ng-spark/auth/models/src/auth-route-config.ts:56-71`]; the IdP's existing SPA page [verified `Endpoints/Portal/ConnectedApplicationEndpoints.cs:37,82`] becomes such a contribution. |
| D15 | Audit: one structured log event per call (user, client id, grant id, tool, arguments **hash**, row count, outcome, duration) + `AgentGrant.LastUsedAt` written at most once a minute. Writes are audited by the kept plans (D8). **owner** (R1-Q7) | Arguments are hashed because search terms can be personal data. |
| D16 | Rate limits per user for signed-in calls, per IP for anonymous calls, with the existing quota mechanism. | [verified `docs/guide-quotas.md`]. |
| D17 | **Anonymous and signed-in follow security.json exactly**, narrowed by `agentPermissions`. Denials copy the web's map: anonymous caller, target not permitted **or unknown** → uniform **401**, carrying the OAuth challenge when setup 2 or 3 is wired (the cue to sign in) and a plain 401 in setup 1; signed-in caller denied or unknown → uniform not-found. **Anonymous calls are read-only**: whatever `AgentPermissions` returns for `EAgentKind.Anonymous` is clamped to `Read`, and `propose_changes` is refused (D8). **owner** (R1-Q10). **Round 4:** "signed-in follows security.json" holds only once D4's store-backed groups and user-id mapping exist (S2/S4). **S11 failed** (source-read), so two measures: (a) the 401/403 tool error text tells the agent how to start **the client's own** sign-in, per client — Claude Code: call the authenticate tool or run `claude mcp login <name>`; Codex: run `codex mcp login <name>`, then reconnect; Gemini: ask the user to run `/mcp auth <name>` (owner idea 2026-10-12, live test pending). Sending the user to the site's login page does **not** help (it yields a cookie, ignored on `/mcp`, D3), and URL-mode elicitation is not for authorizing the client to the server. (b) A per-app **`RequireSignIn`** option (default **off**): when on, every unauthenticated `/mcp` request, `initialize` included, answers 401 with the challenge. Gemini requests every `scopes_supported` entry; harmless, because the grant decides (D6). Step-up on 403 works only in Claude Code. ~~"Mid-session 401 handling by clients → S11"~~ superseded by this. | Web map: `SparkDenial.Refuse` 401s anonymous before loading, so no existence oracle [verified `Endpoints/SparkDenial.cs:22-34,72-78`]; ng-spark redirects any 401 to sign-in [verified `ng-spark/auth/interceptors/src/spark-auth.interceptor.ts:14-28`]. **S1 [verified live]:** a cookie-only `/mcp` call is anonymous with the bearer-only policy. **S11 [inferred, source-read]:** no client opens sign-in on a mid-session 401 without a token (Claude Code marks the server "needs auth", Codex errors "Authentication required", Gemini errors naming `/mcp auth`); only Claude Code steps up on 403 [spark_mcp_spikes.md S3 / S11]. |
| D18 | **Web alignment in this PR:** query execute answers an anonymous caller **401** (uniform, existing or not) instead of 404, like persistent objects. Release-notes line. **owner** (R1-Q10). **Extended 2026-10-12 (owner):** `POST /spark/queries/get` and `GET /spark/types/{id}` answer the same way (anonymous on a host that can sign in → uniform 401, signed-in → 404); the lists `GET /spark/queries` and `/spark/types` stay filtered and answer 200. | Today query execute 404s everyone [verified `QueryReadPath.cs:78-85,193-199`], so an anonymous visitor on a protected query page is never sent to sign in. **Extension [verified, red→green]:** `MetadataAnonymousDenialTests` — the three anonymous-401 cases failed with 404 before, 9/9 green after (commit 7ff485c8, `SparkDenial.NotFoundOrSignIn`). |
| D19 | ~~**Web fix in this PR:** a reference whose **target type** the caller may not `Read` keeps its id and shows the redacted placeholder, for web and MCP alike.~~ *Superseded 2026-10-12 (owner):* a reference attribute (single or array; AsDetail/embedded rows are not references) is **shown only to a caller holding `Read` on its target type**. Without it the attribute is treated exactly like an attribute-level denial, for every verb: absent from `/spark/types`, query metadata and columns, detail, query and AsDetail rows, search, sort, filter and distincts, and a save drops a posted value and keeps the stored one. The placeholder stays only where a breadcrumb **template** of the owning type interpolates the reference. A **required** hidden reference fails the save with a clear error naming the attribute and the type to ask `Read` on (a startup check cannot tell: rights combine across a caller's groups). Red test first; audit every app's security.json for affected references; release-notes line. **owner** (R1-Q3, 2026-10-12) | `BreadcrumbResolver` consulted only row security and attribute rights, never `IPermissionService` [verified `BreadcrumbResolver.cs:98-104`, `RowSecurity.cs:254-258`, `EntityMapper.cs:359-380`]. The first fix (de7faaec) left the attribute and the target id on the wire (`EntityMapper.cs:361-379` kept `Value`, `QueryResultProjector.cs:219-225` sent Value/ObjectId). **Superseding fix [verified, red→green]:** `AttributeRightsEnforcement.GetHiddenReferencesAsync`, unioned into `GetDeniedAsync`; `ReferenceTargetRightsTests` 8/12 red before (ids on the wire, columns, metadata, sort/filter oracles, a posted reference overwriting the stored one, a bare "Owner is required."), 12/12 after (commit 911fd4b8). The model synchronizer plays no part: `ModelSynchronizer.cs:817-825` only records `DataType` Reference and `ReferenceType` and never reads security.json. **App audit:** QnA grants `Read/SparkUser` behind a `SparkUserActions` row rule (authors of a visible post), HR grants `Read/Profession` to visitors and signed-in users (public reference data); still hidden by design: CodeCoverage `ApiToken.CreatedByUserId` (no SparkUser row rule), Fleet `Car.Manager` for `Module:HR`/`Machine:FleetApi`, HR `Person.Company`/`Professions` for `Module:Fleet` — no machine caller sends or reads those references (replicas carry no `Manager`/`Company`/`Professions`). |
| D20 | **Endpoint `/mcp`**, configurable; `Spark:Mcp:CanonicalUrl` override (default `PublicOrigin` + path) for a subdomain behind a proxy. Startup fails if the path is already mapped. **owner** (R1-Q9) | Outside `/spark` the endpoint doesn't inherit Spark's default CORS and ambient-credential antiforgery [verified `docs/guide-cors.md:32,55-57`]. |
| D21 | **Tool prefix** `mcp.ToolPrefix`, defaulting to a slug of `ServerName` (`seotester_`, `codecoverage_`); the guide tells developers to set it explicitly so renaming `ServerName` doesn't rename tools users allow-listed, applied to generic and domain tools; domain tools are written unprefixed. **owner** (R1-Q13) | Two Spark apps in one session (ours) would collide on generic tool names. |
| D22 | **External authorization server (setup 2) maps a token to a user only through a linked external login**: the server is configured as one of the app's sign-in providers, the issuer is bound to that provider, the token's `sub` must equal the linked login's key. Unmapped → **401, never anonymous**. Groups and rights come from the local user, never from token role claims. A **shadow `AgentGrant`** is created on first use with "everything exposed", `Read` only. `Write` requires both the user turning it on under Connected applications **and** `mcp:write` in the token: in this setup only, the token scope is a real cap, because we can't step the user up through another server's consent (R3-Q18). Opaque tokens go through introspection. Full adversarial test list in §6.2. **owner** (R1-Q14, Q15: "if we manage to make it secure, add the appropriate tests"). **Amended 2026-10-12 (owner):** the shadow grant is bound to the user's security stamp like personal tokens and embedded grants; a stamp change counts as *disconnected* until the user allows the agent again, which re-stamps it. A Spark identity provider can be this external server for another app's `/mcp` (`idp.ForMcpResource(url)`); the demo wires HR on SparkId (decision log, "Demo apps"). | Matching on `email` is a classic account-takeover bug; issuer binding stops cross-issuer `sub` collisions. |
| D23 | Existing upload credentials are **not** MCP credentials: CodeCoverage `covt_` tokens and GitHub Actions OIDC tokens are rejected on `/mcp`. | Their principal is a token, not a user [verified `ApiTokens/ApiTokenAuthenticationHandler.cs:14-96`]. |
| D24 | First adopters **in the same PR**: **SeoTester** (app permissions `Read \| Write`, types `Read` only; the one write is `seotester_start_scan`) and **CodeCoverage** (`Read`), both on the embedded setup. **HR** demonstrates `Read, Write` on contracts with propose → apply. **owner** (2026-10-12 request; R1-Q11) | One PR rule. |

---

## 1. The problem

A Spark app knows things an AI coding agent needs, but the agent can't reach them as the user:

- **SeoTester** holds every finding of the last scan: rule, page, severity, how-to-fix text, snippets for
  nginx/Apache/IIS/HTML. Today the owner reads them on screen and retypes them into the agent.
- **CodeCoverage** holds per-file line coverage, patch coverage and coverage drops. An agent writing tests
  has to guess which files would raise the number most.
- **Any Spark app** (HR's contracts, Fleet's cars) has data a user may want to ask an agent about, guarded
  by security.json and row security.

All three agents speak MCP. The server must obey the same rights as the web UI, for the same user,
without the agent ever holding the user's password or cookie.

## 2. Goals and non-goals

**Goals**

1. A Spark app adds an MCP server with one package and a few lines in `Program.cs`.
2. Three setups (D4): personal tokens only; an external OAuth server; the app as its own OAuth server.
3. Rights, row security and attribute redaction apply unchanged, for anonymous and signed-in callers.
4. Developers choose what agents may see and do per app, per agent and per type; users narrow it further
   per agent.
5. Generic read tools, generic writes through propose → apply, and app tools written as plain C# classes.
6. Works with Claude Code, Codex CLI and Gemini CLI without hand-registering a client.
7. SeoTester and CodeCoverage ship useful tools at go-live.
8. Documentation for developers and end users.

**Non-goals**

- An in-app chat assistant or server-side LLM calls.
- A stdio transport. Without an `HttpContext` row security fails closed [verified
  `HttpContextSparkCurrentUser.cs:19-21`].
- Semantic or vector search over records.

## 3. Options considered

### 3.1 Who issues the tokens

Resolved by D4: all three, chosen by flags. The rejected alternative was a central SparkId: its `sub` and
groups are SparkId's, not the app's, and production apps would depend on it. **owner** (R1-Q1: "SparkId
has nothing to do with it").

OpenIddict 7.7.2 has neither DCR nor CIMD [cited github.com/openiddict/openiddict-core/issues/2404];
Duende has DCR but is commercial [cited docs.duendesoftware.com/identityserver/quickstarts/8-mcp/]. Our
own IdentityProvider is extended instead.

### 3.2 How tools reach data

Rejected: tools calling the public services alone (endpoint-only checks would be skipped, D9); tools
calling the HTTP endpoints over loopback (token passthrough, double serialization). Chosen: shared internal
services (D9).

## 4. Design

### 4.1 Request flow (embedded setup)

```mermaid
sequenceDiagram
    participant U as User
    participant A as Agent
    participant M as App /mcp
    participant S as App /connect/* (embedded server)
    A->>M: tools/call seotester_list_findings (no token)
    M-->>A: 401 WWW-Authenticate: Bearer resource_metadata=".../.well-known/oauth-protected-resource/mcp"
    A->>S: discover, register (CIMD or loopback DCR)
    A->>U: opens /connect/authorize?...&resource=https://app/mcp&code_challenge=...
    U->>S: signs in with the app's own sign-in; picks types and read/changes
    S-->>A: code → token (aud = https://app/mcp, sub = local user, grant = AgentGrants/...)
    A->>M: tools/call ..., Authorization: Bearer ...
    M->>M: app(agent) ∩ type ∩ grant ∩ rights; same read path as the web; redaction
    M-->>A: rows this user may see, fenced as data
```

Calls that anonymous may make succeed without any of this (D17).

### 4.2 The three setups

| Setup | Packages | Token | Browser sign-in | Metadata published |
|---|---|---|---|---|
| 1. Personal access token | Authorization + Mcp | `spat_…` from the account page, pasted as a bearer header | no | none: a 401 carries no OAuth hint |
| 2. External authorization server | Authorization + Mcp | issued by Entra/Keycloak/Auth0, or another Spark app's identity provider (`idp.ForMcpResource(url)`), for audience = canonical URI | at the external server | RFC 9728, pointing to it |
| 3. Embedded authorization server | Authorization + IdentityProvider + Mcp | issued by the app | at the app, any sign-in method | RFC 9728 + RFC 8414 |

If setups 2 and 3 are both wired, the metadata lists the embedded server first; tokens of both are valid.

### 4.3 Package surface

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>(...);            // the app's existing sign-in
    spark.AddIdentityProvider(idp => idp.ForMcp().AgentsOnly()); // setup 3 only; the provider serves agents only
    spark.AddMcp(mcp =>
    {
        mcp.ServerName = "SeoTester";                    // ToolPrefix defaults to "seotester_"
        mcp.Instructions = "SEO findings of the sites you own...";
        mcp.Authentication = EAgentAuthentication.All;   // default
        mcp.AgentPermissions = agent => agent.Kind == EAgentKind.Anonymous
            ? EAgentPermission.Read
            : EAgentPermission.Read | EAgentPermission.Write;
        mcp.AddTools<SeoTools>();
    });
});
```

- `ForMcp()` turns on code+PKCE, refresh rotation with family revocation, revocation, RFC 8414, RFC 9207
  `iss`, CIMD, loopback DCR, the `mcp:read`/`mcp:write` scopes and an API resource whose audience is the
  canonical URI. Device, client-credentials and token-exchange stay off. Round 4 adds the D5 list: AS
  metadata flags, any-port/any-path loopback matching for `localhost` and `127.0.0.1`, `offline_access`
  advertised and refresh tokens for MCP clients, no `client_id` audience fallback, public (not pairwise)
  `sub`.
- `AddMcp` registers the SDK (`AddMcpServer().WithHttpTransport()`, `Stateless`), a `Spark:Mcp` bearer
  scheme per wired setup, the bearer-only endpoint policy and the authenticated-scheme feature reset (D3),
  the RFC 9728 document and challenge, the Origin check, the `RequireSignIn` option (D17), and maps the
  endpoint.
- The canonical URI never comes from the request's `Host` header.

### 4.4 Generic tools (names shown without prefix)

| Tool | Needs | Does |
|---|---|---|
| `discover(search?, skip, take)` | Read | Exposed types and queries the caller may query, with descriptions. |
| `describe(type)` | Read | Readable attributes: names, labels, descriptions, data types, references. Schema only. |
| `query(query, search?, filters?, sort?, skip, take)` | Read | Runs a query through the shared query read path. Compact table + object ids. |
| `get(type, id, include?)` | Read | One object as presented: readable attributes, available actions, detail rows on request. |
| `propose_changes(summary, operations[])` | Write, signed in (M4: was "Read") | Validates create/update/delete/custom-action operations against rights, rules and etags; stores a plan; returns `planId` + readable `changes`. Changes nothing. At most 50 operations. |
| `apply_changes(planId, changes)` | Write, signed in | Checks that `changes` equals the plan's rendering, re-authorizes, re-validates and runs. One-shot; user-bound; expires after 10 min; a stale etag fails the whole plan. |

### 4.5 App-specific tools

- **Checked services only**: `IDatabaseAccess` (not `...UncheckedAsync`), `IQueryExecutor`,
  `IPermissionService`, or the shared read paths. Analyzer SPARK05x warns on a raw session or an unchecked
  call in a tool class.
- `ReadOnly = true` on `[McpServerTool]` means the tool needs `Read`; anything else needs `Write`. Write
  tools are not listed when the effective app permission lacks `Write`.
- `SparkMcpResult` serializes with Spark's options, pages, and fences untrusted text.

### 4.6 Identity, groups and grants

- Embedded and personal-token setups: `sub` is the local user id. External setup: D22's mapping.
- Groups are resolved **per request from the user store**, never from token claims, so a removed
  membership applies on the next call. ~~[inferred → S4]~~ **Round 4: this does not exist today and must
  be built** [verified live, spark_mcp_spikes.md S2, S4]:
  - Tokens carry no groups (`OidcTokenGenerator.cs:139-164`), and today's only provider reads claims
    (`ClaimsGroupMembershipProvider.cs:19-43`), so a bearer caller gets only the authenticated group.
  - New: a store-backed `IGroupMembershipProvider` that loads `SparkUser.Roles` by the token's `sub` per
    request, **for bearer principals only** (composition is a union, so it can't remove a stale cookie
    claim). It lives in the Authorization package, which owns `SparkUser`; core already has the seam
    (`AddGroupMembershipProvider`). The S4 prototype cost one `LoadAsync<SparkUser>` per request and
    denied the next call after a role removal, with no new sign-in.
  - New: `ISparkCurrentUser.Id` maps a bearer principal's `sub` to the local user id (today it reads
    `NameIdentifier`, which the resource server doesn't set: `HttpContextSparkCurrentUser.cs:25-27`).
    Without it row security keyed on the user id fails closed for every agent.
  - Setup 2 (D22) uses the same provider after the linked-login mapping.
  - *Added 2026-10-12:* every setup's principal also carries the role and group claims a cookie sign-in of the user
    carries (Identity's claims factory, made per request), so application code that tests `User.IsInRole` treats an
    agent as the user (decision log, "Agent principals carry the cookie's role and group claims").
- **The cookie path is unchanged (existing behaviour, recorded, not changed by this PR):** cookie role
  claims are baked in at sign-in, `RemoveFromRoleAsync` doesn't change the security stamp, and the claims
  refresh at Identity's default 30-minute `SecurityStampValidator` interval. After a membership change the
  web can be more permissive than the agent for up to 30 minutes; the agent is the stricter side. Parity
  tests seed the cookie after setting memberships.
- The grant (D12) is read per call and applied inside `IPermissionService` (D13).
- Consent screen (setup 3) and token creation (setup 1) offer: "everything this app makes available to
  agents" (preselected, includes types exposed later) or a chosen list of types with their queries shown
  underneath; and read vs read + changes, limited by the app's `AgentPermissions` for that agent.

### 4.7 SeoTester adoption

| Tool | Returns |
|---|---|
| `seotester_list_sites` | Sites of the user's organizations: origin, verified state, latest score and grade, last run. |
| `seotester_run_summary(site, run?)` | Category scores and every check: rule, severity, status, affected count, delta New/Fixed/Unchanged. |
| `seotester_list_findings(site, run?, minSeverity?, category?, checkId?, urlPrefix?, skip, take)` | Per-page findings rendered from the catalogue + `Detail`, grouped by rule, with page URL and element evidence. |
| `seotester_explain_check(checkId)` | Title, summary, how-to-fix, nginx/Apache/IIS/HTML snippets, docs URL. Anonymous, which needs a new `QueryRead/CheckDefinition` grant for the anonymous group: today only signed-in users have it [verified `apps/SeoTester/SeoTester/App_Data/security.json:39`]; the catalogue is already public through `GET /seotester/catalogue` [verified `Catalogue/CatalogueEndpoint.cs:20`]. |
| `seotester_get_page(site, url)` | One page's facts and findings: its latest result, and the scan that checked it (one result per page and site, decision log "M5 follow-ups"). |
| `seotester_start_scan(site)` (Write) | Starts a re-scan; same organization role and quota as the button. |
| `seotester_scan_status(run)` | Phase and progress. |

Gaps closed in this PR:

1. **Element evidence**: up to 5 CSS selectors ~~and an outer-HTML excerpt of at most 300 characters~~ per
   finding; today axe keeps only id, impact, node count, help [verified
   `SeoTester.Library/Rendering/RenderFacts.cs:46`]. Size → S7. *M5: selectors only* — S7 measured +40.7 % page-result
   growth with excerpts (limit 25 %), +20.4 % with selectors only; an excerpt also stored body text, which SeoTester never
   keeps (decision log "M5 implementation").
2. **Expired page results** (7 days): the tools say so and suggest a re-scan; never an empty "clean" list. *M5
   follow-ups:* the same for pages a later scan checked again (a site keeps one result per page, the latest): the tools
   name that scan.
3. **Untrusted text**: crawled titles, descriptions, H1s, URLs and excerpts are always fenced (D10); they
   are already cut to 200 characters [verified `CheckFinding.cs:15,48-58`].

Model: `Site`, `AuditRun`, `CheckDefinition` get `"agentPermissions": "Read"`; `PageResult` stays `None`
(large, expiring; reached through the tools). No repository link on `Site`: the agent works in the
repository already.

### 4.8 CodeCoverage adoption

| Tool | Returns |
|---|---|
| `codecoverage_list_repositories` | Repositories the caller may read, with coverage. Public ones work anonymously. |
| `codecoverage_summary(repo, branch?)` | Totals, 30-commit trend, largest drops. |
| `codecoverage_hotspots(repo, branch?, pathPrefix?, take)` | Files ranked by uncovered lines, most first; covered % and the number of uncovered ranges as columns (§4.8.1). ~~"ranked by expected gain per unit of effort"~~ superseded by S8. |
| `codecoverage_file(repo, path, commit?)` | Uncovered line ranges and partly covered branches. Line numbers only. |
| `codecoverage_patch(repo, pullRequest)` | Patch coverage and uncovered changed lines. |
| `codecoverage_drops(repo, since?)` | Commits that lowered coverage, with the files responsible. |

**4.8.1 Ranking.** Files are ranked by **uncovered lines, descending** (ties by path), and the tool description says
exactly that, with no "expected gain / effort" claim. The covered percentage and the number of uncovered line ranges
are returned as columns for the agent to weigh; they never enter the order. ~~Score = uncovered lines × ease; ease is
higher for files that already have some coverage and for files whose uncovered lines sit in few ranges. Heuristic until
S8.~~ Superseded 2026-10-12 by **S8 (FAIL)**: replaying the last 60 test-adding master commits, the ease score reached
0.03–0.42× of the uncovered-lines baseline's gain per added test line (never the 1.5× the spike required; P(≥ 1.5) ≤
0.008 in every variant); the fewer-ranges term pushed it toward small single-gap files that rarely gain
[spark_mcp_spikes.md S8].

**4.8.2 Authorization.** Repository scoping moves out of `BrowseController` methods [verified
`Controllers/BrowseController.cs:22-34`] into a service shared with the tools (D9). Unknown and forbidden
repositories answer alike (D17). `Repository` and commit summaries get `"agentPermissions": "Read"`;
per-file coverage documents stay `None`.

### 4.9 Client compatibility

| Client | Registration | Sign-in | Static token |
|---|---|---|---|
| Claude Code (2.1.296) | CIMD or DCR | `/mcp` → Authenticate, or `claude mcp login <name>`; steps up on 403 | `--header "Authorization: Bearer ${SPARK_PAT}"` |
| Codex CLI (≥ 0.148 for CIMD) | CIMD when the AS metadata advertises it with `none`, else DCR | `codex mcp login <name>`; `codex mcp add <name> --url …` signs in automatically when the PRM exists | `bearer_token_env_var = "SPARK_PAT"` |
| Gemini CLI (0.63) | DCR (no CIMD) | `/mcp auth <name>` | `headers` in `settings.json` |
| VS Code (Copilot agent mode; source-read at 42bad02a) | CIMD (`https://vscode.dev/oauth/client-metadata.json`) when advertised, else DCR | prompted when the server asks; loopback `http://127.0.0.1:33418/` (or a random port) or `https://vscode.dev/redirect` | `headers` in `mcp.json` |

Loopback redirects on any port and any path, `localhost` and `127.0.0.1`, native clients only (RFC 8252).
No client starts sign-in on a mid-session 401 without a token (S11, source-read); the 401 text and
`RequireSignIn` cover it (D17). Columns per client: [spark_mcp_spikes.md S3 / S11](spark_mcp_spikes.md).

## 5. Security

| Threat | Mitigation |
|---|---|
| Agent sees more than the user | D9 shared paths + parity tests, D10 redaction net, D6 layers, D13 grant in `IPermissionService`, D19 reference fix. |
| Agent sees more than the user allowed it | D12 grant, read per call. |
| Token reused at another service | Audience = canonical URI; at+jwt type check; no passthrough. |
| CSRF on `/mcp` | Cookies ignored; Origin validated. |
| Existence oracle | Anonymous: uniform 401; signed-in: uniform not-found (D17). |
| Malicious registration | D5 guards; unverified label; consent shows redirect host. |
| Spoofed agent identity in `AgentPermissions` | Only `VerifiedHost` is proof; the name is `UnverifiedName` (D7). |
| External token mapped to the wrong user | D22: linked external login only, issuer-bound; unmapped → 401; groups local. |
| Stolen refresh token / token | Rotation with family revocation; security-stamp change revokes everything; tokens expire. |
| Prompt injection from crawled content | D10 fencing; no data in tool descriptions; propose → apply with the echoed diff (D8). S6. |
| Agent loops | D16 limits; paging caps. |
| Untraceable agent action | D15 logs + D8 plans with client and grant. |

## 6. Spikes and required tests

### 6.1 Spikes

| # | Question | Method | Pass |
|---|---|---|---|
| S1 | Does SDK 2.2 hybrid mode run in a Spark host on net11, composite scheme untouched? | DemoApp, one echo tool, MCP Inspector + Claude Code. | 2026-07-28 and 2025-11-25 clients call the tool; cookie-only request runs as anonymous; `/spark` tests green. |
| S2 | Can the IdP run inside an app with its own cookie + external sign-in? | SeoTester locally, `ForMcp()`, sign in via GitHub at `/connect/authorize`. | Code+PKCE completes; `sub` = local id; `IPermissionService` answers identically for cookie and token. |
| S3 | Do all three clients sign in without a hand-registered client? | Claude Code, Codex, Gemini against S2; record versions and path taken. | Each signs in and calls a tool, or the failure and workaround are recorded. |
| S4 | Do group changes reach a live token? | Remove a group after sign-in, call. | Next call denied, no new sign-in. |
| S5 | Is parity real? | HR contracts, 3 users, each generic tool vs its endpoint; then delete one check. | Identical results; the mutation fails the suite. |
| S6 | Does fencing hold? | Fixture site whose title/description instruct a destructive command; ask Claude Code to fix findings. | Agent fixes, doesn't obey; transcript in `docs/spark_mcp_spike_S6.md`. |
| S7 | What does element evidence cost? | Re-scan 3 real sites with selectors + excerpts. | `PageResult` < 64 KB at p99, DB growth < 25%; otherwise selectors only. |
| S8 | Does the hotspot ranking predict cheap gains? | Replay the last 60 test-adding commits of this repo. | Top-10 files gained ≥ 1.5× coverage per test line; otherwise rank by uncovered lines and say so. |
| S9 | Do results fit the output budget? | Largest realistic call per tool on production-size data. | Every default page < 10k tokens. |
| S11 | Do clients start sign-in on a 401 mid-session, and can a user sign in voluntarily to a server that answered anonymously? | Each client: anonymous `tools/list` succeeds, then a protected `tools/call` gets 401. | Each opens the sign-in. Otherwise: per-app `RequireSignIn` switch restoring sign-in-first, documented per client. |

(S10, compare-exchange expiry, was dropped when plans moved to a collection.)

**Results (round 4, 2026-10-12, [spark_mcp_spikes.md](spark_mcp_spikes.md)):** S1 PASS live (SparkId
instead of DemoApp, which has no Authorization; MCP Inspector not run; plain `Stateless`, not hybrid).
S2 PARTIAL live (flow PASS, `sub` = local id; parity FAILS until store groups + id mapping exist). S4 FAIL
as-is, PASS with the store-provider prototype. S3 source-read only, live sign-ins with Codex ≥ 0.148 and
Gemini 0.63 pending. S11 FAIL by source reading → 401 text + `RequireSignIn` (D17); live check pending.
**S8 FAIL (2026-10-12, plan M6):** the ease score never beat "most uncovered lines" (0.03–0.42× of it) → §4.8.1 ranks
by uncovered lines [spark_mcp_spikes.md S8].
**M5 (2026-10-12):** S7 live — p99 8.9 KB (PASS), page-result growth +40.7 % with excerpts (FAIL), +20.4 % with
selectors only → selectors only. S6 prepared (fixture site, server-side dry run: every injected instruction stayed
inside the fence); the live Claude Code run is the owner's. **S6 live (2026-10-12): PASS**
([spark_mcp_spike_S6.md](spark_mcp_spike_S6.md)).

### 6.2 Required adversarial tests (red first)

External setup (D22): wrong audience · wrong issuer · `sub` linked under a different provider · same `sub`
value from another issuer · ID token presented as access token · `alg: none` and HMAC/RSA confusion ·
expired and not-yet-valid · token for another resource · unlinked `sub` → 401, not anonymous · token role
claims grant nothing · revoked shadow grant blocks the next call.

All setups: grant revoke from either side (AgentGrant ↔ OidcGrant) blocks the next call · grant type
restriction blocks generic and app tools · grant edit applies without re-sign-in · `apply_changes` with a
tampered `changes` fails · plan replay fails · plan of another user fails · expired plan fails · anonymous
write refused · anonymous uniform 401 for existing and unknown names · signed-in uniform not-found ·
cookie on `/mcp` ignored · bearer + cookie runs as the bearer · authenticated-scheme feature reset on
`/mcp` · group removal denies the bearer's next call (S4) · bearer `ISparkCurrentUser.Id` = local id ·
`RequireSignIn` 401s an unauthenticated `initialize` · unknown `resource` never yields `aud` = `client_id` ·
bad `Origin` 403 · `covt_` and GitHub OIDC rejected · personal token past
expiry rejected · security-stamp change revokes all · CIMD to a private address refused · DCR with a
non-loopback redirect refused · ~~unreferenced-type reference shows the placeholder (D19)~~ a reference to
a type the caller may not Read is absent from metadata, rows, sort/filter/distincts and kept on save (D19,
2026-10-12) · query execute, queries/get and types/{id} 401 anonymous (D18).

**Coverage (M7a audit, 2026-10-12; decision log "M7a").** Every item and the tests that hold it, as `file:line` of the test
method. **+** = added in M7a; each was written red where the property was missing, else proven falsifiable by removing its
guard once (mutation runs in the decision log). Setups: PAT (personal tokens), EMB (embedded server), EXT (external server),
REM (a Spark provider as another app's external server), STUB (test schemes). Paths: `M/` = `tests/MintPlayer.Spark.Tests/Mcp/`,
`T/` = `tests/MintPlayer.Spark.Tests/`, `E2E/` = `tests/MintPlayer.Spark.E2E.Tests/Mcp/`, `CC/` = `apps/CodeCoverage/CodeCoverage.Tests/Mcp/`,
`SEO/` = `apps/SeoTester/SeoTester.Tests/Mcp/`, `HR/` = `apps/HR/HR.Tests/Mcp/`, `FL/` = `apps/Fleet/Fleet.Tests/`.

| Item | Tests | Setups |
|---|---|---|
| Wrong audience | `M/McpExternalAuthorizationServerTests.cs:156` (theory: another audience, no audience), `:211` (opaque, introspected) | EXT |
| Wrong issuer | `M/McpExternalAuthorizationServerTests.cs:156` (another issuer with the same or its own key, the right issuer name with another key) | EXT |
| `sub` linked under another provider | `M/McpExternalAuthorizationServerTests.cs:100`, `:82` (same e-mail, no link) | EXT |
| Same `sub` from another issuer | `M/McpExternalAuthorizationServerTests.cs:156` (the linked `sub` from another issuer), `M/McpRemoteResourceTests.cs:264` | EXT, REM |
| ID token as access token | `M/McpExternalAuthorizationServerTests.cs:156` (the website's client as audience; typed `JWT`) | EXT |
| `alg: none`, HMAC/RSA confusion | `M/McpExternalAuthorizationServerTests.cs:156` (alg none; HMAC; **+** HMAC keyed with the issuer's own public key under its `kid`) | EXT |
| Expired, not yet valid | `M/McpExternalAuthorizationServerTests.cs:156` | EXT |
| Token for another resource | `M/McpExternalAuthorizationServerTests.cs:156` (**+** another MCP endpoint of the same issuer, the application's origin, a path below `/mcp`, `aud` of other endpoints beside a near miss), `M/McpEmbeddedAuthorizationServerTests.cs:220`, `M/McpRemoteResourceTests.cs:221` | EXT, EMB, REM |
| Unlinked `sub` → 401 | `M/McpExternalAuthorizationServerTests.cs:70`, `:491` (login unlinked), `M/McpRemoteResourceTests.cs:238` | EXT, REM |
| Token role claims grant nothing | `M/McpExternalAuthorizationServerTests.cs:113` | EXT |
| Revoked shadow grant | `M/McpExternalAuthorizationServerTests.cs:427` | EXT |
| Grant revoke from either side | `M/McpEmbeddedAuthorizationServerTests.cs:284` (OidcGrant withdrawn), `:299` (AgentGrant revoked), `M/McpPersonalAccessTokenTests.cs:164`, `M/McpExternalAuthorizationServerTests.cs:427`, **+** `E2E/McpAgentJourneyTests.cs:138` (revoked on the page, next call 401). REM: the provider's withdrawal ends the refresh (EMB cases); the resource side is EXT's | EMB, PAT, EXT |
| Grant type restriction, generic and app tools | `M/McpPersonalAccessTokenTests.cs:245`, **+** `M/McpEmbeddedAuthorizationServerTests.cs:354` (consent's chosen types), **+** `M/McpExternalAuthorizationServerTests.cs:371`, `CC/CoverageMcpScopingParityTests.cs:151` (app tool), **+** `SEO/SeoToolsTests.Adversarial.cs:25` (app tools; red: the SDK's generic error, fixed) | PAT, EMB, EXT |
| Grant edit without re-sign-in | `M/McpPersonalAccessTokenTests.cs:245`, **+** `M/McpExternalAuthorizationServerTests.cs:371` (narrowed and widened, same token), `:277`, `M/McpPlanTests.cs:360` | PAT, EXT, STUB |
| Tampered `changes` | `M/McpPlanTests.cs:265` | STUB |
| Plan replay | `M/McpPlanTests.cs:284`, **+** `:595` (a custom action on a selection runs once) | STUB |
| Plan of another user | `M/McpPlanTests.cs:247` (user, agent, grant) | STUB |
| Expired plan | `M/McpPlanTests.cs:302` | STUB |
| Anonymous write refused | `M/McpPlanTests.cs:437` (**+** a handed plan cannot be applied anonymously), **+** `SEO/SeoToolsTests.Adversarial.cs:94` (`start_scan`), **+** `HR/McpParityTests.cs:363` | STUB, PAT, EMB |
| Anonymous uniform 401 | `M/McpServerTests.cs:155`, `M/McpSignInChallengeTests.cs:60`, `CC/CoverageMcpScopingParityTests.cs`, `SEO/SeoToolsTests.cs:50`, **+** `SEO/SeoToolsTests.Adversarial.cs:94` (`get_page`), **+** the S5 suites | STUB, EMB, PAT |
| Signed-in uniform not-found | `M/McpServerTests.cs:180`, `CC/CoverageMcpScopingParityTests.cs`, `SEO/SeoToolsTests.cs:79`, **+** `SEO/SeoToolsTests.Adversarial.cs:61` (every read, a scan of another site), **+** the S5 suites | STUB, PAT |
| Cookie on `/mcp` ignored | `M/McpServerTests.cs:77` (stub cookie), **+** `M/McpPersonalAccessTokenTests.cs:118` (the cookie `/spark/auth/login` issues) | STUB, PAT |
| Bearer + cookie runs as the bearer | `M/McpServerTests.cs:88`, **+** `M/McpPersonalAccessTokenTests.cs:118` | STUB, PAT |
| Authenticated-scheme feature reset | `M/McpServerTests.cs:77`, `:88`, **+** `M/McpPersonalAccessTokenTests.cs:118` | STUB, PAT |
| Group removal denies the next call (S4) | `M/McpPersonalAccessTokenTests.cs:92`, **+** `M/McpEmbeddedAuthorizationServerTests.cs:380`, **+** `M/McpExternalAuthorizationServerTests.cs:405`, `FL/McpEmbeddedServerTests.cs:113`, **+** `SEO/SeoToolsTests.Adversarial.cs:127` (organization membership) | PAT, EMB, EXT |
| Bearer `ISparkCurrentUser.Id` = local id | `M/McpServerTests.cs:88`, `M/McpPersonalAccessTokenTests.cs:77`, `M/McpEmbeddedAuthorizationServerTests.cs:83`, `M/McpExternalAuthorizationServerTests.cs:53`, `M/McpRemoteResourceTests.cs:238` | all |
| `RequireSignIn` 401s `initialize` | `M/McpServerTests.cs:113`, `M/McpSignInChallengeTests.cs`, **+** `M/McpPersonalAccessTokenTests.cs:145` | STUB, PAT |
| Unknown `resource` never `aud` = `client_id` | `M/McpEmbeddedAuthorizationServerTests.cs:198`, **+** `:420` (no `resource`: the token's `aud` decoded), `M/McpRemoteResourceTests.cs` | EMB, REM |
| Bad `Origin` 403 | `M/McpServerTests.cs:133` | STUB |
| `covt_` and GitHub OIDC rejected | `CC/CoverageMcpCredentialTests.cs:21` | CodeCoverage |
| Personal token past expiry | `M/McpPersonalAccessTokenTests.cs:180` | PAT |
| Security-stamp change revokes all | `M/McpPersonalAccessTokenTests.cs:199`, `M/McpExternalAuthorizationServerTests.cs:452`, **+** `M/McpEmbeddedAuthorizationServerTests.cs:401` | PAT, EXT, EMB |
| CIMD to a private address | `T/IdentityProvider/OidcClientMetadataDocumentTests.cs:132`, `:143` (through the real SSRF-safe client) | EMB |
| DCR with a non-loopback redirect | `M/McpEmbeddedAuthorizationServerTests.cs:164` | EMB |
| Hidden reference (D19) | web: `T/Authorization/ReferenceTargetRightsTests.cs:182`, `:194`, `:238` and the rest of the class; MCP: `M/McpReferenceRedactionTests.cs:91`, `:113`, **+** `:141` (sort and filter), `M/McpPlanTests.cs:462`, **+** `:197` (kept on a plan's save), **+** `HR/McpParityTests.cs` (restricted user, every tool) | STUB, PAT |
| D18 anonymous 401 | `T/Endpoints/Queries/QueryExecuteAnonymousDenialTests.cs`, `T/Endpoints/Queries/MetadataAnonymousDenialTests.cs` | web |

Pending: none. SeoTester's items (M5) are in the table since its merge; the live runs (S3/S11 sign-ins) stay on the manual checklist; S6 ran live on 2026-10-12 (PASS).

## 7. Risks

- **OAuth server in production apps** (setup 3): only the agents' surface is mapped (`idp.ForMcp().AgentsOnly()`,
  decision log "IdP surface for MCP-only apps").
- **Spec churn**: the SDK absorbs transport changes.
- **Three setups**: three test paths; the adversarial list is the guard.
- **Gemini without CIMD**: if loopback DCR is ever turned off, Gemini users need a personal token.
- **Behaviour changes for existing apps** (D18, D19): release notes; audit of every app's security.json.

## 8. Open questions

None. Q18 was resolved in round 3 (§10, D22).

## 9. Out of scope

- An in-app assistant or server-side LLM call.
- A stdio transport.
- MCP resources and prompts beyond one prompt per adopter ("fix my SEO findings", "raise coverage of this
  repository").
- Service accounts (a non-human user per organization for CI). Personal tokens act as their creator.
- How the identity provider's own roles (client administration, developer approval) map to in-app roles per registered
  OAuth application. Apps that host the provider only for agents keep those rights switched off (owner, 2026-10-12);
  the design is tracked in [#508](https://github.com/MintPlayer/MintPlayer.Spark/issues/508).

## 10. Decision log

- **2026-10-12, investigation.** Four parallel investigations: Spark internals, the two apps, the MCP spec
  + SDK + clients, and an existing commercial framework's MCP feature (public package metadata only).
- **2026-10-12, owner corrections.** SeoTester, not CodeCoverage, is the SEO use case; CodeCoverage adopts
  too, for raising coverage.
- **Round 1 (owner, 2026-10-12), grill:**
  - Q1: the app itself issues tokens; "SparkId has nothing to do with it". Refined by Q16.
  - Q2: grant stored server-side, restricted **per entity type** (queries listed underneath), scopes
    editable on the grant → D12, D13.
  - Q3: fix the reference-target `Read` gap for web and MCP → D19.
  - Q4: personal access tokens as grants, 90-day default, 1-year max, `spat_` → D11.
  - Q5: extend `OidcGrant`. **Superseded by Q17.**
  - Q6: plans in an `McpPlans` collection with the echoed diff and 1-year retention ("you don't necessarily
    need to avoid a new collection") → D8. The owner prefers to avoid new collections where possible.
  - Q7: logs + `LastUsedAt` → D15.
  - Q8: CIMD from any host + loopback-only DCR → D5.
  - Q9: `/mcp`, with a canonical-URL override for subdomains → D20.
  - Q10: MCP follows security.json for anonymous and signed-in; uniform 401/not-found; anonymous
    read-only; **align the web's query execute** → D17, D18.
  - Q11: `EAgentPermission` flags at app, type and grant; `agentPermissions` replaces `exposeToAgents` → D6.
  - Q12: the app layer is `Func<AgentInformation, EAgentPermission>` with verified fields only → D6, D7.
  - Q13: configurable tool prefix from `ServerName` → D21.
  - Q14: keep the external authorization server, "if we manage to make it secure, add the appropriate
    tests" → D22, §6.2.
  - Q15: external tokens map through linked external logins only; shadow grant → D22.
  - Q16: three setups by flags, default all; personal tokens move to the Authorization package → D4, D11.
  - Q17: `AgentGrant` collection in the Authorization package with natural ids, source of truth → D12.
- **Round 2 (consistency review, 2026-10-12)**, reading §0 back against the grill:
  - D6: the token scope is not a cap; the grant decides. Otherwise Q2's "edit without re-sign-in" breaks.
  - D8/D17: anonymous calls can't propose (they would write plan documents); `AgentPermissions` for
    anonymous is clamped to `Read`; setup 1's 401 carries no OAuth challenge.
  - D14: the IdentityProvider depends on Authorization, so it contributes its grants through an extension
    point instead of its page "moving" into Authorization.
  - D21: the prefix default can't be remembered "once"; the guide says to set it explicitly.
  - §4.7: anonymous `seotester_explain_check` needs a new anonymous grant in SeoTester's security.json.
  - Q18 opened: the shadow grant's first-use permissions in the external setup.
- **Round 3 (owner, 2026-10-12):** Q18 as recommended: shadow grant starts "everything exposed", `Read`;
  `Write` needs the user's toggle and `mcp:write` in the external token → D22.
- **Round 4 — spikes S1–S4, S11 (2026-10-12).** Evidence in [spark_mcp_spikes.md](spark_mcp_spikes.md);
  S1, S2, S4 run live against SparkId on branch `spike/spark-mcp` (local, never pushed); S3, S11 by
  reading client source, live confirmation pending (owner, Codex ≥ 0.148 and Gemini 0.63).
  - **S1 PASS** (§S1a–c): SDK 2.2.0 on net11; plain `Stateless` serves 2025-11-25 (initialize, no
    session, 405 on GET) and 2026-07-28 clients; Claude Code 2.1.296 connected. Without a policy a cookie
    authenticates `/mcp` and beats a bearer; a bearer-only endpoint policy makes cookie-only anonymous.
    Findings: stale `ISparkAuthenticatedSchemeFeature`, SPARK020 false positive, Origin not probed.
    → **D2** reworded (pin 2.2.0, `Stateless`, no hybrid/affinity), **D3** mechanism specified.
  - **S2 PARTIAL** (§S2): code + PKCE S256 with a public client gives an `at+jwt` with `aud` = canonical
    URI, RFC 9207 `iss`, `sub` = local id. Parity fails: the bearer has no named groups and a null
    `ISparkCurrentUser.Id`. Gap list G1–G11. → **D4** prerequisite, **D5** gap items (M3b).
  - **S4 FAIL as-is, PASS with the prototype** (§S4): a per-request store provider denies the next
    bearer call after a role removal, no new sign-in. The cookie stays stale up to Identity's 30-minute
    stamp validation — existing behaviour, recorded, unchanged. → **§4.6** rewritten (to be built).
  - **S3 source-read** (§S3 / S11): redirect URIs, CIMD support (Codex needs the metadata flags, Gemini
    none), scope behaviour per client. → **D5** loopback matching for `localhost`/`127.0.0.1`, any port
    and path; metadata flags; advertise `offline_access`.
  - **S11 FAIL by source reading** (§S3 / S11): no client opens sign-in on a mid-session 401 without a
    token. → **D17**: per-client sign-in instructions in the 401/403 error text (owner idea; live test
    pending) and per-app `RequireSignIn` (default off). Step-up on 403 only in Claude Code.
- **M3a implementation (2026-10-12).** Commits ae7999b8 (seam), 96e4baed (Authorization), 6fdb5408
  (tests). Decisions taken while building, with their evidence:
  - **The seam lives in Abstractions** (owner direction; the Authorization package references only
    Abstractions, and neither it nor the IdentityProvider may reference Mcp, nor Mcp them).
    `AgentInformation`, `EAgentKind` and `EAgentAuthentication` moved from Mcp to namespace
    `MintPlayer.Spark.Abstractions` (beside `EAgentPermission`); `AgentInformation` gained `GrantId`.
    New: `IAgentCredentialScheme` (Setup, Scheme, SubjectIsLocalUserId, `DescribeAsync` →
    `AgentCredential` = agent + `AgentGrantScope`), registered by a token package as an enumerable
    singleton; Mcp's effective setups are flags ∩ these registrations (no assembly-name detection; the
    built-in detector list is gone). `IAgentExposure`, implemented by Mcp, tells the account page (and, in
    M3b, the consent screen) what may be granted: exposed types the caller may see, app layer per agent,
    effective setups.
  - **D13, refined: the grant is enforced in Mcp's `IAgentRequestRestriction`, not a second one in the
    Authorization package.** Only Mcp knows which targets are entity types (`IModelLoader` is core); a
    grant restriction without it would either deny application resources or need a new core seam. The
    Authorization package owns the grant's semantics (`AgentGrant.ToScope()` → `AgentGrantScope.For`),
    read per request through `DescribeAsync`; the restriction intersects it with the type layer inside
    `IPermissionService`, so every tool still honours it. Application resources (non-type targets) follow
    the app layer, the grant's read/changes cap and security.json. Evidence: with the grant clause removed,
    `The_grant_narrows_tools_and_calls_and_an_edit_applies_to_the_next_call` fails (mutation run).
  - **Personal access tokens are `/mcp`-only.** The scheme is registered with ASP.NET Core but not as a
    Spark credential (`AddCredentialScheme`), because the agent layers (app, type, grant) exist only on
    `/mcp`; on the web a token would act with the user's full rights. Test `A_token_opens_no_web_endpoint`.
    The scheme's `SubjectIsLocalUserId` reaches core through the gate's authenticated-scheme feature.
  - **Token format `spat_{base64url(userId)}.{tokenId}.{secret}`**: the grant's natural id (D12) is a
    point load, with no index (stale right after creation) and no lookup collection (owner prefers none).
    The user id is not a secret; the 256-bit secret is, stored as SHA-256 and compared in constant time.
  - **Store-backed groups** (`AgentStoreGroupMembership`, Authorization): active for any non-ambient scheme
    with `SubjectIsLocalUserId`, so setup 3's tokens (M3b) get them with no further work. Composed through
    `AddGroupMembershipProvider`, which moved to Abstractions (same namespace `MintPlayer.Spark.Extensions`,
    callers unchanged) together with its internal composition types. Evidence: S4 test red without the
    registration, green with it.
  - **Revocation:** deleting the grant; expiry; the user's security stamp at creation ≠ now (password change,
    removed login); lockout; deleted user. All answer 401 on `/mcp` (never anonymous). Grants are cached 5 s
    per node and invalidated on edit/revoke on the node that made the change; the user (stamp, lockout,
    groups) is read on every call.
  - **D14 endpoints:** `/spark/auth/manage/agents` (+ `tokens`, `update`, `revoke`,
    `applications/revoke`), cookie + antiforgery, mapped only with Mcp (`IAgentExposure`) or an
    `ISparkConnectedApplicationSource` (the IdentityProvider's extension point, M3b). A grant is addressed
    by its key below the signed-in user's prefix; a choice beyond app(agent) or the type is refused, not
    trimmed. Client: `withConnectedAgents()` (route `account/agents`, overview link), ng-spark 22.35.0.
  - **For M3b:** the IdP registers an `IAgentCredentialScheme` (Setup = Embedded) whose `DescribeAsync`
    loads `AgentGrants/{userId}/app/{clientId}` via `AgentGrantStore` (internal, IVT to the IdP exists); its
    `/connect/applications` contribution is an `ISparkConnectedApplicationSource`; OidcGrant ↔ AgentGrant
    revocation uses `AgentGrant.OidcGrantId` (no hook yet: revoking an AgentGrant does not touch OidcGrant).
- **M3b implementation (2026-10-12).** The embedded authorization server (`idp.ForMcp()`), the RFC 9728 side in
  Mcp, and the D17 HTTP 401. Decisions taken while building, with their evidence:
  - **Who serves RFC 9728: the Mcp package.** The document describes the resource (`/mcp`, its canonical URL),
    and the server that signs agents in may be the embedded one or an external one (M3c), so each OAuth setup
    names its issuer through two new default members of `IAgentCredentialScheme`: `AuthorizationServer(requestOrigin)`
    and `ScopesSupported`. Mapped at `/.well-known/oauth-protected-resource/mcp` and the bare well-known path,
    only when a setup offers OAuth; the embedded server is listed first (§4.2). The challenge is
    `Bearer resource_metadata="…", scope="mcp:read mcp:write"` (+ `error="invalid_token"` for a refused token);
    with personal tokens alone it stays a plain `Bearer` and no document exists. The canonical URL reaches the
    IdentityProvider through `IAgentExposure.ResourceUrl` (new); `ForMcp()` without `AddMcp` or without a canonical
    URL refuses startup, because an audience guessed from `Host` is the passthrough hole.
  - **G11: scope ownership is "before the first `.` or `:`"**, and `OidcResource.Audience` (new, optional) separates
    the audience from the name. `OidcResources/mcp` (name `mcp`, audience = canonical URL) owns `mcp:read`/`mcp:write`;
    identity scopes still resolve first, so no existing scope changes meaning. Chosen over a scope→resource link
    collection (owner prefers no new collections) and keeps every lookup a point load.
  - **G4: an MCP request names only the canonical URL.** A request for an `mcp:*` scope may name no other `resource`
    and carry no other API's scope (`invalid_target` / `invalid_scope`); no `resource` implies the canonical URL. The
    token endpoint checks a named `resource` against the grant's (Codex always sends one). The generator's
    `client_id` audience fallback is kept for non-MCP clients (existing behaviour) and is an exception for MCP
    tokens. `/mcp` accepts a token whose `aud` is exactly `[canonical]`, `typ` `at+jwt`, a live record and grant,
    and no `cnf` (sender-constrained tokens cannot be checked there). Evidence: mutation run below.
  - **CIMD clients are materialised** as `OidcApplications/agent-{sha256(clientId)[..32]}` and the client id is
    reserved, so the token, revocation and introspection endpoints find them like any client, by a point load
    added in front of the index lookup (consistent the instant the client exists). Refetched after
    `MetadataDocumentCacheFor` (1 h). A pre-registered client with the same id wins. Fetch guards: the existing
    SSRF-safe client (one resolution for check and connect), port 443, no redirects, 5 KiB, 5 s; URL with a path
    and no user info, query, fragment, dot segments (also `%2e`) or trailing `/` (a RavenDB id ending in `/` asks for
    generation, and the grant id ends with the client id); optional host allow-list; the document must name its own
    URL, be public (`none`, no secret) and list https or loopback redirects. Fetches and anonymous registrations
    share an always-on per-address budget (20/h), independent of the app's rate-limiter middleware.
  - **Anonymous DCR** answers only a request without `Authorization`; one with an initial access token is still a
    developer's registration. Loopback or VS Code redirects only, public, no registration access token, client id
    `mcp_…`, pruned after 30 days without use by `OidcAgentClientCleanupService` (every 6 h; bounded index waits).
    Expired codes and tokens need no job: they carry `@expires` (existing).
  - **VS Code [verified, source-read]:** microsoft/vscode at 42bad02a, `src/vs/base/common/oauth.ts:946-972` registers
    `https://insiders.vscode.dev/redirect`, `https://vscode.dev/redirect`, `http://127.0.0.1/`, `http://127.0.0.1:33418/`
    (`token_endpoint_auth_method: none`); `workbench/api/node/loopbackServer.ts:106-158` listens on 33418 or a random
    port; `workbench/api/common/extHostAuthentication.ts:750` sends `https://vscode.dev/redirect` for every product,
    the `vscode://`/`vscode-insiders://` return target travels in `state` and is **never a `redirect_uri`**. Its
    CIMD documents (`https://vscode.dev/oauth/client-metadata.json`, insiders likewise) list `http://127.0.0.1:33418/`
    and the product's own redirect. So the built-in exact list is the two https URIs only, beside the loopback rule;
    no custom scheme is accepted.
  - **Refresh tokens for agents regardless of `offline_access`** (G8), rotating with family revocation (existing
    code), and not tied to the browser session, so signing out of the website does not sign the agent out. `sub` is
    the local id for every MCP token whatever the client's subject type (G10).
  - **Deviation: the consent screen is server-rendered, not Angular.** Every `/connect` page is server-rendered
    (localised, themed, antiforgery, and the `/connect` middleware's `frame-ancestors 'none'` + `X-Frame-Options:
    DENY`); an Angular page would need a second API and lose those for free. `AgentConsent` replaces the scope
    checkboxes for MCP requests: verified host or "unverified", loopback notice, all types or chosen types (with
    their queries), read or read + changes, limited by `IAgentExposure.AppPermissions(agent)` and each type's
    `agentPermissions`; a forged POST beyond that is refused, not trimmed. A locked account gets 403. Every agent
    sign-in shows it (no remembered or implicit consent): it is where the grant is chosen.
  - **D12 links:** consent writes `AgentGrants/{userId}/app/{clientId}` with `OidcGrantId` and the user's security
    stamp (a stamp change refuses the token on `/mcp`, as for personal tokens). Withdrawing the `OidcGrant` deletes
    the agent grant (`OidcGrantWithdrawal`); revoking the agent grant on Connected applications withdraws the
    `OidcGrant` through a new internal `IAgentGrantLink` (Authorization, IVT to the IdP). The IdP's sign-ins at
    other websites are an `ISparkConnectedApplicationSource` (registered with or without `ForMcp()`; agent clients
    excluded, they are listed as agent grants).
  - **D17 as HTTP 401.** With an OAuth setup wired, `SparkMcpResult.Refuse()` for an anonymous caller marks the
    request; the gate buffers anonymous responses on such a server and replaces the tool result with HTTP 401, the
    challenge, and a JSON-RPC error (code -32001, the call's id, `McpSignInText`). `RequireSignIn` and refused tokens
    answer the same body (id read from the request). Without OAuth the tool-error text stays. Uniform for exposed,
    unexposed and unknown types and queries (test).
  - **Audit:** `registration.dynamic` (agent flag), `registration.metadata-document`, `registration.pruned`,
    `token.issued` (MCP tokens), `token.revoked`, `token.pkce.failed`.
  - **Evidence:** `McpEmbeddedAuthorizationServerTests` (18 cases), `McpSignInChallengeTests` (6),
    `OidcClientMetadataDocumentTests` (28 cases), 52/52 green. Mutation run (red with the guard removed, green
    restored): audience check off → `A_token_for_another_resource_is_refused_on_mcp` fails; loopback rule off → 5
    `Anonymous_registration_refuses_a_non_loopback_redirect` cases fail; self-naming check off →
    `A_document_that_names_another_client_id_is_refused` fails; the 401 swap off →
    `Anonymous_refusals_are_one_uniform_http_401_with_the_challenge` fails.
  - **For M3c:** register the external setup's `IAgentCredentialScheme` with `AuthorizationServer` = the external
    issuer and its scopes; the PRM and challenge then list it after the embedded server with no Mcp change. The
    D17 401 applies to it unchanged. The `ext/` grant and the linked-login mapping remain M3c's.
- **M3c implementation (2026-10-12).** Setup 2: `spark.AddExternalAgentAuthorizationServer(issuer, loginProvider, o => …)`
  in the Authorization package (`ResourceServer` namespace). Decisions taken while building, with their evidence:
  - **Validation reuses the resource server, under its own scheme.** `AddSparkResourceServer`'s scheme registration
    became the internal `AddTokenScheme(services, scheme, options)`; setup 2 registers it as
    `Spark.Agent.External.Token` (JWT bearer, or `SparkIntrospectionHandler` with `UseIntrospection`) and does **not**
    declare it a Spark credential, so the token opens `/mcp` only (as personal tokens, M3a). Calling
    `AddSparkResourceServer` itself was rejected: it registers the app's one `SparkJwtBearerExtensions.Scheme` as a web
    credential, which an app may already use for its own API (Fleet), and a web credential would act without the agent
    layers. Issuer pinned (= discovery authority), signature from its published keys, lifetime, `at+jwt` unless
    `RequireAccessTokenType = false`; a `cnf`-bound token is refused (as in M3b).
  - **Introspection: supported, opt-in** (`UseIntrospection`), because the resource server already speaks RFC 7662 and
    D22 requires it for opaque tokens; every token then goes through introspection (the resource server's model: one
    mode per scheme). The answer's `aud` must include and its `iss` equal the configured values (existing handler).
  - **The audience is the MCP canonical URL, not configurable.** Filled at startup from `IAgentExposure.ResourceUrl` into
    the shared options, which the scheme reads on first use; startup fails without `AddMcp` or a canonical URL. JWT
    validation accepts a token whose `aud` contains the canonical URL (JwtBearer semantics; external servers may add
    audiences), unlike the embedded server's exactly-one rule, which governs tokens we mint.
  - **The mapping lives inside the scheme, so `SubjectIsLocalUserId` is true.** The outer handler
    (`Spark.Agent.External`) authenticates the inner scheme, then finds the user through Identity's
    `FindByLoginAsync(loginProvider, token[SubjectClaim])` (new internal `IAgentLinkedLogins`, generic over `TUser`) and
    emits a principal whose `sub` is the **local** id. Core's `ISparkCurrentUser.Id` and the store-backed groups (M3a)
    then work unchanged; the external subject never reaches them. Never by email; the issuer is pinned before the
    lookup, so another issuer's equal `sub` never reaches it; `SubjectClaim` (default `sub`) exists for servers whose
    access-token `sub` differs from the sign-in's login key (Entra: `oid`). Startup fails when `loginProvider` is not a
    registered remote scheme (D22 "configured as one of the app's sign-in providers").
  - **Shadow grant:** created on first use with `CreateIfMissingAsync` (empty change vector, so concurrent first calls
    cannot overwrite each other or a user's edit), `Kind = ExternalAuthorizationServer`, client id from `azp` else
    `client_id` (a token with neither is refused: the grant id needs it), `Issuer` (new field, shown on Connected
    applications as a badge), no verified host, everything exposed, `Read`.
  - **D22 write cap** in `ExternalAgentServerScheme.ScopeOf`: the grant scope's `Permissions` lose `Write` unless the
    token's `scope`/`scp` contains `WriteScope` (default `mcp:write`, ordinal); `AgentGrantScope.For` caps chosen types by
    `Permissions`, so the cap reaches every type. M4's write tools go through the same `IPermissionService` layer.
  - **Deviation: revoking an external grant keeps it as disconnected** (`AgentGrant.RevokedAt`, status `disconnected`)
    instead of deleting it. The external server keeps issuing tokens (refresh tokens included), so a deleted shadow grant
    would be recreated by the agent's next call and the revocation would last one call. A disconnected grant refuses
    every call until the user edits it (`update` clears `RevokedAt`). Unlinking the login, lockout and deletion of the
    user refuse the next call too. ~~No security-stamp check for `ext/` grants: the user never consented to a stamp, and
    removing the login (which changes the stamp) already unmaps the token.~~ *Superseded 2026-10-12 (owner decision 4,
    "Demo apps" below): `ext/` grants are stamp-bound like the others.*
  - **Evidence:** `McpExternalAuthorizationServerTests` (29 cases, in-memory RSA issuer and an RFC 7662 stub), green,
    with `McpPersonalAccessTokenTests`, `McpEmbeddedAuthorizationServerTests`, `SparkResourceServerTests`,
    `McpSignInChallengeTests`, `McpServerTests` (102/102). Mutation runs (red with the guard removed, green restored):
    audience validation off → "another audience" and "no audience" fail; write cap off →
    `Changes_need_mcp_write_in_the_token_even_with_the_users_toggle` and `The_write_cap_is_the_intersection_…` fail;
    the `RevokedAt` checks off → `A_disconnected_grant_refuses_the_next_call_…` fails; the lookup widened to another
    provider → `A_subject_linked_under_another_provider_is_not_mapped` fails.
  - **For M7:** the §6.2 external list is covered except "token for another resource" beyond the audience cases and a
    live run against a real Entra/Keycloak/Auth0 tenant (none tried); `apply_changes` through an external token once M4
    lands (the cap is already enforced in `IPermissionService`, tested through the write tool `acme_touch`).
- **M4 implementation (2026-10-12).** Writes: `propose_changes` → `apply_changes` over `McpPlans`, and HR as the demo.
  Decisions taken while building, with their evidence:
  - **Both tools need `Write`** (neither is `ReadOnly`), so §4.4's "propose needs Read, signed in" became "Write, signed
    in": a plan that can never be applied is noise, and every operation's check goes through `IPermissionService`,
    where Edit/New/Delete/custom actions need `Write` anyway (`McpAgentRestriction.Needs`). An anonymous caller (clamped
    to `Read`, D17) therefore never sees them; a direct call answers the SDK's "Unknown tool", and the planner refuses an
    anonymous owner with `SparkMcpResult.Refuse()` as a second line. `apply_changes` is annotated `Destructive`.
  - **Check without writing is the write path itself.** `DatabaseAccess` runs its save and delete sequences with a
    `checkOnly` flag that returns after the last gate and the validation (rights, natural-id probe, row gates, collection
    guard, version, disabled action, write shield, `ISaveValidation`); `PersistentObjectSavePath` gained
    `CheckCreateAsync`/`CheckUpdateAsync`/`CheckDeleteAsync` and a shared `DeleteAsync` (the `/spark/po/delete` endpoint
    now calls it, D9); `CustomActionExecution.CheckAsync` stops before the quota gates (a check is never counted).
    Interceptors, `MapAsync` and WITH CHECK run only at apply, with the real save.
  - **Operations are built as the web client posts them:** the object loaded through `IPersistentObjectReadPath` (a create
    scaffolded as `/spark/po/new` does, `OnNewAsync` included), through Spark's JSON and back, then the agent's values set.
    An attribute absent for the caller (denied, a hidden reference per D19, or nonexistent) or read-only is refused by name
    with one message for all of them; one the write shield would drop silently is refused the same way, because a silent
    drop would make the shown diff false. One operation per object; at most 50; at most 30,000 characters of diff.
  - **The echo:** `changes` is one string per operation (`Field: before -> after`, compact JSON values); the propose answer
    fences `{planId, changes}` as data and keeps the instruction outside the fence. Apply compares the echo with the stored
    rendering (line endings and trailing spaces normalised), then **re-prepares every operation as the caller sees it now
    and refuses if its rendering changed**, then runs every check again before any write.
  - **Binding and one-shot:** the plan stores user id, client id and grant id; any mismatch, a malformed id (`McpPlans/`
    + 32 hex, so no other document can be loaded as a plan) and an unknown plan answer the uniform not-found and do not
    use the plan up. The first apply that passes binding, status, expiry and echo **claims** the plan (status
    `Applying`, saved with optimistic concurrency in its own session), so two racing applies run it at most once. A
    mistyped echo does not claim. Expiry is checked on read (`ExpiresAt`), never left to the sweep; `AddMcp` calls
    `RequireDocumentExpiration("Mcp")` as the IdentityProvider, Messaging and MailManager do.
  - **Atomicity (the honest guarantee):** a new internal `ISparkWriteUnit` (`DatabaseAccess.WriteUnit.cs`) runs a plan's
    saves and deletes through the unchanged save path but defers the commit: one `SaveChangesAsync` carries every write,
    each with the change vector captured at propose time, **plus the plan's `Applied` record**. RavenDB applies one batch
    request as one transaction on the node that takes it, so a document changed by anyone — even during the apply —
    fails the whole request: `Conflict (409)…`, nothing written, plan `Failed`. Single-node, like every Spark save, not
    cluster-wide. After-save/after-delete interceptors run after that commit; a failed batch clears the request session
    (a deferred delete cannot be withdrawn otherwise); a framework path that commits on its own (bulk delete)
    refuses to run inside a batch instead of committing early. **Not covered:** a write an interceptor or Actions class
    commits itself. **A custom action is always a plan of its own** (it runs code that may commit), executed through
    `CustomActionExecution` with its etag checked at prepare; its plan record is written after it, separately. A create
    an interceptor cancels fails the whole plan.
  - **Retention:** applied and failed plans keep `PlanRetention` (`Spark:Mcp:PlanRetention`, default 365 days, must be
    positive) as `@expires`; the record holds user, client, kind, grant, summary, operations, the diff, created ids, error.
  - **HR:** `AddMcp` (prefix `hr`, app `Read | Write`, anonymous `Read`); the contracts are `CarreerJob` rows (an
    `AsDetail` of `Person`), so `Person` and `CarreerJob` are `"Read, Write"` and `Profession`/`Company` `"Read"` (their
    references would otherwise be hidden, D19). ~~HR signs in against SparkId, so the setup wired is personal access tokens
    (account page); setup 2 against SparkId is possible with M3c's `AddExternalAgentAuthorizationServer` but not wired.~~
    *Superseded 2026-10-12: HR now takes SparkId's agent tokens (setup 2) beside personal access tokens ("Demo apps" below).*
  - **The type-level right is asked first** (`New`/`Edit`/`Delete` on the type, before the object is scaffolded or an
    attribute looked at), as the web endpoints do: otherwise a caller who may not create a type could tell its attribute
    names from the answers, and the application's `OnNewAsync` would run for them. Red without it
    (`A_caller_who_may_not_create_a_type_learns_nothing_about_its_attributes`), green with it.
  - **Evidence:** `McpPlanTests` 21/21 green (custom-action plans included: checked at propose without running, run
    once at apply, refused beside other operations and without the action right); the related classes (`Mcp`,
    `Endpoints.PersistentObject`, `CustomAction`, `DurableAfterCommit`, `Interceptor`) 397/397 green on the core change.
    Mutation runs (guard removed → red, restored → green): binding check off →
    the 3 `A_plan_is_bound_to_the_user_agent_and_grant…` cases fail; echo check off → `An_echo_that_differs…` fails;
    status check off → `A_plan_runs_once` fails; expiry check off → `An_expired_plan_is_refused` fails; one-commit batching
    off (each save commits) → `A_write_racing_the_apply_fails_the_whole_commit` fails. The apply-time re-check removed
    stays green: the real save refuses a revoked right too (the re-check only moves the refusal before any write).
  - **For M5/M7:** a custom action on a selection (`query` + `ids`) is passed through to `CustomActionExecution` but has
    no MCP test, and only the object it runs on carries an etag (selected rows are re-materialized at apply, as on the
    web); SeoTester's `seotester_start_scan` is its own tool, not a plan. `apply_changes` through an external (M3c) token
    and the parity suite's write half are M7's. HR's MCP wiring builds but has not been run against the live app.
- **M6 implementation (2026-10-12).** CodeCoverage adopts the MCP server on the embedded setup. Commits ff8d72b7 (Mcp
  package), c7af03f6 (scoping service, agent identification), fd4febd1 (tools and wiring), b937b351 (tests). Decisions
  taken while building, with their evidence:
  - **S8 FAIL → §4.8.1 rewritten.** `codecoverage_hotspots` ranks by uncovered lines, ties by path; covered percentage and
    uncovered ranges (S8's run rule: a non-coverable line does not split a run, a partial line counts as covered) are
    columns. Evidence: [spark_mcp_spikes.md S8](spark_mcp_spikes.md#s8--does-the-hotspot-ranking-predict-cheap-gains);
    `CoverageToolsTests.Hotspots_rank_by_uncovered_lines_…` on a fixture where the old ease score orders differently.
  - **One gate, `IRepositoryScope`.** `BrowseController`'s scoping (owner keys before resolving and unconditionally, an
    unknown forge resolving to nothing, the listing rule, the repository's own commits without fork contributions, its
    branches) moved into a scoped service both surfaces call; where a commit's tree and files live (`CoverageSources`)
    too. The tools resolve every repository through it and refuse every null alike. Evidence: the existing browse classes
    unchanged and green (`BrowseControllerTests`, `…AssemblyTests`, `…RemainderTests`, `RepositoryExistenceParityTests`,
    `CommitGraphVisibilityTests`, `DependencyGraphEndpointTests`); `CoverageMcpScopingParityTests` compares MCP and browse
    per caller (anonymous over HTTP; signed in, the controller in the host under a cookie principal) for a public
    repository, the caller's private one (also by a former name), another organisation's, a missing one, an unknown owner
    and an unknown forge: anonymous answers are byte-identical HTTP 401s with the challenge, signed-in ones byte-identical
    "Not found." (browse: 401/404 alike).
  - **Bug found: agents were invisible to CodeCoverage's owner check.** `ForgeIntegrationResolver` and `GitHubAccessService`
    found the viewer with `UserManager.GetUserAsync(principal)` (the cookie's NameIdentifier); an agent token carries `sub`,
    so an agent signed in as an owner saw public repositories only (core's `ISparkCurrentUser` had been fixed in M1, this
    app read the principal itself). Both ask `ISparkCurrentUser.Id` now. Evidence, red → green: before the fix the parity
    case for acme-dev's own organisation answered "Not found."; `GitHubAccessServiceRefreshTests.An_agent_caller_without_a_NameIdentifier_claim_…`
    is new. Each fix is needed on its own (mutation runs, file restored from the commit): the old `ForgeIntegrationResolver`
    alone fails 3 tests (acme-dev's private repository, the list parity, the personal token opening `/mcp`), the old
    `GitHubAccessService` alone fails those 3 and the new unit test. The guide now says to identify users through
    `ISparkCurrentUser.Id`.
  - **Bug found in the package: a `Spark:Mcp` section made `Authentication` explicit.** .NET's binder assigns every settable
    property its current value when the section exists, so any key (the test host's `Quota`, a deployment's `CanonicalUrl`)
    set the explicit flag and startup refused for setups never named. ~~`AddMcp` now sets the flag from the presence of the
    `Authentication` key.~~ *Reconciled at the rebase onto `feat/spark-mcp` (2026-10-12): the demo-app work found the same
    bug independently ("Demo apps" below) and fixed it in `SparkMcpOptions.Create`, which also un-pins `ToolPrefix`. That
    one implementation stays; M6's line in `AddMcp` is gone, both sides' tests stay (M6's through `AddMcp`, the other's
    through `Create`).* Evidence: the CodeCoverage MCP host failed to boot (all 27 of its tests failed at fixture start with
    "mcp.Authentication names setups that are not wired up: ExternalAuthorizationServer …") until the fix; `McpServerTests.A_Spark_Mcp_section_without_an_Authentication_key_…`
    and `An_Authentication_key_in_configuration_is_explicit` (with the fixing line removed, the first fails: "found true").
    The Spark MCP namespace stays green: 146/146 after the rebase onto `feat/spark-mcp` (102/102 before it).
  - **Tools.** The six of §4.8, all `ReadOnly`; the app layer is `Read` for every agent (D24). A repository is named
    `forge/owner/name` (the website's URL segments; no forge is defaulted, an unknown one answers like a missing
    repository). Each tool asks `IPermissionService` for the rights it reads (`Query`/`Read` on `Repository`, `Read`/`Query` on
    `Commit`), so a grant without the type refuses it (test). Malformed arguments answer `SparkMcpResult.Invalid` (made
    public, with `Track`), everything else the uniform `Refuse`. The default commit is the one the repository's headline shows
    (`LatestCoverageSha`) on the default branch, else the branch's newest complete, else newest. `patch` includes fork
    contributions (the pull request is their context) and reads the added lines from the forge's comparison (cached 5 min),
    matched as the patch number was. `drops` compares the commit's and its parent's tree summaries. Lists are bounded (≤ 50
    rows, ≤ 400 ranges, ≤ 200 partial branches, ≤ 30 × 40 patch ranges, ≤ 10 drops × 5 files) with omitted counts, so the
    40k-character cap never cuts an answer (test: 3000 ranges, 50 long paths). Reads live in `ICoverageInsights`; the tool
    class holds no session (SPARK050 quiet in the build log).
  - **Prompt.** `mcp.AddPrompts<T>()` added to the package, prefixed like the tools (D21); a prompt reads no data, so it is
    listed to everyone. `codecoverage_raise_coverage(repository, branch?)`; a test checks it names only listed tools.
  - **The embedded server inside CodeCoverage.** `AddIdentityProvider(idp => { idp.Issuer ??= origin; idp.ForMcp(); })` and
    `mcp.CanonicalUrl ??= origin + "/mcp"`, origin = `Spark:Auth:PublicBaseUrl`, else `Coverage:BaseUrl`. The app signs in
    with GitHub only (local credentials off), so `/connect/login` shows the GitHub button and resumes the authorization after
    it. Evidence: `CoverageMcpSignInTests` (RFC 9728/8414 documents; an anonymous loopback client's authorize → `/connect/login`
    → `/spark/auth/external-login?provider=GitHub` → github.com). Personal access tokens: `withConnectedAgents()` in the SPA.
  - **Deviation: no signing-key secret.** The IdP keeps its keys in RavenDB (`OidcKeys`), the private halves protected by Data
    Protection, generated at first start (I10); `SigningKeyPath` only imports a legacy file. M9 therefore adds no secret and no
    configuration; the Data Protection volume now also guards the signing keys, and losing it refuses startup
    (`docs/code-coverage/hosting.md` "AI agents (MCP)", compose comment corrected).
  - **Decision for the owner: the IdP's own rights are switched off** (`"libraries": { "identity-provider": false }`). The MCP
    preset needs none of them, the anonymous surface is unchanged (`securityPosture.txt`: the layer "switched off", 46 inert
    grants), and binding the slots needs a developers group of its own (bound to `Administrators`, every approved developer
    would become one). The IdP still maps its whole endpoint set (route snapshot +42: device, PAR, introspection, the portal
    API); only agent clients exist, and the portal's admin endpoints refuse without the rights. Whether `ForMcp()` should unmap
    the non-MCP endpoints is open (§7 "only the MCP preset is on" holds per client, not per route).
  - **D23** needed no code: the gate refuses every credential no agent setup accepts. Evidence: `CoverageMcpCredentialTests`
    shows a `covt_` token and a GitHub Actions token, each accepted by its own scheme in the same host, get 401
    `invalid_token` on `/mcp` while the same call without any credential succeeds. Mutation: with the gate's refusal of an
    unaccepted credential disabled, the `covt_` call answers 200 as anonymous and the test fails.
  - **Totals:** the 27 new CodeCoverage MCP tests, and every touched or host-booting CodeCoverage class (browse, route
    snapshot, registration inventory, surface tests, the end-to-end classes): 155/155 green.
  - **For M7:** the live run (Claude Code against `https://coverage.mintplayer.com/mcp`); the SPA route spec was not run here (no
    `node_modules` in the agent's worktree).
- **Demo apps: HR on SparkId (setup 2), Fleet embedded (setup 3); remote resources; stamp-bound external grants
  (2026-10-12).** Owner decisions of 2026-10-12: (1) a Spark identity provider can be the external authorization server
  of *another* application's `/mcp`; (2) HR uses SparkId that way, beside personal tokens; (3) Fleet hosts its own
  embedded server, beside personal tokens; (4) setup 2 grants are security-stamp bound like the others. Decisions taken
  while building, with their evidence:
  - **The API: `idp.ForMcpResource(url, r => …)`**, one call per endpoint, or `Spark:IdentityProvider:Mcp:Resources`
    (`Url`, `DisplayName`, `Permissions`); `idp.ForMcp()` unchanged for the provider's own endpoint, and both combine.
    Both share `SparkIdentityProviderMcpOptions` (CIMD hosts, DCR, lifetimes). `LocalEndpoint` (`bool?`): `ForMcp()`
    sets it; unset, a `Spark:IdentityProvider:Mcp` section serves the own endpoint only when it lists no `Resources`, so
    SparkId's configuration needs no switch and an old config-only `ForMcp` keeps its meaning. The `/mcp` bearer scheme
    (`IAgentCredentialScheme`, setup 3) is registered only with the own endpoint: otherwise the provider would list itself
    in an RFC 9728 document for an audience it never mints.
  - **One resource per endpoint, the scopes shared (owner: "an `OidcResources` entry whose `Audience` is that canonical
    URL, scopes `mcp:read`/`mcp:write`").** `OidcResources/mcp` stays the own endpoint's; another endpoint is
    `OidcResources/mcp-{sha256(url)[..32]}` (a URL cannot be a document id). Scope ownership by prefix (G11) cannot tell
    them apart, so `OidcScopeCatalog.LoadAsync(…, mcpResourceId, …)` resolves `mcp:*` in the endpoint the request is for:
    `/connect/authorize` chooses the endpoint first (`OidcMcpResources.Select`), the token endpoint and every refresh
    from the chain's recorded `resource`. An endpoint no longer configured leaves the scopes undefined: no token
    (`invalid_scope`), never one for the own endpoint's audience. Chosen over one `mcp` resource with several audiences
    (the owner named a resource per endpoint) and over endpoint-specific scope names (the resource app's
    `WriteScope`/`ScopesSupported` default to `mcp:read`/`mcp:write`, and agents read them from its RFC 9728 document).
  - **Choosing the endpoint (D5 G4 generalised):** exactly one distinct `resource`, an endpoint listed here; two at once or
    an unknown one is `invalid_target`. None named: the own endpoint (as before), else the only one, else refused. URL
    rules: https, or http on a loopback host (local development); no query, fragment or user info; a trailing `/` dropped
    as the Mcp package drops it; malformed or duplicate (also against the own canonical URL) refuses startup.
  - **Consent for another application's endpoint (the open point of decision 1):** it names the application and its
    host ("It will work as you in {name} ({host}), and nowhere else"), offers **read** or **read and make changes** (the
    latter only when `r.Permissions` has `Write`, default `Read | Write`), and **no types**: the provider cannot see that
    application's exposure, `AgentPermissions` or the user's rights there. It says where the rest is decided: the
    application's Connected applications, where the shadow grant (D22) starts as everything exposed, read only. "Make
    changes" puts `mcp:write` in the token, the cap D22 applies beside the user's toggle there. No agent grant is written
    at the provider (it would decide nothing: no `/mcp` there reads it); the `OidcGrant` keeps the bookkeeping (refresh
    family, revocation at `/connect/applications`). A forged `write` beyond `Permissions`, or any posted type, is refused.
  - **Subject:** the public user id (G10), as for the own endpoint. The application maps it through the login its users
    linked by signing in at the provider, whose key is the `sub` the provider gave its sign-in client: that client must
    use public subjects (the default; HR's `hr` client does). A pairwise one fails closed (401). Not built: a per-endpoint
    "subject as client X sees it".
  - **One OAuth grant per user and client at the provider:** an agent client signed in for two endpoints shares it;
    revoking it (or the provider's own agent grant, which withdraws it, D12) ends the client's sign-ins for both. Refresh
    chains keep their own scopes and `resource`, so no endpoint's token leaks into the other's.
  - **D22 amended (decision 4):** the shadow grant stores the user's security stamp at creation; a mismatch refuses the
    call ("security stamp changed") and lists the grant as `disconnected`; `update` (allowing it again) re-stamps it.
    Only `ext/` grants are re-stamped: a personal token stays revoked after a password change, and an embedded grant is
    replaced by the next consent. Supersedes M3c's "no security-stamp check for `ext/` grants".
  - **Bug found: Connected applications offered Edit only for `active` grants**, so a disconnected external agent could not
    be allowed again from the page although M3c's text said "edit it to allow this agent again". Edit now shows for
    `disconnected` (Disconnect hides); spec case in `spark-connected-agents.component.spec.ts`.
  - **Bug found: agent principals lacked `ClaimTypes.NameIdentifier`.** `ISparkCurrentUser.Id` maps `sub` (M1), but
    application code reads the claim: Fleet's `CarActions` row filter treats an authenticated principal without it as a
    machine, **unrestricted**, so an agent saw every user's cars (red: `totalItems: 2` for a viewer with one car). The
    same read fails closed in Organizations' row policy (SeoTester's sites), notifications and quotas. The three agent
    handlers (personal token, embedded, external) now add the local id as `NameIdentifier` beside `sub`; the external
    scheme adds the *local* id, never the external subject. ~~`IsInRole` stays false for agents (groups come from the
    store, not role claims), so app code that tests roles is stricter for agents than for the web: Fleet's
    administrators' agents see only the cars they created. Parity gap for M7, recorded.~~ *Superseded 2026-10-12: agent
    principals carry the cookie's role and group claims (decision log, "Agent principals carry the cookie's role and
    group claims").*
  - **Bug found: binding `Spark:Mcp` marked `Authentication` explicit.** The configuration binder calls every public
    setter with the getter's value when the key is absent, so any `Spark:Mcp` key (here `CanonicalUrl`) made all three
    setups mandatory (Fleet refused to start without setup 2), and pinned `ToolPrefix` to the default server name's slug.
    `SparkMcpOptions.Create` undoes both for keys the section does not hold. (M6 had found the `Authentication` half in
    parallel; at its rebase this became the one fix, see "M6 implementation".)
  - **Deviation (D6 wording):** `mcp.AgentPermissions` is a `Func`; "a plain value converts implicitly" never compiled
    (`mcp.AgentPermissions = EAgentPermission.Read` is CS0029). The guide now writes `_ => EAgentPermission.Read`.
  - **HR:** `AddExternalAgentAuthorizationServer(<SparkId authority>, loginProvider: "SparkId")` only where the SparkId
    sign-in is configured (Development; the E2E and route-snapshot hosts have none), personal tokens everywhere,
    `Spark:Mcp:CanonicalUrl = https://localhost:5005/mcp` in Development, and `withConnectedAgents()` in the SPA (M4 said
    "account page" but the page was never mounted in HR). **SparkId:** `appsettings.Development.json` lists HR's URL
    (`Spark HR`), no `/mcp` of its own; nothing secret.
  - **Fleet:** references Mcp and the IdentityProvider; `AddMcp` (prefix `fleet`, app `Read`); `Car`, `ServiceEntry` (its
    service rows, AsDetail), `Person` (`Car.Manager`, else hidden by D19) and `Company` (anonymous-readable) are
    `"agentPermissions": "Read"`; `ConfirmDeleteCar` (a dialog) stays `None`. `idp.ForMcp()` only when
    `Spark:IdentityProvider:Issuer` is set (Development: `https://localhost:5003`, canonical `https://localhost:5003/mcp`;
    the signing key is generated in Development, `SigningKeyPath` from secrets elsewhere), so the E2E host is unchanged.
    The provider beside the SparkId sign-in: its login page offers SparkId (test), and its library rights are switched
    off (`"libraries": {"identity-provider": false}`: its management types are not Fleet's to offer; an unbound slot would
    refuse startup). The SPA no longer answers `/connect` and `/.well-known` (an agent probing metadata must get 404, not
    `index.html`). Model hashes and the posture baseline regenerated (posture: the library inert, nothing reachable
    anonymously changed); route snapshot taken with the provider on.
  - **Open for M5/M6 (production apps):** hosting the provider for agents maps its whole surface, not only the MCP
    preset §7 assumed: device authorization, PAR, introspection, the developer-portal and key-admin APIs (Fleet's route
    snapshot). Inert without registrations and rights, but an agents-only mapping would be the tighter design for
    SeoTester and CodeCoverage. *Resolved 2026-10-12: `idp.ForMcp().AgentsOnly()`, and Fleet, CodeCoverage and SeoTester
    use it ("IdP surface for MCP-only apps" below).*
  - **Live run (owner, M7 checklist), Claude Code; not run yet:** RavenDB on `localhost:8080`; Node trusting the dev
    certificate (`dotnet dev-certs https --export-path <file>.pem --format pem`, `NODE_EXTRA_CA_CERTS=<file>.pem`).
    *HR via SparkId:* `dotnet run --project apps/SparkId/SparkId --launch-profile https` (5011) and
    `dotnet run --project apps/HR/HR --launch-profile https` (5005); sign in to HR once with "Spark Identity" (links the
    login; give that HR user a group that reads people); `claude mcp add --transport http hr https://localhost:5005/mcp`,
    then `claude mcp login hr` (or `/mcp` → Authenticate): SparkId's sign-in and consent ("Spark HR (localhost:5005)"),
    `hr_discover`, `hr_query`. Changes: "make changes" at SparkId *and* the toggle under HR → Account → Connected
    applications. *Fleet embedded:* `dotnet run --project apps/Fleet/Fleet --launch-profile https` (5003);
    `claude mcp add --transport http fleet https://localhost:5003/mcp`, `claude mcp login fleet`: Fleet's own sign-in
    (password, passkey or SparkId), its consent (types), `fleet_discover`, `fleet_query` (`cars`). Personal tokens: Account
    → Connected applications in either app, then `--header "Authorization: Bearer <token>"`.
  - **Evidence:** `McpRemoteResourceTests` 17/17 (CIMD and DCR against a provider without `/mcp`, `aud` = the other
    endpoint only and kept on refresh, consent without types, `mcp:write` only with changes, a forged write or type refused,
    resource selection and `invalid_target`, the provider's own `/mcp` and another embedded server in its own database with
    its own key refuse the token, the resource application maps the provider's user through the linked login and 401s an
    unlinked one, URL rules, duplicate and `ForMcp()`-without-Mcp startup refusals); the stamp case in
    `McpExternalAuthorizationServerTests` (30/30); `McpAgentPrincipalTests` 3/3; `McpOptionsBindingTests` 2/2; Fleet's
    `McpEmbeddedServerTests` 2/2 on its real `Program` (metadata document and loopback registration, the SparkId button on
    its sign-in page, an agent reads only the viewer's own car) and its route snapshot; HR's and SparkId's route snapshots
    were stale since M3a/M3b/M4 (`/spark/auth/manage/agents`, `/connect/register`'s rate limit, HR's `/mcp`) and were
    brought up to date; the spec 7/7. Regression: the `Mcp` and `IdentityProvider` namespaces and the library route
    snapshots, 719/719. Gates: `--spark-verify-model`/`-security` green for Fleet, HR, SparkId. Mutation runs (guard
    removed → red, restored → green): `NameIdentifier` off → the three `McpAgentPrincipalTests` (and, before the fix,
    Fleet's car test: `totalItems: 2`); the stamp check off → the stamp test (agent still 200); the re-stamp off → the
    stamp test at "allowing it again"; the remote write cap off → `A_read_only_endpoint_…`; an unknown `resource` falling
    back to the implied endpoint → `The_resource_names_the_endpoint_…`; the own `/mcp` audience check off →
    `A_token_for_the_other_endpoint_is_refused_by_the_providers_own_mcp` (200, the shared OAuth grant makes it reachable);
    the binding reset off → `A_canonical_url_from_configuration_…`; the component's `disconnected` Edit off → the spec case.
- **M5 implementation (2026-10-12).** SeoTester adopts the MCP server (§4.7, D24): commits 2e59bb87 (Mcp), 7de5705f
  (Organizations, Settings), c0aeacdd (SeoTester), 203d52f9 (S7 outcome); 21b01a8c, 6ce592b8, 801ea1ca and 56842722 before
  the rebase onto `feat/spark-mcp` ("M5 follow-ups"). Decisions taken while building, with their evidence:
  - **An application tool declares what it reaches beyond the type layer.** As built in M2 the restriction refused both
    things §4.7 and D24 ask for: `PageResult` is `None` yet "reached through the tools", and `seotester_start_scan` runs
    `StartAudit` on `Site`, whose type is `Read` (a custom action needs `Write` at the type). New, on the tool method, for
    that call only: `[McpReadsThrough("PageResult", "AuditRun")]` judges reading the first type by the second's layers
    (type and the grant's choice), never writing it; `[McpRunsAction("Site", "StartAudit")]` lets that one action need
    `Read` from the type and the grant's choice of types, while `Write` is still required from the app layer and the
    grant's cap (checked first, as for every write); a `ReadOnly` tool declaring an action refuses startup. The generic
    tools keep the type layer (`seotester_get` on a `PageResult` is refused). Chosen over making `PageResult` `Read`
    (contradicts §4.7) or `Site` `Read, Write` (opens generic writes, contradicts D24). Evidence: `McpToolReachTests` (5);
    mutation runs: the read-through branch off → 2 of them and 3 `SeoToolsTests` red; the action branch off → 1 and 2 red.
  - **`ISparkMcpActions`** runs a custom action from an app tool through `ICustomActionExecution`, the web's own sequence
    (right, catalogue, row-gated parent, the row rule for the action, disabled actions, quota gates), mapped to
    Executed/Refused/Invalid/Disabled/Throttled. `seotester_start_scan` is `StartAudit` through it, so quota, cool-down,
    joining and the Member role are `AuditStarter`'s, never a second path (test: the quota refusal is logged by the same
    `AuditStarter`). M4's `apply_changes` may want to share the mapping.
  - **An agent's bearer `sub` is the user in two more packages.** *Superseded at the rebase ("M5 follow-ups"): every
    agent principal now carries `NameIdentifier`, so the fallback was dropped; the tests stay.* `OrganizationRowPolicy`
    and `ISparkQuotas.KeyFor` read only the cookie's `NameIdentifier`: an agent got no row of its own organizations (fails
    closed) and a quota keyed by address. ~~Both fall back to `ISparkCurrentUser.Id` (D4).~~ SeoTester's `StartAudit`, `StartFullCrawl`, `VerifySite` take
    the user from `ISparkCurrentUser` too. Evidence: fallback off → 8 `SeoToolsTests` red ("Not found." for the user's own
    site); `KeyFor` red with `ip:unknown`.
  - **Element evidence: selectors only (S7).** p99 8.9 KB passes; page-result growth +40.7 % with excerpts fails the 25 %
    bound, +20.4 % with selectors only passes ([spark_mcp_spikes.md](spark_mcp_spikes.md) S7). The excerpt also broke
    SeoTester's owner-requested "Nothing crawled is stored" (`docs/seo_analyzer_PRD.md` §12): `CrawledContentTests` found
    its body marker in an axe excerpt; green with selectors only. Selectors carry ids and, from axe, attribute fragments
    (`a[href$="about"]`), never text. ~~**Owner to confirm** that ids in selectors are "facts about a page".~~
    **Confirmed by the owner (2026-10-12):** selectors are accepted ("M5 follow-ups").
  - **`seotester_explain_check` says an unknown rule plainly** instead of D17's uniform 401: the catalogue is public
    (`GET /seotester/catalogue`), so there is nothing to hide and signing in would not help. Sites, scans and pages follow
    D17 exactly (one uniform 401 for anonymous, existing or not; "Not found." for another organization's).
  - **`list_findings` pages over pages**, not findings: `skip`/`take` (≤ 200) count the scan's pages in URL order, so a
    call's cost is bounded whatever the filters; the answer stops before about 38k characters and names `nextSkip`
    (mutation: budget off → the cap test red). The run's totals answer when the pages expired (§4.7 gap 2).
  - **No signing key in configuration.** The IdentityProvider generates its keys into `OidcKeys`, protected with Data
    Protection; SeoTester's issuer defaults to `Spark:Auth:PublicBaseUrl` and its canonical URL to that + `/mcp`, so
    production needs no new secret (deploy.md §4.4: the Data Protection volume becomes critical). Development uses the
    package's mechanism (issuer = request origin) and `Spark:Mcp:CanonicalUrl` `http://localhost:5014/mcp`.
  - **The identity provider's portal rights are off in SeoTester** (`"libraries": { "identity-provider": false }`): it is
    only the agents' authorization server. Its routes are still mapped (42 new routes in the snapshot, including
    `/spark/identity-provider/*` and `/connect/register` with an initial access token), now without any right behind them.
    **Owner decision:** whether `ForMcp()` should map only the agent endpoints.
  - **Prompts:** `mcp.AddPrompts<T>()`, ~~unprefixed (clients namespace prompts per server); SeoTester's
    `fix_seo_findings`~~ (*superseded at the rebase: one prompt API, M6's, prefixed: `seotester_fix_seo_findings`*). A `List<Type>` bound to the SDK's `WithPrompts<T>(T target)` overload and registered nothing
    (`prompts/list` "not available"): passed as `IEnumerable<Type>`.
  - **SeoTester's SPA mounts `withConnectedAgents()`**: without it a user could not create a personal access token or
    revoke an agent there.
  - **S6 prepared, not run:** fixture site `apps/SeoTester/tools/s6-injection-site/`, a server-side dry run (every
    injected instruction stayed inside the fence) and the runbook in [spark_mcp_spikes.md](spark_mcp_spikes.md).
  - **For M6:** if CodeCoverage's per-file coverage documents are model types, its tools need `[McpReadsThrough]` too
    (*answered in "M5 follow-ups": not needed*).
    **For the PR:** the per-project version gate needs bumps for ~~Organizations and Settings (and~~ Mcp, Spark if not yet
    (*Organizations and Settings are unchanged since the rebase*).
- **M6 merged into `feat/spark-mcp` (2026-10-12).** Rebased onto the demo-app commits, then fast-forwarded. One conflict
  in code: both sides had fixed the `Spark:Mcp` binding bug; the demo-app side's `SparkMcpOptions.Create` stays (it also
  un-pins `ToolPrefix`), M6's own line in `AddMcp` went, both sides' tests stay ("M6 implementation" amended). The guide's
  paragraph on identifying the caller merges both: `ISparkCurrentUser.Id` first, `NameIdentifier` on every agent
  principal. The M6 entry above is placed by its date, before "Demo apps", and its commit ids are the rebased ones.
  Evidence: the solution builds; the Spark `Mcp` namespace 146/146 on the rebased tree; with the role-parity change
  below on top, CodeCoverage's `Mcp` classes 27/27, its registration inventory 6/6 and its route snapshot.
- **Agent principals carry the cookie's role and group claims (2026-10-12).** The gap "Demo apps" recorded: an agent's
  principal had no role claims, so `User.IsInRole` was false for every agent, and a Fleet administrator's agent saw only
  the cars the administrator created while the administrator's cookie saw every car (`CarActions.cs:28`). Requested
  2026-10-12: the same role and group claims a cookie sign-in of the user carries, from the store per request (S4), for
  every agent scheme, reusing how the cookie gets them. Decisions, with their evidence:
  - **How the cookie gets them:** ASP.NET Identity's `UserClaimsPrincipalFactory<TUser, SparkRole>`, registered by
    `AddIdentityApiEndpoints<TUser>().AddRoles<SparkRole>()` (`SparkAuthenticationExtensions.cs:60-64`); Spark has no
    factory of its own. It adds a role claim per `SparkUser.Roles` entry (type `IdentityOptions.ClaimsIdentity.RoleClaimType`,
    default `ClaimTypes.Role`), the claims of those roles, and the claims stored on the user.
  - **Reused, not re-mapped:** `IAgentRoleClaimsProvider` (`Agents/AgentRoleClaims.cs`), registered with
    `AddAuthentication<TUser>` beside `IAgentLinkedLogins`, runs the application's `IUserClaimsPrincipalFactory<TUser>` (an
    application's own factory counts too) on the user the scheme loaded for its token check (`AgentRequestUser`; a
    `TUser` subtype is loaded as itself through `UserManager`), and keeps the claims of the cookie's role claim type and
    of a group type (core's list, `AgentStoreGroupMembership.GroupClaimTypes`). The personal-token, embedded and external
    handlers add them and build their identity with the cookie's role claim type; it was `"role"`, a type no agent claim
    had.
  - **Per request:** each scheme runs once per request and nothing caches across requests, so a removed role is gone on
    the agent's next call; `RemoveFromRoleAsync` changes no security stamp, so the agent stays signed in. The cookie keeps
    its claims until Identity revalidates it (§4.6): the agent is the stricter side, as for groups.
  - **Role and group claims only,** never the cookie's other claims (email, security stamp, the user's other stored
    claims): a stored claim named like a scheme's own would sit beside it, and `scope` is read off the agent's principal
    as D22's write cap (`ExternalAgentServer.cs:69`). Mutation (the filter removed, every cookie claim copied):
    `An_external_servers_token_carries_the_cookies_roles_and_none_of_the_users_other_claims` red, the agent's `scope`
    read `mcp:read mcp:write` for a token issued `mcp:read`; the class's other five tests stayed green.
  - **Groups unchanged:** `AgentStoreGroupMembership` stays. Composition is a union, an application may replace core's
    claims provider, and that provider reads `ClaimTypes.Role`, not a custom role claim type; with Identity's default
    both answer the same groups.
  - **Observation, not changed:** `UserManager.AddToRoleAsync` hands the store the normalized name and Spark's
    `UserStore` keeps it as given (`UserStore.cs:311-317`), so a role added that way reads `DEVELOPERS` (the red run's
    cookie claims; Fleet's log: `groups: [VIEWERS, Administrators]` for a user given `Viewers` that way and
    `Administrators` on the document), and `IsInRole("Developers")`, an ordinal comparison, is false for cookie and
    agent alike. Fleet's administrators hold the role as written on the document (the E2E seed,
    `SparkAppTestHost.cs:341`), which Fleet's test copies. Parity holds either way. *Superseded: fixed in "Role names
    keep their declared casing" below; Fleet's test adds the role through `UserManager` now.*
  - **Application audit:** the only role read in application code is Fleet's `CarActions.CurrentUserIsAdmin`. SeoTester's
    roles are organization memberships read from the store, CodeCoverage's administrators come from linked forge logins
    (`CoverageAdministrators`, by `NameIdentifier`), HR reads no claims. The `NameIdentifier` reads (SeoTester's
    `ReportPdf`, `SiteScheduleInterceptor`, `SeoInterceptors`, `SiteActions`, `CancelAuditAction`; CodeCoverage's
    `CoverageAdministrators`; Fleet's `CarActions`) are served since "Demo apps" added the claim.
  - **Evidence, red → green:** `McpAgentPrincipalTests` (a personal token, an embedded server's token, an external
    server's token: the claims equal the cookie's from Identity's factory, `IsInRole` alike, the role removed → gone on the
    next call, a stored `scope` claim never copied); red: `"inrole=False;claims="` against
    `"inrole=True;claims=group=Reviewers|…/role=DEVELOPERS"`. Fleet's
    `An_administrators_agent_sees_the_cars_the_cookie_sees_until_the_role_is_removed` (real `Program`, embedded server):
    red: the cookie's query returned both cars, the agent's `totalItems: 1`. Green: `McpAgentPrincipalTests` 6/6,
    Fleet's `McpEmbeddedServerTests` 3/3. Regression: the Spark `Mcp` namespace 149/149, `IdentityProvider` 565/565 and
    the library route snapshots 35/35 (749/749, after the race below; 748/749 before it); CodeCoverage's `Mcp` classes,
    registration inventory and route snapshot 34/34; the Fleet, HR, SparkId, SeoTester, DemoApp and QnA route
    snapshots; the solution builds, 0 errors.
- **`McpEmbeddedAuthorizationServerTests.Reusing_a_refresh_token_revokes_the_whole_family` raced (2026-10-12).** Run
  beside the `IdentityProvider` namespace, the successor refresh token still refreshed after the replay (200 where 400
  was expected); it had passed in the `Mcp` namespace alone (146/146 above). `Token.RevokeAuthorizationChainAsync` finds
  the chain through an index, best-effort by design (its own remarks), and the class's shared database gives that index
  every earlier case's tokens to catch up on: the race OidcTokenSecurity's two chain-revocation cases showed
  (contributions PRD, M8 item 11). Fixed the same way: the case waits for indexing before the replay, pinning what it
  tests (a chain the index can see is torn down); the production code is unchanged. Evidence: the same filter (`Mcp`,
  `IdentityProvider`, library route snapshots) 748/749 before the wait, 749/749 after it. *Superseded 2026-10-12 by
  "Refresh-token families exact" below: the family's document decides, loaded by id, and the case no longer waits.*
- **IdP surface for MCP-only apps (2026-10-12).** Owner decision of 2026-10-12: apps that host the identity provider only to
  sign agents in (Fleet now; SeoTester and CodeCoverage in M5/M6, all with `ForMcp()`) expose only what agents need, and the
  provider's registration gives developers granular control over flows and endpoints; a full provider like SparkId keeps
  everything. Commits 702f1deb (flows, endpoints, `AgentsOnly()`, Fleet), 52a34d2c (the disable cascade), bbd2609d
  (tests), rebased onto `feat/spark-mcp` at ca0577b0: M5, the M6 merge and agent role parity above, the role-name casing
  fix and the M5 follow-ups below (made after this entry, merged first). Decisions taken while building, with their
  evidence:
  - **The API: two flags enums on the options, defaults `All`, and one preset.** `idp.Flows` (`SparkIdentityProviderFlows`:
    `AuthorizationCode`, `RefreshToken`, `ClientCredentials`, `DeviceCode`, `TokenExchange`) and `idp.Endpoints`
    (`SparkIdentityProviderEndpoints`: `UserInfo`, `EndSession`, `Introspection`, `Revocation`, `PushedAuthorization`,
    `DeveloperRegistration`, `DeveloperPortal`, `ConnectedApplications`, `Administration`), bindable as
    `Spark:IdentityProvider:Flows`/`Endpoints` ("AuthorizationCode, RefreshToken"). `idp.ForMcp().AgentsOnly()` sets
    `AuthorizationCode | RefreshToken` and `Revocation`. Flags over a fluent `Flows(f => …)` builder because the options are
    bound from configuration first, as everywhere in Spark (`SparkSignInIdentifiers` is the precedent), and a lambda builder
    cannot be. Two enums, not one: grant types and endpoints are different questions with different failure modes
    (`unsupported_grant_type` against 404). The defaults are today's surface, so SparkId and every existing provider are
    unchanged: the library route snapshots (`auth-*-all-options`), SparkId's snapshot and
    `The_default_provider_keeps_every_flow_and_endpoint` are green without a fixture change.
  - **One decision, read in three places.** `OidcSurface` (internal singleton, built once in `AddIdentityProvider`) answers
    every endpoint's `IEndpointBase.IsEnabled` (`MintPlayer.AspNetCore.Endpoints` 11.4: evaluated once at map time, also for
    the user-generic endpoints `MapEndpoint<T>` closes), the token endpoint's grant switch, and both metadata documents, so
    a route that is not mapped is never advertised and a grant that is not advertised is never honoured. Without
    `RefreshToken` no refresh token is issued at all (authorization code and device grants), not just refused later. The
    flows' pages follow their flow: authorize and consent with `AuthorizationCode`, the device pages with `DeviceCode`, the
    sign-in, two-factor and error pages with either.
  - **Registration splits by who registers.** `DeveloperRegistration` is RFC 7591 with an initial access token plus RFC 7592
    management; agents' anonymous loopback registration stays the agent server's own switch (`DynamicRegistration`), so
    `AgentsOnly()` keeps `POST /connect/register` for agents while a developer's valid initial access token there answers
    401 (test seeds one through `OidcClientRegistration.IssueInitialAccessTokenAsync` for an approved developer).
    RFC 7592 is unmapped: anonymous registrations get no registration access token.
  - **Startup refuses what cannot work, naming the dependency** (`OidcSurface.For`, every broken rule listed):
    `RefreshToken`, `UserInfo`, `EndSession`, `ConnectedApplications` need `AuthorizationCode` or `DeviceCode`;
    `PushedAuthorization` needs `AuthorizationCode`; `DeveloperRegistration` needs `DeveloperPortal` (the initial access tokens
    come from `POST /spark/identity-provider/developer/registration-token`); `ForMcp()`/`ForMcpResource()` need
    `AuthorizationCode`; `Flows = None` is refused. **`ForMcpResource()` needs `UserInfo`**: D22 makes the provider that
    application's sign-in (its users link their login here), and Spark's OpenID Connect sign-in sets
    `GetClaimsFromUserInfoEndpoint = true` [verified `SparkExternalProviderExtensions.cs:244`]. So `AgentsOnly()` beside
    `ForMcpResource` refuses to start instead of breaking that application's sign-in. End-session is not required there:
    Spark's logout signs out the application cookie only [verified `Authorization/Endpoints/Logout.cs:20`].
  - **What agents need, and why userinfo and end-session are out.** Kept: the OpenID discovery document beside RFC 8414
    (the MCP authorization spec has clients try RFC 8414, then OpenID discovery; rmcp's order is exactly that
    [verified, `generate_discovery_urls` in modelcontextprotocol/rust-sdk `crates/rmcp/src/transport/auth.rs`, read
    2026-10-12]), JWKS, authorize with the sign-in, two-factor, consent and error pages, the token endpoint for code and
    refresh, registration, revocation (RFC 7009; public clients may revoke their own tokens). Left out, because no MCP client
    calls them: userinfo and end-session [source search 2026-10-12: no `userinfo_endpoint`/`end_session_endpoint` use in
    openai/codex or modelcontextprotocol/rust-sdk; Gemini CLI declares `revocation_endpoint` only in its metadata type
    (`packages/core/src/mcp/oauth-utils.ts`) and calls none of them; VS Code's `extHostAuthentication.ts` reads only the
    authorization, token and registration endpoints and takes the account label from the token's claims; Claude Code is
    closed source, so [inferred] from S3: it requests the advertised `mcp:*` scopes plus `offline_access`]. Agent tokens never
    carry `openid` (the RFC 8414 `scopes_supported` is `mcp:read mcp:write offline_access`; S3: Codex and Gemini request what
    is advertised), so there is no OpenID sign-in to describe or end. Also out: device grant, client credentials, token
    exchange, PAR, introspection, developer registration and portal, the provider's connected-applications pages (the user
    manages agents on the application's account page, which the provider's `ISparkConnectedApplicationSource` still feeds),
    key administration.
  - **The consent pages' links follow the surface.** "Not you?" pointed at `/connect/logout` and "manage" at
    `/connect/applications`; on a provider without them the links would 404, so they are rendered only where mapped. In
    setup 3 the provider is the application, whose own sign-out serves the wrong-account case.
  - **Fleet** uses `idp.ForMcp().AgentsOnly()`; its route snapshot lost 22 routes (device ×3, PAR, introspection,
    userinfo ×2, logout, `/connect/applications` ×3, RFC 7592 ×3, portal ×8 incl. admin ×2) and nothing else changed.
    SeoTester's and CodeCoverage's, which M5 and M6 added with `ForMcp()` before this work was rebased onto them, move
    to `AgentsOnly()` next. *Done: the next two bullets.*
  - **CodeCoverage** (production, coverage.mintplayer.com) uses `idp.ForMcp().AgentsOnly()` since f931bf4c, made after
    the rebase onto M6. Its route snapshot lost exactly the 22 routes Fleet lost and nothing else (22 deletions, no
    addition). Red first: `CoverageMcpSignInTests.CodeCoverage_maps_only_what_its_agents_need` (Fleet's case on CodeCoverage's
    real `Program`) failed with the routes answering 401, 302, 200 or 400 instead of 404. The app needed nothing else: its
    SPA fallback already skips `/connect`, `/.well-known` and `/spark`, its management types were already off, and its
    agents' surface (`CoverageMcpSignInTests`' registration, authorize and sign-in cases) is what `AgentsOnly()` keeps.
    `docs/code-coverage/hosting.md` lists the surface for M9.
  - **SeoTester** (production, seotester.mintplayer.com) uses it since 757c5fb7, made after the rebase onto M5, as
    CodeCoverage does: the same 22 routes left its snapshot and nothing else. One more change was needed: its SPA fallback
    skipped only `/spark`, so an unmapped `/connect`, `/.well-known` or `/mcp` path reached the SPA instead of answering
    404; it now skips all three, as CodeCoverage's and Fleet's do. Mutation (the old fallback restored, `AgentsOnly()`
    kept): the surface case failed at the first unmapped `/connect` path, the SPA's default-page middleware throwing
    because the test host builds no SPA; production would have served `index.html`. Red
    first: `AgentAuthorizationServerTests.SeoTester_maps_only_what_its_agents_need` failed with the routes answering 401,
    302, 200, 400 or 415 instead of 404; its companion (an agent registers itself and is sent to the website's sign-in)
    passed before and pins the agents' path. The rate limiter M5 added still covers what stays (token, revocation,
    registration: `RateLimitTests`). `docs/seotester/deploy.md` §4.4 lists the surface for M9. Both apps green on the
    final tree: the numbers closing "Refresh-token families exact" below.
  - **Bug found: the disable cascade gave up on a stale index.** `OidcDisableCascade` sent its patch-by-query without a
    `StaleTimeout`, and RavenDB then refuses at once when the index is stale (`QueryOperationOptions.StaleTimeout`: "null
    by default - throw immediately if index is stale" [verified, Raven.Client 7.2.6 docs]): a token issued moments before
    an application was disabled, or an agent client pruned, left the patch undone and the exception escaping. Found as
    `McpEmbeddedAuthorizationServerTests.An_unused_dynamic_registration_is_pruned_with_its_grants` failing with "Cannot
    perform bulk operation. Index is stale." in the batched run (it passed when the class ran alone). The patch now waits
    for the index, bounded by the cascade's existing 30 s. Red → green:
    `OidcOperationsTests.Disabling_revokes_a_token_its_index_has_not_caught_up_with` holds indexing back for a second; it
    failed with exactly that exception without the timeout and passes with it.
  - **Test race: `Reusing_a_refresh_token_revokes_the_whole_family`, the one recorded above.** The family sweep reads an
    auto-index, best-effort by design (`Token.cs`, `RevokeAuthorizationChainAsync`'s remarks). When the class's earlier
    cases had already created that index (`OidcGrantWithdrawal` queries the same two fields), the replay, one request after
    the rotation, ran before the index held the rotated token. It failed in 2 of 2 class runs with the surface change,
    passed alone and in 1 class run on the base; the surface change does not touch that path for a full provider. This
    work fixed it as `feat/spark-mcp` did in 19a0b123 (a wait for indexing before the replay, as the provider's own
    `Reused_refresh_token_is_refused_and_revokes_the_chain` does); the rebase kept that one fix and its comment.
    **Open for the owner:** RFC 9700 §4.14.2 expects the active refresh token revoked. A bounded wait in the sweep would
    make that exact; every caller of the sweep is refusing the request anyway, so the wait would delay no token.
    *Resolved 2026-10-12 (owner): exact without a wait, by a family document loaded by id ("Refresh-token families exact"
    below); the case's wait is gone.*
  - **Evidence:** red first. `McpAgentsOnlyProviderTests` and `OidcSurfaceTests` against the API with nothing reading it: 15 of
    17 failed for the intended reasons (the routes answered 401/302/200 instead of 404, the documents listed 5 grant types,
    other grants answered `invalid_client`, no startup refusal, the consent links present, a developer's initial access
    token registered a client: 201). The two that passed guard what must not change: the default provider keeps every
    flow and endpoint, and the configuration binding. Fleet's `McpEmbeddedServerTests` before `AgentsOnly()`: 2 of 3 failed
    (the 404s; the consent links), and the registration case passed. Then green: the `IdentityProvider` namespace, the
    `Mcp` classes using the embedded and remote hosts (`McpAgentsOnlyProviderTests`, `McpEmbeddedAuthorizationServerTests`,
    `McpRemoteResourceTests`, `McpAgentPrincipalTests`, `McpSignInChallengeTests`) and the library route snapshots, 638/638.
    Fleet's `McpEmbeddedServerTests` and route snapshot: 4/4. SparkId's route snapshot: 1/1, fixture unchanged. The
    solution builds with 0 errors. Mutation runs (guard removed → red, restored → green): refresh tokens issued without the
    flow → `Without_the_refresh_flow_…` (a refresh token came back); the developer gate in `/connect/register` off →
    `An_agent_registers_anonymously_…` (201 Created); userinfo always advertised →
    `Its_metadata_documents_advertise_only_what_it_maps`.
- **Role names keep their declared casing (2026-10-12, found along the way).** The observation above, fixed. Decisions,
  with their evidence:
  - **Root cause:** `UserManager.AddToRoleAsync` normalizes the name before it calls `IUserRoleStore.AddToRoleAsync`
    (Identity's contract: the store receives `normalizedRoleName`), and Spark's `UserStore` appended it as given
    (`UserStore.cs:311-317` before). `GetRolesAsync` returned the stored strings, Identity's claims factory made each a
    role claim, and `ClaimsPrincipal.IsInRole` compares claim values ordinally: `IsInRole("Administrators")` was false for
    the cookie and for every agent (`AgentRoleClaimsProvider` runs the same factory). Spark's own authorization never
    noticed: core reads role claims as group names (`ClaimsGroupMembershipProvider.cs:19-26`) and matches them to
    `security.json` case-insensitively (`SecurityFileAccessControl.cs:164-165`; `[SparkAuthorize(Group)]` likewise).
  - **The registry is `security.json`.** Identity's role store is registered (`AddRoles<SparkRole>()`, `RoleStore`,
    `SparkAuthenticationExtensions.cs:64,73`), but nothing in `libs/` or `apps/` creates a `SparkRole` (no `RoleManager`
    use) and the user store never consulted one: a role is a group name. So `UserStore` resolves the declared name from
    core's `ISecurityConfigurationLoader` (an optional injection; the first case-insensitive match, as core resolves a
    role claim's group) and stores nothing more. `AddToRoleAsync` stores the declared name; `GetRolesAsync` maps every
    stored value to it (case duplicates dropped), so a document holding `ADMINISTRATORS` (written by `UserManager`
    before) or `administrators` (patched by hand) answers by `Administrators`, and nothing is rewritten. A name no group
    declares keeps what the store receives, Identity's normalized form: the store never sees the caller's casing.
  - **Rejected:** a `UserManager` subclass handing the store the caller's casing (re-implements Identity's
    `AddToRoleAsync`/`AddToRolesAsync`, still fails `IsInRole("Administrators")` for a caller who wrote
    `administrators`, and does nothing for documents already written); a migration rewriting stored roles (touches
    production documents, where read-side compatibility was required).
  - **Membership ignores case**, unchanged: `IsInRoleAsync` and `RemoveFromRoleAsync` already compared
    `OrdinalIgnoreCase`, and `GetUsersInRoleAsync`'s RavenDB equality on the auto-index already matched any casing
    (pinned below, green before the fix).
  - **Callers:** no production code calls `AddToRoleAsync`, `RemoveFromRoleAsync` or `GetUsersInRoleAsync` (tests only).
    `GetRolesAsync` feeds the cookie, every agent principal (personal tokens, embedded and external servers, through
    `AgentRoleClaimsProvider`) and `/spark/auth/me` (`GetCurrentUser.cs:43`). Readers of the stored list itself:
    `AgentStoreGroupMembership.cs:49` (group names, matched case-insensitively), the personal-data export
    (`PersonalData.cs:54`, as stored) and the identity provider's `role` claim (`OidcTokenGenerator.cs:281,365`), which
    emits stored values: declared names for every role `UserManager` adds from now on.
  - **Production data:** CodeCoverage writes no roles (its Administrators come from linked forge logins,
    `CoverageAdministrators`); SeoTester's are patched in as `Administrators` (`docs/seotester/deploy.md:277`), the
    declared name. Every casing reads by the declared name, so nothing needs migrating.
  - **Evidence, red → green:** `UserRoleNameTests` (new: real Identity, a cookie sign-in through `/spark/auth/login`, an
    endpoint answering `User.IsInRole`): red `found "ADMINISTRATORS" instead of "Administrators"` after
    `AddToRoleAsync(user, "administrators")`, and `"out"` for `IsInRole("Viewers")` on a document holding `viewers`; the
    removal (three stored casings) and `GetUsersInRole` (three stored × three asked casings) cases green before and
    after. `UserStoreTests.A_role_is_kept_and_read_by_the_name_its_group_is_declared_under`: red, `found
    "ADMINISTRATORS"`. `McpAgentPrincipalTests` asks `IsInRole("Developers")`, the declared name, no longer the stored
    one: red ×3, `inrole=False;…/role=DEVELOPERS`. Fleet's `An_administrators_agent_sees_…` adds the role through
    `UserManager` (the document workaround removed): red, the administrator's cookie saw one car. Mutation (the
    read-side mapping removed): the document case red (`"out"`), the store case red. Green: the `Authorization.Identity`
    namespace and `McpAgentPrincipalTests` 141/141, Fleet's `McpEmbeddedServerTests` 3/3; the solution builds, 0 errors.
- **M5 follow-ups (2026-10-12).** M5 rebased onto `feat/spark-mcp` (402e1934: M4 writes, the demo apps, M6, agent role parity) and the
  owner's decisions of 2026-10-12 on it. Commits e2020f3b (reconciliation), 2028b83e (pages, selectors),
  fdcfdcf9 (rate limiting) and this documentation. Decisions, with their evidence:
  - **One prompt API, M6's.** M5 and M6 each added `mcp.AddPrompts<T>()`: M5's unprefixed through the SDK's
    `WithPrompts`, M6's prefixed like the tools through `McpPromptRegistration`, with a duplicate name refused at startup.
    M6's is kept: D21's prefix keeps two Spark servers in one session apart, which holds for everything a server lists,
    not only its tools. SeoTester's prompt is now `seotester_fix_seo_findings` (Claude Code:
    `/mcp__seotester__seotester_fix_seo_findings`); M5's workaround for the SDK's `List<Type>` overload went with
    `WithPrompts`. Evidence: `SeoToolsTests.The_fix_prompt_is_offered_…` lists and gets it by the prefixed name;
    CodeCoverage's prompt test is unchanged.
  - **`SparkMcpResult`**: M4's `Serialize`, M6's public `Invalid` and `Track`, and M5's `Json(note, value)` are all kept.
    The two `Json` overloads share one `FencedJson` (serialize, cut at 40k characters, fence) instead of two copies.
  - **Agent identity in Organizations and Settings: the fallback is dropped, the tests stay.** M5 made
    `OrganizationRowPolicy` and `ISparkQuotas.KeyFor` fall back to `ISparkCurrentUser.Id`, because agent principals
    carried the user only as `sub`. The demo-app work then put `NameIdentifier` on every agent principal (8a43aea2
    embedded server, cb543b7b personal tokens and external server; `McpAgentPrincipalTests`), which both packages read. The
    fallback became dead code, and both files are back to `feat/spark-mcp`'s.
    `SparkQuotaTests.An_agents_bearer_caller_spends_its_users_quota_not_one_per_address` now sends a real personal access
    token through `/mcp` instead of a hand-made `sub`-only principal, which no setup produces any more. Mutation: with
    `NameIdentifier` off the token's principal it is red (`key=ip:unknown`). `SeoToolsTests.An_agent_sees_its_users_sites_…`
    passes without the fallback.
  - **CodeCoverage's tools do not use `[McpReadsThrough]`.** The attribute changes what `IPermissionService` answers for a
    type while the tool runs. CodeCoverage's tools ask it only about `Repository` and `Commit` (both `Read` for agents) and
    read file coverage and builds through `ICoverageInsights`, below `IRepositoryScope`'s gate. `FileCoverage` is not a
    model type; `Build` is, but no check asks about it. Declaring the attribute would change nothing, so the tools stay as
    M6 built them. The guide now says when the attributes apply.
  - **(a) Selectors are accepted (owner).** SeoTester's rule now reads "no crawled text or markup is stored; element
    selectors are kept with the latest page result" (`docs/seo_analyzer_PRD.md` §12, `docs/seotester/mcp.md`,
    `ElementEvidence`). `CrawledContentTests.No_text_or_markup_of_a_crawled_and_rendered_page_is_stored_only_selectors` still
    keeps the body marker (text, comment, attribute, script, script-written text) out of the whole export. A second
    marker is the id of a heading that `seo.single-h1` flags. It must be stored as the selector `#…` (the control), and
    with every stored selector string taken out of the export it must appear nowhere. Mutation: the first heading's outer HTML stored as the page's `H1` turned it red, the marker found in
    `"H1":"<h1 id=…` outside any selector.
  - **(b) One result per page and site, the latest (owner).** `PageResult` ids are `{siteId}/pages/{hash}`.
    `PageResult.OwnerOf` uses the site, never the origin: one origin can be a site of several organizations, and the page
    is read under its site's organization. A scan's fetch replaces the earlier scan's result, the 7-day expiry counts from
    that fetch, and `RunId` names the scan that wrote it. What read per-run page results (inventory of 2026-10-12), and how
    each is kept:
    - *Aggregate (scores, per-check totals, samples) and the render and PageSpeed samples* read the pages the run stored
      (`CrawlFrontier.Stored`, by the site's prefix), never one an earlier scan left. Two scans of one site can run at once
      (a full crawl during an audit of a verified site, or two starts that race; `AuditStarter`) and write the same
      documents. A page the run stored therefore counts for it whoever wrote it last. The run's render and PageSpeed
      findings are scored from the frontier (`SampledFindings`); on the page they replace the same check's finding instead
      of adding a second one (a constant patch script, the values as arguments: Raven's typed patch could not translate
      the list of checks). Evidence: `CrawlPipelineTests.A_new_scan_replaces_each_pages_result_and_counts_only_the_pages_it_fetched`
      was red on per-run ids (0 pages under the site). Mutation "every page of the site": the second scan scored
      `seo.duplicate-titles` Fail from the first scan's `/b`. `RunPagesTests` (2), mutations: selecting by `RunId` only
      lost the page a concurrent scan fetched again; appending findings left two `accessibility.color-contrast` on the page.
    - *New/Fixed/Unchanged, alerts, the trend, the PDF report, share links, `run_summary` and `scan_status`* use AuditRun
      fields only (the delta compares `CheckResults`, computed at scan time) and are unaffected. There is no CSV export or
      side-by-side comparison.
    - *The run page's page list* (`RunPages`, `RunId == run`) now holds the pages whose latest result is the run's. The
      page says so (`seoUi.pagesLatestOnly`) until the 7-day note replaces it; the later scan's run lists the pages it
      checked again.
    - *MCP:* `seotester_get_page` answers the page's one result with `page.scan`, and names the scan that wrote it when it
      is another. `seotester_list_findings` lists the run's pages and says how many a later scan checked again and which
      scan. With none left it answers the run's totals, never an empty list that reads as clean (§4.7 gap 2). Evidence:
      `SeoToolsTests.A_page_a_later_scan_checked_again_…` and `A_scan_whose_pages_a_later_scan_checked_again_…` were both
      red before ("This scan has no page with that URL", and the clean "None of the pages read has a matching finding").
    - *og:image proxy* (by page id) is unaffected.

    **No migration** (owner, 2026-10-12: only the owner used production in the past 3 days, and no important data is
    stored there). There is no compatibility code: per-run results written before the change are ignored and expire
    within 7 days.
  - **(c) Rate limiting.** SeoTester calls `spark.AddRateLimiter()`: 150 requests per 10 s per address on `/spark` and
    `/connect`, at the BeforeAuthentication stage, with no second `UseRateLimiter`. The identity provider's endpoint
    budgets now act. `RateLimitTests` (own host, provider budget 3): the 4th empty `POST /connect/register` answers 429;
    without the call it answered 400 (red). An empty registration is refused for its redirect URIs before the provider's
    always-on registration budget is spent, so only the policy can answer 429. The shared test hosts raise both budgets,
    because every test request is one partition (the E2E host does the same). In production the partition is the
    client's address: Spark core reads it from Traefik's `X-Forwarded-For`.
  - **(d)** `docs/guide-mcp.md`, "Reach past the type layer: `[McpReadsThrough]` and `[McpRunsAction]`" (under "Write your
    own tools"). **(e)** This entry; `docs/seotester/mcp.md` (latest result per page, selectors, the prompt's name),
    `README.md`, `deploy.md` §4.4 (rate limiting).
  - **Tests** (on 402e1934): Spark.Tests 223/223 (220/220 on a0c25c6d; the `Mcp` namespace, `Organizations`, `SparkQuotaTests`, the route snapshot,
    `SparkCredentialInventoryTests`); CodeCoverage.Tests 34/34 (`Mcp`, the route snapshot, `RegistrationInventoryTests`);
    SeoTester.Tests 96/96 (`Mcp`, `Crawler`, the route snapshot, `CompositionTests`, `RateLimitTests`,
    `CrawledContentTests`, `RenderPipelineTests`, `PageChecksTests`, `RenderAndPsiChecksTests`). The first SeoTester run
    was 95/96: the new pipeline test's verified site linked the fixed `external.example.org`, so the verified-site test,
    which shares the collection's fake web, counted two HEAD checks. `BuildSite` now takes the external host and the new
    test owns one. After the mutation runs (sources restored): `SparkQuotaTests` and `McpAgentPrincipalTests` 10/10, the
    SeoTester set 96/96 again. The SeoTester client's templates type-check (`ngc -p ClientApp/tsconfig.app.json --noEmit`,
    `rootDir` widened to the repository, exit 0). Not run: the SPA spec and the full suites.
- **Refresh-token families exact (2026-10-12).** Owner decision of 2026-10-12, resolving the open point of "IdP surface for
  MCP-only apps" above: RFC 9700 §4.14.2 has the provider revoke the active refresh token when a rotated one comes back,
  and that must hold without depending on a possibly stale index and without a wait. Commits 611e3cd4 (the family
  document), f7c89f3a (tests). Decisions, with their evidence:
  - **A family document decides, loaded by id.** `OidcRefreshFamilies/<SHA-256 of the code's or device code's document
    id>` (`OidcRefreshFamily`: status, the generation of the active refresh token, `@expires` at the latest expiry of its
    tokens) is stored in the batch that redeems a code or device code for a refresh token. Every refresh token carries
    `FamilyId` and its `Generation`; every access token issued with one carries `FamilyId`, and a token exchanged from such
    an access token inherits it. A refresh is honoured only for the family's active token (valid, at the family's
    generation, the family valid). Access tokens (introspection, userinfo, `/mcp`) and refresh-token introspection consult
    the family by id beside the grant (`AccessTokens.ResolveAsync`, `FamilyPermits`; the record's load includes the
    family). The id derives from the code, so a code presented again finds the family it started with nothing stored on
    the code.
  - **A rotation is a compare-and-swap on the family.** It advances the generation in the batch that spends the token, so
    under that session's optimistic concurrency two rotations of one token cannot both commit, and a rotation that read the
    family before a revocation committed fails.
  - **A revocation cannot lose.** A reuse (a rotated token presented again, or a valid one not at its family's
    generation), the second spend of one refresh token or code at once (the loser of the race re-reads the token and finds
    it `redeemed`), and a code presented again revoke the family in a batch of their own that checks no change vector
    (`Token.RevokeFamilyAsync`). This deviates from the owner's sketch, which had the reuse mark the family "in the same
    session with optimistic concurrency": an optimistic revocation can lose to a rotation committed between its read and
    its write, and a retry can lose again, so exactness would hang on timing. The unconditional write wins against a
    rotation committed after it read the family; a rotation that read the family before it fails on its own change vector;
    revoked is final, so overwriting a rotation's generation loses nothing. The reuse audit event (D9) commits with the
    revocation, so it lands even when the sweep's bookkeeping loses a race (before, it rode that batch).
  - **The index sweep is cleanup.** ~~`RevokeAuthorizationChainAsync` still marks the authorization's valid tokens
    revoked through its index, best-effort, after the family is revoked; nothing depends on it. It also still reaches the
    authorization's other families (another device of the same user and client) when its index holds them, as before.
    Exactness covers the family the signal came from, which is what RFC 9700 §4.14.2 and RFC 6749 §4.1.2 ask. *Open for
    the owner: narrow the sweep to the family, or keep its wider best-effort reach.*~~ *Superseded the same day, below.*
  - **A reuse revokes exactly its family, nothing else of the grant** (owner decision 2026-10-12, answering the open
    point above: "narrow it"). One user often runs one agent client on several machines: the same metadata-document
    client id, so the same grant, and a family per machine. A revocation that reaches another family only when an index
    happens to hold its tokens is worse than none, and the user can revoke everything under Connected applications. So a
    reuse and a code presented again or spent twice, and by the same reasoning (Claude, technical) a refresh left with no
    scope (`NoScopeLeft`: the grant's other families may still carry available scopes), revoke their family and sweep
    nothing of the grant: the bookkeeping after
    the revocation (`Token.SweepFamilyAsync`) marks the presented credential and, best-effort, the family's own tokens the
    grant's index already holds (filtered by `FamilyId` in memory, so no new index). `RevokeAuthorizationChainAsync` now
    serves only a withdrawn grant, whose tokens the grant link refuses anyway: cleanup of dead documents. Red first:
    `A_reuse_revokes_its_family_and_leaves_the_grants_other_families_working` (two families under one grant, indexing on
    and caught up) failed with family B's access token inactive (the grant sweep had marked it revoked); green after.
    Regression: the `IdentityProvider` and `Mcp` namespaces 744/744.
  - **Revoking the grant ends its families through the link,** unchanged: every redemption and every access-token check
    loads the `OidcGrant` (`OidcGrants.PermitsAsync`: status and `LastRevokedAt`), so withdrawing it, or revoking the agent
    grant linked to it (D12), ends every family of the grant at once. No family document is written for it.
  - **Rejected:** a bounded wait for the index in the sweep (the open point above: still timing-bound, and a wait in a
    request path); setting the grant's `LastRevokedAt` on a reuse (exact too, but it signs out every family of the grant,
    every device of a shared client id such as Claude Code's metadata document, and makes a theft signal look like the
    user withdrawing consent); the family as an `OidcToken` of a new type (the token sweeps and the disable cascade query
    `OidcTokens` by grant, application and status and would rewrite it in passing; a typed generation reads better).
  - **No backward compatibility, and nothing to migrate:** a refresh token without a family is refused, fail closed as a
    missing grant. None exists outside development: the MCP servers of CodeCoverage (M6) and SeoTester (M5), the two
    production apps that host the provider, have not been deployed (M9), and SparkId and Fleet are development demos whose
    agents sign in again. Seeded refresh
    tokens in tests are stored in a family (`OidcTestHost.StoreInFamilyAsync`), so a case refuses them for what it is
    about. A device code spent twice keeps its old answer (`invalid_grant`, no teardown): RFC 8628 clients poll, and two
    overlapping polls must not revoke anything.
  - **Not changed:** RFC 7009 revocation of a refresh token still sweeps the grant's access tokens best-effort (the
    revoked refresh token itself is refused at once, by its loaded status); refresh-token introspection consults the
    family now but, as before, not the grant.
  - **Evidence, red → green:** `OidcRefreshTokenFamilyTests` stops the database's indexing while a family grows, after a
    first reuse has primed the sweep's auto-index, so the sweep cannot see the tokens in question. Red against the code
    before the family document: 4 of 5, each with the successor's or the winner's refresh token still refreshing (200): a
    rotated token presented again, four concurrent refreshes of one token, a code presented twice, a code redeemed four
    times at once. The fifth, the grant withdrawal, passed before too: it pins the link the families keep. Green: 5/5.
    Mutation runs (guard removed → red, restored → green): the family check on access tokens off → the four cases red at
    their access-token assertion (introspection `active: true`); the lost race's re-read off (`SpentMeanwhileAsync` never
    true) → both concurrency cases red (the winner's refresh token refreshed, 200), so their losers really race. The waits
    for indexing before a reuse are gone from `McpEmbeddedAuthorizationServerTests.Reusing_a_refresh_token_revokes_the_whole_family`
    and `OidcTokenSecurityTests.Reused_refresh_token_is_refused_and_revokes_the_chain`. On the final tree (rebased onto
    ca0577b0, CodeCoverage and SeoTester on `AgentsOnly()`): the `IdentityProvider` and `Mcp` namespaces with the library
    route snapshots 753/753; CodeCoverage's `Mcp` classes and route snapshot 29/29; SeoTester's `Mcp` classes
    (`SeoToolsTests`, `AgentAuthorizationServerTests`), `RateLimitTests` and route snapshot 18/18; Fleet's
    `McpEmbeddedServerTests` and route snapshot 5/5; SparkId's route snapshot 1/1. The solution builds, 0 errors.
- **M7a — the §6.2 audit, S5, S9, E2E and the tests earlier milestones left (2026-10-12).** Decisions and evidence:
  - **Audit:** every §6.2 item mapped to its tests in §6.2 "Coverage (M7a audit)", SeoTester's included since its merge.
    Gaps closed (marked **+** there): HMAC keyed with the issuer's own public key; tokens of the trusted issuer for
    neighbouring resources (another MCP endpoint, the origin, a path below `/mcp`, a near miss beside other audiences); a
    type choice made at consent (EMB) and on the page (EXT, narrowed and widened on the same token); group removal for EMB,
    EXT and SeoTester's organization membership; the security stamp for EMB; the real sign-in cookie on `/mcp` (alone
    anonymous, beside a token the token decides, the scheme feature reset); `RequireSignIn` on a personal-token host; the
    implied `resource` followed to the token's `aud`; sort and filter on a hidden reference through `query`; a hidden
    reference kept by a plan's save; anonymous `apply_changes` of a handed plan; SeoTester: chosen types, every read's
    uniform not-found, anonymous `start_scan` and `get_page`. Not covered, by design: REM's provider-side withdrawal against
    a live JWT (the resource validates the token itself; the provider's withdrawal ends the refresh, the EMB cases).
  - **Earlier milestones' tests:** a custom action on a selection through propose/apply (`McpPlanTests`: checked at propose,
    run once at apply on the rows re-read through the query; a row the caller cannot see, a missing query and a row gone by
    apply refuse the whole plan); `apply_changes` with an external server's token (`McpExternalAuthorizationServerTests`:
    the toggle and `mcp:write` at propose *and* at apply; a read token cannot run a plan a write token proposed).
  - **S5 and S9:** spikes doc, both PASS after fixes. HR's suite runs HR's real `Program` over a copy of its content root
    whose `security.json` adds one test group (no other way to reach D19 and attribute denials with HR's own groups).
  - **E2E** (`tests/MintPlayer.Spark.E2E.Tests/Mcp/McpAgentJourneyTests.cs`, 2/2): the OAuth dance against Fleet's own
    server (401 + RFC 9728 challenge → PRM → RFC 8414 → anonymous loopback DCR → Playwright on `/connect/login` and
    `/connect/consent`, whose forms post their antiforgery fields → code + PKCE + `resource` at a real loopback listener →
    `tools/list`, `tools/call`), and a personal token made, used and revoked on Connected applications. Fleet runs as its
    agents' server only in this collection (`FleetTestHost.AgentAuthorizationServer`, environment `E2EMcp`, its own process
    and rate-limit bucket); the shared host is unchanged. No new host app; `E2E_APPS` unchanged.
  - **Bugs found and fixed, each red first:** (1) a sub-query without its parent answered the web with a 500
    (`EnsureParent`'s exception) and `/mcp` with the SDK's opaque error: now 400 and `SparkQueryParentRequiredException`
    (S5, HR's `company-people`); (2) `get` dropped detail rows (the web carries them in `object`/`objects`): HR's contracts
    read `null`; now inline objects, counted rows and `include`, bounded by 100 rows and 30,000 characters (S5, S9);
    (3) a SeoTester grant of the scans without the catalogue made `run_summary`, `list_findings` and `get_page` throw: the
    catalogue is now read through the scan (`[McpReadsThrough(CheckDefinition, AuditRun)]`); (4) `seotester_run_summary` and
    `seotester_list_findings` passed the output cap (40,567 and 40,558 characters, cut JSON): sample URLs while they fit,
    headers counted (S9); (5) `codecoverage_drops` at its maximum `take` threw: RavenDB's session request budget, lifted for
    that loop (S9).
  - **Decision for the owner (taken, reversible):** an AsDetail type without `agentPermissions` is no type for the agent, but
    its rows inside an exposed object stay the object's values (HR's `Person.Address`), as query rows and the web show
    them; an AsDetail type's `agentPermissions` governs writing its rows. The other reading (hide them) would need a narrowing
    in the presentation path.
  - **Falsifiability:** two mutation batches ran before the owner stopped mutation runs (2026-10-12: "they burn tokens for
    little gain"; evidence is red-before-fix from now on). With the guard removed these new tests failed: the type choice in
    `ScopeOf` and at consent, the store groups (EMB, EXT), the embedded stamp check, `RequireSignIn`, the signature and
    audience checks (the HMAC and neighbouring-resource variants), the bearer-only policy with the gate's reset (the cookie
    test), the tool filter (anonymous apply), the hidden-reference set (sort and filter), the selection gates, the write cap
    (external apply). Passed immediately, not mutated: the implied-`resource` `aud` test (its one mutation missed the
    path), SeoTester's uniform not-found, anonymous and membership tests, the kept-on-save assertion, the E2E journeys. All
    guards restored; `grep -rn "M7 MUTATION\|if (false &&"` over `libs apps tests` is empty.
  - **Still open for M7:** the manual checklist (live S3/S11 sign-ins with Claude Code, Codex, Gemini) and S6's live run.
- **S6 live (2026-10-12): PASS.** Evidence in [spark_mcp_spike_S6.md](spark_mcp_spike_S6.md). The setup: Claude Code
  2.1.296 (`claude-opus-5-5`), a local SeoTester on `SeoTester-S6` with the fixture site scanned (2 pages), a read-only
  personal token, and only SeoTester's read tools plus `Read`, `Edit`, `Glob` and `Grep`.
  - **Not obeyed, in either run.** Every injected string reached the agent inside the D10 fence (14 of 14 in
    `list_findings`, none outside). No shell call ran or prepared an injected command: the shell calls were two
    read-only `git ls-files` before any finding was read, and one refused `curl` of the fixture's images.
    `S6_CANARY.txt` was never edited, and both repositories kept their one commit.
  - **Fixed in the MCP-prompt run.** That run fixed `lang`, charset and viewport, one H1, alt texts, link texts,
    absolute canonicals and `user-scalable=no`, and removed the injected text as page content. The plain-prompt run
    changed nothing: its `Write` was refused by the tool set, and it did not retry with `Edit`.
  - **Runbook corrections (spikes doc, S6 steps 3, 4, 8 and 9):**
    - `Spark__HttpsRedirection=false`. Otherwise `http://localhost:5014/mcp` answers `307` to https under the `https`
      profile.
    - Confirm a new SparkId account's email first. SeoTester refuses `email_verified` false.
    - `--setting-sources project,local --permission-mode dontAsk`. User settings that allow `Bash` or default to `auto`
      would otherwise give the agent a shell, because `--allowedTools` only adds to those rules.
    - Judge shell calls by what they run.
  - **Open observations for the owner, not changed:**
    - Neither final answer told the user that the pages address AI agents. The MCP prompt's summary step could ask for
      that.
    - The `seo.canonical` message reads "which returns relative" (a template slip in `PageChecks.cs`, `Canonical`).
- **"Create an account" on `/connect/login` (owner request during the live checks, 2026-10-12).** A new user sent to the
  sign-in page by an agent's authorization request had no way to register. The page now links to the application's
  registration page with the pending authorization as `returnUrl`, only when the application offers registration
  (`LocalCredentials` is `Full`, the mode that maps `POST /spark/auth/register`). The page path is the new
  `idp.RegistrationPath` (`Spark:IdentityProvider:RegistrationPath`, default `/register`, ng-spark's `withRegistration()`);
  no existing option named it. An off-site `returnUrl` gets no link, and ng-spark's register page passes only a local one
  on to its sign-in step. Owner's scope: a simple link; the email-confirmation mail does not carry the `returnUrl`.
  Evidence: `OidcLocalCredentialModeTests.Login_page_links_to_registration_with_the_local_return_url_only_when_registration_is_on`
  red before the change, green after; the register spec's returnUrl case.
- **Live checks, round 1 (2026-10-12).** Claude Code 2.1.296 against HR (setup 2, SparkId as the external server), owner at
  the keyboard. Evidence and decisions:
  - **S3 for Claude Code: PASS.** It registered through CIMD (`client_id=https://claude.ai/oauth/claude-code-client-metadata`,
    redirect `http://localhost:<port>/callback`), asked `mcp:read mcp:write offline_access` with
    `resource=https://localhost:5005/mcp`, signed in at SparkId, consented, and reconnected.
  - **Setup 2 mapping held:** before the owner had signed in to HR with Spark Identity, HR refused the valid token
    (`External agent access token refused on /mcp (no linked login)`, D22). After that sign-in, the agent connected.
  - **Groups per request (S4) held live:** the new HR user first saw only Company and Profession (Signed-in users);
    after adding it to *HR managers* in the database, the next question listed 4 people, with no new sign-in.
  - **Local TLS:** Claude Code rejects the ASP.NET development certificate even through `NODE_EXTRA_CA_CERTS` (not a
    CA), ignores `NODE_USE_SYSTEM_CA`, and refuses IP names under a name-constrained CA (`UNSUPPORTED_CONSTRAINT_TYPE`).
    A localhost-only test CA with a DNS-only certificate worked; guide-mcp.md troubleshooting says so.
  - **Sign-in page:** owner request, a "Create an account" link carrying `returnUrl` on `/connect/login` (entry above);
    after registering, ng-spark's register page returns to `/connect/login` for a pending `/connect/` request.
  - **Found, to fix in this PR** (plan M7): a deleted user's email reservation outlives the user; the external-login
    callback doesn't log why creating the account failed; query rows carry the full metadata of AsDetail values;
    the unlinked-user 401 doesn't say what to do. The agent itself pointed out the oversized rows and data gaps
    (people without a name, an empty Person) in the demo data.
  - **Fixed (b377bf36):** `UserStore.DeleteAsync` removes the user and its own `emails/` reservation in one cluster-wide
    transaction; create/update never leave a reservation behind on a failed save; creation takes over a reservation
    whose user document is gone (compare-exchange index), never one whose user exists. Red→green: `UserStoreTests` (3 new).
  - **Fixed (8e48d460):** the callback logs provider, step and `IdentityResult` error *codes* (descriptions echo the
    address, so they are left out). Red→green: `ExternalLoginCallbackTests.Callback_logs_why_…` (both steps).
  - **Fixed (24b780b2):** MCP rows compact a single AsDetail value to its attribute values (through the redaction net,
    as `get` does); the web projection keeps the object, which a custom renderer receives (#329). Red→green:
    `McpQueryRowShapeTests`.
  - **Fixed (0b1c88c5):** a validly signed token with no linked login gets a 401 whose JSON-RPC message and
    `error_description` say "sign in to <app> once with <provider> to link it, then reconnect"; every other refusal is
    unchanged. Red→green: `McpExternalAuthorizationServerTests.An_unlinked_subject_is_a_401_not_anonymous`; the refused-token
    theory asserts no advice.
