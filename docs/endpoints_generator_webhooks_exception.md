# Why `MintPlayer.Spark.Webhooks.GitHub` stays hand-mapped

Every other endpoint Spark serves is a generator endpoint class. These two are not, and this records
why, so the question is not reopened as an oversight.

`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Extensions/SparkBuilderExtensions.cs`:

| Route | Line | Shape |
|---|---|---|
| `options.WebhookPath` (default `/api/github/webhooks`) | `:51` | `MapGitHubWebhooks(path, secret).DisableAntiforgery()` |
| `options.DevWebSocketPath` (default `/spark/github/dev-ws`) | `:72` | `MapGet`, gated on `options.DevelopmentAppId.HasValue` |

The assembly deliberately takes **no** `PackageReference` to `MintPlayer.AspNetCore.Endpoints`: with
nothing convertible, the generator would have nothing to generate.

## First, a correction to the obvious assumption

**`Path` does not have to be a compile-time constant.** Both mapping paths read `TEndpoint.Path` at
run time — `EndpointGenerator.Producer.cs:471` for the generated mapping, and
`EndpointRouteBuilderExtensions.cs:91` for `MapEndpoint<T>()`. A computed `Path` maps perfectly well.
What a non-literal costs is only *build-time analysis*: `RouteLiteral.Read` returns null, MPEP007–
MPEP010 are skipped, and MPEP011 says so at **Info** severity — its own message reads "Nothing is
wrong with a computed Path, which is why this is Info."

So "the path comes from configuration" is not by itself a reason these cannot convert.

## The actual blocker

**`Path` is `static` and receives no `IServiceProvider`** (`IEndpointBase.cs:25`). The value in
`GitHubWebhooksOptions` is supplied per `AddGithubWebhooks(...)` call, and a static property can only
reach it through a static mutable field — process-global, and wrong the moment two hosts run in one
process. There is no override hook, and `MapEndpoint<T>()` does not help: it reads the same static.

A *configuration-driven route* therefore cannot be expressed. This is worth an upstream request — a
provider-aware or instance `Path`, or a group-level path override — and is the second of the two
asks noted in the PRD.

## Route 1 is blocked twice over

Even with a configurable path, `MapGitHubWebhooks` is a **third-party** extension
(`Octokit.Webhooks.AspNetCore`) that maps its own route *and* installs a `RequestDelegate` performing
HMAC signature validation over the raw body. Converting it would mean reimplementing that
validation — changing what the endpoint does, which this work puts out of scope. Nor is the path
vestigial: `tests/MintPlayer.Spark.Tests/Webhooks/GitHub/SparkBuilderExtensionsTests.cs:56` sets it
to `/custom/hook`.

## Route 2, and a stale motivation

The `DevelopmentAppId.HasValue` gate maps cleanly onto `IEndpointGroup.IsEnabled`; only the path
blocks it.

⚠️ The reason usually given for converting this one is **stale**. It was cited as the example of a
hand-mapped route with no metadata — "a bare `Map()` matching every mutating verb". That was true and
is now fixed: `:72` is `MapGet`, with an explanatory comment at `:65-71`, recorded in
`docs/release-notes-preview-87.md:71`. There is no remaining metadata hole. What a class would add is
`WithName` and OpenAPI, which is close to worthless for a WebSocket upgrade.

It *could* convert if `DevWebSocketPath` stopped being configurable — a public behaviour change to
`GitHubWebhooksOptions`, needing its own decision. It is not behaviour-neutral, so it is not done
here.

## What would change this

Either of the upstream asks landing:

1. a provider-aware or instance `Path`, which unblocks route 2 immediately; or
2. signature validation exposed as something composable, which is the only thing that would make
   route 1 reconsiderable.
