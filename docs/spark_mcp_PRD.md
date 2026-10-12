# PRD — MintPlayer.Spark.Mcp: an MCP server inside a Spark app, acting as the user

**Status:** designed; owner grill done and consistency-reviewed (2026-10-12, §10); one open question (Q18). No code yet.
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
| D2 | Built on the official C# SDK `ModelContextProtocol.AspNetCore` **2.2.x**, stateless HTTP, hybrid mode for 2025-11-25 clients. | 2.2.0 stable 2026-08-13, spec 2026-07-28, targets net10 [cited nuget.org/packages/ModelContextProtocol.AspNetCore]. Hybrid on net11 → S1. |
| D3 | `/mcp` accepts bearer tokens only. Cookies are ignored on it; `Origin` is validated. A call **without** a token runs as `anonymous` (D17). | Spec: token only in `Authorization`, audience = canonical URI, no passthrough, Origin MUST [cited modelcontextprotocol.io/specification/2026-07-28/basic/authorization]. No ambient credential → no CSRF surface; bearer requests are antiforgery-exempt [verified `SparkAntiforgeryMiddleware.cs:56-70,155-164`]. |
| D4 | **Three setups, flags `EAgentAuthentication { PersonalAccessToken, ExternalAuthorizationServer, EmbeddedAuthorizationServer }`, default all.** Effective = flags ∩ what is wired up; an explicitly set flag without its wiring fails at startup. See §4.2. **owner** (R1-Q16) | Only an OAuth server that speaks MCP's OAuth (PKCE, `resource`, CIMD/DCR) can give an agent browser sign-in; GitHub/Google/personal Microsoft can't, so CodeCoverage and SeoTester need the embedded one. The resource server and external-login links are already in the Authorization package [verified `libs/authorization/.../ResourceServer/SparkResourceServerExtensions.cs`, `Endpoints/ExternalLogin/ExternalLoginCallback.cs`]. |
| D5 | Client registration (embedded setup): **Client ID Metadata Documents from any HTTPS host** with SSRF guards (public addresses only, size cap, timeout, cache), **plus anonymous DCR limited to loopback redirect URIs**, rate-limited per IP, pruned after 30 days unused. The consent screen shows client name and redirect host; DCR clients are labelled **unverified**. **owner** (R1-Q8) | Spec 2026-07-28: CIMD SHOULD, DCR deprecated but kept [cited …/authorization/client-registration]. Claude Code and Codex do both, Gemini CLI only DCR [cited code.claude.com/docs/en/mcp, learn.chatgpt.com/docs/extend/mcp.md, geminicli.com/docs/tools/mcp-server]. Today's DCR needs an initial access token [verified `Endpoints/Register.cs:13-41`]. → S3. |
| D6 | **One `[Flags] EAgentPermission { None, Read, Write }` at every layer.** Effective = **app(agent) ∩ type ∩ grant ∩ security.json rights**. App: `mcp.AgentPermissions` is a `Func<AgentInformation, EAgentPermission>` (a plain value converts implicitly), evaluated per call, synchronous. Type: model JSON `"agentPermissions"` (default `None` = not exposed; **replaces `exposeToAgents`**). Grant: chosen by the user per type. `Write` without `Read` on a type fails at startup. **The grant, not the token's scope, decides**: a token's `mcp:read`/`mcp:write` scope is never a cap, so a grant edited upward applies without a new sign-in (R1-Q2). Scopes exist only so a missing `Write` can answer 403 `insufficient_scope`, whose re-consent updates the grant. **owner** (R1-Q11, Q12) | Same enum convention as `EShowedOn` [verified `libs/model/MintPlayer.Spark.Model/EShowedOn.cs:9`]. |
| D7 | `AgentInformation` carries only what can be trusted, named accordingly: `Kind` (`EAgentKind`: `Anonymous`, `PersonalAccessToken`, `ExternalAuthorizationServer`, `MetadataDocument`, `DynamicRegistration`, `PreRegistered`), `ClientId`, `VerifiedHost` (CIMD only), `UnverifiedName`, `User`. **owner** (R1-Q12) | Client names are self-declared; a CIMD client id's host was fetched over HTTPS. |
| D8 | Generic tools `query`, `get`, `discover`, `describe`, `propose_changes`, `apply_changes`. Writes are **propose → apply** with plans in an **`McpPlans` collection**, native document `@expires` 10 min, one-shot via optimistic concurrency. `apply_changes(planId, changes)` **must echo the readable diff** so the host's approval prompt shows what will run; the server checks it matches. Applied plans lose their 10-min expiry and are kept `Spark:Mcp:PlanRetention` (default 1 year) as the write audit. **Proposing requires a signed-in caller**: an anonymous propose would let anyone write plan documents. **owner** (R1-Q6; anonymous rule from consistency review R2) | The approval prompt shows call arguments; an opaque id alone would hide the change. |
| D9 | **Authorization parity is a hard requirement.** Every check that today lives only in an HTTP endpoint moves into an internal service both call; `InternalsVisibleTo("MintPlayer.Spark.Mcp")`. Parity tests prove identical rows and attributes. | Endpoint-only today: type/query list filtering [verified `Endpoints/EntityTypes/List.cs:30,56`, `Queries/List.cs:28`, `Queries/Get.cs:54`], save validation + etag [verified `Endpoints/PersistentObject/Update.cs:52,81-93`], custom-action gates [verified `Endpoints/Actions/ExecuteCustomAction.cs:84,91,314`]; `ICustomActionResolver` bypasses all of it [verified `CustomActionResolver.cs:7-29`]. |
| D10 | Results are serialized with **Spark's HTTP `JsonOptions`** (the redaction net), **fenced as untrusted data** in a random per-call tag, paged, capped at about 10k tokens. | Redaction net lives in HTTP `JsonOptions` [verified `SparkMiddleware.cs:102-104`]. Crawled content is an injection channel → S6. Claude Code warns at 10k output tokens [cited code.claude.com/docs/en/mcp]. |
| D11 | **Personal access tokens** live in the **Authorization package** (all setups): created on the account page with the same per-type choice and read/changes choice, prefix `spat_`, shown once, stored hashed, **mandatory expiry, default 90 days, max 1 year**, revoked on security-stamp change. **owner** (R1-Q4, Q16) | All three clients accept a static bearer header [cited D5 sources]. Precedent: CodeCoverage's hashed `covt_` tokens [verified `CodeCoverage.Library/Entities/ApiToken.cs`]. |
| D12 | **`AgentGrant` collection in the Authorization package**, one per user and agent, **natural ids**: `AgentGrants/{userId}/pat/{tokenId}`, `AgentGrants/{userId}/ext/{sha256(issuer + "\|" + clientId)[..32]}`, `AgentGrants/{userId}/app/{clientId}`. Holds per-type permissions ("all exposed" or a chosen list), read/changes, `LastUsedAt`, and for tokens the hash and expiry. **It is the source of truth for access**; in the embedded setup `OidcGrant` keeps OAuth bookkeeping and revoking either revokes the other. Read per call, cached a few seconds, invalidated on edit. **owner** (R1-Q2, Q5, Q17) | A grant edited on the server takes effect on the next call without a new sign-in, in both directions. `OidcGrant` exists only with the IdentityProvider [verified `libs/identity_provider/.../Models/OidcGrant.cs:8-46`]. |
| D13 | **Per-type grant restrictions are enforced inside `IPermissionService`** for agent requests, so every tool, generic or app-written, honours them. **owner** (R1-Q2) | Query rights are already `Query/{type}` [verified `QueryReadPath.cs:97-101`]. |
| D14 | A **Connected applications** section in the Authorization package's account area (all setups) lists each grant: agent, kind, verified/unverified, allowed types, read/changes, created, last used, token expiry; edit, revoke, create token. Linked from `/account` through `SPARK_ACCOUNT_OVERVIEW_LINKS`. The IdentityProvider references the Authorization package, not the reverse, so the section has an **extension point** through which the IdentityProvider contributes its non-agent OAuth grants (sign-in at other websites); its server-rendered `/connect/applications` stays. **owner** (R1-Q4, Q5) | Account page link mechanism [verified `ng-spark/auth/models/src/auth-route-config.ts:56-71`]; the IdP's existing SPA page [verified `Endpoints/Portal/ConnectedApplicationEndpoints.cs:37,82`] becomes such a contribution. |
| D15 | Audit: one structured log event per call (user, client id, grant id, tool, arguments **hash**, row count, outcome, duration) + `AgentGrant.LastUsedAt` written at most once a minute. Writes are audited by the kept plans (D8). **owner** (R1-Q7) | Arguments are hashed because search terms can be personal data. |
| D16 | Rate limits per user for signed-in calls, per IP for anonymous calls, with the existing quota mechanism. | [verified `docs/guide-quotas.md`]. |
| D17 | **Anonymous and signed-in follow security.json exactly**, narrowed by `agentPermissions`. Denials copy the web's map: anonymous caller, target not permitted **or unknown** → uniform **401**, carrying the OAuth challenge when setup 2 or 3 is wired (the cue to sign in) and a plain 401 in setup 1; signed-in caller denied or unknown → uniform not-found. **Anonymous calls are read-only**: whatever `AgentPermissions` returns for `EAgentKind.Anonymous` is clamped to `Read`, and `propose_changes` is refused (D8). **owner** (R1-Q10) | Web map: `SparkDenial.Refuse` 401s anonymous before loading, so no existence oracle [verified `Endpoints/SparkDenial.cs:22-34,72-78`]; ng-spark redirects any 401 to sign-in [verified `ng-spark/auth/interceptors/src/spark-auth.interceptor.ts:14-28`]. Mid-session 401 handling by clients → S11. |
| D18 | **Web alignment in this PR:** query execute answers an anonymous caller **401** (uniform, existing or not) instead of 404, like persistent objects. Release-notes line. **owner** (R1-Q10) | Today query execute 404s everyone [verified `QueryReadPath.cs:78-85,193-199`], so an anonymous visitor on a protected query page is never sent to sign in. |
| D19 | **Web fix in this PR:** a reference whose **target type** the caller may not `Read` keeps its id and shows the redacted placeholder, for web and MCP alike. Red test first; audit every app's security.json for affected references; release-notes line. **owner** (R1-Q3) | `BreadcrumbResolver` consults only row security and attribute rights, never `IPermissionService` [verified `BreadcrumbResolver.cs:98-104`, `RowSecurity.cs:254-258`, `EntityMapper.cs:359-380`]. |
| D20 | **Endpoint `/mcp`**, configurable; `Spark:Mcp:CanonicalUrl` override (default `PublicOrigin` + path) for a subdomain behind a proxy. Startup fails if the path is already mapped. **owner** (R1-Q9) | Outside `/spark` the endpoint doesn't inherit Spark's default CORS and ambient-credential antiforgery [verified `docs/guide-cors.md:32,55-57`]. |
| D21 | **Tool prefix** `mcp.ToolPrefix`, defaulting to a slug of `ServerName` (`seotester_`, `codecoverage_`); the guide tells developers to set it explicitly so renaming `ServerName` doesn't rename tools users allow-listed, applied to generic and domain tools; domain tools are written unprefixed. **owner** (R1-Q13) | Two Spark apps in one session (ours) would collide on generic tool names. |
| D22 | **External authorization server (setup 2) maps a token to a user only through a linked external login**: the server is configured as one of the app's sign-in providers, the issuer is bound to that provider, the token's `sub` must equal the linked login's key. Unmapped → **401, never anonymous**. Groups and rights come from the local user, never from token role claims. A **shadow `AgentGrant`** is created on first use. Opaque tokens go through introspection. Full adversarial test list in §6.2. **owner** (R1-Q14, Q15: "if we manage to make it secure, add the appropriate tests") | Matching on `email` is a classic account-takeover bug; issuer binding stops cross-issuer `sub` collisions. |
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
  canonical URI. Device, client-credentials and token-exchange stay off.
- `AddMcp` registers the SDK (`AddMcpServer().WithHttpTransport()`), a `Spark:Mcp` bearer scheme per
  wired setup, the RFC 9728 document and challenge, the Origin check, and maps the endpoint.
- The canonical URI never comes from the request's `Host` header.

### 4.4 Generic tools (names shown without prefix)

| Tool | Needs | Does |
|---|---|---|
| `discover(search?, skip, take)` | Read | Exposed types and queries the caller may query, with descriptions. |
| `describe(type)` | Read | Readable attributes: names, labels, descriptions, data types, references. Schema only. |
| `query(query, search?, filters?, sort?, skip, take)` | Read | Runs a query through the shared query read path. Compact table + object ids. |
| `get(type, id, include?)` | Read | One object as presented: readable attributes, available actions, detail rows on request. |
| `propose_changes(summary, operations[])` | Read, signed in | Validates create/update/delete/custom-action operations against rights, rules and etags; stores a plan; returns `planId` + readable `changes`. Changes nothing. At most 50 operations. |
| `apply_changes(planId, changes)` | Write | Checks that `changes` equals the plan's rendering, re-authorizes, re-validates and runs. One-shot; user-bound; expires after 10 min; a stale etag fails the whole plan. |

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
  membership applies on the next call [inferred → S4].
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
| Claude Code | CIMD or DCR | `/mcp` → Authenticate, or `claude mcp login <name>` | `--header "Authorization: Bearer ${SPARK_PAT}"` |
| Codex CLI | `auto`: CIMD on loopback, else DCR | `codex mcp login <name>` | `bearer_token_env_var = "SPARK_PAT"` |
| Gemini CLI | DCR | `/mcp auth <name>` | `headers` in `settings.json` |

Loopback redirects on any port, `localhost` and `127.0.0.1`, native clients only (RFC 8252).

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

### 6.2 Required adversarial tests (red first)

External setup (D22): wrong audience · wrong issuer · `sub` linked under a different provider · same `sub`
value from another issuer · ID token presented as access token · `alg: none` and HMAC/RSA confusion ·
expired and not-yet-valid · token for another resource · unlinked `sub` → 401, not anonymous · token role
claims grant nothing · revoked shadow grant blocks the next call.

All setups: grant revoke from either side (AgentGrant ↔ OidcGrant) blocks the next call · grant type
restriction blocks generic and app tools · grant edit applies without re-sign-in · `apply_changes` with a
tampered `changes` fails · plan replay fails · plan of another user fails · expired plan fails · anonymous
write refused · anonymous uniform 401 for existing and unknown names · signed-in uniform not-found ·
cookie on `/mcp` ignored · bad `Origin` 403 · `covt_` and GitHub OIDC rejected · personal token past
expiry rejected · security-stamp change revokes all · CIMD to a private address refused · DCR with a
non-loopback redirect refused · unreferenced-type reference shows the placeholder (D19) · query execute
401s anonymous (D18).

## 7. Risks

- **OAuth server in production apps** (setup 3): only the MCP preset is on.
- **Spec churn**: the SDK absorbs transport changes.
- **Three setups**: three test paths; the adversarial list is the guard.
- **Gemini without CIMD**: if loopback DCR is ever turned off, Gemini users need a personal token.
- **Behaviour changes for existing apps** (D18, D19): release notes; audit of every app's security.json.

## 8. Open questions

| # | Question | Recommendation |
|---|---|---|
| Q18 | External setup: the external server's consent screen can't show our type list, so what does the **shadow grant** allow on first use? | "Everything exposed", `Read` only. `Write` only once the user turns it on under Connected applications **and** the token carries `mcp:write`. In the external setup the token's scope is a real cap, because we can't step the user up through someone else's consent. |

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
