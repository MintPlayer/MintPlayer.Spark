using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Converters;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Json.Serialization.NewtonsoftJson;
using Raven.Client.ServerWide.Operations;
using System.Reflection;

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

        // Register authorization (required by UseSpark → UseAuthorization)
        services.AddAuthorization();

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
                Urls = options.RavenDb.Urls.Length > 0 ? options.RavenDb.Urls : ["http://localhost:8080"],
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

            // Wait for RavenDB to become available (handles container startup ordering in docker-compose, etc.)
            WaitForRavenDbConnection(store, options.RavenDb);

            var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
            if (hostEnvironment.IsDevelopment() || options.RavenDb.EnsureDatabaseCreated)
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

        app.UseAuthorization();

        // Antiforgery validation for mutating requests that carry IAntiforgeryMetadata.
        //
        // Runs BEFORE the built-in UseAntiforgery() so this middleware can call
        // IAntiforgery.ValidateRequestAsync before FormFeature's "unvalidated" guard
        // gets set. After successful validation we set IAntiforgeryValidationFeature to
        // "validated" so (a) the built-in middleware and FormFeature treat the request
        // as already checked and (b) EndpointMiddleware doesn't throw
        // "contains anti-forgery metadata, but a middleware was not found".
        //
        // The built-in UseAntiforgery() was narrowed in 8.0.1 to validate ONLY form-content
        // bodies — Spark's JSON API is not protected by it alone. This middleware closes
        // that gap for any mutating HTTP method (POST/PUT/PATCH/DELETE) whose endpoint has
        // IAntiforgeryMetadata.RequiresValidation = true.
        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            var metadata = endpoint?.Metadata.GetMetadata<IAntiforgeryMetadata>();
            if (metadata is { RequiresValidation: true } && IsMutatingMethod(context.Request.Method))
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                    context.Features.Set<IAntiforgeryValidationFeature>(new SparkAntiforgeryValidationFeature(isValid: true));
                }
                catch (AntiforgeryValidationException ex)
                {
                    context.Features.Set<IAntiforgeryValidationFeature>(new SparkAntiforgeryValidationFeature(isValid: false, error: ex));
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
            await next(context);
        });

        // Keep the built-in middleware registered — EndpointMiddleware uses its presence as a
        // "antiforgery was wired" probe when the endpoint has IAntiforgeryMetadata. For
        // non-form mutating requests that pass Spark's validation above, it's a no-op.
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
    /// Configures Spark middleware with additional options.
    /// Call after UseRouting(). Do NOT call UseAuthentication/UseAuthorization/UseAntiforgery separately.
    /// </summary>
    /// <example>
    /// <code>
    /// app.UseSpark(o => o.SynchronizeModelsIfRequested&lt;MyContext&gt;(args));
    /// </code>
    /// </example>
    public static IApplicationBuilder UseSpark(this IApplicationBuilder app, Action<UseSparkOptions> configure)
    {
        app.UseSpark();

        var options = new UseSparkOptions { App = app };
        configure(options);

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

        // Map all core Spark endpoints (source-generated from endpoint classes)
        endpoints.MapSparkCoreEndpoints();

        // Map module-specific endpoints (authorization, replication, etc.)
        registry.MapEndpoints(endpoints);

        return endpoints;
    }

    private static void WaitForRavenDbConnection(IDocumentStore store, Configuration.RavenDbOptions ravenDbOptions)
    {
        var maxRetries = ravenDbOptions.MaxConnectionRetries;
        if (maxRetries <= 0) return;

        var delay = TimeSpan.FromSeconds(Math.Max(ravenDbOptions.RetryDelaySeconds, 1));

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 1));
                if (attempt > 1)
                {
                    Console.WriteLine($"Successfully connected to RavenDB after {attempt} attempts.");
                }
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                Console.WriteLine($"Waiting for RavenDB to become available (attempt {attempt}/{maxRetries}): {ex.Message}");
                Thread.Sleep(delay);
            }
        }

        // Final attempt — let the exception propagate if it still fails
        store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 1));
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

    private static bool IsMutatingMethod(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);

    /// <summary>
    /// Spark's implementation of <see cref="IAntiforgeryValidationFeature"/> used to record
    /// the outcome of <see cref="IAntiforgery.ValidateRequestAsync"/>. The concrete class
    /// in <c>Microsoft.AspNetCore.Antiforgery</c> is internal, so we provide our own.
    /// </summary>
    private sealed class SparkAntiforgeryValidationFeature(bool isValid, AntiforgeryValidationException? error = null)
        : IAntiforgeryValidationFeature
    {
        public bool IsValid { get; } = isValid;
        public Exception? Error { get; } = error;
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
