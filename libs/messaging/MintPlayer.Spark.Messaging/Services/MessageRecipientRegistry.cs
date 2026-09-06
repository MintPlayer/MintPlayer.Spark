using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

/// <inheritdoc cref="IMessageRecipientRegistry"/>
/// <remarks>
/// The third consumer of the same scan, and now its owner: <see cref="MessageTypeAllowList"/> reads
/// the descriptors to decide what may be deserialized (a security boundary, which also needs the
/// handler types), and <see cref="MessageSubscriptionManager"/> read them to decide which queues to
/// start a worker for. That second question is this class's — a queue is worth a worker exactly
/// when its message type has a recipient — so the manager now asks here instead of scanning again.
/// <para>
/// Built once from the <see cref="IServiceCollection"/> captured at registration time, because DI
/// cannot enumerate its own registrations after the provider is built. That makes the answer a
/// startup fact: a recipient registered after <c>AddSparkMessaging</c> is still seen (the collection
/// is read lazily on first use, by which time the provider exists and registration is over), but
/// nothing registered after the first query is.
/// </para>
/// </remarks>
internal sealed class MessageRecipientRegistry : IMessageRecipientRegistry
{
    private readonly IServiceCollectionAccessor? accessor;
    private readonly Lazy<HashSet<Type>> messageTypes;

    public MessageRecipientRegistry(IServiceCollectionAccessor? accessor)
    {
        this.accessor = accessor;
        messageTypes = new Lazy<HashSet<Type>>(Scan, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The message types that have at least one recipient — the queues worth draining.</summary>
    public IReadOnlyCollection<Type> ConsumedMessageTypes => messageTypes.Value;

    public bool HasRecipient(Type? messageType)
        => messageType is not null && messageTypes.Value.Contains(messageType);

    public bool HasRecipient<TMessage>() => HasRecipient(typeof(TMessage));

    private HashSet<Type> Scan()
    {
        var found = new HashSet<Type>();
        var descriptors = accessor?.Services;
        if (descriptors is null) return found;

        foreach (var descriptor in descriptors)
        {
            var serviceType = descriptor.ServiceType;
            if (!serviceType.IsGenericType || serviceType.GetGenericTypeDefinition() != typeof(IRecipient<>))
                continue;

            found.Add(serviceType.GetGenericArguments()[0]);
        }

        return found;
    }
}
