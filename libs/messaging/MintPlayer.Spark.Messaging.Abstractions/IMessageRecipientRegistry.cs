namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Answers "does anything in this app consume messages of this type?".
/// <para>
/// A broadcast to a message type with no <see cref="IRecipient{TMessage}"/> is not merely useless:
/// it stores a document that nothing will ever drain. Queue workers are started per registered
/// recipient, so a message on an unsubscribed queue is retained until someone deletes it by hand.
/// A publisher that broadcasts speculatively — one envelope per shape, in the hope that some app
/// subscribes to one of them — therefore needs to ask first.
/// </para>
/// <para>
/// The set is built once at startup from the registered <see cref="IRecipient{TMessage}"/>
/// descriptors and never changes, so this is a hash lookup rather than a DI resolution: it is safe
/// on a per-message path. Closed generic message types are distinct keys, so
/// <c>IRecipient&lt;Envelope&lt;A&gt;&gt;</c> does not make <c>Envelope&lt;B&gt;</c> consumed.
/// </para>
/// </summary>
public interface IMessageRecipientRegistry
{
    /// <summary>
    /// True when at least one <see cref="IRecipient{TMessage}"/> is registered for
    /// <paramref name="messageType"/>. False for a null type.
    /// </summary>
    bool HasRecipient(Type? messageType);

    /// <summary>
    /// True when at least one <see cref="IRecipient{TMessage}"/> is registered for
    /// <typeparamref name="TMessage"/>.
    /// </summary>
    bool HasRecipient<TMessage>();
}
