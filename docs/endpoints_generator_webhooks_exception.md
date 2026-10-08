# Why the GitHub webhook POST stays hand-mapped

Every endpoint Spark serves is a generator endpoint class, with one exception in
`MintPlayer.Spark.Webhooks.GitHub`: the GitHub webhook POST. This records why, so that the question
is not reopened as an oversight. It is decision **D4** of
[`endpoints_generator_completion_PRD.md`](endpoints_generator_completion_PRD.md).

`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Extensions/SparkBuilderExtensions.cs`:

| Route | Shape |
|---|---|
| `options.WebhookPath` (default `/api/github/webhooks`) | `MapGitHubWebhooks(path, secret).DisableAntiforgery()`: Octokit, **hand-mapped** |
| `options.DevWebSocketPath` (default `/spark/github/dev-ws`) | `DevWebSocketEndpoint`: a **generator endpoint**, mapped by `MapSparkWebhooksGitHubEndpoints()` |

## The blocker: Octokit owns the signature check

`MapGitHubWebhooks` is a **third-party** extension (`Octokit.Webhooks.AspNetCore`). It maps its own
route and installs a `RequestDelegate` that owns the HTTP side of a delivery: it reads the raw body,
validates the `X-Hub-Signature-256` HMAC over it with the configured secret, refuses a request that
fails with an error status, and only then hands the body to the registered `WebhookEventProcessor`
(`SparkWebhookEventProcessor`). The signature check is internal to that delegate; Octokit does not
expose it as something an endpoint class could call.

Two facts limit how strong this blocker is, and they are stated here so the decision stays honest:
- **Dispatch is not the blocker.** `WebhookEventProcessor.ProcessWebhookAsync(headers, body)` is
  public, and Spark already calls it directly from both dev-tunnel clients
  (`SmeeBackgroundService.cs:87`, `WebSocketDevClientService.cs:113`).
- **Spark checks the signature a second time.** `SparkWebhookEventProcessor.ProcessWebhookAsync`
  verifies `X-Hub-Signature-256` again through its own `ISignatureService`, fail-closed on an empty
  secret and on a mismatch (`SparkWebhookEventProcessor.cs:45-56`). That check runs inside dispatch,
  so it can only drop the event; the request-level refusal is Octokit's.

So a generator endpoint *could* read the body and call the processor without accepting forged
deliveries. Converting would still replace Octokit's request handling (body reading, the refusal
status, its error handling) with Spark's own on the one route where a mistake means accepting forged
deliveries, for no gain in metadata: the route needs `DisableAntiforgery()` and nothing else. **D4
keeps it on Octokit.**

**The path is not the blocker.** It used to be: `Path` was static and received no
`IServiceProvider`, so a configurable route could not be expressed. MintPlayer.AspNetCore.Endpoints
11.4.0-rc.0 added `IEndpointBase.GetPath(IServiceProvider)` (upstream #37), and the dev WebSocket below
uses it. That change does nothing for the webhook POST, because Octokit owns the signature check
whatever the path is. The path is also really configurable, not a leftover:
`tests/MintPlayer.Spark.Tests/Webhooks/GitHub/SparkBuilderExtensionsTests.cs` sets it to `/custom/hook`.

## The dev WebSocket is a generator endpoint

`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Endpoints/DevWebSocketEndpoint.cs` is an `IGetEndpoint`
class:
- `Path` is `DevWebSocketEndpoint.DefaultPath`, and `GitHubWebhooksOptions.DevWebSocketPath` defaults
  to that constant, so the default is written once.
- `GetPath(services)` returns `options.DevWebSocketPath`, read from the root provider at map time.
  The default has no route parameters, so a configured path that adds one fails at startup with an
  `InvalidOperationException` naming the endpoint and both patterns.
- `IsEnabled(services)` checks `DevelopmentAppId.HasValue` on `IOptions<GitHubWebhooksOptions>`, so a
  value an app sets through `Configure<GitHubWebhooksOptions>()` counts too. `IDevWebSocketService` is
  registered unconditionally (a plain singleton that starts no work). A mapped endpoint can therefore
  always be activated, and a disabled one is never mapped, never activated, and never builds the
  service. It used to be registered from the options passed to `AddGithubWebhooks`, which diverged
  from `IsEnabled` when `DevelopmentAppId` came from `Configure`: the endpoint was mapped and failed
  to activate (`SparkBuilderExtensionsTests`, red before the change).
- `[Inject]` brings in `IGitHubClientFactory`, `IOptions<GitHubWebhooksOptions>` and
  `IDevWebSocketService`. Nothing is pulled from `RequestServices`.
- It stays a GET, with no antiforgery or authorization metadata (the route table snapshot
  `tests/MintPlayer.Spark.Tests/Endpoints/RouteSnapshots/webhooks-dev-app.txt` is unchanged). Spark's
  same-origin guard on WebSocket upgrades (`SparkMiddleware`, after `UseWebSockets()`) still refuses
  a cross-origin handshake before the endpoint runs.

The price of `GetPath` is MPEP034 (Info): the endpoint gets no typed link and no client contract.
Neither matters for a WebSocket upgrade.

Tests: `DevWebSocketEndpointTests` (behaviour), `DevWebSocketEndpointMappingTests` (gate, configured
path, startup check) and `DevWebSocketOriginGuardTests` (the guard on Spark's real pipeline).

## What would change this

Octokit, or a package next to it, exposing its request-level signature validation as something an
endpoint can call, or a decision to make Spark's own `ISignatureService` the request-level check. Until then the webhook POST stays on `MapGitHubWebhooks`, and the regression guard
(plan M8) lists it in its allow-list with this document as the reason.
