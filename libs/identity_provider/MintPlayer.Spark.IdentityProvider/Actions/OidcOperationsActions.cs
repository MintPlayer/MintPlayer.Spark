using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>
/// The audit trail (<c>docs/identity_provider_platform_PRD.md</c> D9): identity-provider administrators see
/// every event; a developer sees the events of the applications they are an Admin of.
/// </summary>
internal partial class OidcAuditEventActions : DefaultPersistentObjectActions<OidcAuditEvent>
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly OidcPortalAccess access;

    public IQueryable<OidcAuditEvent> AuditEvents() => session.Query<OidcAuditEvent>();

    public override async Task<Expression<Func<OidcAuditEvent, bool>>?> GetRowFilterAsync(string action)
    {
        if (await access.IsAdministratorAsync())
            return null;
        if (access.UserId is not { } userId)
            return e => false;

        // From the members view, not a query of the collection (#536 D47, D42).
        var adminOfIds = (await session.ApplicationsAdministeredByAsync(userId, CancellationToken.None)).Select(a => a.Id.Value).ToList();
        return e => e.ApplicationId != null && adminOfIds.Contains(e.ApplicationId);
    }
}

/// <summary>Every user's grants, for identity-provider administrators (D9), with a revoke action.</summary>
public partial class OidcGrantActions : DefaultPersistentObjectActions<OidcGrant>
{
    [Inject] private readonly ISparkSession session;

    public IQueryable<OidcGrant> Grants() => session.Query<OidcGrant>();
}
