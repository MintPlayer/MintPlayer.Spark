using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Moderation.Documents;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// "Content deleted by a moderator reverses its votes" (§3.12), as a durable after-commit hook (#482,
/// D17): the delete and the reversal can no longer disagree. Before, the reversal ran in the request
/// after the commit, and a failure there was logged and lost.
/// </summary>
/// <remarks>
/// <see cref="ModerationInterceptor"/> decides in its before-delete hook — it knows the post's author
/// and the actor — and records <see cref="ReverseVotesFact"/>. The reversal is idempotent per entry, so
/// a redelivery repeats nothing.
/// </remarks>
internal sealed partial class ModerationVoteReversal : IAfterDeleteCommitted<IModeratable>
{
    /// <summary>Set by the before-delete hook when this delete must reverse the post's votes.</summary>
    public const string ReverseVotesFact = "Moderation.ReverseVotes";

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ReputationLedger ledger;

    public async Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        if (!change.Facts.ContainsKey(ReverseVotesFact))
            return;

        using var read = documentStore.OpenAsyncSession();
        var voteIds = await read.Query<ModerationVote>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(v => v.TargetId == change.Id && v.Direction != 0)
            .Select(v => v.Id!)
            .Take(10_000)
            .ToListAsync(cancellationToken);

        if (voteIds.Count > 0)
            await ledger.ReverseVotesAsync(voteIds, "content-deleted", null, cancellationToken);
    }
}
