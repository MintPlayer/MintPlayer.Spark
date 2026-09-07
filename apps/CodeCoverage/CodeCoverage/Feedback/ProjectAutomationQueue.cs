namespace CodeCoverage.Feedback;

/// <summary>
/// The queue project-board automation runs on.
/// <para>
/// <b>Its own queue, not <see cref="CoverageQueues.Publishing"/>.</b> Until the messaging rework
/// this would have been impossible: Spark created one RavenDB data subscription per distinct queue
/// name, and the licence capped subscriptions per database at three, so a new name silently killed
/// an existing queue. Messaging now runs a single shared subscription with in-process per-queue
/// lanes, so a queue name costs nothing and the decision is about <em>ordering</em> alone.
/// </para>
/// <para>
/// On ordering, isolation is clearly right here. A queue is one FIFO lane, so sharing the
/// publishing queue would let a board reconciliation — several GraphQL round trips — delay a
/// check-run publish, and would let a wedged Projects V2 call stall coverage feedback that has
/// nothing to do with it. Board automation is also the lower-value half: coverage feedback is what
/// the app is for, and it must not queue behind a card move.
/// </para>
/// <para>
/// Nothing about a card move needs ordering against a coverage publish, which is the only reason
/// sharing would have been justified. The one ordering that does matter — deletion not overtaking
/// in-flight publishes for the same repository — is unaffected, because that stays on
/// <see cref="CoverageQueues.Publishing"/>.
/// </para>
/// </summary>
public static class ProjectAutomationQueue
{
    /// <summary>Project-board automation driven by repository webhook deliveries.</summary>
    public const string Name = "coverage-project-automation";
}
