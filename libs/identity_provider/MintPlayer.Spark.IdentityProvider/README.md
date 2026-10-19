# MintPlayer.Spark.IdentityProvider

Turns a Spark application into an OpenID Connect provider with a developer portal: the discovery and
JWKS documents, the `/connect/*` protocol endpoints and pages, and `/spark/identity-provider/*` for the
SPA. Applications, resources, grants, tokens, keys and audit events are RavenDB documents. The package is
a **Spark library layer** (`identity-provider`) that ships its own model, rights, menu and translations.

Design and decisions: [docs/identity_provider_platform_PRD.md](../../../docs/identity_provider_platform_PRD.md).
For developers registering applications: [docs/guide-identity-provider-developers.md](../../../docs/guide-identity-provider-developers.md).

```csharp
spark.AddAuthentication<SparkUser>(auth => auth.LocalCredentials = SparkLocalCredentials.Full);
spark.AddIdentityProvider(options =>
{
    options.Issuer = builder.Configuration["SparkIdentityProvider:Issuer"]; // required outside Development
});
```

`AddAuthentication<TUser>()` must come first: the token, login and logout endpoints are closed over the
user type. On the SPA side:

```ts
sparkAuthRoutes(/* … */),
withIdentityProvider(withConnectedApplications(), withDeveloperRoutes(), withManagementRoutes()),
// and provideSparkClientOperations() for the show-once secret dialog
```

## AI agents: `ForMcp()`

With the Mcp package, the provider can be the application's own authorization server for AI agents (setup 3 of
[docs/spark_mcp_PRD.md](../../../docs/spark_mcp_PRD.md)):

```csharp
spark.AddIdentityProvider(idp => idp.ForMcp(mcp =>
{
    mcp.MetadataDocumentHosts = [];          // empty: client metadata documents from any public HTTPS host
}));
spark.AddMcp(mcp => { /* … */ });            // required; so is a canonical URL (Spark:Mcp:CanonicalUrl or Spark:Auth:PublicBaseUrl)
```

It adds, and changes nothing else:

- `GET /.well-known/oauth-authorization-server` (RFC 8414): code + PKCE (S256), refresh, revocation, scopes
  `mcp:read`, `mcp:write`, `offline_access`, `client_id_metadata_document_supported`, the `none` auth method,
  `authorization_response_iss_parameter_supported` (RFC 9207 `iss` is on every authorization response).
- The API resource `OidcResources/mcp`, owning `mcp:read` and `mcp:write`, whose `Audience` is the MCP
  canonical URL. A request for those scopes may name only that `resource` (RFC 8707; anything else is
  `invalid_target`, also at the token endpoint), its token has exactly that audience and the local user id as
  `sub` (never pairwise), and it always shows the agent consent screen.
- **Client ID Metadata Documents**: a `client_id` that is an `https` URL is fetched through the SSRF-safe client
  (public addresses only, one DNS resolution for check and connect, port 443, no redirects, `MetadataDocumentMaxBytes`
  5 KiB, `MetadataDocumentTimeout` 5 s), kept for `MetadataDocumentCacheFor` (1 h). The URL needs a path and no
  user info, query, fragment, dot segments or trailing `/`; the document must name its own URL as `client_id`, be a
  public client, and list https or loopback redirect URIs. The client is stored at `OidcApplications/agent-{hash}`.
- **Anonymous registration** at `/connect/register` (no initial access token; `DynamicRegistration`, default on):
  public clients whose redirect URIs are all loopback (`localhost`, `127.0.0.1`, `[::1]`) or VS Code's
  `https://vscode.dev/redirect` / `https://insiders.vscode.dev/redirect`. Labelled unverified, no registration
  access token, `RegistrationsPerHour` (20) per address together with document fetches, deleted after
  `UnusedClientLifetime` (30 days) without use by a job that runs every six hours. A request with an initial
  access token is a developer's registration, as before.
- Loopback redirect URIs of agent clients match on any port and any path (RFC 8252 §7.3).
- Refresh tokens for agents whether or not they asked for `offline_access`, rotating, a reused one revoking exactly
  its family (see "Refresh-token families" below); not tied to the browser session (signing out of the website does
  not sign the agent out).
- The agent consent screen: client name, verified host (metadata documents) or "unverified", the loopback
  notice, the types the agent may use and read or read + changes, limited by the Mcp package's
  `AgentPermissions(agent)` and each type's `agentPermissions`. A locked account is refused. Allowing writes
  `AgentGrants/{userId}/app/{clientId}`, linked to the `OidcGrant`; revoking either revokes both.
- The `/mcp` bearer scheme for these tokens (`IAgentCredentialScheme`, setup `EmbeddedAuthorizationServer`).
- Audit events `registration.metadata-document`, `registration.pruned`, `token.issued`, `token.revoked`,
  `token.pkce.failed`.

Without `ForMcp()` the provider also contributes its sign-ins at other websites to the account page's
**Connected applications** (`ISparkConnectedApplicationSource`).

`ForMcp()` leaves the rest of the provider mapped: the device grant, PAR, introspection, userinfo, end-session, the
developer portal and key administration. An application that hosts the provider **only** for its agents adds
`AgentsOnly()`, which maps what agents use and nothing else ([below](#only-what-agents-need-agentsonly)):

```csharp
spark.AddIdentityProvider(idp => idp.ForMcp().AgentsOnly());
```

## AI agents for another application: `ForMcpResource(url)`

The provider can also sign agents in for **another** application's MCP endpoint, which accepts its tokens as its external
authorization server (that application's `spark.AddExternalAgentAuthorizationServer(issuer, loginProvider)`, setup 2 of
the PRD). It needs no MCP endpoint of its own, and nothing else changes:

```csharp
spark.AddIdentityProvider(idp => idp.ForMcpResource("https://hr.example.com/mcp", r =>
{
    r.DisplayName = "HR";                                          // the consent screen; default: the URL's host
    r.Permissions = EAgentPermission.Read | EAgentPermission.Write; // default; Read alone never grants mcp:write
}));
```

or in configuration, one entry per endpoint:

```json
{ "Spark": { "IdentityProvider": { "Mcp": { "Resources": [ { "Url": "https://hr.example.com/mcp", "DisplayName": "HR" } ] } } } }
```

- Each endpoint is an API resource `OidcResources/mcp-{sha256(url)[..32]}` whose `Audience` is the URL, with the scopes
  `mcp:read` and `mcp:write`. The agent names the endpoint in `resource` (RFC 8707); the token's only audience is that
  URL and its `sub` is the user's id here (public, never pairwise). The token and refresh endpoints resolve the scopes
  for the endpoint the chain was authorized for; one that is no longer configured gets no token (`invalid_scope`).
- Everything else is `ForMcp()`'s: the RFC 8414 document, metadata documents, anonymous loopback registration with VS
  Code's redirects, refresh rotation with family revocation, the audit events.
- **The consent screen** names the application and its host, and offers read, or read + changes when `Permissions`
  allows them. It cannot see what that application exposes, so it offers no types: the agent's grant lives in that
  application, created on its first call, and the user picks types and turns changes on there (Connected applications).
  Changes chosen here put `mcp:write` in the token, which that application requires beside its own toggle. No agent
  grant is written here; the `OidcGrant` keeps the OAuth bookkeeping, revocable at `/connect/applications`.
- **Choosing the endpoint:** one `resource`, an endpoint listed here (another, or two at once, is `invalid_target`). A
  request without one means this application's own endpoint (`ForMcp()`), else the only endpoint there is, else it is
  refused.
- The URL must be exactly the canonical URL that application publishes (`Spark:Mcp:CanonicalUrl`): https (http only
  on a loopback host), no query, fragment or user info; a trailing `/` is dropped. A malformed or duplicate URL stops
  startup.
- `Mcp:LocalEndpoint`: `ForMcp()` sets it. Left unset, a `Spark:IdentityProvider:Mcp` section serves this
  application's own `/mcp` only when it lists no `Resources`; the `/mcp` bearer scheme is registered only then.
- That application's users link this provider's account by signing in to it here (its `loginProvider`); the linked
  login's key is the `sub` this provider gave that sign-in client. Keep that client on public subjects (the default):
  agent tokens carry the public user id, and a pairwise key would not match (the agent gets 401).
- One OAuth grant per user and agent client: a client signed in for two endpoints shares it, and revoking it ends the
  client's sign-ins for both.

## The host's part: two groups

The layer's `security.json` grants rights to two slots. The host binds them to its own groups in its
`App_Data/security.json`:

```json
"bindings": {
  "identity-provider:administrators": ["Administrators"],
  "identity-provider:developers": ["Developers"]
}
```

- **Administrators** see and manage every application and resource (`ManageAll/IdentityProvider`),
  decide developer requests, scope approvals and go-live reviews, see the audit trail and every grant,
  and rotate keys.
- The decisions are approval requests (`MintPlayer.Spark.Approvals`, which the provider adds itself; see
  `docs/guide-approvals.md`). The kinds are `identity-provider.developer`, `identity-provider.scope` and
  `identity-provider.go-live`.
  - Administrators decide all three. An API's owners also decide the scope requests for that API.
  - Every decider works through the Approvals inbox (`pending-approvals`), which the provider's menu links to.
  - A rejection or a revocation needs a reason. A developer's reason is shown on `/developers`.
- **Developers** are users whose developer request was approved (or who were made developers). They
  see the applications they are a member of and the API resources they own.

## Endpoints

| Path | Purpose |
|---|---|
| `/.well-known/openid-configuration`, `/.well-known/jwks` | Discovery; every published key (next, active, retired) |
| `/connect/authorize` (GET and POST) | Code flow with PKCE; `prompt`, `max_age`, `acr_values`, `login_hint`, `ui_locales`, `claims`, `id_token_hint`, `resource`, `request` (JAR), `request_uri` (PAR only), `response_mode=query|form_post`; answers carry `iss` (RFC 9207) |
| `/connect/par` | Pushed authorization requests (RFC 9126); the `request_uri` lives 90 s |
| `/connect/token` | `authorization_code`, `refresh_token` (rotating; a reused one revokes exactly its family, at once, and is audited), `client_credentials`, the device grant, token exchange (RFC 8693) |
| `/connect/device_authorization`, `/connect/device` | Device grant (RFC 8628) and its user-code page |
| `/connect/userinfo` | Plain, signed or encrypted per client |
| `/connect/introspect`, `/connect/revoke` | RFC 7662 / RFC 7009; introspection answers the token's owner and its audiences |
| `/connect/register[/{client_id}]` | Dynamic registration (RFC 7591/7592) with an initial access token from `POST /spark/identity-provider/developer/registration-token` |
| `/connect/login`, `/connect/two-factor`, `/connect/consent`, `/connect/logout`, `/connect/applications`, `/connect/error` | Server-rendered pages, localized (`ui_locales`, the `spark-lang` cookie, `Accept-Language`), light/dark |
| `/spark/identity-provider/*` | The SPA's API: developer status, invitations, connected applications, admin keys |

All of them are mapped by default; [Choosing flows and endpoints](#choosing-flows-and-endpoints) leaves some out.

Client authentication: `client_secret_basic`, `client_secret_post`, `private_key_jwt` (RFC 7523),
`tls_client_auth`, `self_signed_tls_client_auth` (RFC 8705), or none for a public client. A client
registered with a method may use only that one. Failures answer a uniform `invalid_client`.

Tokens:
- Access tokens are JWTs typed `at+jwt` (RFC 9068), each backed by a record so they can be revoked.
- DPoP (RFC 9449) and certificate binding (RFC 8705) put `cnf` in the token.
- Pairwise subjects per sector are available.
- id_tokens carry `azp`, `amr`, `acr`, `sid`, `at_hash`, `auth_time`, and can be encrypted
  (`RSA-OAEP` with `A128CBC-HS256`/`A256CBC-HS512`).

## Choosing flows and endpoints

By default the provider offers every flow and maps every endpoint. `Flows` and `Endpoints` choose fewer. What is left
out is not mapped (404), refused at `/connect/token` (`unsupported_grant_type`), and absent from both discovery
documents, which list only what is mapped.

```csharp
spark.AddIdentityProvider(idp =>
{
    idp.Flows = SparkIdentityProviderFlows.AuthorizationCode | SparkIdentityProviderFlows.RefreshToken;
    idp.Endpoints = SparkIdentityProviderEndpoints.Revocation | SparkIdentityProviderEndpoints.UserInfo;
});
```

or in configuration, as comma-separated names:

```json
{ "Spark": { "IdentityProvider": { "Flows": "AuthorizationCode, RefreshToken", "Endpoints": "Revocation, UserInfo" } } }
```

| `Flows` | Maps |
|---|---|
| `AuthorizationCode` | `/connect/authorize` (GET and POST), `/connect/consent`, the `authorization_code` grant |
| `RefreshToken` | the `refresh_token` grant; without it no refresh token is issued |
| `ClientCredentials` | the `client_credentials` grant |
| `DeviceCode` | `/connect/device_authorization`, `/connect/device`, the device grant |
| `TokenExchange` | the token-exchange grant (RFC 8693) |

With `AuthorizationCode` or `DeviceCode` the sign-in pages (`/connect/login`, `/connect/two-factor`) and
`/connect/error` are mapped too. `/connect/token`, `/.well-known/openid-configuration` and `/.well-known/jwks` always are.

| `Endpoints` | Maps |
|---|---|
| `UserInfo` | `/connect/userinfo` (GET and POST) |
| `EndSession` | `/connect/logout`: RP-initiated, back- and front-channel logout, and the consent pages' "Not you?" link |
| `Introspection` | `/connect/introspect` |
| `Revocation` | `/connect/revoke` |
| `PushedAuthorization` | `/connect/par` |
| `DeveloperRegistration` | `POST /connect/register` with an initial access token, `/connect/register/{clientId}` (RFC 7592), and `POST /spark/identity-provider/developer/registration-token`, which issues those tokens |
| `DeveloperPortal` | `/spark/identity-provider/developer` (GET and POST) and `…/invitations/accept` |
| `ConnectedApplications` | `/connect/applications` and its two revoke forms, `/spark/identity-provider/applications[/withdraw]`, and the consent pages' link to them |
| `Administration` | `/spark/identity-provider/admin/keys[/rotate]` |

Anonymous registration of AI agents is the agent server's own switch (`ForMcp(m => m.DynamicRegistration = …)`, default
on): it maps `POST /connect/register` without `DeveloperRegistration`, and a request there carrying an initial access
token is then refused (401), because developer registration is off.

Startup refuses a combination that cannot work, and the message names the dependency:

- `RefreshToken`, `UserInfo`, `EndSession` and `ConnectedApplications` need `AuthorizationCode` or `DeviceCode`;
- `PushedAuthorization` needs `AuthorizationCode`;
- `DeveloperRegistration` needs `DeveloperPortal`, where developers get their initial access tokens;
- `ForMcp()` and `ForMcpResource()` need `AuthorizationCode`;
- `ForMcpResource()` needs `UserInfo`: that application's users sign in here to link their login, and Spark's OpenID
  Connect sign-in reads userinfo;
- `Flows = None` would issue no tokens.

### Only what agents need: `AgentsOnly()`

For an application that hosts the provider only to sign AI agents in to its own `/mcp` (Fleet in the demo):

```csharp
spark.AddIdentityProvider(idp => idp.ForMcp().AgentsOnly());
```

It sets `Flows = AuthorizationCode | RefreshToken` and `Endpoints = Revocation`; a later assignment wins. Mapped:

- the discovery document, the RFC 8414 document and the JWKS;
- `/connect/authorize`, with the sign-in, two-factor, consent and error pages;
- `/connect/token` for `authorization_code` and `refresh_token`;
- `/connect/register` for anonymous loopback registration, and Client ID Metadata Documents;
- `/connect/revoke`.

Everything else answers 404. The consent page then shows no "Not you?" link and no link to `/connect/applications`.
Users see and revoke their agents on the application's account page (Connected applications), which the provider still
feeds. Why userinfo and end-session are left out:

- No MCP client calls them. The MCP authorization spec defines discovery, registration, authorization code with PKCE
  and `resource`, and refresh. Source read on 2026-10-12: Codex and rmcp, Gemini CLI and VS Code reference neither
  endpoint.
- Agent tokens carry `mcp:read`, `mcp:write` and `offline_access`, never `openid`, so there is no OpenID sign-in to
  describe or end.

The provider's management types belong to its library layer, which `AgentsOnly()` does not change. Switch them off in
the application's `security.json` (`"libraries": { "identity-provider": false }`), as Fleet does, or bind its slots.

`AgentsOnly()` is not for a provider that also signs agents in for another application's endpoint (`ForMcpResource`).
That application's users sign in here to link their login, and Spark's OpenID Connect sign-in reads userinfo, so
startup refuses the combination.

## Configuration (`Spark:IdentityProvider`)

| Key | Default | Meaning |
|---|---|---|
| `Issuer` | request origin in Development | The `iss` of every token |
| `Flows` / `Endpoints` | `All` / `All` | What the provider offers and maps ([Choosing flows and endpoints](#choosing-flows-and-endpoints)) |
| `SigningKeyPath` | | A legacy key file, imported once into the key ring |
| `Developers:RequireApproval` / `TermsVersion` / `TermsUrl` | `true` / `1` / none | Becoming a developer |
| `Apps:RequireReviewToGoLive` / `MaxRedirectUris` / `InvitationLifetime` | `false` / `20` / 7 days | Applications |
| `TwoFactor:Enabled` | `true` | The provider's own second factor at `/connect/login` |
| `Branding:ProductName` / `LogoUrl` / `ExtraCss` | | The `/connect` pages' header and styles |
| `RegistrationPath` | `/register` | The application's registration page, linked from `/connect/login` when `LocalCredentials` is `Full` ([Login page and federation](#login-page-and-federation)); empty hides the link |
| `Keys:RotationDays` / `PrePublishDays` / `RetainRetiredDays` | `90` / `2` / `14` | Signing-key rotation |
| `Audit:RetentionDays` | `180` | RavenDB deletes older audit events |
| `RateLimits:PermitLimit` / `Window` | `120` / 1 min | Per IP on token, PAR, device authorization, revocation and userinfo, when the app runs `spark.AddRateLimiter()` |
| `RateLimits:ClientAuthenticationFailures` / `ClientAuthenticationFailureWindow` | `10` / 5 min | Failed client authentications per client and IP before that pair is refused |
| `EnableDynamicCors` | `false` | CORS for the registered origins on the browser-facing endpoints |
| `ClientRequests:AllowedNetworks` / `AllowedPorts` / … | none / `80`, `443` | The SSRF-safe clients that fetch a client's `jwks_uri` and post to its `backchannel_logout_uri` (`SafeHttpOptions`). Loopback, private and metadata addresses are refused; a local relying party in development is allowed by naming it, e.g. `AllowedNetworks: ["127.0.0.1/32"]` and its port. A configured list **replaces** its default, so `AllowedPorts: [5001]` allows 5001 only; list 443 as well to keep it. The back-channel client follows at most 3 redirects, all within the origin (scheme, host, port) of the registered URI; a cross-origin redirect is refused because it would resend the logout token |

## Keys

Keys are documents in `OidcKeys`, with the private key protected by ASP.NET Data Protection. Two key
types are kept, RSA (RS256) and EC (ES256):
- A key is published `PrePublishDays` before it signs, so relying parties have it cached.
- It signs for `RotationDays`.
- After that it stays published for `RetainRetiredDays`.

`OidcKeyRotationService` advances the schedule. `POST /spark/identity-provider/admin/keys/rotate`
rotates at once (administrators only).

## Scopes, resources and claims

`OidcResources` holds two kinds of resource:
- **Identity resources:** `openid`, `profile`, `email`, … Each is one scope with its `ClaimTypes`.
- **API resources:** each holds scopes named `<resource>.<scope>`. The resource's name is the access
  token's `aud`.

An application lists its scopes with a required flag. A scope owned by someone else stays *Pending*
until its owner approves it, through an `identity-provider.scope` approval request raised with the save.
The status is the server's: a save keeps every existing scope's stored status.

Claims come only from the granted scopes' `ClaimTypes`. The `claims` parameter never widens consent.

| Scope | ClaimTypes | Source |
|---|---|---|
| `openid` | (`sub` is always emitted) | `SparkUser.Id` (pairwise per sector when the client asks) |
| `profile` | `name`, `preferred_username`, `given_name`, `family_name` | `UserName`; given and family name from the user's stored claims, emitted only when stored |
| `email` | `email`, `email_verified` | `Email`, `EmailConfirmed` (a JSON boolean) |
| (custom, e.g. `roles`) | `role` | `SparkUser.Roles` |

## Consent

- Consent is remembered with an expiry and is incremental (`include_granted_scopes`).
- Users withdraw it whole or per scope, from the SPA's connected-applications page or `/connect/applications`.
- Withdrawal revokes the tokens issued under it.
- Disabling an application revokes its tokens and grants. Disabling a resource or an API scope revokes
  the tokens that carry it.

## Lifetimes

Per client (`OidcApplication`):

| Property | Default | Applies to |
|---|---|---|
| `AccessTokenLifetimeMinutes` | 60 | access tokens (`expires_in`) |
| `IdTokenLifetimeMinutes` | 5 | id_tokens |
| `RefreshTokenLifetimeDays` | 14 | refresh tokens (only with `offline_access` and the `refresh_token` grant) |

Authorization codes live 5 minutes and are single-use.

## Refresh-token families

Each code or device code redeemed for a refresh token starts a family: an `OidcRefreshFamilies` document that records
whether the family stands and which refresh token is its active one. Every refresh token, and every access token issued
with one, carries the family's id, and every use loads the family by id, never through an index:

- A refresh is honoured only for the family's active refresh token. The rotation advances the family in the same
  batch, so two rotations of one token cannot both succeed.
- A rotated refresh token presented again, one refresh token or code spent twice at once, or a code presented twice
  revokes the family at once (RFC 9700 §4.14.2, RFC 6749 §4.1.2): its active refresh token and its access tokens are
  refused from the next request on, and the reuse is audited. Nothing else of the grant is touched: the same client
  on the user's other machines runs other families of the same grant, and keeps working.
- Withdrawing the grant ends every family issued under it: each use loads the grant too.

## Login page and federation

`/connect/login` is mapped whenever a flow signs users in (`AuthorizationCode` or `DeviceCode`, so by default). It shows:

- The password form, unless `LocalCredentials` is `Disabled`. In that case the POST and
  `/connect/two-factor` are unmapped.
- A button per interactive external scheme, so the provider can itself sign people in with GitHub,
  Google, or another OIDC provider.
  - Each button runs the external login in redirect mode.
  - Success resumes the pending authorization.
  - A refusal comes back as a fixed message.
- A "Create an account" link when the application offers registration (`LocalCredentials` is `Full`). It points at
  `RegistrationPath` (default `/register`, ng-spark's `withRegistration()`) with the pending authorization as
  `returnUrl`, which the registration page passes on to its sign-in step. A page asked with an off-site `returnUrl` shows
  no link. Set `RegistrationPath` to an empty string to hide it.

## Sessions and logout

- Each interactive sign-in starts a provider session, whose `sid` appears in id_tokens.
- `GET /connect/logout` (`Endpoints.EndSession`) ends it:
  - It sends back-channel logout tokens to the clients' `BackChannelLogoutUri`.
  - It renders front-channel iframes for their `FrontChannelLogoutUri`.
  - It revokes the session's refresh tokens.
  - It honours `post_logout_redirect_uri` only when that URI is registered for the client.
- The client is named by `client_id` or by `id_token_hint`.

## Resource servers

APIs that accept these tokens use `MintPlayer.Spark.Authorization`:

```csharp
spark.AddSparkResourceServer("https://id.example", "fleet");   // at+jwt, DPoP/cnf enforced
app.MapGet("/api/fleet/cars", …).RequireScope("fleet.read");
```

`UseIntrospection` asks the provider about every token instead. The introspecting client's id must
be the audience.

## The demo

`apps/SparkId` is the provider (`https://localhost:5011`), with every flow and endpoint. QnA (5009), HR (5005) and
Fleet (5003) are its relying parties. Fleet also hosts a provider of its own, for its AI agents only
(`idp.ForMcp().AgentsOnly()`). In Development, SparkId's migrations seed:
- the identity scopes;
- the `qna`, `hr` and `fleet` clients, with published development-only secrets;
- the `fleet` API resource with `fleet.read`, which HR requests and passes to Fleet's
  `/api/fleet/cars`.
