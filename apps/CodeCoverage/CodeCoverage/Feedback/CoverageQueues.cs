namespace CodeCoverage.Feedback;

/// <summary>
/// The queue names this application is allowed to use, and why there are only
/// two of them.
/// <para>
/// <b>RavenDB caps data subscriptions per database, and Spark creates one
/// subscription per distinct queue name.</b> This deployment runs on the
/// AGPL/open-source licence, whose limit is <c>MaxNumberOfSubscriptionsPerDatabase: 3</c>
/// — verified against the live server, which answers a create beyond the cap
/// with <c>402 Payment Required</c> and
/// <c>LicenseLimitException: The maximum number of subscriptions per database
/// cannot exceed the limit of: 3</c>.
/// </para>
/// <para>
/// One of those three is the webhook queue the framework itself declares
/// (<c>spark-github-all</c>), so the application gets <b>two</b>. It previously
/// declared five, and the surplus failed silently: the subscription was never
/// created, the worker started against a subscription that did not exist, died
/// as "non-recoverable", and the app went on looking healthy. Merged-PR build
/// deletion never ran in production for exactly this reason, unnoticed.
/// </para>
/// <para>
/// Sharing a queue is safe: a subscription selects <i>documents</i> by
/// <c>QueueName</c>, and the worker then dispatches each message to the
/// <c>IRecipient&lt;T&gt;</c> for the type recorded on the message itself. So
/// several message types on one queue keep their own separate recipients. What
/// sharing costs is isolation — messages on the same queue are processed in
/// order, so a slow handler delays its queue-mates.
/// </para>
/// <para>
/// <b>Do not add a third name.</b> Adding one silently kills a queue, and which
/// one dies depends on creation order. If a new queue is genuinely needed, the
/// licence has to change first.
/// </para>
/// </summary>
public static class CoverageQueues
{
    /// <summary>
    /// Report parsing, build finalization and commit assembly. Deliberately
    /// isolated: it is strict FIFO and latency-sensitive, and everything on
    /// <see cref="Publishing"/> makes network calls to GitHub.
    /// </summary>
    public const string Ingestion = "coverage-parse-session";

    /// <summary>
    /// Everything that talks to GitHub, plus retention. Named for the check-run
    /// publish because that is the subscription that already exists on the
    /// server — renaming it would require creating a new subscription, which is
    /// the very thing the cap forbids. The name is therefore narrower than the
    /// contents; this constant is the honest description.
    /// </summary>
    public const string Publishing = "coverage-publish-feedback";
}
