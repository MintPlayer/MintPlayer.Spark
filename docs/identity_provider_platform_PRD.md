# PRD / Plan: Spark IdentityProvider as a full identity-provider plugin

Status: **investigation done, owner decisions open** (2026-10-08). Branch:
`feat/464-490-pwa-external-login` (PR #499).

Origin: an owner request made while testing #464/#490 SSO (HR as the IdP, QnA as the RP):

> we now also need pages to sign up as developer, to create/manage oauth apps, a way for an
> application to specify scopes and resources, a way for the end-user to select scopes that are
> allowed for the intermediate to read, ...

Related documents:
- [pwa_external_login_PRD.md](pwa_external_login_PRD.md), D7: the RP preset and federation are done.
- [findings-identity-provider-audit.md](findings-identity-provider-audit.md): its open findings are
  absorbed into §4 below.
- [idp-e2e-test-matrix.md](idp-e2e-test-matrix.md)

Per the one-PR rule this lands in **the same PR** unless the owner decides otherwise (Q0).

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

## 3. Locked decisions that still apply

These come from the audit, `coverage-handoff-plan.md`, PRD-MultiHostE2E and the #490 PRD.
- **Hand-built:** no OpenIddict. Code + PKCE S256 only; no implicit, hybrid or `plain`.
- **The `/connect/*` protocol pages stay server-rendered.** The IdP host needs no ClientApp.
- **Natural ids:** a document that drives an authorization decision is addressed by natural id,
  never by an index (audit :184).
- **D13:** applications and scopes are Spark PersistentObjects governed by `security.json`, not a
  bespoke API.
- **D1:** `client_credentials` is *the* machine credential, and there are no personal access tokens.
- **D15a:** the protocol endpoints follow RFC rules, not `security.json`.
- **Withdrawal** revokes the whole grant per app. The access-token lifetime window is accepted and
  stated in the UI.
- **Logout GET without antiforgery is deliberate** (audit :151).
- **Never splice strings into RQL.**
- **Prefer ASP.NET Identity** over third-party packages.

## 4. Proposed design (subject to §6)

### D1: Ownership, and developers as a group
- `OidcApplication` gains `Owners: string[]` (SparkUser ids) and `CreatedBy`. A row-level-security
  filter shows a non-admin only the apps they own; Administrators see everything. This reuses the
  existing row-security mechanism (#236), not a parallel one.
- A **Developers** well-known group is granted `QueryReadEditNewDelete/OidcApplication`, with the
  row filter applied. Becoming a developer is configured by
  `Spark:IdentityProvider:Developers:Registration = Disabled | Open | Approval`. `Open` joins the
  group when the user accepts the developer terms; `Approval` puts the user in a queue that an
  admin approves.
- A `DeveloperProfile` document holds terms version and acceptance time and, for `Approval`, the
  request status. Its id is `DeveloperProfiles/<userId>`.

### D2: App registration done properly
- **ClientId:** generated server-side (random base64url, 24 characters) on New, and read-only
  afterwards. Uniqueness comes from the generator; a hand-typed id is no longer accepted.
- **Secrets:**
  - A custom action, **Generate secret**, calls `ClientSecretHasher.GenerateSecret`, stores the hash
    with a description and an expiry (default 1 year), and shows the plaintext **once**, through a
    client operation (a modal with a copy button).
  - The hash is never sent to the client: the attribute is removed from every view.
  - **Revoke secret** works per row. Several active secrets may overlap during a rotation.
  - The token endpoint stamps `LastUsedAt`, at most once an hour per secret.
- **Typed fields:** `ClientType`, `ConsentType`, `ApplicationType` (web/spa/native) and
  `AllowedGrantTypes` become lookups. Scopes become references to `OidcScope` (D4).
- **Branding:** `LogoUrl` (https), `HomepageUrl`, `PrivacyPolicyUrl`, `TermsOfServiceUrl` and
  `SupportEmail`, all shown on consent.
- **Redirect URIs:** exact match, https only except for loopback, which already holds. A per-app
  limit is configurable.

### D3: API resources
- A new `OidcApiResource` collection has:
  - `Name`, which **is** the audience and the natural id (`OidcApiResources/<name>`)
  - `DisplayName`, `Description`, `Owners` and `Enabled`
  - `AllowIntrospection`, which replaces `MayIntrospectAnyAudience` for this resource's tokens
- `OidcScope` gains `Kind = Identity | Api`, and an `Api` scope gains `Resource` (a reference). The
  inline `Audiences` field goes; no backward compatibility is needed while the package is in
  preview, and a migration moves the data.
- **Who defines what:**
  - Developers who own a resource define its API scopes. A scope name must be prefixed by the
    resource name (`fleet.read`), which keeps scope names globally unique without coordination.
  - Identity scopes (`openid`, `profile`, `email`, `offline_access`, …) stay admin-only.
- A scope marked `Sensitive` (today `Emphasize`) needs admin approval before an app may request it.
  `OidcApplicationScope.Status = Pending | Approved`, and an unapproved scope is dropped from the
  request.
- **RFC 8707:** `resource=` on authorize and token narrows `aud` to the named resources, which must
  be covered by the granted scopes. Without it, the behaviour stays as today: the union of the
  granted scopes' resources.

### D4: Per-app scope declaration
- `AllowedScopes: string[]` becomes `Scopes: OidcApplicationScope[]` (an AsDetail):
  `{ Scope (reference), Required: bool, Status }`.
  - A **required** scope is always requested and can't be unticked.
  - An **optional** scope is requested only when the authorize request asks for it, and the user may
    decline it.
  - The global `OidcScope.Required` remains only for `openid`.

### D5: Consent done properly
- **Remember and expiry:**
  - `AllowRememberConsent` (a checkbox on the page, checked by default) and
    `ConsentLifetimeSeconds` (stored as `ExpiresAt` on `OidcAuthorization`) are wired up.
  - Unchecked means the grant applies only to this request, so the next authorize prompts again.
- **Incremental:** a request for a superset prompts **only for the scopes not yet granted** and shows
  the rest as "already allowed". `include_granted_scopes=true` merges the previous grant into the
  new token.
- **The token response returns `scope`** whenever the granted set differs from the requested one
  (RFC 6749 §5.1). The README tells RP developers to handle partial grants.
- **The page shows:** the logo, the app name with "by <owner/publisher>", the redirect host, the
  signed-in account with a "Not you?" link (sign out, then back to authorize), and the
  privacy/terms links. An unverified app gets a warning line (if Q4 is answered "yes").
- **The demo shows consent:** the `qna` seed becomes `explicit`, with `email` optional.

### D6: The end user's connected apps
- A new Angular page, `account/applications`, in `@mintplayer/ng-spark/auth/account`, listed in
  the overview's PAGES when the host has the IdP. It shows:
  - each app's logo and name
  - the granted scopes (display names in the user's language)
  - granted-on and last-used dates
  - a **Remove access** button, which keeps today's all-or-nothing semantics (locked decision §3)
- It is served by JSON endpoints under `/spark/auth/connected-applications`. The server-rendered
  `/connect/applications` stays for hosts without the SPA.

### D7: Localisation and branding of `/connect/*`
- The server-rendered pages read the request culture (`ui_locales` first, then the
  `Accept-Language` / culture cookie) and use the same translations source as the SPA.
  `<html lang>` is set.
- **New keys:** `idp.login.*`, `idp.consent.*`, `idp.apps.*`, `idp.logout.*`, `idp.error.*`.
- `OidcScope.DisplayName` and `Description` become `TranslatedString`.
- **Host branding:** `SparkIdentityProviderOptions.Branding { ProductName, LogoUrl, ExtraCss }`.
- A branded **`/connect/error`** page replaces the plain-text 400s.

### D8: Protocol completions
- **`client_secret_basic`** on token, introspect and revoke, advertised in discovery (G17).
- **`prompt`:**
  - `none` → `login_required` / `consent_required` / `interaction_required`
  - `login` → re-authenticate
  - `consent` → force the consent page
  - `select_account` → treated as `login`
- **Other request parameters:** `max_age` (compare with `auth_time`), and `login_hint`
  (pre-fills the identifier).
- **Key rotation (N4):**
  - **Storage:** a `OidcSigningKeys` collection holds the keys, encrypted with ASP.NET Data
    Protection. The dev file remains as a bootstrap.
  - **States:** each key is `Next`, `Active` or `Retired`.
  - **JWKS:** publishes `Next`, `Active` and `Retired` until the longest token lifetime has passed.
  - **Rotation:** automatic every N days (default 90), plus an admin "Rotate now" action.
- **Token claims:** `typ: at+jwt` on access tokens, `azp` on the id token (O22), and the nonce moved
  to its own field (O20).
- **Uniform client authentication failures (O15):** one error text, and constant-time verification
  against a dummy hash for unknown clients.
- **Resource servers:**
  - `spark.AddSparkResourceServer(authority, audience)`, a thin wrapper over
    `AddJwtBearerCredential`.
  - a `[RequireScope("fleet.read")]` policy, with an `IEndpointConventionBuilder.RequireScope(...)`
    form.
- **Logout:**
  - Logout revokes the user's refresh tokens for this session's clients.
  - `form_post` response mode.

### D9: Operations
- **Menu:** HR's `programUnits.json` gets an **Identity provider** group with Applications, API
  resources, Scopes, Developers (approval queue) and Grants. The library ships a program-unit
  fragment that apps opt into.
- **Grants:** a read-only `OidcAuthorization` query for admins (by user or app), with a "Revoke"
  action.
- **Disabling** an app or resource revokes outstanding grants and tokens in a background sweep. The
  token endpoint already rejects a disabled client.
- **Audit log:** an `OidcAuditEvents` collection with an `@expires` retention (default 180 days)
  records:
  - app created, changed or disabled
  - secret generated or revoked
  - consent granted or withdrawn
  - refresh reuse detected
  - key rotated

  Token issuance is **not** logged per token; it counts as usage stats only (Q6).
- **Clean-up:** `@expires` on `OidcToken` (O13), and compare-exchange reservations for `ClientId`
  and scope `Name` (O17).

## 5. Not done (genuinely)

These depend on Q5. The proposal is to leave them out:

| Feature | Why |
|---|---|
| Device authorization grant (RFC 8628) | No target client today (TVs, CLIs). |
| Token exchange (RFC 8693) | No target client today. |
| PAR (RFC 9126), JAR, DPoP, mTLS | Not needed by the target clients. |
| Front- and back-channel logout | Front-channel is broken by third-party cookie blocking. Back-channel needs a server-side session store that the library doesn't have. |
| Dynamic client registration (RFC 7591/7592) | D1/D2 give self-service through the portal. Open DCR on a self-hosted IdP is an abuse vector. |
| Pairwise subjects, `acr`/`amr`, the `claims` request parameter | Not needed by the target clients. |
| Per-scope withdrawal on the connected-apps page | Locked decision §3. |

## 6. Owner decisions needed (to be grilled)

| # | Question | Recommendation |
|---|---|---|
| Q0 | Does this land in PR #499 or in its own PR? | **#499** (one-PR rule). Trap: #499 is reviewable today and this is about as large again. |
| Q1 | Where do the developer pages live: (A) the generic Spark PO screens with row security and custom actions, plus one small `/developers` landing/terms page; (B) dedicated Angular pages in a new `@mintplayer/ng-spark/identity-provider` entry point; (C) server-rendered `/connect/developer/*`? | **A**: it follows D13 with the least new UI, and "show the secret once" is a client operation. Trap: the generic screens feel like an admin tool rather than a developer portal. |
| Q2 | How does a user become a developer (D1): Open, Approval or Disabled, and which default? | **Configurable, default `Disabled`.** HR runs `Open` for the demo. Trap: a forgotten `Open` on a production host lets anyone register clients. |
| Q3 | API resources (D3): (A) a first-class `OidcApiResource`, with developers owning their APIs' scopes; (B) keep `Audiences` on admin-defined scopes? | **A**: P2 explicitly asks for resources. Trap: a migration of existing scope data, and one more screen. |
| Q4 | Sensitive scopes: does an app need admin approval before requesting them, and does the consent page show an "unverified app" warning? | **Approval: yes; warning: no.** One admin is the verifier on a self-hosted IdP. |
| Q5 | Do the §5 items stay out? | **Yes.** Trap: a third-party SDK that requires PAR or the device flow can't integrate. |
| Q6 | Audit log scope (D9): the events listed, without per-token issuance? | **Yes**, with 180-day retention. |
| Q7 | Does the connected-apps page move into the SPA (D6), or is a link to `/connect/applications` enough? | **The SPA page**, so it fits the account area and is localised. |

## 7. Milestones (tests batched at the end)

- **I1** Model: ownership, generated ClientId, typed lookups, branding fields, `OidcApiResource`,
  `Kind`/`Resource` on scopes, the `OidcApplicationScope` AsDetail. Migrations for existing data
  (scope `Audiences` → resources, `AllowedScopes` → `Scopes`). Compare-exchange uniqueness.
  Regenerate HR's model and modelHashes.
- **I2** Developers: the `Developers` group, row-security filter, `DeveloperProfile`, registration
  modes, the `/developers` page (terms and request), the admin approval action, security.json
  rights.
- **I3** Secrets: Generate (show once) and Revoke actions, the hash hidden everywhere, `LastUsedAt`,
  `client_secret_basic`, uniform client-auth failures.
- **I4** Consent: per-app required/optional scopes, remember and expiry, incremental prompt,
  `include_granted_scopes`, the `scope` in the token response, the page contents (logo, publisher,
  redirect host, account, links), sensitive-scope approval, the `qna` seed made explicit.
- **I5** Protocol: `prompt`, `max_age`, `login_hint`, `resource=` (RFC 8707), `at+jwt`, `azp`, the
  nonce field, `form_post`, logout revoking refresh tokens.
- **I6** Keys: `OidcSigningKeys` with Next/Active/Retired, Data Protection encryption, automatic and
  manual rotation, a multi-key JWKS.
- **I7** End-user UI: the `account/applications` page and its endpoints; localisation and branding of
  `/connect/*` (D7) and `/connect/error`; translations in en/fr/nl.
- **I8** Operations: the program-unit fragment and HR's menu, the grants query and revoke, the
  disable cascade, `OidcAuditEvents`, `@expires` on tokens.
- **I9** Resource server: `AddSparkResourceServer`, `RequireScope`. A demo resource is registered by
  QnA or Fleet, validates an HR-issued token, and is denied without the scope.
- **I10** Tests: unit tests per milestone, written but not run; the open
  [idp-e2e-test-matrix](idp-e2e-test-matrix.md) rows this touches (A-C5 consent, A-S*, R-J3–J6
  rotation, T-O1–O4 uniform errors); and an E2E test of developer → create app → secret once →
  QnA-style RP → granular consent → token carries only the granted scopes → withdraw. Then one
  sweep.
- **I11** Docs and versions: the IdP README, release notes (preview.103 if it is still unreleased,
  otherwise the next one), closing open items in the audit doc, the matrix, and the ng-spark minor
  bump.

## 8. Acceptance

1. A non-admin HR user becomes a developer and creates an app. They see the secret exactly once
   and see only their own apps.
2. That app requests `openid profile email fleet.read` with `email` optional. The user unticks
   `email`: the token has no email claim, the token response's `scope` omits it, and userinfo
   omits it.
3. A second authorize that adds a new scope prompts only for that scope.
4. The account → Applications page lists the app in the user's language. Remove access ends it:
   refresh fails, and an access token stops working once it expires.
5. A rotation keeps tokens issued under the old key valid until they expire.
6. `client_secret_basic` works. `prompt=none` without a session returns `login_required`.
7. A Fleet endpoint with `RequireScope("fleet.read")` accepts the token and rejects one without that
   scope.
