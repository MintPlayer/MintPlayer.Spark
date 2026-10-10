# PRD / Plan: Spark IdentityProvider as a full identity-provider plugin

> **Shipped in PR #499 (2026-10-09). The remaining identity-provider work (open items from §0 item 6,
> the conformance plans that were not run, and Duende parity) now lives in
> [identity_provider_parity_PRD.md](identity_provider_parity_PRD.md).** The status line below is
> historical.

Status: **grilled, all decisions locked (§6). Implementation in progress: I0–I6 and I8–I10 done,
I11 partly; I7, I12–I14 and #490 D11 open (§0)** (2026-10-08). Branch:
`feat/464-490-pwa-external-login` (PR #499). Nothing pushed yet (R4).

## 0. Implementation status (resume here)

Every commit builds `apps/SparkId`, `tests/MintPlayer.Spark.Tests` and
`tests/MintPlayer.Spark.E2E.Tests` with 0 errors. **No test suite has been run** for any of this
(batched to the end). Expect failures in the sweep: tests asserting the old English page strings,
`/connect/*` response shapes (now HTML error pages), and the route snapshots.

| Milestone | Status | Commit | What was built |
|---|---|---|---|
| Spikes S1–S3 | ✅ | 0984ba69 | §2.4 |
| I0 apps | ✅ | 262a7496 | `apps/SparkId` (5011/5012) with a ClientApp. HR, Fleet and QnA are relying parties through the `SparkId` scheme. Fleet only validates (`Spark:JwtBearer:Authority`). `SparkIdTestHost`, and `JwtBearerCredentialTests` on the two-host `SparkIdFleetE2ECollection`. CI and tools know the sixth app. |
| I1 data model | ✅ | db166290 | D1 as amended below. The library layer (alias `identity-provider`), and the migration `M_202610090900_OidcDataModel` |
| I2 developers | ✅ | db166290 | Developer status field. `OidcDeveloperMembership` provides the slot group. `GET/POST /spark/identity-provider/developer`. The request queue with `ApproveDeveloper`/`RejectDeveloper`. Mails `Mail/SparkIdentityProvider/*.mjml` (en/fr/nl). `OidcAudit` |
| I3 teams, modes | ✅ | db166290 | The authorize gate (`OidcApplicationAccess`). Invitations (`OidcInvitations`, `POST /spark/identity-provider/invitations/accept`). The team sub-query and `InviteMember`/`ResendInvitation`/`RemoveMember`. `SwitchToLive`/`SwitchToDevelopment`. The scope-approval and go-live queues |
| I4 registration UX | ✅ | 272b21bc | New generic client operation **`showSecret`** (server side only). `GenerateSecret`/`RevokeSecret`, the secrets sub-query, the redirect URI rules, the URI limit. Lookups (`LookupReferences/OidcLookups.cs`) |
| I5 consent | ✅ | 30c2dcf0 | Remember and expiry, `include_granted_scopes`, the consent page contents. `OidcGrantWithdrawal` (whole or per scope). SPA API `GET /spark/identity-provider/applications` and `POST .../applications/withdraw` |
| I6 server pages | ✅ | 8c27d926 | `ConnectText` (ui_locales, then the `spark-lang` cookie, then Accept-Language). Branding. `/connect/error`. `POST /connect/applications/revoke-scope`. About 100 `identityProvider.connect.*` keys |
| I7 SPA | ✅ | (I7 commit) | `showSecret` handler (`SparkSecretDialogService`, mounts itself on `document.body`); `spark-lang` cookie in `SparkLanguageService`; `@mintplayer/ng-spark/identity-provider` (+ `/core`, `/connected-applications`, `/developers`, `/management`) with `withIdentityProvider(withConnectedApplications(), withDeveloperRoutes(), withManagementRoutes())`; routes `account/applications`, `developers`, `developers/invitations/:token`, `identity-provider/admin`; `SPARK_ACCOUNT_OVERVIEW_LINKS`; the D11 bypass toggle on the two-factor page; SparkId wiring (client operations, toast container, sidebar links). Type-checked (ngc), not run in a browser |
| I8 protocol I | ✅ | 8c27d926 | `OidcClientAuthenticator` (basic, post, private_key_jwt, tls/self-signed mTLS, none; O15). `OidcAuthorizeHandler` with `OidcAuthorizeParameters`, GET and POST (prompt, max_age, acr step-up, login_hint, ui_locales, claims, id_token_hint, resource, form_post, `iss`). at+jwt, azp, amr/acr/sid, pairwise (`OidcSubjects`), JWE (`OidcJwe`), signed/encrypted userinfo. Discovery |
| I9 protocol II | ✅ | 8c27d926, 2b8d23e1 | PAR (`/connect/par`), JAR (`OidcRequestObjects`), DPoP and cnf-bound tokens (`OidcProofOfPossession`), the device grant (`/connect/device_authorization`, `/connect/device`), token exchange, gated DCR (`/connect/register[/{client_id}]` plus `POST /spark/identity-provider/developer/registration-token`) |
| I10 keys, sessions | ✅ | 3758cfe4 | `OidcKeyRing` (RSA and EC, Data Protection, rotation by `OidcKeyRotationService`, legacy key import). `sid` in the cookie (OnSigningIn). `OidcSessionStore`: back-channel logout tokens, front-channel iframes, logout revokes the session's refresh tokens |
| I11 operations | ✅ | 495e5a70, (I11+I12 commit) | The audit query (`OidcAuditEventActions`: admins all, app Admins their apps), the grants query with `RevokeGrant`, the menu fragment. `INC:Tokens` on the application per access token (`OidcExpiry.StoreExpiringAsync`). The disable cascade (`OidcDisableCascade`, from both interceptors' `OnAfterSaveAsync`). The named policy `SparkIdentityProviderMachine` on `OidcConnectCorsGroup` and the client-auth failure throttle (`Spark:IdentityProvider:RateLimits`). `GET /spark/identity-provider/admin/keys`, `POST .../admin/keys/rotate` |
| I12 resource servers | ✅ | (I11+I12 commit) | `spark.AddSparkResourceServer(authority, audience, …)` in `MintPlayer.Spark.Authorization.ResourceServer`: at+jwt only, DPoP scheme and `cnf` (jkt, x5t#S256) enforced, or `UseIntrospection` (`SparkIntrospectionHandler`). `[RequireScope]` / `.RequireScope()`. `SparkDpopProof` is shared with the IdP's token endpoint. Introspection now answers `iss`, `cnf`, `group(s)`, `act`. Fleet: `GET /api/fleet/cars` needs `fleet.read`; SparkId seeds the `fleet` API resource and offers `fleet.read` to HR (`M_202610091000_FleetApi`). HR asks for `fleet.read`, saves SparkId's tokens, and `GET /api/hr/fleet-cars` calls Fleet with the user's access token |
| I13 tests, conformance | ✅ tests; conformance see below | 4a46cfad, db80416e, (journey commit) | 16 unit-test classes and D11 tests; the E2E journey `IdentityProviderJourneyTests` (developer request → approval → app → secret once → tester invite → Development refusal → Live → granular consent → token scopes → per-scope withdrawal); route snapshots regenerated; local sweep green. The OpenID conformance suite: see "Conformance" in the open work |
| I14 docs, versions | ✅ | (I7 commit) | Release notes (`release-notes-preview-103.md` §8 and "New"), `Spark.Abstractions` and `Authorization.Abstractions` → preview.103 (the rest already were; ng-spark 22.31.0). The IdP README rewritten; `docs/guide-identity-provider-developers.md`; README index rows |
| #490 D11 external-login 2FA | ✅ | (D11 commit) | `/spark/auth/external-login/two-factor` (GET/POST), `Spark:Auth:ExternalLogin:TwoFactor:{Enabled,AllowUserBypass}`, `SparkUser.BypassTwoFactorForExternalLogin` with `GET/POST /spark/auth/manage/external-login-two-factor` and the capability `externalLoginTwoFactorBypass`. `ConnectPageTheme` and the HTML helpers (`SparkPageHtml`, `ISparkPageBranding`) moved to `MintPlayer.Spark.Authorization.Pages`. The account-page toggle (SPA) is part of I7 |

**Decisions taken during implementation** (each also stated where it applies):
- **Scope-name uniqueness is structural** (natural ids plus prefixes), not a compare-exchange
  reservation. Only client ids use one (D1).
- **An application's `DisplayName` stays a plain string:** it is a product name. Resource and scope
  names are `TranslatedString` (D1).
- **Row filtering uses `GetRowFilterAsync`** on the library's Actions, not `RowFilterPolicy` (D2).
- **Lookups:** member role, mode, resource kind, client type and consent type are
  `TransientLookupReference`s shipped by the library.
- **PAR request_uris are reusable until they expire (90 s), not single-use.** The bounce through
  sign-in redeems the same `request_uri` again, and RFC 9126 says only SHOULD.
- **JAR by reference (non-PAR `request_uri`) is refused** (`request_uri_parameter_supported: false`).
- **`acr_values` steps up once:** a fresh sign-in, which passes 2FA where the account has it. Values
  are `urn:mintplayer:spark:acr:1fa|mfa`. An unmet *essential* acr from `claims` answers
  `unmet_authentication_requirements`.
- **The claims parameter never widens consent:** claims still come only from granted scopes, and
  essential `acr` is honoured.
- **A forced re-authentication** (prompt=login, max_age, step-up) marks its return with
  `spark_reauth=1`, so it is forced only once. Only the client asking could abuse that marker.
- **JWE content encryption** is `A128CBC-HS256`/`A256CBC-HS512` only, validated at registration (S2).
- **Signing keys** are in `OidcKeys`; `SigningKeyPath` is only a one-time import. The E2E hosts
  still write a key file, which the ring imports.
- **Token exchange impersonation** still stamps `act` with the calling client.
- **The client-auth throttle is keyed on (client id, IP address), not the client alone:** keyed on the
  client, anyone who knows a client id could lock the real client out. Issued secrets are generated
  (256 bits), so the throttle's job is mostly to cap PBKDF2 work. It answers the same `invalid_client`.
- **The machine-endpoint policy** sits on top of Spark's global per-IP limiter and acts only when the
  app runs the middleware (`spark.AddRateLimiter()`); the policy is always registered, so its name never
  goes missing. The E2E hosts raise both budgets.
- **Disabling an application** revokes its valid tokens and its grants; re-enabling does not restore
  them. **Disabling a resource or an API scope** revokes the tokens carrying those scopes and keeps
  the grants. Both are set-based patches with parameters, waited on for at most 30 s.
- **The resource server's DPoP replay cache is in memory**, so a replay on another instance of the same
  resource server within the 2-minute proof lifetime is not detected. The IdP's own cache is in RavenDB.
- **D11 keeps the text service in the IdP:** Authorization references only Spark's abstractions, so the
  external-login two-factor page translates through `IManager` (`auth.externalTwoFactor*`) and honours
  the `spark-lang` cookie when it is a culture name. Only the theme and the HTML helpers moved.
- **D11 `requires_two_factor`** now means "the two-factor cookie is gone", as the PRD says; a sign-in
  that needs the code is redirected to the page.
- **I7 texts are server translations** (`identityProvider.spa.*` in the library's `translations.json`,
  read through `SparkAuthTranslationService` like the auth pages), not a client-side table. The account
  overview link label is the one inline `{en, fr, nl}`: the link type carries its own translations.
- **Accepting an invitation takes a click** on the page; opening the link does nothing by itself.
- **Fixed from the I13 unit tests (red before the fix, by design of the test):**
  1. A signed request object sent by value is pushed internally (as PAR) before the sign-in bounce, so
     the bounce carries only `request_uri` and the `_jar` flag (`OidcAuthorizeHandler.BounceableAsync`).
  2. Token exchange: the `actor_token` must be the calling client's own token and not the subject token
     (`TokenGrants.cs`), so it cannot be used to bypass `AllowImpersonation`.
  3. Refresh checks the session record by point-load (`OidcSessionStore.HasEndedAsync`), so a refresh
     token that logout's index read missed is still refused.
  4. Only a rotated (`redeemed`) refresh token counts as reuse; a revoked one is refused without a
     false `RefreshTokenReuse` audit event.
  5. `ApproveDeveloper`/`RejectDeveloper` check for an administrator themselves, as the go-live decision
     does.
- **Fixed from the E2E journey (`IdentityProviderJourneyTests`, I13):**
  1. The developer-requests query's RQL (`id(u)` on an alias) answered 500; it is the unaliased form now.
  2. The library's mail templates were never registered (`AddSparkMailTemplates`), so every portal mail
     was dead-lettered.
  3. AsDetail rows are checked against their own type: `OidcApplicationScope` and `OidcApiScope` get
     `ReadEditNewDelete` for both slots, `ClientClaim` for administrators only (a `group` claim on a
     client's tokens is authority at every resource server), `OidcReviewDecision` `Read` for both.
  4. **The standard identity resources (`openid`, `profile`, `email`) are a library migration in every
     environment** (`M_202610091100_StandardIdentityResources`): without `openid` no sign-in works, and
     they used to exist only where SparkId's Development seed made them. Existing resources are left alone.
- **Known and accepted:** `SparkIntrospectionHandler` fetches discovery through a static
  `ConfigurationManager` with a plain `HttpDocumentRetriever` (not `IHttpClientFactory`); its test seeds
  that field by reflection. `OidcInvitations` finds the invited account through an index, so an
  account registered seconds earlier may get no mail; the answer is "Invitation sent" either way.
- **Fleet's development audience is `fleet`** (the API resource's name). The E2E hosts and
  `JwtBearerCredentialTests` keep `fleet-api` with their own seeded resource.

**Open work, in order:**
1. ~~I11 rest~~ ✅
2. ~~I7 SPA~~ ✅ (the remaining sub-bullets below are done)
   **Was:** (`libs/node_packages/ng-spark`):
   - the `showSecret` client-operation handler (a modal with a copy button);
   - `SparkLanguageService` also writes the `spark-lang` cookie;
   - the entry point `@mintplayer/ng-spark/identity-provider` with `withIdentityProvider`,
     `withConnectedApplications`, `withDeveloperRoutes` and `withManagementRoutes`;
   - the pages: connected applications (the APIs above); `/developers` (status, terms, request,
     registration token); `/developers/invitations/:token?app=` (accept); the management page (key
     rotation, links to the queues);
   - the account-overview card; fix the doc comment on `SparkAuthRoutesFeature`;
   - SparkId's `app.routes.ts` and menu.
3. ~~I12~~ ✅
4. ~~#490 D11~~ ✅ (server side; the account-page toggle is in I7)
5. ~~I13~~ ✅ unit tests, journey, snapshots, sweep. **Conformance (2026-10-09, local Docker, prebuilt
   suite images, SparkId at `https://host.docker.internal:5011`, static clients, scripted browser):**

   | Plan | Result |
   |---|---|
   | `oidcc-basic-certification-test-plan` (discovery, static) | **0 failed**: 21 passed, 5 warnings, 3 REVIEW, 6 skipped |
   | `oidcc-config-certification-test-plan` | passed |
   | `oidcc-formpost-basic-certification-test-plan` (discovery, static) | **0 failed**: 22 passed, 4 warnings, 3 REVIEW, 6 skipped |
   | RP-initiated, front-channel, back-channel logout (static, code) | **not verified**: the suite's headless browser fails on its own post-logout page (`bootstrap.min.js` syntax error in HtmlUnit), so the modules time out. The checks that ran passed (`CheckPostLogoutState`, the post-logout redirect parameters, `login_required` after logout). The back channel would also need SparkId to trust the suite's self-signed certificate |
   | Dynamic OP | **not run**: registration is gated by an initial access token (D8), which the dynamic plans do not send |
   | FAPI 2.0 | **not run**: needs mTLS or DPoP client setups and a TLS front the local setup does not have |

   - **REVIEW** means the suite wants a human to look at a screenshot (prompt=login, max_age=1, the
     redirect-URI error page); the browser scripts update the placeholders.
   - **Skipped:** the `address`/`phone`/all-scopes modules (not offered), refresh token (no
     `offline_access` on the conformance clients), request objects by value without a registered key.
   - **Warnings, accepted:** `email` is also in the id_token for scope `email` (OIDC §5.4 prefers userinfo
     when an access token is issued; RPs read it from either); profile claims the test user does not
     have; `claims` essential `name` without the profile scope (the claims parameter never widens
     consent, see the decisions above).
   - **Fixed because of the suite:** userinfo by POST (header and form body), `max_age` with no
     allowance (it was 60 s), a missing `response_type`/`scope` reported to the client, and the
     data-model migration's InvalidCastException on a freshly seeded database.
   - Reproduce: `docker compose -f docker-compose-prebuilt.yml up -d` in a clone of
     `gitlab.com/openid/conformance-suite`; seed two clients with redirect
     `https://localhost.emobix.co.uk:8443/test/a/sparkid/callback`, `RequirePkce=false`,
     `client_secret_basic`; run `scripts/run-test-plan.py` with `CONFORMANCE_DEV_MODE=1`.
6. ~~I14~~ ✅, including the reconciliation of `findings-identity-provider-audit.md` and
   `idp-e2e-test-matrix.md` (2026-10-09). **Still open there:** `/connect/userinfo` does not check the token's
   audience (N2 residual); a seeder or migration that writes an application directly bypasses the client-id
   reservation (O17); the expired-code message (O15, needs the 256-bit code); login without a dummy hash
   (O27, accepted, rate limit); and the matrix rows marked "not written".

Origin: an owner request made while testing #464/#490 SSO (HR as the IdP, QnA as the RP):

> we now also need pages to sign up as developer, to create/manage oauth apps, a way for an
> application to specify scopes and resources, a way for the end-user to select scopes that are
> allowed for the intermediate to read, ...

Related documents:
- [pwa_external_login_PRD.md](pwa_external_login_PRD.md), D7: the RP preset and federation are done.
- [findings-identity-provider-audit.md](findings-identity-provider-audit.md): its open findings are
  absorbed into §4 below.
- [idp-e2e-test-matrix.md](idp-e2e-test-matrix.md)

Lands in **PR #499** (Q0).

## 1. Goals

| # | Goal |
|---|---|
| P1 | A user can **sign up as a developer** and **create and manage their own OAuth apps**: client id, secrets, redirect URIs, branding, the scopes the app requests. Developers never see each other's apps. |
| P2 | An app (client) declares the **scopes** it needs (required or optional) and the **resources (APIs)** they grant access to. API owners define their API's scopes. |
| P3 | The end user **chooses which optional scopes** an app may use. The token carries only what was granted. The user can review and withdraw access later from the app's own account area. |
| P4 | The protocol surface a third-party developer expects from an OIDC provider: `client_secret_basic`, `prompt`/`max_age`/`login_hint`, key rotation, `at+jwt`, a resource-server helper. |
| P5 | Admins can operate it: menu entries, a grants view, an audit trail, disabling an app with cascade, signing-key rotation. |

## 2. Current state (evidence, 2026-10-08)

Library `L` = `libs/identity_provider/MintPlayer.Spark.IdentityProvider`. It is hand-built, with no
OpenIddict; storage is RavenDB.

### 2.1 What exists

- **Endpoints:**
  - discovery and JWKS
  - `/connect/authorize` (code + PKCE S256 only)
  - `/connect/token`, with three grants: `authorization_code`; `refresh_token` (rotation and reuse
    detection, `Token.cs:402-411`); `client_credentials`
  - `/connect/userinfo`, `/connect/introspect` (RFC 7662, audience-gated), `/connect/revoke`
    (RFC 7009)
  - `/connect/logout` (RP-initiated)
  - the server-rendered pages `/connect/login`, `/connect/two-factor`, `/connect/consent`,
    `/connect/applications` (with `/revoke`)
- **Collections:** `OidcApplications`, `OidcScopes`, `OidcAuthorizations` (the consent grant,
  natural id = hash of subject and app), `OidcAuthorizationRequests` (10 min, `@expires`) and
  `OidcTokens` (code, access and refresh, id = SHA-256 of the value).
- **Indexes:** `OidcApplications_ByClientId`, `OidcAuthorizations_BySubject` and
  `OidcTokens_ByExpiration`.
- **Granular consent already works:**
  - optional scopes can be unticked
  - the POST may only narrow the set, and required scopes are re-added (`Consent.cs:205-227`)
  - the narrowed set ends up on the code and in the tokens
- **The scope model:** `OidcScope` has `Required`, `Emphasize`, `ClaimTypes` and `Audiences`. It
  "unifies IdentityResource + ApiScope + ApiResource" (`OidcScope.cs:5`). The token's `aud` is the
  union of the granted scopes' `Audiences`, falling back to the client id
  (`OidcTokenGenerator.cs:158-183`).
- **Secrets:** a list of PBKDF2 hashes with an expiry (`ClientSecretHasher.cs`).
- **Admin screens:** generic Spark PersistentObject CRUD, opted in via `IOidcApplicationContext`
  (`apps/HR/HR/HRContext.cs:21,29-30`). HR grants these rights to **Administrators only**
  (`security.json:71-81`).
- **Resource servers:** another Spark app validates these tokens with `AddJwtBearerCredential`
  (`libs/authorization/.../SparkJwtBearerExtensions.cs:60-120`).

### 2.2 Gaps

**Developers and apps (P1)**
- G1 **No ownership:** `OidcApplication` has no owner field, no row-level security and no "my
  apps" query. Granting a non-admin group the CRUD rights would expose every client.
- G2 **No developer onboarding:** no developer role, no terms acceptance, no `/developers` page.
- G3 **Secret handling:**
  - The operator types a plaintext secret into `ClientSecret.Hash`, and it is hashed on save
    (`OidcApplicationInterceptors.cs:180-190`).
  - The hash is shown in the grid, the form and the breadcrumb (`ClientSecret.json:46`).
  - `ClientSecretHasher.GenerateSecret` (`:106`) is never called.
  - There is no reveal-once, no rotate action and no last-used date.
- G4 **ClientId is typed by hand.** Its uniqueness is checked by a query, which races (audit O17).
- G5 **Typed fields are free strings:** `ClientType`, `ConsentType`, `AllowedGrantTypes` and
  `AllowedScopes` are plain strings, not lookups or references.
- G6 **No branding:** there is no logo, homepage, privacy-policy, terms or `ApplicationType` field.
  The consent page shows only `DisplayName`.

**Scopes and resources (P2)**
- G7 **No API-resource entity.** Audiences are inlined on global, admin-only scopes. A developer
  cannot register "my API" with its own scopes.
- G8 **No per-app scope declaration:** there is only `AllowedScopes`, with no required/optional
  split per app.
- G9 **Hard-coded claim sources:** only `name`, `preferred_username`, `given_name`/`family_name`,
  `email`, `email_verified` and `role` (`OidcTokenGenerator.cs:210-244`).
- G10 **No RFC 8707 `resource` parameter.** A token for several APIs carries every audience.

**Consent and the end user (P3)**
- G11 **Dead consent fields:** `AllowRememberConsent` and `ConsentLifetimeSeconds` are never read
  (audit O10). Consent is always remembered and never expires (`Authorize.cs:182-191`).
- G12 **A superset request re-prompts for all scopes,** not just the new ones. There is no
  `include_granted_scopes`.
- G13 **The consent page omits key details:** no logo, publisher, redirect host, signed-in account
  or "not you?". Scopes have a single language.
- G14 **Connected apps are hard to reach:** `/connect/applications` is server-rendered, shows raw
  scope names, and is not linked from the SPA account area (`spark-account-overview` PAGES).
- G15 **The demo never shows consent:** the `qna` seed uses `ConsentType=implicit`
  (`M_202610081200_QnARelyingParty.cs`).
- G16 **The `/connect/*` pages are English-only:** no `lang`, no `ui_locales`
  (`ConnectPage.cs:49-51`).

**Protocol (P4)**
- G17 **Only `client_secret_post`** (audit O23). `client_secret_basic` is the OAuth default, and many
  SDKs send nothing else.
- G18 **Ignored request parameters:** `prompt`, `max_age`, `login_hint`, `ui_locales`, `acr_values`
  and `claims`. `prompt=none` cannot return `login_required`/`consent_required`, so silent
  re-authentication is impossible.
- G19 **One signing key with a fixed `kid`** (`OidcSigningKeyService.cs:65`, audit N4). Rotation means
  an outage.
- G20 **Access tokens lack `typ: at+jwt`** (RFC 9068), and the id token lacks `azp` (audit O22).
  `OidcToken.State` stores the nonce (O20).
- G21 **Thin resource-server support:** no `RequireScope` policy helper and no introspection-based
  handler.
- G22 **No `form_post`, no `private_key_jwt`, no back-channel logout.** Logout does not revoke the
  user's tokens.
- G23 **Error and timing differences reveal whether a client or code exists** (audit O15).

**Operations (P5)**
- G24 **Admin screens are unreachable from the menu:** HR's `programUnits.json` does not list them.
- G25 **No admin view of `OidcAuthorization` or `OidcToken`,** and no audit log.
- G26 **"Disable app" does not cascade:** `Enabled` exists, but nothing revokes outstanding tokens.
- G27 **Remaining audit items:** O13 (`Take(1000)`, no `@expires` on tokens) and O17 (no
  compare-exchange uniqueness).

### 2.3 Prior art

The legacy `C:\Repos\MintPlayer` has **no** OAuth server or developer portal on any of its 15 branches
(`git grep`: 0 hits). It only consumed providers. This library is the only prior art.

### 2.4 Spike results (2026-10-08, read-only, before I0)

**S1: a library can ship its whole security layer, and row filtering is C#.**
- **The layer:** `<SparkLibraryAlias>` in the csproj makes the source-generator targets pick up
  `App_Data/{Model/*.json, actions.json, translations.json, security.json, programUnits.json}` and
  compile them into `[assembly: SparkLayer]`
  (`libs/source_generators/MintPlayer.Spark.SourceGenerators/build/MintPlayer.Spark.SourceGenerators.targets:34-54`).
  The Authorization library ships Model JSON, Actions and rights this way
  (`libs/authorization/MintPlayer.Spark.Authorization/App_Data/security.json:11-17`, `Actions/PasskeysActions.cs`).
- **The IdP today** has no `App_Data` and no alias. HR owns `OidcApplication.json`/`OidcScope.json`
  and grants them to Administrators by raw id (`apps/HR/HR/App_Data/security.json:70-81`).
- **What a library security.json may contain:** only `rights` and `reservedTargets`. Its grants may
  name its own Model types or reserved targets, and its groups only `@anonymous`, `@authenticated`
  or its own `alias:slot`. Deny and `$remove` are refused (`SparkSecurityLayers.cs` `CheckLibrary`,
  :359-449). A slot exists because a grant names it.
- **Unbound slots:** an unbound slot refuses startup (:298, `SecurityConfigurationLoader.cs:108-113`).
  `"libraries": {"identity-provider": false}` makes the layer inert.
- **Resolving a slot in code:** `SparkSecurityFiles.ResolveGroup` resolves a slot to one id at
  startup (`ModerationStartupCheck.cs:38-60`).
- **Answer to D2's open question:** a library **cannot** ship a row policy in JSON; row security has
  no JSON form. It **can** register one in C# with `AddSparkRowPolicy<T>()`, which SoftDelete
  already does (`SparkSoftDeleteExtensions.cs:37`; `RowFilterPolicy<T>` at `IRowPolicy.cs:150`).
  **Decision:**
  - The members-only filter is an IdP `RowFilterPolicy<OidcApplication>`.
  - The "administrators see everything" bypass is a right, `ManageAll/IdentityProvider`, on the
    reserved target `IdentityProvider`. It is granted to the slot `identity-provider:administrators`
    and checked with `IAccessControl.IsAllowedAsync`. There is no public "is in group" API
    (`SparkAuthorizeAttribute.cs:163` is private).
- **Membership:** `AddGroupMembershipProvider<T>()` merges providers. It is scoped and cached per
  principal per request (`SparkGroupMembership.cs:60-109`). Moderation's
  `ModerationPrivilegeProvider` returns slot-resolved ids, and the IdP's developer provider does the same.
- **Indexes:** the IdP's three hand-written indexes stay in the library, as Moderation's do
  (`SparkModerationExtensions.cs:79`, `AddIndexesFrom`). The owner rule "indexes only in the
  application" (2026-10-04) is about `[GenerateIndex]` entity indexes being duplicated into libraries.
  Plugin-internal indexes over the plugin's own collections are the existing precedent.

**S2: crypto. Wilson 8.22 covers everything, so no new package is needed.**
- **Today:**
  - The only token package is `Microsoft.IdentityModel.JsonWebTokens` 8.22.0 (IdP csproj:37), used
    through `JsonWebTokenHandler`.
  - **Signing key:** one RSA key with a fixed `kid` "spark-oidc-key-1", stored as **plaintext JSON**
    at `SigningKeyPath` (`OidcSigningKeyService.cs`).
  - **Validation** pins that single key (`AccessTokens.cs:38-47`, `OidcIdTokenHint.cs:29-30`), and
    the JWKS is one hand-written RSA entry (`Jwks.cs:16-36`).
  - **Client authentication** is `client_secret_post` only, copied into three places
    (`Token.cs:148/:393/:589`, verified at :755-773).
- **What Wilson provides:**
  - **Signing algorithms:** ES256/PS256 through `ECDsaSecurityKey` and `SecurityAlgorithms.RsaSsaPssSha256`.
  - **Key export and thumbprints:** `JsonWebKeyConverter` for EC/RSA JWKs, and
    `JsonWebKey.ComputeJwkThumbprint()` for RFC 7638 (DPoP `jkt`).
  - **Token types:** `TokenType = "at+jwt"` for access tokens, and `ValidTypes` for `dpop+jwt`, JAR
    and client assertions.
  - **Encryption (JWE):** through `EncryptingCredentials` (RSA-OAEP, ECDH-ES). **Caveat:** Wilson
    encrypts with `A*CBC-HS*` content encryption only. Discovery therefore advertises
    `A128CBC-HS256`/`A256CBC-HS512` until I8 shows that `A256GCM` encrypts.
- **Decisions:**
  - `OidcKeys` replaces the file. The key material is protected with
    `IDataProtectionProvider.CreateProtector("Spark.IdentityProvider.OidcKeys")`, since Data
    Protection is always on (`SparkDataProtection.cs:26-31`).
  - Losing the Data Protection key ring makes the keys undecryptable, so startup **fails loudly**
    and does not silently regenerate them.
  - Every validation switches to `IssuerSigningKeys`.
  - A single `IClientAuthenticator` replaces the three copies.
- **mTLS:** Fleet already accepts client certificates (`apps/Fleet/Fleet/Program.cs:24-42`), and
  Replication has trusted certificate forwarding (`ModuleCertificateForwarding.cs:9-97`), which the
  IdP reuses. `cnf.x5t#S256 = Base64Url(SHA-256(cert))`.
- **Relying-party gap:** ASP.NET's JwtBearer handler checks neither DPoP nor `cnf`, so I12's
  resource-server helper validates the proof in `OnTokenValidated`. Fleet's
  `AddJwtBearerCredential` sets no `ValidTypes` (`SparkJwtBearerExtensions.cs:60-110`).

**S3: scaffolding SparkId.**
- **Registration:** QnA arrived in squash d661d80b, so the list of touchpoints comes from grepping:
  - `MintPlayer.Spark.slnx:31-35`
  - `package.json:18` (workspaces)
  - `pull-request.yml:99/137/148/173`
  - `tools/verify-ngsw-config.test.mjs:62-66`
  - `tools/verify-coverage-paths{,.test}.mjs`
  - `tools/test-local.mjs:33`
  - `SparkAppTestHost.cs:26`
  - `HiddenAttributesStayProtectedTests.cs:106`
- **Ports:** 5011/5012 are unused.
- **Seeds:** only the `qna` client is seeded (`apps/HR/HR/Migrations/M_202610081200_QnARelyingParty.cs`).
  There are no `hr` or `fleet` client seeds.
- **The scheme name:** QnA's relying-party scheme is named `HR`, which fixes `/signin-HR`
  (`apps/QnA/QnA/appsettings.Development.json:20-31`, `QnATestHost.cs:80-93`,
  `ExternalLoginHandoffBrowserTests.cs:170`). It is renamed `SparkId`.
- **Fleet** self-issues only when `SparkIdentityProvider:Issuer` is set
  (`apps/Fleet/Fleet/Program.cs:91-103`). `JwtBearerCredentialTests` relies on that through
  `FleetTestHost.cs:80`.

## 3. Locked decisions that still apply

These come from the audit, `coverage-handoff-plan.md`, PRD-MultiHostE2E and the #490 PRD.
- **Hand-built:** no OpenIddict. Code + PKCE S256; no implicit, hybrid or `plain` (§5).
- **Protocol pages are server-rendered** (sharpened by grill Q7): everything that can appear in
  the external-login popup or redirect is server-rendered, and everything else is SPA.
- **Natural ids:** a document that drives an authorization decision is addressed by natural id,
  never by an index (audit :184).
- **D13:** applications and resources are Spark PersistentObjects governed by `security.json`, not a
  bespoke API (reconfirmed by grill Q1).
- **D1:** `client_credentials` is *the* machine credential, and there are no personal access tokens.
- **D15a:** the protocol endpoints follow RFC rules, not `security.json`.
- **Logout GET without antiforgery is deliberate** (audit :151).
- **Never splice strings into RQL.** Prefer ASP.NET Identity.
- ~~Withdrawal revokes the whole grant per app~~: **reversed by grill Q5**. A single scope can be
  withdrawn (D6).

**Licence facts that bind the design** (memory `reference_raven_licence_tiers`, measured on
production, which runs Community):
- **Revisions are capped** ("revisions 1000 > licensed 2" was refused). Revisions are never the
  audit trail (Q6).
- **Expired documents are swept every 36 hours at best.** `@expires` only cleans up; validity always
  checks `ExpiresAt` itself.
- CI and the local machine run the Developer licence, so they cannot see either limit.

## 4. Design (grilled 2026-10-08, see §6)

### D1: Data model: RavenDB-native IdentityServer, six collections (Q3 = A, Q6 = A)

| Collection | Duende counterpart | Holds |
|---|---|---|
| `OidcApplications` | Clients | the client settings, embedded secrets, redirect/logout URIs, CORS origins, `Scopes[]` (each required or optional, with a status), `Members[]` (D3), `Mode` (D4), branding, client JWKS, per-app protocol settings (D8). Usage counters are a RavenDB **time series** on the document. |
| `OidcResources` | IdentityResources + ApiResources + ApiScopes | `Kind = Identity \| Api`. An identity resource is itself one scope with its claim types. An API resource's `Name` **is** the audience (natural id `OidcResources/<name>`), with `Owners`, `AllowIntrospection`, `AutoApprove` and its API scopes **embedded** as an array. A scope belongs to exactly one resource. |
| `OidcGrants` | PersistedGrants (consent) | per user × app (natural id), with the granted scopes, `ExpiresAt` and remember settings. Replaces `OidcAuthorizations`. |
| `OidcTokens` | PersistedGrants (codes, tokens), ServerSideSessions | `Kind`: code, access, refresh, pending authorize request, PAR request, device code, session (D8), DPoP `jti`. All carry `@expires`. Replaces `OidcAuthorizationRequests`; the hourly cleanup job and its `Take(1000)` go (O13). |
| `OidcKeys` | Keys | signing and encryption keys with a state (Next/Active/Retired), encrypted with ASP.NET Data Protection |
| `OidcAuditEvents` | none | the audit trail (D9), with `@expires` retention defaulting to 180 days |

- **Uniqueness (O17):**
  - **Client ids:** a compare-exchange reservation `oidc/client-ids/<client id>` naming the
    application document (`OidcClientIdReservation`). A failed save leaves a reservation whose owner
    does not exist, and the next save takes it over.
  - **Scope names:** unique **by construction**, not by a reservation (decided in I1). Resources have
    natural ids (`OidcResources/<name>`). A resource name contains no dot, and an API scope must
    start with `<resource>.`. So `fleet.read` can only live in `OidcResources/fleet`, and an identity
    scope can never be spelled like an API scope.
  - **Lookups:** resolving a scope is two point-loads (`OidcScopeCatalog`), never an index query. A
    scope disabled a moment ago is therefore never read back as enabled.
- **API scope names** carry their resource's prefix (`fleet.read`).
- **Developer status** is a `Developer { Status, TermsVersion, RequestedAt, DecidedAt, DecidedBy }`
  field on the user document. It adds no collection. The provider patches it in and reads it with a
  projection, so `SparkUser` needs no property. RavenDB keeps the field across loads and saves of
  the user (`PreserveDocumentPropertiesNotFoundOnModel`, measured true in client 7.2.6).
- **Display names:** `OidcResource`/`OidcApiScope` `DisplayName`/`Description` are
  `TranslatedString` (D7). An application's `DisplayName` stays a plain string (decided in I1): it
  is a product name, like a brand, and is not translated.
- **Tokens:** every `OidcTokens` document is stored with `@expires` (`StoreExpiringAsync`). The
  hourly `OidcTokenCleanupService`, its index and `TokenCleanupInterval` are removed (O13).
- **Migrations:** `M_202610090900_OidcDataModel`, shipped by the library.
  - It moves `OidcScopes` → `OidcResources`. A scope with audiences becomes a scope of each
    audience's API resource, prefixed when it lacked the prefix, and the applications' lists are
    renamed with it.
  - It moves `OidcAuthorizations` → `OidcGrants` (same hash, new prefix, tokens repointed) and
    `AllowedScopes` → `Scopes[]` (Live mode, secrets get ids).
  - It deletes `OidcAuthorizationRequests` and stamps `@expires` on every token.
  - It runs wherever the library is referenced: today only SparkId, because HR and Fleet no longer
    reference it (I0) and CodeCoverage never did.

### D2: Becoming a developer, and the shipped groups (revised Q2, roles answer)

- A signed-in user requests developer status on `/developers` by accepting the developer terms.
- **Approval:** `Spark:IdentityProvider:Developers:RequireApproval` (default `true`). An
  Administrator approves or rejects the request with an action, and the requester gets an email.
  When the setting is `false`, the user becomes a developer at once.
- **Terms:** `Developers:TermsVersion`. Raising it makes developers accept the terms again before
  their next portal action.
- **Shipped as group slots, the Moderation precedent** (`docs/spark_composition_PRD.md` D4,
  `libs/moderation/.../App_Data/security.json`):
  - The IdentityProvider's own `App_Data/security.json` grants rights to the slots
    `identity-provider:administrators` and `identity-provider:developers`.
  - The app binds each slot in its `bindings` (HR: Administrators, and a new Developers group). An
    unbound slot refuses startup. `"libraries": { "identity-provider": false }` opts out.
- **Membership needs no seeding:** a merged group-membership provider (the #460 reputation
  mechanism, `SparkBuilderGroupMembershipExtensions`) puts a user in the bound developers group
  exactly while `Developer.Status == Approved`.
- **Row filtering (resolved by spike S1, §2.4):** a library can't ship a row policy in JSON, but it
  can register one in C#.
  - **As built in I1:** the filter lives in the library's own Actions classes
    (`OidcApplicationActions.GetRowFilterAsync`, `OidcResourceActions.GetRowFilterAsync`). That is the
    per-type form of the same rule, which the framework applies to lists, detail, edit, delete and,
    as a WITH CHECK, create. A separate `RowFilterPolicy` would add nothing.
  - **Who passes:** an active Admin or Developer member sees an application. An API is changed only
    by its owners, and identity resources only by administrators.
  - **Administrators** bypass both filters through the right `ManageAll/IdentityProvider`, granted to
    `identity-provider:administrators`.
- **The library layer:** the IdP becomes a library layer (`<SparkLibraryAlias>identity-provider`),
  shipping its own Model JSON, Actions, translations, rights and program-unit fragment. Apps bind
  its slots and own none of its files.

### D3: Apps, roles and invitations (Facebook model, Q4 = yes, with testers)

- **Per-app roles** live in `Members[]` on the app document, not in Spark groups:

  | Role | Can |
  |---|---|
  | **Admin** | everything: members, secrets, delete, switch mode, request go-live. The creator starts as Admin. |
  | **Developer** | edit settings, URIs and scopes. No secrets, no members. |
  | **Tester** | no portal access. Can authorize the app while it is in Development mode. |

- **Who can be invited:** Admins and Developers only from existing developers; Testers from any
  account.
- **Invitations:**
  - The app Admin enters an email address, and the answer is **always "Invitation sent"**.
  - The email goes out only if the address belongs to an eligible account. It carries a
    single-use link, valid for 7 days, stored as a hash with `ExpiresAt` on the member entry.
  - Accepting requires being signed in as that account.
  - **No existence oracle** (the #453 rule: missing ≡ no-access). An invite that could not be
    delivered shows **Pending** until it shows **Expired**, exactly like an ignored one. Pending
    entries show the typed address, never a resolved name.
- **The portal list** shows every app the developer is a member of, with their role, the app's
  mode and status, and pending scope approvals. Admins also see members with their status, and
  can resend, revoke or remove.

### D4: Development and Live modes (Q4)

- **Development** is the default for a new app: only its members can authorize it, and nothing
  needs approval.
- **Live:** anyone can authorize it.
  - **Basic scopes are free:** identity scopes, and API scopes on resources that the app's own
    members own.
  - **Cross-owner API scopes start as `Pending`** until a resource owner (or an
    identity-provider administrator) approves them. Until then they are dropped for non-members.
    `AutoApprove` on a resource skips this.
- **Go-live review:** `Spark:IdentityProvider:Apps:RequireReviewToGoLive` (default `false`). When
  set, an administrator must also approve the switch to Live.
- There is no "unverified app" warning on consent: developers are vetted, and Development mode
  limits exposure.

### D5: App registration done properly (G3–G6)

- **ClientId:** generated server-side and read-only.
- **Secrets:**
  - A **Generate secret** action shows the plaintext **once**, in a client-operation modal with a
    copy button. That is a new generic Spark client operation.
  - The hash never leaves the server. **Revoke** works per secret, and secrets can overlap during
    a rotation.
  - `LastUsedAt` is stamped at most once an hour.
- **Typed fields:** lookups and references replace free strings.
- **Branding:** `LogoUrl`, `HomepageUrl`, `PrivacyPolicyUrl`, `TermsOfServiceUrl` and `SupportEmail`.
- **Redirect URIs:** exact match, https except loopback, with a per-app limit.

### D6: Consent, and the user's connected apps (G11–G16, Q5, Q7)

**Consent:**
- **Remember and expiry:** `AllowRememberConsent` and `ConsentLifetimeSeconds` are wired up.
- **Incremental:** the user is asked only for scopes not yet granted. `include_granted_scopes` is
  honoured.
- **Token response:** includes `scope` whenever it differs from the request.
- **The page shows** the logo, "by <publisher>", the redirect host, the signed-in account with "Not
  you?", and the privacy and terms links.
- **Demo:** the `qna` seed becomes explicit, with `email` optional.

**Connected apps** (`account/applications`, SPA, D7):
- logos and display names in the user's language
- the granted scopes, with **per-scope withdrawal** (Q5 reversal) as well as removing the whole app
- first-granted and last-used dates
- The server-rendered `/connect/applications` stays as the fallback for hosts without a SPA.

### D7: Pages: popup-visible = server-rendered, everything else = SPA (Q1, Q7, routing)

**Server-rendered** (all of `/connect/*`):
- `login`, `two-factor`, `consent`, `device` (enter the user code), `logout` (plus front-channel
  iframes), `error`, and the `applications` fallback
- **Localised:** `ui_locales`, then the culture cookie, then `Accept-Language`, using the same
  translations as the SPA. `<html lang>` is set.
- **Branded:** `SparkIdentityProviderOptions.Branding { ProductName, LogoUrl, ExtraCss }`.
- **Scope text:** `OidcResource` and scope `DisplayName`/`Description` become `TranslatedString`.

**SPA** (a new entry point, `@mintplayer/ng-spark/identity-provider`), plugged into the existing
route-feature pattern (`sparkAuthRoutes(...features)`, `auth/routes/src/spark-auth-routes.ts:96`):

```ts
sparkAuthRoutes(
  withLocalLogin(), withRegistration(), withAccount(),
  withIdentityProvider(
    withConnectedApplications(),   // account/applications
    withDeveloperRoutes(),         // developers (request/terms), developers/invitations/:token, developers/apps → owner-filtered query
    withManagementRoutes(),        // identity-provider/admin: developer requests, go-live and scope approvals, audit
  ),
)
```

- **One feature:** `withIdentityProvider` merges its sub-features into one `SparkAuthRoutesFeature`.
  Each page keeps its own `import()`, so a page nobody opts into is never bundled.
- **The account overview:** `withConnectedApplications` adds `connectedApplications` to
  `SPARK_AUTH_ROUTE_PATHS`, and the overview shows the card only when that path is set.
- **Editing:** apps and resources use the generic `po/:type` screens from `sparkRoutes()` (Q1 = A).
- **Doc fix:** `SparkAuthRoutesFeature`'s doc comment ("not constructible by consumers") gets
  corrected to "constructed only by ng-spark's entry points".

### D8: Protocol: as complete as a toolkit should be (Q5 = expanded scope)

| Area | Features |
|---|---|
| Client authentication | `client_secret_post`, **`client_secret_basic`**, **`private_key_jwt`** (client JWKS or `jwks_uri`), **mTLS** `tls_client_auth` / `self_signed_tls_client_auth` (certificate from the connection or a trusted forwarded header) |
| Authorize | `prompt` (none/login/consent/select_account), `max_age`, `login_hint`, `ui_locales`, `acr_values` (step-up), the **`claims`** parameter, **`resource`** (RFC 8707), **PAR** (RFC 9126, a per-app "require PAR" setting), **JAR** (RFC 9101), `response_mode` query/**form_post**, `iss` in the response (RFC 9207) |
| Grants | authorization_code, refresh_token, client_credentials, **device_code** (RFC 8628), **token exchange** (RFC 8693, delegation/impersonation with `act`, allowed per app) |
| Tokens | **`at+jwt`** (RFC 9068), `azp`, a separate nonce field (O20), **`amr`** from the actual sign-in (`pwd`, `otp`, `mfa`, `hwk` for passkeys, `fed` for an external login) and `acr`, **pairwise subjects** (per sector), **DPoP** (RFC 9449, `cnf.jkt`, `jti` replay entries in `OidcTokens`), **certificate-bound tokens** (RFC 8705), **signed and/or encrypted id_token and userinfo** per app |
| Keys | `OidcKeys` with Next/Active/Retired, a multi-key JWKS, automatic rotation (default 90 days) and manual rotation, RS256 plus ES256/PS256 |
| Logout | RP-initiated logout, **back-channel logout** (sessions in `OidcTokens`, a `sid` claim, logout tokens posted to each RP), **front-channel logout** (best-effort, documented). Logout revokes the session's refresh tokens. |
| Registration | **Dynamic client registration** (RFC 7591/7592), gated: an approved developer gets an initial access token, and a registered app is owned by that developer and starts in Development mode |
| Introspection / revocation | as today, plus Basic/JWT/mTLS client authentication and DPoP-aware introspection |
| Hardening | uniform client-authentication failures with constant-time dummy verification (O15); rate limits as in D9 |
| Resource servers | `spark.AddSparkResourceServer(authority, audience)`, `[RequireScope]` / `.RequireScope(...)`, an introspection-based handler for reference-style validation, DPoP and mTLS proof validation |

Everything is advertised in discovery.

### D9: Operations

- **Audit** (`OidcAuditEvents`, Q6): each event records time, actor, app, subject, kind, details and
  IP. It is written in the same session as the change. The recorded events:
  - app created, changed, mode switched, disabled
  - secret generated or revoked
  - member invited, accepted, removed
  - developer requested, approved, rejected
  - consent granted, narrowed, withdrawn
  - refresh-token reuse detected
  - key rotated
  - dynamic registration

  Per-token issuance goes to time-series counters instead. Admins see a query of all events; app
  Admins see their own app's.
- **Menu:** the library ships a program-unit fragment (Identity provider: Applications, Resources,
  Developer requests, Grants, Audit), and HR opts in.
- **Disabling** an app or resource revokes its grants and tokens in a background sweep. Admins get
  a grants query with a revoke action.
- **Rate limits** on authorize, token, device, PAR, introspection and registration, and on client
  authentication failures (#265 infrastructure).

## 5. Not done (genuinely, for security reasons only, Q5)

| Feature | Why |
|---|---|
| Implicit flow, the password grant (ROPC) | Removed by OAuth 2.1 and the Security BCP (RFC 9700). Shipping them would make the toolkit less safe. |
| Hybrid flow | Used only by obsolete FAPI 1 profiles. |
| `check_session_iframe` session management | Broken by third-party cookie blocking in every major browser. Back-channel logout replaces it. |

## 6. Owner decisions (grilled 2026-10-08)

| # | Decision |
|---|---|
| Q0 | **A**: lands in PR #499 (one-PR rule). |
| Q1 | **A**: developer and admin editing through the generic Spark PersistentObject screens with a members-only filter. Secrets are shown once through a client operation. A few routed SPA pages fill the rest (D7). |
| Q2 | Revised during Q4: developer requests with **configurable admin approval** (`RequireApproval`, default `true`). |
| Q3 | **A**: the RavenDB-native IdentityServer model. "Something like IdentityServer, but since we're using RavenDB we can nicely fit the data into a few (eg 3 to 5) collections." Five collections plus the audit collection from Q6 (D1). |
| Q4 | The Facebook-style model: developer request, app roles Admin/Developer/**Tester**, email invitations without an existence oracle, Development/Live modes, resource-owner approval for cross-owner scopes, optional go-live review (D2–D4). |
| Roles | Shipped as library group slots bound by the app (the Moderation precedent). Membership comes from a merged provider and is never seeded (D2). |
| Q5 | **Expanded scope:** "This isn't a specific application, but a toolkit. So missing features is not a good thing." Everything in D8, plus per-scope withdrawal. The only exclusions are those in §5. |
| Q6 | **A**: `OidcAuditEvents` as a sixth collection. Revisions are capped on the Community licence. |
| Q7 | **A**: "everything that will appear in the external-login popup window = server rendered page; everything else = SPA" (D7). |
| R1 | **B: a new host app, `apps/SparkId`** (https 5011, http 5012). HR, Fleet and QnA all become its relying parties. Fleet stops self-issuing and only validates (`Spark:JwtBearer:Authority` → SparkId). HR drops `AddIdentityProvider` and `IOidcApplicationContext`, and its OIDC model files, migrations and rights move to SparkId. **DemoApp stays** (first removed, then reversed by the owner after the impact inventory). It is the only app running Spark without the Authorization package (`SPARK030`, plain `AddSpark`), the guides' running example, and the only place showing StartPage, query-card slots and XML-doc descriptions. Its role is written down as "the minimal Spark app: core only, no Authorization, no identity provider". SparkId **has a ClientApp** (Q7: the developer portal, connected apps and admin are SPA), which deviates from PRD-MultiHostE2E §3a's "no ClientApp". |
| R2 | **A**: the conformance suite runs locally only, via `npm run conformance` (Docker, against SparkId). Results are recorded in this PRD; there is no CI workflow. |
| R3 | **A**: idempotent migrations, tested against exported documents. **Measured:** CodeCoverage (production) neither hosts nor references the IdP (GitHub OAuth, passkeys, GitHub Actions OIDC, `covt_` tokens), and every OIDC seed is either development-only in HR or written by a test. **So no production data is migrated.** |
| R4 | Push once everything is roughly implemented, then fix the build and test failures from CI. |
| 2FA | A second factor at **both** the IdP and the application, each switchable. Specified in the #490 PRD, D11. |

## 7. Milestones (one PR, tests written per milestone and run once at the end)

- **I1 Data model** (D1). The six collections, the migrations, compare-exchange uniqueness, the
  `Developer` field, the `Members[]`/`Mode`/`Scopes[]`/branding/JWKS fields. The library layer
  (S1): Model JSON moves from HR into the IdP. Regenerate the model JSON and modelHashes for SparkId, HR, Fleet and
  CodeCoverage.
- **I2 Developers and groups** (D2). The library security layer with slots, HR's bindings, the
  merged membership provider, request/approve/reject actions and emails, terms versioning.
- **I3 Apps, roles, invitations, modes** (D3, D4). The members-only filter, role checks, the
  invitation flow and emails, the Development-mode gate at authorize, scope approvals, go-live
  review.
- **I4 Registration UX** (D5). Generated ClientId, the Generate/Revoke secret actions with the
  generic "show once" client operation, typed fields, branding.
- **I5 Consent and connected apps** (D6). Remember and expiry, incremental consent,
  `include_granted_scopes`, `scope` in the token response, the consent page contents, per-scope
  withdrawal, the `qna` seed.
- **I6 Server pages** (D7). Localisation and branding for `/connect/*`, `/connect/error`,
  `/connect/device`, the front-channel logout page, translations in en/fr/nl.
- **I7 SPA pages** (D7). The `@mintplayer/ng-spark/identity-provider` entry point,
  `withIdentityProvider`/`withConnectedApplications`/`withDeveloperRoutes`/`withManagementRoutes`,
  the account-overview card, HR routes and menu.
- **I8 Protocol I.** Client authentication (basic, `private_key_jwt`, mTLS), `prompt`/`max_age`/
  `login_hint`/`acr_values`/`claims`/`resource`, `form_post`, `iss`, `at+jwt`, `azp`, `amr`/`acr`,
  pairwise subjects, signed/encrypted id_token and userinfo, uniform failures.
- **I9 Protocol II.** PAR, JAR, the device grant, token exchange, DPoP, certificate-bound tokens,
  dynamic client registration.
- **I10 Keys and sessions.** `OidcKeys` rotation and a multi-key JWKS. Sessions with `sid`,
  back-channel and front-channel logout, logout revoking refresh tokens.
- **I11 Operations** (D9). The audit collection and its queries, time-series usage, the disable
  cascade, the grants query, rate limits, the program-unit fragment.
- **I12 Resource servers.** `AddSparkResourceServer`, `RequireScope`, the introspection handler,
  DPoP/mTLS validation, and a demo: a Fleet API protected by `fleet.read` from HR.
- **I13 Tests and conformance.**
  - unit tests per milestone, and the open [idp-e2e-test-matrix](idp-e2e-test-matrix.md) rows
  - an E2E journey: developer request → approval → create app → secret shown once → invite a
    tester → Development-mode sign-in → go Live → granular consent → token carries only the granted
    scopes → per-scope withdrawal
  - the **OpenID Foundation conformance suite** in Docker: Basic, Config, Dynamic and Form Post
    OP; RP-Initiated, Back-Channel and Front-Channel Logout; FAPI 2.0 Security Profile
  - then one local sweep
- **I0 Apps (R1), before I1.**
  - **Create `apps/SparkId`:**
    - modelled on HR's Program.cs minus the business code, with a ClientApp, ngsw-config and
      manifest
    - nx `project.json` files, a slnx entry and the npm workspace
    - a `SparkIdTestHost` (E2E)
    - registration in `E2E_APPS`, `verify-coverage-paths`, and `pull-request.yml` (build list and
      the model/security verify loops)
  - **Move the OIDC seeds into SparkId migrations,** for the `qna`, `hr` and `fleet` clients and the
    machine client. QnA, HR and Fleet get `Spark:Auth:Providers:OpenIdConnect:SparkId`.
  - **Fleet:** retire self-issuing, and move `JwtBearerCredentialTests` to a two-host fixture
    (SparkId issues, Fleet validates; multi-host plan R3). Update the route snapshot tests.
  - **App lists:** `verify-ngsw-config.test.mjs` ("finds the five Spark apps") and
    `HiddenAttributesStayProtectedTests.cs:106` (≥ 5) now count six apps, SparkId included.
  - **DemoApp's role** goes into README.md and CLAUDE.md: "the minimal Spark app: core only, no
    Authorization, no identity provider". New features don't land there.
- **I14 Docs and versions.** The IdP README and a developer-portal guide, release notes,
  closing items in the audit doc and the matrix, the ng-spark minor bump and the NuGet preview
  bump (majors unchanged).

## 8. Acceptance

1. A user requests developer status, an administrator approves it, and the user gets the email and
   the portal.
2. The developer creates an app, sees the secret exactly once, and invites a developer (Developer
   role) and a non-developer (Tester). Inviting an unknown address looks identical to a successful
   invite.
3. In Development mode, the tester can sign in and an outsider is refused.
4. After going Live, a cross-owner `fleet.read` stays pending until Fleet's resource owner approves
   it.
5. The user unticks `email`: the token, the token response's `scope` and userinfo all lack it. A
   later request adding a scope asks only for that scope.
6. On `account/applications` the user withdraws a single scope, and the next refresh token lacks it.
7. A key rotation keeps old tokens valid until they expire. Back-channel logout ends the QnA
   session.
8. The device grant, PAR, DPoP, `private_key_jwt` and dynamic registration each work once, end to
   end.
9. The conformance profiles in I13 pass. Any profile that can't run (mTLS without a TLS proxy) is
   reported as **not verified**, never as passed.
10. A Fleet endpoint with `RequireScope("fleet.read")` accepts a token that has the scope and
    rejects one that doesn't.
