using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.WaitingRoom.Contracts;

namespace MintPlayer.Spark.WaitingRoom.Live;

/// <summary>
/// The waiting room's live topics (X2): the public <c>waiting-room:{key}</c> carries the room's progress for everyone
/// (<see cref="WaitingRoomFrame"/>, so a million viewers cost one publish); the private stream of the same name carries
/// one identity's call and admission (<see cref="WaitingRoomPrivateFrame"/>). Frames carry <c>serverTime</c>, so the page
/// counts down against the server's clock.
/// </summary>
internal sealed class WaitingRoomPublisher(ISparkLiveTopics topics, ILogger<WaitingRoomPublisher> logger)
{
    public const string TopicPrefix = "waiting-room:";

    public static string Topic(string roomKey) => TopicPrefix + roomKey;

    public void PublishRoom(WaitingRoomFrame frame)
    {
        try
        {
            topics.Publish(Topic(frame.Key), frame);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publishing waiting room {Room} failed; its topic stays stale until the next tick.", frame.Key);
        }
    }

    public void PublishPrivate(string roomKey, string identityKey, WaitingRoomPrivateFrame frame)
    {
        if (identityKey.StartsWith("ip:", StringComparison.Ordinal))
            return;
        try
        {
            topics.PublishPrivate(identityKey, Topic(roomKey), frame);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publishing to an entry of waiting room {Room} failed; the page still sees it on its next poll.", roomKey);
        }
    }
}

/// <summary>Anyone may follow a room's progress; a user or visitor may follow its own private stream (the socket's identity keys it).</summary>
internal sealed class WaitingRoomTopicPolicy : ISparkLiveTopicPolicy
{
    public string Prefix => WaitingRoomPublisher.TopicPrefix;

    public bool CanSubscribe(string topic, SparkLiveViewer viewer) => topic.Length > Prefix.Length;

    public bool CanSubscribePrivate(string topic, SparkLiveViewer viewer) => topic.Length > Prefix.Length && !viewer.IsAddressOnly;
}
