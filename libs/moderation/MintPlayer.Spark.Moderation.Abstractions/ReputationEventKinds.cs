namespace MintPlayer.Spark.Moderation;

/// <summary>
/// The reputation events, as named under <c>Spark:Moderation:Reputation</c>. The configurable ones
/// carry points; <see cref="Reversal"/> and <see cref="Retraction"/> are compensating entries whose
/// points are always the negation of what they compensate.
/// </summary>
public static class ReputationEventKinds
{
    /// <summary>The author's post received an up-vote (default +10).</summary>
    public const string UpvoteReceived = "UpvoteReceived";

    /// <summary>The author's post received a down-vote (default −2).</summary>
    public const string DownvoteReceived = "DownvoteReceived";

    /// <summary>The voter cast a down-vote (default −1; only on the types in <c>DownvoteCastTypes</c>, all by default).</summary>
    public const string DownvoteCast = "DownvoteCast";

    /// <summary>A flag the user raised was upheld (default +2).</summary>
    public const string FlagUpheld = "FlagUpheld";

    /// <summary>A flag the user raised was declined (default 0).</summary>
    public const string FlagDeclined = "FlagDeclined";

    /// <summary>A compensating entry written by a fraud rule, a moderator, a merge or a deletion. Never configurable.</summary>
    public const string Reversal = "Reversal";

    /// <summary>A compensating entry written when a voter withdraws or changes a vote. Never configurable.</summary>
    public const string Retraction = "Retraction";

    /// <summary>The names <c>Spark:Moderation:Reputation</c> accepts; any other name is a startup error.</summary>
    public static readonly IReadOnlySet<string> Configurable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        UpvoteReceived, DownvoteReceived, DownvoteCast, FlagUpheld, FlagDeclined,
    };
}
