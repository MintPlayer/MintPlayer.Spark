using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Licensing;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Http;
using Raven.Client.ServerWide.Operations;
using Sparrow.Json;

namespace MintPlayer.Spark.Services;

/// <summary>Reads the server's licence once per process (warehouse WX13). See <see cref="ISparkLicenseCapabilities"/>.</summary>
[Register(typeof(ISparkLicenseCapabilities), ServiceLifetime.Singleton)]
internal sealed partial class SparkLicenseCapabilities : ISparkLicenseCapabilities
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILogger<SparkLicenseCapabilities> logger;

    private readonly SemaphoreSlim gate = new(1, 1);
    private SparkLicenseStatus? cached;
    private bool read;

    public async Task<SparkLicenseStatus?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (read)
            return cached;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (read)
                return cached;

            try
            {
                cached = await documentStore.Maintenance.Server.SendAsync(new GetLicenseStatusOperation(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unknown, not "no": a reader treats it as such (replication's Auto falls back to HTTP).
                logger.LogWarning(ex, "The RavenDB licence status (/license/status) could not be read.");
                cached = null;
            }

            read = true;
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SparkIndexBudget?> GetIndexBudgetAsync(CancellationToken cancellationToken = default)
    {
        var licence = await GetAsync(cancellationToken);
        try
        {
            var names = await documentStore.Maintenance.SendAsync(new GetIndexNamesOperation(0, int.MaxValue), cancellationToken);
            var auto = names.Count(IsAutoIndex);
            return new SparkIndexBudget(names.Length - auto, auto, licence?.MaxStaticIndexesPerDatabase, licence?.MaxAutoIndexesPerDatabase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The database's indexes could not be counted for the licence budget.");
            return null;
        }
    }

    /// <summary>RavenDB names every auto index <c>Auto/…</c>.</summary>
    internal static bool IsAutoIndex(string name) => name.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Logs a warning when the deployed static indexes exceed the licence's limit, and an error when the
    /// limit leaves fewer than two for the host. Called once after Spark deployed its indexes. Never throws.
    /// </summary>
    internal async Task WarnOnIndexBudgetAsync(CancellationToken cancellationToken = default)
    {
        var budget = await GetIndexBudgetAsync(cancellationToken);
        if (budget is null || budget.MaxStaticIndexes is null)
            return;

        if (budget.StaticOverLimit)
            logger.LogWarning(
                "This database has {Static} static indexes, more than the {Max} the RavenDB licence allows per database. " +
                "Index deployment beyond the limit is refused by the server; compose library indexes (one declared multi-map index) or use a licence without the cap.",
                budget.StaticIndexes, budget.MaxStaticIndexes);
        if (budget.AutoOverLimit)
            logger.LogWarning(
                "This database has {Auto} auto indexes, more than the {Max} the licence allows. Bind library pages to static indexes.",
                budget.AutoIndexes, budget.MaxAutoIndexes);
    }

    /// <summary>Parses the body of <c>/license/status</c>. Internal for tests.</summary>
    internal static SparkLicenseStatus Parse(JsonElement root) => new()
    {
        Type = root.TryGetProperty("Type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null,
        HasRavenEtl = root.TryGetProperty("HasRavenEtl", out var etl) && etl.ValueKind == JsonValueKind.True,
        MaxStaticIndexesPerDatabase = Limit(root, "MaxNumberOfStaticIndexesPerDatabase"),
        MaxAutoIndexesPerDatabase = Limit(root, "MaxNumberOfAutoIndexesPerDatabase"),
        MaxSubscriptionsPerDatabase = Limit(root, "MaxNumberOfSubscriptionsPerDatabase"),
    };

    private static int? Limit(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var limit)
            ? limit
            : null;

    /// <summary><c>GET /license/status</c>.</summary>
    internal sealed class GetLicenseStatusOperation : IServerOperation<SparkLicenseStatus?>
    {
        public RavenCommand<SparkLicenseStatus?> GetCommand(DocumentConventions conventions, JsonOperationContext context) => new Command();

        private sealed class Command : RavenCommand<SparkLicenseStatus?>
        {
            public override bool IsReadRequest => true;

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/license/status";
                return new HttpRequestMessage { Method = HttpMethod.Get };
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject? response, bool fromCache)
            {
                if (response is null)
                    return;
                using var json = JsonDocument.Parse(response.ToString());
                Result = Parse(json.RootElement);
            }
        }
    }
}
