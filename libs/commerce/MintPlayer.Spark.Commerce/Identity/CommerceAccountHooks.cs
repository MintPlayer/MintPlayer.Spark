using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Identity;

/// <summary>GDPR Art. 15/20 export of a user's commerce data: the legal documents they accepted and the DSA notices
/// they filed (§4.15).</summary>
internal sealed class CommercePersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser> where TUser : SparkUser
{
    public string Name => "commerce";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        using var session = store.OpenAsyncSession();
        var acceptances = await session.Query<LegalAcceptance>().Where(a => a.SubjectId == userId).Take(1024).ToListAsync(cancellationToken);
        var notices = await session.Query<DsaNotice>().Where(n => n.ReporterUserId == userId).Take(1024).ToListAsync(cancellationToken);
        return new
        {
            LegalAcceptances = acceptances.Select(a => new { a.Kind, a.Version, a.DocumentId, a.AcceptedAt, a.Source }),
            DsaNotices = notices.Select(n => new { n.Id, n.TargetKind, n.TargetId, n.Reason, n.Explanation, n.ReceivedAt, Status = n.Status.ToString() }),
        };
    }
}

/// <summary>
/// Account deletion (GDPR Art. 17): the reporter's identity and contact data is removed from their DSA notices (the
/// notice itself is kept for its retention period, GDPR Art. 17(3)(b)/(e)); acceptances are kept as proof of what was
/// agreed (Art. 17(3)(e)), with the clicking user cleared.
/// </summary>
internal sealed class CommerceAccountDeletion<TUser>(IDocumentStore store) : ISparkAccountDeletionHandler<TUser> where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        using var session = store.OpenAsyncSession();
        foreach (var notice in await session.Query<DsaNotice>().Where(n => n.ReporterUserId == userId).Take(1024).ToListAsync(cancellationToken))
        {
            notice.ReporterUserId = null;
            notice.ReporterKey = "deleted";
            notice.ContactName = null;
            notice.ContactEmail = null;
        }
        foreach (var acceptance in await session.Query<LegalAcceptance>().Where(a => a.SubjectId == userId).Take(1024).ToListAsync(cancellationToken))
            acceptance.AcceptedBy = null;
        await session.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// An organization's deletion ends its relationship with the platform: the seller profile is kept, with
/// <see cref="SellerProfile.RelationshipEndedAt"/> set, so the trader data survives the 6 months DSA Art. 30(5)
/// requires and DAC7 records their floor; the retention job erases it after. Export returns the profile without the
/// identity document.
/// </summary>
internal sealed class CommerceOrganizationData(IDocumentStore store, TimeProvider clock) : IOrganizationDataHandler
{
    public string Name => "commerce";

    public async Task<object?> ExportAsync(Organization organization, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var profile = await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(organization.Id!), cancellationToken);
        return profile is null ? null : new { profile.Status, profile.TraderStatus, profile.Trader, profile.Dac7, profile.Verification, profile.ApprovedAt };
    }

    public async Task DeleteAsync(Organization organization, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var profile = await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(organization.Id!), cancellationToken);
        if (profile is null || profile.RelationshipEndedAt is not null) return;
        profile.RelationshipEndedAt = clock.GetUtcNow();
        profile.Status = SellerRegistrationStatus.Suspended;
        profile.StatusReason = "organization-deleted";
        await session.SaveChangesAsync(cancellationToken);
    }
}
