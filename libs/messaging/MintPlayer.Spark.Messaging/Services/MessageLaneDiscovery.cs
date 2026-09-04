using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The lanes that exist because something is registered to handle them.
/// </summary>
/// <remarks>
/// Under one subscription this list is no longer load-bearing for delivery — a lane nobody declared
/// still works, because the pump is created on first delivery. It is used to start pumps eagerly, so
/// that a lane whose entire backlog predates startup is drained without waiting for a new message to
/// ring its bell, and to validate partition selectors at startup.
/// </remarks>
internal sealed class MessageLaneDiscovery(IServiceCollectionAccessor accessor) : IMessageLaneDiscovery
{
    public IReadOnlyCollection<string> DiscoverLaneNames()
        => [.. MessageTypes().Select(QueueNames.ForMessageType).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every message type with a registered recipient.</summary>
    public IReadOnlyCollection<Type> MessageTypes()
        => [.. accessor.Services
            .Select(d => d.ServiceType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRecipient<>))
            .Select(t => t.GetGenericArguments()[0])
            .Distinct()];
}
