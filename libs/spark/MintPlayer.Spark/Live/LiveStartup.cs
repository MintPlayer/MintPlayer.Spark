using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Live;

/// <summary>
/// What <c>UseSpark()</c> does for live updates (X4): checks the options, decides which live queries are app
/// streams, and installs the <c>Actions</c> change source on the document store.
/// </summary>
/// <remarks>
/// <para><b>Errors</b> (refuse startup): a non-positive limit or interval; a <c>Custom.*</c> query whose method
/// has the app-stream signature but is not <c>"live": true</c> (it could only ever answer an empty grid).</para>
/// <para>
/// <see cref="SparkQuery.IsAppStream"/> is stamped on the query loader's instances, which every query lookup
/// returns for the life of the process.
/// </para>
/// </remarks>
internal static class LiveStartup
{
    public static void Run(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<SparkLiveOptions>>().Value;
        Validate(options);

        if (options.Source != SparkLiveSource.Changes)
        {
            var hub = services.GetRequiredService<LiveHub>();
            hub.AttachActionsSource(LiveActionsSource.Install(services.GetRequiredService<IDocumentStore>(), hub));
        }

        var modelLoader = services.GetRequiredService<IModelLoader>();
        var problems = new List<string>();
        foreach (var query in services.GetRequiredService<IQueryLoader>().GetQueries())
        {
            if (!query.Source.StartsWith("Custom.", StringComparison.OrdinalIgnoreCase) || query.EntityType is null)
                continue;
            var definition = modelLoader.GetEntityTypeByName(query.EntityType);
            if (SparkTypeResolver.ResolveClrType(definition?.ClrType) is not { } entityType)
                continue;

            // By type, never by instance: a library's actions class may need services only its own
            // registration adds, and a model layer is present whether or not the app added that library.
            var actionsType = ActionsResolver.FindActionsTypeFor(entityType);
            var isStream = actionsType is not null && LiveAppStreamMethod.Resolve(actionsType, query.Source["Custom.".Length..]) is not null;
            if (isStream && !query.Live)
            {
                problems.Add(
                    $"Query '{query.Name}': {actionsType!.Name}.{query.Source["Custom.".Length..]} is an app stream " +
                    $"(LiveQueryArgs, CancellationToken → IAsyncEnumerable), which only a live query can serve. Add \"live\": true " +
                    $"to the query in the model file.");
            }
            query.IsAppStream = isStream && query.Live;
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
    }

    internal static void Validate(SparkLiveOptions options)
    {
        var problems = new List<string>();
        if (options.Throttle < TimeSpan.Zero)
            problems.Add($"{SparkLiveOptions.SectionName}:Throttle must not be negative.");
        if (options.MaxSubscriptionsPerSocket < 1)
            problems.Add($"{SparkLiveOptions.SectionName}:MaxSubscriptionsPerSocket must be at least 1.");
        if (options.MaxSocketsPerUser < 1)
            problems.Add($"{SparkLiveOptions.SectionName}:MaxSocketsPerUser must be at least 1.");
        foreach (var (name, value) in new[]
                 {
                     ("Heartbeat", options.Heartbeat), ("SendTimeout", options.SendTimeout), ("Revalidate", options.Revalidate),
                     ("MaxSocketLifetime", options.MaxSocketLifetime), ("IndexWait", options.IndexWait),
                 })
        {
            if (value <= TimeSpan.Zero)
                problems.Add($"{SparkLiveOptions.SectionName}:{name} must be positive.");
        }
        if (options.MaxMessageBytes < 1024)
            problems.Add($"{SparkLiveOptions.SectionName}:MaxMessageBytes must be at least 1024.");
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
    }
}
