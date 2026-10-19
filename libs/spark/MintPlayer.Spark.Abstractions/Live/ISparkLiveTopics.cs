namespace MintPlayer.Spark.Abstractions.Live;

/// <summary>
/// Who a live socket belongs to, frozen when it connected (commerce PRD X2). A socket lives at most
/// <c>Spark:Live:MaxSocketLifetime</c> and the client reconnects after signing in, so it is never staler than
/// that.
/// </summary>
/// <param name="IdentityKey"><c>user:{id}</c>, <c>visitor:{id}</c> or <c>ip:{address or /64}</c> (<see cref="Visitor.ISparkVisitor.IdentityKey"/>).</param>
/// <param name="UserId">The signed-in user, if any.</param>
/// <param name="VisitorId">The visitor cookie's id, if the socket carried a valid one.</param>
public sealed record SparkLiveViewer(string IdentityKey, string? UserId, string? VisitorId)
{
    /// <summary>
    /// Whether the viewer is known only by its network address. Many people share one address (a carrier's
    /// NAT, an office), so such a viewer never gets a private stream.
    /// </summary>
    public bool IsAddressOnly => IdentityKey.StartsWith("ip:", StringComparison.Ordinal);
}

/// <summary>
/// Broadcast topics on the live socket (commerce PRD X2, <c>docs/guide-live-updates.md</c> "Topics"): one
/// value published once and fanned out to every subscriber with <b>no per-viewer read</b>, for state that is
/// the same for everyone (a queue's progress, the seats left) or that belongs to one identity (an admission).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>A topic payload is public by construction.</b> It bypasses row security, redaction and attribute
/// rights, which every persistent-object and query push goes through. Publish only what anyone allowed by
/// the topic's <see cref="ISparkLiveTopicPolicy"/> may see, as a small DTO, never a persistent object.
/// </para>
/// <para>
/// The latest value of a topic is retained: a subscriber that joins later, or reconnects, gets it at once.
/// A slow subscriber gets only the latest value, never a queue of them. Frames carry the server's clock
/// (<c>serverTime</c>, Unix milliseconds) so a client can count down against the server, not its own clock.
/// </para>
/// <para>
/// In-process: a value published on one instance reaches the sockets of that instance.
/// </para>
/// </remarks>
public interface ISparkLiveTopics
{
    /// <summary>
    /// Publishes <paramref name="payload"/> on <paramref name="topic"/>: serialized once, retained, and sent to
    /// every subscriber (latest wins).
    /// </summary>
    void Publish(string topic, object payload);

    /// <summary>
    /// Publishes <paramref name="payload"/> on <paramref name="topic"/> to every socket of one identity only
    /// (<c>user:{id}</c> or <c>visitor:{id}</c>), retained for <c>Spark:Live:PrivateTopicRetention</c> so a
    /// reconnecting socket still gets it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="identityKey"/> is an address key (<c>ip:…</c>): a shared
    /// address must never share a private stream.</exception>
    void PublishPrivate(string identityKey, string topic, object payload);
}

/// <summary>
/// Decides who may subscribe to the topics under one <see cref="Prefix"/> (<c>"waitingroom:"</c>). A topic no
/// policy claims cannot be subscribed to, and a refusal is the same <c>unavailable</c> answer as an unknown
/// topic, so the socket is no existence oracle. Registered as a singleton
/// (<c>services.AddSingleton&lt;ISparkLiveTopicPolicy, …&gt;()</c>); the longest matching prefix decides.
/// </summary>
/// <remarks>
/// Asked on subscribe and again every <c>Spark:Live:Revalidate</c>. Keep it cheap and in memory: it runs for
/// every socket, and it gets no request scope.
/// </remarks>
public interface ISparkLiveTopicPolicy
{
    /// <summary>The topic prefix this policy governs, e.g. <c>"waitingroom:"</c>.</summary>
    string Prefix { get; }

    /// <summary>Whether <paramref name="viewer"/> may follow the public topic <paramref name="topic"/>. A public topic answers <see langword="true"/> for anonymous viewers.</summary>
    bool CanSubscribe(string topic, SparkLiveViewer viewer);

    /// <summary>
    /// Whether <paramref name="viewer"/> may follow its own private stream on <paramref name="topic"/>. Asked
    /// only for a viewer with a user or visitor identity (an address-only viewer is refused before). By
    /// default the same answer as <see cref="CanSubscribe"/>.
    /// </summary>
    bool CanSubscribePrivate(string topic, SparkLiveViewer viewer) => CanSubscribe(topic, viewer);
}
