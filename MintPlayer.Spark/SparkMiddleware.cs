using Microsoft.AspNetCore.Builder;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;

namespace MintPlayer.Spark;

public static class SparkExtensions
{
    public static IServiceCollection AddSpark(this IServiceCollection services)
    {
        // Register the Spark services
        return services
            .AddSparkServices()
            .AddScoped<IDocumentStore, DocumentStore>((services) =>
            {
                var store = new DocumentStore
                {
                    Urls = ["http://localhost:8080"],
                    Database = "YourDatabaseName",
                };

                store.Initialize();
                return store;
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
        persistentObjectGroup.MapGet("/{type}", async (HttpContext context, string type) =>
        {
            await context.Response.WriteAsync($"Get all {type}s!");
        });
        persistentObjectGroup.MapGet("/{type}/{id:guid}", async (HttpContext context, string type, Guid id) =>
        {
            await context.Response.WriteAsync($"Get {type} with ID {id}!");
        });

        // Now visit: https://localhost:32773/spark/po/artist/d72cf934-39f7-4850-8a03-3cfa89a55234

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
