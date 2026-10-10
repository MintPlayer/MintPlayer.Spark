using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Organizations.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Organizations.Identity;

/// <summary>Creates the user's personal organization on sign-in (D21): one load by id when it exists.</summary>
internal sealed class PersonalOrganizationOnSignIn<TUser>(ISparkOrganizations organizations, IOptions<SparkOrganizationsOptions> options)
    : ISparkSignedInHandler<TUser>
    where TUser : SparkUser
{
    public async Task OnSignedInAsync(TUser user, CancellationToken cancellationToken)
    {
        if (options.Value.CreatePersonalOrganizationOnSignIn && user.Id is { Length: > 0 } id)
            await organizations.EnsurePersonalOrganizationAsync(id, cancellationToken);
    }
}

/// <summary>
/// Account deletion (D26): the personal organization goes, with its data; every other membership goes;
/// an organization the user alone owns goes too when nobody else is in it. When others are in it, the
/// deletion stops with the account intact until ownership is transferred: an organization needs an Owner.
/// </summary>
/// <remarks>Checks everything before deleting anything, so a refusal leaves no half-deleted state; idempotent for the retry.</remarks>
internal sealed class OrganizationAccountDeletion<TUser>(IDocumentStore store, SparkOrganizations organizations, OrganizationMembershipReader reader)
    : ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;

        using var session = store.OpenAsyncSession();
        var memberships = await OrganizationMembershipReader.LoadAsync(session, userId, cancellationToken);
        var toDelete = new List<Organization>();
        foreach (var membership in memberships)
        {
            var organization = membership.OrganizationId is { } id ? await session.LoadAsync<Organization>(id, cancellationToken) : null;
            if (organization is null)
                continue;
            if (organization.IsPersonal)
            {
                toDelete.Add(organization);
                continue;
            }
            if (membership.Role != OrganizationRole.Owner || await SparkOrganizations.HasOtherOwnerAsync(session, organization.Id!, userId, cancellationToken))
                continue;

            var others = (await SparkOrganizations.MembersAsync(session, organization.Id!, cancellationToken))
                .Any(m => !string.Equals(m.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (others)
                throw new SparkValidationException($"You are the only Owner of '{organization.Name}'. Make another member Owner, or delete the organization, before deleting your account.");
            toDelete.Add(organization);
        }

        foreach (var organization in toDelete)
        {
            await organizations.DeleteDependentsAsync(session, organization, cancellationToken);
            session.Delete(organization);
        }
        // The deleted organizations' memberships went with them; the rest are left one by one.
        var deleted = toDelete.Select(o => o.Id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var membership in memberships.Where(m => m.OrganizationId is null || !deleted.Contains(m.OrganizationId)))
            session.Delete(membership);

        // The user's own pending join requests go with them.
        var requests = await session.Query<OrganizationInvitation>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(i => i.UserId == userId && i.Kind == OrganizationInvitationKind.JoinRequest)
            .ToListAsync(cancellationToken);
        foreach (var request in requests)
            session.Delete(request);

        await session.SaveChangesAsync(cancellationToken);
        reader.Invalidate();
    }
}

/// <summary>
/// The <c>organizations</c> section of the personal-data export (D26): each membership, and for every
/// organization the user owns, what each <see cref="IOrganizationDataHandler"/> exports.
/// </summary>
internal sealed class OrganizationPersonalData<TUser>(IDocumentStore store, IEnumerable<IOrganizationDataHandler> dataHandlers)
    : ISparkPersonalDataContributor<TUser>
    where TUser : SparkUser
{
    public string Name => "organizations";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return null;

        using var session = store.OpenAsyncSession();
        var memberships = await OrganizationMembershipReader.LoadAsync(session, userId, cancellationToken);
        var result = new List<object>();
        foreach (var membership in memberships.OrderBy(m => m.JoinedAtUtc))
        {
            if (membership.OrganizationId is not { } id || await session.LoadAsync<Organization>(id, cancellationToken) is not { } organization)
                continue;

            Dictionary<string, object?>? data = null;
            if (membership.Role == OrganizationRole.Owner)
            {
                data = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var handler in dataHandlers)
                {
                    if (await handler.ExportAsync(organization, cancellationToken) is { } exported)
                        data[handler.Name] = exported;
                }
            }

            result.Add(new
            {
                id = organization.Id,
                name = organization.Name,
                isPersonal = organization.IsPersonal,
                role = membership.Role.ToString(),
                joinedAtUtc = membership.JoinedAtUtc,
                data,
            });
        }
        return result;
    }
}
