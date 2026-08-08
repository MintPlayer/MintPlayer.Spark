# Implementation plan — Spark hardening for the Coverage integration (M0)

See [PRD-CoverageHandoff.md](./PRD-CoverageHandoff.md), [findings-replication-mtls.md](./findings-replication-mtls.md) and [findings-identity-provider-audit.md](./findings-identity-provider-audit.md). Base: `master` @ `febea26`. Branch: `feat/spark-hardening-m0`. **Everything ships in a single PR** — the six handoff items *and* the credential/authentication unification (M8–M11).

TDD where there's behaviour to pin: failing test first, then the fix. Per CLAUDE.md, **test suites run once at the end**, not per milestone — intermediate milestones are verified by reading code and type-checking. Committing per milestone is fine.

## Status — resume here

Branch `feat/spark-hardening-m0`, based on `master` @ `febea26`. Working tree clean.

| Commit | What |
|---|---|
| `9a181e6` | PRD + plan + mTLS findings |
| `5220564` | Decisions D1–D6, IdP milestone |
| `7ac799a` | **M1 done** — queue-name derivation (18 tests green) |
| `d51f9fd` | **M12.1 done** — IdP ported to `libs/identity_provider/`, added to sln, builds unchanged |
| `19f4bf2` | **M12.2 done** — PBKDF2 + constant-time secret verify; claim-prefix fix (18 tests green) |
| `dfab40a` | **M12.3 fixes** — natural-id token lookups, no plaintext bearer values, reuse detection |
| `09dc3cb` | **M12.3 fixes** — account takeover, client binding, `ClientType` fail-closed, delegated-claim leak |
| `697097e` | **M12.3 fixes** — consent GET validation, `returnUrl` sanitizing |
| `d994c28` | Audit recorded, M12 re-sequenced |
| `cf85533` | D7/D8 decided, M12.5 spec'd |
| `3f2473e` | **M12.5 done** — server-side request binding; closes O1, O9, O11 (33 IdP tests green) |

| `66ea577` | **O2, O3, O4** — redemption race, CSRF on `/connect/*`, password oracle |
| `9b2958a` | **O5, O6, O7** — `jti` + DB-backed validity, drop `Payload`, issuer from options |
| `643b876` | e2e matrix §A; O2/O3/O4 marked closed; **O26** raised |
| `d473564` | **N1 (Critical), N3** — introspection ownership gate, `token_type_hint` no longer gates the search |
| `d738186` | e2e matrix complete (A/T/L/R, ~200 cases); O3 confirmed closed; **O27** + logout-CSRF decided |
| `f5ccfa6` | **O8, O12, O14, O19, O21, O25** — refresh gating, logout client binding, machine scopes, audiences, URL building, exact client lookup |
| _(next)_ | **O16, O18, O22 (part), O24, O26** — interactive-only auth, constant-time PKCE, `openid`-gated id_token, error codes, server-side required scopes |

**Remaining open:** O10, O13, O15, O17, O20, O22 (the `auth_time`/`azp` half), O23, N2, N4, plus O27 (accepted with a rationale). None is above Medium; the Criticals and Highs are all closed.

**Next real work is M12.6, not more findings.** What is left is a short tail of Medium/Low items, while *every* fix in this branch — two Criticals, a one-click account takeover, a cross-client disclosure — is still reasoned rather than observed. Weigh a day of tail-chasing against the first test that actually exercises `/connect/*`.

**Next action: M12.4's remaining findings**, resuming at **O8** and working down through O25. Everything in the "highest value" tier is now closed (O1–O7), as are O9 and O11.

⚠️ **O7 introduced a required setting.** `SparkIdentityProviderOptions.Issuer` must be configured outside Development or token issuance throws. Any demo or deployment wiring up the IdP needs it — check this before M7.

**Then:** M12.6 tests, M12.7 registration surface.

**Not started:** M2, M3, M8, M9, M10, M11, M6, M7. Note **M9 gates M10 and M11** — Spark endpoints carry no authorization metadata, so a credential scheme registered before the composite default scheme exists is dead code.

**Verification debt:** the full suite (`npx nx run-many --target=test`) has **not** been run — per CLAUDE.md it runs once at the end. Only targeted filters have been run so far (`QueueNamesTests`, `IdentityProvider`), both green. The four demo ClientApps have not been built or exercised since the IdP port.

**No IdP behaviour is tested.** All 33 IdP tests are pure-function unit tests; nothing exercises an endpoint. Every fix from M12.2 onward — the takeover fix, client binding, the redemption race, antiforgery, revocation — is reasoned-correct and unobserved. **M12.6 is not optional polish; it is where this PR's central claim gets evidence.** See M12.6 for the host blocker that gates it.

**Known-unreviewed:** the IdP's signing-key service, JWKS, discovery, UserInfo, and introspection/revocation caller-auth were never audited — the reviewer covering them never reported. Re-run before merge.

## Resolved decisions (2026-08-08)

**D1 — External POST credential: OAuth2 `client_credentials` via `MintPlayer.Spark.IdentityProvider`**, not a per-user secret. Same experience for the consumer, better security posture for the application. **Conditional on the package being audited and proven sound** — see M12. Three defects are already known (unsalted SHA-256 + non-constant-time compare in `VerifyClientSecret`; application claims emitted as `client_group` so a machine token resolves to zero groups; no resource-server side at all).
> ⚠️ **Open — see Q1 in the handover notes.** If `client_credentials` is the upload credential, **M4 (the PAT library) has no consumer.** Confirm whether M4 is dropped, or kept for a different audience.

**D2 — Antiforgery.** CI/workflow posts can't carry XSRF at all, and `client_credentials` is sufficient there → exempt requests not authenticated by an ambient (cookie) credential, keyed on *the scheme that produced the principal*. Separately: Spark **hand-rolls** the XSRF cookie (`SparkMiddleware.cs:48,238-241`) and duplicates `MintPlayer.AspNetCore.SpaServices.Xsrf`, whose `UseAntiforgeryGenerator()` does exactly the same `GetAndStoreTokens` + `XSRF-TOKEN` cookie (`HttpOnly = false`). The demos reference `MintPlayer.AspNetCore.SpaServices` but **not** the `.Xsrf` package. **Adopt the package; delete the duplicate.**

**D3 — Certificate forwarding (my call).** `AddCertificateForwarding` with a **configurable header name**, defaulting to `X-ARR-ClientCert`. Document both Traefik (`passTLSClientCert` → `X-Forwarded-Tls-Client-Cert`, the deployment this repo actually uses) and nginx (`ssl-client-cert`). Ships with a trusted-proxy allowlist — the demos' `KnownProxies.Clear()` must **not** be inherited.

**D4 — `Everyone` stays as-is.** Anonymous vs. authenticated access is already decided by `security.json` and, where needed, the Actions classes. No change. (My "machine caller" phrasing meant a non-human client such as CI; the point stands but needs no special-casing.)

**D5 — Replication → `IPermissionService` needs no new hard dependency.** `IPermissionService`, `IAccessControl` and `IGroupMembershipProvider` live in **`MintPlayer.Spark.Abstractions/Authorization/`**, and `MintPlayer.Spark.Replication.csproj:30` **already references** `MintPlayer.Spark.Abstractions`. Routing `/spark/sync/apply` through the permission pipeline therefore adds **zero** coupling to the Authorization package — that package supplies the `security.json` implementation, not the abstraction.

**D7 — The stored authorization request also carries `OidcAuthorization.Id`.** Consent creates the authorization, writes its id onto the request record, and code issuance reads it from there. This makes **O1 fall out of M12.5** rather than needing a separate fix: today `AuthorizationId` is hardcoded `""` at issuance, which silently kills `Revocation`'s access-token cascade (it has never executed once) *and* the reuse-detection chain revocation added in `dfab40a`. Threading a parameter through `GenerateCodeAndRedirectAsync` would work but leaves the same "remember to pass it" fragility that caused the original bug — the request record is the natural home for state the flow accumulates.

**D8 — The request document uses the natural-id pattern**, `OidcAuthorizationRequests/{sha256(request_id)}`, matching `OidcTokenReference`. Gives point-load consistency (no index staleness on a security decision), single-use enforcement by document existence, and no plaintext handle at rest — the same three properties that fix bought for tokens. One storage idiom across the package rather than two.

**D6 — Row-level scoping is the Actions classes' `IsAllowedAsync(string action, T entity)`**, which already exists (`DefaultPersistentObjectActions.cs:98`). M5 is what makes it actually enforced on the query and stream paths. **Phase D collapses into M5** — no separate design exercise. The only residue is that `security.json`'s *property-level* rights are documented but dead (`MatchesResource` is exact string equality); that becomes a doc fix in M6.

## Sequencing

M1 first: it's the only actively-breaking item, and landing it makes WebhooksDemo runnable again, which M2's manual verification depends on. The contained fixes (M2, M3) follow, then the two large pieces. M6 (docs) is last so it describes what actually shipped — the queue-name format M1 settles and the Actions contract M5 changes.

Because this is one large PR, **keep each milestone a separate, self-contained commit** so the diff stays reviewable and bisectable. M5 in particular should be read as its own unit.

| Milestone | Item | Size |
|---|---|---|
| M1 | Queue-name derivation | Medium |
| M2 | External-login popup | Medium |
| M3 | ng-bootstrap 22.13.0 | Small |
| M8 | mTLS quick fixes (F1, F2, F5, F6) | Small |
| M9 | **Scheme plumbing — composite scheme + antiforgery** | Medium, **high blast radius** |
| M12 | Port + **audit** the IdentityProvider (client_credentials) | Large |
| M4 | API tokens package — **pending Q1** | Large |
| M10 | Credential handlers (cert, cert-forwarding, JWT resource server) | Medium |
| M5 | Row-level authz on queries + stream | Large |
| M11 | Retire the authorization bypasses | Medium |
| M6 | Documentation | Small |
| M7 | Release | Small |

**Ordering constraints that are not negotiable:**
- **M9 gates M4, M10 and M11.** Spark's endpoints carry no authorization metadata, so extra schemes never run until a composite default scheme exists ([findings](./findings-replication-mtls.md) F7). A credential handler merged before M9 is dead code on Spark's endpoints. M4 moved after M9 for this reason.
- **M5 before M11** — M11 routes the sync write path through the chokepoint M5 establishes.
- M8 is independent and cheap; do it early to get the silent-auth-bypass fixes in regardless of what happens downstream.

**The two riskiest milestones are M9 and M5.** M9 changes the default authenticate scheme and the antiforgery gate for *every* existing Spark app, and its failure mode is silent (wrong scheme → anonymous principal → `Everyone` rights) rather than a build break. It needs a deliberate regression sweep across all four demos, not just green tests.

---

## M1 — Queue-name derivation

### M1.1 — Failing tests

`tests/MintPlayer.Spark.Tests/Messaging/QueueNamesTests.cs` (new). `InternalsVisibleTo` for `MintPlayer.Spark.Tests` is already granted at `MintPlayer.Spark.Messaging.csproj:29`, so `internal static class QueueNames` is directly reachable — no new plumbing.

Cases:
- Non-generic type → `FullName`, unchanged (pins the existing `MessageBusTests.BroadcastAsync_persists_a_SparkMessage_with_inferred_queue_name_and_payload` behaviour).
- Nested non-generic type → `Outer+Inner` still valid (`+` is allowed).
- Closed generic, no attribute → passes validation; contains no `[`, `]`, `,`, `=` or whitespace.
- Closed generic with **two** type arguments → joined with `-`, never `,`.
- **Generic-of-generic** (`Foo<Bar<Baz>>`) → still valid, and distinct from `Foo<Baz>`. This is the case that rules out the simple-name shortcut.
- `[MessageQueue("…")]` still wins over derivation.
- Producer/consumer agreement: the name `MessageBus` would store equals the name `MessageSubscriptionManager` would discover, for the same closed generic. **This is the actual correctness bar** — not merely "doesn't throw."

### M1.2 — Regression test for the real failure

`tests/MintPlayer.Spark.Tests/Messaging/` — model on `MessageSubscriptionManagerLifecycleTests.cs`, which already builds a `ServiceCollection` + `AddSparkMessaging()` + an `IRecipient<T>` registration + resolves `MessageSubscriptionManager` as `IHostedService`.

Register `IRecipient<TestGeneric<SomeArg>>` with no `[MessageQueue]` **alongside** a valid non-generic recipient — reproducing WebhooksDemo's mixed-queue shape (PRD §1). Assert `StartAsync` completes and the manager's execute task does not fault. Red today, green after M1.3.

### M1.3 — `QueueNames`

New `libs/messaging/MintPlayer.Spark.Messaging/Services/QueueNames.cs`, `internal static`:

- `ForMessageType(Type)` — `attribute?.QueueName ?? SafeName(type)`.
- `SafeName(Type)` — **one recursive function** (PRD §1): non-generic returns `FullName!` (base case, so non-generic names are unchanged for free); generic returns `{definition.FullName}-{args joined "-"}` with each argument passed through `SafeName` **recursively** — not `Type.Name`, which mis-derives `Foo<Bar<Baz>>`. Never `,` as separator. Ends with a defensive sanitize (residual disallowed char → `_`) covering runtime-emitted proxy types.
- `IsValid(string)` — `IsValidQueueName` moved here verbatim from `MessageSubscriptionWorker.cs:60-73`.
- Cache per `Type` via the existing `ReflectionCache` pattern (`MessageSubscriptionWorker.cs:129-134`) — this runs per broadcast and per discovery scan.

### M1.4 — Rewire call sites

- `MessageBus.cs:34-36` → `QueueNames.ForMessageType(messageType)`.
- `MessageSubscriptionManager.cs:107-108` → same.
- `MessageSubscriptionWorker.ConfigureSubscription` → `QueueNames.IsValid`; delete the local copy.

### M1.5 — Fix the stale exception message

`MessageSubscriptionWorker.cs:51-52` advertises `[A-Za-z0-9._-]+`, which is already wrong — it omits `+` and `` ` ``, both allowed. Correct it to match `IsValid`.

### M1.6 — Delete dead code

Delete `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Messages/GitHubQueueNames.cs`. It has zero call sites and, per PRD §1, cannot be resurrected usefully — the consumer side can't reach it. Leave `SparkWebhookEventProcessor.HandleWebhookAsync` alone; the general fix covers it. Update its now-inaccurate comment at `SparkWebhookEventProcessor.cs:126-135`.

---

## M2 — External-login popup

### M2.1 — Failing tests

`tests/MintPlayer.Spark.Tests/` — extend the existing `MapSparkIdentityApiTests.cs` / `ExternalLoginCallbackTests.cs`:
- `/spark/auth/external-login?…&popup=1` → redirect `Location` contains `popup=1`. (Today `MapSparkIdentityApiTests.cs:96-97` only asserts it contains `external-login-callback`; the propagation itself is untested — this is the actual bug.)
- Callback **failure** path with `popup` set → returns postMessage HTML, not a redirect. One test per branch (`info is null`, unverified email, `CreateAsync` failure).

### M2.2 — Server

`libs/authorization/MintPlayer.Spark.Authorization/Extensions/SparkAuthenticationExtensions.cs`:
- `/external-login` (~107-121): accept `popup` from the incoming query and append it to the callback URL built at `:118`. Plain query string — **not** OAuth `state` (PRD §3: that URL never reaches the provider).
- Failure branches `:139`, `:167`, `:181`: when `popup` is set, emit the postMessage HTML with `{ type: 'spark:external-login', success: false, error }` instead of `Results.Redirect`.
- Success branch `:208-223`: payload becomes `{ type: 'spark:external-login', success: true }`. Keep `targetOrigin` as `window.location.origin`.

### M2.3 — Library method

`libs/node_packages/ng-spark-auth/core/src/spark-auth.service.ts` — add `loginWithProvider(provider, { returnUrl?, mode?: 'popup' | 'redirect' })`, matching the file's existing async/`firstValueFrom`/`config.apiBasePath` style. It owns: URL construction from `config.apiBasePath` (not a hardcoded path), `window.open`, the `message` listener with an origin check, **unconditional cleanup** from every exit path, manual-close detection via a `closed` poll, and the post-login `checkAuth()`.

Add unit tests under the package's Vitest setup.

### M2.4 — Demo

`Demo/WebhooksDemo/WebhooksDemo/ClientApp/src/app/shell/shell.component.ts:55-73` — delete the hand-rolled `window.open` + listener; call `loginWithProvider('GitHub', { returnUrl: '/github-projects' })`. No `window.open` or `addEventListener` left in app code.

---

## M3 — ng-bootstrap 22.13.0

### M3.1 — Dependency

Root `package.json:30` → `"@mintplayer/ng-bootstrap": "^22.13.0"`. Then `npm install` **from the repo root only** (single `node_modules`, npm workspaces).

No `overrides` entries needed — `lit`, `@mintplayer/web-components`, `ng-click-outside`, `ng-focus-on-load` are already installed at satisfying versions (PRD §4). `ng-swiper` drops out of the tree on its own; remove it from `package-lock.json` via the install, not by hand.

### M3.2 — Accordion migration (8 files)

For each of `Demo/{DemoApp,Fleet,HR,WebhooksDemo}/*/ClientApp/src/app/shell/shell.component.{ts,html}`:
- `.ts`: `BsAccordionTabHeaderComponent` → `BsAccordionTabHeaderDirective`, in both the import statement and the `@Component.imports` array.
- `.html`: `<bs-accordion-tab-header>…</bs-accordion-tab-header>` → `<ng-container *bsAccordionTabHeader>…</ng-container>` (**structural** directive).

Line refs: DemoApp `.ts:5,16` / `.html:12,16`; Fleet `.ts:5,21` / `.html:25,29`; HR `.ts:5,21` / `.html:25,29`; WebhooksDemo `.ts:5,20` / `.html:42,46`.

### M3.3 — Visual check

Headers now render into a named shadow-DOM slot rather than light-DOM projection, so the mechanical diff does not guarantee identical rendering. Start each demo host (`dotnet run`, which spawns the dev server itself — **never** `ng serve`) and confirm sidebar expand/collapse and header content in all four.

---

## M4 — API tokens package

### M4.1 — Project skeleton

`libs/authorization/MintPlayer.Spark.Authorization.ApiTokens/` with a csproj modelled on `MintPlayer.Spark.Authorization.csproj` (`<Version>10.0.0-preview.41</Version>` — matching the current line; the release bump happens once in M6). **Add a project entry to `MintPlayer.Spark.sln`** — required, or CI's bare `dotnet restore` skips it and the `--no-restore` build fails. Add `<InternalsVisibleTo Include="MintPlayer.Spark.Tests" />`.

> **Before starting M4, re-read `C:\Repos\Coverage\Coverage\ApiTokens\`.** Coverage is being developed concurrently; that directory appeared mid-way through writing this plan and is the reference implementation for M4.2–M4.4.

### M4.2 — Document + service

`Identity/SparkApiToken.cs` — id `ApiTokens/{sha256-hex}`, plus `Prefix`, `Scopes`, `CreatedByUserId`, `CreatedOnUtc`, `ExpiresOnUtc?`, `RevokedOnUtc?`.

`Services/ApiTokenService.cs` — port from `Coverage/ApiTokens/ApiTokenService.cs`, generalizing the prefix to a configurable option:
- `GenerateTokenValue()` — `{prefix}` + base64url of `RandomNumberGenerator.GetBytes(32)` (`+`→`-`, `/`→`_`, trailing `=` trimmed).
- `Hash(value)` — `Convert.ToHexStringLower(SHA256.HashData(...))`; the hash **is** the document id.
- `LooksLikeToken(value)` — cheap prefix+length pre-filter, checked *before* hashing.
- Plus issue / validate / list-by-user / revoke over `IAsyncDocumentSession`, mirroring `UserStore`/`RoleStore` — **not** the `PersistentObject` pipeline.

Tests: round-trip issue→validate, revoked rejected, expired rejected, unknown rejected, and that the plaintext never appears in the stored document.

### M4.3 — Authentication handler

`Authentication/ApiTokenAuthenticationHandler.cs` — port from `Coverage/ApiTokens/ApiTokenAuthenticationHandler.cs`. Plain `AuthenticationHandler<AuthenticationSchemeOptions>`; no bespoke options class needed.

**The `NoResult()` discipline is the load-bearing detail** — return `AuthenticateResult.NoResult()` when the header is missing, isn't `Bearer`/`Token`, or lacks our prefix, so cookie and other bearer schemes still get their turn. Reserve `Fail()` for a token that *is* ours but is unknown or revoked. This is what makes three schemes coexist without per-endpoint configuration.

Claims: namespaced types (`{prefix}:scope`, `…:hash`, plus app-defined ones), emitting optional claims only when present.

**Scope→group mapping is a required decision, not a detail** (PRD §2 risk 2). `ClaimsGroupMembershipProvider.cs:19-26` reads only `"group"`/`"groups"`/role claims, so scope claims alone grant nothing. Default: emit scopes as scope claims *and* document that apps map scopes→groups via the existing `AddGroupMembershipProvider<TProvider>()` (`SparkAuthorizationExtensions.cs:66-78`). Don't let it look automatic.

### M4.4 — Registration

`Extensions/ApiTokenBuilderExtensions.cs` — `AddApiTokens(this ISparkBuilder builder, …)`, **not** an `IdentityBuilder` extension. Coverage's working code registers the scheme outside the Identity pipeline (`Coverage/Program.cs:83-84`):

```csharp
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(SchemeName, null);
```

A PAT scheme is orthogonal to Identity provider configuration, so the `configureProviders`/`IdentityBuilder` route the handoff assumed is both unnecessary and wrong-layer. This was M4's biggest unknown and is now closed (PRD §2 risk 1).

### M4.5 — Endpoints

`Endpoints/ApiTokensGroup.cs` (`Prefix => "/spark/auth/tokens"`) + `IssueToken` / `ListTokens` / `RevokeToken`, following the `IEndpointBase` pattern. Cookie-authenticated (these are user-facing management ops, not the CI-facing token path), with `RequireAntiforgeryTokenAttribute(true)` stamped directly on each mutating endpoint as `Logout.cs:14` does.

Integration tests: issue via cookie auth, then authenticate a request with the returned token; revoke, then confirm the token no longer authenticates; confirm an anonymous caller cannot issue.

### M4.6 — Demo wiring

Coverage's M0 exit criterion is *"a demo app can mint and authenticate with an API token."* Wire it into one demo (WebhooksDemo, which already uses `spark.AddAuthentication<SparkUser>(configureProviders: …)` at `Program.cs:29-30`) and add a token-authenticated endpoint to prove the scheme end to end.

---

## M5 — Row-level authorization on queries and `/stream`

See PRD §5 for the verified findings. Two bypasses, one root cause. This changes the public Actions contract, so it needs its own commit and its own release note.

### M5.1 — Failing tests

`tests/MintPlayer.Spark.E2E.Tests/Security/RowLevelAuthzTests.cs` — the existing suite passes only because it exercises `/spark/po` (`:71`). Extend it, red first:

- A row denied by `IsAllowedAsync` is absent from `/spark/queries/{id}/execute` over the same data — and `totalItems` reflects the **post-filter** count.
- Same for the WebSocket `/stream` path.
- Fleet regression, the concrete in-tree exploit: a non-admin querying `GetCars` sees only their own cars, matching what `/spark/po` returns.
- WebhooksDemo regression: `GitHubProjectActions`' org-scoping actually filters the list endpoint (it currently no-ops — see M5.3).
- Fail-closed: a projection row whose base document won't load is **denied**, not emitted.

### M5.2 — Unify the read paths

Introduce a single enforcement component that every read path funnels through — PO get, PO list, query execute (both `Database.*` and `Custom.*`), stream, and breadcrumbs. Today each grew its own ad-hoc authorization and two of four ended up with none; patching the two call sites without unifying just re-creates the drift.

Keep `IsAllowedAsync(string, T)` as the authoritative per-instance gate. Add a composable filter to `DefaultPersistentObjectActions<T>`:

```csharp
public virtual Expression<Func<T, bool>>? GetRowFilter(string action) => null;
```

Detect which of the two is overridden by reflection (`method.DeclaringType != typeof(DefaultPersistentObjectActions<T>)`), cached in the existing `ReflectionCache`. Four states:

- **Neither** → no row policy, no filtering, zero cost. Most apps.
- **`GetRowFilter` only** → composed into the `IQueryable` before materialization; total from `Statistics(out var stats).TotalResults`.
- **`IsAllowedAsync` only** → post-materialization filter (today's PO behavior). Correct, O(collection). Emit a startup warning naming the type.
- **Both** → compose the predicate *and* run the per-instance gate as backstop. Predicate is the optimization; the gate is the truth.

Single-document paths (get/edit/delete by id) always use `IsAllowedAsync` — there's no query to shape.

**Design the chokepoint for writes too.** A third bypass of the same root cause exists on the write side — `SyncActionHandler` reflectively invokes `OnSaveAsync`/`OnDeleteAsync`, skipping `DatabaseAccess`'s `EnsureAuthorizedAsync` entirely ([findings-replication-mtls.md](./findings-replication-mtls.md) F4). Migrating the sync path onto the chokepoint is **not** committed scope here, but the component must be shaped so it can be, rather than being read-path-only by construction.

**Projection fallback:** `Database.Cars` queries `VCar` via `Cars_Overview`, not `Car`. When a predicate typed on the entity can't compose into the projection, fall back automatically to post-filter with batched base-document reload (`LoadAsync(string[])`) — **never** silently unfiltered — and emit a diagnostic. Fleet's main query exercises this on day one.

### M5.3 — Delete `OnQueryAsync`, migrate its one consumer

Remove the dead hook from `Actions/IPersistentObjectActions.cs:18` and `Actions/DefaultPersistentObjectActions.cs:21`, and fix the false comment at `DatabaseAccess.cs:142` that recommends it.

Migrate `Demo/WebhooksDemo/WebhooksDemo/Actions/GitHubProjectActions.cs:17` to `GetRowFilter`. Note this **tightens** WebhooksDemo — the override is currently a no-op, so its list endpoint returns every project regardless of org membership.

### M5.4 — Flip the fail-open branches

`RowSecurity.cs:30` (hook not found), `DatabaseAccess.cs:169` (no readable `Id` on a projection → currently returns everything unfiltered), `:179` (empty id), `:181` (base doc failed to load), `:441` (unknown hook shape). All become fail-closed: *unevaluable ≠ permitted*. Custom queries returning element types with no resolvable `Id` on a row-policy type are now denied — document the escape hatch.

### M5.5 — Stream path

Filter `batchList` at `StreamingQueryExecutor.cs:87`, before breadcrumb resolution and mapping. `StreamingDiffEngine` diffs against the previously emitted set, so a row that *becomes* invisible mid-stream is emitted as a `remove` for free. Re-evaluate the one-shot type-level gate at `:50` in the same pass (partially closes the still-open R2-M4).

### M5.6 — Record the finding

Add it to `docs/prd/PRD-SecurityAudit.md` under a properly-numbered heading. "R4-H1" is fabricated and there is no written record of this anywhere in the repo.

---

## M8 — mTLS quick fixes

Independent of everything else; land early. See [findings](./findings-replication-mtls.md).

- **F1** — `Development` mode claims to verify the module is registered and doesn't (`ModuleCertificateValidator.cs:65-78`). Implement the lookup (the comment describes the better behavior). Fix the same false claim at `guide-replication-mtls.md:29`. Decide whether `Disabled` survives at all — today it returns `Ok` with *no log line*.
- **F1 tests** — `ModuleCertificateValidatorTests.cs:33` passes `registrationService: null!` while asserting a "known module" is OK, proving the opposite of its name. `ReplicationEndpointAuthTests.cs:24-50` asserts `BeOneOf([Forbidden, Unauthorized])`, loose enough to pass under either mode. Tighten both so the Development branch is actually pinned.
- **F2** — the guide documents `Spark:Replication:ClientCertificate:*`; the demos bind a different section, so the documented config binds to nothing. Make the guide and the binding agree.
- **F5** — `IReplicationHttpClientProvider` is registered and never resolved; `PerTargetOverrides` is documented but non-functional. Wire it or delete it and correct the guide.
- **F6** — `ModuleCertificateValidator` constructs a fresh `DocumentStore` per validation. Cache it.

## M9 — Scheme plumbing (gates M4, M10, M11)

The prerequisite. Nothing downstream functions without it.

### M9.1 — Composite authenticate scheme

A Spark-owned composite handler: try each registered credential scheme in turn, first `Success` wins, `NoResult` falls through — mirroring the framework's own `CompositeIdentityHandler`. Set as `DefaultAuthenticateScheme` in `AddSparkAuthentication` (`SparkAuthenticationExtensions.cs:44-49`), which today inherits `Identity.BearerAndApplication` implicitly and is never overridden.

Prefer the composite over `AddPolicyScheme` + `ForwardDefaultSelector`: the latter sniffs and selects exactly one scheme, whereas handlers already return `NoResult` correctly, which is what a composite wants.

### M9.2 — Registration surface

`spark.AddCredentialScheme<THandler>("Name")` so M10's handlers register into the composite rather than each app wiring `AddAuthentication().AddScheme<>()` by hand.

### M9.3 — Antiforgery exemption + adopt the XSRF package (per D2)

`SparkMiddleware.cs:181-201` enforces double-submit on mutating requests unconditionally. Exempt requests **whose principal came from a non-cookie scheme** — decide from what authenticated the request, not from what headers or cookies happen to be present, so the gate can't be suppressed by request shape.

Also replace the hand-rolled XSRF cookie generation at `SparkMiddleware.cs:238-241` with `UseAntiforgeryGenerator()` from **`MintPlayer.AspNetCore.SpaServices.Xsrf`** (`C:\Repos\MintPlayer.AspNetCore.SpaServices\MintPlayer.AspNetCore.SpaServices.Xsrf`). It does the identical `GetAndStoreTokens` + `XSRF-TOKEN` cookie with `HttpOnly = false`. Keep `AddAntiforgery(opt => opt.HeaderName = "X-XSRF-TOKEN")` (`:48`) — the package supplies the cookie, not the header config. Confirm the package is published to NuGet at a compatible version before taking the dependency.

### M9.4 — Regression sweep

Non-negotiable, because the failure mode is silent. Verify against all four demos that browser login still works, that mutating PO requests still require XSRF, and that an unrecognised credential still lands as anonymous rather than erroring. Add a test asserting the default scheme is the composite.

## M10 — Credential handlers

Each handler's entire authorization integration is emitting `new Claim("group", "…")`.

- **M10.1 — Client certificate.** `AddCertificate()` + `OnCertificateValidated`, lifting the pinning and mode ladder from `ModuleCertificateValidator.cs:45-110`. Derive identity from the cert's `CN` (the guide's own generation recipe sets `CN=$MODULE`) rather than the request body — same guarantee, no schema change, removes body-trust. Requires `AllowedCertificateTypes = CertificateTypes.All` and `ValidateCertificateUse = false` or chain validation rejects the self-signed CA the guide tells operators to create. Keep the lookup live (or short-TTL): a cert authenticates the *connection*, so under keep-alive a cached ticket would outlive a revoked pin.
- **M10.2 — Certificate forwarding.** `AddCertificateForwarding` for proxy-terminated TLS, **with a trusted-proxy allowlist**. The demos currently `KnownProxies.Clear()`; inheriting that posture here would let anyone forge a cert header. Without this the cert scheme cannot work in this repo's own Traefik deployment. **See open question Q3 (which header formats).**
- **M10.3 — ClientId/Secret consumer side.** Per D1 this is a **JWT-bearer resource-server scheme** validating against the IdP's JWKS, mapping token claims → group claims. The issuing side is M12. This half does not exist on the IdP branch and is a genuine build either way.

## M12 — Port and audit `MintPlayer.Spark.IdentityProvider` (per D1)

The `client_credentials` issuer. **The audit is the deliverable, not a formality** — the user's condition is that the package "works exactly as it's supposed to and doesn't have vulnerabilities."

### M12.1 — Port to `master`

The branch predates the `libs/` reorg (package sits at repo root), Angular 22, and the breadcrumbs redesign. Move to `libs/identity_provider/`, add to `MintPlayer.Spark.sln` (required — CI's bare `dotnet restore` skips anything not in the sln), align to `10.0.0-preview.41`.

### M12.2 — Fix the three known defects

1. `VerifyClientSecret` (`Endpoints/Token.cs`) uses **unsalted single-round SHA-256** and `string.Equals(Ordinal)`, which is **not constant-time**. Replace with PBKDF2/Argon2 + salt and `CryptographicOperations.FixedTimeEquals` — the pattern `libs/webhooks/.../SignatureService.cs:36` already gets right.
2. `OidcTokenGenerator.GenerateAccessToken` emits application claims as `client_{Type}`, so `{Type:"group"}` becomes `client_group` and never matches `ClaimsGroupMembershipProvider.GroupClaimTypes` → **a machine token authorizes as nobody**. Map to real group claims.
3. No resource-server side exists (that's M10.3).

### M12.3 — Security audit ✅ done (partially)

Findings recorded in **[findings-identity-provider-audit.md](./findings-identity-provider-audit.md)**: 11 fixed (4 Critical, 6 High, 1 Medium), 25 open, plus one unreviewed surface.

**Fixed** (`19f4bf2`, `dfab40a`, `09dc3cb`, `697097e`): client-secret crypto; the `client_group` claim defect; authorization-code replay via stale index; plaintext bearer values at rest; refresh reuse detection; `/connect/consent` validating nothing (account takeover); codes and refresh tokens not bound to the redeeming client; `ClientType` failing open; application claims leaking into delegated tokens; `returnUrl` open redirects.

**Not audited — the reviewer never reported:** `OidcSigningKeyService`, `Jwks`, `Discovery`, `UserInfo`, and the `Introspection`/`Revocation` caller-auth model. **Re-run before merge.**

### M12.4 — Close the open findings

In the order given in the findings doc:

1. **O1 — populate `AuthorizationId`.** It is hardcoded `""`, so `Revocation`'s access-token cascade has never executed once *and* the reuse-detection chain revocation added in `dfab40a` currently revokes only the presented token. One change, two dead paths revived. **Do first.**
2. **O2 — optimistic concurrency** on redemption. The point-load fixed replay-by-staleness, not replay-by-concurrency.
3. **O3 — antiforgery on the three `/connect/*` POSTs**, following `Authorization/Endpoints/Logout.cs`.
4. **O4 — `lockoutOnFailure: true`** plus rate limiting; `isPersistent` from a checkbox.
5. **O5/O6 — `jti` on access tokens, introspection consults the database, stop persisting `Payload`** (written 3×, read 0×).
6. **O7 — issuer from options**, not the `Host` header.
7. O8–O17 (Medium), then O18–O25 (Low).

### M12.5 — Bind the authorization request server-side — **DONE**

The structural fix (findings §3). The same "re-derive the request from browser input" defect appeared in **five** places and all five had been individually patched — which is exactly why this was worth doing: the sixth page added would have been wrong again and nothing would have failed loudly.

**Outcome:** the consent hop now carries one input, an opaque `request_id`. `/connect/consent` reads no client, redirect URI, scope, challenge, nonce or state from the browser — all of it comes from the stored request. **Closes O1, O9, O11.** Two further consistency defects were found and fixed while in here (see below). 33 IdP tests green; package builds clean.

Per **D7** and **D8**. What landed:

**New `Models/OidcAuthorizationRequest.cs`** — `Id` (natural, per D8), `ApplicationId`, `Subject`, `RedirectUri`, `Scopes`, `CodeChallenge`, `CodeChallengeMethod`, `Nonce`, `State`, `AuthorizationId` (filled by consent, per D7), `CreatedAt`, `ExpiresAt` (~10 min), `Status`.

**New `Services/OidcRequestReference.cs`** — mirror `OidcTokenReference`: `GenerateValue()`, `DocumentId(value)`. The hash/generate primitive moved into a shared internal `Services/OpaqueHandle.cs` so the two facades cannot drift; each names its own collection so no caller ever passes a prefix around.

**`Authorize.Handle`** — after the existing validation (`client_id`, `redirect_uri`, `RequirePkce`, `S256`, `AllowedScopes`, `Enabled`, plus **O11**'s missing `AllowedGrantTypes` check while here), store the request and redirect to `/connect/consent?request_id=<opaque>`. The auto-approve path (`ConsentType == "implicit"`) writes the request too, so code issuance has one source of truth.

**`Consent.HandleGet` / `HandlePost`** — read `request_id` only. Point-load the request; reject if missing, expired, not `valid`, or belonging to another subject. Render from the record. The POST carries `request_id` + decision + the scope checkboxes, which are intersected against `request.Scopes` (already validated) rather than re-validated from scratch.

**`GenerateCodeAndRedirectAsync`** — take the request document instead of eight loose parameters, and copy `AuthorizationId` from it onto the code (this is O1's fix).

**Mark the request consumed** when the code is issued, so it is genuinely single-use.

**Lifetime.** Requests live in RavenDB alongside everything else, in collection `OidcAuthorizationRequests`, in the database from `options.RavenDb.Database` — there is one document store per app. `ExpiresAt` (10 min) is enforced on read, so an expired request is refused whether or not the document still exists. Physical removal is by RavenDB's own expiration feature: the document carries `@expires`, and the IdP enables `ConfigureExpirationOperation` at startup with the same `DeleteFrequencyInSec` as Messaging (they write the same database-level setting, so they must agree). No sweeper service, and nothing accumulates — otherwise this collection would grow by one dead document per sign-in, forever.

Afterwards the per-hop `redirect_uri`/scope/PKCE checks added in `09dc3cb` and `697097e` become redundant belt-and-braces. **Keep them** — they cost nothing and they fail closed if a future path ever reintroduces a parameter-carrying hop.

Removes the `nonce`/`code_challenge` tampering surface entirely.

**Two defects found while implementing — both fixed here:**

- **The grant record was itself read through a stale index.** `OidcAuthorizations_BySubjectAndApplication` backed *two* security decisions: whether to skip the consent screen, and which authorization a code belongs to. Eventual consistency meant a consent revoked moments earlier could still satisfy the skip check, and concurrent authorize requests could each miss the other's write and create rival grant records — splitting the very token chain revocation sweeps by `AuthorizationId`. Fixed by giving `OidcAuthorization` a natural id derived from `(subject, applicationId)` (new internal `Services/OidcAuthorizationReference.cs`), which makes "one grant per user per application" true by construction. The index is deleted and its registration removed. This is the same reasoning as D8, applied one document further; O9 is closed by *this*, not by the request handle alone.
- **The composite id was not injective.** The first cut hashed `$"{subject} {applicationId}"`, under which `("x y", "z")` and `("x", "y z")` collide — one user's grant answering for another's. Now length-framed. Caught by writing the test, not by review; `OidcReferenceTests` pins it.

Re-consent now reinstates a revoked grant (`Status` back to `valid`, `RevokedAt` cleared). That is the correct reading of the user's action, and it is the only sane behaviour once the id is fixed per pair — previously a revoked row simply left an orphan behind and a fresh one was created.

**Spike:** confirmed, not run as a separate exercise — `Authorization/Identity/RoleStore.cs:147-152` already stores natural-id documents (`SparkRoles/{name}`) under the same conventions, and `AsyncDocumentIdGenerator` (`SparkMiddleware.cs:75-79`) is only consulted when `Id` is null. Two existing precedents settle it.

### M12.6 — Tests

**Correction to an earlier assumption in this plan:** it said "assume no coverage on security-relevant paths", which is wrong about the *repo* and right only about the *IdP*. `tests/MintPlayer.Spark.E2E.Tests/Security/` already holds ~14 security tests against a real Fleet host running over HTTPS on a random port (`FleetTestHost`, `FleetE2ECollection`), including `ConcurrencyTests`, `XsrfCookieFlagTests`, `ReturnUrlValidationTests` and `ReplicationEndpointAuthTests`. Extend that suite — do not build a parallel one.

**Current IdP coverage is 33 tests, all pure functions** (`ClientSecretHasherTests`, `OidcReferenceTests`) in `MintPlayer.Spark.Tests`. Zero exercise an endpoint, a session, or a request. Everything M12.2–M12.5 fixed is reasoned-correct, not observed-correct.

#### Host blocker — RESOLVED, and the earlier answer here was wrong

**Use `SparkEndpointFactory<TContext>` (`libs/testing/`), in-process, from `MintPlayer.Spark.Tests`.** Not Fleet, not a new host project, no subprocess, no Angular bundle.

It already does everything this needs: boots a Spark host on `TestServer` against a supplied `IDocumentStore`, writes per-test model JSON into a temp content root, exposes `CreateClient()` / `GetService<T>()`, and — the part that matters most here — `MintAntiforgeryAsync()`, which performs the warmup GET and returns the cookie header plus `X-XSRF-TOKEN` for mutating requests. `MintPlayer.Spark.Tests` already references both it and the IdP.

One change was needed and has landed: `SparkEndpointFactory` took `configureServices` but the Spark builder action was fixed, so a caller could add *services* but not *modules* — and authentication and the identity provider are both `ISparkBuilder` extensions. It now also takes `configureSpark`, invoked inside `AddSpark`. Endpoints and middleware a module registers on the builder's registry flow into the pipeline on their own, so `/connect/*` is served with no further plumbing.

```csharp
new SparkEndpointFactory<TestContext>(store, models, configureSpark: spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddIdentityProvider(o => o.Issuer = "https://idp.test");
});
```

**Why the previous answer ("Fleet enables the IdP from configuration") was wrong.** I had not read `libs/testing/` when I wrote it. Two things follow from actually reading it:

1. **The Fleet route is far more invasive than it looked.** `AddSparkFull` is *source-generated* (`SparkFullGenerator.Producer.cs`), gated on feature flags fed from a `.targets` file. Adding the IdP means editing a source generator, its targets, `SparkFullOptions`, and taking a new `ProjectReference` on a **shipped** package — real blast radius on the published dependency graph, in order to test something.
2. **The cost argument for Fleet evaporates.** It rested on the shared collection fixture amortising the host start. In-process `TestServer` has no host to start, no `dotnet run` subprocess, and no `npm run build`, so it is faster than Fleet *and* isolated per test.

The reviewer's suggestion of a dedicated `IdentityProviderTestHost` subprocess is likewise unnecessary — that pattern exists in `FleetTestHost` because Playwright needs a real browser against a real Angular app. Nothing here does.

**Consequences for the matrix.** `SparkIdentityProviderOptions.Issuer` is set directly in `configureSpark`, so the `ASPNETCORE_ENVIRONMENT=E2E` trap noted earlier does not apply. `TestServer`'s `HttpClient` does **not** manage cookies, so thread them explicitly (see `SparkTestClient`) — this matters for every login/consent case, which are cookie-driven.

<details>
<summary>Superseded: the Fleet-from-configuration plan</summary>

Nothing currently serves `/connect/*` under test. `FleetTestHost` launches the **real Fleet project as a `dotnet run` subprocess** (`FleetTestHost.cs:262`) with an `appsettings.E2E.json` override written at startup, so the test project referencing the IdP would achieve nothing — **Fleet itself** must call `AddIdentityProvider()`.

**Take this option: Fleet enables the IdP from configuration.** Fleet gains a `ProjectReference` to the IdP and wires it up when the config says so; the E2E override file turns it on. Reasons it beats a new host:
- `FleetE2ECollection` is a **shared collection fixture**, so the host starts once for the whole suite. Adding OIDC tests to that collection costs approximately nothing, whereas a second host pays a fresh `dotnet run` plus another embedded Raven.
- The fixture already seeds a confirmed admin and can seed extra users (`SeedUserAsync`), which the interactive login and consent flows need. A new host would reimplement that.
- A demo app demonstrating the feature is a reasonable thing to exist anyway.

The alternative — a minimal dedicated host — is only worth it if IdP tests need to run without Fleet's Angular bundle (`EnsureAngularBundleAsync` runs `npm run build`). The `/connect/*` pages are server-rendered HTML and need no SPA, but since the bundle is built once per suite and other tests need it regardless, that saving is theoretical.

⚠️ **The override file must set `Issuer`.** `ASPNETCORE_ENVIRONMENT` is `E2E`, **not** `Development` (`FleetTestHost.cs:269`), so `OidcIssuer.Resolve` will **throw** — O7 made the issuer required outside Development. Add `"Issuer": "{{httpsUrl}}"` to the override JSON, which is easy because `StartFleetAsync` already computes the HTTPS URL before writing the file (`FleetTestHost.cs:226-256`). Treat the throw as the design working: it fails loudly at startup instead of silently trusting the `Host` header.

</details>

New application records (`OidcApplication`) will need seeding per test — public client, confidential client, a `client_credentials`-only client, one that is disabled — which is also what M12.7 needs, so build the seeding helper once and share it.

**The case list lives in [idp-e2e-test-matrix.md](./idp-e2e-test-matrix.md)** — every case with its precondition, exact request, expected outcome and what it pins, including which cases are expected to **fail on first run** because they pin still-open findings. Write those anyway: a test authored after the fix only proves the fix compiles.

**Behavioural tests** (`Security/OidcSecurityTests.cs` or similar), each with its expected-failure half:
- concurrent redemption of one code → exactly one token set, the loser gets `invalid_grant`, and nothing partial is written (O2)
- POST to `/connect/{login,consent,two-factor}` **without** an antiforgery token → rejected; with one → accepted (O3)
- repeated bad passwords → lockout engages (O4)
- revoked access token → introspects `active: false`, `/connect/userinfo` returns 401 (O5)
- revoking an access token directly (not just its refresh token) actually takes effect (O5)
- consent POST carrying a `request_id` issued to a *different* user → rejected (M12.5)
- replaying a consumed `request_id` → rejected, no second code (M12.5)
- code replay and refresh reuse → whole chain revoked, not just the presented token (O1 + F5)
- client binding: client B redeeming client A's code → `invalid_grant` (already fixed, never tested)
- grant-type gating per `AllowedGrantTypes` on all three grants, scope validation against `AllowedScopes`, secret expiry and rotation, rejected-secret paths

**Coverage invariants** — enumerated from `EndpointDataSource`, not a hand-written list, so a route added later is included automatically:
- every interactive `/connect` POST carries `IAntiforgeryMetadata` with `RequiresValidation`
- `/token`, `/introspect`, `/revoke` deliberately do **not** — assert the exemption so nobody "fixes" it and breaks every conforming OAuth client
- the registered index list contains no index used for an authorization decision (the derived-id rule, findings §3)

This second group is what makes the fixes durable: the recurring failure mode in this package was one defect at five sites, and re-reading code says nothing about the sixth.

### M12.7 — Application registration surface

**Blocks Coverage using `client_credentials` at all.** There is no way to create an `OidcApplication` today — the admin screens lived in `Demo/SparkId` and were not ported. Either port them or add a minimal registration API. This is where `RedirectUris` and `AllowedScopes` are set, so it is a security surface, not just convenience.

## M11 — Retire the authorization bypasses

This is the phase that actually delivers "no duplication per credential type" — M9 and M10 only prevent *new* duplication.

- **M11.1** — route `/spark/sync/apply` through the principal + `IPermissionService`. Today `SyncActionHandler` reflectively invokes `OnSaveAsync`/`OnDeleteAsync`, skipping the `DatabaseAccess` chokepoint entirely, so an authenticated module can write anything anywhere.
- **M11.2** — route the webhook processor through the same. Its crypto is correct (`FixedTimeEquals`); it just never establishes an identity.
- **M11.3** — migration note. Existing replication users will need `security.json` entries for their modules or cross-module sync starts failing. **See open question Q5.**

---

## M6 — Documentation

Apply every row of PRD §6 **and** the "Additional broken API references" section — the sweep found substantially more than the handoff listed.

Handoff items: delete the `UseSparkAntiforgery` calls and rewrite that section; replace the `AddSparkAuthorization`/`AddSparkAuthentication`/`MapSparkIdentityApi` samples with the real `spark.AddAuthorization(…)` / `spark.AddAuthentication<TUser>(…)` API; fix `AllowedDevUsers` to state it fails closed; drop the fictional queue-name column and describe what M1 actually produces; repoint the Fleet "Complete Example" citation at WebhooksDemo; refresh stale preview and Angular version numbers.

Swept items — the recurring failure mode is READMEs documenting `IServiceCollection` method names that became `ISparkBuilder` extensions, plus methods that never existed:
- **Messaging README** — `spark.AddMessaging()` / `spark.AddRecipients()`; drop `CreateSparkMessagingIndexes()` (internal, automatic) and the nonexistent `AddRecipient<,>()` row.
- **SubscriptionWorker README** — fix the base ctor to `(ILoggerFactory, IDocumentStore)`; `TrackRetryAsync` returns `RetryOutcome` (`retry.WillRetry`), not `bool`; `spark.AddSubscriptionWorkers()`.
- **Spark core README** — add the missing `PersistentObject obj` parameter to the `OnBeforeSaveAsync`/`OnAfterSaveAsync` samples; delete `[LookupReferenceName]`, `CreateSparkIndexesAsync()`, and the `CreateSparkIndexes()` row; correct both `AddSpark` overloads; `AddSparkActions()` → `AddActions()`.

Since several of these samples **don't compile as written**, treat compilability as the bar for any code block touched.

---

## M7 — Release

Bump `<Version>` in each affected csproj — hand-maintained across 20 files, no script. The new ApiTokens package ships at the same preview number. Bump `@mintplayer/ng-spark-auth`'s `version` (M2.3 adds public API); **`@mintplayer/ng-spark` needs no bump** — M3 doesn't touch its source and its caret peer range already admits 22.13.0.

**Release note — must not be buried.** M5 is a breaking change to the public Actions contract (`OnQueryAsync` removed, `GetRowFilter` added) *and* a behavior change for every row-scoped app. Call out explicitly:
1. Apps overriding `IsAllowedAsync` now get fewer rows and smaller `totalItems` on queries and streams. That's the fix, but it is user-visible — Fleet's Cars list changes for non-admins.
2. `OnQueryAsync` is gone; anyone overriding it was silently getting nothing and must migrate to `GetRowFilter`.
3. Fail-closed flips drop rows in apps unknowingly relying on the old fail-open branches — chiefly projection-backed queries with unloadable base documents.
4. Per-instance-only apps see O(collection) reads on query paths plus a startup warning.
5. Streams may emit mid-stream `remove` patches as rows become invisible.

Merging to `master` publishes automatically (`--skip-duplicate` means an unbumped version silently no-ops). Never `dotnet nuget push` / `npm publish` by hand from the branch.

---

## Final verification

Once **all** milestones are implemented:

```
npx nx run-many --target=test
```

Requires `RAVENDB_LICENSE` (JSON) or the root `raven-license.log`. No Docker. Covers the .NET suites and both Vitest packages.

Then the manual checks that tests can't cover:
- The four demo sidebars — expand/collapse and header content (M3.3).
- WebhooksDemo GitHub popup login end to end: success, provider-side cancellation, and manually closing the popup (M2).
- Fleet's Cars list as a non-admin, confirming it now matches what `/spark/po` returns (M5).
- WebhooksDemo's project list as a user outside the org, confirming it is now filtered (M5.3).

## Follow-ups filed, not done here

- **Raven Skip/Take pushdown** — unlocked by `GetRowFilter` (M5.2) but deliberately out of scope: a performance change with its own correctness surface, wanting its own PR and benchmarks.
- **`BsShellTopbarDirective`** — needs an upstream ng-bootstrap contribution before the four demo copies can go.
- **Report back to Coverage** that `spark-handoff.md` §2 contradicts their own `PLAN.md` on sequencing; that their docs use four different names for the token concept; and that their `PRD.md:145` describes the PAT handler as wired through `configureProviders`/`IdentityBuilder` when their own working code correctly registers it as a standalone scheme instead.
