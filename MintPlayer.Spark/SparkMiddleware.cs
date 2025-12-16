using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Endpoints.PersistentObject;
using Raven.Client.Documents;

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

                store.Initialize();
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

    public static IApplicationBuilder UseSpark(this IApplicationBuilder app)
        => app.UseMiddleware<SparkMiddleware>();

    public static IEndpointRouteBuilder MapSpark(this IEndpointRouteBuilder endpoints)
    {
        // Register the Spark middleware for all requests
        var sparkGroup = endpoints.MapGroup("/spark");
        sparkGroup.MapGet("/", async context =>
        {
            await context.Response.WriteAsync("Spark Middleware is active!");
        });
        var persistentObjectGroup = sparkGroup.MapGroup("/po");
        persistentObjectGroup.MapGet("/{type}", async (HttpContext context, string type, ListPersistentObjects action) =>
            await action.HandleAsync(context, type));
        persistentObjectGroup.MapGet("/{type}/{id:guid}", async (HttpContext context, string type, Guid id, GetPersistentObject action) =>
            await action.HandleAsync(context, type, id));
        persistentObjectGroup.MapPost("/{type}", async (HttpContext context, string type, CreatePersistentObject action) =>
            await action.HandleAsync(context, type));
        persistentObjectGroup.MapPut("/{type}/{id:guid}", async (HttpContext context, string type, Guid id, UpdatePersistentObject action) =>
            await action.HandleAsync(context, type, id));
        persistentObjectGroup.MapDelete("/{type}/{id:guid}", async (HttpContext context, string type, Guid id, DeletePersistentObject action) =>
            await action.HandleAsync(context, type, id));

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
