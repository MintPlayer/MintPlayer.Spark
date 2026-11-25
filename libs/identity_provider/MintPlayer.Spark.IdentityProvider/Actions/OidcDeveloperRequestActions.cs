using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>One row of the <c>oidc-developer-requests</c> query; its property names are the <c>OidcDeveloperRequest</c> attributes.</summary>
/// <param name="Id">The requesting user's document id, which the row actions receive as the selection.</param>
public sealed record OidcDeveloperRequest(string Id, string? UserName, string? Email, DateTime RequestedAt, int TermsVersion);

/// <summary>
/// Backs <c>Custom.DeveloperRequests</c>, the administrators' queue of pending developer requests
/// (<c>docs/identity_provider_platform_PRD.md</c> D2). A virtual type: its rows are the <see cref="OidcDeveloper"/>
/// documents whose status is <c>Requested</c>, with the requesting user's name and email.
/// </summary>
internal sealed partial class OidcDeveloperRequestActions : ISparkOwnsRowSecurity
{
    public string RowSecurityRationale =>
        "Rows are the users with a pending developer request. Only Query/OidcDeveloperRequest reaches them, and " +
        "the library's security.json grants it to identity-provider:administrators alone. The projection carries " +
        "the user name, email and request date: what an administrator needs to decide, and nothing of the credentials.";

    [Inject] private readonly ISparkSession session;
    [Inject] private readonly OidcUserDocuments users;

    /// <summary>The pending requests, oldest first by the query's sort.</summary>
    public async Task<IQueryable<OidcDeveloperRequest>> DeveloperRequests()
    {
        // The view narrows (#536 D47); the loaded documents decide, compared exactly.
        var rows = await session.ToListAsync(session.Query<OidcDeveloperState, OidcDevelopers_ByStatus>()
            .Where(d => d.Status == OidcDeveloperStatuses.Requested));
        if (rows.Count == 0)
            return Enumerable.Empty<OidcDeveloperRequest>().AsQueryable();

        var developers = await session.LoadAsync<OidcDeveloper>(rows.Select(r => r.Id).OfType<string>());
        var pending = developers.Values.OfType<OidcDeveloper>()
            .Where(d => d.Status == OidcDeveloperStatuses.Requested && d.UserId.Length > 0)
            .ToList();
        var accounts = await users.LoadAsync(session, pending.Select(d => d.UserId), CancellationToken.None);
        return pending
            .Select(d => (Developer: d, Account: accounts.GetValueOrDefault(d.UserId)))
            .Where(p => p.Account is not null) // a deleted user's request is nobody's to decide
            .Select(p => new OidcDeveloperRequest(p.Developer.UserId, p.Account!.UserName, p.Account.Email,
                p.Developer.RequestedAt, p.Developer.TermsVersion))
            .ToList()
            .AsQueryable();
    }
}
