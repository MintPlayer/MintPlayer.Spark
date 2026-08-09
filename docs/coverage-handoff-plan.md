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
| `e4ce3df` | **O16, O18, O22 (part), O24, O26** — interactive-only auth, constant-time PKCE, `openid`-gated id_token, error codes, server-side required scopes |
| `6241984` | `SparkEndpointFactory` gains `configureSpark`; host decision reversed with reasons |
| `6e9ef6b` | **M12.6 started** — 24 e2e tests; found **N5** (High) and a wrong O25 fix |
| `28633e6` | **M12.6 flow coverage** — 131 IdP tests (98 e2e) across the whole flow |
| `ab3fbaa` | **Forgery batch** — 138 IdP tests, deterministic over repeat runs; found **N6** |
| `9bc39c2` | **Two-factor batch** — 164 IdP tests (26 cases; §L.4 complete) |
| `906b101` | **N7, N8** — remember-me across the 2FA hop; single-use recovery codes under concurrency |
| `999c4e4` | **D9–D11** — `/connect` rate limiting, hashed recovery codes, audience-gated introspection |
| `0b2ae01` | M12.7 spec'd as a PersistentObject (D13); PRD records what the tests returned |
| `94ac56d` | **M12.7 library half** — `IOidcApplicationContext`, `OidcApplicationActions`, `OidcScopeActions` |
| `f01bfca` | Registration story proven end to end; **O17** re-scoped honestly (**193 IdP tests green**) |
| `d2ec998` | Draft PR recorded; breaking changes collected for M7 |
| `9489006` | **M12.7 complete** — route coverage, HR as demo host, `SparkValidationException`; found **N11, N12, N13** (**205 IdP tests green**) |
| `fcaa6b9` | **M13** — consent withdrawal; **N15** (Critical) plus **N16–N18**, three defects in N11's own fix |
| `d92b673` | **M5** — row-level authz on the query and stream paths (user-requested) |
| `a855c2b` | E2E seeding flake fixed; **first green full-suite run** |
| `7cb1abe` | **N19** — optimistic concurrency on the grant document |
| `3a37833` | **N20, N21** — the revocation epoch, and one shared grant rule |
| `e3de54f` | Ported `WaitForIndexing`; **six unwaited index assertions guarded**, two of which could pass vacuously |
| `e3de54f` | **N22** — redirect-URI validation failed open on Linux; **CI green** |
| `10f024f` | PRD/plan/matrix brought current through N22 |
| `8f73301` | **M8 done** — F1, F2, F5, F6; two tests that proved nothing replaced (81 replication tests green) |
| `38379e2` | **Natural ids** — `IHasNaturalId`, ported from CronosCore |
| `1279f07` | **D14** — package + opt-in proposal declined with reasons; conventions split and renamed |
| `6b2b0b5` | **M14.2** — the suite stops substituting the store it tests (**1266 tests green**) |
| `ccfcc9a` | **M9 done** — composite scheme + antiforgery exemption; XSRF package swap declined (**1273 + 61 E2E green**) |
| `50069d4` | **Authentication guide** — schemes, `Everyone`, the failure case; two doc defects corrected |
| `af6bcb9` | Anonymous-CRUD E2E gap closed; a vacuous row-level assertion fixed; **N23** raised (**65 E2E green**) |
| `9617ab1` | Plan and PRD brought current through M9, M14 and N23 |
| `09cf22c` | **M10 done** — module certificate, certificate forwarding, JWT resource server (**1286 tests green**) |
| `(head)` | **M11 done** — sync writes routed through the chokepoint; **N23 fixed**; F11 corrected (**1289 + 65 E2E green**) |

**Draft PR: [#231](https://github.com/MintPlayer/MintPlayer.Spark/pull/231)** — opened 2026-08-09. Still a draft: handoff items 2, 3 and 6 are untouched, **M10 and M11 remain**, and release mechanics (M7) are not done. Item 5 (row-level authz) is **done** — see M5. **M8, M9 and M14 are done**; M9 was the prerequisite that made M10 and M11 worth writing at all, since a credential scheme registered before a composite default scheme existed was dead code on every Spark endpoint.

**The intermittent failure — probable cause found, and six latent defects with it.** Six assertions in the IdentityProvider tests queried an **index** with no preceding wait: `OidcConsentSecurityTests` (×4), `OidcAuthorizeSecurityTests`, `OidcTokenSecurityTests`. Under full-suite load index lag grows, which is exactly the shape of an intermittent that never reproduces under a filter.

Two of them were worse than flaky. `AssertNoCodeAsync` and the authorize-request check assert **absence** (`BeEmpty`) — so a stale index returns nothing and the assertion **passes whether or not the property holds**. Security checks succeeding for the wrong reason, invisibly. All six now wait first.

The mechanism came from the user pointing at `CronosCore.RavenDB.UnitTests`: its `WaitForIndexing` is now ported into `MintPlayer.Spark.Testing` as an extension method on `IDocumentStore`, and `SparkTestDriver` shadows `RavenTestDriver`'s method so all ~50 existing call sites route to the single implementation without being edited. Verified by making it throw and confirming the existing call sites hit it. It also **throws with the actual index errors** when indexes never settle, instead of leaving a mystery failure downstream — and it works on any store, which is why the E2E host (not a `RavenTestDriver` subclass, and previously with no equivalent at all) can now use it.

*Honest status:* the original failure was never identified — output was not captured — so this is a strongly probable cause, not a proven one. Ten-plus clean full-suite runs since, plus a green CI run, but absence of a ~1-in-7 flake over that many runs is only moderate evidence. Treat as fixed-pending-observation.

**Test tooling, while here.** `WaitForIndexing` is now a single implementation — an extension method on `IDocumentStore` in `MintPlayer.Spark.Testing` (`RavenIndexingExtensions`), ported from `CronosCore.RavenDB.UnitTests`. `SparkTestDriver` shadows `RavenTestDriver`'s method of the same name so every existing call site routes to it unedited; the routing was *verified* by making it throw, not assumed. `testenvironments.json` (WSL/Ubuntu) is added at the root so tests can be run locally under the same OS CI uses — which is what made **N22** reproducible rather than merely inferred.

~~**One unidentified intermittent failure, recorded rather than waved away.**~~ During verification a full run reported `Failed: 1, Passed: 1248` — and the output was not captured, so the test is unknown. Six consecutive full-suite runs since have been clean (1249/1249 each), and the E2E suite is clean across every run. So: roughly a 1-in-7 intermittent that has not reproduced, in a suite whose own standard is that determinism is not optional. **Do not treat the green runs below as proof it is gone.** The likely shape, given everything else in this document, is index staleness surfacing only under full-suite load — the same trap that produced three earlier flakes. Next step is a run with per-test output captured, then fixing whatever it names.

**Full test suite, all four projects, green:** 1244 (`MintPlayer.Spark.Tests`) + 61 (E2E) + 54 (source generators) + 38 (client) = **1397 passed, 0 failed**. This is the first complete run on the branch; earlier entries in this document that describe it as unrun are superseded. One pre-existing E2E flake was fixed rather than tolerated — the seeding helper queried an eventually-consistent index with no wait, so a row-level authz case intermittently failed during setup and read like a product failure.

**M12.6 has covered the flow, success and failure.** 131 IdentityProvider tests green — 33 unit plus **98 e2e** spanning `/connect/authorize`, the consent hop, all three token grants, login, logout, introspection, revocation, UserInfo, discovery and JWKS. Every fix in this branch that has observable behaviour now has a test asserting both that the legitimate path works and that the attack is refused.

It paid for itself twice. The first 24 tests found two defects six reviewers reading code had missed: a wrong fix I had shipped (O25's `exact: true` inverted the matching) and **N5**, where the serializer re-added a property-initializer default on load, making every grant-type restriction unenforceable.

**Forgery is now covered** (R-I10–R-I15): `alg=none`, a foreign signing key, RS256→HS256 confusion using the published modulus as the HMAC secret, a tampered payload with the original signature, a forged `kid`, a foreign issuer, and — the one that would slip past a signature-only check — a token this provider genuinely signed but has no record for. Each goes through both introspection and UserInfo, since they are separate entry points to the same resolver.

**Two-factor is now covered** — 26 cases, §L.4. The load-bearing group is "cannot be skipped": the partial-authentication cookie fails against `/connect/authorize`, the consent GET and the consent POST, and the 2FA form cannot be completed by someone who never passed the password step. Also settles **L-L3** empirically (lockout precedes password evaluation, so a locked account never reaches the second factor) rather than by inference from framework behaviour.

**Still untested** (§ references are to the matrix): the concurrency races T-R3/T-R4, which need a parking hook to be meaningful rather than lucky; brute-force lockout on the 2FA step specifically (L-T4); key rotation (R-J5/R-J6, which would pin the open **N4**); and the enumeration characterizations T-O1/T-O2 for the open **O15**.

**On determinism:** three separate flakes were fixed rather than tolerated — an index query in the fixture (replaced by a point-load), seeding before the host deployed its indexes, and not waiting for indexing after seeding. The suite now passes repeat full runs. A flaky security test is worse than none: it teaches people to re-run.

**Remaining open:** O10, O13, O15, O17, O20, O22 (the `auth_time`/`azp` half), O23, N2, N4, plus O27 (accepted with a rationale). None is above Medium; the Criticals and Highs are all closed.

⚠️ **O7 introduced a required setting.** `SparkIdentityProviderOptions.Issuer` must be configured outside Development or token issuance throws. Any demo or deployment wiring up the IdP needs it — check this before M7.

**Not started:** M2, M3, M10, M11, M6, M7. **M8, M9 and M14 are done** — M9 unblocks M10 and M11, which were dead code before a composite default scheme existed.

~~**Verification debt:** the full suite has not been run…~~ **Superseded — see the status block at the top of this document.** The full suite is now green across all four projects (1397 tests), and §T and §L of the matrix are covered. What remains unverified: **the four demo ClientApps have not been built or exercised since the IdP port**, and the concurrency races (T-R3/T-R4, and a withdrawal racing an in-flight refresh) still need a parking hook in the token endpoint.

**Next action:** continue M12.6 from `tests/.../IdentityProvider/OidcTestHost.cs`. Build the signed-in-session helper (register a user via `UserManager`, POST `/connect/login` with an antiforgery token, keep the cookie), which unlocks §A's consent cases and all of §L. Then §T and §R.

## Breaking changes — release notes for M7

Preview package, so no compatibility was required, but each of these changes behaviour a consumer can observe. Collected here because they were decided one at a time across the audit and a release note assembled from memory will miss one.

| Change | Effect | Why |
|---|---|---|
| `SparkIdentityProviderOptions.Issuer` **required** outside Development | Token issuance throws at startup if unset | It was derived from the `Host` header, which the caller controls — a forged header minted tokens claiming any issuer, signed with the real key (**O7**) |
| `/connect/logout` requires `client_id` | Logout without it is refused | `post_logout_redirect_uri` was validated against *every* enabled application, so one client's registered URI was a legal destination for all of them (**O12**) |
| `client_credentials` requires an explicit `scope` | A request omitting it is refused | Omitting it granted the client's entire authority (**O14**) |
| Refresh tokens require `offline_access` | Clients that never asked stop receiving one | Every browser client was silently issued a 14-day credential it could not decline (**O8**) |
| `id_token` requires `openid` | Clients not asking for it stop receiving one | A client wanting only API access still got a signed identity assertion (**O22**) |
| Scopes must have an enabled `OidcScope` | Authorize refuses an undefined scope | Authorize and issuance validated against different sources, so a granted scope silently vanished from the token (**N6**) |
| `AllowedGrantTypes` no longer defaults | A client declaring no grants can use none | The default was re-added by the serializer on load, making every grant restriction unenforceable (**N5**) |
| **Two-factor recovery codes are hashed** | **Existing codes stop working — users must regenerate** | A database dump was a dump of working second-factor bypasses (**D10/N9**). **The only change touching a persisted user format** — needs the most prominent release note |
| Rate limiter covers `/connect` | Interactive OIDC endpoints are throttled where the limiter is enabled | It was scoped to `/spark`, so an app opting in still shipped an unthrottled password endpoint (**D9**) |
| `client_credentials` refuses an undefined or disabled scope | Previously narrowed silently; now 400 `invalid_scope` | No user and no consent step here — issuing less than the caller named produces a client that fails later, far from the cause (**N11**) |
| Token responses announce `scope` when narrowed | New key in the code and refresh grant responses | RFC 6749 §5.1 requires it, precisely so a client can tell it got less than it asked for (**N11**) |
| `OidcToken.Scopes` records **granted**, not requested, scopes | Introspection reports less than before wherever a scope was undefined or disabled | The record over-reported, and introspection is how a resource server learns a token's authority (**N11**) |
| Row-level rules now apply to list, query and stream results | An app relying on the previous (leaky) behaviour sees fewer rows | The rule was enforced on the detail path and skipped on the path list screens use (**M5**) |
| A withdrawn grant refuses issuance, and tokens predating a withdrawal stay dead through re-consent | Clients must re-authorize after a user removes access | Consent was recorded and consulted nowhere (**N14**); re-consent used to resurrect survivors of the sweep (**N20**) |
| Relative and `file:` redirect URIs are refused | A client registered with `/callback` **on Linux** stops saving | It was already impossible to use; the validation just accepted it on Unix and rejected it on Windows (**N22**) |
| An Actions class refusal is now a 400, not a 500 | `SparkValidationException` maps into the standard `errors` envelope; other exceptions still 500 | Business validation had no path to the user anywhere in Spark — every message reached the operator as an empty 500 (**N12**). Framework-wide, not IdP-only |
| `IOidcApplicationContext` members are get-only | An auto-property implementation stops compiling | It returned null, and the query executor answers a null queryable with an empty result — screens that render and are always empty, silently (**N13**) |
| **The default authenticate scheme is now Spark's composite** | Every registered credential scheme runs on Spark endpoints; an app that overrode `DefaultAuthenticateScheme` itself is overridden in turn | Spark endpoints name no scheme, so only the default ever ran — a registered certificate or bearer handler was dead code and its caller arrived anonymous with `Everyone` rights, silently (**F7, M9**) |
| A non-ambient credential is exempt from antiforgery | Bearer/certificate callers can POST without an `XSRF-TOKEN` cookie | CSRF is an attack on ambient authority; demanding a token of a caller that cannot be made to send one protected nothing and made external POSTs impossible (**F8, D2**) |
| A refused credential is logged and reported as a failure | Previously indistinguishable from presenting none | Both outcomes were anonymous-with-`Everyone`, silently — refusal should be legible (**F7**) |
| `UseAuthentication()` runs whenever any credential scheme is registered | An app with credential schemes but no `IdentityUserType` now authenticates | It was gated on Identity alone, so a machine-only app never authenticated anyone (**M9**) |
| New package dependencies | `MintPlayer.Spark.Replication` gains `Microsoft.AspNetCore.Authentication.Certificate`; `MintPlayer.Spark.Authorization` gains `.JwtBearer` | M10's handlers. Both are opt-in at the API level — an app that never calls the extension pays only the restore cost |
| `AddJwtBearerCredential` throws without an `Audience` | An app cannot register the scheme unconfigured | Skipping audience validation accepts every token the issuer minted, for any resource, because the signature is genuine (**M10.3**) |
| `AddModuleCertificateForwarding` throws without a `KnownProxies` entry | Forwarding cannot be enabled without naming the proxy | A forwarded certificate is a plain header; accepting it from anywhere lets any caller claim any module identity (**M10.2**, **D3**) |
| **Cross-module sync is authorized** | A module must be granted rights in `security.json` (`Module:{Name}`) or `/spark/sync/apply` refuses it | The write path skipped the chokepoint, so an authenticated module could write anything anywhere (**F4/M11.1**). **The most disruptive change in this PR for existing replication users** — see the M11.3 migration note |
| A sync action against an unregistered entity type is refused | Previously written via a CLR-reflection fallback | It has no name for `security.json` to grant rights on, so no authorization decision exists — unevaluable is not permitted (**M11.1**) |
| Authorization precedes validation on create/update | An unauthorized caller gets 401/403 where a malformed payload previously returned 400 with validation errors | Those errors told a caller who may not create a type which of its attributes were required (**N23/M11.4**) |

## Resolved decisions (2026-08-08)

**D1 — External POST credential: OAuth2 `client_credentials` via `MintPlayer.Spark.IdentityProvider`**, not a per-user secret. Same experience for the consumer, better security posture for the application. **Conditional on the package being audited and proven sound** — see M12. Three defects are already known (unsalted SHA-256 + non-constant-time compare in `VerifyClientSecret`; application claims emitted as `client_group` so a machine token resolves to zero groups; no resource-server side at all).
> ⚠️ **Open — see Q1 in the handover notes.** If `client_credentials` is the upload credential, **M4 (the PAT library) has no consumer.** Confirm whether M4 is dropped, or kept for a different audience.

**D2 — Antiforgery.** CI/workflow posts can't carry XSRF at all, and `client_credentials` is sufficient there → exempt requests not authenticated by an ambient (cookie) credential, keyed on *the scheme that produced the principal*. Separately: Spark **hand-rolls** the XSRF cookie (`SparkMiddleware.cs:48,238-241`) and duplicates `MintPlayer.AspNetCore.SpaServices.Xsrf`, whose `UseAntiforgeryGenerator()` does exactly the same `GetAndStoreTokens` + `XSRF-TOKEN` cookie (`HttpOnly = false`). The demos reference `MintPlayer.AspNetCore.SpaServices` but **not** the `.Xsrf` package. ~~**Adopt the package; delete the duplicate.**~~

> **REVERSED at implementation (M9.3).** "Does exactly the same" was assumed, not read. The package sets only `Path` and `HttpOnly`; Spark additionally sets `SameSite=Strict`, `Secure=Request.IsHttps`, and guards a null token. Adopting it would have removed `Secure` from the CSRF token cookie — a security regression bought with twenty lines of de-duplication. The exemption half of D2 stands and is implemented; the package half is declined. See M9.3.

**D3 — Certificate forwarding (my call).** `AddCertificateForwarding` with a **configurable header name**, defaulting to `X-ARR-ClientCert`. Document both Traefik (`passTLSClientCert` → `X-Forwarded-Tls-Client-Cert`, the deployment this repo actually uses) and nginx (`ssl-client-cert`). Ships with a trusted-proxy allowlist — the demos' `KnownProxies.Clear()` must **not** be inherited.

**D4 — `Everyone` stays as-is.** Anonymous vs. authenticated access is already decided by `security.json` and, where needed, the Actions classes. No change. (My "machine caller" phrasing meant a non-human client such as CI; the point stands but needs no special-casing.)

**D5 — Replication → `IPermissionService` needs no new hard dependency.** `IPermissionService`, `IAccessControl` and `IGroupMembershipProvider` live in **`MintPlayer.Spark.Abstractions/Authorization/`**, and `MintPlayer.Spark.Replication.csproj:30` **already references** `MintPlayer.Spark.Abstractions`. Routing `/spark/sync/apply` through the permission pipeline therefore adds **zero** coupling to the Authorization package — that package supplies the `security.json` implementation, not the abstraction.

**D7 — The stored authorization request also carries `OidcAuthorization.Id`.** Consent creates the authorization, writes its id onto the request record, and code issuance reads it from there. This makes **O1 fall out of M12.5** rather than needing a separate fix: today `AuthorizationId` is hardcoded `""` at issuance, which silently kills `Revocation`'s access-token cascade (it has never executed once) *and* the reuse-detection chain revocation added in `dfab40a`. Threading a parameter through `GenerateCodeAndRedirectAsync` would work but leaves the same "remember to pass it" fragility that caused the original bug — the request record is the natural home for state the flow accumulates.

**D8 — The request document uses the natural-id pattern**, `OidcAuthorizationRequests/{sha256(request_id)}`, matching `OidcTokenReference`. Gives point-load consistency (no index staleness on a security decision), single-use enforcement by document existence, and no plaintext handle at rest — the same three properties that fix bought for tokens. One storage idiom across the package rather than two.

**D9 — Rate limiting covers `/connect` as well as `/spark`** (user's call). The limiter partitioned on `/spark` and returned *no limiter* for everything else, so an app that opted in still shipped an unthrottled password endpoint — and lockout is per-account, which does nothing against an attacker spreading attempts across accounts. This is what O27's "accept and mitigate" had assumed existed. It now does.

**D10 — Two-factor recovery codes are hashed at rest** (user's call). A recovery code is a standing second-factor bypass, so a database dump was a dump of working bypasses. Applies F4's own reasoning, which this package had already used to refuse "that is how Identity does it" for a strictly less exposed credential. Redemption compares every stored hash in constant time. **Existing cleartext codes stop working** — users must regenerate; acceptable under no-backward-compatibility, but it is the only change in this PR touching a persisted user format.

**D11 — Audience is enforced at introspection, with an explicit opt-out** (user's call). A caller may read a token's claims if it issued the token, **or** the token names it in `aud`, **or** it sets `MayIntrospectAnyAudience` (for a gateway introspecting on behalf of the resources behind it). The audience arm is not a loosening — it repairs one. N1's ownership gate had also refused the deployment RFC 7662 is written for, where a resource server introspects tokens it is meant to accept: those are issued to *clients*, so a resource server never owns them and could never ask. This is also the only place in the package where `aud` means anything.

**D12 — M12.7 (application registration) is the next milestone** (user's call). It is what actually unblocks Coverage: there is still no way to create an `OidcApplication`, so `client_credentials` — D1's whole premise — cannot be used by anyone. It is also where `RedirectUris`, `AllowedScopes` and `MayIntrospectAnyAudience` get set, which makes it a security surface rather than a convenience. Must account for the index-staleness observation: a newly registered application is not usable the instant registration returns.

**D6 — Row-level scoping is the Actions classes' `IsAllowedAsync(string action, T entity)`**, which already exists (`DefaultPersistentObjectActions.cs:98`). M5 is what makes it actually enforced on the query and stream paths. **Phase D collapses into M5** — no separate design exercise. The only residue is that `security.json`'s *property-level* rights are documented but dead (`MatchesResource` is exact string equality); that becomes a doc fix in M6.

**D14 — `IHasNaturalId` stays in core and stays always-on; no separate package, no `spark.AddNaturalIds()` opt-in.** Investigated on request (three parallel agents, 2026-08-09) after the natural-id support landed in `38379e2`. The proposal was to extract the convention into its own package and expose it as `services.AddSpark(spark => spark.AddNaturalIds(…))`. Rejected on both halves, for different reasons.

*Against the opt-in:* **implementing the interface is already the opt-in.** An entity gets a derived id only if its author wrote `GetId()`. A second, separate `AddNaturalIds()` call turns that into a two-part opt-in where **either half alone silently does nothing** — implement the interface without the call and RavenDB's always-present `AsyncDocumentIdGenerator` fallback assigns a GUID, so every `LoadAsync<Car>(Car.GetId(plate))` returns `null`. That reads as "not found", not as a wiring bug.

The decisive framing: **the opt-in would create that hazard, not remove one.** The convention is unconditional today (`SparkMiddleware.cs:75`, inside `AddSparkCore`, which every `AddSpark`/`AddSparkFull` caller runs), so no Spark app can currently get it wrong. Making it opt-in introduces a failure mode and then needs new machinery to defend against it.

That machinery is buildable — an earlier draft of this decision claimed "no generator in this repo emits diagnostics," which is **false** and was corrected before it shipped. Two analyzers exist: `ProjectionPropertyAnalyzer` (`SPARK001`/`SPARK002`, `libs/source_generators/.../Diagnostics/ProjectionPropertyAnalyzer.Rules.cs:7,16`) and `TranslationsDiagnostics` (five descriptors). They ride on core as an `Analyzer` reference (`MintPlayer.Spark.csproj:43`), so they reach every consumer regardless of bootstrap style. A guard would therefore be a copy of an existing pattern, not new capability. It is still the wrong trade: an analyzer *plus* a startup scan-and-throw, to protect against a problem that does not exist until we introduce it.

Auto-wiring via the source generator was evaluated and rejected as a substitute: `SparkFullGenerator` is packed as an analyzer only by `MintPlayer.Spark.AllFeatures.csproj:49-52`, so it runs **only** for AllFeatures consumers. Fleet references AllFeatures and calls `AddSparkFull`; HR does not reference it at all and calls plain `AddSpark`. Auto-wiring would make the same `IHasNaturalId` class behave differently depending on bootstrap style — both of which this repo ships in production.

The cron/migrations precedent does not rescue the opt-in either. `AddCronJobs()` / `AddMigrations()` have the *same* gap — forget the call and every job silently never runs — but there the forgotten call is a single line reviewed once at setup, and everything added afterwards is auto-discovered. Natural ids have no per-instance registration to auto-discover; they are one global convention flip, so the mitigation that makes cron's gap tolerable does not transfer.

*Against the package:* the split is **forced, not chosen**. All four `Demo/*/[Name].Library` entity assemblies reference `MintPlayer.Spark.Abstractions` and never `RavenDB.Client`, so the interface must live in Abstractions while the convention needs a Raven-aware assembly. A natural-ids package would therefore contain **exactly one file** — and at ~60 lines it would be five times smaller than `Migrations` (324 lines), the current floor. Cron and Migrations earn their own packages by carrying real dependencies (NCrontab, a hosted background service, a scheduler); natural ids carries none, needing exactly the `RavenDB.Client` core already pins. Against that, every new package auto-publishes on push to master and needs its own version bumped forever ([CI doctrine](../CLAUDE.md)). The cost is not the ~4 mechanical touch points — CI and Nx need zero changes — it is a permanent versioning obligation for one extension method.

*The one real argument for a builder-level call, and why it points somewhere else.* C# interfaces are inherited transitively and RavenDB matches them with `IsAssignableFrom`, so a **shared base class** — or an entity type shipped by a third-party package — that implements `IHasNaturalId` hands derived ids to every subclass and every consumer, including authors who never wrote `GetId()` and never read the base's derivation. Because the interface lives in Abstractions, which every `*.Library` project already references, a library is in a position to do this.

That is a real concern, but it is the **opposite shape** to the proposal: it argues for an opt-*out* kill switch against behaviour imposed on you, not for gating behaviour you deliberately asked for behind a second call. Not live today — every demo entity across DemoApp/Fleet/HR/WebhooksDemo is a flat class with no shared base, and nothing in the repo implements the interface except the test's own `Car`. Recorded as the escape hatch to add if and when a shared base or a third-party entity type appears, rather than built speculatively now.

*Costs that were checked rather than assumed.* `RavenDB.Client` 7.2.1 was decompiled to settle it: `GenerateDocumentIdAsync` scans `_listOfRegisteredIdConventionsAsync` with one `IsAssignableFrom` per registered convention and falls through to `AsyncDocumentIdGenerator` when none matches. Registration is a one-time sorted insert; the per-operation cost with a single registered convention is one type-assignability check, against session tracking and network I/O. **There is no cost to gate**, which removes the last argument for opt-in that did not depend on precedent. A proposed runtime "scan for implementers and skip registration if none" optimisation was withdrawn on the same evidence — and would have been actively wrong, since the demos' entities live in a separate `*.Library` assembly that an entry-assembly scan would miss, silently disabling the feature for every current demo.

*What was kept from the proposal:* the naming complaint was fair. See M14.

*Recorded because it is worth not re-deriving:* the DI-drain mechanism (a contributor interface resolved via `sp.GetServices<T>()` inside the store factory, immediately before `Initialize()`) was validated and **works** — `sp` there is the root provider, registration order is irrelevant because the factory body is lazy, and `configure(builder)` at `SparkMiddleware.cs:116` provably completes before the factory first runs at `:378`. Two invariants if it is ever needed: contributors must be singletons (root-resolved), and must not depend on `IDocumentStore` (circular resolution mid-construction). It was not needed here, but it is the answer for any future package that must configure conventions before they freeze — which is a hook Spark genuinely lacks, since `SparkModuleRegistry`'s existing extension points all fire *after* `Initialize()`.

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

**Done**, with one addition the spec didn't call for. `NoResult` falls through as designed, but a scheme returning `Fail` is no longer discarded: if something was presented and every scheme refused it, the composite returns the failure and logs a warning naming the schemes tried. Previously — and this is the whole of F7 — a caller presenting an unrecognised credential was indistinguishable from a caller presenting none. Both arrived anonymous with `Everyone` rights, silently. Refusal should be legible in a log; "authenticated as nobody" should not be the same event as "did not try to authenticate".

Only `DefaultAuthenticateScheme` is redirected, via `PostConfigure` so it wins regardless of the order an app calls `AddAuthentication` and `AddCredentialScheme` in. Challenge, sign-in and sign-out stay with Identity — the composite reads credentials and issues none, so a sign-in pointed at it would have nothing to write to and login would break. Pinned by `AddAuthentication_leaves_sign_in_and_challenge_with_Identity`.

### M9.2 — Registration surface

**Done.** Two overloads: `spark.AddCredentialScheme(name, isAmbient)` declares a scheme registered by its own package's extension (`AddJwtBearer`, `AddCertificate`, Identity's), and `spark.AddCredentialScheme<TOptions, THandler>(name, configure, isAmbient)` registers and declares in one step.

Both live in `MintPlayer.Spark.Abstractions`, not core — the Authorization package references only Abstractions, and it is the first caller. Unlike the natural-ids case (D14), this costs nothing: ASP.NET's authentication types are in the shared framework, and `SparkModuleRegistry` already depends on `IApplicationBuilder`. No new package dependency anywhere.

`AddAuthentication<TUser>` now declares Identity's two schemes **separately** rather than the combined `Identity.BearerAndApplication`, because the antiforgery gate has to know which one authenticated: the cookie is ambient and needs CSRF defence, the bearer is not and must not be obstructed by it. The combined scheme cannot answer that question.

`UseSpark` also stops gating `UseAuthentication()` on `IdentityUserType != null`. An app whose only callers are machines registers no user type, and that condition would have left the middleware out entirely — so every certificate or token caller arrived anonymous.

### M9.3 — Antiforgery exemption + adopt the XSRF package (per D2)

`SparkMiddleware.cs:181-201` enforces double-submit on mutating requests unconditionally. Exempt requests **whose principal came from a non-cookie scheme** — decide from what authenticated the request, not from what headers or cookies happen to be present, so the gate can't be suppressed by request shape.

**Done.** The gate reads `ISparkAuthenticatedSchemeFeature`, stamped by the composite handler on success, and skips validation only when the scheme that produced the principal is non-ambient. Anonymous requests stay gated: the conservative reading of D2, and the one that cannot be talked into exempting a caller who presented nothing.

That the decision keys on the *authenticating scheme* rather than on request shape is the security property, not an implementation detail. Were it keyed on "did the caller send an `Authorization` header", an attacker could disable CSRF protection for a cookie-authenticated victim by attaching a junk header. A junk header authenticates nothing, so no scheme records itself and the gate still runs — pinned by `An_unrecognised_credential_does_not_suppress_the_antiforgery_gate`.

**The XSRF package swap is declined — D2's premise was false.** D2 recorded that `UseAntiforgeryGenerator()` from `MintPlayer.AspNetCore.SpaServices.Xsrf` "does the identical `GetAndStoreTokens` + `XSRF-TOKEN` cookie with `HttpOnly = false`". It does not. The package is published (`10.2.1`) and was read rather than assumed:

| | Spark (`SparkMiddleware.cs:239-259`) | Package (`AntiforgeryMiddleware.cs:19`) |
|---|---|---|
| `SameSite` | `Strict` | not set → browser-default Lax |
| `Secure` | `Request.IsHttps` | **not set** — token cookie travels over plain HTTP |
| null `RequestToken` | guarded | unguarded |

Adopting it would trade twenty lines of duplication for a weaker cookie on the CSRF token — losing `Secure` outright. Duplication is a cost; it is not this cost. **Spark's implementation stays.** Revisit if the package gains the attributes upstream; that is a change in a different repository and not this PR's to make.

*Confirmed independently:* `XsrfCookieFlagTests` (E2E) asserts `Secure` and `SameSite=Strict` on the minted cookie. The swap D2 prescribed would have **failed the existing suite** rather than regressing silently — the one case in this audit where the tests would have caught a decision made on a false premise before it shipped.

### M9.4 — Regression sweep

**Done, with one gap stated.** 1273 unit tests and **61 E2E tests** green. The E2E run is the load-bearing part: it starts the real Fleet host via `dotnet run` against its unmodified `Program.cs`, so it exercises cookie login, the antiforgery gate and the new default scheme together, on the real pipeline rather than a substituted one.

The exemption was verified to be load-bearing rather than assumed — disabling `IsNonAmbientCredential` was confirmed to fail exactly the two tests that assert the exemption while leaving both gate tests passing. A test that passes for the wrong reason is the failure mode this milestone is most exposed to, since the whole change is invisible when it goes wrong.

*Gap:* only Fleet is covered end to end. DemoApp, HR and WebhooksDemo have no E2E host, so "browser login still works" is verified for one of four demos, by test rather than by hand. Recorded rather than claimed.

## M10 — Credential handlers

Each handler's entire authorization integration is emitting `new Claim("group", "…")`.

- **M10.1 — Client certificate.** ✅ `spark.AddModuleCertificateAuthentication()` (`libs/replication/.../Authentication/ModuleCertificateAuthentication.cs`). Identity comes from the certificate's `CN`, not the request body — the pin check was always sound, but the module it measured against was whatever the caller wrote in `RequestingModule`, so the body chose its own yardstick. `AllowedCertificateTypes = All` + `ValidateCertificateUse = false` + `RevocationMode = NoCheck`, because the documented recipe issues from an operator-made CA that no machine trusts by default; trust is not delegated to the chain but to the thumbprint pin, which is strictly narrower. Emits `group = "Module:{Name}"`, so a module is governable by `security.json` without the authorization model learning what a module is.
- **M10.2 — Certificate forwarding.** ✅ `spark.AddModuleCertificateForwarding(...)`, header name configurable per D3 (default `X-ARR-ClientCert`; Traefik's `X-Forwarded-Tls-Client-Cert` and nginx's `ssl-client-cert` documented). **Throws at startup if `KnownProxies` is empty** — the unsafe configuration is invisible at runtime, so the refusal has to be where it cannot be missed. The allowlist is enforced by stripping the header from any peer that is not a configured proxy, *before* the forwarding middleware reads it; ordering is the control, not a detail. An unidentifiable peer is not trusted.
- **M10.3 — ClientId/Secret consumer side.** ✅ `spark.AddJwtBearerCredential(...)` in the Authorization package. Validates against the authority's JWKS (discovered and refreshed, so a provider key rotation needs no deployment). **Audience is required and throws when absent** — without it every token the issuer ever minted verifies, including ones a client obtained for a different resource, because the signature is genuine. `MapInboundClaims = false`, or the default renames `group` to a WS-Federation URI and `ClaimsGroupMembershipProvider` silently resolves zero groups — indistinguishable from an unauthenticated caller.

**All three join the composite as non-ambient**, so they are exempt from the antiforgery gate (D2) — which is what makes external POSTs possible at all. Worth recording that "a certificate is never ambient" is **not** true in general: a browser configured with a client certificate for an origin attaches it automatically, exactly like a cookie. It is safe here because this scheme authenticates *modules* against a registration pin, and a browser holds no such certificate.

**Not done in M10:** the existing in-endpoint `ModuleCertificateValidator` check stays. M10 establishes the identity; M11 is what makes the endpoints rely on it and removes the parallel path. Doing both at once would have replaced a working gate with an untested one in a single step.

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

**Blocks Coverage entirely.** There is no way to create an `OidcApplication` today — the admin screens lived in `Demo/SparkId` and were not ported — so `client_credentials`, which D1 makes the CI upload credential, cannot be used by anyone. This is also where `RedirectUris`, `AllowedScopes`, `AllowedGrantTypes` and `MayIntrospectAnyAudience` are set, every one of which the audit showed to be load-bearing. It is a security surface, not a convenience.

**D13 — registration is a Spark PersistentObject, not a bespoke API** (user's call). It inherits the authorization pipeline, `security.json` governance, and the admin UI instead of inventing a parallel set of all three. Same argument that drives M11: a second path to the same data is a second place for the rules to be wrong.

#### How a library type becomes a PersistentObject

No framework change is needed, and an earlier draft of this section was wrong to claim one. Reading `ModelLoader` alone suggests entity definitions can only come from `{ContentRoot}/App_Data/Model/*.json`, which is true but is not the whole workflow: `ModelSynchronizer` **generates** those files by scanning the `IRavenQueryable<T>` properties on the app's `SparkContext` (`ModelSynchronizer.cs:43`), and it runs on `--spark-synchronize-model`.

So the consumer exposes the library's type on its own context —

```csharp
public class CoverageContext : SparkContext
{
    public IRavenQueryable<OidcApplication> OidcApplications { get; set; }
    public IRavenQueryable<OidcScope> OidcScopes { get; set; }
}
```

— runs the synchronizer once, and gets the screens. The entity type living in a package makes no difference; the context property is the whole registration. That also keeps the decision where it belongs: an app opts into administering its own identity configuration rather than having admin screens appear because it referenced a package.

#### What to build

- **`OidcApplicationActions : DefaultPersistentObjectActions<OidcApplication>`** carrying the rules the endpoints assume and the audit proved cannot be left to callers:
  - a **secret is accepted in cleartext once and stored hashed** — never round-tripped back to the client, so the edit screen must show a placeholder and treat "unchanged" as "leave alone";
  - `RedirectUris` and `PostLogoutRedirectUris` must be absolute, and must not carry a fragment;
  - `AllowedGrantTypes` restricted to the three implemented grants — an unknown value silently grants nothing and looks like a working config (**N5**'s lesson);
  - `AllowedScopes` entries must have an enabled `OidcScope` (**N6** — authorize now rejects otherwise, so accepting it here would just move the silent failure);
  - `ClientId` uniqueness, which today is unenforced (**O17**) and is a real impersonation surface;
  - `MayIntrospectAnyAudience` defaults off and should be visibly exceptional in the UI (**D11**).
- **`OidcScope` as a second PersistentObject** — scopes are half the configuration and N6 showed the two halves have to agree.
- **`security.json` guidance**: these screens administer the identity system; the default must not be `Everyone`.

#### Status — library half done

Shipped in the IdP package:

- **`IOidcApplicationContext`** — the consumer implements it on its own `SparkContext` and runs `--spark-synchronize-model`. The interface adds nothing at runtime; it exists so the compiler reports a missing or misnamed property instead of the screens quietly not appearing. Opting in stays deliberate: these screens configure who may obtain tokens, so they appear because an app asked, not because it referenced the package.
- **`OidcApplicationActions`** — absolute redirect URIs with no fragment and no duplicates; grant types restricted to the three implemented, with `refresh_token` requiring `authorization_code` (there is no other way to obtain a first refresh token) and `client_credentials` refused for a public client (it has no secret to authenticate with); a secret typed in cleartext hashed on save and an already-hashed one left alone, so re-saving does not invalidate the secret a client already holds; `ClientId` uniqueness checked *after* the write, because a read-then-write check races and both writers would find nothing.
- **`OidcScopeActions`** — non-empty name, no whitespace in it (scopes are space-delimited on the wire, so `api read` becomes two names that do not exist), no empty audiences, and name uniqueness.
- **16 validation tests**, plus **3 that prove the registration story itself** — a context implementing the interface, run through the real `IModelSynchronizer`, produces `OidcApplication.json` and `OidcScope.json` with the fields an operator must set. That claim was worth testing rather than asserting: an earlier draft of this section concluded the opposite from reading `ModelLoader` alone.

Both classes use `partial` + the `[Inject]` generator, matching every other Actions class in the repo — the IdP now carries the `MintPlayer.SourceGenerators` references for it.

#### Status — complete

The route half and the demo host are done. Both were worth doing rather than declaring finished: each turned up a defect the library half could not have shown.

- **8 route tests** (`OidcAdminRouteTests`) drive `/spark/po/{type}` against the model the **synchronizer generates**, not a hand-authored one — so a change to the entity that breaks the screens fails here rather than in a deployment. They cover registration persisting, an operator's cleartext secret round-tripping through the hash, refusals arriving as readable 400s, duplicate `ClientId`, whitespace in a scope name, and antiforgery. The strongest of them ends where the milestone actually claims to end: a client registered through the screen obtains a `client_credentials` token.
- **`SparkValidationException`** (**N12**) — writing those tests showed an Actions refusal had *no path to the caller at all*: unhandled exception, 500, no body. Every message the audit phrased for an operator was unreachable. Framework-wide, not IdP-specific — `Demo/Fleet`'s `CarActions` throws the same way. Now mapped by Create/Update/Delete into the same `errors` envelope the declarative validator produces.
- **HR is the demo host.** Deliberately not `DemoApp`, which runs `AllowAnonymousAccess()` — wiring client registration into it would publish a registration endpoint to anonymous callers, exactly what the interface's own doc warns against. HR runs deny-by-default authorization, so `security.json` can show the other half: `QueryReadEditNewDelete/OidcApplication` and `/OidcScope` granted to **Administrators only**, with HR managers and Viewers getting nothing.
- **N13, found by building the host:** `IOidcApplicationContext` declared its members `{ get; set; }`. An auto-property returns null, and `QueryExecutor` answers a null queryable with an empty result — screens that render and are always empty, silently. Get-only now, so only the working shape compiles. The interface's doc comment had been showing the broken form, and the registration test had copied it.

**Also fixed here (N11)**, because the token test needed it: every grant recorded the *requested* scopes on the token document while minting the JWT from the *granted* ones. Introspection reads the document, so it over-reported. Details in the findings doc.

#### Caveat that must not be lost

Client lookup rides `OidcApplications_ByClientId`, which is eventually consistent, so **a newly registered application is not usable the instant registration returns**. Harmless in production provisioning, but the screen should not imply otherwise, and any test must wait for indexing — this cost the e2e suite three separate flakes before it was understood.


## M13 — Consent withdrawal (user-requested, 2026-08-09)

**Origin.** The user asked whether an application could still reach resources after a user removed a scope. The answer was yes, indefinitely — see **N14** in the findings. Four adversarial investigations ran against the design before any code was written; they turned up **N15** (Critical) and three defects in **N11's own fix** (N16–N18).

### The decision: revoke, do not narrow

RFC 6749 §6 makes narrowing self-contradictory:

> "If a new refresh token is issued, the refresh token scope MUST be identical to that of the refresh token included by the client in the request."

Narrow and you must not rotate; rotate and you must not narrow. Every major provider treats user-facing withdrawal as **all-or-nothing per application** (Duende, Auth0, Keycloak, Google; only Okta models per-scope grants, and only in an admin API). FAPI Grant Management writes the asymmetry normatively: the AS **MUST** revoke the grant and all refresh tokens, **SHOULD** revoke access tokens.

So withdrawal sets `Status = "revoked"`, sweeps the token chain, and issuance refuses. There is no scope intersection anywhere in this design — the shape the first sketch of it had, and which the review showed would have been a **no-op**, because a fully withdrawn grant still has a fully populated `GrantedScopes`.

### The accepted limitation, stated rather than hidden

Already-issued access tokens are self-contained JWTs. A resource server validating offline against JWKS cannot be told to stop. RFC 7009 §3 anticipated this and prescribes **short access-token lifetimes** as the mitigation rather than a revocation list; Auth0 documents the same window to its own users. `AccessTokenLifetimeMinutes` defaults to 60. The page says so. A page implying immediacy the architecture cannot deliver would be worse than no page.

Adding the grant check to `AccessTokens.ResolveAsync` does close the window for `/connect/introspect` and `/connect/userinfo` — the consumers that ask us rather than validating alone.

### Enforcement — the three-way rule

Read `refreshTokenDoc.AuthorizationId` **verbatim**; never re-derive it from `(Subject, ApplicationId)`, or `client:acme` subjects break and any future subject-format change silently orphans every grant.

| Grant state | Action |
|---|---|
| `AuthorizationId` empty | **Allow.** Two populations: `client_credentials` (no user by construction) and **every refresh token minted before M12.5**, when `AuthorizationId` was always empty (O1). Failing closed here would be a silent multi-day outage on any database seeded before that commit. |
| `AuthorizationId` set, document **missing** | **Fail closed.** Only reachable if someone deleted the grant, and deleting a grant should end access. Matches the package's own precedent at `AccessTokens.cs:25`. |
| Document present, `Status != "valid"` | **`invalid_grant` + revoke the chain.** Not merely narrow. |

The check goes **inline in `HandleRefreshTokenGrant`**, between the client-binding check and the narrowing — never in `LoadScopesAsync` or `GrantedNames`, which all three grants funnel through, because `client_credentials` has no grant document and would 400 outright.

### Surface — a server-rendered `/connect` page, and why not a PersistentObject

`GET /connect/applications` + `POST /connect/applications/revoke`.

The PersistentObject route is blocked, for a reason worth recording precisely because the first reading of it was wrong. It is *not* that the list endpoint lacks row filtering — `DatabaseAccess.GetPersistentObjectsAsync:143` does filter. It is that **Spark's list screens do not use that endpoint**: `SparkQueryListComponent` calls `/spark/queries/{id}/execute` → `QueryExecutor`, which has only type-level `EnsureAuthorizedAsync("Query", …)` at `:126`/`:194` and no row filtering at all. A grants list as a PersistentObject would show every user every other user's grants, and overriding `IsAllowedAsync` would not save it because that hook is never invoked on the query path. **This is M5, now in scope — see below.**

Three reasons it stays a `/connect` page even after M5 lands:
- `OidcAuthorization` has no display name; `Subject` is a raw user id and `ApplicationId` a document id, so a grid needs a join to render anything human-readable.
- The document id is a SHA-256 hash — a meaningless key column.
- **A generic `Delete` would leave the token chain live**, making withdrawal cosmetic. The sweep is code, not a CRUD screen.

Security properties, each load-bearing:
- User resolved via `GetInteractiveUserIdAsync()` — `ApplicationScheme` only, so a mid-2FA cookie and a bearer token both yield null. Using `context.User` or a bare `.RequireAuthorization()` reopens **O16**.
- The form carries the **application** id; the server derives the grant id from the *session's* user. IDOR cannot exist by construction — there is no parameter that could name another user's grant. Same idiom `Consent.cs` uses for its request handle.
- `.RequireAntiforgery()` on the route **and** `AppendAntiforgery` in the markup. Omitting the route half is invisible: the form still works and only the protection disappears. This is O3's shape, and the findings doc predicted "the sixth page someone adds will be wrong again."
- One response shape for every outcome, so "not yours" and "doesn't exist" cannot be distinguished.
- `Content-Security-Policy: frame-ancestors 'none'` across `/connect/*` — nothing set it, and a framed one-click "Allow" on `/connect/consent` is worse than a framed "Remove".

**A new index is required and did not exist.** The grant id is a hash, so it is not prefix-scannable by subject, and O9 deliberately deleted this collection's index. `OidcAuthorizations_BySubject` is added **for listing only** and must never back an authorization decision — the withdrawal itself point-loads. The post-withdrawal confirmation renders from the document just written, not a re-query, or index lag shows the user the app they just removed.

### Also fixed here, because withdrawal made them reachable

- **N15** — reinstatement must not resurrect a withdrawn grant without a screen, and must **replace** `GrantedScopes` rather than union. Ships in the same commit as the surface: shipping the surface alone would have made withdrawal an escalation vector.
- **N16** — an empty granted set is refused rather than minting a scopeless but valid subject-bearing JWT.
- **N17/N18** — N11's own defects: stop mutating the stored scope list, announce the narrowing, and carry the presented scopes onto the rotated refresh token per §6.

## M5 — Row-level authorization on queries (user-requested, 2026-08-09)

Moved from "not started" into this PR at the user's direction, after the withdrawal work established that the query path has no row filtering at all.

**The gap.** `QueryExecutor.cs:126` and `:194` do only type-level `EnsureAuthorizedAsync("Query", entityTypeDefinition.Name)`. `Execute.cs` has no authorization calls anywhere. Spark's list screens use this path. So any owner-scoped entity — the Fleet demo's `CarActions.IsAllowedAsync` scoping to `CreatedBy == CurrentUserId` is the worked example already in the repo — is correctly filtered on the detail and edit paths and **leaks wholesale on the list screen**.

`IRowSecurity` exists (`Services/RowSecurity.cs`) with two consumers, neither of them a query path.

**Shape:** row filtering applied at the one place every query result passes through, sharing the same `IsAllowedAsync` hook the detail path already uses, so an entity's ownership rule is written once and enforced everywhere.

## M14 — Natural ids: naming, and the coverage gap the investigation found (user-requested, 2026-08-09)

Scope came out of D14. The packaging and opt-in halves of the proposal were declined there; two things survive.

### M14.1 — The rename (done)

`ApplySparkIdConventions` was a fair complaint: vague, and it did two unrelated things behind one name. Split, since they are genuinely independent mechanisms — RavenDB consults registered id conventions first and reaches the fallback generator only when none matches, so neither is a mode of the other:

- `conventions.UseNaturalIds()` — derives ids for `IHasNaturalId` entities.
- `conventions.UseGeneratedIds()` — `{Collection}/{Guid}` for everything else.

Called as `store.Conventions.UseNaturalIds().UseGeneratedIds()` at the single production site. This also puts the name the user asked for — *natural ids* — on the thing that actually is natural ids, rather than on a method that also owns Spark's default id scheme.

### M14.2 — The unit/integration suite substitutes the store it is testing

Found while checking where a convention hook would have to reach. **`MintPlayer.Spark.Tests` never runs Spark's production id conventions.**

- The 47 fixtures deriving from `SparkTestDriver` get their store from `RavenTestDriver.GetDocumentStore()` (`SparkTestDriver.cs:69`), which never routes through `AddSpark`.
- The 23 that layer `SparkEndpointFactory` on top *do* call `AddSpark` — and then **remove the registered `IDocumentStore` and substitute the test store** (`SparkEndpointFactory.cs:97-99`), which is the same `RavenTestDriver` store. So the production store factory never executes there either.

Every fixture therefore runs on Raven's stock sequential ids (`trailers/1`) while production runs on `Trailers/{guid}`. `NaturalIdConventionTests` is the only file in the project that has exercised the real rules, and only because it installs them by hand in `PreInitialize`.

The E2E suite is **not** affected: `FleetTestHost.cs:274` shells out `dotnet run` against the real Fleet project, so it gets the unmodified `Program.cs` and the real factory. An earlier draft of this section claimed "no test anywhere," which was too strong and is corrected here.

This is pre-existing and predates natural ids — the GUID generator has been unconditional since `AddSpark` was introduced. It is recorded here rather than in the IdP findings because it is a Spark-wide testing gap, and because it is the same shape as the two vacuous assertions M8 fixed: a test that looks like it covers something whose subject was quietly swapped out.

**Fixed in two files.** `SparkTestDriver` applies the conventions in `PreInitialize`, so all 47 fixtures pick them up; `NaturalIdConventionTests`'s hand-rolled override became redundant and was deleted. **1266 tests green, zero breakage** — which is the outcome the analysis below predicted, and the reason it was worth doing the analysis rather than guessing.

The risk worth checking was ordering, not the obvious `Id.Should().Be(...)` assertions — `trailers/1` and `trailers/2` sort in insertion order and GUIDs do not, so any test storing several documents and asserting on their order would fail for a reason unrelated to its subject. It was checked as a **closed set**: an id-order dependency is only reachable from a `StoreAsync`/`Store` call that omits an explicit id, and every such call site in `tests/` was enumerated and inspected. All of them either sort explicitly by a field (`SparkQuery.SortColumns`) or assert order-independently (`BeEquivalentTo`, `ContainSingle`, `HaveCount`). The 24 id-shape assertions in the tree all trace to explicitly-assigned ids. The single JSON fixture (`Reflection/Fixtures/people.json`) carries `@metadata.@id` on every document, as Raven's import format requires.

## M11 — Retire the authorization bypasses

This is the phase that actually delivers "no duplication per credential type" — M9 and M10 only prevent *new* duplication.

**M11.4 — N23, found while documenting the authentication story (2026-08-09).** `CreatePersistentObject` validates the posted object (`Create.cs:62`) *before* the authorization check, which lives inside `SavePersistentObjectAsync` (`:68`). An anonymous caller posting a malformed payload for an entity type it may not create gets a 400 listing the validation errors, and only reaches 401/403 when the payload happens to be well-formed. The refusal is never in doubt; what leaks is which attributes a type requires, for a type the caller cannot touch — the same class of oracle `NotFoundVsForbiddenTests` exists to prevent.

It lands here rather than as its own fix because the correct repair is the one M11 is already making: `DatabaseAccess` exposing "may I?" independently of "do it", so the check can move ahead of validation **without** adding a second copy of the decision in front of the chokepoint. Current behaviour is pinned by `AnonymousPersistentObjectAccessTests.Anonymous_cannot_create_a_Company_despite_being_able_to_read_them`, which asserts the 400 and says in its own comment that it becomes 401 when this is reordered.

- **M11.1 — done.** `SyncActionHandler` now calls `IDatabaseAccess.SavePersistentObjectAsync` / `DeletePersistentObjectAsync` instead of reflectively invoking the actions pipeline. It had skipped the chokepoint entirely, so the mTLS check proved *which* module was calling and nothing then consulted what that module was allowed to touch — an authenticated module could write any document in any collection (F4).

  A sync action against a collection with **no registered entity type is now refused** (`SparkSyncNotAuthorizableException`). Such a type has no name for `security.json` to grant rights on, so no authorization decision exists to make about it — and the CLR-reflection fallback wrote it anyway. Unevaluable is not permitted.

- **M11.2 — the finding does not survive contact with the code, and the correction matters more than the fix would have.** F11 grouped the GitHub webhook path with the mTLS one as "the same bypass pattern". It is not. `libs/webhooks/` contains **no reference to `IDatabaseAccess`, `SavePersistentObjectAsync`, or a Raven session** — the processor verifies the HMAC and broadcasts a message. It writes nothing, so there is no write path around the chokepoint to retire.

  What is true is narrower: a recipient handling that message runs with **no principal**, so anything it does through `IDatabaseAccess` is authorized as anonymous. That is governed, just not attributed — an app cannot grant "the GitHub webhook" rights that a public caller does not also have. Fixing *that* means the message bus carrying an identity from producer to recipient, which is a messaging-package change and a genuinely different piece of work from retiring a bypass. **Recorded, not attempted here.**

- **M11.3 — migration note (answers Q5).** Cross-module sync now requires the owner module to grant the calling module rights. Two things follow for an existing deployment:

  1. **Register the certificate scheme.** Without `spark.AddModuleCertificateAuthentication()` (M10.1) the calling module arrives anonymous and holds only `Everyone`'s rights, so sync-apply is refused.
  2. **Grant the module in `security.json`.** The scheme emits `group = "Module:{Name}"`, so a module is granted like any other group — declare a group named `Module:HR` and give it `New`/`Edit`/`Delete` on the collections it is allowed to write.

  Both are deliberate: the point of M11 is that "an authenticated module" stops meaning "a fully trusted module". An operator who wants the old behaviour can grant the module broad rights explicitly, which is a decision recorded in configuration rather than an absence of one.

  **No test exercises a successful sync-apply end to end** — `SyncActionSubscriptionWorkerE2ETests` posts to a stub handler and `ReplicationEndpointAuthTests` only asserts refusals — so this change is verified by unit tests on the routing, not by a working cross-module round trip. Worth knowing before upgrading.

- **M11.4 — done, N23.** `IDatabaseAccess.EnsureSaveAuthorizedAsync` lets `Create` and `Update` authorize *before* validating. `SavePersistentObjectAsync` calls the same method, so there is one implementation of the decision and the chokepoint stays authoritative — the endpoint only asks it earlier. The E2E test that pinned the old 400 now asserts 401.

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
