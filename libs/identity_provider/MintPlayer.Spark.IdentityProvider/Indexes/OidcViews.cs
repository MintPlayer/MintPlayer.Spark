using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions.Views;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Indexes;

// The identity provider's views (#536 D47): a RavenDB static index, an SQL view, LINQ in memory (D32). The services
// query these where they used to filter a collection, which made RavenDB build an auto index on first use (D42).
//
// ⚠️ A view compares strings the store's way: RavenDB lowercases its terms and SQL Server's default collation ignores
// case. A view only narrows; the caller loads the documents and compares identifiers exactly before acting on them.

/// <summary>A token's state: what the revocation cascades, sessions and the disable cascade select on.</summary>
public sealed class OidcTokenState
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string AuthorizationId { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>Every token's application, authorization, session, type and status.</summary>
public sealed class OidcTokens_ByState : SparkView<OidcTokenState>
{
    public override Expression<Func<ISparkViewSource, IQueryable<OidcTokenState>>> Map => s =>
        s.Set<OidcToken>().Select(t => new OidcTokenState
        {
            Id = s.IdOf(t),
            ApplicationId = t.ApplicationId,
            AuthorizationId = t.AuthorizationId,
            SessionId = t.SessionId,
            Type = t.Type,
            Status = t.Status,
        });
}

/// <summary>One row per scope of a token: what disabling a scope revokes.</summary>
public sealed class OidcTokenScope
{
    public string? Id { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>A token per scope it carries (a fan-out view, D43).</summary>
public sealed class OidcTokens_ByScope : SparkView<OidcTokenScope>
{
    public override Expression<Func<ISparkViewSource, IQueryable<OidcTokenScope>>> Map => s =>
        from t in s.Set<OidcToken>()
        from scope in t.Scopes
        select new OidcTokenScope { Id = s.IdOf(t), Scope = scope, Status = t.Status };
}

/// <summary>A grant's subject, application and status.</summary>
public sealed class OidcGrantState
{
    public string? Id { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Answers "which applications has this user granted?" and "who granted this application?": grant ids are a hash of
/// (subject, application), so they are not scannable by either.
/// <para>
/// <b>Never an authorization decision.</b> A view is eventually consistent on RavenDB, and this collection once had
/// an index removed for exactly that reason: a grant revoked moments earlier still read back as valid. Withdrawal and
/// every issuance check point-load the grant by its derived id. This view lists grants on a page, and finds the
/// subjects of an application being removed.
/// </para>
/// </summary>
public sealed class OidcGrants_BySubject : SparkView<OidcGrantState>
{
    public override Expression<Func<ISparkViewSource, IQueryable<OidcGrantState>>> Map => s =>
        s.Set<OidcGrant>().Select(g => new OidcGrantState
        {
            Id = s.IdOf(g),
            Subject = g.Subject,
            ApplicationId = g.ApplicationId,
            Status = g.Status,
        });
}

/// <summary>A user's developer status, for the administrators' queue of pending requests.</summary>
public sealed class OidcDeveloperState
{
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>Every developer status, by status (<c>Custom.DeveloperRequests</c>).</summary>
public sealed class OidcDevelopers_ByStatus : SparkView<OidcDeveloperState>
{
    public override Expression<Func<ISparkViewSource, IQueryable<OidcDeveloperState>>> Map => s =>
        s.Set<OidcDeveloper>().Select(d => new OidcDeveloperState
        {
            Id = s.IdOf(d),
            UserId = d.UserId,
            Status = d.Status,
        });
}

/// <summary>One member of an application's team.</summary>
public sealed class OidcApplicationMemberEntry
{
    public string? Id { get; set; }
    public string? UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// An application per member of its team (a fan-out view, D43): the applications a user is an Admin of, for the
/// audit trail's row filter. Every row of an application carries its id.
/// </summary>
public sealed class OidcApplications_ByMember : SparkView<OidcApplicationMemberEntry>
{
    public override Expression<Func<ISparkViewSource, IQueryable<OidcApplicationMemberEntry>>> Map => s =>
        from a in s.Set<OidcApplication>()
        from m in a.Members
        select new OidcApplicationMemberEntry { Id = s.IdOf(a), UserId = m.UserId, Role = m.Role, Status = m.Status };
}

/// <summary>One scope an application asks for, with its approval status.</summary>
public sealed class OidcApplicationScopeEntry
{
    public string? Id { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>An application per scope it asks for (a fan-out view): the pending scope approvals.</summary>
public sealed class OidcApplications_ByScope : SparkView<OidcApplicationScopeEntry>
{
    public override Expression<Func<ISparkViewSource, IQueryable<OidcApplicationScopeEntry>>> Map => s =>
        from a in s.Set<OidcApplication>()
        from scope in a.Scopes
        select new OidcApplicationScopeEntry { Id = s.IdOf(a), Scope = scope.Name, Status = scope.Status };
}

/// <summary>An application that has a go-live review, decided or not.</summary>
public sealed class OidcApplicationReference
{
    public string? Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// The applications with a go-live review. The review is one value stored whole (a JSON column in SQL), so the view
/// narrows to the applications that have one, and the caller reads its status from the loaded document.
/// </summary>
public sealed class OidcApplications_WithGoLiveReview : SparkView<OidcApplicationReference>
{
    public override Expression<Func<ISparkViewSource, IQueryable<OidcApplicationReference>>> Map => s =>
        s.Set<OidcApplication>()
            .Where(a => a.GoLiveReview != null)
            .Select(a => new OidcApplicationReference { Id = s.IdOf(a), ClientId = a.ClientId });
}

/// <summary>An agent client's registration and last use: what pruning unused dynamic registrations selects on.</summary>
public sealed class OidcAgentClientUse
{
    public string? Id { get; set; }
    public string? AgentRegistration { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>The applications an AI agent registered, with their last use.</summary>
public sealed class OidcAgentClients_ByLastUse : SparkView<OidcAgentClientUse>
{
    public override Expression<Func<ISparkViewSource, IQueryable<OidcAgentClientUse>>> Map => s =>
        s.Set<OidcApplication>()
            .Where(a => a.AgentRegistration != null)
            .Select(a => new OidcAgentClientUse
            {
                Id = s.IdOf(a),
                AgentRegistration = a.AgentRegistration,
                LastUsedAt = a.LastUsedAt,
            });
}
