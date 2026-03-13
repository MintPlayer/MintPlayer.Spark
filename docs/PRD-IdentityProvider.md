# PRD: Spark Identity Provider (SparkId)

## Problem Statement

Currently, each Spark application (HR, Fleet) manages its own user database and authentication independently. There is no centralized identity provider, meaning:

- Users must create separate accounts for each application
- No single sign-on (SSO) across applications
- No standardized way to add social login (Google, Facebook, Microsoft, X, LinkedIn)
- No OIDC-compliant token issuance for third-party integrations

IdentityServer solves this for SQL-backed apps but requires ~20 database tables and a commercial license. Spark's RavenDB foundation enables a dramatically simpler data model.

## Solution Overview

Ship a **Spark Identity Provider** consisting of:

| Deliverable | Package | Purpose |
|---|---|---|
| **NuGet package** | `MintPlayer.Spark.IdentityProvider` | Self-contained OIDC server with RavenDB storage (no OpenIddict dependency) |
| **npm package** | `@mintplayer/ng-spark-auth` (enhanced) | `SPARK_OIDC_PROVIDERS` token + external login buttons on login page |
| **Demo app** | `Demo/SparkId/` | Reference identity provider with login/register, OIDC client management, and social login |

### Key Value Proposition

> **IdentityServer = 20 SQL tables. Spark IdentityProvider = 5 RavenDB collections.**

| SQL (IdentityServer) | RavenDB (Spark) | Notes |
|---|---|---|
| Users, UserClaims, UserRoles, UserLogins, UserTokens, Roles, RoleClaims | **SparkUsers** | Single document with embedded roles, claims, logins, tokens |
| Applications, ApplicationRedirectUris, ApplicationPostLogoutRedirectUris, ApplicationScopes, ApplicationProperties | **OidcApplications** | Single document with embedded URIs, scopes, properties |
| Authorizations, AuthorizationScopes | **OidcAuthorizations** | Authorization grants with embedded scope references |
| Tokens | **OidcTokens** | Access/refresh/ID tokens referencing authorizations |
| Scopes, ScopeResources, ScopeClaims | **OidcScopes** | Scope definitions with embedded claims and resources |

### Why No OpenIddict?

OpenIddict is a capable library, but adds a significant dependency tree and forces its abstractions on consumers. The OIDC Authorization Code + PKCE flow is a well-defined protocol. By implementing it ourselves:

1. **Zero external dependencies** - Only `Microsoft.IdentityModel.JsonWebTokens` (already part of ASP.NET Core)
2. **Full control** - No fighting framework conventions; our endpoints, our data model
3. **Simpler** - We only need Authorization Code + PKCE, not the full OAuth2 specification
4. **RavenDB-native** - Direct document storage, no adapter layer between OpenIddict models and RavenDB
5. **Consistency** - Same patterns as the existing `UserStore`/`RoleStore` implementation

---

## Architecture

### System Topology

```
┌────────────────────────────────────────────────────────────────┐
│                   SparkId (Identity Provider)                   │
│                   https://localhost:5001                         │
│                                                                 │
│  ┌────────────────┐  ┌──────────────┐  ┌────────────────────┐  │
│  │ OIDC Server     │  │ Spark Auth   │  │ Social Providers   │  │
│  │ (self-built)    │  │ (Identity)   │  │ via AddOidcLogin() │  │
│  │ /connect/*      │  │ /spark/auth  │  │ Google, FB, MS,    │  │
│  │                 │  │              │  │ X, LinkedIn         │  │
│  └───────┬─────────┘  └──────┬───────┘  └────────┬───────────┘  │
│          │                   │                    │              │
│          └──────────┬────────┴────────────────────┘              │
│                     │                                            │
│   ┌─────────────────┴──────────────────┐                        │
│   │   RavenDB (5 collections)          │                        │
│   │   SparkUsers, OidcApplications,    │                        │
│   │   OidcAuthorizations, OidcTokens,  │                        │
│   │   OidcScopes                       │                        │
│   └────────────────────────────────────┘                        │
│                                                                 │
│   Spark CRUD UI: manage OidcApplications + OidcScopes           │
│   MVC consent page: /connect/consent                            │
│   Login/Register: ng-spark-auth (same as HR/Fleet)              │
└────────────────────────────────────────────────────────────────┘
        ▲ OIDC                              ▲ OIDC
        │ Authorization Code + PKCE         │
   ┌────┴──────┐                      ┌─────┴─────┐
   │    HR     │                      │   Fleet   │
   │ :5005     │                      │  :5003    │
   │ AddOidc   │                      │ AddOidc   │
   │ Login()   │                      │ Login()   │
   └───────────┘                      └───────────┘
```

### OIDC Flow (Authorization Code + PKCE)

```
1. User clicks "Login with SparkId" on HR login page
2. HR Angular app → redirect to SparkId /connect/authorize
     ?client_id=hr-app
     &redirect_uri=https://localhost:5005/auth/callback
     &response_type=code
     &scope=openid profile email
     &code_challenge=...
     &code_challenge_method=S256
     &state=...
3. SparkId: user not logged in → show SparkId login page (ng-spark-auth)
4. User authenticates (local account OR social provider via AddOidcLogin)
5. SparkId: consent screen (MVC page, can auto-approve trusted clients)
6. SparkId → redirect to HR callback with authorization code
7. HR backend: POST SparkId /connect/token (exchanges code for tokens)
8. HR backend: validates ID token, creates local Identity session
9. HR Angular: redirected to original page, authenticated
```

### Social Login Reuse

The same `AddOidcLogin()` code serves two purposes:

```
HR  → AddOidcLogin("sparkid", ...)  → connects to SparkId
Fleet → AddOidcLogin("sparkid", ...) → connects to SparkId
SparkId → AddOidcLogin("google", ...)   → connects to Google
SparkId → AddOidcLogin("facebook", ...) → connects to Facebook
SparkId → AddOidcLogin("microsoft", ...)→ connects to Microsoft
SparkId → AddOidcLogin("x", ...)        → connects to X/Twitter
SparkId → AddOidcLogin("linkedin", ...) → connects to LinkedIn
```

One code path. No `Microsoft.AspNetCore.Authentication.Google` etc. needed.

---

## Package 1: MintPlayer.Spark.IdentityProvider (NuGet)

### Dependencies

```xml
<!-- No external OIDC framework. Only framework-included JWT support. -->
<FrameworkReference Include="Microsoft.AspNetCore.App" />
<ProjectReference Include="..\..\MintPlayer.Spark\MintPlayer.Spark.csproj" />
<ProjectReference Include="..\..\MintPlayer.Spark.Authorization\MintPlayer.Spark.Authorization.csproj" />
```

`Microsoft.IdentityModel.JsonWebTokens` is already included in the ASP.NET Core shared framework.

### 5 RavenDB Collections

#### 1. SparkUsers (existing)
No changes. Already stores users with embedded roles, claims, logins, tokens.

#### 2. OidcApplications (Spark PersistentObject)
Managed via Spark CRUD UI in SparkId. Users can create and manage OIDC clients through the standard entity management interface.

```csharp
public class OidcApplication : PersistentObject
{
    // Spark PersistentObject fields (Id, etc.) inherited

    public string ClientId { get; set; } = "";
    public string? ClientSecretHash { get; set; }       // BCrypt or SHA256 hashed
    public string DisplayName { get; set; } = "";
    public string ClientType { get; set; } = "confidential"; // "public" or "confidential"
    public string ConsentType { get; set; } = "explicit";    // "explicit" or "implicit"
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public List<string> AllowedScopes { get; set; } = [];    // references OidcScope names
    public bool RequirePkce { get; set; } = true;
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 14;
    public bool Enabled { get; set; } = true;
}
```

#### 3. OidcScopes (Spark PersistentObject)
Managed via Spark CRUD UI in SparkId.

```csharp
public class OidcScope : PersistentObject
{
    public string Name { get; set; } = "";           // e.g., "openid", "profile", "email"
    public string DisplayName { get; set; } = "";    // e.g., "Your identity"
    public string? Description { get; set; }         // e.g., "Access your user identifier"
    public List<string> ClaimTypes { get; set; } = []; // Claims included when scope is granted
    public bool Required { get; set; }               // Cannot be deselected on consent screen
}
```

#### 4. OidcAuthorizations
Internal entity (not managed via CRUD UI). Created automatically when user consents.

```csharp
public class OidcAuthorization
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = "";  // reference to OidcApplication
    public string Subject { get; set; } = "";        // SparkUser.Id
    public string Status { get; set; } = "valid";    // "valid", "revoked"
    public List<string> GrantedScopes { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
```

#### 5. OidcTokens
Internal entity. Stores authorization codes, access tokens, refresh tokens.

```csharp
public class OidcToken
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = "";
    public string AuthorizationId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Type { get; set; } = "";           // "authorization_code", "access_token", "refresh_token"
    public string? ReferenceId { get; set; }         // opaque token value for lookup
    public string? CodeChallenge { get; set; }       // PKCE: stored on authorization_code
    public string? CodeChallengeMethod { get; set; } // "S256"
    public string? RedirectUri { get; set; }         // stored on authorization_code for validation
    public List<string> Scopes { get; set; } = [];
    public string? Payload { get; set; }             // JWT for self-contained tokens
    public string Status { get; set; } = "valid";    // "valid", "redeemed", "revoked", "expired"
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public string? State { get; set; }               // OIDC state parameter passthrough
}
```

### RavenDB Indexes

```csharp
public class OidcApplications_ByClientId : AbstractIndexCreationTask<OidcApplication>
    // Fast lookup by ClientId for /connect/authorize and /connect/token

public class OidcTokens_ByReferenceId : AbstractIndexCreationTask<OidcToken>
    // Fast lookup for authorization code exchange and token validation

public class OidcTokens_ByExpiration : AbstractIndexCreationTask<OidcToken>
    // For expired token cleanup (background job)

public class OidcAuthorizations_BySubjectAndApplication : AbstractIndexCreationTask<OidcAuthorization>
    // Check existing consent for a user+app combination
```

### OIDC Server Implementation

All OIDC protocol logic is self-contained in this package. No external OIDC framework.

#### Signing Key Management

```csharp
public class OidcSigningKeyService
{
    // Development: auto-generates RSA key pair, persists to App_Data/oidc-signing-key.json
    // Production: loads from configuration (X509 certificate or PEM)
    public SecurityKey GetSigningKey();
    public JsonWebKey GetPublicJwk();
}
```

#### OIDC Endpoints

| Endpoint | Path | Method | Purpose |
|---|---|---|---|
| Discovery | `/.well-known/openid-configuration` | GET | OIDC discovery document |
| JWKS | `/.well-known/jwks` | GET | JSON Web Key Set (public signing keys) |
| Authorize | `/connect/authorize` | GET | Start authorization, show consent |
| Consent | `/connect/consent` | GET/POST | MVC consent page (Allow/Deny) |
| Token | `/connect/token` | POST | Exchange authorization code for tokens |
| Userinfo | `/connect/userinfo` | GET | Returns user claims for the access token |
| Logout | `/connect/logout` | GET | OIDC front-channel logout |

#### Discovery Document (`/.well-known/openid-configuration`)

```json
{
  "issuer": "https://localhost:5001",
  "authorization_endpoint": "https://localhost:5001/connect/authorize",
  "token_endpoint": "https://localhost:5001/connect/token",
  "userinfo_endpoint": "https://localhost:5001/connect/userinfo",
  "end_session_endpoint": "https://localhost:5001/connect/logout",
  "jwks_uri": "https://localhost:5001/.well-known/jwks",
  "response_types_supported": ["code"],
  "grant_types_supported": ["authorization_code", "refresh_token"],
  "subject_types_supported": ["public"],
  "id_token_signing_alg_values_supported": ["RS256"],
  "scopes_supported": ["openid", "profile", "email", "roles"],
  "code_challenge_methods_supported": ["S256"],
  "token_endpoint_auth_methods_supported": ["client_secret_post"]
}
```

#### Authorization Endpoint (`/connect/authorize`)

```csharp
// GET /connect/authorize
// Validates: client_id, redirect_uri, response_type=code, scope, code_challenge, code_challenge_method
// If user not authenticated → redirect to /login?returnUrl=/connect/authorize?...
// If no existing valid consent → redirect to /connect/consent?...
// If consent exists or auto-approved → generate authorization code, redirect to redirect_uri
```

#### Consent Page (`/connect/consent`) - MVC Razor Page

Server-rendered page, no SPA. Minimal dependencies.

```html
<!-- Razor Page: shows app name, requested scopes, Allow/Deny -->
<h2>@Model.ApplicationName wants to access your account</h2>
<p>This application is requesting the following permissions:</p>
<ul>
  @foreach (var scope in Model.RequestedScopes)
  {
    <li>
      <input type="checkbox" checked="@scope.Required" disabled="@scope.Required" />
      <strong>@scope.DisplayName</strong> — @scope.Description
    </li>
  }
</ul>
<form method="post">
  <button type="submit" name="decision" value="allow">Allow</button>
  <button type="submit" name="decision" value="deny">Deny</button>
</form>
```

#### Token Endpoint (`/connect/token`)

```csharp
// POST /connect/token
// Content-Type: application/x-www-form-urlencoded
//
// grant_type=authorization_code:
//   - Validates: client_id, client_secret, code, redirect_uri, code_verifier
//   - PKCE: SHA256(code_verifier) must match stored code_challenge
//   - Returns: { access_token (JWT), id_token (JWT), refresh_token, token_type, expires_in }
//
// grant_type=refresh_token:
//   - Validates: client_id, client_secret, refresh_token
//   - Returns: new access_token + id_token, optionally new refresh_token
```

#### JWT Generation

```csharp
public class OidcTokenGenerator
{
    // Generates signed JWTs using the signing key
    // ID Token claims: sub, iss, aud, exp, iat, nonce, email, name, roles (based on scopes)
    // Access Token claims: sub, iss, aud, exp, iat, scope, client_id
    public string GenerateIdToken(SparkUser user, OidcApplication app, IEnumerable<string> scopes);
    public string GenerateAccessToken(SparkUser user, OidcApplication app, IEnumerable<string> scopes);
    public string GenerateRefreshToken(); // opaque random string
}
```

#### Token Cleanup

```csharp
// Background hosted service
public class OidcTokenCleanupService : BackgroundService
{
    // Runs periodically (e.g., every hour)
    // Deletes expired/redeemed tokens from OidcTokens collection
    // Uses OidcTokens_ByExpiration index
}
```

### Extension Methods

```csharp
public static class SparkIdentityProviderExtensions
{
    /// <summary>
    /// Configures this Spark application as an OIDC Identity Provider.
    /// Registers OIDC endpoints, signing key service, token generator,
    /// and token cleanup background service.
    ///
    /// OidcApplications and OidcScopes are managed as Spark PersistentObjects
    /// through the standard CRUD UI.
    /// </summary>
    public static ISparkBuilder AddIdentityProvider(
        this ISparkBuilder builder,
        Action<SparkIdentityProviderOptions>? configure = null)
    {
        // 1. Register OidcSigningKeyService
        // 2. Register OidcTokenGenerator
        // 3. Register OidcTokenCleanupService (background)
        // 4. Map OIDC endpoints (discovery, jwks, authorize, consent, token, userinfo, logout)
        // 5. Add MVC/Razor Pages for consent page
        // 6. Seed default scopes (openid, profile, email, roles) if not present
    }
}

public class SparkIdentityProviderOptions
{
    /// <summary>
    /// Path to signing key file. Default: App_Data/oidc-signing-key.json
    /// Auto-generated in Development; must be provided in Production.
    /// </summary>
    public string SigningKeyPath { get; set; } = "App_Data/oidc-signing-key.json";

    /// <summary>
    /// Whether to auto-approve consent for clients with ConsentType = "implicit".
    /// </summary>
    public bool AutoApproveImplicitConsent { get; set; } = true;

    /// <summary>
    /// Token cleanup interval. Default: 1 hour.
    /// </summary>
    public TimeSpan TokenCleanupInterval { get; set; } = TimeSpan.FromHours(1);
}
```

---

## Package 2: AddOidcLogin() in MintPlayer.Spark.Authorization

The OIDC client functionality lives in `MintPlayer.Spark.Authorization` since it already has the auth infrastructure.

### AddOidcLogin() Extension

```csharp
public static class SparkBuilderOidcExtensions
{
    /// <summary>
    /// Adds an external OIDC login provider. The same method is used for:
    /// - HR/Fleet connecting to SparkId
    /// - SparkId connecting to Google, Facebook, Microsoft, X, LinkedIn
    ///
    /// Usage:
    ///   spark.AddOidcLogin("sparkid", opts => {
    ///       opts.Authority = "https://localhost:5001";
    ///       opts.ClientId = "hr-app";
    ///       opts.ClientSecret = "hr-dev-secret";
    ///   });
    /// </summary>
    public static ISparkBuilder AddOidcLogin(
        this ISparkBuilder builder,
        string scheme,
        Action<SparkOidcLoginOptions> configure)
    {
        // 1. Fetch discovery document from Authority/.well-known/openid-configuration
        //    (or use manually configured endpoints for non-OIDC providers)
        // 2. Register the scheme in ASP.NET Core authentication
        // 3. Register callback endpoint: /spark/auth/oidc-callback/{scheme}
        // 4. Register provider listing endpoint: GET /spark/auth/external-providers
    }
}

public class SparkOidcLoginOptions
{
    /// <summary>
    /// OIDC Authority URL. Discovery document fetched from {Authority}/.well-known/openid-configuration.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Manual endpoint configuration (for providers without standard OIDC discovery).
    /// If Authority is set, these are auto-populated from the discovery document.
    /// </summary>
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? UserInfoEndpoint { get; set; }

    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];
    public string DisplayName { get; set; } = "";
    public string? Icon { get; set; }              // Bootstrap icon name for UI

    /// <summary>
    /// Claim type mappings. Maps external provider claim types to local claim types.
    /// Default: standard OIDC claim types (sub, email, name, etc.)
    /// </summary>
    public Dictionary<string, string> ClaimMappings { get; set; } = new();
}
```

### OIDC Client Endpoints

```
GET  /spark/auth/external-providers
     → Returns configured external login providers
     → [{ "scheme": "sparkid", "displayName": "SparkId", "icon": "shield-lock" }, ...]

GET  /spark/auth/external-login/{scheme}?returnUrl=...
     → Generates PKCE challenge, stores state
     → Redirects to external provider's authorization endpoint

GET  /spark/auth/oidc-callback/{scheme}
     → Receives authorization code from external provider
     → Backend exchanges code for tokens (backend-initiated, secret never in browser)
     → Extracts user claims from ID token
     → Creates or links local SparkUser account
     → Establishes local Identity session
     → Redirects to returnUrl
```

### OIDC Client Flow (Backend-Initiated)

```
1. Angular app calls: GET /spark/auth/external-login/sparkid?returnUrl=/dashboard
2. Backend generates PKCE code_verifier + code_challenge
3. Backend stores code_verifier + returnUrl in encrypted cookie (or session)
4. Backend redirects to external provider's /connect/authorize with code_challenge
5. User authenticates at external provider
6. External provider redirects to /spark/auth/oidc-callback/sparkid?code=...&state=...
7. Backend retrieves code_verifier from cookie
8. Backend POSTs to external provider's /connect/token with code + code_verifier
9. Backend validates ID token, extracts claims
10. Backend finds or creates local SparkUser (via IUserLoginStore)
11. Backend signs in user (Identity cookie)
12. Backend redirects to original returnUrl
```

---

## Package 3: ng-spark-auth Enhancement (npm)

### New: SPARK_OIDC_PROVIDERS Injection Token

```typescript
// models/oidc-provider.ts
export interface SparkOidcProvider {
  /** Unique scheme name, e.g. "sparkid" */
  scheme: string;
  /** Display name shown on button, e.g. "SparkId" */
  displayName: string;
  /** Bootstrap icon name, e.g. "shield-lock" */
  icon?: string;
  /** Color class for the button, e.g. "primary", "dark" */
  buttonClass?: string;
}

export const SPARK_OIDC_PROVIDERS = new InjectionToken<SparkOidcProvider[]>('SPARK_OIDC_PROVIDERS');
```

### New: provideSparkOidcLogin()

```typescript
// providers/provide-spark-oidc.ts

export interface SparkOidcLoginConfig {
  /** Scheme name matching backend AddOidcLogin() scheme */
  scheme: string;
  /** Display name on button */
  displayName: string;
  /** Bootstrap icon name */
  icon?: string;
  /** Button color class */
  buttonClass?: string;
}

/**
 * Registers an OIDC login provider button on the Spark login page.
 * The actual OIDC flow is backend-initiated — clicking the button
 * navigates to /spark/auth/external-login/{scheme}.
 *
 * Usage:
 *   provideSparkOidcLogin({
 *     scheme: 'sparkid',
 *     displayName: 'SparkId',
 *     icon: 'shield-lock',
 *   })
 */
export function provideSparkOidcLogin(
  config: SparkOidcLoginConfig
): EnvironmentProviders { ... }
```

Since the flow is backend-initiated, the Angular side is simple:
1. Register provider button via `provideSparkOidcLogin()`
2. Clicking the button navigates to `/spark/auth/external-login/{scheme}?returnUrl=...`
3. Everything else happens server-side
4. User returns to the Angular app already authenticated

No `SparkAuthCallbackComponent` needed on the Angular side — the backend handles the entire callback and redirects the user back to the returnUrl with a session cookie already set.

### Login Component Enhancement

```html
<!-- After the local login form -->
@if (oidcProviders().length > 0) {
  <hr class="my-3">
  <div class="text-center mb-2">
    <small class="text-muted">{{ 'authOrLoginWith' | t }}</small>
  </div>
  @for (provider of oidcProviders(); track provider.scheme) {
    <a
      class="btn btn-outline-{{ provider.buttonClass ?? 'secondary' }} w-100 mb-2"
      [href]="getExternalLoginUrl(provider)"
    >
      @if (provider.icon) {
        <i class="bi bi-{{ provider.icon }} me-2"></i>
      }
      {{ provider.displayName }}
    </a>
  }
}
```

The button is a plain `<a>` link to `/spark/auth/external-login/{scheme}?returnUrl=...` — a full page navigation, not an API call.

### Updated Public API Exports

```typescript
// New exports in public-api.ts
export type { SparkOidcProvider, SparkOidcLoginConfig } from './lib/models/oidc-provider';
export { SPARK_OIDC_PROVIDERS } from './lib/models/oidc-provider';
export { provideSparkOidcLogin } from './lib/providers/provide-spark-oidc';
```

---

## Demo App: SparkId (Identity Provider)

### Project Structure

```
Demo/SparkId/
├── SparkId/
│   ├── SparkId.csproj
│   ├── Program.cs
│   ├── SparkIdContext.cs
│   ├── Entities/
│   │   ├── OidcApplication.cs        (Spark PersistentObject)
│   │   └── OidcScope.cs              (Spark PersistentObject)
│   ├── Actions/
│   │   ├── OidcApplicationActions.cs
│   │   └── OidcScopeActions.cs
│   ├── Pages/
│   │   └── Consent.cshtml            (MVC Razor Page for OIDC consent)
│   ├── Properties/
│   │   └── launchSettings.json        (https://localhost:5001)
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   └── ClientApp/
│       ├── package.json               (@spark-demo/sparkid)
│       ├── src/
│       │   └── app/
│       │       ├── app.config.ts      (provideSparkAuth + provideSparkOidcLogin for social)
│       │       ├── app.routes.ts      (sparkAuthRoutes + sparkRoutes)
│       │       └── app.ts
│       └── ...
├── SparkId.Library/
│   ├── SparkId.Library.csproj
│   └── Entities/
│       ├── OidcApplication.cs
│       └── OidcScope.cs
```

### Backend: Program.cs

```csharp
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddRazorPages();  // For consent page

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<SparkIdContext>();
    spark.AddActions();

    spark.AddAuthorization();
    spark.AddAuthentication<SparkUser>();

    // Social login providers — same AddOidcLogin() that HR/Fleet use for SparkId
    spark.AddOidcLogin("google", opts =>
    {
        opts.Authority = "https://accounts.google.com";
        opts.ClientId = builder.Configuration["Authentication:Google:ClientId"]!;
        opts.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
        opts.DisplayName = "Google";
        opts.Icon = "google";
    });
    spark.AddOidcLogin("facebook", opts =>
    {
        opts.AuthorizationEndpoint = "https://www.facebook.com/v18.0/dialog/oauth";
        opts.TokenEndpoint = "https://graph.facebook.com/v18.0/oauth/access_token";
        opts.UserInfoEndpoint = "https://graph.facebook.com/me?fields=id,name,email";
        opts.ClientId = builder.Configuration["Authentication:Facebook:ClientId"]!;
        opts.ClientSecret = builder.Configuration["Authentication:Facebook:ClientSecret"];
        opts.DisplayName = "Facebook";
        opts.Icon = "facebook";
    });
    spark.AddOidcLogin("microsoft", opts =>
    {
        opts.Authority = "https://login.microsoftonline.com/common/v2.0";
        opts.ClientId = builder.Configuration["Authentication:Microsoft:ClientId"]!;
        opts.ClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
        opts.DisplayName = "Microsoft";
        opts.Icon = "microsoft";
    });
    spark.AddOidcLogin("x", opts =>
    {
        opts.AuthorizationEndpoint = "https://twitter.com/i/oauth2/authorize";
        opts.TokenEndpoint = "https://api.twitter.com/2/oauth2/token";
        opts.UserInfoEndpoint = "https://api.twitter.com/2/users/me";
        opts.ClientId = builder.Configuration["Authentication:X:ClientId"]!;
        opts.ClientSecret = builder.Configuration["Authentication:X:ClientSecret"];
        opts.DisplayName = "X";
        opts.Icon = "twitter-x";
        opts.Scopes = ["tweet.read", "users.read"];
    });
    spark.AddOidcLogin("linkedin", opts =>
    {
        opts.AuthorizationEndpoint = "https://www.linkedin.com/oauth/v2/authorization";
        opts.TokenEndpoint = "https://www.linkedin.com/oauth/v2/accessToken";
        opts.UserInfoEndpoint = "https://api.linkedin.com/v2/userinfo";
        opts.ClientId = builder.Configuration["Authentication:LinkedIn:ClientId"]!;
        opts.ClientSecret = builder.Configuration["Authentication:LinkedIn:ClientSecret"];
        opts.DisplayName = "LinkedIn";
        opts.Icon = "linkedin";
    });

    // One line to become an OIDC Identity Provider
    spark.AddIdentityProvider();
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.SparkId";
});

// ... standard Spark SPA setup
```

### Angular Side: SparkId Frontend

The SparkId app's Angular frontend provides:
1. **Login/Register** pages via ng-spark-auth (same as HR/Fleet)
2. **OIDC client management** via Spark CRUD UI (OidcApplications entity)
3. **Scope management** via Spark CRUD UI (OidcScopes entity)
4. **Social login buttons** via `provideSparkOidcLogin()` (Google, Facebook, etc.)

```typescript
// app.config.ts
import { provideSparkAuth, withSparkAuth, provideSparkOidcLogin } from '@mintplayer/ng-spark-auth';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth()),
    provideAnimations(),
    provideSparkAuth(),
    // Social login buttons on the SparkId login page
    provideSparkOidcLogin({ scheme: 'google', displayName: 'Google', icon: 'google' }),
    provideSparkOidcLogin({ scheme: 'facebook', displayName: 'Facebook', icon: 'facebook' }),
    provideSparkOidcLogin({ scheme: 'microsoft', displayName: 'Microsoft', icon: 'microsoft' }),
    provideSparkOidcLogin({ scheme: 'x', displayName: 'X', icon: 'twitter-x' }),
    provideSparkOidcLogin({ scheme: 'linkedin', displayName: 'LinkedIn', icon: 'linkedin' }),
    provideZonelessChangeDetection()
  ]
};
```

The consent page is NOT part of the Angular SPA — it's a server-rendered MVC Razor page at `/connect/consent`.

---

## HR & Fleet Integration

### HR Program.cs Changes

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<HRContext>();
    spark.AddActions();
    spark.AddAuthorization();
    spark.AddAuthentication<SparkUser>();

    // One line to add SparkId as login provider
    spark.AddOidcLogin("sparkid", opts =>
    {
        opts.Authority = "https://localhost:5001";
        opts.ClientId = "hr-app";
        opts.ClientSecret = builder.Configuration["SparkId:ClientSecret"];
        opts.DisplayName = "SparkId";
        opts.Icon = "shield-lock";
    });
});
```

### HR Angular Changes

```typescript
// app.config.ts
import { provideSparkOidcLogin } from '@mintplayer/ng-spark-auth';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth()),
    provideAnimations(),
    provideSparkAuth(),
    provideSparkOidcLogin({
      scheme: 'sparkid',
      displayName: 'SparkId',
      icon: 'shield-lock',
      buttonClass: 'primary',
    }),
    provideZonelessChangeDetection()
  ]
};
```

Fleet gets the identical treatment.

---

## Translation Keys

New translation keys for ng-spark-auth:

```typescript
'authOrLoginWith'          // "or login with"
'authCallbackProcessing'   // "Processing login..."
```

Consent page translations are in the MVC Razor page (server-side), not in ng-spark-auth.

---

## Implementation Phases

### Phase 1: OIDC Client (`AddOidcLogin`) in MintPlayer.Spark.Authorization
1. `SparkOidcLoginOptions` model
2. `AddOidcLogin()` extension method on `ISparkBuilder`
3. Discovery document fetching (for OIDC-compliant providers)
4. PKCE generation (code_verifier + code_challenge)
5. External login endpoints: `/spark/auth/external-providers`, `/spark/auth/external-login/{scheme}`, `/spark/auth/oidc-callback/{scheme}`
6. Backend code exchange: POST to provider's token endpoint with code + code_verifier
7. ID token validation, claim extraction
8. User creation/linking via `IUserLoginStore`
9. Session establishment and redirect

### Phase 2: ng-spark-auth OIDC Enhancement
10. `SPARK_OIDC_PROVIDERS` injection token + `SparkOidcProvider` model
11. `provideSparkOidcLogin()` provider function
12. Update `SparkLoginComponent` to render OIDC provider buttons
13. Update `sparkAuthRoutes()` (no callback route needed — backend handles it)
14. New translation keys

### Phase 3: SparkId Demo App - Project Setup
15. Create `Demo/SparkId/` project structure (.NET + Angular)
16. `SparkId.Library` with `OidcApplication` and `OidcScope` entities
17. `SparkIdContext` with collections
18. Actions classes for OidcApplication and OidcScope
19. Add to solution file, npm workspaces, tsconfig paths
20. Wire up login/register (same as HR/Fleet) + social login via `AddOidcLogin()`

### Phase 4: OIDC Server (`AddIdentityProvider`) in MintPlayer.Spark.IdentityProvider
21. Create `MintPlayer.Spark.IdentityProvider` project
22. `OidcSigningKeyService` (key generation, storage, loading)
23. `OidcTokenGenerator` (JWT creation: ID tokens, access tokens)
24. Discovery endpoint (`/.well-known/openid-configuration`)
25. JWKS endpoint (`/.well-known/jwks`)
26. Authorization endpoint (`/connect/authorize`)
27. Token endpoint (`/connect/token`) — authorization code + refresh token grants
28. Userinfo endpoint (`/connect/userinfo`)
29. Logout endpoint (`/connect/logout`)
30. `OidcAuthorization` and `OidcToken` document models + indexes
31. `OidcTokenCleanupService` (background)
32. `AddIdentityProvider()` extension on `ISparkBuilder`

### Phase 5: Consent Page (MVC)
33. MVC Razor Page at `/connect/consent`
34. Consent model: app name, requested scopes, allow/deny
35. Creates `OidcAuthorization` on approval
36. Generates authorization code and redirects

### Phase 6: SparkId + HR + Fleet Integration Testing
37. Configure SparkId as identity provider in Program.cs
38. Register HR and Fleet as OIDC clients (via Spark CRUD UI or seed data)
39. Seed default scopes (openid, profile, email, roles)
40. Wire up HR and Fleet with `AddOidcLogin("sparkid", ...)` + `provideSparkOidcLogin()`
41. End-to-end flow testing: HR → SparkId → consent → back to HR

---

## Port Allocation

| App | HTTPS | HTTP |
|---|---|---|
| SparkId (new) | `https://localhost:5001` | `http://localhost:5002` |
| Fleet | `https://localhost:5003` | `http://localhost:5004` |
| HR | `https://localhost:5005` | `http://localhost:5006` |
| DemoApp | `https://localhost:5007` | `http://localhost:5008` |

---

## Non-Goals (Explicit)

- **Device authorization flow** - Not needed for web apps
- **Client credentials flow** - Focus on interactive login only
- **Multi-tenancy** - Single identity provider instance per deployment
- **Token introspection** - Can be added later
- **SAML** - OIDC only

---

## Security Considerations

1. **PKCE required** for all clients (both public SPAs and confidential backends)
2. **Client secrets** stored hashed (BCrypt) in RavenDB
3. **Signing keys**: auto-generated RSA in development; X509 certificate in production
4. **Token lifetimes**: Access tokens 1 hour, refresh tokens 14 days (configurable per client)
5. **Consent**: Per-client configurable — `implicit` (auto-approve) or `explicit` (show consent page)
6. **CORS**: SparkId allows origins matching registered client redirect URIs
7. **Token cleanup**: Background service prunes expired/redeemed tokens hourly
8. **Backend-initiated code exchange**: Client secrets and PKCE verifiers never exposed to browser
9. **Authorization codes**: Single-use, short-lived (5 minutes), bound to redirect_uri
10. **State parameter**: CSRF protection on the authorization flow

---

## Resolved Questions

| # | Question | Decision | Rationale |
|---|---|---|---|
| 1 | OpenIddict dependency? | **No — build ourselves** | Zero dependencies, full control, simpler for the supported flow |
| 2 | `AddOidcLogin()` location? | **MintPlayer.Spark.Authorization** | Already has auth infrastructure |
| 3 | Consent page rendering? | **MVC Razor Page** | No SPA complexity needed for a simple form |
| 4 | Code exchange model? | **Backend-initiated** | Client secret never in browser |
| 5 | Social login packages? | **Reuse `AddOidcLogin()`** | Same code path for SparkId→Google and HR→SparkId |
| 6 | Scopes storage? | **RavenDB collection** | 5 collections total; managed via Spark CRUD UI |
| 7 | OIDC client management? | **Spark PersistentObject CRUD** | Standard entity management in SparkId app |
