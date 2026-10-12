# Plan — MintPlayer.Spark.Mcp

PRD: [spark_mcp_PRD.md](spark_mcp_PRD.md) (decisions D1–D24, grill round 1). One branch, one PR,
including both production adopters (D24). Tests run once, after M8 (batched sweep), except where a spike
needs its own run. Red tests for D18, D19 and the §6.2 adversarial list are written before their fixes.

## Sequencing

```
spikes S1–S4, S11 → M1 → M2 → M3a ─┬→ M3b → M4 ─┬→ M5 SeoTester (+S6, S7)
                                   └→ M3c        └→ M6 CodeCoverage (+S8)
                         M7 parity, adversarial, E2E (S5, S9) → M8 docs → sweep → PR → M9 deploy
```

S1–S4 and S11 first: they can change D2, D4, D5 and D17. A failing S11 adds the `RequireSignIn` switch.

## Milestones

### M0 — Spikes S1–S4, S11
Throwaway branch. Results go into the PRD decision log with evidence.

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

### M2 — Mcp package and read tools
- `libs/mcp/MintPlayer.Spark.Mcp`: `AddMcp`, options (`ServerName`, `ToolPrefix`, `Instructions`,
  `Authentication`, `AgentPermissions` with `AgentInformation`, `Path`, `CanonicalUrl`, quotas), SDK 2.2,
  Origin check, cookies ignored, path-collision startup check.
- Generic `discover`, `describe`, `query`, `get`; `SparkMcpResult` (Spark `JsonOptions`, fencing, paging);
  prefixing; ReadOnly → Read / else Write filter; anonymous handling with uniform 401 (D17); per-user and
  per-IP limits; audit log events.
- Analyzer SPARK05x for raw sessions / unchecked calls in tool classes.

### M3a — Authorization package: grants and personal tokens (setup 1)
- `AgentGrant` collection with natural ids (D12), per-call read with short cache and invalidation.
- Personal access tokens (`spat_`, hash, expiry, security-stamp revocation) and their bearer scheme.
- Connected applications section (ng-spark) linked from `/account`: list, edit types and read/changes,
  revoke, create token, last used. An extension point through which the IdentityProvider contributes its
  non-agent OAuth grants (its SPA portal page becomes that contribution).

### M3b — Embedded authorization server (setup 3)
- IdentityProvider `ForMcp()`: RFC 8414, RFC 9207 `iss`, CIMD with SSRF guards, loopback-only anonymous
  DCR with rate limit and 30-day pruning, `mcp:read`/`mcp:write`, audience = canonical URI, refresh
  family revocation.
- Consent screen: client name, verified/unverified, redirect host, type choice, read/changes limited by
  `AgentPermissions(agent)`.
- `OidcGrant` ↔ `AgentGrant` linking; revoke in either revokes both.

### M3c — External authorization server (setup 2)
- Mcp wiring over `AddSparkResourceServer` (+ introspection), RFC 9728 pointing at the external server.
- Mapping through linked external logins only, issuer-bound; unmapped → 401; groups local; shadow grant.

### M4 — Writes
- `McpPlans` collection with document `@expires`; `propose_changes`, `apply_changes(planId, changes)` with
  diff echo check, one-shot, user and grant binding, re-validation, 409 handling, retention after apply.
- HR: contracts `"agentPermissions": "Read, Write"`, app `Read | Write`.

### M5 — SeoTester
- Embedded setup; `SeoTools` (PRD §4.7); `agentPermissions` on `Site`/`AuditRun`/`CheckDefinition`;
  anonymous `QueryRead/CheckDefinition` in security.json.
- Element evidence after S7; expired-page wording; one "fix my SEO findings" prompt; S6.

### M6 — CodeCoverage
- Repository scoping out of `BrowseController` into a shared service.
- `CoverageTools` (PRD §4.8), ranking after S8; `covt_`/OIDC rejected on `/mcp` (D23); one prompt.

### M7 — Tests
- Parity suite (S5) over HR, SeoTester and CodeCoverage fixtures (no personal data).
- The full adversarial list of PRD §6.2.
- E2E: the OAuth dance with a scripted client against `SparkAppTestHost`; personal-token flow; S9 budgets.

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
  `/mcp`, `/connect/*` and `/.well-known/*` through the proxy without SSE buffering.
- Connect Claude Code to both production servers; run each documented example once.
