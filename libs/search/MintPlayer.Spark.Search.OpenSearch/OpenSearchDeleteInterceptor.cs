using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Search.OpenSearch;

/// <summary>
/// Removes a hard-deleted or purged row of a searchable type from the engine (<c>docs/spark_search_PRD.md</c> D8). Its
/// index entry vanishes with the document, so the sync cannot see it go. Durable (#482): Spark Messaging delivers it
/// after the delete committed, with retries while the engine is down. A soft delete is a save, which the sync copies.
/// </summary>
internal sealed partial class OpenSearchDeleteInterceptor : IAfterDeleteCommitted
{
    [Inject] private readonly OpenSearchEngine engine;
    [Inject] private readonly IModelLoader models;

    /// <summary>Only searchable types, so no outbox message is written for any other delete. The model is fixed per process.</summary>
    public bool AppliesTo(Type entityType)
        => models.GetEntityTypes().Any(t => t.Search?.Enabled == true && string.Equals(t.ClrType, entityType.FullName, StringComparison.Ordinal));

    public async Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        if (!change.IsReplaced)
            await engine.DeleteAsync(change.Id, cancellationToken);
    }
}
