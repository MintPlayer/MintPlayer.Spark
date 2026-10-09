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
- **Developers** are users whose developer request was approved (or who were made developers). They
  see the applications they are a member of and the API resources they own.

## Endpoints

| Path | Purpose |
|---|---|
| `/.well-known/openid-configuration`, `/.well-known/jwks` | Discovery; every published key (next, active, retired) |
| `/connect/authorize` (GET and POST) | Code flow with PKCE; `prompt`, `max_age`, `acr_values`, `login_hint`, `ui_locales`, `claims`, `id_token_hint`, `resource`, `request` (JAR), `request_uri` (PAR only), `response_mode=query|form_post`; answers carry `iss` (RFC 9207) |
| `/connect/par` | Pushed authorization requests (RFC 9126); the `request_uri` lives 90 s |
| `/connect/token` | `authorization_code`, `refresh_token` (rotating; a reused one revokes its whole token chain and is audited), `client_credentials`, the device grant, token exchange (RFC 8693) |
| `/connect/device_authorization`, `/connect/device` | Device grant (RFC 8628) and its user-code page |
| `/connect/userinfo` | Plain, signed or encrypted per client |
| `/connect/introspect`, `/connect/revoke` | RFC 7662 / RFC 7009; introspection answers the token's owner and its audiences |
| `/connect/register[/{client_id}]` | Dynamic registration (RFC 7591/7592) with an initial access token from `POST /spark/identity-provider/developer/registration-token` |
| `/connect/login`, `/connect/two-factor`, `/connect/consent`, `/connect/logout`, `/connect/applications`, `/connect/error` | Server-rendered pages, localized (`ui_locales`, the `spark-lang` cookie, `Accept-Language`), light/dark |
| `/spark/identity-provider/*` | The SPA's API: developer status, invitations, connected applications, admin keys |

Client authentication: `client_secret_basic`, `client_secret_post`, `private_key_jwt` (RFC 7523),
`tls_client_auth`, `self_signed_tls_client_auth` (RFC 8705), or none for a public client. A client
registered with a method may use only that one. Failures answer a uniform `invalid_client`.

Tokens:
- Access tokens are JWTs typed `at+jwt` (RFC 9068), each backed by a record so they can be revoked.
- DPoP (RFC 9449) and certificate binding (RFC 8705) put `cnf` in the token.
- Pairwise subjects per sector are available.
- id_tokens carry `azp`, `amr`, `acr`, `sid`, `at_hash`, `auth_time`, and can be encrypted
  (`RSA-OAEP` with `A128CBC-HS256`/`A256CBC-HS512`).

## Configuration (`Spark:IdentityProvider`)

| Key | Default | Meaning |
|---|---|---|
| `Issuer` | request origin in Development | The `iss` of every token |
| `SigningKeyPath` | | A legacy key file, imported once into the key ring |
| `Developers:RequireApproval` / `TermsVersion` / `TermsUrl` | `true` / `1` / none | Becoming a developer |
| `Apps:RequireReviewToGoLive` / `MaxRedirectUris` / `InvitationLifetime` | `false` / `20` / 7 days | Applications |
| `TwoFactor:Enabled` | `true` | The provider's own second factor at `/connect/login` |
| `Branding:ProductName` / `LogoUrl` / `ExtraCss` | | The `/connect` pages' header and styles |
| `Keys:RotationDays` / `PrePublishDays` / `RetainRetiredDays` | `90` / `2` / `14` | Signing-key rotation |
| `Audit:RetentionDays` | `180` | RavenDB deletes older audit events |
| `RateLimits:PermitLimit` / `Window` | `120` / 1 min | Per IP on token, PAR, device authorization, revocation and userinfo, when the app runs `spark.AddRateLimiter()` |
| `RateLimits:ClientAuthenticationFailures` / `ClientAuthenticationFailureWindow` | `10` / 5 min | Failed client authentications per client and IP before that pair is refused |
| `EnableDynamicCors` | `false` | CORS for the registered origins on the browser-facing endpoints |

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
until its owner approves it.

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

## Login page and federation

`/connect/login` is always mapped. It shows:

- The password form, unless `LocalCredentials` is `Disabled`. In that case the POST and
  `/connect/two-factor` are unmapped.
- A button per interactive external scheme, so the provider can itself sign people in with GitHub,
  Google, or another OIDC provider.
  - Each button runs the external login in redirect mode.
  - Success resumes the pending authorization.
  - A refusal comes back as a fixed message.

## Sessions and logout

- Each interactive sign-in starts a provider session, whose `sid` appears in id_tokens.
- `GET /connect/logout` ends it:
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

`apps/SparkId` is the provider (`https://localhost:5011`). QnA (5009), HR (5005) and Fleet (5003) are its
relying parties. In Development, SparkId's migrations seed:
- the identity scopes;
- the `qna`, `hr` and `fleet` clients, with published development-only secrets;
- the `fleet` API resource with `fleet.read`, which HR requests and passes to Fleet's
  `/api/fleet/cars`.
