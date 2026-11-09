using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.RavenDB;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Storage.Raven;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark;

/// <summary>
/// <c>UseRavenDb()</c>: RavenDB as the application's store (#551 D17/D18). Registers the document store, the
/// request-scoped sessions, the storage seam (<see cref="ISparkStore"/>, <see cref="ISparkSession"/>,
/// <see cref="ISparkQueryProvider"/>) and the provider's capabilities: change feed, expiration, index catalog and
/// deployment, the Data Protection key store and the row-key scan.
/// </summary>
public static class SparkRavenDbExtensions
{
    /// <summary>Uses RavenDB, configured from <c>Spark:RavenDb</c>, then by <paramref name="configure"/>.</summary>
    public static ISparkBuilder UseRavenDb(this ISparkBuilder builder, Action<RavenDbOptions>? configure = null)
    {
        var options = new RavenDbOptions();
        builder.Configuration?.GetSection(RavenDbOptions.SectionName).Bind(options);
        configure?.Invoke(options);

        var services = builder.Services;
        services.AddSingleton(options);

        services.AddSingleton<IDocumentStore>(sp =>
        {
            var store = new DocumentStore
            {
                Urls = options.Urls.Length > 0 ? options.Urls : ["http://localhost:8080"],
                Database = options.Database,
            };

            // One call, shared with the test drivers. Configuring a store by hand here is how
            // production and the suite drifted apart -- see SparkStoreConfiguration.
            store.ApplySparkConventions();

            // A secured server: the client certificate and a private server authority, from
            // Spark:RavenDb. Nothing configured = nothing changes (local dev, the tests).
            SparkRavenDbSecurity.Apply(store, options);

            store.Initialize();

            // Wait for RavenDB to become available (handles container startup ordering in docker-compose, etc.)
            WaitForConnection(store, options);

            var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
            if (hostEnvironment.IsDevelopment() || options.EnsureDatabaseCreated)
            {
                var databaseNames = store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, int.MaxValue));
                if (!databaseNames.Contains(options.Database))
                {
                    store.Maintenance.Server.Send(new CreateDatabaseOperation(o =>
                        o.Regular(options.Database).WithReplicationFactor(1)
                    ));
                }
            }

            return store;
        });

        AddRavenDbSessions(services);
        return builder;
    }

    /// <summary>
    /// The request-scoped sessions and the storage seam over an <see cref="IDocumentStore"/> registered by the caller.
    /// <see cref="UseRavenDb"/> calls it; a test host that brings its own store calls it directly.
    /// </summary>
    public static IServiceCollection AddRavenDbSessions(this IServiceCollection services)
    {
        // Request-scoped Raven sessions. One session per HTTP request, disposed when the
        // DI scope ends. MaxNumberOfRequestsPerSession stays at Raven's default (30) — if
        // a single method needs more headroom, use SessionExtensions.IgnoreMaxRequests().
        services.AddScoped<IAsyncDocumentSession>(sp => sp.GetRequiredService<IDocumentStore>().OpenAsyncSession());
        services.AddScoped<IDocumentSession>(sp => sp.GetRequiredService<IDocumentStore>().OpenSession());

        // The storage seam (docs/spark_editor_PRD.md D2/D3). The request-scoped ISparkSession wraps the request's own
        // Raven session, so code still holding IAsyncDocumentSession shares its unit of work.
        services.AddScoped<ISparkSession>(sp => new RavenSparkSession(sp.GetRequiredService<IAsyncDocumentSession>()));

        // Store, query provider, change feed, expiration, index catalog + deployment, key store, row-key scan
        // ([Register]ed in this package).
        services.AddSparkRavenDbServices();
        return services;
    }

    private static void WaitForConnection(IDocumentStore store, RavenDbOptions options)
    {
        var maxRetries = options.MaxConnectionRetries;
        if (maxRetries <= 0) return;

        var delay = TimeSpan.FromSeconds(Math.Max(options.RetryDelaySeconds, 1));

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
}
