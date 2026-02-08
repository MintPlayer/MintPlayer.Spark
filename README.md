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
builder.Services.AddSpark(builder.Configuration);
builder.Services.AddScoped<SparkContext, MySparkContext>();
builder.Services.AddSparkActions();

// 2. Configure middleware
app.UseSpark();
app.SynchronizeSparkModelsIfRequested<MySparkContext>(args);
app.CreateSparkIndexes();
app.UseEndpoints(endpoints => endpoints.MapSpark());
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
├── MintPlayer.Spark.SourceGenerators/           # Compile-time DI code generation
├── Demo/
│   ├── DemoApp/                                 # Sample ASP.NET Core + Angular application
│   └── DemoApp.Library/                         # Shared entity definitions
└── docs/                                        # Documentation
```

## Documentation

- **[Spark Library Documentation](MintPlayer.Spark/README.md)** - Detailed API reference and usage guide
- **[Messaging Documentation](MintPlayer.Spark.Messaging/README.md)** - Durable message bus with RavenDB persistence, scoped recipients, and retry logic
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
