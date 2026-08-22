# Authentication schemes, well-known groups, and what happens when authentication fails

Who a caller is, how Spark decides it, and what they get when the answer is "nobody".

This is a reference for the whole repository — the framework packages and the demos. Every claim
below is either cited to a file or pinned by a named test; where something is **not** covered by a
test, it says so rather than implying otherwise.

> **Read this before changing an authentication scheme.** The failure mode throughout this area is
> silent: a wrong default scheme authenticates *fewer* callers rather than erroring, and a caller
> who fails authentication is handled exactly like one who never tried. Nothing goes red.

---

## 0. The rule, in one table

**Authentication varies by surface. Authorization is `security.json` unless there is no Spark
identity to name.**

Two axes, and conflating them is the common mistake — it leads to "the certificate is the whole
answer for module traffic", which is exactly the property F4 and F12 existed to remove.

| Surface | Authentication (who are you) | Authorization (what may you do) |
|---|---|---|
| Spark endpoints — browser | Identity cookie | `security.json` |
| Spark endpoints — API client | Identity bearer, or external JWT (`Spark:JwtBearer`) | `security.json` |
| Spark endpoints — anonymous | none | `security.json` (the `anonymous` group only) |
| Inter-module: `/spark/sync/apply`, `/spark/etl/deploy` | client certificate → `Module:{Name}` | **`security.json`** |
| OAuth protocol: `/connect/token`, introspect, revoke | `client_id` + secret | the protocol's own rules — **not** `security.json` |
| GitHub webhooks | HMAC signature | none — no identity exists |
| Background jobs, framework bookkeeping | none | none |

Two things this table corrects, because both are easy to assume and both are wrong:

- **Spark endpoints are not cookie-or-anonymous.** Four credentials reach them — cookie, Identity
  bearer, module certificate, external JWT — and all four resolve to group claims and go through
  `security.json`. The cookie is just the browser case.
- **Inter-module traffic is not "certificate instead of `security.json`".** The certificate only
  *authenticates*. Authorization is still `security.json`, via `Module:HR` → `Replicate/Cars`,
  `ReadEditNew/Car`. Treating the certificate as the whole answer is what made an authenticated
  module omnipotent (F4 on the write side, F12 on the read side).

### The three exceptions, and why each is legitimate

- **OAuth protocol endpoints.** An OAuth client is a principal in a *different* model — its authority
  is scopes, grant types and consent, governed by RFC 6749/7009. `security.json` has nothing to say
  about "may this client refresh this token". Note the boundary: the `OidcApplication`/`OidcScope`
  **admin screens** are PersistentObjects, so *configuring* the OAuth system is `security.json`-
  governed. Only the protocol traffic is not.
- **Webhooks.** GitHub has no Spark identity and cannot be given one. HMAC proves the delivery is
  genuine, which is a different question from what a caller may do. Whatever the delivery ultimately
  causes in application data *is* authorized — as anonymous.
- **Framework bookkeeping.** No identity, and gating it breaks two of the four authorization
  configurations outright (§5a).

The unifying test is the same one throughout: **is there a caller identity for `security.json` to
name?** Cookie, bearer, certificate — yes, so authorize. Webhook, background tick — no, so the
control lands downstream, at the data.

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
| External providers (GitHub, Google, Microsoft, Apple) | `configureProviders` on `spark.AddAuthentication<TUser>()`; GitHub via `GitHubAuthenticationExtensions.cs` | OAuth round trip; signs into `Identity.External` (`GitHubAuthenticationExtensions.cs:32`) | — | No — challenge-only; never authenticates an incoming Spark request |

Two further schemes exist as of M10, both **opt-in** — an app registers them only if it accepts that
kind of caller:

| Scheme | Enabled by | Credential | Ambient | In composite |
|---|---|---|---|---|
| `Spark:ModuleCertificate` | `spark.AddModuleCertificateAuthentication()` (Replication) | Client certificate, identity from `CN`, pinned per module | No | Yes |
| `Spark:JwtBearer` | `spark.AddJwtBearerCredential(...)` (Authorization) | OAuth2/OIDC access token from a configured authority | No | Yes |

### Why those two are non-ambient — and why that is not a general rule

Neither can be replayed by a cross-site page: a browser will not construct an `Authorization` header
on an attacker's behalf, and cannot be induced to complete a TLS handshake with a module's private
key. So both are exempt from the antiforgery gate, which is what makes external POSTs work at all.

But **"a certificate is never ambient" is false in general.** A browser configured with a client
certificate for an origin attaches it automatically, exactly like a cookie, and a cross-site POST
would carry it. `Spark:ModuleCertificate` is safe to treat as non-ambient because it authenticates
*modules* against a registration pin — a browser holds no such certificate. A browser-facing
client-certificate deployment would need to revisit that.

### The two guards that refuse at startup

Both unsafe configurations are silent at runtime, so both are refused when the app builds:

- **`AddJwtBearerCredential` requires an `Audience`.** Without it, every token the issuer ever minted
  validates — including one a client obtained for an entirely different resource. The signature is
  genuine; the audience is the only field that says the bearer was meant to be talking to *you*.
- **`AddModuleCertificateForwarding` requires a `KnownProxies` entry.** A forwarded certificate is an
  ordinary request header, so accepting it from anywhere lets any caller that reaches the app
  directly claim to be any module. That is worse than no mTLS, because it looks like mTLS. The
  allowlist is enforced by stripping the header from untrusted peers *before* the forwarding
  middleware reads it, and an unidentifiable peer is not trusted.

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

The rest of the identity provider's surface also sits outside the scheme system, deliberately: the
token and introspection endpoints authenticate **OIDC clients** from `client_id`/`client_secret` in
the form body (`Token.cs:754-773`, reused by `Introspection.cs:46-52`), and `/connect/userinfo`
parses the `Authorization: Bearer` header itself and validates the JWT against the provider's own
signing keys (`UserInfo.cs:22-50`). These authenticate protocol participants, not `PersistentObject`
callers, so none of them belongs in Spark's composite. The scheme that *would* — a JWT-bearer
resource server validating the provider's access tokens for Spark's own endpoints — does not exist
yet and is M10.

---

## 3. Callers authenticated without a scheme

Two paths verify a caller **inside the endpoint** and never set `HttpContext.User`. They are
authenticated in the ordinary sense of the word and anonymous to the authorization pipeline.

| Path | Verifies | Sets `User`? | Therefore |
|---|---|---|---|
| Replication mTLS (`libs/replication/.../ModuleCertificateValidator.cs`) | Client cert thumbprint against the module's pin in `SparkModules` | No | Gates whether the request proceeds. Since M11 the **write** it authorizes goes through the normal chokepoint, so a module must be granted rights in `security.json` — see below |
| GitHub webhooks (`libs/webhooks/`) | HMAC over the payload | No | Correct crypto. It writes nothing itself; it broadcasts a message |

### Cross-module writes are authorized (M11)

`/spark/sync/apply` used to reach the actions pipeline directly, skipping `DatabaseAccess` — so the
certificate proved *which* module was calling and nothing then consulted what that module was
allowed to touch (**F4**). It now routes through the same chokepoint as every other write.

Two consequences for an existing deployment:

- Register `spark.AddModuleCertificateAuthentication()`, or the calling module arrives anonymous and
  holds only the `anonymous` group's rights.
- Grant it in `security.json` under the group name `Module:{Name}` — the scheme emits
  `group = "Module:HR"`, so a module is granted exactly like any other group.

A sync action against a collection with **no registered entity type is refused**: it has no name for
`security.json` to grant rights on, so no authorization decision exists to make about it, and the
previous CLR-reflection fallback wrote it regardless.

### The webhook path is not the same bypass — a correction

F11 grouped the webhook path with the mTLS one. Checking rather than assuming: `libs/webhooks/`
contains **no reference to `IDatabaseAccess`, `SavePersistentObjectAsync`, or a Raven session**. The
processor verifies the HMAC and hands a message to the bus; it writes nothing, so there is no write
path around the chokepoint.

What is true is narrower and still worth knowing: a recipient handling that message runs with **no
principal**, so anything it does through `IDatabaseAccess` is authorized as anonymous. It is
governed, just not attributed — an app cannot grant "the GitHub webhook" rights that a public caller
does not also have. Carrying an identity from producer to recipient is a messaging-package change,
and is not done.

---

## 4. The four outcomes — and why three of them are the same

| Outcome | `HttpContext.User` | Groups | Rights (with `security.json`) |
|---|---|---|---|
| **A. No credential** | Unauthenticated empty principal | none | `anonymous` only |
| **B. Credential accepted** | Authenticated principal | its group claims **+ `authenticated`** | its groups' rights + `authenticated`'s |
| **C. Credential refused by every scheme** | **identical to A** | none | `anonymous` only |
| **D. Authentication not configured** | identical to A | none | `anonymous` only |

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

## 5. `anonymous` and `authenticated` — the well-known groups

Two group roles are decided from authentication state rather than from claims. An application
declares which of its groups play them, **by id**:

```json
{
  "wellKnown": {
    "anonymous":     "00000000-0000-0000-0000-000000000000",
    "authenticated": "a1b2c3d4-0000-0000-0000-00000000000f"
  },
  "groups": {
    "00000000-0000-0000-0000-000000000000": { "en": "Anonymous visitors", "nl": "Anonieme bezoekers" },
    "a1b2c3d4-0000-0000-0000-00000000000f": { "en": "Signed-in users",    "nl": "Aangemelde gebruikers" }
  }
}
```

`anonymous` applies while the caller has **not** signed in; `authenticated` applies once they have.
They are mutually exclusive, and both are optional — an application declaring neither behaves exactly
as one declaring neither always did.

### What replaced `Everyone`, and why the migration is "one grant becomes two"

`Everyone` was added to **every** caller's group set — signed-in or not — so its rights were the
floor for everybody. It is deleted, and `anonymous` is not a drop-in replacement: it is **narrower**.

> Every right you granted to `Everyone` was granted to the public internet. Decide, per right,
> whether that was intended. If it was, move it to `anonymous`. If it was not, move it to
> `authenticated` — **do not delete it**. A right that both should keep becomes two grants.

⚠️ Fleet granted `QueryRead/Company` through `Everyone` and nowhere else, so the anonymous-only
migration would have locked every signed-in Fleet user out of Company. That is the failure mode this
paragraph exists to prevent.

**And never simply delete the grant.** Type-level rights gate row rules — `DatabaseAccess.cs:84` runs
before the row gate at `:106` — so with no grant at all `GetRowFilterAsync` and `IsAllowedAsync` never
run and *every* caller is denied, signed-in ones included. A row rule narrows an admitted right; it
cannot grant one.

A file still declaring a group named `Everyone` with no `wellKnown` block **fails to load**, with
these instructions inline. Once the roles are declared by id a group's *name* carries no behaviour at
all, so an application is then free to call a group whatever it likes — including "Everyone".

### Why by id rather than by name

The well-known groups used to be matched by *display name*, through
`TranslatedString.GetDefaultValue()`:

```csharp
public string GetDefaultValue() => Translations.Values.FirstOrDefault() ?? string.Empty;
```

That is the first translation in **file order** — not the English one. So
`{"en":"Everyone","nl":"Iedereen"}` matched and `{"nl":"Iedereen","en":"Everyone"}` did not:
reordering two JSON keys silently changed who could reach what. Renaming a group for the UI silently
un-declared its role, and two groups sharing a first translation resolved arbitrarily. Meanwhile
claim-based membership resolution matched a name against *any* translation — two different matching
rules, sixty lines apart.

An explicit id map fixes localization, renaming, duplicates and case in one move.

### A claim cannot assert a well-known group

Every id named in `wellKnown` is excluded from claim-derived membership resolution. So a principal
carrying `group: "Signed-in users"` does **not** get the authenticated group — authentication state
decides it, and nothing else can.

This is worth stating because it was previously false while being documented as true. A comment
shipped in #306 asserted the guarantee; `ResolveGroupIds` matched any provider-returned name against
any translation of any group, well-known ones included, so the name resolved the id at step 1 and the
`IsAuthenticated` test was never reached. Inert with the shipped claims provider, which returns
nothing for an unauthenticated caller — and silently broken by any custom one.

### The commonest shape

*Any signed-in user may query this type, and a row rule narrows it to their own rows.* Before the
`authenticated` group existed this could not be written at all: a signed-in user carrying no group
claims resolved to exactly the same set as an anonymous visitor, so the two were indistinguishable to
`security.json`.

```json
{ "resource": "QueryRead/Repository", "groupId": "a1b2c3d4-0000-0000-0000-00000000000f" }
```

### Seeing the anonymous surface

Every startup prints which rights an anonymous caller holds, **including when the answer is nothing**
— silence is indistinguishable from the check not running. `--spark-verify-security` fails CI when
that set changes, against a baseline written by `--spark-synchronize-security`. Both run without a
database, because the posture is computed from `security.json` alone.

In both shipped demos the anonymous grant is exactly one entry, `QueryRead/Company`, mirrored by an
`authenticated` twin. *Pinned end to end by
`PermissionsEndpointAuthTests.Unauthenticated_GET_permissions_for_Company_reports_read_but_no_write`
and `..._for_Car_reports_no_access`.*

---

## 5a. Which writes are authorized — and why the framework's own are not

Once cross-module writes went through the permission chokepoint (M11), the obvious next question is
where that stops. The framework writes documents constantly: message-queue entries, sync-action
records, module registrations, OIDC tokens, migration markers, cron locks. Should those be
authorized too?

**No, and the rule that says so is:**

> A write goes through the chokepoint **if and only if there is a caller identity for
> `security.json` to name.**

| Caller | Identity | Writes authorized? |
|---|---|---|
| A person | cookie or bearer token | Yes |
| A module | client certificate → `Module:{Name}` | Yes, since M11 |
| An OAuth client | `client_id`/secret | Yes, by the protocol's own rules (RFC 6749/7009), not `security.json` |
| A GitHub webhook | **none** — GitHub has no Spark identity | No. Gated by HMAC at a different layer |
| A background worker | **none** | No. Writes its own bookkeeping directly |

The module case is the important precedent, because it shows the rule is not "exempt awkward
callers". M11 did not special-case modules to skip authorization — it gave them an identity
(`Module:HR`) and put them through the *same* path as a person. Where an identity can exist, it
should, and then the write is governed.

### Why gating framework bookkeeping would break working applications

This was checked rather than argued. Under the configurations that existed at the time:

| Configuration | Framework write today | If routed through the chokepoint |
|---|---|---|
| `security.json` | Works | **Breaks silently** unless the operator grants rights on framework resource names they do not know exist |

The `security.json` row is the only one left today, and it is the one that breaks. A background worker has
no `HttpContext`, and `ClaimsGroupMembershipProvider` returns an empty group list rather than
throwing — so calling `IPermissionService` there would evaluate the worker **as an anonymous
caller**, not as a trusted system. That is a footgun rather than a control.

### Where the control actually lands

Nothing is lost by leaving the queue ungated, because the queue is not where authority is exercised.
An anonymous GitHub delivery can enqueue a `SparkMessage`; if the recipient of that message then
writes application data, **that** write goes through `IDatabaseAccess` and is authorized — as
anonymous, because that is who caused it. The control sits at the data, not at the plumbing.

### Two limitations this leaves, stated rather than hidden

- **There is no system principal.** A background job that legitimately needs to write app data with
  elevated rights has no way to say so: the framework understands "a caller with a principal" and
  "nothing", and "nothing" resolves to anonymous. Adding one means minting a synthetic identity per
  system actor and requiring apps to grant it — the `Module:{Name}` shape, generalized. Not built.
- **A framework collection can also be application data.** `OidcApplication` and `OidcScope` are
  deliberately exposed as PersistentObjects (via `IOidcApplicationContext`, which the HR demo
  implements), so they are administered through the ordinary authorized path *and* written by the
  provider's own internals. The exemption above therefore keys on the **write path**, not on the
  collection.

## 6. There is only one authorization configuration

Since preview.62 there is nothing to choose. `AddSpark` registers the `security.json` evaluator
unconditionally, every application has a file, and a missing or malformed one **refuses startup**.
`spark.AddAuthorization()`, `spark.AllowAnonymousAccess()` and `AuthorizationOptions.DefaultBehavior`
are deleted.

The four configurations this section used to enumerate were four answers to a question that should
not have existed — *what does the framework do before the developer has said anything?* Two of them
denied everything (which reads as a broken application), and two allowed everything (one of them
only when `security.json` was also absent, which is a pairing nobody chooses deliberately).

An application that genuinely wants to be open now grants `*/*` in its file, where the decision is
visible next to the rights it overrides, is printed by the startup posture report, and moves the
committed `securityPosture.txt` baseline in code review.

See **[the authorization guide](guide-authorization.md)** for the rights model, precedence, and
what `Query` without `Read` does to a grid.

### What each demo actually ships

| Demo | Wiring | Effective posture |
|---|---|---|
| **Fleet** | `AddSparkFull` + `AddAuthentication<SparkUser>` | anonymous gets `QueryRead/Company` |
| **HR** | `AddSpark` + `AddAuthentication<SparkUser>`, and hosts the **OIDC identity provider** | same |
| **DemoApp** | `AddSpark`, no sign-in at all | everything granted to `anonymous`, mirrored on `authenticated`; `Stock` and `Address` are `Query` without `Read` |
| **WebhooksDemo** | `AddSpark` + GitHub OAuth, local credentials disabled | `anonymous` declared and granted **nothing** |

Each demo's `App_Data/securityPosture.txt` states its anonymous surface in one committed file.

---

## 7. Test coverage — what is actually verified

| Behaviour | Covered | Where |
|---|---|---|
| Anonymous caller **cannot list or create** a protected entity (`Car`) | **E2E** | `AnonymousPersistentObjectAccessTests` (4 tests) |
| Anonymous caller **can** read what the `anonymous` group grants (`Company`) | **E2E** | same |
| Anonymous caller gets the `anonymous` group's rights and no more, as reported | **E2E** | `PermissionsEndpointAuthTests` (2 tests) |
| Anonymous caller sees only permitted metadata | **E2E** | `MetadataEndpointAuthTests` (4 tests) |
| Anonymous caller cannot mutate lookup references | **E2E** | `LookupReferenceAuthTests` (2 tests) |
| Denied and non-existent are indistinguishable to the client | **E2E** | `NotFoundVsForbiddenTests` |
| Browser cookie login grants group rights | **E2E, indirectly** | every admin-authenticated `Security/*` test succeeds at a Car operation; no test asserts login→rights as its own subject |
| Mutating request without an XSRF token is rejected | **unit only** | `AntiforgerySecurityTests`, `CredentialSchemeTests` — no E2E equivalent |
| The XSRF cookie carries `Secure` and `SameSite=Strict` | **E2E** | `XsrfCookieFlagTests` (2 tests) |
| Non-ambient credential is exempt from antiforgery | **unit** | `CredentialSchemeTests` |
| A refused credential does **not** suppress the antiforgery gate | **unit** | `CredentialSchemeTests` |
| A refused credential is indistinguishable from anonymity | **unit** | `AuthenticationOutcomeTests` (3 tests) |
| Composite is the default authenticate scheme; sign-in/challenge are not | **unit** | `SparkBuilderExtensionsTests` (3 tests) |
| Row-level filtering on get-by-id, list and child query | **E2E** | `RowLevelAuthzTests` (3 tests) |
| Row-level filtering on a **stream** | **unit only** | `RowLevelQueryAuthorizationTests` — no E2E |
| `DenyAllAccessControl` denies an unconfigured app | **unit only** | `PermissionServiceDefaultsTests` |
| Replication endpoints refuse an unauthenticated caller | **E2E** | `ReplicationEndpointAuthTests` (3 tests) |
| Development-mode mTLS still verifies module registration | **unit** | `ModuleCertificateValidatorTests` |
| A JWT credential with no audience is refused at startup | **unit** | `CredentialHandlerRegistrationTests` (7 tests) |
| A forwarded certificate header is stripped from untrusted peers | **unit** | `CertificateForwardingTrustTests` (3 tests) |

### Gaps, stated rather than implied

- **Only Fleet has an E2E host.** DemoApp, HR and WebhooksDemo are not exercised end to end at all,
  so "login works" is verified for one demo of four.
- **No E2E test authenticates with a bearer token or a certificate.** M10 built both schemes and
  their guards are tested, but no demo registers either, so nothing exercises a real credential
  end to end. `CredentialSchemeTests` proves the composite plumbing works for *whatever* handler is
  registered, using a stub. Closing this needs a demo that opts in — worth doing when M11 makes the
  replication endpoints rely on the certificate identity rather than their own check.
- **A rejected credential's *authorization* outcome is untested.** `AuthenticationOutcomeTests`
  proves the principal is anonymous, and `CredentialSchemeTests` proves the antiforgery gate stays
  armed — but nothing asserts that a garbage bearer token yields exactly the `anonymous` group's rights on a real
  permission check.
- **Logout is not proven to revoke anything.** `LogoutTests` asserts that `SignOutAsync` was *called*
  with the right scheme, against a mock. No test logs in, logs out, and retries with the stale
  cookie.
- **A 2FA-pending principal is not tested against a Spark endpoint.** It is excluded by construction
  — the two-factor schemes are never registered as credentials, so the composite never consults them
  — which is a strong argument but not a test. The equivalent guarantee *is* tested for the identity
  provider's own endpoints (21 tests, §L.4 of the [IdP matrix](./idp-e2e-test-matrix.md)), which is
  a different subsystem and should not be read as covering this one.
- **No test exercises a *successful* cross-module sync end to end.** `SyncActionSubscriptionWorkerE2ETests`
  posts to a stub handler and `ReplicationEndpointAuthTests` asserts only refusals, so M11's routing
  change is verified by unit tests on the routing rather than by a working round trip. This is the
  least-covered change in the PR and the one most likely to affect an existing deployment.

### ~~N23 — validation precedes authorization on the create path~~ — fixed in M11.4

`CreatePersistentObject` validates the posted object (`Create.cs:62`) before the authorization check,
which lives inside `SavePersistentObjectAsync` (`:68`). A caller with no right to create an entity
type therefore gets a **400 with validation errors** when the payload is malformed, and only reaches
`401`/`403` when it is well-formed.

The refusal itself is never in doubt. What leaks is which attributes an entity type requires, for a
type the caller cannot create — a mild oracle, and inconsistent with the standard
`NotFoundVsForbiddenTests` exists to hold. Pinned as current behaviour in
`AnonymousPersistentObjectAccessTests.Anonymous_cannot_create_a_Company_despite_being_able_to_read_them`
so the reorder is a visible, deliberate change when it happens.

**Fixed in M11.4.** `IDatabaseAccess.EnsureSaveAuthorizedAsync` lets `Create` and `Update` authorize
before validating. It is not a second copy of the rule: `SavePersistentObjectAsync` calls the same
method, so there is one implementation of the decision and the chokepoint stays authoritative — the
endpoint only asks it earlier. The E2E test that pinned the old 400 now asserts 401.

---

## Choosing how much of the local-credential surface to mount

`spark.AddAuthentication<TUser>()` mounts ASP.NET Core Identity's endpoint family under
`/spark/auth`. An application that signs users in exclusively through an external provider does not
want most of it, and `SparkLocalCredentials` chooses how much is mapped:

```csharp
spark.AddAuthentication<SparkUser>(
    configure: auth => auth.LocalCredentials = SparkLocalCredentials.Disabled,
    configureProviders: identity => identity.AddGitHub(options => { /* … */ }));
```

| Mode | Mapped | Not mapped |
|---|---|---|
| `Full` (default) | everything below | — |
| `SignInOnly` | `login`, `refresh`, `forgotPassword`, `resetPassword`, `confirmEmail`, `manage/2fa`, `GET|POST manage/info` | `register`, `resendConfirmationEmail` |
| `Disabled` | `manage/2fa`, `GET manage/info` | `register`, `login`, `refresh`, `confirmEmail`, `resendConfirmationEmail`, `forgotPassword`, `resetPassword`, `POST manage/info` |

`/spark/auth/me`, `/spark/auth/logout`, `/spark/auth/csrf-refresh`, `/spark/auth/external-login` and
`/spark/auth/external-login-callback` are mapped in **every** mode. **External login providers are
never affected by this setting** — it gates local passwords only, and `Disabled` in fact *requires*
at least one provider (see below).

The excluded endpoints are absent from the route table, not shadowed behind middleware that returns
404. They do not appear in the endpoint data source or in OpenAPI.

### Why a mode rather than one switch per endpoint

Closing `register` alone is the obvious move and it is not enough. `register` is a loud
account-enumeration oracle — an existing address comes back `400 DuplicateUserName`, a new one
`200`. But `forgotPassword`, `resetPassword`, `confirmEmail` and `resendConfirmationEmail` stay live
and, while ASP.NET Core returns a uniform response from each, they retain a timing side-channel and
remain an unauthenticated mail-send trigger keyed on an email address. `login` distinguishes
`LockedOut` and `NotAllowed`, which are reachable only for an account that exists.

On the client the same conclusion arrives by a different route: the pages form a star centred on the
login page, and every template dereferences its siblings' paths unconditionally, so removing any
proper subset leaves a dangling `routerLink`. The family is the unit on both tiers.

### `Disabled` requires an external provider

Mapping throws at startup if `LocalCredentials` is `Disabled` and no interactive provider is
registered. The alternative is an application that boots healthy and that nobody can sign into, with
a symptom — a sign-in page with no buttons — that points nowhere near the cause.

A scheme counts as interactive when it carries a `DisplayName`. That is what separates `AddGitHub` /
`AddGoogle` from Identity's internal cookies and from machine-caller schemes such as API tokens or
JWT bearer, which would be a dead end if offered as a sign-in button.

### The client half

Opt into the pages the server actually mounts, and point `loginUrl` at a route that exists:

```ts
// app.routes.ts — external sign-in only
...sparkAuthRoutes(withExternalLogin(githubProvider())),

// app.routes.ts — password sign-in as well
...sparkAuthRoutes(withLocalLogin(), withRegistration(), withExternalLogin(githubProvider())),

// app.config.ts
provideSparkAuth({ loginUrl: '/sign-in' }),
```

**Nothing is mounted unless a feature asks for it** — `sparkAuthRoutes()` with no arguments emits no
pages at all. `withExternalLogin(...)` routes `SparkSignInComponent` at `/sign-in`; it renders one
button per provider, read from `GET /spark/auth/capabilities` rather than from a hard-coded scheme
name. A `githubProvider()` declaration only *decorates* that button — icon, label, ordering, keyed by
scheme — so it cannot conjure a provider the server does not have, and a provider the server adds
later gets a default button rather than disappearing.

The client and server halves are configured independently and can disagree without anything failing,
which is why the sign-in page warns in development when it is routed against a server still reporting
`localCredentials = Full`.

`loginUrl` is the single target the guard, the interceptor and `SparkAuthBarComponent` all redirect
to. Nothing connects it to the routes that exist, so pointing it at an unregistered route used to
produce a redirect into a blank page with no error at all; it now warns once in development.

### Discovering the configuration at runtime

`GET /spark/auth/capabilities` is anonymous and reports both halves:

```json
{ "localCredentials": "Disabled", "externalProviders": [{ "scheme": "GitHub", "displayName": "GitHub" }] }
```

The mode is derived from the route table rather than read back from configuration, so it cannot claim
a surface that was never mapped. Use it to check that the client's build-time route configuration and
the server's deployment-time mode agree — that mismatch is otherwise invisible until a user hits it.

### Notes

- `MintPlayer.Spark.IdentityProvider` honours the same mode: `Disabled` also drops `/connect/login`
  and `/connect/two-factor`. Every OIDC protocol endpoint is untouched, since a provider that
  federates upstream still needs all of them.
- `POST /manage/info` counts as local-credential in `Disabled` mode because it rotates the email the
  external binding was provisioned against, desynchronizing it from the issuer-attested claim.
  `GET /manage/info` is kept.
- The Bearer credential scheme stays registered in `Disabled` mode, but nothing can issue a bearer
  token once `POST /login` is gone.
- This is a surface control, not a rate limit. `spark.AddRateLimiter()` remains worth enabling for
  whatever surface you do mount — see [rate limiting](./guide-rate-limiting.md).

---

## The `user:email` scope is not optional for GitHub

`AddGitHub` requests `user:email` by default, and auto-provisioning depends on it.

Spark refuses to create an account for an external identity unless the issuer **attested** the email
address. GitHub's `/user` endpoint returns whatever the user set as primary, verified or not, so the
attestation comes from `/user/emails` — which an OAuth App token cannot read without `user:email`.
Without the scope there is no `urn:github:email_verified` claim, and first-time sign-in fails with
`email_not_verified`.

Two provider types, two different things to check:

| | What grants the email read |
|---|---|
| **OAuth App** | the `user:email` scope, requested by default since preview.59 |
| **GitHub App** | scopes are ignored entirely; grant the **"Email addresses: Read-only"** account permission |

A non-success response from `/user/emails` is logged at Warning naming both. Signing in to an
*already-linked* account is unaffected — only first-time binding is gated — which is why this can look
fine in a long-running app and fail for every new user.

It matters most in `SparkLocalCredentials.Disabled`: there are no local accounts to fall back on, so a
provisioning failure means nobody can sign in at all.

---

## See also

- [Authorization package](../libs/authorization/MintPlayer.Spark.Authorization/README.md) — `security.json`, groups, rights syntax
- [Replication mTLS](./guide-replication-mtls.md) — cross-module client certificates
- [HTTP API Specification](./Spark-API-Specification.md) — per-endpoint auth requirements
- [findings-replication-mtls.md](./findings-replication-mtls.md) — F1–F11, the analysis this guide's §3 summarises
