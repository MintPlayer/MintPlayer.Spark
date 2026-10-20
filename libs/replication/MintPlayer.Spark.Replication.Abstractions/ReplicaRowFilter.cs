using MintPlayer.Spark.Replication.Abstractions.Rendering;

namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Owner side: restricts which rows of a source collection one consumer module receives (warehouse §4.2.3, "Row
/// filter"). Without it every approved consumer of a collection gets every document; a warehouse serving several shops
/// would send shop A's status, shipments, stock levels and return receipts to shop B. The warehouse implements it with
/// its owner links (Q-W23): consumer module → the owner ids it serves.
/// </summary>
/// <remarks>
/// <para>Registered in DI (any number); every condition that applies must admit a row. The owner evaluates them when
/// it delivers, so the filter is never the consumer's: the HTTP push and the reconcile skip refused rows, and the ETL
/// script is rendered with the conditions (<see cref="ReplicaScriptRenderer.RenderEtlScript(ReplicaManifest, string, ReplicaRowScope)"/>).</para>
/// <para>A row that leaves a consumer's scope is removed there by the next reconcile (HTTP) or by the ETL reset that
/// follows <see cref="IReplicaRowScopeChanges.ScopeChangedAsync"/>. Deletes of scoped collections are delivered by the
/// reconcile only (the deleted row can no longer be checked, and an id must not reach a consumer that never held it).</para>
/// </remarks>
public interface IReplicaRowFilter
{
    /// <summary>
    /// The condition rows of <paramref name="sourceCollection"/> must meet to reach <paramref name="consumerModule"/>,
    /// or <see langword="null"/> when this filter does not restrict that pair. An empty value list delivers nothing.
    /// </summary>
    Task<ReplicaRowCondition?> GetConditionAsync(string consumerModule, string sourceCollection, CancellationToken cancellationToken);
}

/// <summary>
/// Owner side: tell replication that a consumer's row scope changed (an owner link added or removed), so its delivery is
/// brought in line: the ETL transforms are re-rendered and reset, the HTTP subscriptions reconciled.
/// </summary>
public interface IReplicaRowScopeChanges
{
    /// <summary>Queues the re-delivery in <paramref name="session"/> (a RavenDB async session), so it commits with the change.</summary>
    Task ScopeChangedAsync(object session, string consumerModule, CancellationToken cancellationToken = default);
}
