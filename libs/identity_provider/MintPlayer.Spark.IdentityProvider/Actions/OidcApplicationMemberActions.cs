using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>One row of an application's <c>oidc-application-members</c> sub-query (D3).</summary>
/// <param name="Id">The member entry's id (<see cref="OidcApplicationMember.MemberId"/>).</param>
/// <param name="Name">An active member's user name. A pending invitation shows only the address that was typed: never a resolved name, which would tell the inviter the address has an account (#453).</param>
/// <param name="Status"><c>Invited</c>, <c>Expired</c> or <c>Active</c>. An invitation that could not be delivered looks exactly like one that was ignored.</param>
public sealed record OidcApplicationMemberRow(string Id, string Name, string Role, string Status, DateTime? InvitedAt, DateTime? AcceptedAt);

/// <summary>
/// Backs <c>Custom.Members</c>, the team list on an application's page
/// (<c>docs/identity_provider_platform_PRD.md</c> D3). A virtual row type, built from the parent
/// application's <c>Members[]</c>.
/// </summary>
internal sealed partial class OidcApplicationMemberRowActions : ISparkOwnsRowSecurity
{
    public string RowSecurityRationale =>
        "Rows come from the parent OidcApplication only, which the executor re-loads through the row-gated read " +
        "path (members only, OidcApplicationActions.GetRowFilterAsync) before this method runs. Pending invitations " +
        "show the typed address and never a resolved account, so the list is no existence oracle (#453).";

    public const string QueryAlias = "oidc-application-members";

    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<IQueryable<OidcApplicationMemberRow>> Members(CustomQueryArgs args)
    {
        args.EnsureParent(nameof(OidcApplication));
        if (args.Parent?.Id is not { Length: > 0 } applicationId)
            return Enumerable.Empty<OidcApplicationMemberRow>().AsQueryable();

        var app = await session.LoadAsync<OidcApplication>(applicationId);
        if (app is null)
            return Enumerable.Empty<OidcApplicationMemberRow>().AsQueryable();

        var userIds = app.Members.Where(m => m.Status == OidcMemberStatuses.Active && m.UserId is not null).Select(m => m.UserId!).ToList();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var lookup = session.Advanced.DocumentStore.OpenAsyncSession())
        {
            foreach (var (id, user) in await lookup.LoadAsync<UserNameHolder>(userIds))
                names[id] = user?.UserName ?? id;
        }

        var now = DateTime.UtcNow;
        return app.Members
            .Where(m => m.Status != OidcMemberStatuses.Revoked && m.MemberId is not null)
            .Select(m => new OidcApplicationMemberRow(
                m.MemberId!,
                m.Status == OidcMemberStatuses.Active && m.UserId is not null ? names.GetValueOrDefault(m.UserId, m.UserId) : m.Email ?? "",
                m.Role,
                m.Status == OidcMemberStatuses.Invited && m.InvitationExpiresAt < now ? "Expired" : m.Status,
                m.InvitedAt,
                m.AcceptedAt))
            .ToList()
            .AsQueryable();
    }

    private sealed class UserNameHolder
    {
        public string? UserName { get; set; }
    }
}
