# GitHub Webhooks

Spark provides first-class GitHub webhook integration through two optional packages. Webhook events are broadcast as typed messages on the Spark message bus, so you handle them by implementing `IRecipient<T>` — the same pattern used for all Spark messaging.

## Packages

| Package | Purpose |
|---|---|
| `MintPlayer.Spark.Webhooks.GitHub` | Core: webhook processing, typed messages, signature validation, production-side dev forwarding |
| `MintPlayer.Spark.Webhooks.GitHub.DevTunnel` | Dev-only: smee.io tunnel and WebSocket client for receiving forwarded webhooks locally |

The DevTunnel package is only needed during development. Production deployments only need the core package.

## Prerequisites

You need a GitHub App with webhook events enabled. If you don't have one yet, follow the steps below.

### Creating a GitHub App

1. Go to your GitHub App settings:
   - **Personal account**: https://github.com/settings/apps
   - **Organization**: `https://github.com/organizations/{org_name}/settings/apps`

2. Click **"New GitHub App"** and fill in:

   | Setting | Value |
   |---|---|
   | **GitHub App name** | Any unique name (e.g., `MyWebhooksBot`) |
   | **Homepage URL** | `https://github.com` (any valid URL) |
   | **Webhook URL** | Your production endpoint (e.g., `https://your-app.example.com/api/github/webhooks`) or a [smee.io](https://smee.io/) channel URL for local development |
   | **Webhook secret** | Generate a strong random string (e.g., `openssl rand -hex 32`) |
   | **Permissions** | Under "Repository permissions", grant access to the events you need (e.g., Issues → Read & write, Pull requests → Read & write) |
   | **Subscribe to events** | Check the events you want to receive (e.g., Issues, Pull request, Check run) |
   | **Where can this GitHub App be installed?** | "Any account" for public apps, or "Only on this account" for private use |

3. Click **"Create GitHub App"**. On the resulting page, note the following values:
   - **App ID** — displayed at the top of the page
   - **Client ID** — shown in the "About" section

4. Scroll down to **"Private keys"** and click **"Generate a private key"**. This downloads a `.pem` file needed for making authenticated GitHub API calls (e.g., commenting on issues, moving project board items).

### Installing the GitHub App on repositories

After creating the app, install it on the repositories you want to receive webhooks from:

1. Navigate to your app's installation page:
   - **Personal account**: `https://github.com/settings/apps/{app_slug}/installations`
   - **Organization**: `https://github.com/organizations/{org_name}/settings/apps/{app_slug}/installations`

2. Click **"Install"**, then choose either "All repositories" or "Only select repositories" and pick the repositories you want.

3. Click **"Install"**.

### Configuration values

After setup, you should have these values ready for your `appsettings.json` or user secrets:

| Value | Where to find it |
|---|---|
| **Webhook secret** | The string you entered when creating the app |
| **App ID** | Top of the app's settings page |
| **Client ID** | "About" section on the app's settings page |
| **Client secret** | Generate one under "Client secrets" on the app's settings page (needed for OAuth login) |
| **Private key** | The `.pem` file downloaded after generating a private key |

## Setup

### 1. Install packages

Add project references (or NuGet package references) to your web application:

```xml
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub" Version="10.0.0-preview.29" />
<!-- Development only: -->
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub.DevTunnel" Version="10.0.0-preview.29" />
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

### Storing secrets

Use the .NET user secrets manager for local development — never commit secrets to `appsettings.json`:

```bash
cd YourApp
dotnet user-secrets set "GitHub:WebhookSecret" "your-webhook-secret"
dotnet user-secrets set "GitHub:ClientId" "Iv1.abc123"
dotnet user-secrets set "GitHub:ClientSecret" "your-client-secret"
dotnet user-secrets set "GitHub:PrivateKeyPath" "C:\path\to\app.pem"
dotnet user-secrets set "GitHub:ProductionAppId" "123456"
```

For the WebSocket dev tunnel (Option B below):

```bash
dotnet user-secrets set "GitHub:DevelopmentAppId" "789012"
dotnet user-secrets set "GitHub:DevWebSocketUrl" "wss://your-app.example.com/spark/github/dev-ws"
dotnet user-secrets set "GitHub:DevGitHubToken" "ghp_..."
```

### appsettings.json

Only non-secret defaults belong here. Leave secret values empty — user secrets or environment variables override them at runtime:

```json
{
  "GitHub": {
    "WebhookSecret": "",
    "SmeeChannelUrl": ""
  }
}
```

For production (Docker), pass secrets via environment variables:

```yaml
environment:
  - GitHub__WebhookSecret=${GITHUB_WEBHOOK_SECRET}
  - GitHub__ClientId=${GITHUB_APP_CLIENT_ID}
  - GitHub__ProductionAppId=${GITHUB_PRODUCTION_APP_ID}
  - GitHub__DevelopmentAppId=${GITHUB_DEVELOPMENT_APP_ID}
  - GitHub__PrivateKeyPath=/run/secrets/github-app.pem
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
| `ClientSecret` | `null` | GitHub App Client Secret. Required for GitHub OAuth login (not needed for webhook processing). |

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

#### 1. Create a development GitHub App

Create a second GitHub App (e.g., `MyBot-Dev`) with:
- **Same webhook URL** as the production app (e.g., `https://your-app.example.com/api/github/webhooks`)
- **Same webhook secret** as the production app
- **Same permissions and event subscriptions**
- The dev app does **not** need a private key or client secret — it's only used to identify which webhooks to forward

Install the dev app on the same repositories as the production app.

#### 2. Configure production

Production needs both App IDs so it knows which webhooks to process locally and which to forward:

```csharp
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
    options.ProductionAppId = long.Parse(builder.Configuration["GitHub:ProductionAppId"]!);
    options.DevelopmentAppId = long.Parse(builder.Configuration["GitHub:DevelopmentAppId"]!);
});
```

For Docker deployments, pass both App IDs via environment variables (see [appsettings.json](#appsettingsjson) section above).

#### 3. Configure the local developer machine

Set up user secrets:

```bash
dotnet user-secrets set "GitHub:WebhookSecret" "<same-as-production>"
dotnet user-secrets set "GitHub:DevWebSocketUrl" "wss://your-app.example.com/spark/github/dev-ws"
dotnet user-secrets set "GitHub:DevGitHubToken" "<github-personal-access-token>"
```

> **Important:** Do **not** set `DevelopmentAppId` on the local developer machine. That value is only needed on the production server (the forwarding side). If the local app has `DevelopmentAppId` configured, and the value corresponds to the AppID that sent the webhook, the webhook processor will see the dev app's ID in the forwarded webhook headers and try to forward it again instead of processing it — causing all recipients to be silently skipped.

The `DevGitHubToken` is a [GitHub personal access token](https://github.com/settings/tokens) (classic, no scopes needed) — it's only used to verify your identity during the WebSocket handshake.

Then in `Program.cs`, enable the WebSocket dev tunnel:

```csharp
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;

    var wsUrl = builder.Configuration["GitHub:DevWebSocketUrl"];
    var wsToken = builder.Configuration["GitHub:DevGitHubToken"];
    if (!string.IsNullOrEmpty(wsUrl) && !string.IsNullOrEmpty(wsToken))
    {
        options.AddWebSocketDevTunnel(wsUrl, wsToken);
    }
});
```

#### How it works

When a webhook arrives at production, the `SparkWebhookEventProcessor` checks the `X-GitHub-Hook-Installation-Target-ID` header:
- If it matches `ProductionAppId` → process locally (broadcast to message bus)
- If it matches `DevelopmentAppId` → forward to all connected WebSocket dev clients

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

## GitHub OAuth & authenticated API calls

If your webhook recipients need to call the GitHub API (e.g., move items on project boards, comment on issues), you need the GitHub App's private key and App ID configured. The `IGitHubInstallationService` creates authenticated clients using the app's installation token:

```csharp
public partial class MyRecipient : IRecipient<GitHubWebhookMessage<IssuesEvent>>
{
    [Inject] private readonly IGitHubInstallationService _installationService;

    public async Task HandleAsync(GitHubWebhookMessage<IssuesEvent> message, CancellationToken ct)
    {
        var client = await _installationService.CreateClientAsync(message.InstallationId);
        // Use client to make authenticated API calls
    }
}
```

For user-facing features that need the user's own GitHub token (e.g., listing their projects), configure GitHub OAuth via Spark Authorization:

```csharp
spark.AddAuthentication<SparkUser>(configureProviders: identity =>
{
    identity.AddGitHub(options =>
    {
        options.ClientId = builder.Configuration["GitHub:ClientId"]!;
        options.ClientSecret = builder.Configuration["GitHub:ClientSecret"]!;
        options.Scope.Add("read:user");
        options.Scope.Add("read:org");      // Access organization memberships
        options.Scope.Add("read:project");  // Access GitHub Projects V2
        options.SaveTokens = true;
    });
});
```

This requires a **Client secret** generated on the GitHub App's settings page (under "Client secrets"). The user's OAuth token is then available via `HttpContext.GetTokenAsync("access_token")`.
