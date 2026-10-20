using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Messages;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Owner: the row scope of one (consumer, source collection), combined from every registered
/// <see cref="IReplicaRowFilter"/> (warehouse §4.2.3, "Row filter"). Read when delivering, never cached: an owner link
/// removed a second ago no longer admits its rows.
/// </summary>
internal interface IReplicaRowScopes
{
    /// <returns><see langword="null"/> when no filter restricts the pair (every row is delivered).</returns>
    Task<ReplicaRowScope?> GetAsync(string consumer, string sourceCollection, CancellationToken cancellationToken);
}

internal sealed partial class ReplicaRowScopes : IReplicaRowScopes
{
    [Inject] private readonly IEnumerable<IReplicaRowFilter> filters;

    public async Task<ReplicaRowScope?> GetAsync(string consumer, string sourceCollection, CancellationToken cancellationToken)
    {
        var conditions = new List<ReplicaRowCondition?>();
        foreach (var filter in filters)
            conditions.Add(await filter.GetConditionAsync(consumer, sourceCollection, cancellationToken));
        return ReplicaRowScope.Combine(conditions);
    }

    /// <summary>Whether the source document (its JSON as the store serializes it) is within <paramref name="scope"/>.</summary>
    public static bool Admits(ReplicaRowScope? scope, JsonElement source)
        => scope is null || scope.Admits(field =>
            source.ValueKind == JsonValueKind.Object && source.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null);
}

/// <summary><see cref="IReplicaRowScopeChanges"/>: a deploy message in the caller's session, so it commits with the link change.</summary>
internal sealed partial class ReplicaRowScopeChanges : IReplicaRowScopeChanges
{
    [Inject] private readonly IMessageOutbox outbox;

    public Task ScopeChangedAsync(object session, string consumerModule, CancellationToken cancellationToken = default)
    {
        if (session is not IAsyncDocumentSession asyncSession)
            throw new ArgumentException("The session must be a RavenDB IAsyncDocumentSession.", nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerModule);

        return outbox.EnqueueAsync(asyncSession, new ReplicaDeployMessage { Consumer = consumerModule }, cancellationToken: cancellationToken);
    }
}
