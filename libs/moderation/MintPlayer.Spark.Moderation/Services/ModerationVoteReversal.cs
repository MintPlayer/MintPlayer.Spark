using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Moderation.Indexes;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// "Content deleted by a moderator reverses its votes" (§3.12), as a durable after-commit interceptor (#482,
/// D17): the delete and the reversal can no longer disagree. Before, the reversal ran in the request
/// after the commit, and a failure there was logged and lost.
/// </summary>
/// <remarks>
/// <see cref="ModerationInterceptor"/> decides in its before-delete interceptor — it knows the post's author
/// and the actor — and records <see cref="ReverseVotesFact"/>. The reversal is idempotent per entry, so
/// a redelivery repeats nothing.
/// </remarks>
internal sealed partial class ModerationVoteReversal : IAfterDeleteCommitted<IModeratable>
{
    /// <summary>Set by the before-delete interceptor when this delete must reverse the post's votes.</summary>
    public const string ReverseVotesFact = "Moderation.ReverseVotes";

    [Inject] private readonly ISparkStore store;
    [Inject] private readonly ReputationLedger ledger;

    public async Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        if (!change.Facts.ContainsKey(ReverseVotesFact))
            return;

        using var read = store.OpenSession();
        // The view's id column is a plain string (s.IdOf), so it projects; the document's typed id would not (#536 D23).
        var targetId = change.Id;
        var voteIds = (await read.ToListAsync(read.Query<Moderation_Votes.Result, Moderation_Votes>(ReputationLedger.IndexWait)
            .Where(v => v.TargetId == targetId && v.Direction != 0)
            .Select(v => v.Id)
            .Take(10_000), cancellationToken)).OfType<string>().ToList();

        if (voteIds.Count > 0)
            await ledger.ReverseVotesAsync(voteIds, "content-deleted", null, cancellationToken);
    }
}
