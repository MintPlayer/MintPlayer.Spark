using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Approvals.Models;
using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Organizations.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Organizations.Migrations;

/// <summary>
/// Join requests are decision requests since D26 (<c>docs/commerce_PRD.md</c> §4.13, §8): every pending join request
/// stored the old way (an <c>OrganizationInvitation</c> with <c>Kind = JoinRequest</c>) becomes an
/// <see cref="ApprovalRequest"/> of kind <see cref="OrganizationJoinApproval"/>, and the old document goes. The work is
/// <see cref="OrganizationJoinRequestUpgrade"/>.
/// </summary>
/// <remarks>
/// Post-deploy check: <c>from OrganizationInvitations where Kind = 'JoinRequest'</c> returns nothing, and
/// <c>from ApprovalRequests where Kind = 'organizations.join' and Data.legacyTokenHash != null</c> holds one request
/// per join request that was pending at the deploy.
/// </remarks>
public partial class M_202610191700_OrganizationJoinRequestsToApprovals : ISparkMigration
{
    public static string? Description => "Organizations: pending join requests become approval requests (D26)";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IOptions<SparkApprovalsOptions> approvalOptions;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<M_202610191700_OrganizationJoinRequestsToApprovals> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var result = await OrganizationJoinRequestUpgrade.RunAsync(store, timeProvider.GetUtcNow().UtcDateTime, approvalOptions.Value.Retention, cancellationToken);
        logger.LogInformation(
            "Organizations: {Converted} pending join request(s) became approval requests; {Expired} expired and {Orphaned} without organization or requester were deleted.",
            result.Converted, result.Expired, result.Orphaned);
    }
}

/// <summary>
/// The rewrite of <see cref="M_202610191700_OrganizationJoinRequestsToApprovals"/>. A pending, unexpired join request
/// becomes a pending <see cref="ApprovalRequest"/>:
/// <list type="bullet">
/// <item>its id is <c>ApprovalRequests/{the invitation's guid}</c>, so the old mailed link (which names the invitation)
/// finds it, and a rerun cannot convert one twice;</item>
/// <item>the deciders are the organization's Owners and Admins now, its deadline is the invitation's expiry, and
/// <see cref="ApprovalRequest.NotifiedAtUtc"/> is set, so nobody is mailed again (the Owners were mailed the link);</item>
/// <item><c>Data.legacyTokenHash</c> keeps the token's hash: the old link opens the request page (<see cref="SparkOrganizations.AcceptAsync"/>).</item>
/// </list>
/// An expired one, or one whose organization or requester is gone, is only deleted. Each conversion commits the new
/// request and the deletion together. Idempotent: a second run finds no old join request and changes nothing.
/// </summary>
internal static class OrganizationJoinRequestUpgrade
{
    /// <summary>The collection the pre-D26 code stored join requests in (with the invitations).</summary>
    internal const string LegacyCollection = "OrganizationInvitations";

    /// <summary>The stored <c>Kind</c> of a pre-D26 join request (the enum was saved by name).</summary>
    internal const string LegacyJoinRequestKind = "JoinRequest";

    internal sealed record Result(int Converted, int Expired, int Orphaned);

    public static async Task<Result> RunAsync(IDocumentStore store, DateTime utcNow, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        // A scan of the collection, never a filtered query: that would make RavenDB build an auto index on every
        // database (indexes belong to the application, D-O22). The collection holds only the pending invitations.
        var legacy = new List<LegacyJoinRequest>();
        using (var reader = store.OpenAsyncSession())
        {
            for (var skip = 0; ; skip += 1024)
            {
                var page = await reader.Query<LegacyJoinRequest>(collectionName: LegacyCollection)
                    .Skip(skip).Take(1024)
                    .ToListAsync(cancellationToken);
                legacy.AddRange(page.Where(i => i.Id is { Length: > 0 } && i.Kind == LegacyJoinRequestKind));
                if (page.Count < 1024)
                    break;
            }
        }

        int converted = 0, expired = 0, orphaned = 0;
        foreach (var old in legacy)
        {
            using var session = store.OpenAsyncSession();
            var outcome = await ConvertAsync(session, old, utcNow, retention, cancellationToken);
            session.Delete(old.Id!);
            await session.SaveChangesAsync(cancellationToken);
            switch (outcome)
            {
                case Outcome.Converted: converted++; break;
                case Outcome.Expired: expired++; break;
                case Outcome.Orphaned: orphaned++; break;
            }
        }
        return new Result(converted, expired, orphaned);
    }

    /// <summary>The request a migrated join request becomes: <c>ApprovalRequests/</c> + the invitation id's guid.</summary>
    internal static string RequestIdFor(string invitationId)
    {
        var slash = invitationId.IndexOf('/');
        return "ApprovalRequests/" + (slash >= 0 ? invitationId[(slash + 1)..] : invitationId);
    }

    private enum Outcome { Converted, Expired, Orphaned, AlreadyConverted }

    private static async Task<Outcome> ConvertAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, LegacyJoinRequest old, DateTime utcNow, TimeSpan retention, CancellationToken cancellationToken)
    {
        // Raw documents are not filtered by @expires: one past its expiry waits for the server's sweep.
        if (old.ExpiresAtUtc is not { } expires || expires <= utcNow)
            return Outcome.Expired;
        if (old.OrganizationId is not { Length: > 0 } organizationId || old.UserId is not { Length: > 0 } requesterId || old.TokenHash is not { Length: > 0 })
            return Outcome.Orphaned;
        var organization = await session.LoadAsync<Organization>(organizationId, cancellationToken);
        if (organization is null)
            return Outcome.Orphaned;

        var requestId = RequestIdFor(old.Id!);
        if (await session.LoadAsync<ApprovalRequest>(requestId, cancellationToken) is not null)
            return Outcome.AlreadyConverted;

        var role = OrganizationJoinRequests.RoleOf(new Dictionary<string, string?> { [OrganizationJoinRequests.RoleKey] = old.Role });
        var name = old.UserName ?? old.Email ?? requesterId;
        var requestedAt = old.CreatedAtUtc ?? utcNow;
        var deciders = (await OrganizationJoinRequests.DeciderIdsAsync(session, organizationId, cancellationToken))
            .Where(id => id != requesterId)
            .ToList();
        var subject = OrganizationJoinRequests.SubjectOf(organizationId, requesterId);

        var request = new ApprovalRequest
        {
            Id = requestId,
            Kind = OrganizationJoinApproval.Name,
            SubjectType = subject.Type,
            SubjectId = subject.Id,
            SubjectKey = subject.Key,
            // ApprovalVisibility.KindAndType: what a per-type decider scope matches.
            KindAndType = OrganizationJoinApproval.Name + "|" + subject.Type,
            Status = ApprovalStatuses.Pending,
            Title = OrganizationJoinRequests.Title(name, organization.Name),
            Summary = OrganizationJoinRequests.Summary(name, old.Email, organization.Name, role),
            Url = OrganizationJoinRequests.OrganizationUrl(organizationId),
            OrganizationId = organizationId,
            RequestedBy = requesterId,
            RequestedAtUtc = requestedAt,
            DeciderIds = deciders,
            DecideByUtc = expires,
            // The Owners were mailed when it was raised: the sweep must not tell anyone again.
            NotifiedAtUtc = utcNow,
            Data = new Dictionary<string, string?>
            {
                [OrganizationJoinRequests.RoleKey] = role.ToString(),
                [OrganizationJoinRequests.LegacyTokenHashKey] = old.TokenHash,
            },
            History = [new ApprovalRequestEvent { Status = ApprovalStatuses.Pending, By = requesterId, AtUtc = requestedAt }],
            ExpiresAtUtc = expires + retention,
        };
        await session.StoreAsync(request, requestId, cancellationToken);
        return Outcome.Converted;
    }

    /// <summary>A join request as the pre-D26 code stored it (<c>OrganizationInvitation</c> with its requester fields).</summary>
    internal sealed class LegacyJoinRequest
    {
        public string? Id { get; set; }
        public string? OrganizationId { get; set; }
        public string? Kind { get; set; }
        public string? Email { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? Role { get; set; }
        public string? TokenHash { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? CreatedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
    }
}
