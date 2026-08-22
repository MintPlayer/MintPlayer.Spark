# MintPlayer.Spark.Authorization

Identity for MintPlayer.Spark: ASP.NET Core Identity backed by RavenDB, external login providers,
and automatic Angular frontend integration.

## Overview

**This package no longer owns authorization.** As of preview.62, `App_Data/security.json` is read
by Spark core, every application has one, and a missing or malformed file refuses startup. See
**[the authorization guide](../../../docs/guide-authorization.md)** for the rights model, group
semantics, precedence, and the `--spark-init-security` starter.

What moved into core: `SecurityConfiguration`, `Right`, `ISecurityConfigurationLoader`, the
evaluator, the validator, the claims-based group provider, the posture reporter and
`[SparkAuthorize]`. `spark.AddAuthorization()`, `AuthorizationOptions` (including
`DefaultBehavior`) and `spark.AllowAnonymousAccess()` are **deleted**, not deprecated. An
application that wants to be open grants `*/*` in its file, where the decision is visible.

What this package still gives you:

- **Identity** — `SparkUser`, `SparkRole`, RavenDB user/role stores, the `/spark/auth/*`
  endpoint family, and how much of it to mount (`SparkLocalCredentials`)
- **External login** — GitHub and any other OAuth/OIDC provider
- **JWT bearer** — for machine callers
- **The Angular half** — `@mintplayer/ng-spark-auth`, installed and scaffolded by MSBuild

Custom group membership is a core concern now: use `spark.UseGroupMembershipProvider<T>()` from
`MintPlayer.Spark.Extensions`, with or without this package.

## Installation

```bash
dotnet add package MintPlayer.Spark.Authorization
```

## Backend Setup

### Add Authentication

The authorization package includes built-in ASP.NET Core Identity support with RavenDB-backed user and role stores. To enable authentication:

```csharp
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddActions();
    spark.AddAuthentication<SparkUser>();
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.MyApp";
});
```

And in the middleware pipeline:

```csharp
var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSpark();      // authentication, authorization, antiforgery and the XSRF-TOKEN cookie

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();   // Spark's endpoints, including /spark/auth/* from AddAuthentication
});
```

`UseSpark()` owns the whole pipeline — it calls `UseAuthentication()` / `UseAuthorization()` in the
right order and wires antiforgery. There is no `UseSparkAntiforgery()`; see below.

#### Identity Endpoints

`AddAuthentication<TUser>()` registers the identity endpoints itself, so you never map them by
hand. They live under `/spark/auth/`:

| Endpoint | Method | Description |
|---|---|---|
| `/spark/auth/register` | POST | Register a new user |
| `/spark/auth/login` | POST | Log in (returns auth cookie) |
| `/spark/auth/logout` | POST | Log out (requires XSRF token) |
| `/spark/auth/me` | GET | Get current user info |
| `/spark/auth/refresh` | POST | Refresh authentication token |
| `/spark/auth/forgotPassword` | POST | Start password reset flow |
| `/spark/auth/resetPassword` | POST | Complete password reset |
| `/spark/auth/manage/2fa` | POST | Configure two-factor authentication |
| `/spark/auth/manage/info` | GET/POST | Get or update user profile |
| `/spark/auth/csrf-refresh` | POST | Get a fresh CSRF token |

#### Custom Group Membership Provider

By default, Spark resolves user groups from ASP.NET Core Identity roles. To integrate with a different authentication system, implement `IGroupMembershipProvider`:

```csharp
public class MyGroupProvider : IGroupMembershipProvider
{
    public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        // Return the group names the current user belongs to
        // These names are matched against group translations in security.json
        return Task.FromResult<IEnumerable<string>>(["Administrators"]);
    }
}
```

Register it:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseGroupMembershipProvider<MyGroupProvider>();
});
```

`UseGroupMembershipProvider` lives in `MintPlayer.Spark.Extensions` — it is a core concern, so an
application can say where groups come from without depending on this package.

⚠️ A provider cannot hand a caller `anonymous` or `authenticated`. Those are decided from
authentication state and their ids are excluded from claim-derived membership, so returning
"Signed-in users" resolves nothing.

`UseGroupMembershipProvider` removes the default registration rather than adding a second one, so
which provider runs does not depend on registration order.

### XSRF/Antiforgery Protection

When using cookie-based authentication, mutation endpoints (POST, PUT, DELETE) are protected with
XSRF tokens. **You do not wire this up** — `UseSpark()` does all of it: it generates the
`XSRF-TOKEN` cookie on every response *and* validates the `X-XSRF-TOKEN` header on incoming
mutations. The Angular frontend reads the cookie and echoes it back in the header
(the double-submit pattern), which Angular's `HttpClient` does by default.

There is no `UseSparkAntiforgery()` method, and never was — do not call `UseAuthentication()`,
`UseAuthorization()` or `UseAntiforgery()` yourself either. `UseSpark()` orders all four, and
adding your own copy changes that order.

Antiforgery applies only to **ambient** credentials — a cookie, which a browser attaches to a
cross-site request whether or not the user meant to. A caller presenting a bearer token or a
client certificate is exempt, because a token that must be attached deliberately cannot be
attached by an attacker's page, and demanding a cookie-derived header of a CI job that has no
cookie would only make legitimate calls impossible. See
[Authentication Schemes](../../../docs/guide-authentication-schemes.md) for the full rule.

## How Authorization Integrates with Spark

Spark core's `PermissionService` delegates every check to `IAccessControl`, which `AddSpark`
registers unconditionally as the `security.json` evaluator. There is no state in which
authorization is absent, and therefore no default to choose:

```csharp
// From MintPlayer.Spark/Services/PermissionService.cs
public async Task EnsureAuthorizedAsync(string action, string target, ...)
{
    var resource = $"{action}/{target}";
    if (!await DecideAsync(resource, cancellationToken))
        throw new SparkAccessDeniedException(resource);
}
```

Two earlier shapes are gone. The original returned early when no `IAccessControl` was registered,
so a missing package silently opened every endpoint. Its replacement — a deny-all default plus
`AddAuthorization()` / `AllowAnonymousAccess()` opt-ins — was safe but still left "nobody wired
it up" as a state with a made-up meaning. Now the file decides, and an open application says so
by granting `*/*`.

## Angular Frontend Setup

### Automatic npm Package Installation

When you reference `MintPlayer.Spark.Authorization` (via NuGet), the package includes MSBuild targets that automatically:

1. **Install `@mintplayer/ng-spark-auth`** via npm on first build (if your project has a `package.json` in the SPA root)
2. **Generate `spark-auth.setup.ts`** - a TypeScript scaffolding file with documented auth helpers

Both happen automatically during `dotnet build`. No manual npm install needed.

### Wire Up Your Angular App

After the first build, a `spark-auth.setup.ts` file appears in your SPA's `src/` directory. Use it to wire up authentication:

**app.config.ts:**

```typescript
import { ApplicationConfig } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { setupSparkAuthProviders, setupSparkAuthHttp } from './spark-auth.setup';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(setupSparkAuthHttp()),
    ...setupSparkAuthProviders(),
  ]
};
```

**app.routes.ts:**

```typescript
import { Routes } from '@angular/router';
import { setupSparkAuthRoutes, sparkAuthGuard } from './spark-auth.setup';

export const routes: Routes = [
  {
    path: '',
    children: [
      ...setupSparkAuthRoutes(),
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      { path: 'protected', loadComponent: () => import('./pages/protected/protected.component'), canActivate: [sparkAuthGuard] },
    ]
  }
];
```

The generated setup file includes the following helpers:

| Export | Description |
|--------|-------------|
| `setupSparkAuthProviders(config?)` | Returns providers array for `app.config.ts` |
| `setupSparkAuthHttp()` | Returns `HttpFeature` with auth interceptor (handles 401 redirects) |
| `setupSparkAuthRoutes(config?)` | Returns route array with login, register, forgot-password, reset-password pages |
| `sparkAuthGuard` | Route guard that redirects unauthenticated users to login |
| `SparkAuthBarComponent` | Auth bar component (`<spark-auth-bar>`) for login/logout UI |
| `SparkAuthService` | Injectable service with `login()`, `register()`, `logout()`, `loginWithProvider()`, `user` signal, etc. |

### External Login (GitHub, Google, …)

Once a provider is registered server-side (Step 3), sign-in is one call — `SparkAuthService`
owns the whole handshake:

```typescript
const result = await this.authService.loginWithProvider('GitHub', { returnUrl: '/projects' });
if (result.success) this.router.navigate(['/projects']);
```

It defaults to a popup and resolves once the flow ends, whichever way it ends. Pass
`{ mode: 'redirect' }` for a full-page navigation instead; that promise never settles,
because the outcome arrives as the next page load rather than as a value.

On failure `result.error` is one of `no_login_info` (the user cancelled at the provider),
`email_not_verified` (the provider did not attest the address, so no account was created),
`account_creation_failed`, `popup_blocked` or `popup_closed`. The codes are deliberately
coarse: they never distinguish "no such account" from anything else.

Do not hand-roll `window.open` plus a `message` listener. The popup can end in four ways —
success, a server-side refusal, a blocked window, and a user who simply closes it — and a
listener that is only removed on success leaks on the other three.

### Customizing the Generated File

The `spark-auth.setup.ts` file is generated **once** and never overwritten. You can freely customize it - for example, to change default configuration:

```typescript
export function setupSparkAuthProviders(config?: Partial<SparkAuthConfig>) {
  return [provideSparkAuth({
    apiBasePath: '/spark/auth',
    defaultRedirectUrl: '/dashboard',
    loginUrl: '/sign-in',
    ...config,
  })];
}
```

### Importing Directly

You can also skip the generated file and import directly from the npm package:

```typescript
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark-auth';
import {
  sparkAuthRoutes, withLocalLogin, withRegistration, withExternalLogin, githubProvider,
} from '@mintplayer/ng-spark-auth/routes';
```

The root entry point carries the bootstrap API only; everything else lives on a sub-path (`/routes`,
`/core`, `/models`, `/guards`, …).

Pages are **opted into individually** — `sparkAuthRoutes()` with no features mounts nothing:

```typescript
...sparkAuthRoutes(withLocalLogin(), withRegistration()),
...sparkAuthRoutes(withExternalLogin(githubProvider())),
```

Match them to the server's `SparkLocalCredentials`, which defaults to `Disabled`.

## MSBuild Properties

Customize the build targets by setting these properties in your `.csproj`:

| Property | Default | Description |
|----------|---------|-------------|
| `EnableSparkAuthSpa` | `true` | Master switch for all SPA-related targets |
| `GenerateSparkAuthSetupFile` | `true` | Set to `false` to skip generating the TypeScript setup file |
| `SpaRoot` | `ClientApp\` | Path to the SPA source directory |
| `SparkAuthSetupFile` | `$(SpaRoot)src\spark-auth.setup.ts` | Path for the generated TypeScript file |
| `SparkAuthNpmPackage` | `@mintplayer/ng-spark-auth` | npm package to install |

Example - disable automatic frontend setup:

```xml
<PropertyGroup>
    <EnableSparkAuthSpa>false</EnableSparkAuthSpa>
</PropertyGroup>
```

## Local Development (ProjectReference)

When referencing the Authorization project directly (instead of via NuGet), add explicit imports to your `.csproj` since `buildTransitive` only applies to NuGet package references:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

    <Import Project="..\path\to\MintPlayer.Spark.Authorization\Targets\spark-authorization.props" />

    <!-- ... your project content ... -->

    <Import Project="..\path\to\MintPlayer.Spark.Authorization\Targets\spark-authorization.targets" />

</Project>
```

## Example: Role-Based Access Control

A typical setup with three roles:

| Group | Companies | Cars | People |
|---|---|---|---|
| Administrators | Full CRUD | Full CRUD | Full CRUD |
| Managers | Read | Create/Edit (no delete) | Create/Edit (no delete) |
| Viewers | Read | Read | Read |
| Anonymous visitors | Read | -- | -- |

The corresponding `security.json`:

```json
{
  "wellKnown": {
    "anonymous": "00000000-0000-0000-0000-000000000000",
    "authenticated": "a1b2c3d4-0000-0000-0000-00000000000f"
  },
  "groups": {
    "00000000-0000-0000-0000-000000000000": { "en": "Anonymous visitors" },
    "a1b2c3d4-0000-0000-0000-00000000000f": { "en": "Signed-in users" },
    "a1b2c3d4-0000-0000-0000-000000000001": { "en": "Administrators" },
    "a1b2c3d4-0000-0000-0000-000000000002": { "en": "Managers" },
    "a1b2c3d4-0000-0000-0000-000000000003": { "en": "Viewers" }
  },
  "rights": [
    { "id": "...", "resource": "QueryRead/Company", "groupId": "00000000-0000-0000-0000-000000000000", "isDenied": false },

    { "id": "...", "resource": "QueryReadEditNewDelete/Company", "groupId": "a1b2c3d4-0000-0000-0000-000000000001", "isDenied": false },
    { "id": "...", "resource": "QueryReadEditNewDelete/Car", "groupId": "a1b2c3d4-0000-0000-0000-000000000001", "isDenied": false },
    { "id": "...", "resource": "QueryReadEditNewDelete/Person", "groupId": "a1b2c3d4-0000-0000-0000-000000000001", "isDenied": false },

    { "id": "...", "resource": "QueryReadEditNew/Car", "groupId": "a1b2c3d4-0000-0000-0000-000000000002", "isDenied": false },
    { "id": "...", "resource": "QueryReadEditNew/Person", "groupId": "a1b2c3d4-0000-0000-0000-000000000002", "isDenied": false },
    { "id": "...", "resource": "QueryRead/Company", "groupId": "a1b2c3d4-0000-0000-0000-000000000002", "isDenied": false },

    { "id": "...", "resource": "QueryRead/Car", "groupId": "a1b2c3d4-0000-0000-0000-000000000003", "isDenied": false },
    { "id": "...", "resource": "QueryRead/Person", "groupId": "a1b2c3d4-0000-0000-0000-000000000003", "isDenied": false },
    { "id": "...", "resource": "QueryRead/Company", "groupId": "a1b2c3d4-0000-0000-0000-000000000003", "isDenied": false }
  ]
}
```

⚠️ The anonymous grant is the ONLY one an unauthenticated visitor gets — `anonymous` is not a
floor under the other groups. A Manager who is also meant to read Companies needs their own
grant, which is why one appears on every role above.

## Complete Example

See the demo apps for working authorization setups:
- `../Demo/WebhooksDemo/WebhooksDemo/Program.cs` -- `spark.AddAuthentication<SparkUser>(…)` with an external provider, then `UseSpark()` / `MapSpark()`
- `../Demo/Fleet/Fleet/Program.cs` -- the same thing through `AddSparkFull` / `UseSparkFull`, which bundle the common packages
- `../Demo/Fleet/Fleet/App_Data/security.json` -- role-based permissions including custom action permissions
- `../Demo/HR/HR/App_Data/security.json` -- role-based permissions for HR entities
- `../Demo/DemoApp/DemoApp/App_Data/security.json` -- a fully public app, and the `Query`-without-`Read` showcase
- `../../spark/MintPlayer.Spark/Services/SecurityFileAccessControl.cs` -- permission evaluation, in core

## Requirements

- .NET 10.0+
- RavenDB 6.2+
- Node.js (for automatic npm integration)
- Angular 22+ (for frontend components)

## License

MIT License
