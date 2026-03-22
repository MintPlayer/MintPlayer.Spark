# WebhooksDemo

A minimal Spark application demonstrating GitHub webhook integration. Receives webhook events via smee.io and logs them through typed `IRecipient<T>` handlers.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [RavenDB 6.2+](https://ravendb.net/) running on `http://localhost:8080`
- A GitHub account

## Step-by-step setup

### 1. Create a smee.io channel

Go to [smee.io](https://smee.io/) and click **"Start a new channel"**. You'll get a unique URL like `https://smee.io/abc123xyz`. Keep this tab open — you'll need this URL in the next steps.

### 2. Create a GitHub App

Go to your GitHub App settings:

- **Personal account**: https://github.com/settings/apps
- **Organization**: `https://github.com/organizations/{org_name}/settings/apps`

Click **"New GitHub App"** and fill in:

| Setting | Value |
|---|---|
| **GitHub App name** | Any unique name (e.g., `MyWebhooksTest`) |
| **Homepage URL** | `https://github.com` (any valid URL) |
| **Webhook URL** | Paste your smee.io channel URL from step 1 |
| **Webhook secret** | Generate a strong random string (e.g., using `openssl rand -hex 32`) |
| **Permissions** | Under "Repository permissions": Issues → Read & write, Pull requests → Read & write |
| **Subscribe to events** | Check: Issues, Pull request |
| **Where can this GitHub App be installed?** | Any account |

Click **"Create GitHub App"**. On the resulting page, note the **App ID** displayed at the top.

Optionally, scroll down to **"Private keys"** and click **"Generate a private key"** — this downloads a `.pem` file you'll need if your recipients make authenticated GitHub API calls.

### 3. Create a test repository

Create a new repository on your personal account (or use an existing one). This is the repo where you'll create issues/PRs to trigger webhooks.

### 4. Install the GitHub App on your repository

Navigate to your app's installation page:

- **Personal account**: `https://github.com/settings/apps/{app_name}/installations`
- **Organization**: `https://github.com/organizations/{org_name}/settings/apps/{app_name}/installations`

Click **"Install"**, then choose either "All repositories" or "Only select repositories" and pick your test repository. Click **"Install"**.

### 5. Configure user secrets

From the repository root, run:

```bash
cd Demo/WebhooksDemo/WebhooksDemo

# Required: webhook secret (must match what you entered in the GitHub App settings)
dotnet user-secrets set "GitHub:WebhookSecret" "your-webhook-secret"

# Required: smee.io channel URL from step 1
dotnet user-secrets set "GitHub:SmeeChannelUrl" "https://smee.io/your-channel-id"
```

Optionally, if your recipients need to make authenticated GitHub API calls (e.g., commenting on issues):

```bash
dotnet user-secrets set "GitHub:AppId" "your-app-id"
dotnet user-secrets set "GitHub:ClientId" "your-client-id"
dotnet user-secrets set "GitHub:PrivateKeyPath" "C:\path\to\your-app.private-key.pem"
```

You can find the App ID and Client ID on your GitHub App's settings page. The private key is the `.pem` file downloaded in step 2.

### 6. Run the application

```bash
cd Demo/WebhooksDemo/WebhooksDemo
dotnet run
```

You should see a log line confirming the smee.io connection:

```
info: MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services.SmeeBackgroundService
      Connecting to smee.io channel: https://smee.io/your-channel-id
```

### 7. Trigger a webhook

Go to your test repository on GitHub and **create a new issue** (or pull request). Within a few seconds you should see log output like:

```
info: WebhooksDemo.Recipients.LogAllWebhooks
      Webhook received: issues from owner/repo (installation 12345678)
info: WebhooksDemo.Recipients.LogIssues
      Issue #1 (opened): My test issue in owner/repo
```

Try other actions too: close the issue, reopen it, edit the title, create a pull request, etc. Each action triggers a webhook that flows through the message bus to your recipients.

## How it works

```
GitHub ──POST──► smee.io ──SSE──► SmeeBackgroundService ──POST──► /api/github/webhooks
                                   (re-minimizes JSON)              │
                                                                    ▼
                                                     SparkWebhookEventProcessor
                                                        │              │
                                                        ▼              ▼
                                          GitHubWebhookMessage<T>   GitHubWebhookMessage
                                             (typed queue)            (catch-all queue)
                                                        │              │
                                                        ▼              ▼
                                                   IRecipient<T> handlers
```

The app registers three `IRecipient<T>` handlers:

| Recipient | Message type | What it logs |
|---|---|---|
| `LogPullRequest` | `GitHubWebhookMessage<PullRequestEvent>` | PR number, action, title, repo |
| `LogIssues` | `GitHubWebhookMessage<IssuesEvent>` | Issue number, action, title, repo |
| `LogAllWebhooks` | `GitHubWebhookMessage` (catch-all) | Event type, repo, installation ID |

## Troubleshooting

| Problem | Solution |
|---|---|
| No log output when creating an issue | Check that the smee.io channel URL matches between your GitHub App settings and user secrets |
| Signature validation failed | Ensure the webhook secret in user secrets matches exactly what you entered in the GitHub App settings |
| Smee.io not connecting | Make sure the smee.io tab is still open (or just verify the channel URL is correct) |
| RavenDB connection error | Ensure RavenDB is running on `http://localhost:8080` |
