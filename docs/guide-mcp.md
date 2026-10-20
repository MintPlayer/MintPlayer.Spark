# MCP server (developers)

> **Draft.** The package is built ([spark_mcp_PRD.md](spark_mcp_PRD.md), plan M1–M7) and its flows are covered by
> tests, including an end-to-end sign-in against Fleet. The sign-ins with real agents (Claude Code, Codex, Gemini CLI,
> VS Code) still have to run against a live app; this banner goes once they have.

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
  - [Reach past the type layer: `[McpReadsThrough]` and `[McpRunsAction]`](#reach-past-the-type-layer-mcpreadsthrough-and-mcprunsaction)
- [Anonymous and signed-in callers](#anonymous-and-signed-in-callers)
- [Grants and personal access tokens](#grants-and-personal-access-tokens)
- [Using an external authorization server](#using-an-external-authorization-server)
- [A Spark identity provider as the external server](#a-spark-identity-provider-as-the-external-server)
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
| **External authorization server** | Authorization + Mcp | The agent signs in at your organization's OAuth server (Entra, Keycloak, Auth0), or at another Spark application's identity provider ([below](#a-spark-identity-provider-as-the-external-server)). | Yes, there |
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
    spark.AddIdentityProvider(idp => idp.ForMcp().AgentsOnly()); // embedded setup only; see below

    spark.AddMcp(mcp =>
    {
        mcp.ServerName = "Contracts";                        // tools become contracts_query, ...
        mcp.Instructions = "Contracts, customers and invoices of the signed-in user.";
        mcp.AgentPermissions = _ => EAgentPermission.Read;   // see below
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

`ForMcp()` adds what MCP needs: authorization code with PKCE, refresh tokens with rotation,
revocation, the RFC 8414 metadata, Client ID Metadata Documents, and anonymous registration restricted to
loopback redirect URIs (and VS Code's own). It needs `AddMcp` and a canonical URL (`Spark:Mcp:CanonicalUrl`
or `Spark:Auth:PublicBaseUrl`): every agent token is addressed to that URL and to nothing else, so the app
refuses to start without one. The MCP package then publishes `/.well-known/oauth-protected-resource/mcp`,
and every 401 on `/mcp` carries `WWW-Authenticate: Bearer resource_metadata="…", scope="mcp:read mcp:write"`.
With an OAuth setup wired, an anonymous call that is refused answers a real HTTP 401 (with a JSON-RPC error
telling the agent how to start its own sign-in) instead of a tool error, because an HTTP 401 is what makes an
agent offer to sign in. See the [identity provider README](../libs/identity_provider/MintPlayer.Spark.IdentityProvider/README.md).

`ForMcp()` adds the agents' side and leaves the rest of the identity provider mapped: device grant, PAR,
introspection, userinfo, end-session, the developer portal and key administration. If the provider is in your app
**only** for its agents, add `AgentsOnly()`. It maps the discovery and RFC 8414 documents, the JWKS, authorize with
your sign-in and the consent page, the token endpoint for code and refresh, anonymous registration, and revocation;
everything else answers 404 and is not advertised. Users manage their agents under Connected applications on your
account page. Switch the provider's management types off in `security.json`
(`"libraries": { "identity-provider": false }`). An app that is also a full identity provider, like `apps/SparkId`,
leaves `AgentsOnly()` out, or picks flows and endpoints one by one
([Choosing flows and endpoints](../libs/identity_provider/MintPlayer.Spark.IdentityProvider/README.md#choosing-flows-and-endpoints)).

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

**2. The app, per agent.** `mcp.AgentPermissions` is a function of `AgentInformation` (`_ => EAgentPermission.Read` for one value),
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
| `propose_changes` | Write, signed in | Validate and store a set of changes; returns `planId` and a readable diff. |
| `apply_changes` | Write, signed in | Run a stored plan. |

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
   `SparkMcpResult.Refuse()` for "not found" and "not allowed" alike (the uniform answer, see below);
   `SparkMcpResult.Invalid(message)` only for arguments that are malformed whatever the data (a bad page size, a
   malformed name), so its message can say what is wrong; `SparkMcpResult.Track(rows)` to put the row count in the
   call's audit event when you answer with `Json` or `Text`.
4. **Keep results small.** Return ids and the fields needed; aim below 10k tokens per call. Bound every list yourself
   (and say how many were left out): the cap cuts an oversized answer mid-document.
5. **Check the types you read.** An app tool that reads an entity type outside the generic read paths asks
   `IPermissionService.IsAllowedAsync("Read", "<Type>")` (or `Query`) first, so the type's `agentPermissions` and the
   user's grant narrow it like every generic tool. A type the tool declares it reads through another (below) answers by
   that other type's layers.

The caller is available as a `ClaimsPrincipal user` parameter or through `ISparkCurrentUser`; identify the user
through `ISparkCurrentUser.Id`. An agent's principal carries the local user id as `sub` and as
`ClaimTypes.NameIdentifier`, so code that reads the id from the claim (or `UserManager.GetUserAsync(principal)`), as
from the sign-in cookie, works unchanged; before that, CodeCoverage's owner lookups missed every agent (plan M6). It
also carries the role and group claims a cookie sign-in of the user carries (Identity's claims factory, made on every
call), so `User.IsInRole` and role-claim reads answer for the agent as for the user, and a role removed from the user is
gone on the agent's next call. Only role and group claims are copied, never the cookie's others (email, the user's
other stored claims).

**The application's own words outside the fence.** `SparkMcpResult.Json(note, value)` puts `note` before the fenced
JSON: say what the data is and what to call next there, and never put a value a user typed or a crawler fetched in it.

**Prompts.** `mcp.AddPrompts<TPrompts>()` adds `[McpServerPrompt]` methods of a `[McpServerPromptType]` class, prefixed
like the tools (`fix_seo_findings` is served as `seotester_fix_seo_findings`; Claude Code shows it as
`/mcp__seotester__seotester_fix_seo_findings`). A prompt is a message template the user picks in the agent (one per app
is plenty, PRD §9); it reads no data, so every caller sees it. Name the tools it mentions with the prefix, and put no
data in it.

### Reach past the type layer: `[McpReadsThrough]` and `[McpRunsAction]`

A type's `agentPermissions` (and the user's choice of types in the grant) narrows every rights check of an `/mcp` call,
so a tool cannot read a `None` type or run a custom action on a `Read` type. Two attributes on the tool method lift
exactly that, for that tool's calls only (SeoTester's tools are the example: `PageResult` stays `None`, and its one write
runs `StartAudit` on `Site`, which agents may only read):

```csharp
[McpServerTool(Name = "list_findings", ReadOnly = true)]
[McpReadsThrough("PageResult", "AuditRun")]        // a run's pages, read as part of the run
public async Task<CallToolResult> ListFindingsAsync(...) { ... }

[McpServerTool(Name = "start_scan")]                // not ReadOnly: a write
[McpRunsAction("Site", "StartAudit")]               // this one action on a type agents may only read
public async Task<CallToolResult> StartScanAsync(string site, CancellationToken ct)
{
    var outcome = await actions.RunAsync("Site", "StartAudit", siteId, ct);   // ISparkMcpActions
    return outcome.Status switch
    {
        SparkMcpActionStatus.Executed => SparkMcpResult.Json("Scan started.", outcome.Result),
        SparkMcpActionStatus.Refused => SparkMcpResult.Refuse(),
        _ => /* Invalid, Disabled, Throttled, Failed: outcome.Message, outcome.RetryAfter */,
    };
}
```

- `[McpReadsThrough(type, through)]` judges **reading** (`Read`, `Query`) `type` by `through`'s `agentPermissions` and the
  user's choice of types, while the tool runs; writing `type` still follows its own layers (with `None`: never). Use it
  for rows that belong to an exposed parent and are read as part of it. A `through` that names no entity type refuses the
  read (fails closed).
- `[McpRunsAction(type, action)]` lets that one custom action need only `Read` from the type and the grant's choice of
  types. It still needs `Write` from `mcp.AgentPermissions` and the grant's read/changes (checked first, as for every
  write), so the tool is listed only to agents allowed changes. Any other action on the type still needs `Write` there.
  A `ReadOnly` tool that declares it refuses startup.
- Types and actions are named as in the model (case-insensitive); both attributes may be repeated on one method. They
  apply only to calls of the method they are on: another tool, and every generic tool, keeps the type layer.
- Neither touches `security.json`, row security, disabled actions or quotas: those decide exactly as on the web.
- They act only on checks that reach `IPermissionService`: `IDatabaseAccess`, `IQueryExecutor`, `ISparkMcpActions` and
  your own `IsAllowedAsync` calls. A tool that gates on a parent's rights itself and reads the rest below that gate
  needs neither (CodeCoverage's tools ask for `Read` on `Repository` and `Commit`, then read file coverage through
  their own service; plan M6).
- `ISparkMcpActions.RunAsync(type, action, id)` runs a custom action through the same gates as the button
  (`POST /spark/actions/execute`): the right, the row-gated load of the object, the row rule for the action, disabled
  actions, quotas. It answers `Executed` (with what the action handed to `SetResult`), `Refused` (answer
  `SparkMcpResult.Refuse()`: unknown, denied and missing alike), `Invalid`, `Disabled`, `Throttled` (with
  `RetryAfter`) or `Failed` (details in the server log only). Never call the action class or its service directly: that
  would be a second, unchecked path.

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

A grant names its chosen types by **id** (`Types[i].TypeId`), never by name: an application's
`[assembly: PersistentObjectName]` rename (D28) changes a name, so a stored name would grant nothing after it,
or grant another type that took the name. Grants stored by name before D28 are moved by the migration
`M_202610191530_AgentGrantTypesById`; a name the model no longer knows is left as it is and grants nothing.

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
- **Groups and roles.** An agent's groups and role claims are read from the user store on every call, never
  from the token, so removing a role applies to the agent's next call. The web cookie keeps its role claims
  until Identity re-validates its security stamp (30 minutes by default), so right after a membership change
  the agent is the stricter side.
- **`AgentInformation`, `EAgentKind`, `EAgentAuthentication`** are in `MintPlayer.Spark.Abstractions`
  (`using MintPlayer.Spark.Abstractions;`), next to `EAgentPermission`.

For package authors: a package that issues agent tokens registers an `IAgentCredentialScheme`
(Abstractions) describing the agent and the user's grant behind each principal; a package that grants
other access a user should see on the same page registers an `ISparkConnectedApplicationSource`
(Authorization package).

## Using an external authorization server

To have agents sign in at your organization's OAuth server:

1. Configure it as one of the app's **sign-in providers**, so users can sign in to the website with it.
2. Configure it as the MCP authorization server, bound to that provider:

   ```csharp
   spark.AddAuthentication<SparkUser>();
   spark.AddOpenIdConnect("Corp", "Corp sign-in", o => { /* authority, client id, secret */ });
   spark.AddExternalAgentAuthorizationServer("https://login.corp.example/realms/acme", loginProvider: "Corp");
   spark.AddMcp(mcp => { /* … */ }); // with Spark:Mcp:CanonicalUrl or Spark:Auth:PublicBaseUrl set
   ```

   Spark validates its access tokens (signature from the issuer's published keys, the issuer, audience =
   your canonical URL, `at+jwt`, lifetime). For a server that hands out opaque tokens, set
   `o.UseIntrospection = true` with this app's `IntrospectionClientId`/`IntrospectionClientSecret`.
   Register an API at the server whose tokens carry the canonical URL as `aud`, with the scopes
   `mcp:read` and `mcp:write`; if the server names scopes differently, list its names in
   `o.ScopesSupported` and set `o.WriteScope`. For a server that types its access tokens `JWT` rather than
   `at+jwt`, set `o.RequireAccessTokenType = false` (an ID token is still refused: its audience is the
   client). If the access token's `sub` differs from the login key the website's sign-in stores (Entra's
   `sub` is per application), set `o.SubjectClaim` to the claim both share (`oid`). Not yet tried against a
   real Entra, Keycloak or Auth0 tenant.
3. A token maps to a local user **only** if that user has signed in to the website through that provider:
   the token's issuer must be the configured one and its `sub` must equal the key of that provider's
   linked login. There is no mapping by email.
4. An unmapped token gets 401, never anonymous access. Groups and rights come from the local user, never
   from roles in the token.

The RFC 9728 document then lists the external server (after the embedded one, if both are wired), so an
agent's 401 sends it there to sign in. Startup fails if the provider isn't registered, without the Mcp
package, or without a canonical URL; an explicit `mcp.Authentication` flag
`ExternalAuthorizationServer` without this call fails too.

Users must therefore sign in to the website once with that provider before their agent works. The agent
starts with read access to everything exposed; changes need both the user turning them on and `mcp:write`
in the token (in this setup only, the token's scope is a cap: the user can't be stepped up through another
server's consent). Its grant appears on their account page after its first call, with the server's issuer;
revoking it there marks it *disconnected* and blocks the agent even though the external server keeps
issuing tokens. Editing it allows the agent again. Unlinking the login from the account also blocks it.

The grant is bound to the user's security stamp, as personal access tokens and the embedded server's grants are: a
password change, a removed sign-in method or any other stamp change shows it as *disconnected* and refuses the agent,
however many tokens the external server still issues, until the user allows it again under Connected applications,
which stamps it anew.

## A Spark identity provider as the external server

A Spark application with the identity provider package can be the external authorization server of **another**
application's `/mcp`. The demo does exactly this: SparkId signs agents in for HR. Users already sign in to HR with
their SparkId account, and the agent signs in at SparkId too.

**On the identity provider**, list the other application's MCP endpoint. It needs no MCP endpoint of its own:

```csharp
spark.AddIdentityProvider(idp => idp.ForMcpResource("https://hr.example.com/mcp", r => r.DisplayName = "HR"));
```

or in configuration (`Spark:IdentityProvider:Mcp:Resources`, one entry per endpoint, `Url` and `DisplayName`). The URL
is the other application's canonical URL, exactly. The provider then publishes the RFC 8414 document, accepts
metadata-document clients and anonymous loopback registration, and mints tokens whose only audience is that URL, with
`mcp:read`, `mcp:write` only when the user allowed changes, and the user's id as `sub`. `idp.ForMcp()` stays the call
for the provider's own `/mcp`; both can be combined, and an agent then names the endpoint it wants in `resource`.
Such a provider is also the other application's sign-in, which reads userinfo, so it keeps its OpenID Connect
surface: `AgentsOnly()` beside `ForMcpResource` refuses to start.

**On the application**, accept the provider as its external server, bound to the sign-in its users already link:

```csharp
spark.AddExternalProviders(builder.Configuration);   // Spark:Auth:Providers:OpenIdConnect:SparkId (issuer, client)
spark.AddExternalAgentAuthorizationServer("https://id.example.com", loginProvider: "SparkId");
spark.AddMcp(mcp => { /* … */ });                     // Spark:Mcp:CanonicalUrl = https://hr.example.com/mcp
```

What the user sees:

1. The agent connects to the application's `/mcp`, gets the 401, reads the protected-resource document, and opens
   the provider's sign-in.
2. The provider's consent screen names the application and its host and offers **read** or **read and make changes**.
   It offers no types: it cannot see what the application exposes, or the user's rights there.
3. The application maps the token to the user who linked that provider account (never by email). The first call
   creates the agent's grant there: everything exposed, read only. The user picks types and turns changes on under
   Connected applications in the application. Changes need that toggle *and* "make changes" at the provider.

A user who never signed in to the application through the provider gets 401: they sign in to the website with it
once, which links the account. Keep the application's sign-in client at the provider on public subjects (the
default): the agent token's `sub` is the user's id, and the linked login's key must equal it.

**Revoking**: the agent's grant in the application disconnects it at once. The provider's `/connect/applications`
withdraws its OAuth grant, so its refresh token stops working; an access token the application validates locally keeps
working until it expires (60 minutes) unless the application disconnects the agent too.

## Changes: propose, then apply

1. `propose_changes(summary, operations)` validates every operation and stores a plan. It returns `planId`
   and a readable diff, `changes`: one entry per operation, `Field: before -> after`. Nothing changes. At most
   50 operations. An operation is `{ kind, type, id?, etag?, values?, action?, query?, ids? }` with `kind`
   `create`, `update`, `delete` or `action` (a custom action); `values` sets attributes by name, the rest
   keep their value.
2. The agent calls `apply_changes(planId, changes)`, passing the diff back. The agent's approval prompt
   therefore shows the user exactly what will run, and the server refuses if `changes` differs from the
   plan.
3. `apply_changes` checks everything again and runs it. A plan runs once, belongs to the user, the agent
   and the grant that proposed it (anyone else gets the uniform not-found), and expires after 10 minutes.

What "validates" means: the operation goes through the same write path as the web pages, without the write
— the type and attribute rights, the row gates, the disabled-action hook, the validation rules, and the
version of the object (the etag). An attribute the caller cannot set (read-only, denied, or a reference to a
type they may not read) is refused by name, the same answer a misspelled name gets; on the web the save
would drop it silently, which in a plan would make the diff wrong. The application's interceptors, its
Actions `MapAsync` and WITH CHECK run only at apply time, with the real save.

**What is guaranteed at apply time:**

- Every operation is prepared again as the caller sees the object *now*, its diff rendered again, and the
  plan refused if the rendering changed (other rights, other data). Then every check runs again before
  anything is written.
- The saves and deletes of a plan, and the plan's own "applied" record, commit in **one RavenDB
  `SaveChanges`**: one request, which the node that takes it applies as one transaction. Each write carries
  the etag captured at propose time, so a record changed by anyone in between — even during the apply —
  fails the whole request: the answer starts with `Conflict (409)` and nothing is saved. This is RavenDB's
  single-node transaction, as every Spark save is, not a cluster-wide one.
- Not covered: a write your interceptor or Actions class commits on its own (its own `SaveChangesAsync` or
  session) is outside the transaction.
- A **custom action** runs your code, which may commit by itself, so it cannot share a transaction: it is
  always a plan of its own (one operation). It goes through the same gates as the button; its check at
  propose time stops before the quota gates, which count only the real run.
- A create that an interceptor cancels fails the plan: on the web a cancel means "nothing happened", in a
  plan it would be a smaller change than the one approved.

Applied plans, and plans whose apply failed, are kept in the `McpPlans` collection
(`Spark:Mcp:PlanRetention`, default one year) as the record of what agents changed or tried to: user,
client, grant, summary, operations, the diff, and the ids creates were given. Proposed plans that are never
applied carry a 10-minute `@expires`; the database needs document expiration, which `UseSpark()` turns on
because `AddMcp` asks for it.

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
| External setup: always 401 | The user never signed in to the website through that provider, the configured issuer differs from the token's `iss`, the token's `aud` is not the canonical URL, it carries no `azp`/`client_id`, or the user disconnected the agent on the account page. The server log names the reason (event `SparkExternalAgentTokenRefused`). |
| A tool the user expects is missing | It needs `Write` and some layer gives only `Read`; or the type's `agentPermissions` is `None`; or the user lacks `Query` on it. |
| Local development: Claude Code fails with `UNABLE_TO_VERIFY_LEAF_SIGNATURE` (or `UNSUPPORTED_CONSTRAINT_TYPE`) on `https://localhost` | Claude Code does not trust the ASP.NET development certificate, not even through `NODE_EXTRA_CA_CERTS`, because that certificate is not a CA; `NODE_USE_SYSTEM_CA` and `dotnet dev-certs https --trust` do not help either (verified with 2.1.296). Use a local CA (for example mkcert): serve a `localhost` certificate it issued (`Kestrel__Certificates__Default__Path`/`KeyPath`), trust the CA in the OS store for the browser, VS Code, Codex and .NET, and start Claude Code and Gemini CLI with `NODE_EXTRA_CA_CERTS` pointing at the CA. Keep IP addresses out of the certificate when the CA is name-constrained: Claude Code refuses IP names under a constrained CA (`UNSUPPORTED_CONSTRAINT_TYPE`). |
| Results end with "truncated" | The page cap was reached; the agent should page with `skip`. |
