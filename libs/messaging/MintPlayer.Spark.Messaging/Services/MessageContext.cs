using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Scoped <see cref="IMessageContext"/>. <see cref="MessageProcessor"/> fills it for the scope it
/// creates per message, before any handler runs.
/// </summary>
internal sealed class MessageContext : IMessageContext
{
    private SparkMessage? message;

    internal void Set(SparkMessage current) => message = current;

    private SparkMessage Current
        => message ?? throw new InvalidOperationException("IMessageContext can only be used inside a message handler.");

    public string MessageId => Current.Id!;
    public string QueueName => Current.QueueName;
    public int AttemptCount => Current.AttemptCount;
    public DateTime? ExpiresAtUtc => Current.ExpiresAtUtc;
}
