# MintPlayer.Spark.Webhooks.GitHub

GitHub webhook integration for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark). Webhook events are broadcast as typed messages on the Spark message bus, so you handle them by implementing `IRecipient<T>` -- the same pattern used for all Spark messaging.

## Features

- Receive and validate GitHub App webhooks with HMAC-SHA256 signature verification
- Strongly-typed event handling via `IRecipient<GitHubWebhookMessage<TEvent>>`
- Catch-all handler for processing every webhook event generically
- Authenticated GitHub API access via `IGitHubInstallationService` (comment on issues, review PRs, etc.)
- Dev tunneling for local development (smee.io or WebSocket forwarding from production)
- Auto-registration of recipients and services via source generators

## Packages

| Package | Purpose |
|---|---|
| `MintPlayer.Spark.Webhooks.GitHub` | Core: webhook processing, typed messages, signature validation, GitHub API client |
| `MintPlayer.Spark.Webhooks.GitHub.DevTunnel` | Dev-only: smee.io tunnel and WebSocket client for receiving forwarded webhooks locally |

The DevTunnel package is only needed during development. Production deployments only need the core package.

## Prerequisites

You need a **GitHub App** with webhook events enabled. Create one at:

- **Personal account**: https://github.com/settings/apps
- **Organization**: `https://github.com/organizations/{org_name}/settings/apps`

When creating the app, configure the **Webhook URL** (your production endpoint or smee.io channel) and select the events you want to receive.

For API calls (e.g. commenting on issues), you also need the app's **Client ID** and **private key** (.pem file).

## Installation

```xml
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub" Version="10.0.0-preview.22" />

<!-- Development only: -->
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub.DevTunnel" Version="10.0.0-preview.22" />
```

## Quick start

### 1. Register in Program.cs

```csharp
using MintPlayer.Spark.Webhooks.GitHub.Extensions;

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;

        // Required for GitHub API calls (IGitHubInstallationService):
        options.ClientId = builder.Configuration["GitHub:ClientId"];
        options.PrivateKeyPath = builder.Configuration["GitHub:PrivateKeyPath"];
    });
});
```

The webhook endpoint is mapped automatically when you call `app.MapSpark()`. By default it listens at `/api/github/webhooks` (configurable via `options.WebhookPath`).

### 2. Handle webhook events

Create a class that implements `IRecipient<GitHubWebhookMessage<TEvent>>` for the event type you want to handle. The source generator auto-registers it in DI.

```csharp
using MintPlayer.SourceGenerators.Attributes;
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

### 3. Calling the GitHub API from a recipient

Inject `IGitHubInstallationService` to get an authenticated `IGitHubClient` scoped to the GitHub App installation that triggered the webhook. This lets your recipients call the GitHub API -- post comments, create reviews, update labels, and more.

```csharp
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.Issues;

public partial class LogIssues : IRecipient<GitHubWebhookMessage<IssuesEvent>>
{
    [Inject] private readonly ILogger<LogIssues> _logger;
    [Inject] private readonly IGitHubInstallationService _gitHubInstallationService;

    public async Task HandleAsync(GitHubWebhookMessage<IssuesEvent> message, CancellationToken ct)
    {
        var issue = message.Event.Issue;
        _logger.LogInformation("Issue #{Number} ({Action}): {Title} in {Repo}",
            issue.Number, message.Event.Action, issue.Title, message.RepositoryFullName);

        if (message.Event.Action == IssuesActionValue.Opened)
        {
            var githubClient = await _gitHubInstallationService.CreateClientAsync(message.InstallationId);
            await githubClient.Issue.Comment.Create(
                message.Event.Repository!.Id, (int)issue.Number, "Thanks for creating this issue");
        }
    }
}
```

`CreateClientAsync` authenticates as the GitHub App by creating a JWT signed with the app's private key, then exchanges it for a short-lived installation access token. This requires `ClientId` and either `PrivateKeyPem` or `PrivateKeyPath` to be configured in options.

## Catch-all recipient

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
    "ClientId": "Iv1.abc123",
    "PrivateKeyPath": "my-app.pem",
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
| `ClientId` | `null` | GitHub App Client ID. Required for `IGitHubInstallationService` API calls. |
| `PrivateKeyPem` | `null` | GitHub App private key PEM content (inline). Either this or `PrivateKeyPath` is required for API calls. |
| `PrivateKeyPath` | `null` | Path to the GitHub App private key `.pem` file. Relative paths are resolved from the working directory. |

### Full Program.cs example

```csharp
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;

builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;

        // GitHub API authentication
        options.ClientId = builder.Configuration["GitHub:ClientId"];
        options.PrivateKeyPath = builder.Configuration["GitHub:PrivateKeyPath"];

        // Production/dev app IDs (for WebSocket forwarding)
        if (long.TryParse(builder.Configuration["GitHub:ProductionAppId"], out var prodId))
            options.ProductionAppId = prodId;
        if (long.TryParse(builder.Configuration["GitHub:DevelopmentAppId"], out var devId))
            options.DevelopmentAppId = devId;

        // Local development: smee.io tunnel
        var smeeUrl = builder.Configuration["GitHub:SmeeChannelUrl"];
        if (!string.IsNullOrEmpty(smeeUrl))
        {
            options.AddSmeeDevTunnel(smeeUrl);
        }
    });
});
```

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

The `SmeeBackgroundService` connects to the smee.io channel via Server-Sent Events, re-minimizes the JSON body (required for correct signature validation), and feeds it into your local webhook processor.

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

The processor currently handles these Octokit event types:

| Event type | Queue name |
|---|---|
| `PushEvent` | `spark-github-push` |
| `IssuesEvent` | `spark-github-issues` |
| `IssueCommentEvent` | `spark-github-issue-comment` |
| `PullRequestEvent` | `spark-github-pull-request` |
| `PullRequestReviewEvent` | `spark-github-pull-request-review` |
| `PullRequestReviewCommentEvent` | `spark-github-pull-request-review-comment` |
| `CheckRunEvent` | `spark-github-check-run` |
| `CheckSuiteEvent` | `spark-github-check-suite` |
| `InstallationEvent` | `spark-github-installation` |
| `RepositoryEvent` | `spark-github-repository` |

Adding a new event type requires a single one-liner override in `SparkWebhookEventProcessor`. Unhandled events are silently dropped by Octokit.

## Docker deployment

The repository includes a production-ready `docker-compose.yml` at `Demo/WebhooksDemo/docker-compose.yml` with `${...}` placeholders for secrets. Create a `.env` file on your server (see `Demo/WebhooksDemo/.env.example`):

```env
GITHUB_WEBHOOK_SECRET=whsec_your_webhook_secret
GITHUB_APP_CLIENT_ID=Iv1.your_client_id
GITHUB_PRODUCTION_APP_ID=123456
TRAEFIK_HOST=spark-webhooks.example.com
```

Place your GitHub App private key alongside it:

```bash
cp ~/my-app.private-key.pem /var/www/webhooks-demo/github-app.pem
chmod 600 /var/www/webhooks-demo/github-app.pem
```

The compose file mounts the PEM file read-only into the container. See the [Docker Deployment Guide](../docs/guide-docker-deployment.md) for full details.

## Architecture

```
GitHub ──POST──▶ /api/github/webhooks
                       │
                SparkWebhookEventProcessor
                  ├── Verify HMAC-SHA256 signature
                  ├── Dev-app webhook? ──▶ Forward via WebSocket to dev clients
                  └── Production webhook:
                       ├── Broadcast GitHubWebhookMessage<TEvent> ──▶ typed queue
                       └── Broadcast GitHubWebhookMessage ──▶ spark-github-all
                                    │
                        ┌───────────┴───────────┐
                   IRecipient<...>          IRecipient<...>
                   (your handlers)         (your handlers)
```

### Dev tunnel flow

```
GitHub ──POST──▶ Production server
                       │
          ┌────────────┴────────────┐
     Production app?           Dev app?
     Process normally     Forward via WebSocket
                                    │
                          Developer's machine
                          (WebSocketDevClientService)
                                    │
                          SparkWebhookEventProcessor
                          (processes locally)
```

```
GitHub ──POST──▶ smee.io channel
                       │ (SSE)
                 SmeeBackgroundService
                 (re-minimizes JSON)
                       │
                SparkWebhookEventProcessor
                (processes locally)
```

## License

MIT
