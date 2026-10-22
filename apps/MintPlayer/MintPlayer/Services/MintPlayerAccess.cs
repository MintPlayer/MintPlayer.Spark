using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Services;

/// <summary>Who the caller is, for the rules that depend on ownership.</summary>
public sealed partial class MintPlayerAccess
{
    /// <summary>The legacy role, kept as the group name (security.json).</summary>
    public const string AdministratorGroup = "Administrator";

    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IGroupMembershipProvider groups;

    private bool? isAdministrator;

    public string? UserId => currentUser.IsAuthenticated ? currentUser.Id : null;

    public async Task<bool> IsAdministratorAsync()
        => isAdministrator ??= UserId is not null
            && (await groups.GetCurrentUserGroupsAsync()).Contains(AdministratorGroup, StringComparer.OrdinalIgnoreCase);
}
