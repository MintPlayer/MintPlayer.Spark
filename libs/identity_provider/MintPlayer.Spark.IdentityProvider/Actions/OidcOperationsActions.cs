using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>
/// The audit trail (<c>docs/identity_provider_platform_PRD.md</c> D9): identity-provider administrators see
/// every event; a developer sees the events of the applications they are an Admin of.
/// </summary>
internal partial class OidcAuditEventActions : DefaultPersistentObjectActions<OidcAuditEvent>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;

    public IRavenQueryable<OidcAuditEvent> AuditEvents() => session.Query<OidcAuditEvent>();

    public override async Task<Expression<Func<OidcAuditEvent, bool>>?> GetRowFilterAsync(string action)
    {
        if (await access.IsAdministratorAsync())
            return null;
        if (access.UserId is not { } userId)
            return e => false;

        var adminOf = await session.Query<OidcApplication>()
            .Where(a => a.Members.Any(m => m.UserId == userId && m.Status == OidcMemberStatuses.Active && m.Role == OidcMemberRoles.Admin))
            .Select(a => a.Id)
            .ToListAsync();
        return e => e.ApplicationId != null && e.ApplicationId.In(adminOf);
    }
}

/// <summary>Every user's grants, for identity-provider administrators (D9), with a revoke action.</summary>
public partial class OidcGrantActions : DefaultPersistentObjectActions<OidcGrant>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IRavenQueryable<OidcGrant> Grants() => session.Query<OidcGrant>();
}
