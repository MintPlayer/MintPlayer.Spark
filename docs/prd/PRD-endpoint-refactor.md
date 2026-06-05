# PRD: Refactor Endpoints from Static to DI-Instantiated Instance Classes

## Problem Statement

Spark endpoints are currently split into two distinct patterns:

1. **MintPlayer.Spark core endpoints** (22 classes) — already instance classes with `[Register(ServiceLifetime.Scoped)]` and `[Inject]`, resolved via ASP.NET DI parameter binding in `MapSpark()`
2. **MintPlayer.Spark.Authorization endpoints** (3 classes) — `static` classes with static `Handle` methods, wired as `(Delegate)ClassName.Handle`
3. **MintPlayer.Spark.Replication endpoints** (2 classes) — `static` classes with static `Handle*Async` methods, resolving services manually from `context.RequestServices`

The static endpoints cannot participate in DI, making them harder to test, extend, and maintain. The goal is to unify all endpoints on a single pattern: **scoped instance classes instantiated through the service provider**.

Additionally, the route wiring in `MapSpark()` is a monolithic 80-line method that hard-codes every route. This should be refactored to let each endpoint declare its own route metadata.

The generic endpoint infrastructure (interface, discovery, `ActivatorUtilities`-based instantiation) should live in a **new reusable library `MintPlayer.AspNetCore.Endpoints`** so it can be used in projects beyond Spark.

## Current State Analysis

### Pattern A: Core Endpoints (MintPlayer.Spark) — Already Instance-Based

22 endpoint classes, all following this pattern:

```csharp
[Register(ServiceLifetime.Scoped)]
public sealed partial class CreatePersistentObject
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IValidationService validationService;
    // ...

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId) { ... }
}
```

These are resolved by ASP.NET's DI parameter binding in the lambda:
```csharp
persistentObjectGroup.MapPost("/{objectTypeId}",
    async (HttpContext context, string objectTypeId, CreatePersistentObject action) =>
        await action.HandleAsync(context, objectTypeId));
```

**Files (22):**
| File | Class | Visibility |
|------|-------|-----------|
| `Endpoints/EntityTypes/List.cs` | `ListEntityTypes` | public |
| `Endpoints/EntityTypes/Get.cs` | `GetEntityType` | public |
| `Endpoints/Queries/List.cs` | `ListQueries` | public |
| `Endpoints/Queries/Get.cs` | `GetQuery` | public |
| `Endpoints/Queries/Execute.cs` | `ExecuteQuery` | public |
| `Endpoints/Queries/StreamExecuteQuery.cs` | `StreamExecuteQuery` | internal |
| `Endpoints/Culture/Get.cs` | `GetCulture` | public |
| `Endpoints/Translations/Get.cs` | `GetTranslations` | public |
| `Endpoints/ProgramUnits/Get.cs` | `GetProgramUnits` | public |
| `Endpoints/Permissions/GetPermissions.cs` | `GetPermissions` | public |
| `Endpoints/Aliases/GetAliases.cs` | `GetAliases` | public |
| `Endpoints/PersistentObject/List.cs` | `ListPersistentObjects` | public |
| `Endpoints/PersistentObject/Get.cs` | `GetPersistentObject` | public |
| `Endpoints/PersistentObject/Create.cs` | `CreatePersistentObject` | public |
| `Endpoints/PersistentObject/Update.cs` | `UpdatePersistentObject` | public |
| `Endpoints/PersistentObject/Delete.cs` | `DeletePersistentObject` | public |
| `Endpoints/Actions/ListCustomActions.cs` | `ListCustomActions` | internal |
| `Endpoints/Actions/ExecuteCustomAction.cs` | `ExecuteCustomAction` | internal |
| `Endpoints/LookupReferences/List.cs` | `ListLookupReferences` | public |
| `Endpoints/LookupReferences/Get.cs` | `GetLookupReference` | public |
| `Endpoints/LookupReferences/AddValue.cs` | `AddLookupReferenceValue` | public |
| `Endpoints/LookupReferences/UpdateValue.cs` | `UpdateLookupReferenceValue` | public |
| `Endpoints/LookupReferences/DeleteValue.cs` | `DeleteLookupReferenceValue` | public |

### Pattern B: Authorization Endpoints (MintPlayer.Spark.Authorization) — Static

3 endpoint classes, all static:

```csharp
internal static class GetCurrentUser
{
    public static IResult Handle(HttpContext httpContext) { ... }
}

internal static class Logout
{
    public static async Task<IResult> Handle(HttpContext httpContext) { ... }
}

internal static class CsrfRefresh
{
    public static IResult Handle() => Results.Ok();
}
```

Wired as delegates:
```csharp
authGroup.MapGet("/me", GetCurrentUser.Handle);
authGroup.MapPost("/logout", (Delegate)Logout.Handle);
authGroup.MapPost("/csrf-refresh", CsrfRefresh.Handle);
```

### Pattern C: Replication Endpoints (MintPlayer.Spark.Replication) — Static with Manual Service Resolution

2 endpoint classes, static with manual `context.RequestServices.GetRequiredService<T>()`:

```csharp
internal static class EtlEndpoints
{
    public static async Task<IResult> HandleDeployAsync(HttpContext context)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<EtlTaskManager>>();
        var etlTaskManager = context.RequestServices.GetRequiredService<EtlTaskManager>();
        // ...
    }
}
```

Wired as delegates:
```csharp
endpoints.MapPost("/spark/etl/deploy", (Delegate)EtlEndpoints.HandleDeployAsync);
endpoints.MapPost("/spark/sync/apply", (Delegate)SyncEndpoints.HandleApplyAsync);
```

### Route Wiring: `SparkExtensions.MapSpark()`

All core routes are hard-coded in a single monolithic method (`SparkMiddleware.cs:229-315`). Module endpoints are deferred via `SparkModuleRegistry.MapEndpoints()` which invokes registered `Action<IEndpointRouteBuilder>` callbacks.

## Proposed Design

### 1. New Library: `MintPlayer.AspNetCore.Endpoints`

A standalone, reusable NuGet package containing the generic endpoint infrastructure. This library has **no dependency on Spark** — it only depends on `Microsoft.AspNetCore.Routing` and related ASP.NET Core abstractions. It can be used in any ASP.NET Core project.

**Package:** `MintPlayer.AspNetCore.Endpoints`
**Location:** New project at solution root (sibling to `MintPlayer.Spark`, `MintPlayer.Spark.Abstractions`, etc.)

#### `IEndpoint` Interface

```csharp
namespace MintPlayer.AspNetCore.Endpoints;

/// <summary>
/// Contract for endpoint classes that are instantiated per-request via DI
/// using ActivatorUtilities.CreateInstance.
/// Implementing classes declare their route(s) via the static MapRoutes method.
/// </summary>
public interface IEndpoint
{
    /// <summary>
    /// Maps this endpoint's routes onto the given route builder.
    /// Called once at startup. Implementations should use the provided
    /// EndpointRouteBuilderExtensions to wire up handlers that resolve
    /// the endpoint instance per-request.
    /// </summary>
    static abstract void MapRoutes(IEndpointRouteBuilder routes);
}
```

#### `EndpointRouteBuilderExtensions` — Per-Request Instantiation via `ActivatorUtilities`

Generic extension methods that create endpoint instances per-request using `ActivatorUtilities.CreateInstance`:

```csharp
namespace MintPlayer.AspNetCore.Endpoints;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps all IEndpoint implementations from the given types onto a route group.
    /// Each endpoint's static MapRoutes method is invoked with the group.
    /// </summary>
    public static RouteGroupBuilder MapEndpoints(
        this IEndpointRouteBuilder routes,
        string groupPrefix,
        params Type[] endpointTypes)
    {
        var group = routes.MapGroup(groupPrefix);
        foreach (var type in endpointTypes)
        {
            var method = type.GetMethod(
                nameof(IEndpoint.MapRoutes),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(IEndpointRouteBuilder)]);
            method?.Invoke(null, [group]);
        }
        return group;
    }

    /// <summary>
    /// Maps all IEndpoint implementations found in the given assemblies.
    /// Discovers types implementing IEndpoint and calls MapRoutes on each.
    /// </summary>
    public static IEndpointRouteBuilder MapEndpoints(
        this IEndpointRouteBuilder routes,
        string groupPrefix,
        params Assembly[] assemblies)
    {
        var group = routes.MapGroup(groupPrefix);
        var endpointTypes = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IEndpoint).IsAssignableFrom(t));

        foreach (var type in endpointTypes)
        {
            var method = type.GetMethod(
                nameof(IEndpoint.MapRoutes),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(IEndpointRouteBuilder)]);
            method?.Invoke(null, [group]);
        }
        return routes;
    }

    /// <summary>
    /// Creates a scoped endpoint instance using ActivatorUtilities.CreateInstance.
    /// Use this in MapRoutes implementations when you want explicit per-request
    /// instantiation rather than relying on ASP.NET DI parameter binding.
    /// </summary>
    public static TEndpoint CreateEndpoint<TEndpoint>(this HttpContext context)
        where TEndpoint : class, IEndpoint
    {
        return ActivatorUtilities.CreateInstance<TEndpoint>(context.RequestServices);
    }
}
```

This gives endpoint authors two choices for how to wire their handler:

**Option A — ASP.NET DI parameter binding (current Spark core pattern):**
```csharp
public static void MapRoutes(IEndpointRouteBuilder routes)
{
    routes.MapGet("/{id}",
        async (HttpContext context, string id, GetPersistentObject endpoint) =>
            await endpoint.HandleAsync(context, id));
}
```

**Option B — Explicit `ActivatorUtilities.CreateInstance`:**
```csharp
public static void MapRoutes(IEndpointRouteBuilder routes)
{
    routes.MapGet("/{id}", async (HttpContext context, string id) =>
    {
        var endpoint = context.CreateEndpoint<GetPersistentObject>();
        await endpoint.HandleAsync(context, id);
    });
}
```

Option B is useful when:
- The endpoint class is `internal` (ASP.NET parameter binding requires public types)
- You want to avoid the endpoint appearing as a bindable parameter
- You need to pass additional constructor arguments beyond what's in DI

### 2. Spark Packages Reference `MintPlayer.AspNetCore.Endpoints`

The dependency chain:

```
MintPlayer.AspNetCore.Endpoints          (new, standalone)
  ├── Microsoft.AspNetCore.Routing.Abstractions
  └── Microsoft.Extensions.DependencyInjection.Abstractions

MintPlayer.Spark.Abstractions            (existing, no change needed)

MintPlayer.Spark                         (references MintPlayer.AspNetCore.Endpoints)
MintPlayer.Spark.Authorization           (references MintPlayer.AspNetCore.Endpoints)
MintPlayer.Spark.Replication             (references MintPlayer.AspNetCore.Endpoints)
```

`ISparkEndpoint` is **not needed** — all Spark endpoint classes directly implement `IEndpoint` from the shared library.

### 3. Convert All Endpoints to Instance Classes Implementing `IEndpoint`

#### Core endpoints (already instance-based) — add `IEndpoint` and `MapRoutes`

Example for `CreatePersistentObject`:

```csharp
using MintPlayer.AspNetCore.Endpoints;

[Register(ServiceLifetime.Scoped)]
public sealed partial class CreatePersistentObject : IEndpoint
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IValidationService validationService;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;

    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/{objectTypeId}",
            async (HttpContext context, string objectTypeId, CreatePersistentObject action) =>
                await action.HandleAsync(context, objectTypeId))
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId) { /* unchanged */ }
}
```

#### Authorization endpoints — convert from static to instance

Example for `GetCurrentUser`:

```csharp
using MintPlayer.AspNetCore.Endpoints;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class GetCurrentUser : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/me", async (HttpContext context) =>
        {
            var endpoint = context.CreateEndpoint<GetCurrentUser>();
            return endpoint.Handle(context);
        });
    }

    public IResult Handle(HttpContext httpContext)
    {
        // Same logic, now an instance method
    }
}
```

Note: Uses `CreateEndpoint<T>()` because the class is `internal` and can't be used as a minimal API parameter.

Example for `Logout`:

```csharp
using MintPlayer.AspNetCore.Endpoints;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class Logout : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/logout", async (HttpContext context) =>
        {
            var endpoint = context.CreateEndpoint<Logout>();
            return await endpoint.Handle(context);
        })
        .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    public async Task<IResult> Handle(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.Ok();
    }
}
```

#### Replication endpoints — convert from static to instance with injected services

Example for `EtlEndpoints` → `EtlDeploy`:

```csharp
using MintPlayer.AspNetCore.Endpoints;

[Register(ServiceLifetime.Scoped)]
internal sealed partial class EtlDeploy : IEndpoint
{
    [Inject] private readonly ILogger<EtlDeploy> logger;
    [Inject] private readonly EtlTaskManager etlTaskManager;

    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/deploy", async (HttpContext context) =>
        {
            var endpoint = context.CreateEndpoint<EtlDeploy>();
            return await endpoint.HandleAsync(context);
        });
    }

    public async Task<IResult> HandleAsync(HttpContext context) { /* same logic, using injected fields */ }
}
```

### 4. Refactor `MapSpark()` to Use `MapEndpoints()`

Replace the monolithic route wiring with calls to the library's `MapEndpoints()`:

```csharp
public static IEndpointRouteBuilder MapSpark(this IEndpointRouteBuilder endpoints)
{
    var registry = endpoints.ServiceProvider.GetRequiredService<SparkModuleRegistry>();

    var sparkGroup = endpoints.MapGroup("/spark");
    sparkGroup.MapGet("/", async context =>
    {
        await context.Response.WriteAsync("Spark Middleware is active!");
    });

    // Entity Types
    sparkGroup.MapEndpoints("/types",
        typeof(ListEntityTypes), typeof(GetEntityType));

    // Queries
    sparkGroup.MapEndpoints("/queries",
        typeof(ListQueries), typeof(GetQuery), typeof(ExecuteQuery), typeof(StreamExecuteQuery));

    // Single endpoints (no sub-group needed — map directly on sparkGroup)
    GetCulture.MapRoutes(sparkGroup);
    GetTranslations.MapRoutes(sparkGroup);
    GetProgramUnits.MapRoutes(sparkGroup);
    GetPermissions.MapRoutes(sparkGroup);
    GetAliases.MapRoutes(sparkGroup);

    // Persistent Objects
    sparkGroup.MapEndpoints("/po",
        typeof(ListPersistentObjects), typeof(GetPersistentObject),
        typeof(CreatePersistentObject), typeof(UpdatePersistentObject),
        typeof(DeletePersistentObject));

    // Custom Actions
    sparkGroup.MapEndpoints("/actions",
        typeof(ListCustomActions), typeof(ExecuteCustomAction));

    // Lookup References
    sparkGroup.MapEndpoints("/lookupref",
        typeof(ListLookupReferences), typeof(GetLookupReference),
        typeof(AddLookupReferenceValue), typeof(UpdateLookupReferenceValue),
        typeof(DeleteLookupReferenceValue));

    // Map module-specific endpoints (authorization, replication, etc.)
    registry.MapEndpoints(endpoints);

    return endpoints;
}
```

### 5. Update `SparkModuleRegistry` for Module Endpoint Registration

Modules (Authorization, Replication) register their endpoint types instead of `Action<IEndpointRouteBuilder>` callbacks:

```csharp
public class SparkModuleRegistry
{
    public Type? IdentityUserType { get; set; }

    private readonly List<Action<IApplicationBuilder>> middlewareActions = [];
    private readonly List<Action<IEndpointRouteBuilder>> endpointActions = [];
    private readonly List<(string GroupPrefix, Type[] EndpointTypes)> endpointGroups = [];

    // Existing callback-based registration (keep for backward compat during migration)
    public void AddEndpoints(Action<IEndpointRouteBuilder> action) => endpointActions.Add(action);

    // New type-based registration using MintPlayer.AspNetCore.Endpoints
    public void AddEndpointGroup(string groupPrefix, params Type[] endpointTypes)
        => endpointGroups.Add((groupPrefix, endpointTypes));

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var action in endpointActions)
            action(endpoints);

        foreach (var (prefix, types) in endpointGroups)
            endpoints.MapEndpoints(prefix, types);
    }
}
```

## Implementation Plan

### Phase 1: Create `MintPlayer.AspNetCore.Endpoints` Library
1. Create new project `MintPlayer.AspNetCore.Endpoints` at solution root
2. Target same TFM as the rest of the solution (net10.0)
3. Add minimal dependencies: `Microsoft.AspNetCore.Routing.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`
4. Implement `IEndpoint` interface
5. Implement `EndpointRouteBuilderExtensions` (`MapEndpoints` overloads, `CreateEndpoint<T>`)
6. Configure NuGet package metadata for publishing

### Phase 2: Add `IEndpoint` + `MapRoutes` to Core Endpoints (Non-Breaking)
1. Add project reference from `MintPlayer.Spark` to `MintPlayer.AspNetCore.Endpoints`
2. For each of the 22 core endpoint classes, add `: IEndpoint` and `static MapRoutes`
3. Move the route definition from the monolithic `MapSpark()` into each endpoint's `MapRoutes()`
4. Update `MapSpark()` to use `MapEndpoints()` from the library instead of hard-coding routes
5. Verify all routes still function identically

### Phase 3: Convert Authorization Endpoints to Instance Classes
1. Add project reference from `MintPlayer.Spark.Authorization` to `MintPlayer.AspNetCore.Endpoints`
2. Convert `GetCurrentUser` from `static class` to `[Register(ServiceLifetime.Scoped)] partial class : IEndpoint`
3. Convert `Logout` from `static class` to `[Register(ServiceLifetime.Scoped)] partial class : IEndpoint`
4. Convert `CsrfRefresh` from `static class` to `[Register(ServiceLifetime.Scoped)] partial class : IEndpoint`
5. Use `context.CreateEndpoint<T>()` in `MapRoutes` (since these are `internal`)
6. Update `SparkAuthenticationExtensions.MapSparkIdentityApi<TUser>()` to use `MapEndpoints()`

### Phase 4: Convert Replication Endpoints to Instance Classes
1. Add project reference from `MintPlayer.Spark.Replication` to `MintPlayer.AspNetCore.Endpoints`
2. Split `EtlEndpoints` static class into `EtlDeploy : IEndpoint` instance endpoint
3. Split `SyncEndpoints` static class into `SyncApply : IEndpoint` instance endpoint
4. Replace manual `context.RequestServices.GetRequiredService<T>()` with `[Inject]` fields
5. Use `context.CreateEndpoint<T>()` in `MapRoutes`
6. Update `SparkReplicationExtensions.MapSparkReplication()` to use `MapEndpoints()`

### Phase 5: Cleanup & Module Registry Update
1. Remove the monolithic route definitions from `MapSpark()`
2. Update `SparkModuleRegistry` to add `AddEndpointGroup()` method
3. Migrate module registrations from `AddEndpoints(Action<>)` to `AddEndpointGroup(prefix, types)`
4. Remove `ISparkEndpoint` if it was ever introduced — `IEndpoint` from the shared library is the only interface
5. Verify no dead code remains

## Library API Surface: `MintPlayer.AspNetCore.Endpoints`

| Type | Description |
|------|-------------|
| `IEndpoint` | Interface with `static abstract void MapRoutes(IEndpointRouteBuilder)` |
| `EndpointRouteBuilderExtensions.MapEndpoints(string, params Type[])` | Maps endpoints by type list onto a group |
| `EndpointRouteBuilderExtensions.MapEndpoints(string, params Assembly[])` | Auto-discovers `IEndpoint` implementations from assemblies |
| `EndpointRouteBuilderExtensions.CreateEndpoint<T>(HttpContext)` | Creates endpoint instance via `ActivatorUtilities.CreateInstance` |

## Risks and Considerations

### Static Abstract Interface Members
- Requires C# 11 / .NET 7+. The project already targets .NET 10, so this is fine.
- The `[Register]` source generator handles DI registration. The `[Inject]` source generator handles constructor generation. Both work with `partial class` — no changes needed to source generators.

### `ActivatorUtilities.CreateInstance` vs ASP.NET DI Parameter Binding
- The core endpoints currently rely on ASP.NET's parameter binding (the endpoint class appears as a lambda parameter). This still works and is the simplest approach for `public` endpoint classes.
- `ActivatorUtilities.CreateInstance` (via `CreateEndpoint<T>()`) is needed for `internal` endpoints that can't be bound as lambda parameters. It creates a new instance per call, injecting constructor parameters from the service provider.
- Both approaches create a scoped instance per request. The library supports both — endpoint authors choose which fits their use case.

### Reusability Outside Spark
- `MintPlayer.AspNetCore.Endpoints` has zero Spark dependencies. It only depends on ASP.NET Core routing and DI abstractions.
- Any ASP.NET Core project can reference it and implement `IEndpoint` for self-registering, DI-instantiated endpoints.
- The `MapEndpoints(assemblies)` overload enables convention-based auto-discovery in non-Spark projects.

### Identity API Endpoints
- `authGroup.MapIdentityApi<TUser>()` is a framework call that maps ASP.NET Identity's built-in endpoints. This cannot be converted to the instance pattern — it stays as-is.

### Breaking Changes
- No breaking changes to external consumers. All changes are internal to the Spark packages.
- The `SparkModuleRegistry.AddEndpoints()` callback API can be preserved alongside the new type-based API for a smooth migration.

## Success Criteria
1. `MintPlayer.AspNetCore.Endpoints` is a standalone NuGet package with no Spark dependencies
2. All endpoint classes across all Spark packages are non-static instance classes implementing `IEndpoint`
3. All endpoint classes use `[Inject]` for DI (constructor generated by source generator)
4. All endpoint classes implement `IEndpoint` with self-contained `MapRoutes` definitions
5. The monolithic `MapSpark()` method delegates to `MapEndpoints()` calls from the library
6. No manual `context.RequestServices.GetRequiredService<T>()` calls in any endpoint
7. All existing routes, HTTP methods, and antiforgery metadata are preserved
8. All existing tests pass without modification
