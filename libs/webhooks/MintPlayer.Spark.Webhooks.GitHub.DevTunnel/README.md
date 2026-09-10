# MintPlayer.Spark.Webhooks.GitHub.DevTunnel

Development-only tunneling for [MintPlayer.Spark.Webhooks.GitHub](../MintPlayer.Spark.Webhooks.GitHub/README.md). GitHub can't reach `localhost`, so this package gives you two ways to receive real webhook deliveries on your dev machine — without deploying.

> **Dev-only.** Reference this package from local/development builds only. Production deployments need just the core `MintPlayer.Spark.Webhooks.GitHub` package, which receives webhooks directly over HTTPS.

## Installation

```xml
<!-- Development only -->
<PackageReference Include="MintPlayer.Spark.Webhooks.GitHub.DevTunnel" Version="10.0.0-preview.42" />
```

Both tunnels are wired through the core package's options object, so you configure them inside `spark.AddGithubWebhooks(...)`.

## Option A — smee.io tunnel

No production deployment needed. Point your GitHub App's Webhook URL at a [smee.io](https://smee.io/) channel, then relay it to your local app:

```csharp
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;

spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
    options.AddSmeeDevTunnel(builder.Configuration["GitHub:SmeeChannelUrl"]!);
});
```

`AddSmeeDevTunnel(smeeChannelUrl)` registers a `SmeeBackgroundService` that connects to the channel over Server-Sent Events and feeds each delivery into the same `SparkWebhookEventProcessor` used in production. Signature validation still applies, and it reconnects on its own if the channel drops.

> **Why it reads the SSE stream by hand rather than using a smee client library.** GitHub signs the exact bytes it sends, so the body must reach the processor unchanged. A client library that hands back a *parsed* body has already destroyed them: this tunnel previously went through `Smee.IO.Client`, whose body is typed `object` and materializes as a Newtonsoft `JObject`, and the body GitHub signed as `{"created_at":"2024-01-01T12:00:12.000+02:00","amount":1.50}` came back as `{"created_at":"2024-01-01T11:00:12+01:00","amount":1.5}` — the decimal lost its trailing zero and the timestamp was rebased into the *host machine's* timezone. Every such delivery failed HMAC validation and was silently dropped. The body is now lifted straight out of the frame with `JsonElement.GetRawText()`. The rule: **capture the signed bytes, never reconstruct them.**

## Option B — WebSocket forwarding from production

When your app is already deployed, run two GitHub Apps (e.g. **MyBot** and **MyBot-Dev**) pointing at the same production webhook URL. Production processes its own app's webhooks and **forwards the dev app's** webhooks to connected developers over a WebSocket.

```csharp
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;

// Developer's local Program.cs — do NOT set DevelopmentAppId locally
spark.AddGithubWebhooks(options =>
{
    options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
    options.AddWebSocketDevTunnel(
        builder.Configuration["GitHub:DevWebSocketUrl"]!,   // wss://yourapp.com/spark/github/dev-ws
        builder.Configuration["GitHub:DevGitHubToken"]!);   // your GitHub token
});
```

`AddWebSocketDevTunnel(productionWebSocketUrl, githubToken)` registers a `WebSocketDevClientService` that connects to the production server's dev-WS endpoint. The handshake sends your GitHub token; the server validates it against the GitHub API to determine your username and (if `AllowedDevUsers` is configured on the server) whether you're allowed to connect. Forwarded deliveries are processed locally through `SparkWebhookEventProcessor`, exactly as a direct delivery would be.

The production side of this flow (`DevelopmentAppId`, `DevWebSocketPath`, `AllowedDevUsers`, `DevSocketFilter`) is configured on the **core** package — see the [GitHub Webhooks README](../MintPlayer.Spark.Webhooks.GitHub/README.md#option-b-websocket-forwarding-from-production).

### Which developer gets which delivery

With several developers connected, the production side routes each delivery rather than fanning it out to everyone. By default a delivery goes to **the developer who caused it** — `sender.login` matched case-insensitively against the login they authenticated with. A payload carrying no sender goes to everyone, so an unattributed delivery is never lost.

`AllowedDevUsers` does *not* do this: it gates who may **connect**, not who receives **which** delivery. Without routing, every connected developer sees every other developer's webhook traffic.

Override it on the production side:

```csharp
// Route by repository instead of by sender
options.DevSocketFilter = (context, developerLogin) =>
    context.RepositoryFullName.StartsWith($"{developerLogin}/", StringComparison.OrdinalIgnoreCase);

// Or restore the old fan-out-to-everyone behaviour
options.DevSocketFilter = static (_, _) => true;
```

`context` is a `GitHubWebhookRoutingContext` carrying `EventName`, `SenderLogin`, `RepositoryFullName` and `InstallationId`, read from the already-signature-verified payload. Route on those values; don't authorize on them — anyone who can open a pull request controls `SenderLogin`.

## Extension methods

| Method | Description |
|---|---|
| `AddSmeeDevTunnel(string smeeChannelUrl)` | Relay a smee.io channel into the local webhook processor via SSE. |
| `AddWebSocketDevTunnel(string productionWebSocketUrl, string githubToken)` | Connect to a production server's dev-WS endpoint to receive forwarded dev-app webhooks. |

Both are extensions on `GitHubWebhooksOptions` and are called inside `spark.AddGithubWebhooks(...)`.

## Requirements

- .NET 10.0+
- `MintPlayer.Spark.Webhooks.GitHub` (referenced automatically)
- A GitHub App with a webhook secret (see the [core package README](../MintPlayer.Spark.Webhooks.GitHub/README.md))

## License

MIT
