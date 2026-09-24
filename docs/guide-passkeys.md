# Passkeys (WebAuthn)

A passkey is a public-key credential held by the user's device. The private half never leaves the
authenticator, the browser binds it to your domain, and there is no shared secret on the server — so
a database dump discloses nothing usable, and a phishing page on a lookalike domain cannot make the
authenticator sign anything.

Spark implements passkeys on **ASP.NET Core Identity's own support**, which has shipped since .NET 10
and is what the solution's `net11.0` target builds against. There
is no third-party WebAuthn package, and Spark writes no cryptographic code.

## Turning them on

```csharp
spark.AddAuthentication<SparkUser>(configure: auth =>
{
    auth.Passkeys = SparkPasskeys.Enabled;
    auth.PasskeyServerDomain = "example.com";   // strongly recommended, see below
});
```

Client side, mount the management page:

```ts
...sparkAuthRoutes(withExternalLogin(githubProvider()), withPasskeys()),
```

That is the whole setup. The sign-in page grows a "Sign in with a passkey" button on its own, once
the server reports the capability and the browser supports the ceremony.

### Passkeys are independent of `SparkLocalCredentials`

⚠️ **This is the point most likely to be got wrong by analogy.** `SparkLocalCredentials` governs
*email and password*. A passkey is a passwordless credential, so the applications that most want one
are exactly the applications that set local credentials to `Disabled`.

CodeCoverage is the worked example: GitHub is its only way in, it has no passwords and never will,
and passkeys give it its first credential that does not route through a forge. Gating them behind
`LocalCredentials` would have forced it to re-enable password sign-in in order to stop using
passwords.

### Enrollment always needs a session

There is no passkey-first account creation. A passkey is *added to* an account that already exists
and is already signed in, so enabling this gives nobody a new way to reach an account they could not
already reach. The first credential is still a password or an external login.

## `PasskeyServerDomain` — set it

Left null, the relying-party id is derived from the request `Host`. That is correct right up until a
proxy is misconfigured, and the failure is unrecoverable:

- **A passkey binds to its RP id for life.** There is no migration and no re-key.
- Credentials enrolled under a wrong id are unusable forever.
- Correctly-enrolled credentials stop working the moment the value changes.

Pinning it costs one line and converts a silent, permanent misconfiguration into a value you can
review. Dev and production differ legitimately — a passkey enrolled against `localhost` is not meant
to work against the real domain.

## What the ceremony looks like

Both flows are two round trips. The client asks for options, hands them to the authenticator, and
posts back what it produced.

| | Enrollment | Sign-in |
|---|---|---|
| 1 | `POST /spark/auth/passkeys/creation-options` | `POST /spark/auth/passkeys/request-options` |
| 2 | `navigator.credentials.create()` | `navigator.credentials.get()` |
| 3 | `POST /spark/auth/passkeys` | `POST /spark/auth/passkeys/sign-in` |

Management is `GET /spark/auth/passkeys`, `POST /spark/auth/passkeys/{id}/name` and
`DELETE /spark/auth/passkeys/{id}`. Everything except the two sign-in routes requires authentication
and an antiforgery token.

### ⚠️ The challenge never leaves the server

`IPasskeyHandler` will hand you an `AttestationState` / `AssertionState` string, and it is **plaintext
JSON containing the challenge and the target user id**:

```json
{"challenge":"h1vnj1gRE…","userEntity":{"id":"Users/1-A","name":"user@example.com",…}}
```

Not encrypted, not signed, no expiry. The obvious stateless-SPA design — return that to the browser
and accept it back — hands an attacker two things:

- **Assertion replay.** The challenge is WebAuthn's entire anti-replay mechanism. Choose your own and
  a captured assertion verifies forever.
- **Passkey injection.** Rewrite `userEntity.id` and the credential enrolls against *that* account.
  Persistent account takeover, no cryptography broken.

Spark therefore drives the ceremony through `SignInManager`, which keeps the state in a
DataProtection-protected cookie and never exposes it. **If you extend this surface, do not reach for
`IPasskeyHandler` directly**, and if you ever must — for a token-only cross-origin client — protect
the state yourself with `IDataProtector`, an expiry and a session binding.

## Sign-in takes no username, on purpose

`MakePasskeyRequestOptionsAsync` accepts a user, and passing one populates `allowCredentials`. That
turns the request-options endpoint into a **user-existence oracle**: a known account answers with
credentials, an unknown one with an empty list, to any anonymous caller.

Spark always passes `null`. The browser offers whatever discoverable credential it holds for the
relying party, the assertion carries the user handle, and the account is resolved from the credential
afterwards. The response is identical for every caller.

For the same reason, every sign-in rejection returns one body — unknown credential, bad signature,
failed user verification and malformed payload alike. Lockout is the single exception, because a user
who cannot tell a locked account from a broken authenticator keeps retrying into the lockout.

## Policy

Spark pins these explicitly rather than inheriting them, so a framework default moving cannot change
your posture:

| Option | Value | Why |
|---|---|---|
| `UserVerificationRequirement` | `required` | Under `preferred` an authenticator may skip the PIN or biometric and the assertion still verifies, quietly reducing a passkey to **possession-only** — anyone holding the unlocked device signs in. |
| `ResidentKeyRequirement` | `preferred` | Discoverable credentials are what make usernameless sign-in possible. A non-discoverable credential simply is not offered. |
| `VerifyAttestationStatement` | unset | Attestation identifies the authenticator *model*. It matters for enterprise device allow-lists; for everyone else it adds a privacy-sensitive identifier and a metadata-service dependency. |

## Browser support

The client requires the native `PublicKeyCredential.parseCreationOptionsFromJSON` and
`parseRequestOptionsFromJSON` helpers, plus a secure context. Where they are missing the passkey UI is
not rendered at all and the other sign-in methods are unaffected — a button that fails on click is
worse than no button.

The alternative was hand-rolling base64url conversion over every field of the options, on the
security-critical path, to support browsers a user holding a passkey is unlikely to be running.

## Storage

Passkeys live on the user document (`SparkUser.Passkeys`), and each one holds a cluster-wide
compare/exchange reservation keyed on a hash of its credential id. That reservation does two jobs:
it resolves a credential to its owner without an index read — a stale index on the sign-in path would
reject a *valid* passkey — and it makes a credential id unique across all users.

⚠️ Mutating `SparkUser.Passkeys` directly bypasses the reservation. Go through the store.

### Sign counts and clone detection

`SignCount` is a clone-detection signal, and the whole loop is handled for you — **Spark adds no check
of its own**, because the framework already enforces one correctly:

1. `PasskeyHandler` fails the assertion when the incoming count is **less than or equal to** the
   stored one — a counter that fails to advance means two authenticators hold the same credential.
2. That check is skipped when the incoming count **and** the stored count are both zero. Synced
   passkeys (iCloud Keychain and similar) report a permanently-zero counter, so requiring an increase
   would have locked those users out entirely.
3. On success the handler advances the stored count, `SignInManager` calls
   `UserManager.AddOrUpdatePasskeyAsync`, and that calls the store *and then* `UpdateUserAsync` — so
   the new value reaches RavenDB.

Step 3 is the one worth knowing about when writing a store: the counter is only a defence if it is
persisted, and Spark's `UpdateAsync` runs with optimistic concurrency so two concurrent assertions
cannot both write it.

## Removing the last credential

`DELETE /spark/auth/passkeys/{id}` refuses with `last_credential` when the passkey is the only thing
that can sign the account in. Unlinking an external login refuses the same way, and each now counts
the other — a passkey counts only while `SparkPasskeys` is `Enabled`, for the same reason a password
stops counting under `LocalCredentials.Disabled`: an enrolled credential with no endpoint to present
it to is an artefact, not a way in.

## See also

- [Authentication schemes](guide-authentication-schemes.md)
- [Authorization](guide-authorization.md)
- PRD and plan: [`issue_439_PRD.md`](issue_439_PRD.md), [`issue_439_plan.md`](issue_439_plan.md)
