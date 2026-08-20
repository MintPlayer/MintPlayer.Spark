# 10.0.0-preview.58

## Breaking changes

### `AddAuthentication<TUser>` takes a new first parameter

```diff
- spark.AddAuthentication<SparkUser>(configureIdentity, configureProviders)
+ spark.AddAuthentication<SparkUser>(configure, configureIdentity, configureProviders)
```

All three are optional. Callers using named arguments — which every demo and every documented example
does — are unaffected. **Positional** callers that passed `configureIdentity` first must add the new
parameter or switch to named arguments.

### `ExternalLoginOptions` is deleted

`MintPlayer.Spark.Authorization.Configuration.ExternalLoginOptions` is gone. It was dead
configuration: nothing read it, and it gated nothing. External providers have always been registered
through the `configureProviders` callback, and still are:

```csharp
spark.AddAuthentication<SparkUser>(
    configureProviders: identity => identity.AddGitHub(o => { /* … */ }));
```

Remove any reference to the type. Its presence made it look like the place providers were enabled,
which is the reason it is being removed rather than left alone.

## Local credentials are now opt-out — server and client

`spark.AddAuthentication<TUser>()` used to mount ASP.NET Core Identity's entire endpoint family
unconditionally, and `sparkAuthRoutes()` always emitted all five local-credential pages. An
application that signs users in exclusively through GitHub (or any other external provider) had no
way to turn either off.

```csharp
spark.AddAuthentication<SparkUser>(
    configure: auth => auth.LocalCredentials = SparkLocalCredentials.Disabled,
    configureProviders: identity => identity.AddGitHub(/* … */));
```

```ts
...sparkAuthRoutes({ localCredentials: 'disabled' }),
provideSparkAuth({ loginUrl: '/sign-in' }),
```

Three modes: `Full` (the default, unchanged behaviour), `SignInOnly` (no self-service registration),
and `Disabled` (no local passwords at all). The excluded endpoints are **absent from the route
table**, not shadowed behind a 404 — they do not appear in the endpoint data source or in OpenAPI.

**External login is unaffected in every mode.** `/spark/auth/external-login` and its callback are
always mapped, and `Disabled` in fact *requires* at least one registered provider — mapping throws at
startup otherwise, rather than booting an application nobody can sign into.

The unit of opt-out is the whole family rather than one switch per endpoint, because closing
`register` alone is not enough: `forgotPassword`, `resetPassword`, `confirmEmail` and
`resendConfirmationEmail` remain a timing side-channel and an unauthenticated mail-send trigger. On
the client, the pages form a star centred on the login page and every template dereferences its
siblings unconditionally, so any proper subset dangles a link. Full reasoning in
[guide-authentication-schemes.md](./guide-authentication-schemes.md).

## New: `GET /spark/auth/capabilities`

Anonymous. Reports the local-credential mode and the registered external providers:

```json
{ "localCredentials": "Disabled", "externalProviders": [{ "scheme": "GitHub", "displayName": "GitHub" }] }
```

The mode is derived from the route table rather than read back from configuration, so it cannot claim
a surface that was never mapped. It exists because the client's route configuration and the server's
mode are set independently, and a mismatch was otherwise invisible until a user hit it.

## New: `SparkSignInComponent` (`@mintplayer/ng-spark-auth/sign-in`)

A sign-in landing page rendering one button per external provider, read from `/spark/auth/capabilities`
rather than from a hard-coded scheme name. Routed at `/sign-in` whenever local credentials are limited;
not routed in `full` mode, where the login page is already the landing page.

The library previously shipped no UI for external sign-in at all — every consumer hand-rolled the
button in its own shell and wrote the scheme as a string literal.

## Fixed: redirecting to a login route that does not exist

`sparkAuthGuard`, `sparkAuthInterceptor` and `SparkAuthBarComponent` each read `config.loginUrl`
independently. Nothing connected that value to the routes that actually exist, so pointing it at an
unregistered route produced a redirect into a blank page — no type error, no build failure, no runtime
error. All three now share `resolveSignInUrl`, which warns once in development when `loginUrl` matches
no registered route.

## `MintPlayer.Spark.IdentityProvider` honours the same mode

`Disabled` also drops `/connect/login` and `/connect/two-factor`. Every OIDC protocol endpoint —
`authorize`, `token`, `userinfo`, `introspect`, `revoke`, `logout`, `consent`, and both discovery
documents — is untouched in all modes: a provider that federates to an upstream provider needs all of
them. The mode is read from `SparkAuthenticationOptions` rather than duplicated, so the two packages
cannot disagree.

## Package versions

- All `MintPlayer.Spark.*` packages → `10.0.0-preview.58`
- `@mintplayer/ng-spark-auth` → `22.2.0` (new entry point and config fields — additive)
- `@mintplayer/ng-spark` → unchanged; its TypeScript surface is untouched
