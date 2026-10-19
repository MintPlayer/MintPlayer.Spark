using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Migrations;

/// <summary>
/// Moves the identity provider's pending decisions onto approval requests (commerce D26, research §9.3):
/// <list type="bullet">
/// <item>a scope row still <c>Pending</c> gets an <c>identity-provider.scope</c> request, requested by whoever its old
/// <c>Review</c> named (else the application's creator);</item>
/// <item>a pending <c>GoLiveReview</c> on an application still in Development gets an <c>identity-provider.go-live</c> request;</item>
/// <item>a user whose developer status is <c>Requested</c> gets an <c>identity-provider.developer</c> request.</item>
/// </list>
/// Then the old <c>GoLiveReview</c> and <c>Scopes[].Review</c> fields are removed from every application: RavenDB keeps
/// fields the model no longer declares, so without this they would stay in the documents for good. Decided requests
/// are not copied (no history before D26 is kept beyond the scope row's status and the audit trail).
/// <para>
/// Idempotent: a request already pending for the same subject is found, not raised twice (the engine's dedupe), and a
/// second run finds no old fields. The deciders are told by the Approvals sweep. Production (CodeCoverage, SeoTester)
/// runs the provider for agents (<c>AgentsOnly()</c>): no developer portal, so this is expected to find nothing there.
/// </para>
/// </summary>
public partial class M_202610201000_OidcApprovalRequests : ISparkMigration
{
    public static string? Description => "Identity provider: pending developer, scope and go-live decisions to approval requests (commerce D26)";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkApprovals approvals;
    [Inject] private readonly IServiceProvider services;

    private sealed class LegacyDecision
    {
        public string? Status { get; set; }
        public string? RequestedBy { get; set; }
    }

    private sealed class LegacyScope
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public LegacyDecision? Review { get; set; }
    }

    private sealed class LegacyApplication
    {
        public string? Id { get; set; }
        public List<LegacyScope>? Scopes { get; set; }
        public LegacyDecision? GoLiveReview { get; set; }
    }

    private sealed class RequestedDeveloper
    {
        public string? Id { get; set; }
        public string? UserName { get; set; }
        public string? Email { get; set; }
        public int TermsVersion { get; set; }
    }

    private const int PageSize = 256;

    /// <summary>Removes the pre-D26 decision fields. A constant script: nothing is spliced in.</summary>
    private const string StripScript =
        "delete this.GoLiveReview; if (this.Scopes) { this.Scopes.forEach(function (s) { delete s.Review; }); }";

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await MigrateApplicationsAsync(cancellationToken);
        await MigrateDevelopersAsync(cancellationToken);
    }

    private async Task MigrateApplicationsAsync(CancellationToken ct)
    {
        for (var skip = 0; ; skip += PageSize)
        {
            List<LegacyApplication> page;
            using (var read = store.OpenAsyncSession())
            {
                page = await read.Advanced.AsyncRawQuery<LegacyApplication>("from OidcApplications select id() as Id, Scopes, GoLiveReview")
                    .Skip(skip).Take(PageSize).ToListAsync(ct);
            }
            foreach (var legacy in page.Where(l => l.Id is { Length: > 0 }))
                await MigrateApplicationAsync(legacy, ct);
            if (page.Count < PageSize)
                return;
        }
    }

    private async Task MigrateApplicationAsync(LegacyApplication legacy, CancellationToken ct)
    {
        var hasLegacyFields = legacy.GoLiveReview is not null || legacy.Scopes?.Any(s => s.Review is not null) == true;
        using var session = store.OpenAsyncSession();
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
        if (await session.LoadAsync<OidcApplication>(legacy.Id, ct) is not { } app)
            return;

        var raised = false;
        foreach (var scope in app.Scopes.Where(s => s.Status == OidcScopeStatuses.Pending))
        {
            if (OidcScopeCatalog.ApiResourceNameOf(scope.Name) is not { } apiName)
                continue;
            var requestedBy = legacy.Scopes?.FirstOrDefault(s => string.Equals(s.Name, scope.Name, StringComparison.OrdinalIgnoreCase))?.Review?.RequestedBy
                ?? app.CreatedBy;
            if (requestedBy is not { Length: > 0 })
                continue;
            await approvals.SubmitAsync<OidcScopeApprovalKind>(session, OidcApprovalRequests.Scope(app, scope.Name, apiName, requestedBy), ct);
            raised = true;
        }

        if (legacy.GoLiveReview is { Status: OidcScopeStatuses.Pending } review && app.Mode == OidcApplicationModes.Development
            && (review.RequestedBy ?? app.CreatedBy) is { Length: > 0 } goLiveRequester)
        {
            await approvals.SubmitAsync<OidcGoLiveApprovalKind>(session, OidcApprovalRequests.GoLive(app, goLiveRequester), ct);
            raised = true;
        }

        if (hasLegacyFields)
            session.Advanced.Defer(new PatchCommandData(app.Id!, null, new PatchRequest { Script = StripScript }));
        if (raised || hasLegacyFields)
            await session.SaveChangesAsync(ct);
    }

    private async Task MigrateDevelopersAsync(CancellationToken ct)
    {
        // The collection is an identifier (RqlIdentifier-validated in OidcUserDocuments); the status is a parameter.
        var collection = services.GetRequiredService<OidcUserDocuments>().Collection;
        for (var skip = 0; ; skip += PageSize)
        {
            List<RequestedDeveloper> page;
            using (var read = store.OpenAsyncSession())
            {
                page = await read.Advanced
                    .AsyncRawQuery<RequestedDeveloper>(
                        $"from '{collection}' where {OidcDeveloper.FieldName}.Status = $status "
                      + $"select id() as Id, UserName, Email, {OidcDeveloper.FieldName}.TermsVersion as TermsVersion")
                    .AddParameter("status", OidcDeveloperStatuses.Requested)
                    .Skip(skip).Take(PageSize)
                    .ToListAsync(ct);
            }

            using (var session = store.OpenAsyncSession())
            {
                // Each submission queries for a pending request first.
                session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
                foreach (var developer in page.Where(d => d.Id is { Length: > 0 }))
                {
                    await approvals.SubmitAsync<OidcDeveloperApprovalKind>(session,
                        OidcApprovalRequests.Developer(developer.Id!, developer.UserName, developer.Email, developer.TermsVersion), ct);
                }
                await session.SaveChangesAsync(ct);
            }

            if (page.Count < PageSize)
                return;
        }
    }
}
