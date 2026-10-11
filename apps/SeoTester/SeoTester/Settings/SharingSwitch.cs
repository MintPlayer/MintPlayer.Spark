using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Sharing;
using SeoTester.Entities;

namespace SeoTester.Settings;

/// <summary>
/// The administrator's sharing switch (<c>docs/seo_analyzer_PRD.md</c> D11, D31) for every share link and badge,
/// <see cref="SeoSettings.Sharing"/>:
/// <list type="bullet">
/// <item><c>Off</c> refuses new links and hides existing ones; switched back on, they work again.</item>
/// <item><c>VerifiedOnly</c> does the same per object for a site its organization has not verified (or no longer
/// has), and for a run of such a site: decided from the stored site, when a link is made and on every read.</item>
/// <item><c>AllSites</c> (the default, D11) shares any site.</item>
/// </list>
/// </summary>
internal sealed class SharingSwitch(ISparkSettings settings, Raven.Client.Documents.IDocumentStore store) : ISparkShareGate
{
    public async ValueTask<bool> IsEnabledAsync(string typeName, CancellationToken cancellationToken)
        => await settings.GetAsync(SeoSettings.Sharing, cancellationToken) != SeoFeatureScope.Off;

    public async ValueTask<bool> IsEnabledForAsync(string typeName, object entity, CancellationToken cancellationToken)
    {
        if (await settings.GetAsync(SeoSettings.Sharing, cancellationToken) != SeoFeatureScope.VerifiedOnly)
            return true;
        switch (entity)
        {
            case Site site:
                return site.IsVerified;
            case AuditRun { SiteId: { Length: > 0 } siteId }:
                using (var session = store.OpenAsyncSession())
                    return await session.LoadAsync<Site>(siteId, cancellationToken) is { IsVerified: true };
            default:
                return false;
        }
    }
}
