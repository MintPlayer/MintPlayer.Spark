# PRD — Passkeys (WebAuthn) in Spark, and on CodeCoverage (#439)

Issue: [#439](https://github.com/MintPlayer/MintPlayer.Spark/issues/439) — "passkeys - already
supported?". The issue body is empty; §1 below is what it should say.

---

## 1. Verification summary — the question, answered

> *Does the Spark framework already support passkeys? It's already supported in AspnetCore.Identity,
> do we need to add custom code to integrate with spark?*

**No, and yes.** Measured, not recalled:

| Claim | Verdict |
|---|---|
| Spark supports passkeys today | **Refuted.** A case-insensitive sweep for `passkey`, `webauthn`, `fido`, `PublicKeyCredential`, `navigator.credentials`, `attestation` over the whole repository returns **zero first-party hits**, server or client. The only matches anywhere are inside Playwright's bundled CDP typings under `tests/MintPlayer.Spark.E2E.Tests/bin/`. |
| ASP.NET Core Identity supports passkeys | **Confirmed, and it is GA — not preview.** `Microsoft.Extensions.Identity.Core` **10.0.12** ships `IUserPasskeyStore<TUser>`, `UserPasskeyInfo`, `IPasskeyHandler<TUser>`, `IdentityPasskeyOptions`, and passkey members on `UserManager`/`SignInManager`. Nothing is `[Experimental]` or `[RequiresPreviewFeatures]`. |
| Custom code is needed | **Confirmed, and more than expected.** Three of the four layers are missing entirely. |

⚠️ **Two assumptions that a reasonable person would make, and that are both false.** Both were
measured, and both change the design:

1. **`MapIdentityApi<TUser>()` does not map any passkey endpoint.** Measured by building a real host,
   calling `MapIdentityApi` and enumerating `EndpointDataSource`: the complete route set is
   `register`, `login`, `refresh`, `confirmEmail`, `resendConfirmationEmail`, `forgotPassword`,
   `resetPassword`, `manage/2fa`, `manage/info` (GET+POST). **No `/passkey/*` of any kind.** The HTTP
   surface must be hand-written. `IdentityApiEndpointRouteBuilderExtensions` has exactly one public
   member. *(This also refutes a worry raised during investigation — that Spark's allow-by-default
   route filter is already publishing passkey endpoints. It is not, because there are none to
   publish. The filter fragility is real but separate; see §5.9.)*

2. **`IPasskeyHandler`'s ceremony state is unprotected plaintext.** `MakeCreationOptionsAsync` and
   `MakeRequestOptionsAsync` return an `AttestationState` / `AssertionState` string that is *literally
   readable JSON containing the challenge*:

   ```json
   {"challenge":"h1vnj1gREhqnQ88ROhhN2rQ2qUgf8EoLo16ILwUS2cA=",
    "userEntity":{"id":"Users/1-A","name":"user@example.com","displayName":"Example User"}}
   ```

   Not encrypted, not signed, no expiry, no session binding. This is §4 — the single most important
   fact in this document.

**What exists to build on:** `UserStore<TUser>`
(`libs/authorization/MintPlayer.Spark.Authorization/Identity/UserStore.cs:16`) already implements 14
Identity store interfaces against RavenDB, with a cluster-safe compare/exchange idiom for email
uniqueness that a credential-ID index can copy directly. `GET /spark/auth/capabilities`
(`Endpoints/GetAuthCapabilities.cs:27`) is an established server-driven capability contract the
client already consumes. `SparkCredentialInventory` already answers "would this leave the user with
no way back in?".

---

## 2. Problem

CodeCoverage (production, coverage.mintplayer.com) authenticates **only** through GitHub OAuth.
`SparkLocalCredentials` is never set by the app, so it takes the framework default `Disabled`
(`Configuration/SparkAuthenticationOptions.cs:43`) and `/spark/auth/login`, `/register`,
`/forgotPassword`, `/resetPassword` are not mapped at all. There is no password, no profile page, and
no user-identity resource in `security.json`.

That is a deliberate, good posture — but it means:

- **A single point of failure.** If a user loses access to GitHub, or GitHub's OAuth is down, or the
  GitHub App installation is suspended, they cannot sign in at all.
- **Every sign-in is a third-party round trip** with a full redirect, and the OAuth token is stored
  and refreshed per user (`GitHubUserTokenService.cs:44+`) purely to keep the session usable.
- **There is no phishing-resistant credential.** GitHub OAuth is as strong as the user's GitHub
  account, which may be password + TOTP.

A passkey is the right second credential: phishing-resistant by construction (the RP ID is bound by
the browser, not typed by the user), no shared secret on the server, and — with user verification
required — a single gesture that is both possession and knowledge.

**Why it belongs in the framework, not the app.** The store, the user entity and the auth endpoints
all live in `MintPlayer.Spark.Authorization`. CodeCoverage cannot add `IUserPasskeyStore` to a
`UserStore<TUser>` it does not own. So the work is: build it in Spark, opt into it from CodeCoverage.

---

## 3. What .NET 10 actually gives us — the measured contract

Measured against `Microsoft.AspNetCore.App` **10.0.12** by runtime reflection and a live host.

### 3.1 The store contract — the whole persistence surface

```csharp
public interface IUserPasskeyStore<TUser> : IUserStore<TUser>, IDisposable where TUser : class
{
    Task                         AddOrUpdatePasskeyAsync(TUser user, UserPasskeyInfo passkey, CancellationToken ct);
    Task<TUser?>                 FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct);
    Task<UserPasskeyInfo?>       FindPasskeyAsync(TUser user, byte[] credentialId, CancellationToken ct);
    Task<IList<UserPasskeyInfo>> GetPasskeysAsync(TUser user, CancellationToken ct);
    Task                         RemovePasskeyAsync(TUser user, byte[] credentialId, CancellationToken ct);
}
```

Five methods. `UserManager.SupportsUserPasskey` is just `Store is IUserPasskeyStore<TUser>`.

`UserPasskeyInfo` splits cleanly into write-once and mutable state, and the split is the whole
storage design:

| Write-once at registration | Mutable, must be re-persisted on every assertion |
|---|---|
| `CredentialId`, `PublicKey`, `CreatedAt`, `Transports`, `AttestationObject`, `ClientDataJson`, `IsBackupEligible` | `SignCount`, `IsUserVerified`, `IsBackedUp`, `Name` |

### 3.2 The handler, and the two ways to drive it

```csharp
public interface IPasskeyHandler<TUser> where TUser : class
{
    Task<PasskeyCreationOptionsResult>  MakeCreationOptionsAsync(PasskeyUserEntity userEntity, HttpContext ctx);
    Task<PasskeyRequestOptionsResult>   MakeRequestOptionsAsync(TUser? user, HttpContext ctx);
    Task<PasskeyAttestationResult>      PerformAttestationAsync(PasskeyAttestationContext ctx);
    Task<PasskeyAssertionResult<TUser>> PerformAssertionAsync(PasskeyAssertionContext ctx);
}
```

`PasskeyHandler<TUser>` (the default implementation) is **`sealed`**, and its
`PerformAttestationCoreAsync` / `PerformAssertionCoreAsync` are private. **There is no subclassing
extension point.** You either implement the interface from scratch or configure through
`IdentityPasskeyOptions`. There is also **no `IdentityBuilder.AddPasskeyHandler<T>()`** — replacing
the handler is a plain `services.AddScoped<IPasskeyHandler<TUser>, T>()`.

`IPasskeyHandler<TUser> -> PasskeyHandler<TUser>` is registered **Scoped** by `AddSignInManager()`,
which `AddIdentityApiEndpoints<TUser>()` calls — so Spark already has it in the container today
(`SparkAuthenticationExtensions.cs:50-55`), unused.

`SignInManager<TUser>` wraps the handler and **handles the ceremony state for you**:

```csharp
Task<string>                        MakePasskeyCreationOptionsAsync(PasskeyUserEntity userEntity);
Task<string>                        MakePasskeyRequestOptionsAsync(TUser? user);
Task<PasskeyAttestationResult>      PerformPasskeyAttestationAsync(string credentialJson);
Task<PasskeyAssertionResult<TUser>> PerformPasskeyAssertionAsync(string credentialJson);
Task<SignInResult>                  PasskeySignInAsync(string credentialJson);
```

These return only the options JSON and park the state server-side in a **DataProtection-protected
cookie** — measured: one call emitted exactly one `Set-Cookie`, `Identity.TwoFactorUserId=CfDJ8…`,
i.e. it reuses `IdentityConstants.TwoFactorUserIdScheme` with `AuthenticationProperties` keys
`PasskeyOperation` and `PasskeyState`.

### 3.3 Options, and their real defaults

Read off a live `IOptions<IdentityPasskeyOptions>`:

| Option | Default | Consequence |
|---|---|---|
| `ServerDomain` (RP ID) | **`null`** → derived from the request `Host` | §5.8 — load-bearing behind a proxy |
| `ChallengeSize` | `32` | fine |
| `AuthenticatorTimeout` | `00:05:00` | fine |
| `UserVerificationRequirement` | `"required"` | good — keep |
| `ResidentKeyRequirement` | `"preferred"` | discoverable credentials → usernameless sign-in |
| `AuthenticatorAttachment` | `null` | both platform and roaming authenticators allowed |
| `AttestationConveyancePreference` | `null` → browser sends `"none"` | we do not collect attestation |
| `IsAllowedAlgorithm` | `null` → the 9 COSE algorithms in the wire dump | includes RS1 (`-65535`)? **no** — measured set is ES256/384/512, PS256/384/512, RS256/384/512 |
| `ValidateOrigin` | `null` → framework default (origin must match the request) | |
| **`VerifyAttestationStatement`** | **`null` → the attestation statement is NOT verified** | a stated decision, D6 |

### 3.4 The wire shape, measured

`MakeCreationOptionsAsync` for host `coverage.mintplayer.com`:

```json
{"rp":{"name":"coverage.mintplayer.com","id":"coverage.mintplayer.com"},
 "user":{"id":"VXNlcnMvMS1B","name":"user@example.com","displayName":"Example User"},
 "challenge":"h1vnj1gREhqnQ88ROhhN2rQ2qUgf8EoLo16ILwUS2cA",
 "pubKeyCredParams":[…9 algorithms…],
 "timeout":300000,"excludeCredentials":[],
 "authenticatorSelection":{"residentKey":"preferred","requireResidentKey":false,"userVerification":"required"},
 "hints":[],"attestationFormats":[]}
```

`user.id` is base64url of `PasskeyUserEntity.Id` — i.e. whatever we pass. `VXNlcnMvMS1B` decodes to
`Users/1-A`. See D7.

### 3.5 Client contract

`Microsoft.AspNetCore.Identity.UI` 10.0.5 has **no** passkey support (all 30 of its `.js` files
scanned: zero hits). The only WebAuthn JS Microsoft ships is in the Blazor Web App template —
`PasskeySubmit.razor.js` — which uses the **native** `PublicKeyCredential.parseCreationOptionsFromJSON`
/ `parseRequestOptionsFromJSON` helpers, so no base64url shim is needed, at the price of refusing
older browsers. It is a form-associated custom element that piggybacks a form POST; the ceremony is
reusable, the delivery mechanism is not (Spark is a zoneless Angular SPA).

### 3.6 Forward compatibility

Diffed the passkey surface between the `net10.0` and `net11.0-rc.1` ref packs: **exactly two
additions, nothing removed or renamed** — `UserPasskeyInfo.Aaguid` and `IdentityPasskeyData.Aaguid`.
An implementation written against net10.0 carries forward unchanged.

---

## 4. The security problem at the centre

**The obvious design is the insecure one, and nothing in the API stops you building it.**

A SPA talking to minimal APIs invites a stateless ceremony: `POST /creation-options` returns
`{optionsJson, state}`, the client keeps `state`, and posts it back with the credential. The API is
shaped to allow exactly this — `PasskeyAttestationContext.AttestationState` is a `string` the caller
supplies.

Measured, that state is:

```
AttestationState (147 chars, plaintext JSON):
{"challenge":"h1vnj1gREhqnQ88ROhhN2rQ2qUgf8EoLo16ILwUS2cA=",
 "userEntity":{"id":"Users/1-A","name":"user@example.com","displayName":"Example User"}}

AssertionState (60 chars, plaintext JSON):
{"challenge":"OsktI/PYh2b/9NM4Tm8qCbpM5G2bRS++NlonwBWjwQs="}
```

If the client supplies that value, an attacker controls it. Two concrete attacks:

- **Assertion replay.** The challenge is the entire anti-replay mechanism of WebAuthn. An attacker who
  can set it can replay a captured `navigator.credentials.get()` response indefinitely — the signature
  over a challenge they chose still verifies. Sign-in is forgeable.
- **Passkey injection → account takeover.** The attestation state carries `userEntity.id`, and the
  handler binds the new credential to *that* id. An attacker posts an attestation from their own
  authenticator with `userEntity.id` rewritten to the victim's user document id, and now owns a
  credential that signs in as the victim. This is worse than replay: it is persistent.

Neither attack needs to break any cryptography. They need only the ability to edit a JSON field.

**The rule this PRD imposes:** *the ceremony state never leaves the server in a form the client can
choose.* Two acceptable implementations; we take the first.

| | How state is bound | Code we own | Verdict |
|---|---|---|---|
| **A. `SignInManager` helpers** | Framework's own DataProtection-protected cookie, keyed to the browser session | none | **Chosen (D1)** |
| B. `IPasskeyHandler` + our own `IDataProtector` | We wrap the state with purpose string, expiry and a session binding, then round-trip it | a protector, an expiry, a binding, and tests for all three | Fallback only |

Option A means Spark writes **no cryptographic code at all** for the ceremony. That is the whole
argument: the correct amount of bespoke crypto in this feature is zero. Option B is held in reserve
for one scenario only — a future token-only/cross-origin client where no cookie is available — and if
we ever take it, the wrapper is the security-critical unit and gets its own adversarial tests.

⚠️ Option A has one sharp edge, and it is a spike (SP1): `SignInManager` stores the state under
`IdentityConstants.TwoFactorUserIdScheme`. Spark registers Identity through
`AddIdentityApiEndpoints<TUser>()`, **not** `AddIdentity<TUser,TRole>()`, and the two register
different scheme sets. If that scheme is absent, the first call throws at runtime rather than at
startup. Investigation observed a successful `Set-Cookie` under `AddIdentityApiEndpoints`, so the
expectation is that it works — but "observed once in a probe" is not "asserted by a test", and this
gates the whole design.

---

## 5. Design

### 5.1 Storage — embedded list + a hashed compare/exchange index

`SparkUser` gains one property, matching the shape of its existing `Logins` / `Tokens` /
`TwoFactorRecoveryCodes` collections:

```csharp
public List<SparkUserPasskey> Passkeys { get; set; } = [];
```

`SparkUserPasskey` mirrors `UserPasskeyInfo` field-for-field (and is *not* `IdentityUserPasskey<TKey>`
— that type lives in `Microsoft.Extensions.Identity.Stores` and is shaped for EF's `DbSet`, with a
`UserId` back-pointer we do not want in an embedded document).

`byte[]` round-trips through RavenDB as base64, which is fine. With `attestation: "none"` the stored
`AttestationObject` is a few hundred bytes, so a handful of passkeys per user does not meaningfully
grow the `Users` document.

**`FindByPasskeyIdAsync(byte[] credentialId)` is the hard part**, because it is a *global* lookup
across all users, from a value embedded in a child collection. Three options:

| Option | Why not |
|---|---|
| `Query<TUser>().Where(u => u.Passkeys.Any(p => p.CredentialId == id))` | Relies on a RavenDB auto-index over an embedded byte array; the store already documents (`UserStore.cs:370`, `:525`) that Raven cannot auto-index `.Any()` over multiple fields, and this is an **index read on the sign-in hot path** — stale results mean a valid passkey is rejected. |
| A `SparkIndexCreationTask` over the collection | Correct, but still eventually consistent, and `libs/authorization/` deliberately contains no index classes today. |
| **Compare/exchange reservation** | **Chosen.** Strongly consistent, cluster-safe, and *the same idiom the store already uses for email* (`UserStore.cs:33`, `:614-615`, `:246-262` — which explicitly reads the reservation and then `LoadAsync`es, to avoid a stale-index read). It buys global uniqueness for free. |

**Key shape.** A credential ID may be up to 1023 bytes per spec; base64url of that is 1364 chars,
which will not fit a compare/exchange key. So the key is over a **SHA-256 of the credential ID**:

```
passkeys/{Base64Url(SHA256(credentialId))}   ->   the user's document id
```

Fixed 43-char suffix, no length ceiling, and the raw credential ID never appears in the
compare/exchange key space. SHA-256 is used as a content-addressed identifier here, not as a security
boundary — the credential ID is public data, and collision resistance is the only property needed.

Lifecycle mirrors the email reservation exactly: taken in `AddOrUpdatePasskeyAsync` when the
credential is new (an update of an existing credential must **not** re-reserve), released in
`RemovePasskeyAsync`, and released for every passkey in `DeleteAsync`. ⚠️ `DeleteAsync` today releases
only the email reservation (`UserStore.cs:156-165`); leaving passkey reservations behind would make a
credential ID permanently unusable and leak a deleted user's document id.

### 5.2 The store

`UserStore<TUser>` adds `IUserPasskeyStore<TUser>` to its interface list (`UserStore.cs:16-30`) and
the five methods. `AddOrUpdatePasskeyAsync` is the only non-trivial one — it is an upsert keyed on
`CredentialId` (byte-wise, via `SequenceEqual`, not reference equality), and it must preserve the
write-once fields while overwriting the mutable ones (§3.1).

The store's existing `UseOptimisticConcurrency = true` (`UserStore.cs:60`) matters here for the same
reason the comment there gives for recovery codes: **the sign count is a replay counter, and two
concurrent assertions must not both succeed in writing it.**

### 5.3 The endpoints — hand-written, in Spark's own group

`MapIdentityApi` contributes nothing here (§1), so these join the source-generated `/spark/auth/*`
endpoints next to `capabilities`, `me`, `logout`, `csrf-refresh`:

| Route | Verb | Auth | Purpose |
|---|---|---|---|
| `/spark/auth/passkeys/creation-options` | POST | **required** | begin enrollment for the signed-in user |
| `/spark/auth/passkeys` | POST | **required** | complete enrollment (attestation) |
| `/spark/auth/passkeys` | GET | **required** | list the caller's passkeys (metadata only) |
| `/spark/auth/passkeys/{id}` | DELETE | **required** | remove one |
| `/spark/auth/passkeys/{id}/name` | POST | **required** | rename one |
| `/spark/auth/passkeys/request-options` | POST | anonymous | begin sign-in |
| `/spark/auth/passkeys/sign-in` | POST | anonymous | complete sign-in (assertion) |

Rules, all enforced rather than documented:

- **Enrollment is always authenticated.** There is no "register a new account with a passkey" flow in
  this PRD (§9). A passkey is added to an account that already exists and is already signed in.
- **`POST`, not `GET`, for both options endpoints** — they mutate server state (they set the ceremony
  cookie), and a `GET` would be cacheable and CSRF-navigable.
- **Antiforgery on every authenticated route**, matching the existing treatment of
  `/manage/2fa`, `/manage/info`, `/resetPassword`, `/forgotPassword`, `/logout`
  (`LocalCredentialEndpointFilter.cs:45-46`). The two anonymous sign-in routes are exempt for the same
  reason `/login` is — there is no session to protect yet.
- **Rate limiting.** `sign-in` and `request-options` are anonymous and unauthenticated; they go behind
  the Spark rate limiter at the `BeforeAuthentication` stage like the rest of `/spark`.
- The listing endpoint returns **metadata only** — id, name, created, last used, backup state,
  transports. Never the public key, attestation object or client data JSON.

### 5.4 Sign-in is discoverable-credential only — no username

`MakeRequestOptionsAsync` accepts a `TUser?`. Passing a user populates `allowCredentials`, which means
the endpoint that resolves the username becomes a **user-existence oracle**: a request for a known
user returns credentials, an unknown one returns an empty list.

**We pass `null`, always.** `request-options` takes no username and no body-supplied identity. The
browser offers whatever discoverable credential it holds for the RP, the assertion carries the user
handle, and `FindByPasskeyIdAsync` resolves the user server-side.

This is not only safer, it is the *only* flow that fits CodeCoverage, whose sign-in page has no
username field at all (`SparkSignInComponent` renders provider buttons). It also makes
`ResidentKeyRequirement` load-bearing: `"preferred"` is the measured default and is right, because a
non-discoverable credential simply will not be offered and the user falls back to GitHub.

### 5.5 Passkeys are **not** gated by `SparkLocalCredentials`

This is the decision most likely to be got wrong by analogy.

`SparkLocalCredentials` means *email + password*. CodeCoverage sets it to `Disabled` on purpose, and
**wants passkeys anyway** — a passwordless credential is exactly what it is missing. Gating passkeys
behind `LocalCredentials` would force the app to re-enable password sign-in to get them, which is the
opposite of the goal.

So passkeys get their own option on `SparkAuthenticationOptions`:

```csharp
public SparkPasskeys Passkeys { get; set; } = SparkPasskeys.Disabled;   // Disabled | Enabled
```

Two modes, not three (D3). Default **`Disabled`**, matching `LocalCredentials` and
`ExternalLoginLinking` — adding `IUserPasskeyStore` to the shared `UserStore<TUser>` flips
`SupportsUserPasskey` to `true` for *every* Spark application on upgrade, and no application should
silently gain a new credential type. The routes are absent from the route table when disabled, not
404-shadowed, exactly as `LocalCredentialEndpointFilter` does it (`:28-31`).

`GET /spark/auth/capabilities` gains `passkeys: boolean`, derived from the **live route table** like
`localCredentials` already is (`GetAuthCapabilities.cs:32-41`) rather than read from the options
object, so it cannot advertise a surface that was never mapped.

### 5.6 A passkey is a credential — it joins the inventory

`SparkCredentialInventory` exists to answer "would this unlink leave the user with no way in?", and
`/external-logins/unlink` already refuses with `last_credential`. Passkeys must participate on both
sides:

- Unlinking the last external login must **not** succeed just because a passkey exists **unless** that
  passkey is real and usable — and conversely must be *allowed* when one is.
- `DELETE /spark/auth/passkeys/{id}` must refuse to remove the user's **last remaining credential**
  with the same `last_credential` shape the client already knows.

For CodeCoverage specifically the risk is mild — GitHub remains — but the framework rule has to hold
for an app whose only credential *is* a passkey.

### 5.7 Sign-count regression is a real control, and we do not yet know if we get it for free

`UserPasskeyInfo.SignCount` is the authenticator's monotonic counter; a counter that fails to advance
is the standard signal of a **cloned authenticator**. Whether `PasskeyHandler` rejects a non-increasing
count, or merely reports it and leaves the decision to us, is **not established** — it is a private
implementation detail of a sealed class. SP2 settles it. If the handler does not enforce it, the
endpoint must, and either way the store must persist the advanced value on every successful assertion
(§3.1, §5.2).

Note many modern authenticators (notably iCloud Keychain and other synced passkeys) report a
permanently-zero sign count. The rule must therefore be "reject a *decrease* from a non-zero baseline",
never "require an increase".

### 5.8 RP ID and origin, in production

`ServerDomain` defaults to `null`, meaning the RP ID is derived from the request `Host` — measured:
host `coverage.mintplayer.com` produced `"rp":{"id":"coverage.mintplayer.com"}`.

Two consequences:

- **Forwarded headers are load-bearing.** CodeCoverage sits behind Traefik and already hardens
  `ForwardedHeaders` to private ranges with `ForwardLimit = 1` (`Program.cs:35-70`, applied `:484`).
  If `Host` were ever wrong, every passkey would silently bind to the wrong RP ID and *none* would
  work — and credentials already enrolled under the right RP ID would be unusable, permanently.
- **Set it explicitly anyway.** A passkey is bound to its RP ID for life; there is no migration. The
  cost of pinning `ServerDomain` from configuration is one line, and it converts a silent
  misconfiguration into a startup value we can assert. Dev (`localhost`) and production
  (`coverage.mintplayer.com`) differ, which is correct and expected — passkeys enrolled in dev are not
  meant to work in production.

`ValidateOrigin` stays at its default. CodeCoverage's SPA is same-origin with its host
(`UseAngularCliServer` proxies it), so no cross-origin allowance is needed, and adding one would be a
downgrade.

### 5.9 The route filter's allow-by-default fallback

`LocalCredentialEndpointFilter.IsAllowed` classifies routes by suffix and ends in `return true`
(`:163` and `:186`). Anything `MapIdentityApi` gains in a future patch release is published in **all
three** `LocalCredentials` modes without anyone noticing.

Today this exposes nothing — `MapIdentityApi` maps no passkey routes (§1). But this PRD is the moment
it becomes a live risk: Microsoft adding `/passkey/*` to `MapIdentityApi` in a 10.0.x servicing update
would publish passkey endpoints into apps that set `Passkeys = Disabled`, bypassing §5.5 entirely.

**The fix is a guard, not a rewrite** (F-scope, D8): assert the exact expected route set at startup
under the filter, and fail loudly when `MapIdentityApi` contributes a route Spark does not recognise.
Inverting the filter to deny-by-default is the tempting move and is rejected — it would silently drop
any future route Spark *should* carry, trading a loud failure for a quiet one.

### 5.10 CodeCoverage — what the app adds

- **Opt in**: `auth.Passkeys = SparkPasskeys.Enabled` and `ServerDomain` from `Coverage:BaseUrl`'s host
  in `Program.cs:125-185`.
- **`security.json`**: one new right, `Manage/Passkeys`, granted to the `authenticated` well-known
  group only. There is no anonymous grant — sign-in itself is endpoint-gated, not resource-gated.
  Requires `--spark-synchronize-security`.
- **Management UI**: a passkey page under the account area. There is no profile page today, so this is
  net-new; it mounts through a new `withPasskeys()` route feature (§5.11) plus an entry point. Whether
  it also becomes a sidebar `programUnits.json` entry is D9.
- ⚠️ **`App_Data/Model/SparkUser.json` must not change.** Its header comment
  (`:11-23`) warns that `SparkUser` has no actions class and therefore no row rule, so anything
  declared there is readable by anyone who can read a referencing token. Passkey material must never
  be surfaced through the Spark model.
- ⚠️ **Antiforgery is `WarnOnly = true`** in this app (`Program.cs:119-123`), so the antiforgery
  requirement in §5.3 is *advisory here today*. That is a pre-existing posture and out of scope to
  change, but it is stated because it changes the real-world strength of the CSRF control on the new
  authenticated endpoints. Flagged as F1.

### 5.11 Client

New secondary entry point `@mintplayer/ng-spark-auth/passkeys` beside the existing 13, plus a
`withPasskeys()` feature for `sparkAuthRoutes(...)`. ⚠️ The `import()` must live *inside* the feature
function or the chunk ships for every app regardless (`spark-auth-routes.ts:61-64`).

`SparkAuthService` gains the ceremony methods. They are shaped like the existing `externalFlow`
(`spark-auth.service.ts:147-198`): a single `settle()` teardown path and a **closed error union**, not
thrown exceptions — `{ success: false, error: 'unsupported' | 'cancelled' | 'no_credential' |
'last_credential' | 'failed' }`.

Feature detection gates the UI: `navigator.credentials`, `PublicKeyCredential`,
`parseCreationOptionsFromJSON` and `parseRequestOptionsFromJSON` must all exist, and the "Sign in with
a passkey" button is not rendered otherwise. Using the native `parse*FromJSON` helpers (as Microsoft's
own template does) means **no base64url shim** — D5 records the browser-support trade.

Conditional mediation (autofill UI) is a deliberate **non-goal** for this PR (§9): it interacts with
the sign-in page's layout and needs its own design pass.

Strings come from the server (`GET /spark/translations`), not client constants.

### 5.12 Failure-mode requirements

These are not theoretical. They were derived by auditing a **working, non-vulnerable** WebAuthn
implementation — `C:\Repos\MintPlayer`, which hand-rolls the ceremony on Fido2NetLib and gets the
central property right (its challenge lives in `HttpContext.Session` and is never client-supplied).
Everything below is something that implementation either does not do, or does in a way worth
improving on. Each becomes a requirement here rather than a review comment later.

- **R1 — The ceremony state is cleared on every terminal outcome, not only on success.** The natural
  shape is `clear-after-the-happy-path`, which leaves a live challenge behind whenever verification
  throws. Severity is low (an attacker cannot induce the server to mint a challenge they captured),
  but "the challenge is single-use" should be true by construction, not by luck. Clear it in a
  `finally`, on success, on failure and on exception alike.

- **R2 — `UserVerificationRequirement` is pinned to `Required`, explicitly.** `"required"` is the
  measured .NET 10 default (§3.3), so this costs nothing — but it must be *written down*, not
  inherited. Under `Preferred`, an authenticator may skip the PIN or biometric and the assertion still
  verifies, which silently degrades a passkey to **possession-only**: anyone holding the unlocked
  device signs in. For a credential that grants full account access that is the wrong trade, and a
  future default change must not be able to make it for us.

- **R3 — Malformed ceremony input returns `400`, never `500`, and never echoes exception text.**
  Decoding attacker-controlled base64url and byte arrays throws readily (a user handle that is not a
  valid GUID is the classic case). An unhandled throw on an anonymous endpoint is both an availability
  concern and an information leak when the message reaches the client. `PasskeyException` is the
  framework's own signal and maps to a generic failure.

- **R4 — Sign-in failures are indistinguishable.** Unknown credential, wrong signature, failed user
  verification and sign-count regression all return the same status and the same body. Distinguishing
  them re-introduces the oracle that D10 removes.

- **R5 — The ceremony cookie is `HttpOnly`, `Secure` and `SameSite=Lax` or stricter, with a lifetime
  no longer than the ceremony.** `AuthenticatorTimeout` is five minutes (§3.3); the cookie should not
  outlive it meaningfully. ⚠️ `SecurePolicy` is one of those settings whose framework default is
  permissive, so this is an assertion to write a test for, not a box to assume is ticked. If SP1 lands
  D1, this cookie is Identity's own and the audit is of Identity's configuration rather than ours.

- **R6 — Enrollment refuses a credential ID already bound to any user, without disclosing to whom.**
  Covered by the compare/exchange reservation (§5.1), which fails the write rather than reporting a
  conflict. Stated here so the error mapping does not helpfully explain the collision.

---

## 6. Decisions

| # | Decision | Rationale |
|---|---|---|
| **D1** | Drive the ceremony through **`SignInManager`**, not `IPasskeyHandler` directly | §4. The framework's state is plaintext; `SignInManager` binds it to a DataProtection cookie. Spark writes zero crypto. |
| **D2** | State is **never** round-tripped through the client | §4. Both attacks reduce to "the client chose the challenge". |
| **D3** | `SparkPasskeys` has **two** modes (`Disabled`/`Enabled`), not three | A "SignInOnly" mode that blocks new enrollment is a real but rare operator need; YAGNI until asked. Adding a third value later is source-compatible. |
| **D4** | Passkeys are **not** gated by `SparkLocalCredentials` | §5.5. They are a passwordless credential; the app that most wants them has passwords off. |
| **D5** | Require native `PublicKeyCredential.parse*FromJSON`; no base64url shim | Matches Microsoft's own template. Shipping a shim is ~40 lines of hand-rolled base64url on the security-critical path, to support browsers that a user with a passkey almost certainly does not have. Refusal is explicit and falls back to GitHub. |
| **D6** | **Do not** verify attestation statements (`VerifyAttestationStatement` stays `null`) | Attestation identifies the *authenticator model*. It matters for enterprise device allow-lists; it does nothing for a public coverage site, and requesting it adds a privacy-sensitive identifier and a metadata-service dependency. Stated, not defaulted-into. |
| **D7** | The user handle is the RavenDB user document id | It is stable, opaque-ish and non-reassignable. ⚠️ WebAuthn requires the user handle to contain no PII — a document id qualifies, an email would not. |
| **D8** | Guard the `MapIdentityApi` route set at startup; **do not** invert the filter to deny-by-default | §5.9. A loud failure beats a silent drop. |
| **D9** | Passkey management is reachable from the shell, **not** a `programUnits.json` sidebar entry | `programUnits.json`'s header explains why the sidebar stays at two units; a credential page is account plumbing, not a program unit. |
| **D10** | Sign-in is **discoverable-credential only**; `request-options` never takes a username | §5.4. Removes a user-existence oracle and matches CodeCoverage's UI. |
| **D11** | Pin every security-relevant `IdentityPasskeyOptions` value explicitly, even where it equals the current default | R2. `UserVerificationRequirement`, `ResidentKeyRequirement` and `ServerDomain` all change behaviour silently if a servicing update moves a default. Writing them down costs three lines and makes the posture reviewable in one place. |

---

## 7. Blast radius

| Area | Risk |
|---|---|
| `UserStore<TUser>` | **Shared by every Spark app.** Adding an interface changes `SupportsUserPasskey` everywhere. Mitigated by D3's default-`Disabled` route gating — the capability exists, the surface does not. |
| `SparkUser` | A new property on a document every app stores. Additive and defaulted; RavenDB preserves undeclared fields, so a rollback does not lose data. |
| `DeleteAsync` | Now has a second reservation class to release. A miss here permanently burns credential IDs. |
| Compare/exchange keyspace | New `passkeys/` prefix alongside `emails/`. No collision, but it is cluster state that a database restore must carry. |
| `GetAuthCapabilities` | Wire contract change, consumed by the Angular client. Additive boolean. |
| CodeCoverage `security.json` | Requires `--spark-synchronize-security`; the CI security-verify gate fails otherwise. |
| ng-spark-auth | New entry point + new service methods. Minor bump only (`22.10.0` → `22.11.0`) — the major is locked to Angular 22. |
| `MintPlayer.Spark.Authorization` | `10.0.0-preview.84` → `.85`. Major stays `10` — .NET 10 has not moved. |
| CI | Two gates apply: the `libs/**` version bump and the model/description sync. |

---

## 8. Acceptance criteria

1. `UserStore<TUser>` implements `IUserPasskeyStore<TUser>`; `UserManager.SupportsUserPasskey` is true.
2. With `Passkeys = Disabled` (the default), **no** `/spark/auth/passkeys*` route exists in the route
   table, and `capabilities.passkeys` is `false`.
3. With `Passkeys = Enabled`, a signed-in user can enroll a passkey, list it, rename it and delete it.
4. A user with an enrolled passkey can sign in **without supplying a username**, and without GitHub.
5. `request-options` returns an identically-shaped response whether or not any passkey exists — no
   user-existence oracle.
6. An attestation or assertion posted **without** a prior options call is rejected. An attestation
   whose `userEntity.id` was tampered with cannot be produced at all, because the client never holds
   the state.
7. A replayed assertion (same credential JSON posted twice) is rejected.
8. A sign count that *decreases* from a non-zero baseline is rejected; a permanently-zero count is
   accepted.
9. Deleting a user releases every passkey compare/exchange reservation, and the credential IDs become
   re-registerable.
10. Registering a credential ID already bound to another user fails, and fails without disclosing
    which user holds it.
11. Deleting the last remaining credential is refused with `last_credential`.
12. CodeCoverage's `SparkUser.json` model file is byte-identical after the change.
13. `--spark-verify-model` and `--spark-verify-security` pass for all four apps.
14. A browser without `parse*FromJSON` sees no passkey UI and a working GitHub sign-in.
15. **(R1)** After a *failed* attestation or assertion, the stored ceremony state is gone: an
    immediately repeated post of the same credential JSON is rejected with "no ceremony in progress",
    not re-evaluated.
16. **(R2, D11)** `UserVerificationRequirement` is `Required` in the emitted options JSON, asserted on
    the wire rather than assumed from the default.
17. **(R3)** Every ceremony endpoint returns `400` for malformed base64url, a truncated credential
    JSON and a user handle that is not a valid identifier — never `500`, and no response body contains
    an exception type, message or stack frame.
18. **(R4)** Unknown credential, bad signature, failed user verification and sign-count regression are
    indistinguishable to the caller in status code and body.
19. **(R5)** The ceremony cookie carries `HttpOnly` and `Secure`, and `SameSite` is `Lax` or stricter.

---

## 9. Non-goals

- **Passkey-first account creation.** Enrollment requires an existing, signed-in account. Creating an
  account from a passkey alone needs an identity-proofing story CodeCoverage does not have.
- **Conditional mediation / autofill UI.** Deliberate; own design pass.
- **Attestation verification and authenticator allow-lists** (D6).
- **Passkeys as a second factor to GitHub.** Here a passkey is an *alternative* credential, not a step-up.
- **The `MintPlayer.Spark.IdentityProvider` `/connect/*` surface.** It is on master at
  `10.0.0-preview.86` with its own login and two-factor pages; wiring passkeys into the OAuth
  authorization-code flow is a separate piece of work.
- **GitLab / Bitbucket.** Their `AddXxxIntegration()` is still an intentional no-op.
- **Migrating existing users.** Nothing to migrate; passkeys are purely additive.

---

## 10. Tests that encode the intent

Server (`tests/MintPlayer.Spark.Tests/Authorization/Identity/`, `SparkTestDriver` + embedded RavenDB,
MintPlayer.Assertions):

- `UserStorePasskeyTests` — the five store methods; upsert preserves write-once fields and advances
  mutable ones; reservation taken once on insert and *not* re-taken on update; released on remove and
  on user delete; duplicate credential ID across users refused; `FindByPasskeyIdAsync` resolves
  through compare/exchange, not a query.
- `PasskeyEndpointTests` — route presence/absence per `SparkPasskeys` mode; antiforgery metadata on
  the authenticated routes; `request-options` response uniformity (AC5); attestation without prior
  options rejected (AC6); replayed assertion rejected (AC7); sign-count rules (AC8); listing returns
  no key material.
- `PasskeyFailureModeTests` — the §5.12 requirements, which are the ones a review will not catch:
  ceremony state gone after a *failed* attempt (AC15); `userVerification: "required"` asserted on the
  emitted wire JSON rather than inferred from the default (AC16); a table of malformed inputs —
  truncated credential JSON, invalid base64url, a user handle that is not a valid identifier — each
  returning `400` with no exception text (AC17); the four sign-in failure modes compared for
  byte-identical responses (AC18); ceremony cookie attributes (AC19).
- `SparkCredentialInventoryTests` — extended for the last-credential rule (AC11).
- `MapSparkIdentityApiTests` — extended with the D8 route-set guard.

Client (`vitest`, beside `spark-auth.external-login.spec.ts`): ceremony happy path, the closed error
union, feature-detection refusal, and that `request-options` is called with no username.

E2E: Playwright's CDP **virtual authenticator** domain is already available in the bundled protocol
typings — it makes an end-to-end enroll-then-sign-in test genuinely feasible. ⚠️ E2E shares one
rate-limit bucket (150/10s on 127.0.0.1); a ceremony test must stay inside it.

---

## 11. Docs to write in the same PR

- `docs/guide-passkeys.md` — new. The ceremony, the two options, why the state never leaves the
  server, RP ID and `ServerDomain`, and the browser-support floor.
- `docs/guide-authentication-schemes.md` — passkeys as a credential class alongside local credentials
  and external logins; where they sit relative to `SparkLocalCredentials`.
- `docs/code-coverage/` — the opt-in, the new right, and where the management page lives.
- `apps/CodeCoverage/CodeCoverage/README.md` — dev setup note: passkeys need the `https` launch
  profile, and a passkey enrolled against `localhost` will never work in production.
