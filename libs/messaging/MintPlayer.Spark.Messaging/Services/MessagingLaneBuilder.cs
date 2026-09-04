using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

internal sealed class MessagingLaneBuilder(LaneRegistry registry) : IMessagingLaneBuilder
{
    public TimeSpan PartitionBlockBudget { get; private set; } = TimeSpan.FromMinutes(15);

    public IQueueBuilder Queue<TMessage>()
    {
        var laneName = QueueNames.ForMessageType(typeof(TMessage));
        registry.BindMessageTypeToLane(typeof(TMessage), laneName);
        return registry.Declare(laneName);
    }

    public IQueueBuilder Queue(string laneName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laneName);

        // The name is no longer interpolated into a subscription query, so RQL injection is not the
        // risk it was — but a name that reaches configuration and metrics still has to be a name.
        if (!QueueNames.IsValid(laneName))
            throw new ArgumentException(
                $"'{laneName}' is not a valid lane name. Lane names must match [A-Za-z0-9._+`-]+.", nameof(laneName));

        return registry.Declare(laneName);
    }

    public IMessagingLaneBuilder MaxPartitionBlock(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        PartitionBlockBudget = budget;
        return this;
    }
}
