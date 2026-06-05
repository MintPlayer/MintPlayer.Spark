# PRD: Unified Spark Builder Pattern

## Problem Statement

Currently, consuming applications (DemoApp, HR, Fleet) must call many individual `AddSpark*` / `UseSpark*` extension methods in their `Program.cs`. This leads to:

1. **Verbose startup code** -- up to 12+ separate Spark-related calls scattered through `Program.cs`
2. **Easy to forget a call** -- e.g. forgetting `CreateSparkMessagingIndexes()` after adding `AddSparkMessaging()`
3. **Ordering mistakes** -- `UseSparkAntiforgery()` must come before `UseSpark()`, `UseAuthentication()` before `UseAuthorization()`, etc.
4. **No discoverability** -- new consumers have to read docs to know which methods exist and which are optional

### Current State (Fleet Program.cs -- worst case)

```csharp
// Services
builder.Services.AddSpark(builder.Configuration);
builder.Services.AddSparkActions();                          // source-generated
builder.Services.AddSparkCustomActions();                    // source-generated
builder.Services.AddScoped<SparkContext, FleetContext>();
builder.Services.AddSparkAuthorization();                    // MintPlayer.Spark.Authorization
builder.Services.AddSparkAuthentication<SparkUser>();         // MintPlayer.Spark.Authorization
builder.Services.AddSparkMessaging();                        // MintPlayer.Spark.Messaging
builder.Services.AddSparkRecipients();                       // source-generated (if present)
builder.Services.AddSparkReplication(opt => { ... });         // MintPlayer.Spark.Replication

// Middleware
app.UseSparkAntiforgery();                                   // MintPlayer.Spark.Authorization
app.UseSpark();                                              // MintPlayer.Spark
app.CreateSparkIndexes();                                    // MintPlayer.Spark
app.CreateSparkMessagingIndexes();                            // MintPlayer.Spark.Messaging
app.UseSparkReplication();                                   // MintPlayer.Spark.Replication
app.SynchronizeSparkModelsIfRequested<FleetContext>(args);    // MintPlayer.Spark

// Endpoints
endpoints.MapSpark();                                        // MintPlayer.Spark
endpoints.MapSparkReplication();                              // MintPlayer.Spark.Replication
endpoints.MapSparkIdentityApi<SparkUser>();                   // MintPlayer.Spark.Authorization
```

### Desired State

```csharp
// Services -- single entry point
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddActions();              // calls source-generated AddSparkActions()
    spark.AddCustomActions();        // calls source-generated AddSparkCustomActions()
    spark.UseContext<FleetContext>();

    spark.AddAuthorization();        // optional module
    spark.AddAuthentication<SparkUser>(auth =>
    {
        // optional: auth.ConfigureIdentity(o => { ... });
        // optional: auth.AddGoogle(o => { ... });
    });

    spark.AddMessaging();            // auto-registers recipients if source-generated method exists

    spark.AddReplication(opt =>
    {
        opt.ModuleName = "Fleet";
        opt.ModuleUrl = "https://localhost:5003";
        // ...
    });
});

// Middleware -- single entry point
app.UseSpark(spark =>
{
    // Authorization module auto-adds antiforgery + authentication + authorization middleware
    // Core auto-adds XSRF cookie middleware + SparkMiddleware
    // Messaging auto-creates messaging indexes
    // Replication auto-runs UseSparkReplication()
    spark.SynchronizeModelsIfRequested<FleetContext>(args);
});

// Endpoints -- single entry point
app.MapSpark();
// Internally maps: core endpoints + replication endpoints + identity API endpoints
```

---

## Inventory of Extension Methods

### Service Registration (IServiceCollection)

| Method | Package | Generated? | Parameters | Notes |
|--------|---------|-----------|------------|-------|
| `AddSpark(IConfiguration)` | Core | No | `IConfiguration` | Binds `Spark` config section |
| `AddSpark(Action<SparkOptions>)` | Core | No | Lambda | Direct options config |
| `AddSparkActions()` | Source-generated | Yes | None | Registers `IPersistentObjectActions<T>` implementations |
| `AddSparkCustomActions()` | Source-generated | Yes | None | Registers `SparkCustomAction` implementations |
| `AddSparkRecipients()` | Source-generated | Yes | None | Registers `IRecipient<T>` implementations |
| `AddSparkSubscriptionWorkers()` | Source-generated | Yes | None | Registers `IHostedService` workers |
| `AddSparkAuthorization(Action<AuthorizationOptions>?)` | Authorization | No | Optional lambda | Registers permission services |
| `AddSparkAuthentication<TUser>(Action<IdentityOptions>?)` | Authorization | No | Optional lambda | Returns `IdentityBuilder` for chaining |
| `AddSparkMessaging(Action<SparkMessagingOptions>?)` | Messaging | No | Optional lambda | Registers message bus + hosted service |
| `AddSparkReplication(Action<SparkReplicationOptions>)` | Replication | No | Required lambda | Registers ETL + sync services |
| `AddSparkSubscriptions(Action<SparkSubscriptionOptions>?)` | SubscriptionWorker | No | Optional lambda | Registers subscription infrastructure |

### Middleware / App Configuration (IApplicationBuilder / WebApplication)

| Method | Package | Parameters | Notes |
|--------|---------|------------|-------|
| `UseSpark()` | Core | None | XSRF cookie + SparkMiddleware |
| `UseSparkAntiforgery()` | Authorization | None | Just calls `app.UseAntiforgery()` |
| `CreateSparkIndexes(Assembly?)` | Core | Optional assembly | Deploys RavenDB indexes |
| `CreateSparkMessagingIndexes()` | Messaging | None | Deploys `SparkMessages_ByQueue` index |
| `UseSparkReplication()` | Replication | None | Module registration + ETL deployment |
| `SynchronizeSparkModels<TContext>()` | Core | None | Syncs entity definitions to JSON |
| `SynchronizeSparkModelsIfRequested<TContext>(args)` | Core | `string[] args` | Conditional sync + exit |

### Endpoint Mapping (IEndpointRouteBuilder)

| Method | Package | Parameters | Notes |
|--------|---------|------------|-------|
| `MapSpark()` | Core | None | All core API endpoints |
| `MapSparkReplication()` | Replication | None | ETL deploy + sync endpoints |
| `MapSparkIdentityApi<TUser>()` | Authorization | None | Identity API under `/spark/auth` |

---

## Architecture Design

### Package Dependency Graph (current)

```
MintPlayer.Spark.Abstractions  (no deps)
MintPlayer.Spark.Messaging.Abstractions  (no deps)
MintPlayer.Spark.Replication.Abstractions  (no deps)
MintPlayer.Spark.SubscriptionWorker  (RavenDB.Client)

MintPlayer.Spark  --> Abstractions
MintPlayer.Spark.Authorization  --> Abstractions
MintPlayer.Spark.Messaging  --> Messaging.Abstractions, SubscriptionWorker
MintPlayer.Spark.Replication  --> Abstractions, Messaging.Abstractions, Replication.Abstractions, SubscriptionWorker
```

### Key Design Challenge

The builder object in `AddSpark(spark => { ... })` needs methods like `spark.AddAuthorization()` and `spark.AddReplication()`, but the Core package does **not** reference Authorization or Replication. This must be solved without creating circular dependencies.

### Proposed Solution: Extension Methods on the Builder

1. **Core package** defines `ISparkBuilder` interface and `SparkBuilder` class:

```csharp
// In MintPlayer.Spark
namespace MintPlayer.Spark;

public interface ISparkBuilder
{
    IServiceCollection Services { get; }
    IConfiguration? Configuration { get; }
    SparkOptions Options { get; }
}

public class SparkBuilder : ISparkBuilder
{
    public IServiceCollection Services { get; }
    public IConfiguration? Configuration { get; }
    public SparkOptions Options { get; } = new();

    internal bool HasAuthorization { get; set; }
    internal bool HasMessaging { get; set; }
    internal bool HasReplication { get; set; }
    internal Type? UserType { get; set; }

    public SparkBuilder(IServiceCollection services, IConfiguration? configuration)
    {
        Services = services;
        Configuration = configuration;
    }
}
```

2. **Each optional package** adds extension methods on `ISparkBuilder`:

```csharp
// In MintPlayer.Spark.Authorization
namespace MintPlayer.Spark.Authorization;

public static class SparkBuilderAuthorizationExtensions
{
    public static ISparkBuilder AddAuthorization(this ISparkBuilder builder, Action<AuthorizationOptions>? configure = null)
    {
        builder.Services.AddSparkAuthorization(configure);
        if (builder is SparkBuilder sb) sb.HasAuthorization = true;
        return builder;
    }

    public static IdentityBuilder AddAuthentication<TUser>(this ISparkBuilder builder, Action<IdentityOptions>? configure = null)
        where TUser : SparkUser, new()
    {
        if (builder is SparkBuilder sb) sb.UserType = typeof(TUser);
        return builder.Services.AddSparkAuthentication<TUser>(configure);
    }
}
```

3. **Similarly for middleware**, define `ISparkAppBuilder` / `SparkAppBuilder`:

```csharp
// In MintPlayer.Spark
public interface ISparkAppBuilder
{
    IApplicationBuilder App { get; }
}
```

4. **Source-generated methods** are called internally. The source generator should also generate builder extension methods, or the builder can call them via reflection/convention.

### Source-Generated Method Discovery

The source-generated `AddSparkActions()`, `AddSparkCustomActions()`, `AddSparkRecipients()` are `internal static` methods in the consuming project's namespace. The builder (in the Core package) cannot directly call them.

**Options:**

A. **Source generator also generates a builder registration call** -- The source generator emits an additional method that plugs into the builder. The consuming app still needs to call at least one method.

B. **The builder calls them via reflection** -- At the end of `AddSpark()`, scan the entry assembly for methods matching the convention `AddSpark*` on classes named `Spark*Extensions`.

C. **Keep source-generated calls explicit** -- The consuming app calls `spark.AddActions()` which is a thin wrapper the source generator provides. The source generator would generate an extension on `ISparkBuilder`:

```csharp
// Source-generated in consuming project
internal static class SparkActionsBuilderExtensions
{
    internal static ISparkBuilder AddActions(this ISparkBuilder builder)
    {
        SparkActionsExtensions.AddSparkActions(builder.Services);
        return builder;
    }
}
```

**Recommended: Option C** -- explicit but clean, no reflection magic, type-safe.

**However**, this requires changes to the source generator to also emit builder extension methods.

### Handling `MapSpark()` Consolidation

The `MapSpark()` method in the builder should automatically map all registered module endpoints. This requires knowing at middleware time which modules were registered at service time.

**Approach:** Store a `SparkModuleRegistry` in DI that tracks which modules are active:

```csharp
public class SparkModuleRegistry
{
    public bool HasAuthorization { get; set; }
    public bool HasReplication { get; set; }
    public Type? IdentityUserType { get; set; }
    // etc.
}
```

Populated during `AddSpark()`, consumed during `UseSpark()` and `MapSpark()`.

For `MapSparkIdentityApi<TUser>()` which requires a generic type parameter, use `MakeGenericMethod` to invoke it when the user type is stored in the registry.

---

## Implementation Plan

### Phase 1: Core Builder Infrastructure

**Files to create/modify:**

1. **Create** `MintPlayer.Spark/Builder/ISparkBuilder.cs` -- Builder interface
2. **Create** `MintPlayer.Spark/Builder/SparkBuilder.cs` -- Builder implementation
3. **Create** `MintPlayer.Spark/Builder/ISparkAppBuilder.cs` -- App builder interface
4. **Create** `MintPlayer.Spark/Builder/SparkAppBuilder.cs` -- App builder implementation
5. **Create** `MintPlayer.Spark/Builder/SparkModuleRegistry.cs` -- Module registry (singleton in DI)
6. **Modify** `MintPlayer.Spark/SparkMiddleware.cs` -- Add new `AddSpark` and `UseSpark` overloads that accept builder lambdas
7. **Modify** `MintPlayer.Spark/Configuration/SparkOptions.cs` -- No changes needed (reuse as-is)

### Phase 2: Module Builder Extensions

8. **Create** `MintPlayer.Spark.Authorization/Extensions/SparkBuilderExtensions.cs` -- `AddAuthorization()`, `AddAuthentication<TUser>()` on `ISparkBuilder`; `UseAuthorization()` on `ISparkAppBuilder`
9. **Create** `MintPlayer.Spark.Messaging/SparkBuilderExtensions.cs` -- `AddMessaging()` on `ISparkBuilder`
10. **Create** `MintPlayer.Spark.Replication/Extensions/SparkBuilderExtensions.cs` -- `AddReplication()` on `ISparkBuilder`; `UseReplication()` on `ISparkAppBuilder`

### Phase 3: Source Generator Updates

11. **Modify** `MintPlayer.Spark.SourceGenerators/Generators/ActionsRegistrationGenerator.Producer.cs` -- Also emit `ISparkBuilder.AddActions()` extension
12. **Modify** `MintPlayer.Spark.SourceGenerators/Generators/CustomActionsRegistrationGenerator.Producer.cs` -- Also emit `ISparkBuilder.AddCustomActions()` extension
13. **Modify** `MintPlayer.Spark.SourceGenerators/Generators/RecipientRegistrationGenerator.Producer.cs` -- Also emit `ISparkBuilder.AddRecipients()` extension
14. **Modify** `MintPlayer.Spark.SourceGenerators/Generators/SubscriptionWorkerRegistrationGenerator.Producer.cs` -- Also emit `ISparkBuilder.AddSubscriptionWorkers()` extension

### Phase 4: Consolidated Endpoint Mapping

15. **Modify** `MintPlayer.Spark/SparkMiddleware.cs` -- New `MapSpark()` overload on `WebApplication`/`IEndpointRouteBuilder` that reads `SparkModuleRegistry` to auto-map all registered module endpoints

### Phase 5: Update Demo Apps

16. **Modify** `Demo/DemoApp/DemoApp/Program.cs`
17. **Modify** `Demo/Fleet/Fleet/Program.cs`
18. **Modify** `Demo/HR/HR/Program.cs`

---

## Detailed API Surface

### Service Registration

```csharp
// Minimal (DemoApp-like)
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddActions();
    spark.UseContext<DemoSparkContext>();
    spark.AddMessaging();
});

// Full (Fleet-like)
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddActions();
    spark.AddCustomActions();
    spark.UseContext<FleetContext>();

    spark.AddAuthorization();
    spark.AddAuthentication<SparkUser>();
    spark.ConfigureApplicationCookie(o => o.Cookie.Name = ".SparkAuth.Fleet");

    spark.AddMessaging();

    spark.AddReplication(opt =>
    {
        opt.ModuleName = "Fleet";
        opt.ModuleUrl = "https://localhost:5003";
        opt.SparkModulesUrls = ["http://localhost:8080"];
        opt.SparkModulesDatabase = "SparkModules";
        opt.AssembliesToScan = [typeof(Fleet.Replicated.Person).Assembly];
    });
});
```

### Middleware

```csharp
app.UseSpark(spark =>
{
    spark.SynchronizeModelsIfRequested<FleetContext>(args);
});
// Internally:
//   if HasAuthorization -> app.UseAntiforgery()
//   app.UseSpark() (XSRF cookie + SparkMiddleware)
//   app.CreateSparkIndexes()
//   if HasMessaging -> app.CreateSparkMessagingIndexes()
//   if HasReplication -> app.UseSparkReplication()
//   SynchronizeModelsIfRequested if requested
```

### Endpoint Mapping

```csharp
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
    // Internally also maps replication + identity if registered
});

// OR, if we add a WebApplication extension:
app.MapSpark();
```

---

## Edge Cases & Considerations

### 1. `AddSparkAuthentication` returns `IdentityBuilder`
The current API returns `IdentityBuilder` so consumers can chain `.AddGoogle()`, `.AddMicrosoftAccount()`, etc. The builder pattern must preserve this:

```csharp
spark.AddAuthentication<SparkUser>(auth =>
{
    auth.ConfigureIdentity(o => { ... });
    auth.AddExternalLogin(identity => identity
        .AddGoogle(o => { ... })
        .AddMicrosoftAccount(o => { ... }));
});
```

Or alternatively, return the `IdentityBuilder` from `AddAuthentication`:
```csharp
spark.AddAuthentication<SparkUser>()
    .AddGoogle(o => { ... });
```

### 2. `ConfigureApplicationCookie` is standard ASP.NET Identity
This is not a Spark method. The builder could offer a convenience wrapper or the consumer keeps calling it directly on `builder.Services`.

### 3. Ordering of Middleware
The builder controls ordering internally, preventing mistakes. The `UseSpark()` builder ensures:
- `UseAntiforgery()` before Spark middleware (if auth is registered)
- `CreateSparkIndexes()` always runs
- `CreateSparkMessagingIndexes()` only if messaging is registered
- `UseSparkReplication()` only if replication is registered

### 4. `SynchronizeSparkModelsIfRequested` needs generic `TContext` and `args`
This cannot be fully automated since it needs the context type and command-line args. It stays as an explicit call on the app builder.

### 5. `AddSpark(IConfiguration)` overload
Keep the `IConfiguration` parameter for binding the `Spark` config section. The builder lambda is optional:
```csharp
builder.Services.AddSpark(builder.Configuration, spark => { ... });  // with config binding
builder.Services.AddSpark(spark => { ... });                         // no config binding, manual options
```

---

## Testing Strategy

1. **Unit tests**: Verify `SparkModuleRegistry` is correctly populated for each `Add*` call
2. **Integration tests**: Verify all three demo apps start correctly with the new builder API
3. **Source generator tests**: Verify the generators emit valid builder extension methods

---

## Success Criteria

- Fleet `Program.cs` Spark-related code reduces from ~15 lines to ~3 blocks (AddSpark, UseSpark, MapSpark)
- No change in runtime behavior
- All demo apps compile and run correctly
- Optional modules (Authorization, Messaging, Replication) remain truly optional -- apps that don't reference them don't get their methods in IntelliSense
- Old standalone methods are removed -- the builder is the only entry point
