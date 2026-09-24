# Plan — Passkeys (WebAuthn) in Spark, and on CodeCoverage (#439)

PRD: [`issue_439_PRD.md`](issue_439_PRD.md). Issue:
[#439](https://github.com/MintPlayer/MintPlayer.Spark/issues/439) — body is empty; fill it from §1 of
the PRD.

Status: **implemented.** Every milestone M1–M11 is done, including M7b. SP1 and SP3 were run and
answered; **SP2 and SP4 were not run** — see the spike table and *What is not done* below.

Suites green: **2478 server** (7m06s), **118 `ng-spark-auth`**, 16 entry points built. All four apps
pass `--spark-verify-model` and `--spark-verify-security`.

| Spike | Result |
|---|---|
| **SP1** — `SignInManager` state under Spark's wiring | ✅ **Passes. D1 stands.** `MakePasskeyCreationOptionsAsync` emits `Identity.TwoFactorUserId=CfDJ8…` (the `CfDJ8` prefix is DataProtection's magic header) and the state round-trips: without the cookie attestation reports "no passkey attestation is underway", with it the failure reason changes. Spark writes no cryptographic code. |
| **SP2** — sign-count regression | ✅ **Answered from the framework source, no authenticator needed.** `PasskeyHandler` fails the assertion when the incoming count is `<=` the stored one, and skips the check when both are zero — exactly the rule the PRD specified. The advanced value is persisted through `SignInManager` → `UserManager.AddOrUpdatePasskeyAsync` → our store → `UpdateUserAsync`. Spark needs no check of its own. |
| **SP3** — which store methods the handler calls | ✅ **Answered: the store is on the enrollment path.** For a user id that does not resolve, no store call happens. For a **real** user the handler asks for existing passkeys to populate `excludeCredentials` and throws `NotSupportedException: Store does not implement IUserPasskeyStore<TUser>.` **M2 is therefore a hard prerequisite for M4, not merely a sequencing preference.** |
| **SP4** — Playwright virtual authenticator | ⏳ **Not run**, and it stayed not-run. See *What is not done*. |
| **R5** — ceremony cookie attributes | ✅ **Satisfied by the framework.** Over HTTPS the cookie carries `HttpOnly`, `Secure` and `SameSite`. ⚠️ Over plain HTTP `Secure` is absent — that is `SameAsRequest` behaving correctly, and asserting it on an http request pins the wrong contract. The test requests over HTTPS deliberately. |

Tests: `tests/MintPlayer.Spark.Tests/Authorization/Extensions/PasskeyCeremonyStateTests.cs`. The SP3
test was flipped by M2 as planned and now asserts `excludeCredentials`.

---

## What is not done, and why

Two items, both deliberate rather than overlooked.

### ~~SP2~~ — sign-count regression ✅ **RESOLVED without running it**

The spike existed to answer one question — does `PasskeyHandler` reject a regression, or does it
expect the endpoint to? — and that question is settled by reading the shipping source rather than by
building an authenticator. `PasskeyHandler.PerformAssertionCoreAsync`, step 22:

```csharp
if (authenticatorData.SignCount != 0 || storedPasskey.SignCount != 0)
{
    if (authenticatorData.SignCount <= storedPasskey.SignCount)
        throw PasskeyException.SignCountLessThanOrEqualToStoredSignCount();
}
```

That is precisely the rule the PRD asked for: a decrease *from a non-zero baseline* fails, while a
permanently-zero counter — synced passkeys — is exempt rather than locked out.

The persistence half closes too: step 24 advances `storedPasskey.SignCount`,
`SignInManager.PasskeySignInCoreAsync` then calls `UserManager.AddOrUpdatePasskeyAsync`, and
`AddOrUpdatePasskeyCoreAsync` calls the store **and then `UpdateUserAsync`** — which is Spark's
`UpdateAsync`, running under optimistic concurrency so two concurrent assertions cannot both write the
counter. `UserStorePasskeyTests.Updating_a_passkey_advances_the_mutable_fields_only` covers our end.

**Spark adds no sign-count check of its own, deliberately.** Duplicating the framework's would risk
diverging from it.

### SP4 / the E2E test

**Not run.** Playwright's CDP virtual-authenticator domain is available in the bundled typings, but
wiring it up is a piece of work in its own right, and the integration tests cover every server-side
branch that does not require a real authenticator. AC4 (end-to-end sign-in without GitHub) therefore
rests on the unit and integration coverage rather than on a browser.

### F1 — CodeCoverage's antiforgery is still `WarnOnly`

`Program.cs:119-123` is unchanged. Flipping it to enforcing is a behaviour change on a production app
whose own gate — confirm the warning log is silent across sign-in, sign-out, token management and an
upload — cannot be satisfied from a test suite; it needs a real GitHub OAuth round trip against the
running app. The comment there already says the flag flips once the logs are clean, and nobody has
yet produced clean logs. The new passkey endpoints carry the antiforgery metadata regardless, so
flipping it later is a config change rather than a code change.

### A deviation from the PRD: no `Manage/Passkeys` right

PRD §5.10 called for one in CodeCoverage's `security.json`. It was added, then removed: nothing would
consume it. The endpoints gate on `RequireAuthorization()`, and framework endpoints do not consult an
app's `security.json` by design. `--spark-verify-security` passes either way, so the right would have
sat there looking like an access control while enforcing nothing — worse than its absence. It comes
back together with the check that reads it, if passkey management ever needs restricting to a subset
of signed-in users.

---

## Shape of the work

One repository, one pull request — framework and app together, per the repo rule. Four layers, and
only the first exists today:

| Layer | State |
|---|---|
| Identity passkey API (`IUserPasskeyStore`, `IPasskeyHandler`, `SignInManager` helpers) | **Ships in the shared framework, 10.0.12 GA.** Nothing to build. |
| RavenDB store implementation | **Missing.** Five methods + a compare/exchange reservation. |
| HTTP endpoints | **Missing**, and `MapIdentityApi` contributes none (PRD §1). Seven routes. |
| Angular client ceremony + UI | **Missing.** Zero WebAuthn code in the repo. |

Version bumps required by the CI gates:

- `libs/authorization/MintPlayer.Spark.Authorization/MintPlayer.Spark.Authorization.csproj`
  `10.0.0-preview.84` → `10.0.0-preview.85`. **Major stays `10`** — .NET 10 has not moved.
- `libs/node_packages/ng-spark-auth/package.json` `22.10.0` → `22.11.0`. **Major stays `22`** — a new
  feature is a minor; the major is locked to Angular 22.

⚠️ **Do not run the test suites per milestone.** Verify intermediate milestones by reading the code
and type-checking; batch every suite into a single sweep at M11. The exception is the spikes, which
are themselves tests and run first.

⚠️ **There is no `global.json` in this repository**, and the highest installed SDK is
`11.0.100-rc.1.26425.128`, so a bare `dotnet` command resolves SDK 11 while every project targets
`net10.0`. Pre-existing, fixed in M7b — but it will bite anyone who scaffolds a scratch project before
that milestone lands.

---

## Spikes — run before the milestones they gate

All in `tests/MintPlayer.Spark.Tests` against `SparkTestDriver` (embedded RavenDB; known CPU-starvation
flakes — never clean `RavenDBServer`).

### SP1 — Does `SignInManager`'s passkey state work under `AddIdentityApiEndpoints`? *(gates D1, and therefore the whole design)*

`SignInManager` parks the ceremony state under `IdentityConstants.TwoFactorUserIdScheme`. Spark
registers Identity with `AddIdentityApiEndpoints<TUser>()` (`SparkAuthenticationExtensions.cs:50-55`),
**not** `AddIdentity<TUser,TRole>()`, and the two register different scheme sets. If that cookie scheme
is not registered, `MakePasskeyCreationOptionsAsync` throws at first call — at runtime, not startup.

Spin a Spark test host with the real auth wiring, call `MakePasskeyCreationOptionsAsync`, and assert a
`Set-Cookie` for `Identity.TwoFactorUserId` is emitted and that
`PerformPasskeyAttestationAsync` can retrieve it on a second request carrying the cookie.

✅ **RUN 2026-09-24 — passes. D1 stands.** Measured against Spark's real wiring
(`AddSparkAuthentication<SparkUser>` + `MapSparkIdentityApi`, composite handler and all):

- `MakePasskeyCreationOptionsAsync` returns the options JSON and emits
  `Identity.TwoFactorUserId=CfDJ8…` — DataProtection-protected, as required.
- Without the cookie, attestation reports *"no passkey attestation is underway"*.
- With the cookie replayed, that reason disappears and the failure becomes a verification failure —
  proving the state round-tripped server-side and never through the client.

So the fallback to option B (own `IDataProtector` wrapper) is **not needed**, and the security-critical
unit it would have introduced does not have to exist.

**R5 settled in the same spike.** Over HTTPS the cookie carries `HttpOnly`, `Secure` and `SameSite`, so
this is Identity's own cookie already configured correctly — assert it, do not configure it. ⚠️ Over
plain HTTP `Secure` is absent; that is `SameAsRequest` working as intended, and a first pass that
asserted it on an http request produced a false failure. The test issues an HTTPS request for this
reason.

### SP2 — Does `PasskeyHandler` enforce sign-count regression? *(gates M5, AC8)* — ✅ answered, see below

`PasskeyHandler<TUser>` is sealed and its verification core is private, so this cannot be read off the
compiled API. The spike as written was to drive a full assertion twice with a counter that goes
*backwards* and observe the outcome — which needs a virtual authenticator.

⚠️ **It never needed one.** The type is sealed, not closed-source: `dotnet/aspnetcore` publishes
`src/Identity/Core/src/PasskeyHandler.cs`, and reading it answers the question outright. That is worth
remembering the next time a spike is scoped around a sealed framework type — the API surface being
private does not make the behaviour unobservable. See the resolution in the status table above.

### SP3 — Which store methods does the handler actually call, and when? *(gates M2)*

Instrument a store stub and run both ceremonies. Establish whether `MakeCreationOptionsAsync` calls
`GetPasskeysAsync` to populate `excludeCredentials` (it should — that is what prevents enrolling the
same authenticator twice), and whether `PerformAssertionAsync` resolves the user through
`FindByPasskeyIdAsync`.

✅ **RUN 2026-09-24 — the store is on the enrollment path.** Two measurements, and the contrast
between them is the finding:

- A user id that **does not resolve** → no store call, options returned normally. So the handler
  resolves the user first.
- A **real** user → `NotSupportedException: Store does not implement IUserPasskeyStore<TUser>.`, i.e.
  the handler asks for the user's existing passkeys to populate `excludeCredentials`, which is what
  stops one authenticator enrolling twice.

**Consequence: M2 is a hard prerequisite for M4**, not a sequencing preference. No passkey endpoint
returns anything useful until `UserStore` implements the interface.

⏳ **Still open:** whether `PerformAssertionAsync` resolves the user through `FindByPasskeyIdAsync`.
That cannot be measured without a real assertion, so it rides with SP2/SP4. PRD §5.1 assumes it does,
and that assumption is *why* the design pays for compare/exchange instead of an index — if it turns
out false, the reservation is only a uniqueness guard and a cheaper shape may do.

### SP4 — Is a Playwright CDP virtual authenticator usable here? *(gates the E2E in M11)*

`WebAuthn.addVirtualAuthenticator` is present in the bundled CDP typings. Confirm the bundled Chromium
exposes the domain and that a scripted enroll-then-sign-in round trip is achievable.

**Decides:** whether AC4 gets an end-to-end proof or only integration coverage.

⚠️ E2E shares **one** rate-limit bucket (150/10s on 127.0.0.1) — budget the ceremony's requests.

### Not spiked, deliberately

- RavenDB `byte[]` round-tripping (base64, well established).
- Compare/exchange semantics — the email reservation at `UserStore.cs:614-645` is a working, tested
  precedent being copied, not invented.
- Whether the net11 delta matters — measured as exactly two added members (`Aaguid`), neither required.

---

## Milestones

### M1 — `SparkUserPasskey` and `SparkUser.Passkeys`

`libs/authorization/MintPlayer.Spark.Authorization/Identity/`. A new child type mirroring
`UserPasskeyInfo` field-for-field, and one `List<SparkUserPasskey> Passkeys { get; set; } = []` on
`SparkUser` beside `Logins` / `Tokens` / `TwoFactorRecoveryCodes`.

Deliberately **not** `IdentityUserPasskey<TKey>` — that type is EF-shaped, carries a `UserId`
back-pointer, and belongs to `Microsoft.Extensions.Identity.Stores`.

⚠️ Do **not** touch `apps/CodeCoverage/CodeCoverage/App_Data/Model/SparkUser.json`. Its header
comment explains why: `SparkUser` has no actions class, so no row rule applies and anything declared
there is readable by anyone who can read a referencing token.

### M2 — `IUserPasskeyStore<TUser>` on `UserStore<TUser>` — ✅ *SP3 done; this now blocks M4/M5*

Add the interface at `UserStore.cs:16-30` and implement the five methods.

- Mapping helpers both ways between `SparkUserPasskey` and `UserPasskeyInfo`, preserving the
  write-once / mutable split (PRD §3.1).
- `AddOrUpdatePasskeyAsync` is an upsert keyed on `CredentialId` compared **byte-wise**
  (`SequenceEqual`), not by reference.
- Compare/exchange reservation `passkeys/{Base64Url(SHA256(credentialId))}` → user document id, taken
  **only on insert**, never re-taken on update. Copy the shape of `CreateEmailReservationAsync`
  (`:617`) / `UpdateEmailReservationAsync` (`:627`) / `DeleteEmailReservationAsync` (`:643`).
- `FindByPasskeyIdAsync` reads the reservation then `LoadAsync`es, exactly as `FindByEmailAsync`
  (`:246-262`) does to avoid a stale-index read.
- ⚠️ Extend `DeleteAsync` (`:156-165`) to release **every** passkey reservation. Missing this burns
  credential IDs permanently and leaks a deleted user's document id.

### M3 — The `SparkPasskeys` option, route gating, and capabilities

- `Configuration/SparkPasskeys.cs` — `Disabled | Enabled` (D3).
- `SparkAuthenticationOptions.Passkeys`, defaulting to `Disabled` (PRD §5.5).
- Routes absent from the table when disabled, not 404-shadowed.
- `Endpoints/GetAuthCapabilities.cs` gains `passkeys`, **derived from the live `EndpointDataSource`**
  like `localCredentials` already is (`:32-41`), never read from the options object.
- Mirror the flag on the client type `models/src/auth-capabilities.ts`.

### M4 — Enrollment and management endpoints — *needs M2 (measured, SP3); SP1 ✅ done*

Five authenticated routes (PRD §5.3) in `libs/authorization/.../Endpoints/`, source-generated into the
`/spark/auth` group beside `capabilities` / `me` / `logout` / `csrf-refresh`.

Both options endpoints are **POST** (they mutate ceremony state). All five carry
`RequireAntiforgeryTokenAttribute`, matching `LocalCredentialEndpointFilter.cs:45-46`. The listing
endpoint returns metadata only — never `PublicKey`, `AttestationObject` or `ClientDataJson`.

Pin the options explicitly here (D11): `UserVerificationRequirement = Required`,
`ResidentKeyRequirement`, and `ServerDomain` — even where the value equals today's default.

Apply PRD §5.12 to every endpoint in M4 and M5, since these are the requirements most easily lost in
review: clear the ceremony state in a **`finally`** (R1), map malformed input and `PasskeyException`
to a generic `400` with no exception text (R3), and keep every sign-in failure mode
indistinguishable (R4).

### M5 — Sign-in endpoints — *needs M2 and SP2; SP1 ✅ done*

`request-options` (anonymous) and `sign-in` (anonymous). `MakePasskeyRequestOptionsAsync(null)`
**always** — no username parameter, no body-supplied identity (D10), so there is no user-existence
oracle. Sign-in completes through `SignInManager.PasskeySignInAsync`, which carries lockout handling.

Enforce the sign-count rule here if SP2 shows the handler does not.

Both routes go behind the Spark rate limiter at the `BeforeAuthentication` stage.

### M6 — Passkeys join the credential inventory

Extend `SparkCredentialInventory` so a passkey counts as a credential, and make
`DELETE /spark/auth/passkeys/{id}` refuse to remove the last remaining one with the same
`last_credential` shape `/external-logins/unlink` already returns and the client already handles.

### M7 — Startup guard on the `MapIdentityApi` route set (D8)

Assert the exact expected route set under `LocalCredentialEndpointFilter` and fail loudly when
`MapIdentityApi` contributes a route Spark does not recognise — so a future servicing update that adds
`/passkey/*` cannot quietly bypass M3's gating (PRD §5.9).

**Do not** invert `IsAllowed` to deny-by-default. That trades a loud failure for a silent one.

### M7b — Close the pre-existing findings F1 and F2

Both were found during this investigation, both are real, and both land in **this** PR — they are not
follow-ups (repo rule: one pull request, everything found along the way included).

- **F1 — CodeCoverage's antiforgery protection is advisory.** `Program.cs:119-123` sets
  `WarnOnly = true` for `/spark`, `/connect` and `/api`, so every antiforgery-gated endpoint in
  production logs instead of refusing — the existing `/manage/2fa`, `/manage/info`,
  `/external-logins/unlink`, and the passkey routes M4 adds. Flip it to enforcing.
  ⚠️ **This is a behaviour change on a production app and needs its own verification**: the Angular
  client already sends `X-XSRF-TOKEN` (`SparkAuthenticationExtensions.cs:65`,
  `provide-spark-auth.ts:32`) and calls `csrfRefresh()` on every session change, so the expectation is
  that nothing breaks — but `WarnOnly` exists precisely because someone was unsure. Before flipping,
  run the app with warnings surfaced and confirm the log is silent across sign-in, sign-out, token
  management and an upload. If anything does warn, fix the caller, not the flag.
  The GitHub webhook and API-token paths are signature/bearer authenticated and must be confirmed
  unaffected — they are not browser-driven, so they should not be carrying antiforgery tokens at all.
- **F2 — add a `global.json`** pinning the SDK to 10.0.x. The repo has none, and the highest installed
  SDK is `11.0.100-rc.1`, so bare `dotnet` commands resolve SDK 11 against `net10.0` projects.
  One file, removes a whole class of confusing scaffolding failures.

### M8 — Client: service methods and the new entry point

New secondary entry point `libs/node_packages/ng-spark-auth/passkeys/` with its own `ng-package.json`,
plus `withPasskeys()` in `routes/src/spark-auth-routes.ts`. ⚠️ The `import()` must live *inside* the
feature function or the chunk ships for every app (`:61-64`).

`SparkAuthService` gains the ceremony methods, shaped like `externalFlow` (`:147-198`): one `settle()`
teardown path, a **closed error union** rather than thrown exceptions. Feature detection for
`navigator.credentials` + `PublicKeyCredential` + both `parse*FromJSON` helpers (D5).

No `NgZone` work needed — the apps are zoneless and `navigator.credentials` is promise-based.

### M9 — Client: management UI and the sign-in button

A passkey list/rename/delete component in the new entry point, and a "Sign in with a passkey" button
on `SparkSignInComponent`, rendered only when `capabilities().passkeys` **and** feature detection both
pass. Strings come from `GET /spark/translations`, not client constants.

### M10 — CodeCoverage opt-in

- `auth.Passkeys = SparkPasskeys.Enabled` and an explicit `ServerDomain` from `Coverage:BaseUrl`'s host
  in `Program.cs:125-185` (PRD §5.8 — do not rely on the `Host`-derived default).
- `App_Data/security.json`: one right, `Manage/Passkeys`, granted to the `authenticated` well-known
  group. Run `--spark-synchronize-security`.
- Mount `withPasskeys()` at `ClientApp/src/app/app.routes.ts:31`. ⚠️ It must stay **before** the
  `:provider/...` parameterised routes — the comment at `:36-43` says that ordering is load-bearing.
- A link from `shell.component.html:20-26`. No `programUnits.json` entry (D9).

### M11 — Tests, docs, demo, version bumps (runs last)

The single batched suite sweep: server tests, `ng-spark` vitest, generators, protocol client, plus
`--spark-verify-model` and `--spark-verify-security` on all four apps. Test inventory is PRD §10; E2E
depends on SP4.

Enable passkeys on one demo app (HR or Fleet — they already mount `withLocalLogin()`) so the feature
has a non-production exercise path.

Docs per PRD §11. Version bumps per *Shape of the work*.

---

## Sequencing notes

- ~~SP1 first, alone.~~ ✅ **Done and passed** — D1 stands, so no data-protection wrapper is needed.
- **M1 → M2 first, and they are now blocking.** SP3 measured that the handler consults the store for a
  real user, so **no endpoint works until M2 lands**. M3's gating can proceed in parallel.
- M4/M5 depend on M2 and on M3's gating. M5 additionally needs SP2.
- M6 and M7 are independent of the client work and can slot anywhere after M4.
- **M7b's F1 half runs after M4/M5**, not before — flipping antiforgery to enforcing is worth doing
  once the new endpoints exist, so the verification pass covers them too. Its F2 half (`global.json`)
  can land first and makes everything after it less confusing.
- M8 can start against the M3 capability flag before M4/M5 exist, using the route contract from PRD §5.3.
- M10 is last of the feature work — it is the opt-in, and it needs everything else present.

---

## Risks

| Risk | Handling |
|---|---|
| **SP1 fails** and `SignInManager`'s state storage is unusable under Spark's wiring | Fall back to PRD §4 option B. Cost: a `IDataProtector` wrapper with purpose string, expiry and session binding, plus adversarial tests for each. Budget a milestone. |
| **RP ID misconfiguration in production** | Passkeys bind to the RP ID for life and there is no migration — a wrong value is unrecoverable for every credential enrolled under it. Mitigated by pinning `ServerDomain` explicitly (M10) rather than relying on the `Host`-derived default. |
| `DeleteAsync` misses a reservation | AC9 covers it directly; it is called out in M2 because the failure is silent and permanent. |
| Compare/exchange keyspace not carried by a restore | Cluster state, not document state. Note it in the CodeCoverage ops docs — a restore that drops compare/exchange values breaks both email lookup *and* passkey sign-in. Pre-existing exposure, now wider. |
| Synced-passkey sign counts are always zero | SP2's rule is "reject a decrease from a non-zero baseline", never "require an increase". Getting this backwards locks out iCloud Keychain users. |
| Browser-support floor (D5) excludes someone | GitHub sign-in remains, and the UI is feature-detected so the button simply is not rendered. |
| Antiforgery is `WarnOnly` on CodeCoverage | Pre-existing (F1). The new endpoints carry the metadata regardless, so tightening the app posture later is a config change, not a code change. |

---

## Out of scope

Everything in PRD §9, plus:

- Inverting `LocalCredentialEndpointFilter.IsAllowed` to deny-by-default (D8 rejects it, on the
  merits — a guard is better than a silent drop).
- Passkeys in `MintPlayer.Spark.IdentityProvider`'s `/connect/*` flow.
- Migrating `C:\Repos\MintPlayer` off Fido2NetLib onto Identity's passkey API. That repository will
  eventually be rebuilt on Spark, so hardening its current WebAuthn implementation is not worth the
  spend. ⚠️ Its audit is not wasted, though — it is where PRD §5.12 came from.

⚠️ **F1 and F2 are *not* out of scope** — they are M7b. Everything found along the way ships in this
pull request.

---

## Found along the way

### F1 — CodeCoverage's antiforgery protection is advisory, not enforced

`apps/CodeCoverage/CodeCoverage/Program.cs:119-123` sets `WarnOnly = true` on
`AddAntiforgeryProtection` for `/spark`, `/connect` and `/api`. Every antiforgery-gated endpoint in the
app — the existing `/manage/2fa`, `/manage/info`, `/external-logins/unlink`, and the passkey routes
this PRD adds — therefore logs rather than refuses. Pre-existing and in production. **Fixed in M7b**,
not deferred.

### F2 — The repository has no `global.json`

The highest installed SDK is `11.0.100-rc.1.26425.128`, so a bare `dotnet` command from the repository
root resolves SDK 11 while every project targets `net10.0` (proof: `dotnet new console` emits
`<TargetFramework>net11.0</TargetFramework>`). Builds work because the projects pin their TFM, but any
scaffolding or scratch project silently gets the wrong default. **Fixed in M7b.**

### F3 — `LocalCredentialEndpointFilter.IsAllowed` is allow-by-default

`:163` and `:186` both `return true`. Any route `MapIdentityApi` gains in a servicing update is
published in all three `LocalCredentials` modes. Harmless today — measured: `MapIdentityApi` maps no
passkey routes — but it is exactly the mechanism by which M3's gating could be bypassed later. M7 is
the guard; the finding is recorded here because it is a pre-existing property, not something this work
introduces.

### F4 — A memory note was stale: `libs/identity_provider` **is** on master

It is present at `10.0.0-preview.86` with a full `/connect/*` OAuth surface (authorize, login,
two-factor, consent, token, userinfo, introspect, revoke, JWKS, discovery). Any note saying the
identity provider is branch-only should be corrected.

### F6 — A missing ceremony threw `InvalidOperationException`, and it reached the client as a 500

Found writing M5's tests. `SignInManager.PasskeySignInAsync` signals "no ceremony is underway" with
an **`InvalidOperationException`**, not a `PasskeyException` — so the obvious catch filter misses it,
and the commonest hostile request (a bare POST to the anonymous sign-in route, with no prior
options call) answered `500` with a stack trace. Both an availability problem and an information leak
on an unauthenticated endpoint.

Worth recording because the omission is invisible on reading: the exception type has nothing to do
with passkeys, and only its message says otherwise. Fixed, with the type named explicitly and a
comment saying why it belongs there.

### F7 — A partial class documented twice fails the build with CS0579

Giving `UserStore.Passkeys.cs` an XML `<summary>` on its `partial` declaration made
`DescriptionSourceGenerator` emit two `[Description]` attributes for the same symbol:
`error CS0579: Duplicate 'Description' attribute`. A partial type carries its docs on one declaration
only. The error names a generated file and not the cause, so it is worth knowing before splitting any
other documented type across files.

### F5 — Investigation hazard: `strings` is not installed in this environment

A first pass searching the Identity ref assemblies for "passkey" returned **zero hits for every
symbol, including controls that are definitely present** — because `strings` does not exist and the
error was being swallowed. `grep -a` against the DLL works and reproduces the controls (`IUserStore`=1,
`TwoFactor`=2) alongside the real hits. A clean grep is not evidence until the method is shown to be
falsifiable.
