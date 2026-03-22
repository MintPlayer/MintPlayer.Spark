# GitHub Webhooks

Spark provides first-class GitHub webhook integration through two optional packages. Webhook events are broadcast as typed messages on the Spark message bus, so you handle them by implementing `IRecipient<T>` — the same pattern used for all Spark messaging.

## Packages

| Package | Purpose |
|---|---|
| `MintPlayer.Spark.Webhooks.GitHub` | Core: webhook processing, typed messages, signature validation, production-side dev forwarding |
| `MintPlayer.Spark.Webhooks.GitHub.DevTunnel` | Dev-only: smee.io tunnel and WebSocket client for receiving forwarded webhooks locally |

The DevTunnel package is only needed during development. Production deployments only need the core package.

## Prerequisites

You need a GitHub App with webhook events enabled. Manage your GitHub Apps here:

- **Personal account**: https://github.com/settings/apps
- **Organization**: `https://github.com/organizations/{org_name}/settings/apps`

When creating the app, configure the Webhook URL (your production endpoint or smee.io channel) and select the events you want to receive.

## Setup

### 1. Install packages

Add project references (or NuGet package references) to your web application:

```xml
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub" Version="10.0.0-preview.19" />
<!-- Development only: -->
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub.DevTunnel" Version="10.0.0-preview.19" />
```

### 2. Register in Program.cs

```csharp
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions; // dev-tunnel only

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
    });
});
```

The webhook endpoint is mapped automatically when you call `app.MapSpark()`. By default it listens at `/api/github/webhooks` (configurable via `options.WebhookPath`).

### 3. Handle webhook events

Create a class that implements `IRecipient<GitHubWebhookMessage<TEvent>>` for the event type you want to handle. The source generator auto-registers it in DI — no `[Register]` attribute needed.

```csharp
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks.Events;

public partial class OnPullRequest : IRecipient<GitHubWebhookMessage<PullRequestEvent>>
{
    [Inject] private readonly ILogger<OnPullRequest> _logger;

    public Task HandleAsync(GitHubWebhookMessage<PullRequestEvent> message, CancellationToken ct)
    {
        var pr = message.Event.PullRequest;
        _logger.LogInformation("PR #{Number} ({Action}): {Title}",
            pr.Number, message.Event.Action, pr.Title);
        return Task.CompletedTask;
    }
}
```

You can create recipients for any Octokit event type: `PullRequestEvent`, `IssuesEvent`, `PushEvent`, `CheckRunEvent`, `IssueCommentEvent`, etc.

### Catch-all recipient

To handle every webhook event regardless of type, implement `IRecipient<GitHubWebhookMessage>` (the non-generic version):

```csharp
public partial class LogAllWebhooks : IRecipient<GitHubWebhookMessage>
{
    [Inject] private readonly ILogger<LogAllWebhooks> _logger;

    public Task HandleAsync(GitHubWebhookMessage message, CancellationToken ct)
    {
        _logger.LogInformation("Webhook: {EventType} from {Repo}",
            message.EventType, message.RepositoryFullName);
        return Task.CompletedTask;
    }
}
```

Both the typed and catch-all messages are broadcast for every event, so you can mix and match.

## Message types

| Type | Queue | Use case |
|---|---|---|
| `GitHubWebhookMessage<TEvent>` | `spark-github-{event-name}` (e.g., `spark-github-pull-request`) | Handle a specific event type with full IntelliSense on the Octokit event model |
| `GitHubWebhookMessage` | `spark-github-all` | Handle all events generically; provides `EventType` (string) and `EventJson` (raw JSON) |

Both records include `Headers`, `InstallationId`, and `RepositoryFullName`.

## Configuration

### appsettings.json

```json
{
  "GitHub": {
    "WebhookSecret": "whsec_...",
    "ProductionAppId": "123456",
    "DevelopmentAppId": "789012",
    "SmeeChannelUrl": "https://smee.io/your-channel-id",
    "DevWebSocketUrl": "wss://myapp.com/spark/github/dev-ws",
    "DevGitHubToken": "ghp_..."
  }
}
```

### Options reference

| Option | Default | Description |
|---|---|---|
| `WebhookSecret` | `""` | Webhook secret from your GitHub App settings. Used for HMAC-SHA256 signature validation. |
| `WebhookPath` | `"/api/github/webhooks"` | Endpoint path for receiving webhooks. |
| `ProductionAppId` | `null` | GitHub App ID for the production app. |
| `DevelopmentAppId` | `null` | GitHub App ID for the dev app. When set, webhooks from this app are forwarded to dev clients instead of being processed locally. |
| `DevWebSocketPath` | `"/spark/github/dev-ws"` | WebSocket endpoint path for dev client connections. |
| `AllowedDevUsers` | `[]` | GitHub usernames allowed to connect via WebSocket. Empty = all authenticated users. |

## Local development

There are two ways to receive webhooks on your local machine.

### Option A: smee.io tunnel (no production deployment needed)

1. Go to [smee.io](https://smee.io/) and create a new channel
2. In your GitHub App settings, set the Webhook URL to the smee.io channel URL
3. Configure your app:

```csharp
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
    options.AddSmeeDevTunnel(builder.Configuration["GitHub:SmeeChannelUrl"]!);
});
```

The `SmeeBackgroundService` connects to the smee.io channel via Server-Sent Events, re-minimizes the JSON body (required for correct signature validation), and forwards it to your local webhook endpoint.

### Option B: WebSocket forwarding from production

When your app is already deployed, you can create two GitHub Apps (e.g., **MyBot** and **MyBot-Dev**) pointing to the same production webhook URL. Production processes its own webhooks normally, and forwards dev-app webhooks to connected developers via WebSocket.

**Production `Program.cs`:**
```csharp
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = "...";
    options.ProductionAppId = 123456;
    options.DevelopmentAppId = 789012;
});
```

**Developer's local `Program.cs`:**
```csharp
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = "...";
    options.AddWebSocketDevTunnel(
        builder.Configuration["GitHub:DevWebSocketUrl"]!,
        builder.Configuration["GitHub:DevGitHubToken"]!);
});
```

The WebSocket handshake validates the developer's GitHub token against the GitHub API to determine their username. If `AllowedDevUsers` is configured, only listed users can connect.

## Signature validation

All webhook payloads are validated using the `X-Hub-Signature-256` HMAC-SHA256 header. This happens in the `SparkWebhookEventProcessor` regardless of how the webhook was received (direct HTTP, smee.io, or WebSocket).

When webhooks arrive through smee.io, the JSON body may be reformatted during SSE relay. The `SmeeBackgroundService` re-minimizes the JSON before forwarding to ensure the signature matches what GitHub originally signed.

## Supported events

The processor currently overrides these Octokit event types:

- `PushEvent`
- `IssuesEvent`
- `IssueCommentEvent`
- `PullRequestEvent`
- `PullRequestReviewEvent`
- `PullRequestReviewCommentEvent`
- `CheckRunEvent`
- `CheckSuiteEvent`
- `InstallationEvent`
- `RepositoryEvent`

Adding a new event type requires a single one-liner override in `SparkWebhookEventProcessor`. Unhandled events are silently dropped.

## Wire format

WebSocket dev forwarding uses the same format as GitHub's HTTP requests:

```
Header-Name: Value
Header-Name: Value

{json-body}
```

Headers and body are separated by a blank line (`\n\n`).
