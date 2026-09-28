using MintPlayer.Spark.Moderation.Services;

namespace MintPlayer.Spark.Moderation;

/// <summary>
/// Runs moderation's background work on demand: the same code the Cron jobs run, for an operator
/// who changed a threshold and does not want to wait for the next occurrence, and for an end-to-end
/// test that must not wait at all (a privilege earned by a vote counts only from the crediting run
/// that credits it, every 5 minutes by default).
/// </summary>
/// <remarks>
/// <para>
/// Nothing here checks a right: the Cron jobs have no caller either. An application that exposes these
/// over HTTP decides who may call them (QnA maps them only for its end-to-end host).
/// </para>
/// <para>
/// Running a job out of schedule changes nothing about the rules: crediting still credits only the
/// entries whose <c>CreditableAfterUtc</c> has passed, and the detector judges the same window.
/// </para>
/// </remarks>
public interface ISparkModerationJobs
{
    /// <summary>
    /// Fraud measure 5: credits every ledger entry whose delay has passed and recomputes the
    /// recipients' summaries (what the privilege provider reads). The crediting job's body.
    /// </summary>
    /// <returns>How many entries were credited.</returns>
    Task<int> RunCreditingAsync(CancellationToken cancellationToken = default);

    /// <summary>Fraud measure 6: the detector the nightly job runs, over its usual window.</summary>
    Task<FraudDetectionReport> RunFraudDetectorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes the stored reputation summary of each user from the ledger indexes, without crediting
    /// anything. Summaries are derived data; recomputing one is always safe.
    /// </summary>
    Task RecomputeReputationAsync(IEnumerable<string> userIds, CancellationToken cancellationToken = default);
}
