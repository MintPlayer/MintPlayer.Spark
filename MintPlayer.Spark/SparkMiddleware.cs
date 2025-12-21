using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Endpoints.EntityTypes;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.ProgramUnits;
using MintPlayer.Spark.Endpoints.Queries;
using Raven.Client.Documents;
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
