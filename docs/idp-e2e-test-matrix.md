# IdentityProvider — e2e security test matrix

Companion to [PRD-CoverageHandoff.md](./PRD-CoverageHandoff.md) §8 and [coverage-handoff-plan.md](./coverage-handoff-plan.md) M12.6, and the evidence half of [findings-identity-provider-audit.md](./findings-identity-provider-audit.md).

**The invariant every case below serves:** no tampering reachable from a URL, a form field, a header or a cookie can make the provider mint a credential for a destination, a client, or a subject the attacker chose — and no legitimate flow is broken in the process.

## How to read this

Each case gives a test name, precondition, the exact request with the tampered element named, and the expected outcome. Outcomes are precise about **whether a credential is minted**, because "returns 400" and "mints nothing" are different assertions and only the second one is the security property.

Cases are prefixed by area: **A-** authorize/consent, **T-** token endpoint, **L-** login/session, **R-** resource-server surface (introspect/revoke/userinfo/JWKS).

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

*Awaiting the token-grants reviewer; section reserved.*

## L — `/connect/login`, `/connect/two-factor`, logout, session

*Awaiting the login-session reviewer; section reserved.*

## R — introspection, revocation, userinfo, discovery, JWKS

*Awaiting the token-lifecycle reviewer; section reserved. This surface has never been audited — expect new findings rather than only test cases.*
