using MintPlayer.Spark.Organizations.Models;

namespace MintPlayer.Spark.Organizations;

/// <summary>
/// Organizations, memberships, invitations and join requests (<c>docs/seo_analyzer_PRD.md</c> X11). The
/// generic UI's actions call it, and so does application code (a join request when a user tries to
/// verify a site another organization holds, D18). Every method takes the acting user explicitly and
/// checks their role itself, so it is safe to call from background work.
/// </summary>
/// <remarks>
/// Refusals: an organization that does not exist, or that the acting user may not act on, throws
/// <see cref="MintPlayer.Spark.Abstractions.Authorization.SparkRowLevelAccessDeniedException"/> (the
/// missing-row 404, #453); a broken rule (the last Owner leaving, a personal organization's user leaving) throws
/// <see cref="MintPlayer.Spark.Abstractions.SparkValidationException"/> (400).
/// </remarks>
public interface ISparkOrganizations
{
    /// <summary><paramref name="userId"/>'s memberships, organization id → role (read once per request).</summary>
    Task<IReadOnlyDictionary<string, OrganizationRole>> GetMembershipsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary><paramref name="userId"/>'s role in <paramref name="organizationId"/>, or null when they are not a member.</summary>
    Task<OrganizationRole?> GetRoleAsync(string organizationId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The organization the caller chose in the switcher (<c>/spark/organizations/current</c>, Q-M11a) while they are
    /// still a member of it; null for "All organizations". Read from the current request: lists of
    /// <see cref="IOrganizationOwned"/> rows show only its rows, and a new row with no organization lands there.
    /// </summary>
    Task<string?> GetCurrentOrganizationIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="userId"/>'s personal organization, created with them as Owner when missing (D21).
    /// Idempotent and race-free: its id is fixed (<see cref="Organization.PersonalIdFor"/>).
    /// </summary>
    Task<Organization> EnsurePersonalOrganizationAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Creates an organization with <paramref name="ownerUserId"/> as its Owner.</summary>
    Task<Organization> CreateAsync(string name, string ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invites <paramref name="email"/> as <paramref name="role"/> (never Owner) and mails the link. Admin
    /// and up; an invitation to the same address replaces the pending one. A personal organization too (D42).
    /// The mail goes out on the inviter's behalf, so the inviter's account needs a confirmed email address
    /// (D43); without one the call throws a <see cref="MintPlayer.Spark.Abstractions.SparkValidationException"/> and nothing is stored or sent.
    /// The invited address needs no account at all.
    /// </summary>
    Task InviteAsync(string organizationId, string email, OrganizationRole role, string inviterUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the Owners of <paramref name="organizationId"/>, by mail, to let <paramref name="requesterUserId"/>
    /// in as <paramref name="role"/> (D18), a personal organization's too (D42). A member already: nothing happens.
    /// A request from the same user replaces their pending one. Returns whether a request was made. The mail
    /// carries the requester's address on their behalf, so like an inviter the requester needs a confirmed
    /// email address (D43); without one the call throws a <see cref="MintPlayer.Spark.Abstractions.SparkValidationException"/>.
    /// </summary>
    Task<bool> RequestToJoinAsync(string organizationId, string requesterUserId, OrganizationRole role = OrganizationRole.Member, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a mailed link as <paramref name="userId"/>. An invitation makes the signed-in user a
    /// member, and only when that account's address is the invited one (D43; it need not be confirmed, the
    /// inviter's is); a join request makes
    /// its requester one, and needs an Owner or Admin to open it, whatever their address. The token is
    /// single-use and expires; every refusal reads the same, except the wrong account for a valid invitation.
    /// </summary>
    Task<OrganizationAcceptance> AcceptAsync(string invitationId, string token, string userId, CancellationToken cancellationToken = default);

    /// <summary>Removes <paramref name="userId"/>'s membership (D26: access only). The last Owner must transfer ownership first.</summary>
    Task LeaveAsync(string organizationId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Makes member <paramref name="toUserId"/> an Owner and the Owner <paramref name="fromUserId"/> an Admin.</summary>
    Task TransferOwnershipAsync(string organizationId, string fromUserId, string toUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the organization (Owners only, never a personal one): every <see cref="IOrganizationDataHandler"/>
    /// first, then its memberships, invitations and the organization itself (D26).
    /// </summary>
    Task DeleteAsync(string organizationId, string actingUserId, CancellationToken cancellationToken = default);
}

/// <summary>What redeeming a link answers: the organization joined, or why not.</summary>
/// <remarks>
/// <see cref="Reason"/> names the refusal for a client to translate: <see cref="InvalidReason"/> or
/// <see cref="OtherAddressReason"/>; null when accepted.
/// </remarks>
public sealed record OrganizationAcceptance(bool Accepted, string? OrganizationId, string? OrganizationName, string? Problem, string? Reason = null)
{
    /// <summary>The one refusal for a link that is unknown, forged, used, expired or not the caller's to accept.</summary>
    public const string InvalidProblem = "This link is not valid. It may have expired, been used already, or be meant for someone else.";

    /// <summary>
    /// A valid invitation opened by an account whose address is not the invited one (D43). The link
    /// stays live for the right account.
    /// </summary>
    public const string OtherAddressProblem = "This invitation was sent to another email address. Sign in with the account that uses that address, then open the link again.";

    /// <summary><see cref="Reason"/> of <see cref="InvalidProblem"/>.</summary>
    public const string InvalidReason = "invalid";

    /// <summary><see cref="Reason"/> of <see cref="OtherAddressProblem"/>.</summary>
    public const string OtherAddressReason = "otherAddress";

    internal static OrganizationAcceptance Invalid { get; } = new(false, null, null, InvalidProblem, InvalidReason);

    internal static OrganizationAcceptance OtherAddress { get; } = new(false, null, null, OtherAddressProblem, OtherAddressReason);
}
