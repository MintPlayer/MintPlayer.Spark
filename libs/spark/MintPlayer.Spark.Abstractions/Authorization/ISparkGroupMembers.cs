namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Who is in a group, for a library that has to tell a group's members something (replication's
/// <c>replication:approvers</c> slot, commerce X20 follow-up). A group's name is its identity for role claims
/// (<see cref="SecurityConfiguration.Groups"/>), so the members are the users holding that role. The Authorization
/// package implements it over its user store; without it a library reaches open pages only.
/// </summary>
public interface ISparkGroupMembers
{
    /// <summary>Every user holding any of <paramref name="groupNames"/> (matched case-insensitively), each once.</summary>
    Task<IReadOnlyList<SparkGroupMember>> GetMembersAsync(IReadOnlyCollection<string> groupNames, CancellationToken cancellationToken = default);
}

/// <summary>One member of a group: enough to address a notice to them, never credentials.</summary>
/// <param name="UserId">The user document's id; also the <c>user:{id}</c> live channel.</param>
/// <param name="UserName">The public handle.</param>
/// <param name="Email">The address, when there is one.</param>
/// <param name="EmailConfirmed">Whether <paramref name="Email"/> is confirmed; mail goes only to a confirmed address.</param>
/// <param name="PreferredCulture">The culture mail is written in; null is the application's default.</param>
public sealed record SparkGroupMember(string UserId, string? UserName, string? Email, bool EmailConfirmed, string? PreferredCulture);

/// <summary>Resolves a slot (or a group id, or a well-known token) to the names of the groups it stands for.</summary>
public static class SparkGroupNames
{
    /// <summary>
    /// The group names <paramref name="group"/> resolves to in <paramref name="configuration"/> (a slot through
    /// <c>bindings</c>), or an empty list, with <paramref name="problem"/> saying why, when it does not resolve.
    /// Well-known groups (<c>@anonymous</c>, <c>@authenticated</c>) have no members to enumerate and resolve to none.
    /// </summary>
    public static IReadOnlyList<string> Of(SecurityConfiguration configuration, string group, out string? problem)
    {
        var ids = SparkSecurityFiles.ResolveGroup(configuration, group, out problem);
        if (ids is null)
            return [];
        var names = new List<string>();
        foreach (var id in ids)
        {
            if (configuration.Groups.FirstOrDefault(g => Guid.TryParse(g.Key, out var key) && key == id) is { Value: { Length: > 0 } name })
                names.Add(name);
        }
        return names;
    }
}
