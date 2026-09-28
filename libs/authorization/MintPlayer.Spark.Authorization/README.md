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

| Endpoint | Method | Modes | Description |
|---|---|---|---|
| `/spark/auth/register` | POST | Full | Register; mails a confirmation link |
| `/spark/auth/resendConfirmationEmail` | POST | Full | Re-send the confirmation link (unconfirmed accounts only) |
| `/spark/auth/login` | POST | Full, SignInOnly | Log in with **email or user name** (cookie with `?useCookies=true`, else bearer) |
| `/spark/auth/refresh` | POST | Full, SignInOnly | Refresh a bearer token |
| `/spark/auth/forgotPassword` | POST | Full, SignInOnly | Mail a reset link — also to an **unconfirmed** address |
| `/spark/auth/resetPassword` | POST | Full, SignInOnly | Complete a reset; **confirms the email** |
| `/spark/auth/confirmEmail` | GET | all | Legacy mailbox link (plain-text answer) |
| `/spark/auth/confirm-email` | POST | all | `{ userId, code, changedEmail? }` — what the SPA's confirm page posts |
| `/spark/auth/manage/info` | GET / POST | all / Full, SignInOnly | Read email + confirmed; change password (`oldPassword`) or email (link to the new address) |
| `/spark/auth/manage/password` | POST | Full, SignInOnly | `{ currentPassword?, newPassword }` — set a first password or change it |
| `/spark/auth/manage/profile` | GET / POST | all | User name, `preferredCulture` (mail language) + app fields (`ISparkProfileContributor<TUser>`) |
| `/spark/auth/manage/2fa` | POST | all | Microsoft's 2FA management (creates the authenticator key) |
| `/spark/auth/manage/2fa/authenticator-uri` | GET | all | `{ sharedKey, authenticatorUri, qrCodeSvg }`, `Cache-Control: no-store` |
| `/spark/auth/manage/personal-data` | GET | all | GDPR export: the account + `ISparkPersonalDataContributor<TUser>` sections |
| `/spark/auth/manage/account` | DELETE | all | GDPR deletion, re-authentication required |
| `/spark/auth/me` | GET | all | Current user info |
| `/spark/auth/logout` | POST | all | Log out (requires XSRF token) |
| `/spark/auth/csrf-refresh` | POST | all | Get a fresh CSRF token |

Every mutating route carries the antiforgery stamp, anonymous ones included; the one stated
exemption is Microsoft's bearer `/refresh` (its credential travels in the body).

#### Sign-in identifier (#460 D4)

`/login` and the OIDC `/connect/login` page share `SparkSignInManager<TUser>.FindUserForSignInAsync`:
an identifier containing `@` is looked up as an email first and as a user name only when **no
account** has that email; without `@` it is a user name. A wrong password never falls through to a
second account. `SparkUserNameValidator<TUser>` enforces the matching rule on every save: **a user
name containing `@` must equal that account's own email**, so the two lookups cannot collide. A
confirmed email change moves an email-shaped user name with it (`SparkUserManager<TUser>`); a chosen
handle stays. Two-factor and recovery codes work unchanged (the resolved user carries them).

#### Account mail and links (#460 D6, D16)

Every account mail goes through Identity's `IEmailSender<TUser>` (MailManager implements it), and its
link comes from `ISparkAuthLinkBuilder`: the **SPA's** pages — `{PublicBaseUrl}/confirm-email?userId=…&code=…[&changedEmail=…]`
and `{PublicBaseUrl}/reset-password?email=…&code=…` — whose components post the token back.

- ⚠️ Set `Spark:Auth:PublicBaseUrl` (or `SparkAuthenticationOptions.Links.PublicBaseUrl`) outside
  Development. Without it no link-bearing mail is sent (logged as an error) rather than deriving a
  link from the request's `Host` header, which on an anonymous endpoint is the attacker's to choose
  (password-reset poisoning). Development falls back to the request origin.
- **Sending** (#460 M8): add `MintPlayer.Spark.MailManager` (`spark.AddMailManager()`, `Spark:Mail:*`)
  and nothing else. Spark replaces Identity's no-op `IEmailSender<TUser>` with one that queues the
  shipped MJML templates `SparkAuth/ConfirmEmail`, `SparkAuth/PasswordReset` and
  `SparkAuth/LinkConfirmation` (English + Dutch; override one with `Templates/Mail/SparkAuth/{Name}[.{culture}].mjml`
  in the app). Each mail is `Sensitive` (encrypted in the queue, scrubbed when done), expires shortly
  before its token and is written in `SparkUser.PreferredCulture` (null → `Spark:Mail:DefaultCulture`).
  An `IEmailSender<TUser>` the app registers itself is kept.
- **Registration needs mail (D6).** `LocalCredentials = Full` whose mail would be discarded —
  Identity's no-op, or Spark's sender without MailManager — refuses startup. Opt out with
  `AllowUnconfirmedRegistration = true` / `Spark:Auth:AllowUnconfirmedRegistration=true`.
- `SparkAuthenticationOptions.RequireConfirmedEmail` (default `false`) refuses sign-in to unconfirmed
  accounts. A completed password reset confirms the email, and `forgotPassword` mails unconfirmed
  addresses, so older unconfirmed accounts can always get in.

#### Secrets at rest (#460 D5)

The authenticator key and every external-login token are stored as `sdp1:` + Data Protection
ciphertext (purpose `MintPlayer.Spark.Authorization.UserStore` + field kind, no user id). Legacy
plaintext is still read, and `SparkUserBackfill<TUser>` rewrites it once after start (marker document
`SparkAuth/Backfills/Users.v1`; `BackfillUsersOnStartup = false` to skip). ⚠️ If the key ring loses
its keys, protected values become unreadable: the authenticator key then reads as a key nobody holds —
**two-factor stays required**, authenticator codes fail, recovery codes still work, and resetting the
key repairs the account (a `null` would have made Identity skip the second factor). Tokens read as
absent until the provider issues new ones. `SparkUser.CreatedAtUtc` and `RegistrationMethod`
(`password`, `external:{scheme}`, `other`) are stamped on create; the backfill fills `CreatedAtUtc`
from the oldest revision where revisions exist (RavenDB keeps no creation date in metadata).

#### External providers (#460 D7)

Presets: `AddGitHub`, `AddSparkGoogle`, `AddSparkMicrosoftAccount`, `AddSparkFacebook`,
`AddSparkTwitter`, `AddSparkLinkedIn` (on the `IdentityBuilder` in `configureProviders`). Each declares
its **verified-email signal**: verified → a confirmed account; a reliable signal saying "not verified"
→ no account (`email_not_verified`); **no reliable signal** (Facebook, X, Microsoft work/school
accounts) → an **unconfirmed** account and a confirmation mail (`confirm_email_sent` when
`RequireConfirmedEmail` is on). Microsoft is trusted only for personal accounts (id-token `tid` = the
consumers tenant). A scheme with no preset keeps the old rule: `email_verified=true` or no account.
Override per scheme with `SparkAuthenticationOptions.ExternalProviders[scheme]`. New user names are a
**slug of the display name** (`john-doe`, `john-doe-2`, never the email's local part), editable on
the profile page; GitHub keeps the login verbatim (applications compare it with repository owners).

#### Account deletion and personal data (#460 D8)

`DELETE /spark/auth/manage/account` with `{ password }`, or with no body when the session's sign-in is
younger than `ReauthenticationMaxAge` (default 5 minutes) — otherwise `403 reauthentication_required`.
Every `ISparkAccountDeletionHandler<TUser>` runs first (registration order); one that throws stops the
deletion with the account intact (`500 deletion_failed`, retryable — make handlers idempotent). The
store deletes the account last and releases its email and passkey reservations. Only then does every
`ISparkAccountDeletedHandler<TUser>` run — the place for a goodbye mail, which must not go out when
the deletion stopped; one that throws is logged and the response is still 204. Audit fields hold user
ids only; **RavenDB revisions are not rewritten** — content-level personal data is the application's
handler's job.

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

### npm dependency and the generated setup file

When you reference `MintPlayer.Spark.Authorization` (via NuGet), the package's MSBuild targets:

1. **Check that the SPA declares `@mintplayer/ng-spark-auth`** — warning `SPARK030` when
   `$(SpaRoot)package.json` does not. The build never runs `npm` (it used to, in the wrong directory
   for a root-level `node_modules`, writing an unpinned range); add the dependency yourself with the
   major matching your Angular major.
2. **Generate `spark-auth.setup.ts`** once — a TypeScript scaffolding file with documented auth helpers.

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

`twitterProvider()` (scheme `Twitter`, labelled "X") and `linkedInProvider()` (scheme `LinkedIn`) match
the server's `AddSparkTwitter()` / `AddSparkLinkedIn()` presets, next to `githubProvider()`,
`googleProvider()`, `facebookProvider()` and `microsoftProvider()`.

### Account pages (`withAccount()`, #460 D16)

```typescript
import { sparkAuthRoutes, withLocalLogin, withAccount } from '@mintplayer/ng-spark-auth/routes';
import { provideSparkAccountProfileFields } from '@mintplayer/ng-spark-auth/models';

// routes
...sparkAuthRoutes(withLocalLogin(), withAccount()),
// providers (optional: app fields on the profile page)
provideSparkAccountProfileFields(
  { name: 'Bio', label: 'profile.bio', type: 'textarea', maxLength: 500 },
  { name: 'Newsletter', label: 'profile.newsletter', type: 'checkbox' },
),
```

| Page | Default path | Component | Server |
|---|---|---|---|
| Confirm email (public) | `confirm-email` | `SparkConfirmEmailComponent` | `POST confirm-email { userId, code, changedEmail? }` |
| Overview | `account` | `SparkAccountOverviewComponent` | links to the mounted pages |
| Profile | `account/profile` | `SparkAccountProfileComponent` | `GET/POST manage/profile`; email change via `POST manage/info { newEmail }` |
| Password | `account/password` | `SparkChangePasswordComponent` | `POST manage/password` |
| Two-factor | `account/two-factor` | `SparkTwoFactorSetupComponent` | `POST manage/2fa`, `GET manage/2fa/authenticator-uri` |
| Connected logins | `account/logins` | `SparkExternalLoginsComponent` | `GET external-logins`, link / unlink |
| Passkeys | `account/passkeys` | `SparkPasskeysComponent` | `passkeys/*` |
| Personal data + deletion | `account/personal-data` | `SparkPersonalDataComponent` | `GET manage/personal-data`, `DELETE manage/account` |

- **Guarding and paths.** Every page except confirm-email is guarded by `sparkAuthGuard`
  (`sparkAuthenticatedGuard` is the same guard). That guard waits for the session check, so reloading an account page does not send a signed-in user
  to the sign-in page. Override the guard with `withAccount({ canActivate: [...] })`, change a path
  with `withAccount({ profile: 'me' })`, or leave pages out with `exclude: ['externalLogins']`.
  `confirm-email` must match `Spark:Auth:Links:ConfirmEmailPath`, which is where confirmation mails
  link to. No path starts with a parameter, so the pages neither shadow `sparkRoutes()` nor are
  shadowed by it.
- **Profile.** The profile page shows:
  - the user name;
  - the email, with a change form that mails the NEW address, so nothing changes until that link is
    opened;
  - the **language for emails**, which sets `SparkUser.PreferredCulture`. The choices are the app's
    languages from `/spark/culture`, and "Default" clears it;
  - the app's `SPARK_ACCOUNT_PROFILE_FIELDS`, each validated and stored by an
    `ISparkProfileContributor<TUser>` that declares the same name.
  Field errors render next to their control.
- **Two-factor.** The QR code is the server's SVG, shown as an `<img>` data URL and never inserted as
  markup. Recovery codes are shown once, right after they are generated.
- **Account deletion.** It asks for the password, or accepts a sign-in younger than
  `ReauthenticationMaxAge` (5 minutes). A 403 `reauthentication_required` is explained on the page.
- **Mode restrictions.** Under `SparkLocalCredentials.Disabled`, `manage/password` and `manage/info`
  are not mapped. Exclude `changePassword` there; the profile page's email-change form then shows the
  404 as "not available".
- **Login label.** The login form's identifier field is labelled "Email or user name"
  (`auth.emailOrUserName`, D4).

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
- `../apps/CodeCoverage/CodeCoverage/Program.cs` -- `spark.AddAuthentication<SparkUser>(…)` with an external provider, then `UseSpark()` / `MapSpark()`
- `../apps/Fleet/Fleet/Program.cs` -- the same thing through `AddSparkFull` / `UseSparkFull`, which bundle the common packages
- `../apps/Fleet/Fleet/App_Data/security.json` -- role-based permissions including custom action permissions
- `../apps/HR/HR/App_Data/security.json` -- role-based permissions for HR entities
- `../apps/DemoApp/DemoApp/App_Data/security.json` -- a fully public app, and the `Query`-without-`Read` showcase
- `../../spark/MintPlayer.Spark/Services/SecurityFileAccessControl.cs` -- permission evaluation, in core

## Requirements

- .NET 10.0+
- RavenDB 6.2+
- Node.js (for automatic npm integration)
- Angular 22+ (for frontend components)

## License

MIT License
