# PRD / Plan: Spark IdentityProvider toward Duende IdentityServer parity

Follows `docs/identity_provider_platform_PRD.md`, which shipped in PR #499 and is finished (I0–I14
✅). This document holds **everything still to do in the identity-provider area**. Per the one-PR
rule, it lands as **one dedicated PR** (`feat/idp-parity`). Spikes come first where the approach is
unclear.

## 0. Status (resume here)

| Step | State |
|---|---|
| Investigation of the current state (2026-10-09, §1) | ✅ |
| Spikes S1–S8 (§4) | ⏳ not started |
| Owner grilling of the open decisions (§6) | ✅ 2026-10-09, G1–G5 locked |
| Milestones P1–P14 (§5) | ⏳ |

## 1. Findings (2026-10-09, evidence-backed)

### 1.1 What is already implemented

The library is `libs/identity_provider/MintPlayer.Spark.IdentityProvider` (104 .cs files), hosted by
`apps/SparkId` (`Program.cs:31`). HR, Fleet and QnA use it as relying parties or resource servers.
Paths below are relative to the library.

- **Endpoints:**
  - Discovery (`Endpoints/Discovery.cs:50-96`) and JWKS (multi-key, `Endpoints/Jwks.cs`).
  - Authorize, GET and POST (`Services/OidcAuthorizeHandler.cs`), and Token (`Endpoints/Token.cs:107-116`).
  - UserInfo, signed or encrypted.
  - End session, plus front-channel and back-channel logout (`Services/OidcSessionStore.cs`).
  - Introspection (RFC 7662) and revocation (RFC 7009).
  - Device authorization (RFC 8628).
  - PAR (RFC 9126), which a client can be required to use (`Models/OidcApplication.cs:138`).
  - JAR by value (RFC 9101, `Services/OidcRequestObjects.cs`).
  - Dynamic client registration (RFC 7591/7592), gated by an initial access token (`Endpoints/Register.cs`).
- **Grants:** authorization_code with PKCE (S256 only), client_credentials, refresh_token with rotation and
  reuse detection (`Token.cs:415-431`), device_code, and token exchange (RFC 8693, with `act`,
  delegation and impersonation, `Endpoints/TokenGrants.cs:145-186`).
- **Client authentication:** client_secret_basic, client_secret_post, private_key_jwt, tls_client_auth,
  self_signed_tls_client_auth and none (public clients) (`Services/OidcClientAuthenticator.cs:41-48`).
  Secrets are hashed with PBKDF2, and failed attempts are throttled per (client, IP).
- **Tokens:**
  - Access tokens are `at+jwt` and every `jti` is recorded server-side, so they can be revoked
    (`Services/AccessTokens.cs`).
  - DPoP and mTLS token binding (`Services/OidcProofOfPossession.cs`).
  - Resource indicators (RFC 8707).
  - A resource / API-scope / identity-resource model (`Models/OidcResource.cs`).
  - JWE for id_token and userinfo (`Services/OidcJwe.cs`) and pairwise subjects (`Services/OidcSubjects.cs`).
  - A key ring with RS256, PS256 and ES256, automatic rotation every 90 days, and manual rotation
    (`Services/OidcKeyRing.cs`, `OidcKeyRotationService`).
- **User-facing:**
  - Server-rendered login, 2FA, consent and device pages.
  - Remembered consent with expiry, and `include_granted_scopes`.
  - A connected-applications page with withdrawal of a whole grant or a single scope.
  - External-IdP buttons.
  - `acr_values` step-up; `amr` maps passkeys to `hwk`.
- **Operations:**
  - Seven RavenDB collections, cleaned up through `@expires` (`Services/OidcExpiry.cs`).
  - An audit-event collection with a query, plus a RevokeGrant action.
  - Rate limiting on `/connect`.
  - A developer portal (teams, invitations, Development/Live modes) and admin screens.
- **Resource server:** `AddSparkResourceServer` and `[RequireScope]`
  (`libs/authorization/.../ResourceServer/`).
- **Tests:** about 45 classes (Spark.Tests Oidc*, plus the E2E classes `IdentityProviderJourneyTests`
  and `JwtBearerCredentialTests`).
- **OpenID conformance:**
  - Passed: the basic, config and form-post plans, with 0 failures.
  - Logout plans: not verified.
  - Dynamic OP and FAPI 2.0 plans: not run.

### 1.2 PR #54 ("feat: Add OIDC Identity Provider package", open since 2026-03-13): close it as superseded

The PR conflicts with master and uses the old layout (`Demo/`, `MintPlayer.Spark/`,
`node_packages/ng-spark-auth`). Nothing in it is worth salvaging, so no branch is needed. Each piece is
either on master, done again more thoroughly, or obsolete:

| PR #54 piece | Master |
|---|---|
| IdentityProvider package (code+PKCE, consent, discovery, introspection, revocation) | Superseded by the #499 library, which goes far beyond it |
| `OidcTokenCleanupService` | Obsolete: RavenDB `@expires` (`Services/OidcExpiry.cs`) |
| Four collections (Applications/Scopes/Authorizations/Tokens) | Obsolete: the D1 data model (`Migrations/M_202610090900_OidcDataModel.cs`) |
| `Demo/SparkId` | Now `apps/SparkId` |
| Hand-rolled RP client (`AddOidcLogin`, `OidcClientService`, `OidcCallback`) | Obsolete: ASP.NET `AddOpenIdConnect` (`SparkExternalProviderExtensions.cs:231`) |
| External login, link/unlink, external 2FA | On master (`libs/authorization/.../Endpoints/ExternalLogin/`) |
| `SparkUsers_ByLogin` index | Solved differently (`Identity/UserStore.cs:395-403`) |
| EntityMapper simple-type AsDetail arrays | Obsolete: AsDetail was rebuilt; plain `isArray` attributes are used instead |
| Angular OIDC login, buttons, popup, profile | On master as `ng-spark/auth/*` (#464/#490) |
| SpaServices 10.5.0, demo translations, `docs/PRD-IdentityProvider.md` | Obsolete or superseded |

### 1.3 Issue #53 ("Identity provider"): close it as completed by #499

The issue body is one sentence, and it is cut off: *"…a single nuget package, that ships an
IdentityProvider. When a developer wants to allow setup of an OIDC provider"*. All three parts of it
are met:

| Requirement in #53 | Where master meets it |
|---|---|
| A single NuGet package | `MintPlayer.Spark.IdentityProvider`, published up to `11.0.0-preview.104` |
| Ships an identity provider | `AddIdentityProvider`, `Extensions/SparkIdentityProviderExtensions.cs:40` |
| A developer can opt in and set it up | `spark.AddIdentityProvider(...)`, plus `docs/guide-identity-provider-developers.md` |

#499's body said only "Closes #464, closes #490", which is why #53 is still open. The remaining work
in this PRD goes beyond what #53 asked for and should not keep it open.

### 1.4 Was identity-provider work cut to keep a PR small? No record of it

I checked all of the following:

- `docs/code-coverage/dependency-updates-PRD.md`: it has **no** identity-provider content. "OIDC"
  there means GitHub Actions OIDC, and "fleet" means the fleet of repositories.
- The bodies of PRs #499, #501 and #502, and their squash commits.
- Leftover branches: none is ahead of its squash commit.
- Stashes.
- The platform PRD itself. Its Q0 says "one PR", and its Q5 says "the only exclusions are those in §5".

The only items left out were left out **for security** (§7 below). The real open items are the
platform PRD's §0 item 6 and its conformance gaps. All of them are taken over into §3 here.

## 2. Goals

1. Feature coverage comparable to **Duende IdentityServer** (Community + Business + Enterprise
   features), apart from the security exclusions in §7. A feature Spark lacks counts as a defect (Q5,
   carried over).
2. Every feature proven by tests (red → green for security features) and, wherever an OpenID
   certification plan exists, by the **conformance suite**.
3. A production-shaped deployment: several instances behind a reverse proxy (mTLS, DPoP nonces, key
   rotation, sessions all hold across nodes).

## 3. Backlog (gap catalogue)

The "Now" column says what master does today.

### Tier A: open items carried over from #499 (must)

| # | Item | Now | Evidence |
|---|---|---|---|
| A1 | UserInfo must check the token's audience (N2 residual) | `ValidateAudience = false` | `Services/AccessTokens.cs:43` |
| A2 | A seeder or migration that writes an application must go through the client-id reservation (O17) | bypassed | platform PRD §0 item 6 |
| A3 | Expired code and never-issued code get different messages (O15, test row T-O2) | partly: `Token.cs:186` versus `:163/168/178` | needs the 256-bit code |
| A4 | Login uses a dummy password hash, so an unknown user can't be told apart by timing (O27) | accepted risk | `Endpoints/Login.cs:252` |
| A5 | Write the "not written" rows of `docs/idp-e2e-test-matrix.md` (T-O2, L-L5) | open | matrix line 9 |
| A6 | Resource-server DPoP replay cache works across instances | in memory | platform PRD:65 |
| A7 | `SparkIntrospectionHandler` fetches discovery through `IHttpClientFactory`, not a static `ConfigurationManager` | static | platform PRD:97-100 |
| A8 | An invitation to an account registered seconds earlier still sends its mail (index lag) | may send nothing | platform PRD:97-100 |
| A9 | Run the OpenID conformance plans: logout (RP-initiated, front-channel, back-channel), dynamic OP, FAPI 2.0 | not verified / not run | platform PRD §0 item 5 |
| A10 | Fix the stale links to `docs/PRD-IdentityProvider.md` and the false "not yet on master" claim | stale | `docs/prd/PRD-SecurityAudit.md:526`, `docs/prd/PRD-SparkClient-Followups.md:263` |

### Tier B: parity, needed for a credible identity provider (must)

| # | Feature | Now | Gap |
|---|---|---|---|
| B1 | **DPoP nonces** (`DPoP-Nonce`, `use_dpop_nonce`) at the token endpoint and the resource server | absent | needed for FAPI 2.0 DPoP. See S2 |
| B2 | **mTLS behind a reverse proxy**: trusted certificate forwarding, `mtls_endpoint_aliases` | auth works only with direct TLS | see S1 |
| B3 | **Absolute refresh-token lifetime** next to the sliding one (Duende `AbsoluteRefreshTokenLifetime` / `SlidingRefreshTokenLifetime`) | sliding only: every rotation sets `UtcNow + RefreshTokenLifetimeDays` (`Token.cs:568`), so a chain never ends | `ChainStartedAt`; host-wide options plus a per-app override; defaults per G3 |
| B4 | **Server-side sessions**: ticket store, session query, admin "end session", inactivity timeout | session record (`sid` + clients) only | see S5 |
| B5 | **FAPI 2.0 security profile switch** per client (forces PAR, PKCE, sender-constrained tokens, `iss`, short code lifetime, allowed algorithms) | building blocks only | depends on B1 and B2; certify with A9 |
| B6 | **OpenTelemetry for every Spark library** (G4): `Meter` + `ActivitySource` per category | absent everywhere | see S7 and G4 |
| B7 | **Pluggable event sink** (`IIdentityProviderEventSink`) next to the audit collection | audit collection only | Duende `IEventSink` |
| B8 | **Pluggable extension grants** (`IExtensionGrant`, composed via `[Register]`) | closed switch, `Token.cs:115` | see S6 |
| B9 | **JWT-bearer grant (RFC 7523)**, the first extension grant | client-assertion form only | depends on B8 |
| B10 | **Profile / claims service**: configurable claim mapping for id_token, access token and userinfo, per resource, plus user claims from the Spark user | fixed list in `Services/OidcTokenGenerator.cs:254-277` | Duende `IProfileService` |

### Tier C: valuable (should)

| # | Feature | Notes |
|---|---|---|
| C1 | **Opaque reference access tokens**, chosen per application | today every token is a JWT whose `jti` is recorded. Introspection already exists, so this changes only the token format |
| C2 | **Dynamic providers**: external IdPs (OIDC/SAML-less) configured at runtime from RavenDB | today they come from configuration at startup (`SparkExternalProvidersConfigurationExtensions.cs:49`) |
| C3 | **JAR by reference** (`request_uri` to an external URL), with SSRF guards | refused today (`Discovery.cs:79`) |
| C4 | **Conformance mode for dynamic client registration**: open DCR, or a per-environment policy, so the dynamic plans can run | DCR needs an initial access token |
| C5 | **Single-use PAR `request_uri`** | today it can be reused until it expires (90 s) |
| C6 | **Admin "sessions" and "tokens" screens**: list or kill a user's sessions and tokens | builds on B4 |
| C7 | **CIBA readiness**: the user-notification channel decided in S4 | prerequisite for D1 |

### Tier D: exotic but in scope (could, in the same PR if the spikes come back green)

| # | Feature |
|---|---|
| D1 | **CIBA** (OpenID CIBA core; poll mode first, then ping) |
| D2 | **RAR**, Rich Authorization Requests (RFC 9396, `authorization_details`) |
| D3 | **JARM** (JWT-secured authorization response mode) |
| D4 | **BFF package**: token management and an API proxy for SPA relying parties (the Duende.BFF equivalent) |
| D5 | **Additional signing algorithms**: EdDSA (Ed25519), ES384/ES512, PS384/PS512 |
| D6 | **Client-certificate–bound refresh tokens** for public clients (RFC 8705 §4) |

### Decided not to do (not gaps)

- **Per-client CORS:** a CORS preflight request carries no `client_id`, so the IdP can only check the
  union of all applications' origins. This is documented in `Services/OidcCorsOrigins.cs:14-19`.
- **The §7 security exclusions:** implicit flow, ROPC, hybrid flow and `check_session_iframe`.

## 4. Spikes (each one answers a single question with a measurement; no production code)

| Spike | Question | How we measure | Unblocks |
|---|---|---|---|
| **S1 mTLS behind a proxy** | Can a client certificate reach SparkId through nginx or Caddy (`ssl_client_escaped_cert` / `X-Client-Cert` plus ASP.NET `AddCertificateForwarding`), accepted **only** from trusted proxies? Do `mtls_endpoint_aliases` on a separate host or port work? | Docker nginx with `ssl_verify_client optional_no_ca` in front of SparkId. A forged header from an untrusted IP is refused (test red → green). The FAPI 2.0 mTLS plan passes | B2, B5 |
| **S2 DPoP nonces** | Stateless nonces (an HMAC over a time window) or nonces stored in RavenDB, with several instances? How does the `use_dpop_nonce` retry work at the token endpoint **and** at resource servers (`AddSparkResourceServer`)? | Two SparkId instances behind a load balancer: a nonce from node 1 is accepted on node 2, a replay is rejected. Count the extra round trips per token request. The FAPI DPoP plan passes | B1, B5, A6 |
| **S3 Key rotation across nodes** | With several instances, can the RavenDB key ring + Data Protection ever sign a token with a key that is not yet in the JWKS a resource server cached? Who promotes keys, and how long do caches live? | Force a rotation every minute under load, on two nodes. Target: 0 validation failures at Fleet; record the time for a new key to propagate | B5, D5 |
| **S4 CIBA notification channel** | Poll, ping or push? How do we reach the user: Spark messaging, web push through the PWA (#464), or mail? | End-to-end latency from the backchannel request to the user prompt, with no new dependency. The FAPI-CIBA plan in poll mode passes | C7, D1 |
| **S5 Server-side sessions** | Can a RavenDB `ITicketStore` share the `OidcSessionStore` record? What does it cost per request, and how does sliding expiry interact with revisions? | p95 latency per authenticated request, before and after. Killing a session invalidates the cookie on the next request, on two nodes | B4, C6 |
| **S6 Extension-grant API shape** | Does an `IExtensionGrant` fit `[Register]`/`[Inject]` composition, and does discovery advertise it automatically? | Implement the RFC 7523 grant as an extension, with no `Token.cs` change beyond the dispatcher. `grant_types_supported` updates without code changes | B8, B9 |
| **S7 Telemetry instrument set** | Naming is settled by G4. For each runtime library, which domain outcomes can HTTP metrics not see, and so deserve an instrument? What is the per-request overhead when no listener subscribes? | Run the E2E journey with an OTLP exporter: one span per endpoint, and the counters match the `INC:Tokens` time series | B6 |
| **S8 Build or adopt (OpenIddict)** | For the long tail (CIBA, RAR, JARM), is adopting OpenIddict's server cheaper than extending our own protocol layer, which already passes conformance? | Prototype JARM both ways. Compare files and lines touched, and the conformance result. Default unless proven otherwise: **keep our own** (the platform PRD's D1 already chose a RavenDB-native design) | D1–D3 |

## 5. Milestones (one PR; tests written per milestone, run **once** at the end)

| # | Milestone | Items |
|---|---|---|
| P0 | Spikes S1–S8, with results written into §4 | — |
| P1 | Close the #499 residuals | A1–A5, A7, A8, A10 |
| P2 | Refresh-token lifetime model (absolute + sliding, reuse vs one-time) | B3 |
| P3 | Profile / claims service | B10 |
| P4 | Spark-wide telemetry (G4): `TelemetryCategory` attribute, category resolver, configuration switches, instruments in every runtime library; the identity provider's event sinks (audit + metrics); every error exit raises an event | B6, B7 |
| P5 | Extension grants + JWT-bearer grant | B8, B9 |
| P6 | DPoP nonces + distributed RS replay cache | B1, A6 |
| P7 | mTLS behind a proxy + endpoint aliases | B2 |
| P8 | Server-side sessions + admin sessions/tokens screens | B4, C6 |
| P9 | FAPI 2.0 profile switch | B5 |
| P10 | Reference tokens, single-use PAR, JAR by reference | C1, C3, C5 |
| P11 | Dynamic providers | C2 |
| P12 | DCR conformance mode, then run **all** conformance plans (basic, config, form-post, logout ×3, dynamic, FAPI 2.0) | C4, A9 |
| P13 | CIBA, RAR, JARM, extra algorithms, certificate-bound refresh tokens | C7, D1–D3, D5, D6 |
| P14 | The `MintPlayer.Spark.Bff` package (G2), the IdP's BFF application template, and HR's Fleet proxy rewritten onto it | D4 |
| P0a | `tools/conformance/` docker-compose (G5); needed before S1–S3 | — |

Each milestone updates `docs/guide-identity-provider-developers.md`, discovery and the test matrix.
New security features each get a red → green test.

## 6. Owner decisions (grilled 2026-10-09, all locked)

**G1. Tier D is in this PR, gated by its spikes.** A Tier D feature lands only when its spike shows it
passes its own conformance plan (FAPI-CIBA for CIBA). Otherwise it moves to §7 with the measured
reason. It never becomes a follow-up.

**G1a. Design rule for every extension point in this PR.** Use the tools .NET already provides:
- one interface;
- several DI registrations (`[Register]` already registers several implementations side by side);
- the consumer injects all of them as `IEnumerable<T>`.

This applies to `IExtensionGrant`, `IIdentityProviderEventSink`, claims contributors (B10) and CIBA
notifiers. **No backward compatibility:** the packages are still in preview, so existing APIs are
replaced, not wrapped.

**G2. The BFF is a new opt-in package, `MintPlayer.Spark.Bff`** (`spark.AddBff(...)`). It integrates
with both packages:
- **Authorization:** it builds on the relying-party OIDC login and the cookie session (token storage,
  refresh, an API proxy that attaches the token).
- **IdentityProvider:** it adds a "BFF" application template whose defaults are a confidential client,
  code + PKCE, refresh tokens, and DPoP where it applies.

HR's hand-written Fleet proxy (`apps/HR/HR/Api/FleetCarsProxyEndpoint.cs`) is rewritten onto the BFF,
so HR is its first real user. HR, Fleet and QnA already sign in through SparkId.

**G3. The developer decides the refresh-token policy. Spark only ships defaults.** There are two
layers:
- `SparkIdentityProviderOptions` holds the host-wide defaults;
- each `OidcApplication` can override them.

The settings are the sliding window, the absolute cap for public clients, the absolute cap for
confidential clients, and single-use or reusable tokens. The shipped defaults:

| Setting | Default |
|---|---|
| Sliding window | 14 days |
| Absolute cap, public clients | 30 days, counted from `ChainStartedAt` |
| Absolute cap, confidential clients | 90 days, counted from `ChainStartedAt` |
| Token use | single-use, with reuse detection |

At deploy, existing chains get `ChainStartedAt` set to the deploy time, so nobody is logged out.

**G4. Telemetry covers every Spark library, not just the identity provider.** It is in this PR. The
rules:
- **Category name:** by default, a library's telemetry category is its **full assembly name**, for
  example `MintPlayer.Spark.Authorization`. This applies to any assembly, ours or third-party.
  `[assembly: TelemetryCategory("…")]` overrides it.
- **Meter and activity source:** each category has one meter and one activity source, named after the
  category.
- **Configuration:** the developer turns categories on or off in app configuration, for example
  `Spark:Telemetry:Categories:<category>: false`. A disabled category creates no instruments.
- **Default:** on. OpenTelemetry only collects meters a listener subscribes to, so an unsubscribed
  category costs close to nothing.
- **Assemblies without runtime code** (Abstractions, SourceGenerators, Attributes, Testing) emit
  nothing.
- **Shared plumbing:** the attribute, the category resolver and the per-category
  `Meter`/`ActivitySource` factory live in `MintPlayer.Spark.Abstractions`.
- **What already exists is not duplicated:** HTTP rate, latency and status, and trace propagation,
  already come from ASP.NET Core and `HttpClient`. Spark emits only the domain outcomes HTTP can't
  see. For the identity provider those are the reason behind a 400 (`invalid_client`, refresh-token
  reuse, DPoP replay, expired code).
- **The identity provider's events** go through `IEnumerable<IIdentityProviderEventSink>`. The library
  ships two sinks: the RavenDB audit sink, and a metrics sink built on the shared telemetry.
- ⚠️ **No personal data in metric attributes:** user identifiers never go on metrics (cardinality,
  personal data). They may appear on spans only, in pairwise or hashed form.
- ⚠️ **Every error exit raises an event,** or the metrics go blind to it. A test enforces this for the
  `/connect/*` endpoints.

**G5. Conformance infrastructure is a committed docker-compose in `tools/conformance/`.** It contains:
- the OpenID conformance suite;
- an nginx TLS front that forwards client certificates;
- two SparkId nodes;
- RavenDB.

It runs locally, on demand, **never in CI**. Image versions are pinned. Every pass records its date and
plan IDs in §0, so a stale date shows when the proof has gone out of date. It is used for A9, S1, S2
and S3.

## 7. Not done (security exclusions, carried over from the platform PRD §5)

- Implicit flow and ROPC: removed by OAuth 2.1 / RFC 9700.
- Hybrid flow: only obsolete FAPI 1 uses it.
- `check_session_iframe`: broken by third-party cookie blocking. Front-channel and back-channel
  logout cover the same need.

## 8. Acceptance

- Every row of §3 (tiers A–C, and D unless the owner moves it out in §6) is implemented, with tests.
- All OpenID conformance plans pass: basic, config, form-post, RP-initiated / front-channel /
  back-channel logout, dynamic OP, and FAPI 2.0 (DPoP and mTLS variants).
- Discovery advertises exactly what is implemented (D8, carried over).
- The local sweep `npm run test:affected` is green, and so is CI on the single PR.
