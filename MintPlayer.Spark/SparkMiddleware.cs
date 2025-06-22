using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark;

public static class SparkMiddlewareExtensions
{
    public static IApplicationBuilder UseSpark(this IApplicationBuilder app)
        => app.UseMiddleware<SparkMiddleware>();

    public static IEndpointRouteBuilder MapSpark(this IEndpointRouteBuilder endpoints)
    {
        // Register the Spark middleware for all requests
        endpoints.MapGroup("/spark")
            .MapGet("/", async context =>
            {
                await context.Response.WriteAsync("Spark Middleware is active!");
            });

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
