# MintPlayer.Spark

[![codecov](https://codecov.io/gh/MintPlayer/MintPlayer.Spark/branch/master/graph/badge.svg)](https://codecov.io/gh/MintPlayer/MintPlayer.Spark)

A low-code web application framework for .NET that eliminates boilerplate code. Inspired by [Vidyano](https://www.vidyano.com/), Spark uses a PersistentObject pattern to replace traditional DTOs, repositories, and controllers with a single generic middleware.

## Key Features

- **Zero DTOs** - Uses `PersistentObject` as a universal data container
- **Zero Boilerplate** - Generic middleware handles all CRUD operations
- **Configuration Over Code** - Entity definitions stored as JSON files, auto-generated from C# classes
- **Dynamic UI** - Angular frontend automatically renders forms and lists based on entity metadata
- **RavenDB Integration** - Document database with index support for optimized queries

## Technology Stack

| Component | Technology |
|-----------|------------|
| Backend | .NET 10.0 |
| Frontend | Angular 22 |
| Database | RavenDB 6.2+ |
| UI Library | @mintplayer/ng-bootstrap |

## Quick Start (AllFeatures)

The fastest way to get started is with `MintPlayer.Spark.AllFeatures`. Reference this single package and three source-generated methods handle all the wiring:

```csharp
builder.Services.AddSparkFull(builder.Configuration);

// Build step: regenerates App_Data/Model when --spark-synchronize-model is passed.
// Needs no database, so it also runs in CI.
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

app.UseRouting();
app.UseSparkFull();
app.MapSparkFull();
```

The source generator discovers your `SparkContext`, `SparkUser`, Actions, Recipients and Custom Actions at compile time — no generic type parameters needed.

Configure individual features via `SparkFullOptions`:

```csharp
builder.Services.AddSparkFull(builder.Configuration, options =>
{
    options.Replication = opt =>
    {
        opt.ModuleName = "Fleet";
        opt.ModuleUrl = "https://localhost:5003";
    };
});
```

See the [AllFeatures documentation](docs/prd/PRD-AllFeatures.md) for details.

### Granular Setup

If you only need a subset of features, use the individual packages directly:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddActions();
    spark.AddMessaging();
    spark.AddRecipients();
});

app.UseRouting();
app.UseSpark();
app.MapSpark();
```

### Define Your Context

```csharp
public class MySparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
}
```

```bash
# Generate model files
dotnet run --spark-synchronize-model
```

## Project Structure

```
MintPlayer.Spark/
├── libs/
│   ├── spark/
│   │   ├── MintPlayer.Spark/                     # Core framework library (CRUD)
│   │   └── MintPlayer.Spark.Abstractions/        # Shared interfaces and models
│   ├── authorization/
│   │   └── MintPlayer.Spark.Authorization/       # Optional auth + group-based access control
│   ├── messaging/
│   │   ├── MintPlayer.Spark.Messaging/           # Durable message bus with RavenDB persistence
│   │   └── MintPlayer.Spark.Messaging.Abstractions/  # Messaging interfaces (IMessageBus, IRecipient<T>)
│   ├── replication/
│   │   ├── MintPlayer.Spark.Replication/         # Cross-module ETL replication
│   │   └── MintPlayer.Spark.Replication.Abstractions/  # Replication interfaces and models
│   ├── subscription_worker/
│   │   ├── MintPlayer.Spark.SubscriptionWorker/  # RavenDB subscription-based background workers
│   │   └── MintPlayer.Spark.SubscriptionWorker.Abstractions/
│   ├── cron/
│   │   └── MintPlayer.Spark.Cron/                # Cron-scheduled background jobs (multi-node safe)
│   ├── webhooks/
│   │   ├── MintPlayer.Spark.Webhooks.GitHub/     # GitHub webhook integration
│   │   └── MintPlayer.Spark.Webhooks.GitHub.DevTunnel/  # Dev-only: smee.io tunnel + WebSocket client
│   ├── client/
│   │   ├── MintPlayer.Spark.Client/              # Typed HTTP client SDK
│   │   └── MintPlayer.Spark.Client.Authorization/
│   ├── all_features/
│   │   ├── MintPlayer.Spark.AllFeatures/         # All-in-one package (references all + source generator)
│   │   └── MintPlayer.Spark.AllFeatures.SourceGenerators/  # Generates AddSparkFull/UseSparkFull/MapSparkFull
│   ├── source_generators/
│   │   └── MintPlayer.Spark.SourceGenerators/    # Compile-time DI code generation
│   ├── testing/
│   │   └── MintPlayer.Spark.Testing/             # Test harness: embedded RavenDB driver, in-memory host factory
│   ├── socket_extensions/
│   │   └── MintPlayer.Dotnet.SocketExtensions/   # WebSocket read/write helpers
│   └── node_packages/                            # Angular libraries (@mintplayer/ng-spark, ng-spark-auth)
├── tests/                                        # Test projects (unit, source-generator, client, E2E)
├── Demo/
│   ├── DemoApp/                                  # Sample ASP.NET Core + Angular application
│   ├── Fleet/                                    # Fleet management demo (auth, messaging, replication)
│   ├── HR/                                       # HR demo (auth, messaging, replication)
│   └── WebhooksDemo/                             # GitHub webhooks demo application
└── docs/                                         # Documentation (guides, prd/, codecov/)
```

## Documentation

### Developer Guides

| Guide | Description |
|-------|-------------|
| [Getting Started](libs/spark/MintPlayer.Spark/README.md) | PersistentObject pattern, SparkContext, entity definitions, model synchronization |
| [Reference Attributes](docs/guide-reference-attributes.md) | Entity-to-entity links, lookup references, reference selection modals |
| [AsDetail Attributes](docs/guide-asdetail-attributes.md) | Embedded objects, array/collection AsDetail, inline and modal editing |
| [Queries & Sorting](docs/guide-queries-and-sorting.md) | Index-based queries, projections, column sorting, query definitions |
| [The model hash](docs/model-hash.md) | Why a deployed app refuses to start on a stale model, verifying in CI, merge conflicts, the override |
| [Attribute Grouping](docs/guide-attribute-grouping.md) | Two-level Tabs and Groups layout for entity forms and detail pages |
| [Custom Attribute Renderers](docs/guide-custom-attribute-renderers.md) | Replace default attribute display/editing with custom Angular components |
| [Custom Actions](docs/guide-custom-actions.md) | Custom business operations on persistent objects with UI integration |
| [PO/Query Aliases](docs/guide-aliases.md) | Friendly URLs for entities and queries (`/po/car` instead of `/po/{guid}`) |
| [TranslatedString & i18n](docs/guide-translated-strings.md) | Multi-language support for labels, descriptions, and validation messages |
| [Authorization](libs/authorization/MintPlayer.Spark.Authorization/README.md) | Optional security package, `security.json`, groups, permissions, XSRF |
| [Authentication Schemes & `Everyone`](docs/guide-authentication-schemes.md) | Every scheme in the repo, what an unauthenticated caller gets, and what happens when authentication fails |
| [Manager & Retry Actions](docs/guide-manager-retry-actions.md) | IManager interface, confirmation dialogs, chained retry actions |
| [Durable Message Bus](libs/messaging/MintPlayer.Spark.Messaging/README.md) | RavenDB-backed messaging with per-handler retry isolation, checkpoint support, and queue isolation |
| [Cross-Module Synchronization](docs/guide-cross-module-sync.md) | Entity replication between modules with write-back support |
| [Cross-Module mTLS](docs/guide-replication-mtls.md) | Issuing and pinning the client certificates that authenticate one module to another |
| [Subscription Workers](libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker/README.md) | RavenDB subscription-based background processing with retry handling |
| [Cron Jobs](libs/cron/MintPlayer.Spark.Cron/README.md) | Cron-scheduled background jobs, UTC schedules, schedule overrides, multi-node compare-exchange locking |
| [GitHub Webhooks](libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/README.md) | React to GitHub events via typed messages, with smee.io and WebSocket dev tunneling |
| [GitHub Webhooks — Dev Tunnel](libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/README.md) | Dev-only: receive real webhook deliveries on localhost via smee.io or WebSocket forwarding from production |
| [Docker Deployment](docs/guide-docker-deployment.md) | Deploy with Docker Compose, RavenDB configuration, Traefik reverse proxy |
| [Testing Harness](libs/testing/MintPlayer.Spark.Testing/README.md) | Embedded RavenDB driver, in-memory Spark host factory, antiforgery-aware HTTP client, JSON fixtures, Verify defaults |

### Reference

- **[HTTP API Specification](docs/Spark-API-Specification.md)** - Every HTTP endpoint (routes, payloads, auth, retry protocol) exposed by the framework
- **[Spark Library API](libs/spark/MintPlayer.Spark/README.md)** - Detailed API reference and usage guide
- **[Messaging API](libs/messaging/MintPlayer.Spark.Messaging/README.md)** - Message bus API reference
- **[Cron Jobs](libs/cron/MintPlayer.Spark.Cron/README.md)** - Cron-scheduled background jobs: `ISparkCronJob`, schedule overrides, multi-node compare-exchange locking
- **[Product Requirements Document](docs/prd/PRD.md)** - Full specification and architecture

## Contributing

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- [RavenDB 6.2+](https://ravendb.net/) (local instance or Docker)
- IDE: Visual Studio 2025 / VS Code / JetBrains Rider

### Building the Project

The repo is an **Nx 22 workspace** spanning all .NET and Angular projects. Task graph and caching work across both stacks. CI shares build outputs across runs through a self-hosted Nx remote cache — see [Nx remote cache](docs/guide-nx-remote-cache.md) for configuration, token gating, and the cache-poisoning consideration that's relevant if write access to this repo ever expands beyond a single maintainer.

```bash
# Clone the repository
git clone https://github.com/MintPlayer/MintPlayer.Spark.git
cd MintPlayer.Spark

# Install JS dependencies (once; npm workspaces for all ClientApps and libraries)
npm install

# Build everything the Nx graph knows about (.NET + Angular, cached)
npx nx run-many -t build

# Or just what's changed since the last green main
npx nx affected -t build
```

Individual projects:

```bash
# Build a specific .csproj
npx nx build Fleet

# Build an Angular library (ng-packagr)
npx nx build @mintplayer/ng-spark

# Visualize the graph
npx nx graph
```

### Running the Demo Application

F5 from Visual Studio or plain `dotnet run` still works — each demo's `Program.cs` uses `MintPlayer.AspNetCore.SpaServices.UseAngularCliServer`, which spawns `npm run start` in `ClientApp/`. That script now delegates to `nx run <app>:serve`, so Nx orchestrates the dev-server behind the scenes:

```bash
# Start RavenDB (using Docker)
docker run -d -p 8080:8080 -e RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork ravendb/ravendb

# Run the demo application
cd Demo/DemoApp/DemoApp
dotnet run
```

The application will be available at `https://localhost:5001`.

> **RavenDB note:** the demos connect to `http://localhost:8080` (`appsettings.json` → `Spark:RavenDb:Urls`). If you point them at a **standalone/local RavenDB** instead of the Docker container above, make sure its `PublicServerUrl` is `http://localhost:8080` — *not* `http://host.docker.internal:8080`. RavenDB advertises `PublicServerUrl` through its cluster topology and the client routes **all** subsequent requests there (caching it under `Demo/**/bin/**/*.raven-cluster-topology`); a `host.docker.internal` value the host can't reach makes every request fail with `ServiceUnavailable`. `host.docker.internal` is only correct when a *container* must reach a host-installed database.

#### Running multiple modules together (SlnLaunch)

The cross-module demos (HR + Fleet, which replicate data to each other) are launched together with the [`MintPlayer.SlnLaunch`](https://www.nuget.org/packages/MintPlayer.SlnLaunch) dotnet tool, driven by the `MintPlayer.Spark.slnLaunch` profile in the repo root:

```bash
dnx MintPlayer.SlnLaunch        # runs the "HR + Fleet" profile
```

Use **10.0.1+**, which builds the projects sequentially before launching them in parallel (earlier versions could fail intermittently because the concurrent `dotnet run` builds raced on the MSBuild server pipe / shared output DLLs). The ASP.NET hosts come up on:

| Module | Host (Spark API + app) |
| --- | --- |
| Fleet | `https://localhost:5003` |
| HR | `https://localhost:5005` |

> The `/spark/*` endpoints live on the **host** port above. Each host also spawns its own Angular dev server on a separate random port (printed as `➜ Local: http://localhost:<port>/`); hitting that dev-server port directly serves `index.html` for every path, so a request like `/spark/program-units` looks like a 404. Always use the host port for API/middleware requests.

**Library HMR:** edit any file under `libs/node_packages/ng-spark/src/**` or `libs/node_packages/ng-spark-auth/src/**` while a demo is running — changes reflect in the browser without a restart, with component state preserved. Libraries are consumed as **source** during dev (tsconfig path aliases resolve directly to `.ts` files). The ng-packagr `build` target on each library produces the publishable dist for `npm publish`; dev never consumes dist.

### Model Synchronization

When you modify entity classes, regenerate the JSON model files:

```bash
cd Demo/DemoApp
dotnet run --spark-synchronize-model
```

This updates files in `App_Data/Model/` based on your SparkContext properties, and writes
`App_Data/modelHashes.json` — a fingerprint of the entity classes those files were generated from.

> **A deployed application refuses to start when that fingerprint does not match.** Change an entity
> and forget to re-run synchronization, and the app fails at startup rather than serving a model that
> no longer describes its classes — which otherwise surfaces as missing columns and values silently
> dropped on save. In Development it warns instead, since drift there is normal while you are editing.
>
> Commit `App_Data/modelHashes.json` along with `App_Data/Model/`. See **[docs/model-hash.md](docs/model-hash.md)**
> for what the hash covers, how to verify it in CI with `--spark-verify-model`, how to resolve a merge
> conflict on it, and the emergency `SPARK_MODEL_HASH_OVERRIDE` escape hatch.

#### Upgrading an existing application

`modelHashes.json` did not exist before `10.0.0-preview.51`, and the startup check fails closed on a
missing one — so an application upgrading from an earlier preview will **not start in production**
until it has been generated. The API changes below are compile errors and cannot be deployed by
accident; this one is not, so do it first.

1. `dotnet run --spark-synchronize-model`
2. Commit the new `App_Data/modelHashes.json` **and** the regenerated `App_Data/Model/*.json` — model
   attributes are now written in name order, which reorders existing files once.
3. Move model synchronization from the middleware to the builder phase, before `builder.Build()`:

   ```csharp
   // before
   app.UseSpark(o => o.SynchronizeModelsIfRequested<MyContext>(args));

   // after — needs no database, so it also runs in CI
   if (builder.SynchronizeSparkModelsIfRequested(args))
       return;
   ```

4. `app.UseSparkFull(args)` becomes `app.UseSparkFull()`.

#### Excluding a property with `[IgnoreProperty]`

Every public read/write property becomes a model attribute. To keep one out of the model, mark it
`[IgnoreProperty]`:

```csharp
public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;

    [IgnoreProperty]                       // stored by RavenDB, invisible to Spark
    public string InternalToken { get; set; } = string.Empty;
}
```

The property stays an ordinary CLR property and is still persisted. Spark excludes it from the
generated model JSON, from the `PersistentObject` in both directions, from `[Reference]` includes,
from replication (both the payload and the list of fields the owner module may write), and from the
generated `AttributeNames` constants. It applies on embedded/value-object types too.

A computed get-only property is already excluded and needs no attribute — as is a property named
`Id`, which is the document id.

Two things to know:

- **Ignoring an existing property discards its model settings.** The next synchronize removes the
  attribute block from the committed model file, along with its id, translated label, rules,
  renderer and group. Re-adding the property later regenerates it with a new id.
- **Ignoring the last property that referenced an embedded type leaves that type's model file
  behind.** Only projection files are cleaned up automatically; delete an orphaned
  `App_Data/Model/{Type}.json` by hand.

- **The exclusion is only as fresh as the synchronized model.** Inbound writes (including
  cross-module replication) are refused because the attribute is absent from
  `App_Data/Model/`, not by a runtime attribute check. If you add `[IgnoreProperty]` to a
  property that was already in the model and don't re-run synchronize, the old attribute is
  still there and still writable. Re-synchronize and commit the result.
- **It does not filter your ETL script.** `[Replicated(EtlScript = "…")]` is developer-authored
  JavaScript that Spark copies verbatim to RavenDB — nothing derives it from your properties. If
  a field must not leave the source module, leave it out of the script yourself.

Note that `[JsonIgnore]` does **not** do this — model synchronization does not read serialization
attributes.

A build-time analyzer (**SPARK003**) reports a `[Breadcrumb]` template that names an ignored
property, so the contradiction surfaces when you compile rather than when you next synchronize.

### Contribution Workflow

1. **Fork** the repository
2. **Create a feature branch** from `master`
   ```bash
   git checkout -b feature/your-feature-name
   ```
3. **Make your changes** following the coding standards below
4. **Test** your changes with the demo application
5. **Commit** with clear, descriptive messages
6. **Push** to your fork
7. **Open a Pull Request** against `master`

### Coding Standards

- Follow [C# coding conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use nullable reference types (`<Nullable>enable</Nullable>`)
- Use `[Register]` and `[Inject]` attributes from MintPlayer.SourceGenerators for DI
- Add XML documentation comments to public APIs
- Keep methods focused and testable

### Project Guidelines

- **MintPlayer.Spark** - Core library, no application-specific code
- **MintPlayer.Spark.Abstractions** - Interfaces and models shared across projects
- **Demo/DemoApp** - Sample application for testing features
- **Demo/DemoApp.Library** - Example of shared entity definitions

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
