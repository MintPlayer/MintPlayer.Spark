using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// <see cref="ISparkGroupMembers"/> over the user store: a group's members are the users holding its name as a role,
/// found through <see cref="UserManager{TUser}.GetUsersInRoleAsync"/> (case-insensitive, as core matches role claims).
/// </summary>
internal sealed class SparkGroupMembers<TUser>(UserManager<TUser> userManager) : ISparkGroupMembers
    where TUser : SparkUser, new()
{
    public async Task<IReadOnlyList<SparkGroupMember>> GetMembersAsync(IReadOnlyCollection<string> groupNames, CancellationToken cancellationToken = default)
    {
        var members = new Dictionary<string, SparkGroupMember>(StringComparer.Ordinal);
        foreach (var name in groupNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var user in await userManager.GetUsersInRoleAsync(name))
            {
                if (user.Id is { Length: > 0 } id && !members.ContainsKey(id))
                    members[id] = new SparkGroupMember(id, user.UserName, user.Email, user.EmailConfirmed, user.PreferredCulture);
            }
        }
        return [.. members.Values.OrderBy(m => m.UserId, StringComparer.Ordinal)];
    }
}
