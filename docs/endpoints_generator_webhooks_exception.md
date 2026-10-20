# The GitHub webhook POST: no longer an exception

**Status (2026-10-19, #521 X5): closed.** Every endpoint Spark serves is now a generator endpoint
class. The GitHub webhook POST was the last exception (decision **D4** of
[`endpoints_generator_completion_PRD.md`](endpoints_generator_completion_PRD.md)); it became
`GitHubWebhookEndpoint`, a source of the generic `MintPlayer.Spark.Webhooks` library. The text below
records what the exception was, why it existed, and what removed it, so the question is not
reopened.

`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub`:

| Route | Shape |
|---|---|
| `options.WebhookPath` (default `/api/github/webhooks`) | `GitHubWebhookEndpoint`: a **generator endpoint** (`IPostEndpoint<WebhookRequest>`), mapped by `MapSparkWebhooksGitHubEndpoints()` |
| `options.DevWebSocketPath` (default `/spark/github/dev-ws`) | `DevWebSocketEndpoint`: a **generator endpoint**, mapped by the same method |

## What the exception was

The POST was `MapGitHubWebhooks(path, secret).DisableAntiforgery()`, a **third-party** extension
(`Octokit.Webhooks.AspNetCore`). It mapped its own route and installed a `RequestDelegate` that owned
the HTTP side of a delivery: it read the raw body, validated `X-Hub-Signature-256` with the
configured secret, refused a failing request, and only then handed the body to the registered
`WebhookEventProcessor` (`SparkWebhookEventProcessor`). Octokit did not expose that check as
something an endpoint class could call. D4 kept the route on Octokit rather than replace its
request handling on the one route where a mistake means accepting forged deliveries.

The document named what would change this: "a decision to make Spark's own `ISignatureService` the
request-level check."

## What removed it

That decision is #521 X5. `MintPlayer.Spark.Webhooks` owns the request side of every webhook route:

- **The body is read raw** in the endpoint's `BindRequestAsync` override (the endpoint library's
  typed-binding hook, which the convention test's R1 allows), under the source's size cap: 25 MB for
  GitHub, GitHub's own limit. The bytes are never parsed and re-serialized.
- **The signature is checked over those bytes** by `IWebhookReceiver`, with the source's strategy.
  GitHub's strategy is `GitHubWebhookSignature`, an instance of the generic `HmacSignatureStrategy`
  (`X-Hub-Signature-256`, HMAC-SHA256, lower-case hex, prefix `sha256=`). The old internal
  `SignatureService` now delegates to it. A failure answers `401` before the processor runs.
- **The processor runs inline**, before the `200`, exactly as before: it forwards development-app
  deliveries to the dev tunnel and otherwise broadcasts the catch-all envelope once per
  `X-GitHub-Delivery` (`BroadcastOnceAsync`). It still checks the signature a second time, because the
  dev-tunnel clients (`SmeeBackgroundService`, `WebSocketDevClientService`) call it directly.

## Behaviour changes, deliberately

| Case | Before (Octokit) | Now |
|---|---|---|
| `WebhookSecret` empty | Octokit skipped its check; the processor dropped the delivery after a `200` | `401` at the request, and an error is logged. Fail closed at the edge |
| Bad or missing signature | Octokit's refusal | `401`, nothing processed |
| Body not `application/json` | Octokit's content-type refusal | `415` |
| Body over 25 MB | Kestrel's limit | `413` before a byte is read (declared length) or as soon as the running total passes it |
| `WebhookPath` set through `Configure<GitHubWebhooksOptions>()` | Ignored: Octokit's mapper took the value passed to `AddGithubWebhooks` | Honoured: `GetPath` reads `IOptions` at map time, as the dev WebSocket already did |
| `WebhookSecret` set through `Configure<>()` | Ignored by the route | Honoured: read per delivery |

The route table snapshots (`tests/MintPlayer.Spark.Tests/Endpoints/RouteSnapshots/webhooks*.txt`)
are unchanged: `POST /api/github/webhooks | antiforgery=not-required`. The package reference moved
from `Octokit.Webhooks.AspNetCore` to `Octokit.Webhooks`: only the event model and the processor
base class are used now.

## The regression guard

`EndpointConventionsTests.MappingExceptions` no longer lists `MapGitHubWebhooks`, and
`MappingMethods` still detects it, so a reintroduced Octokit mapping fails R3.

Tests: `GitHubWebhookEndpointTests` (the route: exact body to the processor, 401 on a tampered or
unsigned delivery and on an empty secret, 415, a configured path, not served by the shared route),
`WebhookSignatureStrategyTests`, `WebhookReceiverTests`, `ReceiveWebhookEndpointTests`, and the
unchanged `SignatureServiceTests` and `SmeeSignatureFidelityTests`.

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
  service.
- `[Inject]` brings in `IGitHubClientFactory`, `IOptions<GitHubWebhooksOptions>` and
  `IDevWebSocketService`. Nothing is pulled from `RequestServices`.
- It stays a GET, with no antiforgery or authorization metadata. Spark's same-origin guard on
  WebSocket upgrades (`SparkMiddleware`, after `UseWebSockets()`) still refuses a cross-origin
  handshake before the endpoint runs.

The price of `GetPath` is MPEP034 (Info): the endpoint gets no typed link and no client contract.
Neither matters for a WebSocket upgrade or a webhook POST, so `GitHubWebhookEndpoint` pays it too.

Tests: `DevWebSocketEndpointTests` (behaviour), `DevWebSocketEndpointMappingTests` (gate, configured
path, startup check) and `DevWebSocketOriginGuardTests` (the guard on Spark's real pipeline).
