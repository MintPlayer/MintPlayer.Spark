using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Organizations.Identity;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Organizations.Services;

/// <summary>The default <see cref="ISparkOrganizations"/> (scoped).</summary>
internal sealed class SparkOrganizations(
    IDocumentStore store,
    OrganizationMembershipReader reader,
    CurrentOrganization current,
    OrganizationMail mail,
    IEnumerable<IOrganizationDataHandler> dataHandlers,
    IModelLoader modelLoader,
    TimeProvider timeProvider,
    ILogger<SparkOrganizations> logger) : ISparkOrganizations
{
    /// <summary>The invitation lifetime when the model states no <c>expiresAfter</c> (D18: 14 days).</summary>
    internal static readonly TimeSpan DefaultInvitationLifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// A personal organization takes members like any other (D42), but its user owns it for good: nobody
    /// else becomes Owner, and its user is neither demoted, removed, nor able to leave.
    /// </summary>
    internal const string PersonalOwnerProblem = "A personal organization belongs to its user: its Owner role cannot change hands.";
    internal const string LastOwnerProblem = "An organization needs an Owner. Make another member Owner first.";
    internal const string InviterUnconfirmedProblem = "Confirm your email address before you invite anyone: an invitation is sent on your behalf.";
    internal const string RequesterUnconfirmedProblem = "Confirm your email address before you ask to join: the request is mailed to the owners on your behalf.";

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public Task<IReadOnlyDictionary<string, OrganizationRole>> GetMembershipsAsync(string userId, CancellationToken cancellationToken = default)
        => reader.GetAsync(Require(userId, nameof(userId)), cancellationToken);

    public async Task<OrganizationRole?> GetRoleAsync(string organizationId, string userId, CancellationToken cancellationToken = default)
        => (await GetMembershipsAsync(userId, cancellationToken)).TryGetValue(organizationId, out var role) ? role : null;

    public Task<string?> GetCurrentOrganizationIdAsync(CancellationToken cancellationToken = default)
        => current.GetIdAsync(cancellationToken);

    public async Task<Organization> EnsurePersonalOrganizationAsync(string userId, CancellationToken cancellationToken = default)
    {
        Require(userId, nameof(userId));
        var id = Organization.PersonalIdFor(userId);

        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<Organization>(id, cancellationToken) is { } existing)
            return existing;

        var user = await session.LoadAsync<SparkUser>(userId, cancellationToken)
            ?? throw new InvalidOperationException($"User '{userId}' does not exist.");

        var organization = new Organization
        {
            Id = id,
            Name = user.UserName ?? user.Email ?? userId,
            IsPersonal = true,
            CreatedBy = userId,
            CreatedAtUtc = Now,
        };

        // An empty change vector means "must not exist yet": two first sign-ins racing create it once.
        await session.StoreAsync(organization, string.Empty, id, cancellationToken);
        var membership = NewMembership(organization.Id, user, OrganizationRole.Owner, Now);
        await session.StoreAsync(membership, string.Empty, membership.Id, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            using var again = store.OpenAsyncSession();
            return await again.LoadAsync<Organization>(id, cancellationToken)
                ?? throw new InvalidOperationException($"The personal organization of '{userId}' could not be created.");
        }

        reader.Invalidate(userId);
        return organization;
    }

    public async Task<Organization> CreateAsync(string name, string ownerUserId, CancellationToken cancellationToken = default)
    {
        Require(ownerUserId, nameof(ownerUserId));
        name = ValidName(name);

        using var session = store.OpenAsyncSession();
        var user = await session.LoadAsync<SparkUser>(ownerUserId, cancellationToken)
            ?? throw new InvalidOperationException($"User '{ownerUserId}' does not exist.");

        var organization = new Organization { Id = Organization.NewId(), Name = name, CreatedBy = ownerUserId, CreatedAtUtc = Now };
        await session.StoreAsync(organization, organization.Id, cancellationToken);
        var membership = NewMembership(organization.Id!, user, OrganizationRole.Owner, Now);
        await session.StoreAsync(membership, membership.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        reader.Invalidate(ownerUserId);
        return organization;
    }

    public async Task InviteAsync(string organizationId, string email, OrganizationRole role, string inviterUserId, CancellationToken cancellationToken = default)
    {
        Require(inviterUserId, nameof(inviterUserId));
        email = ValidEmail(email);
        if (role is OrganizationRole.Owner || !Enum.IsDefined(role))
            throw new SparkValidationException("An invitation gives the Viewer, Member or Admin role. Ownership is transferred to a member.", nameof(OrganizationInvitation.Role));

        using var session = store.OpenAsyncSession();
        var organization = await LoadOrganizationAsync(session, organizationId, cancellationToken);
        var inviter = await RequireRoleAsync(session, organization.Id!, inviterUserId, OrganizationRole.Admin, cancellationToken);
        // D43 (owner correction 2026-10-10): the mail goes out on the inviter's behalf, so the inviter's
        // address must be confirmed. The invitee may not even have an account yet.
        if (!OrganizationEmail.HasConfirmedAddress(await session.LoadAsync<SparkUser>(inviterUserId, cancellationToken)))
            throw new SparkValidationException(InviterUnconfirmedProblem);

        // One live link per address: a new invitation replaces the pending one.
        foreach (var pending in await PendingAsync(session, organization.Id!, OrganizationInvitationKind.Invitation, i => i.Email == email, cancellationToken))
            session.Delete(pending);

        var token = NewToken();
        var invitation = new OrganizationInvitation
        {
            Id = OrganizationInvitation.NewId(),
            OrganizationId = organization.Id,
            Kind = OrganizationInvitationKind.Invitation,
            Email = email,
            Role = role,
            TokenHash = Hash(token),
            CreatedBy = inviterUserId,
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now + InvitationLifetime,
        };
        await session.StoreAsync(invitation, invitation.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        // Mailed to any address, account or not: the answer and the stored record are the same either
        // way, so inviting is no existence oracle (#453).
        await mail.SendAsync(OrganizationMailTemplates.Invitation, email, null, null, invitation, token, new
        {
            organization = organization.Name,
            role = role.ToString(),
            inviter = inviter.UserName ?? "",
        }, cancellationToken);
    }

    public async Task<bool> RequestToJoinAsync(string organizationId, string requesterUserId, OrganizationRole role = OrganizationRole.Member, CancellationToken cancellationToken = default)
    {
        Require(requesterUserId, nameof(requesterUserId));
        if (role is OrganizationRole.Owner || !Enum.IsDefined(role))
            throw new SparkValidationException("A join request asks for the Viewer, Member or Admin role.", nameof(OrganizationInvitation.Role));

        using var session = store.OpenAsyncSession();
        var organization = await session.LoadAsync<Organization>(Require(organizationId, nameof(organizationId)), cancellationToken)
            ?? throw new SparkRowLevelAccessDeniedException("Organization");
        if (await session.LoadAsync<OrganizationMembership>(OrganizationMembership.IdFor(requesterUserId, organization.Id!), cancellationToken) is not null)
            return false;

        var requester = await session.LoadAsync<SparkUser>(requesterUserId, cancellationToken)
            ?? throw new InvalidOperationException($"User '{requesterUserId}' does not exist.");
        // D43 (owner correction 2026-10-10): the Owners are mailed the requester's address on their
        // behalf, so, like an inviter's, it must be confirmed.
        if (!OrganizationEmail.HasConfirmedAddress(requester))
            throw new SparkValidationException(RequesterUnconfirmedProblem);

        foreach (var pending in await PendingAsync(session, organization.Id!, OrganizationInvitationKind.JoinRequest, i => i.UserId == requesterUserId, cancellationToken))
            session.Delete(pending);

        var token = NewToken();
        var request = new OrganizationInvitation
        {
            Id = OrganizationInvitation.NewId(),
            OrganizationId = organization.Id,
            Kind = OrganizationInvitationKind.JoinRequest,
            Email = requester.Email,
            UserId = requesterUserId,
            UserName = requester.UserName,
            Role = role,
            TokenHash = Hash(token),
            CreatedBy = requesterUserId,
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now + InvitationLifetime,
        };
        await session.StoreAsync(request, request.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        // Every Owner gets the same link; the first who accepts spends it.
        foreach (var owner in await OwnersAsync(session, organization.Id!, cancellationToken))
        {
            if (await session.LoadAsync<SparkUser>(owner.UserId, cancellationToken) is not { Email: { Length: > 0 } ownerEmail } ownerUser)
                continue;
            await mail.SendAsync(OrganizationMailTemplates.JoinRequest, ownerEmail, ownerUser.UserName, ownerUser.PreferredCulture, request, token, new
            {
                organization = organization.Name,
                role = role.ToString(),
                requester = requester.UserName ?? "",
                requester_email = requester.Email ?? "",
            }, cancellationToken);
        }
        return true;
    }

    public async Task<OrganizationAcceptance> AcceptAsync(string invitationId, string token, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(invitationId) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(userId)
            || !invitationId.StartsWith("OrganizationInvitations/", StringComparison.OrdinalIgnoreCase))
            return OrganizationAcceptance.Invalid;

        using var session = store.OpenAsyncSession();
        // Spending the link is a delete with the version read here: of two concurrent accepts, one fails.
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;

        var invitation = await session.LoadAsync<OrganizationInvitation>(invitationId, cancellationToken);
        if (invitation?.TokenHash is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(invitation.TokenHash), Encoding.ASCII.GetBytes(Hash(token)))
            // Raw loads are not row-filtered: an expired invitation lingers until the 36 h sweep (X1).
            || invitation.ExpiresAtUtc is not { } expires || expires <= Now
            || invitation.OrganizationId is null)
            return OrganizationAcceptance.Invalid;

        var organization = await session.LoadAsync<Organization>(invitation.OrganizationId, cancellationToken);
        if (organization is null)
            return OrganizationAcceptance.Invalid;

        string memberUserId;
        if (invitation.Kind == OrganizationInvitationKind.JoinRequest)
        {
            // Read from storage, not the request's cache: the decision is about who may let people in now.
            // No address rule here (D43): an Owner or Admin decides, and the requester signed in to ask.
            var approver = await session.LoadAsync<OrganizationMembership>(OrganizationMembership.IdFor(userId, organization.Id!), cancellationToken);
            if (approver is null || approver.Role < OrganizationRole.Admin || invitation.UserId is null)
                return OrganizationAcceptance.Invalid;
            memberUserId = invitation.UserId;
        }
        else
        {
            // D43: an invitation is for the mailbox it was sent to. The signed-in account's address must be
            // the invited one; it need not be confirmed (owner correction 2026-10-10: the inviter's is).
            // Refused before anything is written, so the link stays live for the right account. The token
            // was valid, so the holder already knows the invitation exists: saying why discloses nothing (#453).
            if (await session.LoadAsync<SparkUser>(userId, cancellationToken) is not { } account
                || !OrganizationEmail.IsAddressOf(account, invitation.Email))
                return OrganizationAcceptance.OtherAddress;
            memberUserId = userId;
        }

        var membershipId = OrganizationMembership.IdFor(memberUserId, organization.Id!);
        if (await session.LoadAsync<OrganizationMembership>(membershipId, cancellationToken) is null)
        {
            if (await session.LoadAsync<SparkUser>(memberUserId, cancellationToken) is not { } member)
                return OrganizationAcceptance.Invalid;
            var membership = NewMembership(organization.Id!, member, invitation.Role is OrganizationRole.Owner ? OrganizationRole.Admin : invitation.Role, Now);
            await session.StoreAsync(membership, string.Empty, membershipId, cancellationToken);
        }

        session.Delete(invitation);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            return OrganizationAcceptance.Invalid;
        }

        reader.Invalidate(memberUserId);
        logger.LogInformation("{Kind} {InvitationId} accepted by {UserId}: {MemberId} joined {OrganizationId}.",
            invitation.Kind, invitationId, userId, memberUserId, organization.Id);
        return new OrganizationAcceptance(true, organization.Id, organization.Name, null);
    }

    public async Task LeaveAsync(string organizationId, string userId, CancellationToken cancellationToken = default)
    {
        Require(userId, nameof(userId));
        using var session = store.OpenAsyncSession();
        var membership = await session.LoadAsync<OrganizationMembership>(OrganizationMembership.IdFor(userId, Require(organizationId, nameof(organizationId))), cancellationToken)
            ?? throw new SparkRowLevelAccessDeniedException("Organization");
        var organization = await LoadOrganizationAsync(session, organizationId, cancellationToken);
        // Its members may leave a personal organization (D42); its user cannot: it goes with the account.
        if (organization.IsPersonal && membership.Role == OrganizationRole.Owner)
            throw new SparkValidationException("A personal organization cannot be left by its user. It goes with the account.");
        if (membership.Role == OrganizationRole.Owner && !await HasOtherOwnerAsync(session, organization.Id!, userId, cancellationToken))
            throw new SparkValidationException(LastOwnerProblem);

        session.Delete(membership);
        await session.SaveChangesAsync(cancellationToken);
        reader.Invalidate(userId);
    }

    public async Task TransferOwnershipAsync(string organizationId, string fromUserId, string toUserId, CancellationToken cancellationToken = default)
    {
        Require(fromUserId, nameof(fromUserId));
        Require(toUserId, nameof(toUserId));
        if (string.Equals(fromUserId, toUserId, StringComparison.OrdinalIgnoreCase))
            throw new SparkValidationException("Choose another member to make Owner.");

        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var organization = await LoadOrganizationAsync(session, organizationId, cancellationToken);
        var from = await RequireRoleAsync(session, organization.Id!, fromUserId, OrganizationRole.Owner, cancellationToken);
        if (organization.IsPersonal)
            throw new SparkValidationException(PersonalOwnerProblem);
        var to = await session.LoadAsync<OrganizationMembership>(OrganizationMembership.IdFor(toUserId, organization.Id!), cancellationToken)
            ?? throw new SparkValidationException("Only a member can be made Owner. Invite them first.");

        to.Role = OrganizationRole.Owner;
        from.Role = OrganizationRole.Admin;
        await session.SaveChangesAsync(cancellationToken);
        reader.Invalidate(fromUserId);
        reader.Invalidate(toUserId);
    }

    public async Task DeleteAsync(string organizationId, string actingUserId, CancellationToken cancellationToken = default)
    {
        Require(actingUserId, nameof(actingUserId));
        using var session = store.OpenAsyncSession();
        var organization = await LoadOrganizationAsync(session, organizationId, cancellationToken);
        await RequireRoleAsync(session, organization.Id!, actingUserId, OrganizationRole.Owner, cancellationToken);
        if (organization.IsPersonal)
            throw new SparkValidationException("A personal organization is deleted with the account.");

        await DeleteDependentsAsync(session, organization, cancellationToken);
        session.Delete(organization);
        await session.SaveChangesAsync(cancellationToken);
        reader.Invalidate();
    }

    // ---- shared with the interceptors and the account hooks -------------------------------------------

    /// <summary>
    /// D26's cascade, written into <paramref name="session"/> (the caller saves): every
    /// <see cref="IOrganizationDataHandler"/> first, then the organization's memberships and invitations.
    /// </summary>
    internal async Task DeleteDependentsAsync(IAsyncDocumentSession session, Organization organization, CancellationToken cancellationToken)
    {
        foreach (var handler in dataHandlers)
            await handler.DeleteAsync(organization, cancellationToken);

        foreach (var id in await IdsAsync<OrganizationMembership>(session, organization.Id!, cancellationToken))
            session.Delete(id);
        foreach (var id in await IdsAsync<OrganizationInvitation>(session, organization.Id!, cancellationToken))
            session.Delete(id);
    }

    /// <summary>Whether an Owner other than <paramref name="userId"/> exists.</summary>
    internal static async Task<bool> HasOtherOwnerAsync(IAsyncDocumentSession session, string organizationId, string userId, CancellationToken cancellationToken)
        => (await OwnersAsync(session, organizationId, cancellationToken)).Any(o => !string.Equals(o.UserId, userId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The organization's members, read from an index that is waited on: these answers decide whether an Owner remains.</summary>
    internal static async Task<List<OrganizationMembership>> MembersAsync(IAsyncDocumentSession session, string organizationId, CancellationToken cancellationToken)
    {
        var all = new List<OrganizationMembership>();
        for (var skip = 0; ; skip += 1024)
        {
            var page = await session.Query<OrganizationMembership>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(m => m.OrganizationId == organizationId)
                .Skip(skip).Take(1024)
                .ToListAsync(cancellationToken);
            all.AddRange(page);
            if (page.Count < 1024)
                return all;
        }
    }

    private static async Task<List<OrganizationMembership>> OwnersAsync(IAsyncDocumentSession session, string organizationId, CancellationToken cancellationToken)
        => (await MembersAsync(session, organizationId, cancellationToken)).Where(m => m.Role == OrganizationRole.Owner).ToList();

    private static async Task<List<string>> IdsAsync<T>(IAsyncDocumentSession session, string organizationId, CancellationToken cancellationToken)
        where T : class, IOrganizationOwned
    {
        var ids = new List<string>();
        for (var skip = 0; ; skip += 1024)
        {
            var page = await session.Query<T>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(x => x.OrganizationId == organizationId)
                .Skip(skip).Take(1024)
                .ToListAsync(cancellationToken);
            ids.AddRange(page.Select(x => session.Advanced.GetDocumentId(x)).Where(id => id is not null)!);
            if (page.Count < 1024)
                return ids;
        }
    }

    private static async Task<List<OrganizationInvitation>> PendingAsync(
        IAsyncDocumentSession session, string organizationId, OrganizationInvitationKind kind,
        System.Linq.Expressions.Expression<Func<OrganizationInvitation, bool>> match, CancellationToken cancellationToken)
        => await session.Query<OrganizationInvitation>()
            // Waited on: a pending link the index has not seen yet would stay live beside its replacement.
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(i => i.OrganizationId == organizationId && i.Kind == kind)
            .Where(match)
            .ToListAsync(cancellationToken);

    private static async Task<Organization> LoadOrganizationAsync(IAsyncDocumentSession session, string organizationId, CancellationToken cancellationToken)
        => await session.LoadAsync<Organization>(Require(organizationId, nameof(organizationId)), cancellationToken)
            ?? throw new SparkRowLevelAccessDeniedException("Organization");

    /// <summary>The acting user's membership, read from storage, when its role is at least <paramref name="required"/>; else the missing-row refusal.</summary>
    private static async Task<OrganizationMembership> RequireRoleAsync(IAsyncDocumentSession session, string organizationId, string userId, OrganizationRole required, CancellationToken cancellationToken)
    {
        var membership = await session.LoadAsync<OrganizationMembership>(OrganizationMembership.IdFor(userId, organizationId), cancellationToken);
        return membership is not null && membership.Role >= required
            ? membership
            : throw new SparkRowLevelAccessDeniedException("Organization");
    }

    internal static OrganizationMembership NewMembership(string organizationId, SparkUser user, OrganizationRole role, DateTime joinedAtUtc) => new()
    {
        Id = OrganizationMembership.IdFor(user.Id!, organizationId),
        OrganizationId = organizationId,
        UserId = user.Id,
        UserName = user.UserName,
        Role = role,
        JoinedAtUtc = joinedAtUtc,
    };

    private TimeSpan InvitationLifetime
        => modelLoader.GetEntityTypeByClrType(typeof(OrganizationInvitation).FullName!)?.ExpiresAfter is { } after && after > TimeSpan.Zero
            ? after
            : DefaultInvitationLifetime;

    internal static string ValidName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > 200)
            throw new SparkValidationException("An organization needs a name of at most 200 characters.", nameof(Organization.Name));
        return trimmed;
    }

    private static string ValidEmail(string? email)
    {
        var trimmed = email?.Trim() ?? "";
        var at = trimmed.IndexOf('@');
        if (trimmed.Length > 254 || at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1 || trimmed.Any(char.IsWhiteSpace))
            throw new SparkValidationException("Enter one email address.", nameof(OrganizationInvitation.Email));
        return trimmed;
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value;

    /// <summary>A 256-bit single-use token, base64url.</summary>
    internal static string NewToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lower-case hex SHA-256 of <paramref name="token"/>: what is stored.</summary>
    internal static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
