# PRD: GitHub Webhooks Integration for MintPlayer.Spark

## 1. Problem Statement

Spark applications need a simple, declarative way to react to GitHub webhook events (push, pull request, issues, check runs, etc.). Today, developers must manually wire up Octokit.Webhooks, write boilerplate for local development tunneling, and build custom infrastructure for forwarding production webhooks to dev machines. This PRD defines a first-class GitHub Webhooks integration for the Spark framework.

## 2. Goals

1. **One-liner setup**: `spark.AddGithubWebhooks(...)` registers everything needed.
2. **Message bus integration**: Webhook events are broadcast as typed messages on the Spark `IMessageBus`, so developers simply implement `IRecipient<T>` to react to events.
3. **Local development**: One-liner smee.io tunnel support for developers without a public URL.
4. **Production-to-dev forwarding**: When a "dev" GitHub App sends webhooks to the production deployment, production forwards them via WebSocket to the correct developer's local machine.
5. **Type safety**: Single generic message record `GitHubWebhookMessage<TEvent>` reusing Octokit's existing event types — zero boilerplate message classes to maintain.

## 3. Non-Goals

- Building a full GitHub bot framework (authenticated API calls, installation management) — developers use Octokit directly for that.
- Replacing Octokit.Webhooks — we extend `WebhookEventProcessor`, not replace it.
- UI components for webhook management.

## 4. Architecture Overview

### 4.1 New NuGet Packages

| Package | Purpose | Dependencies |
|---------|---------|-------------|
| `MintPlayer.Spark.Webhooks.GitHub` | Core: `SparkWebhookEventProcessor`, message types, extension methods, production WebSocket forwarder | `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark.Messaging.Abstractions`, `Octokit.Webhooks.AspNetCore` |
| `MintPlayer.Spark.Webhooks.GitHub.DevTunnel` | Dev-only: smee.io tunnel + WebSocket client for receiving forwarded webhooks | `MintPlayer.Spark.Webhooks.GitHub`, `Smee.IO.Client` |

**Rationale for separation**: The smee.io dependency (`Smee.IO.Client`) and WebSocket dev-client are only needed during development. Production deployments should not carry these dependencies. The DevTunnel package is conditionally referenced (e.g., via `Condition="'$(Configuration)'=='Debug'"` or an `#if DEBUG` guard in `Program.cs`).

### 4.2 High-Level Flow

```
                        ┌─────────────────────────────────────────────┐
                        │              Production Server              │
                        │                                             │
  GitHub ──POST──►  MapGitHubWebhooks()                               │
                        │                                             │
                        ▼                                             │
               SparkWebhookEventProcessor                             │
                   │            │                                     │
                   │            ▼                                     │
                   │   Is this from the Dev GitHub App?               │
                   │      YES ──► WebSocket forward to dev client     │
                   │                                                  │
                   ▼                                                  │
              IMessageBus.BroadcastAsync<TWebhookMessage>()           │
                   │                                                  │
                   ▼                                                  │
              IRecipient<TWebhookMessage> handlers                    │
                        └─────────────────────────────────────────────┘

                        ┌─────────────────────────────────────────────┐
                        │           Developer Machine (local)         │
                        │                                             │
  Option A: smee.io ──SSE──► SmeeService ──POST──► local webhooks     │
                        │                                             │
  Option B: WebSocket ◄──── Production server forwards dev webhooks   │
                        │         │                                   │
                        │         ▼                                   │
                        │   SparkWebhookEventProcessor (local)        │
                        │         │                                   │
                        │         ▼                                   │
                        │   IMessageBus ──► IRecipient<T> handlers    │
                        └─────────────────────────────────────────────┘
```

## 5. Design Decisions

### 5.1 Queue Strategy: Single Generic Message with Dynamic Queue Names

**Decision**: Use a single generic record `GitHubWebhookMessage<TEvent>` that wraps Octokit's existing strongly-typed event classes. Queue names are computed dynamically from the event type using the `BroadcastAsync(message, queueName)` overload.

**Alternatives Considered**:

| Approach | Pros | Cons |
|----------|------|------|
| A) One message class per event type | Static `[MessageQueue]`, matches existing Spark patterns | 60+ near-identical boilerplate records to maintain, must add one for each new GitHub event |
| **B) Single generic `<TEvent>` record** (chosen) | Zero boilerplate, automatic support for all Octokit event types, same type safety | Dynamic queue names (uses existing `BroadcastAsync` overload), no custom per-event properties |
| C) User-configurable routing rules | Maximum flexibility | Complex configuration, over-engineered for most use cases |

**Rationale**: Octokit.Webhooks already provides 60+ strongly-typed event classes (`PullRequestEvent`, `IssuesEvent`, `PushEvent`, etc.). Wrapping each one in a near-identical message class adds maintenance burden with no type-safety benefit. The generic approach gives developers the same IntelliSense and compile-time checking via `GitHubWebhookMessage<PullRequestEvent>`, while the library automatically supports any event type Octokit adds in the future.

### 5.2 Message Record Design

Two records: a non-generic base for catch-all recipients, and a generic one for event-specific recipients.

```csharp
// Catch-all base record — broadcast on queue "spark-github-all" for every webhook event
[MessageQueue("spark-github-all")]
public record GitHubWebhookMessage
{
    public required WebhookHeaders Headers { get; init; }
    public required long InstallationId { get; init; }
    public required string RepositoryFullName { get; init; }
    public required string EventType { get; init; }     // e.g., "pull_request", "issues"
    public required string EventJson { get; init; }     // raw JSON for dynamic processing
}

// Event-specific generic record — broadcast on a per-event-type queue
public record GitHubWebhookMessage<TEvent> where TEvent : WebhookEvent
{
    public required WebhookHeaders Headers { get; init; }
    public required long InstallationId { get; init; }
    public required string RepositoryFullName { get; init; }
    public required TEvent Event { get; init; }
}
```

**Queue naming**: The generic record's queue name is derived from the Octokit event type name at broadcast time:

```csharp
// e.g., "PullRequestEvent" → "spark-github-pull-request"
//       "IssuesEvent"      → "spark-github-issues"
var queueName = GitHubQueueNames.FromEventType<TEvent>();
await messageBus.BroadcastAsync(genericMessage, queueName, ct);
await messageBus.BroadcastAsync(catchAllMessage, ct); // uses [MessageQueue] attribute
```

The `GitHubQueueNames` helper converts PascalCase event type names to kebab-case queue names with a `spark-github-` prefix, stripping the `Event` suffix.

**Supported event types**: All event types from `Octokit.Webhooks` are supported automatically. The `SparkWebhookEventProcessor` overrides the most commonly used events initially, and more can be added trivially (each override is ~5 lines of identical code). Unhandled events are logged and silently dropped.

### 5.3 Dev vs. Production GitHub App Identification

GitHub includes the header `X-GitHub-Hook-Installation-Target-ID` (the GitHub App ID) and `X-GitHub-Hook-Installation-Target-Type` (value: `integration` for GitHub Apps) on every webhook delivery. The JSON payload also contains `installation.app_id`.

**Configuration**:
```csharp
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = "...";
    options.ProductionAppId = 123456;      // Production GitHub App ID
    options.DevelopmentAppId = 789012;     // Development GitHub App ID (optional)
});
```

When `DevelopmentAppId` is configured and a webhook arrives with a matching App ID:
- Production does **not** process the webhook through `IMessageBus` locally
- Instead, forwards the raw headers + body via WebSocket to connected dev clients

### 5.4 Smee.io Tunnel (Separate Package)

For developers without a deployed production server, smee.io provides a public URL that tunnels webhooks to localhost via Server-Sent Events.

```csharp
// In Program.cs (development only)
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = "...";
    options.AddSmeeDevTunnel("https://smee.io/your-channel-id");
});
```

**Implementation**: A `BackgroundService` that connects to the smee.io channel using `Smee.IO.Client`, receives webhook payloads via SSE, and POSTs them to the local webhook endpoint (e.g., `http://localhost:5000/api/github/webhooks`).

**Signature validation caveat**: smee.io relays webhook payloads as JSON through Server-Sent Events, which may reformat (pretty-print) the body. Before validating the `X-Hub-Signature-256` HMAC, the JSON body must be **re-minimized** to match the exact bytes GitHub signed. This is the same approach used in the SlingBot implementation.

### 5.5 WebSocket Dev Forwarding (Same Package as Smee)

For teams that already have a production deployment, the "dev" GitHub App's webhooks are forwarded from production to the developer's local machine.

**Production side** (in `MintPlayer.Spark.Webhooks.GitHub`):
- WebSocket endpoint at `/spark/github/dev-ws`
- Accepts connections with a handshake (GitHub token for identity)
- Maintains list of connected dev clients
- When a "dev app" webhook arrives, forwards raw headers + body to all connected clients

**Dev client side** (in `MintPlayer.Spark.Webhooks.GitHub.DevTunnel`):
```csharp
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = "...";
    options.AddWebSocketDevTunnel("wss://myapp.com/spark/github/dev-ws", githubToken: "...");
});
```

**Implementation**: A `BackgroundService` that maintains a WebSocket connection to production, receives forwarded webhooks, and POSTs them to the local webhook endpoint.

## 6. API Surface

### 6.1 Package: MintPlayer.Spark.Webhooks.GitHub

**Extension Methods**:
```csharp
public static class SparkGitHubWebhooksExtensions
{
    // Primary registration on ISparkBuilder
    public static ISparkBuilder AddGithubWebhooks(
        this ISparkBuilder builder,
        Action<GitHubWebhooksOptions> configure);
}
```

**Options**:
```csharp
public class GitHubWebhooksOptions
{
    // Stores deferred service registrations from AddSmeeDevTunnel / AddWebSocketDevTunnel
    internal IServiceCollection Services { get; set; } = default!;

    /// <summary>Webhook secret configured in the GitHub App settings.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Webhook endpoint path. Defaults to "/api/github/webhooks".</summary>
    public string WebhookPath { get; set; } = "/api/github/webhooks";

    /// <summary>GitHub App ID for the production app.</summary>
    public long? ProductionAppId { get; set; }

    /// <summary>
    /// GitHub App ID for the development app. When set, webhooks from this app
    /// are forwarded to connected dev clients instead of being processed locally.
    /// </summary>
    public long? DevelopmentAppId { get; set; }

    /// <summary>
    /// WebSocket path for dev forwarding endpoint. Defaults to "/spark/github/dev-ws".
    /// Only active when DevelopmentAppId is set.
    /// </summary>
    public string DevWebSocketPath { get; set; } = "/spark/github/dev-ws";

    /// <summary>
    /// Allowed GitHub usernames for WebSocket dev connections.
    /// If empty, all authenticated connections are accepted.
    /// </summary>
    public List<string> AllowedDevUsers { get; set; } = [];
}
```

The `AddSmeeDevTunnel` and `AddWebSocketDevTunnel` methods are extension methods on `GitHubWebhooksOptions`, provided by the `MintPlayer.Spark.Webhooks.GitHub.DevTunnel` package (see section 6.2). This keeps dev-tunnel configuration inside the same options lambda.

**SparkWebhookEventProcessor** (internal, auto-registered):
```csharp
internal class SparkWebhookEventProcessor : WebhookEventProcessor
{
    // For each supported event type, a thin override:
    // 1. Call shared helper: HandleWebhookAsync<TEvent>(headers, event, ct)
    // 2. Helper extracts metadata (installation ID, repo)
    // 3. Checks if dev app → forward via WebSocket
    // 4. Otherwise → messageBus.BroadcastAsync(new GitHubWebhookMessage<TEvent>{...}, queueName, ct)
}
```

### 6.2 Package: MintPlayer.Spark.Webhooks.GitHub.DevTunnel

**Extension Methods on `GitHubWebhooksOptions`**:
```csharp
public static class GitHubWebhooksDevTunnelExtensions
{
    /// <summary>
    /// Adds a smee.io tunnel for receiving webhooks during local development.
    /// The developer configures the smee channel URL as the Webhook URL in the GitHub App settings.
    /// </summary>
    public static GitHubWebhooksOptions AddSmeeDevTunnel(
        this GitHubWebhooksOptions options,
        string smeeChannelUrl);

    /// <summary>
    /// Connects to a production server's WebSocket endpoint to receive forwarded dev webhooks.
    /// The production server must have DevelopmentAppId configured.
    /// Uses the GitHub token to authenticate and determine the developer's GitHub username.
    /// </summary>
    public static GitHubWebhooksOptions AddWebSocketDevTunnel(
        this GitHubWebhooksOptions options,
        string productionWebSocketUrl,
        string githubToken);
}
```

### 6.3 Consumer Usage

**Registration** (in `Program.cs`):
```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddActions();
    spark.AddMessaging();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"]!;
        options.ProductionAppId = 123456;
        options.DevelopmentAppId = 789012;

        // Development only — pick ONE:
        // Option A: smee.io tunnel (no production deployment needed)
        options.AddSmeeDevTunnel(builder.Configuration["GitHub:SmeeChannelUrl"]!);
        // Option B: WebSocket from production (production deployment exists)
        // options.AddWebSocketDevTunnel("wss://myapp.com/spark/github/dev-ws", devGithubToken);
    });
});
```

**Endpoint mapping** (the existing Octokit `MapGitHubWebhooks` is used):
```csharp
app.UseSpark();
app.MapSpark();
// GitHub webhook endpoint is mapped automatically via SparkModuleRegistry.AddEndpoints
```

**Handling webhook events** (in any Spark application service):

No `[Register]` attribute is needed — the `RecipientRegistrationGenerator` source generator automatically discovers all `IRecipient<T>` implementations and registers them in DI.

```csharp
public class PullRequestHandler : IRecipient<GitHubWebhookMessage<PullRequestEvent>>
{
    public async Task HandleAsync(GitHubWebhookMessage<PullRequestEvent> message, CancellationToken ct)
    {
        var pr = message.Event.PullRequest;
        var action = message.Event.Action;

        if (action == "opened")
        {
            // React to new PR
            Console.WriteLine($"New PR #{pr.Number}: {pr.Title} in {message.RepositoryFullName}");
        }
    }
}

public class IssueHandler : IRecipient<GitHubWebhookMessage<IssuesEvent>>
{
    public async Task HandleAsync(GitHubWebhookMessage<IssuesEvent> message, CancellationToken ct)
    {
        if (message.Event.Action == "opened")
        {
            // React to new issue
        }
    }
}
```

## 7. Internal Implementation Details

### 7.1 SparkWebhookEventProcessor

Each event override delegates to a shared generic helper:

```csharp
protected override Task ProcessPullRequestWebhookAsync(
    WebhookHeaders headers, PullRequestEvent evt, PullRequestAction action, CancellationToken ct)
    => HandleWebhookAsync(headers, evt, ct);

protected override Task ProcessIssuesWebhookAsync(
    WebhookHeaders headers, IssuesEvent evt, IssuesAction action, CancellationToken ct)
    => HandleWebhookAsync(headers, evt, ct);

// ... same one-liner for each supported event type

private async Task HandleWebhookAsync<TEvent>(
    WebhookHeaders headers, TEvent evt, string eventJson, CancellationToken ct)
    where TEvent : WebhookEvent
{
    // 1. Check if dev app → forward raw headers + body via WebSocket
    // 2. Otherwise → broadcast both catch-all and event-specific messages

    var installationId = evt.Installation?.Id ?? 0;
    var repoFullName = evt.Repository?.FullName ?? string.Empty;

    // Event-specific message (e.g., queue "spark-github-pull-request")
    var queueName = GitHubQueueNames.FromEventType<TEvent>();
    var typedMessage = new GitHubWebhookMessage<TEvent>
    {
        Headers = headers,
        InstallationId = installationId,
        RepositoryFullName = repoFullName,
        Event = evt,
    };
    await messageBus.BroadcastAsync(typedMessage, queueName, ct);

    // Catch-all message (queue "spark-github-all")
    var catchAllMessage = new GitHubWebhookMessage
    {
        Headers = headers,
        InstallationId = installationId,
        RepositoryFullName = repoFullName,
        EventType = headers.Event ?? string.Empty,
        EventJson = eventJson,
    };
    await messageBus.BroadcastAsync(catchAllMessage, ct);
}
```

Adding support for a new event type = one additional one-liner override.

### 7.2 Dev WebSocket Forwarder (Production Side)

- Registered as singleton `IDevWebSocketService`
- Manages list of `DevClient { WebSocket, GitHubUsername }`
- **Handshake protocol** (same as SlingBot):
  1. Client connects to WebSocket endpoint and sends a JSON handshake: `{ "GithubToken": "ghp_..." }`
  2. Server calls GitHub API (`Octokit`) with the token to resolve the authenticated username
  3. Server checks username against `AllowedDevUsers` list (if configured; if empty, all authenticated users are accepted)
  4. Server maintains the `DevClient` in-memory with the resolved username
- **Wire format** (matches GitHub's HTTP format and SlingBot):
  ```
  Header-Name: Value\n
  Header-Name: Value\n
  \n
  {json-body}
  ```
- When a dev-app webhook arrives, forwards to all connected dev clients matching the filter

### 7.3 SmeeService (Dev Side)

- `BackgroundService` that runs only when `AddSmeeDevTunnel` is called
- Creates `SmeeClient` connected to configured channel URL
- On each `OnMessage` event:
  1. Extracts headers and body from smee event
  2. **Re-minimizes the JSON body** before forwarding — smee.io may reformat the JSON, which would break the `X-Hub-Signature-256` HMAC validation. The body must be byte-identical to what GitHub originally signed.
  3. POSTs to local webhook endpoint using `HttpClient`
  4. This triggers the standard `SparkWebhookEventProcessor` → `IMessageBus` flow

### 7.4 WebSocketDevClient (Dev Side)

- `BackgroundService` that runs only when `AddWebSocketDevTunnel` is called
- Connects to production WebSocket endpoint with GitHub token handshake
- On each received message:
  1. Parses headers and body from the wire format
  2. POSTs to local webhook endpoint using `HttpClient`
  3. Triggers standard processing flow
- Auto-reconnects with exponential backoff on disconnection

## 8. Configuration via appsettings.json

```json
{
  "Spark": {
    "RavenDb": { "Urls": ["http://localhost:8080"], "Database": "MyApp" }
  },
  "GitHub": {
    "WebhookSecret": "whsec_...",
    "ProductionAppId": 123456,
    "DevelopmentAppId": 789012,
    "SmeeChannelUrl": "https://smee.io/abc123",
    "DevWebSocketUrl": "wss://myapp.com/spark/github/dev-ws",
    "DevGitHubToken": "ghp_..."
  }
}
```

The core options can be bound from configuration, with dev-tunnel methods called inline:
```csharp
spark.AddGithubWebhooks(options =>
{
    builder.Configuration.GetSection("GitHub").Bind(options);

    // Dev tunnel (if needed) — still configured on the options object
    options.AddSmeeDevTunnel(builder.Configuration["GitHub:SmeeChannelUrl"]!);
});
```

## 9. Project Structure

```
MintPlayer.Dotnet.SocketExtensions/             # Moved from SlingBot repo
├── SocketExtensions.cs                          # ReadMessage, WriteMessage, ReadObject, WriteObject
└── MintPlayer.Dotnet.SocketExtensions.csproj

MintPlayer.Spark.Webhooks.GitHub/
├── Extensions/
│   └── SparkBuilderExtensions.cs          # AddGithubWebhooks() on ISparkBuilder
├── Configuration/
│   └── GitHubWebhooksOptions.cs           # Options POCO (with internal Services for deferred registration)
├── Messages/
│   ├── GitHubWebhookMessage.cs            # Non-generic catch-all + generic GitHubWebhookMessage<TEvent>
│   └── GitHubQueueNames.cs               # Event type → queue name helper
├── Services/
│   ├── SparkWebhookEventProcessor.cs      # Extends WebhookEventProcessor
│   ├── IDevWebSocketService.cs            # Interface for dev forwarding
│   └── DevWebSocketService.cs             # WebSocket server for dev clients (validates GitHub token → username)
├── MintPlayer.Spark.Webhooks.GitHub.csproj
└── README.md

MintPlayer.Spark.Webhooks.GitHub.DevTunnel/
├── Extensions/
│   └── GitHubWebhooksDevTunnelExtensions.cs  # AddSmeeDevTunnel, AddWebSocketDevTunnel on GitHubWebhooksOptions
├── Services/
│   ├── SmeeBackgroundService.cs           # Smee.io SSE listener (re-minimizes JSON before signature validation)
│   └── WebSocketDevClientService.cs       # WebSocket client to production
├── MintPlayer.Spark.Webhooks.GitHub.DevTunnel.csproj
└── README.md

Demo/WebhooksDemo/                               # New demo application
├── WebhooksDemo/                                # ASP.NET Core backend
│   ├── Program.cs
│   ├── Recipients/                              # IRecipient<T> implementations
│   └── appsettings.json
└── WebhooksDemo.Library/                        # Shared library (Actions, etc.)
```

## 10. Scenarios

### Scenario A: Developer with no production deployment

1. Developer creates a GitHub App, sets webhook URL to their smee.io channel
2. In `Program.cs`:
   ```csharp
   spark.AddGithubWebhooks(options =>
   {
       options.WebhookSecret = "...";
       options.AddSmeeDevTunnel("https://smee.io/abc123");
   });
   ```
3. Developer implements `IRecipient<GitHubWebhookMessage<PullRequestEvent>>` etc.
4. Smee.io forwards GitHub webhooks → local endpoint → message bus → recipients

### Scenario B: Team with production deployment

1. Team creates two GitHub Apps: **MyBot** (prod, ID 123) and **MyBot-Dev** (dev, ID 456)
2. Both apps have the same webhook URL: `https://myapp.com/api/github/webhooks`
3. Production `Program.cs`:
   ```csharp
   spark.AddGithubWebhooks(o =>
   {
       o.WebhookSecret = "...";
       o.ProductionAppId = 123;
       o.DevelopmentAppId = 456;
   });
   ```
4. Developer's local `Program.cs`:
   ```csharp
   spark.AddGithubWebhooks(options =>
   {
       options.WebhookSecret = "...";
       options.AddWebSocketDevTunnel("wss://myapp.com/spark/github/dev-ws", devToken);
   });
   ```
5. Production webhooks (App ID 123) → processed by production recipients
6. Dev webhooks (App ID 456) → forwarded via WebSocket → processed by local recipients

### Scenario C: Simple production-only setup

1. Single GitHub App, no dev forwarding needed
2. ```csharp
   spark.AddGithubWebhooks(o => o.WebhookSecret = "...");
   ```
3. All webhooks processed directly by `IRecipient<GitHubWebhookMessage<TEvent>>` handlers

## 11. Dependencies

| Package | Version | Used By |
|---------|---------|---------|
| `Octokit.Webhooks.AspNetCore` | 3.x | Core |
| `Octokit.Webhooks` | 3.x | Core (transitive) |
| `MintPlayer.Spark.Abstractions` | current | Core |
| `MintPlayer.Spark.Messaging.Abstractions` | current | Core |
| `Octokit` | 13.x | Core (GitHub API for WebSocket handshake username lookup) |
| `Smee.IO.Client` | 1.0.x | DevTunnel only |
| `MintPlayer.Dotnet.SocketExtensions` | 10.x (moved from SlingBot repo into this repo) | Core (WebSocket helpers) |

## 12. Resolved Design Questions

1. **`MintPlayer.Dotnet.SocketExtensions`**: Move the library from the SlingBot repository into this repository. It will be published from here going forward. The SlingBot repo will be archived.

2. **WebSocket handshake validation**: Same approach as SlingBot — use the developer's GitHub token to call the GitHub API, determine the GitHub username, and downstream webhooks to connected clients filtered by username. This requires an `Octokit` dependency in the core package for the production-side WebSocket endpoint.

3. **Catch-all recipient**: Supported. The `SparkWebhookEventProcessor` will broadcast **two messages** for each webhook event:
   - A non-generic `GitHubWebhookMessage` (base record) on queue `spark-github-all`
   - A generic `GitHubWebhookMessage<TEvent>` on the event-specific queue (e.g., `spark-github-pull-request`)

   This allows developers to implement either:
   - `IRecipient<GitHubWebhookMessage>` — catch-all for any webhook event
   - `IRecipient<GitHubWebhookMessage<PullRequestEvent>>` — specific event type

   The non-generic base record carries the same metadata (`Headers`, `InstallationId`, `RepositoryFullName`) plus `EventJson` (raw JSON string) and `EventType` (string, e.g., `"pull_request"`) for dynamic processing.

4. **Wire format**: Use `headers\n\nbody` text format (same as SlingBot). This matches how GitHub itself sends webhooks over HTTP, so smee.io forwarding, WebSocket forwarding, and the actual webhook endpoint all use the same format. No unnecessary abstraction layers.

## 13. Implementation Plan

### Phase 0: Prerequisites
1. Move `MintPlayer.Dotnet.SocketExtensions` from the SlingBot repo into this solution
2. Add to solution file and verify NuGet packaging

### Phase 1: Core Package (`MintPlayer.Spark.Webhooks.GitHub`)
1. Create project, define `GitHubWebhookMessage` (catch-all) + `GitHubWebhookMessage<TEvent>` (generic) records and `GitHubQueueNames` helper
2. Implement `SparkWebhookEventProcessor` with generic `HandleWebhookAsync<TEvent>` — broadcasts both catch-all and typed messages to `IMessageBus`
3. Implement `AddGithubWebhooks()` extension method on `ISparkBuilder` + `GitHubWebhooksOptions`
4. Implement webhook signature validation (HMAC-SHA256 via `X-Hub-Signature-256`)
5. Register endpoint via `SparkModuleRegistry.AddEndpoints`
6. Add to solution file

### Phase 2: Dev Forwarding (Production Side)
1. Implement `DevWebSocketService` — WebSocket server for dev clients
2. WebSocket handshake: validate GitHub token via Octokit API → resolve username → filter by `AllowedDevUsers`
3. Add dev-app detection logic in `SparkWebhookEventProcessor` (check `X-GitHub-Hook-Installation-Target-ID` against `DevelopmentAppId`)
4. Forward dev webhooks to connected clients using `headers\n\nbody` wire format
5. Register WebSocket endpoint via `SparkModuleRegistry.AddEndpoints`

### Phase 3: DevTunnel Package (`MintPlayer.Spark.Webhooks.GitHub.DevTunnel`)
1. Create project, implement `SmeeBackgroundService` — including JSON re-minimization before signature validation
2. Implement `WebSocketDevClientService` with auto-reconnect and exponential backoff
3. Implement `AddSmeeDevTunnel()` and `AddWebSocketDevTunnel()` as extension methods on `GitHubWebhooksOptions`
4. Test locally with smee.io channel

### Phase 4: Demo App & Documentation
1. Create new `Demo/WebhooksDemo/` application (ASP.NET Core + Library project)
2. Implement sample `IRecipient<GitHubWebhookMessage<PullRequestEvent>>` and `IRecipient<GitHubWebhookMessage>` (catch-all) handlers
3. Write guide document
4. Publish NuGet packages
