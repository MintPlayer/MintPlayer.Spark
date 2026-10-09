# Registering applications with a Spark identity provider

This guide is for **developers who register applications** with an identity provider built on
`MintPlayer.Spark.IdentityProvider` (the demo is `apps/SparkId`). The operator's side (hosting,
options, keys) is covered by the [package README](../libs/identity_provider/MintPlayer.Spark.IdentityProvider/README.md).
The design and its decisions are in [identity_provider_platform_PRD.md](identity_provider_platform_PRD.md).

## 1. Become a developer

Sign in and open **Developers** (`/developers`):
- Accept the current terms and request developer access.
- Where the operator requires approval (the default), an administrator decides, and you get a mail
  either way.
- If the terms change, you accept them again.

Once you are active, the **Applications** query lists the applications you are a member of, and you can
create new ones.

## 2. Create an application

Create it from the **Applications** query. The **client id** is generated and can never change:
every token, grant and redirect is tied to it.

- **Client type:**
  - **Confidential** for a server that can keep a secret.
  - **Public** for a SPA, mobile or desktop app. A public client has no secret and must use PKCE,
    which every client must do anyway.
- **Redirect URIs** match exactly, including case.
  - Web apps use `https`; `http` is accepted only for loopback addresses.
  - Native apps, which are public clients, may use loopback or a private-use scheme
    (`com.example.app:/callback`).
  - Fragments and wildcards are refused.
- **Post-logout redirect URIs**, **back-channel** and **front-channel logout URIs** are what logout
  calls or returns to.
- **Grant types:**
  - `authorization_code` for people signing in;
  - `refresh_token` together with the `offline_access` scope;
  - `client_credentials` for a service acting as itself;
  - the device grant for TVs and CLIs;
  - token exchange for calling a downstream API on a user's behalf.

### Secrets

Admins of the application press **Generate secret**. The value is shown **once**, in a dialog with a
Copy button; only a hash is stored. Generate a second secret before revoking the first to rotate
without downtime. The secrets list shows when each was last used.

Instead of a secret you can use:
- **`private_key_jwt`:** register your public keys as a JWKS or a `jwks_uri`, and sign a client
  assertion per request.
- **mTLS:** `tls_client_auth` with a certificate subject, or `self_signed_tls_client_auth` with a
  registered certificate.

Pick the method in **Token endpoint authentication**. Once set, it is the only method accepted.

## 3. Scopes

List the scopes your application may ask for, each **required** or optional:
- Users see optional scopes on the consent page and can untick them.
- They can also withdraw any scope later.
- A required scope cannot be refused: refusing it means not signing in.

- **Identity scopes** (`openid`, `profile`, `email`, …) are always available.
- **API scopes** belong to an **API resource**, `<resource>.<scope>` (for example `fleet.read`). The
  resource's name becomes the access token's `aud`.
  - Scopes of a resource your own team owns are available at once.
  - Someone else's API scope is **Pending** until that resource's owner, or an administrator,
    approves it, unless the resource auto-approves. Until then it is granted only to your team
    members.

To publish your own API, create an **API resource** (you become its owner) and define its scopes.
Validate the tokens in your API with `spark.AddSparkResourceServer(authority, "<resource>")` and
`.RequireScope("<resource>.<scope>")`, or with any RFC 9068 validator.

## 4. Your team

The application's **Members** have one of three roles:

| Role | Can |
|---|---|
| **Admin** | everything: members, secrets, delete, switch mode |
| **Developer** | edit settings, URIs and scopes; no secrets, no members |
| **Tester** | no portal access; may sign in to the application while it is in Development |

**Invite** by email:
- Admins and Developers must already be developers; Testers can be any account.
- The answer is always "Invitation sent", whether or not the address has an account, so the form
  cannot be used to find out who is registered.
- The link is single-use and valid for 7 days. It must be opened while signed in as the invited
  account, and accepting takes a click.

An application always keeps at least one Admin.

## 5. Development and Live

- **Development** (the default): only members can sign in to the application, and nothing needs
  approval. Use it while building.
- **Live:** anyone can sign in. Switch with **Switch to Live**. Where the operator requires a go-live
  review, an administrator approves the switch first. **Switch to Development** takes it back.

## 6. Protocol options

Turn on only what you need:
- **Require PAR:** authorization requests must be pushed to `/connect/par` first.
- **Require signed request objects** (JAR).
- **Require DPoP:** tokens are bound to a key your client holds; send a DPoP proof with the token
  request and with every API call.
- **Certificate-bound access tokens:** with mTLS.
- **Pairwise subjects:** a different `sub` per sector, so applications cannot correlate users.
- **id_token / userinfo signing and encryption algorithms.**
- **Default max age:** forces a fresh sign-in after that many seconds.
- **Allow impersonation:** token exchange may act for the subject.

## 7. Registering by protocol

Approved developers can also register with **dynamic client registration** (RFC 7591):
1. On `/developers`, press **Issue registration token**. The initial access token is shown once and is
   short-lived.
2. `POST /connect/register` with that token as a Bearer token and your client metadata.
3. Keep the `registration_access_token` from the answer. It reads, updates or deletes the
   registration at `/connect/register/{client_id}` (RFC 7592).

An application registered this way is an ordinary application: you are its Admin, and it starts in
Development.

## 8. What your users see

- The consent page names your application and lists the scopes. Consent is remembered, and asking for
  more scopes later asks only for the new ones (`include_granted_scopes`).
- **Connected applications** (`/account/applications` in the SPA, or `/connect/applications`) lets them
  withdraw access, whole or per scope. The tokens issued under it stop working at once.
- When an administrator disables your application, everything issued to it is revoked, and re-enabling
  it asks your users for consent again.
- Every change to your application, its members and its secrets is recorded in the **audit trail**.
  As an Admin of the application, you see its entries.
