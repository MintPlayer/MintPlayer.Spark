using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Deletes the per-queue subscription definitions left behind by the pre-single-subscription
/// design, so they stop consuming the database's subscription budget.
/// <para>
/// <b>Runs on every startup, not once.</b> A migration with an applied-once marker was the obvious
/// home and is the wrong one: a stale definition can come back — an older build booting against the
/// same database re-creates its own — and a once-ever cleanup would never remove it again. Deleting
/// is idempotent (a missing name does not throw) and measured at about 2 ms per definition, so
/// running it every boot costs nothing and self-heals.
/// </para>
/// <para>
/// It also lives here, in the messaging host's async startup, rather than in the Spark middleware
/// registry. The registry hands out an <c>Action&lt;IApplicationBuilder&gt;</c>, so anything async
/// placed there has to block on <c>.GetAwaiter().GetResult()</c> — which is exactly why
/// <c>SparkMigrationRunner</c> does. Here the prune and the create that follows it are ordinary
/// straight-line async code in one method, so the ordering needs no argument to defend it.
/// </para>
/// </summary>
internal sealed partial class LegacySubscriptionCleanup
{
    /// <summary>
    /// Names starting with this are the old per-queue definitions (<c>SparkMessaging-{queue}</c>).
    /// <para>
    /// ⚠ <b>The trailing hyphen is load-bearing.</b> The live subscription is named
    /// <see cref="MessageFeeder.SubscriptionNameConstant"/> — <c>"SparkMessaging"</c>, with no
    /// hyphen — so it does not match this prefix and survives. Verified directly: with
    /// <c>SparkMessaging</c>, <c>SparkMessaging-Legacy1</c> and <c>SparkMessaging-Legacy2</c>
    /// present, an ordinal prefix match selects exactly the two legacy names. If either name ever
    /// changes, change them together: a cleanup that matches the live subscription deletes it on
    /// every boot, and the worker treats that as non-recoverable, so the queue dies silently and
    /// stays dead across restarts.
    /// </para>
    /// </summary>
    private const string LegacyPrefix = MessageFeeder.SubscriptionNameConstant + "-";

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILogger<LegacySubscriptionCleanup> logger;

    /// <summary>
    /// Removes every legacy per-queue definition. Returns how many were deleted, for tests and for
    /// the startup log line.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await documentStore.Subscriptions.GetSubscriptionsAsync(0, 1024, token: cancellationToken);

            // Prefix, never a hardcoded list: the names embed queue names, including
            // assembly-qualified generic arguments, and the set that accumulated in production was
            // not the set anyone would have written down.
            var legacy = existing
                .Where(s => s.SubscriptionName?.StartsWith(LegacyPrefix, StringComparison.Ordinal) == true)
                .Select(s => s.SubscriptionName!)
                .ToList();

            foreach (var name in legacy)
            {
                await documentStore.Subscriptions.DeleteAsync(name, token: cancellationToken);
                logger.LogInformation("Deleted legacy per-queue subscription '{SubscriptionName}'", name);
            }

            if (legacy.Count > 0)
            {
                logger.LogInformation(
                    "Removed {Count} legacy per-queue subscription definition(s); all queues now share '{SubscriptionName}'",
                    legacy.Count, MessageFeeder.SubscriptionNameConstant);
            }

            return legacy.Count;
        }
        catch (Exception ex)
        {
            // Never fail startup for this. The consequence of not pruning is that the old
            // definitions keep occupying subscription slots — which is bad, and is why this exists
            // — but refusing to boot over it would turn a recoverable licence-budget problem into
            // an outage. The create that follows fails loudly on its own if the budget is gone.
            logger.LogError(ex, "Could not clean up legacy per-queue subscriptions; continuing startup");
            return 0;
        }
    }
}
