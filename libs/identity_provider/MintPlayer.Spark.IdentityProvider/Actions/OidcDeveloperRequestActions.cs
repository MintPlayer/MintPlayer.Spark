using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>One row of the <c>oidc-developer-requests</c> query; its property names are the <c>OidcDeveloperRequest</c> attributes.</summary>
/// <param name="Id">The requesting user's document id, which the row actions receive as the selection.</param>
public sealed record OidcDeveloperRequest(string Id, string? UserName, string? Email, DateTime RequestedAt, int TermsVersion);

/// <summary>
/// Backs <c>Custom.DeveloperRequests</c>, the administrators' queue of pending developer requests
/// (<c>docs/identity_provider_platform_PRD.md</c> D2). A virtual type: its rows are users whose
/// <c>Developer.Status</c> is <c>Requested</c>, projected per request.
/// </summary>
internal sealed partial class OidcDeveloperRequestActions : ISparkOwnsRowSecurity
{
    public string RowSecurityRationale =>
        "Rows are the users with a pending developer request. Only Query/OidcDeveloperRequest reaches them, and " +
        "the library's security.json grants it to identity-provider:administrators alone. The projection carries " +
        "the user name, email and request date: what an administrator needs to decide, and nothing of the credentials.";

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcUserDocuments users;

    /// <summary>The pending requests, oldest first by the query's sort.</summary>
    public async Task<IQueryable<OidcDeveloperRequest>> DeveloperRequests()
    {
        // The collection is an identifier (RqlIdentifier-validated in OidcUserDocuments); the status is a parameter.
        // `id()`, not `id(u)` over an alias: in a plain (non-JavaScript) select the server rejected the
        // aliased form with "id(doc) must be called with an object argument", a 500 on every load of the
        // queue (found by the E2E journey, IdentityProviderJourneyTests).
        var rows = await session.Advanced
            .AsyncRawQuery<OidcDeveloperRequest>(
                $"from '{users.Collection}' where {OidcDeveloper.FieldName}.Status = $status "
              + $"select id() as Id, UserName, Email, "
              + $"{OidcDeveloper.FieldName}.RequestedAt as RequestedAt, {OidcDeveloper.FieldName}.TermsVersion as TermsVersion")
            .AddParameter("status", OidcDeveloperStatuses.Requested)
            .ToListAsync();
        return rows.AsQueryable();
    }
}
