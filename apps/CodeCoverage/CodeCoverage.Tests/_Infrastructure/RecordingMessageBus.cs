using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Tests;

/// <summary>
/// An <see cref="IMessageBus"/> that records what was sent, and how.
/// </summary>
/// <remarks>
/// Several test files carry a private copy of this; new tests use this one. The existing copies
/// are left where they are rather than converted wholesale.
/// </remarks>
public sealed class RecordingMessageBus : IMessageBus
{
    // Routes the options overload onto the three this fake records, so it sees every publish.
    public Task BroadcastAsync<TMessage>(TMessage message, BroadcastOptions options, CancellationToken cancellationToken = default)
        => options.DeduplicationKey is { } key ? BroadcastOnceAsync(message, key, cancellationToken)
         : options.Delay is { } delay ? DelayBroadcastAsync(message, delay, cancellationToken)
         : BroadcastAsync(message, cancellationToken);

    /// <summary>Every message, in the order sent, whatever the method.</summary>
    public List<object> Messages { get; } = [];

    /// <summary>The deduplication keys passed to <see cref="BroadcastOnceAsync{TMessage}"/>, in order.</summary>
    public List<string> DeduplicationKeys { get; } = [];

    /// <summary>The delays passed to <see cref="DelayBroadcastAsync{TMessage}"/>, in order.</summary>
    public List<TimeSpan> Delays { get; } = [];

    /// <summary>
    /// Drop a <see cref="BroadcastOnceAsync{TMessage}"/> whose key was seen before, as the real bus
    /// does while the first message's document exists. Off by default.
    /// </summary>
    public bool Deduplicates { get; set; }

    public IEnumerable<T> Of<T>() => Messages.OfType<T>();

    public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        => Record(message);

    public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
    {
        var duplicate = DeduplicationKeys.Contains(deduplicationKey);
        DeduplicationKeys.Add(deduplicationKey);
        return Deduplicates && duplicate ? Task.CompletedTask : Record(message);
    }

    public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        Delays.Add(delay);
        return Record(message);
    }

    private Task Record<TMessage>(TMessage message)
    {
        if (message is not null) Messages.Add(message);
        return Task.CompletedTask;
    }
}
