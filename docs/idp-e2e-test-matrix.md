# IdentityProvider — e2e security test matrix

Companion to [PRD-CoverageHandoff.md](./PRD-CoverageHandoff.md) §8 and [coverage-handoff-plan.md](./coverage-handoff-plan.md) M12.6, and the evidence half of [findings-identity-provider-audit.md](./findings-identity-provider-audit.md).

**The invariant every case below serves:** no tampering reachable from a URL, a form field, a header or a cookie can make the provider mint a credential for a destination, a client, or a subject the attacker chose — and no legitimate flow is broken in the process.

## How to read this

Each case gives a test name, precondition, the exact request with the tampered element named, and the expected outcome. Outcomes are precise about **whether a credential is minted**, because "returns 400" and "mints nothing" are different assertions and only the second one is the security property.

Cases are prefixed by area: **A-** authorize/consent, **T-** token endpoint, **L-** login/session, **R-** resource-server surface (introspect/revoke/userinfo/JWKS).

**Host:** all of these need something serving `/connect/*`; see the plan's M12.6 for the decision (Fleet enables the IdP from configuration, reusing the shared collection fixture). Most cases are plain `HttpClient` + `CookieContainer` in the style of `XsrfCookieFlagTests` — the interactive pages are server-rendered, so **Playwright is not needed anywhere in this matrix**.

**Both halves are mandatory.** A suite of rejection assertions passes trivially against a completely broken endpoint that rejects everything. Every area therefore opens with the flows that must *succeed*.

Notation: **JSON400** = 400 with a JSON error body, no redirect. **ERR-REDIRECT** = 302 to the registered `redirect_uri` carrying `error`/`error_description` (+`state`). **CODE-REDIRECT** = 302 carrying `code` (+`state`) — a credential *is* minted. **NO CODE** = assert no `OidcToken` document was created.

## Status of the surrounding work

Some cases below are expected to **fail on first run** because they pin findings that are still open. That is intentional: they are the regression net for work not yet done, and the plan's milestone list says which. They should be written now and marked, not deferred — a test written after the fix only proves the fix compiles.

---

## A — `/connect/authorize` and the consent hop

Source: `Endpoints/Authorize.cs`, `Endpoints/Consent.cs`. Post-M12.5, `redirect_uri`/`scope`/`code_challenge`/`nonce` are request parameters **only** on the first hop; the consent hop reads them exclusively from the stored `OidcAuthorizationRequest` keyed by `request_id`.

### A.1 Must succeed

| # | Test | Precondition | Request | Expected | Pins |
|---|---|---|---|---|---|
| A-H1 | `Authorize_explicit_consent_creates_pending_request` | `ConsentType=explicit`, `RequirePkce=true`, no prior grant, user signed in | GET `/connect/authorize` with valid `client_id`, registered `redirect_uri`, `response_type=code`, `scope=openid profile`, `state`, S256 `code_challenge`, `nonce` | 302 → `/connect/consent?request_id=…`; request persisted `Status=pending` with all parameters copied, `ExpiresAt`≈+10 min | baseline |
| A-H2 | `Consent_get_renders_from_stored_request_only` | A-H1 | GET `/connect/consent?request_id=…` and nothing else | 200 HTML; scope list = `request.Scopes`; **markup contains no `redirect_uri`, `scope`, `code_challenge` or `nonce` field** — only `request_id` and the antiforgery token | structural fix (findings §3) |
| A-H3 | `Consent_post_allow_mints_code` | A-H2 | POST `request_id`, `decision=allow`, `scopes=openid`, `scopes=profile`, antiforgery token | CODE-REDIRECT to the *original* `redirect_uri` with `state` echoed; grant created `Status=valid`; `request.Status→consumed` | baseline |
| A-H4 | `Authorize_implicit_consent_auto_approves` | `ConsentType=implicit`, `AutoApproveImplicitConsent=true` | valid GET | CODE-REDIRECT straight from `/connect/authorize`; consent page never reached | `Authorize.cs` implicit path |
| A-H5 | `Authorize_skips_consent_when_grant_covers_scopes` | prior grant `valid` with `GrantedScopes ⊇ requested` | valid GET | CODE-REDIRECT directly; existing `AuthorizationId` reused, no second grant document | O9 fix |
| A-H6 | `Authorize_partial_scope_coverage_forces_reconsent` | prior grant covers `openid` only; request asks `openid profile` | valid GET | falls through to the consent screen — **not** auto-approved | negative space of A-H5; blocks silent scope widening |
| A-H7 | `Consent_post_can_narrow_scopes` | A-H2 with optional `profile` | POST `scopes=openid` only | CODE-REDIRECT; `request.Scopes` narrowed to `[openid]`; grant gains only `openid` | legitimate narrowing must keep working, or A-S5 is untestable |
| A-H8 | `Consent_post_deny_redirects_without_code` | A-H2 | POST `decision=deny` | ERR-REDIRECT `access_denied` to the **stored** `redirect_uri`, `state` echoed, NO CODE, `request.Status→denied` | |
| A-H9 | `Consent_handle_is_bound_to_subject_not_session` | same user signed in on two clients | move the `/connect/consent?request_id=…` URL between them | **succeeds** — binding is to `Subject`, deliberately not to a session or device | prevents someone "hardening" this into session-binding and breaking legitimate use |

### A.2 `redirect_uri` — the destination

All JSON400 `invalid_request`, **NO CODE**. The value is rejected before it is ever trusted as a redirect target, so none of these may produce an ERR-REDIRECT.

| # | Test | Tampered value | Pins |
|---|---|---|---|
| A-R1 | `Authorize_rejects_unregistered_redirect_uri` | `https://evil.example.com/cb` | |
| A-R2 | `Authorize_rejects_redirect_uri_prefix_extension` | registered + `2`, and registered + `/extra` | ordinal exact match, no prefix matching |
| A-R3 | `Authorize_rejects_redirect_uri_case_variation` | host-case and path-case variants | `StringComparer.Ordinal` |
| A-R4 | `Authorize_rejects_redirect_uri_trailing_slash` | registered + `/` | |
| A-R5 | `Authorize_rejects_redirect_uri_userinfo_trick` | `https://registered.example.com@evil.com/cb` | raw string compare gives no URL-parsing ambiguity to exploit — pin it so nobody "improves" this into a `Uri` host comparison |
| A-R6 | `Authorize_rejects_redirect_uri_path_traversal` | `…/cb/../../evil` | |
| A-R7 | `Authorize_rejects_redirect_uri_fragment_smuggling` | `…/cb#https://evil.com` | |
| A-R8 | `Authorize_validates_the_redirect_uri_it_actually_uses` | send the parameter **twice**, evil first then registered, and again in the opposite order | whichever value is validated must be the one used for the redirect. Both orders tested, because a mismatch here is a smuggling vector |
| A-R9 | `Consent_post_ignores_injected_redirect_uri_field` | add `redirect_uri=https://evil.com` to the consent POST | field ignored entirely; redirect uses `request.RedirectUri` | the structural fix — this is *the* test for M12.5 |
| A-R10 | `Redirect_url_is_well_formed_when_registered_uri_has_query` | register `https://good.example.com/cb?tenant=1` and complete a flow | **currently produces `…?tenant=1?code=…`** — malformed. Expected-to-fail until **O21** is fixed | O21 |

### A.3 `client_id`

| # | Test | Request | Expected | Pins |
|---|---|---|---|---|
| A-C1 | `Authorize_rejects_unknown_client` | unknown `client_id` | JSON400 `invalid_client` | |
| A-C2 | `Authorize_rejects_disabled_application` | `Enabled=false` | JSON400 `invalid_client` | |
| A-C3 | `Authorize_rejects_client_not_registered_for_code_grant` | `AllowedGrantTypes=["client_credentials"]` | JSON400 `unauthorized_client` | regression pin for **O11** (closed) |
| A-C4 | `Authorize_client_id_lookup_is_case_sensitive` | registered `AcmeApp`, request `acmeapp` | **must not resolve.** Expected-to-fail if RavenDB's default case-insensitive term matching applies | **O25** — run this one early; it decides whether O25 is cosmetic or impersonation |
| A-C5 | `Authorize_validation_order_does_not_leak_client_existence` | unknown client **and** `response_type=token` | `unsupported_response_type`, not `invalid_client` | pins the ordering so error shape can't be used to enumerate clients |

### A.4 `scope`

| # | Test | Request | Expected | Pins |
|---|---|---|---|---|
| A-S1 | `Authorize_rejects_scope_outside_allowed_set` | `scope=openid admin` | ERR-REDIRECT `invalid_scope` (redirect is correct here — `redirect_uri` is already validated) | |
| A-S2 | `Authorize_rejects_empty_scope` | `scope=` | JSON400 `invalid_request` via the required-parameter check | distinguishes empty from disallowed |
| A-S3 | `Authorize_scope_matching_is_case_insensitive` | `scope=OpenID` | succeeds — intentional | pins deliberate behaviour so it isn't "fixed" |
| A-S4 | `Authorize_tolerates_duplicate_scope_tokens` | `scope=openid openid profile` | succeeds, no double-grant in `EnsureAuthorizationAsync` | |
| A-S5 | `Consent_post_cannot_inject_unrequested_scope` | request had `openid`; POST `scopes=openid&scopes=admin` where `admin` **is** in `AllowedScopes` | `admin` dropped; code carries `openid` only | the escalation M12.5 closed — highest-value case in A.4 |
| A-S6 | `Consent_post_rejects_empty_scope_grant` | POST with no surviving scopes | JSON400, NO CODE | |
| A-S7 | `Consent_post_omitting_required_scope` | forge a POST omitting a scope marked `Required` | **currently accepted** — expected-to-fail pending a decision on **O26** | O26 (new) |

### A.5 PKCE

| # | Test | Request | Expected | Pins |
|---|---|---|---|---|
| A-P1 | `Authorize_rejects_missing_challenge_when_required` | `RequirePkce=true`, no `code_challenge` | ERR-REDIRECT `invalid_request` | |
| A-P2 | `Authorize_rejects_plain_method` | `code_challenge_method=plain` | ERR-REDIRECT `invalid_request` | downgrade attack |
| A-P3 | `Authorize_rejects_challenge_with_method_omitted` | challenge present, method absent | ERR-REDIRECT — no implicit default | |
| A-P4 | `Authorize_allows_no_pkce_when_not_required` | `RequirePkce=false`, no challenge | succeeds, `CodeChallenge=null` propagates | |
| A-P5 | `Consent_post_ignores_injected_code_challenge` | add `code_challenge=<attacker's>` to the consent POST | ignored; the code carries the challenge from the **original** authorize call | with A-R9, the pair that proves the consent hop is inert |

### A.6 `response_type`, `state`, `nonce`

| # | Test | Request | Expected | Pins |
|---|---|---|---|---|
| A-T1 | `Authorize_rejects_token_response_type` | `response_type=token` | JSON400 `unsupported_response_type` | implicit flow unreachable |
| A-T2 | `Authorize_rejects_id_token_response_type` | `response_type=id_token` | same | hybrid unreachable |
| A-T3 | `Authorize_response_type_compare_is_exact` | `response_type=Code` | rejected | |
| A-N1 | `State_echoed_verbatim_on_success` | `state=xyz` | present unmodified on CODE-REDIRECT | |
| A-N2 | `State_echoed_on_error_and_deny_redirects` | `state=xyz` + invalid scope; and + deny | present on both | CSRF protection for the *client* depends on this |
| A-N3 | `State_omitted_entirely_when_absent` | no `state` | no `state=` fragment at all, not `state=` empty | |
| A-N4 | `Nonce_never_appears_in_any_redirect` | `nonce=secret` | absent from every redirect; travels only into the stored request | |

### A.7 `request_id` — the consent handle

All exercise `Authorize.LoadPendingRequestAsync`; run each against **both** the GET and the POST.

| # | Test | Precondition | Expected | Pins |
|---|---|---|---|---|
| A-I1 | `Consent_rejects_unknown_request_id` | — | 400, NO CODE | |
| A-I2 | `Consent_rejects_expired_request` | request older than `ExpiresAt` | 400, NO CODE | the 10-minute bound |
| A-I3 | `Consent_rejects_consumed_request_replay` | complete a flow, re-POST the same handle | 400, **no second code** | single-use |
| A-I4 | `Consent_rejects_denied_request_replay` | deny, then re-POST with `decision=allow` | 400, NO CODE — denial is terminal | |
| A-I5 | `Consent_rejects_handle_issued_to_another_user` | user A obtains a handle; user B presents it | 400, NO CODE | **the hijack the `Subject` binding exists to close** — the single most important case in section A |
| A-I6 | `Consent_post_without_prior_get_succeeds` | skip the GET | succeeds — the GET is a pure render and gates nothing | documents that possession of a valid, own, pending handle *is* the gate |
| A-I7 | `Authorize_issues_a_fresh_handle_per_call` | identical request twice | two distinct handles and documents | no id reuse |

### A.8 CSRF and unauthenticated access

| # | Test | Request | Expected | Pins |
|---|---|---|---|---|
| A-F1 | `Consent_post_rejected_without_antiforgery_token` | forged cross-origin POST with a valid own `request_id`, cookie riding along, **no** token | **rejected**, NO CODE | **O3 — the confirmation that decides whether the O3 fix works at all.** See note below |
| A-F2 | `Consent_post_accepted_with_valid_antiforgery_token` | the same POST *with* the token | succeeds | the other half — without it, A-F1 passes against a totally broken endpoint |
| A-U1 | `Authorize_unauthenticated_redirects_to_login` | no auth cookie | 302 → `/connect/login?returnUrl=…` (server-constructed, self-referential) | |
| A-U2 | `Consent_get_unauthenticated_redirects_to_login` | no auth cookie | 302 → login carrying only `request_id` | the handle is safe to round-trip; the parameters no longer exist to leak |
| A-U3 | `Consent_post_unauthenticated_returns_401_not_redirect` | no auth cookie | **401**, deliberately not a redirect | asymmetric with A-U2 by design — redirecting a POST drops the body; pin it so nobody "fixes" the inconsistency |
| A-U4 | `Authorize_refuses_a_bearer_only_caller` | valid Spark **API bearer** token, no interactive cookie session | **must not** mint a request or a code — the interactive grant requires an interactive session | **O16, open.** Expected-to-fail. Ambient `context.User` resolves the bearer scheme before the cookie, so a non-interactive credential can drive the interactive flow headlessly with no human at any screen |

> **On A-F1.** The team reviewing `Consent.cs` reported the antiforgery check as missing, because `HandlePost` contains no validation call. It is registered as endpoint metadata one file away (`SparkIdentityProviderExtensions.cs:99`) and enforced by middleware (`SparkMiddleware.cs:184-196`) — the reviewer wasn't given those files, so the report was a false positive of my scoping, not of the code. But it makes the underlying point sharply: **the protection is invisible at the point of use.** A reader of the handler cannot tell it is protected, which is precisely why A-F1/A-F2 must exist and why the coverage invariant in §A.9 enumerates endpoints rather than trusting inspection.

### A.9 Coverage invariants (not per-case — these enumerate)

Driven from `EndpointDataSource`, so a route added later is included automatically and the test fails until it complies.

| # | Test | Assertion |
|---|---|---|
| A-V1 | `Every_interactive_connect_post_requires_antiforgery` | every POST under `/connect` **except** `/token`, `/introspect`, `/revoke` carries `IAntiforgeryMetadata` with `RequiresValidation` |
| A-V2 | `Machine_endpoints_deliberately_exempt_from_antiforgery` | those three carry **no** such metadata — asserting the exemption is intentional stops someone "completing" A-V1 and breaking every conforming OAuth client |
| A-V3 | `No_registered_index_backs_an_authorization_decision` | the registered RavenDB index list contains no index queried on an authorization path (the derived-id rule, findings §3) |

---

## T — `/connect/token`

Source: `Endpoints/Token.cs`, `Services/ClientSecretHasher.cs`, `Services/OidcTokenGenerator.cs`.

Two markers are used below. **[EXPECTED-FAIL]** — the case pins correct behaviour that the code does not yet have; write it, mark it skipped, remove the skip when the fix lands. **[CHARACTERIZATION]** — the case passes today but asserts something *undesirable*; invert the assertion when the finding is fixed rather than deleting the test.

### T.1 Must succeed

| # | Test | Precondition | Expected | Pins |
|---|---|---|---|---|
| T-H1 | `Token_issues_access_id_and_refresh_for_valid_code` | confidential app, `RequirePkce`, valid unexpired code with matching challenge/redirect | 200 with `access_token` (carrying `jti`), `id_token` (`sub`, `aud`=client, scope-driven claims, **no** application claims merged), `refresh_token`. DB: code→`redeemed` with `RedeemedAt`; access + refresh docs `valid` under the same `AuthorizationId`. Headers `Cache-Control: no-store`, `Pragma: no-cache` | RFC 6749 §4.1 |
| T-H2 | `Token_refresh_rotates_and_retires_the_old_token` | valid refresh doc | 200 with a new triple; `id_token` has **no** `nonce`; old refresh→`redeemed`; new refresh `valid` under the same `AuthorizationId` | RFC 6749 §6 |
| T-H3 | `Token_client_credentials_issues_access_token_only` | machine client with `Claims=[{group, Administrators}]` | 200 with **only** `access_token`/`token_type`/`expires_in`. JWT has no `sub`, carries `client_id`, `scope`, and the application's `group` claim unprefixed. DB: one access doc, `Subject="client:{ClientId}"` | the delegated/machine claim split |

### T.2 Client authentication

| # | Test | Precondition | Expected | Pins |
|---|---|---|---|---|
| T-A1 | `Token_rejects_missing_secret_for_confidential_client` | 1 valid secret | 401 `invalid_client`; **no DB mutation** — the secret check runs before the code point-load | |
| T-A2 | `Token_rejects_wrong_secret` | | 401 `invalid_client` | |
| T-A3 | `Token_rejects_expired_secret` | sole secret with past `ExpiresAt` | 401 `invalid_client` | rotation window |
| T-A4 | `Token_accepts_either_secret_during_rotation` | two unexpired secrets | both succeed | |
| T-A5 | `VerifyClientSecret_hashes_every_candidate_even_after_a_match` | 5 valid secrets | **unit test, not e2e** — call the `internal static` directly with a counting fake and assert no short-circuit. Wall-clock timing over HTTP is too noisy to assert on | the deliberate non-short-circuit |
| T-A6 | `Token_public_client_with_no_secrets_may_omit_it` | `ClientType="public"`, `Secrets=[]` | 200 | RFC 6749 §2.3 |
| T-A7 | `Token_ClientType_public_is_matched_case_insensitively` | `ClientType="PUBLIC"`, `Secrets=[]` | 200 | pins the intended boundary |
| T-A8 | `Token_ClientType_with_whitespace_still_requires_a_secret` | `ClientType=" public"`, `Secrets=[]` | 401 — fails closed into "must authenticate", and with no secrets it then *cannot* authenticate. A config footgun, **not** a bypass; pin it so the fail-closed direction is never loosened | regression guard for the `ClientType` fix |
| T-A9 | `Token_rejects_disabled_application_identically_to_unknown` | `Enabled=false` | 401 `invalid_client`, byte-identical to the unknown-client response | positive result — no oracle here |

### T.3 Client binding — RFC 6749 §4.1.3

| # | Test | Expected | Pins |
|---|---|---|---|
| T-B1 | `Token_rejects_code_belonging_to_another_client` | 400 `invalid_grant`; **the code stays `valid`** — a mismatch is refused but not treated as theft, so the rightful client can still redeem it. Assert the status explicitly | |
| T-B2 | `Token_rejects_refresh_token_belonging_to_another_client` | 400 `invalid_grant`; the refresh doc stays `valid` for the same reason | |

### T.4 Single-use and replay — RFC 6819 §5.2.2.3

| # | Test | Expected | Pins |
|---|---|---|---|
| T-R1 | `Token_replayed_code_revokes_the_whole_chain` | 400 `invalid_grant`; code→`revoked`; **and the access *and* refresh tokens minted by the first redemption also flip to `revoked`** — asserting only that the HTTP call failed would miss the entire point of the fix | O1 + F5 |
| T-R2 | `Token_reused_refresh_token_revokes_the_whole_chain` | 400; old doc→`revoked`; the successor access **and** refresh tokens revoked — these are the attacker's other stolen credentials | F5 |
| T-R3 | `Token_concurrent_code_redemption_yields_exactly_one_token_set` | one 200, one 400 `invalid_grant`; exactly one access/refresh pair exists for that `AuthorizationId`; code ends `redeemed` (**not** `revoked` — the concurrency loser fails at the save, which does not trigger the theft teardown that a point-load-detected replay does) | O2 |
| T-R4 | `Token_concurrent_refresh_rotation_yields_exactly_one_token_set` | as above | O2 |

> **On making T-R3/T-R4 deterministic.** True simultaneity over HTTP is not guaranteed, so a single run can pass by luck. Best: a test-only hook that parks both requests between the point-load and the save, then releases them together. Failing that, loop the race ~20× and assert the invariant "never more than one valid pair per `AuthorizationId`" every iteration — that still catches a regression that drops `UseOptimisticConcurrency`, because without it both requests would occasionally win. A two-session unit test is the fallback. **Do not** write a single-shot race test and call it covered.

### T.5 PKCE

| # | Test | Expected | Pins |
|---|---|---|---|
| T-P1 | `Token_rejects_missing_verifier_when_challenge_present` | 400 `invalid_grant` | |
| T-P2 | `Token_rejects_wrong_verifier` | 400 `invalid_grant` | |
| T-P3 | `Token_rejects_verifier_from_a_different_authorization` | 400 — cross-authorization confusion closed | |
| T-P4 | `Token_fails_closed_on_a_plain_method_code` | seed a `plain` code directly (unreachable via `/connect/authorize`). Redemption computes S256 unconditionally, so an unhashed challenge never matches → 400. Fails closed *despite* the endpoint never reading `CodeChallengeMethod` | flags `OidcToken.CodeChallengeMethod` as written-but-never-read |
| T-P5 | `Token_succeeds_without_pkce_when_no_challenge_was_set` | 200 | |

### T.6 redirect_uri, expiry, scope

| # | Test | Expected | Pins |
|---|---|---|---|
| T-D1 | `Token_rejects_mismatched_redirect_uri` | 400 `invalid_grant` "redirect_uri mismatch" | |
| T-D2 | `Token_missing_redirect_uri_is_invalid_request_not_invalid_grant` | 400 `invalid_request` — a *different* code, caught earlier. Pin it so a refactor merging the paths doesn't silently change the wire contract | |
| T-D3 | `Token_rejects_redirect_uri_differing_by_trailing_slash` | 400 `invalid_grant` — ordinal, no normalization | |
| T-E1 | `Token_rejects_expired_code` | 400; code→`expired` (not `revoked` — a timeout is not theft) | |
| T-E2 | `Token_rejects_expired_refresh_token` | 400; **DB untouched** — this path is read-only, unlike the code path. Harmless inconsistency; pin it so it's deliberate | |
| T-S1 | `Token_refresh_narrows_scopes_when_AllowedScopes_shrinks` | 200; the removed scope is gone from the new token and persisted narrower | revoking a scope takes effect next refresh |
| T-S2 | `Token_refresh_ignores_an_injected_scope_parameter` | 200 with the original scopes — the handler never reads `scope` on this grant. Not exploitable (intersection is always against stored scopes) but pin that it is ignored rather than honoured | |

### T.7 Grant gating and machine scopes — open findings

| # | Test | Expected | Pins |
|---|---|---|---|
| T-G1 | `Token_rejects_code_grant_for_client_not_allowing_it` | 400 `unauthorized_client` | |
| T-G2 | `Token_rejects_client_credentials_grant_for_client_not_allowing_it` | 400 `unauthorized_client` | |
| T-G3 | **[EXPECTED-FAIL]** `Token_rejects_refresh_grant_for_client_not_allowing_it` | **should** be 400 `unauthorized_client`; **currently 200** — `HandleRefreshTokenGrant` has no `AllowedGrantTypes` check at all, unlike the other two handlers | **O8** (first half) |
| T-G4 | **[EXPECTED-FAIL]** `Token_does_not_mint_a_refresh_token_when_the_grant_is_not_allowed` | **should** omit it; **currently** every code redemption mints and stores a refresh token unconditionally — no check against `AllowedGrantTypes` or `offline_access`, so every browser client silently receives a 14-day credential it never asked for | **O8** (second half) |
| T-G5 | **[EXPECTED-FAIL]** `Token_client_credentials_without_scope_does_not_grant_everything` | **currently** an omitted `scope` grants **all** `AllowedScopes`, `api.admin` included — least privilege violated by omission | **O14** |
| T-G6 | `Token_client_credentials_rejects_scope_outside_AllowedScopes` | 400 `invalid_scope`, whole request fails, no partial grant | |

### T.10 Scope integrity — the token and its record must agree (N11)

The JWT is minted from the scopes that resolve to a defined, **enabled** `OidcScope`; the token document used to record the *requested* list. Introspection reads the document, so it over-reported — and a resource server has no way to notice. These are in `OidcScopeIntegrityTests`.

| # | Test | Expected | Pins |
|---|---|---|---|
| T-S1 | `A_machine_token_carries_the_scopes_it_asked_for` | `scope` echoed and the JWT's `scope` claims match — so the refusals below are not passing trivially | |
| T-S2 | `A_disabled_scope_is_refused_rather_than_dropped` | 400 `invalid_scope` naming the scope. No user, no consent step: silently issuing less produces a client that fails later, far from the cause | **N11** |
| T-S3 | `The_stored_record_matches_what_the_token_carries` | introspection's `scope` equals the JWT's claims | **N11** |
| T-S4 | `Disabling_a_scope_narrows_the_next_refresh_and_says_so` | the refreshed token drops the scope, the response announces the narrowing (RFC 6749 §5.1), and introspection agrees. A refresh token outlives the configuration it was minted under, so this is the window in which an operator's revocation takes effect or does not | **N11** |

### T.8 Enumeration oracles

| # | Test | Compare | Pins |
|---|---|---|---|
| T-O1 | **[CHARACTERIZATION]** `Token_distinguishes_unknown_client_from_bad_secret` | unknown client → `{"error":"invalid_client"}` with **no** `error_description`; known client + wrong secret → the same error **with** a description. That difference binary-searches the whole `client_id` namespace with no secret needed. **Invert this assertion when O15 is fixed** | **O15** |
| T-O2 | **[CHARACTERIZATION]** `Token_distinguishes_never_issued_code_from_expired_code` | different `error_description` text. Narrower (needs a captured code first), same bug class | **O15** |
| T-O3 | `Token_refresh_grant_has_no_such_oracle` | never-issued and expired both produce the identical message from one shared branch — **this grant is already right; use it as the model when fixing T-O1/T-O2** | contrast |
| T-O4 | `Token_timing_unknown_vs_known_client` | unknown returns before any PBKDF2; known runs 100k iterations per unexpired secret. **Do not gate CI on HTTP wall-clock timing.** If asserted at all, wide margins over many reps, informational only; the durable form is a benchmark, not an e2e test | O15 (timing half) |

### T.9 Protocol basics and token shape

| # | Test | Expected | Pins |
|---|---|---|---|
| T-M1 | `Token_rejects_unsupported_grant_type` | 400 `unsupported_grant_type` | |
| T-M2 | `Token_rejects_non_form_content_type` | 400 `invalid_request` | |
| T-M3 | `Token_rejects_missing_required_parameters` | 400 `invalid_request`, each parameter omitted in turn | |
| T-M4 | **[EXPECTED-FAIL]** `Token_multi_audience_access_token_carries_every_audience` | two granted scopes with different `Audiences` → **currently only the first reaches the token.** `OidcTokenGenerator` builds `new ClaimsIdentity(claims)` *before* appending the remaining audiences, and the constructor copies the list rather than aliasing it, so the later `claims.Add` calls go nowhere. Fails closed (narrows), but the code's own comment describes behaviour it does not have | **O19**, confirmed by reading, not just cited |
| T-M5 | `Token_id_token_nonce_round_trips` | the original `nonce` appears in the `id_token`. Regression guard only: the value is right today despite `OidcToken.State` being the field that holds it, and a future refactor that also stores the real OAuth `state` there would break this silently | O20 |

## L — `/connect/login`, `/connect/two-factor`, logout, session

Source: `Endpoints/{Login,TwoFactor,Logout,ConnectPage}.cs`, `SparkAuthenticationExtensions.SanitizeReturnUrl`, and the antiforgery middleware in `SparkMiddleware.cs:181-201`.

**These are plain `HttpClient` + `CookieContainer` tests**, in the style of `XsrfCookieFlagTests` — the pages are server-rendered HTML, so Playwright buys nothing here. Reserve the browser for anything client-rendered, of which this surface has none.

**Read the lockout threshold off the host's `IdentityOptions`** rather than hardcoding 5, so a fixture that overrides it doesn't silently make the lockout tests vacuous.

### L.1 Antiforgery — the wiring is confirmed working

| # | Test | Request | Expected | Pins |
|---|---|---|---|---|
| L-A1 | `Login_succeeds_with_valid_token_and_issues_session` | GET first, then POST with valid creds + correct field + cookie | 302 to `returnUrl`, app cookie set | **positive control — without it the rest of L.1 passes against a dead endpoint** |
| L-A2 | `Login_rejects_post_without_antiforgery_field` | cookie present, field omitted | 400; **no app cookie, and the lockout counter does not move** — the middleware returns before `next()`, so the handler never runs | O3 |
| L-A3 | `Login_rejects_post_with_no_cookie_and_no_field` | fresh client, no GET | 400 | O3 |
| L-A4 | `Login_rejects_field_without_cookie` | correct field, cookie stripped | 400 | O3 |
| L-A5 | `Login_rejects_cookie_without_field` | cookie present, field stripped | 400 | O3 |
| L-A6 | `Login_rejects_token_from_a_different_session` | session B's cookie + session A's field | 400 — the token is bound to the cookie's secret | O3 |
| L-A7 | `Login_rejects_correct_value_under_the_wrong_field_name` | right value, wrong name | 400 — no header is set, so lookup falls to the exact field name | O3 |
| L-A8 | `Login_accepts_a_token_minted_on_the_two_factor_page` | same session, token harvested from the other page | **succeeds — expected, not a bug.** Tokens are bound to the session cookie, not the route; validity still requires being the legitimate browser for that session | documents the real boundary so this is never filed as a defect |
| L-A9…L-A14 | the same six cases against `/connect/two-factor` | | identical outcomes | O3 |

### L.2 `returnUrl`

`SanitizeReturnUrl` requires non-empty, no CR/LF, leading `/`, and `[1]` not `/` or `\`. ASP.NET Core decodes the query before the handler sees it, so one level of encoding collapses before the check.

| # | Test | Value | Expected | Pins |
|---|---|---|---|---|
| L-R1 | `Login_get_renders_sanitized_returnUrl_in_the_hidden_field` | `http://attacker.test/phish` | hidden field is `/`, never the attacker value | F11 |
| L-R2 | `Login_post_rejects_absolute_url` | `https://attacker.test/phish` | 302 → `/` | F11 |
| L-R3 | `Login_post_rejects_protocol_relative` | `//attacker.test` | 302 → `/` | F11 |
| L-R4 | `Login_post_rejects_backslash_authority` | `/\evil.com` | 302 → `/` | browsers coerce `\`→`/` |
| L-R5 | `Login_post_rejects_single_encoded_protocol_relative` | `%2F%2Fevil.com` | 302 → `/` — decodes to `//evil.com` **before** the check, so the `//` rule catches it | pins decode-then-check ordering |
| L-R6 | `Login_post_rejects_double_encoded` | `%252F%252Fevil.com` | 302 → `/` — but for a **different reason**: it decodes to a literal `%2F%2F…` which fails the leading-slash rule. **Document this**, or someone "simplifying" the `//` check will reopen the path | F11 |
| L-R7 | `Login_post_rejects_javascript_uri` | `javascript:alert(1)` | 302 → `/` | F11 |
| L-R8 | `Login_post_rejects_whitespace_prefix` | `⎵//evil.com` | 302 → `/` | F11 |
| L-R9 | `Login_post_rejects_crlf_no_header_injection` | `/ok%0d%0aSet-Cookie:%20x=1` | 302 → `/`, and **no injected `Set-Cookie` in the response** | header splitting |
| L-R10 | `Login_returnUrl_cannot_chain_into_an_open_redirect` | `/connect/logout?post_logout_redirect_uri=https://evil.com` | login redirects there (a relative path is legal), and the **second hop** 400s because the URI isn't registered | the "passes sanitising but redirects onward" case — no gadget today, **contingent on O12** |
| L-R11…L-R13 | the same against `/connect/two-factor`, including the recovery-code path | | 302 → `/` | F11 |

### L.3 Lockout and enumeration

| # | Test | Expected | Pins |
|---|---|---|---|
| L-L1 | `Login_engages_lockout_after_the_configured_failures` | after N wrong passwords, the lockout message appears — **and a subsequent attempt with the correct password is also refused** | **O4** — previously unreachable code |
| L-L2 | `Login_lockout_message_does_not_reveal_password_correctness` | locked-out + right password and locked-out + wrong password produce the same text | the intended asymmetry is locked-vs-not, nothing finer |
| L-L3 | `Login_lockout_precedes_the_two_factor_step` | a locked-out 2FA user submitting the **correct** password lands on the lockout error, not `/connect/two-factor` | inferred from stock `SignInManager.PreSignInCheck` — **verify empirically, this is not Spark's own code** |
| L-L4 | `Login_unknown_and_wrong_password_return_identical_text` | both `Invalid email or password.` | confirmed by reading — no message oracle |
| L-L5 | **[CHARACTERIZATION]** `Login_unknown_email_responds_faster_than_a_wrong_password` | unknown short-circuits before PBKDF2; known runs it | **O27**, accepted risk — see the findings entry. Keep the test informational, never CI-gating: wall-clock assertions over HTTP are too noisy to gate on |

### L.4 Two-factor integrity

| # | Test | Expected | Pins |
|---|---|---|---|
| L-T1 | `TwoFactor_post_without_the_password_step_is_rejected` | a fresh client posting a syntactically valid code with no partial-auth cookie fails — there is no `TwoFactorUserId` principal to resolve | **the "jump straight to 2FA" case**; partial-auth lives in a separate scheme |
| L-T2 | `Consent_while_only_half_authenticated_redirects_to_login` | password done, 2FA not — `/connect/consent` bounces to login because `context.User` isn't authenticated under the application scheme | the same invariant proved from the consent side |
| L-T3 | `TwoFactor_recovery_code_is_single_use` | first use succeeds, second fails | |
| L-T4 | `TwoFactor_brute_force_eventually_locks_out` | repeated wrong codes trip the same lockout counter | **still open** — not yet written; see the note below |

### L.4b Two-factor — implemented

26 cases in `OidcTwoFactorSecurityTests`, all green. Grouped as:

- **Succeeds:** the password step redirects to the second factor and issues no application cookie; a valid authenticator code completes sign-in; a valid recovery code does too; and a fully completed sign-in can drive `/connect/authorize` (the positive control — without it every skip case below would pass against a flow that simply never works).
- **Cannot be skipped:** the partial-authentication cookie cannot drive `/connect/authorize`, cannot reach the consent page, and cannot POST consent; and the two-factor form cannot be completed by someone who never passed the password step. This is the property the whole feature rests on — half-authenticated must be indistinguishable from unauthenticated to everything downstream.
- **Wrong credentials:** invalid code, invalid recovery code, empty submission, **another user's valid authenticator code**, and another user's recovery code.
- **Recovery-code lifecycle:** single-use, spending one leaves the rest usable, and the remaining count decrements.
- **Antiforgery and returnUrl:** the POST is rejected without a token and with a token from another session; four off-origin `returnUrl` shapes are refused on the completed sign-in; the rendered form carries no off-origin destination; the error box does not reflect supplied text.
- **Account state:** a locked-out account never reaches the second factor (lockout precedes password evaluation, so the correct password gains nothing — this settles L-L3 empirically rather than by inference); disabling 2FA returns the account to a single step.

**Testing note worth keeping.** A valid authenticator code cannot be obtained from Identity: `AuthenticatorTokenProvider.GenerateAsync` deliberately returns an empty string, because in the real flow the code comes from the user's phone and the server only ever validates. The fixture therefore implements RFC 6238 exactly as `Rfc6238AuthenticationService` does — HMAC-SHA1 over a big-endian 30-second timestep, dynamically truncated to six digits, no modifier — and plays the phone. The first run of these tests failed with `error=missing_code`, which is what surfaced this.

**Not yet covered on this surface:** brute-force lockout on the 2FA step specifically (L-T4 above), and whether a failed second factor counts toward the same lockout counter as a failed password.

### L.5 Session and `rememberMe`

| # | Test | Expected | Pins |
|---|---|---|---|
| L-S1 | `Login_mints_a_fresh_auth_cookie` | no app cookie exists before sign-in; one is minted after | classic session fixation does not apply — cookie-auth creates the ticket only at `SignInAsync`. Record as verified-sound rather than leaving it an open question |
| L-S2 | `Login_as_a_different_user_cannot_complete_a_pending_request` | user A starts the flow; user B follows the same login link and authenticates as B; the consent hop 400s | the subject binding again, reached from the login side — **login has no knowledge of `request_id`, and does not need any** |
| L-S3 | `Logout_clears_the_auth_cookie` | expired `Set-Cookie`, and a protected route is then unauthenticated | |
| L-S4 | `Login_without_rememberMe_issues_a_session_cookie` | non-persistent cookie | regression pin for the O4 half that was hardcoded `true` |
| L-S5 | `Login_with_rememberMe_issues_a_persistent_cookie` | future expiry | |

### L.6 Logout

| # | Test | Expected | Pins |
|---|---|---|---|
| L-G1 | `Logout_redirects_to_a_registered_post_logout_uri` | 302 to it | |
| L-G2 | `Logout_rejects_an_unregistered_post_logout_uri` | 400 | |
| L-G3 | `Logout_appends_state_with_a_single_question_mark` | `…/done?state=abc` | `Logout.cs` is the one place that builds URLs correctly — **use it as the model when fixing O21 elsewhere** |
| L-G4 | **[EXPECTED-FAIL]** `Logout_rejects_another_apps_post_logout_uri` | currently **succeeds** — validation spans every enabled application, so any registered URI is accepted in any client's logout | **O12**, confirmed still reproducing |
| L-G5 | `Logout_is_idempotent_without_a_session` | 302 to the validated URI even when already signed out | |
| L-G6 | `Logout_requires_no_antiforgery_token_by_design` | a bare GET succeeds | **accepted risk, decided** — front-channel logout must be plain-navigable; forcing a sign-out is a nuisance, not an escalation. Pinned so the absence reads as a decision |

## R — introspection, revocation, userinfo, discovery, JWKS

Source: `Endpoints/{Introspection,Revocation,UserInfo,Discovery,Jwks}.cs`, `Services/{AccessTokens,OidcIssuer,OidcSigningKeyService}.cs`.

This surface produced the audit's only **Critical** (N1, now fixed) precisely because it was the part nobody had read. Sections R.4 and R.5 cover code that had never been reviewed at all before 2026-08-08.

### R.1 Caller authentication

| # | Test | Expected | Pins |
|---|---|---|---|
| R-A1 | `Introspect_rejects_missing_client_credentials` | 400 `invalid_request` | |
| R-A2 | `Introspect_rejects_unknown_client` | 401 `invalid_client` | |
| R-A3 | `Introspect_rejects_wrong_secret` | 401, same shape as R-A2 — no oracle | O15 |
| R-A4 | `Introspect_rejects_disabled_client` | 401 `invalid_client` | |
| R-A5 | `Revoke_rejects_wrong_secret_and_leaves_token_live` | 401, **and** the token still introspects `active:true` afterwards — assert the non-effect, not just the status | |
| R-A6 | `Introspect_rejects_non_form_content_type` | 400 `invalid_request` | |

### R.2 Introspection — must succeed

| # | Test | Expected | Pins |
|---|---|---|---|
| R-I1 | `Introspect_reports_own_valid_access_token_active` | `active:true`, correct `sub`, `scope`, `token_type`, `exp`/`iat`, and `aud` present | O5, N2 |
| R-I2 | `Introspect_reports_own_valid_refresh_token_active` | `active:true` with the granted scopes | O5 |
| R-I3 | `Introspect_reports_machine_token_active_with_no_sub` | `active:true`, `sub` absent | machine tokens carry no subject |
| R-I4 | `Introspect_resolves_regardless_of_token_type_hint` | a live access token with `token_type_hint=refresh_token` still reports `active:true` | **N3** — was a false negative |

### R.3 Introspection — must refuse

| # | Test | Expected | Pins |
|---|---|---|---|
| R-I5 | `Introspect_reports_revoked_access_token_inactive` | `active:false` | **O5 — the whole point of that fix** |
| R-I6 | `Introspect_reports_revoked_refresh_token_inactive` | `active:false` | O5 |
| R-I7 | `Introspect_reports_expired_tokens_inactive` | `active:false`, both types | |
| R-I8 | `Introspect_reports_never_issued_token_inactive` | `active:false`, no 500 | |
| R-I9 | `Introspect_reports_garbage_string_inactive` | `active:false`, no 500 or stack trace | |
| R-I10 | `Introspect_rejects_token_signed_by_a_foreign_key` | `active:false` — signature fails before any DB lookup | forgery |
| R-I11 | `Introspect_rejects_alg_none_token` | `active:false` | forgery |
| R-I12 | `Introspect_rejects_hmac_confusion_token` | HS256 signed with the RSA public key as the secret → `active:false` | pins that `IssuerSigningKey` is set directly rather than resolved from the header |
| R-I13 | `Introspect_rejects_tampered_payload_with_original_signature` | `active:false` | integrity |
| R-I14 | `Introspect_ignores_the_tokens_own_kid` | forged `kid` + attacker key → `active:false` — the key is pinned server-side, never chosen by the token | forgery |
| R-I15 | `Introspect_rejects_token_from_a_different_issuer` | `active:false` | O7 regression pin |

### R.4 Ownership — the Critical

| # | Test | Expected | Pins |
|---|---|---|---|
| R-N1 | `Introspect_refuses_another_clients_refresh_token` | client B introspecting client A's refresh token gets `active:false` — **not** `sub` and `scope` of A's user | **N1 (fixed)** |
| R-N2 | `Introspect_refuses_another_clients_access_token` | as above; before the fix this returned `active:true` *and* the true owning `client_id`, confirming a cross-client read | **N1 (fixed)** |
| R-N3 | `Introspect_ownership_failure_is_indistinguishable_from_never_issued` | the R-N1/R-N2 responses are byte-identical to R-I8's | the gate must not become its own oracle |

### R.5 Revocation

| # | Test | Expected | Pins |
|---|---|---|---|
| R-V1 | `Revoke_own_access_token_takes_effect` | 200, then `active:false` | |
| R-V2 | `Revoke_refresh_token_cascades_to_its_access_tokens` | every token under the same `AuthorizationId` goes inactive | O1 made this reachable |
| R-V3 | `Revoke_access_token_resolves_via_jti` | 200 and actually inactive — proves the `jti` fallback fires, since an access token's document is not keyed by the presented value | O5 |
| R-V4 | `Revoke_works_regardless_of_token_type_hint` | an access token revoked with `token_type_hint=refresh_token` **is** revoked. Previously it matched nothing, revoked nothing, and still answered 200 — a caller responding to a breach was told the credential was dead while it stayed live | **N3 (fixed)** |
| R-V5 | `Revoke_already_revoked_token_returns_200` | 200, no error, no state change | RFC 7009 §2.2 |
| R-V6 | `Revoke_another_clients_token_returns_200_but_does_not_revoke` | 200 (the RFC forbids revealing failure) **and** the token still introspects `active:true` for its owner | the gate introspection was missing |
| R-V7 | `Revoke_never_issued_token_returns_200` | 200, no document created | |
| R-V8 | `Revoke_negative_outcomes_are_indistinguishable` | foreign-client, never-issued and already-revoked produce identical status, body and headers | RFC 7009 non-disclosure |

### R.6 UserInfo

| # | Test | Expected | Pins |
|---|---|---|---|
| R-U1 | `UserInfo_returns_only_claims_for_granted_scopes` | `openid profile email` → the mapped claims and no others | |
| R-U2 | `UserInfo_with_openid_only_returns_sub_alone` | no `name`, `email`, `role` keys at all | ungranted scopes yield nothing |
| R-U3 | `UserInfo_rejects_missing_or_malformed_bearer` | 401 + `WWW-Authenticate`, for absent header, wrong scheme, and empty value | |
| R-U4 | `UserInfo_rejects_revoked_access_token` | 401 | **O5 — "kept serving claims" was the bug** |
| R-U5 | `UserInfo_rejects_expired_access_token` | 401 | |
| R-U6 | `UserInfo_rejects_token_without_sub` | a machine token → 401 | |
| R-U7 | `UserInfo_rejects_token_whose_user_was_deleted` | 401 | |
| R-U8 | `UserInfo_rejects_an_id_token_presented_as_an_access_token` | 401 — id tokens carry no `jti` and have no governing record, so they cannot resolve. Token-type confusion is closed incidentally; pin it so it stays closed | |
| R-U9 | `UserInfo_rejects_a_refresh_token_presented_as_a_bearer` | 401, no 500 on non-JWT input | |
| R-U10 | `UserInfo_rejects_forged_and_foreign_signed_tokens` | 401 for `alg=none` and attacker-key variants | forgery |
| R-U11 | `UserInfo_drops_claims_for_a_scope_disabled_after_issuance` | 200, but that scope's claims are absent — resolution re-checks `Enabled` live. **Document as intended**: it is dynamic claim revocation, not a bug | |

### R.7 Discovery and JWKS

| # | Test | Expected | Pins |
|---|---|---|---|
| R-D1 | `Discovery_issuer_matches_configuration_exactly` | equals the configured `Issuer`, no trailing slash | O7 |
| R-D2 | `Discovery_advertised_endpoints_all_resolve` | every `*_endpoint` and `jwks_uri` returns something other than 404 — cross-check against the route table rather than trusting the document | |
| R-D3 | `Discovery_advertises_only_what_is_enforced` | `response_types_supported=["code"]` and `code_challenge_methods_supported=["S256"]`, **and** `/connect/authorize` actually rejects `token`, `id_token` and `plain` | advertised-vs-enforced |
| R-D4 | `Discovery_omits_disabled_and_hidden_scopes` | neither appears in `scopes_supported` | |
| R-D5 | `Discovery_and_jwks_need_no_credentials` | 200 unauthenticated — these are public by design | |
| R-D6 | `Forged_host_header_does_not_move_the_issuer` | spoofed `Host` on discovery, introspection and userinfo changes nothing | **O7 regression pin** |
| R-J1 | `Jwks_exposes_no_private_key_material` | keys carry only `kty/use/kid/alg/n/e` — assert the **absence** of `d/p/q/dp/dq/qi` | |
| R-J2 | `Jwks_kid_matches_the_kid_in_issued_tokens` | exact match, so a relying party can select the key | |
| R-J3 | `Jwks_published_key_verifies_a_real_token` | verify a freshly issued token offline using only the published `n`/`e` | end-to-end trust chain |
| R-J4 | `Jwks_publishes_exactly_one_key` | length 1 — pins today's no-overlap behaviour so N4's fix visibly changes it | **N4** |
| R-J5 | **[EXPECTED-FAIL]** `Jwks_kid_changes_when_the_key_is_replaced` | **currently** the literal `"spark-oidc-key-1"` survives rotation, so a relying party caching by `kid` keeps the stale key | **N4** |
| R-J6 | **[EXPECTED-FAIL]** `Tokens_issued_before_rotation_remain_valid_until_expiry` | **currently** they fail instantly — one key is held, so rotation is a hard cutover with no overlap window | **N4** |

### R.8 Signing key service

| # | Test | Expected | Pins |
|---|---|---|---|
| R-K1 | `SigningKey_missing_in_production_fails_startup` | throws naming the path; the app must not invent a key and start | fails closed |
| R-K2 | `SigningKey_auto_generated_in_development_only` | generated and persisted; **reused** on the next start, not regenerated | |
| R-K3 | `SigningKey_is_stable_across_restarts` | tokens signed in run 1 verify against JWKS from run 2 | |
| R-K4 | `SigningKey_corrupt_file_fails_loudly` | a clean exception, never a silent fallback to a fresh in-memory key | |

### R.9 Audience — the open gap

| # | Test | Expected | Pins |
|---|---|---|---|
| R-X1 | `Introspection_reports_aud_so_a_resource_server_can_check_it` | `aud` present in the response | **N2**, disclosure half — fixed |
| R-X2 | **[CHARACTERIZATION]** `Audience_is_not_enforced_anywhere` | a token minted for resource A introspects `active:true` and is accepted at `/connect/userinfo` regardless of audience. Documents the current gap; invert if N2's enforcement half is ever decided in favour of enforcing here | **N2**, open |

## M — the admin screens (M12.7)

Registration is a PersistentObject, so it inherits the authorization pipeline and the antiforgery
middleware rather than running as a parallel API. These cases go through `/spark/po/{type}` —
`OidcApplicationActionsTests` calls the Actions class directly, which proves the rules and nothing
about whether anything reaches them. In `OidcAdminRouteTests`, against the model the **synchronizer
generates**, not a hand-authored one.

| # | Test | Expected | Pins |
|---|---|---|---|
| M-A1 | `Registering_a_client_through_the_route_persists_it` | 201, document stored with its redirect URI | the happy path the refusals below are measured against |
| M-A2 | `A_client_registered_through_the_route_can_obtain_a_token` | `client_credentials` succeeds against a client and scope registered entirely through the screens | the claim M12.7 rests on, end to end |
| M-A3 | `The_secret_an_operator_types_is_stored_hashed` | stored value is not the plaintext, and `ClientSecretHasher.Verify` accepts what was typed | a secret that cannot authenticate against its own registration is this milestone's failure mode |
| M-A4 | `A_refused_registration_returns_the_reason_rather_than_a_500` | 400 with the message in the standard `errors` envelope | **N12** — before this, an Actions refusal was an unhandled exception with no body |
| M-A5 | `An_unsupported_grant_type_is_refused_at_the_route` | 400 naming the supported grants | |
| M-A6 | `A_duplicate_client_id_is_refused_at_the_route` | 400; two applications sharing a `client_id` makes impersonation a matter of index ordering | **O17** |
| M-A7 | `A_scope_name_with_whitespace_is_refused_at_the_route` | 400 — scopes are space-delimited on the wire | **N6**-adjacent |
| M-A8 | `Registration_requires_an_antiforgery_token` | 400 and **nothing written** — a registration a cross-site POST can perform is a client-registration endpoint open to any page the operator visits | |

### Registration — the model itself

`OidcAdminRegistrationTests` runs a context implementing `IOidcApplicationContext` through the real
`IModelSynchronizer`. Worth testing rather than asserting: an earlier draft of the plan concluded
the opposite from reading `ModelLoader` alone and proposed a registry for a problem that does not
exist.

| # | Test | Expected |
|---|---|---|
| M-R1 | `A_library_entity_on_the_context_becomes_a_persistent_object` | `OidcApplication.json` generated, `clrType` still pointing into the package |
| M-R2 | `The_generated_model_carries_the_fields_an_operator_must_set` | `ClientId`, `RedirectUris`, `AllowedScopes`, `AllowedGrantTypes`, `Enabled`, `MayIntrospectAnyAudience` — the audit found each failing silently when wrong |
| M-R3 | `Scopes_are_registered_alongside_applications` | `OidcScope.json` with `Audiences` (D11) |

### Not covered

- **Authorization on these screens is demonstrated, not asserted.** HR's `security.json` grants them to Administrators alone, but no test drives the routes as a non-administrator. `SparkEndpointFactory` opts into `AllowAnonymousAccess()`, so a case would have to override `IAccessControl` — worth adding when M5 (row-level authz) is picked up, since it needs the same fixture.
- The Angular screens themselves. These are generated pages with no bespoke code; the four demo ClientApps have not been exercised since the IdP port either.
