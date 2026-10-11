using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Organizations.Actions;

/// <summary>
/// The organizations screen (<c>Custom.Organizations</c>, alias <c>organizations</c>) and its two
/// sub-queries. A library cannot use a <c>Database.X</c> source, which needs a property on the
/// application's context. Which rows each caller sees is the package's row policy (members only, by
/// role), not these methods: they only scope a sub-query to its parent organization.
/// </summary>
public partial class OrganizationActions : DefaultPersistentObjectActions<Organization>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IServiceProvider services;

    /// <inheritdoc cref="OrganizationActionsFailClosed" />
    public override Task<Expression<Func<Organization, bool>>?> GetRowFilterAsync(string action) => OrganizationActionsFailClosed.Filter<Organization>(services);

    /// <summary>Every organization the caller belongs to (the row policy narrows it).</summary>
    public IRavenQueryable<Organization> Organizations() => session.Query<Organization>();
}

/// <summary>Backs <c>Custom.OrganizationMembers</c>, the members list on an organization's page.</summary>
public partial class OrganizationMembershipActions : DefaultPersistentObjectActions<OrganizationMembership>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IServiceProvider services;

    /// <inheritdoc cref="OrganizationActionsFailClosed" />
    public override Task<Expression<Func<OrganizationMembership, bool>>?> GetRowFilterAsync(string action) => OrganizationActionsFailClosed.Filter<OrganizationMembership>(services);

    /// <summary>The parent organization's members. The parent was authorized by the query endpoint; the rows by the row policy.</summary>
    public IQueryable<OrganizationMembership> OrganizationMembers(CustomQueryArgs args)
        => ParentId(args) is { } organizationId
            ? session.Query<OrganizationMembership>().Where(m => m.OrganizationId == organizationId)
            : Enumerable.Empty<OrganizationMembership>().AsQueryable();

    internal static string? ParentId(CustomQueryArgs args)
        => args.Parent?.Id is { Length: > 0 } id && string.Equals(args.ParentType, nameof(Organization), StringComparison.OrdinalIgnoreCase) ? id : null;
}

/// <summary>Backs <c>Custom.OrganizationInvitations</c>, the pending invitations and join requests on an organization's page (Admins and up).</summary>
public partial class OrganizationInvitationActions : DefaultPersistentObjectActions<OrganizationInvitation>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IServiceProvider services;

    /// <inheritdoc cref="OrganizationActionsFailClosed" />
    public override Task<Expression<Func<OrganizationInvitation, bool>>?> GetRowFilterAsync(string action) => OrganizationActionsFailClosed.Filter<OrganizationInvitation>(services);

    /// <summary>The parent organization's pending invitations and join requests; expired ones are hidden by the expiration policy.</summary>
    public IQueryable<OrganizationInvitation> OrganizationInvitations(CustomQueryArgs args)
        => OrganizationMembershipActions.ParentId(args) is { } organizationId
            ? session.Query<OrganizationInvitation>().Where(i => i.OrganizationId == organizationId)
            : Enumerable.Empty<OrganizationInvitation>().AsQueryable();
}

/// <summary>
/// Without <c>AddOrganizations()</c> the package's types are still in the model (library layers come with
/// the reference), but the row policy that limits them to members is not registered. The Actions classes
/// then close them: no row for anyone. With the package added they add nothing, and the policy decides.
/// </summary>
internal static class OrganizationActionsFailClosed
{
    public static Task<Expression<Func<T, bool>>?> Filter<T>(IServiceProvider services) where T : class
        => Task.FromResult<Expression<Func<T, bool>>?>(services.GetService(typeof(ISparkOrganizations)) is null ? _ => false : null);
}
