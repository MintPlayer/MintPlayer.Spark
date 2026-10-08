# MintPlayer.Spark.IdentityProvider

Turns a Spark application into an OpenID Connect provider: `/.well-known/openid-configuration`,
`/.well-known/jwks` and the `/connect/*` endpoints (authorize, token, userinfo, introspect, revoke,
logout, consent, connected applications, login, two-factor). Clients (`OidcApplication`) and scopes
(`OidcScope`) are RavenDB documents, administered through the application's own PersistentObject
screens (`IOidcApplicationContext`).

```csharp
spark.AddAuthentication<SparkUser>(auth => auth.LocalCredentials = SparkLocalCredentials.Full);
spark.AddIdentityProvider(options =>
{
    options.Issuer = builder.Configuration["SparkIdentityProvider:Issuer"]; // required outside Development
});
```

`AddAuthentication<TUser>()` must come first: the token, login and logout endpoints are closed over the
user type.

## What a relying party gets

- Authorization code flow only (`response_type=code`), delivered on the query string
  (`response_modes_supported: ["query"]`; there is no `form_post`). PKCE S256 is required per client
  (`RequirePkce`, on by default).
- Client authentication: `client_secret_post`. Secrets are stored hashed (`ClientSecretHasher`); a plain
  value entered on the admin screen is hashed on save.
- Exact-match redirect URIs, compared ordinally (casing matters).
- RS256 signatures with a `kid`; the id_token has a single `aud` (the client id), the `nonce` the client
  sent, `auth_time` (when the user actually signed in, kept across refreshes) and `at_hash` (binding it to
  the access token issued alongside).
- Discovery advertises `scopes_supported` (enabled scopes with `ShowInDiscoveryDocument`) and
  `claims_supported` (the claims the provider can emit for those scopes, plus the id_token's own).

The stock ASP.NET Core OpenIdConnect handler works against it unchanged. From another Spark app use the
preset, which sets code + PKCE, `response_mode=query` and `openid profile email`:

```csharp
spark.AddOpenIdConnect("SparkId", "Spark Identity", o =>
{
    o.Authority = "https://id.example";
    o.ClientId = "qna";
    o.ClientSecret = builder.Configuration["…"];
});
// or: spark.AddExternalProviders(builder.Configuration) with Spark:Auth:Providers:OpenIdConnect:SparkId
```

Register that client here with `RedirectUris = ["https://<rp-host>/signin-SparkId"]` and
`PostLogoutRedirectUris = ["https://<rp-host>/signout-callback-SparkId"]` — the preset's `CallbackPath` and
`SignedOutCallbackPath` are `/signin-<scheme>` and `/signout-callback-<scheme>` with the scheme's exact
casing. On the client side: `withExternalLogin(oidcProvider('SparkId', 'Spark Identity'))`.

The demo wiring: `apps/SparkId` is the provider (`https://localhost:5011`). QnA (5009), HR (5005) and
Fleet (5003) are its relying parties, and Fleet validates SparkId's machine tokens. SparkId's migration
`M_202610081200_RelyingParties` seeds the scopes below and the `qna`, `hr` and `fleet` clients in
Development only, with published development-only secrets that each app's `appsettings.Development.json`
repeats.

## Scopes and claims

Claims come only from the granted scopes' `ClaimTypes`; a scope listing a claim the provider has no
source for emits nothing for it. Seed these:

| Scope | ClaimTypes | Source |
|---|---|---|
| `openid` | (`sub` is always emitted) | `SparkUser.Id` |
| `profile` | `name`, `preferred_username`, `given_name`, `family_name` | `UserName` for the first two; `given_name` / `family_name` from the user's stored claims (`SparkUser.Claims`, under the OIDC name or `ClaimTypes.GivenName` / `ClaimTypes.Surname`) — **emitted only when stored**, since `SparkUser` has no name fields of its own |
| `email` | `email`, `email_verified` | `Email`, `EmailConfirmed` — `email_verified` is a JSON boolean in both the id_token and userinfo |
| (custom, e.g. `roles`) | `role` | `SparkUser.Roles`, as `role` in both the id_token and userinfo |

`picture`, `locale` and other standard claims are not supported.

## Lifetimes

Per client (`OidcApplication`):

| Property | Default | Applies to |
|---|---|---|
| `AccessTokenLifetimeMinutes` | 60 | access tokens (`expires_in`) |
| `IdTokenLifetimeMinutes` | 5 | id_tokens — read once at sign-in, so kept short; it used to reuse the access-token lifetime |
| `RefreshTokenLifetimeDays` | 14 | refresh tokens (only with `offline_access` and the `refresh_token` grant) |

Authorization codes live 5 minutes and are single-use.

## Login page and federation

`/connect/login` is always mapped. It shows:

- the password form, unless `LocalCredentials` is `Disabled` (the POST and `/connect/two-factor` are
  unmapped then);
- a button per interactive external scheme — the same list as `/spark/auth/capabilities` — so the
  provider can itself sign people in with GitHub, Google, another OIDC provider, …. Each is a plain link to
  `/spark/auth/external-login?provider=<scheme>&returnUrl=<pending /connect/authorize>&errorUrl=<this page>`
  (redirect mode). Success resumes the authorization; a refusal comes back here as
  `?sparkExternalLogin=<code>` and is shown as a fixed message (never the query text);
- "No sign-in method is configured" when there is neither.

## Logout

`GET /connect/logout` signs the user out, then honours `post_logout_redirect_uri` only when it is
registered (exact match) for the client that asks. The client is named by `client_id` **or** by
`id_token_hint` — what the stock handler sends. The hint must be an id_token this provider signed
(signature with its own key and issuer validated; an expired one is accepted, an access token is not); a
hint whose audience differs from `client_id` is refused. `state` is appended to the redirect.
