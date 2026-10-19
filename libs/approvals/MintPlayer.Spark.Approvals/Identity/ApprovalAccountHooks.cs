using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Approvals.Identity;

/// <summary>
/// An account's deletion: the requests it raised are deleted (their subject's own library cleans up the subject), and
/// its id is taken off the requests it decided or could see. Pending requests it raised go without running a handler,
/// as <see cref="ISparkApprovals.ClosePendingAsync{TKind}"/> does.
/// </summary>
internal sealed class ApprovalAccountDeletion<TUser>(IDocumentStore store) : ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;

        // Random ids: by query, in pages, waiting for the index so none is missed.
        while (true)
        {
            using var page = store.OpenAsyncSession();
            var batch = await page.Query<ApprovalRequest>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(r => r.RequestedBy == userId || r.DecidedBy == userId || r.DeciderIds.Any(d => d == userId) || r.VisibleTo.Any(v => v == userId))
                .Take(512)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                break;
            foreach (var request in batch)
            {
                if (request.RequestedBy == userId)
                {
                    page.Delete(request);
                    continue;
                }
                if (request.DecidedBy == userId)
                    request.DecidedBy = null;
                request.DeciderIds.Remove(userId);
                request.VisibleTo.Remove(userId);
                foreach (var change in request.History.Where(e => e.By == userId))
                    change.By = null;
            }
            await page.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>The account's export: the requests it raised, as their requester sees them.</summary>
internal sealed class ApprovalPersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser>
    where TUser : SparkUser
{
    public string Name => "approvals";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return null;
        using var session = store.OpenAsyncSession();
        var requests = await session.Query<ApprovalRequest>()
            .Where(r => r.RequestedBy == userId)
            .OrderByDescending(r => r.RequestedAtUtc)
            .Take(1024)
            .ToListAsync(cancellationToken);
        return requests.Select(r => new { r.Kind, r.Title, r.Status, r.RequestedAtUtc, r.DecidedAtUtc, r.Reason }).ToList();
    }
}
