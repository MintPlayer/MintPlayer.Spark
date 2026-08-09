# Authentication schemes, `Everyone`, and what happens when authentication fails

Who a caller is, how Spark decides it, and what they get when the answer is "nobody".

This is a reference for the whole repository — the framework packages and the demos. Every claim
below is either cited to a file or pinned by a named test; where something is **not** covered by a
test, it says so rather than implying otherwise.

> **Read this before changing an authentication scheme.** The failure mode throughout this area is
> silent: a wrong default scheme authenticates *fewer* callers rather than erroring, and a caller
> who fails authentication is handled exactly like one who never tried. Nothing goes red.

---

## 1. How a request acquires an identity

Spark's endpoints carry **no** `[Authorize]` and **no** `RequireAuthorization`. They are anonymous at
the ASP.NET layer and authorize inside the handler via `IPermissionService`.

That has a consequence worth stating plainly, because it was a real defect (F7): **ASP.NET only runs
the default authenticate scheme** unless an endpoint names another. Registering an extra scheme did
nothing on a Spark endpoint — its caller simply arrived anonymous.

Since M9, Spark installs a composite handler as `DefaultAuthenticateScheme`
(`libs/spark/MintPlayer.Spark.Abstractions/Authentication/SparkCompositeAuthenticationHandler.cs`).
It tries each registered credential scheme in order and adopts the first that succeeds:

```csharp
spark.AddCredentialScheme(IdentityConstants.ApplicationScheme, isAmbient: true);  // cookie
spark.AddCredentialScheme(IdentityConstants.BearerScheme);                        // token
spark.AddCredentialScheme<TOptions, THandler>("MyScheme");                        // register + declare
```

Only `DefaultAuthenticateScheme` is redirected. **Challenge, sign-in and sign-out stay with
Identity** — the composite reads credentials and issues none, so a sign-in pointed at it would have
nothing to write to.

### `isAmbient` — the only thing it decides

A credential is *ambient* when the browser attaches it to any request to the origin without the
caller doing anything. That is a cookie, and it is the entire precondition for CSRF.

`isAmbient` drives one decision: whether Spark demands an antiforgery token
(`SparkExtensions.IsNonAmbientCredential`). A bearer token or client certificate cannot be replayed
by a cross-site page, so demanding a token of such a caller protects nothing and makes external
POSTs impossible (F8) — a CI job has no `XSRF-TOKEN` cookie to echo.

The decision reads **the scheme that produced the principal**, never request shape. Keyed on "was an
`Authorization` header present", an attacker could switch off CSRF protection for a
cookie-authenticated victim by attaching a junk header. A junk header authenticates nothing, so no
scheme records itself and the gate still runs.
*Pinned by `CredentialSchemeTests.An_unrecognised_credential_does_not_suppress_the_antiforgery_gate`.*

Spark mints the `XSRF-TOKEN` cookie itself (`SparkExtensions`, the middleware registration) with
`SameSite=Strict`, `Secure` when the request is HTTPS, and `HttpOnly=false` so the SPA can read and
echo it. It deliberately does **not** use `UseAntiforgeryGenerator()` from
`MintPlayer.AspNetCore.SpaServices.Xsrf`, which sets only `Path` and `HttpOnly` — adopting it would
drop `Secure` from the token cookie. `XsrfCookieFlagTests` asserts both attributes end to end, so
that swap would fail the suite rather than pass quietly.

---

## 2. The schemes

| Scheme | Registered by | Credential | Ambient | In composite |
|---|---|---|---|---|
| `Identity.Application` | `AddIdentityApiEndpoints` via `spark.AddAuthentication<TUser>()` | Session cookie | **Yes** | Yes |
| `Identity.Bearer` | same | `Bearer` access token | No | Yes |
| `Identity.External` | same | Transient cookie during an OAuth round trip | — | No |
| `Identity.TwoFactorUserId` | same | Cookie holding a **partially** authenticated user between password and second factor | — | **No** |
| `Identity.TwoFactorRememberMe` | same | "Don't ask again on this device" cookie | — | No |
| External providers (GitHub, Google, Microsoft, Apple) | `configureProviders` on `spark.AddAuthentication<TUser>()`; GitHub via `GitHubAuthenticationExtensions.cs` | OAuth round trip; signs into `Identity.External` (`GitHubAuthenticationExtensions.cs:32`) | — | No |

**The two-factor schemes are deliberately absent from the composite.** A `TwoFactorUserId` cookie
represents someone who proved a password and nothing else. It must never satisfy a Spark
authorization check, and it does not: it is not registered as a credential scheme, so the composite
never consults it.

### The identity provider does not use the composite, on purpose

`MintPlayer.Spark.IdentityProvider`'s interactive pages resolve the end user by asking for
`Identity.Application` **explicitly** rather than reading the ambient principal
(`Endpoints/InteractiveUserExtensions.cs:29`):

> The authorization-code grant delegates *a person's* authority. Only the cookie the login page
> issues evidences a person, so only that scheme is consulted here.

This closed a real hole (O16): the ambient principal used to be whatever the first registered scheme
produced — the bearer scheme — so an API access token satisfied "is a user signed in?" on
`/connect/authorize` and the consent pages, and could drive the whole interactive grant headlessly.
M9 changes the ambient principal's *ordering* (cookie is now tried first), but the IdP's explicit
resolution is what makes the guarantee, and it is unaffected.

---

## 3. Callers authenticated without a scheme

Two paths verify a caller **inside the endpoint** and never set `HttpContext.User`. They are
authenticated in the ordinary sense of the word and anonymous to the authorization pipeline.

| Path | Verifies | Sets `User`? | Therefore |
|---|---|---|---|
| Replication mTLS (`libs/replication/.../ModuleCertificateValidator.cs`) | Client cert thumbprint against the module's pin in `SparkModules` | No | The module is authenticated but has **no groups** — `/spark/sync/apply` bypasses `IPermissionService` entirely, so an authenticated module can write anything anywhere (**F4**, open, scheduled as M11) |
| GitHub webhooks (`libs/webhooks/`) | HMAC over the payload | No | Correct crypto, no identity established (**F11**, scheduled as M11) |

Retiring both bypasses is M11. Until then, treat "authenticated module" as "fully trusted module".

---

## 4. The four outcomes — and why three of them are the same

| Outcome | `HttpContext.User` | Groups | Rights (with `security.json`) |
|---|---|---|---|
| **A. No credential** | Unauthenticated empty principal | none | `Everyone` only |
| **B. Credential accepted** | Authenticated principal | its group claims **+ `Everyone`** | its groups' rights + `Everyone`'s |
| **C. Credential refused by every scheme** | **identical to A** | none | `Everyone` only |
| **D. Authentication not configured** | identical to A | none | `Everyone` only |

**A, C and D are indistinguishable downstream.** ASP.NET's authentication middleware assigns
`HttpContext.User` only when a result carries a principal, and both `AuthenticateResult.Fail` and
`NoResult` carry none — so a rejected credential reaches the endpoint as anonymity and is *authorized*
as anonymity.

This is asserted against a running pipeline, not taken from documentation:
`AuthenticationOutcomeTests.A_refused_credential_is_indistinguishable_from_no_credential`.

The only trace outcome C leaves is a warning from the composite handler naming the schemes that
refused. That log line is the entire difference between "presented a bad credential" and "presented
nothing" — worth knowing when reading an incident.

### `IsAuthenticated` does not affect the decision

Nothing in the authorization path branches on authentication state except
`ClaimsGroupMembershipProvider.cs:32`, which returns an empty group list for an unauthenticated
principal. Endpoints do read `User.Identity?.IsAuthenticated`, but only **after** a denial, to choose
`401 "Authentication required"` over `403 "Access denied"` (e.g. `Endpoints/PersistentObject/List.cs:31-40`).
It never changes whether access is granted.

---

## 5. `Everyone` is a baseline grant, not an anonymous-only group

`AccessControlService.cs:48-54` adds `Everyone` to **every** caller's group set — authenticated or
not, before the no-groups check, with no authentication-state test anywhere near it. It is matched by
*display name*: any group whose default translation is literally `Everyone`.

So `Everyone`'s rights are the floor for every caller in every outcome. A signed-in administrator has
them too.

**This is not a defect, but it is a sharp edge**: a right granted to `Everyone` is granted to the
public internet. In both shipped demos that grant is exactly one entry:

```json
{ "resource": "QueryRead/Company", "groupId": "00000000-0000-0000-0000-000000000000" }
```

An unauthenticated caller against Fleet or HR can list and read Company records, and nothing else.
*Pinned end to end by `PermissionsEndpointAuthTests.Unauthenticated_GET_permissions_for_Company_reports_read_but_no_write`
and `..._for_Car_reports_no_access`.*

---

## 6. The four authorization configurations

Which `IAccessControl` is registered last wins.

| Configuration | Behaviour | Where |
|---|---|---|
| Neither opt-in called | **Denies everything**, unconditionally | `DenyAllAccessControl`, the DI default (`SparkExtensions.cs:65`) |
| `spark.AddAuthorization()` | `security.json` groups + rights, with `Everyone` always added | `AccessControlService` |
| `spark.AllowAnonymousAccess()` | **Allows everything.** `security.json`, groups and `Everyone` are never consulted at all | `AllowAllAccessControl` |
| `AddAuthorization(o => o.DefaultBehavior = AllowAll)` | `security.json` as above, but an unmatched resource is *allowed* instead of denied | `AccessControlService`, both the empty-groups branch and the final fallthrough |

Spark's real fail-closed guarantee lives in the first row — the deny-all DI default (R2-H1) — not in
any authentication check. The other three are deliberate ways to waive it, and all of them waive it
for unauthenticated callers too.

**A missing `security.json` is not a locked door.** The loader logs a warning and returns an empty
configuration (`SecurityConfigurationLoader.cs:57-61`). With no groups and no `Everyone`, every
request falls to `DefaultBehavior` — so an app that set `AllowAll` for convenience and has no
`security.json` allows everything to everyone. That is exactly WebhooksDemo's configuration
(`Demo/WebhooksDemo/WebhooksDemo/Program.cs:29`, no `App_Data/security.json`); it is a demo, but do
not copy the pairing into an app that matters.

### What each demo actually ships

| Demo | Wiring | Effective posture |
|---|---|---|
| **Fleet** | `AddSparkFull` → `AddAuthorization()` + `AddAuthentication<SparkUser>` | `security.json`; anonymous gets `QueryRead/Company` |
| **HR** | `AddSpark` + `AddAuthorization()` (`Program.cs:28`) | same |
| **DemoApp** | `AllowAnonymousAccess()` (`Program.cs:30`) | **everything allowed, no authorization at all** |
| **WebhooksDemo** | `AddAuthorization(DefaultBehavior = AllowAll)`, no `security.json` | **everything allowed** |

---

## 7. Test coverage — what is actually verified

| Behaviour | Covered | Where |
|---|---|---|
| Browser cookie login succeeds and grants group rights | **E2E** | `SparkClientFactory.LoginAsync` used across the Fleet E2E suite |
| Anonymous caller gets `Everyone` rights and no more | **E2E** | `PermissionsEndpointAuthTests` (both cases) |
| Anonymous caller is denied what `Everyone` does not grant | **E2E** | `PermissionsEndpointAuthTests.Unauthenticated_GET_permissions_for_Car_reports_no_access` |
| Anonymous caller sees only permitted metadata | **E2E** | `MetadataEndpointAuthTests` (4 tests) |
| Denied and non-existent are indistinguishable to the client | **E2E** | `NotFoundVsForbiddenTests` |
| Mutating request without an XSRF token is rejected | **unit only** | `AntiforgerySecurityTests` — no E2E equivalent |
| The XSRF cookie carries `Secure` and `SameSite=Strict` | **E2E** | `XsrfCookieFlagTests` (2 tests) |
| Non-ambient credential is exempt from antiforgery | **unit** | `CredentialSchemeTests` (4 tests) |
| A refused credential does **not** suppress the antiforgery gate | **unit** | `CredentialSchemeTests` |
| A refused credential is indistinguishable from anonymity | **unit** | `AuthenticationOutcomeTests` (3 tests) |
| Composite is the default authenticate scheme; sign-in/challenge are not | **unit** | `SparkBuilderExtensionsTests` (3 tests) |
| Row-level filtering on list/query/stream | **E2E + unit** | `RowLevelAuthzTests`; `RowLevelQueryAuthorizationTests` |
| Replication endpoints refuse an unauthenticated caller | **E2E** | `ReplicationEndpointAuthTests` (3 tests) |
| Development-mode mTLS still verifies module registration | **unit** | `ModuleCertificateValidatorTests` |

### Gaps, stated rather than implied

- **Only Fleet has an E2E host.** DemoApp, HR and WebhooksDemo are not exercised end to end at all,
  so "login works" is verified for one demo of four.
- **No E2E test authenticates with a bearer token.** The bearer scheme's participation in the
  composite is covered only by unit tests.
- **A 2FA-pending principal is not tested against a Spark endpoint.** It is excluded by construction
  (the scheme is never registered as a credential), which is a strong argument but not a test. The
  equivalent guarantee *is* tested for the identity provider's own endpoints
  (`OidcTwoFactor*` tests, §L.4 of the [IdP matrix](./idp-e2e-test-matrix.md)).
- **`DenyAllAccessControl` has no E2E coverage** — no demo ships without an authorization opt-in, so
  the fail-closed default is only unit-tested.
- **The two non-scheme paths (replication sync-apply, webhooks) are known bypasses**, not gaps in
  testing. See F4/F11 and M11 in the [handoff plan](./coverage-handoff-plan.md).

---

## See also

- [Authorization package](../libs/authorization/MintPlayer.Spark.Authorization/README.md) — `security.json`, groups, rights syntax
- [Replication mTLS](./guide-replication-mtls.md) — cross-module client certificates
- [HTTP API Specification](./Spark-API-Specification.md) — per-endpoint auth requirements
- [findings-replication-mtls.md](./findings-replication-mtls.md) — F1–F11, the analysis this guide's §3 summarises
