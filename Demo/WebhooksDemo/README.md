# WebhooksDemo

A minimal Spark application demonstrating GitHub webhook integration. Receives webhook events via smee.io and logs them through typed `IRecipient<T>` handlers.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [RavenDB 6.2+](https://ravendb.net/) running on `http://localhost:8080`
- A GitHub App with webhook events enabled

## Setup

### 1. Create a GitHub App

Go to your [GitHub App settings](https://github.com/settings/apps) (or `https://github.com/organizations/{org}/settings/apps` for an organization) and create a new app:

| Setting | Value |
|---|---|
| **Webhook URL** | Your smee.io channel URL (see step 2) |
| **Webhook secret** | Generate a strong random string |
| **Permissions** | Issues: Read & write, Pull requests: Read & write |
| **Subscribe to events** | Issues, Pull request |
| **Where can this app be installed?** | Any account (for testing) |

### 2. Create a smee.io channel

Go to [smee.io](https://smee.io/) and click "Start a new channel". Copy the channel URL.

### 3. Configure user secrets

```bash
cd Demo/WebhooksDemo/WebhooksDemo
dotnet user-secrets set "GitHub:WebhookSecret" "your-webhook-secret"
dotnet user-secrets set "GitHub:SmeeChannelUrl" "https://smee.io/your-channel-id"
```

Optionally, if your recipients need to make authenticated GitHub API calls (e.g., commenting on issues), also store your GitHub App credentials:

```bash
dotnet user-secrets set "GitHub:AppId" "your-app-id"
dotnet user-secrets set "GitHub:ClientId" "your-client-id"
dotnet user-secrets set "GitHub:PrivateKeyPath" "C:\path\to\your-app.private-key.pem"
```

You can find these values on your GitHub App's settings page. The private key is downloaded when you generate one under "Private keys".

### 4. Install the GitHub App

Install your GitHub App on a repository you want to test with. Go to your app's page on GitHub and click "Install App".

### 5. Run the application

```bash
cd Demo/WebhooksDemo/WebhooksDemo
dotnet run
```

### 6. Trigger a webhook

Create or edit an issue or pull request in the repository where you installed the app. You should see log output like:

```
info: WebhooksDemo.Recipients.LogAllWebhooks  - Webhook received: issues from owner/repo (installation 12345678)
info: WebhooksDemo.Recipients.LogIssues        - Issue #1 (opened): My test issue in owner/repo
```

## How it works

The app registers three `IRecipient<T>` handlers:

| Recipient | Message type | What it logs |
|---|---|---|
| `LogPullRequest` | `GitHubWebhookMessage<PullRequestEvent>` | PR number, action, title, repo |
| `LogIssues` | `GitHubWebhookMessage<IssuesEvent>` | Issue number, action, title, repo |
| `LogAllWebhooks` | `GitHubWebhookMessage` (catch-all) | Event type, repo, installation ID |

The smee.io background service connects to the channel via SSE, re-minimizes the JSON (for correct signature validation), and forwards webhooks to the local endpoint where `SparkWebhookEventProcessor` broadcasts them to the message bus.
