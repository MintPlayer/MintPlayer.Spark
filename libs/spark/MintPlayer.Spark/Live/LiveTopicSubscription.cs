using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Live;

namespace MintPlayer.Spark.Live;

/// <summary>A topic's latest value, serialized once: what every subscriber is sent.</summary>
/// <param name="Seq">Increases with every publish (process-wide), so a subscriber knows whether it holds it.</param>
/// <param name="Data">The payload's UTF-8 JSON.</param>
/// <param name="At">When it was published (a <see cref="TimeProvider"/> timestamp).</param>
/// <param name="IsPrivate">Whether it is one identity's private value (those expire, see <see cref="SparkLiveOptions.PrivateTopicRetention"/>).</param>
internal sealed record LiveRetainedTopic(long Seq, byte[] Data, long At, bool IsPrivate);

/// <summary>
/// A subscription to a broadcast topic or to the socket identity's private stream on a topic (commerce PRD
/// X2): <c>topic</c> frames carrying the retained value, sent without a scope or a read.
/// </summary>
/// <remarks>
/// Evaluated by <see cref="LiveConnection"/> itself, never through <see cref="EvaluateAsync"/>: the dirty bit and
/// the throttle are the same as every other kind's, so a slow client costs one send of the latest value and
/// never a queue of them.
/// </remarks>
internal sealed class LiveTopicSubscription : LiveSubscription
{
    /// <summary>The longest topic name accepted.</summary>
    public const int MaxNameLength = 200;

    public LiveTopicSubscription(LiveConnection connection, string id, string topic, bool isPrivate)
        : base(connection, id)
    {
        Topic = topic;
        IsPrivate = isPrivate;
        HubKey = isPrivate ? PrivateKey(connection.Viewer.IdentityKey, topic) : PublicKey(topic);
    }

    public string Topic { get; }
    public bool IsPrivate { get; }

    /// <summary>Where the hub keeps this subscription and its retained value.</summary>
    public string HubKey { get; }

    /// <summary>The <see cref="LiveRetainedTopic.Seq"/> the client was last sent (worker only).</summary>
    public long LastSentSeq { get; set; }

    public static string PublicKey(string topic) => "topic\n" + topic;
    public static string PrivateKey(string identityKey, string topic) => "private\n" + identityKey + "\n" + topic;

    /// <summary>1–200 characters, none of them whitespace or control characters.</summary>
    public static bool IsValidName([NotNullWhen(true)] string? topic)
        => topic is { Length: > 0 and <= MaxNameLength } && !topic.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary><c>{"type":"topic","sub":…,"seq":…,"serverTime":…,"data":…}</c>, the data copied in as it was serialized.</summary>
    public byte[] Frame(LiveRetainedTopic value, long serverTime)
    {
        var buffer = new ArrayBufferWriter<byte>(value.Data.Length + 128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "topic");
            writer.WriteString("sub", Id);
            writer.WriteNumber("seq", value.Seq);
            writer.WriteNumber("serverTime", serverTime);
            writer.WritePropertyName("data");
            writer.WriteRawValue(value.Data, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public override Task<LiveOutcome> EvaluateAsync(IServiceProvider services, CancellationToken cancellationToken)
        => throw new NotSupportedException("A topic is sent by the connection from the hub's retained value, without a read.");
}

/// <summary>
/// <see cref="ISparkLiveTopics"/>: serializes a payload once, with the HTTP JSON options (camelCase, the boundary
/// net included, so a persistent object nobody was presented cannot leak through a topic), and hands it to
/// the hub.
/// </summary>
internal sealed class LiveTopics(LiveHub hub, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json) : ISparkLiveTopics
{
    public void Publish(string topic, object payload)
    {
        Validate(topic);
        hub.PublishTopic(LiveTopicSubscription.PublicKey(topic), Serialize(payload), isPrivate: false);
    }

    public void PublishPrivate(string identityKey, string topic, object payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityKey);
        if (!identityKey.StartsWith("user:", StringComparison.Ordinal) && !identityKey.StartsWith("visitor:", StringComparison.Ordinal))
            throw new ArgumentException(
                $"A private stream belongs to a user ('user:…') or a visitor ('visitor:…'), not to '{identityKey}': "
                + "everyone behind one address would share it.", nameof(identityKey));
        Validate(topic);
        hub.PublishTopic(LiveTopicSubscription.PrivateKey(identityKey, topic), Serialize(payload), isPrivate: true);
    }

    private static void Validate(string topic)
    {
        if (!LiveTopicSubscription.IsValidName(topic))
            throw new ArgumentException(
                $"A topic name is 1–{LiveTopicSubscription.MaxNameLength} characters without whitespace or control characters.", nameof(topic));
    }

    private byte[] Serialize(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), json.Value.SerializerOptions);
    }
}
