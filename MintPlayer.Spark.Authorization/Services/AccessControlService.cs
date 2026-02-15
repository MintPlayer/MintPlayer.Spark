using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Models;

namespace MintPlayer.Spark.Authorization.Services;

[Register(typeof(IAccessControl), ServiceLifetime.Scoped)]
internal partial class AccessControlService : IAccessControl
{
    [Inject] private readonly ISecurityConfigurationLoader configLoader;
    [Inject] private readonly IGroupMembershipProvider groupMembershipProvider;
    [Inject] private readonly IOptions<AuthorizationOptions> options;
    [Inject] private readonly ILogger<AccessControlService> logger;

    /// <summary>
    /// Combined action patterns that include multiple individual actions.
    /// </summary>
    private static readonly Dictionary<string, string[]> CombinedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EditNew"] = ["Edit", "New"],
        ["EditNewDelete"] = ["Edit", "New", "Delete"],
        ["NewDelete"] = ["New", "Delete"],
        ["QueryRead"] = ["Query", "Read"],
        ["QueryReadEdit"] = ["Query", "Read", "Edit"],
        ["QueryReadEditNew"] = ["Query", "Read", "Edit", "New"],
        ["QueryReadEditNewDelete"] = ["Query", "Read", "Edit", "New", "Delete"],
        ["ReadEdit"] = ["Read", "Edit"],
        ["ReadEditNew"] = ["Read", "Edit", "New"],
        ["ReadEditNewDelete"] = ["Read", "Edit", "New", "Delete"],
    };

    public async Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
    {
        var config = configLoader.GetConfiguration();
        var groupNames = await groupMembershipProvider.GetCurrentUserGroupsAsync(cancellationToken);
        var groupNamesList = groupNames.ToList();

        // No groups = no access (unless default allows)
        if (groupNamesList.Count == 0)
        {
            var defaultResult = options.Value.DefaultBehavior == DefaultAccessBehavior.AllowAll;
            LogAuthorizationDecision(resource, groupNamesList, defaultResult, "no groups (default behavior)");
            return defaultResult;
        }

        // Resolve group names to IDs
        var groupIds = ResolveGroupIds(config, groupNamesList);

        if (groupIds.Count == 0)
        {
            var defaultResult = options.Value.DefaultBehavior == DefaultAccessBehavior.AllowAll;
            LogAuthorizationDecision(resource, groupNamesList, defaultResult, "no matching group IDs found (default behavior)");
            return defaultResult;
        }

        // Get rights for the user's groups
        var relevantRights = config.Rights
            .Where(r => groupIds.Contains(r.GroupId))
            .ToList();

        // 1. Check explicit denials first (denials take precedence)
        if (relevantRights.Any(r => MatchesResource(r.Resource, resource) && r.IsDenied))
        {
            LogAuthorizationDecision(resource, groupNamesList, false, "explicit denial");
            return false;
        }

        // 2. Check exact match
        if (relevantRights.Any(r => MatchesResource(r.Resource, resource) && !r.IsDenied))
        {
            LogAuthorizationDecision(resource, groupNamesList, true, "exact match");
            return true;
        }

        // 3. Check combined actions (e.g., EditNewDelete includes Edit, New, Delete)
        var (action, target) = ParseResource(resource);
        foreach (var right in relevantRights.Where(r => !r.IsDenied))
        {
            if (IsCombinedActionMatch(right.Resource, action, target))
            {
                LogAuthorizationDecision(resource, groupNamesList, true, $"combined action match: {right.Resource}");
                return true;
            }
        }

        // 4. Default behavior
        var result = options.Value.DefaultBehavior == DefaultAccessBehavior.AllowAll;
        LogAuthorizationDecision(resource, groupNamesList, result, "default behavior");
        return result;
    }

    private HashSet<Guid> ResolveGroupIds(SecurityConfiguration config, IEnumerable<string> groupNames)
    {
        var result = new HashSet<Guid>();

        foreach (var groupName in groupNames)
        {
            // Find group by name (case-insensitive)
            var matchingGroup = config.Groups
                .FirstOrDefault(g => string.Equals(g.Value, groupName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(matchingGroup.Key) && Guid.TryParse(matchingGroup.Key, out var groupId))
            {
                result.Add(groupId);
            }
        }

        return result;
    }

    private static bool MatchesResource(string rightResource, string requestedResource)
    {
        return string.Equals(rightResource, requestedResource, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Action, string Target) ParseResource(string resource)
    {
        var slashIndex = resource.IndexOf('/');
        if (slashIndex < 0)
        {
            return (resource, string.Empty);
        }

        return (resource[..slashIndex], resource[(slashIndex + 1)..]);
    }

    private static bool IsCombinedActionMatch(string rightResource, string requestedAction, string requestedTarget)
    {
        var (rightAction, rightTarget) = ParseResource(rightResource);

        // Target must match
        if (!string.Equals(rightTarget, requestedTarget, StringComparison.OrdinalIgnoreCase))
            return false;

        // Check if the right's action is a combined action that includes the requested action
        if (CombinedActions.TryGetValue(rightAction, out var includedActions))
        {
            return includedActions.Contains(requestedAction, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private void LogAuthorizationDecision(string resource, IEnumerable<string> groups, bool allowed, string reason)
    {
        var groupsString = string.Join(", ", groups);

        if (allowed)
        {
            logger.LogDebug("Authorization ALLOWED for {Resource} (groups: [{Groups}]): {Reason}",
                resource, groupsString, reason);
        }
        else
        {
            logger.LogWarning("Authorization DENIED for {Resource} (groups: [{Groups}]): {Reason}",
                resource, groupsString, reason);
        }
    }
}
