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
| Frontend | Angular 21 |
| Database | RavenDB 6.2+ |
| UI Library | @mintplayer/ng-bootstrap |

## Quick Start (AllFeatures)

The fastest way to get started is with `MintPlayer.Spark.AllFeatures`. Reference this single package and three source-generated methods handle all the wiring:

```csharp
builder.Services.AddSparkFull(builder.Configuration);

app.UseRouting();
app.UseSparkFull(args);
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

See the [AllFeatures documentation](docs/PRD-AllFeatures.md) for details.

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
app.UseSpark(o => o.SynchronizeModelsIfRequested<MySparkContext>(args));
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
├── MintPlayer.Spark/                            # Core framework library (CRUD)
├── MintPlayer.Spark.Abstractions/               # Shared interfaces and models
├── MintPlayer.Spark.Authorization/              # Optional auth + group-based access control
├── MintPlayer.Spark.Messaging.Abstractions/     # Messaging interfaces (IMessageBus, IRecipient<T>)
├── MintPlayer.Spark.Messaging/                  # Durable message bus with RavenDB persistence
├── MintPlayer.Spark.Replication/                # Cross-module ETL replication
├── MintPlayer.Spark.Replication.Abstractions/   # Replication interfaces and models
├── MintPlayer.Spark.SubscriptionWorker/         # RavenDB subscription-based background workers
├── MintPlayer.Spark.Webhooks.GitHub/            # GitHub webhook integration
├── MintPlayer.Spark.Webhooks.GitHub.DevTunnel/  # Dev-only: smee.io tunnel + WebSocket client
├── MintPlayer.Spark.SourceGenerators/           # Compile-time DI code generation
├── MintPlayer.Spark.AllFeatures/                # All-in-one package (references all + source generator)
├── MintPlayer.Spark.AllFeatures.SourceGenerators/ # Generates AddSparkFull/UseSparkFull/MapSparkFull
├── MintPlayer.Dotnet.SocketExtensions/          # WebSocket read/write helpers
├── Demo/
│   ├── DemoApp/                                 # Sample ASP.NET Core + Angular application
│   ├── Fleet/                                   # Fleet management demo (auth, messaging, replication)
│   ├── HR/                                      # HR demo (auth, messaging, replication)
│   ├── WebhooksDemo/                            # GitHub webhooks demo application
│   └── DemoApp.Library/                         # Shared entity definitions
└── docs/                                        # Documentation
```

## Documentation

### Developer Guides

| Guide | Description |
|-------|-------------|
| [Getting Started](docs/guide-getting-started.md) | PersistentObject pattern, SparkContext, entity definitions, model synchronization |
| [Reference Attributes](docs/guide-reference-attributes.md) | Entity-to-entity links, lookup references, reference selection modals |
| [AsDetail Attributes](docs/guide-asdetail-attributes.md) | Embedded objects, array/collection AsDetail, inline and modal editing |
| [Queries & Sorting](docs/guide-queries-and-sorting.md) | Index-based queries, projections, column sorting, query definitions |
| [Attribute Grouping](docs/guide-attribute-grouping.md) | Two-level Tabs and Groups layout for entity forms and detail pages |
| [Custom Attribute Renderers](docs/guide-custom-attribute-renderers.md) | Replace default attribute display/editing with custom Angular components |
| [Custom Actions](docs/guide-custom-actions.md) | Custom business operations on persistent objects with UI integration |
| [PO/Query Aliases](docs/guide-aliases.md) | Friendly URLs for entities and queries (`/po/car` instead of `/po/{guid}`) |
| [TranslatedString & i18n](docs/guide-translated-strings.md) | Multi-language support for labels, descriptions, and validation messages |
| [Authorization](docs/guide-authorization.md) | Optional security package, `security.json`, groups, permissions, XSRF |
| [Manager & Retry Actions](docs/guide-manager-retry-actions.md) | IManager interface, confirmation dialogs, chained retry actions |
| [Durable Message Bus](docs/guide-message-bus.md) | RavenDB-backed messaging with per-handler retry isolation, checkpoint support, and queue isolation |
| [Cross-Module Synchronization](docs/guide-cross-module-sync.md) | Entity replication between modules with write-back support |
| [Subscription Workers](docs/guide-subscription-workers.md) | RavenDB subscription-based background processing with retry handling |
| [GitHub Webhooks](docs/guide-github-webhooks.md) | React to GitHub events via typed messages, with smee.io and WebSocket dev tunneling |
| [Docker Deployment](docs/guide-docker-deployment.md) | Deploy with Docker Compose, RavenDB configuration, Traefik reverse proxy |

### Reference

- **[Spark Library API](MintPlayer.Spark/README.md)** - Detailed API reference and usage guide
- **[Messaging API](MintPlayer.Spark.Messaging/README.md)** - Message bus API reference
- **[Product Requirements Document](docs/PRD.md)** - Full specification and architecture

## Contributing

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- [RavenDB 6.2+](https://ravendb.net/) (local instance or Docker)
- IDE: Visual Studio 2025 / VS Code / JetBrains Rider

### Building the Project

The repo is an **Nx 22 workspace** spanning all .NET and Angular projects. Task graph and caching work across both stacks.

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

**Library HMR:** edit any file under `node_packages/ng-spark/src/**` or `node_packages/ng-spark-auth/src/**` while a demo is running — changes reflect in the browser without a restart, with component state preserved. Libraries are consumed as **source** during dev (tsconfig path aliases resolve directly to `.ts` files). The ng-packagr `build` target on each library produces the publishable dist for `npm publish`; dev never consumes dist.

### Model Synchronization

When you modify entity classes, regenerate the JSON model files:

```bash
cd Demo/DemoApp
dotnet run --spark-synchronize-model
```

This updates files in `App_Data/Model/` based on your SparkContext properties.

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
