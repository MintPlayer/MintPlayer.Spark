# Findings — `MintPlayer.Spark.IdentityProvider` security audit (M12.3)

**Date:** 2026-08-08
**Scope:** the OIDC provider ported from `feat/identity-provider` in `d51f9fd`. Never security-reviewed before this pass.
**Method:** four parallel adversarial reviewers (authorize/consent/login, token endpoint, data model, endpoints/keys) plus direct verification. **The endpoints/keys reviewer never reported — see "Not yet audited".**
**Condition this satisfies:** shipping `client_credentials` was made conditional on the package being proven sound (plan D1).

Severity uses the reviewers' ratings. "Fixed in" refers to commits on `feat/spark-hardening-m0`.

---

## 1. Fixed

| # | Severity | Finding | Fixed in |
|---|---|---|---|
| F1 | High | `VerifyClientSecret` used **unsalted single-round SHA-256** and `string.Equals` (not constant-time). Hashing existed only in consumer seed code, so every consumer reimplemented it and a mismatch failed silently as `invalid_client`. | `19f4bf2` |
| F2 | High | Application claims emitted as `client_{Type}`, so `{Type:"group"}` became `client_group`, matched nothing in `ClaimsGroupMembershipProvider`, and **every machine token authorized as a member of no group**. | `19f4bf2` |
| F3 | Critical | **Authorization-code replay via stale index.** Codes and refresh tokens were found by index query filtered on `Status == "valid"`; RavenDB indexes are eventually consistent, so a redeemed code still read back as valid until the index caught up. | `dfab40a` |
| F4 | High | **Bearer values stored in plaintext.** `OidcToken.ReferenceId` held the raw code/refresh token. A database dump was a live-credential dump. | `dfab40a` |
| F5 | High | **No refresh-token reuse detection.** Replaying a rotated token merely failed; a thief who redeemed first took over the chain permanently and silently. | `dfab40a` (partial — see O1) |
| F6 | Critical | **`/connect/consent` re-validated nothing** — not `redirect_uri`, `AllowedScopes`, `RequirePkce`, nor `Enabled`. One victim click on a consent screen naming a genuinely trusted client, carrying the attacker's `redirect_uri` and `code_challenge`, yielded full account takeover. Granted scopes were persisted, making escalation durable. | `09dc3cb`, `697097e` |
| F7 | Critical | **Authorization codes not bound to the redeeming client.** `codeToken.ApplicationId` was never compared to the presenter. The `redirect_uri` check compares against the code's *own* stored value — the issuing client's registered URI, which is public — so it provided no binding. A public client could redeem a confidential client's code **presenting no secret at all**. RFC 6749 §4.1.3. | `09dc3cb` |
| F8 | Critical | **Refresh tokens not client-bound, scopes never re-validated.** Any client could present another's refresh token and receive one carrying the original's subject and scopes. | `09dc3cb` |
| F9 | High | **Client authentication skipped on `ClientType == "confidential"`** — a case-sensitive ordinal compare against an unvalidated free-form string. `"Confidential"` or a trailing space silently disabled it. | `09dc3cb` |
| F10 | High | **Application claims merged into delegated tokens.** A service client configured `{Type:"group", Value:"Administrators"}` made **every user who signed in through it an administrator** — by consenting, which they do themselves. F2's prefix removal widened this without closing it. | `09dc3cb` |
| F11 | High | **Open redirect on `returnUrl`** at `/connect/login` and `/connect/two-factor` — sending a freshly authenticated user off-origin. The repo already shipped `SanitizeReturnUrl` for this exact bug class; it was `private` and uncalled. | `697097e` |

---

## 2. Open

### Highest value

**O1 — `AuthorizationId` is always `""`, so every revocation cascade is dead code.** `Authorize.cs` hardcodes it with the comment *"Will be linked when consent is created"*; nothing ever fills it, and `Token.cs` propagates the empty string onto every issued token. Consequences: `Revocation.cs`'s access-token cascade **has never executed once**, and the reuse-detection chain revocation added in `dfab40a` currently revokes only the presented token. *Fix: thread the real `OidcAuthorization.Id` through code issuance.* **Do this first — it un-breaks two paths at once.**

**O2 — Parallel-redemption race (Medium→High).** `dfab40a` fixed replay-by-staleness via point-loads, **not** replay-by-concurrency. Two simultaneous redemptions still both load, both validate, and both save; there is no `UseOptimisticConcurrency` or compare-exchange anywhere in the package. *Fix: optimistic concurrency on the redemption session, or compare-exchange on the code value.*

**O3 — No CSRF protection on any `/connect/*` POST (High).** `MapIdentityProviderEndpoints` stamps no `IAntiforgeryMetadata`, and the handlers read the body via `ReadFormAsync` rather than `[FromForm]`, so neither Spark's middleware nor the built-in `UseAntiforgery()` covers them. Mitigated in practice only by the cookie's default `SameSite=Lax` — a browser default, not a control, and lost the moment a host sets `SameSite=None` (routine for SPA/OIDC; HR and Fleet already call `ConfigureApplicationCookie`). *Fix: `RequireAntiforgeryTokenAttribute(true)` on the three POST routes + hidden token field, exactly as `Authorization/Endpoints/Logout.cs` already does.*

**O4 — `lockoutOnFailure: false` (High).** `Login.cs` passes `false`, so failed passwords never call `AccessFailedAsync`. `/connect/login` is an unauthenticated, unthrottled, **unlimited password oracle**, and the `IsLockedOut` branch is unreachable. `SparkUser` fully supports lockout, and `MapIdentityApi`'s own login passes `true` — this endpoint is strictly weaker than the API beside it. `isPersistent: true` is also hardcoded.

**O5 — Revoked access tokens keep working (High).** Access tokens are self-contained JWTs with **no `jti`**; `Introspection` and `UserInfo` validate signature and expiry only and never consult `OidcToken`. So `Status = "revoked"` has no effect, and introspection reports `active: true` for a revoked token — the one question RFC 7662 exists to answer. `client_credentials` tokens are unrevocable entirely.

**O6 — `Payload` stores the full signed access-token JWT in cleartext (High).** Written three times, **read zero times** — grep confirms no readers. Delete the property.

### Medium

- **O7** — `iss` derived from the attacker-controllable `Host` header on every issuance and validation path. A forged `Host` mints tokens claiming a different issuer, signed with the real key. *Fix: issuer from options; request-derived only in Development.*
- **O8** — `HandleRefreshTokenGrant` never checks `AllowedGrantTypes` (the other two grants do). Symmetrically, code redemption **always** mints a refresh token regardless of `AllowedGrantTypes` or `offline_access`, so every browser client silently receives a 14-day credential it never requested.
- **O9** — Consent duplicate-grant race and lost revocation: the existing-grant lookup rides an index, so concurrent consents create duplicate `OidcAuthorization` documents (revocation then revokes one, the other keeps auto-approving); and the grant is mutated without optimistic concurrency, so a consent POST can write `Status = "valid"` back over a concurrent `"revoked"`.
- **O10** — `AllowRememberConsent` and `ConsentLifetimeSeconds` are declared and **never read**. Remembered consent never expires and ignores the client's own setting.
- **O11** — `/connect/authorize` never checks `AllowedGrantTypes`, so a `client_credentials`-only client can drive users through the full interactive flow; rejection happens only at the token endpoint.
- **O12** — `post_logout_redirect_uri` validated against **every** enabled application, so one client's URI is accepted in another's logout. Also loads the whole application collection per logout.
- **O13** — Cleanup service deletes only `valid|redeemed`; **`revoked` and `expired` documents accumulate forever**. `Take(1000)` once per hour also caps throughput.
- **O14** — `client_credentials` grants **every** allowed scope when none is requested. Least privilege violated by omission.
- **O15** — Error responses distinguish unknown-client from bad-secret, and expired-code from invalid-code — client-id and code enumeration oracles. Plus a timing oracle: unknown clients return instantly while known ones run 100k PBKDF2 iterations.
- **O16** — `/connect/authorize` reads ambient `context.User`, which under `AddIdentityApiEndpoints` resolves the **bearer** scheme before the cookie. A Spark API bearer token can therefore drive the interactive flow headlessly and mint OIDC codes with no user present. *Fix: authenticate explicitly against `IdentityConstants.ApplicationScheme`.*
- **O17** — No uniqueness constraint on `OidcApplication.ClientId` or `OidcScope.Name`. Two documents sharing a `ClientId` means effective config is whichever the index returns first. `UserStore` already has the compare-exchange reservation pattern to copy.

### Low

- **O18** — PKCE comparison uses `string.Equals`, not `FixedTimeEquals`. Low impact (the stored side is the public challenge, not the verifier) but inconsistent with the deliberate timing hygiene in `VerifyClientSecret`.
- **O19** — Multi-audience access tokens silently drop all but the first: `new ClaimsIdentity(claims)` copies the list, so the later `claims.Add` never reaches the token. Fails closed, but the comment describes behaviour the code doesn't have.
- **O20** — `OidcToken.State` actually stores the **nonce**. Rename before someone stores the real `state` there and silently breaks ID-token replay binding.
- **O21** — Redirect URLs built with an unconditional `?`, so a registered URI containing a query string yields `…?x=1?code=…`. `Logout.cs` gets this right; factor out a helper.
- **O22** — `id_token` issued without checking for the `openid` scope; no `auth_time`/`azp`, so RPs cannot implement `max_age`.
- **O23** — Only `client_secret_post` is supported. Discovery advertises this honestly, but RFC 6749 §2.3.1 makes Basic mandatory-to-implement and SDKs defaulting to it will fail.
- **O24** — Login error text is reflected from the query string (HTML-encoded, so no XSS) — attacker-authored copy inside the real IdP's styled error box. Use error codes mapped to fixed strings.
- **O25** — RavenDB string equality is case-insensitive by default; `ClientId` lookups don't use `Exact()`.

---

## 3. The structural fix — bind the authorization request server-side

F6, F7's amplification, the scope escalation, and `nonce`/`code_challenge` tampering are all one defect: **`/connect/consent` is routed separately and re-derives the authorization request from browser input instead of referring to the validated one.** Every security parameter round-trips through the user agent and returns as untrusted input, so every hop must independently remember to re-validate.

The same defect appeared in **five** places (authorize, consent GET, consent POST, login `returnUrl`, two-factor `returnUrl`). All five are now correct; the sixth page someone adds will be wrong again, and nothing will fail loudly.

**Fix:** persist the validated request at the end of `Authorize.Handle` as a short-lived, single-use server-side document, and redirect to `/connect/consent?request_id=<opaque>`. The page renders from that record; the POST carries only `request_id` and the decision. `redirect_uri`, `scope`, `code_challenge` and `nonce` are then **never request parameters after the first hop** — there is nothing to re-validate and no way to forget. Being a point-load by id, it also closes O9's duplicate-grant race.

This is planned as **M12.5**.

---

## 4. Not yet audited

The endpoints/keys reviewer never reported. **Unreviewed surface:** `OidcSigningKeyService` (key generation, storage, permissions, rotation, multi-instance behaviour), `Jwks.cs` (private-key exposure), `Discovery.cs` (advertised-vs-enforced mismatches), `UserInfo.cs`, and the `Introspection`/`Revocation` caller-authentication model.

Partial coverage exists from the token reviewer: RS256 is hardcoded at both signing sites with no negotiation, so **`alg=none` is not reachable**; both verification paths pin `IssuerSigningKey` to the single RSA key rather than resolving from the token header, so **`alg` confusion is closed there**; and production refuses to auto-generate a key. That is not a substitute for reviewing the key service itself.

---

## 5. Also missing: no way to register an application

There is **no admin surface for `OidcApplication`**. It lived in `Demo/SparkId` on the branch (`OidcApplicationActions.cs` + `App_Data/Model/OidcApplication.json`), which was deliberately not ported. Today the documents — `RedirectUris`, `AllowedScopes`, `AllowedGrantTypes`, hashed secrets — must be seeded by hand.

Coverage cannot use `client_credentials` until a client can be registered and a secret minted. This is where `RedirectUris` and `AllowedScopes` get set correctly or not, so it is also a security surface. Either port the SparkId management screens or add a minimal registration API.

---

## 6. What is sound (verified — do not regress)

- **`redirect_uri` matching is exact ordinal**, no prefix/wildcard/subdomain, and is validated *before* any error redirect, so the classic error-leak-to-unvalidated-URI is absent.
- **Implicit and hybrid flows are unreachable** — strict `response_type != "code"` rejection, no `response_mode`, nothing ever lands in a fragment. Discovery agrees.
- **PKCE `plain` is rejected outright**; `RequirePkce` defaults `true`; S256 is recomputed correctly and re-verified at the token endpoint.
- **All empty-collection defaults fail closed** — the classic "empty means unrestricted" is genuinely absent throughout.
- **Expiry is enforced at use time for all three token types**, not merely by the cleanup service.
- **2FA cannot be skipped** by jumping ahead; partial-auth state lives in the framework's separate scheme, not a hand-rolled flag, so it is immune to the staleness class.
- **No user enumeration by message**; no XSS in the generated HTML (all interpolation is `HtmlEncode`d).
- **Email uniqueness is cluster-safe** via compare-exchange in `UserStore` — the right primitive to copy for O2 and O17.
- **`client_credentials` is the most correct of the three grants**: a genuinely public client cannot use it, `AllowedGrantTypes` and `AllowedScopes` are both enforced, and no `id_token` or refresh token is issued.
- Codes and refresh tokens are 256-bit CSPRNG values; code TTL is 5 minutes; token responses set `no-store`.
