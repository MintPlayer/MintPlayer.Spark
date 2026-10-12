# MintPlayer.Spark.Mcp

A [Model Context Protocol](https://modelcontextprotocol.io) server inside a Spark application. AI agents
(Claude Code, Codex, Gemini) connect to `/mcp` and read the application's data **as the user who connected
them**: `security.json`, row security and attribute redaction apply exactly as in the web UI.

> **Preview.** Agents sign in with personal access tokens (`spat_…`, created on the account page of an app
> with `MintPlayer.Spark.Authorization`; without that package every call runs as `anonymous`), through the
> app's embedded authorization server (`idp.ForMcp()`, `MintPlayer.Spark.IdentityProvider`), or through an
> external one (`spark.AddExternalAgentAuthorizationServer(...)`, `MintPlayer.Spark.Authorization`). See
> `docs/spark_mcp_plan.md`.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddMcp(mcp =>
    {
        mcp.ServerName = "Contracts";
        mcp.ToolPrefix = "contracts";              // contracts_discover, contracts_query, ...
        mcp.Instructions = "Contracts, customers and invoices of the signed-in user.";
        mcp.AgentPermissions = _ => EAgentPermission.Read;
        mcp.AddTools<ContractTools>();              // optional, written unprefixed
    });
});
```

Nothing is exposed until a type opts in, in its model JSON: `"agentPermissions": "Read"`.

| Option | Default | |
|---|---|---|
| `ServerName` | `Spark` | Shown to agents; the tool prefix defaults to its slug. |
| `ToolPrefix` | slug of `ServerName` | Set it explicitly before publishing: users allow-list tools by name. |
| `Instructions` | none | Sent at `initialize`. |
| `Authentication` | `All` | Effective = flags ∩ what is wired; an explicit flag without its wiring fails at startup. |
| `AgentPermissions` | `_ => Read` | Per call; anonymous callers are clamped to `Read`. |
| `Path` | `/mcp` | `Spark:Mcp:Path`. Startup fails if already mapped. |
| `CanonicalUrl` | `Spark:Auth:PublicBaseUrl` + `Path` | `Spark:Mcp:CanonicalUrl`. The only allowed browser `Origin`. |
| `RequireSignIn` | `false` | Every request without a token, `initialize` included, answers 401. |
| `Quota` | 120 / 30 per minute | Per signed-in user / per anonymous address (`Spark:Mcp:Quota`). |
| `PlanRetention` | 365 days | `Spark:Mcp:PlanRetention`. How long applied (and failed) plans are kept as the write audit. |

Generic tools: `discover`, `describe`, `query`, `get`, and the writes `propose_changes` → `apply_changes`
(signed-in callers with `Write`; plans in the `McpPlans` collection, which needs document expiration, enabled by
`UseSpark()`). A plan's saves and deletes commit in one RavenDB transaction or not at all; a custom action is a plan
of its own. Your own tools use checked services only
(`IDatabaseAccess` without its `…UncheckedAsync` methods, `IQueryExecutor`, `IPermissionService`; analyzer
SPARK050 flags a raw RavenDB session) and answer through `SparkMcpResult`.

The full guide is [docs/guide-mcp.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-mcp.md).
