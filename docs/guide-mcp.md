# MCP server (developers)

> **Draft.** This guide describes the design in [spark_mcp_PRD.md](spark_mcp_PRD.md). The package does
> not exist yet; names may change during implementation. Remove this banner once every snippet below has
> been run against a real app.

`MintPlayer.Spark.Mcp` gives a Spark app a [Model Context Protocol](https://modelcontextprotocol.io)
server. AI agents such as Claude Code, OpenAI Codex and Gemini connect to it and read (and, if you allow
it, change) data **as the user who connected them**. Rights in `security.json`, row security and
attribute redaction apply exactly as they do in the web UI, for anonymous callers too.

End users: see [Connect an AI agent](guide-mcp-agents.md).

## Contents

- [Choose a setup](#choose-a-setup)
- [Install and wire up](#install-and-wire-up)
- [Choose what agents can see and do](#choose-what-agents-can-see-and-do)
- [Generic tools](#generic-tools)
- [Write your own tools](#write-your-own-tools)
- [Anonymous and signed-in callers](#anonymous-and-signed-in-callers)
- [Grants and personal access tokens](#grants-and-personal-access-tokens)
- [Using an external authorization server](#using-an-external-authorization-server)
- [Changes: propose, then apply](#changes-propose-then-apply)
- [Untrusted text and prompt injection](#untrusted-text-and-prompt-injection)
- [Limits and audit](#limits-and-audit)
- [Deployment](#deployment)
- [Testing](#testing)
- [Troubleshooting](#troubleshooting)

## Choose a setup

| Setup | Packages | How an agent gets a token | Browser sign-in |
|---|---|---|---|
| **Personal access token** | Authorization + Mcp | The user creates a `spat_…` token on their account page and pastes it into the agent's config. | No |
| **External authorization server** | Authorization + Mcp | The agent signs in at your organization's OAuth server (Entra, Keycloak, Auth0). | Yes, there |
| **Embedded authorization server** | Authorization + IdentityProvider + Mcp | Your app is the OAuth server; users sign in with your normal sign-in, whatever it is. | Yes, at your app |

All three are on by default (`mcp.Authentication = EAgentAuthentication.All`); each works only when its
packages and configuration are present. Setting a flag explicitly without its wiring fails at startup.

**Which one?** If your users sign in through GitHub, Google, personal Microsoft accounts or local
passwords, only the embedded server gives them browser sign-in: those providers can't act as an MCP
authorization server. If your organization already runs an OAuth server that supports MCP clients, the
external setup avoids running one in your app. Personal tokens work in every setup, for agents that can't
open a browser.

## Install and wire up

```powershell
dotnet add package MintPlayer.Spark.Mcp --prerelease
dotnet add package MintPlayer.Spark.IdentityProvider --prerelease   # embedded setup only
```

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>(auth => { /* your existing sign-in */ });
    spark.AddIdentityProvider(idp => idp.ForMcp());          // embedded setup only

    spark.AddMcp(mcp =>
    {
        mcp.ServerName = "Contracts";                        // tools become contracts_query, ...
        mcp.Instructions = "Contracts, customers and invoices of the signed-in user.";
        mcp.AgentPermissions = EAgentPermission.Read;        // see below
        mcp.RequireSignIn = false;                           // default; see "Anonymous and signed-in callers"
        mcp.AddTools<ContractTools>();                       // optional
    });
});
```

```json
{
  "Spark": {
    "PublicOrigin": "https://contracts.example.com",
    "Mcp": { "Path": "/mcp" }
  }
}
```

The canonical server URL is `https://contracts.example.com/mcp` (no trailing slash). It is the token
audience and what users paste into their agent, so treat it as permanent. To serve it from a subdomain
through your proxy, set `Spark:Mcp:CanonicalUrl` to that URL. The app fails to start if `/mcp` is already
mapped; an Angular route named `mcp` isn't detected, so don't create one.

`ForMcp()` turns on only what MCP needs: authorization code with PKCE, refresh tokens with rotation,
revocation, the RFC 8414 metadata, Client ID Metadata Documents, and anonymous registration restricted to
loopback redirect URIs (and VS Code's own). It needs `AddMcp` and a canonical URL (`Spark:Mcp:CanonicalUrl`
or `Spark:Auth:PublicBaseUrl`): every agent token is addressed to that URL and to nothing else, so the app
refuses to start without one. The MCP package then publishes `/.well-known/oauth-protected-resource/mcp`,
and every 401 on `/mcp` carries `WWW-Authenticate: Bearer resource_metadata="…", scope="mcp:read mcp:write"`.
With an OAuth setup wired, an anonymous call that is refused answers a real HTTP 401 (with a JSON-RPC error
telling the agent how to start its own sign-in) instead of a tool error, because an HTTP 401 is what makes an
agent offer to sign in. See the [identity provider README](../libs/identity_provider/MintPlayer.Spark.IdentityProvider/README.md).

## Choose what agents can see and do

Agent access is `[Flags] EAgentPermission { None, Read, Write }`, decided at four layers. The effective
permission is their intersection:

**app (per agent) ∩ entity type ∩ the user's grant ∩ security.json rights**

**1. Entity types: nothing is exposed until you say so.** In the model JSON:

```json
{
  "name": "Contract",
  "agentPermissions": "Read, Write",
  "description": "A signed agreement with a customer, with start and end dates and a yearly value."
}
```

A type's queries follow the type. `"Write"` without `"Read"` fails at startup. Write descriptions:
agents pick tools and fields by reading them, and attribute descriptions
([guide](guide-attribute-descriptions.md)) are sent with `describe`.

**2. The app, per agent.** `mcp.AgentPermissions` is a value or a function of `AgentInformation`,
evaluated on every call:

```csharp
mcp.AgentPermissions = agent => agent.Kind switch
{
    EAgentKind.Anonymous => EAgentPermission.Read,
    EAgentKind.PersonalAccessToken => EAgentPermission.Read,
    _ when agent.VerifiedHost is "claude.ai" or "openai.com"
        => EAgentPermission.Read | EAgentPermission.Write,
    _ => EAgentPermission.Read,
};
```

| `AgentInformation` member | Trust it? |
|---|---|
| `Kind` (`Anonymous`, `PersonalAccessToken`, `ExternalAuthorizationServer`, `MetadataDocument`, `DynamicRegistration`, `PreRegistered`) | Yes |
| `ClientId` | Yes, as an identity of that registration |
| `VerifiedHost` | Yes: set only for metadata-document clients, whose document we fetched over HTTPS |
| `UnverifiedName` | **No.** Any program can call itself "Claude Code". |
| `User` | Yes |
| `GrantId` | Yes: the user's grant for this agent (`AgentGrants/…`), for logs; `null` when anonymous |

Keep the function fast and free of database calls. Gemini registers dynamically and has no
`VerifiedHost`, so a verified-host allowlist gives Gemini users read-only access.

**3. The user's grant.** When a user connects an agent, they choose "everything this app makes available
to agents" or specific types, and whether changes are allowed (offered only if layers 1 and 2 allow it).
They can change both later on their account page.

**4. Rights.** security.json and row security decide which rows and attributes, as on screen. A reference
to a type the caller may not read is left out entirely, like an attribute the caller may not read: neither
its id nor the referenced object's name is shown (a breadcrumb that names it shows a placeholder).

If a type allows `Write` but the app's function never returns it, the app logs a warning at startup.

## Generic tools

Names are prefixed with `mcp.ToolPrefix`, which defaults once to a slug of `ServerName`
(`contracts_query`). Set it explicitly before you publish: renaming `ServerName` later must not rename tools
users have allow-listed.

| Tool | Needs | Purpose |
|---|---|---|
| `discover` | Read | Exposed types and queries the caller may query. |
| `describe` | Read | Readable attributes of a type: names, labels, descriptions, types, references. |
| `query` | Read | Run a query with search, filters, sort and paging. |
| `get` | Read | One object as presented, with its available actions. |
| `propose_changes` | Read, signed in | Validate and store a set of changes; returns `planId` and a readable diff. |
| `apply_changes` | Write | Run a stored plan. |

"Give me a list of all contracts" becomes `contracts_discover` → `contracts_query`, returning exactly the
rows the user's row security lets through.

## Write your own tools

Domain tools can join, summarize and phrase results for the job. Write a normal MCP SDK tool class, without
the prefix:

```csharp
[McpServerToolType]
public sealed partial class ContractTools
{
    [Inject] private readonly IQueryExecutor queryExecutor;

    [McpServerTool(Name = "expiring", ReadOnly = true)]
    [Description("Contracts of the signed-in user that end within the given number of days.")]
    public async Task<object> ExpiringAsync(
        [Description("Days from today, 1 to 365.")] int days,
        CancellationToken cancellationToken)
    {
        var result = await queryExecutor.ExecuteQueryAsync(/* "ExpiringContracts", ... */);
        return SparkMcpResult.Rows(result);
    }
}
```

Rules:

1. **Use checked services only.** `IDatabaseAccess` (not its `...UncheckedAsync` methods),
   `IQueryExecutor` and `IPermissionService` enforce rights, row security and the user's grant. A raw
   RavenDB session enforces nothing. Analyzer SPARK05x warns.
2. **`ReadOnly = true` means the tool needs `Read`; otherwise it needs `Write`.** Forgetting the flag makes
   the tool less available, never more. Write tools aren't even listed for an agent without `Write`.
3. **Return through `SparkMcpResult`**: Spark serialization (redaction included), paging and fencing.
4. **Keep results small.** Return ids and the fields needed; aim below 10k tokens per call.

The caller is available as a `ClaimsPrincipal user` parameter or through `ISparkCurrentUser`.

## Anonymous and signed-in callers

MCP follows security.json exactly, like the web app:

- **No token**: the call runs as `anonymous` and sees what an anonymous visitor sees, narrowed by
  `agentPermissions`. Anonymous calls are always read-only, whatever `AgentPermissions` returns, and can't
  propose changes.
- If an anonymous call targets something anonymous may not use, or something that doesn't exist, the
  answer is a uniform **401**. With an OAuth setup wired it carries the sign-in challenge: that's how the
  agent knows to sign the user in.
- A signed-in caller who is denied gets the same "not found" as for something that doesn't exist.

**Agents don't sign in by themselves halfway through a session.** Claude Code, Codex and Gemini start
their OAuth sign-in when they connect, not when a later tool call answers 401. So the 401 (and 403) tool
error text tells the agent how to start the client's own sign-in:

| Client | What the error tells the agent |
|---|---|
| Claude Code | Call the authenticate tool, or run `claude mcp login <name>` (the user can also use `/mcp` → Authenticate). |
| Codex | Run `codex mcp login <name>`, then reconnect. |
| Gemini CLI | Ask the user to run `/mcp auth <name>`. |

Sending the user to your website's sign-in page doesn't help: that gives the browser a cookie, and
`/mcp` ignores cookies. The token has to come through the agent's own sign-in. Only Claude Code asks for
more access by itself when a call answers 403 `insufficient_scope`.

**`RequireSignIn`.** If your app has nothing useful for anonymous agents, set `mcp.RequireSignIn = true`
(default `false`). Every request without a token, including the first `initialize`, then gets 401 with the
sign-in challenge, so every agent signs the user in as soon as it connects. It needs setup 2 or 3: with
personal tokens only, the 401 carries no challenge.

## Grants and personal access tokens

Every connected agent has a grant (`AgentGrant`, stored by the Authorization package): which types, read or
read + changes, when it was last used. The grant is read on every call, so a user's edit or revocation
applies to the agent's next call without signing in again.

Users manage grants under **Account → Connected applications**, and create **personal access tokens**
there for agents that can't open a browser (CI jobs). Tokens start with `spat_`, are shown once, are stored
hashed, and expire after 90 days by default (at most a year). A password change, a removed sign-in method
or a locked account revokes all personal access tokens.

Wiring, as built (plan M3a):

- **Personal access tokens need no extra call.** `spark.AddAuthentication<TUser>()` wires them, and
  `AddMcp` accepts them because the Authorization package registers an `IAgentCredentialScheme` for
  setup 1. Leave `PersonalAccessToken` out of `mcp.Authentication` to refuse them.
- **A token works on `/mcp` only.** Its scheme is not one of Spark's web credentials, so it cannot call
  the app's API (`/spark/*`) or the account pages, where the agent layers would not narrow it.
- **The page.** Mount it in the client with `sparkAuthRoutes(withAccount(), withConnectedAgents())`; it
  appears on the account overview. It offers only what a call could use: the types the user may see that
  have `agentPermissions`, and changes only where `mcp.AgentPermissions` (for that agent) and the type
  allow them. The server endpoints are under `/spark/auth/manage/agents`.
- **Groups.** An agent's groups are read from the user store on every call, never from the token, so
  removing a role applies to the agent's next call. The web cookie keeps its role claims until Identity
  re-validates its security stamp (30 minutes by default), so right after a membership change the agent is
  the stricter side.
- **`AgentInformation`, `EAgentKind`, `EAgentAuthentication`** are in `MintPlayer.Spark.Abstractions`
  (`using MintPlayer.Spark.Abstractions;`), next to `EAgentPermission`.

For package authors: a package that issues agent tokens registers an `IAgentCredentialScheme`
(Abstractions) describing the agent and the user's grant behind each principal; a package that grants
other access a user should see on the same page registers an `ISparkConnectedApplicationSource`
(Authorization package).

## Using an external authorization server

To have agents sign in at your organization's OAuth server:

1. Configure it as one of the app's **sign-in providers**, so users can sign in to the website with it.
2. Configure it as the MCP authorization server; Spark validates its access tokens (audience = your
   canonical URL, `at+jwt`, signature, lifetime; introspection for opaque tokens).
3. A token maps to a local user **only** if that user has signed in to the website through that provider:
   the token's issuer must match the provider and its `sub` must match the linked login. There is no
   mapping by email.
4. An unmapped token gets 401, never anonymous access. Groups and rights come from the local user, never
   from roles in the token.

Users must therefore sign in to the website once with that provider before their agent works. The agent starts with read access to everything exposed; changes need both the user turning them on and `mcp:write` in the token. Its
grant appears on their account page after its first call; revoking it there blocks the agent even though
the external server keeps issuing tokens.

## Changes: propose, then apply

1. `propose_changes` validates every operation (rights, business rules, concurrency etags) and stores a
   plan. It returns `planId` and a readable diff, `changes`. Nothing changes. At most 50 operations.
2. The agent calls `apply_changes(planId, changes)`, passing the diff back. The agent's approval prompt
   therefore shows the user exactly what will run, and the server refuses if `changes` differs from the
   plan.
3. `apply_changes` checks everything again and runs it. A plan is single-use, belongs to the user and
   agent that proposed it, and expires after 10 minutes. If a record changed in between, the whole plan
   fails with the conflict.

Applied plans are kept (`Spark:Mcp:PlanRetention`, default one year) as the record of what agents changed.
Custom actions are operations too and go through the same gates as the button.

## Untrusted text and prompt injection

Free text typed by others or fetched from the web can contain instructions aimed at the agent. Spark wraps
such values in a per-call random fence and tells the agent that fenced content is data:

```csharp
return SparkMcpResult.Text($"Page title: {SparkMcpResult.Untrusted(page.Title)}");
```

Never put data into tool descriptions, and keep writes behind propose → apply.

## Limits and audit

- Calls are limited per user (signed in) and per IP (anonymous): `Spark:Mcp:Quota`.
- Every call writes a structured log event: user, client id, grant, tool, a hash of the arguments, row
  count, outcome and duration. Arguments are hashed because search terms can be personal data.

## Deployment

- The reverse proxy must pass `/mcp`, `/connect/*` and `/.well-known/*` and must not buffer
  `text/event-stream` responses (the server sends `X-Accel-Buffering: no`; don't override it).
- The embedded server signs tokens with its own key. Store it like other deployment secrets; rotating it
  disconnects every agent.
- CORS isn't needed for desktop agents.

## Testing

`MintPlayer.Spark.Testing` gets an `McpTestClient` that signs a test user in and calls tools. For every
tool that mirrors an endpoint, add a parity test: same user, same data, same rows.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Agent says "401" and never opens a browser | A static `Authorization` header in the agent's config replaces OAuth; or only the personal-token setup is wired; or the agent connected anonymously and doesn't sign in mid-session (see [Anonymous and signed-in callers](#anonymous-and-signed-in-callers), or set `RequireSignIn`). |
| Sign-in completes, then every call is 401 | Audience mismatch: the canonical URL differs from what the user configured (trailing slash, `http`, `www`). |
| External setup: always 401 | The user never signed in to the website through that provider, or the issuer configured for the provider differs from the token's `iss`. |
| A tool the user expects is missing | It needs `Write` and some layer gives only `Read`; or the type's `agentPermissions` is `None`; or the user lacks `Query` on it. |
| Local development: Claude Code fails with `UNABLE_TO_VERIFY_LEAF_SIGNATURE` on `https://localhost` | Node doesn't trust the ASP.NET development certificate. Use the app's `http` profile locally, or point `NODE_EXTRA_CA_CERTS` at the exported dev certificate. |
| Results end with "truncated" | The page cap was reached; the agent should page with `skip`. |
