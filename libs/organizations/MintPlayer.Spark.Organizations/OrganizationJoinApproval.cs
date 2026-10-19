using MintPlayer.Spark.Approvals;

namespace MintPlayer.Spark.Organizations;

/// <summary>
/// The approval kind of a join request (<c>docs/commerce_PRD.md</c> §4.13, D26): a user asks to join an organization,
/// its Owners and Admins approve or decline in-app. The subject is the organization (<c>"Organization"</c>, its id),
/// keyed by the requester, so one user's new request replaces only their own pending one. Raised by
/// <see cref="ISparkOrganizations.RequestToJoinAsync"/>.
/// </summary>
public sealed class OrganizationJoinApproval : IApprovalKind
{
    /// <summary>Stored on every request: never change it.</summary>
    public static string Name => "organizations.join";
}
