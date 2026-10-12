# PRD — MintPlayer.Spark.Mcp: an MCP server inside a Spark app, acting as the user

**Status:** designed; owner grill done and consistency-reviewed (2026-10-12, §10); no open questions.
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
| D22 | **External authorization server (setup 2) maps a token to a user only through a linked external login**: the server is configured as one of the app's sign-in providers, the issuer is bound to that provider, the token's `sub` must equal the linked login's key. Unmapped → **401, never anonymous**. Groups and rights come from the local user, never from token role claims. A **shadow `AgentGrant`** is created on first use with "everything exposed", `Read` only. `Write` requires both the user turning it on under Connected applications **and** `mcp:write` in the token: in this setup only, the token scope is a real cap, because we can't step the user up through another server's consent (R3-Q18). Opaque tokens go through introspection. Full adversarial test list in §6.2. **owner** (R1-Q14, Q15: "if we manage to make it secure, add the appropriate tests") | Matching on `email` is a classic account-takeover bug; issuer binding stops cross-issuer `sub` collisions. |
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
| 2. External authorization server | Authorization + Mcp | issued by Entra/Keycloak/Auth0 for audience = canonical URI | at the external server | RFC 9728, pointing to it |
| 3. Embedded authorization server | Authorization + IdentityProvider + Mcp | issued by the app | at the app, any sign-in method | RFC 9728 + RFC 8414 |

If setups 2 and 3 are both wired, the metadata lists the embedded server first; tokens of both are valid.

### 4.3 Package surface

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>(...);            // the app's existing sign-in
    spark.AddIdentityProvider(idp => idp.ForMcp());      // setup 3 only
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
| `seotester_get_page(site, url)` | One page's facts and findings. |
| `seotester_start_scan(site)` (Write) | Starts a re-scan; same organization role and quota as the button. |
| `seotester_scan_status(run)` | Phase and progress. |

Gaps closed in this PR:

1. **Element evidence**: up to 5 CSS selectors and an outer-HTML excerpt of at most 300 characters per
   finding; today axe keeps only id, impact, node count, help [verified
   `SeoTester.Library/Rendering/RenderFacts.cs:46`]. Size → S7.
2. **Expired page results** (7 days): the tools say so and suggest a re-scan; never an empty "clean" list.
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
| `codecoverage_hotspots(repo, branch?, pathPrefix?, take)` | Files ranked by expected gain per unit of effort (§4.8.1). |
| `codecoverage_file(repo, path, commit?)` | Uncovered line ranges and partly covered branches. Line numbers only. |
| `codecoverage_patch(repo, pullRequest)` | Patch coverage and uncovered changed lines. |
| `codecoverage_drops(repo, since?)` | Commits that lowered coverage, with the files responsible. |

**4.8.1 Ranking.** Score = uncovered lines × ease; ease is higher for files that already have some coverage
and for files whose uncovered lines sit in few ranges. Heuristic until S8.

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

## 7. Risks

- **OAuth server in production apps** (setup 3): only the MCP preset is on.
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
    user refuse the next call too. No security-stamp check for `ext/` grants: the user never consented to a stamp, and
    removing the login (which changes the stamp) already unmaps the token.
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
    references would otherwise be hidden, D19). HR signs in against SparkId, so the setup wired is personal access tokens
    (account page); setup 2 against SparkId is possible with M3c's `AddExternalAgentAuthorizationServer` but not wired.
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
