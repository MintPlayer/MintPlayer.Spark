# Release notes — `10.0.0-preview.42`

Packages: all `MintPlayer.Spark.*` at `10.0.0-preview.42`; `@mintplayer/ng-spark-auth` at `22.1.0`
(`@mintplayer/ng-spark` is unchanged at `22.0.8`).

This release is a security-hardening pass. Spark is in preview and compatibility was not a
constraint, so several things that used to be permitted are now refused. **Most of the changes below
were fail-open behaviours** — a request that should have been rejected and wasn't — which means the
upgrade can surface as *"my app stopped working"* where the honest description is *"my app was
serving requests it should not have been."* Each entry says which.

Read [Authentication Schemes & `Everyone`](./guide-authentication-schemes.md) alongside this if you
use authentication at all; it is the reference for what an unauthenticated caller gets, what a
*rejected* credential gets, and where `security.json` applies.

---

## Start here — the four that will bite

### 1. Two-factor recovery codes are now hashed

**Existing recovery codes stop working. Every user with 2FA enabled must regenerate them.**

This is the only change in the release that touches a persisted user format. Codes were stored in
plaintext, so a database dump was a dump of working second-factor bypasses. There is no migration:
a hash cannot be derived from data we deliberately no longer keep. Tell your users before upgrading,
not after.

### 2. Cross-module replication is authorized

If you use `MintPlayer.Spark.Replication`, **`/spark/sync/apply` and `/spark/etl/deploy` now refuse
a module that has no rights in the owner's `security.json`.** Previously the write path skipped
Spark's authorization chokepoint entirely, so any module that could authenticate could write
anything anywhere, and could have any collection — including `SparkUsers` — continuously pushed
into a database it controls.

Grant each consuming module explicitly, using the `Module:{Name}` group the certificate handler
emits:

```json
{ "id": "…", "resource": "Replicate/Cars",   "groupId": "…module-hr…", "isDenied": false },
{ "id": "…", "resource": "ReadEditNew/Car",  "groupId": "…module-hr…", "isDenied": false }
```

`Replicate/{Collection}` governs what a module may *read* via ETL; the ordinary CRUD rights govern
what it may *write* via sync. See `Demo/Fleet` and `Demo/HR` for both halves.

Also: a sync action against an entity type Spark does not know is now refused rather than written
through a reflection fallback. It has no name for `security.json` to grant rights on, so no
authorization decision exists — and unevaluable is not the same as permitted.

### 3. The default authenticate scheme is now Spark's composite

Spark's endpoints name no authentication scheme, so only the *default* one ever ran. Any credential
scheme you registered beyond it — a certificate handler, a bearer handler — was **dead code on every
Spark endpoint**, and its caller arrived anonymous with `Everyone` rights. Silently.

Spark now installs a composite authenticate scheme that tries each registered credential scheme.
Two consequences:

- If you set `DefaultAuthenticateScheme` yourself, Spark overrides it.
- A **refused** credential is now logged and reported as a failure. It used to be indistinguishable
  from presenting none — both landed on anonymous-with-`Everyone`.

`UseAuthentication()` also now runs whenever any credential scheme is registered, not only when
Identity is configured, so a machine-to-machine app with no user store authenticates its callers.

### 4. Row-level rules now apply to lists, queries and streams

If any Actions class overrides `IsAllowedAsync(string action, T entity)`, **your list screens will
show fewer rows than before, and `totalItems` will be smaller.** That is the fix: the rule was
enforced when opening a record and skipped on the screen that lists them, so an entity carefully
scoped to its owner was disclosed in full on the list page.

**This is not a signature change** — nothing to migrate, no override to rewrite. Points worth
knowing:

- Filtering happens after materialization, so a row-scoped type reads its whole collection per
  query. Raven-side pushdown is a known follow-up.
- Unverifiable rows are now dropped rather than shown: a projection with no readable `Id`, or one
  whose base document no longer loads. If a row-scoped type is backed by a projection that does not
  store `Id`, its queries now return **nothing** — add the field to the index.
- Streams may emit mid-stream `remove` patches as rows become invisible.

---

## OIDC / IdentityProvider

Only relevant if you use `MintPlayer.Spark.IdentityProvider`.

| Change | What breaks |
|---|---|
| `SparkIdentityProviderOptions.Issuer` is **required** outside Development | Startup throws if unset. It used to come from the `Host` header — which the caller controls, so a forged header minted tokens claiming any issuer, signed with your real key |
| `/connect/logout` requires `client_id` | Logout without it is refused. `post_logout_redirect_uri` was validated against *every* enabled application, making one client's registered URI a legal destination for all of them |
| `client_credentials` requires an explicit `scope` | Omitting it used to grant the client's entire authority |
| `client_credentials` refuses an undefined or disabled scope | Was silently narrowed; now `400 invalid_scope`. There is no user and no consent step here, so issuing less than asked produces a client that fails later, far from the cause |
| Refresh tokens require `offline_access` | Clients that never asked stop receiving one. Every browser client was silently issued a 14-day credential it could not decline |
| `id_token` requires `openid` | A client wanting only API access no longer receives a signed identity assertion |
| Scopes must have an enabled `OidcScope` | Authorize refuses an undefined scope. Authorize and issuance used to validate against different sources, so a granted scope could vanish from the token |
| `AllowedGrantTypes` no longer defaults | A client declaring no grants can use none. The default was re-added by the serializer on load, making every grant restriction unenforceable |
| Token responses announce `scope` when narrowed | New key in code and refresh responses (RFC 6749 §5.1), so a client can tell it got less than it asked for |
| `OidcToken.Scopes` records **granted**, not requested, scopes | Introspection reports less than before wherever a scope was undefined or disabled |
| A withdrawn grant refuses issuance | Clients must re-authorize after a user removes access. Consent was recorded and consulted nowhere; re-consent used to resurrect tokens that a revocation sweep had killed |
| Relative and `file:` redirect URIs are refused | A client registered with `/callback` **on Linux** stops saving. It was already impossible to use — validation just accepted it on Unix and rejected it on Windows |
| The `/connect` endpoints are rate-limited | Where the limiter is enabled. It was scoped to `/spark`, so an app opting in still shipped an unthrottled password endpoint |
| Introspection is audience-gated | A resource server only sees tokens for its own audience |

---

## Authentication and authorization

| Change | What breaks |
|---|---|
| A non-ambient credential is exempt from antiforgery | Bearer and certificate callers can POST without an `XSRF-TOKEN` cookie. CSRF is an attack on ambient authority; demanding a token of a caller that cannot be made to send one protected nothing and made external POSTs impossible |
| `AddJwtBearerCredential` throws without an `Audience` | You cannot register the scheme unconfigured. Skipping audience validation accepts every token the issuer minted, for any resource, because the signature is genuine |
| `AddModuleCertificateForwarding` throws without a `KnownProxies` entry | Forwarding cannot be enabled without naming the proxy. A forwarded certificate is a plain header; accepting it from anywhere lets any caller claim any module identity |
| `spark.UseGroupMembershipProvider<T>()` added | New — the documented extension point previously had no public registration API |
| Authorization now precedes validation on create/update | An unauthorized caller gets 401/403 where a malformed payload previously returned 400 with validation errors. Those errors told a caller who may not create a type which of its attributes were required |
| An Actions-class refusal is a 400, not a 500 | `SparkValidationException` maps into the standard `errors` envelope. Framework-wide, not IdP-only — business validation previously had no path to the user, so every message reached the operator as an empty 500 |
| **Property-level rights never worked** | Doc fix, not a behaviour change: `"Edit/Person/Salary"` parses and matches nothing. Scope a property through the Actions class instead |

---

## API changes

| Change | Migration |
|---|---|
| **`IDatabaseAccess`'s untyped document family is renamed `…UncheckedAsync`** | Rename every call: `GetDocumentAsync` → `GetDocumentUncheckedAsync`, and likewise for `GetDocumentsAsync`, `GetDocumentsByObjectTypeIdAsync`, `SaveDocumentAsync`, `DeleteDocumentAsync`. They perform **no** authorization while sitting beside `SavePersistentObjectAsync`, which invited the inference that anything on that interface is authorized |
| **`IPersistentObjectActions<T>.OnQueryAsync` is removed** | Any override stops compiling — and it was never running. The framework declared the hook and called it from nowhere, so an Actions class scoping rows there was writing dead code. `Demo/WebhooksDemo` did exactly that and leaked its whole project list. **If you override it, move the logic to `IsAllowedAsync(string action, T entity)`**, which every read path consults |
| `IDatabaseAccess` gains `EnsureSaveAuthorizedAsync` | Any hand-written implementation must add it |
| `IOidcApplicationContext` members are get-only | An auto-property implementation stops compiling. It returned null, and a null queryable answers as an empty result — screens that render and are always empty |
| `SparkTestDriver` applies Spark's id conventions | Downstream test projects get `{Collection}/{Guid}` where they previously got RavenDB's sequential ids |
| New package dependencies | `MintPlayer.Spark.Replication` gains `Microsoft.AspNetCore.Authentication.Certificate`; `MintPlayer.Spark.Authorization` gains `.JwtBearer`. Both are opt-in at the API level |

---

## Frontend

| Change | Migration |
|---|---|
| **The external-login popup message changed shape** | `{ type: 'external-login-success' }` became `{ type: 'spark:external-login', success, error? }`. If you hand-rolled a listener, replace it with `SparkAuthService.loginWithProvider(provider, { returnUrl })` — it owns the whole handshake and, unlike a hand-rolled version, settles on a blocked popup and on one the user closed |
| The external-login callback reports refusals to a popup | With `?popup`, all three failure branches now post a message instead of redirecting. A cancelled login used to leave the opener waiting on a window nobody would close |
| **ng-bootstrap 22.13 needs `@mintplayer/web-components` ≥ 2.5** | An app on `web-components@2.0.x` **fails to build**: `Missing "./accordion" specifier`. Update it — `npm update @mintplayer/web-components`. ng-bootstrap's peer range (`^2.0.0`) admits versions lacking the subpaths it imports, so a satisfied range is not a working one |
| **`BsAccordionTabHeaderComponent` → `*bsAccordionTabHeader`** | ng-bootstrap 22.13 made it a structural directive: `<ng-container *bsAccordionTabHeader>…</ng-container>`, and swap the symbol in your `imports`. Affects consumers of the demo shell pattern, not `@mintplayer/ng-spark` |

---

## Added

- **Messaging retry policy is configurable.** `AddMessaging` now binds the `Spark:Messaging` section
  (`MaxAttempts`, `BackoffDelays`, `FallbackPollInterval`, `RetentionDays`), so a durable bus can be
  tuned per environment instead of only from a C# delegate compiled into `Program.cs`. Code still wins
  over configuration.
  - *Minor observable change:* `SparkMessagingOptions.BackoffDelays` now defaults to **empty**, with
    the schedule applied by `ResolvedBackoffDelays`. Anything reading the raw property directly sees
    `[]` rather than the five delays. This is deliberate — .NET's binder *appends* to a non-empty
    collection, so an initialised default would have survived binding and stayed first, silently
    overriding a configured schedule (the same defect as `SparkModulesUrls` below).
- **`spark.UseGroupMembershipProvider<T>()`** — a supported registration path for a custom
  `IGroupMembershipProvider`; the documented extension point previously had only an `internal` helper.
- **`SparkFullOptions.Configure`** — an escape hatch to the `ISparkBuilder` for apps using
  `AddSparkFull`, which previously could not register a credential scheme at all.

## Fixed

- **Generic message types produced invalid queue names**, taking the host down at startup. A closed
  generic's `FullName` embeds assembly-qualified type arguments, so `GitHubWebhookMessage<PushEvent>`
  derived a name full of `[`, `]`, `,` and spaces. Queue names are now composed from the generic
  definition plus each argument's own derived name. If you were relying on a derived queue *name*,
  don't — route by CLR type.
- **Redirect-URI validation failed open on Linux.**
- **A configured `SparkModulesUrls` never took effect.** It initialised to
  `["http://localhost:8080"]`, and .NET's configuration binder *appends* to a non-empty collection
  rather than replacing it — so the configured URL landed second and `DocumentStore` connected to the
  first. Every deployment that configured where its module registry lived was still talking to
  localhost, silently, with a config file that said otherwise. **If you run replication, verify which
  server your modules actually registered with.**
- **The dev-tunnel allow-list failed open**: an empty `AllowedDevUsers` meant "any authenticated
  GitHub user", who could then subscribe to every private-repo webhook the dev app receives. Empty
  now means nobody.
