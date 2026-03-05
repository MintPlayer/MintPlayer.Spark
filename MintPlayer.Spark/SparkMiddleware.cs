using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Converters;
using MintPlayer.Spark.Endpoints.Actions;
using MintPlayer.Spark.Endpoints.Aliases;
using MintPlayer.Spark.Endpoints.Culture;
using MintPlayer.Spark.Endpoints.EntityTypes;
using MintPlayer.Spark.Endpoints.Translations;
using MintPlayer.Spark.Endpoints.LookupReferences;
using MintPlayer.Spark.Endpoints.Permissions;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.ProgramUnits;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Json.Serialization.NewtonsoftJson;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark;

public static class SparkExtensions
{
    public static IServiceCollection AddSpark(this IServiceCollection services, IConfiguration configuration, Action<ISparkBuilder> configure)
    {
        var builder = new SparkBuilder(services, configuration);
        configuration.GetSection("Spark").Bind(builder.Options);
        return services.AddSparkCore(builder, configure);
    }

    public static IServiceCollection AddSpark(this IServiceCollection services, Action<ISparkBuilder> configure)
    {
        var builder = new SparkBuilder(services);
        return services.AddSparkCore(builder, configure);
    }

    private static IServiceCollection AddSparkCore(this IServiceCollection services, SparkBuilder builder, Action<ISparkBuilder> configure)
    {
        var options = builder.Options;

        // Register antiforgery (required by Spark's POST/PUT/DELETE endpoints)
        services.AddAntiforgery(opt => opt.HeaderName = "X-XSRF-TOKEN");

        // Ensure HttpContextAccessor is available (needed for RequestCultureResolver)
        services.AddHttpContextAccessor();

        // Register the Spark services
        services.AddSparkServices();

        services.AddSingleton<IDocumentStore>(sp =>
        {
            var store = new DocumentStore
            {
                Urls = options.RavenDb.Urls,
                Database = options.RavenDb.Database,
            };

            // Use GUID-based document IDs instead of HiLo
            store.Conventions.AsyncDocumentIdGenerator = (dbName, entity) =>
            {
                var collectionName = store.Conventions.GetCollectionName(entity.GetType());
                return Task.FromResult($"{collectionName}/{Guid.NewGuid()}");
            };

            // Register custom JSON converters for RavenDB document serialization
            store.Conventions.Serialization = new NewtonsoftJsonSerializationConventions
            {
                CustomizeJsonSerializer = serializer =>
                {
                    serializer.Converters.Add(new ColorNewtonsoftJsonConverter());
                }
            };

            store.Initialize();

            var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
            if (hostEnvironment.IsDevelopment())
            {
                var databaseNames = store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, int.MaxValue));
                if (!databaseNames.Contains(options.RavenDb.Database))
                {
                    store.Maintenance.Server.Send(new CreateDatabaseOperation(o =>
                        o.Regular(options.RavenDb.Database).WithReplicationFactor(1)
                    ));
                }
            }

            return store;
        });

        // Let modules register their services
        configure(builder);

        // Store the registry in DI so UseSpark/MapSpark can access it
        services.AddSingleton(builder.Registry);

        return services;
    }

    /// <summary>
    /// Registers the SparkContext implementation for this application.
    /// </summary>
    public static ISparkBuilder UseContext<TContext>(this ISparkBuilder builder)
        where TContext : SparkContext
    {
        builder.Services.AddScoped<SparkContext, TContext>();
        return builder;
    }

    /// <summary>
    /// Registers entity-specific Actions class for customizing CRUD behavior.
    /// Used internally by the source generator.
    /// </summary>
    public static IServiceCollection AddSparkActions<TActions, TEntity>(this IServiceCollection services)
        where TActions : class, IPersistentObjectActions<TEntity>
        where TEntity : class
    {
        services.AddScoped<IPersistentObjectActions<TEntity>, TActions>();
        services.AddScoped<TActions>();
        return services;
    }

    /// <summary>
    /// Configures Spark middleware, indexes, and all registered module middleware.
    /// Call after UseRouting(). Do NOT call UseAuthentication/UseAuthorization/UseAntiforgery separately
    /// when using this method — they are added automatically if authentication is configured.
    /// </summary>
    public static IApplicationBuilder UseSpark(this IApplicationBuilder app)
    {
        var registry = app.ApplicationServices.GetRequiredService<SparkModuleRegistry>();

        // If authentication is registered, add auth middleware
        if (registry.IdentityUserType != null)
        {
            app.UseAuthentication();
        }

        // Always add authorization and antiforgery
        app.UseAuthorization();
        app.UseAntiforgery();

        app.UseWebSockets();

        // Generate XSRF-TOKEN cookie on each response for Angular's HttpClient
        app.Use(async (context, next) =>
        {
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            var tokens = antiforgery.GetAndStoreTokens(context);
            if (tokens.RequestToken != null)
            {
                context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken, new CookieOptions
                {
                    HttpOnly = false,
                    SameSite = SameSiteMode.Strict,
                    Path = "/"
                });
            }
            await next(context);
        });

        app.UseMiddleware<SparkMiddleware>();

        // Create RavenDB indexes
        CreateSparkIndexes(app);

        // Run module-specific middleware/startup tasks
        registry.ApplyMiddleware(app);

        return app;
    }

    /// <summary>
    /// Synchronizes entity definitions between SparkContext and App_Data/Model/*.json files.
    /// Call this during development to generate or update model files based on your SparkContext properties.
    /// </summary>
    public static IApplicationBuilder SynchronizeSparkModels<TContext>(this IApplicationBuilder app)
        where TContext : SparkContext, new()
    {
        var hostEnvironment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();

        if (!hostEnvironment.IsDevelopment())
        {
            Console.WriteLine("Model synchronization is only available in Development mode.");
            return app;
        }

        var synchronizer = app.ApplicationServices.GetRequiredService<IModelSynchronizer>();
        var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();

        // Create a temporary context with a session to resolve queryable properties
        using var session = documentStore.OpenAsyncSession();
        var sparkContext = new TContext();
        sparkContext.Session = session;

        synchronizer.SynchronizeModels(sparkContext);

        Console.WriteLine("Model synchronization completed.");

        return app;
    }

    /// <summary>
    /// Checks command-line arguments for --spark-synchronize-model and runs synchronization if present.
    /// Exits the application after synchronization completes.
    /// </summary>
    public static IApplicationBuilder SynchronizeSparkModelsIfRequested<TContext>(this IApplicationBuilder app, string[] args)
        where TContext : SparkContext, new()
    {
        if (args.Contains("--spark-synchronize-model"))
        {
            app.SynchronizeSparkModels<TContext>();
            Environment.Exit(0);
        }
        return app;
    }

    /// <summary>
    /// Maps all Spark endpoints, including any registered module endpoints (authorization, replication, etc.).
    /// </summary>
    public static IEndpointRouteBuilder MapSpark(this IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<SparkModuleRegistry>();

        // Register the Spark middleware for all requests
        var sparkGroup = endpoints.MapGroup("/spark");
        sparkGroup.MapGet("/", async context =>
        {
            await context.Response.WriteAsync("Spark Middleware is active!");
        });

        // Entity Types endpoints
        var typesGroup = sparkGroup.MapGroup("/types");
        typesGroup.MapGet("/", async (HttpContext context, ListEntityTypes action) =>
            await action.HandleAsync(context));
        typesGroup.MapGet("/{id}", async (HttpContext context, string id, GetEntityType action) =>
            await action.HandleAsync(context, id));

        // Queries endpoints
        var queriesGroup = sparkGroup.MapGroup("/queries");
        queriesGroup.MapGet("/", async (HttpContext context, ListQueries action) =>
            await action.HandleAsync(context));
        queriesGroup.MapGet("/{id}", async (HttpContext context, string id, GetQuery action) =>
            await action.HandleAsync(context, id));
        queriesGroup.MapGet("/{id}/execute", async (HttpContext context, string id, ExecuteQuery action) =>
            await action.HandleAsync(context, id));
        queriesGroup.Map("/{id}/stream", async (HttpContext context, string id, StreamExecuteQuery action) =>
            await action.HandleAsync(context, id));

        // Culture endpoint
        sparkGroup.MapGet("/culture", async (HttpContext context, GetCulture action) =>
            await action.HandleAsync(context));

        // Translations endpoint
        sparkGroup.MapGet("/translations", async (HttpContext context, GetTranslations action) =>
            await action.HandleAsync(context));

        // Program Units endpoint
        sparkGroup.MapGet("/program-units", async (HttpContext context, GetProgramUnits action) =>
            await action.HandleAsync(context));

        // Permissions endpoint
        sparkGroup.MapGet("/permissions/{entityTypeId}", async (HttpContext context, string entityTypeId, GetPermissions action) =>
            await action.HandleAsync(context, entityTypeId));

        // Aliases endpoint
        sparkGroup.MapGet("/aliases", async (HttpContext context, GetAliases action) =>
            await action.HandleAsync(context));

        // Persistent Object endpoints
        var persistentObjectGroup = sparkGroup.MapGroup("/po");
        persistentObjectGroup.MapGet("/{objectTypeId}", async (HttpContext context, string objectTypeId, ListPersistentObjects action) =>
            await action.HandleAsync(context, objectTypeId));
        persistentObjectGroup.MapGet("/{objectTypeId}/{**id}", async (HttpContext context, string objectTypeId, string id, GetPersistentObject action) =>
            await action.HandleAsync(context, objectTypeId, id));
        persistentObjectGroup.MapPost("/{objectTypeId}", async (HttpContext context, string objectTypeId, CreatePersistentObject action) =>
            await action.HandleAsync(context, objectTypeId)).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        persistentObjectGroup.MapPut("/{objectTypeId}/{**id}", async (HttpContext context, string objectTypeId, string id, UpdatePersistentObject action) =>
            await action.HandleAsync(context, objectTypeId, id)).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        persistentObjectGroup.MapDelete("/{objectTypeId}/{**id}", async (HttpContext context, string objectTypeId, string id, DeletePersistentObject action) =>
            await action.HandleAsync(context, objectTypeId, id)).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        // Custom Actions endpoints
        var actionsGroup = sparkGroup.MapGroup("/actions");
        actionsGroup.MapGet("/{objectTypeId}", async (HttpContext context, string objectTypeId, ListCustomActions action) =>
            await action.HandleAsync(context, objectTypeId));
        actionsGroup.MapPost("/{objectTypeId}/{actionName}", async (HttpContext context, string objectTypeId, string actionName, ExecuteCustomAction action) =>
            await action.HandleAsync(context, objectTypeId, actionName)).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        // LookupReferences endpoints
        var lookupRefGroup = sparkGroup.MapGroup("/lookupref");
        lookupRefGroup.MapGet("/", async (HttpContext context, ListLookupReferences action) =>
            await action.HandleAsync(context));
        lookupRefGroup.MapGet("/{name}", async (HttpContext context, string name, GetLookupReference action) =>
            await action.HandleAsync(context, name));
        lookupRefGroup.MapPost("/{name}", async (HttpContext context, string name, AddLookupReferenceValue action) =>
            await action.HandleAsync(context, name)).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        lookupRefGroup.MapPut("/{name}/{key}", async (HttpContext context, string name, string key, UpdateLookupReferenceValue action) =>
            await action.HandleAsync(context, name, key)).WithMetadata(new RequireAntiforgeryTokenAttribute(true));
        lookupRefGroup.MapDelete("/{name}/{key}", async (HttpContext context, string name, string key, DeleteLookupReferenceValue action) =>
            await action.HandleAsync(context, name, key)).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        // Map module-specific endpoints (authorization, replication, etc.)
        registry.MapEndpoints(endpoints);

        return endpoints;
    }

    private static void CreateSparkIndexes(IApplicationBuilder app, Assembly? assembly = null)
    {
        var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
        var indexRegistry = app.ApplicationServices.GetRequiredService<IIndexRegistry>();
        var targetAssembly = assembly ?? Assembly.GetEntryAssembly();

        if (targetAssembly == null)
        {
            Console.WriteLine("Warning: Could not determine entry assembly for index creation.");
            return;
        }

        try
        {
            // Find and register all index types
            var indexTypes = targetAssembly.GetTypes()
                .Where(t => !t.IsAbstract && IsAbstractIndexCreationTask(t))
                .ToList();

            foreach (var indexType in indexTypes)
            {
                indexRegistry.RegisterIndex(indexType);
            }

            // Find and register all projection types with FromIndexAttribute
            var projectionTypes = targetAssembly.GetTypes()
                .Where(t => t.GetCustomAttribute<FromIndexAttribute>() != null)
                .ToList();

            foreach (var projectionType in projectionTypes)
            {
                var attr = projectionType.GetCustomAttribute<FromIndexAttribute>()!;
                indexRegistry.RegisterProjection(projectionType, attr.IndexType);
            }

            // Create indexes in RavenDB
            IndexCreation.CreateIndexes(targetAssembly, documentStore);
            Console.WriteLine($"RavenDB indexes created/updated from assembly: {targetAssembly.GetName().Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating RavenDB indexes: {ex.Message}");
        }
    }

    private static bool IsAbstractIndexCreationTask(Type type)
    {
        var current = type;
        while (current != null && current != typeof(object))
        {
            if (current.IsGenericType)
            {
                var genericDef = current.GetGenericTypeDefinition();
                if (genericDef == typeof(AbstractIndexCreationTask<>) ||
                    genericDef == typeof(AbstractMultiMapIndexCreationTask<>))
                {
                    return true;
                }
            }
            current = current.BaseType;
        }
        return false;
    }
}

public partial class SparkMiddleware
{
    [Inject] private readonly RequestDelegate next;

    public async Task InvokeAsync(HttpContext context)
    {
        // Pre-processing logic
        Console.WriteLine("Before the next middleware");

        // Call the next middleware in the pipeline
        await next(context);

        // Post-processing logic
        Console.WriteLine("After the next middleware");
    }
}
