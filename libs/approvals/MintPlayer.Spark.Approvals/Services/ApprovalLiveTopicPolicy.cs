using MintPlayer.Spark.Abstractions.Live;

namespace MintPlayer.Spark.Approvals.Services;

/// <summary>
/// <c>approvals:inbox</c> is a private stream only (<see cref="ApprovalNotices.InboxTopic"/>): a signed-in user follows
/// their own frames, nobody follows a public one, and a visitor without an account has no inbox.
/// </summary>
internal sealed class ApprovalLiveTopicPolicy : ISparkLiveTopicPolicy
{
    public string Prefix => "approvals:";

    public bool CanSubscribe(string topic, SparkLiveViewer viewer) => false;

    public bool CanSubscribePrivate(string topic, SparkLiveViewer viewer)
        => topic == ApprovalNotices.InboxTopic && viewer.UserId is { Length: > 0 };
}
