using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Expiration;

namespace MintPlayer.Spark.Expiration;

/// <summary>
/// What <c>UseSpark()</c> does for document expiration (<c>docs/seo_analyzer_PRD.md</c> X1): checks the
/// model, installs the <c>@expires</c> stamper, and enables expiration on the database once — the one
/// place that does, with the one shared frequency.
/// </summary>
/// <remarks>
/// <para><b>Errors</b> (refuse startup):
/// <list type="bullet">
/// <item>a model type implementing <see cref="IExpiring"/> whose <c>ExpiresAtUtc</c> is not a public
/// read/write <c>DateTime?</c> property (the filter is rebound by member name, and RavenDB stores only
/// public properties);</item>
/// <item>a model type declaring <c>expiresAfter</c> that does not implement <see cref="IExpiring"/>, or
/// declaring zero or less;</item>
/// <item>a delete frequency below <see cref="SparkExpiration.MinimumDeleteFrequency"/>.</item>
/// </list></para>
/// <para>The database is configured when a library declared
/// <see cref="SparkModuleRegistry.RequireDocumentExpiration"/>, the app called <c>AddExpiration()</c>, or a
/// model type implements <see cref="IExpiring"/> — unless <c>Spark:Expiration:ConfigureDatabase</c> is
/// false.</para>
/// <para><b>Warning</b>: an index projection over an expiring collection without <c>ExpiresAtUtc</c> —
/// the filter then runs after materialization for queries on it.</para>
/// </remarks>
internal static class ExpirationStartup
{
    public static void Run(IServiceProvider services, SparkModuleRegistry registry)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ExpirationStartup).FullName!);
        var modelLoader = services.GetRequiredService<IModelLoader>();

        var problems = new List<string>();
        var expiring = new List<Type>();
        foreach (var definition in modelLoader.GetEntityTypes())
        {
            var type = SparkTypeResolver.ResolveClrType(definition.ClrType);
            var isExpiring = type is not null && typeof(IExpiring).IsAssignableFrom(type);
            if (isExpiring && !expiring.Contains(type!))
            {
                expiring.Add(type!);
                if (MemberProblem(type!) is { } problem)
                    problems.Add(problem);
            }

            if (definition.ExpiresAfter is { } after)
            {
                if (!isExpiring)
                    problems.Add($"Model type {definition.Name} declares \"expiresAfter\" but {definition.ClrType ?? "its CLR type"} does not implement IExpiring, so nothing would carry the expiry.");
                else if (after <= TimeSpan.Zero)
                    problems.Add($"Model type {definition.Name} declares \"expiresAfter\": \"{after}\"; it must be positive.");
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Document expiration cannot govern these types:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));

        if (services.GetService<IDocumentStore>() is not { } store)
            return;

        ExpirationStamper.Install(store, modelLoader, services.GetService<TimeProvider>() ?? TimeProvider.System);

        if (expiring.Count > 0 && services.GetService<IIndexCatalog>() is { } catalog)
        {
            // EntityTypes, so a multi-map default — no collection of its own, but the default of each type its
            // [DefaultIndex(...)] lists — is checked for each of those types (docs/default_index_PRD.md, SP1).
            foreach (var entry in catalog.GetAllEntries())
            foreach (var collection in entry.EntityTypes)
            {
                if (typeof(IExpiring).IsAssignableFrom(collection)
                    && entry.ProjectionType is { } projection && projection.GetProperty(nameof(IExpiring.ExpiresAtUtc)) is null)
                    logger.LogWarning(
                        "Projection {Projection} of index {Index} has no ExpiresAtUtc, so expired {EntityType} rows are dropped " +
                        "after materialization for queries on it and pages come back short: add ExpiresAtUtc to the projection and the Map.",
                        projection.Name, entry.IndexName, collection.Name);
            }
        }

        var requesters = registry.DocumentExpirationRequesters.ToList();
        if (expiring.Count > 0)
            requesters.Add("IExpiring model types (" + string.Join(", ", expiring.Select(t => t.Name)) + ")");
        if (requesters.Count == 0)
            return;

        var options = services.GetRequiredService<IOptions<SparkExpirationOptions>>().Value;
        if (!options.ConfigureDatabase)
        {
            logger.LogInformation("Document expiration is needed by {Requesters} but Spark:Expiration:ConfigureDatabase is false; the database record is left as it is.",
                string.Join(", ", requesters));
            return;
        }
        var frequency = SparkExpiration.ValidateDeleteFrequency(options.DeleteFrequency);

        store.Maintenance.Send(new ConfigureExpirationOperation(new ExpirationConfiguration
        {
            Disabled = false,
            DeleteFrequencyInSec = frequency,
        }));
        logger.LogDebug("Document expiration enabled every {Frequency} for {Requesters}.", options.DeleteFrequency, string.Join(", ", requesters));
    }

    private static string? MemberProblem(Type type)
    {
        const string name = nameof(IExpiring.ExpiresAtUtc);
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
            return $"{type.FullName}.{name} is not a public property (an explicit interface implementation is stored nowhere).";
        if (property.PropertyType != typeof(DateTime?))
            return $"{type.FullName}.{name} is {property.PropertyType.Name}, expected DateTime?.";
        if (property.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true)
            return $"{type.FullName}.{name} must have a public getter and setter.";
        return null;
    }
}
