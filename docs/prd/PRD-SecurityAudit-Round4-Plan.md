# Implementation plan — Security Audit Round 4

Companion to [`PRD-SecurityAudit.md` §11](./PRD-SecurityAudit.md). Same shape as prior rounds: **TDD — a failing test that pins the secure behavior first, then the fix.** One `fix(security)` PR on branch `security-audit`, ordered by severity so the High lands first and can ship alone if triage wants to split.

Every finding below was verified against code on HEAD by an adversarial verifier (see PRD §11 method). Existing e2e security tests live in `tests/MintPlayer.Spark.E2E.Tests/Security/`; unit tests in `tests/MintPlayer.Spark.Tests/`. New tests extend those files where one already covers the surface.

## Ordering & bundling

1. **R4-H1** — query/stream row-level authz bypass *(the one High; fix first)*
2. **R4-M1 + R4-M2** — replication dev-mode mTLS bypass + ETL SSRF *(same file, one workstream)*
3. **R4-M3** — combined-action deny
4. **R4-L1 / L2 / L3 / L4** — lookupref reads, register enumeration, mandatory ETag, WS origin port
5. **Sweep-in from §11.3** — H-1b (read-path visibility) and R2-M6 (webhook replay) are the two highest-value still-open priors; include unless triage defers.

Each step: write the red test, make it green, keep the diff minimal (per the "minimal PR diff" preference — no formatter/schematic churn).

---

## Step 1 — R4-H1: enforce the row-level hook on query + stream paths (HIGH)

**Design decision (PRD triage Q2):** push the per-row filter into a single shared seam so the three read surfaces can't drift again. `DatabaseAccess.FilterByRowLevelAuthAsync` already implements it for `/spark/po`; extract the row-filtering into an `IRowSecurity`-backed helper (it already exists — `QueryExecutor` simply doesn't inject it) and call it from both query executors after the entity-type `EnsureAuthorizedAsync` check.

**Red test** — `tests/MintPlayer.Spark.E2E.Tests/Security/RowLevelAuthzTests.cs` (extend):
- `Query_execute_applies_row_level_authz`: as a non-admin owning only their own `Car`, `GET /spark/queries/{GetCars}/execute` returns only their rows, not the admin's. Fails today (returns all).
- `Query_stream_applies_row_level_authz`: same assertion over the WebSocket stream (`/spark/queries/{id}/stream`).
- `Custom_query_applies_row_level_authz`: same for a `Custom.*` source (e.g. Fleet's `Stolen_Cars`).

**Fix:**
- `libs/spark/MintPlayer.Spark/Services/QueryExecutor.cs` — inject the row-security filter; apply it in `ExecuteDatabaseQueryAsync` (`:126`) and `ExecuteCustomQueryAsync` (`:194`) over the materialized results before mapping/returning.
- `libs/spark/MintPlayer.Spark/Streaming/StreamingQueryExecutor.cs:50` — apply the same filter per batch before yielding (`:84-93`).
- Reuse the exact predicate `DatabaseAccess` uses (`IsAllowedAsync("Query", entity)` via the Actions class) so behavior is identical across `/po`, `/queries/execute`, `/queries/stream`.

**Watch:** custom queries return projections, not always the entity type — the filter must resolve the row's owning entity-type/Actions the same way `DatabaseAccess` does; where a projection can't be mapped back to an entity (the `V*` context-property case noted in project memory), fail closed (exclude) rather than leak.

---

## Step 2 — R4-M1 + R4-M2: replication dev-mode bypass + ETL SSRF (MEDIUM ×2)

**Red tests** — `tests/MintPlayer.Spark.E2E.Tests/Security/ReplicationEndpointAuthTests.cs` (extend):
- `Dev_mode_sync_apply_still_requires_module_and_cert`: host in Development, default options, unauth `POST /spark/sync/apply` with a made-up `RequestingModule` → 401/403; `ISyncActionHandler` never invoked. (Today: 200.)
- `Etl_deploy_rejects_target_url_outside_allowlist`: `DeployAsync` with a `TargetUrls` entry not in the allow-list → error, `PutConnectionStringOperation` never sent. (In-process test against `EtlTaskManager`, mirroring existing `EtlDeployEndpointTests`.)

**Fix (R4-M1)** — `libs/replication/MintPlayer.Spark.Replication/Services/ModuleCertificateValidator.cs`:
- Development branch (`:65-78`) must still load `moduleInformations/{requestingModule}` from `SparkModules` and 403 on unknown (the code comment already claims this — make it true).
- `ResolveMode` (`:45-53`): stop treating `Auto`→`Development` as "no cert". Require explicit `Mode=Disabled` to skip certs; when disabled, the endpoints bind loopback-only (or aren't mapped). Update `SparkReplicationOptions.cs` doc-comment for the `Mode` enum.

**Fix (R4-M2)** — `libs/replication/MintPlayer.Spark.Replication/Services/EtlTaskManager.cs:53-60`:
- Add an operator-configured allow-list on `SparkReplicationOptions` (peer hosts / required `https`). Validate every `TargetUrls` entry before assigning `TopologyDiscoveryUrls`; reject with a clear error otherwise.

**Triage note (PRD Q4):** if dev boxes are always loopback-only, R4-M1 drops toward Low — confirm the deployment posture before deciding how hard to gate.

---

## Step 3 — R4-M3: combined-action DENY expansion (MEDIUM)

**Red test** — `tests/MintPlayer.Spark.Tests/Authorization/` (new `AccessControlServiceDenyTests.cs`, or extend the existing access-control unit tests):
- `Combined_form_deny_blocks_expanded_action`: rights = broad grant `QueryReadEditNewDelete/Car` (Everyone) + combined deny `EditNewDelete/Car` (group G); a member of G requesting `Edit/Car` → **denied**. Fails today (allowed).
- Guard test: an exact-form deny still works (regression).

**Fix** — `libs/authorization/MintPlayer.Spark.Authorization/Services/AccessControlService.cs`:
- In the denial-evaluation step (`:70`), match deny rights via **both** exact `MatchesResource` **and** `IsCombinedActionMatch` (`:136-151`) — i.e. apply the same combined-action expansion to denies that `:85` currently applies only to allows. Deny short-circuits before any grant. Preserve the "denials take precedence" invariant (comment at `:69`).

---

## Step 4 — Lows

### R4-L1 — gate LookupReference reads
**Red test** — `tests/MintPlayer.Spark.E2E.Tests/Security/LookupReferenceAuthTests.cs` (extend): `Anonymous_lookupref_list_is_refused` and `Anonymous_lookupref_get_is_refused` → 401.
**Fix** — `libs/spark/MintPlayer.Spark/Endpoints/LookupReferences/List.cs`, `Get.cs`: inject `IPermissionService`, add `EnsureAuthorizedAsync("Read","LookupReferences")` (mirror the resource used by the R2-H4 `AddValue`/`UpdateValue`/`DeleteValue` gate and the EntityTypes read gate).

### R4-L2 — register account-enumeration
**Red test** — new `tests/MintPlayer.Spark.E2E.Tests/Security/RegisterEnumerationTests.cs`: `Register_response_is_indistinguishable_for_taken_vs_free_email` (status + body shape identical; no address echoed).
**Fix** — `libs/authorization/MintPlayer.Spark.Authorization/Identity/UserStore.cs:60-66`: return a generic failure (don't echo the email; use a non-`DuplicateEmail`-distinguishable result), **or** wrap the mapped `/register` endpoint to normalize its response. Simplest robust option: post-process the register result to a generic 400. Document the choice — this overrides stock `MapIdentityApi` behavior.

### R4-L3 — mandatory optimistic concurrency
**Red test** — `tests/MintPlayer.Spark.E2E.Tests/Security/ConcurrencyTests.cs` (extend): `Update_without_etag_is_rejected` (PUT existing record, no `Etag` → 400/428) **or** `Two_concurrent_etagless_updates_conflict` (second → 409).
**Fix** — `libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs:216`: require `Etag` for updates of existing records (reject when empty), **or** set `session.Advanced.UseOptimisticConcurrency = true` on the write session (`SparkMiddleware.cs:113-114`) so the save is conditional on the loaded change vector — closing the TOCTOU gap between `checkSession` and the save session. Prefer the session flag: it's atomic and removes the separate-session check entirely.

### R4-L4 — WebSocket origin port
**Red test** — `tests/MintPlayer.Spark.E2E.Tests/Security/WebSocketOriginTests.cs` (extend): `WS_rejects_same_host_different_port` (`Origin: https://<host>:<other-port>` → 403).
**Fix** — `libs/spark/MintPlayer.Spark/SparkMiddleware.cs:226-233`: compare the full authority (`Request.Host.ToString()` / host+port, ideally scheme) instead of `originUri.Host` vs `Request.Host.Host`, or match against an explicit allowed-origins list.

---

## Step 5 — Sweep-in from §11.3 (recommended, confirm at triage)

### H-1b — read-path attribute visibility (MEDIUM)
Decide semantics first (PRD Q3). If `IsVisible=false` is a confidentiality control: add a read-side gate symmetric with `IsWritableBySchema` — in `EntityMapper.PopulateAttributeValues` (`:208-230`), omit/null `Value` for `IsVisible=false` attributes (and honor a per-caller `IsAttributeVisibleAsync` hook, the H-1b design). If it's a pure UI hint: stop gating writes on it and document that. Either way, make read and write symmetric.
**Test** — `tests/MintPlayer.Spark.E2E.Tests/Security/AttributeWriteProtectionTests.cs` companion (new `AttributeReadVisibilityTests.cs`): an `IsVisible=false` attribute's value is not present in `GET /spark/po/{type}/{id}` nor in `/spark/queries/{id}/execute`.

### R2-M6 — webhook delivery replay (MEDIUM)
**Test** — `tests/MintPlayer.Spark.E2E.Tests/Security/` (webhook area): `Replayed_delivery_is_dropped` — same signed body + `X-GitHub-Delivery` twice → second is a no-op (no second broadcast).
**Fix** — `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Services/SparkWebhookEventProcessor.cs:35-83`: record processed `X-GitHub-Delivery` IDs in a bounded-TTL store (RavenDB doc with expiry / compare-exchange) and drop duplicates; optionally reject deliveries outside a timestamp window.

**Defer (document, don't fix this PR unless asked):** M-2 (claim-issuer trust) — natural home is the IdentityProvider/OIDC multi-issuer work (PRD Q5); M-4 (`[SparkQuery]` opt-in) — footgun, not reachable; R2-H19 (`navigate` client-op) — inert until a navigate handler ships. Leave a one-line tracking note in each so they aren't lost (per the "out-of-scope follow-through" preference).

---

## Verification

- Per fix: `dotnet test tests/MintPlayer.Spark.E2E.Tests --filter <TestClass>` red → green.
- Full security suite green before PR: `dotnet test tests/MintPlayer.Spark.E2E.Tests --filter FullyQualifiedName~Security` + `dotnet test tests/MintPlayer.Spark.Tests`.
- Do **not** run `ng build`/`ng serve` for any frontend-touching change — the ASP.NET host proxies the dev server; save + live-reload (per repo guidance).
- CI auto-publishes on push to master — land this via PR to `master`, never hand-publish from the branch.
