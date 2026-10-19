using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Organizations.Identity;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Organizations.CustomActions;

/// <summary>
/// Invites an address to the organization whose page it is on (D18). Admins and up. The answer is
/// "Invitation sent" whether or not the address has an account (#453).
/// </summary>
internal sealed partial class InviteOrganizationMemberAction : ICustomAction
{
    /// <summary>The prompt's form (<c>App_Data/Model/OrganizationInvite.json</c>), never stored; by id (D28).</summary>
    internal static readonly Guid PromptType = OrganizationTypeIds.OrganizationInvite;

    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkOrganizations organizations;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } organizationId || OrganizationActionUser.Id(httpContextAccessor) is not { } userId)
            return;

        // D43 (owner correction 2026-10-10): the invitation is mailed on the inviter's behalf, so say why
        // before asking for an address. InviteAsync enforces the same rule.
        if (!OrganizationEmail.HasConfirmedAddress(await session.LoadAsync<SparkUser>(userId, cancellationToken)))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("organizations.confirmEmailToInvite"), NotificationKind.Error);
            return;
        }

        var prompt = await manager.GetPersistentObjectAsync(PromptType, cancellationToken: cancellationToken);
        prompt["Role"].Value = OrganizationRole.Member.ToString();
        var send = manager.GetTranslatedMessage("organizations.invite");
        manager.Retry.Action(
            title: manager.GetTranslatedMessage("organizations.inviteTitle"),
            options: [send],
            defaultOption: send,
            persistentObject: prompt,
            cancellable: true);

        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption || answer.PersistentObject is not { } form)
            return;

        var email = form.TryGetAttribute("Email", out var e) ? e.Value?.ToString() : null;
        var roleText = form.TryGetAttribute("Role", out var r) ? r.Value?.ToString() : null;
        if (!Enum.TryParse<OrganizationRole>(roleText, ignoreCase: true, out var role))
            role = OrganizationRole.Member;

        if (!await OrganizationActionUser.TryAsync(manager, () => organizations.InviteAsync(organizationId, email ?? "", role, userId, cancellationToken)))
            return;

        manager.Client.Notify(manager.GetTranslatedMessage("organizations.invitationSent"), NotificationKind.Success);
        manager.Client.RefreshQuery(OrganizationQueryAliases.Invitations);
    }
}

/// <summary>Leaves the organization whose page it is on (D26: access only). The last Owner must transfer ownership first.</summary>
internal sealed partial class LeaveOrganizationAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkOrganizations organizations;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } organizationId || OrganizationActionUser.Id(httpContextAccessor) is not { } userId)
            return;

        if (!await OrganizationActionUser.TryAsync(manager, () => organizations.LeaveAsync(organizationId, userId, cancellationToken)))
            return;

        manager.Client.Notify(manager.GetTranslatedMessage("organizations.left"), NotificationKind.Success);
        manager.Client.Navigate("/query/" + OrganizationQueryAliases.Organizations);
    }
}

/// <summary>
/// Makes the selected member an Owner and the caller an Admin (D23). Owners only; on a row of an
/// organization's members list.
/// </summary>
internal sealed partial class TransferOrganizationOwnershipAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkOrganizations organizations;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.SelectedItems is not [{ Id: { Length: > 0 } membershipId }] || OrganizationActionUser.Id(httpContextAccessor) is not { } userId)
            return;

        // The selected row was authorized by the row gate (TransferOrganizationOwnership needs Owner).
        if (await session.LoadAsync<OrganizationMembership>(membershipId, cancellationToken) is not { OrganizationId: { } organizationId, UserId: { } memberId })
            return;

        if (!await OrganizationActionUser.TryAsync(manager, () => organizations.TransferOwnershipAsync(organizationId, userId, memberId, cancellationToken)))
            return;

        manager.Client.Notify(manager.GetTranslatedMessage("organizations.ownershipTransferred"), NotificationKind.Success);
        manager.Client.RefreshQuery(OrganizationQueryAliases.Members);
    }
}

/// <summary>The package's query aliases.</summary>
public static class OrganizationQueryAliases
{
    /// <summary>The caller's organizations.</summary>
    public const string Organizations = "organizations";

    /// <summary>An organization's members (sub-query).</summary>
    public const string Members = "organization-members";

    /// <summary>An organization's pending invitations (sub-query).</summary>
    public const string Invitations = "organization-invitations";
}

/// <summary>What the actions share: the caller, and refusals turned into notifications.</summary>
internal static class OrganizationActionUser
{
    public static string? Id(IHttpContextAccessor accessor)
        => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null;

    /// <summary>Runs <paramref name="work"/>; a rule it breaks is shown to the caller, a refusal reads as "not found".</summary>
    public static async Task<bool> TryAsync(IManager manager, Func<Task> work)
    {
        try
        {
            await work();
            return true;
        }
        catch (SparkValidationException ex)
        {
            manager.Client.Notify(ex.Message, NotificationKind.Error);
            return false;
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("organizations.notAllowed"), NotificationKind.Error);
            return false;
        }
    }
}
