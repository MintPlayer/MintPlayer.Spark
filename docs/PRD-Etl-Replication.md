# ETL Replication PRD for MintPlayer.Spark

## Overview

Implement cross-module ETL (Extract-Transform-Load) replication for the Spark framework, allowing independent Spark applications to replicate data between each other using RavenDB's native ETL capabilities. This includes module registration, attribute-based ETL script declaration, and automatic ETL task deployment via the existing durable message bus.

## Motivation

In a microservices/modular architecture, different applications own different data domains. For example, a **Fleet** module owns Cars and CarBrands, while an **HR** module owns People, Addresses, and Companies. However, HR may need a read-only copy of Cars for display purposes. Rather than querying Fleet's API at runtime, RavenDB ETL replication can push transformed copies of documents from Fleet's database into HR's database automatically. This is a proven pattern used in the Cronos ecosystem (inspiration: `C:\Repos\ETL`, `C:\Repos\Fleet`, `C:\Repos\HR`).

---

## Goals

1. **Module Registration**: Each Spark application registers itself (name, URL, database) in a shared `SparkModules` RavenDB database on startup
2. **Declarative ETL Scripts**: A `[Replicated]` attribute allows developers to declare what data their module needs from other modules, with the JavaScript ETL transformation inline
3. **Automatic ETL Deployment**: On startup, each module collects its `[Replicated]` attributes, groups them by source module, and sends the ETL scripts to the correct source module using the durable message bus (with retry logic)
4. **ETL Endpoint**: A receiving module exposes an endpoint (via the Spark library) that accepts ETL script requests and creates/updates RavenDB ETL tasks in its own database
5. **Demo Applications**: Two demo apps (Fleet and HR) demonstrate the full replication flow

---

## Architecture

### High-Level Flow

```
┌─────────────────────────────────────────────────────┐
│                  SparkModules DB                     │
│  (shared RavenDB database for module discovery)      │
│                                                      │
│  moduleInformations/Fleet  { url, db, name }        │
│  moduleInformations/HR     { url, db, name }        │
└──────────────┬──────────────────────┬───────────────┘
               │                      │
        registers on startup    registers on startup
               │                      │
    ┌──────────▼──────────┐  ┌────────▼────────────┐
    │   Fleet App         │  │   HR App             │
    │   localhost:5001    │  │   localhost:5002      │
    │   DB: SparkFleet    │  │   DB: SparkHR        │
    │                     │  │                       │
    │   Owns:             │  │   Owns:               │
    │   - Cars            │  │   - People            │
    │   - CarBrands       │  │   - Addresses         │
    │                     │  │   - Companies          │
    │                     │  │                       │
    │   Receives ETL      │  │   Has [Replicated]:   │
    │   scripts from HR ◄─┼──┤   - Car (from Fleet)  │
    │   via message bus   │  │                       │
    │                     │  │   On startup:          │
    │   Creates RavenDB   │  │   1. Register in       │
    │   ETL task to push  │  │      SparkModules      │
    │   Cars → SparkHR    │  │   2. Scan [Replicated] │
    └─────────────────────┘  │   3. Lookup Fleet URL  │
                             │   4. POST ETL scripts  │
                             │      to Fleet via       │
                             │      message bus        │
                             └────────────────────────┘
```

### Data Flow for Replication

1. **HR startup** → scans assembly for `[Replicated]` attributes
2. Finds `[Replicated(SourceModule = "Fleet", ...)]` on HR's `Car` class
3. Looks up Fleet's URL from `SparkModules` database
4. Sends an `EtlScriptDeploymentMessage` via `IMessageBus` to itself (local queue)
5. A recipient picks up the message and POSTs the ETL scripts to Fleet's `/spark/etl/deploy` endpoint
6. Fleet's endpoint receives the scripts and creates/updates a RavenDB ETL task:
   - Connection string pointing to HR's database (`SparkHR`)
   - Transformation script from the `[Replicated]` attribute
7. RavenDB in Fleet now continuously pushes transformed Car documents to HR's database

---

## New Library: MintPlayer.Spark.Replication

A separate library project to keep replication concerns isolated. It depends on:
- `MintPlayer.Spark.Abstractions` (for shared types)
- `MintPlayer.Spark.Messaging.Abstractions` (for `IMessageBus`, `IRecipient<T>`)
- `Raven.Client` (for ETL operations)

### Project: MintPlayer.Spark.Replication.Abstractions

Shared types that demo apps reference:

```
MintPlayer.Spark.Replication.Abstractions/
├── ReplicatedAttribute.cs
├── Models/
│   ├── ModuleInformation.cs
│   ├── EtlScriptRequest.cs
│   └── EtlDeploymentResult.cs
└── Configuration/
    └── SparkReplicationOptions.cs
```

### Project: MintPlayer.Spark.Replication

Implementation:

```
MintPlayer.Spark.Replication/
├── Services/
│   ├── ModuleRegistrationService.cs
│   ├── EtlScriptCollector.cs
│   ├── EtlDeploymentService.cs
│   └── EtlTaskManager.cs
├── Messages/
│   ├── EtlScriptDeploymentMessage.cs
│   └── EtlScriptDeploymentRecipient.cs
├── Endpoints/
│   └── EtlEndpoints.cs
└── Extensions/
    └── SparkReplicationExtensions.cs
```

---

## Detailed Design

### 1. Module Registration

#### ModuleInformation Model

```csharp
namespace MintPlayer.Spark.Replication.Abstractions.Models;

public class ModuleInformation
{
    public string? Id { get; set; }           // "moduleInformations/{AppName}"
    public required string AppName { get; set; }
    public required string AppUrl { get; set; }   // e.g. "https://localhost:5001"
    public required string DatabaseName { get; set; }
    public DateTime RegisteredAtUtc { get; set; }
}
```

#### SparkReplicationOptions

```csharp
namespace MintPlayer.Spark.Replication.Abstractions.Configuration;

public class SparkReplicationOptions
{
    /// <summary>Name of this module (e.g. "Fleet", "HR")</summary>
    public required string ModuleName { get; set; }

    /// <summary>The publicly reachable URL of this module</summary>
    public required string ModuleUrl { get; set; }

    /// <summary>
    /// Connection info for the shared SparkModules database.
    /// All modules must point to the same shared database.
    /// </summary>
    public string[] SparkModulesUrls { get; set; } = ["http://localhost:8080"];
    public string SparkModulesDatabase { get; set; } = "SparkModules";
}
```

#### Registration Behavior

On application startup (via `app.UseSparkReplication()`):

1. Open a session to the `SparkModules` database
2. Store/update a `ModuleInformation` document with ID `moduleInformations/{ModuleName}`
3. This makes the module discoverable by other modules

### 2. The [Replicated] Attribute

```csharp
namespace MintPlayer.Spark.Replication.Abstractions;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ReplicatedAttribute : Attribute
{
    /// <summary>
    /// The name of the module that owns the original data.
    /// Must match the ModuleName in the source module's SparkReplicationOptions.
    /// </summary>
    public required string SourceModule { get; init; }

    /// <summary>
    /// The RavenDB collection name in the source database to replicate from.
    /// If null, the collection name is inferred from the OriginalType.
    /// </summary>
    public string? SourceCollection { get; init; }

    /// <summary>
    /// The original CLR type in the source module.
    /// Used to infer the source collection name if SourceCollection is not set.
    /// </summary>
    public Type? OriginalType { get; init; }

    /// <summary>
    /// The JavaScript ETL transformation script.
    /// Uses RavenDB ETL script syntax (e.g. loadToCars({...})).
    /// </summary>
    public required string EtlScript { get; init; }
}
```

#### Example Usage (in HR module)

```csharp
[Replicated(
    SourceModule = "Fleet",
    SourceCollection = "Cars",
    EtlScript = """
        loadToCars({
            LicensePlate: this.LicensePlate,
            Model: this.Model,
            Year: this.Year,
            Color: this.Color,
            Brand: this.Brand,
            '@metadata': {
                '@collection': 'Cars'
            }
        });
    """)]
public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }
    public string? Brand { get; set; }
}
```

### 3. ETL Script Collection & Deployment

#### EtlScriptRequest Model

```csharp
namespace MintPlayer.Spark.Replication.Abstractions.Models;

public class EtlScriptRequest
{
    /// <summary>Name of the requesting module (the one that wants the data)</summary>
    public required string RequestingModule { get; set; }

    /// <summary>Database name of the requesting module (target for ETL)</summary>
    public required string TargetDatabase { get; set; }

    /// <summary>RavenDB URLs of the requesting module (for connection string)</summary>
    public required string[] TargetUrls { get; set; }

    /// <summary>Individual ETL transformation scripts grouped by source collection</summary>
    public required List<EtlScriptItem> Scripts { get; set; }
}

public class EtlScriptItem
{
    /// <summary>Source collection name in the owning module's database</summary>
    public required string SourceCollection { get; set; }

    /// <summary>JavaScript ETL transformation script</summary>
    public required string Script { get; set; }
}
```

#### EtlScriptCollector Service

On startup, scans the application's assemblies for `[Replicated]` attributes:

```csharp
namespace MintPlayer.Spark.Replication.Services;

public class EtlScriptCollector
{
    /// <summary>
    /// Scans assemblies for [Replicated] attributes and groups them by source module.
    /// Returns a dictionary: SourceModuleName → List<EtlScriptItem>
    /// </summary>
    public Dictionary<string, List<EtlScriptItem>> CollectScripts(params Assembly[] assemblies);
}
```

**Logic:**
1. Find all classes with `[Replicated]`
2. For each, extract `SourceModule`, `SourceCollection` (or infer from `OriginalType`), and `EtlScript`
3. Group by `SourceModule`
4. Return grouped scripts

#### Deployment via Message Bus

Instead of directly POSTing to remote modules, the deployment uses the existing **durable message bus** (`MintPlayer.Spark.Messaging`). This provides:

- **Retry logic** with exponential backoff
- **Persistence** — messages survive app restarts
- **Dead-letter queue** for failed deployments
- **Queue isolation** — ETL deployments don't block other messages

##### EtlScriptDeploymentMessage

```csharp
namespace MintPlayer.Spark.Replication.Messages;

[MessageQueue("spark-etl-deployment")]
public class EtlScriptDeploymentMessage
{
    /// <summary>The source module that owns the data and should create the ETL task</summary>
    public required string SourceModuleName { get; set; }

    /// <summary>The URL of the source module (looked up from SparkModules DB)</summary>
    public required string SourceModuleUrl { get; set; }

    /// <summary>The ETL script request payload to send to the source module</summary>
    public required EtlScriptRequest Request { get; set; }
}
```

##### EtlScriptDeploymentRecipient

A message bus recipient that:
1. Receives the `EtlScriptDeploymentMessage`
2. POSTs the `EtlScriptRequest` to `{SourceModuleUrl}/spark/etl/deploy`
3. If the HTTP call fails, throws an exception → message bus retries automatically

```csharp
namespace MintPlayer.Spark.Replication.Messages;

public class EtlScriptDeploymentRecipient : IRecipient<EtlScriptDeploymentMessage>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EtlScriptDeploymentRecipient> _logger;

    public async Task HandleAsync(EtlScriptDeploymentMessage message, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("spark-etl");
        var response = await client.PostAsJsonAsync(
            $"{message.SourceModuleUrl}/spark/etl/deploy",
            message.Request,
            ct);
        response.EnsureSuccessStatusCode();
    }
}
```

##### Startup Flow

On `app.UseSparkReplication()`:

1. Register module in SparkModules DB
2. Scan assemblies for `[Replicated]` attributes
3. For each source module group:
   a. Look up the source module's URL from SparkModules DB
   b. Build an `EtlScriptRequest` with this module's database info as target
   c. Broadcast an `EtlScriptDeploymentMessage` via `IMessageBus`
4. The message bus picks up the message and the recipient POSTs to the source module
5. On failure → automatic retry with exponential backoff

### 4. ETL Receiving Endpoint

Each Spark module that uses replication exposes:

```
POST /spark/etl/deploy
```

This endpoint is mapped by `app.MapSparkReplication()`.

#### EtlTaskManager Service

Handles creating/updating RavenDB ETL tasks:

```csharp
namespace MintPlayer.Spark.Replication.Services;

public class EtlTaskManager
{
    private readonly IDocumentStore _documentStore;

    /// <summary>
    /// Creates or updates RavenDB ETL tasks for the given request.
    /// 1. Creates/updates a RavenConnectionString pointing to the target database
    /// 2. Creates/updates an RavenEtlConfiguration with the transformation scripts
    /// 3. Resets ETL tasks where scripts have changed
    /// </summary>
    public async Task DeployEtlScriptsAsync(EtlScriptRequest request);
}
```

**Implementation steps:**

1. **Connection String**: Use `PutConnectionStringOperation<RavenConnectionString>` to create/update a connection string named `spark-etl-{RequestingModule}` pointing to the requesting module's database URLs and database name.

2. **ETL Configuration**: Use `AddEtlOperation<RavenConnectionString>` or `UpdateEtlOperation<RavenConnectionString>` to create/update an ETL task named `spark-etl-{RequestingModule}` with:
   - One `Transformation` per script item
   - Each transformation maps a source collection to the target via the JavaScript script

3. **Change Detection**: Compare existing transformations to new ones. Only reset ETL (via `ResetEtlOperation`) for scripts that have actually changed.

4. **Cleanup**: Remove transformations for collections that are no longer requested.

### 5. Extension Methods

```csharp
namespace MintPlayer.Spark.Replication;

public static class SparkReplicationExtensions
{
    /// <summary>Register replication services</summary>
    public static IServiceCollection AddSparkReplication(
        this IServiceCollection services,
        Action<SparkReplicationOptions> configure);

    /// <summary>Register module and trigger ETL script deployment</summary>
    public static WebApplication UseSparkReplication(this WebApplication app);

    /// <summary>Map the /spark/etl/deploy endpoint</summary>
    public static WebApplication MapSparkReplication(this WebApplication app);
}
```

---

## Demo Applications

### Folder Structure

```
Demo/
├── Fleet/
│   ├── Fleet.csproj
│   ├── Program.cs
│   ├── FleetSparkContext.cs
│   ├── Entities/
│   │   ├── Car.cs
│   │   └── CarBrand.cs       (DynamicLookupReference)
│   ├── LookupReferences/
│   │   └── CarStatus.cs      (TransientLookupReference)
│   ├── Indexes/
│   │   └── Cars_Overview.cs
│   └── ClientApp/             (Angular frontend)
│
├── HR/
│   ├── HR.csproj
│   ├── Program.cs
│   ├── HRSparkContext.cs
│   ├── Entities/
│   │   ├── Person.cs
│   │   ├── Address.cs          (nested type on Person)
│   │   └── Company.cs
│   ├── Replicated/
│   │   └── Car.cs              ([Replicated] from Fleet)
│   ├── Indexes/
│   │   └── People_Overview.cs
│   └── ClientApp/              (Angular frontend)
│
└── Fleet.Shared/                (optional: shared entity types for type-safe OriginalType references)
    ├── Fleet.Shared.csproj
    └── Car.cs                   (minimal class for typeof() reference)
```

### Fleet Demo App

**Database**: `SparkFleet`
**URL**: `https://localhost:5001`
**Module Name**: `Fleet`

**Entities**:
- `Car`: Id, LicensePlate, Model, Year, Color, Status (LookupRef: CarStatus), Brand (LookupRef: CarBrand)
- `CarBrand`: DynamicLookupReference (user-managed)

**Program.cs**:
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(opt => {
    opt.RavenDb.Urls = ["http://localhost:8080"];
    opt.RavenDb.Database = "SparkFleet";
});
builder.Services.AddScoped<SparkContext, FleetSparkContext>();
builder.Services.AddSparkActions();
builder.Services.AddSparkMessaging();
builder.Services.AddSparkRecipients();
builder.Services.AddSparkReplication(opt => {
    opt.ModuleName = "Fleet";
    opt.ModuleUrl = "https://localhost:5001";
    opt.SparkModulesUrls = ["http://localhost:8080"];
    opt.SparkModulesDatabase = "SparkModules";
});

var app = builder.Build();

app.UseSpark();
app.CreateSparkIndexes();
app.CreateSparkMessagingIndexes();
app.UseSparkReplication();    // Register module + deploy ETL scripts (if any)
app.MapSpark();
app.MapSparkReplication();    // Expose POST /spark/etl/deploy

app.Run();
```

### HR Demo App

**Database**: `SparkHR`
**URL**: `https://localhost:5002`
**Module Name**: `HR`

**Entities (owned)**:
- `Person`: Id, FirstName, LastName, Email, DateOfBirth, Company (Reference), Address (nested)
- `Company`: Id, Name, Website
- `Address`: Street, PostalCode, City (nested on Person)

**Replicated entity**:
```csharp
// In HR/Replicated/Car.cs
[Replicated(
    SourceModule = "Fleet",
    SourceCollection = "Cars",
    EtlScript = """
        loadToCars({
            LicensePlate: this.LicensePlate,
            Model: this.Model,
            Year: this.Year,
            Color: this.Color,
            '@metadata': { '@collection': 'Cars' }
        });
    """)]
public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }
}
```

**Program.cs**:
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(opt => {
    opt.RavenDb.Urls = ["http://localhost:8080"];
    opt.RavenDb.Database = "SparkHR";
});
builder.Services.AddScoped<SparkContext, HRSparkContext>();
builder.Services.AddSparkActions();
builder.Services.AddSparkMessaging();
builder.Services.AddSparkRecipients();
builder.Services.AddSparkReplication(opt => {
    opt.ModuleName = "HR";
    opt.ModuleUrl = "https://localhost:5002";
    opt.SparkModulesUrls = ["http://localhost:8080"];
    opt.SparkModulesDatabase = "SparkModules";
});

var app = builder.Build();

app.UseSpark();
app.CreateSparkIndexes();
app.CreateSparkMessagingIndexes();
app.UseSparkReplication();    // Register module + send ETL scripts to Fleet via message bus
app.MapSpark();
app.MapSparkReplication();    // Expose POST /spark/etl/deploy

app.Run();
```

### Demo Flow

1. Start Fleet app → registers as `moduleInformations/Fleet` in SparkModules DB
2. Start HR app → registers as `moduleInformations/HR` in SparkModules DB
3. HR scans assemblies → finds `[Replicated(SourceModule = "Fleet")]` on `Car`
4. HR looks up Fleet's URL from SparkModules DB
5. HR broadcasts `EtlScriptDeploymentMessage` via message bus
6. Message bus recipient POSTs to `https://localhost:5001/spark/etl/deploy`
7. Fleet's endpoint creates:
   - Connection string `spark-etl-HR` → `SparkHR` database
   - ETL task `spark-etl-HR` with Cars transformation
8. RavenDB in Fleet now pushes Car documents to SparkHR
9. HR can query Cars from its own database (read-only replicated copies)

---

## RavenDB ETL Operations Reference

These are the RavenDB maintenance operations used by `EtlTaskManager`:

| Operation | Purpose |
|-----------|---------|
| `PutConnectionStringOperation<RavenConnectionString>` | Create/update connection string to target DB |
| `AddEtlOperation<RavenConnectionString>` | Create a new ETL task |
| `UpdateEtlOperation<RavenConnectionString>` | Update an existing ETL task |
| `ResetEtlOperation` | Reset ETL state to re-process all documents |
| `DeleteOngoingTaskOperation` | Remove an ETL task |
| `GetOngoingTaskInfoOperation` | Check if an ETL task exists |

---

## API Endpoints

### POST /spark/etl/deploy

**Request Body**: `EtlScriptRequest`
```json
{
    "requestingModule": "HR",
    "targetDatabase": "SparkHR",
    "targetUrls": ["http://localhost:8080"],
    "scripts": [
        {
            "sourceCollection": "Cars",
            "script": "loadToCars({ LicensePlate: this.LicensePlate, ... });"
        }
    ]
}
```

**Response**: `200 OK` with `EtlDeploymentResult`
```json
{
    "success": true,
    "tasksCreated": 0,
    "tasksUpdated": 1,
    "tasksRemoved": 0
}
```

**Error Responses**:
- `400 Bad Request` — invalid request body
- `500 Internal Server Error` — RavenDB operation failed (triggers message bus retry)

---

## Implementation Steps

### Phase 1: Abstractions
1. [ ] Create `MintPlayer.Spark.Replication.Abstractions` project
2. [ ] Implement `ReplicatedAttribute`
3. [ ] Implement `ModuleInformation` model
4. [ ] Implement `EtlScriptRequest` / `EtlScriptItem` / `EtlDeploymentResult` models
5. [ ] Implement `SparkReplicationOptions`

### Phase 2: Core Replication Library
6. [ ] Create `MintPlayer.Spark.Replication` project
7. [ ] Implement `ModuleRegistrationService` (register in SparkModules DB on startup)
8. [ ] Implement `EtlScriptCollector` (scan assemblies for `[Replicated]`)
9. [ ] Implement `EtlScriptDeploymentMessage` and `EtlScriptDeploymentRecipient` (message bus integration)
10. [ ] Implement `EtlTaskManager` (create/update/remove RavenDB ETL tasks)
11. [ ] Implement ETL endpoint (`POST /spark/etl/deploy`)
12. [ ] Implement `SparkReplicationExtensions` (`AddSparkReplication`, `UseSparkReplication`, `MapSparkReplication`)

### Phase 3: Demo Applications
13. [ ] Remove or rename existing DemoApp
14. [ ] Create Fleet demo app with Car, CarBrand, CarStatus entities
15. [ ] Create HR demo app with Person, Company, Address entities
16. [ ] Add `[Replicated]` Car entity in HR
17. [ ] Configure both apps with replication options
18. [ ] Test end-to-end: Fleet running → HR starts → ETL created → Cars replicated

### Phase 4: Polish
19. [ ] Add logging throughout the replication pipeline
20. [ ] Handle edge cases: module not yet registered, connection refused, stale ETL tasks
21. [ ] Ensure SparkModules database is auto-created in development mode
22. [ ] Add XML documentation comments on public API

---

## Files to Create

### New Projects
- `MintPlayer.Spark.Replication.Abstractions/MintPlayer.Spark.Replication.Abstractions.csproj`
- `MintPlayer.Spark.Replication/MintPlayer.Spark.Replication.csproj`

### New Files (Abstractions)
- `ReplicatedAttribute.cs`
- `Models/ModuleInformation.cs`
- `Models/EtlScriptRequest.cs`
- `Models/EtlScriptItem.cs`
- `Models/EtlDeploymentResult.cs`
- `Configuration/SparkReplicationOptions.cs`

### New Files (Replication Library)
- `Services/ModuleRegistrationService.cs`
- `Services/EtlScriptCollector.cs`
- `Services/EtlTaskManager.cs`
- `Messages/EtlScriptDeploymentMessage.cs`
- `Messages/EtlScriptDeploymentRecipient.cs`
- `Endpoints/EtlEndpoints.cs`
- `Extensions/SparkReplicationExtensions.cs`

### New Files (Demo - Fleet)
- `Demo/Fleet/Fleet.csproj`
- `Demo/Fleet/Program.cs`
- `Demo/Fleet/FleetSparkContext.cs`
- `Demo/Fleet/Entities/Car.cs`
- `Demo/Fleet/LookupReferences/CarBrand.cs`
- `Demo/Fleet/LookupReferences/CarStatus.cs`
- `Demo/Fleet/Indexes/Cars_Overview.cs`
- `Demo/Fleet/appsettings.json`
- `Demo/Fleet/Properties/launchSettings.json`

### New Files (Demo - HR)
- `Demo/HR/HR.csproj`
- `Demo/HR/Program.cs`
- `Demo/HR/HRSparkContext.cs`
- `Demo/HR/Entities/Person.cs`
- `Demo/HR/Entities/Address.cs`
- `Demo/HR/Entities/Company.cs`
- `Demo/HR/Replicated/Car.cs`
- `Demo/HR/Indexes/People_Overview.cs`
- `Demo/HR/appsettings.json`
- `Demo/HR/Properties/launchSettings.json`

### Modified Files
- `MintPlayer.Spark.sln` — add new projects
- `Demo/DemoApp/` — remove or keep as a third example

---

## Configuration

### appsettings.json (Fleet)
```json
{
    "Spark": {
        "RavenDb": {
            "Urls": ["http://localhost:8080"],
            "Database": "SparkFleet"
        }
    },
    "SparkReplication": {
        "ModuleName": "Fleet",
        "ModuleUrl": "https://localhost:5001",
        "SparkModulesUrls": ["http://localhost:8080"],
        "SparkModulesDatabase": "SparkModules"
    }
}
```

### appsettings.json (HR)
```json
{
    "Spark": {
        "RavenDb": {
            "Urls": ["http://localhost:8080"],
            "Database": "SparkHR"
        }
    },
    "SparkReplication": {
        "ModuleName": "HR",
        "ModuleUrl": "https://localhost:5002",
        "SparkModulesUrls": ["http://localhost:8080"],
        "SparkModulesDatabase": "SparkModules"
    }
}
```

---

## Message Bus Integration Details

The ETL deployment uses the existing `MintPlayer.Spark.Messaging` infrastructure:

- **Message**: `EtlScriptDeploymentMessage` with `[MessageQueue("spark-etl-deployment")]`
- **Recipient**: `EtlScriptDeploymentRecipient` implements `IRecipient<EtlScriptDeploymentMessage>`
- **Persistence**: Messages are stored in the module's own RavenDB database (same as all Spark messages)
- **Retry**: On HTTP failure, the message bus automatically retries with exponential backoff (default: 2s, 10s, 30s, 60s, 300s)
- **Dead Letter**: After max attempts (default 5), the message is dead-lettered for manual inspection
- **Idempotency**: The `EtlTaskManager.DeployEtlScriptsAsync` is idempotent — re-deploying the same scripts is a no-op (existing ETL tasks are only updated if scripts actually changed)

This approach is more robust than direct HTTP calls because:
1. If the source module is down when this module starts, the message bus retries automatically
2. Messages survive application restarts (persisted in RavenDB)
3. No need for custom retry/circuit-breaker logic

---

## Open Questions

1. **Shared entity types**: Should we require a shared library (e.g., `Fleet.Shared`) for `OriginalType` references, or is `SourceCollection` string-based matching sufficient?
   - **Recommendation**: Use `SourceCollection` string for loose coupling. `OriginalType` is optional for cases where a shared library exists.

2. **Authentication**: Should the `/spark/etl/deploy` endpoint require authentication between modules?
   - **Recommendation**: Not for the initial implementation. Can be added later with a shared API key in `SparkReplicationOptions`.

3. **Bidirectional replication**: Should we support HR pushing data back to Fleet?
   - **Recommendation**: Yes, the architecture already supports this. Any module can have `[Replicated]` attributes pointing to any other module. The design is fully symmetrical.

4. **What happens to the existing DemoApp?**
   - **Recommendation**: Keep it as-is for now (it demonstrates non-replication Spark features). The Fleet and HR apps are added alongside it.
