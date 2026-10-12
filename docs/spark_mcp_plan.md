# Plan — MintPlayer.Spark.Mcp

PRD: [spark_mcp_PRD.md](spark_mcp_PRD.md) (decisions D1–D24, rounds 1–4). One branch, one PR,
including both production adopters (D24). Tests run once, after M8 (batched sweep), except where a spike
needs its own run. Red tests for D18, D19 and the §6.2 adversarial list are written before their fixes.

## Sequencing

```
spikes S1–S4, S11 → M1 → M2 → M3a ─┬→ M3b → M4 ─┬→ M5 SeoTester (+S6, S7)
                                   └→ M3c        └→ M6 CodeCoverage (+S8)
                         M7 parity, adversarial, E2E (S5, S9) → M8 docs → sweep → PR → M9 deploy
```

S1–S4 and S11 first: they can change D2, D4, D5 and D17. A failing S11 adds the `RequireSignIn` switch.
(It did, by source reading: PRD round 4.)

## Milestones

### M0 — Spikes S1–S4, S11 — done (2026-10-12), except the live S3/S11 run
Throwaway branch `spike/spark-mcp`. Results in [spark_mcp_spikes.md](spark_mcp_spikes.md) and the PRD
decision log, round 4. S1, S2, S4 ran live. S3 and S11 were answered by source reading; their live
sign-ins (Codex ≥ 0.148, Gemini 0.63, Claude Code) moved to the M7 manual checklist.

### M1 — Core: shared paths and the two web fixes
- Move endpoint-only checks into internal services (D9): type/query list filtering; the save sequence
  (`EnsureSaveAuthorizedAsync` → `ISaveValidation` → save with etag) for create and update; the
  custom-action sequence (grant, catalogue gate, per-row check, selection cap, quota, disabled actions).
  `InternalsVisibleTo("MintPlayer.Spark.Mcp")`.
- D19: reference target without `Read` → placeholder (red test first); audit the security.json of every
  app in `apps/` for affected references.
- D18: query execute 401s anonymous callers uniformly (red test first).
- Hook in `IPermissionService` for an agent-request restriction provider (D13); no-op without Mcp.
- `agentPermissions` (`EAgentPermission`) in the model JSON, JSON schema, `--spark-describe`, and the
  `Write`-without-`Read` startup check.
- Analyzer SPARK020: fix the false positive on `RequireAuthorization("<name>")` for a policy the app (or a
  Spark package such as Mcp) registers itself (`AuthorizeAttributeAnalyzer.cs`); red test first (PRD D3,
  spikes S1 finding 2).
- `ISparkCurrentUser.Id` for bearer principals: map the token's `sub` to the local user id
  (`HttpContextSparkCurrentUser.cs:25-27` reads only `NameIdentifier`). Core, because the class is in
  `MintPlayer.Spark` (PRD D4, §4.6).

### M2 — Mcp package and read tools
- `libs/mcp/MintPlayer.Spark.Mcp`: `AddMcp`, options (`ServerName`, `ToolPrefix`, `Instructions`,
  `Authentication`, `AgentPermissions` with `AgentInformation`, `Path`, `CanonicalUrl`, `RequireSignIn`,
  quotas), SDK pinned to 2.2.0 in `Stateless` mode (no hybrid), our own Origin check, path-collision
  startup check.
- Cookies ignored (PRD D3): `AddMcp` builds the bearer-only endpoint policy itself, then resets
  `ISparkAuthenticatedSchemeFeature` after the policy swaps the user; must not trip SPARK020.
- `RequireSignIn` (default off): every unauthenticated `/mcp` request, `initialize` included, 401s with
  the challenge (PRD D17, S11).
- The 401/403 tool error text names each client's own sign-in: Claude Code authenticate tool or
  `claude mcp login <name>`; `codex mcp login <name>` then reconnect; Gemini `/mcp auth <name>` (PRD D17).
- Generic `discover`, `describe`, `query`, `get`; `SparkMcpResult` (Spark `JsonOptions`, fencing, paging);
  prefixing; ReadOnly → Read / else Write filter; anonymous handling with uniform 401 (D17); per-user and
  per-IP limits; audit log events.
- Analyzer SPARK05x for raw sessions / unchecked calls in tool classes.

### M3a — Authorization package: grants and personal tokens (setup 1) — done (2026-10-12)
- Store-backed groups for agent bearer callers (PRD D4, §4.6): an `IGroupMembershipProvider` loading
  `SparkUser.Roles` by `sub` per request, bearer principals only, registered through core's
  `AddGroupMembershipProvider`. Here, not in core: `ClaimsGroupMembershipProvider` lives in core
  (`libs/spark/MintPlayer.Spark/Services/`), but the store provider needs `SparkUser`, which the
  Authorization package owns. Used by setups 1–3 (setup 2 after the linked-login mapping). Test: a role
  removal denies the bearer's next call (S4).
- `AgentGrant` collection with natural ids (D12), per-call read with short cache and invalidation.
- Personal access tokens (`spat_`, hash, expiry, security-stamp revocation) and their bearer scheme.
- Connected applications section (ng-spark) linked from `/account`: list, edit types and read/changes,
  revoke, create token, last used. An extension point through which the IdentityProvider contributes its
  non-agent OAuth grants (its SPA portal page becomes that contribution).

### M3b — Embedded authorization server (setup 3) — done (2026-10-12)
As built, with the deviations, in the PRD decision log ("M3b implementation"): RFC 9728 served by Mcp through
`IAgentCredentialScheme.AuthorizationServer`; G11 via scope prefix before `.` or `:` plus `OidcResource.Audience`;
the consent screen is a server-rendered `/connect` page (not Angular); VS Code's built-in list is the two https
redirects only (`vscode://` is never a `redirect_uri`); an anonymous refusal is an HTTP 401 when OAuth is wired.
- IdentityProvider `ForMcp()`: RFC 8414, RFC 9207 `iss`, CIMD with SSRF guards, loopback-only anonymous
  DCR with rate limit and 30-day pruning, `mcp:read`/`mcp:write`, audience = canonical URI, refresh
  family revocation.
- S2 gap list (PRD D5, spikes G1–G11), each with a test:
  - RFC 8414 `/.well-known/oauth-authorization-server` (G1), advertising
    `client_id_metadata_document_supported: true`, `token_endpoint_auth_methods_supported` incl. `none`,
    `authorization_response_iss_parameter_supported: true`, and `offline_access` in `scopes_supported`.
  - RFC 9728 `/.well-known/oauth-protected-resource[/mcp]` + `WWW-Authenticate: Bearer resource_metadata=…`
    (G2; served by Mcp, pointing at the embedded server).
  - CIMD lookup path beside the exact `ClientId` query in `OidcAuthorizationFlow` (G5).
  - Anonymous loopback DCR without an initial access token (G6).
  - Loopback redirect matching: `localhost` and `127.0.0.1`, any port, any path, for DCR and for CIMD
    documents with portless loopback URIs (G6, S3).
  - RFC 8707 `resource` → `aud` for `/mcp`, no fallback to `client_id` for the MCP resource (G4).
  - `mcp:read`/`mcp:write` expressible: explicit scope → resource link on `OidcApiScope` instead of
    "text before the first dot" (G11).
  - Refresh tokens issued to MCP clients (G8).
  - Public (not pairwise) `sub` for MCP clients (G10).
- Consent screen: client name, verified/unverified, redirect host, type choice, read/changes limited by
  `AgentPermissions(agent)`.
- `OidcGrant` ↔ `AgentGrant` linking; revoke in either revokes both.
- Hardening (comparison review 2026-10-12): optional CIMD host allow-list (empty = any HTTPS host, D5);
  CIMD fetch size cap, timeout and freshness cache; client id URL must be https with a path and no
  fragment, user info or dot segments, and the document must name its own URL as `client_id`; consent page
  `frame-ancestors 'none'` + `X-Frame-Options: DENY`; a "this application runs on your computer, continue only
  if you started it" notice for loopback clients; disabled or locked accounts refused at consent; a cleanup
  job for expired codes, tokens and pruned clients; rate limits on `/connect/*`; audit events for client
  registration, token issue/revoke and PKCE failures. VS Code (Copilot agent mode) is a fourth supported client (owner,
  2026-10-12): confirm from its source which redirects it registers, and allow exactly
  `https://vscode.dev/redirect`, `https://insiders.vscode.dev/redirect` and the `vscode://`/`vscode-insiders://`
  callbacks it uses (built-in list next to the loopback rule, never "any https"; PKCE makes the relay safe). Add
  VS Code to the client matrix (§4.9) and to guide-mcp-agents.md.

### M3c — External authorization server (setup 2) — done (2026-10-12)
As built, with the deviations, in the PRD decision log ("M3c implementation"): `spark.AddExternalAgentAuthorizationServer(issuer,
loginProvider)` in the Authorization package; validation shares `AddSparkResourceServer`'s scheme code under an `/mcp`-only
scheme (introspection opt-in); the mapping happens inside the scheme, so its `sub` is the local id; revoking keeps the
grant as *disconnected* (a deleted shadow grant would come back on the next call).
- Mcp wiring over `AddSparkResourceServer` (+ introspection), RFC 9728 pointing at the external server.
- Mapping through linked external logins only, issuer-bound; unmapped → 401; groups local; shadow grant.
- Tests: `McpExternalAuthorizationServerTests` (29), with mutation runs.

### M4 — Writes — done (2026-10-12)
As built, with the deviations, in the PRD decision log ("M4 implementation"): both tools need `Write` (propose too);
check-only runs of the real save/delete/custom-action paths; one RavenDB commit per plan via `ISparkWriteUnit`, with
the plan's applied record in it (single-node transaction); a custom action is always a plan of its own; apply re-renders
the diff and refuses if it changed; the plan is claimed before it runs (at most once). HR's contracts are `CarreerJob`
rows on `Person`; HR is wired for personal access tokens.
- `McpPlans` collection with document `@expires`; `propose_changes`, `apply_changes(planId, changes)` with
  diff echo check, one-shot, user and grant binding, re-validation, 409 handling, retention after apply.
- HR: contracts `"agentPermissions": "Read, Write"`, app `Read | Write`.

### Demo apps: HR on SparkId (setup 2), Fleet embedded (setup 3) — done (2026-10-12)
Owner decisions of 2026-10-12; as built, with the deviations, in the PRD decision log ("Demo apps").
- IdentityProvider `idp.ForMcpResource(url, r => …)` (or `Spark:IdentityProvider:Mcp:Resources`): a Spark identity
  provider as the external authorization server of another application's `/mcp`. One `OidcResources/mcp-{hash}` per
  endpoint (audience = its URL, `mcp:read`/`mcp:write`), the endpoint chosen by `resource`; the same metadata, CIMD,
  loopback DCR, VS Code redirects, refresh families as `ForMcp()`, which stays the call for the provider's own `/mcp`.
  Consent offers read or read + changes only (`mcp:write`); types and the write toggle stay with the application's
  shadow grant (D22).
- HR: `AddExternalAgentAuthorizationServer(SparkId, loginProvider: "SparkId")` where the SparkId sign-in is configured,
  beside personal access tokens; `Spark:Mcp:CanonicalUrl` and SparkId's resource entry in the Development settings;
  Connected applications mounted in the SPA.
- Fleet: Mcp (`Read`; `Car`, `ServiceEntry`, `Person`, `Company` `"Read"`), personal access tokens, and `idp.ForMcp()`
  when `Spark:IdentityProvider:Issuer` is set (Development); the provider's management rights off; Connected
  applications mounted in the SPA.
- D22 amended: external grants are security-stamp bound; a stamp change disconnects until the user allows the agent
  again (re-stamp). The Connected applications page now offers Edit for a disconnected grant.
- Tests: `McpRemoteResourceTests`, stamp case in `McpExternalAuthorizationServerTests`, Fleet's `McpEmbeddedServerTests`
  (real `Program`, CIMD and DCR), the connected-agents spec; Fleet's route snapshot.
- Live run with Claude Code (owner, with M7's manual checklist): steps in the decision log entry.

### Agent role parity — done (2026-10-12)
As built in the PRD decision log ("Agent principals carry the cookie's role and group claims").
- Every agent scheme (personal tokens, the embedded server, an external server) adds the role and group claims
  Identity's claims factory puts in the user's sign-in cookie, made per request, under the cookie's role claim type:
  `User.IsInRole` answers for an agent as for the user (Fleet's administrators' agents see every car), and a removed
  role is gone on the next call (S4). Closes the gap the demo-app work recorded.
- Tests: three setups in `McpAgentPrincipalTests` (the cookie's claims, the role removed, no other stored claim
  copied), Fleet's administrator against the cookie in `McpEmbeddedServerTests`.

### M5 — SeoTester — done (2026-10-12), except the live S6 run
As built, with the deviations, in the PRD decision log ("M5 implementation"); S7 and the S6 runbook in
[spark_mcp_spikes.md](spark_mcp_spikes.md).
- Embedded setup (`idp.ForMcp()`, issuer = the public URL, no key in configuration) and personal access tokens;
  `SeoTools` (PRD §4.7); `agentPermissions: "Read"` on `Site`/`AuditRun`/`CheckDefinition`; anonymous
  `QueryRead/CheckDefinition` in security.json; the identity provider's portal rights off.
- Mcp: `[McpReadsThrough]` (a run's pages through the run) and `[McpRunsAction]` (`StartAudit` on a `Read` type) per tool;
  `ISparkMcpActions` (custom actions through the web's gates); prompts through M6's `AddPrompts<T>()`.
- An agent sees its user's organizations' rows and spends its user's quota (Organizations, Settings): since the rebase
  onto `feat/spark-mcp`, through `NameIdentifier` on every agent principal (8a43aea2, cb543b7b); M5's own `sub`
  fallback in both packages was dropped as redundant (decision log, "M5 follow-ups").
- Element evidence: selectors only (S7: excerpts +40.7 % > 25 %, and they stored body text); expired-page wording; the
  `seotester_fix_seo_findings` prompt; Connected applications in SeoTester's SPA.
- S6: fixture site `apps/SeoTester/tools/s6-injection-site/`, server-side dry run, runbook. **Live run: owner.**
- Tests: `SeoToolsTests` (12), `McpToolReachTests` (5), evidence cases in `PageChecksTests`, quota key in
  `SparkQuotaTests`; mutation runs recorded in the decision log.

### M6 — CodeCoverage — done (2026-10-12), except the live run (M7/M9)
As built, with the deviations, in the PRD decision log ("M6 implementation").
- Repository scoping out of `BrowseController` into `IRepositoryScope` (`apps/CodeCoverage/.../Services/RepositoryScope.cs`),
  shared with the tools; `CoverageSources` shared too. Browse answers unchanged.
- `CoverageTools` (PRD §4.8): `list_repositories`, `summary`, `hotspots`, `file`, `patch`, `drops`, read-only, through the
  scope and `IPermissionService`; reads in `ICoverageInsights` (SPARK050 quiet). Hotspots rank by uncovered lines (S8 FAIL,
  [spikes](spark_mcp_spikes.md#s8--does-the-hotspot-ranking-predict-cheap-gains)).
- Embedded setup in the app (`ForMcp()`, issuer and canonical URL from the public origin) plus personal tokens
  (`withConnectedAgents()` in the SPA); `covt_`/OIDC rejected on `/mcp` (D23, test); one prompt
  (`codecoverage_raise_coverage`, new `mcp.AddPrompts<T>()`).
- Found and fixed on the way: agents were invisible to CodeCoverage's owner check (`ISparkCurrentUser.Id` now); any
  `Spark:Mcp` key made `Authentication` explicit (package).
- For M9: no new secret or configuration; the Data Protection volume now guards the signing keys
  ([hosting.md](code-coverage/hosting.md#ai-agents-mcp)).

### M7 — Tests
- Parity suite (S5) over HR, SeoTester and CodeCoverage fixtures (no personal data).
- The full adversarial list of PRD §6.2.
- E2E: the OAuth dance with a scripted client against `SparkAppTestHost`; personal-token flow; S9 budgets.
- Manual checklist, live S3/S11 (owner, moved from M0): Claude Code, Codex ≥ 0.148 and Gemini 0.63 each
  sign in without a hand-registered client (record the registration path taken) and call a tool; with
  anonymous access, a protected call's 401 text leads the model to start the client's sign-in; with
  `RequireSignIn` on, `initialize` 401s and the client signs in at connect. Results into
  `spark_mcp_spikes.md` and the PRD log.

### M8 — Documentation
- Bring `guide-mcp.md`, `guide-mcp-agents.md`, `seotester/mcp.md`, `code-coverage/mcp.md` in line with what
  was built; package README; IdentityProvider README (`ForMcp`); Authorization README (grants, personal
  tokens); `guide-authorization.md` (D18, D19, agent layer); `diagnostics.md`; README guide table; release
  notes with the two behaviour changes.
- Remove the "Draft" banners only after each documented command ran against the real app.

### Sweep and PR
`RAVENDB_LICENSE=... npm run test:affected`, all green, then one PR. Version diff: new package
`11.0.0-preview.*`, ng-spark minor bump only.

### M9 — Deploy (production)
- SeoTester and CodeCoverage: signing key for the embedded server in deployment secrets, public origin,
  `/mcp`, `/connect/*` and `/.well-known/*` through the proxy without SSE buffering. *CodeCoverage (M6):* no signing-key
  secret exists — the IdP generates its keys into `OidcKeys`, protected by the Data Protection key ring, so the
  `dataprotection-keys` volume must persist and be backed up; the issuer and `/mcp` URL follow `Coverage:BaseUrl` /
  `Spark:Auth:PublicBaseUrl` (already set); Traefik routes the whole host (nothing to add)
  ([hosting.md](code-coverage/hosting.md#ai-agents-mcp)).
- Connect Claude Code to both production servers; run each documented example once.
