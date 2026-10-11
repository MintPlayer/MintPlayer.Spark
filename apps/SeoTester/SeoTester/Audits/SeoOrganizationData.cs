using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using SeoTester.Entities;

namespace SeoTester.Audits;

/// <summary>
/// D26 for SeoTester: deleting an organization deletes its sites, runs and pages; the personal-data export lists its
/// sites and runs. The abuse log stays (D28, administrators', expiring after a year), and so do the per-host cool-downs
/// (they belong to the target, not to an organization).
/// </summary>
internal sealed class SeoOrganizationData(IDocumentStore store) : IOrganizationDataHandler
{
    public string Name => "seotester";

    public async Task<object?> ExportAsync(Organization organization, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var organizationId = organization.Id;
        var sites = await StreamAsync(session, session.Query<Site>().Where(s => s.OrganizationId == organizationId), cancellationToken);
        var runs = await StreamAsync(session, session.Query<AuditRun>().Where(r => r.OrganizationId == organizationId), cancellationToken);
        return new
        {
            sites = sites.Select(s => new { s.Id, s.Origin, s.IsVerified, s.VerificationMethod, s.VerifiedAtUtc, s.Monitoring, s.Language, s.PwaProfile, s.CreatedAtUtc }),
            runs = runs.Select(r => new { r.Id, r.SiteId, r.Origin, r.Status, r.StartedAtUtc, r.CompletedAtUtc, r.Score, r.Grade, r.ScoringVersion, r.IsFullCrawl }),
        };
    }

    /// <summary>Idempotent: deletes whatever is left, by query, collection by collection (pages first).</summary>
    public async Task DeleteAsync(Organization organization, CancellationToken cancellationToken)
    {
        // Values as parameters; the collection names are constants of this application (CLAUDE.md, RQL rule).
        foreach (var collection in new[] { "PageResults", "AuditRuns", "Sites" })
        {
            var query = new IndexQuery
            {
                Query = $"from {RqlIdentifier.Collection(collection)} where OrganizationId = $organizationId",
                QueryParameters = new Parameters { ["organizationId"] = organization.Id },
            };
            var operation = await store.Operations.SendAsync(new DeleteByQueryOperation(query), token: cancellationToken);
            await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
        }
    }

    private static async Task<List<T>> StreamAsync<T>(IAsyncDocumentSession session, IQueryable<T> query, CancellationToken cancellationToken)
    {
        var list = new List<T>();
        await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
        while (await stream.MoveNextAsync())
            list.Add(stream.Current.Document);
        return list;
    }
}
