using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.RavenDB.Converters;
using MintPlayer.Spark.Storage;
using Raven.Client.Documents;
using Raven.Client.Json.Serialization.NewtonsoftJson;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.RavenDB;

public static class RavenDbSparkExtensions
{
    /// <summary>
    /// Configures the Spark application to use RavenDB as the storage backend.
    /// Creates the <see cref="IDocumentStore"/>, <see cref="ISparkSessionFactory"/>,
    /// and <see cref="ISparkStorageProvider"/> registrations.
    /// </summary>
    public static ISparkBuilder UseRavenDb(this ISparkBuilder builder, Action<RavenDbSparkOptions>? configure = null)
    {
        var options = new RavenDbSparkOptions();

        // Bind from configuration if available
        builder.Configuration?.GetSection("Spark:RavenDb").Bind(options);

        // Apply user overrides
        configure?.Invoke(options);

        // Register IDocumentStore singleton
        builder.Services.AddSingleton<IDocumentStore>(sp =>
        {
            var store = new DocumentStore
            {
                Urls = options.Urls,
                Database = options.Database,
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

            // Wait for RavenDB to become available
            WaitForRavenDbConnection(store, options);

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

        // Register ISparkSessionFactory
        builder.Services.AddSingleton<ISparkSessionFactory>(sp =>
        {
            var documentStore = sp.GetRequiredService<IDocumentStore>();
            return new RavenDbSessionFactory(documentStore);
        });

        // Register ISparkStorageProvider
        builder.Services.AddSingleton<ISparkStorageProvider>(sp =>
        {
            var documentStore = sp.GetRequiredService<IDocumentStore>();
            return new RavenDbStorageProvider(documentStore);
        });

        return builder;
    }

    private static void WaitForRavenDbConnection(IDocumentStore store, RavenDbSparkOptions options)
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
