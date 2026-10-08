namespace QnA.Testing;

/// <summary>
/// End-to-end test seams: run Moderation's two Cron jobs now instead of waiting for their schedule.
/// A privilege earned by a vote counts only from the crediting run that credits it (every 5 minutes),
/// and the fraud detector runs nightly, so a test that waited would be slow and still racy.
/// </summary>
/// <remarks>
/// <para>
/// Off unless <c>QnA:TestSeams:Enabled</c> is true, which only the E2E host's generated settings set.
/// Refused at startup in Production. Even when on, only a moderator (a holder of
/// <c>Audit/Moderation</c>) gets an answer other than 404, and every call needs the antiforgery token.
/// </para>
/// <para>
/// The seams are the endpoints of <see cref="QnATestSeamsGroup"/>. They call
/// <see cref="MintPlayer.Spark.Moderation.ISparkModerationJobs"/> — the jobs' own code — so the rules
/// are exactly the scheduled ones: crediting still credits only entries whose delay has passed.
/// </para>
/// </remarks>
public static class QnATestSeams
{
    public const string EnabledKey = "QnA:TestSeams:Enabled";

    /// <summary>Whether the seams are on. Throws when they are configured on in Production.</summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>(EnabledKey))
            return false;
        if (environment.IsProduction())
            throw new InvalidOperationException($"'{EnabledKey}' is set in Production. The QnA test seams run Moderation's jobs on request; they exist for the E2E host only.");
        return true;
    }
}
