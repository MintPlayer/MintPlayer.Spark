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

### B1 — Generic endpoints ⚠️ ANSWERED 2026-09-25, and the answer is the bad one

**An open-generic endpoint class is not skipped — it breaks the build of the whole assembly.**
Measured against `11.1.0-rc.0` with a two-class repro; filed as
[MintPlayer.AspNetCore.Tools#34](https://github.com/MintPlayer/MintPlayer.AspNetCore.Tools/issues/34).

The generator writes the declaring type's own type parameter into two generated files, where nothing
can bind it:

```csharp
// EndpointMapping.g.cs
ObjectFactory<global::Echo<TPayload>> _f0 = ActivatorUtilities.CreateFactory<global::Echo<TPayload>>(Type.EmptyTypes);
// EndpointContracts.g.cs
[assembly: EndpointContractAttribute(typeof(global::Echo<TPayload>), "Echo", "/api/echo", …)]
```

→ `error CS0246: The type or namespace name 'TPayload' could not be found`.

⚠️ **This is worse than "unsupported".** Discovery is not opt-in, so one generic endpoint class fails
the build for every *other* endpoint in the assembly, with an error naming a generated file the
consumer cannot edit. It also closes the manual door: `MapEndpoint<T>()` would be the natural way to
register it with a closed argument, but you cannot reach that call site because the assembly does not
compile.

**A closed derived type works**, and inherits membership — measured in the same spike:

```csharp
public class EchoString : Echo<string> { public new static string Path => "/echo-string"; }
// → mapped at /api/echo-string, [MemberOf<ApiGroup>] inherited from the base
```

That is a fine answer for an **application**, which knows its closed types. It is no answer for a
**library**: Spark's `TUser` is chosen by the consuming application, so the framework assembly has no
closed type to declare, and making every app declare one per endpoint would be worse than the
`MapPost` calls it replaces.

**What unblocks it:** the generator skipping open generics with an info diagnostic. Then generic
endpoint classes coexist, and `MapEndpoint<Passkeys<TUser>>()` registers them where `TUser` is closed.

Note how the four auth endpoints that *did* migrate avoid the problem entirely: `Logout` calls
`httpContext.SignOutAsync(IdentityConstants.ApplicationScheme)` rather than taking
`SignInManager<TUser>`. That works for logout and does not generalise — passkey enrolment genuinely
needs the typed managers.

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

## ⚠️ This is now an upgrade *and* a migration

`c04ffac` in `MintPlayer.AspNetCore.Tools` is a **redesign**, published as `11.1.0-rc.0`:

- group membership moves from the `IMemberOf<T>` **interface** to a `[MemberOf<T>]` **attribute**,
  which now **inherits** from a base class
- route/query values bind to properties on the endpoint class (`[RouteParam]`)
- the generator captures the route literal, enabling build-time route diagnostics, typed links, a
  contract snapshot and a generated client
- diagnostics grew from MPEP001-006 to **MPEP001-024**; MPEP003/004 are retired in favour of `CS0579`
- the interfaces and attributes split into `MintPlayer.AspNetCore.Endpoints.Abstractions`

⚠️ **Spark is on `10.0.0`.** So adopting this is two jobs, not one: an upgrade that touches all **32
existing** endpoint classes (`IMemberOf<T>` → `[MemberOf<T>]`, plus whatever `11.0.0` changed), and
then the migration of the ~37 hand-mapped routes. They should be separate commits — a mechanical
upgrade with no behaviour change is reviewable; mixed with a migration it is not.

## Sequencing

1. ✅ **B1 settled** — see above. Open generics break the build; Tools#34 filed.
2. **Upgrade 10.0.0 → 11.1.0-rc.0** on the three opted-in assemblies, mechanically, no new endpoints.
   Verify against all **five** test projects — the route table is asserted by `XsrfSurfaceTests`, so a
   membership or prefix regression shows up as an exact-set failure rather than silently.
3. **Migrate `MintPlayer.Spark.Webhooks.GitHub`** — 2 routes, non-generic, and
   `/api/github/webhooks` already carries `DisableAntiforgery()`, so it exercises `Configure`
   metadata. Smallest real target.
4. **Migrate `MintPlayer.Spark.IdentityProvider`** — ~12 routes, non-generic but **conditional**
   (B2). This is where `MapEndpoint<T>()` gets proven on a real surface.
5. **The auth surface last** — 14 routes, generic **and** conditional. ⚠️ Blocked on Tools#34.

## Related, not part of this

`MintPlayer.AspNetCore.SpaServices.Xsrf` **`11.0.0-rc.2` is published** (PR #86 merged, closing the
issue Spark filed). The swap and its checklist are tracked in MintPlayer.Spark#452 — including
whether Spark's own Traefik deployment is emitting a non-`Secure` cookie today, which is worth
checking independently of the swap. Still not a reason to take the package's middleware in place of
Spark's mint; see *Out of scope*.
