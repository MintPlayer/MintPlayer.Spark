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
| `MintPlayer.Spark.Webhooks.GitHub` | Core: webhook processing, typed messages, signature validation, GitHub API client, production-side dev forwarding |
| `MintPlayer.Spark.Webhooks.GitHub.DevTunnel` | Dev-only: smee.io tunnel and WebSocket client for receiving forwarded webhooks locally |

The DevTunnel package is only needed during development. Production deployments only need the core package.

## Prerequisites

You need a **GitHub App** with webhook events enabled. If you don't have one yet, follow the steps below.

### Creating a GitHub App

GitHub Apps can be created under a **personal account** or an **organization account**. Organization-owned apps are recommended when the app needs access to organization-level resources like GitHub Projects V2.

1. Go to your GitHub App settings:
   - **Personal account**: https://github.com/settings/apps
   - **Organization**: `https://github.com/organizations/{org_name}/settings/apps` (e.g., https://github.com/organizations/MintPlayer/settings/apps)

2. Click **"New GitHub App"** and fill in the basic settings:

   | Setting | Value |
   |---|---|
   | **GitHub App name** | Any unique name (e.g., `MyWebhooksBot`). Must be globally unique across GitHub. |
   | **Homepage URL** | `https://github.com` (any valid URL) |
   | **Callback URL** | Your user-authorization redirect URI(s). Add one per environment — GitHub requires exact matches including port. For a local app: `https://localhost:<port>/signin-github` and your production URL if applicable. You can add multiple URLs. |
   | **Request user authorization (OAuth) during installation** | Check this box if users will sign in with GitHub. Without it, the app only handles webhooks and cannot issue user access tokens. |
   | **Expire user authorization tokens** | Leave unchecked for now — non-expiring user tokens avoid the need for refresh-token handling in your app. |
   | **Webhook URL** | Your production endpoint (e.g., `https://your-app.example.com/api/github/webhooks`) or a [smee.io](https://smee.io/) channel URL for local development |
   | **Webhook secret** | Generate a strong random string: `openssl rand -hex 32` |

3. Set **Permissions**. These control what the app can access:

   **Repository permissions:**

   | Permission | Access | Needed for |
   |---|---|---|
   | **Issues** | Read & write | Receiving issue webhooks, commenting on issues |
   | **Pull requests** | Read & write | Receiving PR webhooks, reading closing issue references |
   | **Metadata** | Read-only | Automatically granted, required by GitHub |

   **Organization permissions:**

   | Permission | Access | Needed for |
   |---|---|---|
   | **Projects** | Read & write | Moving issues/PRs on GitHub Projects V2 boards |

   > **Note:** Organization permissions require the app to be installed at the organization level. If the app only has repository permissions, it won't be able to access organization-owned project boards.

4. **Subscribe to events.** Scroll down to "Subscribe to events" and check the events you want to handle:

   | Event | Triggers on |
   |---|---|
   | **Issues** | Issue opened, closed, reopened, labeled, assigned, etc. |
   | **Pull request** | PR opened, closed, merged, ready for review, converted to draft, etc. |
   | **Pull request review** | Review submitted (approved, changes requested), dismissed |
   | **Check run** | CI check completed |
   | **Issue comment** | Comment added to an issue or PR |

   Only subscribe to events you actually handle — unnecessary events create noise.

5. Under **"Where can this GitHub App be installed?"**, choose:
   - **"Any account"** — allows other organizations/users to install your app
   - **"Only on this account"** — restricts to the owning account (recommended for private/internal apps)

6. Click **"Create GitHub App"**.

### After creation: collect credentials

On the app's settings page after creation:

1. **Note the App ID** — displayed at the top of the page (e.g., `123456`).

2. **Note the Client ID** — shown in the "About" section (e.g., `Iv1.abc123def456`).

3. **Generate a Client Secret** — scroll to "Client secrets" and click **"Generate a new client secret"**. Copy it immediately — GitHub only shows it once. This is needed for GitHub OAuth login.

4. **Generate a Private Key** — scroll to "Private keys" and click **"Generate a private key"**. This downloads a `.pem` file. The private key is used to create installation tokens for authenticated API calls (e.g., moving items on project boards, commenting on issues).

### Installing the GitHub App

After creating the app, install it on the repositories/organizations you want to receive webhooks from:

1. Navigate to your app's installation page:
   - **Personal account**: `https://github.com/settings/apps/{app_slug}/installations`
   - **Organization**: `https://github.com/organizations/{org_name}/settings/apps/{app_slug}/installations`

   > **Tip:** The `{app_slug}` is the lowercase, hyphenated version of your app name (e.g., `my-webhooks-bot`).

2. Click **"Install"**, then choose:
   - **"All repositories"** — the app receives webhooks from every repository in the account
   - **"Only select repositories"** — pick specific repositories

3. Click **"Install"**.

If you need the app on both a personal account and an organization, install it separately on each.

### Configuration values

After setup, you should have these values ready for your `appsettings.json` or user secrets:

| Value | Where to find it |
|---|---|
| **Webhook secret** | The string you entered when creating the app |
| **App ID** | Top of the app's settings page |
| **Client ID** | "About" section on the app's settings page |
| **Client secret** | Generated under "Client secrets" on the app's settings page |
| **Private key** | The `.pem` file downloaded after generating a private key |

### Organization access for logged-in users

If your app signs users in with GitHub and authorizes access based on which GitHub organizations they belong to, **use the same GitHub App for user login**. GitHub Apps support a user-authorization (sign-in) flow via their Client ID + Client Secret; enable **"Request user authorization (OAuth) during installation"** on the app's settings page.

With that in place, query `GET https://api.github.com/user/installations` (authenticated with the user's access token) to discover which accounts — personal and organizational — the user has installed the app on. Each installation's `account.login` is an owner you can authorize the user to act on.

```
GET https://api.github.com/user/installations
Authorization: Bearer {user_access_token}
Accept: application/vnd.github+json
```

This sidesteps a classic footgun: if you use a **separate** OAuth App for login and read org memberships via `GET /user/orgs` or the GraphQL `viewer.organizations` query, GitHub **silently omits** any org that has enabled third-party OAuth App restrictions until an org owner approves the OAuth App at `https://github.com/organizations/{org_name}/settings/oauth_application_policy`. GitHub App installations don't have this restriction — installing the app on the org *is* the approval.

**Recommendation:** one GitHub App, used for both webhooks and user login. Discover org access via `/user/installations`. No separate OAuth App, no approval dance.

## Installation

```xml
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub" Version="10.0.0-preview.42" />

<!-- Development only: -->
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub.DevTunnel" Version="10.0.0-preview.42" />
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

Create a class that implements `IRecipient<GitHubWebhookMessage<TEvent>>` for the event type you want to handle. The source generator auto-registers it in DI -- no `[Register]` attribute needed.

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
            var githubClient = await _gitHubInstallationService.CreateInstallationClientAsync(message.InstallationId);
            await githubClient.Issue.Comment.Create(
                message.Event.Repository!.Id, (int)issue.Number, "Thanks for creating this issue");
        }
    }
}
```

`CreateInstallationClientAsync` authenticates as the GitHub App by creating a JWT signed with the app's private key, then exchanges it for a short-lived installation access token. This requires `ClientId` and either `PrivateKeyPem` or `PrivateKeyPath` to be configured in options.

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

**The catch-all really does mean every event.** It is broadcast for whatever GitHub sends, including
events this package offers no typed envelope for and events that did not exist when it was written —
so `message.EventType` is the thing to switch on, and a `default` arm is not dead code.

This was not always true. Dispatch used to be one method override per event over a base whose
implementations are silent no-ops, so an event nobody had written an override for was discarded with
no log and no error. `installation_repositories` was one of them for the lifetime of the package,
while a downstream app sat waiting for it — which is why the catch-all is now emitted ahead of the
typed dispatch rather than from inside it.

A typed envelope is broadcast **only when something is registered to consume it**. A broadcast to a
message type with no `IRecipient<T>` is not a no-op: queue workers are started per registered
recipient, so it stores a document that nothing will ever drain, one per delivery, forever. If you
subscribe only to the catch-all, you pay for the catch-all only.

Typed dispatch is best-effort. Octokit deserializes into an action-specific type with required
properties, so a payload shape it does not model throws — that is logged and swallowed, because the
catch-all has already been delivered and failing the request would only earn a redelivery and a
duplicate.

## Message types

| Type | Use case |
|---|---|
| `GitHubWebhookMessage<TEvent>` | Handle a specific event type with full IntelliSense on the Octokit event model |
| `GitHubWebhookMessage` | Handle all events generically; provides `EventType` (string) and `EventJson` (raw JSON) |

Both records include `Headers`, `InstallationId`, and `RepositoryFullName`.

You route by **CLR type**, not by queue name: declare `IRecipient<GitHubWebhookMessage<PushEvent>>`
and messaging delivers push events to it. The queue behind a typed message is derived from the
type and is an implementation detail — don't write it down or match on it. The one name worth
knowing is `spark-github-all`, which the non-generic `GitHubWebhookMessage` declares explicitly.

## Configuration

### Storing secrets

Use the .NET user secrets manager for local development — never commit secrets to `appsettings.json`.

App credentials are grouped by environment (`Production` / `Development`) so a single process can hold both sets at once. Which environment is active is decided by `IHostEnvironment.IsDevelopment()` in `Program.cs`.

```bash
cd YourApp
dotnet user-secrets set "GitHub:WebhookSecret" "your-webhook-secret"

# Production app credentials
dotnet user-secrets set "GitHub:Production:AppId" "123456"
dotnet user-secrets set "GitHub:Production:ClientId" "Iv1.abc123"
dotnet user-secrets set "GitHub:Production:ClientSecret" "your-client-secret"
dotnet user-secrets set "GitHub:Production:PrivateKeyPath" "C:\path\to\prod-app.pem"

# Development app credentials (second GitHub App — see Option B below)
dotnet user-secrets set "GitHub:Development:AppId" "789012"
dotnet user-secrets set "GitHub:Development:ClientId" "Iv1.xyz789"
dotnet user-secrets set "GitHub:Development:ClientSecret" "your-dev-client-secret"
dotnet user-secrets set "GitHub:Development:PrivateKeyPath" "C:\path\to\dev-app.pem"
```

For the WebSocket dev tunnel (Option B below):

```bash
dotnet user-secrets set "GitHub:DevWebSocketUrl" "wss://your-app.example.com/spark/github/dev-ws"
dotnet user-secrets set "GitHub:DevGitHubToken" "ghp_..."
```

### appsettings.json

Only non-secret defaults belong here. Leave secret values empty — user secrets or environment variables override them at runtime:

```json
{
  "GitHub": {
    "WebhookSecret": "",
    "SmeeChannelUrl": "",
    "Production": {
      "AppId": "",
      "ClientId": "",
      "ClientSecret": "",
      "PrivateKeyPath": ""
    },
    "Development": {
      "AppId": "",
      "ClientId": "",
      "ClientSecret": "",
      "PrivateKeyPath": ""
    }
  }
}
```

For production (Docker), pass secrets via environment variables:

```yaml
environment:
  - GitHub__WebhookSecret=${GITHUB_WEBHOOK_SECRET}
  - GitHub__Production__AppId=${GITHUB_PRODUCTION_APP_ID}
  - GitHub__Production__ClientId=${GITHUB_PRODUCTION_CLIENT_ID}
  - GitHub__Production__ClientSecret=${GITHUB_PRODUCTION_CLIENT_SECRET}
  - GitHub__Production__PrivateKeyPath=/run/secrets/github-prod-app.pem
  - GitHub__Development__AppId=${GITHUB_DEVELOPMENT_APP_ID}
```

### Options reference

| Option | Default | Description |
|---|---|---|
| `WebhookSecret` | `""` | Webhook secret from your GitHub App settings. Used for HMAC-SHA256 signature validation. |
| `WebhookPath` | `"/api/github/webhooks"` | Endpoint path for receiving webhooks. |
| `ProductionAppId` | `null` | GitHub App ID for the production app. |
| `DevelopmentAppId` | `null` | GitHub App ID for the dev app. When set, webhooks from this app are forwarded to dev clients instead of being processed locally. |
| `DevWebSocketPath` | `"/spark/github/dev-ws"` | WebSocket endpoint path for dev client connections. |
| `AllowedDevUsers` | `[]` | GitHub usernames allowed to connect via WebSocket. **Empty means nobody** — the dev tunnel stays closed until you name someone (R2-H12). |
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
    options.ProductionAppId = long.Parse(builder.Configuration["GitHub:Production:AppId"]!);
    options.DevelopmentAppId = long.Parse(builder.Configuration["GitHub:Development:AppId"]!);
});
```

For Docker deployments, pass both App IDs via environment variables (see the [appsettings.json](#appsettingsjson) section above).

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

The processor currently handles these Octokit event types:

Subscribe with `IRecipient<GitHubWebhookMessage<T>>` for any of these `T`:

| Event type |
|---|
| `PushEvent` |
| `IssuesEvent` |
| `IssueCommentEvent` |
| `PullRequestEvent` |
| `PullRequestReviewEvent` |
| `PullRequestReviewCommentEvent` |
| `CheckRunEvent` |
| `CheckSuiteEvent` |
| `InstallationEvent` |
| `RepositoryEvent` |

Adding a new event type requires a single one-liner override in `SparkWebhookEventProcessor`. Unhandled events are silently dropped by Octokit.

## Wire format

WebSocket dev forwarding uses the same format as GitHub's HTTP requests:

```
Header-Name: Value
Header-Name: Value

{json-body}
```

Headers and body are separated by a blank line (`\n\n`).

## GitHub OAuth & authenticated API calls

For server-side API calls scoped to an installation (commenting on issues, moving items on project boards), inject `IGitHubInstallationService` — see [Calling the GitHub API from a recipient](#3-calling-the-github-api-from-a-recipient) above.

For user-facing features that need the user's *own* GitHub token (e.g., listing the projects that user can access), configure GitHub user authorization via Spark Authorization:

```csharp
var envPrefix = builder.Environment.IsDevelopment() ? "Development" : "Production";

spark.AddAuthentication<SparkUser>(configureProviders: identity =>
{
    identity.AddGitHub(options =>
    {
        options.ClientId = builder.Configuration[$"GitHub:{envPrefix}:ClientId"]!;
        options.ClientSecret = builder.Configuration[$"GitHub:{envPrefix}:ClientSecret"]!;
        options.SaveTokens = true;
    });
});
```

This requires a **Client secret** generated on the GitHub App's settings page (under "Client secrets"), and the **"Request user authorization (OAuth) during installation"** option enabled on the app. The user's access token is then available via `HttpContext.GetTokenAsync("access_token")`.

> **Note:** GitHub App user access tokens do **not** take OAuth scopes in the authorize URL — the token's permissions are derived from the app's installation permissions (set under "Permissions" on the app settings page). Calls to `options.Scope.Add(...)` are silently ignored.

## Project board automation

The [`apps/CodeCoverage`](../apps/CodeCoverage) application drives GitHub Projects V2 boards from
webhook deliveries: an issue or pull-request event moves the corresponding card to a configured
column. It replaces an earlier demo of the same idea, and the differences are deliberate rather
than incidental — that app was single-tenant, so several of its defaults are unsafe once a server
holds more than one person's installations.

### How it works

1. **Boards are discovered, not enabled by hand.** The nightly reconciler lists every Projects V2
   board each installation can see and writes a `GitHubProject` document per board. There is no
   "enable this project" screen, and `New/GitHubProject` is deliberately not granted.

2. **Automation is opt-in per board.** `AutomationEnabled` defaults to **false**, precisely because
   discovery is automatic: a default of true would mean installing the app on an organization
   silently started moving cards on every board it could see.

3. **Rules map an event to a column.** Each `EventColumnMapping` names one of eighteen events
   (`WebhookEventType`) and one target column. Rules are edited inline on the board's page, and the
   column is picked from the board's own cached columns.

4. **A delivery is routed, then acted on.** `ProjectAutomationRouter` drops anything unautomatable
   or self-authored and re-publishes the rest onto its own queue; `ProjectAutomationRecipient`
   resolves which rules match and calls the GraphQL API. Splitting them keeps board work on its own
   FIFO lane, so a slow board cannot delay coverage feedback.

### Two design points worth copying

**A rule stores the single-select option id, never the column name.** Names are renameable and not
unique, so storing the name would break every rule on a board the moment someone renamed a column —
and break it *silently*, because the move would simply match nothing. Storing the id means a rename
is a non-event; only the cached label goes stale until the next sync.

**The loop guard tests the App id, not "was this made by an App".** CodeCoverage creates check runs
and posts pull-request comments, so it receives webhooks for its own writes; unguarded, publishing
feedback moves a card and moving a card publishes feedback. But **every** check run on GitHub is
created by an App or by Actions, so dropping all App-authored deliveries would make
`CheckRunCompleted` permanently inert. The test compares `check_run.app.id` against this instance's
own configured App id.

### Nothing tells you when a column changes

Projects V2 webhook events are organization-scoped — a user-account installation receives none of
them — and, more decisively, **renaming a Status option produces no event for either owner type**
(measured, both owner shapes). So cached columns can only be corrected by the nightly reconciliation
or by the `SyncColumns` action, and a rule whose target column was deleted stays syntactically valid
and silently never fires until one of the two runs. That is why the rule records `LastFiredAtUtc`
and `LastError` on itself: a rule that has never fired is configured wrong, one that fired until a
date stopped working then.

### Key files

| File | Purpose |
|---|---|
| `Recipients/ProjectAutomationRouter.cs` | Filters a delivery and re-publishes it onto the automation queue; holds the self-authored loop guard |
| `Recipients/ProjectAutomationRecipient.cs` | Resolves which rules match a delivery and moves the cards; records per-rule outcomes |
| `Services/GitHubProjectCards.cs` | GraphQL calls: move an issue or PR, and resolve a PR's closing issues |
| `Services/IInstallationProjects.cs` | Lists an installation's boards and reads a board's Status field and its options |
| `Services/GitHubStateReconciler.cs` | Nightly discovery of boards, and column refresh for automated ones |
| `CustomActions/SyncColumnsAction.cs` | Re-reads a board's columns from GitHub on demand |
| `Actions/GitHubProjectActions.cs` | Row security, the account sub-query, and rule-id stamping |
| `Actions/ProjectColumnActions.cs` | Parent-scoped custom query supplying the target-column options |
| `LookupReferences/WebhookEventType.cs` | The closed set of eighteen automatable events, with the wire values to match them |

### Example document

```json
{
  "OwnerLogin": "MintPlayer",
  "InstallationId": 153539364,
  "NodeId": "PVT_kwDOAug2bM4AthJv",
  "Number": 1,
  "Name": "Delivery board",
  "StatusFieldId": "PVTSSF_lADOAug2bM4AthJvzgkQ7-s",
  "Columns": [
    { "Id": "f75ad846", "Name": "Todo" },
    { "Id": "98236657", "Name": "Done" }
  ],
  "EventMappings": [
    {
      "Id": "PullRequestMerged",
      "EventType": "PullRequestMerged",
      "TargetColumnOptionId": "98236657",
      "Enabled": true
    }
  ],
  "AutomationEnabled": true,
  "DeleteBranchOnPrClose": false,
  "Connection": "Connected",
  "@metadata": { "@collection": "GitHubProjects" }
}
```

`DeleteBranchOnPrClose` is opt-in per board and defaults to false — another deliberate departure,
where the earlier demo deleted head branches unconditionally and organization-wide. On a
multi-tenant server that setting mutates other people's repositories, and it is the one
irreversible action in the feature.

## Docker deployment

The repository includes a production-ready `docker-compose.yml` at
`../apps/CodeCoverage/docker-compose.yml` with `${...}` placeholders for secrets. Keep the GitHub
App private key out of the image and mount it at runtime, and give the app a `stop_grace_period`
long enough for in-flight message handlers to finish rather than being killed mid-write.
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
