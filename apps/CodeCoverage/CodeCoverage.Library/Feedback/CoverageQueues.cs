namespace CodeCoverage.Feedback;

/// <summary>
/// The queue names this application uses.
/// <para>
/// <b>The subscription cap no longer scales with queue count.</b> Spark's messaging host
/// now runs <em>one</em> RavenDB data subscription (<c>SparkMessaging</c>) for every queue,
/// with in-process pumps providing per-queue FIFO, so adding a queue name costs no
/// subscription. Declaring a new queue here is an ordinary modelling decision again.
/// </para>
/// <para>
/// The history is worth keeping, because it explains the shape of this file and cost real
/// production outages. RavenDB caps data subscriptions per database — 3 on the licence this
/// deployment is registered under (Community; the earlier claim of "AGPL/open-source" in
/// this comment was wrong, and the server reports <c>Commercial / Community</c>). Spark used
/// to create one subscription <em>per distinct queue name</em>, so the cap was a hard budget
/// on how many queues an app could have. Seven <c>SparkMessaging-*</c> definitions had been
/// declared over time and five of them were silently dead: the create failed with
/// <c>402 Payment Required</c> / <c>LicenseLimitException</c>, the worker started against a
/// subscription that did not exist, died as "non-recoverable", and the app went on looking
/// healthy. Merged-PR build deletion never ran in production for exactly that reason,
/// unnoticed for months.
/// </para>
/// <para>
/// Sharing a queue is still safe, and still costs isolation: a subscription selects
/// <i>documents</i>, and the pump dispatches each message to the <c>IRecipient&lt;T&gt;</c>
/// for the type recorded on the message itself, so several message types on one queue keep
/// their own recipients. But one queue is one FIFO lane, so a slow handler still delays its
/// queue-mates. That — not the licence — is now the only reason to split or share a name.
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
    /// Everything that talks to GitHub, plus retention. The name is narrower than the
    /// contents — it was chosen when renaming a queue meant creating a subscription and the
    /// cap forbade it. That constraint is gone, so this name is now merely historical rather
    /// than forced; renaming it is safe whenever the in-flight <c>SparkMessages</c> documents
    /// carrying the old <c>QueueName</c> are drained or migrated first.
    /// </summary>
    public const string Publishing = "coverage-publish-feedback";

    /// <summary>
    /// Commit-graph mirror clones, fetches and deletions (#504). Its own lane because a first
    /// clone may legitimately run for the whole fetch quota (minutes), and nothing on
    /// <see cref="Publishing"/> should wait behind it. Deletions share this lane on purpose: FIFO
    /// keeps a deletion behind a fetch queued before it, never ahead of one.
    /// </summary>
    public const string CommitGraph = "coverage-commit-graph";

    /// <summary>
    /// The merge queue's saga (merge-queue PRD §5.5, M5): its steps, and the webhook signals that wake it. One lane, so
    /// every change to a queue's state is serialised with every other, which is what keeps each base branch's queue
    /// serial (D11) without a lock. A step runs at most one git-writer job (≤ 240 s), under the 5-minute claim.
    /// </summary>
    public const string MergeQueue = "coverage-merge-queue";
}
