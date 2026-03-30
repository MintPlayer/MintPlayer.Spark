using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;
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
        // Register authorization (required by UseSpark → UseAuthorization)
        services.AddAuthorization();

        // Register antiforgery (required by Spark's POST/PUT/DELETE endpoints)
        services.AddAntiforgery(opt => opt.HeaderName = "X-XSRF-TOKEN");

        // Ensure HttpContextAccessor is available (needed for RequestCultureResolver)
        services.AddHttpContextAccessor();

        // Register the Spark services
        services.AddSparkServices();

        // Let modules register their services (this is where UseRavenDb(), AddActions(), etc. are called)
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
    /// Configures Spark middleware, storage provider initialization, and all registered module middleware.
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

        // Initialize storage provider (creates indexes, ensures database, etc.)
        var storageProvider = app.ApplicationServices.GetService<ISparkStorageProvider>();
        storageProvider?.Initialize(app);

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
        var sessionFactory = app.ApplicationServices.GetRequiredService<ISparkSessionFactory>();

        // Create a temporary context with a session to resolve queryable properties
        using var session = sessionFactory.OpenSession();
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
}

public partial class SparkMiddleware
{
    [Inject] private readonly RequestDelegate next;

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
    }
}
