using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// Reads one repository's rules on its default branch and offers its managers the switch to one summary job when they
/// require one check per CI job (#714 Dec11); handled by <see cref="OfferSummaryJobRecipient"/>.
/// </summary>
/// <remarks>
/// The queue makes the same offer each time it reads the rules at enable. This message is the one-off pass after the deploy
/// (<c>M_202611271200_OfferSummaryJob</c>), so a quiet repository is told too. On <see cref="CoverageQueues.Publishing"/>
/// because it calls the forge, like <see cref="RefreshKnownChecksMessage"/>.
/// </remarks>
[MessageQueue(CoverageQueues.Publishing)]
public sealed record OfferSummaryJobMessage
{
    /// <summary>The repository document id, e.g. <c>Repositories/github/123</c>.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Once per repository: the offer itself is made once, so a second pass would only cost forge calls.</summary>
    public static string OnceKey(string repositoryId) => $"offer-summary-job-{repositoryId}";
}

/// <summary>
/// The one-off pass of Dec11: one read of the default branch's rules (<see cref="IForgeMergeQueue.GetBranchRulesAsync"/>,
/// the App's own token, no admin rights), then <see cref="IRulesetNotices.OfferSummaryJobAsync"/>. A disconnected or archived
/// repository, one without a default branch or a merge-queue forge, and one the forge refuses are skipped: the queue makes
/// the same offer at its next enable.
/// </summary>
public partial class OfferSummaryJobRecipient : IRecipient<OfferSummaryJobMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> forgeQueues;
    [Inject] private readonly IRulesetNotices notices;
    [Inject] private readonly ILogger<OfferSummaryJobRecipient> logger;

    public async Task HandleAsync(OfferSummaryJobMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (repository.DefaultBranch is not { Length: > 0 } branch) return;
        if (forgeQueues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } queue) return;

        ForgeBranchRules rules;
        try
        {
            rules = await queue.GetBranchRulesAsync(repository, branch, cancellationToken);
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException)
        {
            // Not worth a redelivery: the queue reads the same rules, and makes the same offer, at its next enable.
            logger.LogWarning(ex, "Could not read the branch rules of {Repository}", repository.FullName);
            return;
        }

        await notices.OfferSummaryJobAsync(repository, branch, rules, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
