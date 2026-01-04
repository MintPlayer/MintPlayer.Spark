using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Endpoints.EntityTypes;
using MintPlayer.Spark.Endpoints.LookupReferences;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.ProgramUnits;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark;

public static class SparkExtensions
{
    public static IServiceCollection AddSpark(this IServiceCollection services, Action<SparkOptions>? configureOptions = null)
    {
        var options = new SparkOptions();
        configureOptions?.Invoke(options);

        // Register the Spark services
        return services
            .AddSparkServices()
            .AddSingleton<IDocumentStore>(sp =>
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
    }

    public static IServiceCollection AddSpark(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new SparkOptions();
        configuration.GetSection("Spark").Bind(options);

        return services.AddSpark(opt =>
        {
            opt.RavenDb.Urls = options.RavenDb.Urls;
            opt.RavenDb.Database = options.RavenDb.Database;
        });
    }

    /// <summary>
    /// Registers entity-specific Actions class for customizing CRUD behavior.
    /// Consider using the generated AddSparkActions() extension method which auto-discovers Actions classes.
    /// </summary>
    /// <typeparam name="TActions">The Actions class type (must implement IPersistentObjectActions&lt;TEntity&gt;)</typeparam>
    /// <typeparam name="TEntity">The entity type</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddSparkActions<TActions, TEntity>(this IServiceCollection services)
        where TActions : class, IPersistentObjectActions<TEntity>
        where TEntity : class
    {
        services.AddScoped<IPersistentObjectActions<TEntity>, TActions>();
        services.AddScoped<TActions>();
        return services;
    }

    public static IApplicationBuilder UseSpark(this IApplicationBuilder app)
        => app.UseMiddleware<SparkMiddleware>();

    /// <summary>
    /// Synchronizes entity definitions between SparkContext and App_Data/Model/*.json files.
    /// Call this during development to generate or update model files based on your SparkContext properties.
    /// </summary>
    /// <typeparam name="TContext">The SparkContext implementation type</typeparam>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
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
    /// <typeparam name="TContext">The SparkContext implementation type</typeparam>
    /// <param name="app">The application builder</param>
    /// <param name="args">Command-line arguments</param>
    /// <returns>The application builder for chaining</returns>
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
    /// Creates or updates all RavenDB indexes defined in the specified assembly.
    /// Scans for all AbstractIndexCreationTask implementations and deploys them to RavenDB.
    /// Call this at application startup to ensure indexes are available for queries.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <param name="assembly">The assembly to scan for index definitions. If null, scans the entry assembly.</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder CreateSparkIndexes(this IApplicationBuilder app, Assembly? assembly = null)
    {
        var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
        var targetAssembly = assembly ?? Assembly.GetEntryAssembly();

        if (targetAssembly == null)
        {
            Console.WriteLine("Warning: Could not determine entry assembly for index creation.");
            return app;
        }

        try
        {
            IndexCreation.CreateIndexes(targetAssembly, documentStore);
            Console.WriteLine($"RavenDB indexes created/updated from assembly: {targetAssembly.GetName().Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating RavenDB indexes: {ex.Message}");
        }

        return app;
    }

    /// <summary>
    /// Asynchronously creates or updates all RavenDB indexes defined in the specified assembly.
    /// Scans for all AbstractIndexCreationTask implementations and deploys them to RavenDB.
    /// Call this at application startup to ensure indexes are available for queries.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <param name="assembly">The assembly to scan for index definitions. If null, scans the entry assembly.</param>
    /// <returns>A task representing the async operation</returns>
    public static async Task CreateSparkIndexesAsync(this IApplicationBuilder app, Assembly? assembly = null)
    {
        var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
        var targetAssembly = assembly ?? Assembly.GetEntryAssembly();

        if (targetAssembly == null)
        {
            Console.WriteLine("Warning: Could not determine entry assembly for index creation.");
            return;
        }

        try
        {
            await IndexCreation.CreateIndexesAsync(targetAssembly, documentStore);
            Console.WriteLine($"RavenDB indexes created/updated from assembly: {targetAssembly.GetName().Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating RavenDB indexes: {ex.Message}");
        }
    }

    public static IEndpointRouteBuilder MapSpark(this IEndpointRouteBuilder endpoints)
    {
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
        typesGroup.MapGet("/{id:guid}", async (HttpContext context, Guid id, GetEntityType action) =>
            await action.HandleAsync(context, id));

        // Queries endpoints
        var queriesGroup = sparkGroup.MapGroup("/queries");
        queriesGroup.MapGet("/", async (HttpContext context, ListQueries action) =>
            await action.HandleAsync(context));
        queriesGroup.MapGet("/{id:guid}", async (HttpContext context, Guid id, GetQuery action) =>
            await action.HandleAsync(context, id));
        queriesGroup.MapGet("/{id:guid}/execute", async (HttpContext context, Guid id, ExecuteQuery action) =>
            await action.HandleAsync(context, id));

        // Program Units endpoint
        sparkGroup.MapGet("/program-units", async (HttpContext context, GetProgramUnits action) =>
            await action.HandleAsync(context));

        // Persistent Object endpoints
        var persistentObjectGroup = sparkGroup.MapGroup("/po");
        persistentObjectGroup.MapGet("/{objectTypeId:guid}", async (HttpContext context, Guid objectTypeId, ListPersistentObjects action) =>
            await action.HandleAsync(context, objectTypeId));
        persistentObjectGroup.MapGet("/{objectTypeId:guid}/{id}", async (HttpContext context, Guid objectTypeId, string id, GetPersistentObject action) =>
            await action.HandleAsync(context, objectTypeId, id));
        persistentObjectGroup.MapPost("/{objectTypeId:guid}", async (HttpContext context, Guid objectTypeId, CreatePersistentObject action) =>
            await action.HandleAsync(context, objectTypeId));
        persistentObjectGroup.MapPut("/{objectTypeId:guid}/{id}", async (HttpContext context, Guid objectTypeId, string id, UpdatePersistentObject action) =>
            await action.HandleAsync(context, objectTypeId, id));
        persistentObjectGroup.MapDelete("/{objectTypeId:guid}/{id}", async (HttpContext context, Guid objectTypeId, string id, DeletePersistentObject action) =>
            await action.HandleAsync(context, objectTypeId, id));

        // LookupReferences endpoints
        var lookupRefGroup = sparkGroup.MapGroup("/lookupref");
        lookupRefGroup.MapGet("/", async (HttpContext context, ListLookupReferences action) =>
            await action.HandleAsync(context));
        lookupRefGroup.MapGet("/{name}", async (HttpContext context, string name, GetLookupReference action) =>
            await action.HandleAsync(context, name));
        lookupRefGroup.MapPost("/{name}", async (HttpContext context, string name, AddLookupReferenceValue action) =>
            await action.HandleAsync(context, name));
        lookupRefGroup.MapPut("/{name}/{key}", async (HttpContext context, string name, string key, UpdateLookupReferenceValue action) =>
            await action.HandleAsync(context, name, key));
        lookupRefGroup.MapDelete("/{name}/{key}", async (HttpContext context, string name, string key, DeleteLookupReferenceValue action) =>
            await action.HandleAsync(context, name, key));

        return endpoints;
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
