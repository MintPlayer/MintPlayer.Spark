# MintPlayer.Spark.AllFeatures

The all-in-one package for [MintPlayer.Spark](../../spark/MintPlayer.Spark/README.md). Reference this single package and a source generator wires up the entire framework — CRUD, authorization, messaging, replication, and cron — through three calls: `AddSparkFull` / `UseSparkFull` / `MapSparkFull`.

> Full design and rationale: [PRD-AllFeatures](../../../docs/prd/PRD-AllFeatures.md).

## Installation

```bash
dotnet add package MintPlayer.Spark.AllFeatures
```

Installing the package also installs the bundled source generators (they ship in the package's `analyzers/` folder), so no separate generator reference is needed.

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSparkFull(builder.Configuration);

var app = builder.Build();

app.UseRouting();
app.UseSparkFull(args);
app.MapSparkFull();

app.Run();
```

That's the whole wiring. The source generator discovers your `SparkContext`, `SparkUser`, Actions, Recipients, Custom Actions, and Cron jobs **at compile time** — no generic type parameters, no manual `AddActions()`/`AddRecipients()`/`AddCronJobs()` calls.

- **`AddSparkFull`** — registers Spark + every feature it detects in your project (and the discovered Actions/Recipients/Custom Actions/Cron jobs).
- **`UseSparkFull(args)`** — adds the Spark middleware pipeline and handles `--spark-synchronize-model`.
- **`MapSparkFull`** — maps all Spark + Identity endpoints.

## What's bundled

Referencing `MintPlayer.Spark.AllFeatures` transitively brings in:

| Package | Provides |
|---------|----------|
| [`MintPlayer.Spark`](../../spark/MintPlayer.Spark/README.md) | Core CRUD framework, PersistentObject pipeline, endpoints |
| [`MintPlayer.Spark.Authorization`](../../authorization/MintPlayer.Spark.Authorization/README.md) | ASP.NET Core Identity + `security.json` group-based access control |
| [`MintPlayer.Spark.Messaging`](../../messaging/MintPlayer.Spark.Messaging/README.md) | Durable RavenDB message bus (pulls in the subscription worker) |
| `MintPlayer.Spark.Replication` | Cross-module ETL replication |
| [`MintPlayer.Spark.Cron`](../../cron/MintPlayer.Spark.Cron/README.md) | Multi-node-safe cron-scheduled background jobs |
| `MintPlayer.Spark.SourceGenerators` + `…AllFeatures.SourceGenerators` | Compile-time DI registration + the `AddSparkFull`/`UseSparkFull`/`MapSparkFull` emission |

GitHub webhooks ([`MintPlayer.Spark.Webhooks.GitHub`](../../webhooks/MintPlayer.Spark.Webhooks.GitHub/README.md)) and the typed `MintPlayer.Spark.Client` SDK are **not** bundled — add them explicitly when needed.

## Configuration

Pass a `SparkFullOptions` delegate to opt into and configure individual features. Every property is optional; unset features use their defaults (and replication/rate-limiting stay off until configured).

```csharp
builder.Services.AddSparkFull(builder.Configuration, options =>
{
    options.Replication = opt =>
    {
        opt.ModuleName = "Fleet";
        opt.ModuleUrl = "https://localhost:5003";
    };

    // Enable the rate limiter with default limits (150 requests / 10 s, partitioned by client IP).
    options.RateLimiter = _ => { };
});
```

| Option | Configures |
|--------|------------|
| `Authorization` | Group-based authorization (`security.json` behavior). |
| `Identity` | ASP.NET Core Identity options (password rules, lockout, …). |
| `IdentityProviders` | External login providers (Google, Microsoft, OIDC, …). |
| `Messaging` | The durable message bus. |
| `Replication` | Cross-module ETL replication (off unless set). |
| `RateLimiter` | The `/spark/` rate limiter (off unless set; `_ => { }` enables defaults). |

## Requirements

- .NET 10.0+
- RavenDB 6.2+
- An `IDocumentStore` (configured from `Spark:RavenDb` in `appsettings.json`)

## License

MIT
