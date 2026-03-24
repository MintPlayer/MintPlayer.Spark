# MintPlayer.Spark

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

## Quick Start

```csharp
// 1. Configure services
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<MySparkContext>();
    spark.AddActions();
    spark.AddMessaging();
    spark.AddRecipients();
});

// 2. Configure middleware
app.UseRouting();
app.UseSpark();
app.SynchronizeSparkModelsIfRequested<MySparkContext>(args);
app.MapSpark();
```

```csharp
// 3. Define your context
public class MySparkContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
}
```

```bash
# 4. Generate model files
dotnet run --spark-synchronize-model
```

## Project Structure

```
MintPlayer.Spark/
├── MintPlayer.Spark/                            # Core framework library (CRUD)
├── MintPlayer.Spark.Abstractions/               # Shared interfaces and models
├── MintPlayer.Spark.Messaging.Abstractions/     # Messaging interfaces (IMessageBus, IRecipient<T>)
├── MintPlayer.Spark.Messaging/                  # Durable message bus with RavenDB persistence
├── MintPlayer.Spark.Webhooks.GitHub/            # GitHub webhook integration
├── MintPlayer.Spark.Webhooks.GitHub.DevTunnel/  # Dev-only: smee.io tunnel + WebSocket client
├── MintPlayer.Spark.SourceGenerators/           # Compile-time DI code generation
├── MintPlayer.Dotnet.SocketExtensions/          # WebSocket read/write helpers
├── Demo/
│   ├── DemoApp/                                 # Sample ASP.NET Core + Angular application
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
| [Durable Message Bus](docs/guide-message-bus.md) | RavenDB-backed messaging with scoped recipients and retry logic |
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

```bash
# Clone the repository
git clone https://github.com/MintPlayer/MintPlayer.Spark.git
cd MintPlayer.Spark

# Build the solution
dotnet build MintPlayer.Spark.sln

# Build the Angular frontend
cd Demo/DemoApp/ClientApp
npm install
npm run build
```

### Running the Demo Application

```bash
# Start RavenDB (using Docker)
docker run -d -p 8080:8080 -e RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork ravendb/ravendb

# Run the demo application
cd Demo/DemoApp
dotnet run
```

The application will be available at `https://localhost:5001`.

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
