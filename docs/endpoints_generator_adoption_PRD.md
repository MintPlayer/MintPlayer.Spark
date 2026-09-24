# PRD — Use the Endpoints source-generator everywhere

Branch: `feat/endpoints-generator-everywhere`. Status: **not started** — blocked on a generator
change the maintainer is making. This document exists so the investigation is not repeated.

## Intent

`MintPlayer.AspNetCore.Endpoints` gives a class per endpoint: `Path`, `Methods`, group membership via
`IMemberOf<TGroup>`, metadata via `static IEndpointBase.Configure`, constructor injection, and
source-generated discovery. Spark uses it for about half its surface and hand-writes the rest. The
two halves have drifted, and the drift is not cosmetic — it is where the CSRF work in #451 kept
finding gaps.

**Goal:** every endpoint Spark maps is a generator endpoint, so that "what is this endpoint's path,
verb, group and metadata" is answered the same way everywhere.

## Measured starting point (2026-09-24)

| Assembly | State |
|---|---|
| `MintPlayer.Spark` | ✅ generator — the whole core surface |
| `MintPlayer.Spark.Replication` | ✅ generator — both endpoints |
| `MintPlayer.Spark.Authorization` | ⚠️ **mixed** — 4 generator classes; **14 hand-mapped routes** (7 passkey, 7 external-login) |
| `MintPlayer.Spark.IdentityProvider` | ❌ not opted in — **~12 hand-mapped** `/connect/*` |
| `MintPlayer.Spark.Webhooks.GitHub` | ❌ not opted in — **2 hand-mapped** |

32 generator classes against ~37 hand-mapped routes. Three assemblies carry
`[assembly: EndpointsMethodName(...)]`; two do not.

⚠️ The passkey surface added in #442 is hand-mapped, so this is not only legacy — it is still being
added to.

## ⚠️ Two blockers, both real, both found by reading the generator rather than guessing

### B1 — Generic endpoints

The generator emits `ActivatorUtilities.CreateFactory<T>(Type.EmptyTypes)` per endpoint, which needs
a **closed** type. The auth surface is generic over `TUser` (`MapSparkIdentityApi<TUser>`,
`SignInManager<TUser>`, `UserManager<TUser>`), and the generator has no way to know what `TUser` is.

Note how the four auth endpoints that *did* migrate avoid this: `Logout` calls
`httpContext.SignOutAsync(IdentityConstants.ApplicationScheme)` rather than taking
`SignInManager<TUser>`. That works for logout and does not generalise — passkey enrolment genuinely
needs the typed managers.

⚠️ **Unknown, and the thing to settle first:** does the generator *skip* an open generic implementing
`IEndpointBase`, or does it emit uncompilable code for it? The README documents no behaviour either
way (MPEP001-006 do not mention generics). This decides whether generic endpoint classes can even
coexist with the generator in the same assembly.

### B2 — Conditional registration

The generator maps everything it discovers, unconditionally. Several surfaces are conditional:

- passkey routes only when `SparkPasskeys.Enabled`
- external-login routes only when a provider is configured, and `/external-logins/link` only when
  `ExternalLoginLinking != WhenSignedIn`
- `/connect/login` and `/connect/two-factor` only in some `SparkLocalCredentials` modes
- `MapIdentityApi` routes filtered per mode by `LocalCredentialEndpointFilter`

## The escape hatch that probably resolves both

`MapEndpoint<T>()` — documented as "for one-off registrations without the source generator", and
crucially **it honours group membership**, nested prefixes and the groups' `Configure` hooks, giving
the same route the generated mapping would.

So a conditional or generic endpoint could still be written as a proper endpoint class and registered
explicitly at the point where the condition is known and `TUser` is closed:

```csharp
if (passkeys == SparkPasskeys.Enabled)
    endpoints.MapEndpoint<PasskeyCreationOptions<TUser>>();
```

That would give one vocabulary everywhere — `Path`, `Configure`, `IMemberOf` — while keeping the
call-site control the conditional surfaces need. **It depends entirely on B1's answer.**

## Why this is worth doing

Not tidiness. Three concrete things, all from #451:

1. **Metadata has one home.** `Configure` is where antiforgery, authorization and rate-limit metadata
   go. A hand-mapped route puts it in a fluent chain at the registration site, far from the handler,
   which is how `/spark/github/dev-ws` ended up mapped with a bare `Map()` — matching every mutating
   verb, with no metadata and no gate that could reach it.
2. **`IEndpointGroup.Configure` is implemented nowhere.** It is the natural place to apply a
   convention to a whole surface at once. #451 instead repeated one `WithMetadata` line 13 times.
3. **The route table becomes enumerable from source.** `XsrfSurfaceTests` has to build a host and
   read `EndpointDataSource` to answer "what is the mutating surface". That is the right test either
   way, but a uniform declaration makes an analyzer possible.

## Out of scope

- ⚠️ **`MintPlayer.AspNetCore.SpaServices.Xsrf` is NOT being adopted, and this is settled** — its
  middleware sets only `Path` and `HttpOnly`, leaving the token cookie without `Secure` or
  `SameSite`, and Spark's `XsrfCookieFlagTests` would fail the swap. Declined on evidence twice
  (`docs/coverage-handoff-plan.md:667-676`, then `docs/xsrf_minting_PRD.md §4`). Filed upstream as
  [MintPlayer.AspNetCore.SpaServices#85](https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/85);
  revisit only if that lands.
  - What the package gets *right* is the **placement** — `Response.OnStarting` rather than before the
    handler. That is a separate, still-open piece of work (`docs/xsrf_minting_plan.md` M1-M3), and it
    is about Spark's own mint, not about taking the package.
- Changing what any endpoint *does*. This is a migration of how endpoints are declared.

## First steps when this unblocks

1. Settle B1 empirically: add one open-generic endpoint class to `MintPlayer.Spark.Authorization` and
   build. Skipped, or broken codegen?
2. Pick the smallest real target — `MintPlayer.Spark.Webhooks.GitHub`, 2 routes, non-generic, one of
   which (`/api/github/webhooks`) already carries `DisableAntiforgery()` and so exercises `Configure`.
3. Then `IdentityProvider` (non-generic, but conditional), then the auth surface (generic **and**
   conditional) last.
